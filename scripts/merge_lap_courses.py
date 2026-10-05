"""Zersplitterte Streckenordner im Rundenarchiv wieder zusammenfuehren.

    python scripts/merge_lap_courses.py                Probelauf: was waere zu tun
    python scripts/merge_lap_courses.py --apply        tun, mit Sicherung
    python scripts/merge_lap_courses.py --toleranz 120 andere Schwelle

## Was schiefging

`LapArchive.CourseKey` rundete den Startpunkt einer Runde auf 25 m und machte daraus
den Ordnernamen. Eine Rundung hat GRENZEN, und zwei Ueberfahrten derselben
Start-Ziel-Linie koennen auf verschiedene Seiten einer solchen Grenze fallen. Dann
bekommt dieselbe Strecke zwei Ordner.

Am 2026-09-13 im eigenen Bestand gemessen -- 44 Ordner, 140 Runden, davon 14 Paare
naeher als 250 m beieinander:

    course_1300_275    <-> course_1325_275      2,6 m
    course_2775_5000   <-> course_2800_5000     5,9 m
    course_-1675_-4425 <-> course_-1675_-4450   8,8 m

Fuer eine Karte ist das der unangenehmste Fall: die Daten sind vollstaendig da, aber
verteilt, und nichts weist darauf hin. Wer den Ordner einer Strecke oeffnet, sieht
zwei von fuenf Runden und haelt das fuer alles.

Die App legt seit dem 2026-09-13 keine neuen Splitter mehr an -- sie SUCHT den
vorhandenen Ordner, statt einen Namen auszurechnen. Dieses Skript raeumt auf, was
vorher entstanden ist.

## Die Schwelle

120 m, dieselbe wie `RecordedLap.StartTolerance` im Delta. Absichtlich derselbe
Wert: was das Delta als "dieselbe Strecke" behandelt, soll auch im selben Ordner
liegen.

## Was NICHT passiert

Nichts wird geloescht. Vor jeder Aenderung geht eine vollstaendige Kopie nach
`laps_vor_zusammenfuehrung/`. Zusammengefuehrt wird durch VERSCHIEBEN; bleibt ein
Zielpfad belegt, bekommt die Datei eine Ziffer angehaengt, statt die vorhandene zu
ueberschreiben.
"""

from __future__ import annotations

import argparse
import json
import math
import shutil
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "server"))
from local_settings import app_data  # noqa: E402  (FHCompanion, bis 2026-09-26 ForzaGripHaptics)
sys.path.insert(0, str(Path(__file__).resolve().parent))
from lap_folders import kennung, kurs_ordner, kurs_pfad  # noqa: E402,F401
WURZEL = app_data()
ARCHIV = WURZEL / "laps"
SICHERUNG = WURZEL / "laps_vor_zusammenfuehrung"


