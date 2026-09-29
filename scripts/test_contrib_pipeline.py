"""Fremdbeitraege, Beglaubigung und das Ausblenden von Laeufen -- an Kunstdaten.

    python scripts/test_contrib_pipeline.py

Warum an Kunstdaten und nicht am Bestand: ein Vollbau dauert eine Viertelstunde und
laeuft dem Sweep in die Quere. Hier zaehlt, dass die WEGE stimmen -- eigener Lauf,
Fremdbeitrag, ausgeblendeter Lauf -- und dass die Ablehnungen halten.
"""

from __future__ import annotations

import json
import shutil
import sys
import tempfile
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
# Der Server-Code liegt seit der Trennung in server/.
sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "server"))

import build_analytics_dataset as dataset  # noqa: E402
import contrib_format as fmt  # noqa: E402
import import_contrib  # noqa: E402

FAILURES: list[str] = []
NL = chr(10)


def check(name: str, condition: bool, detail: str = "") -> None:
    if condition:
        print(f"  ok    {name}")
    else:
        FAILURES.append(name)
        print(f"  FAIL  {name}{('  -- ' + detail) if detail else ''}")


def refuses(name: str, call, expect_in: str = "") -> None:
    """Etwas MUSS abgelehnt werden -- und mit einer Begruendung, die dazu passt.

    Die Begruendung wird mitgeprueft, weil eine Ablehnung aus dem falschen Grund
    genauso taeuscht wie gar keine: sie sieht im Protokoll aus wie Sicherheit.
    """
    try:
        call()
    except fmt.ContribError as error:
        if expect_in and expect_in.lower() not in str(error).lower():
            check(name, False, f"abgelehnt, aber mit: {error}")
        else:
            check(name, True)
        return
    except Exception as error:
        check(name, False, f"falsche Ausnahme: {type(error).__name__}: {error}")
        return
    check(name, False, "durchgelassen")


def make_run(root: Path, run_id: str, track: str, klass: str, rows: int = 30) -> Path:
    """Ein eigener Lauf, so wie write_board ihn hinterlaesst."""
    run = root / run_id
    run.mkdir(parents=True, exist_ok=True)
    entries = [{
        "rank": index + 1,
        "lap_time_seconds": 60.0 + index * 0.01,
        "car_id": 100 + (index % 3),
        "gamertag": f"player{index}",
        "is_clean": True,
    } for index in range(rows)]
    (run / "rows.jsonl").write_text(
        NL.join(json.dumps(entry) for entry in entries), encoding="utf-8")
    (run / "state.json").write_text(json.dumps({
        "run_id": run_id, "track": track, "performance_class": klass,
        "rivals_mode": "Road Racing", "route_index": 0, "end_status": "invalid_tail",
        "status": "valid_section_complete", "rows_collected": rows,
        "minimum_rank": 1, "maximum_rank": rows, "missing_ranks": 0,
        "scanner": "ocr", "completed_at": "2026-08-27T12:00:00+02:00",
    }, indent=1), encoding="utf-8")
    import pandas
    pandas.DataFrame(entries).to_parquet(run / "leaderboard_entries.parquet",
                                         index=False)
    return run


def sample_zip(target: Path, secret: str, contributor: str = "kai", *,
               run_id: str | None = None, rows: int = 12,
               tamper_rows=None, tamper_manifest=None) -> Path:
    """Eine gueltige Abgabe bauen -- und auf Wunsch danach daran drehen."""
    run_id = run_id or "ocr_RoadRacing_idx03_A_20260827_150000"
    entries = [{"rank": i + 1, "lap_time_seconds": 61.0 + i * 0.01,
                "car_id": 200 + (i % 2), "gamertag": f"gast{i}", "is_clean": True}
               for i in range(rows)]
    rows_text = NL.join(
        json.dumps(e, separators=(",", ":")) for e in entries) + NL
    # Der Hash wird ueber die SAUBEREN Zeilen gebildet und danach werden die
    # verbogenen geschrieben -- sonst waere die Faelschung in sich stimmig und die
    # Pruefung wuerde nur beweisen, dass ein Hash zu sich selbst passt.
    clean_rows_text = rows_text
    if tamper_rows:
        rows_text = tamper_rows(rows_text)
    state = {"run_id": run_id, "track": "Ito Sprint", "performance_class": "A",
             "rivals_mode": "Road Racing", "route_index": 14,
             "end_status": "invalid_tail", "status": "valid_section_complete",
             "rows_collected": len(entries), "scanner": "ocr",
             "completed_at": "2026-08-27T15:00:00+02:00"}
    state_bytes = json.dumps(state, indent=1, ensure_ascii=False).encode("utf-8")
    manifest = fmt.build_manifest(contributor, [{
        "run_id": run_id,
        "state_sha256": fmt.sha256_bytes(state_bytes),
        "rows_sha256": fmt.sha256_bytes(clean_rows_text.encode("utf-8")),
        "rows": len(entries)}], tool="test",
        created_at="2026-08-27T15:00:00+02:00")
    if tamper_manifest:
        manifest = tamper_manifest(manifest)
    with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as archive:
        archive.writestr("manifest.json", fmt.canonical(manifest).decode("utf-8"))
        archive.writestr("signature.txt", fmt.sign(manifest, secret))
        archive.writestr(f"runs/{run_id}/state.json", state_bytes.decode("utf-8"))
        archive.writestr(f"runs/{run_id}/rows.jsonl", rows_text)
    return target


