[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = ".\admin",
    [switch] $BlankVMPassword,
    [string] $HostIp = "172.17.128.1",
    [string] $HostUser = "$env:COMPUTERNAME\$env:USERNAME",
    [switch] $UseLocalShareUser,
    [string] $ShareUserName = "ForzaVmShare",
    [string] $SharePassword = "",
    [string] $RunId = "20260609_170935_977_highway_circuit_d_09fcb916",
    [switch] $InstallSteam
)

$ErrorActionPreference = "Stop"

function ConvertFrom-SecureStringPlainText {
    param([Parameter(Mandatory = $true)][securestring] $SecureString)

    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureString)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)
    }
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
        Write-Host "[vm-bootstrap] update local share user $env:COMPUTERNAME\$Name"
        Set-LocalUser -Name $Name -Password $secure -PasswordNeverExpires $true
        if (-not $existing.Enabled) {
            Enable-LocalUser -Name $Name
        }
    } else {
        Write-Host "[vm-bootstrap] create local share user $env:COMPUTERNAME\$Name"
        New-LocalUser -Name $Name -Password $secure -FullName "Forza VM Share" -Description "SMB account for the Forza capture VM" -PasswordNeverExpires | Out-Null
    }

    $account = "$env:COMPUTERNAME\$Name"
    $capturePath = (Get-SmbShare -Name "ForzaCapture" -ErrorAction Stop).Path
    $repoPath = (Get-SmbShare -Name "ForzaRepo" -ErrorAction Stop).Path

    Write-Host "[vm-bootstrap] grant share access to $account"
    Grant-SmbShareAccess -Name "ForzaCapture" -AccountName $account -AccessRight Change -Force | Out-Null
    Grant-SmbShareAccess -Name "ForzaRepo" -AccountName $account -AccessRight Read -Force | Out-Null

    Write-Host "[vm-bootstrap] grant NTFS access to $account"
    & icacls $capturePath /grant "${account}:(OI)(CI)M" | Out-Null
    & icacls $repoPath /grant "${account}:(OI)(CI)RX" | Out-Null

    return $account
}

if ($BlankVMPassword) {
    Write-Host "[vm-bootstrap] using blank VM password for $VMUser"
    $vmCredential = [pscredential]::new($VMUser, [Security.SecureString]::new())
} else {
    Write-Host "[vm-bootstrap] asking for VM Windows credentials"
    $vmCredential = Get-Credential -UserName $VMUser -Message "Windows account inside $VMName"
}

if ($UseLocalShareUser) {
    if ([string]::IsNullOrWhiteSpace($SharePassword)) {
        $SharePassword = New-SharePassword
    }
    $HostUser = Ensure-HostShareUser -Name $ShareUserName -Password $SharePassword
    $hostPassword = $SharePassword
} else {
    Write-Host "[vm-bootstrap] asking for host share credentials"
    $hostCredential = Get-Credential -UserName $HostUser -Message "Host share credentials for \\$HostIp\ForzaCapture and \\$HostIp\ForzaRepo"
    $HostUser = $hostCredential.UserName
    $hostPassword = ConvertFrom-SecureStringPlainText -SecureString $hostCredential.Password
}

