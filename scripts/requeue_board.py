"""Ein faelschlich als erledigt vermerktes Board zum Nachscannen zurueckstellen.

Der Sweep haelt `done_pairs` IM SPEICHER und schreibt die ganze Fortschrittsdatei nach
jedem erfolgreichen Board neu. Ein Eingriff waehrend eines laufenden Sweeps wird darum
beim naechsten Board wieder ueberschrieben -- deshalb weigert sich dieses Skript, solange
einer laeuft, statt still nichts zu bewirken.

    python scripts/requeue_board.py 18 A
    python scripts/requeue_board.py 18 A --force   # trotz laufendem Sweep
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
PROGRESS = WORKSPACE / "data/runtime/overnight/baseline20k_progress.json"


def sweep_running() -> bool:
    try:
        out = subprocess.run(
            ["powershell.exe", "-NoProfile", "-Command",
             "@(Get-CimInstance Win32_Process -Filter \"Name='python.exe' OR Name='bash.exe'\" | "
             "Where-Object { $_.CommandLine -and $_.CommandLine -notmatch ' -c ' -and "
             r"($_.CommandLine -match 'ocr_board_sweep\.py' -or "
             r"$_.CommandLine -match 'breadth_first_sweep\.sh') }).Count"],
            capture_output=True, text=True, timeout=60).stdout.strip()
        return out.isdigit() and int(out) > 0
    except Exception:
        return True  # im Zweifel annehmen, dass einer laeuft


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("route_index", type=int)
    ap.add_argument("klass")
    ap.add_argument("--progress-file", type=Path, default=PROGRESS)
    ap.add_argument("--force", action="store_true")
    args = ap.parse_args()

    if sweep_running() and not args.force:
        print("Es laeuft ein Sweep. Er wuerde die Aenderung beim naechsten fertigen "
              "Board ueberschreiben. Erst anhalten, dann hier nochmal -- oder --force.")
        return 2

    data = json.loads(args.progress_file.read_text(encoding="utf-8-sig"))
    pair = [args.route_index, args.klass]
    before = len(data["pairs"])
    data["pairs"] = [p for p in data["pairs"] if list(p) != pair]
    if len(data["pairs"]) == before:
        print(f"idx{args.route_index} {args.klass} stand gar nicht als erledigt drin.")
        return 1

    # Der Streckenname steht in `boards` und wird nur zur Anzeige gefuehrt; er bleibt
    # stehen, sonst verliert der naechste Lauf die Zuordnung Strecke->Klasse fuer die
    # uebrigen Klassen derselben Strecke.
    args.progress_file.write_text(json.dumps(data, indent=1), encoding="utf-8")
    print(f"idx{args.route_index} {args.klass} zurueckgestellt; "
          f"{len(data['pairs'])} Boards bleiben erledigt.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
