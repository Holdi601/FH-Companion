param(
    [string] $OutputPath = "data/background/background_options.json",
    [switch] $JsonOnly
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

function Test-ElevatedAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-Safe {
    param(
        [Parameter(Mandatory = $true)] [scriptblock] $ScriptBlock,
        $Default = $null
    )

    try {
        return & $ScriptBlock
    }
    catch {
        return [pscustomobject]@{
            error = $_.Exception.Message
        }
    }
}

function Get-CommandStatus {
    param([Parameter(Mandatory = $true)][string[]] $Names)

    $result = @()
    foreach ($name in $Names) {
        $command = Get-Command -Name $name -ErrorAction SilentlyContinue
        $result += [pscustomobject]@{
            name = $name
            available = ($null -ne $command)
            source = if ($null -ne $command) { [string]$command.Source } else { "" }
            path = if ($null -ne $command) { [string]$command.Path } else { "" }
        }
    }
    return $result
}

function Get-OptionalFeatureStatus {
    param(
        [Parameter(Mandatory = $true)][string[]] $FeatureNames,
        [Parameter(Mandatory = $true)][bool] $IsElevated
    )

    if (-not $IsElevated) {
        return @([pscustomobject]@{
            feature_name = "all"
            state = "unknown"
            note = "requires elevated PowerShell"
        })
    }

    $result = @()
    foreach ($featureName in $FeatureNames) {
        $feature = Invoke-Safe {
            Get-WindowsOptionalFeature -Online -FeatureName $featureName |
                Select-Object FeatureName, State
        }
        if ($feature.PSObject.Properties.Name -contains "error") {
            $result += [pscustomobject]@{
                feature_name = $featureName
                state = "error"
                note = [string]$feature.error
            }
        } else {
            $result += [pscustomobject]@{
                feature_name = [string]$feature.FeatureName
                state = [string]$feature.State
                note = ""
            }
        }
    }
    return $result
}

function Get-PartitionableGpuStatus {
    param([Parameter(Mandatory = $true)][bool] $IsElevated)

    if (-not (Get-Command -Name Get-VMHostPartitionableGpu -ErrorAction SilentlyContinue)) {
        return @([pscustomobject]@{
            name = ""
            available = $false
            note = "Hyper-V GPU partition cmdlet is not available"
        })
    }
    if (-not $IsElevated) {
        return @([pscustomobject]@{
            name = ""
            available = $false
            note = "requires elevated PowerShell"
        })
    }

    $gpus = Invoke-Safe {
        Get-VMHostPartitionableGpu |
            Select-Object Name, TotalVRAM, AvailableVRAM, MinPartitionVRAM, MaxPartitionVRAM, OptimalPartitionVRAM,
                TotalEncode, AvailableEncode, TotalDecode, AvailableDecode, TotalCompute, AvailableCompute
    }
    if ($gpus.PSObject.Properties.Name -contains "error") {
        return @([pscustomobject]@{
            name = ""
            available = $false
            note = [string]$gpus.error
        })
    }

    return @($gpus | ForEach-Object {
        $_ | Add-Member -NotePropertyName available -NotePropertyValue $true -PassThru
    })
}

function Get-RdpStatus {
    $terminalServerKey = "HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server"
    $deny = Invoke-Safe {
        (Get-ItemProperty -Path $terminalServerKey -Name fDenyTSConnections -ErrorAction Stop).fDenyTSConnections
    }
    $enabled = $false
    $note = ""
    if ($deny -is [int]) {
        $enabled = ([int]$deny -eq 0)
    } elseif ($deny.PSObject.Properties.Name -contains "error") {
        $note = [string]$deny.error
    }

    return [pscustomobject]@{
        enabled = $enabled
        fDenyTSConnections = if ($deny -is [int]) { [int]$deny } else { $null }
        mstsc_available = ($null -ne (Get-Command -Name mstsc.exe -ErrorAction SilentlyContinue))
        quser_available = ($null -ne (Get-Command -Name quser.exe -ErrorAction SilentlyContinue))
        tscon_available = ($null -ne (Get-Command -Name tscon.exe -ErrorAction SilentlyContinue))
        note = $note
    }
}

function New-Recommendations {
    param(
        [Parameter(Mandatory = $true)] $Report
    )

    $recommendations = @()
    if (-not $Report.is_elevated_admin) {
        $recommendations += "Run this script once from an elevated PowerShell to verify Hyper-V optional features, existing VMs, and GPU-P partitionability."
    }
    if (@($Report.gpus).Count -ge 2) {
        $recommendations += "Two display adapters are visible. This is promising for a VM/background setup because the host desktop can remain on one adapter while the guest uses GPU-P or passthrough resources."
    }
    if ($Report.hyper_v.cmdlets_available) {
        $recommendations += "Hyper-V PowerShell cmdlets are installed. The next practical experiment is a Windows gaming VM with GPU-P, Steam/Forza installed in the guest, and the existing rank scanner run inside the guest."
    } else {
        $recommendations += "Hyper-V cmdlets are missing. VM-based background automation will need Hyper-V enabled or a different virtualization stack."
    }
    if (-not $Report.rdp.enabled) {
        $recommendations += "RDP is disabled. That is fine for now; RDP alone is not the preferred capture path for DirectX games, but it can help administer a VM after setup."
    }
    if ($Report.network_capture.pktmon_available -or $Report.network_capture.tshark_available) {
        $recommendations += "Network metadata capture tools are available. They can help identify endpoints and timing, but encrypted leaderboard payloads should be treated as opaque unless an official/plain API is discovered."
    }
    return $recommendations
}

$isElevated = Test-ElevatedAdmin
$computer = Invoke-Safe {
    Get-ComputerInfo |
        Select-Object WindowsProductName, WindowsVersion, OsHardwareAbstractionLayer, HyperVisorPresent,
            CsNumberOfLogicalProcessors, CsNumberOfProcessors
}
$gpus = Invoke-Safe {
    Get-CimInstance Win32_VideoController |
        Select-Object Name, AdapterRAM, DriverVersion, PNPDeviceID
}
$hyperVCommands = Get-CommandStatus -Names @(
    "Get-VM",
    "New-VM",
    "Add-VMGpuPartitionAdapter",
    "Get-VMHostPartitionableGpu",
    "Dismount-VMHostAssignableDevice",
    "Add-VMAssignableDevice"
)
$optionalFeatures = Get-OptionalFeatureStatus -FeatureNames @(
    "Microsoft-Hyper-V-All",
    "HypervisorPlatform",
    "VirtualMachinePlatform"
) -IsElevated $isElevated
$partitionableGpus = Get-PartitionableGpuStatus -IsElevated $isElevated
$vms = @()
if ($isElevated -and (Get-Command -Name Get-VM -ErrorAction SilentlyContinue)) {
    $vmResult = Invoke-Safe {
        Get-VM | Select-Object Name, State, Generation, ProcessorCount, MemoryAssigned, Path
    }
    if ($vmResult.PSObject.Properties.Name -contains "error") {
        $vms = @([pscustomobject]@{ error = [string]$vmResult.error })
    } else {
        $vms = @($vmResult)
    }
}

$networkCommands = Get-CommandStatus -Names @("pktmon.exe", "netsh.exe", "tshark.exe", "wireshark.exe", "dumpcap.exe")
$report = [pscustomobject]@{
    generated_at = (Get-Date).ToUniversalTime().ToString("o")
    workspace = $workspace
    is_elevated_admin = $isElevated
    windows = $computer
    gpus = @($gpus)
    hyper_v = [pscustomobject]@{
        cmdlets = $hyperVCommands
        cmdlets_available = (@($hyperVCommands | Where-Object { $_.available }).Count -ge 3)
        optional_features = $optionalFeatures
        partitionable_gpus = $partitionableGpus
        existing_vms = $vms
    }
    rdp = Get-RdpStatus
    network_capture = [pscustomobject]@{
        commands = $networkCommands
        pktmon_available = [bool](@($networkCommands | Where-Object { $_.name -eq "pktmon.exe" -and $_.available }).Count)
        tshark_available = [bool](@($networkCommands | Where-Object { $_.name -eq "tshark.exe" -and $_.available }).Count)
        dumpcap_available = [bool](@($networkCommands | Where-Object { $_.name -eq "dumpcap.exe" -and $_.available }).Count)
    }
}
$report | Add-Member -NotePropertyName recommendations -NotePropertyValue (New-Recommendations -Report $report)

$resolvedOutput = Resolve-WorkspacePath $OutputPath
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $resolvedOutput) | Out-Null
$report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $resolvedOutput -Encoding UTF8

if ($JsonOnly) {
    $report | ConvertTo-Json -Depth 20
    exit 0
}

Write-Host "[background-check] report: $resolvedOutput"
Write-Host "[background-check] elevated admin: $($report.is_elevated_admin)"
Write-Host "[background-check] Hyper-V cmdlets available: $($report.hyper_v.cmdlets_available)"
Write-Host "[background-check] GPUs visible: $(@($report.gpus).Count)"
foreach ($gpu in @($report.gpus)) {
    Write-Host "[background-check] GPU: $($gpu.Name) driver=$($gpu.DriverVersion)"
}
foreach ($recommendation in @($report.recommendations)) {
    Write-Host "[background-check] next: $recommendation"
}
