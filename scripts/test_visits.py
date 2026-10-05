"""Besuche, Laender und Downloads je Fassung -- an Faellen, deren Ergebnis feststeht.

    python scripts/test_visits.py

Schreibt nur in einen Temp-Ordner (FORZA_VISITS_FILE, FORZA_DOWNLOADS_FILE), nie in
die echten Zahlen. Die Laenderdatenbank muss unter data/runtime/geoip liegen
(server/geoip.py holt sie); ohne sie werden die Laenderpruefungen uebersprungen.
"""
from __future__ import annotations

import json
import os
import sys
import tempfile
import zipfile
from pathlib import Path

WS = Path(__file__).resolve().parent.parent
TMP = Path(tempfile.mkdtemp())
os.environ["FORZA_VISITS_FILE"] = str(TMP / "visits.json")
os.environ["FORZA_DOWNLOADS_FILE"] = str(TMP / "downloads.json")
sys.path.insert(0, str(WS / "server"))
import downloads  # noqa: E402
import geoip  # noqa: E402
import visits  # noqa: E402

fehler = 0


def pruefe(gut: bool, text: str) -> None:
    global fehler
    print(("  ok   " if gut else "  FEHL ") + text)
    fehler += 0 if gut else 1


UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/140.0 Safari/537.36"
UA2 = "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_6) AppleWebKit/605.1.15 Safari/605.1.15"
T0 = 1790000000.0                      # 2026-09-21 14:13 UTC
SEITE = "/rivals_auto_wertung.html"


