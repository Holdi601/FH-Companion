"""Die sechs unsicheren Cross-Country-Streckennamen aus ihren Lesungen rekonstruieren.

    python scripts/reconstruct_cross_country_names.py
    python scripts/reconstruct_cross_country_names.py --min-score 0.90

## Warum es das gibt

Sechs der 19 Cross-Country-Strecken stehen seit dem 2026-08-28 mit
`confidence: low` im Anhang und darum NICHT im Katalog. Der Sweep weigert sich,
eine Strecke zu scannen, die er nicht benennen kann -- zu Recht, Zeilen unter
falschem Streckennamen sind schlimmer als keine Zeilen. Die Folge: 42 der 133
Boards wurden nie gefahren, und zwar lautlos ("ohne Navigation uebersprungen"),
waehrend der Lauf mit "finished" endete.

## Warum sich das rekonstruieren laesst, ohne zu raten

Der Streckenname laeuft als LAUFSCHRIFT durch ein festes Fenster. Jede Lesung in
`pieces` ist darum ein Ausschnitt derselben UMLAUFENDEN Zeichenkette, an einer
anderen Stelle begonnen:

    'ross-Country Circuit Edogav'
    '-Country Circuit Edogawa'
    'Edogawa Cross-Co y Circuit'

Damit ist eine Rekonstruktion pruefbar und keine Vermutung: ein Name stimmt, wenn
sich JEDE Lesung als Fenster im verdoppelten Namen wiederfindet. Der verdoppelte
Name ("abcabc") enthaelt jede Rotation von "abc" als zusammenhaengendes Stueck --
das ist der ganze Trick, und er ist die Pruefung zugleich.

Zusaetzlich muss der Name dem Muster der 13 GESICHERTEN Namen folgen: ein
Eigenname, dann "Cross-Country", wahlweise gefolgt von "Circuit".

## Was dieses Skript NICHT tut

Es traegt nichts ein. Es rechnet, bewertet und legt einen Vorschlag daneben. Der
Eintrag in den Katalog gehoert hinter eine Bestaetigung AM SPIEL -- ein falscher
Streckenname faellt beim Scannen niemandem auf, und genau daran sind am
2026-09-03 fuenf Boards verlorengegangen.
"""

from __future__ import annotations

import argparse
import json
import re
from difflib import SequenceMatcher
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
LESUNGEN = WORKSPACE / "config" / "route_names_cross_country.json"
UNSICHER = (5, 7, 8, 12, 15, 16)

# Die Woerter, die in JEDEM Cross-Country-Namen vorkommen und darum nichts
# unterscheiden. Was uebrig bleibt, ist der Eigenname.
GERUEST = ("cross", "country", "circuit", "crosscountry", "cross-country")


def saeubern(text: str) -> str:
    """Satzzeichen und OCR-Krümel weg, alles klein, einfache Leerzeichen."""
    text = text.replace("-", " ").replace("'", " ").replace('"', " ")
    text = re.sub(r"[^A-Za-z0-9 ]+", " ", text)
    return re.sub(r"\s+", " ", text).strip().lower()


def eigennamen(pieces: list[str]) -> list[tuple[str, int]]:
    """Welche Woerter kommen vor, die nicht zum Geruest gehoeren -- und wie oft."""
    zaehler: dict[str, int] = {}
    for stueck in pieces:
        for wort in saeubern(stueck).split():
            if wort in GERUEST or len(wort) < 3:
                continue
            # Ein am Fensterrand abgeschnittenes Wort ist ein Praefix oder Suffix
            # eines vollstaendigen; beide sollen auf denselben Eintrag zahlen.
            zaehler[wort] = zaehler.get(wort, 0) + 1
    return sorted(zaehler.items(), key=lambda kv: (-kv[1], -len(kv[0])))


def beste_fenster_aehnlichkeit(stueck: str, kandidat: str) -> float:
    """Wie gut passt eine Lesung in IRGENDEIN Fenster des verdoppelten Namens?

    Der verdoppelte Name enthaelt jede Rotation als zusammenhaengendes Stueck.
    Verglichen wird mit Fenstern in der Laenge der Lesung, ein Zeichen versetzt.
    """
    s = saeubern(stueck)
    k = saeubern(kandidat)
    if not s or not k:
        return 0.0
    doppelt = k + " " + k
    breite = len(s)
    bestes = 0.0
    for start in range(0, max(1, len(doppelt) - breite + 1)):
        fenster = doppelt[start:start + breite]
        wert = SequenceMatcher(None, s, fenster).ratio()
        if wert > bestes:
            bestes = wert
    return bestes


