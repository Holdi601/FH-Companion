"""Catalogue of this project's OCR failure modes, and the corrections for them.

Every fix here existed already, scattered across the codebase as a one-off with its
reasoning buried in a comment -- which meant rediscovering the same failure twice. This
is the single place that names each mode, keeps the evidence, and says what to do.

The modes, all measured against real captures on 2026-08-23/24:

1. TRAILING TRUNCATION -- the last character(s) of a name are dropped.
     'Highway Circuit' read 100x, 'Highway Circui' 63x; 'Seaside Park Sprint' 4x,
     'Seaside' 1x. Correction: prefix match against known names. The common case, and
     the only mode the codebase already handled.

2. CHARACTER SUBSTITUTION -- a glyph is read as a similar one.
     'Irokawa Circul' (t->l), 'Ciicuit'/'CiFcuit' (r->i/F), 'Se"ide Park Sprint'
     (as->"), 'Audi R8 VIO plus' (V10->VIO), 'i0MK' (0<->O).
     Correction: similarity match on a normalised key. A prefix test cannot see these,
     because the damage is in the middle.

3. INTERNAL LOSS / SPACE INSERTION -- a character becomes a space, or vanishes.
     'Dai oku Circuit' for 'Daikoku Circuit'. Correction: the normalised key strips
     spaces entirely before comparing.

4. SPURIOUS INSERTION -- extra glyphs appear inside a word.
     'Tokyo RailV$tay Sprints' for 'Tokyo Railway Sprints', read 4x that way against 1x
     clean. Worth noting: the CORRUPT reading outnumbered the clean one, so a majority
     vote across captures would have chosen the wrong name.

5. SEVERE GARBLING -- a low-contrast or overlapping title is simply unreadable.
     The 4.4 KM route was read five ways and no two agree: 'Electhe toyi?Ciicuit',
     'Electriei0MKCiFcuit', 'Electrie town Circuit', 'ElectheiowKCircuit',
     'ElecthefroW?Ciicuit'. Correction: none. Return the reading unchanged and let the
     caller treat the board as unnamed. Guessing here files rows under a fiction.

6. CLIPPED LEADING DIGIT -- the rank column is RIGHT-ALIGNED, so a longer number
     extends LEFT, and any mask whose left edge sits inside it cuts the leading digit.
     Two instances, the same bug at different layers:
       - a capture window starting at x=110 cut '4,287' to '287'  (fixed: capture x=78)
       - the KEEP_BANDS rank mask starting at x=100 cut '21,038' to '1,038'
         (fixed 2026-08-24: band 100 -> 78)
     The second survived the first fix for weeks because the pixels WERE captured and
     then masked away. Its signature: screen-route density 85-100% below rank 10,000 and
     1-4% above, while the memory route read 100% at every depth -- 25 of 39 boards
     affected. The clipped rows were not lost; they were renumbered into 1,000-9,999,
     collided with genuine rows already banked, and were dropped there as duplicates.
     Every row in a frame is clipped identically, so the consecutive-rank check CONFIRMS
     the wrong numbering and no in-frame validation can see it. Character pitch is ~16 px
     against a right edge of ~176, so: 6 characters need x<=80, 7 need x<=64. Ranks past
     99,999 therefore need the CAPTURE moved left of 78 as well -- five boards are that
     deep. Prevention only: a clipped reading cannot be repaired afterwards, which is why
     correct_rank() takes an external ceiling instead of trusting the grammar.

7. SEPARATOR READ AS A DIGIT -- the column rule '|' after a rank becomes a '1', so
     '981|' reads as '9811'. Correction: parse field by field rather than with one
     whitespace-anchored pattern (see RANK_RE in ocr_leaderboard_frames).

8. PLACEHOLDER LAP TIMES -- at speed the game draws a row before its time has loaded and
     the capture takes the placeholder, giving hundreds of consecutive ranks one time,
     sometimes faster than rank 1. Correction: keep the longest non-decreasing run by
     lap time. Never "drop repeated times" -- real ties are common, and one Shimanoyama
     D time is shared by 266 genuine entries.

9. MIS-NUMBERED FRAMES / PHANTOM MAXIMUM -- a frame's rank column misreads high and the
     chunk's maximum then claims a depth the view never reached: 'raw max 4,781' against
     a settled 2,302. Believing it once "proved" a 61,000-rank board whose real end was
     29,697. Correction: cap a chunk's advance (MAX_CHUNK_ADVANCE) and take position
     from a settled frame, never from a chunk's maximum.

10. GAMERTAG GLYPHS -- emoji and platform icons in the driver column produce stray
     glyphs that shift the whole line. Correction: mask the driver column out; nothing
     downstream needs it. This turned one usable line per frame into all eleven.

11. UI CHROME AS CONTENT -- 'Routes' and 'Details' are panel headings, and both were
     harvested as route names. Correction: reject readings that match known chrome.

Not an OCR fault, but the same silent-loss shape and it cost 42 runs, so it belongs
with them:

12. UTF-8 BOM -- PowerShell writes JSON with a BOM, so json.load(open(path)) raises and
     a bare `except: continue` skips the file without a word. Correction:
     encoding='utf-8-sig' when reading anything PowerShell may have written.
"""

