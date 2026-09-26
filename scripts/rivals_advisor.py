"""Answer two questions off the Rivals dataset, for a live overlay to draw.

    1. "These three tracks, this class -- which cars, in what order?"
    2. "The car I am in -- where does it stand, per category and class?"

Both are exactly what the analytics page computes; this module is a port of its
scoring so the overlay and the website can never disagree. The rules that matter
live in `build_analytics_site.py` (`pickCars`, `classTable`, `renderScoreboard`)
and are mirrored here with the same names, so a change there is easy to carry
over. `scripts/test_rivals_advisor.py` runs the page's own JavaScript over the
same payload and fails on any divergence.

    from rivals_advisor import Advisor
    adv = Advisor.load()
    advice = adv.advise(["Soni Circuit", "Tokyo Sprint", "Venus Sprint"], "A")
    for row in advice.by_points[:10]:
        print(row.place, row.name, row.points)
"""

from __future__ import annotations

import json
import unicodedata
from dataclasses import dataclass, field
from difflib import SequenceMatcher
from pathlib import Path
from typing import Sequence

WORKSPACE = Path(__file__).resolve().parent.parent
DEFAULT_DATASET = WORKSPACE / "data/analytics/laps.json"

# Slowest to fastest. "X" exists only as a stock class, never as a board.
CLASS_ORDER = ["D", "C", "B", "A", "S1", "S2", "R"]
FULL_CLASS_ORDER = CLASS_ORDER + ["X"]

# A board thinner than this has no meaningful "slowest car", so it scores its
# points but stays out of the time sum -- same constant as the page.
MIN_BOARD_FOR_TIME = 1000

# How the page words the stock class against the board's class.
TUNE_LABEL = {"down": "detuned", "same": "stock class", "up": "tuned up"}


# --------------------------------------------------------------------------- #
# picks and aggregates
# --------------------------------------------------------------------------- #

@dataclass(slots=True)
class Pick:
    """The one lap that represents a car on one board."""
    ms: int
    rank: int
    best_rank: int
    took: int
    wanted: int
    count: int
    thin: bool


@dataclass(slots=True)
class TrackEntry:
    track: str
    klass: str
    picks: dict[int, Pick]
    worst: int
    deep: bool
    rows: int


@dataclass(slots=True)
class CarCell:
    """One car on one track inside a class table."""
    ms: int | None
    points: int
    pos: int | None
    of: int
    substituted: bool = False
    missing: bool = False
    out_of_time_sum: bool = False
    rank: int | None = None
    took: int = 0


@dataclass(slots=True)
class CarAgg:
    car: int
    points: int = 0
    ms: int = 0
    present: int = 0
    thin: int = 0
    per: dict[str, CarCell] = field(default_factory=dict)


@dataclass(slots=True)
class ClassTable:
    klass: str
    tracks: list[TrackEntry]
    cars: list[CarAgg]
    time_tracks: int
    shallow_tracks: list[str]


@dataclass(slots=True)
class Row:
    """One line of the overlay's car order."""
    place: int
    car: int
    name: str
    points: int
    ms: int
    present: int
    thin: int
    per: dict[str, CarCell]
    stock_class: str | None = None
    stock_pi: int | None = None
    tune: str | None = None


@dataclass(slots=True)
class Advice:
    klass: str
    tracks: list[str]
    missing_tracks: list[str]
    shallow_tracks: list[str]
    by_points: list[Row]
    by_time: list[Row]
    time_tracks: int


@dataclass(slots=True)
class Standing:
    """Where one car stands in one category and class."""
    category: str
    klass: str
    place_points: int
    place_time: int
    of: int
    points: int
    ms: int
    present: int
    tracks: int
    tune: str | None


# --------------------------------------------------------------------------- #
# the dataset
# --------------------------------------------------------------------------- #

