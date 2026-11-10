"""Der Stichtag der Rivals-Daten (seit 2026-11-10): Bau, Sicht, Seite und Server.

    python scripts/test_dataset_cutoff.py

Baut aus erfundenen Laeufen (drei Tage, einer nach der Loeschung der Bestenlisten am
2026-11-03) einen Datensatz mit build_analytics_dataset und prueft:

  * DIE SICHT IST EXAKT: fuer jeden Stichtag stimmen die Runden je Gruppe, gcount, rows,
    valid, invalid, maxRank, dist und impl mit einem Bau ueberein, der JEDE Runde behaelt
    (LAPS_PER_GROUP unbegrenzt) -- also mit dem, was ein Bau nur aus den Laeufen ab dem
    Stichtag ergaebe, bis auf die Grenze von fuenf Runden je Gruppe,
  * ALTE LESER sehen dasselbe wie vorher: die fuenf schnellsten Runden je Gruppe und alle
    Zaehlungen ueber alles sind dieselben,
  * keine Runde wird ueber die Loeschung hinweg mit einer anderen zusammengelegt,
  * die Seite (scripts/dataset_cutoff.js) rechnet genau wie server/stichtag.py,
  * der Server: "most needed" (scan_needs), das Tor (scan_submissions), die Pruefung
    eingereichter Runden (leaderboard_check) und die Horizon-Play-Zeiten nur ab dem Stichtag.

Nichts davon beruehrt den echten Datensatz; alles liegt in einem Wegwerfordner.
"""
from __future__ import annotations

import json
import random
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path

import pyarrow as pa
import pyarrow.parquet as pq

WORKSPACE = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(WORKSPACE / "server"))
sys.path.insert(0, str(WORKSPACE / "scripts"))

import build_analytics_dataset as bau  # noqa: E402
import stichtag  # noqa: E402

fehler = 0


def pruefe(bedingung, text: str) -> None:
    global fehler
    if bedingung:
        print("  ok    " + text)
    else:
        fehler += 1
        print("  FEHL  " + text)


SPALTEN = ["rank", "lap_time_seconds", "car_id", "car_codename", "car_name", "pi", "is_clean",
           "used_tcs", "used_abs", "used_stm", "used_friction_assist", "used_auto_brake",
           "used_auto_shifting", "used_clutch", "used_super_easy_assist"]


def lauf(root: Path, run_id: str, strecke: str, klasse: str, wann: str, zeilen: list[dict],
         gesamt: int | None = None) -> None:
    ordner = root / run_id
    ordner.mkdir(parents=True)
    tabelle = {s: [z.get(s) for z in zeilen] for s in SPALTEN}
    schema = pa.schema([("rank", pa.int64()), ("lap_time_seconds", pa.float64()), ("car_id", pa.int64()),
                        ("car_codename", pa.string()), ("car_name", pa.string()), ("pi", pa.int64())]
                       + [(s, pa.bool_()) for s in SPALTEN[6:]])
    pq.write_table(pa.table(tabelle, schema=schema), ordner / "leaderboard_entries.parquet")
    zustand = {"run_id": run_id, "track": strecke, "performance_class": klasse,
               "rivals_mode": "Road Racing", "completed_at": wann, "status": "end_detected",
               "rows_collected": len(zeilen), "maximum_rank": max(z["rank"] for z in zeilen),
               "scanner": "memory"}
    if gesamt:
        zustand.update({"implied_total": gesamt, "length_estimator": "slope",
                        "thumb_points": [{"rank": 1, "position": 0.0}] * 3})
    (ordner / "state.json").write_text(json.dumps(zustand), encoding="utf-8")


def zeile(rank: int, sekunden: float, auto: int, sauber: bool = True, tcs: bool = False) -> dict:
    return {"rank": rank, "lap_time_seconds": sekunden, "car_id": auto, "car_codename": None,
            "car_name": None, "pi": None, "is_clean": sauber, "used_tcs": tcs, "used_abs": False,
            "used_stm": False, "used_friction_assist": False, "used_auto_brake": False,
            "used_auto_shifting": False, "used_clutch": False, "used_super_easy_assist": False}