from __future__ import annotations

import difflib
import json
import re
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent

# Panel headings that have been harvested as route names (mode 11).
UI_CHROME = {"routes", "details", "rivals", "gap to rival", "select", "back",
             "route length", "filter", "global", "change rival"}

# Route type is the one thing similarity must never blur: Shimanoyama has BOTH a
# Circuit (1.1 KM) and a Sprint (6.5 KM), and they are different boards.
TYPE_WORDS = ("circuit", "sprint", "colossus", "goliath")

# Above this, two normalised names are the same route. 0.82 accepts 'Irokawa Circul' and
# 'Tokyo RailV$tay Sprints' while rejecting the five mutually-inconsistent readings of
# the 4.4 KM route -- which is exactly the wanted behaviour: repair what is repairable,
# and leave a genuinely unreadable title alone rather than inventing a name for it.
SIMILARITY = 0.82


def norm(text: str) -> str:
    """Comparison key: lowercase, letters and digits only (mode 3 strips spaces)."""
    return re.sub(r"[^a-z0-9]", "", (text or "").lower())


def route_type(text: str) -> str | None:
    key = norm(text)
    for word in TYPE_WORDS:
        if word in key:
            return word
    return None


def is_ui_chrome(text: str) -> bool:
    return (text or "").strip().lower() in UI_CHROME


def known_route_names() -> list[str]:
    """Every route name the catalogue holds, verified or harvested from captures."""
    path = WORKSPACE / "config/fh6_board_catalogue.json"
    names: set[str] = set()
    if not path.exists():
        return []
    try:
        cat = json.loads(path.read_text(encoding="utf-8-sig"))
    except Exception:
        return []
    for key, value in (cat.get("tracks") or {}).items():
        if key == "harvested_from_captures":
            for route in (value.get("routes") or []):
                if route.get("name"):
                    names.add(route["name"])
            continue
        for entry in (value.get("verified") or []):
            if entry.get("name"):
                names.add(entry["name"])

    # AUCH DAS KARUSSELL -- sonst kennt der Korrektor Namen nicht, auf die der
    # Navigator landen kann.
    #
    # Der Katalog fuehrt Streckennamen an ZWEI Stellen: unter `tracks.*.verified`
    # und unter `route_carousel_by_category`. Der Sweep navigiert nach dem
    # Karussell, korrigierte den gelesenen Namen aber nur gegen `verified`. Weichen
    # die beiden ab, entsteht ein besonders unangenehmer Fehler:
    #
    # Am 2026-09-13 las der Navigator auf Position 8 sauber 'qangan Cross-Country
    # Circui'. Nangan stand nur im Karussell, also fand der Korrektor es nicht --
    # und "reparierte" es zum aehnlichsten BEKANNTEN Namen: 'Oka Cross-Country
    # Circuit'. Der Sweep sah daraufhin einen Namen von Position 4, hielt die
    # Navigation fuer nicht weitergeschaltet und verwarf ein vollstaendig richtig
    # angefahrenes Board.
    #
    # Es passiert nur SPORADISCH, und das macht es teuer: eine stark beschaedigte
    # Lesung gilt als unlesbar und faellt sauber auf den Katalognamen zurueck. Nur
    # eine mittelmaessig beschaedigte wird zu etwas Falschem "korrigiert".
    for eintraege in (cat.get("route_carousel_by_category") or {}).values():
        if not isinstance(eintraege, list):
            continue
        for entry in eintraege:
            name = entry.get("name") if isinstance(entry, dict) else entry
            if isinstance(name, str) and name.strip():
                names.add(name.strip())
    return sorted(names, key=len, reverse=True)


