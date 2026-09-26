# Extraction Speed Plan

Status: accepted 2026-08-18. Supersedes the "Next Steps" list in
[network_protocol_research.md](network_protocol_research.md).

Operator decisions on 2026-08-18:

- **Phase 3 is authorized** (direct in-process `GetRows` calls), with deliberate
  rate limiting near the game's natural request pace.
- **Priority is the full-catalogue sweep**, not single-board speed. This makes
  Phase 3 the critical path rather than an optional upside, because parameterised
  requests are the only way to change board without menu navigation.

Execution order therefore: Phase 0 gate -> Phase 2 plaintext boundary ->
Phase 3 direct calls -> Phase 1 hardening of whatever loop remains. Phase 1's
auto-discovery and checkpointing still get built, because they are also what
makes Phase 3 survive a game patch and a mid-sweep crash.

Goal: extract FH6 Rivals leaderboards as fast and as accurately as possible,
across many (mode, track, performance class) boards, from the Hyper-V VM.

## Where The Project Actually Stands

Three extraction paths exist in this repo. Only one is accurate.

| Path | Speed | Accuracy | Verdict |
| --- | --- | --- | --- |
| OCR of screenshots (`run_leaderboard_rank_scan.ps1`) | ~2,900 ranks/min claimed | Column mis-reads, needs coverage reports and dedupe | Dead end, keep only for menu-state recognition |
| Direct memory `ScoreboardRow` read (`run_memory_leaderboard_scan.ps1`) | ~670 rows/min steady, ~290 rows/min including startup | Exact values, zero missing ranks on verified runs | Current best, but too slow at scale |
| `GetRows` HTTP replay (`replay_captured_getrows.py`) | n/a | Response body is application-encrypted, undecodable | Blocked on crypto RE |

Measured from `data/memory_scans/cache300r_20260624_211135` (300 ranks, 0 gaps):

- first 100 ranks: 44 s (two full-process memory scans to discover the buffers)
- steady state: ~4.5 s per 50-row block → **~670 rows/min**
- a 20,000-rank board is ~30 min; a 94,000-rank board is ~2.3 h
- that is per board. There are 6 Rivals categories x N routes x 8 classes.

### Why the memory path is slow

Per 50-row block, `run_memory_leaderboard_scan.ps1` spends:

1. ~0.81 s sending ~45 discrete `{DOWN}` keys via `SendKeys` at 18 ms spacing
2. 0.35 s fixed `SettleMs` sleep regardless of whether new data arrived
3. ~0.3-0.5 s spawning a fresh `python.exe` for `capture_scoreboard_memory.py`
4. an extra ~100 `ReadProcessMemory` calls from `enrich_linked_vectors`, whose
   output is never used downstream
5. on roughly every other block, the exact-address read misses and the script
   polls for up to `ExactWaitSeconds` (4 s) before falling back

None of that is inherent. The inherent cost is 50 keypresses (the game samples
input per frame, so ~16.7 ms apart is the floor) plus one server round trip.

### Confirmed technical assets

- Post-decrypt `ScoreboardRow` array in process memory, stride `0x2d0`, with
  verified offsets for rank, XUID, gamertag, lap time, submitted time, class id,
  drivetrain id, and 10 assist/clean/ghost flags.
- The concrete network call: `POST /Services/o.xtsw`, `Content-Type: bin/xtsw`,
  `X-ClassName: Forza.WebServices.Scoreboard`, `X-Method: GetRows`, cursor-based
  (only `maxSize` is a visible parameter, there is no `startAt`).
- Frida 17.15.3 working in the VM at `C:\ForzaTools\Python312`, plus tracers for
  WinHTTP, libHttpClient response dispatch, RVA hooks, and memory watchpoints.
- The response body at the libHttpClient boundary is captured but encrypted
  (31,120 bytes, entropy 7.994, multiple of 16). The decrypt/deserialize
  function inside the `Xls` layer has not been located yet.
- The protocol carries far more than we currently extract: `carPerformanceIndex`,
  `carPowertrainId`, `carPower`, `carTorque`, `carWeight`, `tuneCreator`,
  `scoreId`, `garageId`, `usedDrafting`, `usedMulligan`, `hadPenalty`,
  `numberOfCarsPassed`, `totalRowCount`.

### The executable is packed: static analysis of the relevant code is worthless

Verified 2026-08-18 by comparing `data/network_probes/runtime_hooks/forza_5ed9000.bin`
(read from the file) against `forza_runtime_5ed9000.bin` (read from the live
process) at the same RVA:

- only **20.0%** of bytes match
- the on-disk page sits at **7.815 bits/byte** entropy; the runtime page is
  **6.507** and disassembles as ordinary MSVC code (`eb 03`, `48 8b fd`,
  `48 8d 05 ...`)

So `forzahorizon6.exe` ships those regions encrypted and decrypts them in
memory. This explains the earlier note that the binary "does not reliably
disassemble" around the Xls transport, and it invalidates the disassembly in
`data/network_probes/static_analysis/scoreboard_row_core_functions.asm` and
friends, which decode as nonsense (`rcl`, `jrcxz`, `movabs al, byte ptr [...]`)
because they are ciphertext.

Consequences:

1. **Phase 2 and 3 must work from a runtime dump**, not the shipped file.
   `scripts/dump_forza_runtime_image.py` (new) reads the module out of the live
   process and rewrites the section table so file offset == RVA, producing a PE
   that `pefile`/`capstone` and every existing helper in `scripts/` consumes
   unmodified. Validated against a live `explorer.exe`: RVA/offset roundtrip
   exact, exception directory intact, clean disassembly. It is read-only
   (`PROCESS_VM_READ`) and injects nothing.
2. **Byte-pattern signatures for version resilience must be built from runtime
   dumps**, never from the file, or they will match ciphertext that changes
   meaning on every build.
3. Expect anti-debug alongside the packer. The one clean stability test we have
   (`stability_20260623_224656_727.json`) shows attach exit 0, hooks loaded, and
   the game alive afterwards, so Frida is workable, but every long run needs a
   liveness watchdog and checkpointed resume.

### Two environment facts that shape the plan

1. **The VM build is pinned to the June 24 build** (`6.382.893.0`, Steam buildid
   `23832027`) only because Steam has not been online since. `AutoUpdateBehavior`
   is `0` (always update), and the live service will very likely force an update
   anyway. **Every hard-coded RVA in this repo is one patch away from being
   wrong.** Offset and code discovery must become automatic and self-verifying.
2. The VM is healthy and, since the operator logged Steam in on 2026-08-18, the
   game runs online again. **Logging in triggered a patch**: Steam build
   `23832027` -> `24584928`, file version `6.382.893.0` -> `6.420.696.0`. Every
   RVA recorded in June is dead, which is exactly why discovery has to be
   automatic. The shipped executable for the new build is archived at
   `data/network_probes/static_analysis/forzahorizon6_6.420.696.0.exe`.
   Steam's `loginusers.vdf` still has `RememberPassword` `0`, so the login will
   need repeating after long idle gaps.

## The Plan

Four phases. Phase 1 is unconditional and gives a usable tool. Phase 3 is where
the order-of-magnitude win is, and it is the only phase that writes to or calls
into the game process.

### Phase 0 — Gate: CLEARED (with a forced patch), 2026-08-18

Steam logged in, the game force-updated to `6.420.696.0`, and it runs online and
renders at 1920x1080 in the VM. The remaining Phase 0 task is to confirm a Rivals
leaderboard still loads for the account, which needs the game driven to a
`Change Rival` screen.

The forced patch is the useful part of this result: it is the scenario the plan
was built for, and it proves the point that no RVA or field offset may be
hard-coded.

### Phase 1 — Make the memory path 3-4x faster and resumable (unconditional)

Rewrite the scan loop as a **single long-lived Python process in the VM** that
owns both the memory reads and the input, replacing the PowerShell-drives-Python
loop.

1. **No per-block process spawn.** One `OpenProcess` handle for the whole run.
2. **Poll instead of sleep.** Replace the fixed 350 ms settle with a 5-10 ms
   poll on the two known buffer addresses until the next rank appears, with a
   timeout. This alone removes most of the wasted time and the 4 s miss penalty.
3. **Batch input via `SendInput`**, paced just above the frame interval, rather
   than `SendKeys` per key. Calibrate the minimum reliable spacing once and
   record it.
4. **Drop `enrich_linked_vectors` from the hot loop.** Keep raw row bytes; do
   field archaeology offline.
5. **Buffer output.** Append rows to one open file / SQLite transaction instead
   of writing a JSON + CSV pair per 50 rows.
6. **Auto-discover the layout at run start** instead of trusting constants:
   locate a contiguous run of records whose rank field increments by 1 and whose
   gamertag/lap-time/date fields validate, and derive stride and offsets from
   that. Fail loudly with a diagnostic dump if discovery fails. This is what
   makes the tool survive a game patch.
7. **Checkpoint and resume by rank**, so a crash or a patch mid-run costs one
   block, not the run.
8. **One datastore.** SQLite + Parquet keyed by
   `(rivals_mode, track, performance_class, captured_at, rank)`, so repeated
   snapshots are diffable instead of scattered per-run folders.

Expected: **2,000-2,800 rows/min**, i.e. a 20,000-rank board in ~8 min and a
94,000-rank board in ~40 min. Startup drops from 44 s to a few seconds once
buffer discovery is single-pass.

### Phase 2 — Read-only hook to get the plaintext response (no injection)

Two complementary routes, both requiring the game to be running.

**Route A, offline analysis of a runtime dump.** Dump the decrypted image with
`dump_forza_runtime_image.py`, then re-run the existing string/RTTI/xref helpers
against that dump instead of the shipped exe. The field names the protocol uses
(`carPerformanceIndex`, `totalRowCount`, `tuneCreator`) are the entry point: they
lead to the reflection/descriptor tables, and the runtime hooks already recorded
descriptor RVAs (`rows_descriptor` at `0x7237ec0`, `scoreboard_row_descriptor` at
`0x725b0e8`). If the wire format is descriptor-driven, the layout can be read out
of the binary as data rather than guessed from heap bytes.

**Route B, work backwards from the known-good end at runtime:**

1. Set a hardware watchpoint on the `rank` field of a known `ScoreboardRow`
   buffer (`trace_memory_watchpoint.py` already does this) and capture the stack
   at the moment the row is written.
