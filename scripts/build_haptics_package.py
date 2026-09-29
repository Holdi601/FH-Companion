"""Das Paket bauen, das ein Spieler bekommt: ein Ordner, alles drin, ohne Internet nutzbar.

    python scripts/build_haptics_package.py
    python scripts/build_haptics_package.py --framework-dependent
    python scripts/build_haptics_package.py --no-zip --out dist/probe

Ergebnis ist `dist/fh-companion/` und daneben die ZIP mit demselben Namen und
dem Baudatum. Der Ordner ist das Paket -- er wird nicht "installiert", sondern
entpackt und gestartet.

## Warum selbsttragend gebaut wird (und was das kostet)

Der Nutzer soll nichts nachinstallieren muessen. Ein normaler .NET-Bau braucht die
"Desktop Runtime 9" auf dem Zielrechner; fehlt sie, kommt beim Doppelklick ein
Dialog, der nach einem Download fragt, und danach hat man immer noch kein Programm.
Also wird mit `--self-contained` gebaut: die Laufzeit liegt mit im Ordner.

Das kostet Platz -- rund 160 MB im Ordner, gut 70 MB als ZIP. Wer das nicht will,
nimmt `--framework-dependent`: dann sind es rund 3 MB, und das erzeugte `START.cmd`
prueft die Laufzeit, bietet die Installation ueber winget an und erklaert den Rest.
Beides ist ein vollstaendiges Paket, nur die Bringschuld liegt anders.

Getrimmt (`PublishTrimmed`) wird NICHT: WinForms unterstuetzt es nicht, und ein
Trimmen, das Reflexion wegwirft, faellt erst im laufenden Programm auf.

## Warum die Daten mitkommen

Ohne Datensatz zeigt das Overlay ein leeres Panel, und der einzige Ausweg waere der
Server -- der genau dann aus ist, wenn auf dem Scan-Rechner gespielt wird. Der
Datensatz kommt daher als Datei mit ins Paket (`data/analytics/laps.json`), zusammen
mit `laps.meta.json`: Fassung, Baustand, Anzahl Boards. Die Fassung ist der Hash
des Datensatzes, genau wie `scripts/analytics_api.py` sie berechnet -- damit die App
nach dem Anstoepseln am Server erkennt, dass sie schon das Neueste hat, und nicht
sieben Megabyte umsonst holt.

Die App bevorzugt danach von selbst, was neuer ist: ein spaeter geholter Download
gewinnt gegen die mitgelieferte Datei, eine neu entpackte Fassung gewinnt gegen
einen alten Download. Verglichen wird der BAUSTAND, nicht der Abrufzeitpunkt.

## Warum die Binaerdateien in `app/` liegen

Ein selbsttragender WinForms-Bau sind ueber 200 Dateien. Liegen die im Wurzelordner,
findet niemand `START.cmd`, und die zwei Dateien, die ein Mensch anfassen soll --
`config/overlay.json` und der Datensatz -- gehen darin unter. Die App sucht beide
aufwaerts vom Programmordner, `app/` funktioniert also ohne Sonderfall.

## Warum am Ende die gebaute .exe selbst befragt wird

`verify()` startet die fertige Binaerdatei mit `--dataset-source` und `--dump-class-table`
aus einem FREMDEN Arbeitsverzeichnis, ohne Netz. Erst wenn die App selbst sagt, dass
sie den mitgelieferten Datensatz gefunden, geladen und eine Klassentabelle daraus
gerechnet hat, wird gezippt. Alles andere waere geraten: ob ein Paket seine Daten
findet, entscheidet der Suchpfad der Binaerdatei und nicht die Absicht des Packers.

Und `gui_check()` macht das Fenster wirklich auf -- zehn Sekunden, dann hoeflich zu.
Beim ersten Bau dieses Pakets war genau das der Fund: alle kopflosen Pruefungen gruen,
das Fenster kam nie hoch, weil die Rivals-Registerkarte `BeginInvoke` im Konstruktor
rief. Kopflose Pfade zweigen VOR dem WinForms-Aufbau ab und koennen das nie sehen.
"""

from __future__ import annotations

import argparse
import contextlib
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
import zipfile
from datetime import datetime, timedelta
from pathlib import Path

HERE = Path(__file__).resolve().parent
WORKSPACE = HERE.parent

APP_PROJECT = WORKSPACE / "haptics" / "ForzaHaptics.Tester"
APP_EXE_NAME = "FH Companion.exe"
# Bis 2026-09-26 hiess die App so. Der Aktualisierer der alten Fassungen startet nach
# dem Austausch fest diese Datei -- das Paket legt darum eine KOPIE des neuen
# Starters unter diesem Namen dazu (er laedt "FH Companion.dll", siehe AppInfo.cs).
OLD_APP_EXE_NAME = "Forza Grip Haptics.exe"
SDL_DLL = WORKSPACE / "haptics" / "ForzaHaptics.Probe" / "native" / "SDL3.dll"
DATASET = WORKSPACE / "data" / "analytics" / "laps.json"
OVERLAY_CONFIG = WORKSPACE / "config" / "overlay.json"
APP_README = WORKSPACE / "haptics" / "README.md"
# AM WURZELVERZEICHNIS, nicht unter haptics/: auf GitHub wird die Datei dort
# erwartet, und sie gilt fuer das ganze Projekt, nicht nur fuer die App.
NOTICES = WORKSPACE / "THIRD_PARTY_NOTICES.md"
LICENSE_FILE = WORKSPACE / "LICENSE"
LICENSES_DIR = WORKSPACE / "licenses"
DEFAULT_OUT = WORKSPACE / "dist" / "fh-companion"
# HTTPS seit 2026-09-25. Die App faellt nur bei einem gescheiterten TLS-Handschlag
# auf HTTP zurueck (Rivals/ServerHttp.cs) und hebt die alte http-Vorgabe beim Laden an.
# Die Adresse steht nicht im Quelltext, sondern in config/local.json (nie im
# Repository, siehe server/local_settings.py).
sys.path.insert(0, str(WORKSPACE / "server"))
import local_settings  # noqa: E402

DEFAULT_SERVER = local_settings.server()
OLD_DEFAULT_SERVER = local_settings.old_server()

# Dateien, die im Paket nichts zu suchen haben. Die .pdb allein sind ueber 30 MB und
# helfen nur, wenn man die Quellen daneben hat.
DROP_SUFFIXES = (".pdb", ".xml")


class Failed(Exception):
    """Ein Abbruch mit einem Satz, den man lesen kann."""


class Busy(Exception):
    """Ein anderer Lauf ist dran. Kein Fehler, nur nichts zu tun."""


LOCK = WORKSPACE / "data" / "runtime" / "haptics_package.lock"
LOCK_STALE_MINUTES = 60


def take_lock() -> None:
    """Verhindern, dass zwei Laeufe dieselbe ZIP schreiben.

    Es gibt inzwischen drei Ausloeser: die Hand, der Seitenbau (er ruft nach jedem
    neuen Datensatz `--data-only`) und der Aufgabenplaner. Zwei davon gleichzeitig
    treffen sich in derselben Datei, und das Ergebnis ist eine ZIP, die sich nicht
    entpacken laesst -- der Empfaenger merkt es, nicht der Packer.

    Nach einer Stunde gilt eine Sperre als vergessen: ein voller Bau braucht Minuten,
    also ist alles darueber ein abgeschossener Lauf und keine Arbeit.
    """
    LOCK.parent.mkdir(parents=True, exist_ok=True)
    for attempt in (1, 2):
        try:
            handle = os.open(str(LOCK), os.O_CREAT | os.O_EXCL | os.O_WRONLY)
            with os.fdopen(handle, "w", encoding="utf-8") as file:
                file.write(f"pid {os.getpid()} seit "
                           f"{datetime.now():%Y-%m-%d %H:%M:%S}")
            return
        except FileExistsError:
            age = datetime.now() - datetime.fromtimestamp(LOCK.stat().st_mtime)
            # DER INHABER LEBT NICHT MEHR: dann ist die Sperre vergessen, egal wie
            # jung. Am 2026-09-25 hielt ein abgebrochener Bau so jeden weiteren eine
            # Stunde lang auf ("Ein anderer Lauf ist dran" -- von einem Toten).
            held = LOCK.read_text(encoding="utf-8", errors="replace").strip()
            inhaber = held.split()[1] if held.startswith("pid ") and len(held.split()) > 1 else ""
            if inhaber.isdigit() and not _lebt(int(inhaber)) and attempt == 1:
                say(f"  Sperre von pid {inhaber}, der nicht mehr laeuft -- wird uebergangen")
                LOCK.unlink(missing_ok=True)
                continue
            if age < timedelta(minutes=LOCK_STALE_MINUTES) or attempt == 2:
                held = LOCK.read_text(encoding="utf-8", errors="replace").strip()
                raise Busy(f"{held} (seit {int(age.total_seconds() / 60)} min)")
            say(f"  Sperre ist {int(age.total_seconds() / 60)} min alt -- vergessen, "
                "wird uebergangen")
            LOCK.unlink(missing_ok=True)


