<#
.SYNOPSIS
    Haelt den Auswertungs-Webserver dauerhaft am Leben. Fuer den Aufgabenplaner.

.DESCRIPTION
    WARUM: die Seite ist zweimal still gestorben und tagelang niemandem aufgefallen --
    am 2026-08-27 mit einem harten Neustart (neun Stunden weg) und am 2026-08-30 um
    12:39, als beim Aufraeumen des Sweeps Python-Prozesse weggeraeumt wurden (fuenf
    Stunden weg). Gebaut wurde die Seite in beiden Faellen weiter, nur ausgeliefert
    nicht mehr. Genau das ist der Fehler, den ein Mensch nicht bemerken kann.

    Dieses Skript schaut alle $IntervalSeconds Sekunden nach und startet den Server
    neu, wenn er fehlt. Es ist eine SCHLEIFE und kein Fuenf-Minuten-Takt im Planer,
    weil ein Takt alle fuenf Minuten ein Konsolenfenster aufblitzen liesse -- auf
    einem Rechner, auf dem gespielt wird, ist das nicht hinnehmbar.

    Anhalten:  schtasks /end /tn "Forza Analytics Site"
    Abmelden:  scripts\install_site_task.cmd /entfernen

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/keep_site_up.ps1
#>
[CmdletBinding()]
param(
    [int] $IntervalSeconds = 300,
    [int] $Port = 8787
)

$ErrorActionPreference = "Continue"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$ensure = Join-Path $PSScriptRoot "ensure_analytics_server.ps1"
$log = Join-Path $workspace "data\runtime\site_keeper.log"
New-Item -ItemType Directory -Force -Path (Split-Path $log) | Out-Null

function Note { param([string] $Message)
    Add-Content -LiteralPath $log -Value ("{0} {1}" -f (Get-Date -Format "MM-dd HH:mm:ss"), $Message)
}

Note "=== Aufseher fuer die Seite an (alle $IntervalSeconds s, Port $Port) ==="
while ($true) {
    try {
        $running = @(Get-CimInstance Win32_Process -Filter "Name='python.exe'" -ErrorAction SilentlyContinue |
                     Where-Object { $_.CommandLine -and $_.CommandLine -match 'serve_analytics' })
        if ($running.Count -eq 0) {
            Note "Server war weg -- starte neu"
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $ensure -Port $Port |
                ForEach-Object { Note "  $_" }
        }
    } catch {
        Note "Fehler beim Nachsehen: $($_.Exception.Message)"
    }
    Start-Sleep -Seconds $IntervalSeconds
}
