from __future__ import annotations

import argparse
import bisect
import json
import struct
from pathlib import Path
from typing import Any

import pefile
from capstone import CS_ARCH_X86, CS_MODE_64, Cs
from capstone.x86 import X86_OP_MEM, X86_REG_RIP


def find_occurrences(data: bytes, value: str) -> list[tuple[int, str]]:
    results: list[tuple[int, str]] = []
    for encoding, label in (("ascii", "ascii"), ("utf-16-le", "utf16")):
        needle = value.encode(encoding)
        offset = 0
        while True:
            offset = data.find(needle, offset)
            if offset < 0:
                break
            results.append((offset, label))
            offset += max(1, len(needle))
    return results


def runtime_functions(pe: pefile.PE) -> tuple[list[int], list[tuple[int, int]]]:
    functions: list[tuple[int, int]] = []
    for entry in getattr(pe, "DIRECTORY_ENTRY_EXCEPTION", []):
        begin = int(entry.struct.BeginAddress)
        end = int(entry.struct.EndAddress)
        if begin < end:
            functions.append((begin, end))
    functions.sort()
    return [item[0] for item in functions], functions


def containing_function(
    rva: int,
    starts: list[int],
    functions: list[tuple[int, int]],
) -> tuple[int, int] | None:
    index = bisect.bisect_right(starts, rva) - 1
    if index < 0:
        return None
    begin, end = functions[index]
    return (begin, end) if begin <= rva < end else None


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Find x64 RIP-relative code references to strings in a PE image."
    )
    parser.add_argument("--exe", required=True, type=Path)
    parser.add_argument("--string", action="append", dest="strings", required=True)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()

    exe_path = args.exe.resolve()
    image = exe_path.read_bytes()
    pe = pefile.PE(str(exe_path), fast_load=False)
    image_base = int(pe.OPTIONAL_HEADER.ImageBase)
    starts, functions = runtime_functions(pe)

    targets: dict[int, list[dict[str, Any]]] = {}
    strings_report: list[dict[str, Any]] = []
    for value in args.strings:
        occurrences = []
        for offset, encoding in find_occurrences(image, value):
            try:
                rva = int(pe.get_rva_from_offset(offset))
            except pefile.PEFormatError:
                continue
            record = {
                "value": value,
                "encoding": encoding,
                "file_offset": offset,
                "rva": rva,
                "va": image_base + rva,
            }
            occurrences.append(record)
            direct_target = dict(record)
            direct_target.update(
                {
                    "target_kind": "direct",
                    "target_va": image_base + rva,
                    "string_va": image_base + rva,
                }
            )
            targets.setdefault(image_base + rva, []).append(direct_target)

            pointer_patterns = (
                (struct.pack("<Q", image_base + rva), "pointer_va64"),
                (struct.pack("<I", rva), "pointer_rva32"),
            )
            for pointer_bytes, target_kind in pointer_patterns:
                pointer_offset = 0
                while True:
                    pointer_offset = image.find(pointer_bytes, pointer_offset)
                    if pointer_offset < 0:
                        break
                    try:
                        pointer_rva = int(pe.get_rva_from_offset(pointer_offset))
                    except pefile.PEFormatError:
                        pointer_offset += 1
                        continue
                    pointer_target = dict(record)
                    pointer_target.update(
                        {
                            "target_kind": target_kind,
                            "target_va": image_base + pointer_rva,
                            "target_file_offset": pointer_offset,
                            "string_va": image_base + rva,
                        }
                    )
                    targets.setdefault(image_base + pointer_rva, []).append(
                        pointer_target
                    )
                    pointer_offset += len(pointer_bytes)
        strings_report.append({"value": value, "occurrences": occurrences})

    disassembler = Cs(CS_ARCH_X86, CS_MODE_64)
    disassembler.detail = True
    xrefs: list[dict[str, Any]] = []

    if not functions:
        raise RuntimeError("The PE image has no x64 runtime-function table.")

    for function_begin, function_end in functions:
        code = pe.get_data(function_begin, function_end - function_begin)
        for instruction in disassembler.disasm(code, image_base + function_begin):
            for operand in instruction.operands:
                if operand.type != X86_OP_MEM or operand.mem.base != X86_REG_RIP:
                    continue
                target_va = instruction.address + instruction.size + operand.mem.disp
                matches = targets.get(target_va)
                if not matches:
                    continue
                instruction_rva = instruction.address - image_base
                function = containing_function(instruction_rva, starts, functions)
                for match in matches:
                    xrefs.append(
                        {
                            "string": match["value"],
                            "string_encoding": match["encoding"],
                            "string_va": f"0x{match['string_va']:016x}",
                            "referenced_target_va": f"0x{target_va:016x}",
                            "target_kind": match["target_kind"],
                            "instruction_va": f"0x{instruction.address:016x}",
                            "instruction_rva": f"0x{instruction_rva:08x}",
                            "mnemonic": instruction.mnemonic,
                            "operands": instruction.op_str,
                            "function_begin_rva": (
                                f"0x{function[0]:08x}" if function else ""
                            ),
                            "function_end_rva": (
                                f"0x{function[1]:08x}" if function else ""
                            ),
                        }
                    )

    report = {
        "exe": str(exe_path),
        "image_base": f"0x{image_base:016x}",
        "strings": strings_report,
        "xrefs": xrefs,
    }
    text = json.dumps(report, indent=2)
    if args.output:
        output_path = args.output.resolve()
        output_path.parent.mkdir(parents=True, exist_ok=True)
        output_path.write_text(text + "\n", encoding="utf-8")
        print(output_path)
    else:
        print(text)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
