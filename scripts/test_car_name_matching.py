"""Den Autonamen-Zuordner gegen die belegten Kurznamen des Wikis pruefen.

WARUM ES DAS GIBT: bis zum 2026-09-11 gab es keine einzige Pruefung, die den Zuordner
an etwas AUSSERHALB seiner eigenen Annahmen gemessen hat. Die vorhandenen Testfaelle
waren aus derselben Vorstellung gebaut wie der Code -- wohlgeformte Namen -- und
blieben deshalb gruen, waehrend auf der Seite Runden unter falschen Autos standen:
"Corvette 15" unter einer 1953er, "Mazda RX-8" unter einem Formula-Drift-Rennwagen,
"Ford Mustang S5" ebenso. Gefunden hat das jedes Mal der Nutzer, nie ein Test.

Das Forza-Wiki nennt je Auto die Kurzform, die das Spiel anzeigt ("abbreviated as
\"Acura Integra\""). Das ist eine unabhaengige Quelle: 600 Kurznamen, geholt von
`scripts/fetch_wiki_abbreviations.py`. Jeder davon MUSS auf sein Auto fallen.

    python scripts/test_car_name_matching.py
    python scripts/test_car_name_matching.py --show 40   # mehr Abweichungen zeigen
"""

from __future__ import annotations

import argparse
import json
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


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--abbreviations", type=Path,
                        default=WORKSPACE / "config/fh6_wiki_abbreviations.json")
    parser.add_argument("--show", type=int, default=25)
    args = parser.parse_args(argv)

    import build_car_roster as rt

    daten = json.loads(args.abbreviations.read_text(encoding="utf-8"))
    kurznamen = daten["abbreviations"]
    mehrdeutig = set(daten.get("ambiguous", []))

    cars = rt.load_roster(WORKSPACE / "data/car_catalogue/fh6cars.html")
    index = rt.build_index(cars)
    keys = list(index)
    years = rt.build_year_index(cars)
    bekannt = {(rt.normalise(c["name"]), c["year"]) for c in cars}

    richtig = falsch = verworfen = uebersprungen = 0
    abweichungen: list[tuple[str, str, str]] = []

    for kurz, ziele in sorted(kurznamen.items()):
        if kurz in mehrdeutig:
            uebersprungen += 1
            continue
        ziel = ziele[0]
        # Autos, die unser Verzeichnis gar nicht kennt, sagen nichts ueber den
        # Zuordner -- die Liste von forza.net und das Wiki decken sich nicht ganz.
        if (rt.normalise(ziel["name"]), ziel["year"]) not in bekannt:
            uebersprungen += 1
            continue
        sauber = rt.clean_ocr_name(kurz)
        auto, _ = rt.match_car(sauber, index, keys, years) if sauber else (None, 0.0)
        if auto is None:
            verworfen += 1
            abweichungen.append((kurz, ziel["name"], "verworfen"))
        elif (rt.normalise(auto["name"]), auto["year"]) == (rt.normalise(ziel["name"]),
                                                            ziel["year"]):
            richtig += 1
        else:
            falsch += 1
            abweichungen.append((kurz, f"{ziel['name']} ({ziel['year']})",
                                 f"{auto['name']} ({auto['year']})"))

    gesamt = richtig + falsch + verworfen
    print(f"{gesamt} belegte Kurznamen geprueft "
          f"({uebersprungen} uebersprungen: mehrdeutig oder nicht im Verzeichnis)\n")
    print(f"  richtig zugeordnet: {richtig:5}  ({richtig / max(1, gesamt) * 100:.1f} %)")
    print(f"  FALSCHES Auto:      {falsch:5}")
    print(f"  verworfen:          {verworfen:5}")

    falsche = [a for a in abweichungen if a[2] != "verworfen"]
    if falsche:
        print(f"\nFALSCH ZUGEORDNET -- das sind die teuren Fehler:")
        for kurz, soll, ist in falsche[:args.show]:
            print(f"  {kurz!r:26} soll {soll[:38]:38} ist {ist}")
    fehlend = [a for a in abweichungen if a[2] == "verworfen"]
    if fehlend:
        print(f"\nVERWORFEN -- Zeilen gehen verloren, aber nichts wird verfaelscht:")
        for kurz, soll, _ in fehlend[:args.show]:
            print(f"  {kurz!r:26} waere {soll}")

    # Ein falsch zugeordnetes Auto ist der Fehler, der die Wertung verdirbt: dem einen
    # fehlen Runden, dem anderen werden fremde gutgeschrieben. Verworfene kosten nur.
    return 1 if falsch else 0


if __name__ == "__main__":
    raise SystemExit(main())
