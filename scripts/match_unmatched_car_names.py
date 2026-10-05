from __future__ import annotations

import argparse
import html
import io
import json
import re
import sys
import time
import unicodedata
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path
from typing import Any

import pandas as pd
import requests
from rapidfuzz import fuzz, process


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_INPUT = ROOT / "data" / "analytics" / "unmatched_car_names.tsv"
DEFAULT_OUTPUT = ROOT / "data" / "analytics" / "unmatched_car_names_matched.tsv"
DEFAULT_REVIEW = ROOT / "data" / "analytics" / "unmatched_car_names_match_review.tsv"
DEFAULT_ALIAS_OUTPUT = ROOT / "data" / "analytics" / "fh6_car_aliases.tsv"
DEFAULT_CACHE_DIR = ROOT / "data" / "cache" / "car_matching"

FORZA_CARS_URL = "https://forza.net/fh6cars"
FANDOM_API = "https://forza.fandom.com/api.php"


MANUFACTURER_ALIASES = {
    "a-b": "abarth",
    "alumi craft": "alumicraft",
    "amg td": "amg transport dynamics",
    "aston": "aston martin",
    "austin healey": "austin-healey",
    "chevy": "chevrolet",
    "chev": "chevrolet",
    "formula d": "formula drift",
    "holden hsv": "hsv",
    "hummer": "gmc",
    "jeep": "jeep",
    "lambo": "lamborghini",
    "m b": "mercedes benz",
    "m-b": "mercedes benz",
    "mb": "mercedes benz",
    "merc": "mercedes benz",
    "merce": "mercedes benz",
    "mini": "mini",
    "mb amg": "mercedes amg",
    "vw": "volkswagen",
}

ROMAN_OCR_FIXES = {
    "mkii": "mk2",
    "mk ii": "mk2",
    "mki1": "mk2",
    "mkil": "mk2",
    "mkll": "mk2",
    "mkiii": "mk3",
    "mk iii": "mk3",
    "mkili": "mk3",
    "mkill": "mk3",
    "mkilll": "mk3",
}

TOKEN_NOISE = {
    "driver",
    "car",
    "class",
    "rank",
    "time",
    "abs",
    "tcs",
    "stm",
    "gear",
    "clean",
    "dirty",
}

OCR_PREFIX_NOISE = {
    "b",
    "bb",
    "be",
    "bee",
    "bf",
    "bh",
    "bi",
    "bl",
    "bp",
    "by",
    "cl",
    "ee",
    "eee",
    "ei",
    "el",
    "ell",
    "gall",
    "gee",
    "geez",
    "gree",
    "i",
    "ii",
    "il",
    "ll",
    "sl",
    "tt",
    "ug",
    "ww",
    "zl",
}

MAKE_HINTS = {
    "abarth": "Abarth",
    "acura": "Acura",
    "alfa romeo": "Alfa Romeo",
    "alumi craft": "Alumicraft",
    "alumicraft": "Alumicraft",
    "amg": "Mercedes-AMG",
    "aston martin": "Aston Martin",
    "audi": "Audi",
    "bmw": "BMW",
    "chevrolet": "Chevrolet",
    "chevy": "Chevrolet",
    "dodge": "Dodge",
    "ferrari": "Ferrari",
    "ford": "Ford",
    "honda": "Honda",
    "hyundai": "Hyundai",
    "jaguar": "Jaguar",
    "jeep": "Jeep",
    "lambo": "Lamborghini",
    "lamborghini": "Lamborghini",
    "maserati": "Maserati",
    "mazda": "Mazda",
    "mclaren": "McLaren",
    "mercedes amg": "Mercedes-AMG",
    "mercedes benz": "Mercedes-Benz",
    "m b": "Mercedes-Benz",
    "mini": "MINI",
    "mit": "Mitsubishi",
    "mitsubishi": "Mitsubishi",
    "nissan": "Nissan",
    "pagani": "Pagani",
    "peugeot": "Peugeot",
    "polaris": "Polaris",
    "porsche": "Porsche",
    "subaru": "Subaru",
    "susaru": "Subaru",
    "toyota": "Toyota",
    "volkswagen": "Volkswagen",
    "vw": "Volkswagen",
}

KEY_MODEL_TOKENS = {
    "22b",
    "aventador",
    "camaro",
    "cayman",
    "celica",
    "chaser",
    "city",
    "civic",
    "countach",
    "diablo",
    "evo",
    "fiesta",
    "focus",
    "gallardo",
    "golf",
    "huracan",
    "impreza",
    "lancer",
    "legacy",
    "miata",
    "mustang",
    "revuelto",
    "sian",
    "sterrato",
    "temerario",
    "wrx",
}

