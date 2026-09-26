[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = ".\admin",
    [string] $RunId = "",
    [int] $DurationSeconds = 30,
    [int] $SampleIntervalSeconds = 2,
    [int] $MaxFileSizeMB = 256,
    [string] $OutputRoot = "data/network_probes",
    [switch] $KeepGuestTrace
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

function Write-TraceStatus {
    param([Parameter(Mandatory = $true)][string] $Message)
    Write-Host "[http-trace] $Message"
}

function New-BlankCredential {
    param([Parameter(Mandatory = $true)][string] $User)

    if ($User -notmatch '^[^\\]+\\' -and $User -notmatch '@') {
        $User = ".\$User"
    }
    return [pscredential]::new($User, [Security.SecureString]::new())
}

function New-RetryVmSession {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)] $Credential
    )

    for ($attempt = 1; $attempt -le 20; $attempt += 1) {
        try {
            return New-PSSession -VMName $Name -Credential $Credential -ErrorAction Stop
        } catch {
            if ($attempt -eq 20) {
                throw
            }
            Start-Sleep -Seconds 2
        }
    }
}

if ($DurationSeconds -lt 1 -or $SampleIntervalSeconds -lt 1 -or $MaxFileSizeMB -lt 32) {
    throw "DurationSeconds/SampleIntervalSeconds must be positive and MaxFileSizeMB must be at least 32."
}

$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$hostRoot = Resolve-WorkspacePath (Join-Path $OutputRoot "${stamp}_http_boundary")
$guestRoot = "C:\ForzaNetworkProbes\${stamp}_http_boundary"
$guestEtl = Join-Path $guestRoot "internetclient_trace.etl"
$guestXml = Join-Path $guestRoot "internetclient_trace.xml"
$timelinePath = Join-Path $hostRoot "rank_timeline.jsonl"
$credential = New-BlankCredential -User $VMUser
$session = New-RetryVmSession -Name $VMName -Credential $credential
$traceStarted = $false

