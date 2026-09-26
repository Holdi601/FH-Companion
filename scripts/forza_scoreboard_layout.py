"""Derive the in-memory ScoreboardRow layout from a buffer instead of hard-coding it.

`capture_scoreboard_memory.py` hard-codes stride `0x2d0` and every field offset.
Those constants were read off the 2026-06-24 build (`6.382.893.0`). The game's
code is packed and decrypted at runtime, Steam is set to always update, and the
live service will force updates, so those constants have a short shelf life, and
they fail in a way that is easy to misread: a shifted offset makes every record
fail validation and the scanner just reports "no row block found".

This module derives the layout from the data. Given a buffer containing a
contiguous run of scoreboard records, it:

1. finds every position holding a duplicated u32 (`rank`, `rank`) pair, the
   distinctive marker the existing extractor already relies on
2. infers the record stride from the spacing of consecutive ranks
3. locates each remaining field by testing candidate offsets column-wise across
   every record in the run, keeping only offsets where all records validate

Column-wise validation is what makes this trustworthy: a coincidence has to hold
at the same offset across dozens of records, not just one.

All offsets are expressed **relative to the rank position**, not to the record
start, because that is what a scanner actually has in hand: it finds a rank pair
and needs to know where the other fields sit. `layout_relative_to_record_start`
converts when absolute record offsets are wanted.

Check that a layout still holds against a real capture, with no game running:

    python forza_scoreboard_layout.py --chunk-json <rows_*.json>
"""

from __future__ import annotations

import argparse
import json
import struct
from dataclasses import asdict, dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable, Iterable

# Xbox Live XUIDs are allocated from a known band, which makes this a cheap and
# very selective column test.
XUID_MINIMUM = 2_533_274_790_000_000
XUID_MAXIMUM = 2_535_999_999_999_999

MINIMUM_LAP_TIME_SECONDS = 1.0
MAXIMUM_LAP_TIME_SECONDS = 86_400.0

# MSVC std::string with short-string optimisation: 16-byte inline buffer, then
# size, then capacity. An SSO string has capacity exactly 15.
SSO_CAPACITY = 15
SSO_BUFFER_BYTES = 16

# Offsets verified on build 6.382.893.0, as deltas from the rank position
# (rank itself was at 0x48 within the record).
KNOWN_BUILD_RANK_RELATIVE = {
    "stride": 0x2D0,
    "xuid": 0x10,
    "gamertag": 0x18,
    "gamertag_size": 0x28,
    "lap_time": 0x138,
    "submitted_time": 0x140,
    "car_drive_type_id": 0x158,
    "car_powertrain_id": 0x15C,
    "flags": 0x160,
}

# Field order recovered from the game's own reflection tables (see
# dump_forza_type_descriptors.py). The three fields ahead of carDriveTypeId are
# not separately discoverable by column validation -- carClassId is constant on a
# class-filtered board and carId/carPerformanceIndex have no distinguishing
# range -- but the descriptor gives their order and sizes, so they are read at
# fixed deltas from the discovered carDriveTypeId anchor and then validated.
#
#   carId               uint16   anchor - 8
#   carClassId          uint8    anchor - 6
#   carPerformanceIndex float32  anchor - 4
#   carDriveTypeId      int32    anchor          <- discovered empirically
#   carPowertrainId     int32    anchor + 4
CAR_ID_DELTA = -8
CAR_CLASS_ID_DELTA = -6
CAR_PERFORMANCE_INDEX_DELTA = -4

# Performance class ids run D, C, B, A, S1, S2, R, X.
PERFORMANCE_CLASS_IDS = {0: "D", 1: "C", 2: "B", 3: "A", 4: "S1", 5: "S2", 6: "R", 7: "X"}


@dataclass
class Layout:
    """Validated field offsets, all relative to the rank position."""

    stride: int
    record_count: int
    xuid: int | None = None
    gamertag: int | None = None
    gamertag_size: int | None = None
    lap_time: int | None = None
    submitted_time: int | None = None
    car_drive_type_id: int | None = None
    car_powertrain_id: int | None = None
    flags: int | None = None
    flag_count: int = 0
    notes: list[str] = field(default_factory=list)

    def matches_known_build(self) -> bool:
        """True when this is the layout verified on build 6.382.893.0."""
        expected = KNOWN_BUILD_RANK_RELATIVE
        return (
            self.stride == expected["stride"]
            and self.xuid == expected["xuid"]
            and self.gamertag == expected["gamertag"]
            and self.gamertag_size == expected["gamertag_size"]
            and self.lap_time == expected["lap_time"]
            and self.submitted_time == expected["submitted_time"]
            and self.car_drive_type_id == expected["car_drive_type_id"]
            and self.car_powertrain_id == expected["car_powertrain_id"]
            and self.flags == expected["flags"]
        )

    def required_fields_present(self) -> bool:
        return None not in (
            self.xuid,
            self.gamertag,
            self.gamertag_size,
            self.lap_time,
            self.submitted_time,
        )


