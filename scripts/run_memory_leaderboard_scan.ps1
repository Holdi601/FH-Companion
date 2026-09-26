[CmdletBinding()]
param(
    [string] $RouteConfig = "config/fh6_learned_route.json",
    [Parameter(Mandatory = $true)]
    [string] $Track,
    [string] $PerformanceClass = "D",
    [string] $RivalsMode = "Road Racing",
    [string] $TotalRanks = "Auto",
    [string] $RunId = "",
    [string] $OutputRoot = "data/memory_scans",
    # Keep rows whose gamertag has not been resolved yet. The name comes from a
    # separate profile lookup that lags behind fast scrolling, so discarding those
    # rows loses complete results -- rank, lap time, car and XUID are all there --
    # and makes a board that is still serving look finished. On 2026-08-21 fifteen
    # boards in a row reported 149 / 99 / 50 rows for hours, which is the count of
    # resolved NAMES, not of rows served.
    [switch] $AllowMissingGamertag,
    [int] $StartRank = 1,
    [int] $PrefetchMargin = 5,
    [int] $MaxDownBurst = 60,
    [int] $KeyDelayMs = 18,
    [int] $SettleMs = 350,
    # A board lists every VALID lap by time and then every INVALID one, which
    # restarts from a faster time. Verified on all 40 sweep runs (2026-08-22): not one
    # board has a valid lap after the first invalid one, and the invalid tail is 22.3%
    # of everything scanned. So the first invalid row is the end of the useful board
    # and the scan stops there. Pass -ScanInvalidLaps to page through it anyway.
    [switch] $ScanInvalidLaps,
    [int] $NoProgressLimit = 8,
    # "No rows arrived $NoProgressLimit times" is NOT "the board ended", and until
    # 2026-08-23 the loop treated it as one: it broke out and only THEN asked the
    # scrollbar, purely to LABEL the run. Every board that stopped with the thumb
    # mid-bar was recorded as truncated and never carried further, which is how 35
    # of 44 scanned boards ended up with an unconfirmed end. The scrollbar answer
    # now decides whether to STOP, not just what to call it: a thumb that is not at
    # the bottom rejects the end, waits, and keeps paging. Rejections are bounded so
    # a genuinely dead board still terminates.
    [int] $MaxEndRejections = 3,
    # How long to wait out a slow page before re-reading, per rejection. The waits
    # escalate (1x, 2x, 3x) because the pathology this exists for is a page the game
    # serves LATE, not one it never serves.
    [int] $EndRetryWaitSeconds = 60,
    # A Rivals board does not necessarily open at rank 1: it opens centred on the
    # player's OWN time. Reading the real position beats assuming one -- see the
    # top-align block below for what assuming it cost.
    # In UntilEnd mode the row reads cannot say how far along the board is, so
    # eta_minutes has always been null exactly when it would be most useful. The
    # scrollbar thumb knows: its position IS the progress fraction. Sampling it
    # occasionally costs one frame capture; 0 disables.
    # DISABLED BY DEFAULT (0). Sampling from inside the guest looked cheap and was
    # not: each sample spawns the capture automation and blocks the scan loop for
    # ~35 s, which on 2026-08-20 dropped a live A scan from ~600 ranks/min to 123
    # -- three hours instead of forty minutes for one board. The host can capture
    # the same frame and run the same detector without touching the scan loop, so
    # progress belongs there. Left in place for deliberate debugging only.
    [int] $ProgressCheckSeconds = 0,
    # Seek to a resume point by counting from the TOP instead of estimating the
    # current position. Measuring the position needs an open-mode read, which
    # returns some resident block rather than the one under the cursor, so on
    # 2026-08-21 the estimate never converged: six attempts, then 98 minutes of
    # reads for 700 ranks on both a deep (23,501) and a shallow (1,001) resume.
    # The top of the list is a KNOWN anchor and one key press is exactly one row,
    # so counting from there is deterministic. It costs presses, not guesses.
    [switch] $NoSeekViaTop,
    [switch] $NoStartAlign,
    [int] $StartAlignAttempts = 6,
    # Ceiling on one upward burst. Without it a corrupt StartRank could scroll for
    # hours; with it the worst case is bounded and visible in the log.
    [int] $MaxSeekUpKeys = 30000,
    # How many stalled reads may escalate to a full ~6 GB sweep before the rest
    # are cheap retries. See the stall comment in the paging loop.
    [int] $FullSweepStallLimit = 1,
    # Attempts to scroll back for a rank the scroll overshot. See the recovery
    # block in the paging loop.
    [int] $MaxGapRetries = 2,
    [int] $MaxIterations = 100000,
    [int] $MaxNearAddresses = 8,
    # Window searched around the last known block before falling back to a full
    # 6 GB sweep. Widened from 256 MB: on some boards (e.g. Highway Circuit) the
    # buffer relocates further than 256 MB between pages, so the near read missed
    # and every page paid for a full sweep -- throughput collapsed to ~67/min vs
    # ~740/min on boards that stayed within the window. A 1536 MB window still
    # costs a quarter of a full sweep but catches those larger hops.
    [int] $NearRadiusMb = 1536,
    # The buffer does not always land near the LAST place it was seen; on a long
    # board it cycles between a handful of arenas. Probing the most recent few
    # cached addresses costs a few seconds and saves a ~50 s full sweep whenever
    # the block is sitting at any of them.
    [int] $NearProbeCount = 3,
    # A full sweep walks the whole ~6 GB process, and at 16 MB per read that is
    # mostly syscall overhead. Bigger reads make the unavoidable sweeps cheaper,
    # which is what decides throughput on boards too large to stay cached.
    [int] $FullSweepChunkMb = 64,
    # How many consecutive failed reads before the view is allowed to creep PAST
    # the wanted rank. See the failure path at the bottom of the loop.
    [int] $StallNudgeAfter = 4,
    # How far past the wanted rank the cursor may be driven while reads keep
    # failing. The failure path now presses towards the wanted rank on the FIRST
    # miss instead of after four, and this is the ceiling that stops that from
    # becoming the unbounded drift which once cost the A board its throughput:
    # one page past the target and the pressing stops.
    [int] $MaxStallAdvance = 50,
    # How many pages must be delivered BY a stall advance before the jump at the
    # end of the loop starts parking the cursor past the page bottom by default.
    # Two, not one: a single late page on a healthy board is a slow fetch, not a
    # board that has stopped prefetching, and only the latter wants the change.
    [int] $CrossToFetchAfter = 2,
    # How many stalls may be recovered before the board counts as chronically
    # slow. The throughput watchdog's window is reset by a recovery -- otherwise
    # it stops the board in the very moment the recovery worked -- so something
    # else has to catch a board that stalls and recovers forever. 0 disables.
    [int] $MaxStallRecoveries = 4,
    # Self-healing floor. A board that has slowed to a crawl is in the same state
    # as one that stopped outright -- the game is not serving pages promptly any
    # more -- and re-entering the board is the proven cure for that (S1 stopped
    # dead at rank 1950 twice, and after re-entry served 1951-2000 at once). So
    # rather than grinding on at 50 ranks/min, stop, report "degraded", and let the
    # sweep's resume loop re-seat and continue from here. 0 disables the watchdog.
    # Deliberately low. Re-seating costs a navigation plus a seek back down to
    # where the pass stopped, so it must answer real pathology, not merely a
    # mediocre stretch: a page answered by the near read costs ~10 s, which is
    # 300/min, and tripping on that would re-seat forever.
    [int] $MinRanksPerMinute = 120,
    # And it may not fire until the pass has actually banked something. Without
    # this floor a pass that starts slow trips immediately, re-seats, starts slow
    # again, and the whole pass budget goes on 300-rank slices.
    [int] $MinGainBeforeWatchdog = 400,
    # Measured over this many PRODUCTIVE pages, so a slow ramp at the start or a
    # few failed reads cannot trip it -- only sustained slowness can.
    [int] $RateWindowPages = 6,
    # Seconds to wait at a cached exact address for the expected rank to appear.
    # Cut from 4 s: when the block has moved, that wait was dead time on every
    # page before the near/full fallback even started. A short poll then move on.
    # How long to WAIT at a known buffer address for the page to arrive. This was 1
    # second, and that single number was the whole throughput problem on a deep
    # board. Measured on the A board 2026-08-20: 213 productive reads used only 14
    # distinct addresses, and the entire second half of the run alternated between
    # exactly TWO -- so the address was always known and cached. Yet each page cost
    # ~60 s in a fixed cycle: exact read (1 s) plus near read (9 s) both empty, a
    # cheap retry (1 s) empty, then a ~50 s full sweep that found the block AT one
    # of those same two cached addresses. Nothing had moved; the game simply had not
    # finished fetching the page yet, and the sweep was an expensive way of waiting
    # for it. Waiting at the address instead returns as soon as the data lands --
    # usually well before the sweep would have -- and costs nothing when it is
    # already there. The end of a board now takes longer to establish, which is the
    # right trade: the alternative is calling a slow fetch an end of board.
    [int] $ExactWaitSeconds = 1,
    # The long wait belongs on the LAST cheap stage, not the first. Hanging it on
    # the opening exact read cost 30 s on every single page whose block had merely
    # moved a little -- the near read then found it instantly, so the wait bought
    # nothing and turned ~10 s pages into 40 s ones (300 ranks/min down to 75) on
    # the 2026-08-21 overnight run. Order that actually works: exact quickly, then
    # the near arenas, and only if both come up empty is the page genuinely not
    # fetched yet -- THEN wait for it, which is still far cheaper than a ~50 s
    # sweep that was only ever succeeding because it took long enough.
    [int] $SlowFetchWaitSeconds = 30,
    [string] $PythonExe = ""
)

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class ForzaMemoryFocus {
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
}
"@

