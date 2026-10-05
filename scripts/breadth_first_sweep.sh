#!/usr/bin/env bash
# Nacht 26.08.2026: die restlichen Strecken in die Breite scannen, nicht in die Tiefe.
#
# WARUM DIESE REIHENFOLGE: offen sind 89 Boards, das sind bei gemessenen ~18 Minuten je
# Board rund 27 Stunden. Zur Verfuegung stehen bis morgen Abend etwa 15. Es passt also
# knapp die Haelfte, und dann entscheidet die Reihenfolge, WAS fehlt:
#
#   Strecke fuer Strecke (bisher): vier weitere Strecken vollstaendig, neun ueberhaupt nicht
#   Klasse fuer Klasse (hier):     alle 23 Strecken vertreten, die oberen Klassen fehlen
#
# Der Auftrag lautet "sodass wir bald alle 23 Strecken haben", also gewinnt die Breite.
# Nach dem ersten Durchgang (Klasse D, ~3 Stunden) hat JEDE Strecke Daten; danach fuellen
# die weiteren Durchgaenge die Klassen auf, in der Reihenfolge D C B A S1 S2 R.
#
# D zuerst und nicht A: die kleinen Klassen sind schnell durch (10-15 min statt 25), also
# ist die Breite frueher erreicht. Dass A und S1 die umkaempftesten Klassen sind, spricht
# dafuer sie SPAETER zu holen -- sie sind die teuersten und wuerden die Breite verzoegern.
#
# Die Fortschrittsdatei ist dieselbe wie bisher, damit die 72 fertigen Boards nicht erneut
# laufen und ein Abbruch an derselben Stelle weitermacht.
set -u
cd "$(dirname "$0")/.."
LOG=data/runtime/overnight/breadth.out
mkdir -p data/runtime/overnight
: > "$LOG"
say() { echo "$(date '+%H:%M:%S') $*" >> "$LOG"; }

PROGRESS=data/runtime/overnight/baseline20k_progress.json
ROUTES=10-22

forza_state() {
  powershell.exe -NoProfile -Command "
\$c = [pscredential]::new('.\admin', [Security.SecureString]::new())
try {
  \$s = New-PSSession -VMName 'ForzaScrapeVM' -Credential \$c -ErrorAction Stop
  \$r = Invoke-Command -Session \$s -ScriptBlock {
    if (Get-Process -Name 'forzahorizon6' -ErrorAction SilentlyContinue) { 'UP' } else { 'DOWN' } }
  Remove-PSSession \$s -ErrorAction SilentlyContinue
  \$r } catch { 'UNREACHABLE' }" 2>/dev/null | tr -d '\r\n '
}

# Ein gekillter Lauf laesst Gast-Tasks zurueck, die weiter Tasten druecken -- die naechste
# Navigation scheitert dann als "Could not find the anchor route", was wie ein
# Karussellfehler aussieht und in die falsche Richtung weist.
say "=== Gast-Tasks aufraeumen ==="
powershell.exe -NoProfile -Command "
\$c = [pscredential]::new('.\admin', [Security.SecureString]::new())
try {
  \$s = New-PSSession -VMName 'ForzaScrapeVM' -Credential \$c -ErrorAction Stop
  Invoke-Command -Session \$s -ScriptBlock {
    Get-ScheduledTask -ErrorAction SilentlyContinue |
      Where-Object { \$_.TaskName -like 'Chunk*' -or \$_.TaskName -like 'Cap*' } |
      ForEach-Object { Stop-ScheduledTask -TaskName \$_.TaskName -ErrorAction SilentlyContinue
                       Unregister-ScheduledTask -TaskName \$_.TaskName -Confirm:\$false -ErrorAction SilentlyContinue } }
  Remove-PSSession \$s -ErrorAction SilentlyContinue } catch {}" >> "$LOG" 2>&1

say "Forza: $(forza_state)"
if [ "$(forza_state)" != "UP" ]; then
  say "starte Forza"
  timeout 1200 powershell.exe -NoProfile -ExecutionPolicy Bypass \
    -File scripts/start_vm_forza.ps1 -VMName ForzaScrapeVM >> "$LOG" 2>&1
  for i in 1 2 3 4 5 6 7 8 9 10; do
    [ "$(forza_state)" = "UP" ] && break
    say "  warte auf Forza ($i/10)"; sleep 60
  done
fi
[ "$(forza_state)" = "UP" ] || { say "ABBRUCH: Forza laeuft nicht"; exit 1; }
say "Forza laeuft"

for klass in D C B A S1 S2 R; do
  say "=== Durchgang Klasse $klass ueber Strecken $ROUTES ==="
  # --until-hour 20 heisst faktisch: keine Zeitsperre. Die Regel im Sweep lautet
  # "hour >= until_hour UND hour < 20", das kann mit 20 nie zutreffen. Beendet wird von
  # aussen, wenn der Rechner gebraucht wird.
  timeout 43200 python scripts/ocr_board_sweep.py \
    --route-indices "$ROUTES" \
    --classes "$klass" \
    --row-cap 20000 \
    --chunk-seconds 120 \
    --max-chunks 12 \
    --until-hour 20 \
    --progress-file "$PROGRESS" >> "$LOG" 2>&1
  say "  Klasse $klass beendet (exit $?)"
  say "  Stand: $(python -c "
import json
d = json.load(open('$PROGRESS', encoding='utf-8-sig'))
print(len(d.get('pairs', [])), 'Boards erledigt')" 2>/dev/null)"
done

say "=== alle Durchgaenge durch -- Seite bauen ==="
timeout 2400 python scripts/build_analytics_site.py >> "$LOG" 2>&1
say "  Aufbau exit $?"
say "fertig"
