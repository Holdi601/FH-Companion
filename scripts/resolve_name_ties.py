"""Namens-Gleichstaende aufloesen: erscheint die Alternative anderswo unter EIGENEM Namen?

    python scripts/resolve_name_ties.py                  nur zeigen
    python scripts/resolve_name_ties.py --schreiben      Ergebnis als TSV ablegen

## Der Befund, auf dem das beruht

`data/analytics/name_ties_offen.tsv` haelt 472 Bildschirmnamen fest, die mit gleicher
Punktzahl auf mehrere Katalogautos passen; 274 davon sind echte Entscheidungen ueber
33.275 Zeilen. Die groesste: `Lambo Huracan` mit 6.954 Zeilen, passend auf den
Huracán LP 610-4, den EVO und den STO.

Das sah nach einer Frage aus, die nur ein Mensch beantworten kann. Sie ist es nicht.

**Das Spiel unterscheidet die Varianten sehr wohl -- nur an anderer Stelle.** In den
gelesenen Zeilen stehen nebeneinander:

    Huracan Sterrato    2.506x
    Huracan STO            41x
    Huracán EVO            16x
    Lambo Huracan          36x     (in derselben Stichprobe)

Die Varianten bekommen ihren eigenen Namen OHNE Markenpraefix; die Grundversion
bekommt "Lambo Huracan". Dasselbe Muster bei `Audi RS 7` neben `Audi RS 7 '21` und
bei `Lambo Countach` neben `L. Countach '21`.

Daraus wird ein Beleg: **hat eine Alternative anderswo ihren eigenen Bildschirmnamen,
dann ist sie mit dem mehrdeutigen Namen nicht gemeint.** Bleibt genau eine
Alternative ohne eigenen Namen uebrig, ist die Entscheidung gefallen -- nicht
geraten, sondern belegt.

## Was das NICHT kann

Findet sich fuer keine oder fuer mehrere Alternativen ein eigener Name, bleibt der
Fall offen. Dann fehlt der Beleg, und geraten wird hier nicht: ein falscher Name
schreibt eine Bestzeit dem falschen Auto gut, und das sieht aus wie eine Auskunft.
"""

from __future__ import annotations

import argparse
import csv
import difflib
import re
import sys
import unicodedata
from collections import Counter
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import build_car_roster as roster_tools  # noqa: E402
from build_car_roster import clean_ocr_name  # noqa: E402

WORKSPACE = Path(__file__).resolve().parent.parent
TIES = WORKSPACE / "data" / "analytics" / "name_ties_offen.tsv"
ROWS_ROOT = WORKSPACE / "data" / "memory_scans" / "full_sweep"
NAME_RE = re.compile(r'"car_name"\s*:\s*"((?:[^"\\]|\\.)*)"')

# Wie oft ein Name mindestens gelesen sein muss, um als "eigener Name" zu gelten.
#
# ZWOELF UND NICHT DREI. Bei drei kam '4721 Nissan GTR '20' (3x) als eigener Name
# des NISMO durch -- eine Zeile, in die die Rangnummer hineingerutscht ist. Ein
# Auto, das wirklich auf den Bestenlisten steht, wird hundertfach gelesen; alles im
# einstelligen Bereich ist Rauschen. Und ein Name, der mit einer Ziffernfolge
# beginnt, traegt eine Rangnummer und keinen Hersteller.
MINDEST_VORKOMMEN = 12
RANG_VORNE = re.compile(r"^\s*\d{2,}")

# Ab welcher Punktzahl der Matcher als sicher gilt.
#
# SEINE SKALA GEHT VON 0 BIS 1, nicht bis 100. Mein erster Versuch setzte die
# Schwelle auf 99 und verwarf damit jeden einzelnen Treffer -- das Ergebnis lautete
# "0 entschieden", was wie eine Aussage ueber die Daten aussah und eine ueber meinen
# Zahlenbereich war.
SICHER = 0.95


def falte(text: str) -> str:
    """Kleinschreibung, ohne Akzente, ohne Satzzeichen -- fuer den Vergleich."""
    ohne = unicodedata.normalize("NFKD", text)
    ohne = "".join(c for c in ohne if not unicodedata.combining(c))
    return re.sub(r"[^a-z0-9 ]+", " ", ohne.lower()).strip()


# Was OCR aus Ziffern macht. Beide Seiten werden gleich behandelt, also kann die
# Abbildung nichts verfaelschen -- sie kann nur verhindern, dass ein Verleser als
# eigenstaendiger Name durchgeht.
VERWECHSLUNG = str.maketrans({"O": "0", "o": "0", "D": "0",
                              "I": "1", "l": "1", "|": "1",
                              "A": "4", "S": "5", "B": "8", "Z": "2",
                              "G": "6", "T": "7"})