2. Walk up that stack to the deserializer, and from there to its caller, whose
   input buffer is the **plaintext** `GetRows` response.
3. Hook that boundary read-only and dump one plaintext response.

Payoff even without Phase 3:

- Names every field, so we get car / PI / tune creator / `totalRowCount`
  correctly instead of guessing offsets from raw hex.
- `totalRowCount` removes the OCR-based player-total estimate entirely.
- Reading rows from the plaintext buffer is cheaper and more robust than
  scanning heap for row arrays.

### Phase 3 — Direct in-process `GetRows` calls (the real win, gated)

The 2026-08-18 descriptor recovery changed this phase from speculative to
specified. The scoreboard service takes `{leaderboardId, gameScoreboardId, view,
startAt, maxResults}` and returns `{rows, userRow, totalRowCount}`, so the target
is a normal random-access paginated API rather than a cursor to be walked.

Call it via a Frida `NativeFunction` and read the response from the Phase 2 hook.
This removes the UI from the loop entirely:

- no keypresses, no scroll, no settle
- `startAt` gives arbitrary rank offsets, so disjoint ranges can be fetched
  **concurrently** instead of sequentially
- `maxResults` sets the page size directly
- `leaderboardId` + `view` make board selection a request parameter, which is
  the difference between "one board per session" and the whole catalogue
- `totalRowCount` replaces the OCR player-total estimate

Open question for this phase: whether `GetRows` itself takes that shape or the
`{scoreId, maxSize}` cursor shape, with `GetRowsWithTotalCount` taking the other.

Constraints I will hold to unless told otherwise:

- Deliberate rate limiting. The natural in-game request rate is the reference;
  do not exceed it by a large factor. This is a live authenticated service on a
  real Xbox account, and the failure mode is a ban, not an error code.
- Read-only hooks first, calls second, and every call path validated on a
  throwaway target before a long run.
- Crash containment: the game will be killed and relaunched by the runner, and
  the run resumes from the last checkpointed rank.

### Phase 4 — Standalone replay outside the game (optional, probably unnecessary)

Reproducing the `bin/xtsw` encryption and `X-Validation` signing outside the
process would let us fetch without the game or the VM at all. It needs session
key extraction from the auth handshake plus the signing scheme, and it carries
the highest detection/ban risk. Phase 3 gets the throughput without any of that,
so this stays research-only unless Phase 3 is blocked.

## Built And Verified So Far (2026-08-18)

Both pieces below are unblocked work done while Phase 0 waits on the Steam
login. Both are read-only with respect to the game.

### `scripts/dump_forza_runtime_image.py`

Reads a module out of a live process and writes it back as a PE whose file
offsets equal its RVAs, so the decrypted code can be analysed with
`pefile`/`capstone` and every existing helper in `scripts/` unmodified. This is
the prerequisite for Phase 2 Route A, because the shipped executable is
ciphertext where it matters.

Validated against a live `explorer.exe`: exact RVA/offset roundtrip, 6,655
runtime-function entries recovered, clean disassembly, and the expected
comparison profile against the on-disk file (`.pdata`/`.rsrc`/`.reloc` identical,
`.didat` differing where the loader resolved imports).

`scripts/capture_forza_runtime_image.ps1` drives it from the host: verifies the
guest game process, runs the dumper in the VM, compresses, copies back, and
prints per-section runtime-only decryption ratios.

### `scripts/forza_scoreboard_layout.py`

Derives the `ScoreboardRow` layout from a buffer rather than trusting hard-coded
constants, so a game patch is a re-derivation instead of a re-reverse-engineering
job. It finds duplicated `(rank, rank)` u32 pairs, infers the stride from the
spacing of consecutive ranks, then locates each field by testing candidate
offsets column-wise across every record in the run.

Verified against all 16 captured chunks that hold a full 50-row block: **16 of 16
recovered the 6.382.893.0 layout exactly, with a single surviving candidate per
field and no warnings**, in 0.07 s total. (The other 25 chunk files hold 0 or 1
rows; those are capture failures from the old runner, not layout problems.)

Three flaws found and fixed by running against real data rather than one sample:

1. Requiring `carClassId` to vary across records fails on exactly the boards we
   scan, because a class-filtered leaderboard has a constant class id.
2. Allowing zero-valued id columns instead admits every zero-filled column, and
   the low/high halves of an MSVC `std::string` 8-byte size field read as a
   plausible `(small non-zero, zero)` id pair. Excluding confirmed fields and all
   32-byte SSO string blocks isolates the real pair.
3. `carDriveTypeId` 0 is a legitimate value (17 of 49 records in one block), so
   only the class id may be required non-zero.

One robustness fix came out of it too: a chunk from a failed June run holds rows
from two heap locations 37 GB apart, so naive buffer reconstruction tried to
allocate 37 GB. Rows are now clustered by address and the largest contiguous
cluster is used.

### `scripts/dump_forza_type_descriptors.py`, `find_runtime_xrefs.py`, `analyze_runtime_image_diff.py`

Recover the game's own reflection tables (860 types), map which pages are
runtime-decrypted, and find code/pointer references to a string or address inside
a 183 MB image in seconds rather than the hour a full disassembly costs. Findings
are recorded in [network_protocol_research.md](network_protocol_research.md): the
packing measurement, the corrected random-access pagination model, and the
corrected in-memory row layout with two mislabelled columns and three previously
unextracted fields.

### `scripts/forza_scoreboard_scan.py`

Live scanner that finds row blocks by structure and decodes them with a derived
layout, so it works on a build whose offsets nobody has recorded. Validated
offline against the June captures: 50/50 rows decoded, `performance_class` = `R`
for every row on the R-class board, and `car_id`, `car_class_id` and
`car_performance_index_raw` now extracted where the old scanner dropped them.

### Navigation must run inside the guest, in one process

Found the hard way on 2026-08-18. Driving the game with one key per host-side
scheduled task puts 10-30 seconds between inputs, and at that cadence:

- the game auto-hides its HUD, so OCR frames contain only the car and no menu
  text, making every state check fail
- menu context drifts between keypresses, so a replayed key sequence lands in the
  wrong place. 33 `RIGHT` presses intended to cycle hub tabs instead walked into
  the Character submenu

`scripts/forza_navigator.ps1` runs the whole perceive-act loop in one guest
process: the WinRT OCR engine is created once and reused (~250 ms per cycle
instead of a `powershell.exe` spawn per frame), the HUD is woken by nudging the
mouse one pixel rather than pressing a direction key that would move the
selection, focus is confirmed against `GetForegroundWindow` because `SendKeys`
reports success regardless, and every step is a bounded "press until the marker
appears" goal rather than a fixed keystroke count. Failures back out to a known
hub root instead of firing keys into an unknown submenu.

`scripts/start_forza_navigation.ps1` is the host launcher; it only copies, starts,
and tails. `-Explore` records every distinct screen so the route table for a new
build can be derived without a human describing the menus.

Design rule going forward: **no step of the extraction pipeline may require a
human operator in its input loop.** Both are too slow to drive a game, and
neither scales to hundreds of boards.

## Verified End To End On The Patched Build (2026-08-18, 21:30)

Navigation and extraction both work on `6.420.696.0` with nothing hard-coded from
the previous build.

### Navigation: cold start to leaderboard, unattended

`scripts/start_forza_navigation.ps1 -FreshStart` closes Forza, relaunches it and
drives it to the `Change Rival` leaderboard with no operator in the
input loop. The route, all of it discovered today because the patch invalidated
the old one:

```text
title -> Continue -> garage -> Campaign>Drive -> free roam -> ESC -> world pause
  -> RIGHT x8 -> ONLINE tab -> [RIGHT,DOWN] -> Rivals hub -> ENTER -> Road Racing
  -> route carousel: RIGHT x3 -> Soni Circuit -> ENTER
  -> class strip: RIGHT x6 -> R -> Y (Change Rival) -> leaderboard
```

Confirmed by the final frame containing `Filter: Global` plus the table itself,
with gamertags matching the June capture.

Four things about this build that no amount of re-reading the old configs would
have revealed, and which cost one run each to find:

1. The garage menu has no Online tab; Rivals requires leaving to free roam.
2. Every list is **horizontal**. 64 `DOWN` presses moved the route carousel not
   at all, with the cursor parked away from the UI; `RIGHT` found the track in 3.
3. Performance class `R` is rendered **off-screen** to the right of `S2`, so it
   cannot be found by reading the strip. The header names the selected class, so
   scroll and watch the header.
4. The leaderboard opens with **`Y` (Change Rival)**, not `ENTER` -- `ENTER` there
   starts a race. The on-screen glyph OCRs as "B", which is misleading.

### Extraction: layout auto-discovery earned its keep

`scripts/start_forza_scoreboard_scan.ps1 -Discover` decoded 11 rows, ranks 40-50,
from the live process. The record **stride changed from `0x2d0` (720) to `0x370`
(880)**, so `matches_build_6_382_893_0` is false and every hard-coded offset in
`capture_scoreboard_memory.py` is now wrong. Discovery derived the new layout
without being told anything.

Notably the field offsets *relative to the rank pair* are unchanged
(xuid +0x10, gamertag +0x18, lap time +0x138, submitted time +0x140,
drive type +0x158, powertrain +0x15c, flags +0x160). Only the element size grew,
which suggests an outer wrapper around the same inner record rather than a
reshuffled struct.

Data verified internally consistent: lap times rise monotonically with rank
(33.524 s at rank 40 to 33.592 s at rank 50), `carClassId` is 6 = R throughout,
and `carId` values match the meta cars seen in June.

### Three limits this exposed, in priority order

1. **Only 11 rows per scan, not 50.** Eleven rows is exactly the visible viewport,
   so the block found is the UI's on-screen row list rather than the 50-row block
   the service returns. The old scanner read 50-row blocks. Either the fetch
   buffer still exists with a different stride and was missed, or this build keeps
   rows only in the wrapper. Worth resolving before tuning anything else, because
   it sets the rows-per-round-trip ceiling.
2. **A full discovery sweep costs 202 s** over 6.1 GB of committed memory. Fine
   once per session to populate a build profile, far too slow per block. The
   profile-based fast path that re-reads known addresses is what repeated scans
   must use.
3. **`carPerformanceIndex` is still uncalibrated**, reading ~0.886 as float32
   where the UI shows PI 998. The on-screen table shows `R 998`, so a single
   frame is enough to calibrate it.