def _lebt(pid: int) -> bool:
    """Laeuft dieser Prozess noch? Unter Windows ueber OpenProcess -- os.kill(pid, 0)
    schickte dort ein Strg+C an die Prozessgruppe statt nur zu fragen."""
    if os.name != "nt":
        try:
            os.kill(pid, 0)
            return True
        except OSError:
            return False
    import ctypes
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    griff = kernel.OpenProcess(0x1000, False, pid)      # PROCESS_QUERY_LIMITED_INFORMATION
    if not griff:
        # Zugriff verweigert (5) heisst: es gibt ihn, er gehoert nur jemand anderem.
        return ctypes.get_last_error() == 5
    try:
        code = ctypes.c_ulong()
        return bool(kernel.GetExitCodeProcess(griff, ctypes.byref(code))) and code.value == 259
    finally:
        kernel.CloseHandle(griff)


def drop_lock() -> None:
    LOCK.unlink(missing_ok=True)


def say(text: str) -> None:
    print(text, flush=True)


def run(command: list[str], cwd: Path | None = None,
        timeout: int = 900) -> subprocess.CompletedProcess:
    return subprocess.run(command, cwd=str(cwd) if cwd else None, timeout=timeout,
                          capture_output=True, text=True)


# --------------------------------------------------------------------------- #
# 1. Voraussetzungen


def preflight(self_contained: bool) -> dict:
    """Alles pruefen, was fehlen kann, BEVOR eine halbe Stunde Bauzeit vergeht."""
    facts: dict[str, str] = {}

    dotnet = shutil.which("dotnet")
    if dotnet is None:
        raise Failed("`dotnet` ist nicht im PATH. Das .NET-9-SDK baut dieses Paket.")
    version = run([dotnet, "--version"]).stdout.strip()
    facts["sdk"] = version
    major = version.split(".")[0]
    if not major.isdigit() or int(major) < 9:
        raise Failed(f"Das SDK ist {version}; gebraucht wird 9.0 oder neuer.")

    if not APP_PROJECT.exists():
        raise Failed(f"Das Projekt fehlt: {APP_PROJECT}")

    # SDL3.dll liegt bewusst nicht im Projekt, sondern wird per <None Include> aus dem
    # Probe-Projekt kopiert. Fehlt sie, baut alles durch und die Gamepad-Ausgaben sind
    # auf dem Zielrechner still.
    if not SDL_DLL.exists():
        raise Failed(
            f"SDL3.dll fehlt: {SDL_DLL}\n"
            "  Ohne sie hat das Paket keine Ausgabe fuer Xbox-/PlayStation-Pads.\n"
            "  Die x64-Fassung aus SDL 3.4.10 oder neuer dort ablegen.")
    facts["sdl_bytes"] = f"{SDL_DLL.stat().st_size:,}"

    if not DATASET.exists():
        raise Failed(
            f"Der Datensatz fehlt: {DATASET}\n"
            "  Einmal `python scripts/build_analytics_site.py` laufen lassen.")
    if not OVERLAY_CONFIG.exists():
        raise Failed(f"Die Einstellungen fehlen: {OVERLAY_CONFIG}")

    facts["mode"] = "selbsttragend" if self_contained else "braucht die Laufzeit"
    say(f"  SDK {version} · SDL3.dll {facts['sdl_bytes']} Bytes · {facts['mode']}")
    return facts


# --------------------------------------------------------------------------- #
# 2. Bauen


def publish(app_dir: Path, self_contained: bool) -> None:
    app_dir.parent.mkdir(parents=True, exist_ok=True)
    command = [
        "dotnet", "publish", str(APP_PROJECT),
        "-c", "Release",
        "-r", "win-x64",
        f"--self-contained={'true' if self_contained else 'false'}",
        "-o", str(app_dir),
        "-p:DebugType=none",
        "-p:DebugSymbols=false",
        "--nologo",
    ]
    say("  dotnet publish ...")
    result = run(command, timeout=1800)
    if result.returncode != 0:
        tail = (result.stdout + result.stderr).strip().splitlines()[-25:]
        raise Failed("dotnet publish ist gescheitert:\n    " + "\n    ".join(tail))

    for path in list(app_dir.rglob("*")):
        if path.is_file() and path.suffix.lower() in DROP_SUFFIXES:
            path.unlink()

    exe = app_dir / APP_EXE_NAME
    if not exe.exists():
        raise Failed(f"Der Bau hat keine {APP_EXE_NAME} hinterlassen.")
    # DIE KOPIE UNTER DEM ALTEN NAMEN (siehe OLD_APP_EXE_NAME oben). Ohne sie startete
    # der alte Aktualisierer nach dem Austausch die liegengebliebene alte Fassung,
    # die sofort wieder das Update anboete -- eine Schleife ohne Ausweg.
    shutil.copy2(exe, app_dir / OLD_APP_EXE_NAME)
    for needed in ("SDL3.dll", "HidSharp.dll"):
        if not (app_dir / needed).exists():
            raise Failed(f"{needed} fehlt im gebauten Ordner -- das Paket waere taub.")
    if self_contained and not (app_dir / "System.Windows.Forms.dll").exists():
        raise Failed("Selbsttragend gebaut, aber die WinForms-Laufzeit liegt nicht bei.")


# --------------------------------------------------------------------------- #
# 3. Daten und Einstellungen


def dataset_version(raw: bytes) -> str:
    """Dieselbe Fassungskennung, die `analytics_api.summary()` ausliefert."""
    return hashlib.sha256(raw).hexdigest()[:16]


def copy_dataset(root: Path) -> dict:
    target = root / "data" / "analytics" / "laps.json"
    target.parent.mkdir(parents=True, exist_ok=True)
    raw = DATASET.read_bytes()
    payload = json.loads(raw.decode("utf-8"))
    boards = len(payload.get("boards", []))
    if boards == 0:
        raise Failed(f"{DATASET} enthaelt keine Boards.")

    target.write_bytes(raw)
    # `built_at` MUSS der Baustand des Datensatzes sein und nicht der Zeitpunkt des
    # Packens: die App vergleicht damit gegen einen Download, und ein Paket, das sich
    # jung rechnet, verdraengt sonst neuere Daten.
    built_at = datetime.fromtimestamp(
        DATASET.stat().st_mtime).astimezone().isoformat(timespec="seconds")
    meta = {
        "version": dataset_version(raw),
        "built_at": built_at,
        "boards": boards,
        "fetched_at": datetime.now().astimezone().isoformat(timespec="seconds"),
        "source": "im Paket mitgeliefert",
    }
    (target.parent / "laps.meta.json").write_text(
        json.dumps(meta, indent=2, ensure_ascii=False), encoding="utf-8")

    tracks = len(payload.get("tracks", []))
    classes = len(payload.get("classes", []))
    laps = payload.get("meta", {}).get("kept_laps", 0)
    named = payload.get("meta", {}).get("named_cars", 0)
    cars = len(payload.get("carNames", []))
    say(f"  Datensatz: {boards} Boards, {tracks} Strecken, {classes} Klassen, "
        f"{laps:,} Runden, {named}/{cars} Autos benannt, {len(raw) / 1e6:.1f} MB")
    return {"boards": boards, "version": meta["version"], "built_at": built_at,
            "tracks": tracks, "classes": classes, "laps": laps,
            "cars": cars, "named_cars": named, "bytes": len(raw),
            "categories": list(payload.get("categories", []))}


ROUTE_MAPS = WORKSPACE / "data" / "route_maps"
CAR_LIST = WORKSPACE / "data" / "cars" / "fh6_car_availability.json"


