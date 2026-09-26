# -*- coding: utf-8 -*-
"""Pruefen, dass eine eingereichte Bestzeit auf der LIVE-Seite ankommt.

    python scripts/test_submitted_live.py

Der Teil, den test_lap_submit.py nicht abdeckt: nicht ob der Server die Runde
annimmt, sondern ob die AUSGELIEFERTE Seite sie als neue Bestzeit zeigt.

Ohne Browser, aber so nah daran wie moeglich: die Seite wird vom Server geholt,
ihr eigenes Skript herausgeschnitten und in node mit dem LIVE-Datensatz und der
LIVE-Einreichungsliste ausgefuehrt. Geprueft wird damit, was ein Besucher
bekommt -- nicht, was auf der Platte hier liegt.

  1. ein echtes Brett und sein schnellstes Auto aus dem Live-Datensatz
  2. fuer dieses Auto eine Runde einreichen, eine Sekunde schneller als die
     Zeit, die die Seite fuer es waehlt
  3. ein Auto einreichen, das in KEINER Bestenliste steht
  4. die Live-Seite holen und ihr pickCars laufen lassen:
       -> das Auto zeigt die eingereichte Zeit, gekennzeichnet
       -> die alte Rivals-Zeit bleibt daneben sichtbar
       -> das neue Auto steht in der Tabelle
  5. im Verwaltungsbereich ausblenden -> auf der Seite wieder die Rivals-Zeit
"""
from __future__ import annotations

import hashlib
import hmac
import json
import os
import re
import secrets
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(WORKSPACE / "server"))
import local_settings  # noqa: E402

SERVER = local_settings.server()
TEMP = Path(os.environ.get("TEMP", "."))
PI_ORDER = ["D", "C", "B", "A", "S1", "S2", "R"]


def hole(pfad, method="GET", body=None, headers=None):
    req = urllib.request.Request(SERVER + pfad, data=body, method=method,
                                 headers=headers or {})
    with urllib.request.urlopen(req, timeout=120) as r:
        return r.read()


def signiert(secret, method, pfad, body):
    stamp, nonce = str(int(time.time())), secrets.token_urlsafe(12)
    text = "\n".join([method, pfad, stamp, nonce, hashlib.sha256(body).hexdigest()])
    return {"X-Forza-Timestamp": stamp, "X-Forza-Nonce": nonce,
            "X-Forza-Signature": hmac.new(secret.encode(), text.encode(),
                                          hashlib.sha256).hexdigest(),
            "Content-Type": "application/json"}


def admin(secret, pfad, nutzlast):
    body = json.dumps(nutzlast).encode()
    stamp = str(int(time.time()))
    text = "\n".join(["POST", pfad, stamp, hashlib.sha256(body).hexdigest()])
    sig = hmac.new(secret.encode(), text.encode(), hashlib.sha256).hexdigest()
    return hole(pfad, "POST", body, {"X-Forza-Timestamp": stamp,
                                     "X-Forza-Signature": sig,
                                     "Content-Type": "application/json"})


def runde_fuer(sekunden: float, strecke: str, klasse: str, ordinal: int,
               name: str | None = None) -> dict:
    """Eine in sich stimmige Runde: Uhr und Weg laufen vorwaerts, der letzte
    Messpunkt passt zur Rundenzeit -- genau das, was der Server prueft."""
    meter = 4000.0
    n = 200
    proben = [{"Seconds": round(sekunden * i / (n - 1), 3),
               "Metres": round(meter * i / (n - 1), 1),
               "X": round(i * 20.0, 1), "Z": 0.0} for i in range(n)]
    lap = {"lapSeconds": round(sekunden, 3), "lengthMetres": meter,
           "carOrdinal": ordinal, "carClass": PI_ORDER.index(klasse),
           "performanceIndex": 650, "track": strecke, "samples": proben}
    if name:
        lap["carName"] = name
    return lap


