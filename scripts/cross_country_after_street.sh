#!/usr/bin/env bash
# Wartet, bis Street Racing durch ist, richtet Cross-Country ein und scannt es.
#
#     nohup bash scripts/cross_country_after_street.sh > /dev/null 2>&1 &
#
# Anhalten: touch data/runtime/overnight/STOP   (gilt fuer Sweep UND diese Kette)
#
# ## Warum das nicht einfach "category_sweep.sh Cross-Country" ist
#
# Cross-Country ist die erste Kategorie, deren Streckennamen NICHT auf den Schirm
# passen. Position 0 liest sich als "and Cross-Country Circuit Le" -- vorn und hinten
# gekappt. Zwei Dinge haengen daran:
#
#   * Der ANKER, von dem der Navigator Positionen abzaehlt, ist der Name an Index 0.
#     Ein gekappter Name wird nie gefunden ("Could not find the anchor route").
#   * Die BESCHRIFTUNG der Boards. Ein abgeschnittener Name landet sonst dauerhaft im
#     Auswertungsbestand, und repair_track_names haette keine Wahrheit mehr, gegen die
#     es reparieren koennte.
#
# Darum: erst Namen ernten (harvest_route_names.py setzt sie aus mehreren Aufnahmen
# zusammen), dann PRUEFEN, und erst bei bestandener Pruefung scannen.
#
# ## Das Pruef-Tor
#
# Gescannt wird nur, wenn alle 19 Namen plausibel sind: jeder mindestens acht Zeichen,
# und jeder entweder mit "Cross-Country" darin oder ein bekannter Marathonname
# ("The Titan"). Faellt einer durch, bricht die Kette ab und meldet es -- lieber kein
# Cross-Country als 19 Boards unter Namen wie "Cras-eouniry Circ".
set -u
cd "$(dirname "$0")/.."

CATEGORY="Cross-Country"
ROUTES="0-18"
COUNT=19
STREET_PROGRESS=data/runtime/overnight/street_racing_progress.json
STREET_EXPECTED=105
NAMES_FILE="config/route_names_cross-country.json"
LOG=data/runtime/overnight/cross_country.log
STOP=data/runtime/overnight/STOP
POLL=120

say() { echo "$(date '+%m-%d %H:%M:%S') $*" >> "$LOG"; }

street_done() {
  python -c "
import json
try:
    d = json.load(open('$STREET_PROGRESS', encoding='utf-8-sig'))
    print(len(d.get('pairs', [])))
except Exception:
    print(-1)
" 2>/dev/null || echo -1
}

