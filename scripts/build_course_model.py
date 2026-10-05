"""Aus mehreren gefahrenen Runden den Rennkurs bilden -- Verlauf, Breite, Ausdehnung.

    python scripts/build_course_model.py
    python scripts/build_course_model.py --course course_-6350_-3750 --bin 25

## Warum

Eine Strecke ist ihr Linienzug, nicht ihre Laenge -- das hat der Abend des
2026-09-12/13 dreimal teuer bewiesen. Bisher steht dafuer ein fester Korridor von
50 m im Programm. Der ist geraten: eine Bergstrasse ist schmaler, eine Autobahn
breiter, und zwei parallele Strassen 60 m nebeneinander trennt ein fester Wert
ueberhaupt nicht.

Gemessen werden kann er aber, und zwar aus dem, was ohnehin anfaellt: JEDE gefahrene
Runde -- auch jeder Abbruch -- besteht aus Punkten, die sicher zur Strecke gehoeren.

## Was hier berechnet wird

- **Ausdehnung**: wie weit die Strecke reicht, gemessen an der Fahrt, die am
  weitesten kam. Sie ist die Grundlinie.
- **Deckung** je Fahrt: bis zu welchem Anteil dieser Grundlinie sie gekommen ist.
  Damit unterscheidet sich ein Abbruch von einer vollstaendigen Fahrt, ohne
  Laengen zu vergleichen.
- **Breite** je Abschnitt: wie weit die Fahrten seitlich auseinanderliegen. Das ist
  die gefahrene Breite der Strasse -- nicht ihre bauliche, aber die, auf die es
  ankommt.

Das Ergebnis ist eine Beschreibung, keine Entscheidung: es wird nichts eingetragen
und nichts verworfen. Die Zahlen sollen zeigen, ob die 50 m zu weit oder zu eng
sind, bevor die Regel im Programm daran haengt.
"""

from __future__ import annotations

import argparse
import json
import math
import os
import sys
from collections import defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "server"))
from local_settings import app_data  # noqa: E402  (FHCompanion, bis 2026-09-26 ForzaGripHaptics)
sys.path.insert(0, str(Path(__file__).resolve().parent))
from lap_folders import kennung, kurs_ordner, kurs_pfad, ist_unfertig  # noqa: E402,F401
WURZEL = app_data() / "laps"


