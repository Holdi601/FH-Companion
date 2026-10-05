"""Alle Bildschirmnamen auflisten, die der Zuordner NICHT auf ein Auto bringt.

Warum es das gibt: eine unerkannte Zeile faellt lautlos aus jeder Wertung. Am
2026-09-10 waren das 8,7 % aller Zeilen (556.921 von 6.366.532), ohne dass irgendeine
Kennzahl der Seite darauf zeigte. Diese Liste macht den Verlust sichtbar und nach
Kosten sortierbar -- die teuersten Namen zuerst, damit sich das Aufloesen lohnt.

Die Ausgabe ist eine TSV-Datei: Bildschirmname, Zeilen, auf wie vielen Boards, und
was der Zuordner beim Abwerfen des ersten Wortes gefunden haette. Die letzte Spalte
ist ein VORSCHLAG, kein Ergebnis -- sie hilft beim Zuordnen von Hand und wird
nirgends automatisch uebernommen.

    python scripts/list_unmatched_car_names.py
    python scripts/list_unmatched_car_names.py --min-rows 20 --out irgendwo.tsv
"""

from __future__ import annotations

import argparse
import sys
from collections import Counter, defaultdict
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
    parser.add_argument("--root", type=Path,
                        default=WORKSPACE / "data/memory_scans/full_sweep")
    parser.add_argument("--out", type=Path,
                        default=WORKSPACE / "data/analytics/unmatched_car_names.tsv")
    parser.add_argument("--min-rows", type=int, default=1,
                        help="Namen mit weniger Zeilen weglassen")
    parser.add_argument("--every", type=int, default=1,
                        help="nur jedes n-te Board lesen (schnelle Stichprobe)")
    args = parser.parse_args(argv)

    import pandas as pd
    import pyarrow.parquet as pqm
    import build_car_roster as rt

    cars = rt.load_roster(WORKSPACE / "data/car_catalogue/fh6cars.html")
    index = rt.build_index(cars)
    keys = list(index)
    years = rt.build_year_index(cars)

    entschieden: dict[str, str | None] = {}

    def zuordnen(name: str) -> str | None:
        if name not in entschieden:
            sauber = rt.clean_ocr_name(name)
            auto, _ = (rt.match_car(sauber, index, keys, years)
                       if sauber else (None, 0.0))
            entschieden[name] = f"{auto['name']} '{str(auto['year'])[-2:]}" if auto else None
        return entschieden[name]

    def vorschlag(name: str) -> str:
        """Was ohne die ersten Woerter herauskaeme -- ein Hinweis, keine Wahrheit.

        Wortweise abzuwerfen erzeugt Unsinn, sobald der Rest zufaellig woanders passt:
        "Nissan GT-R 12" kam so als "Mercedes-AMG GT R '17" heraus, weil von "GT R"
        genug uebrig blieb. Ein Vorschlag zaehlt deshalb nur, wenn ein langes Wort des
        Originals in ihm wiederkehrt -- meist die Marke. Lieber eine leere Spalte als
        eine, die beim Zuordnen in die Irre fuehrt.
        """
        teile = rt.clean_ocr_name(name).split()
        lang = [w.lower().strip("-.,") for w in teile if len(w) >= 4]
        for weg in (1, 2):
            if len(teile) - weg < 2:
                break
            treffer = zuordnen(" ".join(teile[weg:]))
            if not treffer:
                continue
            klein = treffer.lower()
            if not lang or any(w and w in klein for w in lang):
                return treffer
        return ""

    boards = [p for p in sorted(args.root.glob("*/leaderboard_entries.parquet"))
              if "car_name" in pqm.ParquetFile(p).schema.names]
    boards = boards[::max(1, args.every)]

    zeilen: Counter[str] = Counter()
    boards_je_name: defaultdict[str, set] = defaultdict(set)
    gesamt = 0
    for pq in boards:
        df = pd.read_parquet(pq, columns=["car_name"])
        for name, anzahl in df["car_name"].astype(str).value_counts().items():
            gesamt += anzahl
            if zuordnen(name) is None:
                zeilen[name] += anzahl
                boards_je_name[name].add(pq.parent.name)

    weg = sum(zeilen.values())
    args.out.parent.mkdir(parents=True, exist_ok=True)
    with args.out.open("w", encoding="utf-8", newline="") as handle:
        handle.write("bildschirmname\tzeilen\tboards\tvorschlag\n")
        for name, anzahl in zeilen.most_common():
            if anzahl < args.min_rows:
                continue
            handle.write(f"{name}\t{anzahl}\t{len(boards_je_name[name])}\t{vorschlag(name)}\n")

    print(f"{len(boards)} Boards, {gesamt:,} Zeilen mit Namen")
    print(f"unerkannt: {weg:,} Zeilen ({weg / max(1, gesamt) * 100:.1f} %) "
          f"in {len(zeilen):,} verschiedenen Schreibweisen")
    print(f"geschrieben: {args.out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
