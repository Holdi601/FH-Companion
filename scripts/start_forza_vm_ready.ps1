[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = ".\admin",
    [string] $GameAppId = "2483190",
    [string] $GameName = "Forza Horizon 6",
    [string] $GuestWorkspace = "C:\ForzaAutomation",
    [int] $Width = 1920,
    [int] $Height = 1080,
    [int] $StartupWaitSeconds = 25,
    [string] $GpuInstancePattern = "VEN_10DE|NVIDIA|DEV_2C02",
    [int] $GpuResourcePercent = 100,
    [switch] $NoCleanDisplaySession,
    [switch] $NoCopy,
    [switch] $NoLaunch
)

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path

function Write-Ready {
    param([Parameter(Mandatory = $true)][string] $Message)
    Write-Host "[forza-vm-ready] $Message"
}

function New-BlankCredential {
    param([Parameter(Mandatory = $true)][string] $User)
    if ($User -notmatch '^[^\\]+\\' -and $User -notmatch '@') {
        $User = ".\$User"
    }
    return [pscredential]::new($User, [Security.SecureString]::new())
}

function Wait-PowerShellDirect {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)] $Credential
    )

    for ($i = 0; $i -lt 90; $i += 1) {
        try {
            Invoke-Command -VMName $Name -Credential $Credential -ScriptBlock { $env:COMPUTERNAME } -ErrorAction Stop | Out-Null
            return
        } catch {
            Start-Sleep -Seconds 2
        }
    }
    throw "PowerShell Direct did not become ready for $Name."
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

function Ensure-GpuPartition {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $Pattern,
        [Parameter(Mandatory = $true)][int] $Percent
    )

    Set-VM -Name $Name -GuestControlledCacheTypes $true -LowMemoryMappedIoSpace 1GB -HighMemoryMappedIoSpace 32GB | Out-Null

    $existing = @(Get-VMGpuPartitionAdapter -VMName $Name -ErrorAction SilentlyContinue)
    if ($existing.Count -gt 0) {
        Write-Ready "GPU-P adapter already present"
        return
    }

    if (-not (Get-Command -Name Get-VMHostPartitionableGpu -ErrorAction SilentlyContinue)) {
        throw "GPU-P cmdlets are not available on this host."
    }

    $gpu = Get-VMHostPartitionableGpu |
        Where-Object { [string]$_.Name -match $Pattern } |
        Select-Object -First 1
    if ($null -eq $gpu) {
        $available = (Get-VMHostPartitionableGpu | ForEach-Object { $_.Name }) -join [Environment]::NewLine
        throw "No partitionable GPU matched '$Pattern'. Available GPUs:`n$available"
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

    Write-Ready "add GPU-P adapter: $($gpu.Name)"
    Add-VMGpuPartitionAdapter @args | Out-Null
}

function Stop-GuestVm {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)] $Credential
    )

    $vm = Get-VM -Name $Name -ErrorAction Stop
    if ($vm.State -ne "Running") {
        return
    }

    Write-Ready "request guest shutdown"
    try {
        Invoke-Command -VMName $Name -Credential $Credential -ScriptBlock {
            Get-Process -Name forzahorizon6,steamwebhelper,GameBar,GameBarFTServer -ErrorAction SilentlyContinue |
                Stop-Process -Force -ErrorAction SilentlyContinue
            & "$env:SystemRoot\System32\shutdown.exe" /s /t 0 /f
        } -ErrorAction Stop | Out-Null
    } catch {
        Write-Warning "Guest shutdown request failed: $($_.Exception.Message)"
    }

    $deadline = (Get-Date).AddSeconds(45)
    do {
        Start-Sleep -Seconds 2
        $vm = Get-VM -Name $Name -ErrorAction Stop
    } while ($vm.State -ne "Off" -and (Get-Date) -lt $deadline)

    if ($vm.State -ne "Off") {
        Write-Ready "force stop VM"
        Stop-VM -Name $Name -Force
    }
}

if (-not (Get-VM -Name $VMName -ErrorAction SilentlyContinue)) {
    throw "VM not found: $VMName"
}

$credential = New-BlankCredential -User $VMUser
$vm = Get-VM -Name $VMName -ErrorAction Stop
$needsCleanStart = -not $NoCleanDisplaySession -and $vm.State -eq "Running"

if ($needsCleanStart -and $vm.State -eq "Running") {
    Stop-GuestVm -Name $VMName -Credential $credential
}