def correct_route_name(read: str, known: list[str] | None = None) -> tuple[str, str]:
    """Repair a route name if it is repairable. Returns (name, how).

    `how` is one of exact / prefix / similar / chrome / unreadable, so a caller can
    refuse to bank a board whose name was only guessed at -- which matters because
    mode 5 is unrepairable and must not be papered over.
    """
    read = (read or "").strip()
    if not read:
        return read, "unreadable"
    if is_ui_chrome(read):
        return read, "chrome"
    names = known if known is not None else known_route_names()
    key = norm(read)
    for name in names:
        if key == norm(name):
            return name, "exact"
    # Mode 1: truncation, in either direction.
    for name in names:
        nk = norm(name)
        if nk and (nk.startswith(key) or key.startswith(nk)):
            return name, "prefix"
    # Modes 2 and 4: damage inside the word. The route type must still agree.
    want = route_type(read)
    best, score = None, 0.0
    for name in names:
        if want and route_type(name) != want:
            continue
        ratio = difflib.SequenceMatcher(None, key, norm(name)).ratio()
        if ratio > score:
            best, score = name, ratio
    if best and score >= SIMILARITY:
        return best, "similar"
    return read, "unreadable"


# --------------------------------------------------------------------------------
# Per-field correction. A correction is only safe where the field's own grammar
# constrains it, so each column gets its own rule and nothing gets a generic fuzzy
# match it cannot support.
#
# Measured over 213,492 scanned rows on 2026-08-24:
#   drivetrain  3 distinct values, all valid    -> needs NO correction
#   gearbox     3 distinct values, all valid    -> needs NO correction
#   lap time    parses cleanly via field-by-field extraction
#   car         5,428 distinct readings for 636 cars -> the one column that needs work,
#               and mostly NOT from OCR damage (see correct_car_name).
# The closed-set fields are listed anyway: they are cheap to validate, and a value
# outside the set is then a signal that the column map has drifted.
# --------------------------------------------------------------------------------

DRIVETRAINS = {"AWD", "RWD", "FWD"}
GEARBOXES = {"A", "M", "MC"}

# Glyphs a digit is read as, for NUMERIC fields only. Applying this map to a text field
# would corrupt real letters, which is why it is scoped to rank and lap time.
DIGIT_LOOKALIKES = str.maketrans({
    "O": "0", "o": "0", "Q": "0", "D": "0",
    "I": "1", "l": "1", "i": "1", "!": "1",
    "Z": "2", "z": "2",
    "S": "5", "s": "5",
    "G": "6", "b": "6",
    "T": "7",
    "B": "8",
    "g": "9", "q": "9",
})

# M:SS.mmm, or SS.mmm on a sub-minute lap. Anchored, so a stray glyph fails the match
# rather than being absorbed into a plausible-looking time.
LAP_RE = re.compile(r"^(?:(\d{1,2}):)?([0-5]?\d)\.(\d{3})$")


def correct_closed_set(read: str, allowed: set[str]) -> tuple[str | None, str]:
    """Snap a reading onto a small fixed set, or reject it.

    With three members there is no room for a near miss to be ambiguous, so a single
    edit is safe to repair. Anything further out is refused: a value outside the set
    usually means the column map has drifted, and quietly snapping it would hide that.
    """
    read = (read or "").strip().upper()
    if read in allowed:
        return read, "exact"
    best, score = None, 0.0
    for value in allowed:
        ratio = difflib.SequenceMatcher(None, read, value).ratio()
        if ratio > score:
            best, score = value, ratio
    if best and score >= 0.66:
        return best, "snapped"
    return None, "rejected"


def correct_lap_time(read: str) -> tuple[float | None, str]:
    """Parse a lap time under its own strict grammar; repair digit look-alikes only.

    The column can hold nothing but digits, one colon and one dot in a fixed shape, so
    unlike a name this field can be VALIDATED rather than guessed at. A reading that
    still fails the pattern after the digit map is refused -- there is no partial credit
    for a time, because a wrong one silently reorders the board.
    """
    text = (read or "").strip().replace(",", ".").replace(" ", "")
    for candidate in (text, text.translate(DIGIT_LOOKALIKES)):
        m = LAP_RE.match(candidate)
        if m:
            minutes = int(m.group(1) or 0)
            seconds = int(m.group(2))
            millis = int(m.group(3))
            total = minutes * 60 + seconds + millis / 1000.0
            # A lap under 10 s or over 20 min is not a lap on these routes.
            if 10.0 <= total <= 1200.0:
                return round(total, 3), ("exact" if candidate == text else "repaired")
    return None, "rejected"


