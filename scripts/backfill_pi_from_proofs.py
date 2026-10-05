"""Den PI je Runde aus den Belegbildern nachtragen -- ohne neuen Scan.

    python scripts/backfill_pi_from_proofs.py                  Probelauf, alle Boards
    python scripts/backfill_pi_from_proofs.py --boards NAME    Probelauf, nur diese
    python scripts/backfill_pi_from_proofs.py --apply          schreiben

## Warum

Bis 2026-09-26 wurde der PI der Runde (die Zahl in der Spalte "Class") nie gelesen;
was in `performance_index` steht, sind Fehltreffer aus Autonamen. Seitdem liest
`ocr_leaderboard_frames` den Kasten in einem eigenen Durchgang. Fuer die schon
abgelegten Boards sind die Bilder laengst geloescht -- bis auf die Belege: seit
2026-09-10 sichert `save_proofs` je Rang den Zeilenausschnitt (`proof.zip`,
`r########.webp`, der Name IST der Rang), ausgeduennt auf die zehn schnellsten Runden
je Auto. Das sind genau die Runden, die die Seite zeigt.

Der Ausschnitt ist die volle Zeile, 1700 x 50, im selben Raster wie eine Zeile im
Bild. `pi_crop` aus ocr_leaderboard_frames passt darum unveraendert.

## Was geprueft wird, bevor etwas geschrieben wird

* **Das Klassenband.** Ein PI ausserhalb von (Grenze darunter, eigene Grenze] ist
  eine Fehllesung und wird nicht eingetragen.
* **Die Rundenzeit.** Der Ausschnitt gehoert zum Rang, weil er beim Scan unter
  diesem Rang abgelegt wurde -- und das ist eine Annahme, keine Messung. Also wird
  auch die Rundenzeit im Ausschnitt gelesen (dieselbe Zeilen-OCR wie beim Scan) und
  mit der abgelegten verglichen. Nur bei Gleichheit wird der PI eingetragen. Ist die
  Zeit im Ausschnitt nicht lesbar, bleibt der Rang leer: ungeprueft ist nicht
  eingetragen.
* **Nichts wird ueberschrieben.** Traegt eine Zeile schon einen PI, bleibt er stehen;
  weicht der gelesene ab, wird das gezaehlt und gemeldet.

Ohne `--apply` wird NUR berichtet. Mit `--apply` werden rows.jsonl und
leaderboard_entries.parquet ersetzt (atomar, ueber eine Zwischendatei); beim ersten
Schreiben bleibt je eine `.bak` daneben liegen.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import sys
import tempfile
import zipfile
from collections import Counter
from concurrent.futures import ProcessPoolExecutor
from pathlib import Path

import cv2
import numpy

sys.path.insert(0, str(Path(__file__).resolve().parent))

import ocr_leaderboard_frames as ocr  # noqa: E402

ROOT = Path("data/memory_scans/full_sweep")
PROOF_NAME = "proof.zip"
PROOF_RE = re.compile(r"^r(\d{8})\.webp$")
# Die Belege sind in Bildkoordinaten geschnitten, die Aufnahme begann bei x = 78.
OFFSET_X = 78
# So viele Belege je Arbeitspaket. Der PI selbst braucht kein tesseract, die Pruefung
# der Rundenzeit schon -- ein Aufruf je Paket. Mehr spart kaum noch Startzeit, und ein
# kleines Paket verliert bei einem Absturz weniger.
BATCH = 400


def read_crops(batch: list[tuple[int, bytes]]) -> list[tuple[int, int | None, float | None]]:
    """(Rang, PI, Rundenzeit im Ausschnitt) je Beleg; None, was nicht lesbar war."""
    work = tempfile.mkdtemp(prefix="fh6pi_")
    try:
        pis: dict[int, int | None] = {}
        line_paths, line_ranks = [], []
        for rank, data in batch:
            band = cv2.imdecode(numpy.frombuffer(data, numpy.uint8), cv2.IMREAD_COLOR)
            if band is None:
                continue
            # Derselbe Musterabgleich wie beim Scan. Die Belege sind WebP mit
            # Qualitaet 70; gemessen an 3.183 Belegen aus sieben Boards las er jeden.
            pis[rank] = ocr.read_pi(ocr.pi_crop(band, OFFSET_X))
            # Die Zeile genau so, wie der Scan sie gelesen hat, EIN Ausschnitt je Seite:
            # so ordnet der Seitenvorschub jede Zeit ihrem Rang zu.
            line = ocr.prepare(ocr.keep_only(band, OFFSET_X))
            target = Path(work) / f"ln{len(line_paths):05d}.png"
            cv2.imwrite(str(target), line)
            line_paths.append(str(target))
            line_ranks.append(rank)

        times: dict[int, float | None] = {}
        pages = ocr.ocr_batch(line_paths) if line_paths else []
        if len(pages) >= len(line_paths):
            for rank, page in zip(line_ranks, pages):
                text = " ".join(part for part in page.splitlines() if part.strip())
                parsed = ocr.parse_line(text) if text else None
                times[rank] = parsed["lap_time_seconds"] if parsed else None
        return [(rank, pis.get(rank), times.get(rank)) for rank, _ in batch]
    finally:
        shutil.rmtree(work, ignore_errors=True)


def load_rows(path: Path) -> list[dict]:
    rows = []
    for text in path.read_text(encoding="utf-8-sig").splitlines():
        if text.strip():
            rows.append(json.loads(text))
    return rows


def replace_atomically(target: Path, write) -> None:
    """Erst daneben schreiben, dann ersetzen -- ein Abbruch laesst die alte Datei ganz."""
    backup = target.with_name(target.name + ".bak")
    if not backup.exists():
        shutil.copyfile(target, backup)
    temporary = target.with_name(target.name + ".tmp")
    write(temporary)
    os.replace(temporary, target)


def write_parquet(path: Path, found: dict[int, int]) -> int:
    """Spalte `pi` ins Parquet, nach Rang zugeordnet. Gibt die Zahl belegter Zeilen."""
    import pyarrow as pa
    import pyarrow.parquet as pq

    table = pq.read_table(path)
    ranks = table["rank"].to_pylist()
    old = (table["pi"].to_pylist() if "pi" in table.column_names
           else [None] * len(ranks))
    values = []
    for rank, before in zip(ranks, old):
        if before is not None and before == before:        # vorhanden und nicht NaN
            values.append(int(before))
        else:
            values.append(found.get(int(rank)) if rank is not None else None)
    column = pa.array(values, type=pa.int32())
    if "pi" in table.column_names:
        table = table.set_column(table.column_names.index("pi"), "pi", column)
    else:
        table = table.append_column("pi", column)
    replace_atomically(path, lambda tmp: pq.write_table(table, tmp))
    return sum(1 for value in values if value is not None)


def backfill_board(folder: Path, pool: ProcessPoolExecutor, apply: bool) -> dict:
    state = json.loads((folder / "state.json").read_text(encoding="utf-8-sig"))
    klass = str(state.get("performance_class") or "")
    rows_path = folder / "rows.jsonl"
    parquet = folder / "leaderboard_entries.parquet"
    report = {"board": folder.name, "class": klass}
    if ocr.pi_range(klass) is None:
        report["skipped"] = f"unknown class {klass!r}"
        return report
    if not rows_path.exists():
        report["skipped"] = "no rows.jsonl"
        return report

    with zipfile.ZipFile(folder / PROOF_NAME) as bundle:
        crops = []
        for name in bundle.namelist():
            match = PROOF_RE.match(name)
            if match:
                crops.append((int(match.group(1)), bundle.read(name)))
    crops.sort()
    batches = [crops[i:i + BATCH] for i in range(0, len(crops), BATCH)]
    readings: list[tuple[int, int | None, float | None]] = []
    for result in pool.map(read_crops, batches):
        readings.extend(result)

    rows = load_rows(rows_path)
    by_rank = {int(row["rank"]): row for row in rows if row.get("rank") is not None}
    counts = Counter()
    outside = Counter()
    found: dict[int, int] = {}
    for rank, pi, seconds in readings:
        counts["crops"] += 1
        row = by_rank.get(rank)
        if row is None:
            counts["rank_not_in_rows"] += 1
            continue
        if pi is None:
            counts["pi_unread"] += 1
            continue
        if not ocr.pi_fits_class(pi, klass):
            counts["pi_outside_band"] += 1
            outside[pi] += 1
            continue
        if seconds is None:
            counts["time_unread"] += 1
            continue
        if abs(float(seconds) - float(row.get("lap_time_seconds") or -1)) > 0.0005:
            counts["time_mismatch"] += 1
            continue
        before = row.get("pi")
        if before is not None:
            counts["already_same" if before == pi else "already_different"] += 1
            continue
        found[rank] = pi
    report.update(counts)
    report["would_set"] = len(found)
    report["outside_values"] = outside.most_common(6)
    report["values"] = Counter(found.values()).most_common(5)
    if not apply or not found:
        return report

    for row in rows:
        rank = row.get("rank")
        if row.get("pi") is None:
            row["pi"] = found.get(int(rank)) if rank is not None else None

    def write_jsonl(tmp: Path) -> None:
        with tmp.open("w", encoding="utf-8") as handle:
            for row in rows:
                handle.write(json.dumps(row, separators=(",", ":")) + "\n")

    replace_atomically(rows_path, write_jsonl)
    if parquet.exists():
        report["parquet_rows_with_pi"] = write_parquet(parquet, found)
    report["written"] = True
    return report


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--root", type=Path, default=ROOT)
    parser.add_argument("--boards", nargs="*", default=None,
                        help="nur diese Laufordner (Name oder Pfad)")
    parser.add_argument("--limit", type=int, default=0, help="nur die ersten N Boards")
    parser.add_argument("--workers", type=int, default=2,
                        help="tesseract-Prozesse zugleich (hoechstens 4)")
    parser.add_argument("--apply", action="store_true",
                        help="wirklich schreiben; ohne das wird nur berichtet")
    args = parser.parse_args(argv)

    if not Path(ocr.TESSERACT).exists():
        print(f"tesseract not found at {ocr.TESSERACT}")
        return 2
    if args.boards:
        folders = [Path(name) if Path(name).is_dir() else args.root / name
                   for name in args.boards]
    else:
        folders = sorted(path.parent for path in args.root.glob(f"*/{PROOF_NAME}"))
    folders = [folder for folder in folders if (folder / PROOF_NAME).exists()
               and (folder / "state.json").exists()]
    if args.limit:
        folders = folders[:args.limit]
    if not folders:
        print("no board with proof.zip found")
        return 1

    workers = max(1, min(4, args.workers))
    total = Counter()
    with ProcessPoolExecutor(max_workers=workers) as pool:
        for folder in folders:
            report = backfill_board(folder, pool, args.apply)
            print(json.dumps(report, ensure_ascii=False), flush=True)
            for key in ("crops", "rank_not_in_rows", "pi_unread", "pi_outside_band",
                        "time_unread", "time_mismatch", "already_same",
                        "already_different", "would_set"):
                total[key] += int(report.get(key) or 0)
    print(f"\n{len(folders)} board(s): " + ", ".join(f"{key} {value:,}"
                                                for key, value in total.items()))
    if not args.apply:
        print("(dry run -- nothing written; --apply writes)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