class Advisor:
    def __init__(self, payload: dict) -> None:
        self.d = payload
        self.tracks: list[str] = payload["tracks"]
        self.classes: list[str] = payload["classes"]
        self.categories: list[str] = payload["categories"]
        self.car_names: list[str] = payload["carNames"]
        self.car_ids: list[int] = payload["carIds"]
        self.car_meta: list[dict | None] = payload.get("carMeta") or []
        # Signature bit order comes from the dataset, so a flag added upstream
        # cannot silently change what a filter means here.
        self.bit = {name: 1 << i for i, name in enumerate(payload["flags"])}
        self.boards: list[dict] = payload["boards"]
        self._norm_tracks = {_normalise(name): name for name in self.tracks}
        self._table_cache: dict[tuple, ClassTable] = {}
        self._car_by_name: dict[str, int] = {}
        for index, name in enumerate(self.car_names):
            self._car_by_name.setdefault(_normalise(name), index)

    @classmethod
    def load(cls, path: Path | str = DEFAULT_DATASET) -> "Advisor":
        return cls(json.loads(Path(path).read_text(encoding="utf-8-sig")))

    # ---------------- vocabulary, for the screen reader ---------------- #

    @property
    def class_names(self) -> list[str]:
        """Board classes present, slowest first."""
        seen = set(self.classes)
        return ([k for k in CLASS_ORDER if k in seen]
                + [k for k in self.classes if k not in CLASS_ORDER])

    def track_names(self, category: str | None = None) -> list[str]:
        if category is None:
            return list(self.tracks)
        return sorted({self.tracks[b["t"]] for b in self.boards
                       if self.categories[b["c"]] == category})

    def has_board(self, track: str, klass: str,
                  category: str | None = None) -> bool:
        return any(self.tracks[b["t"]] == track and self.classes[b["k"]] == klass
                   and (category is None or self.categories[b["c"]] == category)
                   for b in self.boards)

    def classes_for_track(self, track: str,
                          category: str | None = None) -> list[str]:
        out = {self.classes[b["k"]] for b in self.boards
               if self.tracks[b["t"]] == track
               and (category is None or self.categories[b["c"]] == category)}
        return [k for k in CLASS_ORDER if k in out]

    # ---------------- core scoring ---------------- #

    def masks(self, clean: str = "valid", gear: str = "any",
              assists: dict[str, str] | None = None) -> tuple[int, int]:
        """Filter -> (need, forbid) signature masks, as the page's `masks()`.

        The overlay defaults to `clean="valid"`: a dirty lap is not a time you
        can aim at. Everything else stays open, because narrowing the assists
        would also narrow which car is represented at all.
        """
        need = forbid = 0
        bit = self.bit
        if clean == "valid":
            need |= bit["clean"]
        elif clean == "invalid":
            forbid |= bit["clean"]
        if gear == "auto":
            need |= bit["autoshift"]
        elif gear == "manual":
            forbid |= bit["autoshift"] | bit["clutch"]
        elif gear == "clutch":
            need |= bit["clutch"]
            forbid |= bit["autoshift"]
        for key, mode in (assists or {}).items():
            if key not in bit:
                continue
            if mode == "with":
                need |= bit[key]
            elif mode == "without":
                forbid |= bit[key]
        return need, forbid

    def pick_cars(self, board: dict, need: int, forbid: int) -> dict[int, Pick]:
        """Which lap represents each car on one board -- the page's `pickCars`."""
        gsig = board["gsig"]
        gcar = board["gcar"]
        gcount = board["gcount"]
        ok = [False] * len(gsig)
        laps: dict[int, list[tuple[int, int]]] = {}
        counts: dict[int, int] = {}
        for g, sig in enumerate(gsig):
            if (sig & need) != need or (sig & forbid):
                continue
            ok[g] = True
            car = gcar[g]
            counts[car] = counts.get(car, 0) + gcount[g]
            laps.setdefault(car, [])
        lgrp, lms, lrank = board["lgrp"], board["lms"], board["lrank"]
        for i, g in enumerate(lgrp):
            if not ok[g]:
                continue
            laps[gcar[g]].append((lms[i], lrank[i]))

        out: dict[int, Pick] = {}
        for car, entries in laps.items():
            if not entries:
                continue
            entries.sort(key=lambda pair: pair[0])
            best_rank = min(rank for _, rank in entries)
            # The better the car's best lap looks, the deeper into its own times
            # we reach, because a lap at the very top is usually an outlier.
            wanted = 5 if best_rank <= 100 else (2 if best_rank <= 1000 else 1)
            have = min(wanted, len(entries))
            ms, rank = entries[have - 1]
            count = counts[car]
            out[car] = Pick(ms=ms, rank=rank, best_rank=best_rank, took=have,
                            wanted=wanted, count=count, thin=count < wanted)
        return out

    def class_table(self, klass: str, tracks: Sequence[str] | None = None,
                    category: str | None = None,
                    need: int = 0, forbid: int = 0) -> ClassTable:
        """Points and time sum for one class -- the page's `classTable`."""
        cache_key = (klass, tuple(sorted(tracks)) if tracks else None,
                     category, need, forbid)
        hit = self._table_cache.get(cache_key)
        if hit is not None:
            return hit

        wanted_tracks = set(tracks) if tracks else None
        entries: list[TrackEntry] = []
        shallow: list[str] = []
        for board in self.boards:
            if self.classes[board["k"]] != klass:
                continue
            if category is not None and self.categories[board["c"]] != category:
                continue
            name = self.tracks[board["t"]]
            if wanted_tracks is not None and name not in wanted_tracks:
                continue
            picks = self.pick_cars(board, need, forbid)
            if not picks:
                continue
            worst = max(p.ms for p in picks.values())
            rows = board.get("valid") or board.get("rows") or 0
            deep = rows >= MIN_BOARD_FOR_TIME
            if not deep:
                shallow.append(name)
            entries.append(TrackEntry(track=name, klass=klass, picks=picks,
                                      worst=worst, deep=deep, rows=rows))

        cars: dict[int, CarAgg] = {}
        for entry in entries:
            ordered = sorted(entry.picks.items(), key=lambda kv: kv[1].ms)
            n = len(ordered)
            for index, (car, pick) in enumerate(ordered):
                agg = cars.get(car)
                if agg is None:
                    agg = cars[car] = CarAgg(car=car)
                # Place 1 scores as many points as there are cars on this board.
                points = n - index
                agg.points += points
                agg.present += 1
                if pick.thin:
                    agg.thin += 1
                agg.per[entry.track] = CarCell(
                    ms=pick.ms, points=points, pos=index + 1, of=n,
                    rank=pick.rank, took=pick.took)

        # A car missing on a track inherits that track's slowest time, so the sum
        # stays comparable across cars that did not run everywhere.
        for agg in cars.values():
            for entry in entries:
                own = agg.per.get(entry.track)
                if own is not None:
                    if entry.deep:
                        agg.ms += own.ms
                    else:
                        own.out_of_time_sum = True
                    continue
                if not entry.deep:
                    agg.per[entry.track] = CarCell(
                        ms=None, points=0, pos=None, of=len(entry.picks),
                        missing=True, out_of_time_sum=True)
                    continue
                agg.ms += entry.worst
                agg.per[entry.track] = CarCell(
                    ms=entry.worst, points=0, pos=None, of=len(entry.picks),
                    substituted=True)

        table = ClassTable(klass=klass, tracks=entries,
                           cars=list(cars.values()),
                           time_tracks=sum(1 for e in entries if e.deep),
                           shallow_tracks=shallow)
        self._table_cache[cache_key] = table
        return table

    # ---------------- question 1: what to drive ---------------- #

    def advise(self, tracks: Sequence[str], klass: str,
               category: str | None = None, limit: int | None = None,
               clean: str = "valid", gear: str = "any") -> Advice:
        """Rank the cars for a set of tracks in one class.

        `tracks` are names as the dataset spells them -- run them through
        `match_track` first if they came off the screen. Tracks without a board
        in this class are reported in `missing_tracks` rather than dropped
        silently: the order then answers a smaller question than was asked, and
        the overlay has to say so.
        """
        if category is None and len(self.categories) == 1:
            category = self.categories[0]
        wanted = list(dict.fromkeys(tracks))
        have = [t for t in wanted if self.has_board(t, klass, category)]
        missing = [t for t in wanted if t not in have]

        need, forbid = self.masks(clean=clean, gear=gear)
        table = self.class_table(klass, tracks=have or None, category=category,
                                 need=need, forbid=forbid)

        return Advice(klass=klass, tracks=have, missing_tracks=missing,
                      shallow_tracks=table.shallow_tracks,
                      by_points=self._rank(table, "points", limit),
                      by_time=self._rank(table, "time", limit),
                      time_tracks=table.time_tracks)

    def _rank(self, table: ClassTable, mode: str,
              limit: int | None = None) -> list[Row]:
        # Stable sort over the insertion order of `cars`, so ties fall the same
        # way the page's stable Array.sort leaves them.
        if mode == "points":
            ordered = sorted(table.cars, key=lambda a: -a.points)
        else:
            ordered = sorted(table.cars, key=lambda a: a.ms)
        rows: list[Row] = []
        for index, agg in enumerate(ordered):
            if limit is not None and index >= limit:
                break
            meta = self.car_meta[agg.car] if agg.car < len(self.car_meta) else None
            meta = meta or {}
            rows.append(Row(
                place=index + 1, car=agg.car, name=self.car_names[agg.car],
                points=agg.points, ms=agg.ms, present=agg.present,
                thin=agg.thin, per=agg.per,
                stock_class=meta.get("stockClass"),
                stock_pi=meta.get("stockPi"),
                tune=self.tune_of(agg.car, table.klass)))
        return rows

    def tune_of(self, car: int, board_class: str) -> str | None:
        """Where the car started, against the class of the board it ran on."""
        meta = self.car_meta[car] if car < len(self.car_meta) else None
        stock = (meta or {}).get("stockClass")
        if not stock:
            return None
        try:
            si = FULL_CLASS_ORDER.index(stock)
            bi = FULL_CLASS_ORDER.index(board_class)
        except ValueError:
            return None
        if bi < si:
            return "down"
        if bi > si:
            return "up"
        return "same"

    # ---------------- question 2: where does my car stand ---------------- #

    def standings(self, car: int, clean: str = "valid",
                  gear: str = "any") -> list[Standing]:
        """Every category and class in which this car has a lap at all.

        One entry per (category, class): the car's place by points and by time
        sum over ALL tracks of that class, which is the website's default view.
        A class the car never ran is left out -- that is the answer to "which
        class should I build this car for": the ones where it places well.
        """
        need, forbid = self.masks(clean=clean, gear=gear)
        out: list[Standing] = []
        for category in self.categories:
            for klass in self.class_names:
                table = self.class_table(klass, tracks=None, category=category,
                                         need=need, forbid=forbid)
                if not table.tracks:
                    continue
                mine = next((a for a in table.cars if a.car == car), None)
                if mine is None:
                    continue
                by_points = sorted(table.cars, key=lambda a: -a.points)
                by_time = sorted(table.cars, key=lambda a: a.ms)
                out.append(Standing(
                    category=category, klass=klass,
                    place_points=by_points.index(mine) + 1,
                    place_time=by_time.index(mine) + 1,
                    of=len(table.cars), points=mine.points, ms=mine.ms,
                    present=mine.present, tracks=len(table.tracks),
                    tune=self.tune_of(car, klass)))
        return out

    # ---------------- matching what the screen and the game say ---------------- #

    def match_track(self, text: str,
                    cutoff: float = 0.80) -> tuple[str, float] | None:
        """Map one OCR'd line to a track name, or None.

        Matching against a closed vocabulary is what makes the screen reader
        layout-independent: no row geometry has to be known, only that the name
        appears somewhere in the frame.

        Die Schwelle stand bis 2026-09-09 auf 0,62 -- passend fuer die 23 Namen von
        Road Racing. Mit inzwischen 72 Namen aus vier Kategorien sind zufaellige
        Aehnlichkeiten viel wahrscheinlicher, und eine ist eingetreten: der MENUETEXT
        "Horizon Festival" traf die Dirt-Racing-Strecke "Horizon Stadium Scramble"
        mit 0,65. Auf dem Event-Sign-Up-Schirm haette das eine Strecke erfunden, die
        gar nicht angeboten wird.

        0,80 liegt mittig in einer breiten Luecke, gemessen am ganzen Bestand:
        echte Lesungen mit OCR-Rauschen erreichen 0,90 bis 1,00 (schlechtester Fall
        "goliath" -> "The Goliath" mit 0,90), der schlimmste Fehltreffer 0,65.
        Die Schwelle waechst also nicht mit dem Vokabular mit -- wer eine weitere
        Kategorie freischaltet, sollte sie neu messen.
        """
        probe = _normalise(text)
        if not probe:
            return None
        exact = self._norm_tracks.get(probe)
        if exact:
            return exact, 1.0
        best: tuple[str, float] | None = None
        for norm, name in self._norm_tracks.items():
            score = _similarity(probe, norm)
            # A short OCR fragment sitting inside a name ("goliath") is a hit,
            # and so is the reverse ("the goliath  stage 2").
            if (norm in probe or probe in norm) and len(probe) >= 5:
                # Der Einschluss-Bonus ist fuer BRUCHSTUECKE gedacht: "goliath" statt
                # "The Goliath", "hokubu" statt "Hokubu Circuit". Er darf nicht gelten,
                # wenn dem Gelesenen mehrere ganze Woerter des Namens fehlen -- sonst
                # wird der Menuetext "Horizon Festival" zur Strecke "Horizon Festival
                # Drag Strip" (0,90 bei Schwelle 0,80, aufgetaucht am 2026-09-10, als
                # die Drag-Strecken in den Bestand kamen). Ein fehlendes Wort ist ein
                # weggelassener Zusatz ("Circuit"), zwei sind ein anderer Ort.
                fehlend = [w for w in norm.split() if w not in probe.split()
                           and len(w) >= 4]
                if len(fehlend) <= 1:
                    score = max(score, 0.90)
            if best is None or score > best[1]:
                best = (name, score)
        return best if best and best[1] >= cutoff else None

    def match_car_name(self, text: str, cutoff: float = 0.72) -> int | None:
        """Map an OCR'd car name to a car index in the dataset."""
        probe = _normalise(text)
        if not probe:
            return None
        hit = self._car_by_name.get(probe)
        if hit is not None:
            return hit
        best_index, best_score = None, 0.0
        for norm, index in self._car_by_name.items():
            score = _similarity(probe, norm)
            if (norm in probe or probe in norm) and len(probe) >= 6:
                score = max(score, 0.88)
            if score > best_score:
                best_index, best_score = index, score
        return best_index if best_score >= cutoff else None

    def car_index_for_id(self, car_id: int) -> int | None:
        try:
            return self.car_ids.index(int(car_id))
        except ValueError:
            return None