### A performance bug worth not repeating

The first scan attempts burned 19 and 7 minutes of CPU with no output. Candidate
detection was vectorised with numpy, but each candidate then handed the whole
16 MB block to `discover_layout`, whose rank search is a Python loop over every
4 bytes. It now receives a ~40 KB window around the run instead, with a stride of
slack past the last record so the column tests still reach its far fields. The
same mistake had already appeared in the layout module's flag scan, where fixing
it took discovery from 8 s per chunk to 0.14 s.

## Correction And Root Cause (2026-08-19)

The "verified end to end" claim above was true for one run and did not survive
the next. Two things were wrong underneath it.

### The host had been running a broken GPU partition for a day

The host took NVIDIA 32.0.16.1088 on 2026-08-18 at 02:38, while `vmwp.exe` still
held the GPU. GPU-PV requires the guest's staged driver to match the host's
exactly; the guest stayed on 32.0.16.1047 from 2026-05-19. From that moment the
guest's RTX 5080 was **Problem Code 43** and the game was running without real
GPU acceleration -- through the 20:00-22:56 session on the 18th and again on the
19th. Driving that broken partition hard-froze the host at 16:29 on 2026-08-19:
no bugcheck, no TDR event, no dump, just Kernel-Power 41 on the next boot.

So the navigation and extraction results recorded above were obtained on a
software-rendered game. They are still valid as *route* and *layout* findings --
the menus and the memory are the same either way -- but every timing number
taken before this fix should be treated as unrepresentative.

`scripts/sync_vm_gpu_pv_drivers.ps1` now re-stages the guest driver from the
running host over PowerShell Direct. The pre-existing
`install_vm_gpu_pv_host_drivers.ps1` cannot do this job unattended: it mounts the
VHD, which needs `SeManageVolumePrivilege`, and membership of Hyper-V
Administrators does not include it. Check after every host driver update --
nothing else warns you.

The partition was also allocated at 100% of VRAM, compute, encode and decode of
the same GPU driving the host desktop. It now runs at 50%.

### The route was still counting keypresses

`[RIGHT,DOWN]` to reach the Rivals tile is a press count, and the patch reordered
the tile row, so it walked into the Social panel's Online Player List three runs
in a row. Which tile is highlighted is not in the OCR text, so the navigator
could not detect the miss, and its fallbacks were equally blind.

The fix generalises the mouse-awareness the script already documented as a
hazard. `Get-ScreenFrame` now keeps each OCR line's bounding rectangle,
`Find-TextRect` resolves a label to a position, and `Select-ByHover` moves the
cursor onto it, confirms with ENTER, and re-parks the cursor. The ONLINE tab
labels the tile `Rivals` / `Top the Leaderboards`, both of which OCR cleanly.

Verified offline on the host: the OCR engine returns 232 line rectangles for a
desktop frame with none inverted or off-screen, and a hover round-trip on a
3840x2160 display lands exactly on the requested pixel, so OCR bitmap space and
`SetCursorPos` space agree with no DPI skew.

The rule this makes explicit, and which the remaining route steps should follow:
**name the target, never count the steps to it.** A press count silently encodes
the current menu layout, which is precisely what a patch changes.

## The 50-Row Buffer Exists (2026-08-19, verified)

Limit 1 above -- "only 11 rows per scan, not 50" -- was wrong about the build,
and right only about the scanner. `scripts/find_scoreboard_buffers.py` detects
records by XUID rather than by a duplicated `(position, rank)` pair, and against
the live process at the Soni Circuit / R leaderboard it found three real record
arrays in **15.6 s** over the same 6.1 GB that the old discovery sweep took 202 s
to cross:

| stride | records | ranks | consecutive | gamertag sample |
| --- | --- | --- | --- | --- |
| `0x370` | 45 | 1-50 | no | darknessqq8, trviis, VSM Lightning, LetzeLU, Quill089 |
| `0x2d0` | 39 | 12-50 | yes | JNordSchleife, VSM FstR Doctor, consistence0, VRX Bolt |
| `0x370` | 28 | 12-39 | yes | JNordSchleife, VSM FstR Doctor, consistence0, VRX Bolt |

Cross-checked against the frame captured at the same moment: the first array's
gamertags are the ones on screen (`trviis`, `VSM Lightning`, `LetzeLU` all appear
in the visible table), so this is the displayed page and it holds **ranks 1-50,
not 11**. The 11-row result was an artefact of the duplicated-rank-pair
heuristic, which finds the UI's on-screen row list and cannot see a buffer where
`position` is an index rather than a rank. The rows-per-round-trip ceiling is 50
as originally assumed, and Phase 1's throughput estimate stands.

Two further things fall out of this, both of which contradict earlier notes:

- **Both strides are live at once.** `0x2d0` (720, the June size) and `0x370`
  (880) hold the *same rows* -- arrays two and three have identical gamertags at
  different addresses and different spacings. So the stride did not "change from
  720 to 880"; the 880 record is an outer wrapper around the unchanged 720-byte
  inner record, which is exactly what the unchanged relative field offsets
  suggested. `capture_scoreboard_memory.py`'s June constants are therefore not
  necessarily dead -- they may still fit the inner record.
- **XUID banding is a far better locator than structure matching.** It is
  selective enough to need no assumption about the surrounding layout, and the
  record stride falls out of the spacing between hits rather than being supplied.
  Scans that must be fast should key off it.

`carPerformanceIndex` also has its calibration reference now: the frame captured
at the leaderboard reads `R 998` for every visible row, against the ~0.886 that
offset currently decodes as float32.

Raw report: `data/memory_scans/buffers_20260819/buffers.json`.

## Full Board Paging Works (2026-08-19)

Ranks 1-500 of Soni Circuit / A were paged and exported with **500 unique ranks
and zero gaps**, as Parquet, CSV and JSON. The whole chain now runs unattended:
navigate, page, export, switch board, repeat.

Three things had to be fixed to get there, and two of them were wrong beliefs
rather than missing code.

### The June decoder was never broken

`capture_scoreboard_memory.py` decodes this build correctly -- 50 rows, ranks
1-50, gamertags matching the screen. The claim above that "every hard-coded
offset is now wrong" was false; only the *navigator* needed replacing. This
follows directly from the buffer finding: the 880-byte record is a wrapper
around the unchanged 720-byte one.

### Paging stalled on a buffer it could see

After the first page every read returned nothing. The board was scrolling fine
and a clean 50-row block for ranks 51-100 was sitting in memory; the scanner
anchors on the exact `(rank, rank)` byte pair, and scrolling evicts rows from the
top, so asking for rank 51 when the live block starts at 52 matched nothing at
all. `--expected-rank 51` gave 0 rows while `--expected-rank 60` gave 41.

The scanner now falls back to an unanchored sweep and takes the block spanning
the wanted rank, or the one starting closest after it. General rule: **never
anchor on a value the game is free to evict.**

### Board switching had to stop meaning "done"

The navigator returned success on seeing any leaderboard, so a sweep would have
silently rescanned whichever board was already open. `-FromLeaderboard` backs out
to the route list first. This is what makes a sweep affordable: a cold start is
about four minutes, a switch is seconds.

`scripts/run_board_sweep.ps1` is the loop: catalogue in, manifest out, `-Resume`
to continue, and an unreachable board is recorded and skipped rather than
killing the run.

### What still bounds the sweep

- **Throughput ~120-150 ranks/min**, drifting down with depth. The cause is that
  every page whose exact address missed paid for a full ~6 GB sweep. A
  near-address stage now sits between the two, searching a window around the last
  known block first; its effect is not yet measured.
- **Board depth is unknown.** This build shows no player count on the
  leaderboard, so `-TotalRanks Auto` cannot work here and depth is only known by
  paging to the end with `UntilEnd`.
- **Scope.** 6 verified tracks x 7 verified classes is 42 boards in Road Racing
  alone; six modes puts the matrix past 250. At 20,000 ranks that is ~2.7 h per
  board, so "every track, every class, first to last place" is a multi-day job
  even before the personal-time restriction is accounted for.
- **Two fields decode wrongly.** `car_class_id` reads 3 on both the R and the A
  board, so it is not the performance class -- the earlier note that "carClassId
  6 = R" is wrong. `car_performance_index` comes back empty.

## Cross-cutting work

- **Version resilience over pinned RVAs.** Byte-pattern signatures and
  self-validating structure discovery, with a single `build_profile.json` per
  game build recording what was discovered and verified.
- **Board catalogue.** Enumerate the real (mode, track, class) matrix once and
  store it, so sweeps are driven by data rather than by hand-written batch JSON.
- **Field completion.** Decode the remaining row fields; prefer Phase 2's named
  plaintext over offset guessing. The raw 720-byte rows already captured are
  mostly MSVC `std::string` slots that read as empty with stale XAML text in
  their inline buffers, which is why car/PI archaeology stalled.
- **Prune the dead OCR path** down to just menu-state recognition, and archive
  the rest so the repo stops carrying three half-working pipelines.

## Throughput summary

Measured on 2026-08-19 against Soni Circuit / A on build 6.420.696.0, 500 ranks
per run, verified complete (500 unique ranks, no gaps) in both cases.

| | Before | After the near-address stage |
| --- | --- | --- |
| Cumulative over 500 ranks | ~125/min | **565/min** |
| Steady-state rate | ~120/min | **~800/min** |
| Full 6 GB memory sweeps | one per page | **none** |
| 20,000-rank board | ~2.7 h | **~25 min** |
| 94,000-rank board | ~13 h | **~2 h** |
| Switching board | ~4 min (cold start) | **~20 s** (-FromLeaderboard) |

The old table's "~4.5 s per 50-row block" was never achieved on this build; the
real figure before this work was ~24 s per block, because every page whose exact
address missed paid for a complete sweep of the process. There was nothing
between "read one known address" and "search all 6 GB"; a 256 MB window around
the last known block now sits in between and caught every single page of the
measured run. The full sweep survives as the last resort and was not needed once.

The remaining unknown is board depth. This build does not show a player count on
the leaderboard, so `-TotalRanks Auto` cannot work and depth is discovered only
by paging with `UntilEnd`.

## First Full Board, and the Two Faults That Ended the Overnight Run (2026-08-20)

