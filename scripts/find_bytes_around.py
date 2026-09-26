"""Eine Zeichenkette im Spielspeicher suchen und zeigen, was drumherum steht.

## Warum es das gibt

Bisher wurde im Speicher immer nach VERMUTETEN Offsets gesucht: "ScoreboardScoreData
ist 112 Byte gross, die Tune-Id steht bei +80". Findet so ein Suchlauf nichts, sind
zwei sehr verschiedene Dinge moeglich -- die Daten sind nicht da, ODER die Offsets
stimmen fuer diese Spielfassung nicht. Aus einem Fehlschlag laesst sich das nicht
unterscheiden, und deshalb traegt er auch keine Aussage.

Dieses Werkzeug dreht die Richtung um. Es sucht nach etwas, das wir SICHER kennen --
dem Gamertag des ausgewaehlten Rivalen, wie er auf dem Schirm steht -- und zeigt die
Bytes drumherum. Was dort liegt, liegt dort; keine Annahme noetig. Steht in der Naehe
eine 16-Byte-Id oder ein Share-Code, sieht man es. Steht dort nichts dergleichen, ist
das eine echte Antwort und nicht bloss ein misslungener Rateversuch.

## Was es meldet

Je Fundstelle das Fenster als Hex mit lesbarer Spalte, dazu zwei Auswertungen:

* **Kurzstrings** im Fenster -- so faellt ein Tune-Name oder ein Erstellername auf,
  auch wenn er an einer voellig anderen Stelle liegt als gedacht.
* **Id-Kandidaten**: 16 Byte am Stueck, die weder lauter Nullen noch Text sind und
  genug verschiedene Werte enthalten, um eine Guid/versioned_id sein zu koennen.

Gesucht wird in beiden Kodierungen: ASCII/UTF-8 und UTF-16LE. Spiele halten
Anzeigetexte oft als UTF-16, Netzwerkdaten als UTF-8 -- und welche Fassung ein
Gamertag gerade hat, ist genau die Art Annahme, die dieses Werkzeug vermeiden soll.

    python scripts/find_bytes_around.py --text SkokuNaFide
    python scripts/find_bytes_around.py --text SkokuNaFide --before 256 --after 512
    python scripts/find_bytes_around.py --self-test

Laeuft im GAST, nicht auf dem Host: es liest den Speicher von forzahorizon6.exe.
Es liest nur -- kein Tastendruck, keine Navigation.
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

# Ein Fenster von 128 Byte davor und 384 danach deckt jede der bekannten Strukturen
# ab: ScoreboardScoreData ist 112 Byte gross, ScoreboardRow 400+, und im UGCItem
# liegen Id, Share-Code und Ersteller innerhalb von rund 250 Byte beieinander.
DEFAULT_BEFORE = 128
DEFAULT_AFTER = 384

PRINTABLE = re.compile(rb"[ -~]{4,}")
# Ein Share-Code, wie ihn das Spiel anzeigt: kurz, Ziffern und Grossbuchstaben.
SHARE_CODE = re.compile(rb"(?:^|\x00)([0-9A-Z][0-9A-Z ]{4,11})\x00")


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


def id_candidates(window: bytes, window_base: int) -> list[dict]:
    """16-Byte-Bloecke, die eine versioned_id / Guid sein KOENNTEN.

    Ausgeschlossen wird, was sicher keine ist: lauter Nullen, lauter gleiche Bytes,
    und alles, was sich als Text liest (ein Name ist keine Id). Uebrig bleiben
    Kandidaten, keine Treffer -- die Entscheidung faellt beim Ansehen.
    """
    found: list[dict] = []
    for offset in range(0, max(0, len(window) - 16) + 1, 4):
        block = window[offset:offset + 16]
        if len(block) < 16:
            break
        if not any(block):
            continue
        if len(set(block)) < 6:
            continue
        # Text ist keine Id: mehr als zwoelf druckbare Zeichen am Stueck spricht
        # dagegen, ebenso eine UTF-16-Kette (jedes zweite Byte null).
        printable = sum(1 for b in block if 0x20 <= b <= 0x7E)
        if printable >= 12:
            continue
        if all(block[i] == 0 for i in range(1, 16, 2)):
            continue
        found.append({"at": window_base + offset, "hex": block.hex()})
    return found


def strings_in(window: bytes) -> list[str]:
    out: list[str] = []
    for match in PRINTABLE.finditer(window):
        out.append(match.group().decode("ascii", "replace"))
    # UTF-16LE zusaetzlich: jedes zweite Byte null herausnehmen und erneut lesen.
    try:
        wide = window.decode("utf-16-le", "ignore")
    except Exception:
        wide = ""
    for match in re.finditer(r"[ -~]{4,}", wide):
        out.append(match.group())
    seen: set[str] = set()
    unique: list[str] = []
    for item in out:
        if item not in seen:
            seen.add(item)
            unique.append(item)
    return unique


def hexdump(window: bytes, base: int, limit: int = 512) -> list[str]:
    lines: list[str] = []
    for offset in range(0, min(len(window), limit), 16):
        chunk = window[offset:offset + 16]
        text = "".join(chr(b) if 0x20 <= b <= 0x7E else "." for b in chunk)
        lines.append(f"{base + offset:012x}  {chunk.hex(' '):<47}  {text}")
    return lines


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


def needles(text: str) -> list[tuple[str, bytes]]:
    return [("utf8", text.encode("utf-8")),
            ("utf16le", text.encode("utf-16-le"))]


def number_needles(value: float) -> list[tuple[str, bytes]]:
    """Eine bekannte ZAHL als Suchmuster, in allen Fassungen, die in Frage kommen.

    Warum das noetig wurde: dreimal hintereinander habe ich eine Struktur nach ihrer
    vermuteten Form gesucht und dreimal Muell oder nichts gefunden. Eine Zahl, die
    nachweislich auf dem Schirm steht -- Leistung, Gewicht, eine Rundenzeit -- ist
    dagegen ein Anker ohne jede Annahme. Findet man sie, weiss man, wo der Datensatz
    des Autos liegt, und kann von dort aus lesen, statt zu raten.
    """
    out: list[tuple[str, bytes]] = [
        ("float32", struct.pack("<f", float(value))),
        ("float64", struct.pack("<d", float(value))),
    ]
    if float(value).is_integer() and -2_147_483_648 <= value <= 2_147_483_647:
        out.append(("int32", struct.pack("<i", int(value))))
    return out


def search(pid: int, text: str, before: int, after: int,
           max_hits: int, number: float | None = None,
           chunk: int = 8 << 20) -> list[dict]:
    process = kernel32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ,
                                   False, pid)
    if not process:
        raise SystemExit(f"OpenProcess auf {pid} fehlgeschlagen")
    patterns = number_needles(number) if number is not None else needles(text)
    longest = max(len(raw) for _, raw in patterns)
    hits: list[dict] = []
    try:
        for base, size in readable_regions(process):
            offset = 0
            while offset < size and len(hits) < max_hits:
                length = min(chunk, size - offset)
                # Ueberlappung, damit ein Treffer an der Blockgrenze nicht zerfaellt.
                data = read_memory(process, base + offset, length + longest)
                if data:
                    for encoding, raw in patterns:
                        start = 0
                        while len(hits) < max_hits:
                            found = data.find(raw, start)
                            if found < 0:
                                break
                            start = found + 1
                            address = base + offset + found
                            lo = max(0, found - before)
                            hi = min(len(data), found + len(raw) + after)
                            window = data[lo:hi]
                            window_base = base + offset + lo
                            hits.append({
                                "address": hex(address),
                                "encoding": encoding,
                                "window_at": hex(window_base),
                                "strings": strings_in(window),
                                "share_code_like": [
                                    m.group(1).decode("ascii", "replace")
                                    for m in SHARE_CODE.finditer(window)],
                                "id_candidates": [
                                    {"at": hex(c["at"]), "hex": c["hex"]}
                                    for c in id_candidates(window, window_base)],
                                "hexdump": hexdump(window, window_base),
                            })
                offset += length
            if len(hits) >= max_hits:
                break
    finally:
        kernel32.CloseHandle(process)
    return hits


def self_test() -> int:
    """Ohne Spiel pruefbar: die Auswertung eines Fensters, nicht das Lesen fremden
    Speichers. Genau die Teile, in denen ein stiller Denkfehler stecken kann."""
    ok = True

    def check(label: str, condition: bool) -> None:
        nonlocal ok
        print(f"  {'ok  ' if condition else 'FEHL'}  {label}")
        ok = ok and condition

    window = (b"\x00" * 8
              + b"SkokuNaFide\x00"
              + bytes.fromhex("0b02a4f19c3d4e7788991122334455de")
              + b"\x00\x00\x00\x00"
              + b"483 921 004\x00"
              + b"\x00" * 8)
    check("Kurzstring gefunden", "SkokuNaFide" in strings_in(window))
    check("Share-Code erkannt",
          any("483 921 004" == m.group(1).decode() for m in SHARE_CODE.finditer(window)))
    candidates = [c["hex"] for c in id_candidates(window, 0)]
    check("Id-Kandidat gefunden",
          "0b02a4f19c3d4e7788991122334455de" in candidates)

    # Was NICHT als Id durchgehen darf.
    check("lauter Nullen ist keine Id", not id_candidates(b"\x00" * 64, 0))
    check("lauter gleiche Bytes ist keine Id", not id_candidates(b"\xaa" * 64, 0))
    text_only = b"CreatorNameHere!" * 4
    check("reiner Text ist keine Id",
          all(c["hex"] != text_only[:16].hex() for c in id_candidates(text_only, 0)))
    utf16 = "AbcdefghAbcdefgh".encode("utf-16-le")
    check("UTF-16-Text ist keine Id",
          all(c["hex"] != utf16[:16].hex() for c in id_candidates(utf16, 0)))

    check("beide Kodierungen werden gesucht",
          [n for n, _ in needles("x")] == ["utf8", "utf16le"])
    check("Zahlensuche liefert drei Fassungen",
          [n for n, _ in number_needles(1123)] == ["float32", "float64", "int32"])
    check("float32 von 1123 stimmt",
          number_needles(1123)[0][1] == struct.pack("<f", 1123.0))
    check("krumme Zahl hat keine int32-Fassung",
          [n for n, _ in number_needles(2.5)] == ["float32", "float64"])
    check("UTF-16 wird als Muster gebildet",
          needles("Ab")[1][1] == b"A\x00b\x00")

    print("\nalles bestanden" if ok else "\nFEHLGESCHLAGEN")
    return 0 if ok else 1


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--text", help="die gesuchte Zeichenkette, z.B. ein Gamertag")
    parser.add_argument("--number", type=float, default=None,
                        help="statt Text eine bekannte ZAHL suchen (Leistung, "
                             "Gewicht, Rundenzeit) -- als float32, float64 und int32")
    parser.add_argument("--process", default="forzahorizon6")
    parser.add_argument("--pid", type=int, default=0)
    parser.add_argument("--before", type=int, default=DEFAULT_BEFORE)
    parser.add_argument("--after", type=int, default=DEFAULT_AFTER)
    parser.add_argument("--max-hits", type=int, default=40)
    parser.add_argument("--out", type=Path,
                        default=Path("data/runtime/bytes_around.json"))
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()

    if args.self_test:
        return self_test()
    if not args.text and args.number is None:
        raise SystemExit("--text oder --number fehlt (oder --self-test)")
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

    hits = search(pid, args.text or "", args.before, args.after,
                  args.max_hits, args.number)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(hits, indent=1), encoding="utf-8")

    with_id = [h for h in hits if h["id_candidates"]]
    with_code = [h for h in hits if h["share_code_like"]]
    label = args.text if args.number is None else f"Zahl {args.number}"
    print(f"'{label}': {len(hits)} Fundstelle(n), "
          f"{len(with_id)} mit Id-Kandidat, {len(with_code)} mit Share-Code-Form"
          f" -> {args.out}")
    for hit in hits[:8]:
        print(f"\n  {hit['address']} ({hit['encoding']})")
        interesting = [s for s in hit["strings"] if len(s) >= 5][:6]
        if interesting:
            print(f"    Strings: {' | '.join(interesting)}")
        if hit["share_code_like"]:
            print(f"    Share-Code-Form: {', '.join(hit['share_code_like'])}")
        for candidate in hit["id_candidates"][:4]:
            print(f"    Id-Kandidat {candidate['at']}: {candidate['hex']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
