"""Namen in den Uebersetzungen angleichen: Reiter der App und Menues des Spiels.

    python scripts/lang_names.py

Laeuft auch am Ende von `lang_build.py`, nach jedem Bau.

## Warum

Die Reiter der App sind seit 2026-09-29 uebersetzt ("Lap delta HUD" heisst auf
Spanisch anders), aber viele aeltere Saetze nennen den Reiter noch englisch: "im
Reiter Lap delta HUD". Ebenso nannten Saetze die Menues des Spiels englisch
("Settings > HUD and Gameplay", "My Cars"), waehrend das Spiel in der Sprache des
Spielers "Ajustes > Interfaz y experiencia de juego" zeigt. Der Nutzer sucht dann
einen Namen, den es auf seinem Bildschirm nicht gibt.

## Was

In jeder Sprachdatei (config/lang/<code>.json) und im uebersetzten Hinweis beim Start
(config/lang/disclosure/<code>.txt) wird ein englischer Name ersetzt durch:
- bei Reitern: die Uebersetzung des Reiternamens in derselben Datei,
- bei Menues des Spiels: die Schreibweise des Spiels (config/game_text.json,
  "menu_terms", aus den Tabellen des Spiels).

Nur mehrteilige Namen -- ein einzelnes Wort wie "Tunes" ist auch ein gewoehnliches
Wort. Laengere Namen zuerst ("Data Out IP Address" vor "Data Out"). Mehrfaches
Ausfuehren aendert nichts mehr.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
LANG = WORKSPACE / "config" / "lang"
SPIELTEXT = WORKSPACE / "config" / "game_text.json"

REITER = ("Live grip telemetry", "All telemetry + outputs", "Vibration test", "Blueprint editor",
          "Rivals overlay", "Lap delta HUD", "Car notes", "Car collection",
          "My times")

# Menues des Spiels, wie die englischen Saetze sie nennen -> englischer Tabellentext.
MENUES = (
    ("Settings › HUD and Gameplay", ("Settings", " › ", "HUD & Gameplay")),
    ("Settings > HUD and Gameplay", ("Settings", " > ", "HUD & Gameplay")),
    ("Settings › HUD & Gameplay", ("Settings", " › ", "HUD & Gameplay")),
    ("HUD and Gameplay", ("HUD & Gameplay",)),
    ("Data Out IP Address", ("Data Out IP Address",)),
    ("Data Out IP Port", ("Data Out IP Port",)),
    ("Upgrades & Tuning", ("Upgrades & Tuning",)),
    ("My Tuning Setups", ("My Tuning Setups",)),
    ("Event Sign Up", ("Event Sign Up",)),
    ("My Cars", ("My Cars",)),
    ("Data Out", ("Data Out",)),
)


def ersetzungen(code: str, tabelle: dict[str, str], menue: dict[str, str]) -> list[tuple[str, str]]:
    paare: list[tuple[str, str]] = []
    for name in REITER:
        neu = tabelle.get(name, "").strip()
        if neu and neu != name:
            paare.append((name, neu))
    for englisch, teile in MENUES:
        if not all(t in menue or t.strip() in ("›", ">") for t in teile):
            continue
        neu = "".join(menue.get(t, t) for t in teile)
        if neu != englisch:
            paare.append((englisch, neu))
    return sorted(paare, key=lambda p: -len(p[0]))


def angleichen(text: str, paare: list[tuple[str, str]], ausser: str | None = None) -> str:
    for alt, neu in paare:
        if alt == ausser or alt not in text:
            continue
        text = text.replace(alt, neu)
    return text


def main() -> int:
    menue_alle = {}
    if SPIELTEXT.exists():
        menue_alle = json.loads(SPIELTEXT.read_text(encoding="utf-8")).get("menu_terms", {})
    geaendert = 0
    for datei in sorted(LANG.glob("*.json")):
        if datei.name.startswith("_"):
            continue
        code = datei.stem
        tabelle = json.loads(datei.read_text(encoding="utf-8"))
        paare = ersetzungen(code, tabelle, menue_alle.get(code, {}))
        if not paare:
            continue
        neu = {}
        n = 0
        for schluessel, wert in tabelle.items():
            # Der Name selbst bleibt, wie er uebersetzt ist; alles andere wird angeglichen.
            ersetzt = angleichen(wert, paare, ausser=schluessel)
            if ersetzt != wert:
                n += 1
            neu[schluessel] = ersetzt
        if n:
            datei.write_text(json.dumps(neu, ensure_ascii=False, indent=1), encoding="utf-8")
            geaendert += n
        hinweis = LANG / "disclosure" / f"{code}.txt"
        if hinweis.exists():
            alt = hinweis.read_text(encoding="utf-8")
            ersetzt = angleichen(alt, paare)
            if ersetzt != alt:
                hinweis.write_text(ersetzt, encoding="utf-8", newline="\n")
                geaendert += 1
    print(f"Namen angeglichen: {geaendert} Stelle(n)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
