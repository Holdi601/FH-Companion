[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $RunId,
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = ".\admin",
    [string] $OutputRoot = "data/network_probes",
    [int] $PollSeconds = 2,
    [int] $TimeoutHours = 8,
    [int] $MaxFileSizeMB = 4096
)

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path

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

function Write-RunTrace {
    param([Parameter(Mandatory = $true)][string] $Message)
    Write-Host "[scoreboard-trace] $Message"
}

if ($PollSeconds -lt 1 -or $TimeoutHours -lt 1 -or $MaxFileSizeMB -lt 256) {
    throw "PollSeconds/TimeoutHours must be positive and MaxFileSizeMB must be at least 256."
}

$safeRunId = $RunId -replace "[^A-Za-z0-9_]", "_"
$sessionName = "ForzaWebIO_$($safeRunId.Substring(0, [Math]::Min(45, $safeRunId.Length)))"
$hostRoot = Resolve-WorkspacePath (Join-Path $OutputRoot ("run_{0}_http_boundary" -f $RunId))
$guestRoot = "C:\ForzaNetworkProbes\run_$safeRunId"
$guestEtl = Join-Path $guestRoot "webio.etl"
$guestXml = Join-Path $guestRoot "webio.xml"
$guestStatePath = "C:\ForzaCaptures\rank_scans\$RunId\state.json"
$timelinePath = Join-Path $hostRoot "rank_timeline.jsonl"
$metadataPath = Join-Path $hostRoot "metadata.json"
$credential = New-BlankCredential -User $VMUser
$session = New-PSSession -VMName $VMName -Credential $credential
$traceStarted = $false
$startedAt = (Get-Date).ToUniversalTime()
$deadline = (Get-Date).AddHours($TimeoutHours)

