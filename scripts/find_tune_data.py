"""Vollstaendige Tune-Datensaetze im Spielspeicher finden und lesbar machen.

## Worum es geht

Der Share-Code eines fremden Tunes ist bisher nirgends aufzutreiben. Der Nutzer hat
darauf die bessere Frage gestellt: **wenn schon kein Code, dann zeig, WAS getunt
wurde** -- Teileliste und Einstellungen, damit man den Wagen nachbauen kann.

Und genau das steht in der Typtabelle des Spiels, vollstaendig:

    TuningData (636 B)            TuneData (632 B)          VersionedTuneData (640 B)
      +8    InstalledParts          +8    installedParts      +12  CarId
      +424  CarTuneDef              +424  tuning              +16  CreatorXuid
      +624  CarId                   +624  creatorXuid         +24  InstalledParts
      +628  CarMakeId                                         +440 Tuning
      +632  CarClassId

`CInstalledParts` (404 B) fuehrt **49 benannte Teileplaetze** -- Motor, Antrieb,
Turbo, Reifenmischung, Fluegel, Gewichtsreduktion und so fort. `CCarTuneDef` fuehrt
die Einstellungen: Anpressdruck vorn/hinten, Achsantrieb, Bremsdruck und -balance,
Mittendifferential, und je Achse Reifendruck, Sturz, Spur, Nachlauf, Federn,
Stabilisator, Fahrhoehe, Zug- und Druckstufe sowie die beiden LSD-Werte. Dazu die
Getriebeuebersetzungen und die Tankfuellung.

Das ist Zeile fuer Zeile der Tuning-Schirm des Spiels. Wer das hat, kann nachbauen.

**`TuneData` traegt ausserdem `creatorXuid`.** Unser eigener Bestand kennt zu 447.000
Runden xuid UND Gamertag -- eine gefundene Ersteller-xuid liesse sich also gegen die
eigenen Daten aufloesen, ohne den verschluesselten Dienst zu fragen.

## Wie gesucht wird

Anker ist der Schwanz von `TuningData`: drei kleine Ganzzahlen hintereinander
(CarId < 65536, CarMakeId < 65536, CarClassId <= 7), also zwoelf Byte, deren obere
Haelften null sind. Das findet der Regex schnell. Erst auf den Treffern werden die
Gleitkommawerte geprueft -- die haben enge, physikalisch sinnvolle Bereiche und
sortieren den Zufall aus.

## Ehrlich zu den Einheiten

Welche Einheit hinter `m_Pressure` steht (bar oder psi) und ob `m_BrakeBias` 0..1
oder 0..100 laeuft, ist NICHT aus der Tabelle ablesbar. Dieses Skript meldet die
rohen Zahlen und markiert sie als ungeeicht. Geeicht wird spaeter an einem bekannten
Fall: den eigenen Wagen im Tuning-Schirm ablesen und mit dem Fund vergleichen.

    python scripts/find_tune_data.py
    python scripts/find_tune_data.py --layout all --max-hits 50
    python scripts/find_tune_data.py --self-test

Laeuft im GAST. Es liest nur -- kein Tastendruck, keine Navigation.
"""

from __future__ import annotations

import argparse
import ctypes
import json
import re
import struct
import sys
import time
from ctypes import wintypes
from pathlib import Path
from typing import Iterable

PROCESS_QUERY_INFORMATION = 0x0400
PROCESS_VM_READ = 0x0010
MEM_COMMIT = 0x1000
MEM_PRIVATE = 0x20000
PAGE_NOACCESS = 0x01
PAGE_GUARD = 0x100

# Die 49 benannten Teileplaetze aus CInstalledParts, in der Reihenfolge der Tabelle.
# Alles dahinter ist im Spiel unbenutzt (_unknown0..50) und wird nicht gemeldet.
PART_NAMES = [
    "Version", "Engine", "Drivetrain", "CarBody", "Motor", "Brakes", "SpringDamper",
    "AntiSwayFront", "AntiSwayRear", "TireCompound", "RearWing", "RimSizeFront",
    "RimSizeRear", "Camshaft", "Valves", "Displacement", "PistonsCompression",
    "FuelSystem", "Ignition", "Exhaust", "Intake", "Flywheel", "Manifold",
    "RestrictorPlate", "OilCooling", "SingleTurbo", "TwinTurbo", "QuadTurbo",
    "SuperchargerCSC", "SuperchargerDSC", "Intercooler", "Clutch", "Transmission",
    "Driveline", "Differential", "FrontBumper", "RearBumper", "Hood", "SideSkirts",
    "TireWidthFront", "TireWidthRear", "WeightReduction", "ChassisStiffness",
    "MotorParts", "WheelStyle", "_unused_Aspiration", "TrackSpacingFront",
    "TrackSpacingRear", "FrontAspectRatioOffset", "RearAspectRatioOffset",
]

