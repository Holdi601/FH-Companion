"""Den aufgezeichneten Strecken einen Namen geben -- indem man sie ansieht.

    python scripts/name_courses.py                      auflisten
    python scripts/name_courses.py --karten             je Strecke ein Bild zeichnen
    python scripts/name_courses.py --setzen course_1300_275 "Hokubu Sprint"
    python scripts/name_courses.py --setzen-aus namen.txt
    python scripts/name_courses.py --zeiten             eigene Bestzeiten je Strecke

## Warum es das gibt

Die Telemetrie nennt keine Strecke. Der Rundenbestand ordnet darum nach dem ORT der
Start-Ziel-Linie ein, und die Ordner heissen `course_1300_275`. Das ist als Kennung
richtig und als Beschriftung unbrauchbar: seit es Time Attack in der freien Welt
gibt, steht dieser Name im Streifen ueber dem Spiel.

Das Feld `Name` in jeder `course.json` ist von Anfang an dafuer da. Was fehlte, war
eine Moeglichkeit herauszufinden, WELCHE Strecke `course_1300_275` ist.

## Was nicht funktioniert hat

**Ueber die Laenge.** Der Katalog kennt 89 Strecken mit Laengenangabe. Gemessen am
2026-09-14 gegen die 32 aufgezeichneten Kurse: KEIN EINZIGER wird dadurch eindeutig,
29 von 32 haben mehr als vier Kandidaten, einer hat 21. Die Laengen liegen zwischen
1 und 9 km und draengen sich entsprechend. Eine Kandidatenliste mit 21 Eintraegen ist
keine Hilfe, sondern eine Arbeitsbeschaffung.

**Ueber die eigene Bestzeit in der Bestenliste.** Waere schaerfer -- eine Rundenzeit
auf die Millisekunde ist so gut wie eindeutig. Der eigene Gamertag kommt in den
Rohscans aber nicht vor: die Scans nehmen die oberen Raenge, die eigene Zeit liegt
tiefer.

## Was funktioniert: hinsehen

Jede aufgezeichnete Runde bringt ihre Messpunkte mit, alle 5 m einen, mit
Weltkoordinaten. Daraus wird der Streckenverlauf gezeichnet. Wer die Strecke gefahren
ist, erkennt sie an ihrer Form in einem Augenblick -- und braucht dafuer weder eine
Kandidatenliste noch eine Karte des Spiels.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "server"))
from local_settings import app_data  # noqa: E402  (FHCompanion, bis 2026-09-26 ForzaGripHaptics)
sys.path.insert(0, str(Path(__file__).resolve().parent))
from lap_folders import kennung, kurs_ordner, kurs_pfad  # noqa: E402,F401
STANDARD = app_data() / "laps"

# Groesse des gezeichneten Bildes, in Bildpunkten.
BREITE = 760
RAND = 24


def lade_notiz(ordner: Path) -> dict | None:
    pfad = ordner / "course.json"
    if not pfad.exists():
        return None
    try:
        return json.loads(pfad.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        return None


def runden(ordner: Path) -> list[dict]:
    """Alle abgelegten Runden einer Strecke -- die Datei heisst nie course.json."""
    aus = []
    for f in sorted(ordner.rglob("*.json")):
        if f.name == "course.json":
            continue
        try:
            d = json.loads(f.read_text(encoding="utf-8-sig"))
        except (OSError, ValueError):
            continue
        lap = d.get("Lap") or d.get("lap")
        if isinstance(lap, dict):
            aus.append({"datei": f, "lap": lap, "klasse": d.get("Class") or d.get("class")})
    return aus


def punkte(lap: dict) -> list[tuple[float, float]]:
    """Die Weltkoordinaten einer Runde. Die Messpunkte sind JSON-Objekte."""
    aus = []
    for s in lap.get("samples") or lap.get("Samples") or []:
        if not isinstance(s, dict):
            continue
        x = s.get("X", s.get("x"))
        z = s.get("Z", s.get("z"))
        if x is None or z is None:
            continue
        if x == 0 and z == 0:
            continue
        aus.append((float(x), float(z)))
    return aus


def zeichne(ordner: Path, notiz: dict, fahrten: list[dict]) -> Path | None:
    """Ein SVG mit allen Fahrten dieser Strecke. Gibt den Pfad zurueck."""
    spuren = [p for p in (punkte(f["lap"]) for f in fahrten) if len(p) >= 3]
    if not spuren:
        return None

    alle = [p for spur in spuren for p in spur]
    xs = [p[0] for p in alle]
    zs = [p[1] for p in alle]
    minx, maxx = min(xs), max(xs)
    minz, maxz = min(zs), max(zs)
    spanne = max(maxx - minx, maxz - minz, 1.0)
    # QUADRATISCH und massstabsgetreu: eine Strecke auf das Bild zu strecken
    # veraendert ihre Form, und die Form ist hier das ganze Erkennungsmerkmal.
    skala = (BREITE - 2 * RAND) / spanne
    mittex = (minx + maxx) / 2
    mittez = (minz + maxz) / 2

    def bild(x: float, z: float) -> tuple[float, float]:
        # Z waechst im Spiel nach Norden, im Bild nach unten -- also umdrehen,
        # sonst liegt jede Strecke gespiegelt vor einem.
        return (BREITE / 2 + (x - mittex) * skala,
                BREITE / 2 - (z - mittez) * skala)

    teile = [
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{BREITE}" height="{BREITE}" '
        f'viewBox="0 0 {BREITE} {BREITE}">',
        f'<rect width="{BREITE}" height="{BREITE}" fill="#11161c"/>',
    ]
    for i, spur in enumerate(spuren):
        d = " ".join(
            ("M" if j == 0 else "L") + "%.1f %.1f" % bild(x, z)
            for j, (x, z) in enumerate(spur))
        # Die laengste Fahrt hell, die uebrigen blass: der Verlauf soll aus einer
        # Linie ablesbar sein, die anderen zeigen nur, wie sehr sie streuen.
        hell = len(spur) == max(len(s) for s in spuren)
        teile.append(
            f'<path d="{d}" fill="none" stroke="{"#7fd4ff" if hell else "#31536b"}" '
            f'stroke-width="{2.4 if hell else 1.2}" stroke-linejoin="round"/>')

    sx, sz = notiz.get("StartX", 0.0), notiz.get("StartZ", 0.0)
    if sx or sz:
        px, pz = bild(float(sx), float(sz))
        teile.append(f'<circle cx="{px:.1f}" cy="{pz:.1f}" r="7" fill="none" '
                     f'stroke="#ffd166" stroke-width="2.5"/>')
        teile.append(f'<circle cx="{px:.1f}" cy="{pz:.1f}" r="2.5" fill="#ffd166"/>')

    # Massstab: ohne ihn sieht eine 1-km-Schleife aus wie eine 9-km-Runde.
    km = spanne / 1000.0
    teile.append(
        f'<text x="{RAND}" y="{BREITE - RAND}" fill="#8fa3b5" '
        f'font-family="Segoe UI, sans-serif" font-size="15">'
        f'{ordner.name} &#183; {len(spuren)} Fahrt(en) &#183; '
        f'Bildkante {km:.1f} km &#183; Start = Ring</text>')
    teile.append("</svg>")

    ziel = ordner / "course_map.svg"
    ziel.write_text("\n".join(teile), encoding="utf-8")
    return ziel


def setze_namen(wurzel: Path, paare: list[tuple[str, str]]) -> int:
    geaendert = 0
    for ordner_name, name in paare:
        # Die Kennung genuegt: der Ordner heisst inzwischen "Name (course_...)".
        ordner = kurs_pfad(wurzel, ordner_name)
        notiz = lade_notiz(ordner)
        if notiz is None:
            print("  %-24s keine course.json -- uebersprungen" % ordner_name)
            continue
        alt = (notiz.get("Name") or "").strip()
        notiz["Name"] = name
        # Von Hand gesetzt: die App berichtigt ihn nie nach der Mehrheit der Runden
        # (LapArchive.NamenNachtragen) und verlegt den Kurs nie nach "unfinished".
        notiz["NameEvidence"] = "manual"
        # ALLE anderen Felder bleiben, wie sie sind -- vor allem StartX/StartZ.
        # Sie sind der Anker, an dem spaetere Runden diese Strecke wiederfinden;
        # ein verschobener Anker zerschneidet den Bestand.
        (ordner / "course.json").write_text(
            json.dumps(notiz, indent=2, ensure_ascii=False), encoding="utf-8")
        print("  %-24s %s%s" % (ordner_name, name,
                                (" (war: %s)" % alt) if alt else ""))
        geaendert += 1
    return geaendert


def zeiten_zeigen(ordner: list[Path]) -> int:
    """Je Strecke die Bestzeit, aufgeschluesselt nach Klasse und Auto.

    Die Ordnerstruktur ist die Auswertung -- Strecke / Klasse / Auto / Abstimmung --,
    nur liest sie sich als Dateibaum schlecht. Hier steht dasselbe als Tabelle.

    Gewertete und selbst gestoppte Fahrten werden GETRENNT ausgewiesen. Sie sind
    nicht dasselbe: in der freien Welt gibt es keine Streckengrenzen, und eine Zeit
    von dort neben einer Rennzeit stehen zu lassen waere eine Gegenueberstellung,
    die keine ist.
    """
    leer = True
    for d in ordner:
        notiz = lade_notiz(d)
        if notiz is None:
            continue
        fahrten = runden(d)
        if not fahrten:
            continue
        leer = False
        name = (notiz.get("Name") or "").strip()
        print("%s%s" % (d.name, ("  --  " + name) if name else ""))

        # Nach Klasse, Auto und Betriebsart buendeln; je Buendel die schnellste.
        beste: dict[tuple, tuple[float, float, int]] = {}
        for f in fahrten:
            lap = f["lap"]
            sek = float(lap.get("lapSeconds") or lap.get("LapSeconds") or 0)
            if sek <= 0:
                continue
            frei = bool(lap.get("freeRoam") or lap.get("FreeRoam"))
            schluessel = (f["klasse"] or "?",
                          int(lap.get("carOrdinal") or lap.get("CarOrdinal") or 0),
                          frei)
            laenge = float(lap.get("lengthMetres") or lap.get("LengthMetres") or 0)
            vorhanden = beste.get(schluessel)
            anzahl = (vorhanden[2] if vorhanden else 0) + 1
            if vorhanden is None or sek < vorhanden[0]:
                beste[schluessel] = (sek, laenge, anzahl)
            else:
                beste[schluessel] = (vorhanden[0], vorhanden[1], anzahl)

        for (klasse, auto, frei), (sek, laenge, anzahl) in sorted(beste.items()):
            print("    %-4s Auto %-6d %-10s %8.3f s  %6.0f m  aus %d Fahrt(en)"
                  % (klasse, auto, "freie Welt" if frei else "gewertet",
                     sek, laenge, anzahl))
    if leer:
        print("Keine abgelegten Runden gefunden.")
    return 0


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--wurzel", default=str(STANDARD),
                   help="Rundenbestand (Vorgabe: der der App)")
    p.add_argument("--karten", action="store_true",
                   help="je Strecke ein SVG mit dem Verlauf zeichnen")
    p.add_argument("--setzen", nargs=2, metavar=("ORDNER", "NAME"), action="append",
                   help="einen Namen eintragen; mehrfach moeglich")
    p.add_argument("--setzen-aus", metavar="DATEI",
                   help="Zeilen der Form 'course_x_z = Name'")
    p.add_argument("--zeiten", action="store_true",
                   help="je Strecke die Bestzeiten nach Klasse und Auto")
    args = p.parse_args(argv)

    wurzel = Path(args.wurzel)
    if not wurzel.exists():
        print("Kein Rundenbestand unter %s" % wurzel)
        return 1

    if args.setzen or args.setzen_aus:
        paare = [(a, b) for a, b in (args.setzen or [])]
        if args.setzen_aus:
            for zeile in Path(args.setzen_aus).read_text(encoding="utf-8-sig").splitlines():
                zeile = zeile.strip()
                if not zeile or zeile.startswith("#") or "=" not in zeile:
                    continue
                links, rechts = zeile.split("=", 1)
                paare.append((links.strip(), rechts.strip()))
        print("Namen eintragen:")
        n = setze_namen(wurzel, paare)
        print("%d Strecke(n) benannt." % n)
        return 0

    ordner = sorted(kurs_ordner(wurzel),
                    key=lambda d: -(lade_notiz(d) or {}).get("Laps", 0))

    if args.zeiten:
        return zeiten_zeigen(ordner)

    if not ordner:
        print("Keine aufgezeichneten Strecken unter %s" % wurzel)
        return 1

    print("%-24s %-26s %5s %9s  %s"
          % ("Ordner", "Name", "Runden", "Laenge", "Bild"))
    ohne_namen = gezeichnet = 0
    for d in ordner:
        notiz = lade_notiz(d)
        if notiz is None:
            print("%-24s %-26s %5s %9s  keine course.json" % (d.name, "", "", ""))
            continue
        name = (notiz.get("Name") or "").strip()
        if not name:
            ohne_namen += 1
        lang = notiz.get("LongestMetres", 0)
        bild = ""
        if args.karten:
            fahrten = runden(d)
            ziel = zeichne(d, notiz, fahrten)
            if ziel is not None:
                bild = ziel.name
                gezeichnet += 1
            else:
                bild = "keine Messpunkte"
        print("%-24s %-26s %5d %7.2f km  %s"
              % (d.name, name or "--", notiz.get("Laps", 0), lang / 1000.0, bild))

    print()
    print("%d Strecke(n), davon %d ohne Namen." % (len(ordner), ohne_namen))
    if args.karten:
        print("%d Bild(er) geschrieben -- course_map.svg im jeweiligen Ordner." % gezeichnet)
        print("Im Browser oeffnen, Strecke erkennen, dann:")
        print('  python scripts/name_courses.py --setzen %s "Name der Strecke"'
              % ordner[0].name)
    else:
        print("Mit --karten wird je Strecke der gefahrene Verlauf gezeichnet.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
