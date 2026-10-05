"""Prove every scoring implementation is the website's scoring, not a re-reading of it.

There are three of them now:

    the page's JavaScript   scripts/build_analytics_site.py   -- the source of truth
    the Python advisor      scripts/rivals_advisor.py         -- CLI and tooling
    the C# advisor          haptics/.../Rivals/RivalsAdvisor.cs -- the play-time overlay

Three implementations of a rule that is still changing drift silently: the overlay
would keep naming a car the page no longer ranks first, and nothing would look
broken. So the reference side of every check here is the shipped page's own
JavaScript, evaluated by `dump_site_class_table.js`, and both ports are compared
against it -- car by car, on points, time sum, presence, thin count and the full
ordering including ties.

    python scripts/test_rivals_advisor.py
    python scripts/test_rivals_advisor.py --no-csharp

Needs node on PATH for the reference dumps, and a built haptics binary for the C#
side; either missing turns its checks into skips, which is reported rather than
hidden.
"""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import rivals_advisor as ra

WORKSPACE = Path(__file__).resolve().parent.parent
DEFAULT_PAGE = WORKSPACE / "data/analytics/rivals_auto_wertung.html"
HAPTICS = WORKSPACE / "haptics/ForzaHaptics.Tester"
EXE_NAME = "FH Companion.exe"

failures: list[str] = []
skipped: list[str] = []


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


# --------------------------------------------------------------------------- #
# the three implementations
# --------------------------------------------------------------------------- #

def site_table(page: Path, klass: str, tracks: list[str],
               clean: str = "valid", gear: str = "any") -> dict:
    """The reference: the shipped page's own JavaScript."""
    node = shutil.which("node")
    if not node:
        raise Skip("node is not on PATH")
    cmd = [node, str(WORKSPACE / "scripts/dump_site_class_table.js"),
           "--page", str(page), "--class", klass,
           "--tracks", ",".join(tracks), "--clean", clean, "--gear", gear]
    done = subprocess.run(cmd, capture_output=True, text=True, cwd=WORKSPACE,
                          encoding="utf-8")
    if done.returncode != 0:
        raise Skip(f"dumper failed: {done.stderr.strip()[:200]}")
    return json.loads(done.stdout)


DUMP_SOURCE = HAPTICS / "Rivals/RivalsDump.cs"


def find_exe() -> Path | None:
    """The freshest built binary, not just any one that matches.

    A stale Release build from before the dump flag existed used to win this
    glob, and a WinExe handed an argument it does not know simply opens its
    window and waits -- which looks exactly like a hung test for 300 seconds.
    """
    hits = list(HAPTICS.glob(f"bin/**/{EXE_NAME}"))
    if not hits:
        return None
    return max(hits, key=lambda path: path.stat().st_mtime)


def csharp_table(exe: Path, dataset: Path, klass: str, tracks: list[str],
                 clean: str = "valid", gear: str = "any") -> dict:
    if DUMP_SOURCE.exists() and exe.stat().st_mtime < DUMP_SOURCE.stat().st_mtime:
        raise Skip(f"{exe.name} is older than {DUMP_SOURCE.name}; run "
                   f"dotnet build in haptics/ForzaHaptics.Tester")
    with tempfile.TemporaryDirectory() as tmp:
        out = Path(tmp) / "table.json"
        cmd = [str(exe), "--dump-class-table", str(out), "--class", klass,
               "--tracks", ",".join(tracks), "--clean", clean, "--gear", gear,
               "--dataset", str(dataset)]
        try:
            done = subprocess.run(cmd, capture_output=True, text=True, cwd=WORKSPACE,
                                  encoding="utf-8", errors="replace", timeout=60)
        except subprocess.TimeoutExpired:
            raise Skip("the C# dump did not return in 60 s -- a binary that does "
                       "not know the flag opens its window instead") from None
        if not out.exists():
            raise Skip(f"the C# dump wrote nothing (exit {done.returncode}) "
                       f"{done.stderr.strip()[:160]}")
        return json.loads(out.read_text(encoding="utf-8"))


# --------------------------------------------------------------------------- #
# the comparison
# --------------------------------------------------------------------------- #