def kandidaten(pieces: list[str]) -> list[str]:
    """Mögliche Namen: Eigenname(n) + 'Cross-Country' [+ 'Circuit']."""
    woerter = [w for w, _ in eigennamen(pieces)]
    if not woerter:
        return []
    # Ein Eigenname kann aus zwei Woertern bestehen ("Tateyama Alpine",
    # "Shinjuku Gyoen"). Darum auch Paare der haeufigsten Woerter anbieten.
    stamm: list[str] = []
    for wort in woerter[:4]:
        stamm.append(wort)
    for a in woerter[:3]:
        for b in woerter[:3]:
            if a != b:
                stamm.append(f"{a} {b}")

    hat_circuit = any("circuit" in saeubern(p) for p in pieces)
    endungen = ["cross country circuit", "cross country"] if hat_circuit \
        else ["cross country", "cross country circuit"]

    raus = []
    for s in stamm:
        for e in endungen:
            raus.append(f"{s} {e}")
    return raus


def bewerten(kandidat: str, pieces: list[str]) -> float:
    """Der schlechteste Treffer zaehlt -- eine einzige Lesung, die nicht passt,
    ist ein Hinweis darauf, dass der Name falsch ist."""
    werte = [beste_fenster_aehnlichkeit(p, kandidat) for p in pieces]
    if not werte:
        return 0.0
    # Mittel und Minimum zusammen: das Mittel belohnt breite Uebereinstimmung,
    # das Minimum bestraft eine Lesung, die gar nicht passen will.
    return 0.65 * (sum(werte) / len(werte)) + 0.35 * min(werte)


def gross(name: str) -> str:
    """Aus 'edogawa cross country circuit' wird 'Edogawa Cross-Country Circuit'."""
    teile = name.split()
    raus = []
    i = 0
    while i < len(teile):
        if teile[i] == "cross" and i + 1 < len(teile) and teile[i + 1] == "country":
            raus.append("Cross-Country")
            i += 2
            continue
        raus.append(teile[i].capitalize())
        i += 1
    return " ".join(raus)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--min-score", type=float, default=0.85,
                        help="ab hier gilt ein Vorschlag als belastbar")
    args = parser.parse_args(argv)

    daten = json.loads(LESUNGEN.read_text(encoding="utf-8"))
    nach_index = {r["route_index"]: r for r in daten["routes"]}

    print(f"{'Strecke':8s} {'Wert':>5s}  Vorschlag")
    print("-" * 64)
    ergebnis = {}
    for idx in UNSICHER:
        eintrag = nach_index.get(idx, {})
        pieces = eintrag.get("pieces") or []
        if not pieces:
            print(f"idx{idx:02d}     ----   keine Lesungen vorhanden")
            continue
        bester, bester_wert = "", 0.0
        for kandidat in kandidaten(pieces):
            wert = bewerten(kandidat, pieces)
            if wert > bester_wert:
                bester, bester_wert = kandidat, wert
        marke = "ok " if bester_wert >= args.min_score else "?? "
        print(f"idx{idx:02d}  {marke}{bester_wert:5.3f}  {gross(bester)}")
        print(f"          {len(pieces)} Lesungen, schlechteste passt zu "
              f"{min(beste_fenster_aehnlichkeit(p, bester) for p in pieces):.0%}")
        ergebnis[idx] = {"name": gross(bester), "score": round(bester_wert, 4),
                         "readings": len(pieces)}

    ziel = WORKSPACE / "data" / "analytics" / "cross_country_name_proposals.json"
    ziel.parent.mkdir(parents=True, exist_ok=True)
    ziel.write_text(json.dumps(ergebnis, indent=2, ensure_ascii=False), encoding="utf-8")
    print(f"\ngeschrieben: {ziel}")
    print("NICHTS eingetragen -- die Bestaetigung gehoert ans Spiel.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
