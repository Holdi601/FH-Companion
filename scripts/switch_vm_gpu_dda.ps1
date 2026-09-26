[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $GpuNamePattern = "NVIDIA|RTX 5080",
    [string] $StatePath = "data/dda/forza_gpu_dda_state.json",
    [switch] $StartVM,
    [switch] $ForceActiveHostDisplay
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

function Write-Dda {
    param([Parameter(Mandatory = $true)][string] $Message)
    Write-Host "[dda-switch] $Message"
}

function Get-DeviceLocationPath {
    param([Parameter(Mandatory = $true)][string] $InstanceId)

    $property = Get-PnpDeviceProperty -InstanceId $InstanceId -KeyName "DEVPKEY_Device_LocationPaths" -ErrorAction Stop
    $paths = @($property.Data)
    if ($paths.Count -lt 1 -or [string]::IsNullOrWhiteSpace([string]$paths[0])) {
        throw "No PCI location path found for $InstanceId"
    }
    return [string]$paths[0]
}

function Test-ActiveHostDisplay {
    param([Parameter(Mandatory = $true)][string] $InstanceId)

    $video = Get-CimInstance Win32_VideoController |
        Where-Object { [string]$_.PNPDeviceID -eq $InstanceId } |
        Select-Object -First 1
    if ($null -eq $video) {
        return $false
    }
    return ($null -ne $video.CurrentHorizontalResolution -or $null -ne $video.CurrentVerticalResolution)
}

$statePathResolved = Resolve-WorkspacePath $StatePath
New-Item -ItemType Directory -Force -Path ([System.IO.Path]::GetDirectoryName($statePathResolved)) | Out-Null

$gpu = Get-PnpDevice -Class Display |
    Where-Object { [string]$_.FriendlyName -match $GpuNamePattern } |
    Select-Object -First 1
if ($null -eq $gpu) {
    throw "No display device matched '$GpuNamePattern'."
}
$locationPath = Get-DeviceLocationPath -InstanceId $gpu.InstanceId
$activeHostDisplay = Test-ActiveHostDisplay -InstanceId $gpu.InstanceId
if ($activeHostDisplay -and -not $ForceActiveHostDisplay) {
    throw "The selected GPU is still the active host display. Move your monitor(s) to the motherboard/AMD iGPU, confirm Windows is displaying through AMD, then rerun. Use -ForceActiveHostDisplay only if you deliberately accept losing the host display."
}

$vm = Get-VM -Name $VMName -ErrorAction Stop
if ($vm.State -ne "Off") {
    Write-Dda "stop VM $VMName"
    Stop-VM -Name $VMName -Force
}

Write-Dda "configure VM MMIO/cache for DDA"
Set-VMMemory -VMName $VMName -DynamicMemoryEnabled $false | Out-Null
Set-VM -Name $VMName -GuestControlledCacheTypes $true -LowMemoryMappedIoSpace 1GB -HighMemoryMappedIoSpace 32GB | Out-Null

$gpuPv = @(Get-VMGpuPartitionAdapter -VMName $VMName -ErrorAction SilentlyContinue)
if ($gpuPv.Count -gt 0) {
    Write-Dda "remove GPU-PV adapter(s)"
    Remove-VMGpuPartitionAdapter -VMName $VMName
}

Write-Dda "dismount RTX from host: $locationPath"
Dismount-VMHostAssignableDevice -LocationPath $locationPath -Force

Write-Dda "assign RTX to VM $VMName"
Add-VMAssignableDevice -LocationPath $locationPath -VMName $VMName

$state = [pscustomobject]@{
    vm_name = $VMName
    gpu_name = $gpu.FriendlyName
    gpu_instance_id = $gpu.InstanceId
    location_path = $locationPath
    switched_at = (Get-Date).ToUniversalTime().ToString("o")
    previous_gpu_pv_count = $gpuPv.Count
}
$state | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $statePathResolved -Encoding UTF8
Write-Dda "state saved: $statePathResolved"

if ($StartVM) {
    Write-Dda "start VM $VMName"
    Start-VM -Name $VMName
}

Write-Dda "done"
