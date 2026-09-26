from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any

import pefile
from capstone import CS_ARCH_X86, CS_MODE_64, Cs
from capstone.x86 import X86_OP_IMM, X86_OP_MEM, X86_REG_RIP


def parse_int(value: str) -> int:
    return int(value, 0)


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Find x64 code references to one or more PE RVAs."
    )
    parser.add_argument("--exe", required=True, type=Path)
    parser.add_argument("--rva", action="append", required=True, type=parse_int)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()

    exe = args.exe.resolve()
    pe = pefile.PE(str(exe), fast_load=False)
    image_base = int(pe.OPTIONAL_HEADER.ImageBase)
    targets = {image_base + rva: rva for rva in args.rva}
    disassembler = Cs(CS_ARCH_X86, CS_MODE_64)
    disassembler.detail = True
    xrefs: list[dict[str, Any]] = []

    for entry in getattr(pe, "DIRECTORY_ENTRY_EXCEPTION", []):
        function_begin = int(entry.struct.BeginAddress)
        function_end = int(entry.struct.EndAddress)
        if function_begin >= function_end:
            continue
        code = pe.get_data(function_begin, function_end - function_begin)
        for instruction in disassembler.disasm(code, image_base + function_begin):
            referenced: set[int] = set()
            for operand in instruction.operands:
                if operand.type == X86_OP_MEM and operand.mem.base == X86_REG_RIP:
                    referenced.add(
                        instruction.address + instruction.size + operand.mem.disp
                    )
                elif operand.type == X86_OP_IMM:
                    referenced.add(int(operand.imm))
            for target_va in referenced.intersection(targets):
                xrefs.append(
                    {
                        "target_rva": f"0x{targets[target_va]:08x}",
                        "instruction_rva": (
                            f"0x{instruction.address - image_base:08x}"
                        ),
                        "mnemonic": instruction.mnemonic,
                        "operands": instruction.op_str,
                        "function_begin_rva": f"0x{function_begin:08x}",
                        "function_end_rva": f"0x{function_end:08x}",
                    }
                )

    report = {
        "exe": str(exe),
        "image_base": f"0x{image_base:016x}",
        "targets": [f"0x{rva:08x}" for rva in args.rva],
        "xrefs": xrefs,
    }
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
