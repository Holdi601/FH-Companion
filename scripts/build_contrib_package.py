"""Das Paket bauen, das ein Freund bekommt.

    python scripts/build_contrib_package.py kai
    python scripts/build_contrib_package.py kai --server https://your-server.example.org:8787

Legt fuer `kai` ein Geheimnis an (falls noch keins da ist), sammelt genau die
Dateien ein, die der Scanner auf einem fremden Rechner braucht, und schreibt eine
ZIP mit `start.cmd`.

## Warum ein Verzeichnis voll lesbarer Dateien und keine .exe

Eine selbstgebaute .exe waere rund 300 MB (OCR-Modelle und Laufzeit muessen mit
hinein) und schlaegt bei Virenscannern regelmaessig an. Wer ein fremdes Programm
laufen laesst, das Tastendruecke schickt und den Bildschirm filmt, sollte
nachlesen koennen, was es tut. Hier ist alles Text.

## Was im Paket steckt und was nicht

Drin: der Scanner, die Bildschirmleser, die zwei PowerShell-Skripte fuer Tasten und
Aufnahme, der Streckenkatalog, und die persoenliche Konfiguration mit dem
Geheimnis.

Nicht drin: alles, was mit der VM zu tun hat, die Auswertung, der Server, die
Schluessel anderer Beitragender. Der Scanner faehrt hier ausschliesslich lokal.
"""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
import zipfile
from datetime import datetime
from pathlib import Path

HERE = Path(__file__).resolve().parent
WORKSPACE = HERE.parent
# Seit der Trennung liegt der Server-Code in server/. Dieses Bauskript bleibt in
# scripts/, weil es den SCANNER einpackt -- und der lebt hier, mit allem, was er an
# OCR und Fernsteuerung braucht. Es greift nur fuer das Abgabeformat und die
# Schluesselverwaltung hinueber.
SERVER = WORKSPACE / "server"
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(SERVER))

import contrib_format as fmt  # noqa: E402
import contrib_keys  # noqa: E402

# Was im Paket neben dem Scanner liegen muss, aber im Server-Ordner gepflegt wird.
# Es gibt bewusst nur EINE Fassung von contrib_format: der Beitragende schnuert die
# ZIP damit, der Server prueft sie damit. Zwei Kopien waeren zwei Formate, sobald
# eine davon einmal nicht mitgezogen wird.
AUS_SERVER = {"contrib_format.py", "local_settings.py"}


def quelle_von(name: str) -> Path:
    return (SERVER if name in AUS_SERVER else HERE) / name

# Der Scanner und alles, was er auf einem fremden Rechner anfasst. Diese Liste ist
# der einzige Ort, an dem sie steht -- fehlt hier etwas, faellt es beim Freund auf
# und nicht hier, deshalb prueft `verify_imports()` unten dagegen.
PYTHON_FILES = [
    "contrib_scan.py",
    "contrib_format.py",
    # Liest config/local.json. Das Paket bekommt eine eigene mit nur der Serveradresse
    # (siehe _build_files) -- so kennt der Scanner seinen Server, ohne dass die
    # Adresse im Quelltext steht.
    "local_settings.py",
    "local_target.py",
    "ocr_board_sweep.py",
    # Die Autonamen-Zuordnung. Sie gruppiert die BELEGBILDER nach dem zugeordneten
    # Auto statt nach dem rohen Bildschirmnamen. Ohne sie faellt ocr_board_sweep in
    # seinen `except`-Zweig zurueck und gruppiert nach dem rohen Namen -- davon gibt
    # es je Board rund 1.900 Varianten ("Acura Integra", "I| Acura Integra"), und
    # jede behaelt dann ihre eigenen zehn Belege. Die Abgabe eines Beitragenden
    # waere damit etwa dreimal so gross wie noetig, und anders gruppiert als die des
    # Hauptrechners. 51 KB Skript plus 146 KB Katalog sind das wert.
    "build_car_roster.py",
    "ocr_leaderboard_frames.py",
    "ocr_corrections.py",
    "detect_leaderboard_scrollbar.py",
    "screen_reader.py",
    # Die OCR-Maschine selbst. `screen_reader.Reader.engine` holt sie sich von hier;
    # ohne sie liest das Standbild nichts, und der Scanner koennte die Boardlaenge
    # nicht messen.
    "extract_leaderboard.py",
    # Der Kategoriewaehler mit Sichtpruefung des gelben Rahmens. Ohne ihn kommt ein
    # fremder Rechner nicht verlaesslich aus Road Racing heraus: blindes
    # Tastendruecken hat am 2026-08-28 dreimal die falsche Kategorie geoeffnet.
    # ACHTUNG: er greift zur Zeit ueber die VM-Skripte zu und ist auf einem fremden
    # Rechner darum noch nicht benutzbar -- er liegt bei, damit die Anleitung darauf
    # verweisen kann, und die Anleitung sagt bis dahin "Kategorie im Spiel selbst
    # oeffnen".
    "select_rivals_category.py",
    # Die Sprachwahl. Ohne sie waere das Werkzeug beim Empfaenger wieder
    # einsprachig -- und zwar englisch, was besser als deutsch, aber schlechter
    # als seine eigene Sprache ist.
    "i18n.py",
]

