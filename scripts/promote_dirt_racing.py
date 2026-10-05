"""Dirt Racing vom unverifizierten Anhang in den Katalog heben -- und damit den Riegel oeffnen.

    python scripts/promote_dirt_racing.py                 # Probelauf, aendert nichts
    python scripts/promote_dirt_racing.py --apply
    python scripts/promote_dirt_racing.py --apply --idx00 "Chiheisen Scramble"

## Warum es das gibt

`ocr_board_sweep.refuse_unverified_category()` weist jede Kategorie ab, die unter
`tracks` fehlt. Fuer Dirt Racing liegt die Streckenliste seit 2026-08-28 als
`route_enumerations_unverified` bereit: 21 Strecken mit Namen UND Laengen, aufgenommen
mit `-EnumerateRoutes`. Sie ist deutlich sauberer als die von Cross-Country, weil die
Namen dort nicht am Kartenrand abgeschnitten werden.

## Was geprueft wurde, bevor das hier geschrieben wurde

- **21 Strecken**, genau so viele wie die Kachel im Kategoriegitter verspricht.
- Klares Muster: 10x "... Scramble" (2,1-5,0 km), 10x "... Trail" (5,1-8,0 km) und
  "The Gauntlet" (30,1 km) als Marathonstrecke, analog zu The Colossus/The Goliath.
- **Genau EIN Name ist unsauber**: Index 0 steht als "Chiheisen Scranfble" da -- der
  klassische OCR-Fehler m -> nf. Er wird hier NICHT blind korrigiert: ein falscher
  Name faellt beim Scannen niemandem auf, und genau daran hat das Projekt am
  2026-09-03 fuenf verfaelschte Boards verloren. Ohne `--idx00` bleibt die Position
  LEER, und der Sweep ueberspringt sie -- dieselbe Regel wie bei den sechs unsicheren
  Cross-Country-Strecken.
- **Wahrzeichen `Scramble|Trail`**: gegen alle Namen aller Kategorien geprueft, kommt
  ausserhalb von Dirt Racing NICHT vor. Es deckt 19 der 21 Namen ab, und die
  Streckenliste zeigt immer mehrere Eintraege gleichzeitig.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
KATALOG = WORKSPACE / "config" / "fh6_board_catalogue.json"
KATEGORIE = "Dirt Racing"


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--apply", action="store_true", help="wirklich schreiben")
    ap.add_argument("--idx00", default="",
                    help="geprueter Name fuer Index 0; ohne diesen bleibt er leer")
    args = ap.parse_args(argv[1:])

    cat = json.loads(KATALOG.read_text(encoding="utf-8-sig"))
    quelle = (cat.get("route_enumerations_unverified") or {}).get(KATEGORIE)
    if not quelle:
        print(f"FEHLER: keine unverifizierte Liste fuer {KATEGORIE!r}")
        return 2
    if KATEGORIE in (cat.get("tracks") or {}):
        print(f"{KATEGORIE!r} steht bereits unter tracks -- nichts zu tun")
        return 0

    eintraege, leer = [], []
    for r in quelle:
        idx, name = r["route_index"], (r.get("name") or "").strip()
        if idx == 0:
            name = args.idx00.strip()
        if not name or "Scranfble" in name:
            leer.append(idx)
            continue
        eintraege.append({"route_index": idx, "name": name,
                          "route_length_km": r.get("route_length_km")})

    print(f"{KATEGORIE}: {len(eintraege)} Strecken werden freigeschaltet"
          + (f", {len(leer)} bleiben leer (uebersprungen): {leer}" if leer else ""))
    for e in eintraege[:3]:
        print(f"   idx{e['route_index']:02d}  {e['name']}  {e['route_length_km']} km")
    print("   ...")

    if not args.apply:
        print("\nPROBELAUF -- nichts geaendert. Mit --apply schreiben.")
        return 0

    cat.setdefault("tracks", {})[KATEGORIE] = {
        "_comment": [
            "Freigeschaltet 2026-09-05 aus route_enumerations_unverified (Aufnahme",
            "vom 2026-08-28 mit -EnumerateRoutes). 21 Strecken, Muster 10x Scramble /",
            "10x Trail / The Gauntlet 30,1 km.",
            "Wahrzeichen in forza_navigator.ps1: 'Scramble|Trail' -- gegen alle",
            "Kategorien auf Kollisionen geprueft, kommt sonst nirgends vor.",
            "Leere Positionen werden vom Sweep uebersprungen statt falsch beschriftet.",
        ],
        "verified": eintraege,
    }
    cat.setdefault("route_carousel_by_category", {})[KATEGORIE] = [
        {**e, "step": None, "scanned": False} for e in eintraege
    ]
    KATALOG.write_text(json.dumps(cat, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"\nKatalog geschrieben: {KATALOG}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
