[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = ".\admin",
    [string] $Track = "Highway Circuit",
    [Alias("PerformanceClass")]
    [string] $PiClass = "C",
    [string] $RivalsMode = "Road Racing",
    [string] $EventType = "Rivals",
    [string] $TotalRanks = "Auto",
    [int] $StartRank = 1,
    [int] $CurrentRank = 1,
    [int] $ChunkSize = 550,
    [int] $RowsPerScreenshot = 11,
    [int] $RowDelayMs = 0,
    [int] $RowSettleMs = 120,
    [int] $NavigationDelayMs = 10,
    [int] $NavigationBurstSize = 20,
    [int] $NavigationSettleMs = 250,
    [double] $NavigationCheckSeconds = 7.0,
    [int] $NavigationSpamBurstSize = 1,
    [int] $NavigationSpamPauseMs = 2,
    [int] $NavigationFeedbackThreshold = 500,
    [int] $NavigationMaxChecks = 1000,
    [int] $ScrollbarJumpThreshold = 5000,
    [string] $RunId = "",
    [string] $GuestWorkspace = "C:\ForzaAutomation",
    [string] $GuestCaptureRoot = "C:\ForzaCaptures",
    [string] $HostCaptureRoot = "data/vm_share/rank_scans",
    [string] $HostProcessedRoot = "data/processed/vm_rank_scans",
    [ValidateSet("windows-batch", "windows-single", "rapidocr-cpu", "rapidocr-gpu")]
    [string] $OcrBackend = "rapidocr-gpu",
    [string] $ExtractParallelJobs = "Auto",
    [string] $OcrThreadsPerProcess = "Auto",
    [switch] $AlreadyAtLeaderboard,
    [switch] $NoCopy,
    [switch] $NoHostSync,
    [switch] $NoHostExtractor,
    [switch] $NoStartForza,
    [switch] $UseScrollbarJump,
    [switch] $NoScrollbarJump,
    [switch] $Detach,
    [int] $ProgressPollSeconds = 2
)

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path

function Write-ScanStart {
    param([Parameter(Mandatory = $true)][string] $Message)
    Write-Host "[vm-rank-scan] $Message"
}

function Resolve-WorkspacePath {
    param([Parameter(Mandatory = $true)][string] $Path)
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }
    return [System.IO.Path]::GetFullPath((Join-Path $workspace $Path))
}

function New-BlankCredential {
    param([Parameter(Mandatory = $true)][string] $User)
    if ($User -notmatch '^[^\\]+\\' -and $User -notmatch '@') {
        $User = ".\$User"
    }
    return [pscredential]::new($User, [Security.SecureString]::new())
}

function Wait-PowerShellDirect {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)] $Credential
    )

    for ($i = 0; $i -lt 90; $i += 1) {
        try {
            Invoke-Command -VMName $Name -Credential $Credential -ScriptBlock { $env:COMPUTERNAME } -ErrorAction Stop | Out-Null
            return
        } catch {
            Start-Sleep -Seconds 2
        }
    }
    throw "PowerShell Direct did not become ready for $Name."
}

function ConvertTo-SafeName {
    param([Parameter(Mandatory = $true)][string] $Value)

    $safe = $Value.ToLowerInvariant() -replace "[^a-z0-9]+", "_"
    $safe = $safe.Trim("_")
    if ([string]::IsNullOrWhiteSpace($safe)) {
        return "value"
    }
    return $safe
}

function ConvertTo-PowerShellLiteral {
    param([AllowNull()][string] $Value)
    if ($null -eq $Value) {
        return "''"
    }
    return "'" + ($Value -replace "'", "''") + "'"
}

function Get-HostScanProgress {
    param(
        [Parameter(Mandatory = $true)][string] $RunRoot,
        [Parameter(Mandatory = $true)][string] $ProcessedRoot
    )

    $chunkRoot = Join-Path $RunRoot "chunks"
    $processedChunkRoot = Join-Path $ProcessedRoot "chunks"
    $screenshots = 0
    $syncedChunks = 0
    $processedChunks = 0
    if (Test-Path -LiteralPath $chunkRoot) {
        $screenshots = @(Get-ChildItem -LiteralPath $chunkRoot -Recurse -File -Filter "leaderboard*.png" -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '[\\/]_end_duplicates[\\/]' }).Count
        $syncedChunks = @(Get-ChildItem -LiteralPath $chunkRoot -Directory -ErrorAction SilentlyContinue).Count
    }
    if (Test-Path -LiteralPath $processedChunkRoot) {
        $processedChunks = @(Get-ChildItem -LiteralPath $processedChunkRoot -Recurse -File -Filter ".extract_complete.json" -ErrorAction SilentlyContinue).Count
    }
    return [pscustomobject]@{
        screenshots = $screenshots
        synced_chunks = $syncedChunks
        processed_chunks = $processedChunks
    }
}

