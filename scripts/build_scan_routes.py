"""config/fh6_scan_routes.json aus dem Board-Katalog bauen.

Der Scanner in der App (C#, `Scan/ScanNavigator.cs`) faehrt eine Strecke ueber ihren
Index im Streckenkarussell an: Index 0 ist der Anker, von dem aus gezaehlt wird. Die
Reihenfolge je Kategorie steht im Katalog unter `route_carousel_by_category`; hier wird
sie in eine kleine Datei gezogen, die mit der App ausgeliefert wird.

Dazu je Kategorie das Wahrzeichen: ein Muster, das NUR in der Streckenliste dieser
Kategorie vorkommt. Der gelbe Rahmen auf dem Kategorieschirm beweist nichts (die
Markierung hinkt dem Zustand hinterher, 2026-09-01); erst die geoeffnete Liste zaehlt.
Die Muster stammen aus `forza_navigator.ps1` ($script:CategoryLandmark).

Geprueft wird ausserdem, dass die Streckennamen einer Kategorie ueber ihren "Kopf"
(Name ohne Gattungswort: Circuit, Sprint, Cross-Country, Scramble, Trail, Drag Strip,
Chase) unterscheidbar sind -- denn genau so ordnet der Navigator eine gelesene
Laufschrift einer Position zu.
"""

from __future__ import annotations

import json
import re
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
KATALOG = WORKSPACE / "config" / "fh6_board_catalogue.json"
KARTEN = WORKSPACE / "data" / "route_maps"

# Laengen, die keine nachgezeichnete Karte hat -- vom Schirm abgelesen.
LAENGE_NACHTRAG = {
    # data/runtime/navigation/20260914_091346/frames/0001_cycle-1.png: "Route Length: 6.0 KM",
    # Leiste 6.5 | 6.0 | 7.0 | 7.8 | 3.1 -- Nachbarn idx15, idx17, idx18 und idx0 ("23.1" ohne die 2).
    "Shinjuku Gyoen Cross-Country": 6.0,
}
ZIEL = WORKSPACE / "config" / "fh6_scan_routes.json"

WAHRZEICHEN = {
    "Road Racing": r"Highway\s+Circu(?:i(?:t)?)?",
    "Street Racing": r"Daikoku\s+Chase",
    "Cross-Country": r"Cross[\s-]?Country",
    "Dirt Racing": r"Scramble|Trail",
    "Drag Racing": r"Drag",
    "Touge": r"Nanamaga|Arashiya|Bandai|Norikura\s+Sky|Haruna",
}

# Dieselben Marken wie ScanScreen.StreckenKopf in der App: ab hier beginnt das
# Gattungswort (auch von der Texterkennung verstuemmelt: "Cras-eouniry", "Cire").
MARKEN = ("cro", "cra", "ero", "era", "ros", "ras", "cir", "spr", "scr", "tra", "dra", "cha")


KLASSEN = {"cro": "cross", "cra": "cross", "ero": "cross", "era": "cross", "ros": "cross", "ras": "cross",
           "cir": "cir", "spr": "spr", "scr": "scr", "tra": "tra", "dra": "dra", "cha": "cha"}


def falte(name: str) -> str:
    return re.sub(r"[^A-Za-z0-9]", "", name).lower()


def kopf(name: str) -> str:
    """Der Ort vor dem Gattungswort. Ab Stelle 3, damit "Hirosaki" sein "ros" behaelt."""
    s = falte(name)
    for i in range(3, len(s) - 2):
        if s[i:i + 3] in MARKEN:
            return s[:i]
    return s


def gattung(name: str) -> str:
    """Die Art des Gattungsworts ("cross", "cir", "spr" ...), leer ohne eines."""
    s = falte(name)
    k = kopf(name)
    return KLASSEN.get(s[len(k):len(k) + 3], "") if len(k) < len(s) else ""


def levenshtein(a: str, b: str) -> int:
    vorher = list(range(len(b) + 1))
    for i, ca in enumerate(a, 1):
        jetzt = [i]
        for j, cb in enumerate(b, 1):
            jetzt.append(min(vorher[j] + 1, jetzt[j - 1] + 1, vorher[j - 1] + (ca != cb)))
        vorher = jetzt
    return vorher[-1]


def aehnlichkeit(a: str, b: str) -> float:
    if not a or not b:
        return 0.0
    return 1.0 - levenshtein(a, b) / max(len(a), len(b))


SCHWELLE = 0.7      # ab hier zeigt eine Lesung die Strecke
ABSTAND = 0.15      # so weit muss die beste Strecke vor der zweitbesten liegen


