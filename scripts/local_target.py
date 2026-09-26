"""Denselben Scanner auf dem Rechner laufen lassen, auf dem Forza selbst laeuft.

Der Sweep hier fernsteuert eine Hyper-V-VM: navigieren, filmen und ein Standbild
holen gehen alle ueber PowerShell Direct in den Gast. Ein Freund, der beitragen will,
hat keine solche VM und wird auch keine aufsetzen -- er hat Forza auf seinem
Spielrechner. Dieses Modul ist genau diese drei Uebergaenge, lokal.

**Es ist derselbe Scanner.** `ocr_board_sweep.scan_board` mit seiner ganzen
Erfahrung -- Ungueltig-Rand, Scrollbalken-Laenge, Nachzuegler, die Abbruch-Erkennung
von heute Nacht -- laeuft unveraendert. Nur woher die Bilder kommen ist anders. Waere
das hier eine zweite Umsetzung des Scanners, haetten wir binnen einer Woche zwei
Scanner, von denen einer still schlechtere Daten liefert.

## Was lokal einfacher ist

Im Gast muessen Tastendruck und Aufnahme als **geplante Aufgaben** starten, weil
Eingaben eine interaktive Sitzung brauchen und PowerShell Direct keine hat. Auf dem
eigenen Rechner ist die Sitzung interaktiv -- die Aufgaben entfallen, und damit auch
die Falle, dass ein abgebrochener Lauf einen weiterdrueckenden Tastendienst
hinterlaesst.

## Was lokal schwieriger ist

Der Rechner gehoert waehrenddessen dem Scanner. Jeder Tastendruck geht an das
Fenster im Vordergrund: wer waehrend eines Laufs anderswo hinklickt, schickt
Pfeiltasten in sein eigenes Fenster und der Lauf liest eine Bestenliste, die sich
nicht bewegt.
"""

from __future__ import annotations

import json
import subprocess
import sys
import time
import zipfile
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
SCRIPTS = WORKSPACE / "scripts"

# Der Wert, der statt eines VM-Namens durchgereicht wird. Ein Name, der als Hostname
# nicht vorkommen kann, damit ein Tippfehler nie versehentlich lokal laeuft.
LOCAL = "::local::"


def is_local(target: str) -> bool:
    return str(target) == LOCAL


# ---------------------------------------------------------------- Navigation

def navigate(category: str, route_index: int, klass: str, anchor: str,
             warm: bool, timeout: int = 1500) -> tuple[int, str]:
    """`forza_navigator.ps1` direkt aufrufen.

    Der Navigator kennt gar keine VM -- er hat weder einen -VMName-Schalter noch eine
    Remote-Sitzung, er wurde nur bisher immer in den Gast kopiert und dort gestartet.
    Hier laeuft er einfach da, wo er ohnehin hingehoert: neben dem Spiel.
    """
    # EIGENE LAUFKENNUNG, damit der Zustand danach zu FINDEN ist.
    # Ohne sie wuerfelt der Navigator sich eine aus, und der einzige Weg zur
    # state.json waere "der neueste Ordner" -- was bei zwei Laeufen kurz
    # hintereinander den falschen erwischt.
    run_id = time.strftime("%Y%m%d_%H%M%S") + "_local"
    out_root = WORKSPACE / "data/runtime/navigation"
    args = ["-RivalsMode", category, "-PerformanceClass", klass,
            "-RouteIndex", str(route_index), "-RouteAnchor", anchor,
            "-RouteCount", "23",
            "-RunId", run_id,
            "-OutputRoot", str(out_root)]
    if warm:
        args.append("-FromLeaderboard")
    result = subprocess.run(
        ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass",
         "-File", str(SCRIPTS / "forza_navigator.ps1"), *args],
        capture_output=True, text=True, errors="replace", timeout=timeout)
    output = (result.stdout or "") + (result.stderr or "")

    # DIE "status:"-ZEILE NACHREICHEN -- ohne sie ist dieser Weg NICHT derselbe.
    #
    # Der Navigator schreibt seinen Endzustand nach state.json und sagt auf der
    # Konsole nur "done: leaderboard_reached". Die Zeile "status: ..." kommt vom
    # VM-Umweg start_forza_navigation.ps1, der die state.json liest -- und GENAU
    # danach sucht ocr_board_sweep.navigate().
    #
    # Auf dem lokalen Weg gab es sie nie. Damit galt jedes Board als nicht
    # erreicht, auch wenn der Navigator "leaderboard reached" und den richtigen
    # Streckennamen gemeldet hatte: der Scanner versuchte es ein zweites Mal "vom
    # anderen Schirmzustand", stand dabei schon auf dem Board, bestaetigte nichts
    # mehr und schrieb "not reachable; next". Am 2026-09-19 auf Soni Circuit
    # dreimal hintereinander so gesehen, 0 Boards aus drei sauberen Laeufen.
    #
    # Betrifft nur das Beitrags-Werkzeug: der Hauptrechner faehrt ueber die VM und
    # bekommt die Zeile von dort.
    zustand = out_root / run_id / "state.json"
    if zustand.exists():
        # utf-8-SIG, NICHT utf-8. Der Navigator schreibt die Datei mit
        # `Set-Content -Encoding UTF8`, und das ist in PowerShell 5.1 UTF-8 MIT
        # Stueckliste. json.loads stolpert ueber das ﻿ am Anfang, die Ausnahme
        # lief in ein stilles `except: pass`, und die status-Zeile fehlte weiter --
        # bei einer state.json, die ausdruecklich "leaderboard_reached" sagte.
        # Am 2026-09-19 einen ganzen Lauf lang genau daran im Kreis gelaufen.
        try:
            daten = json.loads(zustand.read_text(encoding="utf-8-sig"))
        except Exception as fehler:
            # NICHT MEHR STILL. Eine unlesbare state.json tarnt sich als
            # "Board nicht erreichbar"; das darf man sehen.
            print(f"    local| state.json nicht lesbar ({fehler})", flush=True)
            daten = None
        if daten is not None:
            output += f"\nstatus: {daten.get('status', 'unknown')}"
            if daten.get("error"):
                output += f"\nerror: {daten['error']}"
    else:
        # Keine state.json heisst: der Navigator ist gestorben, bevor er einen
        # Endzustand hatte. Dann bleibt es bei dem, was auf der Konsole stand --
        # und "status:" fehlt zu Recht.
        print(f"    local| keine state.json unter {zustand}", flush=True)

    return result.returncode, output


