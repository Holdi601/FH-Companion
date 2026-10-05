[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = ".\admin"
)

<#
.SYNOPSIS
    Das Vollbild-Werbefenster "Windows 10 support has ended" im Gast schliessen.

.DESCRIPTION
    Am 2026-09-18 stand dieses Fenster nach dem Start der VM vor allem anderen und
    haette jeden Lauf blockiert: der Scanner drueckt Pfeiltasten an das Fenster im
    Vordergrund, und das war nicht Forza.

    ## Warum geschlossen und nicht beantwortet

    Das Fenster hat zwei Knoepfe, "Decline Upgrade" und "Continue", und "Continue"
    sieht ausgewaehlt aus. Blind ENTER zu schicken kann also eine Aktualisierung auf
    Windows 11 ausloesen -- auf einer VM, deren GPU-Partitionierung an den Treibern
    des Wirts haengt und die bereits sechs Wirts-Abstuerze in ihrer Geschichte hat.
    Das ist kein Knopf, den man aufs Geratewohl drueckt.

    WM_CLOSE waehlt keinen der beiden. Es schliesst das Fenster, und die Kampagne
    kommt spaeter wieder -- weshalb zusaetzlich die Zielfassung des Gasts auf
    Windows 10 22H2 festgenagelt wird (siehe die Aufrufstelle).

    ## Warum ueber eine geplante Aufgabe

    PowerShell Direct laeuft in Sitzung 0. Von dort sind die Fenster des angemeldeten
    Benutzers unsichtbar: EnumWindows liefert nichts und GetForegroundWindow gibt 0
    zurueck. Nur eine Aufgabe, die IN der Benutzersitzung laeuft, sieht den Schirm.
    Denselben Weg gehen send_vm_forza_keys.ps1 und drive_vm_forza.ps1.
#>

$ErrorActionPreference = "Stop"

$credential = [pscredential]::new($VMUser, [Security.SecureString]::new())
$session = New-PSSession -VMName $VMName -Credential $credential
$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$taskName = "ForzaDismissNag_$stamp"
$runnerPath = "C:\ForzaAutomation\dismiss_nag_$stamp.ps1"
$logPath = "C:\ForzaAutomation\dismiss_nag_$stamp.log"

try {
    $runner = @"
`$ErrorActionPreference = "Continue"
Start-Transcript -LiteralPath "$logPath" -Force | Out-Null
try {
"@ + @'
Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class NagWin {
  public delegate bool E(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(E e, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  public static List<string> Report = new List<string>();
  public static int CloseCampaign() {
    int closed = 0;
    var targets = new List<IntPtr>();
    EnumWindows((h, l) => {
      if (!IsWindowVisible(h)) return true;
      uint pid = 0; GetWindowThreadProcessId(h, out pid);
      string pname = "";
      try { pname = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch {}
      var cls = new StringBuilder(256); GetClassName(h, cls, 256);
      var txt = new StringBuilder(256); GetWindowText(h, txt, 256);
      Report.Add(pid + "|" + pname + "|" + cls.ToString() + "|" + txt.ToString());
      // NACH DER FENSTERKLASSE, NICHT NACH DEM PROZESS.
      //
      // Am 2026-09-18 gemessen: das Vollbild-Werbefenster gehoert "explorer" --
      // es ist ein Shell_OOBEProxy mit dem Titel "Microsoft account". Nach dem
      // Prozessnamen zu suchen fand darum nichts ("closed=0"), und auf den
      // Prozessnamen "explorer" zu zielen waere das Gegenteil davon: das haette
      // die Taskleiste und den Schreibtisch mitgeschlossen.
      //
      // Die Klasse trifft genau dieses eine Fenster. Shell_TrayWnd,
      // DummyDWMListenerWindow und der Rest der Shell bleiben, wo sie sind.
      if (cls.ToString().Equals("Shell_OOBEProxy", StringComparison.OrdinalIgnoreCase) ||
          pname.Equals("setup", StringComparison.OrdinalIgnoreCase) ||
          pname.Equals("MusNotification", StringComparison.OrdinalIgnoreCase) ||
          pname.Equals("MusNotificationUx", StringComparison.OrdinalIgnoreCase) ||
          pname.Equals("Windows10UpgraderApp", StringComparison.OrdinalIgnoreCase)) {
        targets.Add(h);
      }
      return true;
    }, IntPtr.Zero);
    foreach (var h in targets) { SendMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero); closed++; }
    return closed;
  }
}
"@
$n = [NagWin]::CloseCampaign()
"closed=$n"
"--- visible windows ---"
[NagWin]::Report | ForEach-Object { $_ }
'@ + @"
} finally { Stop-Transcript | Out-Null }
"@

    Invoke-Command -Session $session -ArgumentList $taskName, $runnerPath, $runner, $logPath -ScriptBlock {
        param($TaskName, $RunnerPath, $Runner, $LogPath)
        New-Item -ItemType Directory -Path (Split-Path -Parent $RunnerPath) -Force | Out-Null
        Set-Content -LiteralPath $RunnerPath -Value $Runner -Encoding UTF8

        # DIESELBE BAUART WIE send_vm_forza_keys.ps1, und aus demselben Grund:
        # -LogonType Interactive laesst die Aufgabe IN der angemeldeten Sitzung
        # laufen. Ohne das laeuft sie in Sitzung 0, sieht keinen Schirm, findet
        # kein Fenster und meldet trotzdem Erfolg.
        $action = New-ScheduledTaskAction -Execute "powershell.exe" `
            -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$RunnerPath`""
        $principal = New-ScheduledTaskPrincipal -UserId "$env:COMPUTERNAME\admin" `
            -LogonType Interactive -RunLevel Limited
        $settings = New-ScheduledTaskSettingsSet `
            -ExecutionTimeLimit (New-TimeSpan -Minutes 2) -MultipleInstances IgnoreNew
        Register-ScheduledTask -TaskName $TaskName -Action $action `
            -Principal $principal -Settings $settings -Force | Out-Null
        Start-ScheduledTask -TaskName $TaskName
    }

    Start-Sleep -Seconds 6

    Invoke-Command -Session $session -ArgumentList $taskName, $logPath -ScriptBlock {
        param($TaskName, $LogPath)
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $LogPath) { Get-Content -LiteralPath $LogPath }
        else { "kein Protokoll -- die Aufgabe hat nichts geschrieben" }
    }
} finally {
    if ($session) { Remove-PSSession $session }
}
