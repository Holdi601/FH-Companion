"""Turn the sweep's per-board parquet files into the analytics lap dataset.

The site recomputes everything under live filters -- only laps without TCS, only
invalid laps, only manual gearbox -- and the rule that decides which of a car's laps
represents it must then run on what is left. So this exports LAPS, not one pre-picked
lap per car:

    board = (category, track, performance class)
    group = (board, car, assist signature), carrying its true lap count
    lap   = (group, lap time in ms, board rank)

Only the five fastest laps per group are kept, and that is provably enough: the rule
never reaches deeper than a car's 5th fastest lap, and every filter the site offers
is a conjunction over the signature bits, so a qualifying set is always a union of
whole groups -- and the 5 fastest of a union are among the 5 fastest of each group in
it. The true count rides along per group, so the site can still say how many laps a
car really has under the current filter.

Each kept lap also carries the PI it was driven at (`lpi`, aligned with `lms` and
`lrank`), when the screen showed one and it fits the board's class. It is display only:
it never decides which laps are kept, so the guarantee above is untouched. A board
without a single read PI leaves `lpi` out entirely rather than shipping a column of
nulls.

Boards are merged across the sweep's several passes (each covers a different rank
span) and deduplicated by rank.

    python scripts/build_analytics_dataset.py
"""

from __future__ import annotations

import argparse
import bisect
import hashlib
import json
import os
import re
import statistics
import time
from collections import defaultdict
from datetime import datetime
from pathlib import Path

import pyarrow.parquet as pq

# Bit order of the assist signature. `clean` is bit 0 and means the lap was VALID, so
# a filter for invalid laps asks for bit 0 clear.
FLAGS = [
    ("clean", "is_clean"),
    ("tcs", "used_tcs"),
    ("abs", "used_abs"),
    ("stm", "used_stm"),
    ("friction", "used_friction_assist"),
    ("autobrake", "used_auto_brake"),
    ("autoshift", "used_auto_shifting"),
    ("clutch", "used_clutch"),
    ("supereasy", "used_super_easy_assist"),
    # AUS DEM NEUESTEN LAUF DIESES BOARDS (seit 2026-10-03): gesetzt bei den Zeilen, die
    # die Bestenliste beim letzten Scan zeigte. Die anderen sind aeltere Zeiten, die der
    # Datensatz BEHAELT -- Rivals fuehrt je Spieler nur eine Zeit, und wer mit einem
    # anderen Auto schneller wird, verdraengt damit seine Zeit mit dem ersten Auto von
    # der Liste. Fuer die Frage "welches Auto ist stark" zaehlt die trotzdem.
    ("current", "_current"),
]

# `car_name` matters as much as `car_id`: a screen-read row has no id and is identified
# by the name OCR read off it, which `resolve_car` then matches against the roster. It
# was missing from this list until 2026-08-23, and because the reader takes only the
# intersection of this list with what a file actually holds, every OCR row arrived with
# no car at all and was dropped -- Hokubu A contributed 81 laps out of 9,078 rows, and
# those 81 came from its 149-row memory scan. A whole night of screen scanning sat in
# the files and never reached the site.
COLUMNS = ["rank", "lap_time_seconds", "car_id", "car_codename", "car_name", "pi"] + \
          [source for _, source in FLAGS]

# Klassenobergrenzen fuer den PI je Runde, dieselben wie PI_CAPS und PI_FLOOR in
# ocr_leaderboard_frames. Hier als eigene Kopie, weil dieser Bau auch im Server-Image
# laeuft, und dort gibt es kein OpenCV, das jenes Modul beim Import braucht. Wer eine
# Grenze aendert, aendert beide. D beginnt bei 100 einschliesslich: PI 100 gibt es
# wirklich (Reliant Supervan, BMW Isetta).
PI_CAPS = {"D": 400, "C": 500, "B": 600, "A": 700, "S1": 800, "S2": 900, "R": 998}
PI_ORDER = ["D", "C", "B", "A", "S1", "S2", "R"]
PI_FLOOR = 99


# ---------------------------------------------------------------- Namenszuordnung
#
# DER TEUERSTE TEIL DES BAUS, gemessen 2026-09-27: 259.100 verschiedene
# Bildschirmnamen ohne car_id (nicht die 25.000 vom 2026-09-11 -- jede OCR-Variante
# eines Namens zaehlt einzeln), je Name 7,7 ms unscharfe Suche, nacheinander auf einem
# Kern. Das Lesen aller Boards dauert dagegen 22 Sekunden.
#
# Die Suche ist eine reine Funktion des Namens: dieselbe Fahrzeugliste, dieselben
# Regeln in build_car_roster.py und dieselben zwei Alias-Tabellen ergeben immer
# dasselbe Auto. Darum (1) auf alle Kerne verteilt und (2) gemerkt, unter einem
# Fingerabdruck genau dieser Dateien. Aendert sich eine davon, wird alles neu gesucht;
# sonst nur, was seit dem letzten Bau neu hinzukam.
#
# Gemerkt wird NUR das Suchergebnis (welcher Eintrag der Fahrzeugliste). Die
# synthetischen Kennungen fuer Autos ohne car_id vergibt weiter der Bau selbst, in der
# Reihenfolge des ersten Auftretens -- sonst wuerden sie von Bau zu Bau springen.
NAMEN_CACHE = Path("data/cache/car_name_matches.json")
ROSTER_HTML = Path("data/car_catalogue/fh6cars.html")
NAMEN_FORMAT = "1"
_NAMEN_ARBEITER: dict = {}


def _namen_fingerabdruck(roster_html: Path) -> str:
    import build_car_roster as rt
    h = hashlib.sha256(NAMEN_FORMAT.encode())
    for p in (Path(rt.__file__), roster_html, rt.ALIAS_FILE, rt.WIKI_FILE):
        h.update(Path(p).name.encode())
        h.update(Path(p).read_bytes() if Path(p).exists() else b"-")
    return h.hexdigest()


def _namen_vorbereiten(roster_html: str) -> None:
    """Je Arbeitsprozess einmal: Fahrzeugliste laden und die Suchindizes bauen."""
    import build_car_roster as rt
    cars = rt.load_roster(Path(roster_html))
    index = rt.build_index(cars)
    _NAMEN_ARBEITER.update(rt=rt, index=index, keys=list(index),
                           years=rt.build_year_index(cars),
                           pos={id(car): i for i, car in enumerate(cars)})


