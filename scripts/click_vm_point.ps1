[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = ".\admin",
    [Parameter(Mandatory = $true)][int] $X,
    [Parameter(Mandatory = $true)][int] $Y,
    [string] $Expect = ""
)

<#
.SYNOPSIS
    Einen Mausklick an eine gemessene Stelle des Gast-Bildschirms schicken.

.DESCRIPTION
    Gedacht fuer Fenster, die sich nicht anders wegbekommen lassen -- etwa die
    Vollbild-Werbung "Windows 10 support has ended", die WM_CLOSE ignoriert und auf
    eine Antwort besteht.

    ## Die Koordinaten kommen aus einer MESSUNG, nicht aus dem Augenmass

    capture_vm_current_screen.ps1 legt neben dem Bild eine current.ocr.json mit dem
    Rahmen jeder gelesenen Zeile. Daraus faellt der Mittelpunkt eines Knopfes auf den
    Punkt genau. Am 2026-09-18 waren das (1571, 814) fuer "Decline Upgrade" und
    (1745, 812) fuer "Continue" -- 174 Bildpunkte auseinander. Wer solche Knoepfe
    nach Augenmass anklickt, trifft irgendwann den falschen, und der falsche startet
    hier eine Aktualisierung auf Windows 11.

    ## -Expect

    Sicherheitsnetz: der Fenstertitel unter dem Mauszeiger muss diesen Text
    enthalten, sonst wird NICHT geklickt. Steht auf dem Schirm inzwischen etwas
    anderes, passiert lieber nichts als das Falsche.

.EXAMPLE
    ./scripts/click_vm_point.ps1 -X 1571 -Y 814 -Expect "Microsoft account"
#>

$ErrorActionPreference = "Stop"

$credential = [pscredential]::new($VMUser, [Security.SecureString]::new())
$session = New-PSSession -VMName $VMName -Credential $credential
$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$taskName = "ForzaClick_$stamp"
$runnerPath = "C:\ForzaAutomation\click_$stamp.ps1"
$logPath = "C:\ForzaAutomation\click_$stamp.log"

try {
    $runner = @"
`$ErrorActionPreference = "Continue"
Start-Transcript -LiteralPath "$logPath" -Force | Out-Null
try {
`$targetX = $X
`$targetY = $Y
`$expect = "$Expect"

"@ + @'
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class Clicker {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint f);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
  public static string Under(int x, int y) {
    POINT p; p.X = x; p.Y = y;
    IntPtr h = GetAncestor(WindowFromPoint(p), 2);   // GA_ROOT
    var t = new StringBuilder(256); GetWindowText(h, t, 256);
    var c = new StringBuilder(256); GetClassName(h, c, 256);
    return c.ToString() + "|" + t.ToString();
  }
  public static void Click(int x, int y) {
    SetCursorPos(x, y);
    System.Threading.Thread.Sleep(120);
    mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);   // LEFTDOWN
    System.Threading.Thread.Sleep(60);
    mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);   // LEFTUP
  }
}
"@
$under = [Clicker]::Under($targetX, $targetY)
"under=($targetX,$targetY) -> $under"
if ($expect -and $under -notlike "*$expect*") {
    "ABGEBROCHEN: erwartet '$expect', gefunden '$under' -- es wurde NICHT geklickt."
} else {
    [Clicker]::Click($targetX, $targetY)
    "geklickt"
}
'@ + @"
} finally { Stop-Transcript | Out-Null }
"@

    Invoke-Command -Session $session -ArgumentList $taskName, $runnerPath, $runner -ScriptBlock {
        param($TaskName, $RunnerPath, $Runner)
        New-Item -ItemType Directory -Path (Split-Path -Parent $RunnerPath) -Force | Out-Null
        Set-Content -LiteralPath $RunnerPath -Value $Runner -Encoding UTF8
        # Interaktive Sitzung -- aus Sitzung 0 gibt es keinen Mauszeiger.
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
        else { "kein Protokoll" }
    }
} finally {
    if ($session) { Remove-PSSession $session }
}
