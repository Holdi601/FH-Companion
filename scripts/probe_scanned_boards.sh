#!/usr/bin/env bash
# Depth-probe every board that ALREADY has scan data but no independent measurement.
#
# Tonight established that a board's own deepest rank is not evidence of where it ends:
# three boards were cut to the consecutive ranks 10,114/10,115/10,116 by an old
# straggler trim, and two boards whose deepest ranks differ by 6 turned out to be 4,564
# and 24,130 ranks long. So 26 of the 40 boards holding data have never been measured.
#
# Order is by information value, not route number:
#   3 Shimanoyama -- 7 boards holding only 5,519 rows between them, and its A carries
#     the 10,115 trim fingerprint that Hokubu S1 turned out to be hiding ~413,000 behind.
#   2 Shirakawa   -- 7 boards, the largest holding at 49,031 rows; most to lose if short.
#   0 Highway     -- 6 boards. Its A is the calibration board, already measured.
#   1 Narai-Juku  -- 4 boards (S1/S2/R were never scanned, so nothing to measure yet).
#   5 Soni R      -- the one class the route-5 pass ran out of time on.
#
# Classes are listed per route so already-probed boards are not re-measured.
set -u
cd "$(dirname "$0")/.."
LOG=data/runtime/probe_scanned_boards.out
: > "$LOG"

run() {  # run <route-index> <classes> <label>
  echo "=== route $1 ($3) classes $2 ===" >> "$LOG"
  python scripts/probe_board_depth.py --route-indices "$1" --classes "$2" --seconds 120 >> "$LOG" 2>&1
  echo "=== route $1 done (exit $?) ===" >> "$LOG"
}

run 3 D,C,B,A,S1,S2,R Shimanoyama
run 2 D,C,B,A,S1,S2,R Shirakawa
run 0 D,C,B,S1,S2,R   Highway
run 1 D,C,B,A         Narai-Juku
run 5 R               Soni

echo "ALL ROUTES COMPLETE" >> "$LOG"
