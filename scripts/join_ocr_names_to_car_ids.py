"""Name the memory scans' car_ids from the OCR boards already on disk.

The two routes read the same boards from opposite ends. A memory row carries the game's
own `car_id` and no name; a screen-read row carries the name OCR lifted off the display
and no id. Where both cover the same rank, the lap time joins them -- and the memory
runs that died at 149 rows left exactly that overlap on Hokubu and Shimanoyama, because
the OCR pass then read the same boards from rank 1.

This matters beyond labels. Until a car_id has a name, the analytics build cannot tell
that memory's id 3665 and the screen's "Toyota GR Yaris" are one car, so it lists both
and the car competes against itself in its own class -- inflating the field size that
the points rule is computed from. Joining collapses them.

Unlike `process_name_captures.py` this needs no capture: it reads the OCR boards the
sweep has already written, so it costs nothing and can run any time.

    python scripts/join_ocr_names_to_car_ids.py
"""

from __future__ import annotations

import argparse
import json
import sys
from collections import Counter, defaultdict
from pathlib import Path

import pyarrow.parquet as pq

sys.path.insert(0, str(Path(__file__).resolve().parent))

import build_car_roster as roster_tools  # noqa: E402
from build_car_roster import clean_ocr_name  # noqa: E402

IDS_FILE = Path("config/fh6_car_id_names.json")

# Was ein exakter Treffer aufbringen muss, um einen vorhandenen Namen zu
# ersetzen. Die Begruendung steht unten an der Stelle, die sie anwendet.
UMSTOSS_MINDEST = 5
UMSTOSS_FAKTOR = 2


def board_key(state: dict) -> tuple[str, str]:
    return ((state.get("track") or "").strip().rstrip("t") or "?",
            (state.get("performance_class") or "?").upper())


