# VM Rank Scanning

Run all commands from the host:

```powershell
cd <repo>
```

## 1. Start The VM And Forza

Clean start with the known-good VM settings:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start_forza_vm_ready.ps1
```

This sets/validates:

- VM `ForzaScrapeVM`
- Hyper-V console transport `VMBus`
- VM console video `1920x1080`
- GPU-P adapter present
- scripts/config copied to `C:\ForzaAutomation` in the VM
- Steam scheduled task registered
- Forza started in the VM

Fast path without rebooting the VM:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start_forza_vm_ready.ps1 -NoCleanDisplaySession
```

Use the fast path only when the VM is already healthy and Forza is behaving.

## 2. Required In-Game State

Before using `-AlreadyAtLeaderboard`, the VM must be sitting on the visible `Change Rival` leaderboard for the exact route/class you want to scan.

Checklist:

- You are on the `Change Rival` screen.
- Rows with `Driver`, `Car`, `Class`, `Drivetrain`, `Time`, `ABS`, `TCS`, `STM`, `GEAR` are visible.
- The selected row is rank 1 if you start with `-StartRank 1 -CurrentRank 1`.
- You have posted a personal Rivals time for this track/performance class. If not, drive/post a time first.

## 3. Start A New Scan From The Visible Leaderboard

Default automatic scan:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start_vm_rank_scan.ps1 `
  -AlreadyAtLeaderboard `
  -Track "Soni Circuit" `
  -PerformanceClass D `
  -RivalsMode "Road Racing"
```

The default `-TotalRanks Auto` reads the footer text such as `94,031 Players`. If that is not readable, it automatically falls back to scanning until a full 11-row scroll produces the same visible leaderboard again.

Speed behavior:

- The 11 DOWN inputs between screenshots are sent as one burst followed by a 120 ms UI settle.
- Resume jumps send individually paced DOWN/UP inputs, OCR the actually selected rank, recalculate the remaining distance, and repeat. Capture does not start until the exact target rank is verified. Unreadable OCR immediately after a long scroll is retried on settled screenshots before the run is failed.
- The final 500 ranks of a navigation jump use paced exact inputs. Shortened OCR values such as `203` while the table is near rank `2,000` are rejected unless the movement is genuinely near a digit boundary such as `1,000`.
- A navigation target must be confirmed by two settled OCR snapshots before capture begins. After capture, the verified end rank may not exceed the maximum rank reachable from the planned start and number of DOWN inputs; otherwise the chunk is rejected before it can be synchronized as complete.
- Each screenshot chunk leaves the final row selected, OCR-verifies its real rank, and starts the next chunk at the following rank. Inputs discarded while Forza loads another server page therefore create overlap and lower speed, not missing rank ranges.
- The host samples progress every two seconds and derives live speed from newly created screenshots.

Small smoke test:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start_vm_rank_scan.ps1 `
  -AlreadyAtLeaderboard `
  -Track "Soni Circuit" `
  -PerformanceClass D `
  -RivalsMode "Road Racing" `
  -TotalRanks 550
```

Specific scan size:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start_vm_rank_scan.ps1 `
  -AlreadyAtLeaderboard `
  -Track "Soni Circuit" `
  -PerformanceClass D `
  -RivalsMode "Road Racing" `
  -TotalRanks 22424
```

The current `Change Rival` screen does not always show a total player count. Use `-TotalRanks UntilEnd` when you want the scanner to discover the end by scrolling, or pass a numeric `-TotalRanks` when you know the exact total.

The host terminal follows progress by default:

```text
[vm-rank-scan] rank 35112/94031 (37.3%) | 2,860 ranks/min | ETA 20m 36s | screenshots guest/synced 3192/3150 | processed chunks 5 | task Running/267009
```

During a resume jump, the host instead shows:

```text
[vm-rank-scan] navigating verified rank 312 -> 35010 | 44 ranks/s | capture not started | task Running/267009
```

If the game misses inputs or a screenshot is captured before the rows settle, use more conservative timings:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start_vm_rank_scan.ps1 `
  -AlreadyAtLeaderboard `
  -Track "Soni Circuit" `
  -PerformanceClass R `
  -RivalsMode "Road Racing" `
  -RowSettleMs 180 `
  -NavigationCheckSeconds 5 `
  -NavigationSpamBurstSize 8 `
  -NavigationSpamPauseMs 12
```

The experimental scrollbar jump is disabled by default. Enable it only for testing with `-UseScrollbarJump`.

When the host watcher is attached, `Ctrl+C` now stops the VM scheduled task and the host sync/extractor helpers. With `-Detach`, stop the scheduled task explicitly before changing the in-game leaderboard.

Detach and let it run in the background:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start_vm_rank_scan.ps1 `
  -AlreadyAtLeaderboard `
  -Track "Soni Circuit" `
  -PerformanceClass D `
  -RivalsMode "Road Racing" `
  -Detach
```

## 4. Continue After An Interrupted/Short Run

If an old smoke-test run stopped at rank 550 and Forza is still on the leaderboard around the next page, continue into the same dataset by reusing the same `-RunId`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start_vm_rank_scan.ps1 `
  -AlreadyAtLeaderboard `
  -Track "Soni Circuit" `
  -PerformanceClass D `
  -RivalsMode "Road Racing" `
  -TotalRanks Auto `
  -StartRank 551 `
  -CurrentRank 561 `
  -RunId "20260616_135634_259_soni_circuit_d_89edb43a"
```

Use `-CurrentRank` for the currently selected rank in the VM. The old 550-rank run ended after capturing the page through rank 550 and normally leaves the selection around rank 561.

## 5. Output Locations

Each scan prints a `run_id`. Outputs are under:

```text
<repo>\data\vm_share\rank_scans\<RunId>\chunks
<repo>\data\processed\vm_rank_scans\<RunId>\combined\leaderboard_entries.csv
<repo>\data\processed\vm_rank_scans\<RunId>\leaderboard_entries.parquet
<repo>\data\processed\vm_rank_scans\<RunId>\combined\rank_coverage_report.json
<repo>\data\background\vm_rank_scan\<RunId>\
```

## 6. Watch Existing Background Jobs

Find active watcher/extractor processes:

```powershell
Get-CimInstance Win32_Process |
  Where-Object {
    $_.Name -ieq "powershell.exe" -and
    $_.CommandLine -match "sync_vm_capture_via_psdirect|run_host_extract_from_vm_captures|wait_and_extract"
  } |
  Select-Object ProcessId, CommandLine
```

Tail logs:

```powershell
Get-Content "<repo>\data\background\vm_rank_scan\<RunId>\host_sync.out.log" -Tail 80 -Wait
```

```powershell
Get-Content "<repo>\data\background\vm_rank_scan\<RunId>\host_extract.out.log" -Tail 80 -Wait
```

## 7. Merge Existing Processed Chunks

If screenshots/extraction already exist and you only need to regenerate combined CSV/parquet:

```powershell
python .\scripts\merge_rank_scan_outputs.py `
  --input-root "<repo>\data\processed\vm_rank_scans\<RunId>\chunks" `
  --output-dir "<repo>\data\processed\vm_rank_scans\<RunId>\combined" `
  --parquet-path "<repo>\data\processed\vm_rank_scans\<RunId>\leaderboard_entries.parquet" `
  --total-ranks 22424 `
  --max-scanned-rank 22424
```