# CCarTuneDefBase, Offsets relativ zum Anfang des CarTuneDef-Blocks.
TUNE_SCALARS = [
    (8, "FrontDownforce"), (12, "RearDownforce"), (16, "FinalDriveRatio"),
    (20, "BrakePressure"), (24, "BrakeBias"), (28, "HandbrakePressure"),
    (32, "CenterDiffTorqueSplit"), (36, "STMScale"), (40, "TCSTakeOffSlip"),
    (44, "TCSMovingSlip"), (48, "SteerSpeed"), (52, "SteerAmount"),
]
SUSP_FRONT_AT = 56
SUSP_REAR_AT = 112
FUEL_LOAD_AT = 168
SUSP_FIELDS = [
    (8, "Pressure"), (12, "Camber"), (16, "Toe"), (20, "Caster"), (24, "Springs"),
    (28, "Swaybar"), (32, "RideHeight"), (36, "DampBump"), (40, "DampRebound"),
    (44, "LSDTorqueAccel"), (48, "LSDTorqueDecel"),
]

LAYOUTS = {
    # Name              Satzlaenge  Teile bei  Tune bei  CarId bei  ClassId bei
    "TuningData": {"size": 636, "parts": 8, "tune": 424, "car_id": 624,
                   "make_id": 628, "class_id": 632, "creator_xuid": None},
    "TuneData": {"size": 632, "parts": 8, "tune": 424, "car_id": None,
                 "make_id": None, "class_id": None, "creator_xuid": 624},
    "VersionedTuneData": {"size": 640, "parts": 24, "tune": 440, "car_id": 12,
                          "make_id": None, "class_id": None, "creator_xuid": 16},
}

# Zwoelf Byte, in denen drei kleine Ganzzahlen stehen: CarId, CarMakeId, CarClassId.
#
# Die erste Fassung liess die unteren Haelften voellig offen. Das trifft im Heap
# millionenfach, und weil hinter jedem Treffer eine Pruefung von 50 Ganzzahlen und
# 34 Gleitkommazahlen haengt, lief der Suchlauf am 2026-08-30 in 15 Minuten nicht
# durch -- er wurde abgeschnitten und lieferte GAR NICHTS. Ein Werkzeug, das nicht
# fertig wird, ist schlimmer als eines, das nichts findet: es sagt einem nicht mal,
# dass es nichts gefunden hat.
#
# Darum sind die oberen Bytes jetzt eingegrenzt: CarId <= 30000 (hohes Byte <= 0x75)
# und CarMakeId <= 5119 (hohes Byte <= 0x13). Das kostet keine Genauigkeit -- Werte
# darueber haetten die spaetere Pruefung ohnehin nicht bestanden.
# Die beiden Negativ-Vorschauen sind der eigentliche Hebel. Ohne sie passt der Anker
# auf JEDE Stelle in einem genullten Speicherbereich -- und davon hat ein Spiel sehr
# viel. Die Zahl der Treffer ging dadurch in die Millionen, und die reine
# Python-Schleife darueber kostete mehr als alles andere zusammen. CarId 0 und
# MakeId 0 gibt es nicht, also darf der Anker sie gar nicht erst anbieten.
TAIL_ANCHOR = re.compile(
    rb"(?!\x00\x00)[\s\S][\x00-\x75]\x00\x00"
    rb"(?!\x00\x00)[\s\S][\x00-\x13]\x00\x00"
    rb"[\x00-\x07]\x00\x00\x00")


class MemoryBasicInformation(ctypes.Structure):
    _fields_ = [
        ("BaseAddress", ctypes.c_void_p),
        ("AllocationBase", ctypes.c_void_p),
        ("AllocationProtect", wintypes.DWORD),
        ("PartitionId", wintypes.WORD),
        ("RegionSize", ctypes.c_size_t),
        ("State", wintypes.DWORD),
        ("Protect", wintypes.DWORD),
        ("Type", wintypes.DWORD),
    ]


if sys.platform == "win32":
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel32.OpenProcess.restype = wintypes.HANDLE
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel32.CloseHandle.restype = wintypes.BOOL
    kernel32.VirtualQueryEx.argtypes = [wintypes.HANDLE, ctypes.c_void_p,
                                        ctypes.POINTER(MemoryBasicInformation),
                                        ctypes.c_size_t]
    kernel32.VirtualQueryEx.restype = ctypes.c_size_t
    kernel32.ReadProcessMemory.argtypes = [wintypes.HANDLE, ctypes.c_void_p,
                                           ctypes.c_void_p, ctypes.c_size_t,
                                           ctypes.POINTER(ctypes.c_size_t)]
    kernel32.ReadProcessMemory.restype = wintypes.BOOL


