"""Die volle Telemetrie einer eingereichten Runde: pruefen, ablegen, wieder herausgeben.

    python scripts/test_full_telemetry.py

Seit 2026-09-28 schickt die App mit jeder Runde ihre ganze .tele.gz mit (jedes Paket,
alle Felder). Der Server nimmt sie nur an, wenn sie zu DIESER Runde passt, entpackt
nie ueber eine Grenze hinaus und legt sie neben die Rundendatei -- die Listen sollen
davon nichts merken.
"""
from __future__ import annotations

import base64
import gzip
import json
import os
import sys
import tempfile
import time
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(WORKSPACE / "server"))

import lap_submissions as laps  # noqa: E402

fehler = 0


def pruefe(bedingung, text: str) -> None:
    global fehler
    print(("  ok    " if bedingung else "  FEHLT ") + text)
    if not bedingung:
        fehler += 1


def spur(sekunden: float, zeilen: int = 40, ende: float | None = None) -> dict:
    ende = sekunden if ende is None else ende
    daten = [[ende * i / (zeilen - 1), 75.0 * i, 1000.0 + 16 * i, 1.5, 0.0] for i in range(zeilen)]
    return {"version": 1, "rows": zeilen, "columns": ["t", "metres", "TimestampMS", "Speed", "Brake"],
            "data": daten}


def verpackt(inhalt: dict | bytes) -> dict:
    roh = inhalt if isinstance(inhalt, bytes) else json.dumps(inhalt).encode("utf-8")
    return {"encoding": "gzip+base64", "format": "fhc-tele-1",
            "data": base64.b64encode(gzip.compress(roh)).decode("ascii")}


def status_von(f) -> int:
    try:
        f()
        return 200
    except laps.SubmitError as e:
        return e.status


runde = {"lapSeconds": 100.0, "course": "course_1_2", "carOrdinal": 1234,
         "recordedAt": "2026-09-28T12:00:00+02:00"}

print("Pruefen")
gepackt, info, flags = laps.pruefe_telemetrie(None, runde)
pruefe(gepackt is None and flags == ["no full telemetry"],
       "ohne Telemetrie: angenommen, aber als 'no full telemetry' markiert")
gepackt, info, flags = laps.pruefe_telemetrie(verpackt(spur(100.0)), runde)
pruefe(gepackt is not None and flags == [] and info["rows"] == 40 and info["columns"] == 5,
       "eine passende Spur wird angenommen (%s)" % info)
pruefe(status_von(lambda: laps.pruefe_telemetrie({"encoding": "gzip+base64", "data": "%%%"}, runde)) == 400,
       "kein Base64: 400")
pruefe(status_von(lambda: laps.pruefe_telemetrie({"encoding": "zip", "data": ""}, runde)) == 400,
       "falsche Kodierung: 400")
pruefe(status_von(lambda: laps.pruefe_telemetrie(verpackt(b"kein json"), runde)) == 400,
       "gepackt, aber kein JSON: 400")
pruefe(status_von(lambda: laps.pruefe_telemetrie(verpackt(spur(100.0, ende=50.0)), runde)) == 422,
       "endet bei 50 s unter einer 100-s-Runde: 422")
rueckwaerts = spur(100.0)
rueckwaerts["data"][10][0] = 1.0
pruefe(status_von(lambda: laps.pruefe_telemetrie(verpackt(rueckwaerts), runde)) == 422,
       "die Uhr laeuft rueckwaerts: 422")
schief = spur(100.0)
schief["data"][5] = schief["data"][5][:3]
pruefe(status_von(lambda: laps.pruefe_telemetrie(verpackt(schief), runde)) == 400,
       "eine Zeile mit zu wenig Werten: 400")
kaputt = spur(100.0)
kaputt["data"][7][3] = "schnell"
pruefe(status_von(lambda: laps.pruefe_telemetrie(verpackt(kaputt), runde)) == 400,
       "ein Wert, der keine Zahl ist: 400")
pruefe(status_von(lambda: laps.pruefe_telemetrie(verpackt(spur(100.0, zeilen=5)), runde)) == 422,
       "fuenf Zeilen sind keine Runde: 422")

# Die Entpack-Grenze: eine kleine Datei, die riesig aufgeht. Grenze fuer den Test
# herabgesetzt, damit er keinen Speicher frisst.
alt = laps.MAX_TELE_ENTPACKT
laps.MAX_TELE_ENTPACKT = 1024 * 1024
try:
    bombe = {"encoding": "gzip+base64",
             "data": base64.b64encode(gzip.compress(b" " * (3 * 1024 * 1024))).decode("ascii")}
    pruefe(len(bombe["data"]) < 20_000, "die Bombe ist klein (%d Zeichen)" % len(bombe["data"]))
    pruefe(status_von(lambda: laps.pruefe_telemetrie(bombe, runde)) == 413,
           "sie wird beim Entpacken gestoppt: 413")
finally:
    laps.MAX_TELE_ENTPACKT = alt

