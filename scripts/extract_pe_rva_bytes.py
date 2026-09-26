from __future__ import annotations

import argparse
import struct
from pathlib import Path


def read_u16(data: bytes, offset: int) -> int:
    return struct.unpack_from("<H", data, offset)[0]


def read_u32(data: bytes, offset: int) -> int:
    return struct.unpack_from("<I", data, offset)[0]


def rva_to_file_offset(headers: bytes, rva: int) -> int:
    pe_offset = read_u32(headers, 0x3C)
    if headers[pe_offset : pe_offset + 4] != b"PE\0\0":
        raise ValueError("Not a PE image")

    coff = pe_offset + 4
    section_count = read_u16(headers, coff + 2)
    optional_size = read_u16(headers, coff + 16)
    section_table = coff + 20 + optional_size

    for index in range(section_count):
        entry = section_table + index * 40
        virtual_size = read_u32(headers, entry + 8)
        virtual_address = read_u32(headers, entry + 12)
        raw_size = read_u32(headers, entry + 16)
        raw_offset = read_u32(headers, entry + 20)
        mapped_size = max(virtual_size, raw_size)
        if virtual_address <= rva < virtual_address + mapped_size:
            return raw_offset + (rva - virtual_address)

    raise ValueError(f"RVA 0x{rva:x} is not inside a PE section")


def main() -> int:
    parser = argparse.ArgumentParser(description="Extract a small PE byte range by RVA.")
    parser.add_argument("--exe", required=True, type=Path)
    parser.add_argument("--rva", required=True, type=lambda value: int(value, 0))
    parser.add_argument("--size", required=True, type=int)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()

    if args.size <= 0:
        raise ValueError("size must be positive")

    with args.exe.open("rb") as handle:
        headers = handle.read(1024 * 1024)
        file_offset = rva_to_file_offset(headers, args.rva)
        handle.seek(file_offset)
        data = handle.read(args.size)

    if len(data) != args.size:
        raise ValueError(f"Expected {args.size} bytes, read {len(data)}")

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes(data)
    print(
        f"{args.output.resolve()} rva=0x{args.rva:x} "
        f"file_offset=0x{file_offset:x} size={len(data)}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
