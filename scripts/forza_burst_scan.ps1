<#
.SYNOPSIS
    Measure, live, which key-sending strategy actually pages a leaderboard fastest,
    then keep scanning with the winner.

.DESCRIPTION
    The resident-reader split (`forza_paged_scan.ps1`) removed per-page process
    start and the fixed settle, and still only reached 217 ranks/min live against
    a 750/min baseline. So the remaining cost is not the reader: it is how the keys
    are sent, and what the loop throws away.

    Two things change here.

    * **Harvest, do not request.** The old loop asks for ONE rank and discards the
      rest of the block it just read. When the game has prefetched ahead, those
      rows are already paid for. `{"harvest":1}` returns everything resident, so
      the loop can keep scrolling instead of blocking on a rank model, and rows
      the cursor has already passed still land in the output.
    * **The press is the bottleneck, so measure it.** 50 separate `SendWait` calls
      18 ms apart cost ~0.9-1.2 s per 50-rank page by themselves. `{DOWN 50}` is
      one call; `{PGDN}` may be one key for a whole page. Neither is known to work
      on this build, and guessing is how two sessions were spent, so each
      candidate drives a real board for a fixed slice of wall clock and the loop
      continues with whichever delivered ranks fastest.

    The cursor is not observable -- only "did new rows arrive" is. So a strategy is
    judged on ranks/min AND on the gaps it leaves, and both go in the summary.

.EXAMPLE
    .\forza_burst_scan.ps1 -Output C:\ForzaAutomation\data\burst\rows.jsonl
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Output,
    [string] $Checkpoint,
    [string] $Summary,
    [string] $TimingCsv,
    [string] $Python = "C:\ForzaTools\Python312\python.exe",
    [string] $ServerScript = "C:\ForzaAutomation\scripts\forza_row_server.py",
    [string] $ProfilePath = "C:\ForzaAutomation\build_profile.json",
    [int] $StartRank = 1,
    # Candidates, in the order they are tried. "downN" = N separate SendWait calls
    # N ms apart (down18 is the proven baseline), "burst" = one {DOWN n} call,
    # "pgdn" = {PGDN} presses.
    [string] $Strategies = "down18,down8,burst,pgdn",
    [int] $CalibrateSeconds = 50,
    # Rows pressed past the bottom of the loaded block. The game fetches when the
    # cursor CROSSES that bottom; parking on it fetches nothing, which is the
    # documented cause of both the mod-50 truncations and the rank-401 stall.
    [int] $Cross = 6,
    [int] $PgdnStride = 50,
    [int] $PgdnDelayMs = 40,
    # "hold": press DOWN once, keep it down for this long, release. SendKeys paces
    # itself at ~31 ms per key, which caps discrete pressing at ~1,900 ranks/min no
    # matter how the loop is arranged. A held key costs one press for a whole scroll
    # -- IF the game samples key state per frame rather than consuming key events.
    [int] $HoldMs = 400,
    # "wheel": the leaderboard has a scrollbar, so it is a mouse UI. Wheel events are
    # not subject to the ~31 ms keyboard repeat ceiling and a notch usually moves
    # several rows, which is the only remaining way past ~1,900 ranks/min without
    # breaking the encrypted network path.
    [int] $WheelNotches = 10,
    [int] $WheelDelayMs = 15,
    [int] $WheelX = 900,
    [int] $WheelY = 560,
    [int] $HarvestPollMs = 40,
    [int] $FetchTimeoutMs = 6000,
    # 50 measured best (659 ranks/min). Individual cycles delivering 49 ranks off
    # 20 presses suggested a smaller advance would win, and a run at 20 came back
    # at 150/min -- but it also ran 3,000 ranks deeper into the board than the run
    # it was compared against, so neither number is clean. Do not re-tune this
    # without entering a fresh board per variant.
    [int] $MaxPressPerCycle = 50,
    [int] $StallCycles = 4,
    [int] $MaxSeconds = 900,
    [int] $MaxRanks = 0,
    # Flow mode: never wait for a particular rank. Press, harvest whatever is
    # resident, press again. The read costs ~20 ms against ~600 ms of keys, so the
    # list scrolls essentially continuously and the reader just keeps up -- which is
    # the operator's "spam the down key and record it" idea with memory as the
    # sensor instead of a camera. The waiting loop is what made the old scan spend
    # 100 s on a page the game had not served yet.
    [switch] $Flow,
    # The stride that is right depends on where the board is. Deep in an already
    # served board, 40 rows per cycle measured 1,426 ranks/min with 9% missed; at
    # rank 1, where the game only fetches on demand, the same 40 jumped over ranks
    # that had not been served yet and missed 80%. So adapt: grow the stride while
    # the harvest keeps up with the pressing, shrink it when it does not.
    [switch] $AdaptiveStride,
    [int] $MinStride = 8,
    [int] $MaxStride = 60,
    [int] $FlowStopAfterDeadCycles = 25,
    # Records the reader decodes per address. 80 barely covers one 50-row page, so
    # a page that lands slightly off the known address reads as nothing at all --
    # 12 of 38 cycles on 2026-08-22, each costing the 6 s fetch timeout.
    [int] $ReadRecords = 240
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms

