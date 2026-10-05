[CmdletBinding()]
param(
    [string] $Phase = "",
    [string] $Detail = "",
    # running | idle | done | error -- faerbt die Leiste im Overlay.
    [ValidateSet("running", "idle", "done", "error")]
    [string] $State = "running",
    # Das Overlay starten, falls es noch nicht laeuft.
    [switch] $Show,
    [string] $StatusPath = ""
)

<#
.SYNOPSIS
    Eine Zeile in die Statusdatei schreiben, die run_status_overlay.ps1 anzeigt.

.DESCRIPTION
    Leere -Phase laesst die bisherige Phase stehen, so dass ein Aufrufer, der nur
    den Schritt kennt (der Navigator bei jeder Protokollzeile), die Phase nicht
    ueberschreibt. Geschrieben wird ueber eine Nebendatei und Umbenennen: das
    Overlay liest nie eine halbe Datei.
#>

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
if (-not $StatusPath) { $StatusPath = Join-Path $workspace "data\runtime\run_status.json" }
New-Item -ItemType Directory -Force -Path (Split-Path $StatusPath) | Out-Null

$alt = $null
if ((-not $Phase) -and (Test-Path -LiteralPath $StatusPath)) {
    try { $alt = Get-Content -LiteralPath $StatusPath -Raw | ConvertFrom-Json } catch { $alt = $null }
}
$eintrag = [ordered]@{
    phase   = if ($Phase) { $Phase } elseif ($alt) { [string]$alt.phase } else { "" }
    detail  = $Detail
    state   = $State
    updated = (Get-Date).ToString("o")
}
$tmp = "$StatusPath.tmp"
[IO.File]::WriteAllText($tmp, ($eintrag | ConvertTo-Json -Compress), (New-Object Text.UTF8Encoding $false))
Move-Item -LiteralPath $tmp -Destination $StatusPath -Force

if ($Show) {
    $laeuft = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
        Where-Object { $_.CommandLine -match 'run_status_overlay\.ps1' }).Count -gt 0
    if (-not $laeuft) {
        Start-Process powershell.exe -WindowStyle Hidden -ArgumentList @(
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-STA", "-File",
            (Join-Path $PSScriptRoot "run_status_overlay.ps1"), "-StatusPath", $StatusPath)
    }
}
