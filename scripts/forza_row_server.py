"""A resident row reader the PowerShell scan loop talks to over stdin/stdout.

Why this shape. Phase 1 tried to move the whole loop into Python, including the
key presses, and that failed: nine runs on 2026-08-21 read memory perfectly and
never scrolled the list, because synthetic input sent from a guest scheduled task
is ignored by the game even with the window confirmed foreground. The loop that
does drive the game is `run_memory_leaderboard_scan.ps1`, using
`[System.Windows.Forms.SendKeys]` from a PowerShell process in the console
session.

So keep the sender that works and remove only the parts that actually cost time:

* a Python process per page (~0.5 s of interpreter start, every page)
* the fixed 350 ms settle, plus the blunt multi-second waits when a page is late
* a JSON + CSV file pair written per page

This process starts once, holds one OpenProcess handle, and answers requests:

    -> {"want": 51}                     read the page containing rank 51
    -> {"harvest": 1}                   every row resident now, no waiting
    -> {"want": 51, "timeout_ms": 8000} same, with an explicit deadline
    -> {"ping": 1}                      liveness
    -> {"quit": 1}                      exit

    <- {"ok": true, "rows": [...], "waited_ms": 42}
    <- {"ok": false, "reason": "timeout", "waited_ms": 8000}

One JSON object per line, in and out, so the caller never has to parse
partial output. Polling happens here at a few milliseconds, so a page that lands
in 40 ms is answered in 40 ms instead of costing a fixed settle.

Read-only: PROCESS_QUERY_INFORMATION|PROCESS_VM_READ. This process sends no
input at all -- that stays with the caller, deliberately.
"""

from __future__ import annotations

import argparse
import json
import struct
import sys
import threading
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from forza_fast_scan import (  # noqa: E402
    Pager,
    discover,
    find_addresses,
    find_rank_address,
    load_profile,
    save_profile,
)
from forza_scoreboard_scan import (  # noqa: E402
    ProcessMemory,
    decode_rows,
    rank_pair_offsets,
    resolve_pid,
)


