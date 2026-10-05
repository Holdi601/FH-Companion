# -*- coding: utf-8 -*-
"""Den ganzen Weg einer eingereichten Rundenzeit pruefen -- mit echten Daten.

    python scripts/test_lap_submit.py                 gegen den Live-Server
    python scripts/test_lap_submit.py --server http://127.0.0.1:8787

## Warum es diesen Test gibt

`LapSubmit` in der App hat bis heute KEINEN Aufrufer: die App laedt nur herunter.
Die Serverseite ist damit nie im Zusammenhang gelaufen -- jeder einzelne Baustein
sieht richtig aus, und genau so sahen auch die Bausteine des Beitrags-Werkzeugs
aus, bevor neun Laeufe noetig waren, um es zum Laufen zu bringen.

Geprueft wird deshalb der VOLLE Weg, nicht die Teile:

  1. anmelden                     POST /api/lap/register
  2. eine ECHTE Runde einreichen  POST /api/lap/submit
  3. sie wiederfinden             GET  /api/lap/list
  4. eine SCHNELLERE nachreichen  -> sie muss die alte als Bestzeit ablösen
  5. ein Auto, das in keiner Bestenliste steht -> muss trotzdem ankommen
  6. Unmoegliches einreichen      -> muss abgelehnt werden
  7. als Verwalter ausblenden     POST /api/admin/lap/hide  -> weg aus der Liste
  8. wieder einblenden            POST /api/admin/lap/show  -> wieder da
  9. suchen und filtern           ueber die Verwaltungsliste

## Warum eine echte Runde und keine erfundene

Die Pruefung auf der Serverseite sieht sich die Messpunkte an: Uhr und Weg muessen
vorwaerts laufen, der Abstand zwischen zwei Punkten muss fahrbar sein, und der
letzte Punkt muss zur behaupteten Rundenzeit passen. Eine erfundene Punktfolge
besteht das entweder zufaellig oder gar nicht -- in beiden Faellen prueft sie den
Server nicht, sondern meine Phantasie. Genommen wird darum eine aufgezeichnete
Runde aus dem eigenen Bestand, und fuer den Bestzeit-Fall wird sie SAUBER
gestaucht: alle Zeiten mal demselben Faktor, damit sie in sich stimmig bleibt.
"""
from __future__ import annotations

import argparse
import hashlib
import hmac
import json
import os
import secrets
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(WORKSPACE / "server"))
import local_settings  # noqa: E402

LAPS = local_settings.app_data() / "laps"
DEFAULT_SERVER = local_settings.server()

OK = "  ok   "
FAIL = "  FEHL "


class Fehler(Exception):
    pass


def hole(server: str, pfad: str, method: str = "GET", body: bytes | None = None,
         headers: dict | None = None, timeout: int = 60):
    req = urllib.request.Request(server + pfad, data=body, method=method,
                                 headers=headers or {})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            roh = r.read()
            return r.status, json.loads(roh) if roh else {}
    except urllib.error.HTTPError as e:
        roh = e.read()
        try:
            return e.code, json.loads(roh)
        except Exception:
            return e.code, {"raw": roh.decode("utf-8", "replace")[:300]}


# --------------------------------------------------------------- Unterschrift

def signiere(secret: str, method: str, pfad: str, body: bytes) -> dict:
    stamp = str(int(time.time()))
    nonce = secrets.token_urlsafe(12)
    rumpf = hashlib.sha256(body or b"").hexdigest()
    text = "\n".join([method.upper(), pfad, stamp, nonce, rumpf])
    sig = hmac.new(secret.encode(), text.encode(), hashlib.sha256).hexdigest()
    return {"X-Forza-Timestamp": stamp, "X-Forza-Nonce": nonce,
            "X-Forza-Signature": sig, "Content-Type": "application/json"}


def admin_kopf(secret: str, method: str, pfad: str, body: bytes) -> dict:
    stamp = str(int(time.time()))
    text = "\n".join([method.upper(), pfad, stamp,
                      hashlib.sha256(body or b"").hexdigest()])
    sig = hmac.new(secret.encode(), text.encode(), hashlib.sha256).hexdigest()
    return {"X-Forza-Timestamp": stamp, "X-Forza-Signature": sig,
            "Content-Type": "application/json"}


# --------------------------------------------------------------- echte Runde

