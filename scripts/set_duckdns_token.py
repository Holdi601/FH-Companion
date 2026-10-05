"""Den DuckDNS-Token auf GNAS ablegen -- verdeckt eingegeben, nie angezeigt.

    python scripts/set_duckdns_token.py

Der Token steht in Home Assistant unter Einstellungen > Add-ons > Duck DNS >
Konfiguration, Feld "Token" (Auge-Symbol zum Anzeigen). Dieses Skript fragt ihn
verdeckt ab und schreibt ihn ueber SSH -- ueber die Standardeingabe, nicht als
Befehlszeile -- nach <geheim_ordner>/duckdns.env auf dem GNAS (Rechte 600, Ordner
700). Von dort liest ihn nur der Zertifikatsdienst (docker compose --profile tls).
"""
from __future__ import annotations

import getpass
import re
import subprocess
import sys
from pathlib import Path

WS = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(WS / "scripts"))


def main() -> int:
    token = getpass.getpass("DuckDNS token (input hidden): ").strip()
    # DuckDNS-Tokens sind UUIDs. Was anders aussieht, ist fast sicher falsch kopiert.
    if not re.fullmatch(r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", token):
        print("That does not look like a DuckDNS token (expected xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx). Nothing stored.")
        return 1
    import deploy_gnas as d
    cfg = d.einstellung()
    cfg["host"] = d.erreichbarer_host(cfg)
    ordner = d.geheim_ordner(cfg)
    ziel = ordner + "/duckdns.env"
    befehl = (f"umask 077; mkdir -p '{ordner}' && chmod 700 '{ordner}' "
              f"&& cat > '{ziel}' && chmod 600 '{ziel}' && stat -c '%a %U' '{ziel}'")
    # ALS BYTES, nicht text=True: unter Windows macht ein Textkanal aus "\n" ein "\r\n",
    # und das "\r" landete mit im Token (so geschehen am 2026-09-24, 52 statt 51 Bytes).
    r = subprocess.run(d.ssh_basis(cfg) + [befehl], input=f"DuckDNS_Token={token}\n".encode("ascii"),
                       capture_output=True, timeout=60)
    token = ""
    aus, fehler = r.stdout.decode("utf-8", "replace").strip(), r.stderr.decode("utf-8", "replace").strip()
    if r.returncode != 0:
        print("Storing failed:", (fehler or aus)[-200:])
        return 1
    print(f"Stored on GNAS ({aus}).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