**Highway Circuit / D / Road Racing was scanned end to end**: ranks 1-8193,
8136 rows, `status: end_detected`, in 87 minutes. That is the first board taken
to its last place, and it settles the depth question for this board -- 8,193
players, discovered only by paging with `UntilEnd`, since this build shows no
player count.

It also produced the first honest per-board cost: **67 ranks/min**, an order of
magnitude below the 740/min measured on Soni Circuit. The cause was the
near-address window: on Highway Circuit the row buffer relocates further than
256 MB between pages, so the near read missed and every page paid for a full
~6 GB sweep. `-NearRadiusMb` is now 1536 by default. **Unverified** -- the run
that would have measured it never got a board.

Two faults are fixed but likewise unverified against the game.

### Single-rank gaps at page boundaries

57 ranks were missing out of 8,193, every one of them at a chunk request
boundary (`rank mod 100` of 1 or 51). The scanner takes the live block spanning
the wanted rank, or the nearest block starting after it. When the scroll
overshoots by one row, the wanted rank is already evicted from the top of the
live window, the block comes back starting one past it, and that rank is lost
for good -- the next request is computed from the block's maximum, so the loss
does not compound, it just leaves a hole.

`run_memory_leaderboard_scan.ps1` now compares the block's minimum rank against
the rank it asked for. If the block starts past it, the loop sends UP keys to
bring the row back into the live window and re-requests that exact rank, up to
`-MaxGapRetries` (2) times before accepting the gap. A recovery read reads below
the frontier by design, so it no longer counts toward the no-progress limit.

### A zombie game turns one failure into a wasted night

Five consecutive navigation runs failed with `Stuck in state 'unknown'`, each
OCR'ing the Steam desktop and the PowerShell console instead of the game. A
`forzahorizon6` process existed and the navigator attached to it -- the log shows
`focus did not stick on first attempt` -- but its window never came up, so every
board failed in about two minutes.

The sweep treated each of those as the board's own fault, recorded it
`unreachable`, and moved to the next one. Left alone overnight that walks the
entire route list without scanning anything, and a `-Resume` cannot tell an
infrastructure failure from a board that genuinely has no posted personal time.

`run_full_sweep.ps1` now counts consecutive navigation failures. A single failure
still means "this board is unreachable", which is usually true. Two in a row
escalate to a VM reboot (`-RecycleAfterFailures`, `-MaxRecycles 6`), after which
the *same* board is retried rather than skipped. The reboot is the documented
reset for this state; forcing a GPU-partitioned VM's game to relaunch in place is
what breaks its Steam launch.

The manifest now also records `rows`, `max_rank` and `missing_ranks` per board
from the merge report, so "complete" can be audited instead of trusted.

### Where the sweep stands

One board complete out of 161 in Road Racing alone (23 routes x 7 classes). At
the unverified 740/min the D board would take ~11 minutes rather than 87, which
is the difference between one night covering ~40 boards and covering ~5. The
first board of the next run measures it.

## Measured: 56 → 617 ranks/min, and Gapless (2026-08-20, later)

Three boards of Highway Circuit / Road Racing, same route, same build, so the
numbers compare directly:

| | D (before) | C (before, deeper fixes off) | B (all fixes) |
| --- | --- | --- | --- |
| Ranks | 8,136 of 8,193 | 5,593 of 5,593 | **8,763 of 8,763** |
| Missing ranks | 57 | 0 | **0** |
| Wall clock | 146 min | ~61 min | **14.2 min** |
| Effective rate | 56/min | 92/min | **617/min** |
| End-of-board detection | ~26 min | 37 min | **1.4 min** |

### What the profile says now

Of B's 14.2 minutes, **173 exact-address pages at 3.8 s each are 11.1 minutes,
78% of the run**. Three full sweeps cost 2.1 min between them and everything
else is under a minute. The bottleneck has moved from searching memory to the
per-page fixed cost, which is roughly 1.2 s of keypresses (45 DOWN at 18 ms plus
a 350 ms settle), ~0.5 s of Python process startup, and the read itself.

### The vectorised scan pays where the anchor fails, not everywhere

This is worth stating precisely, because the first estimate was wrong. When the
wanted rank exists somewhere in memory, `bytes.find` locates it at C speed and
always did; a full sweep on that path costs 42.7 s, and that is **read** cost --
6 GB at about 140 MB/s -- not search cost. The Python 4-byte loop only ran when
the anchor *failed*: at the end of a board, and for ranks the game had already
evicted. Those were the 240-second pages, and they are the ones that collapsed.

So the two fixes are complementary rather than redundant:

- **Vectorising** (`find_rank_pair_offsets`, numpy) turns the unanchored scan
  from 142 s per 6 GB into 3.9 s, verified byte-identical to the scalar loop.
- **Capping full sweeps per stall** (`-FullSweepStallLimit 1`, plus one more on
  the final attempt) stops paying eight times over to learn the same thing. The
  end of a board is a stall that cannot resolve: the memory does not change
  between attempts, so attempts 2-7 were re-searching an unchanged haystack for a
  rank that does not exist.

### Language and parallelism, measured rather than assumed

Single core, 512 MB buffers, extrapolated to 6 GB:

| | Throughput | 6 GB |
| --- | --- | --- |
| Python scalar loop (what shipped) | 0.04 GB/s | 142 s |
| numpy, full scan (3 passes + stride filter) | 1.55 GB/s | **3.9 s** |
| C `memchr` (`bytes.find`, no match) | 5.68 GB/s | 1.1 s |
| Plain memory copy -- the floor for any language | 5.41 GB/s | **1.1 s** |
| numpy, single SIMD comparison | 11.18 GB/s | 0.5 s |

`ReadProcessMemory` has to copy the bytes regardless of language, and that copy
alone is 1.1 s per 6 GB. A perfect C port would take the scan from 3.9 s to
~1.1-1.5 s: a factor of 3, after numpy already took a factor of 37. Threading
numpy across regions reaches the same floor without porting the decoder, so a
native rewrite is the last thing to reach for, not the first.

### Board switching without a cold start

`-WarmStart` on `run_full_sweep.ps1` switches boards from the open leaderboard
instead of relaunching: C ended at 15:12:25 and B was on the next leaderboard at
15:12:49, a **24-second** switch against roughly four minutes for a cold start.
It also keeps the game continuously driven, which is what it survives.

### What is left, in order of measured payoff

1. **The 3.8 s page.** 78% of a board. Test whether the leaderboard honours
   `PageDown`: 45 keypresses would become one, cutting ~1.1 s. Then make the
   scanner a persistent process instead of one Python start per page (~0.5 s).
   Together the page goes to roughly 1.5 s and a board to ~6 min.
2. **Export overlap.** Archive, copy, merge and Parquet cost ~5 min per board
   with the game idle -- and idle is when it dies. None of it needs the game, so
   it belongs in a background job while the next board navigates.
3. **The read path.** `read_memory` allocates a fresh buffer per 16 MB chunk and
   copies it again via `.raw`, roughly 12 GB of avoidable memcpy per sweep.
   `scripts/bench_process_read.py` measures chunk size against a reusable buffer
   to say whether 140 MB/s is syscall overhead or the real rate.
4. **Pointer chain instead of searching.** Reading a pointer to the row
   container is a factor of 1000 over sweeping 6 GB, and it makes relocation a
   non-event. Open-ended reverse engineering, so it ranks after the certain wins.

## The 1-Second Timeout That Was Costing Whole Boards (2026-08-20 night)

The scan had been reporting `end_detected` on boards that were nowhere near their
end, and the sweep recorded those as `complete`. Root cause was a single
parameter: `-ExactWaitSeconds 1`. The exact-address read waited one second for the
page, gave up, the near read missed too, and a ~50 s full sweep then "found" the
block **at one of the same cached addresses**. Nothing had moved; the game had
simply not finished fetching, and the sweep was an expensive way to wait for it.

Evidence: across 213 productive reads on Highway Circuit A only 14 distinct buffer
addresses appeared, and the entire second half of the run alternated between
exactly two, ~43 GB apart in two heap arenas. The address was always known.

Fixed by ordering the ladder properly -- exact (1 s), then the near arenas, and
only then a 30 s wait at the known addresses before any sweep. Hanging the long
wait on the *first* stage instead cost 40 s on every page whose block had merely
moved a little, taking 300 ranks/min down to 75.

Result on the same board that had "ended" at 12,000 the day before:

| board | before | after |
| --- | --- | --- |
| Highway Circuit A | 11,999 rows, "complete" | **29,697 rows, gapless, scrollbar-confirmed end** |
| Highway Circuit S1 | 1,949 rows | 24,199 (still truncated) |
| Narai-Juku A | -- | 50,850 (still truncated) |

Steady rate measured from chunk timestamps: **4.0 s per 50-rank page, 750/min**,
27 of 30 pages at exactly 4 s with the occasional 40-70 s late page absorbed by
the wait instead of ending the board.

### The scrollbar is the end-of-board signal

`scripts/detect_leaderboard_scrollbar.py` reads the thumb position out of a frame.
Completed boards read 99.5% with the thumb at the bottom, and the size implied by
the position lands within 0.5-2% of the true total (29,697 actual against 29,849
estimated). A board that stops with the thumb elsewhere is truncated, and
`run_full_sweep.ps1` now records `truncated` rather than banking it. Trust the
scrollbar, not "no new rows": mod-50 endings are page boundaries, not board ends.

### The resume loop does not work, and single-pass beats it

Re-entering a board does make the game serve again -- proven directly: S1 stopped
dead at 1950 twice, and after re-entry a scan with `-StartRank 1951` returned
1951-2000 immediately. But the seek back to the resume point never converges:

| board | resume depth | align attempts | chunks | productive | gained | took |
| --- | --- | --- | --- | --- | --- | --- |
| Highway S1 | 23,501 | 6 of 6 | 86 | 14 | 700 | 2 h 07 |
| Narai-Juku D | 1,001 | 6 of 6 | 85 | 14 | 700 | 1 h 27 |

Depth was not the cause; the shallow case failed identically. Prime suspect is
`Measure-BoardPosition` reading in open mode, which returns *some* resident block
rather than the one under the cursor, so the seek steers by a stale position.
A first pass on the same board took 999 ranks in 10 minutes. Until the seek is
fixed, run `-MaxResumePasses 1`.

## Phase 1 Attempted: Read-Correct, Input-Blocked (2026-08-21)

