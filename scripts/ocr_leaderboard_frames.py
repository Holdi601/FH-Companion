"""Read a leaderboard out of captured frames, in parallel, and check it against itself.

The screen route. Memory reading is exact, but its sampling could not keep up with the
scroll; the screen shows 11 rows at once, so one frame every ~90 ms already overlaps at
the measured ~116 rows/s, and frames are processed afterwards on as many cores as the
machine has -- with the game shut down.

Two things decide whether this is fast enough, and neither is the pixel count:

* **One tesseract call per FRAME, not per cell.** The first version cropped 11 rows x 3
  fields and called the engine 33 times a frame; each call is ~25 ms of process start
  whatever the image size. `--psm 6` over the whole strip lets tesseract do the line
  segmentation, and rank, car, class, time and gear are all on the same line anyway.
* **A file list, so one process handles many frames.** `tesseract list.txt stdout`
  reads every path in the list and separates the pages with a form feed. Together
  these turn ~58,000 process starts into a dozen.

ABS/TCS/STM are never OCR'd: a filled marker is dark in the middle of its ring and a
hollow one is not, which is a threshold on a 18x18 crop and far more reliable than
asking a text engine about a glyph that is not text.

What the table cannot give: xuid, `is_clean`, the submitted timestamp and the assists
beyond those three. So this complements the memory path rather than replacing it.

Self-validation, because OCR invents digits silently:
  * the same rank read in several frames must agree on its lap time, or it is dropped
  * a rank whose time contradicts its neighbours breaks the board's own ordering and
    is dropped

    python scripts/ocr_leaderboard_frames.py --frames data/runtime/frames/run1 \
        --out data/runtime/frames/run1/rows.jsonl
"""

from __future__ import annotations

import argparse
import json
import os
import re
import statistics
import subprocess
import sys
import tempfile
from collections import defaultdict
from concurrent.futures import ProcessPoolExecutor
from pathlib import Path

import cv2
import numpy

def finde_tesseract() -> str:
    """Wo tesseract.exe wirklich liegt.

    FRUEHER EIN FESTER PFAD. "C:\\Program Files\\Tesseract-OCR\\tesseract.exe"
    stimmt auf dem Hauptrechner und sonst oft nicht: die Installation fragt nach
    dem Ordner, die winget-Fassung legt woanders hin, und eine Nutzer-Installation
    landet unter %LOCALAPPDATA%.

    Am 2026-09-19 im Beitrags-Lauf gemessen: der Fehlschlag kam NACH dem
    Herunterladen von 300 MB Bibliotheken und einer Aufnahme von 540 MB -- also
    nach rund einer halben Stunde Arbeit des Beitragenden, fuer eine Datei, die in
    der ersten Sekunde haette gesucht werden koennen.

    Reihenfolge: PATH, dann Registry, dann die ueblichen Orte. Leerer Rueckgabewert
    heisst: nicht da.
    """
    import os
    import shutil

    gefunden = shutil.which("tesseract")
    if gefunden:
        return gefunden

    try:
        import winreg
        for hive in (winreg.HKEY_LOCAL_MACHINE, winreg.HKEY_CURRENT_USER):
            try:
                with winreg.OpenKey(hive, r"SOFTWARE\Tesseract-OCR") as schluessel:
                    pfad, _ = winreg.QueryValueEx(schluessel, "Path")
                    kandidat = os.path.join(pfad, "tesseract.exe")
                    if os.path.exists(kandidat):
                        return kandidat
            except OSError:
                continue
    except ImportError:
        pass

    for basis in (os.environ.get("ProgramFiles", r"C:\Program Files"),
                  os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)"),
                  os.environ.get("LOCALAPPDATA", "")):
        if not basis:
            continue
        kandidat = os.path.join(basis, "Tesseract-OCR", "tesseract.exe")
        if os.path.exists(kandidat):
            return kandidat
    return ""


TESSERACT = finde_tesseract() or r"C:\Program Files\Tesseract-OCR\tesseract.exe"

# Geometry in SCREEN coordinates; the capture's own x offset is subtracted at runtime,
# so moving the capture window does not silently shift every column.
ROW_TOP = 12
ROW_HEIGHT = 54.2
ROW_COUNT = 11

