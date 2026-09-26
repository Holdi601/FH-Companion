"""Die Rivalen-Struktur im Speicher suchen -- traegt SIE das Tune?

## Die Frage

Die Bestenlistenzeile fuehrt kein Tune; das ist an 311 Zeilen gemessen. Uebrig
bleibt `ScoreboardScoreData` (112 Byte), die Struktur hinter `ScoreboardRival` --
also der Datensatz des AUSGEWAEHLTEN Rivalen. Laut Typtabelle:

    +24  carId          uint16      +56  carPerformanceIndex  float32
    +28  garageId       int32       +60..+72  dreizehn bools
    +40  carPower       float32     +76  numberOfCarsPassed   int32
    +44  carTorque      float32     +80  versionedTuneId      16 Byte
    +48  carWeight      float32     +96  versionedLiveryId    16 Byte
    +52  carClassId     uint8

Ist +80 gefuellt, gibt es einen Weg zum Tune -- einen Rivalen zur Zeit, aber es
gibt ihn. Ist es leer, ist das Thema zu Ende.

## Warum das NEBEN dem Sweep laufen darf

Es liest nur. Kein Tastendruck, keine Navigation, keine Menuefuehrung -- damit
streitet es sich nicht mit dem Sweep um das eine Spiel. Es kostet ein paar
Sekunden Gast-CPU.

## Wie gesucht wird

Anker sind die dreizehn Wahrheitswerte bei +60: `[\\x00\\x01]{13}` findet der
Regex in C-Tempo. Erst auf den Treffern wird geprueft, ob Klasse, PI, CarId und
die drei Fahrzeugwerte zusammenpassen -- das haelt die Fehltreffer klein, ohne
den Heap byteweise anzufassen.

    python scripts/find_rival_score_data.py
    python scripts/find_rival_score_data.py --self-test
"""

from __future__ import annotations

import argparse
import ctypes
import json
import re
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

BOOLS_AT = 60
BOOLS_LEN = 13
RECORD_SIZE = 112
CAR_ID_AT = 24
CLASS_AT = 52
PI_AT = 56
POWER_AT = 40
TORQUE_AT = 44
WEIGHT_AT = 48
TUNE_AT = 80
LIVERY_AT = 96

ANCHOR = re.compile(rb"[\x00\x01]{%d}" % BOOLS_LEN)


class MemoryBasicInformation(ctypes.Structure):
    _fields_ = [
        ("BaseAddress", ctypes.c_void_p), ("AllocationBase", ctypes.c_void_p),
        ("AllocationProtect", wintypes.DWORD), ("PartitionId", wintypes.WORD),
        ("RegionSize", ctypes.c_size_t), ("State", wintypes.DWORD),
        ("Protect", wintypes.DWORD), ("Type", wintypes.DWORD),
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


def plausible(record: bytes) -> bool:
    """Passt der Zahlenteil zu einem echten Fahrzeugdatensatz?

    Die Werte sind absichtlich weit gefasst: die Einheiten von Leistung, Drehmoment
    und Gewicht sind nicht kalibriert, gesucht wird nur "nicht null und nicht
    absurd". Die harte Pruefung machen Klasse und PI, weil beide einen bekannten,
    engen Wertebereich haben.
    """
    if len(record) < RECORD_SIZE:
        return False
    car_id = struct.unpack_from("<H", record, CAR_ID_AT)[0]
    if not 1 <= car_id <= 30000:
        return False
    if record[CLASS_AT] > 7:
        return False
    pi = struct.unpack_from("<f", record, PI_AT)[0]
    # NICHT 0.1..1.05, und nicht exakt 1.0. Der erste Lauf am lebenden Spiel holte
    # damit 5.327 Treffer, von denen 5.298 den PI-Wert exakt 1000 trugen: die
    # Gleitkommazahl 1.0 liegt im Heap millionenfach herum (Skalierungen, Alpha,
    # Normalen), und mit ihr kommt jeder Nachbarblock durch. Ein echter PI liegt
    # zwischen 100 und 999 -- 1.0 ist deshalb ein Ausschluss, kein Grenzfall.
    if not 0.1 <= pi < 0.9995:
        return False
    power, torque, weight = struct.unpack_from("<3f", record, POWER_AT)
    for value in (power, torque, weight):
        if not 10.0 <= value <= 20000.0:
            return False
    # Drei identische Zahlen sind ein Muster, kein Fahrzeug.
    if power == torque or torque == weight or power == weight:
        return False
    return True


def records_in_block(data: bytes, base_address: int) -> list[dict]:
    found: list[dict] = []
    for match in ANCHOR.finditer(data):
        start = match.start() - BOOLS_AT
        if start < 0 or start + RECORD_SIZE > len(data):
            continue
        record = data[start:start + RECORD_SIZE]
        if not plausible(record):
            continue
        tune = record[TUNE_AT:TUNE_AT + 16]
        livery = record[LIVERY_AT:LIVERY_AT + 16]
        found.append({
            "address": hex(base_address + start),
            "car_id": struct.unpack_from("<H", record, CAR_ID_AT)[0],
            "car_class_id": record[CLASS_AT],
            "performance_index": round(struct.unpack_from("<f", record, PI_AT)[0] * 1000, 1),
            "versioned_tune_id": tune.hex() if any(tune) else "",
            "versioned_livery_id": livery.hex() if any(livery) else "",
        })
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


def scan(pid: int, chunk: int = 8 << 20) -> list[dict]:
    process = kernel32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ,
                                   False, pid)
    if not process:
        raise SystemExit(f"OpenProcess auf {pid} fehlgeschlagen")
    out: list[dict] = []
    seen: set[str] = set()
    try:
        for base, size in readable_regions(process):
            offset = 0
            while offset < size:
                length = min(chunk, size - offset)
                data = read_memory(process, base + offset, length + RECORD_SIZE)
                if data:
                    for entry in records_in_block(data, base + offset):
                        key = f"{entry['car_id']}|{entry['versioned_tune_id']}|{entry['performance_index']}"
                        if key in seen:
                            continue
                        seen.add(key)
                        out.append(entry)
                offset += length
    finally:
        kernel32.CloseHandle(process)
    return out


