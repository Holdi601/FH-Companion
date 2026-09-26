"""Locate every array of scoreboard records in a live process, by XUID.

`forza_scoreboard_scan.py` finds records by looking for a duplicated u32
(`position`, `rank`) pair. That works for the UI's on-screen row list, where
position equals rank, but it will silently miss any buffer where they differ --
and the 50-row block the service returns is exactly the kind of place where
`position` might be an index rather than a rank. On build 6.420.696.0 that
scanner found one 11-row array, which is precisely the visible viewport, so the
larger fetch buffer is either absent or invisible to that heuristic.

This detects records by a far more selective signature instead: Xbox Live XUIDs
occupy a narrow numeric band, so a vectorised pass over the heap as u64 finds
candidate record positions with almost no false positives, whatever the
surrounding structure looks like. Positions are then grouped into runs of
constant spacing, which yields both the record stride and the element count
without assuming either.

Read-only: PROCESS_VM_READ only, no injection.

    python find_scoreboard_buffers.py --process-name forzahorizon6 --output buffers.json
"""

from __future__ import annotations

import argparse
import json
import struct
import sys
from collections import Counter
from pathlib import Path
from typing import Any

sys.path.insert(0, str(Path(__file__).resolve().parent))

from forza_scoreboard_scan import ProcessMemory, resolve_pid  # noqa: E402
from forza_scoreboard_layout import (  # noqa: E402
    XUID_MAXIMUM,
    XUID_MINIMUM,
    read_sso_string,
)

# Records are 8-byte aligned in every build seen so far, so only aligned u64
# positions need testing.
ALIGNMENT = 8


def xuid_offsets(block: bytes) -> list[int]:
    """Aligned byte offsets whose u64 falls in the Xbox Live XUID band."""
    import numpy

    usable = len(block) - (len(block) % 8)
    if usable < 8:
        return []
    words = numpy.frombuffer(block[:usable], dtype="<u8")
    hits = numpy.nonzero((words >= XUID_MINIMUM) & (words <= XUID_MAXIMUM))[0]
    return (hits * 8).tolist()


# A block holding more XUID-band hits than this is not a scoreboard array, it is
# a region whose contents happen to land in the band. Run detection is superlinear
# in the hit count, so an unbounded block is how a scan turns into the 19- and
# 7-minute CPU burns that produced no output at all.
MAXIMUM_HITS_PER_BLOCK = 20_000


def runs_by_stride(
    offsets: list[int], *, minimum_records: int
) -> list[tuple[int, list[int]]]:
    """Group offsets into runs of constant spacing.

    The dominant gap between neighbouring XUIDs in an array of records IS the
    record stride, so it is derived rather than assumed.
    """
    if len(offsets) < minimum_records:
        return []
    gaps = Counter(
        offsets[index + 1] - offsets[index] for index in range(len(offsets) - 1)
    )
    runs: list[tuple[int, list[int]]] = []
    claimed: set[int] = set()
    # Hoisted: the set is identical on every iteration, and rebuilding it per
    # candidate stride made the pass eight times heavier for nothing.
    positions = set(offsets)
    # Consider the handful of most common gaps; a real record stride shows up
    # many times, random coincidences do not.
    for stride, count in gaps.most_common(8):
        if stride <= 0 or stride % ALIGNMENT != 0 or count + 1 < minimum_records:
            continue
        for start in offsets:
            if start in claimed:
                continue
            run = [start]
            walk = start
            while walk + stride in positions:
                walk += stride
                run.append(walk)
            if len(run) >= minimum_records:
                claimed.update(run)
                runs.append((stride, run))
    runs.sort(key=lambda item: len(item[1]), reverse=True)
    return runs


