"""Die Anleitungsvideos der Download-Seite (/guide/...): ganz, in Bereichen, und nichts sonst.

    python scripts/test_guide_video.py

Ohne Byte-Bereiche (206) spielt Safari ein Video gar nicht ab, und anderswo geht das
Spulen nicht. Und der Pfad darf nur die zwei bekannten Namen treffen -- kein Weg aus
dist/tutorials/ hinaus.
"""
from __future__ import annotations

import socket
import sys
import tempfile
import threading
import urllib.error
import urllib.request
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(WORKSPACE / "server"))

import analytics_api as api        # noqa: E402
import serve_analytics as srv      # noqa: E402

fehler = 0


def pruefe(bedingung, text: str) -> None:
    global fehler
    print(("  ok    " if bedingung else "  FEHLT ") + text)
    if not bedingung:
        fehler += 1


def ruf(port: int, pfad: str, kopf: dict | None = None, methode: str = "GET"):
    anfrage = urllib.request.Request("http://127.0.0.1:%d%s" % (port, pfad), headers=kopf or {}, method=methode)
    try:
        with urllib.request.urlopen(anfrage, timeout=10) as antwort:
            return antwort.status, dict(antwort.headers), antwort.read()
    except urllib.error.HTTPError as e:
        return e.code, dict(e.headers), e.read()


with tempfile.TemporaryDirectory() as tmp:
    ordner = Path(tmp)
    (ordner / "dist" / "tutorials").mkdir(parents=True)
    inhalt = bytes(range(256)) * 40          # 10 240 Bytes, jede Stelle wiedererkennbar
    (ordner / "dist" / "tutorials" / "fh-companion-setup-pc.mp4").write_bytes(inhalt)
    api.DIST_DIR = ordner / "dist"
    seite = ordner / "seite.html"
    seite.write_text("<html></html>", encoding="utf-8")
    srv.NUR_AUSLIEFERN = True
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        port = s.getsockname()[1]
    handler = srv.make_handler(ordner / "sweep", seite, ordner / "bauer.py")
    server = srv.Server(("127.0.0.1", port), handler)
    faden = threading.Thread(target=server.serve_forever, daemon=True)
    faden.start()
    try:
        print("Ganz")
        st, kopf, rumpf = ruf(port, "/guide/fh-companion-setup-pc.mp4")
        pruefe(st == 200 and rumpf == inhalt, "das ganze Video (HTTP %d, %d Bytes)" % (st, len(rumpf)))
        pruefe(kopf.get("Content-Type") == "video/mp4" and kopf.get("Accept-Ranges") == "bytes",
               "als video/mp4, mit Accept-Ranges")

        print("\nBereiche")
        st, kopf, rumpf = ruf(port, "/guide/fh-companion-setup-pc.mp4", {"Range": "bytes=100-199"})
        pruefe(st == 206 and rumpf == inhalt[100:200] and kopf.get("Content-Range") == "bytes 100-199/10240",
               "bytes=100-199 -> 206 mit genau diesen 100 Bytes")
        st, _, rumpf = ruf(port, "/guide/fh-companion-setup-pc.mp4", {"Range": "bytes=10000-"})
        pruefe(st == 206 and rumpf == inhalt[10000:], "offenes Ende: bytes=10000-")
        st, _, rumpf = ruf(port, "/guide/fh-companion-setup-pc.mp4", {"Range": "bytes=-40"})
        pruefe(st == 206 and rumpf == inhalt[-40:], "die letzten 40 Bytes: bytes=-40")
        st, kopf, _ = ruf(port, "/guide/fh-companion-setup-pc.mp4", {"Range": "bytes=20000-"})
        pruefe(st == 416 and kopf.get("Content-Range") == "bytes */10240", "hinter dem Ende: 416")

        print("\nNichts sonst")
        (ordner / "dist" / "tutorials" / "fh-companion-setup-pc.jpg").write_bytes(b"\xff\xd8\xff" + b"0" * 50)
        st, kopf, rumpf = ruf(port, "/guide/fh-companion-setup-pc.jpg")
        pruefe(st == 200 and kopf.get("Content-Type") == "image/jpeg", "das Standbild als image/jpeg")
        for pfad in ["/guide/fh-companion-setup-xbox.mp4", "/guide/../laps.json", "/guide/%2e%2e/seite.html",
                     "/guide/anything.mp4"]:
            st, _, _ = ruf(port, pfad)
            pruefe(st == 404, "%s -> 404 (war %d)" % (pfad, st))
    finally:
        server.shutdown()
        server.server_close()

print()
if fehler:
    print("%d Pruefung(en) fehlgeschlagen." % fehler)
    sys.exit(1)
print("alles bestanden")
