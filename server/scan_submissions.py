"""Bestenlisten-Scans aus der App -- angenommen wie ein Fremdbeitrag, ausgewiesen wie eine Runde.

## Was ankommt

Die App liest im Reiter "Scan" eine Rivals-Bestenliste vom Schirm (Scan/BoardScanner.cs)
und legt einen Lauf ab: state.json + rows.jsonl, dasselbe Format wie das alte
Python-Werkzeug. Diesen Lauf schickt sie an `POST /api/scan/submit`:

    {"state": {...state.json...}, "rows": "<der Text von rows.jsonl>"}

Unterschrieben wie eine Runde (lap_submissions.verify, eigener Pfad) -- dieselbe
Anmeldung, dasselbe Abschalten (submit_laps). KEINE Gamertags: der Scanner liest sie
nicht, und was hier ankommt, wird auf eine feste Liste von Feldern beschnitten
(ZEILEN_FELDER). Ein Feld namens "gamertag" kommt so gar nicht erst auf die Platte.

## Wohin

Genau dorthin, wo `import_contrib.py` die Laeufe der Freunde ablegt:

    data/memory_scans/contrib/app-<kurz>/app-<kurz>_<run_id>/state.json
                                                             rows.jsonl
                                                             leaderboard_entries.parquet

`<kurz>` sind zehn Hex-Zeichen aus dem Hash der Installationskennung -- die Kennung
selbst steht nicht im Ordnernamen. Der Lauf heisst auf dem Server "app-<kurz>_<run_id>"
(gespeicherter_name): die Sichtbarkeitsliste kennt nur den Namen, und lokale Laeufe
heissen wie die der App. `deploy_gnas.py` holt diesen Baum (HOLEN_BAEUME)
auf den Rechner, auf dem der Datensatz gebaut wird, und `build_analytics_dataset.py`
liest `contrib/*/*/state.json` -- ein App-Scan erreicht den Datensatz damit auf genau
demselben Weg wie ein Fremdbeitrag, und er wird wie einer ausgeblendet
(config/dataset_visibility.json, im Admin-Bereich).

Daneben ein Eingangsbuch (data/submissions/scans/<kennung>.json) mit der
Installationskennung: fuer das Tageskontingent und um eine Installation sperren zu
koennen. Es bleibt auf dem Server; der contrib-Baum, der weiterreist, traegt sie nicht.

## Was geprueft wird

Die Regeln der Fremdbeitraege (contrib_format.validate_rows / validate_state), nicht
eine Kopie davon. Dazu: eine bekannte Klasse, Zustandsfelder mit dem erwarteten Typ
(der Datensatzbau rechnet mit ihnen), und Zaehlungen (Zeilen, hoechster Platz) aus den
Zeilen selbst statt aus dem, was die App behauptet. Merkwuerdiges -- Zeiten, die mit dem
Platz fallen, ein Board, das der Datensatz nicht kennt -- wird angenommen und markiert,
damit ein Mensch es ansieht.

## Das Tor (seit 2026-10-04)

Eine App meldet sich ohne Passwort an. Was sie schickt, wird darum zwar immer abgelegt,
geht aber nur dann von selbst in den Datensatz, wenn der Admin schon VERTRAUEN_AB Laeufe
dieser Installation eingeblendet hat, das Board dort schon steht, seine Spitzenzeiten zu
denen des Datensatzes passen und es mindestens UNBEWIESEN_ZEILEN Zeilen hat (freigabe()).
Sonst steht der Lauf vom ersten Augenblick an auf der Sichtbarkeitsliste, mit dem Grund
("unknown board", "times do not match", ...), und der Admin blendet ihn ein.

Derselbe Lauf (run_id) zweimal: abgelehnt. Ein Lauf wird nie ueberschrieben.
"""

from __future__ import annotations

import bisect
import hashlib
import json
import math
import os
import re
import shutil
import threading
import time
from datetime import datetime, timezone
from pathlib import Path

import contrib_format as fmt
import import_contrib
import lap_submissions as laps

WORKSPACE = Path(__file__).resolve().parent.parent
CONTRIB_ROOT = import_contrib.CONTRIB_ROOT
SCANS_DIR = WORKSPACE / "data" / "submissions" / "scans"