def copy_car_list(root: Path) -> int:
    """Die Autoliste fuer "Car collection" -- aelter als 20 Stunden, dann vorher neu holen.

    Kein Abbruch, wenn es nicht geht: die App holt die Liste ohnehin vom Server, und
    ein Paket ohne sie ist besser als keines. Eine alte Liste wird mitgeliefert.
    """
    import sys as _sys
    import time as _time
    _sys.path.insert(0, str(WORKSPACE / "server"))
    import car_availability
    if car_availability.veraltet(CAR_LIST):
        car_availability.aktualisieren(lambda m: say("  " + m), CAR_LIST)
    if not CAR_LIST.exists():
        say("  Autoliste: keine -- die App holt sie vom Server")
        return 0
    ziel = root / "data" / "cars" / CAR_LIST.name
    ziel.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(CAR_LIST, ziel)
    autos = len(json.loads(CAR_LIST.read_text(encoding="utf-8")).get("cars", []))
    say(f"  Autoliste: {autos} Autos")
    return autos


def update_key_path() -> Path:
    """Wo der private Update-Schluessel liegt -- NIE im Arbeitsbereich."""
    eigener = os.environ.get("FORZA_UPDATE_KEY")
    if eigener:
        return Path(eigener)
    return Path.home() / ".forza-signing" / "update_signing_key.pkcs8.b64"


def sign_package(root: Path, zip_path: Path) -> str:
    """Die ZIP mit dem privaten Update-Schluessel unterschreiben.

    Mit der gebauten App selbst (--update-sign), damit Unterschreiben und Pruefen
    dieselbe Bibliothek und dasselbe Format benutzen. Fehlt der Schluessel, bricht
    der Bau ab: ein unsigniertes Paket wuerde jede aktualisierte App ablehnen, und
    das merkte man erst beim Nutzer.
    """
    schluessel = update_key_path()
    if not schluessel.exists():
        raise Failed(f"Kein Update-Schluessel unter {schluessel}. Einmalig anlegen mit "
                     f"'\"{APP_EXE_NAME}\" --update-key-new' und den oeffentlichen "
                     "Schluessel in UpdateSignature.cs eintragen -- oder die Sicherung "
                     "des vorhandenen Schluessels dorthin zurueckkopieren.")
    exe = root / "app" / APP_EXE_NAME
    r = subprocess.run([str(exe), "--update-sign", str(zip_path), str(schluessel)],
                       capture_output=True, text=True, timeout=120)
    sig = (r.stdout or "").strip()
    if r.returncode != 0 or not sig:
        raise Failed(f"Unterschreiben fehlgeschlagen: {(r.stderr or r.stdout)[-300:]}")
    pruef = subprocess.run([str(exe), "--update-verify", str(zip_path), sig],
                           capture_output=True, text=True, timeout=120)
    if pruef.returncode != 0:
        raise Failed("Die frische Unterschrift laesst sich mit dem eingebauten "
                     "oeffentlichen Schluessel nicht pruefen -- passt UpdateSignature.PublicKey "
                     "zum Schluessel unter " + str(schluessel) + "?")
    say("  Paket unterschrieben und gegen den eingebauten Schluessel geprueft")
    return sig


def copy_route_maps(root: Path) -> int:
    """Die aufbereiteten Rivalen-Karten mitliefern: Linienzug und Kartenbild je Strecke.

    SEIT 2026-09-25 STATT DER ROHEN ERNTE. Die rohe Ernte (`data/route_shapes`) ist
    eine Punktwolke je Aufnahme; die App konnte daraus nur Pixelflecken zeichnen,
    und ihre Wahl "die meisten Bildpunkte je Name" nahm fuer "Izu Cross-Country"
    eine Aufnahme, die eine andere Strecke zeigt. `route_maps.py` waehlt nur aus den
    ueber die Karussellposition geprueften Kartenlaeufen und legt je Strecke einen
    geordneten, geglaetteten Weg und den Kartenausschnitt als JPEG ab.

    Ohne diesen Ordner zeigt die Quelle "rivals" im Paket leere Kaesten -- darum
    bricht der Bau ab, wenn er fehlt oder unvollstaendig ist, statt still ein Paket
    ohne Karten auszuliefern.
    """
    ziel = root / "data" / "route_maps"
    if ziel.exists():
        shutil.rmtree(ziel)
    ziel.mkdir(parents=True, exist_ok=True)
    # Die alte Ernte gehoert nicht mehr ins Paket (siehe oben).
    alt = root / "data" / "route_shapes"
    if alt.exists():
        shutil.rmtree(alt)
    karten = sorted(ROUTE_MAPS.glob("*.json")) if ROUTE_MAPS.exists() else []
    if not karten:
        raise Failed("Keine aufbereiteten Rivalen-Karten in data/route_maps -- "
                     "erst `python scripts/route_maps.py` laufen lassen.")
    kopiert = 0
    ohne_bild = []
    for datei in karten:
        try:
            d = json.loads(datei.read_text(encoding="utf-8"))
        except Exception:
            continue
        if not d.get("name") or not d.get("path"):
            continue
        shutil.copy2(datei, ziel / datei.name)
        bild = ROUTE_MAPS / str(d.get("image") or "")
        if d.get("image") and bild.is_file():
            shutil.copy2(bild, ziel / bild.name)
        else:
            ohne_bild.append(d["name"])
        kopiert += 1
    if ohne_bild:
        raise Failed(f"{len(ohne_bild)} Rivalen-Karte(n) ohne Bild: {', '.join(ohne_bild[:5])}")
    say(f"  Rivalen-Karten: {kopiert} Strecke(n) mit Linie und Kartenbild")
    return kopiert


def copy_config(root: Path, server: str) -> None:
    # utf-8-sig und nicht utf-8: PowerShell schreibt bei `-Encoding utf8`
    # eine Bytefolgemarke an den Anfang, und daran scheiterte der Bau am
    # 2026-09-15. Die Marke ist unsichtbar, gueltig und voellig harmlos --
    # nur `json.loads` stolpert darueber. Andere Stellen hier lesen laengst so.
    settings = json.loads(OVERLAY_CONFIG.read_text(encoding="utf-8-sig"))
    # Ausdruecklich hineinschreiben, auch wenn es dem Standardwert entspricht: wer die
    # Adresse leeren will, muss sie erst einmal sehen. Eine leere Zeichenkette heisst
    # "nie fragen" -- dann oeffnet die App gar keinen Socket nach draussen.
    settings["dataset_url"] = DEFAULT_SERVER if OLD_DEFAULT_SERVER and server.rstrip("/") == OLD_DEFAULT_SERVER else server
    # PERSOENLICHES BLEIBT HIER. Die Einstellungen dieses Rechners sind die Vorlage
    # fuer JEDEN Nutzer -- ein hier eingetragener Gamertag reichte sonst die Runden
    # aller unter diesem Namen ein. Und die Einreichung ist im Paket AN (so gewollt:
    # abwaehlbar, nicht anwaehlbar), gleich wie sie auf diesem Rechner steht.
    # Ob hier die Haptik-Ausgabe aus ist, gehoert ebenso diesem Rechner: ohne den
    # Schluessel gilt die Vorgabe (an), und eine frische Installation vibriert.
    for persoenlich in ("gamertag", "disclosure_ack", "skipped_update", "telemetry_from_lan",
                        "haptics_graph_enabled"):
        settings.pop(persoenlich, None)
    settings["submit_laps"] = True
    target = root / "config" / "overlay.json"
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(json.dumps(settings, indent=2, ensure_ascii=False),
                      encoding="utf-8")

    # DIE TEILENAMEN MUESSEN MIT. Ohne sie zeigt der Tuning-Inspektor beim Nutzer
    # wieder nur Nummern -- und genau das war der Einwand, der ihn erst gebaut hat.
    namen = WORKSPACE / "config" / "fh6_part_names.json"
    if namen.exists():
        shutil.copy2(namen, root / "config" / namen.name)
    else:
        say("  HINWEIS: config/fh6_part_names.json fehlt -- der Tuning-Inspektor "
            "zeigt dann Nummern statt Namen. "
            "Mit laufendem Spiel: python scripts/extract_part_names.py --apply")

    # DIE SPRACHDATEIEN MUESSEN EBENSO MIT. `Loc.cs` sucht `config/lang/<code>.json`
    # vom Programmordner aufwaerts; liegt der Ordner nicht im Paket, findet es
    # nichts und die App ist beim Nutzer wieder einsprachig -- englisch zwar, aber
    # das war nicht der Zweck der Uebung.
    #
    # Nicht einzeln aufgezaehlt: es kommen Sprachen dazu, und eine Liste, die man
    # beim Hinzufuegen vergisst, faellt nicht auf.
    lang_quelle = WORKSPACE / "config" / "lang"
    if lang_quelle.is_dir():
        lang_ziel = root / "config" / "lang"
        lang_ziel.mkdir(parents=True, exist_ok=True)
        gezaehlt = 0
        for datei in sorted(lang_quelle.glob("*.json")):
            if datei.name.startswith("_"):
                continue        # _keys.json ist Werkzeug, keine Sprache
            shutil.copy2(datei, lang_ziel / datei.name)
            gezaehlt += 1
        say(f"  Sprachen: {gezaehlt} Datei(en)")
    else:
        say("  HINWEIS: config/lang/ fehlt -- die App laeuft dann nur auf Englisch.")


