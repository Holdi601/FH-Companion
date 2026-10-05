<#
Does FH Companion cost Forza frames? Measure it while you drive.

    powershell -ExecutionPolicy Bypass -File scripts\measure_game_impact.ps1 -Label with-fhc -Seconds 90
    powershell -ExecutionPolicy Bypass -File scripts\measure_game_impact.ps1 -Label overlays-off -Seconds 90
    powershell -ExecutionPolicy Bypass -File scripts\measure_game_impact.ps1 -Label without-fhc -Seconds 90

Drive the same thing each time (the same free-roam loop or the same Rivals route)
and compare the three summaries. Results go to data\runtime\perf\<label>-<time>.*

What is measured
  * Every second: CPU and GPU share of Forza, FH Companion and the desktop
    compositor (dwm), from the Windows performance counters. No extra tool needed.
  * If PresentMon is available (-PresentMon <path to PresentMon.exe>, or found on
    PATH): every frame Forza presents -- frame time, and the PRESENT MODE. "Hardware:
    Independent Flip" means Windows shows the game directly; "Composed: Flip" means
    the compositor combines it with other windows first, which costs about one
    refresh of display latency. That is the number that says whether an overlay adds
    delay. PresentMon needs to run as administrator (or as a member of the
    "Performance Log Users" group); the script says so if it cannot start a trace.

Nothing is changed on the system and nothing leaves the computer.
#>
param(
    [string]$Label = "run",
    [int]$Seconds = 90,
    [string]$PresentMon = "",
    [string]$Game = "forzahorizon6"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "data\runtime\perf"
New-Item -ItemType Directory -Force -Path $out | Out-Null
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$base = Join-Path $out "$Label-$stamp"

$spiel = Get-Process -Name $Game -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $spiel) { Write-Host "Forza ($Game.exe) is not running -- start it and drive, then run this again."; exit 1 }
$fhc = Get-Process -Name "FH Companion", "Forza Grip Haptics" -ErrorAction SilentlyContinue | Select-Object -First 1
$dwm = Get-Process -Name "dwm" -ErrorAction SilentlyContinue | Select-Object -First 1
$pids = @{ forza = $spiel.Id; fhc = $(if ($fhc) { $fhc.Id } else { -1 }); dwm = $(if ($dwm) { $dwm.Id } else { -1 }) }
Write-Host ("Forza pid {0}, FH Companion {1}, dwm {2} -- measuring {3} s as '{4}'" -f $pids.forza, $(if ($fhc) { $fhc.Id } else { "not running" }), $pids.dwm, $Seconds, $Label)

# --- PresentMon (optional)
$pm = $null
if (-not $PresentMon) {
    $cmd = Get-Command PresentMon.exe -ErrorAction SilentlyContinue
    if ($cmd) { $PresentMon = $cmd.Source }
}
if ($PresentMon -and (Test-Path $PresentMon)) {
    $csv = "$base-frames.csv"
    $pmArgs = @("--process_id", $spiel.Id, "--output_file", $csv, "--timed", $Seconds, "--terminate_after_timed", "--no_console_stats", "--stop_existing_session")
    try {
        $pm = Start-Process -FilePath $PresentMon -ArgumentList $pmArgs -PassThru -WindowStyle Hidden
    } catch {
        Write-Host "PresentMon could not start ($($_.Exception.Message)). Run this window as administrator for frame times."
    }
} else {
    Write-Host "PresentMon not found -- measuring CPU/GPU shares only. For frame times and present mode pass -PresentMon <path>."
}

