[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = ".\admin",
    [Parameter(Mandatory = $true)]
    [ValidateSet(
        "UP", "DOWN", "LEFT", "RIGHT", "ENTER", "ESC",
        "X", "Y", "A", "B", "D", "PAGEDOWN", "PAGEUP"
    )]
    [string] $Key,
    [int] $Count = 1,
    [int] $DelayMs = 100,
    [int] $SettleMs = 500
)

$ErrorActionPreference = "Stop"
if ($Count -lt 1 -or $DelayMs -lt 0 -or $SettleMs -lt 0) {
    throw "Count must be positive and delays cannot be negative."
}

$credential = [pscredential]::new(
    $VMUser,
    [Security.SecureString]::new()
)
$session = New-PSSession -VMName $VMName -Credential $credential
$stamp = Get-Date -Format "yyyyMMdd_HHmmss_fff"
$taskName = "ForzaKeys_$stamp"
$runnerPath = "C:\ForzaAutomation\send_keys_$stamp.ps1"
$logPath = "C:\ForzaAutomation\send_keys_$stamp.log"

try {
    $runner = @"
`$ErrorActionPreference = "Stop"
Start-Transcript -LiteralPath "$logPath" -Force | Out-Null
`$shell = New-Object -ComObject WScript.Shell
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Win32Focus {
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
}
'@
try {
    `$process = Get-Process -Name forzahorizon6 -ErrorAction Stop |
        Sort-Object MainWindowHandle -Descending |
        Select-Object -First 1
    Write-Host "Forza PID=`$(`$process.Id) Handle=`$(`$process.MainWindowHandle) Title=`$(`$process.MainWindowTitle)"
    `$focused = `$false
    if (`$process.MainWindowHandle -and `$process.MainWindowHandle -ne 0) {
        `$focused = [Win32Focus]::SetForegroundWindow([IntPtr]`$process.MainWindowHandle)
        Write-Host "SetForegroundWindow=`$focused"
    }
    if (-not `$focused) {
        `$focused = `$shell.AppActivate([int]`$process.Id)
        Write-Host "AppActivate(pid)=`$focused"
    }
    if (-not `$focused -and `$process.MainWindowTitle) {
        `$focused = `$shell.AppActivate([string]`$process.MainWindowTitle)
        Write-Host "AppActivate(title)=`$focused"
    }
    if (-not `$focused) {
        throw "Could not focus Forza Horizon 6."
    }
    Start-Sleep -Milliseconds 300
    for (`$i = 0; `$i -lt $Count; `$i += 1) {
        `$shell.SendKeys("{$Key}")
        if ($DelayMs -gt 0) {
            Start-Sleep -Milliseconds $DelayMs
        }
    }
    if ($SettleMs -gt 0) {
        Start-Sleep -Milliseconds $SettleMs
    }
} finally {
    Stop-Transcript | Out-Null
}
"@
    Invoke-Command -Session $session -ArgumentList $taskName, $runnerPath, $runner -ScriptBlock {
        param($TaskName, $RunnerPath, $Runner)
        Set-Content -LiteralPath $RunnerPath -Value $Runner -Encoding UTF8
        $action = New-ScheduledTaskAction `
            -Execute "powershell.exe" `
            -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$RunnerPath`""
        $principal = New-ScheduledTaskPrincipal `
            -UserId "$env:COMPUTERNAME\admin" `
            -LogonType Interactive `
            -RunLevel Limited
        $settings = New-ScheduledTaskSettingsSet `
            -ExecutionTimeLimit (New-TimeSpan -Minutes 5) `
            -MultipleInstances IgnoreNew
        Register-ScheduledTask `
            -TaskName $TaskName `
            -Action $action `
            -Principal $principal `
            -Settings $settings `
            -Force | Out-Null
        Start-ScheduledTask -TaskName $TaskName
    }

    do {
        Start-Sleep -Milliseconds 250
        $taskResult = Invoke-Command -Session $session -ArgumentList $taskName -ScriptBlock {
            param($TaskName)
            $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
            $info = Get-ScheduledTaskInfo -TaskName $TaskName -ErrorAction SilentlyContinue
            [pscustomobject]@{
                state = if ($task) { [string]$task.State } else { "Missing" }
                result = if ($info) { [int]$info.LastTaskResult } else { -1 }
            }
        }
    } while ($taskResult.state -eq "Running")

    if ($taskResult.result -ne 0) {
        $guestLog = Invoke-Command -Session $session -ArgumentList $logPath -ScriptBlock {
            param($LogPath)
            if (Test-Path -LiteralPath $LogPath) {
                Get-Content -LiteralPath $LogPath -Raw
            }
        }
        if ($guestLog) {
            Write-Host $guestLog
        }
        throw "Forza key task failed with result $($taskResult.result)."
    }
    Write-Host "[forza-keys] sent $Key x$Count"
} finally {
    if ($session) {
        Invoke-Command -Session $session -ArgumentList $taskName, $runnerPath, $logPath -ScriptBlock {
            param($TaskName, $RunnerPath, $LogPath)
            Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
            Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $RunnerPath -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $LogPath -Force -ErrorAction SilentlyContinue
        } -ErrorAction SilentlyContinue
        Remove-PSSession $session
    }
}
