"""Wie aktuell der Datensatz ist, und der Wunsch nach einem Neubau (seit 2026-10-12).

Gebaut wird hier, auf dem Server (serve_analytics.py, Minutentakt). Dieses Modul sagt der
Verwaltung:

  * wann der ausgelieferte Datensatz gebaut wurde (Zeitstempel von data/analytics/laps.json),
  * welche angenommenen Scans seitdem eingegangen sind, und ob sich die Sichtbarkeitsliste
    seitdem geaendert hat -- beides wartet auf den naechsten Bau,
  * ob der Admin einen Neubau angefordert hat (data/runtime/build_request.json),
  * wie der letzte Bau ausging (data/runtime/auto_build.json, von serve_analytics._melden).

Der Nutzer am 2026-10-12: zwei Scans eines Freundes standen in der Verwaltung, aber nicht auf
der Seite -- gebaut wurde nur, wenn jemand von Hand auslieferte.
"""

from __future__ import annotations

import json
import time
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
ANFRAGE = WORKSPACE / "data" / "runtime" / "build_request.json"
LETZTER_LAUF = WORKSPACE / "data" / "runtime" / "auto_build.json"


def _lies(pfad: Path):
    try:
        return json.loads(pfad.read_text(encoding="utf-8"))
    except Exception:
        return None


def zustand(dataset_file: Path, visibility_file: Path, scans_liste: list, versteckt: set,
            anfrage: Path | None = None, letzter: Path | None = None) -> dict:
    """Alles, was die Verwaltung ueber den naechsten Bau wissen muss."""
    try:
        gebaut = dataset_file.stat().st_mtime
    except OSError:
        gebaut = None
    try:
        sicht = visibility_file.stat().st_mtime
    except OSError:
        sicht = None
    wartend = []
    for d in scans_liste:
        if d.get("run_id") in versteckt:
            continue
        if gebaut is None or (d.get("received") or 0) > gebaut:
            wartend.append({k: d.get(k) for k in ("run_id", "who", "track", "class", "rows", "received")})
    wartend.sort(key=lambda d: d.get("received") or 0)
    return {
        "built_at": gebaut,
        "pending_scans": wartend,
        "visibility_changed": bool(sicht and gebaut and sicht > gebaut),
        "request": _lies(anfrage or ANFRAGE),
        "last_run": _lies(letzter or LETZTER_LAUF),
        "now": time.time(),
    }


def anfordern(wer: str = "admin", anfrage: Path | None = None, now: float | None = None) -> dict:
    """Einen Neubau anfordern. Der Minutentakt des Servers nimmt ihn auf."""
    pfad = anfrage or ANFRAGE
    pfad.parent.mkdir(parents=True, exist_ok=True)
    eintrag = {"at": now or time.time(), "by": wer}
    pfad.write_text(json.dumps(eintrag), encoding="utf-8")
    return eintrag
