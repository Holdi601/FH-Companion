<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="branding/png/fh-companion-shield-dark-transparent.png">
    <img src="branding/png/fh-companion-shield-transparent.png" alt="FH Companion logo" width="240">
  </picture>
</p>

# FH Companion

A Windows companion app for Forza Horizon 6. It adds an in-game overlay with
route and car advice, a lap delta and live map, and car notes. It also records
your laps and times, shows your tuning and tunes, and drives controller haptics
from the game's telemetry. It comes with a website of car ratings built from the
public Rivals leaderboards.

A quick overview: https://www.youtube.com/watch?v=YM7poTzjVUk

**Live version:** the car ratings are at
<https://rradick.duckdns.org:8787/rivals_auto_wertung.html>, and the app can be
downloaded from <https://rradick.duckdns.org:8787/app>.

**Setting up:** step by step with pictures, [on the PC you play on](docs/setup_pc.md)
or [for an Xbox or a second PC](docs/setup_xbox.md). Both are also short videos on
the download page.

> FH Companion is an unofficial fan project. It is not affiliated with or endorsed
> by Microsoft, Xbox Game Studios or Playground Games. Forza Horizon is a trademark
> of Microsoft.

## About this project

This is a hobby project to test the capabilities of Claude. I am not open to any
discussion about whether I should rewrite everything by hand, or stop, because it
is AI, or anything of that sort.

If you find bugs, have feature requests or want to contribute: be my guest. Open
an issue or a pull request. I am mostly active on Discord, so if you want a quick
reaction, reach me there: <https://discord.gg/A9ssnMXPZf>

## Features

### In-game overlay

Drawn over the game, and only while Forza runs. Every part can be switched off.

- **What to drive.** On the Event Sign Up screen: which cars rank best over
  exactly the three offered routes in that class. Toggle with `F8` or the
  gamepad's View button.
  - Joining a Horizon Play series midway ("2/3"): the ranking covers only the
    races you still drive, and the route already under way counts as done.
  - Laps driven in a series are named after the route that is up next, checked
    against the lap length.
- **Your car.** Which car you are in, and where it places in every category and
  class it has laps in. Toggle with `F6` or a left-stick click.
- **Course maps on the sign-up screen.** An outline of each offered route, drawn
  from your own recorded laps or from the game's Rivals map. The Rivals map
  appears as the game's picture or as a smooth traced line.
  - Tiles sit side by side or stacked.
  - Line thickness and smoothing are adjustable.
  - In a championship, each route is coloured by state: done, now or next.
  - Tiles hide when the race starts or after a time you set.
- **Lap delta.** Time gained or lost against a reference lap, measured at the
  same place on track, not after the same number of seconds.
  - The reference can be the same car and tune, the same car, the same PI class,
    the same car and class, or any lap.
  - The label always starts with the selected reference. In the PI-class and
    any-lap modes it also names the reference car, with its class when that
    differs from yours.
  - **Time to beat.** A line under the delta shows your car's best time on the
    website for this route and class, or your own submitted time if that is
    faster. Beat it and the lap goes onto the leaderboard.
- **Input strip.** Throttle, brake, clutch, steering and gear.
- **Live map.** The current lap drawn as you drive, with adjustable thickness and
  smoothing. Jumps are drawn as gaps.
- **Tyre overview (off by default).** All four tyres at a glance while you drive.
  The colour shows temperature and the outline shows slip. Each tyre tilts with
  its slip angle, and LOCK, SPIN and SLIDE tags mark what it is doing. Bars show
  wheelspin or locking and suspension travel, and icons show puddles, kerbs and
  bumpy ground.
- **Car notes in the car menu.** Your own note for the car highlighted in My Cars,
  plus the applied tune's name, tuner and description.
- **Messages.** A short note when a lap or sprint is stored, and a warning when
  you get close to the game's limit for downloaded tunes.
- **Celebration.** When a lap beats the website's best time for that car, route
  and class: a card with your time and the gap, confetti and a short sound, for
  about five seconds near the top of the screen. On by default; the switch and a
  **Try it** button are in the Lap delta HUD tab, and the sound can be turned
  off on its own.
