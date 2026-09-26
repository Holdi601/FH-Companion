"""Wie oft wurde geladen, und wie viele Installationen melden sich?

## Was gezaehlt wird

**Downloads** -- jeder Abruf von `/download/haptics`. Eine schlichte Zahl je Tag.

**Aktive Installationen** -- jede App, die sich an einem Tag beim Server meldet.
Sie tut das ohnehin: beim Start fragt sie `/api/summary` (ist der Datensatz neuer?)
und `/api/haptics` (gibt es ein Update?). Wer das Programm an einem Tag benutzt hat,
taucht damit genau einmal auf.

## Woran eine Installation erkannt wird -- und warum keine IP gespeichert wird

Zuerst an `X-Forza-Install`: eine Zufallszahl, die die App einmal bei sich anlegt.
Sie sagt nichts ueber den Rechner, den Benutzer oder den Ort -- sie unterscheidet nur
eine Installation von einer anderen.

Aeltere Fassungen schicken das nicht. Fuer sie bleibt die Absenderadresse, und die
ist eine personenbezogene Angabe. Gespeichert wird sie deshalb NICHT: abgelegt wird
`HMAC(geheimes Salz, Kennung)`, verkuerzt auf 16 Zeichen. Das Salz liegt nur auf
diesem Server, wird beim ersten Lauf zufaellig erzeugt und nie ausgeliefert. Aus dem
Gespeicherten laesst sich die Adresse nicht zurueckrechnen; wer das Salz nicht hat,
kann auch nicht pruefen, ob eine bestimmte Adresse dabei war.

## Was die Zahlen NICHT sind

Zwei Menschen hinter demselben Anschluss zaehlen als einer, solange ihre App die
Kennung noch nicht mitschickt. Ein Mensch mit zwei Rechnern zaehlt als zwei. Wer das
Programm nie startet, fehlt -- ein Download ist kein Nutzer. Die Zahlen sind eine
Untergrenze fuer die Benutzung, keine Kopfzahl, und die Verwaltungsseite sagt das
auch so.

## Aufbewahrung

40 Tage. Das ist die laengste Frage, die diese Seite beantwortet (30 Tage), plus
Luft. Aeltere Tage werden bei jedem Schreibvorgang entfernt -- nicht von einem
Aufraeumlauf, den irgendwann niemand mehr anstoesst.
"""

from __future__ import annotations

import hashlib
import hmac
import json
import os
import secrets
import threading
import time
from datetime import date, datetime, timedelta, timezone
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
USAGE_FILE = WORKSPACE / "data" / "runtime" / "usage.json"
SALT_FILE = WORKSPACE / "data" / "runtime" / "usage_salt.txt"

#: So lange bleiben Tageszeilen stehen.
AUFBEWAHRUNG_TAGE = 40

#: So viele Zeichen des Hashs werden abgelegt. 16 hexadezimale Zeichen sind 64 Bit
#: -- genug, dass zwei Installationen sich nicht zufaellig treffen, und wenig genug,
#: dass die Datei auch bei tausend Installationen klein bleibt.
HASH_LAENGE = 16

_sperre = threading.Lock()


def _salz() -> bytes:
    """Das geheime Salz dieses Servers, beim ersten Mal erzeugt."""
    try:
        roh = SALT_FILE.read_text(encoding="utf-8").strip()
        if len(roh) >= 32:
            return roh.encode("utf-8")
    except OSError:
        pass
    neu = secrets.token_hex(32)
    try:
        SALT_FILE.parent.mkdir(parents=True, exist_ok=True)
        SALT_FILE.write_text(neu, encoding="utf-8")
        try:
            os.chmod(SALT_FILE, 0o600)
        except OSError:
            pass
    except OSError:
        # Ohne Datei bleibt das Salz fuer diesen Lauf im Speicher. Die Zahlen des
        # Tages stimmen dann, ueber einen Neustart hinweg nicht -- besser als gar
        # nicht zu zaehlen, und es faellt in der Datei auf.
        pass
    return neu.encode("utf-8")


