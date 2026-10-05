#!/usr/bin/env bash
# Aufpassen, dass der Cross-Country-Sweep die Nacht ueberlebt.
#
#     nohup bash scripts/cc_supervisor.sh > /dev/null 2>&1 &
#
# ## Warum
#
# Am 2026-09-13 um 20:00 hat der zweite Durchgang sofort abgebrochen: der
# vorherige Lauf hatte das Spiel auf einer Rivalen-Karte stehen lassen (idx16 ist
# ohne eigene Zeit nicht scannbar), und von dort aus findet der Kategoriewaehler
# seinen Schirm nicht. Er hat fuenf Versuche mit ESC; reichen die nicht, endet der
# ganze Lauf -- und dann steht der Rechner die Nacht ueber still, obwohl 35 Boards
# offen sind.
#
# Ein Fehlschlag ist hier fast immer ein ZUSTAND, kein Defekt: das Spiel steht auf
# dem falschen Schirm. Der naechste Anlauf beginnt mit einer frischen Navigation und
# raeumt genau das auf. Also lohnt sich Wiederholen -- aber nicht endlos.
#
# ## Was er NICHT tut
#
# Er startet nichts, solange noch ein Sweep laeuft. Zwei Prozesse, die dieselbe VM
# fernsteuern, sind schlimmer als gar keiner. Und er hoert auf, sobald eine
# STOP-Datei da ist oder keine Boards mehr offen sind -- eine Schleife, die nach
# getaner Arbeit weiterlaeuft, startet irgendwann etwas Unerwuenschtes.
set -u
cd "$(dirname "$0")/.."

KATEGORIE="Cross-Country"
STRECKEN="5,7,8,12,15,16"
STOP=data/runtime/overnight/STOP
LOG=data/runtime/overnight/cc_supervisor.out
MAX_NEUSTARTS="${MAX_NEUSTARTS:-12}"
: > "$LOG"
sagen() { echo "$(date '+%Y-%m-%d %H:%M:%S') $*" >> "$LOG"; }

laeuft() {
  # Ohne Argumente in `ps` (Git-Bash zeigt sie nicht) ueber die Prozessliste von
  # Windows -- dort steht die Befehlszeile vollstaendig.
  powershell.exe -NoProfile -Command \
    "if (Get-CimInstance Win32_Process -Filter \"Name='bash.exe'\" | Where-Object { \$_.CommandLine -match 'category_sweep' }) { 'JA' } else { 'NEIN' }" \
    2>/dev/null | tr -d '\r' | grep -q JA
}

offen() {
  python - <<'PY' 2>/dev/null
import json
from pathlib import Path
KLASSEN = ["D", "C", "B", "A", "S1", "S2", "R"]
OFFEN = [5, 7, 8, 12, 15, 16]
p = Path("data/runtime/overnight/cross-country_progress.json")
try:
    fertig = {tuple(x) for x in json.loads(p.read_text(encoding="utf-8-sig")).get("pairs", [])}
except Exception:
    print(len(OFFEN) * len(KLASSEN))
else:
    print(sum(1 for i in OFFEN for k in KLASSEN if [i, k] not in [list(f) for f in fertig]))
PY
}

# Hat der Bestenlisten-Dienst gerade gestreikt?
#
# WARUM DAS HIER STEHT: ein "Server Error" sagt nichts ueber das Board und nichts
# ueber den Sweep -- er sagt, dass der Dienst gerade nicht antwortet. Ein Neustart
# faehrt dann geradewegs wieder hinein, scheitert wieder, und verbraucht dabei eine
# meiner zwoelf Wiederholungen. In der Nacht zum 2026-08-27 ist auf diesem Weg ein
# Board mit 89 Zeilen als ERLEDIGT eingetragen worden, weil ein Dienstausfall wie
# ein zu Ende gelesenes Board aussieht.
#
# Der Sweep selbst zieht sich schon zurueck (8 Minuten, dreimal). Gibt er danach
# auf, war der Ausfall laenger -- und dann ist eine Viertelstunde Ruhe die richtige
# Antwort, nicht ein sofortiger neuer Anlauf.
dienstausfall() {
  # Nur die juengsten Zeilen: ein Ausfall von vor zwei Stunden ist vorbei.
  tail -n 400 data/runtime/overnight/cross-country.out 2>/dev/null \
    | grep -qiE "server error|cannot reach the leaderboard"
}

sagen "Aufseher gestartet; hoechstens $MAX_NEUSTARTS Neustarts"
neustarts=0
while [ "$neustarts" -lt "$MAX_NEUSTARTS" ]; do
  sleep 120
  [ -e "$STOP" ] && { sagen "STOP gefunden -- Ende"; exit 0; }
  if laeuft; then continue; fi

  rest=$(offen)
  case "$rest" in ''|*[!0-9]*) rest=0 ;; esac
  if [ "$rest" -le 0 ]; then
    sagen "kein Sweep laeuft und nichts mehr offen -- Ende"
    exit 0
  fi

  if dienstausfall; then
    sagen "kein Sweep laeuft, aber der Bestenlisten-Dienst hat zuletzt gestreikt"
    sagen "  -- 15 Minuten Ruhe, statt in den Ausfall hineinzulaufen"
    sleep 900
    continue
  fi

  neustarts=$((neustarts + 1))
  sagen "kein Sweep laeuft, noch $rest Board(s) offen -- Neustart $neustarts/$MAX_NEUSTARTS"
  bash scripts/category_sweep.sh "$KATEGORIE" "$STRECKEN" >> "$LOG" 2>&1
  sagen "  Lauf beendet (exit $?)"
done
sagen "Obergrenze von $MAX_NEUSTARTS Neustarts erreicht -- Ende"
