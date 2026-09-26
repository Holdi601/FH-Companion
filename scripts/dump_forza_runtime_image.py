"""Dump a live module image from a running process into an analysable PE file.

Large parts of forzahorizon6.exe are stored encrypted on disk and only
decrypted in memory, so disassembling the shipped executable produces garbage
for exactly the regions this project cares about (the Xls transport, the
scoreboard deserializer, the crypto path). Comparing a static code page against
the same page read from the live process showed only 20% of bytes matching, with
the static copy at 7.8 bits/byte entropy and the runtime copy decoding as normal
MSVC code.

This reads the module out of the live process and writes it back out as a PE
whose file offsets equal its RVAs, so the existing pefile/capstone tooling
(find_pe_string_xrefs.py, find_pe_rva_xrefs.py, inspect_pe_functions.py,
find_msvc_rtti_vftables.py) works on the decrypted code without modification.

Read-only: it opens the target with PROCESS_QUERY_INFORMATION|PROCESS_VM_READ
and never writes to the process or injects anything.

Usage inside the VM:

    C:\\ForzaTools\\Python312\\python.exe dump_forza_runtime_image.py ^
        --process-name forzahorizon6 ^
        --output C:\\ForzaCaptures\\runtime_image\\forza_runtime.exe ^
        --compare-exe "C:\\Program Files (x86)\\Steam\\steamapps\\common\\ForzaHorizon6\\forzahorizon6.exe"

Depends only on ctypes so it needs no packages in the guest.
"""

from __future__ import annotations

import argparse
import collections
import ctypes
import json
import math
import struct
import sys
from ctypes import wintypes
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

PROCESS_QUERY_INFORMATION = 0x0400
PROCESS_VM_READ = 0x0010
LIST_MODULES_ALL = 0x03
PAGE_SIZE = 0x1000
IMAGE_SCN_MEM_EXECUTE = 0x20000000


class ModuleInfo(ctypes.Structure):
    _fields_ = [
        ("lpBaseOfDll", ctypes.c_void_p),
        ("SizeOfImage", wintypes.DWORD),
        ("EntryPoint", ctypes.c_void_p),
    ]


if sys.platform == "win32":
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel32.OpenProcess.restype = wintypes.HANDLE
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel32.CloseHandle.restype = wintypes.BOOL
    kernel32.ReadProcessMemory.argtypes = [
        wintypes.HANDLE,
        ctypes.c_void_p,
        ctypes.c_void_p,
        ctypes.c_size_t,
        ctypes.POINTER(ctypes.c_size_t),
    ]
    kernel32.ReadProcessMemory.restype = wintypes.BOOL
    kernel32.K32EnumProcessModulesEx.argtypes = [
        wintypes.HANDLE,
        ctypes.POINTER(ctypes.c_void_p),
        wintypes.DWORD,
        ctypes.POINTER(wintypes.DWORD),
        wintypes.DWORD,
    ]
    kernel32.K32EnumProcessModulesEx.restype = wintypes.BOOL
    kernel32.K32GetModuleBaseNameW.argtypes = [
        wintypes.HANDLE,
        ctypes.c_void_p,
        wintypes.LPWSTR,
        wintypes.DWORD,
    ]
    kernel32.K32GetModuleBaseNameW.restype = wintypes.DWORD
    kernel32.K32GetModuleInformation.argtypes = [
        wintypes.HANDLE,
        ctypes.c_void_p,
        ctypes.POINTER(ModuleInfo),
        wintypes.DWORD,
    ]
    kernel32.K32GetModuleInformation.restype = wintypes.BOOL


def entropy(data: bytes) -> float:
    if not data:
        return 0.0
    counts = collections.Counter(data)
    total = len(data)
    return -sum(
        (count / total) * math.log2(count / total) for count in counts.values()
    )


def resolve_pid(process_name: str) -> int:
    """Find the newest process with this name, without requiring psutil."""
    import subprocess

    completed = subprocess.run(
        [
            "powershell.exe",
            "-NoProfile",
            "-Command",
            (
                f"Get-Process -Name '{process_name}' -ErrorAction Stop | "
                "Sort-Object StartTime -Descending | "
                "Select-Object -First 1 -ExpandProperty Id"
            ),
        ],
        capture_output=True,
        text=True,
        check=False,
    )
    text = completed.stdout.strip()
    if not text.isdigit():
        raise RuntimeError(
            f"Could not resolve a pid for '{process_name}': {completed.stderr.strip()}"
        )
    return int(text)


