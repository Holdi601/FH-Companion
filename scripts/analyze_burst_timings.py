"""Profile a burst-scan run: where each strategy's cycle time actually goes.

`forza_burst_scan.ps1` writes one row per cycle with the two costs separated --
`keys_ms` is the time spent sending presses, `wait_ms` the time spent waiting for
the game to serve the next block. Any argument about how to make the scan faster
is an argument about which of those two numbers dominates, so print them per
strategy rather than eyeballing the log.

    python scripts/analyze_burst_timings.py data/runtime/burst_scan/*_timings.csv
"""

from __future__ import annotations

import csv
import statistics
import sys
from collections import defaultdict
from pathlib import Path


def summarise(path: Path) -> None:
    per_strategy: dict[str, list[dict]] = defaultdict(list)
    with path.open(newline="", encoding="utf-8-sig") as handle:
        for row in csv.DictReader(handle):
            per_strategy[row["strategy"]].append(row)

    print(f"\n{path.name}")
    header = ("strategy", "cycles", "ranks", "keys_ms", "wait_ms", "cycle_ms",
              "ranks/min", "per-50")
    print("{:<10} {:>6} {:>6} {:>8} {:>8} {:>9} {:>10} {:>8}".format(*header))
    for strategy, rows in per_strategy.items():
        keys = [float(r["keys_ms"]) for r in rows]
        wait = [float(r["wait_ms"]) for r in rows]
        cycle = [float(r["cycle_ms"]) for r in rows]
        gained = sum(int(r["gained"]) for r in rows)
        total_ms = sum(cycle)
        # Rate over the strategy's own wall clock, and the cost of 50 ranks --
        # the unit the whole plan is written in.
        rate = gained / (total_ms / 60000.0) if total_ms else 0.0
        per50 = (total_ms / gained * 50 / 1000.0) if gained else float("nan")
        print("{:<10} {:>6} {:>6} {:>8.0f} {:>8.0f} {:>9.0f} {:>10.0f} {:>8.1f}".format(
            strategy, len(rows), gained,
            statistics.median(keys), statistics.median(wait),
            statistics.median(cycle), rate, per50))

    dead = [r for rows in per_strategy.values() for r in rows if int(r["gained"]) == 0]
    if dead:
        print(f"dead cycles: {len(dead)}"
              f" (median {statistics.median([float(r['cycle_ms']) for r in dead]):.0f} ms each)")


def main(argv: list[str]) -> int:
    if not argv:
        print(__doc__)
        return 2
    for pattern in argv:
        path = Path(pattern)
        if path.exists():
            summarise(path)
            continue
        matches = sorted(Path(path.parent or ".").glob(path.name))
        if not matches:
            print(f"no timing csv matched {pattern}")
        for match in matches:
            summarise(match)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