def self_test() -> int:
    record = bytearray(RECORD_SIZE)
    struct.pack_into("<H", record, CAR_ID_AT, 4210)
    record[CLASS_AT] = 6
    struct.pack_into("<f", record, PI_AT, 0.8862)
    struct.pack_into("<3f", record, POWER_AT, 480.0, 520.0, 1310.0)
    for i in range(BOOLS_AT, BOOLS_AT + BOOLS_LEN):
        record[i] = i % 2
    tune = bytes.fromhex("0b0200001af9350a4d7c11e900ab34cd")
    record[TUNE_AT:TUNE_AT + 16] = tune

    blob = bytes(64) + bytes(record) + bytes(64)
    found = records_in_block(blob, 0x140000000)
    ok = True

    def check(name: str, condition: bool, detail: str = "") -> None:
        nonlocal ok
        print(f"  {'ok   ' if condition else 'FAIL '} {name}"
              f"{('  -- ' + detail) if detail and not condition else ''}")
        ok = ok and condition

    check("ein Satz gefunden", len(found) == 1, str(len(found)))
    if found:
        check("CarId", found[0]["car_id"] == 4210, str(found[0]["car_id"]))
        check("PI", abs(found[0]["performance_index"] - 886.2) < 0.5,
              str(found[0]["performance_index"]))
        check("Tune-Id", found[0]["versioned_tune_id"] == tune.hex())
    check("leerer Speicher liefert nichts", not records_in_block(bytes(4096), 0))
    print("\nalles bestanden" if ok else "\nFEHLGESCHLAGEN")
    return 0 if ok else 1


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--process", default="forzahorizon6")
    parser.add_argument("--pid", type=int, default=0)
    parser.add_argument("--out", type=Path,
                        default=Path("data/runtime/rival_score_data.json"))
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

    records = scan(pid)
    with_tune = [r for r in records if r["versioned_tune_id"]]
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(records, indent=1), encoding="utf-8")
    print(f"{len(records)} Fahrzeugdatensaetze, davon {len(with_tune)} mit Tune-Id"
          f" -> {args.out}")
    for entry in (with_tune or records)[:15]:
        print(f"  car={entry['car_id']:<6} class={entry['car_class_id']}"
              f" PI={entry['performance_index']:<7}"
              f" tune={entry['versioned_tune_id'][:24] or '-'}"
              f" livery={entry['versioned_livery_id'][:16] or '-'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
