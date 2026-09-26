#!/usr/bin/env bash
# Nacht vom 24. auf den 25.08.2026: die unbenannten Autos aufloesen.
#
# WARUM SO: ein car_id bekommt seinen Namen nur, wenn eine Speicher-Zeile (car_id, kein
# Name) und eine Bildschirm-Zeile (Name, keine car_id) desselben Boards ueber dieselbe
# Rundenzeit zusammenfinden. Alle bisherigen Speicher-Laeufe sind vom 20.-22.08., alle
# Bildschirm-Laeufe vom 23.-24.08. -- dazwischen wurden neue Zeiten gesetzt, die
# Zeitgruppen passen nicht mehr, und 76 Autos stehen als "Car #1283" im Dashboard.
# Ein Speicher-Lauf allein behebt das NICHT: er waere wieder Tage von den vorhandenen
# Bildschirm-Laeufen entfernt. Deshalb pro Board BEIDES hintereinander, Minuten
# auseinander -- dann ist der Ueberlapp garantiert.
#
# BOARD-AUSWAHL: gemessen am 24.08. decken 5 Boards alle 76 unbenannten Autos ab,
# Shirakawa S1 allein 62. Reihenfolge nach Ertrag, damit ein Abbruch das Wichtigste
# schon hat.
#
# Route-Index: 0 Highway, 1 Narai-Juku, 2 Shirakawa, 3 Shimanoyama, 4 Hokubu,
#              5 Soni, 6 Daikoku.
# KEIN --progress-file: sonst gelten diese Boards als "schon erledigt" und werden
# uebersprungen -- die 49 fertigen Boards stehen genau dort drin.
set -u
cd "$(dirname "$0")/.."
LOG=data/runtime/overnight/night_names.out
mkdir -p data/runtime/overnight
: > "$LOG"
say() { echo "$(date '+%H:%M:%S') $*" >> "$LOG"; }

MEM_OUT=data/memory_scans/full_sweep
STOP_NEW=0630          # nach dieser Uhrzeit kein neues Board mehr anfangen
BOARDS=( "2|S1|Shirakawa Circuit" "0|A|Highway Circuit" "2|R|Shirakawa Circuit"
         "1|B|Narai-Juku Circuit" "0|C|Highway Circuit" )

forza_state() {
  powershell.exe -NoProfile -Command "
\$c = [pscredential]::new('.\admin', [Security.SecureString]::new())
try {
  \$s = New-PSSession -VMName 'ForzaScrapeVM' -Credential \$c -ErrorAction Stop
  \$r = Invoke-Command -Session \$s -ScriptBlock {
    if (Get-Process -Name 'forzahorizon6' -ErrorAction SilentlyContinue) { 'UP' } else { 'DOWN' } }
  Remove-PSSession \$s -ErrorAction SilentlyContinue
  \$r } catch { 'UNREACHABLE' }" 2>/dev/null | tr -d '\r\n '
}

say "=== Nachtlauf Autonamen ==="
state=$(forza_state)
say "Forza: $state"
if [ "$state" != "UP" ]; then
  say "starte Forza (die VM laeuft, nur das Spiel wurde um 20:50 fuer das Zocken beendet)"
  timeout 900 powershell.exe -NoProfile -ExecutionPolicy Bypass \
    -File scripts/start_vm_forza.ps1 -VMName ForzaScrapeVM >> "$LOG" 2>&1
  say "  Start exit $?, Zustand jetzt: $(forza_state)"
fi
[ "$(forza_state)" = "UP" ] || { say "ABBRUCH: Forza laeuft nicht"; exit 1; }

done_boards=0
for entry in "${BOARDS[@]}"; do
  IFS='|' read -r idx klass track <<< "$entry"
  now=$(date +%H%M)
  if [ "$now" -ge "$STOP_NEW" ] && [ "$now" -lt 2000 ]; then
    say "nach $STOP_NEW -- kein neues Board mehr"; break
  fi
  say "--- $track $klass (idx $idx) ---"

  say "  1/3 navigieren"
  timeout 1800 powershell.exe -NoProfile -ExecutionPolicy Bypass \
    -File scripts/start_forza_navigation.ps1 -VMName ForzaScrapeVM \
    -Track "$track" -PerformanceClass "$klass" -RivalsMode "Road Racing" >> "$LOG" 2>&1
  if [ $? -ne 0 ]; then say "  Navigation gescheitert -- naechstes Board"; continue; fi

  say "  2/3 Speicher-Lauf (liefert die car_ids)"
  timeout 5400 powershell.exe -NoProfile -ExecutionPolicy Bypass \
    -File scripts/start_vm_memory_leaderboard_scan.ps1 \
    -Track "$track" -PerformanceClass "$klass" -RivalsMode "Road Racing" \
    -HostOutputRoot "$MEM_OUT" -SkipNavigation -KeepForzaRunning >> "$LOG" 2>&1
  say "    exit $?"

  say "  3/3 frischer Bildschirm-Scan desselben Boards (liefert die Namen)"
  timeout 3600 python scripts/ocr_board_sweep.py \
    --route-indices "$idx" --classes "$klass" --row-cap 3000 --until-hour 9 >> "$LOG" 2>&1
  say "    exit $?"
  done_boards=$((done_boards+1))
done
say "Boards bearbeitet: $done_boards"

if [ "$done_boards" -gt 0 ]; then
  say "=== Join car_id -> Name ==="
  timeout 1800 python scripts/join_ocr_names_to_car_ids.py >> "$LOG" 2>&1
  say "  exit $?"
fi

# Der Aufbau laeuft unabhaengig von den Scans: die geaenderte drop_contested-Regel
# (stark belegte Zweitplatzierte behalten ihren Namen, 7 Autos) wirkt erst mit ihm.
say "=== Seite neu aufbauen ==="
timeout 2400 python scripts/build_analytics_site.py >> "$LOG" 2>&1
say "  exit $?"
grep -oE '[0-9]+/[0-9]+ cars named' data/runtime/overnight/site_build.log 2>/dev/null | tail -1 |
  sed 's/^/  Ergebnis: /' >> "$LOG"

say "=== Forza beenden, GPU fuer den Morgen frei ==="
powershell.exe -NoProfile -Command "
\$c = [pscredential]::new('.\admin', [Security.SecureString]::new())
try {
  \$s = New-PSSession -VMName 'ForzaScrapeVM' -Credential \$c -ErrorAction Stop
  Invoke-Command -Session \$s -ScriptBlock {
    Get-ScheduledTask -ErrorAction SilentlyContinue |
      Where-Object { \$_.TaskName -like 'Chunk*' -or \$_.TaskName -like 'Cap*' -or \$_.TaskName -like 'ForzaMemoryScan*' } |
      ForEach-Object { Stop-ScheduledTask -TaskName \$_.TaskName -ErrorAction SilentlyContinue
                       Unregister-ScheduledTask -TaskName \$_.TaskName -Confirm:\$false -ErrorAction SilentlyContinue }
    Get-Process -Name 'forzahorizon6' -ErrorAction SilentlyContinue | Stop-Process -Force }
  Remove-PSSession \$s -ErrorAction SilentlyContinue
  'aufgeraeumt' } catch { 'Gast nicht erreichbar' }" >> "$LOG" 2>&1
say "fertig"
