#!/usr/bin/env bash
# Swap the running baseline scan onto the pipelined build once the A-class board finishes.
#
# The changes cannot be picked up by a running process (Python imported the source at
# start), and restarting mid-board discards that board's rows -- so the swap waits for a
# clean board boundary. The progress file is the boundary signal: it gains an entry only
# after a board has been written.
set -u
cd "$(dirname "$0")/.."
PROG=data/runtime/overnight/baseline20k_progress.json
LOG=data/runtime/overnight/pipeline_swap.out
: > "$LOG"
echo "waiting for the A board to be recorded in $PROG" >> "$LOG"

while : ; do
  if [ -f "$PROG" ] && python -c "
import json,sys
p=json.load(open('$PROG'))
sys.exit(0 if any(x[1]=='A' for x in p.get('pairs',[])) else 1)
" 2>/dev/null; then
    echo "A board recorded at $(date '+%H:%M:%S')" >> "$LOG"
    break
  fi
  sleep 20
done

echo "stopping the current scan" >> "$LOG"
powershell.exe -NoProfile -Command "
Get-CimInstance Win32_Process | Where-Object { \$_.Name -eq 'python.exe' -and \$_.CommandLine -match 'ocr_board_sweep' } |
  ForEach-Object { Stop-Process -Id \$_.ProcessId -Force -ErrorAction SilentlyContinue; 'stopped scan ' + \$_.ProcessId }
Start-Sleep -Seconds 3
Get-CimInstance Win32_Process | Where-Object { \$_.Name -eq 'powershell.exe' -and \$_.CommandLine -match 'capture_one_chunk' } |
  ForEach-Object { Stop-Process -Id \$_.ProcessId -Force -ErrorAction SilentlyContinue; 'stopped capture child ' + \$_.ProcessId }
" >> "$LOG" 2>&1
# also stop the old launcher so it cannot race the new one
pkill -f run_baseline_20k.sh 2>/dev/null || true
sleep 3

echo "relaunching on the pipelined build at $(date '+%H:%M:%S')" >> "$LOG"
nohup bash scripts/run_baseline_20k.sh > /dev/null 2>&1 &
echo "relaunched pid $!" >> "$LOG"
