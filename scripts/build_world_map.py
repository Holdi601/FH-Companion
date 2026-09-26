"""Die Weltkarte der Verwaltungsseite bauen -- einmal, aus Natural Earth.

    python scripts/build_world_map.py

Schreibt server/assets/world-110m.json: je Land (ISO-3166-Kuerzel) ein SVG-Pfad in
der Equal-Earth-Projektion, dazu der Name. Die Verwaltungsseite laedt die Datei von
diesem Server -- kein Kartendienst, kein fremder Abruf (wie bei den Schriften).

## Quelle

Natural Earth, "Admin 0 - Countries", 1:110 Mio. (v5.1.2). Gemeinfrei: "Made with
Natural Earth. Free vector and raster map data @ naturalearthdata.com". Die
GeoJSON-Fassung kommt aus nvkelso/natural-earth-vector und wird nach
data/runtime/naturalearth/ geladen, falls sie fehlt.

## Warum Equal Earth

Flaechentreu: bei einer Heatmap nach Laendern ist die Flaeche die Tinte. In einer
Mercator-Karte waeren Kanada und Russland riesige Farbflaechen, die mehr Gewicht
behaupten, als ihre Zahlen haben.

## Kuerzel

Natural Earth fuehrt Frankreich, Norwegen und Kosovo in ISO_A2 als "-99"; die
richtigen Kuerzel stehen in ISO_A2_EH. Nordzypern und Somaliland haben keins -- sie
werden Zypern und Somalia zugeschlagen, so wie die Laenderdatenbank (DB-IP) sie
auch meldet. Die Antarktis bleibt weg.
"""
from __future__ import annotations

import json
import math
import sys
import urllib.request
from pathlib import Path

WS = Path(__file__).resolve().parent.parent
QUELLE = ("https://raw.githubusercontent.com/nvkelso/natural-earth-vector/v5.1.2/"
          "geojson/ne_110m_admin_0_countries.geojson")
ROH = WS / "data" / "runtime" / "naturalearth" / "ne_110m_admin_0_countries.geojson"
ZIEL = WS / "server" / "assets" / "world-110m.json"
BREITE = 960.0
ZUSCHLAG = {"N. Cyprus": "CY", "Somaliland": "SO"}

A1, A2, A3, A4 = 1.340264, -0.081106, 0.000893, 0.003796
M = math.sqrt(3) / 2


def equal_earth(lon: float, lat: float) -> tuple[float, float]:
    lam, phi = math.radians(lon), math.radians(lat)
    theta = math.asin(M * math.sin(phi))
    t2 = theta * theta
    t6 = t2 * t2 * t2
    x = (2 * math.sqrt(3) * lam * math.cos(theta)) / (3 * (A1 + 3 * A2 * t2 + t6 * (7 * A3 + 9 * A4 * t2)))
    y = theta * (A1 + A2 * t2 + t6 * (A3 + A4 * t2))
    return x, y


def main() -> int:
    if not ROH.exists():
        ROH.parent.mkdir(parents=True, exist_ok=True)
        with urllib.request.urlopen(QUELLE, timeout=120) as r:
            ROH.write_bytes(r.read())
    daten = json.loads(ROH.read_text(encoding="utf-8"))

    laender: dict[str, dict] = {}
    for f in daten["features"]:
        p = f["properties"]
        name = p.get("NAME") or p.get("ADMIN") or "?"
        cc = ZUSCHLAG.get(name) or p.get("ISO_A2_EH") or p.get("ISO_A2")
        if not cc or cc == "-99" or cc == "AQ":
            continue
        g = f["geometry"]
        polys = g["coordinates"] if g["type"] == "MultiPolygon" else [g["coordinates"]]
        eintrag = laender.setdefault(cc, {"n": p.get("NAME_EN") or name, "ringe": []})
        for poly in polys:
            for ring in poly:
                eintrag["ringe"].append([equal_earth(lon, lat) for lon, lat in ring])

    alle = [pt for e in laender.values() for r in e["ringe"] for pt in r]
    x0, x1 = min(p[0] for p in alle), max(p[0] for p in alle)
    y0, y1 = min(p[1] for p in alle), max(p[1] for p in alle)
    s = BREITE / (x1 - x0)
    hoehe = round((y1 - y0) * s, 1)

    raus = {}
    for cc, e in sorted(laender.items()):
        teile = []
        for ring in e["ringe"]:
            punkte = []
            for x, y in ring:
                pt = (round((x - x0) * s, 1), round((y1 - y) * s, 1))
                if not punkte or punkte[-1] != pt:
                    punkte.append(pt)
            if len(punkte) < 3:
                continue
            teile.append("M" + "L".join(f"{a:g} {b:g}" for a, b in punkte) + "Z")
        if teile:
            raus[cc] = {"n": e["n"], "d": "".join(teile)}

    ZIEL.parent.mkdir(parents=True, exist_ok=True)
    ZIEL.write_text(json.dumps({
        "source": "Made with Natural Earth (public domain), 1:110m admin 0 countries, v5.1.2",
        "projection": "Equal Earth",
        "viewBox": f"0 0 {BREITE:g} {hoehe:g}",
        "countries": raus,
    }, separators=(",", ":"), ensure_ascii=False), encoding="utf-8")
    print(f"{len(raus)} Laender, {ZIEL.stat().st_size // 1024} KB -> {ZIEL}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