def _u32(data: bytes, offset: int) -> int:
    return struct.unpack_from("<I", data, offset)[0]


def _u64(data: bytes, offset: int) -> int:
    return struct.unpack_from("<Q", data, offset)[0]


def _f64(data: bytes, offset: int) -> float:
    return struct.unpack_from("<d", data, offset)[0]


def find_rank_positions(
    data: bytes, *, maximum_rank: int = 10_000_000
) -> list[tuple[int, int]]:
    """Positions of duplicated u32 rank pairs, as (offset, rank)."""
    found: list[tuple[int, int]] = []
    limit = len(data) - 8
    for offset in range(0, max(0, limit + 1), 4):
        value = _u32(data, offset)
        if 1 <= value <= maximum_rank and value == _u32(data, offset + 4):
            found.append((offset, value))
    return found


def infer_runs(
    data: bytes,
    positions: list[tuple[int, int]],
    *,
    minimum_records: int,
) -> list[tuple[int, list[int]]]:
    """Group rank positions into runs, returned as (stride, offsets).

    A run is a set of positions whose ranks increase by exactly 1 with constant
    byte spacing. Those two constraints together make the spacing the stride.
    """
    by_rank: dict[int, list[int]] = {}
    for offset, rank in positions:
        by_rank.setdefault(rank, []).append(offset)

    runs: list[tuple[int, list[int]]] = []
    claimed: set[int] = set()

    for start_offset, start_rank in positions:
        if start_offset in claimed:
            continue
        # Candidate strides come from wherever the next rank actually lives.
        for next_offset in by_rank.get(start_rank + 1, []):
            stride = next_offset - start_offset
            if stride <= 0 or stride % 8 != 0:
                continue
            offsets = [start_offset]
            rank = start_rank
            offset = start_offset
            while True:
                offset += stride
                rank += 1
                if offset + 8 > len(data):
                    break
                if _u32(data, offset) != rank or _u32(data, offset + 4) != rank:
                    break
                offsets.append(offset)
            if len(offsets) >= minimum_records:
                claimed.update(offsets)
                runs.append((stride, offsets))
                break
    runs.sort(key=lambda item: len(item[1]), reverse=True)
    return runs


def read_sso_string(data: bytes, buffer_offset: int) -> str | None:
    """Read an MSVC std::string at an absolute offset, or None if invalid."""
    size_offset = buffer_offset + SSO_BUFFER_BYTES
    if size_offset + 16 > len(data):
        return None
    size = _u64(data, size_offset)
    capacity = _u64(data, size_offset + 8)
    if capacity != SSO_CAPACITY or not 1 <= size <= SSO_CAPACITY:
        return None
    raw = data[buffer_offset : buffer_offset + size]
    if len(raw) < size:
        return None
    try:
        text = raw.decode("utf-8")
    except UnicodeDecodeError:
        return None
    if not all(32 <= ord(character) < 127 for character in text):
        return None
    return text


