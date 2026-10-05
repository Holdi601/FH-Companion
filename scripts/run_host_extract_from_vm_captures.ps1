[CmdletBinding()]
param(
    [string] $StatePath = "",
    [string] $CaptureRunRoot = "",
    [string] $ProcessedRunRoot = "",
    [string] $Track = "",
    [Alias("PerformanceClass")]
    [string] $PiClass = "",
    [string] $EventType = "",
    [string] $RivalsMode = "",
    [string] $TotalRanks = "",
    [string] $MaxScannedRank = "",
    [ValidateSet("windows-batch", "windows-single", "rapidocr-cpu", "rapidocr-gpu")]
    [string] $OcrBackend = "rapidocr-gpu",
    [ValidateSet("fast", "precise")]
    [string] $OcrMode = "fast",
    [string] $ExtractParallelJobs = "Auto",
    [string] $OcrThreadsPerProcess = "Auto",
    [int] $PollSeconds = 20,
    [int] $MaxChunks = 0,
    [switch] $Watch,
    [switch] $Force,
    [switch] $SkipMerge
)

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$script:extractorJobs = @()

function Resolve-WorkspacePath {
    param([Parameter(Mandatory = $true)][string] $InputPath)

    if ([System.IO.Path]::IsPathRooted($InputPath)) {
        return $InputPath
    }
    return [System.IO.Path]::GetFullPath((Join-Path $workspace $InputPath))
}

function Write-HostExtract {
    param([Parameter(Mandatory = $true)][string] $Message)
    Write-Host "[host-extract] $Message"
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
    try {
        $processors = @(Get-CimInstance -ClassName Win32_Processor -ErrorAction Stop)
        foreach ($processor in $processors) {
            $physicalCores += [int]$processor.NumberOfCores
        }
    } catch {
        $physicalCores = 0
    }
    if ($physicalCores -lt 1) {
        $physicalCores = [Math]::Max(1, [int][Math]::Ceiling($logicalThreads / 2.0))
    }
    $reserveThreads = [Math]::Max(2, [int][Math]::Ceiling($logicalThreads * 0.20))
    return [pscustomobject]@{
        physical_cores = $physicalCores
        logical_threads = $logicalThreads
        reserved_threads = $reserveThreads
        usable_threads = [Math]::Max(1, $logicalThreads - $reserveThreads)
    }
}

function Resolve-ParallelJobs {
    param(
        [Parameter(Mandatory = $true)][string] $Requested,
        [Parameter(Mandatory = $true)][string] $Backend,
        [Parameter(Mandatory = $true)] $Compute
    )

    $explicit = ConvertTo-PositiveIntOrNull $Requested
    if ($null -ne $explicit) {
        return $explicit
    }
    if ($Backend -eq "rapidocr-gpu") {
        return 1
    }
    return [Math]::Max(1, [Math]::Min([int]$Compute.physical_cores, [int][Math]::Floor([int]$Compute.usable_threads / 2.0)))
}

function Resolve-ThreadsPerProcess {
    param(
        [Parameter(Mandatory = $true)][string] $Requested,
        [Parameter(Mandatory = $true)][string] $Backend,
        [Parameter(Mandatory = $true)][int] $Jobs,
        [Parameter(Mandatory = $true)] $Compute
    )

    $explicit = ConvertTo-PositiveIntOrNull $Requested
    if ($null -ne $explicit) {
        return $explicit
    }
    if ($Backend -eq "rapidocr-cpu") {
        return [Math]::Max(1, [int][Math]::Floor([int]$Compute.usable_threads / [Math]::Max(1, $Jobs)))
    }
    return 1
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
        [int] $Lines = 60
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return ""
    }
    return (Get-Content -LiteralPath $Path -Tail $Lines -ErrorAction SilentlyContinue) -join [Environment]::NewLine
}

