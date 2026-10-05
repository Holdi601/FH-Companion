# Leaderboard extractor

(Moved here from the repository README on 2026-09-26.)

Automation for turning Forza Horizon 6 Rivals leaderboard screenshots or scroll recordings into structured data.

## Wiki

The current VM workflow is documented in:

- [index.md](index.md)
- [vm_rank_scanning.md](vm_rank_scanning.md)
- [troubleshooting.md](troubleshooting.md)
- [play_overlay.md](play_overlay.md) (the in-game overlay, in the app)

There are these entry points:

- `scripts/run_forza_pipeline.ps1`: one-command orchestration for status, observe, capture, display-mode handling, and extraction.
- `scripts/run_capture_automation.ps1`: starts/focuses the game, sends configured menu keys, captures screenshots, then runs OCR extraction.
- `scripts/extract_leaderboard.py`: extracts rows from already captured screenshots or video.

## Full Automation

The normal entrypoint is:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run_forza_pipeline.ps1 -Mode Status
```

Learn the route interactively:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run_forza_pipeline.ps1 -Mode Learn
```

`Learn` is a guided recorder. It launches/focuses FH6, tells you which hotkeys to use, records whitelisted keyboard/menu input plus mouse clicks, and saves OCR checkpoints while you manually navigate to the target Rivals leaderboard.

During learning:

- Use keyboard/mouse, not controller.
- Press `F8` after each important screen has settled, for example title screen, continue menu, garage, free drive, pause menu, Online tab, Rivals hub, Rivals mode selection, route selection, and rival selection.
- Press `F9` when the leaderboard page is visible and ready to page through. This finishes the recording and writes `config/fh6_learned_route.json`.
- Press `F10` to stop early. This saves only a partial route inside the learning session folder; it does not overwrite `config/fh6_learned_route.json`.

Each `F8`/`F9` checkpoint becomes an OCR state guard in the learned route. Replay waits for stable screen text before sending the next key, so loading screens should not cause macro spam. The learner intentionally ignores volatile text such as player names, car names, credits, FPS counters, PI values, and leaderboard row data when building navigation fingerprints.

When a learned pause-menu route contains a long run of tab navigation keys, the generator replaces that brittle key spam with OCR-guided navigation. For example, instead of replaying many fixed `RIGHT` presses before the Online tab, it presses `RIGHT` until Online-tab text such as `Top the Leaderboards` is actually visible, then sends `RIGHT` and `DOWN` to select the Rivals tile.

If replay waits forever, inspect the latest `wait_*.png` and `.ocr.txt` files under the capture folder to see which marker was not found. If the screenshots from a learning session are good but the generated guards need improving, regenerate the route without relearning:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/learn_forza_route.ps1 `
  -EventsPath data/learn/learn_YYYYMMDD_HHMMSS/events.json `
  -OutputConfig config/fh6_learned_route.json `
  -Track "REPLACE_WITH_TRACK_NAME" `
  -PerformanceClass S1 `
  -RivalsMode "Road Racing"
```

Then test the learned route:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run_forza_pipeline.ps1 `
  -Mode Capture `
  -RouteConfig config/fh6_learned_route.json `
  -Track "REPLACE_WITH_TRACK_NAME" `
  -PerformanceClass S1 `
  -RivalsMode "Road Racing" `
  -Pages 3 `
  -DryRun
```

If the game is already sitting on a visible Rivals leaderboard, test only multi-page capture and extraction:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run_forza_pipeline.ps1 `
  -Mode Capture `
  -RouteConfig config/fh6_current_leaderboard_pages.json `
  -Track "REPLACE_WITH_TRACK_NAME" `
  -PerformanceClass D `
  -RivalsMode "Road Racing" `
  -Pages 3 `
  -SkipLaunch `
  -NoDisplayManage `
  -ClearCaptureOutput
```

This writes `leaderboard_000.png`, `leaderboard_001.png`, and so on under `data/inbox/current_leaderboard`, then extracts them into CSV/JSON/SQLite/Parquet in `data/processed`.

