# -*- coding: utf-8 -*-
"""Streckennamen aus den Rivalen-Karten holen -- ueber die Form, nicht ueber Text.

    python scripts/route_shapes.py --ernten          Karten aus den Frames holen
    python scripts/route_shapes.py --eichen          Welt -> Karte aus einem Treffer
    python scripts/route_shapes.py --zuordnen        eigene Kurse benennen
    python scripts/route_shapes.py --setzen          die Namen wirklich schreiben

## Worum es geht

Der Rundenbestand kennt keine Streckennamen. Die Telemetrie liefert keine, das Feld
`track` ist bei allen aufgezeichneten Runden leer, und die Kursordner heissen nach den
Koordinaten ihrer Start-Ziel-Linie.

Der Rivalen-Schirm des Spiels zeigt dagegen genau das, was fehlt: den NAMEN, die
LAENGE ("Route Length: 6.6 KM") und den VERLAUF, magenta auf die Weltkarte gezeichnet.
Und der eigene Rundenbestand hat den Verlauf auch -- in Weltkoordinaten, alle 5 m ein
Messpunkt.

Zwei Verlaeufe derselben Strecke, einmal in Bildpunkten und einmal in Metern. Was
fehlt, ist die Abbildung zwischen beiden.

## Warum das besser ist als Formvergleich

Die Karte ist NORDWEISEND und massstaeblich. Zwischen Weltkoordinaten und Kartenpunkten
liegt darum keine beliebige Verzerrung, sondern eine Aehnlichkeitsabbildung mit
genau drei Zahlen: Massstab, Verschiebung in X, Verschiebung in Z. Ist sie einmal an
EINER bekannten Strecke geeicht, liegt jede weitere Strecke sofort in Weltkoordinaten
-- und dann ist die Zuordnung zum eigenen Kurs kein Aehnlichkeitsmass mehr, sondern
ein Abstand in Metern zwischen zwei Startpunkten.

Ein Formvergleich haette geschaetzt. Das hier misst.

## Was hier NICHT passiert

Geraten wird nicht. Ein Kurs bekommt seinen Namen nur, wenn genau EIN Kandidat
innerhalb der Toleranz liegt; liegen zwei darin, bleibt er namenlos. Und geschrieben
wird erst mit `--setzen`, vorher ist jeder Lauf eine Probe.
"""
from __future__ import annotations

import argparse
import json
import math
import os
import sys
from pathlib import Path

import numpy as np
from PIL import Image

WORKSPACE = Path(__file__).resolve().parent.parent
FRAMES = WORKSPACE / "data" / "runtime" / "navigation"
ERNTE = WORKSPACE / "data" / "route_shapes"
sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "server"))
from local_settings import app_data  # noqa: E402  (FHCompanion, bis 2026-09-26 ForzaGripHaptics)
LAPS = app_data() / "laps"

# Die magenta Linie der Strecke. Gemessen am Frame vom 2026-09-14: rund
# (214, 44, 190). Der Toleranzschlauch ist weit genug fuer die Kantenglaettung und
# eng genug, dass das Gruen der Karte und das Gelbgruen der Menuebalken draussen
# bleiben -- entscheidend ist, dass Rot und Blau hoch sind und Gruen tief.
def magenta_maske(rgb: np.ndarray) -> np.ndarray:
    r = rgb[..., 0].astype(np.int16)
    g = rgb[..., 1].astype(np.int16)
    b = rgb[..., 2].astype(np.int16)
    return (r > 120) & (b > 110) & (g + 60 < r) & (g + 60 < b) & (abs(r - b) < 90)