# --- Leistungszaehler je Sekunde
$kerne = [Environment]::ProcessorCount
$zeilen = @()
$vorher = @{}
foreach ($k in $pids.Keys) { $p = Get-Process -Id $pids[$k] -ErrorAction SilentlyContinue; if ($p) { $vorher[$k] = $p.TotalProcessorTime } }
$uhr = [Diagnostics.Stopwatch]::StartNew()
for ($i = 0; $i -lt $Seconds; $i++) {
    Start-Sleep -Seconds 1
    $zeit = $uhr.Elapsed.TotalSeconds
    $gpu = @{ forza = 0.0; fhc = 0.0; dwm = 0.0 }
    try {
        $werte = (Get-Counter '\GPU Engine(*engtype_3D)\Utilization Percentage' -ErrorAction SilentlyContinue).CounterSamples
        foreach ($w in $werte) {
            foreach ($k in $pids.Keys) {
                if ($pids[$k] -gt 0 -and $w.InstanceName -like "pid_$($pids[$k])_*") { $gpu[$k] += $w.CookedValue }
            }
        }
    } catch { }
    $zeile = [ordered]@{ t = [math]::Round($zeit, 1) }
    foreach ($k in @("forza", "fhc", "dwm")) {
        $p = if ($pids[$k] -gt 0) { Get-Process -Id $pids[$k] -ErrorAction SilentlyContinue } else { $null }
        if ($p -and $vorher.ContainsKey($k)) {
            $cpu = ($p.TotalProcessorTime - $vorher[$k]).TotalMilliseconds / 10.0
            $vorher[$k] = $p.TotalProcessorTime
        } else { $cpu = 0 }
        $zeile["${k}_cpu_pct_of_one_core"] = [math]::Round($cpu, 1)
        $zeile["${k}_gpu3d_pct"] = [math]::Round($gpu[$k], 1)
    }
    $zeilen += [pscustomobject]$zeile
}
$zeilen | Export-Csv -NoTypeInformation -Path "$base-usage.csv"

function Mittel($spalte) { [math]::Round((($zeilen | Measure-Object -Property $spalte -Average).Average), 1) }
$bericht = @()
$bericht += "label            : $Label"
$bericht += "seconds          : $Seconds"
$bericht += "forza cpu / gpu  : $(Mittel 'forza_cpu_pct_of_one_core') % of one core / $(Mittel 'forza_gpu3d_pct') % 3D"
$bericht += "fhc   cpu / gpu  : $(Mittel 'fhc_cpu_pct_of_one_core') % of one core / $(Mittel 'fhc_gpu3d_pct') % 3D"
$bericht += "dwm   cpu / gpu  : $(Mittel 'dwm_cpu_pct_of_one_core') % of one core / $(Mittel 'dwm_gpu3d_pct') % 3D"

if ($pm) {
    $pm.WaitForExit(($Seconds + 30) * 1000) | Out-Null
    $csv = "$base-frames.csv"
    if (Test-Path $csv) {
        $frames = Import-Csv $csv
        $spalteZeit = @("MsBetweenPresents", "msBetweenPresents", "FrameTime") | Where-Object { $frames[0].PSObject.Properties.Name -contains $_ } | Select-Object -First 1
        $spalteModus = @("PresentMode") | Where-Object { $frames[0].PSObject.Properties.Name -contains $_ } | Select-Object -First 1
        if ($spalteZeit) {
            $ms = $frames | ForEach-Object { [double]$_.$spalteZeit } | Where-Object { $_ -gt 0 } | Sort-Object
            $n = $ms.Count
            $mittel = ($ms | Measure-Object -Average).Average
            $var = ($ms | ForEach-Object { ($_ - $mittel) * ($_ - $mittel) } | Measure-Object -Average).Average
            $p99 = $ms[[math]::Min($n - 1, [int]($n * 0.99))]
            $bericht += "frames           : $n"
            $bericht += "avg fps          : $([math]::Round(1000 / $mittel, 1))"
            $bericht += "1% low fps       : $([math]::Round(1000 / $p99, 1))"
            $bericht += "frame time sd    : $([math]::Round([math]::Sqrt($var), 2)) ms"
            $bericht += "frames > 2x avg  : $(($ms | Where-Object { $_ -gt 2 * $mittel }).Count)"
        }
        if ($spalteModus) {
            $bericht += "present modes    :"
            $frames | Group-Object -Property $spalteModus | Sort-Object Count -Descending | ForEach-Object {
                $bericht += ("    {0,-32} {1,6:0.0} %" -f $_.Name, (100.0 * $_.Count / $frames.Count))
            }
        }
    } else {
        $bericht += "frames           : PresentMon wrote nothing (not elevated?)"
    }
}
$bericht | Set-Content -Path "$base-summary.txt" -Encoding utf8
$bericht | ForEach-Object { Write-Host $_ }
Write-Host "saved: $base-*"
