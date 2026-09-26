"""Die Runden-Einreichung ueber ECHTES HTTP -- vom Anmelden bis zum Sperren.

    python scripts/test_lap_endpoints.py

## Warum zusaetzlich zu test_lap_submissions.py

Der andere Test ruft die Funktionen direkt. Er beweist, dass die Logik stimmt, und
sagt nichts darueber, ob sie ueber den Server ERREICHBAR ist. Genau dazwischen
liegen die Fehler, die man sonst erst im Betrieb findet: ein Pfad, der in der Weiche
fehlt; Kopfzeilen, die anders heissen als erwartet; ein Rumpf, den der Server gar
nicht erst liest; eine Antwort, die als HTML statt als JSON herauskommt.

Der Server laeuft dafuer hier im Prozess, auf einem freien Port, mit
Wegwerfverzeichnissen. Keine echte Schluesseldatei, kein echter Rundenbestand.
"""

from __future__ import annotations

import hashlib
import hmac
import json
import socket
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(WORKSPACE / "server"))

import analytics_api as api        # noqa: E402
import lap_submissions as laps     # noqa: E402
import serve_analytics as srv      # noqa: E402

fehler = 0


def pruefe(bedingung, text: str) -> None:
    global fehler
    if bedingung:
        print("  ok    " + text)
    else:
        fehler += 1
        print("  FEHL  " + text)


