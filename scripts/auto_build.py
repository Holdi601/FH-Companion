"""Neue Scans selbsttaetig in den Datensatz bringen (seit 2026-10-12).

    python scripts/auto_build.py              # einmal nachsehen (so ruft die Aufgabenplanung)
    python scripts/auto_build.py --einrichten # die Aufgabe "FH Companion\\Auto build" anlegen

## Warum

Der Datensatz der Seite wird auf diesem Rechner gebaut -- hier liegen alle Laeufe, auf dem
Server nur die eingereichten. Bis 2026-10-12 geschah das nur, wenn jemand von Hand auslieferte:
zwei Scans eines Freundes standen in der Verwaltung, aber nicht auf der Seite.

## Was ein Lauf tut

1. Den Server fragen (GET /api/admin/build, mit dem Admin-Geheimnis unterschrieben): warten
   angenommene Scans, hat sich die Sichtbarkeitsliste geaendert, hat der Admin "Rebuild now"
   gedrueckt?
2. Nichts davon: fertig. Sonst bauen -- aber selbsttaetig NUR, wenn Forza nicht laeuft
   (gebaut wird nicht, waehrend gespielt wird). Ein angeforderter Bau laeuft sofort, mit
   niedrigster Prioritaet.
3. Bauen heisst: `deploy_gnas.py --apply --nur-datensatz` -- holen, bauen, nur Seite und
   Datensatz schicken. Nie Code, nie ein App-Paket.
4. Dem Server melden, wie es ausging (data/runtime/auto_build.json), und die Anforderung
   loeschen, wenn sie nicht juenger ist als der Lauf.

Ohne Fenster: die Aufgabe startet pythonw, und jeder Unterprozess bekommt CREATE_NO_WINDOW.
"""

from __future__ import annotations

import argparse
import hashlib
import hmac
import json
import os
import ssl
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(WORKSPACE / "scripts"))
import deploy_gnas as deploy  # noqa: E402

# KEIN FENSTER, NIE: unter pythonw oeffnete jedes ssh/scp des Deploy-Moduls sonst kurz eine
# Konsole -- auch mitten im Spiel. Hier fuer jeden Unterprozess dieses Laufs.
_run = subprocess.run


def _ohne_fenster(*a, **k):
    k["creationflags"] = k.get("creationflags", 0) | 0x08000000
    return _run(*a, **k)


subprocess.run = _ohne_fenster

PROTOKOLL = WORKSPACE / "data" / "runtime" / "auto_build.log"
SPERRE = WORKSPACE / "data" / "runtime" / "auto_build.lock"
MELDUNG = WORKSPACE / "data" / "runtime" / "auto_build.json"
AUFGABE = r"FH Companion\Auto build"
OHNE_FENSTER = 0x08000000
NIEDRIG = 0x00000040  # IDLE_PRIORITY_CLASS
UNTER_NORMAL = 0x00004000  # BELOW_NORMAL_PRIORITY_CLASS


def log(text: str) -> None:
    PROTOKOLL.parent.mkdir(parents=True, exist_ok=True)
    zeile = time.strftime("%Y-%m-%d %H:%M:%S") + " " + text
    with PROTOKOLL.open("a", encoding="utf-8") as f:
        f.write(zeile + "\n")
    # Das Protokoll klein halten: die letzten 2.000 Zeilen.
    try:
        zeilen = PROTOKOLL.read_text(encoding="utf-8").splitlines()
        if len(zeilen) > 2500:
            PROTOKOLL.write_text("\n".join(zeilen[-2000:]) + "\n", encoding="utf-8")
    except OSError:
        pass


def server_und_geheimnis() -> tuple[str, str]:
    lokal = json.loads((WORKSPACE / "config" / "local.json").read_text(encoding="utf-8-sig"))
    schluessel = json.loads((WORKSPACE / "config" / "contrib_keys.json").read_text(encoding="utf-8-sig"))
    return str(lokal["server"]).rstrip("/"), str(schluessel["admin"])


def admin_aufruf(methode: str, pfad: str, koerper: bytes = b"") -> dict:
    server, geheim = server_und_geheimnis()
    stamp = str(int(time.time()))
    nachricht = "\n".join([methode, pfad, stamp, hashlib.sha256(koerper).hexdigest()])
    sig = hmac.new(geheim.encode("utf-8"), nachricht.encode("utf-8"), hashlib.sha256).hexdigest()
    anfrage = urllib.request.Request(server + pfad, data=koerper if methode == "POST" else None, method=methode,
                                     headers={"X-Forza-Timestamp": stamp, "X-Forza-Signature": sig,
                                              "Content-Type": "application/json"})
    # Das Geheimnis reist nie mit, nur die Unterschrift. Ein Zertifikat, das sich nicht
    # pruefen laesst (eigene Stelle), haelt den Lauf darum nicht auf.
    for kontext in (ssl.create_default_context(), ssl._create_unverified_context()):
        try:
            with urllib.request.urlopen(anfrage, timeout=30, context=kontext) as a:
                return json.loads(a.read().decode("utf-8"))
        except ssl.SSLError:
            continue
        except urllib.error.URLError as e:
            if isinstance(getattr(e, "reason", None), ssl.SSLError):
                continue
            raise
    raise RuntimeError("the server's certificate could not be used")


