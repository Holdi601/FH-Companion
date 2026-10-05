"""Boards auf DIESEM Rechner scannen und das Ergebnis abgeben.

Das ist das Werkzeug fuer jemanden, der Forza selbst laufen hat und beitragen will.

    python scripts/contrib_scan.py --classes A --route-indices 0-4
    python scripts/contrib_scan.py --pack-only          # nur einpacken, nicht scannen
    python scripts/contrib_scan.py --upload             # danach hochladen

Es benutzt **denselben Scanner** wie der Hauptrechner (`ocr_board_sweep`), nur mit
`--local`: die Bilder kommen vom eigenen Bildschirm statt aus einer VM. Alles, was
der Scanner ueber Bestenlisten gelernt hat -- wo der gueltige Teil endet, wie die
Boardlaenge aus dem Scrollbalken faellt, wann ein Lesevorgang abgebrochen ist --
gilt hier unveraendert.

## Was der Rechner waehrenddessen macht

Er gehoert dem Scanner. Das Werkzeug drueckt Pfeiltasten und filmt den Bildschirm;
beides geht an das Fenster im Vordergrund. Wer nebenher etwas anderes anklickt,
schickt die Tasten dorthin und der Lauf liest eine Liste, die stillsteht. Also:
starten, Finger weg, wiederkommen.

## Was abgegeben wird

Nur Zeilen. Keine Bilder -- die waeren je Board zwischen 266 MB und 2,2 GB, und sie
werden nach dem Lesen ohnehin geloescht. Eine fertige Abgabe ist rund 5 MB je Board
und enthaelt Rang, Rundenzeit, Auto-Kennung, Spielername und die Kennzeichen des
Laufs.
"""

from __future__ import annotations

import argparse
import json
import ssl
import sys
import urllib.error
import urllib.request
from datetime import datetime
from pathlib import Path

HERE = Path(__file__).resolve().parent
WORKSPACE = HERE.parent
sys.path.insert(0, str(HERE))
# Im Arbeitsbaum liegen contrib_format und local_settings in server/; im Paket
# liegen sie flach neben diesem Skript, und den Ordner gibt es dort nicht.
if (WORKSPACE / "server").is_dir():
    sys.path.insert(1, str(WORKSPACE / "server"))

import contrib_format as fmt
import i18n
from i18n import T  # noqa: E402
import local_target  # noqa: E402
import local_settings  # noqa: E402

CONFIG_FILE = WORKSPACE / "config/contrib_config.json"
SCAN_ROOT = WORKSPACE / "data/contrib_runs"
OUT_DIR = WORKSPACE / "data/contrib_out"
TOOL = "contrib_scan/1"
# HTTPS seit 2026-09-25. Wer noch die alte Vorgabe in seiner contrib.json hat,
# bekommt beim Laden die neue; eine selbst eingetragene Adresse bleibt.
# Die Adresse kommt aus config/local.json -- im Paket legt der Bau sie dort ab.
DEFAULT_SERVER = local_settings.server()
OLD_DEFAULT_SERVER = local_settings.old_server()


def load_config(path: Path) -> dict:
    if not path.exists():
        raise SystemExit(
            T("{path} is missing.").format(path=path) + "\n"
            + T("It holds the details you were given:") + "\n"
            '  {"contributor": "...", "secret": "...", "upload_password": "...",\n'
            '   "server": "' + (DEFAULT_SERVER or "https://your-server.example.org:8787") + '"}')
    data = json.loads(path.read_text(encoding="utf-8-sig"))
    for key in ("contributor", "secret"):
        if not str(data.get(key) or "").strip():
            raise SystemExit(T("{path}: {key} is missing").format(
                path=path, key=key))
    fmt.check_contributor(data["contributor"])
    if OLD_DEFAULT_SERVER and str(data.get("server") or "").strip().rstrip("/") == OLD_DEFAULT_SERVER:
        data["server"] = DEFAULT_SERVER
    return data


def _tls_kontext() -> ssl.SSLContext:
    """Die Stammzertifikate von Windows UND, falls da, die von certifi.

    Windows laedt manche Stammzertifikate erst nach, wenn ein Browser sie braucht.
    Python fragt nur, was schon im Speicher liegt -- auf einem Rechner, der noch nie
    eine Seite mit ISRG Root X2 geoeffnet hat, scheiterte die Pruefung sonst,
    obwohl das Zertifikat gueltig ist. certifi (in requirements.txt) bringt die
    Liste von Mozilla mit, und in der steht es.
    """
    kontext = ssl.create_default_context()
    try:
        import certifi
        kontext.load_verify_locations(certifi.where())
    except Exception:
        pass
    return kontext


