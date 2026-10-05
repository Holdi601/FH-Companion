"""Serve the Rivals analytics page on localhost, rebuilding it when scans change.

The page is a self-contained file, so a browser can open it directly -- but a served
address survives reloads, bookmarks and a second machine on the LAN, and it lets the
rebuild happen on request: every hit checks whether any board output is newer than
the page and regenerates it first. A finished scan is therefore on the site the next
time the page is loaded, with nothing to run by hand.

    python server/serve_analytics.py
    python server/serve_analytics.py --port 8787 --host 127.0.0.1

Binds 127.0.0.1 by default, so nothing is exposed to the network. Pass
--host 0.0.0.0 deliberately if another device should reach it.
"""

from __future__ import annotations

import argparse
import http.server
import json
import socket
import socketserver
import ssl
import subprocess
import sys
import threading
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import analytics_api  # noqa: E402
import downloads  # noqa: E402
import geoip  # noqa: E402
import usage  # noqa: E402
import visits  # noqa: E402

REBUILD_LOCK = threading.Lock()

# ---------------------------------------------------------------- Bremsen
#
# DIESER SERVER STEHT IM INTERNET, und bis 2026-09-24 durfte jeder so oft fragen,
# wie er wollte: die Seite (27 MB), das Paket (69 MB), die Zusammenfassung (27 MB
# parsen). Ein einziger Rechner in einer Schleife haette die Leitung oder die CPU
# des NAS belegt. Jetzt ein Eimer je Adresse und Art: FUELLUNG ist der Vorrat,
# RATE was je Sekunde nachkommt. Grosszuegig fuer Menschen und Apps, eng fuer
# Schleifen.
GRENZEN = {
    "api":      (2.0, 120),         # 120 auf einmal, 2 je Sekunde nach
    "post":     (0.5, 30),
    "page":     (0.5, 30),          # die grosse Seite
    "download": (30 / 3600, 10),    # Paket: 10 auf einmal, 30 je Stunde
    # Die volle Telemetrie einer Runde (seit 2026-09-30): bis zu einige MB je Stueck.
    "telemetry": (60 / 3600, 20),   # 20 auf einmal, 60 je Stunde
}
_EIMER: dict = {}
_EIMER_SCHLOSS = threading.Lock()


def darf(adresse: str, art: str, jetzt: float | None = None) -> float:
    """0.0 heisst: darf. Sonst die Sekunden bis zum naechsten Versuch."""
    rate, fuellung = GRENZEN[art]
    jetzt = time.monotonic() if jetzt is None else jetzt
    with _EIMER_SCHLOSS:
        vorrat, zuletzt = _EIMER.get((adresse, art), (float(fuellung), jetzt))
        vorrat = min(float(fuellung), vorrat + (jetzt - zuletzt) * rate)
        if vorrat >= 1.0:
            _EIMER[(adresse, art)] = (vorrat - 1.0, jetzt)
            return 0.0
        _EIMER[(adresse, art)] = (vorrat, jetzt)
        if len(_EIMER) > 20000:
            # Alte Eimer weg: volle Eimer sind dasselbe wie keine.
            for k in [k for k, (v, z) in _EIMER.items() if jetzt - z > 3600]:
                _EIMER.pop(k, None)
        return (1.0 - vorrat) / rate


# Hoechstens so viele Anfragen gleichzeitig. Jede bekommt einen eigenen Faden; ohne
# Obergrenze legen ein paar hundert langsame Verbindungen den Dienst lahm.
GLEICHZEITIG = threading.BoundedSemaphore(64)

# Auf einem Spiegel gibt es nichts zu bauen.
#
# Das GNAS bekommt die fertige Seite und den Datensatz zugeschickt; Boards, Sweeps
# und der Bauer liegen dort gar nicht. Ohne diesen Riegel wuerde jeder Aufruf einen
# Bau anstossen, der am fehlenden build_analytics_site.py scheitert -- die Seite
# bliebe zwar heil, aber das Protokoll liefe mit Fehlern voll und jeder echte
# Fehler ginge darin unter. Ein Spiegel liefert aus, sonst nichts.
NUR_AUSLIEFERN = False


# The page is built from data AND from code, so both have to count as sources. A fix to
# the builder used to reach the page only when a scan happened to land afterwards: on
# 2026-08-23 a correction that collapsed 339 duplicate cars sat unserved because no board
# had been written since. The car-name file belongs here too -- naming a car_id changes
# what the page shows without any board changing.
CODE_SOURCES = ("build_analytics_dataset.py", "build_analytics_site.py",
                "build_car_roster.py")
