"""Measure how fast another process's memory can actually be read.

After vectorising the candidate scan, a full sweep is no longer limited by
searching but by reading: 6 GB in 42.7 s on the B board, about 140 MB/s. This
tells us which part of that is real ReadProcessMemory cost and which part is
ours -- chunk size (one syscall per chunk) and the two extra copies that
read_memory makes per chunk by allocating a fresh buffer and then taking .raw.

Read-only; it never writes to the target process.

    python scripts/bench_process_read.py --pid 1234 --budget-gb 2
"""
from __future__ import annotations

import argparse
import ctypes
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from capture_scoreboard_memory import (  # noqa: E402
    PROCESS_QUERY_INFORMATION,
    PROCESS_VM_READ,
    kernel32,
    read_memory,
    readable_regions,
)


def read_reusable(process: int, address: int, length: int, buffer, view) -> int:
    """Read into a buffer we already own, and hand back a view, not a copy."""
    actual = ctypes.c_size_t()
    ok = kernel32.ReadProcessMemory(
        process, ctypes.c_void_p(address), buffer, length, ctypes.byref(actual)
    )
    if not ok and actual.value == 0:
        return 0
    _ = view[: actual.value]      # what a caller would scan; no copy
    return actual.value


def sweep(process: int, regions, chunk_size: int, budget: int, *, reusable: bool) -> tuple[int, float]:
    buffer = ctypes.create_string_buffer(chunk_size) if reusable else None
    view = memoryview(buffer).cast("B") if reusable else None
    total = 0
    started = time.perf_counter()
    for base, size in regions:
        offset = 0
        while offset < size:
            length = min(chunk_size, size - offset)
            if reusable:
                got = read_reusable(process, base + offset, length, buffer, view)
            else:
                got = len(read_memory(process, base + offset, length))
            total += got
            offset += length
            if total >= budget:
                return total, time.perf_counter() - started
    return total, time.perf_counter() - started


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--pid", type=int, required=True)
    parser.add_argument("--budget-gb", type=float, default=2.0)
    parser.add_argument("--chunk-sizes-mb", default="16,64,256")
    args = parser.parse_args()

    handle = kernel32.OpenProcess(
        PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, args.pid
    )
    if not handle:
        print(f"could not open pid {args.pid}", file=sys.stderr)
        return 1

    regions = list(readable_regions(handle))
    total_gb = sum(size for _, size in regions) / (1024 ** 3)
    print(f"pid {args.pid}: {len(regions)} readable regions, {total_gb:.2f} GB committed")
    budget = int(args.budget_gb * (1024 ** 3))

    print(f"{'chunk':>8} {'variant':<22} {'read':>9} {'time':>8} {'rate':>11} {'6 GB in':>10}")
    for mb in [int(x) for x in args.chunk_sizes_mb.split(",")]:
        for reusable in (False, True):
            got, seconds = sweep(
                handle, regions, mb * 1024 * 1024, budget, reusable=reusable
            )
            gb = got / (1024 ** 3)
            rate = gb / seconds if seconds else 0.0
            label = "reusable buffer" if reusable else "read_memory (current)"
            print(
                f"{mb:>6} MB {label:<22} {gb:>7.2f} GB {seconds:>7.2f}s "
                f"{rate*1024:>8.0f} MB/s {6/rate if rate else 0:>8.1f} s"
            )
    kernel32.CloseHandle(handle)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
