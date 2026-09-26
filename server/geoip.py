"""Land und Region einer Adresse -- aus einer LOKALEN Datenbank.

## Warum lokal

Ein Dienst wie ip-api.com bekaeme jede Besucheradresse geschickt. Nach der DSGVO
waere das eine Uebermittlung personenbezogener Daten an einen Dritten, oft in ein
Drittland -- genau das, was die selbst ausgelieferten Schriften vermeiden sollten.
Hier wird die Adresse nur in einer Datei auf diesem Server nachgeschlagen und
danach verworfen; gespeichert wird allein die Zaehlung je Land und Region
(visits.py).

## Die Datenbank

DB-IP "IP to City Lite" (https://db-ip.com), Lizenz CC BY 4.0 -- die Verwaltungsseite
nennt die Quelle, wie die Lizenz es verlangt. Monatlich neu, rund 60 MB gepackt. Sie
enthaelt auch Staedte; die werden hier bewusst NICHT gelesen: eine Stadt ist bei
wenigen Besuchern schon fast eine Person.

Geholt wird sie von diesem Server selbst, im Hintergrund: beim Start, wenn sie fehlt
oder aelter als 35 Tage ist. Die Anfrage an db-ip.com traegt nur die Adresse dieses
Servers, keine eines Besuchers. Erst wird entpackt und GEOEFFNET, dann ausgetauscht
-- eine halbe oder kaputte Datei ersetzt nie eine gute.
"""
from __future__ import annotations

import gzip
import ipaddress
import os
import shutil
import threading
import time
import urllib.request
from datetime import date
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
ORDNER = WORKSPACE / "data" / "runtime" / "geoip"
DATEI = ORDNER / "dbip-city-lite.mmdb"
QUELLE = "https://download.db-ip.com/free/dbip-city-lite-{monat}.mmdb.gz"
HOECHSTENS_TAGE = 35
ATTRIBUTION = "IP geolocation by DB-IP (db-ip.com), CC BY 4.0"

_schloss = threading.Lock()
_leser = None
_geladen_aus: float = 0.0
_holt = False


def _oeffnen():
    """Den Leser (neu) oeffnen, wenn die Datei sich geaendert hat."""
    global _leser, _geladen_aus
    try:
        stand = DATEI.stat().st_mtime
    except OSError:
        return None
    if _leser is not None and stand == _geladen_aus:
        return _leser
    try:
        import maxminddb
    except ImportError:
        return None
    try:
        neu = maxminddb.open_database(str(DATEI))
    except Exception:
        return None
    alt, _leser, _geladen_aus = _leser, neu, stand
    if alt is not None:
        try:
            alt.close()
        except Exception:
            pass
    return _leser


def lookup(ip: str) -> dict | None:
    """{'cc', 'country', 'region', 'continent'} -- oder None, wenn unbekannt.

    Privates Netz und unbekannte Adressen geben None; der Aufrufer zaehlt sie unter
    "unbekannt", statt ein Land zu erfinden.
    """
    try:
        adresse = ipaddress.ip_address(ip)
    except ValueError:
        return None
    if adresse.is_private or adresse.is_loopback or adresse.is_link_local:
        return None
    _vielleicht_erneuern()
    with _schloss:
        leser = _oeffnen()
        if leser is None:
            return None
        try:
            rec = leser.get(str(adresse))
        except Exception:
            return None
    if not isinstance(rec, dict):
        return None
    land = rec.get("country") or {}
    cc = str(land.get("iso_code") or "").upper()
    if not cc:
        return None
    namen = land.get("names") or {}
    regionen = rec.get("subdivisions") or []
    region = ""
    if regionen and isinstance(regionen, list):
        region = str(((regionen[0] or {}).get("names") or {}).get("en") or "")
    kontinent = rec.get("continent") or {}
    return {
        "cc": cc,
        "country": str(namen.get("en") or cc),
        "region": region,
        "continent": str(kontinent.get("code") or ""),
        "continentName": str((kontinent.get("names") or {}).get("en") or ""),
    }


_zuletzt_geprueft = 0.0


def _vielleicht_erneuern() -> None:
    """Hoechstens stuendlich nachsehen, ob die Datenbank veraltet ist.

    Nur beim Start zu pruefen reichte nicht: der Dienst laeuft Monate ohne Neustart,
    und die Laender stuenden dann auf dem Stand seines letzten Starts.
    """
    global _zuletzt_geprueft
    jetzt = time.time()
    if jetzt - _zuletzt_geprueft < 3600:
        return
    _zuletzt_geprueft = jetzt
    try:
        alt = (jetzt - DATEI.stat().st_mtime) / 86400 >= HOECHSTENS_TAGE
    except OSError:
        alt = True
    if alt:
        refresh_in_background()


def status() -> dict:
    """Fuer die Verwaltungsseite: gibt es die Datenbank, und von wann ist sie?"""
    try:
        st = DATEI.stat()
        return {"available": True, "updated": time.strftime("%Y-%m-%d", time.gmtime(st.st_mtime)),
                "attribution": ATTRIBUTION}
    except OSError:
        return {"available": False, "updated": None, "attribution": ATTRIBUTION}


def _monate() -> list[str]:
    heute = date.today()
    diesen = f"{heute.year:04d}-{heute.month:02d}"
    vorher = date(heute.year - (heute.month == 1), 12 if heute.month == 1 else heute.month - 1, 1)
    return [diesen, f"{vorher.year:04d}-{vorher.month:02d}"]


def refresh(log=print, force: bool = False) -> bool:
    """Die Datenbank holen, wenn sie fehlt oder zu alt ist. True, wenn danach eine da ist."""
    try:
        alter_tage = (time.time() - DATEI.stat().st_mtime) / 86400
    except OSError:
        alter_tage = None
    if not force and alter_tage is not None and alter_tage < HOECHSTENS_TAGE:
        return True
    ORDNER.mkdir(parents=True, exist_ok=True)
    for monat in _monate():
        url = QUELLE.format(monat=monat)
        teil = DATEI.with_suffix(".part")
        gepackt = DATEI.with_suffix(".gz.part")
        try:
            anfrage = urllib.request.Request(url, headers={"User-Agent": "forza-site-geoip"})
            with urllib.request.urlopen(anfrage, timeout=300) as antwort, open(gepackt, "wb") as f:
                shutil.copyfileobj(antwort, f, 1 << 20)
            with gzip.open(gepackt, "rb") as ein, open(teil, "wb") as aus:
                shutil.copyfileobj(ein, aus, 1 << 20)
            # ERST OEFFNEN, DANN TAUSCHEN.
            import maxminddb
            probe = maxminddb.open_database(str(teil))
            probe.get("8.8.8.8")
            probe.close()
            os.replace(teil, DATEI)
            log(f"GeoIP: {monat} geladen ({DATEI.stat().st_size // (1 << 20)} MB)")
            return True
        except Exception as fehler:
            log(f"GeoIP: {monat} nicht geladen -- {type(fehler).__name__}: {str(fehler)[:120]}")
        finally:
            for rest in (teil, gepackt):
                try:
                    rest.unlink()
                except OSError:
                    pass
    return DATEI.exists()


def refresh_in_background(log=print) -> None:
    """Beim Start: ohne den Server aufzuhalten, und nie zweimal gleichzeitig."""
    global _holt
    with _schloss:
        if _holt:
            return
        _holt = True

    def lauf():
        global _holt
        try:
            refresh(log)
        finally:
            _holt = False

    threading.Thread(target=lauf, name="geoip-refresh", daemon=True).start()