# DIE LINIE HAT NICHT IMMER DIESELBE FARBE. In der VM war sie magenta, auf dem
# Rechner des Nutzers am 2026-09-24 BLAU, rund (11, 132, 190) -- dasselbe Spiel,
# eine andere Einstellung. Nur nach Magenta zu suchen hiess dort: jeder der 86
# Kartenschirme faellt durch, und weil --ernten vorher aufraeumt, waere danach
# WENIGER da gewesen als vorher.
#
# Blau ueber den Farbton, nicht ueber feste Kanalschwellen: gemessen auf dem
# Kartenfeld lag die Linie bei 195-210 Grad und Saettigung ueber 0,7; das
# tuerkise Flachwasser an den Kuesten bei 165-185 Grad, der gelbe Titeltext bei
# 60. Das Band 190-218 trennt die drei.
def _farbton_saettigung(rgb: np.ndarray):
    f = rgb.astype(np.float32) / 255.0
    r, g, b = f[..., 0], f[..., 1], f[..., 2]
    mx = f.max(axis=2)
    mn = f.min(axis=2)
    d = np.maximum(mx - mn, 1e-6)
    ton = np.where(mx == r, ((g - b) / d) % 6,
                   np.where(mx == g, (b - r) / d + 2, (r - g) / d + 4)) * 60.0
    satt = np.where(mx > 0, (mx - mn) / np.maximum(mx, 1e-6), 0.0)
    return ton, satt, mx


def blau_maske(rgb: np.ndarray) -> np.ndarray:
    ton, satt, wert = _farbton_saettigung(rgb)
    return (ton >= 190) & (ton <= 218) & (satt > 0.7) & (wert > 0.5)


# JEDE KATEGORIE HAT IHRE EIGENE FARBE -- das war die eigentliche Ursache, nicht
# eine Einstellung. Gemessen am 2026-09-24 an den Kartenschirmen des Wirts:
#
#   Road Racing    blau     (8, 134, 194)    ~195 Grad
#   Street Racing  magenta  (169, 44, 163)   ~300 Grad
#   Cross-Country  gruen    (30, 154, 80)    ~144 Grad
#   Dirt Racing    orange   (236, 110, 24)   ~24 Grad
#   Drag Racing    rot      (228, 52, 105)   ~342 Grad
#
# In JEDEM Frame liegt ausserdem das Tuerkis des Seitenhintergrunds (39, 128,
# 121) bei ~175 Grad am Rand des Kartenfelds, rund 6.000 Punkte. Kein Band
# darf es beruehren; das gruene endet darum bei 158.
def gruen_maske(rgb: np.ndarray) -> np.ndarray:
    ton, satt, wert = _farbton_saettigung(rgb)
    return (ton >= 128) & (ton <= 158) & (satt > 0.6) & (wert > 0.4)


def orange_maske(rgb: np.ndarray) -> np.ndarray:
    ton, satt, wert = _farbton_saettigung(rgb)
    return (ton >= 12) & (ton <= 38) & (satt > 0.7) & (wert > 0.6)


def rot_maske(rgb: np.ndarray) -> np.ndarray:
    ton, satt, wert = _farbton_saettigung(rgb)
    return (ton >= 332) & (ton <= 352) & (satt > 0.6) & (wert > 0.6)


# Touge zeichnet TUERKIS, (32, 168, 165) bei ~178 Grad -- fast genau der Farbton
# des Seitenhintergrunds. Trennbar ist das nur ueber den Ort: der Hintergrund
# lag nie IN der Karte, nur am Rand eines zu grosszuegigen Kartenfelds. Seit das
# Feld auf das Kartenbild selbst beschnitten ist (KARTENFELD), bleibt davon nichts.
def tuerkis_maske(rgb: np.ndarray) -> np.ndarray:
    ton, satt, wert = _farbton_saettigung(rgb)
    return (ton >= 172) & (ton <= 186) & (satt > 0.6) & (wert > 0.5)


LINIENFARBEN = {"magenta": magenta_maske, "blau": blau_maske, "gruen": gruen_maske,
                "orange": orange_maske, "rot": rot_maske, "tuerkis": tuerkis_maske}

# Wie viele Punkte eine Linie mindestens hat. Frueher 2000 -- eine Drag-Strecke
# ist aber ein gerader Strich von 1 km und bringt nur 955 bis 2255. Die Grenze
# darf so tief liegen, weil ein Frame ohnehin erst mit "Route Length" als
# Kartenschirm zaehlt.
MINDESTPUNKTE = 800


