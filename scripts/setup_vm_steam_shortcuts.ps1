[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = ".\admin",
    [string] $GameAppId = "2483190",
    [string] $GameName = "Forza Horizon 6"
)

$ErrorActionPreference = "Stop"

function Write-Setup {
    param([Parameter(Mandatory = $true)][string] $Message)
    Write-Host "[vm-steam-shortcuts] $Message"
}

$credential = [pscredential]::new($VMUser, [Security.SecureString]::new())

$guestScript = {
    param(
        [string] $GameAppId,
        [string] $GameName
    )

    $ErrorActionPreference = "Stop"

    function Write-GuestSetup {
        param([Parameter(Mandatory = $true)][string] $Message)
        Write-Host "[guest-steam-shortcuts] $Message"
    }

    $steamCandidates = @(
        "C:\Program Files (x86)\Steam\steam.exe",
        "C:\Program Files\Steam\steam.exe",
        "C:\Steam\steam.exe"
    )
    $steam = $steamCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $steam) {
        throw "Steam executable was not found."
    }

    $desktop = [Environment]::GetFolderPath("Desktop")

    Write-GuestSetup "write Steam and game shortcuts"
    @"
@echo off
start "" "$steam"
"@ | Set-Content -LiteralPath (Join-Path $desktop "Launch Steam.cmd") -Encoding ASCII

    @"
@echo off
start "" "steam://install/$GameAppId"
"@ | Set-Content -LiteralPath (Join-Path $desktop "Install $GameName.cmd") -Encoding ASCII

    @"
@echo off
start "" "$steam" -applaunch $GameAppId
"@ | Set-Content -LiteralPath (Join-Path $desktop "Run $GameName.cmd") -Encoding ASCII

    Write-GuestSetup "register interactive Steam launcher task"
    $taskPrincipal = New-ScheduledTaskPrincipal -UserId "$env:COMPUTERNAME\admin" -LogonType Interactive -RunLevel Highest
    $taskSettings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 24)

    $steamTaskName = "ForzaLaunchSteam"
    $steamTaskAction = New-ScheduledTaskAction -Execute $steam
    Register-ScheduledTask -TaskName $steamTaskName -Action $steamTaskAction -Principal $taskPrincipal -Settings $taskSettings -Force | Out-Null

    $runGameTaskName = "ForzaRunGame"
    $runGameTaskAction = New-ScheduledTaskAction -Execute $steam -Argument "-applaunch $GameAppId"
    Register-ScheduledTask -TaskName $runGameTaskName -Action $runGameTaskAction -Principal $taskPrincipal -Settings $taskSettings -Force | Out-Null

    [pscustomobject]@{
        steam = $steam
        desktop = $desktop
        steam_task = $steamTaskName
        run_game_task = $runGameTaskName
        install_command = "steam://install/$GameAppId"
        run_command = "steam://rungameid/$GameAppId"
    }
}

Write-Setup "configure Steam shortcuts in $VMName"
Invoke-Command -VMName $VMName -Credential $credential -ScriptBlock $guestScript -ArgumentList $GameAppId, $GameName -ErrorAction Stop