Forza's leaderboard scrolls by rows, not by `PageDown`. The capture configs therefore take one screenshot, press `DOWN` 21 times for the first jump so the viewport moves from ranks 1-11 to 12-22, then press `DOWN` 11 times for each later jump. To capture the entire visible leaderboard set, use `-Pages All`; the runner reads the footer text such as `20,381 Players` and calculates the screenshot count from 11 rows per screenshot.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run_forza_pipeline.ps1 `
  -Mode Capture `
  -RouteConfig config/fh6_current_leaderboard_pages.json `
  -Track "REPLACE_WITH_TRACK_NAME" `
  -PerformanceClass D `
  -RivalsMode "Road Racing" `
  -Pages All `
  -SkipLaunch `
  -NoDisplayManage `
  -ClearCaptureOutput
```

Run the visual route and extract leaderboard rows:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run_forza_pipeline.ps1 `
  -Mode Capture `
  -Track "REPLACE_WITH_TRACK_NAME" `
  -PiClass S1
```

Run a batch of maps/modes/classes/pages:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run_forza_pipeline.ps1 `
  -Mode Batch `
  -BatchConfig config/fh6_batch.example.json
```

Batch jobs live in `config/fh6_batch.example.json`:

```json
{
  "defaults": {
    "event_type": "Rivals",
    "rivals_modes": ["Road Racing"],
    "performance_classes": ["All"],
    "pages": 20
  },
  "jobs": [
    {
      "track": "REPLACE_WITH_TRACK_NAME",
      "rivals_modes": ["Road Racing", "Street Racing"],
      "performance_classes": ["A", "S1", "R"],
      "pages": 10
    }
  ]
}
```

Here, `performance_classes` means Forza car performance classes: `D`, `C`, `B`, `A`, `S1`, `S2`, `R`, and `X`. Use `All` to expand to every performance class in that order. The older `pi_classes` key is still accepted as an alias.

The batch runner expands each job into every track/mode/performance-class combination. Each job gets its own capture/output folder, and all jobs can write to the shared SQLite database and Parquet dataset:

- `data/processed/leaderboard_entries.sqlite`
- `data/processed/leaderboard_entries.parquet`

Useful switches:

- `-DryRun`: print what would happen.
- `-SkipLaunch`: use an already running FH6 window.
- `-NoDisplayManage`: do not change FH6 fullscreen/windowed config.
- `-ClearCaptureOutput`: clear the configured capture output folder before running.
- `-Pages 50`: for a single capture, override how many leaderboard pages are captured while scrolling.
- `-CaptureCount 50`: older alias for `-Pages` when `-Pages` is not set.

By default the pipeline sets FH6 to windowed before launch, runs the selected workflow, and restores the previous display config afterwards.

Leaderboard table extraction is separate from route learning. The learned route only navigates to the table and captures pages. Column meaning comes from `config/fh6_rivals_1080p.json`, which defines the leaderboard crop and relative column ranges for rank, gamertag, car, car class, PI, drivetrain, time, ABS, TCS, STM, and gear. During capture, the learned route repeats: screenshot the current table page, scroll by rows unless this was the last requested page, wait for the key delay, then screenshot the next page. `-Pages` controls how many screenshots are captured, and `-Pages All` resolves the count from the leaderboard footer. The extractor processes only `leaderboard_*.png` from the capture folder, writes `rank_coverage_report.json`, and deduplicates repeated rows before writing CSV/JSON/SQLite/Parquet.

First discover the launch id and process/window name:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/find_forza_apps.ps1
```

Copy `config/capture_automation.example.json` to a real route file, then replace:

- `launch.command` with a `shell:AppsFolder\...` AppID or a Steam launch URI.
- `window.process_names` / `window.title_regex` with the actual game window.
- `steps` with the menu route from the main menu to the chosen Rivals leaderboard.
- `extraction.track` and `extraction.pi_class`.

On this machine, FH6 was detected as:

- Steam launch URI: `steam://rungameid/2483190`
- Process name: `forzahorizon6`
- Window title: `Forza Horizon 6`

