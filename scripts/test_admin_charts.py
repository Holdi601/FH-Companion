"""Die Grafiken der Verwaltungsseite im echten Browser -- mit erfundenen Zahlen.

    python scripts/test_admin_charts.py [--out ORDNER]

Baut in einem Temp-Ordner ein Jahr Besuche (25 Laender mit Regionen, Seiten,
eigene Aufrufe, Bots) und mehrere Fassungen der Werkzeuge, startet den Server
lokal NUR auf diesen Dateien (FORZA_VISITS_FILE, FORZA_DOWNLOADS_FILE), meldet
sich in Edge an und prueft:

  * jede Liniengrafik ist gezeichnet (SVG mit Pfaden), die Karte hat Laender und
    eingefaerbte Flaechen, der Tooltip erscheint beim Ueberfahren;
  * jeder Zeitraum-Knopf zeichnet ohne Skriptfehler;
  * hell, dunkel und in Telefonbreite -- als Bilder im Ausgabeordner.

Das Admin-Geheimnis kommt aus config/contrib_keys.json und wird nie ausgegeben.
"""
from __future__ import annotations

import argparse
import json
import math
import os
import random
import socket
import subprocess
import sys
import tempfile
import time
from datetime import datetime, timedelta, timezone
from pathlib import Path

WS = Path(__file__).resolve().parent.parent
LAENDER = [("DE", "Germany", "EU", "Europe", ["Bavaria", "Berlin", "North Rhine-Westphalia", "Hesse"]),
           ("US", "United States", "NA", "North America", ["California", "Texas", "New York", "Washington"]),
           ("GB", "United Kingdom", "EU", "Europe", ["England", "Scotland"]),
           ("NL", "The Netherlands", "EU", "Europe", ["North Holland", "South Holland"]),
           ("FR", "France", "EU", "Europe", ["Ile-de-France", "Occitanie"]),
           ("PL", "Poland", "EU", "Europe", ["Masovia"]), ("SE", "Sweden", "EU", "Europe", ["Stockholm"]),
           ("BR", "Brazil", "SA", "South America", ["Sao Paulo"]), ("JP", "Japan", "AS", "Asia", ["Tokyo"]),
           ("AU", "Australia", "OC", "Oceania", ["New South Wales", "Victoria"]),
           ("CA", "Canada", "NA", "North America", ["Ontario", "Quebec"]),
           ("IT", "Italy", "EU", "Europe", ["Lombardy"]), ("ES", "Spain", "EU", "Europe", ["Madrid"]),
           ("RU", "Russia", "EU", "Europe", ["Moscow"]), ("IN", "India", "AS", "Asia", ["Karnataka"]),
           ("MX", "Mexico", "NA", "North America", ["Mexico City"]), ("KR", "South Korea", "AS", "Asia", ["Seoul"]),
           ("ZA", "South Africa", "AF", "Africa", ["Gauteng"]), ("TR", "Turkey", "AS", "Asia", ["Istanbul"]),
           ("AT", "Austria", "EU", "Europe", ["Vienna"]), ("CH", "Switzerland", "EU", "Europe", ["Zurich"]),
           ("NO", "Norway", "EU", "Europe", ["Oslo"]), ("FI", "Finland", "EU", "Europe", ["Uusimaa"]),
           ("SG", "Singapore", "AS", "Asia", [""]), ("??", "", "", "", [])]