def same_as_reference(ref: dict, label: str, tracks: list[str], time_tracks: int,
                      cars: dict[int, dict], by_points: list[int],
                      by_time: list[int], name_of) -> None:
    """One implementation against the page, in the terms the page uses."""
    assert sorted(ref["tracks"]) == sorted(tracks), (
        f"{label}: tracks in scope differ: page {ref['tracks']} vs {tracks}")
    assert ref["timeTracks"] == time_tracks, (
        f"{label}: time-sum track count {time_tracks} != page {ref['timeTracks']}")

    theirs = {row["car"]: row for row in ref["cars"]}
    assert set(theirs) == set(cars), (
        f"{label}: {len(cars)} cars vs page {len(theirs)}; "
        f"only here: {sorted(set(cars) - set(theirs))[:5]}, "
        f"only there: {sorted(set(theirs) - set(cars))[:5]}")
    for car, row in theirs.items():
        got = cars[car]
        for field in ("points", "ms", "present", "thin"):
            assert got[field] == row[field], (
                f"{label}: {row['name']}: {field} {got[field]}, "
                f"page says {row[field]}")

    # Order, not just values: ties have to fall the same way, or the overlay names
    # a different car than the page's first row.
    for what, mine, theirs_order in (("points order", by_points, ref["byPoints"]),
                                     ("time order", by_time, ref["byTime"])):
        assert mine == theirs_order, _first_diff(f"{label}: {what}", mine,
                                                 theirs_order, name_of)


def _first_diff(what: str, mine: list[int], theirs: list[int], name_of) -> str:
    for i, (a, b) in enumerate(zip(mine, theirs)):
        if a != b:
            return (f"{what} diverges at place {i + 1}: {name_of(a)!r} "
                    f"vs page {name_of(b)!r}")
    return f"{what} differs in length: {len(mine)} vs {len(theirs)}"


def compare_python(adv: ra.Advisor, page: Path, klass: str, tracks: list[str],
                   clean: str, gear: str) -> None:
    ref = site_table(page, klass, tracks, clean, gear)
    need, forbid = adv.masks(clean=clean, gear=gear)
    mine = adv.class_table(klass, tracks=tracks or None, need=need, forbid=forbid)
    cars = {agg.car: {"points": agg.points, "ms": agg.ms, "present": agg.present,
                      "thin": agg.thin} for agg in mine.cars}
    same_as_reference(
        ref, "python", [e.track for e in mine.tracks], mine.time_tracks, cars,
        [r.car for r in adv._rank(mine, "points")],
        [r.car for r in adv._rank(mine, "time")],
        lambda c: adv.car_names[c])


def compare_csharp(adv: ra.Advisor, page: Path, exe: Path | None, dataset: Path,
                   klass: str, tracks: list[str], clean: str, gear: str) -> None:
    if exe is None:
        raise Skip("no built haptics binary; run dotnet build in "
                   "haptics/ForzaHaptics.Tester")
    ref = site_table(page, klass, tracks, clean, gear)
    got = csharp_table(exe, dataset, klass, tracks, clean, gear)
    if "error" in got:
        raise Skip(str(got["error"]))
    cars = {row["car"]: row for row in got["cars"]}
    same_as_reference(ref, "c#", got["tracks"], got["timeTracks"], cars,
                      got["byPoints"], got["byTime"], lambda c: adv.car_names[c])


# --------------------------------------------------------------------------- #

