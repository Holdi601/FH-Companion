"""Die HTTP-Schnittstelle -- ohne Socket, an Kunstdaten.

    python scripts/test_analytics_api.py

Geprueft wird vor allem, was NICHT durchkommen darf: ein Admin-Aufruf ohne
Unterschrift, mit fremder Unterschrift, mit alter Uhrzeit, oder einer, bei dem
jemand nach dem Unterschreiben noch am Rumpf gedreht hat.
"""

from __future__ import annotations

import json
import shutil
import sys
import tempfile
import time
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
# Der Server-Code liegt seit der Trennung in server/.
sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "server"))

import analytics_api as api  # noqa: E402
import contrib_format as fmt  # noqa: E402

FAILURES: list[str] = []
NL = chr(10)
ADMIN = "admin-geheimnis-fuer-den-test"
KAI = "geheimnis-fuer-kai"
GATE = "das-globale-zugangspasswort-mit-viel-laenge"
KEYS = {"keys": {"kai": KAI}, "admin": ADMIN, "upload_password": GATE}


def check(name: str, condition: bool, detail: str = "") -> None:
    if condition:
        print(f"  ok    {name}")
    else:
        FAILURES.append(name)
        print(f"  FAIL  {name}{('  -- ' + detail) if detail else ''}")


def call(method: str, path: str, *, body: bytes = b"", headers: dict | None = None,
         **kwargs):
    kwargs.setdefault("keys", KEYS)
    status, _type, payload = api.handle(method, path, headers or {}, body, **kwargs)
    try:
        return status, json.loads(payload.decode("utf-8"))
    except Exception:
        return status, payload


def typed(password: str = GATE) -> dict:
    """Der zweite Faktor einer Abgabe: das GETIPPTE Zugangspasswort.

    Seit dem 2026-08-28 reichen die Unterschriften im Paket nicht mehr, weil das
    Paket sein eigenes `gate.txt` mitbringt und eine weitergereichte ZIP damit ein
    Vollzugang waere. Ohne diese Kopfzeile antwortet jede Abgabe mit 401 -- die
    Pruefungen unten liefen deshalb monatelang gegen den falschen Fehler.
    """
    return {"X-Forza-Upload-Password": password}


def signed(method: str, path: str, body: bytes = b"", *, secret: str = ADMIN,
           now: float | None = None) -> dict:
    stamp = str(int(now if now is not None else time.time()))
    return {"X-Forza-Timestamp": stamp,
            "X-Forza-Signature": api.admin_signature(secret, method, path, stamp, body)}


def make_zip(target: Path, secret: str, contributor: str = "kai",
             run_id: str = "ocr_RoadRacing_idx05_A_20260827_160000",
             gate: str | None = GATE) -> Path:
    entries = [{"rank": i + 1, "lap_time_seconds": 70.0 + i * 0.02,
                "car_id": 300, "gamertag": f"g{i}", "is_clean": True}
               for i in range(8)]
    rows_text = NL.join(json.dumps(e, separators=(",", ":"))
                        for e in entries) + NL
    state = {"run_id": run_id, "track": "Satta Sprint", "performance_class": "A",
             "rivals_mode": "Road Racing", "route_index": 17,
             "end_status": "invalid_tail", "status": "valid_section_complete",
             "rows_collected": len(entries), "scanner": "ocr",
             "completed_at": "2026-08-27T16:00:00+02:00"}
    state_bytes = json.dumps(state, indent=1, ensure_ascii=False).encode("utf-8")
    manifest = fmt.build_manifest(contributor, [{
        "run_id": run_id, "state_sha256": fmt.sha256_bytes(state_bytes),
        "rows_sha256": fmt.sha256_bytes(rows_text.encode("utf-8")),
        "rows": len(entries)}], tool="test",
        created_at="2026-08-27T16:00:00+02:00")
    with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as archive:
        archive.writestr("manifest.json", fmt.canonical(manifest).decode("utf-8"))
        archive.writestr("signature.txt", fmt.sign(manifest, secret))
        if gate is not None:
            archive.writestr("gate.txt", fmt.sign(manifest, gate))
        archive.writestr(f"runs/{run_id}/state.json", state_bytes.decode("utf-8"))
        archive.writestr(f"runs/{run_id}/rows.jsonl", rows_text)
    return target