function Resolve-WorkspacePath {
    param([Parameter(Mandatory = $true)][string] $Path)
    if ([IO.Path]::IsPathRooted($Path)) {
        return $Path
    }
    return [IO.Path]::GetFullPath((Join-Path $workspace $Path))
}

function Write-MemoryScan {
    param([Parameter(Mandatory = $true)][string] $Message)
    Write-Host "[forza-memory] $Message"
}

function Ensure-Property {
    param($Object, [string] $Name, $Value)
    if ($Object.PSObject.Properties.Name -contains $Name) {
        $Object.$Name = $Value
    } else {
        $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
    }
}

function Set-NestedProperty {
    param($Object, [string] $Parent, [string] $Name, $Value)
    if (-not ($Object.PSObject.Properties.Name -contains $Parent) -or $null -eq $Object.$Parent) {
        $Object | Add-Member -NotePropertyName $Parent -NotePropertyValue ([pscustomobject]@{})
    }
    Ensure-Property $Object.$Parent $Name $Value
}

function Focus-Forza {
    $process = Get-Process -Name forzahorizon6 -ErrorAction Stop |
        Where-Object { $_.MainWindowHandle -ne 0 } |
        Sort-Object StartTime -Descending |
        Select-Object -First 1
    if ($null -eq $process) {
        throw "Forza window was not found."
    }
    $focused = [ForzaMemoryFocus]::SetForegroundWindow([IntPtr]$process.MainWindowHandle)
    if (-not $focused) {
        $shell = New-Object -ComObject WScript.Shell
        $focused = $shell.AppActivate([int]$process.Id)
    }
    if (-not $focused) {
        throw "Could not focus Forza."
    }
    Start-Sleep -Milliseconds 100
    return $process
}

function Send-DownKeys {
    param([int] $Count)
    Focus-Forza | Out-Null
    for ($index = 0; $index -lt $Count; $index += 1) {
        [System.Windows.Forms.SendKeys]::SendWait("{DOWN}")
        if ($KeyDelayMs -gt 0) {
            Start-Sleep -Milliseconds $KeyDelayMs
        }
    }
    Start-Sleep -Milliseconds $SettleMs
}

function Send-UpKeys {
    param([int] $Count)
    Focus-Forza | Out-Null
    for ($index = 0; $index -lt $Count; $index += 1) {
        [System.Windows.Forms.SendKeys]::SendWait("{UP}")
        if ($KeyDelayMs -gt 0) {
            Start-Sleep -Milliseconds $KeyDelayMs
        }
    }
    Start-Sleep -Milliseconds $SettleMs
}

function Resolve-PythonExecutable {
    if (-not [string]::IsNullOrWhiteSpace($PythonExe)) {
        if (-not (Test-Path -LiteralPath $PythonExe)) {
            throw "Python executable was not found at '$PythonExe'."
        }
        return (Resolve-Path -LiteralPath $PythonExe).Path
    }
    $candidates = @(
        "C:\ForzaTools\Python312\python.exe",
        "C:\Users\admin\AppData\Local\Programs\Python\Python313\python.exe",
        "C:\Users\admin\AppData\Local\Programs\Python\Python312\python.exe"
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }
    $command = Get-Command python.exe -ErrorAction SilentlyContinue
    if ($command -and $command.Source -notmatch "\\WindowsApps\\") {
        return $command.Source
    }
    throw "A real Python installation was not found. Expected C:\ForzaTools\Python312\python.exe."
}

