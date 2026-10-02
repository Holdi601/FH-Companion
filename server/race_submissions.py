"""Eingereichte Rennergebnisse aus Horizon Play -- und die Horizon-Play-Zeiten je Auto daraus.

## Was ankommt

Nach einem Rennen liest die App den Ergebnisschirm (RaceResultsReader): je Fahrer Platz,
Mensch/KI/verlassen, Auto (car_id), PI, auf Rundkursen die beste Runde, sonst den
Fortschritt, und die Zeit im Ziel. KEINE Gamertags (Entscheidung des Nutzers, 2026-10-01).
Dazu ein Bild des Ergebnisschirms als Beleg -- es sieht nur der Verwalter, nie die Seite
(darauf stehen die Gamertags der anderen).

Unterschrieben wie eine Runde (lap_submissions.verify, eigener Pfad), mitgeschickt immer
dann, wenn die App Runden einreicht (submit_laps, ab Werk an, abschaltbar).

## Was daraus wird

Je Strecke, Klasse und Auto die Zeiten aller MENSCHEN (Stufenabzeichen) -- KI und wer
verlassen hat, zaehlen nicht. Auf Rundkursen die beste Runde (direkt vergleichbar mit
einer Rivals-Runde), auf Strecken von A nach B die Zeit im Ziel.

Die Zeit eines Autos ist NICHT die schnellste, sondern die an der 1-%-Grenze (Wunsch des
Nutzers, 2026-10-01): ein Betrueger in einer Lobby soll sie nicht bestimmen. Genauer: die
Zeiten aufsteigend, genommen wird die an Stelle ceil(1 % von N) (ab 0 gezaehlt) -- so
faellt immer mindestens die eine schnellste weg; bei 100 Zeiten die zwei schnellsten,
bei 500 die fuenf. Mit nur EINER Zeit gilt sie, ist aber "unbestaetigt".

Was der Verwalter als Betrug kennt, blendet er aus (je Zeile eines Rennens): dann rueckt
die naechste nach -- und auch die kann er ausblenden, bis es stimmt. Ausgeblendet, nie
geloescht, wie bei den Runden (lap_submissions.set_hidden).
"""

from __future__ import annotations

import base64
import hashlib
import json
import math
import re
import time
from pathlib import Path

import lap_submissions as laps

WORKSPACE = Path(__file__).resolve().parent.parent
RACES_DIR = WORKSPACE / "data" / "submissions" / "races"

MAX_BELEG = 1_500_000          # das Bild, entpackt (JPEG, 1280x720 sind ~150 KB)
MAX_ZEILEN = 16
RENNEN_JE_INSTALL_TAG = 200
ARTEN = ("human", "ai", "left")
KLASSEN = ("D", "C", "B", "A", "S1", "S2", "R", "X")
_KENNUNG = re.compile(r"^[0-9a-f]{20}$")


def klasse_aus_pi(pi: int | None) -> str | None:
    """Wie LapArchive.ClassOf: D bis 400, C 500, B 600, A 700, S1 800, S2 900, R 998."""
    if not pi or pi <= 0:
        return None
    for grenze, k in ((400, "D"), (500, "C"), (600, "B"), (700, "A"), (800, "S1"), (900, "S2"), (998, "R")):
        if pi <= grenze:
            return k
    return "X"


def _ganz(wert, klein: int, gross: int):
    if isinstance(wert, bool) or not isinstance(wert, (int, float)):
        return None
    w = int(wert)
    return w if klein <= w <= gross else None


def _text(wert, laenge: int) -> str | None:
    if not isinstance(wert, str):
        return None
    t = re.sub(r"[\x00-\x1f<>]", "", wert).strip()
    return t[:laenge] or None


