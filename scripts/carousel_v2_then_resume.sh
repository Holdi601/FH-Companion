#!/usr/bin/env bash
# Zweiter Versuch. Der erste hat 30x rechts auf dem RIVALS-Schirm gedrueckt, also durch
# die Leistungsklassen -- dort aendert sich der Streckenname per Definition nie. Ursache
# war ein untaugliches Erkennungsmerkmal: "Route Length" steht auf beiden Schirmen.
# Jetzt wird auf die Ueberschrift "Routes" plus mehrere KM-Kacheln geprueft.
set -u
cd "$(dirname "$0")/.."
LOG=data/runtime/overnight/carousel_v2.out
: > "$LOG"
echo "Karussell v2 startet $(date '+%H:%M:%S')" >> "$LOG"
bash scripts/probe_carousel.sh >> "$LOG" 2>&1
echo "Karussell v2 fertig $(date '+%H:%M:%S') -- Scan weiter bis 17:00" >> "$LOG"
UNTIL_HOUR=17 nohup bash scripts/run_baseline_20k.sh >> "$LOG" 2>&1 &
echo "Scan gestartet pid $!" >> "$LOG"
