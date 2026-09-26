# Direct Memory Leaderboard Scanning

This is the preferred workflow. It launches the VM and Forza, navigates to the
requested Rivals category, track, and performance class, reads structured
leaderboard rows from Forza memory, and exports CSV, JSON, and Parquet.

OCR is used only for menu-state recognition, track selection, and the optional
player-total estimate. Leaderboard row values are not read with OCR.

## Requirement

The Xbox account inside the VM must already have a posted Rivals time for the
requested track and performance class. Forza does not expose the `Change Rival`
leaderboard before that time exists.

## One-Command Run

Run this from an ordinary PowerShell terminal on the host:

```powershell
cd <repo>

powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start_vm_memory_leaderboard_scan.ps1 `
  -Track "Soni Circuit" `
  -PerformanceClass R `
  -RivalsMode "Road Racing" `
  -TotalRanks Auto
```

Supported classes are `D`, `C`, `B`, `A`, `S1`, `S2`, `R`, and `X`.

Supported Rivals categories are:

- `Road Racing`
- `Cross-Country`
- `Street Racing`
- `Touge`
- `Drag Racing`
- `Dirt Racing`

`-TotalRanks Auto` reads the leaderboard total once. If the total is not
visible, the scanner continues until newly loaded memory blocks stop advancing.

## What The Command Does

1. Verifies that Hyper-V and `ForzaScrapeVM` are in a usable state.
2. Starts and prepares the VM.
3. Copies the current automation scripts and configuration into the VM.
4. Closes an old Forza process and launches a deterministic fresh session.
5. Replays the learned route to the Road Racing anchor.
6. Selects the requested category, discovers the track by OCR, selects the
   performance class, and opens `Change Rival`.
7. Reads fixed-layout `ScoreboardRow` records from process memory.
8. Scrolls only far enough to make Forza fetch the next row block.
9. Prints rows collected, maximum rank, rows per minute, and ETA on the host.
10. Copies the chunks to the host and merges them into CSV, JSON, and Parquet.

## Output

Each run is written to:

```text
data\memory_scans\<run_id>\
```

The main files are:

```text
leaderboard_entries.parquet
combined\leaderboard_entries.csv
combined\leaderboard_entries.json
combined\merge_report.json
state.json
runner.log
```

The merge report includes the captured rank range and missing ranks.

## Verified Result

The workflow was verified end to end on June 24, 2026 with:

```text
Soni Circuit / Road Racing / R / ranks 1-200
```

The command launched the VM and Forza, found the track, verified class `R`,
captured four structured 50-row memory blocks, and wrote a readable Parquet
file with 200 unique ranks and no gaps.

A separate 300-rank test verified the two-buffer memory cache. The first two
blocks require full-memory discovery; later blocks alternate between two cached
heap locations and are substantially faster.

Current structured fields include rank, XUID, gamertag, lap time, submitted
time, class ID, drivetrain ID, assists, clean-lap status, and ghost status.
Vehicle-name and PI decoding from linked row objects is still in progress.

## Hyper-V Recovery

If the command reports that `vmms` is `StopPending` or the VM is `Stopping`,
restart the Windows host once. Do not repeatedly force-stop the GPU-partitioned
VM. After the restart, run the same one-command scan again.

## Current Direct-Data Boundary

The network request is `Forza.WebServices.Scoreboard.GetRows`, but its
`bin/xtsw` body is application-encrypted. The stable direct boundary currently
used is the post-decrypt `ScoreboardRow` array inside Forza memory. This keeps
the result structured while avoiding TLS interception, account-token replay,
and OCR of every table cell.