def _tls_gescheitert(error: Exception) -> bool:
    """Nur ein gescheiterter TLS-Handschlag -- keine Ablehnung, keine Zeitueberschreitung."""
    if isinstance(error, urllib.error.HTTPError):
        return False
    grund = getattr(error, "reason", error)
    return isinstance(grund, ssl.SSLError) or isinstance(error, ssl.SSLError)


def preflight() -> list[str]:
    """Was diesem Lauf im Weg steht -- bevor er eine halbe Stunde verbraucht.

    Lieber vorher meckern als hinterher ein leeres Board abliefern. Jede Meldung hier
    ist ein Fall, der im VM-Betrieb schon einmal eine Nacht gekostet hat.
    """
    trouble: list[str] = []
    try:
        import subprocess
        running = subprocess.run(
            ["powershell.exe", "-NoProfile", "-Command",
             "@(Get-Process -Name 'forzahorizon6' -ErrorAction SilentlyContinue).Count"],
            capture_output=True, text=True, timeout=60).stdout
        if not "".join(c for c in running if c.isdigit()).strip("0"):
            trouble.append(
                "Forza laeuft nicht. Das Spiel starten und ins Hauptmenue bringen.")
    except Exception:
        trouble.append("Konnte nicht pruefen, ob Forza laeuft.")

    # JEDE AUFLOESUNG AB 720p. Frueher wurde hier alles ausser 1920x1080 abgelehnt,
    # weil die Tabellenspalten dort vermessen sind. Heute wird die Spielflaeche in
    # diesen Bezugsrahmen gebracht (screen_reader.grab_game) -- verlangt ist nur
    # noch, dass die Schrift gross genug zum Lesen ist.
    try:
        import screen_reader
        flaeche = screen_reader.game_area()
        if flaeche is not None:
            x, y, w, h = screen_reader.sixteen_nine(flaeche)
            if w < 1280 or h < 720:
                trouble.append(T(
                    "The game area is {0}x{1}. Below 1280x720 the leaderboard text is "
                    "too small to read reliably -- use a larger window or resolution.")
                    .format(w, h))
            else:
                if (w, h) != (flaeche[2], flaeche[3]):
                    print(T("Note: the game area is {0}x{1}, not 16:9. The scan uses its "
                            "centred 16:9 part; menu positions at this aspect ratio are "
                            "an assumption.").format(flaeche[2], flaeche[3]))
                frame = screen_reader.grab_game(flaeche)
                print(T("Game area {0}x{1} at {2},{3}, read at 1920x1080 (scale {4:.2f}).")
                      .format(flaeche[2], flaeche[3], flaeche[0], flaeche[1], w / 1920))
        else:
            # Kein Fenster gefunden: das meldet schon die Prozesspruefung oben,
            # wenn das Spiel nicht laeuft. Laeuft es, genuegt der Hauptschirm.
            screen_reader.grab_game()
    except Exception as error:
        trouble.append(f"Bildschirmaufnahme geht nicht ({error}).")

    # TESSERACT, UND ZWAR JETZT. Es ist ein eigenstaendiges Programm, kein
    # pip-Paket -- requirements.txt kann es nicht mitbringen, und die Einrichtung
    # in start.cmd merkt sein Fehlen nicht. Ohne diese Pruefung faellt es erst auf,
    # wenn die Bilder einer halben Stunde schon aufgenommen sind.
    try:
        from ocr_leaderboard_frames import finde_tesseract
        if not finde_tesseract():
            trouble.append(T(
                "Tesseract OCR is not installed. The leaderboard rows are read "
                "with it, and it is a separate program -- not a Python package. "
                "Install it from https://github.com/UB-Mannheim/tesseract/wiki "
                "or with: winget install -e --id UB-Mannheim.TesseractOCR"))
    except Exception as error:
        trouble.append(f"Konnte nicht pruefen, ob Tesseract da ist ({error}).")

    return trouble


def collect_runs(root: Path, since: float) -> dict[str, tuple[dict, str]]:
    """Die Laeufe einsammeln, die dieser Aufruf erzeugt hat.

    `since` haelt aeltere Laeufe heraus: wer zweimal scannt und zweimal abgibt, soll
    beim zweiten Mal nicht den ersten Lauf noch einmal mitschicken -- der Server
    wuerde ihn als schon vorhanden abweisen und die ganze Abgabe mit ihm.
    """
    runs: dict[str, tuple[dict, str]] = {}
    for state_path in sorted(root.glob("*/state.json")):
        if state_path.stat().st_mtime < since:
            continue
        rows_path = state_path.parent / "rows.jsonl"
        if not rows_path.exists():
            continue
        state = json.loads(state_path.read_text(encoding="utf-8-sig"))
        runs[state_path.parent.name] = (state, rows_path.read_text(encoding="utf-8"))
    return runs


