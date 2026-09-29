"""Die Texte des Spiels, die die App auf dem Bildschirm sucht, in allen Spielsprachen.

    python scripts/extract_game_text.py [--tables <StringTables-Ordner>]

Schreibt `config/game_text.json`. Die App liest Strecken, Statusworte ("In
Progress", "Up Next") und die Kopfzeile einer Reihe vom Bildschirm -- bis
2026-09-29 nur auf Englisch. Ein Spieler mit spanischem Spiel sah weder
Streckenvorschau noch Autowahl: "Descenso del puente Rainbow" ist "Rainbow Bridge
Descent", nur wusste die App das nicht.

## Woher

Aus den Stringtabellen des Spiels: `media/Stripped/StringTables/<SPRACHE>.zip`,
darin `.str`-Dateien. Aufbau (gemessen an FH6, 2026-09-29): bei 0x94 die Zahl der
Eintraege, ab 0x98 je Eintrag ein Schluessel (u32) und ein Versatz (u32), danach
die Zeichenketten, UTF-8 und nullterminiert. Derselbe Schluessel steht in jeder
Sprache fuer denselben Text.

## Warum nach englischem Text und nicht nach Schluesseln

Die Schluessel koennen sich mit einem Update des Spiels aendern, der englische
Text der Strecken und Statusworte kaum. Gesucht wird darum der englische Text,
und dessen Schluessel fuehrt zu allen anderen Sprachen.

## Nur, was die App braucht

Streckennamen (kurz, ohne Satzzeichen am Ende) und eine Handvoll Worte des
Anmeldeschirms. Keine ganzen Tabellen.
"""
from __future__ import annotations

import argparse
import json
import re
import struct
import sys
import zipfile
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
ZIEL = WORKSPACE / "config" / "game_text.json"

# Dateien, in denen Streckennamen stehen.
STRECKEN_DATEIEN = ("CareerTrackInfo.str", "RivalsEventData.str")

# Worte, die die Leser auf dem Bildschirm suchen: Schluessel in game_text.json ->
# englischer Text, GENAU wie das Spiel ihn fuehrt. Gesucht wird in ALLEN Tabellen;
# steht derselbe englische Text an mehreren Stellen, zaehlen alle Uebersetzungen
# (dasselbe Wort kann je nach Stelle anders uebersetzt sein).
WORTE = {
    # Anmeldeschirm, Horizon Play, Rivals (RivalsScreenReader)
    "in_progress": "In Progress",
    "up_next": "Up Next",
    "joining": "Joining {0} {1}/{2}",
    "route_length": "Route Length:",
    "route_length_plain": "Route Length",
    "rivals": "Rivals",
    "routes": "Routes",
    "spec_racing": "Spec Racing",
    "laps": "LAPS",
    "km": "km",
    "event_sign_up": "Event Sign Up",
    "horizon_play": "Horizon Play",
    # My Cars (CarGridReader) und die Tune-Liste (TuneDeleter)
    "my_cars": "My Cars",
    "upgrades_tuning": "Upgrades & Tuning",
    "designs_paints": "Designs & Paints",
    "my_tuning_setups": "My Tuning Setups",
    "custom_tuning": "Custom Tuning",
    "date_created": "Date Created",
    "tuner_rank": "Tuner Rank",
    "trending": "TRENDING",
    "all_time_greats": "ALL TIME GREATS",
    "toggle_stats": "Toggle Stats",
    "select_an_action": "Select An Action",
    "get_in_car": "Get In Car",
    "file_options": "File Options",
    "delete_file": "Delete File",
    "delete": "Delete",
    "please_wait": "Please Wait",
    "yes": "Yes",
    "creator": "Creator",
    "current_car": "CURRENT CAR",
}

# Menues des Spiels, die die App in ihren eigenen Texten NENNT ("Settings > HUD and
# Gameplay", "My Cars"): je App-Sprache die Schreibweise des Spiels, damit die
# Uebersetzungen der App dasselbe Wort benutzen wie das Spiel (scripts/lang_names.py).
MENUE = ("My Cars", "Data Out", "Data Out IP Address", "Data Out IP Port", "HUD & Gameplay",
         "Settings", "Event Sign Up", "Upgrades & Tuning", "My Tuning Setups")

# App-Sprache -> Tabelle des Spiels. Ohne Tabelle (ro, th, vi, id, ms) zeigt das Spiel Englisch.
APP_SPRACHEN = {"de": "DE", "fr": "FR", "es": "ES", "it": "IT", "pt": "PT", "nl": "NL", "pl": "PL",
                "cs": "CZ", "da": "DK", "sv": "SV", "fi": "FI", "hu": "HU", "el": "EL", "tr": "TR",
                "ru": "RU", "ja": "JP", "ko": "KO", "zh-Hans": "CHS", "zh-Hant": "CHT", "zh": "CHS",
                "nb": "NO"}

# Saetze mit Platzhaltern: die App vergleicht nur den Teil vor dem ersten "{".
ANFAENGE = {
    "are_you_sure_delete": "Are you sure you want to delete this file?{0}{1}",
}


def tabelle(zip_pfad: Path, datei: str) -> dict[int, str]:
    """Eine .str-Datei: Schluessel -> Text."""
    b = zipfile.ZipFile(zip_pfad).read(datei)
    anzahl = struct.unpack_from("<I", b, 0x94)[0]
    if anzahl <= 0 or 0x98 + 8 * anzahl > len(b):
        raise ValueError(f"{zip_pfad.name}/{datei}: unerwarteter Aufbau")
    blob = 0x98 + 8 * anzahl
    raus: dict[int, str] = {}
    for i in range(anzahl):
        schluessel, off = struct.unpack_from("<II", b, 0x98 + 8 * i)
        a = blob + off
        e = b.find(b"\x00", a)
        if a >= len(b) or e < 0:
            continue
        raus[schluessel] = b[a:e].decode("utf-8", "replace").strip()
    return raus


