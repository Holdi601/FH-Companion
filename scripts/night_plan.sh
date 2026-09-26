#!/usr/bin/env bash
# Nachtplan, unbeaufsichtigt. Zwei Phasen mit fester Zeitteilung.
#
# PHASE 1 (bis zu 3 Stunden): SPEICHER-LAEUFE auf den Boards mit unbenannten Autos.
#   Die car_id -> Name Zuordnung entsteht, indem Speicher-Zeilen (car_id, kein Name) und
#   Bildschirm-Zeilen (Name, keine car_id) ueber die Rundenzeit verbunden werden. Alle 43
#   Speicher-Laeufe sind vom 20.-22.08., alle 76 Bildschirm-Laeufe vom 23.-24.08. -- die
#   Quellen haben sich NIE zeitnah ueberschnitten. Dazwischen wurden neue Zeiten gesetzt,
#   die Zeitgruppen stimmen nicht mehr, 81 Autos bleiben ohne Namen. Ein Speicher-Lauf
#   JETZT gibt dem Join sauberen Ueberlapp.
#   Shimanoyama und Hokubu fehlen absichtlich: dort liest die Speicher-Route 50 Zeilen
#   und haengt (bekannt und dokumentiert).
#
# PHASE 2 (Rest der Nacht): OCR-SWEEP auf den Strecken 7-22, 112 nie gescannte Boards.
#   Beantwortet nebenbei, ob ein Rivals-Board eine selbst gefahrene Zeit braucht.
#
# Drei Fehler des ersten Entwurfs sind hier behoben:
#   - run_memory_leaderboard_scan.ps1 laeuft IM GAST; vom Host aus braucht es den
#     Wrapper start_vm_memory_leaderboard_scan.ps1. Der erste Entwurf rief das
#     Gast-Skript direkt auf und scheiterte an "Cannot find a process forzahorizon6".
#   - Die Zeitsperre verglich die Stunde gegen 9..20 und traf damit um 18:22 sofort zu.
#     Jetzt entscheidet ein Stichtag, der beim Start berechnet wird.
#   - Die Erfolgspruefung zaehlte nur Ordner. Ein Ordner entstand auch beim Fehlschlag,
#     also meldete sie Erfolg. Jetzt wird die state.json des Laufs gelesen.
set -u
cd "$(dirname "$0")/.."
LOG=data/runtime/overnight/night_plan.out
mkdir -p data/runtime/overnight
: > "$LOG"
say() { echo "$(date '+%H:%M:%S') $*" >> "$LOG"; }

MEM_OUT=data/memory_scans/full_sweep
PHASE1_END=$(( $(date +%s) + 3*3600 ))     # drei Stunden fuer die Namen
BOARDS=(
  "Highway Circuit|S1"   "Shirakawa Circuit|S1"  "Highway Circuit|A"
  "Shirakawa Circuit|A"  "Narai-Juku Circuit|A"  "Shirakawa Circuit|R"
  "Shirakawa Circuit|B"  "Highway Circuit|B"     "Narai-Juku Circuit|B"
)

newest_rows() {   # Zeilen im jungsten Nicht-OCR-Lauf
  local newest
  newest=$(ls -dt "$MEM_OUT"/*/ 2>/dev/null | grep -v '/ocr_' | head -1)
  [ -z "$newest" ] && { echo 0; return; }
  python - "$newest" <<'PY' 2>/dev/null || echo 0
import json, sys
from pathlib import Path
p = Path(sys.argv[1]) / "state.json"
try:
    print(json.loads(p.read_text(encoding="utf-8-sig")).get("rows_collected") or 0)
except Exception:
    print(0)
PY
}

run_memory() {   # run_memory <Track> <Klasse>
  say "Speicher-Lauf: $1 $2"
  local before after
  before=$(newest_rows)
  timeout 5400 powershell.exe -NoProfile -ExecutionPolicy Bypass \
    -File scripts/start_vm_memory_leaderboard_scan.ps1 \
    -Track "$1" -PerformanceClass "$2" -RivalsMode "Road Racing" \
    -HostOutputRoot "$MEM_OUT" -KeepForzaRunning >> "$LOG" 2>&1
  local code=$?
  after=$(newest_rows)
  say "  exit $code, Zeilen im jungsten Lauf: $after (vorher $before)"
  [ "$code" -eq 0 ] && [ "$after" -gt 100 ]
}

say "=== Phase 1: Speicher-Laeufe, Zeitfenster 3 h ==="
ok=0
for entry in "${BOARDS[@]}"; do
  if [ "$(date +%s)" -ge "$PHASE1_END" ]; then say "Zeitfenster ausgeschoepft"; break; fi
  if run_memory "${entry%|*}" "${entry#*|}"; then
    ok=$((ok+1))
  else
    say "  Lauf ohne brauchbares Ergebnis"
    if [ "$ok" -eq 0 ]; then
      say "ABBRUCH: der erste Lauf hat nichts geliefert -- keine acht weiteren Fehlschlaege."
      break
    fi
  fi
done
say "Phase 1 beendet, $ok brauchbare Laeufe"

if [ "$ok" -gt 0 ]; then
  say "=== Join und Neuaufbau ==="
  timeout 900 python scripts/join_ocr_names_to_car_ids.py >> "$LOG" 2>&1
  say "  Join exit $?"
  timeout 900 python scripts/build_analytics_site.py >> "$LOG" 2>&1
  say "  Aufbau exit $?"
fi

say "=== Phase 2: OCR-Sweep auf Strecken 7-22 bis 09:00 ==="
ROUTES=7-22 UNTIL_HOUR=9 nohup bash scripts/run_baseline_20k.sh >> "$LOG" 2>&1 &
say "gestartet, pid $!"