# --------------------------------------------------------------------------- #
# 4. Was ein Mensch liest


START_SELF_CONTAINED = """@echo off
cd /d "%~dp0"

if not exist "app\\{exe}" (
  echo.
  echo   Die Programmdateien fehlen. Bitte die ZIP komplett entpacken,
  echo   nicht einzelne Dateien daraus herausziehen.
  echo.
  pause
  exit /b 1
)

start "" "app\\{exe}" %*
"""

# Bei der schlanken Fassung ist die Laufzeit die einzige Bringschuld -- also wird sie
# hier geprueft und, wenn winget da ist, auch angeboten. `dotnet --list-runtimes` ist
# der einzige verlaessliche Test: der Ordner kann existieren, ohne dass die
# Desktop-Laufzeit darin liegt.
START_FRAMEWORK = """@echo off
setlocal EnableDelayedExpansion
cd /d "%~dp0"

if not exist "app\\{exe}" (
  echo.
  echo   Die Programmdateien fehlen. Bitte die ZIP komplett entpacken.
  echo.
  pause
  exit /b 1
)

set "HAVE="
where dotnet >nul 2>nul
if not errorlevel 1 (
  for /f "tokens=*" %%L in ('dotnet --list-runtimes 2^>nul') do (
    echo %%L | find "Microsoft.WindowsDesktop.App 9." >nul && set "HAVE=1"
  )
)

if not defined HAVE (
  echo.
  echo   Es fehlt die .NET Desktop Runtime 9 ^(x64^).
  echo.
  where winget >nul 2>nul
  if errorlevel 1 (
    echo   Zu holen hier:
    echo   https://dotnet.microsoft.com/download/dotnet/9.0/runtime
    echo.
    pause
    exit /b 1
  )
  echo   Ich kann sie jetzt installieren. Windows fragt dabei nach Erlaubnis.
  echo.
  choice /c JN /m "  Installieren"
  if errorlevel 2 (
    echo   Abgebrochen.
    pause
    exit /b 1
  )
  winget install --id Microsoft.DotNet.DesktopRuntime.9 --accept-package-agreements --accept-source-agreements
  echo.
)

start "" "app\\{exe}" %*
"""

READ_ME = """FH Companion
============

Companion app for Forza Horizon 6: rivals overlay, lap delta, maps, car notes,
lap records, tuning and tune tools, and controller haptics.
Double-click START.cmd. Nothing is installed{install_note}.

To start it from the desktop or the taskbar, use the two buttons at the top
right of the window. "Desktop shortcut" puts a shortcut to this folder's copy on
your desktop, and replaces one that still points to a moved or deleted folder.
"Pin to taskbar" creates the Start menu entry and tells you the one right-click
that Windows leaves to you; the button shows a tick once the pin is there.

The dataset of {built_day} is included: {boards} boards across {tracks} routes
and {classes} classes, {laps} recorded laps. The program therefore needs NO
internet connection to work.

{categories_note}


What it does
------------

* Rivals overlay: two panels over the game. Which of the offered routes suits
  your car best, and which car is worth driving in this class -- computed from
  the laps of real players.
* Maps: an outline of each route offered on the Event Sign Up screen (from your
  own laps or the game's Rivals map), and a live map of the lap as you drive.
* Lap delta: your time against a reference lap -- your own best, the same car,
  the same PI class -- measured at the place you are standing, not after the
  same number of seconds.
* Car notes: your own note and the applied tune, next to the car highlighted in
  the car menu.
* My times: every lap recorded on your PC, best per car and course, standings.
* Time attack in free roam: Forza runs no clock outside a race. This does.
* Tuning inspector: what the tune on your car is made of.
* Tunes: how many downloaded tunes you keep against the game's limit, which ones
  no car uses, and whose tunes you drive.
* Haptics: tyre grip becomes vibration -- on the side the car is sliding -- and a
  locking wheel a short buzz, without setting anything up. For the
  2026 Steam Controller (four actuators), DualSense (including the adaptive
  triggers), and Xbox, PlayStation and 8BitDo pads. The signal editor maps
  telemetry to haptics as a node graph (saved as .fhgraph.json). Vibration
  costs power: a wireless controller runs out of battery faster, the more so
  the stronger and higher-frequency you set it.


So that telemetry arrives (once, in the game)
---------------------------------------------

Forza Horizon 6, Settings, HUD and Gameplay, near the bottom:

    Data Out            On
    Data Out IP         127.0.0.1
    Data Out Port       5300
    Data Out Format     Dash   (or Sled)

Without this every number in the program stays at 0 -- the game sends nothing.


Tuning inspector
----------------

The "Tuning inspector" tab shows what the tune on the car you are sitting in
consists of: every fitted part with its name, step and price, all 37 tuning
sliders, and where the tune came from. One button, and the game has to be running.

The data comes out of the game's garage database, which exists only in memory.
One thing it does not say, and the tab therefore does not claim: the displayed
UNITS of the sliders (2.1 BAR is stored there as position 0.4). What is foreign
to a part -- an engine swap, say -- is visible at once.


Time attack in free roam
------------------------

Forza runs no clock outside a race. This program does. Drive across the
start/finish line of a route you have driven before and the delta strip works
just as it does in a rivals race: your time against your own best, measured where
you are standing. The second time across the same line the lap is finished and
stored -- with car and performance class, like any other.

The lines are already there: every route you have ever recorded a lap on has one.
EventLab routes too, because a route is recognised by its PLACE and never by a
name.

You set a line of your own with F10 while driving -- for a back road, a mountain
pass, a circuit the game does not have.

Free-roam times are kept SEPARATE from scored ones: out here there are no track
limits, and a time cut across a meadow would be no answer as the best time of a
rivals route.


When it stays quiet, and why that is deliberate
-----------------------------------------------

The program only touches the controller when Forza is running AND telemetry is
arriving. Otherwise it would overwrite the rumble of another game -- or, with
Forza running but Data Out off, overwrite Forza's own rumble with silence. The
overlay panels likewise appear only while the game runs; a key press before that
does nothing, and a pinned panel is taken back in when you leave the game.

The line under the title always says what the matter is:

    Haptic/trigger firewall active            it is working
    idle - waiting for forzahorizon6.exe      the game is not running
    idle - no telemetry on UDP 5300           Data Out is missing (see above)

The test buttons on the first tab run regardless, always -- somebody is pressing
them on purpose. To switch the interlocks off entirely, say to check an actuator
without the game, set in config/overlay.json:

    "haptics_require_forza": false
    "haptics_require_telemetry": false
    "overlay_require_forza": false


What leaves your computer
-------------------------

It downloads. It does not upload. Your lap times, your telemetry, your tunes and
your gamertag are never sent anywhere -- they are written into this folder and
stay there. There is no account and nothing to sign in to. The program explains
all of this on first start and asks you to confirm you have read it.

Two things about it do look unusual, and you should know before you run it: it
reads the running game's memory (read-only, only while the Tuning tab is open,
only to find the part list the telemetry does not contain), and it replaces its
own files when it updates. Both are why antivirus software sometimes distrusts
it; see the download page for the checksum to verify what you got.


The dataset
-----------

data/analytics/laps.json is the dataset that came with this download,
laps.meta.json says when it is from. The program takes whatever is newest on this
disk at startup and then asks the server in the background:

    {server}

The connection is encrypted (HTTPS). Only if this PC cannot check the server's
certificate does the program fall back to plain http, as it always used before;
updates stay signed either way and are refused if the signature does not match.
If it is reachable and has something newer, that is fetched (about {mb} MB) and
stored in your user profile. If it is not reachable -- the normal case, since it
runs on a machine somebody also plays on -- the shipped dataset stays, and the
line under "Refresh data" says exactly that. If you never want it asked, set in
config/overlay.json:

    "dataset_url": ""

Then the program opens no connection to the outside at all. The same is achieved
by "offline": true.


Language
--------

The program follows the display language of Windows and falls back to English.
25 languages are included; the picker at the top right of the window overrides
the choice, and it takes effect the next time the program starts.


Settings
--------

config/overlay.json: keys, size and opacity of the panels, how long they stay up,
which mode your laps are tagged with, and whether the full telemetry track is
written next to each lap. The file explains itself in its first line.


Read more
---------

LICENSE.txt              this program's licence (MIT)
THIRD_PARTY_NOTICES.md   what others wrote and under which terms
licenses/               the full licence texts of the bundled components

From the command line:

    app\\{exe} --overlay                 start straight into the overlay
    app\\{exe} --dataset-source d.json   says which dataset is in force
    app\\{exe} --lang-report de          shows how a language resolves
    app\\{exe} --own-cars B              your own best cars in a class
"""