function Complete-ExtractorJob {
    param([Parameter(Mandatory = $true)] $Job)

    if ($null -eq $Job -or $null -eq $Job.process) {
        return
    }
    $Job.process.WaitForExit()
    $Job.process.Refresh()
    $exitCode = $Job.process.ExitCode
    $outputCsv = Join-Path $Job.output_dir "leaderboard_entries.csv"
    if ($null -eq $exitCode -and (Test-Path -LiteralPath $outputCsv)) {
        $exitCode = 0
    }
    if ($exitCode -ne 0) {
        $stderr = Get-LogTail -Path $Job.stderr -Lines 120
        $stdout = Get-LogTail -Path $Job.stdout -Lines 60
        throw "Extractor '$($Job.name)' failed with exit code $exitCode.`nstderr:`n$stderr`nstdout:`n$stdout"
    }

    $marker = [pscustomobject]@{
        completed_at = (Get-Date).ToUniversalTime().ToString("o")
        chunk = $Job.name
        source = $Job.capture_dir
        output = $Job.output_dir
    }
    $marker | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Job.output_dir ".extract_complete.json") -Encoding UTF8
    Write-HostExtract "done $($Job.name)"
}

function Reap-ExtractorJobs {
    param(
        [int] $MaxRunning,
        [switch] $WaitForSlot
    )

    $remaining = @()
    foreach ($job in (@($script:extractorJobs) | Where-Object { $null -ne $_ -and $null -ne $_.process })) {
        if ($job.process.HasExited) {
            Complete-ExtractorJob -Job $job
        } else {
            $remaining += $job
        }
    }
    $script:extractorJobs = @($remaining | Where-Object { $null -ne $_ -and $null -ne $_.process })

    while ($WaitForSlot -and $script:extractorJobs.Count -ge $MaxRunning) {
        $oldest = @($script:extractorJobs | Where-Object { $null -ne $_ -and $null -ne $_.process }) | Select-Object -First 1
        if ($null -eq $oldest) {
            $script:extractorJobs = @()
            break
        }
        Complete-ExtractorJob -Job $oldest
        $script:extractorJobs = @($script:extractorJobs | Where-Object { $_.name -ne $oldest.name })
        Reap-ExtractorJobs -MaxRunning $MaxRunning
    }
}

function Wait-AllExtractorJobs {
    param([int] $MaxRunning)

    Reap-ExtractorJobs -MaxRunning $MaxRunning
    foreach ($job in (@($script:extractorJobs) | Where-Object { $null -ne $_ -and $null -ne $_.process })) {
        Complete-ExtractorJob -Job $job
    }
    $script:extractorJobs = @()
}

function Start-ChunkExtractor {
    param(
        [Parameter(Mandatory = $true)][string] $ChunkDir,
        [Parameter(Mandatory = $true)][string] $OutputDir,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][int] $MaxRunning,
        [Parameter(Mandatory = $true)][int] $ThreadsPerProcess,
        [Parameter(Mandatory = $true)] $Metadata,
        [Parameter(Mandatory = $true)][string] $LogDir
    )

    Reap-ExtractorJobs -MaxRunning $MaxRunning -WaitForSlot
    New-Item -ItemType Directory -Force -Path $OutputDir, $LogDir | Out-Null
    $stdout = Join-Path $LogDir "$Name.out.log"
    $stderr = Join-Path $LogDir "$Name.err.log"
    $profile = Resolve-WorkspacePath "config/fh6_rivals_1080p.json"
    $args = @(
        (Resolve-WorkspacePath "scripts/extract_leaderboard.py"),
        "--input", $ChunkDir,
        "--include-glob", "leaderboard*.png",
        "--track", [string]$Metadata.track,
        "--performance-class", [string]$Metadata.pi_class,
        "--event-type", [string]$Metadata.event_type,
        "--rivals-mode", [string]$Metadata.rivals_mode,
        "--profile", $profile,
        "--output-dir", $OutputDir,
        "--ocr-mode", $OcrMode,
        "--ocr-backend", $OcrBackend,
        "--dump-ocr"
    )
    $argumentLine = ($args | ForEach-Object { ConvertTo-ProcessArgument ([string]$_) }) -join " "
    Write-HostExtract "start $Name ($OcrMode/$OcrBackend, running $($script:extractorJobs.Count + 1)/$MaxRunning)"

    $oldThreadBudget = $env:FORZA_OCR_THREADS_PER_PROCESS
    $env:FORZA_OCR_THREADS_PER_PROCESS = [string]$ThreadsPerProcess
    try {
        $process = Start-Process -FilePath "python" -ArgumentList $argumentLine -RedirectStandardOutput $stdout -RedirectStandardError $stderr -WindowStyle Hidden -PassThru
    } finally {
        if ($null -eq $oldThreadBudget) {
            Remove-Item Env:\FORZA_OCR_THREADS_PER_PROCESS -ErrorAction SilentlyContinue
        } else {
            $env:FORZA_OCR_THREADS_PER_PROCESS = $oldThreadBudget
        }
    }

    $script:extractorJobs += [pscustomobject]@{
        name = $Name
        process = $process
        stdout = $stdout
        stderr = $stderr
        capture_dir = $ChunkDir
        output_dir = $OutputDir
    }
}

