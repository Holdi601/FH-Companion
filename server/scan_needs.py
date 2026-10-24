"""Welche Boards am noetigsten einen neuen Scan brauchen -- und wer gerade welches scannt.

Seit 2026-10-24. Der Nutzer: "In the app you should see which leaderboards need the most a new
scan as they are the oldest" -- und: wer einen Scan startet, meldet das Board als "being scanned"
an (30-45 Minuten), damit mehrere Scanner nicht dasselbe tun. Die Anmeldung sperrt nichts, sie
schiebt das Board nur ans Ende der Liste; kommt in der Zeit kein Scan, verfaellt sie.

Die Liste rechnet wie der Reiter "Needs a rescan" der Seite (build_analytics_site.renderRescan):
jedes Board des Datensatzes und jede bekannte Strecke in jeder Klasse; je Board der juengste Scan
aus dem Datensatz -- und aus den seitdem angenommenen Scans, die der naechste Bau erst aufnimmt.
"""

from __future__ import annotations

import json
import os
import threading
import time
from datetime import datetime
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
ANMELDUNGEN = WORKSPACE / "data" / "runtime" / "scan_claims.json"
KLASSEN = ("D", "C", "B", "A", "S1", "S2", "R")
# So lange gilt eine Anmeldung: ein ganzes Board dauert bis zu einer halben Stunde.
ANMELDUNG_SEKUNDEN = 40 * 60
# Mehr gleichzeitige Anmeldungen je Installation nicht -- sonst sperrt einer die ganze Liste.
HOECHSTENS_JE_INSTALLATION = 30

_SCHLOSS = threading.Lock()
_DATEN: dict = {"schluessel": None, "bretter": []}
_ANMELDUNG_SCHLOSS = threading.Lock()


def _falte(text) -> str:
    return " ".join(str(text or "").strip().lower().split())


def board_schluessel(strecke, klasse) -> str:
    """Ein Board heisst Strecke|Klasse -- die Scans des Datensatzes tragen keine Kategorie."""
    return "%s|%s" % (_falte(strecke), str(klasse or "").strip().upper())


def _zeit(text) -> float | None:
    if not text:
        return None
    try:
        return datetime.fromisoformat(str(text)[:19]).timestamp()
    except ValueError:
        return None


def bretter(dataset_file: Path) -> list[dict]:
    """Alle Boards mit Kategorie, juengstem Scan (Zeitpunkt), Tiefe und Zahl der Scans. Gemerkt je Stand."""
    st = dataset_file.stat()
    schluessel = (str(dataset_file), st.st_size, st.st_mtime_ns)
    with _SCHLOSS:
        if _DATEN["schluessel"] == schluessel:
            return _DATEN["bretter"]
        d = json.loads(dataset_file.read_text(encoding="utf-8"))
        kategorien = d.get("categories") or []
        tracks, classes = d.get("tracks") or [], d.get("classes") or []
        meta = d.get("meta") or {}
        zuletzt: dict[str, float] = {}
        anzahl: dict[str, int] = {}
        for s in meta.get("scans") or []:
            k = board_schluessel(s.get("t"), s.get("k"))
            anzahl[k] = anzahl.get(k, 0) + 1
            t = _zeit(s.get("when"))
            if t is not None and t > zuletzt.get(k, 0.0):
                zuletzt[k] = t
        reihe: dict[str, dict] = {}
        for b in d.get("boards") or []:
            try:
                strecke = tracks[b["t"]]
                klasse = str(classes[b["k"]]).upper()
                c = b.get("c")
                kategorie = kategorien[c] if isinstance(c, int) and 0 <= c < len(kategorien) else ""
            except (IndexError, KeyError, TypeError):
                continue
            k = board_schluessel(strecke, klasse)
            alt = reihe.get(k)
            tiefe = b.get("maxRank") if isinstance(b.get("maxRank"), int) else 0
            if alt is None or tiefe > alt["depth"]:
                reihe[k] = {"category": kategorie, "track": strecke, "class": klasse, "depth": tiefe}
        for kategorie, namen in (meta.get("known_routes") or {}).items():
            for name in namen or []:
                # Die Liste der unbestaetigten Strecken traegt Saetze neben Namen.
                if not isinstance(name, str) or len(name) > 40 or "," in name:
                    continue
                for klasse in KLASSEN:
                    reihe.setdefault(board_schluessel(name, klasse),
                                     {"category": kategorie, "track": name, "class": klasse, "depth": 0})
        raus = []
        for k, e in reihe.items():
            raus.append(dict(e, key=k, last=zuletzt.get(k), scans=anzahl.get(k, 0)))
        _DATEN["schluessel"] = schluessel
        _DATEN["bretter"] = raus
        return raus