function Resolve-TotalRankCount {
    param([string] $Value)
    $numeric = 0
    if ([int]::TryParse($Value, [ref]$numeric) -and $numeric -gt 0) {
        return $numeric
    }
    if ($Value -notmatch "^(?i:auto|all|until[-_ ]?end|end)$") {
        throw "-TotalRanks must be Auto, UntilEnd, All, or a positive integer."
    }
    if ($Value -notmatch "^(?i:auto)$") {
        return 0
    }

    $base = Get-Content -LiteralPath (Resolve-WorkspacePath $RouteConfig) -Raw | ConvertFrom-Json
    $observeRoot = Join-Path $runRoot "total_rank_observe"
    $stateRoot = Join-Path $observeRoot "_state"
    Set-NestedProperty $base "display" "manage" $false
    Set-NestedProperty $base "display" "restore_after" $false
    Set-NestedProperty $base "launch" "startup_wait_seconds" 0
    Set-NestedProperty $base "capture" "output_dir" $observeRoot
    Set-NestedProperty $base "capture" "state_dir" $stateRoot
    Set-NestedProperty $base "capture" "clear_output_dir" $true
    Set-NestedProperty $base "capture" "dump_state_ocr" $true
    Set-NestedProperty $base "extraction" "enabled" $false
    $base.steps = @(
        [pscustomobject]@{
            action = "wait_for_text"
            pattern = "Driver|Drivetrain|Filter:\s+Global|Change\s+Rival"
            timeout_seconds = 45
            interval_seconds = 1
            name = "leaderboard_{index:000}.png"
        },
        [pscustomobject]@{ action = "observe"; name = "leaderboard_total.png" }
    )
    $configPath = Join-Path $runRoot "observe_total.json"
    $base | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $configPath -Encoding UTF8
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "run_capture_automation.ps1") `
        -Config $configPath -SkipLaunch -SkipExtract |
        ForEach-Object { Write-Host $_ }
    $observeExitCode = $LASTEXITCODE
    if ($observeExitCode -ne 0) {
        Write-MemoryScan "total player OCR failed; continuing in UntilEnd mode"
        return 0
    }
    $ocrFile = Get-ChildItem -LiteralPath $stateRoot -Filter "*.ocr.txt" |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -eq $ocrFile) {
        return 0
    }
    $text = Get-Content -LiteralPath $ocrFile.FullName -Raw -Encoding UTF8
    $match = [regex]::Match(
        $text,
        "([0-9][0-9., ]*)\s+Players",
        [Text.RegularExpressions.RegexOptions]::IgnoreCase
    )
    if (-not $match.Success) {
        Write-MemoryScan "player total was not visible; continuing in UntilEnd mode"
        return 0
    }
    $digits = $match.Groups[1].Value -replace "[^0-9]", ""
    if ([int]::TryParse($digits, [ref]$numeric)) {
        return $numeric
    }
    return 0
}

if ([string]::IsNullOrWhiteSpace($RunId)) {
    $RunId = "{0}_{1}" -f (Get-Date -Format "yyyyMMdd_HHmmss_fff"), ([guid]::NewGuid().ToString("N").Substring(0, 8))
}
if ($StartRank -lt 1) {
    throw "-StartRank must be at least 1."
}
$runRoot = Resolve-WorkspacePath (Join-Path $OutputRoot $RunId)
$chunkRoot = Join-Path $runRoot "chunks"
$statePath = Join-Path $runRoot "state.json"
New-Item -ItemType Directory -Force -Path $runRoot, $chunkRoot | Out-Null

$process = Focus-Forza
$python = Resolve-PythonExecutable
function Confirm-BoardEnd {
    <#
        Decide whether an apparent end is a real one. Test-ListAtEnd reports what the
        thumb looks like; this turns that into a decision, and it is the only place
        allowed to end a board on "no rows arrived".

        Returns "ended" (thumb at the bottom), "unverified" (scrollbar unreadable --
        the caller stops but must not bank the board as finished), "exhausted" (the
        list demonstrably continues but the rejection budget is spent), or "more"
        (thumb mid-bar, so the caller waits and keeps paging).
    #>
    param([int] $AtRank, [int] $Rejections)

    $sample = Test-ListAtEnd
    # Keep the frame the decision was made on, so the run's state.json carries the
    # evidence rather than a second, later capture of a screen that may have moved.
    $script:lastEndSample = $sample
    if ($null -eq $sample -or -not $sample.found) {
        Write-MemoryScan "end-check at rank ${AtRank}: scrollbar unreadable -- stopping, end UNVERIFIED"
        return "unverified"
    }
    if ($sample.at_bottom) {
        Write-MemoryScan "end-check at rank ${AtRank}: thumb is at the bottom -- genuine end of board"
        return "ended"
    }
    $pct = [double]$sample.position
    # The thumb's position IS the progress fraction, and here -- unlike the periodic
    # progress sample -- the view is known to sit at the rank the reads just failed
    # on, so rank/position is a sound estimate of the whole board. Measured against a
    # confirmed end it lands within 0.5-2% (29,697 actual vs 29,849 estimated), which
    # is what turns "the list continues" into "and this much of it is left".
    if ($pct -gt 0.02 -and $AtRank -gt 0) {
        $script:lastEndEstimate = [int][Math]::Round($AtRank / $pct)
        Write-MemoryScan ("end-check: rank {0} at {1:P1} implies a board of about {2} ranks, roughly {3} still to read" -f `
            $AtRank, $pct, $script:lastEndEstimate, ($script:lastEndEstimate - $AtRank))
    }
    if ($Rejections -ge $MaxEndRejections) {
        Write-MemoryScan ("end-check at rank {0}: thumb still at {1:P0} after {2} rejected ends -- giving up here; TRUNCATED, not finished" -f `
            $AtRank, $pct, $Rejections)
        return "exhausted"
    }
    Write-MemoryScan ("end-check at rank {0}: thumb is only at {1:P0} of the bar -- the list continues; this is a slow page, not an end (rejection {2}/{3})" -f `
        $AtRank, $pct, ($Rejections + 1), $MaxEndRejections)
    return "more"
}

function Test-ListAtEnd {
    <#
        Ask the SCREEN whether the list ended, instead of inferring it from the
        scan. The leaderboard has a scrollbar whose thumb reaches the bottom of
        its track only at the real end of the list, so it answers the one question
        the row reads cannot: on 2026-08-20 the scan reported "end_detected" for S1
        at rank 100, again at 1950, and for A at 12000, while the thumb was sitting
        near the TOP of the bar every time -- three truncated boards recorded as
        finished. Returns the detector's result, or $null when it could not look.
    #>
    $observeRoot = Join-Path $runRoot "endcheck_observe"
    $stateRoot = Join-Path $observeRoot "_state"
    try {
        $base = Get-Content -LiteralPath (Resolve-WorkspacePath $RouteConfig) -Raw | ConvertFrom-Json
        Set-NestedProperty $base "display" "manage" $false
        Set-NestedProperty $base "display" "restore_after" $false
        Set-NestedProperty $base "launch" "startup_wait_seconds" 0
        Set-NestedProperty $base "capture" "output_dir" $observeRoot
        Set-NestedProperty $base "capture" "state_dir" $stateRoot
        Set-NestedProperty $base "capture" "clear_output_dir" $true
        Set-NestedProperty $base "capture" "dump_state_ocr" $true
        Set-NestedProperty $base "extraction" "enabled" $false
        # Confirm the leaderboard is really on screen first. The detector finds
        # bar-like stripes on other screens too, so it must not be asked about a
        # frame that is not a leaderboard.
        $base.steps = @(
            [pscustomobject]@{
                action = "wait_for_text"
                pattern = "Driver|Drivetrain|Filter:\s+Global|Change\s+Rival"
                timeout_seconds = 20
                interval_seconds = 1
                name = "endcheck_wait_{index:000}.png"
            },
            [pscustomobject]@{ action = "observe"; name = "endcheck.png" }
        )
        $configPath = Join-Path $runRoot "observe_endcheck.json"
        $base | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $configPath -Encoding UTF8
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "run_capture_automation.ps1") `
            -Config $configPath -SkipLaunch -SkipExtract | ForEach-Object { Write-Host $_ }
        if ($LASTEXITCODE -ne 0) {
            Write-MemoryScan "end-check: could not capture a frame (exit $LASTEXITCODE)"
            return $null
        }
        $frame = Get-ChildItem -LiteralPath $observeRoot -Filter "*.png" -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($null -eq $frame) {
            Write-MemoryScan "end-check: no frame was written"
            return $null
        }
        $detector = Join-Path $PSScriptRoot "detect_leaderboard_scrollbar.py"
        $raw = & $python $detector $frame.FullName 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-MemoryScan "end-check: detector failed: $raw"
            return $null
        }
        return ($raw -join "`n" | ConvertFrom-Json)
    } catch {
        Write-MemoryScan "end-check: $($_.Exception.Message)"
        return $null
    }
}


