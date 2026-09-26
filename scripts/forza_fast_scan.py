"""Page a Rivals leaderboard from inside the guest in ONE long-lived process.

Phase 1 of docs/extraction_speed_plan.md. The old loop has PowerShell spawn a
fresh Python process per 50-row page, sleep a fixed 350 ms, and write a JSON+CSV
pair per page -- 4 s per page, 750 ranks/min, and a late page costs 30-70 s
because the wait is one blunt timeout instead of a poll.

What changes:

* one process, one OpenProcess handle, one layout discovery, for the whole board
* the wanted rank is POLLED for at the known buffer addresses every few ms, so a
  page that lands in 40 ms costs 40 ms
* input goes through batched SendInput, not a SendKeys call per key
* rows append to one open JSONL file; the rank checkpoint is rewritten in place

Three bugs from the first run (2026-08-21) are fixed here, and they are worth
naming because each one silently destroyed the throughput it was meant to create:

1. It advanced the view by a fixed page size after every read. The resident block
   ended after 13 rows, the 50 presses walked the view past the next wanted rank,
   and it stalled. The view now advances by exactly the number of ranks that were
   actually read.
2. The hot loop called `scan_process(addresses=...)`, which reads only
   `stride * (minimum_records + 8)` bytes -- 11 records, not the 50-row page. The
   hot loop now reads the page itself and uses `rank_pair_offsets` + `decode_rows`
   directly.
3. Layout discovery costs ~207 s of full-heap sweep. It is now cached in a build
   profile (stride, addresses and every field offset) and re-verified cheaply, so
   only a patched game pays for discovery again.

Read-only against the game: PROCESS_QUERY_INFORMATION|PROCESS_VM_READ plus
synthetic key input. Nothing is written into the process.
"""

from __future__ import annotations

import argparse
import ctypes
import ctypes.wintypes as wintypes
import dataclasses
import io
import json
import struct
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from forza_scoreboard_layout import Layout  # noqa: E402
from forza_scoreboard_scan import (  # noqa: E402
    ProcessMemory,
    decode_rows,
    rank_pair_offsets,
    resolve_pid,
    runs_from_offsets,
    scan_process,
)

user32 = ctypes.WinDLL("user32", use_last_error=True)
_SENDINPUT_REPORTED = [False]

VK_DOWN = 0x28
_GAME_WINDOW = [0]
INPUT_KEYBOARD = 1
KEYEVENTF_KEYUP = 0x0002


class KEYBDINPUT(ctypes.Structure):
    _fields_ = [
        ("wVk", wintypes.WORD),
        ("wScan", wintypes.WORD),
        ("dwFlags", wintypes.DWORD),
        ("time", wintypes.DWORD),
        ("dwExtraInfo", ctypes.POINTER(ctypes.c_ulong)),
    ]


class _INPUTunion(ctypes.Union):
    _fields_ = [("ki", KEYBDINPUT), ("padding", ctypes.c_byte * 32)]


class INPUT(ctypes.Structure):
    _fields_ = [("type", wintypes.DWORD), ("union", _INPUTunion)]


user32.SendInput.argtypes = (wintypes.UINT, ctypes.POINTER(INPUT), ctypes.c_int)
user32.SendInput.restype = wintypes.UINT
user32.keybd_event.argtypes = (wintypes.BYTE, wintypes.BYTE, wintypes.DWORD,
                               ctypes.POINTER(ctypes.c_ulong))
user32.GetForegroundWindow.restype = wintypes.HWND


INPUT_METHOD = ["keybd"]


