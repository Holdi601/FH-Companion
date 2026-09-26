"""Laeuft alles? Eine Abfrage statt zehn.

    python scripts/status.py
    python scripts/status.py --kurz        nur die Ampeln

## Warum es das gibt

Die Frage "funktionieren nun alle Features?" ist zweimal gestellt worden, und beide
Male habe ich sie mit einer Handvoll einzelner Befehle beantwortet -- Seite abrufen,
Paket vergleichen, Fortschrittsdatei lesen, VM ansehen. Das ist jedesmal dieselbe
Arbeit, und sie wird nur richtig beantwortet, wenn man an alles denkt.

Geprueft wird von AUSSEN, wo es geht: die Seite ueber die oeffentliche Adresse und
nicht ueber 127.0.0.1, denn die Frage lautet, ob die App draussen ankommt. Eine
Pruefung, die den kurzen Weg nimmt, prueft den Weg nicht, um den es geht.

Rueckgabe 0, wenn nichts rot ist.
"""

from __future__ import annotations

import argparse
import json
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime, timedelta
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
GRUEN, GELB, ROT = "ok  ", "warn", "FEHL"


class Bericht:
    def __init__(self) -> None:
        self.zeilen: list[tuple[str, str, str]] = []

    def sag(self, ampel: str, titel: str, text: str = "") -> None:
        self.zeilen.append((ampel, titel, text))

    def rot(self) -> int:
        return sum(1 for a, _, _ in self.zeilen if a == ROT)

    def gelb(self) -> int:
        return sum(1 for a, _, _ in self.zeilen if a == GELB)


def hole(url: str, timeout: int = 25) -> tuple[int, bytes]:
    try:
        with urllib.request.urlopen(url, timeout=timeout) as antwort:
            return antwort.status, antwort.read()
    except urllib.error.HTTPError as fehler:
        return fehler.code, b""
    except Exception:
        return 0, b""