# Ein Board hat bis zu rund 20.000 Zeilen zu je ~450 Byte; contrib_format laesst
# 60.000 zu. 32 MB lassen dafuer Luft und bleiben unter der Lesegrenze des Servers.
MAX_BODY = 32 * 1024 * 1024
# Ein Board von 20.000 Zeilen braucht in der App rund 40 Minuten -- mehr als
# 40 Laeufe am Tag schafft niemand, der wirklich scannt.
LAEUFE_JE_INSTALL_TAG = 40
KLASSEN = ("D", "C", "B", "A", "S1", "S2", "R", "X")
PI_GRENZEN = {"D": (99, 400), "C": (400, 500), "B": (500, 600), "A": (600, 700),
              "S1": (700, 800), "S2": (800, 900), "R": (900, 998)}
_KENNUNG = re.compile(r"^[0-9a-f]{20}$")
_SCHLOSS = threading.Lock()

# WAS VON EINER ZEILE AUF DIE PLATTE DARF, mit Typ. Alles andere faellt weg -- auch ein
# Gamertag, falls je einer mitkaeme. Die Namen sind die der beiden Scanner (App und
# altes Werkzeug) und die, die build_analytics_dataset.COLUMNS liest. Die App filtert
# mit derselben Liste (Scan/ScanUpload.cs, Felder); wer hier eins ergaenzt, dort auch.
ZEILEN_FELDER = {
    "rank": int, "lap_time_seconds": float, "pi": int, "car_id": int,
    "car_short_id": int, "car_name": str, "car_codename": str, "car_full_name": str,
    "drivetrain": str, "gearbox": str, "source_frame": str, "row_index": int,
    "rank_anchored": bool, "line": str, "readings": int, "agreement": int,
    "pi_agreement": int, "scanner": str,
    "is_clean": bool, "used_tcs": bool, "used_abs": bool, "used_stm": bool,
    "used_friction_assist": bool, "used_auto_brake": bool, "used_auto_shifting": bool,
    "used_clutch": bool, "used_super_easy_assist": bool,
}

# Dasselbe fuer state.json. Die Zaehlungen (rows_collected usw.) stehen hier NICHT:
# sie werden aus den Zeilen neu gerechnet.
ZUSTAND_FELDER = {
    "run_id": str, "track": str, "performance_class": str, "rivals_mode": str,
    "route_index": int, "end_status": str, "end_rejections": int, "row_cap": int,
    "implied_total": int, "length_estimator": str, "status": str, "scanner": str,
    "scanner_tool": str, "readings": int, "seconds": float, "completed_at": str,
}


def wer_von(install_id: str) -> str:
    """Der Ordnername unter contrib/ fuer diese Installation: app-<10 Hex>."""
    return "app-" + hashlib.sha256(install_id.encode("utf-8")).hexdigest()[:10]


def kennung(install_id: str, run_id: str) -> str:
    return hashlib.sha256(f"{install_id}|{run_id}".encode("utf-8")).hexdigest()[:20]


def _wert(wert, art):
    """Ein Wert in genau dem erwarteten Typ -- oder None. Nie etwas Verschachteltes."""
    if wert is None:
        return None
    if art is bool:
        return wert if isinstance(wert, bool) else None
    if isinstance(wert, bool):
        return None
    if art is int:
        if isinstance(wert, (int, float)) and math.isfinite(wert) and float(wert).is_integer() \
                and abs(wert) < 1e12:
            return int(wert)
        return None
    if art is float:
        if isinstance(wert, (int, float)) and math.isfinite(wert):
            return float(wert)
        return None
    if isinstance(wert, str):
        t = "".join(c for c in wert if c >= " " and c != "\x7f").strip()[:160]
        return t or None
    return None


def sauber(roh: dict, felder: dict) -> dict:
    raus = {}
    for name, art in felder.items():
        w = _wert(roh.get(name), art)
        if w is not None:
            raus[name] = w
    return raus


def _zeitpunkt(text):
    """Ein ISO-Zeitpunkt als Sekunden seit 1970 -- oder None, wenn er nicht lesbar ist."""
    try:
        t = datetime.fromisoformat(str(text).strip())
    except (TypeError, ValueError):
        return None
    if t.tzinfo is None:
        t = t.astimezone()
    return t.timestamp()


