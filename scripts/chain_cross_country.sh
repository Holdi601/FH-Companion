#!/usr/bin/env bash
# Nach Touge weiter mit Cross-Country -- ohne dass jemand danebensteht.
#
# WARUM: am 2026-09-12 stellte sich heraus, dass Cross-Country nur 91 seiner 133
# Boards hat. Sechs Strecken (idx 05, 07, 08, 12, 15, 16) wurden NIE gescannt --
# keine verstreuten Luecken, sondern ganze Routen. Das ist der groesste offene
# Posten im ganzen Bestand, und er faellt in keiner Zusammenfassung auf, weil "91
# Boards" nach viel aussieht.
#
# Der Touge-Aufseher beendet sich von selbst, sobald dort nichts mehr offen ist.
# Genau dann -- und erst dann, wenn auch der letzte Sweep-Prozess weg ist -- wird
# hier der naechste aufgesetzt. Zwei Aufseher gleichzeitig waeren schlimmer als
# keiner: sie starten sich gegenseitig Sweeps in den Lauf (2026-09-05 passiert,
# 20 Minuten lang wurde gar nichts gescannt).
#
# Der Bereich 5-16 deckt die sechs Luecken ab; was dazwischen schon fertig ist,
# ueberspringt der Sweep in Sekunden ("already done on an earlier run").
#
#     bash scripts/chain_cross_country.sh
#
# Anhalten: data/runtime/overnight/STOP anlegen -- das gilt fuer beide Aufseher.
set -u
cd "$(dirname "$0")/.."

LOG=data/runtime/overnight/chain.log
STOP=data/runtime/overnight/STOP
say() { echo "$(date '+%m-%d %H:%M:%S') $*" >> "$LOG"; }

offene_touge() {
  python - <<'PY'
import json, pathlib
f = pathlib.Path("data/runtime/overnight/touge_progress.json")
paare = {tuple(x) for x in json.loads(f.read_text(encoding="utf-8"))["pairs"]}
klassen = ["D", "C", "B", "A", "S1", "S2", "R"]
print(sum(1 for i in range(5) for k in klassen if (i, k) not in paare))
PY
}

laeuft() {
  # Sowohl der Aufseher als auch der Sweep selbst zaehlen: nach dem letzten Board
  # laeuft noch der Katalogabgleich, und der gehoert nicht unterbrochen.
  ps -ef 2>/dev/null | grep -E "[k]eep_sweeping|[c]ategory_sweep|[o]cr_board_sweep" \
    | grep -v "Cross-Country" | wc -l
}

say "=== Kette an: wartet auf das Ende von Touge ==="
while true; do
  if [ -f "$STOP" ]; then
    say "STOP gefunden -- Kette beendet sich, Cross-Country wird nicht gestartet."
    exit 0
  fi
  offen=$(offene_touge 2>/dev/null || echo 99)
  aktiv=$(laeuft)
  if [ "$offen" = "0" ] && [ "$aktiv" = "0" ]; then
    break
  fi
  sleep 60
done

say "Touge ist vollstaendig und alle Sweeps sind aus -- Cross-Country 5-16 wird aufgesetzt"
nohup bash scripts/keep_sweeping.sh "Cross-Country" 5-16 \
  > data/runtime/overnight/keep_cross_country.out 2>&1 &
say "Aufseher gestartet (pid $!)"
