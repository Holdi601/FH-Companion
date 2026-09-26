"""Sweep boards the fast way: scroll, film, read on the host, delete the frames.

Measured on 2026-08-22, which is why this exists:

| route                        | fresh board | coverage        |
| ---------------------------- | ----------- | --------------- |
| memory loop (waits per page) | 724/min     | gapless         |
| flow scanner (memory, no wait)| 285/min    | 80% missed      |
| screen capture + OCR         | ~1,490/min  | 100% in span    |

The screen wins for the same reason the flow scanner was supposed to -- it never waits
for the game -- but unlike the flow scanner it cannot miss rows: the table shows 11 at a
time and 19 frames/s against ~25 rows/s of scroll is heavy over-coverage. Reading happens
on the host afterwards, so it cannot slow the capture down either.

What the screen does not carry: `xuid` and the submitted timestamp. It does carry rank,
lap time, car NAME, drivetrain, gearbox, ABS/TCS/STM and the dirty-lap flag.

Per board: navigate, then capture in chunks. Each chunk is OCR'd and its frames deleted
before the next one, so disk stays near one chunk (~350 MB) instead of growing to
gigabytes. A board ends when a chunk adds no new ranks, or when its rows turn invalid --
the tail every board has, worth 22% of it.

    python scripts/ocr_board_sweep.py --route-indices 6-22 --until-hour 10
"""

from __future__ import annotations

import argparse
import bisect
import io
import json
import os
import shutil
import subprocess
import sys
import time
import zipfile
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime
from pathlib import Path

from detect_leaderboard_scrollbar import measure as measure_scrollbar
import ocr_corrections

WORKSPACE = Path(__file__).resolve().parent.parent
SCRIPTS = WORKSPACE / "scripts"

sys.path.insert(0, str(SCRIPTS))
import local_target  # noqa: E402
from ocr_leaderboard_frames import pi_fits_class  # noqa: E402

# Der Navigator reicht OCR-Text durch, und der enthaelt alles Moegliche. Landet stdout
# in einer umgeleiteten Datei, waehlt Python unter Windows cp1252 -- ein einziges
# unbekanntes Zeichen beendet dann den GANZEN Klassendurchgang mit UnicodeEncodeError.
# Genau so starb am 2026-09-01 11:32 der D-Durchgang bei idx16. Die Logdatei war nie
# betroffen, die schreibt seit jeher utf-8; nur print() war es.
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):  # kein TextIOWrapper (z.B. unter pytest)
        pass

# How many times a chunk that adds nothing may be overruled by a scrollbar that is
# not at the bottom. "No new ranks" and "the board ended" are different events and
# this file used to treat them as one -- see the stop rule in scan_board.
MAX_END_REJECTIONS = 3
CLASSES = ["D", "C", "B", "A", "S1", "S2", "R"]


def log(message: str, log_path: Path | None = None) -> None:
    line = f"[ocr-sweep] {datetime.now():%Y-%m-%d %H:%M:%S} {message}"
    print(line, flush=True)
    if log_path:
        with log_path.open("a", encoding="utf-8") as handle:
            handle.write(line + "\n")


def powershell(script: Path, args: list[str], timeout: int = 1800) -> tuple[int, str]:
    result = subprocess.run(
        ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass",
         "-File", str(script), *args],
        capture_output=True, text=True, errors="replace", timeout=timeout)
    return result.returncode, (result.stdout or "") + (result.stderr or "")


def _forza_mb(vm: str) -> int:
    """Arbeitsspeicher von Forza im Gast in MB. -1 = Gast nicht erreichbar, 0 = weg."""
    probe = (
        r"$c = [pscredential]::new('.\admin', [Security.SecureString]::new()); "
        "try { $s = New-PSSession -VMName '" + vm + "' -Credential $c -ErrorAction Stop; "
        "$r = Invoke-Command -Session $s -ScriptBlock { "
        "$p = Get-Process forzahorizon6 -ErrorAction SilentlyContinue; "
        "if ($p) { [int]($p.WorkingSet64 / 1MB) } else { 0 } }; "
        "Remove-PSSession $s -ErrorAction SilentlyContinue; $r } catch { -1 }")
    try:
        out = subprocess.run(["powershell.exe", "-NoProfile", "-Command", probe],
                             capture_output=True, text=True, timeout=180).stdout
        return int("".join(c for c in out if c.isdigit() or c == "-") or "-1")
    except Exception:
        return -1


def dismiss_dialog(vm: str, log_path: Path | None = None) -> None:
    """Einen modalen Dialog mit EINEM ENTER quittieren.

    WOFUER -- der teuerste Stillstand des Projekts, 2026-09-07:
    Um 09:56 erschien "Server Error / There was an error communicating with the server."
    Der Sweep zog sich zurueck und versuchte es erneut, achtmal, dann Pausen, dann
    Neustarts -- bis 14:42. In diesen VIER STUNDEN UND 46 MINUTEN hat NICHTS den Dialog
    weggeklickt. Um 14:42 druckte der Ausfall-Warter ein einziges Mal ENTER; elf Minuten
    spaeter stand das Spiel wieder normal in der Streckenliste und der Scan lief weiter.

    Der Rueckzug allein kann also nicht funktionieren: ein modaler Dialog blockiert
    JEDE Navigation, und eine Wiederholung dagegen ist so aussichtsreich wie die
    vorige. Das widerspricht NICHT der Regel "ein Serverfehler ist ein
    Rueckzugssignal" (server-error-means-stop) -- gewartet wird weiterhin. Es wird nur
    zusaetzlich der Weg freigeraeumt, damit das Warten ueberhaupt etwas bewirken kann.
    Ein Tastendruck je Rueckzug ist kein Haemmern.
    """
    if local_target.is_local(vm):
        return
    try:
        code, _ = powershell(SCRIPTS / "drive_vm_forza.ps1",
                             ["-Key", "ENTER", "-Count", "1", "-SettleMs", "3000"],
                             timeout=180)
        log(f"  Dialog mit ENTER quittiert (exit {code})", log_path)
    except Exception as error:
        log(f"  WARNUNG Dialog liess sich nicht quittieren: {error}", log_path)


def guest_looks_wedged(vm: str, log_path: Path) -> bool:
    """Ist der Gast WIRKLICH krank -- oder ist nur die Navigation gescheitert?

    reset_guest() ist fuer einen ganz bestimmten Zustand gebaut (siehe dort): Forza
    haengt als Huelle bei ~660 MB ohne Fenster, weil Steam es neu gestartet hat und es
    nie hochkam. Aufgerufen wurde er aber bei JEDEM zweiten Navigationsfehler in Folge,
    egal warum.

    Das ist teuer bezahlt. Am 2026-09-03 loesten zwei voellig gesunde Faelle je einen
    VM-Neustart aus: einmal "Could not select performance class 'A'" (die Klassenzeile
    ist auf hellen Karten unlesbar) und einmal "Could not find the anchor route
    'The Titan'". Beide Male lief das Spiel normal weiter; der Neustart hat nichts
    repariert, was kaputt war. Und ein VM-Start ist bei diesem Aufbau kein
    Nulltarif -- zwei der sechs Host-Abstuerze der Projektgeschichte passierten genau
    dabei.

    Darum jetzt erst hinsehen. "Krank" heisst: der Gast antwortet nicht mehr, Forza
    ist weg, oder Forza steht deutlich unter dem Speicher eines geladenen Spiels.
    Die Schwelle liegt bei 1000 MB: die Huelle sitzt bei ~660 MB, ein fertig geladenes
    Hauptmenue wurde bei 1564-1677 MB gemessen. Ein LADENDES Spiel sieht kurzzeitig
    genauso aus wie ein totes (forza-window-handle-is-zero), deshalb entscheidet der
    Speicher und nicht das Fensterhandle.
    """
    if local_target.is_local(vm):
        return False
    mb = _forza_mb(vm)
    if mb < 0:
        log("  Gast antwortet nicht -- das ist ein echter Haenger", log_path)
        return True
    if mb == 0:
        log("  Forza laeuft nicht mehr im Gast -- das ist ein echter Haenger", log_path)
        return True
    if mb < 1000:
        # Wenig Speicher heisst NICHT automatisch Huelle. Direkt nach einem Neustart
        # steht ein voellig gesundes, LADENDES Forza ebenfalls bei ~585 MB (gemessen
        # 2026-09-03 14:18). Der Unterschied ist die Bewegung: eine ladende Instanz
        # waechst, eine Huelle nicht. Ohne diese zweite Messung koennte ein Reset den
        # naechsten ausloesen -- eine Neustartschleife auf einem Aufbau, bei dem jeder
        # VM-Start ein Absturzrisiko fuer den Host ist.
        # NICHT auf Wachstum pruefen. Gemessen am 2026-09-03 waehrend eines Ladevorgangs:
        # 714 -> 584 -> 1586 MB. Der Wert FAELLT zwischendurch, weil das Spiel den
        # Ladepuffer freigibt. Eine Regel "gewachsen = laedt" haette den Schritt
        # 714 -> 584 als Huelle gewertet und die VM mitten im Hochfahren neu gestartet.
        # Also mehrfach messen und das MAXIMUM nehmen: eine Huelle bleibt unten, ein
        # ladendes Spiel ueberschreitet die Schwelle innerhalb einer Minute.
        werte = [mb]
        for _ in range(3):
            time.sleep(20)
            werte.append(_forza_mb(vm))
        hoechster = max(werte)
        if hoechster >= 1000:
            log(f"  Forza laedt gerade (Verlauf {werte} MB) -- kein Haenger", log_path)
            return False
        log(f"  Forza haengt als Huelle (Verlauf {werte} MB, nie ueber 1000) -- "
            f"das ist ein echter Haenger", log_path)
        return True
    log(f"  Gast ist gesund (Forza {mb} MB) -- die Navigation ist gescheitert, nicht "
        f"der Gast. KEIN VM-Neustart.", log_path)
    return False


def reset_guest(vm: str, log_path: Path) -> bool:
    """The documented cure for a game that is running but has no window.

    Forza sitting at ~660 MB with no main window is a shell, not a game: Steam
    relaunched it and it never came up. Navigation then OCRs the desktop and every
    board is recorded unreachable within a minute -- which is how an unattended night
    marches through the whole route list without scanning anything. Only a VM reset
    fixes it, and `start_forza_vm_ready.ps1` is that reset plus a clean display session.
    """
    if local_target.is_local(vm):
        # Lokal gibt es keine VM, die man neu starten koennte -- und das Spiel des
        # Nutzers ungefragt abzuschiessen waere eine Frechheit. Also sagen, was los
        # ist, und aufhoeren.
        log("  Forza reagiert nicht wie erwartet. Bitte pruefen, ob das Spiel im "
            "Menue steht und im Vordergrund ist, dann neu starten.", log_path)
        return False
    log("  guest looks wedged; resetting the VM", log_path)
    code, _ = powershell(SCRIPTS / "start_forza_vm_ready.ps1", ["-VMName", vm],
                         timeout=1200)
    time.sleep(45)
    return code == 0


def platz_im_gast(vm: str, log_path) -> float:
    """Wie viel Platz die VM noch hat, in GB. -1 heisst: nicht zu erfahren.

    ## Warum das vor jedem Board steht

    In der Nacht zum 2026-09-14 lief dieser Sweep eine Stunde blind: die Navigation
    erreichte jede Strecke, die Aufnahme meldete "nothing captured", und kein
    Zaehler schlug an. Die Ursache war die volle Platte der VM -- null Byte frei von
    255 GB, davon 46 GB eigene Bildschirmaufnahmen, die seit dem 18. August niemand
    entfernt hatte.

    Das Tueckische daran ist nicht der volle Datentraeger, sondern WIE er sich
    zeigt: "nothing captured", "kein Standbild bekommen", "Could not find the anchor
    route". Alles Meldungen, die nach Anzeige, Navigation oder Spiel aussehen. Ich
    habe daraufhin die VM neu gestartet, was kurz half, weil dabei etwas Temp frei
    wurde -- und die falsche Spur damit bestaetigt schien.

    Eine Zeile "noch 0,4 GB frei" haette die Stunde erspart.
    """
    if local_target.is_local(vm):
        return -1.0
    befehl = ("$c = [pscredential]::new('.\\admin', [Security.SecureString]::new()); "
              "Invoke-Command -VMName '%s' -Credential $c -ScriptBlock "
              "{ [math]::Round((Get-PSDrive C).Free / 1073741824, 2) }" % vm)
    try:
        lauf = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive",
                               "-Command", befehl],
                              capture_output=True, text=True, timeout=120)
        return float((lauf.stdout or "").strip().splitlines()[-1].replace(",", "."))
    except Exception:
        return -1.0


def verified_categories() -> list[str]:
    """Die Kategorien, fuer die der Katalog Strecken kennt -- und nur die."""
    try:
        data = json.loads((WORKSPACE / "config/fh6_board_catalogue.json")
                          .read_text(encoding="utf-8-sig"))
        return sorted((data.get("tracks") or {}).keys())
    except Exception:
        return ["Road Racing"]


