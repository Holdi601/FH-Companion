"""Turn a flow scan's rows.jsonl into the outputs the sweep already understands.

`forza_burst_scan.ps1 -Flow` writes one JSON object per row and nothing else, because
its job was measurement. The sweep reads a board through three files: `state.json`
(status, rows, maximum rank), `combined/merge_report.json` (rows, maximum_rank,
missing_rank_count) and `leaderboard_entries.parquet`. This writes those, so the flow
scanner can replace the waiting loop without touching the sweep's bookkeeping or the
analytics build.

It also reports the gaps rather than hiding them: a flow scan samples a rotating row
pool, so it misses some ranks by construction, and the missing list is what a second
pass is aimed at.

    python convert_flow_rows.py --rows rows.jsonl --out data/memory_scans/.../run_id
"""

from __future__ import annotations

import argparse
import json
from datetime import datetime, timezone
from pathlib import Path


def load_rows(path: Path) -> list[dict]:
    """Tolerant reader: a killed scanner can leave one truncated last line."""
    rows, broken = [], 0
    for line in path.read_bytes().split(b"\n"):
        line = line.strip()
        if not line:
            continue
        try:
            rows.append(json.loads(line.decode("utf-8")))
        except Exception:
            broken += 1
    if broken:
        print(f"[convert] skipped {broken} unparsable line(s)")
    return rows


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rows", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--run-id", default="")
    parser.add_argument("--track", default="")
    parser.add_argument("--performance-class", default="")
    parser.add_argument("--rivals-mode", default="")
    parser.add_argument("--status", default="",
                        help="override; default is derived from the gaps")
    parser.add_argument("--minutes", type=float, default=0.0)
    args = parser.parse_args(argv)

    rows = load_rows(args.rows)
    if not rows:
        print("[convert] no rows")
        return 2

    # One row per rank, preferring the copy that has a gamertag: the pool serves a
    # row before its name arrives, and the scanner writes the nameless version so the
    # rank is not lost.
    best: dict[int, dict] = {}
    for row in rows:
        rank = int(row.get("rank") or 0)
        if rank < 1:
            continue
        current = best.get(rank)
        if current is None or (not current.get("gamertag") and row.get("gamertag")):
            best[rank] = row
    ordered = [best[rank] for rank in sorted(best)]
    ranks = sorted(best)
    low, high = ranks[0], ranks[-1]
    missing = [rank for rank in range(low, high + 1) if rank not in best]
    nameless = sum(1 for row in ordered if not row.get("gamertag"))

    args.out.mkdir(parents=True, exist_ok=True)
    combined = args.out / "combined"
    combined.mkdir(parents=True, exist_ok=True)

    report = {
        "rows": len(ordered),
        "minimum_rank": low,
        "maximum_rank": high,
        "missing_rank_count": len(missing),
        # Capped: a board scanned from rank 1 with a shallow start can otherwise
        # write tens of thousands of numbers nobody reads.
        "missing_ranks": missing[:5000],
        "rows_without_gamertag": nameless,
        "source": "flow",
    }
    (combined / "merge_report.json").write_text(json.dumps(report, indent=2),
                                                encoding="utf-8")

    status = args.status
    if not status:
        # The sweep treats "truncated" as resumable and "complete" as done. A flow
        # pass with gaps is neither finished nor broken: it is a pass that needs
        # another one, which is exactly what "truncated" already triggers.
        status = "complete" if not missing else "truncated"
    state = {
        "run_id": args.run_id or args.out.name,
        "status": status,
        "track": args.track,
        "performance_class": args.performance_class,
        "rivals_mode": args.rivals_mode,
        "rows_collected": len(ordered),
        "minimum_rank": low,
        "maximum_rank": high,
        "missing_ranks": len(missing),
        "rows_without_gamertag": nameless,
        "ranks_per_minute": round(len(ordered) / args.minutes, 1) if args.minutes else None,
        "scanner": "flow",
        "completed_at": datetime.now(timezone.utc).isoformat(),
    }
    (args.out / "state.json").write_text(json.dumps(state, indent=2), encoding="utf-8")

    try:
        import pandas

        frame = pandas.DataFrame(ordered)
        frame.to_parquet(args.out / "leaderboard_entries.parquet", index=False)
        wrote_parquet = True
    except Exception as error:                 # parquet is a convenience, not the data
        print(f"[convert] parquet skipped ({error})")
        wrote_parquet = False

    print(f"[convert] {len(ordered)} rows, {low}..{high}, {len(missing)} missing "
          f"({100 * len(missing) / max(1, high - low + 1):.1f}%), "
          f"{nameless} without a name, status={status}, "
          f"parquet={'yes' if wrote_parquet else 'no'} -> {args.out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
