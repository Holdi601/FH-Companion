"""Eingereichte Rennergebnisse ueber ECHTES HTTP: einreichen, Horizon-Play-Zeiten, ausblenden.

    python scripts/test_race_endpoints.py

Ein eigener Server auf 127.0.0.1 mit Wegwerfverzeichnissen -- nie der echte.
Geprueft wird, was der Nutzer am 2026-10-01 festgelegt hat:
  * nur Menschen geben Zeiten (KI und wer verlassen hat nicht),
  * die Zeit eines Autos ist die an der 1-%-Grenze, nie die schnellste einzelne,
  * ausgeblendete Zeiten zaehlen nicht, die naechste rueckt nach,
  * oeffentlich steht weder ein Gamertag noch die schnellste einzelne Zeit,
  * das Beweisbild sieht nur der Verwalter.
"""

from __future__ import annotations

import base64
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

import analytics_api as api          # noqa: E402
import lap_submissions as laps       # noqa: E402
import race_submissions as rennen    # noqa: E402
import serve_analytics as srv        # noqa: E402

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


def ruf(port: int, method: str, pfad: str, body: bytes | None = None, kopf: dict | None = None):
    anfrage = urllib.request.Request("http://127.0.0.1:%d%s" % (port, pfad), data=body, method=method)
    for k, v in (kopf or {}).items():
        anfrage.add_header(k, v)
    if body is not None:
        anfrage.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(anfrage, timeout=10) as a:
            roh = a.read()
            art = a.headers.get("Content-Type", "")
            return a.status, (json.loads(roh.decode("utf-8")) if "json" in art else roh)
    except urllib.error.HTTPError as e:
        roh = e.read().decode("utf-8", errors="replace")
        try:
            return e.code, json.loads(roh)
        except ValueError:
            return e.code, {"_roh": roh[:200]}


def unterschrieben(secret: str, install_id: str, pfad: str, body: bytes, nonce: str) -> dict:
    stamp = str(int(time.time()))
    return {"X-Forza-Install": install_id, "X-Forza-Timestamp": stamp, "X-Forza-Nonce": nonce,
            "X-Forza-Signature": laps.signature(secret, "POST", pfad, stamp, nonce, body)}


def admin_kopf(secret: str, method: str, pfad: str, body: bytes) -> dict:
    stamp = str(int(time.time()))
    nachricht = "\n".join([method.upper(), pfad, stamp, hashlib.sha256(body or b"").hexdigest()])
    return {"X-Forza-Timestamp": stamp,
            "X-Forza-Signature": hmac.new(secret.encode("utf-8"), nachricht.encode("utf-8"),
                                          hashlib.sha256).hexdigest()}


BELEG = base64.b64encode(b"\xff\xd8\xff\xe0" + b"\0" * 200).decode("ascii")


def ein_rennen(nummer: int, zeiten_ms: list[int], rundkurs: bool = True, modus: str = "horizon-play",
               auto: int = 3852, extra: list | None = None) -> dict:
    """Ein Rennen: die ersten Zeilen Menschen mit dem Auto `auto`, dazu eine KI und ein Verlassener."""
    feld = []
    for i, ms in enumerate(zeiten_ms):
        z = {"place": i + 1, "kind": "human", "car": auto, "carShort": "Honda Beat '91", "pi": 600,
             "ms": 150_000 + i * 1000}
        if rundkurs:
            z["bestLapMs"] = ms
        else:
            z["ms"] = ms
        feld.append(z)
    feld[0]["self"] = True
    n = len(feld)
    feld.append({"place": n + 1, "kind": "ai", "car": auto, "pi": 600, "bestLapMs": 1000 if rundkurs else None,
                 "ms": 140_000})
    feld.append({"place": n + 2, "kind": "left", "car": auto, "pi": 600})
    feld[-2] = {k: v for k, v in feld[-2].items() if v is not None}
    feld += extra or []
    return {"id": "race-%d" % nummer, "at": "2026-10-01T22:22:40", "track": "Festival Chase",
            "class": "B", "mode": modus, "laps": 3, "drivers": len(feld), "field": feld}