def freier_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def ruf(port: int, method: str, pfad: str, body: bytes | None = None,
        kopf: dict | None = None) -> tuple[int, dict]:
    anfrage = urllib.request.Request(
        "http://127.0.0.1:%d%s" % (port, pfad), data=body, method=method)
    for k, v in (kopf or {}).items():
        anfrage.add_header(k, v)
    if body is not None:
        anfrage.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(anfrage, timeout=10) as antwort:
            return antwort.status, json.loads(antwort.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        roh = e.read().decode("utf-8", errors="replace")
        try:
            return e.code, json.loads(roh)
        except ValueError:
            return e.code, {"_roh": roh[:200]}


def unterschrieben(secret: str, install_id: str, pfad: str, body: bytes,
                   nonce: str) -> dict:
    stamp = str(int(time.time()))
    return {
        "X-Forza-Install": install_id,
        "X-Forza-Timestamp": stamp,
        "X-Forza-Nonce": nonce,
        "X-Forza-Signature": laps.signature(secret, "POST", pfad, stamp, nonce, body),
    }


def admin_kopf(secret: str, method: str, pfad: str, body: bytes) -> dict:
    stamp = str(int(time.time()))
    nachricht = "\n".join([method.upper(), pfad, stamp,
                           hashlib.sha256(body or b"").hexdigest()])
    return {
        "X-Forza-Timestamp": stamp,
        "X-Forza-Signature": hmac.new(secret.encode("utf-8"),
                                      nachricht.encode("utf-8"),
                                      hashlib.sha256).hexdigest(),
    }


def runde() -> dict:
    punkte = 40
    proben = [{"Seconds": 100.0 * i / (punkte - 1), "Metres": 3000.0 * i / (punkte - 1),
               "X": 3000.0 * i / (punkte - 1), "Z": 0.0} for i in range(punkte)]
    # Strecke und Klasse braucht der Abgleich mit der Bestenliste (seit 2026-09-24).
    return {"course": "course_100_200", "lapSeconds": 100.0, "lengthMetres": 3000.0,
            "track": "Test Circuit", "carClass": 4,
            "carOrdinal": 1234, "performanceIndex": 800,
            "recordedAt": "2026-09-13T18:00:00+02:00", "samples": proben}


with tempfile.TemporaryDirectory() as tmp:
    ordner = Path(tmp)
    ADMIN = "admin-geheimnis-fuer-den-test-0123456789"

    # Alle Pfade auf Wegwerfverzeichnisse umbiegen, BEVOR der Server laeuft.
    laps.KEYS_FILE = ordner / "submit_keys.json"
    laps.ANMELDE_VERSUCHE = ordner / "register_attempts.json"
    laps.LAPS_DIR = ordner / "laps"
    api.KEYS_FILE = ordner / "contrib_keys.json"
    api.KEYS_FILE.write_text(json.dumps({"keys": {}, "admin": ADMIN}), encoding="utf-8")
    # Der Schalter. Er steht auf dem Server bewusst auf AUS; dieser Test schaltet
    # ihn fuer sich selbst ein -- und prueft weiter unten ausdruecklich, dass die
    # Schnittstelle ohne ihn nicht da ist.
    api.FEATURES_FILE = ordner / "features.json"
    api.FEATURES_FILE.write_text(json.dumps({"lap_submissions": True}),
                                 encoding="utf-8")
    seite = ordner / "seite.html"
    seite.write_text("<html></html>", encoding="utf-8")
    # Eine Bestenliste, gegen die die Testrunde (100 s) schneller ist (110 s).
    api.DATASET_FILE = ordner / "laps.json"
    api.DATASET_FILE.write_text(json.dumps({
        "flags": [], "tracks": ["Test Circuit"], "classes": ["S1"], "carIds": ["1234"],
        "boards": [{"t": 0, "k": 0, "gcar": [0], "lgrp": [0], "lms": [110000]}]}), encoding="utf-8")
    # Der Spiegelbetrieb verhindert, dass der Server waehrend des Tests die
    # Auswertungsseite baut -- das dauert eine Minute und hat hier nichts zu suchen.
    srv.NUR_AUSLIEFERN = True

    port = freier_port()
    handler = srv.make_handler(ordner / "sweep", seite, ordner / "bauer.py")
    server = srv.Server(("127.0.0.1", port), handler)
    faden = threading.Thread(target=server.serve_forever, daemon=True)
    faden.start()
    print("Server laeuft auf 127.0.0.1:%d" % port)

    try:
        print("\nAnmelden ueber HTTP")
        status, antwort = ruf(port, "POST", "/api/lap/register",
                              json.dumps({"hardware": "b" * 64,
                                          "gamertag": "TestDriver"}).encode("utf-8"))
        pruefe(status == 200, "POST /api/lap/register antwortet 200 (war %d)" % status)
        pruefe(antwort.get("install_id") and antwort.get("secret"),
               "Kennung und Geheimnis kommen zurueck")
        install_id, secret = antwort.get("install_id", ""), antwort.get("secret", "")

        status, antwort = ruf(port, "POST", "/api/lap/register",
                              json.dumps({"hardware": "nein", "gamertag": "X"}).encode())
        pruefe(status == 400, "unbrauchbare Angaben ergeben 400 (war %d)" % status)
        pruefe("error" in antwort, "und eine Begruendung im Rumpf")

        print("\nAnmeldebremse wirkt auch ueber HTTP")
        # Der Server gibt `client_address` als Absender weiter; ueber die Schleife
        # ist das 127.0.0.1 -- also zaehlen alle Aufrufe dieses Tests auf dieselbe
        # Adresse. Die erste Anmeldung oben zaehlt schon mit.
        gebremst = False
        for i in range(laps.ANMELDUNGEN_JE_IP_STUNDE + 2):
            status, _ = ruf(port, "POST", "/api/lap/register",
                            json.dumps({"hardware": "%064x" % (i + 1),
                                        "gamertag": "Tester%d" % i}).encode("utf-8"))
            if status == 429:
                gebremst = True
                break
        pruefe(gebremst, "nach %d Anmeldungen von derselben Adresse: 429"
               % laps.ANMELDUNGEN_JE_IP_STUNDE)
        # Fuer den Rest des Tests wieder freigeben.
        laps.ANMELDE_VERSUCHE.unlink(missing_ok=True)

        print("\nEinreichen ueber HTTP")
        rumpf = json.dumps({"lap": runde()}).encode("utf-8")
        status, antwort = ruf(port, "POST", "/api/lap/submit", rumpf,
                              unterschrieben(secret, install_id, "/api/lap/submit",
                                             rumpf, "nonce-0001"))
        pruefe(status == 200, "eine unterschriebene Runde wird angenommen (war %d: %s)"
               % (status, antwort.get("error", "")))
        lap_id = antwort.get("id", "")
        pruefe(bool(lap_id), "der Server nennt eine Kennung fuer die Runde")

        status, antwort = ruf(port, "POST", "/api/lap/submit", rumpf,
                              unterschrieben("falsch", install_id, "/api/lap/submit",
                                             rumpf, "nonce-0002"))
        pruefe(status == 401, "eine falsche Unterschrift ergibt 401 (war %d)" % status)

        status, _ = ruf(port, "POST", "/api/lap/submit", rumpf,
                        {"X-Forza-Install": install_id})
        pruefe(status == 401, "ohne Zeitstempel und Nonce: 401 (war %d)" % status)

        # Unmoegliche Runde: 3 km in 5 Sekunden.
        schnell = runde()
        schnell["lapSeconds"] = 5.0
        b2 = json.dumps({"lap": schnell}).encode("utf-8")
        status, antwort = ruf(port, "POST", "/api/lap/submit", b2,
                              unterschrieben(secret, install_id, "/api/lap/submit",
                                             b2, "nonce-0003"))
        pruefe(status == 422, "2160 km/h werden abgewiesen (war %d)" % status)

        print("\nAuflisten")
        status, antwort = ruf(port, "GET", "/api/lap/list")
        pruefe(status == 200 and len(antwort.get("laps", [])) == 1,
               "genau eine Runde ist sichtbar")
        roh = json.dumps(antwort)
        pruefe("install_id" not in roh,
               "die Installationskennung wird NICHT oeffentlich ausgegeben")
        pruefe(secret not in roh, "und das Geheimnis erst recht nicht")

        print("\nAdmin: ausblenden")
        koerper = json.dumps({"id": lap_id, "reason": "Probe"}).encode("utf-8")
        status, antwort = ruf(port, "POST", "/api/admin/lap/hide", koerper,
                              admin_kopf(ADMIN, "POST", "/api/admin/lap/hide", koerper))
        pruefe(status == 200, "mit Admin-Unterschrift: 200 (war %d: %s)"
               % (status, antwort.get("error", "")))
        status, antwort = ruf(port, "GET", "/api/lap/list")
        pruefe(len(antwort.get("laps", [])) == 0, "die Runde ist nicht mehr sichtbar")

        status, _ = ruf(port, "POST", "/api/admin/lap/hide", koerper,
                        {"X-Forza-Timestamp": str(int(time.time())),
                         "X-Forza-Signature": "0" * 64})
        pruefe(status == 401, "ohne gueltige Admin-Unterschrift: 401 (war %d)" % status)

        print("\nAdmin: sperren")
        koerper = json.dumps({"install_id": install_id, "reason": "Probe"}).encode("utf-8")
        status, antwort = ruf(port, "POST", "/api/admin/lap/ban", koerper,
                              admin_kopf(ADMIN, "POST", "/api/admin/lap/ban", koerper))
        pruefe(status == 200, "sperren geht (war %d: %s)"
               % (status, antwort.get("error", "")))
        rumpf3 = json.dumps({"lap": runde()}).encode("utf-8")
        status, antwort = ruf(port, "POST", "/api/lap/submit", rumpf3,
                              unterschrieben(secret, install_id, "/api/lap/submit",
                                             rumpf3, "nonce-0004"))
        pruefe(status == 403, "eine gesperrte Installation bekommt 403 (war %d)" % status)
        pruefe("gesperrt" in str(antwort.get("error", "")),
               "und erfaehrt den Grund, statt zu raten")

        print("\nAdmin: die Installationen ansehen")
        status, antwort = ruf(port, "GET", "/api/admin/installs", None,
                              admin_kopf(ADMIN, "GET", "/api/admin/installs", b""))
        pruefe(status == 200, "die Liste kommt (war %d)" % status)
        roh = json.dumps(antwort)
        pruefe(secret not in roh and "secret" not in roh,
               "sie enthaelt kein Geheimnis")
        print("\nOhne den Schalter ist nichts davon da")
        # DIE WICHTIGSTE PRUEFUNG DIESER DATEI.
        #
        # Am 2026-09-13 ging diese Schnittstelle oeffentlich, weil ein Deploy Code
        # und Daten zusammen schickt. Der Schalter ist die Abhilfe -- also muss
        # geprueft sein, dass er WIRKT, und zwar mit demselben laufenden Server.
        api.FEATURES_FILE.write_text(json.dumps({"lap_submissions": False}),
                                     encoding="utf-8")
        for pfad, verfahren, koerper in [
            ("/api/lap/list", "GET", None),
            ("/api/lap/register", "POST", b"{}"),
            ("/api/lap/submit", "POST", b"{}"),
            ("/api/admin/lap/hide", "POST", b"{}"),
            ("/api/admin/installs", "GET", None),
        ]:
            status, _ = ruf(port, verfahren, pfad, koerper)
            pruefe(status == 404, "%s %s ist zu (HTTP %d)" % (verfahren, pfad, status))
        api.FEATURES_FILE.write_text(json.dumps({"lap_submissions": True}),
                                     encoding="utf-8")
        status, _ = ruf(port, "GET", "/api/lap/list")
        pruefe(status == 200, "und mit Schalter sofort wieder da (HTTP %d)" % status)
    finally:
        server.shutdown()
        server.server_close()

print()
if fehler:
    print("%d Pruefung(en) fehlgeschlagen." % fehler)
    raise SystemExit(1)
print("alles bestanden")
