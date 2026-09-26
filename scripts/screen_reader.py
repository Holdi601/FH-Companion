"""Read the Horizon Play screen: which three routes, and which class.

The overlay has to know what the game is offering before it can say what to
drive. This reads that off the screen -- and deliberately does NOT depend on
where the game draws it. The dataset already carries a closed vocabulary of 23
route names and 7 classes, so every OCR'd line is matched against that list
instead of against a row geometry. A patch that moves the cards, a different
aspect ratio or a language change in the surrounding chrome therefore costs
nothing, which the leaderboard reader's column map cannot claim.

    python scripts/screen_reader.py --shot                 # grab the screen, read it, report
    python scripts/screen_reader.py --image some/frame.png  # read a saved frame
    python scripts/screen_reader.py --watch                 # keep reading, print changes

`--image` is the calibration path: point it at a capture of the Horizon Play
screen and it prints every line it read, what it matched, and what it rejected.
"""

from __future__ import annotations

import argparse
import json
import re
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Iterable, Sequence

import numpy as np

WORKSPACE = Path(__file__).resolve().parent.parent
CONFIG_PATH = WORKSPACE / "config/overlay.json"

# Forza's PI bands. Only a fallback: a class token on screen always wins, because
# the number next to a card can be the PI cap, the reward, or a distance.
# BERICHTIGT am 2026-09-26, wie der C#-Leser am 2026-09-14: die Zahlen sind die
# OBERGRENZEN der Klassen (D bis 400, C bis 500, ...). Hier stand 500 fuer D, jede
# Klasse war um eine Stufe verschoben.
PI_BANDS = [(400, "D"), (500, "C"), (600, "B"), (700, "A"), (800, "S1"),
            (900, "S2"), (9999, "R")]

# HORIZON PLAY UND ANDERE REIHEN (seit 2026-09-26, wie RivalsScreenReader.cs): die
# Statusspalte neben den Strecken und die Ueberschrift "Joining ... 2/3". Die
# Texterkennung liest den Schraegstrich gern als 1 ("213").
STATUS_IN_PROGRESS = "InProgress"
STATUS_UP_NEXT = "UpNext"
SERIES_PATTERN = re.compile(
    r"J[o0]in[il1]ng\s+(?P<name>.+?)\s+(?P<k>[1-9])\s*[/1Il|\\]\s*(?P<n>[1-9])(?!\d)",
    re.IGNORECASE)
# Dieselbe Zeile wie der Streckenname; die naechste Strecke steht ~175 px tiefer.
STATUS_NEAR_Y = 50.0

# The class as the game writes it, in the two orders it uses, plus the bare
# "A 800" form. `R` is included because FH6's Rivals boards have an R class;
# it has no PI band, so only a token can produce it.
CLASS_TOKEN = r"(S\s?1|S\s?2|[ABCDRX])"
CLASS_PATTERNS = [
    re.compile(CLASS_TOKEN + r"\s*[-:]?\s*CLASS\b", re.I),
    re.compile(r"\bCLASS\s*[-:]?\s*" + CLASS_TOKEN + r"\b", re.I),
    re.compile(r"\b" + CLASS_TOKEN + r"\s*[-:]?\s*(\d{3})\b", re.I),
]
PI_PATTERN = re.compile(r"\b([1-9]\d{2})\b")

# The Event Sign Up screen prints the restriction as a LONE badge -- a single "C"
# in the card's top-right corner, with no word "class" anywhere near it. Matched
# only once the routes have confirmed the screen, because a stray capital letter
# is otherwise the easiest thing in the world to find.
LONE_CLASS = re.compile(r"^(S\s?1|S\s?2|[ABCDRX])$", re.I)

# A one-make event: every entrant drives the same car, so a ranking of 400 cars
# is not advice, it is noise. The card says so in words.
SPEC_PATTERN = re.compile(r"\bSPEC\s*RACING\b|\bONE[\s-]?MAKE\b", re.I)