def f32(raw: bytes, at: int) -> float:
    return struct.unpack_from("<f", raw, at)[0]


def i32(raw: bytes, at: int) -> int:
    return struct.unpack_from("<i", raw, at)[0]


def finite(value: float) -> bool:
    return value == value and abs(value) != float("inf")


def sane(value: float, low: float, high: float) -> bool:
    """Endlich, nicht denormal, und im erwarteten Bereich.

    Das "nicht denormal" ist der Punkt, an dem die erste Fassung dieses Filters
    scheiterte: Zufallsbytes werden haeufig zu Winzlingen wie 1e-42. Die sind
    groesser als null, endlich und kleiner als jede Obergrenze -- eine Pruefung auf
    "Federrate > 0" laesst sie also anstandslos durch, und der ganze Heap sieht
    ploetzlich nach Tunes aus. Genau das ist am 2026-08-30 passiert: 25 Funde, alle
    Muell, mit Federraten von "0.0", die in Wahrheit 1e-42 waren.
    """
    if not finite(value):
        return False
    if value != 0.0 and abs(value) < 1e-3:
        return False
    return low <= value <= high


def plausible_parts(raw: bytes, at: int) -> bool:
    """Die Teileliste ist der schaerfste Filter, den es hier gibt.

    Ausbaustufen sind kleine Ganzzahlen -- ein Teileplatz haelt einen Index in eine
    Liste von Teilen, keine Adresse. Steht dort 1073741824 (das ist 0x40000000, die
    Bytes einer Gleitkommazahl 2.0), ist der Satz kein Satz. Diese eine Pruefung
    raeumt die Fehltreffer weg, an denen die Gleitkommapruefung allein scheitert.
    """
    for index in range(len(PART_NAMES)):
        value = i32(raw, at + index * 4)
        if value < -1 or value > 2000:
            return False   # sofort raus, nicht erst die ganze Liste bauen
    return True


def plausible_tune(block: bytes) -> bool:
    """Sieht dieser CarTuneDef-Block nach einem echten Tune aus?

    Geprueft wird NICHT auf enge Wunschbereiche -- die Einheiten sind ungeeicht, und
    ein zu enger Filter wuerde genau die Faelle wegwerfen, wegen derer gesucht wird.
    Geprueft wird auf das, was ohne Einheitenwissen sicher ist: alle Werte endlich,
    keine absurden Groessenordnungen, Federn und Fahrhoehe positiv, und die
    Bremsbalance in einem der beiden moeglichen Bereiche (0..1 oder 0..100).
    """
    values = [f32(block, at) for at, _ in TUNE_SCALARS]
    if not all(finite(v) for v in values):
        return False
    if not all(abs(v) < 1e6 for v in values):
        return False
    if not any(v != 0.0 for v in values):
        return False

    # Zufall wiederholt sich: ein Block, in dem zwoelf Werte nur zwei oder drei
    # verschiedene Zahlen sind, ist ein Muster im Speicher und kein Fahrzeug.
    if len({round(v, 4) for v in values}) < 5:
        return False

    # UND Zufall ist gleichfoermig. Der Fehltreffer vom 2026-08-30 hatte zwoelf
    # verschiedene Werte -- aber alle zwischen 7,8 und 14,4, also ein Stueck aus
    # einem Feld gleichartiger Gleitkommazahlen (Vertexdaten, Gewichte, was auch
    # immer). Ein echter Tune ist das Gegenteil von gleichfoermig: Anpressdruck
    # geht in die Hunderte, ein Uebersetzungsverhaeltnis liegt bei 3, die
    # Bremsbalance bei 50, und mehrere Felder sind schlicht 0. Verlangt wird
    # deshalb Spannweite -- entweder eine echte Null oder zwei Groessenordnungen
    # zwischen dem kleinsten und dem groessten Betrag.
    magnitudes = [abs(v) for v in values if v != 0.0]
    has_zero = len(magnitudes) < len(values)
    if magnitudes and not has_zero:
        if max(magnitudes) / min(magnitudes) < 20.0:
            return False

    if not sane(f32(block, 16), 1.0, 20.0):        # Achsantrieb
        return False
    if not sane(f32(block, 24), 0.01, 100.0):      # Bremsbalance
        return False

    for base in (SUSP_FRONT_AT, SUSP_REAR_AT):
        # Untergrenzen mit Absicht deutlich ueber null: eine Federrate von 1e-42
        # ist keine weiche Feder, sondern Muell (siehe sane()).
        if not sane(f32(block, base + 24), 1.0, 1e6):      # Federn
            return False
        if not sane(f32(block, base + 32), 0.5, 1e4):      # Fahrhoehe
            return False
        if not sane(f32(block, base + 8), 0.5, 1e3):       # Reifendruck
            return False
        camber = f32(block, base + 12)
        # Sturz darf null sein, liegt aber in JEDER Einheit zwischen -20 und 20.
        if not finite(camber) or not -20.0 <= camber <= 20.0:
            return False
    return True