def kennung(wert: str) -> str:
    """Aus einer Kennung einen Eintrag machen, aus dem nichts zurueckzurechnen ist."""
    return hmac.new(_salz(), (wert or "").encode("utf-8"),
                    hashlib.sha256).hexdigest()[:HASH_LAENGE]


def _laden() -> dict:
    try:
        d = json.loads(USAGE_FILE.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        d = {}
    d.setdefault("downloads", {})
    d.setdefault("checkins", {})
    d.setdefault("since", date.today().isoformat())
    return d


def _speichern(d: dict) -> None:
    grenze = (date.today() - timedelta(days=AUFBEWAHRUNG_TAGE)).isoformat()
    for feld in ("downloads", "checkins"):
        d[feld] = {tag: wert for tag, wert in d[feld].items() if tag >= grenze}
    try:
        USAGE_FILE.parent.mkdir(parents=True, exist_ok=True)
        temp = USAGE_FILE.with_suffix(".part")
        temp.write_text(json.dumps(d, separators=(",", ":")), encoding="utf-8")
        temp.replace(USAGE_FILE)
    except OSError:
        # Eine verlorene Zaehlung ist aergerlich, ein Fehler im Abruf schlimmer.
        pass


def _heute(now: float | None = None) -> str:
    stempel = datetime.fromtimestamp(now if now is not None else time.time(),
                                     timezone.utc)
    return stempel.date().isoformat()


def note_download(now: float | None = None) -> None:
    """Ein Paket wurde geladen."""
    tag = _heute(now)
    with _sperre:
        d = _laden()
        d["downloads"][tag] = int(d["downloads"].get(tag, 0)) + 1
        _speichern(d)


def note_checkin(install: str | None, client: str | None,
                 now: float | None = None) -> None:
    """Eine Installation hat sich gemeldet.

    Geschrieben wird nur, wenn sie an diesem Tag NEU ist -- sonst kostet jeder
    Programmstart einen Schreibvorgang, und die App fragt beim Start zweimal.
    """
    roh = (install or "").strip() or (client or "").strip()
    if not roh:
        return
    eintrag = kennung(roh)
    tag = _heute(now)
    with _sperre:
        d = _laden()
        bekannt = d["checkins"].setdefault(tag, [])
        if eintrag in bekannt:
            return
        # OBERGRENZE JE TAG. Die Kennung schickt der Aufrufer; ein Skript mit
        # jedesmal neuer Kennung blaehte sonst die Datei auf und liesse sie bei
        # jeder Meldung ganz neu schreiben. Mehr echte Installationen als das an
        # einem Tag waeren ein schoenes Problem -- gezaehlt wird dann nicht weiter.
        if len(bekannt) >= MAX_JE_TAG:
            return
        bekannt.append(eintrag)
        _speichern(d)


MAX_JE_TAG = 20_000


def summary(now: float | None = None) -> dict:
    """Downloads und aktive Installationen ueber Tag, Woche und Monat."""
    heute = datetime.fromtimestamp(now if now is not None else time.time(),
                                   timezone.utc).date()
    with _sperre:
        d = _laden()

    def fenster(tage: int) -> list[str]:
        return [(heute - timedelta(days=i)).isoformat() for i in range(tage)]

    def downloads(tage: int) -> int:
        return sum(int(d["downloads"].get(t, 0)) for t in fenster(tage))

    def aktive(tage: int) -> int:
        menge: set[str] = set()
        for t in fenster(tage):
            menge.update(d["checkins"].get(t, []))
        return len(menge)

    verlauf = []
    for i in range(29, -1, -1):
        tag = (heute - timedelta(days=i)).isoformat()
        verlauf.append({
            "day": tag,
            "downloads": int(d["downloads"].get(tag, 0)),
            "active": len(d["checkins"].get(tag, [])),
        })

    return {
        "since": d.get("since"),
        "retentionDays": AUFBEWAHRUNG_TAGE,
        "downloads": {
            "today": downloads(1),
            "week": downloads(7),
            "month": downloads(30),
            "total": sum(int(v) for v in d["downloads"].values()),
        },
        "active": {
            "today": aktive(1),
            "week": aktive(7),
            "month": aktive(30),
        },
        "daily": verlauf,
    }