$total = Resolve-TotalRankCount -Value $TotalRanks
Write-MemoryScan "pid=$($process.Id) python='$python' track='$Track' class='$PerformanceClass' mode='$RivalsMode' total=$(if($total -gt 0){$total}else{'UntilEnd'})"

$scanner = Join-Path $PSScriptRoot "capture_scoreboard_memory.py"
$started = Get-Date
$currentRank = $StartRank
$lastMaximum = $StartRank - 1
$nearAddresses = [Collections.Generic.List[long]]::new()
$noProgress = 0
$endRejections = 0
$endVerdict = $null
$lastEndSample = $null
$lastEndEstimate = $null
$seenRanks = [Collections.Generic.HashSet[int]]::new()
$gapAttempts = @{}
$forcedRank = 0


function Measure-BoardPosition {
    <#
        The rank span of whatever row block is currently loaded, read WITHOUT
        asking for a specific rank -- the scanner's open mode, which reports the
        block that is actually there instead of hunting for one that may not be.
        Returns $null when nothing was found.
    #>
    param([Parameter(Mandatory = $true)][string] $Label)
    $json = Join-Path $chunkRoot ("align_{0}.json" -f $Label)
    Remove-Item -LiteralPath $json -Force -ErrorAction SilentlyContinue
    & $python @(
        $scanner,
        "--pid", [string]$process.Id,
        "--minimum-rows", "3",
        "--max-rows", "512",
        "--chunk-size-mb", "16",
        "--output-json", $json,
        "--track", $Track,
        "--performance-class", $PerformanceClass,
        "--rivals-mode", $RivalsMode
    ) | ForEach-Object { Write-Host $_ }
    if (-not (Test-Path -LiteralPath $json)) { return $null }
    $rows = @((Get-Content -LiteralPath $json -Raw | ConvertFrom-Json).rows)
    if ($rows.Count -eq 0) { return $null }
    return [pscustomobject]@{
        Minimum = [int](($rows | Measure-Object -Property rank -Minimum).Minimum)
        Maximum = [int](($rows | Measure-Object -Property rank -Maximum).Maximum)
        Source  = [string]$rows[0].source_address
    }
}

# Every DOWN burst below is computed from $currentRank, so the model has to be
# TRUE that the list sits at $StartRank. It frequently is not: a Rivals board
# opens centred on the player's own time. On 2026-08-20 the S1 board opened at
# rank ~36 -- the player has a time in S1 but none in D/C/B/A, which is exactly
# why only S1 failed -- the model said 1, every burst was off by that offset, and
# the scan wrote seven empty chunks at rank 101 before the no-progress limit
# would have closed the board at 99 rows and reported "end_detected".
#
# So read the position instead of assuming it, and scroll to a CONFIRMED start.
# This costs no extra work: the open read primes $nearAddresses, which turns the
# first loop iteration's unavoidable full sweep into a cheap exact read.
# Put the list where the rank model claims it is, in EITHER direction. The first
# version of this only knew how to scroll back towards the top, and its "aligned"
# test was `Minimum -le $StartRank` -- true for any StartRank once the list sat at
# rank 1. That is exactly wrong for resuming a truncated board: asked to continue
# at 10101 it would declare success with the list at rank 1 and then read into
# nothing. The test is now whether the loaded block actually CONTAINS $StartRank.
# Resuming: walk to the top, confirm it, then count down. Only the resume case
# takes this path -- a fresh board already starts at rank 1, so nothing changes
# there.
if ($StartRank -gt 1 -and -not $NoSeekViaTop) {
    Write-MemoryScan "seek-via-top: going to the top of the list, then counting down to $StartRank"
    $atTop = $false
    for ($attempt = 1; $attempt -le 6 -and -not $atTop; $attempt += 1) {
        # Generous but bounded: enough presses to clear the deepest place the view
        # could be, so the top is reached rather than approached.
        Send-UpKeys -Count ([Math]::Min($StartRank + 200, $MaxSeekUpKeys))
        $position = Measure-BoardPosition -Label ("top{0:000}" -f $attempt)
        if ($null -eq $position) { continue }
        if ($position.Source -match "^0x[0-9a-fA-F]+$") {
            $topAddress = [Convert]::ToInt64($position.Source.Substring(2), 16)
            if (-not $nearAddresses.Contains($topAddress)) { $nearAddresses.Add($topAddress) }
        }
        # Rank 1 resident is the only proof that the list is actually at the top.
        if ($position.Minimum -le 1) {
            Write-MemoryScan "seek-via-top: confirmed at the top after $attempt burst(s)"
            $atTop = $true
        }
    }
    if (-not $atTop) {
        Write-MemoryScan "seek-via-top: could not confirm the top; falling back to position estimates"
    } else {
        $down = $StartRank - 1 - $PrefetchMargin
        if ($down -gt 0) {
            Write-MemoryScan "seek-via-top: key DOWN x$down to reach $StartRank"
            Send-DownKeys -Count $down
        }
        $currentRank = $StartRank
        $NoStartAlign = $true      # the position is now known, not estimated
    }
}

if (-not $NoStartAlign) {
    for ($attempt = 1; $attempt -le $StartAlignAttempts; $attempt += 1) {
        $position = Measure-BoardPosition -Label ("{0:000}" -f $attempt)
        if ($null -eq $position) {
            Write-MemoryScan "seek: no row block loaded; leaving the model at rank $StartRank"
            break
        }
        if ($position.Source -match "^0x[0-9a-fA-F]+$") {
            $alignAddress = [Convert]::ToInt64($position.Source.Substring(2), 16)
            if (-not $nearAddresses.Contains($alignAddress)) { $nearAddresses.Add($alignAddress) }
        }
        if ($StartRank -ge $position.Minimum -and $StartRank -le $position.Maximum) {
            Write-MemoryScan "seek: rank $StartRank is inside the loaded block $($position.Minimum)-$($position.Maximum); aligned"
            # Anchor the rank model on what was MEASURED. This loop sends key
            # bursts without counting them into $currentRank, so every resume
            # entered the main loop with the model it was handed -- rank
            # $StartRank -- no matter where thousands of presses had actually left
            # the list. Counting the presses instead would be worse: they are
            # silently eaten once the served list runs out, so the model would
            # claim the cursor had passed a rank it never reached. After a read the
            # cursor sits at the loaded block's highest rank, so that is the anchor.
            $currentRank = $position.Maximum
            break
        }
        if ($position.Minimum -gt $StartRank) {
            # Too deep. Measure from the block's TOP, not its lowest rank: after a
            # read the cursor sits at the block's highest rank, so scrolling by the
            # lower bound leaves the view inside the block already loaded and the
            # game re-fetches nothing -- the trap the gap recovery below documents.
            $delta = ($position.Maximum - $StartRank) + $PrefetchMargin + 5
            Write-MemoryScan "seek: list at $($position.Minimum)-$($position.Maximum), wanted $StartRank; key UP x$delta"
            Send-UpKeys -Count $delta
        } else {
            # Not deep enough -- the resume case. ADD the prefetch margin, do not
            # subtract it. The block runs from roughly the cursor to cursor plus the
            # prefetch, so aiming at (StartRank - margin) parks the cursor short and
            # leaves the block ending just BELOW the wanted rank: the next round then
            # computes a delta of 0, the Max(1, ...) floor turns that into a single
            # row, and the seek creeps one rank per verification capture. Observed
            # 2026-08-20: rank 1907 to 1912 in a minute while seeking 1951. Aiming at
            # StartRank itself puts the cursor on it, so the block contains it.
            $delta = [Math]::Max(1, ($StartRank - $position.Maximum) + $PrefetchMargin)
            Write-MemoryScan "seek: list at $($position.Minimum)-$($position.Maximum), wanted $StartRank; key DOWN x$delta"
            Send-DownKeys -Count $delta
        }
        if ($attempt -eq $StartAlignAttempts) {
            # Same anchoring as the aligned case, and it matters more here: an
            # unconverged seek is exactly where the model and the list disagree.
            # Measured on Highway Circuit S1 pass 2, 2026-08-21: all six attempts
            # read the block 23451-23500 while the model said 23501, because every
            # press aimed past the end of the served list went nowhere.
            $currentRank = $position.Maximum
            Write-MemoryScan "seek: could not reach rank $StartRank in $attempt attempt(s); scanning from the measured position $currentRank"
        }
    }
}