def notiz(ordner: Path) -> dict | None:
    p = ordner / "course.json"
    if not p.exists():
        return None
    try:
        return json.loads(p.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        return None


def runden(ordner: Path) -> list[Path]:
    return [p for p in ordner.rglob("*.json") if p.name != "course.json"]


def gruppen(ordner: list[Path], toleranz: float) -> list[list[Path]]:
    """Ordner zu Gruppen verbinden, die dieselbe Strecke sind.

    Transitiv, und das ist Absicht: liegen A und B 80 m auseinander und B und C
    ebenfalls, gehoeren alle drei zusammen, auch wenn A und C 160 m trennen. Ein
    Streckenbeginn ist eine Flaeche, keine Nadel -- eine Startaufstellung ist breit,
    und drei Ueberfahrten spannen sie auf.
    """
    orte = {}
    for o in ordner:
        n = notiz(o)
        if n and (n.get("StartX") or n.get("StartZ")):
            orte[o] = (float(n.get("StartX", 0)), float(n.get("StartZ", 0)))

    offen = list(orte)
    ergebnis = []
    while offen:
        kern = [offen.pop(0)]
        gewachsen = True
        while gewachsen:
            gewachsen = False
            for kandidat in list(offen):
                if any(math.dist(orte[kandidat], orte[m]) <= toleranz for m in kern):
                    kern.append(kandidat)
                    offen.remove(kandidat)
                    gewachsen = True
        if len(kern) > 1:
            ergebnis.append(kern)
    return ergebnis


def main(argv: list | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--toleranz", type=float, default=120.0)
    parser.add_argument("--archiv", type=Path, default=ARCHIV)
    args = parser.parse_args(argv)

    archiv: Path = args.archiv
    if not archiv.exists():
        print("Kein Archiv unter " + archiv.as_posix())
        return 1

    ordner = sorted(kurs_ordner(archiv))
    print("Ordner: %d, Runden: %d" % (
        len(ordner), sum(len(runden(o)) for o in ordner)))

    zusammen = gruppen(ordner, args.toleranz)
    if not zusammen:
        print("Nichts zusammenzufuehren -- jede Strecke hat genau einen Ordner.")
        return 0

    print("\n%d Gruppen, die dieselbe Strecke sind:" % len(zusammen))
    plan = []
    for kern in zusammen:
        # Der Ordner mit den MEISTEN Runden bleibt: er ist der eingebuergerte, und
        # sein Name steht schon in den Runden, die darin liegen.
        kern = sorted(kern, key=lambda o: (-len(runden(o)), o.name))
        ziel, quellen = kern[0], kern[1:]
        n_ziel = len(runden(ziel))
        print("\n  bleibt: %-22s (%d Runden)" % (ziel.name, n_ziel))
        for q in quellen:
            n = notiz(q) or {}
            z = notiz(ziel) or {}
            d = math.dist((float(n.get("StartX", 0)), float(n.get("StartZ", 0))),
                          (float(z.get("StartX", 0)), float(z.get("StartZ", 0))))
            print("    <- %-22s (%d Runden, %.1f m entfernt)" % (
                q.name, len(runden(q)), d))
            plan.append((q, ziel))

    if not args.apply:
        print("\nProbelauf -- %d Ordner waeren zusammenzufuehren. Mit --apply." % len(plan))
        return 0

    if SICHERUNG.exists():
        shutil.rmtree(SICHERUNG)
    print("\nSicherung nach " + SICHERUNG.as_posix())
    shutil.copytree(archiv, SICHERUNG)

    verschoben = 0
    for quelle, ziel in plan:
        for datei in runden(quelle):
            rel = datei.relative_to(quelle)
            neu = ziel / rel
            neu.parent.mkdir(parents=True, exist_ok=True)
            # Kein Ueberschreiben: gleiche Sekunde, gleiches Auto, gleicher Tune --
            # unwahrscheinlich, aber der Preis waere eine verlorene Runde.
            if neu.exists():
                stamm, endung = neu.stem, neu.suffix
                i = 2
                while neu.exists():
                    neu = neu.with_name("%s_%d%s" % (stamm, i, endung))
                    i += 1
            # Das Feld "Course" IN der Datei mitziehen, sonst widerspricht die Runde
            # dem Ordner, in dem sie liegt.
            try:
                d = json.loads(datei.read_text(encoding="utf-8-sig"))
                # Die KENNUNG, nicht der Ordnername "Name (course_...)".
                d["Course"] = kennung(ziel.name) or ziel.name
                neu.write_text(json.dumps(d, ensure_ascii=False), encoding="utf-8")
                datei.unlink()
            except (OSError, ValueError):
                shutil.move(str(datei), str(neu))
            verschoben += 1

        # Die Zaehler der Notiz zusammenrechnen, dann den leeren Ordner entfernen.
        nq, nz = notiz(quelle) or {}, notiz(ziel) or {}
        nz["Laps"] = int(nz.get("Laps", 0)) + int(nq.get("Laps", 0))
        kurz = [v for v in (nz.get("ShortestMetres"), nq.get("ShortestMetres")) if v]
        lang = [v for v in (nz.get("LongestMetres"), nq.get("LongestMetres")) if v]
        if kurz:
            nz["ShortestMetres"] = min(kurz)
        if lang:
            nz["LongestMetres"] = max(lang)
        nz.setdefault("MergedFrom", []).append(kennung(quelle.name) or quelle.name)
        (ziel / "course.json").write_text(
            json.dumps(nz, indent=1, ensure_ascii=False), encoding="utf-8")

        # NUR ENTFERNEN, WENN WIRKLICH NICHTS MEHR DRIN IST.
        #
        # Die App laeuft womoeglich waehrenddessen und legt Runden ab. Der Plan
        # oben ist eine Momentaufnahme; eine Runde, die zwischen Plan und Loeschen
        # fertig wird, laege in einem Ordner, den dieses Skript gleich entfernt --
        # und waere weg. Ein zweiter Blick kostet nichts und schliesst das aus.
        nachzuegler = runden(quelle)
        if nachzuegler:
            print("  %s: %d Runde(n) kamen waehrenddessen dazu -- Ordner bleibt "
                  "stehen, bitte erneut laufen lassen." % (
                      quelle.name, len(nachzuegler)))
            continue
        shutil.rmtree(quelle)

    ordner = sorted(kurs_ordner(archiv))
    print("\n%d Runden verschoben. Jetzt: %d Ordner, %d Runden." % (
        verschoben, len(ordner), sum(len(runden(o)) for o in ordner)))
    rest = gruppen(ordner, args.toleranz)
    print("Verbleibende Splitter: %d" % len(rest))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
