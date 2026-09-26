from __future__ import annotations

import argparse
import csv
import ctypes
import json
import math
import struct
import sys
import time
from ctypes import wintypes
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable

try:
    import numpy as np
except ImportError:  # the scalar fallback below keeps this optional
    np = None


ROW_STRIDE = 0x2D0
# Whether a row without a resolved gamertag counts as a row. See parse_row.
REQUIRE_GAMERTAG = [True]
RANK_OFFSET = 0x48
XUID_OFFSET = 0x58
GAMERTAG_OFFSET = 0x60
GAMERTAG_LENGTH_OFFSET = 0x70
LAP_TIME_OFFSET = 0x180
SUBMITTED_TIME_OFFSET = 0x188
# The car block, per the reflection-table layout verified against 300 June rows
# (docs/network_protocol_research.md). These were previously either unread
# (carId, carClassId, carPerformanceIndex) or mislabelled: 0x1A0/0x1A4 are the
# drive-type and powertrain ids, NOT the performance class, so the old
# car_class_id / car_drivetrain_id columns were wrong.
CAR_ID_OFFSET = 0x198  # uint16 -- ordinal into the game's car catalogue
CAR_CLASS_ID_OFFSET = 0x19A  # uint8 -- D/C/B/A/S1/S2/R/X
CAR_PI_OFFSET = 0x19C  # float32 -- performance index, still uncalibrated
DRIVE_TYPE_ID_OFFSET = 0x1A0
POWERTRAIN_ID_OFFSET = 0x1A4
FLAGS_OFFSET = 0x1A8
# Das Tune-Ende der Zeile, aus derselben Typtabelle wie der Rest (ScoreboardRow:
# versionedTuneId +372, tuneCreator +392, scoreId +696 -- hier alle um 0x40 nach
# vorn verschoben, weil dieser Leser den Satz 0x40 frueher anfangen laesst).
#
# WOFUER: die Id sagt, WELCHES geteilte Tune diese Runde gefahren hat, der Creator
# WER es gebaut hat. Beides kostet nichts extra -- es steht in derselben Zeile wie
# die Rundenzeit. Der Share-Code steht NICHT hier; der liegt im UGC-Satz des
# Storefronts, siehe find_tune_share_codes.py.
#
# UNGEPRUEFT: in dem einen rohen Abzug vom Juni ist versionedTuneId in allen elf
# Zeilen null. Ob das Feld je gefuellt ist, entscheidet erst eine Messung am
# lebenden Spiel. Darum wird es hier gelesen und mitgeschrieben, aber NICHTS
# haengt daran -- eine leere Id verwirft keine Zeile.
VERSIONED_TUNE_ID_OFFSET = 0x1B4  # 16 Byte
TUNE_CREATOR_OFFSET = 0x1C8  # user-Objekt: xuid +8, Gamertag +16
# scoreId (+696 im Satz, hier 0x2F8) liegt AUSSERHALB des Fensters: dieser Leser
# faengt 0x40 vor dem Satz an und liest 0x2D0 Byte, also endet er bei 0x290. Wer
# scoreId will, muss das Fenster verbreitern -- fuer die Tune-Frage braucht es das
# nicht.

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
    kernel32.VirtualQueryEx.argtypes = [
        wintypes.HANDLE,
        ctypes.c_void_p,
        ctypes.POINTER(MemoryBasicInformation),
        ctypes.c_size_t,
    ]
    kernel32.VirtualQueryEx.restype = ctypes.c_size_t
    kernel32.ReadProcessMemory.argtypes = [
        wintypes.HANDLE,
        ctypes.c_void_p,
        ctypes.c_void_p,
        ctypes.c_size_t,
        ctypes.POINTER(ctypes.c_size_t),
    ]
    kernel32.ReadProcessMemory.restype = wintypes.BOOL


def uint16(data: bytes, offset: int) -> int:
    return struct.unpack_from("<H", data, offset)[0]


def float32(data: bytes, offset: int) -> float:
    return struct.unpack_from("<f", data, offset)[0]


def uint32(data: bytes, offset: int) -> int:
    return struct.unpack_from("<I", data, offset)[0]


def uint64(data: bytes, offset: int) -> int:
    return struct.unpack_from("<Q", data, offset)[0]


def float64(data: bytes, offset: int) -> float:
    return struct.unpack_from("<d", data, offset)[0]


