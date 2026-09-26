"""Die zwei Antworten aus einer Tune-Messung ablesen.

    python scripts/report_tune_probe.py data/runtime/tune_probe

Liest, was `probe_tune_fields.ps1` aus dem Gast zurueckgeholt hat, und sagt in
Klartext, welche der beiden Welten wir haben:

* fuehrt die Bestenlistenzeile eine `versionedTuneId` -- dann ist eine Tuner-
  Wertung ueber den ganzen Bestand moeglich;
* liegen Id/Share-Code-Paare im Heap -- dann laesst sich zu einer Id der
  tippbare Code finden, ohne den verschluesselten Dienst zu sprechen.
"""

from __future__ import annotations

import json
import sys
from collections import Counter
from pathlib import Path


def load(path: Path):
    if not path.exists():
        return None
    try:
        return json.loads(path.read_text(encoding="utf-8-sig"))
    except Exception as error:  # eine halbe Datei ist eine Antwort, kein Absturz
        print(f"  {path.name} nicht lesbar: {error}")
        return None


def rows_of(payload) -> list[dict]:
    """Die Zeilenliste aus der Ausgabe holen, egal wie sie verpackt ist."""
    if payload is None:
        return []
    if isinstance(payload, list):
        return [item for item in payload if isinstance(item, dict) and "rank" in item]
    if isinstance(payload, dict):
        for key in ("rows", "records", "entries"):
            value = payload.get(key)
            if isinstance(value, list):
                return [item for item in value if isinstance(item, dict)]
        # Verschachtelt: {"arrays": [[row, ...], ...]}
        out: list[dict] = []
        for value in payload.values():
            if isinstance(value, list):
                for item in value:
                    if isinstance(item, dict) and "rank" in item:
                        out.append(item)
                    elif isinstance(item, list):
                        out.extend(x for x in item if isinstance(x, dict) and "rank" in x)
        return out
    return []


def main() -> int:
    folder = Path(sys.argv[1] if len(sys.argv) > 1 else "data/runtime/tune_probe")
    print(f"Messung aus {folder}")

    rows = rows_of(load(folder / "rows.json"))
    print()
    print("FRAGE 1 -- traegt die Bestenlistenzeile die Tune-Id?")
    if not rows:
        print("  keine Zeilen gelesen. Stand das Spiel auf einer Bestenliste?")
    else:
        with_id = [r for r in rows if r.get("versioned_tune_id")]
        with_creator = [r for r in rows if r.get("tune_creator")]
        print(f"  {len(rows)} Zeilen gelesen")
        print(f"  davon mit versionedTuneId: {len(with_id)}")
        print(f"  davon mit Ersteller-Gamertag: {len(with_creator)}")
        if with_id:
            print("  ANTWORT: JA -- die Tuner-Wertung ist ueber den Bestand machbar.")
            for row in with_id[:5]:
                print(f"    Rang {row['rank']:>6}  {row.get('gamertag',''):<16}"
                      f"  tune {row['versioned_tune_id'][:16]}..."
                      f"  by {row.get('tune_creator','')!r}")
            creators = Counter(r.get("tune_creator", "") for r in with_creator)
            if creators:
                print(f"    haeufigste Ersteller: {creators.most_common(5)}")
        else:
            print("  ANTWORT: NEIN -- die Liste fuehrt kein Tune mit. Dann bleibt nur")
            print("  der Rivalen-Detailpfad (ScoreboardScoreData.versionedTuneId),")
            print("  also ein Menueaufruf je Runde.")

    codes = load(folder / "share_codes.json") or []
    print()
    print("FRAGE 2 -- liegen Id/Share-Code-Paare im Speicher?")
    if not isinstance(codes, list):
        codes = []
    plausible = [c for c in codes if c.get("plausible")]
    print(f"  {len(codes)} Treffer, davon {len(plausible)} plausibel")
    for entry in (plausible or codes)[:10]:
        compact = (entry.get("layouts") or {}).get("compact") or {}
        print(f"    {entry.get('share_code',''):<12} {entry.get('versioned_id','')[:16]}..."
              f"  car={compact.get('car_id')}  by={compact.get('creator_gamertag')!r}")
    if plausible:
        print("  ANTWORT: JA -- zu einer Tune-Id laesst sich der Code im Heap finden.")
    elif codes:
        print("  UNKLAR: Treffer ja, aber keiner mit Ersteller und plausibler CarId.")
        print("  Naechster Versuch: im Spiel eine Tune-Suche oeffnen, dann nochmal.")
    else:
        print("  ANTWORT: nichts gefunden. Das Spiel hat vermutlich keine UGC-Liste")
        print("  geladen -- im Spiel eine Tune-Suche oeffnen und nochmal messen.")

    if rows and codes:
        ids = {r.get("versioned_tune_id") for r in rows if r.get("versioned_tune_id")}
        hit = [c for c in codes if c.get("versioned_id") in ids]
        print()
        print(f"VERBINDUNG -- Zeilen-Ids, zu denen ein Code im Heap liegt: {len(hit)}")
        for entry in hit[:10]:
            print(f"    {entry['share_code']}  {entry['versioned_id']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
