"""Der Namensvergleich des Navigators -- gegen echte Lesungen aus dem Spiel.

    python scripts/test_route_name_compare.py     (braucht powershell)

## Wofuer der Vergleich da ist

`Select-RouteByIndex` zaehlt beim Anfahren einer Karussellposition **bestaetigte
Schritte** statt Tastendruecke: nach jedem RIGHT wird gelesen, und wenn sich der
Streckenname nicht geaendert hat, kam der Druck nicht an und wird wiederholt. Das
ist noetig, weil Druecke verloren gehen -- am 2026-09-13 im Protokoll belegt, der
erste Druck nach dem Anker.

Damit haengt alles daran, ob zwei Lesungen DIESELBE Strecke zeigen. Und das ist
schwerer, als es klingt:

**Laufschrift.** Lange Namen wandern durch ihr Feld. Dieselbe Position, fuenf
Lesungen hintereinander:

    'Tateyama Alpine Cross-Coun' -> 'rateyama Alpine Cross-Coun'
    -> 'ateyama Alpine Cross-Count' -> 'iteyama Alpin Cross-Countr'

Ein wandernder Text aendert sich, ohne dass sich die Position aendert. Wer das fuer
einen Schritt haelt, zaehlt zu frueh weiter und landet eins zu kurz.

**Der gemeinsame Namensteil.** JEDER Name endet auf "Cross-Country". Ein Vergleich
ueber den ganzen Namen findet darin genug Gemeinsamkeit, um 'Izu Cross-Country' und
'Temple Cross-Country' fuer dieselbe Strecke zu halten -- so geschehen, fuenf von
sieben Pruefungen fielen darueber. Verglichen wird deshalb nur der Ort davor.

**Benachbarte Positionen.** 'Shimanoyama' (15) und 'Yahikoyama' (14) stehen
nebeneinander und teilen sich 'oyama'. Genau solche Paare muss der Vergleich
trennen, denn nur zwischen Nachbarn wird ueberhaupt gezaehlt.

## Die Faelle unten sind ALLE echt

Jede Zeichenkette stammt aus `data/runtime/navigation/*/navigator.log`. Ausgedachte
Faelle haetten den Fehler nicht gefunden -- die OCR beschaedigt Namen auf Weisen,
die man sich nicht ausdenkt ('Cross-Cdufitry', 'Cras-eouniry', '3ity Docks').
"""

from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
NAVIGATOR = WORKSPACE / "scripts" / "forza_navigator.ps1"

