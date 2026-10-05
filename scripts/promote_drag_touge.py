"""Drag Racing und Touge freischalten -- mit am Spiel geprueften Namen.

    python scripts/promote_drag_touge.py            # Probelauf
    python scripts/promote_drag_touge.py --apply

## Was am 2026-09-09 am Spiel nachgelesen wurde

Die unverifizierte Aufnahme vom 2026-08-28 hatte drei fehlerhafte Namen. Alle drei
sind am Bildschirm geprueft, nicht geraten:

  'lrokawa Space Centre Drag'  ->  'Irokawa Space Centre Drag Strip'
  'Ito Airfield Drag S Strip'  ->  'Ito Airfield Drag Strip'
  'Mt karuna'                  ->  'Mt Haruna'

Beim Irokawa war der Titel am Kartenrand abgeschnitten ("awa Space Centre Drag
Strip"), UND der Klassenschirm schnitt noch staerker ab. Gerettet hat es der
Beschreibungstext rechts auf demselben Schirm: "This drag strip runs alongside the
Irokawa Space Centre." -- eine unabhaengige Quelle im selben Bild. Der Trick ist
uebertragbar: steht der Name nicht im Titel, steht er oft in den Details.

## Wahrzeichen, gegen ALLE Kategorien auf Kollisionen geprueft

  Drag Racing:  "Drag"        -- in allen 3 Namen, sonst nirgends
  Touge:        Alternative aus vier Streckennamen. Es gibt KEIN gemeinsames Wort.
                ACHTUNG: "Norikura" allein waere falsch -- Street Racing hat eine
                "Norikura Descent". Nur "Norikura Skyline" ist eindeutig.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
KATALOG = WORKSPACE / "config" / "fh6_board_catalogue.json"

GEPRUEFT = {
    "Drag Racing": {
        0: "Horizon Festival Drag Strip",
        1: "Irokawa Space Centre Drag Strip",
        2: "Ito Airfield Drag Strip",
    },
    "Touge": {
        0: "Hakone Nanamagari",
        1: "Norikura Skyline",
        2: "Arashiyama Takao",
        3: "Bandai Azuma",
        4: "Mt Haruna",
    },
}


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--apply", action="store_true")
    args = ap.parse_args(argv[1:])

    cat = json.loads(KATALOG.read_text(encoding="utf-8-sig"))
    unver = cat.get("route_enumerations_unverified") or {}
    fertig = 0

    for kategorie, namen in GEPRUEFT.items():
        if kategorie in (cat.get("tracks") or {}):
            print(f"{kategorie}: steht bereits unter tracks -- uebersprungen")
            continue
        quelle = {r["route_index"]: r for r in (unver.get(kategorie) or [])}
        if not quelle:
            print(f"{kategorie}: FEHLER, keine unverifizierte Liste")
            return 2
        eintraege = [{"route_index": i, "name": n,
                      "route_length_km": (quelle.get(i) or {}).get("route_length_km")}
                     for i, n in sorted(namen.items())]
        print(f"{kategorie}: {len(eintraege)} Strecken")
        for e in eintraege:
            alt = (quelle.get(e["route_index"]) or {}).get("name", "")
            marke = "" if alt == e["name"] else f"   (war {alt!r})"
            print(f"   idx{e['route_index']:02d}  {e['name']:34s} {e['route_length_km']} km{marke}")
        if args.apply:
            cat.setdefault("tracks", {})[kategorie] = {
                "_comment": [
                    "Freigeschaltet 2026-09-09. Namen einzeln am Spiel nachgelesen,",
                    "nicht aus der Aufnahme uebernommen -- drei davon waren falsch.",
                    "Wahrzeichen siehe forza_navigator.ps1 CategoryLandmark.",
                ],
                "verified": eintraege,
            }
            cat.setdefault("route_carousel_by_category", {})[kategorie] = [
                {**e, "step": None, "scanned": False} for e in eintraege
            ]
        fertig += 1
        print()

    if not args.apply:
        print("PROBELAUF -- nichts geaendert. Mit --apply schreiben.")
        return 0
    KATALOG.write_text(json.dumps(cat, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"Katalog geschrieben: {fertig} Kategorie(n) freigeschaltet")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
