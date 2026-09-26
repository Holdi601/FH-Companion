from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Any

import pandas as pd

sys.path.insert(0, str(Path(__file__).resolve().parent))
from car_catalogue import codename_for, load_catalogue  # noqa: E402


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Merge structured FH6 ScoreboardRow memory captures."
    )
    parser.add_argument("--input-dir", required=True, type=Path)
    parser.add_argument("--output-dir", required=True, type=Path)
    parser.add_argument("--parquet-path", type=Path)
    parser.add_argument("--drop-raw", action="store_true")
    args = parser.parse_args()

    rows: dict[int, dict[str, Any]] = {}
    sources: list[str] = []
    for path in sorted(args.input_dir.glob("rows_*.json")):
        report = json.loads(path.read_text(encoding="utf-8"))
        sources.append(str(path.resolve()))
        for row in report.get("rows", []):
            if args.drop_raw:
                row.pop("raw_row_hex", None)
            rank = int(row["rank"])
            current = rows.get(rank)
            quality = (
                bool(row.get("submitted_time_utc")),
                len(str(row.get("gamertag", ""))),
                int(row.get("car_class_id", 999)) <= 20,
                int(row.get("car_powertrain_id", 999)) <= 20,
            )
            current_quality = (
                bool(current.get("submitted_time_utc")),
                len(str(current.get("gamertag", ""))),
                int(current.get("car_class_id", 999)) <= 20,
                int(current.get("car_powertrain_id", 999)) <= 20,
            ) if current else None
            if current is None or quality > current_quality:
                rows[rank] = row

    ordered = [rows[rank] for rank in sorted(rows)]

    # Join the human-readable codename onto each row from the accumulated
    # catalogue. Unknown carIds get "" until the catalogue is refreshed on a
    # board that loads them (see update_car_catalogue.ps1).
    catalogue = load_catalogue()
    for row in ordered:
        if "car_id" in row:
            row["car_codename"] = codename_for(row["car_id"], catalogue)

    frame = pd.DataFrame(ordered)
    args.output_dir.mkdir(parents=True, exist_ok=True)
    csv_path = args.output_dir / "leaderboard_entries.csv"
    json_path = args.output_dir / "leaderboard_entries.json"
    report_path = args.output_dir / "merge_report.json"
    frame.to_csv(csv_path, index=False)
    json_path.write_text(json.dumps(ordered, indent=2) + "\n", encoding="utf-8")

    parquet_path = args.parquet_path or (
        args.output_dir / "leaderboard_entries.parquet"
    )
    parquet_path.parent.mkdir(parents=True, exist_ok=True)
    frame.to_parquet(parquet_path, engine="pyarrow", index=False)

    ranks = sorted({int(row["rank"]) for row in ordered})
    missing: list[int] = []
    if ranks:
        present = set(ranks)
        missing = [
            rank
            for rank in range(ranks[0], ranks[-1] + 1)
            if rank not in present
        ]
    report = {
        "source_files": sources,
        "rows": len(ordered),
        "minimum_rank": ranks[0] if ranks else None,
        "maximum_rank": ranks[-1] if ranks else None,
        "missing_rank_count": len(missing),
        "missing_ranks_preview": missing[:200],
        "csv": str(csv_path.resolve()),
        "json": str(json_path.resolve()),
        "parquet": str(parquet_path.resolve()),
    }
    report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
