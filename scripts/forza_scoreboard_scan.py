"""Find and decode ScoreboardRow blocks in a live Forza process, offsets and all.

`capture_scoreboard_memory.py` hard-codes stride `0x2d0` and every field offset
from build 6.382.893.0. The game patched to 24584928 on 2026-08-18, so those
constants are no longer trustworthy, and when they are wrong the old scanner
reports "no row block found" rather than saying the layout moved.

This scans for the structure instead:

1. sweep committed private memory for positions holding a duplicated u32
   (`rank`, `rank`) pair, vectorised with numpy so a multi-gigabyte heap is one
   fast pass rather than a Python loop
2. group those into runs whose ranks increase by 1 with constant spacing, which
   yields the record stride
3. hand the surrounding buffer to `forza_scoreboard_layout.discover_layout` to
   locate each field by column-wise validation
4. decode the rows with the layout it derived, and remember both the layout and
   the buffer addresses in a build profile so later reads are cheap

Read-only: opens the target with PROCESS_QUERY_INFORMATION|PROCESS_VM_READ and
never writes to the process or injects anything.

    python forza_scoreboard_scan.py --process-name forzahorizon6 --discover \
        --profile build_profile.json --output rows.json
"""

from __future__ import annotations

import argparse
import ctypes
import json
import struct
import sys
import time
from ctypes import wintypes
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterator

sys.path.insert(0, str(Path(__file__).resolve().parent))

from forza_scoreboard_layout import (  # noqa: E402
    CAR_CLASS_ID_DELTA,
    CAR_ID_DELTA,
    CAR_PERFORMANCE_INDEX_DELTA,
    PERFORMANCE_CLASS_IDS,
    Layout,
    discover_layout,
    read_sso_string,
    read_system_time,
)

PROCESS_QUERY_INFORMATION = 0x0400
PROCESS_VM_READ = 0x0010
MEM_COMMIT = 0x1000
MEM_PRIVATE = 0x20000
PAGE_NOACCESS = 0x01
PAGE_GUARD = 0x100

MAXIMUM_RANK = 10_000_000
DEFAULT_MINIMUM_RECORDS = 8


class MemoryBasicInformation(ctypes.Structure):
    _fields_ = [
        ("BaseAddress", ctypes.c_void_p),
        ("AllocationBase", ctypes.c_void_p),
        ("AllocationProtect", wintypes.DWORD),
        ("PartitionId", wintypes.WORD),
        ("RegionSize", ctypes.c_size_t),
        ("State", wintypes.DWORD),
        ("Protect", wintypes.DWORD),
        ("Type", wintypes.DWORD),
    ]


if sys.platform == "win32":
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel32.OpenProcess.restype = wintypes.HANDLE
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel32.CloseHandle.restype = wintypes.BOOL
    kernel32.VirtualQueryEx.argtypes = [
        wintypes.HANDLE,
        ctypes.c_void_p,
        ctypes.POINTER(MemoryBasicInformation),
        ctypes.c_size_t,
    ]
    kernel32.VirtualQueryEx.restype = ctypes.c_size_t
    kernel32.ReadProcessMemory.argtypes = [
        wintypes.HANDLE,
        ctypes.c_void_p,
        ctypes.c_void_p,
        ctypes.c_size_t,
        ctypes.POINTER(ctypes.c_size_t),
    ]
    kernel32.ReadProcessMemory.restype = wintypes.BOOL


