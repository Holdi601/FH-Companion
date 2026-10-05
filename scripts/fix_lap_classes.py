"""Abgelegte Runden in den richtigen Klassenordner umziehen.

    python scripts/fix_lap_classes.py            zeigen, was zu tun waere
    python scripts/fix_lap_classes.py --apply    tun

## Warum

Der Rundenbestand sortiert nach Strecke / KLASSE / Auto / Abstimmung / Merkzettel.
Die Klasse wurde aus dem PI abgeleitet -- mit Grenzen, die bis zum 2026-09-14 um
eine ganze Stufe zu niedrig lagen: "bis 500 ist D". Richtig ist

    D bis 400, C bis 500, B bis 600, A bis 700, S1 bis 800, S2 bis 900, R bis 998

So steht es auf der Klassenleiste des Spiels, so sagt es der Nutzer, und die
Garagen-Datenbank bestaetigt es unabhaengig ueber ihre Spalte `ClassID`.

Damit liegt JEDE vor der Berichtigung abgelegte Runde eine Stufe zu niedrig -- eine
S1-Runde unter "A", eine R-Runde unter "S2". Die Dateien selbst sind in Ordnung; nur
der Ordner luegt. Das ist keine Kleinigkeit: wer die Karte einer Klasse ansieht,
bekommt sonst die Runden der naechstniedrigeren dazu.

## Was NICHT angefasst wird

`personal_laps.json` und die Auswertung: dort steht die Klasse nirgends als Text,
sondern es wird mit dem rohen Feld gerechnet. Und `course.json` bleibt, wie sie ist
-- der Startpunkt darin ist der Anker, an dem spaetere Runden ihre Strecke
wiederfinden.
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import sys
from collections import Counter
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "server"))
from local_settings import app_data  # noqa: E402  (FHCompanion, bis 2026-09-26 ForzaGripHaptics)
STANDARD = app_data() / "laps"

# Dieselben Grenzen wie in LapArchive.ClassOf. Wer eine aendert, aendert beide.
KAPPEN = [(0, "unknown"), (400, "D"), (500, "C"), (600, "B"), (700, "A"),
          (800, "S1"), (900, "S2")]


def klasse(pi: int) -> str:
    if pi <= 0:
        return "unknown"
    for kappe, name in KAPPEN[1:]:
        if pi <= kappe:
            return name
    return "R"


def pi_von(datei: Path) -> int | None:
    try:
        d = json.loads(datei.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        return None
    lap = d.get("Lap") or d.get("lap")
    if not isinstance(lap, dict):
        return None
    pi = lap.get("performanceIndex", lap.get("PerformanceIndex"))
    return int(pi) if isinstance(pi, (int, float)) else None


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--wurzel", default=str(STANDARD))
    p.add_argument("--apply", action="store_true")
    args = p.parse_args(argv)

    wurzel = Path(args.wurzel)
    if not wurzel.exists():
        print("Kein Rundenbestand unter %s" % wurzel)
        return 1

    umzuege: list[tuple[Path, Path]] = []
    unklar = 0
    zaehler: Counter = Counter()

    for datei in wurzel.rglob("*.json"):
        if datei.name == "course.json":
            continue
        # Erwartet: wurzel/strecke/klasse/auto/abstimmung/merkzettel/datei
        rel = datei.relative_to(wurzel).parts
        if len(rel) != 6:
            continue
        pi = pi_von(datei)
        if pi is None:
            unklar += 1
            continue
        richtig = klasse(pi)
        if rel[1] == richtig:
            zaehler[("bleibt", rel[1])] += 1
            continue
        ziel = wurzel.joinpath(rel[0], richtig, *rel[2:])
        umzuege.append((datei, ziel))
        zaehler[(rel[1], richtig)] += 1

    print("%d Runde(n) liegen richtig, %d muessen umziehen, %d ohne PI"
          % (sum(n for (a, _), n in zaehler.items() if a == "bleibt"),
             len(umzuege), unklar))
    print()
    for (von, nach), n in sorted(zaehler.items()):
        if von == "bleibt":
            continue
        print("   %-8s -> %-4s  %d Runde(n)" % (von, nach, n))

    # DAS FELD IN DER DATEI IST EIN EIGENER SCHRITT.
    #
    # Mein erster Entwurf stieg bei "nichts umzuziehen" vorher aus -- und liess
    # damit genau den Fall liegen, der nach einem halb gelaufenen Umzug uebrig
    # bleibt: Ordner richtig, Feld `Class` in der Datei noch falsch. Aufgefallen
    # ist es in der Gegenprobe, weil die Uebersicht weiter die alten Klassen zeigte.
    if not args.apply:
        print("\nProbelauf. Mit --apply wird umgezogen und das Feld `Class` in\n"
              "den Dateien berichtigt.")
        return 0

    print()
    verschoben = 0
    leer = 0
    for quelle, ziel in umzuege:
        try:
            ziel.parent.mkdir(parents=True, exist_ok=True)
            if ziel.exists():
                # Derselbe Name im Zielordner: die Dateinamen tragen Zeitstempel und
                # Sekunden, also ist das dieselbe Runde. Dann genuegt die vorhandene.
                quelle.unlink()
            else:
                shutil.move(str(quelle), str(ziel))
            verschoben += 1
        except OSError as fehler:
            print("   %s: %s" % (quelle.name, fehler))

    # Leergeraeumte Klassenordner entfernen -- ein leerer "A"-Ordner neben einem
    # vollen "S1" sieht aus, als fehlten dort Runden.
    for ordner in sorted(wurzel.rglob("*"), key=lambda q: -len(q.parts)):
        if ordner.is_dir() and not any(ordner.iterdir()):
            try:
                ordner.rmdir()
                leer += 1
            except OSError:
                pass

    if umzuege:
        print("%d Runde(n) umgezogen, %d leere Ordner entfernt."
              % (verschoben, leer))
    print("%d Klassenfeld(er) in den Dateien berichtigt." % felder_richten(wurzel))
    return 0


def felder_richten(wurzel: Path) -> int:
    """Auch das Feld `Class` IN der Datei stand auf der alten Klasse.

    Der Ordner ist das eine, der Inhalt das andere: `LapArchive.Save` schreibt die
    Klasse zusaetzlich in die Runde selbst. Nur die Ordner umzuhaengen liesse in
    jeder Datei die falsche Angabe stehen -- und ausgewertet wird spaeter die Datei,
    nicht der Pfad.
    """
    n = 0
    for datei in wurzel.rglob("*.json"):
        if datei.name == "course.json":
            continue
        try:
            d = json.loads(datei.read_text(encoding="utf-8-sig"))
        except (OSError, ValueError):
            continue
        lap = d.get("Lap") or d.get("lap")
        if not isinstance(lap, dict):
            continue
        pi = lap.get("performanceIndex", lap.get("PerformanceIndex"))
        if not isinstance(pi, (int, float)):
            continue
        richtig = klasse(int(pi))
        schluessel = "Class" if "Class" in d else ("class" if "class" in d else None)
        if schluessel is None or d[schluessel] == richtig:
            continue
        d[schluessel] = richtig
        try:
            datei.write_text(json.dumps(d, separators=(",", ":")), encoding="utf-8")
            n += 1
        except OSError:
            pass
    return n


if __name__ == "__main__":
    sys.exit(main())
