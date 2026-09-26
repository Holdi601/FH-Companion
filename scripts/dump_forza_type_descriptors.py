"""Recover Forza's service data model from the reflection tables in the binary.

The game describes every serialised type with a table of 0x60-byte field
descriptors. Layout of one descriptor, verified against 300 captured rows:

    +0x00  type code        3 = int32, 7 = float32, 12/19 = nested object,
                            16 = SYSTEMTIME, 17 = bool, 18 = versioned id
    +0x08  pointer to the field name
    +0x10  field offset within the record
    +0x18..+0x38  validation bounds, 0x7fffffff where unbounded
    +0x40  field size in bytes

A type is introduced by a header descriptor whose offset word is the sentinel
0x7fffffff. The header does not sit next to its fields: it points at them, with
the field array pointer at +0x40 and the field count at +0x48, and its name
pointer at +0x58 gives the type name.

Getting that linkage wrong silently attributes each field list to the
alphabetically neighbouring type, which is why this reads the pointer rather
than assuming the fields follow the header.

So the wire model for every service type is readable as data, with no
disassembly and no hooking, and it is rediscovered per build instead of
hard-coded.

    python dump_forza_type_descriptors.py --image forza_runtime.exe \
        --output type_descriptors.json --grep Scoreboard
"""

from __future__ import annotations

import argparse
import json
import re
import struct
from pathlib import Path
from typing import Any

import pefile

DESCRIPTOR_STRIDE = 0x60
HEADER_SENTINEL = 0x7FFFFFFF

# Offsets within one field descriptor.
TYPE_CODE_OFFSET = 0x00
NAME_POINTER_OFFSET = 0x08
FIELD_OFFSET_OFFSET = 0x10
FIELD_SIZE_OFFSET = 0x40

# Offsets within a type header descriptor.
HEADER_FIELDS_POINTER_OFFSET = 0x40
HEADER_FIELD_COUNT_OFFSET = 0x48
HEADER_NAME_POINTER_OFFSET = 0x58

IDENTIFIER = re.compile(r"^[A-Za-z_][A-Za-z0-9_]{1,62}$")

MAXIMUM_FIELD_OFFSET = 1 << 20
MAXIMUM_FIELD_COUNT = 512

TYPE_CODE_NAMES = {
    0: "uint8",
    1: "uint16",
    3: "int32",
    7: "float32",
    12: "object",
    16: "systemtime",
    17: "bool",
    18: "versioned_id",
    19: "object",
}


def read_c_string(image: bytes, rva: int, limit: int = 96) -> str | None:
    if not 0 <= rva < len(image):
        return None
    end = image.find(b"\0", rva, rva + limit)
    if end < 0:
        return None
    try:
        text = image[rva:end].decode("ascii")
    except UnicodeDecodeError:
        return None
    return text or None