with tempfile.TemporaryDirectory() as tmp:
    ordner = Path(tmp)
    ADMIN = "admin-geheimnis-fuer-den-test-0123456789"
    laps.KEYS_FILE = ordner / "submit_keys.json"
    laps.ANMELDE_VERSUCHE = ordner / "register_attempts.json"
    laps.LAPS_DIR = ordner / "laps"
    rennen.RACES_DIR = ordner / "races"
    api.KEYS_FILE = ordner / "contrib_keys.json"
    api.KEYS_FILE.write_text(json.dumps({"keys": {}, "admin": ADMIN}), encoding="utf-8")
    api.FEATURES_FILE = ordner / "features.json"
    api.FEATURES_FILE.write_text(json.dumps({"lap_submissions": True}), encoding="utf-8")
    seite = ordner / "seite.html"
    seite.write_text("<html></html>", encoding="utf-8")
    srv.NUR_AUSLIEFERN = True

    port = freier_port()
    server = srv.Server(("127.0.0.1", port), srv.make_handler(ordner / "sweep", seite, ordner / "bauer.py"))
    threading.Thread(target=server.serve_forever, daemon=True).start()
    try:
        status, antwort = ruf(port, "POST", "/api/lap/register",
                              json.dumps({"hardware": "c" * 64, "gamertag": "RaceTester"}).encode("utf-8"))
        pruefe(status == 200, "anmelden (HTTP %d)" % status)
        install_id, secret = antwort.get("install_id", ""), antwort.get("secret", "")
        nonce = iter("nonce-r%04d" % i for i in range(1000))

        def einreichen(r: dict, beleg: str = BELEG):
            b = json.dumps({"race": r, "proof": beleg}).encode("utf-8")
            return ruf(port, "POST", "/api/race/submit", b,
                       unterschrieben(secret, install_id, "/api/race/submit", b, next(nonce)))

        print("\nEinreichen")
        status, antwort = einreichen(ein_rennen(1, [47_000]))
        pruefe(status == 200 and antwort.get("ok"), "ein Rennen mit Beweisbild wird angenommen (%d: %s)"
               % (status, antwort.get("error", "")))
        erste_id = antwort.get("id", "")
        b = json.dumps({"race": ein_rennen(2, [47_000])}).encode("utf-8")
        status, _ = ruf(port, "POST", "/api/race/submit", b)
        pruefe(status == 401, "ohne Unterschrift: 401 (war %d)" % status)
        status, antwort = einreichen(ein_rennen(3, [47_000]), beleg=base64.b64encode(b"GIF89a").decode())
        pruefe(status == 400 and "JPEG" in antwort.get("error", ""), "ein Beleg, der kein JPEG ist: 400")
        status, antwort = einreichen(ein_rennen(4, [47_000]), beleg="")
        pruefe(status == 400, "ohne Beleg: 400 (war %d)" % status)
        kaputt = ein_rennen(5, [47_000])
        kaputt["field"][1]["place"] = 1
        status, _ = einreichen(kaputt)
        pruefe(status == 400, "zwei Fahrer auf demselben Platz: 400 (war %d)" % status)
        status, antwort = einreichen(ein_rennen(1, [47_000]))
        pruefe(status == 200 and antwort.get("id") == erste_id, "dasselbe Rennen nochmal: dieselbe Kennung")

        print("\nHorizon-Play-Zeiten: nur Menschen, die 1-%-Grenze, nie die schnellste")
        status, antwort = ruf(port, "GET", "/api/race/hp")
        bretter = antwort.get("boards", []) if status == 200 else []
        pruefe(len(bretter) == 1 and bretter[0]["ms"] == 47_000 and bretter[0]["confirmed"] is False,
               "eine einzige Zeit gilt, aber unbestaetigt: %s" % bretter)
        pruefe(all("fastest" not in b for b in bretter), "oeffentlich keine schnellste einzelne Zeit")
        pruefe(1000 not in [b["ms"] for b in bretter], "die KI-Zeit (1.000 ms) zaehlt nicht")
        # Ein "Betrueger" mit 30 s, dazu 120 ehrliche Zeiten zwischen 46 und 50 s.
        einreichen(ein_rennen(10, [30_000, 48_000, 49_000]))
        for i in range(12):
            einreichen(ein_rennen(20 + i, [46_000 + (j * 400) + i * 37 for j in range(10)]))
        status, antwort = ruf(port, "GET", "/api/race/hp")
        b = [x for x in antwort.get("boards", []) if x["kind"] == "lap"][0]
        n = b["n"]
        pruefe(n == 1 + 3 + 120, "alle menschlichen Zeiten gezaehlt: %d" % n)
        alle = sorted([47_000, 30_000, 48_000, 49_000] + [46_000 + (j * 400) + i * 37 for i in range(12) for j in range(10)])
        import math
        stelle = max(1, math.ceil(0.01 * len(alle)))
        pruefe(b["ms"] == alle[stelle] and b["ms"] != 30_000 and b["confirmed"],
               "die Zeit an der 1-%%-Grenze (%d ms, Stelle %d), nicht die des Betruegers" % (b["ms"], stelle))

        print("\nAusblenden: die naechste rueckt nach")
        status, liste = ruf(port, "GET", "/api/admin/race/list", None,
                            admin_kopf(ADMIN, "GET", "/api/admin/race/list", b""))
        pruefe(status == 200 and len(liste.get("races", [])) >= 14, "die Verwaltung sieht alle Rennen")
        pruefe(all("install_id" not in r for r in liste.get("races", [])), "ohne install_id")
        pruefe(all("gamertag" not in json.dumps(r).lower() for r in liste.get("races", [])), "ohne Gamertags")
        vorher = b["ms"]
        # Die zwei schnellsten ehrlichen Zeiten ausblenden (die Stelle selbst und eine davor).
        ziele = sorted(((z["bestLapMs"], r["id"], z["place"]) for r in liste["races"] for z in r["field"]
                        if z.get("kind") == "human" and z.get("bestLapMs")))[:3]
        for _, rid, platz in ziele:
            koerper = json.dumps({"id": rid, "place": platz, "reason": "known cheat"}).encode("utf-8")
            status, _ = ruf(port, "POST", "/api/admin/race/hide", koerper,
                            admin_kopf(ADMIN, "POST", "/api/admin/race/hide", koerper))
        pruefe(status == 200, "ausblenden (HTTP %d)" % status)
        status, antwort = ruf(port, "GET", "/api/race/hp")
        nachher = [x for x in antwort.get("boards", []) if x["kind"] == "lap"][0]["ms"]
        pruefe(nachher > vorher, "nach dem Ausblenden rueckt eine langsamere Zeit nach (%d -> %d)" % (vorher, nachher))
        koerper = json.dumps({"id": ziele[0][1], "place": ziele[0][2]}).encode("utf-8")
        status, _ = ruf(port, "POST", "/api/admin/race/show", koerper,
                        admin_kopf(ADMIN, "POST", "/api/admin/race/show", koerper))
        pruefe(status == 200, "wieder einblenden geht")

        print("\nDas Beweisbild nur fuer den Verwalter")
        koerper = json.dumps({"id": erste_id}).encode("utf-8")
        status, bild = ruf(port, "POST", "/api/admin/race/proof", koerper,
                           admin_kopf(ADMIN, "POST", "/api/admin/race/proof", koerper))
        pruefe(status == 200 and isinstance(bild, bytes) and bild.startswith(b"\xff\xd8\xff"),
               "mit Admin-Unterschrift kommt das JPEG")
        status, _ = ruf(port, "POST", "/api/admin/race/proof", koerper)
        pruefe(status == 401, "ohne: 401 (war %d)" % status)

        print("\nNur Horizon Play, Sprints mit der Zeit im Ziel")
        einreichen(ein_rennen(90, [151_000], rundkurs=False, modus="solo", auto=777))
        einreichen(ein_rennen(91, [152_000], rundkurs=False, modus="horizon-play", auto=778))
        status, antwort = ruf(port, "GET", "/api/race/hp")
        autos = {(x["car"], x["kind"]) for x in antwort.get("boards", [])}
        pruefe((777, "race") not in autos, "ein Solo-Rennen gibt keine Horizon-Play-Zeit")
        pruefe((778, "race") in autos, "ein Sprint gibt die Zeit im Ziel")

        print("\nAusgeschaltet: nicht da")
        api.FEATURES_FILE.write_text(json.dumps({"lap_submissions": False}), encoding="utf-8")
        status, _ = ruf(port, "GET", "/api/race/hp")
        pruefe(status == 404, "GET /api/race/hp ist zu (HTTP %d)" % status)
    finally:
        server.shutdown()
        server.server_close()

print()
if fehler:
    print("%d Pruefung(en) fehlgeschlagen." % fehler)
    raise SystemExit(1)
print("alles bestanden")