def categories_note(categories: list[str]) -> str:
    """Sagen, welche Kategorien drin sind -- vor allem, welche nicht.

    "137 Boards ueber 23 Strecken" liest sich wie "das ganze Spiel". Es ist bisher
    ausschliesslich Road Racing, und wer sein Auto in Street Racing sucht, soll das
    hier erfahren und nicht am leeren Panel raten.
    """
    if not categories:
        return "Welche Kategorien darin stecken, sagt der Datensatz nicht."
    listed = ", ".join(categories)
    if len(categories) == 1:
        return (f"Gescannt ist bisher ausschliesslich {listed}. Fuer die anderen\n"
                "Kategorien gibt es noch keine Boards; das Overlay sagt dort\n"
                '"noch nicht gescannt" statt eine Platzierung zu erfinden.')
    return f"Gescannt sind: {listed}."


def write_docs(root: Path, facts: dict, self_contained: bool, server: str) -> None:
    start = (START_SELF_CONTAINED if self_contained else START_FRAMEWORK)
    (root / "START.cmd").write_text(start.format(exe=APP_EXE_NAME), encoding="utf-8")

    built_day = facts["built_at"][:10]
    (root / "README.txt").write_text(READ_ME.format(
        exe=APP_EXE_NAME,
        boards=facts["boards"],
        tracks=facts["tracks"],
        classes=facts["classes"],
        # Komma als Tausendertrennung: die Anleitung ist englisch. Der Punkt
        # stand hier, solange sie deutsch war, und sah danach aus wie eine
        # Zahl mit Nachkommastellen.
        laps=f"{facts['laps']:,}",
        built_day=built_day,
        server=server or "(none configured)",
        mb=f"{facts['bytes'] / 1e6:.0f}",
        categories_note=categories_note(facts.get("categories") or []),
        install_note="" if self_contained else
                     " except the .NET Desktop Runtime 9, which START.cmd offers"
    ), encoding="utf-8")

    # README-app.md WIRD NICHT MEHR MITGELIEFERT. Es ist eine Entwicklernotiz
    # ueber Hardware-Versuche (Stand 2026-08-27) und stand im Paket neben einer
    # Nutzeranleitung -- zwei Dateien mit aehnlichem Namen, von denen die eine
    # nicht fuer den Leser gedacht war. Sie bleibt im Projekt, wo sie hingehoert.
    if NOTICES.exists():
        shutil.copy2(NOTICES, root / "THIRD_PARTY_NOTICES.md")
    # DIE LIZENZTEXTE (seit 2026-09-26). MIT und Apache-2.0 verlangen, dass der
    # volle Text mit den Binaerdateien reist -- die Zusammenfassung allein genuegt
    # nicht. Dazu die eigene Lizenz der App.
    if LICENSE_FILE.exists():
        shutil.copy2(LICENSE_FILE, root / "LICENSE.txt")
    if LICENSES_DIR.is_dir():
        ziel = root / "licenses"
        if ziel.exists():
            shutil.rmtree(ziel)
        shutil.copytree(LICENSES_DIR, ziel)


# --------------------------------------------------------------------------- #
# 5. Die gebaute Binaerdatei selbst befragen


@contextlib.contextmanager
def fresh_machine():
    """Den heruntergeladenen Datensatz dieses Rechners waehrend der Pruefung wegnehmen.

    Die App zieht bewusst den NEUEREN Datensatz vor: einen spaeter geholten Download
    gegenueber der mitgelieferten Datei. Auf dem Baurechner laeuft die App selbst und
    hat sich beim Start frische Daten vom Server geholt -- und damit sieht die Pruefung
    nie, was ein fremder Rechner sieht, sondern immer den eigenen Zwischenspeicher.
    Genau daran ist der Bau am 2026-09-12 gescheitert, und zwar zu Recht: die Pruefung
    hat lieber nichts ausgeliefert als etwas Ungeprueftes.

    Weggenommen wird ausschliesslich der Zwischenspeicher des Datensatzes. Die eigenen
    Runden (`personal_laps.json`), die Anordnungen und der zuletzt benutzte Graph
    bleiben unangetastet -- das sind Daten des Nutzers, kein Zwischenspeicher.
    """
    lokal = os.environ.get("LOCALAPPDATA")
    ordner = local_settings.app_data() if lokal else None
    beiseite: list[tuple[Path, Path]] = []
    try:
        if ordner is not None and ordner.is_dir():
            for name in ("laps.json", "laps.meta.json"):
                quelle = ordner / name
                if not quelle.exists():
                    continue
                ziel = ordner / (name + ".packing-aside")
                if ziel.exists():
                    ziel.unlink()
                quelle.rename(ziel)
                beiseite.append((quelle, ziel))
            if beiseite:
                say("  Zwischenspeicher des Baurechners beiseite gelegt")
        yield
    finally:
        # Unbedingt zurueck, auch wenn die Pruefung scheitert: sonst laedt die App
        # des Nutzers beim naechsten Start 26 MB umsonst nach.
        for quelle, ziel in beiseite:
            try:
                if quelle.exists():
                    quelle.unlink()
                ziel.rename(quelle)
            except OSError as fehler:
                say(f"  ACHTUNG: {ziel.name} konnte nicht zurueckbenannt werden: {fehler}")


def verify(root: Path, facts: dict) -> None:
    with fresh_machine():
        _verify(root, facts)


def _verify(root: Path, facts: dict) -> None:
    """Das gebaute Paket wirklich benutzen, statt es nur anzuschauen.

    WIEDERHERGESTELLT AM 2026-09-15. Diese Funktion ging verloren, als eine
    Textersetzung fuer `gui_check` auf die erste Fundstelle von
    `exe = root / "app" / APP_EXE_NAME` traf -- und die steht hier. Neu geschrieben
    gegen die Schnittstellen in `Rivals/RivalsDump.cs`; der Zweck und die
    Ausgabezeile sind dieselben wie vorher.

    Geprueft wird ueber die kopflosen Befehle der App selbst, nicht durch Lesen der
    Dateien: "die Datei liegt da" und "das Programm kann sie benutzen" sind zwei
    verschiedene Aussagen, und eine abgeschnittene Kopie besteht die erste.

      --dataset-source  laedt den Datensatz so, wie die App ihn beim Start sucht,
                        und sagt, WOHER er kam. Kaeme er hier vom Server statt aus
                        dem Paket, wuerde die Pruefung etwas bestaetigen, das im
                        Paket gar nicht drin ist -- darum legt `fresh_machine()`
                        vorher den Zwischenspeicher des Baurechners beiseite.

      --dump-class-table  rechnet eine echte Wertung. Ein Datensatz, der laedt, aber
                        keine Tabelle ergibt, ist fuer den Nutzer wertlos.
    """
    exe = root / "app" / APP_EXE_NAME
    if not exe.exists():
        raise Failed(f"{exe} fehlt -- der Bau hat nichts abgelegt.")

    with tempfile.TemporaryDirectory() as tmp:
        quelle_datei = Path(tmp) / "dataset-source.json"
        tabelle_datei = Path(tmp) / "class-table.json"

        # 1. Laedt der Datensatz, und kommt er aus dem Paket?
        ergebnis = run([str(exe), "--dataset-source", str(quelle_datei)], timeout=300)
        if not quelle_datei.exists():
            raise Failed(
                "--dataset-source hat nichts geschrieben "
                f"(Code {ergebnis.returncode}).\n"
                f"  {(ergebnis.stdout or '') + (ergebnis.stderr or '')}".rstrip())
        found = json.loads(quelle_datei.read_text(encoding="utf-8"))

        if found.get("error"):
            raise Failed(f"Der Datensatz laedt nicht: {found['error']}")
        if not found.get("boards"):
            raise Failed("Der Datensatz enthaelt kein einziges Board.")
        if str(found.get("source", "")).lower().startswith("server"):
            raise Failed(
                "Geprueft wurde ein Datensatz VOM SERVER, nicht der aus dem Paket."
                f"\n  Damit sagt die Pruefung nichts ueber das, was ausgeliefert "
                "wird.")

        # Das Paket beschreibt sich in LIESMICH.txt mit einer Boardzahl. Weicht sie
        # von der ab, die die App tatsaechlich laedt, ist eines von beiden falsch --
        # und der Nutzer kann es nachrechnen.
        behauptet = facts.get("boards")
        if behauptet is not None and int(behauptet) != int(found["boards"]):
            raise Failed(
                f"Das Paket behauptet {behauptet} Boards, geladen werden "
                f"{found['boards']}.")

        # 2. Ergibt er auch eine Wertung?
        ergebnis = run([str(exe), "--dump-class-table", str(tabelle_datei),
                        "--class", "A"], timeout=300)
        if not tabelle_datei.exists():
            raise Failed(
                "--dump-class-table hat nichts geschrieben "
                f"(Code {ergebnis.returncode}).\n"
                f"  {(ergebnis.stdout or '') + (ergebnis.stderr or '')}".rstrip())
        table = json.loads(tabelle_datei.read_text(encoding="utf-8"))
        if table.get("error"):
            raise Failed(f"Die Klassentabelle scheitert: {table['error']}")

        cars = table.get("cars") or []
        if len(cars) < 10:
            raise Failed(f"Die Klasse A ergibt nur {len(cars)} Autos -- zu wenig, um "
                         "das Paket fuer brauchbar zu erklaeren.")

        say(f"  geprueft: {found['source']}, {found['boards']} Boards, "
            f"Fassung {found['version']}, Klasse A -> {len(cars)} Autos")