def refuse_unverified_category(category: str) -> str | None:
    """Warum diese Kategorie (noch) nicht gescannt werden darf -- oder None.

    Zwei unabhaengige Gruende, und beide erzeugen KORRUPTE Daten statt eines
    Fehlschlags, was viel teurer ist:

    1. Der Navigator kann die Kategorie gar nicht wechseln. Er drueckt RECHTS auf
       einer SENKRECHTEN Liste, in der alle Kategorien gleichzeitig stehen: die
       Markierung bewegt sich nicht, das Suchmuster passt trotzdem sofort, und ENTER
       oeffnet was zufaellig markiert war. Ein "Street Racing"-Lauf liefert also eine
       Nacht Road-Racing-Zeilen unter falscher Kategorie.
    2. Der Streckenkatalog kennt nur die Karussellpositionen von Road Racing. Selbst
       bei richtiger Kategorie kaeme der Streckenname aus der falschen Liste.

    Beides faellt beim Scannen NICHT auf -- die Zeilen sehen tadellos aus. Darum hier
    ein Riegel und keine Warnung: eine Warnung, die nichts verhindert, ist keine
    Pruefung.
    """
    known = verified_categories()
    # GETRIMMT VERGLEICHEN. Am 2026-09-19 kam die Kategorie als " Road Racing" an
    # -- mit fuehrendem Leerzeichen, aus der Anfuehrungszeichen-Behandlung der
    # Kommandozeile. Der Riegel fiel zu, obwohl genau diese Kategorie in der Liste
    # steht, und die Meldung zeigte die Kategorie in Anfuehrungszeichen, wo man das
    # Leerzeichen sehen KONNTE -- aber nur, wenn man danach sucht.
    if category.strip() in {k.strip() for k in known}:
        return None
    return "\n".join([
        f"Die Kategorie {category!r} ist noch nicht scanbar.",
        f"  Scanbar ist zur Zeit: {', '.join(known)}.",
        "  Grund 1: der Navigator wechselt die Kategorie nicht -- er drueckt RECHTS",
        "           auf einer senkrechten Liste, die Markierung bleibt stehen, und",
        "           ENTER oeffnet die zuletzt benutzte Kategorie.",
        "  Grund 2: der Streckenkatalog kennt nur die Karussellpositionen von",
        "           Road Racing.",
        "  Beides erzeugt Zeilen, die richtig aussehen und unter der falschen Strecke",
        "  stehen. Erst die Kategorie-Auswahl in Ordnung bringen, dann die Strecken",
        "  mit  forza_navigator.ps1 -EnumerateRoutes  aufnehmen.",
    ])


def category_routes(category: str) -> list:
    """Die Karussellpositionen EINER Kategorie.

    Bis 2026-08-28 gab es nur eine Liste, die von Road Racing. Bei einer anderen
    Kategorie waere der Name aus der falschen Liste gekommen -- richtige Zeilen unter
    falscher Beschriftung, und das faellt beim Scannen nicht auf.
    """
    try:
        data = json.loads((WORKSPACE / "config/fh6_board_catalogue.json")
                          .read_text(encoding="utf-8-sig"))
    except Exception:
        return []
    per_category = data.get("route_carousel_by_category") or {}
    if category in per_category and isinstance(per_category[category], list):
        return per_category[category]
    # Rueckfall auf die alte, flache Liste -- die ist Road Racing.
    if category == "Road Racing":
        return (data.get("route_carousel") or {}).get("positions", [])
    return []


def _name_key(text: str) -> str:
    """Namen vergleichbar machen: nur Buchstaben und Ziffern, klein."""
    return "".join(c for c in (text or "").lower() if c.isalnum())


def foreign_route_match(route_index: int, category: str, read_name: str) -> int:
    """Ist der GELESENE Name der einer ANDEREN Strecke dieser Kategorie?

    Gibt deren Karussellposition zurueck, sonst -1.

    WOFUER: full_track_name() kennt die Cross-Country-Namen nicht, deshalb faellt dort
    JEDE Lesung in den Zweig "unlesbar" -- auch eine voellig korrekte wie
    'Temple Cross-Country'. Der Katalogersatz greift dann bedingungslos. Solange die
    Lesung Muell ist ('I @ 2,666,890'), ist das genau richtig. Ist sie aber der saubere
    Name einer anderen Strecke, heisst das etwas voellig anderes: der Schirm zeigte noch
    die VORIGE Strecke, oder die Navigation ist gar nicht weitergeschaltet. Am
    2026-09-01 dreimal gesehen (idx02 las idx01, idx10 las idx09, idx09 las idx08), und
    jedes Mal wurde stumm der erwartete Name eingesetzt.

    Dass es an jenem Tag gut ging, war Glueck: die Boards waren nachweislich
    verschieden (Soni Highlands 72,058 s gegen Ruriko-ji 143,41 s). Waere die Navigation
    wirklich stehengeblieben, waeren die Zeilen unter dem falschen Streckennamen
    gelandet -- und das faellt beim Scannen nicht auf.
    """
    key = _name_key(read_name)
    if len(key) < 8:                      # zu kurz, um etwas zu beweisen
        return -1
    for other, entry in enumerate(category_routes(category)):
        if other == route_index:
            continue
        other_name = entry if isinstance(entry, str) else (entry or {}).get("name", "")
        other_key = _name_key(other_name)
        if len(other_key) < 8:
            continue
        if key == other_key or key.startswith(other_key) or other_key.startswith(key):
            return other
    return -1


def category_route_count(category: str) -> int:
    """Wie viele Strecken die Kategorie hat, aus dem Katalog."""
    return len(category_routes(category))


def category_anchor(category: str) -> str:
    """Position 0 der Kategorie -- der Name, von dem aus gezaehlt wird."""
    routes = category_routes(category)
    if not routes:
        return ""
    first = routes[0]
    if isinstance(first, str):
        return first
    return first.get("name") or "" if isinstance(first, dict) else ""


def catalogue_route(route_index: int, category: str = "Road Racing") -> str:
    """Der Streckenname zu einer Karussellposition, aus dem Katalog.

    Zweitquelle neben der Bildschirmlesung, und die verlaesslichere: der Navigator laeuft
    das Karussell ab und BESTAETIGT die Position, bevor er ENTER drueckt. Der gelesene
    Name ist nur die Gegenprobe.

    Noetig wurde das am 2026-08-26 um 03:32: fuer Index 18 las die OCR statt des
    Streckennamens eine Punktestandszeile, und das Board landete mit 2.631 Zeilen unter
    dem Streckennamen 'I @ 2,666,890'. Die Pruefung an dieser Stelle hat das gesehen und
    nur gewarnt -- gebannt wurde es trotzdem. Eine Warnung, die nichts verhindert, ist
    keine Pruefung.
    """
    # Negative Indizes ausdruecklich abweisen: positions[-1] waere in Python die LETZTE
    # Strecke, also The Goliath -- ein stiller Griff auf die falsche Zeile statt eines
    # Fehlschlags. Der Rauschtest hat genau das gefunden.
    if not isinstance(route_index, int) or route_index < 0:
        return ""
    try:
        entry = category_routes(category)[route_index]
    except Exception:
        return ""
    # Die Eintraege sind Objekte mit name/lap_km/route_index, waren aber frueher reiner
    # Text -- beide Formen muessen gehen. Ein Objekt versehentlich als Name zu schreiben
    # erzeugt genau den Muellnamen, den diese Funktion verhindern soll.
    if isinstance(entry, str):
        return entry
    if isinstance(entry, dict):
        name = entry.get("name")
        return name if isinstance(name, str) else ""
    return ""


def navigate(vm: str, category: str, route_index: int, klass: str,
             anchor: str, warm: bool) -> tuple[bool, str, bool]:
    """Returns whether the board opened, which route it is, AND whether the game
    answered with "Server Error".

    Der dritte Wert existiert, weil ein Serverfehler das Gegenteil eines Boardfehlers
    ist: er sagt nichts ueber Strecke oder Klasse, sondern "spaeter nochmal". Ohne die
    Unterscheidung sieht er wie ein nicht erreichbares Board aus -- am 2026-08-25 wurde
    deshalb um 07:21 von Hand die ganze Nacht abgebrochen, obwohl 1,5 Stunden Laufzeit
    uebrig waren und der Fehler zum ersten Mal auftrat.

    The anchor is only where counting starts -- route index 4 reached from "Highway
    Circuit" is Hokubu Circuit. Filing every board under the anchor put Hokubu's rows
    into Highway's board, which is a data error, not a cosmetic one. The navigator
    prints what it read off the screen; that is the track.
    """
    args = ["-VMName", vm, "-RivalsMode", category, "-PerformanceClass", klass,
            "-RouteIndex", str(route_index), "-RouteAnchor", anchor,
            # NICHT mehr fest 23: Street Racing hat 15, Dirt 21, Drag 3. Mit 23
            # wuerde das Karussell ueber den Rundlauf hinaus gezaehlt und die
            # Position waere eine andere als gemeint.
            "-RouteCount", str(category_route_count(category) or 23)]
    # Never -FreshStart here. It kills and relaunches the game, and relaunching in
    # quick succession is precisely what breaks the Steam launch and leaves the
    # windowless shell this runner then has to reset the VM to clear -- a state this
    # code created for itself twice. With no flag the navigator works from whatever
    # screen it finds, title screen included.
    if warm:
        args.append("-FromLeaderboard")
    if local_target.is_local(vm):
        # Derselbe Navigator, nur ohne den Umweg durch den Gast. Alles danach --
        # Auswertung der Ausgabe, Streckenname, Serverfehler -- ist identisch, weil es
        # dieselbe Ausgabe ist.
        code, output = local_target.navigate(category, route_index, klass, anchor, warm)
    else:
        code, output = powershell(SCRIPTS / "start_forza_navigation.ps1", args,
                                  timeout=1500)
    if "status: leaderboard_reached" not in output:
        # Blind failures cost an hour tonight: keep the last few navigator lines so the
        # next look does not need a live guest to explain what happened.
        tail = [line.strip() for line in output.splitlines() if line.strip()][-6:]
        for line in tail:
            log("    nav| " + line[:160])
    # ZWEI MARKEN, WEIL DER NAVIGATOR ZWEI SCHREIBT.
    #
    #   "route index 5 confirmed on screen as 'Soni Circuit'"
    #       -- beim Auswaehlen in der Streckenliste.
    #   "confirmed route on the board: Soni Circuit"
    #       -- beim Landen auf der Bestenliste.
    #
    # Hier stand nur die erste. Wer ueber den Klassenschirm landet, sieht nur die
    # zweite -- dann blieb route leer, die Ebene darueber hielt das fuer einen
    # Fehlschlag, versuchte es "vom anderen Schirmzustand" erneut, und der zweite
    # Versuch stand bereits auf dem Board und bestaetigte gar nichts mehr. Ergebnis:
    # "not reachable; next" fuer ein Board, das zweimal richtig erreicht worden war.
    #
    # Am 2026-09-19 im Beitrags-Lauf auf Soni Circuit gemessen. Es betrifft NICHT
    # nur das Werkzeug des Freundes: ocr_board_sweep ist derselbe Scanner, den auch
    # der Hauptrechner faehrt.
    route = ""
    for line in output.splitlines():
        for marker in ("confirmed on screen as", "confirmed route on the board:"):
            if marker in line:
                gefunden = line.split(marker, 1)[1].strip().strip("'\"")
                if gefunden:
                    route = gefunden
    # DER ABSTURZ IST EIN EIGENES ERGEBNIS, kein Navigationsfehlschlag.
    # Am 2026-09-18 kam nach einem "Video Card Crash" ein "Stuck in state
    # 'unknown'" zurueck, und die Ebene hier versuchte es brav ein zweites Mal
    # vom anderen Schirmzustand aus -- gegen ein Spiel, das nicht mehr lief.
    crashed = ("Game crashed:" in output
               or "terminated unexpectedly" in output
               or "Video Card Crash" in output)
    return (("status: leaderboard_reached" in output), route,
            "Server Error" in output, crashed)


def capture_chunk(vm: str, seconds: int, out_zip: Path) -> int:
    """Run the presser and the frame capture as two guest tasks; bring back a zip."""
    if local_target.is_local(vm):
        size = local_target.capture_chunk(seconds, out_zip)
        log(f"  [chunk] lokal aufgenommen -> {out_zip.name} ({size / 1e6:.0f} MB)")
        return size
    code, output = powershell(SCRIPTS / "capture_one_chunk.ps1",
                              ["-VMName", vm, "-Seconds", str(seconds),
                               "-OutZip", str(out_zip)],
                              timeout=seconds + 600)
    for line in output.splitlines():
        if "frames" in line:
            log("  " + line.strip())
    return out_zip.stat().st_size if out_zip.exists() else 0


