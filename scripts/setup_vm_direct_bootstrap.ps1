[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = ".\admin",
    [switch] $BlankVMPassword,
    [string] $HostIp = "172.17.128.1",
    [string] $ShareUserName = "ForzaVmShare",
    [string] $SharePassword = "",
    [string] $RunId = "20260609_170935_977_highway_circuit_d_09fcb916"
)

$ErrorActionPreference = "Stop"

function Write-Bootstrap {
    param([Parameter(Mandatory = $true)][string] $Message)
    Write-Host "[vm-direct-bootstrap] $Message"
}

function New-SharePassword {
    return ("FzA9" + ([guid]::NewGuid().ToString("N")))
}

function Ensure-HostShareUser {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $Password
    )

    $secure = ConvertTo-SecureString $Password -AsPlainText -Force
    $existing = Get-LocalUser -Name $Name -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Bootstrap "update local share user $env:COMPUTERNAME\$Name"
        Set-LocalUser -Name $Name -Password $secure -PasswordNeverExpires $true
        Enable-LocalUser -Name $Name
    } else {
        Write-Bootstrap "create local share user $env:COMPUTERNAME\$Name"
        New-LocalUser -Name $Name -Password $secure -FullName "Forza VM Share" -Description "SMB account for the Forza capture VM" -PasswordNeverExpires | Out-Null
    }

    $account = "$env:COMPUTERNAME\$Name"
    $capturePath = (Get-SmbShare -Name "ForzaCapture" -ErrorAction Stop).Path
    $repoPath = (Get-SmbShare -Name "ForzaRepo" -ErrorAction Stop).Path

    Write-Bootstrap "grant share access to $account"
    Grant-SmbShareAccess -Name "ForzaCapture" -AccountName $account -AccessRight Change -Force | Out-Null
    Grant-SmbShareAccess -Name "ForzaRepo" -AccountName $account -AccessRight Read -Force | Out-Null

    Write-Bootstrap "grant NTFS access to $account"
    & icacls $capturePath /grant "${account}:(OI)(CI)M" | Out-Null
    & icacls $repoPath /grant "${account}:(OI)(CI)RX" | Out-Null

    return $account
}

if ([string]::IsNullOrWhiteSpace($SharePassword)) {
    $SharePassword = New-SharePassword
}

$hostShareUser = Ensure-HostShareUser -Name $ShareUserName -Password $SharePassword

if ($BlankVMPassword) {
    Write-Bootstrap "use blank VM password for $VMUser"
    $vmCredential = [pscredential]::new($VMUser, [Security.SecureString]::new())
} else {
    Write-Bootstrap "ask for VM Windows credentials"
    $vmCredential = Get-Credential -UserName $VMUser -Message "Windows account inside $VMName"
}