def disclosure_version() -> int:
    """Welche Fassung der Erklaerung der Quelltext gerade verlangt."""
    # APP_PROJECT IST schon der Projektordner, nicht die .csproj -- .parent
    # landete in haptics/ und damit neben dem Projekt statt darin.
    quelle = APP_PROJECT / "Disclosure.cs"
    for zeile in quelle.read_text(encoding="utf-8").splitlines():
        if "const int Fassung" in zeile:
            return int(zeile.rsplit("=", 1)[1].strip().rstrip(";"))
    raise Failed("Disclosure.cs nennt keine Fassung -- gui_check kann nicht pruefen.")


def _ack_setzen(root: Path, wert: int | None) -> None:
    """`disclosure_ack` in der ausgelieferten Einstellungsdatei setzen oder loeschen."""
    datei = root / "config" / "overlay.json"
    if not datei.exists():
        raise Failed(f"{datei} fehlt -- ohne sie laesst sich die Erklaerung nicht "
                     "pruefen.")
    d = json.loads(datei.read_text(encoding="utf-8"))
    if wert is None:
        d.pop("disclosure_ack", None)
    else:
        d["disclosure_ack"] = wert
    datei.write_text(json.dumps(d, indent=2, ensure_ascii=False), encoding="utf-8")


def _verwaiste_beenden(ordner: Path) -> int:
    """Uebriggebliebene PRUEFKOPIEN wegraeumen, bevor gemessen wird -- nur aus `ordner`.

    WARUM: am 2026-09-15 scheiterte ein Bau mit "gestorben (Code 0)", waehrend eine
    Instanz von 10:08 noch lief -- ein Rest aus einer frueheren Pruefung, deren
    CloseMainWindow nicht durchkam. Eine Messung gegen einen unbekannten Mitbewerber
    ist keine Messung.

    NUR AUS DEM EIGENEN ORDNER (seit 2026-09-27). Bis dahin wurde JEDER Prozess
    dieses Namens beendet -- auch die App, mit der der Nutzer gerade spielte. Der
    geplante Bau um 15:00 schoss sie so mitten in einer Meisterschaft ab. Eine
    installierte Kopie darf daneben laufen: sie belegt zwar UDP 5300, aber die
    Pruefkopie zeigt dann nur "Could not listen" und laeuft weiter, und seit
    Einzelinstanz gilt der Riegel je Programmordner.
    """
    wurzel = str(ordner.resolve()).replace("'", "''")
    auswahl = ("Get-Process -Name 'FH Companion','Forza Grip Haptics' -ErrorAction SilentlyContinue"
               f" | Where-Object {{ $_.Path -and $_.Path.StartsWith('{wurzel}', "
               "[System.StringComparison]::OrdinalIgnoreCase) }")
    script = (f"{auswahl} | ForEach-Object {{ $_.Kill() }}; "
              "Start-Sleep -Milliseconds 500; "
              f"({auswahl} | Measure-Object).Count")
    try:
        r = run(["powershell.exe", "-NoProfile", "-Command", script], timeout=60)
        zeilen = (r.stdout or "").strip().splitlines()
        return int(zeilen[-1]) if zeilen and zeilen[-1].strip().isdigit() else 0
    except Exception:
        return 0


def _start_und_titel(exe: Path, sekunden: int = 10) -> str:
    """Starten, warten, Fenstertitel holen, hoeflich schliessen.

    Ueber PowerShell, weil nur damit das Fenster hoeflich zugemacht werden kann:
    `Kill` waere ein Abschuss, und die App schickt beim Schliessen absichtlich noch
    den Nullbefehl an den Controller.

    Minimiert, weil dieser Bau auch aus dem Aufgabenplaner kommt: ein Fenster, das
    sich mitten im Rennen nach vorne holt, ist schlimmer als kein Fenster. Der Titel
    ist auch minimiert lesbar, der Beweis bleibt derselbe.
    """
    # DEN TITEL BEOBACHTEN, NICHT EINMAL ABLESEN.
    #
    # Vorher wurde er genau einmal nach zehn Sekunden gelesen. Am 2026-09-15 kam
    # dabei ein leerer Titel heraus, weil der Erklaerungsdialog in genau diesem
    # Augenblick weggeklickt wurde und das Hauptfenster noch nicht stand -- der Bau
    # brach ab, obwohl mit dem Programm alles stimmte.
    #
    # Jetzt wird im halben Sekundentakt nachgesehen und der ERSTE nicht leere Titel
    # behalten. Ein Fenster, das je einen Titel hatte, gilt damit als aufgegangen,
    # egal was danach damit geschieht.
    script = f"""
$p = Start-Process -FilePath '{exe}' -WindowStyle Minimized -PassThru
$title = ''
for ($i = 0; $i -lt {sekunden * 2}; $i++) {{
  Start-Sleep -Milliseconds 500
  $p.Refresh()
  if ($p.HasExited) {{ break }}
  if ($title -eq '' -and $p.MainWindowTitle -ne '') {{ $title = $p.MainWindowTitle }}
}}
$p.Refresh()
if ($p.HasExited -and $title -eq '') {{ 'DEAD ' + $p.ExitCode; exit 0 }}
if (-not $p.HasExited) {{
  $null = $p.CloseMainWindow()
  Start-Sleep -Seconds 3
  $p.Refresh()
  if (-not $p.HasExited) {{ $p.Kill() }}
}}
'ALIVE ' + $title
"""
    _verwaiste_beenden(exe.parent)
    result = run(["powershell.exe", "-NoProfile", "-Command", script], timeout=120)
    zeilen = (result.stdout or "").strip().splitlines()
    return zeilen[-1].strip() if zeilen else ""


