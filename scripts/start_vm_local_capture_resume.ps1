[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = ".\admin",
    [string] $RunId = "20260609_170935_977_highway_circuit_d_09fcb916",
    [string] $StatePath = "",
    [string] $GuestWorkspace = "C:\ForzaAutomation",
    [string] $GuestCaptureRoot = "C:\ForzaCaptures",
    [string] $TaskName = "ForzaCaptureResumeLocal",
    [switch] $StartHostSync,
    [switch] $StartHostExtractor,
    [switch] $SkipCopy,
    [switch] $RestartGame,
    [switch] $NoMinimizeVmConnect
)

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path

function Resolve-WorkspacePath {
    param([Parameter(Mandatory = $true)][string] $Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }
    return [System.IO.Path]::GetFullPath((Join-Path $workspace $Path))
}

function Write-Start {
    param([Parameter(Mandatory = $true)][string] $Message)
    Write-Host "[vm-local-start] $Message"
}

function New-BlankCredential {
    param([Parameter(Mandatory = $true)][string] $User)
    if ($User -notmatch '^[^\\]+\\' -and $User -notmatch '@') {
        $User = ".\$User"
    }
    return [pscredential]::new($User, [Security.SecureString]::new())
}

function Test-CanAccessVm {
    param([Parameter(Mandatory = $true)][string] $Name)
    try {
        Get-VM -Name $Name -ErrorAction Stop | Out-Null
        return $true
    } catch {
        return $false
    }
}

function Ensure-Property {
    param(
        [Parameter(Mandatory = $true)] $Object,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)] $Value
    )

    if ($Object.PSObject.Properties.Name -contains $Name) {
        $Object.$Name = $Value
    } else {
        $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
    }
}

function Minimize-VmConnect {
    if ($NoMinimizeVmConnect) {
        return
    }
    try {
        Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class ForzaVmWindowTools {
    [DllImport("user32.dll")]
    public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
}
"@ -ErrorAction SilentlyContinue
        Get-Process -Name vmconnect -ErrorAction SilentlyContinue | ForEach-Object {
            if ($_.MainWindowHandle -ne [IntPtr]::Zero) {
                [ForzaVmWindowTools]::ShowWindowAsync($_.MainWindowHandle, 6) | Out-Null
                Write-Start "minimized VMConnect window (pid $($_.Id))"
            }
        }
    } catch {
        Write-Warning "Could not minimize VMConnect: $($_.Exception.Message)"
    }
}

function Wait-PowerShellDirect {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)] $Credential
    )

    for ($i = 0; $i -lt 60; $i += 1) {
        try {
            Invoke-Command -VMName $Name -Credential $Credential -ScriptBlock { $env:COMPUTERNAME } -ErrorAction Stop | Out-Null
            return
        } catch {
            Start-Sleep -Seconds 2
        }
    }
    throw "PowerShell Direct did not become ready for $Name."
}

if ([string]::IsNullOrWhiteSpace($StatePath)) {
    $StatePath = Join-Path $workspace "data/vm_share/rank_scans/$RunId/state.json"
}
$StatePath = Resolve-WorkspacePath $StatePath
if (-not (Test-Path -LiteralPath $StatePath)) {
    throw "State file not found: $StatePath"
}

Minimize-VmConnect

if (-not (Test-CanAccessVm -Name $VMName)) {
    throw "This shell cannot access Hyper-V VM '$VMName' without elevation. Open a Hyper-V-enabled shell (member of Hyper-V Administrators) and run it again."
}

$vm = Get-VM -Name $VMName -ErrorAction Stop
if ($vm.State -ne "Running") {
    Write-Start "start VM $VMName"
    Start-VM -Name $VMName
}

$credential = New-BlankCredential -User $VMUser
Write-Start "wait for PowerShell Direct"
Wait-PowerShellDirect -Name $VMName -Credential $credential

$session = $null
$hostRunRoot = Resolve-WorkspacePath (Join-Path "data/vm_share/rank_scans" $RunId)
$hostLogRoot = Resolve-WorkspacePath (Join-Path "data/background/vm_local_capture" $RunId)
New-Item -ItemType Directory -Force -Path $hostRunRoot, (Join-Path $hostRunRoot "chunks"), $hostLogRoot | Out-Null

