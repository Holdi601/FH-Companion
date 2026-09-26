#!/usr/bin/env bash
# Warten, bis Forza wieder online kommt -- und dann den Sweep von selbst anfahren.
#
# WOFUER: Am 2026-08-27 ab 19:20 scheiterte das Spiel reproduzierbar mit
# "INITIALISATION FAILED / Error Code E:O-e9-8", vier Mal mit jeweils NEUER
# Korrelations-ID, auch nach einem vollstaendigen VM-Neustart. Alles Lokale war in
# Ordnung (Build aktuell, Gast-UTC deckungsgleich, Netz und DNS erreichbar, Gaming
# Services laufen). Microsoft fuehrt E:0-e9 selbst als bekanntes Problem. Es ist also
# nichts zu reparieren, nur abzuwarten -- aber niemand soll dafuer alle zehn Minuten
# nachsehen muessen.
#
# WARUM NICHT NEU STARTEN: Vier Spielneustarts in 25 Minuten haben genau das
# ausgeloest, wovor forza-launch-fragility warnt -- der Steam-Startpfad gab auf
# (Task-Ergebnis 0x800710E0, kein Prozess mehr). Diese Schleife startet das Spiel
# darum NIE neu. Sie benutzt den Weg, den der Dialog selbst anbietet: ENTER auf
# "Return To Title", dann ENTER auf "Start Game". Kostet nichts und kann nichts
# zerlegen.
#
# WANN SIE AUFGIBT: nach MAX_TRIES Runden (Standard 24 = 4 Stunden). Ein Dienst, der
# vier Stunden weg ist, kommt nicht in der fuenften zurueck, weil jemand weiter
# ENTER drueckt.
set -u
cd "$(dirname "$0")/.."

LOG=data/runtime/overnight/wait_online.log
INTERVAL=${INTERVAL:-600}          # Sekunden zwischen zwei Versuchen
MAX_TRIES=${MAX_TRIES:-24}
STOP=data/runtime/overnight/STOP

# WAS wieder anfaehrt, wenn der Dienst zurueck ist. Die Vorgabe ist das alte
# Verhalten -- und das ist eine Falle: keep_sweeping.sh OHNE Argumente faellt auf
# "Road Racing" 10-22 zurueck und startet breadth_first_sweep.sh, also einen Lauf
# ueber ein laengst fertiges Feld. Wer in einer anderen Kategorie wartet, muss das
# hier setzen (2026-09-01 aufgefallen, Cross-Country):
#
#   RESUME_CMD='bash scripts/keep_sweeping.sh "Cross-Country" 0-18' \
#       bash scripts/wait_for_forza_online.sh
RESUME_CMD="${RESUME_CMD:-bash scripts/keep_sweeping.sh}"
SCREEN=data/runtime/current_vm_screen/current.ocr.txt

# Der Ausfalldialog -- und was AUSDRUECKLICH KEINER IST.
#
# "Horizon Life is currently unavailable" stand hier bis 2026-09-01 17:35 mit drin.
# Das war falsch und hat rund eine Stunde Scanzeit gekostet: Der NUTZER hat sich in
# die VM verbunden und eine Cross-Country-Bestenliste geoeffnet, waehrend ich noch
# auf das Ende des "Ausfalls" wartete. Das Standbild zeigte beides gleichzeitig --
# das Banner UND eine voll funktionierende Bestenliste mit Fahrern, Autos und Zeiten.
#
# Horizon Life ist die soziale Freeroam-Schicht. Ihre Stoerung sagt NICHTS ueber den
# Bestenlisten-Dienst. Das Banner bleibt ausserdem stehen, bis es jemand wegklickt --
# ich habe dasselbe stehende Bild acht Mal gelesen und jedes Mal fuer einen frischen
# Beweis gehalten.
#
# Regel daraus: ein STEHENDES Banner ist kein Beweis fuer einen laufenden Ausfall,
# sondern nur dafuer, dass es niemand quittiert hat. Hier stehen deshalb nur noch die
# beiden Texte, die das Spiel zeigt, wenn es einen Dienst WIRKLICH nicht erreicht.
# 2026-09-07 kam eine dritte Formulierung dazu: der Dialog "Server Error / There was
# an error communicating with the server." Ohne sie stufte der Warter ihn als
# "unklar" ein und wartete passiv, waehrend der Aufseher alle sechs Minuten einen
# neuen Sweep gegen den toten Dienst startete.
OUTAGE="Feature Locked|server is not available|error communicating with the server"

