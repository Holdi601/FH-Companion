<#
.SYNOPSIS
    Scroll a leaderboard continuously while the reader records every row it sees.

.DESCRIPTION
    The previous loops pressed keys and read memory in the same thread, so they were
    blind while pressing: a 40-key burst takes ~1.24 s, and everything that scrolled
    past in between was lost -- 9% of ranks on a served board, 80% at a board start.
    Stride tuning could not fix that, because the stride was never the problem.

    The arithmetic that matters: the game advances one row per key event and Windows
    cannot repeat a key faster than ~31 ms, so a row is on screen -- and in the row
    pool -- for at least 31 ms. A reader sampling every 16 ms therefore cannot miss
    one. That is what this does:

      * `forza_row_server.py` records on its own thread at 16 ms and keeps every rank
      * this script does nothing but press DOWN, continuously
      * progress is checked a few times a second from the recorder's counters, which
        costs one tiny request instead of shipping rows over the pipe

    So the ceiling is the key rate, ~1,900 ranks/min, and the gaps should be close to
    zero rather than a tuning exercise.

.EXAMPLE
    .\forza_stream_scan.ps1 -Output C:\ForzaAutomation\data\stream\rows.jsonl
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Output,
    [string] $Summary,
    [string] $Python = "C:\ForzaTools\Python312\python.exe",
    [string] $ServerScript = "C:\ForzaAutomation\scripts\forza_row_server.py",
    [string] $ProfilePath = "C:\ForzaAutomation\build_profile.json",
    [int] $ReadRecords = 240,
    [double] $PollMs = 16,
    # Keys per burst between two progress checks. Big enough that SendKeys is not
    # interrupted constantly, small enough to notice the end of the board quickly.
    [int] $KeysPerBurst = 60,
    [int] $MaxSeconds = 2400,
    # The board is done when the highest rank has not moved for this long despite
    # keys going in. Generous, because a late page is normal.
    [int] $StallSeconds = 20,
    [switch] $ScanInvalidLaps
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms

function Write-Stream { param([string] $Message) Write-Host "[stream] $Message" }

$game = Get-Process -Name forzahorizon6 -ErrorAction Stop |
    Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $game) { throw "Forza has no main window; nothing to drive." }

Add-Type -Namespace StreamWin -Name N -MemberDefinition @"
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr h);
[DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow();
"@

$script:handle = $game.MainWindowHandle
$script:gamePid = $game.Id

function Focus-Game {
    if ([StreamWin.N]::GetForegroundWindow() -eq $script:handle) { return }
    $ok = [StreamWin.N]::SetForegroundWindow($script:handle)
    if (-not $ok) {
        $shell = New-Object -ComObject WScript.Shell
        $ok = $shell.AppActivate([int]$script:gamePid)
    }
    if (-not $ok) { Write-Stream "WARNING focus did not stick" }
    Start-Sleep -Milliseconds 60
}

$info = New-Object System.Diagnostics.ProcessStartInfo
$info.FileName = $Python
$info.Arguments = "`"$ServerScript`" --profile `"$ProfilePath`" --read-records $ReadRecords"
$info.RedirectStandardInput = $true
$info.RedirectStandardOutput = $true
$info.RedirectStandardError = $true
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$server = [System.Diagnostics.Process]::Start($info)

function Ask {
    param([hashtable] $Request)
    $server.StandardInput.WriteLine(($Request | ConvertTo-Json -Compress))
    $server.StandardInput.Flush()
    $line = $server.StandardOutput.ReadLine()
    if ($null -eq $line) { throw "the row server closed its output" }
    return $line | ConvertFrom-Json
}

try {
    $ready = $server.StandardOutput.ReadLine() | ConvertFrom-Json
    if (-not $ready.ok) { throw "row server did not come up: $($ready.reason)" }
    Write-Stream "reader ready: stride $($ready.stride), $($ready.addresses) address(es)"

    $started = Ask @{ record = @{ poll_ms = $PollMs } }
    if (-not $started.ok) { throw "recorder did not start: $($started.reason)" }
    Write-Stream "recording every $($started.poll_ms) ms; pressing DOWN continuously"

    $runStarted = Get-Date
    $lastMax = 0
    $lastProgress = Get-Date
    $bursts = 0

    while ($true) {
        Focus-Game
        # One call for the whole burst: SendKeys paces itself at ~31 ms per key and
        # adding our own sleep on top only makes the scroll slower.
        [System.Windows.Forms.SendKeys]::SendWait("{DOWN $KeysPerBurst}")
        $bursts = $bursts + 1

        $stats = Ask @{ stats = 1 }
        if ($stats.ok) {
            if ([int]$stats.max_rank -gt $lastMax) {
                $lastMax = [int]$stats.max_rank
                $lastProgress = Get-Date
            }
            if ($bursts % 10 -eq 0) {
                $minutes = [Math]::Max(0.001, ((Get-Date) - $runStarted).TotalMinutes)
                Write-Stream ("rows={0} max={1} missing={2} named={3} polls={4} rate={5:N0}/min" -f `
                    $stats.rows, $stats.max_rank, $stats.missing_in_span, $stats.named,
                    $stats.polls, ($stats.rows / $minutes))
            }
            if (-not $ScanInvalidLaps -and [int]$stats.invalid_from -gt 0) {
                Write-Stream "invalid laps from rank $($stats.invalid_from); the valid section is done"
                break
            }
        }

        if (((Get-Date) - $lastProgress).TotalSeconds -ge $StallSeconds) {
            Write-Stream "no new rank for $StallSeconds s at $lastMax; treating the board as finished"
            break
        }
        if (((Get-Date) - $runStarted).TotalSeconds -ge $MaxSeconds) {
            Write-Stream "reached the time limit at rank $lastMax"
            break
        }
    }

    # Let the recorder catch the tail of what is still resident before it stops.
    Start-Sleep -Milliseconds 400
    $written = Ask @{ write = $Output }
    [void](Ask @{ stop_record = 1 })
    $minutes = [Math]::Max(0.001, ((Get-Date) - $runStarted).TotalMinutes)
    Write-Stream ("done: {0} rows, ranks {1}..{2}, {3} missing, {4} named, {5} polls, {6:N1} min -> {7:N0}/min" -f `
        $written.written, $written.min_rank, $written.max_rank, $written.missing_in_span,
        $written.named, $written.polls, $minutes, ($written.written / $minutes))

    if ($Summary) {
        [pscustomobject]@{
            rows = $written.written
            min_rank = $written.min_rank
            max_rank = $written.max_rank
            missing_in_span = $written.missing_in_span
            named = $written.named
            polls = $written.polls
            decoded = $written.decoded
            invalid_from = $written.invalid_from
            bursts = $bursts
            keys_per_burst = $KeysPerBurst
            poll_ms = $PollMs
            minutes = [Math]::Round($minutes, 2)
            rows_per_min = [Math]::Round(($written.written / $minutes), 0)
        } | ConvertTo-Json | Set-Content -LiteralPath $Summary -Encoding UTF8
    }
} finally {
    try { $server.StandardInput.WriteLine('{"quit":1}'); $server.StandardInput.Flush() } catch {}
    Start-Sleep -Milliseconds 400
    if (-not $server.HasExited) { $server.Kill() }
}
