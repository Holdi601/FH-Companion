"""Das Wahrzeichen fuer Dirt Racing in forza_navigator.ps1 eintragen.

    python scripts/add_dirt_landmark.py            # Probelauf
    python scripts/add_dirt_landmark.py --apply

NICHT waehrend eines laufenden Sweeps ausfuehren: forza_navigator.ps1 wird bei JEDEM
Board frisch in den Gast kopiert, eine Aenderung traefe sofort das naechste Board.

Das Wahrzeichen beantwortet die Frage "ist das wirklich die geoeffnete Kategorie?".
Fuer Dirt Racing ist 'Scramble|Trail' geeignet, und das ist gemessen, nicht geraten:
beide Woerter kommen in KEINER anderen Kategorie vor (geprueft gegen alle Namen unter
tracks, route_carousel_by_category und route_enumerations_unverified), und zusammen
decken sie 19 der 21 Namen ab. Die einzige Ausnahme ist "The Gauntlet" -- die
Streckenliste zeigt aber immer mehrere Eintraege gleichzeitig, ein Treffer ist also
praktisch sicher.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

ZIEL = Path(__file__).resolve().parent / "forza_navigator.ps1"
ANKER = r'    "Cross-Country" = "Cross[\s-]?Country"' + '\n'
NEU = (
    '    # Dirt Racing braucht ebenfalls keinen einzelnen Streckennamen: 10 seiner 21\n'
    '    # Strecken heissen "... Scramble", 10 "... Trail", und beide Woerter kommen in\n'
    '    # keiner anderen Kategorie vor (gegen alle Katalognamen geprueft). Nur "The\n'
    '    # Gauntlet" faellt heraus, was nichts macht: die Streckenliste zeigt immer\n'
    '    # mehrere Eintraege auf einmal.\n'
    '    "Dirt Racing"   = "Scramble|Trail"\n'
)


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--apply", action="store_true")
    args = ap.parse_args(argv[1:])

    text = ZIEL.read_text(encoding="utf-8")
    if '"Dirt Racing"' in text.split("$script:CategoryLandmark")[1].split("}")[0]:
        print("Wahrzeichen fuer Dirt Racing steht bereits drin -- nichts zu tun")
        return 0
    if ANKER not in text:
        print("FEHLER: Ankerzeile (Cross-Country) nicht gefunden -- nichts geaendert")
        return 2

    if not args.apply:
        print("PROBELAUF. Es wuerde nach der Cross-Country-Zeile eingefuegt:")
        print(NEU.rstrip())
        return 0

    ZIEL.write_text(text.replace(ANKER, ANKER + NEU, 1), encoding="utf-8")
    print(f"Wahrzeichen eingetragen: {ZIEL}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
