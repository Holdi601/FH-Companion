<#
.SYNOPSIS
    Run forza_burst_scan.ps1 in the guest console session and bring its numbers back.

.DESCRIPTION
    Same shape as start_forza_paged_scan.ps1 -- the driver has to live in the guest
    console session because only that session's synthetic input reaches the game --
    but this one is a measurement: it drives the open board with each key strategy
    in turn and returns a summary of ranks/min and gaps per strategy.

.EXAMPLE
    .\scripts\start_forza_burst_scan.ps1 -CalibrateSeconds 50 -MaxSeconds 600
#>
[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = ".\admin",
    [string] $GuestWorkspace = "C:\ForzaAutomation",
    [int] $StartRank = 1,
    [string] $Strategies = "down18,down8,burst,pgdn",
    [int] $CalibrateSeconds = 50,
    [int] $Cross = 6,
    [int] $MaxSeconds = 600,
    [int] $MaxRanks = 0,
    [int] $ReadRecords = 240,
    [int] $MaxPressPerCycle = 50,
    [switch] $Flow,
    [int] $HoldMs = 400,
    [switch] $AdaptiveStride,
    [int] $FetchTimeoutMs = 6000,
    [string] $HostOutputRoot = "data/runtime/burst_scan",
    [int] $TimeoutMinutes = 30
)

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
function Write-Host-Burst { param([string] $M) Write-Host "[burst-host] $((Get-Date).ToString('HH:mm:ss')) $M" }

$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$hostRoot = [IO.Path]::GetFullPath((Join-Path $workspace $HostOutputRoot))
New-Item -ItemType Directory -Force -Path $hostRoot | Out-Null
$guestRoot = "$GuestWorkspace\data\burst_scan\$stamp"
$guestLog = "$guestRoot\burst.log"
$guestRows = "$guestRoot\rows.jsonl"
$guestCheck = "$guestRoot\checkpoint.json"
$guestSummary = "$guestRoot\summary.json"
$guestTiming = "$guestRoot\timings.csv"
$taskName = "ForzaBurstScan_$stamp"

$credential = [pscredential]::new($VMUser, [Security.SecureString]::new())
$session = New-PSSession -VMName $VMName -Credential $credential

try {
    Write-Host-Burst "copying the driver and reader into the guest"
    foreach ($name in "forza_burst_scan.ps1", "forza_row_server.py",
                      "forza_fast_scan.py", "forza_scoreboard_scan.py",
                      "forza_scoreboard_layout.py") {
        Copy-Item -ToSession $session -LiteralPath (Join-Path $PSScriptRoot $name) `
            -Destination (Join-Path $GuestWorkspace "scripts") -Force
    }
    Invoke-Command -Session $session -ArgumentList $guestRoot -ScriptBlock {
        param($Root) New-Item -ItemType Directory -Force -Path $Root | Out-Null
    }

    $runner = "$GuestWorkspace\burst_run_$stamp.ps1"
    $body = @"
Start-Transcript -LiteralPath "$guestLog" -Force | Out-Null
try {
    & "$GuestWorkspace\scripts\forza_burst_scan.ps1" ``
        -Output "$guestRows" ``
        -Checkpoint "$guestCheck" ``
        -Summary "$guestSummary" ``
        -TimingCsv "$guestTiming" ``
        -StartRank $StartRank ``
        -Strategies "$Strategies" ``
        -CalibrateSeconds $CalibrateSeconds ``
        -Cross $Cross ``
        -MaxSeconds $MaxSeconds ``
        -MaxRanks $MaxRanks ``
        -ReadRecords $ReadRecords ``
        -MaxPressPerCycle $MaxPressPerCycle ``
        -HoldMs $HoldMs ``
        $(if ($AdaptiveStride) { '-AdaptiveStride' }) ``
        $(if ($Flow) { '-Flow' }) ``
        -FetchTimeoutMs $FetchTimeoutMs
} catch {
    Write-Host "[burst] ERROR `$(`$_.Exception.Message)"
} finally {
    Stop-Transcript | Out-Null
}
"@
    Invoke-Command -Session $session -ArgumentList $runner, $body -ScriptBlock {
        param($Path, $Text) Set-Content -LiteralPath $Path -Value $Text -Encoding UTF8
    }

    Write-Host-Burst "registering interactive guest task $taskName"
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
        Start-Sleep -Seconds 5
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

    foreach ($pair in @(@($guestRows, "rows.jsonl"), @($guestCheck, "checkpoint.json"),
                        @($guestSummary, "summary.json"), @($guestTiming, "timings.csv"),
                        @($guestLog, "burst.log"))) {
        $exists = Invoke-Command -Session $session -ArgumentList $pair[0] -ScriptBlock { param($P) Test-Path $P }
        if ($exists) {
            Copy-Item -FromSession $session -LiteralPath $pair[0] `
                -Destination (Join-Path $hostRoot "${stamp}_$($pair[1])") -Force
        }
    }
    Write-Host-Burst "artifacts: $hostRoot"
} finally {
    Remove-PSSession $session -ErrorAction SilentlyContinue
}
