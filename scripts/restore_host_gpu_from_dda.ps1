[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $StatePath = "data/dda/forza_gpu_dda_state.json",
    [switch] $RestoreGpuPv,
    [switch] $StartVM
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
    Write-Host "[dda-restore] $Message"
}

$statePathResolved = Resolve-WorkspacePath $StatePath
if (-not (Test-Path -LiteralPath $statePathResolved)) {
    throw "DDA state file not found: $statePathResolved"
}
$state = Get-Content -LiteralPath $statePathResolved -Raw | ConvertFrom-Json
$locationPath = [string]$state.location_path
if ([string]::IsNullOrWhiteSpace($locationPath)) {
    throw "DDA state file does not contain a location_path."
}

$vm = Get-VM -Name $VMName -ErrorAction Stop
if ($vm.State -ne "Off") {
    Write-Dda "stop VM $VMName"
    Stop-VM -Name $VMName -Force
}

$assigned = @(Get-VMAssignableDevice -VMName $VMName -ErrorAction SilentlyContinue |
    Where-Object { [string]$_.LocationPath -eq $locationPath })
if ($assigned.Count -gt 0) {
    Write-Dda "remove assignable device from VM"
    Remove-VMAssignableDevice -VMName $VMName -LocationPath $locationPath
}

Write-Dda "mount RTX back to host"
Mount-VMHostAssignableDevice -LocationPath $locationPath

if ($RestoreGpuPv) {
    Write-Dda "restore GPU-PV adapter"
    if (-not (Get-VMGpuPartitionAdapter -VMName $VMName -ErrorAction SilentlyContinue)) {
        $partitionable = Get-VMHostPartitionableGpu |
            Where-Object { [string]$_.Name -match "VEN_10DE|DEV_2C02" } |
            Select-Object -First 1
        if ($partitionable) {
            Add-VMGpuPartitionAdapter -VMName $VMName -InstancePath ([string]$partitionable.Name) | Out-Null
        } else {
            Add-VMGpuPartitionAdapter -VMName $VMName | Out-Null
        }
    }
}

if ($StartVM) {
    Write-Dda "start VM $VMName"
    Start-VM -Name $VMName
}

Write-Dda "done"
