"""Find a stable pointer to the leaderboard's row block, so reads stop searching.

The scan's worst cost is not reading memory -- a page read is ~20 ms -- it is FINDING
the page: the block moves between heap arenas, so the loop pays an exact/near/sweep
ladder per page, and a miss costs 60-100 s. Something in the game holds a pointer to
that block (a container's `begin` field, most likely). If we can name that holder,
every page read becomes: read 8 bytes, then read the rows. No search at all.

The hunt needs the list to be MOVING, because a holder is only proven by tracking:
an address that happens to contain the block address once could be a stale copy, a
register spill, or another reader's cache. So run this while something scrolls the
board -- `forza_burst_scan.ps1 -Flow` is exactly that -- and the phases are:

  1. locate one row block the ordinary way (one search, paid once)
  2. find every 8-byte slot in the process that holds that address
  3. watch those slots while the list scrolls: the real holder keeps pointing at a
     VALID row block, and points at a different one each time the page turns
  4. classify the survivors -- inside the executable's image (a static RVA, the
     jackpot) or on the heap (then find who points at THEM, one level up)

Read-only throughout: PROCESS_QUERY_INFORMATION|PROCESS_VM_READ, no writes, no
injection, safe to run against a game a scan is already reading.

    python find_row_pointer_chain.py --profile build_profile.json --watch-seconds 90
"""

from __future__ import annotations

import argparse
import ctypes
import ctypes.wintypes as wintypes
import json
import struct
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from forza_fast_scan import (  # noqa: E402
    Pager,
    discover,
    find_addresses,
    load_profile,
)
from forza_scoreboard_scan import (  # noqa: E402
    ProcessMemory,
    rank_pair_offsets,
    resolve_pid,
)

kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
psapi = ctypes.WinDLL("psapi", use_last_error=True)

MEM_COMMIT = 0x1000
MEM_IMAGE = 0x1000000
MEM_MAPPED = 0x40000
MEM_PRIVATE = 0x20000
PAGE_NOACCESS = 0x01
PAGE_GUARD = 0x100
LIST_MODULES_ALL = 0x03


class MODULEINFO(ctypes.Structure):
    _fields_ = [("lpBaseOfDll", ctypes.c_void_p),
                ("SizeOfImage", wintypes.DWORD),
                ("EntryPoint", ctypes.c_void_p)]


# Without argtypes, ctypes marshals a 64-bit module base as a C int and raises
# "int too long to convert" on the first module above 2 GB -- which is every module
# in this process.
psapi.EnumProcessModulesEx.argtypes = (wintypes.HANDLE, ctypes.POINTER(ctypes.c_void_p),
                                       wintypes.DWORD, ctypes.POINTER(wintypes.DWORD),
                                       wintypes.DWORD)
psapi.GetModuleFileNameExW.argtypes = (wintypes.HANDLE, ctypes.c_void_p,
                                       wintypes.LPWSTR, wintypes.DWORD)
psapi.GetModuleInformation.argtypes = (wintypes.HANDLE, ctypes.c_void_p,
                                       ctypes.POINTER(MODULEINFO), wintypes.DWORD)


