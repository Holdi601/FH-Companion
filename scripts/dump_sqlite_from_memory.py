"""Die SQLite-Datenbanken des Spiels aus dem Arbeitsspeicher herausholen.

## Woher der Gedanke kommt

Der Nutzer hat am 2026-08-30 die richtige Frage gestellt: "wie kommts dass du es
nicht querien kannst, du hattest doch vorher queries gefunden?"

Genau so ist es. Im Speicher stehen fertige SQL-Abfragen des Spiels:

    SELECT 40 AS PartEnum, Id AS PartId, CarbodyId, IsStock, Level,
           ManufacturerID, Price, 0 AS IsException
    FROM List_UpgradeCarBodyWeight WHERE CarbodyId = 2993000

Wo Abfragen laufen, gibt es eine Datenbank. Auf der Platte liegt keine `.db`-Datei
-- aber im Speicher stehen **sechs** SQLite-Kopfzeilen, und eine davon unmittelbar
neben Pfaden wie `Upgrade_Parts\\Drivetrain\\drivetrain_Icon.swatchbin`. Das ist der
Teilekatalog, und er ist vollstaendig da.

## Warum das mehr wert ist als jede Struktursuche

Bis hierher wurde geraten, wie eine Struktur im Speicher aussieht -- dreimal daneben.
Eine Datenbank dagegen beschreibt sich selbst: Tabellennamen, Spalten, Typen. Einmal
herausgeholt, laesst sie sich mit `sqlite3` in aller Ruhe befragen, ohne Spiel und
ohne VM.

## Was drin steht -- und was NICHT

Der Katalog sagt, welche Teile es je Karosserie GIBT, mit Stufe, Preis und
Hersteller. Er sagt **nicht**, welche Teile ein bestimmter Rivale verbaut hat. Er ist
das Woerterbuch, nicht der Text: aus "Teil 37, Stufe 2" wird damit ein lesbarer Name.

## Wie gelesen wird

Eine SQLite-Datei beginnt mit `SQLite format 3\\0`, danach steht im Kopf alles
Noetige: Seitengroesse bei Offset 16 (16 Bit, big endian; 1 bedeutet 65536) und die
Zahl der Seiten bei Offset 28 (32 Bit, big endian). Groesse = Seitengroesse * Seiten.

    python scripts/dump_sqlite_from_memory.py --out-dir C:/ForzaAutomation/dbs
    python scripts/dump_sqlite_from_memory.py --self-test

Laeuft im GAST. Rein lesend.
"""

from __future__ import annotations

import argparse
import ctypes
import json
import struct
import sys
from ctypes import wintypes
from pathlib import Path
from typing import Iterable

PROCESS_QUERY_INFORMATION = 0x0400
PROCESS_VM_READ = 0x0010
MEM_COMMIT = 0x1000
MEM_PRIVATE = 0x20000
PAGE_NOACCESS = 0x01
PAGE_GUARD = 0x100

MAGIC = b"SQLite format 3\x00"
HEADER_SIZE = 100
# Mehr als das wird nicht am Stueck herausgeholt. Eine Katalogdatenbank ist einige
# Dutzend MB gross; alles darueber ist eher ein Fehlfund als ein Volltreffer.
MAX_DB_BYTES = 512 << 20


class MemoryBasicInformation(ctypes.Structure):
    _fields_ = [
        ("BaseAddress", ctypes.c_void_p),
        ("AllocationBase", ctypes.c_void_p),
        ("AllocationProtect", wintypes.DWORD),
        ("PartitionId", wintypes.WORD),
        ("RegionSize", ctypes.c_size_t),
        ("State", wintypes.DWORD),
        ("Protect", wintypes.DWORD),
        ("Type", wintypes.DWORD),
    ]