def gui_check(root: Path) -> None:
    """Das Fenster wirklich aufmachen.

    Die kopflosen Pruefungen oben sagen nichts ueber den Programmstart. Genau daran
    lag es am 2026-08-27: die Rivals-Registerkarte rief `BeginInvoke` in ihrem
    Konstruktor, also bevor es ein Fensterhandle gab, und das Programm starb vor dem
    ersten Fenster -- monatelang unbemerkt, weil jeder Test kopflos lief und die
    kopflosen Pfade VOR dem WinForms-Aufbau abzweigen.

    Zehn Sekunden Leben plus ein Fenstertitel sind der Beweis, dass die
    Nachrichtenschleife laeuft. Ein Absturz beim Aufbau endet in unter einer Sekunde.

    ZWEI STARTS SEIT 2026-09-15. Die App zeigt beim ersten Start die Erklaerung
    (siehe `Disclosure.cs`) und laesst `MainForm` erst danach zu. Ein einziger Start
    saehe jetzt nur noch den Erklaerungsdialog, meldete ALIVE -- und liesse genau die
    Regression durch, gegen die diese Pruefung gebaut wurde.
    """
    exe = root / "app" / APP_EXE_NAME
    fassung = disclosure_version()

    # 1. Ohne Zustimmung MUSS die Erklaerung kommen.
    _ack_setzen(root, None)
    erster = _start_und_titel(exe)
    if erster.startswith("DEAD"):
        # EIN zweiter Versuch, mehr nicht. Eine Fensterpruefung haengt an Fokus,
        # Ports und Zeit -- Dinge, die einmal danebengehen koennen, ohne dass am
        # Programm etwas falsch ist. Zweimal hintereinander ist dagegen ein Befund.
        say("  der erste Startversuch endete sofort -- ein zweiter Versuch")
        erster = _start_und_titel(exe, sekunden=15)
    _pruefe_lebt(erster, "der erste Start (Erklaerung)")
    titel = erster[6:].strip()
    if "does" not in titel.lower():
        raise Failed(
            f"Beim ersten Start kam nicht die Erklaerung, sondern {titel!r}.\n"
            "  Ohne sie laeuft das Programm bei einem neuen Nutzer los, bevor er "
            "weiss, dass es fremden Speicher liest und Daten holt.")
    say(f"  erster Start zeigt die Erklaerung: {titel}")

    # 2. Mit Zustimmung MUSS das Hauptfenster kommen.
    _ack_setzen(root, fassung)
    zweiter = _start_und_titel(exe)
    _pruefe_lebt(zweiter, "der zweite Start (Hauptfenster)")
    titel = zweiter[6:].strip()
    if "does" in titel.lower():
        raise Failed("Auch nach der Zustimmung kam wieder die Erklaerung -- "
                     "sie wird nicht gespeichert.")
    say(f"  Fenster kam hoch: {titel or '(ohne Titel)'}")

    # 3. So ausliefern, wie ein neuer Nutzer es bekommen soll: ohne Zustimmung.
    _ack_setzen(root, None)


def _pruefe_lebt(answer: str, was: str) -> None:
    if answer.startswith("DEAD"):
        code = answer.split(" ", 1)[1] if " " in answer else "?"
        raise Failed(
            f"Das Programm ist beim Start gestorben (Code {code}) -- {was}.\n"
            "  Der Grund steht im Windows-Ereignisprotokoll unter Anwendung, "
            f"Quelle '.NET Runtime'. Diese Zeile holt ihn:\n"
            "    powershell -c \"Get-WinEvent -FilterHashtable "
            "@{LogName='Application'} | ? ProviderName -match '.NET Runtime' "
            "| select -First 1 -Expand Message\"")
    if not answer.startswith("ALIVE"):
        raise Failed(f"{was}: nichts Verwertbares gesagt: {answer!r}")



# --------------------------------------------------------------------------- #
# 6. ZIP


def build_id(files: list[Path], root: Path) -> str:
    """Eine Kennung, die den INHALT des Pakets beschreibt.

    Nicht das Datum: zwei Pakete desselben Tages sind verschieden, und ein
    zurueckgenommener Stand traegt ein aelteres Datum als das, was jemand schon
    installiert hat. Der Selbstaktualisierer fragt "ist das ein ANDERES Paket als
    meines", und darauf antwortet nur der Inhalt.

    Nicht die Pruefsumme der ZIP: die kann nicht IN der ZIP liegen. Also ueber die
    Dateien, bevor sie eingepackt werden -- Name und Inhalt, in fester Reihenfolge.
    """
    h = hashlib.sha256()
    for p in files:
        h.update(p.relative_to(root).as_posix().encode("utf-8"))
        h.update(b"\0")
        with p.open("rb") as f:
            for stueck in iter(lambda: f.read(1 << 20), b""):
                h.update(stueck)
    return h.hexdigest()[:16]


def make_zip(root: Path, facts: dict, keep: int = 5) -> Path:
    stamp = datetime.now().strftime("%Y%m%d")
    archive = root.parent / f"{root.name}-{stamp}.zip"
    if archive.exists():
        archive.unlink()

    # DIE KENNUNG KOMMT VOR DEM EINPACKEN, ABER IN DAS PAKET HINEIN.
    #
    # Die installierte App muss sagen koennen, welche Fassung sie ist -- sonst kann
    # sie nicht wissen, ob die auf dem Server eine andere ist. Darum wird erst der
    # Inhalt gehasht, dann app.meta.json geschrieben, dann gepackt. Die Metadatei
    # selbst zaehlt nicht mit (sie kann sich nicht selbst enthalten).
    vorher = [p for p in sorted(root.rglob("*")) if p.is_file()
              and p.name != "app.meta.json"]
    kennung = build_id(vorher, root)
    (root / "app.meta.json").write_text(json.dumps({
        "build": kennung,
        "built_at": datetime.now().astimezone().isoformat(timespec="seconds"),
        "name": archive.name,
        "_": "Welche Fassung dieser Ordner ist. Der Selbstaktualisierer vergleicht "
             "'build' mit dem, was der Server unter /api/haptics nennt.",
    }, indent=1, ensure_ascii=False), encoding="utf-8")

    files = [p for p in sorted(root.rglob("*")) if p.is_file()]
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as zf:
        for path in files:
            # Mit dem Ordnernamen als Wurzel: entpackt man die ZIP mitten in den
            # Downloads-Ordner, liegen sonst 200 Laufzeitdateien darin herum.
            zf.write(path, Path(root.name) / path.relative_to(root))
    # Ein fester Name daneben, damit ein Lesezeichen, ein Chatlink oder ein
    # Kopierbefehl nicht jeden Tag angepasst werden muss.
    latest = root.parent / f"{root.name}-latest.zip"
    shutil.copy2(archive, latest)

    # Der Zettel NEBEN der ZIP: was der Server unter /api/haptics ausgibt. Er
    # traegt dieselbe Kennung wie die Datei im Paket -- damit vergleicht die App
    # Gleiches mit Gleichem, statt sich auf Dateidaten zu verlassen, die eine
    # Uebertragung veraendern kann.
    roh = latest.read_bytes()
    (latest.with_suffix(".zip.meta.json")).write_text(json.dumps({
        "build": kennung,
        "built_at": datetime.now().astimezone().isoformat(timespec="seconds"),
        "name": archive.name,
        "bytes": len(roh),
        "sha256": hashlib.sha256(roh).hexdigest(),
        # DIE UNTERSCHRIFT. Ohne sie verweigert die App seit 2026-09-24 jedes
        # Update -- die Pruefsumme allein kam vom selben Server wie die Datei.
        "sig": sign_package(root, latest),
    }, indent=1, ensure_ascii=False), encoding="utf-8")

    # Taeglich 59 MB laufen sonst voll. Der feste Name wird nie mitgezaehlt.
    dated = sorted(root.parent.glob(f"{root.name}-????????.zip"))
    for old in dated[:-keep] if keep > 0 else []:
        old.unlink(missing_ok=True)
        say(f"  alter Stand entfernt: {old.name}")

    say(f"  ZIP: {archive.name} · {archive.stat().st_size / 1e6:.0f} MB "
        f"({len(files)} Dateien), auch als {latest.name}")
    return archive


# --------------------------------------------------------------------------- #


def app_ist_veraltet(root: Path) -> bool:
    """Ist der Quelltext der App neuer als die Binaerdatei im Paket?

    ## Warum diese Warnung eine ganze Auslieferung wert ist

    `--data-only` tauscht absichtlich nur den Datenbestand und ruehrt `dotnet
    publish` nicht an -- ein voller Bau nach jedem Board waere Verschwendung. Die
    Bau-Kennung im Beipackzettel wird dabei trotzdem neu vergeben, weil sich die
    Daten ja geaendert haben.

    Genau daraus entsteht eine Falle, in die ich am 2026-09-14 gelaufen bin: ich
    habe den ganzen Nachmittag an der App gearbeitet, der Sweep hat nach jedem Board
    brav ein Paket mit NEUER Kennung gebaut und ausgeliefert -- und darin lag die
    Binaerdatei von 15:26, ohne eine einzige der Aenderungen. Nach aussen sah es aus
    wie ein Update. Der Selbstaktualisierer haette es geladen und nichts bekommen.

    Die Warnung kostet nichts und macht den Unterschied zwischen "die Daten sind
    frisch" und "die App ist frisch" sichtbar. Abgebrochen wird NICHT: ein frischer
    Datensatz ist auch mit alter App richtig, und ein Sweep um drei Uhr nachts soll
    daran nicht scheitern.
    """
    try:
        binaer = max((f.stat().st_mtime for f in (root / "app").glob("*.dll")),
                     default=0.0)
        quelle = 0.0
        neuster = None
        for f in (WORKSPACE / "haptics").rglob("*.cs"):
            # bin/ und obj/ sind Bauergebnisse, kein Quelltext.
            if "bin" in f.parts or "obj" in f.parts:
                continue
            m = f.stat().st_mtime
            if m > quelle:
                quelle, neuster = m, f
    except OSError:
        return False
    if quelle <= binaer or neuster is None:
        return False
    from datetime import datetime
    say("  ACHTUNG: die App im Paket ist vom %s, der Quelltext vom %s"
        % (datetime.fromtimestamp(binaer).strftime("%Y-%m-%d %H:%M"),
           datetime.fromtimestamp(quelle).strftime("%Y-%m-%d %H:%M")))
    say("           (%s). --data-only baut die App NICHT neu."
        % neuster.relative_to(WORKSPACE))
    say("           Fuer Programmaenderungen einmal ohne --data-only bauen.")
    return True