def pruefe_rennen(rennen: dict) -> tuple[dict, list]:
    """Das eingereichte Rennen auf das bringen, was abgelegt werden darf, und was daran auffaellt."""
    if not isinstance(rennen, dict):
        raise laps.SubmitError(400, "'race' must be an object.")
    feld_roh = rennen.get("field")
    if not isinstance(feld_roh, list) or not 2 <= len(feld_roh) <= MAX_ZEILEN:
        raise laps.SubmitError(400, "'field' must list 2 to %d drivers." % MAX_ZEILEN)
    strecke = _text(rennen.get("track"), 120)
    if not strecke:
        raise laps.SubmitError(400, "'track' is missing.")
    sauber = {
        "raceId": _text(rennen.get("id"), 80) or "",
        "at": _text(rennen.get("at"), 40),
        "track": strecke,
        "course": _text(rennen.get("course"), 120),
        "class": rennen.get("class") if rennen.get("class") in KLASSEN else None,
        "mode": _text(rennen.get("mode"), 20) or "unknown",
        "laps": _ganz(rennen.get("laps"), 1, 50),
        "drivers": _ganz(rennen.get("drivers"), 1, 24),
    }
    feld, auffaellig, plaetze = [], [], set()
    for roh in feld_roh:
        if not isinstance(roh, dict):
            raise laps.SubmitError(400, "Every field entry must be an object.")
        platz = _ganz(roh.get("place"), 1, MAX_ZEILEN)
        if platz is None or platz in plaetze:
            raise laps.SubmitError(400, "Places must be unique numbers from 1.")
        plaetze.add(platz)
        art = roh.get("kind") if roh.get("kind") in ARTEN else "ai"
        z = {
            "place": platz,
            "kind": art,
            "self": roh.get("self") is True,
            "car": _ganz(roh.get("car"), 1, 99_999),
            "carShort": _text(roh.get("carShort"), 60),
            "carName": _text(roh.get("carName"), 80),
            "pi": _ganz(roh.get("pi"), 100, 999),
            "progress": _ganz(roh.get("progress"), 0, 100),
            "bestLapMs": _ganz(roh.get("bestLapMs"), 5_000, 3_600_000),
            "ms": _ganz(roh.get("ms"), 10_000, 7_200_000),
        }
        feld.append({k: v for k, v in z.items() if v is not None and v is not False})
    feld.sort(key=lambda z: z["place"])
    if sum(1 for z in feld if z.get("self")) > 1:
        raise laps.SubmitError(400, "Only one driver can be the submitter.")
    # Wer frueher ankam, kann keine laengere Zeit haben.
    zeiten = [z["ms"] for z in feld if "ms" in z]
    if any(b < a for a, b in zip(zeiten, zeiten[1:])):
        auffaellig.append("finishing times out of order")
    for z in feld:
        if "bestLapMs" in z and "ms" in z and z["bestLapMs"] > z["ms"]:
            auffaellig.append("a best lap longer than the race")
            break
    sauber["field"] = feld
    return sauber, auffaellig