DEFAULT_CONFIG: dict[str, Any] = {
    "region": None,            # [left, top, right, bottom] in screen pixels, or null
    # The two places worth reading on the Event Sign Up screen, as FRACTIONS of
    # the frame so one setting fits 1080p, 1440p and 4K alike. Everything else is
    # masked away before the OCR ever sees it: the card's lower half prints the
    # featured car's own class and PI ("C 484"), which on a Spec Racing event is
    # the spec car and not the restriction -- reading it would silently answer
    # the wrong question. Measured off a 2560x1440 capture, 2026-08-26.
    #   routes: the numbered 01/02/03 list on the left, green header included so
    #           a one-make event can still be recognised
    #   class : the lone badge in the card's top-right corner
    "region_routes": [0.16, 0.165, 0.62, 0.62],
    "region_class": [0.76, 0.15, 0.90, 0.28],
    # Off by default: a whole-frame sweep is what read a spec car's PI as the
    # restriction. Turn it on only to diagnose a mask that has drifted.
    "full_frame_fallback": False,
    "poll_seconds": 1.5,
    "change_threshold": 2.0,   # mean abs grey difference that counts as a new screen
    "min_ocr_seconds": 0.8,
    "ocr_gpu": False,          # the game wants the GPU; text this size reads fine on CPU
    # Menu text is oversized at 4K, so the OCR runs on a scaled copy: measured
    # 2.0 s per masked read at 1920, 1.27 s at 1100, for the same lines. Applies
    # per region, so the small class crop is never scaled at all. 0 = native.
    "max_ocr_width": 1100,
    "min_tracks": 2,           # below this the screen is not a route offer
    "max_tracks": 3,
    "track_cutoff": 0.62,
    "car_cutoff": 0.86,        # a car name has to be near-certain, names are everywhere
    "telemetry_port": 5300,
    # Forza sends its stream to ONE endpoint, and a UDP port takes ONE listener:
    # with the haptics tool already bound to 5300 the overlay fails with WinError
    # 10013, and the other way round the haptics tool fails with 10048. So the
    # overlay can pass every packet straight on to a second port -- point the
    # haptics tool's own port box at it and both run. null = do not relay.
    "telemetry_relay_port": None,
    # Windows virtual-key codes; F1 is 112, so F6=117 F7=118 F8=119 F9=120 F10=121.
    # The two panels have separate triggers because they answer separate
    # questions and are never on screen together.
    "hotkey_right_vk": 119,    # F8  -- read the screen, show what to drive
    "hotkey_left_vk": 117,     # F6  -- show the current car's standings
    "hotkey_score_vk": 118,    # F7  -- points order <-> time-sum order
    "hotkey_pin_vk": 120,      # F9  -- keep the panel that is up
    "hotkey_quit_vk": 121,     # F10
    "gamepad_right": ["BACK"],        # View button
    "gamepad_left": ["LEFT_THUMB"],   # left stick click
    "side_width_fraction": 0.26,
    "opacity": 0.9,
    # The overlay is a glance, not furniture: a trigger shows it for this long
    # and it takes itself away again -- including when the read found nothing,
    # so a failure message never becomes permanent.
    "show_seconds": 30,
    "auto_show": True,
    # "points" or "time": the two scores answer different questions and the
    # overlay shows ONE of them, so a glance is a single ranked list.
    "score_mode": "points",
}


def load_config(path: Path | str = CONFIG_PATH) -> dict[str, Any]:
    cfg = dict(DEFAULT_CONFIG)
    path = Path(path)
    if path.exists():
        try:
            cfg.update(json.loads(path.read_text(encoding="utf-8-sig")))
        except (ValueError, OSError) as exc:
            print(f"[overlay] ignoring {path}: {exc}")
    return cfg


# --------------------------------------------------------------------------- #
# what one frame said
# --------------------------------------------------------------------------- #

@dataclass(slots=True)
class Line:
    text: str
    x: float
    y: float
    score: float


@dataclass(slots=True)
class ScreenState:
    tracks: list[str] = field(default_factory=list)
    track_scores: dict[str, float] = field(default_factory=dict)
    klass: str | None = None
    klass_source: str = ""
    car: int | None = None
    car_name: str | None = None
    spec: bool = False
    lines: list[Line] = field(default_factory=list)
    read_seconds: float = 0.0
    # Horizon Play und andere Reihen (siehe SERIES_PATTERN).
    track_status: dict[str, str] = field(default_factory=dict)
    series: str | None = None
    series_index: int = 0
    series_count: int = 0

    @property
    def is_offer(self) -> bool:
        """Enough to answer with: at least two routes and a class."""
        return bool(self.klass) and len(self.tracks) >= 2

    @property
    def first_own_index(self) -> int:
        """Ab welcher Strecke man selbst faehrt: "Up Next", dann "2/3", dann nach "In Progress"."""
        for i, name in enumerate(self.tracks):
            if self.track_status.get(name) == STATUS_UP_NEXT:
                return i
        if 1 <= self.series_index <= len(self.tracks):
            return self.series_index - 1
        for i, name in enumerate(self.tracks):
            if self.track_status.get(name) == STATUS_IN_PROGRESS and i + 1 < len(self.tracks):
                return i + 1
        return 0

    @property
    def remaining_tracks(self) -> list[str]:
        return self.tracks[self.first_own_index:]

    def key(self) -> tuple:
        if self.series_index or self.track_status:
            return (tuple(self.tracks), self.klass, self.first_own_index)
        return (tuple(self.tracks), self.klass)


