<#
.SYNOPSIS
    Scan every route of every Rivals category at every performance class, driven
    by carousel index with OCR confirmation. Unattended, resumable, self-healing.

.DESCRIPTION
    Route names OCR unreliably, but the route carousel's ORDER is fixed, so routes
    are reached by index from a clean anchor and the landed route is confirmed by
    OCR and recorded. Categories can differ in their route set, so each category
    is enumerated once on entry (route count + anchor) before its index scan.

    Structure per category:
      * cold-start Forza into the category, enumerate its routes (count + a clean
        anchor route), then
      * for each route index x each class: warm-switch to that board (seconds,
        not a cold start), confirm the route by OCR, and page the leaderboard.

    A board that cannot be reached or scanned is recorded and skipped. Any
    navigation failure resets to a cold start for the next board, so a Forza
    crash mid-run costs one board, not the sweep. The manifest is rewritten after
    every board; -Resume continues where it stopped.

    The car codename catalogue is refreshed after each category so coverage
    accumulates across the whole run.

.EXAMPLE
    .\scripts\run_full_sweep.ps1
    .\scripts\run_full_sweep.ps1 -Categories "Road Racing","Street Racing" -Resume
#>
[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = ".\admin",
    [string[]] $Categories = @("Road Racing", "Street Racing", "Cross-Country", "Dirt Racing"),
    [string[]] $Classes = @("D", "C", "B", "A", "S1", "S2", "R"),
    [string] $CleanAnchorCandidates = "Highway Circuit,Soni Circuit,The Goliath,Narai-Juku Circuit",
    [string] $TotalRanks = "UntilEnd",
    # Restrict the run to specific carousel indices. Empty means every route in
    # the category. Use it to scan one board -- e.g. -RouteIndices 0 -Classes C --
    # through this script's bookkeeping and failure handling rather than by
    # calling the navigator and scanner by hand.
    [int[]] $RouteIndices = @(),
    [string] $OutputRoot = "data/memory_scans/full_sweep",
    # The analytics page is rebuilt from the finished boards after each one, so a
    # scan shows up without a second command. Off with -NoAnalyticsSite.
    # Scan with the stream scanner: keys and reads on separate threads, so nothing
    # scrolls past unread. The flow scanner it replaces shared one thread and was
    # blind for the 1.24 s of every key burst, which cost 9% of ranks on a served
    # board and 80% at a board start -- and no stride setting could fix that, because
    # the stride was never what was wrong.
    [switch] $FlowScanner,
    [switch] $NoAnalyticsSite,
    # Car names come from an in-memory catalogue that only holds the current
    # context's cars, so it is re-dumped and merged after every board.
    [switch] $NoCarCatalogue,
    [string] $AnalyticsOut = "data/analytics/rivals_auto_wertung.html",
    [string] $AnalyticsPython = "",
    [switch] $Resume,
    [switch] $WhatIfOnly,
    # Skip the live route enumeration and use a known route count + anchor. Avoids
    # the post-enumeration idle gap in which Forza tends to die. Only correct when
    # the count/anchor match the category (Road Racing: 23, "Highway Circuit").
    [switch] $SkipEnumeration,
    # The game is already sitting on a leaderboard from a previous invocation, so
    # switch boards from there instead of cold-starting. A cold start costs ~4 min
    # and a relaunch, and relaunching in quick succession is what breaks the Steam
    # launch; a warm switch is ~20 s. If the game did die in the meantime the nav
    # failure handling below forces a cold start anyway, so this is safe to pass.
    [switch] $WarmStart,
    [int] $RouteCount = 23,
    [string] $RouteAnchor = "Highway Circuit",
    # A single nav failure is usually the board's own fault (no personal time
    # posted for that track+class). Several in a row means the game is gone: a
    # zombie forzahorizon6 whose window cannot be focused leaves the navigator
    # OCR'ing the Steam desktop and failing every board in ~2 minutes, which is
    # how an overnight run burns the whole route list without scanning anything.
    # A VM reboot is the known reliable reset for that state, so escalate to one
    # instead of marching on.
    [int] $RecycleAfterFailures = 2,
    [int] $MaxRecycles = 6,
    # A forced stop makes the guest cold-boot, which took 7.5 minutes on
    # 2026-08-20 -- and because a failing New-PSSession blocks for 20-30 s on its
    # own, an 8 minute budget polled the guest only a handful of times and missed
    # it by seconds. Generous, because the alternative is losing the run.
    # A board that stops serving rows is not finished -- leaving it and coming back
    # makes the game serve again. Verified 2026-08-20: the S1 board stopped dead at
    # rank 1950 on two separate runs, and after re-entering it returned ranks
    # 1951-2000 straight away. Each pass costs one navigation plus a seek down to
    # where the last pass stopped, so this bounds how much a single board may cost.
    # Sweep-level give-up. A board that yields only its resident pages is not a
    # small board, it is a service that has stopped serving -- and the per-board
    # watchdog cannot see it, because such a board is not slow, it simply ends
    # after two or three pages. On 2026-08-21 fifteen boards in a row came back
    # with 149, 149, 149 ... rows over three hours, 5,186 rows in total, and the
    # sweep kept asking until the service answered with an outright "Server
    # Error". Stop the sweep instead, and let the operator decide when to resume.
    [int] $MinRowsPerBoard = 400,
    [int] $LowYieldBoardsBeforeStop = 3,
    [int] $MaxResumePasses = 10,
    # Resuming means scrolling back down to where the last pass stopped, one key
    # press per rank, so its cost grows with DEPTH -- not with the number of passes.
    # Measured 2026-08-21: resuming S1 at rank 23501 spent 27 min on the seek alone
    # (six rounds, never converging) and then 98 min for 700 ranks, while resuming
    # at rank 1951 earlier the same night was instant. Past this depth a pass costs
    # more than it returns, so record the board truncated and move on rather than
    # spend the night on one board. Raise it once the seek stops being one key per
    # rank at 18 ms.
    [int] $MaxResumeDepth = 6000,
    [int] $GuestReadyTimeoutMinutes = 20
)

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$scripts = $PSScriptRoot