def punkte_der_maske(maske: np.ndarray) -> np.ndarray:
    ys, xs = np.nonzero(maske)
    return np.stack([xs, ys], axis=1).astype(np.float64)


def frame_lesen(pfad: Path) -> np.ndarray:
    with Image.open(pfad) as bild:
        return np.asarray(bild.convert("RGB"))


def sidecar(pfad: Path) -> str:
    txt = pfad.with_suffix(".txt")
    if not txt.exists():
        return ""
    return txt.read_text(encoding="utf-8-sig", errors="ignore")


def name_und_laenge(text: str, katalog: list[str]) -> tuple[str | None, float | None]:
    import re
    name = next((t for t in katalog if t in text), None)
    m = re.search(r"Route Length[: ]+([0-9]+(?:\.[0-9]+)?)\s*KM", text, re.I)
    laenge = float(m.group(1)) if m else None
    return name, laenge


def katalog() -> list[str]:
    pfad = WORKSPACE / "data" / "analytics" / "laps.json"
    d = json.loads(pfad.read_text(encoding="utf-8"))
    return sorted(d["tracks"], key=len, reverse=True)


# ---------------------------------------------------------------- ernten

_ENGINE = None


def titel_lesen(png: Path, namen: list[str]) -> tuple[str | None, float | None]:
    """Titel und Laenge aus dem Kartenbild -- mit dem Host-Leser.

    Der Name wird gegen den Katalog gehalten, nicht roh uebernommen: ein
    Lesefehler wie "Festival Chese" soll zu "Festival Chase" werden oder gar
    nichts, aber nie ein neuer Streckenname, den es nicht gibt.
    """
    global _ENGINE
    try:
        import difflib
        sys.path.insert(0, str(WORKSPACE / "scripts"))
        from read_route_titles import crop, texts, clean, TITLE_BAND, LENGTH_BAND
        if _ENGINE is None:
            from extract_leaderboard import create_rapidocr_engine
            _ENGINE = create_rapidocr_engine(False)
        bild = frame_lesen(png)
        titel = " ".join(clean(x) for x in texts(_ENGINE, crop(bild, TITLE_BAND)))
        laenge_text = " ".join(clean(x) for x in texts(_ENGINE, crop(bild, LENGTH_BAND)))
    except Exception:
        return None, None

    name = next((n for n in namen if n.lower() in titel.lower()), None)
    if name is None and titel.strip():
        # Unscharf, aber mit harter Schwelle: lieber namenlos als falsch benannt.
        treffer = difflib.get_close_matches(titel.strip(), namen, n=1, cutoff=0.82)
        name = treffer[0] if treffer else None

    import re
    m = re.search(r"([0-9]+(?:[.,][0-9]+)?)\s*K", laenge_text, re.I)
    laenge = float(m.group(1).replace(",", ".")) if m else None
    return name, laenge


