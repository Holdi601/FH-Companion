"""Wer die Seite besucht -- gezaehlt, ohne jemanden wiederzuerkennen.

## Was gezaehlt wird

Aufrufe der oeffentlichen SEITEN: die Auswertung, /app und /contribute. Nicht die
Verwaltungsseite (das ist der Betreiber), keine Schnittstellen, Schriften oder
Downloads -- die sind kein Besuch, sondern das, was eine Seite nachlaedt.

## Besucher, ohne Cookie und ohne gespeicherte Adresse

Ein Besucher ist HMAC(Tagessalz, Adresse + Browserkennung), gekuerzt auf 16
Zeichen. Das Tagessalz entsteht zufaellig und wird um Mitternacht (UTC) verworfen.
Damit lassen sich Besucher INNERHALB eines Tages auseinanderhalten, aber niemand
ueber Tage hinweg verfolgen -- auch nicht von jemandem, der diese Datei hat. So
arbeiten auch Plausible und Fathom; es wird nichts auf dem Geraet des Besuchers
gespeichert und nichts Personenbezogenes aufbewahrt, ein Einwilligungsbanner ist
dafuer nicht noetig.

Die Folge steht auch auf der Verwaltungsseite: "Besucher" einer Woche ist die Summe
der Tagesbesucher. Wer an drei Tagen kommt, zaehlt dreimal.

## Land und Region

Aus einer lokalen Datenbank (geoip.py), nie ueber einen fremden Dienst. Gespeichert
wird nur die Zaehlung je Land und Region.

## Nicht gezaehlt

Bots und Skripte (an der Browserkennung), Aufrufe ohne Browserkennung, und das
eigene Netz: private Adressen und die eigene oeffentliche Adresse -- wer von zu
Hause ueber den eigenen DNS-Namen schaut, kommt mit genau dieser Adresse an. Die
eigenen Aufrufe werden gesondert gezaehlt, damit sichtbar bleibt, dass sie fehlen.

## Aufbewahrung

Zaehlungen je Stunde und Tag bleiben (nichts Personenbezogenes darin). Die Hashes
gibt es nur fuer den laufenden Tag.
"""
from __future__ import annotations

import hashlib
import hmac
import ipaddress
import json
import os
import re
import secrets
import socket
import threading
import time
from datetime import datetime, timezone
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
# Ueberschreibbar fuer Tests: ein Pruefserver soll nie in die echten Zahlen schreiben.
DATEI = Path(os.environ.get("FORZA_VISITS_FILE") or WORKSPACE / "data" / "runtime" / "visits.json")
# Der eigene oeffentliche Name -- aus config/local.json, nicht aus dem Quelltext.
import local_settings  # noqa: E402

EIGENER_NAME = local_settings.public_host()
HASH_LAENGE = 16
SCHREIBEN_ALLE = 15.0          # Sekunden; dazwischen bleibt es im Speicher
MAX_JE_TAG = 50_000            # Hashes je Tag und Topf -- danach wird nicht weiter unterschieden

# Seiten, die als Besuch zaehlen. Schluessel = Pfad, Wert = Anzeigename.
SEITEN = {
    "/rivals_auto_wertung.html": "car ratings",
    "/app": "app download", "/app/": "app download",
    "/haptics": "app download", "/haptics/": "app download",
    "/contribute": "contribute", "/contribute/": "contribute",
    "/contribute.html": "contribute",
    "/mitmachen": "contribute", "/mitmachen/": "contribute",
}

BOT = re.compile(
    r"bot|crawl|spider|slurp|curl|wget|python|httpclient|http-client|headless|"
    r"preview|monitor|uptime|scan|go-http|java/|okhttp|libwww|facebookexternalhit|"
    r"embedly|lighthouse|pingdom|zgrab|masscan|nmap|axios|node-fetch|powershell",
    re.IGNORECASE)

_schloss = threading.RLock()
_d: dict | None = None
_geschrieben = 0.0
_eigene_ip: tuple[float, set[str]] = (0.0, set())


def _utc(now: float | None) -> datetime:
    return datetime.fromtimestamp(time.time() if now is None else now, timezone.utc)


def _leer() -> dict:
    return {"since": _utc(None).date().isoformat(), "day": "", "salt": "",
            "seen": {}, "hours": {}, "days": {}, "geo": {}, "pages": {},
            "own": {}, "bots": {}, "names": {}, "continents": {}}


