"""Bestenlisten-Scans aus der App ueber ECHTES HTTP: einreichen, ablehnen, verwalten, ausblenden.

    python scripts/test_scan_endpoints.py

Ein eigener Server auf 127.0.0.1 mit Wegwerfverzeichnissen -- nie der echte.
Geprueft wird:
  * ein Lauf landet wie ein Fremdbeitrag unter contrib/app-<kurz>/app-<kurz>_<run_id>/
    (state.json, rows.jsonl, Parquet) und wird vom Datensatzbau gefunden -- im eigenen
    Namensraum: ein gleichnamiger lokaler Lauf wird weder aus- noch eingeblendet,
  * keine Gamertags: ein mitgeschicktes Feld "gamertag" kommt nicht auf die Platte,
  * ohne oder mit falscher Unterschrift: 401,
  * kaputte Zeilen oder ein kaputter Zustand: 400, und es liegt nichts da,
  * derselbe Lauf zweimal: 409,
  * ausgeblendet ueber die Sichtbarkeitsliste: der Bau laesst ihn weg,
  * DAS TOR: ein Lauf geht nur von selbst in den Datensatz, wenn sein Board dort steht
    (Kategorie, Strecke, Klasse), die Haelfte der 20 Spitzenzeiten trifft, er mindestens
    500 Zeilen hat (bei JEDEM Ende -- das Ende behauptet die App nur) und der Admin schon
    zwei Laeufe der Installation eingeblendet hat -- sonst liegt er ausgeblendet da, mit
    Grund, und der Admin gibt ihn frei,
  * das Tageskontingent und der Schalter.
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
sys.path.insert(0, str(WORKSPACE / "scripts"))

import analytics_api as api            # noqa: E402
import build_analytics_dataset as bau  # noqa: E402
import import_contrib                  # noqa: E402
import lap_submissions as laps         # noqa: E402
import scan_submissions as scans       # noqa: E402
import serve_analytics as srv          # noqa: E402

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
        with urllib.request.urlopen(anfrage, timeout=20) as a:
            return a.status, json.loads(a.read().decode("utf-8"))
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


def ein_lauf(run_id: str, n: int = 600, strecke: str = "Festival Chase", klasse: str = "A",
             basis: float = 60.0, modus: str = "Road Racing", ende: str = "end_detected",
             gesamt: int | None = 120, grenze: int | None = None) -> tuple[dict, str]:
    """Ein Lauf, wie ihn Scan/BoardScanner.cs schreibt: state.json und rows.jsonl.

    Mit den Vorgaben trifft er die 20 Spitzenzeiten des Testdatensatzes (datensatz()).
    """
    zeilen = []
    for i in range(n):
        zeilen.append({"rank": i + 1, "lap_time_seconds": round(basis + i * 0.05, 3),
                       "car_name": "Honda Beat '91", "drivetrain": "RWD", "gearbox": "M",
                       "source_frame": "f%04d" % (i // 9), "row_index": i % 11, "rank_anchored": True,
                       "line": "%d | BEAT | 650 | RWD | 1:00.000" % (i + 1),
                       "used_abs": False, "used_tcs": False, "used_stm": False, "is_clean": True,
                       "readings": 2, "agreement": 2, "pi": 650, "pi_agreement": 2,
                       "car_full_name": "Honda Beat '91", "car_short_id": 1234, "scanner": "fhc"})
    zustand = {"run_id": run_id, "track": strecke, "performance_class": klasse,
               "rivals_mode": modus, "route_index": 3, "end_status": ende,
               "implied_total": gesamt, "length_estimator": "slope",
               "thumb_points": [{"rank": 1, "position": 0.0}, {"rank": n, "position": 0.4}],
               "status": ende, "gapless": True, "rows_collected": 99999,
               "minimum_rank": 1, "maximum_rank": 99999, "missing_ranks": 0, "scanner": "ocr",
               "scanner_tool": "fhc/1.0.0.0", "readings": 2 * n, "seconds": 120,
               "completed_at": "2026-10-04T03:12:00.000000+02:00"}
    if gesamt is None:
        zustand.pop("implied_total")
    if grenze is not None:
        zustand["row_cap"] = grenze
    return zustand, "\n".join(json.dumps(z) for z in zeilen) + "\n"


def datensatz(pfad: Path) -> None:
    """Ein winziger gebauter Datensatz (Form von build_analytics_dataset).

    Road Racing / Festival Chase / A: 20 aktuelle gueltige Zeiten, 60,000 s + i * 0,05 s --
    genau die von ein_lauf. Dazu eine verdraengte schnellere Zeit (59,000 s, gueltig, aber
    nicht mehr "current") und eine ungueltige (58,000 s): beide stehen auf keinem neuen
    Scan und duerfen nicht zu den Spitzenzeiten zaehlen. Und Festival Chase B unter Street
    Racing -- dasselbe Board in einer anderen Kategorie ist ein anderes.
    """
    sauber, aktuell = 1, 2
    pfad.write_text(json.dumps({
        "categories": ["Road Racing", "Street Racing"], "tracks": ["Festival Chase"],
        "classes": ["A", "B"], "carIds": [1234], "carNames": ["Honda Beat '91"],
        "flags": ["clean", "current"],
        "boards": [
            {"c": 0, "t": 0, "k": 0, "gcar": [0, 0, 0], "gsig": [sauber | aktuell, sauber, aktuell],
             "lgrp": [0] * 20 + [1, 2], "lms": [60000 + 50 * i for i in range(20)] + [59000, 58000],
             "lrank": list(range(1, 23))},
            {"c": 1, "t": 0, "k": 1, "gcar": [0], "gsig": [sauber | aktuell], "lgrp": [0],
             "lms": [90000], "lrank": [1]},
        ]}), encoding="utf-8")


with tempfile.TemporaryDirectory() as tmp:
    ordner = Path(tmp)
    ADMIN = "admin-geheimnis-fuer-den-test-0123456789"
    laps.KEYS_FILE = ordner / "submit_keys.json"
    laps.ANMELDE_VERSUCHE = ordner / "register_attempts.json"
    laps.LAPS_DIR = ordner / "laps"
    contrib = ordner / "contrib"
    scans.CONTRIB_ROOT = contrib
    scans.SCANS_DIR = ordner / "scans"
    api.CONTRIB_ROOT = contrib
    api.VISIBILITY_FILE = ordner / "dataset_visibility.json"
    # Ein winziger Datensatz: Festival Chase A (Road Racing) und B (Street Racing).
    api.DATASET_FILE = ordner / "laps.json"
    datensatz(api.DATASET_FILE)
    api.KEYS_FILE = ordner / "contrib_keys.json"
    api.KEYS_FILE.write_text(json.dumps({"keys": {}, "admin": ADMIN}), encoding="utf-8")
    api.FEATURES_FILE = ordner / "features.json"
    api.FEATURES_FILE.write_text(json.dumps({"lap_submissions": True}), encoding="utf-8")
    seite = ordner / "seite.html"
    seite.write_text("<html></html>", encoding="utf-8")
    srv.NUR_AUSLIEFERN = True
    # Der Test schickt mehr als die 30 POSTs, die der Eimer einer Adresse auf einmal zulaesst.
    srv.GRENZEN = dict(srv.GRENZEN, post=(0.5, 1000))

    port = freier_port()
    server = srv.Server(("127.0.0.1", port), srv.make_handler(ordner / "sweep", seite, ordner / "bauer.py"))
    threading.Thread(target=server.serve_forever, daemon=True).start()
    try:
        status, antwort = ruf(port, "POST", "/api/lap/register",
                              json.dumps({"hardware": "d" * 64, "gamertag": "ScanTester"}).encode("utf-8"))
        pruefe(status == 200, "anmelden (HTTP %d)" % status)
        install_id, secret = antwort.get("install_id", ""), antwort.get("secret", "")
        wer = scans.wer_von(install_id)
        nonce = iter("nonce-s%04d" % i for i in range(1000))
        PFAD = "/api/scan/submit"
        scans.VERTRAUEN_AB = 0

        def auf_server(run_id: str) -> str:
            """Der Name auf dem Server: im Namensraum der Installation."""
            return "%s_%s" % (wer, run_id)

        def einreichen(zustand: dict, zeilen: str, geheimnis: str | None = None):
            b = json.dumps({"state": zustand, "rows": zeilen}).encode("utf-8")
            return ruf(port, "POST", PFAD, b,
                       unterschrieben(geheimnis or secret, install_id, PFAD, b, next(nonce)))

        print("\nEinreichen")
        zustand, zeilen = ein_lauf("ocr_RoadRacing_idx03_A_20261004_031200")
        # Ein Gamertag, das NICHT ankommen darf -- auch wenn eine fremde App es mitschickt.
        mit_name = zeilen.replace('"rank": 1,', '"rank": 1, "gamertag": "SomePlayer",', 1)
        pruefe("SomePlayer" in mit_name, "(Testlauf traegt einen Gamertag)")
        status, antwort = einreichen(zustand, mit_name)
        pruefe(status == 200 and antwort.get("ok") and antwort.get("rows") == 600
               and antwort.get("run_id") == auf_server("ocr_RoadRacing_idx03_A_20261004_031200"),
               "ein Lauf wird angenommen (%d: %s)" % (status, antwort.get("error", "")))
        pruefe(antwort.get("flags") == [], "ein gewoehnlicher Lauf ist nicht auffaellig: %s" % antwort.get("flags"))
        pruefe(antwort.get("held") is False and antwort.get("reason") == "",
               "ein Lauf, der zum bekannten Board passt, wird nicht zurueckgehalten: %s" % antwort.get("reason"))
        lauf = contrib / wer / auf_server("ocr_RoadRacing_idx03_A_20261004_031200")
        pruefe(wer.startswith("app-") and len(wer) == 14 and install_id not in wer,
               "der Ordner heisst app-<10 Hex>, nicht nach der Kennung (%s)" % wer)
        pruefe(all((lauf / n).exists() for n in ("state.json", "rows.jsonl", "leaderboard_entries.parquet")),
               "state.json, rows.jsonl und Parquet liegen unter contrib/%s/<run_id>/" % wer)
        gespeichert = (lauf / "rows.jsonl").read_text(encoding="utf-8") if lauf.exists() else ""
        pruefe("SomePlayer" not in gespeichert and "gamertag" not in gespeichert.lower(),
               "kein Gamertag in den abgelegten Zeilen")
        st = json.loads((lauf / "state.json").read_text(encoding="utf-8")) if lauf.exists() else {}
        pruefe(st.get("rows_collected") == 600 and st.get("maximum_rank") == 600 and st.get("contributed_by") == wer
               and st.get("run_id") == lauf.name,
               "Zaehlungen aus den Zeilen, nicht aus der Behauptung (%s, %s)" % (st.get("rows_collected"), st.get("maximum_rank")))
        pruefe(install_id not in json.dumps(st), "die Kennung der Installation steht nicht im Lauf")
        if lauf.exists():
            import pandas
            tafel = pandas.read_parquet(lauf / "leaderboard_entries.parquet")
            pruefe(len(tafel) == 600 and "gamertag" not in tafel.columns and tafel["rank"].dtype.kind == "i",
                   "das Parquet hat 600 Zeilen, Raenge als Zahlen, keinen Gamertag")
        gefunden, _ = bau.discover_runs(ordner / "sweep", [contrib], {})
        pruefe([(p.parent.name, w) for p, w in gefunden] == [(lauf.name, wer)],
               "der Datensatzbau findet den Lauf wie einen Fremdbeitrag: %s" % gefunden)
        pruefe(not (scans.SCANS_DIR / "_eingang").exists() or not any((scans.SCANS_DIR / "_eingang").iterdir()),
               "kein halber Lauf bleibt im Eingang liegen")

        print("\nUnterschrift")
        b = json.dumps({"state": zustand, "rows": zeilen}).encode("utf-8")
        status, _ = ruf(port, "POST", PFAD, b)
        pruefe(status == 401, "ohne Unterschrift: 401 (war %d)" % status)
        z2, r2 = ein_lauf("ocr_RoadRacing_idx03_A_20261004_040000")
        status, _ = einreichen(z2, r2, geheimnis="falsch" * 8)
        pruefe(status == 401, "mit falschem Geheimnis: 401 (war %d)" % status)
        pruefe(not any(contrib.glob("*/*ocr_RoadRacing_idx03_A_20261004_040000")), "... und nichts abgelegt")

        print("\nKaputte Zeilen, kaputter Zustand")
        z3, r3 = ein_lauf("ocr_bad_rows_1")
        status, antwort = einreichen(z3, r3.replace('"rank": 2,', '"rank": 1,', 1))
        pruefe(status == 400 and "twice" in antwort.get("error", ""), "ein Platz doppelt: 400 (%d: %s)"
               % (status, antwort.get("error", "")))
        z3, r3 = ein_lauf("ocr_bad_rows_2")
        status, antwort = einreichen(z3, r3.replace('"lap_time_seconds": 60.0,', '"lap_time_seconds": 2.0,', 1))
        pruefe(status == 400 and "out of range" in antwort.get("error", ""), "eine Zeit von 2 s: 400")
        z3, r3 = ein_lauf("ocr_bad_rows_3")
        status, _ = einreichen(z3, "")
        pruefe(status == 400, "ohne Zeilen: 400 (war %d)" % status)
        status, _ = einreichen(z3, "kein json\n")
        pruefe(status == 400, "Zeilen, die kein JSON sind: 400 (war %d)" % status)
        z3, r3 = ein_lauf("ocr_bad_state_1")
        z3["track"] = ""
        status, _ = einreichen(z3, r3)
        pruefe(status == 400, "ein Zustand ohne Strecke: 400 (war %d)" % status)
        z3, r3 = ein_lauf("../../etc")
        status, _ = einreichen(z3, r3)
        pruefe(status == 400, "ein Laufname, der aus dem Ordner fuehrt: 400 (war %d)" % status)
        z3, r3 = ein_lauf("ocr_bad_class", klasse="Q")
        status, _ = einreichen(z3, r3)
        pruefe(status == 400, "eine unbekannte Klasse: 400 (war %d)" % status)
        pruefe(not any(contrib.glob("*/*ocr_bad_*")), "von den kaputten Laeufen liegt nichts da")
        k = laps.load_keys()["installs"][install_id]
        pruefe(not k.get("banned") and not k.get("strikes"), "abgelehnte Scans kosten keinen Strafpunkt")

        print("\nDerselbe Lauf zweimal")
        status, antwort = einreichen(zustand, zeilen)
        pruefe(status == 409 and "already" in antwort.get("error", ""), "zweimal derselbe run_id: 409 (%d)" % status)

        print("\nAuffaelliges wird angenommen und markiert")
        z4, r4 = ein_lauf("ocr_CrossCountry_idx01_S2_20261004_050000", strecke="Somewhere New", klasse="S2")
        r4 = r4.replace('"lap_time_seconds": 60.1,', '"lap_time_seconds": 59.0,', 1)
        status, antwort = einreichen(z4, r4)
        flags = antwort.get("flags", [])
        pruefe(status == 200 and "board not in the dataset yet" in flags and "PI outside the class" in flags
               and "lap times fall as the rank rises" in flags, "neues Board, falscher PI, fallende Zeit: %s" % flags)
        pruefe(antwort.get("held") is True and "unknown board" in antwort.get("reason", ""),
               "... und ein unbekanntes Board wird zurueckgehalten: %s" % antwort.get("reason"))
        neu_lauf = auf_server("ocr_CrossCountry_idx01_S2_20261004_050000")

        print("\nVerwalten und ausblenden")
        status, liste = ruf(port, "GET", "/api/admin/scan/list", None,
                            admin_kopf(ADMIN, "GET", "/api/admin/scan/list", b""))
        laeufe = liste.get("scans", []) if status == 200 else []
        pruefe(len(laeufe) == 2 and all("install_id" not in d for d in laeufe),
               "die Verwaltung sieht beide Scans, ohne install_id (%d)" % status)
        gehalten = {d["run_id"]: d for d in laeufe}.get(neu_lauf, {})
        pruefe(gehalten.get("hidden") is True and "unknown board" in gehalten.get("reason", "")
               and "unknown board" in gehalten.get("held", ""),
               "die Verwaltung zeigt den zurueckgehaltenen Lauf ausgeblendet, mit Grund: %s" % gehalten.get("reason"))
        status, _ = ruf(port, "GET", "/api/admin/scan/list")
        pruefe(status == 401, "ohne Admin-Unterschrift: 401 (war %d)" % status)
        koerper = json.dumps({"run_id": lauf.name, "hidden": True, "reason": "test"}).encode("utf-8")
        status, _ = ruf(port, "POST", "/api/admin/visibility", koerper,
                        admin_kopf(ADMIN, "POST", "/api/admin/visibility", koerper))
        pruefe(status == 200, "ausblenden ueber die Sichtbarkeitsliste (HTTP %d)" % status)
        status, liste = ruf(port, "GET", "/api/admin/scan/list", None,
                            admin_kopf(ADMIN, "GET", "/api/admin/scan/list", b""))
        versteckt = {d["run_id"]: d for d in liste.get("scans", []) if d.get("hidden")}
        pruefe(sorted(versteckt) == sorted([lauf.name, neu_lauf]) and versteckt[lauf.name].get("reason") == "test",
               "die Verwaltung zeigt ihn als ausgeblendet")
        gefunden, ausgelassen = bau.discover_runs(ordner / "sweep", [contrib], bau.hidden_run_ids(api.VISIBILITY_FILE))
        pruefe(ausgelassen == 2 and gefunden == [],
               "der Datensatzbau laesst den ausgeblendeten und den zurueckgehaltenen Lauf weg")
        runs = api.list_runs(ordner / "sweep", contrib, api.VISIBILITY_FILE)
        pruefe(any(r["run_id"] == lauf.name and r["hidden"] and r["by"] == wer for r in runs),
               "auch unter 'Scan runs' steht er, ausgeblendet, mit seiner Herkunft")
        koerper = json.dumps({"run_id": lauf.name, "hidden": False}).encode("utf-8")
        status, _ = ruf(port, "POST", "/api/admin/visibility", koerper,
                        admin_kopf(ADMIN, "POST", "/api/admin/visibility", koerper))
        gefunden, ausgelassen = bau.discover_runs(ordner / "sweep", [contrib], bau.hidden_run_ids(api.VISIBILITY_FILE))
        pruefe(status == 200 and ausgelassen == 1 and [p.parent.name for p, _ in gefunden] == [lauf.name],
               "wieder eingeblendet: der Bau nimmt ihn")
        # "Show" auf dem zurueckgehaltenen: freigegeben, und die Verwaltung sagt, was er war.
        koerper = json.dumps({"run_id": neu_lauf, "hidden": False}).encode("utf-8")
        status, _ = ruf(port, "POST", "/api/admin/visibility", koerper,
                        admin_kopf(ADMIN, "POST", "/api/admin/visibility", koerper))
        status2, liste = ruf(port, "GET", "/api/admin/scan/list", None,
                             admin_kopf(ADMIN, "GET", "/api/admin/scan/list", b""))
        frei = {d["run_id"]: d for d in liste.get("scans", [])}.get(neu_lauf, {})
        gefunden, ausgelassen = bau.discover_runs(ordner / "sweep", [contrib], bau.hidden_run_ids(api.VISIBILITY_FILE))
        pruefe(status == 200 and status2 == 200 and frei.get("hidden") is False
               and "unknown board" in frei.get("held", "") and ausgelassen == 0 and len(gefunden) == 2,
               "freigegeben: der Bau nimmt ihn, die Verwaltung behaelt den alten Grund (%s)" % frei.get("held"))

        print("\nWas auf der Seite gewinnt, wird nicht blind uebernommen")
        # Der Bau nimmt den juengsten completed_at je Board als "current" und die groesste
        # implied_total als Boardgroesse -- beides darf eine Installation nicht erzwingen.
        z7, r7 = ein_lauf("ocr_future_1")
        z7["completed_at"] = "2099-01-01T00:00:00.000000+00:00"
        z7["implied_total"] = 10 ** 11
        status, _ = einreichen(z7, r7)
        st7 = json.loads(next(contrib.glob("*/" + auf_server("ocr_future_1") + "/state.json")).read_text(encoding="utf-8")) \
            if status == 200 else {}
        pruefe(status == 200 and str(st7.get("completed_at", ""))[:4] == time.strftime("%Y")
               and "implied_total" not in st7 and "row_coverage" not in st7,
               "ein Zeitpunkt in der Zukunft wird die Empfangszeit, eine riesige Laenge faellt weg (%s, %s)"
               % (st7.get("completed_at"), st7.get("implied_total")))
        z8, r8 = ein_lauf("ocr_past_1")
        status, _ = einreichen(z8, r8)
        st8 = json.loads(next(contrib.glob("*/" + auf_server("ocr_past_1") + "/state.json")).read_text(encoding="utf-8")) \
            if status == 200 else {}
        pruefe(status == 200 and str(st8.get("completed_at", "")).startswith("2026-10-04T")
               and st8.get("implied_total") == 120,
               "ein gewoehnlicher Zeitpunkt und eine gewoehnliche Laenge bleiben (%s)" % st8.get("completed_at"))

        print("\nDas Tor")

        def versteckt_mit(run_id: str) -> str | None:
            """Der Grund auf der Sichtbarkeitsliste -- oder None, wenn der Lauf sichtbar ist."""
            return bau.hidden_run_ids(api.VISIBILITY_FILE).get(run_id)

        def tor(run_id: str, erwartet: str | None, was: str, lauf_art: dict | None = None, zeilen_neu=None) -> None:
            z, r = ein_lauf(run_id, **(lauf_art or {}))
            if zeilen_neu is not None:
                r = zeilen_neu(r)
            st, a = einreichen(z, r)
            grund = versteckt_mit(auf_server(run_id))
            liegt = any(contrib.glob("*/" + auf_server(run_id) + "/state.json"))
            if erwartet is None:
                pruefe(st == 200 and liegt and grund is None and a.get("held") is False,
                       "%s: sichtbar (%d, %s)" % (was, st, a.get("reason") or a.get("error")))
            else:
                pruefe(st == 200 and liegt and grund is not None and erwartet in grund
                       and a.get("held") is True and erwartet in a.get("reason", ""),
                       "%s: abgelegt, ausgeblendet mit '%s' (%d, %s)" % (was, erwartet, st, grund))

        tor("ocr_gate_times_1", "times do not match", "andere Zeiten auf einem bekannten Board", {"basis": 70.0})
        tor("ocr_gate_mode_1", "unknown board", "dasselbe Board in einer anderen Kategorie", {"modus": "Cross-Country"})
        tor("ocr_gate_class_1", "unknown board", "eine Klasse, die der Datensatz fuer diese Strecke nicht hat",
            {"klasse": "S1"})

        # Die Haelfte: 10 der 20 Spitzenzeiten getroffen reicht, 9 nicht. Davor je zwei Zeilen
        # mit Zeiten, die der Datensatz nicht kennt.
        def vorn_fremd(n_fremd: int):
            def f(r: str) -> str:
                zeilen = [json.loads(x) for x in r.splitlines()]
                for i, x in enumerate(zeilen):
                    x["rank"] = i + 1 + n_fremd
                fremd = [dict(zeilen[0], rank=i + 1, lap_time_seconds=round(60.01 + i * 0.002, 3))
                         for i in range(n_fremd)]
                alle = fremd + zeilen
                hinten = [dict(zeilen[0], rank=len(alle) + i + 1, lap_time_seconds=round(200.0 + i * 0.01, 3))
                          for i in range(600 - len(alle))]
                return "\n".join(json.dumps(x) for x in alle + hinten) + "\n"
            return f
        tor("ocr_gate_half_1", None, "genau die Haelfte der Spitzenzeiten getroffen (10 von 20)",
            {"basis": 60.5, "n": 10}, vorn_fremd(2))
        tor("ocr_gate_half_2", "times do not match", "eine weniger als die Haelfte (9 von 20)",
            {"basis": 60.55, "n": 9}, vorn_fremd(2))
        # Auf 1 ms genau: jede Zeit um 1 ms daneben trifft noch, um 2 ms nicht mehr.
        tor("ocr_gate_ms_1", None, "jede Zeit 1 ms daneben", {"basis": 60.001})
        tor("ocr_gate_ms_2", "times do not match", "jede Zeit 2 ms daneben", {"basis": 60.002})
        # Die verdraengte (59,000 s) und die ungueltige (58,000 s) Zeit sind keine Spitzenzeiten:
        # ein Lauf, der nur sie traegt, trifft nichts.
        tor("ocr_gate_old_1", "times do not match", "nur die verdraengte und die ungueltige Zeit", {"n": 2},
            lambda r: r.replace('"lap_time_seconds": 60.0,', '"lap_time_seconds": 58.0,', 1)
                       .replace('"lap_time_seconds": 60.05,', '"lap_time_seconds": 59.0,', 1))
        # Schneller als der Datensatz, aber die Zeiten darunter stimmen: sichtbar, nur markiert.
        z9, r9 = ein_lauf("ocr_gate_fast_1")
        r9 = r9.replace('"lap_time_seconds": 60.0,', '"lap_time_seconds": 57.0,', 1)
        status, antwort = einreichen(z9, r9)
        pruefe(status == 200 and antwort.get("held") is False and versteckt_mit(auf_server("ocr_gate_fast_1")) is None
               and any("2%" in f for f in antwort.get("flags", [])),
               "eine Bestzeit mehr als 2 %% unter dem Datensatz: sichtbar, aber markiert (%s)" % antwort.get("flags"))

        print("\nDas Tor: unbewiesene Enden wie beim alten Werkzeug")
        tor("ocr_gate_stop_1", "end not proven", "angehalten nach 50 Zeilen", {"ende": "stopped", "n": 50})
        tor("ocr_gate_stop_2", None, "angehalten nach 600 von etwa 700 Zeilen (86 %)",
            {"ende": "stopped", "n": 600, "gesamt": 700})
        tor("ocr_gate_stop_3", "end not proven", "angehalten nach 600 von etwa 1.000 Zeilen (60 %)",
            {"ende": "stopped", "n": 600, "gesamt": 1000})
        tor("ocr_gate_stop_4", None, "angehalten nach 600 Zeilen, Laenge unbekannt",
            {"ende": "stopped", "n": 600, "gesamt": None})
        tor("ocr_gate_crash_1", "end not proven", "Spiel abgestuerzt nach 50 Zeilen", {"ende": "game_crashed", "n": 50})
        tor("ocr_gate_server_1", "server error", "Server-Fehler, auch mit 600 Zeilen",
            {"ende": "server_error", "n": 600, "gesamt": 600})
        # DAS ENDE BEHAUPTET DIE APP NUR: auch ein "bewiesenes" Ende braucht 500 Zeilen.
        tor("ocr_gate_short_1", "only 50 rows", "Ende erkannt nach 50 Zeilen", {"n": 50})
        tor("ocr_gate_cap_1", "only 50 rows", "die ersten 50 Plaetze, bewusst begrenzt",
            {"ende": "row_cap_reached", "n": 50, "grenze": 50})
        tor("ocr_gate_cap_2", None, "die ersten 600 Plaetze, bewusst begrenzt",
            {"ende": "row_cap_reached", "grenze": 600})
        tor("ocr_gate_cap_3", "row cap", "Grenze 5000, nur 600 gelesen",
            {"ende": "row_cap_reached", "grenze": 5000})
        tor("ocr_gate_cap_4", "row cap", "Grenze behauptet, aber keine angegeben", {"ende": "row_cap_reached"})
        tor("ocr_gate_tail_1", None, "bis zum Block der ungueltigen", {"ende": "invalid_tail"})
        z10, r10 = ein_lauf("ocr_gate_two_1", basis=70.0, ende="stopped", n=50)
        status, antwort = einreichen(z10, r10)
        pruefe(status == 200 and "times do not match" in antwort.get("reason", "")
               and "end not proven" in antwort.get("reason", ""),
               "zwei Gruende zugleich stehen beide da: %s" % antwort.get("reason"))

        print("\nDas Tor: Namen, die unter Windows ein anderer Ordner wuerden")
        # Gebaut und ausgeblendet wird auf Windows nach dem Ordnernamen. "x." wird dort zu "x",
        # und der Eintrag "x." auf der Liste traefe den zurueckgehaltenen Lauf nicht mehr.
        for name in ("ocr_gate_dot_1.", "ocr_gate_nl_1.\n", "CON", "nul.ocr_gate"):
            zn, rn = ein_lauf(name, basis=70.0)
            status, antwort = einreichen(zn, rn)
            pruefe(status == 400 and not any(contrib.glob("*/" + name.strip() + "/state.json"))
                   and not any(contrib.glob("*/" + name.strip().rstrip(".") + "/state.json")),
                   "Name %r abgelehnt (%d: %s)" % (name, status, antwort.get("error", "")))

        print("\nDas Tor: ein Doppelter blendet das Original nicht aus")
        # Derselbe run_id wie der erste Lauf, diesmal mit Zeiten, die nicht passen: 409 -- und der
        # erste bleibt sichtbar. Blendete das Tor vor der Doppelpruefung aus, verschwaende er.
        z11, r11 = ein_lauf("ocr_RoadRacing_idx03_A_20261004_031200", basis=70.0)
        status, _ = einreichen(z11, r11)
        pruefe(status == 409 and versteckt_mit(lauf.name) is None,
               "ein abgelehnter Doppelter mit falschen Zeiten: 409, das Original bleibt sichtbar")

        print("\nDas Tor: ohne lesbaren Datensatz wird ausgeblendet")
        echt = api.DATASET_FILE.read_bytes()
        api.DATASET_FILE.write_text("{kaputt", encoding="utf-8")
        tor("ocr_gate_nodata_1", "dataset not readable", "kaputter Datensatz")
        api.DATASET_FILE.unlink()
        tor("ocr_gate_nodata_2", "dataset not readable", "kein Datensatz")
        api.DATASET_FILE.write_bytes(echt)
        tor("ocr_gate_back_1", None, "der Datensatz ist wieder da")

        print("\nDas Tor: Datensatzformen, die nicht passen")
        formen = [
            ("ohne boards", {"tracks": [], "classes": []}),
            ("boards ohne Zeiten", {"tracks": ["Festival Chase"], "classes": ["A"], "boards": [{"t": 0, "k": 0}]}),
            ("Muell in den Spalten", {"tracks": ["Festival Chase"], "classes": ["A"], "flags": ["clean"], "boards": [
                {"t": 0, "k": 0, "gsig": [1, "x"], "lgrp": [0, 1, 7, "a", 0], "lms": [60000, 60050, 60100, 60150, None]},
                {"t": 9, "k": 0}, "kein board"]}),
        ]
        b = None
        for nr, (name, inhalt) in enumerate(formen):
            f = ordner / ("ds_%d.json" % nr)
            f.write_text(json.dumps(inhalt), encoding="utf-8")
            b = scans.lade_bretter(f)
            pruefe(b is None or isinstance(b, dict), "%s: lade_bretter wirft nicht (%s)" % (name, type(b).__name__))
        gefunden_b = scans.brett_von(b, {"track": " festival  CHASE ", "performance_class": "a",
                                         "rivals_mode": "Touge"}) if b is not None else None
        pruefe(gefunden_b == [60000],
               "ein Board ohne Kategorie gilt fuer jede; Strecke und Klasse gefaltet; Muell faellt weg: %s" % gefunden_b)
        pruefe(scans.zeiten_passen([], [{"lap_time_seconds": 60.0}]) is False
               and scans.zeiten_passen([60000], []) is False,
               "ohne bekannte oder ohne eingereichte Zeiten passt nichts")

        print("\nDas Tor: eine neue Installation")
        # Ihre ersten Laeufe sieht ein Mensch an -- auch wenn alles andere passt. Vertraut ist
        # sie erst, wenn der Admin VERTRAUEN_AB ihrer zurueckgehaltenen Laeufe eingeblendet hat.
        scans.VERTRAUEN_AB = 2
        status, antwort2 = ruf(port, "POST", "/api/lap/register",
                               json.dumps({"hardware": "e" * 64, "gamertag": "NeuTester"}).encode("utf-8"))
        neu_id, neu_geheim = antwort2.get("install_id", ""), antwort2.get("secret", "")
        neu_wer = scans.wer_von(neu_id)

        def neu_einreichen(name: str):
            z, r = ein_lauf(name)
            b = json.dumps({"state": z, "rows": r}).encode("utf-8")
            return ruf(port, "POST", PFAD, b, unterschrieben(neu_geheim, neu_id, PFAD, b, next(nonce)))

        def zeigen(name: str) -> int:
            k = json.dumps({"run_id": name, "hidden": False}).encode("utf-8")
            return ruf(port, "POST", "/api/admin/visibility", k, admin_kopf(ADMIN, "POST", "/api/admin/visibility", k))[0]

        st_a, a_a = neu_einreichen("ocr_trust_1")
        st_b, a_b = neu_einreichen("ocr_trust_2")
        pruefe(st_a == 200 and st_b == 200 and a_a.get("held") is True and a_b.get("held") is True
               and "new installation" in a_a.get("reason", ""),
               "eine neue Installation: ein passender Lauf wird trotzdem zurueckgehalten (%s)" % a_a.get("reason"))
        pruefe(zeigen(a_a.get("run_id", "")) == 200, "der Admin gibt den ersten frei")
        st_c, a_c = neu_einreichen("ocr_trust_3")
        pruefe(st_c == 200 and a_c.get("held") is True and "new installation" in a_c.get("reason", ""),
               "ein freigegebener Lauf reicht noch nicht (%s)" % a_c.get("reason"))
        pruefe(zeigen(a_b.get("run_id", "")) == 200, "der Admin gibt den zweiten frei")
        st_d, a_d = neu_einreichen("ocr_trust_4")
        pruefe(st_d == 200 and a_d.get("held") is False,
               "nach zwei freigegebenen Laeufen geht ein passender von selbst durch (%s)" % a_d.get("reason"))
        scans.VERTRAUEN_AB = 0

        print("\nDas Tor: ein eigener Namensraum")
        # Ein lokaler Lauf und ein App-Lauf mit demselben Namen: der zurueckgehaltene App-Lauf
        # blendet den lokalen nicht aus, und ein "Show" des App-Laufs blendet keinen
        # ausgeblendeten lokalen ein.
        lokal = ordner / "sweep" / "ocr_Touge_idx04_R_20260912_230551"
        lokal.mkdir(parents=True)
        (lokal / "state.json").write_text(json.dumps(ein_lauf(lokal.name)[0]), encoding="utf-8")
        zl, rl = ein_lauf(lokal.name, basis=70.0)
        status, antwort = einreichen(zl, rl)
        pruefe(status == 200 and antwort.get("held") is True and antwort.get("run_id") == auf_server(lokal.name)
               and versteckt_mit(lokal.name) is None and versteckt_mit(auf_server(lokal.name)) is not None,
               "der App-Lauf liegt im eigenen Namensraum ausgeblendet, der lokale bleibt sichtbar (%s)" % antwort.get("run_id"))
        api.set_visibility(lokal.name, True, "admin", api.VISIBILITY_FILE)
        pruefe(zeigen(auf_server(lokal.name)) == 200 and versteckt_mit(lokal.name) == "admin",
               "ein 'Show' des App-Laufs blendet den ausgeblendeten lokalen nicht ein")

        print("\nTageskontingent")
        alt = scans.LAEUFE_JE_INSTALL_TAG
        scans.LAEUFE_JE_INSTALL_TAG = sum(1 for d in scans.list_scans() if d.get("install_id") == install_id)
        z5, r5 = ein_lauf("ocr_quota_1")
        status, _ = einreichen(z5, r5)
        pruefe(status == 429, "mehr Laeufe als erlaubt an einem Tag: 429 (war %d)" % status)
        scans.LAEUFE_JE_INSTALL_TAG = alt

        print("\nDie gemeinsame Ablage mit den Freunden")
        # import_contrib benutzt dieselbe Schreibfunktion -- ein Lauf von dort sieht gleich aus.
        import_contrib.write_run(contrib / "kai" / "ocr_friend_1", ein_lauf("ocr_friend_1")[0],
                                 [{"rank": 1, "lap_time_seconds": 61.0}], "kai", "test")
        pruefe((contrib / "kai" / "ocr_friend_1" / "leaderboard_entries.parquet").exists(),
               "import_contrib.write_run legt einen Freundeslauf ab")

        print("\nAusgeschaltet: nicht da")
        api.FEATURES_FILE.write_text(json.dumps({"lap_submissions": False}), encoding="utf-8")
        z6, r6 = ein_lauf("ocr_off_1")
        status, _ = einreichen(z6, r6)
        pruefe(status == 404, "POST /api/scan/submit ist zu (HTTP %d)" % status)
    finally:
        server.shutdown()
        server.server_close()

print()
if fehler:
    print("%d Pruefung(en) fehlgeschlagen." % fehler)
    raise SystemExit(1)
print("alles bestanden")
