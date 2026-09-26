# Troubleshooting

## The Whole Host Freezes And Has To Be Power-Cycled

Symptom: the host desktop stops responding entirely while the game runs in the
VM. No bugcheck, no minidump, no `nvlddmkm` TDR entry -- the only trace is a
Kernel-Power event 41 on the next boot, with no bugcheck code, because the event
log never got flushed. Only a power cut recovers it.

Cause: the guest's copy of the NVIDIA driver no longer matches the host's. GPU
paravirtualisation requires them to be identical. The host updates its driver in
the background, the guest's staged copy in `HostDriverStore` does not change, and
from then on the partitioned adapter comes up as **Problem Code 43**. The game
still launches -- it falls back to software rendering -- but driving a broken
partition can wedge the host's display driver, and when it does, the host is
gone.

This happened on 2026-08-18: the host took 32.0.16.1088 at 02:38 while
`vmwp.exe` still held the GPU, which is itself visible as Kernel-PnP event 225.
The guest stayed on 32.0.16.1047 from 2026-05-19, and the host froze during the
next cold start of the game.

Check it in one command, from a normal (unelevated) shell:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\sync_vm_gpu_pv_drivers.ps1 -WhatIfOnly
```

It prints the host driver version, the guest adapter's status and problem code,
and how many files differ. `status=Error problem=43` means do not launch the game.
Drop `-WhatIfOnly` to fix it: the matching package is copied in over PowerShell
Direct and the guest is restarted, after which it re-checks and reports whether
the adapter came back `status=OK problem=0`.

Two things worth knowing about the alternative,
`scripts/install_vm_gpu_pv_host_drivers.ps1`:

- It mounts the VHD offline, which needs `SeManageVolumePrivilege`. Membership of
  Hyper-V Administrators is enough to start, stop and reconfigure the VM but
  **not** to mount a VHD, so it fails with `0x80070522` from a normal shell.
- It requires the VM to be off. `sync_vm_gpu_pv_drivers.ps1` works against the
  running guest and needs no elevation and no SMB credentials.

Re-check this after **every** host GPU driver update. Nothing else warns you.

### The partition may also be over-allocated

Separately, check that the guest is not entitled to the entire GPU while the host
is driving its own desktop with it:

```powershell
Get-VMGpuPartitionAdapter -VMName ForzaScrapeVM | Select-Object Max*,Optimal*
```

`1000000000` is 100%. This VM now runs at `500000000` (50%) for VRAM, compute,
encode and decode, which leaves the host compositor guaranteed headroom and costs
nothing that matters for menu navigation or memory scanning. Changing it requires
the VM to be off.

## Script Starts But Nothing Scrolls

Check the guest task result:

```powershell
$cred = [pscredential]::new(".\admin", [Security.SecureString]::new())
Invoke-Command -VMName ForzaScrapeVM -Credential $cred -ScriptBlock {
  Get-ScheduledTask | Where-Object TaskName -like "ForzaRankScan*" | ForEach-Object {
    $info = Get-ScheduledTaskInfo -TaskName $_.TaskName
    [pscustomobject]@{
      TaskName = $_.TaskName
      State = $_.State
      LastRunTime = $info.LastRunTime
      LastTaskResult = $info.LastTaskResult
    }
  }
}
```

`LastTaskResult = 1` means the VM runner failed. Read the transcript:

```powershell
$cred = [pscredential]::new(".\admin", [Security.SecureString]::new())
Invoke-Command -VMName ForzaScrapeVM -Credential $cred -ScriptBlock {
  Get-ChildItem C:\ForzaCaptures\rank_scans -Recurse -File -Filter "*.transcript.log" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1 |
    Get-Content -Tail 120
}
```

## Observed Menu Graph On 6.420.696.0

Recorded by `scripts/forza_navigator.ps1` frame captures on 2026-08-18. Each
screen is listed with text that appears on **only** that screen, which is what the
navigator matches on. Using non-unique text is what caused several failed runs:
`Festival Playlist|Collection Journal|Campaign` matches the garage menu, the world
pause menu's CAMPAIGN tab, and the map footer all at once.

| Screen | Unique marker | Action that advances it |
| --- | --- | --- |
| Title | `Start Game`, `Accessibility/Settings` | ENTER, repeatedly, until the continue menu appears |
| Continue menu | `Continue` on its own line | ENTER, then wait out a long world load |
| Garage menu | `CUSTOMISABLE GARAGE`, `Forzavista` | LEFT to the CAMPAIGN section, ENTER to pick Drive |
| World pause menu | `CREATIVE HUB`, `Reset Car Position`, `Exit Game` | RIGHT until the ONLINE tab's content shows |
| Map | `Close Map`, `Toggle Map Regions`, `Set Route` | ESC |
| ONLINE tab | `Top the Leaderboards`, `Horizon Open`, `Horizon Tour` | RIGHT, DOWN, ENTER opens the Rivals hub |
| Rivals hub | `My Rivals`, `Showcase Rivals`, `Monthly Rivals` | ENTER on the leftmost tile |
| Confirmation modal | `Are you sure you want to`, `Return to Horizon Solo?` | LEFT then ENTER to answer **No** |
| Forza LINK | `Forza LINK Selection`, `The Eliminator` | ESC |

Notes that cost several runs to learn:

- The **garage menu has no Online tab at all**. Its carousel is
  `CAMPAIGN | BUY & SELL | CARS | CUSTOMISABLE GARAGE | CHARACTER` and it wraps,
  so seeking RIGHT for a Rivals marker can never succeed and instead walks into
  the CHARACTER submenu. Rivals exists only on the world pause menu.
- The world pause menu's tab bar reads `CREATIVE HUB` **and** `ONLINE` regardless
  of which tab is active, so matching the word `Online` "finds" the tab while
  CAMPAIGN is still selected. Pressing ENTER there picks CAMPAIGN's first item,
  `World Map`, and lands on the map. Match the ONLINE tab's *content*
  (`Top the Leaderboards`) instead.
- The Rivals hub is a tile row of `Horizon Rivals / Monthly Rivals / My Rivals /
  Showcase Rivals`. It is **not** the `Road Racing / Street Racing / ...` category
  list the pre-patch route expected.
- **ESC does not dismiss confirmation modals.** They have `No`/`Yes` buttons and a
  `Select / ENTER` prompt, so ESC leaves the dialog on screen indefinitely. Answer
  `No`: confirming `Return to Horizon Solo?` switches the online session.
- Which tile is highlighted cannot be read by OCR, because highlight is not text.
  The navigator tries the known-good key plan first and falls back through other
  plausible positions, backing out between attempts.

Start from a cold launch whenever possible; it is the only entry state with no
inherited modal, online session, or leftover submenu:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start_forza_navigation.ps1 `
  -FreshStart -Track "Soni Circuit" -PerformanceClass R
