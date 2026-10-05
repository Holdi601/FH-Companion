#!/usr/bin/env bash
# Geduldig warten, bis die VM von allein aus dem Zustand "Stopping" herauskommt --
# und dann alles wieder anfahren.
#
# LAGE am 2026-08-26 ab 11:08: das Gastsystem hing (alle sechs Integrationsdienste
# "Lost Communication", 36% CPU, 2 Tage 18 Stunden Laufzeit). Der dokumentierte Reset
# (start_forza_vm_ready.ps1) loeste den harten Stopp aus, aber die VM bleibt seit
# ueber 17 Minuten im Zustand "Stopping": der Arbeitsprozess vmwp.exe beendet sich nicht.
#
# WAS HIER ABSICHTLICH NICHT PASSIERT: vmwp.exe abschiessen. Das waere der uebliche
# letzte Griff, aber die drei Host-Abstuerze in der Projektgeschichte haengen genau an
# dieser VM und ihrem GPU-Passthrough. Ein verlorener Nachmittag ist erholbar, ein
# abgestuerzter Host waehrend der Abwesenheit unter Umstaenden nicht. Diese Entscheidung
# gehoert dem Nutzer, nicht dem Automaten.
#
# Loest sich der Zustand von allein, geht es ohne Zutun weiter.
set -u
cd "$(dirname "$0")/.."
LOG=data/runtime/overnight/vm_wait.out

# WAS danach wieder anfaehrt. Die Vorgabe ist das alte Verhalten --
# resume_when_service_returns.sh startet breadth_first_sweep.sh, und das ist auf
# ROAD RACING festgeschrieben. Wer in einer anderen Kategorie steckengeblieben ist,
# haette damit stumm die falsche Kategorie weitergescannt. Am 2026-09-01 haette es
# genau das getan: haengengeblieben war Cross-Country.
#
#   RESUME_CMD='bash scripts/category_sweep.sh "Cross-Country" 0-18' \
#       bash scripts/wait_for_vm_then_resume.sh
RESUME_CMD="${RESUME_CMD:-bash scripts/resume_when_service_returns.sh}"
mkdir -p data/runtime/overnight
: > "$LOG"
say() { echo "$(date '+%H:%M:%S') $*" >> "$LOG"; }

vm_state() {
  powershell.exe -NoProfile -Command "(Get-VM -Name ForzaScrapeVM).State" 2>/dev/null | tr -d '\r\n '
}

say "warte darauf, dass die VM den Zustand Stopping verlaesst (bis zu 5 Stunden)"
last=""
for i in $(seq 1 100); do          # 100 x 3 min = 5 Stunden
  st=$(vm_state)
  if [ "$st" != "$last" ]; then say "  Zustand: $st"; last="$st"; fi
  if [ "$st" = "Off" ]; then
    say "VM ist aus -- starte sie ueber die dokumentierte Prozedur"
    timeout 1800 powershell.exe -NoProfile -ExecutionPolicy Bypass \
      -File scripts/start_forza_vm_ready.ps1 -VMName ForzaScrapeVM >> "$LOG" 2>&1
    say "  Start exit $?, Zustand: $(vm_state)"
    if [ "$(vm_state)" = "Running" ]; then
      say "=== Sweep wieder anfahren ==="
      nohup bash -c "$RESUME_CMD" >> "$LOG" 2>&1 &
      say "  Wiederanlauf gestartet"
      exit 0
    fi
    say "ABBRUCH: VM laeuft nach dem Start nicht"
    exit 1
  fi
  if [ "$st" = "Running" ]; then
    say "VM laeuft wieder (hat sich selbst gefangen) -- Sweep anfahren"
    nohup bash -c "$RESUME_CMD" >> "$LOG" 2>&1 &
    exit 0
  fi
  sleep 180
done
say "nach 5 Stunden immer noch $last -- hier hilft nur eine Entscheidung von Hand"
