#!/usr/bin/env bash
# Die beiden letzten offenen Kategorien in einer Nacht, nacheinander.
#
# Nacheinander und nicht parallel: es gibt EIN Spiel in EINER VM, und zwei Sweeps
# wuerden sich im selben Menue gegenseitig die Navigation zerlegen.
#
# FORZA_SITE_EVERY=0: kein Seitenaufbau zwischendurch. Der Aufbau dauert bei 504
# Boards rund drei Stunden -- alle fuenf Boards einer waere die ganze Nacht einer,
# parallel zur OCR, und genau dabei sind im August zweimal OCR-Arbeiter gestorben.
# Der Aufseher baut am Ende jedes Laufs ohnehin einmal.
set -u
cd "$(dirname "$0")/.."
export FORZA_SITE_EVERY=0
LOG=data/runtime/overnight/night_drag_touge.log
mkdir -p "$(dirname "$LOG")"

say() { echo "$(date '+%m-%d %H:%M:%S') $*" | tee -a "$LOG"; }

say "=== Nacht beginnt: Drag Racing (21 Boards), dann Touge (35) ==="
bash scripts/keep_sweeping.sh "Drag Racing" 0-2
say "Drag Racing beendet (Aufseher-Code $?)"

if [ -f data/runtime/overnight/STOP ]; then
  say "STOP-Datei liegt -- Touge wird nicht mehr angefangen."
  exit 0
fi

say "=== weiter mit Touge ==="
bash scripts/keep_sweeping.sh "Touge" 0-4
say "Touge beendet (Aufseher-Code $?)"
say "=== Nacht fertig ==="