def _namen_suchen(namen: list[str]) -> list[tuple[str, int]]:
    """(Name, Stelle in der Fahrzeugliste); -1 = kein Auto, -2 = Stelle nicht bestimmbar."""
    w = _NAMEN_ARBEITER
    raus = []
    for name in namen:
        sauber = w["rt"].clean_ocr_name(name)
        if not sauber:
            raus.append((name, -1))
            continue
        car, _score = w["rt"].match_car(sauber, w["index"], w["keys"], w["years"])
        raus.append((name, -1 if car is None else w["pos"].get(id(car), -2)))
    return raus


def namen_zuordnen(namen: list[str], roster_html: Path, log=print) -> dict[str, int]:
    """Stelle in der Fahrzeugliste je Name -- aus dem Merker, der Rest parallel gesucht."""
    fingerabdruck = _namen_fingerabdruck(roster_html)
    gemerkt: dict[str, int] = {}
    try:
        daten = json.loads(NAMEN_CACHE.read_text(encoding="utf-8"))
        if daten.get("fingerprint") == fingerabdruck:
            gemerkt = {str(k): int(v) for k, v in daten.get("names", {}).items()}
    except (OSError, ValueError, AttributeError):
        pass
    fehlt = [n for n in namen if n not in gemerkt]
    if fehlt:
        start = time.time()
        # Wenige Namen lohnen keinen Prozessstart (je Prozess rund eine Sekunde fuer
        # die Fahrzeugliste). Viele verteilt -- mit Luft fuer einen laufenden Sweep,
        # dessen OCR auf derselben Maschine rechnet.
        if len(fehlt) < 3000:
            _namen_vorbereiten(str(roster_html))
            gefunden = _namen_suchen(fehlt)
        else:
            from concurrent.futures import ProcessPoolExecutor
            arbeiter = max(2, min(12, (os.cpu_count() or 4) - 4))
            stuecke = [fehlt[i:i + 1000] for i in range(0, len(fehlt), 1000)]
            gefunden = []
            with ProcessPoolExecutor(max_workers=arbeiter, initializer=_namen_vorbereiten,
                                     initargs=(str(roster_html),)) as pool:
                for teil in pool.map(_namen_suchen, stuecke):
                    gefunden.extend(teil)
        gemerkt.update(dict(gefunden))
        log(f"  Namenszuordnung: {len(fehlt):,} neue von {len(namen):,} Namen gesucht "
            f"in {time.time() - start:.0f} s")
        try:
            NAMEN_CACHE.parent.mkdir(parents=True, exist_ok=True)
            zwischen = NAMEN_CACHE.with_name(NAMEN_CACHE.name + ".tmp")
            zwischen.write_text(json.dumps({"fingerprint": fingerabdruck, "names": gemerkt},
                                           ensure_ascii=False, separators=(",", ":")),
                                encoding="utf-8")
            zwischen.replace(NAMEN_CACHE)
        except OSError:
            pass   # ohne Merker nur langsamer, nie falsch
    return {n: gemerkt[n] for n in namen}


def lap_pi(value, klass: str) -> int | None:
    """Der PI einer Zeile, wenn er auf ein Board dieser Klasse passt, sonst None.

    Nochmals geprueft, obwohl der Scanner schon prueft: Beitraege anderer Rechner
    kommen hier ungesehen an, und aeltere Laeufe koennen einen Wert tragen, der vor
    der Pruefung geschrieben wurde. Fehlt die Spalte, steht dort None oder NaN.
    """
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return None
    if value != value or int(value) != value:
        return None
    name = str(klass or "").upper()
    if name not in PI_CAPS:
        return None
    index = PI_ORDER.index(name)
    low = PI_CAPS[PI_ORDER[index - 1]] if index else PI_FLOOR
    return int(value) if low < int(value) <= PI_CAPS[name] else None


LAPS_PER_GROUP = 5

MAKES = {
    "ABA": "Abarth", "ACU": "Acura", "ALF": "Alfa Romeo", "ALP": "Alpine",
    "AMG": "Mercedes-AMG", "AST": "Aston Martin", "AUD": "Audi", "BEN": "Bentley",
    "BMW": "BMW", "BUG": "Bugatti", "BUI": "Buick", "CAD": "Cadillac",
    "CAT": "Caterham", "CHE": "Chevrolet", "CHR": "Chrysler", "CIT": "Citroen",
    "DAT": "Datsun", "DOD": "Dodge", "DON": "Donkervoort", "EAG": "Eagle",
    "FER": "Ferrari", "FIA": "Fiat", "FOR": "Ford", "FRD": "Ford", "GMC": "GMC",
    "HON": "Honda", "HOO": "Hoonigan", "HUM": "Hummer", "HYU": "Hyundai",
    "INF": "Infiniti", "JAG": "Jaguar", "JEE": "Jeep", "KOE": "Koenigsegg",
    "LAM": "Lamborghini", "LAN": "Lancia", "LEX": "Lexus", "LOT": "Lotus",
    "MAS": "Maserati", "MAZ": "Mazda", "MCL": "McLaren", "MER": "Mercedes-Benz",
    "MIN": "Mini", "MIT": "Mitsubishi", "MOR": "Morgan", "NIS": "Nissan",
    "NOB": "Noble", "OPE": "Opel", "PAG": "Pagani", "PEU": "Peugeot",
    "PLY": "Plymouth", "PON": "Pontiac", "POR": "Porsche", "RAD": "Radical",
    "REN": "Renault", "ROL": "Rolls-Royce", "RUF": "RUF", "SAA": "Saab",
    "SAL": "Saleen", "SHE": "Shelby", "SUB": "Subaru", "SUZ": "Suzuki",
    "TOY": "Toyota", "TVR": "TVR", "ULT": "Ultima", "VOL": "Volvo",
    "VOK": "Volkswagen", "VWG": "Volkswagen", "ZEN": "Zenvo",
    "AC": "AC", "ARI": "Ariel", "ATS": "ATS", "BAC": "BAC", "BOL": "Bolwell",
    "DEL": "DeLorean", "EXO": "Exomotive", "FOM": "Formula Drift", "GTA": "GTA",
    "ITA": "Italdesign", "KTM": "KTM", "LIN": "Lincoln", "LOC": "Local Motors",
    "MG": "MG", "OLD": "Oldsmobile", "PEE": "Peel", "POL": "Polestar",
    "RAM": "RAM", "RIM": "Rimac", "TES": "Tesla", "TOR": "Toroidion",
    "TRI": "Triumph", "VUH": "VUHL", "WMO": "W Motors",
}

