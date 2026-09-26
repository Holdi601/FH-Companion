"""Classify how each memory-scan run ended, from its own runner.log.

The question this answers: was the board really finished, or did the paging loop
leave the cursor above the bottom of the loaded page and time out? Those look
identical in the manifest (both end on a multiple of 50) but not in the log -- a
run that died that way has a `key UP xN to recover` gap recovery immediately
before the stall, and its last downward move is the 10-row no-progress cap.

Usage:
    python scripts/audit_truncation_signature.py [sweep_root]

Default root is data/memory_scans/full_sweep.
"""
from __future__ import annotations

import io
import os
import re
import sys
from pathlib import Path

STALL = re.compile(r"no row block found for rank (\d+) \((\d+)/(\d+)\)")
RECOVER = re.compile(r"block started at \d+, past wanted \d+; key UP x(\d+)")
DOWN = re.compile(r"key DOWN x(\d+)")
CROSS = re.compile(r"key DOWN x(\d+) to cross into the page")
SCROLLBAR = re.compile(r"scrollbar thumb is at ([\d.]+)")
STATUS = re.compile(r"capture finished with status=(\w+)")


def classify(log_text: str) -> dict:
    stalls = STALL.findall(log_text)
    ups = RECOVER.findall(log_text)
    downs = DOWN.findall(log_text)
    crosses = CROSS.findall(log_text)
    status = STATUS.search(log_text)
    scrollbar = SCROLLBAR.search(log_text)
    dead_rank = int(stalls[-1][0]) if stalls else None
    return {
        "status": status.group(1) if status else "?",
        "dead_rank": dead_rank,
        "mod50": (dead_rank - 1) % 50 if dead_rank else None,
        "gap_recoveries": len(ups),
        "last_up": int(ups[-1]) if ups else 0,
        "last_down": int(downs[-1]) if downs else 0,
        "crossings": len(crosses),
        "scrollbar": scrollbar.group(1) if scrollbar else "",
        # The signature: a gap recovery scrolled the cursor up further than any
        # later press brought it back, so the stall is the loop's, not the board's.
        "suspect": bool(ups) and (int(ups[-1]) > int(downs[-1]) if downs else True),
    }


def main() -> int:
    root = Path(sys.argv[1] if len(sys.argv) > 1 else "data/memory_scans/full_sweep")
    runs = sorted(p for p in root.glob("*") if (p / "runner.log").exists())
    if not runs:
        print("no runs with a runner.log under", root)
        return 1
    print(
        "%-52s %-15s %8s %4s %7s %6s %8s %5s %s"
        % ("run", "status", "died at", "m50", "gaprecov", "lastUP", "lastDOWN", "cross", "suspect")
    )
    suspect = 0
    for run in runs:
        info = classify(io.open(run / "runner.log", encoding="utf-8", errors="replace").read())
        suspect += bool(info["suspect"])
        print(
            "%-52s %-15s %8s %4s %7d %6d %8d %5d %s"
            % (
                run.name[:52],
                info["status"],
                info["dead_rank"] if info["dead_rank"] else "-",
                info["mod50"] if info["mod50"] is not None else "-",
                info["gap_recoveries"],
                info["last_up"],
                info["last_down"],
                info["crossings"],
                "YES" if info["suspect"] else "",
            )
        )
    print()
    print(
        "%d of %d runs ended with the cursor left above the page bottom -- those are the loop's"
        " stalls, not the board's end" % (suspect, len(runs))
    )
    print(
        "A run made after the 2026-08-21 paging fix should show crossings > 0 and no suspect"
        " endings; see docs/extraction_speed_plan.md."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
