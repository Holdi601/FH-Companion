"""Den Tune-Datensatz ueber BEKANNTE Werte finden -- die Eichung statt der Vermutung.

## Warum es das gibt

Am 2026-08-30 habe ich dreimal hintereinander nach einer Struktur gesucht, wie die
Typtabelle des Spiels sie beschreibt (`TuningData`, `TuningItem`, `CInstalledParts`)
-- und dreimal nichts oder Muell gefunden, bei vollstaendig abgesuchtem Speicher und
mit sichtbar fahrendem Geist. Der Verdacht liegt nahe, dass die Typtabelle die
SERIALISIERUNG beschreibt und nicht die Form im Arbeitsspeicher.

Also andersherum: der Nutzer oeffnet im Spiel das Tuning-Menue und liest zwei Zahlen
ab, die dort schwarz auf weiss stehen -- Reifendruck vorn 2,1 und hinten 2,3 BAR.
Diese beiden Gleitkommazahlen MUESSEN im Speicher stehen. Wo sie in der richtigen
Beziehung zueinander liegen, liegt der Tune-Datensatz.

**Der Abstand wird gemessen, nicht angenommen.** Die Typtabelle sagt 56 Byte
(m_FrontSuspTire bei +56, m_RearSuspTire bei +112). Ob das im Speicher auch so ist,
ist genau die Frage -- deshalb sucht dieses Skript in einem Fenster und meldet, in
welchem Abstand das Paar tatsaechlich vorkommt.

## Lesart des Ergebnisses

Kommt ein Abstand haeufig vor und passt er zu einer der bekannten Strukturen, ist die
Form gefunden. Kommt das Paar gar nicht vor, stehen die Werte anders im Speicher
(skaliert, als Ganzzahl in Zehnteln, oder in psi umgerechnet) -- auch das ist eine
Antwort, und das Skript probiert die naheliegenden Umrechnungen gleich mit.

    python scripts/find_tune_by_values.py --front 2.1 --rear 2.3
    python scripts/find_tune_by_values.py --front 2.1 --rear 2.3 --window 512
    python scripts/find_tune_by_values.py --self-test

Laeuft im GAST. Rein lesend.
"""

from __future__ import annotations

import argparse
import collections
import ctypes
import json
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


def encodings_of(value: float) -> list[tuple[str, bytes]]:
    """Alle Fassungen, in denen so ein Wert im Speicher stehen koennte.

    Der Bildschirm zeigt 2,1 BAR. Das Spiel kann das als Gleitkommazahl 2.1 halten,
    als 21 in Zehnteln, oder intern in psi rechnen (1 bar = 14,5038 psi) -- ein
    Fehlschlag der ersten Fassung waere sonst nicht von "gar nicht da" zu
    unterscheiden.
    """
    out = [
        ("f32", struct.pack("<f", value)),
        ("f64", struct.pack("<d", value)),
        ("f32_psi", struct.pack("<f", value * 14.5037738)),
    ]
    tenths = round(value * 10)
    if abs(tenths - value * 10) < 1e-6:
        out.append(("i32_tenths", struct.pack("<i", tenths)))
    return out


def float_window(value: float, tolerance: float) -> tuple[list[bytes], float, float]:
    """Vorfilter fuer eine Gleitkommazahl MIT Toleranz.

    Exakte Byte-Gleichheit war der Fehler des ersten Versuchs: steht im Speicher
    2.0999999 statt 2.1, oder ein in psi umgerechneter und gerundeter Wert, trifft
    ein Byte-Vergleich nie -- und das Ergebnis "nicht gefunden" haette dann nichts
    mit der Frage zu tun.

    Positive float32 sind in ihrem Bitmuster monoton: alle Werte eines engen
    Bereichs teilen sich die oberen zwei Bytes. Die lassen sich als billiger
    Vorfilter suchen, danach wird der Wert genau geprueft.
    """
    lo, hi = value - tolerance, value + tolerance
    lo_bits = struct.unpack("<I", struct.pack("<f", lo))[0]
    hi_bits = struct.unpack("<I", struct.pack("<f", hi))[0]
    if hi_bits < lo_bits:
        lo_bits, hi_bits = hi_bits, lo_bits
    # NICHT nur EIN Praefix: 2,1 +/- 0,02 laeuft von 0x4005.. bis 0x400F.., teilt
    # sich also die oberen zwei Bytes nicht. Die erste Fassung gab dann gar keinen
    # Vorfilter zurueck und fiel auf das Absuchen jeder 4-Byte-Stelle zurueck -- ueber
    # 5,7 GiB waere das nie fertig geworden. Ein knappes Dutzend Praefixe ist
    # dagegen so billig wie eines.
    prefixes = []
    for high in range(lo_bits >> 16, (hi_bits >> 16) + 1):
        prefixes.append(struct.pack("<I", high << 16)[2:])
        if len(prefixes) > 64:      # zu weite Toleranz -- dann lieber ohne Vorfilter
            return [], lo, hi
    return prefixes, lo, hi


