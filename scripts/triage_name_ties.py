"""Die offenen Namens-Gleichstaende sortieren: welche aendern die Wertung wirklich?

    python scripts/triage_name_ties.py
    python scripts/triage_name_ties.py --zeigen harmlos

## Das Problem

`data/analytics/name_ties_offen.tsv` haelt 472 Bildschirmnamen fest, die mit
gleicher Punktzahl auf mehrere Katalogautos passen -- zusammen rund 47.000
Zeilen. Der Bauer waehlt eines davon. Waehlt er falsch, landen die Zeilen beim
falschen Auto, und das ist schlimmer, als sie wegzulassen: ein falscher Eintrag
sieht aus wie eine Auskunft.

## Warum 472 Einzelentscheidungen keine Hilfe sind

Weil die meisten keine sind. `2012 Nissan GT-R Black Edition (R35)` gegen
`2012 Nissan GT-R Black Edition (R35) Forza Edition` ist dasselbe Auto mit einem
Anhaengsel; `2014 Lamborghini Huracán LP 610-4` gegen `2020 Huracán STO` sind zwei
verschiedene Autos mit verschiedenen Zeiten.

Die erste Sorte kostet nichts, egal wie sie ausgeht. Die zweite entscheidet, wem
eine Bestzeit gutgeschrieben wird.

Dieses Skript trennt sie -- damit aus 472 Entscheidungen die paar werden, die
wirklich eine sind.

## Die Sortierung

**harmlos** -- die Kandidaten sind dieselbe Marke UND dasselbe Grundmodell UND
dasselbe Baujahr. Es unterscheidet sie nur ein Zusatz (Forza Edition, ein
Ausstattungskuerzel). Fuer eine Wertung "welches Auto ist auf dieser Strecke
schnell" sind sie ein Auto.

**jahr** -- gleiche Marke und gleiches Modell, aber verschiedene Baujahre. Kann
einen Unterschied machen (ein GT-R von 2012 ist nicht der von 2017), macht ihn
aber nur, wenn beide Baujahre ueberhaupt im Bestand vorkommen.

**echt** -- verschiedene Modelle. Hier faellt eine Entscheidung, und nur diese
Faelle gehoeren einem Menschen vorgelegt.
"""

from __future__ import annotations

import argparse
import re
from collections import Counter
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
DATEI = WORKSPACE / "data" / "analytics" / "name_ties_offen.tsv"

# Zusaetze, die ein Auto nicht zu einem anderen machen.
ANHAENGSEL = re.compile(
    r"\b(forza\s+edition|fe|special\s+edition|launch\s+edition|edition)\b", re.I)

JAHR = re.compile(r"\b(19|20)\d{2}\b")

# Im Bildschirmnamen steht das Baujahr oft verkuerzt: "Ford Mustang '24",
# "Nissan GT-R 12", "ME MX-5 '94".
JAHR_KURZ = re.compile(r"(?:'|\b)(\d{2})\b")


def jahr_hinweis(bildschirmname: str) -> set:
    """Moegliche volle Baujahre aus einer zweistelligen Zahl im Namen.

    ## Warum das zaehlt

    Das Spiel schreibt den Namen kurz, aber es schreibt das JAHR dazu -- und der
    Katalog fuehrt jedes Auto mit vollem Baujahr. Ein Gleichstand zwischen
    '2012 Nissan GT-R Black Edition' und '2017 Nissan GT-R' ist deshalb keiner,
    sobald der Bildschirm 'Nissan GT-R 12' sagt: 2017 faellt weg.

    Am 2026-09-14 ausgezaehlt: 248 der 472 Gleichstaende tragen so einen Hinweis,
    und er schliesst dort mindestens einen Kandidaten aus -- knapp 20.000 Zeilen.

    ## Die Grenze bei 30

    00-30 wird zu 20xx, 31-99 zu 19xx. Das Spiel erschien 2026; ein '27er Modell
    gibt es, ein 1927er in dieser Liste nicht. Die Grenze ist eine Annahme, aber
    eine, die sich an jedem Einzelfall nachpruefen laesst -- darum steht der
    Hinweis unten in der Ausgabe mit dabei.
    """
    raus = set()
    for m in JAHR_KURZ.finditer(bildschirmname or ""):
        zz = m.group(1)
        raus.add(("20" if int(zz) <= 30 else "19") + zz)
    return raus


def jahre_von(kandidat: str) -> set:
    return {m.group(0) for m in JAHR.finditer(kandidat or "")}


def nach_jahr(bildschirmname: str, kandidaten: list) -> list:
    """Die Kandidaten, die zum Jahr im Bildschirmnamen passen -- sonst alle.

    Passt KEINER, bleibt alles stehen: dann ist die Zahl im Namen kein Baujahr
    gewesen (eine Modellbezeichnung etwa), und darauf etwas zu stuetzen waere
    schlimmer als sie zu ignorieren.
    """
    hinweis = jahr_hinweis(bildschirmname)
    if not hinweis:
        return kandidaten
    passend = [k for k in kandidaten if jahre_von(k) & hinweis]
    return passend or kandidaten