```

## Navigation Cannot Find The Rivals Entry

Symptom, on build 6.420.696.0:

```text
seek RIGHT did NOT find 'Top the Leaderboards|Rivals' in 10 presses
Could not find the Rivals entry from the hub.
```

Cause: the player is inside a garage. The garage menu is a carousel of

```text
CAMPAIGN | BUY & SELL | CARS | CUSTOMISABLE GARAGE | CHARACTER
```

which wraps around, and it has no Online tab at all. Rivals only exists on the
world pause menu, so pressing RIGHT looking for it just walks into the CHARACTER
submenu. Section item lists observed:

- CAMPAIGN: Drive, Collection Journal, Festival Playlist, Settings
- BUY & SELL: Autoshow, Auction House, Car Pass, Car Packs, Voucher Cars
- CARS: My Cars, Upgrades & Tuning, Designs & Paints, Car Horns, Number Plates,
  Barn Finds, Treasure Cars
- CUSTOMISABLE GARAGE: Customise Garage, View Garage, Browse & Manage,
  Display Cars, Visitor Permission
- CHARACTER: Customise Character, My Name, Forza LINK, My Stats

Fix: leave the garage first via CAMPAIGN > Drive, then open the world pause menu.
`scripts/forza_navigator.ps1` does this, and confirms the transition by waiting for
the hub markers to disappear rather than assuming the keypress landed.

## Blind Tile Selection Lands In The Wrong Submenu

Symptom: the navigator reaches the ONLINE tab, presses its way toward Rivals, and
ends up somewhere else entirely -- repeatedly, with the same wrong destination:

```text
state: online_tab
opening Rivals tile via [RIGHT,DOWN] then ENTER
UNKNOWN screen: Online Player List / RECENT PLAYERS / Driver / ... / FRIENDS
FAILED Stuck in state 'unknown' after 15 cycles.
```

Cause: the tile was chosen by counting arrow presses. `[RIGHT,DOWN]` was correct
on the previous build; after the patch reordered the tile row the same count
lands on the Social panel instead. Which tile is highlighted cannot be read out
of the OCR text, so the navigator could not tell it had missed, and its fallback
plans -- `[]`, `[DOWN]`, `[RIGHT]` -- are equally blind guesses.

Fix: hover the tile's own label. Forza's menus are mouse-aware, and a cursor
resting on an item pins the highlight there. Everywhere else in the navigator
that is a hazard, which is why the cursor is parked in a dead corner; for
choosing a named tile it is the asset. `Get-ScreenFrame` keeps each OCR line's
bounding rectangle, `Find-TextRect` looks up where a label is, and
`Select-ByHover` moves the cursor onto it, confirms with ENTER, then re-parks the
cursor so the next keyboard step is not fighting a pinned highlight.

The ONLINE tab labels its tiles `Rivals` over `Top the Leaderboards`, both of
which OCR cleanly, so nothing about the tile's index or the row's order has to be
known. The blind plans remain as a fallback for a build that draws a label as an
image.

The general rule: prefer naming a target over counting steps to it. A press count
encodes the menu layout, and a patch invalidates it silently.

## Navigation Sends Keys But Nothing Happens

Two separate causes, both of which look identical from the host:

1. **Inputs too far apart.** Driving the game with one key per host-side
   scheduled task puts 10-30 s between inputs. The game auto-hides its HUD, so
   OCR frames contain only the car and every state check fails, and menu context
   drifts so replayed keys land in the wrong place. Use
   `scripts/start_forza_navigation.ps1`, which runs the whole loop in one guest
   process at roughly 250 ms per cycle.
2. **Focus not actually held.** `Get-Process().MainWindowHandle` is `0` when
   queried over PowerShell Direct, because that runs in session 0. The game also
   owns a `ForzaFullscreenShadeWindow` that must not be focused; only its `App`
   class window counts. `SendKeys` returns success even when nothing received the
   input, so focus has to be confirmed against `GetForegroundWindow`.

To inspect input delivery directly:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\diagnose_vm_forza_input.ps1 -Key ENTER
```

