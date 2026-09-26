"""Ende zu Ende gegen den LIVE-Server: die App reicht ein, die Seite zeigt es, die
Verwaltungsseite loescht, stellt wieder her, sperrt und entsperrt -- im echten Browser.

    python scripts/test_submit_e2e.py [--server URL]

1. Eine echte Runde aus dem Archiv, die die Bestenliste schlaegt, ueber den
   Einreichweg der App (--submit-lap), unter einer Test-Kennung in einem eigenen
   Ordner -- die echte Anmeldung des Nutzers bleibt unberuehrt.
2. Dieselbe Runde noch einmal: das Buch der App haelt sie auf. Ohne Buch: der
   Server weist sie ab (nicht schneller als die schon eingereichte).
3. Die Runde steht in /api/lap/list, und die Logik der Live-Seite (dieselbe
   submitted_laps.js) legt sie als neue Bestzeit des Autos auf das Board.
4. Die Verwaltungsseite in Edge (Playwright, headless), ueber einfaches HTTP --
   also mit dem eingebauten HMAC statt crypto.subtle: anmelden, nach Spieler
   filtern, loeschen, wiederherstellen, Spieler sperren, entsperren, aufraeumen.

Das Admin-Geheimnis kommt aus config/contrib_keys.json und wird nie ausgegeben.
"""
from __future__ import annotations

import argparse
import hashlib
import hmac
import json
import os
import re
import secrets
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.request
from pathlib import Path

WS = Path(__file__).resolve().parent.parent
EXE = WS / "haptics/ForzaHaptics.Tester/bin/Release/net9.0-windows10.0.19041.0/win-x64/FH Companion.exe"
sys.path.insert(0, str(WS / "server"))
import local_settings  # noqa: E402

LAPS = local_settings.app_data() / "laps"

fehler = 0


def pruefe(gut: bool, text: str) -> None:
    global fehler
    print(("  ok   " if gut else "  FEHL ") + text)
    if not gut:
        fehler += 1


def hole(server: str, pfad: str, admin: str | None = None):
    kopf = {"User-Agent": "forza-e2e"}
    if admin:
        stamp = str(int(time.time()))
        msg = "\n".join(["GET", pfad, stamp, hashlib.sha256(b"").hexdigest()])
        kopf["X-Forza-Timestamp"] = stamp
        kopf["X-Forza-Signature"] = hmac.new(admin.encode(), msg.encode(), hashlib.sha256).hexdigest()
    with urllib.request.urlopen(urllib.request.Request(server + pfad, headers=kopf), timeout=60) as r:
        return json.loads(r.read().decode("utf-8"))


