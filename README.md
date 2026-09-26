# FH Companion

A Windows companion app for Forza Horizon 6: controller haptics from the game's
telemetry, a lap-time overlay, and Rivals advice drawn from the public
leaderboards.

> FH Companion is an unofficial fan project. It is not affiliated with or endorsed
> by Microsoft, Xbox Game Studios or Playground Games. Forza Horizon is a trademark
> of Microsoft.

## What it does

- **Controller haptics.** Grip, slip and surface from the game's UDP telemetry
  become trigger and grip feedback on the Steam Controller, DualSense and other
  gamepads. A node editor decides how each signal maps to each actuator.
- **Lap overlay.** A live delta against your own best, the leaderboard, or the
  fastest car in the same PI class. It also shows a live map of the lap and
  outlines of the offered courses on the sign-up screen, and follows a
  championship race by race.
- **Rivals advice.** Which car to drive on which route and class, built from
  leaderboard data. The same data feeds the car ratings website in `server/`.
- **Car notes.** Your own notes and the applied tune, shown next to the car
  highlighted in the car menu.
- **Tuning inspector and tunes.** Every part and slider of the current car, read
  from the game's in-memory garage. Also: how many downloaded tunes you store,
  which ones no car uses, and whose tunes you drive.
- **Free-roam time attack.** Timed laps outside races, from start and finish
  lines learned from your own laps.
- **25 languages.** The texts live in `config/lang`.

## Repository layout

| Folder | What is in it |
|---|---|
| `haptics/ForzaHaptics.Tester` | The app (C#, .NET 9, WinForms) |
| `haptics/ForzaHaptics.Probe`, `haptics/ForzaTelemetry.Probe` | The controller and telemetry probes the app grew out of |
| `server/` | The website: car ratings, app download, lap submissions, admin view |
| `scripts/` | Leaderboard scanning, the data pipeline, packaging, deployment, tests |
| `config/` | Car and route catalogues, translations, example settings |
| `docs/` | Notes on every part; start at [docs/index.md](docs/index.md) |

## Building

```powershell
dotnet build haptics/ForzaHaptics.Tester -c Release
```

The self-contained package (runtime, lap records and settings in one folder):

```powershell
python scripts/build_haptics_package.py
```

See [docs/haptics_package.md](docs/haptics_package.md).

## Settings for your own deployment

Values that belong to one installation stay out of the source. These are the
server address built into the app, the website's public name and its contact
address. Copy `config/local.example.json` to `config/local.json` and fill it in.
That file is ignored by git. Without it the app runs without a server.

Secrets never go into the repository either. `.githooks/pre-commit` runs
`scripts/check_no_secrets.py` on every commit. To enable the hook:

```powershell
git config core.hooksPath .githooks
```

## Tests

```powershell
python scripts/run_tests.py
```

This runs everything that needs neither the game nor a VM. The app's own
checks run with `"FH Companion.exe" --self-test` (this shows overlays on the
screen) and `--edge-case-test` (no windows).

## Leaderboard extraction

The scanner that reads the Rivals leaderboards is described in
[docs/leaderboard_extractor.md](docs/leaderboard_extractor.md) and
[docs/memory_leaderboard_scanning.md](docs/memory_leaderboard_scanning.md).

## Third-party components

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
