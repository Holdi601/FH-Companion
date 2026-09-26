"""Eine Rivals-Kategorie auswaehlen -- und dabei den gelben Rahmen wirklich ansehen.

    python scripts/select_rivals_category.py "Cross-Country"
    python scripts/select_rivals_category.py "Dirt Racing" --dry-run
    python scripts/select_rivals_category.py --where

## Warum es dieses Skript gibt

Der Kategorieschirm ist ein 2x3-Gitter:

    Road Racing 23    Cross-Country 19   Street Racing 15
    Dirt Racing 21    Drag Racing 3      Touge 5

Drei Anlaeufe sind daran gescheitert, alle drei aus demselben Grund -- die
Markierung wurde nicht GESEHEN, sondern angenommen:

1. `Invoke-Seek -Key RIGHT -Pattern <Name>` brach beim ersten Blick ab, weil jeder
   Name schon auf dem Schirm steht. Es wurde nie eine Taste gedrueckt.
2. `Select-ByHover` auf das Label: geoeffnet wurde Road Racing statt Street Racing.
3. Blinde Gitter-Navigation (zweimal LINKS, einmal OBEN, dann Offsets): sechs Runden
   in Folge landete der Navigator wieder in Street Racing. Die Tasten kamen zu
   schnell nach dem ESC, waehrend der Schirm noch aufbaute.

Der Unterschied hier: nach JEDEM Tastendruck wird ein Standbild geholt und der
gelbe Rahmen im Bild gesucht. Gedrueckt wird nur, solange er woanders sitzt, und
ENTER kommt erst, wenn er auf der Zielkachel steht. Kein Timing, keine Annahme.

## Wie der Rahmen gefunden wird

Gemessen, nicht geschaetzt: RGB (202, 255, 2), drei Pixel dick. Gesucht wird nicht
nach "gelb", sondern nach GRUEN GROESSER ROT -- jedes gelbe Auto und jede gelbe
Schrift auf diesem Schirm hat es umgekehrt. Aus allen Rahmenpixeln wird der
Schwerpunkt gebildet und der naechsten Kachelmitte zugeordnet; das ist robust
dagegen, dass der Rahmen duenn ist und meine Kachelrechtecke ein paar Pixel neben
der Wahrheit liegen koennten. Die Rechtecke stehen relativ, damit eine andere
Aufloesung nicht danebengreift.
"""

from __future__ import annotations

import argparse
import subprocess
import sys
import time
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
WORKSPACE = HERE.parent
SCREEN_DIR = WORKSPACE / "data" / "runtime" / "current_vm_screen"

# Zeile, Spalte im Gitter -- abgelesen am Standbild vom 2026-08-28.
GRID = {
    "Road Racing":   (0, 0),
    "Cross-Country": (0, 1),
    "Street Racing": (0, 2),
    "Dirt Racing":   (1, 0),
    "Drag Racing":   (1, 1),
    "Touge":         (1, 2),
}

# Kachelflaechen als Anteile (x0, y0, x1, y1), gemessen am 1920x1080-Standbild:
# Reihe 0 y 265..555, Reihe 1 y 560..850; Spalten x 95..665, 675..1240, 1250..1815.
TILES = {
    (0, 0): (0.049, 0.245, 0.347, 0.514),
    (0, 1): (0.352, 0.245, 0.646, 0.514),
    (0, 2): (0.651, 0.245, 0.945, 0.514),
    (1, 0): (0.049, 0.519, 0.347, 0.787),
    (1, 1): (0.352, 0.519, 0.646, 0.787),
    (1, 2): (0.651, 0.519, 0.945, 0.787),
}

# Die Rahmenfarbe, am Standbild gemessen und nicht geschaetzt: RGB (202, 255, 2),
# drei Pixel dick. Entscheidend ist das Verhaeltnis -- beim Rahmen ist GRUEN groesser
# als Rot. Jedes gelbe Auto und jede gelbe Schrift auf diesem Schirm hat es umgekehrt
# (Rot >= Gruen), und daran ist die erste Fassung gescheitert: sie zaehlte das gelbe
# Rallyeauto in der Dirt-Racing-Kachel und meldete Dirt statt Road Racing.
FRAME_MIN_GREEN = 235
FRAME_RED_RANGE = (150, 230)
FRAME_MAX_BLUE = 40
FRAME_GREEN_OVER_RED = 20
# Der Rahmen um EINE Kachel bringt rund 5.000 Pixel mit; alles darunter ist Rauschen
# oder ein Schirm ohne Markierung.
FRAME_MIN_PIXELS = 800


def capture() -> np.ndarray | None:
    """Ein frisches Standbild holen. Gibt None, wenn die Aufnahme scheitert."""
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
        return None
    sys.path.insert(0, str(HERE))
    from screen_reader import read_image
    frame = read_image(frame_path)
    # read_image liefert BGR (OpenCV); hier wird in RGB gerechnet.
    return frame[:, :, ::-1] if frame is not None and frame.ndim == 3 else frame


def on_category_screen() -> tuple[bool, str]:
    """Ist ueberhaupt der Kategorieschirm offen? Sagt auch, was sonst zu sehen war.

    WARUM DAS NOETIG IST: Am 2026-08-28 stand das Spiel auf dem KLASSENSCHIRM
    ("Nachi Run", Kacheln A/S1/S2/R) -- und der hat ebenfalls einen gelben Rahmen,
    an derselben Stelle im Bild. Der Rahmenfinder hielt ihn fuer eine
    Kategorie-Kachel und drueckte acht Mal ins Leere. Ein Rahmen allein beweist
    also nichts; erst der Schirm gibt ihm Bedeutung.

    Geprueft wird am Text, nicht am Bild: der Kategorieschirm nennt "Routes
    Available" und mehrere Kategorienamen gleichzeitig, und das tut kein anderer.
    """
    text_file = SCREEN_DIR / "current.ocr.txt"
    try:
        text = text_file.read_text(encoding="utf-8-sig")
    except OSError:
        return False, "(keine OCR-Datei)"
    flat = " ".join(text.split())
    hits = sum(1 for name in GRID if name.lower() in flat.lower())
    if "routes available" in flat.lower() and hits >= 3:
        return True, flat[:70]
    return False, flat[:70]


