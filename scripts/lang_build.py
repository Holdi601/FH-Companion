# -*- coding: utf-8 -*-
"""Aus geordneten Listen die Sprachdateien bauen.

    python scripts/lang_build.py <modul.py> [...]

## Warum nach POSITION und nicht nach Schluessel

Die Schluessel sind ganze englische Saetze, manche mit Zeilenumbruechen, mit einem
Gedankenstrich, mit einem Leerzeichen am Ende ("the garage row could not be read: ").
Wer die abtippt, vertippt sich -- und das Ergebnis ist kein Fehler, sondern eine
Uebersetzung, die einfach nie greift. Still, unauffindbar, und beim Pruefen faellt
sie nur als "fehlt" auf, waehrend danebe eine "verwaiste" steht.

Darum liefert ein Sprachmodul eine LISTE in genau der Reihenfolge von
`config/lang/_keys.json` (alphabetisch sortiert). Das Modul nennt dazu die Anzahl
und eine Pruefsumme der Schluessel, gegen die hier verglichen wird: aendert sich ein
englischer Satz, bricht der Bau ab, statt 53 Sprachen stillschweigend zu verschieben.

Ein leerer Eintrag heisst ausdruecklich "nicht uebersetzt" -- dann zeigt die App den
englischen Satz. Das ist erlaubt und besser als eine schlechte Erfindung.
"""
from __future__ import annotations

import hashlib
import importlib.util
import json
import sys
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
LANG = WORKSPACE / "config" / "lang"
KEYS = LANG / "_keys.json"


def schluessel() -> list[str]:
    d = json.loads(KEYS.read_text(encoding="utf-8"))
    return sorted(d)


def pruefsumme(keys: list[str]) -> str:
    h = hashlib.sha256()
    for k in keys:
        h.update(k.encode("utf-8"))
        h.update(b"\0")
    return h.hexdigest()[:16]


def laden(pfad: Path):
    spec = importlib.util.spec_from_file_location(pfad.stem, pfad)
    if spec is None or spec.loader is None:
        raise SystemExit(f"{pfad} laesst sich nicht laden")
    modul = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(modul)
    return modul


def main(argv: list[str] | None = None) -> int:
    argv = sys.argv[1:] if argv is None else argv
    if not argv:
        print(__doc__)
        return 2

    keys = schluessel()
    summe = pruefsumme(keys)
    print(f"{len(keys)} Saetze, Pruefsumme {summe}")

    geschrieben = 0
    for name in argv:
        pfad = Path(name)
        if not pfad.is_absolute():
            pfad = WORKSPACE / name
        modul = laden(pfad)

        erwartet = getattr(modul, "SCHLUESSEL_SUMME", None)
        if erwartet and erwartet != summe:
            print(f"ABBRUCH: {pfad.name} wurde gegen die Saetze {erwartet} "
                  f"geschrieben, jetzt gelten {summe}.")
            print("  Ein englischer Satz hat sich geaendert. "
                  "scripts/lang_keys.py --write, dann die Liste nachziehen.")
            return 1

        # Vor jedem Schreiben pruefen: ein Modul, das SPRACHEN fuer etwas
        # anderes benutzt (am 2026-09-24 eine Liste der Sprachkennungen), lief
        # sonst erst in einen AttributeError, NACHDEM die ZUSATZ-Dateien schon
        # geschrieben waren -- halb gebaut und mit Fehlercode.
        listen = getattr(modul, "SPRACHEN", {})
        if not isinstance(listen, dict):
            print(f"ABBRUCH: SPRACHEN in {pfad.name} ist {type(listen).__name__}, "
                  f"erwartet wird {{kennung: [saetze]}}. Anders benennen.")
            return 1

        # NACHTRAEGE, NACH SCHLUESSEL STATT NACH POSITION.
        #
        # Kommt ein Satz dazu -- weil ein neuer Knopf entstand oder, wie am
        # 2026-09-15, weil das Scan-Werkzeug an denselben Speicher gehaengt wurde --,
        # muesste er sonst in jede der grossen Positionslisten an der richtigen
        # Stelle eingefuegt werden. 25 Listen, 25 Gelegenheiten zum Verrutschen.
        #
        # Ein Nachtrag nennt darum den englischen Satz selbst. Das Risiko dabei ist
        # der Tippfehler; abgesichert ist es durch `lang_keys.py`, das einen
        # Schluessel, den es nicht gibt, als "verwaist" meldet.
        for code, tabelle in getattr(modul, "ZUSATZ", {}).items():
            datei = LANG / f"{code}.json"
            vorhanden = {}
            if datei.exists():
                try:
                    vorhanden = json.loads(datei.read_text(encoding="utf-8"))
                except ValueError:
                    vorhanden = {}
            unbekannt = [s for s in tabelle if s not in keys]
            if unbekannt:
                print(f"ABBRUCH: {code} nennt {len(unbekannt)} Satz/Saetze, die es "
                      f"nicht gibt -- vermutlich vertippt:")
                for s in unbekannt[:5]:
                    print(f"  {s[:70]!r}")
                return 1
            vorhanden.update({k: v for k, v in tabelle.items() if str(v).strip()})
            LANG.mkdir(parents=True, exist_ok=True)
            datei.write_text(
                json.dumps({k: vorhanden[k] for k in keys if k in vorhanden},
                           ensure_ascii=False, indent=1),
                encoding="utf-8")
            fehlt = len(keys) - len(vorhanden)
            print(f"  {code:<8} +{len(tabelle):<3} -> {len(vorhanden):>3} uebersetzt"
                  + (f", {fehlt} offen" if fehlt else ""))
            geschrieben += 1

        for code, liste in listen.items():
            if len(liste) != len(keys):
                print(f"ABBRUCH: {code} hat {len(liste)} Eintraege, "
                      f"gebraucht werden {len(keys)}.")
                return 1
            tabelle = {k: v for k, v in zip(keys, liste) if str(v).strip()}
            LANG.mkdir(parents=True, exist_ok=True)
            (LANG / f"{code}.json").write_text(
                json.dumps(tabelle, ensure_ascii=False, indent=1),
                encoding="utf-8")
            fehlt = len(keys) - len(tabelle)
            print(f"  {code:<6} {len(tabelle):>3} uebersetzt"
                  + (f", {fehlt} offen (bleibt englisch)" if fehlt else ""))
            geschrieben += 1

    print(f"{geschrieben} Sprachdatei(en) geschrieben.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
