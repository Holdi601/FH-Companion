"""Ist eine eingereichte Runde WIRKLICH schneller als die Bestenliste?

Der Server nimmt nicht, was die App behauptet. Er schlaegt selbst nach -- im selben
Datensatz, aus dem die Seite ihre Tabellen baut (data/analytics/laps.json) --, wie
schnell dieses Auto auf dieser Strecke in dieser Klasse bisher war, und laesst eine
Runde nur durch, wenn sie darunter liegt. Sonst wird sie abgewiesen und NICHT
abgelegt: was nicht schneller ist, hat auf der Seite nichts zu suchen und belegt
keinen Platz.

## Welche Zeit zaehlt

Die SCHNELLSTE GUELTIGE Runde des Autos auf dem Board. Die Seite zeigt je Auto eine
Zeit, die bis zu fuenf Runden tief gewaehlt sein kann (pickCars) -- also nie
schneller als diese. Wer unter der schnellsten liegt, ersetzt darum auf der Seite
in jedem Fall die angezeigte Zeit. Ungueltige Runden zaehlen nicht: die Seite zeigt
sie standardmaessig nicht, und sie sind oft schneller (Board has an invalid tail).

Dazu kommt die schnellste schon angenommene, sichtbare Einreichung fuer dasselbe
Auto: eine zweite, langsamere Einreichung aendert nichts und wird ebenso abgewiesen.

Ein Auto, das auf dem Board noch gar nicht steht, ist ein NEUES Auto -- es wird
angenommen, denn es gibt nichts, was es schlagen muesste.

Die Regeln entsprechen scripts/submitted_laps.js: Strecke nach gefaltetem Namen,
Klasse nach PI-Reihenfolge (0..6 = D C B A S1 S2 R), Auto nach Kennung.
"""
from __future__ import annotations

import json
import re
import threading
from pathlib import Path

PI_ORDER = ["D", "C", "B", "A", "S1", "S2", "R"]

_SCHLOSS = threading.Lock()
_INDEX: dict = {"schluessel": None, "bretter": {}}


def falte(name) -> str:
    return " ".join(str(name or "").strip().lower().split())


_KEIN_NAME = re.compile(r"^car\s*#?\s*\d+$", re.I)
_UNTERSCHEIDUNG = re.compile(r"\s*\(#\d+\)\s*$")


def gleiches_auto(a, b) -> bool:
    """Meinen zwei Autonamen dasselbe Auto? Ohne echten Namen auf einer Seite: ja (nichts spricht dagegen)."""
    a, b = str(a or "").strip(), str(b or "").strip()
    if not a or not b or _KEIN_NAME.match(a) or _KEIN_NAME.match(b):
        return True
    kern = lambda s: re.sub(r"[^a-z0-9]+", " ", _UNTERSCHEIDUNG.sub("", s).lower()).strip()
    return kern(a) == kern(b)


def _index(dataset_file: Path) -> dict:
    """(Strecke, Klasse) -> {Autokennung: schnellste gueltige ms}. Gemerkt je Stand."""
    st = dataset_file.stat()
    schluessel = (str(dataset_file), st.st_size, st.st_mtime_ns)
    with _SCHLOSS:
        if _INDEX["schluessel"] == schluessel:
            return _INDEX["bretter"]
        d = json.loads(dataset_file.read_text(encoding="utf-8"))
        flags = d.get("flags") or []
        sauber = 1 << flags.index("clean") if "clean" in flags else 0
        tracks, classes, car_ids = d["tracks"], d["classes"], d["carIds"]
        namen = {}
        for cid, name in zip(car_ids, d.get("carNames") or []):
            try:
                namen[int(cid)] = str(name or "")
            except (TypeError, ValueError):
                continue
        _INDEX["namen"] = namen
        bretter: dict = {}
        for b in d["boards"]:
            try:
                key = (falte(tracks[b["t"]]), classes[b["k"]])
            except (IndexError, KeyError, TypeError):
                continue
            gueltig = [(sig & sauber) == sauber for sig in b.get("gsig", [])]
            beste: dict = bretter.setdefault(key, {})
            gcar, lgrp, lms = b.get("gcar", []), b.get("lgrp", []), b.get("lms", [])
            for i, g in enumerate(lgrp):
                if g >= len(gcar) or (g < len(gueltig) and not gueltig[g]):
                    continue
                try:
                    ordinal = int(car_ids[gcar[g]])
                except (IndexError, ValueError, TypeError):
                    continue
                ms = lms[i]
                if ordinal not in beste or ms < beste[ordinal]:
                    beste[ordinal] = ms
        _INDEX.update({"schluessel": schluessel, "bretter": bretter})
        return bretter