def app(lap: Path, gamertag: str, heim: Path, server: str, dry: bool = False) -> str:
    env = dict(os.environ, FORZA_SUBMIT_HOME=str(heim))
    a = [str(EXE), "--submit-lap", str(lap), "--gamertag", gamertag, "--server", server]
    if dry:
        a.append("--dry-run")
    r = subprocess.run(a, capture_output=True, text=True, env=env, timeout=180)
    zeilen = [z for z in (r.stdout or "").splitlines() if z.strip()]
    return zeilen[-1] if zeilen else (r.stderr or "")[-300:]


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--server", default=local_settings.server())
    args = ap.parse_args()
    server = args.server.rstrip("/")
    admin = json.loads((WS / "config" / "contrib_keys.json").read_text(encoding="utf-8-sig")).get("admin")
    if not admin:
        print("kein Admin-Geheimnis in config/contrib_keys.json")
        return 2

    heim = Path(tempfile.mkdtemp(prefix="forza-e2e-"))
    gamertag = "E2E_" + secrets.token_hex(3)
    print(f"Server {server}, Test-Spieler {gamertag}")

    # ------------------------------------------------ 1. eine Runde, die die Bestenliste schlaegt
    kandidat = None
    for kurs in sorted(LAPS.iterdir()):
        cj = kurs / "course.json"
        if not cj.exists():
            continue
        name = json.loads(cj.read_text(encoding="utf-8-sig")).get("Name") or ""
        if not name or name.startswith("course_"):
            continue
        for lap in sorted(kurs.rglob("*.json")):
            if lap.name == "course.json":
                continue
            if app(lap, gamertag, heim, server, dry=True).startswith("would submit: faster than the leaderboard"):
                kandidat = lap
                break
        if kandidat:
            break
    pruefe(kandidat is not None, f"eine Runde gefunden, die die Bestenliste schlaegt ({kandidat and kandidat.name})")
    if not kandidat:
        return 1

    erst = app(kandidat, gamertag, heim, server)
    pruefe(erst.startswith("submitted"), f"die App reicht ein: {erst}")
    zwei = app(kandidat, gamertag, heim, server)
    pruefe(zwei.startswith("not submitted: not faster than the time already sent"),
           f"dieselbe Runde noch einmal haelt das Buch auf: {zwei}")
    (heim / "submitted_laps.json").unlink(missing_ok=True)
    drei = app(kandidat, gamertag, heim, server)
    pruefe(drei.startswith("server says not faster"), f"ohne Buch weist der SERVER sie ab: {drei}")

    # ------------------------------------------------ 2. auf der Seite
    liste = hole(server, "/api/lap/list")["laps"]
    meine = [x for x in liste if x.get("gamertag") == gamertag]
    pruefe(len(meine) == 1, f"genau eine Runde von {gamertag} in /api/lap/list ({len(meine)})")
    if not meine:
        return 1
    runde = meine[0]
    lap_id = runde["id"]
    ms = round(float(runde["lap"]["lapSeconds"]) * 1000)

    seite = urllib.request.urlopen(server + "/rivals_auto_wertung.html", timeout=120).read().decode("utf-8")
    seite = seite.replace("\r\n", "\n")
    m = re.search(r"const D = (\{.*?\});\n", seite, re.S)
    js = (WS / "scripts" / "submitted_laps.js").read_text(encoding="utf-8")
    probe = js + """
const D = JSON.parse(require('fs').readFileSync(0, 'utf8'));
const laps = %s;
const s = buildSubmitted(D, laps);
const r = [];
s.byBoard.forEach((karte, key) => karte.forEach((v, car) => r.push([key, D.carIds[car], v.ms, v.id])));
console.log(JSON.stringify({placed: s.placed, unplaced: s.unplaced, rows: r}));
""" % json.dumps(liste)
    ergebnis = subprocess.run(["node", "-e", probe], input=m.group(1) if m else "{}",
                              capture_output=True, text=True, encoding="utf-8", timeout=120)
    try:
        auswertung = json.loads(ergebnis.stdout)
    except ValueError:
        auswertung = {"rows": [], "unplaced": [ergebnis.stderr[-300:]]}
    platz = [r for r in auswertung["rows"] if r[3] == lap_id]
    pruefe(bool(platz) and platz[0][2] == ms,
           f"die Live-Seite legt die Runde aufs Board: {platz[0][0] if platz else auswertung.get('unplaced')}")

    # ------------------------------------------------ 3. Verwaltungsseite im Browser
    from playwright.sync_api import sync_playwright
    with sync_playwright() as p:
        browser = p.chromium.launch(channel="msedge", headless=True)
        seite = browser.new_page()
        seite.on("dialog", lambda d: d.accept())
        seite.goto(server + "/admin")
        # Ueber HTTP ist die Seite kein sicherer Kontext -> das eingebaute HMAC; ueber
        # HTTPS (seit 2026-09-24) ist sie einer -> crypto.subtle. Beide Wege muessen
        # die folgenden Schritte tragen.
        sicher = seite.evaluate("window.isSecureContext")
        erwartet = server.startswith("https://")
        pruefe(sicher is erwartet, "die Seite laeuft " + ("MIT sicherem Kontext (crypto.subtle)" if sicher
                                                           else "ohne sicheren Kontext (eingebautes HMAC)")
               + f", wie bei {server.split(':')[0]} erwartet")
        seite.fill("#secret", admin)
        seite.click("#unlock")
        seite.wait_for_selector("#laps table", timeout=30000)
        seite.fill("#f-player", gamertag)
        zeile = seite.locator("#laps tr", has_text=gamertag)
        pruefe(zeile.count() == 1, f"im Admin nach Spieler gefiltert: {zeile.count()} Zeile(n)")

        zeile.locator("button[data-hide='1']").click()
        seite.wait_for_timeout(2500)
        oeffentlich = [x["id"] for x in hole(server, "/api/lap/list")["laps"]]
        pruefe(lap_id not in oeffentlich, "im Browser geloescht -> von der oeffentlichen Liste verschwunden")

        seite.check("#f-hidden")
        seite.wait_for_timeout(2500)
        seite.fill("#f-player", gamertag)
        seite.locator("#laps tr", has_text=gamertag).locator("button[data-hide='0']").click()
        seite.wait_for_timeout(2500)
        oeffentlich = [x["id"] for x in hole(server, "/api/lap/list")["laps"]]
        pruefe(lap_id in oeffentlich, "im Browser wiederhergestellt -> wieder in der oeffentlichen Liste")

        seite.uncheck("#f-hidden")
        seite.wait_for_timeout(2500)
        seite.fill("#f-player", gamertag)
        seite.locator("#laps tr", has_text=gamertag).locator("button[data-ban]").click()
        seite.wait_for_timeout(3000)
        bans = hole(server, "/api/admin/bans", admin)
        gesperrt = [x for x in bans["installs"] if x.get("gamertag") == gamertag and x.get("banned")]
        pruefe(bool(gesperrt), "im Browser 'ban player' -> Installation gesperrt")
        seite.click("#bans-refresh")
        seite.wait_for_timeout(2500)
        bann_zeile = seite.locator("#bans-installs tr", has_text=gamertag)
        pruefe(bann_zeile.count() == 1, "die Sperre steht im Abschnitt Bans")
        bann_zeile.locator("button[data-unban-install]").click()
        seite.wait_for_timeout(3000)
        bans = hole(server, "/api/admin/bans", admin)
        pruefe(not [x for x in bans["installs"] if x.get("gamertag") == gamertag and x.get("banned")],
               "im Browser 'unban' -> Sperre aufgehoben")
        pruefe(isinstance(bans.get("ips"), list), f"Adressliste im Admin lesbar ({len(bans.get('ips', []))} Eintraege)")

        # Aufraeumen: die Testrunde nicht auf der Seite stehen lassen.
        seite.check("#f-hidden")
        seite.wait_for_timeout(2500)
        seite.fill("#f-player", gamertag)
        knopf = seite.locator("#laps tr", has_text=gamertag).locator("button[data-hide='1']")
        if knopf.count():
            knopf.click()
            seite.wait_for_timeout(2500)
        oeffentlich = [x["id"] for x in hole(server, "/api/lap/list")["laps"]]
        pruefe(lap_id not in oeffentlich, "Testrunde zum Schluss wieder ausgeblendet")
        browser.close()

    shutil.rmtree(heim, ignore_errors=True)
    print("Alle Schritte bestanden." if fehler == 0 else f"{fehler} Schritt(e) FEHLGESCHLAGEN.")
    return 1 if fehler else 0


if __name__ == "__main__":
    sys.exit(main())
