"""Aufnahmen aufraeumen -- im Gast und auf dem Host -- und vor dem Volllaufen warnen.

    python scripts/prune_captures.py                  zeigen, was zu tun waere
    python scripts/prune_captures.py --apply          tun
    python scripts/prune_captures.py --pruefen        nur den freien Platz melden
    python scripts/prune_captures.py --apply --behalten 40

## Warum es das gibt

In der Nacht zum 2026-09-14 lief der Sweep eine Stunde lang BLIND: die Navigation
erreichte jede Strecke, die Aufnahme lieferte "nothing captured", und kein Zaehler
schlug an. Ich habe das zuerst fuer eine kaputte Anzeigesitzung gehalten und die VM
neu gestartet -- was kurz half, weil dabei etwas Temp frei wurde.

Die Ursache war banal: **die Platte der VM war voll.** Null Byte frei von 255 GB.
Und der groesste Posten waren unsere eigenen Rueckstaende --
`data\\navigation` mit 23 GB und `data\\frames` mit 23 GB, gewachsen seit dem
18. August, weil nichts sie je entfernt hat.

Der Navigator legt zu JEDER Anfahrt Bildschirmaufnahmen ab. Das ist richtig so --
ohne sie waere keiner der Navigationsfehler dieser Woche zu klaeren gewesen. Falsch
war nur, sie ewig zu behalten.

## Warum die Platzpruefung wichtiger ist als das Aufraeumen

Weil eine volle Platte sich als etwas ganz anderes zeigt. "nothing captured",
"kein Standbild bekommen", "Could not find the anchor route" -- alles Symptome, die
nach Anzeige, Navigation oder Spiel aussehen. Eine Stunde Fehlersuche an der
falschen Stelle. Eine Zeile "noch 0,4 GB frei" haette sie erspart.
"""

from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
GB = 1024 ** 3

# Ordner auf dem HOST, in denen je Lauf ein Unterordner entsteht.
HOST_ORDNER = [
    ("data/runtime/navigation", 40),
    ("data/runtime/scan_screen", 10),
]

# Unter dieser Grenze im Gast wird nicht mehr gescannt -- es hat keinen Zweck.
KRITISCH_GB = 8.0
# Unter dieser Grenze wird vor dem naechsten Board aufgeraeumt.
KNAPP_GB = 25.0

GAST_PRUEFEN = """
$c = Get-PSDrive C
[math]::Round($c.Free / 1073741824, 2)
"""

# Die Anzahl wird EINGESETZT statt uebergeben: ein `param()` in einem Skriptblock,
# der als Zeichenkette durch Invoke-Command geht, bekommt sein Argument nicht
# zuverlaessig -- der erste Versuch gab am 2026-09-14 eine leere Antwort zurueck.
GAST_RAEUMEN = """
$Behalten = __BEHALTEN__
$gb = 1073741824
$vorher = [math]::Round((Get-PSDrive C).Free / $gb, 2)
$n = 0
foreach ($paar in @(
    @{ Pfad = 'C:\\ForzaAutomation\\data\\navigation'; Behalten = $Behalten },
    @{ Pfad = 'C:\\ForzaAutomation\\data\\frames';     Behalten = 3 })) {
  if (-not (Test-Path $paar.Pfad)) { continue }
  $alt = Get-ChildItem $paar.Pfad -Directory -ErrorAction SilentlyContinue |
         Sort-Object LastWriteTime -Descending | Select-Object -Skip $paar.Behalten
  foreach ($d in $alt) {
    try { [System.IO.Directory]::Delete($d.FullName, $true); $n++ } catch {}
  }
}
$nachher = [math]::Round((Get-PSDrive C).Free / $gb, 2)
# DREI BLANKE ZEILEN und keine Zeichenkette mit Pipes.
#
# Hier stand "$n|$vorher|$nachher". Das ging nie durch: der Aufrufer verdoppelt
# jedes Anfuehrungszeichen zu `" -- innerhalb des Skriptblocks zerbricht damit das
# Zitat, und PowerShell liest die Pipes als Pipeline ("Expressions are only allowed
# as the first element of a pipeline"). Das Aufraeumen im Gast lief also seit jeher
# gar nicht, und gemeldet wurde nur "Antwort unbrauchbar: ''".
$n
$vorher
$nachher
"""