def decode_parts(raw: bytes, at: int) -> dict:
    out: dict[str, int] = {}
    for index, name in enumerate(PART_NAMES):
        out[name] = i32(raw, at + index * 4)
    return out


def decode_susp(block: bytes, base: int) -> dict:
    return {name: round(f32(block, base + off), 4) for off, name in SUSP_FIELDS}


def decode_tune(raw: bytes, at: int) -> dict:
    block = raw[at:at + 176]
    tune = {name: round(f32(block, off), 4) for off, name in TUNE_SCALARS}
    tune["FuelLoad"] = round(f32(block, FUEL_LOAD_AT), 4)
    tune["Front"] = decode_susp(block, SUSP_FRONT_AT)
    tune["Rear"] = decode_susp(block, SUSP_REAR_AT)
    return tune


def decode(raw: bytes, layout: dict, address: int, name: str = "?") -> dict | None:
    if len(raw) < layout["size"]:
        return None
    # Teile zuerst: schaerfer UND billiger als die Gleitkommapruefung, und sie
    # bricht beim ersten unmoeglichen Wert ab.
    if not plausible_parts(raw, layout["parts"]):
        return None
    tune_at = layout["tune"]
    if not plausible_tune(raw[tune_at:tune_at + 176]):
        return None
    record: dict = {
        "layout": name,
        "address": hex(address),
        "units": "ungeeicht -- rohe Gleitkommawerte, siehe Modulkopf",
        "parts": decode_parts(raw, layout["parts"]),
        "tune": decode_tune(raw, tune_at),
    }
    for key in ("car_id", "make_id", "class_id"):
        if layout[key] is not None:
            record[key] = i32(raw, layout[key])
    if layout["creator_xuid"] is not None:
        record["creator_xuid"] = struct.unpack_from("<Q", raw, layout["creator_xuid"])[0]
    return record


# --- Der Behaelter: TuningItem = UGCItem + TuningData -------------------------
#
# TuningItem (1196 B) = UGCItem (552 B) + Data (TuningData) bei +552.
# Im UGCItem stehen die Angaben, die ein Tune erst zuschreibbar machen:
#
#     +144  Id         versioned_id
#     +184  ShareCode  kurze Zeichenkette
#     +248  Creator    UserData -> qwXuid UND wzGamerTag
#
# ACHTUNG, UND DAS IST DER WICHTIGSTE SATZ IN DIESER DATEI:
# **Der Ersteller ist NICHT der Fahrer.** Wer eine Runde faehrt, hat das Tune in der
# Regel von jemand anderem heruntergeladen -- laut Nutzer stimmen die beiden Namen
# "die meiste Zeit" nicht ueberein. Der Gamertag aus der Bestenlistenzeile gehoert
# zum FAHRER, der Gamertag hier zum TUNER. Sie duerfen nirgends zusammenfallen:
# nicht in der Auswertung, nicht auf der Seite, und schon gar nicht in einer
# Tuner-Wertung, die sonst dem Fahrer Punkte fuer fremde Arbeit gaebe.
# Darum tragen alle Felder hier das Praefix "creator_", und ein Fund ohne
# Erstellernamen wird als unbekannt gemeldet und NIE vom Fahrer ergaenzt.
UGC_IN_ITEM = 552
SHARE_CODE_AT = 184
CREATOR_AT = 248
ASCII_RUN = re.compile(rb"[ -~]{3,}")


def strings_in(raw: bytes, start: int, end: int) -> list[str]:
    """Lesbare Zeichenketten in einem Ausschnitt, ASCII und UTF-16LE."""
    window = raw[max(0, start):max(0, end)]
    out: list[str] = []
    for match in ASCII_RUN.finditer(window):
        out.append(match.group().decode("ascii", "replace"))
    try:
        wide = window.decode("utf-16-le", "ignore")
    except Exception:
        wide = ""
    for match in re.finditer(r"[ -~]{3,}", wide):
        out.append(match.group())
    seen: set[str] = set()
    unique: list[str] = []
    for item in out:
        if item and item not in seen:
            seen.add(item)
            unique.append(item)
    return unique


