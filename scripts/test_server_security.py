"""Die Sicherheitsregeln des Servers pruefen -- ohne Netz, gegen Wegwerf-Dateien.

    python scripts/test_server_security.py

Jede Pruefung ist so gebaut, dass sie ohne die jeweilige Regel FEHLSCHLAEGT:
eine Runde mit Markup im Feld, eine wiederholte Admin-Unterschrift, ein
Kontingent, das voll ist, eine langsamere Runde, eine Kette von Fehlversuchen.
Gruen heisst also, dass die Regel greift -- nicht nur, dass nichts abgestuerzt ist.
"""
from __future__ import annotations

import json
import re
import secrets
import sys
import tempfile
import time
from pathlib import Path

WS = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(WS / "server"))

import analytics_api as api  # noqa: E402
import ip_bans  # noqa: E402
import lap_submissions as laps  # noqa: E402

fehler = 0


def pruefe(gut: bool, text: str) -> None:
    global fehler
    print(("  ok   " if gut else "  FEHL ") + text)
    if not gut:
        fehler += 1


tmp = Path(tempfile.mkdtemp())
laps.KEYS_FILE = tmp / "submit_keys.json"
laps.LAPS_DIR = tmp / "laps"
laps.ANMELDE_VERSUCHE = tmp / "register_attempts.json"
api.FEATURES_FILE = tmp / "features.json"
api.FEATURES_FILE.write_text(json.dumps({"lap_submissions": True}), encoding="utf-8")
api.UPLOAD_ATTEMPTS_FILE = tmp / "upload_attempts.json"
ip_bans.DATEI = tmp / "ip_bans.json"

# Eine kleine Bestenliste: "Test Circuit", Klasse A, Auto 1234 mit 60.000 s gueltig
# (und einer UNGUELTIGEN 50.000 s, die nicht zaehlen darf).
KLASSEN = ["A", "B", "C", "D", "R", "S1", "S2"]
api.DATASET_FILE = tmp / "laps.json"
api.DATASET_FILE.write_text(json.dumps({
    "flags": ["clean"], "tracks": ["Test Circuit"], "classes": KLASSEN,
    "carIds": [1234, 5678], "carNames": ["Test Car", "Other Car"], "carMeta": [{}, {}],
    "categories": [], "meta": {},
    "boards": [{"c": 0, "t": 0, "k": KLASSEN.index("A"),
                "gcar": [0, 0, 1], "gsig": [1, 0, 1], "gcount": [1, 1, 1],
                "lgrp": [0, 1, 2], "lms": [60000, 50000, 70000], "lrank": [2, 1, 3]}],
}), encoding="utf-8")
A = 3  # PI-Reihenfolge D C B A -> A ist 3


class H(dict):
    def get(self, k, d=None):
        return super().get(k, d)


# Echte oeffentliche Adressen: die Dokumentationsnetze (198.51.100.x, 203.0.113.x)
# zaehlt Python zu den privaten -- dort griffe die Heimnetz-Ausnahme.
def aufruf(method, path, body=b"", headers=None, client="93.184.216.34"):
    status, typ, payload = api.handle(method, path, H(headers or {}), body,
                                      keys={"admin": "adminsecret", "keys": {}},
                                      client=client)
    try:
        return status, json.loads(payload.decode("utf-8"))
    except Exception:
        return status, payload


def anmelden(hw="ab" * 20, tag="Tester", client="93.184.216.34"):
    return aufruf("POST", "/api/lap/register",
                  json.dumps({"hardware": hw, "gamertag": tag}).encode(), client=client)


def admin(method, path, body=b""):
    stamp = str(int(time.time()))
    sig = api.admin_signature("adminsecret", method, path, stamp, body)
    return aufruf(method, path, body, {"X-Forza-Timestamp": stamp, "X-Forza-Signature": sig})


st, d = anmelden()
pruefe(st == 200 and "secret" in d, f"Anmeldung ({st})")
install, geheim = d["install_id"], d["secret"]


def einreichen(runde: dict, wer=None, schluessel=None):
    body = json.dumps({"lap": runde}).encode()
    stamp = str(int(time.time()))
    nonce = secrets.token_hex(12)
    sig = laps.signature(schluessel or geheim, "POST", "/api/lap/submit", stamp, nonce, body)
    return aufruf("POST", "/api/lap/submit", body, {
        "X-Forza-Install": wer or install, "X-Forza-Timestamp": stamp,
        "X-Forza-Nonce": nonce, "X-Forza-Signature": sig})