# Module, die eine mitgelieferte Datei importiert, die aber bewusst FEHLEN. Jeder
# Eintrag braucht eine Begruendung -- eine stumme Ausnahmeliste ist genau der Ort, an
# dem spaeter ein wirklich fehlendes Modul untergeht.
DELIBERATELY_ABSENT = {
    # Nur in `screen_reader.main()`, also in dessen Kommandozeile. Der Scanner ruft
    # sie nie auf. Mitzuliefern hiesse, die ganze Auswertungskette anzuhaengen.
    "rivals_advisor": "nur in der Kommandozeile von screen_reader, nie im Scanner",
}
POWERSHELL_FILES = [
    "forza_navigator.ps1",
    "press_down_continuously.ps1",
    "capture_leaderboard_frames.ps1",
]
import local_settings  # noqa: E402

DEFAULT_SERVER = local_settings.server()

CONFIG_FILES = [
    "config/fh6_board_catalogue.json",
    "config/overlay.json",
    # Der Autokatalog, aus dem build_car_roster seine Namen nimmt. Der Pfad ist in
    # ocr_board_sweep fest verdrahtet ("data/car_catalogue/fh6cars.html") und
    # relativ zum Arbeitsverzeichnis -- start.cmd setzt das auf den Ordner des
    # Werkzeugs, also liegt die Datei hier richtig.
    "data/car_catalogue/fh6cars.html",
]

# Die Sprachdateien. Nicht einzeln aufgezaehlt: es kommen welche dazu, und eine
# Liste, die man beim Hinzufuegen vergisst, faellt nicht auf -- das Werkzeug ist
# dann einfach wieder englisch. `_keys.json` bleibt draussen, das ist Werkzeug.
LANG_GLOB = "config/lang/*.json"