def main() -> int:
    d = lambda: visits._laden()  # noqa: E731
    tag0 = visits._utc(T0).date().isoformat()
    std0 = visits._utc(T0).strftime("%Y-%m-%dT%H")

    pruefe(visits.note_page(SEITE, "8.8.8.8", UA, T0) == "counted", "ein Besuch zaehlt")
    visits.note_page(SEITE, "8.8.8.8", UA, T0 + 60)
    pruefe(d()["days"][tag0] == [2, 1] and d()["hours"][std0] == [2, 1],
           f"derselbe Besucher zweimal: 2 Aufrufe, 1 Besucher ({d()['days'][tag0]})")
    visits.note_page("/app", "8.8.8.8", UA2, T0 + 120)
    pruefe(d()["days"][tag0] == [3, 2], "anderer Browser = anderer Besucher")
    pruefe(d()["pages"][tag0] == {"car ratings": 2, "app download": 1}, f"Seiten: {d()['pages'][tag0]}")

    pruefe(visits.note_page(SEITE, "8.8.4.4", "Mozilla/5.0 (compatible; Googlebot/2.1)", T0) == "bot", "Googlebot ist ein Bot")
    pruefe(visits.note_page(SEITE, "8.8.4.4", "", T0) == "bot", "ohne Browserkennung ist ein Bot")
    pruefe(visits.note_page(SEITE, "8.8.4.4", "Mozilla/5.0 HeadlessChrome/140", T0) == "bot",
           "ein Pruefbrowser (headless) zaehlt nicht")
    pruefe(visits.note_page(SEITE, "192.168.1.20", UA, T0) == "own", "das eigene Heimnetz zaehlt nicht mit")
    eigene = sorted(visits._eigene_ip[1]) or ["(nicht aufgeloest)"]
    if visits.eigenes_netz(eigene[0]):
        pruefe(visits.note_page(SEITE, eigene[0], UA, T0) == "own",
               f"die eigene oeffentliche Adresse ({eigene[0]}) zaehlt nicht mit")
    pruefe(visits.note_page("/admin", "8.8.8.8", UA, T0) == "not a page", "die Verwaltungsseite ist kein Besuch")
    pruefe(visits.note_page("/api/summary", "8.8.8.8", UA, T0) == "not a page", "eine Schnittstelle ist kein Besuch")
    pruefe(d()["bots"][tag0] == 3 and d()["own"][tag0] >= 1, "Bots und eigene Aufrufe werden gesondert gezaehlt")

    # Naechster Tag: neues Salz, derselbe Mensch ist ein neuer Tagesbesucher.
    salz0 = d()["salt"]
    visits.note_page(SEITE, "8.8.8.8", UA, T0 + 86400)
    tag1 = visits._utc(T0 + 86400).date().isoformat()
    pruefe(d()["salt"] != salz0 and d()["days"][tag1] == [1, 1],
           "am naechsten Tag: neues Salz, die Hashes von gestern sind weg")
    pruefe(not any(k.startswith("h" + std0) for k in d()["seen"]), "keine Stunden-Hashes des Vortags mehr")

    if geoip.status()["available"]:
        g = d()["geo"][tag0]
        pruefe("US" in g and "California" in g["US"][2], f"Land und Region aus der lokalen Datenbank: {list(g)}")
        pruefe(g["US"][0] == 3 and g["US"][1] == 2, f"je Land Aufrufe und Besucher ({g['US'][:2]})")
        visits.note_page(SEITE, "2a00:1450:4001:80e::200e", UA, T0)
        pruefe("DE" in d()["geo"][tag0], "auch IPv6 bekommt ein Land")
    else:
        print("  --   Laenderdatenbank fehlt, Laenderpruefungen uebersprungen")

    s = visits.summary(T0)
    roh = json.dumps(s)
    pruefe("salt" not in s and "seen" not in s and d()["salt"] not in roh,
           "die Auskunft fuer die Verwaltung enthaelt weder Salz noch Hashes")
    visits.flush()
    pruefe(json.loads(Path(os.environ["FORZA_VISITS_FILE"]).read_text())["days"][tag0][0] >= 3,
           "flush schreibt die Zaehler auf die Platte")

    # ---- Downloads je Fassung
    pruefe(downloads.herkunft("") == "app" and downloads.herkunft("ForzaGripHaptics/2026.9.15.0") == "app"
           and downloads.herkunft(UA) == "browser" and downloads.herkunft("curl/8.9") == "other",
           "Herkunft: App, Browser, anderes")
    pruefe(not downloads.zaehlt("HEAD", None) and not downloads.zaehlt("GET", "bytes=500-")
           and downloads.zaehlt("GET", "bytes=0-") and downloads.zaehlt("GET", None),
           "HEAD und fortgesetzte Downloads zaehlen nicht")
    paket = TMP / "forza-grip-haptics-latest.zip"
    with zipfile.ZipFile(paket, "w") as z:
        z.writestr("x.txt", "x")
    paket.with_name(paket.name + ".meta.json").write_text(json.dumps(
        {"build": "abc123", "name": "forza-grip-haptics-20260925.zip", "built_at": "2026-09-25T10:00:00+02:00"}))
    f = downloads.fassung_des_pakets(paket)
    pruefe(f["id"] == "abc123" and f["name"].endswith("20260925.zip"), "Fassung der App aus der Metadatei")
    downloads.note("haptics", f, UA, T0)
    downloads.note("haptics", f, "", T0 + 10)
    downloads.note("haptics", {"id": "old999", "name": "alt.zip", "built": ""}, UA, T0 - 86400)
    downloads.note("tool", downloads.fassung_aus_bytes(b"werkzeug", "forza-contrib-tool.zip"), UA, T0)
    v = {(r["tool"], r["version"]): r for r in downloads.summary()["versions"]}
    pruefe(v[("haptics", "abc123")]["total"] == 2 and v[("haptics", "abc123")]["by"] == {"browser": 1, "app": 1},
           "je Fassung gezaehlt, nach Herkunft getrennt")
    pruefe(("haptics", "old999") in v and len([k for k in v if k[0] == "tool"]) == 1,
           "alte Fassungen und das Werkzeug stehen getrennt")

    print("Alle Pruefungen bestanden." if fehler == 0 else f"{fehler} Pruefung(en) FEHLGESCHLAGEN.")
    return 1 if fehler else 0


if __name__ == "__main__":
    sys.exit(main())