def main() -> int:
    tmp = Path(tempfile.mkdtemp(prefix="api_test_"))
    try:
        dataset_file = tmp / "laps.json"
        dataset_file.write_text(json.dumps({
            "tracks": ["Satta Sprint", "Ito Sprint"], "classes": ["A", "B"],
            "boards": [{"t": 0, "k": 0, "size": 1234}, {"t": 1, "k": 1}],
            "meta": {"cars": 10, "named_cars": 7, "raw_rows": 500, "kept_laps": 400,
                     "hidden_runs": 0, "contributors": ["kai"]},
        }), encoding="utf-8")
        vis = tmp / "visibility.json"
        contrib = tmp / "contrib"
        sweep = tmp / "full_sweep"
        sweep.mkdir()
        # Der Fehlversuchszaehler MUSS in den Testordner zeigen. Ohne ihn schreibt ein
        # Test mit falschem Passwort in data/runtime/upload_attempts.json und sperrt
        # nach zehn Laeufen die echte Maschine fuer 24 Stunden.
        where = dict(dataset_file=dataset_file, visibility=vis,
                     contrib_root=contrib, sweep_root=sweep,
                     attempts_file=tmp / "upload_attempts.json")

        status, data = call("GET", "/api/summary", **where)
        check("die Uebersicht kommt", status == 200 and data["boards"] == 2,
              f"{status} {data}")
        check("sie traegt eine Version", len(data.get("version", "")) == 16,
              str(data.get("version")))
        first_version = data["version"]

        status, raw = call("GET", "/api/dataset", **where)
        check("der ganze Datensatz kommt", status == 200 and "tracks" in raw)

        status, data = call("GET", "/api/nichts", **where)
        check("ein unbekannter Pfad ist 404", status == 404, str(status))

        # --- Abgabe ---
        good = make_zip(tmp / "gut.zip", KAI)
        status, data = call("POST", "/api/contribute", body=good.read_bytes(),
                            headers=typed(), **where)
        check("eine gueltige Abgabe wird angenommen",
              status == 200 and data.get("ok") and data["rows"] == 8,
              f"{status} {data}")
        check("der Lauf liegt beim Beitragenden",
              (contrib / "kai" / "ocr_RoadRacing_idx05_A_20260827_160000").is_dir())

        forged = make_zip(tmp / "falsch.zip", "nicht-das-geheimnis",
                          run_id="ocr_RoadRacing_idx06_A_20260827_160000")
        status, data = call("POST", "/api/contribute", body=forged.read_bytes(),
                            headers=typed(), **where)
        check("eine falsch signierte Abgabe wird abgewiesen",
              status == 403 and not data.get("ok"), f"{status} {data}")
        check("und sie liegt nirgends",
              not (contrib / "kai" / "ocr_RoadRacing_idx06_A_20260827_160000").exists())

        status, data = call("POST", "/api/contribute", body=b"", headers=typed(),
                            **where)
        check("ein leerer Rumpf wird abgewiesen", status == 400, str(status))

        # Der zweite Faktor selbst: ohne Kopfzeile und mit falschem Passwort.
        status, data = call("POST", "/api/contribute", body=good.read_bytes(), **where)
        check("ohne getipptes Zugangspasswort kein Zutritt", status == 401,
              f"{status} {data}")
        status, data = call("POST", "/api/contribute", body=good.read_bytes(),
                            headers=typed("falsch"), **where)
        check("ein falsches Zugangspasswort wird abgewiesen und gezaehlt",
              status == 401 and "attempt" in str(data.get("error", "")).lower(),
              f"{status} {data}")

        status, data = call("POST", "/api/contribute", body=b"PK das ist kein zip",
                            headers=typed(), **where)
        check("etwas, das keine ZIP ist, bringt den Server nicht um",
              status >= 400, f"{status} {data}")

        # --- Das Werkzeug haengt am selben Tor wie das Hochladen ---
        # Es fernsteuert ein fremdes Spiel und laedt am Ende hier hoch. Frei zu haben
        # war es, solange nichts darauf zeigte; seit die Startseite darauf verlinkte,
        # ist "unverlinkt" kein Schutz mehr gewesen.
        status, data = call("GET", "/download/tool", **where)
        check("das Scan-Werkzeug ist ohne Passwort zu", status == 401,
              f"{status} {data}")
        status, data = call("GET", "/download/tool", headers=typed("falsch"), **where)
        check("ein falsches Passwort holt das Werkzeug auch nicht", status == 401,
              f"{status} {data}")
        status, data = call("GET", "/api/package", **where)
        check("und der alte Pfad dorthin ebenso wenig", status == 401,
              f"{status} {data}")
        status, data = call("GET", "/download/tool", headers=typed(), **where)
        check("mit dem richtigen Passwort kommt es",
              status == 200 and isinstance(data, bytes) and data[:2] == b"PK",
              f"{status} {type(data).__name__}")

        # --- Die App dagegen ist absichtlich offen ---
        fake_dist = tmp / "dist"
        fake_dist.mkdir()
        check("ohne gebautes Paket sagt es das, statt zu raten",
              api.haptics_package(fake_dist) is None)
        (fake_dist / "forza-grip-haptics-20260101.zip").write_bytes(b"alt")
        (fake_dist / "forza-grip-haptics-latest.zip").write_bytes(b"neu")
        found = api.haptics_package(fake_dist)
        check("die App wird gefunden, und zwar die neueste",
              found is not None and found.name.endswith("-latest.zip"), str(found))
        (fake_dist / "forza-grip-haptics-latest.zip").unlink()
        found = api.haptics_package(fake_dist)
        check("ohne -latest zaehlt das juengste Datum",
              found is not None and "20260101" in found.name, str(found))


        # --- Das Tor: das globale Zugangspasswort ---
        no_gate = make_zip(tmp / "ohne_tor.zip", KAI, gate=None,
                           run_id="ocr_RoadRacing_idx07_A_20260827_160000")
        status, data = call("POST", "/api/contribute", body=no_gate.read_bytes(),
                            headers=typed(), **where)
        check("ein Paket ohne gate.txt wird abgewiesen",
              status == 403 and "access" in data.get("error", "").lower(),
              f"{status} {data}")

        old_gate = make_zip(tmp / "altes_tor.zip", KAI, gate="das-alte-passwort",
                            run_id="ocr_RoadRacing_idx08_A_20260827_160000")
        status, data = call("POST", "/api/contribute", body=old_gate.read_bytes(),
                            headers=typed(), **where)
        check("ein Paket mit altem Zugangspasswort bekommt die passende Auskunft",
              status == 403 and "package" in data.get("error", "").lower(),
              f"{status} {data}")

        # Neu erzeugen -- und danach darf die vorher gueltige ZIP nicht mehr gelten.
        keys_file = tmp / "keys.json"
        keys_file.write_text(json.dumps(KEYS), encoding="utf-8")
        status, data = call("POST", "/api/admin/rotate-gate",
                            headers=signed("POST", "/api/admin/rotate-gate"),
                            keys_file=keys_file, **where)
        check("das Zugangspasswort laesst sich neu erzeugen",
              status == 200 and data.get("length") == 256,
              f"{status} {str(data)[:160]}")
        rotated = json.loads(keys_file.read_text(encoding="utf-8"))
        check("und steht danach in der Schluesseldatei",
              rotated["upload_password"] == data["upload_password"])
        check("es ist wirklich ein anderes als vorher",
              rotated["upload_password"] != GATE)

        status, data = call("POST", "/api/contribute", body=good.read_bytes(),
                            keys={"keys": {"kai": KAI}, "admin": ADMIN,
                                  "upload_password": rotated["upload_password"]},
                            headers=typed(rotated["upload_password"]),
                            **{k: v for k, v in where.items()})
        check("eine mit dem alten Passwort gebaute ZIP gilt danach nicht mehr",
              status == 403 and "access" in data.get("error", "").lower(),
              f"{status} {data}")

        status, data = call("POST", "/api/admin/rotate-gate",
                            keys_file=keys_file, **where)
        check("neu erzeugen geht nicht ohne Admin-Unterschrift",
              status == 401, str(status))

        # --- Admin ---
        status, data = call("GET", "/api/admin/runs", **where)
        check("ohne Unterschrift kein Admin", status == 401, f"{status} {data}")

        status, data = call("GET", "/api/admin/runs",
                            headers=signed("GET", "/api/admin/runs"), **where)
        check("mit gueltiger Unterschrift kommt die Liste",
              status == 200 and len(data["runs"]) == 1, f"{status} {data}")
        check("der Lauf ist als Fremdbeitrag ausgewiesen",
              data["runs"][0]["by"] == "kai" and not data["runs"][0]["hidden"],
              json.dumps(data["runs"][0]))

        status, data = call("GET", "/api/admin/runs",
                            headers=signed("GET", "/api/admin/runs",
                                           secret="falsches-admin-geheimnis"), **where)
        check("eine fremde Unterschrift wird abgewiesen", status == 401, str(status))

        status, data = call("GET", "/api/admin/runs",
                            headers=signed("GET", "/api/admin/runs",
                                           now=time.time() - 4000), **where)
        check("ein alter Aufruf laesst sich nicht wiederholen",
              status == 401 and "off by" in data.get("error", ""),
              f"{status} {data}")

        # Unterschrieben ueber einen anderen Pfad als den aufgerufenen.
        status, data = call("GET", "/api/admin/runs",
                            headers=signed("GET", "/api/summary"), **where)
        check("eine Unterschrift fuer einen anderen Pfad gilt nicht",
              status == 401, str(status))

        body = json.dumps({"run_id": "ocr_RoadRacing_idx05_A_20260827_160000",
                           "hidden": True, "reason": "Zeiten unglaubwuerdig"}
                          ).encode("utf-8")
        status, data = call("POST", "/api/admin/visibility", body=body,
                            headers=signed("POST", "/api/admin/visibility", body),
                            **where)
        check("ausblenden geht", status == 200 and data["hidden"], f"{status} {data}")
        hidden = json.loads(vis.read_text(encoding="utf-8"))["hidden"]
        check("und steht mit Begruendung in der Datei",
              hidden and hidden[0]["reason"] == "Zeiten unglaubwuerdig",
              json.dumps(hidden))

        # Nach dem Unterschreiben am Rumpf gedreht.
        headers = signed("POST", "/api/admin/visibility", body)
        tampered = json.dumps({"run_id": "ocr_RoadRacing_idx05_A_20260827_160000",
                               "hidden": False}).encode("utf-8")
        status, data = call("POST", "/api/admin/visibility", body=tampered,
                            headers=headers, **where)
        check("ein nachtraeglich veraenderter Rumpf bricht die Unterschrift",
              status == 401, f"{status} {data}")
        hidden = json.loads(vis.read_text(encoding="utf-8"))["hidden"]
        check("und der Lauf bleibt ausgeblendet", len(hidden) == 1, json.dumps(hidden))

        status, data = call("GET", "/api/admin/runs",
                            headers=signed("GET", "/api/admin/runs"), **where)
        check("die Liste zeigt den Lauf jetzt als ausgeblendet",
              data["runs"][0]["hidden"] and data["runs"][0]["reason"],
              json.dumps(data["runs"][0]))

        body = json.dumps({"run_id": "../../etc/passwd", "hidden": True}).encode()
        status, data = call("POST", "/api/admin/visibility", body=body,
                            headers=signed("POST", "/api/admin/visibility", body),
                            **where)
        check("ein Laufname mit Pfadanteil wird auch als Admin abgelehnt",
              status == 400, f"{status} {data}")

        # Wieder einblenden.
        body = json.dumps({"run_id": "ocr_RoadRacing_idx05_A_20260827_160000",
                           "hidden": False}).encode("utf-8")
        status, data = call("POST", "/api/admin/visibility", body=body,
                            headers=signed("POST", "/api/admin/visibility", body),
                            **where)
        check("wieder einblenden geht auch",
              status == 200 and not data["hidden"], f"{status} {data}")
        check("und die Datei ist wieder leer",
              json.loads(vis.read_text(encoding="utf-8"))["hidden"] == [])

        # Ohne Admin-Geheimnis muss die Ansicht sich verweigern, nicht oeffnen.
        status, _type, payload = api.handle("GET", "/api/admin/runs", {}, b"",
                                            keys={"keys": {}, "admin": ""}, **where)
        check("ohne gesetztes Admin-Geheimnis ist die Ansicht zu, nicht offen",
              status == 503, f"{status} {payload[:120]}")

        # Die Version muss sich aendern, wenn sich der Datensatz aendert.
        dataset_file.write_text(dataset_file.read_text(encoding="utf-8")
                                .replace('"cars": 10', '"cars": 11'), encoding="utf-8")
        status, data = call("GET", "/api/summary", **where)
        check("eine Aenderung am Datensatz aendert die Version",
              data["version"] != first_version,
              f"{first_version} -> {data['version']}")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)

    print()
    if FAILURES:
        print(f"{len(FAILURES)} Pruefung(en) fehlgeschlagen: {', '.join(FAILURES)}")
        return 1
    print("alles bestanden")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