def board_laeufe(rng: random.Random, basis: float, autos: list[int], n: int, tcs_anteil: float = 0.2,
                 ungueltig: int = 10) -> list[dict]:
    """Ein Board wie das Spiel: gueltige Runden nach Zeit, dann die ungueltigen von vorn."""
    zeiten = sorted(round(basis + rng.random() * 20 + rng.random() ** 3 * 30, 3) for _ in range(n))
    zeilen = [zeile(i + 1, t, rng.choice(autos), True, rng.random() < tcs_anteil) for i, t in enumerate(zeiten)]
    for j, t in enumerate(sorted(round(basis - 2 + rng.random() * 25, 3) for _ in range(ungueltig))):
        zeilen.append(zeile(n + j + 1, t, rng.choice(autos), False))
    return zeilen


def gruppen(b: dict, flags: list[str]) -> dict:
    """(car_id-Index, Signatur) -> sortierte Rundenzeiten."""
    out: dict = {}
    for i, g in enumerate(b["lgrp"]):
        out.setdefault((b["gcar"][g], b["gsig"][g]), []).append(b["lms"][i])
    return {k: sorted(v) for k, v in out.items()}


def zaehlung(b: dict) -> dict:
    return {(b["gcar"][g], b["gsig"][g]): b["gcount"][g] for g in range(len(b["gcar"]))}


def bauen(root: Path, leer: Path, unbegrenzt: bool = False) -> dict:
    alt = bau.LAPS_PER_GROUP
    if unbegrenzt:
        bau.LAPS_PER_GROUP = 10 ** 9
    try:
        return bau.build(root, contrib_roots=[leer], visibility=leer / "keine.json", runs_out=None)
    finally:
        bau.LAPS_PER_GROUP = alt