def correct_rank(read: str, ceiling: int | None = None) -> tuple[int | None, str]:
    """Parse a rank. Digits and thousands separators only.

    `ceiling` exists because of failure mode 6: a clipped leading digit produces a
    perfectly well-formed number, so grammar alone cannot catch it. Only a bound from
    outside the frame can -- which is why the caller passes the scan's own position.
    """
    text = (read or "").strip()
    # Mode 7: the column rule comes back as ')', ']' or '|' and a trailing one was being
    # read as a digit -- '981|' became 9811. Strip separators from the ends BEFORE any
    # digit repair, never translate them.
    text = text.strip(")]|[(")
    text = text.replace(",", "").replace(".", "").replace(" ", "")
    for candidate in (text, text.translate(DIGIT_LOOKALIKES)):
        if candidate.isdigit() and 1 <= len(candidate) <= 7:
            value = int(candidate)
            if value < 1:
                return None, "rejected"
            if ceiling is not None and value > ceiling:
                return None, "above_ceiling"
            return value, ("exact" if candidate == text else "repaired")
    return None, "rejected"


# --------------------------------------------------------------------------------
# NICHT BENUTZEN -- build_car_roster.match_car() ist besser.
#
# Gemessen am 2026-08-24 auf denselben 300 haeufigsten Screen-Lesungen (189.510 Zeilen):
#
#     build_car_roster.match_car()   95,7 % der Zeilen aufgeloest
#     correct_car_name() hier        89,3 %
#     nur der bestehende loest:      40 Lesungen
#     nur dieser hier:                5
#
# Bei 9 Uneinigkeiten hatte der bestehende jedes Mal recht, teils deutlich:
#   'Ferrari FXX-K WP' -> "FXX-K Evo Welcome Pack" (WP=Welcome Pack), hier nur "FXX K"
#   "Nissan GT-R '12"  -> "GT-R Black Edition (R35)", hier faelschlich
#                         "#12 Skyline GT-R (BNR32) JTC" -- die '12 als Startnummer gelesen
#
# Und die Basis/Variante-Trennung, um die es hier viel Muehe gab, leistet der bestehende
# Matcher ebenfalls fehlerfrei (7 von 7 Paaren korrekt: 'S-Cargo FE' vs 'S-Cargo' usw.).
#
# Das Folgende bleibt nur als Dokumentation der Screen-Konvention stehen, die weiter
# gilt und in build_car_roster nicht so ausfuehrlich beschrieben ist. Wer eine
# Autonamen-Aufloesung braucht: build_car_roster.match_car() nehmen.
# --------------------------------------------------------------------------------
# CAR CATALOGUE (siehe Warnung oben). The screen does not print the roster's name -- it prints its own
# abbreviation of it, so most "mismatches" are convention, not OCR damage. Measured
# 2026-08-24 over 213,492 rows: 11,449 distinct readings for 636 cars, of which only
# 3.2% matched a roster model verbatim. The five systematic differences:
#
#   "Honda Beat '91"      make + model + 'YY year suffix
#   "Alfa SE 048SP"       make abbreviated  (Alfa Romeo -> Alfa)
#   "P. 911 GT3 RS 23"    make reduced to an initial, year without an apostrophe
#   "Huracan STO"         accents folded    (Huracan <- Huracán)
#   "55 Mazda 787B"       leading '#' of a race number dropped
#   "Autozam AZ-1'93"     no space before the year
#   "Toyota Sera ?91"     apostrophe read as a garbage glyph
#
# So: parse the reading into (tokens, year), then match on the MODEL with the year
# binding where present. Fuzzy-matching the whole string would file cars under the
# wrong entry -- 'Cayman WTAC' is 0.87 similar to 'Cayman GT3 WTAC' but they are two
# different cars in the roster.
# --------------------------------------------------------------------------------

# The screen's year suffix, in every shape it has been read: "'91", " 91", "91'", "?91"
# (apostrophe as a garbage glyph), and glued straight onto the model as in "AZ-1'93".
YEAR_SUFFIX = re.compile(r"[\s'\"`´?‘’�]*(\d{2})\s*['\"`´‘’]?\s*$")

