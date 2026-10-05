"""Hat der Bestenlisten-Dienst in den letzten Minuten gestreikt?

    python scripts/frischer_dienstausfall.py [minuten]

Rueckgabe 0 = ja, frisch. 1 = nein.

## Warum die Zeit dazugehoert

Der Tagesplan hielt am 2026-09-14 um 14:58 fuenfzehn Minuten inne, weil er in den
letzten 400 Protokollzeilen einen Server-Fehler von 11:07 fand -- vier Stunden alt.
Beim naechsten Blick haette er denselben Eintrag wieder gefunden: eine Schleife, die
sich selbst bis zum Abend blockiert.

Ein Ausfall ist ein Zustand von JETZT. Die Protokollzeilen tragen ihre Uhrzeit mit,
also wird sie gelesen.
"""

import re
import sys
from datetime import datetime, timedelta
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
MUSTER = re.compile(
    r"(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}).*"
    r"(server error|cannot reach the leaderboard)", re.I)


def frisch(minuten: int = 30) -> bool:
    grenze = datetime.now() - timedelta(minutes=minuten)
    for name in ("cross-country.out", "street_racing.out"):
        p = WORKSPACE / "data" / "runtime" / "overnight" / name
        if not p.exists():
            continue
        zeilen = p.read_text(encoding="utf-8", errors="replace").splitlines()[-600:]
        for zeile in zeilen:
            m = MUSTER.search(zeile)
            if not m:
                continue
            try:
                wann = datetime.strptime(m.group(1), "%Y-%m-%d %H:%M:%S")
            except ValueError:
                continue
            if wann >= grenze:
                print("frischer Ausfall um %s" % m.group(1))
                return True
    return False


if __name__ == "__main__":
    minuten = int(sys.argv[1]) if len(sys.argv) > 1 else 30
    raise SystemExit(0 if frisch(minuten) else 1)