def ocr_zip(archive: Path, workers: int, keep_last_frame: Path | None = None,
            board_root: Path | None = None,
            log_path: Path | None = None,
            klass: str | None = None) -> list[dict]:
    """Unpack, read, delete. The frames are the bulky part and nothing needs them after.

    One frame is worth keeping: the LAST one, because the scrollbar in it is the only
    direct evidence of whether the list has actually ended. It is copied out before the
    folder goes, so disk still holds one chunk plus one PNG rather than gigabytes.
    """
    folder = archive.with_suffix("")
    folder.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(archive) as bundle:
        bundle.extractall(folder)
    rows_path = folder / "rows.jsonl"

    def read_frames(with_workers: int) -> subprocess.CompletedProcess:
        command = [sys.executable, str(SCRIPTS / "ocr_leaderboard_frames.py"),
                   "--frames", str(folder), "--out", str(rows_path),
                   "--batch", "40", "--workers", str(with_workers), "--offset-x", "78"]
        # Die Klasse des Boards, damit ein PI ausserhalb ihres Bandes schon bei der
        # Abstimmung je Rang nicht mitzaehlt -- sonst kann eine Fehllesung, die in
        # zwei Bildern gleich falsch ist, eine richtige einzelne ueberstimmen.
        if klass:
            command += ["--pi-class", str(klass)]
        return subprocess.run(command, capture_output=True, text=True)

    result = read_frames(workers)
    # Stuerzt die OCR ab, fehlt rows.jsonl und der Chunk liefert lautlos nichts -- was
    # unter dem Pipelining im Log AUSSIEHT wie eine noch laufende OCR. Also melden.
    if result.returncode != 0 or not rows_path.exists():
        tail = (result.stderr or result.stdout or "").strip().splitlines()[-2:]
        log(f"  WARNUNG OCR fehlgeschlagen (exit {result.returncode}) fuer "
            f"{archive.name}: {' | '.join(t.strip() for t in tail)}")
        # EIN zweiter Versuch mit wenigen Arbeitern. Grund: am 2026-08-25 um 14:07 starb
        # ein Arbeitsprozess abrupt (BrokenProcessPool) bei 14 GB freiem Speicher -- ein
        # Absturz, keine Last. Der Chunk war damit verloren, 2.429 Frames, und zwar
        # lautlos: eine Warnung im Log, aber die Raenge fehlen im Board, ohne dass
        # irgendeine Kennzahl darauf zeigt. Die Frames liegen hier schon entpackt, der
        # zweite Versuch kostet also nur Rechenzeit. Weniger Arbeiter, weil ein
        # abgestuerzter Pool mit derselben Aufteilung gern erneut abstuerzt.
        retry_workers = max(2, workers // 3)
        log(f"  zweiter OCR-Versuch fuer {archive.name} mit {retry_workers} Arbeitern")
        result = read_frames(retry_workers)
        if result.returncode != 0 or not rows_path.exists():
            tail = (result.stderr or result.stdout or "").strip().splitlines()[-2:]
            log(f"  WARNUNG zweiter Versuch ebenfalls gescheitert (exit "
                f"{result.returncode}): {' | '.join(t.strip() for t in tail)}")
        else:
            log(f"  zweiter Versuch erfolgreich fuer {archive.name}")
    rows = []
    if rows_path.exists():
        for line in rows_path.read_text(encoding="utf-8").splitlines():
            if line.strip():
                rows.append(json.loads(line))
    if keep_last_frame is not None:
        frames = sorted(folder.glob("*.png"))
        if frames:
            shutil.copyfile(frames[-1], keep_last_frame)
    # Muss VOR dem Loeschen stehen: danach ist der Beleg fuer immer weg. Genau daran
    # scheiterte am 2026-09-10 die Frage, ob "Chevrolet Corvette '53" wirklich auf dem
    # Schirm stand -- die Bilder waren laengst geloescht.
    if board_root is not None and rows:
        save_proofs(rows, folder, Path(board_root), log_path)
    shutil.rmtree(folder, ignore_errors=True)
    # Das Archiv wird nur bei ERFOLG geloescht. Am 2026-08-25 starb die OCR eines Chunks
    # von Electric Town A, und weil hier ohne Ansehen des Ergebnisses geloescht wurde,
    # waren Archiv und entpackte Frames weg, bevor jemand nachsehen konnte. Das Board
    # beginnt seitdem bei Rang 2394 statt 1 -- die 2.393 schnellsten Runden, also der
    # fuer eine Auto-Wertung wichtigste Teil, sind nur durch einen neuen Scan zu holen.
    # Bleibt das Archiv liegen, kostet ein zweiter Anlauf nur Rechenzeit statt Spielzeit.
    if rows:
        archive.unlink(missing_ok=True)
    else:
        log(f"  Archiv BLEIBT fuer einen spaeteren Anlauf: {archive}")
    return rows


def read_scrollbar(frame: Path) -> dict | None:
    """What the thumb says about the end of the list, or None if it could not be read."""
    if not frame.exists():
        return None
    try:
        result = measure_scrollbar(frame, 0.5, 12.0, 40.0, 0.01, 12)
    except Exception as error:  # a detector failure must not end a board
        log(f"  scrollbar check failed: {error}")
        return None
    return result if result.get("found") else None


# A clump is suspect when it sits this far past the rest of the board, and is judged a
# mis-numbered frame when the rows beyond that gap come from no more than this many
# distinct frames. One screenful is 11 rows; a real stretch of board is crossed by
# hundreds of frames even when few of its ranks render.
GAP_FOR_CLUMP = 300
FRAMES_FOR_CLUMP = 5

# A chunk cannot cross more ranks than the scroll can cover in its runtime; the widest
# advance ever measured was ~4,600 in 120 s. So a row claiming a rank further than this
# past where the board already was did not come from this chunk's scrolling.
#
# It happens for a specific reason. OCR inserts a digit into a comma-grouped rank --
# "1,708" read as "14,708" -- and because several rows of the same frame make the same
# mistake, the frame's own >=3-agreeing-ranks anchor accepts it and the whole screenful
# lands 13,000 ranks too far. That is why 14,722 and 14,723 kept turning up on board
# after board: it is rank 1,72x on every one of them.
MAX_CHUNK_ADVANCE = 6500


def _longest_ordered(entries: list[dict]) -> set[int]:
    """Ranks of the longest run whose lap times never go backwards."""
    ordered = sorted(entries, key=lambda row: int(row["rank"]))
    laps = [float(row["lap_time_seconds"]) for row in ordered]
    tails: list[float] = []
    tails_at: list[int] = []
    previous = [-1] * len(laps)
    for index, lap in enumerate(laps):
        slot = bisect.bisect_right(tails, lap)
        if slot:
            previous[index] = tails_at[slot - 1]
        if slot == len(tails):
            tails.append(lap)
            tails_at.append(index)
        else:
            tails[slot] = lap
            tails_at[slot] = index
    keep: set[int] = set()
    index = tails_at[-1] if tails_at else -1
    while index >= 0:
        keep.add(int(ordered[index]["rank"]))
        index = previous[index]
    return keep


def drop_stragglers(rows: dict[int, dict]) -> tuple[dict[int, dict], int]:
    """Remove rows a mis-numbered frame parks at a rank the list was never at.

    A frame's rank numbering comes from a majority of its own readable ranks, and once
    in a while three of them agree on a wrong base, putting one screenful of rows far
    from where the list actually was. A wrong rank is worse than a missing one.

    Two tests, because each catches what the other cannot.

    The first is the board's own ordering: each section is sorted by lap time, so a row
    whose time does not fit its rank was read somewhere else. That also removes the
    placeholder times the game draws before a row's real time arrives.

    The second is frame provenance, and it exists because the first is not enough. A
    mis-numbered frame carries REAL rows from the current view, and deep in a board
    those are the slowest times so far -- so parked even further down they still extend
    the non-decreasing run and the ordering test waves them through. Soni A on
    2026-08-23 came back spanning 1..14,723 for that reason. What gives them away is
    where they came from: below the gap sat 8,915 rows from 5,790 distinct frames, and
    beyond it 18 rows from 5 frames, 11 of them from one. A real stretch of board is
    crossed by many frames even when the scroll is so fast that only a few of its ranks
    render; one frame's worth of rows beyond a large gap is that one frame being wrong.

    This replaces an earlier density rule -- cut at the first big gap with only a sliver
    of rows past it -- which deleted real data: past rank ~10,000 the list scrolls so
    fast that a chunk renders only 30-120 of the thousands of ranks it crosses, so the
    true tail IS sparse. Shimanoyama D was written as 1..10,122 while its chunks had
    reached 27,726, and the memory scan of that board has 25,599 rows. Frame count
    separates sparse-but-real from few-frames-and-wrong; row count never could.
    """
    if len(rows) < 50:
        return rows, 0
    total = len(rows)

    def timed(clean: bool) -> list[dict]:
        return [row for row in rows.values()
                if bool(row.get("is_clean")) is clean
                and isinstance(row.get("lap_time_seconds"), (int, float))]

    # The two sections are sorted independently -- the invalid list restarts at a
    # faster time than the valid one -- so each is tested on its own.
    keep = _longest_ordered(timed(True)) | _longest_ordered(timed(False))
    if keep:
        # A row whose time could not be read is judged by company: inside the span the
        # ordered rows cover it is as trustworthy as they are, outside it is a phantom.
        low, high = min(keep), max(keep)
        keep |= {int(row["rank"]) for row in rows.values()
                 if not isinstance(row.get("lap_time_seconds"), (int, float))
                 and low <= int(row["rank"]) <= high}
    rows = {rank: row for rank, row in rows.items() if rank in keep}

    # Iteratively, because wrong bases arrive in several separated clumps: one board
    # ended with 4 rows past a 1,103 gap, then 47, then 13 past a 2,927 gap, and cutting
    # only the last clump left the other two in the data.
    while True:
        ranks = sorted(rows)
        cut_at = None
        for index in range(len(ranks) - 1, 0, -1):
            if ranks[index] - ranks[index - 1] < GAP_FOR_CLUMP:
                continue
            beyond = [rows[rank] for rank in ranks[index:]]
            frames = {row.get("source_frame") for row in beyond}
            if len(frames) <= FRAMES_FOR_CLUMP:
                cut_at = ranks[index]
                break
        if cut_at is None:
            break
        rows = {rank: row for rank, row in rows.items() if rank < cut_at}
    return rows, total - len(rows)


def supported_top(rows: dict[int, dict]) -> int:
    """The highest rank that survives the ordering test, i.e. that will be kept.

    The raw maximum is not safe to steer on. A frame whose rank base decodes wrongly
    parks rows far past the list, and those phantoms recur: 14,722 and 14,723 turned up
    on four and three separate boards on 2026-08-22/23, more often than any real deep
    rank. Steering the stop decision on the raw maximum makes a phantom look like
    progress, so the board runs its whole chunk budget for nothing -- Shimanoyama D
    spent chunks 9 through 12 gaining 31, 78, 31 and 116 rows while its "maximum"
    climbed from 20,121 to 27,726.

    Deciding on the kept rows instead makes the stop condition mean what it says: stop
    when the data the board will actually be written with stops growing.
    """
    kept, _ = drop_stragglers(dict(rows))
    return max(kept) if kept else 0


def write_board(run_root: Path, rows: dict[int, dict], meta: dict) -> dict:
    """The three files the analytics build and the manifest already understand."""
    rows, dropped = drop_stragglers(rows)
    run_root.mkdir(parents=True, exist_ok=True)
    (run_root / "combined").mkdir(exist_ok=True)
    ordered = [rows[rank] for rank in sorted(rows)]
    # DER PI JE RUNDE, gegen die Klasse des Boards geprueft. Die OCR stimmt schon mit
    # dem Band ab, aber nur wenn sie die Klasse kennt; hier ist sie immer bekannt, und
    # jede Zeile bekommt das Feld -- leer statt falsch. Ein PI ausserhalb des Bandes
    # ist eine Fehllesung und keine Auskunft: ein Auto ueber der Obergrenze darf auf
    # diesem Board gar nicht fahren, eines darunter gehoert in die Klasse darunter.
    klass = meta.get("performance_class")
    pi_rejected = 0
    for row in ordered:
        pi = row.get("pi")
        if pi is not None and not pi_fits_class(pi, klass):
            pi = None
            pi_rejected += 1
        row["pi"] = int(pi) if pi is not None else None
        if row["pi"] is None:
            row["pi_agreement"] = 0
    with (run_root / "rows.jsonl").open("w", encoding="utf-8") as handle:
        for row in ordered:
            handle.write(json.dumps(row, separators=(",", ":")) + "\n")

    low = ordered[0]["rank"]
    high = ordered[-1]["rank"]
    missing = (high - low + 1) - len(ordered)
    report = {"rows": len(ordered), "minimum_rank": low, "maximum_rank": high,
              "missing_rank_count": missing, "stragglers_dropped": dropped,
              "pi_read": sum(1 for row in ordered if row["pi"] is not None),
              "pi_out_of_class": pi_rejected,
              "source": "ocr"}
    (run_root / "combined" / "merge_report.json").write_text(
        json.dumps(report, indent=2), encoding="utf-8")

    state = dict(meta)
    # `status` used to be "complete" whenever no rank was missing BETWEEN the first
    # and the last row read -- a statement about internal gaps that says nothing
    # about whether the board ended there. Boards that stopped 20,000 ranks early
    # were gapless, so they were banked as complete. The two facts are now separate:
    # `status` reports how the board ENDED, `gapless` reports the holes.
    status = {"end_detected": "end_detected",
              "invalid_tail": "valid_section_complete"}.get(
                  meta.get("end_status"), meta.get("end_status") or "truncated")
    state.update({"status": status, "gapless": missing == 0,
                  "rows_collected": len(ordered), "minimum_rank": low,
                  "maximum_rank": high, "missing_ranks": missing, "scanner": "ocr",
                  "completed_at": datetime.now().astimezone().isoformat()})
    (run_root / "state.json").write_text(json.dumps(state, indent=2), encoding="utf-8")

    try:
        import pandas

        frame = pandas.DataFrame(ordered)
        # Ganzzahl MIT Luecke. Ohne das macht pandas aus einer Spalte mit einem
        # einzigen None lauter Kommazahlen (487.0), und eine ganz leere Spalte wird
        # zum Objekttyp ohne Zahlen darin.
        for column in ("pi", "pi_agreement"):
            if column in frame.columns:
                frame[column] = frame[column].astype("Int64")
        frame.to_parquet(run_root / "leaderboard_entries.parquet", index=False)
    except Exception as error:
        log(f"  parquet skipped ({error})")
    return report


# Below this the thumb has not moved far enough off the top for a single
# rank/position ratio to mean anything: at 3-5 px of travel one pixel is worth ~790
# ranks. The SLOPE between two parked positions cancels the thumb's minimum-height
# offset and is preferred whenever there is any spread at all.
# EFFECTIVE throughput, not scroll speed. A 120 s chunk advances ~2,339 ranks, which
# looks like 1,170 ranks/min -- but a chunk costs 4.59 min end to end once OCR, the
# settled screenshot and navigation are counted (measured over 21 boards / 31 chunks on
# 2026-08-23). Quoting the scroll rate made an ETA 2.3x too optimistic, so the honest
# number is the one that includes the work between chunks.
RANKS_PER_MINUTE = 510
# Wartezeit nach einem "Server Error" des Spiels, bevor dasselbe Board erneut angefahren
# wird. Acht Minuten sind lang genug, dass kein Haemmern entsteht, und kurz genug, dass
# drei Rueckzuege unter einer halben Stunde bleiben.
SERVER_ERROR_WAIT = 480
# Rueckzuege NACH Boards, nicht innerhalb eines Boards. Der Rueckzug in der Navigation
# zaehlt dreimal je Board -- ist der Dienst laenger weg, waeren das bei 13 offenen Boards
# 39 Anlaeufe gegen etwas, das nicht antwortet. Genau dieses Haemmern soll die Regel
# "ein Serverfehler ist ein Rueckzugssignal" verhindern. Also eskaliert es: nach zwei
# Boards in Folge eine halbe Stunde Pause, nach dem dritten endet der Lauf geordnet --
# geordnet, damit Join und Seitenaufbau am Ende noch laufen.
SERVER_ERROR_LONG_WAIT = 1800
SERVER_ERROR_GIVE_UP = 3
# Nach wie vielen fertigen Boards die Seite neu gebaut wird -- siehe die Begruendung an
# der Aufrufstelle: dauernd parallel laufende Aufbauten kosten Rechenzeit und Abstuerze.
# Wie viele Boards zwischen zwei Seitenaufbauten liegen. FORZA_SITE_EVERY=0 heisst:
# nur einmal am Ende des Laufs.
#
# WARUM EINSTELLBAR: der Aufbau ist mitgewachsen. Gemessen 2026-08-24 waren es 119 s,
# gemessen 2026-09-09 bei 504 Boards rund drei Stunden. Bei 119 s war ein Aufbau
# neben der OCR tragbar; bei drei Stunden liefe praktisch dauernd einer mit -- und
# genau dabei sind am 25. und 26.08. je ein OCR-Arbeiter mit BrokenProcessPool
# gestorben, waehrend ein Aufbau alle Parquets einlas.
SITE_EVERY = int(os.environ.get("FORZA_SITE_EVERY", "5") or 0)

MIN_THUMB_POSITION = 0.02
MIN_THUMB_SPREAD = 0.002


def full_track_name(read: str) -> tuple[str, str]:
    """Resolve an OCR-damaged route name. See scripts/ocr_corrections.py for the modes.

    Returns (name, how). A board whose name only resolved by similarity, or not at all,
    is still scanned -- but `how` is recorded so it can be re-checked rather than
    silently trusted.
    """
    return ocr_corrections.correct_route_name(read)


SETTLED_SCREEN = WORKSPACE / "data/runtime/scan_screen"


# Der Wortlaut des Spiels, wenn der Leaderboard-Dienst nicht antwortet:
# "Server Error / There was an error communicating with the server. Please try again
# later." Zwei Fassungen, weil die OCR am kurzen Titel schon einmal gescheitert ist
# (drei Zeichen und Grossbuchstaben, siehe windows-ocr-needs-three-characters), der
# lange Satz aber immer durchkam.
SERVER_ERROR_MARKS = ("server error", "error communicating with the server")


def server_error_on_settled_frame() -> bool:
    """Steht der Serverfehler-Dialog auf dem Standbild, das gerade gelesen wurde?

    Kostet nichts: `settled_point` hat Bild UND OCR schon geschrieben, hier wird nur
    dieselbe Datei noch einmal befragt.

    WARUM ES DAS GIBT: Am 2026-08-27 um 15:41 fiel der Dienst MITTEN im Scan aus. Die
    Navigation war um 15:39 noch erfolgreich, also griff die Serverfehler-Erkennung des
    Navigators nicht mehr -- der Lauf filmte drei Minuten lang einen Dialog, schrieb 44
    Zeilen als `end_unverified`, und im Protokoll stand bis zum naechsten
    Navigationsversuch nichts. Aufgefallen ist es dem Nutzer am Bildschirm.

    Der Zeilenriegel (`SUSPECT_ROW_FLOOR`) hat den Fall damals abgefangen, aber nur
    weil so wenige Zeilen da waren. Faellt der Dienst nach 8.000 Zeilen aus, ist das
    ein Board mit unbewiesenem Ende und reichlich Zeilen -- also ein falsch vermerkter
    Erfolg, und der wird fuer immer uebersprungen.
    """
    ocr = SETTLED_SCREEN / "current.ocr.json"
    try:
        data = json.loads(ocr.read_text(encoding="utf-8-sig"))
    except Exception:
        return False
    text = " ".join((line.get("text") or "") for line in data.get("lines", [])).lower()
    return any(mark in text for mark in SERVER_ERROR_MARKS)


def settled_point(vm: str) -> tuple[int, float, bool] | None:
    """One (rank, thumb position, at_bottom) triple off a SINGLE settled frame.

    Two hard-won details, both of which cost a wrong answer before:

    The frame must come from `capture_vm_current_screen.ps1`, not from the chunk that
    was just filmed. The chunk's own frames are not readable by the scrollbar detector
    -- an early probe run reported `scrollbar_unreadable` on every one of them -- which
    is why folding the measurement into the scan initially produced `no_points` on every
    chunk despite the rows banking fine.

    The rank must come off THAT SAME frame, never from the chunk's highest row. Pairing
    a chunk's maximum with a later screenshot read 4,782 against a view settled at
    2,335 and "proved" a 61,000-rank board whose real end was 29,697. And it is the
    MEDIAN of the visible column, not the maximum: a rank clipped by the table's top or
    bottom edge misreads, and the middle of eleven rows never is.
    """
    SETTLED_SCREEN.mkdir(parents=True, exist_ok=True)
    frame = SETTLED_SCREEN / "current.png"
    ocr = SETTLED_SCREEN / "current.ocr.json"
    # Erst die alten Dateien wegraeumen. Sonst liest ein FEHLGESCHLAGENES Standbild die
    # vorige Aufnahme und gibt sie als frische Messung zurueck -- gleicher Rang, gleicher
    # Balken. Der Scan schliesst daraus "kein neuer hoechster Rang" und beendet das Board
    # womoeglich zu frueh, oder verfaelscht die Balkenreihe, aus der die Boardlaenge
    # kommt. Gefunden vom Rauchtest am 2026-08-24: settled_point gegen eine nicht
    # existierende VM lieferte (4657, 0.1963, False) statt None.
    for stale in (frame, ocr):
        try:
            stale.unlink(missing_ok=True)
        except Exception:
            pass
    try:
        if local_target.is_local(vm):
            if not local_target.capture_settled_screen(SETTLED_SCREEN):
                return None
        else:
            subprocess.run(
                ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                 str(SCRIPTS / "capture_vm_current_screen.ps1"),
                 "-VMName", vm, "-OutputDir", str(SETTLED_SCREEN)],
                capture_output=True, text=True, timeout=300)
    except Exception as error:
        # Ein stiller None hier ist von einem echt oben klebenden Scrollbalken nicht zu
        # unterscheiden -- beides endet als "board length not yet measurable".
        log(f"  WARNUNG Standbild fehlgeschlagen: {type(error).__name__} {error}")
        return None
    if not frame.exists() or not ocr.exists():
        return None
    try:
        data = json.loads(ocr.read_text(encoding="utf-8-sig"))
    except Exception:
        return None
    ranks = []
    for line in data.get("lines", []):
        # The rank column sits at the far left; the gamertag, car, PI and lap time all
        # carry digits too, so anything right of x=210 is not a rank.
        if line.get("x", 9999) > 210:
            continue
        text = (line.get("text") or "").strip().replace(",", "")
        if text.isdigit():
            ranks.append(int(text))
    if not ranks:
        return None
    try:
        bar = measure_scrollbar(frame, 0.5, 12.0, 40.0, 0.01, 12)
    except Exception:
        return None
    if not bar.get("found"):
        return None
    ranks.sort()
    return (ranks[len(ranks) // 2], float(bar["position"]),
            bool(bar.get("at_bottom")))


def estimate_length(points: list[tuple[int, float]]) -> tuple[int | None, str]:
    """How long the board is, from (rank, thumb position) pairs gathered while scanning.

    This is the whole reason a separate depth pass is no longer run. A probe was doing
    exactly what a scan does -- navigate, scroll, film, OCR -- and then deleting the
    rows, which cost ~66,600 real rows on 2026-08-23 alone. A scan already parks the
    thumb once per chunk, so the length falls out of readings it is taking anyway.

    Two points beat one: a single ratio assumes the thumb reads 0 at rank 0, and a
    minimum rendered thumb height breaks precisely that near the top.
    """
    if not points:
        return None, "no_points"
    last_rank, last_pos = points[-1]
    if len(points) >= 2:
        first_rank, first_pos = points[0]
        d_rank, d_pos = last_rank - first_rank, last_pos - first_pos
        if d_rank > 0 and d_pos > MIN_THUMB_SPREAD:
            return int(round(d_rank / d_pos)), "slope"
    if last_pos >= MIN_THUMB_POSITION:
        return int(round(last_rank / last_pos)), "single_point"
    # Pinned with no spread yet. Not a length, and NOT a floor either -- three boards
    # read this way and all three measured ~4,570 on a clean re-read. Keep scanning;
    # the spread arrives on its own once the scan has scrolled further.
    return None, "thumb_pinned"


def scan_board(vm: str, category: str, route_index: int, klass: str, track: str,
               *, chunk_seconds: int, max_chunks: int, workers: int,
               out_root: Path, log_path: Path,
               row_cap: int | None = None) -> dict | None:
    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    run_id = f"ocr_{category.replace(' ', '')}_idx{route_index:02d}_{klass}_{stamp}"
    run_root = out_root / run_id
    staging = out_root / "_chunks"
    staging.mkdir(parents=True, exist_ok=True)

    rows: dict[int, dict] = {}
    ocr_queue: list = []
    banked_chunks: list = []
    pool = ThreadPoolExecutor(max_workers=1)
    thumb_points: list[tuple[int, float]] = []
    implied, estimator = None, "no_points"
    highest = 0
    rejections = 0
    # Der zuletzt gepruefte Fingerabdruck. Kein blosses Ja/Nein: die drei
    # niedrigsten Raenge aendern sich waehrend des Scans noch, und dann ist es ein
    # anderer Schluessel, der eine neue Pruefung verdient.
    zwilling_geprueft = ()
    end_status = "chunk_budget_spent"
    for chunk in range(1, max_chunks + 1):
        archive = staging / f"{run_id}_c{chunk:02d}.zip"
        size = capture_chunk(vm, chunk_seconds, archive)
        if size == 0:
            log(f"  chunk {chunk}: nothing captured", log_path)
            end_status = "capture_failed"
            break
        # The chunk's OCR no longer blocks the next capture. Nothing in the decision
        # below needs it: the rank, the thumb and at_bottom all come off one settled
        # screenshot that the guest OCRs itself and returns as ~500 KB, never touching
        # the ~350 MB zip. Measured 2026-08-23: OCR was 73 s of every 251 s chunk, and
        # the game sat idle for all of it -- which is also when it dies.
        ceiling = highest + MAX_CHUNK_ADVANCE
        ocr_queue.append((chunk, pool.submit(ocr_zip, archive, workers, None,
                                            run_root, log_path, klass), ceiling))

        # Fold in whatever finished while we were filming. The invalid-tail test runs
        # on these, so it lags one chunk -- the same lag the old rule already had, since
        # a boundary falling mid-chunk never tripped a 60% threshold either.
        added = invalid = impossible = ocrd = 0
        still = []
        for idx, future, cap_ceiling in ocr_queue:
            if not future.done():
                still.append((idx, future, cap_ceiling))
                continue
            try:
                fresh = future.result()
            except Exception as error:
                log(f"  chunk {idx}: OCR failed ({error})", log_path)
                continue
            ocrd += len(fresh)
            for row in fresh:
                rank = int(row["rank"])
                if rank > cap_ceiling:
                    impossible += 1
                    continue
                if row.get("is_clean") is False:
                    invalid += 1
                if rank not in rows:
                    rows[rank] = row
                    added += 1
            banked_chunks.append((idx, len(fresh), invalid))
        ocr_queue = still

        # DER ZWILLINGS-RIEGEL, SO FRUEH WIE ER GEHEN KANN.
        #
        # Es gibt einen Ausfall, den keine Namenspruefung sieht: der Streckenname
        # schaltet korrekt weiter, die BESTENLISTE darunter bleibt die alte. Gefunden
        # wird er nur am Fingerabdruck -- den drei Spitzenzeiten.
        #
        # Bis zum 2026-09-14 lief diese Pruefung erst NACH dem ganzen Scan. Am selben
        # Tag kostete das zweimal einen vollstaendigen Durchlauf, der danach verworfen
        # wurde: `idx08 C` trug die Liste von idx09, `idx12 A` die von idx13 -- letztere
        # nach 31,8 Minuten Aufnahme.
        #
        # Die drei Spitzenzeiten stehen aber ganz oben im Board, also im ersten Chunk.
        # Sobald die Raenge 1 bis 3 eingebucht sind, ist der Fingerabdruck vollstaendig
        # -- und wenn er einem gespeicherten Board gehoert, hat Weiterfilmen keinen
        # Zweck mehr.
        #
        # DIE DREI NIEDRIGSTEN RAENGE -- nicht die Raenge 1, 2, 3.
        #
        # Mein erster Versuch verlangte genau 1, 2 und 3, weil der gespeicherte
        # Abdruck aus den ersten drei ZEILEN der abgelegten Datei stammt. Das war
        # falsch gedacht: die Datei wird `sorted(rows)` geschrieben, ihre ersten
        # drei Zeilen sind also die drei NIEDRIGSTEN VORHANDENEN Raenge -- und die
        # sind oft nicht 1, 2, 3. Am 2026-09-14 hatte `idx12 A` als niedrigste die
        # Raenge 2, 3, 4; Rang 1 wurde nie erfasst. Die Bedingung trat nie ein, der
        # Riegel schwieg, und der spaete fing es nach 32,7 Minuten ab -- genau die
        # Minuten, die zu sparen der ganze Zweck war.
        #
        # Geprueft wird bei JEDEM Chunk und nicht einmal: welche drei Raenge die
        # niedrigsten sind, aendert sich noch, solange gefilmt wird. Der gemerkte
        # Abdruck verhindert nur, dass derselbe Schluessel zweimal den Bestand
        # durchsucht.
        if len(rows) >= 3:
            frueh = tuple(rows[r].get("lap_time_seconds") for r in sorted(rows)[:3])
            if any(frueh) and frueh != zwilling_geprueft:
                zwilling_geprueft = frueh
                zwilling = stale_content_twin(category, klass, route_index, frueh)
                if zwilling >= 0:
                    log(f"  diese Bestenliste steht schon unter Index {zwilling} "
                        f"(gleiche Bestzeiten {frueh}). Die Anzeige ist nicht "
                        f"mitgewandert -- Aufnahme abgebrochen, statt noch eine "
                        f"halbe Stunde zu filmen.", log_path)
                    end_status = "stale_twin"
                    break

        point = settled_point(vm)
        # Vor jeder anderen Deutung: laeuft der Dienst ueberhaupt noch? Ein Ausfall
        # sieht von hier aus genauso aus wie ein Boardende -- keine neuen Raenge, kein
        # lesbarer Balken -- und wurde bis 2026-08-27 auch so gedeutet.
        if server_error_on_settled_frame():
            log("  der Server-Error-Dialog steht auf dem Schirm: der Dienst ist weg, "
                "das ist kein Boardende. Abbruch dieses Boards.", log_path)
            end_status = "server_error"
            break
        if point:
            thumb_points.append((point[0], point[1]))
        # The scan's position now comes from the settled frame, not from rows that may
        # still be in the OCR queue.
        top = max(point[0] if point else 0, supported_top(rows))
        implied, estimator = estimate_length(thumb_points)
        raw = max(rows) if rows else 0
        phantom = f", raw max {raw}" if raw != top else ""
        refused = f", {impossible} past rank {ceiling} refused" if impossible else ""
        if implied:
            done = top / (min(implied, row_cap) if row_cap else implied)
            # Count down to whatever actually stops this board. With a cap set that is
            # the cap, not the board's length -- reporting "~14.9 h left" on a board due
            # to stop in 25 minutes is worse than reporting nothing.
            target = min(implied, row_cap) if row_cap else implied
            left_h = max(0.0, (target - top)) / (RANKS_PER_MINUTE * 60)
            # Past ~95% the depth figure stops predicting the end: a board does not stop
            # at its estimated length, it stops when a chunk comes back mostly invalid
            # (or on the cap). Printing "99.3% deep, ~0.0 h left" while filming on for
            # another two chunks reads as a hang, so say what it is really waiting for.
            if row_cap and top >= row_cap * 0.95:
                progress = (f", board ~{implied:,} [{estimator}], "
                            f"{top:,}/{row_cap:,} of the baseline cap")
            elif done >= 0.95:
                progress = (f", board ~{implied:,} [{estimator}], {done:.1%} deep -- "
                            f"now filming into the invalid tail, stops when a chunk is "
                            f">60% invalid (last was {invalid} of {ocrd})")
            else:
                progress = (f", board ~{implied:,} [{estimator}], {done:.1%} deep, "
                            f"~{left_h:.1f} h left")
        else:
            progress = f", board length not yet measurable [{estimator}]"
        # Say when nothing was folded in because the OCR is still running. Under the
        # pipeline that is the normal state of chunk 1, and "+0 ranks (total 0)" reads
        # like a failed read rather than like work in flight.
        if ocrd == 0 and ocr_queue:
            banked = (f"OCR of {len(ocr_queue)} chunk(s) still running, "
                      f"{len(rows)} ranks banked so far")
        else:
            banked = (f"+{added} ranks (total {len(rows)}, max {top}{phantom}, "
                      f"{invalid} invalid of {ocrd} read{refused})")
        log(f"  chunk {chunk}: {banked}{progress}", log_path)

        # Judged over whatever OCR completed this round, which may be more than one
        # chunk or none at all -- `fresh` is no longer a single chunk's rows.
        if ocrd and invalid > ocrd * 0.6:
            log("  the chunk is mostly invalid laps: the useful board is done", log_path)
            end_status = "invalid_tail"
            break
        if top <= highest:
            # "The list stopped moving" was read as "the board ended" until
            # 2026-08-23, and the two are different events: the game also stops
            # serving while a page is still on its way. Only the scrollbar can tell
            # them apart, so ask it before banking a short board.
            # Reuse this chunk's settled reading. This branch used to call
            # read_scrollbar(last_frame) on the chunk's own frames -- which the
            # detector cannot read at all -- so `bar` was always None and every
            # stalled board ended as `end_unverified`, never as a proven end.
            bar = None if point is None else {"position": point[1],
                                              "at_bottom": point[2]}
            if bar is None:
                log("  no new highest rank and the scrollbar could not be read; "
                    "stopping, end UNVERIFIED", log_path)
                end_status = "end_unverified"
                break
            if bar.get("at_bottom"):
                log(f"  no new highest rank and the thumb is at the bottom "
                    f"({bar['position']:.1%}) -- genuine end of board", log_path)
                end_status = "end_detected"
                break
            rejections += 1
            if rejections > MAX_END_REJECTIONS:
                log(f"  thumb still at {bar['position']:.1%} after {MAX_END_REJECTIONS} "
                    f"further chunks -- stopping; board is TRUNCATED, not finished", log_path)
                end_status = "truncated"
                break
            log(f"  no new highest rank, but the thumb is only at {bar['position']:.1%} "
                f"of the bar -- the list continues; filming another chunk "
                f"({rejections}/{MAX_END_REJECTIONS})", log_path)
            continue
        rejections = 0
        highest = top

        # A deliberate breadth-first cap: take a baseline from every board before
        # deepening any. `row_cap_reached` is its own status precisely so a capped
        # board is never read later as finished (`end_detected`) or as a failure
        # (`truncated`) -- it is a board we chose to stop, with a known length
        # already measured, and it is the list of boards to revisit when the cap
        # goes up.
        if row_cap is not None and top >= row_cap:
            log(f"  reached the {row_cap:,}-rank baseline cap at {top:,} "
                f"(board ~{implied:,} [{estimator}])" if implied else
                f"  reached the {row_cap:,}-rank baseline cap at {top:,}", log_path)
            end_status = "row_cap_reached"
            break

    # Drain what is still being OCR'd. These rows belong to this board and the whole
    # point of queueing was to keep the game moving, not to drop the tail of the work.
    if ocr_queue:
        log(f"  waiting on {len(ocr_queue)} queued chunk(s) of OCR", log_path)
    for idx, future, cap_ceiling in ocr_queue:
        try:
            fresh = future.result()
        except Exception as error:
            log(f"  chunk {idx}: OCR failed ({error})", log_path)
            continue
        for row in fresh:
            rank = int(row["rank"])
            if rank > cap_ceiling:
                continue
            if rank not in rows:
                rows[rank] = row
    ocr_queue = []
    pool.shutdown(wait=True)

    if not rows:
        return None
    # Whether the end was PROVED matters as much as the rows: a board that stopped
    # without the thumb at the bottom must not be counted as finished later.
    meta = {"run_id": run_id, "track": track, "performance_class": klass,
            "rivals_mode": category, "route_index": route_index,
            "end_status": end_status, "end_rejections": rejections,
            "row_cap": row_cap,
            # Measured during the scan, not by a separate pass.
            "implied_total": implied, "length_estimator": estimator,
            "thumb_points": [{"rank": r, "position": round(p, 4)}
                             for r, p in thumb_points],
            "row_coverage": (round(len(rows) / implied, 4) if implied else None)}
    thin_proofs(run_root, list(rows.values()), log_path)
    report = write_board(run_root, rows, meta)
    # write_board's report describes the rows; the caller also needs to know HOW the
    # board ended, which is the difference between a proven end and a stop.
    report["end_status"] = end_status
    report["implied_total"] = implied
    report["run_root"] = str(run_root)
    log(f"  board written: {report['rows']} rows, {report['minimum_rank']}.."
        f"{report['maximum_rank']}, {report['missing_rank_count']} missing, "
        f"{report['pi_read']} with PI ({report['pi_out_of_class']} outside {klass} "
        f"dropped) [{end_status}]", log_path)
    return report


# Unterhalb dieser Zeilenzahl ist ein Board mit unbewiesenem Ende ein Abbruch. Die
# duennsten echten Boards dieses Bestands liegen bei einigen Tausend Zeilen; die drei
# frueheren `end_unverified`-Faelle hatten 7.298, 9.196 und 10.800. Der Ausfall vom
# 27.08. hatte 89.
SUSPECT_ROW_FLOOR = 500
REJECT_ROOT = WORKSPACE / "data/memory_scans/_rejected"


def board_fingerprint(run_root) -> tuple:
    """Die drei ersten Rundenzeiten eines Laufs -- der Fingerabdruck eines Boards.

    Zwei Bestenlisten mit denselben drei Spitzenzeiten SIND dieselbe Bestenliste. Als
    Schluessel reichen die Zeiten: sie stehen auf Tausendstel genau da und
    unterscheiden sich zwischen Strecken um Sekunden.

    Der Maximalrang gehoert AUSDRUECKLICH NICHT dazu. Zwei Laeufe ueber dasselbe Board
    kommen verschieden tief (6875 gegen 6976 bei Legend Island C); mit dem Rang im
    Schluessel bliebe genau so ein Paar unentdeckt.
    """
    try:
        pfad = Path(run_root) / "rows.jsonl"
        zeilen = pfad.read_text(encoding="utf-8").splitlines()[:3]
        werte = tuple(json.loads(z).get("lap_time_seconds") for z in zeilen)
        return werte if any(werte) else ()
    except Exception:
        return ()


def stale_content_twin(category: str, klass: str, route_index: int,
                       fingerabdruck: tuple) -> int:
    """Gibt es dieselbe Bestenliste schon unter einer ANDEREN Streckennummer?

    Gibt deren Nummer zurueck, sonst -1.

    WOFUER -- der zweite Ausfallmodus, gefunden am 2026-09-05:
    Bis dahin kannte das Skript nur den Fall "Navigation schaltet nicht weiter, der
    Schirm zeigt den ALTEN Streckennamen"; den faengt foreign_route_match() ab. Es gibt
    aber einen zweiten: der Name schaltet korrekt weiter, die BESTENLISTE DARUNTER
    bleibt die alte. Dagegen ist jede Namenspruefung blind, denn der Name stimmt ja.

    So entstanden vier weitere falsche Boards, die erst ein nachtraeglicher Vergleich
    ueber 394 Boards fand:
        idx17 A und idx18 A trugen beide die Daten von idx10 (107,459 s)
        idx02 R trug die von idx01 (73,944 s)
        idx04 S2 trug die von idx03 (57,947 s)
    Alle drei Gruppen stimmten bis auf die dritte Nachkommastelle ueberein -- das ist
    kein Zufall, das ist dieselbe Liste.

    Verglichen wird nur innerhalb derselben Kategorie UND Klasse. Ueber Klassen hinweg
    waere ein Treffer moeglich, wenn dieselbe Strecke in zwei Klassen dieselbe Bestzeit
    haette; das ist selten, aber es waere ein Fehlalarm ohne Erkenntnis.
    """
    if not fingerabdruck:
        return -1
    wurzel = WORKSPACE / "data" / "memory_scans" / "full_sweep"
    for p in sorted(wurzel.glob("ocr_*")):
        zustand = p / "state.json"
        if not zustand.exists():
            continue
        try:
            st = json.loads(zustand.read_text(encoding="utf-8-sig"))
        except Exception:
            continue
        if st.get("rivals_mode") != category or st.get("performance_class") != klass:
            continue
        anderer = st.get("route_index")
        if anderer is None or int(anderer) == int(route_index):
            continue
        if board_fingerprint(p) == fingerabdruck:
            return int(anderer)
    return -1


def reject_run(report: dict, log_path: Path | None = None) -> None:
    """Einen Lauf aus dem Bestand nehmen, den die Auswertung liest.

    Der Aufbau liest `data/memory_scans/full_sweep/*/state.json`; ein Verzeichnis
    unter `_rejected` sieht er nie. Verschoben statt geloescht, weil die Zeilen die
    Beweise fuer den Ausfall sind.
    """
    run_root = report.get("run_root")
    if not run_root:
        return
    src = Path(run_root)
    if not src.exists():
        return
    try:
        REJECT_ROOT.mkdir(parents=True, exist_ok=True)
        shutil.move(str(src), str(REJECT_ROOT / src.name))
        log(f"  beiseitegelegt: {REJECT_ROOT / src.name}", log_path)
    except Exception as error:
        log(f"  konnte den Lauf nicht beiseitelegen: {error}", log_path)


SITE_LOG = WORKSPACE / "data/runtime/overnight/site_build.log"


# Wie hoch eine Zeile im Bildschirmfoto steht, und wie viele Belege je Auto bleiben.
# Beides aus dem OCR-Schritt uebernommen, damit die Werte nicht auseinanderlaufen.
PROOF_ROW_TOP = 12
PROOF_ROW_HEIGHT = 54.2
PROOF_PER_CAR = 10
PROOF_NAME = "proof.zip"


def save_proofs(rows: list[dict], folder: Path, board_root: Path,
                log_path: Path | None = None) -> int:
    """Je gelesener Zeile den Bildausschnitt sichern, solange die Bilder noch da sind.

    WARUM: die Seite zeigt eine Rundenzeit unter einem Autonamen, und beides stammt aus
    derselben gelesenen Zeile. Stimmt der Name nicht, faellt das niemandem auf -- am
    2026-09-10 stand die 0:51.211 auf Daikoku unter "Chevrolet Corvette '53", waehrend
    auf dem Schirm "Corvette 15" stand. Mit dem Ausschnitt daneben braucht das niemand
    zu glauben, er sieht es.

    NICHT das ganze Bild: ein Rahmen sind 195 KB, ein Board 14.000 Rahmen, alle Boards
    zusammen 1,5 TB. Der Zeilenausschnitt ist als WebP 3 KB (gemessen), das Board damit
    63 MB -- und nach dem Ausduennen auf die besten zehn je Auto rund 18 MB.
    """
    try:
        from PIL import Image
    except ImportError:
        return 0
    board_root.mkdir(parents=True, exist_ok=True)
    ziel = board_root / PROOF_NAME
    geschrieben = 0
    try:
        with zipfile.ZipFile(ziel, "a", zipfile.ZIP_STORED) as bundle:
            vorhanden = set(bundle.namelist())
            offen: dict[str, "Image.Image"] = {}
            try:
                for row in rows:
                    rang = row.get("rank")
                    zeile = row.get("row_index")
                    bild_name = row.get("source_frame")
                    if rang is None or zeile is None or not bild_name:
                        continue
                    eintrag = f"r{int(rang):08d}.webp"
                    if eintrag in vorhanden:
                        continue
                    pfad = folder / bild_name
                    if not pfad.exists():
                        continue
                    bild = offen.get(bild_name)
                    if bild is None:
                        if len(offen) > 3:
                            for alt_name, alt_bild in offen.items():
                                alt_bild.close()
                            offen.clear()
                        bild = Image.open(pfad).convert("RGB")
                        offen[bild_name] = bild
                    oben = int(PROOF_ROW_TOP + int(zeile) * PROOF_ROW_HEIGHT)
                    unten = int(oben + PROOF_ROW_HEIGHT) - 4
                    if unten > bild.height:
                        continue
                    schnipsel = bild.crop((0, oben, bild.width, unten))
                    puffer = io.BytesIO()
                    schnipsel.save(puffer, "WEBP", quality=70)
                    bundle.writestr(eintrag, puffer.getvalue())
                    vorhanden.add(eintrag)
                    geschrieben += 1
            finally:
                for bild in offen.values():
                    bild.close()
    except Exception as fehler:
        # Ein fehlender Beleg ist aergerlich, ein abgebrochener Scan teuer.
        log(f"  WARNUNG Belege nicht gesichert: {fehler}", log_path)
        return 0
    return geschrieben


def thin_proofs(board_root: Path, rows: list[dict],
                log_path: Path | None = None) -> None:
    """Nur die schnellsten Runden je Auto behalten.

    Ein Ausschnitt ist 7,1 KB (gemessen an echten Rahmen, nicht geschaetzt). Alle Zeilen
    eines Boards waeren damit rund 150 MB und der ganze Bestand 80 GB; die besten zehn
    je zugeordnetem Auto sind rund 43 MB je Board und 24 GB insgesamt -- und
    beantworten dieselbe Frage, denn gezeigt wird immer eine der schnellen Runden,
    nie Rang 14.000.
    """
    ziel = board_root / PROOF_NAME
    if not ziel.exists():
        return
    # Nach dem ZUGEORDNETEN Auto gruppieren, nicht nach dem rohen Bildschirmnamen: von
    # dem gibt es je Board rund 1.900 Varianten ("Acura Integra", "I| Acura Integra",
    # "EEE Acura Integra"), und jede bekaeme sonst ihre eigenen zehn Belege.
    zuordnen = None
    try:
        import build_car_roster as roster_tools
        katalog = roster_tools.load_roster(Path("data/car_catalogue/fh6cars.html"))
        verzeichnis = roster_tools.build_index(katalog)
        schluessel = list(verzeichnis)
        jahre = roster_tools.build_year_index(katalog)
        merker: dict[str, str] = {}

        def zuordnen(name: str) -> str:
            if name not in merker:
                sauber = roster_tools.clean_ocr_name(name)
                auto, _ = (roster_tools.match_car(sauber, verzeichnis, schluessel, jahre)
                           if sauber else (None, 0.0))
                merker[name] = (f"{auto['name']}|{auto['year']}" if auto else name)
            return merker[name]
    except Exception:
        zuordnen = None

    besten: dict[str, list[tuple[float, int]]] = {}
    for row in rows:
        name = str(row.get("car_name") or "").strip()
        zeit = row.get("lap_time_seconds")
        rang = row.get("rank")
        if not name or zeit is None or rang is None:
            continue
        gruppe = zuordnen(name) if zuordnen else name
        besten.setdefault(gruppe, []).append((float(zeit), int(rang)))
    behalten = set()
    for name, laeufe in besten.items():
        for _zeit, rang in sorted(laeufe)[:PROOF_PER_CAR]:
            behalten.add(f"r{rang:08d}.webp")
    try:
        with zipfile.ZipFile(ziel) as bundle:
            alle = bundle.namelist()
            bleibt = [n for n in alle if n in behalten]
            inhalte = {n: bundle.read(n) for n in bleibt}
        with zipfile.ZipFile(ziel, "w", zipfile.ZIP_STORED) as bundle:
            for n, daten in inhalte.items():
                bundle.writestr(n, daten)
        log(f"  Belege: {len(bleibt)} von {len(alle)} behalten "
            f"({ziel.stat().st_size / 1e6:.1f} MB)", log_path)
    except Exception as fehler:
        log(f"  WARNUNG Belege nicht ausgeduennt: {fehler}", log_path)


def rebuild_site(log_path: Path | None = None) -> None:
    """Seite neu bauen, ohne den Scan warten zu lassen.

    Gemessen 2026-08-24: der Aufbau braucht 119 s und waechst mit dem Datensatz. Er lief
    blockierend nach JEDEM Board, also ~8% Zusatzaufwand fuer ein Ergebnis, das der Scan
    nicht braucht -- bei 23 offenen Boards knapp 46 Minuten.

    Nicht parallel starten: zwei Laeufe wuerden dieselbe HTML-Datei schreiben. Laeuft
    schon einer, wird uebersprungen; das naechste Board baut ohnehin neu.

    Der Rueckgabewert wird in SITE_LOG festgehalten, weil ein stiller Fehlschlag hier
    heute schon einmal den ganzen Seitenaufbau unbemerkt lahmgelegt hat.
    """
    try:
        running = subprocess.run(
            ["powershell.exe", "-NoProfile", "-Command",
             "@(Get-CimInstance Win32_Process | Where-Object { "
             "$_.CommandLine -match 'build_analytics_site' }).Count"],
            capture_output=True, text=True, timeout=60).stdout.strip()
        if running.isdigit() and int(running) > 1:
            log("  Seitenaufbau laeuft noch, uebersprungen", log_path)
            return
    except Exception:
        pass
    SITE_LOG.parent.mkdir(parents=True, exist_ok=True)
    try:
        handle = SITE_LOG.open("a", encoding="utf-8")
        print("=== " + datetime.now().strftime("%Y-%m-%d %H:%M:%S")
              + " ===", file=handle)
        handle.flush()
        subprocess.Popen([sys.executable, str(SCRIPTS / "build_analytics_site.py")],
                         stdout=handle, stderr=handle)
        log("  Seitenaufbau im Hintergrund gestartet", log_path)
    except Exception as error:
        log(f"  WARNUNG Seitenaufbau nicht startbar: {error}", log_path)


def refresh_car_catalogue(route_index: int, klass: str, log_path) -> None:
    """Dump carId -> codename while THIS board's cars are still loaded.

    Der Katalog liegt als zusammenhaengendes Feld im Spielspeicher, aber nur die
    Seiten der gerade geladenen Autos sind eingelagert: ein Lauf am 2026-08-26 fand
    229 Codenamen in carId 4011..4239 und sonst nichts, und darum stand die Abdeckung
    seit Wochen bei 270 von 657. Jedes Board fuehrt einen anderen Autosatz, also
    waechst der Katalog genau dann, wenn nach JEDEM Board gelesen wird -- von Hand
    ist das nie passiert.

    Nur lesend (PROCESS_VM_READ, keine Injektion), keine Eingaben ans Spiel, ~20 s.
    Ein Fehlschlag wird protokolliert und sonst ignoriert: der Katalog ist eine
    Zugabe, kein Grund, einen Sweep zu beenden.
    """
    script = Path(__file__).resolve().parent / "update_car_catalogue.ps1"
    if not script.exists():
        return
    try:
        done = subprocess.run(
            ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass",
             "-File", str(script)],
            capture_output=True, text=True, timeout=300)
    except (OSError, subprocess.TimeoutExpired) as error:
        log(f"idx{route_index:02d} {klass}: car catalogue skipped ({error})", log_path)
        return
    tail = [line for line in (done.stdout or "").splitlines()
            if "catalogue now holds" in line or line.startswith("merge:")]
    log(f"idx{route_index:02d} {klass}: car catalogue "
        f"{'; '.join(tail) if tail else f'exit {done.returncode}'}", log_path)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--vm", default="ForzaScrapeVM")
    parser.add_argument("--local", action="store_true",
                        help="Auf DIESEM Rechner scannen statt in der VM -- fuer "
                             "jemanden, der Forza selbst laufen hat. Der Rechner "
                             "gehoert dann fuer die Dauer dem Scanner: jeder "
                             "Tastendruck geht an das Fenster im Vordergrund.")
    # type=str.strip: die Kategorie kommt ueber mehrere Schalen
    # (start.cmd -> cmd.exe -> python), und jede kann ein Leerzeichen
    # anhaengen. Einmal hier saeubern ist billiger als an jeder Vergleichsstelle.
    parser.add_argument("--category", default="Road Racing", type=str.strip)
    # Leer = aus dem Katalog holen (Position 0 der Kategorie). "Highway Circuit" ist
    # Road Racings Anker; fuer Street Racing ist es "Daikoku Chase", und mit dem
    # falschen Anker findet der Navigator nichts, von dem er zaehlen koennte.
    parser.add_argument("--anchor", default="")
    parser.add_argument("--route-indices", default="6-22")
    parser.add_argument("--classes", default=",".join(CLASSES))
    parser.add_argument("--chunk-seconds", type=int, default=120)
    parser.add_argument("--max-chunks", type=int, default=12)
    # Breadth-first baseline: take this many ranks from every board before deepening
    # any of them. Boards shorter than the cap still finish outright -- 17 of the 35
    # measured on 2026-08-23 were under 20,000.
    # A 20,000-rank baseline over 42 reachable boards is ~20 h, so it cannot finish in
    # one night. Without this the next night restarts at route 0 class D and re-reads
    # everything it already has.
    parser.add_argument("--progress-file", type=Path, default=None,
                        help="record finished boards here and skip them on a later run")
    parser.add_argument("--row-cap", type=int, default=None,
                        help="stop a board once this rank is reached (end_status "
                             "row_cap_reached); omit to scan to the board's end")
    parser.add_argument("--workers", type=int, default=12)
    parser.add_argument("--until-hour", type=int, default=10)
    parser.add_argument("--out-root", type=Path,
                        default=WORKSPACE / "data/memory_scans/full_sweep")
    args = parser.parse_args(argv)

    # Der Anker ist der Name auf Position 0 der Kategorie. Ihn hier EINMAL zu setzen
    # ist sicherer, als ihn an drei Aufrufstellen einzusetzen -- eine vergessene
    # Stelle waere ein Lauf mit Road Racings Anker in einer fremden Kategorie.
    if not args.anchor:
        args.anchor = category_anchor(args.category) or "Highway Circuit"
    if getattr(args, "local", False):
        args.vm = local_target.LOCAL
    refusal = refuse_unverified_category(args.category)
    if refusal:
        print(refusal)
        return 2

    if "-" in args.route_indices:
        low, high = args.route_indices.split("-")
        indices = list(range(int(low), int(high) + 1))
    else:
        indices = [int(part) for part in args.route_indices.split(",") if part.strip()]
    classes = [part.strip() for part in args.classes.split(",") if part.strip()]

    log_root = WORKSPACE / "data/runtime/overnight"
    log_root.mkdir(parents=True, exist_ok=True)
    log_path = log_root / f"ocr_sweep_{datetime.now():%Y%m%d_%H%M%S}.log"
    log(f"{len(indices)} route(s) x {len(classes)} class(es); log {log_path}", log_path)

    # Start warm. Without -FromLeaderboard the navigator that finds a leaderboard calls
    # the run finished without re-selecting anything, so it never prints the route name
    # and the board is skipped -- which is what happened on the first board after every
    # restart. Only a VM reset leaves the game somewhere else, and that path clears it.
    # Resume state. Keyed BOTH ways on purpose: the (index, class) pair is all we know
    # before navigating, but carousel indices drift -- the catalogue warns the same walk
    # needed 3 presses one run and 5 the next -- so the resolved track name is recorded
    # too and is the key that survives a drift.
    progress = {"pairs": [], "boards": []}
    if args.progress_file and args.progress_file.exists():
        progress = json.loads(args.progress_file.read_text(encoding="utf-8"))
        log(f"resuming: {len(progress['boards'])} board(s) already done", log_path)
    done_pairs = {tuple(x) for x in progress.get("pairs", [])}
    done_boards = {tuple(x) for x in progress.get("boards", [])}

    def remember(route_index, klass, track):
        if not args.progress_file:
            return
        done_pairs.add((route_index, klass))
        if track:
            done_boards.add((track, klass))
        args.progress_file.parent.mkdir(parents=True, exist_ok=True)
        args.progress_file.write_text(json.dumps(
            {"row_cap": args.row_cap,
             "pairs": sorted(list(p) for p in done_pairs),
             "boards": sorted(list(b) for b in done_boards)}, indent=1), encoding="utf-8")

    warm = True
    done = 0
    nav_failures = 0
    # WENN DIE NAVIGATION KLAPPT, ABER DAS BILD FEHLT.
    #
    # Am 2026-09-14 ab 02:36 lieferte die Aufnahme im Gast bei JEDEM Board
    # "nothing captured", waehrend die Navigation sauber jede Strecke erreichte
    # ("route is 'Stadium Cross-Country Circuit'"). Der Lauf ist danach eine Stunde
    # lang durch vier Klassen marschiert und hat nichts geholt: `nav_failures`
    # zaehlt nur Navigationsfehler, und davon gab es keine.
    #
    # Ein Board ohne ein einziges Bild ist nie das Board -- es ist immer der Gast.
    # Zwei hintereinander sind ein Zustand, den kein Weitermachen heilt.
    blindlauf = 0
    # Kennt der Katalog fuer diese Kategorie ueberhaupt Strecken? Nur dann darf
    # "kein Eintrag" als "ueberspringen" gelten.
    known_here = any(catalogue_route(i, args.category) for i in indices)
    if not known_here:
        log(f"WARNUNG der Katalog kennt keine einzige Strecke von "
            f"'{args.category}' -- es wird ohne Vorfilter navigiert", log_path)
    server_boards = 0
    for route_index in indices:
        for klass in classes:
            if (route_index, klass) in done_pairs:
                log(f"idx{route_index:02d} {klass}: already done on an earlier run; skipping",
                    log_path)
                continue
            # Ohne Katalognamen wird dieses Board ohnehin verworfen -- dann muss man
            # auch nicht hinnavigieren. Cross-Country hat sechs solcher Positionen
            # (5, 7, 8, 12, 15, 16): absichtlich unbenannt, weil die Namensernte dort
            # zu uneinig war. Bis 2026-09-03 kostete jede davon eine volle Navigation
            # von ~40 s, sieben Mal je Durchlauf -- rund 28 Minuten fuer nichts. Und
            # jede dieser Navigationen konnte fehlschlagen und ueber "zweimal
            # gescheitert" einen VM-Neustart ausloesen, der selbst ein Risiko ist.
            #
            # Die Bedingung "der Katalog kennt die Kategorie ueberhaupt" muss dabei
            # stehen: fehlt oder bricht die Katalogdatei, waeren sonst ALLE Boards
            # stumm uebersprungen und der Lauf taete stundenlang nichts.
            if known_here and not catalogue_route(route_index, args.category):
                log(f"idx{route_index:02d} {klass}: kein Katalogeintrag zu Index "
                    f"{route_index}; ohne Navigation uebersprungen", log_path)
                continue
            if datetime.now().hour >= args.until_hour and datetime.now().hour < 20:
                log(f"past {args.until_hour}:00 -- stopping", log_path)
                return 0
            # PLATZ ZUERST. Ohne ihn schreibt der Gast keine Bilder, und der
            # ganze Lauf sieht danach nach einem Navigationsproblem aus.
            frei = platz_im_gast(args.vm, log_path)
            if 0 <= frei < 8.0:
                log(f"ABBRUCH: der VM sind nur noch {frei:.1f} GB frei. Ohne Platz "
                    f"kann sie keine Bildschirmaufnahmen schreiben -- der Lauf "
                    f"saehe danach aus wie ein Navigationsfehler und waere keiner. "
                    f"Aufraeumen mit: python scripts/prune_captures.py --apply",
                    log_path)
                return 0
            if 0 <= frei < 25.0:
                log(f"  nur noch {frei:.1f} GB in der VM frei -- raeume auf", log_path)
                try:
                    subprocess.run([sys.executable,
                                    str(SCRIPTS / "prune_captures.py"), "--apply"],
                                   capture_output=True, text=True, timeout=900)
                    log(f"  jetzt {platz_im_gast(args.vm, log_path):.1f} GB frei",
                        log_path)
                except Exception as fehler:
                    log(f"  Aufraeumen ging nicht ({fehler})", log_path)

            log(f"idx{route_index:02d} {klass}: navigating", log_path)
            # Rueckzugsschleife um die Navigation: ein Serverfehler beendet nichts, er
            # verschiebt nur. Ohne sie hiess "Server Error" faktisch "Nacht zu Ende".
            server_waits = 0
            while True:
                reached, route_name, server_error, crashed = navigate(
                    args.vm, args.category, route_index, klass, args.anchor, warm)
                if crashed:
                    # NEU STARTEN STATT WEITERDRUECKEN. Ein zweiter Anlauf gegen ein
                    # abgestuerztes Spiel kostet fuenf Minuten und aendert nichts;
                    # was hilft, ist genau das, was reset_guest ohnehin kann.
                    log(f"idx{route_index:02d} {klass}: das Spiel ist abgestuerzt "
                        "-- Gast wird zurueckgesetzt", log_path)
                    if reset_guest(args.vm, log_path):
                        reached, route_name, server_error, crashed = navigate(
                            args.vm, args.category, route_index, klass,
                            args.anchor, False)
                    else:
                        log(f"idx{route_index:02d} {klass}: Zuruecksetzen ging nicht "
                            "-- dieses Board wird uebersprungen", log_path)
                        break
                if not reached or not route_name:
                    # The right flag depends on where the game happens to be: on a
                    # leaderboard it needs -FromLeaderboard to back out and re-select,
                    # at the title screen that flag is wrong. Rather than guess --
                    # guessing cost an hour of "not reachable" -- try the other way
                    # once before calling it a failure.
                    log(f"idx{route_index:02d} {klass}: retrying from the other screen state",
                        log_path)
                    vorher = route_name
                    reached, route_name, again, crashed = navigate(
                        args.vm, args.category, route_index, klass, args.anchor, not warm)
                    server_error = server_error or again
                    # EIN LEERER ZWEITER VERSUCH LOESCHT KEINEN GUTEN ERSTEN.
                    # Der zweite Anlauf steht oft schon auf dem Board und hat
                    # darum nichts mehr zu bestaetigen; seinen leeren Namen zu
                    # uebernehmen warf die Bestaetigung des ersten weg.
                    if not route_name and vorher:
                        route_name = vorher
                    if reached and route_name:
                        warm = not warm
                if not reached:
                    nav_failures += 1
                    # One failure is usually the board's own: no personal time posted
                    # for that track and class, which no amount of retrying changes.
                    # Two in a row is the guest, and marching on would spend the night
                    # failing.
                    if nav_failures >= 2:
                        nav_failures = 0
                        if (guest_looks_wedged(args.vm, log_path)
                                and reset_guest(args.vm, log_path)):
                            reached, route_name, again, crashed = navigate(
                                args.vm, args.category, route_index, klass,
                                args.anchor, False)
                            server_error = server_error or again
                if reached:
                    break
                # Ein Serverfehler sagt nichts ueber dieses Board -- also warten und
                # dasselbe Board erneut versuchen, statt es als unerreichbar abzulegen
                # (das wuerde es fuer diesen Lauf verlieren). Drei Rueckzuege zu je 8
                # Minuten sind knapp eine halbe Stunde; danach ist es kein Aussetzer
                # mehr und der Lauf geht zum naechsten Board weiter, statt sich
                # festzubeissen.
                stop_now = (datetime.now().hour >= args.until_hour
                            and datetime.now().hour < 20)
                if server_error and server_waits < 3 and not stop_now:
                    server_waits += 1
                    log(f"idx{route_index:02d} {klass}: Server Error -- "
                        f"{SERVER_ERROR_WAIT // 60} min Rueckzug, "
                        f"Versuch {server_waits}/3", log_path)
                    # Erst den Weg freiraeumen, dann warten. Ohne das laeuft die
                    # Wiederholung gegen denselben modalen Dialog wie zuvor -- siehe
                    # dismiss_dialog().
                    dismiss_dialog(args.vm, log_path)
                    time.sleep(SERVER_ERROR_WAIT)
                    warm = True
                    continue
                break
            if not reached:
                why = "Server Error, auch nach Rueckzug" if server_error else "not reachable"
                log(f"idx{route_index:02d} {klass}: {why}; next", log_path)
                warm = True
                if server_error:
                    server_boards += 1
                    if server_boards >= SERVER_ERROR_GIVE_UP:
                        log(f"{server_boards} Boards in Folge am Serverfehler gescheitert "
                            f"-- der Leaderboard-Dienst ist offenbar nicht erreichbar. "
                            f"Lauf wird beendet statt weiter anzuklopfen.", log_path)
                        return 0
                    log(f"zweites Board in Folge am Serverfehler -- "
                        f"{SERVER_ERROR_LONG_WAIT // 60} min Pause", log_path)
                    time.sleep(SERVER_ERROR_LONG_WAIT)
                continue
            server_boards = 0
            nav_failures = 0
            warm = True
            route_name, name_how = full_track_name(route_name)
            expected = catalogue_route(route_index, args.category)

            # ZUERST: nennt der Schirm eine ANDERE Karussellposition dieser Kategorie?
            #
            # Dann ist die Navigation nicht weitergeschaltet, und es wird NICHT
            # gescannt -- egal wie gut der Name lesbar war und egal ob der Katalog
            # etwas zu diesem Index weiss.
            #
            # Das ist nicht theoretisch. Am 2026-09-03 hat ein Fingerabdruck-Vergleich
            # (Kategorie + Klasse + drei Bestzeiten) FUENF Cross-Country-Boards
            # gefunden, die in Wahrheit die Daten der VORIGEN Strecke enthielten:
            #   idx02 C == idx01, idx14 D == idx13, idx11 B == idx10,
            #   idx18 B == idx17, idx04 A == idx03
            # In zwei Faellen gewann der Bildschirmname (beide Boards hiessen gleich),
            # in dreien hat der KATALOGERSATZ den korrekt gelesenen Namen ueberschrieben
            # und die Falschbeschriftung damit erst erzeugt. Road Racing und Street
            # Racing waren sauber; es trifft nur das Karussell von Cross-Country.
            #
            # Eine fruehere Fassung hatte genau diese Sperre und ich habe sie am
            # 2026-09-02 wieder herausgenommen, weil sie idx02 wiederholt blockierte.
            # Das war der Fehler: das Blockieren war RICHTIG -- idx02 wurde tatsaechlich
            # nie erreicht. Ein offenes Board kostet einen Scan, ein falsch
            # beschriftetes verfaelscht die Auswertung unbemerkt.
            foreign = foreign_route_match(route_index, args.category, route_name)
            if foreign >= 0:
                log(f"idx{route_index:02d} {klass}: der Schirm nennt {route_name!r} "
                    f"-- das ist Index {foreign}, nicht {route_index}. Die Navigation "
                    f"ist nicht weitergeschaltet; es wird NICHT gescannt, das Board "
                    f"bleibt offen.", log_path)
                continue

            if name_how not in ("exact", "prefix"):
                # Mode 5 und Mode 11: ein nicht aufloesbarer Name wuerde die Zeilen unter
                # einer Erfindung ablegen. Frueher wurde hier nur gewarnt und trotzdem
                # gebannt -- so entstand 'I @ 2,666,890' als Streckenname. Jetzt gilt die
                # bestaetigte Karussellposition, und nur wenn auch die nichts hergibt,
                # wird das Board uebersprungen.
                # DIE MELDUNG NENNT DEN MODUS. Sie hiess bis zum 2026-09-14
                # "Streckenname unlesbar ('Nangan Cross-Country Circuit')" -- und in
                # den Klammern stand der sauber WIEDERHERGESTELLTE Name. Wer das
                # liest, haelt die Lesung fuer gescheitert; tatsaechlich hat der
                # Auflöser sie nur ueber `similar` statt `exact` geschafft.
                #
                # Mich hat genau das an diesem Tag zu einer Zaehlung ueber die
                # Protokolltexte und von dort zu einer falschen Warnung ueber die
                # Datenqualitaet gefuehrt: "Cross-Country loest in 0 von 46 Faellen
                # auf" stimmte fuer `exact`/`prefix` und war als Aussage ueber die
                # Kategorie falsch. Eine Meldung, die den Modus verschweigt, laedt
                # zu diesem Fehlschluss ein.
                if expected:
                    log(f"idx{route_index:02d} {klass}: Name nur ueber '{name_how}' "
                        f"aufgeloest ({route_name!r}); es gilt der Katalogname zu "
                        f"Index {route_index}: {expected!r}", log_path)
                    route_name, name_how = expected, "catalogue"
                else:
                    log(f"idx{route_index:02d} {klass}: Name nur ueber '{name_how}' "
                        f"aufgeloest ({route_name!r}) und kein Katalogeintrag zu "
                        f"Index {route_index}; uebersprungen", log_path)
                    continue
            elif expected and expected != route_name:
                # Beide Quellen lesbar und uneins, aber der Schirm nennt KEINE andere
                # bekannte Position (das waere oben abgefangen worden). Dann ist es eine
                # Lese-Abweichung, kein Navigationsfehler -- der Schirm gilt, aber es
                # muss auffallen.
                log(f"idx{route_index:02d} {klass}: WARNUNG Bildschirm sagt "
                    f"{route_name!r}, Katalog sagt {expected!r} fuer Index "
                    f"{route_index}", log_path)
            started = time.monotonic()
            try:
                if not route_name:
                    # Without a confirmed name the rows cannot be filed against a track,
                    # and guessing would repeat the mistake this check exists for.
                    log(f"idx{route_index:02d} {klass}: route name unread; skipping",
                        log_path)
                    continue
                log(f"idx{route_index:02d} {klass}: route is '{route_name}'", log_path)
                report = scan_board(args.vm, args.category, route_index, klass,
                                    route_name, chunk_seconds=args.chunk_seconds,
                                    max_chunks=args.max_chunks, workers=args.workers,
                                    row_cap=args.row_cap,
                                    out_root=args.out_root, log_path=log_path)
            except Exception as error:
                log(f"idx{route_index:02d} {klass}: failed ({error})", log_path)
                warm = False
                continue
            # BLIND GELAUFEN? Dann hilft nur der Gast, nicht das naechste Board.
            if report and report.get("end_status") == "capture_failed" \
                    and not report.get("rows"):
                blindlauf += 1
                log(f"idx{route_index:02d} {klass}: kein einziges Bild aufgenommen "
                    f"({blindlauf}. Mal in Folge) -- das ist der Gast, nicht das Board",
                    log_path)
                if blindlauf >= 2:
                    blindlauf = 0
                    log("  zweimal blind -- Gast zuruecksetzen, statt weiter "
                        "Strecken abzuklappern", log_path)
                    reset_guest(args.vm, log_path)
                    warm = False
                continue
            if report and report.get("rows"):
                # Ein Board mit Bildern setzt den Zaehler zurueck: gezaehlt wird
                # eine SERIE, nicht die Summe ueber die Nacht.
                blindlauf = 0

            if report:
                done += 1
                minutes = (time.monotonic() - started) / 60.0
                log(f"idx{route_index:02d} {klass}: done in {minutes:.1f} min "
                    f"-> {report['rows'] / max(0.1, minutes):.0f} ranks/min "
                    f"[{report.get('end_status', '?')}]", log_path)
                # Ein Dienstausfall sieht aus wie ein Boardende. Am 2026-08-27 um 04:06
                # lieferte der Leaderboard-Dienst nichts mehr; Coastline Sprint A wurde
                # mit 89 Zeilen und `end_unverified` als ERLEDIGT eingetragen, und der
                # naechste Navigationsversuch meldete dann den Server Error. Ein Board,
                # dessen Ende nicht bewiesen ist UND das kaum Zeilen hat, ist kein
                # duennes Board -- es ist ein abgebrochener Lesevorgang. Es wird nicht
                # vermerkt und der Lauf wird beiseitegelegt, damit weder die Auswertung
                # ihn sieht noch der naechste Durchgang ihn ueberspringt.
                # Ein waehrend des Lesens erkannter Dienstausfall zaehlt nie als Board
                # -- auch mit 8.000 Zeilen nicht. Ohne diese Zeile haengt die Frage, ob
                # der Ausfall vermerkt wird, an der Zeilenzahl, und genau das ist die
                # Falle: viele Zeilen machen den Abbruch nicht zum Board.
                if report.get("end_status") == "server_error":
                    log(f"idx{route_index:02d} {klass}: der Dienst fiel mitten im Lesen "
                        f"aus ({report['rows']} Zeilen). Nicht vermerkt, Lauf "
                        f"beiseitegelegt; das Board bleibt offen.", log_path)
                    reject_run(report, log_path)
                    warm = True
                    continue
                unproven = report.get("end_status") in ("end_unverified", "capture_failed",
                                                        "truncated", "chunk_budget_spent")
                if unproven and report["rows"] < SUSPECT_ROW_FLOOR:
                    log(f"idx{route_index:02d} {klass}: nur {report['rows']} Zeilen und "
                        f"Ende unbewiesen -- das ist ein Abbruch, kein Board. Nicht "
                        f"vermerkt, Lauf beiseitegelegt.", log_path)
                    reject_run(report, log_path)
                    warm = True
                    continue

                # UND GEGEN DIE EIGENE SCHAETZUNG, nicht nur gegen eine feste Zahl.
                #
                # Am 2026-09-12 starb die VM mitten in idx04 R: 11.447 Zeilen kamen
                # herein, der Scrollbalken hatte da laengst ~22.366 hochgerechnet --
                # die halbe Bestenliste. Weil 11.447 ueber SUSPECT_ROW_FLOOR (500)
                # lag, wurde das Board als ERLEDIGT vermerkt, und die Kategorie stand
                # danach auf "35 von 35". Ein halbes Board, als ganzes verbucht, ist
                # schlimmer als gar keines: es faellt nie wieder jemandem auf.
                #
                # Die Schaetzung stammt aus Messwerten desselben Laufs (Rang gegen
                # Balkenstellung) und ist damit der bessere Massstab als jede feste
                # Grenze. 80 % lassen der Schaetzung ihre Unschaerfe, schlagen aber
                # bei einem Abbruch auf halber Strecke sicher an.
                implied = report.get("implied_total")
                if unproven and isinstance(implied, int) and implied > 0:
                    anteil = report["rows"] / implied
                    if anteil < 0.80:
                        log(f"idx{route_index:02d} {klass}: {report['rows']} Zeilen von "
                            f"geschaetzten {implied} ({anteil:.0%}) und Ende unbewiesen "
                            f"-- abgebrochen, nicht fertig. Nicht vermerkt, Lauf "
                            f"beiseitegelegt.", log_path)
                        reject_run(report, log_path)
                        warm = True
                        continue
                # Traegt dieses Board die Bestenliste einer ANDEREN Strecke?
                # Siehe stale_content_twin(): der Streckenname kann korrekt sein und
                # die Liste darunter trotzdem die alte. Das faellt sonst NIE auf --
                # die Zeilen sind echt, die Zahlen plausibel, nur die Strecke stimmt
                # nicht.
                abdruck = board_fingerprint(report.get("run_root"))
                zwilling = stale_content_twin(args.category, klass, route_index, abdruck)
                if zwilling >= 0:
                    log(f"idx{route_index:02d} {klass}: diese Bestenliste steht bereits "
                        f"unter Index {zwilling} (gleiche Bestzeiten {abdruck}). Die "
                        f"Anzeige ist nicht mitgewandert; nicht vermerkt, Lauf "
                        f"beiseitegelegt, das Board bleibt offen.", log_path)
                    reject_run(report, log_path)
                    warm = True
                    continue
                remember(route_index, klass, route_name)
                refresh_car_catalogue(route_index, klass, log_path)
                # Rueckgabewert PRUEFEN. Am 2026-08-24 hat eine Aenderung am
                # Streckenkatalog known_routes() mit einem TypeError abgeschossen und
                # damit jeden Seitenaufbau -- unbemerkt, weil hier nur die Ausgabe
                # eingefangen und nie angesehen wurde. Der Scan sammelte munter weiter,
                # die Seite blieb stehen. Ein Fehler, der schweigt, ist teurer als einer,
                # der laut ist.
                # Nicht nach JEDEM Board. Der Aufbau dauert inzwischen ~15 Minuten und
                # waechst mit dem Datensatz, ein Board dauert 15-25 -- also lief bisher
                # praktisch dauernd einer parallel zur OCR. Das kostet doppelt: einmal
                # Rechenzeit fuer ein Ergebnis, das niemand zwischen zwei Boards ansieht,
                # und einmal in Abstuerzen. Am 2026-08-25 und 26 starb je ein OCR-Arbeiter
                # mit BrokenProcessPool bei 11-14 GB freiem Speicher, waehrend ein Aufbau
                # alle Parquets einlas; die Wiederholung mit 4 statt 12 Arbeitern kam
                # beide Male durch. Alle fuenf Boards heisst rund alle 1,5 Stunden --
                # frisch genug, um ueber Nacht den Fortschritt zu sehen.
                if SITE_EVERY <= 0:
                    log("  Seitenaufbau erst am Ende des Laufs (FORZA_SITE_EVERY=0)",
                        log_path)
                elif done % SITE_EVERY == 0:
                    rebuild_site(log_path)
                else:
                    log(f"  Seitenaufbau erst nach Board {SITE_EVERY - done % SITE_EVERY} weiter",
                        log_path)
    # Zum Schluss immer, damit der letzte Stand nicht bis zum naechsten Lauf wartet.
    rebuild_site(log_path)
    log(f"finished; {done} board(s) scanned", log_path)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