# Windows-Geraetenamen: ein Ordner "CON" oder "nul.x" laesst sich dort nicht anlegen.
_GERAETE = ({"CON", "PRN", "AUX", "NUL"} | {"COM%d" % i for i in range(1, 10)}
            | {"LPT%d" % i for i in range(1, 10)})


def _windows_tauglich(run_id: str) -> None:
    """Der Name muss auch als Ordner unter Windows derselbe bleiben.

    Gebaut wird der Datensatz auf dem Windows-Rechner, und ausgeblendet wird dort nach
    dem ORDNERNAMEN (build_analytics_dataset.discover_runs). Windows streicht Punkte am
    Ende eines Ordnernamens: "abc." landet als "abc" -- und der Eintrag "abc." auf der
    Sichtbarkeitsliste trifft ihn dann nicht mehr. Ein zurueckgehaltener Lauf stuende
    so doch auf der Seite. Die App benennt nie so; abgelehnt wird es darum hart.
    """
    # fullmatch: RUN_ID_RE endet auf "$", und das laesst ein "\n" am Ende durch -- "abc.\n"
    # kaeme an der Punktpruefung vorbei und wuerde danach (sauber()) zu "abc." gestutzt.
    if (not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,119}", run_id) or run_id.endswith(".")
            or run_id.split(".")[0].upper() in _GERAETE):
        raise fmt.ContribError("unusable run name on Windows: %r" % run_id)


# Groesser ist kein Board: validate_rows laesst Raenge bis 10 Millionen zu.
MAX_BOARD_LAENGE = 10_000_000