def pack(config: dict, runs: dict[str, tuple[dict, str]], note: str) -> Path:
    entries = []
    for run_id, (state, rows_text) in runs.items():
        fmt.check_run_id(run_id)
        state_bytes = json.dumps(state, indent=1, ensure_ascii=False).encode("utf-8")
        entries.append({
            "run_id": run_id,
            "state_sha256": fmt.sha256_bytes(state_bytes),
            "rows_sha256": fmt.sha256_bytes(rows_text.encode("utf-8")),
            "rows": sum(1 for line in rows_text.splitlines() if line.strip()),
        })
    manifest = fmt.build_manifest(
        config["contributor"], entries, tool=TOOL,
        created_at=datetime.now().astimezone().isoformat(timespec="seconds"),
        note=note)
    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    target = OUT_DIR / f"forza-contrib-{config['contributor']}-{stamp}.zip"
    return fmt.write_archive(target, manifest, config["secret"], runs,
                             gate_secret=config.get("upload_password") or None)


def upload(archive: Path, server: str, gate: str = "") -> bool:
    url = server.rstrip("/") + "/api/contribute"
    data = archive.read_bytes()
    headers = {"Content-Type": "application/zip",
               "Content-Length": str(len(data))}
    # Das Zugangspasswort auch als KOPFZEILE, nicht nur als gate.txt im Paket.
    # Seit der Upload-Bereich oeffentlich erreichbar ist, prueft der Server einen
    # zweiten, getippten Faktor -- sonst reicht eine weitergegebene ZIP, denn das
    # Passwort liegt darin. Das Werkzeug hat es in seiner Konfiguration und schickt
    # es einfach mit; ein Mensch im Browser tippt es ein.
    if gate:
        headers["X-Forza-Upload-Password"] = gate

    def senden(ziel: str) -> dict:
        request = urllib.request.Request(ziel, data=data, method="POST",
                                         headers=headers)
        with urllib.request.urlopen(request, timeout=300,
                                    context=_tls_kontext()) as response:
            return json.loads(response.read().decode("utf-8"))

    try:
        try:
            answer = senden(url)
        except Exception as error:
            # RUECKFALL NUR BEI GESCHEITERTEM HANDSCHLAG: dann hat der Server die
            # Anfrage nie gesehen, und HTTP ist der Weg, der bis 2026-09-24 der
            # einzige war. Die Beitraege sind unterschrieben (contrib_format), eine
            # veraenderte ZIP nimmt der Server nicht an.
            if not (url.startswith("https://") and _tls_gescheitert(error)):
                raise
            print("  " + T("The server's certificate could not be checked on this PC "
                           "-- sending over plain http, as before."))
            answer = senden("http://" + url[len("https://"):])
        print("  " + T("accepted: {rows} rows in {runs} run(s)").format(
            rows=answer.get("rows", 0), runs=len(answer.get("runs", []))))
        return True
    except urllib.error.HTTPError as error:
        # Der Server begruendet jede Ablehnung -- die Begruendung ist das Einzige, was
        # hier weiterhilft, also darf sie nicht hinter "HTTP 403" verschwinden.
        try:
            detail = json.loads(error.read().decode("utf-8")).get("error", "")
        except Exception:
            detail = ""
        print("  " + T("rejected ({code}): {detail}").format(
            code=error.code, detail=detail or error.reason))
        return False
    except Exception as error:
        print("  " + T("not uploaded: {error}").format(error=error))
        print("  " + T("The file is here, and can be sent by hand:")
              + "\n    " + str(archive))
        return False