class Recorder:
    """Poll the row pool on its own thread and keep every rank it ever sees.

    This is the whole point of the design, and the earlier loop got it wrong: rows
    scroll past at about one per 31 ms (the OS key-repeat ceiling), so a reader that
    looks every 16 ms cannot miss one -- a row is resident for at least as long as it
    takes the next key to arrive. The old loop harvested ONCE per key burst, i.e.
    every ~1.24 s for a 40-row burst, and was blind in between; that, not the game,
    is where 9-80% of ranks went.

    Decoupling also removes the per-cycle JSON round trip: the caller presses keys and
    nothing else, and asks for the result at the end.
    """

    def __init__(self, pager, layout, memory, *, poll_ms: float,
                 relocate_after_empty: int = 60) -> None:
        self.pager = pager
        self.layout = layout
        self.memory = memory
        self.poll_ms = poll_ms
        self.relocate_after_empty = relocate_after_empty
        # rank -> raw row bytes, and rank -> whether that copy carries a name. The
        # gamertag arrives after the rest of the row, so a nameless copy is kept only
        # until a named one shows up.
        self.raw: dict[int, bytes] = {}
        self.named: dict[int, bool] = {}
        self.lock = threading.Lock()
        self._stop = threading.Event()
        self.polls = 0
        self.poll_ms_total = 0.0
        self.empty_polls = 0
        self.decoded = 0
        self.rejected = 0
        self.invalid_seen = 0
        self.max_valid = 0
        self.invalid_from = 0
        self.thread: threading.Thread | None = None

    def start(self) -> None:
        if self.thread and self.thread.is_alive():
            return
        self._stop.clear()
        self.thread = threading.Thread(target=self._run, name="recorder", daemon=True)
        self.thread.start()

    def stop(self) -> None:
        self._stop.set()
        if self.thread:
            self.thread.join(timeout=2.0)

    MAX_POLLED_ADDRESSES = 6

    def _harvest_once(self) -> int:
        """Copy raw row bytes for ranks not held yet; decode nothing.

        Decoding is what made a poll cost 235 ms instead of 16: `decode_rows` builds a
        20-field dict per row with a decoded gamertag, and at this sample rate almost
        all of that work is thrown away on rows already held. In the hot path a row is
        judged by two unpacks -- its rank and its lap time -- and then memcpy'd. The
        dicts are built once, at write time.
        """
        gained = 0
        span = self.pager.span
        stride = self.layout.stride
        lap_at = self.layout.lap_time
        name_at = self.layout.gamertag
        for address in list(self.pager.addresses)[:self.MAX_POLLED_ADDRESSES]:
            block = self.memory.read(address, span)
            if not block:
                continue
            offsets = rank_pair_offsets(block)
            if not offsets:
                continue
            for offset in offsets:
                if offset + stride > len(block):
                    continue
                rank = struct.unpack_from("<I", block, offset)[0]
                if rank < 1 or rank > 5_000_000:
                    continue
                # The 0x10001 / 0x20002 pattern inside every row decodes as a rank of
                # 65537 / 131074; a lap time between a second and a day is what tells
                # a row from that.
                if lap_at is not None:
                    at = offset + lap_at
                    if at + 8 > len(block):
                        continue
                    lap = struct.unpack_from("<d", block, at)[0]
                    if not 1.0 <= lap <= 86400.0:
                        self.rejected += 1
                        continue
                named = False
                if name_at is not None and offset + name_at < len(block):
                    named = block[offset + name_at] != 0
                with self.lock:
                    current = self.named.get(rank)
                    if current is None:
                        gained += 1
                    elif current or not named:
                        # Already held, and this copy is no better.
                        continue
                    self.raw[rank] = bytes(block[offset:offset + stride])
                    self.named[rank] = named
                    self.decoded += 1
        return gained

    def _run(self) -> None:
        while not self._stop.is_set():
            started = time.monotonic()
            try:
                gained = self._harvest_once()
            except Exception:            # a torn read must not kill the recorder
                gained = 0
            with self.lock:
                self.polls += 1
                self.poll_ms_total += (time.monotonic() - started) * 1000.0
                if gained:
                    self.empty_polls = 0
                else:
                    self.empty_polls += 1
            if self.empty_polls and self.empty_polls % self.relocate_after_empty == 0:
                # The pool can be served from an arena we have not been told about.
                # Look near the addresses we know; this is the cheap search.
                try:
                    found = find_addresses(self.memory, self.layout, want_blocks=6,
                                           hint=self.pager.addresses[0]
                                           if self.pager.addresses else None)
                    for address in found or []:
                        if address not in self.pager.addresses:
                            self.pager.remember(address)
                except Exception:
                    pass
            elapsed = (time.monotonic() - started) * 1000.0
            self._stop.wait(max(0.0, self.poll_ms - elapsed) / 1000.0)

    def _decode(self, items: list[tuple[int, bytes]]) -> list[dict]:
        """Decode a handful of stored rows -- used for the tail check and the write."""
        if not items:
            return []
        stride = self.layout.stride
        buffer = b"".join(raw for _, raw in items)
        offsets = [index * stride for index in range(len(items))]
        return decode_rows(buffer, offsets, self.layout, base_address=0)

    def tail_check(self, sample: int = 60) -> tuple[int, int, int]:
        """Where the invalid tail starts, judged from the deepest rows held.

        Reading `is_clean` out of raw bytes would mean guessing which byte of the flag
        block it is; decoding the deepest 60 rows costs a millisecond and guesses
        nothing. The tail is only believed when a body of invalid rows sits at the
        bottom of what we have -- a stray invalid row up top is stale pool content.
        """
        with self.lock:
            ranks = sorted(self.raw)[-sample:]
            items = [(rank, self.raw[rank]) for rank in ranks]
        rows = self._decode(items)
        invalid = [int(row["rank"]) for row in rows if not row.get("is_clean")]
        valid = [int(row["rank"]) for row in rows if row.get("is_clean")]
        max_valid = max(valid) if valid else 0
        if len(invalid) >= 25 and (not valid or min(invalid) >= max_valid - 50):
            return min(invalid), max_valid, len(invalid)
        return 0, max_valid, len(invalid)

    def stats(self) -> dict:
        invalid_from, max_valid, invalid_seen = self.tail_check()
        with self.lock:
            ranks = list(self.raw.keys())
            named = sum(1 for value in self.named.values() if value)
            low = min(ranks) if ranks else 0
            high = max(ranks) if ranks else 0
            return {
                "rows": len(ranks),
                "min_rank": low,
                "max_rank": high,
                "missing_in_span": (high - low + 1 - len(ranks)) if ranks else 0,
                "named": named,
                "polls": self.polls,
                "empty_polls": self.empty_polls,
                "decoded": self.decoded,
                "rejected": self.rejected,
                "invalid_seen": invalid_seen,
                "max_valid": max_valid,
                "invalid_from": invalid_from,
                "avg_poll_ms": round(self.poll_ms_total / max(1, self.polls), 1),
                "addresses": len(self.pager.addresses),
            }

    def write(self, path: Path) -> int:
        """Decode every stored row once and write it out, sorted by rank."""
        with self.lock:
            ordered = [(rank, self.raw[rank]) for rank in sorted(self.raw)]
        rows = self._decode(ordered)
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open("w", encoding="utf-8") as handle:
            for row in rows:
                handle.write(json.dumps(row, separators=(",", ":")) + chr(10))
        return len(rows)


