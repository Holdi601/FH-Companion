"""Das Siegel eines angenommenen App-Laufs (seit 2026-10-11).

Der Server nimmt einen Scan an, prueft ihn und legt ihn ab (scan_submissions.store). Was
danach mit den Dateien geschieht -- auf der Platte des Servers, unterwegs zum Rechner, der
den Datensatz baut, oder dort --, soll nichts mehr an ihnen aendern koennen, ohne dass es
auffaellt. Darum schreibt der Server neben die drei Dateien ein Siegel: ein HMAC-SHA256
ueber den Namen des Laufs und die Pruefsummen von state.json, rows.jsonl und dem Parquet,
mit einem Schluessel, den nur der Server und der Baurechner kennen ("run_seal" in
config/contrib_keys.json, nie im Repository). Der Datensatzbau nimmt einen App-Lauf nur,
wenn das Siegel passt.

Der Name gehoert mit hinein: sonst liesse sich ein gepruefter Lauf unter einem anderen
Namen ablegen, etwa an die Stelle eines ausgeblendeten.
"""

from __future__ import annotations

import hashlib
import hmac
import json
from pathlib import Path

DATEIEN = ("state.json", "rows.jsonl", "leaderboard_entries.parquet")
SIEGEL = "seal.json"
FASSUNG = 1


def _inhalt(run_dir: Path, name: str | None = None) -> bytes:
    teile = ["fhc-run-seal-v%d" % FASSUNG, name or run_dir.name]
    for datei in DATEIEN:
        teile.append(datei + ":" + hashlib.sha256((run_dir / datei).read_bytes()).hexdigest())
    return "\n".join(teile).encode("utf-8")


def siegeln(run_dir: Path, schluessel: str) -> None:
    """Das Siegel schreiben. Wirft, wenn eine der drei Dateien fehlt oder kein Schluessel da ist."""
    siegeln_als(run_dir, run_dir.name, schluessel)


def siegeln_als(run_dir: Path, name: str, schluessel: str) -> None:
    """Wie siegeln(), fuer einen Ordner, der erst noch `name` heissen wird."""
    if not schluessel:
        raise ValueError("no run_seal key")
    wert = hmac.new(schluessel.encode("utf-8"), _inhalt(run_dir, name), hashlib.sha256).hexdigest()
    (run_dir / SIEGEL).write_text(json.dumps({"v": FASSUNG, "hmac": wert}), encoding="utf-8")


def pruefen(run_dir: Path, schluessel: str) -> str | None:
    """None, wenn das Siegel passt -- sonst warum nicht. Wirft nie."""
    if not schluessel:
        return "no run_seal key on this machine"
    try:
        d = json.loads((run_dir / SIEGEL).read_text(encoding="utf-8"))
    except FileNotFoundError:
        return "no seal"
    except Exception:
        return "unreadable seal"
    try:
        soll = hmac.new(schluessel.encode("utf-8"), _inhalt(run_dir), hashlib.sha256).hexdigest()
    except FileNotFoundError as e:
        return "missing file %s" % Path(str(e.filename)).name
    except Exception:
        return "unreadable run"
    if not isinstance(d, dict) or not hmac.compare_digest(str(d.get("hmac") or ""), soll):
        return "seal does not match the files"
    return None


def schluessel_aus(keys_file: Path) -> str:
    """Der Siegelschluessel aus config/contrib_keys.json -- oder "", wenn keiner da ist."""
    try:
        return str(json.loads(keys_file.read_text(encoding="utf-8-sig")).get("run_seal") or "")
    except Exception:
        return ""
