"""Rebuild a captured board's combined exports with the corrected car fields.

The chunk JSONs keep `raw_row_hex`, so the car block the original decoder skipped
or mislabelled can be recovered without re-running the game:

  * carId (uint16 @ 0x198)           -- was not extracted at all
  * carClassId (uint8 @ 0x19A)       -- the real performance class
  * carPerformanceIndex (f32 @ 0x19C) -- raw, still uncalibrated
  * carDriveTypeId (u32 @ 0x1A0)     -- was mislabelled `car_class_id`
  * carPowertrainId (u32 @ 0x1A4)    -- was mislabelled `car_drivetrain_id`

This regenerates combined/leaderboard_entries.{csv,json,parquet} in place from
the chunks, so all three formats carry the corrected columns. It is a backfill
for boards captured before the decoder was fixed; new scans emit these directly.

    python scripts/redecode_car_fields.py <run_dir> [--all] [--no-parquet]

With --all, <run_dir> is treated as a root and every subtree with a chunks/ dir
holding raw rows is rebuilt.
"""

from __future__ import annotations

import argparse
import glob
import io
import json
import os
import struct
from pathlib import Path

CAR_ID_OFFSET = 0x198
CAR_CLASS_ID_OFFSET = 0x19A
CAR_PI_OFFSET = 0x19C
DRIVE_TYPE_ID_OFFSET = 0x1A0
POWERTRAIN_ID_OFFSET = 0x1A4
MIN_ROW_BYTES = 0x1A8  # need the car block plus the flags that follow it

COLUMNS = [
    "rank", "xuid", "gamertag", "lap_time_seconds", "submitted_time_utc",
    "car_id", "car_codename", "car_class_id", "car_performance_index_raw",
    "car_drive_type_id", "car_powertrain_id",
    "used_stm", "used_tcs", "used_abs", "used_friction_assist",
    "used_auto_brake", "used_auto_shifting", "used_clutch",
    "used_super_easy_assist", "is_clean", "has_ghost_file",
]

# non-car columns copied verbatim from the chunk (car_* are (re)derived)
PASSTHROUGH = COLUMNS[:5] + COLUMNS[11:]

from car_catalogue import load_catalogue, codename_for  # noqa: E402


def rebuild_run(run_dir: Path, write_parquet: bool) -> tuple[int, int]:
    """Return (rows_written, rows_without_raw). Rebuilds combined/ in place."""
    chunks = sorted(glob.glob(str(run_dir / "chunks" / "*.json")))
    if not chunks:
        raise FileNotFoundError(f"no chunk JSONs under {run_dir}/chunks")

    catalogue = load_catalogue()

    by_rank: dict[int, dict] = {}
    missing_raw = 0
    for path in chunks:
        data = json.load(io.open(path, encoding="utf-8-sig"))
        for row in data.get("rows", []):
            hexrow = row.get("raw_row_hex") or ""
            if len(hexrow) < MIN_ROW_BYTES * 2 + 8:
                missing_raw += 1
                continue
            raw = bytes.fromhex(hexrow)
            rank = int(row["rank"])
            record = {key: row.get(key) for key in PASSTHROUGH}
            record["rank"] = rank
            car_id = struct.unpack_from("<H", raw, CAR_ID_OFFSET)[0]
            record["car_id"] = car_id
            record["car_codename"] = codename_for(car_id, catalogue)
            record["car_class_id"] = raw[CAR_CLASS_ID_OFFSET]
            record["car_performance_index_raw"] = struct.unpack_from("<f", raw, CAR_PI_OFFSET)[0]
            record["car_drive_type_id"] = struct.unpack_from("<I", raw, DRIVE_TYPE_ID_OFFSET)[0]
            record["car_powertrain_id"] = struct.unpack_from("<I", raw, POWERTRAIN_ID_OFFSET)[0]
            by_rank[rank] = record

    if not by_rank:
        raise ValueError(f"{run_dir}: chunks carry no raw rows; cannot backfill")

    ordered = [by_rank[r] for r in sorted(by_rank)]
    combined = run_dir / "combined"
    combined.mkdir(parents=True, exist_ok=True)

    import csv
    with io.open(combined / "leaderboard_entries.csv", "w", encoding="utf-8", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=COLUMNS)
        writer.writeheader()
        writer.writerows(ordered)

    io.open(combined / "leaderboard_entries.json", "w", encoding="utf-8").write(
        json.dumps(ordered, separators=(",", ":"))
    )

    if write_parquet:
        import pandas as pd
        pd.DataFrame(ordered, columns=COLUMNS).to_parquet(
            combined / "leaderboard_entries.parquet", index=False
        )

    return len(ordered), missing_raw


def main() -> int:
    parser = argparse.ArgumentParser(description="Backfill corrected car fields into combined exports.")
    parser.add_argument("run_dir", type=Path)
    parser.add_argument("--all", action="store_true", help="treat run_dir as a root and rebuild every board under it")
    parser.add_argument("--no-parquet", action="store_true")
    args = parser.parse_args()

    if args.all:
        roots = sorted({
            Path(os.path.dirname(os.path.dirname(p)))
            for p in glob.glob(str(args.run_dir / "**" / "chunks" / "*.json"), recursive=True)
        })
    else:
        roots = [args.run_dir]

    rebuilt = 0
    for run in roots:
        try:
            rows, missing = rebuild_run(run, write_parquet=not args.no_parquet)
        except (ValueError, FileNotFoundError) as error:
            print(f"skip {os.path.relpath(run)}: {error}")
            continue
        note = f" ({missing} rows lacked raw_row_hex)" if missing else ""
        print(f"rebuilt {os.path.relpath(run)}: {rows} rows{note}")
        rebuilt += 1

    print(f"done: {rebuilt} board(s) rebuilt")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