def pruefe_lauf(state, rows_text, bekannte_bretter: set | dict | None = None,
                now: float | None = None) -> tuple[dict, list, list]:
    """Den eingereichten Lauf pruefen. Gibt (Zustand, Zeilen, Auffaelligkeiten) zurueck.

    Wirft SubmitError(400) bei allem, was einen Fremdbeitrag auch scheitern liesse.
    `bekannte_bretter` sind die (Strecke gefaltet, Klasse) des Datensatzes -- oder das
    Ergebnis von bretter_zeiten(), dann zaehlt auch die Kategorie. Fehlt
    das Board dort, wird markiert, nicht abgelehnt: es kann ein wirklich neues sein.
    """
    if not isinstance(state, dict):
        raise laps.SubmitError(400, "'state' must be an object (the run's state.json).")
    if not isinstance(rows_text, str) or not rows_text.strip():
        raise laps.SubmitError(400, "'rows' must be the text of rows.jsonl.")
    try:
        run_id = fmt.check_run_id(str(state.get("run_id") or ""))
        _windows_tauglich(run_id)
        fmt.validate_state(state, run_id)
        roh = fmt.validate_rows(rows_text, run_id)
    except fmt.ContribError as e:
        raise laps.SubmitError(400, str(e)) from None

    zustand = sauber(state, ZUSTAND_FELDER)
    klasse = str(zustand.get("performance_class") or "").strip().upper()
    if klasse not in KLASSEN:
        raise laps.SubmitError(400, "'performance_class' must be one of %s." % ", ".join(KLASSEN))
    zustand["performance_class"] = klasse
    for pflicht in fmt.REQUIRED_STATE_KEYS:
        if not zustand.get(pflicht):
            raise laps.SubmitError(400, "%s: state.json without a usable %s" % (run_id, pflicht))

    zeilen = []
    for r in roh:
        z = sauber(r, ZEILEN_FELDER)
        # Rang und Zeit hat validate_rows schon als Zahlen geprueft -- auch wenn sie als
        # Text kamen. Sie gehen in genau dieser Form weiter, nie als Text.
        z["rank"], z["lap_time_seconds"] = int(r["rank"]), float(r["lap_time_seconds"])
        zeilen.append(z)
    zeilen.sort(key=lambda z: z["rank"])
    raenge = [z["rank"] for z in zeilen]
    zustand.update({
        "rows_collected": len(zeilen),
        "minimum_rank": raenge[0],
        "maximum_rank": raenge[-1],
        "missing_ranks": raenge[-1] - raenge[0] + 1 - len(raenge),
        "gapless": raenge[-1] - raenge[0] + 1 == len(raenge),
    })
    punkte = state.get("thumb_points")
    if isinstance(punkte, list):
        zustand["thumb_points"] = [
            p for p in (sauber(x, {"rank": int, "position": float}) for x in punkte[:5000]
                        if isinstance(x, dict))
            if len(p) == 2]
    # DIE ZWEI ANGABEN, DIE AUF DER SEITE GEWINNEN, nicht blind uebernehmen. Der Datensatzbau
    # nimmt den Lauf mit dem juengsten completed_at als "current" fuer sein Board und die
    # groesste implied_total-Messung als Boardgroesse. Ein Zeitpunkt in der Zukunft hielte
    # einen Lauf fuer immer aktuell -- dann gilt die Empfangszeit. Der Bau vergleicht die
    # Zeitpunkte als Zeitpunkte (build_analytics_dataset.zeitpunkt), nicht als Text: dieser
    # Server laeuft in UTC und schreibt "+00:00", lokale Laeufe tragen "+02:00".
    # Eine Laenge jenseits jedes Boards faellt weg. Die Boardgroesse nimmt der Bau aus einem
    # App-Lauf nur, wenn kein anderer Lauf sie misst.
    jetzt = now or time.time()
    behauptet = _zeitpunkt(zustand.get("completed_at"))
    if behauptet is None or behauptet > jetzt + 3600:
        behauptet = jetzt
    zustand["completed_at"] = datetime.fromtimestamp(behauptet, timezone.utc).astimezone() \
        .isoformat(timespec="microseconds")
    gesamt = zustand.get("implied_total")
    if not isinstance(gesamt, int) or not 0 < gesamt <= MAX_BOARD_LAENGE:
        zustand.pop("implied_total", None)
    if isinstance(zustand.get("implied_total"), int) and zustand["implied_total"] > 0:
        zustand["row_coverage"] = round(len(zeilen) / zustand["implied_total"], 4)
    zustand["source"] = "app"

    auffaellig = []
    # Gueltige Runden kommen nach Platz in steigender Zeit. Der Scanner wirft schon
    # selbst weg, was die Folge bricht -- bleibt doch etwas, ist es merkwuerdig.
    sauber_zeiten = [z["lap_time_seconds"] for z in zeilen if z.get("is_clean") is True]
    if any(b < a for a, b in zip(sauber_zeiten, sauber_zeiten[1:])):
        auffaellig.append("lap times fall as the rank rises")
    grenze = PI_GRENZEN.get(klasse)
    if grenze and any(not grenze[0] < z["pi"] <= grenze[1] for z in zeilen if "pi" in z):
        auffaellig.append("PI outside the class")
    if bekannte_bretter is not None:
        strecke = " ".join(str(zustand["track"]).strip().lower().split())
        if (not brett_von(bekannte_bretter, zustand) if isinstance(bekannte_bretter, dict)
                else (strecke, klasse) not in bekannte_bretter):
            auffaellig.append("board not in the dataset yet")
        elif isinstance(bekannte_bretter, dict) and viel_schneller(brett_von(bekannte_bretter, zustand), zeilen):
            auffaellig.append("best lap more than %d%% under the dataset's" % round(PRUEF_SCHNELLER * 100))
    return zustand, zeilen, auffaellig


