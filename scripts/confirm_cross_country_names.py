"""Die sechs offenen Cross-Country-Streckennamen bestaetigen -- aus zwei Quellen.

    python scripts/confirm_cross_country_names.py
    python scripts/confirm_cross_country_names.py --apply

## Warum zwei Quellen

Sechs der 19 Strecken stehen seit dem 2026-08-31 ohne Namen im Katalog, weil die
OCR-Einigkeit zu gering war. Ohne Namen ueberspringt der Sweep sie -- 42 Boards,
lautlos. Mit einem falschen Namen waere es schlimmer: 20.000 Zeilen landen unter
der falschen Beschriftung, und das faellt niemandem auf.

Der Titel LAEUFT IM KREIS durch ein festes Fenster. Jede Lesung ist darum ein
Ausschnitt derselben umlaufenden Zeichenkette, an einer anderen Stelle begonnen --
und damit pruefbar: ein Name stimmt, wenn sich jede Lesung als Fenster im
verdoppelten Namen wiederfindet.

Eingetragen wird nur, was ZWEI unabhaengige Aufnahmen uebereinstimmend hergeben:
die alte vom 2026-08-31 und eine neue. Eine einzelne Aufnahme kann sich
systematisch irren (dieselbe Schrift, dieselbe Kachel, derselbe Fehler); zwei
Aufnahmen an verschiedenen Abenden irren sich nicht auf dieselbe Weise.

## Was es NICHT tut

Raten. Wo die Quellen sich widersprechen oder die Bewertung unter der Schwelle
bleibt, bleibt die Position leer und der Sweep ueberspringt sie weiterhin. Das ist
der Zustand, in dem sie seit zwei Wochen ist -- er kostet Boards, aber keine Daten.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(WORKSPACE / "scripts"))

from reconstruct_cross_country_names import (  # noqa: E402
    bewerten, gross, kandidaten, saeubern,
)

KATALOG = WORKSPACE / "config" / "fh6_board_catalogue.json"
OFFEN = (5, 7, 8, 12, 15, 16)

# Quellen: (Datei, Beschreibung). Die zweite entsteht bei der Neuaufnahme.
QUELLEN = [
    ("route_names_cross_country_alt.json", "Aufnahme 2026-08-31"),
    ("route_names_cross_country.json", "Neuaufnahme"),
]


def lesungen(datei: Path) -> dict[int, list[str]]:
    if not datei.exists():
        return {}
    d = json.loads(datei.read_text(encoding="utf-8"))
    raus: dict[int, list[str]] = {}
    for r in d.get("routes", []):
        stuecke = r.get("pieces") or ([r["name"]] if r.get("name") else [])
        if stuecke:
            raus[r["route_index"]] = stuecke
    return raus


def bester_name(stuecke: list[str]) -> tuple[str, float]:
    bester, wert = "", 0.0
    for kandidat in kandidaten(stuecke):
        w = bewerten(kandidat, stuecke)
        if w > wert:
            bester, wert = kandidat, w
    return bester, wert


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--apply", action="store_true",
                        help="bestaetigte Namen in den Katalog eintragen")
    parser.add_argument("--min-score", type=float, default=0.85)
    args = parser.parse_args(argv)

    quellen = []
    for name, was in QUELLEN:
        gelesen = lesungen(WORKSPACE / "config" / name)
        if gelesen:
            quellen.append((was, gelesen))
        else:
            print(f"FEHLT: {name} ({was})")
    if len(quellen) < 2:
        print("Zwei Quellen sind noetig. Ohne die zweite wird nichts eingetragen.")
        return 1

    katalog = json.loads(KATALOG.read_text(encoding="utf-8"))
    eintraege = katalog["tracks"]["Cross-Country"]["verified"]
    belegt = {saeubern(e["name"]) for e in eintraege}
    schon = {e["route_index"] for e in eintraege}

    bestaetigt: list[tuple[int, str]] = []
    print(f"{'Strecke':8s} {'Quelle A':34s} {'Quelle B':34s} Urteil")
    print("-" * 100)
    for idx in OFFEN:
        namen, werte = [], []
        for _, gelesen in quellen:
            stuecke = gelesen.get(idx, [])
            if not stuecke:
                namen.append(""); werte.append(0.0); continue
            n, w = bester_name(stuecke)
            namen.append(gross(n)); werte.append(w)

        gleich = namen[0] and saeubern(namen[0]) == saeubern(namen[1])
        stark = min(werte) >= args.min_score
        neu = saeubern(namen[0]) not in belegt
        urteil = ("uebernommen" if gleich and stark and neu
                  else "uneinig" if not gleich
                  else "zu unsicher" if not stark
                  else "Name schon vergeben")
        print(f"idx{idx:02d}    {namen[0][:33]:34s} {namen[1][:33]:34s} "
              f"{urteil} ({werte[0]:.2f}/{werte[1]:.2f})")
        if urteil == "uebernommen":
            bestaetigt.append((idx, namen[0]))

    print(f"\n{len(bestaetigt)} von {len(OFFEN)} bestaetigt.")
    if not args.apply:
        print("Probelauf -- nichts eingetragen. Mit --apply schreiben.")
        return 0
    if not bestaetigt:
        print("Nichts einzutragen; die Positionen bleiben leer.")
        return 0

    for idx, name in bestaetigt:
        if idx in schon:
            continue
        eintraege.append({"name": name, "route_index": idx,
                          "confirmed": "zwei unabhaengige Aufnahmen"})
    eintraege.sort(key=lambda e: e["route_index"])
    KATALOG.write_text(json.dumps(katalog, ensure_ascii=False, indent=1),
                       encoding="utf-8")
    print(f"Katalog ergaenzt: {KATALOG}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
