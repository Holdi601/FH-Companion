"""Aus der Garagen-Datenbank des Spiels lesbare Tuning-Zettel machen.

## Woher die Daten kommen

`dump_sqlite_from_memory.py` holt die SQLite-Datenbanken aus dem laufenden Spiel.
Zwei davon lassen sich oeffnen und enthalten `Career_Garage` -- je Auto eine Zeile
mit ALLEN Ausbaustufen, ALLEN Tuning-Einstellungen und der Herkunft des Tunes
(`VersionedTuneId`, `VersionedTuneXUID`, `TuneFileName`).

Das ist genau der Bauplan, den der Nutzer auf der Seite sehen will: "zeig, was am
Auto gemacht wurde, damit man es nachbauen kann."

## Die zwei Dinge, die man hier wissen muss

**1. Die Tuning-Werte sind normalisiert, 0 bis 1.** Der Bildschirm zeigt 2,1 BAR,
in der Datenbank steht 0,4. Das ist die Reglerposition, nicht der Anzeigewert. Am
2026-08-30 habe ich Stunden damit verbracht, im Speicher nach "2.1" zu suchen -- die
Zahl existiert dort nicht. Ohne die Skala je Auto und Feld laesst sich der
Anzeigewert NICHT zurueckrechnen; dieses Skript gibt darum ehrlich die Reglerposition
aus und markiert sie als solche. Wer echte Einheiten will, muss die Skalen einmal je
Feld eichen (Regler auf Anschlag, Wert ablesen).

**2. Der Fahrer ist NICHT der Tuner.** `VersionedTuneXUID` ist der Ersteller des
Tunes, nicht der Besitzer des Autos. In dieser Garage tragen **558 von 569** Autos
ein fremdes Tune, von **279 verschiedenen** Erstellern. Genau davor hat der Nutzer
gewarnt, und die Zahlen geben ihm recht.

    python scripts/read_garage_tunes.py --db data/runtime/forza_dbs/forza_*.db
    python scripts/read_garage_tunes.py --car-id 4222
    python scripts/read_garage_tunes.py --tuners
    python scripts/read_garage_tunes.py --self-test
"""

from __future__ import annotations

import argparse
import glob
import json
import sqlite3
import sys
from pathlib import Path

# Die Ausbaustufen, wie sie in Career_Garage heissen. -1 bedeutet "nicht verbaut".
PART_COLUMNS = [
    "Engine", "Drivetrain", "CarBody", "Motor", "Brakes", "SpringDamper",
    "AntiSwayFront", "AntiSwayRear", "TireCompound", "RearWing", "RimSizeFront",
    "RimSizeRear", "Camshaft", "Valves", "Displacement", "PistonsCompression",
    "FuelSystem", "Ignition", "Exhaust", "Intake", "Flywheel", "Manifold",
    "RestrictorPlate", "OilCooling", "SingleTurbo", "TwinTurbo", "QuadTurbo",
    "SuperchargerCSC", "SuperchargerDSC", "Intercooler", "Clutch", "Transmission",
    "Driveline", "Differential", "FrontBumper", "RearBumper", "Hood", "SideSkirts",
    "TireWidthFront", "TireWidthRear", "WeightReduction", "ChassisStiffness",
    "TrackSpacingFront", "TrackSpacingRear", "FrontAspectRatio", "RearAspectRatio",
    "MotorParts", "TireBrand", "WheelStyle", "WheelStyleRear",
]