`scripts/forza_fast_scan.py` moved the whole loop into one guest Python process.
Nine runs, no throughput measurement. What it proved works:

- layout auto-discovery derives this build's stride as **880**; the hard-coded
  `0x2D0` = 720 in `capture_scoreboard_memory.py` is pre-patch and wrong
- decoding is exact: real gamertags, monotonically increasing lap times
- a build profile carries stride and every field offset across boards; only the
  addresses go stale, and re-finding those near the previous ones is cheap
- `focused=True` via largest-visible-window plus `GetForegroundWindow` check,
  `sizeof(INPUT)` 40, `SendInput` returning success

What blocks it: **the list never scrolls.** `SendInput` and `keybd_event`, at 8 ms
and 18 ms, with the advance capped to ~50 like the working loop, leave the resident
set unchanged. A success code from `SendInput` means "queued", not "acted on". The
loop that does drive the game uses `[System.Windows.Forms.SendKeys]` from a
PowerShell process in the console session; the difference is the sender.

Six of the nine failures came from reimplementing something this repo already had:
the run grouping (`runs_from_offsets`), the address-relocation fallback, the window
pick, and the proven key spacing. The old loop is slow, but its stages exist for
reasons.

### The salvage: keep the sender, remove the waste

`scripts/forza_row_server.py` plus `scripts/forza_paged_scan.ps1` split the loop.
PowerShell keeps `SendKeys`; a resident Python process holds one handle and answers
`{"want": N}` over stdin/stdout with the page containing that rank, polling every
few ms. That removes the two costs worth removing -- ~0.5 s of interpreter start
per page and the fixed 350 ms settle -- without touching input.

Measured so far, without the game: the protocol handles a present rank, a late one
(waited, not missed), an absent one (timeout, not hang), and 200 rows cross the
pipe in 56 ms, so transport is not a factor against a 4 s page.

One pitfall found by that stub test rather than by a live run: **PowerShell 5.1
writes a UTF-8 BOM into a redirected stdin** and cannot be told not to, so the
first request arrives with a BOM attached and `json.loads` rejects it. The server
strips it. This would have looked exactly like a server that crashes on startup.

Still to measure live: whether removing per-page process start and the fixed settle
actually reaches the 2,000-2,800 ranks/min this plan estimates for Phase 1.

## The Resume Loop Was Never A Seek Problem (2026-08-21 night)

The two failed resume passes were re-read from their own artefacts rather than
re-run, and they say something different from what was assumed. The suspicion on
record was that `Measure-BoardPosition` steers by a phantom position. The recorded
`align_*.json` files refute it: the seek converges, and it converges to the right
place.

Highway Circuit S1, resuming at 23501 (`..._S1_20260821_014410_p02`):

| capture | rows | span | address |
| --- | --- | --- | --- |
| align_001 | 50 | 1-50 | 0x17dc67dbda0 |
| align_002 | 50 | 23451-23500 | 0x17dc67dbda0 |
| align_003..006 | 50 | 23451-23500 | 0x17dc67dbda0 |

Narai-Juku D, resuming at 1001, is the same shape: 1-50, then 951-1000 four times
over. Both seeks land on the 50 rows immediately ABOVE the wanted rank and stop
there. Nothing phantom about it -- the view really was where the measurement said,
and attempts 3-6 changed nothing because the presses aimed past the end of the
served list, where they are eaten.

### The cost was in the paging loop, and it is arithmetic

With the view parked at 23500 and rank 23501 wanted, the loop then ran this cycle
for every single page, from `runner.log` and the chunk mtimes:

```
no row block found for rank 23601 (1/8)   <- 60-72 s each
...                          (2/8)
...                          (3/8)
...                          (4/8)
4 failed reads in a row; nudging the view by 5
...                          (5/8)
5 failed reads in a row; nudging the view by 5
rows=150 max=23650                        <- 8 s, arrives right after the nudges
key DOWN x40
```

Five dead reads at 60-72 s, then two 5-row nudges, then the page. 360 s per 50
ranks -- **8 ranks/min, 14 productive reads out of 85** -- and the keys that
actually delivered each page cost 0.2 s.

Why the nudges are what delivers: the game fetches the next page when the cursor
crosses the BOTTOM of the loaded one. After a productive page the jump parks the
cursor at `lastMaximum - PrefetchMargin`, five rows ABOVE that bottom. On the
first pass of a board that prefetches a page ahead, that is free -- the next rank
is already resident, and the margin is what keeps it from being evicted off the
top of the live window (the x01/x51 single-rank gaps). On a re-seated deep board
nothing is prefetched, so the parking spot fetches nothing at all, and the loop
spends five sweeps discovering it. The two nudges put the cursor 10 rows down,
five past the bottom, and the page arrives.

So the failure had nothing to do with depth, which is why the shallow Narai-Juku
case failed identically, and nothing to do with the seek.

### What changed in `run_memory_leaderboard_scan.ps1`

1. **A failed read presses towards the wanted rank on the first miss**, not after
   four, and only towards it: `$crossTarget = $expectedRank + $PrefetchMargin`,
   so once the model is on target the pressing stops. That is the difference from
   the old unconditional +5 nudge that drifted the A board from 750 to 50
   ranks/min -- this one cannot walk past its target, and `-MaxStallAdvance` (50)
   caps the creep for the case where the model is behind the truth.
2. **Once `-CrossToFetchAfter` (2) pages have been delivered by such a cross, the
   jump parks the cursor PAST the page bottom** instead of above it. Sticky per
   board, not per page: alternating the two spots pays a dead read every other
   page. Two proofs rather than one, so a single late page on a healthy board is
   not mistaken for a board that has stopped prefetching.
3. **The seek anchors the rank model on what it measured.** The align loop sent
   key bursts without counting them into `$currentRank`, so every resume entered
   the paging loop believing the cursor was at `-StartRank` no matter where the
   presses had left the list. Counting the presses would be worse, since they are
   eaten at the end of the served list; the measured block maximum is the anchor.
4. **The throughput watchdog resets its window on a recovery**, so it no longer
   stops a board in the very moment the stall nudge fixed it (Highway Circuit S2).
   A chronic-stall cap replaces what that would otherwise disable: four recoveries
   from EXPENSIVE stalls (`>= -StallNudgeAfter` misses) without a clean window
   still reports `degraded`. Cheap one-miss recoveries do not count, or a board
   that had just been taught to page properly would be stopped for it.

Evidence that crossing does not cost the head of a page: in that same trace all 14
pages arrived with `minimum_rank` exactly at the wanted rank, with the cursor 5-15
rows past the previous bottom.

### Verified offline, not yet live

The game is not running, so this is checked against the recorded trace and a model
of the two board behaviours (`scripts/sim_paging_arithmetic.py`), with the per-read costs
taken from the chunk mtimes (dead read 65 s, productive read 8 s, key 18 ms):

| board model | dead reads / 8 pages | ranks/min |
| --- | --- | --- |
| prefetching, before | 0 | 337 |
| prefetching, after | 0 | 337 |
| on-demand, before | 39 | 9.2 |
| on-demand, after | 2 | 119 |

The prefetching case presses an identical 395 keys before and after and never arms
crossing, which is the property that matters: the first-pass path that delivered
293,354 rows is untouched. The on-demand case keeps two dead reads because the
first page still has to teach the loop, and the model's 119/min understates the
steady state -- once crossing is armed a page costs one 8 s read plus ~1 s of keys.

Live confirmation needed before trusting an unattended sweep, and the honest test
is a resume pass on a board already known to be truncated: expect no more
`no row block found` runs of five, and a `pages needed a cross` line early.

### Still open

- **`Measure-BoardPosition` is still not proof.** Open mode takes the LONGEST
  resident run in the process (`max(arrays, key=len)`), which is not necessarily
  the block under the cursor. It happened to be right in both recorded passes, but
  a stale first page from the previous board would read as "at the top" and no
  code here can tell. The cheap honest test is differential: press ten DOWNs and
  take the block that MOVED. Until that exists, do not add logic that skips work
  on the strength of an open-mode read -- e.g. skipping the up-burst when the view
  looks like it is already at the top, which would otherwise save 7 minutes per
  deep resume.
- **`-MaxResumeDepth` (6000) was tuned against 8 ranks/min.** A resume at depth D
  costs about `D * 0.036 s` in seek presses -- 14 minutes at 23500 -- which was
  indefensible against a pass that gained 700 ranks in two hours and is a
  different calculation against a pass that pages properly. Re-tune it after the
  live check, not before.

### The same bug is the truncation signature, on every board

Having found it on the resume passes, the obvious question is how far it reaches.
All 37 sweep runs were scanned for what happened immediately before the scan died
(`re` over each `runner.log`: the last gap recovery, the last downward jump, the
rank the stall settled on):

| ending | runs | had a `key UP xN` gap recovery just before | last downward move |
| --- | --- | --- | --- |
| `truncated` | 17 | **17 of 17** | `key DOWN x10` |
| `end_detected` at a mod-50 rank now believed short (S1 100, S1 1950, A 12000) | 3 | 3 of 3 | `key DOWN x10` |
| `end_detected`, non-mod-50, believed genuine | 12 | **0 of 12** | the real remainder |

The separation is total, and the mechanism is visible in the log. Hokubu Circuit B,
which recorded 149 rows:

```
rows=149 max=150
block started at 102, past wanted 101; key UP x59 to recover   <- twice
key DOWN x10                                                    <- the no-progress cap
no row block found for rank 151 (1/8) ... (8/8)
4..7 failed reads in a row; nudging the view by 5                <- 20 rows, total
stopped at rank 150 but the scrollbar thumb is at 0% -- board is TRUNCATED
```

The gap recovery for the missing x01 rank scrolls the cursor 59 rows UP, the
duplicate read that follows caps the return trip at `key DOWN x10`, and the stall
nudges add at most 20 more. So rank 151 is requested with the cursor sitting around
rank 36 -- some 114 rows above the bottom of the loaded block -- and on a board that
only fetches when the cursor crosses that bottom, nothing is ever going to arrive.
Eight dead reads later the board is recorded as truncated at a multiple of 50.

That is the whole mod-50 signature: a board recorded as ending on a block boundary
is one where the cursor never crossed the boundary. Not a session that has aged, not
a filtered view, not a server limit. It also explains why a fresh process raised the
ceiling rather than removing it -- a different rank is where the first x01 gap
happens to fall.