# Der ganze erste Start in einer Datei: Python finden oder installieren, Umgebung
# anlegen, Bibliotheken holen, loslegen.
#
# Drei Fallen stecken hier drin, die jeweils erst auf einem fremden Rechner
# auftauchen:
#
# 1. `where python` findet auf Windows 11 fast immer etwas -- naemlich den
#    Platzhalter aus dem Microsoft Store, der bloss den Store aufmacht und mit
#    9009 endet. Deshalb wird nicht auf Vorhandensein geprueft, sondern die
#    Fassung wirklich abgefragt.
# 2. `py -3` ist zuverlaessiger als `python`: der Starter liegt in Windows selbst
#    und weiss, welche Fassungen installiert sind. Also zuerst der, dann `python`.
# 3. Nach einer Installation ueber winget kennt das LAUFENDE Fenster die neue
#    PATH-Eintragung noch nicht -- `python` bleibt unauffindbar. `py` liegt aber
#    in C:\Windows und ist sofort da, also wird danach genau damit weitergemacht.
# ENGLISCH, wie README.md und aus demselben Grund: dieses Fenster ist das erste,
# was jemand sieht, der das Werkzeug von der Seite geladen hat, und die Seite ist
# englisch. Bis zum 2026-09-19 stand hier Deutsch, samt einer Abfrage
# "choice /c JN", deren Text "Installieren" hiess und deren Tasten J und N waren --
# fuer jeden, der kein Deutsch kann, eine Raterei.
#
# Die Erklaerung steht HIER und nicht als rem im Literal: alles im Literal wird
# mitgeliefert, und ein deutscher Kommentar in einer Datei, die ausdruecklich fuer
# Fremde englisch gemacht wurde, waere genau derselbe Fehler nochmal.
#
# Die Ausgaben des SCANNERS sind davon unberuehrt: der laeuft in Python, benutzt
# config/lang und spricht alle 25 Sprachen. Eine .cmd kann das nicht, also sagt
# sie das Noetige einmal auf Englisch und uebergibt.
START_CMD = r"""@echo off
setlocal EnableDelayedExpansion
cd /d "%~dp0"

echo.
echo   Forza Rivals -- contribution scanner
echo.

set "PY="
call :try_python "py -3"
if not defined PY call :try_python "python"

rem  NICHT NUR DER PATH. Am 2026-09-19 auf der Testmaschine gemessen: dort lag
rem  Python 3.12.10 unter C:\ForzaTools\Python312, und im PATH stand nur der
rem  Platzhalter aus dem Microsoft Store -- der beim Aufruf den Store oeffnet und
rem  fuer die Versionspruefung einen Fehler liefert. Ergebnis: "Python 3.10 or
rem  newer is needed and cannot be found here", auf einem Rechner MIT Python.
rem
rem  Wer Python aus dem Installer ohne "Add to PATH" installiert hat -- und das
rem  ist die Voreinstellung fuer Alle-Benutzer-Installationen -- saehe dasselbe.
rem  Also erst in der Registry nachsehen, dann an den ueblichen Orten, und erst
rem  danach anbieten, etwas zu installieren, das laengst da ist.
if not defined PY call :find_registry_python
if not defined PY call :find_common_python

if not defined PY (
  echo   Python 3.10 or newer is needed and cannot be found here.
  echo.
  where winget >nul 2>nul
  if errorlevel 1 goto :manual
  echo   I can install it now ^(via winget, the Windows package service^).
  echo   Windows will ask for permission once.
  echo.
  choice /c YN /m "  Install"
  if errorlevel 2 goto :manual
  echo.
  echo   Installing Python 3.12 ...
  winget install -e --id Python.Python.3.12 --accept-source-agreements --accept-package-agreements
  echo.
  call :try_python "py -3"
  if not defined PY (
    echo   Installed -- but this window does not know about it yet.
    echo   Close this window and run start.cmd again.
    pause
    exit /b 0
  )
)

echo   Python: !PY!
for /f "delims=" %%V in ('!PY! -c "import sys;print(sys.version.split()[0])"') do set "PYVER=%%V"
echo   Version: !PYVER!
echo.

call :ensure_tesseract
if errorlevel 1 goto :fail

if not exist ".venv\Scripts\python.exe" (
  echo   Setting up the environment on this first run. It takes a few minutes
  echo   and downloads around 300 MB -- never again after that.
  echo.
  !PY! -m venv .venv || goto :fail
  ".venv\Scripts\python.exe" -m pip install --upgrade pip --quiet || goto :fail
  ".venv\Scripts\python.exe" -m pip install -r requirements.txt || goto :fail
  echo.
  echo   Setup complete.
  echo.
)

rem Touch the libraries once for real. `pip install` can finish happily and the
rem reader still fail on the first board -- a failed import is worth ten seconds
rem here instead of half an hour later.
".venv\Scripts\python.exe" -c "import numpy, PIL, pandas, pyarrow, cv2, rapidocr, onnxruntime" 2>nul
if errorlevel 1 (
  echo   A library is missing or broken. Trying once more.
  ".venv\Scripts\python.exe" -m pip install -r requirements.txt || goto :fail
  ".venv\Scripts\python.exe" -c "import numpy, PIL, pandas, pyarrow, cv2, rapidocr, onnxruntime" || goto :fail
)

".venv\Scripts\python.exe" scripts\contrib_scan.py %*
echo.
pause
exit /b 0

:ensure_tesseract
rem  Tesseract ist ein eigenstaendiges Programm, kein pip-Paket -- requirements.txt
rem  kann es nicht mitbringen. Ohne es liest der Scanner keine einzige Zeile der
rem  Bestenliste, und das faellt erst NACH der Aufnahme auf: am 2026-09-19 nach
rem  300 MB Bibliotheken und 540 MB Bildern, also nach einer halben Stunde Arbeit.
rem
rem  Darum wird es hier geholt, statt den Nutzer wegzuschicken.
set "TESS="
for /f "delims=" %%T in ('where tesseract 2^>nul') do if not defined TESS set "TESS=%%T"
if not defined TESS if exist "%ProgramFiles%\Tesseract-OCR\tesseract.exe" set "TESS=%ProgramFiles%\Tesseract-OCR\tesseract.exe"
if not defined TESS if exist "%LOCALAPPDATA%\Tesseract-OCR\tesseract.exe" set "TESS=%LOCALAPPDATA%\Tesseract-OCR\tesseract.exe"
if defined TESS (
  echo   Tesseract: !TESS!
  echo.
  exit /b 0
)

echo   Tesseract OCR is missing. The leaderboard rows are read with it.
echo   It is a separate program, so the Python setup cannot fetch it.
echo.
echo   Installing it now ^(about 48 MB^). Windows may ask for permission.
echo.

rem  Erst winget, das ist der gepflegte Weg.
where winget >nul 2>nul
if not errorlevel 1 (
  winget install -e --id UB-Mannheim.TesseractOCR --accept-source-agreements --accept-package-agreements
)

call :locate_tesseract
if defined TESS goto :tess_done

rem  Sonst die Veroeffentlichung von GitHub. NICHT ueber digi.bib.uni-mannheim.de:
rem  dieser Spiegel antwortete am 2026-09-20 mit 403 Forbidden.
rem
rem  FESTGENAGELT auf EINE Fassung und ihre Pruefsumme (2026-09-24 geladen, signiert
rem  von der Universitaet Mannheim). Vorher lud dieses Skript "die neueste" und fuehrte
rem  sie still aus -- wuerde die Veroeffentlichung dort je ausgetauscht, liefe der
rem  Austausch bei jedem Beitragenden ohne winget. Stimmt die Summe nicht, wird NICHTS
rem  ausgefuehrt.
echo   Fetching the installer from GitHub ...
!PY! -c "import hashlib,os,urllib.request as u; f='tesseract-setup.exe'; u.urlretrieve('https://github.com/UB-Mannheim/tesseract/releases/download/v5.4.0.20240606/tesseract-ocr-w64-setup-5.4.0.20240606.exe', f); h=hashlib.sha256(open(f,'rb').read()).hexdigest(); ok=(h=='c885fff6998e0608ba4bb8ab51436e1c6775c2bafc2559a19b423e18678b60c9'); print('  checksum ok' if ok else '  CHECKSUM MISMATCH -- the installer was not run'); (None if ok else os.remove(f))"
if not exist "tesseract-setup.exe" (
  echo   The download did not work, or the installer was not the expected one.
  goto :tess_manual
)
rem  NSIS: /S still, /D als LETZTES und ohne Anfuehrungszeichen.
tesseract-setup.exe /S /D=%ProgramFiles%\Tesseract-OCR
del "tesseract-setup.exe" >nul 2>nul
call :locate_tesseract

:tess_done
if defined TESS (
  echo   Tesseract: !TESS!
  echo.
  exit /b 0
)

:tess_manual
echo.
echo   Tesseract could not be installed automatically.
echo   Please install it by hand: https://github.com/UB-Mannheim/tesseract/wiki
echo   Then close this window and run start.cmd again.
pause
exit /b 1

:locate_tesseract
set "TESS="
for /f "delims=" %%T in ('where tesseract 2^>nul') do if not defined TESS set "TESS=%%T"
if not defined TESS if exist "%ProgramFiles%\Tesseract-OCR\tesseract.exe" set "TESS=%ProgramFiles%\Tesseract-OCR\tesseract.exe"
if not defined TESS if exist "%LOCALAPPDATA%\Tesseract-OCR\tesseract.exe" set "TESS=%LOCALAPPDATA%\Tesseract-OCR\tesseract.exe"
exit /b 0

:try_python
rem Sets PY only if the call really starts a Python 3.10 or newer.
%~1 -c "import sys;sys.exit(0 if sys.version_info>=(3,10) else 1)" >nul 2>nul
if not errorlevel 1 set "PY=%~1"
exit /b 0

:find_registry_python
rem  Die Installer tragen ihren Pfad hier ein, unabhaengig vom PATH. Beide Hives:
rem  HKCU fuer "nur ich", HKLM fuer "alle Benutzer".
for %%H in (HKCU HKLM) do (
  for /f "tokens=*" %%K in ('reg query "%%H\SOFTWARE\Python\PythonCore" 2^>nul') do (
    for /f "tokens=2,*" %%A in ('reg query "%%K\InstallPath" /ve 2^>nul ^| findstr REG_SZ') do (
      if not defined PY call :try_python "%%Bpython.exe"
    )
  )
)
exit /b 0

:find_common_python
rem  Und die Stellen, an die die Installer ohne Registryeintrag legen.
for %%D in (
  "%LOCALAPPDATA%\Programs\Python"
  "C:\Program Files"
  "C:\"
  "C:\ForzaTools"
) do (
  if exist "%%~D" (
    for /d %%P in ("%%~D\Python3*") do (
      if not defined PY call :try_python "%%~P\python.exe"
    )
  )
)
exit /b 0

:manual
echo   Please fetch it by hand: https://www.python.org/downloads/
echo   Tick "Add python.exe to PATH" during the install.
echo   Then close this window and run start.cmd again.
pause
exit /b 1

:fail
echo.
echo   Setup failed. The message above says why.
echo   Most common cause: no internet, or a virus scanner blocking pip.
pause
exit /b 1
"""