Those values are already wired into `config/fh6_rivals_capture_steam.json`.

Run a dry-run first:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run_capture_automation.ps1 `
  -Config config/fh6_rivals_capture_steam.json `
  -DryRun
```

Then run the capture:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run_capture_automation.ps1 `
  -Config config/fh6_rivals_capture_steam.json
```

Forza should be in borderless/windowed fullscreen. Exclusive fullscreen can make normal Windows screenshots come out black.

## Display Mode Toggle

FH6 stores display mode in:

- `%LOCALAPPDATA%\ForzaHorizon6\LocalStorage_Shared\ForzaUserConfigSelections\UserConfigSelections`
- `%LOCALAPPDATA%\ForzaHorizon6\fullscreen_choice`

Check current state:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/set_forza_display_mode.ps1 -Action Status
```

Set windowed with backup:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/set_forza_display_mode.ps1 -Action Windowed
```

Restore the latest backup:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/set_forza_display_mode.ps1 -Action Restore
```

The route configs include a `display` block. Set `display.manage` to `true` when the automation should change FH6 to windowed before launch and restore the previous files when the automation exits.

## Visual Navigation

Forza menus are DirectX-rendered, so this project does not use Windows UI Automation controls. It treats the game as pixels:

1. Focus the game window.
2. Capture a screenshot.
3. Run Windows OCR over that screenshot.
4. Match visible text or save an observation dump.
5. Send keyboard/mouse input only after the expected state is visible.

Useful route actions in the JSON config:

- `observe`: screenshot plus OCR dump for calibration.
- `wait_for_text`: keep observing until OCR sees a regex.
- `click_text`: click the center of an OCR line matching a regex.
- `key_until_text`: press a key until OCR sees a regex.
- `click`: click absolute coordinates or `x_ratio` / `y_ratio`.
- `screenshot`: capture leaderboard frames for extraction.

To inspect the current FH6 screen without sending input:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run_capture_automation.ps1 `
  -Config config/fh6_observe_current.json `
  -SkipLaunch `
  -SkipExtract
```

That writes the screenshot and OCR text under `data/inbox/observe`.

The file `config/fh6_visual_route.example.json` shows the intended state-machine style. It is not a final FH6 menu route yet; the exact route needs one real observation pass from the main menu / current menu.

## Quick Start

1. Put screenshots or a short scroll recording into `data/inbox`.
2. Run the extractor:

```powershell
python scripts/extract_leaderboard.py `
  --input data/inbox `
  --track "Hakone Circuit" `
  --performance-class S1 `
  --profile config/fh6_rivals_1080p.json
```

The output is written to `data/processed` as:

- `leaderboard_entries.csv`
- `leaderboard_entries.json`
- `leaderboard_entries.sqlite`
- `leaderboard_entries.parquet`
- optional raw OCR JSON files when `--dump-ocr` is used

## Capture Workflow

For best results, open the in-game leaderboard, filter to one track and PI class, then slowly scroll through the rows while recording the screen. A 1080p or 1440p recording works well. The extractor samples frames from videos and removes duplicate rows.

You can also use screenshots. If OCR misses columns, adjust the crop and column ratios in `config/fh6_rivals_1080p.json`.

## Example

```powershell
python scripts/extract_leaderboard.py `
  --input data/inbox/my_scroll_recording.mp4 `
  --track "Tokyo Downtown Sprint" `
  --performance-class A `
  --event-type Rivals `
  --frame-every 1.5 `
  --dump-ocr
```

## Notes

- This uses Windows OCR through PowerShell, so no Tesseract install is required.
- It reads only the screen: it does not intercept traffic or touch Forza files.
- The parser is intentionally transparent: raw OCR output is available for calibration.
- Network interception and decryption are intentionally out of scope.
