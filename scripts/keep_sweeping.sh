#!/usr/bin/env bash
# Aufseher fuer eine unbeaufsichtigte Nacht (angelegt 2026-08-27).
#
# WARUM ES IHN GIBT: breadth_first_sweep.sh laeuft die Klassen durch und gibt danach auf.
# Stirbt es -- Python-Absturz, Forza weg, VM neu -- passiert nichts mehr, und die Nacht
# steht still. Der Aufseher startet es dann neu; die Fortschrittsdatei sorgt dafuer, dass
# kein fertiges Board doppelt laeuft.
#
# UND ER HAELT AN, WO DAS RAHMENSKRIPT WEITERMACHT: gibt der Sweep drei Boards in Folge
# am Serverfehler auf, beendet er sich mit Code 0 -- und das Rahmenskript startet
# seelenruhig die naechste Klasse gegen denselben toten Dienst. Ein Serverfehler ist ein
# Zeichen zum Zurueckziehen, nicht zum Wiederholen. Der Aufseher killt den Lauf dann und
# wartet BACKOFF_MIN Minuten; beim dritten Mal hoert er ganz auf.
#
# Anhalten von aussen, jederzeit und ohne etwas zu suchen:
#     touch data/runtime/overnight/STOP
set -u
cd "$(dirname "$0")/.."

# SEIT 2026-08-30 bewacht er JEDE Kategorie, nicht mehr nur Road Racing 10-22:
#
#     bash scripts/keep_sweeping.sh "Street Racing" 0-14
#
# Ohne Argumente bleibt alles wie vorher. Das war noetig, weil der Aufseher sonst in
# einer Street-Racing-Nacht zwar lief, aber den falschen Lauf zaehlte (er suchte
# breadth_first_sweep.sh), die falsche Fortschrittsdatei las und im Notfall den
# falschen Sweep gestartet haette -- also Road Racing ueber ein fertiges Feld.
CATEGORY="${1:-Road Racing}"
ROUTES="${2:-10-22}"
ROUTE_LO="${ROUTES%%-*}"
ROUTE_HI="${ROUTES##*-}"
SLUG=$(echo "$CATEGORY" | tr 'A-Z ' 'a-z_')

LOG=data/runtime/overnight/supervisor.log
STOP=data/runtime/overnight/STOP
REQUEUE=data/runtime/overnight/REQUEUE.txt
if [ "$CATEGORY" = "Road Racing" ] && [ "$ROUTES" = "10-22" ]; then
  SWEEP_CMD="bash scripts/breadth_first_sweep.sh"
  BREADTH=data/runtime/overnight/breadth.out
else
  SWEEP_CMD="bash scripts/category_sweep.sh \"$CATEGORY\" $ROUTES"
  BREADTH="data/runtime/overnight/${SLUG}.out"
fi
# Road Racings Geschichte steht seit Wochen in baseline20k_progress.json -- dieselbe
# Ausnahme wie in category_sweep.sh, aus demselben Grund.
if [ "$CATEGORY" = "Road Racing" ]; then
  PROGRESS=data/runtime/overnight/baseline20k_progress.json
else
  PROGRESS="data/runtime/overnight/${SLUG}_progress.json"
fi
POLL=60                # Sekunden zwischen zwei Blicken
SETTLE=300             # nach einem Neustart so lange nicht nochmal anfassen
BACKOFF_MIN=45         # Rueckzug nach einem Serverfehler-Aufgeben
MAX_SERVER_GIVEUPS=3
MAX_RESTARTS=24

say() { echo "$(date '+%m-%d %H:%M:%S') $*" >> "$LOG"; }

