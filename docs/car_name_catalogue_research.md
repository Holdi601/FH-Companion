# Car Name Catalogue — Extraction Research

## SOLVED (2026-08-19): carId -> codename from an in-memory array

The catalogue is a contiguous array of **32-byte null-padded ASCII codename
slots indexed by carId** (`base + carId*0x20`). Codenames are the internal
identifiers `MAKE_model_year`, e.g. `LOT_00_ExigeWTA_18`, `NIS_32_SkylineWTA_93`,
`FER_355Berlinetta_94` -- the same names as the `media\Cars` folders.

Verified: base derived from anchor carId 4210 (`LOT_00_ExigeWTA_18`) lands
carId 4212 on `NIS_32_SkylineWTA_93`, and 4211 on `HON_19_CRXWTA_90` -- exact
carId indexing. **All 270 dumped codenames match a real `media\Cars` folder
(270/270, zero misses)**, so the extraction is ground-truth correct.

Delivered:
- `scripts/dump_car_catalogue.py` -- read-only, self-anchors on two known
  codenames, verifies the index, bounds the array by walking outward from the
  anchor, and MERGES into a persistent catalogue.
- `scripts/update_car_catalogue.ps1` -- one-command host launcher: seeds the
  guest with the current catalogue, dumps from the live process, merges back to
  `config/fh6_car_catalogue.json`. No operator in the loop.

Coverage: the in-memory array holds the cars loaded for the CURRENT context
(~270, the loaded band; the R board's cars sit in carId 3968..4239). The A board's
cars (carId 326, 2038, 3859...) are a different band and are NOT in a single dump.
The game ships 662 car folders. Full coverage therefore comes from ACCUMULATION:
run the updater after scanning different boards/classes over the weekly sweep and
the merge grows toward the full set. The display label shown on the leaderboard
("Exige WTAC") is a separate, abbreviated UI string and is deliberately not used
as the key -- the codename is unique, dated, and stable.

## Original investigation (how the above was reached)


The scoreboard rows carry only a numeric `carId` (uint16 @ row 0x198). The
display name ("Exige WTAC") is resolved by the game from a local catalogue, not
carried in the network protocol (`ForzaCar` has `carId`/`make` but no name).
Goal: a standalone, weekly-refreshable script that produces `carId -> name`.

## Established (2026-08-19)

- `carId` is fully extractable and now backfilled into every dataset with raw
  rows (see scripts/redecode_car_fields.py). It is a stable, complete join key;
  only the human-readable name is missing.
- Ground truth from a live R-class board: carId **4210 = "Exige WTAC"** (the meta
  car, ~90% of rows), carId **4212 = "Skyline WTAC"** (rank 4, confirmed on
  screen).
- The car name lives in memory as an interned ASCII string object with the
  header `[len uint32][refcount 0xffffffff][chars\0]`. "Exige WTAC" was found 47
  times (all with that header).
- The `ScoreboardRow` does NOT reference the name: its only heap pointers are the
  two linked vectors at 0x128 and 0x140, which point into a different arena
  (0x1f1e_/0x1f1fa_) than the name strings (0x1f45e_/0x1f460_).
- **No 8-byte pointer anywhere in memory equals a "Exige WTAC" string-object
  address** (char addr or char-8). So the catalogue does not hold a direct
  pointer to these ASCII objects.
- "Skyline WTAC" as ASCII was NOT found in memory at all, despite being on
  screen — so the stored form differs from the OCR text (likely UTF-16, or a
  longer canonical/model name, or a string id).

## Open hypotheses (each needs a live-process pass)

1. Catalogue stores names as UTF-16, or via a string-id/hash rather than a
   pointer to the ASCII copies. Next: search for the name as UTF-16LE and for a
   table keyed by carId.
2. Catalogue is an array indexed by carId (entry position encodes the id, so the
   id is never stored as a value near the name). Next: find the name
   representation the catalogue actually uses, then locate the base+stride.
3. File-based source: media\Cars holds 662 car folders named by codename
   (ACU_Integra_23, POR_CarreraGT_03). Human-decodable and updates with the game,
   but carId (a large global ordinal) is not the folder index, so a carId->codename
   manifest still has to be found (candidates: packed media\sfsdata 73MB,
   media\UI\Textures\Localized.zip).

## Tools written

- scripts/find_car_name_table.py — find a name string in the live process, dump
  context (ascii + utf16le, all committed regions).
- scripts/find_car_catalogue.py — two-pass: find name objects, then pointers to
  them and any nearby carId; also lists all pointer sites to test the array
  hypothesis.

Both are read-only (PROCESS_VM_READ, no injection).