def runde(sek=55.0, **extra):
    r = {"lapSeconds": sek, "lengthMetres": 2000.0, "performanceIndex": 790,
         "carOrdinal": 1234, "carClass": A, "track": "Test Circuit", "course": "c",
         "recordedAt": secrets.token_hex(4),
         "samples": [{"Seconds": i * abs(sek) / 10, "Metres": i * 200.0, "X": i * 200.0, "Z": 0.0}
                     for i in range(11)]}
    r.update(extra)
    return r


def strikes(wer):
    return len(laps.load_keys()["installs"][wer].get("strikes", []))


# ---------------------------------------------------------------- XSS
boese = '<img src=x onerror="alert(1)">'
st, d = einreichen(runde(54.0, carName=boese + "\x00\x07", carOrdinal="1234",
                         **{"<script>": 1, "extra": {"a<b": 2, "ok": boese}}))
pruefe(st == 200, f"Runde mit Markup in Nebenfeldern angenommen, aber gesaeubert ({st} {d if st != 200 else ''})")
dateien = sorted(laps.LAPS_DIR.glob("*.json")) if laps.LAPS_DIR.exists() else []
gespeichert = json.loads(dateien[0].read_text(encoding="utf-8"))["lap"] if dateien else {}
pruefe(gespeichert.get("carOrdinal") == 1234, "carOrdinal als Zahl abgelegt")
pruefe("\x00" not in gespeichert.get("carName", "") and "\x07" not in gespeichert.get("carName", ""),
       "Steuerzeichen aus carName entfernt")
pruefe("<script>" not in gespeichert, "unerlaubter Feldname verworfen")
pruefe("a<b" not in gespeichert.get("extra", {}), "unerlaubter Feldname auch verschachtelt verworfen")
st, d = einreichen(runde(53.0, carOrdinal=boese))
pruefe(st != 200, f"carOrdinal mit Markup: Runde ohne Auto abgewiesen ({st})")
admin_html = (WS / "server" / "admin_page.html").read_text(encoding="utf-8")
roh = [z for z in admin_html.splitlines()
       if re.search(r"<\/?[a-z]", z)
       and re.search(r"\+\s*\(?\s*(r|lap|x)\.(?!hidden\s*\?|banned\s*\?)[A-Za-z]", z)]
pruefe(not roh, "Verwaltungsseite setzt keine Rundenfelder unmaskiert ein"
       + ("" if not roh else f": {roh[0].strip()[:60]}"))

# ---------------------------------------------------------------- Bestenliste
k = laps.load_keys(); k["installs"][install]["strikes"] = []; laps.save_keys(k)
st, d = einreichen(runde(52.0))
pruefe(st == 200, f"schneller als die Bestenliste (52 < 60 s) angenommen ({st} {d if st != 200 else ''})")
st, d = einreichen(runde(52.5))
pruefe(st == 409, f"langsamer als die eigene frueher eingereichte Zeit abgewiesen ({st})")
st, d = einreichen(runde(53.0, recordedAt="x1"))
pruefe(st == 409 and strikes(install) == 0,
       f"knapp langsamer (53 gegen 52 eingereicht, 1.9 %) abgewiesen OHNE Strafpunkt ({st}, {strikes(install)})")
st, d = einreichen(runde(40.0, carOrdinal=9999))
pruefe(st == 200 and "car not on this leaderboard yet" in d.get("flags", []),
       f"neues Auto (nicht auf dem Board) angenommen und gekennzeichnet ({st})")
st, d = einreichen(runde(45.0, track="Nowhere Ring"))
pruefe(st == 422, f"Strecke ohne Bestenliste abgewiesen ({st})")
st, d = einreichen(runde(58.0, carOrdinal=5678))
pruefe(st == 200, f"Auto 5678: 58 s schlaegt dessen 70 s ({st})")
st, d = einreichen(runde(51.0, carOrdinal=1234, carClass=2))
pruefe(st == 422, f"falsche Klasse (B, kein Board) abgewiesen ({st})")
gespeicherte = len(list(laps.LAPS_DIR.glob("*.json")))
pruefe(gespeicherte == 4, f"nur angenommene Runden abgelegt ({gespeicherte} statt 4)")
st, d = aufruf("GET", "/api/lap/list")
pruefe(all("samples" not in x["lap"] for x in d["laps"]), "oeffentliche Liste ohne Telemetrie")
pruefe(all("install_id" not in x for x in d["laps"]), "oeffentliche Liste ohne install_id")

