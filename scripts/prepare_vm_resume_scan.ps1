[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $SourceStatePath,

    [string] $HostShareRoot = "data/vm_share",

    [string] $GuestShareRoot = "Z:",

    [string] $HostProcessedRoot = "data/processed/vm_rank_scans",

    [switch] $CopyExistingProcessed,

    [switch] $Force
)

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path

function Resolve-WorkspacePath {
    param([Parameter(Mandatory = $true)][string] $InputPath)

    if ([System.IO.Path]::IsPathRooted($InputPath)) {
        return $InputPath
    }
    return [System.IO.Path]::GetFullPath((Join-Path $workspace $InputPath))
}

function Join-GuestPath {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string[]] $Parts
    )

    $path = $Root.TrimEnd("\", "/")
    foreach ($part in $Parts) {
        $path = $path + "\" + $part.Trim("\", "/")
    }
    return $path
}

function Set-StateProperty {
    param(
        [Parameter(Mandatory = $true)] $State,
        [Parameter(Mandatory = $true)][string] $Name,
        [AllowNull()] $Value
    )

    if ($State.PSObject.Properties.Name -contains $Name) {
        $State.$Name = $Value
    } else {
        $State | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
    }
}

$resolvedSource = Resolve-WorkspacePath $SourceStatePath
if (-not (Test-Path -LiteralPath $resolvedSource)) {
    throw "Source state not found: $resolvedSource"
}

$state = Get-Content -LiteralPath $resolvedSource -Raw | ConvertFrom-Json
$runId = [string]$state.run_id
if ([string]::IsNullOrWhiteSpace($runId)) {
    $runId = Split-Path -Leaf (Split-Path -Parent $resolvedSource)
}

$hostShareRootPath = Resolve-WorkspacePath $HostShareRoot
$hostCaptureRunRoot = Join-Path (Join-Path $hostShareRootPath "rank_scans") $runId
$hostCaptureChunks = Join-Path $hostCaptureRunRoot "chunks"
$hostProcessedRunRoot = Join-Path (Resolve-WorkspacePath $HostProcessedRoot) $runId
$hostProcessedChunks = Join-Path $hostProcessedRunRoot "chunks"
$hostCombinedDir = Join-Path $hostProcessedRunRoot "combined"
$hostParquetPath = Join-Path $hostProcessedRunRoot "leaderboard_entries.parquet"

if ((Test-Path -LiteralPath $hostCaptureRunRoot) -and -not $Force) {
    throw "VM capture run root already exists: $hostCaptureRunRoot. Pass -Force to update state there."
}

New-Item -ItemType Directory -Force -Path $hostCaptureRunRoot, $hostCaptureChunks, $hostProcessedChunks, $hostCombinedDir | Out-Null

$guestRunRoot = Join-GuestPath -Root $GuestShareRoot -Parts @("rank_scans", $runId)
$guestProcessedRunRoot = Join-GuestPath -Root $GuestShareRoot -Parts @("processed", $runId)
Set-StateProperty -State $state -Name "state_path" -Value (Join-GuestPath -Root $guestRunRoot -Parts @("state.json"))
Set-StateProperty -State $state -Name "run_root" -Value $guestRunRoot
Set-StateProperty -State $state -Name "runtime_dir" -Value (Join-GuestPath -Root $guestRunRoot -Parts @("runtime"))
Set-StateProperty -State $state -Name "capture_chunks_dir" -Value (Join-GuestPath -Root $guestRunRoot -Parts @("chunks"))
Set-StateProperty -State $state -Name "processed_run_root" -Value $guestProcessedRunRoot
Set-StateProperty -State $state -Name "processed_chunks_dir" -Value (Join-GuestPath -Root $guestProcessedRunRoot -Parts @("chunks"))
Set-StateProperty -State $state -Name "combined_output_dir" -Value (Join-GuestPath -Root $guestProcessedRunRoot -Parts @("combined"))
Set-StateProperty -State $state -Name "combined_parquet_path" -Value (Join-GuestPath -Root $guestProcessedRunRoot -Parts @("leaderboard_entries.parquet"))
Set-StateProperty -State $state -Name "vm_capture_only" -Value $true
Set-StateProperty -State $state -Name "prepared_for_vm_at" -Value ((Get-Date).ToUniversalTime().ToString("o"))
Set-StateProperty -State $state -Name "prepared_from_state" -Value $resolvedSource

$hostStatePath = Join-Path $hostCaptureRunRoot "state.json"
$state | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $hostStatePath -Encoding UTF8

if ($CopyExistingProcessed) {
    $sourceProcessedChunks = [string]$state.prepared_from_state
    $original = Get-Content -LiteralPath $resolvedSource -Raw | ConvertFrom-Json
    if ($original.PSObject.Properties.Name -contains "processed_chunks_dir" -and -not [string]::IsNullOrWhiteSpace([string]$original.processed_chunks_dir)) {
        $sourceProcessedChunks = [string]$original.processed_chunks_dir
    } else {
        $sourceProcessedChunks = Join-Path (Join-Path (Resolve-WorkspacePath "data/processed/rank_scans") $runId) "chunks"
    }
    if (Test-Path -LiteralPath $sourceProcessedChunks) {
        Write-Host "[vm-resume] copy existing processed chunks: $sourceProcessedChunks -> $hostProcessedChunks"
        Get-ChildItem -LiteralPath $sourceProcessedChunks -Force |
            Copy-Item -Destination $hostProcessedChunks -Recurse -Force -ErrorAction SilentlyContinue
    } else {
        Write-Host "[vm-resume] no existing processed chunks found at $sourceProcessedChunks"
    }
}

$guestResumeCommand = "scripts\run_leaderboard_rank_scan.ps1 -ResumeFrom `"$($state.state_path)`" -SkipExtract"
$hostExtractCommand = "scripts\run_host_extract_from_vm_captures.ps1 -StatePath `"$hostStatePath`" -Watch"

$summary = [pscustomobject]@{
    run_id = $runId
    host_state_path = $hostStatePath
    guest_state_path = $state.state_path
    host_capture_run_root = $hostCaptureRunRoot
    host_processed_run_root = $hostProcessedRunRoot
    host_parquet_path = $hostParquetPath
    guest_resume_command = $guestResumeCommand
    host_extract_command = $hostExtractCommand
}
$summaryPath = Join-Path $hostCaptureRunRoot "vm_resume_info.json"
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $summaryPath -Encoding UTF8

Write-Host "[vm-resume] state: $hostStatePath"
Write-Host "[vm-resume] guest command:"
Write-Host "[vm-resume]   $guestResumeCommand"
Write-Host "[vm-resume] host extractor command:"
Write-Host "[vm-resume]   $hostExtractCommand"
Write-Host "[vm-resume] summary: $summaryPath"
