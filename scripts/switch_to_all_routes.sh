#!/usr/bin/env bash
# Beim naechsten Boardwechsel auf --route-indices 0-22 umstellen.
#
# Zweck: die zentrale offene Frage des Projekts beantworten, ohne dafuer Zeit zu opfern.
# Die Doku behauptet, ein Rivals-Board brauche eine selbst gefahrene Zeit fuer genau
# diese Strecke UND Klasse; der Katalog fuehrt das als unbeantwortet
# (reachable_without_personal_time: null). Waere es wahr, waeren die 112 Boards der 16
# unbefahrenen Strecken unerreichbar. Gegenindiz: Narai-Juku S1/S2/R hatten null Zeilen
# und liessen sich heute Nacht problemlos oeffnen.
# Statt eines Extra-Tests laeuft der Sweep einfach weiter in 7-22 hinein und
# protokolliert 'not reachable', wo es klemmt. Die Antwort faellt nebenbei ab.
#
# Der Fortschrittsstand laesst die 26 fertigen Boards ueberspringen, es geht also nichts
# doppelt.
#
# Lehre vom 10:25 eingebaut: damals starteten zwei Sweeps gleichzeitig und steuerten
# dasselbe Spiel. Hier wird nach dem Beenden ausdruecklich auf NULL Prozesse geprueft,
# und nach dem Start auf GENAU EINEN.
set -u
cd "$(dirname "$0")/.."
LOG=data/runtime/overnight/switch_routes.out
: > "$LOG"
say() { echo "$(date '+%H:%M:%S') $*" >> "$LOG"; }
count() {
  powershell.exe -NoProfile -Command \
    "@(Get-CimInstance Win32_Process | Where-Object { \$_.Name -eq 'python.exe' -and \$_.CommandLine -match 'ocr_board_sweep' }).Count" \
    2>/dev/null | tr -d '\r\n '
}

before=$(grep -c "board written" data/runtime/overnight/baseline20k.out 2>/dev/null || echo 0)
say "warte auf Abschluss des laufenden Boards (bisher $before geschrieben)"
while : ; do
  now=$(grep -c "board written" data/runtime/overnight/baseline20k.out 2>/dev/null || echo 0)
  [ "$now" -gt "$before" ] && break
  [ "$(count)" = "0" ] && { say "Sweep ist von sich aus beendet"; break; }
  sleep 20
done
say "Boardwechsel erreicht"

say "beende den Sweep"
powershell.exe -NoProfile -Command "
Get-CimInstance Win32_Process | Where-Object { \$_.CommandLine -match 'run_baseline_20k' -or (\$_.Name -eq 'python.exe' -and \$_.CommandLine -match 'ocr_board_sweep') } |
  ForEach-Object { Stop-Process -Id \$_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 3
Get-CimInstance Win32_Process | Where-Object { \$_.Name -eq 'powershell.exe' -and \$_.CommandLine -match 'capture_one_chunk' } |
  ForEach-Object { Stop-Process -Id \$_.ProcessId -Force -ErrorAction SilentlyContinue }
" >> "$LOG" 2>&1
sleep 4
for _ in 1 2 3 4 5; do
  [ "$(count)" = "0" ] && break
  say "noch aktiv, warte"; sleep 5
done
if [ "$(count)" != "0" ]; then say "ABBRUCH: Sweep laesst sich nicht beenden"; exit 1; fi
say "Sweep beendet, 0 Prozesse"

say "loesche Gasttasks (sonst haelt ein ChunkPress weiter DOWN)"
powershell.exe -NoProfile -Command "
\$c = [pscredential]::new('.\admin', [Security.SecureString]::new())
try {
  \$s = New-PSSession -VMName 'ForzaScrapeVM' -Credential \$c -ErrorAction Stop
  \$r = Invoke-Command -Session \$s -ScriptBlock {
    Get-ScheduledTask -ErrorAction SilentlyContinue |
      Where-Object { \$_.TaskName -like 'Chunk*' -or \$_.TaskName -like 'Cap*' -or \$_.TaskName -like 'ForzaKeys*' } |
      ForEach-Object { Stop-ScheduledTask -TaskName \$_.TaskName -ErrorAction SilentlyContinue
                       Unregister-ScheduledTask -TaskName \$_.TaskName -Confirm:\$false -ErrorAction SilentlyContinue
                       \$_.TaskName } }
  Remove-PSSession \$s -ErrorAction SilentlyContinue
  if (\$r) { 'geloescht: ' + (\$r -join ', ') } else { 'keine' }
} catch { 'Gastpruefung fehlgeschlagen: ' + \$_.Exception.Message }" >> "$LOG" 2>&1

say "starte Sweep mit ROUTES=0-22 bis 17:00"
ROUTES=0-22 UNTIL_HOUR=17 nohup bash scripts/run_baseline_20k.sh >> "$LOG" 2>&1 &
sleep 45
say "Prozesse jetzt: $(count) (soll 1 sein, 0 = noch in der Vorlaufphase)"