def forza_laeuft() -> bool:
    r = subprocess.run(["tasklist", "/FI", "IMAGENAME eq forzahorizon6.exe", "/NH"],
                       capture_output=True, text=True, creationflags=OHNE_FENSTER)
    return "forzahorizon6.exe" in r.stdout.lower()


def _lebt(sperre: Path) -> bool:
    try:
        pid = int(sperre.read_text().strip())
    except (OSError, ValueError):
        return False
    r = subprocess.run(["tasklist", "/FI", f"PID eq {pid}", "/NH"], capture_output=True, text=True,
                       creationflags=OHNE_FENSTER)
    return str(pid) in r.stdout


def schon_am_laufen() -> bool:
    """Ein anderer Lauf -- oder ein Deploy von Hand (deploy_gnas.SPERRE) -- ist gerade dabei."""
    return _lebt(SPERRE) or _lebt(deploy.SPERRE)


def melden(eintrag: dict) -> None:
    """Das Ergebnis auf den Server legen (data/runtime/auto_build.json)."""
    MELDUNG.write_text(json.dumps(eintrag, ensure_ascii=False), encoding="utf-8")
    try:
        deploy.schicken(deploy.einstellung(), ["data/runtime/auto_build.json"], apply=True)
    except Exception as e:
        log("Ergebnis nicht gemeldet: %s" % e)


def anforderung_loeschen(bis: float) -> None:
    cfg = deploy.einstellung()
    datei = cfg["remote_root"] + "/data/runtime/build_request.json"
    # Nur, wenn sie nicht juenger ist als der Lauf: wer waehrenddessen drueckt, bekommt noch einen.
    befehl = ("python3 -c \"import json,os; p='%s'; "
              "d=json.load(open(p)) if os.path.exists(p) else None; "
              "os.remove(p) if d and float(d.get('at') or 0) <= %f else None\"" % (datei, bis))
    deploy.fern(cfg, befehl)


def lauf() -> int:
    try:
        stand = admin_aufruf("GET", "/api/admin/build")
    except Exception as e:
        log("Server nicht erreicht: %s" % e)
        return 1
    wartend = stand.get("pending_scans") or []
    sicht = bool(stand.get("visibility_changed"))
    angefordert = stand.get("request")
    if not wartend and not sicht and not angefordert:
        return 0
    spielt = forza_laeuft()
    if spielt and not angefordert:
        log("%d Scan(s) warten, Forza laeuft -- spaeter" % len(wartend))
        return 0
    if schon_am_laufen():
        log("ein anderer Lauf oder Deploy ist dabei -- spaeter")
        return 0
    SPERRE.write_text(str(os.getpid()))
    beginn = time.time()
    was = ", ".join(filter(None, [
        "%d new scan(s)" % len(wartend) if wartend else "",
        "visibility changed" if sicht else "",
        "requested" if angefordert else "",
    ]))
    log("Bau: " + was + (" (Forza laeuft, niedrigste Prioritaet)" if spielt else ""))
    try:
        r = subprocess.run([sys.executable.replace("pythonw.exe", "python.exe"),
                            str(WORKSPACE / "scripts" / "deploy_gnas.py"), "--apply", "--nur-datensatz"],
                           cwd=str(WORKSPACE), capture_output=True, text=True, encoding="utf-8", errors="replace",
                           creationflags=OHNE_FENSTER | (NIEDRIG if spielt else UNTER_NORMAL))
        ok = r.returncode == 0
        ausgabe = (r.stdout or "") + (r.stderr or "")
        for zeile in ausgabe.splitlines()[-40:]:
            log("  | " + zeile)
        letzte = [z.strip() for z in ausgabe.splitlines() if z.strip()]
        meldung = "" if ok else (letzte[-1] if letzte else "exit %d" % r.returncode)
    except Exception as e:
        ok, meldung = False, str(e)
    dauer = round(time.time() - beginn)
    log("Bau %s nach %d s" % ("fertig" if ok else "FEHLGESCHLAGEN", dauer))
    melden({"finished": time.time(), "ok": ok, "seconds": dauer, "what": was, "message": meldung[:300]})
    if ok and angefordert:
        anforderung_loeschen(beginn)
    try:
        SPERRE.unlink()
    except OSError:
        pass
    return 0 if ok else 1


def einrichten() -> int:
    pythonw = Path(sys.executable).with_name("pythonw.exe")
    befehl = ["schtasks", "/Create", "/F", "/TN", AUFGABE, "/SC", "MINUTE", "/MO", "5",
              "/TR", '"%s" "%s"' % (pythonw, Path(__file__).resolve())]
    r = subprocess.run(befehl, capture_output=True, text=True)
    print((r.stdout or r.stderr).strip())
    return r.returncode


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--einrichten", action="store_true", help="die Aufgabe in der Aufgabenplanung anlegen")
    args = ap.parse_args(argv)
    if args.einrichten:
        return einrichten()
    try:
        return lauf()
    except Exception as e:
        log("Fehler: %s" % e)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
