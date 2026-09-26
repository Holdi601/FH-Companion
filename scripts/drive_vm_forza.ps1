<#
.SYNOPSIS
    Focus Forza in the VM, optionally wait for on-screen text, send keys, and
    return a screenshot plus OCR of the result.

.DESCRIPTION
    Replaces the fragile focus-then-fire pattern that broke the learned route on
    build 6.420.696.0. Two problems it fixes:

    1. The old path called SetForegroundWindow on `Get-Process().MainWindowHandle`,
       which is 0 when queried across sessions, and SendKeys reports success
       whether or not anything received the input. A stuck screen was therefore
       indistinguishable from a screen that did not respond. This enumerates the
       game's real top-level windows in the interactive session and restores,
       raises, and focuses the visible one, reporting whether focus succeeded.

    2. The route sent the title-screen ENTER once, while the game was still
       loading, then waited for a menu that never came and timed out after
       dozens of identical OCR passes. `-RepeatUntilText` keeps re-sending the
       key until the expected text actually appears, which is what the game's
       variable load time requires.

    All waiting happens inside the guest task, so the host does not sleep.

.EXAMPLE
    # Get past the title screen however long it takes to load
    .\scripts\drive_vm_forza.ps1 -Key ENTER -RepeatUntilText 'Continue|Options|Exit' -TimeoutSeconds 180

.EXAMPLE
    # Just look at the current screen
    .\scripts\drive_vm_forza.ps1 -CaptureOnly
#>
[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = ".\admin",
    [ValidateSet("UP", "DOWN", "LEFT", "RIGHT", "ENTER", "ESC", "SPACE", "TAB",
                 "A", "B", "D", "E", "F", "Q", "X", "Y", "Z", "PAGEDOWN", "PAGEUP")]
    [string] $Key = "",
    [int] $Count = 1,
    [int] $KeyDelayMs = 120,
    [int] $SettleMs = 1200,
    [string] $WaitForText = "",
    [string] $RepeatUntilText = "",
    [int] $TimeoutSeconds = 120,
    [int] $PollSeconds = 3,
    [switch] $CaptureOnly,
    [string] $HostOutputRoot = "data/runtime/vm_drive"
)

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path

function Write-Drive {
    param([Parameter(Mandatory = $true)][string] $Message)
    Write-Host "[forza-drive] $Message"
}

if (-not $CaptureOnly -and -not $Key) {
    throw "Pass -Key, or -CaptureOnly to only look at the screen."
}

$stamp = Get-Date -Format "yyyyMMdd_HHmmss_fff"
$hostRoot = if ([IO.Path]::IsPathRooted($HostOutputRoot)) {
    $HostOutputRoot
} else {
    [IO.Path]::GetFullPath((Join-Path $workspace $HostOutputRoot))
}
New-Item -ItemType Directory -Force -Path $hostRoot | Out-Null

$credential = [pscredential]::new($VMUser, [Security.SecureString]::new())
$session = New-PSSession -VMName $VMName -Credential $credential
$guestRoot = "C:\ForzaAutomation\drive"
$guestRunner = "$guestRoot\drive_$stamp.ps1"
$guestPng = "$guestRoot\drive_$stamp.png"
$guestOcr = "$guestRoot\drive_$stamp.ocr.txt"
$guestReport = "$guestRoot\drive_$stamp.json"
$taskName = "ForzaDrive_$stamp"

$keyMap = @{
    UP = "{UP}"; DOWN = "{DOWN}"; LEFT = "{LEFT}"; RIGHT = "{RIGHT}"
    ENTER = "{ENTER}"; ESC = "{ESC}"; SPACE = " "; TAB = "{TAB}"
    PAGEDOWN = "{PGDN}"; PAGEUP = "{PGUP}"
    A = "a"; B = "b"; D = "d"; E = "e"; F = "f"; Q = "q"; X = "x"; Y = "y"; Z = "z"
}
$keySequence = if ($Key) { $keyMap[$Key] } else { "" }