def status_in(text: str) -> str | None:
    """"In Progress" oder "Up Next" in einer Zeile -- auch angehaengt und verlesen."""
    from rivals_advisor import _normalise, _similarity

    norm = _normalise(text)
    if not norm:
        return None
    if norm.endswith("in progress"):
        return STATUS_IN_PROGRESS
    if norm.endswith("up next"):
        return STATUS_UP_NEXT
    words = norm.split(" ")
    if len(words) < 2:
        return None
    tail = words[-2] + " " + words[-1]
    if _similarity(tail, "in progress") >= 0.8:
        return STATUS_IN_PROGRESS
    if _similarity(tail, "up next") >= 0.8:
        return STATUS_UP_NEXT
    return None


# --------------------------------------------------------------------------- #
# capture
# --------------------------------------------------------------------------- #

def grab_screen(region: Sequence[int] | None = None) -> np.ndarray:
    """The primary screen as BGR, which is what the OCR and cv2 expect."""
    from PIL import ImageGrab

    box = tuple(int(v) for v in region) if region else None
    shot = ImageGrab.grab(bbox=box)
    return np.asarray(shot.convert("RGB"))[:, :, ::-1].copy()


# --------------------------------------------------------------------------- #
# the game's area, at any resolution
# --------------------------------------------------------------------------- #

# Everything the scan measures -- table columns, scrollbar, the still-image OCR --
# was measured on 1920x1080. Instead of asking every contributor to set exactly
# that, the game's area is captured and brought to this reference frame, so the
# 1080p measurements hold at 720p, 1440p, 4K, 8K and 16K alike.
REFERENCE = (1920, 1080)

GAME_PROCESS = "forzahorizon6.exe"


def _dpi_aware() -> None:
    """Physical pixels, not the scaled ones Windows fakes for old programs."""
    import ctypes
    try:
        ctypes.windll.user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
    except Exception:
        try:
            ctypes.windll.user32.SetProcessDPIAware()
        except Exception:
            pass


def game_area(process: str = GAME_PROCESS) -> tuple[int, int, int, int] | None:
    """The game window's client area in screen pixels: (x, y, width, height).

    The same answer as the app's GameArea and the navigator: the visible top-level
    "App" window of the game process. None when the game has no such window.
    """
    import ctypes
    from ctypes import wintypes

    _dpi_aware()
    user32 = ctypes.windll.user32
    kernel32 = ctypes.windll.kernel32
    gefunden: list[int] = []

    def name_of(pid: int) -> str:
        h = kernel32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
        if not h:
            return ""
        try:
            buf = ctypes.create_unicode_buffer(1024)
            size = wintypes.DWORD(1024)
            if kernel32.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(size)):
                return Path(buf.value).name.lower()
            return ""
        finally:
            kernel32.CloseHandle(h)

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def callback(hwnd, _):
        if not user32.IsWindowVisible(hwnd):
            return True
        cls = ctypes.create_unicode_buffer(64)
        user32.GetClassNameW(hwnd, cls, 64)
        if cls.value != "App":
            return True
        pid = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if name_of(pid.value) == process.lower():
            gefunden.append(hwnd)
            return False
        return True

    user32.EnumWindows(callback, 0)
    if not gefunden:
        return None
    rect = wintypes.RECT()
    punkt = wintypes.POINT(0, 0)
    if not user32.GetClientRect(gefunden[0], ctypes.byref(rect)):
        return None
    user32.ClientToScreen(gefunden[0], ctypes.byref(punkt))
    w, h = rect.right - rect.left, rect.bottom - rect.top
    if w < 640 or h < 360:
        return None
    return punkt.x, punkt.y, w, h