if sys.platform == "win32":
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel32.OpenProcess.restype = wintypes.HANDLE
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel32.CloseHandle.restype = wintypes.BOOL
    kernel32.VirtualQueryEx.argtypes = [wintypes.HANDLE, ctypes.c_void_p,
                                        ctypes.POINTER(MemoryBasicInformation),
                                        ctypes.c_size_t]
    kernel32.VirtualQueryEx.restype = ctypes.c_size_t
    kernel32.ReadProcessMemory.argtypes = [wintypes.HANDLE, ctypes.c_void_p,
                                           ctypes.c_void_p, ctypes.c_size_t,
                                           ctypes.POINTER(ctypes.c_size_t)]
    kernel32.ReadProcessMemory.restype = wintypes.BOOL


def parse_header(header: bytes) -> dict | None:
    """Seitengroesse und Seitenzahl aus dem SQLite-Kopf lesen.

    Gibt None zurueck, wenn der Kopf nicht plausibel ist -- die Zeichenkette
    "SQLite format 3" kann auch als blosser Text im Speicher stehen (etwa in einer
    Fehlermeldung der Bibliothek), und dann folgt ihr kein Dateikopf.
    """
    if len(header) < HEADER_SIZE or not header.startswith(MAGIC):
        return None
    raw_page_size = struct.unpack_from(">H", header, 16)[0]
    page_size = 65536 if raw_page_size == 1 else raw_page_size
    # Erlaubt sind nur Zweierpotenzen von 512 bis 65536.
    if page_size < 512 or page_size > 65536 or (page_size & (page_size - 1)):
        return None
    page_count = struct.unpack_from(">I", header, 28)[0]
    write_version, read_version = header[18], header[19]
    if write_version not in (1, 2) or read_version not in (1, 2):
        return None
    reserved = header[20]
    if reserved > 255:
        return None
    total = page_size * page_count
    if page_count == 0 or total > MAX_DB_BYTES:
        # Seitenzahl unbrauchbar (kommt vor, wenn der Kopf nicht frisch geschrieben
        # wurde). Dann wird spaeter blockweise gelesen, bis der Speicher endet.
        total = 0
    return {
        "page_size": page_size,
        "page_count": page_count,
        "total_bytes": total,
        "text_encoding": struct.unpack_from(">I", header, 56)[0],
    }


def readable_regions(process: int) -> Iterable[tuple[int, int]]:
    address = 0
    mbi = MemoryBasicInformation()
    while address < 0x00007FFFFFFFFFFF:
        if not kernel32.VirtualQueryEx(process, ctypes.c_void_p(address),
                                       ctypes.byref(mbi), ctypes.sizeof(mbi)):
            break
        base, size = int(mbi.BaseAddress or 0), int(mbi.RegionSize)
        if base + size <= address:
            break
        if (mbi.State == MEM_COMMIT and mbi.Type == MEM_PRIVATE
                and not (mbi.Protect & PAGE_NOACCESS)
                and not (mbi.Protect & PAGE_GUARD) and size >= 4096):
            yield base, size
        address = base + size


def read_memory(process: int, address: int, length: int) -> bytes:
    buffer = ctypes.create_string_buffer(length)
    actual = ctypes.c_size_t()
    ok = kernel32.ReadProcessMemory(process, ctypes.c_void_p(address), buffer,
                                    length, ctypes.byref(actual))
    if not ok and actual.value == 0:
        return b""
    return buffer.raw[:actual.value]


def find_databases(process: int) -> list[dict]:
    """Alle Stellen, an denen ein plausibler SQLite-Kopf steht."""
    found: list[dict] = []
    for base, size in readable_regions(process):
        offset = 0
        chunk = 8 << 20
        while offset < size:
            length = min(chunk, size - offset)
            data = read_memory(process, base + offset, length + HEADER_SIZE)
            if not data:
                offset += length
                continue
            start = 0
            while True:
                at = data.find(MAGIC, start)
                if at < 0:
                    break
                start = at + 1
                info = parse_header(data[at:at + HEADER_SIZE])
                if info:
                    info["address"] = base + offset + at
                    found.append(info)
            offset += length
    return found