# Only these bands are shown to tesseract. The operator's template makes the point
# well: the driver column carries emoji and platform icons that produce stray glyphs
# and shift the line, and the class badge is known from the board we navigated to. Less
# area is not just faster, it is more accurate.
KEEP_BANDS = [
    # The rank column is RIGHT-ALIGNED, so a wider number extends LEFT: '2,325' starts
    # at x~96 but '21,038' starts at x~80. A band beginning at 100 therefore clipped the
    # leading digit of every rank past 9,999 -- 21,038 read as 1,038, an offset of
    # exactly -20,000 that no in-frame check can see, because every row in the frame is
    # clipped identically and the consecutive-rank test then CONFIRMS the wrong
    # numbering. Measured 2026-08-24: screen-route density was 85-100% below rank 10,000
    # and 1-4% above it, while the memory route read 100% at every depth. The clipped
    # rows were not lost, they were renumbered into 1,000-9,999 and discarded there as
    # duplicates. 78 is the capture window's own left edge, so this costs nothing.
    # Ranks past 99,999 need 7 characters (x~64) and so also need the capture moved
    # left of 78 -- five boards are that deep.
    (78, 196),       # rank, right-aligned; room for six characters

    (592, 862),      # car
    (1042, 1178),    # drivetrain
    (1232, 1762),    # lap time, the dirty-lap marker, ABS/TCS/STM, gearbox
]
CIRCLES = {"abs": 1414, "tcs": 1512, "stm": 1611}
# The dirty-lap marker: a yellow-orange "!" right after the time. Measured at screen
# x 1370-1399 -- 110 hits against a baseline of 20 from the lime row highlight, which is
# why only the middle third of the row height is sampled: the highlight is a border.
DIRTY_BAND = (1366, 1402)

# Parsed token by token, not with one big pattern. The column separators come back as
# ")", "]" or "|", and a "|" right after the rank is read as a "1" -- so "981|" becomes
# "9811". One regex anchored on whitespace matched barely a tenth of the lines; these
# find each field where it actually is, and the frame's own numbering catches a rank
# that a stray separator corrupted.
RANK_RE = re.compile(r"^\W*(\d[\d,]{0,8})")
TIME_RE = re.compile(r"(\d{1,2})\s*[:.]\s*(\d{2})\s*[.,]\s*(\d{3})")
DRIVE_RE = re.compile(r"\b(AWD|RWD|FWD)\b")
GEAR_RE = re.compile(r"\b(MC|M|A)\s*$")
CLASS_RE = re.compile(r"\b(D|C|B|A|S1|S2|R|X)\s*(\d{3})\b")


def parse_line(line: str) -> dict | None:
    """Rank, lap time, car, drivetrain and gearbox out of one OCR line."""
    time_match = TIME_RE.search(line)
    if not time_match:
        return None
    minutes, seconds, thousandths = (int(group) for group in time_match.groups())
    if seconds > 59:
        return None
    lap = minutes * 60 + seconds + thousandths / 1000.0
    if not 1.0 <= lap <= 3600.0:
        return None

    rank = 0
    rank_match = RANK_RE.match(line)
    if rank_match:
        digits = rank_match.group(1).replace(",", "")
        if digits.isdigit() and 1 <= int(digits) <= 2_000_000:
            rank = int(digits)

    drive_match = DRIVE_RE.search(line)
    car = ""
    # Der Autoname steht zwischen Rang und Antriebsspalte. Bis 2026-08-24 wurde er NUR
    # gelesen, wenn auch der Antrieb erkannt wurde -- scheiterte dessen OCR, flog ein
    # perfekt lesbarer Name mit. Messbar: 85.565 Zeilen mit Antrieb, exakt 85.565 mit
    # Autoname, bei 213.492 Zeilen insgesamt. Zwei Drittel der Namen gingen so verloren,
    # und der Name ist die einzige Quelle, aus der die Speicher-car_ids ihre Bezeichnung
    # bekommen (join_ocr_names_to_car_ids).
    # Fehlt der Antrieb, begrenzt die Rundenzeit den Namen nach rechts -- die Spalten
    # stehen in fester Reihenfolge: Rang, Auto, Antrieb, Zeit.
    right = drive_match.start() if drive_match else time_match.start()
    if right > 0:
        head = line[:right]
        if rank_match:
            head = head[rank_match.end():]
        # Separators read as stray punctuation and single letters; strip them off both
        # ends rather than trying to whitelist the car names themselves.
        car = re.sub(r"^[^A-Za-z0-9]+|[^A-Za-z0-9')]+$", "", head).strip()
        car = re.sub(r"\s{2,}", " ", car)
        # The column separators read as stray single letters ("a McLaren 620R a"), so
        # drop one-character tokens at either end -- no car name starts or ends in one.
        parts = car.split()
        while parts and len(parts[0]) == 1 and not parts[0].isdigit():
            parts.pop(0)
        while parts and len(parts[-1]) == 1 and not parts[-1].isdigit():
            parts.pop()
        car = " ".join(parts)

    parsed = {"rank": rank, "lap_time_seconds": round(lap, 3)}
    if car:
        parsed["car_name"] = car[:48]
    if drive_match:
        parsed["drivetrain"] = drive_match.group(1)
    gear_match = GEAR_RE.search(line.rstrip())
    if gear_match:
        parsed["gearbox"] = gear_match.group(1)
    class_match = CLASS_RE.search(line)
    if class_match:
        parsed["performance_class"] = class_match.group(1)
        parsed["performance_index"] = int(class_match.group(2))
    return parsed