# Genau die Pakete, die der Scanner anfasst -- und in den Fassungen, in denen er hier
# laeuft. Zwei Fallen stecken darin:
#
# `rapidocr` und `rapidocr-onnxruntime` sind ZWEI Pakete. Der Code importiert
# `rapidocr` (das neuere); mit dem alten laeuft die Einrichtung glatt durch und erst
# die erste Erkennung scheitert -- also beim Freund, mitten im Lauf.
#
# `onnxruntime` ohne `-gpu`: der Hauptrechner hat die GPU-Fassung, aber die verlangt
# CUDA. Auf einem fremden Rechner ist die CPU-Fassung die einzige, die ueberall
# laeuft, und die Erkennung ist ohnehin nicht der Engpass.
REQUIREMENTS = """numpy>=2.0
pillow>=10
pandas>=2.3
pyarrow>=15
opencv-python-headless>=4.10
rapidocr>=3.0
onnxruntime>=1.20
certifi>=2024.2.2
"""


def verify_imports(staged: Path) -> list[str]:
    """Jede Python-Datei im Paket auf Importe pruefen, die nicht mitgekommen sind.

    Ein vergessenes Modul faellt sonst erst beim Freund auf, mitten in einem
    ImportError, und er kann nichts daran reparieren. Geprueft wird gegen die
    Modulnamen im Paket und die Standardbibliothek plus die Pakete aus
    requirements.txt.
    """
    import ast

    shipped = {path.stem for path in staged.glob("scripts/*.py")}
    # Was requirements.txt mitbringt. Das sind die IMPORT-Namen, nicht die
    # Paketnamen -- cv2 kommt aus opencv-python-headless, PIL aus pillow.
    external = {"numpy", "PIL", "pandas", "pyarrow", "cv2",
                "rapidocr", "onnxruntime", "certifi"}
    missing: list[str] = []
    for path in sorted(staged.glob("scripts/*.py")):
        tree = ast.parse(path.read_text(encoding="utf-8"), filename=str(path))
        for node in ast.walk(tree):
            names = []
            if isinstance(node, ast.Import):
                names = [alias.name.split(".")[0] for alias in node.names]
            elif isinstance(node, ast.ImportFrom) and node.level == 0 and node.module:
                names = [node.module.split(".")[0]]
            for name in names:
                if (name in shipped or name in external
                        or name in DELIBERATELY_ABSENT
                        or name in sys.stdlib_module_names):
                    continue
                missing.append(f"{path.name} importiert {name!r}")
    return sorted(set(missing))