function Write-Full { param([string] $Message) Write-Host "[full-sweep] $((Get-Date).ToString('HH:mm:ss')) $Message" }

# powershell.exe -File delivers comma lists as one string; split so either form works.
function Expand-List { param([string[]] $V) if (-not $V) { return @() } @($V | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
$Categories = Expand-List $Categories
$Classes = Expand-List $Classes

$outRoot = if ([IO.Path]::IsPathRooted($OutputRoot)) { $OutputRoot } else { Join-Path $workspace $OutputRoot }
New-Item -ItemType Directory -Force -Path $outRoot | Out-Null
$manifestPath = Join-Path $outRoot "full_sweep_manifest.json"

$done = @{}
$carried = @()
if ($Resume -and (Test-Path -LiteralPath $manifestPath)) {
    $prev = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $carried = @($prev.boards)
    foreach ($e in $carried) { if ($e.status -eq "complete") { $done[$e.key] = $true } }
    Write-Full "resuming; $($done.Count) board(s) already complete"
}

$results = New-Object System.Collections.ArrayList
function Save-Manifest {
    # One row per board key, the newest verdict winning. Carried entries are added
    # before this run's results, so "last wins" means a re-scanned board replaces
    # its own history instead of appearing twice.
    $latest = [ordered]@{}
    foreach ($entry in $results) {
        $key = [string]$entry.key
        if ([string]::IsNullOrEmpty($key)) { $key = [guid]::NewGuid().ToString() }
        $latest[$key] = $entry
    }
    $payload = [pscustomobject]@{
        updated_at = (Get-Date).ToUniversalTime().ToString("o")
        categories = $Categories
        classes = $Classes
        total_ranks = $TotalRanks
        boards = @($latest.Values)
    }
    $payload | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
}

function Update-CarCatalogue {
    # The carId -> codename array in memory holds the cars loaded for the CURRENT
    # context, so one dump never covers the roster: it covers the board that was
    # just scanned. Running it per board and merging is what turns 7% resolved names
    # into full coverage, and it costs seconds against a board that takes minutes.
    # Read-only against the game, and never fatal: a missing name is a cosmetic gap.
    param([string] $Reason = "")
    if ($NoCarCatalogue) { return }
    $updater = Join-Path $scripts "update_car_catalogue.ps1"
    if (-not (Test-Path -LiteralPath $updater)) { return }
    try {
        $before = 0
        $cataloguePath = Join-Path $workspace "config/fh6_car_catalogue.json"
        if (Test-Path -LiteralPath $cataloguePath) {
            try {
                $before = @((Get-Content -LiteralPath $cataloguePath -Raw |
                    ConvertFrom-Json).by_car_id.PSObject.Properties).Count
            } catch { $before = 0 }
        }
        $ErrorActionPreference = "Continue"
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $updater -VMName $VMName |
            Out-Null
        $after = $before
        if (Test-Path -LiteralPath $cataloguePath) {
            try {
                $after = @((Get-Content -LiteralPath $cataloguePath -Raw |
                    ConvertFrom-Json).by_car_id.PSObject.Properties).Count
            } catch { $after = $before }
        }
        Write-Full "car catalogue after ${Reason}: $after ids (+$($after - $before))"
    } catch {
        Write-Full "car catalogue refresh failed after ${Reason}: $($_.Exception.Message)"
    }
}

function Update-AnalyticsSite {
    # Rebuild the analytics page from whatever boards exist right now, so a finished
    # board is on the site without a second command. It reads the parquet output the
    # merge step has already written, needs no game and no VM, and must never be
    # able to end a sweep: a broken chart is not worth a lost night of scanning.
    param([string] $Reason = "")
    if ($NoAnalyticsSite) { return }
    $builder = Join-Path $scripts "build_analytics_site.py"
    if (-not (Test-Path -LiteralPath $builder)) { return }
    try {
        $python = if ($AnalyticsPython) { $AnalyticsPython } else { "python" }
        $target = if ([IO.Path]::IsPathRooted($AnalyticsOut)) { $AnalyticsOut } else { Join-Path $workspace $AnalyticsOut }
        $arguments = @($builder, "--root", $outRoot, "--out", $target)
        $output = & $python @arguments 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-Full "analytics site rebuilt after $Reason -> $target"
        } else {
            Write-Full "analytics rebuild failed after $Reason (exit $LASTEXITCODE): $output"
        }
    } catch {
        Write-Full "analytics rebuild threw after ${Reason}: $($_.Exception.Message)"
    }
}

function Restart-ForzaVM {
    # Graceful stop first: this VHD carries a GPU-partitioned Windows install and
    # a hard TurnOff risks the guest filesystem. Fall back to TurnOff only if the
    # guest refuses to go down.
    try {
        Write-Full "stopping $VMName"
        Stop-VM -Name $VMName -Force -ErrorAction SilentlyContinue
        $offBy = (Get-Date).AddSeconds(120)
        while ((Get-Date) -lt $offBy -and (Get-VM -Name $VMName).State -ne "Off") { Start-Sleep -Seconds 5 }
        if ((Get-VM -Name $VMName).State -ne "Off") {
            Write-Full "guest did not shut down; forcing power off"
            Stop-VM -Name $VMName -TurnOff -Force -ErrorAction Stop
            Start-Sleep -Seconds 5
        }
        Write-Full "starting $VMName"
        Start-VM -Name $VMName -ErrorAction Stop
    } catch {
        Write-Full "VM recycle failed: $($_.Exception.Message)"
        return $false
    }

    # Ready means PowerShell Direct answers; Steam autostarts in the guest and the
    # navigator's cold start launches the game itself.
    $credential = [pscredential]::new($VMUser, [Security.SecureString]::new())
    $deadline = (Get-Date).AddMinutes($GuestReadyTimeoutMinutes)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 10
        try {
            $probe = New-PSSession -VMName $VMName -Credential $credential -ErrorAction Stop
            $alive = Invoke-Command -Session $probe -ScriptBlock { $true } -ErrorAction Stop
            if ($alive) {
                # PowerShell Direct answers before the desktop session is usable.
                # The navigator's cold start launches the game through Steam, so
                # Steam being up is the readiness signal that actually matters.
                Write-Full "guest is answering; waiting for Steam"
                $steamDeadline = (Get-Date).AddMinutes(5)
                while ((Get-Date) -lt $steamDeadline) {
                    $steam = Invoke-Command -Session $probe -ScriptBlock {
                        [bool](Get-Process -Name steam -ErrorAction SilentlyContinue)
                    } -ErrorAction SilentlyContinue
                    if ($steam) { break }
                    Start-Sleep -Seconds 10
                }
                Remove-PSSession $probe -ErrorAction SilentlyContinue
                Write-Full "guest ready (steam=$steam); letting the desktop settle"
                Start-Sleep -Seconds 45
                return $true
            }
        } catch { }
    }
    Write-Full "guest did not answer PowerShell Direct within $GuestReadyTimeoutMinutes min"
    return $false
}

