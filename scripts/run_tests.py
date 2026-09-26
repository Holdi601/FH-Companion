"""Alle Tests auf einmal laufen lassen -- und sagen, was NICHT lief.

    python scripts/run_tests.py              alles ohne Spiel und VM
    python scripts/run_tests.py --alle       auch die, die das laufende Spiel brauchen
    python scripts/run_tests.py --nur lap    nur Tests, deren Name 'lap' enthaelt

## Warum es das gibt

Es sind vierzehn Testdateien geworden. Sie einzeln aufzurufen heisst, dass man drei
davon vergisst -- und zwar zuverlaessig die, die man gerade kaputt gemacht hat.

## Warum zwei Gruppen

Ein Teil der Tests fernsteuert das Spiel oder liest den Bildschirm. Ohne laufende VM
scheitern die, und zwar nicht als Befund, sondern als Umstand. Ein roter Lauf, dessen
Rot nichts bedeutet, wird nach dem dritten Mal ignoriert -- und dann bedeutet auch
ein echtes Rot nichts mehr. Darum sind sie GETRENNT und werden ausdruecklich als
uebersprungen gemeldet, nicht stillschweigend weggelassen.

## Was "bestanden" hier heisst

Der Rueckgabewert des Skripts, sonst nichts. Jeder Test hier gibt 0 zurueck, wenn er
zufrieden ist, und schreibt am Ende eine Zeile, die ein Mensch lesen kann. Diese
Zeile wird mit angezeigt -- eine Zusammenfassung, die nur "14 ok" sagt, verschweigt
gerade das, wofuer die Tests geschrieben wurden.
"""

from __future__ import annotations

import argparse
import subprocess
import sys
import time
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
TESTS = WORKSPACE / "scripts"

# Tests, die ein laufendes Spiel, eine laufende VM oder einen Bildschirm brauchen.
# Ohne die sind sie nicht rot, sondern unbeantwortet -- ein Unterschied, der zaehlt.
BRAUCHT_SPIEL = {
    "test_frida_attach.py",
    "test_frida_stability.py",
    "test_screen_reader.py",
}

# Was zwar extern ist, aber auf jedem Entwicklungsrechner da sein sollte. Fehlt es,
# meldet der Test das selbst und deutlich -- das ist dann ein Befund, kein Umstand.
BRAUCHT_WERKZEUG = {
    "test_admin_crypto.py": "node",
    "test_lap_signature.py": "dotnet",
    "test_analytics_page.py": "node",
    "test_rivals_advisor.py": "node",
}


def main(argv: list | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--alle", action="store_true",
                        help="auch die Tests, die das laufende Spiel brauchen")
    parser.add_argument("--nur", default="",
                        help="nur Tests, deren Dateiname diesen Text enthaelt")
    parser.add_argument("--zeitgrenze", type=int, default=900,
                        help="Sekunden je Test (Vorgabe 900)")
    args = parser.parse_args(argv)

    dateien = sorted(TESTS.glob("test_*.py"))
    if args.nur:
        dateien = [d for d in dateien if args.nur.lower() in d.name.lower()]
    if not dateien:
        print("Keine Testdatei passt auf %r." % args.nur)
        return 1

    laufen, uebersprungen = [], []
    for d in dateien:
        if d.name in BRAUCHT_SPIEL and not args.alle:
            uebersprungen.append(d)
        else:
            laufen.append(d)

    print("%d Test(s), %d uebersprungen\n" % (len(laufen), len(uebersprungen)))
    breit = max((len(d.name) for d in laufen), default=20)

    ergebnisse = []
    for d in laufen:
        begonnen = time.monotonic()
        print("  %-*s  laeuft ..." % (breit, d.name), end="\r", flush=True)
        try:
            lauf = subprocess.run([sys.executable, str(d)], capture_output=True,
                                  text=True, cwd=str(WORKSPACE),
                                  timeout=args.zeitgrenze)
            code, ausgabe = lauf.returncode, (lauf.stdout or "") + (lauf.stderr or "")
        except subprocess.TimeoutExpired:
            code, ausgabe = -1, "Zeitgrenze von %d s ueberschritten" % args.zeitgrenze
        dauer = time.monotonic() - begonnen

        # Die letzte nicht-leere Zeile: dort steht bei allen Tests das Urteil.
        zeilen = [z.strip() for z in ausgabe.splitlines() if z.strip()]
        letzte = zeilen[-1] if zeilen else "(keine Ausgabe)"
        zeichen = "ok  " if code == 0 else "FEHL"
        print("  %s %-*s  %5.1fs  %s" % (zeichen, breit, d.name, dauer, letzte[:70]))
        ergebnisse.append((d.name, code, ausgabe))

    for d in uebersprungen:
        print("  --   %-*s  uebersprungen: braucht das laufende Spiel" % (breit, d.name))

    schlecht = [(n, a) for n, c, a in ergebnisse if c != 0]
    print()
    if not schlecht:
        print("%d Test(s) bestanden%s." % (
            len(ergebnisse),
            ", %d uebersprungen -- mit --alle auch die" % len(uebersprungen)
            if uebersprungen else ""))
        return 0

    # Bei einem Fehlschlag die ganze Ausgabe -- eine Zusammenfassung ohne die
    # Einzelheiten zwingt dazu, den Test danach nochmal von Hand zu starten.
    print("%d von %d Test(s) fehlgeschlagen:\n" % (len(schlecht), len(ergebnisse)))
    for name, ausgabe in schlecht:
        werkzeug = BRAUCHT_WERKZEUG.get(name)
        hinweis = ("  (braucht %s -- fehlt es, ist das der Grund)" % werkzeug
                   if werkzeug else "")
        print("=" * 72)
        print(name + hinweis)
        print("=" * 72)
        print(ausgabe.strip()[-4000:])
        print()
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