def sources_newest() -> float:
    """Neueste Aenderung an irgendetwas, das ins Paket wandert.

    Damit laesst sich sagen, ob eine gebaute ZIP noch aktuell ist -- dieselbe
    Ueberlegung wie beim Seitenaufbau: gebaut wird aus Code UND aus Daten, also
    zaehlen beide als Quelle.
    """
    newest = 0.0
    for name in PYTHON_FILES + POWERSHELL_FILES:
        try:
            newest = max(newest, quelle_von(name).stat().st_mtime)
        except OSError:
            continue
    for name in CONFIG_FILES:
        try:
            newest = max(newest, (WORKSPACE / name).stat().st_mtime)
        except OSError:
            continue
    for datei in WORKSPACE.glob(LANG_GLOB):
        try:
            newest = max(newest, datei.stat().st_mtime)
        except OSError:
            continue
    # Das Bauskript selbst zaehlt mit: start.cmd und requirements.txt stehen darin.
    try:
        newest = max(newest, Path(__file__).stat().st_mtime)
    except OSError:
        pass
    return newest


PUBLIC_ZIP = WORKSPACE / "dist/forza-contrib-tool.zip"


def ensure_public_package(target: Path | None = None) -> Path:
    """Die Fassung OHNE Zugangsdaten, neu gebaut wenn der Quelltext sich geruehrt hat.

    Diese darf oeffentlich heruntergeladen werden. Das personalisierte Paket darf das
    nicht: es traegt das Geheimnis des Beitragenden und das allgemeine
    Zugangspasswort, und wer es hat, kann Daten einspielen. Deshalb sind es zwei
    verschiedene Dateien und nicht eine mit einem Schalter -- ein Schalter waere
    genau die Art Unterscheidung, die man irgendwann falsch herum stellt.
    """
    target = target or PUBLIC_ZIP
    try:
        if target.exists() and target.stat().st_mtime >= sources_newest():
            return target
    except OSError:
        pass
    return build("", "", target.parent, WORKSPACE / "config/contrib_keys.json",
                 credentials=False, archive_name=target.name)


