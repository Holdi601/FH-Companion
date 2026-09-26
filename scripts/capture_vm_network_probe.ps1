[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = ".\admin",
    [string] $RunId = "",
    [int] $DurationSeconds = 120,
    [int] $SampleIntervalSeconds = 5,
    [int] $MaxFileSizeMB = 512,
    [string] $OutputRoot = "data/network_probes",
    [switch] $SkipAnalysis
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

function Write-Probe {
    param([Parameter(Mandatory = $true)][string] $Message)
    Write-Host "[network-probe] $Message"
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
    throw "Duration/sample interval must be positive and MaxFileSizeMB must be at least 32."
}

$credential = New-BlankCredential -User $VMUser
$session = New-RetryVmSession -Name $VMName -Credential $credential
$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$hostRoot = Resolve-WorkspacePath (Join-Path $OutputRoot "${stamp}_active_scroll")
$guestRoot = "C:\ForzaNetworkProbes\${stamp}_active_scroll"
$guestEtl = Join-Path $guestRoot "forza_scroll.etl"
$guestPcap = Join-Path $guestRoot "forza_scroll.pcapng"
$timelinePath = Join-Path $hostRoot "timeline.jsonl"
$metadataPath = Join-Path $hostRoot "metadata.json"

try {
    if ([string]::IsNullOrWhiteSpace($RunId)) {
        $RunId = Invoke-Command -Session $session -ScriptBlock {
            $task = Get-ScheduledTask -TaskName "ForzaRankScan_*" -ErrorAction SilentlyContinue |
                Where-Object State -eq "Running" |
                Select-Object -First 1
            if ($task) {
                return $task.TaskName.Substring("ForzaRankScan_".Length)
            }
            $latest = Get-ChildItem "C:\ForzaCaptures\rank_scans" -Directory -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending |
                Select-Object -First 1
            if ($latest) {
                return $latest.Name
            }
            return ""
        }
    }
    if ([string]::IsNullOrWhiteSpace($RunId)) {
        throw "No active or recent VM rank-scan run could be detected. Pass -RunId explicitly."
    }

    $guestStatePath = "C:\ForzaCaptures\rank_scans\$RunId\state.json"
    New-Item -ItemType Directory -Force -Path $hostRoot | Out-Null

    Write-Probe "start full packet capture in $VMName for ${DurationSeconds}s"
    Invoke-Command -Session $session -ArgumentList $guestRoot, $guestEtl, $MaxFileSizeMB -ScriptBlock {
        param([string] $Root, [string] $EtlPath, [int] $FileSizeMB)

        New-Item -ItemType Directory -Force -Path $Root | Out-Null
        pktmon stop 2>$null | Out-Null
        pktmon filter remove 2>$null | Out-Null
        $output = & pktmon start --capture --comp nics --pkt-size 0 --file-name $EtlPath --file-size $FileSizeMB --log-mode circular 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "pktmon start failed: $($output -join ' ')"
        }
        $output
    } | Out-Host

    $metadata = [ordered]@{
        started_at = (Get-Date).ToUniversalTime().ToString("o")
        vm = $VMName
        run_id = $RunId
        duration_seconds = $DurationSeconds
        sample_interval_seconds = $SampleIntervalSeconds
        max_file_size_mb = $MaxFileSizeMB
        guest_etl = $guestEtl
        guest_pcap = $guestPcap
        host_root = $hostRoot
    }
    $metadata | ConvertTo-Json | Set-Content -LiteralPath $metadataPath -Encoding UTF8

    $sampleCount = [int][Math]::Ceiling($DurationSeconds / [double]$SampleIntervalSeconds)
    for ($index = 0; $index -lt $sampleCount; $index += 1) {
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

            $forza = Get-Process -Name forzahorizon6 -ErrorAction SilentlyContinue | Select-Object -First 1
            $forzaPid = if ($forza) { [int]$forza.Id } else { 0 }
            $tcp = @(
                Get-NetTCPConnection -ErrorAction SilentlyContinue |
                    Where-Object OwningProcess -eq $forzaPid |
                    ForEach-Object {
                        [pscustomobject]@{
                            state = [string]$_.State
                            local_address = [string]$_.LocalAddress
                            local_port = [int]$_.LocalPort
                            remote_address = [string]$_.RemoteAddress
                            remote_port = [int]$_.RemotePort
                        }
                    }
            )
            $udp = @(
                Get-NetUDPEndpoint -ErrorAction SilentlyContinue |
                    Where-Object OwningProcess -eq $forzaPid |
                    ForEach-Object {
                        [pscustomobject]@{
                            local_address = [string]$_.LocalAddress
                            local_port = [int]$_.LocalPort
                        }
                    }
            )
            $stats = Get-NetAdapterStatistics -Name Ethernet -ErrorAction SilentlyContinue

            [pscustomobject]@{
                timestamp = (Get-Date).ToUniversalTime().ToString("o")
                forza_pid = $forzaPid
                scan_status = if ($state) { [string]$state.status } else { "" }
                verified_rank = if ($state) { [int]$state.navigation_verified_rank } else { 0 }
                target_rank = if ($state) { [int]$state.navigation_target_rank } else { 0 }
                max_scanned_rank = if ($state) { [int]$state.max_scanned_rank } else { 0 }
                next_rank = if ($state) { [int]$state.next_rank } else { 0 }
                tcp = $tcp
                udp = $udp
                adapter_stats = [pscustomobject]@{
                    received_bytes = if ($stats) { [uint64]$stats.ReceivedBytes } else { 0 }
                    sent_bytes = if ($stats) { [uint64]$stats.SentBytes } else { 0 }
                    received_packets = if ($stats) { [uint64]$stats.ReceivedUnicastPackets } else { 0 }
                    sent_packets = if ($stats) { [uint64]$stats.SentUnicastPackets } else { 0 }
                }
            }
        }

        $sample | Select-Object timestamp, forza_pid, scan_status, verified_rank, target_rank, max_scanned_rank, next_rank, tcp, udp, adapter_stats |
            ConvertTo-Json -Depth 8 -Compress |
            Add-Content -LiteralPath $timelinePath -Encoding UTF8

        $elapsed = [Math]::Min($DurationSeconds, ($index + 1) * $SampleIntervalSeconds)
        Write-Probe "$elapsed/${DurationSeconds}s status=$($sample.scan_status) rank=$($sample.verified_rank)/$($sample.target_rank)"
        if ($elapsed -lt $DurationSeconds) {
            Start-Sleep -Seconds $SampleIntervalSeconds
        }
    }

    Write-Probe "stop capture and convert ETL to PCAPNG"
    Invoke-Command -Session $session -ArgumentList $guestEtl, $guestPcap -ScriptBlock {
        param([string] $EtlPath, [string] $PcapPath)

        pktmon stop | Out-Host
        $output = & pktmon etl2pcap $EtlPath --out $PcapPath 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "pktmon etl2pcap failed: $($output -join ' ')"
        }
        $output
    } | Out-Host

    Copy-Item -FromSession $session -LiteralPath $guestEtl -Destination (Join-Path $hostRoot "forza_scroll.etl") -Force
    Copy-Item -FromSession $session -LiteralPath $guestPcap -Destination (Join-Path $hostRoot "forza_scroll.pcapng") -Force
    $metadata.completed_at = (Get-Date).ToUniversalTime().ToString("o")
    $metadata | ConvertTo-Json | Set-Content -LiteralPath $metadataPath -Encoding UTF8

    if (-not $SkipAnalysis) {
        $analyzer = Join-Path $PSScriptRoot "analyze_network_probe.py"
        & python $analyzer --probe-dir $hostRoot
        if ($LASTEXITCODE -ne 0) {
            throw "Network probe analyzer failed with exit code $LASTEXITCODE"
        }
    }

    Write-Probe "complete: $hostRoot"
} finally {
    try {
        Invoke-Command -Session $session -ScriptBlock { pktmon stop 2>$null | Out-Null } -ErrorAction SilentlyContinue
    } catch {
    }
    if ($session) {
        Remove-PSSession $session
    }
}