**With the patch the same situation costs two failed reads:** the first miss presses
`min(-MaxDownBurst, target - cursor)` = 60 rows, the second presses the remaining
~50, the cursor crosses 150, and the game serves 151-200. The 10-row cap on a
no-progress jump is left alone -- it exists so a view that is not moving is not
hammered, and the failure path now supplies the downward pressure instead.

**What this does not settle:** whether every one of those 20 boards has more rows to
give. The scrollbar is still the arbiter, and it said `0%` on Hokubu B, which is
consistent with a board of thousands. Re-running them is the test, and it is now
worth doing: 17 truncated boards plus 3 falsely completed ones is most of the
catalogue, and the sweep totals 298,540 rows against 12 boards that are actually
finished.

## Measured: The Input Path Is The Bottleneck, Not The Fetch (2026-08-22)

`scripts/forza_burst_scan.ps1` drives the open board with one key strategy at a
time for a fixed slice of wall clock and writes a per-cycle CSV that separates the
two costs: `keys_ms` (sending presses) and `wait_ms` (waiting for the game to serve
the next block). Highway Circuit / Road Racing / A, the same board the reference
sweep took gaplessly at 724 ranks/min, so the numbers compare directly.

| strategy | ranks | cycles | keys_ms per cycle | **wait_ms (median)** | ms per key | ranks/min |
| --- | --- | --- | --- | --- | --- | --- |
| `down18` (50 separate SendWait, 18 ms apart) | 141 | 5 | 7,583 | **5** | 63 | 188 |
| `down8` (same, 8 ms apart) | 371 | 8 | 5,698 | **8** | 47 | 484 |
| `burst` (one `{DOWN 120}` call) | 360 | 11 | 3,773 | **9** | 31 | 514 |
| `pgdn` (`{PGDN}` presses) | **0** | 2 | 118 | 28,283 | -- | **0** |

Read the `wait_ms` column first. Across 24 productive cycles the game served the
next block in **5-17 ms**. The fetch is not the cost and never was; the 3.8 s page
in the old profile is the *presses*, and 78% of a board being "per-page fixed cost"
was 78% of a board being `SendKeys`.

### Two things this settles

* **PageDown is not honoured.** Two cycles, 140 presses, zero ranks, on a board
  that `{DOWN n}` was paging either side of the phase. Item 1 of "what is left"
  offered "45 keypresses would become one, cutting ~1.1 s" -- that saving does not
  exist, and the phase cost 57 s to prove it rather than another session of theory.
* **`SendKeys` has a floor of ~31 ms per key, and it is not the `Start-Sleep`.**
  One `SendWait("{DOWN 120}")` call, with no delay of ours at all, still took
  3.78 s: SendKeys paces its own synthetic input. So the 18 ms spacing that this
  repo treats as the proven-safe value is buying reliability at 63 ms/key when the
  same API delivers 31, and the ceiling of the whole approach is
  **50 rows x 31 ms = 1.55 s per page, i.e. ~1,900 ranks/min.**

That is the honest reading of the 2,000-2,800 ranks/min this plan estimated for
Phase 1: reachable, but only at the very floor of `SendKeys`, and not past it
without a different sender. The fetch will not be what stands in the way.

### The gaps were self-inflicted, and they are arithmetic

The first patched run collected 969 ranks with a maximum of 3,100 -- **2,131
missing**. Cause is in the CSV: every cycle pressed 120 rows while one harvest
reads an 80-record window, so ~70 rows of every advance scrolled past unread. The
advance must not exceed the read window. `-MaxPressPerCycle` is now 50, and the
cursor model is anchored on the loaded bottom each cycle (keys past the end of the
served list are eaten, so the true cursor can never be below it) -- without that
anchor the model drifted ahead and pressed three keys where fifty were needed,
which is exactly how the sustained phase fell to 48 ranks/min.

### A dead cycle costs 24-28 s, so it must be rationed

`{"harvest":1}` reads only the addresses already known, and the opening block can
be a stale one from the previous screen: run one read 39 rows that never grew, and
the whole `down18` phase scored 0. The fix is to spend one `{"want": N}` request on
a dead cycle, because that path searches for a *specific rank* and remembers where
it found it. But `find_rank_address` sweeps the whole process -- the CSV puts those
cycles at 24-28 s each -- so it is now only spent after two consecutive dead
cycles. Five such searches in seven cycles is the whole of the 48 ranks/min
collapse; the game was not stalling.

### Cold start of the resident reader, measured

On a *fresh game process* the cached profile's addresses are dead and the hinted
near-search fails, so `forza_row_server.py` falls through to full discovery:
**3 min 35 s** from task start to `reader ready` (14:27:16 -> 14:30:51). On the
next run against the same process it was ready in **6 s**. Per board that is
nothing (the sweep switches boards warm); per game launch it is a fixed cost that
belongs in the ready check, not in the scan's clock.

### The read window was worth a factor of two

Two runs, same board, same strategy (`burst`), advance capped at 50, differing only
in how many records the reader decodes per address:

| `--read-records` | ranks | cycles | dead | productive cycle | ranks/min | gaps in span |
| --- | --- | --- | --- | --- | --- | --- |
| 80 (the default) | 811 | 38 | 12 | 1,566 ms keys + 14 ms wait | 322 | ~44% |
| **240** | **1,714** | 52 | 12 | 1,571 ms keys + 22 ms wait | **659** | **15.8%** |

80 records is 44 KB, barely one 50-row page, so a page landing slightly off the
known address reads as nothing at all and the cycle pays the fetch timeout. Widening
the window costs 211 KB per address per poll -- irrelevant against a 1.6 s cycle --
and doubled the rate.

**The productive cycle is the number to keep:** 1.6 s for 40-49 ranks, i.e.
**~1,600 ranks/min**, against the 724/min the reference sweep took this same board
at. The 659/min headline is that rate diluted by 12 dead cycles which ate 91 s of
156 s. Dead cycles are the entire remaining gap, and they are an address-tracking
problem in the reader, not the game: pages alternate between heap arenas, the pager
keeps only the addresses it has been told about, and a page that lands elsewhere is
invisible until a relocation search finds it.

So the ranking is now unambiguous:

| | cost | worth |
| --- | --- | --- |
| dead cycles (12 of 52, 7.6 s each) | 58% of wall clock | 659 -> ~1,600/min |
| `SendKeys` at 31 ms/key | 1.55 s per page | ~1,600 -> the sender's ceiling |
| the fetch | 22 ms | nothing |

### Four attempts to kill the dead cycle, all refuted, and the method was wrong

Every dead cycle's `wait_ms` is exactly the fetch timeout, so a dead cycle costs
whatever that timeout is. Four changes were tried against it, each on a live board:

| run | change | ranks/min | dead / cycles | started at rank |
| --- | --- | --- | --- | --- |
| 4 | baseline: 240 records, advance 50, 6 s timeout | **659** | 12 / 52 | 4,352 |
| 5 | relocation search bounded to 3 s | 535 | 12 / 47 | 6,550 |
| 6 | + crossing margin allowed past the cap, 2 s timeout, cursor anchored on the resident bottom | 270 | 27 / 49 | 8,350 |
| 7 | + advance capped at 20 presses | 150 | 22 / 34 | 9,450 |

Read the last column before the third. **Each run started where the previous one
stopped, so every comparison is confounded with board depth**, and this plan already
records throughput drifting down with depth elsewhere. The monotonic 659 -> 150
decline cannot be attributed to the changes. What can be said:

* **The bounded relocation works as designed** and is kept: a cycle that would have
  paid a 24-28 s whole-process sweep now shows 10.4 s (6 s timeout + 3 s budget).
* **The other three are reverted** to the configuration that measured 659, with the
  reasoning left in the parameter comments so they are not re-tried blind.
* The evidence that made 20 presses look right is real and still unexplained:
  individual cycles delivered **49 ranks off 20 presses** (0.63 s, i.e. 4,600
  ranks/min for that cycle) while cycles pressing 56 delivered 13 and then nothing.
  A page does not cost 50 presses. Whatever governs when the game serves is not
  "cursor crosses the loaded bottom" alone.

**The method has to change before any more of this.** A/B tuning of the paging loop
needs each variant to enter the board fresh at rank 1 -- four minutes of navigation
per variant -- or depth swamps the effect being measured. Comparing variants by
letting them continue each other, which is what happened here, produces a clean
looking table of numbers that mean nothing.

### What this reorders in "what is left"

1. **Find out what actually makes the game serve a page.** This is now upstream of
   everything else: a cycle that presses 20 can deliver 49 ranks in 0.63 s, and one
   that presses 56 can deliver nothing, so the dead cycle is not a timeout to tune
   but a rule we have not identified. Instrument the cursor directly -- the
   differential read this plan already proposes (press ten, take the block that
   MOVED) -- instead of inferring position from what arrives.
2. **Kill the dead cycle**, once (1) says what causes it. `find_rank_address` now
   takes a `deadline_seconds` and the row server bounds a relocation to 3 s (every
   fourth consecutive miss still pays a full sweep, or a board whose arena really
   moved would never be found again); `--read-records` defaults to 240. Keeping more
   addresses per board, so relocation is rarely needed, is still unwritten.
3. **A sender that is not `SendKeys`.** After that it is the only thing between the
   loop and ~2,000/min. The Phase 1 attempt failed here (`SendInput` from a guest
   scheduled task moved nothing), but that experiment confounded two variables:
   it sent from a *task* AND with `SendInput`. `forza_burst_scan.ps1` proves the
   console-session PowerShell process drives the game; trying `SendInput`,
   `keybd_event` and key auto-repeat *from that same process* separates them.
4. **Held keys.** Auto-repeat is the game's own scroll acceleration and costs one
   press. Never tested, and now doubly interesting: if 20 presses can deliver a
   page, the game's own scroll acceleration may already be doing this.
5. The persistent reader and the removed settle are already banked, and item 1 of
   the old list (PageDown) is dead.

## The Dead Cycle Was Our Own Gamertag Filter (2026-08-22, operator)

Every hypothesis in the section above assumed the dead cycle meant the game had not
served the page. The operator, who has watched the screen while these runs scroll,
says otherwise: scrolling never stutters on the important data, and **only the
player names populate late.**

`Pager.read_rows` ended with:

```python
rows.extend(row for row in decoded if row.get("gamertag"))
```

So a page the game HAD served -- correct ranks, correct lap times, names not yet
filled in -- was discarded in full, and the loop saw "no new rows": a dead cycle,
priced at whatever the fetch timeout was set to. This is why the old loop never met
the bug and every attempt to speed the loop up did: at 4 s per page the names have
arrived by the time anything reads, and **the failure rate scales with read speed.**
It also explains why the failure looked like a serving limit that got worse the
faster we went, and why four plausible fixes to the paging arithmetic all failed --
they were aimed at the wrong mechanism entirely.

The same filter exists in three places, and they are not equally safe:

| site | behaviour | verdict |
| --- | --- | --- |
| `Pager.read_rows` (`forza_fast_scan.py`) | dropped nameless rows unconditionally | **fixed**: `require_name=False`, and `forza_row_server.py` passes it |
| `capture_scoreboard_memory.py:149` | drops unless `--allow-missing-gamertag` | safe only while the loop stays at ~4 s per page; any faster loop must pass the flag |
| `forza_scoreboard_scan.py:357` | unconditional | left alone: this is the discovery path, where a decoded name is a validity signal |

A nameless row is now validated structurally instead -- a lap time between one
second and a day, the same discriminator `find_rank_address` uses -- so a garbage
block still cannot pass as a page.

`forza_burst_scan.ps1` treats such a row as **progress but not output**: it advances
the rank model, keeps the row in a pending table, and writes it once the name lands
(normally within a page or two). Whatever is still nameless at the end is written
out flagged `gamertag_pending`, because a rank held back forever is a rank lost.
The summary now reports `named_rows` and `nameless_rows` separately.

**This correction is written, not measured.** The VM is off at the operator's
request. The test is one `-Strategies burst` run on a board entered fresh at rank 1,
and it is worth doing before anything else on this list: if the dead cycle was
entirely this, the 659 ranks/min figure was 12 of 52 cycles being thrown away, and
the productive-cycle rate of ~1,600/min is what the loop should now hold.

### What this corrects in the record

"The gamertag flag is inert -- 0 of 6,550 rows lacked a name" is true at 4 s per
page and false as a general claim. The flag is not inert; it is speed-dependent, and
it was silently deciding the throughput of every fast-reader experiment since
2026-08-21.

## 22% Of Every Board Is Invalid Laps, And The Scan Can Stop There (2026-08-22)

The operator, reading the analytics output: the faster times that appear at the
bottom of the ranks are invalid laps and can be deleted. The data says something
sharper than that. A board is **two sorted lists**: every valid lap by time, then
every invalid lap by time, restarting from a faster time than the valid block ended
on. Highway Circuit A:

| ranks | first lap | last lap | `is_clean` |
| --- | --- | --- | --- |
| 1 - 14,918 | 88.302 s | 701.25 s | true |
| 14,919 - 29,697 | 75.35 s | 397.66 s | **false** |

Checked across all 40 sweep runs: **not one board has a valid lap after its first
invalid one**, and `is_clean` accounts for **100%** of the ordering violations --
70,114 rows, 22.3% of the 314,487 scanned. Among valid laps alone the boards are
monotone to 0.005% (12 rows of 244,373, all 10 ms apart inside a single resume pass,
i.e. the board shifted slightly between passes).

Two consequences.

**The scan stops at the boundary.** `run_memory_leaderboard_scan.ps1` ends the board
on the first row with `is_clean` false and reports the new status
`valid_section_complete`, which `run_full_sweep.ps1` banks as complete. That is worth
roughly a fifth of the paging on every board that has a tail -- Shirakawa S1 alone
was 13,396 rows of it -- and it removes a false-truncation trap: the scrollbar sits
mid-bar at that point, so the old logic would have called a finished board
TRUNCATED. `-ScanInvalidLaps` pages through the tail anyway.

The cut checks its own premise rather than trusting this measurement forever: if a
page ever shows a valid lap ranked BELOW the first invalid one, it logs that the
board does not separate cleanly and keeps paging. A silent 22% data loss is a much
worse failure than a slow board.

**An earlier filter was solving the wrong problem.** The analytics build had a
median-of-neighbours outlier filter, added the same day to explain a 75.355 s lap
sitting at rank 3,244. It deleted 132 legitimate rows and kept all 70,114 invalid
ones. Replaced by the flag, which needs no tolerance and no window.

## The Row Buffer Is A Pool, Not A Page (2026-08-22, evening)

Reading the raw structure at the profile's addresses, while the list scrolled, ends
several months of wrong assumptions at once:

```
0x1ef58f24078: 115 rank-pair offsets, row stride 0x370 (880) as expected
    off 0x00000 rank 10838  lap 105.772
    off 0x00370 rank 10801  lap 105.747
    off 0x006e0 rank 10901  lap 105.811
    off 0x00a50 rank 10821  lap 105.764
    off 0x00dc0 rank 10809  lap 105.754
```

The rows sit one stride apart, but **their ranks are neither consecutive nor
ordered** -- they are all from the same neighbourhood in arbitrary order. This is not
a page of a sorted array. It is a **pool of recycled row slots** belonging to a
virtualised list: each slot holds whichever rank it currently displays, and the pool
is rewritten as the view moves.

That single fact explains a pile of separate mysteries:

| observed | the pool explains it as |
| --- | --- |
| the block address is stable for 20+ minutes while its "first rank" jumps 914 → 4348 → 7764 | the buffer never moves; its contents rotate |
| a read "finds no row block for rank N" and costs 60-100 s | rank N simply is not in the pool at that instant, and searching 6 GB cannot conjure it |
| flow mode loses 26% of ranks | the pool is overwritten faster than it is polled |
| pages seem to "alternate between heap arenas" | several pools exist; which one holds the wanted rank varies |
| `0` slots in the whole process point at the block | nothing points at the first ROW; the pool is reached as an element of something larger |

### What follows, in order of value

1. **Sample rate, not search.** If rows are lost because the pool turns over between
   polls, the fix is to poll more often per row scrolled -- not to search harder for
   a rank that is no longer resident. Measurable directly: same key rate, smaller
   stride between harvests.
2. **The waiting loop was solving a non-problem.** `run_memory_leaderboard_scan.ps1`
   waits for a *specific rank* to appear and pays the whole exact/near/sweep ladder
   when it does not. On a pool, that rank may never appear at that moment however
   long one waits, which is exactly the 8-dead-reads-then-truncated signature. Flow
   mode (harvest whatever is resident, never wait) measured **1,394 ranks/min against
   25/min for the waiting loop on the same board**.
3. **The pointer chain is worth less than it looked.** Its promise was to stop the
   search; the pool says most searching was avoidable anyway. It keeps one real use:
   finding the pool after a game restart, which currently costs a 3.5-minute
   discovery sweep.

### On the pointer hunt itself

`scripts/find_row_pointer_chain.py` found **no slot anywhere in the process holding
the block address**, and widening to a 16 KB window before the first row produced 559
nearby pointers whose distances did not cluster -- no allocation base. A first pass
with a weak validator (count of rank-pair patterns) reported 40 "tracking" slots;
they were noise -- their blocks had first ranks like 6,648,929, and phase 4 found
nothing pointing at any of them. Two equal consecutive u32 occur all over a busy
heap, and one is embedded in every row at +0xc4 (value 0x10001), so even a real block
yields false offsets.

## The Screen Route, Built And Working (2026-08-22, evening)

The operator pushed for it after the memory sampling kept losing rows, and the
argument that settled it was theirs: the analysis is a separate process from the
capture, so it cannot slow the capture down -- which is doubly true here, because the
capture runs in the guest and the OCR runs on the host.

### The pipeline

| stage | what runs | measured |
| --- | --- | --- |
| scroll | `press_down_continuously.ps1`, one process that only presses | ~116 rows/s |
| capture | `capture_leaderboard_frames.ps1`, a separate process, table region only | 18.5-19.1 fps |
| read | `ocr_leaderboard_frames.py` on the host, 12 workers | offline, game shut down |

The frame budget works out: the table shows 11 rows, so 11 frames/s already covers
116 rows/s, and 19 fps leaves overlap for a dropped frame.

### What the screen gives, and what it cannot

Read and verified against the pixels: **rank** (with its thousands separator),
**lap time**, **car name**, **drivetrain**, **gearbox** (A / M / MC), and **ABS / TCS /
STM**. The assists are not OCR'd at all -- a filled marker is dark in the middle of its
ring and a hollow one is not, so it is a threshold on an 18x18 crop. Verified row by
row against the screenshot.

**Corrected the same evening: `is_clean` IS on the screen.** The operator pointed at the
yellow-orange "!" beside the lap time, and it reads by exactly the method the assist
circles use. Measured position: screen x 1370-1399, 110 yellow hits against a baseline
of 20 from the lime row highlight, which is why only the middle third of the row height
is sampled -- the highlight is a border, the marker is a glyph.

Validated in both directions against the memory scan, which carries the flag exactly:

| control | rows | agreement |
| --- | --- | --- |
| S1 board, ranks 1-3,036 (all valid) | 3,036 | no false positives |
| lap times present in both readings | 2,211 | **2,211 / 2,211** |
| Daikoku D, the invalid tail | 11 | all detected invalid |

So what the screen cannot give is down to `xuid` and the submitted timestamp.

### Three things that had to be got right

* **Binarise per ROW, not per strip.** The table alternates light and dark row
  backgrounds, and one global Otsu threshold over the whole strip left exactly one
  readable line per frame. Per-row thresholding, then the rows stacked back into one
  image, keeps it to a single OCR call.
* **One tesseract process per BATCH.** `tesseract list.txt stdout` reads every path in
  a list file and separates pages with a form feed. Per-cell calls would have been
  33 per frame at ~25 ms of process start each; this is one call per 30-40 frames.
* **Mask everything else.** The operator supplied a template: rank, car, drivetrain,
  and the time-through-gear block, nothing else. The driver column is the one that
  ruins OCR -- gamertags carry emoji and platform icons that produce stray glyphs and
  shift the line -- and it is also the column nothing downstream needs.

### The failure that self-validation could not catch