$runner = @"
`$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class ForzaDriveWin {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    public static IntPtr FindGameWindow(uint wantedPid) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, p) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid != wantedPid || !IsWindowVisible(h)) return true;
            var cls = new StringBuilder(256); GetClassNameW(h, cls, 256);
            // The game's real window is class "App"; it also owns a
            // "ForzaFullscreenShadeWindow" that must not be focused.
            if (cls.ToString() == "App") { found = h; return false; }
            if (found == IntPtr.Zero) found = h;
            return true;
        }, IntPtr.Zero);
        return found;
    }
    public static bool Focus(IntPtr h) {
        if (h == IntPtr.Zero) return false;
        ShowWindow(h, 9);
        BringWindowToTop(h);
        return SetForegroundWindow(h);
    }
}
'@

function Get-ScreenText {
    param([string] `$PngPath, [string] `$OcrPath)
    `$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    `$bmp = New-Object System.Drawing.Bitmap(`$bounds.Width, `$bounds.Height)
    `$gfx = [System.Drawing.Graphics]::FromImage(`$bmp)
    `$gfx.CopyFromScreen(`$bounds.Location, [System.Drawing.Point]::Empty, `$bounds.Size)
    `$gfx.Dispose()
    `$bmp.Save(`$PngPath, [System.Drawing.Imaging.ImageFormat]::Png)
    `$bmp.Dispose()
    # windows_ocr.ps1 emits JSON, so pull the recognised lines out of it; the raw
    # JSON would make every regex match on field names instead of screen text.
    `$text = ''
    try {
        `$raw = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File 'C:\ForzaAutomation\scripts\windows_ocr.ps1' -ImagePath `$PngPath 2>&1 | Out-String
        `$parsed = `$raw | ConvertFrom-Json
        `$text = (@(`$parsed.lines | ForEach-Object { `$_.text }) -join "`n")
    } catch {
        `$text = ''
    }
    Set-Content -LiteralPath `$OcrPath -Value `$text -Encoding UTF8
    return `$text
}

