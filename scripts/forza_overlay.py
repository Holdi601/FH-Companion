"""A play-time overlay: what to drive on the offered routes, and where your car stands.

SUPERSEDED at play time by the Rivals overlay tab in the haptics app
(`haptics/ForzaHaptics.Tester`, see `docs/play_overlay.md`). Forza sends telemetry
to ONE endpoint and a UDP port takes ONE listener, so this and the haptics tool
cannot both have port 5300 -- measured, WinError 10013 one way and 10048 the other.
Living in the same process is what makes the haptics and this panel work at once.

This stays as the reference implementation: it shares `rivals_advisor.py` and
`screen_reader.py` with the test suites, which are what pin the C# port to the
website's scoring. To run it anyway alongside the haptics tool, set
`telemetry_relay_port` and point the haptics port box at that port.

Two panels, drawn on top of the game and never taking a click. They answer
different questions, so they never appear together: each has its own button, and
showing one takes the other away.

  RIGHT  The Horizon Play screen offers three routes and a class. This reads them
         off the screen (`screen_reader.py`) and lists the cars in the order the
         website would over exactly those routes -- by points OR by time sum,
         whichever `score_mode` says, never both at once.
  LEFT   Which car you are in, from Forza's own telemetry stream, and its place
         in every category and class it has laps in. That is the answer to "which
         class should I build this car for".

Both come from `data/analytics/laps.json`, the same file the website is built
from, scored by `rivals_advisor.py` -- which is checked against the page's own
JavaScript by `scripts/test_rivals_advisor.py`, so the overlay can never quietly
disagree with the site.

    python scripts/forza_overlay.py
    python scripts/forza_overlay.py --score time --show-seconds 20
    python scripts/forza_overlay.py --demo "Soni Circuit,The Goliath,Ito Sprint" --class A

Turn on Forza's data-out first (Settings -> HUD and Gameplay -> Data Out: On,
IP 127.0.0.1, port 5300) or the left panel has nothing to read.

Nothing is on screen until a button asks for it, and it takes itself away again
after `show_seconds` -- that holds when the read FAILED too, so a "could not read
the routes" message is never left standing. Defaults:

    F8  / gamepad View(Back)    read the screen, show WHAT TO DRIVE
    F6  / gamepad left-stick    show YOUR CAR
    F7                          switch between the points and time-sum order
    F9                          pin the panel that is up until pressed again
    F10                         quit

Every key and pad button is a setting in `config/overlay.json`.

The game must run BORDERLESS windowed. An exclusive-fullscreen swap chain draws
over every other window, so no overlay of any kind can appear on top of it.
"""

from __future__ import annotations

import argparse
import ctypes
import json
import queue
import socket
import struct
import sys
import threading
import time
import tkinter as tk
import tkinter.font as tkfont
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

sys.path.insert(0, str(Path(__file__).resolve().parent))

import rivals_advisor as ra
from rivals_advisor import Advisor, sum_text
from screen_reader import (CONFIG_PATH, PI_BANDS, ScreenReader, ScreenState,
                           ScreenWatcher, load_config)

WORKSPACE = Path(__file__).resolve().parent.parent
ORDINAL_PATH = WORKSPACE / "config/fh6_car_ordinals.json"

# The page's dark palette, so the overlay reads as the same tool as the site.
INK = "#e7ecf3"
INK_SOFT = "#c3ccd9"
MUTED = "#93a2b5"
GROUND = "#0b1017"
SURFACE = "#131a23"
BAR = "#22b8e6"
WARN = "#f0a22e"
GOOD = "#7ee787"

# Forza's telemetry layout, from haptics/ForzaHaptics.Tester/ForzaPacket.cs so the
# two readers of this stream cannot drift apart.
TELEMETRY_MIN_LENGTH = 232
FIELD_OFFSETS = {
    "IsRaceOn": 0, "CarOrdinal": 212, "CarClass": 216,
    "CarPerformanceIndex": 220, "DrivetrainType": 224, "NumCylinders": 228,
}
DRIVETRAIN = {0: "FWD", 1: "RWD", 2: "AWD"}


# --------------------------------------------------------------------------- #
# the car the player is in
# --------------------------------------------------------------------------- #

@dataclass(slots=True)
class CarState:
    ordinal: int | None = None
    pi: int | None = None
    klass_code: int | None = None
    drivetrain: int | None = None
    cylinders: int | None = None
    race_on: bool = False
    when: float = 0.0

    @property
    def fresh(self) -> bool:
        return self.when > 0 and (time.monotonic() - self.when) < 15.0

    @property
    def pi_class(self) -> str | None:
        """The class the car sits in RIGHT NOW, from its current PI.

        Deliberately derived from the PI and not from `CarClass`: the enum's
        order is not documented for FH6's R class, and the PI is the number the
        game itself puts a cap on.
        """
        if self.pi is None:
            return None
        for cap, name in PI_BANDS:
            if self.pi <= cap:
                return name
        return None