def sixteen_nine(area: tuple[int, int, int, int]) -> tuple[int, int, int, int]:
    """The centred 16:9 part of an area -- measured layouts are 16:9 (an
    ASSUMPTION for other aspect ratios, see the navigator)."""
    x, y, w, h = area
    if w * 9 > h * 16:
        nw, nh = round(h * 16 / 9), h
    elif w * 9 < h * 16:
        nw, nh = w, round(w * 9 / 16)
    else:
        nw, nh = w, h
    return x + (w - nw) // 2, y + (h - nh) // 2, nw, nh


def grab_game(area: tuple[int, int, int, int] | None = None) -> np.ndarray:
    """The game's 16:9 area as a 1920x1080 BGR reference frame.

    Falls back to the primary screen when no game window is found -- the old
    behaviour, as the last resort instead of the only one.
    """
    import cv2
    from PIL import ImageGrab

    _dpi_aware()
    if area is None:
        area = game_area()
    if area is None:
        shot = ImageGrab.grab()
        area = (0, 0, shot.width, shot.height)
        frame = np.asarray(shot.convert("RGB"))[:, :, ::-1]
        x, y, w, h = sixteen_nine(area)
        frame = frame[y:y + h, x:x + w]
    else:
        x, y, w, h = sixteen_nine(area)
        shot = ImageGrab.grab(bbox=(x, y, x + w, y + h), all_screens=True)
        frame = np.asarray(shot.convert("RGB"))[:, :, ::-1]
    if frame.shape[1] != REFERENCE[0] or frame.shape[0] != REFERENCE[1]:
        # INTER_AREA beim Verkleinern mittelt ueber die Quellpunkte, CUBIC beim
        # Vergroessern -- die Tabellenschrift ueberlebt beides.
        shrink = frame.shape[1] > REFERENCE[0]
        frame = cv2.resize(frame, REFERENCE,
                           interpolation=cv2.INTER_AREA if shrink else cv2.INTER_CUBIC)
    return np.ascontiguousarray(frame)


def read_image(path: Path) -> np.ndarray:
    import cv2

    image = cv2.imread(str(path))
    if image is None:
        raise SystemExit(f"could not read {path}")
    return image


def thumb(frame: np.ndarray, width: int = 160) -> np.ndarray:
    """A tiny grey copy, only ever compared against the previous one."""
    import cv2

    height = max(1, int(frame.shape[0] * width / max(1, frame.shape[1])))
    small = cv2.resize(frame, (width, height), interpolation=cv2.INTER_AREA)
    return cv2.cvtColor(small, cv2.COLOR_BGR2GRAY).astype(np.float32)


# --------------------------------------------------------------------------- #
# the reader
# --------------------------------------------------------------------------- #