# Wie viele Runden in Folge der Ausfalltext WEG sein muss, bevor wieder gescannt wird.
# Nicht 1: direkt nach dem Wegklicken steht das Spiel wieder in der Welt und sieht
# "drin" aus, obwohl der Dienst noch tot sein kann -- und dann liefe der Sweep in
# genau den Serverfehler, vor dem server-error-means-stop warnt.
CLEAN_NEEDED=${CLEAN_NEEDED:-2}

mkdir -p data/runtime/overnight
say() { echo "$(date '+%m-%d %H:%M:%S') $*" >> "$LOG"; }

screen_text() {
  powershell.exe -NoProfile -ExecutionPolicy Bypass \
    -File scripts/capture_vm_current_screen.ps1 >/dev/null 2>&1
  tr '\n' ' ' < "$SCREEN" 2>/dev/null
}

press() {   # press ENTER, optionally repeating until a pattern shows up
  local until_pattern="${1:-}"
  if [ -n "$until_pattern" ]; then
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/drive_vm_forza.ps1 \
      -Key ENTER -RepeatUntilText "$until_pattern" -TimeoutSeconds 120 -SettleMs 4000 \
      >> "$LOG" 2>&1
  else
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/drive_vm_forza.ps1 \
      -Key ENTER -Count 1 -SettleMs 6000 >> "$LOG" 2>&1
  fi
}

say "=== warte auf Forza. Anhalten: touch $STOP ==="
clean=0

for try in $(seq 1 "$MAX_TRIES"); do
  [ -e "$STOP" ] && { say "STOP gefunden -- Ende"; exit 0; }

  text="$(screen_text)"
  if echo "$text" | grep -qiE "$OUTAGE"; then clean=0; else clean=$((clean + 1)); fi

  # Im Spiel UND der Ausfalltext seit CLEAN_NEEDED Runden weg?
  if echo "$text" | grep -qiE "Festival Playlist|Horizon Pulse|Close Map|CREATIVE HUB|Rivals" && [ "$clean" -ge "$CLEAN_NEEDED" ]; then
    say "Spiel ist drin -- Sweep wird gestartet"
    rm -f "$STOP"
    nohup bash -c "$RESUME_CMD" > /dev/null 2>&1 &
    say "Wiederanlauf gestartet (pid $!): $RESUME_CMD"
    exit 0
  fi

  if echo "$text" | grep -qiE "$OUTAGE"; then
    say "Versuch $try/$MAX_TRIES: DIENSTAUSFALL auf dem Schirm -- Dialog wegklicken, dann warten"
    press                       # der Dialog bietet OK/ENTER selbst an
  elif echo "$text" | grep -qi "INITIALISATION FAILED"; then
    say "Versuch $try/$MAX_TRIES: Init-Fehler steht -- Return To Title, dann Start Game"
    press                       # Return To Title
    press 'Start Game|Accessibility'
    press 'Continue|Festival Playlist|Horizon Pulse|LOADING|PLEASE WAIT'
  elif echo "$text" | grep -qiE "Start Game|Accessibility"; then
    say "Versuch $try/$MAX_TRIES: Titelbild -- Start Game"
    press 'Continue|Festival Playlist|Horizon Pulse|LOADING|PLEASE WAIT'
  else
    # Leerer oder unbekannter Schirm: NICHT hineindruecken. Waehrend eines Ladebilds
    # ist jeder Tastendruck bestenfalls nutzlos.
    say "Versuch $try/$MAX_TRIES: Schirm unklar (${text:0:60}) -- nur warten"
  fi

  # Direkt nachsehen, ob es diesmal durchkam, bevor die lange Pause beginnt.
  text="$(screen_text)"
  if echo "$text" | grep -qiE "Festival Playlist|Horizon Pulse|Close Map|CREATIVE HUB|Rivals" && [ "$clean" -ge "$CLEAN_NEEDED" ]; then
    say "durchgekommen -- Sweep wird gestartet"
    rm -f "$STOP"
    nohup bash -c "$RESUME_CMD" > /dev/null 2>&1 &
    say "Wiederanlauf gestartet (pid $!): $RESUME_CMD"
    exit 0
  fi

  sleep "$INTERVAL"
done

say "nach $MAX_TRIES Versuchen immer noch kein Online-Spiel -- aufgegeben"