class DescriptorImage:
    def __init__(self, path: Path) -> None:
        self.image = path.read_bytes()
        self.pe = pefile.PE(str(path), fast_load=True)
        self.base = int(self.pe.OPTIONAL_HEADER.ImageBase)

    def word(self, rva: int) -> int | None:
        if not 0 <= rva or rva + 8 > len(self.image):
            return None
        return struct.unpack_from("<Q", self.image, rva)[0]

    def pointer_target(self, rva: int) -> int | None:
        """Resolve a stored absolute pointer to an RVA, if it lands in-image."""
        value = self.word(rva)
        if value is None or not self.base < value < self.base + len(self.image):
            return None
        return value - self.base

    def name_at(self, rva: int) -> str | None:
        target = self.pointer_target(rva)
        if target is None:
            return None
        text = read_c_string(self.image, target)
        if text is None or not IDENTIFIER.match(text):
            return None
        return text

    def field(self, rva: int) -> dict[str, Any] | None:
        name = self.name_at(rva + NAME_POINTER_OFFSET)
        if name is None:
            return None
        offset = self.word(rva + FIELD_OFFSET_OFFSET)
        type_code = self.word(rva + TYPE_CODE_OFFSET)
        size = self.word(rva + FIELD_SIZE_OFFSET)
        if offset is None or type_code is None:
            return None
        if offset == HEADER_SENTINEL or offset > MAXIMUM_FIELD_OFFSET:
            return None
        return {
            "name": name,
            "offset": offset,
            "size": size,
            "type_code": type_code,
            "type": TYPE_CODE_NAMES.get(type_code, f"code_{type_code}"),
        }

    def type_at_header(self, header_rva: int) -> dict[str, Any] | None:
        if self.word(header_rva) != HEADER_SENTINEL:
            return None
        type_name = self.name_at(header_rva + HEADER_NAME_POINTER_OFFSET)
        if type_name is None:
            return None
        fields_rva = self.pointer_target(header_rva + HEADER_FIELDS_POINTER_OFFSET)
        count = self.word(header_rva + HEADER_FIELD_COUNT_OFFSET)
        if fields_rva is None or count is None or not 0 < count <= MAXIMUM_FIELD_COUNT:
            return None
        fields: list[dict[str, Any]] = []
        for index in range(count):
            field = self.field(fields_rva + index * DESCRIPTOR_STRIDE)
            if field is None:
                break
            fields.append(field)
        if not fields:
            return None
        record_size = max(
            (f["offset"] + (f["size"] or 0)) for f in fields
        )
        return {
            "type_name": type_name,
            "header_rva": f"0x{header_rva:08x}",
            "fields_rva": f"0x{fields_rva:08x}",
            "declared_field_count": count,
            "recovered_field_count": len(fields),
            "minimum_record_size": record_size,
            "fields": fields,
        }


def find_types(image: DescriptorImage) -> list[dict[str, Any]]:
    sentinel = struct.pack("<Q", HEADER_SENTINEL)
    data = image.image
    found: list[dict[str, Any]] = []
    seen: set[str] = set()
    position = 0
    while True:
        position = data.find(sentinel, position)
        if position < 0:
            break
        candidate = position
        position += 8
        if candidate % 8 != 0:
            continue
        described = image.type_at_header(candidate)
        if described is None:
            continue
        key = f"{described['type_name']}@{described['fields_rva']}"
        if key in seen:
            continue
        seen.add(key)
        found.append(described)
    found.sort(key=lambda item: item["type_name"])
    return found


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Recover Forza service type descriptors from a PE image."
    )
    parser.add_argument("--image", required=True, type=Path)
    parser.add_argument(
        "--grep",
        action="append",
        default=[],
        help="Only print types whose type or field names match this substring.",
    )
    parser.add_argument(
        "--memory-delta",
        type=int,
        default=0,
        help="Add this to each offset when printing, to show in-memory offsets.",
    )
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()

    image = DescriptorImage(args.image)
    types = find_types(image)

    report = {
        "image": str(args.image.resolve()),
        "image_base": f"0x{image.base:016x}",
        "type_count": len(types),
        "types": types,
    }
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")

    print(f"types recovered: {len(types)}")
    shown = types
    if args.grep:
        needles = [needle.lower() for needle in args.grep]
        shown = [
            item
            for item in types
            if any(
                needle in item["type_name"].lower()
                or any(needle in field["name"].lower() for field in item["fields"])
                for needle in needles
            )
        ]
        print(f"matching --grep: {len(shown)}")
    for item in shown:
        suffix = ""
        if item["recovered_field_count"] != item["declared_field_count"]:
            suffix = (
                f"  [recovered {item['recovered_field_count']}"
                f" of {item['declared_field_count']}]"
            )
        print(
            f"\n{item['type_name']}  ({item['recovered_field_count']} fields,"
            f" >= {item['minimum_record_size']} bytes, fields at"
            f" {item['fields_rva']}){suffix}"
        )
        for field in item["fields"]:
            offset = field["offset"] + args.memory_delta
            print(
                f"  +{offset:<6} {field['type']:<13} size {str(field['size']):<5}"
                f" {field['name']}"
            )
    if args.output:
        print(f"\nreport: {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
