"""Find who points at the leaderboard's current row page.

The scan spends its time SEARCHING for the 50-row page in ~6 GB of process memory,
once per page. It does not have to. The page is a heap block, so something holds a
pointer to it -- most likely a std::vector's `begin` field inside the leaderboard
object. If that holder address is the same from page to page, the search collapses
into a single 8-byte read and the whole exact/near/sweep ladder becomes unnecessary.

Usage, run once per page and compare the holder sets:

    python find_page_pointer.py --pid 1234 --target 0x000001c56955c760

Read-only: it opens the process with PROCESS_VM_READ only and never writes, so it
is safe to run against a game another scan is already reading.
"""

from __future__ import annotations

import argparse
import ctypes
import ctypes.wintypes as wintypes
import json
import struct
import sys
from pathlib import Path

PROCESS_QUERY_INFORMATION = 0x0400
PROCESS_VM_READ = 0x0010
MEM_COMMIT = 0x1000
PAGE_NOACCESS = 0x01
PAGE_GUARD = 0x100

kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)


class MEMORY_BASIC_INFORMATION64(ctypes.Structure):
    _fields_ = [
        ("BaseAddress", ctypes.c_ulonglong),
        ("AllocationBase", ctypes.c_ulonglong),
        ("AllocationProtect", wintypes.DWORD),
        ("__alignment1", wintypes.DWORD),
        ("RegionSize", ctypes.c_ulonglong),
        ("State", wintypes.DWORD),
        ("Protect", wintypes.DWORD),
        ("Type", wintypes.DWORD),
        ("__alignment2", wintypes.DWORD),
    ]


def readable_regions(handle):
    address = 0
    info = MEMORY_BASIC_INFORMATION64()
    while kernel32.VirtualQueryEx(handle, ctypes.c_void_p(address),
                                  ctypes.byref(info), ctypes.sizeof(info)):
        size = int(info.RegionSize)
        if size <= 0:
            break
        if (info.State == MEM_COMMIT
                and not (info.Protect & PAGE_NOACCESS)
                and not (info.Protect & PAGE_GUARD)):
            yield int(info.BaseAddress), size
        address = int(info.BaseAddress) + size
        if address > 0x7FFFFFFFFFFF:
            break


def read_chunk(handle, address, size):
    buffer = ctypes.create_string_buffer(size)
    read = ctypes.c_size_t(0)
    ok = kernel32.ReadProcessMemory(handle, ctypes.c_void_p(address), buffer,
                                    ctypes.c_size_t(size), ctypes.byref(read))
    if not ok or read.value == 0:
        return None
    return buffer.raw[:read.value]


def find_holders(pid: int, target: int, chunk_mb: int, limit: int) -> list[int]:
    handle = kernel32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, pid)
    if not handle:
        raise OSError("OpenProcess failed for pid %d (error %d)" % (pid, ctypes.get_last_error()))
    needle = struct.pack("<Q", target)
    holders: list[int] = []
    chunk = chunk_mb * 1024 * 1024
    try:
        for base, size in readable_regions(handle):
            offset = 0
            while offset < size:
                # Overlap by 7 bytes so a pointer straddling a chunk boundary is
                # not silently missed -- a false negative here would read as
                # "nothing points at the page", which is the wrong conclusion.
                take = min(chunk, size - offset)
                read_len = min(take + 7, size - offset)
                data = read_chunk(handle, base + offset, read_len)
                if data:
                    start = 0
                    while True:
                        hit = data.find(needle, start)
                        if hit < 0:
                            break
                        # Only 8-byte aligned hits can be a real pointer field.
                        if (base + offset + hit) % 8 == 0:
                            holders.append(base + offset + hit)
                            if len(holders) >= limit:
                                return holders
                        start = hit + 1
                offset += take
    finally:
        kernel32.CloseHandle(handle)
    return holders


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pid", type=int, required=True)
    parser.add_argument("--target", type=lambda v: int(v, 0), required=True,
                        help="address of the current page (a row's source_address)")
    parser.add_argument("--chunk-mb", type=int, default=64)
    parser.add_argument("--limit", type=int, default=64,
                        help="stop after this many holders")
    parser.add_argument("--output-json", type=Path)
    args = parser.parse_args(argv)

    holders = find_holders(args.pid, args.target, args.chunk_mb, args.limit)
    result = {
        "pid": args.pid,
        "target": "0x%016x" % args.target,
        "holder_count": len(holders),
        "holders": ["0x%016x" % h for h in holders],
    }
    print(json.dumps(result, indent=2))
    if args.output_json:
        args.output_json.write_text(json.dumps(result, indent=2), encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
