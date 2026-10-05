"""Autonamen in allen bestehenden OCR-Boards neu auswerten -- ohne neuen Scan.

Warum: bis 2026-08-24 wurde der Autoname in ocr_leaderboard_frames.parse_line NUR
uebernommen, wenn auf derselben Zeile auch der Antrieb (AWD/RWD/FWD) erkannt wurde --
der Name steht zwischen Rang und Antriebsspalte, und ohne rechte Grenze wurde er
verworfen. Messbar: 85.565 Zeilen mit Antrieb, exakt 85.565 mit Namen, bei 213.492
Zeilen. Zwei Drittel der lesbaren Namen fielen weg.

Das ist mehr als ein Anzeigefehler. Der Name ist die EINZIGE Quelle, aus der die
car_ids der Speicher-Laeufe ihre Bezeichnung bekommen (join_ocr_names_to_car_ids
verbindet beide Quellen ueber die Rundenzeit). Fehlende Namen heissen also nicht nur
"Car #3665" im Dashboard, sondern auch: dasselbe Auto laeuft als Speicher-ID UND als
Bildschirm-Name durch die Wertung und tritt in seiner Klasse gegen sich selbst an.

Ein neuer Scan ist nicht noetig: die Rohzeile steht als `line` in jedem Board, sowohl in
rows.jsonl als auch im Parquet. Diese Reparatur wertet sie mit dem korrigierten Parser
neu aus und schreibt car_name zurueck.

Gemessen an 8 Boards vor dem Schreiben: brauchbare Namen (die ein Roster-Auto treffen)
31.274 -> 75.699, also +44.425 bei 93,0% -> 92,0% Trefferquote. Mehr Namen, praktisch
gleiche Qualitaet.

    python scripts/repair_car_names.py --dry-run
    python scripts/repair_car_names.py
"""

from __future__ import annotations

import argparse
import json
import shutil
import sys
from pathlib import Path

import pyarrow as pa
import pyarrow.parquet as pq

sys.path.insert(0, str(Path(__file__).resolve().parent))

import ocr_leaderboard_frames as ocr  # noqa: E402

ROOT = Path("data/memory_scans/full_sweep")


def reparse(line: str) -> str | None:
    """Autoname aus einer Rohzeile, oder None."""
    if not line:
        return None
    try:
        parsed = ocr.parse_line(str(line))
    except Exception:
        return None
    return (parsed or {}).get("car_name")


def repair_parquet(path: Path, dry: bool) -> tuple[int, int]:
    """car_name im Parquet neu setzen. Gibt (vorher belegt, nachher belegt)."""
    schema = pq.read_schema(path)
    if "line" not in schema.names:
        return (0, 0)
    table = pq.read_table(path)
    lines = table["line"].to_pylist()
    before = table["car_name"].to_pylist() if "car_name" in schema.names else [None] * len(lines)
    after = [reparse(l) or b for l, b in zip(lines, before)]
    filled_before = sum(1 for v in before if v)
    filled_after = sum(1 for v in after if v)
    if not dry and filled_after > filled_before:
        column = pa.array(after, type=pa.string())
        if "car_name" in schema.names:
            table = table.set_column(schema.names.index("car_name"), "car_name", column)
        else:
            table = table.append_column("car_name", column)
        backup = path.with_suffix(".parquet.bak")
        if not backup.exists():
            shutil.copyfile(path, backup)
        pq.write_table(table, path)
    return (filled_before, filled_after)


def repair_jsonl(path: Path, dry: bool) -> tuple[int, int]:
    """car_name in rows.jsonl neu setzen. Der Join liest diese Datei."""
    rows = []
    for text in path.read_text(encoding="utf-8-sig").splitlines():
        if not text.strip():
            continue
        try:
            rows.append(json.loads(text))
        except Exception:
            continue
    filled_before = sum(1 for r in rows if r.get("car_name"))
    for row in rows:
        name = reparse(row.get("line"))
        if name and not row.get("car_name"):
            row["car_name"] = name
    filled_after = sum(1 for r in rows if r.get("car_name"))
    if not dry and filled_after > filled_before:
        backup = path.with_suffix(".jsonl.bak")
        if not backup.exists():
            shutil.copyfile(path, backup)
        path.write_text("\n".join(json.dumps(r) for r in rows) + "\n", encoding="utf-8")
    return (filled_before, filled_after)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dry-run", action="store_true",
                        help="nur zaehlen, nichts schreiben")
    parser.add_argument("--limit", type=int, default=0,
                        help="nur die ersten N Boards (zum Testen)")
    args = parser.parse_args(argv)

    folders = sorted(p for p in ROOT.glob("ocr_*") if p.is_dir())
    if args.limit:
        folders = folders[: args.limit]
    print(f"{'Board':46} {'Parquet vorher/nachher':>24} {'jsonl vorher/nachher':>22}")
    tp_b = tp_a = tj_b = tj_a = 0
    for folder in folders:
        pqf, jl = folder / "leaderboard_entries.parquet", folder / "rows.jsonl"
        pb = pa_ = jb = ja = 0
        if pqf.exists() and pqf.stat().st_size:
            pb, pa_ = repair_parquet(pqf, args.dry_run)
        if jl.exists() and jl.stat().st_size:
            jb, ja = repair_jsonl(jl, args.dry_run)
        tp_b += pb; tp_a += pa_; tj_b += jb; tj_a += ja
        if pa_ > pb or ja > jb:
            print(f"{folder.name[:46]:46} {f'{pb:,} -> {pa_:,}':>24} {f'{jb:,} -> {ja:,}':>22}")
    print(f"\n{'GESAMT':46} {f'{tp_b:,} -> {tp_a:,}':>24} {f'{tj_b:,} -> {tj_a:,}':>22}")
    print(f"Parquet: {tp_a - tp_b:+,} Namen   jsonl: {tj_a - tj_b:+,} Namen")
    if args.dry_run:
        print("\n(Probelauf -- nichts geschrieben)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