def seitenlogik(seite: str) -> Path:
    """Aus der ausgelieferten Seite genau die Funktionen, die rechnen."""
    js = re.search(r"<script>(.*?)</script>", seite, re.S).group(1)
    kopf = js[:js.index("const D = ")]
    m1 = re.search(r"function pickCarsRivals\(board, need, forbid\) \{.*?\n\}\n", js, re.S)
    m2 = re.search(r"let SUBMITTED = null;\nfunction pickCars\(board, need, forbid\) \{.*?\n\}\n",
                   js, re.S)
    if not (m1 and m2):
        raise SystemExit("Die Live-Seite enthaelt die Einreichungs-Logik nicht.")
    ziel = TEMP / "live_page_logic.js"
    ziel.write_text(kopf + "\n" + m1.group(0) + "\n"
                    + m2.group(0).replace("let SUBMITTED", "var SUBMITTED")
                    + "\nmodule.exports={pickCars,buildSubmitted,"
                      "setSub:s=>{SUBMITTED=s;}};\n", encoding="utf-8")
    return ziel


def main() -> int:
    keys = json.loads((WORKSPACE / "config/contrib_keys.json").read_text(encoding="utf-8"))
    admin_geheim = keys["admin"]

    print("Live-Seite holen ...")
    # NICHT "/": das ist eine 398 Byte kleine Weiterleitung. Die Seite selbst liegt
    # unter ihrem Namen. Und sie kommt mit CRLF -- auf Windows im Textmodus
    # geschrieben --, die Suchmuster unten erwarten LF. Beides zusammen liess den
    # ersten Lauf melden, die Logik fehle, obwohl sie ausgeliefert war.
    seite = hole("/rivals_auto_wertung.html").decode("utf-8").replace("\r\n", "\n")
    logik = seitenlogik(seite)
    # DER DATENSATZ KOMMT AUS DER SEITE, nicht von der Platte hier. Die Seite traegt
    # ihn eingebettet als "const D = {...};" -- genau das bekommt ein Besucher.
    # Die eigene laps.json zu nehmen hiesse, gegen etwas zu pruefen, das der
    # Besucher vielleicht gar nicht hat.
    js = re.search(r"<script>(.*?)</script>", seite, re.S).group(1)
    zeile = js[js.index("const D = ") + len("const D = "):].split("\n", 1)[0]
    D = json.loads(zeile.rstrip().rstrip(";"))
    live_d = TEMP / "live_D.json"
    live_d.write_text(json.dumps(D), encoding="utf-8")
    print(f"  Seite: {len(seite) / 1e6:.1f} MB, Datensatz daraus: "
          f"{len(D['boards'])} Bretter, {len(D['carIds'])} Autos\n")

    # 1. Ein echtes Brett und das Auto, das die Seite dort am schnellsten zeigt.
    #    Die Auswahl rechnet node mit der Seitenlogik selbst -- dieselbe Zahl, die
    #    ein Besucher sieht, nicht die rohe schnellste Runde.
    wahl = subprocess.run(["node", "-e", f"""
      const P=require({json.dumps(str(logik))});
      global.D=JSON.parse(require('fs').readFileSync({json.dumps(str(live_d))},'utf8'));
      const b=D.boards.find(x=>D.classes[x.k]==='B'&&x.lms.length>50);
      const p=[...P.pickCars(b,0,0).entries()].sort((a,c)=>a[1].ms-c[1].ms)[0];
      console.log(JSON.stringify({{t:D.tracks[b.t],k:D.classes[b.k],car:p[0],
        ordinal:D.carIds[p[0]],name:D.carNames[p[0]],ms:p[1].ms}}));
    """], capture_output=True, text=True)
    ziel = json.loads(wahl.stdout)
    print(f"Brett {ziel['t']} {ziel['k']}: {ziel['name']} zeigt {ziel['ms']} ms (Rivals)")

    # 2 + 3. Einreichen.
    antwort = json.loads(hole("/api/lap/register", "POST",
        json.dumps({"hardware": secrets.token_hex(32),
                    "gamertag": "LiveTest_" + secrets.token_hex(2)}).encode(),
        {"Content-Type": "application/json"}))
    iid, sec = antwort["install_id"], antwort["secret"]

    def einreichen(lap):
        body = json.dumps({"lap": lap}).encode()
        h = signiert(sec, "POST", "/api/lap/submit", body)
        h["X-Forza-Install"] = iid
        return json.loads(hole("/api/lap/submit", "POST", body, h))["id"]

    schnell = (ziel["ms"] - 1000) / 1000
    id_schnell = einreichen(runde_fuer(schnell, ziel["t"], ziel["k"], ziel["ordinal"]))
    id_neu = einreichen(runde_fuer(schnell + 2, ziel["t"], ziel["k"], 999777,
                                   "Brand New Car '27"))
    print(f"eingereicht: {schnell:.3f} s fuer {ziel['name']}, dazu ein neues Auto\n")

    # 4. Die Live-Seite rechnen lassen -- mit der LIVE-Einreichungsliste.
    def seite_rechnet():
        live = hole("/api/lap/list").decode("utf-8")
        (TEMP / "live_laps.json").write_text(live, encoding="utf-8")
        r = subprocess.run(["node", "-e", f"""
          const P=require({json.dumps(str(logik))});
          global.D=JSON.parse(require('fs').readFileSync({json.dumps(str(live_d))},'utf8'));
          const laps=JSON.parse(require('fs').readFileSync({json.dumps(str(TEMP / 'live_laps.json'))},'utf8')).laps;
          P.setSub(P.buildSubmitted(D,laps));
          const b=D.boards.find(x=>D.tracks[x.t]==={json.dumps(ziel['t'])}&&D.classes[x.k]==={json.dumps(ziel['k'])});
          const picks=P.pickCars(b,0,0);
          const a=picks.get({ziel['car']});
          const ni=D.carIds.indexOf(999777);
          const n=ni>=0?picks.get(ni):null;
          console.log(JSON.stringify({{ms:a.ms,sub:!!a.submitted,rivals:a.rivalsMs||null,
            neu:n?{{ms:n.ms,sub:!!n.submitted,name:D.carNames[ni]}}:null}}));
        """], capture_output=True, text=True)
        if r.returncode:
            raise SystemExit("node: " + r.stderr[:400])
        return json.loads(r.stdout)

    fehler = 0

    def sag(gut, text):
        nonlocal fehler
        print(("  ok   " if gut else "  FEHL ") + text)
        fehler += 0 if gut else 1

    e = seite_rechnet()
    sag(e["ms"] == round(schnell * 1000) and e["sub"],
        f"Live-Seite zeigt {ziel['name']} mit {e['ms']} ms, gekennzeichnet")
    sag(e["rivals"] == ziel["ms"],
        f"die abgeloeste Rivals-Zeit ({e['rivals']} ms) bleibt sichtbar")
    sag(bool(e["neu"]) and e["neu"]["sub"] and e["neu"]["name"] == "Brand New Car '27",
        f"neues Auto in der Tabelle: {e['neu']}")

    # 5. Ausblenden -> die Seite zeigt wieder die Rivals-Zeit.
    admin(admin_geheim, "/api/admin/lap/hide", {"id": id_schnell, "reason": "Live-Test"})
    e = seite_rechnet()
    sag(e["ms"] == ziel["ms"] and not e["sub"],
        f"nach dem Ausblenden wieder die Rivals-Zeit ({e['ms']} ms)")

    admin(admin_geheim, "/api/admin/lap/hide", {"id": id_neu, "reason": "Live-Test"})
    e = seite_rechnet()
    sag(e["neu"] is None, "ausgeblendetes neues Auto ist von der Seite verschwunden")

    print()
    print("Alle Schritte bestanden." if not fehler else f"{fehler} fehlgeschlagen.")
    return 1 if fehler else 0


if __name__ == "__main__":
    sys.exit(main())