def daten(ordner: Path) -> None:
    rnd = random.Random(7)
    jetzt = datetime.now(timezone.utc)
    heute = jetzt.date()
    d = {"since": (heute - timedelta(days=400)).isoformat(), "day": heute.isoformat(), "salt": "x" * 64, "seen": {},
         "hours": {}, "days": {}, "geo": {}, "pages": {}, "own": {}, "bots": {}, "names": {}, "continents": {}}
    for i in range(400, -1, -1):
        tag = heute - timedelta(days=i)
        welle = 12 + 10 * math.sin(i / 20) + (400 - i) / 25
        u = max(0, int(rnd.gauss(welle, 4)))
        v = u + int(u * rnd.uniform(0.4, 1.6))
        iso = tag.isoformat()
        d["days"][iso] = [v, u]
        d["own"][iso] = rnd.randint(0, 4)
        d["bots"][iso] = rnd.randint(0, 9)
        d["pages"][iso] = {"car ratings": int(v * 0.7), "app download": int(v * 0.22), "contribute": v - int(v * 0.7) - int(v * 0.22)}
        geo = {}
        rest = u
        for k, (cc, name, kc, kn, regionen) in enumerate(LAENDER):
            if rest <= 0:
                break
            anteil = max(1, int(rest * (0.45 if k == 0 else 0.3))) if k < len(LAENDER) - 1 else rest
            anteil = min(anteil, rest)
            rest -= anteil
            eintrag = [anteil + anteil // 2, anteil, {}]
            for r in [x for x in regionen if x]:
                eintrag[2][r] = [max(1, anteil // len(regionen)), max(1, anteil // len(regionen))]
            geo[cc] = eintrag
            if cc != "??":
                d["names"][cc] = name
                d["continents"][cc] = [kc, kn]
        d["geo"][iso] = geo
    for h in range(48):
        s = jetzt - timedelta(hours=h)
        u = max(0, int(rnd.gauss(3 + 2 * math.sin(s.hour / 24 * 2 * math.pi), 1.5)))
        d["hours"][s.strftime("%Y-%m-%dT%H")] = [u + rnd.randint(0, 3), u]
    (ordner / "visits.json").write_text(json.dumps(d), encoding="utf-8")

    tools = {"haptics": {}, "tool": {}}
    for k, (fid, alter) in enumerate([("a1b2c3d4", 120), ("e5f6a7b8", 60), ("c9d0e1f2", 20), ("d3b89b68", 3)]):
        tage = {}
        for i in range(alter, -1, -1):
            tag = (heute - timedelta(days=i)).isoformat()
            tage[tag] = max(0, int(rnd.gauss(4 if i > alter - 7 else 1, 1.5)))
        tools["haptics"][fid] = {"name": f"forza-grip-haptics-2026{9 - k:02d}01.zip", "built": "",
                                 "first": (heute - timedelta(days=alter)).isoformat() + "T08:00:00+00:00",
                                 "last": heute.isoformat() + "T08:00:00+00:00", "total": sum(tage.values()),
                                 "by": {"app": sum(tage.values()) // 2, "browser": sum(tage.values()) - sum(tage.values()) // 2},
                                 "days": tage}
    for k, (fid, alter) in enumerate([("7f3a9c11e0b2", 90), ("0b44de12aa90", 25)]):
        tage = {(heute - timedelta(days=i)).isoformat(): rnd.randint(0, 2) for i in range(alter, -1, -1)}
        tools["tool"][fid] = {"name": "forza-contrib-tool.zip", "built": "",
                              "first": (heute - timedelta(days=alter)).isoformat() + "T08:00:00+00:00",
                              "last": heute.isoformat() + "T08:00:00+00:00", "total": sum(tage.values()),
                              "by": {"browser": sum(tage.values())}, "days": tage}
    (ordner / "downloads.json").write_text(json.dumps({"since": d["since"], "tools": tools}), encoding="utf-8")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=str(Path(tempfile.gettempdir()) / "forza-admin-charts"))
    args = ap.parse_args()
    aus = Path(args.out)
    aus.mkdir(parents=True, exist_ok=True)
    tmp = Path(tempfile.mkdtemp())
    daten(tmp)
    admin = json.loads((WS / "config" / "contrib_keys.json").read_text(encoding="utf-8-sig")).get("admin")
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        port = s.getsockname()[1]
    env = {**os.environ, "FORZA_VISITS_FILE": str(tmp / "visits.json"),
           "FORZA_DOWNLOADS_FILE": str(tmp / "downloads.json")}
    server = subprocess.Popen([sys.executable, str(WS / "server" / "serve_analytics.py"), "--host", "127.0.0.1",
                               "--port", str(port), "--nur-ausliefern"], cwd=str(WS), env=env,
                              stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    fehler = 0

    def pruefe(gut: bool, text: str) -> None:
        nonlocal fehler
        print(("  ok   " if gut else "  FEHL ") + text)
        fehler += 0 if gut else 1

    try:
        for _ in range(50):
            try:
                socket.create_connection(("127.0.0.1", port), timeout=0.2).close()
                break
            except OSError:
                time.sleep(0.2)
        basis = f"http://127.0.0.1:{port}"
        from playwright.sync_api import sync_playwright
        with sync_playwright() as p:
            browser = p.chromium.launch(channel="msedge", headless=True)
            for name, schema, breite in (("light", "light", 1280), ("dark", "dark", 1280), ("phone", "light", 390)):
                seite = browser.new_page(color_scheme=schema, viewport={"width": breite, "height": 900})
                fehlerliste: list[str] = []
                seite.on("pageerror", lambda e, f=fehlerliste: f.append(str(e)))
                seite.on("console", lambda m, f=fehlerliste: f.append(m.text) if m.type == "error" else None)
                fehlende: list[str] = []
                seite.on("response", lambda r, f=fehlende: f.append(r.url) if r.status == 404 else None)
                seite.goto(basis + "/admin")
                seite.fill("#secret", admin)
                seite.click("#unlock")
                seite.wait_for_selector("#map svg path", timeout=30000)
                seite.wait_for_selector("#vc0 svg path", timeout=30000)
                pfade = seite.evaluate("document.querySelectorAll('#map svg path').length")
                gefaerbt = seite.evaluate(
                    "Array.from(document.querySelectorAll('#map svg path')).filter(p => p.getAttribute('fill') !== 'var(--m0)').length")
                pruefe(pfade > 150 and gefaerbt >= 8, f"{name}: Karte mit {pfade} Laendern, {gefaerbt} eingefaerbt")
                for box in ("chart30", "visit-chart", "pages-chart", "vc0", "vc1"):
                    n = seite.evaluate(f"document.querySelectorAll('#{box} svg path').length")
                    pruefe(n >= 2, f"{name}: Liniengrafik #{box} hat {n} Linien")
                # Tooltip der Besuchsgrafik und der Karte
                seite.locator("#visit-chart").scroll_into_view_if_needed()
                box = seite.locator("#visit-chart svg").bounding_box()
                seite.mouse.move(box["x"] + box["width"] * 0.6, box["y"] + box["height"] * 0.5)
                pruefe(seite.evaluate("!document.querySelector('#visit-chart .tip').hidden"), f"{name}: Tooltip der Besuchsgrafik")
                de = seite.locator('#map path[data-cc="DE"]')
                de.hover()
                tip = seite.evaluate("document.querySelector('#map .tip').textContent")
                pruefe("Germany" in tip and "Visitors" in tip, f"{name}: Tooltip der Karte ({tip[:40]}...)")
                de.click()
                seite_text = seite.evaluate("document.getElementById('geo-side').textContent")
                pruefe("Bavaria" in seite_text, f"{name}: ein Klick auf Deutschland zeigt seine Regionen")
                # KEIN WAAGERECHTES SCROLLEN, auch nicht in Telefonbreite -- und wenn doch,
                # welche Elemente ueber den Rand ragen.
                zuBreit = seite.evaluate("""() => { const w = document.documentElement.clientWidth;
                  const raus = [];
                  for (const e of document.querySelectorAll("body *")) {
                    const r = e.getBoundingClientRect();
                    if (r.right > w + 1 && r.width > 0 && !(e.parentElement && e.parentElement.getBoundingClientRect().right > w + 1))
                      raus.push((e.id ? "#" + e.id : e.tagName.toLowerCase() + (e.className ? "." + String(e.className).split(" ")[0] : "")) + " " + Math.round(r.right));
                  }
                  return { breite: document.documentElement.scrollWidth, fenster: w, raus: raus.slice(0, 6) }; }""")
                pruefe(zuBreit["breite"] <= zuBreit["fenster"] + 1,
                       f"{name}: kein waagerechtes Scrollen ({zuBreit['breite']} von {zuBreit['fenster']} px)"
                       + (f" -- ragt hinaus: {zuBreit['raus']}" if zuBreit["breite"] > zuBreit["fenster"] + 1 else ""))
                seite.locator("#visit-chart").scroll_into_view_if_needed()
                seite.screenshot(path=str(aus / f"admin-{name}.png"), full_page=True)
                for r in ("hours", "weeks", "months", "years", "all", "days"):
                    seite.click(f'#ranges button[data-r="{r}"]')
                    n = seite.evaluate("document.querySelectorAll('#visit-chart svg path').length")
                    if r == "months":
                        seite.screenshot(path=str(aus / f"admin-{name}-months.png"), full_page=True)
                    pruefe(n >= 2, f"{name}: Zeitraum {r} gezeichnet")
                # Die Runden-Schnittstelle ist auf dem Pruefserver abgeschaltet und antwortet dann
                # absichtlich 404 (analytics_api: "soll aussehen, als gaebe es sie nicht").
                erwartet = [u for u in fehlende if "/api/lap/" in u or "/api/admin/lap" in u]
                echte = [f for f in fehlerliste if not (f.startswith("Failed to load resource") and len(erwartet) == len(fehlende))]
                pruefe(not echte, f"{name}: keine Skriptfehler" + (f" -- {echte[:2]} {fehlende[:3]}" if echte else ""))
                seite.close()
            browser.close()
    finally:
        server.terminate()
    print(f"Bilder: {aus}")
    print("Alle Pruefungen bestanden." if fehler == 0 else f"{fehler} Pruefung(en) FEHLGESCHLAGEN.")
    return 1 if fehler else 0


if __name__ == "__main__":
    sys.exit(main())
