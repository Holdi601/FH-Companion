param(
    [string] $RouteConfig = "config/fh6_learned_route.json",
    [string] $BaseConfig = "config/fh6_current_leaderboard_row_scan.json",
    [string] $Track = "",
    [Alias("PerformanceClass")]
    [string] $PiClass = "D",
    [string] $EventType = "Rivals",
    [string] $RivalsMode = "Road Racing",
    [string] $TotalRanks = "Auto",
    [int] $StartRank = 1,
    [int] $CurrentRank = 1,
    [int] $ChunkSize = 550,
    [int] $RowsPerScreenshot = 11,
    [string] $ExtractParallelJobs = "Auto",
    [ValidateSet("windows-batch", "windows-single", "rapidocr-cpu", "rapidocr-gpu")]
    [string] $OcrBackend = "rapidocr-gpu",
    [string] $OcrThreadsPerProcess = "Auto",
    [int] $MaxRetryPasses = 2,
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
    [int] $IdleSeconds = 30,
    [double] $IdlePollSeconds = 2.0,
    [int] $IdleChunkSize = 110,
    [int] $IdleNavigationBatchSize = 220,
    [string] $RunId = "",
    [string] $ResumeFrom = "",
    [string] $OutputRoot = "data/rank_scans",
    [string] $ProcessedRoot = "data/processed/rank_scans",
    [int] $StartupWaitSeconds = 90,
    [switch] $AlreadyAtLeaderboard,
    [switch] $RowScan,
    [switch] $SynchronousExtract,
    [switch] $IdleOnly,
    [switch] $NoDisplayManage,
    [switch] $UseScrollbarJump,
    [switch] $NoScrollbarJump,
    [switch] $SkipExtract,
    [switch] $DryRun
)

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$script:extractorProcesses = @()

function Resolve-WorkspacePath {
    param([Parameter(Mandatory = $true)][string] $Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }
    return [System.IO.Path]::GetFullPath((Join-Path $workspace $Path))
}

function Write-RankScan {
    param([Parameter(Mandatory = $true)][string] $Message)
    Write-Host "[rank-scan] $Message"
}

function ConvertTo-PositiveIntOrNull {
    param([AllowNull()] $Value)

    if ($null -eq $Value) {
        return $null
    }
    $text = ([string]$Value).Trim()
    if ([string]::IsNullOrWhiteSpace($text)) {
        return $null
    }
    $parsed = 0
    if ([int]::TryParse($text, [ref]$parsed) -and $parsed -gt 0) {
        return $parsed
    }
    return $null
}

function Get-SystemComputeProfile {
    $logicalThreads = [Environment]::ProcessorCount
    $physicalCores = 0
    $processorName = ""
    try {
        $processors = @(Get-CimInstance -ClassName Win32_Processor -ErrorAction Stop)
        if ($processors.Count -gt 0) {
            $logicalSum = 0
            $coreSum = 0
            foreach ($processor in $processors) {
                $logicalSum += [int]$processor.NumberOfLogicalProcessors
                $coreSum += [int]$processor.NumberOfCores
            }
            if ($logicalSum -gt 0) {
                $logicalThreads = $logicalSum
            }
            if ($coreSum -gt 0) {
                $physicalCores = $coreSum
            }
            $processorName = [string]($processors | Select-Object -First 1 -ExpandProperty Name)
        }
    } catch {
        $physicalCores = 0
    }

    if ($physicalCores -lt 1) {
        $physicalCores = [Math]::Max(1, [int][Math]::Ceiling($logicalThreads / 2.0))
    }
    $reserveThreads = [Math]::Max(1, [int][Math]::Ceiling($logicalThreads * 0.20))
    if ($logicalThreads -ge 12) {
        $reserveThreads = [Math]::Max(2, $reserveThreads)
    }
    $usableThreads = [Math]::Max(1, $logicalThreads - $reserveThreads)

    return [pscustomobject]@{
        processor_name = $processorName
        physical_cores = $physicalCores
        logical_threads = $logicalThreads
        reserved_threads = $reserveThreads
        usable_threads = $usableThreads
    }
}

function Resolve-ExtractParallelJobs {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string] $Requested,
        [Parameter(Mandatory = $true)][string] $Backend,
        [Parameter(Mandatory = $true)] $ComputeProfile
    )

    $explicit = ConvertTo-PositiveIntOrNull $Requested
    if ($null -ne $explicit) {
        return [pscustomobject]@{
            value = $explicit
            source = "explicit"
            reason = "user override"
        }
    }

    $usable = [Math]::Max(1, [int]$ComputeProfile.usable_threads)
    $physical = [Math]::Max(1, [int]$ComputeProfile.physical_cores)
    switch ($Backend) {
        "rapidocr-gpu" {
            return [pscustomobject]@{
                value = 1
                source = "auto"
                reason = "single warm GPU worker avoids CUDA context contention"
            }
        }
        "rapidocr-cpu" {
            return [pscustomobject]@{
                value = [Math]::Max(1, [Math]::Min($physical, [int][Math]::Floor($usable / 2.0)))
                source = "auto"
                reason = "CPU OCR workers get multiple threads each"
            }
        }
        default {
            return [pscustomobject]@{
                value = [Math]::Max(1, [Math]::Min($physical, [int][Math]::Floor($usable / 2.0)))
                source = "auto"
                reason = "Windows OCR workers are process-parallel with headroom"
            }
        }
    }
}

function Resolve-OcrThreadsPerProcess {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string] $Requested,
        [Parameter(Mandatory = $true)][string] $Backend,
        [Parameter(Mandatory = $true)][int] $ResolvedJobs,
        [Parameter(Mandatory = $true)] $ComputeProfile
    )

    $explicit = ConvertTo-PositiveIntOrNull $Requested
    if ($null -ne $explicit) {
        return [pscustomobject]@{
            value = $explicit
            source = "explicit"
        }
    }

    $usable = [Math]::Max(1, [int]$ComputeProfile.usable_threads)
    switch ($Backend) {
        "rapidocr-gpu" {
            return [pscustomobject]@{
                value = 1
                source = "auto"
            }
        }
        "rapidocr-cpu" {
            return [pscustomobject]@{
                value = [Math]::Max(1, [int][Math]::Floor($usable / [Math]::Max(1, $ResolvedJobs)))
                source = "auto"
            }
        }
        default {
            return [pscustomobject]@{
                value = 1
                source = "auto"
            }
        }
    }
}

function ConvertTo-SafeName {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string] $Value)

    $safe = $Value.ToLowerInvariant() -replace "[^a-z0-9]+", "_"
    $safe = $safe.Trim("_")
    if ([string]::IsNullOrWhiteSpace($safe)) {
        return "unknown"
    }
    return $safe
}

function Ensure-Property {
    param(
        [Parameter(Mandatory = $true)] $Object,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)] $Value
    )

    if ($Object.PSObject.Properties.Name -contains $Name) {
        $Object.$Name = $Value
    } else {
        $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
    }
}

function Set-NestedProperty {
    param(
        [Parameter(Mandatory = $true)] $Object,
        [Parameter(Mandatory = $true)][string] $Parent,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)] $Value
    )

    if (-not ($Object.PSObject.Properties.Name -contains $Parent) -or $null -eq $Object.$Parent) {
        $Object | Add-Member -NotePropertyName $Parent -NotePropertyValue ([pscustomobject]@{})
    }
    Ensure-Property -Object $Object.$Parent -Name $Name -Value $Value
}

function Read-JsonConfig {
    param([Parameter(Mandatory = $true)][string] $Path)

    $resolved = Resolve-WorkspacePath $Path
    if (-not (Test-Path -LiteralPath $resolved)) {
        throw "Config not found: $resolved"
    }
    return Get-Content -LiteralPath $resolved -Raw | ConvertFrom-Json
}

function Write-RuntimeConfig {
    param(
        [Parameter(Mandatory = $true)] $ConfigObject,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $RuntimeDir
    )

    New-Item -ItemType Directory -Force -Path $RuntimeDir | Out-Null
    $path = Join-Path $RuntimeDir "$Name.json"
    $ConfigObject | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $path -Encoding UTF8
    return $path
}

function New-KeyStep {
    param(
        [Parameter(Mandatory = $true)][string] $Key,
        [int] $Count = 1,
        [int] $DelayMs = 250,
        [switch] $Burst,
        [int] $BurstSize = 250,
        [int] $SettleAfterMs = 0
    )

    return [pscustomobject]@{
        action = "key"
        key = $Key
        count = $Count
        delay_ms = $DelayMs
        burst = [bool]$Burst
        burst_size = $BurstSize
        settle_after_ms = $SettleAfterMs
    }
}

function New-WaitLeaderboardStep {
    return [pscustomobject]@{
        action = "wait_for_text"
        pattern = "Driver|Drivetrain|Filter:\s+Global|Change\s+Filter|Player\s+options|Change\s+Rival"
        timeout_seconds = 45
        interval_seconds = 1
        name = "wait_leaderboard_{index:000}.png"
    }
}

function New-BaseRuntimeConfig {
    param([Parameter(Mandatory = $true)] $State)

    $config = Read-JsonConfig $State.base_config
    Set-NestedProperty -Object $config -Parent "display" -Name "manage" -Value $false
    Set-NestedProperty -Object $config -Parent "display" -Name "restore_after" -Value $false
    Set-NestedProperty -Object $config -Parent "launch" -Name "startup_wait_seconds" -Value 0
    Set-NestedProperty -Object $config -Parent "window" -Name "idle_only" -Value ([bool]$State.idle_only)
    Set-NestedProperty -Object $config -Parent "window" -Name "idle_seconds" -Value ([int]$State.idle_seconds)
    Set-NestedProperty -Object $config -Parent "window" -Name "idle_poll_seconds" -Value ([double]$State.idle_poll_seconds)
    Set-NestedProperty -Object $config -Parent "window" -Name "restore_previous_after_run" -Value ([bool]$State.idle_only)
    Set-NestedProperty -Object $config -Parent "capture" -Name "dump_state_ocr" -Value $true
    Set-NestedProperty -Object $config -Parent "extraction" -Name "track" -Value $State.track
    Set-NestedProperty -Object $config -Parent "extraction" -Name "performance_class" -Value $State.pi_class
    Set-NestedProperty -Object $config -Parent "extraction" -Name "pi_class" -Value $State.pi_class
    Set-NestedProperty -Object $config -Parent "extraction" -Name "event_type" -Value $State.event_type
    Set-NestedProperty -Object $config -Parent "extraction" -Name "rivals_mode" -Value $State.rivals_mode
    return $config
}

