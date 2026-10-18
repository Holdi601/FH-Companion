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
sys.path.insert(0, str(WORKSPACE / "scripts"))

import analytics_api as api            # noqa: E402
import build_analytics_dataset as bau  # noqa: E402
import import_contrib                  # noqa: E402
import lap_submissions as laps         # noqa: E402
import scan_submissions as scans  # noqa: E402
import submit_tickets as tickets       # noqa: E402
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


def unterschrieben(secret: str, install_id: str, pfad: str, body: bytes, nonce: str,
                   ticket: tuple | None = None) -> dict:
    stamp = str(int(time.time()))
    kopf = {"X-Forza-Install": install_id, "X-Forza-Timestamp": stamp, "X-Forza-Nonce": nonce,
            "X-Forza-Signature": laps.signature(secret, "POST", pfad, stamp, nonce, body)}
    if ticket is not None:
        kopf["X-Forza-Ticket"] = ticket[0]
        kopf["X-Forza-Ticket-Signature"] = tickets.unterschrift_mit(ticket[1], "POST", pfad, stamp, nonce, body)
    return kopf


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
               "scanner_tool": "fhc/1.0.0.0", "readings": 2 * max(n, 600), "seconds": 120,
               # Je Lauf ein eigener Zeitpunkt, wie bei echten Scans: derselbe Zeitpunkt heisst
               # derselbe Scan (scan_submissions.erneut_nach_halt).
               "completed_at": "2026-10-04T03:%02d:%02d.000000+02:00" % divmod(
                   int(hashlib.sha256(run_id.encode("utf-8")).hexdigest(), 16) % 3600, 60)}
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
    SIEGEL = "siegel-schluessel-nur-fuer-den-test"
    api.KEYS_FILE.write_text(json.dumps({"keys": {}, "admin": ADMIN, "run_seal": SIEGEL}), encoding="utf-8")
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
        # Mehr Testlaeufe, als eine Installation an einem Tag einreichen darf (das Kontingent
        # selbst prueft der Abschnitt "Tageskontingent").
        scans.LAEUFE_JE_INSTALL_TAG = 1000
        # Das Bauprotokoll des Tests, nicht das des Arbeitsplatzes.
        scans.LAEUFE_DATEI = ordner / "build_runs.json"

        def auf_server(run_id: str) -> str:
            """Der Name auf dem Server: im Namensraum der Installation."""
            return "%s_%s" % (wer, run_id)

        def ticket_holen(geheimnis: str, kennung: str, zweck: str = PFAD) -> tuple:
            b = json.dumps({"for": zweck}).encode("utf-8")
            st, a = ruf(port, "POST", "/api/lap/ticket", b,
                        unterschrieben(geheimnis, kennung, "/api/lap/ticket", b, next(nonce)))
            if st != 200:
                return ("kein-ticket", b"")
            return (a["ticket"], base64.b64decode(a["key"]))

        def einreichen(zustand: dict, zeilen: str, geheimnis: str | None = None, ticket: tuple | None = None,
                       erneut: bool = False):
            rumpf_d = {"state": zustand, "rows": zeilen}
            if erneut:
                rumpf_d["resubmit"] = True
            b = json.dumps(rumpf_d).encode("utf-8")
            t = ticket or ticket_holen(geheimnis or secret, install_id)
            return ruf(port, "POST", PFAD, b,
                       unterschrieben(geheimnis or secret, install_id, PFAD, b, next(nonce), t))

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
        gefunden, _ = bau.discover_runs(ordner / "sweep", [contrib], {}, seal_key=SIEGEL)
        pruefe([(p.parent.name, w) for p, w in gefunden] == [(lauf.name, wer)],
               "der Datensatzbau findet den Lauf wie einen Fremdbeitrag: %s" % gefunden)
        pruefe((lauf / "seal.json").exists(), "der angenommene Lauf ist versiegelt")
        if lauf.exists():
            echte_zeilen = (lauf / "rows.jsonl").read_bytes()
            (lauf / "rows.jsonl").write_bytes(echte_zeilen.replace(b'"lap_time_seconds":60.0,', b'"lap_time_seconds":50.0,', 1))
            gefunden, ausgelassen = bau.discover_runs(ordner / "sweep", [contrib], {}, seal_key=SIEGEL)
            pruefe(gefunden == [] and ausgelassen == 1, "eine geaenderte Zeile bricht das Siegel: der Bau laesst den Lauf weg")
            (lauf / "rows.jsonl").write_bytes(echte_zeilen)
            gefunden, _ = bau.discover_runs(ordner / "sweep", [contrib], {}, seal_key="ein anderer schluessel")
            pruefe(gefunden == [], "mit einem anderen Schluessel passt das Siegel nicht")
            gefunden, _ = bau.discover_runs(ordner / "sweep", [contrib], {}, seal_key=SIEGEL)
            pruefe(len(gefunden) == 1, "zurueckgesetzt: das Siegel passt wieder")
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
        pruefe(antwort.get("held") is True and "PI outside the class" in antwort.get("reason", "")
               and "only 600 ranks" in antwort.get("reason", "") and "unknown board" not in antwort.get("reason", ""),
               "... zurueckgehalten fuer den PI und die Laenge, nicht weil das Board neu ist: %s" % antwort.get("reason"))
        neu_lauf = auf_server("ocr_CrossCountry_idx01_S2_20261004_050000")

        print("\nVerwalten und ausblenden")
        status, liste = ruf(port, "GET", "/api/admin/scan/list", None,
                            admin_kopf(ADMIN, "GET", "/api/admin/scan/list", b""))
        laeufe = liste.get("scans", []) if status == 200 else []
        pruefe(len(laeufe) == 2 and all("install_id" not in d for d in laeufe),
               "die Verwaltung sieht beide Scans, ohne install_id (%d)" % status)
        gehalten = {d["run_id"]: d for d in laeufe}.get(neu_lauf, {})
        pruefe(gehalten.get("hidden") is True and "PI outside" in gehalten.get("reason", "")
               and "PI outside" in gehalten.get("held", ""),
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
        gefunden, ausgelassen = bau.discover_runs(ordner / "sweep", [contrib], bau.hidden_run_ids(api.VISIBILITY_FILE), seal_key=SIEGEL)
        pruefe(ausgelassen == 2 and gefunden == [],
               "der Datensatzbau laesst den ausgeblendeten und den zurueckgehaltenen Lauf weg")
        runs = api.list_runs(ordner / "sweep", contrib, api.VISIBILITY_FILE)
        pruefe(any(r["run_id"] == lauf.name and r["hidden"] and r["by"] == wer for r in runs),
               "auch unter 'Scan runs' steht er, ausgeblendet, mit seiner Herkunft")
        koerper = json.dumps({"run_id": lauf.name, "hidden": False}).encode("utf-8")
        status, _ = ruf(port, "POST", "/api/admin/visibility", koerper,
                        admin_kopf(ADMIN, "POST", "/api/admin/visibility", koerper))
        gefunden, ausgelassen = bau.discover_runs(ordner / "sweep", [contrib], bau.hidden_run_ids(api.VISIBILITY_FILE), seal_key=SIEGEL)
        pruefe(status == 200 and ausgelassen == 1 and [p.parent.name for p, _ in gefunden] == [lauf.name],
               "wieder eingeblendet: der Bau nimmt ihn")
        # "Show" auf dem zurueckgehaltenen: freigegeben, und die Verwaltung sagt, was er war.
        koerper = json.dumps({"run_id": neu_lauf, "hidden": False}).encode("utf-8")
        status, _ = ruf(port, "POST", "/api/admin/visibility", koerper,
                        admin_kopf(ADMIN, "POST", "/api/admin/visibility", koerper))
        status2, liste = ruf(port, "GET", "/api/admin/scan/list", None,
                             admin_kopf(ADMIN, "GET", "/api/admin/scan/list", b""))
        frei = {d["run_id"]: d for d in liste.get("scans", [])}.get(neu_lauf, {})
        gefunden, ausgelassen = bau.discover_runs(ordner / "sweep", [contrib], bau.hidden_run_ids(api.VISIBILITY_FILE), seal_key=SIEGEL)
        pruefe(status == 200 and status2 == 200 and frei.get("hidden") is False
               and "PI outside" in frei.get("held", "") and ausgelassen == 0 and len(gefunden) == 2,
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
        tor("ocr_gate_mode_1", "only 600 ranks", "ein neues Board (andere Kategorie) mit 600 Raengen",
            {"modus": "Cross-Country"})
        tor("ocr_gate_class_1", "PI outside", "eine neue Klasse mit dem PI der alten", {"klasse": "S1"})

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

        print("\nOhne Freigabe von Hand: eine neue Installation")
        # Seit 2026-10-11: was jede Pruefung besteht, geht von selbst durch -- auch der erste Lauf.
        status, antwort2 = ruf(port, "POST", "/api/lap/register",
                               json.dumps({"hardware": "e" * 64, "gamertag": "NeuTester"}).encode("utf-8"))
        neu_id, neu_geheim = antwort2.get("install_id", ""), antwort2.get("secret", "")
        z_n, r_n = ein_lauf("ocr_first_1")
        b_n = json.dumps({"state": z_n, "rows": r_n}).encode("utf-8")
        st_n, a_n = ruf(port, "POST", PFAD, b_n, unterschrieben(neu_geheim, neu_id, PFAD, b_n, next(nonce),
                                                                ticket_holen(neu_geheim, neu_id)))
        pruefe(st_n == 200 and a_n.get("held") is False,
               "der erste Lauf einer neuen Installation geht von selbst durch (%s)" % a_n.get("reason"))

        def zeigen(name: str) -> int:
            k = json.dumps({"run_id": name, "hidden": False}).encode("utf-8")
            return ruf(port, "POST", "/api/admin/visibility", k, admin_kopf(ADMIN, "POST", "/api/admin/visibility", k))[0]

        print("\nEinmal-Tickets")
        t_1 = ticket_holen(secret, install_id)
        z_k, r_k = ein_lauf("ocr_ticket_1")
        st_k, a_k = einreichen(z_k, r_k, ticket=t_1)
        pruefe(st_k == 200, "mit Ticket angenommen (%d, %s)" % (st_k, a_k.get("error")))
        z_k, r_k = ein_lauf("ocr_ticket_2")
        st_k, a_k = einreichen(z_k, r_k, ticket=t_1)
        pruefe(st_k == 401 and "used" in a_k.get("error", ""), "dasselbe Ticket ein zweites Mal: 401 (%d, %s)" % (st_k, a_k.get("error")))
        st_k, a_k = einreichen(z_k, r_k, ticket=("0" * 32, b"x" * 32))
        pruefe(st_k == 401, "ein erfundenes Ticket: 401 (%d)" % st_k)
        st_k, a_k = einreichen(z_k, r_k, ticket=ticket_holen(secret, install_id, "/api/lap/submit"))
        pruefe(st_k == 401 and "another" in a_k.get("error", ""), "ein Ticket fuer Runden taugt nicht fuer Scans (%d, %s)" % (st_k, a_k.get("error")))
        t_alt = ticket_holen(secret, install_id)
        with tickets._SCHLOSS:
            inst, zweck, schl, _ = tickets._OFFEN[t_alt[0]]
            tickets._OFFEN[t_alt[0]] = (inst, zweck, schl, time.time() - 1)
        st_k, a_k = einreichen(z_k, r_k, ticket=t_alt)
        pruefe(st_k == 401 and "expired" in a_k.get("error", ""), "ein abgelaufenes Ticket: 401 (%d, %s)" % (st_k, a_k.get("error")))
        t_falsch = ticket_holen(secret, install_id)
        st_k, a_k = einreichen(z_k, r_k, ticket=(t_falsch[0], b"y" * 32))
        pruefe(st_k == 401 and "signature" in a_k.get("error", ""), "falscher Ticket-Schluessel: 401 (%d)" % st_k)
        st_k, a_k = einreichen(z_k, r_k, ticket=t_falsch)
        pruefe(st_k == 401 and "used" in a_k.get("error", ""), "auch ein Fehlversuch verbraucht das Ticket (%d)" % st_k)
        b_k = json.dumps({"state": z_k, "rows": r_k}).encode("utf-8")
        st_k, a_k = ruf(port, "POST", PFAD, b_k, unterschrieben(secret, install_id, PFAD, b_k, next(nonce)))
        pruefe(st_k == 401 and "ticket" in a_k.get("error", ""), "ohne Ticket: 401 (%d, %s)" % (st_k, a_k.get("error")))
        b_f = json.dumps({"for": "/api/admin/visibility"}).encode("utf-8")
        st_k, a_k = ruf(port, "POST", "/api/lap/ticket", b_f,
                        unterschrieben(secret, install_id, "/api/lap/ticket", b_f, next(nonce)))
        pruefe(st_k == 400, "kein Ticket fuer fremde Endpunkte (%d)" % st_k)
        offene = [ticket_holen(secret, install_id) for _ in range(tickets.OFFEN_JE_INSTALLATION + 1)]
        pruefe(offene[-1][0] == "kein-ticket", "hoechstens %d offene Tickets je Installation" % tickets.OFFEN_JE_INSTALLATION)
        with tickets._SCHLOSS:
            tickets._OFFEN.clear()

        print("\nDie automatischen Pruefungen")
        # Ein neues Board, lang genug, ohne Nachbarklassen: von selbst sichtbar.
        tor("ocr_auto_new_1", None, "ein neues Board mit 3.000 Raengen",
            {"strecke": "Brand New Road", "n": 3000})
        # Ein neues Board, deutlich langsamer als die Klasse darunter (Festival Chase A: 60,000 s).
        tor("ocr_auto_nb_1", "slower than class A", "neue Klasse S1, langsamer als A",
            {"klasse": "S1", "basis": 70.0, "n": 3000},
            lambda t: t.replace('"pi": 650', '"pi": 750'))
        # Gelesen schneller, als die App es kann; ohne Dauer; weniger Lesungen als Zeilen.
        z_t, r_t = ein_lauf("ocr_auto_tempo_1")
        z_t["seconds"] = 5
        status, a_t = einreichen(z_t, r_t)
        pruefe(a_t.get("held") is True and "faster than the app" in a_t.get("reason", ""),
               "600 Zeilen in 5 s: zurueckgehalten (%s)" % a_t.get("reason"))
        z_t, r_t = ein_lauf("ocr_auto_tempo_2")
        z_t.pop("seconds")
        status, a_t = einreichen(z_t, r_t)
        pruefe(a_t.get("held") is True and "no scan duration" in a_t.get("reason", ""),
               "ohne Dauer: zurueckgehalten (%s)" % a_t.get("reason"))
        z_t, r_t = ein_lauf("ocr_auto_read_1")
        z_t["readings"] = 10
        status, a_t = einreichen(z_t, r_t)
        pruefe(a_t.get("held") is True and "fewer readings" in a_t.get("reason", ""),
               "10 Lesungen fuer 600 Zeilen: zurueckgehalten (%s)" % a_t.get("reason"))

        def fallend(r: str) -> str:
            zeilen = [json.loads(x) for x in r.splitlines()]
            for x in zeilen[101:141:2]:
                x["lap_time_seconds"] = round(x["lap_time_seconds"] - 0.1, 3)
            return "\n".join(json.dumps(x) for x in zeilen) + "\n"
        tor("ocr_auto_fall_1", "lap times fall", "20 Zeiten springen je 0,1 s zurueck", None, fallend)
        tor("ocr_auto_pi_1", "PI outside", "der PI einer anderen Klasse", None,
            lambda t: t.replace('"pi": 650', '"pi": 450'))
        tor("ocr_auto_fast_1", "more than 10%", "eine Bestzeit 11,7 % unter dem Datensatz", None,
            lambda t: t.replace('"lap_time_seconds": 60.0,', '"lap_time_seconds": 53.0,', 1))
        tor("ocr_auto_fast_2", None, "eine Bestzeit 6,7 % unter dem Datensatz (eine Abkuerzung)", None,
            lambda t: t.replace('"lap_time_seconds": 60.0,', '"lap_time_seconds": 56.0,', 1))
        tor("ocr_auto_car_1", None, "fuenf neue Spitzenzeiten fuer ein Auto: nur markiert",
            {"basis": 60.5, "n": 10}, vorn_fremd(5))
        tor("ocr_auto_car_2", None, "zwei neue Spitzenzeiten fuer ein Auto",
            {"basis": 60.5, "n": 10}, vorn_fremd(2))
        # Mindestens 3.000 Raenge -- oder 90 % der Tiefe, die der Datensatz kennt (hier 22).
        tor("ocr_auto_short_1", "only 600 ranks", "600 Raenge eines neuen Boards", {"strecke": "Short Road"})
        tor("ocr_auto_short_2", None, "das ganze bekannte Board (22 Raenge tief)")
        # Ein neues kleines Board, bis zum bewiesenen Ende gelesen (Cross-Country: ~2.000 Raenge).
        tor("ocr_auto_short_3", None, "ein neues Board, 1.500 Raenge bis zum Ende",
            {"strecke": "Small Cross Road", "n": 1500, "ende": "invalid_tail"})
        tor("ocr_auto_short_4", "only 1500 ranks", "1.500 Raenge eines neuen Boards, angehalten",
            {"strecke": "Small Stopped Road", "n": 1500, "ende": "stopped"})
        tor("ocr_auto_short_5", "only 1500 ranks", "1.500 Raenge eines neuen Boards an einer Grenze",
            {"strecke": "Small Capped Road", "n": 1500, "ende": "row_cap_reached", "grenze": 1500})

        print("\nErneut gesendet: wartet auf den Admin")
        z_e, r_e = ein_lauf("ocr_resend_1", strecke="Resend Road", n=3000)
        st_e, a_e = einreichen(z_e, r_e, erneut=True)
        pruefe(st_e == 200 and a_e.get("held") is True and a_e.get("review") is True
               and scans.GRUND_ERNEUT in a_e.get("reason", "")
               and scans.GRUND_ERNEUT in (versteckt_mit(auf_server("ocr_resend_1")) or ""),
               "mit dem Zeichen der App: angehalten zur Durchsicht (%s)" % a_e.get("reason"))
        # Ein angehaltener Scan, ohne Zeichen unter neuem Namen noch einmal geschickt.
        z_h, r_h = ein_lauf("ocr_resend_2", strecke="Held Road")
        st_h, a_h = einreichen(z_h, r_h)
        pruefe(a_h.get("held") is True and a_h.get("review") is False, "(der erste Versand wird angehalten: %s)" % a_h.get("reason"))
        z_h2 = dict(z_h, run_id="ocr_resend_2b")
        st_h2, a_h2 = einreichen(z_h2, r_h)
        pruefe(st_h2 == 200 and a_h2.get("review") is True and scans.GRUND_ERNEUT in a_h2.get("reason", ""),
               "derselbe Scan unter neuem Namen, ohne Zeichen: auch zur Durchsicht (%s)" % a_h2.get("reason"))
        # Ein NEUER Scan desselben Boards ist kein erneuter Versand.
        z_h3, r_h3 = ein_lauf("ocr_resend_3", strecke="Held Road", n=3000)
        st_h3, a_h3 = einreichen(z_h3, r_h3)
        pruefe(st_h3 == 200 and a_h3.get("review") is False and a_h3.get("held") is False,
               "ein neuer, ganzer Scan desselben Boards: von selbst sichtbar (%s)" % a_h3.get("reason"))

        print("\nWo jeder Scan steht")
        st_l, liste_l = ruf(port, "GET", "/api/admin/scan/list", None,
                            admin_kopf(ADMIN, "GET", "/api/admin/scan/list", b""))
        nach_name = {d.get("run_id"): d for d in liste_l.get("scans", [])}
        e1 = nach_name.get(auf_server("ocr_resend_1"), {})
        e2 = nach_name.get(auf_server("ocr_resend_2"), {})
        e3 = nach_name.get(auf_server("ocr_resend_3"), {})
        pruefe(e1.get("state") == "review" and e1.get("resubmit") is True, "erneut gesendet: 'review' (%s)" % e1.get("state"))
        pruefe(e2.get("state") == "held" and "only 600 ranks" in e2.get("stateReason", ""),
               "angehalten: 'held' mit Grund (%s, %s)" % (e2.get("state"), e2.get("stateReason")))
        pruefe(e3.get("state") in ("waiting", "live"), "freigegeben: wartet auf den Bau oder ist da (%s)" % e3.get("state"))
        st_o, offen_l = ruf(port, "GET", "/api/scan/recent")
        oeff = offen_l.get("scans", []) if st_o == 200 else []
        roh = json.dumps(offen_l)
        pruefe(st_o == 200 and oeff and all("who" not in d and "run_id" not in d and "install_id" not in d for d in oeff)
               and install_id not in roh and wer not in roh,
               "die Seite sieht die letzten Scans ohne Absender (%d, %d Scans)" % (st_o, len(oeff)))
        held_road = [d for d in oeff if d.get("track") == "Held Road" and d.get("state") == "held"]
        pruefe(held_road and "only 600 ranks" in held_road[0].get("reason", ""),
               "... und ein angehaltener sagt, warum (%s)" % (held_road[0].get("reason") if held_road else None))
        api.set_visibility(auf_server("ocr_auto_short_1"), True, "copied from another board", api.VISIBILITY_FILE)
        st_o, offen_l = ruf(port, "GET", "/api/scan/recent")
        pruefe("copied from another board" not in json.dumps(offen_l),
               "der Grund des Admins bleibt in der Verwaltung")

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
