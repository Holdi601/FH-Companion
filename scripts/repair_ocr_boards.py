"""Re-apply the straggler cut to boards written before it worked properly.

The first version of the cut removed one trailing clump of mis-numbered rows. Wrong
frame bases arrive in several separated clumps, so boards written with it kept phantom
ranks thousands past where the list actually reached -- 64 of them on Hokubu A, which
also made its coverage read 64% instead of the ~93% it really had.

Rewrites rows.jsonl, the merge report, state.json and the parquet in place.

    python scripts/repair_ocr_boards.py
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))

from ocr_board_sweep import drop_stragglers  # noqa: E402


def repair(run_root: Path) -> tuple[int, int] | None:
    rows_path = run_root / "rows.jsonl"
    if not rows_path.exists():
        return None
    rows: dict[int, dict] = {}
    for line in rows_path.read_text(encoding="utf-8").splitlines():
        if line.strip():
            row = json.loads(line)
            rows[int(row["rank"])] = row
    before = len(rows)
    kept, dropped = drop_stragglers(dict(rows))
    if not dropped:
        return before, 0

    ordered = [kept[rank] for rank in sorted(kept)]
    with rows_path.open("w", encoding="utf-8") as handle:
        for row in ordered:
            handle.write(json.dumps(row, separators=(",", ":")) + "\n")

    low, high = ordered[0]["rank"], ordered[-1]["rank"]
    missing = (high - low + 1) - len(ordered)
    report = {"rows": len(ordered), "minimum_rank": low, "maximum_rank": high,
              "missing_rank_count": missing, "stragglers_dropped": dropped,
              "source": "ocr", "repaired": True}
    (run_root / "combined").mkdir(exist_ok=True)
    (run_root / "combined" / "merge_report.json").write_text(
        json.dumps(report, indent=2), encoding="utf-8")

    state_path = run_root / "state.json"
    if state_path.exists():
        state = json.loads(state_path.read_text(encoding="utf-8"))
        state.update({"rows_collected": len(ordered), "minimum_rank": low,
                      "maximum_rank": high, "missing_ranks": missing,
                      "status": "complete" if missing == 0 else "truncated"})
        state_path.write_text(json.dumps(state, indent=2), encoding="utf-8")

    try:
        import pandas

        pandas.DataFrame(ordered).to_parquet(run_root / "leaderboard_entries.parquet",
                                             index=False)
    except Exception as error:
        print(f"  parquet not rewritten ({error})")
    return before, dropped


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path,
                        default=Path("data/memory_scans/full_sweep"))
    args = parser.parse_args(argv)

    total_dropped = 0
    for run_root in sorted(args.root.glob("ocr_*")):
        result = repair(run_root)
        if result is None:
            continue
        before, dropped = result
        if dropped:
            total_dropped += dropped
            print(f"{run_root.name}: {before} -> {before - dropped} rows "
                  f"({dropped} phantom ranks removed)")
    print(f"done; {total_dropped} phantom rank(s) removed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
