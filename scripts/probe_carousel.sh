#!/usr/bin/env bash
# Das Streckenkarussell EINMAL KOMPLETT abgehen und jede Position lesen.
#
# Der Screen zeigt den Namen nur fuer die markierte Strecke; die anderen Tiles tragen
# bloss ihre Laenge. Also: ein Schritt nach rechts, ein Bild, Namen lesen -- 30 Schritte,
# damit sich die Liste sicher einmal schliesst (das Spielmenue nennt 23 Routen, der
# Operator sagt, es kam kuerzlich eine dazu).
#
# Das ergibt in einem Durchlauf: ob die Eingabe ueberhaupt ankommt, die vollstaendige
# Liste aus dem Spiel selbst, und die echte Anzahl. Eine frueher gelaufene Sequenz
# drueckte 65x ohne Bewegung -- ob Anzeige- oder Eingabefrage, war nie geklaert.
set -u
cd "$(dirname "$0")/.."
OUT=data/runtime/carousel_probe
LOG=$OUT/probe.log
STEPS=${STEPS:-30}
rm -rf "$OUT"; mkdir -p "$OUT"
: > "$LOG"

shot() {
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/capture_vm_current_screen.ps1 \
    -VMName ForzaScrapeVM -OutputDir "$OUT/$1" > /dev/null 2>&1
  local txt="$OUT/$1/current.ocr.txt"
  local name="(kein OCR)" km=""
  if [ -f "$txt" ]; then
    name=$(grep -iE "circuit|sprint|colossus|goliath" "$txt" | head -1 | tr -d '\r')
    km=$(grep -oE "Route Length:?[[:space:]]*[0-9.]+" "$txt" | head -1 | grep -oE "[0-9.]+$")
    [ -z "$name" ] && name="(kein Streckenname im Bild)"
  fi
  printf "%-14s %-6s %s\n" "$1" "${km:-?}" "$name" >> "$LOG"
}

keys() {
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/send_vm_forza_keys.ps1 \
    -VMName ForzaScrapeVM -Key "$1" -Count "${2:-1}" -DelayMs 300 -SettleMs 1100 > /dev/null 2>&1
}

echo "== Ausgangslage ==" >> "$LOG"
shot 00_start
# Zurueck aus der Rangliste in die Streckenauswahl. NICHT blind: pruefen, ob wirklich
# der Routes-Screen erreicht ist, sonst laufen 30 Bilder ins Leere. Erkennungsmerkmal
# ist "Route Length", das nur dort steht.
# "Route Length" steht AUCH auf dem Rivals-Schirm, wo die Kacheln LEISTUNGSKLASSEN
# sind (S2 900, R 998) und nicht Strecken. Daran war der Streckenschirm nicht zu
# erkennen -- ein Durchlauf am 2026-08-24 hat deshalb 30x rechts durch die Klassen
# gedrueckt, wo sich der Streckenname per Definition nie aendert.
# Unterscheidungsmerkmal: Ueberschrift "Routes" UND mehrere Kacheln mit "N.N KM".
on_routes() {
  local t="$OUT/$1/current.ocr.txt"
  [ -f "$t" ] || return 1
  grep -qiE "^[[:space:]]*Routes[[:space:]]*$" "$t" || return 1
  [ "$(grep -coE "[0-9]+\.[0-9]+ ?KM" "$t")" -ge 2 ] || return 1
  return 0
}
reached=""
for step in 1 2 3 4 5; do
  keys ESC 1
  shot "$(printf '0%d_esc' "$step")"
  if on_routes "$(printf '0%d_esc' "$step")"; then
    echo "Routes-Screen nach $step x ESC erreicht" >> "$LOG"
    reached=yes
    break
  fi
done
if [ -z "$reached" ]; then
  echo "ABBRUCH: Routes-Screen nach 5 x ESC nicht erreicht -- keine 30 Bilder verschwenden" >> "$LOG"
  echo "FERTIG $(date '+%H:%M:%S')" >> "$LOG"
  exit 0
fi

echo "== Karussell nach rechts, $STEPS Schritte ==" >> "$LOG"
for i in $(seq 1 "$STEPS"); do
  keys RIGHT 1
  shot "$(printf 'r%02d' "$i")"
done

echo "== Eingabediagnose (sagt, ob Tasten ueberhaupt ankommen) ==" >> "$LOG"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/diagnose_vm_forza_input.ps1 \
  -VMName ForzaScrapeVM -Key DOWN >> "$LOG" 2>&1

echo "== distinkte Streckennamen ==" >> "$LOG"
grep -oE "(Highway|Narai|Shirakawa|Shimanoyama|Hokubu|Soni|Daikoku|Edamame|Irokawa|Electric|Legend|Tateyama|Tokyo|Seaside|Shikisai|Festival|Venus|Coastline|Satta|Ito|Colossus|Goliath)[A-Za-z -]*" "$LOG" \
  | sort -u >> "$LOG"
echo "FERTIG $(date '+%H:%M:%S')" >> "$LOG"