function Invoke-CaptureAutomation {
    param(
        [Parameter(Mandatory = $true)][string] $ConfigPath,
        [switch] $NavigationOnly
    )

    $script = Resolve-WorkspacePath "scripts/run_capture_automation.ps1"
    $args = @(
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        $script,
        "-Config",
        $ConfigPath,
        "-SkipLaunch"
    )
    if ($NavigationOnly) {
        $args += "-SkipExtract"
    }
    if ($DryRun) {
        $args += "-DryRun"
    }

    & powershell @args | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) {
        throw "run_capture_automation.ps1 failed with exit code $LASTEXITCODE"
    }
}

function ConvertTo-ProcessArgument {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string] $Value)

    if ($Value -notmatch '[\s"]') {
        return $Value
    }
    return '"' + ($Value -replace '"', '\"') + '"'
}

function Get-LogTail {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [int] $Lines = 30
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return ""
    }
    return (Get-Content -LiteralPath $Path -Tail $Lines -ErrorAction SilentlyContinue) -join [Environment]::NewLine
}

function Complete-ExtractorProcess {
    param([Parameter(Mandatory = $true)] $Job)

    $Job.process.WaitForExit()
    $Job.process.Refresh()
    $exitCode = $Job.process.ExitCode
    if ($null -eq $exitCode) {
        $stderrText = Get-LogTail -Path $Job.stderr -Lines 200
        if ([string]::IsNullOrWhiteSpace($stderrText)) {
            $exitCode = 0
        }
    }
    if ($exitCode -ne 0) {
        $stderrTail = Get-LogTail -Path $Job.stderr
        $stdoutTail = Get-LogTail -Path $Job.stdout
        throw "Extractor job '$($Job.name)' failed with exit code $exitCode. stderr tail:`n$stderrTail`nstdout tail:`n$stdoutTail"
    }
    Write-RankScan "extractor done: $($Job.name)"
}

function Reap-ExtractorProcesses {
    param([switch] $WaitForSlot)

    $remaining = @()
    foreach ($job in @($script:extractorProcesses)) {
        if ($job.process.HasExited) {
            Complete-ExtractorProcess -Job $job
        } else {
            $remaining += $job
        }
    }
    $script:extractorProcesses = @($remaining)

    while ($WaitForSlot -and $script:extractorProcesses.Count -ge [Math]::Max(1, $ExtractParallelJobs)) {
        $oldest = $script:extractorProcesses | Select-Object -First 1
        Complete-ExtractorProcess -Job $oldest
        $script:extractorProcesses = @($script:extractorProcesses | Where-Object { $_.name -ne $oldest.name })
        Reap-ExtractorProcesses
    }
}

function Wait-AllExtractors {
    Reap-ExtractorProcesses
    foreach ($job in @($script:extractorProcesses)) {
        Complete-ExtractorProcess -Job $job
    }
    $script:extractorProcesses = @()
}

function Start-ExtractorProcess {
    param(
        [Parameter(Mandatory = $true)] $State,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $CaptureDir,
        [Parameter(Mandatory = $true)][string] $ProcessedDir,
        [ValidateSet("fast", "precise")]
        [string] $OcrMode = "fast",
        [ValidateSet("windows-batch", "windows-single", "rapidocr-cpu", "rapidocr-gpu")]
        [string] $Backend = $OcrBackend
    )

    if ($SkipExtract -or $DryRun) {
        Write-RankScan "extractor skipped: $Name"
        return
    }

    Reap-ExtractorProcesses -WaitForSlot

    New-Item -ItemType Directory -Force -Path $ProcessedDir | Out-Null
    $logDir = Join-Path $State.run_root "extract_logs"
    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    $stdout = Join-Path $logDir "$Name.out.log"
    $stderr = Join-Path $logDir "$Name.err.log"
    $profile = Resolve-WorkspacePath "config/fh6_rivals_1080p.json"
    $args = @(
        (Resolve-WorkspacePath "scripts/extract_leaderboard.py"),
        "--input", $CaptureDir,
        "--include-glob", "leaderboard*.png",
        "--track", [string]$State.track,
        "--performance-class", [string]$State.pi_class,
        "--event-type", [string]$State.event_type,
        "--rivals-mode", [string]$State.rivals_mode,
        "--profile", $profile,
        "--output-dir", $ProcessedDir,
        "--ocr-mode", $OcrMode,
        "--ocr-backend", $Backend,
        "--dump-ocr"
    )
    $argumentLine = ($args | ForEach-Object { ConvertTo-ProcessArgument ([string]$_) }) -join " "
    Write-RankScan "extractor start: $Name [$OcrMode/$Backend] (running $($script:extractorProcesses.Count + 1)/$ExtractParallelJobs, threads/process $($State.ocr_threads_per_process))"
    $previousThreadBudget = $env:FORZA_OCR_THREADS_PER_PROCESS
    $env:FORZA_OCR_THREADS_PER_PROCESS = [string]$State.ocr_threads_per_process
    try {
        $process = Start-Process -FilePath "python" -ArgumentList $argumentLine -RedirectStandardOutput $stdout -RedirectStandardError $stderr -WindowStyle Hidden -PassThru
    } finally {
        if ($null -eq $previousThreadBudget) {
            Remove-Item Env:\FORZA_OCR_THREADS_PER_PROCESS -ErrorAction SilentlyContinue
        } else {
            $env:FORZA_OCR_THREADS_PER_PROCESS = $previousThreadBudget
        }
    }
    $script:extractorProcesses += [pscustomobject]@{
        name = $Name
        process = $process
        stdout = $stdout
        stderr = $stderr
    }
    if ($SynchronousExtract) {
        Wait-AllExtractors
    }
}

function Invoke-InitialRoute {
    param([Parameter(Mandatory = $true)] $State)

    if ($AlreadyAtLeaderboard) {
        Write-RankScan "using current visible leaderboard"
        return
    }

    Write-RankScan "launch/navigate to leaderboard via $($State.route_config)"
    $script = Resolve-WorkspacePath "scripts/run_forza_pipeline.ps1"
    $args = @(
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        $script,
        "-Mode", "Capture",
        "-RouteConfig", $State.route_config,
        "-Track", $State.track,
        "-PerformanceClass", $State.pi_class,
        "-EventType", $State.event_type,
        "-RivalsMode", $State.rivals_mode,
        "-Pages", "1",
        "-StartupWaitSeconds", ([string]$StartupWaitSeconds),
        "-SkipExtract"
    )
    if ($NoDisplayManage) {
        $args += "-NoDisplayManage"
    }
    if ($State.idle_only) {
        $args += @("-IdleOnly", "-IdleSeconds", ([string]$State.idle_seconds), "-IdlePollSeconds", ([string]$State.idle_poll_seconds))
    }
    if ($DryRun) {
        $args += "-DryRun"
    }

    & powershell @args | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) {
        throw "run_forza_pipeline.ps1 failed with exit code $LASTEXITCODE"
    }
}

function New-ObserveConfig {
    param([Parameter(Mandatory = $true)] $State)

    $config = New-BaseRuntimeConfig -State $State
    $out = Join-Path $State.runtime_dir "observe"
    Set-NestedProperty -Object $config -Parent "capture" -Name "output_dir" -Value $out
    Set-NestedProperty -Object $config -Parent "capture" -Name "state_dir" -Value (Join-Path $out "_state")
    Set-NestedProperty -Object $config -Parent "capture" -Name "clear_output_dir" -Value $true
    Set-NestedProperty -Object $config -Parent "extraction" -Name "enabled" -Value $false
    $config.steps = @(
        (New-WaitLeaderboardStep),
        [pscustomobject]@{ action = "observe"; name = "leaderboard_context.png" }
    )
    return Write-RuntimeConfig -ConfigObject $config -Name "observe_leaderboard" -RuntimeDir $State.runtime_dir
}

function Get-LatestOcrText {
    param([Parameter(Mandatory = $true)][string] $StateDir)

    if (-not (Test-Path -LiteralPath $StateDir)) {
        return ""
    }
    $file = Get-ChildItem -LiteralPath $StateDir -Filter "*.ocr.txt" |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -eq $file) {
        return ""
    }
    return Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
}

function Resolve-TotalRanks {
    param([Parameter(Mandatory = $true)] $State)

    if ($TotalRanks.Trim() -match "^(?i:until[-_ ]?end|end|all)$") {
        if ($DryRun) {
            throw "-TotalRanks UntilEnd cannot be used with -DryRun because no real screenshots exist for end detection. Pass a numeric -TotalRanks for dry runs."
        }
        Ensure-Property -Object $State -Name "total_ranks_unknown" -Value $true
        Ensure-Property -Object $State -Name "total_ranks_mode" -Value "until_end"
        Ensure-Property -Object $State -Name "coverage_total_ranks" -Value 0
        return 0
    }
    if (($State.total_ranks -as [int]) -gt 0) {
        return [int]$State.total_ranks
    }
    if ($TotalRanks.Trim() -notmatch "^(?i:auto)$") {
        Ensure-Property -Object $State -Name "total_ranks_unknown" -Value $false
        Ensure-Property -Object $State -Name "total_ranks_mode" -Value "explicit"
        Ensure-Property -Object $State -Name "coverage_total_ranks" -Value ([int]$TotalRanks)
        return [int]$TotalRanks
    }
    if ($DryRun) {
        throw "-TotalRanks Auto cannot be used with -DryRun. Pass a numeric -TotalRanks for dry runs."
    }

    $configPath = New-ObserveConfig -State $State
    Invoke-CaptureAutomation -ConfigPath $configPath -NavigationOnly
    $text = Get-LatestOcrText -StateDir (Join-Path (Join-Path $State.runtime_dir "observe") "_state")
    $match = [regex]::Match($text, "([0-9][0-9., ]*)\s+Players", [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $match.Success) {
        Write-RankScan "could not detect total players from the visible leaderboard; falling back to UntilEnd mode"
        Write-RankScan "precondition reminder: if this track/class has no personal Rivals time yet, drive/post a time first so the Change Rival leaderboard exists"
        Ensure-Property -Object $State -Name "total_ranks_unknown" -Value $true
        Ensure-Property -Object $State -Name "total_ranks_mode" -Value "auto_until_end"
        Ensure-Property -Object $State -Name "coverage_total_ranks" -Value 0
        return 0
    }
    $numberText = ($match.Groups[1].Value -replace "[^0-9]", "")
    if ([string]::IsNullOrWhiteSpace($numberText)) {
        throw "Detected invalid player count: $($match.Groups[1].Value)"
    }
    Ensure-Property -Object $State -Name "total_ranks_unknown" -Value $false
    Ensure-Property -Object $State -Name "total_ranks_mode" -Value "auto_detected"
    Ensure-Property -Object $State -Name "coverage_total_ranks" -Value ([int]$numberText)
    return [int]$numberText
}

function New-NavigationConfig {
    param(
        [Parameter(Mandatory = $true)] $State,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)] $Steps
    )

    $config = New-BaseRuntimeConfig -State $State
    $out = Join-Path $State.runtime_dir "nav_$Name"
    Set-NestedProperty -Object $config -Parent "capture" -Name "output_dir" -Value $out
    Set-NestedProperty -Object $config -Parent "capture" -Name "state_dir" -Value (Join-Path $out "_state")
    Set-NestedProperty -Object $config -Parent "capture" -Name "clear_output_dir" -Value $true
    Set-NestedProperty -Object $config -Parent "extraction" -Name "enabled" -Value $false
    $config.steps = @($Steps)
    return Write-RuntimeConfig -ConfigObject $config -Name "nav_$Name" -RuntimeDir $State.runtime_dir
}

