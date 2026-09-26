<#
.SYNOPSIS
    Re-stage the host's NVIDIA GPU-PV driver files into a RUNNING guest, over
    PowerShell Direct.

.DESCRIPTION
    GPU paravirtualisation requires the guest's copy of the display driver to
    match the host's exactly. Every host driver update therefore breaks the
    guest silently: the partitioned adapter comes up with Problem Code 43, the
    game falls back to software rendering or wedges the host's display driver,
    and nothing in the guest says why.

    That is not hypothetical. The host updated to 32.0.16.1088 on 2026-08-18 at
    02:38 while vmwp.exe held the GPU; the guest sat on 32.0.16.1047 from
    2026-05-19 for every session afterwards, and the host hard-froze -- no
    bugcheck, no TDR event, nothing in the log -- while Forza drove the broken
    partition.

    install_vm_gpu_pv_host_drivers.ps1 does the same job by mounting the VHD
    offline. That needs SeManageVolumePrivilege, so it only runs from an
    elevated shell and cannot run unattended from a normal session. This script
    needs no host elevation and no SMB credentials: PowerShell Direct reaches
    the guest over VMBus, which is available to Hyper-V Administrators.

    Only files that actually differ are copied. The guest's existing package is
    renamed to the new package's name first, so its unchanged files count as
    already delivered and the transfer carries the delta rather than all 2.7 GB.

.EXAMPLE
    .\scripts\sync_vm_gpu_pv_drivers.ps1

.EXAMPLE
    .\scripts\sync_vm_gpu_pv_drivers.ps1 -WhatIfOnly   # report the delta, change nothing
#>
[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = ".\admin",
    [string] $DriverInfName = "nvmdi.inf",
    [switch] $WhatIfOnly,
    [switch] $NoRestart
)

$ErrorActionPreference = "Stop"

function Write-Sync {
    param([Parameter(Mandatory = $true)][string] $Message)
    Write-Host "[gpu-pv-sync] $Message"
}

function Connect-Guest {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][pscredential] $Credential,
        [int] $TimeoutMinutes = 5
    )
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    while ((Get-Date) -lt $deadline) {
        try { return New-PSSession -VMName $Name -Credential $Credential -ErrorAction Stop }
        catch { Start-Sleep -Seconds 8 }
    }
    throw "PowerShell Direct to $Name did not come up within $TimeoutMinutes minutes."
}