function Get-GuestScanProgress {
    param(
        [Parameter(Mandatory = $true)] $Session,
        [Parameter(Mandatory = $true)][string] $RunRoot,
        [Parameter(Mandatory = $true)][string] $TaskName
    )

    Invoke-Command -Session $Session -ArgumentList $RunRoot, $TaskName -ScriptBlock {
        param([string] $RunRoot, [string] $TaskName)

        $statePath = Join-Path $RunRoot "state.json"
        $state = $null
        if (Test-Path -LiteralPath $statePath) {
            try {
                $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
            } catch {
                $state = $null
            }
        }

        $chunkRoot = Join-Path $RunRoot "chunks"
        $screenshots = 0
        $chunks = 0
        if (Test-Path -LiteralPath $chunkRoot) {
            $screenshots = @(Get-ChildItem -LiteralPath $chunkRoot -Recurse -File -Filter "leaderboard*.png" -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -notmatch '[\\/]_end_duplicates[\\/]' }).Count
            $chunks = @(Get-ChildItem -LiteralPath $chunkRoot -Directory -ErrorAction SilentlyContinue).Count
        }

        $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        $taskInfo = $null
        if ($task) {
            $taskInfo = Get-ScheduledTaskInfo -TaskName $TaskName -ErrorAction SilentlyContinue
        }

        $lastLog = Get-ChildItem -LiteralPath (Join-Path $RunRoot "logs") -File -Filter "*.log" -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
        $tail = @()
        if ($lastLog) {
            $tail = @(Get-Content -LiteralPath $lastLog.FullName -Tail 12 -ErrorAction SilentlyContinue)
        }

        [pscustomobject]@{
            state_exists = [bool]$state
            status = if ($state -and $state.PSObject.Properties.Name -contains "status") { [string]$state.status } else { "" }
            next_rank = if ($state -and ($state.next_rank -as [int]) -gt 0) { [int]$state.next_rank } else { 0 }
            max_scanned_rank = if ($state -and ($state.max_scanned_rank -as [int]) -ge 0) { [int]$state.max_scanned_rank } else { 0 }
            total_ranks = if ($state -and ($state.total_ranks -as [int]) -gt 0) { [int]$state.total_ranks } else { 0 }
            total_ranks_unknown = if ($state -and ($state.PSObject.Properties.Name -contains "total_ranks_unknown")) { [bool]$state.total_ranks_unknown } else { $false }
            total_ranks_mode = if ($state -and ($state.PSObject.Properties.Name -contains "total_ranks_mode")) { [string]$state.total_ranks_mode } else { "" }
            start_rank = if ($state -and ($state.start_rank -as [int]) -gt 0) { [int]$state.start_rank } else { 1 }
            rows_per_screenshot = if ($state -and ($state.rows_per_screenshot -as [int]) -gt 0) { [int]$state.rows_per_screenshot } else { 11 }
            capture_baseline_screenshots = if ($state -and ($state.capture_baseline_screenshots -as [int]) -ge 0) { [int]$state.capture_baseline_screenshots } else { 0 }
            navigation_target_rank = if ($state -and ($state.navigation_target_rank -as [int]) -gt 0) { [int]$state.navigation_target_rank } else { 0 }
            navigation_verified_rank = if ($state -and ($state.navigation_verified_rank -as [int]) -gt 0) { [int]$state.navigation_verified_rank } else { 0 }
            navigation_ranks_per_second = if ($state -and ($state.navigation_ranks_per_second -as [double]) -gt 0) { [double]$state.navigation_ranks_per_second } else { 0.0 }
            current_selected_rank = if ($state -and ($state.current_selected_rank -as [int]) -gt 0) { [int]$state.current_selected_rank } else { 0 }
            elapsed_seconds = if ($state -and ($state.elapsed_seconds -as [double]) -gt 0) { [double]$state.elapsed_seconds } else { 0.0 }
            guest_screenshots = $screenshots
            guest_chunks = $chunks
            task_state = if ($task) { [string]$task.State } else { "Missing" }
            task_result = if ($taskInfo) { [int]$taskInfo.LastTaskResult } else { $null }
            task_last_run = if ($taskInfo) { $taskInfo.LastRunTime } else { $null }
            log = if ($lastLog) { [string]$lastLog.FullName } else { "" }
            tail = $tail
        }
    }
}