$scriptBlock = {
    param(
        [string] $HostIp,
        [string] $HostUser,
        [string] $HostPassword,
        [string] $RunId,
        [bool] $InstallSteam
    )

    $ErrorActionPreference = "Stop"
    function Write-Step {
        param([Parameter(Mandatory = $true)][string] $Message)
        Write-Host "[guest-bootstrap] $Message"
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
            throw "net.exe $($Arguments[0]) $($Arguments[1]) failed with exit code $exitCode. Output: $($output -join ' | ')"
        }
        return $output
    }

    Write-Step "disable sleep and hibernate"
    powercfg /hibernate off | Out-Null
    powercfg /change monitor-timeout-ac 0 | Out-Null
    powercfg /change standby-timeout-ac 0 | Out-Null
    powercfg /change disk-timeout-ac 0 | Out-Null

    Write-Step "disable Edge first-run noise"
    New-Item -Path "HKLM:\SOFTWARE\Policies\Microsoft\Edge" -Force | Out-Null
    New-ItemProperty -Path "HKLM:\SOFTWARE\Policies\Microsoft\Edge" -Name "HideFirstRunExperience" -PropertyType DWord -Value 1 -Force | Out-Null
    New-ItemProperty -Path "HKLM:\SOFTWARE\Policies\Microsoft\Edge" -Name "DefaultBrowserSettingEnabled" -PropertyType DWord -Value 0 -Force | Out-Null
    New-Item -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" -Force | Out-Null
    New-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" -Name "SubscribedContent-338389Enabled" -PropertyType DWord -Value 0 -Force | Out-Null

    Write-Step "map host shares"
    Invoke-NetUseCommand -Arguments @("use", "Z:", "/delete", "/y") -IgnoreExit | Out-Null
    Invoke-NetUseCommand -Arguments @("use", "Y:", "/delete", "/y") -IgnoreExit | Out-Null
    Invoke-NetUseCommand -Arguments @("use", "Z:", "\\$HostIp\ForzaCapture", "/user:$HostUser", $HostPassword, "/persistent:yes") | Out-Null
    Invoke-NetUseCommand -Arguments @("use", "Y:", "\\$HostIp\ForzaRepo", "/user:$HostUser", $HostPassword, "/persistent:yes") | Out-Null

    if (-not (Test-Path "Z:\")) {
        throw "Z: was not mapped."
    }
    if (-not (Test-Path "Y:\scripts\run_leaderboard_rank_scan.ps1")) {
        throw "Y: was not mapped to the Forza repo."
    }

    Write-Step "write share test"
    "VM write test $(Get-Date -Format o)" | Set-Content -LiteralPath "Z:\vm_write_test.txt" -Encoding UTF8

    Write-Step "set execution policy"
    Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope LocalMachine -Force

    $desktop = [Environment]::GetFolderPath("Desktop")
    $captureCmdPath = Join-Path $desktop "Run Forza Capture Resume.cmd"
    @"
@echo off
cd /d Y:\
powershell -NoProfile -ExecutionPolicy Bypass -File "Y:\scripts\run_leaderboard_rank_scan.ps1" -ResumeFrom "Z:\rank_scans\$RunId\state.json" -SkipExtract
pause
"@ | Set-Content -LiteralPath $captureCmdPath -Encoding ASCII

    @"
Run this on the HOST, not inside the VM:

powershell -NoProfile -ExecutionPolicy Bypass -File "<repo>\scripts\run_host_extract_from_vm_captures.ps1" -StatePath "<repo>\data\vm_share\rank_scans\$RunId\state.json" -Watch
"@ | Set-Content -LiteralPath (Join-Path $desktop "HOST extractor command.txt") -Encoding UTF8

    $gpuInfo = @(Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion, AdapterRAM)
    $wingetExists = [bool](Get-Command winget -ErrorAction SilentlyContinue)
    if ($InstallSteam -and $wingetExists) {
        Write-Step "install Steam via winget"
        winget install --id Valve.Steam -e --accept-package-agreements --accept-source-agreements
    }

    $result = [pscustomobject]@{
        completed_at = (Get-Date).ToUniversalTime().ToString("o")
        host_ip = $HostIp
        mapped_z = (Test-Path "Z:\")
        mapped_y = (Test-Path "Y:\scripts\run_leaderboard_rank_scan.ps1")
        capture_shortcut = $captureCmdPath
        winget_available = $wingetExists
        gpu = $gpuInfo
    }
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath "Z:\guest_bootstrap_result.json" -Encoding UTF8
    $result
}

try {
    Invoke-Command -VMName $VMName -Credential $vmCredential -ScriptBlock $scriptBlock -ArgumentList $HostIp, $hostCredential.UserName, $hostPassword, $RunId, ([bool]$InstallSteam) -ErrorAction Stop
}
catch {
    Write-Error ("VM bootstrap failed: {0}" -f $_.Exception.Message)
    if ($_.ErrorDetails) {
        Write-Error ("Details: {0}" -f $_.ErrorDetails.Message)
    }
    throw
}
