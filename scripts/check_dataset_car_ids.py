"""Prueft den gebauten Datensatz auf bekannte Auto-Zuordnungen -- Exit 1, wenn eine nicht stimmt.

    python scripts/check_dataset_car_ids.py [data/analytics/laps.json]

Die Faelle hier sind belegt (Schirm des Spiels, Garage, Speicher-Scans); ein Datensatz, der
sie anders nennt, geht nicht hinaus. 2026-10-03: die 2177 hiess "Corvette '53" und bekam
die D-Zeiten der '53 -- die Zeiten einer Z06 '15 in Klasse D gibt es nicht.
"""
import collections
import json
import sys
from pathlib import Path

FAELLE = {
    # car_id: (Name beginnt mit, Jahr, Klassen, in denen es KEINE Zeiten geben darf)
    2177: ("Chevrolet Corvette Z06", 2015, ("D", "C")),
    1564: ("Chevrolet Corvette", 1953, ()),
    3766: ("Chevrolet Corvette Z06", 2023, ("D", "C", "B")),
}


def main() -> int:
    pfad = Path(sys.argv[1] if len(sys.argv) > 1 else "data/analytics/laps.json")
    d = json.loads(pfad.read_text(encoding="utf-8-sig"))
    ids, namen = d["carIds"], d["carNames"]
    fehler = 0
    for cid, (anfang, jahr, verboten) in FAELLE.items():
        if cid not in ids:
            print(f"  {cid}: fehlt im Datensatz"); fehler += 1; continue
        i = ids.index(cid)
        name = namen[i]
        klassen = collections.Counter()
        for bd in d["boards"]:
            for j, c in enumerate(bd.get("gcar") or []):
                if c == i:
                    klassen[d["classes"][bd["k"]]] += bd["gcount"][j]
        gut = name.startswith(anfang) and f"'{jahr % 100:02d}" in name and not any(klassen[k] for k in verboten)
        print(f"  {cid}: {name} {dict(sorted(klassen.items()))} {'ok' if gut else 'FALSCH'}")
        fehler += 0 if gut else 1
    doppelt = [n for n, k in collections.Counter(namen).items() if k > 1 and not n.lower().startswith("car")]
    if doppelt:
        print("  doppelte Namen:", doppelt[:10])
    print("alles stimmt" if not fehler else f"{fehler} Zuordnung(en) falsch")
    return 1 if fehler else 0


if __name__ == "__main__":
    raise SystemExit(main())
