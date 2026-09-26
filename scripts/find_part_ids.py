"""Teilelisten im Speicher finden -- diesmal im richtigen Zahlenbereich.

## Der Fehler, den dieses Skript behebt

`find_installed_parts.py` hat jede Ganzzahl ueber 2000 als "unmoeglich" verworfen.
Die Annahme kam aus der Typtabelle, wo die Felder wie Ausbaustufen aussehen (0, 1, 2).
**Sie ist falsch.** In `Career_Garage`, der echten Datenbank des Spiels, stehen
Teile-IDs:

    Engine=4222002  Drivetrain=4222001  CarBody=4222100  Camshaft=4077003
    Clutch=2102000  Transmission=2102008  WeightReduction=4222103

Gemessen ueber 200 Autos: 3.190 Werte zwischen **16.000 und 4.486.000**, davon 2.626
siebenstellig und 532 sechsstellig. Der alte Filter hat also nicht "wenig gefunden",
sondern **alles Echte weggeworfen** -- und die 400 Treffer, die er meldete, waren
Katalogzeilen mit den Stufen 1, 2 und 46.

## Die Signatur

Eine Teileliste ist eine Folge von int32, in der jeder Wert entweder **-1** ist
("nicht verbaut") oder eine Teile-ID im obigen Bereich. Eine solche Folge von 25 und
mehr Werten am Stueck kommt im Speicher praktisch nicht zufaellig vor.

Dazu kommt ein zweites, sehr starkes Merkmal: die IDs eines Autos teilen sich
**Praefixe**. Oben ist 4222xxx die Karosserie, 4077xxx der Motor, 2102xxx das
Getriebe. Wo viele Werte denselben Tausenderblock teilen, ist es ein Fahrzeug und
kein Zufall.

    python scripts/find_part_ids.py
    python scripts/find_part_ids.py --min-run 30 --max-hits 40
    python scripts/find_part_ids.py --self-test

Laeuft im GAST. Rein lesend.
"""

from __future__ import annotations

import argparse
import collections
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

PART_MIN = 10_000
PART_MAX = 9_999_999
NOT_INSTALLED = -1

# Ein int32 im Bereich 10.000..9.999.999 hat als kleines Endian das oberste Byte 0
# und das dritthoechste hoechstens 0x98 (9.999.999 = 0x0098967F). Dazu -1 als
# vier 0xFF. Beides zusammen, 25 Mal am Stueck, ist der Anker.
SLOT = rb"(?:[\s\S][\s\S][\x00-\x98]\x00|\xff\xff\xff\xff)"


def run_pattern(min_run: int) -> re.Pattern:
    return re.compile(SLOT + b"{%d,}" % min_run)


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


def evaluate(values: list[int]) -> dict | None:
    """Aus einer Zahlenfolge einen Fahrzeug-Kandidaten machen -- oder nichts."""
    installed = [v for v in values if v != NOT_INSTALLED]
    if len(installed) < 12:
        return None
    if not all(PART_MIN <= v <= PART_MAX for v in installed):
        return None
    # Wiederholung ist kein Fahrzeug. Der erste Lauf am lebenden Spiel meldete
    # Folgen wie 983055 zweiundachtzigmal hintereinander und 33840/-1 im Wechsel --
    # Puffer, Kacheln, Indextabellen. Eine Teileliste hat viele VERSCHIEDENE Werte:
    # jedes Teil ist ein anderes.
    counts = collections.Counter(installed)
    if len(counts) < 8:
        return None
    if counts.most_common(1)[0][1] > len(installed) // 2:
        return None

    # Sortierte oder durchgezaehlte Folgen sind Tabellen, keine Autos. Der Lauf im
    # Rennen meldete 393252, 393253, 393254 ... und aufsteigende Indexlisten wie
    # 67139, 67303, 67490 -- beides klar erkennbar daran, dass es nur aufwaerts geht.
    # Eine Teileliste folgt der Spaltenreihenfolge des Fahrzeugs, nicht der Groesse.
    rising = sum(1 for a, b in zip(installed, installed[1:]) if b > a)
    if rising >= len(installed) * 0.9 - 1:
        return None

    # Praefixe: die IDs eines Autos teilen sich Tausenderbloecke.
    prefixes = collections.Counter(v // 1000 for v in installed)
    top_prefix, top_count = prefixes.most_common(1)[0]
    if top_count < 4:
        return None
    # Die alte Grenze (bis zur Haelfte der Werte) war praktisch keine: bei 52 Werten
    # liess sie 26 verschiedene Praefixe zu. In einer echten Zeile decken die drei
    # haeufigsten Bloecke fast alles ab -- Karosserie, Motor, Getriebe.
    covered = sum(count for _, count in prefixes.most_common(3))
    if covered < len(installed) * 0.7:
        return None
    return {
        "slots": len(values),
        "installed": len(installed),
        "dominant_prefix": top_prefix,
        "dominant_count": top_count,
        "prefixes": [{"prefix": p, "count": c} for p, c in prefixes.most_common(6)],
        "values": values,
    }


def runs_in_block(data: bytes, base: int, pattern: re.Pattern) -> list[dict]:
    found: list[dict] = []
    for match in pattern.finditer(data):
        start, end = match.start(), match.end()
        if start % 4:
            shift = 4 - (start % 4)
            start += shift
        count = (end - start) // 4
        if count < 12:
            continue
        values = list(struct.unpack_from("<%di" % count, data, start))
        entry = evaluate(values)
        if entry:
            entry["address"] = hex(base + start)
            found.append(entry)
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


def scan(pid: int, min_run: int, max_hits: int, max_seconds: float,
         chunk: int = 8 << 20) -> list[dict]:
    process = kernel32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ,
                                   False, pid)
    if not process:
        raise SystemExit(f"OpenProcess auf {pid} fehlgeschlagen")
    pattern = run_pattern(min_run)
    out: list[dict] = []
    seen: set[tuple] = set()
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
                data = read_memory(process, base + offset, length + 512)
                scanned += len(data)
                if data:
                    for entry in runs_in_block(data, base + offset, pattern):
                        key = tuple(entry["values"][:24])
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
    out.append({"address": "_coverage",
                "scanned_gib": round(scanned / (1 << 30), 2),
                "seconds": round(time.monotonic() - started, 1),
                "complete": not truncated})
    return out