`$forza = Get-Process -Name forzahorizon6 -ErrorAction SilentlyContinue |
    Sort-Object StartTime -Descending | Select-Object -First 1
`$report = [ordered]@{
    ran_at = (Get-Date).ToString('o')
    forza_pid = if (`$forza) { `$forza.Id } else { `$null }
    focused = `$false
    key = '$Key'
    attempts = 0
    matched = `$false
    timed_out = `$false
}
if (-not `$forza) {
    `$report['error'] = 'forzahorizon6 is not running'
    `$report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath '$guestReport' -Encoding UTF8
    exit 1
}

`$window = [ForzaDriveWin]::FindGameWindow([uint32]`$forza.Id)
`$report['focused'] = [ForzaDriveWin]::Focus(`$window)
Start-Sleep -Milliseconds 300

`$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
`$text = ''

if ('$WaitForText' -ne '') {
    # Wait for a state to appear without sending anything.
    do {
        `$text = Get-ScreenText -PngPath '$guestPng' -OcrPath '$guestOcr'
        `$report.attempts += 1
        if (`$text -match '$WaitForText') { `$report['matched'] = `$true; break }
        Start-Sleep -Seconds $PollSeconds
    } while ((Get-Date) -lt `$deadline)
    if (-not `$report['matched']) { `$report['timed_out'] = `$true }
}
elseif ('$RepeatUntilText' -ne '') {
    # Keep sending the key until the expected screen actually appears. The game's
    # load time varies, so a single keypress is not enough.
    do {
        `$text = Get-ScreenText -PngPath '$guestPng' -OcrPath '$guestOcr'
        if (`$text -match '$RepeatUntilText') { `$report['matched'] = `$true; break }
        [void][ForzaDriveWin]::Focus(`$window)
        for (`$i = 0; `$i -lt $Count; `$i += 1) {
            [System.Windows.Forms.SendKeys]::SendWait('$keySequence')
            Start-Sleep -Milliseconds $KeyDelayMs
        }
        `$report.attempts += 1
        Start-Sleep -Milliseconds $SettleMs
        Start-Sleep -Seconds $PollSeconds
    } while ((Get-Date) -lt `$deadline)
    if (-not `$report['matched']) {
        `$text = Get-ScreenText -PngPath '$guestPng' -OcrPath '$guestOcr'
        if (`$text -match '$RepeatUntilText') { `$report['matched'] = `$true }
        else { `$report['timed_out'] = `$true }
    }
}
elseif ('$Key' -ne '') {
    for (`$i = 0; `$i -lt $Count; `$i += 1) {
        [System.Windows.Forms.SendKeys]::SendWait('$keySequence')
        Start-Sleep -Milliseconds $KeyDelayMs
    }
    `$report.attempts = 1
    Start-Sleep -Milliseconds $SettleMs
    `$text = Get-ScreenText -PngPath '$guestPng' -OcrPath '$guestOcr'
}
else {
    `$text = Get-ScreenText -PngPath '$guestPng' -OcrPath '$guestOcr'
}

`$report['screen_text'] = `$text
`$report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath '$guestReport' -Encoding UTF8
exit 0
"@

try {
    Invoke-Command -Session $session -ArgumentList $guestRoot, $guestRunner, $runner -ScriptBlock {
        param($Root, $RunnerPath, $Content)
        New-Item -ItemType Directory -Force -Path $Root | Out-Null
        Set-Content -LiteralPath $RunnerPath -Value $Content -Encoding UTF8
    }

    $limitMinutes = [Math]::Max(3, [int][Math]::Ceiling(($TimeoutSeconds + 90) / 60))
    Invoke-Command -Session $session -ArgumentList $taskName, $guestRunner, $limitMinutes -ScriptBlock {
        param($TaskName, $RunnerPath, $LimitMinutes)
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
        $action = New-ScheduledTaskAction -Execute "powershell.exe" `
            -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$RunnerPath`""
        $principal = New-ScheduledTaskPrincipal -UserId "admin" -LogonType Interactive -RunLevel Highest
        $settings = New-ScheduledTaskSettingsSet `
            -ExecutionTimeLimit (New-TimeSpan -Minutes $LimitMinutes) -MultipleInstances IgnoreNew
        Register-ScheduledTask -TaskName $TaskName -Action $action -Principal $principal -Settings $settings -Force | Out-Null
        Start-ScheduledTask -TaskName $TaskName
    } | Out-Null

    $waitLimit = (Get-Date).AddSeconds($TimeoutSeconds + 120)
    $report = $null
    do {
        Start-Sleep -Seconds 3
        $state = Invoke-Command -Session $session -ArgumentList $taskName, $guestReport -ScriptBlock {
            param($TaskName, $ReportPath)
            $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
            [pscustomobject]@{
                State = if ($task) { [string]$task.State } else { "Missing" }
                Report = if (Test-Path -LiteralPath $ReportPath) {
                    Get-Content -LiteralPath $ReportPath -Raw
                } else { $null }
            }
        }
        if ($state.Report) { $report = $state.Report; break }
    } while ((Get-Date) -lt $waitLimit)

    Invoke-Command -Session $session -ArgumentList $taskName -ScriptBlock {
        param($TaskName)
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    }

    if (-not $report) {
        throw "The drive task did not produce $guestReport."
    }

    foreach ($item in @(@{ Guest = $guestPng; Name = "current.png" },
                        @{ Guest = $guestOcr; Name = "current.ocr.txt" })) {
        $exists = Invoke-Command -Session $session -ArgumentList $item.Guest -ScriptBlock {
            param($Path); Test-Path -LiteralPath $Path
        }
        if ($exists) {
            Copy-Item -FromSession $session -LiteralPath $item.Guest `
                -Destination (Join-Path $hostRoot $item.Name) -Force
        }
    }
    Invoke-Command -Session $session -ArgumentList $guestRoot -ScriptBlock {
        param($Root)
        Get-ChildItem -LiteralPath $Root -File -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTime -lt (Get-Date).AddMinutes(-10) } |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }

    $parsed = $report | ConvertFrom-Json
    Write-Drive "pid=$($parsed.forza_pid) focused=$($parsed.focused) key=$($parsed.key) attempts=$($parsed.attempts) matched=$($parsed.matched) timedOut=$($parsed.timed_out)"
    Write-Drive "screenshot: $(Join-Path $hostRoot 'current.png')"
    Write-Drive "screen text:"
    ($parsed.screen_text -split "`r?`n" | Where-Object { $_ -match '\S' }) |
        ForEach-Object { Write-Host "    $_" }
    if ($parsed.timed_out) { exit 2 }
    exit 0
} finally {
    if ($session) { Remove-PSSession $session }
}