def unique_by_time(pairs: list[tuple[float, object]]) -> dict[int, object]:
    """Times that occur exactly once, keyed by whole milliseconds.

    A time shared by several rows cannot identify a car -- on a deep board ties at
    millisecond precision are ordinary (Shimanoyama D has one shared by 266 entries) --
    so ambiguous times are dropped rather than guessed at.
    """
    seen: Counter[int] = Counter()
    value: dict[int, object] = {}
    for lap, payload in pairs:
        key = int(round(float(lap) * 1000))
        seen[key] += 1
        value[key] = payload
    return {key: value[key] for key, count in seen.items() if count == 1}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path("data/memory_scans/full_sweep"))
    args = parser.parse_args(argv)

    # (Rundenzeit, Rang, Nutzlast) -- der Rang wird gebraucht, um geteilte Zeiten
    # innerhalb einer Gruppe zu ordnen (siehe Gruppenpaarung weiter unten).
    memory: dict[tuple[str, str], list[tuple[float, int, int]]] = defaultdict(list)
    screen: dict[tuple[str, str], list[tuple[float, int, str]]] = defaultdict(list)

    for state_path in sorted(args.root.glob("*/state.json")):
        state = json.loads(state_path.read_text(encoding="utf-8-sig"))
        key = board_key(state)
        folder = state_path.parent
        if folder.name.startswith("ocr_"):
            rows_path = folder / "rows.jsonl"
            if not rows_path.exists():
                continue
            for line in rows_path.read_text(encoding="utf-8").splitlines():
                if not line.strip():
                    continue
                row = json.loads(line)
                lap, name = row.get("lap_time_seconds"), row.get("car_name")
                if isinstance(lap, (int, float)) and name:
                    screen[key].append((lap, int(row.get("rank") or 0), name))
            continue
        parquet = folder / "leaderboard_entries.parquet"
        if not parquet.exists() or "car_id" not in set(pq.read_schema(parquet).names):
            continue
        cols = [c for c in ("lap_time_seconds", "car_id", "rank") 
                if c in set(pq.read_schema(parquet).names)]
        for row in pq.read_table(parquet, columns=cols).to_pylist():
            lap, car_id = row.get("lap_time_seconds"), row.get("car_id")
            if isinstance(lap, (int, float)) and car_id is not None:
                memory[key].append((lap, int(row.get("rank") or 0), int(car_id)))

    roster = roster_tools.load_roster(Path("data/car_catalogue/fh6cars.html"))
    index = roster_tools.build_index(roster)
    keys = list(index)
    years = roster_tools.build_year_index(roster)

    # ZWEI ZAEHLER, NICHT EINER.
    #
    # Es gibt hier zwei Belege sehr verschiedener Guete:
    #
    #   exakt   eine Rundenzeit, die in BEIDEN Quellen genau einmal vorkommt.
    #           Sie identifiziert ein Auto.
    #   gruppe  eine geteilte Zeit, bei der beide Quellen gleich viele Eintraege
    #           haben und die Rangordnung sie paart. Das ist eine ANNAHME ueber die
    #           Zusammensetzung der Gruppe, kein Beleg ueber ein Auto.
    #
    # Bis zum 2026-09-14 liefen beide in denselben Zaehler, und ueberschrieben wurde
    # nach schierer Menge. Das ging schief, sobald die Gruppenpaarung dazukam:
    # auf Narai-Juku A stehen 1.882 exakte Treffer gegen 10.691 Gruppenpaarungen.
    # Gemessen an einem Lauf an diesem Tag: 37 bereits belegte Namen wurden
    # ueberschrieben, 14 davon durch ein voellig anderes Auto -- aus "Ferrari FXX K"
    # wurde "GMC Jimmy", aus "Lamborghini Countach" ein "Audi RS 6 Avant". Die alten
    # Stimmzahlen stammten aus einem Lauf VOR der Gruppenpaarung und konnten gegen
    # die aufgeblaehten neuen gar nicht bestehen.
    #
    # Die Regel lautet jetzt: Gruppenstimmen benennen ein Auto, das noch keinen Namen
    # hat. Einen vorhandenen Namen umstossen darf nur ein exakter Treffer.
    votes: dict[int, Counter[tuple[str, int | None]]] = defaultdict(Counter)
    group_votes: dict[int, Counter[tuple[str, int | None]]] = defaultdict(Counter)
    boards_joined = 0
    for key in sorted(set(memory) & set(screen)):
        by_time_id = unique_by_time([(l, p) for l, _, p in memory[key]])
        by_time_name = unique_by_time([(l, p) for l, _, p in screen[key]])
        shared = set(by_time_id) & set(by_time_name)
        if not shared:
            continue
        boards_joined += 1
        for stamp in shared:
            name = clean_ocr_name(str(by_time_name[stamp]))
            if not name:
                continue
            # key[1] is the board's performance class, and it is the only thing that
            # separates a car from its Forza Edition -- the screen writes the same
            # abbreviated name for both.
            car, _score = roster_tools.match_car(name, index, keys, years,
                                                 board_class=key[1])
            if car is None:
                continue
            votes[by_time_id[stamp]][(car["name"], car["year"])] += 1
        # GETEILTE ZEITEN: bis 2026-08-24 wurden sie komplett verworfen, weil eine
        # geteilte Zeit kein Auto identifiziert. Damit blieben 89 car_ids ohne Namen --
        # und ein namenloses Auto laeuft in der Wertung zusaetzlich als eigener
        # Teilnehmer mit, blaeht also die Feldgroesse auf.
        #
        # Es geht aber sicherer: hat eine Zeit in BEIDEN Quellen die GLEICHE Anzahl
        # Eintraege, dann sehen beide dieselbe Gruppe. Innerhalb der Gruppe sortiert das
        # Board stabil, also paart die Rangordnung sie eindeutig.
        # Gemessen: 3.631 Gruppen erfuellen die Gleichheit, 4.585 nicht -- letztere
        # bleiben unangetastet, weil dort die Zusammensetzung sich geaendert hat und
        # jede Paarung ein FALSCHES Auto in die Wertung setzen wuerde.
        mem_groups = defaultdict(list)
        scr_groups = defaultdict(list)
        for lap, rank, cid in memory[key]:
            mem_groups[int(round(float(lap) * 1000))].append((rank, cid))
        for lap, rank, nm in screen[key]:
            scr_groups[int(round(float(lap) * 1000))].append((rank, nm))
        paired = 0
        for stamp, mrows in mem_groups.items():
            srows = scr_groups.get(stamp)
            if not srows or len(srows) != len(mrows) or len(mrows) < 2:
                continue
            for (_, cid), (_, nm) in zip(sorted(mrows), sorted(srows)):
                name = clean_ocr_name(str(nm))
                if not name:
                    continue
                car, _score = roster_tools.match_car(name, index, keys, years,
                                                     board_class=key[1])
                if car is None:
                    continue
                group_votes[cid][(car["name"], car["year"])] += 1
                paired += 1
        extra = f", {paired} aus Zeitgruppen" if paired else ""
        print(f"  {key[0][:24]:24} {key[1]:>3}  {len(shared)} matching lap time(s){extra}")

    existing = {}
    if IDS_FILE.exists():
        existing = json.loads(IDS_FILE.read_text(encoding="utf-8")).get("by_car_id", {})

    added = updated = aus_gruppen = 0

    # 1) Die exakten Treffer. Nur sie duerfen einen vorhandenen Namen umstossen.
    for car_id, tally in votes.items():
        (name, year), count = tally.most_common(1)[0]
        record = existing.get(str(car_id))
        if record is None:
            existing[str(car_id)] = {"name": name, "year": year, "basis": "exact",
                                     "votes": count, "samples": sum(tally.values())}
            added += 1
            continue
        # Ein Eintrag ohne Vermerk stammt aus der Zeit vor dieser Unterscheidung.
        # Er gilt als exakt belegt -- das ist die vorsichtige Annahme: sie laesst den
        # Bestand, wie er ist, statt ihn auf eine Vermutung hin umzuschreiben.
        alt_basis = record.get("basis") or "exact"
        # EIN VON HAND BELEGTER NAME ("manual", mit Beleg im Eintrag) bleibt: er stammt
        # vom Schirm des Spiels oder der Garage, nicht aus dem Rundenzeit-Join.
        if alt_basis == "manual":
            record["samples"] = int(record.get("samples") or 0) + sum(tally.values())
            continue
        if record.get("name") == name:
            record["basis"] = "exact"
            record["votes"] = max(int(record.get("votes") or 0), count)
            record["samples"] = int(record.get("samples") or 0) + sum(tally.values())
        elif (alt_basis != "exact"
              or (count >= UMSTOSS_MINDEST
                  and count >= UMSTOSS_FAKTOR * int(record.get("votes") or 0))):
            # EINEN NAMEN UMZUSTOSSEN KOSTET MEHR ALS EINEN ZU SETZEN.
            #
            # "mehr Stimmen als vorher" genuegt nicht. Gemessen am 2026-09-14 an den
            # 36 Aenderungen eines Laufs: die alten Namen hatten meist 1 bis 3
            # Stimmen und die neuen 7 bis 255 -- das sind echte Korrekturen. Am
            # unteren Rand aber stand "BMW M2 Forza Edition" mit 1 Stimme gegen
            # "Ford F-150 Raptor R" mit 3, und "Mercedes-AMG SL 63" mit 7 gegen
            # "Mercedes-AMG C 63 S Coupe" mit 9. Dort entscheidet nicht die Evidenz,
            # sondern der Zufall -- und ein falscher Name setzt das FALSCHE Auto in
            # die Wertung, was schlimmer ist als "Car #3849".
            #
            # Also: mindestens fuenf Stimmen, und mindestens doppelt so viele wie der
            # bisherige Name. Wer das nicht erreicht, laesst den Bestand in Ruhe.
            existing[str(car_id)] = {"name": name, "year": year, "basis": "exact",
                                     "votes": count, "samples": sum(tally.values())}
            updated += 1
        else:
            record["samples"] = int(record.get("samples") or 0) + sum(tally.values())

    # 2) Die Gruppenpaarungen -- nur fuer Autos, die noch gar keinen Namen haben.
    for car_id, tally in group_votes.items():
        if str(car_id) in existing:
            continue
        (name, year), count = tally.most_common(1)[0]
        # Und auch dann nur, wenn die Gruppe sich einig ist. Eine Paarung, die zur
        # Haelfte ein anderes Auto nennt, ist geraten.
        if count < 2 or count * 2 <= sum(tally.values()):
            continue
        existing[str(car_id)] = {"name": name, "year": year, "basis": "group",
                                 "votes": count, "samples": sum(tally.values())}
        aus_gruppen += 1

    IDS_FILE.write_text(json.dumps(
        {"source": "forza.net/fh6cars joined to memory car_id by lap time",
         "by_car_id": dict(sorted(existing.items(), key=lambda kv: int(kv[0])))},
        indent=2, ensure_ascii=False), encoding="utf-8")
    print(f"{boards_joined} board(s) joined; {added} car_id(s) newly named from an "
          f"exact lap time, {aus_gruppen} from a tie group, {updated} corrected, "
          f"{len(existing)} named in total")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
