#!/usr/bin/env bash
# Sichert, dass der OCR-Sweep der Nacht wirklich laeuft.
#
# Grund: die Stoppregel im Sweep ist `hour >= until_hour and hour < 20`. Mit
# UNTIL_HOUR=9 beendet sich der Lauf also SOFORT, wenn er zwischen 9 und 20 Uhr
# gestartet wird -- die Regel setzt voraus, dass man abends startet. Endet Phase 1 des
# Nachtplans vor 20:00, startet Phase 2 in genau dieses Fenster und stirbt beim ersten
# Board. Am 2026-08-24 um 18:31 ist das schon einmal passiert.
#
# Dieser Waechter wartet, bis der Nachtplan durch ist, prueft ob ein Sweep laeuft, und
# startet ihn notfalls -- aber erst in einem Zeitfenster, in dem die Stoppregel ihn nicht
# gleich wieder beendet (ab 20:00 oder vor 09:00).
set -u
cd "$(dirname "$0")/.."
LOG=data/runtime/overnight/ensure_sweep.out
: > "$LOG"
say() { echo "$(date '+%H:%M:%S') $*" >> "$LOG"; }

alive() {  # alive <Muster>
  powershell.exe -NoProfile -Command \
    "@(Get-CimInstance Win32_Process | Where-Object { \$_.CommandLine -like '*$1*' -and \$_.CommandLine -notlike '*CimInstance*' }).Count" \
    2>/dev/null | tr -d '\r\n '
}

say "warte, bis der Nachtplan durch ist"
while [ "$(alive 'night_plan.sh')" != "0" ]; do sleep 60; done
say "Nachtplan beendet"

# Kurz warten, damit ein von Phase 2 gestarteter Sweep sich zeigen kann.
sleep 90
if [ "$(alive 'ocr_board_sweep.py')" != "0" ]; then
  say "ein Sweep laeuft bereits -- nichts zu tun"
  exit 0
fi
say "kein Sweep aktiv"

while : ; do
  h=$(date +%H)
  # Sicheres Startfenster: ab 20 Uhr oder vor 9 Uhr, sonst beendet die Stoppregel ihn.
  if [ "$h" -ge 20 ] || [ "$h" -lt 9 ]; then break; fi
  say "  $h Uhr liegt im Sperrfenster 9-20, warte"
  sleep 600
done

say "starte OCR-Sweep auf Strecken 7-22 bis 09:00"
ROUTES=7-22 UNTIL_HOUR=9 nohup bash scripts/run_baseline_20k.sh >> "$LOG" 2>&1 &
sleep 90
say "Sweep-Prozesse jetzt: $(alive 'ocr_board_sweep.py')"