# carId -> Codename aus den Dateien des Spiels (scripts/build_car_game_ids.py).
GAME_IDS = "config/fh6_car_game_ids.json"
ROSTER = Path("config/fh6_car_roster.json")
ID_NAMES = Path("config/fh6_car_id_names.json")


def load_car_meta() -> dict[int, dict]:
    """carId -> the public roster entry, via names read off the screen.

    The in-game catalogue array only ever resolved 47 of 630 ids. This route does not
    need it: OCR gives the car NAME per lap time, memory gives the car_id per lap time,
    and the public roster at forza.net/fh6cars turns the name into make, model, year,
    country, category and the car's STOCK class. Stock class is a property of the car;
    the class a lap is scored in belongs to the board and is never overwritten here.
    """
    if not ID_NAMES.exists() or not ROSTER.exists():
        return {}
    # Keyed by (name, year): six cars share the name "Honda Civic Type R", and a
    # name-only lookup silently returned whichever the roster listed last.
    roster = {(car["name"], car["year"]): car
              for car in json.loads(ROSTER.read_text(encoding="utf-8"))}
    named = json.loads(ID_NAMES.read_text(encoding="utf-8")).get("by_car_id", {})
    meta: dict[int, dict] = {}
    for key, info in named.items():
        car = roster.get((info.get("name", ""), info.get("year")))
        if not car:
            continue
        try:
            car_id = int(key)
        except (TypeError, ValueError):
            continue
        # The year belongs in the displayed name: the roster holds several model years
        # of the same model, and two rows both reading "Honda Civic Si" with different
        # ids is not a list, it is a puzzle. The game writes it as "Dodge Viper '08",
        # so match that.
        display = car["name"]
        if car["year"]:
            display = f"{display} '{str(car['year'])[-2:]}"
        meta[car_id] = {
            "name": display,
            "roster_name": car["name"],
            "make": car["make"],
            "year": car["year"],
            "country": car["country"],
            "car_type": car["car_type"],
            "stock_class": car["stock_class"],
            "stock_pi": car["stock_pi"],
            "votes": info.get("votes"),
            "samples": info.get("samples"),
        }
    return drop_contested(meta)


def drop_contested(meta: dict[int, dict]) -> dict[int, dict]:
    """When several car_ids claim one car, keep only the best-supported claim.

    The join votes per car_id, so a handful of misread rows can hang a name on the wrong
    id. It shows up as two ids with the same name appearing on the SAME board, which the
    game does not do -- and the vote counts separate them cleanly: on 2026-08-23 the
    strong claim held 36 of 39, 56 of 244, 31 of 116 votes while its rival held 2 of 2,
    3 of 18, 3 of 3.

    The loser is left unnamed rather than renamed: we know the name is not its, not what
    is. A tie leaves everyone unnamed for the same reason. The votes themselves stay in
    the ids file, so a later join with more evidence can still settle it.
    """
    claims: dict[tuple, list[int]] = defaultdict(list)
    for car_id, entry in meta.items():
        claims[(entry.get("roster_name"), entry.get("year"))].append(car_id)
    for contenders in claims.values():
        if len(contenders) < 2:
            continue
        ranked = sorted(contenders, key=lambda cid: (meta[cid].get("votes") or 0),
                        reverse=True)
        best = meta[ranked[0]].get("votes") or 0
        runner_up = meta[ranked[1]].get("votes") or 0

        # Die urspruengliche Begruendung -- "zwei ids desselben Autos auf EINEM Board
        # macht das Spiel nicht" -- traegt nicht: am 2026-08-24 gemessen treten ALLE 37
        # umstrittenen Paare auf gemeinsamen Boards auf, keines getrennt. Und ein
        # zweiter Beleg widerlegt die Rangfolge: Mercedes-Benz AMG CLK GTR steht mit
        # id 568 bei 3 Stimmen und 548 Runden gegen id 2654 mit 2 Stimmen und 1617
        # Runden -- bei so wenigen Stimmen entscheidet Rauschen, wer den Namen bekommt
        # und wer als "Car #2654" im Dashboard endet.
        #
        # Ist der Zweite selbst kraeftig belegt, sind es zwei echte Spieleintraege, die
        # der Roster nur nicht unterscheidet: Forza-Edition- und WTAC-Varianten teilen
        # Name und Baujahr mit dem Serienauto, haben aber andere Werte und eigene ids.
        # Toyota Sprinter Trueno GT Apex Forza Edition steht 57 zu 52 Stimmen -- da ist
        # nichts umstritten, da sind es zwei Autos. Beide behalten den Namen und tragen
        # ihre id, damit zwei gleich benannte Zeilen unterscheidbar bleiben.
        # Gemessen: 7 von 37 Paaren, also 7 Autos, die bisher unbenannt blieben.
        #
        # Der duenn belegte Zweite bleibt unbenannt wie bisher: dass sein Name nicht
        # stimmt, ist belegt -- welcher stimmt, nicht. Das braucht einen Speicher-Lauf
        # zeitnah zu einem Bildschirm-Scan, nicht eine andere Anzeige.
        keep_both = runner_up >= 5 and runner_up >= 0.25 * best
        if keep_both:
            for car_id in ranked:
                if (meta[car_id].get("votes") or 0) >= 5 and                         (meta[car_id].get("votes") or 0) >= 0.25 * best:
                    meta[car_id]["name"] = f"{meta[car_id]['name']} (#{car_id})"
                    meta[car_id]["contested_kept"] = True
                else:
                    meta.pop(car_id, None)
            continue

        losers = ranked if best == runner_up else ranked[1:]
        for car_id in losers:
            meta.pop(car_id, None)
    return meta


def load_catalogue() -> dict[int, str]:
    """carId -> codename from the game's own car archives (media/Cars/<CODE>.zip, carclips_<id>)."""
    path = Path(GAME_IDS)
    if not path.exists():
        return {}
    try:
        codes = json.loads(path.read_text(encoding="utf-8")).get("codes") or {}
    except (OSError, ValueError):
        return {}
    merged: dict[int, str] = {}
    for key, value in codes.items():
        try:
            merged[int(key)] = str(value)
        except (TypeError, ValueError):
            continue
    return merged