def build(contributor: str, server: str, out_dir: Path, keys_path: Path,
          *, credentials: bool = True, archive_name: str | None = None) -> Path:
    """Ein Paket bauen. `credentials=False` laesst die Zugangsdaten weg."""
    if not credentials:
        return _build_files(out_dir, "tool", server or DEFAULT_SERVER, None,
                            archive_name or PUBLIC_ZIP.name)

    fmt.check_contributor(contributor)

    keys = contrib_keys.load(keys_path)
    if contributor not in keys["keys"]:
        print(f"  lege ein Geheimnis fuer {contributor} an")
        subprocess.run([sys.executable, str(SERVER / "contrib_keys.py"),
                        "add", contributor, "--keys", str(keys_path)], check=True)
        keys = contrib_keys.load(keys_path)
    if not keys.get("upload_password"):
        print("  lege das allgemeine Zugangspasswort an")
        subprocess.run([sys.executable, str(SERVER / "contrib_keys.py"),
                        "gate", "--keys", str(keys_path)], check=True)
        keys = contrib_keys.load(keys_path)

    stamp = datetime.now().strftime("%Y%m%d")
    return _build_files(out_dir, contributor, server, {
        "contributor": contributor,
        "secret": keys["keys"][contributor],
        "upload_password": keys["upload_password"],
    }, f"forza-contrib-{contributor}-{stamp}.zip")


