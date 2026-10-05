"""Adressen, die zu oft falsch angeklopft haben -- eine gemeinsame Sperrliste.

Gezaehlt werden fehlgeschlagene ANMELDUNGEN aller Art: eine falsche
Admin-Unterschrift, eine falsche Rundenunterschrift, ein falsches
Zugangspasswort. Zehn innerhalb einer Stunde sperren die Adresse fuer 24 Stunden
fuer alles, was sich anmeldet (Admin, Einreichen, Hochladen). Die Seite selbst
bleibt lesbar -- eine Sperre soll Rateversuche beenden, nicht Leser aussperren.

## Das eigene Netz ist ausgenommen

Adressen aus dem Heimnetz (192.168.x.x, 10.x.x.x, localhost ...) werden gezaehlt
und angezeigt, aber NIE gesperrt. Sonst sperrte sich der Verwalter mit zehn
Tippfehlern aus der Seite aus, mit der er Sperren aufhebt -- und muesste per SSH
an diese Datei. Von aussen ist so eine Adresse ohnehin nicht zu faelschen.

## Aufheben

Ueber die Verwaltungsseite (Abschnitt "Bans") oder hier:

    python server/ip_bans.py            Liste
    python server/ip_bans.py unban <ip> Sperre aufheben
"""
from __future__ import annotations

import ipaddress
import json
import os
import sys
import threading
import time
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
DATEI = WORKSPACE / "data" / "runtime" / "ip_bans.json"

FEHL_GRENZE = 10
FEHL_FENSTER = 3600
SPERRE_SEKUNDEN = 24 * 3600

_SCHLOSS = threading.Lock()


def heimnetz(ip: str) -> bool:
    try:
        a = ipaddress.ip_address(ip)
    except ValueError:
        return False
    return a.is_private or a.is_loopback or a.is_link_local


def _laden(pfad: Path | None = None) -> dict:
    try:
        return json.loads((pfad or DATEI).read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}


def _sichern(d: dict, pfad: Path | None = None) -> None:
    ziel = pfad or DATEI
    ziel.parent.mkdir(parents=True, exist_ok=True)
    tmp = ziel.with_suffix(".tmp")
    tmp.write_text(json.dumps(d, indent=1), encoding="utf-8")
    os.replace(tmp, ziel)


def gesperrt(ip: str, now: float | None = None, pfad: Path | None = None) -> int:
    """Sekunden, die diese Adresse noch gesperrt ist; 0 = frei."""
    if not ip or heimnetz(ip):
        return 0
    jetzt = time.time() if now is None else now
    e = _laden(pfad).get(ip) or {}
    return int(max(0.0, float(e.get("until") or 0) - jetzt))


def fehlversuch(ip: str, art: str, now: float | None = None, pfad: Path | None = None) -> bool:
    """Einen Fehlversuch vermerken. True, wenn die Adresse damit gesperrt ist."""
    if not ip:
        return False
    jetzt = time.time() if now is None else now
    with _SCHLOSS:
        d = _laden(pfad)
        e = d.get(ip) or {"failures": []}
        fehl = [t for t in e.get("failures", []) if jetzt - float(t) < FEHL_FENSTER]
        fehl.append(jetzt)
        e["failures"] = fehl[-50:]
        e["last_kind"] = art
        e["last"] = jetzt
        gesperrt_jetzt = False
        if len(fehl) >= FEHL_GRENZE and not heimnetz(ip):
            e["until"] = jetzt + SPERRE_SEKUNDEN
            e["reason"] = f"auto: {len(fehl)} failed {art} attempts within an hour"
            gesperrt_jetzt = True
        d[ip] = e
        # Alte, abgelaufene Eintraege ohne juengere Fehlversuche weg.
        for k in [k for k, v in d.items()
                  if float(v.get("until") or 0) < jetzt
                  and not any(jetzt - float(t) < FEHL_FENSTER for t in v.get("failures", []))]:
            d.pop(k, None)
        _sichern(d, pfad)
        return gesperrt_jetzt


def entsperren(ip: str, pfad: Path | None = None) -> dict:
    with _SCHLOSS:
        d = _laden(pfad)
        war = d.pop(ip, None)
        _sichern(d, pfad)
    return {"ip": ip, "was_banned": bool(war and war.get("until"))}


def liste(now: float | None = None, pfad: Path | None = None) -> list:
    """Alle Adressen mit Fehlversuchen oder Sperre, fuer die Verwaltungsseite."""
    jetzt = time.time() if now is None else now
    raus = []
    for ip, e in sorted(_laden(pfad).items()):
        bis = float(e.get("until") or 0)
        raus.append({
            "ip": ip,
            "banned": bis > jetzt,
            "until": time.strftime("%Y-%m-%d %H:%M", time.localtime(bis)) if bis > jetzt else "",
            "reason": e.get("reason", ""),
            "failures_last_hour": sum(1 for t in e.get("failures", []) if jetzt - float(t) < FEHL_FENSTER),
            "last_kind": e.get("last_kind", ""),
            "home_network": heimnetz(ip),
        })
    return raus


if __name__ == "__main__":
    if len(sys.argv) >= 3 and sys.argv[1] == "unban":
        print(entsperren(sys.argv[2]))
    else:
        for z in liste():
            print(z)