FAMILY_YEAR_PATTERNS = [
    ("A 45", re.compile(r"^A 45\b", re.I)),
    ("Aventador", re.compile(r"^Aventador\b", re.I)),
    ("Bronco", re.compile(r".*\bBronco\b", re.I)),
    ("C 63", re.compile(r"^C 63\b", re.I)),
    ("Camaro", re.compile(r"^Camaro\b", re.I)),
    ("Celica", re.compile(r"^Celica\b", re.I)),
    ("Chaser", re.compile(r"^Chaser\b", re.I)),
    ("City E", re.compile(r"^City E\b", re.I)),
    ("Civic", re.compile(r"^Civic\b", re.I)),
    ("Corolla", re.compile(r"^Corolla\b", re.I)),
    ("Corvette", re.compile(r"^Corvette\b", re.I)),
    ("Countach", re.compile(r"^Countach\b", re.I)),
    ("F-150", re.compile(r".*\bF-150\b", re.I)),
    ("Fiesta", re.compile(r".*\bFiesta\b", re.I)),
    ("Focus", re.compile(r".*\bFocus\b", re.I)),
    ("G 65", re.compile(r"^G 65\b", re.I)),
    ("Golf R", re.compile(r"^Golf R\b", re.I)),
    ("GT Black", re.compile(r"^GT Black\b", re.I)),
    ("GT-R", re.compile(r".*\bGT-R\b", re.I)),
    ("Huracán", re.compile(r"^Hurac[áa]n\b", re.I)),
    ("Lancer", re.compile(r"^Lancer\b", re.I)),
    ("MX-5", re.compile(r"^MX-5\b", re.I)),
    ("Mustang", re.compile(r".*\bMustang\b", re.I)),
    ("RS 3", re.compile(r"^RS 3\b", re.I)),
    ("RS 7", re.compile(r"^RS 7\b", re.I)),
    ("SL65", re.compile(r"^SL 65\b", re.I)),
    ("WRX", re.compile(r".*\bWRX\b", re.I)),
]

SOURCE_PRIORITY = {
    "wiki_abbreviated_as": 5,
    "forza_net_car_name": 4,
    "forza_net_yearless": 3,
    "wiki_title": 3,
    "wiki_model": 2,
    "forza_net_model": 2,
    "generated_family_year": 1,
}


def repair_mojibake(value: Any) -> Any:
    if not isinstance(value, str):
        return value
    if "Ã" not in value and "â" not in value:
        return value
    try:
        return value.encode("latin1").decode("utf-8")
    except UnicodeError:
        return value


@dataclass
class Candidate:
    car_name: str
    make: str
    car_class: str
    alias: str
    source: str
    alias_norm: str
    car_norm: str
    model_norm: str
    tokens: set[str] = field(default_factory=set)


def read_text(path: Path) -> str | None:
    if not path.exists():
        return None
    return path.read_text(encoding="utf-8")