# --------------------------------------------------------------------------- #
# DAS TOR (seit 2026-10-04): was eine App einreicht, erreicht den Datensatz von
# selbst nur, wenn es zu einem Board passt, das der Datensatz schon kennt.
# --------------------------------------------------------------------------- #
#
# Eine Installation meldet sich ohne Passwort an -- anders als ein Freund, dem ein
# Schluessel gegeben wurde. Ein Lauf, den niemand angesehen hat, darf darum nicht von
# selbst auf der Seite stehen. Abgelegt wird er immer (die Arbeit ist getan, und der
# Admin soll sie sehen koennen), aber ausgeblendet, sobald eine dieser Pruefungen
# nicht haelt:
#
#   * DAS BOARD IST BEKANNT: Kategorie + Strecke + Klasse stehen im gebauten
#     Datensatz (data/analytics/laps.json -- dieselbe Datei, die /api/dataset
#     ausliefert und gegen die leaderboard_check die Runden prueft).
#   * DIE ZEITEN STIMMEN: von den 20 schnellsten gueltigen Zeiten des Datensatzes fuer
#     dieses Board steht mindestens die Haelfte auch im Lauf (auf 1 ms genau).
#     Verglichen wird der Wert, nicht der Platz -- wer sich verbessert, schiebt alle
#     unter sich einen Platz tiefer, ihre Zeiten bleiben dieselben.
#     "Die Bestzeit ist hoechstens 2 % schneller als die des Datensatzes" ist KEIN
#     Freibrief: es liesse jede erfundene Liste durch, die nur langsam genug ist. Und
#     als Sperre taugt es nicht: der Datensatz laesst Zeilen weg, deren Auto er nicht
#     zuordnen kann. Am echten App-Scan von Festival Chase A (2026-10-04) fehlten ihm
#     so die Plaetze 1-3; seine "Bestzeit" 127,578 s war Platz 4, der Scan las
#     124,833 s -- 2,2 % schneller und richtig, 18 der 20 Spitzenzeiten trafen.
#     Darum nur eine Markierung fuer den Admin (pruefe_lauf), kein Grund zum Ausblenden.
#     Bevorzugt die Zeiten, die beim letzten Scan auf der Liste standen ("current"):
#     der Datensatz behaelt auch verdraengte Zeiten, und die stehen auf keinem
#     neuen Scan mehr.
#   * DAS ENDE IST BEWIESEN -- wie beim alten Werkzeug (ocr_board_sweep.py): ein
#     Board mit unbewiesenem Ende und weniger als 500 Zeilen oder weniger als 80 %
#     der geschaetzten Laenge ist ein Abbruch, kein Board. Ein Dienstausfall
#     waehrend des Lesens ("server_error") zaehlt nie.
#
# Kann der Datensatz nicht gelesen werden, wird ausgeblendet -- im Zweifel nicht auf
# die Seite. Der Admin blendet ein ("Show"), was er fuer gut befindet.

PRUEF_SPITZE = 20
PRUEF_TOLERANZ_MS = 1
PRUEF_ANTEIL = 0.5
PRUEF_SCHNELLER = 0.02
# Wie ocr_board_sweep.SUSPECT_ROW_FLOOR und die 80 % dort.
UNBEWIESEN_ZEILEN = 500
UNBEWIESEN_ANTEIL = 0.80
# Enden, die das Board als gelesen beweisen (oder bewusst begrenzen). Alles andere --
# "stopped" (Stop, Pause, Fenster gewechselt), "game_crashed", "truncated",
# "end_unverified", "capture_failed", "chunk_budget_spent" -- ist unbewiesen.
BEWIESENE_ENDEN = {"end_detected", "invalid_tail", "valid_section_complete", "row_cap_reached"}

GRUND_UNBEKANNT = "unknown board"
GRUND_ZEITEN = "times do not match"
GRUND_KEIN_DATENSATZ = "dataset not readable"
GRUND_DIENST = "server error during the scan"
# Die ersten Laeufe einer Installation sieht immer ein Mensch an (freigegeben()).
VERTRAUEN_AB = 2
GRUND_NEU = "new installation: its first scans are reviewed"

_ZEITEN_SCHLOSS = threading.Lock()
_ZEITEN: dict = {"schluessel": None, "bretter": {}}


def _falte(name) -> str:
    return " ".join(str(name or "").strip().lower().split())


def bretter_zeiten(dataset_file: Path) -> dict:
    """(Kategorie, Strecke, Klasse) gefaltet -> die schnellsten gueltigen ms, aufsteigend.

    Hoechstens PRUEF_SPITZE je Board; die "current" statt aller, wenn das Board welche
    hat. Ein Board ohne Kategorie (aeltere Datensaetze) steht unter Kategorie "".
    Wirft, wenn die Datei fehlt oder nicht die erwartete Form hat. Gemerkt je Stand.
    """
    st = dataset_file.stat()
    schluessel = (str(dataset_file), st.st_size, st.st_mtime_ns)
    with _ZEITEN_SCHLOSS:
        if _ZEITEN["schluessel"] == schluessel:
            return _ZEITEN["bretter"]
        d = json.loads(dataset_file.read_text(encoding="utf-8"))
        flags = d.get("flags") or []
        sauber = 1 << flags.index("clean") if "clean" in flags else 0
        aktuell = 1 << flags.index("current") if "current" in flags else 0
        kategorien = d.get("categories") or []
        tracks, classes = d["tracks"], d["classes"]
        bretter: dict = {}
        for b in d["boards"]:
            try:
                c = b.get("c")
                modus = _falte(kategorien[c]) if isinstance(c, int) and 0 <= c < len(kategorien) else ""
                key = (modus, _falte(tracks[b["t"]]), str(classes[b["k"]]).upper())
            except (IndexError, KeyError, TypeError, AttributeError):
                continue
            gsig, lgrp, lms = b.get("gsig") or [], b.get("lgrp") or [], b.get("lms") or []
            alle, jetzt = [], []
            for g, ms in zip(lgrp, lms):
                if not isinstance(g, int) or not 0 <= g < len(gsig) or not isinstance(ms, (int, float)) \
                        or isinstance(ms, bool) or not isinstance(gsig[g], int):
                    continue
                sig = gsig[g]
                if (sig & sauber) != sauber:
                    continue
                alle.append(int(ms))
                if aktuell and sig & aktuell:
                    jetzt.append(int(ms))
            zeiten = sorted(jetzt or alle)[:PRUEF_SPITZE]
            # Dasselbe Board zweimal (sollte es nicht geben): die Vereinigung.
            if key in bretter:
                zeiten = sorted(bretter[key] + zeiten)[:PRUEF_SPITZE]
            bretter[key] = zeiten
        _ZEITEN.update({"schluessel": schluessel, "bretter": bretter})
        return bretter


