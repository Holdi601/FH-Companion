#!/usr/bin/env bash
# Stellt sicher, dass Forza laeuft, bevor der Nachtsweep um 20:00 anlaeuft.
#
# Grund: das Spiel steht ab 18:39 untaetig auf dem Titelbildschirm, bis der Sweep um
# 20:00 startet -- ueber eine Stunde Leerlauf. In genau solchen Luecken stirbt es, und
# ein toter Prozess laesst die Navigation mit "forzahorizon6 is not running in this
# session" scheitern. Der Sweep gaebe dann Board fuer Board auf und die Nacht waere weg.
#
# Der Waechter prueft ab 19:45 alle zwei Minuten und startet das Spiel notfalls neu --
# einmal, nicht in einer Schleife: schnelle Neustartzyklen beschaedigen den Steam-Start.
set -u
cd "$(dirname "$0")/.."
LOG=data/runtime/overnight/ensure_forza.out
: > "$LOG"
say() { echo "$(date '+%H:%M:%S') $*" >> "$LOG"; }

forza_up() {
  powershell.exe -NoProfile -Command "
\$c = [pscredential]::new('.\admin', [Security.SecureString]::new())
try {
  \$s = New-PSSession -VMName 'ForzaScrapeVM' -Credential \$c -ErrorAction Stop
  \$r = Invoke-Command -Session \$s -ScriptBlock {
    if (Get-Process -Name 'forzahorizon6' -ErrorAction SilentlyContinue) { 'UP' } else { 'DOWN' } }
  Remove-PSSession \$s -ErrorAction SilentlyContinue
  \$r } catch { 'UNREACHABLE' }" 2>/dev/null | tr -d '\r\n '
}

say "warte bis 19:45"
while : ; do
  now=$(date +%H%M)
  [ "$now" -ge 1945 ] && break
  [ "$now" -lt 900 ] && break
  sleep 120
done

relaunched=0
say "Wachphase beginnt"
while : ; do
  now=$(date +%H%M)
  # bis der Sweep laeuft, hoechstens bis 20:20
  if [ "$now" -ge 2020 ]; then say "Wachphase beendet"; break; fi
  state=$(forza_up)
  if [ "$state" = "UP" ]; then
    say "  Forza laeuft"
  elif [ "$state" = "DOWN" ]; then
    if [ "$relaunched" -eq 0 ]; then
      say "  Forza ist TOT -- einmaliger Neustart"
      timeout 600 powershell.exe -NoProfile -ExecutionPolicy Bypass \
        -File scripts/start_vm_forza.ps1 -VMName ForzaScrapeVM >> "$LOG" 2>&1
      say "  Neustart exit $?"
      relaunched=1
    else
      say "  Forza wieder tot -- kein zweiter Neustart (Steam-Start nicht gefaehrden)"
    fi
  else
    say "  Gast nicht erreichbar ($state)"
  fi
  sleep 120
done
say "fertig, relaunched=$relaunched"
