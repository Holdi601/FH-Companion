"""Die Streckennamen aus dem einsammeln, was der Sweep beim Anfahren gelesen hat.

    python scripts/harvest_names_from_sweep.py                     zeigen
    python scripts/harvest_names_from_sweep.py --apply             in den Katalog
    python scripts/harvest_names_from_sweep.py --mindestens 3      strenger

## Warum diese Quelle und nicht die Streckenliste

`harvest_route_names.py` liest den TITEL der Streckenliste. Der zieht als Laufschrift
durch ein Fenster -- daher die Rotationen, daher die Uneinigkeit, die sechs
Cross-Country-Namen seit dem 2026-08-31 aus dem Katalog heraushaelt.

Der Sweep dagegen faehrt eine Position im Karussell an, BESTAETIGT sie, und liest den
Namen dann an einer anderen Stelle des Bildschirms -- in ruhender Schrift. Jede
Anfahrt ist eine Lesung, und bei sieben Klassen je Strecke sind das sieben.

## Warum mehrere Lesungen noetig sind

Die OCR verschluckt Zeichen: 'Stadium Cross-Country Circt' statt 'Circuit',
'Edogawa CIN' statt etwas Laengerem. Eine einzelne Lesung ist darum kein Name. Aber
dieselbe Strecke siebenmal gelesen ergibt eine Mehrheit, und die laengste Lesung
innerhalb dieser Mehrheit hat am wenigsten verschluckt.

## Warum das vorsichtig sein muss

Am 2026-09-02 hat ein Katalogersatz einen korrekt gelesenen Namen ueberschrieben und
die Falschbeschriftung damit erst erzeugt. Ein offenes Board kostet einen Scan, ein
falsch beschriftetes verfaelscht die Auswertung unbemerkt. Darum:

- nur Positionen, die im Katalog LEER sind -- nie einen vorhandenen Namen ersetzen
- nur Namen, die mindestens `--mindestens` mal (Vorgabe 2) gelesen wurden
- nie einen Namen, den es an einer ANDEREN Position schon gibt
"""

from __future__ import annotations

import argparse
import json
import re
from collections import defaultdict
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
KATALOG = WORKSPACE / "config" / "fh6_board_catalogue.json"

# Zeilen, in denen ein gelesener Name auftaucht, der zu dieser Position GEHOERT.
MUSTER = [
    re.compile(r"idx(?P<idx>\d+) \S+: Streckenname unlesbar \('(?P<name>[^']*)'\)"),
    re.compile(r"route index (?P<idx>\d+) confirmed on screen as '(?P<name>[^']*)'"),
]

# ZEILEN, DIE EINE LESUNG AUSDRUECKLICH VERWERFEN.
#
# "idx15 D: der Schirm nennt 'Yahikoyama Cross-Country' -- das ist Index 14, nicht
# 15. Die Navigation ist nicht weitergeschaltet."
#
# Hier steht ein sauber gelesener Name neben einer Position, zu der er NICHT gehoert:
# das Karussell ist stehengeblieben und zeigt noch die vorige Strecke. Wer solche
# Zeilen als Lesung zaehlt, schreibt genau die Falschbeschriftung in den Katalog, die
# der Sweep gerade verhindert hat -- und der Name saehe dabei voellig plausibel aus.
#
# Am 2026-09-13 um 17:25 waere das beinahe passiert: idx15 haette 'Yahikoyama
# Cross-Country' bekommen, was in Wahrheit idx14 ist.
VERWORFEN = re.compile(r"idx(?P<idx>\d+) \S+: der Schirm nennt ")


def schluessel(text: str) -> str:
    return "".join(c for c in (text or "").lower() if c.isalnum())


# Woerter, die der Bildschirm nennt, die aber keine Strecke sind.
#
# 'Back' stand am 2026-09-13 um 17:30 als "route index 16 confirmed on screen as
# 'Back'" im Protokoll -- die Navigation war auf einem Menueknopf gelandet. Ohne
# diese Liste waere das eine Lesung wie jede andere gewesen, und bei zwei solchen
# Fehlschlaegen haette 'Back' eine Mehrheit gehabt.
UNSINN = {
    "back", "routes", "route", "select", "cancel", "confirm", "loading",
    "rivals", "leaderboard", "leaderboards", "close", "continue", "next",
    "previous", "menu", "options", "unknown", "none",
}