function Format-ProgressDuration {
    param([double] $Seconds)

    if ($Seconds -lt 0 -or [double]::IsNaN($Seconds) -or [double]::IsInfinity($Seconds)) {
        return "unknown"
    }
    $span = [TimeSpan]::FromSeconds([Math]::Round($Seconds))
    if ($span.TotalDays -ge 1) {
        return "{0}d {1}h" -f [int]$span.TotalDays, $span.Hours
    }
    if ($span.TotalHours -ge 1) {
        return "{0}h {1}m" -f [int]$span.TotalHours, $span.Minutes
    }
    if ($span.TotalMinutes -ge 1) {
        return "{0}m {1}s" -f [int]$span.TotalMinutes, $span.Seconds
    }
    return "{0}s" -f $span.Seconds
}

function Watch-ScanProgress {
    param(
        [Parameter(Mandatory = $true)] $Session,
        [Parameter(Mandatory = $true)][string] $RunRoot,
        [Parameter(Mandatory = $true)][string] $TaskName,
        [Parameter(Mandatory = $true)][string] $HostRunRoot,
        [Parameter(Mandatory = $true)][string] $HostProcessedRunRoot,
        [Parameter(Mandatory = $true)][int] $PollSeconds
    )

    Write-ScanStart "following VM scan progress; press Ctrl+C to detach"
    $lastLine = ""
    $speedSamples = [System.Collections.Generic.List[object]]::new()
    while ($true) {
        Start-Sleep -Seconds $PollSeconds
        $guest = Get-GuestScanProgress -Session $Session -RunRoot $RunRoot -TaskName $TaskName
        $hostProgress = Get-HostScanProgress -RunRoot $HostRunRoot -ProcessedRoot $HostProcessedRunRoot

        $capturedScreenshots = [Math]::Max(0, [int]$guest.guest_screenshots - [int]$guest.capture_baseline_screenshots)
        $estimatedRank = ([int]$guest.start_rank - 1) + ($capturedScreenshots * [int]$guest.rows_per_screenshot)
        $displayRank = [Math]::Max([int]$guest.max_scanned_rank, $estimatedRank)
        if ([int]$guest.total_ranks -gt 0) {
            $displayRank = [Math]::Min($displayRank, [int]$guest.total_ranks)
        }

        $now = [DateTime]::UtcNow
        $speedSamples.Add([pscustomobject]@{ timestamp = $now; rank = $displayRank })
        while ($speedSamples.Count -gt 2 -and ($now - $speedSamples[0].timestamp).TotalSeconds -gt 60) {
            $speedSamples.RemoveAt(0)
        }
        $ranksPerMinute = 0.0
        if ($speedSamples.Count -ge 2) {
            $firstSample = $speedSamples[0]
            $elapsed = ($now - $firstSample.timestamp).TotalSeconds
            $rankDelta = $displayRank - [int]$firstSample.rank
            if ($elapsed -gt 0 -and $rankDelta -gt 0) {
                $ranksPerMinute = $rankDelta / ($elapsed / 60.0)
            }
        }

        if ([string]$guest.status -eq "navigating") {
            $navigationSpeed = if ([double]$guest.navigation_ranks_per_second -gt 0) { " | {0:N0} ranks/s" -f [double]$guest.navigation_ranks_per_second } else { "" }
            $line = "navigating verified rank $($guest.navigation_verified_rank) -> $($guest.navigation_target_rank)$navigationSpeed | capture not started | task $($guest.task_state)/$($guest.task_result)"
            if ($line -ne $lastLine) {
                Write-ScanStart $line
                $lastLine = $line
            }
            if ([string]$guest.task_state -ne "Running") {
                if ($null -ne $guest.task_result -and [int]$guest.task_result -ne 0) {
                    Write-ScanStart "guest task failed during navigation; last transcript lines:"
                    foreach ($entry in @($guest.tail)) {
                        Write-Host $entry
                    }
                    throw "VM rank scan failed with task result $($guest.task_result). See guest log: $($guest.log)"
                }
                Write-ScanStart "guest task completed"
                break
            }
            continue
        }

        $totalText = if ([int]$guest.total_ranks -gt 0) { [string]$guest.total_ranks } elseif ([bool]$guest.total_ranks_unknown -or [string]$guest.total_ranks_mode -match "until_end") { "? until-end" } else { "?" }
        $percentText = ""
        if ([int]$guest.total_ranks -gt 0) {
            $percent = [Math]::Min(100.0, ([double]$displayRank / [double]$guest.total_ranks) * 100.0)
            $percentText = " ({0:N1}%)" -f $percent
        }
        $speedText = if ($ranksPerMinute -gt 0) { "{0:N0} ranks/min" -f $ranksPerMinute } else { "speed warming up" }
        $etaText = "unknown"
        if ($ranksPerMinute -gt 0 -and [int]$guest.total_ranks -gt 0) {
            $remainingRanks = [Math]::Max(0, [int]$guest.total_ranks - $displayRank)
            $etaText = Format-ProgressDuration (($remainingRanks / $ranksPerMinute) * 60.0)
        } elseif ([bool]$guest.total_ranks_unknown -or [string]$guest.total_ranks_mode -match "until_end") {
            $etaText = "until end detection"
        }
        $line = "rank $displayRank/$totalText$percentText | $speedText | ETA $etaText | screenshots guest/synced $($guest.guest_screenshots)/$($hostProgress.screenshots) | processed chunks $($hostProgress.processed_chunks) | task $($guest.task_state)/$($guest.task_result)"
        if ($line -ne $lastLine) {
            Write-ScanStart $line
            $lastLine = $line
        }

        if ([string]$guest.task_state -ne "Running") {
            if ($null -ne $guest.task_result -and [int]$guest.task_result -ne 0) {
                Write-ScanStart "guest task failed; last transcript lines:"
                foreach ($entry in @($guest.tail)) {
                    Write-Host $entry
                }
                throw "VM rank scan failed with task result $($guest.task_result). See guest log: $($guest.log)"
            }
            Write-ScanStart "guest task completed"
            break
        }
    }
}

