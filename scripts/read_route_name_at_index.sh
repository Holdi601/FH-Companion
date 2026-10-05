#!/usr/bin/env bash
# Den Streckennamen an einer Position ABLESEN -- ueber den Weg, der nachweislich geht.
#
#     bash scripts/read_route_name_at_index.sh "Cross-Country" 19 5 7 8 12 15 16
#
# WARUM NICHT harvest_route_names.py: das liest den Titel der Streckenliste, der als
# LAUFSCHRIFT durch ein Fenster zieht -- daher die Rotationen und die Unsicherheit,
# die sechs Cross-Country-Namen seit dem 2026-08-31 aus dem Katalog heraushaelt. In
# der Nacht zum 2026-09-13 hat es zudem gar nicht erst navigiert und 19 mal dieselbe
# Bestenliste abgelesen ("QuavosMotorola ToyotaAE86FE ...").
#
# Der Navigator dagegen faehrt eine Position im Karussell an und prueft den Namen
# auf dem BOARD gegen den Katalog -- und schreibt dabei ins Protokoll, was er dort
# liest ("route index 3 confirmed on screen as 'Bandai Azuma'"). Das ist eine
# andere Stelle des Bildschirms, in ruhender Schrift statt als Laufband: ein
# unabhaengiger zweiter Blick auf denselben Namen.
set -u
cd "$(dirname "$0")/.."

KATEGORIE="${1:?Kategorie fehlt}"
ANZAHL="${2:?Streckenzahl fehlt}"
shift 2

# Der Anker ist eine Strecke, deren Name sicher ist -- von dort wird gezaehlt.
# Fuer Cross-Country nennt der Katalog "The Titan" (Index 0) ausdruecklich
# als solchen: in 12 von 12 Aufnahmen woertlich gleich gelesen.
ANKER="${ANKER:-The Titan}"
ANKERINDEX="${ANKERINDEX:-0}"

ZIEL=data/analytics/route_names_read.json
LOG=data/runtime/overnight/route_read.out
: > "$LOG"
echo "{" > "$ZIEL.teil"


# DIE KATEGORIE ZUERST -- mit dem Rahmenleser, nicht mit dem Navigator.
#
# Auf dem Kategorieschirm folgt die Markierung dem Mauszeiger NICHT (am
# 2026-09-13 gemessen: nach drei Schwebe-Versuchen stand der Rahmen unveraendert
# auf Road Racing). Der Navigator scheitert dort also zuverlaessig; der
# Rahmenleser faehrt die Kachel mit Pfeiltasten an und prueft nach jedem Schritt
# den gelben Rahmen im Bild.
#
# Steht die Kategorie einmal, beginnt jede folgende Navigation in der
# Streckenliste und muss sie gar nicht mehr stellen.
echo "$(date '+%H:%M:%S') Kategorie '$KATEGORIE' stellen (Rahmenleser)" | tee -a "$LOG"
python scripts/select_rivals_category.py "$KATEGORIE" >> "$LOG" 2>&1 || {
  echo "$(date '+%H:%M:%S') ABBRUCH: Kategorie nicht zu stellen" | tee -a "$LOG"
  exit 1
}
sleep 3

erster=1
for idx in "$@"; do
  echo "$(date '+%H:%M:%S') idx$idx: anfahren" | tee -a "$LOG"
  timeout 600 powershell.exe -NoProfile -ExecutionPolicy Bypass \
    -File scripts/start_forza_navigation.ps1 \
    -RivalsMode "$KATEGORIE" -RouteIndex "$idx" -RouteCount "$ANZAHL" \
    -RouteAnchor "$ANKER" -RouteAnchorIndex "$ANKERINDEX" \
    -TimeoutMinutes 6 >> "$LOG" 2>&1

  # Der Navigator legt seine Artefakte je Lauf ab; der juengste ist unserer.
  lauf=$(ls -t data/runtime/navigation 2>/dev/null | head -1)
  name=$(grep -oE "route index $idx confirmed on screen as '[^']+'" \
         "data/runtime/navigation/$lauf/navigator.log" 2>/dev/null \
         | tail -1 | sed "s/.*as '//; s/'$//")
  if [ -z "${name:-}" ]; then
    # Zweite Chance: die Zeile, die der Sweep schreibt, wenn er den Namen liest.
    name=$(grep -oE "confirmed route on the board: .*" \
           "data/runtime/navigation/$lauf/navigator.log" 2>/dev/null \
           | tail -1 | sed "s/.*board: //")
  fi
  echo "$(date '+%H:%M:%S') idx$idx: '${name:-unlesbar}'" | tee -a "$LOG"

  [ "$erster" = 1 ] || echo "," >> "$ZIEL.teil"
  erster=0
  printf '  "%s": %s' "$idx" "$(python -c "import json,sys;print(json.dumps(sys.argv[1]))" "${name:-}")" >> "$ZIEL.teil"
done

echo "" >> "$ZIEL.teil"
echo "}" >> "$ZIEL.teil"
mv "$ZIEL.teil" "$ZIEL"
echo "geschrieben: $ZIEL"
cat "$ZIEL"
