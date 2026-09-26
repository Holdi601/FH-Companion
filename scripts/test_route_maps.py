# -*- coding: utf-8 -*-
"""Prueft die Linienverfolgung von route_maps.py an gezeichneten Formen.

    python scripts/test_route_maps.py

An echten Karten laesst sich nur hinsehen (`route_maps.py --blatt`); hier stehen
Formen, deren richtiger Weg feststeht: ein Ring mit einer Luecke (wie unter dem
Startpfeil), eine S-Kurve, eine Schleife, die sich selbst kreuzt.
"""
from __future__ import annotations

import math
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, str(Path(__file__).resolve().parent))
import route_maps as rm  # noqa: E402

FEHLER: list[str] = []


def soll(gut: bool, was: str) -> None:
    if not gut:
        FEHLER.append(was)
        print("FEHLER", was)


def band(zeichnen, groesse=(420, 320)) -> np.ndarray:
    bild = Image.new("L", groesse, 0)
    zeichnen(ImageDraw.Draw(bild))
    # Dieselbe Vorbereitung wie fuer eine echte Karte.
    return rm.saeubern(np.asarray(bild) > 0)


def ring_mit_luecke() -> None:
    def z(d):
        # Ein Ring, 10 Punkte breit, oben 18 Grad offen -- dort sitzt im Spiel der Pfeil.
        d.arc((60, 40, 360, 280), start=-81, end=261, fill=255, width=10)
    m = band(z)
    weg, info = rm.weg_aus_maske(m)
    p = np.asarray([(x, y) for y, x in weg], float)
    luecke = math.hypot(*(p[0] - p[-1]))
    soll(info["abdeckung"] > 0.9, f"Ring: nur {info['abdeckung']:.0%} des Bandes verfolgt")
    soll(luecke < 60, f"Ring: Anfang und Ende {luecke:.0f} Punkte auseinander -- als Rundkurs nicht erkennbar")


def s_kurve() -> None:
    def z(d):
        pts = [(40 + t * 3.4, 160 + 90 * math.sin(t / 100 * 2 * math.pi)) for t in range(101)]
        d.line(pts, fill=255, width=10, joint="curve")
    m = band(z)
    weg, info = rm.weg_aus_maske(m)
    p = np.asarray([(x, y) for y, x in weg], float)
    enden = sorted([tuple(p[0]), tuple(p[-1])])
    soll(info["abdeckung"] > 0.95, f"S-Kurve: nur {info['abdeckung']:.0%} verfolgt")
    soll(enden[0][0] < 60 and enden[1][0] > 360,
         f"S-Kurve: die Enden liegen nicht an den Enden der Kurve ({enden})")


def schleife_mit_kreuzung() -> None:
    # Von links herein, oben herum, und quer ueber den eigenen Weg wieder hinaus:
    # an der Kreuzung muss der Zug GERADEAUS gehen, sonst fehlt die halbe Schleife.
    def z(d):
        pts = [(20, 250), (200, 250), (330, 170), (280, 60), (200, 40), (120, 60),
               (70, 170), (200, 250), (400, 250)]
        d.line(pts, fill=255, width=10, joint="curve")
    m = band(z)
    weg, info = rm.weg_aus_maske(m)
    soll(info["abdeckung"] > 0.85, f"Kreuzung: nur {info['abdeckung']:.0%} verfolgt -- an der Kreuzung abgebogen?")


def vereinfachen_und_glaetten() -> None:
    p = np.asarray([(i, (i // 3) % 2 * 2.0) for i in range(120)], float)
    g = rm.glaetten(p, False, schritt=1.0, sigma=3.0)
    soll(float(np.abs(g[20:-20, 1] - 1.0).max()) < 0.5, "Glaetten: die Treppe bleibt stufig")
    v = rm.vereinfachen(np.asarray([(i, 0.0) for i in range(50)], float), 0.5)
    soll(len(v) == 2, f"Vereinfachen: eine Gerade behaelt {len(v)} Punkte statt 2")


def verlaesslichkeit() -> None:
    # Klein und verschlungen (wie Shimanoyama Circuit): unsicher.
    t = np.linspace(0, 6 * math.pi, 400)
    knaeuel = np.stack([30 + 25 * np.cos(t) * np.sin(t / 3), 30 + 25 * np.sin(t)], axis=1)
    soll(not rm.verlaesslich(knaeuel, 10.0, 1.0), "Verlaesslich: ein kleines Knaeuel gilt als sicher")
    # Klein, aber gerade (Drag-Strecke): sicher.
    gerade = np.stack([np.linspace(0, 60, 50), np.zeros(50)], axis=1)
    soll(rm.verlaesslich(gerade, 10.0, 1.0), "Verlaesslich: eine gerade Drag-Strecke gilt als unsicher")
    # Gross und verschlungen: sicher.
    soll(rm.verlaesslich(knaeuel * 10, 10.0, 1.0), "Verlaesslich: eine grosse Strecke gilt als unsicher")
    # Zu wenig vom Band erfasst: unsicher.
    soll(not rm.verlaesslich(knaeuel * 10, 10.0, 0.6), "Verlaesslich: 60 % Abdeckung gilt als sicher")


def main() -> int:
    verlaesslichkeit()
    ring_mit_luecke()
    s_kurve()
    schleife_mit_kreuzung()
    vereinfachen_und_glaetten()
    if FEHLER:
        print(f"{len(FEHLER)} Fehler")
        return 1
    print("route_maps: alle Pruefungen bestanden")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
