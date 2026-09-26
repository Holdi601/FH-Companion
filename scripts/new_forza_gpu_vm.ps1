[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",

    [Parameter(Mandatory = $true)]
    [string] $IsoPath,

    [string] $VMRoot = "",

    [string] $SwitchName = "Default Switch",

    [int] $MemoryGB = 16,

    [int] $DiskGB = 256,

    [int] $CpuCount = 12,

    [string] $GpuInstancePattern = "VEN_10DE",

    [int] $GpuResourcePercent = 100,

    [switch] $EnableTpm,

    [switch] $NoGpu,

    [switch] $StartAfterCreate,

    [switch] $PlanOnly
)

$ErrorActionPreference = "Stop"

function Test-ElevatedAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function ConvertTo-ByteCount {
    param(
        [Parameter(Mandatory = $true)][int] $Value,
        [Parameter(Mandatory = $true)][ValidateSet("GB")] [string] $Unit
    )

    switch ($Unit) {
        "GB" { return [int64]$Value * 1GB }
    }
}

function Resolve-VMRoot {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [AllowEmptyString()][string] $RequestedRoot
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedRoot)) {
        return [System.IO.Path]::GetFullPath($RequestedRoot)
    }
    if (Test-Path -LiteralPath "D:\") {
        return "D:\HyperV\$Name"
    }
    return "C:\HyperV\$Name"
}

function New-PartitionValue {
    param(
        [AllowNull()] $Total,
        [Parameter(Mandatory = $true)][int] $Percent
    )

    if ($null -eq $Total) {
        return $null
    }
    $totalDouble = [double]$Total
    if ($totalDouble -le 0 -or $totalDouble -gt 9000000000000000000.0) {
        return $null
    }
    return [uint64][Math]::Max(1, [Math]::Floor($totalDouble * ([Math]::Max(1, [Math]::Min(100, $Percent)) / 100.0)))
}

function Add-GpuPartition {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $Pattern,
        [Parameter(Mandatory = $true)][int] $Percent
    )

    if (-not (Get-Command -Name Get-VMHostPartitionableGpu -ErrorAction SilentlyContinue)) {
        throw "Get-VMHostPartitionableGpu is not available. Hyper-V GPU-P cmdlets are missing."
    }

    $gpu = Get-VMHostPartitionableGpu |
        Where-Object { [string]$_.Name -match $Pattern } |
        Select-Object -First 1
    if ($null -eq $gpu) {
        $available = (Get-VMHostPartitionableGpu | ForEach-Object { $_.Name }) -join [Environment]::NewLine
        throw "No partitionable GPU matched pattern '$Pattern'. Available GPUs:`n$available"
    }

    $args = @{
        VMName = $Name
        InstancePath = [string]$gpu.Name
    }

    $vram = New-PartitionValue -Total $gpu.TotalVRAM -Percent $Percent
    if ($null -ne $vram) {
        $args.MinPartitionVRAM = 0
        $args.MaxPartitionVRAM = $vram
        $args.OptimalPartitionVRAM = $vram
    }
    $encode = New-PartitionValue -Total $gpu.TotalEncode -Percent $Percent
    if ($null -ne $encode) {
        $args.MinPartitionEncode = 0
        $args.MaxPartitionEncode = $encode
        $args.OptimalPartitionEncode = $encode
    }
    $decode = New-PartitionValue -Total $gpu.TotalDecode -Percent $Percent
    if ($null -ne $decode) {
        $args.MinPartitionDecode = 0
        $args.MaxPartitionDecode = $decode
        $args.OptimalPartitionDecode = $decode
    }
    $compute = New-PartitionValue -Total $gpu.TotalCompute -Percent $Percent
    if ($null -ne $compute) {
        $args.MinPartitionCompute = 0
        $args.MaxPartitionCompute = $compute
        $args.OptimalPartitionCompute = $compute
    }

    Write-Host "[forza-vm] add GPU-P adapter: $($gpu.Name)"
    Add-VMGpuPartitionAdapter @args | Out-Null
}

$isAdmin = Test-ElevatedAdmin
if (-not $isAdmin -and -not $PlanOnly) {
    throw "Run this script from an elevated PowerShell."
}