# Eine echte Runde aus dem Archiv, falls es eines gibt: so wie die App sie schickt.
archiv = Path(os.environ.get("LOCALAPPDATA", "")) / "FHCompanion" / "laps"
echte = sorted(archiv.rglob("*.tele.gz"), key=lambda p: p.stat().st_mtime)[-1:] if archiv.exists() else []
for datei in echte:
    roh = datei.read_bytes()
    kopf = json.loads(gzip.decompress(roh))
    letzte = kopf["data"][-1][kopf["columns"].index("t")]
    beginn = time.perf_counter()
    gepackt, info, flags = laps.pruefe_telemetrie(
        {"encoding": "gzip+base64", "data": base64.b64encode(roh).decode("ascii")},
        {"lapSeconds": letzte})
    dauer = time.perf_counter() - beginn
    pruefe(gepackt == roh and info["rows"] == kopf["rows"],
           "eine echte Runde (%d Pakete x %d Felder, %d KB) wird unveraendert angenommen, %.2f s"
           % (info["rows"], info["columns"], len(roh) // 1024, dauer))

print("\nAblegen und wieder herausgeben")
with tempfile.TemporaryDirectory() as tmp:
    ordner = Path(tmp)
    laps.KEYS_FILE = ordner / "submit_keys.json"
    gepackt, info, _ = laps.pruefe_telemetrie(verpackt(spur(100.0)), runde)
    d = laps.store("install-x", {"gamertag": "Tester"}, dict(runde), [], root=ordner,
                   keys_path=ordner / "submit_keys.json", volle_spur=gepackt, spur_info=info)
    pruefe((ordner / (d["id"] + ".tele.gz")).read_bytes() == gepackt,
           "die Spur liegt unveraendert neben der Rundendatei")
    liste = laps.list_laps(root=ordner)
    pruefe(len(liste) == 1 and liste[0]["fullTelemetry"]["rows"] == 40,
           "die Liste kennt die Runde einmal, mit Kurzinfo zur Spur")
    voll = laps.get_full_telemetry(d["id"], root=ordner)
    pruefe(voll["columns"][0] == "t" and len(voll["data"]) == 40, "die Verwaltung bekommt jede Zeile")
    pruefe(status_von(lambda: laps.get_full_telemetry("../../config/contrib_keys", root=ordner)) == 400,
           "eine Kennung, die kein Dateiname von uns ist: 400")
    ohne = laps.store("install-x", {"gamertag": "Tester"}, dict(runde, lapSeconds=99.0), [],
                      root=ordner, keys_path=ordner / "submit_keys.json")
    pruefe(status_von(lambda: laps.get_full_telemetry(ohne["id"], root=ordner)) == 404,
           "eine Runde ohne Spur: 404")

print("\nJe Auto, Strecke und Klasse zehn -- je Mensch eine")
with tempfile.TemporaryDirectory() as tmp:
    ordner = Path(tmp) / "laps"
    ordner.mkdir()
    schluessel = Path(tmp) / "submit_keys.json"
    # Elf Menschen; "betrueger" hat drei Installationen auf DERSELBEN Hardware.
    installs = {("i%02d" % n): {"hw": "hw-%02d" % n, "banned": False, "laps": 0} for n in range(11)}
    for n in range(3):
        installs["b%d" % n] = {"hw": "hw-betrueger", "banned": False, "laps": 0}
    laps.save_keys({"installs": installs}, schluessel)

    def lege(install: str, sekunden: float, wagen: int = 1234, versteckt: bool = False) -> str:
        r = dict(runde, lapSeconds=sekunden, carOrdinal=wagen, carClass=3, track="Test Circuit",
                 recordedAt="2026-09-28T12:%02d:%06.3f+02:00" % (int(sekunden) % 60, sekunden))
        g, i, _ = laps.pruefe_telemetrie(verpackt(spur(sekunden)), r)
        d = laps.store(install, {"gamertag": install}, r, [], root=ordner, keys_path=schluessel,
                       volle_spur=g, spur_info=i)
        if versteckt:
            laps.set_hidden(d["id"], True, "Probe", root=ordner)
        return d["id"]

    # Der Betrueger: sechs absurd schnelle Runden ueber drei Installationen.
    betrug = [lege("b%d" % (k % 3), 30.0 + k * 0.1) for k in range(6)]
    ehrlich = [lege("i%02d" % n, 60.0 + n) for n in range(11)]
    versteckt = lege("i00", 59.0, versteckt=True)
    fremd = lege("i01", 65.0, wagen=999)
    gruppe = laps.gruppe_von(dict(track="Test Circuit", carClass=3, carOrdinal=1234))
    weg = laps.nur_die_besten(gruppe, root=ordner, keys_path=schluessel)
    uebrig = [d["id"] for d in laps.list_laps(root=ordner, include_hidden=True)]
    sichtbar = [d for d in laps.list_laps(root=ordner) if laps.gruppe_von(d["lap"]) == gruppe]
    pruefe(len(sichtbar) == 10, "zehn Runden bleiben in der Gruppe (%d)" % len(sichtbar))
    pruefe(betrug[0] in uebrig and not any(b in uebrig for b in betrug[1:]),
           "vom Betrueger bleibt genau eine -- seine schnellste --, ueber alle drei Installationen")
    pruefe(all(e in uebrig for e in ehrlich[:9]) and not any(e in uebrig for e in ehrlich[9:]),
           "neun ehrliche Runden bleiben als Rueckfall, die zwei langsamsten gehen")
    pruefe(versteckt in uebrig, "eine ausgeblendete Runde zaehlt nicht mit und bleibt als Beleg")
    pruefe(fremd in uebrig, "ein anderes Auto bleibt unberuehrt")
    pruefe(not any((ordner / (k + ".tele.gz")).exists() for k in weg),
           "mit einer Runde geht auch ihre volle Telemetrie")
    # Wird der Betrueger ausgeblendet, rueckt niemand nach -- aber neun sind noch da.
    laps.set_hidden(betrug[0], True, "Betrug", root=ordner)
    sichtbar = [d for d in laps.list_laps(root=ordner) if laps.gruppe_von(d["lap"]) == gruppe]
    pruefe(len(sichtbar) == 9 and float(sichtbar[0]["lap"]["lapSeconds"]) >= 60.0,
           "nach dem Ausblenden des Betruegers stehen neun ehrliche Runden da")

print()
if fehler:
    print("%d Pruefung(en) fehlgeschlagen." % fehler)
    sys.exit(1)
print("alles bestanden")
