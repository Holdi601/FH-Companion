#!/usr/bin/env bash
# Den Cross-Country-Sweep ein zweites Mal fahren -- fuer die Klassen, die leer liefen.
#
#     nohup bash scripts/cc_second_pass.sh > /dev/null 2>&1 &
#
# ## Warum das noetig ist
#
# Der Lauf vom 2026-09-13 um 17:19 startete, als die sechs Cross-Country-Positionen
# noch KEINEN Namen hatten. Die Durchgaenge D, C, B, A und S1 haben darum jede
# Position angefahren, den Namen gelesen und das Board VERWORFEN -- null Boards in
# fuenf Durchgaengen. Genau diese Lesungen haben dann die Namen ergeben
# (scripts/name_cross_country_routes.py), die um 17:51 gesetzt wurden.
#
# Ab S2 scannt derselbe Lauf also wirklich. Ohne diesen zweiten Durchgang blieben
# 5 Strecken x 5 Klassen = 30 Boards offen, obwohl der Rechner die ganze Nacht
# laeuft.
#
# ## Warum nichts doppelt gescannt wird
#
# Die Fortschrittsdatei merkt sich Paare aus (Streckennummer, Klasse). Was S2 und R
# gerade erledigen, ueberspringt der zweite Lauf von selbst. Darum werden hier auch
# einfach ALLE Klassen angegeben statt einer Auswahl -- die Datei entscheidet, und
# eine Liste, die ich von Hand pflege, waere die zweite Stelle, an der dieselbe
# Entscheidung faellt.
#
# ## Warum gewartet und nicht gleich gestartet
#
# Zwei Sweeps gleichzeitig hiessen zwei Prozesse, die dieselbe VM fernsteuern. Der
# erste Lauf schreibt als letztes "=== alle Klassen durch ===" -- darauf wird
# gewartet. Sein Protokoll wird vorher beiseitegelegt, denn der zweite Lauf leert es.
set -u
cd "$(dirname "$0")/.."

LOG=data/runtime/overnight/cross-country.out
MARKE="=== alle Klassen durch ==="
EIGEN=data/runtime/overnight/cc_second_pass.out
: > "$EIGEN"
sagen() { echo "$(date '+%Y-%m-%d %H:%M:%S') $*" >> "$EIGEN"; }

sagen "warte darauf, dass der erste Lauf fertig wird"
while ! grep -q "$MARKE" "$LOG" 2>/dev/null; do
  if [ -e data/runtime/overnight/STOP ]; then
    sagen "STOP gefunden -- der zweite Lauf startet nicht"
    exit 0
  fi
  sleep 60
done

# Das Protokoll des ersten Laufs sichern: die Namenslesungen darin sind der Beleg
# fuer die sechs neu gesetzten Streckennamen, und der zweite Lauf leert die Datei.
stempel=$(date '+%Y%m%d_%H%M%S')
cp "$LOG" "data/runtime/overnight/cross-country_${stempel}_erster_lauf.out" 2>/dev/null
sagen "erster Lauf fertig; Protokoll gesichert als cross-country_${stempel}_erster_lauf.out"

sagen "zweiter Lauf startet -- alle Klassen, erledigte Paare werden uebersprungen"
bash scripts/category_sweep.sh "Cross-Country" 5,7,8,12,15,16 >> "$EIGEN" 2>&1
sagen "zweiter Lauf beendet (exit $?)"