function Get-GuestAdapterState {
    param([Parameter(Mandatory = $true)] $Session)
    return Invoke-Command -Session $Session -ScriptBlock {
        $device = Get-PnpDevice -Class Display -ErrorAction SilentlyContinue |
            Where-Object { $_.FriendlyName -match "NVIDIA" } | Select-Object -First 1
        $controller = Get-CimInstance Win32_VideoController |
            Where-Object { $_.Name -match "NVIDIA" } | Select-Object -First 1
        [pscustomobject]@{
            Status = if ($device) { [string]$device.Status } else { "absent" }
            Problem = if ($device) {
                [string](Get-PnpDeviceProperty -InstanceId $device.InstanceId `
                    -KeyName 'DEVPKEY_Device_ProblemCode' -ErrorAction SilentlyContinue).Data
            } else { "" }
            DriverVersion = if ($controller) { [string]$controller.DriverVersion } else { "" }
        }
    }
}

# ------------------------------------------------------------------ host side

$gpu = Get-CimInstance Win32_VideoController |
    Where-Object { [string]$_.Name -match "NVIDIA" } | Select-Object -First 1
if (-not $gpu) { throw "No NVIDIA adapter on the host." }
$hostVersion = [string]$gpu.DriverVersion
Write-Sync "host GPU: $($gpu.Name) driver $hostVersion"

# The package is identified by the driver version recorded in its INF, not by
# folder name: the hash suffix changes on every update and several stale
# packages linger in the repository.
$repository = Join-Path $env:WINDIR "System32\DriverStore\FileRepository"
$package = $null
foreach ($folder in Get-ChildItem -LiteralPath $repository -Directory -Filter "$DriverInfName`_amd64*") {
    $inf = Join-Path $folder.FullName $DriverInfName
    if (-not (Test-Path -LiteralPath $inf)) { continue }
    if (Select-String -LiteralPath $inf -Pattern ([regex]::Escape($hostVersion)) -Quiet) {
        if (-not $package -or $folder.LastWriteTime -gt $package.LastWriteTime) { $package = $folder }
    }
}
if (-not $package) {
    throw "No $DriverInfName package in the host DriverStore declares version $hostVersion."
}
Write-Sync "host package: $($package.Name)"

$prefix = $package.FullName.Length + 1
$hostFiles = @(Get-ChildItem -LiteralPath $package.FullName -File -Recurse | ForEach-Object {
    [pscustomobject]@{
        Rel  = $_.FullName.Substring($prefix)
        Full = $_.FullName
        Len  = $_.Length
        Hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm MD5).Hash
    }
})
$hostRelative = @($hostFiles | ForEach-Object { $_.Rel })
Write-Sync "host package holds $($hostFiles.Count) file(s)"

# ----------------------------------------------------------------- guest side

if ((Get-VM -Name $VMName -ErrorAction Stop).State -ne "Running") {
    Write-Sync "starting $VMName"
    Start-VM -Name $VMName
}

$credential = [pscredential]::new($VMUser, [Security.SecureString]::new())
$session = Connect-Guest -Name $VMName -Credential $credential

try {
    $before = Get-GuestAdapterState -Session $session
    Write-Sync "guest adapter before: status=$($before.Status) problem=$($before.Problem) driver=$($before.DriverVersion)"

    $existing = Invoke-Command -Session $session -ArgumentList $DriverInfName -ScriptBlock {
        param($InfName)
        $repository = "C:\Windows\System32\HostDriverStore\FileRepository"
        New-Item -ItemType Directory -Force -Path $repository | Out-Null
        @(Get-ChildItem -LiteralPath $repository -Directory -Filter "$InfName`_amd64*" -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | ForEach-Object { $_.Name })
    }
    Write-Sync "guest packages: $(if ($existing) { $existing -join ', ' } else { 'none' })"

    # Hash whichever folder actually holds the guest's current files. Compared
    # against the wrong folder this reports the whole package as the delta and
    # quietly pushes 2.7 GB instead of the ~200 MB that really changed.
    $compareAgainst = if ($existing -contains $package.Name) {
        $package.Name
    } elseif ($existing) {
        @($existing)[0]
    } else {
        $package.Name
    }

    # Reuse whatever the guest already has: renaming the newest stale package to
    # the wanted name leaves its unchanged files in place, so only the delta has
    # to cross the wire. The comparison target moves with the rename.
    if (-not $WhatIfOnly -and $existing -and ($existing -notcontains $package.Name)) {
        $donor = @($existing)[0]
        Write-Sync "reusing guest package $donor as $($package.Name)"
        Invoke-Command -Session $session -ArgumentList $donor, $package.Name -ScriptBlock {
            param($Donor, $Wanted)
            $repository = "C:\Windows\System32\HostDriverStore\FileRepository"
            Rename-Item -LiteralPath (Join-Path $repository $Donor) -NewName $Wanted -Force
        }
        $compareAgainst = $package.Name
    }
    $guestHashes = @(Invoke-Command -Session $session -ArgumentList $compareAgainst -ScriptBlock {
        param($Wanted)
        $target = Join-Path "C:\Windows\System32\HostDriverStore\FileRepository" $Wanted
        if (-not (Test-Path -LiteralPath $target)) { return @() }
        $prefix = $target.Length + 1
        Get-ChildItem -LiteralPath $target -File -Recurse | ForEach-Object {
            [pscustomobject]@{
                Rel  = $_.FullName.Substring($prefix)
                Hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm MD5).Hash
            }
        }
    })
    $guestMap = @{}
    foreach ($entry in $guestHashes) { $guestMap[$entry.Rel] = $entry.Hash }

    $changed = @($hostFiles | Where-Object {
        -not $guestMap.ContainsKey($_.Rel) -or $guestMap[$_.Rel] -ne $_.Hash
    })
    $stale = @($guestMap.Keys | Where-Object { $hostRelative -notcontains $_ })
    $megabytes = if ($changed.Count) {
        [math]::Round((($changed | Measure-Object -Property Len -Sum).Sum) / 1MB, 1)
    } else { 0 }
    Write-Sync "delta: $($changed.Count) file(s) to copy ($megabytes MB), $($stale.Count) stale file(s) to remove"

    if ($WhatIfOnly) {
        Write-Sync "-WhatIfOnly: nothing was changed"
        exit 0
    }

    $target = "C:\Windows\System32\HostDriverStore\FileRepository\$($package.Name)"
    Invoke-Command -Session $session -ArgumentList $target, $stale -ScriptBlock {
        param($Target, $Stale)
        New-Item -ItemType Directory -Force -Path $Target | Out-Null
        foreach ($rel in @($Stale)) {
            Remove-Item -LiteralPath (Join-Path $Target $rel) -Force -ErrorAction SilentlyContinue
        }
    }

    $copied = 0
    foreach ($file in $changed) {
        $destination = Join-Path $target $file.Rel
        $parent = Split-Path -Parent $destination
        if ($parent -ne $target) {
            Invoke-Command -Session $session -ArgumentList $parent -ScriptBlock {
                param($Parent); New-Item -ItemType Directory -Force -Path $Parent | Out-Null
            }
        }
        Copy-Item -LiteralPath $file.Full -Destination $destination -ToSession $session -Force
        $copied += 1
        if ($copied % 25 -eq 0) { Write-Sync "copied $copied/$($changed.Count)" }
    }
    Write-Sync "copied $copied driver file(s)"

    # The loose runtime DLLs beside the package matter as much as the package
    # itself: nvapi64.dll and friends are version-locked to nvlddmkm.sys, and a
    # mismatched pair fails exactly as loudly as a missing package, which is to
    # say not at all.
    foreach ($pair in @(
        @{ Source = (Join-Path $env:WINDIR "System32");  Destination = "C:\Windows\System32" },
        @{ Source = (Join-Path $env:WINDIR "SysWOW64"); Destination = "C:\Windows\SysWOW64" }
    )) {
        $loose = @(Get-ChildItem -LiteralPath $pair.Source -File -Filter "nv*" -ErrorAction SilentlyContinue)
        foreach ($file in $loose) {
            Copy-Item -LiteralPath $file.FullName `
                -Destination (Join-Path $pair.Destination $file.Name) `
                -ToSession $session -Force -ErrorAction SilentlyContinue
        }
        Write-Sync "copied $($loose.Count) nv* file(s) into $($pair.Destination)"
    }

    $manifest = [pscustomobject]@{
        synced_at = (Get-Date).ToUniversalTime().ToString("o")
        host_driver_version = $hostVersion
        package = $package.Name
        files_copied = $copied
        files_removed = $stale.Count
    } | ConvertTo-Json
    Invoke-Command -Session $session -ArgumentList $manifest -ScriptBlock {
        param($Json)
        New-Item -ItemType Directory -Force -Path "C:\ForzaAutomation" | Out-Null
        Set-Content -LiteralPath "C:\ForzaAutomation\gpu_pv_driver_sync.json" -Value $Json -Encoding UTF8
    }
}
finally {
    if ($session) { Remove-PSSession $session }
}

if ($NoRestart) {
    Write-Sync "-NoRestart: the guest must be rebooted before the new driver loads"
    exit 0
}

# The adapter only re-enumerates on a clean guest boot; toggling the device is
# not enough once the driver files changed underneath it.
Write-Sync "restarting $VMName so the guest re-enumerates the adapter"
Stop-VM -Name $VMName -Force
Start-VM -Name $VMName

$session = Connect-Guest -Name $VMName -Credential $credential -TimeoutMinutes 6
try {
    $after = Get-GuestAdapterState -Session $session
    Write-Sync "guest adapter after: status=$($after.Status) problem=$($after.Problem) driver=$($after.DriverVersion)"
    if ($after.Status -eq "OK" -and $after.Problem -eq "0") {
        Write-Sync "GPU-PV is healthy"
        exit 0
    }
    Write-Sync "GPU-PV is STILL broken -- do not run the game until this is resolved"
    exit 3
}
finally {
    if ($session) { Remove-PSSession $session }
}