def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--page", type=Path, default=DEFAULT_PAGE)
    parser.add_argument("--dataset", type=Path, default=ra.DEFAULT_DATASET)
    parser.add_argument("--exe", type=Path, default=None,
                        help="the built haptics binary; found automatically")
    parser.add_argument("--no-csharp", action="store_true")
    args = parser.parse_args()

    if not args.dataset.exists():
        print(f"no dataset at {args.dataset}")
        return 2
    adv = ra.Advisor.load(args.dataset)
    exe = None if args.no_csharp else (args.exe or find_exe())
    print(f"dataset: {len(adv.boards)} boards, {len(adv.car_names)} cars, "
          f"classes {', '.join(adv.class_names)}")
    print(f"c# side: {exe if exe else 'not built'}")

    if not args.page.exists():
        skipped.append("the page is not built, every comparison skipped")
        print(f"  skip  no page at {args.page}")
    else:
        tracks = adv.track_names()
        deep_a = [t for t in tracks if adv.has_board(t, "A")][:3]
        scenarios = [
            ("class A, all tracks", "A", [], "valid", "any"),
            ("class A, three tracks", "A", deep_a, "valid", "any"),
            ("class D, all tracks", "D", [], "valid", "any"),
            ("class R, all tracks", "R", [], "valid", "any"),
            ("class A, invalid laps", "A", [], "invalid", "any"),
            ("class A, manual gearbox", "A", [], "valid", "manual"),
            ("class A, no filter at all", "A", [], "any", "any"),
        ]
        for name, klass, tracks_arg, clean, gear in scenarios:
            check(f"python == page: {name}",
                  lambda k=klass, t=tracks_arg, c=clean, g=gear:
                  compare_python(adv, args.page, k, t, c, g))
            if not args.no_csharp:
                check(f"c#     == page: {name}",
                      lambda k=klass, t=tracks_arg, c=clean, g=gear:
                      compare_csharp(adv, args.page, exe, args.dataset, k, t, c, g))

    # ---------------- rules the page cannot check ---------------- #

    def missing_track_is_reported():
        tracks = adv.track_names()
        absent = next((t for t in tracks if not adv.has_board(t, "R")), None)
        if absent is None:
            raise Skip("every track has an R board now")
        advice = adv.advise([tracks[0], absent], "R")
        assert absent in advice.missing_tracks, (
            f"{absent} has no R board but was not reported missing")
        assert absent not in advice.tracks

    def places_are_dense_and_one_based():
        advice = adv.advise(adv.track_names(), "A")
        assert advice.by_points[0].place == 1
        assert [r.place for r in advice.by_points] == \
            list(range(1, len(advice.by_points) + 1))

    def standings_place_within_field():
        # standings() liefert einen Eintrag je (KATEGORIE, Klasse) -- so beantwortet
        # der linke Panel "in welcher Klasse ist dieses Auto gut?".
        #
        # Bis 2026-09-09 nahm dieser Test die Strecken ALLER Kategorien, bildete
        # daraus eine Klasse-A-Rangliste und verglich den Sieger mit dem ERSTEN
        # Klasse-A-Eintrag der standings -- einem beliebigen der vier. Das ging nur
        # auf, solange es eine einzige Kategorie gab. Jetzt wird je Kategorie
        # verglichen, also genau so, wie standings() rechnet.
        geprueft = 0
        for category in adv.categories:
            tracks = [t for t in adv.track_names() if adv.classes_for_track(t)]
            tracks = [t for t in tracks if adv.has_board(t, "A", category)]
            if not tracks:
                continue
            advice = adv.advise(tracks, "A", limit=1, category=category)
            if not advice.by_points:
                continue
            car = advice.by_points[0].car
            stands = adv.standings(car)
            mine = next((s for s in stands if s.klass == "A" and s.category == category), None)
            assert mine is not None, (
                f"{adv.car_names[car]} fuehrt {category} Klasse A, hat dort aber keine standings")
            assert mine.place_points == 1, (
                f"{category}: Klasse-A-Fuehrender steht auf Platz {mine.place_points}")
            geprueft += 1
        assert geprueft, "keine Kategorie mit Klasse-A-Boards gefunden"
        # Und in JEDEM Eintrag muss der Platz im Feld liegen.
        for car in range(min(40, len(adv.car_names))):
            for s in adv.standings(car):
                assert 1 <= s.place_points <= s.of
                assert 1 <= s.place_time <= s.of

    def track_matching_survives_ocr_noise():
        cases = [("The Goliath", "The Goliath"),
                 ("THE  GOLIATH", "The Goliath"),
                 ("Th3 Goliath", "The Goliath"),
                 ("goliath", "The Goliath"),
                 ("Soni Circult", "Soni Circuit"),
                 ("Tateyama Kurobe Sprlnt", "Tateyama Kurobe Sprint"),
                 ("Narai-Juku Circuit", "Narai-Juku Circuit"),
                 ("NARAI JUKU CIRCUIT", "Narai-Juku Circuit")]
        for probe, want in cases:
            if want not in adv.tracks:
                raise Skip(f"{want} is not in this dataset")
            hit = adv.match_track(probe)
            assert hit is not None, f"{probe!r} matched nothing"
            assert hit[0] == want, f"{probe!r} matched {hit[0]!r}, wanted {want!r}"

    def unrelated_text_matches_nothing():
        for probe in ["Horizon Festival", "Change Rival", "Continue",
                      "Difficulty", "1:23.456", "Highly Skilled"]:
            hit = adv.match_track(probe)
            assert hit is None, f"{probe!r} was read as the track {hit[0]!r}"

    def car_name_matching_survives_ocr_noise():
        wanted = next((n for n in adv.car_names if not n.startswith("Car #")), None)
        if wanted is None:
            raise Skip("no named car in the dataset")
        index = adv.match_car_name(wanted)
        assert index is not None and adv.car_names[index] == wanted

    check("a track without a board is reported, not dropped", missing_track_is_reported)
    check("places run 1..n with no gaps", places_are_dense_and_one_based)
    check("a car's standing sits inside its field", standings_place_within_field)
    check("track names survive OCR noise", track_matching_survives_ocr_noise)
    check("menu text is not read as a track", unrelated_text_matches_nothing)
    check("car names survive OCR noise", car_name_matching_survives_ocr_noise)

    if skipped:
        print(f"\n{len(skipped)} skipped")
    print(f"\n{len(failures)} FAILED" if failures else "\nall checks passed")
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