def modules(handle) -> list[dict]:
    """Loaded modules with base and size, so an address can be named."""
    needed = wintypes.DWORD()
    array = (ctypes.c_void_p * 1024)()
    # The array itself, not byref(array): with argtypes declared as POINTER(c_void_p)
    # ctypes converts an array to a pointer to its first element but rejects a
    # pointer TO the array. Same trap as the SendInput call in forza_fast_scan.py.
    if not psapi.EnumProcessModulesEx(handle, array, ctypes.sizeof(array),
                                      ctypes.byref(needed), LIST_MODULES_ALL):
        return []
    count = min(len(array), needed.value // ctypes.sizeof(ctypes.c_void_p))
    found = []
    for index in range(count):
        base = array[index]
        if not base:
            continue
        name = ctypes.create_unicode_buffer(512)
        psapi.GetModuleFileNameExW(handle, ctypes.c_void_p(base), name, 512)
        info = MODULEINFO()
        if not psapi.GetModuleInformation(handle, ctypes.c_void_p(base),
                                          ctypes.byref(info), ctypes.sizeof(info)):
            continue
        found.append({"name": Path(name.value).name,
                      "base": int(info.lpBaseOfDll or 0),
                      "size": int(info.SizeOfImage)})
    return found


def name_address(address: int, module_list: list[dict], regions: list[tuple]) -> dict:
    """Describe an address: which module (with RVA) or which kind of memory."""
    for module in module_list:
        if module["base"] <= address < module["base"] + module["size"]:
            return {"kind": "image", "module": module["name"],
                    "rva": hex(address - module["base"])}
    for base, size, state in regions:
        if base <= address < base + size:
            kind = {MEM_IMAGE: "image", MEM_MAPPED: "mapped",
                    MEM_PRIVATE: "private"}.get(state, hex(state))
            return {"kind": kind, "region": hex(base), "region_size": size}
    return {"kind": "unknown"}


def scan_for_range(memory: ProcessMemory, low: int, high: int, *,
                   chunk: int = 32 << 20, limit: int = 8192) -> list[tuple[int, int]]:
    """Every aligned slot holding a value in [low, high]; returns (slot, value).

    The exact-value scan found nothing, which is itself informative: whatever holds
    the rows does not point at the first ROW, it points at the allocation -- the row
    array can start past a header, and a vector's `begin` is the element address
    only if the elements start at the beginning. So search a neighbourhood and let
    the offset distribution say where the real anchor is.

    numpy, because a range test cannot use bytes.find: 6 GB of uint64 comparisons is
    seconds vectorised and minutes in a Python loop.
    """
    import numpy

    hits: list[tuple[int, int]] = []
    for base, size in memory.regions(minimum_size=8):
        offset = 0
        while offset < size:
            take = min(chunk, size - offset)
            block = memory.read(base + offset, take)
            if not block or len(block) < 8:
                offset += take
                continue
            usable = len(block) - (len(block) % 8)
            values = numpy.frombuffer(block[:usable], dtype="<u8")
            found = numpy.nonzero((values >= low) & (values <= high))[0]
            for index in found:
                slot = base + offset + int(index) * 8
                hits.append((slot, int(values[index])))
                if len(hits) >= limit:
                    return hits
            offset += take
    return hits


def scan_for_value(memory: ProcessMemory, needle: int, *, chunk: int = 32 << 20,
                   limit: int = 4096) -> list[int]:
    """Every 8-byte-aligned slot holding `needle`.

    bytes.find over a raw chunk is C speed; alignment is checked afterwards because
    an unaligned hit is never a real pointer field.
    """
    packed = struct.pack("<Q", needle)
    hits: list[int] = []
    for base, size in memory.regions(minimum_size=8):
        offset = 0
        while offset < size:
            take = min(chunk, size - offset)
            block = memory.read(base + offset, take)
            if not block:
                offset += take
                continue
            start = 0
            while True:
                index = block.find(packed, start)
                if index < 0:
                    break
                address = base + offset + index
                if address % 8 == 0:
                    hits.append(address)
                    if len(hits) >= limit:
                        return hits
                start = index + 1
            offset += take
    return hits


def looks_like_rows(memory: ProcessMemory, address: int, span: int,
                    layout=None) -> tuple[bool, int]:
    """Does `address` hold a REAL row block, and what is its first rank?

    Counting rank-pair patterns is far too weak a test: two equal consecutive uint32
    occur by chance all over a busy heap, and a first run of this hunt duly reported
    40 "tracking" slots whose blocks had first ranks like 6,648,929. A block earns
    the name only if it looks like the array it claims to be -- rows one stride
    apart, ranks ascending by exactly one, and a lap time that could be a lap time.
    """
    block = memory.read(address, span)
    if not block:
        return False, 0
    offsets = rank_pair_offsets(block)
    if len(offsets) < 8:
        return False, 0
    stride = getattr(layout, "stride", 0)
    lap_at = getattr(layout, "lap_time", None)
    run = 1
    best = 1
    start = offsets[0]
    best_start = start
    for previous, current in zip(offsets, offsets[1:]):
        step_ok = (current - previous) == stride if stride else True
        rank_a = struct.unpack_from("<I", block, previous)[0]
        rank_b = struct.unpack_from("<I", block, current)[0]
        if step_ok and rank_b == rank_a + 1:
            run += 1
            if run > best:
                best, best_start = run, start
        else:
            run = 1
            start = current
    if best < 8:
        return False, 0
    first = struct.unpack_from("<I", block, best_start)[0]
    if not 1 <= first <= 5_000_000:
        return False, 0
    if lap_at is not None:
        laps_ok = 0
        for index in range(min(8, best)):
            at = best_start + index * stride + lap_at
            if at + 8 <= len(block):
                value = struct.unpack_from("<d", block, at)[0]
                if 1.0 <= value <= 86400.0:
                    laps_ok += 1
        if laps_ok < 6:
            return False, 0
    return True, first


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--process-name", default="forzahorizon6")
    parser.add_argument("--profile", type=Path)
    parser.add_argument("--watch-seconds", type=float, default=90.0)
    parser.add_argument("--poll-ms", type=float, default=120.0)
    parser.add_argument("--read-records", type=int, default=64)
    parser.add_argument("--max-candidates", type=int, default=4096)
    parser.add_argument("--slack", type=lambda v: int(v, 0), default=0x2000,
                        help="also accept pointers this far BEFORE the first row, "
                             "so a container pointing at the allocation is found")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args(argv)

    pid = resolve_pid(args.process_name)
    print(f"[chain] pid {pid}", flush=True)
    with ProcessMemory(pid) as memory:
        cached = load_profile(args.profile)
        if cached:
            addresses, layout = cached
        else:
            found = discover(memory)
            if not found:
                print("[chain] no row block found", flush=True)
                return 2
            addresses, layout = found
        pager = Pager(memory, addresses, layout, args.read_records, require_name=False)
        if not pager.read_rows():
            addresses = find_addresses(memory, layout, want_blocks=4,
                                       hint=addresses[0] if addresses else None)
            if not addresses:
                found = discover(memory)
                if not found:
                    print("[chain] no row block found", flush=True)
                    return 2
                addresses, layout = found
            pager = Pager(memory, addresses, layout, args.read_records,
                          require_name=False)

        span = layout.stride * args.read_records
        target = None
        for address in addresses:
            ok, first = looks_like_rows(memory, address, span, layout)
            if ok:
                target = address
                print(f"[chain] anchor block 0x{address:016x}, first rank {first}",
                      flush=True)
                break
        if target is None:
            print("[chain] the profile's addresses hold no rows right now", flush=True)
            return 2

        module_list = modules(memory.handle)
        exe = next((m for m in module_list
                    if m["name"].lower().startswith("forzahorizon6")), None)
        if exe:
            print(f"[chain] {exe['name']} at 0x{exe['base']:x} "
                  f"+{exe['size'] / (1 << 20):.1f} MB", flush=True)

        started = time.monotonic()
        print("[chain] phase 2: scanning for slots that hold the block address",
              flush=True)
        exact = scan_for_value(memory, target, limit=args.max_candidates)
        print(f"[chain] {len(exact)} exact holder(s) in "
              f"{time.monotonic() - started:.1f}s", flush=True)

        holders = list(exact)
        if not exact and args.slack:
            started = time.monotonic()
            print(f"[chain] no exact holder; widening to [-0x{args.slack:x}, +0x40] "
                  f"around the first row", flush=True)
            near = scan_for_range(memory, target - args.slack, target + 0x40,
                                  limit=args.max_candidates)
            offsets: dict[int, int] = {}
            for slot, value in near:
                offsets[target - value] = offsets.get(target - value, 0) + 1
            common = sorted(offsets.items(), key=lambda item: item[1], reverse=True)[:8]
            print(f"[chain] {len(near)} nearby pointer(s); most common distances "
                  f"before the first row: "
                  + ", ".join(f"0x{d:x}×{n}" for d, n in common), flush=True)
            holders = [slot for slot, _ in near]
        if not holders:
            print("[chain] nothing points at or near the block -- it is reached by a "
                  "computed offset rather than a stored pointer", flush=True)
            return 3

        # Phase 3: a holder earns trust by TRACKING. Scroll must be happening
        # elsewhere; each time a holder's value changes to another valid row block,
        # that is one point of evidence.
        print(f"[chain] phase 3: watching {len(holders)} slot(s) for "
              f"{args.watch_seconds:.0f}s while the list scrolls", flush=True)
        stats = {address: {"changes": 0, "valid": 0, "reads": 0, "blocks": set(),
                           "last": target, "ranks": set()}
                 for address in holders}
        deadline = time.monotonic() + args.watch_seconds
        while time.monotonic() < deadline:
            for address, state in stats.items():
                raw = memory.read(address, 8)
                if not raw or len(raw) < 8:
                    continue
                value = struct.unpack("<Q", raw)[0]
                state["reads"] += 1
                if value == state["last"]:
                    continue
                state["last"] = value
                state["changes"] += 1
                if value and 0x1000 < value < (1 << 48):
                    ok, first = looks_like_rows(memory, value, span, layout)
                    if ok:
                        state["valid"] += 1
                        state["blocks"].add(value)
                        state["ranks"].add(first)
            time.sleep(args.poll_ms / 1000.0)

        regions = [(base, size, MEM_PRIVATE) for base, size in memory.regions(minimum_size=8)]
        ranked = sorted(stats.items(),
                        key=lambda item: (item[1]["valid"], len(item[1]["blocks"])),
                        reverse=True)
        report = []
        for address, state in ranked[:40]:
            entry = {
                "holder": hex(address),
                "valid_hits": state["valid"],
                "changes": state["changes"],
                "distinct_blocks": len(state["blocks"]),
                "distinct_first_ranks": sorted(state["ranks"])[:8],
                "where": name_address(address, module_list, regions),
            }
            report.append(entry)

        keepers = [entry for entry in report if entry["valid_hits"] > 0]
        print(f"[chain] {len(keepers)} slot(s) tracked the block to another VALID "
              f"row block", flush=True)
        for entry in keepers[:12]:
            print(f"    {entry['holder']}  valid={entry['valid_hits']:<4} "
                  f"blocks={entry['distinct_blocks']:<3} "
                  f"first_ranks={entry['distinct_first_ranks']} {entry['where']}",
                  flush=True)
        if not keepers:
            print("[chain] no slot tracked. Either nothing scrolled during the "
                  "watch, or the block is addressed indirectly.", flush=True)

        # Phase 4: for heap holders, who points at them? One level is usually enough
        # to reach an object whose own address is stable, and from there a static.
        parents = {}
        for entry in keepers[:6]:
            holder = int(entry["holder"], 16)
            found = scan_for_value(memory, holder, limit=64)
            parents[entry["holder"]] = [
                {"address": hex(a), "where": name_address(a, module_list, regions)}
                for a in found[:16]
            ]
            print(f"[chain] {entry['holder']} is pointed at by {len(found)} slot(s)",
                  flush=True)

        payload = {
            "pid": pid,
            "anchor_block": hex(target),
            "modules": [m for m in module_list if "forza" in m["name"].lower()],
            "candidates": report,
            "parents": parents,
            "watch_seconds": args.watch_seconds,
        }
        if args.output:
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(json.dumps(payload, indent=2), encoding="utf-8")
            print(f"[chain] wrote {args.output}", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