class ScreenReader:
    """OCR one frame and say which routes and class it offers.

    The engine is built lazily, so importing this module costs nothing and the
    overlay's window is up before the first model load.
    """

    def __init__(self, advisor, config: dict[str, Any] | None = None) -> None:
        self.adv = advisor
        self.cfg = config or load_config()
        self._engine = None
        self._class_names = set(advisor.class_names) | {"X"}

    # ---------------- OCR ---------------- #

    @property
    def engine(self):
        if self._engine is None:
            import sys

            sys.path.insert(0, str(Path(__file__).resolve().parent))
            from extract_leaderboard import create_rapidocr_engine

            self._engine = create_rapidocr_engine(use_gpu=bool(self.cfg["ocr_gpu"]))
        return self._engine

    def ocr(self, frame: np.ndarray) -> list[Line]:
        scale = 1.0
        limit = int(self.cfg.get("max_ocr_width") or 0)
        if limit and frame.shape[1] > limit:
            import cv2

            scale = limit / frame.shape[1]
            frame = cv2.resize(frame, (limit, max(1, int(frame.shape[0] * scale))),
                               interpolation=cv2.INTER_AREA)
        result = self.engine(frame)
        # `boxes` comes back as a numpy array, so these have to be None-checked
        # rather than truth-tested.
        def as_list(name: str) -> list:
            value = getattr(result, name, None)
            return [] if value is None else list(value)

        texts = as_list("txts")
        boxes = as_list("boxes")
        scores = as_list("scores")
        lines: list[Line] = []
        for index, text in enumerate(texts):
            clean = str(text).strip()
            if not clean:
                continue
            x = y = 0.0
            if index < len(boxes):
                points = boxes[index]
                points = points.tolist() if hasattr(points, "tolist") else points
                x = min(float(p[0]) for p in points)
                y = min(float(p[1]) for p in points)
            score = float(scores[index]) if index < len(scores) else 1.0
            # Report screen coordinates, whatever size the OCR ran at.
            lines.append(Line(text=clean, x=x / scale, y=y / scale, score=score))
        return lines

    # ---------------- interpretation ---------------- #

    # ---------------- masking ---------------- #

    def crop(self, frame: np.ndarray,
             fractions: Sequence[float] | None) -> tuple[np.ndarray, float, float]:
        """One region of the frame, given as fractions, plus its screen offset.

        Fractions and not pixels: the same setting then fits 1080p, 1440p and 4K
        without a second table, and only a change of ASPECT ratio needs a new
        measurement.
        """
        if not fractions or len(fractions) != 4:
            return frame, 0.0, 0.0
        height, width = frame.shape[:2]
        left, top, right, bottom = fractions
        x0 = max(0, min(width - 2, int(round(left * width))))
        x1 = max(x0 + 1, min(width, int(round(right * width))))
        y0 = max(0, min(height - 2, int(round(top * height))))
        y1 = max(y0 + 1, min(height, int(round(bottom * height))))
        return frame[y0:y1, x0:x1].copy(), float(x0), float(y0)

    def _read_region(self, frame: np.ndarray,
                     key: str) -> list[Line]:
        """OCR one masked region, with the lines put back in screen coordinates."""
        sub, dx, dy = self.crop(frame, self.cfg.get(key))
        return [Line(text=line.text, x=line.x + dx, y=line.y + dy,
                     score=line.score) for line in self.ocr(sub)]

    def interpret(self, lines: Iterable[Line],
                  class_lines: Iterable[Line] | None = None) -> ScreenState:
        """Routes from one set of lines, the class from another.

        Two sets and not one, because the restriction badge and the featured
        car's own class look identical as text. Keeping them apart is what stops
        a Spec Racing card's "C 484" from being read as the class cap.
        """
        state = ScreenState(lines=list(lines))
        class_pool = list(class_lines) if class_lines is not None else state.lines
        hits: list[tuple[float, str, float]] = []   # (y, track, score)
        seen: dict[str, float] = {}
        for line in state.lines:
            match = self.adv.match_track(line.text,
                                         cutoff=float(self.cfg["track_cutoff"]))
            if match is None:
                continue
            name, score = match
            # The same route can be read twice (card title and a subtitle); keep
            # the better read, and the position of that better read.
            if name in seen and seen[name] >= score:
                continue
            seen[name] = score
            hits = [h for h in hits if h[1] != name]
            hits.append((line.y, name, score))

        hits.sort(key=lambda h: (-h[2], h[0]))
        keep = hits[: int(self.cfg["max_tracks"])]
        # Report them in reading order, which is the order the cards are offered.
        keep.sort(key=lambda h: h[0])
        state.tracks = [name for _y, name, _s in keep]
        state.track_scores = {name: score for _y, name, score in keep}

        # Die Statusspalte einer Reihe: dieselbe Zeile wie der Streckenname, als
        # eigene Zeile der Texterkennung oder an den Namen angehaengt.
        for line in state.lines:
            status = status_in(line.text)
            if status is None or not keep:
                continue
            y, name, _s = min(keep, key=lambda h: abs(h[0] - line.y))
            if abs(y - line.y) <= STATUS_NEAR_Y:
                state.track_status[name] = status
        for line in state.lines:
            m = SERIES_PATTERN.search(line.text)
            if not m:
                continue
            k, n = int(m.group("k")), int(m.group("n"))
            if not 1 <= k <= n:
                continue
            state.series, state.series_index, state.series_count = m.group("name").strip(), k, n
            break

        # The PI fallback only runs once the routes say we are on an offer
        # screen. Without that guard a desktop reads "You've played for 538
        # hours" as class C, which is exactly the kind of confident nonsense an
        # overlay must not draw.
        # A masked class region is already confirmation enough; a whole-frame read
        # still has to earn it with routes.
        masked = class_lines is not None
        enough = masked or len(state.tracks) >= int(self.cfg["min_tracks"])
        state.klass, state.klass_source = self._find_class(class_pool,
                                                           confirmed=enough)
        state.spec = any(SPEC_PATTERN.search(line.text) for line in state.lines)
        if self.cfg.get("region_car"):
            state.car, state.car_name = self._find_car(state.lines,
                                                       set(state.tracks))
        state.lines = state.lines + [l for l in class_pool if l not in state.lines]
        return state

    def _find_class(self, lines: Sequence[Line],
                    confirmed: bool = True) -> tuple[str | None, str]:
        for pattern in CLASS_PATTERNS:
            for line in lines:
                match = pattern.search(line.text)
                if not match:
                    continue
                token = match.group(1).upper().replace(" ", "")
                if token in self._class_names:
                    return token, f"token {line.text!r}"
        if not confirmed:
            return None, ""
        # The lone badge. Strongest remaining signal, and unambiguous once the
        # routes have confirmed which screen this is.
        for line in lines:
            token = line.text.strip().upper().replace(" ", "")
            if LONE_CLASS.match(line.text.strip()) and token in self._class_names:
                return token, f"badge {line.text.strip()!r}"
        # Last resort: a three-digit number is very likely the PI cap. A guess,
        # and it says so, so the overlay can mark it.
        for line in lines:
            for raw in PI_PATTERN.findall(line.text):
                pi = int(raw)
                if not 100 <= pi <= 999:
                    continue
                for cap, name in PI_BANDS:
                    if pi <= cap:
                        if name in self._class_names:
                            return name, f"PI {pi} in {line.text!r}"
                        break
        return None, ""

    def _find_car(self, lines: Sequence[Line],
                  tracks: set[str]) -> tuple[int | None, str | None]:
        """The car name, only when it is unmistakable.

        Car names are the one vocabulary that overlaps everything else on a
        Forza screen -- makes appear in event titles, models in liveries. So the
        cutoff is high and a line already read as a route never counts.
        """
        best: tuple[int, str, float] | None = None
        cutoff = float(self.cfg["car_cutoff"])
        for line in lines:
            if len(line.text) < 6:
                continue
            if self.adv.match_track(line.text, cutoff=0.8):
                continue
            index = self.adv.match_car_name(line.text, cutoff=cutoff)
            if index is None:
                continue
            score = line.score
            if best is None or score > best[2]:
                best = (index, self.adv.car_names[index], score)
        if best is None:
            return None, None
        return best[0], best[1]

    def read(self, frame: np.ndarray, full: bool = False) -> ScreenState:
        """Read one frame: two small masked regions, or the whole thing.

        The masked path is the fast one as well as the correct one -- two crops
        of a few per cent of the frame instead of every pixel of a 4K screen.
        """
        started = time.perf_counter()
        masked = not full and bool(self.cfg.get("region_routes"))             and bool(self.cfg.get("region_class"))
        if masked:
            state = self.interpret(self._read_region(frame, "region_routes"),
                                   self._read_region(frame, "region_class"))
            if not state.is_offer and (full or self.cfg.get("full_frame_fallback")):
                state = self.interpret(self.ocr(frame))
        else:
            state = self.interpret(self.ocr(frame))
        state.read_seconds = time.perf_counter() - started
        return state