def pretty_car(codename: str, car_id: int) -> str:
    """`TOY_SprinterTG_85` -> `Toyota Sprinter TG '85`."""
    if not codename:
        return f"Car #{car_id}"
    parts = codename.split("_")
    if len(parts) < 2:
        return codename
    make = MAKES.get(parts[0], parts[0].title())
    year = ""
    model_parts = parts[1:]
    if model_parts and re.fullmatch(r"\d{2,4}", model_parts[-1]):
        raw = model_parts[-1]
        model_parts = model_parts[:-1]
        year = f" '{raw[-2:]}"
    # Some catalogue entries carry a numeric index between make and model
    # (`LOT_00_ExigeWTA_18`), and printing it turns an Integra Type R into a
    # "Honda 33 Integra". Dropped only when a model segment survives it, so a car
    # whose name IS a number (`POR_911_92`) keeps it.
    if len(model_parts) > 1 and re.fullmatch(r"\d{1,3}", model_parts[0]):
        model_parts = model_parts[1:]
    model = " ".join(
        re.sub(r"(?<=[a-z])(?=[A-Z])|(?<=\d)(?=[A-Z])", " ", part)
        for part in model_parts
    )
    return f"{make} {model}{year}".strip()


def zeitpunkt(text) -> float:
    """Ein ISO-Zeitpunkt (mit beliebigem Versatz) als Sekunden seit 1970; unlesbar = 0.

    Ohne Versatz gilt die Ortszeit dieses Rechners -- so schreibt das alte Werkzeug.
    """
    try:
        t = datetime.fromisoformat(str(text or "").strip())
    except ValueError:
        return 0.0
    if t.tzinfo is None:
        t = t.astimezone()
    return t.timestamp()


def ortszeit(text) -> str:
    """Ein Zeitpunkt in der Ortszeit dieses Rechners -- fuer die Anzeige ("when"); unlesbar bleibt er, wie er ist."""
    t = zeitpunkt(text)
    return datetime.fromtimestamp(t).astimezone().isoformat() if t else str(text or "")


def canonical_tracks(names: set[str]) -> dict[str, str]:
    """Map OCR-truncated track names onto the longest name they prefix.

    The navigator reads the route name off the screen, so a board can be recorded as
    `Highway Circui`. Anything that is a prefix of a longer recorded name is it.
    """
    ordered = sorted(names, key=len, reverse=True)
    return {name: next((full for full in ordered
                        if full != name and full.startswith(name)), name)
            for name in names}


def drop_placeholder_times(entries: list[dict]) -> tuple[list[dict], int]:
    """Keep the longest run of ranks whose lap times never go backwards.

    At high scroll speed the game draws a row before its lap time has loaded and the
    capture takes a placeholder instead -- the same lag the gamertags show, in another
    column. It surfaces as hundreds of consecutive ranks sharing one time, sometimes
    faster than rank 1, which a board sorted by time cannot contain.

    Dropping repeated times would be wrong: on a deep board, ties at millisecond
    precision are ordinary. The memory scan of Shimanoyama D has 117 times shared by
    25+ entries and one shared by 266 real entries. What the placeholders violate is
    not repetition but the board's own ordering, so that is what this tests, keeping
    the longest non-decreasing subsequence so one bad high value cannot poison the
    rest.

    Measured 2026-08-23 over 9 memory boards (25,599-row Shimanoyama D included) and 4
    OCR boards: memory loses 1 row in 28,000, OCR loses 2.0-3.5%.
    """
    ordered = sorted(entries, key=lambda row: int(row["rank"]))
    laps = [float(row["lap_time_seconds"]) for row in ordered]
    tails: list[float] = []
    tails_at: list[int] = []
    previous = [-1] * len(laps)
    for index, lap in enumerate(laps):
        slot = bisect.bisect_right(tails, lap)
        if slot:
            previous[index] = tails_at[slot - 1]
        if slot == len(tails):
            tails.append(lap)
            tails_at.append(index)
        else:
            tails[slot] = lap
            tails_at[slot] = index
    keep: set[int] = set()
    index = tails_at[-1] if tails_at else -1
    while index >= 0:
        keep.add(index)
        index = previous[index]
    return [row for i, row in enumerate(ordered) if i in keep], len(laps) - len(keep)


def split_invalid(entries: list[dict]) -> tuple[list[dict], list[dict]]:
    """Separate invalid laps from valid ones.

    No heuristic is needed and an earlier one was wrong. Verified across all 40 sweep
    runs on 2026-08-22: a board lists every VALID lap sorted by time, then every
    INVALID lap sorted by time, starting over from a faster time than the valid
    block ends on. Not one board has a valid lap after the first invalid one, and
    `is_clean` accounts for 100% of the ordering violations -- 70,114 rows, 22.3% of
    everything scanned. Among valid laps alone the boards are monotone to within
    0.005% (12 rows out of 244,373, all 10 ms apart inside one resume pass).

    So the invalid block is data, not damage: it is kept and filterable, and the site
    excludes it by default because a lap that did not count must not decide which car
    is fastest.
    """
    valid, invalid = [], []
    for row in sorted(entries, key=lambda item: int(item["rank"])):
        (valid if row.get("is_clean") else invalid).append(row)
    return valid, invalid


def signature(row: dict) -> int:
    bits = 0
    for index, (_, source) in enumerate(FLAGS):
        if row.get(source):
            bits |= 1 << index
    return bits


def name_to_car_id(car_meta: dict[int, dict]) -> dict[tuple[str, int | None], int]:
    """(roster name, year) -> carId, so a screen-read board joins the memory boards.

    The OCR scanner has no car_id: the screen shows a name. Rows would otherwise be
    dropped for lack of an id and a whole night of scanning would be invisible. Where
    the name is one we have already tied to an id, the two sources merge into the same
    car; where it is not, the car still appears under its name with a synthetic id.
    """
    # Keyed on the ROSTER name, not the displayed one. The displayed name carries the
    # model year -- "Dodge Viper '08" -- while the lookup comes from a roster match and
    # holds the plain "Dodge Viper", so keying on the display name never matched and
    # every screen-read row minted a synthetic id even where the car_id was known. The
    # car then appeared twice and competed against itself in its own class.
    return {(meta.get("roster_name") or meta["name"], meta["year"]): car_id
            for car_id, meta in car_meta.items()}