def strecken_aehnlichkeit(gelesen: str, name: str) -> float:
    """Wie sehr eine Lesung (Laufschrift, OCR-Schaden) diese Strecke zeigt (0..1).

    Wie ScanScreen.StreckenAehnlichkeit in der App. Verglichen werden die Koepfe
    (Ort ohne Gattungswort) ueber die Editierdistanz; die Lesung ist ein Fenster des
    vollen Namens, darf also bis zu vier Zeichen spaeter beginnen (so weit laeuft ein
    langer Titel) und ohne Gattungswort vor dem Ende des Kopfs aufhoeren. Gleiche
    Koepfe (Shimanoyama Circuit / Sprint) trennt die Art des Gattungsworts.
    """
    x, y = kopf(gelesen), kopf(name)
    if len(x) < 3 or not y:
        return 0.0
    ga, gb = gattung(gelesen), gattung(name)
    if ga and gb and ga != gb:
        return 0.0
    beste = 0.0
    if not ga:
        # Ohne erkanntes Gattungswort vielleicht mitten darin beschnitten ("Shimanoyama Sp"):
        # dann gegen den gleich langen Anfang des vollen Namens.
        xf = falte(gelesen)
        beste = aehnlichkeit(xf, falte(name)[:len(xf)])
    # Nur ein langer Titel laeuft (ab ~25 Zeichen ist er beschnitten); ein kurzer steht
    # -- und darf nicht verschoben verglichen werden, sonst wird aus "Irokawa" ein
    # verschobenes "sh|irakawa".
    for schub in range(0, 5) if len(falte(name)) > 24 else [0]:
        rest = y[schub:]
        if len(rest) < 3:
            break
        if not ga and len(x) < len(rest):
            rest = rest[:len(x)]        # hinten beschnitten, noch vor dem Gattungswort
        beste = max(beste, aehnlichkeit(x, rest))
    return beste


def gleiche_strecke(gelesen: str, name: str) -> bool:
    return strecken_aehnlichkeit(gelesen, name) >= SCHWELLE


def lokalisiere(gelesen: str, strecken: list[str]) -> int:
    """Die Position einer Lesung im Karussell, -1 wenn keine oder mehrere passen."""
    werte = sorted(((strecken_aehnlichkeit(gelesen, n), i) for i, n in enumerate(strecken)), reverse=True)
    if not werte or werte[0][0] < SCHWELLE:
        return -1
    if len(werte) > 1 and werte[0][0] - werte[1][0] < ABSTAND:
        return -1
    return werte[0][1]


def laengen_laden() -> dict[str, float]:
    """Streckenlaenge je Name, wie der Schirm sie zeigt -- aus den nachgezeichneten Karten."""
    raus: dict[str, float] = {}
    for datei in sorted(KARTEN.glob("*.json")):
        d = json.loads(datei.read_text(encoding="utf-8"))
        if d.get("name") and d.get("routeLengthKm"):
            raus[d["name"]] = float(d["routeLengthKm"])
    raus.update(LAENGE_NACHTRAG)
    return raus


def leiste_verorten(leiste: list, laenge, laengen: list) -> int:
    """Wie ScanScreen.LeisteVerorten: Index aus den Kilometerzahlen der Kachelleiste.

    leiste[k] ist die Zahl in Kachel k (0 = vorige Strecke, 1 = gewaehlte), None wo nichts
    gelesen wurde. Eine Fehllesung ist erlaubt ("23.1" als "3.1").
    """
    n = len(laengen)
    kandidaten = []
    for i in range(n):
        if laenge is not None and laengen[i] is not None and abs(laengen[i] - laenge) > 0.05:
            continue
        treffer = fehler = 0
        for k, wert in enumerate(leiste):
            if wert is None:
                continue
            soll = laengen[(i + k - 1) % n]
            if soll is None:
                continue
            if abs(soll - wert) <= 0.05:
                treffer += 1
            else:
                fehler += 1
        if fehler <= 1 and treffer >= min(3, n - 1):
            kandidaten.append((treffer - 2 * fehler, i))
    kandidaten.sort(reverse=True)
    if not kandidaten or (len(kandidaten) > 1 and kandidaten[1][0] == kandidaten[0][0]):
        return -1
    return kandidaten[0][1]