def emit(payload: dict) -> None:
    sys.stdout.write(json.dumps(payload, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def serve(pager: Pager, memory, layout, profile: Path | None, *,
          poll_ms: float, default_timeout_ms: float,
          relocate_after_ms: float, relocate_budget_ms: float) -> None:
    # Consecutive relocations that found nothing. A bounded search is cheap and
    # usually enough; a board whose arena has really moved still needs the full
    # sweep, so every fourth miss pays for one.
    misses = 0
    state: dict = {"recorder": None}
    for line in sys.stdin:
        # PowerShell 5.1 writes a UTF-8 BOM into a redirected stdin and offers no
        # way to turn it off, so the first request arrives with one attached and
        # json.loads rejects it. Strip it here rather than requiring every caller
        # to get its stream encoding right.
        line = line.lstrip("﻿").strip()
        if not line:
            continue
        try:
            request = json.loads(line)
        except ValueError:
            emit({"ok": False, "reason": "bad json"})
            continue

        if request.get("quit"):
            if state["recorder"] is not None:
                state["recorder"].stop()
            emit({"ok": True, "bye": True})
            return
        if request.get("ping"):
            emit({"ok": True, "pong": True, "addresses": len(pager.addresses)})
            continue

        # Recording: the server samples the pool on its own thread, so the caller can
        # spend all of its time pressing keys. Requests are start/stats/write/stop.
        if request.get("record"):
            options = request["record"] if isinstance(request["record"], dict) else {}
            poll_ms = float(options.get("poll_ms", 16.0))
            if state["recorder"] is None:
                state["recorder"] = Recorder(pager, layout, memory, poll_ms=poll_ms)
            state["recorder"].poll_ms = poll_ms
            state["recorder"].start()
            emit({"ok": True, "recording": True, "poll_ms": poll_ms})
            continue
        if request.get("stats"):
            if state["recorder"] is None:
                emit({"ok": False, "reason": "not recording"})
            else:
                emit({"ok": True, **state["recorder"].stats()})
            continue
        if request.get("write"):
            if state["recorder"] is None:
                emit({"ok": False, "reason": "not recording"})
                continue
            target = Path(str(request["write"]))
            written = state["recorder"].write(target)
            emit({"ok": True, "written": written, "path": str(target),
                  **state["recorder"].stats()})
            continue
        if request.get("stop_record"):
            if state["recorder"] is not None:
                state["recorder"].stop()
            emit({"ok": True, "stopped": True})
            continue

        # Harvest: everything resident RIGHT NOW, with no rank to wait for.
        # The paging loop asks for one specific rank and throws away whatever
        # else the block held, which is wrong when the game has prefetched
        # ahead: those rows are already paid for. Harvesting also lets the
        # caller keep scrolling instead of blocking on a rank model.
        if request.get("harvest"):
            started = time.monotonic()
            rows = pager.read_rows()
            emit({"ok": True, "rows": rows, "harvest": True,
                  "addresses": len(pager.addresses),
                  "waited_ms": int((time.monotonic() - started) * 1000)})
            continue

        wanted = request.get("want")
        if not isinstance(wanted, int):
            emit({"ok": False, "reason": "want must be an int"})
            continue

        timeout_ms = float(request.get("timeout_ms", default_timeout_ms))
        started = time.monotonic()
        deadline = started + timeout_ms / 1000.0
        relocate_at = started + relocate_after_ms / 1000.0
        relocated = False

        while True:
            rows = pager.read_rows()
            if any(int(row["rank"]) == wanted for row in rows):
                emit({"ok": True,
                      "rows": rows,
                      "waited_ms": int((time.monotonic() - started) * 1000)})
                break
            now = time.monotonic()
            # Pages alternate between heap arenas, so the next one routinely lands
            # at an address never seen before. Go and find it rather than polling a
            # stale address until the deadline.
            if not relocated and now >= relocate_at:
                relocated = True
                # Search for THIS RANK, not for any row block. Looking for any
                # block kept re-finding the one already known, so nothing was
                # added and every page after the first timed out.
                #
                # Bounded, because an unbounded sweep is 24-28 s of the whole
                # process and 12 of 52 cycles paid it on 2026-08-22 -- 58% of the
                # wall clock for pages that had merely landed at an address the
                # pager had not been told about. Regions are searched near-first,
                # so the budget keeps the part that finds them.
                misses += 1
                budget = relocate_budget_ms / 1000.0
                if misses % 4 == 0:
                    budget = None
                hit = find_rank_address(
                    memory, layout, wanted,
                    hint=pager.addresses[0] if pager.addresses else None,
                    deadline_seconds=budget)
                if hit is not None:
                    misses = 0
                if hit is not None and hit not in pager.addresses:
                    pager.remember(hit)
                    save_profile(profile, pager.addresses, layout)
                    continue
            if now >= deadline:
                emit({"ok": False, "reason": "timeout",
                      "waited_ms": int((time.monotonic() - started) * 1000)})
                break
            time.sleep(poll_ms / 1000.0)


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--process-name", default="forzahorizon6")
    parser.add_argument("--profile", type=Path)
    parser.add_argument("--read-records", type=int, default=240,
                        help="80 covers barely one 50-row page, so a page landing "
                             "slightly off the known address reads as nothing; 240 "
                             "doubled the measured rate on 2026-08-22")
    parser.add_argument("--poll-ms", type=float, default=5.0)
    parser.add_argument("--timeout-ms", type=float, default=8000.0)
    parser.add_argument("--relocate-after-ms", type=float, default=1200.0)
    parser.add_argument("--relocate-budget-ms", type=float, default=3000.0,
                        help="cap on one relocation sweep; every 4th miss ignores it")
    args = parser.parse_args(argv)

    pid = resolve_pid(args.process_name)
    with ProcessMemory(pid) as memory:
        cached = load_profile(args.profile)
        if cached:
            addresses, layout = cached
        else:
            found = discover(memory)
            if not found:
                emit({"ok": False, "reason": "no row block found"})
                return 2
            addresses, layout = found
            save_profile(args.profile, addresses, layout)

        pager = Pager(memory, addresses, layout, args.read_records, require_name=False)
        if cached and not pager.read_rows():
            # Expected on every board change: the layout survives, the addresses
            # do not. Re-find only the addresses, near the old ones.
            addresses = find_addresses(memory, layout, want_blocks=4,
                                       hint=addresses[0] if addresses else None)
            if not addresses:
                found = discover(memory)
                if not found:
                    emit({"ok": False, "reason": "no row block found"})
                    return 2
                addresses, layout = found
            save_profile(args.profile, addresses, layout)
            pager = Pager(memory, addresses, layout, args.read_records, require_name=False)

        emit({"ok": True, "ready": True, "pid": pid, "stride": layout.stride,
              "addresses": len(pager.addresses)})
        # A silent search is indistinguishable from a hang; the operator watched a
        # blinking cursor for three minutes because of that.
        print("[row-server] ready", file=sys.stderr, flush=True)
        serve(pager, memory, layout, args.profile,
              poll_ms=args.poll_ms, default_timeout_ms=args.timeout_ms,
              relocate_after_ms=args.relocate_after_ms,
              relocate_budget_ms=args.relocate_budget_ms)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