def enclosing_tuning_item(process: int, tuning_data_address: int) -> dict | None:
    """Zu einem gefundenen TuningData den umgebenden TuningItem lesen, falls da.

    Gibt None zurueck, wenn dort nichts Brauchbares steht -- ein TuningData kann
    auch einzeln im Speicher liegen (etwa als Teil des eigenen Wagens), und dann
    gibt es weder Share-Code noch Ersteller. Das ist ein gueltiger Fall, kein Fehler.
    """
    start = tuning_data_address - UGC_IN_ITEM
    if start <= 0:
        return None
    raw = read_memory(process, start, UGC_IN_ITEM)
    if len(raw) < CREATOR_AT + 120:
        return None

    share_codes = [s for s in strings_in(raw, SHARE_CODE_AT, SHARE_CODE_AT + 32)
                   if 5 <= len(s) <= 12]
    creator_names = [s for s in strings_in(raw, CREATOR_AT, CREATOR_AT + 120)
                     if 3 <= len(s) <= 24]
    # qwXuid liegt je nach UserData-Fassung bei +0 oder +8 im Creator-Block.
    xuids = []
    for delta in (0, 8):
        try:
            value = struct.unpack_from("<Q", raw, CREATOR_AT + delta)[0]
        except struct.error:
            continue
        # Eine Xbox-xuid ist eine grosse, aber nicht absurde Zahl.
        if 1_000_000_000_000_000 <= value <= 9_999_999_999_999_999:
            xuids.append(str(value))

    if not (share_codes or creator_names or xuids):
        return None
    return {
        "container_address": hex(start),
        "share_code_candidates": share_codes,
        "creator_gamertag_candidates": creator_names,
        "creator_xuid_candidates": xuids,
        "warning": ("Ersteller != Fahrer. Diese Namen gehoeren zum TUNER. "
                    "Niemals mit dem Gamertag der Bestenlistenzeile gleichsetzen."),
    }


def readable_regions(process: int) -> Iterable[tuple[int, int]]:
    address = 0
    mbi = MemoryBasicInformation()
    while address < 0x00007FFFFFFFFFFF:
        if not kernel32.VirtualQueryEx(process, ctypes.c_void_p(address),
                                       ctypes.byref(mbi), ctypes.sizeof(mbi)):
            break
        base, size = int(mbi.BaseAddress or 0), int(mbi.RegionSize)
        if base + size <= address:
            break
        if (mbi.State == MEM_COMMIT and mbi.Type == MEM_PRIVATE
                and not (mbi.Protect & PAGE_NOACCESS)
                and not (mbi.Protect & PAGE_GUARD) and size >= 4096):
            yield base, size
        address = base + size


def read_memory(process: int, address: int, length: int) -> bytes:
    buffer = ctypes.create_string_buffer(length)
    actual = ctypes.c_size_t()
    ok = kernel32.ReadProcessMemory(process, ctypes.c_void_p(address), buffer,
                                    length, ctypes.byref(actual))
    if not ok and actual.value == 0:
        return b""
    return buffer.raw[:actual.value]


def records_in_block(data: bytes, base_address: int, layouts: list[str]) -> list[dict]:
    found: list[dict] = []
    for name in layouts:
        layout = LAYOUTS[name]
        if layout["car_id"] is None or layout["class_id"] is None:
            # Ohne den Ganzzahl-Schwanz gibt es keinen billigen Anker; diese
            # Fassungen werden an JEDER 4-Byte-Grenze geprueft, aber nur ueber die
            # Gleitkommapruefung. Das ist langsamer und laeuft darum nur auf
            # ausdrueckliche Anforderung (--layout all).
            step = 4
            for start in range(0, max(0, len(data) - layout["size"]) + 1, step):
                record = decode(data[start:start + layout["size"]], layout,
                                base_address + start, name)
                if record:
                    found.append(record)
            continue
        parts_at = layout["parts"]
        for match in TAIL_ANCHOR.finditer(data):
            start = match.start() - layout["car_id"]
            if start < 0 or start + layout["size"] > len(data):
                continue

            # BILLIGE VORPRUEFUNG, DIREKT AUF data -- ohne den 636-Byte-Satz zu
            # kopieren. Der erste Lauf schaffte 2,88 GiB in 602 s (4,9 MB/s), rund
            # zwanzigmal langsamer als der vergleichbare Suchlauf in
            # find_rival_score_data.py. Der Grund war nicht das Suchen, sondern was
            # HINTER jedem Treffer geschah: ein Slice plus 50 Ganzzahlen plus 34
            # Gleitkommazahlen, millionenfach. Diese zwei Tests werfen fast alles
            # weg, bevor irgendetwas kopiert wird.
            pa = start + parts_at

            # 1. Die ersten sechs Teileplaetze sind kleine Ganzzahlen.
            gate = True
            for step in range(6):
                value = struct.unpack_from("<i", data, pa + step * 4)[0]
                if value < -1 or value > 2000:
                    gate = False
                    break
            if not gate:
                continue

            # 2. Der Schwanz von CInstalledParts (_unknown0..50, 204 Byte) ist in
            #    echten Saetzen so gut wie leer. Als Byte-Zaehlung ist das der
            #    billigste scharfe Test, den es hier gibt -- und er sitzt bewusst
            #    unter der Schwelle "alles null", damit ein einzelnes belegtes Feld
            #    einen echten Satz nicht wegwirft.
            if data.count(0, pa + 200, pa + 404) < 180:
                continue

            record = decode(data[start:start + layout["size"]], layout,
                            base_address + start, name)
            if not record:
                continue
            car_id = record.get("car_id", 0)
            if not 1 <= car_id <= 30000:
                continue
            found.append(record)
    return found