class Telemetry(threading.Thread):
    """Forza's data-out stream, read the way the haptics tool reads it."""

    def __init__(self, port: int, relay_port: int | None = None) -> None:
        super().__init__(name="telemetry", daemon=True)
        self.port = port
        self.relay_port = relay_port
        self.state = CarState()
        self.packets = 0
        self.error: str | None = None
        self._stop = threading.Event()

    def stop(self) -> None:
        self._stop.set()

    def run(self) -> None:
        try:
            sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            sock.bind(("0.0.0.0", self.port))
            sock.settimeout(0.5)
        except OSError as exc:
            busy = getattr(exc, "winerror", None) in (10013, 10048)
            self.error = (f"port {self.port} is already taken — the haptics tool "
                          f"is probably on it") if busy else f"port {self.port}: {exc}"
            return
        relay = None
        if self.relay_port:
            relay = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        while not self._stop.is_set():
            try:
                data, _addr = sock.recvfrom(2048)
            except socket.timeout:
                continue
            except OSError as exc:
                self.error = str(exc)
                return
            if relay is not None:
                # Pass it on untouched and first, so the haptics tool sees the
                # stream at the same rate whatever this reader does with it.
                try:
                    relay.sendto(data, ("127.0.0.1", int(self.relay_port)))
                except OSError:
                    pass
            if len(data) < TELEMETRY_MIN_LENGTH:
                continue
            self.packets += 1
            try:
                values = {key: struct.unpack_from("<i", data, offset)[0]
                          for key, offset in FIELD_OFFSETS.items()}
            except struct.error:
                continue
            self.state = CarState(
                ordinal=values["CarOrdinal"], pi=values["CarPerformanceIndex"],
                klass_code=values["CarClass"],
                drivetrain=values["DrivetrainType"],
                cylinders=values["NumCylinders"],
                race_on=bool(values["IsRaceOn"]), when=time.monotonic())


class OrdinalMap:
    """Learns which car ordinal is which car, from the screen.

    Telemetry gives a car ORDINAL. The dataset knows cars by the leaderboard's own
    `car_id`. There is good reason to think those are the SAME number -- the
    codename catalogue is "a contiguous array indexed by carId"
    (`dump_car_catalogue.py`), and an index into the game's car array is exactly
    what an ordinal is -- but it is not proved: on the 46 ids where the codename
    catalogue and the lap-time-joined names both exist they disagree 40 times, so
    one of those two mappings is wrong and it is not yet known which.

    So the overlay treats the direct match as an ASSUMPTION and labels it, while
    a match learned from the screen -- the ordinal on the wire while the car's
    name was drawn -- counts as evidence and wins. Learned pairs are written down,
    so the panel keeps working in a race, where no name is on screen.
    """

    def __init__(self, path: Path = ORDINAL_PATH) -> None:
        self.path = path
        self.by_ordinal: dict[str, dict] = {}
        if path.exists():
            try:
                data = json.loads(path.read_text(encoding="utf-8-sig"))
                self.by_ordinal = data.get("by_ordinal", {})
            except (ValueError, OSError):
                pass
        self.learned_this_run = 0

    def lookup(self, ordinal: int | None) -> dict | None:
        if ordinal is None:
            return None
        return self.by_ordinal.get(str(ordinal))

    def learn(self, ordinal: int, car_index: int, name: str,
              pi: int | None) -> bool:
        key = str(ordinal)
        known = self.by_ordinal.get(key)
        if known and known.get("car_index") == car_index:
            return False
        self.by_ordinal[key] = {
            "car_index": car_index, "name": name, "pi": pi,
            "learned": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        }
        self.learned_this_run += 1
        self.save()
        return True

    def save(self) -> None:
        payload = {
            "source": "ordinal seen on the telemetry wire while the car name was "
                      "on the screen; see scripts/forza_overlay.py",
            "by_ordinal": self.by_ordinal,
        }
        try:
            self.path.parent.mkdir(parents=True, exist_ok=True)
            self.path.write_text(json.dumps(payload, indent=1, ensure_ascii=False),
                                 encoding="utf-8")
        except OSError as exc:
            print(f"[overlay] could not save {self.path}: {exc}")


# --------------------------------------------------------------------------- #
# triggers: keys and gamepad buttons, each mapped to one event
# --------------------------------------------------------------------------- #

