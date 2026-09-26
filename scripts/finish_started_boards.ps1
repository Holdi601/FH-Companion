<#
.SYNOPSIS
    Finish the boards the sweep started, in the order that returns the most data.

.DESCRIPTION
    Which route to use was settled by measurement on 2026-08-23, not by preference:

        memory scanner   600 ranks/min median, 100% complete, up to 50,850 rows --
                         but on Shimanoyama and Hokubu it reads one pool of 50 rows
                         and then stops advancing. Thirteen boards from 2026-08-21
                         sit at 149 or 99 rows for that reason, and a probe on
                         Shimanoyama S2 reproduced it: the game was on the right
                         board with the cursor on row 7, going nowhere.
        OCR loop         400-618 ranks/min, 90-96% complete -- and it reads exactly
                         those boards, 4,700 to 10,100 ranks each.

    So the screen route runs here. It is the slower one where both work, but it is
    the only one that works on what is left.

    Order is by what is actually missing, biggest first, rather than by route number:

      1. Soni (idx 5) -- its two scans are dead 50- and 99-row runs, so the real
         board lengths are still unknown.
      2. Narai-Juku (idx 1), S1/S2/R only -- never scanned at all. A-D are already
         complete from memory at 4,953-50,850 rows, and re-reading them would cost
         68,000 ranks of scrolling for nothing.
      3. Routes 6-22 -- never visited.
      4. Shimanoyama and Hokubu top-ups, last: they are already at 90-96%, and the
         gap is ranks the game never rendered. Worth one more pass only because the
         straggler cut used to delete the deep tail past rank ~10,120 and no longer
         does, so a pass now keeps what the earlier ones threw away.

.EXAMPLE
    .\scripts\finish_started_boards.ps1
#>
[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $LogRoot = "data/runtime/finish_boards",
    # The operator games on this machine, so stop starting boards at this hour.
    [int] $UntilHour = 23,
    [int] $ChunkSeconds = 120,
    [int] $MaxChunks = 30,
    # Restarting after an edit or an interruption should not repeat the stages that
    # already ran. 1 is the first stage; the log names each stage as it starts.
    [int] $FromStage = 1,
    # 100 s of scrolling shows the first ~1,900 ranks, which carry only 38-76% of a
    # board's distinct cars (measured over four Shimanoyama boards). The cars still
    # unnamed are the ones that never showed up, so they sit deeper -- hence longer.
    [int] $NameSeconds = 180,
    [string] $VMUser = ".\admin"
)

$ErrorActionPreference = "Continue"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$logRoot = [IO.Path]::GetFullPath((Join-Path $workspace $LogRoot))
New-Item -ItemType Directory -Force -Path $logRoot | Out-Null
$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$log = Join-Path $logRoot "finish_$stamp.log"

function Write-Step {
    param([string] $Message)
    $line = "[finish] $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss')) $Message"
    Write-Host $line
    Add-Content -LiteralPath $log -Value $line -Encoding UTF8
}

$stages = @(
    @{ kind = "sweep"; name = "Soni, every class"; routes = "5"; classes = "D,C,B,A,S1,S2,R" },
    # Naming, and it is worth doing before more boards: 630 memory car_ids exist and only
    # 334 have a name, so for the other 296 the analytics build cannot tell that memory's
    # id and the screen's name are one car. It lists both, and the car then competes with
    # itself in its own class -- inflating the field size the points rule counts.
    #
    # A name needs BOTH routes on one board: memory supplies the car_id, the screen the
    # name, and the lap time joins them (exact to the millisecond, which is accurate
    # enough -- 1,747 of 1,770 on Hokubu S1). The cars still unnamed sit on Shirakawa,
    # Highway and Narai-Juku, which memory read alone.
    #
    # The 180-second-per-class capture route was tried first and returned three names in
    # 45 minutes: its captures yielded ~1,000 lap times from 3,600 frames and, for two
    # classes, none that the memory board shared at all -- it had filmed the wrong board.
    # So this uses the sweep instead, whose navigation ran unattended all night, capped
    # at two chunks: ~4,600 ranks is far more overlap than a name join needs, and it adds
    # a real board to the data rather than frames that get deleted.
    # Which boards, and how deep, is computed rather than guessed. Each memory board
    # knows the rank at which every car_id it holds first appears, so the reachable
    # unnamed cars per board are countable -- and they overlap enormously:
    #
    #   Shirakawa A   178 reachable within 9,200 ranks   (+178)
    #   Shirakawa S1  125                                 (+28)
    #   Shirakawa B   126                                 (+20)
    #   Shirakawa S2   58                                 (+16)
    #   Highway A     161                                 (+12)
    #   Narai-Juku A  148                                  (+5)
    #   Narai-Juku C   49                                  (+4)
    #
    # Seven boards reach 263 of the 296 unnamed cars. Sweeping all seven classes of all
    # three tracks would take four times as long for the same names: Highway B adds none,
    # Shirakawa C adds none. Depth matters -- the cars still unnamed are the rare ones,
    # so only 21 of Shirakawa S1's 250 show up in the first 2,500 ranks.
    #
    # These ran capped at four chunks at first, which harvests names cheaply but leaves
    # the board unfinished: Shirakawa S1 stopped at 9,988 of its 33,800 ranks. A capped
    # board is not a finished board, so the cap is gone and each board now runs to where
    # it stops yielding new ranks. Names come out of the same pass either way.
    @{ kind = "sweep"; name = "Shirakawa A/S1/B/S2 for names"; routes = "2"; classes = "A,S1,B,S2" },
    @{ kind = "sweep"; name = "Highway A for names"; routes = "0"; classes = "A" },
    @{ kind = "sweep"; name = "Narai-Juku A/C for names"; routes = "1"; classes = "A,C" },
    @{ kind = "join"; name = "join screen names onto memory car_ids" },
    @{ kind = "sweep"; name = "Narai-Juku, the three never scanned"; routes = "1"; classes = "S1,S2,R" },
    @{ kind = "sweep"; name = "routes 6-22, never visited"; routes = (6..22 -join ","); classes = "D,C,B,A,S1,S2,R" },
    @{ kind = "sweep"; name = "Shimanoyama and Hokubu top-ups"; routes = "3,4"; classes = "A,S1,S2,B,C,D,R" }
)

