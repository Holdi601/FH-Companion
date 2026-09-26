[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = ".\admin",
    [int]    $Width  = 1920,
    [int]    $Height = 1080,
    [switch] $NoFix
)

<#
.SYNOPSIS
    Vor jedem Lauf pruefen, dass der Gast wirklich scanbereit ist -- und das
    Behebbare gleich beheben.

.DESCRIPTION
    ## Warum es das gibt

    Am 2026-09-18 stand nach dem Start der VM eine Vollbild-Werbung
    ("Windows 10 support has ended") vor dem Spiel. Der Scanner drueckt Pfeiltasten
    an das Fenster im Vordergrund und filmt den Bildschirm; beides waere an die
    Werbung gegangen. Ein Lauf haette eine Stunde lang eine Liste gelesen, die
    stillsteht -- und am Ende ein leeres Board gemeldet, nicht einen Fehler.

    Das von Hand wegzuklicken war die falsche Antwort. Was ein Lauf zum Gelingen
    braucht, gehoert in den Lauf, nicht in den Kopf dessen, der ihn startet.

    ## Was geprueft wird

      1. Vollbild-Kampagne     Fenster da? -> wegklicken (siehe unten)
      2. Zielfassung           auf Windows 10 22H2 festgenagelt?
      3. Python                vorhanden, und WO? (Der PATH luegt: dort steht der
                               Store-Platzhalter, der beim Aufruf nur Werbung zeigt.)
      4. Aufloesung            genau 1920x1080 -- sonst sitzen alle Spalten daneben
      5. Forza                 laeuft, und steht NICHT mehr im "Start Game"-Schirm

    ## Warum die Kampagne ueber UI Automation weggeht und nicht ueber Koordinaten

    Sie ignoriert WM_CLOSE und besteht auf einer Antwort. Beim ersten Mal habe ich
    die Knoepfe aus einer OCR-Messung angeklickt -- das ging, ist aber an eine
    Aufloesung, eine Sprache und ein Layout gebunden, und der Knopf daneben startet
    eine Aktualisierung auf Windows 11.

    UI Automation sucht den Knopf ueber seinen NAMEN. Findet es "Decline Upgrade"
    nicht, wird nichts geklickt und der Lauf bricht lieber ab. Es gibt keinen Fall,
    in dem hier geraten wird.

    ## Warum in einer geplanten Aufgabe

    PowerShell Direct laeuft in Sitzung 0 und sieht den Schirm des angemeldeten
    Benutzers nicht: EnumWindows liefert nichts, GetForegroundWindow gibt 0.
    Dieselbe Bauart wie send_vm_forza_keys.ps1.

.EXAMPLE
    ./scripts/vm_preflight.ps1
    ./scripts/vm_preflight.ps1 -NoFix      # nur berichten, nichts aendern
#>

$ErrorActionPreference = "Stop"

function Write-Pre { param([string] $Message) Write-Host "[preflight] $Message" }

$credential = [pscredential]::new($VMUser, [Security.SecureString]::new())
$session = New-PSSession -VMName $VMName -Credential $credential
$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$taskName = "ForzaPreflight_$stamp"
$runnerPath = "C:\ForzaAutomation\preflight_$stamp.ps1"
$logPath = "C:\ForzaAutomation\preflight_$stamp.log"

