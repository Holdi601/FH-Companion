"""Die Teileliste eines Autos im Speicher finden -- ohne den Umweg ueber TuningData.

## Warum ein eigenes Werkzeug

`find_tune_data.py` sucht `TuningData` (636 B) als Ganzes: Teile, Einstellungen,
CarId, alles in einer Schachtel. Am 2026-08-30 fand es waehrend eines laufenden
Rennens -- mit sichtbar fahrendem Geist -- **null** Saetze bei vollstaendig
abgesuchtem Speicher (5,89 GiB). Der Geist wird aber dargestellt, also MUSS sein
Aufbau geladen sein. Der Schluss: die Schachtel sieht anders aus als angenommen,
nicht die Teileliste.

Also wird hier nur nach `CInstalledParts` gesucht (404 B), ohne jede Annahme
darueber, worin sie steckt.

## Der Anker

Die Struktur hat 101 int32-Felder. Die ersten 50 sind benannt (Motor, Turbo,
Reifen, Gewicht ...), die letzten 51 heissen in der Typtabelle `_unknown0..50` und
sind im Spiel unbenutzt. **204 Byte am Stueck null** ist damit die Signatur -- und
ein Nullblock dieser Laenge laesst sich in C-Tempo finden.

Weil der Nullblock frueher beginnt, wenn auch die letzten benannten Teile 0 sind
(ein Auto ohne Spurverbreiterung zum Beispiel), wird der Satzanfang nicht fest
gerechnet, sondern in 4-Byte-Schritten rueckwaerts probiert.

    python scripts/find_installed_parts.py
    python scripts/find_installed_parts.py --min-parts 5
    python scripts/find_installed_parts.py --self-test

Laeuft im GAST. Rein lesend.
"""

from __future__ import annotations

import argparse
import ctypes
import json
import re
import struct
import sys
import time
from ctypes import wintypes
from pathlib import Path
from typing import Iterable

PROCESS_QUERY_INFORMATION = 0x0400
PROCESS_VM_READ = 0x0010
MEM_COMMIT = 0x1000
MEM_PRIVATE = 0x20000
PAGE_NOACCESS = 0x01
PAGE_GUARD = 0x100

RECORD_SIZE = 404
NAMED_COUNT = 50          # Felder 0..49 sind benannt
TAIL_AT = NAMED_COUNT * 4  # 200 -- ab hier die 51 unbenutzten Felder
TAIL_BYTES = RECORD_SIZE - TAIL_AT   # 204

PART_NAMES = [
    "Version", "Engine", "Drivetrain", "CarBody", "Motor", "Brakes", "SpringDamper",
    "AntiSwayFront", "AntiSwayRear", "TireCompound", "RearWing", "RimSizeFront",
    "RimSizeRear", "Camshaft", "Valves", "Displacement", "PistonsCompression",
    "FuelSystem", "Ignition", "Exhaust", "Intake", "Flywheel", "Manifold",
    "RestrictorPlate", "OilCooling", "SingleTurbo", "TwinTurbo", "QuadTurbo",
    "SuperchargerCSC", "SuperchargerDSC", "Intercooler", "Clutch", "Transmission",
    "Driveline", "Differential", "FrontBumper", "RearBumper", "Hood", "SideSkirts",
    "TireWidthFront", "TireWidthRear", "WeightReduction", "ChassisStiffness",
    "MotorParts", "WheelStyle", "_unused_Aspiration", "TrackSpacingFront",
    "TrackSpacingRear", "FrontAspectRatioOffset", "RearAspectRatioOffset",
]
assert len(PART_NAMES) == NAMED_COUNT

# Ein Nullblock von 204 Byte. Das ist der gesamte unbenutzte Schwanz der Struktur.
ZERO_TAIL = re.compile(rb"\x00{%d}" % TAIL_BYTES)

# Wie weit der Satzanfang vor dem Nullblock liegen kann: wenn die letzten benannten
# Teile ebenfalls 0 sind, beginnt der Block frueher. 24 Felder Spielraum decken die
# gesamte Karosserie- und Reifengruppe ab.
MAX_TRAILING_ZERO_FIELDS = 24


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


