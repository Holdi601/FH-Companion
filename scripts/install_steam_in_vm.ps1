[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = ".\admin",
    [string] $InstallerUrl = "https://cdn.fastly.steamstatic.com/client/installer/SteamSetup.exe"
)

$ErrorActionPreference = "Stop"

function Write-SteamInstall {
    param([Parameter(Mandatory = $true)][string] $Message)
    Write-Host "[steam-install] $Message"
}

$credential = [pscredential]::new($VMUser, [Security.SecureString]::new())

$guestScript = {
    param([string] $InstallerUrl)

    $ErrorActionPreference = "Stop"

    function Write-GuestSteamInstall {
        param([Parameter(Mandatory = $true)][string] $Message)
        Write-Host "[guest-steam-install] $Message"
    }

    $candidatePaths = @(
        (Join-Path ${env:ProgramFiles(x86)} "Steam\steam.exe"),
        (Join-Path $env:ProgramFiles "Steam\steam.exe"),
        "C:\Steam\steam.exe"
    )
    $existing = $candidatePaths | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if ($existing) {
        Write-GuestSteamInstall "Steam already installed at $existing"
        $steam = $existing
    } else {
        Write-GuestSteamInstall "download official Steam installer"
        $dest = Join-Path $env:TEMP "SteamSetup.exe"
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $InstallerUrl -OutFile $dest -UseBasicParsing

        Write-GuestSteamInstall "run silent installer"
        $process = Start-Process -FilePath $dest -ArgumentList "/S" -Wait -PassThru
        Write-GuestSteamInstall "installer exit=$($process.ExitCode)"

        $steam = $candidatePaths | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        if (-not $steam) {
            throw "Steam executable was not found after install."
        }
    }

    $desktop = [Environment]::GetFolderPath("Desktop")
    $launchCmd = Join-Path $desktop "Launch Steam.cmd"
    @"
@echo off
start "" "$steam"
"@ | Set-Content -LiteralPath $launchCmd -Encoding ASCII

    [pscustomobject]@{
        installed = $true
        steam = $steam
        launch_shortcut = $launchCmd
    }
}

Write-SteamInstall "install Steam in $VMName"
Invoke-Command -VMName $VMName -Credential $credential -ScriptBlock $guestScript -ArgumentList $InstallerUrl -ErrorAction Stop