def _build_files(out_dir: Path, label: str, server: str,
                 credentials: dict | None, archive_name: str) -> Path:
    """Die Dateien einsammeln und einpacken.

    `credentials=None` schreibt eine Vorlage mit leeren Feldern statt der echten
    Zugangsdaten -- das ist die Fassung, die oeffentlich heruntergeladen werden darf.
    """
    out_dir.mkdir(parents=True, exist_ok=True)
    staged = out_dir / f"forza-contrib-{label}"
    shutil.rmtree(staged, ignore_errors=True)
    (staged / "scripts").mkdir(parents=True)
    (staged / "config").mkdir(parents=True)

    for name in PYTHON_FILES + POWERSHELL_FILES:
        source = quelle_von(name)
        if not source.exists():
            raise SystemExit(f"{source} fehlt -- das Paket waere unvollstaendig")
        shutil.copy2(source, staged / "scripts" / name)
    for name in CONFIG_FILES:
        source = WORKSPACE / name
        if not source.exists():
            raise SystemExit(f"{source} fehlt")
        if name == "config/overlay.json":
            # PERSOENLICHES BLEIBT HIER -- wie in build_haptics_package.copy_config.
            # Bis zum 2026-09-25 ging die Datei unveraendert mit, samt dem Gamertag
            # dieses Rechners. Und die Serveradresse ist die HTTPS-Vorgabe.
            einst = json.loads(source.read_text(encoding="utf-8-sig"))
            for persoenlich in ("gamertag", "disclosure_ack", "skipped_update",
                                "telemetry_from_lan"):
                einst.pop(persoenlich, None)
            if einst.get("dataset_url"):
                einst["dataset_url"] = DEFAULT_SERVER
            (staged / "config" / "overlay.json").write_text(
                json.dumps(einst, indent=2, ensure_ascii=False), encoding="utf-8")
            continue
        shutil.copy2(source, staged / "config" / Path(name).name)

    # Die Sprachdateien. `i18n.py` sucht `config/lang` von sich aus aufwaerts, also
    # muss der Ordner genauso heissen wie im Projekt.
    lang_ziel = staged / "config" / "lang"
    lang_ziel.mkdir(parents=True, exist_ok=True)
    gezaehlt = 0
    for datei in sorted(WORKSPACE.glob(LANG_GLOB)):
        if datei.name.startswith("_"):
            continue            # _keys.json ist Werkzeug, keine Sprache
        shutil.copy2(datei, lang_ziel / datei.name)
        gezaehlt += 1
    if gezaehlt == 0:
        raise SystemExit("keine Sprachdatei gefunden -- config/lang/ ist leer")

    if credentials:
        config = {
            "_": ("Deine persoenlichen Angaben. Das Geheimnis und das "
                  "Zugangspasswort werden beim Hochladen NICHT uebertragen -- sie "
                  "werden nur benutzt, um die Datei zu unterschreiben. Trotzdem: "
                  "nicht weitergeben."),
            **credentials, "server": server,
        }
    else:
        config = {
            "_": ("Hier gehoeren die drei Angaben hinein, die du bekommen hast. "
                  "Ohne sie kannst du scannen, aber nicht abgeben."),
            "contributor": "", "secret": "", "upload_password": "",
            "server": server,
        }
    (staged / "config" / "contrib_config.json").write_text(
        json.dumps(config, indent=1, ensure_ascii=False), encoding="utf-8")
    # Die Serveradresse fuer local_settings.py -- nur sie, nichts sonst aus der
    # config/local.json dieses Rechners.
    (staged / "config" / "local.json").write_text(
        json.dumps({"server": server}, indent=1), encoding="utf-8")

    (staged / "start.cmd").write_text(START_CMD, encoding="utf-8")
    (staged / "requirements.txt").write_text(REQUIREMENTS, encoding="utf-8")
    # README.md, nicht LIESMICH.md -- siehe readme().
    (staged / "README.md").write_text(
        readme(credentials["contributor"] if credentials else None, server),
        encoding="utf-8")

    problems = verify_imports(staged)
    if problems:
        print("  WARNUNG: das Paket ist unvollstaendig --")
        for line in problems:
            print(f"    {line}")

    archive = out_dir / archive_name
    archive.unlink(missing_ok=True)
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as bundle:
        for path in sorted(staged.rglob("*")):
            if path.is_file():
                bundle.write(path, path.relative_to(staged.parent))
    # Das ausgepackte Verzeichnis wird nicht gebraucht, und beim oeffentlichen Paket
    # laege es sonst dauerhaft neben der ZIP herum.
    shutil.rmtree(staged, ignore_errors=True)
    return archive