def plausibel(name: str) -> bool:
    """Kann das ueberhaupt ein Streckenname sein?

    Kein Urteil ueber RICHTIG -- nur ueber moeglich. Ein Streckenname im Spiel hat
    mindestens zwei Woerter oder ist wenigstens sechs Zeichen lang; ein einzelnes
    kurzes Wort ist fast immer ein Knopf.
    """
    n = (name or "").strip()
    if len(n) < 6:
        return False
    if schluessel(n) in UNSINN:
        return False
    # Mindestens die Haelfte Buchstaben: 'I @ 2,666,890' war einmal ein
    # "Streckenname" im Bestand, gelesen aus einer Punktestandszeile.
    buchstaben = sum(c.isalpha() for c in n)
    return buchstaben >= max(5, len(n) // 2)


def lesungen(logs: list[Path]) -> tuple[dict, dict]:
    """Gibt (gueltige Lesungen je Index, Anzahl verworfener je Index) zurueck."""
    gefunden = defaultdict(list)
    verworfen = defaultdict(int)
    for log in logs:
        try:
            text = log.read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
        for zeile in text.splitlines():
            v = VERWORFEN.search(zeile)
            if v:
                verworfen[int(v["idx"])] += 1
                continue
            for muster in MUSTER:
                m = muster.search(zeile)
                if m and m["name"].strip():
                    if plausibel(m["name"]):
                        gefunden[int(m["idx"])].append(m["name"].strip())
                    else:
                        verworfen[int(m["idx"])] += 1
                    break
    return gefunden, verworfen


def beste(namen: list[str]) -> tuple[str, int]:
    """Der haeufigste Name -- und darin die vollstaendigste Schreibweise.

    ## Warum nicht einfach nach exakter Gleichheit gruppiert wird

    Die OCR schneidet hinten ab. Dieselbe Strecke kam am 2026-09-13 als

        'Stadium Cross-Country Circt'     (abgeschnitten)
        'Stadium Cross-Country Circuit'   (vollstaendig)

    Nach exakter Gleichheit waeren das zwei Namen mit je einer Stimme -- die
    Mehrheit spaltet sich, und bei `--mindestens 2` bekaeme die Strecke gar keinen
    Namen, obwohl sie zweimal richtig gelesen wurde.

    Darum: ist die eine Lesung der ANFANG der anderen, sind es dieselbe. Die
    laengste Schreibweise der Gruppe gewinnt, denn sie hat am wenigsten verschluckt.

    ## Warum Teilfolge und nicht Anfang

    Der erste Entwurf verlangte, dass die kuerzere Lesung der ANFANG der laengeren
    ist. Der Selbsttest hat ihn widerlegt: 'Circt' ist kein Anfang von 'Circuit' --
    die OCR laesst auch MITTEN im Wort etwas weg, nicht nur hinten. Uebrig bleibt
    die Reihenfolge: was gelesen wurde, steht im vollstaendigen Namen in derselben
    Folge. Das ist genau "Teilfolge".

    ## Warum eine Laengenschranke dazugehoert

    Teilfolge allein ist zu grosszuegig: 'Izu' ist eine Teilfolge von fast allem.
    Darum muss die kuerzere Lesung mindestens 70 % der laengeren ausmachen. 'Naruo
    Cross-Country Circuit' und 'Nangan Cross-Country Circuit' -- zwei verschiedene
    Strecken -- bleiben so getrennt, weil 'naruo...' gar keine Teilfolge von
    'nangan...' ist.
    """
    if not namen:
        return "", 0

    def teilfolge(kurz: str, lang: str) -> bool:
        if len(kurz) < 0.7 * len(lang):
            return False
        i = 0
        for c in lang:
            if i < len(kurz) and c == kurz[i]:
                i += 1
        return i == len(kurz)

    # Nach Laenge absteigend: die vollstaendigste Lesung eroeffnet ihre Gruppe.
    sortiert = sorted(set(namen), key=lambda n: -len(schluessel(n)))
    gruppen: list[tuple[str, list[str]]] = []
    for n in sortiert:
        k = schluessel(n)
        for kopf, mitglieder in gruppen:
            if teilfolge(k, schluessel(kopf)):
                mitglieder.append(n)
                break
        else:
            gruppen.append((n, [n]))

    def stimmen(mitglieder: list[str]) -> int:
        return sum(namen.count(m) for m in mitglieder)

    kopf, mitglieder = max(gruppen, key=lambda g: (stimmen(g[1]), len(schluessel(g[0]))))
    return kopf, stimmen(mitglieder)



def zuordnen(gefunden: dict, anzahl_positionen: int):
    """Jeder Position ihren eigenen Namen geben -- und geklaute wegnehmen.

    Gibt (bester Name je Position, hoffnungslose Positionen, geklaute Lesungen)
    zurueck.

    ## Das Problem

    Das Karussell bleibt manchmal stehen und zeigt noch die vorige Strecke. Die
    Position bekommt dann eine Lesung, die ihr nicht gehoert. Am 2026-09-13 traf
    das idx08 dreimal in Folge: es las 'Stadium Cross-Country Circt', den Namen von
    idx07.

    ## Warum zwei Durchgaenge

    Die naheliegende Regel -- "derselbe Name an zwei Positionen, also verwirf die
    schwaechere" -- wirft die ganze Position weg. idx08 waere damit fuer immer
    namenlos geblieben, obwohl eine richtige Lesung ('Nangan Cross-Country Circu')
    dabei war.

    Also: erst feststellen, WEM ein Name gehoert (die Position mit den meisten
    Lesungen davon -- Stehenbleiben ist die Ausnahme, richtiges Anfahren die Regel),
    dann bei allen anderen genau diese Lesungen streichen und neu auszaehlen.

    Bleibt danach nichts uebrig, war das Karussell bei JEDER Anfahrt stehen
    geblieben. Dann gibt es hier keinen Namen -- und das ist die richtige Antwort.
    Lieber ein offenes Board als ein falsch beschriftetes.
    """
    erst = {idx: beste(namen) for idx, namen in gefunden.items()
            if idx < anzahl_positionen}
    besitzer = {}
    for idx, (name, n) in erst.items():
        k = schluessel(name)
        if k not in besitzer or n > erst[besitzer[k]][1]:
            besitzer[k] = idx

    kandidaten, verlierer, geklaut = {}, {}, {}
    for idx in sorted(erst):
        uebrig = [n for n in gefunden[idx]
                  if besitzer.get(schluessel(n), idx) == idx]
        weg = len(gefunden[idx]) - len(uebrig)
        if uebrig:
            kandidaten[idx] = beste(uebrig)
            if weg:
                geklaut[idx] = (weg, besitzer[schluessel(erst[idx][0])])
        else:
            kandidaten[idx] = erst[idx]
            verlierer[idx] = besitzer[schluessel(erst[idx][0])]
    return kandidaten, verlierer, geklaut


def main(argv: list | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--kategorie", default="Cross-Country")
    parser.add_argument("--mindestens", type=int, default=2)
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--log", type=Path, action="append",
                        help="zusaetzliche Protokolldatei")
    args = parser.parse_args(argv)

    # NUR DAS PROTOKOLL DIESER KATEGORIE.
    #
    # Der erste Entwurf durchsuchte alles unter data/runtime/. Das Ergebnis war der
    # Beweis, dass es falsch ist: fuer Cross-Country idx0 kamen 64 Lesungen
    # 'Highway Circui' heraus -- eine Strecke aus Road Racing. Ein Karussellindex
    # bedeutet je Kategorie etwas ANDERES, und Lesungen aus fremden Kategorien
    # ueberstimmen mit ihrer Menge muehelos die richtigen.
    #
    # Genau diese Verwechslung hat am 2026-08-31 schon einmal Zeilen unter dem
    # Namen einer anderen Strecke abgelegt. Also: die Datei, die der Sweep dieser
    # Kategorie schreibt, und sonst nichts -- oder was ausdruecklich mit --log
    # genannt wird.
    slug = args.kategorie.lower().replace(" ", "_")
    logs = list(args.log or [])
    if not logs:
        eigen = WORKSPACE / "data/runtime/overnight" / (slug + ".out")
        if eigen.exists():
            logs.append(eigen)
    if not logs:
        print("Kein Protokoll fuer %r gefunden (erwartet: %s). Mit --log angeben."
              % (args.kategorie, (WORKSPACE / "data/runtime/overnight" /
                                  (slug + ".out")).as_posix()))
        return 1
    print("Protokolle: " + ", ".join(p.name for p in logs))

    gefunden, verworfen = lesungen(logs)
    if verworfen:
        print("Verworfene Anfahrten (Karussell stand noch auf der vorigen Strecke): "
              + ", ".join("idx%d: %dx" % (k, v) for k, v in sorted(verworfen.items())))
    if not gefunden:
        print("Keine Lesungen gefunden.")
        return 0

    katalog = json.loads(KATALOG.read_text(encoding="utf-8-sig"))
    liste = (katalog.get("route_carousel_by_category") or {}).get(args.kategorie)
    if not isinstance(liste, list):
        print("Kategorie %r steht nicht im Karussell-Katalog." % args.kategorie)
        return 1

    vorhanden = {}
    for e in liste:
        name = e.get("name") if isinstance(e, dict) else e
        idx = e.get("route_index") if isinstance(e, dict) else None
        if name:
            vorhanden[schluessel(name)] = idx

    # DERSELBE NAME AN ZWEI POSITIONEN IST EIN STEHENGEBLIEBENES KARUSSELL.
    #
    # Am 2026-09-13 um 17:28 las idx08 'Stadium Cross-Country Circt' -- denselben
    # Namen wie idx07 kurz zuvor. Das Karussell war nicht weitergeschaltet und zeigte
    # noch die vorige Strecke. Die eingebaute Pruefung des Sweeps kann das hier NICHT
    # sehen: sie vergleicht gegen den Katalog, und beide Namen stehen noch nicht drin.
    #
    # Der Gewinner ist die Position mit den MEISTEN Lesungen dieses Namens -- ein
    # Stehenbleiben ist die Ausnahme, das richtige Anfahren die Regel. Die andere
    # Position bekommt gar nichts: lieber ein offenes Board als ein falsch
    # beschriftetes.
    kandidaten, verlierer, geklaut = zuordnen(gefunden, len(liste))
    for idx, (anzahl, wem) in sorted(geklaut.items()):
        print("  (idx%d: %d Lesung(en) gehoerten idx%d -- nicht mitgezaehlt)"
              % (idx, anzahl, wem))

    print("\n%-5s %-6s %-40s %s" % ("idx", "wie oft", "bester Name", "Urteil"))
    setzen = []
    for idx in sorted(gefunden):
        if idx >= len(liste):
            continue
        eintrag = liste[idx]
        schon = eintrag.get("name") if isinstance(eintrag, dict) else eintrag
        name, anzahl = kandidaten[idx]
        if schon:
            urteil = "Katalog hat schon %r -- unberuehrt" % schon
        elif idx in verlierer:
            urteil = ("derselbe Name gewinnt auf idx%d -- Karussell stand still"
                      % verlierer[idx])
        elif anzahl < args.mindestens:
            urteil = "zu wenige Lesungen (< %d)" % args.mindestens
        elif schluessel(name) in vorhanden:
            urteil = "dieser Name steht schon auf idx%s" % vorhanden[schluessel(name)]
        else:
            urteil = "SETZEN"
            setzen.append((idx, name, anzahl))
        print("%-5d %-6d %-40s %s" % (idx, anzahl, name[:40], urteil))
        # Alle Schreibweisen zeigen, damit sich das Urteil nachpruefen laesst.
        alle = sorted(set(gefunden[idx]))
        if len(alle) > 1:
            for a in alle:
                print("        gelesen: %r (%dx)" % (a, gefunden[idx].count(a)))

    if not setzen:
        print("\nNichts zu setzen.")
        return 0
    if not args.apply:
        print("\nProbelauf -- %d Namen waeren zu setzen. Mit --apply." % len(setzen))
        return 0

    for idx, name, anzahl in setzen:
        eintrag = liste[idx]
        if isinstance(eintrag, dict):
            eintrag["name"] = name
            eintrag["name_source"] = "sweep-lesung"
            eintrag["name_readings"] = anzahl
        else:
            liste[idx] = {"name": name, "route_index": idx,
                          "name_source": "sweep-lesung", "name_readings": anzahl}
    KATALOG.write_text(json.dumps(katalog, ensure_ascii=False, indent=1),
                       encoding="utf-8")
    print("\n%d Namen in den Katalog geschrieben." % len(setzen))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