function Get-SelectedRankFromSnapshot {
    param(
        [Parameter(Mandatory = $true)][string] $OutputDir,
        [Parameter(Mandatory = $true)][string] $StateDir,
        [int] $MaxRank = 0,
        [int] $ReferenceRank = 0
    )

    $jsonFile = Get-ChildItem -LiteralPath $StateDir -File -Filter "*.ocr.json" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -eq $jsonFile) {
        return 0
    }

    $ocr = Get-Content -LiteralPath $jsonFile.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    $width = [int]$ocr.width
    $height = [int]$ocr.height
    if ($width -lt 1 -or $height -lt 1) {
        return 0
    }

    $candidates = @()
    foreach ($line in @($ocr.lines)) {
        foreach ($word in @($line.words)) {
            $text = ([string]$word.text).Trim()
            if ([double]$word.x -gt ($width * 0.095) -or [double]$word.y -lt ($height * 0.20) -or [double]$word.y -gt ($height * 0.84)) {
                continue
            }
            if ($text -notmatch "^[0-9][0-9.,]*$") {
                continue
            }
            $digits = $text -replace "[^0-9]", ""
            $rank = 0
            if (
                -not [int]::TryParse($digits, [ref]$rank) -or
                $rank -lt 1 -or
                ($MaxRank -gt 0 -and $rank -gt $MaxRank)
            ) {
                continue
            }
            $candidates += [pscustomobject]@{
                rank = $rank
                y = [int][Math]::Round([double]$word.y + ([double]$word.height / 2.0))
                digit_length = $digits.Length
            }
        }
    }
    if ($candidates.Count -eq 0) {
        return 0
    }
    if ($ReferenceRank -ge 1000) {
        $referenceDigits = $ReferenceRank.ToString().Length
        $fullLengthCandidates = @($candidates | Where-Object { [int]$_.digit_length -ge $referenceDigits })
        if ($fullLengthCandidates.Count -gt 0) {
            $candidates = $fullLengthCandidates
        } else {
            $digitBoundary = [Math]::Pow(10, $referenceDigits - 1)
            $boundaryTolerance = [Math]::Max(500.0, $digitBoundary * 0.05)
            if ([Math]::Abs($ReferenceRank - $digitBoundary) -gt $boundaryTolerance) {
                Write-RankScan "reject shortened rank OCR far from a digit boundary (reference $ReferenceRank)"
                return 0
            }
        }
    }

    $baseName = $jsonFile.Name.Substring(0, $jsonFile.Name.Length - ".ocr.json".Length)
    $imagePath = Join-Path $OutputDir "$baseName.png"
    if (-not (Test-Path -LiteralPath $imagePath)) {
        return 0
    }

    Add-Type -AssemblyName System.Drawing
    $bitmap = $null
    try {
        $bitmap = [System.Drawing.Bitmap]::new($imagePath)
        $selectedY = 0
        $selectedScore = [double]::PositiveInfinity
        $scanTop = [int][Math]::Round($bitmap.Height * 0.25)
        $scanBottom = [int][Math]::Round($bitmap.Height * 0.81)
        for ($y = $scanTop; $y -le $scanBottom; $y += 2) {
            $total = 0.0
            $samples = 0
            foreach ($offsetY in @(-3, 0, 3)) {
                $sampleY = [Math]::Min($bitmap.Height - 1, [Math]::Max(0, $y + $offsetY))
                for ($i = 0; $i -lt 80; $i += 1) {
                    $x = [int][Math]::Round(($bitmap.Width * 0.12) + (($bitmap.Width * 0.78) * (($i + 0.5) / 80.0)))
                    $x = [Math]::Min($bitmap.Width - 1, [Math]::Max(0, $x))
                    $pixel = $bitmap.GetPixel($x, $sampleY)
                    $total += (0.299 * $pixel.R) + (0.587 * $pixel.G) + (0.114 * $pixel.B)
                    $samples += 1
                }
            }
            $score = if ($samples -gt 0) { $total / $samples } else { 255.0 }
            if ($score -lt $selectedScore) {
                $selectedScore = $score
                $selectedY = $y
            }
        }

        $sortedCandidates = @($candidates | Sort-Object y)
        $rowDiffs = @()
        for ($i = 1; $i -lt $sortedCandidates.Count; $i += 1) {
            $diff = [int]$sortedCandidates[$i].y - [int]$sortedCandidates[$i - 1].y
            if ($diff -ge 20 -and $diff -le 60) {
                $rowDiffs += $diff
            }
        }
        $rowSpacing = $bitmap.Height * 0.05
        if ($rowDiffs.Count -gt 0) {
            $orderedDiffs = @($rowDiffs | Sort-Object)
            $rowSpacing = [double]$orderedDiffs[[int][Math]::Floor($orderedDiffs.Count / 2)]
        }

        $nearest = $sortedCandidates |
            Sort-Object @{ Expression = { [Math]::Abs([int]$_.y - $selectedY) } } |
            Select-Object -First 1
        if ($null -eq $nearest -or $rowSpacing -le 0) {
            return 0
        }
        $rowOffset = [int][Math]::Round(($selectedY - [int]$nearest.y) / $rowSpacing)
        return [Math]::Max(1, [int]$nearest.rank + $rowOffset)
    } catch {
        Write-RankScan "warning: could not identify selected rank after scrollbar jump: $($_.Exception.Message)"
        return 0
    } finally {
        if ($null -ne $bitmap) {
            $bitmap.Dispose()
        }
    }
}

function Invoke-ScrollbarJumpToRank {
    param(
        [Parameter(Mandatory = $true)] $State,
        [Parameter(Mandatory = $true)][int] $TargetRank,
        [Parameter(Mandatory = $true)][string] $Name
    )

    $total = [int]$State.total_ranks
    $current = [int]$State.current_selected_rank
    if ($total -lt 2) {
        return $current
    }

    $scrollbarX = 0.926
    $scrollbarTop = 0.255
    $scrollbarBottom = 0.805
    $startRatio = [Math]::Min(1.0, [Math]::Max(0.0, ($current - 1) / [double]($total - 1)))
    $targetRatio = [Math]::Min(1.0, [Math]::Max(0.0, ($TargetRank - 1) / [double]($total - 1)))
    $startY = $scrollbarTop + (($scrollbarBottom - $scrollbarTop) * $startRatio)
    $targetY = $scrollbarTop + (($scrollbarBottom - $scrollbarTop) * $targetRatio)

    Write-RankScan "large jump via leaderboard scrollbar ($current -> $TargetRank of $total)"
    $steps = @(
        (New-WaitLeaderboardStep),
        [pscustomobject]@{
            action = "drag"
            start_x_ratio = $scrollbarX
            start_y_ratio = $startY
            end_x_ratio = $scrollbarX
            end_y_ratio = $targetY
            duration_ms = 220
            delay_ms = 350
        },
        [pscustomobject]@{
            action = "observe"
            name = "scrollbar_verify.png"
            ocr = $true
        }
    )
    $configPath = New-NavigationConfig -State $State -Name $Name -Steps $steps
    Invoke-CaptureAutomation -ConfigPath $configPath -NavigationOnly
    if ($DryRun) {
        return $TargetRank
    }

    $outputDir = Join-Path $State.runtime_dir "nav_$Name"
    $actualRank = Get-SelectedRankFromSnapshot `
        -OutputDir $outputDir `
        -StateDir (Join-Path $outputDir "_state") `
        -MaxRank ([int]$State.total_ranks) `
        -ReferenceRank $TargetRank
    if ($actualRank -gt 0) {
        Write-RankScan "scrollbar jump landed on selected rank $actualRank"
        return $actualRank
    }
    Write-RankScan "warning: scrollbar jump rank could not be verified; using fast key navigation from the previous rank"
    return $current
}