try {
    $session = New-PSSession -VMName $VMName -Credential $credential

    $guestRunRoot = Join-Path (Join-Path $GuestCaptureRoot "rank_scans") $RunId
    $guestProcessedRoot = Join-Path (Join-Path $GuestCaptureRoot "processed") $RunId
    $guestRunnerPath = Join-Path $GuestWorkspace "run_capture_resume_local.ps1"
    $guestStatePath = Join-Path $guestRunRoot "state.json"

    Write-Start "prepare guest directories"
    Invoke-Command -Session $session -ArgumentList $GuestWorkspace, $GuestCaptureRoot, $guestRunRoot, $guestProcessedRoot -ScriptBlock {
        param([string] $Workspace, [string] $CaptureRoot, [string] $RunRoot, [string] $ProcessedRoot)
        New-Item -ItemType Directory -Force -Path $Workspace, $CaptureRoot, $RunRoot, (Join-Path $RunRoot "logs"), (Join-Path $RunRoot "chunks"), $ProcessedRoot | Out-Null
    }

    if (-not $SkipCopy) {
        Write-Start "copy scripts/config to guest via PowerShell Direct"
        foreach ($name in @("scripts", "config")) {
            $source = Join-Path $workspace $name
            Copy-Item -ToSession $session -LiteralPath $source -Destination $GuestWorkspace -Recurse -Force
        }
    }

    Write-Start "write local guest resume state"
    $state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
    Ensure-Property -Object $state -Name "run_id" -Value $RunId
    Ensure-Property -Object $state -Name "state_path" -Value $guestStatePath
    Ensure-Property -Object $state -Name "run_root" -Value $guestRunRoot
    Ensure-Property -Object $state -Name "runtime_dir" -Value (Join-Path $guestRunRoot "runtime")
    Ensure-Property -Object $state -Name "capture_chunks_dir" -Value (Join-Path $guestRunRoot "chunks")
    Ensure-Property -Object $state -Name "processed_run_root" -Value $guestProcessedRoot
    Ensure-Property -Object $state -Name "processed_chunks_dir" -Value (Join-Path $guestProcessedRoot "chunks")
    Ensure-Property -Object $state -Name "combined_output_dir" -Value (Join-Path $guestProcessedRoot "combined")
    Ensure-Property -Object $state -Name "combined_parquet_path" -Value (Join-Path $guestProcessedRoot "leaderboard_entries.parquet")
    Ensure-Property -Object $state -Name "vm_capture_only" -Value $true
    Ensure-Property -Object $state -Name "psdirect_capture" -Value $true
    Ensure-Property -Object $state -Name "prepared_for_vm_at" -Value ((Get-Date).ToUniversalTime().ToString("o"))
    Ensure-Property -Object $state -Name "prepared_from_state" -Value $StatePath

    $tempState = Join-Path $hostLogRoot "guest_state.json"
    $state | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $tempState -Encoding UTF8
    Copy-Item -ToSession $session -LiteralPath $tempState -Destination $guestStatePath -Force
    Copy-Item -LiteralPath $tempState -Destination (Join-Path $hostRunRoot "state.json") -Force

    $restartGameLiteral = if ($RestartGame) { '$true' } else { '$false' }
    $runner = @"
`$ErrorActionPreference = "Stop"
`$workspace = "$GuestWorkspace"
`$runRoot = "$guestRunRoot"
`$statePath = "$guestStatePath"
`$logRoot = Join-Path `$runRoot "logs"
New-Item -ItemType Directory -Force -Path `$logRoot | Out-Null
`$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
`$transcript = Join-Path `$logRoot "capture_`$stamp.transcript.log"
Start-Transcript -LiteralPath `$transcript -Force | Out-Null
try {
    Set-Location `$workspace
    Write-Host "[vm-runner] started `$((Get-Date).ToString("o"))"
    Write-Host "[vm-runner] workspace `$workspace"
    Write-Host "[vm-runner] state `$statePath"
    if ($restartGameLiteral) {
        if (Get-Process -Name forzahorizon6 -ErrorAction SilentlyContinue) {
            Write-Host "[vm-runner] stop existing Forza process"
            & "`$env:SystemRoot\System32\taskkill.exe" /F /T /IM forzahorizon6.exe 2>`$null | Out-Host
            for (`$i = 0; `$i -lt 20; `$i += 1) {
                if (-not (Get-Process -Name forzahorizon6 -ErrorAction SilentlyContinue)) {
                    break
                }
                Start-Sleep -Milliseconds 500
            }
        }
        Start-Sleep -Seconds 3
    }
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path `$workspace "scripts\run_leaderboard_rank_scan.ps1") -ResumeFrom `$statePath -SkipExtract -NoDisplayManage
    if (`$LASTEXITCODE -ne 0) {
        throw "run_leaderboard_rank_scan.ps1 failed with exit code `$LASTEXITCODE"
    }
    Write-Host "[vm-runner] completed `$((Get-Date).ToString("o"))"
    exit 0
} catch {
    Write-Host "[vm-runner] ERROR `$(`$_.Exception.Message)"
    exit 1
} finally {
    Stop-Transcript | Out-Null
}
"@
    Invoke-Command -Session $session -ArgumentList $guestRunnerPath, $runner -ScriptBlock {
        param([string] $Path, [string] $Content)
        Set-Content -LiteralPath $Path -Value $Content -Encoding UTF8
    }

    Write-Start "register interactive guest task $TaskName"
    Invoke-Command -Session $session -ArgumentList $TaskName, $guestRunnerPath -ScriptBlock {
        param([string] $TaskName, [string] $RunnerPath)

        Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
        Stop-ScheduledTask -TaskName "ForzaCaptureResume" -ErrorAction SilentlyContinue

        if (-not (Test-Path -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon")) {
            New-Item -Path "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" -Force | Out-Null
        }
        New-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" -Name "AutoAdminLogon" -PropertyType String -Value "1" -Force | Out-Null
        New-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" -Name "DefaultUserName" -PropertyType String -Value "admin" -Force | Out-Null
        New-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" -Name "DefaultPassword" -PropertyType String -Value "" -Force | Out-Null
        New-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" -Name "DefaultDomainName" -PropertyType String -Value $env:COMPUTERNAME -Force | Out-Null

        $action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$RunnerPath`""
        $principal = New-ScheduledTaskPrincipal -UserId "$env:COMPUTERNAME\admin" -LogonType Interactive -RunLevel Limited
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 72) -MultipleInstances IgnoreNew
        Register-ScheduledTask -TaskName $TaskName -Action $action -Principal $principal -Settings $settings -Force | Out-Null
        Start-ScheduledTask -TaskName $TaskName
        Start-Sleep -Seconds 2
        $task = Get-ScheduledTask -TaskName $TaskName
        $info = Get-ScheduledTaskInfo -TaskName $TaskName
        [pscustomobject]@{
            TaskName = $task.TaskName
            State = [string]$task.State
            LastRunTime = $info.LastRunTime
            LastTaskResult = $info.LastTaskResult
            Action = ($task.Actions | ForEach-Object { "$($_.Execute) $($_.Arguments)" }) -join "; "
        }
    } | Format-List | Out-String | ForEach-Object { Write-Host $_.TrimEnd() }

    $syncProcess = $null
    if ($StartHostSync) {
        $syncScript = Join-Path $PSScriptRoot "sync_vm_capture_via_psdirect.ps1"
        $syncOut = Join-Path $hostLogRoot "host_sync.out.log"
        $syncErr = Join-Path $hostLogRoot "host_sync.err.log"
        Write-Start "start host PSDirect sync watcher"
        $syncProcess = Start-Process powershell -ArgumentList @(
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-File", "`"$syncScript`"",
            "-VMName", "`"$VMName`"",
            "-VMUser", "`"$VMUser`"",
            "-RunId", "`"$RunId`"",
            "-GuestRunRoot", "`"$guestRunRoot`"",
            "-HostRunRoot", "`"$hostRunRoot`"",
            "-Watch"
        ) -RedirectStandardOutput $syncOut -RedirectStandardError $syncErr -WindowStyle Hidden -PassThru
    }

    $extractProcess = $null
    if ($StartHostExtractor) {
        $extractScript = Join-Path $PSScriptRoot "run_host_extract_from_vm_captures.ps1"
        $extractOut = Join-Path $hostLogRoot "host_extract.out.log"
        $extractErr = Join-Path $hostLogRoot "host_extract.err.log"
        Write-Start "start host extractor watcher"
        $extractProcess = Start-Process powershell -ArgumentList @(
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-File", "`"$extractScript`"",
            "-StatePath", "`"$(Join-Path $hostRunRoot "state.json")`"",
            "-Watch"
        ) -RedirectStandardOutput $extractOut -RedirectStandardError $extractErr -WindowStyle Hidden -PassThru
    }

    [pscustomobject]@{
        vm = $VMName
        task = $TaskName
        guest_workspace = $GuestWorkspace
        guest_run_root = $guestRunRoot
        host_run_root = $hostRunRoot
        host_logs = $hostLogRoot
        sync_pid = if ($syncProcess) { $syncProcess.Id } else { $null }
        extractor_pid = if ($extractProcess) { $extractProcess.Id } else { $null }
        state = Join-Path $hostRunRoot "state.json"
    } | ConvertTo-Json -Depth 5
}
finally {
    if ($null -ne $session) {
        Remove-PSSession $session
    }
}

Write-Start "done"
