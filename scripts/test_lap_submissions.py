"""Eingereichte Runden: Anmelden, Unterschreiben, Pruefen, Sperren.

    python scripts/test_lap_submissions.py

Laeuft vollstaendig in einem Wegwerfverzeichnis -- keine echte Schluesseldatei, kein
echter Rundenbestand, kein Netzwerk.
"""

from __future__ import annotations

import json
import sys
import tempfile
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "server"))

import lap_submissions as laps  # noqa: E402

fehler = 0


def pruefe(bedingung, text: str) -> None:
    global fehler
    if bedingung:
        print("  ok    " + text)
    else:
        fehler += 1
        print("  FEHL  " + text)


def wirft(status: int, fn, text: str) -> None:
    global fehler
    try:
        fn()
    except laps.SubmitError as e:
        if e.status == status:
            print("  ok    %s (%d: %s)" % (text, e.status, e.message[:60]))
            return
        fehler += 1
        print("  FEHL  %s -- Status %d statt %d: %s" % (text, e.status, status, e.message))
        return
    fehler += 1
    print("  FEHL  %s -- es wurde gar nichts geworfen" % text)


class Kopf(dict):
    """Ein Ersatz fuer HTTP-Kopfzeilen: unabhaengig von Gross-/Kleinschreibung."""

    def get(self, name, vorgabe=None):
        for k, v in self.items():
            if k.lower() == name.lower():
                return v
        return vorgabe


def runde(sekunden: float = 100.0, meter: float = 3000.0, punkte: int = 40,
          sprung: float = 0.0, zeitknick: bool = False) -> dict:
    proben = []
    for i in range(punkte):
        t = sekunden * i / (punkte - 1)
        m = meter * i / (punkte - 1)
        x = m
        z = 0.0
        if sprung and i == punkte // 2:
            x = m + sprung
        if zeitknick and i == punkte // 2:
            t = 0.0
        proben.append({"Seconds": t, "Metres": m, "X": x, "Z": z})
    return {"course": "course_100_200", "lapSeconds": sekunden,
            "lengthMetres": meter, "carOrdinal": 1234, "performanceIndex": 800,
            "recordedAt": "2026-09-13T18:00:00+02:00", "samples": proben}


def unterschreibe(kopf: dict, secret: str, install_id: str, body: bytes,
                  pfad: str = "/api/lap/submit", nonce: str = "nonce-0001",
                  jetzt: float | None = None) -> Kopf:
    stamp = str(int(jetzt or time.time()))
    h = Kopf({
        "X-Forza-Install": install_id,
        "X-Forza-Timestamp": stamp,
        "X-Forza-Nonce": nonce,
    })
    h["X-Forza-Signature"] = laps.signature(secret, "POST", pfad, stamp, nonce, body)
    h.update(kopf)
    return h