function Invoke-Merge {
    param(
        [Parameter(Mandatory = $true)] $Metadata,
        [Parameter(Mandatory = $true)][string] $ProcessedChunksDir,
        [Parameter(Mandatory = $true)][string] $CombinedDir,
        [Parameter(Mandatory = $true)][string] $ParquetPath
    )

    if ($SkipMerge) {
        return
    }
    New-Item -ItemType Directory -Force -Path $CombinedDir | Out-Null
    $merge = Resolve-WorkspacePath "scripts/merge_rank_scan_outputs.py"
    $args = @(
        $merge,
        "--input-root", $ProcessedChunksDir,
        "--output-dir", $CombinedDir,
        "--parquet-path", $ParquetPath,
        "--total-ranks", ([string]$Metadata.total_ranks),
        "--max-scanned-rank", ([string]$Metadata.max_scanned_rank)
    )
    $output = & python @args
    if ($LASTEXITCODE -ne 0) {
        throw "merge_rank_scan_outputs.py failed with exit code $LASTEXITCODE"
    }
    $summary = $output | Select-Object -Last 1
    if (-not [string]::IsNullOrWhiteSpace($summary)) {
        Write-HostExtract "merge $summary"
    }
}

if ([string]::IsNullOrWhiteSpace($StatePath) -and [string]::IsNullOrWhiteSpace($CaptureRunRoot)) {
    throw "Pass -StatePath or -CaptureRunRoot."
}

if (-not [string]::IsNullOrWhiteSpace($StatePath)) {
    $StatePath = Resolve-WorkspacePath $StatePath
    if (-not (Test-Path -LiteralPath $StatePath)) {
        throw "State file not found: $StatePath"
    }
    $state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace($CaptureRunRoot)) {
        $CaptureRunRoot = Split-Path -Parent $StatePath
    }
} else {
    $state = [pscustomobject]@{}
}

$CaptureRunRoot = Resolve-WorkspacePath $CaptureRunRoot
$runId = Split-Path -Leaf $CaptureRunRoot
if ([string]::IsNullOrWhiteSpace($ProcessedRunRoot)) {
    $ProcessedRunRoot = Resolve-WorkspacePath (Join-Path "data/processed/vm_rank_scans" $runId)
} else {
    $ProcessedRunRoot = Resolve-WorkspacePath $ProcessedRunRoot
}

$metadata = [pscustomobject]@{
    track = if (-not [string]::IsNullOrWhiteSpace($Track)) { $Track } else { [string]$state.track }
    pi_class = if (-not [string]::IsNullOrWhiteSpace($PiClass)) { $PiClass } else { [string]$state.pi_class }
    event_type = if (-not [string]::IsNullOrWhiteSpace($EventType)) { $EventType } else { [string]$state.event_type }
    rivals_mode = if (-not [string]::IsNullOrWhiteSpace($RivalsMode)) { $RivalsMode } else { [string]$state.rivals_mode }
    total_ranks = if (-not [string]::IsNullOrWhiteSpace($TotalRanks)) { [int]$TotalRanks } elseif (($state.total_ranks -as [int]) -gt 0) { [int]$state.total_ranks } else { 0 }
    max_scanned_rank = if (-not [string]::IsNullOrWhiteSpace($MaxScannedRank)) { [int]$MaxScannedRank } elseif (($state.max_scanned_rank -as [int]) -gt 0) { [int]$state.max_scanned_rank } else { 0 }
}
foreach ($required in @("track", "pi_class", "event_type", "rivals_mode")) {
    if ([string]::IsNullOrWhiteSpace([string]$metadata.$required)) {
        throw "Missing metadata '$required'. Pass it explicitly or use -StatePath."
    }
}
if ($metadata.total_ranks -lt 1) {
    $metadata.total_ranks = [Math]::Max(1, [int]$metadata.max_scanned_rank)
}
if ($metadata.max_scanned_rank -lt 1) {
    $metadata.max_scanned_rank = $metadata.total_ranks
}