def send_key(vk: int, count: int, batch: int, delay_ms: float) -> None:
    """Send `count` presses, `batch` per SendInput call.

    SendKeys pays a shell round trip per key; SendInput takes an array. Batch size
    and spacing stay tunable because the game drops input that arrives faster than
    it samples -- calibrate, do not assume.
    """
    if _GAME_WINDOW[0] and int(user32.GetForegroundWindow() or 0) != _GAME_WINDOW[0]:
        # Focus can be stolen mid-run; re-assert it rather than firing keys into
        # whatever happens to be in front.
        user32.SetForegroundWindow(_GAME_WINDOW[0])
        time.sleep(0.03)
    sent = 0
    while sent < count:
        take = min(batch, count - sent)
        events = (INPUT * (take * 2))()
        for index in range(take):
            events[index * 2].type = INPUT_KEYBOARD
            events[index * 2].union.ki = KEYBDINPUT(vk, 0, 0, 0, None)
            events[index * 2 + 1].type = INPUT_KEYBOARD
            events[index * 2 + 1].union.ki = KEYBDINPUT(vk, 0, KEYEVENTF_KEYUP, 0, None)
        if INPUT_METHOD[0] == "keybd":
            # SendInput reported success for eight runs while the list never moved:
            # the events reached the queue and the game ignored them. The scanner
            # that does work drives the game through SendKeys, so take the older,
            # lower-level path instead of trusting a success code that means only
            # "queued", not "acted on".
            for _ in range(take):
                user32.keybd_event(vk, 0, 0, None)
                user32.keybd_event(vk, 0, KEYEVENTF_KEYUP, None)
            sent += take
            if delay_ms > 0:
                time.sleep(delay_ms / 1000.0 * take)
            continue
        # Pass the array itself, not byref(array): with argtypes declared as
        # POINTER(INPUT), ctypes converts an array to a pointer to its first
        # element but rejects a pointer TO the array.
        inserted = user32.SendInput(take * 2, events, ctypes.sizeof(INPUT))
        if inserted != take * 2 and not _SENDINPUT_REPORTED[0]:
            # Runs 1-5 collected exactly one resident page and then stalled, which
            # read like a throttled board but was the list never moving at all.
            # The return value was never checked, so a rejected batch looked
            # identical to a delivered one. Say it once, loudly, and fall back.
            _SENDINPUT_REPORTED[0] = True
            print(f"[fast-scan] SendInput inserted {inserted} of {take * 2} events "
                  f"(error {ctypes.get_last_error()}); falling back to keybd_event",
                  flush=True)
        if inserted != take * 2:
            for _ in range(take):
                user32.keybd_event(vk, 0, 0, None)
                user32.keybd_event(vk, 0, KEYEVENTF_KEYUP, None)
        sent += take
        if delay_ms > 0:
            time.sleep(delay_ms / 1000.0 * take)


class Rect(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]


def game_window(pid: int) -> int | None:
    """The game's largest visible window.

    Picking the FIRST visible window of the process is what broke the first four
    runs: synthetic input went to some other window of the same process, the list
    never scrolled, and every read returned only what was already resident -- 50
    ranks, one page, looking exactly like a board that had stopped serving. The
    biggest window is the rendered one.
    """
    best = [0, 0]  # hwnd, area

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def callback(hwnd, _lparam):
        owner = wintypes.DWORD(0)
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and user32.IsWindowVisible(hwnd):
            rect = Rect()
            if user32.GetWindowRect(hwnd, ctypes.byref(rect)):
                area = (rect.right - rect.left) * (rect.bottom - rect.top)
                if area > best[1]:
                    best[0], best[1] = hwnd, area
        return True

    user32.EnumWindows(callback, 0)
    return best[0] or None


def focus_game(hwnd: int) -> bool:
    """Bring the window forward and CONFIRM it, rather than hoping."""
    user32.SetForegroundWindow(hwnd)
    time.sleep(0.05)
    return int(user32.GetForegroundWindow() or 0) == int(hwnd)


class Pager:
    """Reads whole pages straight from the addresses discovery handed over."""

    def __init__(self, memory: ProcessMemory, addresses: list[int], layout: Layout,
                 read_records: int, *, require_name: bool = True) -> None:
        self.memory = memory
        self.addresses = list(addresses)
        self.layout = layout
        self.span = layout.stride * read_records
        self.require_name = require_name

    def read_rows(self) -> list[dict]:
        rows: list[dict] = []
        for address in list(self.addresses):
            block = self.memory.read(address, self.span)
            if not block:
                continue
            offsets = rank_pair_offsets(block)
            if not offsets:
                continue
            decoded = decode_rows(block, offsets, self.layout, base_address=address)
            for row in decoded:
                if row.get("gamertag"):
                    rows.append(row)
                    continue
                if self.require_name:
                    continue
                # The gamertag arrives AFTER the rest of the row -- the operator can
                # watch names populate late while ranks and lap times are already on
                # screen. A reader polling every few ms therefore sees structurally
                # complete rows with an empty name, and dropping them makes a page
                # the game HAS served look like no page at all. That is what every
                # dead cycle on 2026-08-22 was, and the bug scales with read speed,
                # which is why the 4 s-per-page loop never met it.
                #
                # Validate the way find_rank_address does, structurally: a lap time
                # between one second and a day, so a garbage block still cannot pass.
                lap = row.get("lap_time_seconds")
                if isinstance(lap, (int, float)) and 1.0 <= float(lap) <= 86400.0:
                    row["gamertag_pending"] = True
                    rows.append(row)
        return rows

    def remember(self, address: int) -> None:
        if address in self.addresses:
            self.addresses.remove(address)
        self.addresses.insert(0, address)
        del self.addresses[16:]


