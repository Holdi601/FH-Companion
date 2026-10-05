#!/usr/bin/env bash
# Der Lauf stoppt um 10:00 (--until-hour 10). Der Operator ist bis ~17:00 auf Arbeit,
# die Maschine also frei -- also direkt weiterlaufen lassen statt bis heute Nacht zu
# warten. Reihenfolge zaehlt: erst muss der alte Prozess weg sein UND die
# Fortschrittskorrektur durch, sonst schreibt der eine dem anderen ins Handwerk.
set -u
cd "$(dirname "$0")/.."
LOG=data/runtime/overnight/continue_day.out
: > "$LOG"

echo "warte auf Ende des Nachtlaufs" >> "$LOG"
while : ; do
  if powershell.exe -NoProfile -Command \
     "if (Get-CimInstance Win32_Process | Where-Object { \$_.Name -eq 'python.exe' -and \$_.CommandLine -match 'ocr_board_sweep' }) { 'RUNNING' }" 2>/dev/null | grep -q RUNNING; then
    sleep 30
  else
    break
  fi
done
echo "Nachtlauf beendet $(date '+%H:%M:%S')" >> "$LOG"

# auf die Fortschrittskorrektur warten (max 5 Min), damit Highway A/S1 wirklich neu laufen
for _ in $(seq 1 10); do
  grep -q "fertig" data/runtime/overnight/progress_fix.out 2>/dev/null && break
  sleep 30
done
echo "Fortschrittskorrektur: $(tail -2 data/runtime/overnight/progress_fix.out 2>/dev/null | tr '\n' ' ')" >> "$LOG"

echo "Tageslauf startet $(date '+%H:%M:%S'), bis 17:00" >> "$LOG"
UNTIL_HOUR=17 nohup bash scripts/run_baseline_20k.sh >> "$LOG" 2>&1 &
echo "gestartet pid $!" >> "$LOG"