# --------------------------------------------------------------------------- #
# a watcher that only pays for OCR when the screen actually changed
# --------------------------------------------------------------------------- #

class ScreenWatcher:
    """Poll the screen cheaply; OCR only when the pixels moved.

    A menu is static, so the expensive step fires on transitions instead of on a
    timer. That is what keeps a 1-2 second poll from taking CPU away from the
    game: the comparison is a 160-pixel-wide grey thumbnail.
    """

    def __init__(self, reader: ScreenReader, config: dict[str, Any] | None = None) -> None:
        self.reader = reader
        self.cfg = config or reader.cfg
        self._last_thumb: np.ndarray | None = None
        self._last_ocr = 0.0
        self.state: ScreenState | None = None
        self.frames = 0
        self.reads = 0

    def poll(self, force: bool = False) -> ScreenState | None:
        """One cheap cycle. Returns a state only when OCR actually ran."""
        region = self.cfg.get("region")
        frame = grab_screen(region)
        self.frames += 1
        small = thumb(frame)
        moved = self._last_thumb is None or \
            float(np.abs(small - self._last_thumb).mean()) >= float(self.cfg["change_threshold"])
        self._last_thumb = small
        now = time.monotonic()
        if not force:
            if not moved:
                return None
            if now - self._last_ocr < float(self.cfg["min_ocr_seconds"]):
                return None
        self._last_ocr = now
        self.reads += 1
        self.state = self.reader.read(frame)
        return self.state