def scan(pid: int, layouts: list[str], max_hits: int,
         max_seconds: float = 240.0, chunk: int = 8 << 20) -> list[dict]:
    """Sucht, bis die Trefferzahl oder die Zeit erreicht ist.

    Die Zeitgrenze ist keine Bequemlichkeit, sondern eine Lehre: der erste Lauf
    gegen das laufende Spiel wurde nach 15 Minuten von aussen abgeschnitten und
    hinterliess KEINE Ausgabe -- nicht einmal die Auskunft, dass er nicht fertig
    wurde. Waehrend eines Rennens ist das Zeitfenster klein; lieber ein ehrliches
    "abgebrochen nach 240 s, so weit gekommen" als gar nichts.
    """
    process = kernel32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ,
                                   False, pid)
    if not process:
        raise SystemExit(f"OpenProcess auf {pid} fehlgeschlagen")
    out: list[dict] = []
    seen: set[str] = set()
    biggest = max(LAYOUTS[name]["size"] for name in layouts)
    deadline = time.monotonic() + max_seconds
    started = time.monotonic()
    truncated = False
    scanned = 0
    read_seconds = 0.0
    match_seconds = 0.0
    try:
        for base, size in readable_regions(process):
            offset = 0
            while offset < size and len(out) < max_hits:
                if time.monotonic() > deadline:
                    truncated = True
                    break
                length = min(chunk, size - offset)
                read_started = time.monotonic()
                data = read_memory(process, base + offset, length + biggest)
                read_seconds += time.monotonic() - read_started
                scanned += len(data)
                if data:
                    match_started = time.monotonic()
                    block_hits = records_in_block(data, base + offset, layouts)
                    match_seconds += time.monotonic() - match_started
                    for entry in block_hits:
                        key = (f"{entry.get('car_id')}|{entry['layout']}|"
                               f"{entry['tune']['FinalDriveRatio']}|"
                               f"{entry['tune']['Front']['Springs']}")
                        if key in seen:
                            continue
                        seen.add(key)
                        # Der Behaelter wird gezielt nachgelesen, nicht aus dem
                        # Block geschnitten: er liegt 552 Byte VOR dem Fund und
                        # damit oft im vorigen Block.
                        container = enclosing_tuning_item(
                            process, int(entry["address"], 16))
                        if container:
                            entry["ugc"] = container
                        out.append(entry)
                        if len(out) >= max_hits:
                            break
                offset += length
            if len(out) >= max_hits or truncated:
                break
    finally:
        kernel32.CloseHandle(process)
    elapsed = time.monotonic() - started
    gb = scanned / (1 << 30)
    rate = gb / elapsed if elapsed > 0 else 0.0
    # IMMER vermerken, wie weit gesucht wurde -- auch bei einem vollstaendigen Lauf.
    # "Nichts gefunden" ist nur dann eine Aussage, wenn danebensteht, wie viel
    # Speicher ueberhaupt angesehen wurde.
    out.append({
        "layout": "_coverage",
        "scanned_gib": round(gb, 2),
        "seconds": round(elapsed, 1),
        "gib_per_second": round(rate, 3),
        "complete": not truncated,
        "read_seconds": round(read_seconds, 1),
        "match_seconds": round(match_seconds, 1),
        "note": ("vollstaendig durchsucht" if not truncated else
                 f"Zeitgrenze {max_seconds:.0f}s erreicht -- NICHT vollstaendig"),
    })
    return out


