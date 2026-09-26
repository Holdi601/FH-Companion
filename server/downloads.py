"""Wie oft jede FASSUNG jedes Werkzeugs geladen wurde.

usage.py zaehlt Downloads nur je Tag und nur 40 Tage lang. Hier steht, WELCHE
Fassung geladen wurde, und fuer immer: die App (Paket) und das Scan-Werkzeug der
Beitragenden. Nichts Personenbezogenes -- nur Zaehler.

## Woher der Abruf kommt

  app      die App holt sich ein Update. Neue Fassungen melden sich als
           "FHCompanion/..." (bis 2026-09-26 "ForzaGripHaptics/..."), alte schicken gar keine Browserkennung.
  browser  jemand laedt es im Browser herunter (die Kennung beginnt mit Mozilla).
  other    alles andere: Skripte, Werkzeuge, Pruefer.

## Wann gezaehlt wird

Vor dem Senden: ein abgebrochener Download ist trotzdem ein Abruf. Nicht gezaehlt
werden HEAD-Anfragen und fortgesetzte Downloads (Range ab einem Byte > 0) -- sonst
zaehlte ein wackliges Netz eine Fassung mehrfach.
"""
from __future__ import annotations

import hashlib
import json
import os
import threading
import time
from datetime import datetime, timezone
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
# Ueberschreibbar fuer Tests: ein Pruefserver soll nie in die echten Zahlen schreiben.
DATEI = Path(os.environ.get("FORZA_DOWNLOADS_FILE") or WORKSPACE / "data" / "runtime" / "downloads.json")
WERKZEUGE = {"haptics": "FH Companion (app)", "tool": "Scan tool (contributors)"}

_schloss = threading.Lock()


def herkunft(user_agent: str | None) -> str:
    ua = (user_agent or "").strip()
    if not ua or ua.startswith(("FHCompanion", "ForzaGripHaptics")):
        return "app"
    if ua.startswith("Mozilla"):
        return "browser"
    return "other"


def zaehlt(method: str, range_header: str | None) -> bool:
    """HEAD und fortgesetzte Downloads zaehlen nicht."""
    if method.upper() != "GET":
        return False
    r = (range_header or "").strip().lower()
    return not r or r.startswith("bytes=0-")


def fassung_des_pakets(paket: Path) -> dict:
    """Fassung der App: aus der Metadatei neben dem Paket (Bau-Kennung, Datum, Name)."""
    meta = paket.with_name(paket.name + ".meta.json")
    try:
        m = json.loads(meta.read_text(encoding="utf-8"))
        return {"id": str(m.get("build") or "?"), "name": str(m.get("name") or paket.name),
                "built": str(m.get("built_at") or "")}
    except (OSError, ValueError):
        st = paket.stat()
        return {"id": f"{int(st.st_mtime)}", "name": paket.name,
                "built": datetime.fromtimestamp(st.st_mtime, timezone.utc).isoformat()}


def fassung_aus_bytes(daten: bytes, name: str) -> dict:
    """Fassung des Werkzeugs: es hat keine Bau-Kennung, also sein Inhalt."""
    return {"id": hashlib.sha256(daten).hexdigest()[:12], "name": name, "built": ""}


def note(werkzeug: str, fassung: dict, user_agent: str | None, now: float | None = None) -> None:
    jetzt = datetime.fromtimestamp(time.time() if now is None else now, timezone.utc)
    stempel = jetzt.isoformat(timespec="seconds")
    tag = jetzt.date().isoformat()
    woher = herkunft(user_agent)
    with _schloss:
        try:
            d = json.loads(DATEI.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            d = {"since": tag, "tools": {}}
        f = d.setdefault("tools", {}).setdefault(werkzeug, {}).setdefault(
            fassung["id"], {"name": fassung.get("name", ""), "built": fassung.get("built", ""),
                            "first": stempel, "total": 0, "by": {}, "days": {}})
        f["total"] = int(f.get("total", 0)) + 1
        f["last"] = stempel
        f["by"][woher] = int(f["by"].get(woher, 0)) + 1
        f["days"][tag] = int(f["days"].get(tag, 0)) + 1
        try:
            DATEI.parent.mkdir(parents=True, exist_ok=True)
            teil = DATEI.with_suffix(".part")
            teil.write_text(json.dumps(d, separators=(",", ":")), encoding="utf-8")
            os.replace(teil, DATEI)
        except OSError:
            pass


def summary() -> dict:
    with _schloss:
        try:
            d = json.loads(DATEI.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            d = {"since": None, "tools": {}}
    reihen = []
    for werkzeug, fassungen in (d.get("tools") or {}).items():
        for fid, f in fassungen.items():
            reihen.append({"tool": werkzeug, "toolName": WERKZEUGE.get(werkzeug, werkzeug),
                           "version": fid, **f})
    reihen.sort(key=lambda r: (r["tool"], r.get("first") or ""), reverse=True)
    return {"since": d.get("since"), "versions": reihen}