# --------------------------------------------------------------------------- #
# text helpers
# --------------------------------------------------------------------------- #

def _normalise(text: str) -> str:
    """Fold case, accents and punctuation so OCR noise stops mattering."""
    folded = unicodedata.normalize("NFKD", str(text))
    folded = "".join(c for c in folded if not unicodedata.combining(c))
    keep = "".join(c.lower() if c.isalnum() else " " for c in folded)
    return " ".join(keep.split())


def _similarity(a: str, b: str) -> float:
    """Token-aware ratio: OCR drops and doubles characters, not whole words."""
    if not a or not b:
        return 0.0
    plain = SequenceMatcher(None, a, b).ratio()
    ta, tb = set(a.split()), set(b.split())
    if not ta or not tb:
        return plain
    jaccard = len(ta & tb) / len(ta | tb)
    return min(1.0, max(plain, (plain + jaccard) / 2 + jaccard / 4))


def lap_text(ms: int | None) -> str:
    if ms is None:
        return "--"
    minutes, rest = divmod(int(ms), 60_000)
    seconds, milli = divmod(rest, 1000)
    if minutes:
        return f"{minutes}:{seconds:02d}.{milli:03d}"
    return f"{seconds}.{milli:03d}"


def sum_text(ms: int | None) -> str:
    if ms is None:
        return "--"
    hours, rest = divmod(int(ms), 3_600_000)
    minutes, rest = divmod(rest, 60_000)
    seconds = rest / 1000
    if hours:
        return f"{hours}:{minutes:02d}:{seconds:06.3f}"
    return f"{minutes}:{seconds:06.3f}"