# ---------------------------------------------------------------------- Anmeldungen

def _lies(pfad: Path) -> dict:
    try:
        d = json.loads(pfad.read_text(encoding="utf-8"))
        return d if isinstance(d, dict) else {}
    except (OSError, ValueError):
        return {}


def _schreibe(pfad: Path, d: dict) -> None:
    pfad.parent.mkdir(parents=True, exist_ok=True)
    neu = pfad.with_suffix(".tmp")
    neu.write_text(json.dumps(d), encoding="utf-8")
    os.replace(neu, pfad)


def anmeldungen(pfad: Path | None = None, now: float | None = None) -> dict:
    """Board -> {"install": ..., "until": ...}, nur die noch gueltigen."""
    jetzt = now or time.time()
    return {k: v for k, v in _lies(pfad or ANMELDUNGEN).items()
            if isinstance(v, dict) and (v.get("until") or 0) > jetzt}


def anmelden(install_id: str, strecke, klasse, pfad: Path | None = None, now: float | None = None) -> tuple[bool, float]:
    """Ein Board fuer diese Installation anmelden. (True, bis) -- oder (False, bis eines anderen)."""
    jetzt = now or time.time()
    k = board_schluessel(strecke, klasse)
    with _ANMELDUNG_SCHLOSS:
        alle = anmeldungen(pfad, jetzt)
        da = alle.get(k)
        if da and da.get("install") != install_id:
            return False, float(da.get("until") or 0)
        eigene = sum(1 for v in alle.values() if v.get("install") == install_id)
        if not da and eigene >= HOECHSTENS_JE_INSTALLATION:
            return False, 0.0
        bis = jetzt + ANMELDUNG_SEKUNDEN
        alle[k] = {"install": install_id, "until": bis}
        _schreibe(pfad or ANMELDUNGEN, alle)
        return True, bis


def freigeben(strecke, klasse, install_id: str | None = None, pfad: Path | None = None,
              now: float | None = None) -> bool:
    """Die Anmeldung loesen -- die eigene, oder (install_id None) jede: das Board wurde gescannt."""
    k = board_schluessel(strecke, klasse)
    with _ANMELDUNG_SCHLOSS:
        alle = anmeldungen(pfad, now)
        da = alle.get(k)
        if not da or (install_id is not None and da.get("install") != install_id):
            return False
        del alle[k]
        _schreibe(pfad or ANMELDUNGEN, alle)
        return True


# ---------------------------------------------------------------------- die Liste

def liste(dataset_file: Path, angenommen: list, anmeldung: dict, now: float | None = None,
          wie_viele: int = 300) -> list[dict]:
    """Die Boards, die einen Scan am noetigsten haben: nie gescannte zuerst, dann die aeltesten.

    `angenommen`: die seit dem Bau angenommenen Scans (Eintraege aus scan_submissions, nicht
    ausgeblendet) -- ein Scan von heute Morgen zaehlt, auch wenn der Bau noch aussteht.
    Angemeldete Boards stehen hinten, mit "being_scanned_until"; die Anmeldung sperrt nichts.
    """
    jetzt = now or time.time()
    frisch: dict[str, float] = {}
    for d in angenommen:
        k = board_schluessel(d.get("track"), d.get("class"))
        t = d.get("received") or 0
        if t > frisch.get(k, 0.0):
            frisch[k] = t
    raus = []
    for b in bretter(dataset_file):
        zuletzt = max(b.get("last") or 0.0, frisch.get(b["key"], 0.0)) or None
        an = anmeldung.get(b["key"])
        raus.append({
            "category": b["category"], "track": b["track"], "class": b["class"],
            "lastScan": zuletzt, "days": None if zuletzt is None else int(max(0.0, jetzt - zuletzt) // 86400),
            "depth": b["depth"], "scans": b["scans"],
            "beingScannedUntil": an.get("until") if an else None,
        })
    raus.sort(key=lambda e: (e["beingScannedUntil"] is not None,
                             e["lastScan"] is not None,
                             e["lastScan"] or 0.0,
                             e["track"].lower(),
                             KLASSEN.index(e["class"]) if e["class"] in KLASSEN else 9))
    return raus[:wie_viele]