- **New car on the leaderboard.** When the server accepts your lap of a car
  that was not on that route and class board yet, a calmer notice in teal says
  thanks: a car rolls in, a "NEW" chip, a little confetti and a soft chime. Only
  for the first lap of that car there; a lap sent later from the queue shows it
  only while Forza runs. It has its own switch, on by default.
- **Personal records.** Your own laps make a personal leaderboard per course
  and class, and new bests get a smaller, shorter celebration: green for a
  personal best in the class or with a car, blue for a car's first time on
  your list. Each kind has its own switch. Records are kept per mode by
  default, so a wall-riding lap from a solo race never beats a Rivals best.
- **Celebrations wait until you stop.** No card appears while you drive: they
  show once the car stands still, the race is over or a menu is up.
- **Layout editor.** Drag every block where you want it and resize it with the
  mouse wheel. You can preview the result on the real screen. Clicking a block
  jumps to its settings, and the settings side can be widened into columns.
- **Recording window.** The overlays stay out of screen recordings, because the
  app reads the screen itself. For OBS, a separate window draws them on a key
  colour: capture it and add a colour-key filter. You can also switch the
  overlays over the game off entirely and watch the recording window on a second
  screen instead.

### Laps and times

- **Every lap recorded** to your disk, with times, positions and speeds.
- **My times.** Your best per car and course, and standings by points and by
  total time.
- **Time attack in free roam.** The game runs no clock outside a race; this does.
  Start and finish lines come from your own laps, and `F10` sets one of your own.
- **Lap submission** to the website when a lap beats the leaderboard. This is on
  by default. A gamertag is optional. Without one the lap appears under a
  temporary player name, and a gamertag set later replaces it on all your laps.
  You can switch it off in the Rivals tab or with `"submit_laps": false` in
  `config/overlay.json`.
  - A lap that cannot be sent at once waits on your disk and is sent later. That
    covers submission being off, offline mode, or no answer from the server, even
    for weeks. Before it goes, it is checked again against that
    day's leaderboard. **Discard waiting laps** in the Rivals tab deletes them
    instead.
  - Every lap carries its mode: **Rivals**, **Horizon Play**, a solo or co-op
    race, or free roam. The app tells them apart by the menu you came from (the
    Rivals screen or the Event Sign Up screen), and a Rivals lap also takes its
    route name from the Rivals screen. A lap whose mode is unknown is not sent.
  - Only Rivals and Horizon Play laps count on the website: there a lap that hits
    a wall is invalid or penalised. Solo, co-op and free-roam laps are listed
    separately and never enter the ranking.

### Tuning and tunes

- **Tuning inspector.** What the tune on your car consists of: every fitted part
  with name, step and price, every slider, and where the tune came from. It is
  read from the garage database the game keeps in memory, read-only.
- **Tunes.** How many downloaded tunes you store, measured against the game's
  limit. The tab offers these views:
  - which tunes no car uses;
  - the cars with the most unused tunes;
  - the tuners behind your best laps;
  - the tuners whose tunes you keep most often;
  - a deletion plan for a chosen time frame, grouped by car and tuner.
- **Deleting tunes through the game's menus** *(in progress, not yet in the
  released download).* The app works through the deletion plan in the game
  itself. A test run first goes through everything without deleting, and a tune
  that is on a car is never deleted.

### Controller haptics

- Tyre grip and wheel lock become vibration on the side the car is sliding, out
  of the box, with no curves to set up first.
- Supports the Steam Controller, DualSense (including adaptive triggers), Xbox,
  PlayStation and 8BitDo pads.
- A vibration test, live grip telemetry, a view of all telemetry and outputs, and
  a node editor with Bézier curves that decides how each signal drives each
  actuator.
- **Battery:** vibration costs power. A wireless controller will likely run out
  of battery noticeably faster while the haptics are on, and the stronger and
  faster (higher frequency) you set the vibration, the more power it draws.
  Untick `Graph output enabled` in the Blueprint editor to switch it off.

### Console or second PC

For an Xbox, or a second PC next to the gaming PC. Switch it on in the Live grip
telemetry tab and restart the app.

- The game sends its telemetry over the network; the tab shows the address to
  enter under Data Out.