def echte_runde() -> tuple[dict, str]:
    """Eine aufgezeichnete Runde mit Messpunkten aus dem eigenen Bestand."""
    if not LAPS.exists():
        raise Fehler(f"Kein Rundenbestand unter {LAPS}")
    beste = None
    for datei in LAPS.rglob("*.json"):
        if datei.name == "course.json":
            continue
        try:
            d = json.loads(datei.read_text(encoding="utf-8"))
        except Exception:
            continue
        lap = d.get("Lap") if isinstance(d.get("Lap"), dict) else d
        if not isinstance(lap, dict):
            continue
        proben = lap.get("samples") or []
        if len(proben) < 30:
            continue
        if not lap.get("lapSeconds") or not lap.get("lengthMetres"):
            continue
        if beste is None or len(proben) > len(beste[0].get("samples") or []):
            beste = (lap, str(datei.relative_to(LAPS)))
    if beste is None:
        raise Fehler("Keine Runde mit genug Messpunkten gefunden.")
    return beste


def stauche(lap: dict, faktor: float) -> dict:
    """Die Runde um `faktor` schneller machen -- IN SICH STIMMIG.

    Alle Zeiten mit demselben Faktor, die Rundenzeit ebenso. Der Weg bleibt, wie
    er war: dieselbe Strecke, nur schneller gefahren. Nur die Rundenzeit zu
    aendern waere genau die Faelschung, die der Server abfangen soll -- und dann
    pruefte dieser Test, ob die Abwehr greift, statt ob der gute Fall geht.
    """
    neu = json.loads(json.dumps(lap))
    neu["lapSeconds"] = round(float(lap["lapSeconds"]) * faktor, 3)
    for p in neu.get("samples") or []:
        for schluessel in ("Seconds", "seconds"):
            if schluessel in p and isinstance(p[schluessel], (int, float)):
                p[schluessel] = round(p[schluessel] * faktor, 4)
    return neu


# --------------------------------------------------------------- der Ablauf

