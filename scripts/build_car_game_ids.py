"""Die car_id jedes Autos aus den Dateien des Spiels.

    python scripts/build_car_game_ids.py [--game <Spielordner>] [--list <Autoliste.json>] [--check]

Schreibt `config/fh6_car_game_ids.json`. Der Server (server/car_availability.py) nimmt
die car_id eines Autos zuerst von hier, erst danach aus der Rundenzeit-Zuordnung.

## Woher

Je Auto liegt ein Archiv im Spiel: `media/Cars/<CODE>.zip`, darin `carclips_<ID>.clipd`.
ID ist die car_id des Spiels -- dieselbe Nummer, die die Telemetrie und die Garage nennen
(gemessen 2026-10-04: alle 608 car_ids, die die App kannte, stehen dort; AST_DB11_17 traegt
2527 = Aston Martin DB11 '17). CODE ist der Codename der Entwickler: Marke (FER, POR, CHE ...),
manchmal Startnummer, Modell, Jahr (zweistellig, nicht immer das Modelljahr der Liste).

## Warum

Die Rundenzeit-Zuordnung hatte verwandte Autos vertauscht: "Ferrari F40 '87" trug 1023 --
das ist FER_F40Competizione_89; die F40 ist 340. Ebenso 599XX/599XX Evolution und FXX K/
FXX K Evo. Und 39 Autos hatten gar keine id, darum zeigte der Reiter "Car collection" sie als
"Not sure", sobald die Garage Autos ohne Namen enthielt (32 auf dem PC des Nutzers).

## Wie zugeordnet wird

Jedes Auto der Liste gegen jeden Codenamen: Anteil der Modellteile des Codes, die im Namen
stehen, Marke (Kuerzel aus sicheren Paaren gelernt), Abstand der Jahre. Eins zu eins, beste
Paare zuerst. SICHER ist ein Paar nur, wenn das Modell ganz passt, die Jahre hoechstens eins
auseinander liegen und der naechstbeste Code deutlich schlechter ist. Alles andere entscheidet
`config/fh6_car_ids_reviewed.json` (von Hand geprueft, mit Begruendung); ohne Eintrag dort
bleibt das Auto hier ohne id, und der Server faellt auf die alte Zuordnung zurueck.
"""
from __future__ import annotations

import argparse
import functools
import json
import os
import re
import sys
import unicodedata
import zipfile
from datetime import datetime, timezone
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
ZIEL = WORKSPACE / "config" / "fh6_car_game_ids.json"
GEPRUEFT = WORKSPACE / "config" / "fh6_car_ids_reviewed.json"
LISTE = WORKSPACE / "data" / "cars" / "fh6_car_availability.json"
FORMAT = "fhc-car-game-ids-1"
CLIP = re.compile(r"carclips_(\d+)\.clipd$", re.IGNORECASE)