def laden(pfad: Path) -> dict | None:
    try:
        d = json.loads(pfad.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None
    lap = d.get("Lap") or d.get("lap")
    if not lap or len(lap.get("samples") or []) < 5:
        return None
    lap["_datei"] = pfad.name
    return lap


def bogenlaengen(punkte: list[dict]) -> list[float]:
    """Der Weg entlang der eigenen Spur, Punkt fuer Punkt aufsummiert."""
    raus = [0.0]
    for a, b in zip(punkte, punkte[1:]):
        raus.append(raus[-1] + math.dist((a["X"], a["Y"], a["Z"]),
                                         (b["X"], b["Y"], b["Z"])))
    return raus


def projizieren(punkt: dict, basis: list[dict], bogen: list[float],
                start: int) -> tuple[float, float, int]:
    """Den Punkt auf die Grundlinie loten: (Bogenlaenge, seitlicher Abstand, Index).

    Gesucht wird in einem Fenster um die zuletzt getroffene Stelle -- eine Strecke
    kreuzt sich selbst, und ohne Gedaechtnis springt die Zuordnung an einer Bruecke
    auf die Strasse darunter.
    """
    px, py, pz = punkt["X"], punkt["Y"], punkt["Z"]
    bester = (float("inf"), 0.0, start)
    von, bis = max(0, start - 5), min(len(basis) - 1, start + 120)
    for i in range(von, bis):
        a, b = basis[i], basis[i + 1]
        ax, ay, az = a["X"], a["Y"], a["Z"]
        vx, vy, vz = b["X"] - ax, b["Y"] - ay, b["Z"] - az
        ll = vx * vx + vy * vy + vz * vz
        t = 0.0 if ll < 1e-9 else max(0.0, min(1.0, ((px - ax) * vx + (py - ay) * vy
                                                     + (pz - az) * vz) / ll))
        qx, qy, qz = ax + vx * t, ay + vy * t, az + vz * t
        d = math.dist((px, py, pz), (qx, qy, qz))
        if d < bester[0]:
            bester = (d, bogen[i] + math.sqrt(ll) * t, i)
    return bester[1], bester[0], bester[2]


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--course", help="nur diese Strecke (Ordnername)")
    parser.add_argument("--bin", type=float, default=50.0,
                        help="Abschnittslaenge fuer die Breitenmessung, in Metern")
    args = parser.parse_args(argv)

    if not WURZEL.is_dir():
        print(f"Kein Archiv unter {WURZEL}")
        return 1

    nach_strecke: dict[str, list[dict]] = defaultdict(list)
    for pfad in WURZEL.rglob("*.json"):
        if pfad.name == "course.json" or ist_unfertig(WURZEL, pfad):
            continue
        # Die Kennung, nicht der Ordnername ("Soni Circuit (course_...)").
        teil = pfad.relative_to(WURZEL).parts[0]
        strecke = kennung(teil) or teil
        if args.course and args.course not in (strecke, teil):
            continue
        lap = laden(pfad)
        if lap:
            nach_strecke[strecke].append(lap)

    if not nach_strecke:
        print("Keine Fahrten gefunden.")
        return 1

    for strecke, fahrten in sorted(nach_strecke.items()):
        if len(fahrten) < 2:
            continue
        # Grundlinie: die Fahrt mit dem laengsten eigenen Weg.
        fahrten.sort(key=lambda l: -l["lengthMetres"])
        basis = fahrten[0]
        bpunkte = basis["samples"]
        bbogen = bogenlaengen(bpunkte)
        ausdehnung = bbogen[-1]

        print(f"\n=== {strecke} — {len(fahrten)} Fahrten ===")
        print(f"Grundlinie: {basis['_datei']}  ({ausdehnung:.0f} m, "
              f"{basis['lapSeconds']:.2f} s)")
        print(f"{'Fahrt':34s} {'bis':>8s} {'Deckung':>8s} {'seitl. Mittel':>14s} "
              f"{'max':>7s}")

        breiten: dict[int, list[float]] = defaultdict(list)
        for lap in fahrten:
            hinweis = 0
            weiteste = 0.0
            seitlich: list[float] = []
            for punkt in lap["samples"]:
                bogen, ab, hinweis = projizieren(punkt, bpunkte, bbogen, hinweis)
                # Nur Punkte, die ueberhaupt zur Strasse gehoeren koennen.
                if ab > 150:
                    continue
                weiteste = max(weiteste, bogen)
                seitlich.append(ab)
                breiten[int(bogen // args.bin)].append(ab)
            deckung = weiteste / ausdehnung if ausdehnung else 0
            mittel = sum(seitlich) / len(seitlich) if seitlich else 0
            print(f"{lap['_datei'][:34]:34s} {weiteste:7.0f}m {deckung:7.1%} "
                  f"{mittel:13.1f}m {max(seitlich or [0]):6.1f}m")

        alle = sorted(x for werte in breiten.values() for x in werte)
        if alle:
            def perzentil(p: float) -> float:
                return alle[min(len(alle) - 1, int(len(alle) * p))]
            print(f"\nSeitliche Streuung ueber alle Fahrten: "
                  f"Median {perzentil(0.5):.1f} m, 95 % {perzentil(0.95):.1f} m, "
                  f"Maximum {alle[-1]:.1f} m")
            eng = [k for k, v in breiten.items() if max(v) < 5]
            weit = sorted(breiten.items(), key=lambda kv: -max(kv[1]))[:3]
            print(f"Engste Abschnitte: {len(eng)} von {len(breiten)} unter 5 m Streuung")
            for k, v in weit:
                print(f"  weitester Abschnitt bei {k * args.bin:.0f} m: "
                      f"{max(v):.1f} m Streuung ({len(v)} Punkte)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
