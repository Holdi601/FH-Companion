# -*- coding: utf-8 -*-
"""Welche Saetze die Oberflaeche zeigt -- und was den Sprachdateien fehlt.

    python scripts/lang_keys.py            # Bericht
    python scripts/lang_keys.py --write    # config/lang/_keys.json neu schreiben

## Warum es diese Liste gibt

`Loc.T("Stop now")` benutzt den englischen Satz als Schluessel (siehe `Loc.cs`).
Das macht eine `en.json` ueberfluessig -- aber es macht auch unsichtbar, WELCHE
Saetze es gibt. Ohne diese Liste laesst sich eine Uebersetzung weder anlegen noch
pruefen, und ein Satz, der im Quelltext umformuliert wurde, verwaist still.

Der Bericht nennt darum beides:

  fehlt     ein Satz steht im Code, aber nicht in der Sprachdatei -> Englisch
  verwaist  ein Satz steht in der Sprachdatei, aber nicht mehr im Code

Verwaiste Eintraege sind kein Fehler, sondern eine Nachricht: der englische Satz
wurde geaendert, die Uebersetzung meint noch den alten.
"""
from __future__ import annotations

import ast
import json
import re
import sys
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
QUELLE = WORKSPACE / "haptics" / "ForzaHaptics.Tester"
LANG = WORKSPACE / "config" / "lang"
KEYS = LANG / "_keys.json"

# Loc.T("...") mit C#-Fluchtfolgen. Der Rueckstrich wird aus chr(92) gebaut, damit
# dieses Muster auch dann heil ankommt, wenn die Datei durch eine Shell gereicht wird.
_B = chr(92)
_MUSTER = re.compile(r'Loc\.T\(\s*"((?:[^"' + _B + _B + r']|' + _B + _B + r'.)*)"\s*\)')


def entfluchten(roh: str) -> str:
    """Aus dem C#-Literal den Text machen, den der Nutzer sieht."""
    return (roh.replace(_B + _B, _B)
               .replace(_B + '"', '"')
               .replace(_B + "n", "\n")
               .replace(_B + "t", "\t"))


#: Welche Python-Dateien ueberhaupt Oberflaeche zeigen. Nicht alle: `lang_keys.py`
#: sucht selbst nach diesem Aufruf, und ein Skript, das nur der Baurechner sieht,
#: gehoert nicht in den Sprachspeicher.
PY_QUELLEN = ("contrib_scan.py",)


def _py_saetze(text: str) -> list[tuple[str, int]]:
    """Alle `T("...")` einer Python-Datei -- ueber den Parser, nicht ueber ein Muster.

    MIT `ast` UND NICHT MIT EINEM REGULAEREN AUSDRUCK, und dafuer gibt es einen
    handfesten Grund: Python fasst nebeneinanderstehende Zeichenketten beim
    Uebersetzen zu EINER zusammen. Aus

        T("No server in the configuration -- send the file "
          "by hand.")

    wird zur Laufzeit ein einziger Schluessel mit dem ganzen Satz. Ein Muster sieht
    nur das erste Stueck -- und legt damit einen Schluessel an, der NIE passt: die
    Uebersetzung greift nicht, und im Bericht steht "fehlt" neben "verwaist", ohne
    dass jemand den Zusammenhang sieht. Am 2026-09-15 sind mir genau so drei von
    27 Saetzen abgeschnitten worden.

    Der Parser macht dieselbe Zusammenfassung wie der Uebersetzer. Damit ist der
    erfasste Schluessel per Konstruktion derselbe, den `T()` spaeter bekommt.
    """
    treffer: list[tuple[str, int]] = []
    try:
        baum = ast.parse(text)
    except SyntaxError:
        return treffer
    for knoten in ast.walk(baum):
        if not isinstance(knoten, ast.Call):
            continue
        ziel = knoten.func
        # `T(...)` -- nicht `fmt.T(...)` und nicht `self.T(...)`.
        if not (isinstance(ziel, ast.Name) and ziel.id == "T"):
            continue
        if not knoten.args:
            continue
        erstes = knoten.args[0]
        if isinstance(erstes, ast.Constant) and isinstance(erstes.value, str):
            treffer.append((erstes.value, knoten.lineno))
    return treffer


