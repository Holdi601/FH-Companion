"""Turn the public FH6 car list into a roster, and use it to name carIds.

Two problems solved by one join, and the second is the one that mattered all day:

1. **OCR spelling.** The screen gives "Ferrari Fi2tdf" where the car is an F12tdf, and
   "Cayman GT4 RS" where the roster says "2022 Porsche 718 Cayman GT4 RS". Matching each
   read name against a canonical roster fixes the spelling and adds manufacturer, year,
   country and category -- filters the analytics site did not have.
2. **carId -> name.** The memory scan knows `car_id` per RANK; the screen knows the car
   NAME per rank. Joined on rank, for the same board, that yields carId -> car. The
   catalogue dumped out of game memory only ever resolved 47 of 630 ids; this needs no
   catalogue at all, just one board read both ways.

    python scripts/build_car_roster.py --html data/car_catalogue/fh6cars.html \
        --ocr data/runtime/frames/RUN/rows.jsonl \
        --parquet data/memory_scans/full_sweep/BOARD/leaderboard_entries.parquet
"""

from __future__ import annotations

import argparse
import difflib
import json
import re
import unicodedata
from collections import Counter, defaultdict
from html.parser import HTMLParser
from pathlib import Path


class TableReader(HTMLParser):
    """The page is one static table; no API to call and no framework state to unpack."""

    def __init__(self) -> None:
        super().__init__()
        self.rows: list[list[str]] = []
        self._row: list[str] | None = None
        self._cell: list[str] | None = None

    def handle_starttag(self, tag, attrs):
        if tag == "tr":
            self._row = []
        elif tag in ("td", "th") and self._row is not None:
            self._cell = []

    def handle_endtag(self, tag):
        if tag in ("td", "th") and self._cell is not None and self._row is not None:
            self._row.append(re.sub(r"\s+", " ", "".join(self._cell)).strip())
            self._cell = None
        elif tag == "tr" and self._row is not None:
            if self._row:
                self.rows.append(self._row)
            self._row = None

    def handle_data(self, data):
        if self._cell is not None:
            self._cell.append(data)


YEAR_RE = re.compile(r"^(\d{4})\s+(.*)$")
CLASS_RE = re.compile(r"^(\d{2,3})\s*([A-Z][0-9]?)$")


def load_roster(path: Path) -> list[dict]:
    reader = TableReader()
    reader.feed(path.read_text(encoding="utf-8", errors="replace"))
    cars = []
    for row in reader.rows:
        if len(row) < 5 or row[0].lower() == "make":
            continue
        make, full, car_type, car_class, country = row[0], row[1], row[2], row[3], row[4]
        year = None
        name = full
        match = YEAR_RE.match(full)
        if match:
            year = int(match.group(1))
            name = match.group(2)
        model = name
        if model.lower().startswith(make.lower()):
            model = model[len(make):].strip()
        pi = None
        klass = None
        class_match = CLASS_RE.match(car_class)
        if class_match:
            pi = int(class_match.group(1))
            klass = class_match.group(2)
        cars.append({
            "make": make,
            "name": name,
            "model": model,
            "year": year,
            "car_type": car_type,
            "stock_pi": pi,
            "stock_class": klass,
            "country": country,
            "collection": row[5] if len(row) > 5 else "",
        })
    return cars


# The screen abbreviates makes and the OCR clips leading punctuation, so both sides get
# folded before matching. Learned from the misses on a real run: "lambo centenario",
# "cargo fe" (the "S-" of S-Cargo FE eaten with the separator), "jjm supra wtac".
# The screen abbreviates the make when the name is long: "Mit. Minicab TA",
# "Nis. #23 GT-R", "L. Aventador '21", "M-AMG GT WP", "AR Giulia GTAm".
APOSTROPHE_YEAR = re.compile(r"['`‘’]\s?\d{2}")

MAKE_ALIASES = {
    "mit": "mitsubishi", "nis": "nissan", "l": "lamborghini", "ar": "alfa romeo",
    "amg": "mercedes amg", "lan": "lancia", "sub": "subaru", "mas": "maserati",
    "cad": "cadillac", "koen": "koenigsegg", "mcl": "mclaren", "lam": "lamborghini",
    # "AM Vulcan", "P. 911 GT3 RS" -- the screen shortens the make to fit the column.
    "am": "aston martin", "p": "porsche", "chev": "chevrolet", "mer": "mercedes benz",
    "lambo": "lamborghini", "chevy": "chevrolet", "merc": "mercedes benz",
    "vw": "volkswagen", "mb": "mercedes benz", "bimmer": "bmw",
    "alfa": "alfa romeo", "aston": "aston martin", "rr": "rolls royce",
    "caddy": "cadillac", "porsche": "porsche", "olds": "oldsmobile",
    "chev": "chevrolet", "mercedes": "mercedes benz", "hyundai": "hyundai",
}
# Two-token abbreviations the screen uses, folded before the make alias pass.
# Applied per TOKEN, never as a substring. " fe" as a substring matched the start of
# " ferrari" and turned every Ferrari into "forza editionrrari" -- 458 Italia became a
# J50 and the breakage was invisible until a name was traced by hand.
# A name with no edition words means the plain car. The roster ships each edition as its
# own entry, so "Lancer MR '04" fits both the Evolution VIII MR and its Welcome Pack and
# would otherwise be refused as ambiguous.
EDITION_MARKERS = ("forza edition", "welcome pack", "preorder")

# Ordered slowest to fastest, so "is this car too fast to be on that board" is a
# comparison. R sits at the top: the game shows it to the right of S2.
CLASS_ORDER = {"D": 0, "C": 1, "B": 2, "A": 3, "S1": 4, "S2": 5, "R": 6}


def class_plausible(car: dict, board_class: str | None) -> bool:
    """Could this car have set a lap on a board of that class?

    Upwards is free -- any car can be tuned onto a faster board. Downwards is not:
    stripping a car two full classes below its stock rating is not something Rivals
    entrants do, so a candidate that far above the board is the wrong candidate.
    One class of slack is allowed because mild detuning is ordinary.
    """
    if not board_class:
        return True
    stock = (car.get("stock_class") or "").upper()
    if stock not in CLASS_ORDER or board_class.upper() not in CLASS_ORDER:
        return True
    return CLASS_ORDER[stock] - CLASS_ORDER[board_class.upper()] <= 1