if ([string]::IsNullOrWhiteSpace($Track)) {
    throw "-Track is required."
}
if ($StartRank -lt 1 -or $CurrentRank -lt 1) {
    throw "-StartRank and -CurrentRank must be >= 1."
}
if ($RowsPerScreenshot -lt 1 -or $ChunkSize -lt 1) {
    throw "-RowsPerScreenshot and -ChunkSize must be >= 1."
}
if ($RowDelayMs -lt 0 -or $RowSettleMs -lt 0 -or $NavigationDelayMs -lt 0 -or $NavigationSettleMs -lt 0 -or $NavigationSpamPauseMs -lt 0) {
    throw "Input delays and settle times must be >= 0."
}
if ($NavigationBurstSize -lt 1 -or $NavigationSpamBurstSize -lt 1 -or $NavigationFeedbackThreshold -lt 1 -or $NavigationMaxChecks -lt 1) {
    throw "Navigation burst sizes, feedback threshold, and max checks must be >= 1."
}
if ($NavigationCheckSeconds -le 0) {
    throw "-NavigationCheckSeconds must be > 0."
}
if ($ScrollbarJumpThreshold -lt 1) {
    throw "-ScrollbarJumpThreshold must be >= 1."
}
if ([string]::IsNullOrWhiteSpace($RunId)) {
    $RunId = "{0}_{1}_{2}_{3}" -f (Get-Date -Format "yyyyMMdd_HHmmss_fff"), (ConvertTo-SafeName $Track), (ConvertTo-SafeName $PiClass), ([guid]::NewGuid().ToString("N").Substring(0, 8))
}

