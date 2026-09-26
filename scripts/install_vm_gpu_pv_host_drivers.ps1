[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VhdPath = "D:\HyperV\ForzaScrapeVM\ForzaScrapeVM.vhdx",
    [string] $DriverInfName = "nvmdi.inf",
    [string] $DriverVersionPattern = "",
    [switch] $KeepStaleDriverFolders,
    [switch] $NoStart
)

$ErrorActionPreference = "Stop"

function Write-GpuDriver {
    param([Parameter(Mandatory = $true)][string] $Message)
    Write-Host "[gpu-pv-driver] $Message"
}

function Get-WindowsRootFromMountedDisk {
    param([Parameter(Mandatory = $true)] $Disk)

    foreach ($partition in @(Get-Partition -DiskNumber $Disk.Number | Where-Object DriveLetter)) {
        $root = "{0}:\" -f $partition.DriveLetter
        $windowsRoot = Join-Path $root "Windows"
        if (Test-Path -LiteralPath (Join-Path $windowsRoot "System32")) {
            return $windowsRoot
        }
    }
    throw "Could not find a Windows root on mounted VHD $VhdPath."
}

function Get-HostDriverStoreFolder {
    param(
        [Parameter(Mandatory = $true)][string] $InfName,
        [Parameter(Mandatory = $true)][string] $VersionPattern
    )

    $folders = Get-ChildItem -LiteralPath "$env:WINDIR\System32\DriverStore\FileRepository" -Directory -Filter "$InfName`_amd64*"
    $matches = foreach ($folder in $folders) {
        $infPath = Join-Path $folder.FullName $InfName
        if (Test-Path -LiteralPath $infPath) {
            $content = Get-Content -LiteralPath $infPath -Raw
            if ($content -match [regex]::Escape($VersionPattern)) {
                $folder
            }
        }
    }
    $selected = @($matches | Sort-Object LastWriteTime -Descending | Select-Object -First 1)
    if (-not $selected) {
        throw "Could not find $InfName matching $VersionPattern in the host DriverStore."
    }
    return $selected[0]
}

function Get-HostNvidiaDriverVersion {
    $gpu = Get-CimInstance Win32_VideoController |
        Where-Object { [string]$_.Name -match "NVIDIA" } |
        Select-Object -First 1
    if ($null -eq $gpu -or [string]::IsNullOrWhiteSpace([string]$gpu.DriverVersion)) {
        throw "Could not detect the host NVIDIA driver version."
    }
    return [string]$gpu.DriverVersion
}

function Copy-MatchingFiles {
    param(
        [Parameter(Mandatory = $true)][string] $SourceDir,
        [Parameter(Mandatory = $true)][string] $DestinationDir,
        [Parameter(Mandatory = $true)][string] $Pattern
    )

    if (-not (Test-Path -LiteralPath $SourceDir)) {
        return 0
    }
    New-Item -ItemType Directory -Force -Path $DestinationDir | Out-Null
    $files = @(Get-ChildItem -LiteralPath $SourceDir -File -Filter $Pattern -ErrorAction SilentlyContinue)
    foreach ($file in $files) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $DestinationDir $file.Name) -Force
    }
    return $files.Count
}

if ([string]::IsNullOrWhiteSpace($DriverVersionPattern)) {
    $DriverVersionPattern = Get-HostNvidiaDriverVersion
    Write-GpuDriver "detected host NVIDIA driver version: $DriverVersionPattern"
}

$driverFolder = Get-HostDriverStoreFolder -InfName $DriverInfName -VersionPattern $DriverVersionPattern
Write-GpuDriver "host driver folder: $($driverFolder.FullName)"

$vm = Get-VM -Name $VMName -ErrorAction Stop
if ($vm.State -ne "Off") {
    Write-GpuDriver "stop VM $VMName"
    try {
        Stop-VM -Name $VMName -ErrorAction Stop
        $deadline = (Get-Date).AddMinutes(3)
        while ((Get-VM -Name $VMName).State -ne "Off" -and (Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 2
        }
    } catch {
        Write-GpuDriver "graceful shutdown failed: $($_.Exception.Message)"
    }
    if ((Get-VM -Name $VMName).State -ne "Off") {
        Write-GpuDriver "force stop VM $VMName"
        Stop-VM -Name $VMName -TurnOff -Force
    }
}

$mounted = $false
try {
    Write-GpuDriver "mount VHD $VhdPath"
    $vhd = Mount-VHD -Path $VhdPath -Passthru
    $mounted = $true
    $disk = $vhd | Get-Disk
    $windowsRoot = Get-WindowsRootFromMountedDisk -Disk $disk
    Write-GpuDriver "guest Windows root: $windowsRoot"

    $hostDriverStore = Join-Path $windowsRoot "System32\HostDriverStore\FileRepository"
    $targetDriverFolder = Join-Path $hostDriverStore $driverFolder.Name
    New-Item -ItemType Directory -Force -Path $hostDriverStore | Out-Null
    if (-not $KeepStaleDriverFolders) {
        Get-ChildItem -LiteralPath $hostDriverStore -Directory -Filter "$DriverInfName`_amd64*" -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -ne $driverFolder.Name } |
            ForEach-Object {
                Write-GpuDriver "remove stale guest HostDriverStore folder: $($_.Name)"
                Remove-Item -LiteralPath $_.FullName -Recurse -Force
            }
    }
    if (Test-Path -LiteralPath $targetDriverFolder) {
        Write-GpuDriver "refresh existing guest HostDriverStore folder"
        Remove-Item -LiteralPath $targetDriverFolder -Recurse -Force
    }
    Write-GpuDriver "copy DriverStore folder"
    Copy-Item -LiteralPath $driverFolder.FullName -Destination $hostDriverStore -Recurse -Force

    $system32 = Join-Path $windowsRoot "System32"
    $syswow64 = Join-Path $windowsRoot "SysWOW64"
    $system32Count = Copy-MatchingFiles -SourceDir "$env:WINDIR\System32" -DestinationDir $system32 -Pattern "nv*"
    $syswow64Count = Copy-MatchingFiles -SourceDir "$env:WINDIR\SysWOW64" -DestinationDir $syswow64 -Pattern "nv*"
    Write-GpuDriver "copied $system32Count System32 nv* files and $syswow64Count SysWOW64 nv* files"

    $manifest = [pscustomobject]@{
        installed_at = (Get-Date).ToUniversalTime().ToString("o")
        vm_name = $VMName
        source_driver_folder = $driverFolder.FullName
        target_driver_folder = $targetDriverFolder
        driver_inf = $DriverInfName
        driver_version_pattern = $DriverVersionPattern
        system32_nv_files = $system32Count
        syswow64_nv_files = $syswow64Count
    }
    $manifestPath = Join-Path $windowsRoot "Temp\forza_gpu_pv_driver_install.json"
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $manifestPath) | Out-Null
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    Write-GpuDriver "manifest: $manifestPath"
}
finally {
    if ($mounted) {
        Write-GpuDriver "dismount VHD"
        Dismount-VHD -Path $VhdPath
    }
}

if (-not $NoStart) {
    Write-GpuDriver "start VM $VMName"
    Start-VM -Name $VMName
}