# Die Einstellungen, gruppiert wie der Tuning-Schirm des Spiels.
TUNE_GROUPS: list[tuple[str, list[str]]] = [
    ("Reifen", ["Tuning_frontTirePressure", "Tuning_rearTirePressure"]),
    ("Getriebe", ["Tuning_finalDriveRatio", "Tuning_firstGear", "Tuning_secondGear",
                  "Tuning_thirdGear", "Tuning_fourthGear", "Tuning_fifthGear",
                  "Tuning_sixthGear", "Tuning_seventhGear", "Tuning_eighthGear",
                  "Tuning_ninthGear", "Tuning_tenthGear"]),
    ("Achsvermessung", ["Tuning_frontCamber", "Tuning_rearCamber", "Tuning_frontToe",
                        "Tuning_rearToe", "Tuning_frontCaster"]),
    ("Stabilisatoren", ["Tuning_frontSwaybar", "Tuning_rearSwaybar"]),
    ("Federn", ["Tuning_frontSpring", "Tuning_rearSpring",
                "Tuning_frontRideHeight", "Tuning_rearRideHeight"]),
    ("Daempfung", ["Tuning_frontDampingStiffness", "Tuning_rearDampingStiffness",
                   "Tuning_frontBumpRatio", "Tuning_rearBumpRatio"]),
    ("Aerodynamik", ["Tuning_frontDownforce", "Tuning_rearDownforce"]),
    ("Bremsen", ["Tuning_brakeBalance", "Tuning_brakePressure"]),
    ("Differential", ["Tuning_frontAccel", "Tuning_rearAccel", "Tuning_frontDecel",
                      "Tuning_rearDecel", "Tuning_centerTorque"]),
]

# -1 heisst bei den Tuning-Feldern "nicht vorhanden" (etwa ein siebter Gang bei einem
# Sechsganggetriebe), NICHT "auf Null gestellt".
NOT_PRESENT = -1.0


def open_db(path: str) -> sqlite3.Connection:
    con = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
    con.row_factory = sqlite3.Row
    return con


def usable(path: str) -> bool:
    try:
        con = open_db(path)
        names = {r[0] for r in con.execute(
            "SELECT name FROM sqlite_master WHERE type='table'")}
        con.close()
        return "Career_Garage" in names
    except Exception:
        return False


def parts_of(row: sqlite3.Row) -> dict[str, int]:
    out: dict[str, int] = {}
    for name in PART_COLUMNS:
        if name in row.keys():
            value = row[name]
            if value is not None and value != -1:
                out[name] = value
    return out


def tune_of(row: sqlite3.Row) -> list[tuple[str, list[tuple[str, float]]]]:
    groups: list[tuple[str, list[tuple[str, float]]]] = []
    for title, columns in TUNE_GROUPS:
        values: list[tuple[str, float]] = []
        for column in columns:
            if column in row.keys():
                value = row[column]
                if value is not None and value != NOT_PRESENT:
                    values.append((column.replace("Tuning_", ""), round(value, 4)))
        if values:
            groups.append((title, values))
    return groups


def sheet(row: sqlite3.Row) -> str:
    lines = [
        f"Auto {row['CarId']}   PI {round((row['PerformanceIndex'] or 0) * 1000)}"
        f"   Klasse {row['ClassID']}   Teilewert {row['PartsValue']}",
    ]
    tuner = row["VersionedTuneXUID"] if "VersionedTuneXUID" in row.keys() else None
    tune_file = row["TuneFileName"] if "TuneFileName" in row.keys() else ""
    if tuner:
        lines.append(f"  TUNE VON xuid {tuner}   Datei {tune_file or '-'}")
        lines.append("  (das ist der ERSTELLER, nicht der Fahrer)")
    else:
        lines.append("  kein fremdes Tune -- eigener Aufbau oder Werkszustand")

    parts = parts_of(row)
    lines.append(f"  Teile ({len(parts)} verbaut):")
    row_out: list[str] = []
    for name, value in parts.items():
        row_out.append(f"{name}={value}")
        if len(row_out) == 4:
            lines.append("      " + ", ".join(row_out))
            row_out = []
    if row_out:
        lines.append("      " + ", ".join(row_out))

    lines.append("  Einstellungen (Reglerposition 0..1, NICHT der Anzeigewert):")
    for title, values in tune_of(row):
        text = ", ".join(f"{n} {v}" for n, v in values)
        lines.append(f"      {title}: {text}")
    return "\n".join(lines)