$hostRunRoot = Resolve-WorkspacePath (Join-Path $HostCaptureRoot $RunId)
$hostLogRoot = Resolve-WorkspacePath (Join-Path "data/background/vm_rank_scan" $RunId)
$hostProcessedRunRoot = Resolve-WorkspacePath (Join-Path $HostProcessedRoot $RunId)
$hostStatePath = Join-Path $hostRunRoot "state.json"
$guestRunRoot = Join-Path (Join-Path $GuestCaptureRoot "rank_scans") $RunId
$guestStatePath = Join-Path $guestRunRoot "state.json"
$guestRunnerPath = Join-Path $GuestWorkspace ("run_rank_scan_{0}.ps1" -f $RunId)
$taskName = "ForzaRankScan_{0}" -f ($RunId -replace "[^A-Za-z0-9_]", "_")
if ($taskName.Length -gt 230) {
    $taskName = $taskName.Substring(0, 230)
}

if (-not $NoStartForza) {
    $readyScript = Join-Path $PSScriptRoot "start_forza_vm_ready.ps1"
    $readyArgs = @(
        "-VMName", $VMName,
        "-VMUser", $VMUser,
        "-NoCleanDisplaySession"
    )
    if ($AlreadyAtLeaderboard) {
        $readyArgs += "-NoLaunch"
    }
    if ($NoCopy) {
        $readyArgs += "-NoCopy"
    }
    Write-ScanStart "ensure VM/Forza is running"
    & powershell -NoProfile -ExecutionPolicy Bypass -File $readyScript @readyArgs | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) {
        throw "start_forza_vm_ready.ps1 failed with exit code $LASTEXITCODE"
    }
}

$credential = New-BlankCredential -User $VMUser
$vm = Get-VM -Name $VMName -ErrorAction Stop
if ($vm.State -ne "Running") {
    Write-ScanStart "start VM $VMName"
    Start-VM -Name $VMName | Out-Null
}

Write-ScanStart "wait for PowerShell Direct"
Wait-PowerShellDirect -Name $VMName -Credential $credential

if ($AlreadyAtLeaderboard) {
    $forzaRunning = Invoke-Command -VMName $VMName -Credential $credential -ScriptBlock {
        [bool](Get-Process -Name forzahorizon6 -ErrorAction SilentlyContinue)
    } -ErrorAction Stop
    if (-not $forzaRunning) {
        throw "-AlreadyAtLeaderboard requires Forza to already be running in the VM and sitting on the visible Change Rival/Rivals leaderboard. Start Forza first or omit -AlreadyAtLeaderboard so the learned route can navigate there. If this track/class has no personal Rivals time yet, drive/post a time first."
    }
}

New-Item -ItemType Directory -Force -Path $hostRunRoot, (Join-Path $hostRunRoot "chunks"), $hostLogRoot, $hostProcessedRunRoot | Out-Null

