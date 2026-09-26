# Leaderboard Protocol Research

This page records verified findings about replacing screenshot/OCR collection with direct leaderboard response parsing.

## Confirmed HTTP Call

FH6 sends Rivals leaderboard page requests to:

```text
POST https://gameservices.fh6.forzamotorsport.net/Services/o.xtsw
Content-Type: bin/xtsw
X-ClassName: Forza.WebServices.Scoreboard, Forza.WebServices
X-Method: GetRows
```

The transport uses WinHTTP/Schannel. `InternetClient` ETW exposes the HTTP request and response bodies before/after TLS, so TLS decryption keys and a man-in-the-middle proxy are not required.

## Confirmed Body Shape

Observed `GetRows` requests:

- The first request after creating a scoreboard context can be 4,128 bytes.
- Subsequent page requests are exactly 544 bytes.
- Response body: normally about 18.7-19.2 KB.
- All observed request and response sizes are multiples of 16 bytes.
- Request bodies share one identical initial 16-byte block within the observed game session.
- All later request blocks change as the requested leaderboard range changes.
- Response entropy is approximately 7.99 bits per byte.
- No repeated 16-byte ciphertext blocks were found inside the sampled bodies.

These measurements prove that the body at the HTTP boundary is not plain serialized leaderboard data. They strongly suggest an application-level protected or compressed-and-encrypted representation. They do not yet prove the exact cipher or mode.


## 2026-08-18: Reflection Tables Recovered (supersedes the pagination section below)

The game patched to Steam build `24584928` / file version `6.420.696.0` on
2026-08-18, invalidating every RVA recorded on `6.382.893.0`. Re-analysis on the
new build produced better answers than the old ones, and corrected two of them.

### The executable is packed, which is why static analysis kept failing

Comparing the runtime image against the shipped file, page by page
(`analyze_runtime_image_diff.py`): **18,468 of ~25,400 executable pages differ
and decrypt only in memory** -- roughly 72 MB of code. A sample page measured
7.815 bits/byte on disk against 6.507 in memory, where the memory copy
disassembles as ordinary MSVC code.

Every `.asm` file in `data/network_probes/static_analysis/` covering the Xls,
crypto, and scoreboard RVAs is therefore disassembled ciphertext, which is the
real reason those regions "did not reliably disassemble". Work from a runtime
dump (`dump_forza_runtime_image.py`), never from the shipped file.

### The service data model is readable as data

The binary carries a reflection table per serialised type. A field descriptor is
0x60 bytes: type code at +0x00, name pointer at +0x08, field offset at +0x10,
validation bounds through +0x38, size at +0x40. A type header descriptor has the
sentinel `0x7fffffff` as its offset word, its field array pointer at +0x40, field
count at +0x48, and type name at +0x58. **The header does not sit adjacent to its
fields**; it points at them, and assuming adjacency mislabels every table.

`dump_forza_type_descriptors.py` recovers **860 types** from the runtime image in
0.25 s, with no disassembly and no hooking.

### Pagination is random-access, not cursor-only

The previous conclusion in this document -- that `maxSize` was the only visible
parameter, that no `startAt` existed for the scoreboard, and that pagination was
therefore cursor/state based -- **is wrong**. It was drawn from string searching
the packed image.

The Scoreboard service's string pool and descriptor tables show two distinct
parameter shapes:

```text
{ scoreId, maxSize }                                        <- the cursor form
{ leaderboardId, gameScoreboardId, view, startAt, maxResults }
```

and a response carrying `rows`, `userRow`, and `totalRowCount`. Registered
scoreboard methods include `GetRows` and `GetRowsWithTotalCount`, alongside
`GetRival`, `GetRivalsEverywhere`, `GetBestScore`, `SubmitScore`,
`SubmitScoreWithGhost`, `SubmitScoreWithThumbnail`, `ReportScore`,
`NotifyRival`, and `DownloadFile`.

This matters more than any speed tuning:

- `startAt` + `maxResults` means **arbitrary rank offsets**, so no scrolling and
  no sequential cursor walk, and disjoint ranges can be fetched concurrently.
- `leaderboardId` + `view` means **board selection is a request parameter**, so a
  full-catalogue sweep needs no menu navigation.
- `totalRowCount` replaces the OCR player-total estimate outright.

Which shape `GetRows` itself takes is not yet settled; `{scoreId, maxSize}` is the
likelier fit for the 544-byte requests already captured, with the
`startAt`/`maxResults` shape belonging to `GetRowsWithTotalCount` or a related
method. Confirming that is the next step, and it is now a question about one
method rather than about the whole protocol.

### Corrected in-memory row layout

`ScoreboardScoreData` is 716 bytes of fields padded to 720 (`0x2d0`), matching
the stride measured from captures. Descriptor offsets map onto the in-memory
record at a constant `+64` from `position` onward. Verified against 300 captured
rows from the June build:

| memory offset | type | field | previously |
| --- | --- | --- | --- |
| 72 / 76 | int32 x2 | `position`, `rank` | read as one "duplicated rank pair" |
| 88 | uint64 | xuid (inside the 304-byte `user` object at 80) | correct |
| 96 | string | gamertag | correct |
| 384 | float64 | `score`, the lap time | correct |
| 392 | systemtime | `submittedTime` | correct |
| 408 | uint16 | `carId` | **not extracted** |
| 410 | uint8 | `carClassId` | **not extracted** |
| 412 | float32 | `carPerformanceIndex` | **not extracted** |
| 416 | int32 | `carDriveTypeId` | **mislabelled `car_class_id`** |
| 420 | int32 | `carPowertrainId` | **mislabelled `car_drivetrain_id`** |
| 424-433 | bool x10 | assists, `isClean`, `hasGhostFile` | correct, names and order confirmed |

So the CSV/Parquet exports under `data/memory_scans/` have two mislabelled
columns. The assist flags, which looked suspect because bytes 427/428/431 are
constant 0 and 432/433 constant 1 across 300 rows, are in fact correctly named
and ordered.

`carClassId` decodes to 6 across all 300 rows of the R-class capture, and the
class ids run D, C, B, A, S1, S2, R, X -- an independent confirmation that the
mapping is right.

One field still needs calibration: `carPerformanceIndex` is a float32 reading
0.848-0.886, not the 901-998 PI the UI shows. It is exported as
`car_performance_index_raw` until it can be checked against on-screen values.

## Pagination Model

The current executable's service-registration metadata lists `maxSize` as the visible parameter for `Scoreboard.GetRows`. It does not list a `startAt` parameter for that method. The only `startAt` string in this build belongs to `ForzaGiftingGetGiftsForUser_Parameters`.

This strongly suggests that leaderboard pagination is cursor/state based:

1. A larger request creates or restores the scoreboard context.
2. Repeated `GetRows(maxSize)` calls request the following rows from that context.
3. The game prefetches several pages while the selected row approaches unloaded data.

The observed response sizes and request frequency are consistent with roughly 50 rows per page, but that page size remains an inference until the protected response is decoded.

## Confirmed Game Types

Static strings and MSVC type information in the current FH6 executable identify:

```text
Xls::Scoreboard::GetRows
Xls::ScoreboardRow
Xls::ScoreboardScore
Xls::ScoreboardScoreData
Xls::ScoreboardRival
```

The executable contains field names needed by the dataset, including:

```text
rank
submittedTime
carClassId
carPerformanceIndex
carDriveTypeId
carPowertrainId
usedSTM
usedTCS
usedABS
usedFrictionAssist
usedAutoBrake
usedAutoShifting
usedClutch
usedSuperEasyAssist
isClean
hasGhostFile
tuneCreator
scoreId
garageId
carPower
carTorque
carWeight
usedDrafting
usedMulligan
usedFullSuggestedLine
usedBrakingSuggestedLine
hadPenalty
numberOfCarsPassed
totalRowCount
```

This confirms that the service response contains substantially richer and cleaner data than OCR can recover.

## Crypto Evidence

The executable statically contains Botan 3.9 and support strings for AES modes, including CBC, GCM, CCM, OCB, and SIV. It also contains the `Xls` transport strings `bin/xtsw`, `X-Encryption`, and `X-Validation`.

This is evidence that the game has the required application-level cryptography available. It is not sufficient to claim that `GetRows` specifically uses one named mode. The next analysis must locate the `Xls` encode/decode call path or capture data immediately before that transform.

## Runtime Hook Evidence

Frida 17.15.3 is installed in the VM under:

```text
C:\ForzaTools\Python312
```

Reusable runtime tracers:

```text
scripts/trace_vm_forza_protocol_startup.ps1
scripts/trace_forza_winhttp.py
scripts/trace_forza_rva_hooks.py
scripts/trace_memory_watchpoint.py
scripts/analyze_winhttp_runtime_trace.py
```

The validated startup WinHTTP trace is:

```text
data\network_probes\runtime_hooks\startup_winhttp_20260623_181746_358.jsonl
data\network_probes\runtime_hooks\startup_winhttp_20260623_181746_358.summary.json
data\network_probes\runtime_hooks\startup_winhttp_20260623_181746_358.summary.csv
```

The raw JSONL is sensitive because it can contain live authorization/session headers. Use the sanitized summary for normal analysis.

Confirmed from the runtime trace:

- `Forza.WebServices.User.Authenticate`: `bin/xtsw`, 592-byte request body.
- `Forza.WebServices.User.ProfileLogon`: `bin/xtsw`, 608-byte request body.
- `Forza.WebServices.Scoreboard.GetRows`: `bin/xtsw`, 544-byte request body.
- The `GetRows` call stack repeatedly crosses these Forza RVAs: `0x29b7890`, `0x29b77a9`, `0x17cb490`, `0x338f3b0`, `0x338f364`, `0x425431`.
- A targeted hook on the `0x17cb410` wrapper exposed deeper Xls/libHttpClient bridge activity and frequent caller RVAs including `0x5983f72`, `0x5e323b0`, `0x5e324ba`, `0x599a32b`, `0x5999c69`, and `0x5997edb`.

The static copy of `forzahorizon6.exe` does not reliably disassemble all of those deeper functions; several regions appear transformed or thunk-like in the file image. Runtime hooks are therefore the preferred path for narrowing the `Xls` encode/decode boundary.

The validated post-decrypt boundary is a contiguous `ScoreboardRow` array in
process memory. A visible leaderboard page produced row records with fixed
`0x2d0` stride. Confirmed fields include rank at `+0x48`, XUID at `+0x58`,
gamertag at `+0x60`, lap time double at `+0x180`, submitted time at `+0x188`,
class id at `+0x1a0`, drivetrain/powertrain id at `+0x1a4`, and
assist/clean/ghost flags at `+0x1a8`.

`scripts/capture_scoreboard_memory.py` reads these records without injecting
code into Forza. `scripts/run_memory_leaderboard_scan.ps1` advances the selected
rank only far enough to trigger the next `GetRows` prefetch, and
`scripts/merge_scoreboard_memory_chunks.py` deduplicates and exports the
captured rows. The host entrypoint
`scripts/start_vm_memory_leaderboard_scan.ps1` also performs automatic menu,
category, track, and performance-class navigation.

An offline validation dump decoded 19 contiguous rows from ranks 5700 through
5718 with no missing ranks and successfully wrote Parquet. This is now the
preferred structured extraction path.


### What `carPerformanceIndex` actually holds (unresolved, but narrowed)

The UI shows PI `998` for the top R-class rows, while the float32 at the offset
the descriptor order assigns to `carPerformanceIndex` reads ~0.886. Checks against
the 300 captured June rows:

- The integer `998` appears **nowhere** in the 720-byte record as a u16, and no
  u16 column sits entirely inside the R-class PI band 901-998.
- No float32 or float64 column holds values in 900-999 either.
- So the displayed PI is **not stored as a number in this record**.

The float is tune-sensitive, not merely a car property: `carId` 4210 carries three
distinct values and 1335 carries two, which is the behaviour PI has (tuning
changes it) and which a fixed per-car rating would not have.

Multiplying by ~1126 lands the values on 997.4-997.9 for cars the UI labels 998,
and 955.5 / 995.4 for two others. That is suggestive but **not confirmed**: the
factor was fitted to make 998 appear, which is circular. Confirming it needs one
sample where the same rows are both visible on screen (PI column readable) and
captured in memory; the scan that produced ranks 40-50 did not overlap the frame
showing ranks 2-11.

Until then the field is exported as `car_performance_index_raw` rather than as a
PI, so no consumer mistakes an unvalidated scalar for the real number.

## Artifacts

Packet and HTTP-boundary captures are stored under:

```text
data\network_probes\
```

The raw ETL/XML files may contain live account/session headers and must stay private. Use the sanitized reports under each probe's `winhttp_analysis` folder.

The copied executable used for offline static analysis is:

```text
data\network_probes\static_analysis\forzahorizon6.exe
```

It is excluded from Git and should not be redistributed.

Optional offline PE/disassembly dependencies:

```powershell
python -m pip install -r .\requirements-research.txt
```

Microsoft Sysinternals Strings is installed inside the VM at:

```text
C:\ForzaTools\SysinternalsStrings\strings64.exe
```

Reusable static-analysis helpers:

```text
scripts/find_pe_string_xrefs.py
scripts/inspect_pe_functions.py
```

## 2026-08-19: Live Recon Of The Decrypt/Request Boundary

Ran the runtime hooks against the live process on build 6.420.696.0 and settled
several things the static analysis could not.

### What is plaintext and what is not

`trace_forza_response_dispatch.py` captured complete response bodies at the
libHttpClient boundary while the leaderboard paged:

- **Profile service responses are plaintext JSON** -- `{"profileUsers":[{"id":
  "2535400000000001",...,"AppDisplayName":"SomePlayer"}...]}`. XUID->gamertag is
  readable off the wire.
- **Scoreboard responses are encrypted.** The 50-row page is ~18.8-19.2 KB,
  exactly 16-byte aligned (1179 blocks), entropy ~8.0, no plaintext envelope and
  no prepended IV. Consistent with an AES block mode whose IV is session-derived.
