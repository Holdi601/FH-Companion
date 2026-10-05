#!/usr/bin/env bash
# Warten, bis der Leaderboard-Dienst wieder antwortet, und dann weiterscannen.
#
#     nohup bash scripts/resume_when_service_back.sh &
#
# ## Wofuer
#
# Ein Serverfehler ist ein Rueckzugssignal, kein Wiederholungssignal: der Sweep gibt
# nach drei Anlaeufen geordnet auf, und danach steht alles still, bis jemand hinsieht.
# Nachts sieht niemand hin. Dieses Skript sieht statt dessen alle PAUSE Sekunden
# einmal auf den Bildschirm -- ein Standbild, kein Tastendruck, das kostet den Dienst
# nichts -- und faehrt die Arbeit wieder an, sobald der Dialog weg ist.
#
# ## Was es NICHT tut
#
# Es startet das Spiel nicht neu. Vier Neustarts in 25 Minuten haben am 2026-08-28
# den Steam-Startpfad zerlegt (siehe forza-launch-fragility). Der Dialog wird einmal
# mit ENTER weggeklickt, mehr nicht.
#
# ## Reihenfolge der Arbeit
#
# Jede Kategorie, in der noch etwas offen ist -- in der Reihenfolge der PLAN-Liste
# weiter unten, gezaehlt von scripts/open_boards.py. Fertige Kategorien werden
# uebersprungen, statt einen Durchgang ueber ein fertiges Feld zu kosten. Die Sweeps
# stellen ihre Kategorie selbst und weigern sich zu scannen, wenn sie unbestaetigt
# bleibt.
set -u
cd "$(dirname "$0")/.."

LOG=data/runtime/overnight/resume_watch.log
STOP=data/runtime/overnight/STOP
PAUSE=${PAUSE:-600}
MAX_ROUNDS=${MAX_ROUNDS:-30}          # 30 x 10 min = fuenf Stunden
SCREEN=data/runtime/current_vm_screen/current.ocr.txt

mkdir -p data/runtime/overnight
say() { echo "$(date '+%m-%d %H:%M:%S') $*" >> "$LOG"; }

look() {
  powershell.exe -NoProfile -ExecutionPolicy Bypass \
    -File scripts/capture_vm_current_screen.ps1 >/dev/null 2>&1
  tr '\n' ' ' < "$SCREEN" 2>/dev/null
}

say "=== Wecker an: warte auf den Dienst. Anhalten: touch $STOP ==="

for round in $(seq 1 "$MAX_ROUNDS"); do
  [ -e "$STOP" ] && { say "STOP gefunden -- Ende"; exit 0; }

  text="$(look)"
  if echo "$text" | grep -qi "server error"; then
    say "Runde $round/$MAX_ROUNDS: Dienst weiter weg"
    # Den Dialog einmal wegklicken, damit das Spiel nicht stundenlang darin steht --
    # ein haengender Dialog ueberlebt sonst bis zum naechsten Blick.
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/drive_vm_forza.ps1 \
      -Key ENTER -Count 1 -SettleMs 3000 >> "$LOG" 2>&1
    sleep "$PAUSE"
    continue
  fi

  if echo "$text" | grep -qi "initialisation failed"; then
    # Andere Baustelle: das ist die fehlende Xbox-Anmeldung, siehe
    # init-failed-means-xbox-login. Dagegen hilft kein Warten.
    say "Runde $round: INIT-FEHLER auf dem Schirm -- das ist die Anmeldung, nicht der"
    say "  Dienst. Ich hoere auf; das braucht einen Menschen an der VM-Konsole."
    exit 1
  fi

  say "Runde $round: kein Serverfehler mehr -- Arbeit wieder anfahren"

  # Die Arbeitsreihenfolge kommt aus den DATEN, nicht mehr aus dem Skript.
  # Bis 2026-09-01 standen hier fest Road Racing und danach Street Racing. An jenem
  # Tag waren beide fertig (161/161 und 105/105) und Cross-Country offen -- der
  # Wecker haette also zweimal ueber ein fertiges Feld gescannt und die einzige
  # offene Kategorie nie angefasst. scripts/open_boards.py zaehlt das Offene und
  # zaehlt dabei nur BENANNTE Strecken, sonst gilt Cross-Country ewig als offen.
  worked=0
  while IFS="|" read -r category routes progress; do
    [ -e "$STOP" ] && { say "STOP gefunden -- Ende"; exit 0; }
    n=$(python scripts/open_boards.py "$category" 2>/dev/null || echo 0)
    if [ "${n:-0}" -le 0 ]; then
      say "  $category uebersprungen -- nichts offen"
      continue
    fi
    say "  $category: $n offene(s) Board(s) -- anfahren"
    PROGRESS_FILE="$progress" bash scripts/category_sweep.sh "$category" "$routes" >> "$LOG" 2>&1
    say "    $category beendet (exit $?)"
    worked=1
  done <<'PLAN'
Road Racing|0-22|data/runtime/overnight/baseline20k_progress.json
Street Racing|0-14|data/runtime/overnight/street_racing_progress.json
Cross-Country|0-18|data/runtime/overnight/cross-country_progress.json
PLAN
  [ "$worked" -eq 0 ] && say "  nichts offen -- keine Kategorie hat noch Arbeit"
  exit 0
done

say "nach $MAX_ROUNDS Runden antwortet der Dienst immer noch nicht -- aufgegeben"
