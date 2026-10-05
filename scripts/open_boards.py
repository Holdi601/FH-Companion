"""Wie viele Boards sind in einer Kategorie noch offen?

    python scripts/open_boards.py "Cross-Country"     -> Zahl auf stdout
    python scripts/open_boards.py --all               -> je Zeile: Kategorie<TAB>offen

WOFUER: die Wiederanlauf-Skripte hatten ihre Arbeitsreihenfolge fest verdrahtet
(erst Road Racing, dann Street Racing). Am 2026-09-01 waren beide fertig und
Cross-Country stand offen -- der Wecker haette also zweimal ueber ein fertiges Feld
gescannt und die einzige offene Kategorie nie angefasst.

WICHTIG, warum nicht einfach Streckenzahl x 7: fuer Cross-Country kennt der Katalog
nur 13 der 19 Strecken beim Namen; die sechs unsicheren stehen absichtlich nicht
drin und werden vom Sweep uebersprungen. Wer stumpf 19 x 7 rechnet, haelt eine
fertige Kategorie fuer zu 30 % offen und schickt jede Nacht einen Lauf los, der
nichts mehr findet.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
CLASSES = ["D", "C", "B", "A", "S1", "S2", "R"]

# Kategorie -> (Fortschrittsdatei, Streckenzahl falls der Katalog sie nicht kennt)
PROGRESS = {
    "Road Racing":   ("baseline20k_progress.json", 23),
    "Street Racing": ("street_racing_progress.json", 15),
    "Cross-Country": ("cross-country_progress.json", 19),
    "Dirt Racing":   ("dirt_racing_progress.json", 21),
    "Drag Racing":   ("drag_racing_progress.json", 3),
    "Touge":         ("touge_progress.json", 5),
}


def catalogue_indices(category: str) -> list[int] | None:
    """Die Streckennummern, die der Katalog BENANNT kennt -- oder None."""
    path = WORKSPACE / "config" / "fh6_board_catalogue.json"
    try:
        data = json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        return None
    entry = (data.get("tracks") or {}).get(category)
    if not isinstance(entry, dict):
        return None
    verified = entry.get("verified")
    if not isinstance(verified, list):
        return None
    idx = [r["route_index"] for r in verified
           if isinstance(r, dict) and (r.get("name") or "").strip()]
    return sorted(idx) or None


def open_boards(category: str) -> int:
    if category not in PROGRESS:
        raise KeyError(category)
    filename, fallback_count = PROGRESS[category]
    path = WORKSPACE / "data" / "runtime" / "overnight" / filename
    try:
        done = {(int(i), str(k))
                for i, k in json.loads(path.read_text(encoding="utf-8-sig"))["pairs"]}
    except (OSError, ValueError, KeyError, TypeError):
        done = set()

    indices = catalogue_indices(category)
    if indices is None:
        indices = list(range(fallback_count))
    return sum(1 for i in indices for k in CLASSES if (i, k) not in done)


def main(argv: list[str]) -> int:
    if len(argv) == 2 and argv[1] == "--all":
        for category in PROGRESS:
            print(f"{category}\t{open_boards(category)}")
        return 0
    if len(argv) == 2:
        print(open_boards(argv[1]))
        return 0
    print(__doc__.strip().splitlines()[0], file=sys.stderr)
    print('  python scripts/open_boards.py "Cross-Country" | --all', file=sys.stderr)
    return 2


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