$guestScript = {
    param(
        [string] $HostIp,
        [string] $HostShareUser,
        [string] $HostSharePassword,
        [string] $RunId
    )

    $ErrorActionPreference = "Stop"

    function Write-GuestStep {
        param([Parameter(Mandatory = $true)][string] $Message)
        Write-Host "[guest-direct-bootstrap] $Message"
    }

    function Invoke-NetUseCommand {
        param(
            [Parameter(Mandatory = $true)][string[]] $Arguments,
            [switch] $IgnoreExit
        )

        $oldPreference = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        try {
            $output = @(& net.exe @Arguments 2>&1 | ForEach-Object { [string]$_ })
            $exitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $oldPreference
        }

        if (-not $IgnoreExit -and $exitCode -ne 0) {
            throw "net.exe $($Arguments -join ' ') failed with exit code $exitCode. Output: $($output -join ' | ')"
        }
        return $output
    }

    Write-GuestStep "disable sleep and hibernate"
    powercfg /hibernate off | Out-Null
    powercfg /change monitor-timeout-ac 0 | Out-Null
    powercfg /change standby-timeout-ac 0 | Out-Null
    powercfg /change disk-timeout-ac 0 | Out-Null

    Write-GuestStep "disable Edge first-run noise"
    New-Item -Path "HKLM:\SOFTWARE\Policies\Microsoft\Edge" -Force | Out-Null
    New-ItemProperty -Path "HKLM:\SOFTWARE\Policies\Microsoft\Edge" -Name "HideFirstRunExperience" -PropertyType DWord -Value 1 -Force | Out-Null
    New-ItemProperty -Path "HKLM:\SOFTWARE\Policies\Microsoft\Edge" -Name "DefaultBrowserSettingEnabled" -PropertyType DWord -Value 0 -Force | Out-Null
    New-Item -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" -Force | Out-Null
    New-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" -Name "SubscribedContent-338389Enabled" -PropertyType DWord -Value 0 -Force | Out-Null

    Write-GuestStep "save SMB credentials"
    & cmdkey.exe /delete:$HostIp 2>$null | Out-Null
    & cmdkey.exe /add:$HostIp /user:$HostShareUser /pass:$HostSharePassword | Out-Null

    Write-GuestStep "map host shares"
    Invoke-NetUseCommand -Arguments @("use", "Z:", "/delete", "/y") -IgnoreExit | Out-Null
    Invoke-NetUseCommand -Arguments @("use", "Y:", "/delete", "/y") -IgnoreExit | Out-Null
    Invoke-NetUseCommand -Arguments @("use", "Z:", "\\$HostIp\ForzaCapture", "/user:$HostShareUser", $HostSharePassword, "/persistent:yes") | Out-Null
    Invoke-NetUseCommand -Arguments @("use", "Y:", "\\$HostIp\ForzaRepo", "/user:$HostShareUser", $HostSharePassword, "/persistent:yes") | Out-Null

    Write-GuestStep "verify mapped paths"
    if (-not (Test-Path "Z:\")) {
        throw "Z: was not mapped."
    }
    if (-not (Test-Path "Y:\scripts\run_leaderboard_rank_scan.ps1")) {
        throw "Y: was not mapped to the Forza repo."
    }

    "VM write test $(Get-Date -Format o)" | Set-Content -LiteralPath "Z:\vm_write_test.txt" -Encoding UTF8

    Write-GuestStep "set execution policy"
    Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope LocalMachine -Force

    Write-GuestStep "write desktop commands"
    $desktop = [Environment]::GetFolderPath("Desktop")
    $captureCmdPath = Join-Path $desktop "Run Forza Capture Resume.cmd"
    @"
@echo off
net use Z: \\$HostIp\ForzaCapture /persistent:yes
net use Y: \\$HostIp\ForzaRepo /persistent:yes
cd /d Y:\
powershell -NoProfile -ExecutionPolicy Bypass -File "Y:\scripts\run_leaderboard_rank_scan.ps1" -ResumeFrom "Z:\rank_scans\$RunId\state.json" -SkipExtract
pause
"@ | Set-Content -LiteralPath $captureCmdPath -Encoding ASCII

    @"
Run this on the HOST, not inside the VM:

powershell -NoProfile -ExecutionPolicy Bypass -File "<repo>\scripts\run_host_extract_from_vm_captures.ps1" -StatePath "<repo>\data\vm_share\rank_scans\$RunId\state.json" -Watch
"@ | Set-Content -LiteralPath (Join-Path $desktop "HOST extractor command.txt") -Encoding UTF8

    Write-GuestStep "register interactive capture task"
    $taskName = "ForzaCaptureResume"
    $taskAction = New-ScheduledTaskAction -Execute "cmd.exe" -Argument "/c `"$captureCmdPath`""
    $taskPrincipal = New-ScheduledTaskPrincipal -UserId "$env:COMPUTERNAME\admin" -LogonType Interactive -RunLevel Highest
    $taskSettings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 72)
    Register-ScheduledTask -TaskName $taskName -Action $taskAction -Principal $taskPrincipal -Settings $taskSettings -Force | Out-Null

    $gpuInfo = @(Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion, AdapterRAM)
    $result = [pscustomobject]@{
        completed_at = (Get-Date).ToUniversalTime().ToString("o")
        host_ip = $HostIp
        share_user = $HostShareUser
        mapped_z = (Test-Path "Z:\")
        mapped_y = (Test-Path "Y:\scripts\run_leaderboard_rank_scan.ps1")
        capture_shortcut = $captureCmdPath
        scheduled_task = $taskName
        host_extract_state = "<repo>\data\vm_share\rank_scans\$RunId\state.json"
        vm_resume_state = "Z:\rank_scans\$RunId\state.json"
        winget_available = [bool](Get-Command winget -ErrorAction SilentlyContinue)
        gpu = $gpuInfo
    }
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath "Z:\guest_bootstrap_result.json" -Encoding UTF8
    $result
}

Invoke-Command -VMName $VMName -Credential $vmCredential -ScriptBlock $guestScript -ArgumentList $HostIp, $hostShareUser, $SharePassword, $RunId -ErrorAction Stop
