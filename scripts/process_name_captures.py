"""Extract, read and join every per-class frame capture, in one command.

`capture_boards_for_names.ps1` leaves one zip per performance class. For each one this
unpacks it, runs the OCR, finds the memory scan of the same board, and merges the
resulting carId -> name votes into `config/fh6_car_id_names.json`.

The join key is the lap time, so the memory scan may be days older than the capture:
ranks shift whenever someone posts a new time, a lap time does not.

    python scripts/process_name_captures.py --frames data/runtime/frames \
        --boards data/memory_scans/full_sweep --route idx00
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
import zipfile
from pathlib import Path

CLASS_FROM_NAME = re.compile(r"_(D|C|B|A|S1|S2|R|X)$")


def newest_board(boards: Path, route: str, klass: str) -> Path | None:
    """The board directory for this route and class holding the most rows."""
    best: tuple[int, Path] | None = None
    for state_path in boards.glob(f"*{route}_{klass}_*/state.json"):
        parquet = state_path.parent / "leaderboard_entries.parquet"
        if not parquet.exists():
            continue
        try:
            rows = int(json.loads(state_path.read_text(encoding="utf-8-sig"))
                       .get("rows_collected") or 0)
        except Exception:
            rows = 0
        if best is None or rows > best[0]:
            best = (rows, parquet)
    return best[1] if best else None


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--frames", type=Path, default=Path("data/runtime/frames"))
    parser.add_argument("--boards", type=Path,
                        default=Path("data/memory_scans/full_sweep"))
    parser.add_argument("--route", default="idx00")
    parser.add_argument("--offset-x", type=int, default=78)
    parser.add_argument("--workers", type=int, default=10)
    parser.add_argument("--only", default="", help="process one class only")
    args = parser.parse_args(argv)

    zips = sorted(args.frames.glob("*.zip"))
    if not zips:
        print(f"no captures in {args.frames}")
        return 2

    for archive in zips:
        match = CLASS_FROM_NAME.search(archive.stem)
        if not match:
            continue
        klass = match.group(1)
        if args.only and klass != args.only:
            continue
        folder = archive.with_suffix("")
        rows = folder / "rows.jsonl"

        if not folder.exists() or not any(folder.glob("*.png")):
            folder.mkdir(parents=True, exist_ok=True)
            with zipfile.ZipFile(archive) as bundle:
                bundle.extractall(folder)
        frames = len(list(folder.glob("*.png")))
        print(f"\n=== {archive.stem}: class {klass}, {frames} frames")

        if not rows.exists():
            result = subprocess.run(
                [sys.executable, "scripts/ocr_leaderboard_frames.py",
                 "--frames", str(folder), "--out", str(rows),
                 "--batch", "40", "--workers", str(args.workers),
                 "--offset-x", str(args.offset_x)],
                capture_output=True, text=True)
            tail = (result.stdout or "").strip().splitlines()[-8:]
            print("\n".join("  " + line for line in tail))
            if result.returncode != 0:
                print("  OCR failed:", (result.stderr or "")[-300:])
                continue

        board = newest_board(args.boards, args.route, klass)
        if board is None:
            # Without a memory scan of the same board there is no car_id to attach the
            # name to. The frames stay on disk; the join can run once one exists.
            print(f"  no memory scan for {args.route} {klass}; nothing to join yet")
            continue
        joined = subprocess.run(
            [sys.executable, "scripts/build_car_roster.py",
             "--ocr", str(rows), "--parquet", str(board)],
            capture_output=True, text=True)
        for line in (joined.stdout or "").splitlines():
            if any(word in line for word in ("lap times", "named", "unmatched")):
                print("  " + line)
        if joined.returncode != 0:
            print("  join failed:", (joined.stderr or "")[-300:])
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
