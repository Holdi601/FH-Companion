"""Locate the leaderboard scrollbar in a frame and report where its thumb sits.

Why this exists: the earlier scan route could not tell "the game stopped serving rows" from
"the board ended". It reports `end_detected` for both, and the sweep records that
as `complete`. On 2026-08-20 that silently truncated two boards -- S1 at rank 100
in an aged session, again at 1950 in a fresh one, and A at 12000 -- all recorded
as finished. The scrollbar is a DIRECT statement about the end of the list: the
thumb sits at the bottom of its track only when the list really is at its end.

The bar is found by contrast against its immediate horizontal neighbours rather
than by fixed coordinates, so a changed layout, resolution or HUD scale does not
silently break the check into always-true.

Output is JSON on stdout:
    {"found": true, "at_bottom": false, "position": 0.0, "visible_fraction": 0.012,
     "bar": {"x0":1773,"x1":1780,"top":276,"bottom":868},
     "thumb": {"top":276,"bottom":282}}
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image


def luminance(image: Image.Image) -> np.ndarray:
    return np.asarray(image.convert("RGB")).astype(np.float64).mean(axis=2)


def find_bar_columns(lum: np.ndarray, x_from: int, x_to: int,
                     min_width: int, max_width: int,
                     min_contrast: float) -> tuple[int, int] | None:
    """The narrow column band that is persistently darker than its surroundings."""
    height, width = lum.shape
    # Compare each column's median against a median taken well to its left and
    # right. A scrollbar track is a thin, tall, uniformly darker stripe.
    middle = lum[height // 4: 3 * height // 4, :]
    column_median = np.median(middle, axis=0)
    span = 24

    darker = np.zeros(width, dtype=bool)
    for x in range(max(x_from, span), min(x_to, width - span)):
        neighbourhood = np.concatenate((
            column_median[x - span: x - 3],
            column_median[x + 4: x + span],
        ))
        if neighbourhood.size == 0:
            continue
        darker[x] = (np.median(neighbourhood) - column_median[x]) > min_contrast

    best = None
    x = 0
    while x < width:
        if not darker[x]:
            x += 1
            continue
        start = x
        while x < width and darker[x]:
            x += 1
        run_width = x - start
        if min_width <= run_width <= max_width:
            # Prefer the tallest such stripe, measured below.
            if best is None or run_width > (best[1] - best[0]):
                best = (start, x - 1)
    return best


def measure(path: Path, x_from_ratio: float, min_contrast: float,
            thumb_contrast: float, bottom_tolerance_ratio: float,
            max_gap: int) -> dict:
    image = Image.open(path)
    lum = luminance(image)
    height, width = lum.shape
    x_from = int(width * x_from_ratio)

    band = find_bar_columns(lum, x_from, width, 3, 20, min_contrast)
    if band is None:
        return {"found": False, "reason": "no scrollbar-like column band found",
                "frame": {"width": width, "height": height}}
    x0, x1 = band

    # Per-row reference taken just outside the band on both sides.
    left = lum[:, max(0, x0 - 14): max(1, x0 - 4)]
    right = lum[:, min(width - 1, x1 + 5): min(width, x1 + 15)]
    reference = np.concatenate((left, right), axis=1).mean(axis=1)
    inside = lum[:, x0: x1 + 1].mean(axis=1)
    delta = reference - inside          # >0 darker than surroundings, <0 brighter

    is_bar = np.abs(delta) > min_contrast
    # Bridge short interruptions before measuring the extent. A single row is
    # enough to cut the bar in half: at y=810 in the 2026-08-20 frame a dark UI
    # edge in the reference columns pushed delta to -1.9 for one row, which ended
    # the run at 807 instead of the true 868. Underreporting the bottom is the
    # dangerous direction -- it moves the bottom UP towards the thumb and would
    # let at_bottom fire on a board that is nowhere near its end.
    y = 0
    while y < height:
        if is_bar[y]:
            y += 1
            continue
        start = y
        while y < height and not is_bar[y]:
            y += 1
        if start > 0 and y < height and (y - start) <= max_gap:
            is_bar[start:y] = True

    # Longest contiguous run of "not background" rows is the bar itself.
    best_top = best_bottom = None
    y = 0
    while y < height:
        if not is_bar[y]:
            y += 1
            continue
        start = y
        while y < height and is_bar[y]:
            y += 1
        if best_top is None or (y - start) > (best_bottom - best_top + 1):
            best_top, best_bottom = start, y - 1
    if best_top is None or (best_bottom - best_top) < height // 8:
        return {"found": False, "reason": "column band was not a tall bar",
                "frame": {"width": width, "height": height},
                "bar": {"x0": int(x0), "x1": int(x1)}}

    # The thumb is the part of the bar BRIGHTER than the surroundings.
    thumb_rows = [y for y in range(best_top, best_bottom + 1)
                  if delta[y] < -thumb_contrast]
    result = {
        "found": True,
        "frame": {"width": width, "height": height},
        "bar": {"x0": int(x0), "x1": int(x1),
                "top": int(best_top), "bottom": int(best_bottom)},
    }
    if not thumb_rows:
        result["at_bottom"] = False
        result["thumb"] = None
        result["reason"] = "bar found but no thumb; treating as not-at-end"
        return result

    thumb_top, thumb_bottom = min(thumb_rows), max(thumb_rows)
    bar_height = best_bottom - best_top + 1
    thumb_height = thumb_bottom - thumb_top + 1
    travel = max(1, bar_height - thumb_height)
    tolerance = max(3.0, bar_height * bottom_tolerance_ratio)

    result["thumb"] = {"top": int(thumb_top), "bottom": int(thumb_bottom)}
    result["position"] = round((thumb_top - best_top) / travel, 4)
    # NOT a usable estimate of the list length: the thumb has a minimum rendered
    # size, so on a long board it bottoms out and stops measuring anything. On
    # 2026-08-20 it read 0.0101 ("about 1100 ranks") on a board that position
    # put at 22000+. Reported for diagnosis only -- derive totals from position.
    result["visible_fraction_unreliable"] = round(thumb_height / bar_height, 4)
    result["gap_to_bottom_px"] = int(best_bottom - thumb_bottom)
    # Conservative on purpose: only an unambiguous bottom counts as the end, so a
    # detector that drifts errs towards "keep scanning" rather than "board done".
    result["at_bottom"] = bool((best_bottom - thumb_bottom) <= tolerance)
    return result


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image", type=Path)
    parser.add_argument("--x-from-ratio", type=float, default=0.5,
                        help="search only right of this fraction of the width")
    parser.add_argument("--min-contrast", type=float, default=12.0,
                        help="luminance difference that counts as bar vs background")
    parser.add_argument("--thumb-contrast", type=float, default=40.0,
                        help="how much brighter than its surroundings the thumb is")
    parser.add_argument("--bottom-tolerance-ratio", type=float, default=0.01,
                        help="thumb this close to the track bottom counts as the end")
    parser.add_argument("--max-gap", type=int, default=12,
                        help="bridge interruptions this short when measuring the bar")
    args = parser.parse_args(argv)

    if not args.image.exists():
        print(json.dumps({"found": False, "reason": "image not found: %s" % args.image}))
        return 2
    print(json.dumps(measure(args.image, args.x_from_ratio, args.min_contrast,
                             args.thumb_contrast, args.bottom_tolerance_ratio,
                             args.max_gap),
                     indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
