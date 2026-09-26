<#
.SYNOPSIS
    Scan many Rivals boards back to back: navigate, page the whole leaderboard,
    move to the next board, without a human in the loop.

.DESCRIPTION
    The two halves already exist and are both verified on build 6.420.696.0:

      scripts/start_forza_navigation.ps1        drives the menus to a board
      scripts/start_vm_memory_leaderboard_scan.ps1  pages that board's ranks

    What was missing is the loop between them, and the reason it was missing is
    that the navigator used to treat "a leaderboard is on screen" as success --
    so starting it while a board was already open returned that board instead of
    the wanted one. -FromLeaderboard fixes that by backing out to the route list
    first, which is why this script only cold-starts the game once.

    That distinction is what makes a sweep affordable. A cold start costs about
    four minutes; switching boards from an open leaderboard costs seconds.

    Failure of one board does not stop the sweep. A board that cannot be reached
    -- most likely because no personal Rivals time has been posted for that exact
    track and class -- is recorded as skipped and the run moves on, because the
    alternative is a sweep that dies overnight on its first gap.

    Progress is written to a manifest after every board, so -Resume continues an
    interrupted sweep without rescanning what is already complete.

.EXAMPLE
    # Every verified track in the catalogue, classes A and R
    .\scripts\run_board_sweep.ps1 -Classes A,R

.EXAMPLE
    # Dry run: print the board list and the estimated cost, touch nothing
    .\scripts\run_board_sweep.ps1 -WhatIfOnly
#>
[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $Catalogue = "config/fh6_board_catalogue.json",
    [string] $RivalsMode = "Road Racing",
    [string[]] $Tracks,
    [string[]] $Classes,
    # "UntilEnd" pages to the bottom of each board. A number caps each board,
    # which is the sane way to sample a wide sweep before committing days to it.
    [string] $TotalRanks = "UntilEnd",
    [string] $OutputRoot = "data/memory_scans/sweep",
    [switch] $Resume,
    [switch] $WhatIfOnly
)

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path

function Write-Sweep {
    param([Parameter(Mandatory = $true)][string] $Message)
    Write-Host "[board-sweep] $((Get-Date).ToString('HH:mm:ss')) $Message"
}

$cataloguePath = if ([IO.Path]::IsPathRooted($Catalogue)) {
    $Catalogue
} else {
    Join-Path $workspace $Catalogue
}
if (-not (Test-Path -LiteralPath $cataloguePath)) {
    throw "Board catalogue not found at $cataloguePath."
}
# NOT $catalogue: that is the [string] parameter holding the path, and
# PowerShell variable names are case-insensitive, so assigning the parsed object
# to it silently coerces the object back into a string via its type constraint.
# .tracks then reads as null and the sweep claims the catalogue is empty.
$catalogueData = Get-Content -LiteralPath $cataloguePath -Raw | ConvertFrom-Json

# powershell.exe -File hands every argument over as a plain string, so
# "-Classes A,R" arrives as the single element "A,R" rather than two. Splitting
# here means the same call works whether the script is dot-invoked or launched
# through -File, instead of silently sweeping a board named "A,R".
function Expand-ListArgument {
    param([string[]] $Values)
    if (-not $Values) { return @() }
    return @($Values |
        ForEach-Object { $_ -split ',' } |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ })
}
$Tracks = Expand-ListArgument -Values $Tracks
$Classes = Expand-ListArgument -Values $Classes

# Only verified entries by default. An unconfirmed track name sends the
# navigator seeking a marker that never appears, which costs a full step timeout
# per board and produces nothing.
if (-not $Tracks) {
    # Where-Object rather than dotted access or the PSObject indexer: the mode
    # names contain spaces ("Road Racing"), which dotted access resolves to
    # nothing, and the indexer misbehaved under powershell.exe -File.
    $modeEntry = $catalogueData.tracks.PSObject.Properties |
        Where-Object { $_.Name -eq $RivalsMode } | Select-Object -First 1
    if (-not $modeEntry) {
        $known = ($catalogueData.tracks.PSObject.Properties | ForEach-Object { $_.Name }) -join ", "
        throw "The catalogue has no tracks for rivals mode '$RivalsMode'. It knows: $known"
    }
    $Tracks = @($modeEntry.Value.verified | ForEach-Object { $_.name })
}
if (-not $Classes) {
    $Classes = @($catalogueData.performance_classes.verified)
}

$boards = foreach ($track in $Tracks) {
    foreach ($class in $Classes) {
        [pscustomobject]@{
            Mode  = $RivalsMode
            Track = $track
            Class = $class
            Key   = "{0}|{1}|{2}" -f $RivalsMode, $track, $class
        }
    }
}
$boards = @($boards)

$outputRootPath = if ([IO.Path]::IsPathRooted($OutputRoot)) {
    $OutputRoot
} else {
    Join-Path $workspace $OutputRoot
}
New-Item -ItemType Directory -Force -Path $outputRootPath | Out-Null
$manifestPath = Join-Path $outputRootPath "sweep_manifest.json"

$done = @{}
$carried = @()
if ($Resume -and (Test-Path -LiteralPath $manifestPath)) {
    $previous = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $carried = @($previous.boards)
    foreach ($entry in $carried) {
        if ($entry.status -eq "complete") { $done[$entry.key] = $true }
    }
    Write-Sweep "resuming; $($done.Count) board(s) already complete"
}