def lade_bretter(dataset_file: Path) -> dict | None:
    """bretter_zeiten -- oder None, wenn der Datensatz nicht lesbar ist. Wirft nie."""
    try:
        return bretter_zeiten(dataset_file)
    except Exception:
        return None


def brett_von(bretter: dict, zustand: dict) -> list | None:
    """Die bekannten Zeiten fuer das Board dieses Laufs -- oder None, wenn es unbekannt ist."""
    strecke = _falte(zustand.get("track"))
    klasse = str(zustand.get("performance_class") or "").upper()
    modus = _falte(zustand.get("rivals_mode"))
    for key in ((modus, strecke, klasse), ("", strecke, klasse)):
        if key in bretter:
            return bretter[key]
    return None


def treffer(bekannt: list, zeilen: list) -> int:
    """Wie viele der bekannten Spitzenzeiten auch im Lauf stehen (auf PRUEF_TOLERANZ_MS genau)."""
    eingereicht = sorted(int(round(float(z["lap_time_seconds"]) * 1000)) for z in zeilen)
    n = 0
    for ms in bekannt:
        i = bisect.bisect_left(eingereicht, ms - PRUEF_TOLERANZ_MS)
        if i < len(eingereicht) and eingereicht[i] <= ms + PRUEF_TOLERANZ_MS:
            n += 1
    return n


def zeiten_passen(bekannt: list, zeilen: list) -> bool:
    """Steht mindestens die Haelfte der bekannten Spitzenzeiten im Lauf?"""
    if not bekannt or not zeilen:
        return False
    return treffer(bekannt, zeilen) >= math.ceil(len(bekannt) * PRUEF_ANTEIL)


def viel_schneller(bekannt: list, zeilen: list) -> bool:
    """Ist die beste gueltige Zeit des Laufs mehr als PRUEF_SCHNELLER unter der des Datensatzes?"""
    if not bekannt or not zeilen:
        return False
    gueltig = [z["lap_time_seconds"] for z in zeilen if z.get("is_clean") is True]
    beste = int(round(float(min(gueltig or [z["lap_time_seconds"] for z in zeilen])) * 1000))
    return beste < min(bekannt) * (1 - PRUEF_SCHNELLER)