# ---------------------------------------------------------------- Strafpunkte -> Sperre
st, d2 = anmelden(hw="cd" * 20, tag="Cheater")
betrueger, bgeheim = d2["install_id"], d2["secret"]
stati = []
for i, sek in enumerate([80.0, -5.0, 90.0, 85.0, 95.0, 30.0]):
    s, _ = einreichen(runde(sek, recordedAt=f"c{i}", carOrdinal=5678), betrueger, bgeheim)
    stati.append(s)
eintrag = laps.load_keys()["installs"][betrueger]
pruefe(bool(eintrag.get("banned") and eintrag.get("banned_auto")),
       f"nach 5 abgewiesenen Einreichungen automatisch gesperrt ({stati})")
pruefe(stati[-1] == 403, f"danach wird auch eine gute Runde abgewiesen ({stati[-1]})")
st, _ = anmelden(hw="cd" * 20, tag="Cheater2")
pruefe(st == 403, f"dieselbe Maschine kann sich nicht neu anmelden ({st})")
st, bans = admin("GET", "/api/admin/bans")
pruefe(st == 200 and any(x["install_id"] == betrueger and x["banned_auto"] for x in bans["installs"]),
       "Sperre erscheint in /api/admin/bans")
st, _ = admin("POST", "/api/admin/lap/unban", json.dumps({"install_id": betrueger}).encode())
e2 = laps.load_keys()["installs"][betrueger]
pruefe(st == 200 and not e2.get("banned") and not e2.get("strikes"),
       "Aufheben entsperrt und loescht die Strafpunkte")

# ---------------------------------------------------------------- Spieler an einer Runde sperren
eine = sorted(laps.LAPS_DIR.glob("*.json"))[0].stem
st, erg = admin("POST", "/api/admin/lap/ban", json.dumps({"lap_id": eine}).encode())
pruefe(st == 200 and erg.get("banned"), f"Spieler ueber eine seiner Runden gesperrt ({st})")
st, _ = admin("POST", "/api/admin/lap/unban", json.dumps({"install_id": install}).encode())
pruefe(st == 200 and not laps.load_keys()["installs"][install].get("banned"),
       "Unban hebt wirklich auf (frueher sperrte /unban erneut)")

# ---------------------------------------------------------------- Kontingent
alt = laps.RUNDEN_JE_INSTALL_TAG
laps.RUNDEN_JE_INSTALL_TAG = 0
st, _ = einreichen(runde(20.0, carOrdinal=4444))
laps.RUNDEN_JE_INSTALL_TAG = alt
pruefe(st == 429, f"Tageskontingent greift ({st})")

# ---------------------------------------------------------------- zu viele Messpunkte
viele = runde(samples=[{"Seconds": i * 0.001, "Metres": i * 0.04, "X": i * 0.04, "Z": 0.0}
                       for i in range(laps.MAX_PROBEN + 1)])
try:
    laps.pruefe_runde(viele)
    pruefe(False, "zu viele Messpunkte abgewiesen")
except laps.SubmitError as e:
    pruefe(e.status == 413, f"zu viele Messpunkte abgewiesen ({e.status})")

# ---------------------------------------------------------------- Admin-Wiederholung
stamp = str(int(time.time()))
rumpf = json.dumps({"run_id": "gibt_es_nicht_1", "hidden": True, "reason": "t"}).encode()
sig = api.admin_signature("adminsecret", "POST", "/api/admin/visibility", stamp, rumpf)
kopf = {"X-Forza-Timestamp": stamp, "X-Forza-Signature": sig}
api.VISIBILITY_FILE = tmp / "visibility.json"
st1, _ = aufruf("POST", "/api/admin/visibility", rumpf, headers=kopf)
st2, _ = aufruf("POST", "/api/admin/visibility", rumpf, headers=kopf)
pruefe(st1 == 200 and st2 == 409, f"aendernde Admin-Unterschrift gilt genau einmal ({st1}, dann {st2})")
sig = api.admin_signature("adminsecret", "GET", "/api/admin/usage", stamp, b"")
g1, _ = aufruf("GET", "/api/admin/usage", headers={"X-Forza-Timestamp": stamp, "X-Forza-Signature": sig})
g2, _ = aufruf("GET", "/api/admin/usage", headers={"X-Forza-Timestamp": stamp, "X-Forza-Signature": sig})
pruefe(g1 == 200 and g2 == 200, f"zweimal dasselbe GET (Doppelklick auf Reload) geht ({g1}, {g2})")