$session = $null
$syncProcess = $null
$extractProcess = $null
$watchStarted = $false
$watchCompleted = $false
try {
    $session = New-PSSession -VMName $VMName -Credential $credential

    Write-ScanStart "prepare guest workspace/run directories"
    Invoke-Command -Session $session -ArgumentList $GuestWorkspace, $GuestCaptureRoot, $guestRunRoot -ScriptBlock {
        param([string] $Workspace, [string] $CaptureRoot, [string] $RunRoot)
        New-Item -ItemType Directory -Force -Path $Workspace, $CaptureRoot, $RunRoot, (Join-Path $RunRoot "logs"), (Join-Path $RunRoot "chunks") | Out-Null
    }

    if (-not $NoCopy) {
        Write-ScanStart "copy scripts/config to guest"
        foreach ($name in @("scripts", "config")) {
            Copy-Item -ToSession $session -LiteralPath (Join-Path $workspace $name) -Destination $GuestWorkspace -Recurse -Force
        }
    }

    $alreadyAtLeaderboardLiteral = if ($AlreadyAtLeaderboard) { '$true' } else { '$false' }
    $useScrollbarJumpLiteral = if ($UseScrollbarJump) { '$true' } else { '$false' }
    $noScrollbarJumpLiteral = if ($NoScrollbarJump) { '$true' } else { '$false' }
    $trackLiteral = ConvertTo-PowerShellLiteral $Track
    $classLiteral = ConvertTo-PowerShellLiteral $PiClass
    $modeLiteral = ConvertTo-PowerShellLiteral $RivalsMode
    $eventLiteral = ConvertTo-PowerShellLiteral $EventType
    $totalRanksLiteral = ConvertTo-PowerShellLiteral $TotalRanks
    $runIdLiteral = ConvertTo-PowerShellLiteral $RunId
    $guestCaptureRootLiteral = ConvertTo-PowerShellLiteral (Join-Path $GuestCaptureRoot "rank_scans")
    $guestProcessedRootLiteral = ConvertTo-PowerShellLiteral (Join-Path $GuestCaptureRoot "processed")
    $workspaceLiteral = ConvertTo-PowerShellLiteral $GuestWorkspace
    $guestStatePathLiteral = ConvertTo-PowerShellLiteral $guestStatePath

    $runner = @"
`$ErrorActionPreference = "Stop"
`$workspace = $workspaceLiteral
`$statePath = $guestStatePathLiteral
`$runRoot = Split-Path -Parent `$statePath
`$logRoot = Join-Path `$runRoot "logs"
New-Item -ItemType Directory -Force -Path `$logRoot | Out-Null
`$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
`$transcript = Join-Path `$logRoot "rank_scan_`$stamp.transcript.log"
Start-Transcript -LiteralPath `$transcript -Force | Out-Null
try {
    Set-Location `$workspace
    Write-Host "[vm-runner] started `$((Get-Date).ToString("o"))"
    Write-Host "[vm-runner] run_id $RunId"
    `$scanArgs = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", (Join-Path `$workspace "scripts\run_leaderboard_rank_scan.ps1"),
        "-RouteConfig", (Join-Path `$workspace "config\fh6_learned_route.json"),
        "-BaseConfig", (Join-Path `$workspace "config\fh6_current_leaderboard_row_scan.json"),
        "-Track", $trackLiteral,
        "-PiClass", $classLiteral,
        "-RivalsMode", $modeLiteral,
        "-EventType", $eventLiteral,
        "-TotalRanks", $totalRanksLiteral,
        "-StartRank", "$StartRank",
        "-CurrentRank", "$CurrentRank",
        "-ChunkSize", "$ChunkSize",
        "-RowsPerScreenshot", "$RowsPerScreenshot",
        "-RowDelayMs", "$RowDelayMs",
        "-RowSettleMs", "$RowSettleMs",
        "-NavigationDelayMs", "$NavigationDelayMs",
        "-NavigationBurstSize", "$NavigationBurstSize",
        "-NavigationSettleMs", "$NavigationSettleMs",
        "-NavigationCheckSeconds", "$NavigationCheckSeconds",
        "-NavigationSpamBurstSize", "$NavigationSpamBurstSize",
        "-NavigationSpamPauseMs", "$NavigationSpamPauseMs",
        "-NavigationFeedbackThreshold", "$NavigationFeedbackThreshold",
        "-NavigationMaxChecks", "$NavigationMaxChecks",
        "-ScrollbarJumpThreshold", "$ScrollbarJumpThreshold",
        "-RunId", $runIdLiteral,
        "-OutputRoot", $guestCaptureRootLiteral,
        "-ProcessedRoot", $guestProcessedRootLiteral,
        "-SkipExtract",
        "-NoDisplayManage"
    )
    if ($alreadyAtLeaderboardLiteral) {
        `$scanArgs += "-AlreadyAtLeaderboard"
    }
    if ($useScrollbarJumpLiteral) {
        `$scanArgs += "-UseScrollbarJump"
    }
    if ($noScrollbarJumpLiteral) {
        `$scanArgs += "-NoScrollbarJump"
    }
    & powershell @scanArgs
    if (`$LASTEXITCODE -ne 0) {
        throw "run_leaderboard_rank_scan.ps1 failed with exit code `$LASTEXITCODE"
    }
    Write-Host "[vm-runner] completed `$((Get-Date).ToString("o"))"
    exit 0
} catch {
    Write-Host "[vm-runner] ERROR `$(`$_.Exception.Message)"
    exit 1
} finally {
    Stop-Transcript | Out-Null
}
"@

    Write-ScanStart "write guest runner"
    Invoke-Command -Session $session -ArgumentList $guestRunnerPath, $runner, $taskName -ScriptBlock {
        param([string] $RunnerPath, [string] $Content, [string] $TaskName)
        Set-Content -LiteralPath $RunnerPath -Value $Content -Encoding UTF8
        Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue

        New-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" -Name "AutoAdminLogon" -PropertyType String -Value "1" -Force | Out-Null
        New-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" -Name "DefaultUserName" -PropertyType String -Value "admin" -Force | Out-Null
        New-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" -Name "DefaultPassword" -PropertyType String -Value "" -Force | Out-Null
        New-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" -Name "DefaultDomainName" -PropertyType String -Value $env:COMPUTERNAME -Force | Out-Null

        $action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$RunnerPath`""
        $principal = New-ScheduledTaskPrincipal -UserId "$env:COMPUTERNAME\admin" -LogonType Interactive -RunLevel Limited
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 72) -MultipleInstances IgnoreNew
        Register-ScheduledTask -TaskName $TaskName -Action $action -Principal $principal -Settings $settings -Force | Out-Null
        Start-ScheduledTask -TaskName $TaskName
        Start-Sleep -Seconds 2
        $task = Get-ScheduledTask -TaskName $TaskName
        $info = Get-ScheduledTaskInfo -TaskName $TaskName
        [pscustomobject]@{
            task = $task.TaskName
            state = [string]$task.State
            last_task_result = $info.LastTaskResult
            runner = $RunnerPath
        }
    } | Out-Host

    if (-not $NoHostSync) {
        $syncScript = Join-Path $PSScriptRoot "sync_vm_capture_via_psdirect.ps1"
        $syncOut = Join-Path $hostLogRoot "host_sync.out.log"
        $syncErr = Join-Path $hostLogRoot "host_sync.err.log"
        Write-ScanStart "start host sync watcher"
        $syncProcess = Start-Process powershell -ArgumentList @(
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-File", "`"$syncScript`"",
            "-VMName", "`"$VMName`"",
            "-VMUser", "`"$VMUser`"",
            "-RunId", "`"$RunId`"",
            "-GuestRunRoot", "`"$guestRunRoot`"",
            "-HostRunRoot", "`"$hostRunRoot`"",
            "-Watch"
        ) -RedirectStandardOutput $syncOut -RedirectStandardError $syncErr -WindowStyle Hidden -PassThru
    }

    if (-not $NoHostExtractor) {
        $extractScript = Join-Path $PSScriptRoot "run_host_extract_from_vm_captures.ps1"
        $waitScript = Join-Path $hostLogRoot "wait_and_extract.ps1"
        $extractOut = Join-Path $hostLogRoot "host_extract.out.log"
        $extractErr = Join-Path $hostLogRoot "host_extract.err.log"
        $extractScriptLiteral = ConvertTo-PowerShellLiteral $extractScript
        $statePathLiteral = ConvertTo-PowerShellLiteral $hostStatePath
        $processedRootLiteral = ConvertTo-PowerShellLiteral $hostProcessedRunRoot
        $ocrBackendLiteral = ConvertTo-PowerShellLiteral $OcrBackend
        $jobsLiteral = ConvertTo-PowerShellLiteral $ExtractParallelJobs
        $threadsLiteral = ConvertTo-PowerShellLiteral $OcrThreadsPerProcess
        @"
`$ErrorActionPreference = "Stop"
`$extractScript = $extractScriptLiteral
`$statePath = $statePathLiteral
`$processedRoot = $processedRootLiteral
for (`$i = 0; `$i -lt 300; `$i += 1) {
    if (Test-Path -LiteralPath `$statePath) {
        break
    }
    Start-Sleep -Seconds 2
}
if (-not (Test-Path -LiteralPath `$statePath)) {
    throw "Timed out waiting for VM state file: `$statePath"
}
`$extractArgs = @(
    "-NoProfile",
    "-ExecutionPolicy", "Bypass",
    "-File", `$extractScript,
    "-StatePath", `$statePath,
    "-ProcessedRunRoot", `$processedRoot,
    "-OcrBackend", $ocrBackendLiteral,
    "-ExtractParallelJobs", $jobsLiteral,
    "-OcrThreadsPerProcess", $threadsLiteral,
    "-Watch"
)
& powershell @extractArgs
exit `$LASTEXITCODE
"@ | Set-Content -LiteralPath $waitScript -Encoding UTF8

        Write-ScanStart "start host extractor watcher"
        $extractProcess = Start-Process powershell -ArgumentList @(
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-File", "`"$waitScript`""
        ) -RedirectStandardOutput $extractOut -RedirectStandardError $extractErr -WindowStyle Hidden -PassThru
    }

    $summary = [pscustomobject]@{
        run_id = $RunId
        vm = $VMName
        task = $taskName
        track = $Track
        pi_class = $PiClass
        rivals_mode = $RivalsMode
        already_at_leaderboard = [bool]$AlreadyAtLeaderboard
        guest_run_root = $guestRunRoot
        host_run_root = $hostRunRoot
        host_processed_root = $hostProcessedRunRoot
        host_state_path = $hostStatePath
        host_logs = $hostLogRoot
        sync_pid = if ($syncProcess) { $syncProcess.Id } else { $null }
        extractor_pid = if ($extractProcess) { $extractProcess.Id } else { $null }
    }
    $summary | ConvertTo-Json -Depth 5

    if (-not $Detach) {
        $watchStarted = $true
        Watch-ScanProgress -Session $session -RunRoot $guestRunRoot -TaskName $taskName -HostRunRoot $hostRunRoot -HostProcessedRunRoot $hostProcessedRunRoot -PollSeconds $ProgressPollSeconds
        $watchCompleted = $true
    }
}
finally {
    if ($watchStarted -and -not $watchCompleted) {
        Write-ScanStart "host watcher interrupted; stopping VM scan and host helpers for $RunId"
        if ($null -ne $session) {
            try {
                Invoke-Command -Session $session -ArgumentList $taskName, $RunId, $guestRunRoot -ScriptBlock {
                    param([string] $TaskName, [string] $RunId, [string] $RunRoot)
                    Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
                    $all = @(Get-CimInstance Win32_Process)
                    $targets = @($all | Where-Object {
                        $_.CommandLine -like "*$RunId*" -and $_.Name -match '^(powershell|python)(\.exe)?$'
                    })
                    $ids = [System.Collections.Generic.HashSet[int]]::new()
                    foreach ($process in $targets) {
                        [void]$ids.Add([int]$process.ProcessId)
                    }
                    $changed = $true
                    while ($changed) {
                        $changed = $false
                        foreach ($process in $all) {
                            if ($ids.Contains([int]$process.ParentProcessId) -and -not $ids.Contains([int]$process.ProcessId)) {
                                [void]$ids.Add([int]$process.ProcessId)
                                $changed = $true
                            }
                        }
                    }
                    foreach ($id in @($ids) | Sort-Object -Descending) {
                        Stop-Process -Id $id -Force -ErrorAction SilentlyContinue
                    }
                    $statePath = Join-Path $RunRoot "state.json"
                    if (Test-Path -LiteralPath $statePath) {
                        try {
                            $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
                            $state.status = "cancelled_by_host"
                            $state.updated_at = (Get-Date).ToUniversalTime().ToString("o")
                            $state | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $statePath -Encoding UTF8
                        } catch {
                        }
                    }
                } -ErrorAction SilentlyContinue
            } catch {
                Write-ScanStart "warning: could not stop guest scan cleanly: $($_.Exception.Message)"
            }
        }

        $allHostProcesses = @(Get-CimInstance Win32_Process)
        $hostTargets = @($allHostProcesses | Where-Object {
            $_.CommandLine -like "*$RunId*" -and $_.ProcessId -ne $PID -and $_.Name -match '^(powershell|python)(\.exe)?$'
        })
        $hostIds = [System.Collections.Generic.HashSet[int]]::new()
        foreach ($process in $hostTargets) {
            [void]$hostIds.Add([int]$process.ProcessId)
        }
        $changed = $true
        while ($changed) {
            $changed = $false
            foreach ($process in $allHostProcesses) {
                if ($hostIds.Contains([int]$process.ParentProcessId) -and -not $hostIds.Contains([int]$process.ProcessId)) {
                    [void]$hostIds.Add([int]$process.ProcessId)
                    $changed = $true
                }
            }
        }
        foreach ($id in @($hostIds) | Sort-Object -Descending) {
            Stop-Process -Id $id -Force -ErrorAction SilentlyContinue
        }

        if (Test-Path -LiteralPath $hostRunRoot) {
            [pscustomobject]@{
                cancelled_at = (Get-Date).ToUniversalTime().ToString("o")
                run_id = $RunId
                reason = "host watcher interrupted"
            } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $hostRunRoot ".cancelled.json") -Encoding UTF8
        }
    }
    if ($null -ne $session) {
        Remove-PSSession $session
    }
}

Write-ScanStart "done"
