# Third-party notices

Everything this project uses that someone else wrote, and the terms it is used
under. Verified on 2026-09-15 against the packages actually bundled in
`dist/fh-companion/` and the metadata in the local NuGet cache — not from
memory.

Where a licence could not be verified offline, it says so. **Please check those
before publishing.**

---

## 1. Code copied into this project

Exactly one piece. The rest of the source is original.

### DualSense adaptive-trigger effect packing

`haptics/ForzaHaptics.Tester/DualSenseTriggerEffects.cs` is adapted from John
"Nielk1" Klein's *DualSense TriggerEffectGenerator*, revision 6:

<https://gist.github.com/Nielk1/6d54cc2c00d2201ccb8c2720ad7538db>

> Copyright (c) 2021-2022 John "Nielk1" Klein
>
> Licensed under the MIT License. Permission is granted, free of charge, to use,
> copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the
> software, subject to inclusion of the copyright and permission notice. The
> software is provided without warranty of any kind.

---

## 2. Libraries redistributed inside the download

These ship inside `fh-companion.zip` and are therefore redistributed.

| Library | Version | Licence | How it was established |
|---|---|---|---|
| **.NET runtime & Windows Desktop runtime** | 9.0 | MIT | Self-contained publish; 196 of the 220 DLLs in `app/` are Microsoft's runtime |
| **SDL** (`SDL3.dll`) | 3.4.10 | zlib | Version resource: "Simple DirectMedia Layer", © 2026 Sam Lantinga |
| **HidSharp** | 2.1.0 | Apache-2.0 | The licence file the `.nuspec` links to (`zer7.com/files/oss/hidsharp/LICENSE.txt`), read 2026-09-26: © 2010-2025 James F. Bellinger, Apache License 2.0 |
| **Microsoft.Data.Sqlite** / `.Core` | 10.0.12 | MIT | SPDX expression in the `.nuspec` |
| **SQLitePCLRaw** (`core`, `bundle_e_sqlite3`, `lib.e_sqlite3`, `provider.e_sqlite3`) | 2.1.12 | Apache-2.0 | SPDX expression in the `.nuspec`; © 2014-2024 SourceGear, LLC |
| **SQLite** (inside `e_sqlite3.dll`) | bundled by SQLitePCLRaw | public domain | SQLite's own terms |
| **System.Memory** | 4.5.3 | MIT | `licenseUrl` points at the .NET Core `LICENSE.TXT` |

### HidSharp

Its NuGet package carries no licence file, only a link to
<http://www.zer7.com/files/oss/hidsharp/LICENSE.txt>. That text (checked
2026-09-26) reads:

> HIDSharp
> Copyright 2010-2025 James F. Bellinger
>
> Licensed under the Apache License, Version 2.0.

---

## 3. Tooling — used to build and scan, **not** redistributed

None of these are inside the download. They are listed because the repository's
`requirements*.txt` install them.

| Package | Pinned in | Typical licence |
|---|---|---|
| `opencv-python-headless` | `requirements.txt` | Apache-2.0 |
| `pandas` | `requirements.txt` | BSD-3-Clause |
| `pyarrow` | `requirements.txt` | Apache-2.0 |
| `rapidocr` | `requirements.txt`, `requirements-ocr-gpu.txt` | Apache-2.0 |
| `onnxruntime-gpu` | `requirements.txt`, `requirements-ocr-gpu.txt` | MIT |
| `nvidia-*-cu12` (cuBLAS, cuDNN, cuFFT, NVRTC, runtime, nvJitLink) | `requirements-ocr-gpu.txt` | **NVIDIA proprietary EULAs** |
| `scapy` | `requirements-network.txt` | GPL-2.0 |
| `capstone` | `requirements-research.txt` | BSD-3-Clause |
| `pefile` | `requirements-research.txt` | MIT |

Two of these deserve a second look if any of this tooling is ever shipped rather
than merely used: the **NVIDIA CUDA** wheels come under NVIDIA's own licence
agreements, and **scapy is GPL-2.0**, which is the only copyleft licence anywhere
in this project. Neither is redistributed today.

The licences in this table are the ones these projects are commonly published
under; unlike section 2 they were **not** verified against local package
metadata.

---

## 4. Fonts

**Bundled and self-hosted since 2026-09-24** -- nothing is fetched from a third party.

The web pages use three typefaces, all under the **SIL Open Font License 1.1**, which
explicitly allows bundling, embedding and redistribution:

| Typeface | Copyright | Licence file |
| --- | --- | --- |
| Barlow Condensed (500, 600, 700) | Copyright 2017 The Barlow Project Authors | `server/fonts/OFL-barlow-condensed.txt` |
| IBM Plex Sans (400, 400 italic, 500, 600) | Copyright 2017 IBM Corp., Reserved Font Name "Plex" | `server/fonts/OFL-ibm-plex-sans.txt` |
| IBM Plex Mono (400, 500, 600) | Copyright 2017 IBM Corp., Reserved Font Name "Plex" | `server/fonts/OFL-ibm-plex-mono.txt` |

The WOFF2 files (Latin and Latin-Extended subsets) were taken once from the
Fontsource packages (`scripts/fetch_fonts.py`, pinned versions, SHA-256 in
`server/fonts/fonts.lock.json`) and are unmodified. The ratings page embeds them as
`data:` URLs; the admin, app and contribute pages load them from this server under
`/fonts/`. The fonts are used under their own names; none is modified, so the
Reserved Font Name clause is not touched.

**Correction:** the note here on 2026-09-15 said no page fetched a web font. That
check covered `server/*.html` only -- the ratings page, built by
`scripts/build_analytics_site.py`, loaded all three from `fonts.googleapis.com`,
which sent every visitor's IP address to Google. `scripts/test_no_third_party.py`
now opens every page in a real browser and fails on any request to another host.

The overlay and the desktop app use Windows fonts (`Segoe UI`, `Consolas`) only.

---

## 5. The application icon and logos

**Resolved on 2026-09-26.** The earlier icon (`steam-controller.ico` and two PNGs
without any provenance) has been removed. The application icon
(`haptics/ForzaHaptics.Tester/assets/fh-companion.ico`), the site icons in
`server/brand/` and the logos in `branding/` were made for this project: vector
geometry built from scratch after a design concept supplied by the maintainer.
They fall under the project's MIT licence like the rest of the repository.

---

## 6. Data

The car ratings are computed from lap rows read off the public Rivals
leaderboards of Forza Horizon 6, including the gamertag attached to each lap.
Those are personal data in the sense of Art. 4 GDPR; how they are handled, on what
legal basis, and how to have them removed is set out in `docs/datenschutz.md`.

The car names, part names and course geometry come from the running game. None of
this is redistributed as game content: what ships is measurements.

**Visitor statistics (server only, since 2026-09-25).**

- *Country and region of visitors.* These come from **DB-IP "IP to City Lite"**
  (https://db-ip.com), licensed **CC BY 4.0**. The server downloads it itself once
  a month and looks addresses up locally. The attribution "IP geolocation by DB-IP"
  is shown on the admin page next to the data, as the licence requires. The
  database is not redistributed: it lives in `data/runtime/geoip/`, which no build
  or deploy copies.
- *World map on the admin page.* It is generated by `scripts/build_world_map.py`
  from **Natural Earth** 1:110m countries, v5.1.2, which is public domain ("Made
  with Natural Earth"), into `server/assets/world-110m.json`.
- *Reading the database.* The server image installs **`maxminddb`** 3.2.0
  (Apache-2.0), pinned in `server/deploy/Dockerfile`.

---

## 7. Trademarks

*Forza*, *Forza Horizon*, *Xbox* and *Windows* are trademarks of Microsoft.
*PlayStation* and *DualSense* are trademarks of Sony Interactive Entertainment.
*Steam* and *Steam Controller* are trademarks of Valve. *8BitDo* is a trademark of
its owner.

This project is **not affiliated with, endorsed by, or connected to** any of them.
The names are used to say which hardware and which game the program works with,
which is the only way to say it.

For exactly this reason the executable's version resource deliberately does **not**
carry "Forza" as its company or product name — see
`docs/defender-false-positive.md`, where an unsigned binary claiming that name was
measured to trigger a malware classifier.

---

## 8. This project's own licence

**MIT** (since 2026-09-26), see [LICENSE](LICENSE). Every component the download
ships is under a permissive licence (MIT, Apache-2.0, zlib, public domain), so the
MIT licence fits without conflict. The only copyleft package anywhere in the
project, `scapy` (GPL-2.0), is optional research tooling that is installed with
pip, never bundled.

The full licence texts of the bundled components are in [licenses/](licenses/),
and the app download carries them in its `licenses` folder next to `LICENSE.txt`.
