from __future__ import annotations

import argparse
import bisect
from pathlib import Path

import pefile
from capstone import CS_ARCH_X86, CS_MODE_64, Cs


def parse_int(value: str) -> int:
    return int(value, 0)


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Disassemble the x64 runtime function containing each requested PE RVA."
    )
    parser.add_argument("--exe", required=True, type=Path)
    parser.add_argument("--rva", action="append", type=parse_int, required=True)
    parser.add_argument("--fallback-bytes", type=int, default=192)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()

    pe = pefile.PE(str(args.exe.resolve()), fast_load=False)
    image_base = int(pe.OPTIONAL_HEADER.ImageBase)
    functions = sorted(
        (
            int(entry.struct.BeginAddress),
            int(entry.struct.EndAddress),
        )
        for entry in getattr(pe, "DIRECTORY_ENTRY_EXCEPTION", [])
        if int(entry.struct.BeginAddress) < int(entry.struct.EndAddress)
    )
    starts = [item[0] for item in functions]
    disassembler = Cs(CS_ARCH_X86, CS_MODE_64)
    lines: list[str] = []

    for requested_rva in args.rva:
        index = bisect.bisect_right(starts, requested_rva) - 1
        if index < 0 or not (
            functions[index][0] <= requested_rva < functions[index][1]
        ):
            begin = requested_rva
            end = requested_rva + args.fallback_bytes
            lines.append(
                f"; requested RVA 0x{requested_rva:08x}, no runtime function; "
                f"raw fallback 0x{begin:08x}-0x{end:08x}"
            )
            code = pe.get_data(begin, end - begin)
            for instruction in disassembler.disasm(code, image_base + begin):
                lines.append(
                    f"> {instruction.address - image_base:08x}  "
                    f"{instruction.mnemonic:<8} {instruction.op_str}"
                )
                if instruction.mnemonic == "ret":
                    break
            lines.append("")
            continue
        begin, end = functions[index]
        lines.append(
            f"; requested RVA 0x{requested_rva:08x}, function 0x{begin:08x}-0x{end:08x}"
        )
        code = pe.get_data(begin, end - begin)
        for instruction in disassembler.disasm(code, image_base + begin):
            marker = ">" if instruction.address - image_base == requested_rva else " "
            lines.append(
                f"{marker} {instruction.address - image_base:08x}  "
                f"{instruction.mnemonic:<8} {instruction.op_str}"
            )
        lines.append("")

    text = "\n".join(lines).rstrip() + "\n"
    if args.output:
        output = args.output.resolve()
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(text, encoding="utf-8")
        print(output)
    else:
        print(text, end="")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