def frame_mask(rgb: np.ndarray) -> np.ndarray:
    pixels = rgb.astype(np.int16)
    red, green, blue = pixels[:, :, 0], pixels[:, :, 1], pixels[:, :, 2]
    low, high = FRAME_RED_RANGE
    return ((green >= FRAME_MIN_GREEN) & (red >= low) & (red <= high)
            & (blue <= FRAME_MAX_BLUE) & (green > red + FRAME_GREEN_OVER_RED))


def highlighted(rgb: np.ndarray) -> tuple[tuple[int, int] | None, dict]:
    """Welche Kachel hat den Rahmen? Ueber den SCHWERPUNKT, nicht ueber Randbaender.

    Der Rahmen umschliesst genau eine Kachel, also liegt sein Schwerpunkt in deren
    Mitte. Das ist unempfindlich dagegen, dass er nur drei Pixel dick ist und dass
    meine Kachelrechtecke ein paar Pixel neben der Wahrheit liegen koennten.
    """
    mask = frame_mask(rgb)
    total = int(mask.sum())
    height, width = mask.shape
    info = {"pixels": total}
    if total < FRAME_MIN_PIXELS:
        return None, info
    ys, xs = np.nonzero(mask)
    cx, cy = xs.mean() / width, ys.mean() / height
    info["centroid"] = (round(cx, 3), round(cy, 3))
    best, best_distance = None, 9.9
    for cell, (x0, y0, x1, y1) in TILES.items():
        mx, my = (x0 + x1) / 2, (y0 + y1) / 2
        distance = ((cx - mx) ** 2 + (cy - my) ** 2) ** 0.5
        if distance < best_distance:
            best, best_distance = cell, distance
    # Ein Schwerpunkt weit ausserhalb jeder Kachelmitte ist kein Treffer, sondern
    # ein anderer Schirm mit gruenen Flaechen.
    return (best if best_distance < 0.12 else None), info


def press(key: str, count: int = 1) -> None:
    subprocess.run(
        ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
         str(HERE / "drive_vm_forza.ps1"), "-Key", key, "-Count", str(count),
         "-KeyDelayMs", "700", "-SettleMs", "1500"],
        capture_output=True, text=True, timeout=300)


def name_of(cell: tuple[int, int] | None) -> str:
    for label, value in GRID.items():
        if value == cell:
            return label
    return "unbekannt"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("category", nargs="?", help="Zielkategorie")
    parser.add_argument("--where", action="store_true",
                        help="nur sagen, wo der Rahmen steht")
    parser.add_argument("--dry-run", action="store_true",
                        help="bewegen, aber kein ENTER")
    parser.add_argument("--max-steps", type=int, default=8)
    args = parser.parse_args(argv)

    rgb = capture()
    if rgb is None:
        print("ABBRUCH: kein Standbild bekommen")
        return 1
    ok, seen = on_category_screen()
    if not ok:
        print(f"ABBRUCH: das ist nicht der Kategorieschirm. Zu sehen: {seen}")
        print("  Mit ESC dorthin zurueck und nochmal.")
        return 1
    cell, info = highlighted(rgb)
    print(f"Rahmenpixel: {info.get('pixels')}, Schwerpunkt {info.get('centroid')}")
    if args.where or not args.category:
        print(f"Rahmen steht auf: {name_of(cell)}")
        return 0 if cell else 1

    target = GRID.get(args.category)
    if target is None:
        print(f"ABBRUCH: '{args.category}' ist keine der {len(GRID)} Kategorien: "
              + ", ".join(GRID))
        return 2

    for step in range(1, args.max_steps + 1):
        if cell is None:
            print("ABBRUCH: auf diesem Schirm ist kein Kategorie-Rahmen zu sehen "
                  "-- ist der Kategorieschirm ueberhaupt offen?")
            return 1
        if cell == target:
            print(f"Rahmen sitzt auf '{args.category}'")
            if args.dry_run:
                print("--dry-run: kein ENTER")
                return 0
            press("ENTER")
            time.sleep(3)
            print("ENTER gesendet")
            return 0
        # Immer nur EINEN Schritt, dann wieder hinsehen. Zwei Tasten am Stueck sind
        # genau die Annahme, an der die blinde Fassung gescheitert ist.
        row, col = cell
        trow, tcol = target
        if col != tcol:
            press("RIGHT" if tcol > col else "LEFT")
        else:
            press("DOWN" if trow > row else "UP")
        rgb = capture()
        if rgb is None:
            print("ABBRUCH: kein Standbild nach dem Tastendruck")
            return 1
        ok, seen = on_category_screen()
        if not ok:
            # Ein Tastendruck hat den Schirm verlassen -- weiterzudruecken hiesse,
            # blind in einem fremden Menue zu wuehlen.
            print(f"ABBRUCH: der Schirm hat gewechselt. Zu sehen: {seen}")
            return 1
        cell, info = highlighted(rgb)
        print(f"  Schritt {step}: Rahmen jetzt auf {name_of(cell)}")

    print(f"ABBRUCH: nach {args.max_steps} Schritten steht der Rahmen auf "
          f"{name_of(cell)}, nicht auf '{args.category}'")
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
