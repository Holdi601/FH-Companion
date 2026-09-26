#!/usr/bin/env bash
# Wartet auf das Ende des laufenden Scans, untersucht das Karussell, startet den Scan
# sofort wieder. Der Operator hat ausdruecklich gesagt: keine Zeit verschenken.
set -u
cd "$(dirname "$0")/.."
LOG=data/runtime/overnight/carousel_then_resume.out
: > "$LOG"
echo "warte auf Ende des Scans (Shimanoyama R laeuft noch)" >> "$LOG"
while : ; do
  if powershell.exe -NoProfile -Command \
     "if (Get-CimInstance Win32_Process | Where-Object { \$_.Name -eq 'python.exe' -and \$_.CommandLine -match 'ocr_board_sweep' }) { 'RUNNING' }" 2>/dev/null | grep -q RUNNING; then
    sleep 30
  else
    break
  fi
done
echo "Scan beendet $(date '+%H:%M:%S')" >> "$LOG"
for _ in $(seq 1 6); do
  grep -q "fertig" data/runtime/overnight/progress_fix.out 2>/dev/null && break
  sleep 20
done
echo "Karussell-Untersuchung startet $(date '+%H:%M:%S')" >> "$LOG"
bash scripts/probe_carousel.sh >> "$LOG" 2>&1
echo "Karussell fertig $(date '+%H:%M:%S') -- Scan wieder starten" >> "$LOG"
UNTIL_HOUR=17 nohup bash scripts/run_baseline_20k.sh >> "$LOG" 2>&1 &
echo "Scan neu gestartet pid $!" >> "$LOG"
