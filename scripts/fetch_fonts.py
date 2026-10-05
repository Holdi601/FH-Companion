"""Die Schriften der Seiten SELBST ablegen -- kein Abruf bei Google.

    python scripts/fetch_fonts.py            laden (nur, was fehlt) und pruefen
    python scripts/fetch_fonts.py --verify   nur pruefen

## Warum

Die Auswertungsseite lud ihre Schriften von fonts.googleapis.com. Jeder Besucher
schickte damit seine IP-Adresse an Google -- ohne Einwilligung ist das nach der
DSGVO unzulaessig (LG Muenchen I, 3 O 17493/20, 20.01.2022). Die Schriften selbst
sind frei: Barlow Condensed, IBM Plex Sans und IBM Plex Mono stehen unter der SIL
Open Font License 1.1, die das Weitergeben und Einbetten ausdruecklich erlaubt.

## Woher

Einmalig von den Fontsource-Paketen (npm, ueber jsDelivr) -- gebaut aus denselben
Quellen, unter derselben Lizenz. Danach liegen die Dateien in server/fonts/ und
werden nie wieder von aussen geholt: die Seite bettet sie ein, der Server liefert
sie selbst aus. Die Pruefsummen stehen in fonts.lock.json; eine veraenderte Datei
faellt beim naechsten Lauf auf.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import sys
import urllib.request
from pathlib import Path

WS = Path(__file__).resolve().parent.parent
ZIEL = WS / "server" / "fonts"
LOCK = ZIEL / "fonts.lock.json"

# Paket -> (CSS-Familienname, Fassung, Schnitte)
SCHRIFTEN = {
    "barlow-condensed": ("Barlow Condensed", "5.2.5", [(500, "normal"), (600, "normal"), (700, "normal")]),
    "ibm-plex-sans": ("IBM Plex Sans", "5.2.5", [(400, "normal"), (400, "italic"), (500, "normal"), (600, "normal")]),
    "ibm-plex-mono": ("IBM Plex Mono", "5.2.5", [(400, "normal"), (500, "normal"), (600, "normal")]),
}
# Latein und Latein-Erweitert: Auto- und Spielernamen tragen Zeichen wie ł, ő, č.
UNTERGRUPPEN = {
    "latin": "U+0000-00FF, U+0131, U+0152-0153, U+02BB-02BC, U+02C6, U+02DA, U+02DC, "
             "U+0304, U+0308, U+0329, U+2000-206F, U+20AC, U+2122, U+2191, U+2193, "
             "U+2212, U+2215, U+FEFF, U+FFFD",
    "latin-ext": "U+0100-02BA, U+02BD-02C5, U+02C7-02CC, U+02CE-02D7, U+02DD-02FF, "
                 "U+0304, U+0308, U+0329, U+1D00-1DBF, U+1E00-1E9F, U+1EF2-1EFF, U+2020, "
                 "U+20A0-20AB, U+20AD-20C0, U+2113, U+2C60-2C7F, U+A720-A7FF",
}


def laden(url: str) -> bytes:
    with urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent": "forza-fonts"}),
                                timeout=60) as r:
        return r.read()


def dateien():
    for paket, (familie, fassung, schnitte) in SCHRIFTEN.items():
        for gewicht, stil in schnitte:
            for teil in UNTERGRUPPEN:
                name = f"{paket}-{teil}-{gewicht}-{stil}.woff2"
                url = f"https://cdn.jsdelivr.net/npm/@fontsource/{paket}@{fassung}/files/{name}"
                yield paket, familie, gewicht, stil, teil, name, url


def main(argv=None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--verify", action="store_true")
    args = ap.parse_args(argv)
    ZIEL.mkdir(parents=True, exist_ok=True)
    lock = json.loads(LOCK.read_text(encoding="utf-8")) if LOCK.exists() else {}
    fehler = 0
    css = ["/* Selbst ausgelieferte Schriften, SIL Open Font License 1.1 -- siehe OFL-*.txt.",
           "   Barlow Condensed (c) The Barlow Project Authors; IBM Plex (c) IBM Corp.",
           "   Erzeugt von scripts/fetch_fonts.py. Kein Abruf bei Dritten. */"]
    for paket, familie, gewicht, stil, teil, name, url in dateien():
        pfad = ZIEL / name
        if not pfad.exists():
            if args.verify:
                print(f"  FEHLT {name}")
                fehler += 1
                continue
            pfad.write_bytes(laden(url))
            print(f"  geladen {name} ({pfad.stat().st_size // 1024} KB)")
        summe = hashlib.sha256(pfad.read_bytes()).hexdigest()
        if name in lock and lock[name] != summe:
            print(f"  VERAENDERT {name}: Pruefsumme stimmt nicht mit fonts.lock.json")
            fehler += 1
        lock[name] = summe
        css.append("@font-face { font-family: \"%s\"; font-style: %s; font-weight: %d; "
                   "font-display: swap; src: url(\"/fonts/%s\") format(\"woff2\"); "
                   "unicode-range: %s; }" % (familie, stil, gewicht, name, UNTERGRUPPEN[teil]))
    for paket, (familie, fassung, _) in SCHRIFTEN.items():
        lizenz = ZIEL / f"OFL-{paket}.txt"
        if not lizenz.exists() and not args.verify:
            lizenz.write_bytes(laden(f"https://cdn.jsdelivr.net/npm/@fontsource/{paket}@{fassung}/LICENSE"))
            print(f"  Lizenz {lizenz.name}")
        if not lizenz.exists():
            fehler += 1
        elif "Open Font License" not in lizenz.read_text(encoding="utf-8", errors="replace"):
            print(f"  {lizenz.name} ist nicht die OFL")
            fehler += 1
    if not args.verify:
        (ZIEL / "fonts.css").write_text("\n".join(css) + "\n", encoding="utf-8")
        LOCK.write_text(json.dumps(lock, indent=1, sort_keys=True), encoding="utf-8")
    anzahl = len(list(ZIEL.glob("*.woff2")))
    print(f"{anzahl} Schriftdateien, {'keine Abweichung' if fehler == 0 else str(fehler) + ' Problem(e)'}")
    return 1 if fehler else 0


if __name__ == "__main__":
    sys.exit(main())
