<#
.SYNOPSIS
    Report why keyboard input is not reaching Forza in the VM.

.DESCRIPTION
    The navigation automation focuses the game with SetForegroundWindow and then
    sends keys with WScript.Shell. Both steps fail silently: SendKeys reports
    success whether or not anything received the input, so a stuck title screen
    looks identical to a menu that simply did not respond.

    PowerShell Direct runs in session 0, where Get-Process reports
    MainWindowHandle as 0 for a process in the interactive session, so focus
    problems cannot be diagnosed from the host at all. This runs as an
    interactive scheduled task in the console session, enumerates the real
    top-level windows, tries to focus the game, sends a key, and reports what the
    foreground window was before and after. The log is left in place.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\diagnose_vm_forza_input.ps1 -Key ENTER
#>
[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = ".\admin",
    [ValidateSet("NONE", "ENTER", "DOWN", "ESC", "SPACE", "A")]
    [string] $Key = "NONE",
    [int] $SettleMs = 2500
)

$ErrorActionPreference = "Stop"

function Write-Diag {
    param([Parameter(Mandatory = $true)][string] $Message)
    Write-Host "[forza-input-diag] $Message"
}

$credential = [pscredential]::new($VMUser, [Security.SecureString]::new())
$session = New-PSSession -VMName $VMName -Credential $credential
$stamp = Get-Date -Format "yyyyMMdd_HHmmss_fff"
$guestRoot = "C:\ForzaAutomation\input_diag"
$guestRunner = Join-Path $guestRoot "diag_$stamp.ps1"
$guestReport = Join-Path $guestRoot "diag_$stamp.json"
$taskName = "ForzaInputDiag_$stamp"

# Built as a here-string so it runs inside the interactive session, where window
# handles and foreground state are actually visible.
$runner = @"
`$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class WinDiag {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    public static List<string> Windows() {
        var found = new List<string>();
        EnumWindows((h, p) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            var title = new StringBuilder(512); GetWindowTextW(h, title, 512);
            var cls = new StringBuilder(256); GetClassNameW(h, cls, 256);
            found.Add(string.Format("{0}|{1}|{2}|{3}|{4}", h.ToInt64(), pid, IsWindowVisible(h), cls.ToString(), title.ToString()));
            return true;
        }, IntPtr.Zero);
        return found;
    }
    public static string Describe(IntPtr h) {
        uint pid; GetWindowThreadProcessId(h, out pid);
        var title = new StringBuilder(512); GetWindowTextW(h, title, 512);
        var cls = new StringBuilder(256); GetClassNameW(h, cls, 256);
        return string.Format("{0}|{1}|{2}|{3}", h.ToInt64(), pid, cls.ToString(), title.ToString());
    }
}
'@

`$forza = Get-Process -Name forzahorizon6 -ErrorAction SilentlyContinue | Sort-Object StartTime -Descending | Select-Object -First 1
`$result = [ordered]@{
    ran_at = (Get-Date).ToString('o')
    session_id = (Get-Process -Id `$PID).SessionId
    forza_pid = if (`$forza) { `$forza.Id } else { `$null }
    forza_main_window_handle = if (`$forza) { [int64]`$forza.MainWindowHandle } else { `$null }
    foreground_before = [WinDiag]::Describe([WinDiag]::GetForegroundWindow())
    all_windows = @([WinDiag]::Windows())
}

# Any top-level window belonging to the game process, visible or not.
`$candidates = @()
if (`$forza) {
    `$candidates = @(`$result.all_windows | Where-Object { (`$_ -split '\|')[1] -eq [string]`$forza.Id })
}
`$result['forza_windows'] = `$candidates

