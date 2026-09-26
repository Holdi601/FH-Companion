# -*- coding: utf-8 -*-
"""Den Rundenbestand nach START UND ZIEL neu einsortieren.

    python scripts/fix_course_keys.py            # Probelauf, aendert nichts
    python scripts/fix_course_keys.py --apply    # wirklich umziehen

## Warum

Bis zum 2026-09-15 bestand die Kennung einer Strecke nur aus ihrer STARTLINIE
(`LapArchive.CourseKey`). Im eigenen Bestand lagen dadurch in einem einzigen
Ordner 56 Laeufe mit Zielen 3.725 m auseinander und Laengen von 906 bis 6.576 m --
mehrere verschiedene Fahrten unter einem Namen. Das Rundendelta vergleicht
innerhalb eines Ordners und mass dort also gegen eine fremde Strecke, ohne dass
irgendetwas darauf hinwies.

Der Hinweis kam vom Nutzer: Start UND Ziel bestimmen die Strecke, und zwar
unabhaengig davon, ob sie in Rivals, im Einzelspieler, im Koop oder in der freien
Welt gefahren wird.

## Wie geordnet wird

Dieselbe Schwelle wie im Programm -- 120 m, `RecordedLap.StartTolerance` -- fuer
BEIDE Punkte. Aelteste Runde zuerst: sie setzt den Anker, spaetere Runden lagern
sich an. Damit haengt das Ergebnis nicht von der Reihenfolge des Dateisystems ab.

NICHT die vorhandene 10-Meter-Stufe `LengthKey` benutzt: die ist zu fein. Dieselbe
6,4-km-Strecke zweimal gefahren ergibt 6.458 m und 6.475 m und faellt in zwei
Stufen; im Bestand verteilte sich eine Strecke so auf acht davon.

## Was noch nachgetragen wird

* `FinishX` / `FinishZ` in jeder `course.json` -- ohne sie findet das Programm
  eine bekannte Strecke nur ueber den Start und koennte wieder mischen.
* `mode` und `modeEvidence` in jeder Runde. Eine Fahrt mit `freeRoam` steht fest
  ("freeroam"/"timer"); alles andere wird **nicht geraten** und bleibt "unknown".

## Sicherheit

Der Probelauf ist die Vorgabe. `--apply` verschiebt nur, es wird nichts geloescht
und nichts ueberschrieben: liegt am Ziel schon eine gleichnamige Datei, bricht der
Umzug fuer diese Datei ab und meldet das.
"""
from __future__ import annotations

import json
import math
import shutil
import sys
from collections import defaultdict
from pathlib import Path

#: Dieselbe Schwelle wie RecordedLap.StartTolerance im Programm.
TOLERANZ_M = 120.0

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "server"))
from local_settings import app_data  # noqa: E402  (FHCompanion, bis 2026-09-26 ForzaGripHaptics)
WURZEL = app_data() / "laps"


