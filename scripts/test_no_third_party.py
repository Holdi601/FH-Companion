"""Laden die Seiten etwas von Dritten? Und kommen die eigenen Schriften an?

    python scripts/test_no_third_party.py [--server URL]

Ohne --server wird der Server lokal gestartet. In Edge (Playwright) wird jede der
vier Seiten geoeffnet und JEDE Anfrage mitgeschrieben: ein einziger Abruf bei einem
fremden Rechner (fonts.googleapis.com, ein CDN, ein Zaehlpixel) ist ein Fehlschlag --
nach der DSGVO geht damit ohne Einwilligung die IP-Adresse des Besuchers hinaus.
Dazu: sind Barlow Condensed, IBM Plex Sans und IBM Plex Mono wirklich geladen?
"""
from __future__ import annotations

import argparse
import socket
import subprocess
import sys
import time
from pathlib import Path
from urllib.parse import urlparse

WS = Path(__file__).resolve().parent.parent
SEITEN = ["/rivals_auto_wertung.html", "/admin", "/app", "/contribute"]
SCHRIFTEN = ['500 20px "Barlow Condensed"', '400 16px "IBM Plex Sans"', '400 16px "IBM Plex Mono"']


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--server")
    args = ap.parse_args()
    server_proc = None
    if args.server:
        basis = args.server.rstrip("/")
    else:
        with socket.socket() as s:
            s.bind(("127.0.0.1", 0))
            port = s.getsockname()[1]
        server_proc = subprocess.Popen([sys.executable, str(WS / "server" / "serve_analytics.py"),
                                        "--host", "127.0.0.1", "--port", str(port), "--nur-ausliefern"],
                                       cwd=str(WS), stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        basis = f"http://127.0.0.1:{port}"
        for _ in range(50):
            try:
                socket.create_connection(("127.0.0.1", port), timeout=0.2).close()
                break
            except OSError:
                time.sleep(0.2)
    eigener = urlparse(basis).netloc
    fehler = 0
    from playwright.sync_api import sync_playwright
    try:
        with sync_playwright() as p:
            browser = p.chromium.launch(channel="msedge", headless=True)
            for pfad in SEITEN:
                seite = browser.new_page()
                fremd: list[str] = []
                eigene = 0

                def mitschreiben(req, fremd=fremd):
                    nonlocal eigene
                    ziel = urlparse(req.url)
                    if ziel.scheme in ("data", "blob", "about"):
                        return
                    if ziel.netloc == eigener:
                        eigene += 1
                    else:
                        fremd.append(req.url[:100])

                seite.on("request", mitschreiben)
                seite.goto(basis + pfad, wait_until="networkidle", timeout=120000)
                seite.evaluate("document.fonts.ready")
                geladen = [s for s in SCHRIFTEN if seite.evaluate(f"document.fonts.check('{s}')")]
                familien = seite.evaluate("Array.from(document.fonts).filter(f => f.status === 'loaded').map(f => f.family + ' ' + f.weight).join(', ')")
                gut = not fremd
                print(("  ok   " if gut else "  FEHL ") + f"{pfad}: {eigene} eigene Anfrage(n), {len(fremd)} fremde"
                      + (f" -> {fremd[:3]}" if fremd else ""))
                fehler += 0 if gut else 1
                gut2 = bool(familien)
                print(("  ok   " if gut2 else "  FEHL ") + f"{pfad}: geladene Schriften: {familien or 'keine'}")
                fehler += 0 if gut2 else 1
                seite.close()
            browser.close()
    finally:
        if server_proc:
            server_proc.terminate()
    print("Keine Anfrage an Dritte, Schriften geladen." if fehler == 0 else f"{fehler} Problem(e).")
    return 1 if fehler else 0


if __name__ == "__main__":
    sys.exit(main())
