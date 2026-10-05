# -*- coding: utf-8 -*-
"""Die Ausgaben der Werkzeuge in der Sprache des Systems -- Englisch, wo es keine gibt.

Das Gegenstueck zu `Loc.cs` in der App, mit demselben Verfahren und demselben
Sprachspeicher unter `config/lang/`:

**Der englische Satz IST der Schluessel.** `T("Nothing to pack")` statt
`T("pack.empty")`. Eine fehlende Uebersetzung faellt damit auf Englisch zurueck,
ohne dass irgendwo eine englische Datei gepflegt werden muesste -- der Rueckfall ist
der Quelltext selbst. Die Begruendung steht ausfuehrlich in `Loc.cs`.

## Warum das Scan-Werkzeug das ueberhaupt braucht

Es geht an FREMDE Leute. Bis zum 2026-09-15 war es durchgehend deutsch -- ein
Werkzeug, das man einem Freund in Warschau oder Sao Paulo schickt, der dann
"Nichts einzupacken -- es ist kein Board fertig geworden" liest. Englisch ist die
Grundlage, die Sprache des Systems die Kuer.

## Die Konsole

Windows-Konsolen sind haeufig nicht auf UTF-8 eingestellt; kyrillische, griechische
oder japanische Ausgaben werden dann zu Fragezeichen. `konsole_auf_utf8()` stellt um,
wo es geht. Wo es nicht geht, faellt die Sprachwahl auf Englisch zurueck, statt
unleserliche Zeichen zu drucken -- ein Kasten ist schlimmer als ein englischer Satz.
"""
from __future__ import annotations

import json
import locale
import os
import sys
from pathlib import Path

def _lang_dir() -> Path:
    """`config/lang` vom eigenen Ort aus AUFWAERTS suchen.

    Im Projekt liegt es eine Ebene ueber `scripts/`. Im Paket, das ein Freund
    bekommt, liegen die Skripte aber flach neben `config/` -- eine feste Annahme
    `parent.parent` fuende dort nichts, und das Werkzeug waere still wieder
    einsprachig.
    """
    hier = Path(__file__).resolve().parent
    for ordner in (hier, *hier.parents):
        kandidat = ordner / "config" / "lang"
        if kandidat.is_dir():
            return kandidat
    return hier.parent / "config" / "lang"


LANG_DIR = _lang_dir()

_tabelle: dict[str, str] | None = None
_sprache = "en"
_geladen = False
_erzwungen: str | None = None


def konsole_auf_utf8() -> bool:
    """Die Ausgabe auf UTF-8 stellen. Gibt zurueck, ob es gelungen ist."""
    try:
        for strom in (sys.stdout, sys.stderr):
            if hasattr(strom, "reconfigure"):
                strom.reconfigure(encoding="utf-8", errors="replace")
        return True
    except Exception:
        return False


