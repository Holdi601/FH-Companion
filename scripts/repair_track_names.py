"""Streckennamen in allen Scans gegen den Katalog geradeziehen.

Der Streckenname eines Boards kommt aus einer Bildschirmlesung. Scheitert die, landen
Zeilen unter einer Erfindung: am 2026-08-26 lag ein Board mit 2.631 Zeilen unter der
Strecke `I @ 2,666,890` -- die OCR hatte eine Punktestandszeile gelesen. Und weil eine
Strecke dann zweimal im Bestand steht, zerfaellt sie auf der Auswertungsseite in zwei.

Die verlaessliche Zweitquelle ist die Karussellposition: der Navigator laeuft sie ab und
BESTAETIGT sie, bevor er das Board oeffnet, und der Katalog kennt den Namen dazu. Genau
das macht `ocr_board_sweep.catalogue_route()` seit demselben Tag beim Scannen -- dieses
Skript zieht die Laeufe nach, die vorher entstanden sind.

Angefasst wird nur, was die Namenskorrektur NICHT aufloesen kann. Ein Name, der exakt
oder als Praefix zu einer bekannten Strecke passt, bleibt stehen: der Bildschirm ist die
erste Quelle, der Katalog nur die Rueckfalloption.

    python scripts/repair_track_names.py --dry-run
    python scripts/repair_track_names.py
"""

from __future__ import annotations

import argparse
import json
import shutil
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import ocr_board_sweep as sweep          # noqa: E402
import ocr_corrections as oc             # noqa: E402

ROOT = Path("data/memory_scans/full_sweep")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dry-run", action="store_true", help="nur zeigen, nichts schreiben")
    args = parser.parse_args(argv)

    changed = skipped = 0
    for folder in sorted(p for p in ROOT.iterdir() if p.is_dir()):
        path = folder / "state.json"
        if not path.is_file():
            continue
        try:
            state = json.loads(path.read_text(encoding="utf-8-sig"))
        except Exception:
            continue
        track = state.get("track")
        track = track.strip() if isinstance(track, str) else ""
        index = state.get("route_index")
        if not isinstance(index, int):
            continue

        good, how = oc.correct_route_name(track) if track else ("", "unreadable")
        if how in ("exact", "prefix", "similar") and good:
            if good != track:
                print(f"  {folder.name}: {track!r} -> {good!r} ({how})")
                if not args.dry_run:
                    _write(path, state, good, how)
                changed += 1
            continue

        expected = sweep.catalogue_route(index)
        if not expected:
            print(f"  {folder.name}: {track!r} unlesbar und kein Katalogeintrag "
                  f"zu Index {index} -- BLEIBT")
            skipped += 1
            continue
        print(f"  {folder.name}: {track!r} unlesbar -> Katalog Index {index}: {expected!r} "
              f"({state.get('rows_collected') or 0} Zeilen)")
        if not args.dry_run:
            _write(path, state, expected, f"katalog:index{index}")
        changed += 1

    print(f"\n{changed} korrigiert, {skipped} ohne Zweitquelle"
          + ("  (Probelauf)" if args.dry_run else ""))
    return 0


def _write(path: Path, state: dict, name: str, how: str) -> None:
    backup = path.with_suffix(".json.bak")
    if not backup.exists():
        shutil.copyfile(path, backup)
    state["track_corrected_from"] = state.get("track")
    state["track_correction"] = how
    state["track"] = name
    path.write_text(json.dumps(state, indent=2, ensure_ascii=False), encoding="utf-8")


if __name__ == "__main__":
    raise SystemExit(main())
