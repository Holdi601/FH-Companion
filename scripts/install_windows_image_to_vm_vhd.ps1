[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $ImageFile = "J:\sources\install.esd",
    [int] $ImageIndex = 6,
    [string] $VhdPath = "D:\HyperV\ForzaScrapeVM\ForzaScrapeVM.vhdx",
    [int] $DiskGB = 256,
    [switch] $StartAfterInstall,
    [switch] $OpenVmConnect
)

$ErrorActionPreference = "Stop"

function Test-ElevatedAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)][string] $FilePath,
        [Parameter(Mandatory = $true)][string[]] $Arguments
    )

    Write-Host "[vhd-install] $FilePath $($Arguments -join ' ')"
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE"
    }
}

function Get-FreeDriveLetter {
    $used = @(Get-Volume -ErrorAction SilentlyContinue | Where-Object DriveLetter | ForEach-Object { [string]$_.DriveLetter })
    foreach ($code in ([byte][char]'Z')..([byte][char]'K')) {
        $letter = [string][char]$code
        if ($used -notcontains $letter) {
            return $letter
        }
    }
    throw "No free drive letter found."
}

if (-not (Test-ElevatedAdmin)) {
    throw "Run this script from an elevated PowerShell."
}
if (-not (Test-Path -LiteralPath $ImageFile)) {
    throw "Image file not found: $ImageFile"
}
if ($DiskGB -lt 128) {
    throw "-DiskGB must be at least 128."
}

$vm = Get-VM -Name $VMName -ErrorAction Stop
if ($vm.State -ne "Off") {
    Write-Host "[vhd-install] stop VM $VMName"
    Stop-VM -Name $VMName -TurnOff -Force
}

$vhdParent = Split-Path -Parent $VhdPath
New-Item -ItemType Directory -Force -Path $vhdParent | Out-Null
if (Test-Path -LiteralPath $VhdPath) {
    $backupPath = "$VhdPath.blank_$(Get-Date -Format yyyyMMdd_HHmmss)"
    Write-Host "[vhd-install] backup existing VHD: $backupPath"
    Move-Item -LiteralPath $VhdPath -Destination $backupPath -Force
}

Write-Host "[vhd-install] create VHD: $VhdPath"
New-VHD -Path $VhdPath -SizeBytes ([uint64]$DiskGB * 1GB) -Dynamic | Out-Null

$mounted = $null
$efiLetter = ""
$windowsLetter = ""
try {
    $mounted = Mount-VHD -Path $VhdPath -Passthru
    $disk = $mounted | Get-Disk
    if ($null -eq $disk) {
        throw "Could not resolve mounted VHD disk."
    }
    Write-Host "[vhd-install] initialize disk $($disk.Number)"
    Initialize-Disk -Number $disk.Number -PartitionStyle GPT

    $efiPartition = New-Partition -DiskNumber $disk.Number -Size 260MB -GptType "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}" -AssignDriveLetter
    Format-Volume -Partition $efiPartition -FileSystem FAT32 -NewFileSystemLabel "System" -Confirm:$false | Out-Null

    New-Partition -DiskNumber $disk.Number -Size 16MB -GptType "{e3c9e316-0b5c-4db8-817d-f92df00215ae}" | Out-Null

    $windowsPartition = New-Partition -DiskNumber $disk.Number -UseMaximumSize -AssignDriveLetter
    Format-Volume -Partition $windowsPartition -FileSystem NTFS -NewFileSystemLabel "Windows" -Confirm:$false | Out-Null

    $efiLetter = [string](Get-Partition -DiskNumber $disk.Number -PartitionNumber $efiPartition.PartitionNumber).DriveLetter
    $windowsLetter = [string](Get-Partition -DiskNumber $disk.Number -PartitionNumber $windowsPartition.PartitionNumber).DriveLetter
    if ([string]::IsNullOrWhiteSpace($efiLetter)) {
        $efiLetter = Get-FreeDriveLetter
        Set-Partition -DiskNumber $disk.Number -PartitionNumber $efiPartition.PartitionNumber -NewDriveLetter $efiLetter
    }
    if ([string]::IsNullOrWhiteSpace($windowsLetter)) {
        $windowsLetter = Get-FreeDriveLetter
        Set-Partition -DiskNumber $disk.Number -PartitionNumber $windowsPartition.PartitionNumber -NewDriveLetter $windowsLetter
    }

    $efiRoot = "${efiLetter}:"
    $windowsRoot = "${windowsLetter}:"
    Write-Host "[vhd-install] apply Windows image index $ImageIndex to $windowsRoot\"
    Invoke-Native -FilePath "dism.exe" -Arguments @(
        "/Apply-Image",
        "/ImageFile:$ImageFile",
        "/Index:$ImageIndex",
        "/ApplyDir:$windowsRoot\"
    )
    Write-Host "[vhd-install] write UEFI boot files to $efiRoot"
    Invoke-Native -FilePath "bcdboot.exe" -Arguments @(
        "$windowsRoot\Windows",
        "/s",
        $efiRoot,
        "/f",
        "UEFI"
    )
}
finally {
    if ($mounted) {
        Dismount-VHD -Path $VhdPath
    }
}

$hardDrive = Get-VMHardDiskDrive -VMName $VMName | Select-Object -First 1
if ($null -eq $hardDrive) {
    Add-VMHardDiskDrive -VMName $VMName -Path $VhdPath | Out-Null
    $hardDrive = Get-VMHardDiskDrive -VMName $VMName | Select-Object -First 1
} else {
    Set-VMHardDiskDrive -VMName $VMName -ControllerType $hardDrive.ControllerType -ControllerNumber $hardDrive.ControllerNumber -ControllerLocation $hardDrive.ControllerLocation -Path $VhdPath
    $hardDrive = Get-VMHardDiskDrive -VMName $VMName | Select-Object -First 1
}

Set-VMFirmware -VMName $VMName -FirstBootDevice $hardDrive -EnableSecureBoot Off
Write-Host "[vhd-install] Windows image installed. First boot device is the VHD."

if ($StartAfterInstall) {
    Start-VM -Name $VMName
    Write-Host "[vhd-install] VM started."
}
if ($OpenVmConnect) {
    Start-Process -FilePath vmconnect.exe -ArgumentList @("localhost", $VMName) -WindowStyle Normal
}
