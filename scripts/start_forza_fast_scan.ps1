<#
.SYNOPSIS
    Run forza_fast_scan.py inside the VM as one interactive task and follow it.

.DESCRIPTION
    The pager has to live in the guest's console session: it sends synthetic key
    input, and input only reaches the game from the session the game renders in --
    a PowerShell Direct session lands elsewhere and its keys go nowhere.

    Deliberately thin. The point of Phase 1 is that ONE guest process owns the
    whole board, so this host script copies it in, starts it, tails its log, and
    brings the rows back. It does not drive the loop.

.EXAMPLE
    .\scripts\start_forza_fast_scan.ps1
    .\scripts\start_forza_fast_scan.ps1 -StartRank 5001 -KeyDelayMs 4
#>
[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = ".\admin",
    [string] $GuestWorkspace = "C:\ForzaAutomation",
    [int] $StartRank = 1,
    [int] $PageSize = 50,
    [double] $PollMs = 8,
    [double] $KeyDelayMs = 8,
    [int] $KeysPerBatch = 1,
    [double] $PageDeadlineSeconds = 25,
    [int] $MaxPages = 100000,
    [int] $ReadRecords = 80,
    # Kept in the guest so layout discovery is paid once per game build, not once
    # per run -- it costs ~207 s of full-heap sweep.
    [string] $ProfilePath = "C:\ForzaAutomation\build_profile.json",
    [double] $MaxTimeFactor = 0,
    [string] $HostOutputRoot = "data/runtime/fast_scan",
    [int] $TimeoutMinutes = 240
)

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
function Write-Fast { param([string] $Message) Write-Host "[fast-host] $((Get-Date).ToString('HH:mm:ss')) $Message" }

$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$hostRoot = [IO.Path]::GetFullPath((Join-Path $workspace $HostOutputRoot))
New-Item -ItemType Directory -Force -Path $hostRoot | Out-Null
$guestRoot = "$GuestWorkspace\data\fast_scan\$stamp"
$guestLog = "$guestRoot\fast_scan.log"
$guestRows = "$guestRoot\rows.jsonl"
$guestCheck = "$guestRoot\checkpoint.json"
$taskName = "ForzaFastScan_$stamp"

$credential = [pscredential]::new($VMUser, [Security.SecureString]::new())
$session = New-PSSession -VMName $VMName -Credential $credential

try {
    Write-Fast "copying the pager and its dependencies into the guest"
    foreach ($name in "forza_fast_scan.py", "forza_scoreboard_scan.py", "forza_scoreboard_layout.py") {
        Copy-Item -ToSession $session -LiteralPath (Join-Path $PSScriptRoot $name) `
            -Destination (Join-Path $GuestWorkspace "scripts") -Force
    }
    Invoke-Command -Session $session -ArgumentList $guestRoot -ScriptBlock {
        param($Root) New-Item -ItemType Directory -Force -Path $Root | Out-Null
    }

    $runner = "$GuestWorkspace\fast_scan_$stamp.ps1"
    $body = @"
`$ErrorActionPreference = "Continue"
Start-Transcript -LiteralPath "$guestLog" -Force | Out-Null
try {
    Set-Location "$GuestWorkspace\scripts"
    & "C:\ForzaTools\Python312\python.exe" forza_fast_scan.py ``
        --start-rank $StartRank ``
        --poll-ms $PollMs ``
        --key-delay-ms $KeyDelayMs ``
        --keys-per-batch $KeysPerBatch ``
        --page-deadline-seconds $PageDeadlineSeconds ``
        --max-pages $MaxPages ``
        --max-time-factor $MaxTimeFactor ``
        --read-records $ReadRecords ``
        --profile "$ProfilePath" ``
        --output "$guestRows" ``
        --checkpoint "$guestCheck"
} finally {
    Stop-Transcript | Out-Null
}
"@
    Invoke-Command -Session $session -ArgumentList $runner, $body -ScriptBlock {
        param($Path, $Text) Set-Content -LiteralPath $Path -Value $Text -Encoding UTF8
    }

    Write-Fast "registering interactive guest task $taskName"
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
            $task = Get-ScheduledTask -TaskName $Name -ErrorAction SilentlyContinue
            $text = if (Test-Path $Log) { Get-Content $Log -Raw } else { "" }
            [pscustomobject]@{ Running = ($task -and $task.State -eq "Running"); Text = $text }
        }
        if ($state.Text) {
            $lines = @($state.Text -split "`r?`n" | Where-Object { $_ -match "^\[fast-scan\]" })
            for ($i = $seen; $i -lt $lines.Count; $i++) { Write-Host ("  " + $lines[$i]) }
            $seen = $lines.Count
        }
        if (-not $state.Running) { break }
    }

    Invoke-Command -Session $session -ArgumentList $taskName -ScriptBlock {
        param($Name) Unregister-ScheduledTask -TaskName $Name -Confirm:$false -ErrorAction SilentlyContinue
    }

    foreach ($pair in @(@($guestRows, "rows.jsonl"), @($guestCheck, "checkpoint.json"), @($guestLog, "fast_scan.log"))) {
        $exists = Invoke-Command -Session $session -ArgumentList $pair[0] -ScriptBlock { param($P) Test-Path $P }
        if ($exists) {
            Copy-Item -FromSession $session -LiteralPath $pair[0] `
                -Destination (Join-Path $hostRoot "${stamp}_$($pair[1])") -Force
        }
    }
    Write-Fast "artifacts: $hostRoot"
} finally {
    Remove-PSSession $session -ErrorAction SilentlyContinue
}
