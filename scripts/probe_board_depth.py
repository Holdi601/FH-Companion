"""Measure how deep a board really is, without reading all of it.

Why this exists: every completeness number in this project so far was measured
against the board's OWN highest rank read, which is circular -- a scan that stops
early makes its own stopping point look like the end. 31 of 44 scanned boards were
banked with no independent evidence of where they finish.

The scrollbar is that evidence, and it does not need a full pass to give it up.
Scroll a few thousand ranks so the thumb is demonstrably off the top, read its
position, and the board's length follows:

    implied_total = rank_on_screen / thumb_position

...or, when the thumb is still pinned near the top after scrolling, the SLOPE between
two parked positions, which cancels the thumb's minimum-height offset:

    implied_total = (rank2 - rank1) / (position2 - position1)

Three things make that sound, and both were learned the hard way on 2026-08-23.

The thumb has to have MOVED. At rank 100 the detector reads position 0.0017 where
arithmetic says 0.0217, because a thumb pinned near the top of its track is dominated
by its own minimum pixel height -- which is exactly how boards that had barely started
got labelled `truncated` with a thumb "at 0%". So scroll first, then read.

Rank and thumb must come from the SAME settled frame. The first calibration run paired
the chunk's highest OCR'd rank with a later screenshot: the chunk claimed 4,782 while
the view had settled at 2,335, and 4782/0.078 "proves" a 61,000-rank board where the
truth is 29,700. Misnumbered frames inflate a chunk's maximum; one screenshot cannot
disagree with itself. Paired properly the same board read 2,339/0.078 = 29,987 against
a confirmed end of 29,697 -- **+1.0%**.

And nothing may fall through silently. The first version returned early on a thumb it
could not measure, and the summary then simply had no row for that board -- so Soni S2,
reading 0.85% at rank 2,348 against a recorded length of 4,579, was the one board the
probe stayed quiet about. Every board now ends with a verdict, and "could not measure"
resolves to `needs_scan`, never to absence.

The comparison is against the board's last rank, NOT against a guessed valid end. The
22.3% invalid tail is an average across boards, not a property of each: Highway A's
last rank, 29,697, is itself a VALID lap, so assuming a tail there would have declared
the board finished some 6,500 ranks early. Where a board does have a tail, its own scan
records `invalid_laps_from_rank` and that is the number to trust.

    python scripts/probe_board_depth.py --route-indices 0-6

Writes data/analytics/board_depth_probe.json: per board the rank on screen, the thumb
position, the implied length, what we already hold, and the verdict.
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
from datetime import datetime
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import ocr_board_sweep as sweep  # noqa: E402
from detect_leaderboard_scrollbar import measure as measure_scrollbar  # noqa: E402

WORKSPACE = Path(__file__).resolve().parent.parent
SCRIPTS = WORKSPACE / "scripts"
OUT = WORKSPACE / "data/analytics/board_depth_probe.json"
SCREEN = WORKSPACE / "data/runtime/probe_screen"

# Below this the thumb has not moved far enough for a single rank/position ratio to
# mean anything on its own -- so the probe scrolls FURTHER rather than giving up.
MIN_POSITION = 0.02
# How many further 120 s chunks a pinned thumb may buy. Four more is ~11,500 extra
# ranks, enough to move the thumb off the top on anything up to roughly 500k.
MAX_EXTRA_CHUNKS = 4
# How much of the implied total we must already hold to call a board finished. 3%
# covers the estimate's own error (measured at +1.0% against Highway A's confirmed
# end) plus the handful of ranks the OCR route never renders.
COMPLETE_AT = 0.97

# The run-id builder truncates track names, so a board's own state file can say
# "Highway Circui". Left alone that splits one board's evidence across two keys and
# makes the dataset lookup miss entirely.
TRACKS = ["Daikoku Circuit", "Highway Circuit", "Hokubu Circuit",
          "Narai-Juku Circuit", "Shimanoyama Circuit", "Shirakawa Circuit",
          "Soni Circuit"]


def full_track_name(read: str) -> str:
    read = (read or "").strip()
    for track in TRACKS:
        if track.startswith(read) or read.startswith(track):
            return track
    return read


def held_by_board() -> dict[tuple[str, str], dict]:
    """What the dataset holds per (track, class): the deepest rank AND how many rows.

    Both, because they answer different questions and only one of them was being asked.
    The deepest rank says how far a scan got; the row count says how much of the board
    it actually came back with, and those diverge wildly -- Soni S2 reaches rank 4,548
    of a ~4,564 board while holding 231 rows, so "we got to the end" and "we have the
    board" are 5% apart. Judging depth alone called that complete.
    """
    path = WORKSPACE / "data/analytics/laps.json"
    if not path.exists():
        return {}
    data = json.loads(path.read_text(encoding="utf-8"))
    tracks, classes = data["tracks"], data["classes"]
    held: dict[tuple[str, str], dict] = {}
    for board in data["boards"]:
        key = (tracks[board["t"]], classes[board["k"]])
        ranks = board.get("lrank") or []
        if not ranks:
            continue
        entry = held.setdefault(key, {"max_rank": 0, "rows": 0})
        entry["max_rank"] = max(entry["max_rank"], max(ranks))
        entry["rows"] += len(ranks)
    return held


def capture_screen(vm: str) -> tuple[Path, list[int]] | None:
    """One settled frame plus the rank column read off it.

    Uses the same capture path as `capture_vm_current_screen.ps1`, which is the one
    proven to produce a frame the scrollbar detector can read -- the chunk's own
    frames did not (the first probe run reported `scrollbar_unreadable` on every one).
    """
    SCREEN.mkdir(parents=True, exist_ok=True)
    result = subprocess.run(
        ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
         str(SCRIPTS / "capture_vm_current_screen.ps1"),
         "-VMName", vm, "-OutputDir", str(SCREEN)],
        capture_output=True, text=True, timeout=300)
    frame = SCREEN / "current.png"
    ocr = SCREEN / "current.ocr.json"
    if not frame.exists() or not ocr.exists():
        sweep.log(f"  screenshot failed (exit {result.returncode})")
        return None
    data = json.loads(ocr.read_text(encoding="utf-8-sig"))
    ranks = []
    for line in data.get("lines", []):
        # The rank column sits at the far left; everything right of it is gamertag,
        # car, PI and lap time, and those carry digits too.
        if line.get("x", 9999) > 210:
            continue
        text = (line.get("text") or "").strip().replace(",", "")
        if text.isdigit():
            ranks.append(int(text))
    return frame, ranks


def read_point(vm: str) -> dict | None:
    """One (rank, thumb position) pair off a single settled frame."""
    shot = capture_screen(vm)
    if shot is None:
        return None
    frame, ranks = shot
    if not ranks:
        return None
    # The median of the visible column, not its minimum: a rank clipped by the top or
    # bottom edge of the table misreads, and the middle of eleven rows never is.
    ranks.sort()
    try:
        bar = measure_scrollbar(frame, 0.5, 12.0, 40.0, 0.01, 12)
    except Exception as error:
        sweep.log(f"  scrollbar unreadable: {error}")
        return None
    if not bar.get("found"):
        return None
    return {"rank": ranks[len(ranks) // 2], "position": float(bar["position"]),
            "at_bottom": bool(bar.get("at_bottom")), "max_rank": max(ranks)}


def probe(vm: str, category: str, route_index: int, klass: str, anchor: str,
          seconds: int, workers: int, warm: bool, staging: Path) -> dict | None:
    reached, route_name = sweep.navigate(vm, category, route_index, klass, anchor, warm)
    if not reached or not route_name:
        reached, route_name = sweep.navigate(vm, category, route_index, klass,
                                             anchor, not warm)
    if not reached or not route_name:
        sweep.log(f"idx{route_index:02d} {klass}: not reachable")
        return None

    staging.mkdir(parents=True, exist_ok=True)
    archive = staging / f"probe_idx{route_index:02d}_{klass}.zip"
    if sweep.capture_chunk(vm, seconds, archive) == 0:
        sweep.log(f"idx{route_index:02d} {klass}: nothing captured")
        return None
    rows = sweep.ocr_zip(archive, workers)
    invalid_share = None
    if rows:
        invalid_share = round(sum(1 for r in rows if r.get("is_clean") is False)
                              / len(rows), 3)

    result = {"track": full_track_name(route_name), "performance_class": klass,
              "route_index": route_index, "rows_in_probe": len(rows),
              "invalid_share_in_probe": invalid_share}

    points = []
    point = read_point(vm)
    if point is None:
        result["verdict"] = "screen_unreadable"
        return result
    points.append(point)

    # Scroll further while the thumb is still pinned near the top. Giving up here --
    # which this did until 2026-08-23 -- discards exactly the boards that matter most:
    # a thumb that has barely moved after 2,300 ranks is the signature of a board an
    # order of magnitude deeper than recorded, and Soni S2 read 0.85% at rank 2,348
    # against a recorded length of 4,579. The deeper the board, the more certainly the
    # old floor threw it away.
    extra = 0
    while points[-1]["position"] < MIN_POSITION and extra < MAX_EXTRA_CHUNKS \
            and not points[-1]["at_bottom"]:
        extra += 1
        sweep.log(f"  thumb still at {points[-1]['position']:.2%} after rank "
                  f"{points[-1]['rank']} -- scrolling further ({extra}/{MAX_EXTRA_CHUNKS})")
        more = staging / f"probe_idx{route_index:02d}_{klass}_x{extra}.zip"
        if sweep.capture_chunk(vm, seconds, more) == 0:
            break
        sweep.ocr_zip(more, workers)
        point = read_point(vm)
        if point is None:
            break
        points.append(point)

    last = points[-1]
    result.update({"rank_on_screen": last["rank"],
                   "thumb_position": round(last["position"], 4),
                   "thumb_at_bottom": last["at_bottom"],
                   "extra_chunks": extra,
                   "points": [{"rank": p["rank"], "position": round(p["position"], 4)}
                              for p in points]})

    if last["at_bottom"]:
        # The probe scrolled off the end: this board is shorter than the probe is long.
        result["implied_total"] = last["rank"]
        result["estimator"] = "reached_bottom"
        return result

    # Two points beat one. A single ratio assumes the thumb reads 0 at rank 0, and a
    # thumb has a minimum pixel height that breaks exactly that assumption near the
    # top. The SLOPE between two parked positions cancels any constant offset:
    #   total = (rank2 - rank1) / (position2 - position1)
    if len(points) >= 2:
        first = points[0]
        d_rank = last["rank"] - first["rank"]
        d_pos = last["position"] - first["position"]
        if d_rank > 0 and d_pos > 0.002:
            result["implied_total"] = int(round(d_rank / d_pos))
            result["estimator"] = "two_point_slope"
            return result

    if last["position"] >= MIN_POSITION:
        result["implied_total"] = int(round(last["rank"] / last["position"]))
        result["estimator"] = "single_point"
        return result

    # Still pinned after every extra chunk. Treating that as a FLOOR -- "the board is at
    # least rank/position long" -- was wrong, and measurement killed it on 2026-08-23:
    #
    #   Soni S2    0.85% @ rank 2348  ->  floor  276,000   re-probed: 4,564  complete
    #   Hokubu D   0.51% @ rank 1314  ->  floor  258,000   re-probed: 4,574  complete
    #   Hokubu C   0.66% @ rank 2303  ->  floor  335,000   re-probed: 4,575  complete
    #
    # Three boards, all read near-zero, all ~4,570. Two of the three had scrolled a
    # normal 2,300 ranks first, so this is not a scroll that fell short -- it is the
    # thumb detector failing on an otherwise good frame. A floor built on that number
    # hard-codes the artifact and would send a scan chasing a third of a million ranks
    # that do not exist.
    #
    # So a pinned thumb is a SUSPECT READING, not a length. It still never disappears
    # quietly -- that part was right and is what surfaced these three -- but it resolves
    # to "measure this again", and the arithmetic figure is kept only as the rejected
    # artifact, under a name nothing can mistake for a board length.
    result["rejected_artifact_total"] = int(round(last["rank"]
                                                 / max(last["position"], 0.0017)))
    result["estimator"] = "none_thumb_pinned"
    result["verdict"] = "suspect_measurement"
    result["why"] = ("thumb still pinned near the top after scrolling; the three boards "
                     "that read this way all measured ~4,570 on a clean re-probe, so "
                     "this frame's thumb is not trustworthy -- re-probe the board")
    return result


CODE_MTIME = datetime.fromtimestamp(
    Path(__file__).stat().st_mtime).astimezone().isoformat(timespec="seconds")

METHOD = ("board length = rank on screen / scrollbar thumb position, both read off "
          "one settled frame after scrolling; calibrated at +1.0% against Highway A's "
          "confirmed end (29,987 implied vs 29,697 actual)")


def bank(row: dict) -> None:
    """Merge one board's result into the report, re-reading the file first.

    The snapshot loaded at startup is NOT safe to write back. On 2026-08-23 two probes
    overlapped and the older process's in-memory copy overwrote the newer one's result:
    Soni S2, freshly measured at 4,564 and `complete`, reverted to
    `thumb_too_high_to_measure` because a run that started 12 minutes earlier had never
    heard about it. That is the same silent loss this whole probe exists to prevent, so
    re-read, replace only this board's row, and write.

    A resolved name also retires its own placeholder: a board that failed navigation is
    banked as `route_05`, and once the retry reads it as `Soni Circuit` the placeholder
    is a second row for one board that still says `not_probed`.
    """
    boards = []
    if OUT.exists():
        boards = json.loads(OUT.read_text(encoding="utf-8")).get("boards", [])
    klass = row["performance_class"]
    stale = {row["track"], f"route_{row['route_index']:02d}"}
    boards = [r for r in boards
              if not (r["track"] in stale and r["performance_class"] == klass)]
    row["probe_code_mtime"] = CODE_MTIME
    boards.append(row)
    OUT.write_text(json.dumps(
        {"probed_at": datetime.now().astimezone().isoformat(),
         "method": METHOD,
         "boards": sorted(boards, key=lambda r: (r["track"], r["performance_class"]))},
        indent=2), encoding="utf-8")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--vm", default="ForzaScrapeVM")
    parser.add_argument("--category", default="Road Racing")
    parser.add_argument("--anchor", default="Highway Circuit")
    parser.add_argument("--route-indices", default="0-6")
    parser.add_argument("--classes", default=",".join(sweep.CLASSES))
    parser.add_argument("--seconds", type=int, default=120,
                        help="how long to scroll before reading the thumb")
    parser.add_argument("--workers", type=int, default=12)
    args = parser.parse_args(argv)

    if "-" in args.route_indices:
        low, high = args.route_indices.split("-")
        indices = list(range(int(low), int(high) + 1))
    else:
        indices = [int(p) for p in args.route_indices.split(",") if p.strip()]
    classes = [p.strip() for p in args.classes.split(",") if p.strip()]

    held = held_by_board()
    staging = WORKSPACE / "data/runtime/depth_probe"

    for route_index in indices:
        for klass in classes:
            sweep.log(f"idx{route_index:02d} {klass}: probing")
            row = probe(args.vm, args.category, route_index, klass, args.anchor,
                        args.seconds, args.workers, True, staging)
            if row is None:
                # A board that could not be reached or filmed still belongs in the
                # report. Skipping it here is the same silent-drop that hid Soni S2:
                # an absent row reads as "nothing to do" when it means "unknown".
                row = {"track": f"route_{route_index:02d}", "performance_class": klass,
                       "route_index": route_index, "verdict": "not_probed",
                       "why": "board could not be navigated to or filmed"}
            info = held.get((row["track"], klass), {"max_rank": 0, "rows": 0})
            have = info["max_rank"]
            row["held_max_valid_rank"] = have
            row["rows_held"] = info["rows"]
            if "verdict" not in row and "implied_total" not in row:
                # Never let a board fall through without a verdict. The first version
                # returned early on an unmeasurable thumb and the summary simply had
                # no row for it -- so the boards the probe exists to catch were the
                # ones it stayed quiet about. Unmeasured means suspect, not fine.
                row["verdict"] = "needs_scan"
                row["why"] = "could not measure the board length; assuming incomplete"
            if "verdict" not in row:
                # Against the board's own last rank, not against a guessed valid end.
                # The 22.3% invalid share is an average over boards, not a property of
                # each one: Highway A's last rank, 29,697, is a VALID lap, so assuming
                # a tail there would have declared it finished 6,500 ranks early.
                total = row["implied_total"]
                # Two separate numbers, because conflating them is what made 5%-covered
                # boards read as finished. `depth_coverage` is how far down the scan got;
                # `row_coverage` is how much of the board it brought back. A verdict of
                # `end_reached` speaks ONLY to the first -- it is not a claim of having
                # the board, and calling it `complete` was the error.
                row["depth_coverage"] = round(have / total, 3) if total else None
                row["row_coverage"] = (round(info["rows"] / total, 3) if total else None)
                row["verdict"] = ("end_reached" if have >= total * COMPLETE_AT
                                  else "needs_scan")
                row["ranks_missing"] = max(0, total - have)
                row["rows_missing"] = max(0, total - info["rows"])
            length = row.get("implied_total")
            shown = (f"~{length}" if length
                     else f"SUSPECT(thumb implied {row['rejected_artifact_total']})"
                     if row.get("rejected_artifact_total") else "UNMEASURED")
            sweep.log(f"  {row['track']} {klass}: screen rank {row.get('rank_on_screen')}, "
                      f"thumb {row.get('thumb_position')}, board {shown} "
                      f"[{row.get('estimator', '-')}], depth {have} "
                      f"({row.get('depth_coverage', '?')}), rows {row.get('rows_held')} "
                      f"({row.get('row_coverage', '?')}) -> {row['verdict']}")
            bank(row)
    return 0


if __name__ == "__main__":
    sys.exit(main())