def find_module(process: int, module_name: str) -> tuple[int, int]:
    needed = wintypes.DWORD()
    count = 1024
    while True:
        array = (ctypes.c_void_p * count)()
        ok = kernel32.K32EnumProcessModulesEx(
            process,
            array,
            ctypes.sizeof(array),
            ctypes.byref(needed),
            LIST_MODULES_ALL,
        )
        if not ok:
            raise ctypes.WinError(ctypes.get_last_error())
        if needed.value <= ctypes.sizeof(array):
            break
        count = needed.value // ctypes.sizeof(ctypes.c_void_p) + 16

    handles = needed.value // ctypes.sizeof(ctypes.c_void_p)
    wanted = module_name.lower()
    for index in range(handles):
        handle = array[index]
        if not handle:
            continue
        buffer = ctypes.create_unicode_buffer(260)
        if not kernel32.K32GetModuleBaseNameW(process, handle, buffer, 260):
            continue
        if buffer.value.lower() != wanted:
            continue
        info = ModuleInfo()
        if not kernel32.K32GetModuleInformation(
            process, handle, ctypes.byref(info), ctypes.sizeof(info)
        ):
            raise ctypes.WinError(ctypes.get_last_error())
        return int(info.lpBaseOfDll or 0), int(info.SizeOfImage)
    raise RuntimeError(f"Module '{module_name}' is not loaded in the target process.")


def read_exact(process: int, address: int, length: int) -> bytes:
    buffer = ctypes.create_string_buffer(length)
    actual = ctypes.c_size_t()
    ok = kernel32.ReadProcessMemory(
        process,
        ctypes.c_void_p(address),
        buffer,
        length,
        ctypes.byref(actual),
    )
    if not ok:
        return b""
    return buffer.raw[: actual.value]


def read_image(
    process: int,
    base: int,
    size: int,
    *,
    block_size: int,
) -> tuple[bytearray, list[tuple[int, int]]]:
    """Read the whole mapped image, zero-filling pages that cannot be read.

    A failed bulk read is retried page by page so one guard page does not cost
    the surrounding 64 KB.
    """
    image = bytearray(size)
    unreadable: list[tuple[int, int]] = []
    offset = 0
    while offset < size:
        want = min(block_size, size - offset)
        chunk = read_exact(process, base + offset, want)
        if len(chunk) == want:
            image[offset : offset + want] = chunk
            offset += want
            continue

        page_offset = offset
        page_end = offset + want
        while page_offset < page_end:
            page_want = min(PAGE_SIZE, page_end - page_offset)
            page = read_exact(process, base + page_offset, page_want)
            if len(page) == page_want:
                image[page_offset : page_offset + page_want] = page
            else:
                if unreadable and unreadable[-1][0] + unreadable[-1][1] == page_offset:
                    unreadable[-1] = (unreadable[-1][0], unreadable[-1][1] + page_want)
                else:
                    unreadable.append((page_offset, page_want))
            page_offset += page_want
        offset = page_end
    return image, unreadable


def parse_headers(image: bytes) -> dict[str, Any]:
    if image[:2] != b"MZ":
        raise RuntimeError("The mapped image does not start with an MZ signature.")
    nt_offset = struct.unpack_from("<I", image, 0x3C)[0]
    if image[nt_offset : nt_offset + 4] != b"PE\0\0":
        raise RuntimeError("The mapped image has no PE signature.")

    file_header = nt_offset + 4
    section_count = struct.unpack_from("<H", image, file_header + 2)[0]
    optional_size = struct.unpack_from("<H", image, file_header + 16)[0]
    optional_header = file_header + 20
    magic = struct.unpack_from("<H", image, optional_header)[0]
    if magic != 0x20B:
        raise RuntimeError(f"Expected a PE32+ image, found optional magic 0x{magic:x}.")

    section_alignment = struct.unpack_from("<I", image, optional_header + 32)[0]
    file_alignment = struct.unpack_from("<I", image, optional_header + 36)[0]
    size_of_image = struct.unpack_from("<I", image, optional_header + 56)[0]
    image_base = struct.unpack_from("<Q", image, optional_header + 24)[0]
    section_table = optional_header + optional_size

    sections = []
    for index in range(section_count):
        entry = section_table + index * 40
        name = image[entry : entry + 8].rstrip(b"\0").decode("ascii", "replace")
        sections.append(
            {
                "index": index,
                "entry_offset": entry,
                "name": name,
                "virtual_size": struct.unpack_from("<I", image, entry + 8)[0],
                "virtual_address": struct.unpack_from("<I", image, entry + 12)[0],
                "size_of_raw_data": struct.unpack_from("<I", image, entry + 16)[0],
                "pointer_to_raw_data": struct.unpack_from("<I", image, entry + 20)[0],
                "characteristics": struct.unpack_from("<I", image, entry + 36)[0],
            }
        )

    return {
        "nt_offset": nt_offset,
        "optional_header": optional_header,
        "section_alignment": section_alignment,
        "file_alignment": file_alignment,
        "size_of_image": size_of_image,
        "image_base": image_base,
        "sections": sections,
    }