def zerlege(name: str) -> tuple[str, str, str]:
    """(Baujahr, Grundname ohne Anhaengsel, voller Name ohne Jahr)."""
    n = (name or "").strip()
    # Die Punktzahl in Klammern am Ende abschneiden: "... (100.0)".
    n = re.sub(r"\s*\(\d+(?:\.\d+)?\)\s*$", "", n)
    m = JAHR.search(n)
    jahr = m.group(0) if m else ""
    ohne_jahr = JAHR.sub("", n).strip()
    grund = ANHAENGSEL.sub("", ohne_jahr)
    # Klammerzusaetze wie "(R35)" bleiben -- sie benennen die Baureihe.
    grund = re.sub(r"\s{2,}", " ", grund).strip(" -")
    return jahr, grund.lower(), ohne_jahr.lower()


def urteil(kandidaten: list, bildschirmname: str = "") -> tuple:
    """(Gruppe, verbleibende Kandidaten nach der Jahresbindung)."""
    uebrig = nach_jahr(bildschirmname, kandidaten)
    if len(uebrig) == 1:
        return "eindeutig", uebrig
    zerlegt = [zerlege(k) for k in uebrig]
    jahre = {j for j, _, _ in zerlegt if j}
    grund = {g for _, g, _ in zerlegt}
    if len(grund) == 1 and len(jahre) <= 1:
        return "harmlos", uebrig
    if len(grund) == 1:
        return "jahr", uebrig
    return "echt", uebrig


def main(argv: list | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--zeigen",
                        choices=["eindeutig", "harmlos", "jahr", "echt"],
                        help="die Faelle dieser Gruppe auflisten")
    parser.add_argument("--grenze", type=int, default=25,
                        help="wie viele Zeilen ein Fall haben muss, um zu zaehlen")
    args = parser.parse_args(argv)

    if not DATEI.exists():
        print("Fehlt: " + DATEI.as_posix())
        return 1

    zeilen = DATEI.read_text(encoding="utf-8", errors="replace").splitlines()
    kopf, rest = zeilen[0].split("\t"), zeilen[1:]
    faelle = []
    for z in rest:
        teile = z.split("\t")
        if len(teile) < 4:
            continue
        name, anzahl, gewaehlt = teile[0], teile[1], teile[2]
        alternativen = [a for a in teile[3:] if a.strip()]
        try:
            n = int(anzahl)
        except ValueError:
            n = 0
        alle = [gewaehlt] + alternativen
        gruppe, uebrig = urteil(alle, name)
        faelle.append({"name": name, "zeilen": n, "gewaehlt": gewaehlt,
                       "alle": alle, "uebrig": uebrig, "urteil": gruppe})

    zaehler = Counter(f["urteil"] for f in faelle)
    zeilen_je = Counter()
    for f in faelle:
        zeilen_je[f["urteil"]] += f["zeilen"]

    gesamt_zeilen = sum(f["zeilen"] for f in faelle)
    print("%d Gleichstaende, zusammen %s Zeilen\n"
          % (len(faelle), f"{gesamt_zeilen:,}".replace(",", ".")))
    print("%-9s %6s  %12s  %s" % ("Gruppe", "Faelle", "Zeilen", "was es bedeutet"))
    for gruppe, was in [
            ("eindeutig", "das Jahr im Namen laesst genau einen uebrig"),
            ("harmlos", "dasselbe Auto, nur ein Zusatz -- Wahl ist egal"),
            ("jahr", "gleiches Modell, anderes Baujahr -- meist egal"),
            ("echt", "verschiedene Autos -- hier faellt eine Entscheidung")]:
        print("%-9s %6d  %12s  %s" % (
            gruppe, zaehler[gruppe],
            f"{zeilen_je[gruppe]:,}".replace(",", "."), was))

    echte = sorted((f for f in faelle if f["urteil"] == "echt"),
                   key=lambda f: -f["zeilen"])
    gross = [f for f in echte if f["zeilen"] >= args.grenze]
    print("\nDavon mit mindestens %d Zeilen: %d Faelle, %s Zeilen"
          % (args.grenze, len(gross),
             f"{sum(f['zeilen'] for f in gross):,}".replace(",", ".")))
    print("Das sind die, die einem Menschen vorzulegen sind -- nicht 472.")

    if args.zeigen:
        wahl = [f for f in faelle if f["urteil"] == args.zeigen]
        wahl.sort(key=lambda f: -f["zeilen"])
        print("\n%s (%d):" % (args.zeigen, len(wahl)))
        for f in wahl[:60]:
            print("  %6d  %-26s -> %s" % (f["zeilen"], f["name"][:26], f["gewaehlt"]))
            for a in f["alle"][1:]:
                print("          %-26s    %s" % ("", a))
    else:
        print("\nDie zehn groessten echten Entscheidungen:")
        for f in echte[:10]:
            print("  %6d  %-24s" % (f["zeilen"], f["name"][:24]))
            for a in f["alle"]:
                print("          %s" % a)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