with tempfile.TemporaryDirectory() as tmp:
    schluessel = Path(tmp) / "submit_keys.json"
    bestand = Path(tmp) / "laps"
    HW = "a" * 64

    print("Anmelden")
    konto = laps.register(HW, "TestDriver", path=schluessel)
    pruefe(konto["install_id"] and konto["secret"], "Kennung und Geheimnis kommen zurueck")
    pruefe(laps.INSTALL_RE.match(konto["install_id"]), "die Kennung hat die erwartete Form")

    inhalt = json.loads(schluessel.read_text(encoding="utf-8"))
    pruefe(inhalt["pepper"], "ein Pfeffer wurde angelegt")
    abgelegt = inhalt["installs"][konto["install_id"]]
    pruefe(abgelegt["hw"] != HW, "der Hardware-Hash liegt GEPFEFFERT da, nicht roh")
    pruefe(len(abgelegt["hw"]) == 64, "und er ist ein SHA-256")
    pruefe(abgelegt["gamertag"] == "TestDriver", "der Gamertag steht im Klartext da")

    print("\nAnmelden weist Unsinn ab")
    wirft(400, lambda: laps.register("kein-hash", "TestDriver", path=schluessel),
          "kein Hex-Hash")
    wirft(400, lambda: laps.register(HW, "", path=schluessel), "kein Gamertag")
    wirft(400, lambda: laps.register(HW, "boese<script>", path=schluessel),
          "Gamertag mit Sonderzeichen")

    print("\nEine Maschine bekommt nicht beliebig viele Kennungen")
    for i in range(laps.MAX_INSTALLS_JE_MASCHINE - 1):
        laps.register(HW, "Zweitkonto%d" % i, path=schluessel)
    wirft(429, lambda: laps.register(HW, "Nochwas", path=schluessel),
          "ab der %d. Kennung ist Schluss" % (laps.MAX_INSTALLS_JE_MASCHINE + 1))

    print("\nAnmeldebremse je Absenderadresse")
    # Die Grenze am Hardware-Hash ist schwach -- den schickt der Client. Diese hier
    # haengt an der IP, und die behauptet niemand.
    versuche = Path(tmp) / "register_attempts.json"
    jetzt = 1_757_800_000.0
    for i in range(laps.ANMELDUNGEN_JE_IP_STUNDE):
        laps.anmeldebremse("203.0.113.7", now=jetzt + i, path=versuche)
    wirft(429, lambda: laps.anmeldebremse("203.0.113.7", now=jetzt + 10,
                                          path=versuche),
          "%d Anmeldungen in einer Stunde reichen" % laps.ANMELDUNGEN_JE_IP_STUNDE)

    # Eine ANDERE Adresse ist davon unberuehrt -- sonst sperrte ein Einzelner alle.
    laps.anmeldebremse("198.51.100.4", now=jetzt + 10, path=versuche)
    pruefe(True, "eine andere Adresse darf weiterhin")

    # Eine Stunde spaeter geht es wieder, der Tag bremst aber weiter.
    laps.anmeldebremse("203.0.113.7", now=jetzt + 3700, path=versuche)
    pruefe(True, "nach einer Stunde ist wieder Platz")

    # Ohne bekannten Absender (etwa im Test oder hinter einem Proxy ohne Angabe)
    # bremst nichts -- sonst waere der Dienst fuer alle zu.
    laps.anmeldebremse("", now=jetzt, path=versuche)
    pruefe(True, "ohne Absenderangabe wird nicht gebremst")

    print("\nregister() fragt die Bremse ZUERST")
    voll = Path(tmp) / "voll.json"
    for i in range(laps.ANMELDUNGEN_JE_IP_STUNDE):
        laps.anmeldebremse("203.0.113.9", now=jetzt + i, path=voll)
    # Absichtlich mit UNGUELTIGEN Angaben: kaeme zuerst die Eingabepruefung, gaebe
    # es 400 statt 429 -- und ein Anklopfender bekaeme eine Auskunft darueber, was
    # der Server erwartet, obwohl er gar nicht mehr drankommen sollte.
    wirft(429, lambda: laps.register("unsinn", "", path=schluessel,
                                     now=jetzt + 20, client="203.0.113.9",
                                     attempts_path=voll),
          "gebremst wird vor der Eingabepruefung")

    print("\nUnterschrift")
    body = json.dumps({"lap": runde()}).encode("utf-8")
    kopf = unterschreibe({}, konto["secret"], konto["install_id"], body)
    kennung, eintrag = laps.verify(kopf, "POST", "/api/lap/submit", body,
                                   path=schluessel)
    pruefe(kennung == konto["install_id"], "die richtige Unterschrift wird angenommen")

    print("\nUnterschrift weist ab, was sie soll")
    falsch = unterschreibe({}, "falsches-geheimnis", konto["install_id"], body,
                           nonce="nonce-0002")
    wirft(401, lambda: laps.verify(falsch, "POST", "/api/lap/submit", body,
                                   path=schluessel), "falsches Geheimnis")

    # Derselbe Aufruf ein zweites Mal: die Nonce ist verbraucht.
    kopf2 = unterschreibe({}, konto["secret"], konto["install_id"], body,
                          nonce="nonce-0003")
    laps.verify(kopf2, "POST", "/api/lap/submit", body, path=schluessel)
    wirft(409, lambda: laps.verify(kopf2, "POST", "/api/lap/submit", body,
                                   path=schluessel),
          "dieselbe Nonce ein zweites Mal")

    # Ein veraenderter Rumpf unter derselben Unterschrift.
    anderer = json.dumps({"lap": runde(sekunden=50.0)}).encode("utf-8")
    kopf3 = unterschreibe({}, konto["secret"], konto["install_id"], body,
                          nonce="nonce-0004")
    wirft(401, lambda: laps.verify(kopf3, "POST", "/api/lap/submit", anderer,
                                   path=schluessel),
          "der Rumpf wurde unterwegs veraendert")

    # Eine alte Uhrzeit.
    alt = unterschreibe({}, konto["secret"], konto["install_id"], body,
                        nonce="nonce-0005", jetzt=time.time() - 10000)
    wirft(401, lambda: laps.verify(alt, "POST", "/api/lap/submit", body,
                                   path=schluessel), "Zeitstempel zu alt")

    print("\nDie Runde selbst")
    pruefe(laps.pruefe_runde(runde()) == [], "eine normale Runde hat keine Auffaelligkeit")
    wirft(422, lambda: laps.pruefe_runde(runde(sekunden=5.0, meter=3000.0)),
          "3 km in 5 s -- 2160 km/h")
    wirft(422, lambda: laps.pruefe_runde(runde(punkte=4)),
          "zu wenig Telemetrie")
    wirft(422, lambda: laps.pruefe_runde(runde(sprung=5000.0)),
          "ein Sprung von 5 km zwischen zwei Messpunkten")
    wirft(422, lambda: laps.pruefe_runde(runde(zeitknick=True)),
          "die Uhr laeuft rueckwaerts")
    # Telemetrie einer langsamen Runde unter einer schnellen Zeit.
    getuerkt = runde(sekunden=100.0)
    getuerkt["lapSeconds"] = 40.0
    wirft(422, lambda: laps.pruefe_runde(getuerkt),
          "die Telemetrie passt nicht zur behaupteten Zeit")

    langsam = runde(sekunden=3000.0, meter=3000.0)
    pruefe("Schnitt" in " ".join(laps.pruefe_runde(langsam)),
           "ein Schneckentempo ist auffaellig, aber nicht verboten")

    print("\nAblegen")
    r = runde()
    datensatz = laps.store(konto["install_id"], eintrag, r, [], root=bestand,
                           keys_path=schluessel)
    pruefe((bestand / (datensatz["id"] + ".json")).exists(), "die Runde liegt als Datei da")
    pruefe(len(laps.list_laps(bestand)) == 1, "und taucht in der Liste auf")
    zweimal = laps.store(konto["install_id"], eintrag, r, [], root=bestand,
                         keys_path=schluessel)
    pruefe(zweimal["id"] == datensatz["id"] and len(laps.list_laps(bestand)) == 1,
           "dieselbe Runde zweimal eingereicht ergibt EINE Datei")

    print("\nAusblenden")
    laps.set_hidden(datensatz["id"], True, "sieht falsch aus", root=bestand)
    pruefe(len(laps.list_laps(bestand)) == 0, "ausgeblendet ist sie nicht mehr in der Liste")
    pruefe(len(laps.list_laps(bestand, include_hidden=True)) == 1,
           "aber die Datei ist noch da -- nichts wird geloescht")
    laps.set_hidden(datensatz["id"], False, root=bestand)
    pruefe(len(laps.list_laps(bestand)) == 1, "und sie laesst sich wieder einblenden")
    # Wohlgeformt, aber unbekannt -- eine unfoermige Kennung ist 400, nicht 404.
    wirft(404, lambda: laps.set_hidden("0" * 20, True, root=bestand),
          "eine unbekannte Kennung")

    print("\nSperren")
    ergebnis = laps.set_banned(konto["install_id"], True, "Betrug",
                               keys_path=schluessel, root=bestand)
    pruefe(ergebnis["laps_touched"] == 1, "die Runde dieser Installation verschwindet mit")
    pruefe(len(laps.list_laps(bestand)) == 0, "und ist nicht mehr sichtbar")
    kopf4 = unterschreibe({}, konto["secret"], konto["install_id"], body,
                          nonce="nonce-0006")
    wirft(403, lambda: laps.verify(kopf4, "POST", "/api/lap/submit", body,
                                   path=schluessel),
          "eine gesperrte Installation reicht nichts mehr ein")
    wirft(403, lambda: laps.register(HW, "Neuanfang", path=schluessel),
          "und meldet sich auch nicht einfach neu an")

    print("\nEntsperren")
    laps.set_banned(konto["install_id"], False, "", keys_path=schluessel, root=bestand)
    pruefe(len(laps.list_laps(bestand)) == 1, "die Runde ist wieder da")

    # Eine Runde, die aus einem ANDEREN Grund ausgeblendet wurde, bleibt es.
    laps.set_hidden(datensatz["id"], True, "abgekuerzt", root=bestand)
    laps.set_banned(konto["install_id"], True, "nochmal", keys_path=schluessel,
                    root=bestand)
    laps.set_banned(konto["install_id"], False, "", keys_path=schluessel, root=bestand)
    versteckt = laps.list_laps(bestand, include_hidden=True)[0]
    pruefe(versteckt["hidden"] and versteckt["hidden_reason"] == "abgekuerzt",
           "ein eigenes Urteil ueberlebt Sperren und Entsperren")

    print("\nAufraeumen alter Hardware-Hashes")
    # Im Datenschutzhinweis steht zugesagt: zwoelf Monate ohne Einreichung, dann
    # weg. Eine Zusage, die nur in einem Dokument steht, ist keine -- sie faellt
    # beim ersten Auskunftsersuchen auf.
    from datetime import datetime, timedelta, timezone

    alt_schluessel = Path(tmp) / "alt.json"
    heute = time.time()

    def vor(tage: int) -> str:
        return (datetime.fromtimestamp(heute, timezone.utc)
                - timedelta(days=tage)).isoformat(timespec="seconds")

    alt_schluessel.write_text(json.dumps({"pepper": "x", "installs": {
        "frisch":     {"secret": "a", "hw": "h1", "gamertag": "Frisch",
                       "last_seen": vor(10), "banned": False},
        "alt":        {"secret": "b", "hw": "h2", "gamertag": "Alt",
                       "last_seen": vor(400), "banned": False},
        "gesperrt":   {"secret": "c", "hw": "h3", "gamertag": "Boese",
                       "last_seen": vor(400), "banned": True},
        "uralt_ges":  {"secret": "d", "hw": "h4", "gamertag": "Uralt",
                       "last_seen": vor(1200), "banned": True},
        "ohne_datum": {"secret": "e", "hw": "h5", "gamertag": "Ohne",
                       "banned": False},
    }}), encoding="utf-8")

    probe = laps.aufraeumen(heute, alt_schluessel, dry_run=True)
    pruefe(sorted(w["install_id"] for w in probe["welche"]) == ["alt", "uralt_ges"],
           "der Probelauf nennt genau die abgelaufenen")
    pruefe(sorted(json.loads(alt_schluessel.read_text())["installs"]) ==
           ["alt", "frisch", "gesperrt", "ohne_datum", "uralt_ges"],
           "und aendert dabei nichts")

    laps.aufraeumen(heute, alt_schluessel)
    uebrig = sorted(json.loads(alt_schluessel.read_text())["installs"])
    pruefe("alt" not in uebrig, "ein Jahr ohne Einreichung -- Hash entfernt")
    pruefe("frisch" in uebrig, "wer kuerzlich da war, bleibt")
    # Der wichtigste Fall: sonst hoebe sich jede Sperre nach einem Jahr von selbst
    # auf, und zwar lautlos -- der Gesperrte meldet sich neu an und ist wieder da.
    pruefe("gesperrt" in uebrig, "ein GESPERRTER bleibt laenger als ein Jahr")
    pruefe("uralt_ges" not in uebrig, "aber auch er nicht ewig")
    pruefe("ohne_datum" in uebrig,
           "ohne lesbares Datum wird nichts geloescht -- Alter unbekannt heisst behalten")

    print("\nEinreichen schiebt die Frist")
    r2 = runde(sekunden=99.0)
    laps.store(konto["install_id"], eintrag, r2, [], root=bestand,
               keys_path=schluessel)
    nach = json.loads(schluessel.read_text(encoding="utf-8"))
    pruefe(bool(nach["installs"][konto["install_id"]].get("last_seen")),
           "jede Einreichung setzt 'last_seen'")

    print("\nDie Admin-Liste gibt keine Geheimnisse heraus")
    liste = laps.installs(schluessel)
    pruefe(len(liste) == laps.MAX_INSTALLS_JE_MASCHINE, "alle Kennungen sind dabei")
    roh = json.dumps(liste)
    pruefe("secret" not in roh and konto["secret"] not in roh,
           "kein Geheimnis in der Ausgabe")
    pruefe(all(len(e["hw_prefix"]) == 12 for e in liste),
           "vom Hardware-Hash nur die ersten Zeichen")

print()
if fehler:
    print("%d Pruefung(en) fehlgeschlagen." % fehler)
    raise SystemExit(1)
print("alles bestanden")