def ende_unbewiesen(zustand: dict) -> str | None:
    """Warum das Ende nicht als Board zaehlt -- oder None. Die Regeln des alten Werkzeugs.

    DAS ENDE BEHAUPTET DIE APP NUR (seit 2026-10-04 so behandelt): "end_detected" oder
    "row_cap_reached" schreibt eine Installation ohne Passwort auch unter zehn Zeilen. Darum
    gilt die Untergrenze von UNBEWIESEN_ZEILEN fuer JEDES Ende, und eine bewusste Grenze
    (row_cap_reached) nur ab UNBEWIESEN_ZEILEN Plaetzen und zu UNBEWIESEN_ANTEIL gelesen.
    Ein wirklich kurzes Board wartet damit auf den Admin -- lieber das als ein erfundenes.
    """
    ende = str(zustand.get("end_status") or zustand.get("status") or "")
    if ende == "server_error":
        return GRUND_DIENST
    zeilen = int(zustand.get("rows_collected") or 0)
    if zeilen < UNBEWIESEN_ZEILEN:
        if ende in BEWIESENE_ENDEN:
            return "only %d rows (%s)" % (zeilen, ende)
        return "end not proven (%s) and only %d rows" % (ende or "?", zeilen)
    if ende == "row_cap_reached":
        grenze = zustand.get("row_cap")
        if not isinstance(grenze, int) or grenze < UNBEWIESEN_ZEILEN or zeilen < grenze * UNBEWIESEN_ANTEIL:
            return "row cap %s with %d rows" % (grenze if isinstance(grenze, int) else "?", zeilen)
        return None
    if ende in BEWIESENE_ENDEN:
        return None
    gesamt = zustand.get("implied_total")
    if isinstance(gesamt, int) and gesamt > 0 and zeilen / gesamt < UNBEWIESEN_ANTEIL:
        return "end not proven (%s) and %d of about %d rows (%.0f%%)" % (
            ende or "?", zeilen, gesamt, 100.0 * zeilen / gesamt)
    return None


def freigegeben(install_id: str, versteckt, root: Path | None = None) -> int:
    """Wie viele zurueckgehaltene Laeufe dieser Installation der Admin eingeblendet hat.

    Nur das zaehlt als Vertrauen: ein Lauf, der von selbst durchkam, hat niemand angesehen.
    `versteckt` sind die run_ids auf der Sichtbarkeitsliste.
    """
    return sum(1 for d in list_scans(root)
               if d.get("install_id") == install_id and d.get("held") and d.get("run_id") not in versteckt)


def freigabe(zustand: dict, zeilen: list, bretter: dict | None, vertraut: bool = True) -> str:
    """Leer, wenn der Lauf von selbst in den Datensatz darf -- sonst die Gruende, ihn zurueckzuhalten.

    `vertraut`: der Admin hat schon VERTRAUEN_AB Laeufe dieser Installation eingeblendet. Alles,
    was das Tor sonst prueft, ist oeffentlich (die Spitzenzeiten stehen in /api/dataset, das Ende
    behauptet die App) und laesst sich abschreiben -- erst ein Mensch, der Laeufe dieser
    Installation angesehen hat, macht sie zu einer, deren Laeufe von selbst durchgehen.
    """
    gruende = []
    if not vertraut:
        gruende.append(GRUND_NEU)
    if bretter is None:
        gruende.append(GRUND_KEIN_DATENSATZ)
    else:
        bekannt = brett_von(bretter, zustand)
        if not bekannt:
            gruende.append(GRUND_UNBEKANNT)
        elif not zeiten_passen(bekannt, zeilen):
            gruende.append(GRUND_ZEITEN)
    ende = ende_unbewiesen(zustand)
    if ende:
        gruende.append(ende)
    return "; ".join(gruende)


def _buch(root: Path | None = None) -> Path:
    return root or SCANS_DIR


def list_scans(root: Path | None = None) -> list:
    raus = []
    ordner = _buch(root)
    if ordner.exists():
        for p in sorted(ordner.glob("*.json")):
            try:
                raus.append(json.loads(p.read_text(encoding="utf-8")))
            except (OSError, ValueError):
                continue
    return raus


def tageskontingent(install_id: str, root: Path | None = None, now: float | None = None) -> None:
    grenze = (now or time.time()) - 86400
    n = sum(1 for d in list_scans(root)
            if d.get("install_id") == install_id and d.get("received", 0) >= grenze)
    if n >= LAEUFE_JE_INSTALL_TAG:
        raise laps.SubmitError(429, "%d scans in 24 hours from this installation; try tomorrow."
                               % LAEUFE_JE_INSTALL_TAG)


def schon_da(run_id: str, contrib_root: Path | None = None) -> bool:
    """Gibt es diesen Lauf schon -- von wem auch immer? Die Sichtbarkeit haengt am Namen."""
    wurzel = contrib_root or CONTRIB_ROOT
    return wurzel.exists() and any(p.is_dir() for p in wurzel.glob("*/" + run_id))


