#!/usr/bin/env bash
# Nachtplan, zweiter Versuch. Zwei Phasen.
#
# PHASE 1 (bis 3 h): SPEICHER-LAEUFE auf den Boards mit unbenannten Autos, ZWEISTUFIG.
#   Warum zweistufig: start_vm_memory_leaderboard_scan.ps1 dokumentiert selbst, dass sein
#   eingebauter Navigator (navigate_forza_rivals_target.ps1) auf Spielstand 6.420.696.0
#   kein Board mehr erreicht. Der Weg ist: erst mit start_forza_navigation.ps1 zum
#   Leaderboard, dann den Speicher-Scan mit -SkipNavigation darauf ansetzen.
#   Der erste Versuch heute scheiterte zusaetzlich an einem beschaedigten Pfad im
#   Skript -- '.\scripts\navigate_...' war zu '.\scripts' + Zeilenumbruch + 'avigate_...'
#   verstuemmelt, ein verschlucktes \n. Repariert.
#
#   Zweck der Phase: die car_id -> Name Zuordnung entsteht, indem Speicher-Zeilen
#   (car_id, kein Name) und Bildschirm-Zeilen (Name, keine car_id) ueber die Rundenzeit
#   verbunden werden. Alle Speicher-Laeufe sind vom 20.-22.08., alle Bildschirm-Laeufe vom
#   23.-24.08. -- nie zeitnah. Dazwischen wurden neue Zeiten gesetzt, die Zeitgruppen
#   passen nicht mehr, 81 Autos bleiben ohne Namen.
#   Shimanoyama und Hokubu fehlen: dort haengt die Speicher-Route bei 50 Zeilen.
#
# PHASE 2 (Rest der Nacht): OCR-Sweep auf Strecken 7-22, 112 nie gescannte Boards.
#   ACHTUNG Startfenster: die Stoppregel im Sweep ist `hour >= until_hour and hour < 20`.
#   Mit UNTIL_HOUR=9 beendet er sich sofort, wenn er zwischen 9 und 20 Uhr startet.
#   Deshalb wird hier gewartet, bis 20:00 vorbei ist.
set -u
cd "$(dirname "$0")/.."
LOG=data/runtime/overnight/night_plan2.out
mkdir -p data/runtime/overnight
: > "$LOG"
say() { echo "$(date '+%H:%M:%S') $*" >> "$LOG"; }
alive() {
  powershell.exe -NoProfile -Command \
    "@(Get-CimInstance Win32_Process | Where-Object { \$_.CommandLine -like '*$1*' -and \$_.CommandLine -notlike '*CimInstance*' }).Count" \
    2>/dev/null | tr -d '\r\n '
}

MEM_OUT=data/memory_scans/full_sweep
PHASE1_END=$(( $(date +%s) + 3*3600 ))
BOARDS=(
  "Highway Circuit|S1"   "Shirakawa Circuit|S1"  "Highway Circuit|A"
  "Shirakawa Circuit|A"  "Narai-Juku Circuit|A"  "Shirakawa Circuit|R"
)

newest_rows() {
  local newest
  newest=$(ls -dt "$MEM_OUT"/*/ 2>/dev/null | grep -v '/ocr_' | head -1)
  [ -z "$newest" ] && { echo 0; return; }
  python - "$newest" <<'PY' 2>/dev/null || echo 0
import json, sys
from pathlib import Path
try:
    print(json.loads((Path(sys.argv[1]) / "state.json").read_text(encoding="utf-8-sig")).get("rows_collected") or 0)
except Exception:
    print(0)
PY
}

run_board() {   # run_board <Track> <Klasse>
  say "--- $1 $2 ---"
  say "  Schritt 1: zum Leaderboard navigieren"
  timeout 1800 powershell.exe -NoProfile -ExecutionPolicy Bypass \
    -File scripts/start_forza_navigation.ps1 \
    -VMName ForzaScrapeVM -Track "$1" -PerformanceClass "$2" -RivalsMode "Road Racing" \
    >> "$LOG" 2>&1
  local nav=$?
  if [ "$nav" -ne 0 ]; then say "  Navigation exit $nav -- Board uebersprungen"; return 1; fi
  say "  Schritt 2: Speicher-Scan mit -SkipNavigation"
  local before after
  before=$(newest_rows)
  timeout 5400 powershell.exe -NoProfile -ExecutionPolicy Bypass \
    -File scripts/start_vm_memory_leaderboard_scan.ps1 \
    -Track "$1" -PerformanceClass "$2" -RivalsMode "Road Racing" \
    -HostOutputRoot "$MEM_OUT" -SkipNavigation -KeepForzaRunning >> "$LOG" 2>&1
  local code=$?
  after=$(newest_rows)
  say "  Scan exit $code, Zeilen im jungsten Lauf: $after (vorher $before)"
  [ "$after" -gt 100 ] && [ "$after" -ne "$before" ]
}

say "=== Phase 1: Speicher-Laeufe, Fenster 3 h ==="
ok=0
for entry in "${BOARDS[@]}"; do
  if [ "$(date +%s)" -ge "$PHASE1_END" ]; then say "Fenster ausgeschoepft"; break; fi
  if run_board "${entry%|*}" "${entry#*|}"; then
    ok=$((ok+1)); say "  brauchbar ($ok bisher)"
  else
    say "  ohne brauchbares Ergebnis"
    if [ "$ok" -eq 0 ]; then say "ABBRUCH von Phase 1 nach dem ersten Fehlschlag"; break; fi
  fi
done
say "Phase 1 beendet, $ok brauchbare Laeufe"

if [ "$ok" -gt 0 ]; then
  say "=== Join und Neuaufbau ==="
  timeout 900 python scripts/join_ocr_names_to_car_ids.py >> "$LOG" 2>&1
  say "  Join exit $?"
  timeout 1200 python scripts/build_analytics_site.py >> "$LOG" 2>&1
  say "  Aufbau exit $?"
fi

say "=== Phase 2: warte auf sicheres Startfenster fuer den Sweep ==="
while : ; do
  h=$(date +%H)
  if [ "$h" -ge 20 ] || [ "$h" -lt 9 ]; then break; fi
  say "  $h Uhr liegt im Sperrfenster 9-20, warte 10 min"
  sleep 600
done
if [ "$(alive 'ocr_board_sweep.py')" != "0" ]; then
  say "es laeuft schon ein Sweep -- nichts zu tun"
else
  say "starte OCR-Sweep auf Strecken 7-22 bis 09:00"
  ROUTES=7-22 UNTIL_HOUR=9 nohup bash scripts/run_baseline_20k.sh >> "$LOG" 2>&1 &
  sleep 90
  say "Sweep-Prozesse: $(alive 'ocr_board_sweep.py')"
fi
say "Nachtplan fertig aufgesetzt"