def known_routes() -> dict[str, list[str]]:
    """Every route the catalogue lists per category, so the page can say what is OPEN.

    Without this the page can only list what has been read, and a reader cannot tell
    whether that is all of it. Road Racing has 23 routes; six are named in the verified
    list and the rest are still unconfirmed, which is itself worth showing.
    """
    path = Path("config/fh6_board_catalogue.json")
    if not path.exists():
        return {}
    try:
        catalogue = json.loads(path.read_text(encoding="utf-8"))
    except Exception:
        return {}
    out: dict[str, list[str]] = {}
    for category, groups in (catalogue.get("tracks") or {}).items():
        if not isinstance(groups, dict):
            continue
        names = []
        for key, entries in groups.items():
            # Nur Listen von Eintraegen ansehen. Der Katalog traegt inzwischen auch
            # Zahlen (wraps_after: 23) und Prosa (_comment) -- ein `for entry in 23`
            # hat den ganzen Seitenaufbau mit einem TypeError abgebrochen, und weil der
            # Sweep ihn als Unterprozess mit verworfener Ausgabe aufruft, sah man nur,
            # dass die Seite nicht mehr aktuell wurde.
            if key.startswith("_") or not isinstance(entries, list):
                continue
            for entry in entries:
                name = entry.get("name") if isinstance(entry, dict) else entry
                if not isinstance(name, str) or not name:
                    continue
                # Kommentarzeilen sind Saetze, keine Streckennamen.
                if len(name) > 40 or name.endswith((".", ":", ",")):
                    continue
                if name not in names:
                    names.append(name)
        out[category] = names
    return out


