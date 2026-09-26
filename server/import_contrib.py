"""Eine fremde Abgabe pruefen und einlassen.

    python server/import_contrib.py eingang/forza-contrib-kai-20260827.zip
    python server/import_contrib.py abgabe.zip --dry-run      # nur pruefen
    python server/import_contrib.py abgabe.zip --force        # vorhandene ersetzen

Geheimnisse stehen in `config/contrib_keys.json`, **je Beitragendem eines**:

    {"keys": {"kai": "...", "mira": "..."}, "admin": "..."}

Je Person ein eigenes: ein durchgesickertes Geheimnis kostet dann einen Beitragenden
und nicht alle, und der Server weiss ohne Zusatzangabe, wer unterschrieben hat.

## Was hier passiert und was nicht

Gelesen wird die ZIP **im Speicher**. Auf die Platte kommt nur, was
`contrib_format.read_archive` durchgelassen hat, und zwar unter einem Pfad, den
dieses Skript selbst zusammensetzt -- die Namen aus dem Archiv werden nie als Pfad
verwendet.

Das Parquet, aus dem die Auswertung spaeter liest, entsteht **hier** aus den
geprueften Zeilen. Eine Binaerdatei von aussen wird nicht angenommen.
"""

from __future__ import annotations

import argparse
import json
import sys
from datetime import datetime
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from contrib_format import ContribError, read_archive, verify  # noqa: E402

WORKSPACE = Path(__file__).resolve().parent.parent
CONTRIB_ROOT = WORKSPACE / "data/memory_scans/contrib"
KEYS_FILE = WORKSPACE / "config/contrib_keys.json"


def load_keys(path: Path | None = None) -> dict:
    path = path or KEYS_FILE
    if not path.exists():
        raise ContribError(
            f"{path} fehlt -- ohne Geheimnisse kann nichts geprueft werden. "
            "Anlegen mit: python server/contrib_keys.py add <name>")
    return json.loads(path.read_text(encoding="utf-8-sig"))


def import_archive(data, *, keys: dict, root: Path = CONTRIB_ROOT,
                   force: bool = False, dry_run: bool = False) -> dict:
    """Eine Abgabe pruefen und (wenn nicht dry_run) einlassen.

    Gibt einen Bericht zurueck. Wirft ContribError, wenn irgendetwas nicht stimmt --
    und dann ist NICHTS geschrieben worden, weil erst alles geprueft und dann
    geschrieben wird.
    """
    # Die Signatur wird INNERHALB von read_archive geprueft, direkt nach dem Manifest
    # und vor der ersten Lauf-Datei -- siehe die Begruendung dort.
    manifest, _signature, runs = read_archive(
        data, secret_for=lambda who: (keys.get("keys") or {}).get(who),
        gate_secret=keys.get("upload_password") or None)
    who = manifest["contributor"]

    target_root = root / who
    existing = [run_id for run_id in runs if (target_root / run_id).exists()]
    if existing and not force:
        raise ContribError(
            f"{len(existing)} Lauf/Laeufe liegen schon hier ({existing[0]} ...). "
            "Mit --force ersetzen.")

    report = {"contributor": who, "runs": [], "rows": 0,
              "created_at": manifest.get("created_at"),
              "tool": manifest.get("tool"), "note": manifest.get("note") or ""}
    for run_id, (state, rows) in sorted(runs.items()):
        report["runs"].append({"run_id": run_id, "rows": len(rows),
                               "track": state.get("track"),
                               "class": state.get("performance_class")})
        report["rows"] += len(rows)
    if dry_run:
        return report

    import pandas

    for run_id, (state, rows) in sorted(runs.items()):
        run_dir = target_root / run_id
        run_dir.mkdir(parents=True, exist_ok=True)
        # Herkunft im Lauf selbst festhalten, nicht nur im Verzeichnisnamen: ein
        # verschobener Ordner verliert sonst still die Angabe, wessen Zahlen das sind.
        state = dict(state)
        state["contributed_by"] = who
        state["imported_at"] = datetime.now().astimezone().isoformat()
        state["contrib_tool"] = manifest.get("tool")
        (run_dir / "state.json").write_text(
            json.dumps(state, indent=1, ensure_ascii=False), encoding="utf-8")
        (run_dir / "rows.jsonl").write_text(
            "\n".join(json.dumps(row, separators=(",", ":")) for row in rows) + "\n",
            encoding="utf-8")
        pandas.DataFrame(rows).to_parquet(
            run_dir / "leaderboard_entries.parquet", index=False)
    (target_root / "last_import.json").write_text(
        json.dumps(report, indent=1, ensure_ascii=False), encoding="utf-8")
    return report


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("archive", type=Path)
    parser.add_argument("--root", type=Path, default=CONTRIB_ROOT)
    parser.add_argument("--keys", type=Path, default=KEYS_FILE)
    parser.add_argument("--force", action="store_true",
                        help="vorhandene Laeufe desselben Namens ersetzen")
    parser.add_argument("--dry-run", action="store_true",
                        help="nur pruefen, nichts schreiben")
    args = parser.parse_args(argv)

    try:
        report = import_archive(args.archive.read_bytes(),
                                keys=load_keys(args.keys), root=args.root,
                                force=args.force, dry_run=args.dry_run)
    except ContribError as error:
        print(f"abgelehnt: {error}")
        return 1

    head = "geprueft" if args.dry_run else "eingelassen"
    print(f"{head}: {len(report['runs'])} Lauf/Laeufe von {report['contributor']}, "
          f"{report['rows']} Zeilen")
    for run in report["runs"]:
        print(f"  {run['track']} {run['class']:<3} {run['rows']:>6} Zeilen  "
              f"{run['run_id']}")
    if report["note"]:
        print(f"  Notiz: {report['note']}")
    if not args.dry_run:
        print("Die Auswertung nimmt sie beim naechsten Seitenaufbau auf.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
