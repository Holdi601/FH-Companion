from __future__ import annotations

import argparse
import json
from pathlib import Path

from capstone import CS_ARCH_X86, CS_MODE_64, Cs


FUNCTIONS = {
    "encrypt_stage_1": (0x5EEA0A0, 0x5EEA8DB),
    "encrypt_stage_2": (0x5EEA8E0, 0x5EEAC18),
    "decrypt_stage_1": (0x5EEAC20, 0x5EEB2B6),
    "decrypt_stage_2": (0x5EEB2C0, 0x5EEBAD9),
}


def read_snapshots(trace: Path) -> list[tuple[int, int, bytes]]:
    snapshots: list[tuple[int, int, bytes]] = []
    with trace.open("r", encoding="utf-8") as handle:
        for line in handle:
            if not line.strip():
                continue
            record = json.loads(line)
            payload = record.get("message", {}).get("payload", {})
            if payload.get("type") != "code-snapshot":
                continue
            snapshots.append(
                (
                    int(payload["index"]),
                    int(payload["elapsedMs"]),
                    bytes.fromhex(record["data_hex"]),
                )
            )
    return snapshots


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Extract and score runtime snapshots of FH6 encryption workers."
    )
    parser.add_argument("trace", type=Path)
    parser.add_argument("--base-rva", type=lambda value: int(value, 0), default=0x5EEA000)
    parser.add_argument("--output-dir", required=True, type=Path)
    args = parser.parse_args()

    args.output_dir.mkdir(parents=True, exist_ok=True)
    disassembler = Cs(CS_ARCH_X86, CS_MODE_64)
    report: dict[str, object] = {
        "trace": str(args.trace.resolve()),
        "base_rva": f"0x{args.base_rva:x}",
        "snapshots": [],
        "best_functions": {},
    }

    snapshots = read_snapshots(args.trace)
    best: dict[str, tuple[int, int, int, list[str]]] = {}

    for index, elapsed_ms, data in snapshots:
        binary_path = args.output_dir / f"workers_{index:02d}_{elapsed_ms:04d}ms.bin"
        binary_path.write_bytes(data)
        snapshot_record: dict[str, object] = {
            "index": index,
            "elapsed_ms": elapsed_ms,
            "binary": str(binary_path.resolve()),
            "functions": {},
        }

        for name, (start_rva, end_rva) in FUNCTIONS.items():
            start = start_rva - args.base_rva
            end = end_rva - args.base_rva
            instructions = list(
                disassembler.disasm(data[start:end], start_rva)
            )
            coverage = sum(instruction.size for instruction in instructions)
            lines = [
                f"{instruction.address:08x}  "
                f"{instruction.mnemonic:<8} {instruction.op_str}"
                for instruction in instructions
            ]
            asm_path = args.output_dir / f"{name}_{index:02d}_{elapsed_ms:04d}ms.asm"
            asm_path.write_text("\n".join(lines) + "\n", encoding="utf-8")
            snapshot_record["functions"][name] = {
                "coverage": coverage,
                "expected": end_rva - start_rva,
                "instruction_count": len(instructions),
                "assembly": str(asm_path.resolve()),
            }
            current = best.get(name)
            if current is None or coverage > current[0]:
                best[name] = (coverage, index, elapsed_ms, lines)

        report["snapshots"].append(snapshot_record)

    for name, (coverage, index, elapsed_ms, lines) in best.items():
        best_path = args.output_dir / f"{name}_best.asm"
        best_path.write_text("\n".join(lines) + "\n", encoding="utf-8")
        report["best_functions"][name] = {
            "snapshot_index": index,
            "elapsed_ms": elapsed_ms,
            "coverage": coverage,
            "expected": FUNCTIONS[name][1] - FUNCTIONS[name][0],
            "assembly": str(best_path.resolve()),
        }

    report_path = args.output_dir / "report.json"
    report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(report_path.resolve())
    for name, item in report["best_functions"].items():
        print(
            f"{name}: snapshot={item['snapshot_index']} "
            f"elapsed={item['elapsed_ms']}ms "
            f"coverage={item['coverage']}/{item['expected']}"
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