function Write-Burst { param([string] $Message) Write-Host "[burst] $Message" }

$game = Get-Process -Name forzahorizon6 -ErrorAction Stop |
    Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $game) { throw "Forza has no main window; nothing to drive." }

Add-Type -Namespace BurstWin -Name N -MemberDefinition @"
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr h);
[DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, System.UIntPtr extra);
[DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, int data, System.UIntPtr extra);
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern bool GetCursorPos(out System.Drawing.Point p);
"@ -ReferencedAssemblies System.Drawing

$script:gameHandle = $game.MainWindowHandle
$script:gamePid = $game.Id

function Focus-Game {
    if ([BurstWin.N]::GetForegroundWindow() -eq $script:gameHandle) { return }
    $ok = [BurstWin.N]::SetForegroundWindow($script:gameHandle)
    if (-not $ok) {
        # The proven loop's fallback. SetForegroundWindow refuses when the calling
        # process does not own the foreground, and a silent refusal looks exactly
        # like a game that ignores input: 0 ranks and dead cycles.
        $shell = New-Object -ComObject WScript.Shell
        $ok = $shell.AppActivate([int]$script:gamePid)
    }
    if (-not $ok) { Write-Burst "WARNING focus did not stick" }
    Start-Sleep -Milliseconds 60
}