def spielordner() -> Path | None:
    """Der Ordner des Spiels: die Steam-Bibliotheken nach ForzaHorizon6 absuchen."""
    kandidaten = []
    for steam in (Path(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")) / "Steam",
                  Path(r"C:\Program Files\Steam")):
        vdf = steam / "steamapps" / "libraryfolders.vdf"
        if vdf.exists():
            for pfad in re.findall(r'"path"\s+"([^"]+)"', vdf.read_text(encoding="utf-8", errors="replace")):
                kandidaten.append(Path(pfad.replace("\\\\", "\\")) / "steamapps" / "common" / "ForzaHorizon6")
    for k in kandidaten:
        if (k / "media" / "Cars").is_dir():
            return k
    return None


def codes_lesen(spiel: Path) -> dict[int, str]:
    """car_id -> Codename, aus jedem Archiv in media/Cars."""
    raus: dict[int, str] = {}
    for z in sorted((spiel / "media" / "Cars").glob("*.zip")):
        try:
            namen = zipfile.ZipFile(z).namelist()
        except (OSError, zipfile.BadZipFile):
            continue
        ids = {int(m.group(1)) for n in namen if (m := CLIP.search(n))}
        if len(ids) != 1:
            continue                       # kein oder mehrdeutiges Auto: nicht raten
        cid = ids.pop()
        if cid in raus:
            raise SystemExit(f"car_id {cid} in zwei Archiven: {raus[cid]} und {z.stem}")
        raus[cid] = z.stem
    return raus


def norm(text: str) -> str:
    """Wie server/car_availability._norm, aber ohne Leerzeichen."""
    text = unicodedata.normalize("NFKD", text or "")
    text = "".join(c for c in text if not unicodedata.combining(c)).lower()
    return re.sub(r"[^a-z0-9]", "", text)


def schluessel(name: str, jahr: int) -> str:
    """Der Schluessel eines Autos in den JSON-Dateien: Name wie in der Liste, dann das Jahr."""
    return f"{name}|{jahr}"


@functools.lru_cache(maxsize=None)
def _teile(code: str) -> tuple[str, int | None, list[str]]:
    p = code.split("_")
    jahr = int(p[-1]) if p[-1].isdigit() else None
    mitte = list(p[1:-1])
    # Die Fassung haengt am Modell ("XJSFE", "M4WP"): fuer den Vergleich mit dem Namen weg damit --
    # ob die Fassung stimmt, entscheidet _spielart.
    if mitte and (art := _spielart(code)) in ("FE", "WP", "PO", "TG", "ID") and len(mitte[-1]) > len(art):
        mitte[-1] = mitte[-1][: -len(art)]
    stuecke: list[str] = []
    for t in mitte:
        stuecke += [norm(x) for x in re.findall(r"[A-Z]?[a-z]+|[A-Z]+(?![a-z])|\d+", t) if norm(x)]
    return p[0].lower(), jahr, stuecke


def _jahre(y2: int, jahr: int | None) -> int:
    if jahr is None:
        return 9
    d = abs(y2 - jahr)
    return min(d, 100 - d)


def _deckung(auto: dict, code: str) -> tuple[float, int, str]:
    marke, jahr, stuecke = _teile(code)
    name = norm(auto["name"])
    # ZAHLEN NUR GANZ: "4" aus BMW_Z4_19 stand in "G40" und machte den Z4 zum Ginetta G40 Junior.
    # Eine Zahl des Codes zaehlt nur, wenn sie als ganze Zahl im Namen steht; Startnummern, die
    # dort nicht stehen, stoeren nicht.
    zahlen = set(re.findall(r"\d+", auto["name"]))
    stuecke = [s for s in stuecke if not (s.isdigit() and s not in zahlen)] or stuecke
    # Ein einzelner Buchstabe ("BMW_i8_15" -> "i") steht in fast jedem Namen: er zaehlt nur, wenn das
    # ganze Modell im Namen steht. Sonst lernte die Alumicraft das Kuerzel "bmw" und ihr Class 10 den i8.
    if any(len(s) == 1 and s.isalpha() for s in stuecke) and "".join(stuecke) not in name:
        stuecke = [s for s in stuecke if not (len(s) == 1 and s.isalpha())] or ["#"]
    gedeckt = sum(len(s) for s in stuecke if (s in zahlen if s.isdigit() else s in name))
    return gedeckt / max(1, sum(len(s) for s in stuecke)), _jahre(auto["year"] % 100, jahr), marke


def _spielart(code: str) -> str:
    """FE = Forza Edition, WP = Welcome Pack, PO = Vorbestellung, TG/ID/Traffic = andere Fassungen."""
    teile = code.split("_")
    modell = teile[-2] if len(teile) > 2 else ""
    for art in ("FE", "WP", "PO", "TG", "ID"):
        if modell.endswith(art):
            return art
    return "Traffic" if "traffic" in code.lower() else ""


def _spielart_name(name: str) -> str:
    """Dieselbe Fassung am Namen der Liste. 2026-10-04: "Jaguar XJ-S Forza Edition '90" lag als sicher auf
    JAG_XJS_90 -- das Modell passte ganz, nur ist das die XJ-S ohne Forza Edition (die ist JAG_XJSFE_90)."""
    if "Forza Edition" in name:
        return "FE"
    if "Welcome Pack" in name:
        return "WP"
    if "Preorder" in name:
        return "PO"
    return ""


def zuordnen(autos: list[dict], codes: dict[int, str]) -> tuple[dict[str, int], list[dict]]:
    """Sichere Paare (Schluessel -> car_id) und die Zweifelsfaelle mit ihren besten Codes."""
    # Markenkuerzel aus der alten Zuordnung lernen (sie stimmt fast ueberall; eine Vertauschung
    # innerhalb einer Marke aendert das Kuerzel nicht). Vorher aus Paaren, die "ganz passten" --
    # und ein Buchstabe oder eine Ziffer passt fast immer: Alumicraft lernte "bmw".
    stimmen: dict[str, dict[str, int]] = {}
    for a in autos:
        for cid in a.get("ids") or []:
            if cid in codes:
                marke = _teile(codes[cid])[0]
                stimmen.setdefault(norm(a["make"]), {}).setdefault(marke, 0)
                stimmen[norm(a["make"])][marke] += 1
    kuerzel = {m: max(v, key=v.get) for m, v in stimmen.items()}

    def wert(a: dict, cid: int) -> float:
        deckung, jahre, marke = _deckung(a, codes[cid])
        marke_ok = (kuerzel.get(norm(a["make"])) == marke or norm(a["make"])[:3] == marke
                    or any(norm(w)[:3] == marke for w in a["name"].split()))
        # Die alte Zuordnung als Beleg, nicht als Urteil: sie stimmt meistens, hat aber Vertauschungen.
        alt = 0.5 if cid in (a.get("ids") or []) else 0.0
        fassung = 0.0 if _spielart(codes[cid]) == _spielart_name(a["name"]) else 1.0
        return deckung * 2 + (0.6 if marke_ok else 0) - min(jahre, 6) * 0.25 + alt - fassung

    paare = sorted(((wert(a, cid), i, cid) for i, a in enumerate(autos) for cid in codes), reverse=True)
    zu_auto: dict[int, tuple[int, float]] = {}
    vergeben: set[int] = set()
    for w, i, cid in paare:
        if w <= 0.6 or i in zu_auto or cid in vergeben:
            continue
        zu_auto[i] = (cid, w)
        vergeben.add(cid)

    sicher: dict[str, int] = {}
    zweifel: list[dict] = []
    for i, a in enumerate(autos):
        k = schluessel(a["name"], a["year"])
        beste = sorted(((wert(a, cid), cid) for cid in codes), reverse=True)[:5]
        if i in zu_auto:
            cid, w = zu_auto[i]
            deckung, jahre, _ = _deckung(a, codes[cid])
            zweite = next((x for x, c in beste if c != cid), 0.0)
            alte = a.get("ids") or []
            if (deckung >= 0.99 and jahre <= 1 and w - zweite >= 0.5 and (not alte or cid in alte)
                    and _spielart(codes[cid]) == _spielart_name(a["name"])):
                sicher[k] = cid
                continue
        zweifel.append({"car": k, "candidates": [[c, codes[c], round(x, 2)] for x, c in beste]})
    return sicher, zweifel


def bauen(spiel: Path, liste: Path, geprueft: Path = GEPRUEFT) -> dict:
    codes = codes_lesen(spiel)
    autos = [a for a in json.loads(liste.read_text(encoding="utf-8"))["cars"] if a.get("name") and a.get("year")]
    sicher, zweifel = zuordnen(autos, codes)
    von_hand = json.loads(geprueft.read_text(encoding="utf-8")).get("cars", {}) if geprueft.exists() else {}
    autos_je_schluessel = {schluessel(a["name"], a["year"]) for a in autos}

    zuordnung: dict[str, int] = {}
    herkunft: dict[str, str] = {}
    for k, cid in sicher.items():
        zuordnung[k], herkunft[k] = cid, "auto"
    # Von Hand Geprueftes gilt vor dem Automatischen -- auch, um eine sichere Zuordnung zu korrigieren.
    for k, e in von_hand.items():
        if k not in autos_je_schluessel:
            continue
        if e.get("id") is None:
            zuordnung.pop(k, None)
            herkunft[k] = "reviewed: not in the game files"
            continue
        zuordnung[k], herkunft[k] = int(e["id"]), "reviewed"
    # Eins zu eins: zeigen zwei Autos auf dieselbe id, ist eins falsch -- dann bekommt keins sie.
    je_id: dict[int, list[str]] = {}
    for k, cid in zuordnung.items():
        je_id.setdefault(cid, []).append(k)
    doppelt = {cid: ks for cid, ks in je_id.items() if len(ks) > 1}
    for cid, ks in doppelt.items():
        for k in ks:
            zuordnung.pop(k)
            herkunft[k] = f"conflict on {cid}"
    unbekannt = [cid for cid in zuordnung.values() if cid not in codes]
    if unbekannt:
        raise SystemExit(f"ids, die das Spiel nicht kennt: {unbekannt}")
    offen = [z for z in zweifel if z["car"] not in zuordnung and z["car"] not in von_hand]

    exe = spiel / "forzahorizon6.exe"
    return {
        "format": FORMAT,
        "_": "car_id of every car in the official list, from the game's own files: media/Cars/<CODE>.zip "
             "holds carclips_<ID>.clipd. Built by scripts/build_car_game_ids.py.",
        "game_build": datetime.fromtimestamp(exe.stat().st_mtime, timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
                      if exe.exists() else None,
        "codes": {str(cid): codes[cid] for cid in sorted(codes)},
        "cars": {k: zuordnung[k] for k in sorted(zuordnung)},
        "how": {k: herkunft[k] for k in sorted(herkunft) if herkunft[k] != "auto"},
        "open": offen,
        "conflicts": {str(cid): ks for cid, ks in doppelt.items()},
    }


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--game", type=Path, help="Ordner des Spiels (sonst aus den Steam-Bibliotheken)")
    ap.add_argument("--list", type=Path, default=LISTE, help="Autoliste (data/cars/fh6_car_availability.json)")
    ap.add_argument("--check", action="store_true", help="nur zusammenfassen, nichts schreiben")
    args = ap.parse_args(argv)
    spiel = args.game or spielordner()
    if spiel is None or not (spiel / "media" / "Cars").is_dir():
        print("Spielordner nicht gefunden (--game <Ordner>)", file=sys.stderr)
        return 2
    daten = bauen(spiel, args.list)
    print(f"Spiel: {len(daten['codes'])} Autos mit car_id; Liste: {len(daten['cars'])} zugeordnet, "
          f"{sum(1 for h in daten['how'].values() if h == 'reviewed')} davon von Hand geprueft, "
          f"{len(daten['open'])} offen, {len(daten['conflicts'])} Konflikte")
    for z in daten["open"]:
        print("  offen:", z["car"], " ".join(f"{c[1]}({c[0]})" for c in z["candidates"][:3]))
    for cid, ks in daten["conflicts"].items():
        print("  Konflikt:", cid, ks)
    if not args.check:
        ZIEL.write_text(json.dumps(daten, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
        print(ZIEL)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
