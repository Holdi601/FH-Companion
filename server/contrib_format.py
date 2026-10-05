"""Das Format, in dem jemand anderes Ergebnisse abgibt -- und wie es beglaubigt wird.

Von beiden Seiten benutzt: das Werkzeug beim Freund packt und signiert damit, der
Server und `import_contrib.py` pruefen damit.

## Aufbau der ZIP

    manifest.json          Beitragender, Werkzeugversion, Zeit, und je Lauf ein SHA-256
    runs/<run_id>/state.json
    runs/<run_id>/rows.jsonl
    signature.txt          HMAC-SHA256 ueber die Bytes von manifest.json, hex
    gate.txt               dasselbe, aber mit dem globalen Zugangspasswort

Die Signatur deckt nur das Manifest -- aber das Manifest deckt mit seinen Hashes jede
Datei. Wer eine Zeile aendert, bricht einen Hash; wer den Hash mitaendert, bricht die
Signatur. Ein gemeinsames Geheimnis reicht dafuer, und es wird **nie uebertragen**.

## Zwei Unterschriften, zwei Zwecke

`signature.txt` sagt **wer**: das Geheimnis gehoert einem Beitragenden, und wird es
zurueckgezogen, betrifft das nur ihn.

`gate.txt` sagt **ob ueberhaupt**: ein einziges, langes Passwort, das in jedem
ausgegebenen Paket steckt. Es neu zu erzeugen macht in einem Schlag jedes bisher
verteilte Paket ungueltig -- der Notschalter, wenn ein Paket irgendwo auftaucht wo es
nicht hingehoert. Es wandert genauso wenig durchs Netz wie das andere: es wird
gerechnet, nicht gezeigt.

Beide werden geprueft, und in dieser Reihenfolge: erst das Tor, dann die Person. So
bekommt jemand mit einem veralteten Paket die Antwort "hol dir das neue Paket" und
nicht "deine Unterschrift ist falsch" -- zwei sehr verschiedene Probleme.

## Was hier NICHT drin ist, mit Absicht

**Kein Parquet.** Der Bestand wird aus Parquet-Dateien gebaut, aber eine fremde
Binaerdatei anzunehmen hiesse, einen Parser mit fremden Bytes zu fuettern. Die ZIP
traegt nur Text; das Parquet entsteht beim Import hier, aus den geprueften Zeilen.

**Keine Bilder.** Ein Chunk roher Aufnahmen ist 266 MB bis 2,2 GB. Der Freund OCRt
selbst, und abgegeben werden die Zeilen -- rund 5 MB fuer ein volles Board.
"""

from __future__ import annotations

import hashlib
import hmac
import io
import json
import re
import zipfile
from pathlib import Path

FORMAT_VERSION = 1

# Ein Laufname wird zu einem Verzeichnisnamen. Er darf darum nichts enthalten, was aus
# dem Zielverzeichnis herausfuehrt -- kein Schraegstrich, kein "..", kein Doppelpunkt.
RUN_ID_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_.-]{0,119}$")
CONTRIBUTOR_RE = re.compile(r"^[a-z0-9][a-z0-9_-]{1,31}$")

# Obergrenzen. Ein Board hat rund 20.000 Zeilen; 60.000 laesst Luft und stoppt eine
# ZIP, die den Rechner vollschreiben will.
MAX_ROWS_PER_RUN = 60_000
MAX_RUNS_PER_ZIP = 200
MAX_UNPACKED_BYTES = 512 * 1024 * 1024

REQUIRED_STATE_KEYS = ("run_id", "track", "performance_class", "rivals_mode")


class ContribError(Exception):
    """Etwas an der Abgabe stimmt nicht. Die Meldung ist fuer Menschen gedacht."""


def canonical(manifest: dict) -> bytes:
    """Die Bytes, ueber die signiert wird. Sortiert, damit beide Seiten dieselben sehen."""
    return json.dumps(manifest, sort_keys=True, separators=(",", ":"),
                      ensure_ascii=False).encode("utf-8")


def sign(manifest: dict, secret: str) -> str:
    return hmac.new(secret.encode("utf-8"), canonical(manifest),
                    hashlib.sha256).hexdigest()


def verify(manifest: dict, secret: str, signature: str) -> bool:
    return hmac.compare_digest(sign(manifest, secret), (signature or "").strip())


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def check_run_id(run_id: str) -> str:
    if not RUN_ID_RE.match(run_id or ""):
        raise ContribError(f"unusable run name: {run_id!r}")
    return run_id