$resolvedIso = [System.IO.Path]::GetFullPath($IsoPath)
if (-not (Test-Path -LiteralPath $resolvedIso)) {
    throw "ISO not found: $resolvedIso"
}
if ($MemoryGB -lt 8) {
    throw "-MemoryGB should be at least 8 for Windows plus Forza."
}
if ($DiskGB -lt 128) {
    throw "-DiskGB should be at least 128. Forza plus Windows will be large."
}
if ($CpuCount -lt 4) {
    throw "-CpuCount should be at least 4."
}
if ($GpuResourcePercent -lt 1 -or $GpuResourcePercent -gt 100) {
    throw "-GpuResourcePercent must be 1-100."
}

$resolvedRoot = Resolve-VMRoot -Name $VMName -RequestedRoot $VMRoot
$vhdPath = Join-Path $resolvedRoot "$VMName.vhdx"
$memoryBytes = ConvertTo-ByteCount -Value $MemoryGB -Unit GB
$diskBytes = [uint64](ConvertTo-ByteCount -Value $DiskGB -Unit GB)
$existingVm = $null
$switch = $null
if ($isAdmin) {
    $existingVm = Get-VM -Name $VMName -ErrorAction SilentlyContinue
    $switch = Get-VMSwitch -Name $SwitchName -ErrorAction SilentlyContinue
}

$plan = [pscustomobject]@{
    vm_name = $VMName
    iso_path = $resolvedIso
    vm_root = $resolvedRoot
    vhd_path = $vhdPath
    switch_name = $SwitchName
    switch_exists = if ($isAdmin) { ($null -ne $switch) } else { $null }
    elevated_admin = $isAdmin
    memory_gb = $MemoryGB
    disk_gb = $DiskGB
    cpu_count = $CpuCount
    gpu_enabled = (-not $NoGpu)
    gpu_instance_pattern = $GpuInstancePattern
    gpu_resource_percent = $GpuResourcePercent
    tpm_enabled = [bool]$EnableTpm
    start_after_create = [bool]$StartAfterCreate
}

Write-Host "[forza-vm] plan:"
$plan | ConvertTo-Json -Depth 5 | Write-Host

if ($PlanOnly) {
    exit 0
}
if ($null -ne $existingVm) {
    throw "VM already exists: $VMName"
}
if ($null -eq $switch) {
    throw "VMSwitch not found: '$SwitchName'. Create one in Hyper-V Manager or pass -SwitchName with an existing switch."
}

New-Item -ItemType Directory -Force -Path $resolvedRoot | Out-Null
Write-Host "[forza-vm] create VM $VMName"
New-VM `
    -Name $VMName `
    -Generation 2 `
    -MemoryStartupBytes $memoryBytes `
    -NewVHDPath $vhdPath `
    -NewVHDSizeBytes $diskBytes `
    -SwitchName $SwitchName |
    Out-Null

Set-VMMemory -VMName $VMName -DynamicMemoryEnabled $false -StartupBytes $memoryBytes | Out-Null
Set-VMProcessor -VMName $VMName -Count $CpuCount | Out-Null
Set-VM `
    -Name $VMName `
    -AutomaticCheckpointsEnabled $false `
    -CheckpointType Disabled `
    -EnhancedSessionTransportType HvSocket `
    -GuestControlledCacheTypes $true `
    -LowMemoryMappedIoSpace 1GB `
    -HighMemoryMappedIoSpace 32GB |
    Out-Null

$dvd = Add-VMDvdDrive -VMName $VMName -Path $resolvedIso -Passthru
Set-VMFirmware -VMName $VMName -FirstBootDevice $dvd -EnableSecureBoot On | Out-Null

if ($EnableTpm) {
    Write-Host "[forza-vm] enable virtual TPM"
    Set-VMKeyProtector -VMName $VMName -NewLocalKeyProtector | Out-Null
    Enable-VMTPM -VMName $VMName | Out-Null
}

if (-not $NoGpu) {
    Add-GpuPartition -Name $VMName -Pattern $GpuInstancePattern -Percent $GpuResourcePercent
}

Write-Host "[forza-vm] created $VMName"
Write-Host "[forza-vm] next: open Hyper-V Manager, connect to the VM, install Windows, then install Steam/Forza and this repo inside the guest."

if ($StartAfterCreate) {
    Write-Host "[forza-vm] starting $VMName"
    Start-VM -Name $VMName | Out-Null
}