`$focused = `$false
`$target = [IntPtr]::Zero
foreach (`$entry in `$candidates) {
    `$parts = `$entry -split '\|'
    if (`$parts[2] -ne 'True') { continue }
    `$target = [IntPtr][int64]`$parts[0]
    [void][WinDiag]::ShowWindow(`$target, 9)
    [void][WinDiag]::BringWindowToTop(`$target)
    `$focused = [WinDiag]::SetForegroundWindow(`$target)
    if (`$focused) { break }
}
`$result['focus_target'] = if (`$target -ne [IntPtr]::Zero) { [WinDiag]::Describe(`$target) } else { '' }
`$result['set_foreground_succeeded'] = `$focused
Start-Sleep -Milliseconds 400
`$result['foreground_after_focus'] = [WinDiag]::Describe([WinDiag]::GetForegroundWindow())

if ('$Key' -ne 'NONE') {
    `$map = @{ ENTER = '{ENTER}'; DOWN = '{DOWN}'; ESC = '{ESC}'; SPACE = ' '; A = 'a' }
    try {
        [System.Windows.Forms.SendKeys]::SendWait(`$map['$Key'])
        `$result['sendkeys_error'] = ''
    } catch {
        `$result['sendkeys_error'] = `$_.Exception.Message
    }
    Start-Sleep -Milliseconds $SettleMs
    `$result['foreground_after_key'] = [WinDiag]::Describe([WinDiag]::GetForegroundWindow())
    `$result['key_sent'] = '$Key'
}

`$result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath '$guestReport' -Encoding UTF8
"@

try {
    Invoke-Command -Session $session -ArgumentList $guestRoot, $guestRunner, $runner -ScriptBlock {
        param($Root, $RunnerPath, $Content)
        New-Item -ItemType Directory -Force -Path $Root | Out-Null
        Set-Content -LiteralPath $RunnerPath -Value $Content -Encoding UTF8
    }

    Write-Diag "running diagnostic in the interactive console session"
    Invoke-Command -Session $session -ArgumentList $taskName, $guestRunner -ScriptBlock {
        param($TaskName, $RunnerPath)
        $action = New-ScheduledTaskAction -Execute "powershell.exe" `
            -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$RunnerPath`""
        $principal = New-ScheduledTaskPrincipal -UserId "admin" -LogonType Interactive -RunLevel Highest
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
        Register-ScheduledTask -TaskName $TaskName -Action $action -Principal $principal -Settings $settings -Force | Out-Null
        Start-ScheduledTask -TaskName $TaskName
    } | Out-Null

    $report = $null
    for ($attempt = 0; $attempt -lt 40; $attempt += 1) {
        Start-Sleep -Seconds 2
        $report = Invoke-Command -Session $session -ArgumentList $guestReport -ScriptBlock {
            param($Path)
            if (Test-Path -LiteralPath $Path) { Get-Content -LiteralPath $Path -Raw } else { $null }
        }
        if ($report) { break }
    }

    Invoke-Command -Session $session -ArgumentList $taskName -ScriptBlock {
        param($TaskName)
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    }

    if (-not $report) {
        throw "The diagnostic task did not produce $guestReport."
    }

    $parsed = $report | ConvertFrom-Json
    Write-Diag "session id inside task : $($parsed.session_id)"
    Write-Diag "forza pid              : $($parsed.forza_pid)"
    Write-Diag "MainWindowHandle       : $($parsed.forza_main_window_handle)"
    Write-Diag "foreground before      : $($parsed.foreground_before)"
    Write-Diag "focus target           : $($parsed.focus_target)"
    Write-Diag "SetForegroundWindow    : $($parsed.set_foreground_succeeded)"
    Write-Diag "foreground after focus : $($parsed.foreground_after_focus)"
    if ($parsed.key_sent) {
        Write-Diag "key sent               : $($parsed.key_sent)"
        Write-Diag "sendkeys error         : $($parsed.sendkeys_error)"
        Write-Diag "foreground after key   : $($parsed.foreground_after_key)"
    }
    Write-Diag "forza top-level windows (handle|pid|visible|class|title):"
    foreach ($entry in @($parsed.forza_windows)) { Write-Host "    $entry" }
    Write-Diag "guest report: $guestReport"
} finally {
    if ($session) { Remove-PSSession $session }
}