def refresh_data_only(root: Path, zip_it: bool, keep: int = 5) -> int:
    """Nur den Datenbestand im schon gebauten Paket austauschen.

    Ein vollstaendiger Bau dauert Minuten und schreibt 156 MB neu, nur damit sieben
    Megabyte JSON aktuell sind. Der Sweep baut den Auswertungsbestand alle fuenf
    Boards neu -- also muss das Auffrischen billig sein, sonst passiert es nie und
    die weitergegebene ZIP ist Wochen alt.

    Die Binaerdateien bleiben unberuehrt; geprueft wird trotzdem, denn sie muessen
    die neue Datei ja auch finden und laden koennen.
    """
    exe = root / "app" / APP_EXE_NAME
    if not exe.exists():
        raise Failed(f"Es gibt noch kein Paket in {root}. Einmal ohne --data-only bauen.")
    app_ist_veraltet(root)
    if not DATASET.exists():
        raise Failed(f"Der Datensatz fehlt: {DATASET}")

    facts = copy_dataset(root)
    # Die Karten sind Daten wie der Bestand -- auch sie werden hier aufgefrischt.
    copy_route_maps(root)
    copy_car_list(root)
    # Auch die LIESMICH nennt Boardzahl, Runden und Baustand. Nur die Daten zu
    # tauschen hiesse, ein Paket auszuliefern, das sich selbst falsch beschreibt --
    # und zwar mit Zahlen, die jemand nachrechnen kann.
    settings = root / "config" / "overlay.json"
    server = DEFAULT_SERVER
    if settings.exists():
        try:
            server = json.loads(settings.read_text(encoding="utf-8")).get(
                "dataset_url", DEFAULT_SERVER)
            if OLD_DEFAULT_SERVER and server.rstrip("/") == OLD_DEFAULT_SERVER:
                server = DEFAULT_SERVER
        except Exception:
            pass
    write_docs(root, facts, (root / "app" / "System.Windows.Forms.dll").exists(),
               server)
    verify(root, facts)
    say(f"  Ordner: {root} · {folder_size(root) / 1e6:.0f} MB")
    if zip_it:
        make_zip(root, facts, keep)
    return 0


def needs_rebuild(root: Path) -> str | None:
    """Muessen die Binaerdateien neu -- und warum? Sonst None.

    Das ist die ganze Entscheidung hinter `--auto`: ein voller Bau schreibt 156 MB
    und dauert Minuten, das Auffrischen der Daten zwanzig Sekunden. Wer periodisch
    packt, will das Erste nur nach einer Code-Aenderung.
    """
    exe = root / "app" / APP_EXE_NAME
    if not exe.exists():
        return "es gibt noch kein Paket"
    packaged = exe.stat().st_mtime
    sources: list[Path] = []
    for pattern in ("*.cs", "*.csproj"):
        sources += [q for q in (WORKSPACE / "haptics").rglob(pattern)
                    if "obj" not in q.parts and "bin" not in q.parts]
    newer = sorted((q for q in sources if q.stat().st_mtime > packaged),
                   key=lambda q: -q.stat().st_mtime)
    if newer:
        return (f"{len(newer)} Quelldatei(en) neuer als das Paket, "
                f"zuletzt {newer[0].name}")
    if SDL_DLL.exists() and SDL_DLL.stat().st_mtime > packaged:
        return "SDL3.dll ist neuer als das Paket"
    return None


def packaged_version(root: Path) -> str | None:
    try:
        meta = root / "data" / "analytics" / "laps.meta.json"
        return json.loads(meta.read_text(encoding="utf-8")).get("version")
    except Exception:
        return None


def folder_size(root: Path) -> int:
    return sum(p.stat().st_size for p in root.rglob("*") if p.is_file())


def forza_laeuft() -> bool:
    """Laeuft das Spiel gerade? Dann wird am Rechner gespielt."""
    try:
        r = run(["tasklist", "/FI", "IMAGENAME eq forzahorizon6.exe", "/NH"], timeout=30)
        return "forzahorizon6.exe" in (r.stdout or "").lower()
    except Exception:
        return False


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--out", default=str(DEFAULT_OUT),
                        help="Zielordner (Standard: dist/fh-companion)")
    parser.add_argument("--framework-dependent", action="store_true",
                        help="ohne .NET-Laufzeit packen (3 MB statt 160)")
    parser.add_argument("--server", default=DEFAULT_SERVER,
                        help="Adresse fuer Aktualisierungen; leer = niemals fragen")
    parser.add_argument("--no-zip", action="store_true",
                        help="nur den Ordner bauen")
    parser.add_argument("--no-gui-check", action="store_true",
                        help="das Fenster nicht aufmachen (es blitzt kurz auf)")
    parser.add_argument("--data-only", action="store_true",
                        help="nur den Datenbestand im vorhandenen Paket auffrischen")
    parser.add_argument("--auto", action="store_true",
                        help="selbst entscheiden: voller Bau, Datentausch oder nichts")
    parser.add_argument("--keep", type=int, default=5,
                        help="so viele datierte ZIPs behalten (0 = alle)")
    args = parser.parse_args(argv)

    # NICHT WAEHREND GESPIELT WIRD (seit 2026-09-27). Der geplante Lauf baut nach einer
    # Code-Aenderung voll -- mit zwei Fensterstarts zur Pruefung. Um 15:00 geschah das
    # mitten in einer Meisterschaft. Der naechste Lauf in drei Stunden versucht es
    # wieder; von Hand (ohne --auto) baut es weiterhin sofort.
    if args.auto and forza_laeuft():
        say("Forza laeuft -- dieser Lauf wird ausgelassen, der naechste versucht es wieder.")
        return 0

    self_contained = not args.framework_dependent
    root = Path(args.out)
    if not root.is_absolute():
        root = WORKSPACE / root

    try:
        take_lock()
    except Busy as busy:
        # Kein Fehlschlag: der andere Lauf macht genau diese Arbeit gerade.
        say(f"Ein anderer Lauf ist dran -- {busy}. Nichts zu tun.")
        return 0

    try:
        if args.auto:
            reason = needs_rebuild(root)
            if reason is None:
                current = dataset_version(DATASET.read_bytes()) if DATASET.exists()                           else None
                zipped = root.parent / f"{root.name}-latest.zip"
                if current is not None and packaged_version(root) == current                         and zipped.exists():
                    say(f"Schon aktuell: Fassung {current}, "
                        f"{zipped.name} vom "
                        f"{datetime.fromtimestamp(zipped.stat().st_mtime):%Y-%m-%d %H:%M}. "
                        "Nichts zu tun.")
                    return 0
                say("Datenbestand auffrischen (Binaerdateien sind aktuell)")
                return refresh_data_only(root, zip_it=not args.no_zip, keep=args.keep)
            say(f"Voller Bau: {reason}")

        if args.data_only:
            say("Datenbestand auffrischen")
            return refresh_data_only(root, zip_it=not args.no_zip, keep=args.keep)

        say("Voraussetzungen")
        preflight(self_contained)

        say("Bauen")
        if root.exists():
            shutil.rmtree(root)
        publish(root / "app", self_contained)

        say("Daten und Einstellungen")
        facts = copy_dataset(root)
        copy_route_maps(root)
        copy_car_list(root)
        copy_config(root, args.server)
        write_docs(root, facts, self_contained, args.server)

        say("Pruefen")
        verify(root, facts)
        if not args.no_gui_check:
            gui_check(root)

        say("Fertig")
        say(f"  Ordner: {root} · {folder_size(root) / 1e6:.0f} MB")
        if not args.no_zip:
            make_zip(root, facts, args.keep)
    except Failed as failure:
        say("")
        say(f"ABBRUCH: {failure}")
        return 1
    except subprocess.TimeoutExpired as timeout:
        say("")
        say(f"ABBRUCH: {timeout.cmd[0]} hat nicht geantwortet.")
        return 1
    finally:
        drop_lock()
    return 0


if __name__ == "__main__":
    sys.exit(main())