def self_test() -> int:
    ok = True

    def check(label: str, condition: bool) -> None:
        nonlocal ok
        print(f"  {'ok  ' if condition else 'FEHL'}  {label}")
        ok = ok and condition

    # Eine echte Zeile aus Career_Garage, gekuerzt.
    real = [4222002, 4222001, 4222100, -1, 4222003, 4222005, 4222003, 4222003,
            4222015, 4222001, 4222003, 4222003, 4077003, 4077003, 4077003,
            4077003, 4077003, 4077003, 4077003, 4077003, 4077003, -1, -1,
            4077003, 4077004, -1, -1, -1, -1, 4077003, 2102000, 2102008,
            2102003, 2102003, 4222100, 4222100]
    entry = evaluate(real)
    check("echte Zeile wird erkannt", entry is not None)
    if entry:
        check("Praefix ist die Karosserie", entry["dominant_prefix"] == 4222)
        check("verbaute Teile gezaehlt", entry["installed"] == 29)

    data = struct.pack("<%di" % len(real), *real)
    found = runs_in_block(data, 0x1000, run_pattern(25))
    check("im Speicherblock auffindbar", len(found) == 1)

    check("kleine Stufen sind KEINE Teile-IDs mehr",
          evaluate([1, 2, 1, 2, 46, 1, 2] * 6) is None)
    check("lauter Nullen ergibt nichts", evaluate([0] * 40) is None)
    check("lauter -1 ergibt nichts", evaluate([-1] * 40) is None)
    scattered = [10001 + i * 100000 for i in range(30)]
    check("lauter verschiedene Praefixe sind kein Auto",
          evaluate(scattered) is None)
    check("zu wenige Teile werden verworfen",
          evaluate([4222002, 4222003, -1, -1] + [-1] * 30) is None)

    # Die Muster, die der erste Lauf am lebenden Spiel gemeldet hat.
    check("ein Wert 82x wiederholt ist kein Auto",
          evaluate([983055] * 82) is None)
    check("zwei Werte im Wechsel sind kein Auto",
          evaluate([33840, 45056] * 20) is None)
    check("echte Zeile ueberlebt die Wiederholungspruefung",
          evaluate(real) is not None)

    # Die drei Muster, die der Lauf IM RENNEN gemeldet hat -- alle drei Rauschen.
    check("durchgezaehlte Folge ist kein Auto",
          evaluate(list(range(393252, 393252 + 28))) is None)
    check("sortierte Tabelle ist kein Auto",
          evaluate([67139, 67303, 67490, 67703, 67892, 68037, 68208, 68292,
                    68463, 68637, 68698, 68704, 68805, 68806, 68817, 68828,
                    69001, 69123, 69345, 69567]) is None)
    check("viele verstreute Praefixe sind kein Auto",
          evaluate([2130893, 41040, 2128813, 43605, 33741, 43605, 33741,
                    43605, 2196494, 31000, 52000, 54000, 50776, 65365,
                    55323, 53274]) is None)

    print("\nalles bestanden" if ok else "\nFEHLGESCHLAGEN")
    return 0 if ok else 1


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--process", default="forzahorizon6")
    parser.add_argument("--pid", type=int, default=0)
    parser.add_argument("--min-run", type=int, default=25)
    parser.add_argument("--max-hits", type=int, default=40)
    parser.add_argument("--max-seconds", type=float, default=400.0)
    parser.add_argument("--out", type=Path,
                        default=Path("data/runtime/part_ids.json"))
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

    records = scan(pid, args.min_run, args.max_hits, args.max_seconds)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(records, indent=1), encoding="utf-8")
    coverage = [r for r in records if r.get("address") == "_coverage"][0]
    real = [r for r in records if r.get("address") != "_coverage"]
    print(f"{len(real)} Teilelisten-Kandidat(en) -> {args.out}")
    print(f"  Abdeckung: {coverage['scanned_gib']} GiB in {coverage['seconds']}s -- "
          + ("vollstaendig" if coverage["complete"] else "NICHT vollstaendig"))
    for entry in real[:12]:
        print(f"\n  {entry['address']}  {entry['installed']} Teile, "
              f"Hauptpraefix {entry['dominant_prefix']} "
              f"({entry['dominant_count']}x)")
        print(f"    Praefixe: "
              + ", ".join(f"{p['prefix']}({p['count']})" for p in entry["prefixes"]))
        print(f"    Werte: {entry['values'][:16]}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