def check_contributor(name: str) -> str:
    if not CONTRIBUTOR_RE.match(name or ""):
        raise ContribError(
            f"unusable contributor name: {name!r} "
            "(lowercase, 2-32 characters, only a-z 0-9 _ -)")
    return name


def validate_rows(raw: str, run_id: str) -> list[dict]:
    """Die Zeilen eines Laufs pruefen und zurueckgeben.

    Streng, weil diese Zahlen spaeter in einer Rangliste landen: ein Rang ist eine
    positive ganze Zahl, eine Rundenzeit liegt zwischen 5 Sekunden und zwei Stunden.
    Was das nicht erfuellt, faellt nicht still weg -- es bricht den Import ab, damit
    niemand halb eingelesene Daten fuer vollstaendig haelt.
    """
    rows: list[dict] = []
    seen: set[int] = set()
    for number, line in enumerate(raw.splitlines(), start=1):
        line = line.strip()
        if not line:
            continue
        if len(rows) >= MAX_ROWS_PER_RUN:
            raise ContribError(f"{run_id}: more than {MAX_ROWS_PER_RUN} rows")
        try:
            row = json.loads(line)
        except Exception as error:
            raise ContribError(
                f"{run_id}, line {number}: not JSON ({error})") from None
        if not isinstance(row, dict):
            raise ContribError(f"{run_id}, line {number}: not an object")
        try:
            rank = int(row["rank"])
            seconds = float(row["lap_time_seconds"])
        except Exception:
            raise ContribError(
                f"{run_id}, line {number}: rank and lap_time_seconds are missing "
                "or are not numbers") from None
        if rank < 1 or rank > 10_000_000:
            raise ContribError(f"{run_id}, line {number}: rank {rank} out of range")
        if not 5.0 <= seconds <= 7200.0:
            raise ContribError(
                f"{run_id}, line {number}: lap time {seconds}s out of range")
        if rank in seen:
            raise ContribError(f"{run_id}: rank {rank} appears twice")
        seen.add(rank)
        rows.append(row)
    if not rows:
        raise ContribError(f"{run_id}: no rows")
    return rows


def validate_state(state: dict, run_id: str) -> dict:
    if not isinstance(state, dict):
        raise ContribError(f"{run_id}: state.json is not an object")
    for key in REQUIRED_STATE_KEYS:
        if not str(state.get(key) or "").strip():
            raise ContribError(f"{run_id}: state.json without {key}")
    if str(state["run_id"]) != run_id:
        raise ContribError(
            f"{run_id}: state.json calls itself {state['run_id']!r} -- the name does not match")
    return state


def build_manifest(contributor: str, runs: list[dict], *, tool: str,
                   created_at: str, note: str = "") -> dict:
    """runs: [{"run_id":..., "state_sha256":..., "rows_sha256":..., "rows":n}, ...]"""
    return {
        "format": FORMAT_VERSION,
        "contributor": check_contributor(contributor),
        "tool": tool,
        "created_at": created_at,
        "note": note[:500],
        "runs": sorted(runs, key=lambda entry: entry["run_id"]),
    }


