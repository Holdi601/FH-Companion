#!/usr/bin/env bash
# Wiederanlauf nach einem Ausfall des Leaderboard-Dienstes.
#
# Am 2026-08-26 ab 10:31 antwortete der Dienst nicht mehr ("Server Error / There was an
# error communicating with the server"). Ein Serverfehler ist ein Rueckzugssignal, kein
# Grund zum Weiterprobieren -- aber auch keiner, die Arbeit fuer den Rest des Tages
# liegen zu lassen. Also: warten, neu ansetzen, und wenn es wieder am Dienst scheitert,
# laenger warten.
#
# Der Sweep selbst gibt nach drei Boards in Folge mit Serverfehler geordnet auf
# (SERVER_ERROR_GIVE_UP). Ein Lauf, der nach wenigen Minuten zurueckkommt, ist also das
# Zeichen "immer noch weg" -- daran wird hier unterschieden, nicht an einer Vermutung.
set -u
cd "$(dirname "$0")/.."
LOG=data/runtime/overnight/resume.out
mkdir -p data/runtime/overnight
: > "$LOG"
say() { echo "$(date '+%H:%M:%S') $*" >> "$LOG"; }

PROGRESS=data/runtime/overnight/baseline20k_progress.json
boards_done() {
  python -c "
import json
try:
    print(len(json.load(open('$PROGRESS', encoding='utf-8-sig')).get('pairs', [])))
except Exception:
    print(0)" 2>/dev/null
}

WAIT=1200          # erste Pause: 20 Minuten
for attempt in 1 2 3 4 5 6 7 8; do
  say "Versuch $attempt: warte $((WAIT / 60)) min auf den Dienst"
  sleep "$WAIT"
  before=$(boards_done)
  say "  starte den Breitensweep (Stand: $before Boards)"
  start=$(date +%s)
  bash scripts/breadth_first_sweep.sh >> "$LOG" 2>&1
  spent=$(( $(date +%s) - start ))
  after=$(boards_done)
  say "  zurueck nach $((spent / 60)) min, Stand jetzt $after Boards (+$((after - before)))"
  if [ "$after" -ge 156 ]; then
    say "alle Boards erledigt"; break
  fi
  if [ "$after" -gt "$before" ]; then
    # Es ging voran -- der Dienst war also da. Kommt der Lauf trotzdem zurueck, war es
    # ein anderer Grund; kurz warten und weitermachen, nicht lange pausieren.
    WAIT=600
    say "  es ging voran, kurze Pause und weiter"
  else
    # Kein einziges Board -- der Dienst ist weiterhin weg. Pause verdoppeln, maximal
    # eine Stunde, damit nicht gegen etwas geklopft wird, das nicht antwortet.
    WAIT=$(( WAIT * 2 )); [ "$WAIT" -gt 3600 ] && WAIT=3600
    say "  kein Fortschritt -- naechste Pause $((WAIT / 60)) min"
  fi
done
say "fertig"