def resolve_edition(cars: list[dict], board_class: str | None) -> dict | None:
    """Choose between a car and its Forza Edition sibling, or refuse to.

    They share make, model and year, so the screen -- which writes "Sprinter Trueno
    '85" for both -- cannot separate them, and the old rule "an unqualified name
    means the plain one" quietly filed every Forza Edition lap under the base car.
    For the AE86 those two are 224 PI and two classes apart (D 376 against B 600),
    so the BOARD says which is possible even when the name does not.

    Returns the single surviving car, or None when the evidence does not decide --
    a missing name costs a filter row, a wrong one corrupts the analysis.
    """
    possible = [car for car in cars if class_plausible(car, board_class)]
    if not possible:
        return None
    if len(possible) == 1:
        return possible[0]
    # A car whose stock class IS the board's class is the straightforward reading;
    # the alternative needs someone to have tuned across a class boundary.
    exact = [car for car in possible
             if (car.get("stock_class") or "").upper() == (board_class or "").upper()]
    if len(exact) == 1:
        return exact[0]
    plain = [car for car in possible
             if not any(mark in car["name"].lower() for mark in EDITION_MARKERS)]
    if len(plain) == 1 and board_class is None:
        return plain[0]
    return None

TOKEN_ALIASES = {
    "fe": "forza edition",
    # The game names this car by its chassis code, the roster by its model. Without the
    # alias "Toyota AE86 FE" matched a BMW M2 Forza Edition and "Toyota AE86" matched the
    # 2013 Toyota 86 -- a car 28 years younger. Its laps then counted for the wrong car,
    # which is why it looked missing from S1.
    "ae86": "sprinter trueno",
    # More of the screen's shorthand, each confirmed against a roster entry:
    # "Mit. Evo Ill '95" is the Lancer Evolution III GSR, "...VIII MR WP" the Welcome
    # Pack edition, and "1 Evolution TA" the Sierra Sierra Enterprises Time Attack car.
    # Without these the Evo III was filed as an Eclipse GSX.
    "evo": "evolution",
    "wp": "welcome pack",
    "ta": "time attack",
    "tme": "tm edition",
    "jcw": "john cooper works",
    "comp": "competition",
}

PHRASE_ALIASES = [
    ("m b ", "mercedes benz "),
    ("m amg ", "mercedes amg "),
    (" t a ", " trans am "),
    (" t a", " trans am"),
    (" coup ", " coupe "),
    (" limo", " limousine"),
]


# A screen-read name often arrives with the neighbouring column bled into it -- the
# rank and class sitting to its left. Real examples from 2026-08-23: "B,225 | DeLorean
# DMC-12", "114,710] Toyota GR Yaris", "ta] Exocet '18", "1,709 | Alfa Romeo 4C".
# Uncleaned, these fail the match and the car goes unnamed, which is how a whole night
# of name captures returned three names.
RANK_BLEED = re.compile(r"^\s*[A-Za-z0-9,.\s]{0,7}[|\]}]+\s*")
# `#` is kept: "#19 CRX WTAC" is a real car name in this game, not punctuation.
LEADING_JUNK = re.compile(r"^[^A-Za-z0-9#]+")


def clean_ocr_name(name: str) -> str:
    """Strip the rank/class fragment the OCR sometimes carries into the car name."""
    text = RANK_BLEED.sub("", str(name))
    text = LEADING_JUNK.sub("", text)
    # Hinter einem senkrechten Strich stehen Antrieb und Fahrhilfen, nicht der Name:
    # "Porsche 911 Turbo S '23 | AwD 3-3". KEIN Auto der Liste fuehrt einen solchen
    # Strich (geprueft: 0 von 636), also ist das Abschneiden gefahrlos.
    #
    # Noetig wurde es, als useful_tokens() anfing, ZIFFERN zu behalten -- richtig fuer
    # "Audi RS 7", aber damit wurden auch die "3" und "4" aus "AwD 3-3" zu geforderten
    # Tokens, die kein Listeneintrag erfuellen kann. 613 Zeilen fielen dadurch aus
    # (2026-09-06).
    # Der senkrechte Strich trennt den Namen von Beiwerk -- aber auf BEIDEN Seiten:
    #   "Porsche 911 Turbo S '23 | AwD 3-3"   Name links, Antrieb rechts
    #   "x) | Honda Beat '91"                 Rest der Rangspalte links, Name rechts
    # Blind links zu nehmen zerstoerte den zweiten Fall (1.676 Schreibweisen am
    # 2026-09-06). Also den Abschnitt waehlen, der die meisten BUCHSTABEN hat: ein
    # Autoname besteht aus Woertern, "AwD 3-3" und "x)" nicht.
    if "|" in text:
        teile = [teil.strip() for teil in text.split("|") if teil.strip()]
        if teile:
            text = max(teile, key=lambda teil: sum(ch.isalpha() for ch in teil))
    return re.sub(r"\s+", " ", text).strip()


def normalise(text: str) -> str:
    """Fold away what OCR gets wrong about punctuation and case."""
    text = text.lower()
    # Split an apostrophe-year off as its own token BEFORE the apostrophe is dropped.
    # Deleting it glued "Toyota Celica'94" into "celica94", which is not a two-digit
    # token, so the year never bound and the name fell through to a fuzzy guess -- it
    # came back as Celica GT '74, 326 rows of a car from twenty years earlier. Only a
    # two-digit group is touched, so "Nissan Silvia K's" is left as it was.
    text = re.sub(r"['`‘’]\s?(\d{2})(?!\d)", r" \1", text)
    # Fold accents rather than letting the strip below turn them into a space: the
    # roster holds "Lamborghini Huracan" with an accent, which became "hurac n" while
    # the screen reads "Huracan" -- the two could never meet. Same for Megane and Coupe.
    text = "".join(ch for ch in unicodedata.normalize("NFKD", text)
                   if not unicodedata.combining(ch))
    # OCR reads a capital S in a model name as a dollar sign ("$2000 WTAC", "Honda $800").
    text = text.replace("$", "s")
    text = text.replace("'", "").replace("`", "").replace("-", " ")
    text = re.sub(r"[^a-z0-9 ]+", " ", text)
    text = re.sub(r"\s+", " ", text).strip()
    for source, target in PHRASE_ALIASES:
        if source in f" {text} ":
            text = f" {text} ".replace(source, target).strip()
            text = re.sub(r"\s+", " ", text)
    parts = text.split()
    if parts and parts[0] in MAKE_ALIASES:
        parts[0:1] = MAKE_ALIASES[parts[0]].split()
    # Expanding a make alias can duplicate the word that already followed it: "aston"
    # becomes "aston martin" and the original "martin" stays, so "Aston Martin Vulcan"
    # normalised to "aston martin martin vulcan" and no read name could ever match it.
    # Alfa Romeo, Mercedes-Benz and Rolls-Royce all had the same defect.
    deduped: list[str] = []
    for token in parts:
        if not deduped or deduped[-1] != token:
            deduped.append(token)
    parts = deduped
    expanded: list[str] = []
    for token in parts:
        # OCR cannot tell a capital I from a lowercase l, so a Roman numeral comes back
        # as "Ill" or "Il". A token made only of those two letters is always a numeral --
        # no model name is -- so fold it back before the aliases run.
        if len(token) > 1 and set(token) <= {"i", "l"}:
            token = "i" * len(token)
        expanded.extend(TOKEN_ALIASES.get(token, token).split())
    return " ".join(expanded)