$chunksRoot = Join-Path $CaptureRunRoot "chunks"
if (-not (Test-Path -LiteralPath $chunksRoot)) {
    throw "Chunks directory not found: $chunksRoot"
}
$processedChunksDir = Join-Path $ProcessedRunRoot "chunks"
$combinedDir = Join-Path $ProcessedRunRoot "combined"
$parquetPath = Join-Path $ProcessedRunRoot "leaderboard_entries.parquet"
$logDir = Join-Path $ProcessedRunRoot "extract_logs"
New-Item -ItemType Directory -Force -Path $processedChunksDir, $combinedDir, $logDir | Out-Null

$compute = Get-SystemComputeProfile
$jobs = Resolve-ParallelJobs -Requested $ExtractParallelJobs -Backend $OcrBackend -Compute $compute
$threads = Resolve-ThreadsPerProcess -Requested $OcrThreadsPerProcess -Backend $OcrBackend -Jobs $jobs -Compute $compute
Write-HostExtract "capture root: $CaptureRunRoot"
Write-HostExtract "processed root: $ProcessedRunRoot"
Write-HostExtract "scheduling: backend $OcrBackend, jobs $jobs, threads/process $threads"

do {
    $chunkDirs = @(Get-ChildItem -LiteralPath $chunksRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Name -notmatch '^\.' -and
            $_.Name -notmatch '\.syncing$' -and
            @(Get-ChildItem -LiteralPath $_.FullName -Filter "leaderboard*.png" -File -ErrorAction SilentlyContinue).Count -gt 0
        } |
        Sort-Object Name)

    $started = 0
    foreach ($chunk in $chunkDirs) {
        $outDir = Join-Path $processedChunksDir $chunk.Name
        $marker = Join-Path $outDir ".extract_complete.json"
        if ((Test-Path -LiteralPath $marker) -and -not $Force) {
            continue
        }
        Start-ChunkExtractor -ChunkDir $chunk.FullName -OutputDir $outDir -Name $chunk.Name -MaxRunning $jobs -ThreadsPerProcess $threads -Metadata $metadata -LogDir $logDir
        $started += 1
        if ($MaxChunks -gt 0 -and $started -ge $MaxChunks) {
            break
        }
    }

    Wait-AllExtractorJobs -MaxRunning $jobs
    if ($started -gt 0) {
        Invoke-Merge -Metadata $metadata -ProcessedChunksDir $processedChunksDir -CombinedDir $combinedDir -ParquetPath $parquetPath
    } elseif (-not $Watch) {
        Invoke-Merge -Metadata $metadata -ProcessedChunksDir $processedChunksDir -CombinedDir $combinedDir -ParquetPath $parquetPath
    } else {
        Write-HostExtract "no new chunks"
    }

    if ($Watch) {
        Start-Sleep -Seconds $PollSeconds
        if (-not [string]::IsNullOrWhiteSpace($StatePath) -and (Test-Path -LiteralPath $StatePath)) {
            $state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
            if (($state.max_scanned_rank -as [int]) -gt 0) {
                $metadata.max_scanned_rank = [int]$state.max_scanned_rank
            }
            if (($state.total_ranks -as [int]) -gt 0) {
                $metadata.total_ranks = [int]$state.total_ranks
            }
        }
    }
} while ($Watch)

Write-HostExtract "done"
Write-HostExtract "combined parquet: $parquetPath"