function Invoke-NavigationProbe {
    param(
        [Parameter(Mandatory = $true)] $State,
        [Parameter(Mandatory = $true)][string] $Key,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][int] $ExpectedRank,
        [int] $Count = 0,
        [double] $DurationSeconds = 0
    )

    $action = $null
    if ($DurationSeconds -gt 0) {
        $action = [pscustomobject]@{
            action = "key_for_duration"
            key = $Key
            duration_seconds = $DurationSeconds
            burst_size = $NavigationSpamBurstSize
            burst_pause_ms = $NavigationSpamPauseMs
            settle_after_ms = $NavigationSettleMs
        }
    } elseif ($Count -gt 0) {
        # Exact final corrections must be paced so Forza consumes every key press.
        $action = New-KeyStep `
            -Key $Key `
            -Count $Count `
            -DelayMs ([Math]::Max(80, $NavigationDelayMs)) `
            -SettleAfterMs $NavigationSettleMs
    }

    $steps = @((New-WaitLeaderboardStep))
    if ($null -ne $action) {
        $steps += $action
    }
    $steps += [pscustomobject]@{
            action = "observe"
            name = "navigation_verify.png"
            ocr = $true
        }
    $configPath = New-NavigationConfig -State $State -Name $Name -Steps $steps
    Invoke-CaptureAutomation -ConfigPath $configPath -NavigationOnly
    if ($DryRun) {
        return $ExpectedRank
    }

    $outputDir = Join-Path $State.runtime_dir "nav_$Name"
    $actualRank = Get-SelectedRankFromSnapshot `
        -OutputDir $outputDir `
        -StateDir (Join-Path $outputDir "_state") `
        -MaxRank ([int]$State.total_ranks) `
        -ReferenceRank $ExpectedRank
    for ($verifyAttempt = 1; $actualRank -lt 1 -and $verifyAttempt -le 3; $verifyAttempt += 1) {
        Write-RankScan "navigation rank was not readable; wait for the table to settle and verify again ($verifyAttempt/3)"
        Start-Sleep -Milliseconds ([Math]::Max(500, $NavigationSettleMs))
        $retryName = "{0}_settle_retry_{1}" -f $Name, $verifyAttempt
        $retrySteps = @(
            (New-WaitLeaderboardStep),
            [pscustomobject]@{
                action = "observe"
                name = "navigation_verify.png"
                ocr = $true
            }
        )
        $retryConfigPath = New-NavigationConfig -State $State -Name $retryName -Steps $retrySteps
        Invoke-CaptureAutomation -ConfigPath $retryConfigPath -NavigationOnly
        $retryOutputDir = Join-Path $State.runtime_dir "nav_$retryName"
        $actualRank = Get-SelectedRankFromSnapshot `
            -OutputDir $retryOutputDir `
            -StateDir (Join-Path $retryOutputDir "_state") `
            -MaxRank ([int]$State.total_ranks) `
            -ReferenceRank $ExpectedRank
    }
    if ($actualRank -lt 1) {
        throw "Could not verify the selected leaderboard rank after navigation. No capture screenshots were started. Inspect $outputDir"
    }
    return $actualRank
}