def load_profile(path: Path | None) -> tuple[list[int], Layout] | None:
    if not path or not path.exists():
        return None
    try:
        data = json.loads(path.read_text(encoding="utf-8-sig"))
        layout = Layout(**data["layout"])
        addresses = [int(a, 16) if isinstance(a, str) else int(a) for a in data["addresses"]]
        return addresses, layout
    except Exception as error:            # a stale profile must never be fatal
        print(f"[fast-scan] profile unusable ({error}); rediscovering", flush=True)
        return None


def save_profile(path: Path | None, addresses: list[int], layout: Layout) -> None:
    if not path:
        return
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps({
        "addresses": [f"0x{a:016x}" for a in addresses],
        "layout": dataclasses.asdict(layout),
    }, indent=2), encoding="utf-8")


def find_rank_address(memory: ProcessMemory, layout: Layout, wanted: int, *,
                      hint: int | None = None, near_radius_mb: int = 1536,
                      chunk_bytes: int = 16 * 1024 * 1024,
                      deadline_seconds: float | None = None) -> int | None:
    """Address of the block holding `wanted`, searched by the rank itself.

    This is the capability the working scan loop has and the resident reader did
    not: it searches for a SPECIFIC rank as a needle rather than for any row
    block. Without it, relocation kept re-finding the block it already knew,
    added nothing, and every page after the first ran into the timeout -- two
    measurement runs produced zero rows for exactly this reason.

    A `ScoreboardRow` starts with the rank stored twice as u32, so the needle is
    that pair. Nearby regions are searched first and the search stops at the first
    hit, because one block is all that is wanted.

    `deadline_seconds` bounds the sweep. Unbounded, this costs 24-28 s of the whole
    process (measured 2026-08-22), and paying that for a page that had merely landed
    at an unknown address took a 1.6 s cycle to 7.6 s and the run to 659 ranks/min.
    Regions are ordered near-first, so an early bail keeps exactly the cheap part.
    """
    needle = struct.pack("<II", wanted, wanted)
    regions = list(memory.regions(minimum_size=layout.stride * 2))
    if hint is not None:
        radius = near_radius_mb * 1024 * 1024
        near = [r for r in regions if abs(r[0] - hint) <= radius]
        far = [r for r in regions if abs(r[0] - hint) > radius]
        near.sort(key=lambda item: abs(item[0] - hint))
        far.sort(key=lambda item: abs(item[0] - hint))
        regions = near + far

    give_up_at = None
    if deadline_seconds is not None:
        give_up_at = time.monotonic() + deadline_seconds

    for base, size in regions:
        if give_up_at is not None and time.monotonic() >= give_up_at:
            return None
        offset = 0
        while offset < size:
            if give_up_at is not None and time.monotonic() >= give_up_at:
                return None
            take = min(chunk_bytes, size - offset)
            block = memory.read(base + offset, min(take + 8, size - offset))
            if not block:
                offset += take
                continue
            start = 0
            while True:
                hit = block.find(needle, start)
                if hit < 0:
                    break
                # Confirm it is a row rather than a coincidence, and do it
                # STRUCTURALLY: the neighbour one stride away must be this rank
                # plus or minus one. Requiring a decoded gamertag would tie the
                # search to string decoding, which is the fragile part.
                def rank_at(position: int) -> int | None:
                    if position < 0 or position + 8 > len(block):
                        return None
                    first, second = struct.unpack_from("<II", block, position)
                    return first if first == second else None

                after = rank_at(hit + layout.stride)
                before = rank_at(hit - layout.stride)
                # The neighbour must be a REAL rank. Accepting 0 made any "1,1"
                # preceded by zeroed memory look like the top of a leaderboard, and
                # the dump anchored on a region of small integers with no lap times
                # in it at all.
                ok_after = after is not None and after >= 1 and after == wanted + 1
                ok_before = before is not None and before >= 1 and before == wanted - 1

                def plausible_lap(position: int) -> bool:
                    # A double between one second and a day is the discriminator the
                    # production parser uses, and leaving it out let a region of
                    # small counters pass as a leaderboard: two consecutive ranks at
                    # 880-byte spacing happen by chance, a valid lap time next to
                    # them does not.
                    if layout.lap_time is None:
                        return True
                    at = position + layout.lap_time
                    if at < 0 or at + 8 > len(block):
                        return False
                    value = struct.unpack_from("<d", block, at)[0]
                    return 1.0 <= value <= 86400.0

                if (ok_after or ok_before) and (
                        plausible_lap(hit)
                        or (ok_after and plausible_lap(hit + layout.stride))
                        or (ok_before and plausible_lap(hit - layout.stride))):
                    # Report the START of the run so the caller gets the whole
                    # page, not its tail.
                    back = hit
                    while rank_at(back - layout.stride) == rank_at(back) - 1:
                        back -= layout.stride
                    return base + offset + back
                start = hit + 1
            offset += take
    return None