DATA_SOURCES = (Path("config/fh6_car_id_names.json"),
                Path("config/fh6_car_catalogue.json"),
                # Ein Fremdbeitrag oder ein ausgeblendeter Lauf aendert die Seite,
                # ohne dass ein eigenes Board dazukaeme. Ohne diese beiden Eintraege
                # bliebe ein Upload unsichtbar, bis zufaellig ein Scan fertig wird.
                Path("config/dataset_visibility.json"),
                # Die Alias-Tabelle entscheidet, welcher Bildschirmname auf welches
                # Auto faellt -- sie aendert die Wertung genauso wie ein neues Board.
                # Ohne diesen Eintrag bliebe eine Korrektur an den Namen unsichtbar,
                # bis zufaellig ein Scan fertig wird. Am 2026-09-10 kamen ueber die
                # Tabelle 132.656 Zeilen zurueck; ohne sie hier stuende die Seite
                # weiter auf dem alten Stand, ohne dass irgendetwas darauf hinweist.
                Path("config/fh6_screen_name_aliases.tsv"))

# Fremdbeitraege liegen zwei Ebenen tief (contrib/<wer>/<lauf>), eigene Laeufe eine.
CONTRIB_ROOT = Path("data/memory_scans/contrib")


def newest_source(root: Path) -> float:
    """Newest mtime among the board outputs, the config and the build scripts."""
    newest = 0.0
    for pattern in ("*/leaderboard_entries.parquet", "*/state.json"):
        for path in root.glob(pattern):
            try:
                newest = max(newest, path.stat().st_mtime)
            except OSError:
                continue
    for path in CONTRIB_ROOT.glob("*/*/state.json"):
        try:
            newest = max(newest, path.stat().st_mtime)
        except OSError:
            continue
    here = Path(__file__).resolve().parent
    for extra in [here / name for name in CODE_SOURCES] + list(DATA_SOURCES):
        try:
            newest = max(newest, extra.stat().st_mtime)
        except OSError:
            continue
    return newest


def built_from(page: Path) -> float:
    """Der Quellstand, aus dem die vorhandene Seite gebaut wurde.

    WARUM NICHT DIE UHRZEIT DER SEITE: ein Bau dauert eine knappe Minute. Wer in
    dieser Minute eine Quelle aendert, hinterlaesst eine Seite, deren Zeitstempel
    JUENGER ist als die Aenderung, die sie nicht enthaelt -- und die deshalb nie
    wieder gebaut wird. Genau so ist am 2026-09-09 eine Kopfzeile verschwunden, die
    schon im Quelltext stand. Der vermerkte Stand ist der, den der Bau BEIM START
    gesehen hat, und danach richtet sich die naechste Entscheidung.
    """
    stamp = page.with_suffix(page.suffix + ".built-from")
    try:
        return float(stamp.read_text(encoding="utf-8").strip())
    except (OSError, ValueError):
        # Kein Vermerk: die alte Regel, damit eine bestehende Seite nicht sofort
        # einmal grundlos neu gebaut wird.
        try:
            return page.stat().st_mtime
        except OSError:
            return 0.0


def rebuild_if_stale(root: Path, page: Path, builder: Path, log) -> None:
    """Start a rebuild if the data has moved on, WITHOUT making the reader wait.

    A build takes 48-76 seconds on the current dataset. Waiting for it inside the
    request is what made the page look offline on 2026-08-23: while a sweep was running,
    boards kept landing, so nearly every reload found the page stale, sat through a full
    build and hit the browser's timeout first. Serving the page one build behind is
    obviously better than serving nothing.

    So: the reader gets whatever page exists now, and the build runs on a daemon thread.
    The very first build has to be waited for -- there is no page to serve yet.
    """
    if NUR_AUSLIEFERN:
        return
    if page.exists():
        if REBUILD_LOCK.acquire(blocking=False):
            REBUILD_LOCK.release()
            if newest_source(root) > built_from(page):
                threading.Thread(target=_rebuild, args=(root, page, builder, log),
                                 daemon=True).start()
        return
    _rebuild(root, page, builder, log)


def _rebuild(root: Path, page: Path, builder: Path, log) -> None:
    """The build itself.

    Serialised, because two browser tabs hitting reload at once would otherwise run
    two builds into the same file and one would serve a half-written page.
    """
    with REBUILD_LOCK:
        source = newest_source(root)
        if source == 0.0:
            return
        # Der Riegel oben gilt nur INNERHALB dieses Prozesses. Der Sweep baut die Seite
        # aber selbst mit, aus seinem eigenen Prozess -- und dann schreiben zwei Laeufe
        # gleichzeitig dieselbe laps.json und dieselbe HTML. Am 2026-08-27 um 12:51 war
        # genau das der Fall: einer seit 12:44 unterwegs, ein zweiter durch einen
        # API-Aufruf angestossen. Der Sweep hat diese Pruefung seit dem 26.08.
        # (`ocr_board_sweep.rebuild_site`), hier fehlte sie.
        try:
            running = subprocess.run(
                ["powershell.exe", "-NoProfile", "-Command",
                 "@(Get-CimInstance Win32_Process | Where-Object { "
                 "$_.CommandLine -match 'build_analytics_site' }).Count"],
                capture_output=True, text=True, timeout=60).stdout
            count = int("".join(c for c in running if c.isdigit()) or 0)
            if count > 1:
                log("rebuild skipped: another build is already running")
                return
        except Exception:
            # Laesst sich das nicht feststellen, lieber bauen als gar nicht -- eine
            # veraltete Seite ist teurer als ein ueberfluessiger Lauf.
            pass
        if built_from(page) >= source:
            return
        log(f"rebuilding: board data is newer than the page")
        started = time.monotonic()
        result = subprocess.run(
            [sys.executable, str(builder), "--root", str(root), "--out", str(page)],
            capture_output=True, text=True)
        if result.returncode == 0:
            # Der Stand, den DIESER Lauf gesehen hat -- nicht der von jetzt: was
            # sich waehrend des Baus geaendert hat, steht nicht in der Seite und
            # muss den naechsten Bau ausloesen.
            try:
                page.with_suffix(page.suffix + ".built-from").write_text(
                    repr(source), encoding="utf-8")
            except OSError:
                pass
            log(f"rebuilt in {time.monotonic() - started:.1f}s: "
                f"{(result.stdout or '').strip().splitlines()[-1:] or ['ok']}"[1:-1])
        else:
            # Serving the previous page beats serving an error: the old numbers are
            # still true, they are just missing the newest board.
            log(f"rebuild FAILED (exit {result.returncode}): "
                f"{(result.stderr or result.stdout or '').strip()[-400:]}")