def without_year(text: str) -> str:
    """Same fold, minus a trailing two-digit year the screen abbreviates to."""
    parts = normalise(text).split()
    if parts and len(parts[-1]) == 2 and parts[-1].isdigit():
        parts.pop()
    return " ".join(parts)


def build_year_index(cars: list[dict]) -> dict[str, list[tuple[str, dict]]]:
    """Roster keyed by the two-digit year the screen prints.

    The year is decisive, not decoration. The screen abbreviates the model -- "Honda
    Civic '97" for a Civic Type R -- so a matcher that treats the year as noise pulls
    four different Civics onto one roster entry, which is exactly what happened: eight
    car ids all named "Honda Civic Si", each with unanimous votes for a wrong car.
    """
    index: dict[str, list[tuple[str, dict]]] = defaultdict(list)
    for car in cars:
        if not car["year"]:
            continue
        short = str(car["year"])[-2:]
        for key in {normalise(car["name"]), normalise(car["model"]),
                    normalise(f"{car['make']} {car['model']}")}:
            if key:
                index[short].append((key, car))
    return index


def build_index(cars: list[dict]) -> dict[str, dict]:
    """Several keys per car: the full name, the model alone, and the model without year.

    ZWEI DURCHGAENGE, und das ist der Punkt. Die ECHTEN Namen eines Autos werden zuerst
    eingetragen; die abgeleiteten -- der Name ohne sein erstes Wort -- erst danach, und
    nur wenn sie keinem anderen Auto ins Gehege kommen.

    Warum: die Ableitung gibt es fuer abgeschnittene Lesungen ("S-Cargo FE" kam als
    "cargo fe" an). Beim Formula-Drift-Wagen heisst das Modellfeld aber "#99 Mazda
    RX-8", ohne erstes Wort also "mazda rx 8" -- der natuerliche Name des NORMALEN
    RX-8, der in der Liste "Mazda RX-8 R3" heisst. Wer zuerst kommt, mahlt zuerst, und
    das war der Drift-Wagen: jede Runde eines gewoehnlichen RX-8 lief seither unter
    einem Rennwagen, mit Sicherheit 1,00 (gefunden 2026-09-11 auf Coastline Sprint).

    Ein abgeleiteter Schluessel faellt deshalb weg, wenn er der Anfang des echten
    Namens eines ANDEREN Autos ist. Dann ist er kein Kuerzel, sondern eine
    Verwechslung, und der Name geht seinen normalen Weg durch den Zuordner.
    """
    def echte_schluessel(car: dict) -> set[str]:
        keys = {normalise(car["name"]), normalise(car["model"]),
                normalise(f"{car['make']} {car['model']}")}
        if car["year"]:
            short = str(car["year"])[-2:]
            for base in list(keys):
                keys.add(f"{base} {short}")
        return {k for k in keys if k and len(k) >= 4}

    index: dict[str, dict] = {}
    echte: dict[str, dict] = {}
    for car in cars:
        for key in echte_schluessel(car):
            echte.setdefault(key, car)
            index.setdefault(key, car)

    # Woerter, mit denen ein echter Name ANFAENGT -- daran wird die Ableitung geprueft.
    anfaenge = sorted(echte)

    def ist_anfang_eines_anderen(key: str, car: dict) -> bool:
        import bisect
        stelle = bisect.bisect_left(anfaenge, key + " ")
        while stelle < len(anfaenge) and anfaenge[stelle].startswith(key + " "):
            if echte[anfaenge[stelle]] is not car:
                return True
            stelle += 1
        return False

    for car in cars:
        abgeleitet: set[str] = set()
        for base in echte_schluessel(car):
            parts = base.split()
            if len(parts) > 1:
                abgeleitet.add(" ".join(parts[1:]))

        # ERST pruefen, DANN Jahrgaenge anhaengen. Andersherum rutscht die Variante
        # durch, deren Stamm gerade verworfen wurde: "ford mustang" gehoert dem
        # Drift-Wagen nicht, "ford mustang 15" waere aber kein Anfang eines anderen
        # echten Namens und bliebe stehen -- und ist zu "ford mustang s5" (dem RTR
        # Spec 5) zu 93 % aehnlich. Genau so landete jede Runde des RTR Spec 5 unter
        # einem Formula-Drift-Rennwagen (2026-09-11).
        erlaubt = set()
        for key in abgeleitet:
            if not key or len(key) < 4:
                continue
            if key in echte and echte[key] is not car:
                continue
            if ist_anfang_eines_anderen(key, car):
                continue
            erlaubt.add(key)

        if car["year"]:
            short = str(car["year"])[-2:]
            for base in list(erlaubt):
                erlaubt.add(f"{base} {short}")

        for key in erlaubt:
            if key not in index:
                index[key] = car
    return index



def token_hits(read_token: str, candidate_tokens: list[str]) -> bool:
    """Steckt das gelesene Token in den Tokens eines Listeneintrags?

    Gleichheit zaehlt immer. Zusaetzlich gilt ein PRAEFIX-Treffer, denn das Spiel
    kuerzt Modellcodes: der Schirm schreibt "McLaren 765", die Liste
    "McLaren 765LT Coupe". Ohne diese Regel ist "765" in "765lt" nicht enthalten, der
    Abgleich scheitert, und der Aehnlichkeitsschaetzer greift daneben -- am 2026-09-06
    landeten so 741 Runden des 765LT unter dem McLaren 620R.

    Der Praefix-Treffer ist bewusst eng gefasst:
    - mindestens DREI Zeichen. Sonst wuerde die "7" aus "Audi RS 7" auf "765lt"
      passen, und ein einstelliges Token darf nie ein fremdes Modell aufziehen.
    - der Rest des Listen-Tokens muss BUCHSTABEN sein ("765" + "lt"). Damit trifft
      "12" nicht auf "1234", also keine Zahl auf eine andere Zahl.
    """
    if read_token in candidate_tokens:
        return True
    if len(read_token) < 3:
        return False
    for candidate in candidate_tokens:
        if (len(candidate) > len(read_token)
                and candidate.startswith(read_token)
                and candidate[len(read_token):].isalpha()):
            return True
    return False


