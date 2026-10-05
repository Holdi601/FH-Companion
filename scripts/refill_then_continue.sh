#!/usr/bin/env bash
# Erst eine Datenluecke schliessen, dann die Breite weitertreiben.
#
# LUECKE: Electric Town Circuit A beginnt bei Rang 2394. Beim Scan am 2026-08-25 um
# 14:07 starb die OCR des ersten Chunks (BrokenProcessPool bei 14 GB freiem Speicher --
# ein Absturz, keine Last), und ocr_zip loeschte Archiv und Frames ohne Ansehen des
# Ergebnisses. Damit fehlen die 2.393 schnellsten Runden des Boards, also genau der Teil,
# aus dem eine Auto-Wertung entsteht. Beides ist im Code inzwischen abgestellt (ein
# zweiter OCR-Versuch mit weniger Arbeitern; Archiv bleibt liegen, wenn nichts herauskam),
# aber diese Zeilen holt nur ein neuer Scan.
#
# KEIN --progress-file im ersten Schritt: idx07 A gilt dort als erledigt und wuerde
# uebersprungen. Der zweite Schritt nutzt sie wieder, damit die 72 fertigen Boards nicht
# erneut laufen.
set -u
cd "$(dirname "$0")/.."
LOG=data/runtime/overnight/refill.out
mkdir -p data/runtime/overnight
: > "$LOG"
say() { echo "$(date '+%H:%M:%S') $*" >> "$LOG"; }

say "=== Schritt 1: Electric Town Circuit A neu scannen (Raenge 1-20.000) ==="
timeout 3600 python scripts/ocr_board_sweep.py \
  --route-indices 7 --classes A --row-cap 20000 --until-hour 20 >> "$LOG" 2>&1
say "  exit $?"
python - >> "$LOG" 2>&1 <<'PY'
import json
from pathlib import Path
runs = sorted(Path("data/memory_scans/full_sweep").glob("ocr_RoadRacing_idx07_A_*"))
for d in runs:
    f = d / "state.json"
    if not f.exists(): continue
    st = json.loads(f.read_text(encoding="utf-8-sig"))
    print(f"  {d.name}: {st.get('rows_collected')} Zeilen, "
          f"Raenge {st.get('minimum_rank')}..{st.get('maximum_rank')}")
PY

say "=== Schritt 2: weiter mit den fehlenden Strecken ==="
ROUTES=7-22 UNTIL_HOUR=20 bash scripts/run_baseline_20k.sh >> "$LOG" 2>&1
say "  Sweep beendet (exit $?)"