It reports the session id, every top-level game window, whether
`SetForegroundWindow` actually took, and the foreground window before and after
the key.

## Paging Stops After The First 50 Rows

Symptom: the first block decodes perfectly, then every following iteration
reports no rows and the scan gives up at `no_progress`:

```text
rows=50 max=50 speed=141.7/min ETA=3min
(chunks 1..7 all expect rank 51 and return rows=0)
```

It looks like the leaderboard is not scrolling. It is. Capture the screen
mid-run and the table is visibly down at rank 70-plus, and an XUID sweep finds a
clean 50-row block at stride `0x2d0` covering ranks 51-100 sitting in memory.

Cause: `capture_scoreboard_memory.py` anchors on the exact byte pattern
`(expected_rank, expected_rank)`. Scrolling evicts rows from the top of the
buffer, so by the time rank 51 is asked for, the live block can start at 52 or
60 -- and the anchor then matches nothing at all, even though the rows are right
there and decodable. The scanner reported "no rows" for a buffer it could see.

Reproduce it directly against a scrolled board:

```text
--expected-rank 51 -> 0 rows
--expected-rank 60 -> 41 rows, ranks 60-100
```

Fix: the scanner now falls back to an unanchored sweep when the exact anchor
misses, and picks the block that spans the wanted rank, or failing that the one
starting closest after it, so the gap stays as small as the game's own eviction
allows. `--expected-rank 51` returns ranks 52-100 instead of nothing.

The general rule: **do not anchor on a value the game is free to evict.** Ask for
what is there at or beyond the wanted position and let the caller advance.

## No Personal Time On Track/Class

Symptom:

- The expected `Change Rival` leaderboard is missing.
- OCR cannot detect leaderboard rows or total players.
- The scan never reaches the scrolling phase.

Fix:

1. In Forza, drive/post a Rivals time for that exact track and performance class.
2. Return to the visible `Change Rival` leaderboard.
3. Start the scan again with `-AlreadyAtLeaderboard`.