try {
    New-Item -ItemType Directory -Force -Path $hostRoot | Out-Null
    $captureStartedAt = (Get-Date).ToUniversalTime().ToString("o")
    $forzaPid = [int](Invoke-Command -Session $session -ScriptBlock {
        $process = Get-Process -Name forzahorizon6 -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $process) {
            throw "Forza Horizon 6 is not running in the VM."
        }
        [int]$process.Id
    })
    if ([string]::IsNullOrWhiteSpace($RunId)) {
        $RunId = [string](Invoke-Command -Session $session -ScriptBlock {
            $task = Get-ScheduledTask -TaskName "ForzaRankScan_*" -ErrorAction SilentlyContinue |
                Where-Object State -eq "Running" |
                Select-Object -First 1
            if ($task) {
                return $task.TaskName.Substring("ForzaRankScan_".Length)
            }
            return ""
        })
    }
    $guestStatePath = if ([string]::IsNullOrWhiteSpace($RunId)) {
        ""
    } else {
        "C:\ForzaCaptures\rank_scans\$RunId\state.json"
    }

    Write-TraceStatus "capture WinHTTP/WebIO boundary for ${DurationSeconds}s (Forza PID $forzaPid)"
    Invoke-Command -Session $session -ArgumentList $guestRoot, $guestEtl, $MaxFileSizeMB -ScriptBlock {
        param([string] $Root, [string] $EtlPath, [int] $FileSizeMB)

        New-Item -ItemType Directory -Force -Path $Root | Out-Null
        & netsh trace stop 2>$null | Out-Null
        $output = & netsh trace start `
            scenario=InternetClient `
            capture=no `
            report=no `
            correlation=no `
            persistent=no `
            tracefile="$EtlPath" `
            maxsize=$FileSizeMB `
            filemode=single `
            overwrite=yes 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "netsh trace start failed: $($output -join ' ')"
        }
        $output
    } | Out-Host
    $traceStarted = $true

    for ($elapsed = 1; $elapsed -le $DurationSeconds; $elapsed += 1) {
        Start-Sleep -Seconds 1
        if (
            -not [string]::IsNullOrWhiteSpace($guestStatePath) -and
            ($elapsed -eq 1 -or $elapsed -eq $DurationSeconds -or $elapsed % $SampleIntervalSeconds -eq 0)
        ) {
            $sample = Invoke-Command -Session $session -ArgumentList $guestStatePath -ScriptBlock {
                param([string] $StatePath)
                $state = $null
                if (Test-Path -LiteralPath $StatePath) {
                    try {
                        $state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
                    } catch {
                        $state = $null
                    }
                }
                [pscustomobject]@{
                    timestamp = (Get-Date).ToUniversalTime().ToString("o")
                    status = if ($state) { [string]$state.status } else { "" }
                    current_selected_rank = if ($state) { [int]$state.current_selected_rank } else { 0 }
                    navigation_verified_rank = if ($state) { [int]$state.navigation_verified_rank } else { 0 }
                    next_rank = if ($state) { [int]$state.next_rank } else { 0 }
                    max_scanned_rank = if ($state) { [int]$state.max_scanned_rank } else { 0 }
                }
            }
            $sample |
                Select-Object timestamp, status, current_selected_rank, navigation_verified_rank, next_rank, max_scanned_rank |
                ConvertTo-Json -Compress |
                Add-Content -LiteralPath $timelinePath -Encoding UTF8
        }
        if ($elapsed -eq 1 -or $elapsed -eq $DurationSeconds -or $elapsed % 5 -eq 0) {
            Write-TraceStatus "$elapsed/${DurationSeconds}s"
        }
    }

    Write-TraceStatus "stop and convert trace"
    Invoke-Command -Session $session -ArgumentList $guestEtl, $guestXml -ScriptBlock {
        param([string] $EtlPath, [string] $XmlPath)

        & netsh trace stop | Out-Host
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

    Copy-Item -FromSession $session -LiteralPath $guestEtl -Destination (Join-Path $hostRoot "internetclient_trace.etl") -Force
    Copy-Item -FromSession $session -LiteralPath $guestXml -Destination (Join-Path $hostRoot "internetclient_trace.xml") -Force

    $metadata = [ordered]@{
        started_at = $captureStartedAt
        completed_at = (Get-Date).ToUniversalTime().ToString("o")
        vm = $VMName
        forza_pid = $forzaPid
        run_id = $RunId
        duration_seconds = $DurationSeconds
        sample_interval_seconds = $SampleIntervalSeconds
        capture = "InternetClient ETW, packet capture disabled"
        raw_trace_is_sensitive = $true
    }
    $metadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $hostRoot "metadata.json") -Encoding UTF8

    $analyzer = Join-Path $PSScriptRoot "analyze_winhttp_trace.py"
    & python $analyzer `
        --trace-xml (Join-Path $hostRoot "internetclient_trace.xml") `
        --output-dir (Join-Path $hostRoot "winhttp_analysis") `
        --process-id $forzaPid
    if ($LASTEXITCODE -ne 0) {
        throw "WinHTTP trace analyzer failed with exit code $LASTEXITCODE"
    }

    if (-not $KeepGuestTrace) {
        Invoke-Command -Session $session -ArgumentList $guestRoot -ScriptBlock {
            param([string] $Root)
            $resolved = [System.IO.Path]::GetFullPath($Root)
            if (-not $resolved.StartsWith("C:\ForzaNetworkProbes\", [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Refusing to remove unexpected trace directory: $resolved"
            }
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }

    Write-TraceStatus "complete: $hostRoot"
    Write-Warning "The raw ETL/XML can contain live account/session headers. Keep this folder private."
} finally {
    if ($traceStarted) {
        try {
            Invoke-Command -Session $session -ScriptBlock { & netsh trace stop 2>$null | Out-Null } -ErrorAction SilentlyContinue
        } catch {
        }
    }
    if ($session) {
        Remove-PSSession $session
    }
}
