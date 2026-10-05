"""Rauchtest fuer ocr_board_sweep: laeuft die FEHLERPFADE wirklich durch.

Warum es das gibt: am 2026-08-24 wurde in ocr_zip() eine Meldung fuer fehlgeschlagene
OCR eingebaut, die auf `result.returncode` zugriff -- aber der Unterprozessaufruf wies
sein Ergebnis keiner Variable zu. Jeder Chunk starb daraufhin mit
`name 'result' is not defined`, der Sweep lief sieben Minuten und bankte nichts.

Die vorhandene Testreihe hat es nicht gefunden, weil sie ocr_zip durch eine Attrappe
ersetzt -- sie prueft die Ablauflogik, nie den echten Code. Und die Syntaxpruefung sieht
eine undefinierte Variable nicht, die faellt erst zur Laufzeit auf.

Also: hier werden die echten Funktionen mit Eingaben aufgerufen, die sie zum Scheitern
bringen. Verlangt wird nicht Erfolg, sondern ein SAUBERES Scheitern -- kein NameError,
kein AttributeError, sondern die vorgesehene Meldung und ein leeres Ergebnis.

    python scripts/test_sweep_smoke.py
"""

from __future__ import annotations

import sys
import tempfile
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import ocr_board_sweep as sweep  # noqa: E402

FAILURES: list[str] = []


def check(name: str, fn) -> None:
    try:
        fn()
        print(f"  ok    {name}")
    except Exception as error:
        FAILURES.append(name)
        print(f"  FAIL  {name} -- {type(error).__name__}: {error}")


def ocr_zip_survives_a_useless_archive() -> None:
    """ocr_zip auf einem Zip ohne Bilder: muss leer zurueckkommen, nicht platzen.

    Genau der Pfad, auf dem der `result`-Fehler lag. Die OCR findet keine Frames und
    schreibt keine rows.jsonl, also laeuft die Fehlermeldung an -- und die darf sich
    nicht selbst zerlegen.
    """
    work = Path(tempfile.mkdtemp())
    archive = work / "nonsense.zip"
    with zipfile.ZipFile(archive, "w") as bundle:
        bundle.writestr("kein_bild.txt", "dies ist kein PNG")
    rows = sweep.ocr_zip(archive, workers=1)
    assert isinstance(rows, list), f"kein list, sondern {type(rows)}"
    assert rows == [], f"unerwartete Zeilen: {rows[:3]}"


def settled_point_survives_a_dead_vm() -> None:
    """settled_point gegen eine VM, die es nicht gibt: None, keine Ausnahme.

    In ein EIGENES Verzeichnis umgelenkt. Beim ersten Lauf dieses Tests hat er in
    data/runtime/scan_screen geschrieben -- dasselbe Verzeichnis, aus dem ein laufender
    Sweep seine Standbilder liest -- und dessen Dateien geloescht. Ein Test darf die
    Produktion nicht anfassen.
    """
    original = sweep.SETTLED_SCREEN
    sweep.SETTLED_SCREEN = Path(tempfile.mkdtemp()) / "screen"
    try:
        point = sweep.settled_point("EineVMDieEsNichtGibt-" + "x" * 12)
        assert point is None, f"erwartet None, bekam {point}"
    finally:
        sweep.SETTLED_SCREEN = original


def estimate_length_handles_every_shape() -> None:
    """Der Schaetzer darf auf keiner Eingabe platzen."""
    cases = [
        [],
        [(1, 0.0)],
        [(0, 0.0), (0, 0.0)],
        [(100, 0.5)],
        [(2339, 0.0085), (9327, 0.0254)],
        [(5, 1.0), (5, 1.0)],
    ]
    for points in cases:
        total, how = sweep.estimate_length(points)
        assert how, f"kein Verfahren fuer {points}"
        assert total is None or total > 0, f"{points} -> {total}"