# (A, B, sollen sie als dieselbe Strecke gelten, Beschreibung)
FAELLE = [
    # --- dieselbe Strecke: Laufschrift und OCR-Schaden ---
    ("Tateyama Alpine Cross-Coun", "rateyama Alpine Cross-Coun", True,
     "Tateyama, ein Zeichen gewandert"),
    ("Tateyama Alpine Cross-Coun", "iteyama Alpin Cross-Countr", True,
     "Tateyama, weit gewandert"),
    ("ateyama Alpine Cross-Count", "tateyama Alpine Cross-Count", True,
     "Tateyama, Zeichen vorne dazu"),
    ("Edogawa Cross-Cdufitry Cire", "idogawa eras-country Cire", True,
     "Edogawa, schwerer OCR-Schaden"),
    ("City Doeksross-Country Ci", "3ity Docks Cross-Country Cil", True,
     "City Docks, schwerer OCR-Schaden"),
    ("Nangan Cros", "Nangan Cross-Country Circu", True,
     "Nangan, abgeschnitten gegen vollstaendig"),
    ("Stadium Cross-Country Circt", "Stadium Cross-Country Circu", True,
     "Stadium, zwei Lesungen"),

    # --- verschiedene Strecken, und zwar BENACHBARTE ---
    ("The Titan", "Legend Island Cross-Countr)", False, "Positionen 0 und 1"),
    ("Oka Cross-Country Circuit", "Edogawa Cross-Country Circ", False,
     "Positionen 4 und 5"),
    ("Edogawa Cross-Country Circ", "City Docks Cross-Country Ci", False,
     "Positionen 5 und 6"),
    ("City Docks Cross-Country Ci", "Stadium Cross-Country Circu", False,
     "Positionen 6 und 7"),
    ("Stadium Cross-Country Circu", "Nangan Cross-Country Circu", False,
     "Positionen 7 und 8"),
    ("Nangan Cross-Country Circu", "Soni Highlands Cross-Counti", False,
     "Positionen 8 und 9"),
    ("Soni Highlands Cross-Counti", "Ruriko-ji Cross-Country", False,
     "Positionen 9 und 10"),
    ("Takashiro Cross-Country", "Tateyama Alpine Cross-Coun", False,
     "Positionen 11 und 12"),
    ("Yahikoyama Cross-Country", "Shimanoyama Cross-Country", False,
     "Positionen 14 und 15 -- teilen sich 'oyama'"),
    ("Shimanoyama Cross-Country", "Shinjuku Gyoen Cross-", False,
     "Positionen 15 und 16 -- teilen sich 'Shi'"),

    # --- verschiedene Strecken, nicht benachbart ---
    ("Naruo Cross-Country Circuit", "Nangan Cross-Country Circu", False,
     "Naruo und Nangan -- teilen sich 'Na'"),
    ("Izu Cross-Country", "Temple Cross-Country", False,
     "kurze Namen, nur die Endung gemeinsam"),
]

SKRIPT = """
$ErrorActionPreference = 'Stop'
$text = Get-Content -LiteralPath $env:NAVPFAD -Raw
$von = $text.IndexOf('function Get-RouteHead {')
$bis = $text.IndexOf('function Get-SettledRoute {')
if ($von -lt 0 -or $bis -lt 0) { throw 'Die Funktionen stehen nicht im Navigator.' }
Invoke-Expression $text.Substring($von, $bis - $von)

$roh = [Console]::In.ReadToEnd()
$faelle = $roh | ConvertFrom-Json
$raus = @()
foreach ($f in $faelle) { $raus += [bool](Test-SameRoute -A $f[0] -B $f[1]) }
[Console]::Out.Write((ConvertTo-Json -InputObject @($raus) -Compress))
"""


def main() -> int:
    if not NAVIGATOR.exists():
        print("Fehlt: " + NAVIGATOR.as_posix())
        return 1

    import os
    umgebung = dict(os.environ, NAVPFAD=str(NAVIGATOR))
    lauf = subprocess.run(
        ["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", SKRIPT],
        input=json.dumps([[a, b] for a, b, _, _ in FAELLE]),
        capture_output=True, text=True, env=umgebung)
    if lauf.returncode != 0:
        print("PowerShell ist gescheitert:\n" + (lauf.stdout + lauf.stderr)[-2000:])
        return 1
    try:
        ergebnisse = json.loads(lauf.stdout.strip().splitlines()[-1])
    except (ValueError, IndexError):
        print("Unbrauchbare Ausgabe:\n" + lauf.stdout[-1000:] + lauf.stderr[-1000:])
        return 1

    fehler = 0
    letzte_gruppe = None
    for (a, b, soll, was), ist in zip(FAELLE, ergebnisse):
        gruppe = "dieselbe Strecke" if soll else "verschiedene Strecken"
        if gruppe != letzte_gruppe:
            print("\n" + gruppe + ":")
            letzte_gruppe = gruppe
        ok = bool(ist) == soll
        fehler += not ok
        print("  %s %-46s %r / %r" % ("ok  " if ok else "FEHL", was, a[:26], b[:26]))

    print()
    if fehler:
        print("%d von %d Faellen falsch beurteilt." % (fehler, len(FAELLE)))
        return 1
    print("alle %d Faelle richtig beurteilt -- alles bestanden" % len(FAELLE))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
