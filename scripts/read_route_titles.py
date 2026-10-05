"""Die Streckennamen einer Kategorie aus den Aufnahme-Standbildern lesen.

    python scripts/read_route_titles.py data/runtime/navigation/20260828_095804

Schreibt `routes.host.json` neben `routes.json` und gibt die Liste aus.

## Warum es das braucht

`forza_navigator.ps1 -EnumerateRoutes` zaehlt die Strecken richtig, aber die Namen
kommen aus der **Gast**-OCR (Windows.Media.Ocr), und die verschmiert sie: aus
"Hokubu Ascent" wurde `*6kubÄcent`, aus "Tokyo Bay Docks Charge" `dkyobftyDocvCharge`,
und an einer Position stand die Kopfzeile "Routes" statt eines Namens. Ein Katalog
mit solchen Namen waere unbrauchbar -- und von Hand 15, 19 und 21 Bilder anzusehen
ist keine Loesung, die man dreimal machen will.

Der grosse Titel im Kartenbild liest dagegen sauber, wenn man ihn der **Host**-OCR
(RapidOCR) vorlegt: gleiche Bilder, besserer Leser. Zusaetzlich steht darunter
"Route Length: 6.7 KM" -- die Laenge dient als Gegenprobe, dass wirklich der Titel
und nicht ein Kachel-Text erwischt wurde.

## Bildbereich

Titel und Laenge stehen unten links im Kartenbild, gemessen an 1920x1080:
Titel etwa y 555-660, Laenge y 660-700, beide ab x 120. Der Bereich wird relativ
gerechnet, damit andere Aufloesungen nicht danebengreifen.
"""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

# Gemessen am Standbild "Hokubu Ascent" (1920x1080): der Titel steht zwischen
# y 565 und 645, die Laengenzeile bei y 678. Das Titelband darf die Laengenzeile
# NICHT enthalten -- sonst gewinnt "Route Length: 6.8 KM" jede Laengenauswahl.
TITLE_BAND = (0.06, 0.505, 0.75, 0.607)   # x0, y0, x1, y1 als Anteile
# Bis 0.62 und nicht 0.40: "Route Length: 30.1 KM" (The Gauntlet) endet bei ~0.415 --
# die schmale Fassung schnitt die Zahl ab, und ohne Laenge galt der Kartenschirm als
# Nicht-Karte (2026-09-24). Rechts davon liegt nur Kartenbild, keine Schrift.
LENGTH_BAND = (0.06, 0.607, 0.62, 0.665)


def crop(frame, band):
    height, width = frame.shape[:2]
    x0, y0, x1, y1 = band
    return frame[int(height * y0):int(height * y1),
                 int(width * x0):int(width * x1)]


def texts(engine, image) -> list[str]:
    result = engine(image)
    out = []
    # RapidOCR gibt je Fassung anders zurueck; beide Formen abfangen, sonst liest
    # das Skript stumm nichts und die Liste sieht nur leer aus.
    boxes = getattr(result, "txts", None)
    if boxes:
        out = [t for t in boxes if t]
    elif isinstance(result, (list, tuple)) and result and result[0]:
        for entry in result[0]:
            if isinstance(entry, (list, tuple)) and len(entry) >= 2:
                out.append(str(entry[1]))
    return [t.strip() for t in out if t and t.strip()]


def clean(text: str) -> str:
    return re.sub(r"\s+", " ", (text or "").strip())


def score(name: str) -> int:
    """Wie glaubwuerdig ist dieser Name?

    Ein Streckenname besteht aus Buchstaben, Leerzeichen und hoechstens einem
    Bindestrich oder Apostroph. Alles mit Ziffern oder fremden Glyphen ist ein
    Lesefehler -- `*6kubAcent` darf nicht gewinnen, nur weil es lang ist. Ohne
    diese Regel waere die Auswahl "der laengere gewinnt", und dann gewinnt Muell,
    sobald er lang genug ist.

    Zwei Abzuege fangen die Kleber ab, die aus der Karte in den Titel geraten:

    * Ein Kleinbuchstabe direkt vor einem Grossbuchstaben INNERHALB eines Wortes
      kommt in diesen Titeln nicht vor. `RainbowE Bridge Descent` ist damit
      erkennbar schlechter als `Rainbow Bridge Descent`, obwohl es ein Zeichen
      laenger ist -- und genau daran waere die Auswahl "der laengere gewinnt"
      gescheitert.
    * Ein einzelner Grossbuchstabe am Ende (`Cedar Run S`) ist dasselbe Problem
      am anderen Ende des Namens.
    """
    if not name:
        return 0
    if not re.fullmatch(r"[A-Za-z][A-Za-z' \-]{2,}", name):
        return 0
    points = len(name)
    points -= 10 * len(re.findall(r"[a-z][A-Z]", name))
    if re.search(r"\s[A-Za-z]$", name):
        points -= 5
    return max(points, 0)