ACCENT_FOLD = str.maketrans({
    "á": "a", "à": "a", "ä": "a", "â": "a", "ã": "a", "å": "a",
    "é": "e", "è": "e", "ë": "e", "ê": "e",
    "í": "i", "ì": "i", "ï": "i", "î": "i",
    "ó": "o", "ò": "o", "ö": "o", "ô": "o", "õ": "o",
    "ú": "u", "ù": "u", "ü": "u", "û": "u",
    "ñ": "n", "ç": "c", "ß": "ss",
})


def fold(text: str) -> str:
    """Lowercase, fold accents, keep letters and digits."""
    return re.sub(r"[^a-z0-9]", "", (text or "").lower().translate(ACCENT_FOLD))


def split_car_reading(read: str) -> tuple[str, int | None]:
    """Split a screen reading into (name part, 4-digit year or None).

    Two digits are ambiguous on their own -- '23' is 2023 but '91' is 1991 -- so the
    century is chosen by the roster's own range: nothing newer than next year exists,
    so anything above that is last century.
    """
    text = (read or "").strip()
    m = YEAR_SUFFIX.search(text)
    if not m:
        return text, None
    two = int(m.group(1))
    year = 2000 + two if two <= 26 else 1900 + two
    return text[:m.start()].strip(), year


def _model_keys(car: dict) -> set[str]:
    """The keys a screen reading could plausibly present this car as."""
    make = car.get("make") or ""
    model = car.get("model") or ""
    name = car.get("name") or ""
    keys = {fold(model), fold(name), fold(f"{make} {model}")}
    # '#55 Mazda 787B' also appears without its '#', and makes get abbreviated to their
    # first word or an initial: 'Alfa Romeo' -> 'Alfa', 'Porsche' -> 'P.'
    keys.add(fold(name.lstrip("#")))
    keys.add(fold(model.lstrip("#")))
    if make:
        first = make.split()[0]
        keys.add(fold(f"{first} {model}"))
        keys.add(fold(f"{make[0]}. {model}"))
    return {k for k in keys if k}


_CAR_INDEX: dict | None = None


def car_index(path: str = "config/fh6_car_roster.json") -> dict:
    """key -> list of roster cars, built once."""
    global _CAR_INDEX
    if _CAR_INDEX is not None:
        return _CAR_INDEX
    idx: dict[str, list[dict]] = {}
    full = WORKSPACE / path
    if full.exists():
        try:
            roster = json.loads(full.read_text(encoding="utf-8-sig"))
        except Exception:
            roster = []
        for car in roster:
            if not isinstance(car, dict):
                continue
            for key in _model_keys(car):
                idx.setdefault(key, []).append(car)
    _CAR_INDEX = idx
    return idx


# Marken, die der Screen abkuerzt. Gemessen an den echten Lesungen 2026-08-24.
MAKE_ABBREV = {
    "am": "Aston Martin", "mit": "Mitsubishi", "lambo": "Lamborghini",
    "mb": "Mercedes-Benz", "p": "Porsche", "alfa": "Alfa Romeo",
    "vw": "Volkswagen", "chevy": "Chevrolet", "merc": "Mercedes-Benz",
}


# Varianten-Kuerzel, die der Screen anhaengt und der Katalog ausschreibt. Gemessen
# 2026-08-24: 'S-Cargo FE' gegen "Nissan S-Cargo Forza Edition", 'Mit. Minicab TA'
# gegen "...Minicab Time Attack". Kein Katalogauto traegt 'fe' oder 'ta' als Token,
# beide sahen deshalb wie fehlende Autos aus.
VARIANT_ABBREV = {"fe": ["forza", "edition"], "ta": ["time", "attack"]}


def car_tokens(text: str) -> list[str]:
    """Tokens einer Lesung, mit abgekuerzter Marke und Variante ausgeschrieben."""
    parts = [t for t in re.split(r"[^A-Za-z0-9]+", (text or "")) if t]
    if parts:
        head = parts[0].lower().rstrip(".")
        if head in MAKE_ABBREV:
            parts = MAKE_ABBREV[head].split() + parts[1:]
    out: list[str] = []
    for t in parts:
        expanded = VARIANT_ABBREV.get(t.lower())
        out.extend(expanded if expanded else [t])
    return [fold(t) for t in out if fold(t)]


