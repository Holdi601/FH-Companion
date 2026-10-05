<#
.SYNOPSIS
    Do nothing but scroll the leaderboard down, for a fixed time.

.DESCRIPTION
    Deliberately the smallest possible script: it presses and nothing else, so that
    whatever is reading -- the memory recorder or the frame capture -- runs in its own
    process and is never blocked by the pressing. That separation is the whole point;
    a single-threaded press-then-read loop was blind for ~1.2 s of every burst and
    lost most of the list.

    Measured on this build: the list advances about 3.6 rows per key event (it
    accelerates), and SendKeys paces itself at ~31 ms per key, so this scrolls at
    roughly 116 rows per second.

.EXAMPLE
    .\press_down_continuously.ps1 -Seconds 90
#>
[CmdletBinding()]
param(
    [int] $Seconds = 90,
    # Small bursts with a pause between them, because the list ACCELERATES under
    # continuous input: measured on Hokubu S1, chunks 1-4 advanced ~1,200-2,900 ranks
    # each and were captured almost completely, while chunks 5-12 advanced 3,000-4,500
    # each and yielded ~100 rows. The skipped ranks are never rendered at all, so no
    # capture rate can recover them -- the scroll has to stay slow enough to be drawn.
    [int] $KeysPerBurst = 20,
    [int] $PauseMs = 160
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms

$game = Get-Process -Name forzahorizon6 -ErrorAction Stop |
    Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $game) { throw "Forza has no main window; nothing to drive." }

Add-Type -Namespace PressWin -Name N -MemberDefinition @"
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr h);
[DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow();
"@

$handle = $game.MainWindowHandle
$deadline = (Get-Date).AddSeconds($Seconds)
$bursts = 0
while ((Get-Date) -lt $deadline) {
    if ([PressWin.N]::GetForegroundWindow() -ne $handle) {
        [void][PressWin.N]::SetForegroundWindow($handle)
        Start-Sleep -Milliseconds 50
    }
    [System.Windows.Forms.SendKeys]::SendWait("{DOWN $KeysPerBurst}")
    $bursts++
    if ($PauseMs -gt 0) { Start-Sleep -Milliseconds $PauseMs }
}
Write-Host "[press] $bursts burst(s) of $KeysPerBurst keys"