try {
    # ---------------------------------------------------------------- Sitzung 0
    # Registry und Prozesse gehen von hier; nur der Schirm nicht.

    $osPinned = Invoke-Command -Session $session -ArgumentList $NoFix.IsPresent -ScriptBlock {
        param($NoFix)
        $wu = "HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate"
        $have = (Get-ItemProperty -Path $wu -Name TargetReleaseVersionInfo -ErrorAction SilentlyContinue).TargetReleaseVersionInfo
        if ($have) { return "gepinnt auf $have" }
        if ($NoFix) { return "NICHT gepinnt (nicht geaendert, -NoFix)" }
        # Ohne das kommt die Kampagne nach jedem Neustart wieder. 22H2 ist die
        # Fassung, die dieser Gast hat; hier wird nichts hochgestuft.
        New-Item -Path $wu -Force | Out-Null
        New-ItemProperty -Path $wu -Name TargetReleaseVersion     -Value 1            -PropertyType DWord  -Force | Out-Null
        New-ItemProperty -Path $wu -Name TargetReleaseVersionInfo -Value "22H2"       -PropertyType String -Force | Out-Null
        New-ItemProperty -Path $wu -Name ProductVersion           -Value "Windows 10" -PropertyType String -Force | Out-Null
        return "gepinnt auf 22H2 (neu gesetzt)"
    }
    Write-Pre "Zielfassung: $osPinned"

    $python = Invoke-Command -Session $session -ScriptBlock {
        # DER PATH LUEGT. Dort steht C:\...\WindowsApps\python.exe, ein
        # Platzhalter, der beim Aufruf den Store oeffnet statt Python zu starten.
        # Gesucht wird darum nach einer EXE, die sich auch nach ihrer Fassung
        # fragen laesst.
        $kandidaten = @(
            "C:\ForzaTools\Python312\python.exe",
            "C:\Program Files\Python312\python.exe",
            "C:\Python312\python.exe"
        )
        $kandidaten += (Get-ChildItem "C:\ForzaTools","C:\Program Files" -Filter python.exe `
                        -Recurse -Depth 2 -ErrorAction SilentlyContinue |
                        Select-Object -ExpandProperty FullName)
        foreach ($k in $kandidaten) {
            if (Test-Path -LiteralPath $k) {
                $v = & $k --version 2>&1
                if ($v -match "^Python \d") { return "$k  ($v)" }
            }
        }
        return "NICHT GEFUNDEN"
    }
    Write-Pre "Python: $python"

    $forza = Invoke-Command -Session $session -ScriptBlock {
        $p = Get-Process forzahorizon6 -ErrorAction SilentlyContinue
        if (-not $p) { return "laeuft NICHT" }
        return "laeuft, PID $($p.Id)"
    }
    Write-Pre "Forza: $forza"

    # ---------------------------------------------------------------- Sitzung 1
    # Alles, wofuer man den Schirm sehen muss.

    $runner = @"
`$ErrorActionPreference = "Continue"
Start-Transcript -LiteralPath "$logPath" -Force | Out-Null
try {
`$wantW = $Width
`$wantH = $Height
`$noFix = `$$($NoFix.IsPresent)

"@ + @'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class Pre {
  public delegate bool E(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(E e, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  public static List<IntPtr> Campaign() {
    var found = new List<IntPtr>();
    EnumWindows((h, l) => {
      if (!IsWindowVisible(h)) return true;
      var c = new StringBuilder(256); GetClassName(h, c, 256);
      // Die Vollbild-Kampagne haengt an explorer und traegt diese Klasse. Nach
      // dem Prozessnamen zu suchen faende sie nicht; auf "explorer" zu zielen
      // wuerde Taskleiste und Schreibtisch mitschliessen.
      if (c.ToString() == "Shell_OOBEProxy") found.Add(h);
      return true;
    }, IntPtr.Zero);
    return found;
  }
}
"@

# --- 1. Aufloesung
# Add-Type ZUERST. [System.Windows.Forms.Screen] wird beim Parsen aufgeloest, nicht
# beim Ausfuehren: stand das Add-Type dahinter, starb das ganze Skript mit
# "Unable to find type" -- und zwar lautlos, weil das Protokoll da noch leer war.
Add-Type -AssemblyName System.Windows.Forms
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
if ($b.Width -eq $wantW -and $b.Height -eq $wantH) { "AUFLOESUNG: ok ($($b.Width)x$($b.Height))" }
else { "AUFLOESUNG: FALSCH ($($b.Width)x$($b.Height), erwartet ${wantW}x${wantH})" }

# --- 2. Vollbild-Kampagne
$campaign = [Pre]::Campaign()
if ($campaign.Count -eq 0) {
    "KAMPAGNE: keine"
} elseif ($noFix) {
    "KAMPAGNE: $($campaign.Count) Fenster (nicht angefasst, -NoFix)"
} else {
    # UEBER DEN NAMEN DES KNOPFES, nicht ueber Koordinaten. Der Schirm kann zwei
    # Stufen haben ("Decline Upgrade" -> Bestaetigung mit nochmals "Decline
    # Upgrade"), darum bis zu drei Durchgaenge.
    $geklickt = 0
    for ($runde = 0; $runde -lt 3; $runde++) {
        $root = [System.Windows.Automation.AutomationElement]::RootElement
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, "Decline Upgrade")
        $btn = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
        if (-not $btn) { break }
        try {
            $pattern = $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
            $pattern.Invoke()
            $geklickt++
            Start-Sleep -Milliseconds 1500
        } catch {
            "KAMPAGNE: 'Decline Upgrade' gefunden, liess sich aber nicht druecken: $_"
            break
        }
    }
    $rest = [Pre]::Campaign().Count
    "KAMPAGNE: $geklickt x 'Decline Upgrade' gedrueckt, $rest Fenster uebrig"
}

# --- 3. Was steht im Vordergrund
$vorne = @()
[Pre]::Campaign() | ForEach-Object { $vorne += "Shell_OOBEProxy" }
"RESTKAMPAGNE: " + $vorne.Count
'@ + @"
} finally { Stop-Transcript | Out-Null }
"@

    Invoke-Command -Session $session -ArgumentList $taskName, $runnerPath, $runner -ScriptBlock {
        param($TaskName, $RunnerPath, $Runner)
        New-Item -ItemType Directory -Path (Split-Path -Parent $RunnerPath) -Force | Out-Null
        Set-Content -LiteralPath $RunnerPath -Value $Runner -Encoding UTF8
        $action = New-ScheduledTaskAction -Execute "powershell.exe" `
            -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$RunnerPath`""
        $principal = New-ScheduledTaskPrincipal -UserId "$env:COMPUTERNAME\admin" `
            -LogonType Interactive -RunLevel Limited
        $settings = New-ScheduledTaskSettingsSet `
            -ExecutionTimeLimit (New-TimeSpan -Minutes 3) -MultipleInstances IgnoreNew
        Register-ScheduledTask -TaskName $TaskName -Action $action `
            -Principal $principal -Settings $settings -Force | Out-Null
        Start-ScheduledTask -TaskName $TaskName
    }

    Start-Sleep -Seconds 12

    $screenReport = Invoke-Command -Session $session -ArgumentList $taskName, $logPath -ScriptBlock {
        param($TaskName, $LogPath)
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $LogPath) {
            Get-Content -LiteralPath $LogPath | Where-Object {
                $_ -match "^(AUFLOESUNG|KAMPAGNE|RESTKAMPAGNE):"
            }
        } else { "kein Protokoll -- die Aufgabe hat nichts geschrieben" }
    }
    $screenReport | ForEach-Object { Write-Pre $_ }

    # STILLE IST KEIN ERFOLG. Beim ersten Bau lieferte der Schirm-Teil gar nichts
    # (ein Typfehler beim Parsen), und weil nichts zu bemaengeln war, meldete das
    # Ganze "bereit". Ein Preflight, der bei kaputtem Preflight gruen zeigt, ist
    # schlimmer als keiner.
    if (-not ($screenReport | Where-Object { $_ -match "^AUFLOESUNG:" })) {
        $probleme = @("der Schirm-Teil hat nichts gemeldet -- Preflight selbst kaputt")
    }

    # ---------------------------------------------------------------- Urteil
    if ($null -eq $probleme) { $probleme = @() }
    if ($python -eq "NICHT GEFUNDEN") { $probleme += "kein Python im Gast" }
    if ($forza -eq "laeuft NICHT")    { $probleme += "Forza laeuft nicht" }
    foreach ($z in $screenReport) {
        if ($z -match "^AUFLOESUNG: FALSCH") { $probleme += $z }
        if ($z -match "^RESTKAMPAGNE: [1-9]") { $probleme += "Vollbild-Kampagne steht noch" }
    }

    if ($probleme.Count -gt 0) {
        Write-Pre "NICHT BEREIT:"
        $probleme | ForEach-Object { Write-Pre "  - $_" }
        exit 1
    }
    Write-Pre "bereit"
    exit 0
} finally {
    if ($session) { Remove-PSSession $session }
}
