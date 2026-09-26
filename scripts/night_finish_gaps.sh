#!/usr/bin/env bash
# Die Nacht durcharbeiten: erst Touge fertig, dann die Cross-Country-Luecke.
#
# ZWEI VERSCHIEDENE BAUSTELLEN, und nur die erste ist Routine:
#
#   1. Touge idx04 R -- ein einzelnes Board, zurueckgestellt, weil meine
#      Abschaltkette es am 2026-09-12 bei 57 % abgewuergt hat.
#
#   2. Cross-Country: 42 Boards auf sechs Strecken, die NIE gescannt wurden.
#      Der Grund steht im Katalog: bei sechs Namen war die OCR-Einigkeit zu
#      gering, die Positionen blieben absichtlich leer, und ein leerer Eintrag
#      laesst den Sweep das Board ueberspringen -- lautlos, 42 mal.
#
# Fuer (2) werden die Namen zuerst NEU aufgenommen und gegen die Rekonstruktion
# aus den alten Lesungen gehalten. Eingetragen wird nur, was beide Quellen
# uebereinstimmend hergeben; alles andere bleibt leer und wartet auf den Morgen.
# Ein falscher Streckenname faellt beim Scannen niemandem auf -- und dann liegen
# 20.000 Zeilen unter der falschen Beschriftung.
#
# Anhalten: data/runtime/overnight/STOP anlegen.
set -u
cd "$(dirname "$0")/.."

LOG=data/runtime/overnight/nacht.log
STOP=data/runtime/overnight/STOP
say() { echo "$(date '+%m-%d %H:%M:%S') $*" | tee -a "$LOG"; }

SWEEPLOG_TOUGE=data/runtime/overnight/touge.out
SWEEPLOG_CC="data/runtime/overnight/cross-country.out"

warte_auf_sweep() {
  # Erkannt wird am PROTOKOLL, nicht an der Prozessliste: `ps` zeigt hier nur
  # "/usr/bin/bash" ohne Argumente, `pgrep -f` ebenso. Eine Abfrage, die immer
  # "nichts laeuft" antwortet, hat am 2026-09-12 eine VM mitten im Board
  # abgeschaltet.
  local datei="$1" marke="$2" grund=""
  while : ; do
    [ -f "$STOP" ] && { say "STOP gefunden"; return 1; }
    if grep -q "$marke" "$datei" 2>/dev/null; then
      grund="Endmarke gesetzt"; break
    fi
    local tot=$(( $(date +%s) - $(stat -c %Y "$datei" 2>/dev/null || echo 0) ))
    if [ "$tot" -gt 2400 ]; then grund="Protokoll steht seit $((tot/60)) min"; break; fi
    sleep 60
  done
  say "  $datei: $grund"
  return 0
}

offene_touge() {
  python - <<'PY'
import json, pathlib
f = pathlib.Path("data/runtime/overnight/touge_progress.json")
paare = {tuple(x) for x in json.loads(f.read_text(encoding="utf-8"))["pairs"]}
print(sum(1 for i in range(5) for k in ["D","C","B","A","S1","S2","R"]
          if (i, k) not in paare))
PY
}

say "=== Nachtlauf beginnt ==="
rm -f "$STOP"

# ---------------------------------------------------------------- #
# 1. Touge zu Ende bringen
# ---------------------------------------------------------------- #
if [ "$(offene_touge)" != "0" ]; then
  say "Touge: $(offene_touge) Board(s) offen -- Aufseher wird gestartet"
  : > "$SWEEPLOG_TOUGE"
  nohup bash scripts/keep_sweeping.sh "Touge" 0-4 \
    > data/runtime/overnight/keep_touge.out 2>&1 &
  warte_auf_sweep "$SWEEPLOG_TOUGE" "alle Klassen durch" || exit 0
  say "Touge fertig: $(offene_touge) Board(s) offen"
else
  say "Touge ist bereits vollstaendig"
fi

[ -f "$STOP" ] && { say "STOP -- Ende"; exit 0; }

# ---------------------------------------------------------------- #
# 2. Die sechs Cross-Country-Namen neu aufnehmen
# ---------------------------------------------------------------- #
# Die alten Lesungen sichern -- die Neuaufnahme ueberschreibt dieselbe Datei,
# und ohne die alte gibt es keine zweite Quelle zum Vergleichen.
cp -f config/route_names_cross_country.json config/route_names_cross_country_alt.json 2>/dev/null

say "Cross-Country: Streckennamen werden neu aufgenommen (19 Strecken)"
timeout 3600 python scripts/harvest_route_names.py "Cross-Country" 19 --shots 8 \
  >> data/runtime/overnight/cc_namen.out 2>&1
say "  Aufnahme beendet (Code $?)"

say "Cross-Country: Lesungen zusammenfuehren und vergleichen"
python scripts/confirm_cross_country_names.py --apply >> "$LOG" 2>&1
say "  Ergebnis siehe oben"

# ---------------------------------------------------------------- #
# 3. Was jetzt einen Namen hat, wird gescannt
# ---------------------------------------------------------------- #
[ -f "$STOP" ] && { say "STOP -- Ende"; exit 0; }
say "Cross-Country: Sweep fuer die Strecken 5-16"
: > "$SWEEPLOG_CC"
nohup bash scripts/keep_sweeping.sh "Cross-Country" 5-16 \
  > data/runtime/overnight/keep_cross_country.out 2>&1 &
warte_auf_sweep "$SWEEPLOG_CC" "alle Klassen durch" || exit 0

say "=== Nachtlauf fertig ==="