def dump(process: int, entry: dict, out_dir: Path) -> dict:
    address = entry["address"]
    total = entry["total_bytes"]
    if not total:
        total = min(64 << 20, MAX_DB_BYTES)
    out_dir.mkdir(parents=True, exist_ok=True)
    path = out_dir / f"forza_{address:012x}.db"
    written = 0
    with path.open("wb") as handle:
        while written < total:
            piece = read_memory(process, address + written,
                                min(4 << 20, total - written))
            if not piece:
                break
            handle.write(piece)
            written += len(piece)
    return {"path": str(path), "bytes": written,
            "expected": total, "address": hex(address),
            "page_size": entry["page_size"], "page_count": entry["page_count"]}


def self_test() -> int:
    ok = True

    def check(label: str, condition: bool) -> None:
        nonlocal ok
        print(f"  {'ok  ' if condition else 'FEHL'}  {label}")
        ok = ok and condition

    header = bytearray(HEADER_SIZE)
    header[0:16] = MAGIC
    struct.pack_into(">H", header, 16, 4096)      # Seitengroesse
    header[18] = 1
    header[19] = 1
    struct.pack_into(">I", header, 28, 25)        # 25 Seiten
    struct.pack_into(">I", header, 56, 1)         # UTF-8
    info = parse_header(bytes(header))
    check("Kopf wird gelesen", info is not None)
    if info:
        check("Seitengroesse", info["page_size"] == 4096)
        check("Seitenzahl", info["page_count"] == 25)
        check("Gesamtgroesse", info["total_bytes"] == 4096 * 25)

    check("Text ohne Kopf wird verworfen",
          parse_header(MAGIC + b"x" * 84) is None)

    bad = bytearray(header)
    struct.pack_into(">H", bad, 16, 4095)         # keine Zweierpotenz
    check("krumme Seitengroesse wird verworfen", parse_header(bytes(bad)) is None)

    bad = bytearray(header)
    bad[18] = 7                                    # unmoegliche Schreibversion
    check("unmoegliche Version wird verworfen", parse_header(bytes(bad)) is None)

    big = bytearray(header)
    struct.pack_into(">I", big, 28, 0xFFFFFFF)     # absurd viele Seiten
    info_big = parse_header(bytes(big))
    check("absurde Groesse faellt auf blockweises Lesen zurueck",
          info_big is not None and info_big["total_bytes"] == 0)

    check("Seitengroesse 1 bedeutet 65536",
          (lambda h: parse_header(bytes(h)))(
              (lambda: (lambda x: (struct.pack_into(">H", x, 16, 1), x)[1])(
                  bytearray(header)))())["page_size"] == 65536)

    print("\nalles bestanden" if ok else "\nFEHLGESCHLAGEN")
    return 0 if ok else 1


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--process", default="forzahorizon6")
    parser.add_argument("--pid", type=int, default=0)
    parser.add_argument("--out-dir", type=Path,
                        default=Path("C:/ForzaAutomation/dbs"))
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()

    if args.self_test:
        return self_test()
    if sys.platform != "win32":
        raise SystemExit("liest Prozessspeicher -- nur unter Windows")

    pid = args.pid
    if not pid:
        import subprocess
        result = subprocess.run(
            ["powershell.exe", "-NoProfile", "-Command",
             f"(Get-Process -Name '{args.process}' -ErrorAction SilentlyContinue).Id"],
            capture_output=True, text=True)
        digits = "".join(c for c in result.stdout if c.isdigit())
        pid = int(digits) if digits else 0
    if not pid:
        raise SystemExit(f"{args.process} laeuft nicht")

    process = kernel32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ,
                                   False, pid)
    if not process:
        raise SystemExit(f"OpenProcess auf {pid} fehlgeschlagen")
    try:
        entries = find_databases(process)
        print(f"{len(entries)} SQLite-Kopf/-Koepfe gefunden")
        results = []
        for entry in entries:
            print(f"  {hex(entry['address'])}: Seiten {entry['page_count']} "
                  f"x {entry['page_size']} = {entry['total_bytes']} Byte")
            results.append(dump(process, entry, args.out_dir))
        for item in results:
            print(f"  -> {item['path']} ({item['bytes']} Byte)")
        print(json.dumps(results, indent=1))
    finally:
        kernel32.CloseHandle(process)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