def find_addresses(memory: ProcessMemory, layout: Layout, *, want_blocks: int,
                   hint: int | None = None, near_radius_mb: int = 1536,
                   chunk_bytes: int = 16 * 1024 * 1024) -> list[int]:
    """Locate row-block addresses for a KNOWN layout, stopping at the first hits.

    This exists because `scan_process` keeps sweeping the whole heap after it has
    already found what it needs -- its inner `break` leaves the window loop, not
    the sweep -- and that costs ~205 s of the ~6 GB process on every board. Once
    the stride and field offsets are known (they survive a board change; only the
    addresses move) the addresses are all that has to be found again, and the
    sweep can stop as soon as it has them.
    """
    stride = layout.stride
    found: list[int] = []
    regions = list(memory.regions(minimum_size=stride * 8))
    if hint is not None and near_radius_mb > 0:
        # Look CLOSE first and only there. The old scan loop has a near stage --
        # 1536 MB around the last known address -- ahead of its full sweep, and
        # leaving it out is what made relocation cost minutes per page: with
        # want_blocks=4 the search never stopped at the block it actually needed
        # and kept sweeping the heap for three more.
        radius = near_radius_mb * 1024 * 1024
        near = [r for r in regions if abs(r[0] - hint) <= radius]
        far = [r for r in regions if abs(r[0] - hint) > radius]
        near.sort(key=lambda item: abs(item[0] - hint))
        far.sort(key=lambda item: abs(item[0] - hint))
        regions = near + far
    elif hint is not None:
        # The buffers move between boards but stay in the same arenas, and they
        # live high in the address space -- sweeping from the bottom spent 208 s
        # to reach them, no better than the full discovery it was meant to avoid.
        regions.sort(key=lambda item: abs(item[0] - hint))
    for base, size in regions:
        offset = 0
        while offset < size:
            want = min(chunk_bytes, size - offset)
            block = memory.read(base + offset, want)
            if not block:
                offset += want
                continue
            offsets = rank_pair_offsets(block)
            # Group with the module's own run finder rather than a hand-rolled
            # "four offsets one stride apart" test: the candidate list carries
            # spurious hits between real rows, and walking rank -> rank+1 the way
            # runs_from_offsets does is what tolerates them. The first attempt at
            # this found nothing on a board it should have matched.
            for run_stride, run_offsets in runs_from_offsets(block, offsets,
                                                             minimum_records=8):
                if run_stride != stride:
                    continue
                rows = decode_rows(block, run_offsets[:8], layout,
                                   base_address=base + offset)
                if sum(1 for row in rows if row.get("gamertag")) >= 4:
                    found.append(base + offset + run_offsets[0])
                    break
            if len(found) >= want_blocks:
                return found
            offset += want
    return found