The first capture started at screen x=110 and clipped the leading digit off the rank:
`4,287` read as `287`. Every row in the frame was clipped identically, so the
consecutive-rank check confirmed the wrong numbering -- eleven rows agreeing on an
offset of exactly -4,000. Nothing inside a frame can detect that. Fixed by capturing
from x=78 and by keeping the column map in SCREEN coordinates with the capture offset
subtracted at runtime, so moving the window cannot silently shift the columns again.

Rank is now parsed as optional, and a frame's numbering comes from whichever ranks
read cleanly: rows in a frame are consecutive, so one good rank fixes all eleven, and
a clipped one can only fail to contribute rather than invent a value.

## Naming The Cars Without A Catalogue (2026-08-22, late)

The in-game catalogue dump stalled at 47 of 630 carIds and kept returning the same 270
entries whatever board was open. The operator's suggestion replaced it outright: take
the public list at `forza.net/fh6cars` (636 cars, a static table: make, model, year,
country, category, stock class and PI) and join it to what the screen already reads.

The join key is the **lap time**, not the rank. A memory scan days older than a capture
still joins, because a posted time belongs to its entry permanently while ranks shift
whenever anyone posts a faster lap. A time held by two different carIds in memory names
neither, and per carId a majority vote over its matched times decides.

Seven captures of ~100 s, one per class on Highway Circuit:

| class | ids named this run | running total |
| --- | --- | --- |
| D | 48 | 48 |
| C | 74 | 101 |
| B | 81 | 149 |
| A | 58 | 188 |
| S2 | 45 | 232 |
| R | 28 | 241 |
| S1 | 66 | **279** (302 including the codename fallback) |

Each class adds 40-50 new ids because each fields different cars, so coverage is built
run by run rather than in one long pass.

### The year is a constraint, not a decoration

The first pass looked like a success and was partly wrong. The screen abbreviates the
model and prints a two-digit year -- "Honda Civic '97" for a Civic Type R -- and a fuzzy
matcher that treats the year as character noise pulled four different Hondas onto the
single roster entry "1986 Honda Civic Si". Eight carIds ended up sharing that one name,
each with UNANIMOUS votes for the wrong car, which is precisely the failure mode that
looks like confidence.

Two fixes, both necessary:

* **Match only within the read year**, and return nothing when nothing there fits. A
  missing name costs a filter row; a wrong name corrupts an analysis.
* **Store the year in the id->name file.** The roster holds six cars called "Honda Civic
  Type R", so a name-only lookup silently returned whichever came last -- which is how
  the 1997 car briefly displayed as '23.

Afterwards the seven Civics separate correctly: Si '86 (D 253), CRX Mugen '84 (D 390),
Type R '97 (C 430), '04 (C 480), '07 (C 454), FD2 '08 (B 514), '23 (A 620). Display
names carry the year the way the game writes it, and duplicate display names fell from
19 to 9 of 302.

### What the site gained

Filters for make (58), country (10), category (30), a two-handle model-year range
(1955-2025), and **tune**: stock class against board class gives detuned / stock class /
tuned up. The board's performance class remains the only class a lap is scored in --
tune decides which cars are listed, never how many points first place was worth.

## The Screen Route As The Sweep (2026-08-23, overnight)

Running the numbers against each other rather than against intuition decided the
scanner for an unattended night:

| route | fresh board, from rank 1 | coverage |
| --- | --- | --- |
| memory loop (waits for each page) | 724/min | gapless |
| flow scanner (memory, never waits) | 285/min | 80% missed |
| **screen capture + OCR** | **~1,300/min** | **99.9% of the body** |

The flow scanner's 1,426/min only ever applied to a board already served; a sweep opens
nothing but fresh ones. Live results the first night: Highway D **7,858 rows in 11.2 min**
against 146 minutes for the same board in August, and Hokubu D **149 -> 4,476 rows in
7.4 min**.

`scripts/ocr_board_sweep.py` runs it: navigate, capture in 120 s chunks, OCR each chunk
on the host and delete its frames before the next (disk stays near 350 MB instead of
growing into gigabytes), stop when a chunk adds no new ranks or turns invalid, write the
three files the analytics build already reads, rebuild the site, next board.

### Three things it needed before it could be left alone

* **Guest recovery.** Forza at ~660 MB with no window is a shell; navigation then OCRs
  the desktop and every board is "unreachable" inside a minute. Two consecutive
  navigation failures now trigger the VM reset and a retry of the same board. Proven
  live at 03:58: reset, then the board opened.
* **The track name must come from the screen.** Filing boards under the navigation
  ANCHOR put Hokubu's rows into Highway Circuit's board -- route index 4 counted from
  "Highway Circuit" is Hokubu. The navigator prints what it read; that is the track, and
  a board whose name cannot be read is skipped rather than guessed.
* **Never `-FreshStart` from a healthy game.** It kills and relaunches, and relaunching
  in quick succession is what breaks the Steam launch -- this runner manufactured the
  very wedged state it then reset the VM to clear, twice, before the flag came out.

### Where the missing ranks actually are

The headline coverage figure of ~93% is misleading and worth stating properly. On
Hokubu D, 260 ranks of 4,782 were missing -- but 256 of them sit in four blocks at the
very END of the board (one of 197, then 39, 11 and 9, all past rank 4,336), and the
body from 1 to 4,300 is missing exactly **four** ranks. That is 99.9% of the useful
data; the weakness is the last few hundred ranks, where the scroll runs out and the
invalid tail begins.

A second pass over the same board is therefore close to worthless -- measured: 93.6% to
94.6%, 46 new ranks -- because the gaps are systematic rather than random. The fix is
not to re-scan the board but to finish its tail: one slow pass over the last ~500 ranks
once the scroll stops advancing.

### Rows the screen cannot give

`xuid` and the submitted timestamp. Everything else the analytics uses is there,
including `is_clean` and the car NAME -- which the memory path cannot resolve at all.
OCR rows carry no `car_id`, so the dataset build maps their names onto known ids and
gives the rest synthetic negative ids with their roster entry attached; without that the
whole night would have been dropped for lack of an id.

## Two Defects The Night Exposed In The Screen Route (2026-08-23)

### The scroll outruns what the game draws

Hokubu S1, chunk by chunk: the first four advanced 1,200-2,900 ranks each and were
captured almost completely; chunks 5-12 advanced 3,000-4,500 each and yielded ~100 rows
apiece. The capture is not the limit -- 19 fps x 11 rows is ~200 rows/s of capacity
against ~24 rows/s in the good chunks. Under continuous input the list ACCELERATES until
it pages in jumps of thousands, and the ranks it jumps over are never rendered, so no
capture rate can recover them. That is also why a second pass over a board is worth
almost nothing (93.6% -> 94.6%): the same ranks are invisible again.

Mitigated live by pressing in bursts of 20 with a 160 ms pause instead of 60 continuous
(`press_down_continuously.ps1`). The next board, Hokubu R, came back at **96% coverage**,
the best of the night, and the chunk-by-chunk decay largely disappeared.

### A placeholder lap time, and why the obvious filter is wrong

Several boards end in hundreds of CONSECUTIVE ranks carrying one identical lap time --
00:54.000 on Hokubu S1, 00:34.850 on Shimanoyama A -- with real car names and sane
ranks. It is the gamertag lag again in another column: at speed the row is drawn before
its time arrives, and a placeholder is captured instead.

The tempting filter -- drop times that repeat many times -- would destroy real data. The
memory scans settle it: Hokubu S1's largest genuine tie is **13** rows, but Shimanoyama
D has 117 times shared by 25+ entries and one shared by **266**. On a deep board, ties
at millisecond precision are ordinary.

What separates them is the board's own ordering. Rank 10,000 on Hokubu S1 read 54.000 s
while rank 1 is slower than that, and on a list sorted by time that is impossible. So
the filter to build is monotonicity against the running maximum, exactly the rule that
`is_clean` turned out to encode on the memory side -- not a rule about repetition.

Roughly 4.7% of the night's OCR rows (5,502 of 117,261) sit in repeated-time runs; how
many of those are placeholders rather than real ties is what the monotonicity check will
say.

## Which Route Is Actually Faster (2026-08-23)

Both routes have now run over the same boards, so this is measured rather than argued.

| | throughput | completeness |
|---|---|---|
| memory scanner | 600 ranks/min median over 18 boards, max 731 | 100%, including Narai-Juku A at 50,850 rows |
| OCR loop | 400-611 ranks/min end to end | 90-96%, and nothing past rank ~10,120 |

The screen route was adopted on the expectation that it would reach ~1,900 ranks/min.
End to end -- navigation, capture, OCR, write -- it does not beat the scanner it was
meant to replace, and it hands back a board with holes in it.

But that is not an argument for dropping it, because the two fail in different places.
Every memory board that reads 149 or 99 rows comes from one session on 2026-08-21 that
died after its first board; the OCR route read those same boards at 4,700-10,100 ranks
two nights later. Where the memory route works it is strictly better; where it collapses
the screen route still returns 90%+ of a board. Keep both, and pick per board on
evidence rather than on principle.

One consequence for reading the data: `rows_collected == maximum_rank` does NOT mean a
board is complete. The thirteen dead boards all report 149 of 149 and look perfect.
Coverage has to be judged against the board's true length, which -- until a scan reaches
the end -- only the other route knows.

## What The Screen Route Costs By Board Depth (2026-08-23)

Twenty-four OCR boards, coverage against the board's own highest rank, after the
straggler rule was corrected (the first version of this table read 94.8% for the middle
band because phantom ranks had inflated the spans it was measured against):

| board length | boards | median coverage |
|---|---|---|
| up to 4,000 | 3 | **99.4%** |
| 4,000-6,000 | 11 | **98.6%** |
| 6,000-9,000 | 3 | 89.4% |
| over 9,000 | 7 | 88.3% |

Two things fall out of this. Anything up to ~6,000 ranks comes back essentially
complete -- Soni C and D, 3,509 and 3,469 ranks, lost 16 and 22 rows -- so the screen
route is not lossy in general, it is lossy past a depth. And beyond that depth the loss
does not keep growing: it settles near 88-89% rather than degrading toward nothing,
which is also why a second pass over a deep board only moves 93.6% to 94.6%. Whatever
the mechanism is, it costs a roughly fixed fraction once the board is past ~6,000 ranks
rather than compounding.

That makes the burst pacing worth re-testing on a DEEP board specifically, and only
there: on short boards there is nothing left to win, and measuring a pacing change on a
board of a different length confounds the pacing with the depth effect above.