def decode(raw: bytes, address: int, min_parts: int) -> dict | None:
    """Einen Kandidaten pruefen und, wenn er haelt, als Teileliste zurueckgeben."""
    if len(raw) < RECORD_SIZE:
        return None
    # Der Schwanz muss leer sein -- das ist die halbe Signatur.
    if raw.count(0, TAIL_AT, RECORD_SIZE) != TAIL_BYTES:
        return None

    values = list(struct.unpack_from("<%di" % NAMED_COUNT, raw, 0))
    # Ausbaustufen sind kleine Ganzzahlen. Ein Zeiger oder eine Gleitkommazahl,
    # die hier als int gelesen wird, faellt sofort auf.
    for value in values:
        if value < -1 or value > 2000:
            return None
    installed = {PART_NAMES[i]: v for i, v in enumerate(values)
                 if i > 0 and v not in (0, -1)}
    if len(installed) < min_parts:
        return None
    # Version ist ein Formatzaehler, keine Ausbaustufe.
    if not 0 <= values[0] <= 50:
        return None
    return {
        "address": hex(address),
        "version": values[0],
        "installed_count": len(installed),
        "installed": installed,
        "all_named": {PART_NAMES[i]: v for i, v in enumerate(values)},
    }


def records_in_block(data: bytes, base_address: int, min_parts: int) -> list[dict]:
    found: list[dict] = []
    for match in ZERO_TAIL.finditer(data):
        # Der Nullblock beginnt NICHT verlaesslich bei Satzanfang+200.
        #
        # Zwei Gruende, und der zweite hat den ersten Entwurf zu Fall gebracht:
        #   * sind die letzten benannten Felder 0, faengt der Block frueher an;
        #   * eine kleine positive Zahl im letzten Feld hat selbst drei Nullbytes
        #     oben drauf. Im Selbsttest stand 2 bei Feld 49, und der Nullblock begann
        #     bei 197 statt 200 -- rechnete man Satzanfang = start-200 in
        #     4-Byte-Schritten, traf man die 0 nie, weil 197 nicht auf dem Raster
        #     liegt. Der Suchlauf haette im Spiel geschwiegen und ich haette es fuer
        #     ein Ergebnis gehalten.
        #
        # Darum wird ein Fenster abgesucht, und die Ausrichtung kommt vom Raster des
        # Puffers (Regionsanfang und Blockgroesse sind beide durch 4 teilbar), nicht
        # von der Fundstelle.
        lowest = match.start() - TAIL_AT - 4
        highest = match.start() - TAIL_AT + MAX_TRAILING_ZERO_FIELDS * 4 + 4
        for start in range(lowest, highest + 1):
            if start % 4 or start < 0 or start + RECORD_SIZE > len(data):
                continue
            record = decode(data[start:start + RECORD_SIZE],
                            base_address + start, min_parts)
            if record:
                found.append(record)
                break
    return found


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


def scan(pid: int, min_parts: int, max_hits: int, max_seconds: float,
         chunk: int = 8 << 20) -> list[dict]:
    process = kernel32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ,
                                   False, pid)
    if not process:
        raise SystemExit(f"OpenProcess auf {pid} fehlgeschlagen")
    out: list[dict] = []
    seen: set[str] = set()
    deadline = time.monotonic() + max_seconds
    started = time.monotonic()
    scanned = 0
    truncated = False
    try:
        for base, size in readable_regions(process):
            offset = 0
            while offset < size and len(out) < max_hits:
                if time.monotonic() > deadline:
                    truncated = True
                    break
                length = min(chunk, size - offset)
                data = read_memory(process, base + offset, length + RECORD_SIZE)
                scanned += len(data)
                if data:
                    for entry in records_in_block(data, base + offset, min_parts):
                        key = json.dumps(entry["installed"], sort_keys=True)
                        if key in seen:
                            continue
                        seen.add(key)
                        out.append(entry)
                        if len(out) >= max_hits:
                            break
                offset += length
            if len(out) >= max_hits or truncated:
                break
    finally:
        kernel32.CloseHandle(process)
    elapsed = time.monotonic() - started
    out.append({
        "address": "_coverage",
        "scanned_gib": round(scanned / (1 << 30), 2),
        "seconds": round(elapsed, 1),
        "complete": not truncated,
    })
    return out