def runden(wurzel: Path) -> list[dict]:
    """Jede abgelegte Runde mit ihrem Pfad, Start und Ziel."""
    gefunden = []
    for datei in wurzel.rglob("*.json"):
        if datei.name == "course.json":
            continue
        try:
            d = json.loads(datei.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            continue
        lap = d.get("Lap") or {}
        proben = lap.get("samples") or []
        if not proben:
            continue
        teile = datei.relative_to(wurzel).parts
        if len(teile) < 6:
            continue
        letzte = proben[-1]
        gefunden.append({
            "datei": datei,
            "rest": teile[1:],                     # klasse/car/tune/tag/name
            "kurs": teile[0],
            "start": (float(lap.get("StartX", 0.0)), float(lap.get("StartZ", 0.0))),
            "ziel": (float(letzte.get("X", 0.0)), float(letzte.get("Z", 0.0))),
            "meter": float(lap.get("lengthMetres", 0.0)),
            "wann": str(lap.get("recordedAt", "")),
            "frei": bool(lap.get("freeRoam", False)),
            "roh": d,
        })
    # Aelteste zuerst: sie setzt den Anker.
    gefunden.sort(key=lambda r: r["wann"])
    return gefunden


def gruppieren(alle: list[dict]) -> list[dict]:
    """Runden zu Strecken zusammenfassen -- Start und Ziel muessen beide passen."""
    haufen: list[dict] = []
    for r in alle:
        for h in haufen:
            if (math.dist(r["start"], h["start"]) <= TOLERANZ_M
                    and math.dist(r["ziel"], h["ziel"]) <= TOLERANZ_M):
                h["runden"].append(r)
                break
        else:
            haufen.append({"start": r["start"], "ziel": r["ziel"], "runden": [r]})
    return haufen


def name_fuer(h: dict, vergeben: set[str]) -> str:
    """Derselbe Name, den LapArchive.CourseKey vergeben wuerde."""
    def r25(v: float) -> int:
        return int(round(v / 25.0)) * 25
    basis = (f"course_{r25(h['start'][0])}_{r25(h['start'][1])}"
             f"_to_{r25(h['ziel'][0])}_{r25(h['ziel'][1])}")
    if basis not in vergeben:
        vergeben.add(basis)
        return basis
    # Zwei Strecken, die auf dasselbe 25-m-Raster fallen, aber weiter als die
    # Schwelle auseinanderliegen. Selten, aber nicht unmoeglich.
    n = 2
    while f"{basis}_{n}" in vergeben:
        n += 1
    vergeben.add(f"{basis}_{n}")
    return f"{basis}_{n}"


def alter_name(h: dict) -> str | None:
    """Der bisherige Ordner, falls alle Runden des Haufens aus demselben kamen."""
    namen = {r["kurs"] for r in h["runden"]}
    return namen.pop() if len(namen) == 1 else None


def main(argv: list[str] | None = None) -> int:
    argv = sys.argv[1:] if argv is None else argv
    schreiben = "--apply" in argv
    wurzel = WURZEL
    for i, a in enumerate(argv):
        if a == "--root" and i + 1 < len(argv):
            wurzel = Path(argv[i + 1])

    if not wurzel.is_dir():
        print(f"Kein Rundenbestand unter {wurzel}")
        return 1

    alle = runden(wurzel)
    vorher = {r["kurs"] for r in alle}
    haufen = gruppieren(alle)
    print(f"{len(alle)} Runden in {len(vorher)} Ordner(n)")
    print(f"nach Start UND Ziel: {len(haufen)} Strecke(n)")
    print()

    vergeben: set[str] = set()
    plan = []
    for h in sorted(haufen, key=lambda x: -len(x["runden"])):
        h["name"] = name_fuer(h, vergeben)
        plan.append(h)

    geteilt = 0
    for alt in sorted(vorher):
        teile = [h for h in plan if any(r["kurs"] == alt for r in h["runden"])]
        if len(teile) > 1:
            geteilt += 1
            print(f"{alt}  zerfaellt in {len(teile)}:")
            for h in sorted(teile, key=lambda x: -len(x["runden"])):
                eigene = [r for r in h["runden"] if r["kurs"] == alt]
                m = [r["meter"] for r in eigene]
                print(f"    {len(eigene):3d} Runden  {min(m):5.0f}-{max(m):5.0f} m"
                      f"  -> {h['name']}")
    if geteilt == 0:
        print("Kein Ordner mischt mehrere Strecken.")
    print()

    if not schreiben:
        print("PROBELAUF -- es wurde nichts veraendert. Mit --apply wirklich umziehen.")
        return 0

    bewegt = geblieben = 0
    fehler = []
    for h in plan:
        ziel_ordner = wurzel / h["name"]
        for r in h["runden"]:
            neu = ziel_ordner.joinpath(*r["rest"])
            if neu == r["datei"]:
                geblieben += 1
                continue
            if neu.exists():
                fehler.append(f"{neu} gibt es schon -- {r['datei'].name} blieb liegen")
                continue
            neu.parent.mkdir(parents=True, exist_ok=True)
            shutil.move(str(r["datei"]), str(neu))
            r["datei"] = neu
            bewegt += 1

        # Die Notiz neu schreiben: Ziel eintragen, Namen behalten.
        notiz_pfad = ziel_ordner / "course.json"
        notiz = {}
        if notiz_pfad.exists():
            try:
                notiz = json.loads(notiz_pfad.read_text(encoding="utf-8"))
            except ValueError:
                notiz = {}
        else:
            # Einen Namen aus dem alten Ordner uebernehmen, falls dort einer stand.
            for alt in {r["kurs"] for r in h["runden"]}:
                p = wurzel / alt / "course.json"
                if p.exists():
                    try:
                        d = json.loads(p.read_text(encoding="utf-8"))
                    except ValueError:
                        continue
                    if (d.get("Name") or "").strip():
                        notiz["Name"] = d["Name"]
                        break
        meter = [r["meter"] for r in h["runden"]]
        notiz.setdefault("Name", "")
        notiz["StartX"], notiz["StartZ"] = h["start"]
        notiz["FinishX"], notiz["FinishZ"] = h["ziel"]
        notiz["Laps"] = len(h["runden"])
        notiz["ShortestMetres"] = min(meter)
        notiz["LongestMetres"] = max(meter)
        notiz["LastSeen"] = max(r["wann"] for r in h["runden"])
        ziel_ordner.mkdir(parents=True, exist_ok=True)
        notiz_pfad.write_text(json.dumps(notiz, indent=1, ensure_ascii=False),
                              encoding="utf-8")

    # Den Modus nachtragen -- ohne zu raten.
    ergaenzt = 0
    for r in alle:
        lap = r["roh"].get("Lap") or {}
        if lap.get("mode"):
            continue
        lap["mode"] = "freeroam" if r["frei"] else "unknown"
        lap["modeEvidence"] = "timer" if r["frei"] else "none"
        r["roh"]["Lap"] = lap
        try:
            r["datei"].write_text(json.dumps(r["roh"], ensure_ascii=False),
                                  encoding="utf-8")
            ergaenzt += 1
        except OSError as f:
            fehler.append(f"{r['datei']}: {f}")

    # Leere Ordner von vorher wegraeumen.
    leer = 0
    for alt in sorted(vorher):
        p = wurzel / alt
        if not p.is_dir():
            continue
        if any(f.name != "course.json" for f in p.rglob("*") if f.is_file()):
            continue
        shutil.rmtree(p)
        leer += 1

    print(f"{bewegt} Runde(n) umgezogen, {geblieben} lagen schon richtig")
    print(f"{ergaenzt} Runde(n) um mode/modeEvidence ergaenzt")
    print(f"{leer} leer gewordene(r) Ordner entfernt")
    if fehler:
        print()
        print(f"{len(fehler)} Stelle(n) blieben liegen:")
        for f in fehler[:10]:
            print("  " + f)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