def describe_run(
    block: bytes, stride: int, run: list[int], base_address: int
) -> dict[str, Any]:
    """Summarise a candidate array without assuming a field layout."""
    # In every build so far the gamertag string sits 8 bytes after the XUID.
    gamertags: list[str] = []
    ranks: list[int] = []
    for position in run:
        tag = read_sso_string(block, position + 8)
        if tag:
            gamertags.append(tag)
        # The (position, rank) pair sat 0x10 before the XUID on both builds seen.
        rank_at = position - 0x10
        if rank_at >= 0 and rank_at + 8 <= len(block):
            first, second = struct.unpack_from("<II", block, rank_at)
            if 1 <= first <= 10_000_000:
                ranks.append(first)
                if first != second:
                    pass  # position != rank here; recorded via pair_matches below
    pair_matches = 0
    for position in run:
        rank_at = position - 0x10
        if rank_at >= 0 and rank_at + 8 <= len(block):
            first, second = struct.unpack_from("<II", block, rank_at)
            if first == second and 1 <= first <= 10_000_000:
                pair_matches += 1

    consecutive = False
    if len(ranks) >= 2:
        consecutive = all(
            ranks[index + 1] == ranks[index] + 1 for index in range(len(ranks) - 1)
        )

    return {
        "array_address": f"0x{base_address + run[0]:016x}",
        "stride": stride,
        "stride_hex": f"0x{stride:x}",
        "records": len(run),
        "gamertags_readable": len(gamertags),
        "gamertag_sample": gamertags[:5],
        "ranks_readable": len(ranks),
        "rank_minimum": min(ranks) if ranks else None,
        "rank_maximum": max(ranks) if ranks else None,
        "ranks_consecutive": consecutive,
        "duplicated_rank_pairs": pair_matches,
    }


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Find arrays of scoreboard records in a live process by XUID."
    )
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--pid", type=int)
    source.add_argument("--process-name")
    parser.add_argument("--minimum-records", type=int, default=5)
    parser.add_argument("--block-size-mb", type=int, default=32)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()

    if sys.platform != "win32":
        raise RuntimeError("Scanning a live process requires Windows.")

    pid = args.pid if args.pid else resolve_pid(args.process_name)
    block_size = max(1, args.block_size_mb) * 1024 * 1024

    candidates: list[dict[str, Any]] = []
    skipped_blocks: list[dict[str, Any]] = []
    scanned = 0
    total_xuids = 0
    with ProcessMemory(pid) as memory:
        for base, size in memory.regions(minimum_size=0x1000):
            offset = 0
            while offset < size:
                want = min(block_size, size - offset)
                block = memory.read(base + offset, want)
                if not block:
                    offset += want
                    continue
                scanned += len(block)
                found = xuid_offsets(block)
                total_xuids += len(found)
                if len(found) > MAXIMUM_HITS_PER_BLOCK:
                    # Say so rather than truncating quietly: a skipped block is a
                    # gap in coverage, and a scan that hides one reads as complete.
                    skipped_blocks.append(
                        {"address": f"0x{base + offset:016x}", "hits": len(found)}
                    )
                    offset += want
                    continue
                if len(found) >= args.minimum_records:
                    for stride, run in runs_by_stride(
                        found, minimum_records=args.minimum_records
                    ):
                        candidates.append(
                            describe_run(block, stride, run, base + offset)
                        )
                offset += want

    candidates.sort(key=lambda item: item["records"], reverse=True)
    report = {
        "process_id": pid,
        "scanned_bytes": scanned,
        "xuid_candidates": total_xuids,
        "array_count": len(candidates),
        "skipped_dense_blocks": skipped_blocks,
        "stride_histogram": dict(
            Counter(item["stride_hex"] for item in candidates).most_common(12)
        ),
        "arrays": candidates[:60],
    }
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")

    print(
        json.dumps(
            {key: value for key, value in report.items() if key != "arrays"}, indent=2
        )
    )
    if skipped_blocks:
        print(
            f"WARNING {len(skipped_blocks)} block(s) exceeded"
            f" {MAXIMUM_HITS_PER_BLOCK} XUID-band hits and were not searched;"
            " coverage is incomplete."
        )
    print(f"{'records':>8} {'stride':>8} {'ranks':>13} {'consec':>7} {'pairs':>6}  address")
    for item in candidates[:25]:
        span = f"{item['rank_minimum']}-{item['rank_maximum']}"
        print(
            f"{item['records']:>8} {item['stride_hex']:>8} {span:>13}"
            f" {str(item['ranks_consecutive']):>7} {item['duplicated_rank_pairs']:>6}"
            f"  {item['array_address']}  {item['gamertag_sample'][:3]}"
        )
    return 0 if candidates else 2


if __name__ == "__main__":
    raise SystemExit(main())