def beleg_pruefen(beleg) -> bytes:
    """Das Bild des Ergebnisschirms: Base64, ein JPEG, nicht zu gross."""
    if not isinstance(beleg, str) or not beleg:
        raise laps.SubmitError(400, "'proof' (the results screen as JPEG, Base64) is missing.")
    if len(beleg) > MAX_BELEG * 4 // 3 + 16:
        raise laps.SubmitError(413, "The proof picture is larger than %d KB." % (MAX_BELEG // 1000))
    try:
        roh = base64.b64decode(beleg, validate=True)
    except Exception:
        raise laps.SubmitError(400, "'proof' is not valid Base64.") from None
    if not roh.startswith(b"\xff\xd8\xff"):
        raise laps.SubmitError(400, "'proof' is not a JPEG picture.")
    return roh


def kennung(install_id: str, rennen: dict) -> str:
    """Dasselbe Rennen derselben Installation zweimal eingereicht: EINE Datei."""
    return hashlib.sha256(f"{install_id}|{rennen.get('raceId')}".encode("utf-8")).hexdigest()[:20]


def tageskontingent(install_id: str, root: Path | None = None, now: float | None = None) -> None:
    root = root or RACES_DIR
    grenze = (now or time.time()) - 86400
    n = 0
    if root.exists():
        for p in root.glob("*.json"):
            try:
                d = json.loads(p.read_text(encoding="utf-8"))
            except (OSError, ValueError):
                continue
            if d.get("install_id") == install_id and d.get("received", 0) >= grenze:
                n += 1
    if n >= RENNEN_JE_INSTALL_TAG:
        raise laps.SubmitError(429, "%d races in 24 hours from this installation; try tomorrow."
                               % RENNEN_JE_INSTALL_TAG)


def store(install_id: str, rennen: dict, auffaellig: list, beleg: bytes,
          root: Path | None = None, now: float | None = None) -> dict:
    root = root or RACES_DIR
    root.mkdir(parents=True, exist_ok=True)
    k = kennung(install_id, rennen)
    alt = {}
    p = root / (k + ".json")
    if p.exists():
        try:
            alt = json.loads(p.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            alt = {}
    d = dict(rennen, id=k, install_id=install_id, received=now or time.time(), flags=auffaellig,
             hiddenPlaces=alt.get("hiddenPlaces", []))
    (root / (k + ".jpg")).write_bytes(beleg)
    laps._atomar_schreiben(p, json.dumps(d, ensure_ascii=False, indent=1))
    return d


def list_races(root: Path | None = None) -> list:
    root = root or RACES_DIR
    raus = []
    if root.exists():
        for p in sorted(root.glob("*.json")):
            try:
                raus.append(json.loads(p.read_text(encoding="utf-8")))
            except (OSError, ValueError):
                continue
    return raus


def beleg(kennung_: str, root: Path | None = None) -> bytes:
    root = root or RACES_DIR
    if not _KENNUNG.match(kennung_ or ""):
        raise laps.SubmitError(400, "Not a valid ID.")
    p = root / (kennung_ + ".jpg")
    if not p.exists():
        raise laps.SubmitError(404, "No proof picture for this race.")
    return p.read_bytes()


def set_row_hidden(kennung_: str, platz: int, hidden: bool, grund: str = "",
                   root: Path | None = None) -> dict:
    """Eine Zeile eines Rennens aus der Wertung nehmen (oder zurueck) -- nie loeschen."""
    root = root or RACES_DIR
    if not _KENNUNG.match(kennung_ or ""):
        raise laps.SubmitError(400, "Not a valid ID.")
    p = root / (kennung_ + ".json")
    if not p.exists():
        raise laps.SubmitError(404, "There is no race with this ID.")
    d = json.loads(p.read_text(encoding="utf-8"))
    if not any(z.get("place") == platz for z in d.get("field", [])):
        raise laps.SubmitError(404, "This race has no place %s." % platz)
    versteckt = {int(x) for x in d.get("hiddenPlaces", [])}
    if hidden:
        versteckt.add(int(platz))
    else:
        versteckt.discard(int(platz))
    d["hiddenPlaces"] = sorted(versteckt)
    if hidden:
        d.setdefault("hideReasons", {})[str(platz)] = (grund or "")[:200]
    laps._atomar_schreiben(p, json.dumps(d, ensure_ascii=False, indent=1))
    return {"ok": True, "id": kennung_, "hiddenPlaces": d["hiddenPlaces"]}


def zeit_an_grenze(zeiten: list) -> tuple[int, bool]:
    """Die Zeit an der 1-%-Grenze: (Zeit, bestaetigt). Siehe den Kopf dieser Datei."""
    z = sorted(zeiten)
    if len(z) == 1:
        return z[0], False
    stelle = min(len(z) - 1, max(1, math.ceil(0.01 * len(z))))
    return z[stelle], True


def hp_bretter(root: Path | None = None) -> list:
    """Die Horizon-Play-Zeiten je Strecke, Klasse, Auto und Art (Runde/Rennen)."""
    gruppen: dict[tuple, list] = {}
    for d in list_races(root):
        if d.get("mode") != "horizon-play":
            continue
        versteckt = set(d.get("hiddenPlaces", []))
        rundkurs = any("bestLapMs" in z for z in d.get("field", []))
        for z in d.get("field", []):
            if z.get("kind") != "human" or z.get("place") in versteckt or not z.get("car"):
                continue
            zeit = z.get("bestLapMs") if rundkurs else z.get("ms")
            if not zeit:
                continue
            klasse = klasse_aus_pi(z.get("pi")) or d.get("class")
            if not klasse:
                continue
            art = "lap" if rundkurs else "race"
            gruppen.setdefault((d["track"], klasse, z["car"], art), []).append(
                {"ms": zeit, "race": d.get("id"), "place": z.get("place")})
    raus = []
    for (strecke, klasse, auto, art), eintraege in sorted(gruppen.items()):
        ms, bestaetigt = zeit_an_grenze([e["ms"] for e in eintraege])
        raus.append({"track": strecke, "class": klasse, "car": auto, "kind": art, "ms": ms,
                     "n": len(eintraege), "confirmed": bestaetigt,
                     "fastest": min(e["ms"] for e in eintraege)})
    return raus