def read_system_time(data: bytes, offset: int) -> datetime | None:
    if offset + 16 > len(data):
        return None
    year, month, weekday, day, hour, minute, second, millisecond = struct.unpack_from(
        "<8H", data, offset
    )
    if weekday > 6 or millisecond > 999 or not 2015 <= year <= 2100:
        return None
    try:
        return datetime(
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


def discover_layout(
    data: bytes,
    *,
    minimum_records: int = 8,
    require_all: bool = False,
) -> tuple[Layout | None, dict[str, Any]]:
    """Derive a Layout from a buffer holding a contiguous run of records."""
    positions = find_rank_positions(data)
    runs = infer_runs(data, positions, minimum_records=minimum_records)
    report: dict[str, Any] = {
        "buffer_bytes": len(data),
        "rank_pair_candidates": len(positions),
        "runs": [
            {"stride": f"0x{stride:x}", "records": len(offsets)}
            for stride, offsets in runs[:8]
        ],
    }
    if not runs:
        report["error"] = "no run of consecutive ranks was found"
        return None, report

    stride, all_records = runs[0]
    # Column tests read up to a full stride past each rank position, so only use
    # records that have a whole stride of buffer behind them.
    records = [offset for offset in all_records if offset + stride <= len(data)]
    if len(records) < minimum_records:
        records = all_records[:-1] or all_records
    report["chosen"] = {
        "stride": f"0x{stride:x}",
        "records_in_run": len(all_records),
        "records_used_for_columns": len(records),
    }

    notes: list[str] = []

    def candidates(width: int, alignment: int) -> Iterable[int]:
        return range(0, stride - width + 1, alignment)

    def column_holds(
        width: int, alignment: int, test: Callable[[bytes, int], bool]
    ) -> list[int]:
        return [
            candidate
            for candidate in candidates(width, alignment)
            if all(test(data, record + candidate) for record in records)
        ]

    def pick(name: str, survivors: list[int]) -> int | None:
        report.setdefault("candidates", {})[name] = [
            f"0x{value:x}" for value in survivors[:8]
        ]
        if not survivors:
            notes.append(
                f"{name}: no offset validated across {len(records)} records"
            )
            return None
        if len(survivors) > 1:
            notes.append(
                f"{name}: {len(survivors)} offsets validated, taking the lowest"
            )
        return survivors[0]

    xuid = pick(
        "xuid",
        column_holds(
            8, 8, lambda buffer, at: XUID_MINIMUM <= _u64(buffer, at) <= XUID_MAXIMUM
        ),
    )
    gamertag = pick(
        "gamertag",
        column_holds(
            SSO_BUFFER_BYTES + 16,
            8,
            lambda buffer, at: read_sso_string(buffer, at) is not None,
        ),
    )
    lap_time = pick(
        "lap_time",
        column_holds(
            8,
            8,
            lambda buffer, at: MINIMUM_LAP_TIME_SECONDS
            <= _f64(buffer, at)
            <= MAXIMUM_LAP_TIME_SECONDS,
        ),
    )
    submitted_time = pick(
        "submitted_time",
        column_holds(
            16, 8, lambda buffer, at: read_system_time(buffer, at) is not None
        ),
    )

    # Class and drivetrain ids are small adjacent u32s. They cannot be required
    # to vary: a board filtered to one performance class has a constant
    # carClassId, which is exactly the case we scan. So accept constant columns
    # and merely prefer pairs where something varies, reporting the ambiguity
    # instead of silently guessing.
    # Requiring the class id to be non-zero is what makes this selective: a row
    # on a leaderboard always has a performance class, so 0 excludes the many
    # zero-filled columns that a plain 0..20 range test would accept. The
    # drivetrain id beside it must NOT be held to the same rule: id 0 is a real
    # drivetrain value, seen in 17 of 49 records in one captured block.
    def small_u32_column(offset: int, *, allow_zero: bool) -> list[int] | None:
        values = [_u32(data, record + offset) for record in records]
        low = 0 if allow_zero else 1
        return values if all(low <= value <= 20 for value in values) else None

    # Exclude bytes already claimed by a confirmed field, and every 32-byte MSVC
    # std::string block. Without the string exclusion the low/high halves of a
    # string's 8-byte size field read as a perfectly plausible
    # (non-zero small, zero) id pair; with it, real captures leave exactly one
    # candidate.
    occupied: set[int] = set()
    for start, width in (
        (0, 8),
        (xuid, 8),
        (gamertag, SSO_BUFFER_BYTES + 16),
        (lap_time, 8),
        (submitted_time, 16),
    ):
        if start is not None:
            occupied.update(range(start, start + width))
    for candidate in range(0, stride - (SSO_BUFFER_BYTES + 16) + 1, 8):
        capacity_offset = candidate + SSO_BUFFER_BYTES + 8
        if all(_u64(data, record + capacity_offset) == SSO_CAPACITY for record in records):
            occupied.update(range(candidate, candidate + SSO_BUFFER_BYTES + 16))

    class_pairs: list[tuple[int, int]] = []
    for candidate in candidates(8, 4):
        if any(index in occupied for index in range(candidate, candidate + 8)):
            continue
        left = small_u32_column(candidate, allow_zero=False)
        right = small_u32_column(candidate + 4, allow_zero=True)
        if left is None or right is None:
            continue
        varying = len(set(left)) > 1 or len(set(right)) > 1
        # Sort key: varying pairs first, then lowest offset.
        class_pairs.append((0 if varying else 1, candidate))
    class_pairs.sort()
    car_drive_type_id = pick("car_drive_type_id", [offset for _, offset in class_pairs])
    car_powertrain_id = None if car_drive_type_id is None else car_drive_type_id + 4

    # The assist/clean/ghost block is a run of bytes only ever 0 or 1, and not
    # constant across the run. Classify every byte column once, then read runs
    # off that, rather than re-walking the stride for each candidate offset.
    boolean_column = bytearray(stride)
    varying_column = bytearray(stride)
    for index in range(stride):
        seen = {data[record + index] for record in records}
        if seen <= {0, 1}:
            boolean_column[index] = 1
            if len(seen) > 1:
                varying_column[index] = 1

    def boolean_run_length(offset: int) -> int:
        length = 0
        while offset + length < stride and boolean_column[offset + length]:
            length += 1
        return length

    flag_runs = []
    for candidate in candidates(8, 8):
        length = boolean_run_length(candidate)
        if length < 8:
            continue
        if any(varying_column[candidate + index] for index in range(length)):
            flag_runs.append((candidate, length))
    flag_runs.sort(key=lambda item: (-item[1], item[0]))
    flags = pick("flags", [offset for offset, _ in flag_runs])
    flag_count = next((length for offset, length in flag_runs if offset == flags), 0)

    layout = Layout(
        stride=stride,
        record_count=len(all_records),
        xuid=xuid,
        gamertag=gamertag,
        gamertag_size=None if gamertag is None else gamertag + SSO_BUFFER_BYTES,
        lap_time=lap_time,
        submitted_time=submitted_time,
        car_drive_type_id=car_drive_type_id,
        car_powertrain_id=car_powertrain_id,
        flags=flags,
        flag_count=min(flag_count, 16),
        notes=notes,
    )
    report["layout"] = asdict(layout)
    report["matches_known_build_6_382_893_0"] = layout.matches_known_build()
    report["required_fields_present"] = layout.required_fields_present()

    if require_all and not layout.required_fields_present():
        report["error"] = "one or more required fields did not validate"
        return None, report
    return layout, report


def layout_relative_to_record_start(layout: Layout, rank_offset_in_record: int) -> dict[str, int | None]:
    """Convert rank-relative offsets to offsets from the record start."""

    def move(value: int | None) -> int | None:
        return None if value is None else value + rank_offset_in_record

    return {
        "stride": layout.stride,
        "rank": rank_offset_in_record,
        "xuid": move(layout.xuid),
        "gamertag": move(layout.gamertag),
        "gamertag_size": move(layout.gamertag_size),
        "lap_time": move(layout.lap_time),
        "submitted_time": move(layout.submitted_time),
        "car_drive_type_id": move(layout.car_drive_type_id),
        "car_powertrain_id": move(layout.car_powertrain_id),
        "flags": move(layout.flags),
    }


def buffer_from_chunk(
    chunk: dict[str, Any], *, maximum_gap: int = 0x10000
) -> bytes:
    """Rebuild the original contiguous memory block from a captured chunk.

    Chunk rows carry `raw_row_hex` and `source_address`, so the original array
    can be reassembled exactly. That makes discovery testable against real
    captures with no game running.

    Rows in a chunk are not guaranteed to come from one array: the old scanner
    sometimes merged blocks from unrelated heap locations, and one captured chunk
    has rows spread across 37 GB of address space. Reconstructing that span
    naively means allocating 37 GB, so rows are clustered by address first and
    the largest contiguous cluster wins.
    """
    rows = chunk.get("rows", [])
    if not rows:
        raise RuntimeError("The chunk contains no rows.")
    entries: list[tuple[int, bytes]] = []
    for row in rows:
        raw = bytes.fromhex(row["raw_row_hex"])
        address_text = str(row.get("source_address") or "")
        address = int(address_text, 16) if address_text.startswith("0x") else 0
        entries.append((address, raw))
    entries.sort()
    if entries[0][0] == 0:
        return b"".join(raw for _, raw in entries)

    clusters: list[list[tuple[int, bytes]]] = [[entries[0]]]
    for address, raw in entries[1:]:
        previous_address, previous_raw = clusters[-1][-1]
        if address - (previous_address + len(previous_raw)) > maximum_gap:
            clusters.append([])
        clusters[-1].append((address, raw))
    cluster = max(clusters, key=len)

    base = cluster[0][0]
    size = cluster[-1][0] - base + len(cluster[-1][1])
    buffer = bytearray(size)
    for address, raw in cluster:
        buffer[address - base : address - base + len(raw)] = raw
    return bytes(buffer)


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Derive and verify the ScoreboardRow layout from a buffer."
    )
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument(
        "--chunk-json",
        type=Path,
        help="A rows_*.json produced by capture_scoreboard_memory.py.",
    )
    source.add_argument("--input-dump", type=Path, help="A raw memory dump.")
    parser.add_argument("--minimum-records", type=int, default=8)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()

    if args.chunk_json:
        chunk = json.loads(args.chunk_json.read_text(encoding="utf-8-sig"))
        data = buffer_from_chunk(chunk)
    else:
        data = args.input_dump.read_bytes()

    layout, report = discover_layout(data, minimum_records=args.minimum_records)
    text = json.dumps(report, indent=2)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(text + "\n", encoding="utf-8")
    print(text)
    return 0 if layout is not None else 2


if __name__ == "__main__":
    raise SystemExit(main())