def render(record: dict) -> str:
    parts = record["installed"]
    lines = [f"  @ {record['address']}  Version {record['version']}, "
             f"{record['installed_count']} Teile gesetzt"]
    row: list[str] = []
    for name, value in parts.items():
        row.append(f"{name}={value}")
        if len(row) == 5:
            lines.append("      " + ", ".join(row))
            row = []
    if row:
        lines.append("      " + ", ".join(row))
    return "\n".join(lines)


def self_test() -> int:
    ok = True

    def check(label: str, condition: bool) -> None:
        nonlocal ok
        print(f"  {'ok  ' if condition else 'FEHL'}  {label}")
        ok = ok and condition

    raw = bytearray(RECORD_SIZE)
    struct.pack_into("<i", raw, 0, 3)             # Version
    struct.pack_into("<i", raw, 4 * 1, 2)         # Engine
    struct.pack_into("<i", raw, 4 * 9, 4)         # TireCompound
    struct.pack_into("<i", raw, 4 * 25, 1)        # SingleTurbo
    struct.pack_into("<i", raw, 4 * 41, 3)        # WeightReduction
    struct.pack_into("<i", raw, 4 * 49, 2)        # RearAspectRatioOffset (letztes Feld)

    rec = decode(bytes(raw), 0x1000, 3)
    check("Satz wird erkannt", rec is not None)
    if rec:
        check("Version gelesen", rec["version"] == 3)
        check("Teile gezaehlt", rec["installed_count"] == 5)
        check("TireCompound", rec["installed"].get("TireCompound") == 4)
        check("WeightReduction", rec["installed"].get("WeightReduction") == 3)
        check("Version zaehlt nicht als Teil", "Version" not in rec["installed"])

    found = records_in_block(bytes(raw), 0x2000, 3)
    check("ueber den Nullschwanz auffindbar", len(found) == 1)

    # Der Fall, fuer den die Rueckwaertssuche gebaut ist: die letzten benannten
    # Felder sind ebenfalls 0, der Nullblock beginnt also frueher.
    raw2 = bytearray(raw)
    struct.pack_into("<i", raw2, 4 * 49, 0)
    struct.pack_into("<i", raw2, 4 * 48, 0)
    found2 = records_in_block(bytes(raw2), 0x3000, 3)
    check("auch mit leeren Endfeldern auffindbar", len(found2) == 1)

    check("leerer Speicher liefert nichts",
          not records_in_block(bytes(RECORD_SIZE * 3), 0, 3))
    check("Nullblock ohne Teile liefert nichts",
          not records_in_block(b"\x00" * (RECORD_SIZE * 2), 0, 3))

    bad = bytearray(raw)
    struct.pack_into("<i", bad, 4 * 1, 1073741824)   # 0x40000000
    check("Zeigergrosser Teilewert wird verworfen",
          decode(bytes(bad), 0, 3) is None)

    bad = bytearray(raw)
    struct.pack_into("<i", bad, TAIL_AT + 8, 5)      # Schwanz nicht leer
    check("belegter Schwanz wird verworfen", decode(bytes(bad), 0, 3) is None)

    check("zu wenige Teile werden verworfen",
          decode(bytes(raw), 0, 40) is None)

    print("\nalles bestanden" if ok else "\nFEHLGESCHLAGEN")
    return 0 if ok else 1


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--process", default="forzahorizon6")
    parser.add_argument("--pid", type=int, default=0)
    parser.add_argument("--min-parts", type=int, default=4,
                        help="so viele belegte Teileplaetze muss ein Satz haben")
    parser.add_argument("--max-hits", type=int, default=60)
    parser.add_argument("--max-seconds", type=float, default=300.0)
    parser.add_argument("--out", type=Path,
                        default=Path("data/runtime/installed_parts.json"))
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

    records = scan(pid, args.min_parts, args.max_hits, args.max_seconds)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(records, indent=1), encoding="utf-8")
    coverage = [r for r in records if r.get("address") == "_coverage"]
    real = [r for r in records if r.get("address") != "_coverage"]
    print(f"{len(real)} Teileliste(n) -> {args.out}")
    if coverage:
        c = coverage[0]
        print(f"  Abdeckung: {c['scanned_gib']} GiB in {c['seconds']}s -- "
              + ("vollstaendig" if c["complete"] else "NICHT vollstaendig"))
    for record in real[:12]:
        print(render(record))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