def useful_tokens(text: str) -> list[str]:
    """Die Tokens, die ein Auto unterscheiden.

    Frueher: alles mit mehr als einem Zeichen. Das warf ZIFFERN weg -- und bei
    "Audi RS 7" ist die "7" das einzige Unterscheidungsmerkmal. Uebrig blieb
    "audi rs", was auf RS 6 Avant, RS 7 Sportback und RS e-tron GT zugleich passt;
    mehrdeutig, also verworfen. Der RS 7 fehlte dadurch KOMPLETT in der Auswertung,
    obwohl er in 242 Boards mit ueber 800 Runden steht (gefunden 2026-09-06).
    """
    return [token for token in text.split() if len(token) > 1 or token.isdigit()]



def containment_hits(tokens: list[str], paare: list[tuple[str, dict]]) -> dict:
    """Kandidaten, die ALLE gelesenen Tokens enthalten -- exakt zuerst, Praefix danach.

    Die Reihenfolge ist der ganze Punkt. Ein Praefix-Treffer ist grosszuegiger und kann
    ZUSAETZLICHE Kandidaten aufziehen, aus einem eindeutigen Treffer also einen
    mehrdeutigen machen. Genau das passierte am 2026-09-06 mit "Alfa Romeo GTA": exakt
    passt nur der Giulia Sprint GTA (1965), per Praefix aber auch der Giulia GTAm
    (2021) -- 686 Zeilen fielen dadurch von "zugeordnet" auf "verworfen".

    Also: erst streng vergleichen. Bleibt genau einer uebrig, ist er es. Erst wenn
    streng KEINER passt, wird die Praefix-Regel zugelassen, die "McLaren 765" mit
    "765LT Coupe" verbindet.
    """
    streng = {id(car): car for schluessel, car in paare
              if all(token in schluessel.split() for token in tokens)}
    if len(streng) == 1:
        return streng
    locker = {id(car): car for schluessel, car in paare
              if all(token_hits(token, schluessel.split()) for token in tokens)}
    return streng if streng else locker


def match_within_year(base: str, candidates: list, board_class: str | None,
                      allow_fuzzy: bool = True):
    """Den Namen unter den Autos EINES Jahrgangs suchen. (car, score) oder (None, 0.0)."""
    if not candidates:
        return None, 0.0
    for candidate_key, car in candidates:
        if candidate_key == base:
            return car, 1.0
    tokens = useful_tokens(base)
    if tokens:
        hits = containment_hits(tokens, candidates)
        if len(hits) > 1:
            picked = resolve_edition(list(hits.values()), board_class)
            if picked is None:
                picked = fewest_extra_words(tokens, list(hits.values()))
            if picked is None:
                return None, 0.0
            hits = {id(picked): picked}
        if len(hits) == 1:
            return next(iter(hits.values())), 0.9
    # The game and the roster do not always use the same model words: the screen says
    # "Corvette C8 '20" where forza.net says "Chevrolet Corvette Stingray Coupe". A
    # token the roster never uses can therefore never be satisfied -- but the YEAR
    # already narrows the field hard, so drop tokens from the end until exactly one
    # car of that year is left.
    remaining = useful_tokens(base)
    while len(remaining) > 1:
        remaining = remaining[:-1]
        hits = containment_hits(remaining, candidates)
        if len(hits) == 1:
            car = next(iter(hits.values()))
            # At least one surviving token has to name the MODEL. Dropping down to the
            # make alone matched "L. Aventador '21" against the only other Lamborghini
            # of 2021 and called it a Countach.
            make = set(normalise(car.get("make") or "").split())
            if any(token not in make for token in remaining):
                return car, 0.8
            break
        if len(hits) > 1:
            break
    if not allow_fuzzy:
        # Ueber eine Jahresgrenze hinweg wird NICHT geraten. Der Aehnlichkeitsschaetzer
        # hat "L. Aventador '21" im Nachbarjahrgang auf einen Lamborghini Sian Roadster
        # gelegt (score 0,74) -- ein falsches Auto ist schlechter als gar keines.
        return None, 0.0
    pool = {candidate_key: car for candidate_key, car in candidates}
    close = difflib.get_close_matches(base, list(pool), n=1, cutoff=0.62)
    if close:
        return pool[close[0]], difflib.SequenceMatcher(None, base, close[0]).ratio()
    return None, 0.0


