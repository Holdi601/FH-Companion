"""Die im Spiel angezeigten Kurznamen aus dem Forza-Wiki holen.

WOZU: der Zuordner raet sonst, welches Auto ein gekuerzter Bildschirmname meint. Das
ging monatelang schief -- "Acura Integra" landete im Nichts, "Corvette 15" bei einer
1953er, "Mazda RX-8" bei einem Formula-Drift-Rennwagen. Das Wiki nennt die Kurzform
je Auto ausdruecklich, im Fliesstext der Autoseite:

    The 2001 '''Acura Integra Type R''' - abbreviated as "Acura Integra" - is ...

Das ist eine BELEGTE Zuordnung, keine Aehnlichkeitsrechnung. Ein Auto kann mehrere
Kurzformen haben ("Abarth 124 '17" und "Fiat Spider 124"), und mehrere Autos koennen
sich eine teilen -- solche Faelle werden hier festgehalten, nicht aufgeloest, denn
zwischen zwei belegten Kandidaten zu waehlen ist wieder Raten.

    python scripts/fetch_wiki_abbreviations.py
    python scripts/fetch_wiki_abbreviations.py --limit 60    # nur anlesen

Ergebnis: config/fh6_wiki_abbreviations.json
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import time
import urllib.parse
import urllib.request
from pathlib import Path

for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

WORKSPACE = Path(__file__).resolve().parent.parent
API = "https://forza.fandom.com/api.php"
KOPF = {"User-Agent": "Mozilla/5.0 (forza-rivals-dataset; contact via repo)"}

# "abbreviated as \"A\" and \"B\"" -- alle Anfuehrungszeichen dahinter einsammeln.
ABK_BLOCK = re.compile(r'abbreviated as ((?:"[^"]{1,60}"(?:\s+and\s+)?)+)', re.IGNORECASE)
IN_ANFUEHRUNG = re.compile(r'"([^"]{1,60})"')
# "The 2001 '''Acura Integra Type R'''"
KOPFZEILE = re.compile(r"The (\d{4})\s+'''([^']{2,80})'''")


def hole(parameter: dict) -> dict:
    adresse = API + "?" + urllib.parse.urlencode(parameter)
    anfrage = urllib.request.Request(adresse, headers=KOPF)
    with urllib.request.urlopen(anfrage, timeout=90) as antwort:
        return json.loads(antwort.read().decode("utf-8"))


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path,
                        default=WORKSPACE / "config/fh6_wiki_abbreviations.json")
    parser.add_argument("--limit", type=int, default=0,
                        help="hoechstens so viele Seiten lesen (0 = alle)")
    args = parser.parse_args(argv)

    grund = {
        "action": "query", "generator": "categorymembers",
        "gcmtitle": "Category:Cars (FH6)", "gcmlimit": "50",
        "prop": "revisions", "rvprop": "content", "rvslots": "main",
        "format": "json",
    }
    weiter: dict[str, str] = {}
    seiten = 0
    eintraege: dict[str, list[dict]] = {}
    ohne = []

    while True:
        antwort = hole({**grund, **weiter})
        gefunden = antwort.get("query", {}).get("pages", {})
        for seite in gefunden.values():
            seiten += 1
            titel = seite.get("title", "")
            fassungen = seite.get("revisions")
            if not fassungen:
                continue
            text = fassungen[0]["slots"]["main"]["*"]
            kopf = KOPFZEILE.search(text)
            jahr = int(kopf.group(1)) if kopf else None
            voll = kopf.group(2).strip() if kopf else titel
            block = ABK_BLOCK.search(text)
            if not block:
                ohne.append(titel)
                continue
            for kurz in IN_ANFUEHRUNG.findall(block.group(1)):
                kurz = kurz.strip()
                if kurz:
                    eintraege.setdefault(kurz, []).append(
                        {"name": voll, "year": jahr, "page": titel})
        if args.limit and seiten >= args.limit:
            break
        if "continue" not in antwort:
            break
        weiter = antwort["continue"]
        time.sleep(0.4)

    mehrdeutig = {k: v for k, v in eintraege.items() if len({(e["name"], e["year"]) for e in v}) > 1}
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps({
        "source": "https://forza.fandom.com/wiki/Category:Cars_(FH6)",
        "pages_read": seiten,
        "abbreviations": eintraege,
        "ambiguous": sorted(mehrdeutig),
        "without_abbreviation": sorted(ohne),
    }, ensure_ascii=False, indent=1), encoding="utf-8")

    print(f"{seiten} Autoseiten gelesen")
    print(f"  Kurznamen gefunden:      {len(eintraege):,}")
    print(f"  davon mehrdeutig:        {len(mehrdeutig):,}")
    print(f"  Seiten ohne Kurznamen:   {len(ohne):,}")
    print(f"geschrieben: {args.out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