def parse_name(row: bytes, name_offset: int) -> str:
    """Einen Gamertag aus einem user-Objekt lesen.

    Das Muster ist ueberall dasselbe: der Name steht an `name_offset`, seine Laenge
    16 Byte spaeter. Es gibt ZWEI solche Objekte je Zeile -- den Fahrer und, weiter
    hinten, den Ersteller des gefahrenen Tunes -- darum als Funktion mit Offset
    statt mit fest verdrahteten Konstanten.
    """
    if name_offset + 24 > len(row):
        return ""
    length = uint64(row, name_offset + 16)
    if length == 0 or length > 15:
        return ""
    raw = row[name_offset : name_offset + min(length, 16)]
    try:
        value = raw.decode("utf-8")
    except UnicodeDecodeError:
        return ""
    if not all(32 <= ord(character) < 127 for character in value):
        return ""
    return value


def parse_gamertag(row: bytes) -> str:
    return parse_name(row, GAMERTAG_OFFSET)


def parse_system_time(row: bytes) -> str | None:
    values = struct.unpack_from("<8H", row, SUBMITTED_TIME_OFFSET)
    year, month, _weekday, day, hour, minute, second, millisecond = values
    try:
        value = datetime(
            year,
            month,
            day,
            hour,
            minute,
            second,
            millisecond * 1000,
            tzinfo=timezone.utc,
        )
    except ValueError:
        return None
    return value.isoformat()


def parse_row(row: bytes, source_address: int = 0) -> dict[str, Any] | None:
    if len(row) < ROW_STRIDE:
        return None
    rank = uint32(row, RANK_OFFSET)
    if rank < 1 or rank != uint32(row, RANK_OFFSET + 4):
        return None
    gamertag = parse_gamertag(row)
    if not gamertag and REQUIRE_GAMERTAG[0]:
        # Rejecting a nameless row throws away a complete result. The gamertag is
        # resolved from a separate profile lookup, so scrolling faster than that
        # lookup leaves rank, lap time, car and XUID all present and only the name
        # missing -- and this early return turned those rows into "no row block
        # found", which the scan then read as the end of the board. On 2026-08-21
        # fifteen boards in a row returned 149 / 99 / 50 rows for hours, and that
        # is the count of rows whose names happened to be resolved, not the count
        # the service served. The XUID is in the row, so names stay recoverable
        # offline; dropping the row is pure loss.
        return None
    lap_time = float64(row, LAP_TIME_OFFSET)
    if not math.isfinite(lap_time) or not (1.0 <= lap_time <= 86400.0):
        return None
    submitted_time = parse_system_time(row)
    if submitted_time is None:
        return None
    car_id = uint16(row, CAR_ID_OFFSET)
    car_class_id = row[CAR_CLASS_ID_OFFSET]
    car_performance_index_raw = float32(row, CAR_PI_OFFSET)
    drive_type_id = uint32(row, DRIVE_TYPE_ID_OFFSET)
    powertrain_id = uint32(row, POWERTRAIN_ID_OFFSET)
    if drive_type_id > 20 or powertrain_id > 20 or car_class_id > 20:
        return None

    flags = row[FLAGS_OFFSET : FLAGS_OFFSET + 10]
    if len(flags) < 10 or any(value not in (0, 1) for value in flags[:10]):
        return None

    tune_id = row[VERSIONED_TUNE_ID_OFFSET : VERSIONED_TUNE_ID_OFFSET + 16]
    tune_creator_xuid = uint64(row, TUNE_CREATOR_OFFSET + 8)
    tune_creator = parse_name(row, TUNE_CREATOR_OFFSET + 16)

    return {
        "rank": rank,
        "xuid": str(uint64(row, XUID_OFFSET)),
        "gamertag": gamertag,
        "lap_time_seconds": lap_time,
        "submitted_time_utc": submitted_time,
        "car_id": car_id,
        "car_class_id": car_class_id,
        "car_performance_index_raw": car_performance_index_raw,
        "car_drive_type_id": drive_type_id,
        "car_powertrain_id": powertrain_id,
        "used_stm": bool(flags[0]),
        "used_tcs": bool(flags[1]),
        "used_abs": bool(flags[2]),
        "used_friction_assist": bool(flags[3]),
        "used_auto_brake": bool(flags[4]),
        "used_auto_shifting": bool(flags[5]),
        "used_clutch": bool(flags[6]),
        "used_super_easy_assist": bool(flags[7]),
        "is_clean": bool(flags[8]),
        "has_ghost_file": bool(flags[9]),
        # Leer heisst hier "kein geteiltes Tune ODER die Liste fuehrt es nicht mit" --
        # die beiden sind aus einer Zeile allein nicht zu unterscheiden.
        "versioned_tune_id": tune_id.hex() if any(tune_id) else "",
        "tune_creator_xuid": str(tune_creator_xuid) if tune_creator_xuid else "",
        "tune_creator": tune_creator,
        "source_address": f"0x{source_address:016x}" if source_address else "",
        "raw_row_hex": row.hex(),
    }