def dataset_checks() -> None:
    tmp = Path(tempfile.mkdtemp(prefix="contrib_ds_"))
    try:
        own = tmp / "full_sweep"
        contrib = tmp / "contrib"
        make_run(own, "ocr_RoadRacing_idx00_A_20260827_120000", "Soni Circuit", "A")
        make_run(contrib / "kai", "ocr_RoadRacing_idx01_B_20260827_130000",
                 "Hokubu Circuit", "B")
        make_run(contrib / "kai", "ocr_RoadRacing_idx02_C_20260827_140000",
                 "Daikoku Circuit", "C")

        vis = tmp / "visibility.json"
        vis.write_text(json.dumps({"hidden": []}), encoding="utf-8")

        payload = dataset.build(own, contrib_roots=[contrib], visibility=vis)
        scans = payload["meta"]["scans"]
        by = sorted(entry["by"] for entry in scans)
        check("ein eigener Lauf und zwei Fremdbeitraege werden gefunden",
              len(scans) == 3, f"{len(scans)} Scans")
        check("die Herkunft steht an jedem Scan",
              by == ["kai", "kai", "local"], str(by))
        check("die Beitragenden stehen im Kopf",
              payload["meta"]["contributors"] == ["kai"],
              str(payload["meta"]["contributors"]))
        check("nichts ist ausgeblendet", payload["meta"]["hidden_runs"] == 0)
        check("drei Boards", len(payload["boards"]) == 3,
              f"{len(payload['boards'])} Boards")

        vis.write_text(json.dumps({"hidden": [
            {"run_id": "ocr_RoadRacing_idx01_B_20260827_130000",
             "reason": "Zeiten unglaubwuerdig"}]}), encoding="utf-8")
        payload = dataset.build(own, contrib_roots=[contrib], visibility=vis)
        check("ein ausgeblendeter Lauf verschwindet aus der Auswertung",
              len(payload["meta"]["scans"]) == 2,
              f"{len(payload['meta']['scans'])} Scans")
        check("das Ausblenden wird gezaehlt, nicht verschwiegen",
              payload["meta"]["hidden_runs"] == 1)
        check("das Board des ausgeblendeten Laufs ist weg",
              "Hokubu Circuit" not in set(payload["tracks"]),
              str(sorted(payload["tracks"])))

        vis.write_text(json.dumps({"hidden": [
            "ocr_RoadRacing_idx00_A_20260827_120000"]}), encoding="utf-8")
        payload = dataset.build(own, contrib_roots=[contrib], visibility=vis)
        check("auch ein eigener Lauf laesst sich ausblenden",
              all(entry["by"] == "kai" for entry in payload["meta"]["scans"]),
              str([entry["by"] for entry in payload["meta"]["scans"]]))
        check("die Kurzform (nur run_id) wird auch verstanden",
              payload["meta"]["hidden_runs"] == 1)

        payload = dataset.build(own, contrib_roots=[tmp / "gibt_es_nicht"],
                                visibility=tmp / "auch_nicht.json")
        check("fehlende Wurzeln und fehlende Sichtbarkeitsdatei sind kein Fehler",
              len(payload["meta"]["scans"]) == 1)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def security_checks() -> None:
    print()
    tmp = Path(tempfile.mkdtemp(prefix="contrib_sec_"))
    try:
        secret = "geheim-fuer-kai"
        keys = {"keys": {"kai": secret}, "admin": "a"}
        root = tmp / "contrib"

        good = sample_zip(tmp / "gut.zip", secret)
        report = import_contrib.import_archive(good.read_bytes(), keys=keys,
                                               root=root, dry_run=True)
        check("eine gueltige Abgabe wird angenommen",
              report["contributor"] == "kai" and report["rows"] == 12,
              json.dumps(report["runs"]))

        refuses("ein falsches Geheimnis wird abgelehnt",
                lambda: import_contrib.import_archive(
                    good.read_bytes(), keys={"keys": {"kai": "falsch"}}, root=root,
                    dry_run=True),
                "signatur")
        refuses("ein unbekannter Beitragender wird abgelehnt",
                lambda: import_contrib.import_archive(
                    good.read_bytes(), keys={"keys": {"mira": secret}}, root=root,
                    dry_run=True),
                "no secret")

        bent = sample_zip(tmp / "verbogen.zip", secret,
                          tamper_rows=lambda text: text.replace("61.0", "31.0"))
        refuses("veraenderte Zeilen brechen ihren Hash",
                lambda: import_contrib.import_archive(bent.read_bytes(), keys=keys,
                                                      root=root, dry_run=True),
                "hash")

        # In sich vollstaendig stimmig -- nur mit dem falschen Geheimnis
        # unterschrieben. Genau so saehe eine Abgabe von jemandem aus, der das
        # Format kennt, aber das Geheimnis nicht hat.
        forged = sample_zip(tmp / "gefaelscht.zip", "ein-anderes-geheimnis")
        refuses("eine in sich stimmige Faelschung ohne das Geheimnis wird abgelehnt",
                lambda: import_contrib.import_archive(forged.read_bytes(), keys=keys,
                                                      root=root, dry_run=True),
                "signatur")

        refuses("ein Laufname mit Pfadanteil wird abgelehnt",
                lambda: fmt.check_run_id("../../windows/system32/x"), "unusable")
        refuses("ein Laufname mit Schraegstrich wird abgelehnt",
                lambda: fmt.check_run_id("gut/boese"), "unusable")
        refuses("ein Beitragender mit Pfadanteil wird abgelehnt",
                lambda: fmt.check_contributor("../admin"), "unusable")

        refuses("eine unmoegliche Rundenzeit wird abgelehnt",
                lambda: fmt.validate_rows(
                    json.dumps({"rank": 1, "lap_time_seconds": 0.2}), "r"),
                "out of range")
        refuses("ein doppelter Rang wird abgelehnt",
                lambda: fmt.validate_rows(
                    json.dumps({"rank": 1, "lap_time_seconds": 60}) + NL
                    + json.dumps({"rank": 1, "lap_time_seconds": 61}), "r"),
                "appears twice")
        refuses("kaputtes JSON wird abgelehnt",
                lambda: fmt.validate_rows("{kein json", "r"), "not JSON")

        report = import_contrib.import_archive(good.read_bytes(), keys=keys, root=root)
        landed = root / "kai" / "ocr_RoadRacing_idx03_A_20260827_150000"
        check("der Lauf liegt unter dem Namen des Beitragenden",
              landed.is_dir(), str(landed))
        check("das Parquet ist hier gebaut worden, nicht mitgeliefert",
              (landed / "leaderboard_entries.parquet").exists())
        state = json.loads((landed / "state.json").read_text(encoding="utf-8"))
        check("die Herkunft steht im Lauf selbst",
              state.get("contributed_by") == "kai", json.dumps(state)[:120])

        refuses("ein zweites Mal einlassen wird ohne --force abgelehnt",
                lambda: import_contrib.import_archive(good.read_bytes(), keys=keys,
                                                      root=root),
                "already exist here")

        own = tmp / "full_sweep"
        make_run(own, "ocr_RoadRacing_idx00_A_20260827_120000", "Soni Circuit", "A")
        vis = tmp / "vis.json"
        vis.write_text(json.dumps({"hidden": []}), encoding="utf-8")
        payload = dataset.build(own, contrib_roots=[root], visibility=vis)
        check("die Auswertung sieht den eingelassenen Lauf",
              payload["meta"]["contributors"] == ["kai"],
              str(payload["meta"]["contributors"]))
        check("und beide Boards sind da", len(payload["boards"]) == 2,
              f"{len(payload['boards'])} Boards")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def roundtrip_checks() -> None:
    """Vom Packer des Freundes bis in die Auswertung -- ohne Spiel.

    Die riskanteste Naht im ganzen Aufbau: `contrib_scan.pack()` schreibt die ZIP,
    `import_contrib` liest sie, und die beiden liegen auf verschiedenen Rechnern.
    Wenn sie sich ueber ein Feld uneinig sind, faellt das sonst erst auf, wenn ein
    Freund eine Stunde gescannt hat und die Abgabe abgewiesen wird.
    """
    print()
    import contrib_scan

    tmp = Path(tempfile.mkdtemp(prefix="contrib_rt_"))
    try:
        secret, gate = "kais-geheimnis", "das-lange-zugangspasswort"
        keys = {"keys": {"kai": secret}, "admin": "a", "upload_password": gate}

        # So sieht es aus, wenn der Scanner beim Freund fertig ist: ein Lauf mit
        # state.json und rows.jsonl unter dem Scan-Verzeichnis.
        scan_root = tmp / "runs"
        make_run(scan_root, "ocr_RoadRacing_idx04_S1_20260827_170000",
                 "Hokubu Circuit", "S1", rows=40)
        runs = contrib_scan.collect_runs(scan_root, 0.0)
        check("der Packer findet den fertigen Lauf", len(runs) == 1, str(list(runs)))

        contrib_scan.OUT_DIR = tmp / "out"
        archive = contrib_scan.pack(
            {"contributor": "kai", "secret": secret, "upload_password": gate},
            runs, "erster Versuch")
        check("er schreibt eine ZIP", archive.exists() and archive.stat().st_size > 0)

        root = tmp / "contrib"
        report = import_contrib.import_archive(archive.read_bytes(), keys=keys,
                                               root=root)
        check("der Importeur nimmt sie an",
              report["contributor"] == "kai" and report["rows"] == 40,
              json.dumps(report["runs"]))
        check("die Notiz kommt mit an", report["note"] == "erster Versuch",
              report["note"])

        own = tmp / "full_sweep"
        make_run(own, "ocr_RoadRacing_idx00_A_20260827_120000", "Soni Circuit", "A")
        vis = tmp / "vis.json"
        vis.write_text(json.dumps({"hidden": []}), encoding="utf-8")
        payload = dataset.build(own, contrib_roots=[root], visibility=vis)
        tracks = set(payload["tracks"])
        check("und die Auswertung zeigt das Board des Freundes",
              "Hokubu Circuit" in tracks, str(sorted(tracks)))
        check("unter seinem Namen",
              any(s["by"] == "kai" and s["t"] == "Hokubu Circuit"
                  for s in payload["meta"]["scans"]),
              json.dumps(payload["meta"]["scans"]))

        # Und das Ausblenden muss auch einen Fremdbeitrag treffen.
        vis.write_text(json.dumps({"hidden": [
            {"run_id": "ocr_RoadRacing_idx04_S1_20260827_170000",
             "reason": "Testlauf"}]}), encoding="utf-8")
        payload = dataset.build(own, contrib_roots=[root], visibility=vis)
        check("ein ausgeblendeter Fremdbeitrag verschwindet auch aus dem Download",
              "Hokubu Circuit" not in set(payload["tracks"]),
              str(sorted(payload["tracks"])))

        # Ein zweiter Lauf spaeter darf den ersten nicht noch einmal mitschicken.
        import time as clock
        marker = clock.time()
        make_run(scan_root, "ocr_RoadRacing_idx05_S1_20260827_180000",
                 "Soni Circuit", "S1", rows=20)
        later = contrib_scan.collect_runs(scan_root, marker)
        check("ein zweiter Lauf packt nur das Neue ein",
              list(later) == ["ocr_RoadRacing_idx05_S1_20260827_180000"],
              str(list(later)))
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def main() -> int:
    dataset_checks()
    security_checks()
    roundtrip_checks()

    print()
    if FAILURES:
        print(f"{len(FAILURES)} Pruefung(en) fehlgeschlagen: {', '.join(FAILURES)}")
        return 1
    print("alles bestanden")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
