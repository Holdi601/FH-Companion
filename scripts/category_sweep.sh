#!/usr/bin/env bash
# Eine ganze Kategorie in die Breite scannen -- Klasse fuer Klasse ueber alle Strecken.
#
#     bash scripts/category_sweep.sh "Street Racing" 0-14
#     CLASSES="D C B" bash scripts/category_sweep.sh "Touge" 0-4
#
# Verallgemeinerung von breadth_first_sweep.sh, das auf Road Racing und die Strecken
# 10-22 festgeschrieben ist. Dieses hier bekommt die Kategorie als Argument.
#
# ## Zwei Dinge, die je Kategorie ANDERS sind
#
# 1. **Eigene Fortschrittsdatei.** Die Datei merkt sich Paare aus (Streckennummer,
#    Klasse) -- OHNE Kategorie. Mit derselben Datei waere "Street Racing Strecke 0"
#    fuer den Sweep dasselbe wie "Road Racing Strecke 0" und wuerde als erledigt
#    uebersprungen. Darum je Kategorie eine Datei.
# 2. **Der Kategoriewechsel passiert HIER, vor dem Sweep**, mit
#    scripts/select_rivals_category.py -- dem Weg, der den gelben Rahmen im Bild
#    prueft. Der Navigator im Gast kann den Wechsel nicht verlaesslich: blindes
#    Tastendruecken hat am 2026-08-28 dreimal Road Racing geoeffnet statt der
#    gewuenschten Kategorie. Innerhalb einer Kategorie braucht er den Schirm nicht,
#    er geht von Board zu Board ueber die Streckenliste.
#
# Anhalten: touch data/runtime/overnight/STOP
set -u
cd "$(dirname "$0")/.."

CATEGORY="${1:?Kategorie fehlt, z.B. \"Street Racing\"}"
ROUTES="${2:?Streckennummern fehlen, z.B. 0-14}"
CLASSES="${CLASSES:-D C B A S1 S2 R}"

SLUG=$(echo "$CATEGORY" | tr 'A-Z ' 'a-z_')
LOG="data/runtime/overnight/${SLUG}.out"
# Die Fortschrittsdatei ist ueberschreibbar, und fuer Road Racing MUSS sie es sein:
# dessen Geschichte steht seit Wochen in baseline20k_progress.json. Ohne diese Zeile
# legte der Slug eine leere road_racing_progress.json an, und 161 fertige Boards
# wuerden von vorn gescannt -- wochenlange Arbeit, unbemerkt doppelt.
if [ "$CATEGORY" = "Road Racing" ]; then
  DEFAULT_PROGRESS="data/runtime/overnight/baseline20k_progress.json"
else
  DEFAULT_PROGRESS="data/runtime/overnight/${SLUG}_progress.json"
fi
PROGRESS="${PROGRESS_FILE:-$DEFAULT_PROGRESS}"
STOP=data/runtime/overnight/STOP

mkdir -p data/runtime/overnight
: > "$LOG"
say() { echo "$(date '+%H:%M:%S') $*" >> "$LOG"; }

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

say "=== Kategorie '$CATEGORY', Strecken $ROUTES, Klassen $CLASSES ==="

# Ein gekillter Lauf laesst Gast-Tasks zurueck, die weiter Tasten druecken.
say "Gast-Tasks aufraeumen"
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