def flatten_to_rva_layout(image: bytearray, headers: dict[str, Any]) -> None:
    """Rewrite the section table so file offset == RVA.

    pefile maps RVAs through PointerToRawData/SizeOfRawData. Pointing each
    section's raw data at its own virtual address, and declaring FileAlignment
    equal to SectionAlignment, makes get_data() and get_rva_from_offset()
    resolve straight into the dumped runtime bytes.
    """
    alignment = headers["section_alignment"]
    struct.pack_into("<I", image, headers["optional_header"] + 36, alignment)
    limit = len(image)

    for section in headers["sections"]:
        entry = section["entry_offset"]
        virtual_address = section["virtual_address"]
        virtual_size = section["virtual_size"]
        raw_size = ((virtual_size + alignment - 1) // alignment) * alignment
        if virtual_address + raw_size > limit:
            raw_size = max(0, limit - virtual_address)
        struct.pack_into("<I", image, entry + 16, raw_size)
        struct.pack_into("<I", image, entry + 20, virtual_address)
        section["dumped_size_of_raw_data"] = raw_size
        section["dumped_pointer_to_raw_data"] = virtual_address


def compare_with_disk(
    image: bytes,
    headers: dict[str, Any],
    disk_path: Path,
) -> dict[str, Any]:
    """Quantify how much of each section is decrypted only at runtime."""
    disk = disk_path.read_bytes()
    disk_headers = parse_headers(disk[:0x1000] + b"\0" * 0x1000)
    by_name = {section["name"]: section for section in disk_headers["sections"]}

    report: list[dict[str, Any]] = []
    for section in headers["sections"]:
        disk_section = by_name.get(section["name"])
        if disk_section is None:
            continue
        length = min(
            section["virtual_size"],
            disk_section["size_of_raw_data"],
            max(0, len(image) - section["virtual_address"]),
        )
        if length <= 0:
            continue
        runtime_bytes = image[
            section["virtual_address"] : section["virtual_address"] + length
        ]
        disk_offset = disk_section["pointer_to_raw_data"]
        disk_bytes = disk[disk_offset : disk_offset + length]
        if len(disk_bytes) < length:
            continue
        matching = sum(1 for left, right in zip(runtime_bytes, disk_bytes) if left == right)
        report.append(
            {
                "section": section["name"],
                "compared_bytes": length,
                "identical_bytes": matching,
                "identical_ratio": round(matching / length, 6),
                "runtime_entropy": round(entropy(runtime_bytes[: 4 << 20]), 4),
                "disk_entropy": round(entropy(disk_bytes[: 4 << 20]), 4),
                "executable": bool(section["characteristics"] & IMAGE_SCN_MEM_EXECUTE),
            }
        )
    return {"disk_exe": str(disk_path), "sections": report}


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Dump a live module image to a PE whose file offsets equal its RVAs."
    )
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--pid", type=int)
    source.add_argument("--process-name")
    parser.add_argument("--module", default="")
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--report", type=Path)
    parser.add_argument(
        "--compare-exe",
        type=Path,
        help="On-disk copy of the same module, to measure runtime-only decryption.",
    )
    parser.add_argument("--block-size-kb", type=int, default=256)
    args = parser.parse_args()

    if sys.platform != "win32":
        raise RuntimeError("Dumping a live image requires Windows.")

    pid = args.pid if args.pid else resolve_pid(args.process_name)
    module_name = args.module or (
        f"{args.process_name}.exe" if args.process_name else ""
    )
    if not module_name:
        raise RuntimeError("--module is required when using --pid.")
    if not module_name.lower().endswith((".exe", ".dll")):
        module_name = f"{module_name}.exe"

    process = kernel32.OpenProcess(
        PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, pid
    )
    if not process:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        base, size = find_module(process, module_name)
        image, unreadable = read_image(
            process, base, size, block_size=max(1, args.block_size_kb) * 1024
        )
    finally:
        kernel32.CloseHandle(process)

    headers = parse_headers(bytes(image[:0x1000]))
    flatten_to_rva_layout(image, headers)

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes(bytes(image))

    unreadable_bytes = sum(length for _, length in unreadable)
    report: dict[str, Any] = {
        "dumped_at": datetime.now(timezone.utc).isoformat(),
        "process_id": pid,
        "module": module_name,
        "module_base": f"0x{base:016x}",
        "image_base": f"0x{headers['image_base']:016x}",
        "size_of_image": size,
        "output": str(args.output.resolve()),
        "unreadable_ranges": [
            {"rva": f"0x{start:08x}", "length": length} for start, length in unreadable
        ],
        "unreadable_bytes": unreadable_bytes,
        "sections": [
            {
                "name": section["name"],
                "virtual_address": f"0x{section['virtual_address']:08x}",
                "virtual_size": section["virtual_size"],
                "executable": bool(section["characteristics"] & IMAGE_SCN_MEM_EXECUTE),
            }
            for section in headers["sections"]
        ],
    }
    if args.compare_exe:
        report["disk_comparison"] = compare_with_disk(
            bytes(image), headers, args.compare_exe
        )

    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
