#!/usr/bin/env bash
# Die letzten Luecken schliessen -- Cross-Country und Street Racing.
#
#     nohup bash scripts/tagesplan.sh > /dev/null 2>&1 &
#     touch data/runtime/overnight/STOP     # haelt an
#
# ## Was noch fehlt (Stand 2026-09-14, 15:00)
#
#     Cross-Country   3 Boards   idx8 C, idx12 B, idx12 A
#     Street Racing   6 Boards   idx9 "Festival Chase", alle ausser S2
#
# Die 7 Boards von Cross-Country idx16 (Shinjuku Gyoen) sind NICHT dabei: dort zeigt
# das Spiel keine Bestenliste, sondern eine Rivalen-Karte, weil auf der Strecke noch
# nie eine eigene Zeit gefahren wurde. Kein Anlauf der Welt aendert das.
#
# ## Warum eine Kategorie "erschoepft" sein kann, obwohl noch etwas offen ist
#
# Der erste Entwurf dieses Plans haette sich festgefressen. Er gab Cross-Country
# Vorrang, bis dort nichts mehr offen ist -- und zwei der drei verbliebenen Boards
# sind reproduzierbar NICHT zu holen:
#
#     idx8 C   zeigt zweimal die Bestenliste von idx9
#     idx12 A  zeigt zweimal die von idx13
#
# Der Fingerabdruck-Riegel verwirft sie zu Recht, sie bleiben offen, und der Plan
# haette bis zu 24 Laeufe daran verbrannt, ohne Street Racing je zu erreichen.
#
# Darum wird gemessen statt gehofft: bringt ein Lauf kein einziges Board, zaehlt das.
# Beim ZWEITEN fruchtlosen Lauf in Folge gilt die Kategorie fuer heute als
# erschoepft. Zwei und nicht einer, weil ein Dienstausfall einen Lauf leer ausgehen
# laesst, ohne dass etwas grundsaetzlich falsch waere -- genau so ist `idx12 B` heute
# frueh gescheitert.
set -u
cd "$(dirname "$0")/.."

STOP=data/runtime/overnight/STOP
LOG=data/runtime/overnight/tagesplan.out
MAX_LAEUFE="${MAX_LAEUFE:-24}"
FRUCHTLOS_MAX="${FRUCHTLOS_MAX:-2}"
: > "$LOG"
sagen() { echo "$(date '+%Y-%m-%d %H:%M:%S') $*" >> "$LOG"; }

laeuft() {
  powershell.exe -NoProfile -Command \
    "if (Get-CimInstance Win32_Process -Filter \"Name='bash.exe'\" | Where-Object { \$_.CommandLine -match 'category_sweep' }) { 'JA' } else { 'NEIN' }" \
    2>/dev/null | tr -d '\r' | grep -q JA
}

# Ein Dienstausfall sagt nichts ueber das Board -- hineinzurennen verbrennt nur
# Versuche. Die ganze Begruendung steht in cc_supervisor.sh.
# NUR EIN FRISCHER AUSFALL ZAEHLT -- die Pruefung steht in einem eigenen Skript,
# weil sie die Uhrzeit der Protokollzeile lesen muss. Ohne Zeitbezug fand der Plan
# am 2026-09-14 um 14:58 einen Fehler von 11:07 und haette sich damit bis zum Abend
# selbst blockiert.
dienstausfall() {
  python scripts/frischer_dienstausfall.py 30 >> "$LOG" 2>&1
}

offen() {
  python - "$1" <<'PY' 2>/dev/null
import json, sys
from pathlib import Path
KL = ["D", "C", "B", "A", "S1", "S2", "R"]
PLAN = {
    "Cross-Country": ("cross-country_progress.json", [5, 7, 8, 12, 15]),
    "Street Racing": ("street_racing_progress.json", [9]),
}
datei, strecken = PLAN[sys.argv[1]]
try:
    fertig = {tuple(x) for x in json.loads(
        Path("data/runtime/overnight/" + datei).read_text(encoding="utf-8-sig"))["pairs"]}
except Exception:
    print(len(strecken) * len(KL))
else:
    print(sum(1 for i in strecken for k in KL if (i, k) not in fertig))
PY
}

zahl() { case "$1" in ''|*[!0-9]*) echo 0 ;; *) echo "$1" ;; esac; }