$degraded = $false
$stallRecoveries = 0
$crossToFetch = $false
$crossProofs = 0
$advancedForRank = 0
$rateWindow = New-Object System.Collections.ArrayList
$lastProgressCheck = [datetime]::MinValue
$scrollPosition = $null
$estimatedTotal = $null

for ($iteration = 0; $iteration -lt $MaxIterations; $iteration += 1) {
    if (-not (Get-Process -Id $process.Id -ErrorAction SilentlyContinue)) {
        throw "Forza exited during memory scan."
    }
    $expectedRank = if ($forcedRank -gt 0) {
        $forcedRank
    } elseif ($seenRanks.Count -gt 0) {
        $lastMaximum + 1
    } else {
        $StartRank
    }
    $isRecovery = $forcedRank -gt 0
    $forcedRank = 0
    $chunkJson = Join-Path $chunkRoot ("rows_{0:000000}_{1:000000000}.json" -f $iteration, $expectedRank)
    $chunkCsv = [IO.Path]::ChangeExtension($chunkJson, ".csv")
    $baseArguments = @(
        $scanner,
        "--pid", [string]$process.Id,
        "--expected-rank", [string]$expectedRank,
        "--minimum-rows", "3",
        "--max-rows", "512",
        "--chunk-size-mb", "16",
        "--output-json", $chunkJson,
        "--output-csv", $chunkCsv,
        "--track", $Track,
        "--performance-class", $PerformanceClass,
        "--rivals-mode", $RivalsMode
    )
    # Appended rather than placed inside the literal: a conditional element there
    # would put $null into the argument array when the switch is off.
    if ($AllowMissingGamertag) { $baseArguments += "--allow-missing-gamertag" }

    $report = $null
    if ($nearAddresses.Count -gt 0) {
        Remove-Item -LiteralPath $chunkJson, $chunkCsv -Force -ErrorAction SilentlyContinue
        $exactArguments = $baseArguments + @(
            # Wait properly as soon as ANY address is cached. The old rule needed two
            # before it waited at all, and even then only for a second.
            "--wait-seconds", [string]$(if ($nearAddresses.Count -ge 1) { $ExactWaitSeconds } else { 0.15 }),
            "--poll-ms", "20"
        )
        foreach ($candidateAddress in @($nearAddresses)) {
            $exactArguments += @("--exact-address", ("0x{0:x}" -f $candidateAddress))
        }
        & $python @exactArguments | ForEach-Object { Write-Host $_ }
        if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $chunkJson)) {
            $candidateReport = Get-Content -LiteralPath $chunkJson -Raw | ConvertFrom-Json
            if (@($candidateReport.rows).Count -gt 0) {
                $report = $candidateReport
            }
        }
    }

    # Between the exact-address read and a full sweep of ~6 GB there is a much
    # cheaper option that was never used: the block moves between pages, but it
    # does not move far. Searching a window around the last known address finds
    # it in a fraction of the time, and only a genuine relocation pays for the
    # full sweep. Without this every page that missed its exact address cost a
    # complete scan, which is what held throughput down to ~125 ranks/min.
    # Probe the most recent arenas, newest first, not just the single last one.
    # On the A board the block kept landing outside a 1536 MB window around its
    # previous address, so every page fell through to a full sweep and throughput
    # collapsed. Widening the radius instead would approach the cost of a sweep,
    # since the radius is already a quarter of the process.
    if ($null -eq $report -and $nearAddresses.Count -gt 0) {
        $probes = [Math]::Min($NearProbeCount, $nearAddresses.Count)
        for ($probe = 1; $probe -le $probes -and $null -eq $report; $probe += 1) {
            $candidateAddress = $nearAddresses[$nearAddresses.Count - $probe]
            Remove-Item -LiteralPath $chunkJson, $chunkCsv -Force -ErrorAction SilentlyContinue
            $nearArguments = $baseArguments + @(
                "--near-address", ("0x{0:x}" -f $candidateAddress),
                "--near-radius-mb", [string]$NearRadiusMb
            )
            & $python @nearArguments | ForEach-Object { Write-Host $_ }
            if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $chunkJson)) {
                $candidateReport = Get-Content -LiteralPath $chunkJson -Raw | ConvertFrom-Json
                if (@($candidateReport.rows).Count -gt 0) {
                    $report = $candidateReport
                }
            }
        }
    }

    # Both cheap paths came up empty, which usually means the page has not landed
    # yet rather than that it moved. Waiting at the addresses we already know is
    # the cheap way to find out; a full sweep would answer the same question by
    # taking ~50 s to run.
    if ($null -eq $report -and $nearAddresses.Count -gt 0 -and $SlowFetchWaitSeconds -gt 0) {
        Remove-Item -LiteralPath $chunkJson, $chunkCsv -Force -ErrorAction SilentlyContinue
        $waitArguments = $baseArguments + @(
            "--wait-seconds", [string]$SlowFetchWaitSeconds,
            "--poll-ms", "50"
        )
        foreach ($candidateAddress in @($nearAddresses)) {
            $waitArguments += @("--exact-address", ("0x{0:x}" -f $candidateAddress))
        }
        & $python @waitArguments | ForEach-Object { Write-Host $_ }
        if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $chunkJson)) {
            $candidateReport = Get-Content -LiteralPath $chunkJson -Raw | ConvertFrom-Json
            if (@($candidateReport.rows).Count -gt 0) {
                $report = $candidateReport
                Write-MemoryScan "rank $expectedRank arrived while waiting at a known address"
            }
        }
    }

    # A stalled read escalates to a full ~6 GB sweep, and the end of a board is a
    # stall that never resolves: on Highway Circuit / C the eight no-progress
    # attempts cost 4.6 min each -- about 37 minutes to establish that rank 5594
    # does not exist. One sweep is enough to prove the buffer did not merely move,
    # so the attempts in between stay cheap (exact + near only). The last attempt
    # pays for a sweep again, which keeps a genuine far relocation recoverable
    # rather than ending the board early.
    $skipFullSweep = $nearAddresses.Count -gt 0 -and
        $noProgress -ge $FullSweepStallLimit -and
        $noProgress -lt ($NoProgressLimit - 1)
    if ($null -eq $report -and -not $skipFullSweep) {
        if ($nearAddresses.Count -gt 0) {
            Write-MemoryScan "exact and near reads missed rank $expectedRank; scan full memory"
        }
        Remove-Item -LiteralPath $chunkJson, $chunkCsv -Force -ErrorAction SilentlyContinue
        # A trailing --chunk-size-mb wins over the one in $baseArguments, so the
        # sweep reads in larger blocks than the targeted reads do.
        & $python @($baseArguments + @("--chunk-size-mb", [string]$FullSweepChunkMb)) |
            ForEach-Object { Write-Host $_ }
        if (-not (Test-Path -LiteralPath $chunkJson)) {
            throw "Memory scanner did not write $chunkJson."
        }
        $report = Get-Content -LiteralPath $chunkJson -Raw | ConvertFrom-Json
    } elseif ($null -eq $report) {
        Write-MemoryScan "stall $noProgress/$NoProgressLimit at rank $expectedRank; cheap retry, no full sweep"
    }

    $rows = if ($null -ne $report) { @($report.rows) } else { @() }
    if ($rows.Count -eq 0) {
        $noProgress += 1
        Write-MemoryScan "no row block found for rank $expectedRank ($noProgress/$NoProgressLimit)"
        if ($noProgress -ge $NoProgressLimit) {
            # Ask the SCREEN before believing the board ended. A thumb that is not
            # at the bottom means the list continues and the missing page is merely
            # late -- wait it out and keep paging instead of banking a short board.
            $endVerdict = Confirm-BoardEnd -AtRank $lastMaximum -Rejections $endRejections
            if ($endVerdict -eq "more") {
                $endRejections += 1
                $waitFor = $EndRetryWaitSeconds * $endRejections
                Write-MemoryScan "waiting ${waitFor}s for the late page, then continuing from rank $lastMaximum"
                Start-Sleep -Seconds $waitFor
                # A board that needs this is not prefetching, so park past the page
                # bottom from here on and cross once now to trigger the fetch.
                $crossToFetch = $true
                Send-DownKeys -Count $PrefetchMargin
                $currentRank += $PrefetchMargin
                $noProgress = 0
                continue
            }
            break
        }
        # A failed read has two causes, and they want opposite treatment.
        #
        # (a) The page was asked for and has not arrived yet. Scrolling then moves
        #     the view further from the very rank being requested, so the next read
        #     misses as well: with two failures per captured page that is +10 rows
        #     of drift for every 50 captured, the feedback loop that took the A
        #     board from 750 ranks/min down to 50 without ever recovering.
        #
        # (b) The page was never asked for, because the cursor is still inside the
        #     block already loaded. The game fetches the next page when the cursor
        #     crosses the BOTTOM of the current one, and the jump at the end of this
        #     loop deliberately parks it $PrefetchMargin rows above that bottom. On
        #     a board that prefetches a page ahead, that costs nothing. On a
        #     re-seated deep board it costs everything: measured on Highway Circuit
        #     S1 pass 2, 2026-08-21, EVERY page ran five dead reads at 60-72 s each,
        #     then the two 5-row nudges this replaces, and the page arrived 8 s
        #     later -- 360 s per 50 ranks, 8 ranks/min, 14 productive reads out of
        #     85, where the keys that actually fixed it cost 0.2 s.
        #
        # Telling them apart needs no new evidence: in case (b) the rank model says
        # the cursor has not reached the wanted rank yet. So press towards it -- and
        # only towards it. That is the difference from the old unconditional nudge:
        # this stops as soon as the model is on target, so it cannot drift, and
        # $MaxStallAdvance caps the creep to one page for the case where the model
        # is behind the truth (presses are eaten by the end of the served list, so
        # a model that counted them can overstate how far the cursor really got).
        $crossTarget = $expectedRank + $PrefetchMargin
        if ($noProgress -ge $StallNudgeAfter) {
            $crossTarget = [Math]::Min(
                $crossTarget + (($noProgress - $StallNudgeAfter + 1) * 5),
                $expectedRank + $PrefetchMargin + $MaxStallAdvance)
        }
        # ...but only once a buffer address is known. Before that a failed read
        # means the block has not been LOCATED yet, not that the page was never
        # asked for, and pressing keys on a board that is still at rank 1 moves
        # the view off the very first page.
        if ($nearAddresses.Count -gt 0 -and $currentRank -lt $crossTarget) {
            $advance = [Math]::Min($MaxDownBurst, $crossTarget - $currentRank)
            Write-MemoryScan "rank $expectedRank not served and the cursor models at $currentRank; key DOWN x$advance to cross into the page"
            Send-DownKeys -Count $advance
            $currentRank += $advance
            # Remembered so the productive read below can tell whether crossing is
            # what delivered the page, rather than guessing from the fact that a
            # read once failed.
            $advancedForRank = $expectedRank
        } else {
            Write-MemoryScan "rank $expectedRank not served and the cursor is already at $currentRank; waiting rather than scrolling"
        }
        continue
    }

    foreach ($row in $rows) {
        [void]$seenRanks.Add([int]$row.rank)
    }
    if (-not $ScanInvalidLaps) {
        # is_clean is false on every row of the invalid tail and true on every row
        # before it, so the first false row IS the boundary -- no heuristic needed.
        $firstInvalid = $rows |
            Where-Object { $null -ne $_.is_clean -and -not $_.is_clean } |
            Sort-Object { [int]$_.rank } |
            Select-Object -First 1
        if ($null -ne $firstInvalid) {
            $boundary = [int]$firstInvalid.rank
            # Self-check, because the whole saving rests on one property of the board:
            # nothing valid may sit BELOW the first invalid row. If this page shows
            # otherwise, the assumption is wrong for this board and cutting here would
            # silently discard real laps -- so say so and keep paging instead.
            $validBelow = @($rows | Where-Object { $_.is_clean -and [int]$_.rank -gt $boundary })
            if ($validBelow.Count -gt 0) {
                Write-MemoryScan "WARNING rank $boundary is invalid but $($validBelow.Count) valid lap(s) rank below it; this board does not separate cleanly, so the scan continues"
                $ScanInvalidLaps = $true
            }
        }
        if ($null -ne $firstInvalid -and -not $ScanInvalidLaps) {
            $invalidFromRank = [int]$firstInvalid.rank
            $validRows = @($rows | Where-Object { $_.is_clean })
            $lastValid = if ($validRows.Count) {
                [int](($validRows | Measure-Object -Property rank -Maximum).Maximum)
            } else { $lastMaximum }
            if ($lastValid -gt $lastMaximum) { $lastMaximum = $lastValid }
            Write-MemoryScan "rank $invalidFromRank is an INVALID lap; the valid section of this board ends at $lastMaximum -- stopping here"
            break
        }
    }
    $sourceText = [string]$rows[0].source_address
    if ($sourceText -match "^0x[0-9a-fA-F]+$") {
        $sourceAddress = [Convert]::ToInt64($sourceText.Substring(2), 16)
        if (-not $nearAddresses.Contains($sourceAddress)) {
            $nearAddresses.Add($sourceAddress)
            while ($nearAddresses.Count -gt $MaxNearAddresses) {
                $nearAddresses.RemoveAt(0)
            }
        }
    }
    $maximum = [int](($rows | Measure-Object -Property rank -Maximum).Maximum)
    $newMaximum = $maximum -gt $lastMaximum
    # A page that arrives after failed reads is a RECOVERY, and the throughput
    # watchdog below has to be told: those stalled reads are still inside its
    # window, so left alone it stops the board in the very moment the recovery
    # worked. Read this before $noProgress is reset.
    $recovered = $newMaximum -and $noProgress -gt 0
    # ...and was it the expensive kind? One miss followed by a cross is ordinary
    # paging on a board that has stopped prefetching -- roughly 9 s. A page that
    # needed $StallNudgeAfter misses paid the whole read ladder for each of them,
    # 60-72 s apiece, and that is the pathology the chronic-stall cap below is
    # counting. Counting every recovery instead would stop a board that had just
    # been taught to page properly.
    $stalledHard = $newMaximum -and $noProgress -ge $StallNudgeAfter
    # Did pressing towards the rank deliver this page? That is the evidence that
    # the game is not prefetching on this board, so the parking spot chosen by the
    # jump at the end of the loop is fetching nothing and every page will pay a
    # dead read to discover it.
    if ($newMaximum -and $advancedForRank -eq $expectedRank -and -not $crossToFetch) {
        $crossProofs += 1
        if ($crossProofs -ge $CrossToFetchAfter) {
            $crossToFetch = $true
            Write-MemoryScan "$crossProofs pages needed a cross to arrive; parking the cursor PAST the page bottom from here on"
        }
    }
    $advancedForRank = 0
    if ($newMaximum) {
        $lastMaximum = $maximum
        $noProgress = 0
    } elseif (-not $isRecovery) {
        $noProgress += 1
    }

    $elapsed = [Math]::Max(0.001, ((Get-Date) - $started).TotalMinutes)
    $rate = $seenRanks.Count / $elapsed

    if ($ProgressCheckSeconds -gt 0 -and
        ((Get-Date) - $lastProgressCheck).TotalSeconds -ge $ProgressCheckSeconds) {
        $lastProgressCheck = Get-Date
        $sample = Test-ListAtEnd
        if ($null -ne $sample -and $sample.found -and $null -ne $sample.thumb) {
            $scrollPosition = [double]$sample.position
            # Extrapolating from a thumb barely off the top turns rounding into
            # nonsense (position 0.004 on 50 ranks "predicts" 12500), so only
            # estimate once the bar has actually moved.
            if ($scrollPosition -gt 0.02) {
                $estimatedTotal = [int][Math]::Round($lastMaximum / $scrollPosition)
            }
            Write-MemoryScan ("progress: scrollbar at {0:P1}{1}" -f $scrollPosition,
                $(if ($estimatedTotal) { ", board looks about $estimatedTotal ranks" } else { ", too early to estimate the total" }))
        }
    }

    $etaMinutes = if ($total -gt 0 -and $rate -gt 0) {
        [Math]::Max(0, ($total - $lastMaximum) / $rate)
    } elseif ($null -ne $estimatedTotal -and $rate -gt 0) {
        [Math]::Max(0, ($estimatedTotal - $lastMaximum) / $rate)
    } else {
        $null
    }
    $state = [ordered]@{
        run_id = $RunId
        status = "running"
        track = $Track
        performance_class = $PerformanceClass
        rivals_mode = $RivalsMode
        process_id = $process.Id
        iteration = $iteration
        rows_collected = $seenRanks.Count
        minimum_rank = if ($seenRanks.Count) { ($seenRanks | Measure-Object -Minimum).Minimum } else { $null }
        maximum_rank = $lastMaximum
        current_rank_estimate = $currentRank
        start_rank = $StartRank
        total_ranks = $total
        ranks_per_minute = [Math]::Round($rate, 1)
        eta_minutes = if ($null -ne $etaMinutes) { [Math]::Round($etaMinutes, 1) } else { $null }
        scroll_position = $(if ($null -ne $scrollPosition) { [Math]::Round($scrollPosition, 4) } else { $null })
        estimated_total_ranks = $estimatedTotal
        cached_memory_regions = $nearAddresses.Count
        no_progress = $noProgress
        updated_at = (Get-Date).ToString("o")
    }
    $state | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $statePath -Encoding UTF8
    Write-MemoryScan ("rows={0} max={1} speed={2:N1}/min ETA={3}" -f `
        $seenRanks.Count,
        $lastMaximum,
        $rate,
        $(if ($null -ne $etaMinutes) { "{0:N1} min" -f $etaMinutes } else { "unknown" })
    )

    if ($total -gt 0 -and $lastMaximum -ge $total) {
        break
    }
    if ($noProgress -ge $NoProgressLimit) {
            # Ask the SCREEN before believing the board ended. A thumb that is not
        # at the bottom means the list continues and the missing page is merely
        # late -- wait it out and keep paging instead of banking a short board.
        $endVerdict = Confirm-BoardEnd -AtRank $lastMaximum -Rejections $endRejections
        if ($endVerdict -eq "more") {
            $endRejections += 1
            $waitFor = $EndRetryWaitSeconds * $endRejections
            Write-MemoryScan "waiting ${waitFor}s for the late page, then continuing from rank $lastMaximum"
            Start-Sleep -Seconds $waitFor
            # A board that needs this is not prefetching, so park past the page
            # bottom from here on and cross once now to trigger the fetch.
            $crossToFetch = $true
            Send-DownKeys -Count $PrefetchMargin
            $currentRank += $PrefetchMargin
            $noProgress = 0
            continue
        }
        break
    }

    # Throughput watchdog. Rate is measured across the last $RateWindowPages
    # productive pages rather than since the start: the cumulative average in
    # state.json lags so badly that it still read 383/min while the board was
    # actually delivering 50, which is precisely why this needs its own window.
    if ($MinRanksPerMinute -gt 0 -and $newMaximum -and ($lastMaximum - $StartRank) -ge $MinGainBeforeWatchdog) {
        if ($recovered) {
            # Measure the recovered board, not the stall it just escaped. Observed
            # on Highway Circuit S2, 2026-08-21: the stall nudge delivered a full
            # 50-row page and the watchdog stopped the board on that same page,
            # because its six-page window still held the six stalled reads. The
            # window restarts here, so the next pages judge the board as it is now
            # serving.
            $rateWindow.Clear()
        }
        if ($stalledHard) {
            $stallRecoveries += 1
            if ($MaxStallRecoveries -gt 0 -and $stallRecoveries -ge $MaxStallRecoveries) {
                # Resetting on every recovery would otherwise mean a board that
                # stalls and recovers forever is never judged at all -- the silent
                # crawl the watchdog exists to stop.
                Write-MemoryScan ("recovered from {0} stalls without a clean stretch of {1} pages; the board is not serving reliably -- stopping at rank {2} so it can be re-seated and resumed" -f `
                    $stallRecoveries, $RateWindowPages, $lastMaximum)
                $degraded = $true
                break
            }
        }
        [void]$rateWindow.Add([pscustomobject]@{ At = (Get-Date); Max = $lastMaximum })
        while ($rateWindow.Count -gt $RateWindowPages) { $rateWindow.RemoveAt(0) }
        if ($rateWindow.Count -eq $RateWindowPages) {
            $span = ($rateWindow[$rateWindow.Count - 1].At - $rateWindow[0].At).TotalMinutes
            $gain = $rateWindow[$rateWindow.Count - 1].Max - $rateWindow[0].Max
            if ($span -gt 0) {
                $recentRate = $gain / $span
                if ($recentRate -lt $MinRanksPerMinute) {
                    Write-MemoryScan ("throughput fell to {0:N0} ranks/min over the last {1} pages (floor {2}); stopping at rank {3} so the board can be re-seated and resumed" -f `
                        $recentRate, $RateWindowPages, $MinRanksPerMinute, $lastMaximum)
                    $degraded = $true
                    break
                }
                # A full window at or above the floor is the clean stretch the
                # recovery counter waits for: the board has proved itself since
                # the last stall, so past stalls stop counting against it.
                $stallRecoveries = 0
            }
        }
    }

    # The scanner returns the live block spanning the wanted rank, or the nearest
    # block starting after it. When the scroll overshot by a row, the wanted rank
    # has already been evicted from the top of the live window and is skipped for
    # good -- which is exactly the single-rank x01/x51 gaps on the first full board
    # (57 lost out of 8193). Scrolling back brings the row into the window again,
    # so ask for it once more instead of accepting the hole.
    $blockMin = [int](($rows | Measure-Object -Property rank -Minimum).Minimum)
    if ($blockMin -gt $expectedRank -and -not $seenRanks.Contains($expectedRank)) {
        $attempts = if ($gapAttempts.ContainsKey($expectedRank)) { $gapAttempts[$expectedRank] } else { 0 }
        if ($attempts -lt $MaxGapRetries) {
            $gapAttempts[$expectedRank] = $attempts + 1
            # Scroll back far enough to leave the block that is already loaded.
            # Measuring from its lowest rank is not enough: after reading, the
            # cursor sits at the block's HIGHEST rank, so on Highway Circuit / A
            # the 11 presses computed that way moved 12000 -> 11989, still inside
            # the loaded 11952-12000 block. The game re-fetches nothing, the retry
            # returns the identical block, and rank 11951 was lost anyway. Measure
            # from the top of the block so the view is forced above the wanted rank.
            $back = ($maximum - $expectedRank) + $PrefetchMargin + 5
            Write-MemoryScan "block started at $blockMin, past wanted $expectedRank; key UP x$back to recover"
            Send-UpKeys -Count $back
            $currentRank = [Math]::Max(1, $currentRank - $back)
            $forcedRank = $expectedRank
            continue
        }
        Write-MemoryScan "rank $expectedRank still missing after $attempts recovery attempt(s); recording the gap"
    }

    # Where to park the cursor for the next page. The default is
    # $PrefetchMargin rows ABOVE the bottom of the block just read, which is
    # right while the game is prefetching a page ahead: the next rank is already
    # resident, and the margin keeps the wanted rank from being evicted off the
    # top of the live window -- the x01/x51 single-rank gaps handled below.
    #
    # A board that has stopped prefetching fetches nothing from there: the cursor
    # has to cross the bottom before the game asks for the next page, and every
    # page then spends a 60-72 s dead read discovering that (measured on Highway
    # Circuit S1 pass 2, 2026-08-21: five dead reads per page, 8 ranks/min). Once
    # $CrossToFetchAfter pages have been delivered by a cross, park past the
    # bottom instead. Sticky rather than per-page: alternating the two spots pays
    # a dead read every other page. Evidence that crossing does not cost the head
    # of the page: in that trace all 14 pages arrived with minimum_rank exactly at
    # the wanted rank, with the cursor 5-15 rows past the previous bottom.
    $parkAt = if ($crossToFetch) {
        $lastMaximum + $PrefetchMargin
    } else {
        $lastMaximum - $PrefetchMargin
    }
    $jump = [Math]::Max(1, $parkAt - $currentRank)
    $jump = [Math]::Min($MaxDownBurst, $jump)
    if (-not $newMaximum) {
        $jump = [Math]::Min(10, $MaxDownBurst)
    }
    Write-MemoryScan "key DOWN x$jump"
    Send-DownKeys -Count $jump
    $currentRank += $jump
}

# Set when the paging loop walked off the end of the valid section.
if (-not (Test-Path variable:invalidFromRank)) { $invalidFromRank = 0 }

$finalState = if (Test-Path -LiteralPath $statePath) {
    Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
} else {
    [pscustomobject]@{}
}
$complete = $total -gt 0 -and $lastMaximum -ge $total
# "No new rows" is not "the board ended". Where the old code assumed the two were
# the same, ask the scrollbar; only an unambiguous bottom is reported as an end,
# and anything else is named so the sweep cannot bank it as a finished board.
$scrollbar = $null
$finalStatus = if ($invalidFromRank -gt 0) {
    # Not truncation and not a scrollbar question: the board's valid section is
    # finished, and what follows is the invalid tail we deliberately skip. The
    # scrollbar would sit mid-bar here and would otherwise call this TRUNCATED.
    Write-MemoryScan "valid section complete: $lastMaximum ranks, invalid laps start at $invalidFromRank"
    "valid_section_complete"
} elseif ($complete) {
    "completed"
} elseif ($degraded) {
    # Not an end-of-board claim, so the scrollbar is not consulted: this says the
    # page fetch went slow, and the caller should re-seat rather than conclude.
    "degraded"
} elseif ($total -eq 0 -and $noProgress -ge $NoProgressLimit) {
    # The paging loop already asked the scrollbar -- through Confirm-BoardEnd, which
    # is what decided to stop -- so re-asking here would capture a second frame and
    # could disagree with the decision actually taken. Report that verdict instead.
    switch ($endVerdict) {
        "ended"      { Write-MemoryScan "stopped at rank $lastMaximum with the thumb at the bottom -- genuine end of board"; "end_detected" }
        "unverified" { Write-MemoryScan "stopped at rank $lastMaximum; the scrollbar could not be read, so the end is UNVERIFIED"; "end_unverified" }
        "exhausted"  { Write-MemoryScan "stopped at rank $lastMaximum with the thumb mid-bar after $endRejections rejected ends -- board is TRUNCATED"; "truncated" }
        default {
            $scrollbar = Test-ListAtEnd
            if ($null -eq $scrollbar -or -not $scrollbar.found) { "end_unverified" }
            elseif ($scrollbar.at_bottom) { "end_detected" }
            else { "truncated" }
        }
    }
} else {
    "partial"
}
Ensure-Property $finalState "status" $finalStatus
Ensure-Property $finalState "target_reached" $complete
Ensure-Property $finalState "invalid_laps_from_rank" $invalidFromRank
Ensure-Property $finalState "scrollbar" $(if ($scrollbar) { $scrollbar } elseif ($lastEndSample) { $lastEndSample } else { $null })
# How many apparent ends the scrollbar overruled. A board that finished with this
# above zero would have been recorded short by the pre-2026-08-23 loop.
Ensure-Property $finalState "end_rejections" $endRejections
# What the scrollbar implies the whole board is, measured at the stop. On a board
# that stopped short this is the size of what is still missing.
Ensure-Property $finalState "scrollbar_implied_total" $lastEndEstimate
Ensure-Property $finalState "completed_at" (Get-Date).ToString("o")
$finalState | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $statePath -Encoding UTF8
Write-MemoryScan "capture finished with status=${finalStatus}: $runRoot"
[pscustomobject]@{
    run_id = $RunId
    output_root = $runRoot
    state = $statePath
    chunks = $chunkRoot
} | ConvertTo-Json