def full_track_name_handles_junk() -> None:
    """Namenskorrektur darf an keiner Lesung scheitern."""
    for reading in ["", "Highway Circui", "Routes", "ÄÖÜ", "x" * 200,
                    "Electhe toyi?Ciicuit"]:
        name, how = sweep.full_track_name(reading)
        assert isinstance(name, str) and how, f"{reading!r} -> {name!r}, {how!r}"


def rebuild_site_does_not_raise() -> None:
    """Der Seitenaufbau laeuft im Hintergrund; ein Fehlstart darf den Sweep nicht toeten."""
    sweep.rebuild_site(None)


def navigate_separates_server_error_from_board_failure() -> None:
    """Ein Serverfehler muss als solcher zurueckkommen, nicht als "Board nicht erreichbar".

    Am 2026-08-25 um 07:21 sah ein erstmaliger "Server Error" wie ein unerreichbares
    Board aus, und der Nachtlauf wurde deswegen von Hand abgebrochen -- 1,5 Stunden
    Laufzeit umsonst. Seitdem meldet navigate() den Serverfehler getrennt, damit der
    Sweep zurueckweichen und dasselbe Board erneut anfahren kann.
    """
    # Vier Werte seit 2026-09-18: der vierte sagt "das Spiel ist abgestuerzt" -- kein
    # Navigationsfehlschlag, den man vom anderen Schirmzustand aus wiederholen koennte.
    cases = [
        ("Server Error / There was an error communicating with the server.",
         (False, "", True, False)),
        ("status: leaderboard_reached route index 7 confirmed on screen as 'Irokawa Circuit'",
         (True, "Irokawa Circuit", False, False)),
        ("FAILED Stuck in state 'rival_detail' after 55 cycles.", (False, "", False, False)),
        ("Game crashed: Video Card Crash. FAILED Stuck in state 'unknown'.", (False, "", False, True)),
        ("", (False, "", False, False)),
    ]
    original = sweep.powershell
    try:
        for output, expected in cases:
            sweep.powershell = lambda *a, **k: (1, output)
            got = sweep.navigate("VM", "Road Racing", 7, "S1", "Highway Circuit", True)
            assert got == expected, f"{output[:40]!r} -> {got}, erwartet {expected}"
    finally:
        sweep.powershell = original


def catalogue_route_never_returns_junk() -> None:
    """Der Katalogname zu einer Karussellposition -- immer Text, nie ein Objekt.

    Am 2026-08-26 las die OCR fuer Index 18 eine Punktestandszeile statt des Namens, und
    das Board landete unter der Strecke 'I @ 2,666,890'. Beim Reparieren habe ich dann
    den ganzen Katalogeintrag als Namen geschrieben -- die Eintraege sind Objekte, keine
    Zeichenketten. Beide Fehler faengt diese Pruefung ab.
    """
    for index in range(0, 23):
        name = sweep.catalogue_route(index)
        assert isinstance(name, str), f"Index {index} liefert {type(name).__name__}"
        assert name and not name.startswith("{"), f"Index {index} -> {name!r}"
        assert "@" not in name and not any(ch.isdigit() for ch in name[:2]),             f"Index {index} sieht nach einer Bildschirmzeile aus: {name!r}"
    for index in (-1, 99, 1000):
        assert sweep.catalogue_route(index) == "", f"Index {index} sollte leer sein"


if __name__ == "__main__":
    print("Rauchtest ocr_board_sweep -- echte Fehlerpfade")
    check("ocr_zip auf unbrauchbarem Archiv", ocr_zip_survives_a_useless_archive)
    check("settled_point gegen tote VM", settled_point_survives_a_dead_vm)
    check("estimate_length auf Grenzfaellen", estimate_length_handles_every_shape)
    check("full_track_name auf Muell", full_track_name_handles_junk)
    check("rebuild_site startet ohne Ausnahme", rebuild_site_does_not_raise)
    check("navigate trennt Serverfehler vom Boardfehler",
          navigate_separates_server_error_from_board_failure)
    check("catalogue_route liefert nie Muell", catalogue_route_never_returns_junk)
    print(f"\n{len(FAILURES)} FEHLER" if FAILURES else "\nalles bestanden")
    raise SystemExit(1 if FAILURES else 0)
