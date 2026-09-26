"""Map which pages of a module are decrypted only at runtime.

A section-level comparison is too coarse to act on: forzahorizon6.exe's `.text`
comes out 32% identical to disk with both sides averaging ~6.5 bits/byte, which
tells you encryption is region-specific but not where. This walks the image page
by page instead and classifies each one, producing the map needed to aim a
disassembler or a hook at the right RVA.

Two modes:

`--disk-exe` compares a runtime dump against the shipped file. Pages that differ
and whose disk copy has markedly higher entropy are packed regions that the
loader or the packer stub decrypted.

`--baseline` compares two runtime dumps of the same build taken at different
times. If the packer decrypts lazily, a dump taken while a Rivals leaderboard is
open contains decrypted code that a title-screen dump does not, and the
difference between them is a short list of candidate RVAs for the scoreboard and
transport path. That is a far stronger signal than searching a 180 MB image.

Both dumps must come from `dump_forza_runtime_image.py`, which lays the image out
so file offset == RVA.
"""

from __future__ import annotations

import argparse
import collections
import json
import math
from pathlib import Path
from typing import Any

import pefile

PAGE_SIZE = 0x1000
IMAGE_SCN_MEM_EXECUTE = 0x20000000

# A page of x86-64 code sits well below this; encrypted or compressed data sits
# above it. Chosen with headroom on both sides of the observed values: real code
# pages in this binary measure ~6.5 and the packed page measured 7.815.
HIGH_ENTROPY = 7.2

# Below this share of matching bytes a page is a different page, not a patched
# one. Import thunk fixups and relocations touch a handful of bytes.
REWRITTEN_RATIO = 0.90


def entropy(data: bytes) -> float:
    if not data:
        return 0.0
    counts = collections.Counter(data)
    total = len(data)
    return -sum((n / total) * math.log2(n / total) for n in counts.values())


def is_blank(data: bytes) -> bool:
    return not data.strip(b"\0")


def classify(
    current: bytes,
    other: bytes,
    *,
    mode: str,
) -> tuple[str, float, float, float]:
    """Return (classification, identical_ratio, current_entropy, other_entropy)."""
    identical = sum(1 for a, b in zip(current, other) if a == b)
    ratio = identical / len(current) if current else 1.0
    current_entropy = entropy(current)
    other_entropy = entropy(other)

    if ratio == 1.0:
        return "identical", ratio, current_entropy, other_entropy
    if is_blank(other) and not is_blank(current):
        return "materialised", ratio, current_entropy, other_entropy
    if is_blank(current) and not is_blank(other):
        return "released", ratio, current_entropy, other_entropy
    if ratio < REWRITTEN_RATIO:
        if mode == "disk" and other_entropy - current_entropy > 0.5 and other_entropy >= HIGH_ENTROPY:
            return "decrypted", ratio, current_entropy, other_entropy
        if mode == "baseline":
            return "changed", ratio, current_entropy, other_entropy
        return "rewritten", ratio, current_entropy, other_entropy
    return "patched", ratio, current_entropy, other_entropy