def _laden() -> dict:
    global _d
    if _d is None:
        try:
            _d = json.loads(DATEI.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            _d = _leer()
        for k, v in _leer().items():
            _d.setdefault(k, v)
    return _d


def _schreiben(d: dict, sofort: bool = False) -> None:
    global _geschrieben
    if not sofort and time.time() - _geschrieben < SCHREIBEN_ALLE:
        return
    try:
        DATEI.parent.mkdir(parents=True, exist_ok=True)
        teil = DATEI.with_suffix(".part")
        teil.write_text(json.dumps(d, separators=(",", ":")), encoding="utf-8")
        try:
            os.chmod(teil, 0o600)   # das Tagessalz steht darin
        except OSError:
            pass
        os.replace(teil, DATEI)
        _geschrieben = time.time()
    except OSError:
        pass


def flush() -> None:
    """Beim Beenden: was im Speicher steht, auf die Platte."""
    with _schloss:
        if _d is not None:
            _schreiben(_d, sofort=True)


def eigenes_netz(ip: str) -> bool:
    """Privat, oder die eigene oeffentliche Adresse (alle 10 Minuten neu aufgeloest)."""
    global _eigene_ip
    try:
        a = ipaddress.ip_address(ip)
    except ValueError:
        return False
    if a.is_private or a.is_loopback or a.is_link_local:
        return True
    stand, menge = _eigene_ip
    if time.time() - stand > 600:
        try:
            menge = {x[4][0] for x in socket.getaddrinfo(EIGENER_NAME, None)}
        except OSError:
            pass
        _eigene_ip = (time.time(), menge)
    return str(a) in menge


def _tag_wechseln(d: dict, tag: str) -> None:
    """Neuer UTC-Tag: neues Salz, die Hashes des alten Tages verwerfen."""
    if d.get("day") == tag and d.get("salt"):
        return
    d["day"] = tag
    d["salt"] = secrets.token_hex(32)
    d["seen"] = {}


def _neu(d: dict, topf: str, h: str) -> bool:
    """Ist dieser Besucher in diesem Topf heute neu? Merkt ihn sich dabei."""
    menge = d["seen"].setdefault(topf, [])
    if h in menge:
        return False
    if len(menge) >= MAX_JE_TAG:
        return False
    menge.append(h)
    return True


def note_page(path: str, ip: str, user_agent: str, now: float | None = None) -> str:
    """Einen Seitenaufruf zaehlen. Gibt zurueck, was daraus wurde (fuer Tests)."""
    seite = SEITEN.get(path.split("?", 1)[0])
    if seite is None:
        return "not a page"
    ua = (user_agent or "").strip()
    t = _utc(now)
    tag, stunde = t.date().isoformat(), t.strftime("%Y-%m-%dT%H")
    with _schloss:
        d = _laden()
        if not ua or BOT.search(ua):
            d["bots"][tag] = int(d["bots"].get(tag, 0)) + 1
            _schreiben(d)
            return "bot"
        if eigenes_netz(ip):
            d["own"][tag] = int(d["own"].get(tag, 0)) + 1
            _schreiben(d)
            return "own"
        _tag_wechseln(d, tag)
        h = hmac.new(d["salt"].encode(), f"{ip}|{ua}".encode(), hashlib.sha256).hexdigest()[:HASH_LAENGE]

        def zaehle(zeile: list, topf: str) -> None:
            zeile[0] += 1
            if _neu(d, topf, h):
                zeile[1] += 1

        zaehle(d["hours"].setdefault(stunde, [0, 0]), "h" + stunde)
        zaehle(d["days"].setdefault(tag, [0, 0]), "d")
        seiten = d["pages"].setdefault(tag, {})
        seiten[seite] = int(seiten.get(seite, 0)) + 1

        import geoip
        ort = geoip.lookup(ip)
        cc = ort["cc"] if ort else "??"
        if ort:
            d["names"][cc] = ort["country"]
            if ort["continent"]:
                d["continents"][cc] = [ort["continent"], ort["continentName"]]
        land = d["geo"].setdefault(tag, {}).setdefault(cc, [0, 0, {}])
        zaehle(land, "c" + cc)
        region = (ort or {}).get("region") or ""
        if region:
            zaehle(land[2].setdefault(region, [0, 0]), "r" + cc + "|" + region)
        _schreiben(d)
    return "counted"


def summary(now: float | None = None) -> dict:
    """Alles fuer die Verwaltungsseite -- zusammengefasst wird im Browser."""
    import geoip
    with _schloss:
        d = _laden()
        # EINE KOPIE, unter dem Schloss gezogen: die Antwort wird erst danach
        # geschrieben, und ein Besuch waehrenddessen veraenderte sonst das Woerterbuch
        # mitten im Serialisieren. Salz und Hashes gehoeren nicht hinein.
        auszug = json.loads(json.dumps({k: d[k] for k in (
            "since", "hours", "days", "geo", "pages", "own", "bots", "names", "continents")}))
    auszug["geoip"] = geoip.status()
    auszug["now"] = _utc(now).isoformat(timespec="seconds")
    return auszug