def _kandidaten() -> list[str]:
    """Welche Kennungen in Frage kommen, von genau nach grob."""
    if _erzwungen:
        roh = [_erzwungen]
    else:
        roh = []
        # Reihenfolge mit Bedacht: eine ausdrueckliche Umgebungsvariable schlaegt
        # die Systemeinstellung, wie ueberall sonst auch.
        for quelle in (os.environ.get("FORZA_LANG"),
                       os.environ.get("LC_ALL"),
                       os.environ.get("LANG")):
            if quelle:
                roh.append(quelle.split(".")[0].replace("_", "-"))
                break
        # AUF WINDOWS ZUERST DIE SYSTEMSCHNITTSTELLE.
        #
        # `locale.getlocale()` liefert dort Namen wie "English_Germany" -- keine
        # BCP-47-Kennung, mit der sich eine Datei finden liesse. Am 2026-09-15
        # gemessen: der Rueckfall auf Englisch war reiner Zufall, nicht Koennen.
        # `GetUserDefaultLocaleName` gibt "en-GB", "de-DE", "zh-TW".
        if not roh and sys.platform == "win32":
            try:
                import ctypes
                puffer = ctypes.create_unicode_buffer(85)
                if ctypes.windll.kernel32.GetUserDefaultLocaleName(puffer, 85):
                    roh.append(puffer.value)
            except Exception:
                pass
        if not roh:
            try:
                # getlocale() statt getdefaultlocale(): letzteres ist abgekuendigt.
                name = locale.getlocale()[0] or ""
                # Nur nehmen, was wie eine Kennung aussieht: "English_Germany" ist
                # keine, und "English-Germany.json" wird es nie geben.
                if name and len(name.split("_")[0]) <= 3:
                    roh.append(name.split(".")[0].replace("_", "-"))
            except Exception:
                pass

    # ERST DIE VOLLEN KENNUNGEN, DANN DIE KURZFORMEN -- sonst bekaeme zh-TW ueber
    # "zh" die vereinfachte Schrift. Dieselbe Falle wie in Loc.cs.
    voll = [k for k in roh if k]
    # Die Schriftvariante dazwischenschieben, weil Python keine Elternkette kennt.
    erweitert: list[str] = []
    for k in voll:
        erweitert.append(k)
        klein = k.lower()
        if klein.startswith("zh"):
            if any(t in klein for t in ("tw", "hk", "mo", "hant")):
                erweitert.append("zh-Hant")
            else:
                erweitert.append("zh-Hans")
        # Norwegisch: es gibt nur nb.json -- Nynorsk (nn) und "no" fuehren dorthin.
        if klein.split("-")[0] in ("nn", "no") and "nb" not in erweitert:
            erweitert.append("nb")
    kurz = [k.split("-")[0] for k in voll if "-" in k]
    return erweitert + kurz


def _laden() -> None:
    global _tabelle, _sprache, _geladen
    if _geladen:
        return
    _geladen = True
    _tabelle = None
    _sprache = "en"

    # Kann die Konsole die Zeichen ueberhaupt? Wenn nicht, ist Englisch besser als
    # eine Reihe Fragezeichen.
    kodierung = (getattr(sys.stdout, "encoding", "") or "").lower()
    utf8_faehig = "utf" in kodierung

    for code in _kandidaten():
        if not code or "/" in code or "\\" in code or ".." in code:
            continue
        datei = LANG_DIR / f"{code}.json"
        if not datei.exists():
            continue
        try:
            d = json.loads(datei.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            continue
        if not isinstance(d, dict) or not d:
            continue
        if not utf8_faehig and not _nur_ascii(d):
            # Eine Sprache, die diese Konsole nicht darstellen kann, wird nicht
            # genommen. Lieber lesbar englisch als unlesbar richtig.
            continue
        _tabelle = d
        _sprache = code
        return


def _nur_ascii(d: dict) -> bool:
    try:
        "".join(d.values()).encode("ascii")
        return True
    except (UnicodeEncodeError, AttributeError):
        return False


def waehle(code: str | None) -> None:
    """Eine Sprache erzwingen. None oder "auto" heisst: die des Systems."""
    global _erzwungen, _geladen
    _erzwungen = None if not code or code == "auto" else code
    _geladen = False
    _laden()


def sprache() -> str:
    _laden()
    return _sprache


def bekannt() -> int:
    _laden()
    return len(_tabelle or {})


def verfuegbar() -> list[str]:
    if not LANG_DIR.is_dir():
        return []
    return sorted(p.stem for p in LANG_DIR.glob("*.json")
                  if not p.name.startswith("_"))


def T(englisch: str) -> str:
    """Den Satz in der geltenden Sprache -- oder den englischen."""
    _laden()
    if _tabelle:
        wert = _tabelle.get(englisch)
        if wert and str(wert).strip():
            return wert
    return englisch


if __name__ == "__main__":
    konsole_auf_utf8()
    waehle(sys.argv[1] if len(sys.argv) > 1 else None)
    print(f"Kandidaten: {', '.join(_kandidaten())}")
    print(f"gewaehlt:   {sprache()}")
    print(f"Saetze:     {bekannt()}")
    print(f"vorhanden:  {', '.join(verfuegbar())}")
    print()
    for probe in ("Stop now", "Ready", "Update failed"):
        print(f"  {probe:<20} -> {T(probe)}")