sweep_running() {
  local n
  n=$(powershell.exe -NoProfile -Command "
@(Get-CimInstance Win32_Process -Filter \"Name='python.exe' OR Name='bash.exe'\" |
  Where-Object { \$_.CommandLine -and \$_.CommandLine -notmatch ' -c ' -and
                 (\$_.CommandLine -match 'ocr_board_sweep\.py' -or
                  \$_.CommandLine -match 'category_sweep\.sh') }).Count" 2>/dev/null | tr -dc '0-9')
  [ "${n:-0}" -gt 0 ] 2>/dev/null
}

say "=== wartet auf das Ende von Street Racing ($STREET_EXPECTED Boards) ==="

while true; do
  [ -e "$STOP" ] && { say "STOP gefunden -- Kette beendet."; exit 0; }
  done_count=$(street_done)
  if [ "$done_count" -ge "$STREET_EXPECTED" ] 2>/dev/null && ! sweep_running; then
    say "Street Racing fertig ($done_count/$STREET_EXPECTED), kein Sweep mehr -- weiter"
    break
  fi
  sleep "$POLL"
done

# --- 1. Kategorie stellen (mit Sichtpruefung des gelben Rahmens) --------------
#
# NICHT einfach select_rivals_category.py aufrufen. Beim ersten Versuch am
# 2026-08-31 um 04:41 stand das Spiel noch auf der BESTENLISTE des letzten
# Street-Racing-Boards; der Waehler sah "Change Rival / R 998 ..." statt des
# Kategorieschirms und die Kette brach nach acht Sekunden ab. category_sweep.sh
# macht es richtig: erst navigieren, dann bis zu fuenf Anlaeufe mit einem ESC
# dazwischen, um sich Schirm fuer Schirm zurueckzuziehen.
say "zur Kategorieauswahl navigieren"
timeout 480 powershell.exe -NoProfile -ExecutionPolicy Bypass \
  -File scripts/start_forza_navigation.ps1 -RivalsMode "$CATEGORY" \
  -TimeoutMinutes 6 >> "$LOG" 2>&1 || say "  erster Anlauf gescheitert (erwartet)"

picked=0
for attempt in 1 2 3 4 5; do
  [ -e "$STOP" ] && { say "STOP gefunden -- Kette beendet."; exit 0; }
  if python scripts/select_rivals_category.py --where >> "$LOG" 2>&1; then
    if python scripts/select_rivals_category.py "$CATEGORY" >> "$LOG" 2>&1; then
      say "  Kategorie '$CATEGORY' gestellt und im Bild bestaetigt (Anlauf $attempt)"
      picked=1
      break
    fi
  fi
  say "  Anlauf $attempt gescheitert -- ESC und nochmal"
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/drive_vm_forza.ps1 \
    -Key ESC -Count 1 -SettleMs 3500 >> "$LOG" 2>&1
done
if [ "$picked" -eq 0 ]; then
  say "ABBRUCH: '$CATEGORY' liess sich nicht stellen -- ohne bestaetigte Kategorie"
  say "         wird weder geerntet noch gescannt."
  exit 1
fi

# --- 2. Namen ernten ----------------------------------------------------------
say "Streckennamen ernten ($COUNT Strecken, mehrere Aufnahmen je Strecke)"
if ! timeout 2400 python scripts/harvest_route_names.py "$CATEGORY" "$COUNT" \
       --shots 4 >> "$LOG" 2>&1; then
  say "ABBRUCH: das Ernten der Namen ist gescheitert."
  exit 1
fi

# --- 3. Das Pruef-Tor ---------------------------------------------------------
say "Namen pruefen"
if ! python - <<'PYEOF' >> "$LOG" 2>&1
import json, sys, re
from pathlib import Path

path = Path("config/route_names_cross-country.json")
if not path.exists():
    print("FEHLT:", path); sys.exit(1)
data = json.loads(path.read_text(encoding="utf-8-sig"))
routes = data.get("routes", data) if isinstance(data, dict) else data
names = [r.get("name", "") if isinstance(r, dict) else str(r) for r in routes]
if len(names) != 19:
    print(f"FEHLER: {len(names)} Namen statt 19"); sys.exit(1)

bad = []
for index, name in enumerate(names):
    text = (name or "").strip()
    ok = len(text) >= 8 and (re.search(r"Cross[\s-]?Country", text, re.I)
                             or text.lower() in {"the titan"})
    if not ok:
        bad.append((index, text))
if bad:
    print("FEHLER: unplausible Namen ->", bad); sys.exit(1)

# Doppelte Namen heissen: das Ernten hat zwei Strecken nicht auseinandergehalten.
# In der unbestaetigten Fassung tragen die Positionen 10 und 11 denselben Text
# ("Tateyama Alpine Cross-Cour") -- genau der Fall, der hier auffallen muss.
lowered = [n.strip().lower() for n in names]
duplicates = {n for n in lowered if lowered.count(n) > 1}
if duplicates:
    print("FEHLER: doppelte Namen ->", sorted(duplicates)); sys.exit(1)

print("alle 19 Namen plausibel und verschieden:")
for index, name in enumerate(names):
    print(f"  {index:2d}  {name}")
PYEOF
then
  say "ABBRUCH: die geernteten Namen haben die Pruefung NICHT bestanden."
  say "        Es wird nichts gescannt -- lieber keine Daten als falsch beschriftete."
  exit 1
fi

# --- 4. Namen in den Katalog schreiben ----------------------------------------
say "Namen in den Katalog uebernehmen"
python - <<'PYEOF' >> "$LOG" 2>&1
import json
from pathlib import Path

names = json.loads(Path("config/route_names_cross-country.json")
                   .read_text(encoding="utf-8-sig"))
routes = names.get("routes", names) if isinstance(names, dict) else names
clean = [{"route_index": i,
          "name": (r.get("name") if isinstance(r, dict) else str(r)).strip()}
         for i, r in enumerate(routes)]

path = Path("config/fh6_board_catalogue.json")
catalogue = json.loads(path.read_text(encoding="utf-8-sig"))
catalogue.setdefault("route_carousel_by_category", {})["Cross-Country"] = clean
# Die unbestaetigte Fassung bleibt als Beleg stehen, wird aber als abgeloest markiert.
unverified = catalogue.get("route_enumerations_unverified", {})
if "Cross-Country" in unverified:
    unverified["_superseded_cross_country"] = (
        "2026-08-31 durch geerntete Namen in route_carousel_by_category ersetzt")
path.write_text(json.dumps(catalogue, indent=1, ensure_ascii=False),
                encoding="utf-8")
print(f"{len(clean)} Namen im Katalog")
PYEOF

# --- 5. Scannen ---------------------------------------------------------------
say "Sweep '$CATEGORY' $ROUTES startet (SKIP_CATEGORY=1, sie steht bereits)"
SKIP_CATEGORY=1 nohup bash scripts/category_sweep.sh "$CATEGORY" "$ROUTES" \
  > /dev/null 2>&1 &
sleep 15
nohup bash scripts/keep_sweeping.sh "$CATEGORY" "$ROUTES" > /dev/null 2>&1 &
say "=== Cross-Country laeuft. Anhalten: touch $STOP ==="
