#!/usr/bin/env bash
# Sweep neu starten, damit er die erweiterte Ueberspringen-Liste einliest.
# Er haelt seinen Stand im Arbeitsspeicher und schreibt ihn nach jedem Board zurueck --
# eine Aenderung an der Datei waehrend des Laufs wird also ueberschrieben (genau so ist
# heute morgen schon eine Korrektur verloren gegangen).
set -u
cd "$(dirname "$0")/.."
LOG=data/runtime/overnight/restart_skiplist.out
: > "$LOG"
say() { echo "$(date '+%H:%M:%S') $*" >> "$LOG"; }
count() {
  powershell.exe -NoProfile -Command \
    "@(Get-CimInstance Win32_Process | Where-Object { \$_.Name -eq 'python.exe' -and \$_.CommandLine -match 'ocr_board_sweep' }).Count" \
    2>/dev/null | tr -d '\r\n '
}
say "beende den Sweep (steckt in einem Board mit 98% Abdeckung, kein Verlust)"
powershell.exe -NoProfile -Command "
Get-CimInstance Win32_Process | Where-Object { \$_.CommandLine -match 'run_baseline_20k' -or (\$_.Name -eq 'python.exe' -and \$_.CommandLine -match 'ocr_board_sweep') } |
  ForEach-Object { Stop-Process -Id \$_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 3
Get-CimInstance Win32_Process | Where-Object { \$_.Name -eq 'powershell.exe' -and \$_.CommandLine -match 'capture_one_chunk' } |
  ForEach-Object { Stop-Process -Id \$_.ProcessId -Force -ErrorAction SilentlyContinue }" >> "$LOG" 2>&1
sleep 4
for _ in 1 2 3 4 5; do [ "$(count)" = "0" ] && break; say "noch aktiv"; sleep 5; done
[ "$(count)" != "0" ] && { say "ABBRUCH: laesst sich nicht beenden"; exit 1; }
say "beendet, 0 Prozesse"
powershell.exe -NoProfile -Command "
\$c = [pscredential]::new('.\admin', [Security.SecureString]::new())
try {
  \$s = New-PSSession -VMName 'ForzaScrapeVM' -Credential \$c -ErrorAction Stop
  \$r = Invoke-Command -Session \$s -ScriptBlock {
    Get-ScheduledTask -ErrorAction SilentlyContinue |
      Where-Object { \$_.TaskName -like 'Chunk*' -or \$_.TaskName -like 'Cap*' } |
      ForEach-Object { Stop-ScheduledTask -TaskName \$_.TaskName -ErrorAction SilentlyContinue
                       Unregister-ScheduledTask -TaskName \$_.TaskName -Confirm:\$false -ErrorAction SilentlyContinue
                       \$_.TaskName } }
  Remove-PSSession \$s -ErrorAction SilentlyContinue
  if (\$r) { 'Gasttasks geloescht: ' + (\$r -join ', ') } else { 'keine Gasttasks' }
} catch { 'Gastpruefung: ' + \$_.Exception.Message }" >> "$LOG" 2>&1
say "starte neu mit ROUTES=0-22, Ueberspringen-Liste aktiv"
ROUTES=0-22 UNTIL_HOUR=17 nohup bash scripts/run_baseline_20k.sh >> "$LOG" 2>&1 &
sleep 50
say "Prozesse: $(count) (soll 1)"
say "erste Zeilen des neuen Laufs:"
tail -6 data/runtime/overnight/baseline20k.out >> "$LOG"
