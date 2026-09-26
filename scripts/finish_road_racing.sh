#!/bin/sh
# Finish the Road Racing boards that were never proved complete, before any new
# route is opened. Per route only the classes that still lack a scrollbar-confirmed
# end are listed -- re-reading the 9 boards that DO have one would cost about three
# hours for rows we already hold.
#
# Order is least-evidence-first: Soni, Hokubu and Shimanoyama are unconfirmed in
# every class; Shirakawa needs only two.
cd "$(dirname "$0")/.." || exit 1
run() {
  echo "=== route $1 classes $2 ==="
  python scripts/ocr_board_sweep.py --route-indices "$1" --classes "$2" \
    --max-chunks 30 --chunk-seconds 120 --until-hour 20
}
run 5 D,C,B,A,S1,S2,R      # Soni
run 4 D,C,B,A,S1,S2,R      # Hokubu
run 3 D,C,B,A,S1,S2,R      # Shimanoyama
run 0 D,C,B,S1,S2,R        # Highway -- A is confirmed at 29,697
run 1 A,S1,S2,R            # Narai-Juku -- D,C,B confirmed
run 2 C,S2                 # Shirakawa -- D,B,A,S1,R confirmed
run 6 D,C,B,A,S1,S2,R      # Daikoku, the one new route already started
echo "=== road racing finish pass done ==="