function Send-Rows {
    # Move the cursor down by $Count rows using $Strategy. Keys aimed past the end
    # of the served list are eaten by the game, so this is a request, not a fact.
    param([int] $Count, [string] $Strategy)
    if ($Count -le 0) { return }
    Focus-Game
    if ($Strategy -eq "pgdn") {
        $presses = [Math]::Max(1, [int][Math]::Round($Count / [double]$PgdnStride))
        for ($i = 0; $i -lt $presses; $i++) {
            [System.Windows.Forms.SendKeys]::SendWait("{PGDN}")
            if ($PgdnDelayMs -gt 0) { Start-Sleep -Milliseconds $PgdnDelayMs }
        }
        return
    }
    if ($Strategy -eq "burst") {
        [System.Windows.Forms.SendKeys]::SendWait("{DOWN $Count}")
        return
    }
    if ($Strategy -eq "wheel") {
        # Park the pointer over the list first: Forza's menus are mouse-aware, and a
        # wheel event goes to whatever is under the cursor.
        [void][BurstWin.N]::SetCursorPos($WheelX, $WheelY)
        $notches = [Math]::Max(1, [int][Math]::Round($Count / 3.0))
        for ($i = 0; $i -lt $notches; $i++) {
            # 0x0800 = MOUSEEVENTF_WHEEL; -120 is one notch towards the user (down).
            [BurstWin.N]::mouse_event(0x0800, 0, 0, -120, [UIntPtr]::Zero)
            if ($WheelDelayMs -gt 0) { Start-Sleep -Milliseconds $WheelDelayMs }
        }
        return
    }
    if ($Strategy -eq "hold") {
        # 0x28 = VK_DOWN, 0x0002 = KEYEVENTF_KEYUP. The count is ignored: how far the
        # list travels is the game's own repeat rate times $HoldMs, which is exactly
        # the thing being measured.
        [BurstWin.N]::keybd_event(0x28, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds $HoldMs
        [BurstWin.N]::keybd_event(0x28, 0, 2, [UIntPtr]::Zero)
        return
    }
    $delay = 18
    if ($Strategy -match '^down(\d+)$') { $delay = [int]$Matches[1] }
    for ($i = 0; $i -lt $Count; $i++) {
        [System.Windows.Forms.SendKeys]::SendWait("{DOWN}")
        if ($delay -gt 0) { Start-Sleep -Milliseconds $delay }
    }
}

# --- resident reader -----------------------------------------------------------
$info = New-Object System.Diagnostics.ProcessStartInfo
$info.FileName = $Python
$info.Arguments = "`"$ServerScript`" --profile `"$ProfilePath`" --timeout-ms 4000 --read-records $ReadRecords"
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

$script:seen = New-Object 'System.Collections.Generic.HashSet[int]'
$script:maxSeen = $StartRank - 1
$script:bottom = $StartRank - 1
$script:writer = $null
# rank -> newest row whose gamertag has not arrived yet. Held back from the output
# but counted as progress, because the game HAS served it: the name is the last
# field to populate, and treating a nameless row as "nothing arrived" is what made
# every fast read look like a stalled board.
$script:pending = @{}
# Consecutive cycles whose new rows were all invalid laps. A board lists every valid
# lap and then every invalid one, so that is the end of the useful board -- 22.3% of
# every board is the tail. Three in a row rather than one, because near the boundary
# a harvest legitimately holds both kinds.
$script:tailCycles = 0
$script:invalidFromRank = 0
$script:counted = New-Object 'System.Collections.Generic.HashSet[int]'

function Get-Collected {
    # Ranks the game has served us, named or not.
    return $script:seen.Count + $script:pending.Count
}

function Harvest {
    # Read everything resident and file the new rows. Returns ranks newly observed.
    $answer = Ask @{ harvest = 1 }
    if (-not $answer.ok) { return 0 }
    $gained = 0
    foreach ($row in $answer.rows) {
        $rank = [int]$row.rank
        if ($rank -lt 1) { continue }
        if ($rank -gt $script:bottom) { $script:bottom = $rank }
        if ($script:seen.Contains($rank)) { continue }
        $fresh = -not $script:pending.ContainsKey($rank)
        if ($rank -gt $script:maxSeen) { $script:maxSeen = $rank }
        if ($fresh) { $gained = $gained + 1 }
        if (-not $row.gamertag) {
            # Keep the newest copy and try again on the next harvest; the name
            # normally lands within a page or two.
            $script:pending[$rank] = $row
            continue
        }
        [void]$script:pending.Remove($rank)
        [void]$script:seen.Add($rank)
        $script:writer.WriteLine(($row | ConvertTo-Json -Compress -Depth 4))
    }
    # Track the boundary: `is_clean` false on every new row means the harvest is
    # looking at the invalid tail.
    $fresh = @($answer.rows | Where-Object { $_.rank -and -not $script:counted.Contains([int]$_.rank) })
    foreach ($row in $answer.rows) { [void]$script:counted.Add([int]$row.rank) }
    if ($fresh.Count -gt 0) {
        $valid = @($fresh | Where-Object { $_.is_clean })
        if ($valid.Count -eq 0) {
            $script:tailCycles = $script:tailCycles + 1
            if (-not $script:invalidFromRank) {
                $script:invalidFromRank = [int](($fresh | Measure-Object -Property rank -Minimum).Minimum)
            }
        } else {
            $script:tailCycles = 0
        }
    }
    return $gained
}

function Invoke-Phase {
    # Drive the board with one strategy for a slice of wall clock.
    param([string] $Strategy, [int] $Seconds)
    $phaseStart = Get-Date
    $startRanks = Get-Collected
    $cycles = 0
    $dead = 0
    $deadRun = 0
    $presses = 0
    while ($true) {
        if (((Get-Date) - $phaseStart).TotalSeconds -ge $Seconds) { break }
        if (((Get-Date) - $script:runStarted).TotalSeconds -ge $MaxSeconds) { break }
        if ($MaxRanks -gt 0 -and (Get-Collected) -ge $MaxRanks) { break }
        $stallLimit = if ($Flow) { $FlowStopAfterDeadCycles } else { $StallCycles }
        if ($deadRun -ge $stallLimit) { break }
        if ($script:tailCycles -ge 3) {
            Write-Burst "invalid laps from rank $($script:invalidFromRank); the valid section of this board is done"
            break
        }

        $cycleStart = Get-Date
        $before = $script:maxSeen
        # Cross the bottom of what is loaded; never walk further than one burst
        # past it, or the presses land past the served list and are eaten.
        if ($Flow) {
            # A fixed stride, chosen so the reader's window (read-records) covers far
            # more than one stride: nothing can scroll past unread between two reads.
            $press = if ($AdaptiveStride) { $script:stride } else { $MaxPressPerCycle }
        } else {
        $target = $script:bottom + $Cross
        $press = $target - $script:cursor
        if ($press -le 0) { $press = $Cross }
        # A dead cycle means the model thinks the cursor is past the bottom while
        # the game disagrees -- keys eaten at the end of the served list put the
        # model ahead of the truth. Escalate the pressure instead of re-pressing
        # the same six rows: that is what turned eight dead reads into two in the
        # main loop, and re-pressing six is exactly how the paged scan died at 401.
        if ($deadRun -gt 0) {
            $floor = 10 * ($deadRun + 1)
            if ($press -lt $floor) { $press = $floor }
        }
        # Allowing the crossing margin on top of the cap was tried (2026-08-22) on
        # the theory that clamping to exactly one page parks the cursor ON the page
        # bottom, where the game does not fetch. It measured worse. Unresolved,
        # because that run was also deeper in the board.
        if ($press -gt $MaxPressPerCycle) { $press = $MaxPressPerCycle }
        }
        # Keys aimed past the end of the served list are eaten, so the true cursor
        # can never be below the loaded bottom. Anchoring on it stops the model
        # drifting ahead and then pressing three keys where fifty are needed --
        # which is how the sustained phase fell to 48/min.
        if ($script:cursor -gt $script:bottom) { $script:cursor = $script:bottom }
        Send-Rows -Count $press -Strategy $Strategy
        $script:cursor = $script:cursor + $press
        $presses = $presses + $press
        $keysMs = ((Get-Date) - $cycleStart).TotalMilliseconds

        # Poll instead of settling: a page that lands in 60 ms costs 60 ms.
        $gained = 0
        $waitStart = Get-Date
        if ($Flow) {
            # One read, no waiting. A rank that is not resident yet will be resident
            # a cycle or two later, and the harvest picks it up then -- there is no
            # reason to hold the keys still while the game fetches.
            $gained = Harvest
        } else {
            while ($true) {
                $gained = $gained + (Harvest)
                if ($script:maxSeen -gt $before) { break }
                if (((Get-Date) - $waitStart).TotalMilliseconds -ge $FetchTimeoutMs) { break }
                Start-Sleep -Milliseconds $HarvestPollMs
            }
        }
        $cycles = $cycles + 1
        if ($AdaptiveStride -and $Flow) {
            # `gained` is how many NEW ranks the harvest actually returned. Close to
            # the stride means the game is keeping up and the cursor can move faster;
            # far below means we are outrunning what has been served, and every extra
            # row pressed is a row lost.
            if ($gained -ge ($press * 0.8)) {
                $script:stride = [Math]::Min($MaxStride, $script:stride + 4)
            } elseif ($gained -lt ($press * 0.45)) {
                $script:stride = [Math]::Max($MinStride, $script:stride - 6)
            }
        }
        if ($script:maxSeen -gt $before) {
            $deadRun = 0
        } else {
            $dead = $dead + 1
            $deadRun = $deadRun + 1
            # Harvest reads only the addresses it already knows. Pages alternate
            # between heap arenas, so "no new rows" is as often a moved block as a
            # board that has stopped -- and the opening block can be a stale one
            # from the previous screen, which reads as 39 rows that never grow.
            # The `want` path is the one that searches for a SPECIFIC rank and
            # remembers where it found it, so spend one request on it.
            $probe = $null
            if ($deadRun -ge 2) {
                # find_rank_address sweeps the whole process: 24-28 s measured. Worth
                # it when the block has really moved, ruinous on a merely late page.
                $probe = Ask @{ want = ($script:maxSeen + 1); timeout_ms = 2500 }
            }
            if ($probe -and $probe.ok) {
                foreach ($row in $probe.rows) {
                    $rank = [int]$row.rank
                    if ($rank -lt 1) { continue }
                    if ($rank -gt $script:bottom) { $script:bottom = $rank }
                    if (-not $script:seen.Add($rank)) { continue }
                    if ($rank -gt $script:maxSeen) { $script:maxSeen = $rank }
                    $script:writer.WriteLine(($row | ConvertTo-Json -Compress -Depth 4))
                }
                if ($script:maxSeen -gt $before) {
                    Write-Burst "relocated: rank $($before + 1) found after a dead cycle"
                    $deadRun = 0
                }
            }
        }

        [void]$script:timings.Add([pscustomobject]@{
            strategy = $Strategy
            cycle = $cycles
            pressed = $press
            keys_ms = [int]$keysMs
            wait_ms = [int]((Get-Date) - $waitStart).TotalMilliseconds
            cycle_ms = [int]((Get-Date) - $cycleStart).TotalMilliseconds
            gained = $gained
            max_seen = $script:maxSeen
            bottom = $script:bottom
            cursor = $script:cursor
        })
        if ($Checkpoint) {
            @{ ranks_collected = (Get-Collected); named = $script:seen.Count
               pending_names = $script:pending.Count; highest_rank = $script:maxSeen
               strategy = $Strategy; cycles = $cycles } |
                ConvertTo-Json | Set-Content -LiteralPath $Checkpoint -Encoding UTF8
        }
    }
    $seconds = [Math]::Max(0.001, ((Get-Date) - $phaseStart).TotalSeconds)
    return @{ ranks = ((Get-Collected) - $startRanks); seconds = $seconds
              cycles = $cycles; dead = $dead; presses = $presses }
}

try {
    $ready = $server.StandardOutput.ReadLine() | ConvertFrom-Json
    if (-not $ready.ok) { throw "row server did not come up: $($ready.reason)" }
    Write-Burst "reader ready: stride $($ready.stride), $($ready.addresses) address(es)"

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Output) | Out-Null
    $script:writer = [System.IO.StreamWriter]::new($Output, $true)
    $script:timings = New-Object System.Collections.ArrayList
    $script:cursor = $StartRank - 1
    $script:stride = $MaxPressPerCycle
    $script:runStarted = Get-Date
    $results = [ordered]@{}

    # One harvest before anything is pressed: on a freshly opened board the first
    # page is already resident, and it is what says where the cursor starts.
    [void](Harvest)
    # Anchoring the cursor on the resident bottom here is defensible in principle
    # -- entering the loop believing the cursor is at StartRank while the block ends
    # at 6550 makes every press count meaningless -- and it measured worse (270/min
    # against 535). Left off until it can be tested on a board entered at rank 1.
    Write-Burst "opening harvest: $(Get-Collected) rows resident, bottom $($script:bottom); cursor anchored at $($script:cursor)"

    foreach ($strategy in @($Strategies -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
        if (((Get-Date) - $script:runStarted).TotalSeconds -ge $MaxSeconds) { break }
        $spanFrom = $script:maxSeen + 1
        $phase = Invoke-Phase -Strategy $strategy -Seconds $CalibrateSeconds
        $rate = $phase.ranks / ($phase.seconds / 60.0)
        $results[$strategy] = [pscustomobject]@{
            strategy = $strategy
            ranks = $phase.ranks
            seconds = [Math]::Round($phase.seconds, 1)
            ranks_per_min = [Math]::Round($rate, 0)
            cycles = $phase.cycles
            dead_cycles = $phase.dead
            presses = $phase.presses
            rank_span = "$spanFrom..$($script:maxSeen)"
        }
        Write-Burst ("{0}: {1} ranks in {2:N0}s -> {3:N0}/min ({4} cycles, {5} dead{6})" -f `
            $strategy, $phase.ranks, $phase.seconds, $rate, $phase.cycles, $phase.dead,
            $(if ($AdaptiveStride) { ", stride ended at $($script:stride)" } else { "" }))
    }

    # The winner is the fastest candidate that actually moved the board.
    $winner = $null
    foreach ($key in $results.Keys) {
        $entry = $results[$key]
        if ($entry.ranks -lt 20) { continue }
        if ($null -eq $winner -or $entry.ranks_per_min -gt $winner.ranks_per_min) { $winner = $entry }
    }
    if ($winner) {
        $rest = $MaxSeconds - ((Get-Date) - $script:runStarted).TotalSeconds
        Write-Burst "winner: $($winner.strategy) at $($winner.ranks_per_min)/min; running it out for $([int]$rest)s"
        if ($rest -gt 10) {
            $phase = Invoke-Phase -Strategy $winner.strategy -Seconds ([int]$rest)
            $rate = $phase.ranks / ($phase.seconds / 60.0)
            $results["sustained"] = [pscustomobject]@{
                strategy = "sustained_$($winner.strategy)"
                ranks = $phase.ranks
                seconds = [Math]::Round($phase.seconds, 1)
                ranks_per_min = [Math]::Round($rate, 0)
                cycles = $phase.cycles
                dead_cycles = $phase.dead
                presses = $phase.presses
                rank_span = "sustained"
            }
            Write-Burst ("sustained {0}: {1} ranks in {2:N0}s -> {3:N0}/min" -f `
                $winner.strategy, $phase.ranks, $phase.seconds, $rate)
        }
    } else {
        Write-Burst "no candidate moved the board; nothing to run out"
    }

    # Gaps are the other half of the verdict: a fast strategy that skips ranks is
    # not a faster scan, it is a shorter one.
    # Anything still nameless at the end goes out as it is, flagged. A rank held
    # back forever would be a rank lost, which is worse than a row without a name.
    foreach ($rank in @($script:pending.Keys)) {
        $script:writer.WriteLine(($script:pending[$rank] | ConvertTo-Json -Compress -Depth 4))
    }
    $script:writer.Flush()
    $missing = 0
    for ($rank = $StartRank; $rank -le $script:maxSeen; $rank++) {
        if (-not $script:seen.Contains($rank) -and -not $script:pending.ContainsKey($rank)) {
            $missing = $missing + 1
        }
    }
    $totalMinutes = [Math]::Max(0.001, ((Get-Date) - $script:runStarted).TotalMinutes)
    Write-Burst ("total: {0} ranks ({1} still nameless), max {2}, {3} missing, {4:N1} min -> {5:N0}/min overall" -f `
        (Get-Collected), $script:pending.Count, $script:maxSeen, $missing, $totalMinutes,
        ((Get-Collected) / $totalMinutes))

    if ($TimingCsv) {
        $script:timings | Export-Csv -LiteralPath $TimingCsv -NoTypeInformation -Encoding UTF8
    }
    if ($Summary) {
        [pscustomobject]@{
            started_at = $script:runStarted.ToUniversalTime().ToString("o")
            start_rank = $StartRank
            ranks_collected = (Get-Collected)
            named_rows = $script:seen.Count
            nameless_rows = $script:pending.Count
            max_rank = $script:maxSeen
            missing_ranks = $missing
            overall_ranks_per_min = [Math]::Round(((Get-Collected) / $totalMinutes), 0)
            cross = $Cross
            phases = @($results.Values)
        } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $Summary -Encoding UTF8
    }
} finally {
    if ($script:writer) { $script:writer.Close() }
    try { $server.StandardInput.WriteLine('{"quit":1}'); $server.StandardInput.Flush() } catch {}
    Start-Sleep -Milliseconds 300
    if (-not $server.HasExited) { $server.Kill() }
}