def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--server", default=DEFAULT_SERVER)
    p.add_argument("--keys", type=Path,
                   default=WORKSPACE / "config" / "contrib_keys.json")
    args = p.parse_args(argv)
    server = args.server.rstrip("/")

    fehler = 0

    def sag(gut: bool, text: str):
        nonlocal fehler
        print((OK if gut else FAIL) + text)
        if not gut:
            fehler += 1

    print(f"Server: {server}\n")

    # --- 0. Verwaltergeheimnis
    admin = ""
    try:
        admin = json.loads(args.keys.read_text(encoding="utf-8")).get("admin", "")
    except Exception:
        pass
    if not admin:
        print("  (kein Admin-Geheimnis -- die Verwaltungsschritte entfallen)")

    # --- 1. Anmelden
    hardware = hashlib.sha256(b"forza-selbsttest-" + secrets.token_bytes(8)).hexdigest()
    gamertag = "TestFahrer_" + secrets.token_hex(3)
    code, antwort = hole(server, "/api/lap/register", "POST",
                         json.dumps({"hardware": hardware,
                                     "gamertag": gamertag}).encode(),
                         {"Content-Type": "application/json"})
    sag(code == 200 and antwort.get("secret"),
        f"anmelden -> {code} {'als ' + gamertag if code == 200 else antwort}")
    if code != 200:
        return 1
    install_id = antwort["install_id"]
    secret = antwort["secret"]

    # --- 2. Eine echte Runde einreichen
    lap, herkunft = echte_runde()
    print(f"       Vorlage: {herkunft}  "
          f"({lap['lapSeconds']:.3f} s, {len(lap.get('samples') or [])} Messpunkte)")

    def einreichen(runde: dict):
        body = json.dumps({"lap": runde}).encode()
        kopf = signiere(secret, "POST", "/api/lap/submit", body)
        kopf["X-Forza-Install"] = install_id
        return hole(server, "/api/lap/submit", "POST", body, kopf)

    code, antwort = einreichen(lap)
    sag(code == 200, f"echte Runde einreichen -> {code} "
                     f"{antwort.get('id', antwort)[:24] if code == 200 else antwort}")
    erste_id = antwort.get("id", "")

    # --- 3. Wiederfinden
    code, antwort = hole(server, "/api/lap/list")
    liste = antwort.get("laps", [])
    sag(code == 200 and any(r.get("id") == erste_id for r in liste),
        f"in /api/lap/list wiederfinden -> {len(liste)} Runde(n)")

    # --- 4. Eine schnellere nachreichen: sie muss die Bestzeit werden
    schneller = stauche(lap, 0.92)
    code, antwort = einreichen(schneller)
    zweite_id = antwort.get("id", "")
    sag(code == 200, f"schnellere Runde ({schneller['lapSeconds']:.3f} s) "
                     f"einreichen -> {code}")

    code, antwort = hole(server, "/api/lap/list")
    liste = antwort.get("laps", [])
    # Die Runde steckt GESCHACHTELT unter "lap" -- der Datensatz traegt aussen
    # nur Kennung, Spieler, Eingang und Sichtbarkeit. Vorher stand hier
    # r.get("lapSeconds") und lieferte still 9e9, also "kein Unterschied"; der
    # Server war in Ordnung, die Erwartung nicht.
    def sekunden(r: dict) -> float:
        return float((r.get("lap") or {}).get("lapSeconds", 9e9))

    meine = [r for r in liste if r.get("id") in (erste_id, zweite_id)]
    zeiten = sorted(sekunden(r) for r in meine)
    sag(len(meine) == 2 and zeiten[0] < zeiten[1],
        f"beide Runden da, schnellste zuerst -> {zeiten}")

    # --- 5. Ein Auto, das in keiner Bestenliste steht
    exot = json.loads(json.dumps(lap))
    exot["carOrdinal"] = 999001            # gibt es im Datensatz nicht
    exot["lapSeconds"] = round(float(lap["lapSeconds"]) * 0.97, 3)
    for pr in exot.get("samples") or []:
        for s in ("Seconds", "seconds"):
            if s in pr and isinstance(pr[s], (int, float)):
                pr[s] = round(pr[s] * 0.97, 4)
    code, antwort = einreichen(exot)
    exot_id = antwort.get("id", "")
    sag(code == 200, f"Auto ohne Bestenliste (Kennung 999001) -> {code}")

    # --- 6. Unmoegliches muss abprallen
    unfug = json.loads(json.dumps(lap))
    unfug["lapSeconds"] = 3.0              # Rundenzeit passt nicht zur Telemetrie
    code, antwort = einreichen(unfug)
    sag(code >= 400, f"unmoegliche Zeit (3 s) wird abgelehnt -> {code} "
                     f"{str(antwort.get('error', antwort))[:60]}")

    # --- 7..9 Verwaltung
    if admin:
        def admin_ruf(pfad: str, nutzlast: dict):
            body = json.dumps(nutzlast).encode()
            return hole(server, pfad, "POST", body,
                        admin_kopf(admin, "POST", pfad, body))

        code, antwort = admin_ruf("/api/admin/lap/hide",
                                  {"id": erste_id, "reason": "Testlauf"})
        sag(code == 200, f"als Verwalter ausblenden -> {code}")

        code, antwort = hole(server, "/api/lap/list")
        sichtbar = [r for r in antwort.get("laps", []) if r.get("id") == erste_id]
        sag(not sichtbar, "ausgeblendete Runde ist aus der oeffentlichen Liste weg")

        code, antwort = admin_ruf("/api/admin/lap/show", {"id": erste_id})
        sag(code == 200, f"wieder einblenden -> {code}")
        code, antwort = hole(server, "/api/lap/list")
        wieder = [r for r in antwort.get("laps", []) if r.get("id") == erste_id]
        sag(bool(wieder), "wieder eingeblendete Runde ist zurueck")

        # Suchen und filtern geschieht auf der Verwaltungsliste. Geprueft wird,
        # dass die Felder da sind, nach denen die Seite filtert -- ohne sie kann
        # die Oberflaeche nichts finden, egal wie gut sie aussieht.
        code, antwort = hole(server, "/api/lap/list")
        felder = set()
        for r in antwort.get("laps", []):
            felder |= set(r.keys())
            felder |= {"lap." + k for k in (r.get("lap") or {})}
        noetig = {"id", "gamertag", "received", "lap.lapSeconds", "lap.carOrdinal"}
        fehlt = noetig - felder
        sag(not fehlt, f"Filterfelder vorhanden ({', '.join(sorted(noetig))})"
                       + (f" -- FEHLT: {fehlt}" if fehlt else ""))

        # Nach Spieler filtern: unsere drei Runden muessen unter unserem Namen
        # auffindbar sein.
        meine = [r for r in antwort.get("laps", [])
                 if r.get("gamertag") == gamertag]
        sag(len(meine) >= 2,
            f"nach Spieler '{gamertag}' filtern -> {len(meine)} Runde(n)")

        # Aufraeumen: was dieser Test angelegt hat, verschwindet wieder.
        for kennung in (erste_id, zweite_id, exot_id):
            if kennung:
                admin_ruf("/api/admin/lap/hide",
                          {"id": kennung, "reason": "Testlauf aufgeraeumt"})
        print("       Testrunden wieder ausgeblendet.")

    print()
    if fehler:
        print(f"{fehler} Schritt(e) fehlgeschlagen.")
        return 1
    print("Alle Schritte bestanden.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