def quelltext_link() -> bytes:
    """Der Link zum Quelltext fuer die Fusszeilen -- oder nichts, ohne Eintrag.

    Aus config/local.json ("source_url"), wie Name und Kontakt: solange das Repository
    privat ist, bleibt der Eintrag leer, und keine Seite zeigt Besuchern eine 404.
    """
    import html as _html
    import local_settings
    url = local_settings.source_url()
    if not url:
        return b""
    return (' &middot; <a href="' + _html.escape(url, quote=True)
            + '" rel="noopener noreferrer" target="_blank">GitHub</a>').encode("utf-8")


def make_handler(root: Path, page: Path, builder: Path):
    # Genau die Dateien, die statisch herausgehen duerfen. Der Rest des Verzeichnisses
    # bleibt drinnen, auch wenn spaeter etwas dazukommt.
    PUBLIC_FILES = {page.name}

    class Handler(http.server.SimpleHTTPRequestHandler):
        # Sekunden je Lese- oder Schreibvorgang auf dem Socket. Ohne Zeitgrenze
        # haelt eine Verbindung, die nie zu Ende sendet, ihren Faden fuer immer.
        timeout = 30

        def __init__(self, *args, **kwargs):
            super().__init__(*args, directory=str(page.parent), **kwargs)

        def handle(self):
            if not GLEICHZEITIG.acquire(timeout=5):
                try:
                    self.request.sendall(b"HTTP/1.1 503 Service Unavailable\r\n"
                                         b"Retry-After: 5\r\nContent-Length: 0\r\n"
                                         b"Connection: close\r\n\r\n")
                except OSError:
                    pass
                return
            try:
                super().handle()
            finally:
                GLEICHZEITIG.release()

        def gebremst(self, art: str) -> bool:
            """True, wenn diese Adresse gerade zu oft fragt -- dann ist 429 gesendet."""
            adresse = self.client_address[0] if self.client_address else ""
            warten = darf(adresse, art)
            if warten <= 0:
                return False
            body = b'{"ok": false, "error": "too many requests"}'
            self.send_response(429)
            self.send_header("Content-Type", "application/json")
            self.send_header("Retry-After", str(int(warten) + 1))
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            if self.command != "HEAD":
                self.wfile.write(body)
            return True

        def log_message(self, fmt, *args):        # quieter than the default
            if "GET" in (fmt % args) and page.name in (fmt % args):
                sys.stdout.write(f"[serve] {self.address_string()} {fmt % args}\n")
                sys.stdout.flush()

        def note(self, message: str) -> None:
            sys.stdout.write(f"[serve] {message}\n")
            sys.stdout.flush()

        def send_api(self, status: int, content_type: str, body: bytes,
                     filename: str | None = None, extra: dict | None = None) -> None:
            self.send_response(status)
            self.send_header("Content-Type", content_type)
            for k, v in (extra or {}).items():
                self.send_header(k, v)
            if filename:
                # Ohne diese Kopfzeile zeigt der Browser die ZIP als Zeichensalat
                # an, statt sie zu speichern.
                self.send_header("Content-Disposition",
                                 f'attachment; filename="{filename}"')
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            if self.command != "HEAD":
                self.wfile.write(body)

        def send_file(self, path, filename: str, missing: str,
                      content_type: str = "application/zip") -> None:
            """Eine Datei am Stueck ausliefern, ohne sie ganz einzulesen."""
            if path is None or not path.exists():
                self.send_api(503, "text/plain; charset=utf-8",
                              missing.encode("utf-8"))
                return
            size = path.stat().st_size
            self.send_response(200)
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(size))
            if filename:
                self.send_header("Content-Disposition",
                                 f'attachment; filename="{filename}"')
            self.end_headers()
            if self.command == "HEAD":
                return
            self.note(f"{filename} ({size / 1e6:.0f} MB) an {self.address_string()}")
            with path.open("rb") as handle:
                while True:
                    chunk = handle.read(1 << 20)
                    if not chunk:
                        break
                    try:
                        self.wfile.write(chunk)
                    except (BrokenPipeError, ConnectionResetError):
                        # Abgebrochener Abruf: haeufig, und kein Fehler des Servers.
                        self.note(f"{filename}: Abruf abgebrochen")
                        return

        def send_video(self, path) -> None:
            """Ein Video ausliefern, auf Wunsch nur einen Bereich (Range: bytes=a-b)."""
            import re as _re
            groesse = path.stat().st_size
            anfang, ende, status = 0, groesse - 1, 200
            bereich = (self.headers.get("Range") or "").strip()
            treffer = _re.match(r"^bytes=(\d*)-(\d*)$", bereich)
            if treffer and (treffer.group(1) or treffer.group(2)):
                a, b = treffer.groups()
                if not a:
                    anfang = max(0, groesse - int(b))
                else:
                    anfang = int(a)
                    ende = min(groesse - 1, int(b)) if b else groesse - 1
                if anfang > ende or anfang >= groesse:
                    self.send_response(416)
                    self.send_header("Content-Range", f"bytes */{groesse}")
                    self.send_header("Content-Length", "0")
                    self.end_headers()
                    return
                status = 206
            self.send_response(status)
            self.send_header("Content-Type", "image/jpeg" if path.suffix == ".jpg" else "video/mp4")
            self.send_header("Accept-Ranges", "bytes")
            self.send_header("Content-Length", str(ende - anfang + 1))
            if status == 206:
                self.send_header("Content-Range", f"bytes {anfang}-{ende}/{groesse}")
            self._kurz_cachen = True
            self.end_headers()
            if self.command == "HEAD":
                return
            with path.open("rb") as handle:
                handle.seek(anfang)
                rest = ende - anfang + 1
                while rest > 0:
                    chunk = handle.read(min(1 << 20, rest))
                    if not chunk:
                        break
                    try:
                        self.wfile.write(chunk)
                    except (BrokenPipeError, ConnectionResetError):
                        return
                    rest -= len(chunk)

        def read_body(self) -> bytes:
            try:
                length = int(self.headers.get("Content-Length") or 0)
            except ValueError:
                return b""
            # Obergrenze schon beim LESEN, nicht erst beim Pruefen: sonst schreibt
            # ein beliebiger Fremder den Arbeitsspeicher voll, bevor irgendjemand
            # gefragt hat, ob er ueberhaupt jemand ist.
            if length <= 0 or length > analytics_api.MAX_UPLOAD_BYTES:
                return b""
            return self.rfile.read(length)

        def do_POST(self):                         # noqa: N802 (stdlib naming)
            if not self.path.startswith("/api/"):
                self.send_error(404)
                return
            if self.gebremst("post"):
                return
            body = self.read_body()
            # Die Maschine mitgeben: die Sperre nach zehn Fehlversuchen haengt daran.
            # `client_address[0]` und nicht `address_string()`, weil letzteres je nach
            # Aufloesung einen Namen liefern kann -- und ein Zaehlwerk, dessen
            # Schluessel sich aendert, zaehlt nichts.
            status, content_type, payload = analytics_api.handle(
                "POST", self.path, self.headers, body,
                client=self.client_address[0] if self.client_address else "")
            if self.path == "/api/contribute" and status == 200:
                self.note(f"Abgabe angenommen von {self.address_string()}")
            elif status >= 400:
                self.note(f"{self.path} abgelehnt ({status}) "
                          f"fuer {self.address_string()}")
            self.send_api(status, content_type, payload,
                          f"forza-contrib-{self.headers.get('X-Forza-For', 'paket')}.zip"
                          if content_type == "application/zip" else None)

        def do_GET(self):                          # noqa: N802 (stdlib naming)
            if self.path in ("/download/haptics", "/download/app", "/download/tool"):
                if self.gebremst("download"):
                    return
            elif self.path.startswith("/api/lap/telemetry/"):
                if self.gebremst("telemetry"):
                    return
            elif (self.path.startswith("/api/") or self.path.startswith("/fonts/")
                  or self.path.startswith("/brand/") or self.path == "/favicon.ico"
                  or self.path.startswith("/guide/")):
                if self.gebremst("api"):
                    return
            elif self.gebremst("page"):
                return
            # BESUCHE: nur Seiten, nur GET (HEAD ist kein Besuch). Was eine Seite ist,
            # entscheidet visits.SEITEN -- die Verwaltungsseite gehoert nicht dazu.
            if self.command == "GET" and self.path.split("?", 1)[0] in visits.SEITEN:
                try:
                    visits.note_page(self.path, self.client_address[0] if self.client_address else "",
                                     self.headers.get("User-Agent", ""))
                except Exception:
                    pass
            if self.path.startswith("/fonts/"):
                # DIE SCHRIFTEN DER SEITEN, vom eigenen Server statt von Google.
                # Nur schlichte Namen aus server/fonts/ -- kein Pfad, kein Ausbrechen.
                import re as _re
                name = self.path[len("/fonts/"):]
                datei = Path(__file__).resolve().parent / "fonts" / name
                if not _re.match(r"^[a-z0-9-]+\.(woff2|css)$", name) or not datei.is_file():
                    self.send_error(404)
                    return
                roh = datei.read_bytes()
                self.send_response(200)
                self.send_header("Content-Type", "font/woff2" if name.endswith(".woff2")
                                 else "text/css; charset=utf-8")
                self.send_header("Content-Length", str(len(roh)))
                self._lange_cachen = True
                self.end_headers()
                if self.command != "HEAD":
                    self.wfile.write(roh)
                return
            if self.path.startswith("/guide/"):
                # DIE ANLEITUNGSVIDEOS (dist/tutorials/), mit Byte-Bereichen: ohne sie
                # spielt Safari ein Video gar nicht, und anderswo geht Spulen nicht.
                datei = analytics_api.guide_video(self.path[len("/guide/"):])
                if datei is None:
                    self.send_error(404)
                    return
                self.send_video(datei)
                return
            if self.path == "/favicon.ico" or self.path.startswith("/brand/"):
                # LOGO UND SYMBOL (server/brand/), wie die Schriften vom eigenen Server.
                # /favicon.ico fragt jeder Browser von selbst an -- so hat auch die
                # Auswertungsseite ein Symbol, ohne dass sie neu gebaut werden muss.
                # Nur schlichte Namen aus server/brand/ -- kein Pfad, kein Ausbrechen.
                import re as _re
                name = "favicon.ico" if self.path == "/favicon.ico" else self.path[len("/brand/"):]
                datei = Path(__file__).resolve().parent / "brand" / name
                treffer = _re.match(r"^[a-z0-9-]+\.(png|ico|svg)$", name)
                if not treffer or not datei.is_file():
                    self.send_error(404)
                    return
                roh = datei.read_bytes()
                self.send_response(200)
                self.send_header("Content-Type", {"png": "image/png", "ico": "image/x-icon",
                                                  "svg": "image/svg+xml"}[treffer.group(1)])
                self.send_header("Content-Length", str(len(roh)))
                # Einen Tag, nicht "immutable" wie die Schriften: ein neues Logo kommt
                # unter demselben Namen.
                self._kurz_cachen = True
                self.end_headers()
                if self.command != "HEAD":
                    self.wfile.write(roh)
                return
            if self.path == "/api/dataset":
                # GESTREAMT statt ganz in den Speicher gelesen: 27 MB je Aufruf,
                # und zehn gleichzeitige Abrufe waeren zehn Kopien im NAS.
                rebuild_if_stale(root, page, builder, self.note)
                self.send_file(analytics_api.DATASET_FILE, "",
                               "No dataset has been built yet.",
                               content_type="application/json; charset=utf-8")
                return
            if self.path in ("/download/haptics", "/download/app"):
                # Frei zu haben, und vom Datentraeger gestreamt: das Paket ist rund
                # 64 MB, und drei gleichzeitige Abrufe waeren sonst drei Kopien im
                # Speicher dieses Servers.
                #
                # Unter dem DATIERTEN Namen: "fh-companion.zip" liegt nach dem
                # dritten Download dreimal gleich im Ordner, und niemand weiss mehr,
                # welche Fassung er gerade gestartet hat -- auch nicht, wenn er
                # danach fragt, ob ein Fehler behoben ist.
                paket = analytics_api.haptics_package()
                # Gezaehlt wird VOR dem Senden: ein abgebrochener Download ist
                # trotzdem ein Abruf, und wer die Zahl erst danach erhoeht,
                # zaehlt bei jedem Abbruch zu wenig.
                if paket is not None and paket.exists():
                    try:
                        usage.note_download()
                    except Exception:
                        pass
                    # JE FASSUNG, fuer immer (usage.py zaehlt nur je Tag, 40 Tage).
                    try:
                        if downloads.zaehlt(self.command, self.headers.get("Range")):
                            downloads.note("haptics", downloads.fassung_des_pakets(paket),
                                           self.headers.get("User-Agent"))
                    except Exception:
                        pass
                self.send_file(paket, paket_name(paket),
                               "No haptics package has been built yet. "
                               "Build one with: python scripts/build_haptics_package.py")
                return
            if self.path.startswith("/api/") or self.path == "/download/tool":
                # Die Seite kann veraltet sein, der Datensatz nicht: die App fragt
                # genau deshalb nach.
                rebuild_if_stale(root, page, builder, self.note)
                # Die Maschine mitgeben, wie beim Hochladen: das Werkzeug haengt
                # jetzt am selben Passwort, und dessen Sperre zaehlt je Maschine.
                status, content_type, payload = analytics_api.handle(
                    "GET", self.path, self.headers, b"",
                    client=self.client_address[0] if self.client_address else "")
                if self.path == "/download/tool" and status >= 400:
                    self.note(f"/download/tool abgelehnt ({status}) "
                              f"fuer {self.address_string()}")
                if (self.path == "/download/tool" and status == 200
                        and content_type == "application/zip"):
                    try:
                        if downloads.zaehlt(self.command, self.headers.get("Range")):
                            downloads.note("tool", downloads.fassung_aus_bytes(
                                payload, "forza-contrib-tool.zip"), self.headers.get("User-Agent"))
                    except Exception:
                        pass
                name = ("forza-contrib-tool.zip" if content_type == "application/zip"
                        else analytics_api.download_name(self.path) if status == 200
                        else None)
                self.send_api(status, content_type, payload, name)
                return
            if self.path == "/admin/world.json":
                # Die Weltkarte der Verwaltungsseite (scripts/build_world_map.py, Natural
                # Earth, gemeinfrei). Von hier und nicht von einem Kartendienst -- wie die
                # Schriften. Nichts Geheimes darin, also ohne Anmeldung.
                karte = Path(__file__).resolve().parent / "assets" / "world-110m.json"
                if not karte.is_file():
                    self.send_error(404)
                    return
                roh = karte.read_bytes()
                self.send_response(200)
                self.send_header("Content-Type", "application/json; charset=utf-8")
                self.send_header("Content-Length", str(len(roh)))
                self._lange_cachen = True
                self.end_headers()
                if self.command != "HEAD":
                    self.wfile.write(roh)
                return
            if self.path in ("/admin", "/admin/", "/admin.html"):
                # Aus dem Skriptverzeichnis ausgeliefert, nicht aus dem Ordner mit der
                # Auswertungsseite: die wird alle fuenf Boards neu gebaut, und die
                # Verwaltung hat dort nichts verloren. Sie ist reines HTML -- ohne das
                # Admin-Geheimnis kann sie nichts, und das steht nicht darin.
                admin = Path(__file__).resolve().parent / "admin_page.html"
                if not admin.exists():
                    self.send_error(404, "admin_page.html is missing")
                    return
                # STRENGE REGELN FUER DIE SEITE MIT DEM GEHEIMNIS. Sie laedt nichts
                # von fremden Adressen, spricht nur mit diesem Server und laesst
                # sich nicht in einen fremden Rahmen einbetten. Kommt trotz der
                # Maskierung je Skript hinein, darf es das Geheimnis nirgends
                # hinschicken.
                self.send_api(200, "text/html; charset=utf-8", admin.read_bytes(),
                              extra={"Content-Security-Policy":
                                     "default-src 'none'; script-src 'unsafe-inline'; "
                                     "style-src 'self' 'unsafe-inline'; font-src 'self'; "
                                     "connect-src 'self'; "
                                     "img-src 'self' data:; base-uri 'none'; "
                                     "form-action 'none'; frame-ancestors 'none'"})
                return
            if self.path in ("/app", "/app/", "/haptics", "/haptics/"):
                # Wie die anderen beiden aus dem Skriptverzeichnis: reines HTML, und
                # der Ordner der Auswertungsseite wird alle fuenf Boards neu gebaut.
                app_file = Path(__file__).resolve().parent / "app_page.html"
                if not app_file.exists():
                    self.send_error(404, "app_page.html is missing")
                    return
                # Der Name der Seite steht in config/local.json, nicht im Quelltext.
                import html as _html
                import local_settings
                seite = app_file.read_bytes().replace(
                    b"__SITE_TITLE__", _html.escape(local_settings.site_title()).encode("utf-8"))
                seite = seite.replace(b"__SOURCE_LINK__", quelltext_link())
                # Fuer die Links zu den Textanleitungen (docs/setup_*.md): nur eine
                # https-Adresse, in einem JS-String -- darum ohne Anfuehrungszeichen.
                quelle = local_settings.source_url() or ""
                if not quelle.startswith("https://") or any(z in quelle for z in "\"'<>\\"):
                    quelle = ""
                seite = seite.replace(b"__SOURCE_URL__", quelle.encode("utf-8"))
                self.send_api(200, "text/html; charset=utf-8", seite)
                return
            if self.path in ("/mitmachen", "/mitmachen/", "/contribute",
                             "/contribute.html"):
                # Wie die Admin-Seite aus dem Skriptverzeichnis: sie hat im Ordner
                # der Auswertungsseite nichts verloren, der wird alle fuenf Boards
                # neu gebaut. Reines HTML, kein Geheimnis darin -- der Upload wird
                # am Paket geprueft, nicht am Absender dieser Seite.
                page_file = Path(__file__).resolve().parent / "contribute_page.html"
                if not page_file.exists():
                    self.send_error(404, "contribute_page.html is missing")
                    return
                self.send_api(200, "text/html; charset=utf-8",
                              page_file.read_bytes().replace(b"__SOURCE_LINK__", quelltext_link()))
                return
            if self.path in ("/", "/index.html"):
                rebuild_if_stale(root, page, builder, self.note)
                self.send_response(302)
                self.send_header("Location", "/" + page.name)
                self.end_headers()
                return
            if self.path == "/status":
                rebuilt = newest_source(root)
                body = json.dumps({
                    "page": page.name,
                    "page_bytes": page.stat().st_size if page.exists() else 0,
                    "newest_board_data": rebuilt,
                }, indent=2).encode("utf-8")
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
                return
            # Ab hier liefert SimpleHTTPRequestHandler aus dem Verzeichnis der Seite.
            # Das ist `data/analytics/`, und dort liegt mehr als die Seite: Zwischen-
            # staende, Pruefausgaben, was auch immer sich ansammelt. Solange dieser
            # Server nur auf localhost lauschte, war das gleichgueltig; seit er auf
            # allen Adressen steht und der Router ihn durchreicht, ist jede Datei
            # darin oeffentlich. Also eine Erlaubnisliste statt eines Verzeichnisses:
            # was hier nicht steht, gibt es nach aussen nicht. Der Datensatz ist
            # absichtlich weiter zu haben -- ueber /api/dataset, wo er hingehoert.
            if self.path.lstrip("/") not in PUBLIC_FILES:
                self.send_error(404)
                return
            if self.path == "/" + page.name:
                rebuild_if_stale(root, page, builder, self.note)
            # No caching: the whole point is that a reload shows the newest scan.
            super().do_GET()

        def guess_type(self, path):
            # SimpleHTTPRequestHandler serves .html without a charset, so the browser
            # falls back to its locale guess and a UTF-8 page renders as "Ã¼ber".
            base = super().guess_type(path)
            if base.startswith("text/html") and "charset" not in base:
                return base + "; charset=utf-8"
            return base

        def end_headers(self):
            # Schriften aendern sich nie unter demselben Namen -- die duerfen im
            # Browser bleiben; alles andere wird bei jedem Laden frisch geholt.
            if getattr(self, "_lange_cachen", False):
                self.send_header("Cache-Control", "public, max-age=2592000, immutable")
            elif getattr(self, "_kurz_cachen", False):
                self.send_header("Cache-Control", "public, max-age=86400")
            else:
                self.send_header("Cache-Control", "no-store")
            # Fuer jede Antwort: keine geratenen Inhaltstypen, kein Einbetten in
            # fremde Seiten, keine Adresse im Referer nach draussen.
            self.send_header("X-Content-Type-Options", "nosniff")
            self.send_header("X-Frame-Options", "DENY")
            self.send_header("Referrer-Policy", "no-referrer")
            self.send_header("Permissions-Policy",
                             "camera=(), microphone=(), geolocation=(), usb=()")
            if isinstance(self.request, ssl.SSLSocket):
                self.send_header("Strict-Transport-Security", "max-age=15552000")
            super().end_headers()

        def version_string(self):
            # Kein "SimpleHTTP/0.6 Python/3.12.x" nach draussen -- das hilft nur
            # jemandem, der nach bekannten Luecken genau dieser Fassung sucht.
            return "forza-site"

    return Handler