def gespeicherter_name(wer: str, run_id: str) -> str:
    """Der Name, unter dem ein App-Lauf liegt: "<app-kurz>_<run_id>".

    EIN EIGENER NAMENSRAUM (seit 2026-10-04): die App benennt wie das alte Werkzeug
    ("ocr_<Kategorie>_idxNN_<Klasse>_<Zeit>"), und die Sichtbarkeitsliste kennt nur den
    blossen Namen. Ein zurueckgehaltener App-Lauf mit dem Namen eines lokalen Laufs blendete
    sonst den lokalen aus (der Datensatzbau blendet nach Ordnernamen aus) -- und ein "Show"
    oder das Zuruecknehmen nach einem Fehler blendete einen ausgeblendeten lokalen wieder ein.
    Lokale Laeufe beginnen nie mit "app-". Wirft SubmitError(400), wenn der Name zu lang wird.
    """
    name = "%s_%s" % (wer, run_id)
    try:
        fmt.check_run_id(name)
        _windows_tauglich(name)
    except fmt.ContribError as e:
        raise laps.SubmitError(400, str(e)) from None
    return name


def store(install_id: str, zustand: dict, zeilen: list, auffaellig: list,
          contrib_root: Path | None = None, root: Path | None = None,
          now: float | None = None, tool: str | None = None,
          zurueckhalten: str = "", ausblenden=None) -> dict:
    """Ablegen wie import_contrib -- erst fertig daneben, dann in einem Zug an den Platz.

    Halb geschriebene Ordner duerfen im contrib-Baum nie stehen: deploy_gnas.py holt
    einen Lauf genau einmal (nur was es dort noch nicht gibt) und naehme ihn sonst
    unvollstaendig mit, fuer immer.

    `zurueckhalten` ist der Grund aus freigabe() ("" = darf auf die Seite);
    `ausblenden(run_id, grund)` setzt ihn auf die Sichtbarkeitsliste. Das geschieht
    VOR dem Zug an den Platz: holt deploy_gnas.py den Lauf, steht er schon auf der
    Liste -- umgekehrt koennte ein Bau dazwischen ihn ungeprueft mitnehmen. Und erst
    NACH der Pruefung auf einen gleichnamigen Lauf: sonst blendete ein abgelehnter
    Doppelter den echten aus.
    """
    wurzel = contrib_root or CONTRIB_ROOT
    buch = _buch(root)
    wer = wer_von(install_id)
    run_id = gespeicherter_name(wer, zustand["run_id"])
    zustand = dict(zustand, run_id=run_id)
    k = kennung(install_id, run_id)
    with _SCHLOSS:
        if schon_da(run_id, wurzel):
            raise laps.SubmitError(409, "A run named %s is already on the server." % run_id)
        werkstatt = buch / "_eingang" / k
        if werkstatt.exists():
            shutil.rmtree(werkstatt)
        import_contrib.write_run(werkstatt, zustand, zeilen, wer,
                                 tool or zustand.get("scanner_tool") or "app")
        ziel = wurzel / wer / run_id
        ziel.parent.mkdir(parents=True, exist_ok=True)
        if zurueckhalten and ausblenden is not None:
            ausblenden(run_id, zurueckhalten)
        try:
            try:
                os.replace(werkstatt, ziel)
            except OSError:
                # Liegt der Eingang auf einem anderen Laufwerk als contrib/, geht es nur
                # mit Kopieren.
                shutil.move(str(werkstatt), str(ziel))
        except Exception:
            # Kein Lauf, also auch kein Eintrag auf der Liste, der nichts meint.
            if zurueckhalten and ausblenden is not None:
                try:
                    ausblenden(run_id, None)
                except Exception:
                    pass
            raise
        eintrag = {
            "id": k, "install_id": install_id, "who": wer, "run_id": run_id,
            "track": zustand.get("track"), "class": zustand.get("performance_class"),
            "mode": zustand.get("rivals_mode"), "rows": zustand.get("rows_collected"),
            "maxRank": zustand.get("maximum_rank"), "status": zustand.get("status"),
            "completedAt": zustand.get("completed_at"), "tool": zustand.get("scanner_tool"),
            "received": now or time.time(), "flags": auffaellig,
            # Warum das Tor ihn zurueckhielt ("" = von selbst sichtbar). Bleibt stehen,
            # auch wenn der Admin ihn spaeter einblendet.
            "held": zurueckhalten,
        }
        laps._atomar_schreiben(buch / (k + ".json"), json.dumps(eintrag, ensure_ascii=False, indent=1))
    return eintrag