# Bildschirmnamen, die zu KURZ sind, um eindeutig zu sein.
#
# Forza kuerzt lange Namen, und mehrere Autos fallen dabei auf denselben Text
# zusammen: "Acura Integra" passt auf den Type R '01 UND den A-Spec '23, "Dodge
# Charger 69" auf den R/T und den Daytona HEMI. Der Zuordner hat solche Namen bisher
# verworfen -- richtig, denn Raten waere schlimmer -- und damit 8,7 % aller Zeilen
# weggeworfen (556.921 von 6.366.532, gemessen 2026-09-10).
#
# Die Aufloesung kommt NICHT aus einer Heuristik, sondern aus Belegen:
#   * "Acura Integra": ueber die Spielspeicher-Kennung bewiesen. car_id 368 ist laut
#     Spiel der Integra Type R '01; von 40 seiner Rundenzeiten auf Highway Circuit C
#     tragen 30 auf dem Schirm genau diesen Namen.
#   * Der Rest stammt vom Besitzer des Bestands, der die Autos faehrt und die
#     Abkuerzungen kennt (2026-09-10).
#
# Wer hier etwas ergaenzt, schreibt bitte dazu, WORAUS die Zuordnung folgt. Ein
# falscher Eintrag ist teurer als ein fehlender: eine verworfene Zeile fehlt, eine
# falsch benannte verfaelscht zwei Autos gleichzeitig.
SHORT_NAMES: dict[str, tuple[str, int]] = {
    "acura integra": ("Acura Integra Type R", 2001),
    "dodge charger 69": ("Dodge Charger R/T", 1969),
    "ferrari 458": ("Ferrari 458 Italia", 2009),
    "ferrari 458 s": ("Ferrari 458 Speciale", 2013),
    "nismo 24": ("Nissan Z NISMO", 2024),
    "z nismo 24": ("Nissan Z NISMO", 2024),
    "nismo gt r 24": ("Nissan GT-R Nismo", 2024),
    "lotus exige 18": ("Lotus Exige Cup 430", 2018),
    "exige wtac": ("Lotus Scura Motorsports Exige WTAC", 2018),
    "ford raptor": ("Ford F-150 Raptor R", 2023),
    "lancia delta": ("Lancia Delta HF Integrale EVO", 1992),
    "lancia delta s4": ("Lancia Delta S4", 1986),
    # "Toyota Tacoma" trugen beide: der TRD Pro und seine Forza Edition. Ein
    # fester Eintrag muesste sich fuer eines entscheiden und laege in der
    # Haelfte der Faelle falsch; die Klasse des Boards trennt sie sauber
    # (resolve_edition). Darum steht hier NICHTS -- das Wiki nennt den TRD Pro,
    # und die Forza Edition holt sich der Klassenvergleich.
    "audi quattro": ("Audi Sport quattro", 1984),
    "pagani zonda": ("Pagani Zonda R", 2009),
    "pagani zonda c": ("Pagani Zonda Cinque Roadster", 2010),
    "huayra r 21": ("Pagani Huayra R", 2021),
    "ford lightning": ("Ford F-150 SVT Lightning", 2003),
    "f 150 lightning": ("Ford F-150 Lightning", 2022),
    "peugeot 205": ("Peugeot 205 Rallye", 1991),
    "peugeot 205 r": ("Peugeot 205 Rallye", 1991),
    "peugeot 205 t16": ("Peugeot 205 Turbo 16", 1984),
    "2 audi st": ("Audi #2 Audi Sport quattro S1", 1986),
    "mit evo vill wp": ("Mitsubishi Lancer Evolution VIII MR Welcome Pack", 2004),
    "toyota supra 20": ("Toyota GR Supra", 2020),
    "gmc k5 jimmy": ("GMC Jimmy", 1970),
    "gmc ks jimmy": ("GMC Jimmy", 1970),
    "ford sd f 450": ("Ford Super Duty F-450 DRW PLATINUM", 2020),
    "34 vw beetle": ("Volkswagen #34 Andretti Rally Cross Beetle", 2017),
    # "S5" ist die Kurzform des Spiels fuer "Spec 5" -- das kann kein Zuordner
    # herleiten, das weiss nur, wer das Auto faehrt (2026-09-11).
    "ford mustang s5": ("Ford Mustang RTR Spec 5", 2018),
    "ford mustang 5": ("Ford Mustang RTR Spec 5", 2018),
    "mustang s5": ("Ford Mustang RTR Spec 5", 2018),
    # OCR-Schaden, kein Kuerzungsproblem: das á wird als é oder als Ersatzzeichen
    # gelesen. Der Rest des Namens ist eindeutig.
    "huracen sterrato": ("Lamborghini Huracán Sterrato", 2022),
    "huracn sterrato": ("Lamborghini Huracán Sterrato", 2022),
    "apollo ie 19": ("Apollo Intensa Emozione", 2019),
    "subaru 228 98": ("Subaru Impreza 22B-STi Version", 1998),
    "alfa romeo 33s": ("Alfa Romeo 33 Stradale", 1968),
    "gma t 50": ("Gordon Murray Automotive T.50", 2020),
    "audi r813": ("Audi R8 Coupé V10 plus 5.2 FSI quattro", 2013),
    "jeep wrangler dd": ("DeBerti Jeep Wrangler Unlimited", 2013),
}


# Die Tabelle wird ueber den NORMALISIERTEN Namen nachgeschlagen, nicht ueber den
# rohen: `normalise` schreibt Abkuerzungen aus, aus "Mit. Evo Vill WP" wird
# "mitsubishi evolution vill welcome pack". Beide Seiten muessen durch dieselbe
# Muehle, sonst trifft die Tabelle nie.
_SHORT_BY_KEY: dict[str, tuple[str, int]] = {}


# Die grosse, von aussen gepflegte Zuordnungstabelle. Sie entsteht aus
# `data/analytics/unmatched_car_names_matched.tsv` ueber
# `scripts/import_name_aliases.py`, das nur eindeutige Zuordnungen uebernimmt --
# steht die zweitbeste Wahl gleichauf, bleibt der Name ungeloest statt geraten.
ALIAS_FILE = Path(__file__).resolve().parent.parent / "config/fh6_screen_name_aliases.tsv"


def _load_alias_file() -> dict[str, tuple[str, int]]:
    """Die Datei einlesen, oder ein leeres Verzeichnis, wenn es sie nicht gibt."""
    raus: dict[str, tuple[str, int]] = {}
    try:
        text = ALIAS_FILE.read_text(encoding="utf-8")
    except OSError:
        return raus
    for zeile in text.splitlines():
        if not zeile.strip() or zeile.startswith("#") or zeile.startswith("schluessel"):
            continue
        teile = zeile.split("\t")
        if len(teile) < 3:
            continue
        schluessel, name, jahr = teile[0].strip(), teile[1].strip(), teile[2].strip()
        if not schluessel or not name or not jahr.isdigit():
            continue
        raus[schluessel] = (name, int(jahr))
    return raus


# Die Kurznamen, die das Spiel selbst anzeigt, belegt vom Forza-Wiki:
#   The 2001 '''Acura Integra Type R''' - abbreviated as "Acura Integra" - is ...
# Geholt von `scripts/fetch_wiki_abbreviations.py`, geprueft von
# `scripts/test_car_name_matching.py`. Das ist die einzige Quelle in dieser Kette, die
# NICHT aus unseren eigenen Annahmen stammt -- und die einzige, die einen Fehler wie
# "Mazda RX-8 ist ein Formula-Drift-Rennwagen" ueberhaupt sichtbar machen konnte.
WIKI_FILE = Path(__file__).resolve().parent.parent / "config/fh6_wiki_abbreviations.json"