def cfg() -> dict:
    try:
        return json.loads((WORKSPACE / "config" / "gnas_deploy.json")
                          .read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        return {}


def pruefe_adresse(b: Bericht, einstellung: dict) -> None:
    url = einstellung.get("public_url") or ""
    name = url.split("//", 1)[-1].split(":", 1)[0].split("/", 1)[0]
    if not name:
        b.sag(GELB, "Adresse", "keine oeffentliche Adresse eingestellt")
        return
    try:
        zeigt_auf = socket.gethostbyname(name)
    except OSError as fehler:
        b.sag(ROT, "Adresse", "%s laesst sich nicht aufloesen (%s)" % (name, fehler))
        return
    _, roh = hole("https://api.ipify.org", 15)
    hier = roh.decode("ascii", "replace").strip()
    if not hier:
        b.sag(GELB, "Adresse", "%s -> %s (eigene Adresse unbekannt)" % (name, zeigt_auf))
    elif hier == zeigt_auf:
        b.sag(GRUEN, "Adresse", "%s -> %s" % (name, zeigt_auf))
    else:
        b.sag(ROT, "Adresse",
              "%s zeigt auf %s, dieser Anschluss ist %s -- die App findet den "
              "Server nicht" % (name, zeigt_auf, hier))


def pruefe_seite(b: Bericht, einstellung: dict) -> dict:
    url = einstellung.get("public_url")
    if not url:
        b.sag(GELB, "Seite", "keine Adresse")
        return {}
    begonnen = time.time()
    code, _ = hole(url + "/", 40)
    dauer = time.time() - begonnen
    if code == 200:
        b.sag(GRUEN, "Seite", "HTTP 200 in %.2f s ueber %s" % (dauer, url))
    else:
        b.sag(ROT, "Seite", "HTTP %s von %s" % (code or "keine Antwort", url))
        return {}

    code, roh = hole(url + "/api/summary", 30)
    if code != 200:
        b.sag(GELB, "Datensatz", "kein /api/summary")
        return {}
    try:
        zus = json.loads(roh.decode("utf-8", "replace"))
    except ValueError:
        b.sag(GELB, "Datensatz", "Antwort nicht lesbar")
        return {}
    b.sag(GRUEN, "Datensatz",
          "%s Boards, %s Runden, %s von %s Autos benannt"
          % (zus.get("boards", "?"), f"{zus.get('keptLaps', 0):,}".replace(",", "."),
             zus.get("namedCars", "?"), zus.get("cars", "?")))
    return zus


def pruefe_paket(b: Bericht, einstellung: dict) -> None:
    """Liefert der Server genau das Paket, das hier zuletzt gebaut wurde?"""
    hier = WORKSPACE / "dist" / "fh-companion-latest.zip.meta.json"
    try:
        meins = json.loads(hier.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        b.sag(GELB, "Paket", "lokal kein Beipackzettel -- nie gebaut?")
        return
    url = einstellung.get("public_url")
    code, roh = hole((url or "") + "/api/haptics", 30)
    if code != 200:
        b.sag(ROT, "Paket", "der Server nennt kein Paket (HTTP %s) -- der "
                            "Selbstaktualisierer der App bliebe still" % (code or "-"))
        return
    try:
        drueben = json.loads(roh.decode("utf-8", "replace"))
    except ValueError:
        b.sag(ROT, "Paket", "Antwort nicht lesbar")
        return
    if drueben.get("build") == meins.get("build"):
        b.sag(GRUEN, "Paket", "Server und hier: %s" % meins.get("build"))
    else:
        b.sag(GELB, "Paket",
              "Server hat %s (%s), hier liegt %s (%s) -- wird beim naechsten "
              "Ausliefern nachgezogen"
              % (drueben.get("build"), drueben.get("built_at", "?")[:16],
                 meins.get("build"), meins.get("built_at", "?")[:16]))


def pruefe_abdeckung(b: Bericht) -> None:
    """Wie viele Bestenlisten sind erfasst -- und was fehlt noch."""
    KL = ["D", "C", "B", "A", "S1", "S2", "R"]
    # ROAD RACING HEISST ANDERS. Die Datei stammt aus dem ersten Durchlauf, der
    # noch "baseline20k" hiess -- geraten haette man das nicht, und mein erster
    # Entwurf dieser Pruefung hat die Kategorie prompt unterschlagen und 427 von
    # 441 gemeldet statt 588 von 602.
    plan = {
        "Road Racing": ("baseline20k_progress.json", 23),
        "Street Racing": ("street_racing_progress.json", 15),
        "Cross-Country": ("cross-country_progress.json", 19),
        "Dirt Racing": ("dirt_racing_progress.json", 21),
        "Touge": ("touge_progress.json", 5),
        "Drag Racing": ("drag_racing_progress.json", 3),
    }
    gesamt = erledigt = 0
    teile = []
    for name, (datei, strecken) in plan.items():
        pfad = WORKSPACE / "data" / "runtime" / "overnight" / datei
        try:
            paare = json.loads(pfad.read_text(encoding="utf-8-sig"))["pairs"]
        except (OSError, ValueError, KeyError):
            continue
        gesamt += strecken * len(KL)
        erledigt += len(paare)
        teile.append("%s %d" % (name.split()[0], len(paare)))
    if gesamt == 0:
        b.sag(GELB, "Abdeckung", "keine Fortschrittsdateien gefunden")
        return
    b.sag(GRUEN, "Abdeckung", "%d von %d Boards (%.1f %%) -- %s"
          % (erledigt, gesamt, 100.0 * erledigt / gesamt, ", ".join(teile)))


def pruefe_sweep(b: Bericht) -> None:
    """Laeuft gerade ein Scan, und wann hat er zuletzt etwas gesagt?"""
    try:
        # NUR python.exe -- sonst findet die Abfrage SICH SELBST.
        #
        # Die Befehlszeile dieser PowerShell enthaelt das Suchwort, also zaehlt ein
        # ungefiltertes `Where-Object { $_.CommandLine -match ... }` den eigenen
        # Prozess mit. Am 2026-09-14 meldete diese Zeile darum "Scan laeuft",
        # waehrend die VM bereits ausgeschaltet war -- und derselbe Fehler hatte
        # mich eine halbe Stunde vorher schon bei einem Deploy-Zaehler in die Irre
        # gefuehrt, der hartnaeckig 4 statt 0 anzeigte.
        lauf = subprocess.run(
            ["powershell.exe", "-NoProfile", "-Command",
             "@(Get-CimInstance Win32_Process -Filter \"Name='python.exe'\" | "
             "Where-Object { $_.CommandLine -match 'ocr_board_sweep' }).Count"],
            capture_output=True, text=True, timeout=90)
        laeuft = (lauf.stdout or "").strip().isdigit() and int(lauf.stdout.strip()) > 0
    except Exception:
        laeuft = False

    neuste, wann = None, None
    ordner = WORKSPACE / "data" / "runtime" / "overnight"
    for p in ordner.glob("*.out"):
        m = datetime.fromtimestamp(p.stat().st_mtime)
        if wann is None or m > wann:
            neuste, wann = p, m
    alter = "" if wann is None else " (letzte Zeile vor %s)" % kurz_dauer(
        datetime.now() - wann)
    if laeuft:
        b.sag(GRUEN, "Scan", "laeuft%s" % alter)
    else:
        b.sag(GELB, "Scan", "laeuft nicht%s" % alter)


def pruefe_vm(b: Bericht) -> None:
    try:
        lauf = subprocess.run(
            ["powershell.exe", "-NoProfile", "-Command",
             "(Get-VM ForzaScrapeVM).State"],
            capture_output=True, text=True, timeout=90)
        zustand = (lauf.stdout or "").strip()
    except Exception:
        zustand = ""
    if zustand == "Running":
        b.sag(GRUEN, "VM", "laeuft")
    elif zustand:
        b.sag(GELB, "VM", zustand)
    else:
        b.sag(GELB, "VM", "nicht erreichbar")


def pruefe_freie_welt(b: Bericht) -> None:
    """Wie viele Start-Ziel-Linien kennt die App fuer die freie Welt?"""
    sys.path.insert(0, str(WORKSPACE / "server"))
    from local_settings import app_data
    wurzel = app_data() / "laps"
    if not wurzel.exists():
        b.sag(GELB, "Freie Welt", "kein Rundenbestand -- noch nichts aufgezeichnet")
        return
    linien = benannt = 0
    for d in wurzel.glob("course_*"):
        try:
            notiz = json.loads((d / "course.json").read_text(encoding="utf-8-sig"))
        except (OSError, ValueError):
            continue
        if notiz.get("StartX", 0) or notiz.get("StartZ", 0):
            linien += 1
            if (notiz.get("Name") or "").strip():
                benannt += 1
    if linien == 0:
        b.sag(GELB, "Freie Welt", "keine Start-Ziel-Linie -- eine Strecke einmal fahren")
    else:
        b.sag(GRUEN, "Freie Welt", "%d Start-Ziel-Linie(n), davon %d benannt"
              % (linien, benannt))


def kurz_dauer(d: timedelta) -> str:
    s = int(d.total_seconds())
    if s < 90:
        return "%d s" % s
    if s < 5400:
        return "%d min" % (s // 60)
    return "%.1f h" % (s / 3600.0)


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--kurz", action="store_true", help="nur die Ampeln")
    args = p.parse_args(argv)

    einstellung = cfg()
    b = Bericht()
    pruefe_adresse(b, einstellung)
    pruefe_seite(b, einstellung)
    pruefe_paket(b, einstellung)
    pruefe_abdeckung(b)
    pruefe_sweep(b)
    pruefe_vm(b)
    pruefe_freie_welt(b)

    breite = max(len(t) for _, t, _ in b.zeilen)
    for ampel, titel, text in b.zeilen:
        if args.kurz and ampel == GRUEN:
            print("[%s] %s" % (ampel, titel))
        else:
            print("[%s] %-*s  %s" % (ampel, breite, titel, text))

    print()
    if b.rot():
        print("%d Sache(n) sind kaputt." % b.rot())
    elif b.gelb():
        print("Nichts kaputt, %d Hinweis(e)." % b.gelb())
    else:
        print("Alles in Ordnung.")
    return 1 if b.rot() else 0


if __name__ == "__main__":
    sys.exit(main())