class ProcessMemory:
    """A read-only handle to another process's memory."""

    def __init__(self, pid: int) -> None:
        self.pid = pid
        self.handle = kernel32.OpenProcess(
            PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, pid
        )
        if not self.handle:
            raise ctypes.WinError(ctypes.get_last_error())

    def close(self) -> None:
        if self.handle:
            kernel32.CloseHandle(self.handle)
            self.handle = None

    def __enter__(self) -> "ProcessMemory":
        return self

    def __exit__(self, *_: object) -> None:
        self.close()

    def read(self, address: int, length: int) -> bytes:
        buffer = ctypes.create_string_buffer(length)
        actual = ctypes.c_size_t()
        ok = kernel32.ReadProcessMemory(
            self.handle,
            ctypes.c_void_p(address),
            buffer,
            length,
            ctypes.byref(actual),
        )
        if not ok and actual.value == 0:
            return b""
        return buffer.raw[: actual.value]

    def regions(self, *, minimum_size: int) -> Iterator[tuple[int, int]]:
        address = 0
        limit = 0x00007FFFFFFFFFFF
        info = MemoryBasicInformation()
        while address < limit:
            queried = kernel32.VirtualQueryEx(
                self.handle,
                ctypes.c_void_p(address),
                ctypes.byref(info),
                ctypes.sizeof(info),
            )
            if not queried:
                break
            base = int(info.BaseAddress or 0)
            size = int(info.RegionSize)
            following = base + size
            if following <= address:
                break
            usable = (
                info.State == MEM_COMMIT
                and info.Type == MEM_PRIVATE
                and not (info.Protect & PAGE_NOACCESS)
                and not (info.Protect & PAGE_GUARD)
            )
            if usable and size >= minimum_size:
                yield base, size
            address = following


def rank_pair_offsets(block: bytes) -> list[int]:
    """Byte offsets in `block` holding two equal, plausible u32 rank values.

    Vectorised: a 4 GB heap cannot be swept with a Python-level loop, but numpy
    turns this into a couple of passes over a uint32 view.
    """
    import numpy

    usable = len(block) - (len(block) % 4)
    if usable < 8:
        return []
    words = numpy.frombuffer(block[:usable], dtype="<u4")
    if words.size < 2:
        return []
    left = words[:-1]
    right = words[1:]
    hits = numpy.nonzero(
        (left == right) & (left >= 1) & (left <= MAXIMUM_RANK)
    )[0]
    return (hits * 4).tolist()


def runs_from_offsets(
    block: bytes,
    offsets: list[int],
    *,
    minimum_records: int,
) -> list[tuple[int, list[int]]]:
    """Group rank-pair offsets into (stride, offsets) runs of consecutive ranks."""
    if not offsets:
        return []
    rank_at = {offset: struct.unpack_from("<I", block, offset)[0] for offset in offsets}
    by_rank: dict[int, list[int]] = {}
    for offset, rank in rank_at.items():
        by_rank.setdefault(rank, []).append(offset)

    runs: list[tuple[int, list[int]]] = []
    claimed: set[int] = set()
    for offset in offsets:
        if offset in claimed:
            continue
        rank = rank_at[offset]
        for successor in by_rank.get(rank + 1, []):
            stride = successor - offset
            if stride <= 0 or stride % 8 != 0:
                continue
            found = [offset]
            walk = offset
            expected = rank
            while True:
                walk += stride
                expected += 1
                if walk + 8 > len(block):
                    break
                if rank_at.get(walk) != expected:
                    break
                found.append(walk)
            if len(found) >= minimum_records:
                claimed.update(found)
                runs.append((stride, found))
                break
    runs.sort(key=lambda item: len(item[1]), reverse=True)
    return runs