def find_float_near(data: bytes, value: float, tolerance: float) -> list[int]:
    """Alle 4-Byte-ausgerichteten Stellen, an denen ein float32 nahe 'value' steht."""
    prefixes, lo, hi = float_window(value, tolerance)
    out: list[int] = []
    if prefixes:
        for prefix in prefixes:
            start = 0
            while True:
                at = data.find(prefix, start)
                if at < 0:
                    break
                start = at + 1
                begin = at - 2
                if begin < 0 or begin % 4 or begin + 4 > len(data):
                    continue
                found = struct.unpack_from("<f", data, begin)[0]
                if lo <= found <= hi:
                    out.append(begin)
    else:
        for begin in range(0, len(data) - 4, 4):
            found = struct.unpack_from("<f", data, begin)[0]
            if lo <= found <= hi:
                out.append(begin)
    return sorted(set(out))


def tolerant_pairs(data: bytes, base: int, front: float, rear: float,
                   tolerance: float, window: int) -> list[dict]:
    found: list[dict] = []
    rear_positions = find_float_near(data, rear, tolerance)
    if not rear_positions:
        return found
    rear_set = sorted(rear_positions)
    import bisect
    for at in find_float_near(data, front, tolerance):
        left = bisect.bisect_left(rear_set, at - window)
        right = bisect.bisect_right(rear_set, at + window)
        for other in rear_set[left:right]:
            if other == at:
                continue
            found.append({
                "front_at": hex(base + at),
                "distance": other - at,
                "front_value": round(struct.unpack_from("<f", data, at)[0], 5),
                "rear_value": round(struct.unpack_from("<f", data, other)[0], 5),
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


def pairs_in_block(data: bytes, base: int, front: bytes, rear: bytes,
                   window: int) -> list[dict]:
    """Jedes Vorkommen von 'front', bei dem 'rear' im Fenster liegt."""
    found: list[dict] = []
    start = 0
    while True:
        at = data.find(front, start)
        if at < 0:
            break
        start = at + 1
        lo = max(0, at - window)
        hi = min(len(data), at + window + len(rear))
        chunk = data[lo:hi]
        pos = chunk.find(rear)
        while pos >= 0:
            distance = (lo + pos) - at
            if distance != 0:
                found.append({
                    "front_at": hex(base + at),
                    "distance": distance,
                })
            pos = chunk.find(rear, pos + 1)
    return found


def floats_around(data: bytes, at: int, before: int, after: int) -> list[float]:
    values: list[float] = []
    lo = max(0, at - before)
    for offset in range(lo, min(len(data) - 4, at + after), 4):
        values.append(round(struct.unpack_from("<f", data, offset)[0], 4))
    return values


def scan(pid: int, front: float, rear: float, window: int, max_hits: int,
         max_seconds: float, tolerance: float = 0.02,
         chunk: int = 8 << 20) -> dict:
    process = kernel32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ,
                                   False, pid)
    if not process:
        raise SystemExit(f"OpenProcess auf {pid} fehlgeschlagen")
    hits: list[dict] = []
    distances: collections.Counter = collections.Counter()
    deadline = time.monotonic() + max_seconds
    started = time.monotonic()
    scanned = 0
    truncated = False
    front_value, rear_value = front, rear
    front_forms = encodings_of(front)
    rear_forms = dict(encodings_of(rear))
    try:
        for base, size in readable_regions(process):
            offset = 0
            while offset < size:
                if time.monotonic() > deadline:
                    truncated = True
                    break
                length = min(chunk, size - offset)
                data = read_memory(process, base + offset, length + window + 8)
                scanned += len(data)
                if data:
                    # Der tolerante Gleitkomma-Weg ist der eigentliche; die exakten
                    # Byte-Fassungen laufen nur noch als Gegenprobe mit.
                    for hit in tolerant_pairs(data, base + offset, front_value,
                                              rear_value, tolerance, window):
                        hit["encoding"] = "f32_tolerant"
                        distances[("f32_tolerant", hit["distance"])] += 1
                        if len(hits) < max_hits:
                            at = int(hit["front_at"], 16) - (base + offset)
                            hit["floats_around"] = floats_around(data, at, 64, 200)
                            hits.append(hit)
                    for name, needle in front_forms:
                        partner = rear_forms.get(name)
                        if not partner or name == "f32":
                            continue
                        for hit in pairs_in_block(data, base + offset, needle,
                                                  partner, window):
                            hit["encoding"] = name
                            distances[(name, hit["distance"])] += 1
                offset += length
            if truncated:
                break
    finally:
        kernel32.CloseHandle(process)
    return {
        "hits": hits,
        "distance_histogram": [
            {"encoding": key[0], "distance": key[1], "count": count}
            for key, count in distances.most_common(25)
        ],
        "scanned_gib": round(scanned / (1 << 30), 2),
        "seconds": round(time.monotonic() - started, 1),
        "complete": not truncated,
    }


def self_test() -> int:
    ok = True

    def check(label: str, condition: bool) -> None:
        nonlocal ok
        print(f"  {'ok  ' if condition else 'FEHL'}  {label}")
        ok = ok and condition

    front = struct.pack("<f", 2.1)
    rear = struct.pack("<f", 2.3)
    data = bytearray(400)
    data[100:104] = front
    data[156:160] = rear          # Abstand 56, wie die Typtabelle behauptet
    found = pairs_in_block(bytes(data), 0x1000, front, rear, 256)
    check("Paar gefunden", len(found) == 1)
    check("Abstand gemessen", found and found[0]["distance"] == 56)
    check("Adresse stimmt", found and found[0]["front_at"] == hex(0x1000 + 100))

    check("ohne Partner kein Treffer",
          not pairs_in_block(bytes(400), 0x1000, front, rear, 256))

    far = bytearray(2000)
    far[100:104] = front
    far[1500:1504] = rear
    check("ausserhalb des Fensters kein Treffer",
          not pairs_in_block(bytes(far), 0, front, rear, 256))

    names = [n for n, _ in encodings_of(2.1)]
    check("vier Fassungen werden probiert",
          names == ["f32", "f64", "f32_psi", "i32_tenths"])
    check("psi-Fassung rechnet um",
          encodings_of(1.0)[2][1] == struct.pack("<f", 14.5037738))
    check("Zehntel-Fassung stimmt",
          encodings_of(2.1)[3][1] == struct.pack("<i", 21))

    # Der Toleranzweg -- der eigentliche. Exakte Bytes trafen im Spiel nichts.
    data2 = bytearray(400)
    struct.pack_into("<f", data2, 100, 2.0999999)
    struct.pack_into("<f", data2, 156, 2.3000002)
    tol = tolerant_pairs(bytes(data2), 0x1000, 2.1, 2.3, 0.02, 256)
    check("leicht abweichende Werte werden gefunden", len(tol) == 1)
    check("Abstand auch hier gemessen", tol and tol[0]["distance"] == 56)
    prefixes, _, _ = float_window(2.1, 0.02)
    check("Vorfilter deckt den ganzen Bereich ab", len(prefixes) >= 2)
    check("Vorfilter findet den Wert",
          100 in find_float_near(bytes(data2), 2.1, 0.02))
    check("weit entfernter Wert wird nicht gefunden",
          not find_float_near(bytes(data2), 9.9, 0.02))

    print("\nalles bestanden" if ok else "\nFEHLGESCHLAGEN")
    return 0 if ok else 1


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--front", type=float, default=2.1)
    parser.add_argument("--rear", type=float, default=2.3)
    parser.add_argument("--window", type=int, default=512)
    parser.add_argument("--tolerance", type=float, default=0.02)
    parser.add_argument("--max-hits", type=int, default=25)
    parser.add_argument("--max-seconds", type=float, default=400.0)
    parser.add_argument("--process", default="forzahorizon6")
    parser.add_argument("--pid", type=int, default=0)
    parser.add_argument("--out", type=Path,
                        default=Path("data/runtime/tune_by_values.json"))
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

    result = scan(pid, args.front, args.rear, args.window, args.max_hits,
                  args.max_seconds, args.tolerance)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(result, indent=1), encoding="utf-8")
    print(f"{len(result['hits'])} Paar(e) gezeigt, "
          f"{result['scanned_gib']} GiB in {result['seconds']}s -- "
          + ("vollstaendig" if result["complete"] else "NICHT vollstaendig")
          + f" -> {args.out}")
    print("\nHaeufigste Abstaende (das ist die gesuchte Form):")
    for row in result["distance_histogram"][:12]:
        print(f"  {row['encoding']:<11} Abstand {row['distance']:>6}  "
              f"{row['count']}x")
    for hit in result["hits"][:5]:
        print(f"\n  {hit['front_at']} ({hit['encoding']}), Abstand {hit['distance']}")
        print(f"    Floats drumherum: {hit['floats_around'][:26]}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