def keep_only(band, offset_x: int) -> numpy.ndarray:
    """Horizontal slices of one row, glued together -- everything else is discarded."""
    pieces = []
    gap = None
    for left, right in KEEP_BANDS:
        x0 = max(0, left - offset_x)
        x1 = min(band.shape[1], right - offset_x)
        if x1 - x0 < 4:
            continue
        piece = band[:, x0:x1]
        if gap is None:
            gap = numpy.zeros((piece.shape[0], 24, piece.shape[2]), dtype=piece.dtype)
        pieces.append(piece)
        pieces.append(gap)
    if not pieces:
        return band
    return numpy.hstack(pieces[:-1])


def prepare(band) -> numpy.ndarray:
    """Binarise ONE row. Per row, not per strip: the table alternates light and dark
    row backgrounds, and a single global threshold over the whole strip leaves half the
    rows unreadable -- the first run returned exactly one usable line per frame."""
    grey = cv2.cvtColor(band, cv2.COLOR_BGR2GRAY)
    grey = cv2.resize(grey, None, fx=1.6, fy=1.6, interpolation=cv2.INTER_CUBIC)
    _, binary = cv2.threshold(grey, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
    if binary.mean() < 127:
        binary = cv2.bitwise_not(binary)
    return binary


def stack(bands: list[numpy.ndarray]) -> numpy.ndarray:
    """Glue binarised rows into one tall image so a frame is still one OCR call."""
    width = max(band.shape[1] for band in bands)
    gap = numpy.full((10, width), 255, dtype=numpy.uint8)
    pieces = []
    for band in bands:
        if band.shape[1] < width:
            band = cv2.copyMakeBorder(band, 0, 0, 0, width - band.shape[1],
                                      cv2.BORDER_CONSTANT, value=255)
        pieces.append(band)
        pieces.append(gap)
    return numpy.vstack(pieces)


def parse_time(text: str) -> float | None:
    cleaned = re.sub(r"\s+", "", text).replace(",", ".")
    cleaned = cleaned.replace("O", "0").replace("o", "0").replace("l", "1")
    if cleaned.count(".") == 2:                    # 01.33.187 -> 01:33.187
        head, mid, tail = cleaned.split(".")
        cleaned = f"{head}:{mid}.{tail}"
    match = TIME_RE.match(cleaned)
    if not match:
        return None
    minutes = int(match.group(1) or 0)
    seconds = int(match.group(2))
    if seconds > 59:
        return None
    return minutes * 60 + seconds + int(match.group(3)) / 1000.0


def circle_filled(row_image, centre_x: int) -> bool | None:
    half = 9
    top = max(0, row_image.shape[0] // 2 - half)
    patch = row_image[top:top + 2 * half, centre_x - half:centre_x + half]
    if patch.size == 0:
        return None
    grey = cv2.cvtColor(patch, cv2.COLOR_BGR2GRAY)
    inner = grey[half // 2:half + half // 2, half // 2:half + half // 2]
    if inner.size == 0:
        return None
    return bool(inner.mean() < grey.mean() - 8)


def dirty_marker(row_image, offset_x: int) -> bool | None:
    """True when the yellow "!" is present, i.e. the lap did not count.

    The one field the screen was thought not to carry. It is not OCR'd -- asking a text
    engine about a coloured glyph is exactly the mistake the assist circles avoid -- but
    counted: yellow-orange pixels in a fixed box, sampled away from the row border so
    the lime highlight on the selected row cannot pose as a marker.
    """
    x0 = max(0, DIRTY_BAND[0] - offset_x)
    x1 = min(row_image.shape[1], DIRTY_BAND[1] - offset_x)
    if x1 - x0 < 6:
        return None
    height = row_image.shape[0]
    patch = row_image[height // 3:2 * height // 3, x0:x1]
    if patch.size == 0:
        return None
    blue = patch[:, :, 0].astype(int)
    green = patch[:, :, 1].astype(int)
    red = patch[:, :, 2].astype(int)
    yellow = ((red > 150) & (green > 120) & (blue < 110)).sum()
    return bool(yellow >= 12)


def ocr_batch(paths: list[str]) -> list[str]:
    """One tesseract process for the whole batch; pages come back split by form feed."""
    with tempfile.TemporaryDirectory() as work:
        listing = Path(work) / "list.txt"
        listing.write_text("\n".join(paths), encoding="utf-8")
        result = subprocess.run(
            [TESSERACT, str(listing), "stdout", "--psm", "6",
             "-c", "load_system_dawg=0", "-c", "load_freq_dawg=0"],
            capture_output=True, text=True, encoding="utf-8", errors="replace")
    if result.returncode != 0 and not result.stdout:
        return []
    pages = result.stdout.split("\f")
    return pages


def read_chunk(frames: list[str], offset_x: int = 78) -> list[dict]:
    """Preprocess a chunk of frames, OCR them in one call, read the circles per row."""
    prepared: list[str] = []
    strips: dict[str, list] = {}
    work = tempfile.mkdtemp(prefix="fh6ocr_")
    try:
        for path in frames:
            image = cv2.imread(path)
            if image is None:
                continue
            bands = []
            for index in range(ROW_COUNT):
                top = int(ROW_TOP + index * ROW_HEIGHT)
                bottom = int(top + ROW_HEIGHT) - 4
                if bottom > image.shape[0]:
                    break
                bands.append(image[top:bottom, :])
            if not bands:
                continue
            strips[path] = bands
            target = Path(work) / (Path(path).stem + ".png")
            cv2.imwrite(str(target),
                        stack([prepare(keep_only(band, offset_x)) for band in bands]))
            prepared.append(str(target))

        pages = ocr_batch(prepared)
        rows: list[dict] = []
        for path, page in zip(prepared, pages):
            source = Path(path).stem
            original = next((key for key in strips if Path(key).stem == source), None)
            if original is None:
                continue
            bands = strips[original]
            lines = [line for line in page.splitlines() if line.strip()]
            # First pass: read what each line gives, rank optional.
            seen: list[dict] = []
            for position, line in enumerate(lines):
                parsed = parse_line(line)
                if parsed is None:
                    continue
                parsed["position"] = position
                parsed["line"] = line
                seen.append(parsed)

            # Rows in a frame are consecutive ranks, so the anchors that agree on the
            # same base decide the rest. A clipped leading digit cannot invent a rank
            # this way; it can only fail to contribute one.
            bases: dict[int, int] = defaultdict(int)
            for item in seen:
                if item["rank"]:
                    bases[item["rank"] - item["position"]] += 1
            base = None
            if bases:
                candidate, votes = max(bases.items(), key=lambda pair: pair[1])
                # Three agreeing anchors, not one. A single misread rank used to define
                # a whole frame's numbering, which put ~500 rows at ranks thousands
                # away from where the list actually was -- the only source of "missing"
                # ranks in an otherwise gapless run.
                if votes >= 3:
                    base = candidate
            if base is None:
                continue

            for item in seen:
                position = item["position"]
                rank = base + position
                # The position-derived rank wins. The base is a majority vote over the
                # frame's readable ranks and the row positions are deterministic, while
                # a line's own rank is what a stray separator corrupts ("981|" -> 9811).
                # Dropping on disagreement threw away 60% of the rows it should have
                # kept; the line's rank now only votes for the base.
                disagrees = bool(item["rank"]) and item["rank"] != rank
                if not 1 <= rank <= 2_000_000:
                    continue
                entry = {key: value for key, value in item.items()
                         if key not in ("position", "line", "rank")}
                entry["rank"] = rank
                entry["source_frame"] = Path(original).name
                # Die Zeilennummer IM Bild. Ohne sie ist der Beleg spaeter nicht mehr
                # auffindbar: der Rahmen wird nach festem Raster geschnitten
                # (ROW_TOP + index * ROW_HEIGHT), aber welcher Rang in welcher Zeile
                # stand, weiss danach niemand mehr.
                entry["row_index"] = position
                entry["rank_anchored"] = bool(item["rank"]) and not disagrees
                entry["line"] = item["line"].strip()[:160]
                if position < len(bands):
                    band = bands[position]
                    for name, centre in CIRCLES.items():
                        entry[f"used_{name}"] = circle_filled(band, centre - offset_x)
                    dirty = dirty_marker(band, offset_x)
                    if dirty is not None:
                        entry["is_clean"] = not dirty
                rows.append(entry)
        return rows
    finally:
        for leftover in Path(work).glob("*"):
            try:
                leftover.unlink()
            except OSError:
                pass
        try:
            Path(work).rmdir()
        except OSError:
            pass


def merge(all_rows: list[dict]) -> tuple[list[dict], dict]:
    by_rank: dict[int, list[dict]] = defaultdict(list)
    for row in all_rows:
        by_rank[row["rank"]].append(row)

    kept, disputed = [], 0
    for rank in sorted(by_rank):
        readings = by_rank[rank]
        times = [row["lap_time_seconds"] for row in readings]
        counts = {value: times.count(value) for value in set(times)}
        best = max(counts.items(), key=lambda item: item[1])
        # A rank seen once is taken as read; seen several times, the majority wins and
        # a tie is dropped rather than guessed.
        if len(times) > 1 and best[1] == 1 and len(counts) > 1:
            disputed += 1
            continue
        winner = dict(next(row for row in readings if row["lap_time_seconds"] == best[0]))
        winner["readings"] = len(readings)
        winner["agreement"] = best[1]
        kept.append(winner)

    ordered = sorted(kept, key=lambda row: row["rank"])
    clean, ordering_drops = [], 0
    for index, row in enumerate(ordered):
        window = [other["lap_time_seconds"]
                  for other in ordered[max(0, index - 6):index + 7] if other is not row]
        if window:
            middle = statistics.median(window)
            if abs(row["lap_time_seconds"] - middle) > max(3.0, 0.15 * middle):
                ordering_drops += 1
                continue
        clean.append(row)

    return clean, {
        "raw_readings": len(all_rows),
        "distinct_ranks": len(by_rank),
        "disputed_dropped": disputed,
        "ordering_dropped": ordering_drops,
        "kept": len(clean),
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--frames", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--workers", type=int, default=max(1, (os.cpu_count() or 4) - 2))
    parser.add_argument("--batch", type=int, default=40,
                        help="frames per tesseract invocation")
    parser.add_argument("--limit", type=int, default=0)
    parser.add_argument("--offset-x", type=int, default=78,
                        help="screen x the capture started at, so the column map lines up")
    args = parser.parse_args(argv)

    if not Path(TESSERACT).exists():
        print(f"tesseract not found at {TESSERACT}")
        return 2
    frames = sorted(str(path) for path in args.frames.glob("*.png"))
    if args.limit:
        frames = frames[:args.limit]
    if not frames:
        print(f"no frames in {args.frames}")
        return 2
    chunks = [frames[i:i + args.batch] for i in range(0, len(frames), args.batch)]
    print(f"{len(frames)} frames, {len(chunks)} tesseract call(s), {args.workers} workers",
          flush=True)

    rows: list[dict] = []
    done = 0
    with ProcessPoolExecutor(max_workers=args.workers) as pool:
        from functools import partial
        worker = partial(read_chunk, offset_x=args.offset_x)
        for result in pool.map(worker, chunks):
            rows.extend(result)
            done += 1
            print(f"  chunk {done}/{len(chunks)}: {len(rows)} readings", flush=True)

    merged, report = merge(rows)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    with args.out.open("w", encoding="utf-8") as handle:
        for row in merged:
            handle.write(json.dumps(row, separators=(",", ":")) + "\n")
    if merged:
        report["min_rank"] = merged[0]["rank"]
        report["max_rank"] = merged[-1]["rank"]
        span = merged[-1]["rank"] - merged[0]["rank"] + 1
        report["missing_in_span"] = span - len(merged)
        report["coverage_pct"] = round(100.0 * len(merged) / span, 1)
    print(json.dumps(report, indent=2))
    (args.out.parent / "ocr_report.json").write_text(json.dumps(report, indent=2),
                                                     encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
