#!/usr/bin/env bash
# Wartet, bis der Street-Racing-Sweep fertig ist, und macht DANN den Rennen-Test.
#
#     nohup bash scripts/race_probe_after_sweep.sh > /dev/null 2>&1 &
#
# WARUM VERKETTET UND NICHT VON HAND: der Sweep ist gegen 01:40 durch, und dann
# schlaeft der Nutzer. Der Test braucht das Spiel exklusiv -- genau dann ist es frei,
# und genau dann ist auch nichts mehr zu verlieren, wenn die Rueckkehr aus dem Rennen
# schiefgeht. Also laeuft er von selbst an, statt bis zum naechsten Morgen zu warten.
#
# WAS ER PRUEFT, bevor er etwas anfasst:
#   * alle 105 Boards der Kategorie sind erledigt
#   * kein Sweep-Prozess laeuft mehr
# Beides muss stimmen. Ein Test, der in einen laufenden Sweep hineinfaehrt, wuerde
# dessen Board zerstoeren -- und der Sweep haette recht, nicht der Test.
#
# DIE KLASSE IST NICHT BELIEBIG: um ein Rivals-Rennen zu STARTEN, braucht es ein
# zugelassenes Auto. Im Spiel sitzt gerade ein S1-Wagen (TVR Griffith, 800). Auf einem
# R-Board fragte das Spiel erst nach einem anderen Auto, und der Test haette einen
# Auswahlschirm gemessen statt eines Rennens. Darum S1.
#
# Anhalten: touch data/runtime/overnight/RACE_PROBE_STOP
set -u
cd "$(dirname "$0")/.."

CATEGORY="${CATEGORY:-Street Racing}"
ROUTE_INDEX="${ROUTE_INDEX:-0}"
KLASS="${KLASS:-S1}"
EXPECTED_BOARDS="${EXPECTED_BOARDS:-105}"
PROGRESS=data/runtime/overnight/street_racing_progress.json
LOG=data/runtime/overnight/race_probe.log
STOP=data/runtime/overnight/RACE_PROBE_STOP
POLL=120

say() { echo "$(date '+%m-%d %H:%M:%S') $*" >> "$LOG"; }

boards_done() {
  python -c "
import json
try:
    d = json.load(open('$PROGRESS', encoding='utf-8-sig'))
    print(len(d.get('pairs', [])))
except Exception:
    print(-1)
" 2>/dev/null || echo -1
}

sweep_running() {
  local n
  n=$(powershell.exe -NoProfile -Command "
@(Get-CimInstance Win32_Process -Filter \"Name='python.exe' OR Name='bash.exe'\" |
  Where-Object { \$_.CommandLine -and \$_.CommandLine -notmatch ' -c ' -and
                 (\$_.CommandLine -match 'ocr_board_sweep\.py' -or
                  \$_.CommandLine -match 'category_sweep\.sh') }).Count" 2>/dev/null | tr -dc '0-9')
  [ "${n:-0}" -gt 0 ] 2>/dev/null
}

say "=== wartet auf das Ende von '$CATEGORY' ($EXPECTED_BOARDS Boards) ==="

while true; do
  [ -e "$STOP" ] && { say "STOP gefunden -- der Test faellt aus."; exit 0; }
  done_count=$(boards_done)
  if [ "$done_count" -ge "$EXPECTED_BOARDS" ] 2>/dev/null; then
    if sweep_running; then
      say "$done_count/$EXPECTED_BOARDS Boards, aber der Sweep raeumt noch auf -- warten"
    else
      say "$done_count/$EXPECTED_BOARDS Boards, kein Sweep mehr -- der Test kann laufen"
      break
    fi
  fi
  sleep "$POLL"
done

# Der Aufseher wuerde einen Sweep neu starten, sobald er keinen mehr sieht. Bei 0
# offenen Boards beendet er sich zwar von selbst, aber verlassen wird sich darauf
# nicht: ein STOP kostet nichts und schliesst das Rennen aus.
say "Aufseher anhalten"
touch data/runtime/overnight/STOP

say "navigiere zu idx$ROUTE_INDEX $KLASS in '$CATEGORY'"
# Der Python-Block liest sie aus der Umgebung (das Here-Doc ist bewusst
# gequotet, damit die Bash keine Anfuehrungszeichen darin anfasst) -- ohne
# export saehe er nur die Voreinstellungen und navigierte woanders hin.
export CATEGORY ROUTE_INDEX KLASS
python - <<'PYEOF' >> "$LOG" 2>&1
import os, sys
sys.path.insert(0, "scripts")
import ocr_board_sweep as sweep

category = os.environ.get("CATEGORY", "Street Racing")
index = int(os.environ.get("ROUTE_INDEX", "0"))
klass = os.environ.get("KLASS", "S1")
anchor = sweep.category_anchor(category)
print(f"anchor={anchor!r}")
# warm=True heisst "wir stehen vermutlich auf einer Bestenliste" -- nach einem
# fertigen Sweep stimmt das. Klappt es nicht, wird die andere Richtung versucht,
# genau wie im Sweep selbst.
reached, name, server_error = sweep.navigate(
    "ForzaScrapeVM", category, index, klass, anchor, True)
if not reached:
    print(f"erster Versuch fehlgeschlagen (server_error={server_error}); andere Richtung")
    reached, name, server_error = sweep.navigate(
        "ForzaScrapeVM", category, index, klass, anchor, False)
print(f"reached={reached} route={name!r} server_error={server_error}")
sys.exit(0 if reached else 1)
PYEOF
nav=$?

if [ "$nav" -ne 0 ]; then
  say "ABBRUCH: die Bestenliste war nicht erreichbar -- ohne sie kein Rivale, kein Rennen."
  exit 1
fi

say "Bestenliste steht -- Rennen-Test laeuft an"
timeout 1800 powershell.exe -NoProfile -ExecutionPolicy Bypass \
  -File scripts/probe_rival_race_tune.ps1 \
  -OutDir data/runtime/tune_probe_race >> "$LOG" 2>&1
say "  Rennen-Test beendet (exit $?)"

say "=== fertig. Ergebnis: data/runtime/tune_probe_race, Protokoll hier ==="
