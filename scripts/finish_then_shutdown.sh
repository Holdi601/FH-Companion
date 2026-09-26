#!/usr/bin/env bash
# Das laufende Board zu Ende bringen, dann die VM herunterfahren.
#
# WOFUER: der Nutzer will spielen. Eine laufende GPU-PV-VM ist dabei kein
# Schoenheitsfehler, sondern ein Risiko -- sechs Host-Abstuerze gehen auf ihr Konto,
# und dafuer genuegte auch eine verhakte, unbenutzte VM.
#
# Abgewartet wird BIS ZUM ENDE des Boards, nicht bis zum Ende des Sweeps: ein
# mittendrin abgebrochenes Board kostet die halbe Stunde, die es schon gelaufen ist,
# und hinterlaesst einen halben Datensatz. Danach verhindert die STOP-Datei, dass der
# Aufseher das naechste aufsetzt.
#
# Heruntergefahren wird HOEFLICH (Stop-VM ohne -Force): der Gast schliesst Forza
# selbst. Ein harter Stopp hat diese VM schon einmal im Zustand "Stopping" verhakt,
# und das war nur mit einem Host-Neustart zu loesen -- also wird hier lieber
# gewartet und im Zweifel gemeldet, statt nachzutreten.
set -u
cd "$(dirname "$0")/.."

LOG=data/runtime/overnight/shutdown.log
STOP=data/runtime/overnight/STOP
say() { echo "$(date '+%m-%d %H:%M:%S') $*" | tee -a "$LOG"; }

SWEEPLOG="${SWEEPLOG:-data/runtime/overnight/touge.out}"

# WARUM NICHT UEBER DIE PROZESSLISTE:
#
# In dieser Umgebung (Git-Bash unter Windows) zeigt `ps -ef` nur "/usr/bin/bash"
# OHNE Argumente, und `pgrep -f` ebenso -- beides am 2026-09-12 nachgemessen. Ein
# grep nach dem Skriptnamen findet deshalb NIE etwas, und eine Abfrage, die immer
# "nichts laeuft" antwortet, ist schlimmer als gar keine.
#
# Genau daran ist die erste Fassung dieses Skripts gescheitert: sie erklaerte den
# laufenden Sweep 30 Sekunden nach dem Start fuer beendet und schaltete die VM
# mitten im Board ab -- 19 Minuten Scan verloren, und das halbe Board wurde
# anschliessend als fertig verbucht.
#
# Beobachtbar ist dagegen das PROTOKOLL: der Sweep schreibt fortlaufend hinein und
# setzt am Ende eine eindeutige Marke.
ENDE_MARKE="alle Klassen durch"

sweep_fertig() {
  grep -q "$ENDE_MARKE" "$SWEEPLOG" 2>/dev/null
}

still_seit() {
  # Sekunden seit dem letzten Schreiben ins Protokoll.
  if [ ! -f "$SWEEPLOG" ]; then echo 99999; return; fi
  echo $(( $(date +%s) - $(stat -c %Y "$SWEEPLOG") ))
}

say "=== wartet auf das Ende des laufenden Boards, dann VM aus ==="

# Zuerst den Riegel: was jetzt noch laeuft, laeuft aus; Neues faengt nicht an.
touch "$STOP"
say "STOP gesetzt -- der Aufseher startet kein weiteres Board"

# Und warten, bis der Sweep von selbst fertig ist.
#
# Zwei Wege hinaus, und beide werden benannt: die Endmarke ist der gute Fall, ein
# lange totes Protokoll der schlechte. 20 Minuten Stille sind grosszuegig -- ein
# einzelner OCR-Block schweigt schon mal ein paar Minuten.
grund=""
while true; do
  if sweep_fertig; then
    grund="der Sweep hat seine Endmarke gesetzt"
    break
  fi
  tot=$(still_seit)
  if [ "$tot" -gt 1200 ]; then
    grund="das Protokoll steht seit $((tot / 60)) Minuten -- der Sweep ist tot"
    break
  fi
  sleep 30
done
say "Sweep zu Ende: $grund"

# Nach der Endmarke laeuft noch der Katalogabgleich; ihm eine Minute Ruhe geben,
# bevor der Gast unter ihm weggezogen wird.
sleep 60

say "fahre ForzaScrapeVM hoeflich herunter"
powershell.exe -NoProfile -Command "Stop-VM -Name 'ForzaScrapeVM' -ErrorAction SilentlyContinue" \
  >> "$LOG" 2>&1

for i in $(seq 1 24); do
  zustand=$(powershell.exe -NoProfile -Command \
    "(Get-VM -Name 'ForzaScrapeVM').State" 2>/dev/null | tr -d '\r\n ')
  say "  Zustand: ${zustand:-unbekannt} ($i/24)"
  [ "$zustand" = "Off" ] && break
  sleep 15
done

zustand=$(powershell.exe -NoProfile -Command \
  "(Get-VM -Name 'ForzaScrapeVM').State" 2>/dev/null | tr -d '\r\n ')
if [ "$zustand" = "Off" ]; then
  say "VM ist aus. Der Rechner gehoert wieder dem Spieler."
else
  say "ACHTUNG: VM steht auf '$zustand' und nicht auf 'Off'. NICHT hart stoppen --"
  say "  ein erzwungener Stopp hat sie schon einmal in 'Stopping' verhakt."
fi