def _load_wiki_abbreviations() -> dict[str, tuple[str, int]]:
    """Kurzname -> Auto, aus dem Wiki. Mehrdeutige bleiben draussen."""
    raus: dict[str, tuple[str, int]] = {}
    try:
        daten = json.loads(WIKI_FILE.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return raus
    mehrdeutig = set(daten.get("ambiguous", []))
    for kurz, ziele in (daten.get("abbreviations") or {}).items():
        if kurz in mehrdeutig or not ziele:
            continue
        ziel = ziele[0]
        jahr = ziel.get("year")
        name = (ziel.get("name") or "").strip()
        if not name or not isinstance(jahr, int):
            continue
        schluessel = normalise(kurz)
        if schluessel:
            raus[schluessel] = (name, jahr)
    return raus


def _short_lookup() -> dict[str, tuple[str, int]]:
    if not _SHORT_BY_KEY:
        # Reihenfolge ist Rangfolge, von schwach nach stark:
        #   1. die maschinelle Tabelle (Aehnlichkeitsrechnung),
        #   2. das Wiki (belegt, was das Spiel anzeigt),
        #   3. die Handtabelle im Quelltext (eigener Beweis, etwa ueber Rundenzeiten).
        _SHORT_BY_KEY.update(_load_alias_file())
        _SHORT_BY_KEY.update(_load_wiki_abbreviations())
        for text, wunsch in SHORT_NAMES.items():
            _SHORT_BY_KEY[normalise(text)] = wunsch
    return _SHORT_BY_KEY


def strip_lead_noise(read: str) -> str | None:
    """Ein fuehrendes Muellwort abwerfen, oder None, wenn keins da ist.

    Die Rangspalte blutet in den Namen, und nicht nur als Ziffern oder Striche: auf
    hellen Kacheln liest die OCR daraus Buchstabenmuell -- "EEE Acura Integra",
    "Ell Acura Integra", "Ball Acura Integra", "th Acura Integra". LEADING_JUNK faengt
    Ziffern und Striche, diese Woerter nicht, und jede Schreibweise fiel einzeln durch.
    Von 47.219 verschiedenen unerkannten Namen (2026-09-10) ist ein grosser Teil genau
    das: ein bekanntes Auto mit einem Wort Dreck davor.

    Abgeworfen wird nur ein KURZES erstes Wort, und nur wenn danach noch zwei Woerter
    stehen -- damit "Peel P50" oder "GMA T.50" nicht zu "P50" verstuemmelt werden.
    """
    teile = read.split()
    if len(teile) < 3:
        return None
    erstes = teile[0]
    if len(erstes) > 4 or any(ch.isdigit() for ch in erstes):
        return None
    return " ".join(teile[1:])


def match_short_name(key: str, index: dict[str, dict]) -> dict | None:
    """Ein Auto aus der Kurznamen-Tabelle, oder None."""
    wunsch = _short_lookup().get(key)
    if wunsch is None:
        return None
    name, jahr = wunsch
    gesucht = normalise(name)
    for kandidaten in index.values():
        for car in (kandidaten if isinstance(kandidaten, list) else [kandidaten]):
            if normalise(car["name"]) == gesucht and car["year"] == jahr:
                return car
    return None


_MODEL_WORDS: dict[int, set[str]] = {}
_ALL_CARS: dict[int, list] = {}


def _all_cars(index: dict) -> list:
    """Die Autos hinter einem Verzeichnis, einmal ausgepackt und gemerkt."""
    schluessel = id(index)
    if schluessel not in _ALL_CARS:
        raus = []
        for wert in index.values():
            raus.extend(wert if isinstance(wert, list) else [wert])
        _ALL_CARS[schluessel] = raus
    return _ALL_CARS[schluessel]


def _model_words(car: dict) -> set[str]:
    """Die Woerter des MODELLNAMENS -- ohne Jahrgang."""
    schluessel = id(car)
    if schluessel not in _MODEL_WORDS:
        _MODEL_WORDS[schluessel] = set(normalise(car["name"]).split())
    return _MODEL_WORDS[schluessel]


def fewest_extra_words(read_tokens: list[str], candidates: list[dict]) -> dict | None:
    """Der Kandidat mit den WENIGSTEN zusaetzlichen Wortern, oder None bei Gleichstand.

    Der Schirm kuerzt einen Namen HINTEN: aus "Mazda RX-8 R3" wird "Mazda RX-8". Er
    kuerzt ihn nicht, indem er vorne "Formula Drift #99" wegnimmt -- ein Rennwagen mit
    Startnummer heisst auf dem Schirm auch so. Enthalten mehrere Autos alle gelesenen
    Woerter, ist deshalb das mit dem geringsten Ueberhang gemeint.

    Gefunden am 2026-09-11: "Mazda RX-8" traf den "Formula Drift #99 Mazda RX-8"
    (drei Woerter Ueberhang) statt den "Mazda RX-8 R3" (eines). Bei Gleichstand wird
    NICHTS zurueckgegeben -- dann ist der Name wirklich mehrdeutig.
    """
    gelesen = set(read_tokens)
    bewertet = []
    for car in candidates:
        worte = set(normalise(car["name"]).split())
        bewertet.append((len(worte - gelesen), car))
    bewertet.sort(key=lambda paar: paar[0])
    if len(bewertet) < 2:
        return bewertet[0][1] if bewertet else None
    if bewertet[0][0] == bewertet[1][0]:
        return None
    return bewertet[0][1]


def match_car(read: str, index: dict[str, dict], keys: list[str],
              year_index: dict[str, list[tuple[str, dict]]] | None = None,
              board_class: str | None = None
              ) -> tuple[dict | None, float]:
    """Exact fold first, then closest match -- OCR errors are usually one or two chars.

    When the read name carries a two-digit year, matching happens ONLY among cars of
    that year, and failing there returns nothing rather than falling back to a
    year-blind guess.

    `board_class` is the performance class of the board the name was read on. It
    settles the one ambiguity the name cannot: a car and its Forza Edition share
    make, model and year and differ only in class. See `resolve_edition`.
    """
    key = normalise(read)
    if not key:
        return None, 0.0

    kurz = match_short_name(key, index)
    if kurz is None:
        # Auch mit einem Wort Dreck davor: "EEE Acura Integra" ist derselbe Kurzname
        # wie "Acura Integra", und ohne diesen Anlauf faellt jede Schreibweise einzeln
        # durch, obwohl die Tabelle das Auto kennt.
        ohne_muell = strip_lead_noise(key)
        if ohne_muell:
            kurz = match_short_name(ohne_muell, index)
    if kurz is not None:
        return kurz, 0.99

    parts = key.split()
    # A trailing two-digit token is only a model YEAR when the screen wrote it with an
    # apostrophe -- "Corvette C8 '20". Without one it belongs to the model name, and
    # treating it as a year sent "DeLorean DMC-12" looking for a 2012 "delorean dmc"
    # that does not exist. That one misreading cost 323 rows, and "Jaguar XJR-15",
    # "TVR Speed 12" and "Honda Civic 16" failed the same way.
    wrote_year = bool(APOSTROPHE_YEAR.search(read))

    # ... ausser die OCR hat das Apostroph verschluckt. Dann steht "Corvette 15" da,
    # und weil die Liste ein Auto kennt, das schlicht "Chevrolet Corvette" heisst
    # (die 1953er), gewann bisher diese mit 0,95 -- die Runde landete unter dem
    # falschen Auto statt gar nicht. Gemessen 2026-09-10: 0,52 % aller Zeilen, unter
    # anderem GT-R '12, 370Z Nismo '19, Camaro ZL1 1LE '18, Mustang GT500 '13 und
    # Corvette Z06 '15.
    #
    # Die Unterscheidung, die beide Lehren zusammenbringt: gehoert die Zahl zum
    # MODELLNAMEN, dann steht sie in der Liste als eigenes Wort neben demselben
    # Grundnamen -- "Toyota 86", "TVR Speed 12", "DeLorean DMC-12". Steht sie dort
    # nicht, ist sie ein Jahr ohne Apostroph. Ohne diese Pruefung haette die
    # Reparatur 375 Zeilen "Toyota 86" zu einem Sprinter Trueno von 1986 gemacht.
    if (year_index and not wrote_year and parts and len(parts[-1]) == 2
            and parts[-1].isdigit()):
        zahl = parts[-1]
        grund = " ".join(parts[:-1])
        kern = [w for w in grund.split() if len(w) > 2]
        # Gegen die MODELLNAMEN pruefen, nicht gegen die Verzeichnis-Schluessel: die
        # tragen den Jahrgang bereits als eigenes Wort ("corvette z06 15"), womit die
        # Pruefung immer zutraefe und die Reparatur wirkungslos waere.
        gehoert_zum_modell = any(
            zahl in _model_words(car) and (not kern or kern[-1] in _model_words(car))
            for car in _all_cars(index)
        )
        if not gehoert_zum_modell:
            car, score = match_within_year(grund, year_index.get(zahl, []), board_class)
            if car is not None:
                # Gedeckelt: ein ergaenztes Apostroph ist nie so sicher wie ein
                # gelesenes.
                return car, min(score, 0.95)

    if year_index and wrote_year and parts and len(parts[-1]) == 2 and parts[-1].isdigit():
        short = parts[-1]
        base = " ".join(parts[:-1])
        car, score = match_within_year(base, year_index.get(short, []), board_class)
        if car is not None:
            return car, score
        # Das Modelljahr des Spiels und das der Liste weichen manchmal um EINS ab. Der
        # Ferrari F80 steht auf dem Schirm als '24 und in der Liste als 2025; der
        # Lamborghini Aventador Ultimae als '21 gegen 2022. Ohne diesen Nachschlag
        # fielen 6.158 Zeilen weg -- nicht wegen eines Lesefehlers, sondern weil zwei
        # Quellen dasselbe Auto verschieden datieren (gemessen 2026-09-06).
        #
        # Der Nachbarjahrgang zaehlt weniger: score wird gedeckelt, damit eine
        # Zuordnung ueber eine Jahresgrenze nie so sicher aussieht wie eine im
        # richtigen Jahr.
        for versatz in (-1, 1):
            nachbar = f"{(int(short) + versatz) % 100:02d}"
            car, score = match_within_year(base, year_index.get(nachbar, []),
                                           board_class, allow_fuzzy=False)
            if car is not None:
                return car, min(score, 0.85)
        return None, 0.0
    if key in index:
        return index[key], 1.0
    trimmed = without_year(read)
    if trimmed and trimmed in index:
        return index[trimmed], 0.95
    # Token containment, before fuzzy matching: the screen prints a short form of the
    # roster name ("crown victoria" for "Ford Crown Victoria", "mazda cosmo" for "Mazda
    # Cosmo Sport"). Accepted only when exactly ONE roster car contains every token, so
    # an ambiguous short form names nothing rather than the wrong car.
    # Junk from the rank column bleeds into the car field ("ug crown victoria", "737
    # pontiac trans am 87"), so try again with the leading token or two dropped. Each
    # attempt still has to be unique, so dropping tokens cannot invent a match.
    for drop in (0, 1, 2):
        tokens = useful_tokens(" ".join((trimmed or key).split()[drop:]))
        if len(tokens) < 2:
            break
        candidate = " ".join(tokens)
        if candidate in index:
            return index[candidate], 0.92 - 0.02 * drop
        # Trailing short tokens are usually the screen's own shorthand for something the
        # roster spells out -- "Mit. Minicab TA" for "...Minicab Time Attack" -- so try
        # again without them. Still unique-or-nothing, so this cannot invent a match.
        for tail in (tokens, tokens[:-1]):
            if len(tail) < 2:
                continue
            hits = containment_hits(tail, list(index.items()))
            if len(hits) == 1:
                return next(iter(hits.values())), 0.9 - 0.02 * drop
            # "Toyota Trueno" matches both the car and its Forza Edition. Same model,
            # different class -- so the board decides, not the missing suffix.
            if len(hits) > 1:
                picked = resolve_edition(list(hits.values()), board_class)
                # Der geringste Ueberhang darf NUR entscheiden, wenn noch jedes
                # gelesene Wort zaehlt. Wurde vorher eins weggeworfen, ist der Rest
                # kein gekuerzter Name mehr, sondern ein Bruchstueck -- und der
                # kuerzeste Kandidat ist dann geraten, nicht erkannt. "Ford Mustang
                # S5" verlor so sein "S5" (der Schirm meint den RTR Spec 5) und
                # bekam den kuerzesten Mustang zugeteilt (2026-09-11, von mir selbst
                # eingebaut und binnen zehn Minuten wieder ausgebaut).
                if picked is None and tail == tokens:
                    picked = fewest_extra_words(tail, list(hits.values()))
                if picked is not None:
                    return picked, 0.88 - 0.02 * drop
    # A bare similarity score is not enough on its own: at cutoff 0.72 "Ferrari 458"
    # came back as "Ferrari J50" and "Ferrari 430" as "Ferrari 430 Scuderia" -- one of
    # those is right and the other is a different car. So a fuzzy hit must also agree on
    # something concrete: every alphabetic word of the read name has to appear in the
    # match (makes and model words), or the strings must be near-identical.
    close = difflib.get_close_matches(key, keys, n=1, cutoff=0.72)
    if close:
        ratio = difflib.SequenceMatcher(None, key, close[0]).ratio()
        tokens = {token for token in key.split() if len(token) > 1}
        # Order matters here. A read name that is a PREFIX of a roster name scores very
        # high -- "lamborghini huracan" against "lamborghini huracan sto" is 0.905 -- and
        # a similarity shortcut placed first therefore filed a name that fits all six
        # Huracans under whichever one difflib ranked first. So the truncation case is
        # decided by uniqueness, and only a name that is NOT a truncation may lean on
        # similarity alone (that is the OCR-noise case, "Huracdn STO" for "Huracan STO").
        if tokens and tokens <= set(close[0].split()):
            rivals = [name for name in keys if tokens <= set(name.split())]
            if len(rivals) == 1:
                return index[rivals[0]], ratio
            # A car and its Forza Edition are the same model; the board's class is
            # what separates them.
            picked = resolve_edition([index[name] for name in rivals], board_class)
            if picked is not None:
                return picked, ratio * 0.98
        elif ratio >= 0.9:
            # Never across makes. The read name's leading word is the make when the
            # screen prints one, and a similarity score alone was happy to answer a
            # Toyota with a BMW.
            lead = key.split()[0] if key.split() else ""
            if not lead or lead in set(close[0].split()):
                return index[close[0]], ratio
    # Last resort for a name whose trailing digits carried no apostrophe: the OCR may
    # simply have dropped it. Tried only after everything else has failed, so a real
    # model number is never sacrificed to a year that was never written.
    if year_index and not wrote_year and parts and len(parts[-1]) == 2 and parts[-1].isdigit():
        candidates = year_index.get(parts[-1], [])
        base = " ".join(parts[:-1])
        pool = {candidate_key: car for candidate_key, car in candidates}
        if base in pool:
            return pool[base], 0.85
        tokens = [token for token in base.split() if len(token) > 1]
        if tokens:
            hits = {id(car): car for candidate_key, car in candidates
                    if all(token in candidate_key.split() for token in tokens)}
            if len(hits) == 1:
                return next(iter(hits.values())), 0.82
    return None, 0.0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--html", type=Path,
                        default=Path("data/car_catalogue/fh6cars.html"))
    parser.add_argument("--roster-out", type=Path,
                        default=Path("config/fh6_car_roster.json"))
    parser.add_argument("--ocr", type=Path, help="rows.jsonl from ocr_leaderboard_frames")
    parser.add_argument("--parquet", type=Path, help="the same board read from memory")
    parser.add_argument("--ids-out", type=Path,
                        default=Path("config/fh6_car_id_names.json"))
    args = parser.parse_args(argv)

    cars = load_roster(args.html)
    args.roster_out.parent.mkdir(parents=True, exist_ok=True)
    args.roster_out.write_text(json.dumps(cars, indent=1), encoding="utf-8")
    makes = Counter(car["make"] for car in cars)
    print(f"roster: {len(cars)} cars, {len(makes)} makes, "
          f"{sum(1 for car in cars if car['year'])} with a year -> {args.roster_out}")

    if not args.ocr or not args.parquet:
        return 0

    index = build_index(cars)
    keys = list(index)
    year_index = build_year_index(cars)

    # Joined on LAP TIME, not rank. The two readings are hours or days apart and a
    # single new posted time shifts every rank below it, while a lap time belongs to its
    # entry for good. Milliseconds make it specific enough to be a key.
    ocr_by_time: dict[int, str] = {}
    for line in args.ocr.read_text(encoding="utf-8").splitlines():
        if not line.strip():
            continue
        row = json.loads(line)
        name = row.get("car_name")
        lap = row.get("lap_time_seconds")
        name = clean_ocr_name(name) if name else ""
        if name and len(name) >= 3 and isinstance(lap, (int, float)):
            ocr_by_time[int(round(float(lap) * 1000))] = name

    import pyarrow.parquet as pq

    table = pq.read_table(args.parquet, columns=["rank", "car_id", "lap_time_seconds"])
    mem_by_time: dict[int, int] = {}
    for car_id, lap in zip(table.column("car_id").to_pylist(),
                           table.column("lap_time_seconds").to_pylist()):
        if car_id is None or lap is None:
            continue
        key = int(round(float(lap) * 1000))
        # A time shared by two different cars cannot name either of them.
        if key in mem_by_time and mem_by_time[key] != int(car_id):
            mem_by_time[key] = -1
        else:
            mem_by_time.setdefault(key, int(car_id))

    shared = sorted(key for key in set(ocr_by_time) & set(mem_by_time)
                    if mem_by_time[key] != -1)
    print(f"lap times read both ways: {len(shared)} "
          f"(of {len(ocr_by_time)} OCR, {len(mem_by_time)} memory)")

    # Vote per carId: one rank could be misread, a hundred ranks of the same car cannot.
    votes: dict[int, Counter] = defaultdict(Counter)
    unmatched: Counter = Counter()
    for key in shared:
        car, score = match_car(ocr_by_time[key], index, keys, year_index)
        if car is None:
            unmatched[normalise(ocr_by_time[key])] += 1
            continue
        # Keyed by name AND year: the roster holds six "Honda Civic Type R", so a name
        # alone cannot identify one, and looking it up later picked whichever came last.
        votes[mem_by_time[key]][(car["name"], car["year"])] += 1

    resolved = {}
    for car_id, counter in votes.items():
        (name, year), count = counter.most_common(1)[0]
        total = sum(counter.values())
        # A carId whose ranks disagree is not named: the join is only as good as its
        # agreement, and a confident wrong name is worse than none.
        if count / total >= 0.6 and total >= 2:
            resolved[car_id] = {"name": name, "year": year,
                                "votes": count, "samples": total}

    # Merge rather than overwrite: each board fields different cars, so coverage is
    # built up run by run. A later run only replaces an id when it saw more of it.
    existing: dict[str, dict] = {}
    if args.ids_out.exists():
        try:
            existing = json.loads(args.ids_out.read_text(encoding="utf-8")).get("by_car_id", {})
        except Exception:
            existing = {}
    added = 0
    for car_id, info in resolved.items():
        key = str(car_id)
        previous = existing.get(key)
        if previous is None:
            added += 1
            existing[key] = info
        elif info["samples"] > previous.get("samples", 0):
            existing[key] = info
    payload = {"source": "forza.net/fh6cars joined to memory car_id by lap time",
               "by_car_id": {key: existing[key] for key in sorted(existing, key=int)}}
    args.ids_out.parent.mkdir(parents=True, exist_ok=True)
    args.ids_out.write_text(json.dumps(payload, indent=1), encoding="utf-8")
    print(f"named {len(resolved)} car id(s) of {len(votes)} seen this run; "
          f"+{added} new, {len(existing)} total -> {args.ids_out}")
    if unmatched:
        print("unmatched OCR names (top):",
              ", ".join(f"{name!r}x{count}" for name, count in unmatched.most_common(8)))
    for car_id, info in list(sorted(resolved.items()))[:10]:
        print(f"   {car_id:>5} -> {info['name']} ({info['year']})  "
              f"({info['votes']}/{info['samples']})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