def decode_rows(
    block: bytes,
    rank_offsets: list[int],
    layout: Layout,
    *,
    base_address: int,
) -> list[dict[str, Any]]:
    """Decode records at known rank positions using a discovered layout."""
    rows: list[dict[str, Any]] = []
    for offset in rank_offsets:
        rank = struct.unpack_from("<I", block, offset)[0]
        row: dict[str, Any] = {
            "rank": rank,
            "record_address": f"0x{base_address + offset - 0:016x}",
        }
        if layout.xuid is not None and offset + layout.xuid + 8 <= len(block):
            row["xuid"] = str(struct.unpack_from("<Q", block, offset + layout.xuid)[0])
        if layout.gamertag is not None:
            row["gamertag"] = read_sso_string(block, offset + layout.gamertag) or ""
        if layout.lap_time is not None and offset + layout.lap_time + 8 <= len(block):
            row["lap_time_seconds"] = struct.unpack_from(
                "<d", block, offset + layout.lap_time
            )[0]
        if layout.submitted_time is not None:
            stamp = read_system_time(block, offset + layout.submitted_time)
            row["submitted_time_utc"] = stamp.isoformat() if stamp else None
        anchor = layout.car_drive_type_id
        if anchor is not None and offset + anchor + 8 <= len(block):
            row["car_drive_type_id"] = struct.unpack_from("<I", block, offset + anchor)[0]
            row["car_powertrain_id"] = struct.unpack_from(
                "<I", block, offset + anchor + 4
            )[0]
            # Read at descriptor-derived deltas, then validate rather than trust.
            car_id_at = offset + anchor + CAR_ID_DELTA
            if car_id_at >= 0:
                row["car_id"] = struct.unpack_from("<H", block, car_id_at)[0]
                class_id = block[offset + anchor + CAR_CLASS_ID_DELTA]
                if class_id in PERFORMANCE_CLASS_IDS:
                    row["car_class_id"] = class_id
                    row["performance_class"] = PERFORMANCE_CLASS_IDS[class_id]
                performance_index = struct.unpack_from(
                    "<f", block, offset + anchor + CAR_PERFORMANCE_INDEX_DELTA
                )[0]
                if 0.0 <= performance_index <= 1000.0:
                    row["car_performance_index_raw"] = performance_index
        if layout.flags is not None and layout.flag_count:
            flags = block[
                offset + layout.flags : offset + layout.flags + layout.flag_count
            ]
            names = [
                "used_stm",
                "used_tcs",
                "used_abs",
                "used_friction_assist",
                "used_auto_brake",
                "used_auto_shifting",
                "used_clutch",
                "used_super_easy_assist",
                "is_clean",
                "has_ghost_file",
            ]
            for index, name in enumerate(names):
                if index < len(flags):
                    row[name] = bool(flags[index])
        rows.append(row)
    return rows


def scan_process(
    memory: ProcessMemory,
    *,
    minimum_records: int,
    block_size: int,
    addresses: list[int] | None = None,
    stride_hint: int | None = None,
) -> tuple[list[dict[str, Any]], dict[str, Any]]:
    """Locate row blocks and decode them, deriving the layout from the data."""
    started = time.monotonic()
    findings: list[dict[str, Any]] = []
    scanned_bytes = 0
    layout_report: dict[str, Any] = {}

    if addresses:
        # A known-address read needs enough bytes behind the block for the
        # column tests to reach the far fields of the last record.
        span = (stride_hint or 0x400) * (minimum_records + 8)
        candidates = [(address, span) for address in addresses]
    else:
        candidates = list(memory.regions(minimum_size=0x400 * minimum_records))

    for base, size in candidates:
        offset = 0
        while offset < size:
            want = min(block_size, size - offset)
            block = memory.read(base + offset, want)
            if not block:
                offset += want
                continue
            scanned_bytes += len(block)
            offsets = rank_pair_offsets(block)
            if len(offsets) >= minimum_records:
                for stride, run in runs_from_offsets(
                    block, offsets, minimum_records=minimum_records
                ):
                    # Hand discover_layout only the bytes around the run, never
                    # the whole block. Its rank search is a Python loop over every
                    # 4 bytes, so passing a 16 MB block costs ~4M iterations per
                    # block and turned a scan into 19 minutes of CPU. The window
                    # needs a full stride of slack past the last record so the
                    # column tests can reach that record's far fields.
                    window_start = max(0, run[0] - stride)
                    window_end = min(len(block), run[-1] + 2 * stride)
                    window = block[window_start:window_end]
                    local_run = [position - window_start for position in run]

                    layout, report = discover_layout(
                        window, minimum_records=minimum_records
                    )
                    if layout is None or not layout.required_fields_present():
                        continue
                    layout_report = report
                    rows = decode_rows(
                        window,
                        local_run,
                        layout,
                        base_address=base + offset + window_start,
                    )
                    rows = [row for row in rows if row.get("gamertag")]
                    if len(rows) >= minimum_records:
                        findings.append(
                            {
                                "block_address": f"0x{base + offset + window_start:016x}",
                                "record_address": rows[0]["record_address"],
                                "stride": stride,
                                "rows": rows,
                                "layout": layout,
                            }
                        )
                    break
            offset += want

    report = {
        "scanned_bytes": scanned_bytes,
        "elapsed_seconds": round(time.monotonic() - started, 3),
        "blocks_found": len(findings),
        "layout_discovery": layout_report,
    }
    return findings, report


