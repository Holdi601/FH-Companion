<#
.SYNOPSIS
    Startet den Auswertungs-Webserver, falls er nicht laeuft. Sonst tut es nichts.

.DESCRIPTION
    WARUM ES DAS GIBT: die Seite war am 2026-08-27 neun Stunden lang unerreichbar und
    am 2026-08-30 rund fuenf, beide Male, ohne dass es jemandem auffiel. Der Server
    ist einfach ein Python-Prozess an einer Sitzung; stirbt der Rechner, die Sitzung
    oder raeumt jemand Python-Prozesse weg, ist die Seite weg -- gebaut wird sie
    weiter, nur ausgeliefert nicht mehr.

    Dieses Skript ist absichtlich klein und idempotent: es prueft, ob schon ein
    serve_analytics laeuft, und startet sonst einen. Es ist fuer den Aufgabenplaner
    gedacht (alle fuenf Minuten), taugt aber auch von Hand.

    --host 0.0.0.0 steht hier fest und mit Absicht: die Voreinstellung bindet nur
    127.0.0.1, und dann ist die Seite still nur auf diesem Rechner zu sehen.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/ensure_analytics_server.ps1
#>
[CmdletBinding()]
param(
    [int] $Port = 8787,
    [switch] $Restart
)

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$logPath = Join-Path $workspace "data\runtime\overnight\serve_analytics.out"
New-Item -ItemType Directory -Force -Path (Split-Path $logPath) | Out-Null

function Get-ServerProcesses {
    Get-CimInstance Win32_Process -Filter "Name='python.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and $_.CommandLine -match 'serve_analytics' }
}

$running = @(Get-ServerProcesses)
if ($running.Count -gt 0 -and -not $Restart) {
    Write-Host "[site] laeuft bereits (pid $($running[0].ProcessId))"
    exit 0
}
if ($Restart -and $running.Count -gt 0) {
    Write-Host "[site] beende $($running.Count) laufende(n) Server"
    $running | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 2
}

$python = @("$env:LOCALAPPDATA\Programs\Python\Python313\python.exe",
            "python.exe") |
          Where-Object { (Test-Path $_) -or (Get-Command $_ -ErrorAction SilentlyContinue) } |
          Select-Object -First 1

# Ohne -WindowStyle Hidden blitzt alle fuenf Minuten ein Fenster auf, wenn der Nutzer
# gerade spielt. Ein Neustart des Servers darf nicht im Bild stehen.
Start-Process -FilePath $python `
    -ArgumentList @("$workspace\server\serve_analytics.py", "--host", "0.0.0.0", "--port", "$Port") `
    -WorkingDirectory $workspace `
    -WindowStyle Hidden `
    -RedirectStandardOutput $logPath `
    -RedirectStandardError "$logPath.err"

Start-Sleep -Seconds 3
$now = @(Get-ServerProcesses)
if ($now.Count -gt 0) {
    Write-Host "[site] gestartet (pid $($now[0].ProcessId)) auf 0.0.0.0:$Port"
    exit 0
}
Write-Host "[site] Start fehlgeschlagen -- siehe $logPath"
exit 1