def ernten(argv) -> int:
    """Aus jedem Frame, der eine Streckenkarte zeigt, die Linie herausholen."""
    namen = katalog()
    ERNTE.mkdir(parents=True, exist_ok=True)
    gefunden = 0
    ohne_namen = 0
    ueberpruefte_nichtkarten = 0
    ohne_linie: list[Path] = []
    for alt_datei in ERNTE.glob("*.json"):
        # Frisch ernten: alte Fehltreffer duerfen nicht liegen bleiben.
        alt_datei.unlink()
    for png in sorted(FRAMES.rglob("*.png")):
        try:
            rgb = frame_lesen(png)
        except Exception:
            continue
        # AUF DAS KARTENFELD BESCHRAENKT, und zwar BEVOR irgendetwas gemessen
        # wird. Die ganze Bildflaeche zu nehmen war ein Fehler: die Klassenleiste
        # unten ist ebenfalls magenta, sie landete in jedem Rahmen und in jeder
        # Ausdehnung -- und ein Filter, der danach auf die Ausdehnung schaut,
        # verwirft dann ausgerechnet die echten Karten.
        pts = linie_aus_frame(png)
        if pts is None:
            # Ein Kartenschirm OHNE erkannte Linie ist kein Nicht-Karte-Fall,
            # sondern eine unbekannte Linienfarbe -- genau so fielen am
            # 2026-09-24 drei ganze Kategorien still heraus. Zaehlen und nennen.
            if "route length" in sidecar(png).lower():
                ohne_linie.append(png)
            continue
        n = len(pts)
        text = sidecar(png)
        name, laenge = name_und_laenge(text, namen)
        # DER GROSSE TITEL, WENN DAS SIDECAR NICHTS HERGIBT.
        #
        # Die Textdatei neben dem Bild stammt von der Gast-OCR (Windows.Media.Ocr),
        # und die verschmiert Namen: aus 36 Kartenbildern fand sie am 2026-09-24
        # genau zwei. Der Titel unten links im Kartenbild ist dagegen gross und
        # sauber gesetzt; der Host-Leser (RapidOCR) liest ihn verlaesslich -- das
        # ist dieselbe Erkenntnis, die schon read_route_titles.py traegt.
        if name is None or laenge is None:
            gelesen_name, gelesen_laenge = titel_lesen(png, namen)
            name = name or gelesen_name
            laenge = laenge or gelesen_laenge
        # NUR KARTENSCHIRME. Jeder echte zeigt "Route Length: N KM" unter dem
        # Titel. Fehlt die Laenge, ist es kein Kartenschirm -- am 2026-09-24 waren
        # das zehn Bestenlisten ("Change Rival"), deren violette Stufenabzeichen
        # den Magenta-Filter ausgeloest hatten. Sie als namenlose Karten zu fuehren
        # hiesse, Muell in die Formbibliothek zu legen.
        if laenge is None:
            ueberpruefte_nichtkarten += 1
            continue
        if name is None:
            ohne_namen += 1
        ziel = ERNTE / (png.parent.parent.name + "_" + png.stem + ".json")
        ziel.write_text(json.dumps({
            "frame": str(png.relative_to(WORKSPACE)),
            "name": name,
            "routeLengthKm": laenge,
            "pixels": n,
            "bbox": [float(pts[:, 0].min()), float(pts[:, 1].min()),
                     float(pts[:, 0].max()), float(pts[:, 1].max())],
            "points": [[float(x), float(y)] for x, y in pts[::3]],
        }), encoding="utf-8")
        gefunden += 1
    print(f"{gefunden} Kartenschirm(e), davon {ohne_namen} ohne lesbaren Namen; "
          f"{ueberpruefte_nichtkarten} Nicht-Karte(n) verworfen")
    ergaenze_nach_position()
    if ohne_linie:
        print(f"WARNUNG: {len(ohne_linie)} Kartenschirm(e) ohne erkannte Linie -- "
              f"eine Linienfarbe, die LINIENFARBEN nicht kennt?")
        for png in ohne_linie[:8]:
            print(f"  {png.relative_to(WORKSPACE)}")
    print(f"abgelegt in {ERNTE}")
    # DIE APP LIEST NICHT DIE ERNTE, sondern deren Aufbereitung (route_maps.py):
    # geordnete Linie und Kartenbild je Strecke. Ohne diesen Schritt zeigte sie nach
    # einer neuen Ernte die alten Karten -- oder, auf einem frischen Rechner, keine.
    import route_maps
    route_maps.main([])
    return 0


# ---------------------------------------------------------------- eigene Kurse

# ---------------------------------------------------------------- nach Position

KATALOG_DATEI = WORKSPACE / "config" / "fh6_board_catalogue.json"