function Invoke-MoveToRank {
    param(
        [Parameter(Mandatory = $true)] $State,
        [Parameter(Mandatory = $true)][int] $TargetRank,
        [Parameter(Mandatory = $true)][string] $Name
    )

    $current = [int]$State.current_selected_rank
    $current = Invoke-NavigationProbe -State $State -Key "DOWN" -Name ("{0}_initial_verify" -f $Name) -ExpectedRank $current
    $State.current_selected_rank = $current
    Ensure-Property -Object $State -Name "navigation_verified_rank" -Value $current
    Save-State -State $State
    Write-RankScan "initial selected rank verified at $current"
    $delta = $TargetRank - $current
    if ($delta -eq 0) {
        return
    }

    if ($UseScrollbarJump -and -not $NoScrollbarJump -and [Math]::Abs($delta) -ge $ScrollbarJumpThreshold -and ([int]$State.total_ranks) -gt 1) {
        $current = Invoke-ScrollbarJumpToRank -State $State -TargetRank $TargetRank -Name ("scrollbar_$Name")
        $State.current_selected_rank = $current
        Save-State -State $State
        $delta = $TargetRank - $current
        if ($delta -eq 0) {
            return
        }
    }

    Write-RankScan "verified navigation $current -> $TargetRank; OCR check every $NavigationCheckSeconds seconds"
    $measuredRanksPerSecond = 0.0
    $stagnantChecks = 0
    Ensure-Property -Object $State -Name "navigation_target_rank" -Value $TargetRank
    Ensure-Property -Object $State -Name "navigation_verified_rank" -Value $current
    Ensure-Property -Object $State -Name "navigation_ranks_per_second" -Value 0.0
    Save-State -State $State -Status "navigating"
    for ($check = 1; $check -le $NavigationMaxChecks; $check += 1) {
        $delta = $TargetRank - $current
        if ($delta -eq 0) {
            $confirmationRanks = @()
            $targetConfirmations = 0
            for ($confirmAttempt = 1; $confirmAttempt -le 3; $confirmAttempt += 1) {
                $confirmedRank = Invoke-NavigationProbe `
                    -State $State `
                    -Key "DOWN" `
                    -Name ("{0}_target_confirm_{1}" -f $Name, $confirmAttempt) `
                    -ExpectedRank $TargetRank
                $confirmationRanks += $confirmedRank
                if ($confirmedRank -eq $TargetRank) {
                    $targetConfirmations += 1
                    if ($targetConfirmations -ge 2) {
                        break
                    }
                }
            }
            if ($targetConfirmations -ge 2) {
                Ensure-Property -Object $State -Name "navigation_verified_rank" -Value $TargetRank
                Ensure-Property -Object $State -Name "navigation_ranks_per_second" -Value $measuredRanksPerSecond
                Save-State -State $State -Status "active"
                Write-RankScan "target rank $TargetRank confirmed twice; capture may begin"
                return
            }

            $current = [int](
                $confirmationRanks |
                    Group-Object |
                    Sort-Object `
                        @{ Expression = { $_.Count }; Descending = $true },
                        @{ Expression = { [Math]::Abs([int]$_.Name - $TargetRank) }; Ascending = $true } |
                    Select-Object -First 1 -ExpandProperty Name
            )
            $State.current_selected_rank = $current
            Ensure-Property -Object $State -Name "navigation_verified_rank" -Value $current
            Save-State -State $State -Status "navigating"
            Write-RankScan "rejected unconfirmed target rank $TargetRank; settled OCR reports $($confirmationRanks -join ', '), continue from $current"
            continue
        }

        $key = if ($delta -gt 0) { "DOWN" } else { "UP" }
        $remaining = [Math]::Abs($delta)
        $segmentName = "{0}_check_{1:0000}" -f $Name, $check
        $duration = 0.0
        $count = 0
        if ($remaining -le $NavigationFeedbackThreshold) {
            $count = $remaining
            Write-RankScan "navigation correction $key x$count from verified rank $current"
        } else {
            $duration = $NavigationCheckSeconds
            if ($measuredRanksPerSecond -gt 0) {
                $duration = [Math]::Min($NavigationCheckSeconds, [Math]::Max(0.35, ($remaining / $measuredRanksPerSecond) * 0.75))
            }
            Write-RankScan ("navigation spam {0} for {1:N2}s from verified rank {2}; {3} ranks remaining" -f $key, $duration, $current, $remaining)
        }

        $before = $current
        $expected = $TargetRank
        $current = Invoke-NavigationProbe -State $State -Key $key -Name $segmentName -ExpectedRank $expected -Count $count -DurationSeconds $duration
        $invalidDirection = ($key -eq "DOWN" -and $current -lt $before) -or ($key -eq "UP" -and $current -gt $before)
        if ($invalidDirection) {
            Write-RankScan "discard implausible OCR rank $current after $key from $before; verify again without input"
            $reverified = 0
            for ($verifyAttempt = 1; $verifyAttempt -le 3; $verifyAttempt += 1) {
                $candidate = Invoke-NavigationProbe -State $State -Key $key -Name ("{0}_reverify_{1}" -f $segmentName, $verifyAttempt) -ExpectedRank $before
                $candidateInvalid = ($key -eq "DOWN" -and $candidate -lt $before) -or ($key -eq "UP" -and $candidate -gt $before)
                if (-not $candidateInvalid) {
                    $reverified = $candidate
                    break
                }
            }
            if ($reverified -lt 1) {
                throw "Navigation OCR repeatedly reported a rank moving opposite to $key. No capture screenshots were started."
            }
            $current = $reverified
        }
        $movement = [Math]::Abs($current - $before)
        if ($movement -eq 0) {
            $stagnantChecks += 1
            if ($stagnantChecks -ge 3) {
                throw "Leaderboard selection did not move during three verified navigation attempts. No capture screenshots were started."
            }
        } else {
            $stagnantChecks = 0
            if ($duration -gt 0) {
                $instantRate = $movement / $duration
                if ($measuredRanksPerSecond -le 0) {
                    $measuredRanksPerSecond = $instantRate
                } else {
                    $measuredRanksPerSecond = ($measuredRanksPerSecond * 0.65) + ($instantRate * 0.35)
                }
            }
        }

        $State.current_selected_rank = $current
        Ensure-Property -Object $State -Name "navigation_verified_rank" -Value $current
        Ensure-Property -Object $State -Name "navigation_ranks_per_second" -Value $measuredRanksPerSecond
        Save-State -State $State
        $rateText = if ($measuredRanksPerSecond -gt 0) { " ({0:N1} ranks/s measured)" -f $measuredRanksPerSecond } else { "" }
        Write-RankScan "navigation verified at rank $current$rateText"
    }

    throw "Navigation exceeded $NavigationMaxChecks verified checks before reaching rank $TargetRank. No capture screenshots were started."
}

function New-ChunkConfig {
    param(
        [Parameter(Mandatory = $true)] $State,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $CaptureDir,
        [Parameter(Mandatory = $true)][string] $ProcessedDir,
        [Parameter(Mandatory = $true)][int] $Count,
        [Parameter(Mandatory = $true)][string] $ScreenshotName,
        [bool] $AdvanceAfterScreenshot = $true,
        [int] $AdvanceCount = 1,
        [int] $RowsPerPage = 1,
        [bool] $ExtractEnabled = $false
    )

    $config = New-BaseRuntimeConfig -State $State
    Set-NestedProperty -Object $config -Parent "capture" -Name "output_dir" -Value $CaptureDir
    Set-NestedProperty -Object $config -Parent "capture" -Name "state_dir" -Value (Join-Path $CaptureDir "_state")
    Set-NestedProperty -Object $config -Parent "capture" -Name "clear_output_dir" -Value $true
    Set-NestedProperty -Object $config -Parent "capture" -Name "leaderboard_rows_per_page" -Value $RowsPerPage
    Set-NestedProperty -Object $config -Parent "extraction" -Name "enabled" -Value ($ExtractEnabled -and -not $SkipExtract)
    Set-NestedProperty -Object $config -Parent "extraction" -Name "output_dir" -Value $ProcessedDir
    Set-NestedProperty -Object $config -Parent "extraction" -Name "dump_ocr" -Value $true

    $steps = @(
        (New-WaitLeaderboardStep),
        [pscustomobject]@{
            action = "repeat"
            count = $Count
            rows_per_page = $RowsPerPage
            steps = @(
                [pscustomobject]@{ action = "screenshot"; name = $ScreenshotName }
            )
        }
    )
    if ($AdvanceAfterScreenshot) {
        $advanceStep = $null
        if ($RowDelayMs -gt 0) {
            $advanceStep = New-KeyStep -Key "DOWN" -Count $AdvanceCount -DelayMs $RowDelayMs -SettleAfterMs $RowSettleMs
        } else {
            $advanceStep = New-KeyStep -Key "DOWN" -Count $AdvanceCount -DelayMs $RowDelayMs -Burst -BurstSize $AdvanceCount -SettleAfterMs $RowSettleMs
        }
        Ensure-Property -Object $advanceStep -Name "skip_on_last_repeat" -Value $true
        $steps[1].steps += $advanceStep
    }
    $config.steps = $steps
    return Write-RuntimeConfig -ConfigObject $config -Name $Name -RuntimeDir $State.runtime_dir
}

function Test-ScreenshotsSimilar {
    param(
        [Parameter(Mandatory = $true)][string] $FirstPath,
        [Parameter(Mandatory = $true)][string] $SecondPath
    )

    if (-not (Test-Path -LiteralPath $FirstPath) -or -not (Test-Path -LiteralPath $SecondPath)) {
        return $false
    }

    try {
        $firstHash = (Get-FileHash -LiteralPath $FirstPath -Algorithm SHA256).Hash
        $secondHash = (Get-FileHash -LiteralPath $SecondPath -Algorithm SHA256).Hash
        if ($firstHash -eq $secondHash) {
            return $true
        }
    } catch {
        # Fall back to sampled pixels below.
    }

    Add-Type -AssemblyName System.Drawing
    $first = $null
    $second = $null
    try {
        $first = [System.Drawing.Bitmap]::new($FirstPath)
        $second = [System.Drawing.Bitmap]::new($SecondPath)
        if ($first.Width -ne $second.Width -or $first.Height -ne $second.Height) {
            return $false
        }

        $left = [int][Math]::Round($first.Width * 0.04)
        $right = [int][Math]::Round($first.Width * 0.96)
        $top = [int][Math]::Round($first.Height * 0.16)
        $bottom = [int][Math]::Round($first.Height * 0.84)
        $sampleX = 180
        $sampleY = 100
        $totalDiff = 0.0
        $highDiff = 0
        $samples = 0

        for ($yi = 0; $yi -lt $sampleY; $yi += 1) {
            $y = [int][Math]::Round($top + (($bottom - $top) * (($yi + 0.5) / $sampleY)))
            $y = [Math]::Min($first.Height - 1, [Math]::Max(0, $y))
            for ($xi = 0; $xi -lt $sampleX; $xi += 1) {
                $x = [int][Math]::Round($left + (($right - $left) * (($xi + 0.5) / $sampleX)))
                $x = [Math]::Min($first.Width - 1, [Math]::Max(0, $x))
                $a = $first.GetPixel($x, $y)
                $b = $second.GetPixel($x, $y)
                $grayA = (0.299 * $a.R) + (0.587 * $a.G) + (0.114 * $a.B)
                $grayB = (0.299 * $b.R) + (0.587 * $b.G) + (0.114 * $b.B)
                $diff = [Math]::Abs($grayA - $grayB)
                $totalDiff += $diff
                if ($diff -ge 18.0) {
                    $highDiff += 1
                }
                $samples += 1
            }
        }

        if ($samples -lt 1) {
            return $false
        }
        $avgDiff = $totalDiff / $samples
        $highDiffRatio = $highDiff / [double]$samples
        return ($avgDiff -le 1.25 -and $highDiffRatio -le 0.01)
    } catch {
        Write-RankScan "warning: screenshot stability comparison failed: $($_.Exception.Message)"
        return $false
    } finally {
        if ($null -ne $first) {
            $first.Dispose()
        }
        if ($null -ne $second) {
            $second.Dispose()
        }
    }
}

function Get-LeaderboardScreenshots {
    param([Parameter(Mandatory = $true)][string] $CaptureDir)

    if (-not (Test-Path -LiteralPath $CaptureDir)) {
        return @()
    }
    return @(Get-ChildItem -LiteralPath $CaptureDir -File -Filter "leaderboard*.png" |
        Sort-Object Name)
}

function Find-FirstStableScreenshotIndex {
    param([Parameter(Mandatory = $true)][string] $CaptureDir)

    $files = @(Get-LeaderboardScreenshots -CaptureDir $CaptureDir)
    for ($i = 1; $i -lt $files.Count; $i += 1) {
        if (Test-ScreenshotsSimilar -FirstPath $files[$i - 1].FullName -SecondPath $files[$i].FullName) {
            return $i
        }
    }
    return -1
}

function Disable-DuplicateTailScreenshots {
    param(
        [Parameter(Mandatory = $true)][string] $CaptureDir,
        [Parameter(Mandatory = $true)][int] $FirstDuplicateIndex
    )

    $files = @(Get-LeaderboardScreenshots -CaptureDir $CaptureDir)
    if ($FirstDuplicateIndex -lt 0 -or $FirstDuplicateIndex -ge $files.Count) {
        return
    }

    $ignoredDir = Join-Path $CaptureDir "_end_duplicates"
    New-Item -ItemType Directory -Force -Path $ignoredDir | Out-Null
    for ($i = $FirstDuplicateIndex; $i -lt $files.Count; $i += 1) {
        Move-Item -LiteralPath $files[$i].FullName -Destination (Join-Path $ignoredDir $files[$i].Name) -Force
    }
    Write-RankScan "end detected: moved $($files.Count - $FirstDuplicateIndex) duplicate tail screenshots to $ignoredDir"
}

function Invoke-RankChunk {
    param(
        [Parameter(Mandatory = $true)] $State,
        [Parameter(Mandatory = $true)][int] $Start,
        [Parameter(Mandatory = $true)][int] $Count,
        [switch] $UntilEndMode
    )

    if ($RowScan) {
        $screenshotCount = $Count
        $rowsPerShot = 1
        $advanceCount = 1
        $targetSelectedRank = $Start
    } else {
        $rowsPerShot = [Math]::Max(1, $RowsPerScreenshot)
        $screenshotCount = [int][Math]::Ceiling($Count / [double]$rowsPerShot)
        $advanceCount = $rowsPerShot
        $targetSelectedRank = $Start + $rowsPerShot - 1
    }
    if ($UntilEndMode) {
        $screenshotCount += 1
    }

    Invoke-MoveToRank -State $State -TargetRank $targetSelectedRank -Name ("move_to_chunk_{0:000000}" -f $Start)

    $coveredCount = $screenshotCount * $rowsPerShot
    $coveredEnd = $Start + $coveredCount - 1
    if (([int]$State.total_ranks) -gt 0) {
        $coveredEnd = [Math]::Min([int]$State.total_ranks, $coveredEnd)
    }
    $name = "chunk_{0:000000}_{1:000}" -f $Start, $screenshotCount
    $captureDir = Join-Path $State.capture_chunks_dir $name
    $processedDir = Join-Path $State.processed_chunks_dir $name
    $screenshotName = "leaderboard_rank_{0:000000}_{{index:000}}.png" -f $Start
    $rangeText = if ($UntilEndMode) { "$Start+ until stable/end" } else { "$Start-$coveredEnd" }
    Write-RankScan "capture ranks $rangeText ($screenshotCount screenshots, $rowsPerShot rows/screenshot)"
    $configPath = New-ChunkConfig -State $State -Name $name -CaptureDir $captureDir -ProcessedDir $processedDir -Count $screenshotCount -ScreenshotName $screenshotName -AdvanceAfterScreenshot $true -AdvanceCount $advanceCount -RowsPerPage $rowsPerShot -ExtractEnabled $false
    Invoke-CaptureAutomation -ConfigPath $configPath
    $plannedCoveredEnd = $coveredEnd
    $verifiedSelectedRank = Invoke-NavigationProbe `
        -State $State `
        -Key "DOWN" `
        -Name ("verify_chunk_end_{0:000000}" -f $Start) `
        -ExpectedRank $coveredEnd
    if ($verifiedSelectedRank -gt $plannedCoveredEnd) {
        throw "Verified chunk end rank $verifiedSelectedRank exceeds the maximum possible end $plannedCoveredEnd. The chunk start/navigation was not trustworthy, so the screenshots were not accepted."
    }
    $verifiedCoveredEnd = $verifiedSelectedRank
    if (([int]$State.total_ranks) -gt 0) {
        $verifiedCoveredEnd = [Math]::Min([int]$State.total_ranks, $verifiedCoveredEnd)
    }
    if ($verifiedCoveredEnd -lt $Start) {
        throw "Verified chunk end rank $verifiedCoveredEnd is before chunk start $Start. The chunk is not safe to continue."
    }
    if ($verifiedCoveredEnd -lt $coveredEnd) {
        Write-RankScan "Forza consumed fewer row inputs than requested; verified chunk coverage is $Start-$verifiedCoveredEnd instead of $Start-$coveredEnd"
    }
    $coveredEnd = $verifiedCoveredEnd
    $coveredCount = $coveredEnd - $Start + 1

    $endDetected = $false
    $actualScreenshotCount = $screenshotCount
    if ($UntilEndMode) {
        $stableIndex = Find-FirstStableScreenshotIndex -CaptureDir $captureDir
        if ($stableIndex -ge 1) {
            $endDetected = $true
            $actualScreenshotCount = $stableIndex
            Disable-DuplicateTailScreenshots -CaptureDir $captureDir -FirstDuplicateIndex $stableIndex
            $coveredCount = $actualScreenshotCount * $rowsPerShot
            $coveredEnd = $Start + $coveredCount - 1
            Ensure-Property -Object $State -Name "total_ranks_unknown" -Value $false
            Ensure-Property -Object $State -Name "total_ranks_mode" -Value "detected_until_end"
            Ensure-Property -Object $State -Name "coverage_total_ranks" -Value 0
            Ensure-Property -Object $State -Name "end_detected_at" -Value ((Get-Date).ToUniversalTime().ToString("o"))
            $State.total_ranks = $coveredEnd
            Write-RankScan "leaderboard end detected after repeated screen; estimated scanned end rank $coveredEnd"
        }
    }

    $State.current_selected_rank = $verifiedSelectedRank
    $State.next_rank = $coveredEnd + 1
    $State.max_scanned_rank = [Math]::Max([int]$State.max_scanned_rank, $coveredEnd)
    [pscustomobject]@{
        completed_at = (Get-Date).ToUniversalTime().ToString("o")
        requested_screenshot_count = $screenshotCount
        actual_screenshot_count = $actualScreenshotCount
        rows_per_screenshot = $rowsPerShot
        start_rank = $Start
        covered_end = $coveredEnd
        end_detected = $endDetected
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $captureDir ".capture_complete.json") -Encoding UTF8
    Start-ExtractorProcess -State $State -Name $name -CaptureDir $captureDir -ProcessedDir $processedDir -OcrMode "fast"
    return [pscustomobject]@{
        end_detected = $endDetected
        covered_end = $coveredEnd
        screenshot_count = $actualScreenshotCount
        capture_dir = $captureDir
        processed_dir = $processedDir
    }
}

function Invoke-RetryRank {
    param(
        [Parameter(Mandatory = $true)] $State,
        [Parameter(Mandatory = $true)][int] $Rank,
        [Parameter(Mandatory = $true)][int] $Pass
    )

    Invoke-MoveToRank -State $State -TargetRank $Rank -Name ("retry_move_rank_{0:000000}_pass_{1:00}" -f $Rank, $Pass)
    $name = "retry_rank_{0:000000}_pass_{1:00}" -f $Rank, $Pass
    $captureDir = Join-Path $State.capture_chunks_dir $name
    $processedDir = Join-Path $State.processed_chunks_dir $name
    $screenshotName = "leaderboard_retry_rank_{0:000000}_pass_{1:00}.png" -f $Rank, $Pass
    Write-RankScan "retry screenshot for missing rank $Rank (pass $Pass)"
    $configPath = New-ChunkConfig -State $State -Name $name -CaptureDir $captureDir -ProcessedDir $processedDir -Count 1 -ScreenshotName $screenshotName -AdvanceAfterScreenshot $false -AdvanceCount 1 -RowsPerPage 1 -ExtractEnabled $false
    Invoke-CaptureAutomation -ConfigPath $configPath
    $State.current_selected_rank = $Rank
    Start-ExtractorProcess -State $State -Name $name -CaptureDir $captureDir -ProcessedDir $processedDir -OcrMode "precise"
}

function Invoke-MergeReport {
    param([Parameter(Mandatory = $true)] $State)

    if ($DryRun -or $SkipExtract) {
        return [pscustomobject]@{
            row_count = 0
            complete_rank_count = 0
            total_ranks = $State.total_ranks
            max_scanned_rank = $State.max_scanned_rank
            missing_ranks = @()
            incomplete_ranks = @()
        }
    }

    $mergeTotalRanks = ""
    $mergeMaxScannedRank = [string]$State.max_scanned_rank
    if (($State.PSObject.Properties.Name -contains "coverage_total_ranks") -and ([int]$State.coverage_total_ranks -gt 0)) {
        $mergeTotalRanks = [string][int]$State.coverage_total_ranks
    } elseif (($State.PSObject.Properties.Name -contains "total_ranks_mode") -and ([string]$State.total_ranks_mode -match "until_end")) {
        $mergeMaxScannedRank = ""
    }

    $script = Resolve-WorkspacePath "scripts/merge_rank_scan_outputs.py"
    $args = @(
        $script,
        "--input-root", $State.processed_chunks_dir,
        "--output-dir", $State.combined_output_dir,
        "--parquet-path", $State.combined_parquet_path,
        "--total-ranks", $mergeTotalRanks,
        "--max-scanned-rank", $mergeMaxScannedRank
    )
    $output = & python @args
    if ($LASTEXITCODE -ne 0) {
        throw "merge_rank_scan_outputs.py failed with exit code $LASTEXITCODE"
    }
    return ($output | Select-Object -Last 1 | ConvertFrom-Json)
}

function Format-Duration {
    param([double] $Seconds)

    if ($Seconds -lt 0 -or [double]::IsNaN($Seconds) -or [double]::IsInfinity($Seconds)) {
        return "unknown"
    }
    $span = [TimeSpan]::FromSeconds([Math]::Round($Seconds))
    if ($span.TotalHours -ge 1) {
        return "{0}h {1}m" -f [int]$span.TotalHours, $span.Minutes
    }
    if ($span.TotalMinutes -ge 1) {
        return "{0}m {1}s" -f [int]$span.TotalMinutes, $span.Seconds
    }
    return "{0}s" -f $span.Seconds
}

function Write-ProgressEta {
    param(
        [Parameter(Mandatory = $true)] $State,
        [Parameter(Mandatory = $true)] $Report,
        [Parameter(Mandatory = $true)][double] $ElapsedSeconds
    )

    $complete = [int]($Report.complete_rank_count)
    $total = [int]$State.total_ranks
    if (($State.PSObject.Properties.Name -contains "coverage_total_ranks") -and ([int]$State.coverage_total_ranks -gt 0)) {
        $total = [int]$State.coverage_total_ranks
    } elseif ($null -ne $Report.total_ranks -and ([string]$Report.total_ranks -as [int]) -gt 0) {
        $total = [int]$Report.total_ranks
    }
    $rpm = 0.0
    if ($ElapsedSeconds -gt 0) {
        $rpm = $complete / ($ElapsedSeconds / 60.0)
    }
    $eta = "unknown"
    if ($rpm -gt 0 -and $total -gt 0) {
        $eta = Format-Duration (([Math]::Max(0, $total - $complete) / $rpm) * 60.0)
    }
    $missing = @($Report.missing_ranks).Count + @($Report.incomplete_ranks).Count
    $totalText = if ($total -gt 0) { [string]$total } else { "?" }
    Write-RankScan ("coverage {0}/{1} complete ranks, {2:N1} ranks/min, ETA {3}, current scanned-to rank {4}, missing/incomplete in scanned window {5}" -f $complete, $totalText, $rpm, $eta, $State.max_scanned_rank, $missing)
}

function Write-CaptureEta {
    param(
        [Parameter(Mandatory = $true)] $State,
        [Parameter(Mandatory = $true)][double] $ElapsedSeconds
    )

    $scanned = [Math]::Max(0, [int]$State.max_scanned_rank - [int]$State.start_rank + 1)
    $total = [int]$State.total_ranks
    $rpm = 0.0
    if ($ElapsedSeconds -gt 0) {
        $rpm = $scanned / ($ElapsedSeconds / 60.0)
    }
    $eta = "unknown"
    if ($rpm -gt 0 -and $total -gt 0) {
        $eta = Format-Duration (([Math]::Max(0, $total - [int]$State.max_scanned_rank) / $rpm) * 60.0)
    } elseif (($State.PSObject.Properties.Name -contains "total_ranks_unknown") -and [bool]$State.total_ranks_unknown) {
        $eta = "until repeated screen"
    }
    $totalText = if ($total -gt 0) { [string]$total } else { "?" }
    Write-RankScan ("capture progress {0}/{1} ranks scanned-window, {2:N1} ranks/min, capture ETA {3}, extractor backlog {4}" -f $scanned, $totalText, $rpm, $eta, $script:extractorProcesses.Count)
}

function Save-State {
    param(
        [Parameter(Mandatory = $true)] $State,
        [string] $Status = ""
    )

    if (-not [string]::IsNullOrWhiteSpace($Status)) {
        $State.status = $Status
    }
    $State.updated_at = (Get-Date).ToUniversalTime().ToString("o")
    New-Item -ItemType Directory -Force -Path ([System.IO.Path]::GetDirectoryName($State.state_path)) | Out-Null
    $State | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $State.state_path -Encoding UTF8
}

function New-ScanState {
    if (-not [string]::IsNullOrWhiteSpace($ResumeFrom)) {
        $path = Resolve-WorkspacePath $ResumeFrom
        if (-not (Test-Path -LiteralPath $path)) {
            throw "Resume state not found: $path"
        }
        $state = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        Ensure-Property -Object $state -Name "resumed_at" -Value ((Get-Date).ToUniversalTime().ToString("o"))
        return $state
    }

    if ([string]::IsNullOrWhiteSpace($Track)) {
        throw "-Track is required for a new rank scan."
    }
    if ($StartRank -lt 1) {
        throw "-StartRank must be >= 1."
    }
    if ($ChunkSize -lt 1) {
        throw "-ChunkSize must be >= 1."
    }
    if ($IdleChunkSize -lt 1) {
        throw "-IdleChunkSize must be >= 1."
    }
    if ($IdleNavigationBatchSize -lt 1) {
        throw "-IdleNavigationBatchSize must be >= 1."
    }
    if ([string]::IsNullOrWhiteSpace($RunId)) {
        $safeTrack = ConvertTo-SafeName $Track
        $safeClass = ConvertTo-SafeName $PiClass
        $suffix = ([guid]::NewGuid().ToString("N")).Substring(0, 8)
        $RunId = "{0}_{1}_{2}_{3}" -f (Get-Date -Format "yyyyMMdd_HHmmss_fff"), $safeTrack, $safeClass, $suffix
    }

    $runRoot = Resolve-WorkspacePath (Join-Path $OutputRoot $RunId)
    $processedRunRoot = Resolve-WorkspacePath (Join-Path $ProcessedRoot $RunId)
    return [pscustomobject]@{
        run_id = $RunId
        status = "active"
        created_at = (Get-Date).ToUniversalTime().ToString("o")
        updated_at = (Get-Date).ToUniversalTime().ToString("o")
        route_config = $RouteConfig
        base_config = $BaseConfig
        track = $Track
        pi_class = $PiClass
        event_type = $EventType
        rivals_mode = $RivalsMode
        total_ranks = 0
        total_ranks_unknown = $false
        total_ranks_mode = ""
        coverage_total_ranks = 0
        start_rank = $StartRank
        next_rank = $StartRank
        current_selected_rank = $CurrentRank
        max_scanned_rank = ($StartRank - 1)
        navigation_target_rank = 0
        navigation_verified_rank = $CurrentRank
        navigation_ranks_per_second = 0.0
        capture_baseline_screenshots = 0
        chunk_size = $ChunkSize
        rows_per_screenshot = $RowsPerScreenshot
        row_delay_ms = $RowDelayMs
        row_settle_ms = $RowSettleMs
        navigation_delay_ms = $NavigationDelayMs
        navigation_burst_size = $NavigationBurstSize
        navigation_settle_ms = $NavigationSettleMs
        navigation_check_seconds = $NavigationCheckSeconds
        navigation_spam_burst_size = $NavigationSpamBurstSize
        navigation_spam_pause_ms = $NavigationSpamPauseMs
        navigation_feedback_threshold = $NavigationFeedbackThreshold
        navigation_max_checks = $NavigationMaxChecks
        scrollbar_jump_threshold = $ScrollbarJumpThreshold
        use_scrollbar_jump = [bool]$UseScrollbarJump
        no_scrollbar_jump = [bool]$NoScrollbarJump
        row_scan = [bool]$RowScan
        idle_only = [bool]$IdleOnly
        idle_seconds = $IdleSeconds
        idle_poll_seconds = $IdlePollSeconds
        idle_chunk_size = $IdleChunkSize
        idle_navigation_batch_size = $IdleNavigationBatchSize
        extract_parallel_jobs_requested = $ExtractParallelJobs
        extract_parallel_jobs = 0
        ocr_backend = $OcrBackend
        ocr_threads_per_process_requested = $OcrThreadsPerProcess
        ocr_threads_per_process = 0
        system_compute = $null
        max_retry_passes = $MaxRetryPasses
        elapsed_seconds = 0.0
        state_path = (Join-Path $runRoot "state.json")
        run_root = $runRoot
        runtime_dir = (Join-Path $runRoot "runtime")
        capture_chunks_dir = (Join-Path $runRoot "chunks")
        processed_run_root = $processedRunRoot
        processed_chunks_dir = (Join-Path $processedRunRoot "chunks")
        combined_output_dir = (Join-Path $processedRunRoot "combined")
        combined_parquet_path = (Join-Path $processedRunRoot "leaderboard_entries.parquet")
    }
}

$state = New-ScanState
if (-not [string]::IsNullOrWhiteSpace($ResumeFrom)) {
    if ($state.PSObject.Properties.Name -contains "rows_per_screenshot" -and [int]$state.rows_per_screenshot -gt 0 -and -not $PSBoundParameters.ContainsKey("RowsPerScreenshot")) {
        $RowsPerScreenshot = [int]$state.rows_per_screenshot
    }
    if ($state.PSObject.Properties.Name -contains "row_scan") {
        $RowScan = [bool]$state.row_scan
    }
    if ($state.PSObject.Properties.Name -contains "chunk_size" -and [int]$state.chunk_size -gt 0 -and -not $PSBoundParameters.ContainsKey("ChunkSize")) {
        $ChunkSize = [int]$state.chunk_size
    }
    if ($state.PSObject.Properties.Name -contains "row_delay_ms" -and [int]$state.row_delay_ms -ge 0 -and -not $PSBoundParameters.ContainsKey("RowDelayMs")) {
        $RowDelayMs = [int]$state.row_delay_ms
    }
    if ($state.PSObject.Properties.Name -contains "row_settle_ms" -and [int]$state.row_settle_ms -ge 0 -and -not $PSBoundParameters.ContainsKey("RowSettleMs")) {
        $RowSettleMs = [int]$state.row_settle_ms
    }
    if ($state.PSObject.Properties.Name -contains "navigation_delay_ms" -and [int]$state.navigation_delay_ms -ge 0 -and -not $PSBoundParameters.ContainsKey("NavigationDelayMs")) {
        $NavigationDelayMs = [int]$state.navigation_delay_ms
    }
    if ($state.PSObject.Properties.Name -contains "navigation_burst_size" -and [int]$state.navigation_burst_size -gt 0 -and -not $PSBoundParameters.ContainsKey("NavigationBurstSize")) {
        $NavigationBurstSize = [int]$state.navigation_burst_size
    }
    if ($state.PSObject.Properties.Name -contains "navigation_settle_ms" -and [int]$state.navigation_settle_ms -ge 0 -and -not $PSBoundParameters.ContainsKey("NavigationSettleMs")) {
        $NavigationSettleMs = [int]$state.navigation_settle_ms
    }
    if ($state.PSObject.Properties.Name -contains "navigation_check_seconds" -and [double]$state.navigation_check_seconds -gt 0 -and -not $PSBoundParameters.ContainsKey("NavigationCheckSeconds")) {
        $NavigationCheckSeconds = [double]$state.navigation_check_seconds
    }
    if ($state.PSObject.Properties.Name -contains "navigation_spam_burst_size" -and [int]$state.navigation_spam_burst_size -gt 0 -and -not $PSBoundParameters.ContainsKey("NavigationSpamBurstSize")) {
        $NavigationSpamBurstSize = [int]$state.navigation_spam_burst_size
    }
    if ($state.PSObject.Properties.Name -contains "navigation_spam_pause_ms" -and [int]$state.navigation_spam_pause_ms -ge 0 -and -not $PSBoundParameters.ContainsKey("NavigationSpamPauseMs")) {
        $NavigationSpamPauseMs = [int]$state.navigation_spam_pause_ms
    }
    if ($state.PSObject.Properties.Name -contains "navigation_feedback_threshold" -and [int]$state.navigation_feedback_threshold -gt 0 -and -not $PSBoundParameters.ContainsKey("NavigationFeedbackThreshold")) {
        $NavigationFeedbackThreshold = [int]$state.navigation_feedback_threshold
    }
    if ($state.PSObject.Properties.Name -contains "navigation_max_checks" -and [int]$state.navigation_max_checks -gt 0 -and -not $PSBoundParameters.ContainsKey("NavigationMaxChecks")) {
        $NavigationMaxChecks = [int]$state.navigation_max_checks
    }
    if ($state.PSObject.Properties.Name -contains "scrollbar_jump_threshold" -and [int]$state.scrollbar_jump_threshold -gt 0 -and -not $PSBoundParameters.ContainsKey("ScrollbarJumpThreshold")) {
        $ScrollbarJumpThreshold = [int]$state.scrollbar_jump_threshold
    }
    if ($state.PSObject.Properties.Name -contains "use_scrollbar_jump" -and -not $PSBoundParameters.ContainsKey("UseScrollbarJump")) {
        $UseScrollbarJump = [bool]$state.use_scrollbar_jump
    }
    if ($state.PSObject.Properties.Name -contains "no_scrollbar_jump" -and -not $PSBoundParameters.ContainsKey("NoScrollbarJump")) {
        $NoScrollbarJump = [bool]$state.no_scrollbar_jump
    }
    if ($state.PSObject.Properties.Name -contains "idle_only" -and -not $PSBoundParameters.ContainsKey("IdleOnly")) {
        $IdleOnly = [bool]$state.idle_only
    }
    if ($state.PSObject.Properties.Name -contains "idle_seconds" -and [int]$state.idle_seconds -gt 0 -and -not $PSBoundParameters.ContainsKey("IdleSeconds")) {
        $IdleSeconds = [int]$state.idle_seconds
    }
    if ($state.PSObject.Properties.Name -contains "idle_poll_seconds" -and [double]$state.idle_poll_seconds -gt 0 -and -not $PSBoundParameters.ContainsKey("IdlePollSeconds")) {
        $IdlePollSeconds = [double]$state.idle_poll_seconds
    }
    if ($state.PSObject.Properties.Name -contains "idle_chunk_size" -and [int]$state.idle_chunk_size -gt 0 -and -not $PSBoundParameters.ContainsKey("IdleChunkSize")) {
        $IdleChunkSize = [int]$state.idle_chunk_size
    }
    if ($state.PSObject.Properties.Name -contains "idle_navigation_batch_size" -and [int]$state.idle_navigation_batch_size -gt 0 -and -not $PSBoundParameters.ContainsKey("IdleNavigationBatchSize")) {
        $IdleNavigationBatchSize = [int]$state.idle_navigation_batch_size
    }
    if ($state.PSObject.Properties.Name -contains "ocr_backend" -and -not [string]::IsNullOrWhiteSpace([string]$state.ocr_backend) -and -not $PSBoundParameters.ContainsKey("OcrBackend")) {
        $OcrBackend = [string]$state.ocr_backend
    }
    if ($state.PSObject.Properties.Name -contains "extract_parallel_jobs_requested" -and -not [string]::IsNullOrWhiteSpace([string]$state.extract_parallel_jobs_requested) -and -not $PSBoundParameters.ContainsKey("ExtractParallelJobs")) {
        $ExtractParallelJobs = [string]$state.extract_parallel_jobs_requested
    } elseif ($state.PSObject.Properties.Name -contains "extract_parallel_jobs" -and (ConvertTo-PositiveIntOrNull $state.extract_parallel_jobs) -and -not $PSBoundParameters.ContainsKey("ExtractParallelJobs")) {
        $ExtractParallelJobs = [int]$state.extract_parallel_jobs
    }
    if ($state.PSObject.Properties.Name -contains "ocr_threads_per_process_requested" -and -not [string]::IsNullOrWhiteSpace([string]$state.ocr_threads_per_process_requested) -and -not $PSBoundParameters.ContainsKey("OcrThreadsPerProcess")) {
        $OcrThreadsPerProcess = [string]$state.ocr_threads_per_process_requested
    } elseif ($state.PSObject.Properties.Name -contains "ocr_threads_per_process" -and (ConvertTo-PositiveIntOrNull $state.ocr_threads_per_process) -and -not $PSBoundParameters.ContainsKey("OcrThreadsPerProcess")) {
        $OcrThreadsPerProcess = [int]$state.ocr_threads_per_process
    }
}

if ($IdleChunkSize -lt 1) {
    throw "-IdleChunkSize must be >= 1."
}
if ($IdleNavigationBatchSize -lt 1) {
    throw "-IdleNavigationBatchSize must be >= 1."
}
if ($IdleSeconds -lt 1) {
    throw "-IdleSeconds must be >= 1."
}
if ($IdlePollSeconds -le 0) {
    throw "-IdlePollSeconds must be > 0."
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
if ($IdleOnly -and -not $PSBoundParameters.ContainsKey("ChunkSize") -and $ChunkSize -gt $IdleChunkSize) {
    $ChunkSize = $IdleChunkSize
    Write-RankScan "idle mode chunk size: $ChunkSize ranks per foreground burst"
}

Ensure-Property -Object $state -Name "chunk_size" -Value $ChunkSize
Ensure-Property -Object $state -Name "rows_per_screenshot" -Value $RowsPerScreenshot
Ensure-Property -Object $state -Name "row_delay_ms" -Value $RowDelayMs
Ensure-Property -Object $state -Name "row_settle_ms" -Value $RowSettleMs
Ensure-Property -Object $state -Name "navigation_delay_ms" -Value $NavigationDelayMs
Ensure-Property -Object $state -Name "navigation_burst_size" -Value $NavigationBurstSize
Ensure-Property -Object $state -Name "navigation_settle_ms" -Value $NavigationSettleMs
Ensure-Property -Object $state -Name "navigation_check_seconds" -Value $NavigationCheckSeconds
Ensure-Property -Object $state -Name "navigation_spam_burst_size" -Value $NavigationSpamBurstSize
Ensure-Property -Object $state -Name "navigation_spam_pause_ms" -Value $NavigationSpamPauseMs
Ensure-Property -Object $state -Name "navigation_feedback_threshold" -Value $NavigationFeedbackThreshold
Ensure-Property -Object $state -Name "navigation_max_checks" -Value $NavigationMaxChecks
Ensure-Property -Object $state -Name "scrollbar_jump_threshold" -Value $ScrollbarJumpThreshold
Ensure-Property -Object $state -Name "use_scrollbar_jump" -Value ([bool]$UseScrollbarJump)
Ensure-Property -Object $state -Name "no_scrollbar_jump" -Value ([bool]$NoScrollbarJump)
Ensure-Property -Object $state -Name "row_scan" -Value ([bool]$RowScan)
Ensure-Property -Object $state -Name "idle_only" -Value ([bool]$IdleOnly)
Ensure-Property -Object $state -Name "idle_seconds" -Value $IdleSeconds
Ensure-Property -Object $state -Name "idle_poll_seconds" -Value $IdlePollSeconds
Ensure-Property -Object $state -Name "idle_chunk_size" -Value $IdleChunkSize
Ensure-Property -Object $state -Name "idle_navigation_batch_size" -Value $IdleNavigationBatchSize
if ($IdleOnly) {
    Write-RankScan ("idle mode enabled: waits for {0}s idle, restores previous window after each automation burst, navigation batch {1}" -f $IdleSeconds, $IdleNavigationBatchSize)
}

$requestedExtractParallelJobs = [string]$ExtractParallelJobs
$requestedOcrThreadsPerProcess = [string]$OcrThreadsPerProcess
$computeProfile = Get-SystemComputeProfile
$jobsResolution = Resolve-ExtractParallelJobs -Requested $requestedExtractParallelJobs -Backend $OcrBackend -ComputeProfile $computeProfile
$resolvedExtractParallelJobs = [int]$jobsResolution.value
$threadsResolution = Resolve-OcrThreadsPerProcess -Requested $requestedOcrThreadsPerProcess -Backend $OcrBackend -ResolvedJobs $resolvedExtractParallelJobs -ComputeProfile $computeProfile
$resolvedOcrThreadsPerProcess = [int]$threadsResolution.value
$ExtractParallelJobs = [string]$resolvedExtractParallelJobs

$stateExtractParallelJobsRequested = $requestedExtractParallelJobs
if ($jobsResolution.source -eq "auto") {
    $stateExtractParallelJobsRequested = "Auto"
}
Ensure-Property -Object $state -Name "extract_parallel_jobs_requested" -Value $stateExtractParallelJobsRequested
Ensure-Property -Object $state -Name "extract_parallel_jobs" -Value $resolvedExtractParallelJobs
Ensure-Property -Object $state -Name "extract_parallel_jobs_source" -Value ([string]$jobsResolution.source)
Ensure-Property -Object $state -Name "extract_parallel_jobs_reason" -Value ([string]$jobsResolution.reason)
Ensure-Property -Object $state -Name "ocr_backend" -Value $OcrBackend
$stateOcrThreadsPerProcessRequested = $requestedOcrThreadsPerProcess
if ($threadsResolution.source -eq "auto") {
    $stateOcrThreadsPerProcessRequested = "Auto"
}
Ensure-Property -Object $state -Name "ocr_threads_per_process_requested" -Value $stateOcrThreadsPerProcessRequested
Ensure-Property -Object $state -Name "ocr_threads_per_process" -Value $resolvedOcrThreadsPerProcess
Ensure-Property -Object $state -Name "ocr_threads_per_process_source" -Value ([string]$threadsResolution.source)
Ensure-Property -Object $state -Name "system_compute" -Value $computeProfile
Write-RankScan ("system compute: {0} physical cores, {1} logical threads, {2} reserved, {3} usable" -f $computeProfile.physical_cores, $computeProfile.logical_threads, $computeProfile.reserved_threads, $computeProfile.usable_threads)
Write-RankScan ("extractor scheduling: backend {0}, jobs {1} ({2}: {3}), threads/process {4} ({5})" -f $OcrBackend, $resolvedExtractParallelJobs, $jobsResolution.source, $jobsResolution.reason, $resolvedOcrThreadsPerProcess, $threadsResolution.source)
Write-RankScan ("input speed: row burst {0} keys + {1}ms settle; navigation spam checks every {2:N1}s (burst {3}, pause {4}ms), exact correction burst {5}" -f $RowsPerScreenshot, $RowSettleMs, $NavigationCheckSeconds, $NavigationSpamBurstSize, $NavigationSpamPauseMs, $NavigationBurstSize)
$sessionStopwatch = [System.Diagnostics.Stopwatch]::StartNew()

try {
    New-Item -ItemType Directory -Force -Path $state.run_root, $state.runtime_dir, $state.capture_chunks_dir, $state.processed_chunks_dir, $state.combined_output_dir | Out-Null
    $existingScreenshotCount = @(Get-ChildItem -LiteralPath $state.capture_chunks_dir -Recurse -File -Filter "leaderboard*.png" -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '[\\/]_end_duplicates[\\/]' }).Count
    Ensure-Property -Object $state -Name "capture_baseline_screenshots" -Value $existingScreenshotCount
    Save-State -State $state

    Invoke-InitialRoute -State $state
    if (-not $AlreadyAtLeaderboard) {
        $state.current_selected_rank = 1
    } elseif (-not [string]::IsNullOrWhiteSpace($ResumeFrom) -and $PSBoundParameters.ContainsKey("CurrentRank")) {
        $state.current_selected_rank = $CurrentRank
    } elseif ([string]::IsNullOrWhiteSpace($ResumeFrom)) {
        $state.current_selected_rank = $CurrentRank
    }
    $state.total_ranks = Resolve-TotalRanks -State $state
    Save-State -State $state
    $totalRankText = if (($state.PSObject.Properties.Name -contains "total_ranks_unknown") -and [bool]$state.total_ranks_unknown) { "unknown; scanning until a repeated screen is detected" } else { [string]$state.total_ranks }
    Write-RankScan "total ranks for current class: $totalRankText"

    $scanUntilEnd = (($state.PSObject.Properties.Name -contains "total_ranks_unknown") -and [bool]$state.total_ranks_unknown)
    while ($scanUntilEnd -or ([int]$state.next_rank -le [int]$state.total_ranks)) {
        $chunkStart = [int]$state.next_rank
        if ($scanUntilEnd) {
            $count = [int]$state.chunk_size
        } else {
            $remaining = [int]$state.total_ranks - $chunkStart + 1
            $count = [Math]::Min([int]$state.chunk_size, $remaining)
        }

        $chunkResult = Invoke-RankChunk -State $state -Start $chunkStart -Count $count -UntilEndMode:$scanUntilEnd
        $state.elapsed_seconds = [double]$state.elapsed_seconds + $sessionStopwatch.Elapsed.TotalSeconds
        $sessionStopwatch.Restart()
        Save-State -State $state
        Write-CaptureEta -State $state -ElapsedSeconds ([double]$state.elapsed_seconds)
        Reap-ExtractorProcesses
        if ($chunkResult.end_detected) {
            Save-State -State $state
            break
        }
        $scanUntilEnd = (($state.PSObject.Properties.Name -contains "total_ranks_unknown") -and [bool]$state.total_ranks_unknown)
    }

    Write-RankScan "capture pass complete; waiting for extractor backlog"
    Wait-AllExtractors

    $finalReport = Invoke-MergeReport -State $state
    Write-ProgressEta -State $state -Report $finalReport -ElapsedSeconds ([double]$state.elapsed_seconds)
    $finalMissing = @(@($finalReport.missing_ranks) + @($finalReport.incomplete_ranks) + @($finalReport.duplicate_content_ranks) |
        ForEach-Object { [int]$_ } |
        Where-Object { $_ -ge [int]$state.start_rank -and $_ -le [int]$state.max_scanned_rank } |
        Sort-Object -Unique)

    for ($pass = 1; $pass -le [int]$state.max_retry_passes -and $finalMissing.Count -gt 0; $pass += 1) {
        Write-RankScan "retry pass $pass for ranks: $($finalMissing -join ', ')"
        foreach ($rank in $finalMissing) {
            Invoke-RetryRank -State $state -Rank $rank -Pass $pass
            Save-State -State $state
            Reap-ExtractorProcesses
        }
        Wait-AllExtractors
        $finalReport = Invoke-MergeReport -State $state
        Write-ProgressEta -State $state -Report $finalReport -ElapsedSeconds ([double]$state.elapsed_seconds)
        $finalMissing = @(@($finalReport.missing_ranks) + @($finalReport.incomplete_ranks) + @($finalReport.duplicate_content_ranks) |
            ForEach-Object { [int]$_ } |
            Where-Object { $_ -ge [int]$state.start_rank -and $_ -le [int]$state.max_scanned_rank } |
            Sort-Object -Unique)
    }

    if ($finalMissing.Count -gt 0) {
        Ensure-Property -Object $state -Name "last_missing_ranks" -Value @($finalMissing)
        Save-State -State $state -Status "blocked_missing_ranks"
        throw "Final coverage still has missing/incomplete ranks: $($finalMissing -join ', '). State saved at $($state.state_path)"
    }

    $completeStatus = if ($DryRun) { "dry_run_complete" } else { "complete" }
    Save-State -State $state -Status $completeStatus
    Write-RankScan "complete"
    Write-RankScan "state: $($state.state_path)"
    Write-RankScan "combined csv: $(Join-Path $state.combined_output_dir 'leaderboard_entries.csv')"
    Write-RankScan "combined parquet: $($state.combined_parquet_path)"
} catch {
    if ($null -ne $state) {
        Ensure-Property -Object $state -Name "last_error" -Value $_.Exception.Message
        if ([string]$state.status -notin @("blocked_missing_ranks", "cancelled", "cancelled_by_host")) {
            Save-State -State $state -Status "failed"
        } else {
            Save-State -State $state
        }
    }
    throw
} finally {
    if ($null -ne $state) {
        $state.elapsed_seconds = [double]$state.elapsed_seconds + $sessionStopwatch.Elapsed.TotalSeconds
        Save-State -State $state
    }
}