def main(argv: list[str] | None = None) -> int:
    # Ohne das werden uebersetzte Ausgaben auf einer
    # Windows-Konsole zu Fragezeichen.
    i18n.konsole_auf_utf8()
    parser = argparse.ArgumentParser(
        # NICHT __doc__: die Moduldokumentation ist deutsch und fuer den
        # geschrieben, der den Quelltext liest. `--help` liest der Freund, der das
        # Werkzeug bekommen hat -- und der bekommt es auf Englisch.
        description=T("Scan leaderboards on THIS PC and hand in the result."),
        epilog=T("Examples:") + """
  contrib_scan.py --classes A --route-indices 0-4
  contrib_scan.py --pack-only      """ + T("pack what is there, do not scan") + """
  contrib_scan.py --upload         """ + T("send it afterwards"),
        formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--config", type=Path, default=CONFIG_FILE)
    parser.add_argument("--classes", default="A",
                        help=T("comma separated, e.g. D,C,B,A,S1,S2,R"))
    parser.add_argument("--route-indices", default="0-3",
                        help=T("e.g. 0-3 or 5,7,9 -- the order of the carousel."))
    # type=str.strip: die Kategorie kommt ueber mehrere Schalen
    # (start.cmd -> cmd.exe -> python), und jede kann ein Leerzeichen
    # anhaengen. Einmal hier saeubern ist billiger als an jeder Vergleichsstelle.
    parser.add_argument("--category", default="Road Racing", type=str.strip)
    parser.add_argument("--row-cap", type=int, default=20000)
    parser.add_argument("--chunk-seconds", type=int, default=120)
    parser.add_argument("--max-chunks", type=int, default=12)
    parser.add_argument("--workers", type=int, default=4,
                        help=T("OCR readers at once. More is faster, but the "
                               "reader pool has already crashed at twelve "
                               "on the main machine."))
    parser.add_argument("--note", default="",
                        help=T("a note for whoever receives this"))
    parser.add_argument("--pack-only", action="store_true",
                        help=T("do not scan, only pack what is already there"))
    parser.add_argument("--upload", action="store_true")
    parser.add_argument("--skip-checks", action="store_true")
    args = parser.parse_args(argv)

    config = load_config(args.config)

    # Derselbe Riegel wie im Sweep, aber hier zaehlt er doppelt: wer das Werkzeug
    # bekommt, kann eine falsch gefuellte Kategorie nicht erkennen und wuerde eine
    # Stunde scannen, um Zeilen abzugeben, die unter der falschen Strecke stehen.
    import ocr_board_sweep
    refusal = ocr_board_sweep.refuse_unverified_category(args.category)
    if refusal:
        print(refusal)
        return 2

    OUT_DIR.mkdir(parents=True, exist_ok=True)
    SCAN_ROOT.mkdir(parents=True, exist_ok=True)
    started = 0.0

    if not args.pack_only:
        if not args.skip_checks:
            trouble = preflight()
            if trouble:
                print(T("This cannot start yet:"))
                for line in trouble:
                    print(f"  - {line}")
                print("\n" + T("If you are sure it works anyway: --skip-checks"))
                return 1

        print(T("This PC now belongs to the scanner. Do not click, do not type,"))
        print(T("and leave Forza in the foreground. Ctrl+C stops it.") + "\n")
        import time
        started = time.time()

        import ocr_board_sweep
        code = ocr_board_sweep.main([
            "--local",
            "--category", args.category,
            "--route-indices", args.route_indices,
            "--classes", args.classes,
            "--row-cap", str(args.row_cap),
            "--chunk-seconds", str(args.chunk_seconds),
            "--max-chunks", str(args.max_chunks),
            "--until-hour", "20",
            # Vier statt der zwoelf, die hier ueblich sind. Auf dem Hauptrechner
            # sind zwoelf Leser schon einmal mitten im Lauf gestorben
            # (BrokenProcessPool bei 14 GB freiem Speicher) -- ein Absturz, keine
            # Last. Auf einem fremden Rechner, dessen Ausstattung niemand kennt,
            # ist Vorsicht mehr wert als die halbe Stunde, die es kostet.
            "--workers", str(args.workers),
            "--out-root", str(SCAN_ROOT),
            "--progress-file", str(SCAN_ROOT / "progress.json"),
        ])
        if code != 0:
            print("\n" + T("The scanner ended with code {code}. Whatever "
                             "finished is packed anyway.").format(code=code))

    runs = collect_runs(SCAN_ROOT, started)
    if not runs:
        print(T("Nothing to pack -- no board was finished."))
        return 1

    archive = pack(config, runs, args.note)
    total = sum(sum(1 for line in text.splitlines() if line.strip())
                for _state, text in runs.values())
    print("\n" + T("Done: {runs} run(s), {rows} rows").format(
        runs=len(runs), rows=total))
    for run_id, (state, _text) in sorted(runs.items()):
        print("  {0} {1}  ".format(state.get("track"),
                                  state.get("performance_class"))
              + T("{rows} rows").format(rows=state.get("rows_collected"))
              + "  [{0}]".format(state.get("end_status")))
    print(f"\n  {archive}  ({archive.stat().st_size / 1e6:.1f} MB)")

    if args.upload:
        server = config.get("server") or ""
        if not server:
            print("  " + T("No server in the configuration -- send the file "
                          "by hand."))
            return 0
        print("\n" + T("Uploading to {server} ...").format(server=server))
        upload(archive, server, config.get("upload_password") or "")
    else:
        print("\n  " + T("To upload: --upload, or just send the file."))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