def gast(skript: str, vm: str = "ForzaScrapeVM", args: list | None = None) -> str:
    """Ein Stueck PowerShell im Gast laufen lassen. Leerer String heisst: ging nicht."""
    befehl = (
        "$c = [pscredential]::new('.\\admin', [Security.SecureString]::new()); "
        "Invoke-Command -VMName '%s' -Credential $c -ScriptBlock { %s } %s"
        % (vm, skript.replace('"', '`"'),
           ("-ArgumentList " + ",".join(str(a) for a in args)) if args else "")
    )
    try:
        lauf = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive",
                               "-Command", befehl],
                              capture_output=True, text=True, timeout=900)
        return (lauf.stdout or "").strip()
    except Exception:
        return ""


def gast_frei_gb(vm: str = "ForzaScrapeVM") -> float | None:
    aus = gast(GAST_PRUEFEN, vm)
    try:
        return float(aus.splitlines()[-1].replace(",", "."))
    except (ValueError, IndexError):
        return None


def host_raeumen(behalten_faktor: int, apply: bool) -> int:
    weg = 0
    for rel, behalten in HOST_ORDNER:
        wurzel = WORKSPACE / rel
        if not wurzel.exists():
            continue
        kinder = sorted((p for p in wurzel.iterdir() if p.is_dir()),
                        key=lambda p: p.stat().st_mtime, reverse=True)
        grenze = max(behalten, behalten_faktor)
        alt = kinder[grenze:]
        if alt:
            print("  %-32s %d Ordner, davon %d zu alt (behalte %d)"
                  % (rel, len(kinder), len(alt), grenze))
        for d in alt:
            if apply:
                shutil.rmtree(d, ignore_errors=True)
            weg += 1
    return weg


def main(argv: list | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--pruefen", action="store_true",
                        help="nur den freien Platz melden, nichts aendern")
    parser.add_argument("--behalten", type=int, default=20,
                        help="wie viele Laeufe stehen bleiben (Vorgabe 20)")
    parser.add_argument("--vm", default="ForzaScrapeVM")
    args = parser.parse_args(argv)

    frei = gast_frei_gb(args.vm)
    if frei is None:
        print("Der Gast antwortet nicht -- laeuft die VM?")
    else:
        zustand = ("KRITISCH" if frei < KRITISCH_GB
                   else "knapp" if frei < KNAPP_GB else "in Ordnung")
        print("Gast C: %.2f GB frei  (%s)" % (frei, zustand))

    host_frei = shutil.disk_usage(WORKSPACE).free / GB
    print("Host  : %.1f GB frei" % host_frei)

    if args.pruefen:
        # Rueckgabewert als Ampel: 2 heisst "nicht scannen".
        if frei is not None and frei < KRITISCH_GB:
            print("\nUnter %.0f GB hat Scannen keinen Zweck: der Gast kann keine "
                  "Bilder schreiben, und das sieht dann aus wie ein Anzeige- oder "
                  "Navigationsfehler." % KRITISCH_GB)
            return 2
        return 0

    print("\nHost aufraeumen:")
    weg = host_raeumen(args.behalten, args.apply)
    if weg == 0:
        print("  nichts zu tun")

    print("Gast aufraeumen:")
    if frei is None:
        print("  uebersprungen -- kein Zugriff")
    elif not args.apply:
        print("  (Probelauf -- mit --apply)")
    else:
        aus = gast(GAST_RAEUMEN.replace("__BEHALTEN__", str(args.behalten)),
                   args.vm)
        # Die letzten drei nicht-leeren Zeilen: Anzahl, vorher, nachher.
        teile = [z.strip() for z in (aus or "").splitlines() if z.strip()][-3:]
        if len(teile) == 3:
            print("  %s Ordner entfernt, frei: %s GB -> %s GB"
                  % (teile[0], teile[1], teile[2]))
        else:
            print("  Antwort unbrauchbar: %r" % aus[-160:])

    if not args.apply:
        print("\nProbelauf. Mit --apply wird es getan.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
