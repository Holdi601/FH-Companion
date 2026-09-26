from __future__ import annotations

import argparse
import ctypes
import json
import re
import struct
from pathlib import Path
from typing import Any

from capture_scoreboard_memory import (
    PROCESS_QUERY_INFORMATION,
    PROCESS_VM_READ,
    kernel32,
    read_memory,
)


def printable_strings(data: bytes) -> list[dict[str, Any]]:
    found: list[dict[str, Any]] = []
    for match in re.finditer(rb"[ -~]{4,}", data):
        found.append(
            {
                "offset": match.start(),
                "encoding": "ascii",
                "text": match.group().decode("ascii"),
            }
        )
    for match in re.finditer(rb"(?:[ -~]\x00){4,}", data):
        found.append(
            {
                "offset": match.start(),
                "encoding": "utf-16le",
                "text": match.group().decode("utf-16le"),
            }
        )
    return sorted(found, key=lambda item: (item["offset"], item["encoding"]))


def plausible_pointers(data: bytes, base_address: int) -> list[dict[str, Any]]:
    pointers: list[dict[str, Any]] = []
    for offset in range(0, len(data) - 7, 8):
        value = struct.unpack_from("<Q", data, offset)[0]
        if 0x10000000000 <= value < 0x800000000000:
            pointers.append(
                {
                    "offset": offset,
                    "address": f"0x{value:016x}",
                    "field_address": f"0x{base_address + offset:016x}",
                }
            )
    return pointers


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--pid", required=True, type=int)
    parser.add_argument("--row-json", required=True, type=Path)
    parser.add_argument("--rank", required=True, type=int)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--bytes", type=int, default=1024)
    args = parser.parse_args()

    report = json.loads(args.row_json.read_text(encoding="utf-8-sig"))
    row = next(
        (item for item in report.get("rows", []) if int(item["rank"]) == args.rank),
        None,
    )
    if row is None:
        raise RuntimeError(f"Rank {args.rank} is not present in {args.row_json}")
    raw = bytes.fromhex(row["raw_row_hex"])

    process = kernel32.OpenProcess(
        PROCESS_QUERY_INFORMATION | PROCESS_VM_READ,
        False,
        args.pid,
    )
    if not process:
        raise ctypes.WinError(ctypes.get_last_error())

    objects: list[dict[str, Any]] = []
    vectors: list[dict[str, Any]] = []
    seen: set[int] = set()
    try:
        for begin_offset, end_offset, capacity_offset in (
            (0x128, 0x130, 0x138),
            (0x140, 0x148, 0x150),
        ):
            begin = struct.unpack_from("<Q", raw, begin_offset)[0]
            end = struct.unpack_from("<Q", raw, end_offset)[0]
            capacity = struct.unpack_from("<Q", raw, capacity_offset)[0]
            length = end - begin if end >= begin else 0
            data = (
                read_memory(process, begin, min(length, 4096))
                if 0 < length <= 4096
                else b""
            )
            vectors.append(
                {
                    "row_offset": f"0x{begin_offset:x}",
                    "begin": f"0x{begin:016x}",
                    "end": f"0x{end:016x}",
                    "capacity": f"0x{capacity:016x}",
                    "length": length,
                    "hex": data.hex(),
                    "uint32": [
                        struct.unpack_from("<I", data, offset)[0]
                        for offset in range(0, len(data) - 3, 4)
                    ],
                    "strings": printable_strings(data),
                }
            )
        for row_offset in range(0, len(raw) - 7, 8):
            pointer = struct.unpack_from("<Q", raw, row_offset)[0]
            if not (0x10000000000 <= pointer < 0x800000000000):
                continue
            if pointer in seen:
                continue
            seen.add(pointer)
            start = max(0, pointer - 0x100)
            data = read_memory(process, start, args.bytes)
            objects.append(
                {
                    "row_offset": f"0x{row_offset:x}",
                    "pointer": f"0x{pointer:016x}",
                    "read_start": f"0x{start:016x}",
                    "read_length": len(data),
                    "strings": printable_strings(data),
                    "pointers": plausible_pointers(data, start),
                }
            )
    finally:
        kernel32.CloseHandle(process)

    result = {
        "process_id": args.pid,
        "rank": args.rank,
        "gamertag": row["gamertag"],
        "source_address": row["source_address"],
        "vectors": vectors,
        "objects": objects,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"objects": len(objects), "output": str(args.output)}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