class Server(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True
    allow_reuse_address = True
    # Gesetzt, wenn ein Zertifikat da ist -- siehe tls_context().
    tls: ssl.SSLContext | None = None

    def process_request_thread(self, request, client_address):
        """HTTP UND HTTPS AUF DEMSELBEN PORT.

        Die ausgelieferten Apps sprechen http://...:8787 an. Wuerde der Port auf
        HTTPS umgestellt, erreichte keine davon mehr den Server -- auch nicht,
        um das Update zu holen, das sie auf HTTPS umstellt. Also wird am ersten
        Byte unterschieden: 0x16 ist der Beginn eines TLS-Handschlags, alles
        andere ist eine HTTP-Anfrage im Klartext.

        HIER, im Faden der Anfrage, und nicht beim Annehmen: dort haette ein
        Gegenueber, das den Handschlag beginnt und dann schweigt, jede weitere
        Verbindung aufgehalten. Zehn Sekunden fuer beides, dann ist Schluss.
        """
        if self.tls is not None:
            try:
                request.settimeout(10)
                erstes = request.recv(1, socket.MSG_PEEK)
                if erstes == b"\x16":
                    request = self.tls.wrap_socket(request, server_side=True)
                request.settimeout(None)
            except (ssl.SSLError, OSError):
                self.shutdown_request(request)
                return
        super().process_request_thread(request, client_address)

    def shutdown_request(self, request):
        """TLS SAUBER BEENDEN. SSLSocket.shutdown() wirft die TLS-Schicht weg, ohne
        close_notify zu senden; strenge Gegenueber (curl mit Schannel) melden dann
        "server closed abruptly" und koennen eine vollstaendige Antwort nicht von
        einer abgeschnittenen unterscheiden. unwrap() sendet es. Auf die Antwort
        des Gegenuebers wird hoechstens zwei Sekunden gewartet -- der Platz in
        GLEICHZEITIG ist da schon wieder frei."""
        if isinstance(request, ssl.SSLSocket):
            try:
                request.settimeout(2)
                request.unwrap()
            except (ssl.SSLError, OSError, ValueError):
                pass
        super().shutdown_request(request)

    def handle_error(self, request, client_address):
        # Abgebrochene Handschlaege und Verbindungen sind im Internet Alltag;
        # sie sollen das Protokoll nicht mit Rueckverfolgungen fluten.
        exc = sys.exc_info()[1]
        if isinstance(exc, (ssl.SSLError, ConnectionError, TimeoutError, OSError)):
            return
        super().handle_error(request, client_address)


def tls_context(cert: Path | None, key: Path | None, log) -> ssl.SSLContext | None:
    """Ein TLS-Kontext, wenn Zertifikat und Schluessel da sind -- sonst None.

    Fehlt beides, laeuft der Server wie bisher nur mit HTTP. So kann der Code
    ausgeliefert werden, bevor es ein Zertifikat gibt, und schaltet sich ein,
    sobald die Zertifikatsaufgabe eines abgelegt hat.
    """
    if not cert or not key or not cert.exists() or not key.exists():
        return None
    try:
        ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        ctx.minimum_version = ssl.TLSVersion.TLSv1_2
        ctx.load_cert_chain(str(cert), str(key))
        log(f"TLS an: {cert}")
        return ctx
    except (ssl.SSLError, OSError) as error:
        log(f"TLS NICHT an, Zertifikat unbrauchbar: {error}")
        return None


def paket_name(paket) -> str:
    """Der Dateiname, unter dem das Paket beim Nutzer ankommt.

    Ausgeliefert wird immer `fh-companion-latest.zip` -- das ist die eine
    Stelle, an der "das Neueste" steht. Unter diesem Namen zu SPEICHERN waere aber
    unbrauchbar: nach dem dritten Download liegen drei gleichnamige Dateien im
    Ordner, und niemand weiss mehr, welche Fassung gerade laeuft. Der Name traegt
    darum das Datum der ausgelieferten Datei selbst -- nicht das eines gleichnamigen
    Nachbarn, der zufaellig daneben liegt.
    """
    if paket is None:
        return "fh-companion.zip"
    try:
        from datetime import datetime
        tag = datetime.fromtimestamp(paket.stat().st_mtime).strftime("%Y%m%d")
        return f"fh-companion-{tag}.zip"
    except OSError:
        return paket.name


def main(argv: list[str] | None = None) -> int:
    workspace = Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path,
                        default=workspace / "data/memory_scans/full_sweep")
    parser.add_argument("--page", type=Path,
                        default=workspace / "data/analytics/rivals_auto_wertung.html")
    parser.add_argument("--builder", type=Path,
                        default=Path(__file__).resolve().parent / "build_analytics_site.py")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8787)
    parser.add_argument("--nur-ausliefern", action="store_true",
                        help="Nie bauen, nur die vorhandene Seite ausliefern "
                             "(fuer den Spiegel auf dem GNAS)")
    parser.add_argument("--tls-cert", type=Path, default=None,
                        help="Zertifikatskette (PEM); zusammen mit --tls-key schaltet "
                             "HTTPS auf demselben Port zu")
    parser.add_argument("--tls-key", type=Path, default=None)
    args = parser.parse_args(argv)

    global NUR_AUSLIEFERN
    NUR_AUSLIEFERN = args.nur_ausliefern

    args.page.parent.mkdir(parents=True, exist_ok=True)
    log = lambda message: (sys.stdout.write(f"[serve] {message}\n"), sys.stdout.flush())
    rebuild_if_stale(args.root, args.page, args.builder, lambda m: log(m))

    handler = make_handler(args.root, args.page, args.builder)
    with Server((args.host, args.port), handler) as server:
        server.tls = tls_context(args.tls_cert, args.tls_key, log)
        if args.tls_cert and server.tls is not None:
            # Erneuerte Zertifikate ohne Neustart: stuendlich nachsehen.
            def nachladen(stand=[args.tls_cert.stat().st_mtime]):
                while True:
                    time.sleep(3600)
                    try:
                        jetzt = args.tls_cert.stat().st_mtime
                    except OSError:
                        continue
                    if jetzt != stand[0]:
                        neu = tls_context(args.tls_cert, args.tls_key, log)
                        if neu is not None:
                            server.tls = neu
                            stand[0] = jetzt
            threading.Thread(target=nachladen, daemon=True).start()
        # Die Laenderdatenbank holt sich der Dienst selbst, ohne den Start aufzuhalten.
        geoip.refresh_in_background(log)
        # Die Autoliste ("Car collection" in der App) ebenso, einmal am Tag -- nur im
        # Spiegelbetrieb: auf dem Spielrechner soll der Server nicht von selbst ins Netz.
        if NUR_AUSLIEFERN:
            import car_availability
            car_availability.im_hintergrund(log)
        # BEIM BEENDEN SICHERN. `docker compose restart` schickt SIGTERM; ohne eigenen
        # Handler endet Python dann sofort, und die Besuche der letzten Sekunden
        # (visits.py schreibt hoechstens alle 15 s) waeren weg.
        import atexit
        import signal
        atexit.register(visits.flush)
        def beenden(*_):
            raise SystemExit(0)

        try:
            signal.signal(signal.SIGTERM, beenden)
        except (ValueError, AttributeError):
            pass
        log(f"http://{args.host}:{args.port}/  ->  {args.page.name}")
        log(f"boards from {args.root}" if not NUR_AUSLIEFERN
            else "Spiegelbetrieb: es wird nichts gebaut")
        log("Strg+C beendet den Server")
        try:
            server.serve_forever()
        except (KeyboardInterrupt, SystemExit):
            log("beendet")
        finally:
            visits.flush()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
