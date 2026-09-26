from __future__ import annotations

import argparse
import json
import struct
from pathlib import Path
from typing import Any

import pefile


def find_all(data: bytes, needle: bytes) -> list[int]:
    offsets: list[int] = []
    start = 0
    while True:
        offset = data.find(needle, start)
        if offset < 0:
            return offsets
        offsets.append(offset)
        start = offset + 1


def is_executable_rva(pe: pefile.PE, rva: int) -> bool:
    section = pe.get_section_by_rva(rva)
    if section is None:
        return False
    return bool(int(section.Characteristics) & 0x20000000)


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Resolve MSVC x64 RTTI type names to complete object locators and vftables."
    )
    parser.add_argument("--exe", required=True, type=Path)
    parser.add_argument("--type-name", action="append", required=True)
    parser.add_argument("--max-methods", type=int, default=64)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()

    exe = args.exe.resolve()
    image = exe.read_bytes()
    pe = pefile.PE(str(exe), fast_load=False)
    image_base = int(pe.OPTIONAL_HEADER.ImageBase)
    report: dict[str, Any] = {
        "exe": str(exe),
        "image_base": f"0x{image_base:016x}",
        "types": [],
    }

    for type_name in args.type_name:
        type_records: list[dict[str, Any]] = []
        for name_offset in find_all(image, type_name.encode("ascii")):
            descriptor_offset = name_offset - 16
            if descriptor_offset < 0:
                continue
            descriptor_rva = int(pe.get_rva_from_offset(descriptor_offset))
            locator_records: list[dict[str, Any]] = []

            for type_rva_offset in find_all(image, struct.pack("<I", descriptor_rva)):
                locator_offset = type_rva_offset - 12
                if locator_offset < 0 or locator_offset + 24 > len(image):
                    continue
                signature, object_offset, cd_offset, type_rva, class_rva, self_rva = (
                    struct.unpack_from("<IIIIII", image, locator_offset)
                )
                try:
                    locator_rva = int(pe.get_rva_from_offset(locator_offset))
                except pefile.PEFormatError:
                    continue
                if signature != 1 or type_rva != descriptor_rva or self_rva != locator_rva:
                    continue

                locator_va = image_base + locator_rva
                vftables: list[dict[str, Any]] = []
                for pointer_offset in find_all(image, struct.pack("<Q", locator_va)):
                    vftable_offset = pointer_offset + 8
                    try:
                        vftable_rva = int(pe.get_rva_from_offset(vftable_offset))
                    except pefile.PEFormatError:
                        continue

                    methods: list[dict[str, Any]] = []
                    for index in range(args.max_methods):
                        entry_offset = vftable_offset + index * 8
                        if entry_offset + 8 > len(image):
                            break
                        method_va = struct.unpack_from("<Q", image, entry_offset)[0]
                        method_rva = method_va - image_base
                        if method_va < image_base or not is_executable_rva(pe, method_rva):
                            break
                        methods.append(
                            {
                                "index": index,
                                "va": f"0x{method_va:016x}",
                                "rva": f"0x{method_rva:08x}",
                            }
                        )

                    if methods:
                        vftables.append(
                            {
                                "locator_pointer_file_offset": pointer_offset,
                                "rva": f"0x{vftable_rva:08x}",
                                "va": f"0x{image_base + vftable_rva:016x}",
                                "methods": methods,
                            }
                        )

                locator_records.append(
                    {
                        "file_offset": locator_offset,
                        "rva": f"0x{locator_rva:08x}",
                        "va": f"0x{locator_va:016x}",
                        "object_offset": object_offset,
                        "cd_offset": cd_offset,
                        "class_descriptor_rva": f"0x{class_rva:08x}",
                        "vftables": vftables,
                    }
                )

            type_records.append(
                {
                    "name_file_offset": name_offset,
                    "descriptor_file_offset": descriptor_offset,
                    "descriptor_rva": f"0x{descriptor_rva:08x}",
                    "descriptor_va": f"0x{image_base + descriptor_rva:016x}",
                    "locators": locator_records,
                }
            )

        report["types"].append({"name": type_name, "occurrences": type_records})

    text = json.dumps(report, indent=2)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(text + "\n", encoding="utf-8")
        print(args.output.resolve())
    else:
        print(text)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