XINPUT_BUTTONS = {
    "DPAD_UP": 0x0001, "DPAD_DOWN": 0x0002, "DPAD_LEFT": 0x0004,
    "DPAD_RIGHT": 0x0008, "START": 0x0010, "MENU": 0x0010,
    "BACK": 0x0020, "VIEW": 0x0020,
    "LEFT_THUMB": 0x0040, "RIGHT_THUMB": 0x0080, "LB": 0x0100, "RB": 0x0200,
    "A": 0x1000, "B": 0x2000, "X": 0x4000, "Y": 0x8000,
}


class _XInputState(ctypes.Structure):
    _fields_ = [("dwPacketNumber", ctypes.c_uint32),
                ("wButtons", ctypes.c_uint16),
                ("bLeftTrigger", ctypes.c_uint8),
                ("bRightTrigger", ctypes.c_uint8),
                ("sThumbLX", ctypes.c_int16), ("sThumbLY", ctypes.c_int16),
                ("sThumbRX", ctypes.c_int16), ("sThumbRY", ctypes.c_int16)]


def button_mask(names: Any) -> int:
    """Config names to an XInput mask, matched as ANY of them.

    A chord would be the safer binding in a game that uses every button, but it
    cannot be written on one config line without inventing a syntax -- so this is
    an any-of, and the defaults pick buttons the Horizon menus leave alone.
    """
    mask = 0
    if isinstance(names, str):
        names = [names]
    for name in names or []:
        mask |= XINPUT_BUTTONS.get(str(name).upper().strip(), 0)
    return mask


class Triggers(threading.Thread):
    """Watch keys and gamepad buttons; push one event name per fresh press.

    Polled rather than hooked: a keyboard hook needs a message loop and a gamepad
    hook needs a driver. Polling `GetAsyncKeyState` and `XInputGetState` at 60 ms
    costs nothing measurable and works while the GAME has focus, which a Tk key
    binding does not.
    """

    def __init__(self, events: queue.Queue, keys: dict[int, str],
                 pads: dict[int, str]) -> None:
        super().__init__(name="triggers", daemon=True)
        self.events = events
        self.keys = {vk: name for vk, name in keys.items() if vk}
        self.pads = {mask: name for mask, name in pads.items() if mask}
        self._stop = threading.Event()
        self._user32 = ctypes.windll.user32
        self._xinput = None
        for dll in ("xinput1_4", "xinput1_3", "xinput9_1_0"):
            try:
                self._xinput = ctypes.windll.LoadLibrary(dll)
                break
            except OSError:
                continue

    def stop(self) -> None:
        self._stop.set()

    def run(self) -> None:
        keys_down: set[int] = set()
        pads_down: set[int] = set()
        state = _XInputState()
        while not self._stop.is_set():
            for vk, event in self.keys.items():
                if self._user32.GetAsyncKeyState(vk) & 0x8000:
                    if vk not in keys_down:
                        keys_down.add(vk)
                        self.events.put(event)
                else:
                    keys_down.discard(vk)
            if self._xinput is not None and self.pads:
                buttons = 0
                for pad in range(4):
                    if self._xinput.XInputGetState(pad, ctypes.byref(state)) == 0:
                        buttons = state.wButtons
                        break
                for mask, event in self.pads.items():
                    if buttons & mask:
                        if mask not in pads_down:
                            pads_down.add(mask)
                            self.events.put(event)
                    else:
                        pads_down.discard(mask)
            time.sleep(0.06)


# --------------------------------------------------------------------------- #
# the windows
# --------------------------------------------------------------------------- #

GWL_EXSTYLE = -20
WS_EX_LAYERED = 0x00080000
WS_EX_TRANSPARENT = 0x00000020
WS_EX_NOACTIVATE = 0x08000000
WS_EX_TOOLWINDOW = 0x00000080


def make_click_through(window: tk.Misc) -> None:
    """Let every click fall through to the game underneath."""
    window.update_idletasks()
    try:
        user32 = ctypes.windll.user32
        hwnd = user32.GetParent(window.winfo_id()) or window.winfo_id()
        style = user32.GetWindowLongW(hwnd, GWL_EXSTYLE)
        user32.SetWindowLongW(hwnd, GWL_EXSTYLE, style | WS_EX_LAYERED
                              | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE
                              | WS_EX_TOOLWINDOW)
    except Exception as exc:                                  # pragma: no cover
        print(f"[overlay] click-through not applied: {exc}")


@dataclass
class Panel:
    """One side of the screen: a head, a list of rows, and a footnote."""
    window: tk.Toplevel
    title: tk.Label
    subtitle: tk.Label
    body: tk.Frame
    note: tk.Label
    until: float = 0.0
    rows: list[tk.Widget] = field(default_factory=list)

    def clear(self) -> None:
        for widget in self.rows:
            widget.destroy()
        self.rows.clear()
        self.note.configure(text="")


