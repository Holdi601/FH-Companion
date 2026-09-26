"""Die Teilenamen aus dem Speicher des laufenden Spiels holen.

    python scripts/extract_part_names.py            zeigen
    python scripts/extract_part_names.py --apply    nach config/fh6_part_names.json

## Warum es das gibt

Die Garage nennt verbaute Teile nur als NUMMER (`Transmission = 2102007`). Der
Katalog, der daraus einen Namen machen wuerde, liegt im Speicher nur als
Seiten-Zwischenspeicher und kommt als Datenbank nicht heraus -- am 2026-09-14
nachgeprueft: kein einziges Abbild traegt eine `List_Upgrade*`-Tabelle.

Die NAMEN selbst liegen aber sehr wohl da, als null-getrennter Zeichenketten-Vorrat:

    Stock Transmission  Street Transmission  Sport Transmission  Race Transmission
    Stock Clutch  Street Clutch  Sport Clutch  Race Clutch
    Rally Diff  Drift Diff  Race Transmission: 6 Speed ... 10 Speed

## Was sich daraus belegen laesst -- und was nicht

**Belegt:** die letzten drei Stellen der Teilenummer sind die Ausbaustufe, und die
Stufen 0 bis 3 sind Stock, Street, Sport und Race. Gemessen an 587 Autos: bei
Kupplung, Bremsen, Nockenwelle, Auspuff, Ansaugung, Schwungrad, Ladeluftkuehler,
Stabilisator und Antriebsstrang kommen GENAU die Stufen 0..3 vor -- und der Vorrat
enthaelt zu jeder dieser Teilearten GENAU die vier Namen.

**Nicht belegt:** was die Stufen ab 4 bedeuten. Beim Getriebe gibt es zehn Stufen
(0..9) und zehn Namen, beim Reifengemisch dreizehn Stufen und ebenso viele Namen --
die Reihenfolge der Sonderstufen steht aber nirgends im Speicher neben der Nummer.
Gepruefte Sackgasse am 2026-09-14: die Namen liegen in einem Zeiger-Array, und neben
keiner Teilenummer steht ein Zeiger darauf.

Darum schreibt dieses Skript den VORRAT heraus und behauptet keine Zuordnung, die es
nicht gibt.

## Zwei Fallen beim Einlesen, beide selbst hineingetappt

1. **An den Nullbytes TRENNEN, nicht mit einem Regex suchen.** Ein Muster wie
   `\\x00(...)\\x00` ueberspringt jede zweite Zeichenkette, weil aufeinanderfolgende
   Eintraege sich ein Nullbyte teilen und der Treffer es verbraucht. Ergebnis war
   eine halbe Liste, in der zu KEINER Teileart alle vier Stufen standen.
2. **Der Vorrat grenzt an andere Vorraete.** Mit einem 64-KB-Fenster stand in der
   Ausgabe eine halbe Vornamenssammlung ("Aaliyah", "Abigail", ...). Ein Sonderteil
   muss darum eines der bekannten Teilewoerter enthalten.
"""

from __future__ import annotations

import argparse
import ctypes
import json
import subprocess
import sys
from ctypes import wintypes
from pathlib import Path

HIER = Path(__file__).resolve().parent
sys.path.insert(0, str(HIER))
import dump_sqlite_from_memory as dm  # noqa: E402

ZIEL = HIER.parent / "config" / "fh6_part_names.json"

# Ein Name, von dem sicher ist, dass er im Vorrat steht -- von dort aus wird gelesen.
ANKER = b"Stock Transmission\x00"

# So weit vor und hinter dem Anker wird eingesammelt.
FENSTER = 24 << 10

# Die Stufennamen, in der Reihenfolge, in der sie im Spiel stehen.
STUFEN = ["Stock", "Street", "Sport", "Race"]

# Woraus ein Teilename bestehen darf, wenn er nicht mit einer Stufe beginnt.
TEILEWOERTER = {
    "diff", "turbo", "supercharger", "conversion", "kit", "bars", "compound",
    "width", "size", "style", "bumper", "wing", "skirts", "bonnet", "hood",
    "restrictors", "intercooler", "swap", "aspirated", "tyre", "tire", "speed",
    "transmission", "clutch", "flywheel", "driveline", "brakes", "dampers",
    "springs", "camshaft", "cams", "valves", "exhaust", "intake", "block",
    "reduction", "cage", "roll", "weight", "engine", "motor", "battery",
}