def read_archive(path_or_bytes, secret_for=None, gate_secret: str | None = None
                 ) -> tuple[dict, str, dict[str, tuple[dict, list[dict]]]]:
    """Eine Abgabe oeffnen und alles darin pruefen -- ohne irgendetwas zu schreiben.

    Gibt (manifest, signatur, {run_id: (state, rows)}) zurueck. Entpackt wird NICHTS
    auf die Platte: die Namen aus dem Archiv werden nie als Pfad benutzt, sondern aus
    den geprueften Namen im Manifest zusammengesetzt. Damit laeuft "zip slip" ins Leere.

    `secret_for` ist eine Funktion Beitragender -> Geheimnis. Ist sie da, wird die
    **Signatur zuerst** geprueft und erst danach ueberhaupt eine Lauf-Datei gelesen.
    Diese Reihenfolge ist Absicht: alles nach dem Manifest ist fremder Inhalt, und
    fremden Inhalt zu zerlegen, bevor feststeht dass er von jemandem stammt den man
    kennt, ist genau der Schritt zu viel. Ohne `secret_for` (zum Ansehen einer Datei,
    die man selbst gebaut hat) bleibt sie unbeglaubigt -- dann ist das dem Aufrufer
    bewusst.
    """
    source = path_or_bytes
    if isinstance(source, (bytes, bytearray)):
        source = io.BytesIO(source)
    try:
        archive = zipfile.ZipFile(source)
    except Exception as error:
        # Alles, was hier ankommt, kommt von aussen. Eine Ausnahme, die nicht
        # ContribError heisst, kaeme beim Aufrufer als Absturz an statt als
        # Ablehnung -- und ein Endpunkt, den ein Fremder mit acht Byte Muell
        # umwirft, ist keiner.
        raise ContribError(f"not a readable ZIP file ({error})") from None
    with archive:
        total = sum(item.file_size for item in archive.infolist())
        if total > MAX_UNPACKED_BYTES:
            raise ContribError(
                f"{total / 1e6:.0f} MB unpacked, the limit is "
                f"{MAX_UNPACKED_BYTES / 1e6:.0f} MB")
        names = set(archive.namelist())
        if "manifest.json" not in names or "signature.txt" not in names:
            raise ContribError("manifest.json or signature.txt is missing")
        try:
            manifest = json.loads(archive.read("manifest.json").decode("utf-8"))
            signature = archive.read("signature.txt").decode("utf-8").strip()
        except Exception as error:
            raise ContribError(
                f"manifest.json or signature.txt is unreadable ({error})") from None
        if not isinstance(manifest, dict):
            raise ContribError("manifest.json is not an object")

        if manifest.get("format") != FORMAT_VERSION:
            raise ContribError(
                f"format {manifest.get('format')!r}, expected {FORMAT_VERSION}")
        who = check_contributor(str(manifest.get("contributor") or ""))

        # Erst das Tor, dann die Person -- siehe oben. Ein leeres gate_secret heisst
        # "kein Tor gesetzt"; dann wird auch keins verlangt, sonst waere eine frische
        # Einrichtung von aussen nicht bedienbar und niemand wuesste warum.
        if gate_secret:
            gate = ""
            if "gate.txt" in names:
                try:
                    gate = archive.read("gate.txt").decode("utf-8").strip()
                except Exception:
                    gate = ""
            if not verify(manifest, gate_secret, gate):
                raise ContribError(
                    "the shared access does not match -- this package is older than the "
                    "current upload password. Ask for a new package.")

        if secret_for is not None:
            secret = secret_for(who)
            if not secret:
                raise ContribError(f"no secret is on file for {who!r}")
            if not verify(manifest, secret, signature):
                raise ContribError(
                    f"the signature of {who!r} does not match -- either the wrong secret "
                    "or the file was altered on the way")

        entries = manifest.get("runs") or []
        if not entries:
            raise ContribError("the manifest names no run")
        if len(entries) > MAX_RUNS_PER_ZIP:
            raise ContribError(f"more than {MAX_RUNS_PER_ZIP} runs")

        runs: dict[str, tuple[dict, list[dict]]] = {}
        for entry in entries:
            run_id = check_run_id(str(entry.get("run_id") or ""))
            state_name = f"runs/{run_id}/state.json"
            rows_name = f"runs/{run_id}/rows.jsonl"
            if state_name not in names or rows_name not in names:
                raise ContribError(f"{run_id}: state.json or rows.jsonl is missing")
            state_raw = archive.read(state_name)
            rows_raw = archive.read(rows_name)
            if sha256_bytes(state_raw) != entry.get("state_sha256"):
                raise ContribError(f"{run_id}: state.json does not match its hash")
            if sha256_bytes(rows_raw) != entry.get("rows_sha256"):
                raise ContribError(f"{run_id}: rows.jsonl does not match its hash")
            try:
                state_obj = json.loads(state_raw.decode("utf-8"))
            except Exception as error:
                raise ContribError(
                    f"{run_id}: state.json is not JSON ({error})") from None
            state = validate_state(state_obj, run_id)
            rows = validate_rows(rows_raw.decode("utf-8"), run_id)
            runs[run_id] = (state, rows)
        return manifest, signature, runs


def write_archive(target: Path, manifest: dict, secret: str,
                  runs: dict[str, tuple[dict, str]],
                  gate_secret: str | None = None) -> Path:
    """runs: {run_id: (state, rows_jsonl_text)}"""
    target.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as archive:
        archive.writestr("manifest.json", canonical(manifest).decode("utf-8"))
        archive.writestr("signature.txt", sign(manifest, secret))
        if gate_secret:
            archive.writestr("gate.txt", sign(manifest, gate_secret))
        for run_id, (state, rows_text) in runs.items():
            archive.writestr(f"runs/{run_id}/state.json",
                             json.dumps(state, indent=1, ensure_ascii=False))
            archive.writestr(f"runs/{run_id}/rows.jsonl", rows_text)
    return target
