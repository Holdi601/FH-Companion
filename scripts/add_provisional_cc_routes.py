"""Den sechs namenlosen Cross-Country-Strecken einen VORLAEUFIGEN Eintrag geben.

    python scripts/add_provisional_cc_routes.py
    python scripts/add_provisional_cc_routes.py --apply
    python scripts/add_provisional_cc_routes.py --remove   # zuruecknehmen

## Warum das vertretbar ist -- und warum es nicht gegen die Regel verstoesst

Die Regel lautet: lieber gar nicht scannen als Zeilen unter einem falschen
Streckennamen ablegen. Sie schuetzt davor, dass 20.000 Zeilen unbemerkt unter dem
Namen einer ANDEREN echten Strecke landen -- so geschehen, als der Navigator eine
Road-Racing-Liste fuer Touge hielt.

Ein Name wie "Cross-Country Route 05 (unbenannt)" kann mit keiner echten Strecke
verwechselt werden. Er ist als Platzhalter erkennbar, er kollidiert mit keinem
Namen im Katalog, und er ist folgenlos zu ersetzen: im Datensatz ist ein Board
`{"c": Kategorie, "t": Streckenindex, "k": Klasse}` -- die IDENTITAET ist der
Index, der Name nur dessen Beschriftung. Die Rohdaten der Sweeps tragen den Index
ohnehin im Dateinamen.

Der Preis ist, dass die Website diese Strecken bis zur Umbenennung mit dem
Platzhalter zeigt. Der Gegenwert sind 42 Boards mit rund 800.000 Rundenzeiten, die
sonst weiter fehlen.

## Und der Nebeneffekt, der das eigentliche Problem loest

Der Navigator schreibt bei jeder Anfahrt mit, was er auf dem Schirm liest
("route index 5 confirmed on screen as '...'"). Jeder Scan liefert damit eine
Lesung des ECHTEN Namens -- sieben je Strecke, bei sieben Klassen. Aus denen
laesst sich der Name danach mit ganz anderer Sicherheit bestimmen als aus der
Laufschrift der Streckenliste.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
KATALOG = WORKSPACE / "config" / "fh6_board_catalogue.json"
OFFEN = (5, 7, 8, 12, 15, 16)
MARKE = "(unbenannt)"


def vorlaeufig(idx: int) -> str:
    return f"Cross-Country Route {idx:02d} {MARKE}"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--remove", action="store_true")
    args = parser.parse_args(argv)

    katalog = json.loads(KATALOG.read_text(encoding="utf-8"))
    eintraege = katalog["tracks"]["Cross-Country"]["verified"]
    vorhanden = {e["route_index"]: e for e in eintraege}

    if args.remove:
        bleiben = [e for e in eintraege if MARKE not in e.get("name", "")]
        weg = len(eintraege) - len(bleiben)
        print(f"{weg} vorlaeufige Eintraege entfernt.")
        if args.apply or True:
            katalog["tracks"]["Cross-Country"]["verified"] = bleiben
            KATALOG.write_text(json.dumps(katalog, ensure_ascii=False, indent=1),
                               encoding="utf-8")
        return 0

    neu = []
    for idx in OFFEN:
        if idx in vorhanden:
            print(f"idx{idx:02d}: schon belegt mit {vorhanden[idx]['name']!r}")
            continue
        neu.append({"name": vorlaeufig(idx), "route_index": idx,
                    "provisional": True,
                    "why": "Name unbestaetigt; Index ist die Identitaet, "
                           "der Name wird nach dem Scan aus den Navigatorlesungen "
                           "ersetzt."})
        print(f"idx{idx:02d}: {vorlaeufig(idx)}")

    if not args.apply:
        print(f"\nProbelauf -- {len(neu)} Eintraege waeren zu setzen. Mit --apply.")
        return 0

    eintraege.extend(neu)
    eintraege.sort(key=lambda e: e["route_index"])
    KATALOG.write_text(json.dumps(katalog, ensure_ascii=False, indent=1),
                       encoding="utf-8")
    print(f"\nKatalog ergaenzt: {len(neu)} vorlaeufige Strecken. "
          f"Zuruecknehmen mit --remove.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