$vm = Get-VM -Name $VMName -ErrorAction Stop
if ($vm.State -eq "Off") {
    Write-Ready "configure VM transport/video/GPU"
    Set-VM -Name $VMName -EnhancedSessionTransportType VMBus -AutomaticCheckpointsEnabled $false -CheckpointType Disabled | Out-Null
    Set-VMVideo -VMName $VMName -ResolutionType Single -HorizontalResolution $Width -VerticalResolution $Height | Out-Null
    Ensure-GpuPartition -Name $VMName -Pattern $GpuInstancePattern -Percent $GpuResourcePercent
    Write-Ready "start VM"
    Start-VM -Name $VMName | Out-Null
} else {
    Write-Ready "VM already running; verify live settings"
    Set-VM -Name $VMName -EnhancedSessionTransportType VMBus -AutomaticCheckpointsEnabled $false -CheckpointType Disabled | Out-Null
    Set-VMVideo -VMName $VMName -ResolutionType Single -HorizontalResolution $Width -VerticalResolution $Height | Out-Null
    if (@(Get-VMGpuPartitionAdapter -VMName $VMName -ErrorAction SilentlyContinue).Count -eq 0) {
        throw "GPU-P adapter is missing while VM is running. Re-run without -NoCleanDisplaySession so the script can repair it while the VM is off."
    }
    Write-Ready "GPU-P adapter already present"
}

Write-Ready "wait for PowerShell Direct"
Wait-PowerShellDirect -Name $VMName -Credential $credential

$session = $null
try {
    $session = New-PSSession -VMName $VMName -Credential $credential
    if (-not $NoCopy) {
        Write-Ready "copy scripts/config to guest"
        Invoke-Command -Session $session -ArgumentList $GuestWorkspace -ScriptBlock {
            param([string] $Workspace)
            New-Item -ItemType Directory -Force -Path $Workspace | Out-Null
        }
        foreach ($name in @("scripts", "config")) {
            Copy-Item -ToSession $session -LiteralPath (Join-Path $workspace $name) -Destination $GuestWorkspace -Recurse -Force
        }
    }

    Write-Ready "ensure Steam/Forza scheduled tasks"
    Invoke-Command -Session $session -ArgumentList $GameAppId, $GameName -ScriptBlock {
        param([string] $AppId, [string] $Name)
        $steamCandidates = @(
            "C:\Program Files (x86)\Steam\steam.exe",
            "C:\Program Files\Steam\steam.exe",
            "C:\Steam\steam.exe"
        )
        $steam = $steamCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        if (-not $steam) {
            throw "Steam executable was not found in the guest."
        }

        $principal = New-ScheduledTaskPrincipal -UserId "$env:COMPUTERNAME\admin" -LogonType Interactive -RunLevel Highest
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 24) -MultipleInstances IgnoreNew
        Register-ScheduledTask -TaskName "ForzaLaunchSteam" -Action (New-ScheduledTaskAction -Execute $steam) -Principal $principal -Settings $settings -Force | Out-Null
        Register-ScheduledTask -TaskName "ForzaRunGame" -Action (New-ScheduledTaskAction -Execute $steam -Argument "-applaunch $AppId") -Principal $principal -Settings $settings -Force | Out-Null

        New-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" -Name "AutoAdminLogon" -PropertyType String -Value "1" -Force | Out-Null
        New-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" -Name "DefaultUserName" -PropertyType String -Value "admin" -Force | Out-Null
        New-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" -Name "DefaultPassword" -PropertyType String -Value "" -Force | Out-Null
        New-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" -Name "DefaultDomainName" -PropertyType String -Value $env:COMPUTERNAME -Force | Out-Null

        [pscustomobject]@{ steam = $steam; run_game_task = "ForzaRunGame" }
    } | Out-Null

    if (-not $NoLaunch) {
        Write-Ready "start Forza"
        Invoke-Command -Session $session -ScriptBlock {
            Start-ScheduledTask -TaskName "ForzaRunGame"
        }
        Start-Sleep -Seconds $StartupWaitSeconds
    }

    $guestStatus = Invoke-Command -Session $session -ScriptBlock {
        [pscustomobject]@{
            session = ((quser 2>$null) -join "`n")
            forza = @(Get-Process -Name forzahorizon6 -ErrorAction SilentlyContinue | Select-Object ProcessName, Id, Responding, MainWindowTitle)
            video = @(Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion, CurrentHorizontalResolution, CurrentVerticalResolution, Status)
        }
    }

    [pscustomobject]@{
        vm = Get-VM -Name $VMName | Select-Object Name, State, EnhancedSessionTransportType, ProcessorCount, MemoryAssigned
        vm_video = Get-VMVideo -VMName $VMName | Select-Object ResolutionType, HorizontalResolution, VerticalResolution
        gpu_partition = @(Get-VMGpuPartitionAdapter -VMName $VMName -ErrorAction SilentlyContinue | Select-Object InstancePath, OptimalPartitionVRAM, OptimalPartitionDecode, OptimalPartitionCompute)
        guest = $guestStatus
    } | ConvertTo-Json -Depth 8
}
finally {
    if ($null -ne $session) {
        Remove-PSSession $session
    }
}

Write-Ready "done"
