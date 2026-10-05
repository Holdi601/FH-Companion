#!/bin/sh
# Measure the 31 Road Racing boards whose end was never proved, so a full re-scan is
# spent only where it buys something.
#
# Skipped on purpose:
#   * the 9 boards with a scrollbar-confirmed end -- nothing left to decide
#   * the 9 that were never scanned at all (Daikoku C-R, Narai-Juku S1/S2/R) -- they
#     need a full read whatever the probe says, so measuring them first only delays it
#
# 120 s of scrolling, not 60: the estimate's error is one thumb pixel divided by the
# thumb's position (~0.17% / position), so a deeper park is a sharper number. Measured
# on Highway A against its confirmed end of 29,697: 120 s gave +1.0%, 60 s gave -2.5%.
cd "$(dirname "$0")/.." || exit 1
probe() {
  echo "=== route $1 classes $2 ==="
  python scripts/probe_board_depth.py --route-indices "$1" --classes "$2" --seconds 120
}
probe 5 D,C,B,A,S1,S2,R    # Soni       -- no class ever confirmed
probe 4 D,C,B,A,S1,S2,R    # Hokubu     -- no class ever confirmed
probe 3 D,C,B,A,S1,S2,R    # Shimanoyama-- no class ever confirmed
probe 0 D,C,B,S1,S2,R      # Highway    -- A is confirmed at 29,697
probe 1 A                  # Narai-Juku -- D,C,B confirmed; S1/S2/R never scanned
probe 2 C,S2               # Shirakawa  -- D,B,A,S1,R confirmed
probe 6 D                  # Daikoku    -- only D has any data
echo "=== depth probe done: data/analytics/board_depth_probe.json ==="