def discover(memory: ProcessMemory) -> tuple[list[int], Layout] | None:
    findings, report = scan_process(memory, minimum_records=20,
                                    block_size=16 * 1024 * 1024)
    if not findings:
        return None
    print(f"[fast-scan] discovery {report['elapsed_seconds']}s, "
          f"{len(findings)} block(s)", flush=True)
    layout = findings[0]["layout"]
    addresses = [int(f["block_address"], 16) for f in findings]
    return addresses, layout


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--process-name", default="forzahorizon6")
    parser.add_argument("--start-rank", type=int, default=1,
                        help="0 or less means start wherever the list currently sits")
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--checkpoint", type=Path)
    parser.add_argument("--profile", type=Path,
                        help="cached stride/addresses/layout, so discovery runs once per build")
    parser.add_argument("--read-records", type=int, default=80,
                        help="records to read per address; must exceed the page size")
    parser.add_argument("--poll-ms", type=float, default=8.0)
    parser.add_argument("--page-deadline-seconds", type=float, default=20.0)
    parser.add_argument("--keys-per-batch", type=int, default=1)
    parser.add_argument("--key-delay-ms", type=float, default=8.0)
    parser.add_argument("--max-pages", type=int, default=100000)
    parser.add_argument("--stall-pages", type=int, default=3)
    parser.add_argument("--input-method", choices=("keybd", "sendinput"), default="keybd")
    parser.add_argument("--max-advance", type=int, default=50,
                        help="cap on rows scrolled per step; the proven loop moves ~50")
    parser.add_argument("--relocate-after-seconds", type=float, default=1.5,
                        help="poll this long before going to look for a moved page")
    # OFF by default on purpose: the tail of a board holds laps several times the
    # leader's, which the operator calls invalid, but the cut-off discards real
    # rows and the factor is theirs to choose.
    parser.add_argument("--max-time-factor", type=float, default=0.0)
    args = parser.parse_args(argv)

    INPUT_METHOD[0] = args.input_method
    pid = resolve_pid(args.process_name)
    hwnd = game_window(pid)
    if not hwnd:
        print("[fast-scan] no visible game window; input would go nowhere", flush=True)
        return 2
    _GAME_WINDOW[0] = int(hwnd)
    focused = focus_game(hwnd)
    print(f"[fast-scan] pid={pid} hwnd=0x{int(hwnd):x} focused={focused}", flush=True)
    if not focused:
        print("[fast-scan] WARNING focus did not stick; scrolling will not work", flush=True)

    with ProcessMemory(pid) as memory:
        cached = load_profile(args.profile)
        if cached:
            addresses, layout = cached
            print(f"[fast-scan] profile hit: stride {layout.stride}, "
                  f"{len(addresses)} address(es), no discovery", flush=True)
        else:
            found = discover(memory)
            if not found:
                print("[fast-scan] no row block found; is a leaderboard open?", flush=True)
                return 2
            addresses, layout = found
            save_profile(args.profile, addresses, layout)

        pager = Pager(memory, addresses, layout, args.read_records)

        # A cached profile is expected to go stale on every board change: the
        # layout survives, the addresses do not. So re-find only the addresses,
        # with an early exit, instead of paying for full layout rediscovery.
        if cached and not pager.read_rows():
            print("[fast-scan] cached addresses are stale; re-finding them for the known layout",
                  flush=True)
            started_find = time.monotonic()
            addresses = find_addresses(memory, layout, want_blocks=4,
                                       hint=addresses[0] if addresses else None)
            if not addresses:
                print("[fast-scan] known layout found no blocks; full rediscovery", flush=True)
                found = discover(memory)
                if not found:
                    return 2
                addresses, layout = found
            else:
                print(f"[fast-scan] found {len(addresses)} block(s) in "
                      f"{time.monotonic() - started_find:.1f}s", flush=True)
            save_profile(args.profile, addresses, layout)
            pager = Pager(memory, addresses, layout, args.read_records)

        # Starting "wherever the list is" beats guessing: after an aborted run the
        # view sits at an unknown rank, and a wrong -StartRank makes every read
        # miss until the deadline.
        start_rank = args.start_rank
        if start_rank <= 0:
            probe = pager.read_rows()
            if not probe:
                print("[fast-scan] nothing resident to start from", flush=True)
                return 2
            start_rank = min(int(r["rank"]) for r in probe)
            print(f"[fast-scan] list sits at rank {start_rank}; starting there", flush=True)

        started = time.monotonic()
        out = io.open(args.output, "a", encoding="utf-8")
        seen: set[int] = set()
        leader_time: float | None = None
        highest = start_rank - 1
        stalls = 0
        pages = 0
        cut = False

        try:
            while pages < args.max_pages and not cut:
                wanted = highest + 1
                deadline = time.monotonic() + args.page_deadline_seconds
                relocate_at = time.monotonic() + args.relocate_after_seconds
                relocated = False
                rows: list[dict] = []
                while True:
                    rows = [r for r in pager.read_rows() if int(r["rank"]) not in seen]
                    if any(int(r["rank"]) == wanted for r in rows):
                        break
                    now = time.monotonic()
                    # The pages alternate between heap arenas, so the NEXT page
                    # routinely lands at an address this pager has never seen. With
                    # only the profile's addresses to read, runs 1-5 saw exactly the
                    # first resident page and then stalled -- which looked like a
                    # throttled board and was really a missing address. Go and find
                    # the new arena instead of polling a stale one to the deadline.
                    if not relocated and now >= relocate_at:
                        relocated = True
                        extra = find_addresses(memory, layout, want_blocks=4,
                                               hint=pager.addresses[0] if pager.addresses else None)
                        added = [a for a in extra if a not in pager.addresses]
                        if added:
                            for address in added:
                                pager.remember(address)
                            print(f"[fast-scan] page moved; picked up "
                                  f"{len(added)} new address(es)", flush=True)
                            save_profile(args.profile, pager.addresses, layout)
                            continue
                    if now >= deadline:
                        rows = []
                        break
                    time.sleep(args.poll_ms / 1000.0)

                if not rows:
                    stalls += 1
                    print(f"[fast-scan] rank {wanted} did not arrive "
                          f"({stalls}/{args.stall_pages})", flush=True)
                    if stalls >= args.stall_pages:
                        break
                    # A late page is not a missing page: nudge, do not stride past it.
                    send_key(VK_DOWN, 2, args.keys_per_batch, args.key_delay_ms)
                    continue

                stalls = 0
                pages += 1
                previous = highest
                for row in sorted(rows, key=lambda r: int(r["rank"])):
                    rank = int(row["rank"])
                    if rank in seen:
                        continue
                    seen.add(rank)
                    highest = max(highest, rank)
                    if rank == 1:
                        leader_time = row.get("lap_time_seconds")
                    out.write(json.dumps(row, separators=(",", ":")) + "\n")
                    if (args.max_time_factor > 0 and leader_time
                            and row.get("lap_time_seconds")
                            and row["lap_time_seconds"] > leader_time * args.max_time_factor):
                        print(f"[fast-scan] lap {row['lap_time_seconds']:.1f}s at rank {rank} "
                              f"exceeds {args.max_time_factor}x the leader; stopping", flush=True)
                        cut = True
                out.flush()
                if rows:
                    pager.remember(int(rows[0]["record_address"], 16) & ~0xF)

                gained = highest - previous
                minutes = max(0.001, (time.monotonic() - started) / 60.0)
                if pages % 10 == 0:
                    print(f"[fast-scan] ranks={len(seen)} highest={highest} pages={pages} "
                          f"gained={gained} rate={len(seen)/minutes:.0f}/min", flush=True)
                if args.checkpoint:
                    args.checkpoint.write_text(json.dumps({
                        "ranks_collected": len(seen),
                        "highest_rank": highest,
                        "pages": pages,
                        "next_rank": highest + 1,
                    }, indent=2), encoding="utf-8")

                if cut or gained <= 0:
                    if gained <= 0:
                        print("[fast-scan] a page returned nothing new; stopping", flush=True)
                    break
                # Advance by exactly what was read. A fixed page size overshoots
                # whenever the resident block is short, and the next wanted rank
                # then scrolls past before it is ever asked for.
                # Cap the step. Reading two resident arenas at once yields ~100
                # fresh ranks, and scrolling 100 rows in one go jumps over pages
                # the game then never fetches -- run 7 sat at rank 100 waiting for
                # 101 forever. The loop that works advances about a page at a time.
                send_key(VK_DOWN, min(gained, args.max_advance),
                         args.keys_per_batch, args.key_delay_ms)
        finally:
            out.close()

        minutes = max(0.001, (time.monotonic() - started) / 60.0)
        print(f"[fast-scan] done: {len(seen)} ranks in {minutes:.1f} min "
              f"-> {len(seen)/minutes:.0f} ranks/min", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