- Every body arrives through one game-side caller: `forzahorizon6.exe+0x5ecf144`.
- `trace_forza_encryption_session.py` fired precisely on the ~19 KB body and
  exercised the decrypt code region at `rva 0x5eea000`.

### The request is encrypted AND integrity-protected

The captured `GetRows` request (from the June WinHTTP ETL) is
`Content-Type: bin/xtsw`, 544-byte body, and carries an **`X-Validation`** header
(e.g. `937a337fc7aa4a1b802acae316891483`) alongside `X-Method: GetRows`,
`X-ContractVersion: 23`, an `X-IdempotencyId`, and an Xbox Live `Authorization`.

Consequence: **a captured request cannot be edited and replayed** to fetch a
different rank range -- the body is encrypted and the edit would fail
`X-Validation`. `replay_captured_getrows.py` can only replay a request verbatim,
which re-fetches the same range.

### The reframing that matters

Structured extraction does **not** need the response decrypted: the game already
decrypts each page into the contiguous `ScoreboardRow` array, and
`capture_scoreboard_memory.py` reads that plaintext today. The only thing gating
throughput is *triggering* the fetch, which is currently done by scrolling the UI
(~750 ranks/min ceiling).

So the prize is request control, not decryption. And because both the request
body and `X-Validation` are produced inside the game, the sole viable route to
arbitrary `startAt` is an **in-process hook on the request builder before
encryption**: rewrite `startAt`/`maxResults` in the plaintext request struct and
let the game encrypt and validate it. That is a multi-session RE effort -- find
the struct-populating function on the request path (mirror of the `+0x5ecf144`
response path), confirm the field offsets, hook and modify -- with no guarantee
of success, but it is the only thing that removes the UI ceiling for boards far
larger than the 5.5k-13k depths measured so far.

## 2026-08-19: Request Path Mapped (WinHTTP backtraces)

`-WinHttp` captured 50 live `GetRows` requests (one per scroll impulse) with full
call stacks. This maps the request-build path -- the mirror of the `+0x5ecf144`
response path -- which is the linchpin of the direct-endpoint strategy.

The encrypted `GetRows` body is 544 bytes, written via `WinHttpWriteData`; the
one game frame at that depth is `forzahorizon6.exe+0x5412fca` (post-encryption,
handing ciphertext to WinHTTP). The plaintext request -- where `startAt` /
`maxResults` live before encryption -- is built higher up, in the application
frames seen on the `WinHttpAddRequestHeaders` stack:

```text
forzahorizon6.exe+0x350bbf6   (top game frame: Scoreboard service call)
              +0xbbaa81
              +0x291f68d
              +0x2a486ec
              +0x35069ff
              +0x5e66ace       -> xgameruntime.dll -> libHttpClient.dll (send)
... encryption happens across the runtime handoff ...
              +0x5412fca       -> WinHttpWriteData (544-byte ciphertext)
```

`X-Validation` is a 32-hex (16-byte) signature over the request, present on every
method. The body is `bin/xtsw` (encrypted). Both are produced in-process, so an
edited replay is impossible; the request must be driven from inside the game.

### Remaining path to a parallel client (honest cost)

1. Hook the mid-stack builders (`+0x291f68d` / `+0x2a486ec` / `+0x35069ff`) and
   dump their buffers to locate the **plaintext** request holding `startAt` and
   `maxResults`, and the field offsets.
2. Either patch `startAt` in place per call (arbitrary ranges, but serial through
   the game's own HTTP client), OR isolate the encrypt+sign routine and call it as
   an **oracle**: feed plaintext with any `startAt`, get a valid `bin/xtsw` body +
   `X-Validation` back.
3. Fire the oracle-produced bodies through an own HTTP client, in parallel, using
   the live `Authorization: XBL3.0` token (which expires, so it must be captured
   fresh per run).

Steps 1-2 are multi-session reverse engineering with no guarantee. The payoff is
the only thing that makes million-row boards tractable: arbitrary, parallel
range fetches instead of the ~750 ranks/min UI-scroll ceiling.

## Next Steps

1. Run the direct-memory workflow end to end after the current Hyper-V host is
   restarted.
2. Verify block-to-block continuity on a long leaderboard and tune the
   prefetch margin for maximum rows per minute.
3. Add checkpoint resume based on the last merged rank.
4. Keep `GetRows` cursor replay as optional research; it is no longer required
   for structured extraction.

## Bounded Replay Test

`scripts/replay_captured_getrows.py` can replay exactly one already captured `GetRows` request. It reads credentials only in memory, never prints or stores them, does not save the response body, and requires `--confirm-live-request`.

This test proved that an independent host process can receive HTTP 200 for one captured request while the session remains valid. It does not yet prove that replay advances the scoreboard cursor, and it cannot decode the protected response.