def better(host: str, guest: str) -> tuple[str, str]:
    """Den plausibleren der zwei Leser nehmen und sagen, welcher es war."""
    hs, gs = score(host), score(guest)
    if hs >= gs and hs > 0:
        return host, "host"
    if gs > 0:
        return guest, "gast"
    # Beide unglaubwuerdig: das Rohergebnis des Hosts behalten, damit ueberhaupt
    # etwas dasteht, aber sichtbar bleibt, dass es nichts wurde.
    return host or guest, "unsicher"


def main(argv: list[str]) -> int:
    if len(argv) < 2:
        print(__doc__.splitlines()[2].strip())
        return 2
    run = Path(argv[1])
    frames = sorted((run / "frames").glob("*enum-*.png"),
                    key=lambda p: int(re.search(r"enum-(\d+)", p.name).group(1)))
    if not frames:
        print(f"keine enum-Standbilder in {run / 'frames'}")
        return 2

    from extract_leaderboard import create_rapidocr_engine
    from screen_reader import read_image
    engine = create_rapidocr_engine(use_gpu=False)

    # Die Gast-OCR als zweite Meinung: sie verschmiert oft, hatte aber bei
    # "Rainbow Bridge Descent" den vollen Namen, wo der Host abschnitt.
    guest_names: dict[int, str] = {}
    guest_file = run / "routes.json"
    if guest_file.exists():
        try:
            data = json.loads(guest_file.read_text(encoding="utf-8-sig"))
            for i, entry in enumerate(data.get("routes", [])):
                guest_names[i] = entry
        except Exception:
            pass

    names: list[dict] = []
    for path in frames:
        index = int(re.search(r"enum-(\d+)", path.name).group(1))
        frame = read_image(path)
        title = texts(engine, crop(frame, TITLE_BAND))
        length = texts(engine, crop(frame, LENGTH_BAND))
        km = None
        for line in length:
            hit = re.search(r"([\d.,]+)\s*KM", line, re.IGNORECASE)
            if hit:
                km = hit.group(1).replace(",", ".")
                break
        # Die Laengenzeile fliegt raus, falls das Band sie doch erwischt -- sie ist
        # laenger als mancher Streckenname.
        title = [t for t in title
                 if not re.search(r"route\s*length|^\d+[.,]?\d*\s*KM$", t, re.I)]
        # Boxen zusammensetzen statt die laengste zu nehmen: bei "Rainbow Bridge
        # Descent" zerlegte RapidOCR den Titel und die laengste Box war "RainbowE".
        host_name = clean(" ".join(title))
        guest_name = clean(guest_names.get(index, ""))
        name, who = better(host_name, guest_name)
        names.append({"index": index, "name": name, "km": km,
                      "host": host_name, "guest": guest_name, "source": who})
        print(f"  {index:>2}  {name:<28} {km or '?':>5} km   [{who}]")

    out = run / "routes.host.json"
    source = {}
    guest = run / "routes.json"
    if guest.exists():
        try:
            source = json.loads(guest.read_text(encoding="utf-8-sig"))
        except Exception:
            pass
    out.write_text(json.dumps({
        "rivals_mode": source.get("rivals_mode"),
        "route_count": len(names),
        "routes": [n["name"] for n in names],
        "detail": names,
    }, indent=2, ensure_ascii=False), encoding="utf-8")
    print(f"\n{len(names)} Strecke(n) -> {out}")
    leer = [n["index"] for n in names if not n["name"]]
    if leer:
        print(f"WARNUNG ohne Namen: {leer}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
