"""Die Kurznamen der Autos (Ergebnisschirm, Startaufstellung) auf car_ids abbilden.

    python scripts/build_car_short_names.py [--tables <StringTables-Ordner>] [--report]

## Wozu

Die Tabellen um ein Rennen nennen die Autos der anderen Fahrer nur KURZ: "Mit. Evo. TME",
"Corvette '53", "Luftauto 911". Fuer die erwartete Platzierung und die Horizon-Play-Zeiten
muss daraus ein Auto werden -- eine car_id.

## Woher

`Data_Car.str` in den Stringtabellen des Spiels enthaelt je Auto den Modellnamen
("Lancer Evolution X GSR") und den Kurznamen ("Lancer GSR '08"). Die Schluessel sind
Pruefsummen; die beiden Eintraege eines Autos unterscheiden sich um eine feste XOR-Konstante
(gemessen 2026-10-01: 0x68b03746 bei 587 Autos, 0x8cd1606e bei 83 -- vermutlich zwei Laengen
der Kennung, wie bei einer CRC zu erwarten). So gehoeren Modell und Kurzname zusammen, ohne
die Datenbank des Spiels (gamedbRC.slt ist verschluesselt).

Vom Modellnamen zum Auto: der volle Name eines Autos ("Mitsubishi Lancer Evolution X GSR")
ENDET auf den Modellnamen, und der Jahrgang im Kurznamen ('08) trennt Baujahre. Quellen: die
Autoliste (forza.net und Wiki, mit car_ids) und die Autonamen des Bestenlisten-Datensatzes.
Wo das nicht genau EIN Auto ergibt, steht der Kurzname ohne id da, mit den Kandidaten --
nie geraten.

Gleicher Schluessel, gleicher Text in jeder Sprache: die Kurznamen aller 24 Sprachen kommen
dazu, falls ein Spiel in einer anderen Sprache laeuft.
"""

from __future__ import annotations

import argparse
import collections
import json
import re
import sys
import unicodedata
import zipfile
from datetime import datetime, timezone
from pathlib import Path

WURZEL = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(WURZEL / "scripts"))
import extract_game_text as spieltext  # noqa: E402

ZIEL = WURZEL / "config" / "fh6_car_short_names.json"
AUTOLISTE = WURZEL / "data" / "cars" / "fh6_car_availability.json"
DATENSATZ = WURZEL / "data" / "analytics" / "laps.json"

# Kuerzel der Hersteller in den Kurznamen.
KUERZEL = {"mit": "mitsubishi", "m b": "mercedes benz", "m amg": "mercedes amg", "chevy": "chevrolet",
           "vw": "volkswagen", "wp": "welcome pack"}


def norm(s: str) -> str:
    s = unicodedata.normalize("NFKD", s).encode("ascii", "ignore").decode().lower()
    return re.sub(r"[^a-z0-9#]+", " ", s).strip()


def jahrgang(text: str) -> int | None:
    """Der Jahrgang im Kurznamen: "'08", oder ohne Hochkomma am Ende ("Nissan Silvia 89")."""
    m = re.search(r"'(\d\d)\b", text) or re.search(r"\s(\d\d)$", text.strip())
    return int(m.group(1)) if m else None


# Die beiden gemessenen Konstanten. Eine dritte, automatisch gesuchte paarte am 2026-10-01
# Kurznamen untereinander ("VW Scirocco '11" mit "Chevy Nova '69") -- darum nur diese.
KONSTANTEN = (0x68B03746, 0x8CD1606E)


def paare(eintraege: dict[int, str]) -> list[tuple[int, int]]:
    """Schluesselpaare (a, b) eines Autos ueber die festen XOR-Konstanten."""
    raus: list[tuple[int, int]] = []
    frei = set(eintraege)
    for konstante in KONSTANTEN:
        for a in sorted(frei):
            if a in frei and (a ^ konstante) in frei:
                raus.append((a, a ^ konstante))
                frei.discard(a)
                frei.discard(a ^ konstante)
    return raus