function Invoke-Nav {
    # Returns @{ ok=$bool; route=$string } ; route is the OCR-confirmed name.
    param([string] $Category, [int] $RouteIndex, [string] $Class, [string] $Anchor, [int] $Count, [switch] $Cold)
    $args = @(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $scripts "start_forza_navigation.ps1"),
        "-VMName", $VMName, "-RivalsMode", $Category, "-PerformanceClass", $Class,
        "-RouteIndex", [string]$RouteIndex, "-RouteAnchor", $Anchor, "-RouteCount", [string]$Count
    )
    if ($Cold) { $args += "-FreshStart" } else { $args += "-FromLeaderboard" }
    # A navigation failure must stay a per-board verdict. With the script-wide
    # ErrorActionPreference of Stop, anything the child writes to stderr becomes a
    # terminating NativeCommandError and takes the whole sweep down -- which is
    # how a stuck game ("Forza did not exit") ended the run with exit 1 instead of
    # being recorded and recycled. Function scope, so the rest keeps failing fast.
    $ErrorActionPreference = "Continue"
    $out = & powershell.exe @args 2>&1 | ForEach-Object { $line = $_.ToString(); Write-Host $line; $line }
    $ok = $LASTEXITCODE -eq 0 -and ($out -match "status: leaderboard_reached")
    $route = ""
    $m = $out | Select-String -Pattern "confirmed route on the board:\s*(.+)$" | Select-Object -Last 1
    if ($m) { $route = $m.Matches[0].Groups[1].Value.Trim() }

    # Why it failed decides what to do next. "Could not select class X" means the
    # menus answered and the game is healthy -- the board is simply not reachable
    # (no posted personal time, or its label does not OCR). Cold-starting for that
    # costs four minutes and a relaunch, and relaunching is what breaks the Steam
    # launch. A stuck/unknown screen or a game that never came up is the opposite
    # case and does need the cold start.
    # Only "could not select" is safe to treat as a healthy game: the menus
    # answered and named the thing they could not find. "Did not reach the
    # leaderboard within N cycles" looks similar but is not -- on Road Racing / S2
    # it was a cold start stuck in the loading screen pressing ESC, where staying
    # warm would have failed forever. Anything ambiguous falls through to the
    # streak counter, which escalates to a cold start and then a VM reboot.
    $reason = "unknown"
    if ($out -match "Could not select") { $reason = "selection" }
    # The menus answered and named what they could not find on screen. A cold
    # start is worth one try (the game may have drifted into an odd state), but a
    # VM reboot cannot fix a text that does not match, so this must not push the
    # streak towards one -- on 2026-08-20 an OCR miss on the route anchor bought
    # two reboots and cost the rest of the class row.
    if ($out -match "Could not find the anchor route") { $reason = "menu" }
    if ($out -match "Stuck in state|did not start within|is not running|Forza did not exit") { $reason = "infra" }
    return @{ ok = $ok; route = $route; reason = $reason }
}