# --------------------------------------------------------------------------- #
# CLI: calibration and a live watch
# --------------------------------------------------------------------------- #

def draw_boxes(frame: np.ndarray, cfg: dict[str, Any], out: Path) -> None:
    """Save the frame with both mask regions outlined, for checking by eye."""
    import cv2

    canvas = frame.copy()
    height, width = canvas.shape[:2]
    for key, colour, label in (("region_routes", (80, 220, 120), "routes"),
                               ("region_class", (60, 170, 250), "class")):
        box = cfg.get(key)
        if not box or len(box) != 4:
            continue
        x0, y0 = int(box[0] * width), int(box[1] * height)
        x1, y1 = int(box[2] * width), int(box[3] * height)
        cv2.rectangle(canvas, (x0, y0), (x1, y1), colour, 3)
        cv2.putText(canvas, label, (x0 + 6, max(20, y0 - 8)),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.8, colour, 2)
    out.parent.mkdir(parents=True, exist_ok=True)
    cv2.imwrite(str(out), canvas)


def describe(state: ScreenState, verbose: bool) -> None:
    print(f"  read in {state.read_seconds * 1000:.0f} ms, {len(state.lines)} lines")
    if state.tracks:
        parts = [f"{name} ({state.track_scores.get(name, 0):.2f})"
                 for name in state.tracks]
        print(f"  routes: {', '.join(parts)}")
    else:
        print("  routes: none matched")
    print(f"  class : {state.klass or 'none'}"
          f"{'  <- ' + state.klass_source if state.klass_source else ''}")
    if state.spec:
        print("  spec  : one-make event -- the car is fixed, a ranking is moot")
    if state.car_name:
        print(f"  car   : {state.car_name}")
    print(f"  usable as an offer: {'yes' if state.is_offer else 'no'}")
    if verbose:
        print("  every line read:")
        for line in sorted(state.lines, key=lambda l: (l.y, l.x)):
            print(f"    y{line.y:7.1f} x{line.x:7.1f} {line.score:.2f}  {line.text}")


def main() -> int:
    import sys

    sys.path.insert(0, str(Path(__file__).resolve().parent))
    from rivals_advisor import Advisor

    parser = argparse.ArgumentParser(description="Read routes and class off the screen.")
    parser.add_argument("--image", type=Path, help="read a saved frame instead of the screen")
    parser.add_argument("--shot", action="store_true", help="grab the screen once")
    parser.add_argument("--watch", action="store_true", help="keep watching, print changes")
    parser.add_argument("--config", type=Path, default=CONFIG_PATH)
    parser.add_argument("--dataset", type=Path, default=None)
    parser.add_argument("--verbose", "-v", action="store_true",
                        help="print every line the OCR returned")
    parser.add_argument("--full", action="store_true",
                        help="OCR the whole frame instead of the two masked regions")
    parser.add_argument("--boxes", type=Path, metavar="OUT.PNG",
                        help="draw the two mask regions on the frame and save it, "
                             "to check the fractions against a real capture")
    args = parser.parse_args()

    cfg = load_config(args.config)
    adv = Advisor.load(args.dataset) if args.dataset else Advisor.load()
    reader = ScreenReader(adv, cfg)

    frame = None
    if args.image:
        frame = read_image(args.image)
    elif args.boxes or args.shot or not args.watch:
        frame = grab_screen(cfg.get("region"))

    if args.boxes is not None and frame is not None:
        draw_boxes(frame, cfg, args.boxes)
        print(f"wrote {args.boxes} -- the routes box must hold the 01/02/03 list, "
              f"the class box only the badge in the card's corner")
    if frame is not None:
        if args.image:
            print(f"{args.image}")
        describe(reader.read(frame, full=args.full), args.verbose or bool(args.image))
        return 0

    watcher = ScreenWatcher(reader, cfg)
    print(f"watching every {cfg['poll_seconds']}s; Ctrl+C stops")
    last: tuple | None = None
    try:
        while True:
            state = watcher.poll()
            if state is not None and state.key() != last:
                last = state.key()
                print(f"\n[{time.strftime('%H:%M:%S')}] frame {watcher.frames}, "
                      f"OCR {watcher.reads}")
                describe(state, args.verbose)
            time.sleep(float(cfg["poll_seconds"]))
    except KeyboardInterrupt:
        print("\nstopped")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