# Einen Lauf fuer eine Kategorie, und sagen ob er etwas gebracht hat.
# Rueckgabe 0 = Fortschritt, 1 = fruchtlos.
lauf_fuer() {
  local kategorie="$1" strecken="$2"
  local vorher nachher
  vorher=$(zahl "$(offen "$kategorie")")
  sagen "Lauf $laeufe/$MAX_LAEUFE: $kategorie, noch $vorher Board(s)"
  bash scripts/category_sweep.sh "$kategorie" "$strecken" >> "$LOG" 2>&1
  nachher=$(zahl "$(offen "$kategorie")")
  sagen "  beendet -- offen: $vorher -> $nachher"
  [ "$nachher" -lt "$vorher" ]
}

sagen "Tagesplan gestartet; hoechstens $MAX_LAEUFE Laeufe"
sagen "  Cross-Country offen: $(zahl "$(offen 'Cross-Country')")"
sagen "  Street Racing offen: $(zahl "$(offen 'Street Racing')")"

laeufe=0
cc_fruchtlos=0
sr_fruchtlos=0
while [ "$laeufe" -lt "$MAX_LAEUFE" ]; do
  [ -e "$STOP" ] && { sagen "STOP gefunden -- Ende"; exit 0; }
  if laeuft; then sleep 120; continue; fi

  cc=$(zahl "$(offen 'Cross-Country')")
  sr=$(zahl "$(offen 'Street Racing')")
  cc_dran=0; sr_dran=0
  [ "$cc" -gt 0 ] && [ "$cc_fruchtlos" -lt "$FRUCHTLOS_MAX" ] && cc_dran=1
  [ "$sr" -gt 0 ] && [ "$sr_fruchtlos" -lt "$FRUCHTLOS_MAX" ] && sr_dran=1

  if [ "$cc_dran" -eq 0 ] && [ "$sr_dran" -eq 0 ]; then
    sagen "nichts mehr zu holen -- Cross-Country offen $cc, Street Racing offen $sr"
    sagen "  (was offen bleibt, ist nicht erreichbar; siehe Kopf dieses Skripts)"
    exit 0
  fi

  if dienstausfall; then
    sagen "der Bestenlisten-Dienst hat zuletzt gestreikt -- 15 Minuten Ruhe"
    sleep 900
    continue
  fi

  # Vor jedem Lauf aufraeumen: eine volle Platte in der VM sieht aus wie ein
  # Navigationsfehler und hat in der Nacht zum 2026-09-14 eine Stunde gekostet.
  python scripts/prune_captures.py --apply >> "$LOG" 2>&1

  laeufe=$((laeufe + 1))
  # STREET RACING ZUERST.
  #
  # Der erste Entwurf gab Cross-Country Vorrang "weil es der groessere Rest ist".
  # Das war einmal wahr und ist es nicht mehr: offen sind 6 Street-Racing-Boards
  # gegen 3 Cross-Country-Boards, und von diesen dreien sind zwei reproduzierbare
  # Zwillinge, die der Fingerabdruck-Riegel zu Recht verwirft. Der sichere Ertrag
  # liegt also vollstaendig bei Street Racing -- dort ist kein Board als blockiert
  # bekannt, und die Kategorie ist befahrbar: der Lauf vom 2026-08-31 hat dort
  # fuenf Boards am Stueck gescannt.
  if [ "$sr_dran" -eq 1 ]; then
    if lauf_fuer "Street Racing" "9"; then
      sr_fruchtlos=0
    else
      sr_fruchtlos=$((sr_fruchtlos + 1))
      sagen "  Street Racing brachte nichts ($sr_fruchtlos/$FRUCHTLOS_MAX)"
      [ "$sr_fruchtlos" -ge "$FRUCHTLOS_MAX" ] && \
        sagen "  -> Street Racing fuer heute erschoepft, weiter mit Cross-Country"
    fi
  else
    if lauf_fuer "Cross-Country" "5,7,8,12,15"; then
      cc_fruchtlos=0
    else
      cc_fruchtlos=$((cc_fruchtlos + 1))
      sagen "  Cross-Country brachte nichts ($cc_fruchtlos/$FRUCHTLOS_MAX)"
    fi
  fi
done
sagen "Obergrenze von $MAX_LAEUFE Laeufen erreicht -- Ende"
