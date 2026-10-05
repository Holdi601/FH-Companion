#!/usr/bin/env bash
# Strip the boards scanned under the rank-clipping bug from the progress file -- AFTER
# the scan process is gone.
#
# Editing it while the sweep runs does not stick: remember() rewrites the file from its
# own in-memory set, so an external deletion is restored the next time a board finishes.
# Same shape as the race that overwrote the depth report on 2026-08-23.
set -u
cd "$(dirname "$0")/.."
LOG=data/runtime/overnight/progress_fix.out
: > "$LOG"
echo "warte darauf, dass der Scan endet" >> "$LOG"
while : ; do
  if powershell.exe -NoProfile -Command \
     "if (Get-CimInstance Win32_Process | Where-Object { \$_.Name -eq 'python.exe' -and \$_.CommandLine -match 'ocr_board_sweep' }) { 'RUNNING' }" 2>/dev/null | grep -q RUNNING; then
    sleep 30
  else
    break
  fi
done
echo "Scan beendet $(date '+%H:%M:%S') -- Fortschrittsstand korrigieren" >> "$LOG"
python - >> "$LOG" 2>&1 <<'PY'
import json
from pathlib import Path
p = Path("data/runtime/overnight/baseline20k_progress.json")
d = json.loads(p.read_text(encoding="utf-8"))
before = len(d["pairs"])
d["pairs"]  = [x for x in d["pairs"]  if not (x[0] == 0 and x[1] in ("A", "S1"))]
d["boards"] = [x for x in d["boards"] if not ("Highway" in x[0] and x[1] in ("A", "S1"))]
d["note"] = ("Highway A und S1 entfernt: beide wurden vor der Verbreiterung der "
             "Rangspalte (x=100 -> 78) gescannt, endeten bei max 17.187 mit ~52% Dichte "
             "gegen 98,9% danach. Sie werden im naechsten Lauf neu gescannt.")
p.write_text(json.dumps(d, indent=1), encoding="utf-8")
print(f"pairs {before} -> {len(d['pairs'])}")
print("verbleibend:", d["pairs"])
PY
echo "fertig" >> "$LOG"
