"""Share-Codes und Tune-Ids aus dem laufenden Spiel lesen.

## Warum das ueberhaupt geht

Eine Bestenlistenzeile (`ScoreboardRow`) traegt bei +372 ein `versionedTuneId` --
eine 16-Byte-Id, keine tippbare Nummer. Der Share-Code entsteht woanders
(`ForzaStorefront.GenerateShareCode(fileId) -> shareCode`), und diesen Dienst
koennen wir nicht rufen: die `bin/xtsw`-Antworten sind verschluesselt.

ABER: die Typtabelle des Spiels (860 Typen, aus dem entpackten Laufzeitabbild,
`data/network_probes/runtime_analysis/.../type_descriptors.json`) beschreibt
mehrere Datensaetze, die BEIDES nebeneinander halten -- `UGCItem`, `TuningItem`,
`ForzaStorefrontFile`, `ForzaStorefrontFileLightweightData`:

    UGCItem (kompakt, 552 B)        UGCItem (breit, 1648 B)
      +144  Id        versioned_id    +696  Id        versioned_id
      +184  ShareCode 10 Zeichen      +736  ShareCode 22 Byte
      +248  Creator   user(304)       +792  Creator   user(856)

**Der Abstand Id -> ShareCode ist in BEIDEN Fassungen genau 40 Byte.** Das ist die
Signatur, nach der dieses Skript sucht: eine Zeichenkette, die wie ein Share-Code
aussieht, und 40 Byte davor 16 Byte, die wie eine Id aussehen. Hat das Spiel
irgendeine UGC-Liste geladen (Tune-Suche, eigene Tunes, das Tune eines Rivalen),
liegt das Paar im Speicher -- ohne einen einzigen Serveraufruf.

`TuningItem` (1196 B) ist `UGCItem` + `TuningData`, traegt also zusaetzlich die
CarId bei +1176. Wo das passt, wird es mitgemeldet.

## Ehrlich zum Stand

Gemessen ist bisher nur der Zeilenteil: in dem einen rohen Abzug, den es gibt
(11 Zeilen um Rang 5.700), ist `versionedTuneId` in JEDER Zeile null. Ob die
Bestenliste die Tune-Id ueberhaupt fuehrt, ist offen; moeglicherweise kommt sie
erst am Rivalen-Detailpfad (`ScoreboardScoreData.versionedTuneId`, +80).
Dieses Skript beantwortet die andere Haelfte: existieren Id/Share-Code-Paare im
Speicher, und wie sehen sie aus.

    python scripts/find_tune_share_codes.py
    python scripts/find_tune_share_codes.py --match-id 0b0200...  # nur diese Id
    python scripts/find_tune_share_codes.py --self-test           # ohne Spiel

Laeuft im GAST, nicht auf dem Host: es liest den Speicher von forzahorizon6.exe.
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

# Der Abstand, der beide Fassungen verbindet: ShareCode steht 40 Byte hinter der Id.
ID_TO_SHARECODE = 40
ID_SIZE = 16

# Wo relativ zur Id die uebrigen Felder liegen, je Fassung. Welche live ist, sagt
# erst der Fund; die Zeilenstruktur nutzt die kompakte (user = 304 Byte), also ist
# sie die wahrscheinlichere.
LAYOUTS = {
    # Name          Id im Satz  Creator im Satz  CarId im Satz  Satzlaenge
    "compact": {"id": 144, "creator": 248, "car_id": 1176, "size": 1196},
    "wide": {"id": 696, "creator": 792, "car_id": None, "size": 1648},
}

# Ein Share-Code ist kurz und besteht aus Ziffern, in der Anzeige in Dreiergruppen.
# Grosszuegig gefasst, damit ein Buchstabencode nicht durchfaellt -- aber mit einer
# Mindestzahl an Ziffern, sonst faengt das Muster jedes kurze Wort im Speicher.
SHARE_CODE = re.compile(rb"^[0-9A-Z][0-9A-Z ]{4,11}$")
MIN_DIGITS = 5

# Dasselbe Muster als Sucher ueber einen ganzen Block: der Code steht am Anfang
# eines Feldes (davor eine Null oder der Blockanfang) und endet auf einer Null.
CANDIDATE = re.compile(rb"(?:^|\x00)([0-9A-Z][0-9A-Z ]{4,11})\x00")


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


def looks_like_share_code(raw: bytes) -> bool:
    if not SHARE_CODE.match(raw):
        return False
    return sum(1 for c in raw if 0x30 <= c <= 0x39) >= MIN_DIGITS


def looks_like_id(raw: bytes) -> bool:
    """16 Byte, die eine versionierte Id sein koennen.

    Nicht null, nicht lauter gleiche Bytes, und keine lesbare Zeichenkette -- sonst
    faengt die Suche zwei benachbarte Textfelder statt eines Datensatzes.
    """
    if len(raw) != ID_SIZE or not any(raw):
        return False
    if len(set(raw)) <= 2:
        return False
    printable = sum(1 for c in raw if 32 <= c < 127)
    return printable < 12


def ascii_at(data: bytes, offset: int, limit: int) -> str:
    end = data.find(b"\x00", offset, offset + limit)
    if end < 0:
        end = offset + limit
    raw = data[offset:end]
    if not raw or not all(32 <= c < 127 for c in raw):
        return ""
    return raw.decode("ascii")


def candidates_in_block(data: bytes, base_address: int) -> list[dict]:
    """Jede Stelle im Block finden, an der 40 Byte hinter einer Id ein Code steht."""
    found: list[dict] = []
    # Ueber die Zeichenkette suchen, nicht ueber die Id: von Codes gibt es eine
    # Handvoll, von Byte-Folgen, die wie eine Id aussehen, Millionen.
    #
    # UND ALS REGEX, nicht als Schleife: der Gast-Heap des Spiels ist ueber 3 GB.
    # Byteweise in Python waeren das Stunden, hier laeuft die Suche in C und der
    # Rest nur noch auf den paar Dutzend Treffern.
    for match in CANDIDATE.finditer(data):
        hit = match.start(1)
        text = match.group(1)
        if not looks_like_share_code(text):
            continue
        id_offset = hit - ID_TO_SHARECODE
        if id_offset < 0:
            continue
        identifier = data[id_offset:id_offset + ID_SIZE]
        if not looks_like_id(identifier):
            continue
        entry = {
            "address": hex(base_address + id_offset),
            "versioned_id": identifier.hex(),
            "share_code": ascii_at(data, hit, 12),
            "layouts": {},
        }
        for name, layout in LAYOUTS.items():
            record = id_offset - layout["id"]
            if record < 0 or record + layout["size"] > len(data):
                continue
            reading = {}
            creator = record + layout["creator"]
            # Im user-Objekt: xuid bei +8, Gamertag bei +16 -- so wie in der Zeile.
            reading["creator_xuid"] = struct.unpack_from("<Q", data, creator + 8)[0]
            reading["creator_gamertag"] = ascii_at(data, creator + 16, 16)
            if layout["car_id"] is not None:
                reading["car_id"] = struct.unpack_from("<i", data, record + layout["car_id"])[0]
            entry["layouts"][name] = reading
        # Eine Einschaetzung, kein Urteil: passt in der kompakten Fassung ein
        # lesbarer Erstellername UND eine CarId in Reichweite des Fuhrparks, ist es
        # sehr wahrscheinlich ein echter UGC-Satz und kein Texturname. Ueber das
        # Laufzeitabbild (188 MB reiner Code) liefen 11 Fehltreffer durch, alle ohne
        # diese beiden Beigaben.
        compact = entry["layouts"].get("compact", {})
        entry["plausible"] = bool(
            compact.get("creator_gamertag")
            and 1 <= int(compact.get("car_id") or 0) <= 30000)
        found.append(entry)
    return found


def readable_regions(process: int) -> Iterable[tuple[int, int]]:
    address = 0
    maximum = 0x00007FFFFFFFFFFF
    mbi = MemoryBasicInformation()
    while address < maximum:
        if not kernel32.VirtualQueryEx(process, ctypes.c_void_p(address),
                                       ctypes.byref(mbi), ctypes.sizeof(mbi)):
            break
        base = int(mbi.BaseAddress or 0)
        size = int(mbi.RegionSize)
        if base + size <= address:
            break
        readable = (mbi.State == MEM_COMMIT and mbi.Type == MEM_PRIVATE
                    and not (mbi.Protect & PAGE_NOACCESS)
                    and not (mbi.Protect & PAGE_GUARD))
        if readable and size >= 4096:
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


def find_process(name: str) -> int:
    import subprocess
    out = subprocess.run(
        ["powershell.exe", "-NoProfile", "-Command",
         f"(Get-Process -Name '{name}' -ErrorAction SilentlyContinue).Id"],
        capture_output=True, text=True)
    digits = "".join(c for c in out.stdout if c.isdigit())
    return int(digits) if digits else 0


def scan(pid: int, match_id: str | None, chunk: int = 4 << 20) -> list[dict]:
    process = kernel32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ,
                                   False, pid)
    if not process:
        raise SystemExit(f"OpenProcess auf {pid} fehlgeschlagen "
                         f"(Fehler {ctypes.get_last_error()})")
    seen: set[tuple[str, str]] = set()
    results: list[dict] = []
    try:
        for base, size in readable_regions(process):
            offset = 0
            while offset < size:
                # Ueberlappend lesen, sonst faellt ein Satz auf der Blockgrenze raus.
                length = min(chunk, size - offset)
                data = read_memory(process, base + offset, length + LAYOUTS["wide"]["size"])
                if not data:
                    offset += length
                    continue
                for entry in candidates_in_block(data, base + offset):
                    key = (entry["versioned_id"], entry["share_code"])
                    if key in seen:
                        continue
                    seen.add(key)
                    if match_id and not entry["versioned_id"].startswith(match_id.lower()):
                        continue
                    results.append(entry)
                offset += length
    finally:
        kernel32.CloseHandle(process)
    return results


def self_test() -> int:
    """Einen Satz bauen, wie die Typtabelle ihn beschreibt, und ihn wiederfinden."""
    layout = LAYOUTS["compact"]
    blob = bytearray(b"\x00" * (layout["size"] + 256))
    identifier = bytes.fromhex("0b0200001af9350a4d7c11e900ab34cd")
    blob[layout["id"]:layout["id"] + ID_SIZE] = identifier
    blob[layout["id"] + ID_TO_SHARECODE:layout["id"] + ID_TO_SHARECODE + 10] = b"123456789\x00"
    struct.pack_into("<Q", blob, layout["creator"] + 8, 2535400000000002)
    blob[layout["creator"] + 16:layout["creator"] + 16 + 8] = b"KaiTunes"
    struct.pack_into("<i", blob, layout["car_id"], 4210)

    found = candidates_in_block(bytes(blob), 0x140000000)
    ok = True

    def check(name: str, condition: bool, detail: str = "") -> None:
        nonlocal ok
        print(f"  {'ok   ' if condition else 'FAIL '} {name}"
              f"{('  -- ' + detail) if detail and not condition else ''}")
        ok = ok and condition

    check("genau ein Satz gefunden", len(found) == 1, f"{len(found)}")
    if found:
        entry = found[0]
        check("die Id stimmt", entry["versioned_id"] == identifier.hex(),
              entry["versioned_id"])
        check("der Share-Code stimmt", entry["share_code"] == "123456789",
              entry["share_code"])
        compact = entry["layouts"].get("compact", {})
        check("Gamertag des Erstellers", compact.get("creator_gamertag") == "KaiTunes",
              str(compact.get("creator_gamertag")))
        check("CarId", compact.get("car_id") == 4210, str(compact.get("car_id")))

    # Und was NICHT gefunden werden darf: ein Code ohne Id davor.
    lonely = bytearray(b"\x00" * 600)
    lonely[200:210] = b"987654321\x00"
    check("ein Code ohne Id davor wird nicht gemeldet",
          not candidates_in_block(bytes(lonely), 0))

    print("\nalles bestanden" if ok else "\nFEHLGESCHLAGEN")
    return 0 if ok else 1


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--process", default="forzahorizon6")
    parser.add_argument("--pid", type=int, default=0)
    parser.add_argument("--match-id", default=None,
                        help="nur Saetze, deren versionedTuneId so beginnt (hex)")
    parser.add_argument("--out", type=Path,
                        default=Path("data/runtime/tune_share_codes.json"))
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()

    if args.self_test:
        return self_test()
    if sys.platform != "win32":
        raise SystemExit("liest Prozessspeicher -- laeuft nur unter Windows")

    pid = args.pid or find_process(args.process)
    if not pid:
        raise SystemExit(f"{args.process} laeuft nicht in dieser Sitzung")
    print(f"scanne {args.process} (pid {pid})")
    results = scan(pid, args.match_id)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(results, indent=1), encoding="utf-8")
    print(f"{len(results)} Id/Share-Code-Paar(e) -> {args.out}")
    for entry in results[:20]:
        compact = entry["layouts"].get("compact", {})
        print(f"  {entry['share_code']:<12} {entry['versioned_id']}"
              f"  car={compact.get('car_id')}  by={compact.get('creator_gamertag')!r}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
