"""Vom echten Ergebnisbild bis zur Horizon-Play-Zeit: die App liest, unterschreibt, sendet -- an
einen LOKALEN Server (nie den echten).

    python scripts/test_race_e2e_local.py <results.jpg> [Strecke]

Startet den Server wie test_race_endpoints.py auf 127.0.0.1 mit Wegwerfverzeichnissen,
ruft "FH Companion.exe --race-submit" mit einer eigenen Anmeldung (FORZA_SUBMIT_HOME) und
prueft, dass das Rennen ankommt und als Horizon-Play-Zeit zaehlt.
"""

from __future__ import annotations

import json
import os
import socket
import subprocess
import sys
import tempfile
import threading
import urllib.request
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(WORKSPACE / "server"))

import analytics_api as api          # noqa: E402
import lap_submissions as laps       # noqa: E402
import race_submissions as rennen    # noqa: E402
import serve_analytics as srv        # noqa: E402

EXE = WORKSPACE / "haptics" / "ForzaHaptics.Tester" / "bin" / "Release" / "net9.0-windows10.0.19041.0" / "win-x64" / "FH Companion.exe"


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    bild = Path(sys.argv[1])
    strecke = sys.argv[2] if len(sys.argv) > 2 else "Festival Chase"
    with tempfile.TemporaryDirectory() as tmp:
        ordner = Path(tmp)
        laps.KEYS_FILE = ordner / "submit_keys.json"
        laps.ANMELDE_VERSUCHE = ordner / "register_attempts.json"
        laps.LAPS_DIR = ordner / "laps"
        rennen.RACES_DIR = ordner / "races"
        api.KEYS_FILE = ordner / "contrib_keys.json"
        api.KEYS_FILE.write_text(json.dumps({"keys": {}, "admin": "x" * 40}), encoding="utf-8")
        api.FEATURES_FILE = ordner / "features.json"
        api.FEATURES_FILE.write_text(json.dumps({"lap_submissions": True}), encoding="utf-8")
        seite = ordner / "seite.html"
        seite.write_text("<html></html>", encoding="utf-8")
        srv.NUR_AUSLIEFERN = True
        with socket.socket() as s:
            s.bind(("127.0.0.1", 0))
            port = s.getsockname()[1]
        server = srv.Server(("127.0.0.1", port), srv.make_handler(ordner / "sweep", seite, ordner / "bauer.py"))
        threading.Thread(target=server.serve_forever, daemon=True).start()
        try:
            umgebung = dict(os.environ, FORZA_SUBMIT_HOME=str(ordner / "identitaet"))
            (ordner / "identitaet").mkdir()
            lauf = subprocess.run([str(EXE), "--race-submit", "http://127.0.0.1:%d" % port, str(bild), strecke],
                                  capture_output=True, text=True, env=umgebung, timeout=120)
            print("App:", lauf.stdout.strip()[:300])
            with urllib.request.urlopen("http://127.0.0.1:%d/api/race/hp" % port, timeout=10) as a:
                bretter = json.loads(a.read().decode("utf-8"))["boards"]
            angekommen = list(rennen.RACES_DIR.glob("*.json"))
            print("Rennen beim Server:", len(angekommen), " Bild:", len(list(rennen.RACES_DIR.glob("*.jpg"))))
            for b in bretter:
                print("  ", b)
            gut = lauf.returncode == 0 and len(angekommen) == 1 and len(bretter) > 0
            if angekommen:
                d = json.loads(angekommen[0].read_text(encoding="utf-8"))
                gut = gut and "gamertag" not in json.dumps(d).lower()
            print("alles bestanden" if gut else "FEHLGESCHLAGEN")
            return 0 if gut else 1
        finally:
            server.shutdown()
            server.server_close()


if __name__ == "__main__":
    raise SystemExit(main())
