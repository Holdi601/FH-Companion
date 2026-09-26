#!/usr/bin/env bash
# Breadth-first 20,000-rank baseline over every reachable Road Racing board.
#
# Why a cap: measured on 2026-08-23, a full pass over all 161 Road Racing boards is
# ~78 h, but 17 of 35 measured boards are under 20,000 ranks and finish outright under
# the cap. So a capped pass banks a usable baseline everywhere first, and the boards it
# stops early are recorded as `row_cap_reached` -- the exact worklist for raising it.
#
# Depth is measured DURING this scan (estimate_length over the thumb readings each chunk
# already takes), so no separate probe pass runs and no rows are thrown away. The old
# probe discarded ~66,600 real rows on 2026-08-23 doing exactly this work.
set -u
cd "$(dirname "$0")/.."
LOG=data/runtime/overnight/baseline20k.out
mkdir -p data/runtime/overnight
# Append, never truncate. A restart used to wipe this, and the previous board's chunk
# detail -- the only record of HOW a board went wrong -- went with it.
{ echo; echo "############ launch $(date '+%Y-%m-%d %H:%M:%S') ############"; } >> "$LOG"

echo "=== waiting for any running depth probe to finish ===" >> "$LOG"
while : ; do
  if powershell.exe -NoProfile -Command \
     "if (Get-CimInstance Win32_Process | Where-Object { \$_.Name -eq 'python.exe' -and \$_.CommandLine -match 'probe_board_depth' }) { 'RUNNING' }" 2>/dev/null | grep -q RUNNING; then
    sleep 30
  else
    break
  fi
done
echo "probe clear at $(date '+%H:%M:%S')" >> "$LOG"

# A killed or finished runner can leave a guest task holding the DOWN key, and the next
# navigation then fails as "could not find the anchor route" -- which reads like a
# screen-recognition bug and sends you looking in the wrong place.
echo "=== clearing stale guest tasks ===" >> "$LOG"
powershell.exe -NoProfile -Command "
\$c = [pscredential]::new('.\admin', [Security.SecureString]::new())
try {
  \$s = New-PSSession -VMName 'ForzaScrapeVM' -Credential \$c -ErrorAction Stop
  \$r = Invoke-Command -Session \$s -ScriptBlock {
    Get-ScheduledTask -ErrorAction SilentlyContinue |
      Where-Object { \$_.TaskName -like 'Chunk*' -or \$_.TaskName -like 'Cap*' -or \$_.TaskName -like 'ForzaMemoryScan*' } |
      ForEach-Object { Stop-ScheduledTask -TaskName \$_.TaskName -ErrorAction SilentlyContinue
                       Unregister-ScheduledTask -TaskName \$_.TaskName -Confirm:\$false -ErrorAction SilentlyContinue
                       \$_.TaskName }
  }
  Remove-PSSession \$s -ErrorAction SilentlyContinue
  if (\$r) { 'cleared: ' + (\$r -join ', ') } else { 'none' }
} catch { 'guest check failed: ' + \$_.Exception.Message }" >> "$LOG" 2>&1

echo "=== baseline scan starting $(date '+%H:%M:%S') ===" >> "$LOG"
# idx 0-6 = Highway, Narai-Juku, Shirakawa, Shimanoyama, Hokubu, Soni, Daikoku.
python scripts/ocr_board_sweep.py \
  --route-indices ${ROUTES:-0-6} \
  --classes D,C,B,A,S1,S2,R \
  --row-cap 20000 \
  --chunk-seconds 120 \
  --max-chunks 12 \
  --until-hour ${UNTIL_HOUR:-10} \
  --progress-file data/runtime/overnight/baseline20k_progress.json \
  >> "$LOG" 2>&1
code=$?
echo "=== baseline scan exited ($code) at $(date '+%H:%M:%S') ===" >> "$LOG"

# Autonamen nachziehen. join_ocr_names_to_car_ids.py ist der Schritt, der einer
# Speicher-car_id ihren Namen gibt (Paarung ueber die Rundenzeit) -- und niemand stoesst
# ihn an. Am 2026-08-24 war er einen Tag alt und hatte 42 Scans nicht gesehen: die Namen
# lagen ungenutzt da, und schwerwiegender lief dasselbe Auto als Speicher-ID UND als
# Bildschirm-Name durch die Wertung, trat gegen sich selbst an und blies die Feldgroesse
# auf, aus der die Punkte gerechnet werden. Ein Nachziehen loeste 109 Doppelungen.
echo "=== Autonamen nachziehen $(date '+%H:%M:%S') ===" >> "$LOG"
timeout 900 python scripts/join_ocr_names_to_car_ids.py >> "$LOG" 2>&1
echo "  join exit $?" >> "$LOG"
timeout 1200 python scripts/build_analytics_site.py >> "$LOG" 2>&1
echo "  Seitenaufbau exit $?" >> "$LOG"