def ziffern(text: str) -> str:
    """Die Ziffernfolge eines Namens, mit zurueckgedrehten OCR-Verwechslungen.

    `Ferrari FAO` ist ein Verleser von `Ferrari F40`: die 4 wurde als A gelesen, die
    0 als O. Ohne diese Ruecknahme haelt der Vergleich die beiden fuer verschiedene
    Autos -- und der Aufloeser meldete prompt, die 768 Zeilen von "Ferrari FAO"
    gehoerten zum F40 Competizione statt zum F40. Ein Widerspruch, der keiner war.
    """
    return re.sub(r"[^0-9]", "", text.translate(VERWECHSLUNG))


def kern(name: str) -> str:
    """Den Namen von vorangestelltem OCR-Schrott befreien.

    Die Rangnummer links rutscht regelmaessig in die Namensspalte und kommt als
    `I|`, `II`, `Il`, `El`, `Bp` oder `EEE` heraus. Fuer den Vergleich muss das weg,
    sonst gilt `I| Ford Mustang '24` als ein ANDERER Name als `Ford Mustang '24` --
    und der saubere Name (6.766 Vorkommen) zaehlt dann als Beleg dafuer, dass der
    Mustang von 2024 "seinen eigenen Namen hat". Der Aufloeser schloss ihn daraufhin
    aus und liess einen Mustang von 1968 fuer eine Zeile uebrig, in der '24 steht.
    Alle acht Widersprueche eines Laufs hatten genau diese Ursache.

    Abgeschnitten wird von vorne, solange ein Wort hoechstens drei Zeichen hat oder
    aus einem einzigen wiederholten Zeichen besteht. Das trifft auch kurze echte
    Woerter wie "M-B" -- aber es trifft sie auf BEIDEN Seiten des Vergleichs, und
    damit aendert es nichts am Ergebnis.
    """
    teile = name.split()
    while teile and (len(teile[0]) <= 3 or len(set(teile[0])) == 1):
        teile.pop(0)
    return " ".join(teile) if teile else name


def aehnlich(a: str, b: str, schwelle: float = 0.85) -> bool:
    """Sind das zwei Lesarten desselben Namens?

    OCR verwechselt Buchstaben, nicht Autos: 'Lambo Huracdn' und 'Lambo Huracan'
    sind derselbe Bildschirmname.

    ## Die Ziffern muessen GLEICH sein, nicht nur aehnlich

    Mein erster Entwurf verglich die ganze Zeichenkette. Damit galt
    `Nissan GT-R '17` als Verleser von `Nissan GT-R 12` -- sie unterscheiden sich ja
    nur in zwei Zeichen. Genau diese zwei Zeichen sind aber das BAUJAHR, also die
    ganze Unterscheidung. Der Filter warf damit die staerksten Belege weg: von
    vierzehn entschiedenen Faellen blieben die drei groessten auf der Strecke, und
    `Audi RS 3` hatte danach ueberhaupt keinen Beleg mehr.

    Also: gleiche Ziffernfolge UND aehnliche Buchstaben. Verschiedene Ziffern heissen
    verschiedene Autos.
    """
    a, b = kern(a), kern(b)
    if ziffern(a) != ziffern(b):
        return False
    nur_a = re.sub(r"[0-9]", "", falte(a))
    nur_b = re.sub(r"[0-9]", "", falte(b))
    return difflib.SequenceMatcher(None, nur_a, nur_b).ratio() >= schwelle


def alle_bildschirmnamen(wurzel: Path, log=print) -> Counter:
    """Jeden je gelesenen Namen zaehlen.

    EIN Prozess und ein Regex statt json.loads: der Durchgang laeuft ueber 2,3 GB,
    und er darf den laufenden Scan nicht verdraengen -- dessen OCR-Arbeiter sind
    schon einmal unter einem parallelen Bau gestorben.
    """
    zaehler: Counter = Counter()
    dateien = sorted(wurzel.glob("ocr_*/rows.jsonl"))
    for i, pfad in enumerate(dateien, 1):
        try:
            with pfad.open("r", encoding="utf-8", errors="replace") as f:
                for zeile in f:
                    treffer = NAME_RE.search(zeile)
                    if treffer:
                        name = treffer.group(1).strip()
                        if name:
                            zaehler[name] += 1
        except OSError:
            continue
        if i % 150 == 0:
            log("  %d von %d Boards gelesen, %d verschiedene Namen"
                % (i, len(dateien), len(zaehler)))
    return zaehler