if __name__ == "__main__":
    import argparse

    parser = argparse.ArgumentParser(
        description="Rank cars for a set of tracks, or place one car.",
        formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--dataset", type=Path, default=DEFAULT_DATASET)
    parser.add_argument("--class", dest="klass", default="A")
    parser.add_argument("--tracks", nargs="*", default=[],
                        help="track names as the screen spells them; fuzzy-matched")
    parser.add_argument("--car", help="car name, to print its standings instead")
    parser.add_argument("--limit", type=int, default=15)
    args = parser.parse_args()

    adv = Advisor.load(args.dataset)
    if args.car:
        index = adv.match_car_name(args.car)
        if index is None:
            raise SystemExit(f"no car matches {args.car!r}")
        print(f"{adv.car_names[index]}  (car_id {adv.car_ids[index]})")
        for st in adv.standings(index):
            print(f"  {st.category:<14} {st.klass:<2}  points #{st.place_points:>4}/"
                  f"{st.of:<4}  time #{st.place_time:>4}/{st.of:<4}"
                  f"  on {st.present}/{st.tracks} tracks")
        raise SystemExit(0)

    names: list[str] = []
    for raw in args.tracks:
        hit = adv.match_track(raw)
        if hit is None:
            raise SystemExit(f"no track matches {raw!r}")
        names.append(hit[0])
    if not names:
        names = adv.track_names()
    advice = adv.advise(names, args.klass, limit=args.limit)
    print(f"class {advice.klass} over {', '.join(advice.tracks)}")
    if advice.missing_tracks:
        print(f"  no board yet: {', '.join(advice.missing_tracks)}")
    print("\n  by points")
    for row in advice.by_points:
        print(f"   {row.place:>3}. {row.name:<38} {row.points:>6} pts"
              f"  {row.present}/{len(advice.tracks)}")
    print("\n  by time sum")
    for row in advice.by_time:
        print(f"   {row.place:>3}. {row.name:<38} {sum_text(row.ms):>12}"
              f"  {row.present}/{len(advice.tracks)}")