def main() -> int:
    katalog = json.loads(KATALOG.read_text(encoding="utf-8"))
    global ALLE_LAENGEN
    ALLE_LAENGEN = laengen_laden()
    quelle = katalog["route_carousel_by_category"]
    kategorien: dict[str, dict] = {}
    fehler = 0
    for name, liste in quelle.items():
        if name == "_comment":
            continue
        eintraege = sorted(liste, key=lambda e: e["route_index"])
        indizes = [e["route_index"] for e in eintraege]
        if indizes != list(range(len(eintraege))):
            print(f"{name}: route_index nicht lueckenlos 0..{len(eintraege) - 1}: {indizes}")
            fehler += 1
        strecken = [e["name"] for e in eintraege]
        for i, a in enumerate(strecken):
            if len(kopf(a)) < 3:
                print(f"{name}: '{a}' hat einen zu kurzen Kopf '{kopf(a)}'")
                fehler += 1
            for b in strecken[i + 1:]:
                if gleiche_strecke(a, b) or gleiche_strecke(b, a):
                    print(f"{name}: '{a}' und '{b}' sind ueber den Kopf nicht unterscheidbar")
                    fehler += 1
            # Laufschrift: nur lange Namen laufen (ab ~25 Zeichen ist der Titel beschnitten),
            # vorn um bis zu drei Zeichen geschoben, hinten auf 26 beschnitten. Die Lesung
            # mit Schub 0 muss treffen; eine geschobene darf hoechstens unentschieden sein.
            fenster = [a[s:s + 26] for s in (range(0, 4) if len(a) > 24 else [0])]
            for schub, f in enumerate(fenster):
                wo = lokalisiere(f, strecken)
                if wo != i and (schub == 0 or wo >= 0):
                    print(f"{name}: Fenster '{f}' von '{a}' landet auf {wo}, nicht {i}")
                    fehler += 1
            # Mitten im Gattungswort beschnitten: Kopf plus zwei Zeichen.
            k = kopf(a)
            if len(falte(a)) > len(k) + 2:
                halb = falte(a)[:len(k) + 2]
                wo = lokalisiere(halb, strecken)
                if wo not in (i, -1):
                    print(f"{name}: beschnitten '{halb}' von '{a}' landet auf {wo}, nicht {i}")
                    fehler += 1
            # OCR-Schaden: ein Zeichen mitten im Kopf falsch.
            k = kopf(a)
            if len(k) >= 7:
                kaputt = k[:3] + "x" + k[4:] + falte(a)[len(k):]
                wo = lokalisiere(kaputt, strecken)
                if wo != i:
                    print(f"{name}: beschaedigt '{kaputt}' von '{a}' landet auf {wo}, nicht {i}")
                    fehler += 1
        laengen = [ALLE_LAENGEN.get(n) for n in strecken]
        for n, l in zip(strecken, laengen):
            if l is None:
                print(f"{name}: keine Laenge fuer '{n}'")
                fehler += 1
        # Jede Position muss sich aus der Leiste eindeutig ergeben -- auch ohne die vorige
        # Kachel (Listenanfang) und mit nur drei gelesenen Zahlen.
        n = len(laengen)
        for i in range(n):
            voll = [laengen[(i + k - 1) % n] for k in range(min(7, n + 1))]
            anfang = [None] + voll[1:4]
            for probe in (voll, anfang):
                wo = leiste_verorten(probe, laengen[i], laengen)
                if wo != i:
                    print(f"{name}: Leiste {probe} von '{strecken[i]}' landet auf {wo}, nicht {i}")
                    fehler += 1
        kategorien[name] = {"landmark": WAHRZEICHEN[name], "routes": strecken, "lengths": laengen}
    # Kein Leistenfenster einer Kategorie darf in einer ANDEREN Kategorie einen Ort finden --
    # sonst haelt der Navigator eine fremde offene Liste fuer die gesuchte.
    for name, k in kategorien.items():
        laengen = k["lengths"]
        n = len(laengen)
        for i in range(n):
            for probe in ([laengen[(i + j - 1) % n] for j in range(min(7, n + 1))],
                          [None] + [laengen[(i + j) % n] for j in range(min(3, n))]):
                for anderer, ak in kategorien.items():
                    if anderer == name:
                        continue
                    wo = leiste_verorten(probe, laengen[i], ak["lengths"])
                    if wo >= 0:
                        print(f"{name} #{i} ({k['routes'][i]}): Leiste {probe} passt auch in {anderer} #{wo}")
                        fehler += 1
    if fehler:
        print(f"{fehler} Problem(e) -- nichts geschrieben.")
        return 1
    raus = {
        "format": "fhc-scan-routes-1",
        "_comment": [
            "Built by scripts/build_scan_routes.py from config/fh6_board_catalogue.json.",
            "routes: the route carousel's order per Rivals category, index 0 = anchor.",
            "landmark: a pattern that appears only in this category's open route list.",
            "lengths: the route length the screen shows (data/route_maps), km, per route.",
        ],
        "categories": kategorien,
    }
    ZIEL.write_text(json.dumps(raus, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"{ZIEL.name}: {len(kategorien)} categories, "
          f"{sum(len(k['routes']) for k in kategorien.values())} routes")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