def section_of(pe: pefile.PE, rva: int) -> tuple[str, bool]:
    for section in pe.sections:
        start = int(section.VirtualAddress)
        end = start + max(int(section.Misc_VirtualSize), int(section.SizeOfRawData))
        if start <= rva < end:
            return (
                section.Name.rstrip(b"\0").decode("ascii", "replace"),
                bool(section.Characteristics & IMAGE_SCN_MEM_EXECUTE),
            )
    return "", False


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Classify each page of a runtime image dump against a reference."
    )
    parser.add_argument("--runtime-image", required=True, type=Path)
    reference = parser.add_mutually_exclusive_group(required=True)
    reference.add_argument(
        "--disk-exe", type=Path, help="The shipped executable for the same build."
    )
    reference.add_argument(
        "--baseline",
        type=Path,
        help="An earlier runtime dump of the same build, to find lazily decrypted code.",
    )
    parser.add_argument(
        "--executable-only",
        action="store_true",
        help="Only report pages inside executable sections.",
    )
    parser.add_argument("--output", type=Path)
    parser.add_argument(
        "--max-listed",
        type=int,
        default=400,
        help="Cap on individual page records written to the report.",
    )
    args = parser.parse_args()

    runtime = args.runtime_image.read_bytes()
    pe = pefile.PE(str(args.runtime_image), fast_load=True)

    mode = "disk" if args.disk_exe else "baseline"
    if args.disk_exe:
        reference_pe = pefile.PE(str(args.disk_exe), fast_load=True)
        disk = args.disk_exe.read_bytes()

        def reference_page(rva: int) -> bytes | None:
            """Map an RVA to the shipped file's bytes for that page."""
            for section in reference_pe.sections:
                start = int(section.VirtualAddress)
                raw_size = int(section.SizeOfRawData)
                if start <= rva < start + raw_size:
                    offset = int(section.PointerToRawData) + (rva - start)
                    page = disk[offset : offset + PAGE_SIZE]
                    return page if len(page) == PAGE_SIZE else None
            return None

    else:
        baseline = args.baseline.read_bytes()

        def reference_page(rva: int) -> bytes | None:
            page = baseline[rva : rva + PAGE_SIZE]
            return page if len(page) == PAGE_SIZE else None

    counts: collections.Counter[str] = collections.Counter()
    by_section: dict[str, collections.Counter[str]] = {}
    interesting: list[dict[str, Any]] = []
    runs: list[dict[str, Any]] = []
    open_run: dict[str, Any] | None = None
    wanted = {"decrypted", "changed", "materialised", "rewritten"}

    for rva in range(0, len(runtime) - PAGE_SIZE + 1, PAGE_SIZE):
        name, executable = section_of(pe, rva)
        if not name:
            continue
        if args.executable_only and not executable:
            continue
        current = runtime[rva : rva + PAGE_SIZE]
        other = reference_page(rva)
        if other is None:
            counts["no-reference"] += 1
            continue
        if is_blank(current) and is_blank(other):
            counts["blank"] += 1
            label = "blank"
        else:
            label, ratio, current_entropy, other_entropy = classify(
                current, other, mode=mode
            )
            counts[label] += 1
            if label in wanted:
                if len(interesting) < args.max_listed:
                    interesting.append(
                        {
                            "rva": f"0x{rva:08x}",
                            "section": name,
                            "executable": executable,
                            "classification": label,
                            "identical_ratio": round(ratio, 4),
                            "runtime_entropy": round(current_entropy, 3),
                            "reference_entropy": round(other_entropy, 3),
                        }
                    )
        by_section.setdefault(name, collections.Counter())[label] += 1

        # Contiguous runs of interesting pages localise a whole function or
        # module far better than isolated page hits.
        if label in wanted:
            if open_run and open_run["end_rva"] == rva:
                open_run["end_rva"] = rva + PAGE_SIZE
                open_run["pages"] += 1
            else:
                if open_run:
                    runs.append(open_run)
                open_run = {
                    "start_rva": rva,
                    "end_rva": rva + PAGE_SIZE,
                    "pages": 1,
                    "section": name,
                    "executable": executable,
                }
        elif open_run:
            runs.append(open_run)
            open_run = None
    if open_run:
        runs.append(open_run)

    runs.sort(key=lambda item: item["pages"], reverse=True)
    report = {
        "runtime_image": str(args.runtime_image.resolve()),
        "reference": str((args.disk_exe or args.baseline).resolve()),
        "mode": mode,
        "page_counts": dict(counts),
        "per_section": {
            name: dict(section_counts) for name, section_counts in by_section.items()
        },
        "largest_runs": [
            {
                "start_rva": f"0x{run['start_rva']:08x}",
                "end_rva": f"0x{run['end_rva']:08x}",
                "pages": run["pages"],
                "bytes": run["pages"] * PAGE_SIZE,
                "section": run["section"],
                "executable": run["executable"],
            }
            for run in runs[:60]
        ],
        "run_count": len(runs),
        "pages": interesting,
    }

    text = json.dumps(report, indent=2)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(text + "\n", encoding="utf-8")

    print(f"mode: {mode}")
    print(f"page classifications: {dict(counts)}")
    print(f"interesting runs: {len(runs)}")
    for run in runs[:15]:
        print(
            f"  {run['section']:<10} 0x{run['start_rva']:08x}-0x{run['end_rva']:08x}"
            f"  {run['pages']:>5} pages  {'exec' if run['executable'] else ''}"
        )
    if args.output:
        print(f"report: {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
