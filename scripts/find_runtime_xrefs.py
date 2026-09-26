"""Find code references to a string or address inside a large runtime image.

`find_pe_string_xrefs.py` disassembles every function and checks each operand.
That is fine for a normal binary, but the decrypted forzahorizon6 runtime image
carries 414,368 functions over 96.6 MB of code, which takes about an hour in
Python.

This inverts the problem. For a RIP-relative instruction at address A of length L
referencing target T, the encoded displacement is `D = T - (A + L)`. So instead
of decoding instructions to discover D, solve for the positions where the four
bytes in the image already equal the D that would be needed to reach T from
there. That is a vectorised comparison over the whole image, and only the handful
of positions that survive get disassembled to confirm what the instruction
actually is.

Also locates absolute 8-byte pointers to the target, which is how vtables,
RTTI and the field-descriptor tables reference things.

    python find_runtime_xrefs.py --image forza_runtime.exe \
        --string carPerformanceIndex --string totalRowCount --output xrefs.json
"""

from __future__ import annotations

import argparse
import bisect
import json
import struct
from pathlib import Path
from typing import Any

import numpy
import pefile
from capstone import CS_ARCH_X86, CS_MODE_64, Cs

# How far back from a displacement field an instruction can start. x86-64
# instructions max out at 15 bytes, and the displacement is followed by at most
# an immediate.
MAXIMUM_INSTRUCTION_LENGTH = 15


def load_functions(pe: pefile.PE) -> tuple[list[int], list[tuple[int, int]]]:
    functions: list[tuple[int, int]] = []
    for entry in getattr(pe, "DIRECTORY_ENTRY_EXCEPTION", []):
        begin = int(entry.struct.BeginAddress)
        end = int(entry.struct.EndAddress)
        if begin < end:
            functions.append((begin, end))
    functions.sort()
    return [item[0] for item in functions], functions


def containing_function(
    rva: int, starts: list[int], functions: list[tuple[int, int]]
) -> tuple[int, int] | None:
    index = bisect.bisect_right(starts, rva) - 1
    if index < 0:
        return None
    begin, end = functions[index]
    return (begin, end) if begin <= rva < end else None


def find_string_vas(
    image: bytes, image_base: int, value: str
) -> list[tuple[int, str]]:
    """Every VA where this string appears, as (va, encoding)."""
    found: list[tuple[int, str]] = []
    for encoding, label in (("ascii", "ascii"), ("utf-16-le", "utf16")):
        needle = value.encode(encoding)
        start = 0
        while True:
            position = image.find(needle, start)
            if position < 0:
                break
            found.append((image_base + position, label))
            start = position + 1
    return found


def rip_relative_hits(
    words: numpy.ndarray, image_base: int, target_va: int, image_length: int
) -> list[int]:
    """Offsets whose 4 bytes are the displacement needed to reach target_va.

    Checked at all four byte alignments so nothing is missed, since x86
    displacements are not aligned.
    """
    hits: list[int] = []
    for shift in range(4):
        usable = (image_length - shift) // 4 * 4
        if usable <= 0:
            continue
        view = words[shift : shift + usable].view("<i4")
        offsets = shift + 4 * numpy.arange(view.size, dtype=numpy.int64)
        # disp == target_va - image_base - (offset + 4)
        needed = target_va - image_base - offsets - 4
        # Comparing int64 needed against int32 view: restrict to representable
        # values first, or numpy will wrap and produce false positives.
        in_range = (needed >= -(2**31)) & (needed < 2**31)
        candidates = numpy.nonzero(in_range)[0]
        if candidates.size == 0:
            continue
        matched = candidates[view[candidates] == needed[candidates].astype("<i4")]
        hits.extend(offsets[matched].tolist())
    return sorted(hits)


def absolute_pointer_hits(image: bytes, target_va: int) -> list[int]:
    """Offsets holding an absolute 8-byte pointer to target_va."""
    needle = struct.pack("<Q", target_va)
    hits: list[int] = []
    start = 0
    while True:
        position = image.find(needle, start)
        if position < 0:
            break
        hits.append(position)
        start = position + 1
    return hits


