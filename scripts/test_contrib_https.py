"""Laedt das Scan-Werkzeug ueber HTTPS hoch -- und faellt es nur zurueck, wenn es muss?

    python scripts/test_contrib_https.py [--server https://your-server.example.org:8787]

1. Der TLS-Kontext des Werkzeugs (Windows-Stammzertifikate + certifi) prueft das
   echte Zertifikat des Servers: GET /status ueber HTTPS, OHNE Rueckfall.
2. Ein Ersatzserver auf 127.0.0.1 beantwortet den TLS-Handschlag mit Klartext --
   wie ein Rechner, der das Zertifikat nicht pruefen kann. upload() muss dann ueber
   HTTP senden, und zwar die ganze Datei.
3. Lehnt der Server ab (HTTP 403), wird NICHT zurueckgefallen: eine Ablehnung ist
   eine Antwort, kein Verbindungsproblem.
4. Die alte http-Vorgabe in contrib.json wird beim Laden zu HTTPS.
"""
from __future__ import annotations

import argparse
import io
import json
import socket
import sys
import tempfile
import threading
import urllib.request
from contextlib import redirect_stdout
from pathlib import Path

WS = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(WS / "scripts"))
# contrib_format liegt im Arbeitsbaum unter server/; im Paket neben dem Werkzeug.
sys.path.insert(0, str(WS / "server"))
import contrib_scan as cs  # noqa: E402

fehler = 0


def pruefe(gut: bool, text: str) -> None:
    global fehler
    print(("  ok   " if gut else "  FEHL ") + text)
    fehler += 0 if gut else 1


def ersatzserver(antworten: list[bytes]):
    """Nimmt len(antworten) Verbindungen an. Ein TLS-Handschlag bekommt Klartext."""
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    s.listen(5)
    log = {"tls": 0, "http": 0, "bytes": 0}

    def lauf():
        for antwort in antworten:
            try:
                c, _ = s.accept()
            except OSError:
                return
            with c:
                c.settimeout(5)
                erstes = c.recv(65536)
                if erstes[:1] == b"\x16":
                    log["tls"] += 1
                    c.sendall(b"HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")
                    continue
                daten = erstes
                kopf, _, rumpf = daten.partition(b"\r\n\r\n")
                laenge = 0
                for zeile in kopf.split(b"\r\n"):
                    if zeile.lower().startswith(b"content-length:"):
                        laenge = int(zeile.split(b":")[1])
                while len(rumpf) < laenge:
                    mehr = c.recv(65536)
                    if not mehr:
                        break
                    rumpf += mehr
                log["http"] += 1
                log["bytes"] = len(rumpf)
                c.sendall(antwort)

    threading.Thread(target=lauf, daemon=True).start()
    return s, s.getsockname()[1], log


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--server", default=cs.DEFAULT_SERVER)
    args = ap.parse_args()

    # 1. Das echte Zertifikat, mit dem Kontext des Werkzeugs.
    try:
        with urllib.request.urlopen(args.server.rstrip("/") + "/status", timeout=20,
                                    context=cs._tls_kontext()) as r:
            pruefe(r.status == 200, f"{args.server}: Zertifikat geprueft, /status {r.status}")
    except Exception as e:
        pruefe(False, f"{args.server}: HTTPS scheitert -- {e}")

    archiv = Path(tempfile.mkdtemp()) / "probe.zip"
    archiv.write_bytes(b"PK" + bytes(range(256)) * 400)          # 102 402 Bytes

    # 2. Rueckfall bei gescheitertem Handschlag.
    ok = json.dumps({"rows": 7, "runs": ["a"]}).encode()
    s, port, log = ersatzserver([b"", b"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n"
                                     b"Content-Length: %d\r\nConnection: close\r\n\r\n" % len(ok) + ok])
    ausgabe = io.StringIO()
    with redirect_stdout(ausgabe):
        gut = cs.upload(archiv, f"https://127.0.0.1:{port}", gate="pw")
    s.close()
    pruefe(gut and log["tls"] == 1 and log["http"] == 1,
           f"Handschlag gescheitert -> ueber HTTP gesendet (TLS {log['tls']}, HTTP {log['http']})")
    pruefe(log["bytes"] == archiv.stat().st_size,
           f"die ganze Datei kam an ({log['bytes']} von {archiv.stat().st_size} Bytes)")
    pruefe("http" in ausgabe.getvalue().lower() and "accepted" in ausgabe.getvalue(),
           "der Nutzer erfaehrt, dass ueber http gesendet wurde")

    # 3. Eine Ablehnung ueber HTTP ist eine Antwort -- kein Rueckfall, keine Wiederholung.
    nein = json.dumps({"error": "falsches Passwort"}).encode()
    s, port, log = ersatzserver([b"HTTP/1.1 403 Forbidden\r\nContent-Type: application/json\r\n"
                                 b"Content-Length: %d\r\nConnection: close\r\n\r\n" % len(nein) + nein,
                                 b""])
    ausgabe = io.StringIO()
    with redirect_stdout(ausgabe):
        gut = cs.upload(archiv, f"http://127.0.0.1:{port}", gate="pw")
    s.close()
    pruefe(not gut and log["http"] == 1 and "falsches Passwort" in ausgabe.getvalue(),
           "eine Ablehnung wird gemeldet und nicht wiederholt")

    # 4. Alte Vorgabe in contrib.json.
    cfg = Path(tempfile.mkdtemp()) / "contrib.json"
    cfg.write_text(json.dumps({"contributor": "probe", "secret": "x" * 32,
                               "server": (cs.OLD_DEFAULT_SERVER or "http://none.invalid") + "/"}), encoding="utf-8")
    try:
        geladen = cs.load_config(cfg)
        pruefe(geladen["server"] == cs.DEFAULT_SERVER, f"alte Vorgabe wird zu {cs.DEFAULT_SERVER}")
    except SystemExit as e:
        pruefe(False, f"contrib.json nicht ladbar: {e}")

    print("Alle Pruefungen bestanden." if fehler == 0 else f"{fehler} Pruefung(en) FEHLGESCHLAGEN.")
    return 1 if fehler else 0


if __name__ == "__main__":
    sys.exit(main())