def render(record: dict) -> str:
    """Einen Fund als Tuning-Zettel ausgeben -- so, wie er auf die Seite koennte."""
    lines = [f"  {record['layout']} @ {record['address']}"]
    if "car_id" in record:
        lines.append(f"    Auto {record['car_id']}"
                     + (f", Marke {record['make_id']}" if "make_id" in record else "")
                     + (f", Klasse {record['class_id']}" if "class_id" in record else ""))
    if "creator_xuid" in record:
        lines.append(f"    Ersteller-xuid {record['creator_xuid']}")
    parts = {k: v for k, v in record["parts"].items()
             if k != "Version" and v not in (0, -1)}
    if parts:
        shown = ", ".join(f"{k}={v}" for k, v in list(parts.items())[:12])
        lines.append(f"    Teile ({len(parts)} gesetzt): {shown}")
    ugc = record.get("ugc")
    if ugc:
        if ugc["share_code_candidates"]:
            lines.append(f"    Share-Code (Kandidaten): "
                         f"{', '.join(ugc['share_code_candidates'][:3])}")
        if ugc["creator_gamertag_candidates"]:
            lines.append(f"    TUNER (nicht der Fahrer!): "
                         f"{', '.join(ugc['creator_gamertag_candidates'][:3])}")
        if ugc["creator_xuid_candidates"]:
            lines.append(f"    Tuner-xuid: {', '.join(ugc['creator_xuid_candidates'])}")
    tune = record["tune"]
    lines.append(f"    Achsantrieb {tune['FinalDriveRatio']}, "
                 f"Bremsbalance {tune['BrakeBias']}, "
                 f"Anpressdruck {tune['FrontDownforce']}/{tune['RearDownforce']}")
    for axle in ("Front", "Rear"):
        a = tune[axle]
        lines.append(f"    {axle}: Druck {a['Pressure']}, Sturz {a['Camber']}, "
                     f"Spur {a['Toe']}, Federn {a['Springs']}, "
                     f"Stabi {a['Swaybar']}, Hoehe {a['RideHeight']}")
    return "\n".join(lines)


