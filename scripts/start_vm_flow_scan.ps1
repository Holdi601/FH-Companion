<#
.SYNOPSIS
    Scan one open leaderboard with the flow scanner and write the sweep's file shapes.

.DESCRIPTION
    Drop-in alternative to start_vm_memory_leaderboard_scan.ps1 for a board that is
    already open. The difference is the loop: the memory scan waits for a specific
    rank and pays an exact/near/sweep read ladder when it does not appear, which on a
    rotating row pool can never help -- measured 25 ranks/min on a resumed board. The
    flow scanner presses and harvests whatever is resident, never waiting: measured
    1,426 ranks/min at rank 11,400 on the same board, with ~9% of ranks missed.

    Those misses are the trade, and they are handled the way the sweep already
    handles a short board: this reports `truncated` while gaps remain, the sweep
    re-enters the board and runs another pass, and the passes union by rank. A pass
    samples a different subset of the pool, so coverage converges instead of
    repeating the same holes.

.EXAMPLE
    .\scripts\start_vm_flow_scan.ps1 -RunId fs_test -Track "Highway Circuit" -PerformanceClass S1
#>
[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = ".\admin",
    [string] $GuestWorkspace = "C:\ForzaAutomation",
    [Parameter(Mandatory = $true)][string] $RunId,
    [string] $Track = "",
    [string] $PerformanceClass = "",
    [string] $RivalsMode = "",
    [string] $HostOutputRoot = "data/memory_scans/full_sweep",
    # 40 measured best: 10 collapses to 106 ranks/min because the game only refills
    # the row pool once the view has moved far enough, and 10 rows stays inside it.
    [int] $MaxPressPerCycle = 40,
    [int] $ReadRecords = 240,
    [int] $MaxSeconds = 2400,
    [int] $StopAfterDeadCycles = 25,
    [int] $TimeoutMinutes = 60
)

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
function Write-Flow { param([string] $M) Write-Host "[flow-host] $((Get-Date).ToString('HH:mm:ss')) $M" }

$hostRoot = [IO.Path]::GetFullPath((Join-Path $workspace $HostOutputRoot))
$runRoot = Join-Path $hostRoot $RunId
New-Item -ItemType Directory -Force -Path $runRoot | Out-Null

$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$guestRoot = "$GuestWorkspace\data\flow_scan\$stamp"
$guestLog = "$guestRoot\flow.log"
$guestRows = "$guestRoot\rows.jsonl"
$guestSummary = "$guestRoot\summary.json"
$taskName = "ForzaFlowScan_$stamp"

$credential = [pscredential]::new($VMUser, [Security.SecureString]::new())
$session = New-PSSession -VMName $VMName -Credential $credential
$started = Get-Date

try {
    foreach ($name in "forza_burst_scan.ps1", "forza_row_server.py", "forza_fast_scan.py",
                      "forza_scoreboard_scan.py", "forza_scoreboard_layout.py") {
        Copy-Item -ToSession $session -LiteralPath (Join-Path $PSScriptRoot $name) `
            -Destination (Join-Path $GuestWorkspace "scripts") -Force
    }
    Invoke-Command -Session $session -ArgumentList $guestRoot -ScriptBlock {
        param($Root) New-Item -ItemType Directory -Force -Path $Root | Out-Null
    }

    $runner = "$GuestWorkspace\flow_run_$stamp.ps1"
    $body = @"
Start-Transcript -LiteralPath "$guestLog" -Force | Out-Null
try {
    & "$GuestWorkspace\scripts\forza_burst_scan.ps1" ``
        -Output "$guestRows" ``
        -Summary "$guestSummary" ``
        -Strategies "burst" -Flow ``
        -CalibrateSeconds $MaxSeconds ``
        -MaxSeconds $MaxSeconds ``
        -ReadRecords $ReadRecords ``
        -MaxPressPerCycle $MaxPressPerCycle ``
        -FlowStopAfterDeadCycles $StopAfterDeadCycles ``
        -AdaptiveStride
} catch {
    Write-Host "[burst] ERROR `$(`$_.Exception.Message)"
} finally {
    Stop-Transcript | Out-Null
}
"@
    Invoke-Command -Session $session -ArgumentList $runner, $body -ScriptBlock {
        param($Path, $Text) Set-Content -LiteralPath $Path -Value $Text -Encoding UTF8
    }

    Write-Flow "starting $taskName (stride $MaxPressPerCycle, up to $MaxSeconds s)"
    Invoke-Command -Session $session -ArgumentList $taskName, $runner -ScriptBlock {
        param($Name, $Script)
        $action = New-ScheduledTaskAction -Execute "powershell.exe" `
            -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$Script`""
        $principal = New-ScheduledTaskPrincipal -UserId "$env:COMPUTERNAME\admin" `
            -LogonType Interactive -RunLevel Highest
        Register-ScheduledTask -TaskName $Name -Action $action -Principal $principal -Force | Out-Null
        Start-ScheduledTask -TaskName $Name
    }

    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    $seen = 0
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 10
        $state = Invoke-Command -Session $session -ArgumentList $taskName, $guestLog -ScriptBlock {
            param($Name, $Log)
            $t = Get-ScheduledTask -TaskName $Name -ErrorAction SilentlyContinue
            $text = ""
            if (Test-Path $Log) { $text = Get-Content $Log -Raw }
            [pscustomobject]@{ Running = ($t -and $t.State -eq "Running"); Text = $text }
        }
        if ($state.Text) {
            $lines = @($state.Text -split "`r?`n" | Where-Object { $_ -match "^\[burst\]" })
            for ($i = $seen; $i -lt $lines.Count; $i++) { Write-Host ("  " + $lines[$i]) }
            $seen = $lines.Count
        }
        if (-not $state.Running) { break }
    }

    Invoke-Command -Session $session -ArgumentList $taskName -ScriptBlock {
        param($Name) Unregister-ScheduledTask -TaskName $Name -Confirm:$false -ErrorAction SilentlyContinue
    }

    $localRows = Join-Path $runRoot "rows.jsonl"
    $exists = Invoke-Command -Session $session -ArgumentList $guestRows -ScriptBlock { param($P) Test-Path $P }
    if (-not $exists) {
        Write-Flow "the scanner produced no rows"
        exit 3
    }
    Copy-Item -FromSession $session -LiteralPath $guestRows -Destination $localRows -Force
    foreach ($pair in @(@($guestSummary, "flow_summary.json"), @($guestLog, "flow.log"))) {
        $there = Invoke-Command -Session $session -ArgumentList $pair[0] -ScriptBlock { param($P) Test-Path $P }
        if ($there) {
            Copy-Item -FromSession $session -LiteralPath $pair[0] `
                -Destination (Join-Path $runRoot $pair[1]) -Force
        }
    }
} finally {
    Remove-PSSession $session -ErrorAction SilentlyContinue
}

$minutes = [Math]::Round(((Get-Date) - $started).TotalMinutes, 2)
$python = "python"
& $python (Join-Path $PSScriptRoot "convert_flow_rows.py") `
    --rows (Join-Path $runRoot "rows.jsonl") `
    --out $runRoot `
    --run-id $RunId --track $Track --performance-class $PerformanceClass `
    --rivals-mode $RivalsMode --minutes $minutes
if ($LASTEXITCODE -ne 0) {
    Write-Flow "conversion failed with exit $LASTEXITCODE"
    exit $LASTEXITCODE
}
Write-Flow "done in $minutes min: $runRoot"