# ---------------------------------------------------------------- Adresssperre
falsch = {"X-Forza-Timestamp": str(int(time.time())), "X-Forza-Signature": "00" * 32}
stati = [aufruf("GET", "/api/admin/usage", headers=falsch, client="8.8.4.4")[0] for _ in range(11)]
pruefe(stati[-1] == 403 and stati.count(401) == 10,
       f"10 falsche Admin-Anmeldungen sperren eine Adresse von aussen ({stati[-2:]})")
st, _ = aufruf("GET", "/api/lap/list", client="8.8.4.4")
pruefe(st == 403, f"gesperrte Adresse kann nicht einreichen/abfragen ({st})")
st, _ = aufruf("GET", "/api/summary", client="8.8.4.4")
pruefe(st != 403, f"gesperrte Adresse darf die oeffentliche Zusammenfassung weiter lesen ({st})")
stati = [aufruf("GET", "/api/admin/usage", headers=falsch, client="192.168.1.20")[0] for _ in range(12)]
pruefe(403 not in stati, "eine Adresse aus dem Heimnetz wird nie gesperrt")
st, bans = admin("GET", "/api/admin/bans")
pruefe(any(x["ip"] == "8.8.4.4" and x["banned"] for x in bans["ips"]),
       "Adresssperre erscheint in /api/admin/bans")
pruefe(any(x["ip"] == "192.168.1.20" and x["home_network"] and not x["banned"] for x in bans["ips"]),
       "Heimnetz-Adresse erscheint, aber ungesperrt")
st, _ = admin("POST", "/api/admin/ip/unban", json.dumps({"ip": "8.8.4.4"}).encode())
st2, _ = aufruf("GET", "/api/admin/usage", headers=falsch, client="8.8.4.4")
pruefe(st == 200 and st2 == 401, f"aufgehobene Adresse darf wieder anklopfen ({st2})")

# ---------------------------------------------------------------- Kennung als Pfad
try:
    laps.set_hidden("../../config/contrib_keys", True)
    pruefe(False, "Pfad als Runden-Kennung abgewiesen")
except laps.SubmitError as e:
    pruefe(e.status == 400, f"Pfad als Runden-Kennung abgewiesen ({e.status})")

# ---------------------------------------------------------------- Zusammenfassung gemerkt
ds = tmp / "summary.json"
ds.write_text(json.dumps({"meta": {}, "tracks": [], "classes": [], "boards": []}), encoding="utf-8")
a = api.summary(ds); b = api.summary(ds)
pruefe(a["version"] == b["version"] and api._SUMMARY_CACHE.get("schluessel") is not None,
       "Zusammenfassung wird gemerkt")
ds.write_text(json.dumps({"meta": {}, "tracks": ["x"], "classes": [], "boards": []}), encoding="utf-8")
pruefe(api.summary(ds)["version"] != a["version"], "geaenderter Datensatz ergibt eine neue Zusammenfassung")

# ---------------------------------------------------------------- Bremse
import serve_analytics as srv  # noqa: E402
jetzt = 1000.0
erlaubt = sum(1 for _ in range(200) if srv.darf("1.2.3.4", "api", jetzt) == 0.0)
pruefe(erlaubt == srv.GRENZEN["api"][1], f"Bremse: {erlaubt} von 200 schnellen Anfragen durch")
pruefe(srv.darf("5.6.7.8", "api", jetzt) == 0.0, "andere Adresse ist nicht betroffen")
pruefe(srv.darf("1.2.3.4", "api", jetzt + 5) == 0.0, "nach einer Pause geht es weiter")

print(f"{'Alle Regeln greifen.' if fehler == 0 else str(fehler) + ' Regel(n) greifen NICHT.'}")
sys.exit(1 if fehler else 0)