def main() -> int:
    rng = random.Random(20261103)
    with tempfile.TemporaryDirectory(dir="E:/temp/fhc" if Path("E:/temp/fhc").exists() else None) as tmp:
        ordner = Path(tmp)
        root, leer = ordner / "runs", ordner / "contrib"
        leer.mkdir()
        autos = [101, 102, 103, 104, 105, 106]

        # Board 1 (Testkurs A): drei Tage. Lauf 2 enthaelt einen Teil von Lauf 1 noch einmal
        # (gleicher Rang, gleiche Zeit) -- diese Zeilen tragen danach den juengeren Tag.
        alt1 = board_laeufe(rng, 60.0, autos, 400)
        alt2 = [dict(z) for z in alt1[:150]] + board_laeufe(rng, 60.5, autos, 200)[150:]
        for i, z in enumerate(alt2):
            z["rank"] = i + 1
        neu = board_laeufe(rng, 63.0, autos[:4], 300)   # nach der Loeschung: langsamer, andere Autos
        lauf(root, "r1", "Testkurs", "A", "2026-10-01T12:00:00+02:00", alt1, gesamt=5000)
        lauf(root, "r2", "Testkurs", "A", "2026-10-20T12:00:00+02:00", alt2, gesamt=5200)
        lauf(root, "r3", "Testkurs", "A", "2026-11-05T12:00:00+01:00", neu, gesamt=3000)
        # Drei Tage spaeter ist das neue Board gewachsen und schneller: Platz r hat eine andere
        # Zeit als in Lauf 3. Beide Laeufe muessen ganz bleiben (placeholder_times_by_era).
        neu2 = board_laeufe(rng, 61.5, autos[:5], 350)
        lauf(root, "r6", "Testkurs", "A", "2026-11-08T12:00:00+01:00", neu2, gesamt=4000)
        zustand6 = json.loads((root / "r6" / "state.json").read_text(encoding="utf-8"))
        zustand6["thumb_points"] = [{"rank": 1, "position": 0.0}] * 5
        (root / "r6" / "state.json").write_text(json.dumps(zustand6), encoding="utf-8")
        # Board 2 (Testkurs B): nur vor der Loeschung. Board 3 (Andere Strecke A): nur danach.
        lauf(root, "r4", "Testkurs", "B", "2026-10-02T12:00:00+02:00", board_laeufe(rng, 70.0, autos, 300))
        lauf(root, "r5", "Andere Strecke", "A", "2026-11-06T12:00:00+01:00", board_laeufe(rng, 80.0, autos, 250))
        # Board 4 (Kollision C): vor und nach der Loeschung auf jedem Platz dieselbe Zeit, aber ein
        # anderes Auto. Bis 2026-11-10 fielen solche Zeilen zu einer zusammen (das alte Auto, mit
        # dem Tag der neuen) -- jetzt bleiben es zwei Runden.
        kollision = [round(60.0 + 0.1 * i, 3) for i in range(30)]
        lauf(root, "r7", "Kollision", "C", "2026-10-03T12:00:00+02:00",
             [zeile(i + 1, t, 101) for i, t in enumerate(kollision)])
        lauf(root, "r8", "Kollision", "C", "2026-11-07T12:00:00+01:00",
             [zeile(i + 1, t, 102) for i, t in enumerate(kollision)])

        d = bauen(root, leer)
        voll = bauen(root, leer, unbegrenzt=True)
        flags = d["flags"]
        print("Datensatz: %d Boards, %d Runden (unbegrenzt %d)"
              % (len(d["boards"]), d["meta"]["kept_laps"], voll["meta"]["kept_laps"]))

        def brett(dd: dict, strecke: str, klasse: str) -> dict:
            return next(b for b in dd["boards"]
                        if dd["tracks"][b["t"]] == strecke and dd["classes"][b["k"]] == klasse)

        a = brett(d, "Testkurs", "A")
        pruefe(a.get("days") == ["2026-10-01", "2026-10-20", "2026-11-05", "2026-11-08"]
               and all(k in a for k in ("lday", "gdays", "dstat", "ddist", "isz", "xgrp", "xms", "xrank", "xday")),
               "Board ueber mehrere Tage traegt days, lday, gdays, dstat, ddist, isz, x... (%s, %d Zusatzrunden)"
               % (a.get("days"), len(a.get("xgrp") or [])))
        einzeln = brett(d, "Andere Strecke", "A")
        pruefe(einzeln.get("days") == ["2026-11-06"] and "lday" not in einzeln and "gdays" not in einzeln,
               "ein Board mit einem Tag traegt nur days")
        pruefe(d["meta"].get("since_default") == stichtag.stichtag() or d["meta"].get("since_default") == "",
               "meta.since_default ist der Stichtag des Servers (%r)" % d["meta"].get("since_default"))

        # --- alte Leser: dieselben fuenf schnellsten je Gruppe, dieselben Zaehlungen
        for b, bv in zip(d["boards"], voll["boards"]):
            ga, gv = gruppen(b, flags), gruppen(bv, flags)
            # Genau die fuenf: die Runden fuer Sichten ab einem Stichtag stehen in x..., nie hier.
            gleich = all(ga[k] == gv[k][:bau.LAPS_PER_GROUP] for k in gv) and set(ga) == set(gv)
            name = "%s %s" % (d["tracks"][b["t"]], d["classes"][b["k"]])
            pruefe(gleich and zaehlung(b) == zaehlung(bv)
                   and all(b[k] == bv[k] for k in ("rows", "valid", "invalid", "maxRank", "dist", "impl")),
                   "alte Leser: %s hat ueber alles dieselben fuenf je Gruppe und Zaehlungen" % name)

        # --- keine Runde ueber die Loeschung hinweg zusammengelegt
        k = brett(d, "Kollision", "C")
        autos_k = {d["carIds"][c]: n for c, n in zip(k["gcar"], k["gcount"])}
        k_ab = brett(stichtag.zuschneiden(d, "2026-11-03"), "Kollision", "C")
        pruefe(k["valid"] == 60 and autos_k == {101: 30, 102: 30}
               and {d["carIds"][c] for c in k_ab["gcar"]} == {102} and k_ab["valid"] == 30,
               "gleicher Rang und Zeit vor und nach der Loeschung bleiben zwei Runden (%s, ab dem Stichtag %d)"
               % (autos_k, k_ab["valid"]))

        # --- die Sicht ist exakt, fuer jeden Stichtag
        for seit in ("2026-09-01", "2026-10-01", "2026-10-02", "2026-10-20", "2026-11-03", "2026-11-05",
                     "2026-11-06", "2026-12-01"):
            sicht, sicht_voll = stichtag.zuschneiden(d, seit), stichtag.zuschneiden(voll, seit)
            pruefe(len(sicht["boards"]) == len(sicht_voll["boards"]),
                   "%s: %d Boards in beiden Bauten" % (seit, len(sicht["boards"])))
            for b, bv in zip(sicht["boards"], sicht_voll["boards"]):
                ga, gv = gruppen(b, flags), gruppen(bv, flags)
                fuenf = all(ga.get(k, []) == gv[k][:bau.LAPS_PER_GROUP] for k in gv) and set(ga) == set(gv)
                zahlen = all(b.get(k) == bv.get(k) for k in ("rows", "valid", "invalid", "maxRank", "dist", "impl", "iest"))
                name = "%s %s" % (d["tracks"][b["t"]], d["classes"][b["k"]])
                pruefe(fuenf and zahlen and zaehlung(b) == zaehlung(bv),
                       "%s: %s -- die fuenf schnellsten je Gruppe ab dem Tag und alle Zaehlungen" % (seit, name))
            pruefe(all(s["when"][:10] >= seit for s in sicht["meta"]["scans"]),
                   "%s: meta.scans nur ab dem Stichtag (%d)" % (seit, len(sicht["meta"]["scans"])))

        # Die Sicht ab dem Stichtag gegen die Laeufe selbst: nur Board 1 (Lauf 3) und Board 3.
        ab = stichtag.zuschneiden(d, "2026-11-03")
        namen = sorted("%s %s" % (d["tracks"][b["t"]], d["classes"][b["k"]]) for b in ab["boards"])
        pruefe(namen == ["Andere Strecke A", "Testkurs A"], "ab 2026-11-03: nur die Boards mit neuen Scans (%s)" % namen)
        a_ab = brett(ab, "Testkurs", "A")
        neu_gueltig = [z for z in neu + neu2 if z["is_clean"]]
        doppelt = len(neu_gueltig) - len({(z["rank"], z["lap_time_seconds"]) for z in neu_gueltig})
        pruefe(a_ab["valid"] == len(neu_gueltig) - doppelt and a_ab["maxRank"] == max(z["rank"] for z in neu_gueltig)
               and a_ab["impl"] == 4000,
               "ab 2026-11-03: Testkurs A zaehlt genau Lauf 3 und 6, beide ganz (%d gueltig von %d, Tiefe %d, Groesse %d)"
               % (a_ab["valid"], len(neu_gueltig) - doppelt, a_ab["maxRank"], a_ab["impl"]))
        alle_ms = {round(z["lap_time_seconds"] * 1000) for z in neu + neu2}
        pruefe(all(m in alle_ms for m in a_ab["lms"]), "ab 2026-11-03: jede Runde stammt aus Lauf 3 oder 6")
        a_spaet = brett(stichtag.zuschneiden(d, "2026-11-06"), "Testkurs", "A")
        pruefe(a_spaet["valid"] == sum(1 for z in neu2 if z["is_clean"]),
               "ab 2026-11-06: Testkurs A zaehlt genau Lauf 6 (%d)" % a_spaet["valid"])
        pruefe(stichtag.zuschneiden(d, None) is d, "Stichtag None: der Datensatz selbst")
        alt_format = {"boards": [{k: v for k, v in a.items() if k not in ("days", "lday", "gdays", "dstat", "ddist", "isz")}]}
        pruefe(stichtag.zuschneiden(alt_format, "2026-11-03")["boards"] == [],
               "ein Board aus dem alten Format (ohne days) gilt als alt")

        # --- die Seite rechnet genauso (node)
        if shutil.which("node"):
            datei = ordner / "d.json"
            datei.write_text(json.dumps(d), encoding="utf-8")
            tage = ["2026-09-01", "2026-10-02", "2026-10-20", "2026-11-03", "2026-11-06", "2026-12-01", ""]
            probe = (WORKSPACE / "scripts" / "dataset_cutoff.js").read_text(encoding="utf-8") + """
const D = JSON.parse(require('fs').readFileSync(%s, 'utf8'));
const out = {};
%s.forEach(s => { out[s] = datasetSince(D, s || null); });
out.laps = lapsSince([{received: "2026-11-02T23:59:59+00:00"}, {received: "2026-11-03T00:00:01+00:00"}, {}], "2026-11-03").length;
out.default = defaultSince(D);
process.stdout.write(JSON.stringify(out));
""" % (json.dumps(str(datei)), json.dumps(tage))
            lauf_js = subprocess.run(["node", "-e", probe], capture_output=True, text=True, encoding="utf-8")
            if lauf_js.returncode != 0:
                pruefe(False, "node: " + lauf_js.stderr[-400:])
            else:
                js = json.loads(lauf_js.stdout)
                for s in tage:
                    py = json.loads(json.dumps(stichtag.zuschneiden(d, s or None)))
                    pruefe(js[s] == py, "Seite = Server fuer Stichtag %r" % (s or "alle"))
                pruefe(js["laps"] == 1, "Seite: eingereichte Runden nur ab dem Stichtag")
                pruefe(js["default"] == (d["meta"]["since_default"] or None),
                       "Seite: die Vorgabe kommt aus meta.since_default")
        else:
            print("  skip  kein node -- Seite nicht verglichen")

        # --- der Server
        laps_json = ordner / "laps.json"
        laps_json.write_text(json.dumps(d), encoding="utf-8")
        import scan_needs
        import scan_submissions as scans
        import leaderboard_check as lbc
        import race_submissions as rennen

        liste = scan_needs.liste(laps_json, [], {}, now=time.time(), seit="2026-11-03")
        je = {(e["track"], e["class"]): e for e in liste}
        b_alt, a_neu = je.get(("Testkurs", "B")), je.get(("Testkurs", "A"))
        pruefe(b_alt is not None and b_alt["lastScan"] is None and b_alt["scans"] == 0 and b_alt["depth"] == 0,
               "most needed: ein Board, dessen juengster Scan vor dem Stichtag liegt, gilt als nie gescannt")
        pruefe(a_neu is not None and a_neu["lastScan"] and a_neu["scans"] == 2
               and liste.index(b_alt) < liste.index(a_neu),
               "most needed: ein Board mit neuem Scan zaehlt nur diesen und steht hinter dem alten")
        liste_alle = scan_needs.liste(laps_json, [], {}, now=time.time(), seit=None)
        pruefe(next(e for e in liste_alle if (e["track"], e["class"]) == ("Testkurs", "B"))["lastScan"],
               "most needed: mit allen Daten hat das alte Board seinen Scan")
        frisch = [{"track": "Testkurs", "class": "B", "received": time.time()}]
        nach = {(e["track"], e["class"]): e for e in scan_needs.liste(laps_json, frisch, {}, now=time.time(), seit="2026-11-03")}
        alt_eingang = [{"track": "Testkurs", "class": "B", "received": 1761900000}]  # 2025-10-31
        vor = {(e["track"], e["class"]): e for e in scan_needs.liste(laps_json, alt_eingang, {}, now=time.time(), seit="2026-11-03")}
        pruefe(nach[("Testkurs", "B")]["lastScan"] and vor[("Testkurs", "B")]["lastScan"] is None,
               "most needed: ein angenommener Scan zaehlt nur ab dem Stichtag")

        bretter = scans.bretter_zeiten(laps_json, "2026-11-03")
        a_tor = scans.brett_von(bretter, {"track": "Testkurs", "performance_class": "A", "rivals_mode": "Road Racing"})
        b_tor = scans.brett_von(bretter, {"track": "Testkurs", "performance_class": "B", "rivals_mode": "Road Racing"})
        # Die Spitzenzeiten: die "current" (Lauf 6, der juengste), die Tiefe ueber beide neuen Laeufe.
        neu_spitze = sorted(round(z["lap_time_seconds"] * 1000) for z in neu2 if z["is_clean"])[:20]
        pruefe(b_tor is None, "Tor: ein Board ohne Zeile ab dem Stichtag ist neu")
        pruefe(a_tor is not None and list(a_tor) == neu_spitze
               and a_tor.tiefe == max(z["rank"] for z in neu + neu2 if z["is_clean"]),
               "Tor: die Spitzenzeiten und die Tiefe kommen nur aus den Scans nach dem Stichtag")
        alle_bretter = scans.bretter_zeiten(laps_json, None)
        pruefe(scans.brett_von(alle_bretter, {"track": "Testkurs", "performance_class": "B", "rivals_mode": "Road Racing"}),
               "Tor: mit allen Daten ist das alte Board bekannt")
        # Ein neuer Scan von Testkurs B nach der Loeschung: gegen das alte Board waere er
        # "times do not match" -- jetzt gilt er als neues Board.
        zustand = {"track": "Testkurs", "performance_class": "B", "rivals_mode": "Road Racing",
                   "end_status": "end_detected", "rows_collected": 3500, "seconds": 300, "readings": 7000}
        zeilen = [{"rank": i + 1, "lap_time_seconds": 90.0 + i * 0.01, "is_clean": True} for i in range(3500)]
        grund_ab = scans.freigabe(zustand, zeilen, bretter)
        grund_alle = scans.freigabe(zustand, zeilen, alle_bretter)
        pruefe(scans.GRUND_ZEITEN not in grund_ab and scans.GRUND_ZEITEN in grund_alle,
               "Tor: ein Scan nach der Loeschung wird nicht an alten Zeiten gemessen (%r / %r)" % (grund_ab, grund_alle))

        auto = d["carIds"][a["gcar"][a["lgrp"][0]]]
        beste_alt = min(a["lms"][i] for i in range(len(a["lgrp"])) if d["carIds"][a["gcar"][a["lgrp"][i]]] == auto
                        and a["gsig"][a["lgrp"][i]] & 1)
        runde = {"lapSeconds": beste_alt / 1000 + 0.5, "track": "Testkurs", "carClass": 3, "carOrdinal": auto}
        try:
            lbc.pruefe(runde, laps_json, [], seit=None)
            pruefe(False, "Einreichen: mit allen Daten haette die langsamere Runde abgewiesen werden muessen")
        except lbc.NichtSchneller as e:
            pruefe(e.status == 409, "Einreichen: mit allen Daten gilt die alte Bestzeit (%d)" % e.status)
        try:
            befund = lbc.pruefe(runde, laps_json, [], seit="2026-11-03")
            neu_beste = [round(z["lap_time_seconds"] * 1000) for z in neu + neu2 if z["car_id"] == auto and z["is_clean"]]
            pruefe(not neu_beste or befund["rivalsMs"] == min(neu_beste),
                   "Einreichen: ab dem Stichtag zaehlt nur die neue Bestzeit (%s)" % befund["rivalsMs"])
        except lbc.NichtSchneller as e:
            pruefe(False, "Einreichen: ab dem Stichtag abgewiesen: %s" % e.message)
        try:
            lbc.pruefe(dict(runde, track="Testkurs", carClass=2), laps_json, [], seit="2026-11-03")
            pruefe(False, "Einreichen: ein Board ohne neuen Scan haette fehlen muessen")
        except lbc.NichtSchneller as e:
            pruefe(e.status == 422, "Einreichen: ein Board ohne Scan ab dem Stichtag gibt es noch nicht (%d)" % e.status)
        frueher = [{"lap": {"track": "Testkurs", "carClass": 3, "carOrdinal": auto, "lapSeconds": 1.0},
                    "received": "2026-10-10T10:00:00+00:00"}]
        try:
            lbc.pruefe(dict(runde, lapSeconds=10.0), laps_json, frueher, seit="2026-11-03")
            pruefe(True, "Einreichen: eine Einreichung von vor dem Stichtag haelt nichts auf")
        except lbc.NichtSchneller as e:
            pruefe(False, "Einreichen: von einer alten Einreichung aufgehalten: %s" % e.message)

        rennen_ordner = ordner / "races"
        rennen_ordner.mkdir()
        for nr, (eingang, ms) in enumerate(((1761900000, 70000), (time.time(), 75000))):
            (rennen_ordner / ("r%d.json" % nr)).write_text(json.dumps({
                "id": "r%d" % nr, "mode": "horizon-play", "track": "Testkurs", "class": "A", "received": eingang,
                "field": [{"kind": "human", "place": 1, "car": 101, "pi": 650, "ms": ms}]}), encoding="utf-8")
        alle_hp = rennen.hp_bretter(rennen_ordner)
        ab_hp = rennen.hp_bretter(rennen_ordner, "2026-11-03")
        pruefe(len(alle_hp) == 1 and alle_hp[0]["n"] == 2 and len(ab_hp) == 1 and ab_hp[0]["n"] == 1 and ab_hp[0]["ms"] == 75000,
               "Horizon Play: ab dem Stichtag nur die neuen Rennen (%s / %s)"
               % ([(x["n"], x["ms"]) for x in alle_hp], [(x["n"], x["ms"]) for x in ab_hp]))

        pruefe(stichtag.lesen("all") is None and stichtag.lesen("") is None and stichtag.lesen("2026-11-03") == "2026-11-03"
               and stichtag.lesen("unsinn") == stichtag.VORGABE and stichtag.lesen("2026-02-30") == stichtag.VORGABE,
               "Einstellung: all, leer, ein Tag, Unsinn")

    print("\n%s" % ("FEHLGESCHLAGEN: %d" % fehler if fehler else "alles bestanden"))
    return 1 if fehler else 0


if __name__ == "__main__":
    raise SystemExit(main())