def eigene_namen(namen: Counter, log=print) -> dict[str, tuple[str, int]]:
    """Welches Auto meint jeder gelesene Name -- sofern er nur EINES meinen kann.

    ## Warum hier der Matcher des Projekts laeuft und nicht mein eigener Wortvergleich

    Mein erster Entwurf suchte nach einem "unterscheidenden Wort": steht im
    Kandidaten ein Wort, das im mehrdeutigen Namen fehlt, und kommt dieses Wort in
    einem anderen gelesenen Namen vor, dann hat der Kandidat einen eigenen Namen.

    Das ging sofort schief. Fuer `1968 Ford Mustang GT 2+2 Fastback` fand es
    `2 Ford GT40` -- angeschlagen auf "2" und "ford", zwei voellig verschiedene
    Autos. Acht angeblich entschiedene Faelle waren damit wertlos.

    `build_car_roster.match_car` ist genau fuer diese Frage gebaut und wird im
    Analysebau ohnehin benutzt. Ein gelesener Name zaehlt hier nur, wenn der Matcher
    ihn VOLLSTAENDIG und EINDEUTIG auf ein Auto abbildet.
    """
    roster = roster_tools.load_roster(Path("data/car_catalogue/fh6cars.html"))
    index = roster_tools.build_index(roster)
    keys = list(index)
    years = roster_tools.build_year_index(roster)

    zuordnung: dict[str, tuple[str, int]] = {}
    geprueft = 0
    for name, anzahl in namen.items():
        if anzahl < MINDEST_VORKOMMEN or RANG_VORNE.match(name):
            continue
        geprueft += 1
        try:
            auto, punkte = roster_tools.match_car(clean_ocr_name(name), index, keys, years)
        except Exception:
            continue
        if auto is None or punkte < SICHER:
            continue
        voll = "%s %s" % (auto.get("year") or "", auto.get("name") or "")
        schluessel = voll.strip()
        vorhanden = zuordnung.get(schluessel)
        if vorhanden is None or anzahl > vorhanden[1]:
            zuordnung[schluessel] = (name, anzahl)
    log("  %d Namen geprueft, %d Autos haben einen eindeutigen eigenen Namen"
         % (geprueft, len(zuordnung)))
    return zuordnung


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--schreiben", action="store_true",
                   help="das Ergebnis als TSV neben die Gleichstandsdatei legen")
    p.add_argument("--mindest-zeilen", type=int, default=25,
                   help="nur Faelle ab so vielen Zeilen (Vorgabe 25)")
    args = p.parse_args(argv)

    if not TIES.exists():
        print("Keine Gleichstandsdatei unter %s" % TIES)
        return 1

    faelle = [r for r in csv.DictReader(
        TIES.read_text(encoding="utf-8-sig").splitlines(), delimiter="\t")
        if int(r.get("zeilen") or 0) >= args.mindest_zeilen]
    print("%d Gleichstand/Gleichstaende ab %d Zeilen"
          % (len(faelle), args.mindest_zeilen))

    print("Alle gelesenen Bildschirmnamen zaehlen (2,3 GB, ein Durchgang) ...")
    namen = alle_bildschirmnamen(ROWS_ROOT)
    print("  %d verschiedene Namen aus %d Zeilen"
          % (len(namen), sum(namen.values())))
    print("Welcher Name meint eindeutig welches Auto ...")
    zuordnung = eigene_namen(namen)
    print()

    entschieden = offen = 0
    ergebnis = []
    for r in sorted(faelle, key=lambda r: -int(r["zeilen"])):
        schirm = r["bildschirmname"]
        kandidaten = [r["gewaehlt"]] + [
            r[k] for k in ("alternative_1", "alternative_2")
            if (r.get(k) or "").strip()]
        # Die Punktzahl in Klammern abschneiden -- sie gehoert zur Anzeige.
        kandidaten = [re.sub(r"\s*\([\d.]+\)\s*$", "", k).strip() for k in kandidaten]

        belegt = {}
        for k in kandidaten:
            treffer = zuordnung.get(k.strip())
            # DER MEHRDEUTIGE NAME SELBST ZAEHLT NICHT -- AUCH NICHT VERLESEN.
            #
            # Ein exakter Vergleich genuegt dafuer nicht. Fuer den Huracán LP 610-4
            # fand sich 'Lambo Huracdn' (9x): derselbe Bildschirmname, nur mit einem
            # verlesenen Buchstaben. Als "eigener Name" gezaehlt haette er den Fall
            # offen gehalten, obwohl er ihn gerade entscheidet.
            if treffer and not aehnlich(treffer[0], schirm):
                belegt[k] = treffer

        uebrig = [k for k in kandidaten if k not in belegt]
        if len(uebrig) == 1 and belegt:
            entschieden += 1
            zustand = "ENTSCHIEDEN"
            antwort = uebrig[0]
        else:
            offen += 1
            zustand = "offen"
            antwort = ""
        ergebnis.append((schirm, r["zeilen"], zustand, antwort,
                         "; ".join("%s -> '%s' (%dx)" % (k, v[0], v[1])
                                   for k, v in belegt.items())))

        print("%6s  %-24s %s" % (r["zeilen"], schirm, zustand))
        for k in kandidaten:
            if k in belegt:
                print("        %-46s hat einen eigenen Namen: '%s' (%dx)"
                      % (k, belegt[k][0], belegt[k][1]))
            else:
                print("        %-46s %s" % (k, "<- also dieser" if antwort == k else ""))

    print()
    print("%d entschieden, %d weiter offen." % (entschieden, offen))

    if args.schreiben:
        ziel = TIES.with_name("name_ties_aufgeloest.tsv")
        with ziel.open("w", encoding="utf-8", newline="") as f:
            w = csv.writer(f, delimiter="\t")
            w.writerow(["bildschirmname", "zeilen", "zustand", "antwort", "belege"])
            w.writerows(ergebnis)
        print("geschrieben: %s" % ziel)
    return 0


if __name__ == "__main__":
    sys.exit(main())