def self_test() -> int:
    ok = True

    def check(label: str, condition: bool) -> None:
        nonlocal ok
        print(f"  {'ok  ' if condition else 'FEHL'}  {label}")
        ok = ok and condition

    con = sqlite3.connect(":memory:")
    con.row_factory = sqlite3.Row
    columns = (["CarId", "PerformanceIndex", "ClassID", "PartsValue",
                "VersionedTuneXUID", "TuneFileName"]
               + PART_COLUMNS
               + [c for _, cols in TUNE_GROUPS for c in cols])
    con.execute("CREATE TABLE Career_Garage (" +
                ",".join(f'"{c}"' for c in columns) + ")")
    values: list = [4222, 0.886, 6, 127500, 2535400000000003, "Tuning_4222"]
    values += [-1] * len(PART_COLUMNS)
    values += [-1.0] * sum(len(c) for _, c in TUNE_GROUPS)
    con.execute("INSERT INTO Career_Garage VALUES (" +
                ",".join("?" * len(columns)) + ")", values)
    row = con.execute("SELECT * FROM Career_Garage").fetchone()

    check("nicht verbaute Teile fallen raus", parts_of(row) == {})
    check("nicht vorhandene Einstellungen fallen raus", tune_of(row) == [])
    text = sheet(row)
    check("Zettel nennt den Ersteller", "2535400000000003" in text)
    check("Zettel warnt vor der Verwechslung", "nicht der Fahrer" in text)
    check("Zettel nennt die Reglerposition", "Reglerposition" in text)

    con.execute("UPDATE Career_Garage SET Engine=4222002, "
                'Tuning_frontTirePressure=0.4, Tuning_rearTirePressure=0.375')
    row = con.execute("SELECT * FROM Career_Garage").fetchone()
    check("verbautes Teil erscheint", parts_of(row) == {"Engine": 4222002})
    groups = dict(tune_of(row))
    check("Reifengruppe erscheint",
          groups.get("Reifen") == [("frontTirePressure", 0.4),
                                   ("rearTirePressure", 0.375)])
    check("Spaltenzahl stimmt", len(PART_COLUMNS) == 50)
    con.close()

    print("\nalles bestanden" if ok else "\nFEHLGESCHLAGEN")
    return 0 if ok else 1


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--db", default="data/runtime/forza_dbs/*.db")
    parser.add_argument("--car-id", type=int, default=0)
    parser.add_argument("--limit", type=int, default=3)
    parser.add_argument("--tuners", action="store_true",
                        help="Rangliste der Ersteller in dieser Garage")
    parser.add_argument("--out", type=Path, default=None)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()

    if args.self_test:
        return self_test()

    candidates = [p for p in sorted(glob.glob(args.db)) if usable(p)]
    if not candidates:
        raise SystemExit(f"keine brauchbare Garagen-Datenbank unter {args.db}")
    con = open_db(candidates[0])
    print(f"Garage aus {candidates[0]}")

    if args.tuners:
        rows = con.execute(
            "SELECT VersionedTuneXUID AS xuid, COUNT(*) AS n FROM Career_Garage "
            "WHERE VersionedTuneXUID IS NOT NULL AND VersionedTuneXUID <> 0 "
            "GROUP BY xuid ORDER BY n DESC, xuid LIMIT 20").fetchall()
        total = con.execute(
            "SELECT COUNT(*) FROM Career_Garage "
            "WHERE VersionedTuneXUID IS NOT NULL AND VersionedTuneXUID <> 0"
        ).fetchone()[0]
        print(f"\n{total} Autos mit fremdem Tune. Haeufigste Ersteller:")
        print("  (xuid, weil der Name in dieser Tabelle nicht steht)")
        for row in rows:
            print(f"    {row['n']:>4} Autos   xuid {row['xuid']}")
        con.close()
        return 0

    if args.car_id:
        rows = con.execute("SELECT * FROM Career_Garage WHERE CarId = ?",
                           (args.car_id,)).fetchall()
    else:
        rows = con.execute(
            "SELECT * FROM Career_Garage ORDER BY PerformanceIndex DESC LIMIT ?",
            (args.limit,)).fetchall()
    for row in rows:
        print()
        print(sheet(row))
    if args.out:
        payload = [{"car_id": r["CarId"],
                    "performance_index": r["PerformanceIndex"],
                    "class_id": r["ClassID"],
                    "creator_xuid": r["VersionedTuneXUID"],
                    "tune_file": r["TuneFileName"],
                    "parts": parts_of(r),
                    "tune_slider_positions": {n: v for _, vs in tune_of(r)
                                              for n, v in vs}}
                   for r in con.execute("SELECT * FROM Career_Garage")]
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(json.dumps(payload, indent=1), encoding="utf-8")
        print(f"\n{len(payload)} Autos -> {args.out}")
    con.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