class OverlayApp:
    def __init__(self, cfg: dict[str, Any], advisor: Advisor,
                 args: argparse.Namespace) -> None:
        self.cfg = cfg
        self.adv = advisor
        self.args = args
        self.events: queue.Queue = queue.Queue()
        self.ordinals = OrdinalMap()
        self.screen: ScreenState | None = None
        self.last_advice_key: tuple | None = None
        self.last_car_key: tuple | None = None
        self.pinned: str | None = None
        self.score_mode = str(cfg.get("score_mode", "points")).lower()
        if self.score_mode not in ("points", "time"):
            self.score_mode = "points"

        self.root = tk.Tk()
        self.root.withdraw()
        self.width = self.root.winfo_screenwidth()
        self.height = self.root.winfo_screenheight()
        # Scale every font off the screen, so a 4K panel is not a strip of ants.
        self.unit = max(9, int(self.height / 108))
        self.f_title = tkfont.Font(family="Segoe UI Semibold", size=self.unit + 4)
        self.f_sub = tkfont.Font(family="Segoe UI", size=self.unit - 2)
        self.f_row = tkfont.Font(family="Segoe UI", size=self.unit - 1)
        self.f_num = tkfont.Font(family="Consolas", size=self.unit - 1)
        self.f_note = tkfont.Font(family="Segoe UI", size=self.unit - 3)

        side = int(self.width * float(cfg["side_width_fraction"]))
        self.panels: dict[str, Panel] = {}
        if not args.left_only:
            self.panels["right"] = self._panel(self.width - side, side)
            self._set_head(self.panels["right"], "What to drive",
                           "waiting for the route screen")
        if not args.right_only:
            self.panels["left"] = self._panel(0, int(side * 0.86))
            self._set_head(self.panels["left"], "Your car",
                           "waiting for Forza's data out")
        for panel in self.panels.values():
            panel.window.withdraw()

        self.reader = ScreenReader(advisor, cfg)
        self.watcher = ScreenWatcher(self.reader, cfg)
        relay = cfg.get("telemetry_relay_port")
        self.telemetry = Telemetry(int(cfg["telemetry_port"]),
                                   int(relay) if relay else None)
        self.telemetry.start()
        self.triggers = Triggers(self.events, self._key_map(), self._pad_map())
        self.triggers.start()

        self._ocr_busy = threading.Event()
        self._ocr_result: queue.Queue = queue.Queue()
        self._forced = False
        # The withdraw above and any deiconify have to be separated by an event
        # cycle: queued together before `mainloop`, Tk collapses them and the
        # window never maps -- which looked exactly like a panel that refuses to
        # draw. Every real trigger arrives during the loop and is fine; the demo
        # is the one path that would have hit it.
        self.root.update_idletasks()
        if args.demo:
            self.root.after(300, lambda: self._show_demo(args.demo, args.klass))
        self.root.after(200, self._tick)

    # ---------------- bindings ---------------- #

    def _key_map(self) -> dict[int, str]:
        cfg = self.cfg
        # `hotkey_vk` was the single trigger before the panels were split; it
        # still means "show what to drive", so an older config keeps working.
        right = int(cfg.get("hotkey_right_vk") or cfg.get("hotkey_vk") or 119)
        return {right: "right",
                int(cfg.get("hotkey_left_vk") or 117): "left",
                int(cfg.get("hotkey_score_vk") or 118): "score",
                int(cfg.get("hotkey_pin_vk") or 120): "pin",
                int(cfg.get("hotkey_quit_vk") or 121): "quit"}

    def _pad_map(self) -> dict[int, str]:
        cfg = self.cfg
        right = button_mask(cfg.get("gamepad_right")
                            or cfg.get("gamepad_buttons") or ["BACK"])
        left = button_mask(cfg.get("gamepad_left") or ["LEFT_THUMB"])
        pads: dict[int, str] = {}
        if right:
            pads[right] = "right"
        if left and left != right:
            pads[left] = "left"
        return pads

    # ---------------- window construction ---------------- #

    def _panel(self, x: int, width: int) -> Panel:
        win = tk.Toplevel(self.root)
        win.overrideredirect(True)
        win.geometry(f"{width}x{self.height}+{x}+0")
        win.configure(bg=GROUND)
        win.attributes("-topmost", True)
        win.attributes("-alpha", float(self.cfg["opacity"]))
        # Without this, pack propagation resizes the toplevel to fit its rows: a
        # long list grew the window past the bottom of the screen, and a short
        # one shrank the panel to a stub.
        win.pack_propagate(False)
        pad = max(8, self.unit)
        head = tk.Frame(win, bg=SURFACE)
        head.pack(fill="x")
        title = tk.Label(head, text="", bg=SURFACE, fg=INK, font=self.f_title,
                         anchor="w", justify="left", wraplength=width - 2 * pad)
        title.pack(fill="x", padx=pad, pady=(pad, 0))
        subtitle = tk.Label(head, text="", bg=SURFACE, fg=MUTED, font=self.f_sub,
                            anchor="w", justify="left", wraplength=width - 2 * pad)
        subtitle.pack(fill="x", padx=pad, pady=(2, pad))
        note = tk.Label(win, text="", bg=GROUND, fg=MUTED, font=self.f_note,
                        anchor="w", justify="left", wraplength=width - 2 * pad)
        # Claimed from the bottom BEFORE the body, so a long list can never take
        # the footnote's space and push it off the screen.
        note.pack(side="bottom", fill="x", padx=pad, pady=(0, pad // 2))
        body = tk.Frame(win, bg=GROUND)
        body.pack(fill="both", expand=True, padx=pad // 2, pady=pad // 2)
        body.pack_propagate(False)
        make_click_through(win)
        return Panel(window=win, title=title, subtitle=subtitle, body=body,
                     note=note)

    def _set_head(self, panel: Panel, title: str, subtitle: str) -> None:
        panel.title.configure(text=title)
        panel.subtitle.configure(text=subtitle)

    def _row(self, panel: Panel, left: str, right: str, *, tone: str = INK,
             right_tone: str | None = None, small: bool = False,
             indent: int = 0) -> None:
        line = tk.Frame(panel.body, bg=GROUND)
        line.pack(fill="x", pady=1)
        font = self.f_note if small else self.f_row
        tk.Label(line, text=left, bg=GROUND, fg=tone, font=font, anchor="w",
                 justify="left").pack(side="left", padx=(indent, 6))
        tk.Label(line, text=right, bg=GROUND, fg=right_tone or MUTED,
                 font=self.f_note if small else self.f_num,
                 anchor="e").pack(side="right")
        panel.rows.append(line)

    def _heading(self, panel: Panel, text: str) -> None:
        label = tk.Label(panel.body, text=text.upper(), bg=GROUND, fg=BAR,
                         font=self.f_note, anchor="w")
        label.pack(fill="x", pady=(max(6, self.unit // 2), 2))
        panel.rows.append(label)

    def _row_budget(self) -> int:
        """How many rows fit in a body, once head and footnote are paid for."""
        line = self.f_row.metrics("linespace") + 8
        head = (self.f_title.metrics("linespace") + self.f_sub.metrics("linespace")
                + 3 * self.unit)
        note = self.f_note.metrics("linespace") * 3 + self.unit
        return max(6, int((self.height - head - note) / max(1, line)) - 1)

    # ---------------- showing and hiding ---------------- #

    def _show(self, side: str, seconds: float | None = None) -> None:
        """Put one side up. The other goes away: they are separate questions."""
        panel = self.panels.get(side)
        if panel is None:
            return
        if seconds is None:
            seconds = float(self.cfg["show_seconds"])
        panel.until = time.monotonic() + max(1.0, seconds)
        panel.window.deiconify()
        panel.window.attributes("-topmost", True)
        for name in list(self.panels):
            if name != side and self.panels[name].until:
                self._hide(name)

    def _hide(self, side: str) -> None:
        panel = self.panels.get(side)
        if panel is None:
            return
        panel.until = 0.0
        panel.window.withdraw()
        if self.pinned == side:
            self.pinned = None

    def _visible(self, side: str) -> bool:
        panel = self.panels.get(side)
        return bool(panel and panel.until)

    # ---------------- the right panel: what to drive ---------------- #

    def _show_demo(self, tracks: str, klass: str) -> None:
        names = []
        for raw in tracks.split(","):
            hit = self.adv.match_track(raw.strip())
            if hit:
                names.append(hit[0])
        state = ScreenState(tracks=names, klass=klass,
                            track_scores={n: 1.0 for n in names},
                            klass_source="demo")
        self.screen = state
        self._render_advice(state)
        self._show("right")

    def _render_advice(self, state: ScreenState) -> None:
        panel = self.panels.get("right")
        if panel is None:
            return
        panel.clear()
        klass = state.klass or ""
        # No limit on the query: the places are the full field either way.
        advice = self.adv.advise(state.tracks, klass)
        budget = self._row_budget()
        self._set_head(panel, f"Class {klass} – what to drive",
                       " · ".join(advice.tracks) or "no route recognised")

        if not advice.by_points:
            self._row(panel, "No board for these routes in this class yet.", "",
                      tone=WARN)
            panel.note.configure(text="The sweep has not reached them. The site's "
                                      "Scan status tab lists what exists.")
            return

        # ONE list, not two. Points and the time sum answer different questions
        # -- points reward beating the field on every route, the time sum rewards
        # raw total pace -- and a glance mid-menu can only hold one order. Which
        # one is a setting (`score_mode`, or F7 while it runs).
        if state.spec:
            # A one-make event: every entrant drives the same car, so ranking 400
            # of them is not advice. Say it once, at the top, and still list the
            # order underneath -- the same three routes come round again in
            # events that are not spec.
            self._row(panel, "One-make event — the car is fixed.", "", tone=WARN)
            self._row(panel, "The order below is for these routes in general.",
                      "", tone=INK_SOFT, small=True)

        by_points = self.score_mode == "points"
        rows = (advice.by_points if by_points else advice.by_time)[:budget - 1]
        self._heading(panel, ("by points" if by_points else "by time sum")
                      + f" · {len(advice.tracks)} route(s)")
        for row in rows:
            mark = "" if row.present == len(advice.tracks) else \
                f"  ({row.present}/{len(advice.tracks)})"
            tone = INK if row.present == len(advice.tracks) else INK_SOFT
            value = f"{row.points} pts" if by_points else sum_text(row.ms)
            self._row(panel, f"{row.place:>2}. {row.name}{mark}", value, tone=tone)

        notes = ["F7 switches to the time sum" if by_points
                 else "F7 switches to points"]
        if advice.missing_tracks:
            notes.append("no board yet for " + ", ".join(advice.missing_tracks))
        if advice.shallow_tracks:
            notes.append("too thin for the time sum: "
                         + ", ".join(sorted(set(advice.shallow_tracks))))
        if state.klass_source.startswith("PI"):
            notes.append(f"class guessed from the {state.klass_source}")
        notes.append("a low place usually means few surviving laps, not a slow car")
        panel.note.configure(text=" — ".join(notes))

    def _render_unreadable(self, state: ScreenState) -> None:
        """Say what was and was not found, so a miss is diagnosable at a glance."""
        panel = self.panels.get("right")
        if panel is None:
            return
        panel.clear()
        self._set_head(panel, "Could not read the routes",
                       f"{len(state.lines)} lines of text on screen")
        if state.tracks:
            self._heading(panel, "routes recognised")
            for name in state.tracks:
                self._row(panel, name, f"{state.track_scores.get(name, 0):.2f}",
                          tone=INK_SOFT)
        else:
            self._row(panel, "No route name matched.", "", tone=WARN)
        self._row(panel, f"class: {state.klass or 'not found'}", "",
                  tone=INK_SOFT if state.klass else WARN)
        self._heading(panel, "what usually fixes it")
        for hint in ("Open the Horizon Play screen, then press again.",
                     "The route names have to be on screen as text.",
                     "Exclusive fullscreen hides the overlay entirely — "
                     "use borderless windowed."):
            self._row(panel, hint, "", tone=INK_SOFT, small=True)
        panel.note.configure(
            text="scripts/screen_reader.py --shot -v prints every line the OCR "
                 "read, which is how to calibrate a screen it keeps missing")

    # ---------------- the left panel: your car ---------------- #

    def _render_car(self, force: bool = False) -> None:
        panel = self.panels.get("left")
        if panel is None:
            return
        car_state = self.telemetry.state
        known = self.ordinals.lookup(car_state.ordinal)
        car_index = known.get("car_index") if known else None
        assumed = False
        if car_index is None and self.screen and self.screen.car is not None:
            car_index = self.screen.car
        if car_index is None:
            # The unproved but likely route: the ordinal used as a leaderboard
            # car_id. Marked, never silent -- if the name below is not the car
            # under you, that is the answer to whether the two numberings match.
            car_index = self.adv.car_index_for_id(car_state.ordinal)                 if car_state.ordinal is not None else None
            assumed = car_index is not None

        key = (car_index, car_state.ordinal, car_state.pi, self.score_mode)
        if key == self.last_car_key and not force:
            return
        self.last_car_key = key
        panel.clear()

        if not car_state.fresh:
            reason = self.telemetry.error or \
                f"no packets on port {self.cfg['telemetry_port']}"
            self._set_head(panel, "Your car", reason)
            self._row(panel, "Forza: Settings → HUD and Gameplay → Data Out",
                      "", tone=INK_SOFT, small=True)
            self._row(panel, f"On · 127.0.0.1 · port {self.cfg['telemetry_port']}",
                      "", tone=INK_SOFT, small=True)
            if self.telemetry.error and "already taken" in self.telemetry.error:
                self._row(panel, "Set telemetry_relay_port in config/overlay.json "
                                 "and point the haptics tool's port box at it — "
                                 "then both can run.", "", tone=INK_SOFT, small=True)
            return

        name = (known or {}).get("name")
        if name is None and car_index is not None:
            name = self.adv.car_names[car_index]
        pi_class = car_state.pi_class
        head_bits = [f"PI {car_state.pi}" if car_state.pi else "",
                     f"class {pi_class}" if pi_class else "",
                     DRIVETRAIN.get(car_state.drivetrain if car_state.drivetrain
                                    is not None else -1, ""),
                     f"{car_state.cylinders} cyl" if car_state.cylinders else "",
                     # Always shown: a wrong name next to the right ordinal is
                     # the whole diagnosis.
                     f"ordinal {car_state.ordinal}"]
        self._set_head(panel, name or f"Car ordinal {car_state.ordinal}",
                       " · ".join(b for b in head_bits if b))

        if car_index is None:
            self._row(panel, "This ordinal is not tied to a car yet.", "", tone=WARN)
            self._row(panel, "Open a screen that prints the car's name (garage, "
                             "tuning, the route screen) and press the read key: "
                             "it is learned and remembered.", "",
                      tone=INK_SOFT, small=True)
            return

        if assumed:
            self._row(panel, "Name assumed from the ordinal — not confirmed.", "",
                      tone=WARN, small=True)
        stands = self.adv.standings(car_index)
        if not stands:
            self._row(panel, "No lap by this car in the records yet.", "", tone=WARN)
            panel.note.configure(text="Only cars that appear on a scanned "
                                      "leaderboard can be placed.")
            return

        # Ranked by the same metric the right panel is set to, so both sides of
        # the screen answer the same question.
        place = (lambda s: s.place_points) if self.score_mode == "points" else \
            (lambda s: s.place_time)
        best = min(stands, key=lambda s: place(s) / max(1, s.of))
        by_category: dict[str, list[ra.Standing]] = {}
        for stand in stands:
            by_category.setdefault(stand.category, []).append(stand)
        for category, group in by_category.items():
            self._heading(panel, category)
            for stand in group:
                share = place(stand) / max(1, stand.of)
                tone = GOOD if stand is best else (
                    INK if share <= 0.25 else INK_SOFT)
                here = " ←" if pi_class == stand.klass else ""
                self._row(panel, f"{stand.klass:<3}{here}",
                          f"#{place(stand)} of {stand.of}", tone=tone,
                          right_tone=tone)
                self._row(panel, f"     {stand.present}/{stand.tracks} routes"
                          + (f" · {ra.TUNE_LABEL.get(stand.tune, '')}"
                             if stand.tune else ""),
                          sum_text(stand.ms), small=True, indent=6)

        missing = [c for c in self.cfg.get("categories_expected", [])
                   if c not in by_category]
        note = [f"best fit: {best.category} {best.klass} "
                f"(#{place(best)} of {best.of})"]
        if missing:
            note.append("not scanned yet: " + ", ".join(missing))
        panel.note.configure(text=" — ".join(note))

    # ---------------- the loop ---------------- #

    def _tick(self) -> None:
        try:
            self._drain_events()
            self._collect_ocr()
            self._start_ocr()
            if self._visible("left"):
                self._render_car()
            now = time.monotonic()
            for side in list(self.panels):
                panel = self.panels[side]
                if panel.until and self.pinned != side and now >= panel.until:
                    self._hide(side)
        except Exception as exc:                              # pragma: no cover
            print(f"[overlay] tick failed: {type(exc).__name__}: {exc}")
        self.root.after(int(float(self.cfg["poll_seconds"]) * 1000), self._tick)

    def _drain_events(self) -> None:
        while True:
            try:
                event = self.events.get_nowait()
            except queue.Empty:
                return
            if event == "quit":
                self.stop()
                return
            if event == "pin":
                up = next((s for s in self.panels if self.panels[s].until), None)
                if up is None:
                    continue
                self.pinned = None if self.pinned == up else up
                self._show(up, 10 ** 6 if self.pinned else None)
            elif event == "score":
                self.score_mode = "time" if self.score_mode == "points" else "points"
                self.last_advice_key = None
                if self.screen is not None and self.screen.is_offer:
                    self._render_advice(self.screen)
                if self._visible("left"):
                    self._render_car(force=True)
                    self._show("left")
                elif self._visible("right"):
                    self._show("right")
            elif event == "left":
                self._render_car(force=True)
                self._show("left")
            elif event == "right":
                # Answer at once from the last read, so the press feels
                # immediate; the fresh read replaces it a second later.
                if self.screen is not None and self.screen.is_offer:
                    self._render_advice(self.screen)
                self._show("right")
                self._start_ocr(force=True)

    def _start_ocr(self, force: bool = False) -> None:
        if self._ocr_busy.is_set() or self.args.demo:
            return
        if not force and not self.cfg.get("auto_show", True) \
                and not self._visible("right"):
            # Nothing would be done with the answer, so do not pay for it.
            return
        self._ocr_busy.set()
        if force:
            self._forced = True

        def work() -> None:
            try:
                state = self.watcher.poll(force=force)
                if state is not None:
                    self._ocr_result.put(state)
            except Exception as exc:                          # pragma: no cover
                print(f"[overlay] read failed: {type(exc).__name__}: {exc}")
            finally:
                self._ocr_busy.clear()

        threading.Thread(target=work, name="ocr", daemon=True).start()

    def _collect_ocr(self) -> None:
        state = None
        while True:
            try:
                state = self._ocr_result.get_nowait()
            except queue.Empty:
                break
        if state is None:
            return
        forced, self._forced = self._forced, False
        self.screen = state
        self._learn_ordinal(state)
        if not state.is_offer:
            if forced:
                # A press has to answer, even when the answer is "I could not
                # read it" -- and that message goes away on the same timer.
                self._render_unreadable(state)
                self._show("right")
            return
        if state.key() != self.last_advice_key:
            self.last_advice_key = state.key()
            self._render_advice(state)
        # An automatic show never interrupts the left panel: that one was asked
        # for by hand.
        if forced or (self.cfg.get("auto_show", True) and not self._visible("left")):
            self._show("right")

    def _learn_ordinal(self, state: ScreenState) -> None:
        car_state = self.telemetry.state
        if state.car is None or not car_state.fresh or car_state.ordinal is None:
            return
        if self.ordinals.learn(car_state.ordinal, state.car,
                               self.adv.car_names[state.car], car_state.pi):
            print(f"[overlay] ordinal {car_state.ordinal} = "
                  f"{self.adv.car_names[state.car]}", flush=True)
            self.last_car_key = None

    def run(self) -> None:
        print(f"[overlay] {self.width}x{self.height}, {len(self.adv.boards)} "
              f"boards, telemetry port {self.cfg['telemetry_port']}", flush=True)
        print(f"[overlay] showing the {self.score_mode} order for "
              f"{float(self.cfg['show_seconds']):g}s per press", flush=True)
        print("[overlay] F8/View: what to drive   F6/left-stick: your car   "
              "F7: points-time   F9: pin   F10: quit", flush=True)
        try:
            self.root.mainloop()
        except KeyboardInterrupt:
            self.stop()

    def stop(self) -> None:
        self.telemetry.stop()
        self.triggers.stop()
        try:
            self.root.destroy()
        except tk.TclError:
            pass


# --------------------------------------------------------------------------- #

def main() -> int:
    parser = argparse.ArgumentParser(
        description="Overlay: what to drive, and where your car stands.")
    parser.add_argument("--config", type=Path, default=CONFIG_PATH)
    parser.add_argument("--dataset", type=Path, default=ra.DEFAULT_DATASET)
    parser.add_argument("--poll", type=float, default=None,
                        help="seconds between cheap screen checks")
    parser.add_argument("--port", type=int, default=None, help="telemetry port")
    parser.add_argument("--score", choices=["points", "time"], default=None,
                        help="which single ranking to show; also F7 while running")
    parser.add_argument("--show-seconds", type=float, default=None,
                        help="how long a press keeps a panel up")
    parser.add_argument("--left-only", action="store_true")
    parser.add_argument("--right-only", action="store_true")
    parser.add_argument("--demo", help="comma-separated routes, to draw without the game")
    parser.add_argument("--class", dest="klass", default="A",
                        help="class for --demo")
    args = parser.parse_args()

    cfg = load_config(args.config)
    if args.poll is not None:
        cfg["poll_seconds"] = args.poll
    if args.port is not None:
        cfg["telemetry_port"] = args.port
    if args.score is not None:
        cfg["score_mode"] = args.score
    if args.show_seconds is not None:
        cfg["show_seconds"] = args.show_seconds
    cfg.setdefault("categories_expected",
                   ["Road Racing", "Street Racing", "Touge", "Dirt Racing"])

    if not Path(args.dataset).exists():
        print(f"no dataset at {args.dataset}; run scripts/build_analytics_site.py")
        return 2
    app = OverlayApp(cfg, Advisor.load(args.dataset), args)
    app.run()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