def kandidaten_laden() -> list[dict]:
    """Autos mit car_id: Name ohne Jahr, Jahr, id."""
    autos: dict[int, dict] = {}
    try:
        ohne = 0
        for a in json.loads(AUTOLISTE.read_text(encoding="utf-8"))["cars"]:
            ids = [i for i in (a.get("ids") or ([a["id"]] if a.get("id") else [])) if isinstance(i, int) and i > 0]
            for i in ids:
                autos[i] = {"id": i, "name": a["name"], "year": a.get("year")}
            # NEUE AUTOS stehen in der Liste, bevor eine Bestenliste sie benennt (der Porsche
            # 'Luftauto 002', 2026-10-01): mit Namen, noch ohne id -- der Name hilft schon.
            if not ids:
                ohne -= 1
                autos[ohne] = {"id": None, "name": a["name"], "year": a.get("year")}
    except (OSError, ValueError, KeyError):
        pass
    try:
        d = json.loads(DATENSATZ.read_text(encoding="utf-8-sig"))
        for i, name in zip(d.get("carIds") or [], d.get("carNames") or []):
            if not isinstance(i, int) or i <= 0 or i in autos or re.match(r"^car\s*#?\s*\d+$", str(name), re.I):
                continue
            m = re.match(r"^(.*?)\s*'(\d\d)(?:\s*\(#\d+\))?$", str(name))
            if m:
                jj = int(m.group(2))
                autos[i] = {"id": i, "name": m.group(1), "year": (1900 if jj > 30 else 2000) + jj}
    except (OSError, ValueError):
        pass
    return list(autos.values())


