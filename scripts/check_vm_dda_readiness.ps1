[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $GpuNamePattern = "NVIDIA|RTX 5080"
)

$ErrorActionPreference = "Stop"

function New-CheckResult {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][bool] $Ok,
        [AllowNull()] $Value = $null,
        [string] $Details = ""
    )
    [pscustomobject]@{
        name = $Name
        ok = $Ok
        value = $Value
        details = $Details
    }
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

$results = @()
$ddaCommands = @(
    "Dismount-VMHostAssignableDevice",
    "Mount-VMHostAssignableDevice",
    "Add-VMAssignableDevice",
    "Remove-VMAssignableDevice",
    "Get-VMAssignableDevice"
)
foreach ($commandName in $ddaCommands) {
    $results += New-CheckResult -Name "command.$commandName" -Ok ([bool](Get-Command $commandName -ErrorAction SilentlyContinue)) -Value $commandName
}

$vm = Get-VM -Name $VMName -ErrorAction Stop
$results += New-CheckResult -Name "vm.exists" -Ok $true -Value ([pscustomobject]@{
    name = $vm.Name
    state = [string]$vm.State
    generation = $vm.Generation
    dynamic_memory = $vm.DynamicMemoryEnabled
    guest_controlled_cache_types = $vm.GuestControlledCacheTypes
    low_mmio = $vm.LowMemoryMappedIoSpace
    high_mmio = $vm.HighMemoryMappedIoSpace
})
$results += New-CheckResult -Name "vm.generation2" -Ok ($vm.Generation -eq 2) -Value $vm.Generation
$results += New-CheckResult -Name "vm.static_memory" -Ok (-not $vm.DynamicMemoryEnabled) -Value $vm.DynamicMemoryEnabled
$results += New-CheckResult -Name "vm.mmio_ready" -Ok ($vm.GuestControlledCacheTypes -and $vm.LowMemoryMappedIoSpace -ge 1GB -and $vm.HighMemoryMappedIoSpace -ge 16GB) -Value ([pscustomobject]@{
    guest_controlled_cache_types = $vm.GuestControlledCacheTypes
    low_mmio = $vm.LowMemoryMappedIoSpace
    high_mmio = $vm.HighMemoryMappedIoSpace
})

$gpu = Get-PnpDevice -Class Display |
    Where-Object { [string]$_.FriendlyName -match $GpuNamePattern } |
    Select-Object -First 1
if ($null -eq $gpu) {
    $results += New-CheckResult -Name "gpu.found" -Ok $false -Details "No display device matched '$GpuNamePattern'."
} else {
    $locationPath = Get-DeviceLocationPath -InstanceId $gpu.InstanceId
    $video = Get-CimInstance Win32_VideoController |
        Where-Object { [string]$_.PNPDeviceID -eq [string]$gpu.InstanceId } |
        Select-Object -First 1
    $isActiveDisplay = $false
    if ($null -ne $video) {
        $isActiveDisplay = $null -ne $video.CurrentHorizontalResolution -or $null -ne $video.CurrentVerticalResolution
    }
    $results += New-CheckResult -Name "gpu.found" -Ok $true -Value ([pscustomobject]@{
        friendly_name = $gpu.FriendlyName
        instance_id = $gpu.InstanceId
        status = [string]$gpu.Status
        problem = [string]$gpu.Problem
        location_path = $locationPath
        active_display = $isActiveDisplay
        current_mode = if ($video) { $video.VideoModeDescription } else { $null }
    })
    $results += New-CheckResult -Name "gpu.not_active_host_display" -Ok (-not $isActiveDisplay) -Value $isActiveDisplay -Details "Move your monitor cable to the motherboard/AMD iGPU before DDA."
}

$amd = Get-CimInstance Win32_VideoController |
    Where-Object { [string]$_.Name -match "AMD|Radeon" } |
    Select-Object -First 1
$amdActive = $false
if ($null -ne $amd) {
    $amdActive = $null -ne $amd.CurrentHorizontalResolution -or $null -ne $amd.CurrentVerticalResolution
}
$results += New-CheckResult -Name "host.amd_igpu_active" -Ok $amdActive -Value ([pscustomobject]@{
    name = if ($amd) { $amd.Name } else { $null }
    active = $amdActive
    mode = if ($amd) { $amd.VideoModeDescription } else { $null }
}) -Details "The host desktop should be on AMD before assigning the RTX to the VM."

$gpuPv = @(Get-VMGpuPartitionAdapter -VMName $VMName -ErrorAction SilentlyContinue)
$assigned = @(Get-VMAssignableDevice -VMName $VMName -ErrorAction SilentlyContinue)
$results += New-CheckResult -Name "vm.gpu_pv_present" -Ok ($gpuPv.Count -gt 0) -Value ($gpuPv | Select-Object InstancePath, CurrentPartitionVRAM, CurrentPartitionCompute)
$results += New-CheckResult -Name "vm.dda_assigned_present" -Ok ($assigned.Count -gt 0) -Value ($assigned | Select-Object InstancePath, LocationPath)

$ok = @($results | Where-Object { -not $_.ok -and $_.name -notin @("vm.gpu_pv_present", "vm.dda_assigned_present") }).Count -eq 0
$report = [pscustomobject]@{
    ok_to_attempt_dda = $ok
    generated_at = (Get-Date).ToUniversalTime().ToString("o")
    results = $results
}
$report | ConvertTo-Json -Depth 8
