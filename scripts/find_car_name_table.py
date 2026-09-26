"""Locate a car-name string in the live Forza process and dump its context.

Recon step toward a standalone carId->name catalogue. The scoreboard rows carry
only a numeric carId; the display name ("Exige WTAC") is resolved by the game
from an in-memory table. This finds a KNOWN car name in memory and shows what
sits around each hit -- a nearby carId, a length prefix, a pointer table -- so
the table's structure can be worked out and then dumped wholesale.

Read-only: PROCESS_VM_READ only, no injection. Searches every committed readable
region (private, mapped, AND image), because a catalogue loaded from a data file
may live in mapped memory rather than the private heap.

    python find_car_name_table.py --process-name forzahorizon6 --needle "Exige WTAC"
"""

from __future__ import annotations

import argparse
import ctypes
import sys
from ctypes import wintypes

kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

PROCESS_QUERY_INFORMATION = 0x0400
PROCESS_VM_READ = 0x0010
MEM_COMMIT = 0x1000
PAGE_NOACCESS = 0x01
PAGE_GUARD = 0x100


class MEMORY_BASIC_INFORMATION(ctypes.Structure):
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


def resolve_pid(name: str) -> int:
    import subprocess

    completed = subprocess.run(
        ["powershell.exe", "-NoProfile", "-Command",
         f"Get-Process -Name '{name}' -ErrorAction Stop | "
         "Sort-Object StartTime -Descending | Select-Object -First 1 -ExpandProperty Id"],
        capture_output=True, text=True, check=False,
    )
    text = completed.stdout.strip()
    if not text.isdigit():
        raise RuntimeError(f"could not resolve pid: {completed.stderr.strip()}")
    return int(text)


def regions(handle: int):
    address = 0
    limit = 0x00007FFFFFFFFFFF
    info = MEMORY_BASIC_INFORMATION()
    while address < limit:
        if not kernel32.VirtualQueryEx(handle, ctypes.c_void_p(address),
                                       ctypes.byref(info), ctypes.sizeof(info)):
            break
        base = int(info.BaseAddress or 0)
        size = int(info.RegionSize)
        nxt = base + size
        if nxt <= address:
            break
        readable = (
            info.State == MEM_COMMIT
            and not (info.Protect & PAGE_NOACCESS)
            and not (info.Protect & PAGE_GUARD)
        )
        if readable and size:
            yield base, size, info.Type
        address = nxt


def read(handle: int, address: int, length: int) -> bytes:
    buffer = ctypes.create_string_buffer(length)
    actual = ctypes.c_size_t()
    ok = kernel32.ReadProcessMemory(handle, ctypes.c_void_p(address), buffer,
                                    length, ctypes.byref(actual))
    if not ok and actual.value == 0:
        return b""
    return buffer.raw[: actual.value]


def find_all(block: bytes, needle: bytes) -> list[int]:
    hits, start = [], 0
    while True:
        i = block.find(needle, start)
        if i < 0:
            break
        hits.append(i)
        start = i + 1
    return hits


def main() -> int:
    parser = argparse.ArgumentParser(description="Find a car-name string in the live process.")
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--pid", type=int)
    source.add_argument("--process-name")
    parser.add_argument("--needle", action="append", required=True,
                        help="car display name to locate; repeatable")
    parser.add_argument("--context", type=int, default=64,
                        help="bytes to dump on each side of a hit")
    parser.add_argument("--max-hits", type=int, default=40)
    args = parser.parse_args()

    if sys.platform != "win32":
        raise RuntimeError("scanning a live process requires Windows")

    pid = args.pid or resolve_pid(args.process_name)
    handle = kernel32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, pid)
    if not handle:
        raise ctypes.WinError(ctypes.get_last_error())

    encodings = {"ascii": lambda s: s.encode("ascii"),
                 "utf16le": lambda s: s.encode("utf-16-le")}
    type_name = {0x20000: "PRIVATE", 0x40000: "MAPPED", 0x1000000: "IMAGE"}

    try:
        total = 0
        for base, size, mtype in regions(handle):
            block = read(handle, base, min(size, 64 * 1024 * 1024))
            if not block:
                continue
            for enc_name, encode in encodings.items():
                for needle in args.needle:
                    nb = encode(needle)
                    for off in find_all(block, nb):
                        total += 1
                        if total > args.max_hits:
                            print(f"... stopped at {args.max_hits} hits")
                            return 0
                        addr = base + off
                        lo = max(0, off - args.context)
                        ctx = block[lo: off + len(nb) + args.context]
                        print(f"\n[{total}] '{needle}' as {enc_name} @ 0x{addr:016x} "
                              f"({type_name.get(mtype, hex(mtype))})")
                        print("  context hex:", ctx.hex())
                        printable = "".join(chr(b) if 32 <= b < 127 else "." for b in ctx)
                        print("  context asc:", printable)
        print(f"\ntotal hits: {total}")
        return 0 if total else 2
    finally:
        kernel32.CloseHandle(handle)


if __name__ == "__main__":
    raise SystemExit(main())