# "Prozess da" ist NICHT "Spiel bereit". Am 2026-08-29 um 15:13 meldete forza_state
# UP, sobald forzahorizon6.exe existierte -- das Spiel lud aber noch, und der
# Navigator sah acht Minuten lang den Windows-Desktop (Papierkorb, Taskleiste) und
# suchte darin eine Strecke. Siehe forza-window-handle-is-zero: ein ladendes Spiel
# sieht aus wie ein totes, und beide Male hilft nur, auf ein echtes Merkmal zu warten.
#
# DIE SCHWELLE WAR FALSCH. Sie stand auf "ueber 3 GB", gemessen wurde aber nie: die
# Anmeldung in diesem Block war kaputt (ein eingedampfter Backslash machte aus
# '.\admin' ein '.'+BEL+'dmin'), der catch lieferte immer 0, und der Sweep wartete
# stumpf zehn Minuten. Am 2026-08-29 17:40, mit reparierter Anmeldung, stand das
# Spiel FERTIG im Hauptmenue -- bei 1677 MB. Die 3 GB waeren nie gekommen.
# Darum jetzt in MB, mit einer Schwelle unter dem gemessenen Hauptmenue-Wert.
for wait_round in $(seq 1 20); do
  ram=$(powershell.exe -NoProfile -Command "
\$c = [pscredential]::new('.\admin', [Security.SecureString]::new())
try {
  \$s = New-PSSession -VMName 'ForzaScrapeVM' -Credential \$c -ErrorAction Stop
  \$r = Invoke-Command -Session \$s -ScriptBlock {
    \$p = Get-Process forzahorizon6 -ErrorAction SilentlyContinue
    if (\$p) { [int](\$p.WorkingSet64 / 1MB) } else { 0 } }
  Remove-PSSession \$s -ErrorAction SilentlyContinue
  \$r } catch { 0 }" 2>/dev/null | tr -dc '0-9')
  [ "${ram:-0}" -ge 1400 ] 2>/dev/null && { say "Spiel geladen (${ram} MB)"; break; }
  say "  Spiel laedt noch (${ram:-0} MB), warte ($wait_round/20)"
  sleep 30
done

# Die Kategorie EINMAL stellen, bevor Boards laufen.
#
# WARUM NICHT DER NAVIGATOR: er kann den Kategorieschirm nicht verlaesslich bedienen
# (die Markierung ist ein gelber Rahmen, der in keinem OCR-Text steht -- siehe
# select_rivals_category.py). Er PRUEFT die geoeffnete Kategorie am Wahrzeichen und
# scheitert sauber, wenn sie falsch ist. Genau das passierte am 2026-08-28 um 16:46:
# nach einem kalten Spielstart stand das Spiel noch in Cross-Country, und jedes Board
# fiel mit "Could not find the anchor route 'Daikoku Chase'" aus. Kein Datenschaden,
# aber eine Minute je Board fuer nichts.
#
# Also: ein Navigationsversuch, um ueberhaupt in den Rivals-Baum zu kommen (er darf
# scheitern), dann mit ESC zum Kategorieschirm und dort per Sichtpruefung waehlen.
if [ "${SKIP_CATEGORY:-0}" = "1" ]; then
  say "Kategorie stellen uebersprungen (SKIP_CATEGORY=1) -- sie steht bereits"
else
say "Kategorie stellen"
timeout 480 powershell.exe -NoProfile -ExecutionPolicy Bypass   -File scripts/start_forza_navigation.ps1 -RivalsMode "$CATEGORY"   -TimeoutMinutes 6 >> "$LOG" 2>&1 || say "  erster Anlauf gescheitert (erwartet)"

picked=0
for attempt in 1 2 3 4 5; do
  if python scripts/select_rivals_category.py --where >> "$LOG" 2>&1; then
    if python scripts/select_rivals_category.py "$CATEGORY" >> "$LOG" 2>&1; then
      say "  Kategorie '$CATEGORY' gestellt und im Bild bestaetigt"
      picked=1
      break
    fi
  fi
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/drive_vm_forza.ps1     -Key ESC -Count 1 -SettleMs 3500 >> "$LOG" 2>&1
done
if [ "$picked" -eq 0 ]; then
  say "ABBRUCH: '$CATEGORY' liess sich nicht stellen -- ohne bestaetigte Kategorie"
  say "         wird nicht gescannt, sonst laufen Zeilen unter falscher Beschriftung."
  exit 1
fi
fi

for klass in $CLASSES; do
  [ -e "$STOP" ] && { say "STOP gefunden -- Ende"; exit 0; }
  say "=== Durchgang Klasse $klass ==="
  timeout 43200 python scripts/ocr_board_sweep.py \
    --category "$CATEGORY" \
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
try:
    d = json.load(open('$PROGRESS', encoding='utf-8-sig'))
    print(len(d.get('pairs', [])), 'Boards erledigt')
except Exception as e:
    print('Fortschritt nicht lesbar:', e)" 2>/dev/null)"
done

# DIE NAMENSZUORDNUNG AUSLOESEN -- sonst tut es niemand.
#
# `join_ocr_names_to_car_ids.py` gibt den car_ids aus dem Speicher einen Namen,
# indem es sie ueber die Rundenzeit mit den vom Schirm gelesenen Namen paart. Es
# ist als Schritt vollstaendig und laeuft trotzdem nie: zwischen dem 2026-08-28 und
# dem 2026-09-14 stand die Datei unberuehrt, waehrend darunter Dutzende Boards
# dazukamen. Genau dasselbe war schon am 2026-08-24 aufgefallen ("42 Scans lang
# nicht gelaufen") -- eine Erkenntnis allein aendert nichts, ein Aufruf schon.
#
# Es geht um mehr als Beschriftung: solange ein car_id namenlos ist, laeuft dasselbe
# Auto als Speicher-Id UND als Schirm-Name in der Wertung mit, tritt gegen sich
# selbst an und blaeht die Feldgroesse auf, aus der die Punkte berechnet werden.
#
# NACH den Klassen und nicht nach jedem Board: der Lauf dauert rund zehn Minuten und
# waere je Board ein Aufschlag von fast hundert Prozent.
say "Namen den Speicher-Ids zuordnen"
timeout 3600 python scripts/join_ocr_names_to_car_ids.py >> "$LOG" 2>&1   || say "  Zuordnung gescheitert -- die Boards sind davon unberuehrt"

say "=== alle Klassen durch ==="

# AUSLIEFERN -- sonst ist es keine Pipeline, sondern ein Bauplatz.
#
# Der Seitenaufbau laeuft nach jedem Board und erzeugt Seite UND Haptik-Paket. Auf
# den GNAS kam beides bisher nur von Hand. Ein Datensatz, der eine Woche alt ist,
# waehrend hier taeglich gescannt wird, ist fuer jeden ausser dem Scanner wertlos.
#
# NACH den Klassen und nicht nach jedem Board: es gehen rund 95 MB ueber die
# Leitung (Seite 27 MB, Paket 66 MB), und zwischen zwei Boards will der Scan die
# Bandbreite selbst.
#
# --kein-bau, weil der Seitenaufbau eben erst gelaufen ist; ein zweiter waere eine
# Minute Rechenzeit fuer dasselbe Ergebnis. Das Ausliefern holt ausserdem, was
# drueben entstanden ist (Fremdbeitraege, der Sichtbarkeitsschalter) -- das ist
# beabsichtigt und der Grund, warum es nicht nur schickt.
say "auf den GNAS ausliefern"
timeout 3600 python scripts/deploy_gnas.py --apply --kein-bau >> "$LOG" 2>&1 || say "  Ausliefern gescheitert -- die Daten hier sind davon unberuehrt"