def ergaenze_nach_position() -> int:
    """Kartenschirme, deren Titel nicht lesbar ist, ueber ihre POSITION benennen.

    Ein Kartenlauf (forza_navigator.ps1 -EnumerateMaps) geht das Karussell einer
    Kategorie der Reihe nach ab und nennt die Bilder map-0, map-1, ... In dieser
    Reihenfolge steht die Kategorie auch im Katalog (route_index). Ein Titel, den
    die OCR nicht lesen kann -- "Shinjuku Gyoen Cross-Count", vom Spiel selbst am
    Kartenrand abgeschnitten --, ist damit trotzdem bekannt.

    SELBSTPRUEFEND, nicht angenommen: gesucht wird der EINE Versatz, unter dem
    JEDER gelesene Name des Laufs zu seiner Position passt. Am 2026-09-24 waren das
    65 von 65 in fuenf Kategorien mit Versatz 0 und 18 von 18 in Cross-Country mit
    Versatz 1 (die Liste oeffnet dort auf dem zweiten Eintrag). Passt auch nur ein
    gelesener Name nicht, oder gibt es weniger als drei, wird nichts ergaenzt --
    lieber eine Luecke als eine falsch benannte Karte.

    Die Laenge kommt, wenn der Schirm sie nicht hergibt, aus dem Katalog.
    """
    import re
    try:
        katalog_d = json.loads(KATALOG_DATEI.read_text(encoding="utf-8"))["tracks"]
    except (OSError, ValueError, KeyError):
        return 0
    gelesen: dict[Path, dict[int, str]] = {}
    for f in ERNTE.glob("*.json"):
        try:
            d = json.loads(f.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            continue
        frame = WORKSPACE / d.get("frame", "")
        m = re.search(r"map-(\d+)$", frame.stem)
        if m and d.get("name"):
            gelesen.setdefault(frame.parent.parent, {})[int(m.group(1))] = d["name"]

    ergaenzt = 0
    for lauf in sorted({p.parent.parent for p in FRAMES.rglob("*map-*.png")}):
        try:
            state = json.loads((lauf / "state.json").read_text(encoding="utf-8-sig"))
        except (OSError, ValueError):
            continue
        kategorie = state.get("rivals_mode")
        eintraege = (katalog_d.get(kategorie) or {}).get("verified") or []
        nach_index = {e.get("route_index"): e for e in eintraege if e.get("route_index") is not None}
        anzahl = len(eintraege)
        namen = gelesen.get(lauf, {})
        if anzahl == 0 or len(namen) < 3:
            continue
        passend = [o for o in range(anzahl)
                   if all((nach_index.get((n + o) % anzahl) or {}).get("name") == name
                          for n, name in namen.items())]
        if len(passend) != 1:
            print(f"  {lauf.name} ({kategorie}): kein eindeutiger Versatz "
                  f"({len(passend)} passen) -- nichts nach Position benannt")
            continue
        versatz = passend[0]
        for png in sorted((lauf / "frames").glob("*map-*.png")):
            n = int(re.search(r"map-(\d+)$", png.stem).group(1))
            if n in namen:
                continue
            eintrag = nach_index.get((n + versatz) % anzahl)
            if not eintrag:
                continue
            pts = linie_aus_frame(png)
            if pts is None:
                continue
            laenge = name_und_laenge(sidecar(png), [])[1] or eintrag.get("route_length_km")
            ziel = ERNTE / (lauf.name + "_" + png.stem + ".json")
            ziel.write_text(json.dumps({
                "frame": str(png.relative_to(WORKSPACE)),
                "name": eintrag["name"],
                "routeLengthKm": laenge,
                "namedBy": f"carousel position {n} + offset {versatz} in {kategorie}",
                "pixels": len(pts),
                "bbox": [float(pts[:, 0].min()), float(pts[:, 1].min()),
                         float(pts[:, 0].max()), float(pts[:, 1].max())],
                "points": [[float(x), float(y)] for x, y in pts[::3]],
            }), encoding="utf-8")
            print(f"  {lauf.name}: map-{n} = {eintrag['name']} (Position, Versatz {versatz})")
            ergaenzt += 1
    if ergaenzt:
        print(f"{ergaenzt} Kartenschirm(e) nach Position benannt")
    return ergaenzt


def eigene_kurse(wurzel: Path) -> dict[str, dict]:
    """Je Kursordner: Startpunkt, Laenge und der gefahrene Verlauf."""
    ergebnis: dict[str, dict] = {}
    if not wurzel.exists():
        return ergebnis
    for ordner in sorted(wurzel.iterdir()):
        notiz_pfad = ordner / "course.json"
        if not notiz_pfad.exists():
            continue
        try:
            notiz = json.loads(notiz_pfad.read_text(encoding="utf-8"))
        except Exception:
            continue
        spur = None
        for datei in sorted(ordner.rglob("*.json")):
            if datei.name == "course.json":
                continue
            try:
                lap = json.loads(datei.read_text(encoding="utf-8"))
            except Exception:
                continue
            # Die Datei umschliesst die Runde: {Course, Tag, Class, Lap}.
            # Die Messpunkte liegen unter "Lap", nicht an der Wurzel.
            kern = lap.get("Lap") if isinstance(lap.get("Lap"), dict) else lap
            proben = kern.get("samples") or kern.get("Samples") or []
            pts = [(s.get("x", s.get("X")), s.get("z", s.get("Z")))
                   for s in proben
                   if s.get("x", s.get("X")) is not None
                   and s.get("z", s.get("Z")) is not None]
            if len(pts) > 20:
                spur = pts
                break
        if spur is None:
            continue
        ergebnis[ordner.name] = {
            "name": notiz.get("Name", ""),
            "startX": notiz.get("StartX", 0.0),
            "startZ": notiz.get("StartZ", 0.0),
            "metres": notiz.get("ShortestMetres", 0.0),
            "track": spur,
        }
    return ergebnis


# ---------------------------------------------------------------- zuordnen

# Das Kartenfeld des Rivalen-Schirms, in Bruchteilen des Bildes. Abgelesen am
# Frame vom 2026-09-14 (1920x1080). Ausserhalb liegt magenta Menuegrafik -- vor
# allem die Klassenleiste unten --, und die wuerde die Linie verfaelschen.
#
# BESCHNITTEN am 2026-09-24 auf das Kartenbild selbst: es reicht von x 122 bis
# 1429 und y 261 bis 716, das alte Feld (0.06, 0.24, 0.75, 0.67) ragte an allen
# Seiten darueber hinaus in den tuerkisen Seitenhintergrund -- rund 6.000 Punkte
# in jedem Frame, genau im Farbton der Touge-Linie. Innen ein paar Punkte Luft.
KARTENFELD = (127 / 1920, 265 / 1080, 1425 / 1920, 713 / 1080)

# Wie weit ein projizierter Punkt im Mittel von der gezeichneten Linie abweichen
# darf, in Bildpunkten. Gemessen am 2026-09-17: der richtige Kurs lag bei 5.0,
# der zweitbeste bei 44.2. Dazwischen ist so viel Luft, dass die Grenze nicht
# fein austariert werden muss.
HOECHSTER_ABSTAND = 15.0

# Wie stark sich Massstab in X und in Z unterscheiden duerfen. Die Karte ist
# nordweisend und massstaeblich; passt eine Spur nur, wenn man sie in einer
# Richtung staucht, passt sie nicht. Genau daran sind oben alle falschen
# Kandidaten zu erkennen -- sie brauchten 0.25 gegen 0.15.
HOECHSTE_VERZERRUNG = 0.08


def linie_aus_frame(pfad: Path, farbe_raus: list | None = None) -> np.ndarray | None:
    """Die Streckenlinie eines Frames, auf das Kartenfeld beschraenkt.

    Jede bekannte Linienfarbe wird probiert; es gilt die mit den meisten
    Punkten IM KARTENFELD. Die andere findet dort nur Beiwerk -- Kuestenwasser,
    Titeltext --, und das bleibt weit unter einer ganzen Strecke.
    `farbe_raus` bekommt, wenn uebergeben, den Namen der gewaehlten Farbe.
    """
    try:
        rgb = frame_lesen(pfad)
    except Exception:
        return None
    hoch, breit = rgb.shape[:2]
    x0, y0, x1, y1 = KARTENFELD
    feld = rgb[int(y0 * hoch):int(y1 * hoch) + 1, int(x0 * breit):int(x1 * breit) + 1]
    beste = None
    for name, maske_von in LINIENFARBEN.items():
        ys, xs = np.nonzero(maske_von(feld))
        if beste is None or len(xs) > len(beste[1]):
            beste = (name, xs, ys)
    name, xs, ys = beste
    if len(xs) < MINDESTPUNKTE:
        return None
    if farbe_raus is not None:
        farbe_raus.append(name)
    # Zurueck in Bildkoordinaten: die Eichung und jede gespeicherte Karte
    # rechnen im ganzen Bild, nicht im Ausschnitt.
    return np.stack([xs + int(x0 * breit), ys + int(y0 * hoch)], axis=1).astype(np.float64)


def passung(spur: list[tuple[float, float]], linie: np.ndarray):
    """Wie gut passt eine eigene Spur auf eine gezeichnete Linie?

    Die Abbildung ist NORDWEISEND und massstaeblich:

        x_karte =  a * X + bx
        y_karte = -a * Z + by

    Das Minus, weil Z im Spiel nach Norden waechst und y im Bild nach unten. Der
    Massstab wird aus den Ausdehnungen geschaetzt -- fuer eine Strecke, die den
    Kartenausschnitt fuellt, ist das genau genug, und die Bewertung faengt jeden
    Fall ab, in dem es das nicht ist.

    Zurueck kommt (mittlerer Abstand, Verzerrung, Massstab).
    """
    P = np.asarray(spur, dtype=np.float64)
    if len(P) < 10:
        return None
    px, pz = P[:, 0], P[:, 1]
    breite_m = px.max() - px.min()
    hoehe_m = pz.max() - pz.min()
    if breite_m <= 1 or hoehe_m <= 1:
        return None
    breite_px = linie[:, 0].max() - linie[:, 0].min()
    hoehe_px = linie[:, 1].max() - linie[:, 1].min()
    ax = breite_px / breite_m
    az = hoehe_px / hoehe_m
    a = (ax + az) / 2
    verzerrung = abs(ax - az) / max(ax, az)

    bx = linie[:, 0].min() - a * px.min()
    by = linie[:, 1].min() + a * pz.max()
    proj = np.stack([a * px + bx, -a * pz + by], axis=1)

    # Mittlerer Abstand zur naechsten gezeichneten Stelle. Beide Seiten werden
    # ausgeduennt: das ist eine Abstandsmessung, keine Bildauswertung, und die
    # volle Punktwolke kostet nur Zeit.
    schritt = max(1, len(proj) // 300)
    pr = proj[::schritt]
    ziel = linie[::7]
    d = np.sqrt(((pr[:, None, :] - ziel[None, :, :]) ** 2).sum(-1)).min(1)
    return float(d.mean()), float(verzerrung), float(a)


def zuordnen(wurzel: Path, setzen: bool) -> int:
    """Jeden geernteten Schirm einem eigenen Kurs zuordnen."""
    kurse = eigene_kurse(wurzel)
    if not kurse:
        print("Keine eigenen Kurse mit Verlauf gefunden.")
        return 1

    karten = sorted(ERNTE.glob("*.json"))
    if not karten:
        print("Keine geernteten Karten. Erst --ernten laufen lassen.")
        return 1

    # Je Streckenname nur EINEN Schirm ansehen. Zwanzig Frames derselben Strecke
    # sind zwanzigmal dieselbe Auskunft und kosten zwanzigmal die Rechenzeit.
    je_name: dict[str, dict] = {}
    for k in karten:
        d = json.loads(k.read_text(encoding="utf-8"))
        if not d.get("name"):
            continue
        if d["name"] not in je_name or d["pixels"] > je_name[d["name"]]["pixels"]:
            je_name[d["name"]] = d

    print(f"{len(je_name)} benannte Strecke(n) in der Ernte, "
          f"{len(kurse)} eigene Kurse")

    geschrieben = 0
    for name, d in sorted(je_name.items()):
        linie = linie_aus_frame(WORKSPACE / d["frame"])
        if linie is None:
            print(f"  {name}: die Linie liess sich nicht noch einmal lesen")
            continue

        treffer = []
        for schluessel, k in kurse.items():
            wert = passung(k["track"], linie)
            if wert is None:
                continue
            abstand, verzerrung, a = wert
            treffer.append((abstand, verzerrung, a, schluessel, k))
        treffer.sort()
        if not treffer:
            continue

        gut = [x for x in treffer
               if x[0] <= HOECHSTER_ABSTAND and x[1] <= HOECHSTE_VERZERRUNG]
        bester = treffer[0]
        print(f"  {name}  ({d.get('routeLengthKm')} km laut Schirm)")
        for abstand, verzerrung, a, schluessel, k in treffer[:3]:
            print(f"      {abstand:7.1f} px  Verzerrung {verzerrung:5.1%}  "
                  f"{schluessel}  {k['metres']:.0f} m")

        if len(gut) != 1:
            print(f"      -> {'kein' if not gut else len(gut)} eindeutiger "
                  "Treffer, nichts geschrieben")
            continue

        abstand, verzerrung, a, schluessel, k = gut[0]
        # DIE LAENGE MUSS AUCH STIMMEN. Die Form ist der Beweis, die Laenge die
        # unabhaengige Gegenprobe -- zwei Quellen, die nichts voneinander wissen.
        km = d.get("routeLengthKm")
        if km and k["metres"] > 0 and abs(k["metres"] / 1000.0 - km) / km > 0.10:
            print(f"      -> Form passt, Laenge nicht ({k['metres']:.0f} m "
                  f"gegen {km} km), nichts geschrieben")
            continue

        if k["name"] and k["name"] != name:
            print(f"      -> heisst schon '{k['name']}', bleibt so")
            continue

        print(f"      -> {schluessel} IST {name}")
        if setzen:
            pfad = wurzel / schluessel / "course.json"
            notiz = json.loads(pfad.read_text(encoding="utf-8"))
            notiz["Name"] = name
            notiz["NameEvidence"] = "map-fit"
            pfad.write_text(json.dumps(notiz, indent=1, ensure_ascii=False),
                            encoding="utf-8")
            geschrieben += 1

    if setzen:
        print(f"{geschrieben} Name(n) geschrieben.")
    else:
        print("Probelauf -- mit --setzen wird geschrieben.")
    return 0


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--ernten", action="store_true",
                   help="Streckenlinien aus den gespeicherten Frames holen")
    p.add_argument("--kurse", action="store_true",
                   help="die eigenen Kurse mit Startpunkt und Laenge auflisten")
    p.add_argument("--zuordnen", action="store_true",
                   help="geerntete Karten den eigenen Kursen zuordnen (Probe)")
    p.add_argument("--setzen", action="store_true",
                   help="die gefundenen Namen wirklich in course.json schreiben")
    p.add_argument("--nach-position", action="store_true",
                   help="nur unlesbare Kartenschirme ueber ihre Karussell-Position "
                        "benennen, ohne alles neu zu ernten")
    p.add_argument("--wurzel", default=str(LAPS))
    args = p.parse_args(argv)

    if args.ernten:
        return ernten(args)
    if args.nach_position:
        ergaenze_nach_position()
        return 0
    if args.zuordnen or args.setzen:
        return zuordnen(Path(args.wurzel), setzen=args.setzen)
    if args.kurse:
        kurse = eigene_kurse(Path(args.wurzel))
        print(f"{len(kurse)} Kurs(e) mit Verlauf")
        for schluessel, k in sorted(kurse.items(),
                                    key=lambda x: -x[1]["metres"]):
            print("  %-42s %7.0f m  %5d Punkte  %s"
                  % (schluessel, k["metres"], len(k["track"]),
                     k["name"] or "-"))
        return 0

    p.print_help()
    return 2


if __name__ == "__main__":
    sys.exit(main())