def steam_bibliotheken() -> list[Path]:
    """Die Steam-Bibliotheken dieses Rechners (libraryfolders.vdf)."""
    raus = []
    for steam in (Path(r"C:\Program Files (x86)\Steam"), Path(r"C:\Program Files\Steam")):
        vdf = steam / "steamapps" / "libraryfolders.vdf"
        if not vdf.exists():
            continue
        for m in re.finditer(r'"path"\s+"([^"]+)"', vdf.read_text(encoding="utf-8", errors="replace")):
            raus.append(Path(m.group(1).replace("\\\\", "\\")))
    return raus


def finde_tabellen() -> Path | None:
    kandidaten = [b / "steamapps" / "common" / "ForzaHorizon6" for b in steam_bibliotheken()]
    kandidaten += [Path(r"C:\XboxGames\Forza Horizon 6\Content"), Path(r"C:\XboxGames\ForzaHorizon6\Content")]
    for k in kandidaten:
        t = k / "media" / "Stripped" / "StringTables"
        if (t / "EN.zip").exists():
            return t
    return None


def ist_name(text: str) -> bool:
    return 3 <= len(text) <= 60 and not text.endswith((".", "!", "?")) and "{" not in text


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--tables", type=Path, help="Ordner media/Stripped/StringTables des Spiels")
    args = ap.parse_args()
    ordner = args.tables or finde_tabellen()
    if ordner is None or not (ordner / "EN.zip").exists():
        print("Stringtabellen des Spiels nicht gefunden -- mit --tables angeben.")
        return 1
    sprachen = sorted(p.stem for p in ordner.glob("*.zip"))
    print(f"{len(sprachen)} Sprachen in {ordner}")

    # Strecken: englischer Name -> alle anderen Schreibweisen.
    strecken: dict[str, set[str]] = {}
    for datei in STRECKEN_DATEIEN:
        en = tabelle(ordner / "EN.zip", datei)
        namen = {k: v for k, v in en.items() if ist_name(v)}
        for sprache in sprachen:
            if sprache in ("EN", "GB"):
                continue
            andere = tabelle(ordner / f"{sprache}.zip", datei)
            for k, name in namen.items():
                ueber = andere.get(k, "").strip()
                if ueber and ueber != name and ist_name(ueber):
                    strecken.setdefault(name, set()).add(ueber)

    # Worte: ueber den englischen Text zu allen Fundstellen, dann in jede Sprache.
    dateien = sorted(zipfile.ZipFile(ordner / "EN.zip").namelist())
    englisch_je_datei: dict[str, dict[int, str]] = {}
    for datei in dateien:
        try:
            englisch_je_datei[datei] = tabelle(ordner / "EN.zip", datei)
        except Exception:
            continue

    def fundstellen(passt) -> list[tuple[str, int]]:
        return [(d, k) for d, t in englisch_je_datei.items() for k, v in t.items() if passt(v)]

    worte: dict[str, list[str]] = {}
    # Gross- und Kleinschreibung zaehlt nicht: "Select An Action" steht so im Spiel.
    ziele = {n: (lambda v, e=e: v.lower() == e.lower()) for n, e in {**WORTE, **ANFAENGE}.items()}
    cache: dict[tuple[str, str], dict[int, str]] = {}
    for name, passt in ziele.items():
        orte = fundstellen(passt)
        if not orte:
            print(f"  WARNUNG: kein Text fuer '{name}'")
            continue
        alle = {englisch_je_datei[d][k] for d, k in orte}
        for sprache in sprachen:
            for d, k in orte:
                if (sprache, d) not in cache:
                    try:
                        cache[(sprache, d)] = tabelle(ordner / f"{sprache}.zip", d)
                    except Exception:
                        cache[(sprache, d)] = {}
                if cache[(sprache, d)].get(k):
                    alle.add(cache[(sprache, d)][k])
        if name in ANFAENGE:
            alle = {a.split("{")[0].strip() for a in alle}
        worte[name] = sorted(a for a in alle if a)

    # Menuenamen je App-Sprache: die erste Fundstelle des englischen Textes.
    menue: dict[str, dict[str, str]] = {}
    for englisch in MENUE:
        orte = fundstellen(lambda v, e=englisch: v == e)
        if not orte:
            print(f"  WARNUNG: Menue '{englisch}' nicht gefunden")
            continue
        d, k = orte[0]
        for app, tab in APP_SPRACHEN.items():
            if tab not in sprachen:
                continue
            if (tab, d) not in cache:
                try:
                    cache[(tab, d)] = tabelle(ordner / f"{tab}.zip", d)
                except Exception:
                    cache[(tab, d)] = {}
            wert = cache[(tab, d)].get(k, "").replace("\u0268", "i").replace("\u0197", "I")
            if wert:
                menue.setdefault(app, {})[englisch] = wert

    daten = {
        "format": "fhc-game-text-1",
        "_": "Aus den Stringtabellen des Spiels (scripts/extract_game_text.py). Strecken: englischer "
             "Name -> Schreibweisen der anderen Spielsprachen. Worte: alle Schreibweisen.",
        "languages": sprachen,
        "routes": {k: sorted(v) for k, v in sorted(strecken.items())},
        "words": worte,
        "menu_terms": menue,
    }
    ZIEL.write_text(json.dumps(daten, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"{ZIEL}: {len(strecken)} Streckennamen, {len(worte)} Worte")
    return 0


if __name__ == "__main__":
    sys.exit(main())