# ---------------------------------------------------------------- Bildfolge

def capture_chunk(seconds: int, out_zip: Path,
                  interval_ms: int = 45, offset_x: int = 78) -> int:
    """Druecken und filmen, gleichzeitig, dann einpacken.

    Beide Skripte sind dieselben, die im Gast laufen. Sie werden nur nebeneinander
    gestartet statt ueber den Aufgabenplaner, und die ZIP entsteht hier statt drueben.

    Die Aufnahme deckt dieselbe Tabelle ab wie im Gast. `offset_x` gehoert zur
    Aufnahme UND zur spaeteren OCR: die Leserin bekommt denselben Wert, sonst sitzen
    ihre Spaltengrenzen um genau diesen Betrag daneben.
    """
    stamp = time.strftime("%Y%m%d_%H%M%S")
    frames = WORKSPACE / "data/runtime/local_frames" / stamp
    frames.mkdir(parents=True, exist_ok=True)

    press = subprocess.Popen(
        ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
         str(SCRIPTS / "press_down_continuously.ps1"), "-Seconds", str(seconds)],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    film = subprocess.Popen(
        ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
         str(SCRIPTS / "capture_leaderboard_frames.ps1"),
         "-OutDir", str(frames), "-Seconds", str(seconds),
         "-IntervalMs", str(interval_ms),
         "-X", str(offset_x), "-Y", "268", "-Width", "1700", "-Height", "610"],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    try:
        film.wait(timeout=seconds + 120)
    except subprocess.TimeoutExpired:
        film.kill()
    # Den Druecker IMMER einholen, auch wenn die Aufnahme schiefging. Ein
    # weiterlaufender Druecker schickt Pfeiltasten in das naechste Fenster, das den
    # Vordergrund bekommt -- also womoeglich in den Browser des Nutzers.
    try:
        press.wait(timeout=30)
    except subprocess.TimeoutExpired:
        press.kill()

    pngs = sorted(frames.glob("*.png"))
    if not pngs:
        _cleanup(frames)
        return 0
    out_zip.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(out_zip, "w", zipfile.ZIP_STORED) as bundle:
        # ZIP_STORED und nicht DEFLATE: PNG ist bereits gepackt, das Zusammendruecken
        # kostet hier Minuten und spart nichts.
        for png in pngs:
            bundle.write(png, png.name)
    _cleanup(frames)
    return out_zip.stat().st_size if out_zip.exists() else 0