def self_test() -> int:
    ok = True

    def check(label: str, condition: bool) -> None:
        nonlocal ok
        print(f"  {'ok  ' if condition else 'FEHL'}  {label}")
        ok = ok and condition

    layout = LAYOUTS["TuningData"]
    raw = bytearray(layout["size"])
    # Teile
    struct.pack_into("<i", raw, layout["parts"] + 4 * 1, 7)     # Engine
    struct.pack_into("<i", raw, layout["parts"] + 4 * 9, 4)     # TireCompound
    struct.pack_into("<i", raw, layout["parts"] + 4 * 41, 3)    # WeightReduction
    # Tune
    tune_at = layout["tune"]
    struct.pack_into("<f", raw, tune_at + 8, 120.0)   # FrontDownforce
    struct.pack_into("<f", raw, tune_at + 12, 180.0)  # RearDownforce
    struct.pack_into("<f", raw, tune_at + 16, 3.75)   # FinalDrive
    struct.pack_into("<f", raw, tune_at + 24, 52.0)   # BrakeBias
    for base in (tune_at + SUSP_FRONT_AT, tune_at + SUSP_REAR_AT):
        struct.pack_into("<f", raw, base + 8, 2.1)    # Pressure
        struct.pack_into("<f", raw, base + 12, -1.5)  # Camber
        struct.pack_into("<f", raw, base + 24, 780.0)  # Springs
        struct.pack_into("<f", raw, base + 32, 12.5)  # RideHeight
    struct.pack_into("<i", raw, layout["car_id"], 3852)
    struct.pack_into("<i", raw, layout["make_id"], 12)
    struct.pack_into("<i", raw, layout["class_id"], 5)

    record = decode(bytes(raw), layout, 0x1000)
    check("ein Satz wird erkannt", record is not None)
    if record:
        check("CarId", record["car_id"] == 3852)
        check("Klasse", record["class_id"] == 5)
        check("Achsantrieb", record["tune"]["FinalDriveRatio"] == 3.75)
        check("Sturz vorn", record["tune"]["Front"]["Camber"] == -1.5)
        check("Federn hinten", record["tune"]["Rear"]["Springs"] == 780.0)
        check("Teil Engine", record["parts"]["Engine"] == 7)
        check("Teil WeightReduction", record["parts"]["WeightReduction"] == 3)
        check("Teilename vollstaendig", len(PART_NAMES) == 50)
        check("Zettel enthaelt die Bremsbalance", "Bremsbalance 52.0" in render(record))

    found = records_in_block(bytes(raw), 0x2000, ["TuningData"])
    check("ueber den Anker auffindbar", len(found) == 1)

    check("leerer Speicher liefert nichts",
          not records_in_block(bytes(layout["size"] * 2), 0, ["TuningData"]))
    noise = bytes(range(256)) * 8
    check("Zufallsbytes liefern nichts",
          not records_in_block(noise, 0, ["TuningData"]))

    bad = bytearray(raw)
    struct.pack_into("<f", bad, tune_at + SUSP_FRONT_AT + 24, -5.0)  # negative Federn
    check("negative Federn werden verworfen",
          decode(bytes(bad), layout, 0) is None)
    bad = bytearray(raw)
    struct.pack_into("<f", bad, tune_at + 16, 999.0)  # absurder Achsantrieb
    check("absurder Achsantrieb wird verworfen",
          decode(bytes(bad), layout, 0) is None)

    # Die beiden Faelle, an denen die erste Fassung des Filters gescheitert ist --
    # gemessen am 2026-08-30 gegen das laufende Spiel, 25 Funde, alle Muell.
    bad = bytearray(raw)
    for base in (tune_at + SUSP_FRONT_AT, tune_at + SUSP_REAR_AT):
        struct.pack_into("<f", bad, base + 24, 1e-42)   # denormale "Federrate"
    check("denormale Federrate wird verworfen",
          decode(bytes(bad), layout, 0) is None)

    bad = bytearray(raw)
    struct.pack_into("<i", bad, layout["parts"] + 4 * 1, 1073741824)  # 0x40000000
    check("Teilewert in Zeigergroesse wird verworfen",
          decode(bytes(bad), layout, 0) is None)

    bad = bytearray(raw)
    for off, _ in TUNE_SCALARS:
        struct.pack_into("<f", bad, tune_at + off, 2.004)  # ueberall derselbe Wert
    check("zwoelf gleiche Werte sind ein Muster, kein Tune",
          decode(bytes(bad), layout, 0) is None)

    check("gueltiger Satz ueberlebt die schaerferen Pruefungen",
          decode(bytes(raw), layout, 0) is not None)

    # Der Fehltreffer vom 2026-08-30, 20:56: zwoelf VERSCHIEDENE Werte, aber alle
    # im Band 7,8..14,4 -- ein Ausschnitt aus einem Feld gleichartiger Zahlen.
    bad = bytearray(raw)
    band = [10.04, 7.85, 12.69, 9.93, 14.39, 14.23, 9.39, 12.79, 11.16, 13.39,
            9.76, 13.35]
    for (off, _), value in zip(TUNE_SCALARS, band):
        struct.pack_into("<f", bad, tune_at + off, value)
    check("gleichfoermiges Zahlenband wird verworfen",
          decode(bytes(bad), layout, 0) is None)

    # Und die Gegenprobe: ein Tune MIT Nullen darf die Spannweitenpruefung passieren
    # (viele echte Tunes lassen Felder auf null stehen).
    okay = bytearray(raw)
    for off, _ in TUNE_SCALARS:
        struct.pack_into("<f", okay, tune_at + off, 0.0)
    struct.pack_into("<f", okay, tune_at + 16, 3.75)    # Achsantrieb
    struct.pack_into("<f", okay, tune_at + 24, 52.0)    # Bremsbalance
    struct.pack_into("<f", okay, tune_at + 8, 120.0)    # Anpressdruck vorn
    struct.pack_into("<f", okay, tune_at + 12, 180.0)   # Anpressdruck hinten
    struct.pack_into("<f", okay, tune_at + 32, 60.0)    # Mittendifferential
    check("Tune mit Nullfeldern bleibt gueltig",
          decode(bytes(okay), layout, 0) is not None)

    print("\nalles bestanden" if ok else "\nFEHLGESCHLAGEN")
    return 0 if ok else 1


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--process", default="forzahorizon6")
    parser.add_argument("--pid", type=int, default=0)
    parser.add_argument("--layout", default="TuningData",
                        help="TuningData (schnell, Voreinstellung), oder 'all' "
                             "fuer zusaetzlich TuneData und VersionedTuneData")
    parser.add_argument("--max-hits", type=int, default=40)
    parser.add_argument("--max-seconds", type=float, default=240.0,
                        help="Zeitgrenze; danach wird mit Vermerk abgebrochen")
    parser.add_argument("--out", type=Path,
                        default=Path("data/runtime/tune_data.json"))
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()

    if args.self_test:
        return self_test()
    if sys.platform != "win32":
        raise SystemExit("liest Prozessspeicher -- nur unter Windows")

    layouts = list(LAYOUTS) if args.layout == "all" else [args.layout]
    for name in layouts:
        if name not in LAYOUTS:
            raise SystemExit(f"unbekannte Fassung '{name}'")

    pid = args.pid
    if not pid:
        import subprocess
        result = subprocess.run(
            ["powershell.exe", "-NoProfile", "-Command",
             f"(Get-Process -Name '{args.process}' -ErrorAction SilentlyContinue).Id"],
            capture_output=True, text=True)
        digits = "".join(c for c in result.stdout if c.isdigit())
        pid = int(digits) if digits else 0
    if not pid:
        raise SystemExit(f"{args.process} laeuft nicht")

    records = scan(pid, layouts, args.max_hits, args.max_seconds)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(records, indent=1), encoding="utf-8")
    coverage = [r for r in records if r.get("layout") == "_coverage"]
    real = [r for r in records if r.get("layout") != "_coverage"]
    print(f"{len(real)} Tune-Datensatz/-saetze -> {args.out}")
    if coverage:
        c = coverage[0]
        print(f"  Abdeckung: {c['scanned_gib']} GiB in {c['seconds']}s "
              f"({c['gib_per_second']} GiB/s) -- {c['note']}")
        print(f"  davon Lesen {c['read_seconds']}s, Suchen {c['match_seconds']}s")
    for record in real[:10]:
        print(render(record))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