def distribution(entries: list[dict], bins: int = 48) -> dict | None:
    """A binned histogram of every valid lap on a board, plus percentile cut-offs.

    The payload otherwise ships only the five fastest laps per car, which is all the
    ranking rules need but cannot answer "where does this time sit in the field". That
    question wants the whole distribution, and a board has thousands of laps -- so the
    shape is computed here, where every row is in hand, and shipped as counts per bin.

    Percentiles are on RANK order, i.e. by time ascending, so p1 is the time you have to
    beat to be in the fastest 1% of the field.
    """
    laps = sorted(int(round(float(row["lap_time_seconds"]) * 1000))
                  for row in entries
                  if isinstance(row.get("lap_time_seconds"), (int, float)))
    if len(laps) < 20:
        return None
    low, high = laps[0], laps[-1]
    # A handful of implausibly slow laps would otherwise flatten the whole curve into the
    # first bin, so the axis stops at the 99th percentile and the tail is counted in.
    cap = laps[min(len(laps) - 1, int(len(laps) * 0.99))]
    if cap <= low:
        cap = high
    width = max(1, (cap - low) // bins)
    counts = [0] * (bins + 1)
    for lap in laps:
        counts[min(bins, (lap - low) // width)] += 1
    marks = {}
    for pct in (1, 5, 10, 25, 50, 75, 90):
        marks[str(pct)] = laps[min(len(laps) - 1, int(len(laps) * pct / 100))]
    return {"low": low, "width": width, "counts": counts, "n": len(laps),
            "slowest": high, "pct": marks}


WORKSPACE_ROOT = Path(__file__).resolve().parent.parent
# Ergebnisse, die jemand anderes beigesteuert hat. Sie liegen getrennt, damit ein
# Fremdbeitrag nie mit einem eigenen Lauf verwechselt wird und sich in einem Rutsch
# wieder entfernen laesst.
CONTRIB_ROOT = WORKSPACE_ROOT / "data/memory_scans/contrib"
# Welche Laeufe die Auswertung NICHT sehen soll. Ausblenden statt loeschen: ein Lauf,
# der heute fehlerhaft aussieht, ist morgen vielleicht der einzige Beleg dafuer, WAS
# schiefging.
VISIBILITY_FILE = WORKSPACE_ROOT / "config/dataset_visibility.json"


def hidden_run_ids(path: Path | None = None) -> dict[str, str]:
    """run_id -> Begruendung, fuer jeden ausgeblendeten Lauf."""
    path = path or VISIBILITY_FILE
    if not path.exists():
        return {}
    try:
        data = json.loads(path.read_text(encoding="utf-8-sig"))
    except Exception:
        return {}
    out = {}
    for entry in data.get("hidden", []):
        if isinstance(entry, str):
            out[entry] = ""
        elif isinstance(entry, dict) and entry.get("run_id"):
            out[str(entry["run_id"])] = str(entry.get("reason") or "")
    return out


def discover_runs(root: Path, extra_roots: list[Path],
                  hidden: dict[str, str]) -> tuple[list[tuple[Path, str]], int]:
    """Alle Laufverzeichnisse, jeweils mit ihrer Herkunft, ohne die ausgeblendeten.

    Die Herkunft ist der Name des Unterordners unter `contrib` -- also der Beitragende
    -- oder "local" fuer die eigenen Laeufe. Sie steht spaeter an jedem Scan, damit auf
    der Seite sichtbar ist, wessen Zahlen man gerade ansieht.
    """
    found: list[tuple[Path, str]] = []
    skipped = 0
    for state_path in sorted(root.glob("*/state.json")):
        if state_path.parent.name in hidden:
            skipped += 1
            continue
        found.append((state_path, "local"))
    for extra in extra_roots:
        if not extra.exists():
            continue
        # contrib/<beitragender>/<run_id>/state.json
        for state_path in sorted(extra.glob("*/*/state.json")):
            if state_path.parent.name in hidden:
                skipped += 1
                continue
            found.append((state_path, state_path.parent.parent.name))
    return found, skipped


def build(root: Path, *, drop_invalid: bool = False,
          contrib_roots: list[Path] | None = None,
          visibility: Path | None = None) -> dict:
    catalogue = load_catalogue()
    car_meta = load_car_meta()
    by_name = name_to_car_id(car_meta)
    try:
        import build_car_roster as roster_tools

        roster_cars = roster_tools.load_roster(ROSTER_HTML)
        roster_index = roster_tools.build_index(roster_cars)
        roster_keys = list(roster_index)
        roster_years = roster_tools.build_year_index(roster_cars)
    except Exception:
        roster_tools = None
        roster_index = roster_keys = roster_years = None

    synthetic: dict[str, int] = {}

    # Der Zuordner wird je ZEILE gerufen, aber es gibt nur rund 25.000 verschiedene
    # Bildschirmnamen bei 6,4 Millionen Zeilen -- jeder wurde also im Schnitt 250 mal
    # neu durch die unscharfe Suche geschickt. Gemessen 2026-09-11: 9.142 Mikrosekunden
    # je Zeile ohne Merker, 16,3 Stunden fuer den ganzen Bestand. Genau das machte den
    # Seitenaufbau von 76 Sekunden (2026-08) zu drei Stunden und mehr, und damit die
    # Website dauerhaft veraltet -- ein Namensfehler blieb einen halben Tag sichtbar.
    gemerkt: dict[str, int | None] = {}

    def resolve_car(row: dict) -> int | None:
        """The row's car_id, or one derived from the name a screen-read row carries."""
        car_id = row.get("car_id")
        if car_id is not None:
            return int(car_id)
        name = row.get("car_name")
        if not name or roster_tools is None:
            return None
        if name in gemerkt:
            return gemerkt[name]
        ergebnis = _resolve_car_name(name)
        gemerkt[name] = ergebnis
        return ergebnis

    # Vorab gesucht (namen_zuordnen, parallel und gemerkt), gefuellt unten, sobald
    # alle Boards gelesen sind. Was darin fehlt, sucht die Zeile wie frueher selbst.
    vorab: dict[str, int] = {}

    def _resolve_car_name(name: str) -> int | None:
        stelle = vorab.get(name, -2)
        if stelle == -2:
            name = roster_tools.clean_ocr_name(name)
            if not name:
                return None
            car, _score = roster_tools.match_car(name, roster_index, roster_keys, roster_years)
        else:
            car = roster_cars[stelle] if stelle >= 0 else None
        if car is None:
            # Every car in the game is in the roster, so a name that still does not match
            # after cleaning is a misreading, not a new car. Dropping the row keeps the
            # car list honest; inventing an entry would corrupt every ranking it enters.
            return None
        if car is not None:
            known = by_name.get((car["name"], car["year"]))
            if known is not None:
                return known
        label = f"{car['name']}|{car['year']}"
        # Negative ids for cars the memory scans have never numbered: distinct, stable
        # within a build, and obviously not a real game id. The roster entry rides along
        # so these cars are named and filterable like any other.
        if label not in synthetic:
            new_id = -(len(synthetic) + 1)
            synthetic[label] = new_id
            if car is not None:
                display = car["name"]
                if car["year"]:
                    display = f"{display} '{str(car['year'])[-2:]}"
                car_meta[new_id] = {
                    "name": display, "roster_name": car["name"], "make": car["make"],
                    "year": car["year"], "country": car["country"],
                    "car_type": car["car_type"], "stock_class": car["stock_class"],
                    "stock_pi": car["stock_pi"], "votes": None, "samples": None,
                }
            else:
                car_meta[new_id] = {
                    "name": name.strip()[:48], "roster_name": None, "make": None,
                    "year": None, "country": None, "car_type": None,
                    "stock_class": None, "stock_pi": None,
                    "votes": None, "samples": None,
                }
        return synthetic[label]

    hidden = hidden_run_ids(visibility)
    roots = contrib_roots if contrib_roots is not None else [CONTRIB_ROOT]
    run_paths, hidden_count = discover_runs(root, roots, hidden)

    staged = []
    track_names: set[str] = set()
    origins: dict[int, str] = {}
    for state_path, origin in run_paths:
        state = json.loads(state_path.read_text(encoding="utf-8-sig"))
        parquet = state_path.parent / "leaderboard_entries.parquet"
        if not parquet.exists():
            continue
        track = (state.get("track") or "").strip()
        track_names.add(track)
        origins[id(state)] = origin
        staged.append((state, parquet, track))
    fix = canonical_tracks(track_names)

    # Provenance, so the page can say what has been read and when rather than leaving it
    # to be guessed from the numbers. A board is usually the merge of several runs, and
    # which route read it matters: a memory run carries the game's own car ids, a screen
    # run carries names and stops short of the deepest ranks.
    scans = []
    for state, parquet, track in staged:
        stamp = state.get("completed_at") or state.get("updated_at") or ""
        scans.append({
            "t": fix.get(track, track),
            "k": (state.get("performance_class") or "?").upper(),
            "src": "ocr" if (state.get("scanner") == "ocr"
                             or str(state.get("run_id", "")).startswith("ocr_")) else "mem",
            "rows": int(state.get("rows_collected") or 0),
            "maxRank": int(state.get("maximum_rank") or 0),
            "status": str(state.get("status") or "?"),
            "when": ortszeit(stamp)[:19],
            # "local" oder der Name des Beitragenden. Ohne diese Angabe waere auf der
            # Seite nicht zu sehen, wessen Zahlen ein Board tragen.
            "by": origins.get(id(state), "local"),
        })
    scans.sort(key=lambda entry: entry["when"], reverse=True)

    # Geschaetzte Gesamtlaenge je Board, aus den Scrollbalken-Messungen der Scans.
    # Sie ist die einzige Zahl, die sagt wie GROSS ein Board ist -- gescannt werden nur
    # die ersten 20.000 Raenge, und ein Board mit 787.000 Eintraegen sieht danach genauso
    # aus wie eines mit 21.000. Fuer "welche Strecke und Klasse ist am umkaempftesten"
    # ist das die tragende Groesse.
    #
    # Mehrere Laeufe je Board messen unterschiedlich gut, also gewinnt der beste: das
    # Steigungsverfahren ueber viele Messpunkte schlaegt eine Einzelmessung, und bei
    # gleichem Verfahren zaehlt die Zahl der Punkte. Warum das noetig ist: die Steigung
    # hebt die Mindesthoehe des Balkens heraus, eine Einzelmessung nicht -- und ein
    # "festgenagelter" Balken ist gar keine Messung. Zwei unabhaengige Laeufe auf Hokubu
    # S1 ergaben mit dem Steigungsverfahren 413.491 und 413.392, also 0,02% auseinander.
    LENGTH_QUALITY = {"slope": 3, "single_point": 2, "thumb_pinned": 1}
    sizes: dict[tuple, tuple[tuple, int, str]] = {}
    # EIN APP-SCAN BESTIMMT DIE GROESSE NUR, WO SONST KEINE MESSUNG IST: Verfahren und Zahl der
    # Messpunkte schreibt die App selbst hinein, und eine Installation meldet sich ohne Passwort
    # an -- "slope" mit 5000 Punkten gewaenne sonst immer.
    app_sizes: dict[tuple, tuple[tuple, int, str]] = {}
    for state, parquet, track in staged:
        implied = state.get("implied_total")
        if not implied:
            continue
        key = ((state.get("rivals_mode") or "Unknown"),
               fix.get(track, track),
               (state.get("performance_class") or "?").upper())
        estimator = str(state.get("length_estimator") or "")
        score = (LENGTH_QUALITY.get(estimator, 0), len(state.get("thumb_points") or []))
        ziel = app_sizes if (state.get("source") == "app"
                             or str(origins.get(id(state), "")).startswith("app-")) else sizes
        if key not in ziel or score > ziel[key][0]:
            ziel[key] = (score, int(implied), estimator)
    for key, wert in app_sizes.items():
        sizes.setdefault(key, wert)

    # Der neueste Lauf je Board: seine Zeilen sind "current", alle anderen Geschichte.
    # ALS ZEITPUNKT, nicht als Text: lokale Laeufe tragen "+02:00", ein App-Scan vom Server
    # (Container in UTC) "+00:00" -- als Text verlor ein App-Scan von 10:00 MESZ
    # ("08:00+00:00") gegen einen lokalen Lauf von 09:30 ("09:30+02:00").
    def lauf_stand(state: dict) -> float:
        return zeitpunkt(state.get("completed_at") or state.get("updated_at"))

    neuester: dict[tuple, float] = {}
    for state, parquet, track in staged:
        key = (state.get("rivals_mode") or "Unknown", fix.get(track, track),
               (state.get("performance_class") or "?").upper())
        if key not in neuester or lauf_stand(state) > neuester[key]:
            neuester[key] = lauf_stand(state)

    # JE BOARD DIE VEREINIGUNG ALLER LAEUFE, nicht der neueste Stand je Rang (so war es
    # bis 2026-10-03: ein Rang, eine Zeile, der zweite Lauf verlor). Dieselbe Runde in
    # zwei Laeufen -- gleicher Rang, gleiche Zeit -- bleibt eine; was spaeter dasselbe Auto
    # mit derselben Zeit ist, faellt unten nach der Autozuordnung zusammen.
    merged: dict[tuple, list[dict]] = defaultdict(list)
    gesehen: dict[tuple, dict[tuple[int, int], dict]] = defaultdict(dict)
    for state, parquet, track in staged:
        key = (state.get("rivals_mode") or "Unknown",
               fix.get(track, track),
               (state.get("performance_class") or "?").upper())
        aktuell = lauf_stand(state) == neuester.get(key)
        # Different scanners write different column sets -- the stream scanner's rows
        # have no `car_codename`, and asking for it aborts the whole build. Read the
        # intersection and treat the rest as absent.
        available = set(pq.read_schema(parquet).names)
        wanted = [name for name in COLUMNS if name in available]
        missing = [name for name in COLUMNS if name not in available]
        for row in pq.read_table(parquet, columns=wanted).to_pylist():
            for name in missing:
                row.setdefault(name, None)
            rank, lap = row.get("rank"), row.get("lap_time_seconds")
            if not isinstance(rank, int) or rank < 1:
                continue
            if not isinstance(lap, (int, float)) or not 1.0 <= lap <= 86400.0:
                continue
            row["_current"] = aktuell
            ms = round(float(lap) * 1000)
            kept = gesehen[key].get((rank, ms))
            if kept is None:
                gesehen[key][(rank, ms)] = row
                merged[key].append(row)
                continue
            # Derselbe Rang mit derselben Zeit aus einem zweiten Lauf ist dieselbe Runde:
            # was der eine Lauf mehr weiss (PI, Auto-Kennung, "aktuell"), bekommt sie.
            if lap_pi(kept.get("pi"), key[2]) is None and lap_pi(row.get("pi"), key[2]) is not None:
                kept["pi"] = row["pi"]
            if kept.get("car_id") is None and row.get("car_id") is not None:
                kept["car_id"] = row["car_id"]
            if aktuell:
                kept["_current"] = True

    # ALLE BILDSCHIRMNAMEN AUF EINMAL SUCHEN, bevor die Boards einzeln durchgehen --
    # parallel und aus dem Merker (siehe namen_zuordnen). Das Ergebnis ist dasselbe wie
    # die Suche Zeile fuer Zeile; nur die Reihenfolge der Arbeit ist eine andere.
    if roster_tools is not None:
        offen = sorted({str(row["car_name"]) for rows in merged.values() for row in rows
                        if row.get("car_id") is None and row.get("car_name")})
        vorab.update(namen_zuordnen(offen, ROSTER_HTML))

    categories: list[str] = []
    tracks: list[str] = []
    classes: list[str] = []
    car_ids: list[int] = []
    car_slot: dict[int, int] = {}
    codenames: dict[int, str] = {}

    def slot(bucket: list, value: str) -> int:
        if value not in bucket:
            bucket.append(value)
        return bucket.index(value)

    boards = []
    total_raw = total_invalid = total_kept = total_placeholder = total_pi = 0
    for (category, track, klass), rows in sorted(merged.items()):
        valid, invalid = split_invalid(rows)
        valid, dropped = drop_placeholder_times(valid)
        total_placeholder += dropped
        entries = valid if drop_invalid else valid + invalid
        total_raw += len(rows)
        total_invalid += len(invalid)

        groups: dict[tuple[int, int], list[dict]] = defaultdict(list)
        # DIESELBE RUNDE AUS SPEICHER- UND BILDSCHIRMLAUF: dort eine Auto-Kennung, hier ein
        # Name -- nach der Zuordnung dasselbe Auto mit derselben Zeit. Eine Zeile je
        # (Auto, Zeit), die aktuelle vor der alten, die mit PI vor der ohne.
        eine_je_runde: dict[tuple[int, int], dict] = {}
        for row in entries:
            car_id = resolve_car(row)
            if car_id is None:
                continue
            schluessel = (car_id, int(round(float(row["lap_time_seconds"]) * 1000)))
            da = eine_je_runde.get(schluessel)
            if da is None:
                eine_je_runde[schluessel] = row
                continue
            besser = (bool(row.get("_current")), lap_pi(row.get("pi"), klass) is not None)
            bisher = (bool(da.get("_current")), lap_pi(da.get("pi"), klass) is not None)
            if besser > bisher:
                eine_je_runde[schluessel] = row
            elif besser == bisher and da.get("car_codename") is None and row.get("car_codename"):
                da["car_codename"] = row["car_codename"]
        for (car_id, _ms), row in eine_je_runde.items():
            if car_id not in car_slot:
                car_slot[car_id] = len(car_ids)
                car_ids.append(car_id)
            code = row.get("car_codename")
            if code and car_id not in codenames:
                codenames[car_id] = code
            groups[(car_slot[car_id], signature(row))].append(row)

        gcar, gsig, gcount = [], [], []
        lgrp, lms, lrank, lpi = [], [], [], []
        for (car_index, sig), laps in sorted(groups.items()):
            laps.sort(key=lambda row: row["lap_time_seconds"])
            group_index = len(gcar)
            gcar.append(car_index)
            gsig.append(sig)
            gcount.append(len(laps))
            for row in laps[:LAPS_PER_GROUP]:
                lgrp.append(group_index)
                lms.append(int(round(float(row["lap_time_seconds"]) * 1000)))
                lrank.append(int(row["rank"]))
                lpi.append(lap_pi(row.get("pi"), klass))
        total_kept += len(lgrp)
        with_pi = sum(1 for value in lpi if value is not None)
        total_pi += with_pi

        boards.append({
            "c": slot(categories, category),
            "t": slot(tracks, track),
            "k": slot(classes, klass),
            "rows": len(entries),
            "valid": len(valid),
            "invalid": len(invalid),
            "maxRank": max((int(row["rank"]) for row in valid), default=0),
            # Geschaetzte Gesamtlaenge und wie sie zustande kam, damit die Seite eine
            # Schaetzung nicht wie eine Zaehlung aussehen laesst.
            "impl": (sizes.get((category, track, klass)) or (None, 0, ""))[1],
            "iest": (sizes.get((category, track, klass)) or (None, 0, ""))[2],
            "gcar": gcar, "gsig": gsig, "gcount": gcount,
            "lgrp": lgrp, "lms": lms, "lrank": lrank,
            "dist": distribution(valid),
        })
        # Nur wenn es etwas zu zeigen gibt: bei 1,46 Mio. Runden waere eine Spalte
        # aus lauter null gut 7 MB mehr fuer die Seite, ohne eine einzige Angabe.
        if with_pi:
            boards[-1]["lpi"] = lpi

    # A name read off the screen and matched to the public roster beats a codename
    # guessed from a partial memory dump, so it wins where both exist.
    car_names = []
    for car_id in car_ids:
        meta = car_meta.get(car_id)
        if meta:
            car_names.append(meta["name"])
        else:
            car_names.append(pretty_car(catalogue.get(car_id)
                                        or codenames.get(car_id, ""), car_id))
    named = sum(1 for name in car_names if not name.startswith("Car #"))
    meta_rows = [car_meta.get(car_id) for car_id in car_ids]

    return {
        "categories": categories,
        "tracks": tracks,
        "classes": classes,
        "carNames": car_names,
        "carIds": car_ids,
        # Per car, in the same order as carNames: make, year, country, category and the
        # car's STOCK class. The site derives "tune" from stock class against the
        # board's class; it never replaces the board's class.
        "carMeta": [None if meta is None or not meta.get("make") else {
            "make": meta["make"], "year": meta["year"], "country": meta["country"],
            "type": meta["car_type"], "stockClass": meta["stock_class"],
            "stockPi": meta["stock_pi"],
        } for meta in meta_rows],
        "flags": [name for name, _ in FLAGS],
        "boards": boards,
        "meta": {
            "raw_rows": total_raw,
            "kept_laps": total_kept,
            "laps_with_pi": total_pi,
            "invalid_rows": total_invalid,
            "scans": scans,
            "known_routes": known_routes(),
            "placeholder_rows_dropped": total_placeholder,
            "invalid_dropped": drop_invalid,
            "hidden_runs": hidden_count,
            "contributors": sorted({o for o in origins.values() if o != "local"}),
            "cars": len(car_ids),
            "named_cars": named,
            "roster_matched": sum(1 for meta in meta_rows if meta),
            "catalogue_ids": len(catalogue),
            "laps_per_group": LAPS_PER_GROUP,
        },
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path,
                        default=Path("data/memory_scans/full_sweep"))
    parser.add_argument("--out", type=Path, default=Path("data/analytics/laps.json"))
    parser.add_argument("--drop-invalid", action="store_true",
                        help="leave invalid laps out of the payload entirely instead "
                             "of shipping them behind the filter")
    parser.add_argument("--quiet", action="store_true")
    parser.add_argument("--contrib-root", type=Path, action="append",
                        help="Wurzel mit Fremdbeitraegen (contrib/<wer>/<lauf>); "
                             "mehrfach angebbar. Voreinstellung: "
                             "data/memory_scans/contrib")
    parser.add_argument("--visibility", type=Path,
                        help="JSON mit ausgeblendeten Laeufen; Voreinstellung "
                             "config/dataset_visibility.json")
    args = parser.parse_args(argv)

    payload = build(args.root, drop_invalid=args.drop_invalid,
                    contrib_roots=args.contrib_root, visibility=args.visibility)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(payload, separators=(",", ":")), encoding="utf-8")

    meta = payload["meta"]
    print(f"{len(payload['boards'])} boards, {meta['raw_rows']} rows read, "
          f"{meta['kept_laps']} laps kept, {meta['invalid_rows']} invalid "
          f"({'dropped' if meta['invalid_dropped'] else 'kept behind the filter'}), "
          f"{meta['named_cars']}/{meta['cars']} cars named, "
          f"{args.out.stat().st_size / 1024:.0f} KB -> {args.out}")
    if args.quiet:
        return 0
    for board in payload["boards"]:
        print(f"  {payload['categories'][board['c']]:<12} "
              f"{payload['tracks'][board['t']]:<22} {payload['classes'][board['k']]:<3} "
              f"{board['valid']:>6} valid {board['invalid']:>6} invalid  "
              f"{len(board['lgrp']):>6} laps")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