def resolve_pid(process_name: str) -> int:
    """The newest process with this name.

    Newest rather than first: a crashed launcher can leave an older process of
    the same name behind, and scanning that one finds no rows at all.
    """
    import subprocess

    completed = subprocess.run(
        [
            "powershell.exe",
            "-NoProfile",
            "-Command",
            (
                f"Get-Process -Name '{process_name}' -ErrorAction Stop | "
                "Sort-Object StartTime -Descending | "
                "Select-Object -First 1 -ExpandProperty Id"
            ),
        ],
        capture_output=True,
        text=True,
        check=False,
    )
    text = completed.stdout.strip()
    if not text.isdigit():
        raise RuntimeError(f"Could not resolve a pid: {completed.stderr.strip()}")
    return int(text)


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Find and decode ScoreboardRow blocks without hard-coded offsets."
    )
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--pid", type=int)
    source.add_argument("--process-name")
    parser.add_argument(
        "--profile",
        type=Path,
        help="Build profile to read known buffer addresses from and write back to.",
    )
    parser.add_argument(
        "--discover",
        action="store_true",
        help="Sweep all committed private memory instead of only profile addresses.",
    )
    parser.add_argument("--minimum-records", type=int, default=DEFAULT_MINIMUM_RECORDS)
    parser.add_argument("--block-size-mb", type=int, default=16)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()

    if sys.platform != "win32":
        raise RuntimeError("Scanning a live process requires Windows.")

    pid = args.pid
    if pid is None:
        pid = resolve_pid(args.process_name)

    profile: dict[str, Any] = {}
    if args.profile and args.profile.exists():
        profile = json.loads(args.profile.read_text(encoding="utf-8-sig"))

    known = [
        int(value, 16)
        for value in profile.get("block_addresses", [])
        if isinstance(value, str) and value.startswith("0x")
    ]

    with ProcessMemory(pid) as memory:
        findings, report = scan_process(
            memory,
            minimum_records=args.minimum_records,
            block_size=max(1, args.block_size_mb) * 1024 * 1024,
            addresses=None if args.discover or not known else known,
            stride_hint=profile.get("layout", {}).get("stride"),
        )

    layout = findings[0]["layout"] if findings else None
    all_rows: dict[int, dict[str, Any]] = {}
    for finding in findings:
        for row in finding["rows"]:
            all_rows[int(row["rank"])] = row
    rows = [all_rows[rank] for rank in sorted(all_rows)]

    result = {
        "captured_at": datetime.now(timezone.utc).isoformat(),
        "process_id": pid,
        "scan": report,
        "blocks": [
            {
                "block_address": finding["block_address"],
                "record_address": finding["record_address"],
                "stride": finding["stride"],
                "row_count": len(finding["rows"]),
                "minimum_rank": min(int(r["rank"]) for r in finding["rows"]),
                "maximum_rank": max(int(r["rank"]) for r in finding["rows"]),
            }
            for finding in findings
        ],
        "row_count": len(rows),
        "minimum_rank": min(all_rows) if all_rows else None,
        "maximum_rank": max(all_rows) if all_rows else None,
        "rows": rows,
    }
    if layout is not None:
        result["layout"] = {
            "stride": layout.stride,
            "stride_hex": f"0x{layout.stride:x}",
            "rank_relative_offsets": {
                "xuid": layout.xuid,
                "gamertag": layout.gamertag,
                "lap_time": layout.lap_time,
                "submitted_time": layout.submitted_time,
                "car_drive_type_id": layout.car_drive_type_id,
                "car_powertrain_id": layout.car_powertrain_id,
                "flags": layout.flags,
            },
            "flag_count": layout.flag_count,
            "matches_build_6_382_893_0": layout.matches_known_build(),
            "notes": layout.notes,
        }

    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")

    if args.profile and findings and layout is not None:
        args.profile.parent.mkdir(parents=True, exist_ok=True)
        args.profile.write_text(
            json.dumps(
                {
                    "updated_at": result["captured_at"],
                    "process_id": pid,
                    "layout": result["layout"],
                    "block_addresses": [
                        finding["block_address"] for finding in findings
                    ],
                },
                indent=2,
            )
            + "\n",
            encoding="utf-8",
        )

    summary = {key: value for key, value in result.items() if key != "rows"}
    print(json.dumps(summary, indent=2))
    return 0 if rows else 2


if __name__ == "__main__":
    raise SystemExit(main())