function Get-CategoryRoutes {
    # Enumerate a category's carousel: returns @{ count=int; anchor=string } or $null.
    param([string] $Category)
    Write-Full "enumerating routes for '$Category'"
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scripts "start_forza_navigation.ps1") `
        -VMName $VMName -RivalsMode $Category -EnumerateRoutes -FreshStart | ForEach-Object { Write-Host $_ }
    # Pull the newest routes.json back from the guest run dir (copied to host).
    $navRoot = Join-Path $workspace "data/runtime/navigation"
    $routesFile = Get-ChildItem $navRoot -Recurse -Filter "routes.json" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $routesFile) { return $null }
    $data = Get-Content -LiteralPath $routesFile.FullName -Raw | ConvertFrom-Json
    $names = @($data.routes)
    if ($names.Count -lt 1) { return $null }
    # Choose an anchor: the first candidate whose (approx) name appears in the list.
    $anchor = $null
    foreach ($cand in ($CleanAnchorCandidates -split ',')) {
        $c = $cand.Trim()
        if ($names -contains $c) { $anchor = $c; break }
    }
    if (-not $anchor) { $anchor = $names[0] }  # fall back to whatever route 0 read as
    return @{ count = $names.Count; anchor = $anchor; names = $names }
}

# ---- build the scan plan ------------------------------------------------------
# Route indices per category are not known until enumeration, so the plan is built
# per category inside the loop. Categories are processed whole so warm switching
# stays within a category and cold starts happen only on category change.

if ($WhatIfOnly) {
    Write-Full "categories: $($Categories -join ', ')"
    Write-Full "classes: $($Classes -join ', ')"
    Write-Full "route counts are discovered per category at run time (enumeration)."
    Write-Full "-WhatIfOnly: nothing was run"
    return
}

# Carry EVERY prior entry forward, not just the complete ones. Dropping the rest
# assumed this run re-attempts them, which is only true when it covers their keys:
# with -Classes A a truncated S1 entry is never re-attempted, so it simply vanished
# from the manifest -- and on 2026-08-20 that pressure is what led to S1 being left
# labelled "complete" just to keep its row. Duplicates are not a concern because
# Save-Manifest keeps the last entry per key, and fresh verdicts are appended after
# these, so a re-attempted board still overwrites its own carried entry.
foreach ($entry in $carried) { [void]$results.Add($entry) }

$totalScanned = 0
$navFailStreak = 0
$recycles = 0
$lowYieldStreak = 0
$serviceExhausted = $false
foreach ($category in $Categories) {
    $info = $null
    if ($SkipEnumeration) {
        # Live enumeration is a separate navigation run that leaves Forza idle on
        # the route list afterwards -- and Forza reliably DIES during that idle
        # gap, so the first scan board then finds no game (it attached to a Steam
        # window). When the route count and anchor are already known, skipping
        # enumeration removes that death window: the first board cold-starts
        # straight into the category and warm switches follow while the game is
        # continuously driven (it survives active use, only idle).
        $info = @{ count = $RouteCount; anchor = $RouteAnchor }
        Write-Full "skip-enumeration: '$category' assumed $RouteCount routes, anchor '$RouteAnchor'"
    } else {
        try { $info = Get-CategoryRoutes -Category $category } catch { Write-Full "enumeration of '$category' failed: $($_.Exception.Message)" }
    }
    if (-not $info) {
        Write-Full "could not enumerate '$category'; recording it unreachable and moving on"
        [void]$results.Add([pscustomobject]@{ key = "$category|ENUM"; category = $category; status = "category_unreachable"; finished_at = (Get-Date).ToUniversalTime().ToString("o") })
        Save-Manifest
        continue
    }
    Write-Full "'$category': $($info.count) routes, anchor '$($info.anchor)'"

    # With enumeration, the game is already in the category on the route list, so
    # the first board goes WARM. With -SkipEnumeration there is no running game
    # yet, so the first board must cold-start (which also selects the category).
    # Either way a cold start is otherwise only forced after a nav failure.
    $freshCategory = [bool]$SkipEnumeration -and -not $WarmStart
    $wantedIndices = if ($RouteIndices.Count -gt 0) { @($RouteIndices) } else { @(0..($info.count - 1)) }
    foreach ($idx in $wantedIndices) {
        if ($idx -lt 0 -or $idx -ge $info.count) { Write-Full "route index $idx is outside '$category' (0..$($info.count - 1)); skipping"; continue }
        foreach ($class in $Classes) {
            $key = "$category|$idx|$class"
            if ($done.ContainsKey($key)) { continue }

            $nav = $null
            while ($true) {
                $nav = Invoke-Nav -Category $category -RouteIndex $idx -Class $class -Anchor $info.anchor -Count $info.count -Cold:$freshCategory
                if ($nav.ok) { $navFailStreak = 0; break }

                if ($nav.reason -eq "selection") {
                    # Healthy game, unreachable board: stay warm and move on.
                    Write-Full "nav failed for $key (reason: selection); game is healthy, staying warm"
                    break
                }
                if ($nav.reason -ne "menu") { $navFailStreak += 1 }
                $freshCategory = $true   # self-heal: force a cold start next
                Write-Full "nav failed for $key (failure $navFailStreak in a row, reason: $($nav.reason))"
                # An infra failure is never the board's fault -- an unkillable game
                # or a screen that never resolves says nothing about whether this
                # (route, class) has a posted time. Escalate at once and retry the
                # same board, instead of burning it and recycling for the next one.
                $escalate = $navFailStreak -ge $RecycleAfterFailures -or $nav.reason -eq "infra"
                if ($escalate -and $recycles -lt $MaxRecycles) {
                    $recycles += 1
                    Write-Full "recycling the VM (recycle $recycles/$MaxRecycles), then retrying $key"
                    if (Restart-ForzaVM) {
                        $navFailStreak = 0
                        continue        # retry this same board on a fresh guest
                    }
                    # Marking this board unreachable and carrying on is wrong: with
                    # no guest there is nothing to navigate, so every remaining
                    # board would be recorded unreachable within minutes and the
                    # manifest would claim the matrix was covered. Stop instead --
                    # -Resume picks up exactly here once the VM is healthy.
                    Write-Full "recycle did not bring the guest back; stopping the sweep at $key"
                    [void]$results.Add([pscustomobject]@{ key = $key; category = $category; route_index = $idx; performance_class = $class; status = "aborted_guest_unavailable"; finished_at = (Get-Date).ToUniversalTime().ToString("o") })
                    Save-Manifest
                    Write-Full "manifest: $manifestPath"
                    exit 2
                }
                break
            }
            if (-not $nav.ok) {
                Write-Full "recording $key unreachable"
                [void]$results.Add([pscustomobject]@{ key = $key; category = $category; route_index = $idx; performance_class = $class; status = "unreachable"; finished_at = (Get-Date).ToUniversalTime().ToString("o") })
                Save-Manifest
                continue
            }
            $freshCategory = $false
            $routeName = if ($nav.route) { $nav.route } else { "route_$idx" }
            $runId = "fs_{0}_{1}_idx{2:00}_{3}_{4}" -f ($category -replace '[^A-Za-z0-9]',''), ($routeName -replace '[^A-Za-z0-9]',''), $idx, $class, (Get-Date -Format "yyyyMMdd_HHmmss")

            # One board can take several passes. A scan ends when rows stop arriving,
            # which is NOT the same as the board ending -- and the difference is now
            # decided by the on-screen scrollbar rather than assumed. When the scan
            # reports "truncated", re-entering the board makes the game serve again,
            # so continue from where it stopped instead of banking a short board.
            $status = "complete"; $note = ""
            $passRunIds = New-Object System.Collections.ArrayList
            $passStartRank = 1
            $reachedRank = 0
            $scanStatus = ""

            for ($pass = 1; $pass -le $MaxResumePasses; $pass += 1) {
                $passRunId = if ($pass -eq 1) { $runId } else { "{0}_p{1:00}" -f $runId, $pass }

                if ($pass -gt 1) {
                    Write-Full "$key stopped at rank $reachedRank; re-seating the board and resuming at $passStartRank (pass $pass/$MaxResumePasses)"
                    $again = Invoke-Nav -Category $category -RouteIndex $idx -Class $class -Anchor $info.anchor -Count $info.count
                    if (-not $again.ok) {
                        $status = "truncated"
                        $note = "resume pass $pass could not re-enter the board; stopped at rank $reachedRank"
                        break
                    }
                }

                [void]$passRunIds.Add($passRunId)
                if ($FlowScanner) {
                    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scripts "start_vm_stream_scan.ps1") `
                        -VMName $VMName -RunId $passRunId -Track $routeName `
                        -PerformanceClass $class -RivalsMode $category `
                        -HostOutputRoot $OutputRoot | ForEach-Object { Write-Host $_ }
                } else {
                    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scripts "start_vm_memory_leaderboard_scan.ps1") `
                        -VMName $VMName -Track $routeName -PerformanceClass $class -RivalsMode $category `
                        -TotalRanks $TotalRanks -RunId $passRunId -HostOutputRoot $OutputRoot `
                        -StartRank $passStartRank `
                        -SkipNavigation -KeepForzaRunning | ForEach-Object { Write-Host $_ }
                }
                if ($LASTEXITCODE -ne 0) {
                    $status = "scan_failed"; $note = "pass $pass exit $LASTEXITCODE"
                    break
                }

                $scanStatus = ""; $scanMax = 0
                $scanStatePath = Join-Path $outRoot "$passRunId/state.json"
                if (Test-Path -LiteralPath $scanStatePath) {
                    try {
                        $scanState = Get-Content -LiteralPath $scanStatePath -Raw | ConvertFrom-Json
                        $scanStatus = [string]$scanState.status
                        if ($null -ne $scanState.maximum_rank) { $scanMax = [int]$scanState.maximum_rank }
                    } catch { Write-Full "could not read scan state for $passRunId" }
                }

                # No forward movement means another pass would repeat this one for
                # nothing, so stop rather than burn the whole pass budget in place.
                if ($scanMax -le $reachedRank) {
                    $status = "truncated"
                    $note = "pass $pass gained nothing past rank $reachedRank"
                    break
                }
                $reachedRank = $scanMax

                switch ($scanStatus) {
                    "completed"      { $status = "complete"; $note = "" }
                    "end_detected"   { $status = "complete"; $note = "scrollbar confirmed the end of the board at rank $reachedRank" }
                    # The valid section ended and the invalid tail began. That is a
                    # finished board, not a short one: the scrollbar sits mid-bar here
                    # because the game still has ~22% of the list left to show, and all
                    # of it is laps that did not count.
                    "valid_section_complete" { $status = "complete"; $note = "valid section complete at rank $reachedRank; invalid laps follow and were skipped" }
                    "end_unverified" { $status = "end_unverified"; $note = "stopped at rank $reachedRank; scrollbar could not be read" }
                    "partial"        { $status = "partial"; $note = "stopped at rank $reachedRank without reaching the board end" }
                    "truncated"      { $status = "truncated"; $note = "stopped at rank $reachedRank; scrollbar was not at the bottom" }
                    "degraded"       { $status = "truncated"; $note = "page fetch slowed to a crawl at rank $reachedRank; re-seating" }
                    default          { $status = "end_unverified"; $note = "pass $pass reported status '$scanStatus'" }
                }
                # "degraded" is resumable for the same reason "truncated" is: both mean
                # the board view stopped serving properly, and re-entering it is the
                # cure for both.
                if ($scanStatus -ne "truncated" -and $scanStatus -ne "degraded") { break }
                if ($MaxResumeDepth -gt 0 -and $reachedRank -ge $MaxResumeDepth) {
                    $status = "truncated"
                    $note = "stopped at rank $reachedRank; deeper than the $MaxResumeDepth resume limit, so the seek back would cost more than the pass returns"
                    Write-Full "$key stopped at rank $reachedRank, past the resume depth limit ($MaxResumeDepth); recording it truncated"
                    break
                }
                $passStartRank = $reachedRank + 1
                if ($pass -eq $MaxResumePasses) {
                    $note = "still truncated at rank $reachedRank after $pass pass(es)"
                }
            }

            # Sum what the passes actually yielded. A per-pass merge report only
            # describes its own slice, so a multi-pass board needs them added up or
            # the manifest understates it by every pass but the last.
            $rows = $null; $maxRank = $null; $missing = $null
            foreach ($passRunId in $passRunIds) {
                $reportPath = Join-Path $outRoot "$passRunId/combined/merge_report.json"
                if (-not (Test-Path -LiteralPath $reportPath)) { continue }
                try {
                    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
                    $rows = [int]$rows + [int]$report.rows
                    $missing = [int]$missing + [int]$report.missing_rank_count
                    if ($null -eq $maxRank -or [int]$report.maximum_rank -gt [int]$maxRank) { $maxRank = $report.maximum_rank }
                } catch { Write-Full "could not read merge report for $passRunId" }
            }

            [void]$results.Add([pscustomobject]@{
                key = $key; category = $category; route_index = $idx; route_name = $routeName
                performance_class = $class; run_id = $runId; status = $status; note = $note
                passes = $passRunIds.Count; pass_run_ids = @($passRunIds)
                rows = $rows; max_rank = $maxRank; missing_ranks = $missing
                finished_at = (Get-Date).ToUniversalTime().ToString("o")
            })
            Save-Manifest
            Update-CarCatalogue -Reason $key
            Update-AnalyticsSite -Reason $key
            if ($status -eq "complete") { $totalScanned += 1 }

            # Yield, not speed, is what exposes a service that has stopped serving.
            if ($null -ne $rows -and [int]$rows -lt $MinRowsPerBoard) {
                $lowYieldStreak += 1
                Write-Full "$key yielded only $rows row(s) ($lowYieldStreak/$LowYieldBoardsBeforeStop below $MinRowsPerBoard)"
                if ($lowYieldStreak -ge $LowYieldBoardsBeforeStop) {
                    Write-Full "$lowYieldStreak boards in a row returned almost nothing; the leaderboard service is not serving. Stopping the sweep rather than asking it again."
                    $serviceExhausted = $true
                    break
                }
            } else {
                $lowYieldStreak = 0
            }
            if ($serviceExhausted) { break }
        }
        if ($serviceExhausted) { break }
    }

    if ($serviceExhausted) { break }

    # Refresh the car codename catalogue after each category (accumulates coverage).
    try {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scripts "update_car_catalogue.ps1") -VMName $VMName | ForEach-Object { Write-Host $_ }
    } catch { Write-Full "car-catalogue refresh failed (non-fatal): $($_.Exception.Message)" }
}

$complete = @($results | Where-Object { $_.status -eq "complete" }).Count
$unreach = @($results | Where-Object { $_.status -like "*unreachable*" }).Count
$failed = @($results | Where-Object { $_.status -eq "scan_failed" }).Count
Write-Full "full sweep finished: $complete complete, $unreach unreachable, $failed scan-failed"
Write-Full "manifest: $manifestPath"
exit 0
