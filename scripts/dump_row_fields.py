"""Dump raw row fields positionally, whether or not the rank field is populated.

Every other tool here finds rows by searching for the rank stored twice as u32.
That makes the rank the key to the whole dataset -- and the operator observed that
when the list is scrolled quickly the rank and the player name are blank while the
car and lap time are already there. If that is true of the memory and not only of
the rendering, then the rows exist, carry the data that matters, and are invisible
to every finder in this repo, because the field they search by is the one that
arrives last.

This tool answers that question. It walks an array by stride from a base address
and prints the raw fields at each position, so a row whose rank is still zero is
still reported instead of skipped.

    python dump_row_fields.py --pid 1234 --base 0x1c5665fe000 --count 60

Read-only.
"""

from __future__ import annotations

import argparse
import json
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from forza_fast_scan import find_rank_address, load_profile  # noqa: E402
from forza_scoreboard_scan import ProcessMemory, resolve_pid  # noqa: E402


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--process-name", default="forzahorizon6")
    parser.add_argument("--profile", type=Path,
                        help="build profile holding the stride and field offsets")
    parser.add_argument("--base", type=lambda v: int(v, 0),
                        help="array base; if omitted, --anchor-rank is searched for")
    parser.add_argument("--anchor-rank", type=int, default=1,
                        help="rank to locate the array by when --base is not given")
    parser.add_argument("--count", type=int, default=60)
    parser.add_argument("--output-json", type=Path)
    args = parser.parse_args(argv)

    cached = load_profile(args.profile)
    if not cached:
        print("no build profile; run a scan once so the layout is known", flush=True)
        return 2
    addresses, layout = cached

    pid = resolve_pid(args.process_name)
    with ProcessMemory(pid) as memory:
        base = args.base
        if base is None:
            base = find_rank_address(memory, layout, args.anchor_rank,
                                     hint=addresses[0] if addresses else None)
            if base is None:
                print(f"could not locate rank {args.anchor_rank} to anchor on", flush=True)
                return 2
            print(f"anchored on rank {args.anchor_rank} at 0x{base:x}", flush=True)

        block = memory.read(base, layout.stride * (args.count + 1))
        if not block:
            print("read failed", flush=True)
            return 2

        records = []
        for index in range(args.count):
            offset = index * layout.stride
            if offset + 8 > len(block):
                break
            first, second = struct.unpack_from("<II", block, offset)
            entry = {"index": index, "address": "0x%x" % (base + offset),
                     "rank": first, "rank_dup": second,
                     "rank_populated": bool(first and first == second)}
            if layout.lap_time is not None and offset + layout.lap_time + 8 <= len(block):
                lap = struct.unpack_from("<d", block, offset + layout.lap_time)[0]
                entry["lap_time"] = round(lap, 4) if 1.0 <= lap <= 86400.0 else None
            if layout.xuid is not None and offset + layout.xuid + 8 <= len(block):
                entry["xuid"] = struct.unpack_from("<Q", block, offset + layout.xuid)[0]
            records.append(entry)

        populated = [r for r in records if r["rank_populated"]]
        with_time = [r for r in records if r.get("lap_time")]
        with_xuid = [r for r in records if r.get("xuid")]
        print("positions read       : %d" % len(records), flush=True)
        print("rank populated       : %d" % len(populated), flush=True)
        print("lap time present     : %d" % len(with_time), flush=True)
        print("xuid present         : %d" % len(with_xuid), flush=True)
        print("", flush=True)
        print("%-6s %-18s %-8s %-8s %s" % ("idx", "address", "rank", "time", "xuid"), flush=True)
        for r in records[:40]:
            print("%-6d %-18s %-8s %-8s %s" % (
                r["index"], r["address"],
                r["rank"] if r["rank_populated"] else "-",
                r.get("lap_time") if r.get("lap_time") else "-",
                r.get("xuid") or "-"), flush=True)

        verdict = ("rows carry car/time/xuid while the rank is blank -- position-based "
                   "reading would recover them"
                   if with_time and len(with_time) > len(populated)
                   else "every row with data also has its rank; the rank is not the laggard")
        print("", flush=True)
        print("VERDICT: %s" % verdict, flush=True)

        if args.output_json:
            args.output_json.write_text(json.dumps({
                "base": "0x%x" % base, "stride": layout.stride,
                "positions": len(records), "rank_populated": len(populated),
                "lap_time_present": len(with_time), "xuid_present": len(with_xuid),
                "records": records,
            }, indent=2), encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
