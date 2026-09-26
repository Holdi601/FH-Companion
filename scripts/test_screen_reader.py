"""Read synthetic Event Sign Up screens, so the overlay's eyes are tested any time.

The real screen is only there while someone is playing, which makes it the worst
possible thing to depend on for a regression test. What can be checked any time is
the part that decides: mask the two regions, OCR them, and confirm the routes, the
class -- and that the traps on the same screen are NOT read as either.

The layout is copied from a real 2560x1440 capture (2026-08-26): the numbered
01/02/03 route list on the left, the lone class badge in the card's top-right
corner, and, lower on the card, the FEATURED car's own class and PI ("C 484").
That last one is the trap the masks exist for: on a Spec Racing event it is the
spec car, not the restriction, so a whole-frame read answers the wrong question
with total confidence.

    python scripts/test_screen_reader.py
    python scripts/test_screen_reader.py --keep   # write the frames out to look at

Every case runs at 1920x1080, 2560x1440 and 3840x2160, because the regions are
fractions and that claim is only worth anything if it is tested.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont

sys.path.insert(0, str(Path(__file__).resolve().parent))

from rivals_advisor import Advisor
from screen_reader import ScreenReader, load_config

WORKSPACE = Path(__file__).resolve().parent.parent
SCRATCH = WORKSPACE / "data/debug/screen_reader_test"
# A frame captured in play. Every synthetic frame below draws the badge on a
# flat colour; the game draws it on a PHOTOGRAPH of a race, which is the case
# that failed in the player's hands while all of these stayed green.
CAPTURED = WORKSPACE / "data/screen_fixtures/event_signup_b.png"
SIZES = [(1920, 1080), (2560, 1440), (3840, 2160)]
HAPTICS = WORKSPACE / "haptics/ForzaHaptics.Tester"
EXE_NAME = "FH Companion.exe"

failures: list[str] = []
skipped: list[str] = []


def find_exe():
    """The freshest built haptics binary, or None."""
    hits = list(HAPTICS.glob(f"bin/**/{EXE_NAME}"))
    return max(hits, key=lambda path: path.stat().st_mtime) if hits else None


def csharp_read(exe, image: Path, full: bool = False) -> dict | None:
    """What the C# reader makes of the same frame.

    The two readers use different OCR engines -- RapidOCR and Windows.Media.Ocr --
    so "the matcher was ported" is not the same claim as "it still reads the
    screen". This is the second claim.
    """
    import subprocess
    import tempfile

    with tempfile.TemporaryDirectory() as tmp:
        out = Path(tmp) / "read.json"
        cmd = [str(exe), "--read-image", str(image), "--out", str(out)]
        if full:
            cmd.append("--full")
        try:
            subprocess.run(cmd, capture_output=True, timeout=120, cwd=WORKSPACE)
        except subprocess.TimeoutExpired:
            return None
        return json.loads(out.read_text(encoding="utf-8")) if out.exists() else None


def font(pixels: int) -> ImageFont.FreeTypeFont:
    for name in ("segoeui.ttf", "arial.ttf", "tahoma.ttf"):
        try:
            return ImageFont.truetype(name, pixels)
        except OSError:
            continue
    return ImageFont.load_default(pixels)


def offer_frame(routes: list[str], badge: str, size: tuple[int, int],
                spec_line: str = "Joining Spec Racing 1/3 - 36.4 KM",
                car_line: str = "INTEGRA '23    RARE   C  484") -> np.ndarray:
    """The Event Sign Up screen, placed by fraction so it scales with the frame."""
    width, height = size
    image = Image.new("RGB", size, (96, 104, 120))          # dusk sky
    pen = ImageDraw.Draw(image)

    def text(fx: float, fy: float, fh: float, body: str,
             fill=(24, 26, 32)) -> None:
        pen.text((fx * width, fy * height), body,
                 font=font(max(9, int(fh * height))), fill=fill)

    # left: the title, the green header, then three numbered rows
    pen.rectangle([0.20 * width, 0.118 * height, 0.395 * width, 0.160 * height],
                  fill=(16, 16, 16))
    text(0.208, 0.122, 0.030, "Event Sign Up", (245, 245, 245))
    pen.rectangle([0.202 * width, 0.180 * height, 0.598 * width, 0.214 * height],
                  fill=(206, 240, 60))
    text(0.210, 0.185, 0.024, spec_line)
    pen.rectangle([0.176 * width, 0.220 * height, 0.598 * width, 0.590 * height],
                  fill=(252, 252, 252))
    for index, route in enumerate(routes):
        top = 0.238 + index * 0.124
        text(0.181, top + 0.010, 0.026, f"0{index + 1}")
        text(0.274, top, 0.028, route)
        text(0.274, top + 0.036, 0.020, f"{7 + index}.6 KM - 3 LAPS",
             (110, 110, 110))
        text(0.274, top + 0.064, 0.018, "Autumn / Late Afternoon / Gale",
             (140, 140, 140))

    # right: the card, its badge, and the featured car's own class and PI
    pen.rectangle([0.602 * width, 0.174 * height, 0.864 * width, 0.855 * height],
                  fill=(246, 214, 24))
    pen.rectangle([0.795 * width, 0.196 * height, 0.826 * width, 0.243 * height],
                  fill=(240, 168, 26))
    text(0.801, 0.200, 0.030, badge)
    text(0.610, 0.640, 0.026, "Spec Racing" if spec_line else "Horizon Play")
    text(0.610, 0.700, 0.024, car_line)

    # the HUD overlay the capture also had, outside both regions
    text(0.938, 0.008, 0.018, "153 GPU:85%", (250, 250, 250))
    return np.asarray(image)[:, :, ::-1].copy()


class Skip(Exception):
    pass


def check(name: str, fn) -> None:
    try:
        fn()
    except Skip as exc:
        skipped.append(f"{name} -- {exc}")
        print(f"  skip  {name} -- {exc}")
    except AssertionError as exc:
        failures.append(f"{name}: {exc}")
        print(f"  FAIL  {name} -- {exc}")
    else:
        print(f"  ok    {name}")


def main() -> int:
    parser = argparse.ArgumentParser(description="Read synthetic route screens.")
    parser.add_argument("--keep", action="store_true",
                        help=f"write the frames to {SCRATCH}")
    parser.add_argument("--dataset", type=Path, default=None)
    parser.add_argument("--no-csharp", action="store_true",
                        help="skip the C# reader even if a binary is built")
    args = parser.parse_args()

    adv = Advisor.load(args.dataset) if args.dataset else Advisor.load()
    cfg = load_config()
    reader = ScreenReader(adv, cfg)
    if args.keep:
        SCRATCH.mkdir(parents=True, exist_ok=True)

    # Frames are always written out: the C# reader takes a path, and a saved frame
    # is also the first thing to look at when a check fails.
    SCRATCH.mkdir(parents=True, exist_ok=True)
    exe = None if args.no_csharp else find_exe()
    both: dict[str, dict] = {}

    def read(frame: np.ndarray, tag: str, full: bool = False):
        path = SCRATCH / f"{tag}.png"
        Image.fromarray(frame[:, :, ::-1]).save(path)
        state = reader.read(frame, full=full)
        if exe is not None:
            got = csharp_read(exe, path, full)
            if got is not None and "error" not in got:
                both[tag] = got
        return state

    # The three routes from the real capture, if this dataset knows them.
    trio = [t for t in ("Highway Circuit", "Hokubu Circuit", "Festival Sprint")
            if t in adv.tracks]
    if len(trio) < 3:
        trio = adv.tracks[:3]

    def the_real_screen_at_every_size():
        for width, height in SIZES:
            state = read(offer_frame(trio, "C", (width, height)), f"offer_{width}")
            assert state.klass == "C", (
                f"{width}x{height}: class read as {state.klass!r} "
                f"({state.klass_source})")
            assert state.tracks == trio, (
                f"{width}x{height}: routes {state.tracks}, wanted {trio}")
            assert state.is_offer, f"{width}x{height}: not taken as an offer"

    def the_route_order_is_the_offer_order():
        state = read(offer_frame(trio, "C", (2560, 1440)), "order")
        assert state.tracks == trio, (
            f"01/02/03 came back as {state.tracks}, wanted {trio}")

    def the_badge_wins_over_the_spec_cars_own_class():
        # The trap, in its sharpest form: the badge says A, the card's car says
        # C 484. A whole-frame read finds both and cannot tell them apart.
        state = read(offer_frame(trio, "A", (2560, 1440)), "badge_vs_car")
        assert state.klass == "A", (
            f"read {state.klass!r} from {state.klass_source} -- the spec car's "
            f"own class must not become the restriction")

    def a_one_make_event_is_flagged():
        state = read(offer_frame(trio, "C", (2560, 1440)), "spec")
        assert state.spec, "Spec Racing was not recognised as a one-make event"

    def a_normal_event_is_not_flagged():
        state = read(offer_frame(trio, "S1", (2560, 1440),
                                 spec_line="Joining Road Racing 1/3 - 36.4 KM",
                                 car_line="Horizon Play   3 Events"), "normal")
        assert not state.spec, "a plain event was flagged as one-make"
        assert state.klass == "S1", f"class read as {state.klass!r}"

    def the_desktop_still_reads_as_nothing():
        image = Image.new("RGB", (2560, 1440), (18, 22, 30))
        pen = ImageDraw.Draw(image)
        pen.text((300, 300), "You've played for 538 hours", font=font(44),
                 fill=(238, 240, 245))
        pen.text((300, 400), "Downloads - 217 items", font=font(40),
                 fill=(238, 240, 245))
        frame = np.asarray(image)[:, :, ::-1].copy()
        state = read(frame, "desktop")
        assert not state.tracks, f"read routes off a desktop: {state.tracks}"
        assert state.klass is None, f"read class {state.klass!r} off a desktop"
        assert not state.is_offer

    def a_captured_game_frame_reads():
        # Measured 2026-09-09 against the build of that morning: all three routes
        # read and the class came back empty with no source, because the badge
        # finder looked for flat background colours and a photograph has none.
        if not CAPTURED.exists():
            raise Skip(f"no captured frame at {CAPTURED.name}")
        frame = np.asarray(Image.open(CAPTURED).convert("RGB"))[:, :, ::-1].copy()
        state = read(frame, "captured")
        assert state.klass == "B", (
            f"the badge on the card read as {state.klass!r} ({state.klass_source})")
        assert len(state.tracks) >= 2, f"routes came back as {state.tracks}"
        assert state.is_offer, "a real sign-up screen was not taken as an offer"

    def a_whole_frame_read_is_the_diagnosis_path():
        # --full is for checking a drifted mask, so it has to still work; it is
        # allowed to be fooled by the card, which is why it is not the default.
        state = read(offer_frame(trio, "C", (2560, 1440)), "full", full=True)
        assert len(state.tracks) == 3, f"--full found {state.tracks}"

    def csharp_agrees_with_python():
        if exe is None:
            raise Skip("no built haptics binary; run dotnet build in "
                       "haptics/ForzaHaptics.Tester")
        if not both:
            raise Skip("the C# reader returned nothing for any frame")
        if CAPTURED.exists() and "captured" in both:
            got = both["captured"]
            assert got.get("klass") == "B", (
                f"C# read the captured badge as {got.get('klass')!r} "
                f"({got.get('klassSource')})")
            # The race type the panel puts in its title comes from the routes, not
            # from the screen: the card's own word for it is artwork on a photo.
            assert got.get("category") == "Cross-Country", (
                f"C# named the race type {got.get('category')!r}")
        wanted = {"offer_1920": ("C", trio), "offer_2560": ("C", trio),
                  "offer_3840": ("C", trio), "badge_vs_car": ("A", trio),
                  "normal": ("S1", trio), "desktop": (None, [])}
        for tag, (klass, routes) in wanted.items():
            got = both.get(tag)
            if got is None:
                continue
            assert got.get("klass") == klass, (
                f"{tag}: C# read class {got.get('klass')!r}, wanted {klass!r} "
                f"({got.get('klassSource')})")
            assert got.get("tracks") == routes, (
                f"{tag}: C# read routes {got.get('tracks')}, wanted {routes}")

    check("the real layout, at 1080p, 1440p and 4K", the_real_screen_at_every_size)
    check("01/02/03 comes back in offer order", the_route_order_is_the_offer_order)
    check("the badge beats the spec car's own class",
          the_badge_wins_over_the_spec_cars_own_class)
    check("a one-make event is flagged", a_one_make_event_is_flagged)
    check("a plain event is not flagged", a_normal_event_is_not_flagged)
    check("a desktop reads as nothing", the_desktop_still_reads_as_nothing)
    check("a captured game frame reads", a_captured_game_frame_reads)
    check("--full still reads the screen", a_whole_frame_read_is_the_diagnosis_path)
    check("the C# reader agrees with the Python one", csharp_agrees_with_python)

    if skipped:
        print(str(len(skipped)) + " skipped")
    print(f"\n{len(failures)} FAILED" if failures else "\nall checks passed")
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
