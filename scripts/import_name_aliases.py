"""Zugeordnete Bildschirmnamen aus einer Tabelle in die Alias-Datei uebernehmen.

Der Zuordner verwirft einen Bildschirmnamen, den er nicht eindeutig auf ein Auto
bringt -- richtig, denn Raten ist teurer als Weglassen. Am 2026-09-10 waren das 8,7 %
aller Zeilen. Die Tabelle `unmatched_car_names_matched.tsv` loest diese Namen auf,
und dieses Werkzeug traegt sie nach `config/fh6_screen_name_aliases.tsv` ein.

WAS UEBERNOMMEN WIRD, UND WAS NICHT:

* nur `match_status = matched`,
* nur wenn die ALTERNATIVE echt schlechter bewertet ist. Steht `alt_1` gleichauf,
  hat das erzeugende Werkzeug zwischen gleich guten Autos gewuerfelt -- bei "Lambo
  Huracan" zwischen LP 610-4, EVO und STO, alle drei mit 100 Punkten. Solche Zeilen
  bleiben ungeloest, und zwar absichtlich: ein falscher Name ist teurer als ein
  fehlender, weil er ZWEI Autos verfaelscht -- dem einen fehlen Runden, dem anderen
  werden fremde gutgeschrieben.
* und nur, wenn das genannte Auto in `data/car_catalogue/fh6cars.html` wirklich
  existiert, mit Jahrgang. Ein Name, den unser Verzeichnis nicht kennt, waere sonst
  ein stiller Totalausfall.

    python scripts/import_name_aliases.py
    python scripts/import_name_aliases.py --dry-run       # nur berichten
    python scripts/import_name_aliases.py --ties ties.tsv # Gleichstaende ausgeben
"""

from __future__ import annotations

import argparse
import csv
import re
import sys
from pathlib import Path

for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

SCRIPTS = Path(__file__).resolve().parent
sys.path.insert(0, str(SCRIPTS))
WORKSPACE = SCRIPTS.parent

JAHR_NAME = re.compile(r"\s*(\d{4})\s+(.*\S)\s*$")
ALT_WERT = re.compile(r"\(([\d.]+)\)\s*$")


def _zahl(text: str) -> float:
    try:
        return float(text or 0)
    except ValueError:
        return 0.0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--table", type=Path,
                        default=WORKSPACE / "data/analytics/unmatched_car_names_matched.tsv")
    parser.add_argument("--out", type=Path,
                        default=WORKSPACE / "config/fh6_screen_name_aliases.tsv")
    parser.add_argument("--ties", type=Path, default=None,
                        help="Gleichstaende zum Nachentscheiden hierhin schreiben")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args(argv)

    import build_car_roster as rt

    cars = rt.load_roster(WORKSPACE / "data/car_catalogue/fh6cars.html")
    nach_jahr: dict[int, list[tuple[str, dict]]] = {}
    for car in cars:
        nach_jahr.setdefault(car["year"], []).append((rt.normalise(car["name"]), car))

    def im_verzeichnis(text: str) -> dict | None:
        treffer = JAHR_NAME.match(text or "")
        if not treffer:
            return None
        jahr, name = int(treffer.group(1)), rt.normalise(treffer.group(2))
        for norm, car in nach_jahr.get(jahr, []):
            if norm == name:
                return car
        return None

    with args.table.open(encoding="utf-8-sig", newline="") as handle:
        zeilen = list(csv.DictReader(handle, delimiter="\t"))

    uebernommen: dict[str, tuple[str, int, str]] = {}
    gleichstand: list[dict] = []
    unbekannt: list[dict] = []
    verworfen = 0

    for eintrag in zeilen:
        if eintrag.get("match_status") != "matched" or not eintrag.get("fh6_car_name", "").strip():
            verworfen += 1
            continue
        punkte = _zahl(eintrag.get("match_score", ""))
        alt = ALT_WERT.search(eintrag.get("alt_1") or "")
        if alt and float(alt.group(1)) >= punkte - 0.01:
            gleichstand.append(eintrag)
            continue
        car = im_verzeichnis(eintrag["fh6_car_name"])
        if car is None:
            unbekannt.append(eintrag)
            continue
        schluessel = rt.normalise(eintrag["bildschirmname"])
        if schluessel:
            uebernommen[schluessel] = (car["name"], car["year"],
                                       eintrag.get("match_source", ""))

    def zeilenzahl(liste) -> int:
        gesamt = 0
        for e in liste:
            try:
                gesamt += int(e.get("zeilen") or 0)
            except ValueError:
                pass
        return gesamt

    print(f"Tabelle: {len(zeilen):,} Zeilen")
    print(f"  uebernommen:            {len(uebernommen):,} Schreibweisen")
    print(f"  Gleichstand, offen:     {len(gleichstand):,} "
          f"({zeilenzahl(gleichstand):,} Board-Zeilen)")
    print(f"  Auto nicht im Verzeichnis: {len(unbekannt):,}")
    print(f"  ohne Zuordnung:         {verworfen:,}")

    if args.ties and gleichstand:
        args.ties.parent.mkdir(parents=True, exist_ok=True)
        with args.ties.open("w", encoding="utf-8", newline="") as handle:
            schreiber = csv.writer(handle, delimiter="\t")
            schreiber.writerow(["bildschirmname", "zeilen", "gewaehlt",
                                "alternative_1", "alternative_2"])
            for e in sorted(gleichstand, key=lambda x: -_zahl(x.get("zeilen", "0"))):
                schreiber.writerow([e["bildschirmname"], e.get("zeilen", ""),
                                    e.get("fh6_car_name", ""), e.get("alt_1", ""),
                                    e.get("alt_2", "")])
        print(f"  Gleichstaende geschrieben: {args.ties}")

    if args.dry_run:
        print("--dry-run: nichts geschrieben")
        return 0

    args.out.parent.mkdir(parents=True, exist_ok=True)
    with args.out.open("w", encoding="utf-8", newline="") as handle:
        handle.write("# Bildschirmname (normalisiert) -> Auto aus fh6cars.html\n")
        handle.write("# erzeugt von scripts/import_name_aliases.py\n")
        handle.write("schluessel\tname\tjahr\tquelle\n")
        for schluessel in sorted(uebernommen):
            name, jahr, quelle = uebernommen[schluessel]
            handle.write(f"{schluessel}\t{name}\t{jahr}\t{quelle}\n")
    print(f"geschrieben: {args.out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