try {
    New-Item -ItemType Directory -Force -Path $hostRoot | Out-Null
    if (Test-Path -LiteralPath $timelinePath) {
        Remove-Item -LiteralPath $timelinePath -Force
    }

    $forzaPid = [int](Invoke-Command -Session $session -ArgumentList $guestStatePath -ScriptBlock {
        param([string] $StatePath)
        if (-not (Test-Path -LiteralPath $StatePath)) {
            throw "Rank scan state does not exist: $StatePath"
        }
        $process = Get-Process -Name forzahorizon6 -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $process) {
            throw "Forza Horizon 6 is not running."
        }
        [int]$process.Id
    })

    Write-RunTrace "start provider-only WebIO trace for run $RunId (Forza PID $forzaPid)"
    Invoke-Command -Session $session -ArgumentList $sessionName, $guestRoot, $guestEtl, $MaxFileSizeMB -ScriptBlock {
        param([string] $SessionName, [string] $Root, [string] $EtlPath, [int] $MaxSize)
        New-Item -ItemType Directory -Force -Path $Root | Out-Null
        & logman stop $SessionName -ets 2>$null | Out-Null
        & logman delete $SessionName 2>$null | Out-Null
        Remove-Item -LiteralPath $EtlPath -Force -ErrorAction SilentlyContinue
        $output = & logman create trace $SessionName `
            -o $EtlPath `
            -p Microsoft-Windows-WebIO 0xFFFFFFFFFFFFFFFF 5 `
            -bs 1024 `
            -nb 16 256 `
            -max $MaxSize `
            -ow `
            -ets 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "logman create trace failed: $($output -join ' ')"
        }
        $output
    } | Out-Host
    $traceStarted = $true

    $lastProgressAt = Get-Date "2000-01-01"
    $finalSample = $null
    while ((Get-Date) -lt $deadline) {
        $sample = Invoke-Command -Session $session -ArgumentList $guestStatePath, "ForzaRankScan_$safeRunId" -ScriptBlock {
            param([string] $StatePath, [string] $TaskName)
            $state = $null
            if (Test-Path -LiteralPath $StatePath) {
                try {
                    $state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
                } catch {
                    $state = $null
                }
            }
            $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
            [pscustomobject]@{
                timestamp = (Get-Date).ToUniversalTime().ToString("o")
                task_state = if ($task) { [string]$task.State } else { "Missing" }
                status = if ($state) { [string]$state.status } else { "" }
                current_selected_rank = if ($state) { [int]$state.current_selected_rank } else { 0 }
                navigation_verified_rank = if ($state) { [int]$state.navigation_verified_rank } else { 0 }
                next_rank = if ($state) { [int]$state.next_rank } else { 0 }
                max_scanned_rank = if ($state) { [int]$state.max_scanned_rank } else { 0 }
                total_ranks = if ($state) { [int]$state.total_ranks } else { 0 }
            }
        }
        $finalSample = $sample
        $sample |
            Select-Object timestamp, task_state, status, current_selected_rank, navigation_verified_rank, next_rank, max_scanned_rank, total_ranks |
            ConvertTo-Json -Compress |
            Add-Content -LiteralPath $timelinePath -Encoding UTF8

        if (((Get-Date) - $lastProgressAt).TotalSeconds -ge 30) {
            Write-RunTrace "rank $($sample.max_scanned_rank)/$($sample.total_ranks), scan=$($sample.status), task=$($sample.task_state)"
            $lastProgressAt = Get-Date
        }

        $terminalStatus = [string]$sample.status -in @(
            "complete",
            "completed",
            "cancelled",
            "cancelled_by_host",
            "failed",
            "error"
        )
        if ($terminalStatus) {
            break
        }
        Start-Sleep -Seconds $PollSeconds
    }

    Write-RunTrace "stop provider trace and convert ETL"
    Invoke-Command -Session $session -ArgumentList $sessionName, $guestEtl, $guestXml -ScriptBlock {
        param([string] $SessionName, [string] $EtlPath, [string] $XmlPath)
        & logman stop $SessionName -ets | Out-Host
        $output = & netsh trace convert `
            input="$EtlPath" `
            output="$XmlPath" `
            dump=XML `
            report=no `
            overwrite=yes 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "netsh trace convert failed: $($output -join ' ')"
        }
        $output
    } | Out-Host
    $traceStarted = $false

    Copy-Item -FromSession $session -LiteralPath $guestEtl -Destination (Join-Path $hostRoot "webio.etl") -Force
    Copy-Item -FromSession $session -LiteralPath $guestXml -Destination (Join-Path $hostRoot "webio.xml") -Force

    [ordered]@{
        started_at = $startedAt.ToString("o")
        completed_at = (Get-Date).ToUniversalTime().ToString("o")
        vm = $VMName
        forza_pid = $forzaPid
        run_id = $RunId
        provider = "Microsoft-Windows-WebIO"
        provider_keywords = "0xFFFFFFFFFFFFFFFF"
        provider_level = 5
        max_file_size_mb = $MaxFileSizeMB
        final_scan_status = if ($finalSample) { [string]$finalSample.status } else { "" }
        final_max_scanned_rank = if ($finalSample) { [int]$finalSample.max_scanned_rank } else { 0 }
        raw_trace_is_sensitive = $true
    } | ConvertTo-Json | Set-Content -LiteralPath $metadataPath -Encoding UTF8

    $analyzer = Join-Path $PSScriptRoot "analyze_winhttp_trace.py"
    & python $analyzer `
        --trace-xml (Join-Path $hostRoot "webio.xml") `
        --output-dir (Join-Path $hostRoot "winhttp_analysis") `
        --process-id $forzaPid
    if ($LASTEXITCODE -ne 0) {
        throw "WinHTTP trace analyzer failed with exit code $LASTEXITCODE"
    }
    Write-RunTrace "complete: $hostRoot"
} finally {
    if ($traceStarted -and $session) {
        try {
            Invoke-Command -Session $session -ArgumentList $sessionName -ScriptBlock {
                param([string] $SessionName)
                & logman stop $SessionName -ets 2>$null | Out-Null
            } -ErrorAction SilentlyContinue
        } catch {
        }
    }
    if ($session) {
        Remove-PSSession $session
    }
}
