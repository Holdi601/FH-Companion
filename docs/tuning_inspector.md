# Tuning inspector

What the tune on the car you are driving actually consists of — every part, every
slider — read out of the game's own database.

| | |
| --- | --- |
| **Where it lives** | *Tuning inspector* tab in FH Companion |
| **What it needs** | Forza running; the button does the rest |
| **Without the game** | `"FH Companion.exe" --tuning-report <file.db>` against an earlier dump |

## Where the data comes from

Forza ships no `.db` file — the Steam folder holds only the executable. But the
running game keeps **SQLite databases in memory**, and one of them is the career
garage: one row per owned car, 146 columns, holding every fitted part, every tuning
slider and the origin of the tune.

The app finds them the way `scripts/dump_sqlite_from_memory.py` does — walk the
process's committed private regions, look for the `SQLite format 3\0` header, check
that what follows really is a header (page size a power of two between 512 and
65536, read and write version 1 or 2), then copy page size × page count out. That is
now in C# (`Tuning/ForzaMemoryDb.cs`) rather than shelling out to Python, because
the app is shipped as a standalone package and a tool that needs a foreign runtime
never runs at the user's machine.

## What it can tell you

**Every fitted part**, by area, with its part number, its step and what was paid for
it. Two things the numbers give up, both measured rather than assumed:

- **The last three digits are the upgrade step.** Across 569 cars, those with no
  purchased parts at all average 0.56 parts above step 0; those with purchases
  average 9.53. And in a row-by-row check on one car, *every* part above step 0 had
  a purchase record in `Career_PurchasedParts` and every part at step 0 had none.
  Step 0 means stock.
- **The leading digits are the car number — and when they are not, the part is
  foreign.** 57 % of all part values carry the car's own `CarId`. The rest belong to
  shared families (`2102xxx` for clutch, transmission, driveline, differential) or
  to *another car* — which is an engine swap. On car 3761 the camshaft, valves,
  displacement and exhaust all read `3899xxx`. Nothing else in the game tells you
  that from the outside.

**Every tuning slider** — 37 of them, from tyre pressure through all ten gears,
camber, toe, caster, anti-roll bars, springs, ride height, rebound and bump,
downforce, brake balance to the differential.

**Where the tune came from**: file name, versioned tune id, and the XUID of whoever
built it. Worth remembering that the driver is not the tuner — in this garage 558 of
569 cars run someone else's tune, by 279 different creators.

## What it cannot tell you, and why

**Part names.** Parts are numbers. The dictionary that would turn `3899003` into
"Race camshaft" is the `List_Upgrade*` catalogue, and it *is* in memory — but only
as a page cache, not as a coherent file image, so it comes out empty. The two large
images (52 MB and 22 MB) dump fine and contain nothing.

**Real units for the sliders.** The screen says 2.1 BAR where the database says
0.4. That is the slider's position, not the displayed value, and the displayed value
simply is not stored anywhere — hours went into searching memory for "2.1" on
2026-08-30 before that was established. Turning it back would need every field
calibrated by hand: slider to each end, value read off. Until then the tab shows the
position and says so.

**The PI.** `Career_Garage.PerformanceIndex` looks like PI (0.7857 → 786) and is
not. Derive the class from it and you contradict the game's own `ClassID` on **128
of 569 cars**, always by exactly one step: the highest value under a given ClassID
sits about a hundred points below that class's ceiling. So the tab takes the class
from `ClassID` — which *is* the telemetry's `CarClass` field, confirmed against
eleven cars actually driven — and the PI from live telemetry, where it is the number
the game shows.

## What is proven, and what still needs the game

The reading half is proven against a **real garage database** — 569 cars, parts,
prices, sliders, engine swap — run from the packaged build, not the development one.

The half that touches a live process is proven too, without Forza: the self-test
creates a SQLite database, loads its bytes into the app's **own** memory, and points
the scanner at the app's own process. Region walk, header validation, reading,
copying the right byte range, opening the result and finding the planted car — all
of it holds.

What is *not* proven is that Forza's own regions behave the same as the .NET heap's.
Only a run with the game can show that, and if the game's process cannot be opened
the tab says so and suggests running as administrator.

## A correction this work forced

The class boundaries in the app were wrong by one whole class. They read "up to 500
is D". The game's class strip says **D 400 / C 500 / B 600 / A 700 / S1 800 /
S2 900 / R 998** — a fact that had been sitting in a comment in
`scripts/forza_navigator.ps1` for weeks, and which the garage database confirms
independently through `ClassID`.

Every lap recorded before 2026-09-14 was therefore filed one class too low: an S1
lap under "A", an R lap under "S2". `scripts/fix_lap_classes.py` moves them and
repairs the `Class` field inside each file; all 140 needed it, every one of them off
by exactly one step.