class NichtSchneller(Exception):
    def __init__(self, status: int, message: str, langsamer: float = 0.0):
        super().__init__(message)
        self.status = status
        self.message = message
        # Um wie viel langsamer als die Vergleichszeit (0.05 = 5 %). Nur bei 409.
        self.langsamer = langsamer


def pruefe(runde: dict, dataset_file: Path, eingereicht: list) -> dict:
    """Wirft NichtSchneller, wenn die Runde nichts verbessert. Gibt sonst den Befund zurueck.

    `eingereicht`: die schon abgelegten Einreichungen (list_laps), fuer die
    schnellste sichtbare desselben Autos auf demselben Board.
    """
    try:
        sekunden = float(runde.get("lapSeconds"))
    except (TypeError, ValueError):
        raise NichtSchneller(400, "No lap time.") from None
    ms = round(sekunden * 1000)
    strecke = falte(runde.get("track"))
    if not strecke:
        raise NichtSchneller(422, "The lap names no track -- without one it cannot be "
                                  "compared with any leaderboard.")
    try:
        klasse = PI_ORDER[int(runde.get("carClass"))]
    except (TypeError, ValueError, IndexError):
        raise NichtSchneller(422, "Unknown class %r." % runde.get("carClass")) from None
    try:
        ordinal = int(runde.get("carOrdinal"))
    except (TypeError, ValueError):
        raise NichtSchneller(422, "No car.") from None
    if not dataset_file.exists():
        raise NichtSchneller(503, "No leaderboard on the server -- nothing to compare with.")

    bretter = _index(dataset_file)
    brett = bretter.get((strecke, klasse))
    if brett is None:
        raise NichtSchneller(422, "There is no leaderboard for %s in class %s."
                                  % (runde.get("track"), klasse))

    rivals = brett.get(ordinal)
    # DER WIDERSPRUCH (seit 2026-10-03): nennt die App das Auto anders als der Datensatz die
    # Kennung (sie liest den Namen vom Ergebnisschirm des Spiels), gehoert das Brett dieser
    # Kennung nicht zu diesem Auto -- die Runde ist dann ein neues Auto, kein Rekord.
    widerspruch = False
    if rivals is not None and not gleiches_auto(runde.get("carName"), _INDEX.get("namen", {}).get(ordinal)):
        widerspruch = True
        rivals = None
    if rivals is not None and ms >= rivals:
        raise NichtSchneller(409, "Not faster than the leaderboard: %.3f s against %.3f s."
                                  % (ms / 1000, rivals / 1000),
                             langsamer=(ms - rivals) / max(1, rivals))

    frueher = [e for e in eingereicht
               if not e.get("hidden")
               and falte((e.get("lap") or {}).get("track")) == strecke
               and str((e.get("lap") or {}).get("carClass")) == str(runde.get("carClass"))
               and str((e.get("lap") or {}).get("carOrdinal")) == str(ordinal)]
    if frueher:
        beste_frueher = min(round(float((e.get("lap") or {}).get("lapSeconds") or 9e9) * 1000)
                            for e in frueher)
        if ms >= beste_frueher:
            raise NichtSchneller(409, "Not faster than the time already submitted: "
                                      "%.3f s." % (beste_frueher / 1000),
                                 langsamer=(ms - beste_frueher) / max(1, beste_frueher))

    return {"rivalsMs": rivals, "newCar": rivals is None, "klass": klasse, "nameDisagrees": widerspruch}
