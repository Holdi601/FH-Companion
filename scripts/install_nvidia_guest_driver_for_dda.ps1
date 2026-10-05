[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = ".\admin",
    [string] $DriverInfName = "nvmdi.inf",
    [string] $DriverVersionPattern = "",
    [string] $GuestDriverRoot = "C:\ForzaAutomation\drivers\nvidia",
    [switch] $RestartGuest
)

$ErrorActionPreference = "Stop"

function Write-GuestDriver {
    param([Parameter(Mandatory = $true)][string] $Message)
    Write-Host "[guest-nvidia-driver] $Message"
}

function New-BlankCredential {
    param([Parameter(Mandatory = $true)][string] $User)
    if ($User -notmatch '^[^\\]+\\' -and $User -notmatch '@') {
        $User = ".\$User"
    }
    return [pscredential]::new($User, [Security.SecureString]::new())
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

if ([string]::IsNullOrWhiteSpace($DriverVersionPattern)) {
    $DriverVersionPattern = Get-HostNvidiaDriverVersion
    Write-GuestDriver "detected host NVIDIA driver version: $DriverVersionPattern"
}

$driverFolder = Get-HostDriverStoreFolder -InfName $DriverInfName -VersionPattern $DriverVersionPattern
Write-GuestDriver "host driver folder: $($driverFolder.FullName)"

$credential = New-BlankCredential -User $VMUser
Write-GuestDriver "wait for PowerShell Direct"
$ready = $false
for ($i = 0; $i -lt 90; $i += 1) {
    try {
        Invoke-Command -VMName $VMName -Credential $credential -ScriptBlock { $env:COMPUTERNAME } -ErrorAction Stop | Out-Null
        $ready = $true
        break
    } catch {
        Start-Sleep -Seconds 2
    }
}
if (-not $ready) {
    throw "PowerShell Direct did not become ready for $VMName."
}

$session = $null
try {
    $session = New-PSSession -VMName $VMName -Credential $credential
    $guestTarget = Join-Path $GuestDriverRoot $driverFolder.Name
    Invoke-Command -Session $session -ArgumentList $GuestDriverRoot, $guestTarget -ScriptBlock {
        param([string] $Root, [string] $Target)
        Stop-Process -Name forzahorizon6 -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force -Path $Root | Out-Null
        if (Test-Path -LiteralPath $Target) {
            Remove-Item -LiteralPath $Target -Recurse -Force
        }
    }

    Write-GuestDriver "copy driver to guest: $guestTarget"
    Copy-Item -ToSession $session -LiteralPath $driverFolder.FullName -Destination $GuestDriverRoot -Recurse -Force

    Write-GuestDriver "install driver with pnputil"
    Invoke-Command -Session $session -ArgumentList (Join-Path $guestTarget $DriverInfName), $DriverVersionPattern -ScriptBlock {
        param([string] $InfPath, [string] $Version)
        if (-not (Test-Path -LiteralPath $InfPath)) {
            throw "Driver INF not found in guest: $InfPath"
        }
        $output = & pnputil.exe /add-driver $InfPath /install 2>&1
        $exitCode = $LASTEXITCODE
        $manifest = [pscustomobject]@{
            installed_at = (Get-Date).ToUniversalTime().ToString("o")
            inf_path = $InfPath
            driver_version = $Version
            exit_code = $exitCode
            output = @($output | ForEach-Object { [string]$_ })
            display_devices = @(Get-PnpDevice -Class Display -ErrorAction SilentlyContinue | Select-Object FriendlyName, Status, Problem, InstanceId)
        }
        $manifestPath = "C:\Windows\Temp\forza_nvidia_dda_driver_install.json"
        $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
        if ($exitCode -ne 0) {
            throw "pnputil failed with exit code $exitCode. Manifest: $manifestPath"
        }
        $manifest
    } | ConvertTo-Json -Depth 8 | Write-Host

    if ($RestartGuest) {
        Write-GuestDriver "restart guest"
        Invoke-Command -Session $session -ScriptBlock { Restart-Computer -Force }
    }
} finally {
    if ($session) {
        Remove-PSSession $session
    }
}

Write-GuestDriver "done"