def describe_instruction(
    image: bytes,
    image_base: int,
    displacement_offset: int,
    target_va: int,
    disassembler: Cs,
) -> dict[str, Any] | None:
    """Decode the instruction whose displacement field sits at this offset."""
    window_start = max(0, displacement_offset - MAXIMUM_INSTRUCTION_LENGTH)
    window = image[window_start : displacement_offset + 8]
    for start in range(len(window) - 8):
        address = image_base + window_start + start
        decoded = list(disassembler.disasm(window[start:], address, count=1))
        if not decoded:
            continue
        instruction = decoded[0]
        end = window_start + start + instruction.size
        # The displacement must fall inside this instruction, and the
        # instruction must actually resolve to the target.
        if not (window_start + start < displacement_offset < end):
            continue
        resolved = instruction.address + instruction.size
        for operand in instruction.operands:
            if operand.type == 3 and operand.mem.base == 41:  # X86_OP_MEM, RIP
                if resolved + operand.mem.disp == target_va:
                    return {
                        "instruction_rva": f"0x{window_start + start:08x}",
                        "instruction_va": f"0x{address:016x}",
                        "mnemonic": instruction.mnemonic,
                        "operands": instruction.op_str,
                        "size": instruction.size,
                    }
    return None


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Find code references to strings or addresses in a runtime image."
    )
    parser.add_argument("--image", required=True, type=Path)
    parser.add_argument("--string", action="append", dest="strings", default=[])
    parser.add_argument(
        "--target-va",
        action="append",
        dest="target_vas",
        default=[],
        type=lambda value: int(value, 0),
    )
    parser.add_argument(
        "--skip-pointers",
        action="store_true",
        help="Skip the absolute 8-byte pointer search.",
    )
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()

    if not args.strings and not args.target_vas:
        parser.error("Pass at least one --string or --target-va.")

    image = args.image.read_bytes()
    pe = pefile.PE(str(args.image), fast_load=False)
    image_base = int(pe.OPTIONAL_HEADER.ImageBase)
    starts, functions = load_functions(pe)
    words = numpy.frombuffer(image, dtype=numpy.uint8)
    disassembler = Cs(CS_ARCH_X86, CS_MODE_64)
    disassembler.detail = True

    targets: list[dict[str, Any]] = []
    for value in args.strings:
        for va, encoding in find_string_vas(image, image_base, value):
            targets.append(
                {
                    "kind": "string",
                    "label": value,
                    "encoding": encoding,
                    "target_va": va,
                }
            )
    for va in args.target_vas:
        targets.append(
            {"kind": "address", "label": f"0x{va:016x}", "target_va": va}
        )

    results: list[dict[str, Any]] = []
    for target in targets:
        target_va = int(target["target_va"])
        code_hits: list[dict[str, Any]] = []
        for offset in rip_relative_hits(words, image_base, target_va, len(image)):
            described = describe_instruction(
                image, image_base, offset, target_va, disassembler
            )
            if described is None:
                continue
            instruction_rva = int(described["instruction_rva"], 16)
            function = containing_function(instruction_rva, starts, functions)
            described["function_begin_rva"] = (
                f"0x{function[0]:08x}" if function else ""
            )
            described["function_end_rva"] = f"0x{function[1]:08x}" if function else ""
            code_hits.append(described)

        pointer_hits: list[str] = []
        if not args.skip_pointers:
            pointer_hits = [
                f"0x{offset:08x}" for offset in absolute_pointer_hits(image, target_va)
            ]

        results.append(
            {
                **{key: value for key, value in target.items() if key != "target_va"},
                "target_va": f"0x{target_va:016x}",
                "target_rva": f"0x{target_va - image_base:08x}",
                "code_reference_count": len(code_hits),
                "code_references": code_hits,
                "absolute_pointer_count": len(pointer_hits),
                "absolute_pointer_rvas": pointer_hits[:64],
            }
        )
        print(
            f"{target['label'][:44]:46s} code refs {len(code_hits):3d}"
            f"   abs pointers {len(pointer_hits):3d}"
        )
        for hit in code_hits[:6]:
            print(
                f"    {hit['instruction_rva']}  {hit['mnemonic']} {hit['operands']}"
                f"   fn {hit['function_begin_rva']}"
            )

    report = {
        "image": str(args.image.resolve()),
        "image_base": f"0x{image_base:016x}",
        "function_count": len(functions),
        "targets": results,
    }
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        print(f"report: {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