# Killing this runner does not kill what it started inside the guest: the capture
# registers an interactive scheduled task that holds the DOWN key, and that task keeps
# pressing. On 2026-08-23 four of them had accumulated across restarts, and the one
# still pressing made every navigation fail with "could not find the anchor route" --
# the navigator was steering a list somebody else was scrolling. So clear them first;
# a run that starts clean cannot inherit that fight.
$credential = [pscredential]::new($VMUser, [Security.SecureString]::new())
try {
    $session = New-PSSession -VMName $VMName -Credential $credential -ErrorAction Stop
    $removed = Invoke-Command -Session $session -ScriptBlock {
        Get-ScheduledTask -ErrorAction SilentlyContinue |
            Where-Object { $_.TaskName -like "Chunk*" -or $_.TaskName -like "Cap*" -or
                           $_.TaskName -like "ForzaMemoryScan*" } |
            ForEach-Object {
                Stop-ScheduledTask -TaskName $_.TaskName -ErrorAction SilentlyContinue
                Unregister-ScheduledTask -TaskName $_.TaskName -Confirm:$false -ErrorAction SilentlyContinue
                $_.TaskName
            }
    }
    Remove-PSSession $session -ErrorAction SilentlyContinue
    if ($removed) { Write-Step "cleared $(@($removed).Count) stale guest task(s): $($removed -join ', ')" }
} catch {
    Write-Step "could not check the guest for stale tasks: $($_.Exception.Message)"
}

Write-Step "start; stage $FromStage of $($stages.Count), stopping at ${UntilHour}:00"

$stageNumber = 0
foreach ($stage in $stages) {
    $stageNumber += 1
    if ($stageNumber -lt $FromStage) {
        Write-Step "skipping stage ${stageNumber}: $($stage.name)"
        continue
    }
    if ((Get-Date).Hour -ge $UntilHour) {
        Write-Step "past ${UntilHour}:00 -- leaving the remaining stages"
        break
    }
    if ($stage.kind -eq "join") {
        Write-Step "stage ${stageNumber}: $($stage.name)"
        # Reads the OCR boards already on disk against the memory boards of the same
        # track and class -- no capture, so it costs nothing and can be re-run any time.
        & python (Join-Path $PSScriptRoot "join_ocr_names_to_car_ids.py") 2>&1 |
            ForEach-Object { Add-Content -LiteralPath $log -Value $_ -Encoding UTF8; Write-Host $_ }
        Write-Step "stage ended (exit $LASTEXITCODE): $($stage.name)"
        continue
    }
    Write-Step "stage ${stageNumber}: $($stage.name)  (routes $($stage.routes); classes $($stage.classes))"
    $chunks = if ($stage.chunks) { $stage.chunks } else { $MaxChunks }
    & python (Join-Path $PSScriptRoot "ocr_board_sweep.py") `
        --vm $VMName --route-indices $stage.routes --classes $stage.classes `
        --chunk-seconds $ChunkSeconds --max-chunks $chunks --until-hour $UntilHour 2>&1 |
        ForEach-Object { Add-Content -LiteralPath $log -Value $_ -Encoding UTF8; Write-Host $_ }
    Write-Step "stage ended (exit $LASTEXITCODE): $($stage.name)"
}

Write-Step "finished. Log: $log"