## Could Not Detect Total Players

The normal VM starter uses `-TotalRanks Auto`. It tries to read a footer like `20,381 Players`; if OCR cannot read it, the scanner keeps going until an 11-row scroll repeats the same screen:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start_vm_rank_scan.ps1 `
  -AlreadyAtLeaderboard `
  -Track "Soni Circuit" `
  -PerformanceClass D `
  -RivalsMode "Road Racing"
```

For a quick smoke test, pass `-TotalRanks 550`. If you know the exact total, pass that number.

## Scroll Is Too Fast Or Misses Ranks

The fast defaults send 11 DOWN inputs as one burst and wait 120 ms before the next screenshot. Increase only the settle time first:

```powershell
-RowSettleMs 180
```

For inaccurate large resume jumps, use shorter verification windows or smaller spam bursts:

```powershell
-NavigationCheckSeconds 5 -NavigationSpamBurstSize 8 -NavigationSpamPauseMs 12
```

Capture screenshots must not begin until the host reports that the exact target rank was verified. The scrollbar jump is disabled unless `-UseScrollbarJump` is explicitly supplied.

## Ctrl+C Did Not Stop The VM

Older versions stopped only the host progress display. The current host starter stops the VM scheduled task and host sync/extractor helpers when an attached watcher is interrupted with `Ctrl+C`.

For an older still-running task:

```powershell
$cred = [pscredential]::new(".\admin", [Security.SecureString]::new())
Invoke-Command -VMName ForzaScrapeVM -Credential $cred -ScriptBlock {
  Get-ScheduledTask -TaskName "ForzaRankScan_*" |
    Where-Object State -eq Running |
    Stop-ScheduledTask
}
```

## ETA Is Unknown

An ETA requires a known total rank count. Use `-TotalRanks Auto` or pass the exact number:

```powershell
-TotalRanks 94031
```

When the footer cannot be read and the scanner is running in until-end mode, it still reports live ranks/min but shows `ETA until end detection`.

## Could Not Find/Focus Target Game Window

Meaning:

- Forza is not running in the VM, or
- the VM is not on the interactive console desktop, or
- the game closed/crashed, or
- `-AlreadyAtLeaderboard` was used before opening Forza.

Fix:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start_forza_vm_ready.ps1
```

Then navigate in the VM to the desired `Change Rival` leaderboard and rerun the scan.

## Host Extractor Started Without StatePath

This was caused by fragile PowerShell line continuation in the generated extractor wrapper. `scripts\start_vm_rank_scan.ps1` now writes an argument-array based wrapper. Re-run the scan starter after pulling the current script into the VM.

## Cannot Overwrite Variable Host

Symptom:

```text
Watch-ScanProgress : Cannot overwrite variable Host because it is read-only or constant.
```

This was a host progress-display bug in `scripts\start_vm_rank_scan.ps1`. The scan in the VM can still complete, but the host follower exits. Update the script and rerun the host extraction for the printed `run_id` if needed.

## Extractor Reads `.syncing` Chunk

Symptom:

```text
Could not read image: ...\chunks\.chunk_000001_050.syncing\leaderboard_rank_...
```

The host extractor raced the PSDirect sync process and picked up a temporary copy folder. `scripts\run_host_extract_from_vm_captures.ps1` now ignores dot-prefixed and `.syncing` directories. Re-run host extraction for the finished run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run_host_extract_from_vm_captures.ps1 `
  -StatePath "<repo>\data\vm_share\rank_scans\<RunId>\state.json"
```

## Stop Stale Watchers

```powershell
Get-CimInstance Win32_Process |
  Where-Object {
    $_.Name -ieq "powershell.exe" -and
    $_.CommandLine -match "sync_vm_capture_via_psdirect|run_host_extract_from_vm_captures|wait_and_extract"
  } |
  ForEach-Object {
    Stop-Process -Id $_.ProcessId -Force
  }
```

## Where Are Results?

Use the printed `run_id`:

```text
data\vm_share\rank_scans\<RunId>\chunks
data\processed\vm_rank_scans\<RunId>\leaderboard_entries.parquet
data\processed\vm_rank_scans\<RunId>\combined\rank_coverage_report.json
data\background\vm_rank_scan\<RunId>\
```