- The HUD shows in a dashboard window instead of over the game, and can go full
  screen on a second monitor. It covers delta, input traces, live map, tyre
  overview, lap recording and personal records.
- Memory reading, the tune tools and controller haptics are off.
- With a video source (a window that shows the game, a capture card or the OBS
  Virtual Camera, or a stream address read with ffmpeg), the screen-reading
  parts work too: sign-up maps, car recommendations, car notes and mode
  detection. Without one, you set the lap mode by hand.

### The app itself

- **25 languages.**
- **Self-contained.** Nothing to install; the runtime is in the download.
- **Works offline.** The car ratings ship with the download and are refreshed
  from the server when a newer set exists.
- **Signed updates.** The app refuses a package that is not signed by the
  publisher.
- **Idle outside the game.** It stays idle unless Forza runs.
- **Out of the game's way.** No keyboard or mouse hooks. While you drive it does
  not read the screen at all. Overlays hand Windows only their own small area,
  and the app runs below the game's priority. The editor and inspector stop
  redrawing while Forza is in front, and controllers get a report only when
  something changes. `%TEMP%\forza-overlay\perf.log` records what it cost, once
  a minute, and `scripts\measure_game_impact.ps1` measures Forza's frames with
  and without it.
- **Start with Forza** *(optional)*. From sign-in the app waits in the
  notification area and opens minimized when Forza starts. Switch it on with the
  button at the top right or on the first start.
- **Explains itself.** On first start it says what it does on your PC and what
  leaves it, and asks you to confirm.

### Website

The server in `server/` runs the public site:

- car ratings per class (points by finishing position, and the sum of lap times,
  with filters);
- the app download;
- signed uploads for friends who scan boards;
- lap submissions;
- an admin view;
- visitor counts without cookies or tracking.

### Leaderboard scanner

`scripts/` reads the Rivals leaderboards from the running game's memory and from
the screen, and builds the dataset behind the ratings. See
[docs/leaderboard_extractor.md](docs/leaderboard_extractor.md) and
[docs/memory_leaderboard_scanning.md](docs/memory_leaderboard_scanning.md).

## Plans

A separate telemetry tool is planned: it will let you analyse geometric data in
any kind of 3D data. It is a work in progress and not part of this repository yet.

## Documentation

- [docs/app_guide.md](docs/app_guide.md): the app, tab by tab
- [docs/index.md](docs/index.md): every other note (overlay internals, packaging,
  server, scanner, privacy)

## Repository layout

| Folder | What is in it |
|---|---|
| `haptics/ForzaHaptics.Tester` | The app (C#, .NET 9, WinForms). The folder name dates from when it was a haptics test tool. |
| `haptics/ForzaHaptics.Probe`, `haptics/ForzaTelemetry.Probe` | The controller and telemetry probes the app grew out of |
| `server/` | The website |
| `scripts/` | Leaderboard scanning, the data pipeline, packaging, deployment, tests |
| `config/` | Car and route catalogues, translations (`config/lang`), example settings |
| `licenses/` | Full licence texts of the bundled components |
| `docs/` | Notes on every part |

## Building

```powershell
dotnet build haptics/ForzaHaptics.Tester -c Release
```

The self-contained download (runtime, lap records, settings and licences in one
folder):

```powershell
python scripts/build_haptics_package.py
```

See [docs/haptics_package.md](docs/haptics_package.md).

## Settings for your own deployment

Values that belong to one installation stay out of the source. These are the
server address built into the app, the website's public name and its contact
address. Copy `config/local.example.json` to `config/local.json` and fill it in.
That file is ignored by git. Without it the app runs without a server.

`.githooks/pre-commit` runs `scripts/check_no_secrets.py` on every commit. To
enable it:

```powershell
git config core.hooksPath .githooks
```

## Tests

```powershell
python scripts/run_tests.py
```

This runs everything that needs neither the game nor a VM. The app's own
checks run with `"FH Companion.exe" --self-test` (it shows overlays on the
screen) and `--edge-case-test` (no windows).

## Licence

MIT, see [LICENSE](LICENSE). Third-party components and their licences are
listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