def correct_car_name(read: str, index: dict | None = None) -> tuple[dict | None, str]:
    """Resolve a screen car reading to a roster car. Returns (car, how).

    `how`: exact / year_bound / ambiguous_year / similar / unmatched. A caller that
    needs certainty should accept only exact and year_bound -- `similar` is a single
    best guess above a high threshold and `ambiguous_year` means several roster cars
    share the model and the reading carried no year to separate them.
    """
    idx = index if index is not None else car_index()
    if not read or not idx:
        return None, "unmatched"
    # Die Jahresziffern sind nicht von Modellziffern zu unterscheiden -- 'Abarth 131'
    # ist ein Modell, 'Corvette C8 20' ein Jahr, und beide enden auf zwei Ziffern. Also
    # nicht raten: BEIDE Lesarten gegen den Katalog halten und die nehmen, die trifft.
    stripped, year = split_car_reading(read)
    readings = [(fold(read), None)]
    if year is not None and fold(stripped):
        readings.append((fold(stripped), year))
    hits, key, year_used = [], fold(read), None
    for cand_key, cand_year in readings:
        found = idx.get(cand_key) or []
        if found:
            hits, key, year_used = found, cand_key, cand_year
            break
    year = year_used if hits else year
    if len(hits) == 1:
        return hits[0], "exact"
    if len(hits) > 1:
        if year is not None:
            bound = [c for c in hits if c.get("year") == year]
            if len(bound) == 1:
                return bound[0], "year_bound"
        return hits[0], "ambiguous_year"
    # Weggelassene Woerter: 'Cayman WTAC' gegen 'Cayman GT3 WTAC'. Die Tokens der
    # Lesung muessen eine Teilmenge des Katalogmodells sein UND das erste Token teilen,
    # damit nicht zwei Autos derselben Marke verwechselt werden.
    # Beide Lesarten auch hier, aus demselben Grund wie oben: 'Lambo SCV12' verliert
    # sonst das '12' als angebliches Jahr und 'SCV' ist keine Teilmenge von
    # 'Essenza SCV12'. Die ungekuerzte Lesart zuerst -- Modellziffern sind haeufiger
    # als angehaengte Jahre.
    for want in ([car_tokens(read)] if year is None
                 else [car_tokens(read), car_tokens(stripped)]):
        if not want:
            continue
        subset = []
        for cars in idx.values():
            for car in cars:
                have = car_tokens(f"{car.get('make','')} {car.get('model','')}")
                if not have:
                    continue
                # KEIN Zwang mehr auf gleiches erstes Token: der Screen laesst die Marke
                # oft weg, 'Exige WTAC' beginnt mit "exige", der Eintrag mit "lotus".
                # Stattdessen: mindestens zwei Tokens, damit ein einzelnes generisches
                # Wort nicht das halbe Katalog trifft.
                if len(want) >= 2 and set(want) <= set(have):
                    subset.append(car)
        # Forza-Edition- und WTAC-Autos sind EIGENE Eintraege mit eigenen Stats, also
        # duerfen sie nicht mit dem Basisauto verwechselt werden. Der Teilmengen-Test
        # ist asymmetrisch -- eine kuerzere Lesung passt in einen laengeren Eintrag --,
        # also wuerde 'S-Cargo' auch auf "S-Cargo Forza Edition" passen und falsche
        # Stats zuweisen. Nur Kandidaten mit DENSELBEN Variantenmarkern zaehlen.
        MARKERS = {"forza", "edition", "wtac", "time", "attack"}
        want_marks = MARKERS & set(want)
        subset = [c for c in subset
                  if (MARKERS & set(car_tokens(f"{c.get('make','')} {c.get('model','')}")))
                  == want_marks]
        uniq = {id(c): c for c in subset}.values()
        if len(uniq) == 1:
            return next(iter(uniq)), "token_subset"
        if len(uniq) > 1 and year is not None:
            bound = [c for c in uniq if c.get("year") == year]
            if len(bound) == 1:
                return bound[0], "year_bound"

    # Nothing keyed. One guarded similarity pass -- but prefiltered, because comparing
    # every reading against every key is ~34M SequenceMatcher calls over this dataset.
    # A key that differs in its first two characters AND its length by more than three
    # is never the 0.90 match this accepts, so it can be skipped without loss.
    best, score = None, 0.0
    for cand_key, cars in idx.items():
        if abs(len(cand_key) - len(key)) > 3 and cand_key[:2] != key[:2]:
            continue
        ratio = difflib.SequenceMatcher(None, key, cand_key).ratio()
        if ratio <= score:
            continue
        pick = cars[0]
        if year is not None:
            same = [c for c in cars if c.get("year") == year]
            if same:
                pick = same[0]
        best, score = pick, ratio
    if best and score >= 0.90:
        return best, "similar"
    return None, "unmatched"