def write_text(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")


def cached_get(session: requests.Session, url: str, cache_path: Path, *, params: dict[str, Any] | None = None) -> str:
    cache_key = url
    if params:
        cache_key += "?" + "&".join(f"{k}={params[k]}" for k in sorted(params))
    cached = read_text(cache_path)
    if cached:
        return cached
    response = session.get(url, params=params, timeout=45)
    response.raise_for_status()
    text = response.content.decode("utf-8", errors="replace")
    write_text(cache_path, text)
    return text


def strip_accents(value: str) -> str:
    value = unicodedata.normalize("NFKD", value)
    return "".join(ch for ch in value if not unicodedata.combining(ch))


def normalize(value: Any) -> str:
    if value is None or (isinstance(value, float) and pd.isna(value)):
        return ""
    text = html.unescape(str(value))
    text = text.replace("\ufffd", " ")
    text = strip_accents(text).casefold()
    text = text.replace("&", " and ")
    text = re.sub(r"[‘’´`]", "'", text)
    text = re.sub(r"[“”]", '"', text)
    text = re.sub(r"(?<=\w)['\"](?=\d{2}\b)", " ", text)
    text = text.replace("/", " ")
    text = text.replace("+", " plus ")
    text = text.replace("#", " ")
    text = re.sub(r"[^a-z0-9]+", " ", text)
    text = re.sub(r"\bsusaru\b", "subaru", text)
    text = re.sub(r"\bcity\s+e?1?184\b", "city e ii 84", text)
    text = re.sub(r"\bcity\s+e\s+1?184\b", "city e ii 84", text)
    text = re.sub(r"\bcity\s+e\s+184\b", "city e ii 84", text)
    text = re.sub(r"\b6508\b", "650s", text)
    text = re.sub(r"\b6505\b", "650s", text)
    text = re.sub(r"\bferrari\s+fao\b", "ferrari f40", text)
    text = re.sub(r"\bferrari\s+f80\s+\d{2}\b", "ferrari f80", text)
    text = re.sub(r"\bang\s+gt\b", "amg gt", text)
    text = re.sub(r"\bama\s+one\b", "amg one", text)
    text = re.sub(r"\bl\s+(aventador|huracan|countach|sian|gallardo|urus|diablo|murcielago|revuelto|temerario)\b", r"lamborghini \1", text)
    text = re.sub(r"\bme\s+mx\s+5\b", "mazda mx 5", text)
    text = re.sub(r"\bme\s+rx\b", "mazda rx", text)
    text = re.sub(r"\bsidn\s*r?\b", "sian roadster", text)
    text = re.sub(r"\bsien\s*r?\b", "sian roadster", text)
    text = re.sub(r"\bhuayra\s+r\s+24\b", "huayra r", text)
    text = re.sub(r"\bsubaru\s+22\s+98\b", "subaru 22b 98", text)
    text = re.sub(r"\bsubaru\s+22b\s+98\b", "subaru 22b 98", text)
    text = re.sub(r"\balfa\s+romeo\s+(?:1z2|122)\b", "alfa romeo tz2", text)
    text = re.sub(r"\babarth\s+12417\b", "abarth 124 17", text)
    text = re.sub(r"\bbmw\s+24\s+19\b", "bmw z4 19", text)
    text = re.sub(r"\bbmw\s+24\b", "bmw z4", text)
    text = re.sub(r"\bbmw\s+m[ilt]\b", "bmw m 1", text)
    text = re.sub(r"\bbmw\s+mit\b", "bmw m 1", text)
    text = re.sub(r"\bhonda\s+civic\s+16\b", "honda civic 86", text)
    text = re.sub(r"\bford\s+focus\s+i7\b", "ford focus 17", text)
    text = re.sub(r"\bvii\s*l\b", "viii", text)
    text = re.sub(r"\bvill\b", "viii", text)
    text = re.sub(r"\bm\s+bg\b", "m b g", text)
    text = re.sub(r"\bm\s+ba\b", "m b a", text)
    text = re.sub(r"\bm\s+ba45\b", "m b a45", text)
    text = re.sub(r"\bm\s+bc63\b", "m b c63", text)
    text = re.sub(r"\bm\s+bsl\b", "m b sl", text)
    text = re.sub(r"\bm\s+bc\b", "m b c", text)
    text = re.sub(r"\bm\s+bclk\b", "m b clk", text)
    text = re.sub(r"\bm\s+bgt\b", "m b gt", text)
    text = re.sub(r"\bgt\s+418\b", "gt 4 18", text)
    text = re.sub(r"\br8(\d{2})\b", r"r8 \1", text)
    text = re.sub(r"\b0(?=[a-z])", "o", text)
    text = re.sub(r"(?<=[a-z])0\b", "o", text)
    text = re.sub(r"\b([a-z]{2,})1\b", r"\1i", text)
    text = re.sub(r"\b([a-z]{2,})5\b", r"\1s", text)
    text = re.sub(r"\bgti\b", "gti", text)
    text = re.sub(r"\bgtr\b", "gt r", text)
    text = re.sub(r"\bg t r\b", "gt r", text)
    text = re.sub(r"\brs(\d+)\b", r"rs \1", text)
    text = re.sub(r"\bm(\d+)\b", r"m \1", text)
    text = re.sub(r"\bc(\d+)\b", r"c \1", text)
    text = re.sub(r"\ba(\d+)\b", r"a \1", text)
    text = re.sub(r"\bg(\d+)\b", r"g \1", text)
    text = re.sub(r"\bsl(\d+)\b", r"sl \1", text)
    text = re.sub(r"\bgt(\d+)\b", r"gt \1", text)
    text = re.sub(r"\b(\d{2})ae\b", r"\1 ae", text)
    text = re.sub(r"\bmk\s*([23])\b", r"mk\1", text)
    text = re.sub(r"\s+", " ", text).strip()
    parts = text.split()
    while parts and parts[0] in OCR_PREFIX_NOISE:
        parts.pop(0)
    text = " ".join(parts)
    for bad, good in ROMAN_OCR_FIXES.items():
        text = re.sub(rf"\b{re.escape(bad)}\b", good, text)
    for bad, good in sorted(MANUFACTURER_ALIASES.items(), key=lambda item: len(item[0]), reverse=True):
        text = re.sub(rf"\b{re.escape(bad)}\b", good, text)
    text = re.sub(r"\bvolkswagen\s+golf\s+r\s+24\b", "volkswagen golf r 22", text)
    text = re.sub(r"\s+", " ", text).strip()
    return text


def tokens(value: str) -> set[str]:
    return {token for token in normalize(value).split() if token not in TOKEN_NOISE and len(token) > 1}


def without_year(name: str) -> str:
    return re.sub(r"^\s*(19|20|25)\d{2}\s+", "", name).strip()


def model_only(car_name: str, make: str) -> str:
    value = without_year(car_name)
    if value.casefold().startswith(make.casefold() + " "):
        value = value[len(make) + 1 :]
    return value.strip()


def generated_family_year_aliases(car_name: str, make: str) -> list[str]:
    year = leading_year(car_name)
    if not year:
        return []
    short_year = year[-2:]
    model = model_only(car_name, make)
    aliases: list[str] = []
    for family, pattern in FAMILY_YEAR_PATTERNS:
        if pattern.match(model):
            aliases.append(f"{make} {family} '{short_year}")
            if make == "Volkswagen":
                aliases.append(f"VW {family} '{short_year}")
            elif make == "Chevrolet":
                aliases.append(f"Chevy {family} '{short_year}")
            elif make == "Lamborghini":
                aliases.append(f"L. {family} '{short_year}")
                aliases.append(f"Lambo {family} '{short_year}")
            elif make == "Mercedes-Benz":
                aliases.append(f"M-B {family} '{short_year}")
            elif make == "Mercedes-AMG":
                aliases.append(f"M-B {family} '{short_year}")
                aliases.append(f"AMG {family} '{short_year}")
            elif make == "Mitsubishi":
                aliases.append(f"Mit. {family} '{short_year}")
            break
    return aliases


def load_forza_cars(cache_dir: Path) -> pd.DataFrame:
    cache_dir.mkdir(parents=True, exist_ok=True)
    cache_path = cache_dir / "fh6cars.html"
    session = requests.Session()
    html_text = cached_get(session, FORZA_CARS_URL, cache_path)
    tables = pd.read_html(io.StringIO(html_text))
    if not tables:
        raise RuntimeError(f"No table found at {FORZA_CARS_URL}")
    frame = tables[0].copy()
    required = {"Make", "Car Name", "Car Type", "Car Class"}
    missing = sorted(required - set(frame.columns))
    if missing:
        raise RuntimeError(f"FH6 car table is missing columns: {missing}")
    frame = frame.dropna(subset=["Car Name"]).reset_index(drop=True)
    for column in ["Make", "Car Name", "Car Type", "Car Class", "Country", "Collection", "Add-Ons"]:
        if column in frame.columns:
            frame[column] = frame[column].map(repair_mojibake)
    frame["Car Name"] = frame["Car Name"].astype(str).str.strip()
    frame["Make"] = frame["Make"].astype(str).str.strip()
    frame["model_only"] = frame.apply(lambda row: model_only(row["Car Name"], row["Make"]), axis=1)
    frame["official_norm"] = frame["Car Name"].map(normalize)
    frame["yearless_norm"] = frame.apply(lambda row: normalize(without_year(row["Car Name"])), axis=1)
    frame["model_norm"] = frame["model_only"].map(normalize)
    return frame


def fandom_query(session: requests.Session, cache_dir: Path, params: dict[str, Any], cache_name: str) -> dict[str, Any]:
    cache_path = cache_dir / f"{cache_name}.json"
    cached = read_text(cache_path)
    if cached:
        return json.loads(cached)
    response = session.get(FANDOM_API, params=params, timeout=45)
    response.raise_for_status()
    data = response.json()
    write_text(cache_path, json.dumps(data, ensure_ascii=False, indent=2))
    time.sleep(0.15)
    return data


def load_fh6_wiki_titles(cache_dir: Path) -> list[str]:
    session = requests.Session()
    titles: list[str] = []
    params: dict[str, Any] = {
        "action": "query",
        "list": "categorymembers",
        "cmtitle": "Category:Cars (FH6)",
        "cmnamespace": "0",
        "cmlimit": "500",
        "format": "json",
    }
    page = 0
    while True:
        data = fandom_query(session, cache_dir, params, f"fandom_category_cars_fh6_{page:03d}")
        titles.extend(member["title"] for member in data.get("query", {}).get("categorymembers", []))
        cont = data.get("continue")
        if not cont:
            break
        params.update(cont)
        page += 1
    return titles


def load_wiki_pages(cache_dir: Path, titles: list[str]) -> dict[str, str]:
    session = requests.Session()
    pages: dict[str, str] = {}
    for index in range(0, len(titles), 50):
        batch = titles[index : index + 50]
        params = {
            "action": "query",
            "prop": "revisions",
            "titles": "|".join(batch),
            "rvprop": "content",
            "rvslots": "main",
            "format": "json",
        }
        data = fandom_query(session, cache_dir, params, f"fandom_pages_{index:04d}_{index + len(batch):04d}")
        for page in data.get("query", {}).get("pages", {}).values():
            title = page.get("title", "")
            revisions = page.get("revisions") or []
            if not title or not revisions:
                continue
            content = revisions[0].get("slots", {}).get("main", {}).get("*", "")
            if content:
                pages[title] = content
    return pages


def clean_wiki_value(value: str) -> str:
    value = html.unescape(value)
    value = re.sub(r"<[^>]+>", " ", value)
    value = re.sub(r"\[\[([^|\]]+)\|([^\]]+)\]\]", r"\2", value)
    value = re.sub(r"\[\[([^\]]+)\]\]", r"\1", value)
    value = re.sub(r"\{\{[^{}]*\}\}", " ", value)
    value = value.replace("''", "")
    value = re.sub(r"\s+", " ", value)
    return value.strip()


def parse_infobox_value(wikitext: str, key: str) -> str:
    pattern = re.compile(rf"^\s*\|{re.escape(key)}\s*=\s*(.*?)\s*$", re.I | re.M)
    match = pattern.search(wikitext)
    if not match:
        return ""
    return clean_wiki_value(match.group(1))


def parse_aliases(wikitext: str) -> list[str]:
    aliases: list[str] = []
    cleaned = clean_wiki_value(wikitext)
    for match in re.finditer(r"abbreviated as\s+", cleaned, re.I):
        segment = cleaned[match.end() : match.end() + 520]
        for terminator in [" - is ", " - are ", ". ", ", is ", ", are ", " and previously known as ", " but referred to "]:
            pos = segment.casefold().find(terminator)
            if pos >= 0:
                segment = segment[:pos]
                break
        quoted = re.findall(r'"([^"]{2,80})"', segment)
        if not quoted:
            quoted = re.findall(r"“([^”]{2,80})”", segment)
        for alias in quoted:
            alias = clean_wiki_value(alias)
            if alias and alias not in aliases:
                aliases.append(alias)
    return aliases


def leading_year(value: str) -> str:
    match = re.match(r"\s*((?:19|20|25)\d{2})\b", value)
    return match.group(1) if match else ""


def title_year(value: str) -> str:
    match = re.search(r"\(((?:19|20|25)\d{2})\)", value)
    return match.group(1) if match else ""


def best_official_for_wiki_page(title: str, year: str, manufacturer: str, model: str, cars: pd.DataFrame) -> tuple[int | None, float]:
    title_norm = normalize(title)
    year_title_norm = normalize(f"{year} {title}") if year else title_norm
    model_norm = normalize(model)
    make_model_norm = normalize(f"{manufacturer} {model}") if manufacturer and model else title_norm
    manufacturer_norm = normalize(manufacturer)
    title_year_value = title_year(title) or year

    best_index: int | None = None
    best_score = -1.0
    for index, row in cars.iterrows():
        make_norm = normalize(row["Make"])
        if manufacturer_norm:
            make_ok = manufacturer_norm == make_norm or manufacturer_norm in make_norm or make_norm in manufacturer_norm
            if not make_ok:
                continue
        scores = [
            fuzz.WRatio(title_norm, row["yearless_norm"]),
            fuzz.WRatio(year_title_norm, row["official_norm"]),
            fuzz.WRatio(make_model_norm, row["yearless_norm"]),
            fuzz.WRatio(model_norm, row["model_norm"]) if model_norm else 0,
        ]
        score = max(scores)
        row_year = leading_year(row["Car Name"])
        if title_year_value and row_year and title_year_value != row_year:
            score -= 18
        if title_year_value and row_year and title_year_value == row_year:
            score += 8
        if model_norm and not (set(model_norm.split()) & set(row["model_norm"].split())):
            score -= 15
        if score > best_score:
            best_index = int(index)
            best_score = float(score)
    return best_index, best_score


def build_candidates(cars: pd.DataFrame, pages: dict[str, str]) -> tuple[list[Candidate], pd.DataFrame]:
    wiki_records: list[dict[str, Any]] = []
    aliases_by_car: dict[str, list[tuple[str, str]]] = {car: [] for car in cars["Car Name"]}

    for title, text in pages.items():
        year = parse_infobox_value(text, "year")
        manufacturer = parse_infobox_value(text, "manufacturer")
        model = parse_infobox_value(text, "model")
        aliases = parse_aliases(text)
        official_index, score = best_official_for_wiki_page(title, year, manufacturer, model, cars)
        official_name = ""
        make = ""
        car_class = ""
        if official_index is not None and score >= 90:
            row = cars.iloc[official_index]
            official_name = row["Car Name"]
            make = row["Make"]
            car_class = row["Car Class"]
            for alias in aliases:
                aliases_by_car[official_name].append((alias, "wiki_abbreviated_as"))
            aliases_by_car[official_name].append((title, "wiki_title"))
            if model:
                aliases_by_car[official_name].append((model, "wiki_model"))
        wiki_records.append(
            {
                "wiki_title": title,
                "wiki_year": year,
                "wiki_manufacturer": manufacturer,
                "wiki_model": model,
                "wiki_aliases": "; ".join(aliases),
                "matched_fh6_car_name": official_name,
                "matched_make": make,
                "matched_car_class": car_class,
                "official_match_score": round(score, 2),
            }
        )

    candidates: list[Candidate] = []
    seen: set[tuple[str, str]] = set()
    for _, row in cars.iterrows():
        car_name = row["Car Name"]
        make = row["Make"]
        car_class = row["Car Class"]
        base_aliases = [
            (row["Car Name"], "forza_net_car_name"),
            (without_year(row["Car Name"]), "forza_net_yearless"),
            (row["model_only"], "forza_net_model"),
        ]
        base_aliases.extend(aliases_by_car.get(car_name, []))
        base_aliases.extend((alias, "generated_family_year") for alias in generated_family_year_aliases(row["Car Name"], make))
        for alias, source in base_aliases:
            alias = clean_wiki_value(str(alias)).strip()
            alias_norm = normalize(alias)
            if len(alias_norm) < 2:
                continue
            key = (car_name, alias_norm)
            if key in seen:
                continue
            seen.add(key)
            candidates.append(
                Candidate(
                    car_name=car_name,
                    make=make,
                    car_class=car_class,
                    alias=alias,
                    source=source,
                    alias_norm=alias_norm,
                    car_norm=row["official_norm"],
                    model_norm=row["model_norm"],
                    tokens=tokens(alias),
                )
            )
    return candidates, pd.DataFrame(wiki_records)


def candidate_score(query_norm: str, query_tokens: set[str], candidate: Candidate) -> float:
    if not query_norm:
        return 0.0
    alias_score = fuzz.WRatio(query_norm, candidate.alias_norm)
    token_score = fuzz.token_set_ratio(query_norm, candidate.alias_norm)
    partial_score = fuzz.partial_ratio(query_norm, candidate.alias_norm)
    score = 0.58 * alias_score + 0.25 * token_score + 0.17 * partial_score

    if query_norm == candidate.alias_norm:
        score += 25
    elif query_norm in {candidate.model_norm, candidate.car_norm}:
        score += 15

    shared = query_tokens & candidate.tokens
    query_digits = {token for token in query_tokens if token.isdigit()}
    candidate_digits = {token for token in candidate.tokens if token.isdigit()}
    if shared:
        score += min(8, 2 * len(shared))
    if query_digits:
        digit_hits = query_digits & candidate_digits
        if digit_hits:
            score += min(10, 5 * len(digit_hits))
        else:
            score -= 12
        score -= min(20, 7 * len(query_digits - candidate_digits))
    critical_query_tokens = query_tokens & KEY_MODEL_TOKENS
    critical_candidate_tokens = candidate.tokens & KEY_MODEL_TOKENS
    if critical_query_tokens and not (critical_query_tokens & critical_candidate_tokens):
        score -= 22
    for fragment in ["m 4", "r8", "rs 3", "rs 7", "gt black", "gt r", "g 65", "a 45", "c 63", "sl 65"]:
        if fragment in query_norm and fragment not in candidate.alias_norm and fragment not in candidate.model_norm:
            score -= 12
    if candidate.alias_norm.startswith(query_norm) or query_norm.startswith(candidate.alias_norm):
        score += 5
    if candidate.source == "wiki_abbreviated_as":
        score += 5
    if not shared and len(query_tokens) > 1:
        score -= 10
    return min(score, 100.0)


def rank_candidates(name: str, candidates: list[Candidate], limit: int = 5) -> list[tuple[Candidate, float]]:
    query_norm = normalize(name)
    query_tokens = tokens(name)
    alias_choices = [candidate.alias_norm for candidate in candidates]
    shortlist_norms = process.extract(
        query_norm,
        alias_choices,
        scorer=fuzz.WRatio,
        limit=80,
    )
    scored: list[tuple[Candidate, float]] = []
    seen_cars: set[str] = set()
    for _, _, alias_index in shortlist_norms:
        candidate = candidates[alias_index]
        score = candidate_score(query_norm, query_tokens, candidate)
        key = candidate.car_name
        if key in seen_cars:
            continue
        seen_cars.add(key)
        scored.append((candidate, score))
    scored.sort(key=lambda item: (item[1], SOURCE_PRIORITY.get(item[0].source, 0)), reverse=True)
    return scored[:limit]


def apply_manual_safety_rules(name: str, ranked: list[tuple[Candidate, float]]) -> list[tuple[Candidate, float]]:
    norm = normalize(name)
    if not ranked:
        return ranked

    def boost(predicate: str, value: float) -> None:
        nonlocal ranked
        updated: list[tuple[Candidate, float]] = []
        for candidate, score in ranked:
            if predicate in candidate.car_norm or predicate in candidate.alias_norm or predicate in candidate.model_norm:
                score = min(100.0, score + value)
            updated.append((candidate, score))
        updated.sort(key=lambda item: item[1], reverse=True)
        ranked = updated

    if "countach" in norm:
        boost("countach", 12)
    if "huracan" in norm:
        boost("huracan", 12)
    if "gt r" in norm or "gtr" in norm:
        boost("gt r", 10)
    if "g 65" in norm or "g65" in norm:
        boost("g 65", 12)
    if "a 45" in norm or "a45" in norm:
        boost("a 45", 12)
    if "rs 3" in norm:
        boost("rs 3", 10)
    if "rs 7" in norm:
        boost("rs 7", 10)
    if "3000" in norm:
        boost("3000", 8)
    if "class 1" in norm or "122" in norm:
        boost("class 1", 8)
    if "ferrari f80" in norm:
        boost("ferrari f80", 18)
    return ranked


def detect_make_hint(name: str) -> str:
    norm = normalize(name)
    for prefix, make in sorted(MAKE_HINTS.items(), key=lambda item: len(item[0]), reverse=True):
        if norm == normalize(prefix) or norm.startswith(normalize(prefix) + " "):
            return make
    return ""


def make_matches_hint(candidate_make: str, hint: str) -> bool:
    if not hint:
        return True
    candidate_norm = normalize(candidate_make)
    hint_norm = normalize(hint)
    if candidate_norm == hint_norm:
        return True
    if hint_norm.startswith("mercedes") and candidate_norm.startswith("mercedes"):
        return True
    return False


def classify_match(name: str, ranked: list[tuple[Candidate, float]]) -> dict[str, Any]:
    ranked = apply_manual_safety_rules(name, ranked)
    if not ranked:
        return {
            "fh6_car_name": "",
            "fh6_make": "",
            "fh6_car_class": "",
            "match_alias": "",
            "match_source": "",
            "match_score": 0,
            "match_status": "unmatched",
            "alt_1": "",
            "alt_2": "",
        }

    norm = normalize(name)
    forced_car_name = {
        "ferrari f40": "1987 Ferrari F40",
    }.get(norm)
    if forced_car_name:
        forced = [(candidate, 100.0) for candidate, _score in ranked if candidate.car_name == forced_car_name]
        if forced:
            ranked = forced + [item for item in ranked if item[0].car_name != forced_car_name]

    best, score = ranked[0]
    make_hint = detect_make_hint(name)
    if make_hint and not make_matches_hint(best.make, make_hint):
        same_make = [(candidate, candidate_score) for candidate, candidate_score in ranked if make_matches_hint(candidate.make, make_hint)]
        if same_make and same_make[0][1] >= score - 10:
            best, score = same_make[0]
            ranked = same_make + [item for item in ranked if item[0].car_name != best.car_name]
        else:
            return {
                "fh6_car_name": "",
                "fh6_make": "",
                "fh6_car_class": "",
                "match_alias": "",
                "match_source": "",
                "match_score": round(score, 2),
                "match_status": "review",
                "alt_1": f"{ranked[0][0].car_name} ({ranked[0][1]:.1f})" if ranked else "",
                "alt_2": f"{ranked[1][0].car_name} ({ranked[1][1]:.1f})" if len(ranked) > 1 else "",
            }
    second_score = ranked[1][1] if len(ranked) > 1 else 0.0
    margin = score - second_score
    status = "matched"
    if score < 78:
        status = "review"
    elif score < 86 or margin < 2.5:
        status = "weak"
    elif margin < 6:
        status = "ambiguous"

    if normalize(name) == best.alias_norm:
        status = "matched"
        score = 100

    return {
        "fh6_car_name": best.car_name if status != "review" else "",
        "fh6_make": best.make if status != "review" else "",
        "fh6_car_class": best.car_class if status != "review" else "",
        "match_alias": best.alias if status != "review" else "",
        "match_source": best.source if status != "review" else "",
        "match_score": round(score, 2),
        "match_status": status,
        "alt_1": f"{ranked[1][0].car_name} ({ranked[1][1]:.1f})" if len(ranked) > 1 else "",
        "alt_2": f"{ranked[2][0].car_name} ({ranked[2][1]:.1f})" if len(ranked) > 2 else "",
    }


def write_backup(path: Path) -> Path:
    timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    backup = path.with_name(f"{path.stem}.before_match_{timestamp}{path.suffix}")
    backup.write_bytes(path.read_bytes())
    return backup


def main() -> int:
    parser = argparse.ArgumentParser(description="Match OCR leaderboard car display names to official FH6 car names.")
    parser.add_argument("--input", type=Path, default=DEFAULT_INPUT)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--review-output", type=Path, default=DEFAULT_REVIEW)
    parser.add_argument("--alias-output", type=Path, default=DEFAULT_ALIAS_OUTPUT)
    parser.add_argument("--cache-dir", type=Path, default=DEFAULT_CACHE_DIR)
    parser.add_argument("--update-input", action="store_true", help="Also overwrite the input TSV after creating a timestamped backup.")
    args = parser.parse_args()

    input_path = args.input
    output_path = args.output
    if not input_path.exists():
        raise FileNotFoundError(input_path)

    cars = load_forza_cars(args.cache_dir)
    wiki_titles = load_fh6_wiki_titles(args.cache_dir)
    wiki_pages = load_wiki_pages(args.cache_dir, wiki_titles)
    candidates, wiki_records = build_candidates(cars, wiki_pages)

    source = pd.read_csv(input_path, sep="\t", encoding="utf-8")
    required = {"bildschirmname", "zeilen", "boards", "vorschlag"}
    missing = sorted(required - set(source.columns))
    if missing:
        raise RuntimeError(f"Input TSV is missing columns: {missing}")

    matches = []
    total = len(source)
    for index, name in enumerate(source["bildschirmname"].astype(str), start=1):
        if index == 1 or index % 2500 == 0:
            print(f"[match] {index}/{total}", flush=True)
        ranked = rank_candidates(name, candidates)
        matches.append(classify_match(name, ranked))

    matched = pd.concat([source, pd.DataFrame(matches)], axis=1)
    matched["source_forza_net"] = FORZA_CARS_URL
    matched["source_wiki_api"] = "https://forza.fandom.com/wiki/Category:Cars_(FH6)"

    output_path.parent.mkdir(parents=True, exist_ok=True)
    matched.to_csv(output_path, sep="\t", index=False, encoding="utf-8")

    review = matched[matched["match_status"].isin(["review", "weak", "ambiguous"])].copy()
    review.sort_values(["match_status", "zeilen"], ascending=[True, False]).to_csv(
        args.review_output,
        sep="\t",
        index=False,
        encoding="utf-8",
    )

    alias_records = []
    for candidate in candidates:
        alias_records.append(
            {
                "fh6_car_name": candidate.car_name,
                "fh6_make": candidate.make,
                "fh6_car_class": candidate.car_class,
                "alias": candidate.alias,
                "alias_source": candidate.source,
                "alias_norm": candidate.alias_norm,
            }
        )
    pd.DataFrame(alias_records).drop_duplicates().to_csv(args.alias_output, sep="\t", index=False, encoding="utf-8")
    wiki_records.to_csv(args.cache_dir / "fandom_wiki_page_matches.tsv", sep="\t", index=False, encoding="utf-8")
    cars.to_csv(args.cache_dir / "forza_net_fh6cars.tsv", sep="\t", index=False, encoding="utf-8")

    backup = None
    if args.update_input:
        backup = write_backup(input_path)
        matched.to_csv(input_path, sep="\t", index=False, encoding="utf-8")

    summary = matched["match_status"].value_counts(dropna=False).to_dict()
    print(json.dumps(
        {
            "input": str(input_path),
            "output": str(output_path),
            "review_output": str(args.review_output),
            "alias_output": str(args.alias_output),
            "input_backup": str(backup) if backup else "",
            "rows": int(len(matched)),
            "fh6_official_cars": int(len(cars)),
            "wiki_fh6_pages": int(len(wiki_pages)),
            "candidate_aliases": int(len(candidates)),
            "match_status_counts": summary,
        },
        ensure_ascii=False,
        indent=2,
    ))
    return 0


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    raise SystemExit(main())