def spielprozess() -> int | None:
    try:
        aus = subprocess.run(
            ["powershell.exe", "-NoProfile", "-Command",
             "(Get-Process forzahorizon6 -ErrorAction SilentlyContinue).Id"],
            capture_output=True, text=True, timeout=120).stdout.strip()
        return int(aus.splitlines()[0]) if aus else None
    except Exception:
        return None


def vorrat(pid: int) -> list[str]:
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.OpenProcess.restype = wintypes.HANDLE
    handle = kernel32.OpenProcess(0x0010 | 0x0400, False, pid)
    if not handle:
        raise SystemExit("Der Spielprozess laesst sich nicht oeffnen.")

    for base, size in dm.readable_regions(handle):
        offset = 0
        while offset < size:
            laenge = min(8 << 20, size - offset)
            daten = dm.read_memory(handle, base + offset, laenge)
            if not daten:
                offset += laenge
                continue
            at = daten.find(ANKER)
            if at >= 0:
                von = max(0, at - FENSTER)
                bis = min(len(daten), at + FENSTER)
                stuecke = daten[von:bis].split(b"\x00")
                return [s.decode("latin-1") for s in stuecke
                        if 3 <= len(s) <= 80
                        and all(0x20 <= c <= 0x7e for c in s)]
            offset += laenge
    return []


def bauteile(namen: list[str]) -> dict[str, list[str]]:
    """Zu welchen Teilearten gibt es alle vier Stufennamen?"""
    menge = set(namen)
    aus: dict[str, list[str]] = {}
    for n in menge:
        for stufe in STUFEN:
            if not n.startswith(stufe + " "):
                continue
            rumpf = n[len(stufe) + 1:]
            reihe = ["%s %s" % (s, rumpf) for s in STUFEN]
            if all(r in menge for r in reihe):
                aus[rumpf] = reihe
    return aus


def sonderteile(namen: list[str], reihen: dict[str, list[str]]) -> list[str]:
    woerter = set(TEILEWOERTER)
    for rumpf in reihen:
        woerter |= {w.lower() for w in rumpf.split()}
    # NUR DIE VIER REINEN STUFENNAMEN AUSSCHLIESSEN, nicht alles, was mit einer
    # Stufe beginnt. "Race Transmission: 7 Speed" faengt mit "Race" an und ist
    # trotzdem ein Sonderteil -- mein erster Filter warf genau die fuenf Gangnamen
    # weg, also die, nach denen der Nutzer gefragt hatte.
    rein = {name for reihe in reihen.values() for name in reihe}
    return sorted(n for n in set(namen)
                  if n not in rein
                  and any(w.lower().strip("',:") in woerter for w in n.split()))


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--apply", action="store_true")
    args = p.parse_args(argv)

    pid = spielprozess()
    if pid is None:
        print("forzahorizon6.exe laeuft nicht -- ohne das Spiel gibt es keinen Vorrat.")
        return 1

    namen = vorrat(pid)
    if not namen:
        print("Der Namensvorrat war nicht zu finden.")
        return 1
    print("%d Zeichenketten im Fenster." % len(namen))

    reihen = bauteile(namen)
    print()
    print("%d Teileart(en) mit allen vier Stufen:" % len(reihen))
    for rumpf in sorted(reihen):
        print("   Stock/Street/Sport/Race %s" % rumpf)

    sonder = sonderteile(namen, reihen)
    print()
    print("%d weitere(r) Teilename(n):" % len(sonder))
    for n in sonder:
        print("   %s" % n)

    if args.apply:
        ZIEL.parent.mkdir(parents=True, exist_ok=True)
        ZIEL.write_text(json.dumps({
            "_comment": [
                "Aus dem Speicher des laufenden Spiels gelesen.",
                "tiers: Stufe 0..3 heissen Stock, Street, Sport, Race -- belegt an",
                "587 Autos: wo genau die Stufen 0..3 vorkommen, enthaelt der Vorrat",
                "genau diese vier Namen.",
                "Fuer Stufen ab 4 ist KEINE Zuordnung belegt; 'other' listet nur,",
                "welche Namen es sonst gibt.",
            ],
            "tiers": STUFEN,
            "byPart": {k: reihen[k] for k in sorted(reihen)},
            "other": sonder,
        }, indent=2, ensure_ascii=False), encoding="utf-8")
        print()
        print("geschrieben: %s" % ZIEL)
    else:
        print()
        print("Probelauf. Mit --apply wird %s geschrieben." % ZIEL.name)
    return 0


if __name__ == "__main__":
    sys.exit(main())
