"""Vollstaendige Streckennamen einer Kategorie holen -- auch die zu langen.

    python scripts/harvest_route_names.py "Cross-Country" 19
    python scripts/harvest_route_names.py "Dirt Racing" 21 --shots 4

Schreibt `config/route_names_<kategorie>.json` und gibt die Liste aus. Das Spiel muss
in der Streckenliste DIESER Kategorie stehen (scripts/select_rivals_category.py).

## Warum es das braucht

Der Titel im Kartenbild ist auf die Kartenbreite beschnitten. Bei Road Racing und
Street Racing passt jeder Name; bei Cross-Country nicht:

    Streckenschirm:  "City Docks Cross-Country Ci"
    Klassenschirm:   "ty Docks Cross-Country Circ"

Zwei Aufnahmen, zwei verschiedene Ausschnitte DESSELBEN Namens -- das Spiel LAESST
den Titel durchlaufen. Damit ist der ganze Name zu bekommen: mehrmals hinsehen und
die Stuecke ueber ihre Ueberlappung zusammensetzen.

Ohne das waere die Alternative, die abgeschnittenen Namen in den Katalog zu
schreiben. Die stehen dann fuer immer so in der Auswertung, und `repair_track_names`
haette keine Wahrheit mehr, gegen die es reparieren koennte.

## Grenzen, ehrlich benannt

Zusammensetzen ist ein Schluss, keine Messung. Darum steht in der Ausgabe je Strecke,
aus wie vielen Stuecken der Name entstand und ob sie sich sauber ueberlappt haben;
`"stitched": false` heisst "ein einziger Ausschnitt, moeglicherweise beschnitten".
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
WORKSPACE = HERE.parent
sys.path.insert(0, str(HERE))

SCREEN_DIR = WORKSPACE / "data" / "runtime" / "current_vm_screen"
TITLE_BAND = (0.06, 0.505, 0.75, 0.607)


def capture_title(engine) -> str:
    """Ein Standbild holen und den Titel daraus lesen."""
    frame_path = SCREEN_DIR / "current.png"
    try:
        frame_path.unlink(missing_ok=True)
    except OSError:
        pass
    subprocess.run(
        ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
         str(HERE / "capture_vm_current_screen.ps1")],
        capture_output=True, text=True, timeout=300)
    if not frame_path.exists():
        return ""
    from screen_reader import read_image
    frame = read_image(frame_path)
    height, width = frame.shape[:2]
    x0, y0, x1, y1 = TITLE_BAND
    crop = frame[int(height * y0):int(height * y1), int(width * x0):int(width * x1)]
    result = engine(crop)
    parts = getattr(result, "txts", None) or []
    if not parts and isinstance(result, (list, tuple)) and result and result[0]:
        parts = [str(e[1]) for e in result[0]
                 if isinstance(e, (list, tuple)) and len(e) >= 2]
    parts = [p.strip() for p in parts if p and p.strip()]
    parts = [p for p in parts
             if not re.search(r"route\s*length|^\d+[.,]?\d*\s*KM$", p, re.I)]
    return re.sub(r"\s+", " ", " ".join(parts)).strip()


def stitch(pieces: list[str]) -> tuple[str, bool]:
    """Ausschnitte zu einem Namen zusammensetzen. Gibt (Name, wurde_geklebt).

    Gesucht wird die laengste Ueberlappung zwischen dem Ende des einen und dem
    Anfang des anderen Stuecks -- in beiden Richtungen, weil unklar ist, welcher
    Ausschnitt weiter links stand.

    Mindestens sechs Zeichen Ueberlappung: kuerzere treffen zufaellig zu ("Cross"
    steckt in fast jedem Namen dieser Kategorie), und ein falsch geklebter Name ist
    schlimmer als ein sichtbar abgeschnittener.
    """
    clean = [p for p in dict.fromkeys(pieces) if p]
    if not clean:
        return "", False
    best = max(clean, key=len)
    stitched = False
    for piece in clean:
        if piece == best or piece in best:
            continue
        merged = merge_two(best, piece)
        if merged and len(merged) > len(best):
            best, stitched = merged, True
    return best.strip(), stitched


def merge_two(left: str, right: str, minimum: int = 6) -> str | None:
    for a, b in ((left, right), (right, left)):
        limit = min(len(a), len(b))
        for size in range(limit, minimum - 1, -1):
            if a[-size:] == b[:size]:
                return a + b[size:]
    return None


def press(key: str, count: int = 1) -> None:
    subprocess.run(
        ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
         str(HERE / "drive_vm_forza.ps1"), "-Key", key, "-Count", str(count),
         "-KeyDelayMs", "600", "-SettleMs", "900"],
        capture_output=True, text=True, timeout=300)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("category")
    parser.add_argument("count", type=int, help="Anzahl Strecken der Kategorie")
    parser.add_argument("--shots", type=int, default=3,
                        help="Aufnahmen je Strecke (mehr = mehr Ausschnitte)")
    parser.add_argument("--gap", type=float, default=1.4,
                        help="Sekunden zwischen zwei Aufnahmen")
    args = parser.parse_args(argv)

    from extract_leaderboard import create_rapidocr_engine
    engine = create_rapidocr_engine(use_gpu=False)

    routes = []
    for index in range(args.count):
        pieces = []
        for shot in range(args.shots):
            if shot:
                time.sleep(args.gap)
            text = capture_title(engine)
            if text:
                pieces.append(text)
        name, was_stitched = stitch(pieces)
        routes.append({"route_index": index, "name": name,
                       "pieces": pieces, "stitched": was_stitched})
        mark = "geklebt" if was_stitched else ("1 Stueck" if pieces else "NICHTS")
        print(f"  {index:>2}  {name:<34} [{mark}]")
        if len(pieces) > 1 and not was_stitched:
            # Mehrere Ausschnitte, aber keine Ueberlappung gefunden: entweder lief
            # der Titel nicht (dann ist er vollstaendig) oder die OCR hat ihn
            # verschieden verlesen. Beides gehoert sichtbar in die Ausgabe.
            print(f"      Stuecke: {pieces}")
        press("RIGHT")

    out = WORKSPACE / "config" / (
        "route_names_" + re.sub(r"[^a-z0-9]+", "_", args.category.lower()) + ".json")
    out.write_text(json.dumps({"rivals_mode": args.category,
                               "route_count": len(routes),
                               "routes": routes}, indent=2, ensure_ascii=False),
                   encoding="utf-8")
    print(f"\n{len(routes)} Strecke(n) -> {out}")
    thin = [r["route_index"] for r in routes if not r["name"]]
    if thin:
        print(f"WARNUNG ohne Namen: {thin}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