def saetze() -> dict[str, list[str]]:
    """Jeder Satz mit den Stellen, an denen er steht -- aus C# UND Python."""
    gefunden: dict[str, list[str]] = {}

    for p in sorted(QUELLE.rglob("*.cs")):
        # Der Ordner Lokal/ geht nie ins Release -- seine Saetze gehoeren nicht in die Sprachdateien.
        if "Lokal" in p.parts:
            continue
        if "bin" in p.parts or "obj" in p.parts:
            continue
        # UEBER DIE GANZE DATEI, NICHT ZEILENWEISE.
        #
        # Das Muster erlaubt nach "Loc.T(" beliebigen Leerraum -- auch einen
        # Zeilenumbruch. Zeilenweise angewandt sah es den aber nie: jede Zeile
        # wurde fuer sich gesucht, und ein Aufruf der Form
        #
        #     _hinweis.Text = Loc.T(
        #         "The note shows over the game while this car is selected.");
        #
        # stand auf zwei Zeilen und wurde nicht gefunden. Der Satz fehlte damit in
        # _keys.json, galt also weder als uebersetzt noch als FEHLEND -- er kam in
        # keinem Bericht vor und blieb in allen 25 Sprachen still englisch.
        # Am 2026-09-24 an zwei Saetzen des Notizen-Reiters aufgefallen.
        text = p.read_text(encoding="utf-8")
        for treffer in _MUSTER.finditer(text):
            satz = entfluchten(treffer.group(1))
            nr = text.count("\n", 0, treffer.start()) + 1
            gefunden.setdefault(satz, []).append(
                f"{p.relative_to(QUELLE).as_posix()}:{nr}")

    skripte = WORKSPACE / "scripts"
    for name in PY_QUELLEN:
        datei = skripte / name
        if not datei.exists():
            continue
        for satz, nr in _py_saetze(datei.read_text(encoding="utf-8")):
            gefunden.setdefault(satz, []).append(f"scripts/{name}:{nr}")

    return gefunden


def sprachen() -> list[Path]:
    if not LANG.is_dir():
        return []
    return sorted(p for p in LANG.glob("*.json") if not p.name.startswith("_"))


def main(argv: list[str] | None = None) -> int:
    argv = sys.argv[1:] if argv is None else argv
    gefunden = saetze()
    print(f"{len(gefunden)} verschiedene Saetze in der Oberflaeche "
          f"({sum(len(v) for v in gefunden.values())} Fundstellen)")

    if "--write" in argv:
        LANG.mkdir(parents=True, exist_ok=True)
        KEYS.write_text(
            json.dumps({s: gefunden[s] for s in sorted(gefunden)},
                       ensure_ascii=False, indent=1),
            encoding="utf-8")
        print(f"geschrieben: {KEYS.relative_to(WORKSPACE)}")

    dateien = sprachen()
    if not dateien:
        print()
        print("Noch keine Sprachdatei unter config/lang/. Englisch kommt aus dem "
              "Quelltext, es fehlt also nichts -- es gibt nur noch nichts zu waehlen.")
        return 0

    print()
    print(f"{'Sprache':<10} {'uebersetzt':>10} {'fehlt':>7} {'verwaist':>9}")
    print("-" * 40)
    schlimm = 0
    for p in dateien:
        try:
            d = json.loads(p.read_text(encoding="utf-8"))
        except ValueError as fehler:
            print(f"{p.stem:<10} KAPUTT: {fehler}")
            schlimm += 1
            continue
        if not isinstance(d, dict):
            print(f"{p.stem:<10} KAPUTT: kein Objekt")
            schlimm += 1
            continue
        belegt = {k for k, v in d.items() if str(v).strip()}
        fehlt = [s for s in gefunden if s not in belegt]
        verwaist = [s for s in belegt if s not in gefunden]
        print(f"{p.stem:<10} {len(belegt):>10} {len(fehlt):>7} {len(verwaist):>9}")
        if "--ausfuehrlich" in argv:
            for s in fehlt[:8]:
                print(f"           fehlt:    {s[:62]}")
            for s in verwaist[:8]:
                print(f"           verwaist: {s[:62]}")

    return 1 if schlimm else 0


if __name__ == "__main__":
    sys.exit(main())