def enrich_arrays(pid: int, arrays: list[list[dict[str, Any]]]) -> None:
    """Add the linked-vector columns to already selected rows.

    Split out of the scan so an unanchored sweep does not pay for it. Enriching
    during discovery means every candidate array anywhere in the heap costs extra
    per-row process reads, which turned the fallback from a slow path into a
    stalled one -- 107 s of CPU without finishing a single page.
    """
    process = kernel32.OpenProcess(
        PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, pid
    )
    if not process:
        return
    try:
        for array in arrays:
            for row in array:
                enrich_linked_vectors(process, row)
    finally:
        kernel32.CloseHandle(process)


def find_rank_pair_offsets(data: bytes, *, minimum_rows: int) -> list[int] | None:
    """Byte offsets of every (rank, rank) uint32 pair in `data`, vectorised.

    Returns None when numpy is missing, which tells the caller to use the scalar
    loop instead.

    This is the unanchored sweep, and it dominated everything else. The scalar
    version steps 4 bytes at a time in Python with two struct.unpack calls per
    step -- about 384 million iterations across the 1536 MB near-window -- and
    measured at 45-127 s per page, 82% of a board's wall clock. The comparison
    itself is two vectorised passes over the same bytes.

    The stride filter is what makes it cheap rather than merely fast: a real row
    is followed by rank+1 exactly one ROW_STRIDE later, and requiring that drops
    essentially every coincidental duplicate pair before parse_row (which is
    expensive, and Python) ever sees it. It can only be applied when at least
    two consecutive rows are wanted, which is why minimum_rows gates it.
    """
    if np is None:
        return None
    words = np.frombuffer(data, dtype="<u4", count=len(data) // 4)
    if words.size < 3:
        return []
    first = words[:-1]
    keep = first == words[1:]
    np.logical_and(keep, first >= 1, out=keep)
    np.logical_and(keep, first <= 10_000_000, out=keep)
    stride_words = ROW_STRIDE // 4
    if minimum_rows >= 2:
        limit = first.size - stride_words
        if limit <= 0:
            return []
        follows = np.zeros(first.size, dtype=bool)
        follows[:limit] = words[stride_words : stride_words + limit] == first[:limit] + 1
        np.logical_and(keep, follows, out=keep)
    return (np.flatnonzero(keep) * 4).tolist()


def choose_loose_array(
    arrays: list[list[dict[str, Any]]], *, wanted_rank: int
) -> list[list[dict[str, Any]]]:
    """Pick the block that best serves `wanted_rank` when no block starts on it.

    Returns at most one array, so the caller's "longest wins" rule cannot pick a
    stale low-rank block over the one that actually advances the scan.
    """
    spanning: list[tuple[int, list[dict[str, Any]]]] = []
    following: list[tuple[int, list[dict[str, Any]]]] = []
    for array in arrays:
        ranks = [row["rank"] for row in array if isinstance(row.get("rank"), int)]
        if not ranks:
            continue
        lowest, highest = min(ranks), max(ranks)
        if lowest <= wanted_rank <= highest:
            spanning.append((lowest, array))
        elif lowest > wanted_rank:
            following.append((lowest, array))
    if spanning:
        # Widest span wins: it carries the most rows at or beyond the wanted rank.
        return [max(spanning, key=lambda item: len(item[1]))[1]]
    if following:
        # Smallest starting rank leaves the smallest gap behind.
        return [min(following, key=lambda item: item[0])[1]]
    return []


def iter_arrays(
    data: bytes,
    *,
    base_address: int = 0,
    expected_rank: int | None = None,
    minimum_rows: int = 3,
    max_rows: int = 256,
) -> Iterable[list[dict[str, Any]]]:
    if expected_rank is not None:
        needles = [struct.pack("<II", expected_rank, expected_rank)]
    else:
        needles = []

    candidate_offsets: set[int] = set()
    if needles:
        for needle in needles:
            start = 0
            while True:
                match = data.find(needle, start)
                if match < 0:
                    break
                candidate_offsets.add(match - RANK_OFFSET)
                start = match + 1
    else:
        vectorised = find_rank_pair_offsets(data, minimum_rows=minimum_rows)
        if vectorised is None:
            for match in range(RANK_OFFSET, len(data) - 8, 4):
                rank = uint32(data, match)
                if 1 <= rank <= 10_000_000 and rank == uint32(data, match + 4):
                    candidate_offsets.add(match - RANK_OFFSET)
        else:
            for match in vectorised:
                if match >= RANK_OFFSET:
                    candidate_offsets.add(match - RANK_OFFSET)

    for candidate in sorted(candidate_offsets):
        if candidate < 0:
            continue
        rows: list[dict[str, Any]] = []
        expected_next: int | None = None
        for index in range(max_rows):
            offset = candidate + index * ROW_STRIDE
            if offset + ROW_STRIDE > len(data):
                break
            parsed = parse_row(
                data[offset : offset + ROW_STRIDE],
                base_address + offset,
            )
            if parsed is None:
                break
            if expected_next is not None and parsed["rank"] != expected_next:
                break
            rows.append(parsed)
            expected_next = parsed["rank"] + 1
        if len(rows) >= minimum_rows:
            yield rows


def readable_regions(process: int) -> Iterable[tuple[int, int]]:
    address = 0
    maximum = 0x00007FFFFFFFFFFF
    mbi = MemoryBasicInformation()
    while address < maximum:
        queried = kernel32.VirtualQueryEx(
            process,
            ctypes.c_void_p(address),
            ctypes.byref(mbi),
            ctypes.sizeof(mbi),
        )
        if not queried:
            break
        base = int(mbi.BaseAddress or 0)
        size = int(mbi.RegionSize)
        next_address = base + size
        if next_address <= address:
            break
        readable = (
            mbi.State == MEM_COMMIT
            and mbi.Type == MEM_PRIVATE
            and not (mbi.Protect & PAGE_NOACCESS)
            and not (mbi.Protect & PAGE_GUARD)
        )
        if readable and size >= ROW_STRIDE * 3:
            yield base, size
        address = next_address


def read_memory(process: int, address: int, length: int) -> bytes:
    buffer = ctypes.create_string_buffer(length)
    actual = ctypes.c_size_t()
    ok = kernel32.ReadProcessMemory(
        process,
        ctypes.c_void_p(address),
        buffer,
        length,
        ctypes.byref(actual),
    )
    if not ok and actual.value == 0:
        return b""
    return buffer.raw[: actual.value]


def enrich_linked_vectors(process: int, row: dict[str, Any]) -> None:
    raw = bytes.fromhex(row["raw_row_hex"])
    for begin_offset, end_offset, capacity_offset in (
        (0x128, 0x130, 0x138),
        (0x140, 0x148, 0x150),
    ):
        begin = uint64(raw, begin_offset)
        end = uint64(raw, end_offset)
        capacity = uint64(raw, capacity_offset)
        length = end - begin if end >= begin else 0
        key = f"linked_vector_0x{begin_offset:x}"
        row[f"{key}_length"] = length
        row[f"{key}_hex"] = (
            read_memory(process, begin, length).hex()
            if 0 < length <= 4096
            else ""
        )
        row[f"{key}_capacity"] = capacity - begin if capacity >= begin else 0


def scan_process(
    pid: int,
    *,
    expected_rank: int | None,
    chunk_size: int,
    minimum_rows: int,
    max_rows: int,
    near_address: int | None,
    near_radius: int,
    enrich: bool = True,
) -> list[list[dict[str, Any]]]:
    process = kernel32.OpenProcess(
        PROCESS_QUERY_INFORMATION | PROCESS_VM_READ,
        False,
        pid,
    )
    if not process:
        raise ctypes.WinError(ctypes.get_last_error())

    arrays: list[list[dict[str, Any]]] = []
    overlap = ROW_STRIDE * max(minimum_rows, 4)
    try:
        for region_base, region_size in readable_regions(process):
            if near_address is not None:
                region_end = region_base + region_size
                wanted_start = max(0, near_address - near_radius)
                wanted_end = near_address + near_radius
                if region_end <= wanted_start or region_base >= wanted_end:
                    continue
            offset = 0
            previous = b""
            while offset < region_size:
                requested = min(chunk_size, region_size - offset)
                current = read_memory(process, region_base + offset, requested)
                if not current:
                    previous = b""
                    offset += requested
                    continue
                combined = previous + current
                combined_base = region_base + offset - len(previous)
                found_arrays = list(
                    iter_arrays(
                        combined,
                        base_address=combined_base,
                        expected_rank=expected_rank,
                        minimum_rows=minimum_rows,
                        max_rows=max_rows,
                    )
                )
                if enrich:
                    for array in found_arrays:
                        for row in array:
                            enrich_linked_vectors(process, row)
                arrays.extend(found_arrays)
                previous = combined[-overlap:]
                offset += requested
    finally:
        kernel32.CloseHandle(process)
    return arrays


def scan_exact_process(
    pid: int,
    *,
    addresses: list[int],
    expected_rank: int | None,
    minimum_rows: int,
    max_rows: int,
    wait_seconds: float,
    poll_ms: int,
) -> list[list[dict[str, Any]]]:
    process = kernel32.OpenProcess(
        PROCESS_QUERY_INFORMATION | PROCESS_VM_READ,
        False,
        pid,
    )
    if not process:
        raise ctypes.WinError(ctypes.get_last_error())

    deadline = time.monotonic() + max(0.0, wait_seconds)
    length = ROW_STRIDE * max_rows
    try:
        while True:
            arrays: list[list[dict[str, Any]]] = []
            for address in addresses:
                data = read_memory(process, address, length)
                found = list(
                    iter_arrays(
                        data,
                        base_address=address,
                        expected_rank=expected_rank,
                        minimum_rows=minimum_rows,
                        max_rows=max_rows,
                    )
                )
                for array in found:
                    for row in array:
                        enrich_linked_vectors(process, row)
                arrays.extend(found)
            if arrays or time.monotonic() >= deadline:
                return arrays
            time.sleep(max(1, poll_ms) / 1000.0)
    finally:
        kernel32.CloseHandle(process)


def deduplicate(arrays: Iterable[list[dict[str, Any]]]) -> list[dict[str, Any]]:
    rows: dict[tuple[int, str, float], dict[str, Any]] = {}
    for array in arrays:
        for row in array:
            key = (
                int(row["rank"]),
                str(row["gamertag"]),
                round(float(row["lap_time_seconds"]), 6),
            )
            rows[key] = row
    return sorted(rows.values(), key=lambda row: int(row["rank"]))


def write_outputs(
    rows: list[dict[str, Any]],
    *,
    json_path: Path,
    csv_path: Path | None,
    metadata: dict[str, Any],
) -> None:
    report = {
        "captured_at": datetime.now(timezone.utc).isoformat(),
        **metadata,
        "row_count": len(rows),
        "minimum_rank": min((row["rank"] for row in rows), default=None),
        "maximum_rank": max((row["rank"] for row in rows), default=None),
        "rows": rows,
    }
    json_path.parent.mkdir(parents=True, exist_ok=True)
    json_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    if csv_path:
        csv_path.parent.mkdir(parents=True, exist_ok=True)
        fieldnames = [key for key in rows[0] if key != "raw_row_hex"] if rows else []
        with csv_path.open("w", newline="", encoding="utf-8") as handle:
            writer = csv.DictWriter(handle, fieldnames=fieldnames)
            if fieldnames:
                writer.writeheader()
                writer.writerows(
                    {key: value for key, value in row.items() if key != "raw_row_hex"}
                    for row in rows
                )


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Extract structured FH6 ScoreboardRow arrays from process memory."
    )
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--pid", type=int)
    source.add_argument("--input-dump", type=Path)
    parser.add_argument("--expected-rank", type=int)
    parser.add_argument("--minimum-rows", type=int, default=3)
    parser.add_argument(
        "--allow-missing-gamertag",
        action="store_true",
        help="keep rows whose name has not been resolved yet; rank, lap time, car "
             "and XUID are still validated, and the name can be looked up offline",
    )
    parser.add_argument("--max-rows", type=int, default=256)
    parser.add_argument("--chunk-size-mb", type=int, default=8)
    parser.add_argument(
        "--near-address",
        type=lambda value: int(value, 0),
        help="Only scan readable regions within --near-radius-mb of this address.",
    )
    parser.add_argument("--near-radius-mb", type=int, default=128)
    parser.add_argument(
        "--exact-address",
        action="append",
        type=lambda value: int(value, 0),
        default=[],
        help="Read a known ScoreboardRow buffer directly. May be repeated.",
    )
    parser.add_argument(
        "--wait-seconds",
        type=float,
        default=0.0,
        help="Wait for --expected-rank to appear at an exact address.",
    )
    parser.add_argument("--poll-ms", type=int, default=25)
    parser.add_argument("--output-json", required=True, type=Path)
    parser.add_argument("--output-csv", type=Path)
    parser.add_argument("--track", default="")
    parser.add_argument("--performance-class", default="")
    parser.add_argument("--rivals-mode", default="")
    args = parser.parse_args()
    REQUIRE_GAMERTAG[0] = not args.allow_missing_gamertag

    if args.input_dump:
        arrays = list(
            iter_arrays(
                args.input_dump.read_bytes(),
                expected_rank=args.expected_rank,
                minimum_rows=args.minimum_rows,
                max_rows=args.max_rows,
            )
        )
    else:
        if sys.platform != "win32":
            raise RuntimeError("Live process scanning requires Windows")
        if args.exact_address:
            arrays = scan_exact_process(
                args.pid,
                addresses=args.exact_address,
                expected_rank=args.expected_rank,
                minimum_rows=args.minimum_rows,
                max_rows=args.max_rows,
                wait_seconds=args.wait_seconds,
                poll_ms=args.poll_ms,
            )
        else:
            arrays = scan_process(
                args.pid,
                expected_rank=args.expected_rank,
                chunk_size=args.chunk_size_mb * 1024 * 1024,
                minimum_rows=args.minimum_rows,
                max_rows=args.max_rows,
                near_address=args.near_address,
                near_radius=args.near_radius_mb * 1024 * 1024,
            )
            if not arrays and args.expected_rank is not None:
                # The game's buffer boundaries do not line up with what we asked
                # for. After scrolling past rank 50 the live block began at rank
                # 60, and anchoring on the exact (rank, rank) pair for 51 then
                # matched nothing at all -- even though ranks 60-100 were sitting
                # in memory, decodable, one call away. Paging stalled on a block
                # it could see but would not accept.
                #
                # So drop the anchor and take what is actually there: prefer a
                # block that spans the wanted rank, otherwise the one starting
                # closest after it, which keeps any gap as small as the game's
                # own eviction allows.
                arrays = choose_loose_array(
                    scan_process(
                        args.pid,
                        expected_rank=None,
                        chunk_size=args.chunk_size_mb * 1024 * 1024,
                        minimum_rows=args.minimum_rows,
                        max_rows=args.max_rows,
                        near_address=args.near_address,
                        near_radius=args.near_radius_mb * 1024 * 1024,
                        enrich=False,
                    ),
                    wanted_rank=args.expected_rank,
                )
                # Only the array that survived selection is worth enriching.
                enrich_arrays(args.pid, arrays)

    best_arrays = [max(arrays, key=len)] if arrays else []
    rows = deduplicate(best_arrays)
    write_outputs(
        rows,
        json_path=args.output_json,
        csv_path=args.output_csv,
        metadata={
            "process_id": args.pid,
            "input_dump": str(args.input_dump.resolve()) if args.input_dump else None,
            "expected_rank": args.expected_rank,
            "near_address": (
                f"0x{args.near_address:016x}" if args.near_address is not None else None
            ),
            "exact_addresses": [
                f"0x{address:016x}" for address in args.exact_address
            ],
            "track": args.track,
            "performance_class": args.performance_class,
            "rivals_mode": args.rivals_mode,
        },
    )
    print(
        json.dumps(
            {
                "rows": len(rows),
                "minimum_rank": min((row["rank"] for row in rows), default=None),
                "maximum_rank": max((row["rank"] for row in rows), default=None),
                "output_json": str(args.output_json.resolve()),
                "output_csv": str(args.output_csv.resolve()) if args.output_csv else None,
            },
            indent=2,
        )
    )
    return 0 if rows else 2


if __name__ == "__main__":
    raise SystemExit(main())