# Nur den echten Lauf zaehlen. Die eigene Suche darf sich nicht selbst finden: ein
# `bash -c "... breadth_first_sweep ..."` traegt das Muster in seiner Kommandozeile.
sweep_running() {
  local n
  n=$(powershell.exe -NoProfile -Command "
@(Get-CimInstance Win32_Process -Filter \"Name='python.exe' OR Name='bash.exe'\" |
  Where-Object { \$_.CommandLine -and \$_.CommandLine -notmatch ' -c ' -and
                 (\$_.CommandLine -match 'ocr_board_sweep\.py' -or
                  \$_.CommandLine -match 'breadth_first_sweep\.sh' -or
                  \$_.CommandLine -match 'category_sweep\.sh') }).Count" 2>/dev/null | tr -dc '0-9')
  [ "${n:-0}" -gt 0 ] 2>/dev/null
}

kill_sweep() {
  powershell.exe -NoProfile -Command "
Get-CimInstance Win32_Process -Filter \"Name='python.exe' OR Name='bash.exe'\" |
  Where-Object { \$_.CommandLine -and \$_.CommandLine -notmatch ' -c ' -and
                 (\$_.CommandLine -match 'ocr_board_sweep\.py' -or
                  \$_.CommandLine -match 'breadth_first_sweep\.sh' -or
                  \$_.CommandLine -match 'category_sweep\.sh') } |
  ForEach-Object { Stop-Process -Id \$_.ProcessId -Force -ErrorAction SilentlyContinue }" >/dev/null 2>&1
}

# Zurueckgestellte Boards zaehlen mit. Sonst waere am Ende der Nacht "0 offen", der
# Aufseher wuerde sich beenden -- und die Nachscan-Liste, die NUR ein Neustart anwendet,
# bliebe fuer immer liegen. Genau das Board, das repariert werden soll, faellt sonst
# durchs Raster, weil es ja als erledigt vermerkt IST.
# Der Webserver ist beim harten Neustart am 27.08. um 02:06 mitgestorben und fiel erst
# neun Stunden spaeter auf: die Seite wurde die ganze Zeit gebaut, nur nicht ausgeliefert.
# Ein Blick auf den Port kostet nichts.
serve_alive() {
  local n
  n=$(powershell.exe -NoProfile -Command "
@(Get-CimInstance Win32_Process -Filter \"Name='python.exe'\" |
  Where-Object { \$_.CommandLine -match 'serve_analytics' }).Count" 2>/dev/null | tr -dc '0-9')
  [ "${n:-0}" -gt 0 ] 2>/dev/null
}

open_boards() {
  local pending=0
  [ -s "$REQUEUE" ] && pending=$(grep -c '[^[:space:]]' "$REQUEUE" 2>/dev/null || echo 0)
  python -c "
import json
d = json.load(open('$PROGRESS', encoding='utf-8-sig'))
done = {(int(i), c) for i, c in d['pairs']}
print(sum(1 for k in ['D','C','B','A','S1','S2','R'] for i in range($ROUTE_LO, $ROUTE_HI + 1) if (i, k) not in done) + $pending)
" 2>/dev/null || echo "?"
}

# NICHT im neuesten Detail-Log suchen: beim Aufgeben beendet sich der Python-Lauf, und
# das Rahmenskript startet binnen einer Sekunde die naechste Klasse mit einem NEUEN Log.
# Der Aufgeben-Satz stuende dann im alten, und der Aufseher haette ihn genau dann
# verpasst, wenn er zaehlt. breadth.out bekommt die Ausgabe aller Laeufe.
# Beide Aufgabe-Pruefungen lesen dieselbe Datei, und die ueberlebt den Lauf, der sie
# geschrieben hat. Am 2026-09-10 um 03:32 ging der Aufseher an, las EINE SEKUNDE spaeter
# den Aufgabe-Satz des um 03:11 gestorbenen Laufs, zaehlte ihn als frisch und legte sich
# 45 Minuten schlafen -- ohne je einen eigenen Lauf gestartet zu haben. Dreimal so, und
# er haette ganz aufgehoert, waehrend das Spiel bereit am Titelbildschirm stand.
#
# Frisch ist die Ausgabe nur, wenn DIESER Aufseher selbst einen Lauf gestartet hat und
# die Datei seitdem geschrieben wurde. category_sweep.sh leert sie beim Start
# (`: > "$LOG"`), ihr Inhalt gehoert danach also ausschliesslich zum laufenden Versuch.
sweep_started_at=0

fresh_log() {
  [ "$sweep_started_at" -gt 0 ] || return 1
  [ -f "$BREADTH" ] || return 1
  local written
  written=$(stat -c %Y "$BREADTH" 2>/dev/null || echo 0)
  # Echt neuer, nicht "gleich alt": `stat` kennt nur ganze Sekunden, und die Datei
  # des VORIGEN Laufs kann in derselben Sekunde liegen, in der dieser startet. Ein
  # echtes Aufgeben braucht Minuten, seine Zeit liegt also weit danach.
  [ "$written" -gt "$sweep_started_at" ]
}

server_gave_up() {
  fresh_log && tail -200 "$BREADTH" | grep -q "Boards in Folge am Serverfehler gescheitert"
}

# Der ZWEITE Grund, aus dem ein Lauf endet, ohne dass ein Neustart hilft.
#
# Am 2026-09-07 blockierte ein Server-Error-Dialog die Kategoriewahl. category_sweep.sh
# gibt dann nach fuenf Anlaeufen auf -- und der Aufseher startete SOFORT einen neuen
# Lauf, der an demselben Dialog scheiterte. Alle sechs Minuten, stundenlang. Seine
# 45-min-Bremse griff nicht, weil sie nur auf den Serverfehler-Satz hoert.
#
# Ein Neustart repariert eine blockierte Kategoriewahl nie schneller als eine Pause.
# Also dieselbe Behandlung: zuruecklehnen statt haemmern.
category_gave_up() {
  fresh_log && tail -200 "$BREADTH" | grep -q "liess sich nicht stellen -- ohne bestaetigte Kategorie"
}

start_sweep() {
  # Ein Neustart ist der einzige rennfreie Moment, um ein falsch vermerktes Board
  # zurueckzustellen: der Sweep haelt seinen Stand im Speicher und wuerde jeden Eingriff
  # beim naechsten fertigen Board ueberschreiben. Zeilen der Form "18 A".
  if [ -s "$REQUEUE" ]; then
    while read -r idx kl; do
      [ -z "${idx:-}" ] && continue
      say "  stelle idx$idx $kl zum Nachscannen zurueck"
      python scripts/requeue_board.py "$idx" "$kl" --force >> "$LOG" 2>&1
    done < "$REQUEUE"
    rm -f "$REQUEUE"
  fi
  # Das Rahmenskript leert breadth.out beim Start -- vorher wegsichern, sonst ist die
  # Geschichte der Nacht nach dem ersten Neustart weg.
  [ -s "$BREADTH" ] && cp "$BREADTH" "data/runtime/overnight/breadth_$(date '+%Y%m%d_%H%M%S').out"
  # NICHT `bash -c "$SWEEP_CMD"`: die Kommandozeile truege dann ' -c ', und genau das
  # filtert sweep_running heraus (damit die eigene Suche sich nicht selbst findet).
  # Der Aufseher saehe seinen eigenen Neustart nicht und startete endlos weiter.
  eval "nohup $SWEEP_CMD > /dev/null 2>&1 &"
  # Ab hier zaehlt, was in das Protokoll geschrieben wird -- vorher nicht.
  sweep_started_at=$(date +%s)
  say "Sweep neu gestartet (pid $!): $SWEEP_CMD"
}

say "=== Aufseher an. Anhalten mit: touch $STOP ==="
restarts=0
giveups=0
misses=0
last_action=0

while true; do
  if [ -f "$STOP" ]; then
    say "STOP gefunden -- Aufseher beendet sich. Der laufende Sweep bleibt unberuehrt."
    exit 0
  fi

  open=$(open_boards)
  if [ "$open" = "0" ]; then
    say "alle Boards erledigt -- Aufseher beendet sich."
    exit 0
  fi

  now=$(date +%s)

  if ! serve_alive; then
    say "Webserver ist weg -- neu gestartet"
    # --host 0.0.0.0 ist Absicht und muss hier stehen: die Voreinstellung bindet nur
    # 127.0.0.1, und ein Neustart des Aufsehers wuerde die Seite sonst still wieder
    # auf den eigenen Rechner einsperren. Am 27.08. genau so passiert.
    nohup python server/serve_analytics.py --host 0.0.0.0 > data/runtime/serve_analytics.out 2>&1 &
  fi

  if category_gave_up; then
    catfails=$((${catfails:-0} + 1))
    say "Kategorie liess sich nicht stellen ($catfails/3) -- Lauf wird beendet."
    kill_sweep
    if [ "$catfails" -ge 3 ]; then
      say "dreimal an der Kategoriewahl gescheitert -- das braucht einen Menschen. Aufseher hoert auf."
      exit 0
    fi
    say "  $BACKOFF_MIN min Rueckzug"
    sleep $((BACKOFF_MIN * 60))
    start_sweep
    restarts=$((restarts + 1)); last_action=$(date +%s); misses=0
    sleep "$SETTLE"
    continue
  fi

  if server_gave_up; then
    giveups=$((giveups + 1))
    say "Serverfehler-Aufgabe erkannt ($giveups/$MAX_SERVER_GIVEUPS) -- Lauf wird beendet."
    kill_sweep
    if [ "$giveups" -ge "$MAX_SERVER_GIVEUPS" ]; then
      say "dreimal am Serverfehler gescheitert -- der Dienst ist unten. Aufseher hoert auf."
      exit 0
    fi
    say "  $BACKOFF_MIN min Rueckzug"
    sleep $((BACKOFF_MIN * 60))
    start_sweep
    restarts=$((restarts + 1)); last_action=$(date +%s); misses=0
    sleep "$SETTLE"
    continue
  fi

  if sweep_running; then
    misses=0
  else
    misses=$((misses + 1))
    # Zwischen zwei Klassen liegt eine Luecke von rund einer Sekunde; erst der zweite
    # Fehlschlag in Folge ist ein toter Lauf.
    if [ "$misses" -ge 2 ] && [ $((now - last_action)) -ge "$SETTLE" ]; then
      if [ "$restarts" -ge "$MAX_RESTARTS" ]; then
        say "$MAX_RESTARTS Neustarts verbraucht -- hier stimmt etwas Grundsaetzliches. Aufseher hoert auf."
        exit 0
      fi
      say "kein Sweep mehr da, $open Board(s) offen -- Neustart $((restarts + 1))/$MAX_RESTARTS"
      start_sweep
      restarts=$((restarts + 1)); last_action=$(date +%s); misses=0
      sleep "$SETTLE"
      continue
    fi
  fi

  sleep "$POLL"
done