def zuordnen(modell: str, kurz: str, autos: list[dict]) -> list[dict]:
    m = norm(modell)
    if not m or m == "null car":
        return []
    kand = [a for a in autos if norm(a["name"]).endswith(" " + m) or norm(a["name"]) == m]
    if not kand:
        # Ohne passendes Ende: die Woerter. "911 Carrera Coupe 'Luftauto 002'" heisst in der
        # Autoliste "Porsche Carrera Coupe 'Luftauto 002'" -- ohne die 911. Ein Name, dessen
        # Woerter (ohne Hersteller) alle im Modell stehen oder umgekehrt, und nur einer.
        wm = set(m.split())
        def woerter(a: dict) -> set[str]:
            teile = norm(a["name"]).split()
            return set(teile[1:]) if len(teile) > 1 else set(teile)
        kand = [a for a in autos if len(woerter(a)) >= 2 and (woerter(a) <= wm or wm <= woerter(a))]
    j = jahrgang(kurz)
    if j is not None and kand:
        mit = [a for a in kand if a.get("year") and a["year"] % 100 == j]
        kand = mit or kand
    # Hersteller aus dem Kurznamen, wenn er dort steht ("Mit. Evo. TME" -> mitsubishi).
    if len(kand) > 1:
        k = norm(kurz)
        for kurzform, lang in KUERZEL.items():
            k = re.sub(rf"\b{kurzform}\b", lang, k)
        erstes = k.split(" ")[0] if k else ""
        mit = [a for a in kand if norm(a["name"]).startswith(erstes)]
        kand = mit or kand
    # DASSELBE AUTO UNTER ZWEI IDS (die Autoliste fuehrt manche doppelt, etwa die Corvette '53
    # unter 1564 und 2177): gleicher Name, gleiches Jahr ist EIN Auto mit mehreren ids.
    gruppen: dict[tuple[str, int | None], list[dict]] = {}
    for a in kand:
        gruppen.setdefault((norm(a["name"]), a.get("year")), []).append(a)
    return [dict(g[0], ids=sorted({x["id"] for x in g if x["id"]})) for g in gruppen.values()]


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--tables", type=Path)
    ap.add_argument("--report", action="store_true")
    args = ap.parse_args()
    ordner = args.tables or spieltext.finde_tabellen()
    if ordner is None:
        print("Stringtabellen des Spiels nicht gefunden (--tables).")
        return 1
    en = spieltext.tabelle(ordner / "EN.zip", "Data_Car.str")
    autos = kandidaten_laden()
    eintraege: dict[str, dict] = {}
    offen = []
    for a, b in paare(en):
        ta, tb = en[a], en[b]
        gefunden = None
        # WELCHE SEITE das Modell ist, sagt der Schluessel: Modellschluessel enden (untere 7 Bit)
        # auf 0x38, Kurznamen nie. Beide Richtungen zu probieren las "Nissan 370Z" als Modell
        # und "370Z" als Kurznamen -- beides passte auf ein Auto.
        if (a & 0x7F) == 0x38 and (b & 0x7F) != 0x38:
            richtungen = ((ta, tb, b),)
        elif (b & 0x7F) == 0x38 and (a & 0x7F) != 0x38:
            richtungen = ((tb, ta, a),)
        else:
            richtungen = ((ta, tb, b), (tb, ta, a))
        for (modell, kurz, kurzschluessel) in richtungen:
            kand = zuordnen(modell, kurz, autos)
            if len(kand) == 1:
                gefunden = (modell, kurz, kurzschluessel, kand[0])
                break
        if gefunden is None:
            modell, kurz, schl = richtungen[0]
            kand = zuordnen(modell, kurz, autos)
            offen.append({"model": modell, "short": kurz, "key": schl,
                          "candidates": [{"ids": x["ids"], "name": x["name"], "year": x.get("year")} for x in kand][:6]})
            continue
        modell, kurz, schl, auto = gefunden
        eintraege[kurz] = {"id": min(auto["ids"]) if auto["ids"] else None, "ids": auto["ids"],
                           "name": auto["name"], "year": auto.get("year"),
                           "model": modell, "key": schl}

    # Dieselben Schluessel in den anderen Sprachen.
    schl_zu_id = {e["key"]: e["id"] for e in eintraege.values() if e["id"]}
    sprachen = {}
    for z in sorted(ordner.glob("*.zip")):
        if z.stem == "EN":
            continue
        try:
            t = spieltext.tabelle(z, "Data_Car.str")
        except (KeyError, zipfile.BadZipFile, OSError):
            continue
        for schl, car in schl_zu_id.items():
            text = t.get(schl)
            if text and text not in eintraege:
                sprachen.setdefault(text, car)

    # Ein Kurzname, der zwei Autos meint, gilt fuer keins.
    doppelt = collections.Counter(e for e in eintraege)
    raus = {
        "source": "Data_Car.str of the game's string tables, model and short name paired by key; "
                  "model matched to the car list (forza.net, wiki) and the leaderboard dataset",
        "built": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "cars": {k: {"id": v["id"], "ids": v["ids"], "name": v["name"], "year": v["year"], "model": v["model"]}
                 for k, v in sorted(eintraege.items()) if doppelt[k] == 1},
        "otherLanguages": dict(sorted(sprachen.items())),
        "unresolved": offen,
    }
    ZIEL.write_text(json.dumps(raus, ensure_ascii=False, indent=1), encoding="utf-8")
    mit_id = sum(1 for v in raus["cars"].values() if v["id"])
    print(f"{mit_id} Kurznamen mit car_id, {len(raus['cars']) - mit_id} nur mit Namen, {len(sprachen)} aus anderen Sprachen, "
          f"{len(offen)} offen -> {ZIEL.relative_to(WURZEL)}")
    if args.report:
        for o in offen:
            print(f"  offen: {o['short']!r} ({o['model']!r}) -> "
                  + (", ".join(f"{c['ids']} {c['name']} {c['year']}" for c in o["candidates"]) or "kein Auto"))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