def readme(contributor: str | None, server: str) -> str:
    """Die Anleitung, die im Paket liegt.

    ENGLISCH, und die Datei heisst README.md. Das Werkzeug wird von der
    oeffentlichen Seite heruntergeladen, die Seite ist englisch, und wer sie
    laedt, muss kein Deutsch koennen. Bis zum 2026-09-18 hiess sie LIESMICH.md
    und war deutsch -- das war der einzige deutsche Rest im ganzen Weg vom
    Download bis zur Abgabe.
    """
    return f"""# Forza Rivals — scanning boards

Thanks for helping out. This reads leaderboards out of Forza Horizon 6 and sends
the rows back. It needs your PC for as long as a run takes.

## One-time setup

1. Install **Python** if you do not have it: <https://www.python.org/downloads/>
   — tick **"Add python.exe to PATH"** during the install. (If you already have
   Python somewhere else, `start.cmd` will find it.)
2. Unpack this folder anywhere.
3. Double-click `start.cmd`. The first run sets itself up
   (a few minutes, around 300 MB).

`start.cmd` also installs **Tesseract OCR** if it is missing — the leaderboard
rows are read with it, and it is a separate program rather than a Python package.
If the automatic install does not work, fetch it from
<https://github.com/UB-Mannheim/tesseract/wiki> and start again.

Everything is checked before a run begins, so anything missing costs you seconds
rather than half an hour of capturing.

## Before every run

- Forza is running and sitting in the **main menu**.
- **Borderless window, 1920×1080.** At any other resolution the columns sit
  somewhere else and the reader returns nonsense. The tool checks this and says so.
- Forza is in the **foreground**.

## Starting a run

Right-click inside the folder → "Open in Terminal", then:

    start.cmd --classes A --route-indices 0-3

That scans class A on the first four routes. Reckon on **20–25 minutes per board**
— so four boards is one to one and a half hours.

**The PC belongs to the scanner for that time.** It presses arrow keys and films
the screen, and both go to whatever window is in front. Click something else and
the keys go there instead, while the run reads a list that is standing still. So:
start it, walk away, come back. Stop any time with **Ctrl+C** — whatever finished
is kept.

Classes: `D C B A S1 S2 R`. Route numbers `0-22`, in carousel order.

## Handing it in

    start.cmd --pack-only --upload

That packs what has been scanned and uploads it to `{server}`. If that fails
(server down, no connection), the ZIP is left in `data/contrib_out/` and can simply
be sent.

## What gets transmitted

Only **rows**: rank, lap time, car, player name, and the flags of the run. No
screen captures — those are deleted after reading, and they would be between
266 MB and 2.2 GB per board.

Your secret and the upload password in `config/contrib_config.json` are **not**
transmitted. They only sign the file; the server recomputes the signature. So do
not pass that file on — but nothing is lost either if somebody reads along while
it uploads.

{("You are signed in as **" + contributor + "**.") if contributor else "**Not signed in yet.** `config/contrib_config.json` wants the three values "
"you were given: `contributor`, `secret` and `upload_password`. Without them "
"you can scan and send the ZIP by hand, but not upload."}

## When something jams

- **"Forza is not running"** — start the game, go to the main menu.
- **"The screen is …"** — set it to 1920×1080 in a borderless window.
- **"Server Error"** — Forza's leaderboards are unreachable right now. The tool
  backs off and tries again on its own; if it stops, try later.
- **A board reads almost nothing** — usually another window was in the foreground.
"""


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("contributor", nargs="?", default="",
                        help="Name des Beitragenden; bei --public egal")
    parser.add_argument("--server", default=DEFAULT_SERVER)
    parser.add_argument("--public", action="store_true",
                        help="die Fassung OHNE Zugangsdaten bauen, die "
                             "die Webseite zum Download anbietet")
    parser.add_argument("--out", type=Path, default=WORKSPACE / "dist")
    parser.add_argument("--keys", type=Path,
                        default=WORKSPACE / "config/contrib_keys.json")
    args = parser.parse_args(argv)

    args.out.mkdir(parents=True, exist_ok=True)
    if args.public:
        archive = ensure_public_package(args.out / PUBLIC_ZIP.name)
        print(f"\n  {archive}  ({archive.stat().st_size / 1e6:.1f} MB)")
        print("\nOhne Zugangsdaten -- diese Fassung darf oeffentlich liegen.")
        return 0
    archive = build(args.contributor, args.server, args.out, args.keys)
    print(f"\n  {archive}  ({archive.stat().st_size / 1e6:.1f} MB)")
    print("\nDas Paket enthaelt das persoenliche Geheimnis -- also ueber einen Weg\n"
          "schicken, dem du traust, und nicht oeffentlich ablegen.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
