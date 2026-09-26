#!/usr/bin/env bash
# Nacht 25.08.2026, aus der Sitzung heraus gestartet -- NICHT ueber den Aufgabenplaner.
# Der Versuch mit einer geplanten Aufgabe (ForzaNightNames, 01:00) endete mit Code 1,
# ohne auch nur die Logdatei anzulegen: bash hat das Skript nicht geoeffnet. Deshalb hier
# wieder der Weg, der die ganze Woche funktioniert hat.
#
# REIHENFOLGE: "mach vor allem mit Boards weiter die wir noch nicht haben -- ich seh auf
# der Analytics-Seite 7 Tracks, also fehlen noch jede Menge bis 23". Der Sweep auf den
# Strecken 7-22 bekommt daher die GANZE Nacht. Die Namensaufloesung laeuft nur, wenn dem
# Sweep vorher die Boards ausgehen -- sie ist nachrangig, obwohl sie ebenfalls fuer heute
# Nacht gewuenscht war.
# Beides nacheinander in EINEM Prozess, weil beides dasselbe Spiel fernsteuert -- zwei
# gleichzeitige Treiber haben sich am 24.08. um 10:25 schon einmal gegenseitig zerlegt.
set -u
cd "$(dirname "$0")/.."
LOG=data/runtime/overnight/night_run.out
mkdir -p data/runtime/overnight
: > "$LOG"
say() { echo "$(date '+%H:%M:%S') $*" >> "$LOG"; }

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

say "=== Nachtlauf ==="
say "Forza: $(forza_state)"
if [ "$(forza_state)" != "UP" ]; then
  say "starte Forza (um 20:50 zum Zocken beendet, die VM lief durch)"
  timeout 1200 powershell.exe -NoProfile -ExecutionPolicy Bypass \
    -File scripts/start_vm_forza.ps1 -VMName ForzaScrapeVM >> "$LOG" 2>&1
  say "  Start exit $?"
fi
# Ein ladendes Spiel sieht aus wie ein totes: der Fensterzeiger ist in beiden Faellen 0.
# Deshalb wird hier gewartet und nicht nach dem Zeiger geurteilt.
for i in 1 2 3 4 5 6 7 8 9 10; do
  [ "$(forza_state)" = "UP" ] && break
  say "  warte auf Forza ($i/10)"; sleep 60
done
if [ "$(forza_state)" != "UP" ]; then say "ABBRUCH: Forza laeuft nicht"; exit 1; fi
say "Forza laeuft"

# --- Teil 1: die restlichen Boards ---------------------------------------------------
# Strecken 7-22, die 16 nie gescannten. Der Sweep ueberspringt anhand der progress-Datei
# die 50 fertigen Boards. UNTIL_HOUR=6 heisst: ab 06:00 wird kein neues Board begonnen.
say "=== Teil 1: Sweep auf Strecken 7-22, bis 09:00 ==="
ROUTES=7-22 UNTIL_HOUR=9 bash scripts/run_baseline_20k.sh >> "$LOG" 2>&1
say "  Sweep beendet (exit $?)"

# --- Teil 2: die unbenannten Autos ---------------------------------------------------
# Ein Speicher-Lauf UND ein frischer Bildschirm-Scan desselben Boards, Minuten
# auseinander: nur so ueberlappen die Rundenzeiten, ueber die car_id und Name gepaart
# werden. Die vorhandenen Quellen liegen Tage auseinander, daher 76 unbenannte Autos.
# Shirakawa S1 deckt 62 davon ab, Highway A weitere 8 -- mehr passt vor 09:00 nicht.
# Teil 2 nur, wenn Teil 1 vor 07:00 fertig ist -- dann sind die Boards ausgegangen und
# die Zeit gehoert den Namen. Sonst hat der Sweep Vorrang bis zum Schluss.
now=$(date +%H%M)
if [ "$now" -ge 0700 ] && [ "$now" -lt 2000 ]; then
  say "=== Teil 2 entfaellt: der Sweep hat die Nacht gebraucht ($now) ==="
  BOARDS_FOR_NAMES=()
else
  say "=== Teil 2: Autonamen (dem Sweep sind die Boards ausgegangen) ==="
  BOARDS_FOR_NAMES=("2|S1|Shirakawa Circuit" "0|A|Highway Circuit")
fi
for entry in "${BOARDS_FOR_NAMES[@]+"${BOARDS_FOR_NAMES[@]}"}"; do
  IFS='|' read -r idx klass track <<< "$entry"
  now=$(date +%H%M)
  if [ "$now" -ge 0800 ] && [ "$now" -lt 2000 ]; then say "nach 08:00 -- kein weiteres Board"; break; fi
  say "--- $track $klass ---"
  say "  1/3 navigieren"
  timeout 1800 powershell.exe -NoProfile -ExecutionPolicy Bypass \
    -File scripts/start_forza_navigation.ps1 -VMName ForzaScrapeVM \
    -Track "$track" -PerformanceClass "$klass" -RivalsMode "Road Racing" >> "$LOG" 2>&1
  if [ $? -ne 0 ]; then say "  Navigation gescheitert -- naechstes Board"; continue; fi
  say "  2/3 Speicher-Lauf (liefert die car_ids)"
  timeout 5400 powershell.exe -NoProfile -ExecutionPolicy Bypass \
    -File scripts/start_vm_memory_leaderboard_scan.ps1 \
    -Track "$track" -PerformanceClass "$klass" -RivalsMode "Road Racing" \
    -HostOutputRoot data/memory_scans/full_sweep -SkipNavigation -KeepForzaRunning >> "$LOG" 2>&1
  say "    exit $?"
  say "  3/3 frischer Bildschirm-Scan desselben Boards (liefert die Namen)"
  # KEIN --progress-file: mit ihr gilt das Board als erledigt und wird uebersprungen.
  timeout 3600 python scripts/ocr_board_sweep.py \
    --route-indices "$idx" --classes "$klass" --row-cap 3000 --until-hour 9 >> "$LOG" 2>&1
  say "    exit $?"
  NAMES_RAN=$(( ${NAMES_RAN:-0} + 1 ))
done

# Join und Aufbau NUR, wenn Teil 2 gelaufen ist. run_baseline_20k.sh haengt beides
# selbst schon an den Sweep an -- am 2026-08-25 lief dadurch alles zweimal, rund 25
# Minuten CPU umsonst, kurz bevor der Rechner wieder gebraucht wurde. Neue Namen
# entstehen ohnehin nur aus Teil 2; ohne ihn hat der Aufbau des Wrappers alles drin.
if [ "${NAMES_RAN:-0}" -gt 0 ]; then
  say "=== Join und Seitenaufbau (Teil 2 hat neue Speicher-Zeilen geliefert) ==="
  timeout 1800 python scripts/join_ocr_names_to_car_ids.py >> "$LOG" 2>&1
  say "  Join exit $?"
  timeout 2400 python scripts/build_analytics_site.py >> "$LOG" 2>&1
  say "  Aufbau exit $?"
else
  say "=== Join und Aufbau entfallen: der Sweep-Wrapper hat sie schon erledigt ==="
fi
grep -oE '[0-9]+ boards, [0-9]+ laps.*cars named' data/runtime/overnight/site_build.log 2>/dev/null |
  tail -1 | sed 's/^/  Ergebnis: /' >> "$LOG"

say "=== aufraeumen: Gast-Tasks weg, Forza aus ==="
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
  Remove-PSSession \$s -ErrorAction SilentlyContinue } catch {}" >> "$LOG" 2>&1
say "fertig"