$pending = @($boards | Where-Object { -not $done.ContainsKey($_.Key) })
Write-Sweep "$($boards.Count) board(s) in scope, $($pending.Count) to scan"
foreach ($board in $pending) {
    Write-Sweep "  - $($board.Track) / $($board.Class)"
}

if ($WhatIfOnly) {
    Write-Sweep "-WhatIfOnly: nothing was run"
    Write-Sweep "cost depends entirely on board depth; measure one board first with -TotalRanks 500"
    return
}

$results = New-Object System.Collections.ArrayList
# Carry the previous run's entries forward. Save-Manifest rewrites the file in
# full, so starting from an empty list would erase every board completed before
# this run -- and the NEXT -Resume would then rescan all of them. On a sweep
# measured in days that turns one interruption into a restart from zero.
#
# Boards being retried this run are dropped from the carried set, otherwise a
# board that was "unreachable" last time and is attempted again would appear
# twice, with the stale verdict alongside the fresh one.
$retrying = @{}
foreach ($board in $pending) { $retrying[$board.Key] = $true }
foreach ($entry in $carried) {
    if (-not $retrying.ContainsKey($entry.key)) { [void]$results.Add($entry) }
}
$boardIndex = 0

function Save-Manifest {
    param($Results)
    $payload = [pscustomobject]@{
        updated_at = (Get-Date).ToUniversalTime().ToString("o")
        rivals_mode = $RivalsMode
        total_ranks = $TotalRanks
        boards = @($Results)
    }
    $payload | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
}

foreach ($board in $pending) {
    $boardIndex += 1
    Write-Sweep "board $boardIndex/$($pending.Count): $($board.Track) / $($board.Class)"

    $forzaUp = $false
    try {
        $credential = [pscredential]::new(".\admin", [Security.SecureString]::new())
        $probe = New-PSSession -VMName $VMName -Credential $credential -ErrorAction Stop
        $forzaUp = Invoke-Command -Session $probe -ScriptBlock {
            [bool](Get-Process -Name forzahorizon6 -ErrorAction SilentlyContinue)
        }
        Remove-PSSession $probe
    } catch {
        $forzaUp = $false
    }

    $navigationArguments = @(
        "-NoProfile", "-ExecutionPolicy", "Bypass",
        "-File", (Join-Path $PSScriptRoot "start_forza_navigation.ps1"),
        "-VMName", $VMName,
        "-Track", $board.Track,
        "-PerformanceClass", $board.Class,
        "-RivalsMode", $board.Mode
    )
    # Cold start only when there is nothing to switch from. Every later board
    # reuses the running game, which is the difference between seconds and
    # roughly four minutes of menu walking per board.
    if ($forzaUp) {
        $navigationArguments += "-FromLeaderboard"
        Write-Sweep "switching from the open board"
    } else {
        $navigationArguments += "-FreshStart"
        Write-Sweep "no game running; cold starting"
    }

    $status = "complete"
    $note = ""
    $runId = "sweep_{0}_{1}_{2}" -f `
        ($board.Track -replace '[^A-Za-z0-9]', ''), $board.Class, (Get-Date -Format "yyyyMMdd_HHmmss")

    & powershell.exe @navigationArguments | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) {
        # Almost always "no personal time posted for this track and class", which
        # is a property of the account rather than a bug, so it is recorded and
        # skipped rather than thrown.
        $status = "unreachable"
        $note = "navigation failed with exit code $LASTEXITCODE"
        Write-Sweep "board unreachable; skipping"
    } else {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass `
            -File (Join-Path $PSScriptRoot "start_vm_memory_leaderboard_scan.ps1") `
            -VMName $VMName `
            -Track $board.Track `
            -PerformanceClass $board.Class `
            -RivalsMode $board.Mode `
            -TotalRanks $TotalRanks `
            -RunId $runId `
            -HostOutputRoot $OutputRoot `
            -SkipNavigation -KeepForzaRunning | ForEach-Object { Write-Host $_ }
        if ($LASTEXITCODE -ne 0) {
            $status = "scan_failed"
            $note = "memory scan failed with exit code $LASTEXITCODE"
            Write-Sweep "scan failed; continuing with the next board"
        } else {
            # Each board loads its own car set, so refreshing the codename
            # catalogue here accumulates coverage across the sweep: by the end,
            # config/fh6_car_catalogue.json spans every car that appeared on any
            # scanned board, and the exports are named. Non-fatal on failure.
            try {
                & powershell.exe -NoProfile -ExecutionPolicy Bypass `
                    -File (Join-Path $PSScriptRoot "update_car_catalogue.ps1") `
                    -VMName $VMName | ForEach-Object { Write-Host $_ }
            } catch {
                Write-Sweep "car-catalogue refresh failed (non-fatal): $($_.Exception.Message)"
            }
        }
    }

    [void]$results.Add([pscustomobject]@{
        key = $board.Key
        mode = $board.Mode
        track = $board.Track
        performance_class = $board.Class
        run_id = $runId
        status = $status
        note = $note
        finished_at = (Get-Date).ToUniversalTime().ToString("o")
    })
    Save-Manifest -Results $results
}

$complete = @($results | Where-Object { $_.status -eq "complete" }).Count
$unreachable = @($results | Where-Object { $_.status -eq "unreachable" }).Count
$failed = @($results | Where-Object { $_.status -eq "scan_failed" }).Count
Write-Sweep "sweep finished: $complete complete, $unreachable unreachable, $failed failed"
Write-Sweep "manifest: $manifestPath"
if ($failed -gt 0) { exit 3 }
exit 0
