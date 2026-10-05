# Shipping the app

One folder, one zip, no install, and the records already inside it. Built by:

```powershell
python scripts/build_haptics_package.py
```

Result: `dist/fh-companion/` and `dist/fh-companion-<date>.zip` next to
it — 156 MB unpacked, 59 MB zipped. Hand over the zip; the recipient unpacks it and
double-clicks `START.cmd`.

## `--data-only` baut die App NICHT neu

Der Sweep frischt das Paket nach jedem Board auf, und zwar mit `--data-only`: nur der
Datenbestand wird getauscht, `dotnet publish` laeuft nicht. Ein voller Bau nach jedem
Board waere Verschwendung.

Die Bau-Kennung wird dabei trotzdem neu vergeben — die Daten haben sich ja geaendert.
Fuer den Selbstaktualisierer sieht das aus wie ein neues Programm. Am 2026-09-14 ist
so ein ganzer Nachmittag Arbeit an der App nicht ausgeliefert worden: neue Kennung,
Binaerdatei von 15:26.

**Nach jeder Aenderung am C#-Quelltext also einmal ohne `--data-only` bauen.**
`--data-only` sagt es seit dem 2026-09-14 auch selbst und nennt die Datei, die neuer
ist als die Binaerdatei im Paket. Abgebrochen wird nicht: ein frischer Datensatz ist
auch mit alter App richtig.


## What is in it

```
fh-companion/
  START.cmd                      double-click, nothing else
  LIESMICH.txt                   what it does, the four Data Out settings
  README-app.md                  every feature, the blueprint signal flow
  THIRD_PARTY_NOTICES.md         SDL, HidSharp
  config/overlay.json            keys, panel size, dataset_url
  data/analytics/laps.json       the records, as shipped
  data/analytics/laps.meta.json  version, build date, board count
  app/                           the binary, SDL3.dll, HidSharp.dll, .NET runtime
```

The binaries live in `app/` because a self-contained WinForms build is 270 files. At
the top stay the four things a human opens: the launcher, the readme, the settings,
and the data. The app looks for both `config/overlay.json` and
`data/analytics/laps.json` by walking *up* from its own directory, so this layout
needs no special case — and neither does running the binary straight out of the
repository.

## Nothing to install

`--self-contained` puts the .NET 9 runtime in the folder. Without it, a first start
on a machine that never had .NET shows a dialog offering a download and then still
no program. That is the whole reason the package is 156 MB rather than 37.

`--framework-dependent` builds the small variant instead. Its generated `START.cmd`
tests for the runtime with `dotnet --list-runtimes`, offers to install it through
winget, and prints the download link when winget is missing. Both are complete
packages; they differ in who carries the burden.

Trimming (`PublishTrimmed`) is deliberately off: WinForms does not support it, and a
trim that throws away something reached by reflection fails at run time, on someone
else's machine.

## Why the data ships with it

Without a dataset the overlay has nothing to say, and the only other source is the
server — which is off precisely when the scanning machine is being played on. So the
records travel inside the package.

`laps.meta.json` carries `version`, `built_at` and `boards`. The version is the
dataset's own sha256 prefix, computed exactly as `server/analytics_api.py` computes
it, so the app can ask the server "is there anything newer" and get a truthful "no"
without downloading seven megabytes to find out.

`DatasetSync.LocalBest()` picks what to load, and the order is by **build date**, not
by when a file arrived:

1. a download in `%LOCALAPPDATA%\FHCompanion\`, if its data was built later
2. the copy that shipped in the package
3. `data/analytics/laps.json` from a repository above the binary — the scanning
   machine's own case, and it has no meta file, which is what tells the two apart

Unzipping a fresh package beside a months-old download therefore wins, and a
download made after the package was cut also wins. "Fetched later" would get one of
those two backwards.

Set `"dataset_url": ""` in `config/overlay.json` and the app never opens a socket of
its own; it works purely from the shipped file.

## What the build proves before it zips

The packager does not trust its own copying. It runs the freshly published binary,
from a *different* working directory, and makes it answer:

- `--dataset-source` — which dataset it would load, from where, which version, and
  how many boards actually parse. The build fails unless that is the file just
  packaged, by version and board count, reported as `Bundled`. A package that
  quietly reads the build machine's repository is empty everywhere else.
- `--dump-class-table --class A` — a real class table out of that data. Loading and
  scoring are different claims; the second one is the point of the package.
- and then it opens the window for ten seconds and closes it politely.

That last check exists because of what the first build found: every headless test was
green while the app could not start at all. `RivalsTab`'s constructor called
`BeginInvoke` before there was a window handle, the exception left `MainForm`'s
constructor, and the process died before painting anything — from the day the overlay
tab was added (2026-08-27) until the same day it was packaged. Headless paths branch
off *before* `ApplicationConfiguration.Initialize()`, so no test that used them could
ever have seen it. Ten seconds of a live message loop is the cheapest proof that the
thing a user double-clicks actually opens.

`--no-gui-check` skips it, for a build where a window flashing up is unwelcome.

## Keeping the shipped data current

The records grow with every sweep, so a package cut today is stale next week. That
happens by itself now: `build_analytics_site.py` calls
`build_haptics_package.py --data-only` after it writes a new dataset, which swaps the
JSON, re-checks that the binary still finds and scores it, and rezips — about twenty
seconds, binaries untouched. Nothing happens if no package has been built yet, and a
failure there is printed but never brings the site build down.

The sweep rebuilds the site every fifth board, so the zip in `dist/` is at most five
boards behind the scan. That cadence is `FORZA_SITE_EVERY` (default 5, `0` means only
once at the end of the run). It exists because the build has grown with the record:
119 s measured on 2026-08-24, about three hours on 2026-09-09 at 504 boards. At two
minutes a build beside the OCR was affordable; at three hours one would be running
essentially all night, which is exactly when an OCR worker died with BrokenProcessPool
twice in August. Set it to 0 for a long unattended sweep and take the one build at the
end.

Rebuild the binaries themselves — after a code change — with the plain command. The
line it prints is worth reading: board count, tracks, classes, laps, and how many
cars still have no name.

## Where people get it

The site hands it out at **`/app`** — a page describing what the app does, with a
download button pointing at **`/download/haptics`**. No password: the app reads its
own telemetry and draws over its own game.

`/download/haptics` serves `dist/fh-companion-latest.zip` (falling back to the
newest dated zip) and **streams it from disk** rather than returning it as bytes
through the API, because the package is around 64 MB and three simultaneous
downloads would otherwise be three copies in the server's memory. If nothing has
been built yet it answers 503 saying so, instead of 404.

The scan tool is the opposite case and is gated — see `docs/contributions.md`.