def _cleanup(folder: Path) -> None:
    import shutil
    shutil.rmtree(folder, ignore_errors=True)


# ---------------------------------------------------------------- Standbild

def capture_settled_screen(out_dir: Path) -> bool:
    """Ein ruhiges Vollbild plus seine OCR, im Format das `settled_point` erwartet.

    `settled_point` braucht zwei Dateien: `current.png` und `current.ocr.json` mit
    einer Liste `lines`, jede mit `x` und `text`. Es liest daraus den mittleren
    sichtbaren Rang und misst am selben Bild den Scrollbalken.

    Warum ein eigenes Bild und nicht das letzte der Bildfolge: die Bilder aus der
    Fahrt sind fuer die Balkenerkennung unbrauchbar -- ein frueher Probelauf meldete
    auf jedem einzelnen `scrollbar_unreadable`. Der Balken ist nur zu sehen, wenn die
    Liste steht.
    """
    out_dir.mkdir(parents=True, exist_ok=True)
    frame = out_dir / "current.png"
    ocr_path = out_dir / "current.ocr.json"
    # Alte Dateien weg, bevor irgendetwas schiefgehen kann: ein fehlgeschlagenes
    # Standbild, das die vorige Aufnahme liegen laesst, wird als frische Messung
    # gelesen -- gleicher Rang, gleicher Balken, und der Scan schliesst daraus auf ein
    # Boardende. Genau dieser Fehler ist im VM-Weg schon einmal aufgetreten.
    for stale in (frame, ocr_path):
        try:
            stale.unlink(missing_ok=True)
        except OSError:
            pass

    sys.path.insert(0, str(SCRIPTS))
    try:
        import screen_reader
        from PIL import Image
    except Exception as error:
        print(f"  Standbild nicht moeglich: {error}")
        return False

    try:
        # Die SPIELFLAECHE im 1080p-Bezugsrahmen, nicht der rohe Hauptschirm: alles
        # danach (Balken, Zeilen, Rang) ist an 1920x1080 vermessen.
        image = screen_reader.grab_game()
        Image.fromarray(image[:, :, ::-1]).save(frame)
    except Exception as error:
        print(f"  Bildschirmaufnahme fehlgeschlagen: {error}")
        return False

    # DIE OCR-MASCHINE DIREKT, NICHT UEBER ScreenReader.
    #
    # Hier stand `screen_reader.Reader(gpu=False)`. Diese Klasse gibt es nicht --
    # sie heisst ScreenReader, und sie verlangt einen `advisor`, der dem
    # Beitrags-Paket ausdruecklich nicht beiliegt (siehe DELIBERATELY_ABSENT in
    # build_contrib_package.py). Der Aufruf konnte also nie funktioniert haben; er
    # lief seit jeher in den except-Zweig und meldete "OCR des Standbilds
    # fehlgeschlagen", woraufhin die Boardlaenge "nicht messbar" blieb.
    #
    # Gebraucht werden hier bloss Textzeilen mit Position -- keine Deutung von
    # Strecke oder Klasse. Dafuer genuegt die Maschine aus extract_leaderboard,
    # die ohnehin mitgeliefert wird und die ScreenReader selbst benutzt.
    try:
        from extract_leaderboard import create_rapidocr_engine
        engine = create_rapidocr_engine(False)
        result = engine(image)

        def als_liste(name: str) -> list:
            # `boxes` kommt als numpy-Array zurueck -- auf None pruefen, nicht auf
            # Wahrheitswert, sonst wirft numpy bei mehr als einem Element.
            wert = getattr(result, name, None)
            return [] if wert is None else list(wert)

        texte = als_liste("txts")
        kaesten = als_liste("boxes")
        guete = als_liste("scores")
        zeilen = []
        for i, text in enumerate(texte):
            sauber = str(text).strip()
            if not sauber:
                continue
            x = y = 0.0
            if i < len(kaesten):
                punkte = kaesten[i]
                punkte = punkte.tolist() if hasattr(punkte, "tolist") else punkte
                x = min(float(p[0]) for p in punkte)
                y = min(float(p[1]) for p in punkte)
            zeilen.append({"text": sauber, "x": x, "y": y,
                           "score": float(guete[i]) if i < len(guete) else 1.0})
    except Exception as error:
        print(f"  OCR des Standbilds fehlgeschlagen: {error}")
        return False

    ocr_path.write_text(json.dumps({"lines": zeilen}, ensure_ascii=False),
                        encoding="utf-8")
    return True
