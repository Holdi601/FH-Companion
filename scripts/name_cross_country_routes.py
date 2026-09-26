"""Die sechs namenlosen Cross-Country-Positionen benennen -- mit drei Belegen je Name.

    python scripts/name_cross_country_routes.py           zeigen, was gesetzt wuerde
    python scripts/name_cross_country_routes.py --apply   setzen

## Warum das seit dem 2026-08-31 offen war

Die Streckenliste im Spiel zeigt ihren Titel als LAUFSCHRIFT. Jede Aufnahme erwischt
eine andere Drehung, und der Katalog vermerkt selbst: "DAS SPIEL SELBST schneidet
die Namen am Kartenrand ab". Beide Lesewege im Spiel liefern darum nur Bruchstuecke
wie 'Stadium Cross-Country Circt' oder 'Edogawa Cras-eouniry Circ'.

Ein Bruchstueck in den Katalog zu schreiben waere teuer: `correct_route_name` loest
eine spaetere, bessere Bildschirmlesung ueber `similar` auf genau diesen Eintrag
auf, und der Sweep bannt die Zeilen dann DAUERHAFT unter dem verstuemmelten Namen.

## Die drei Belege

**1. Die vollstaendige Liste von aussen.** guides4gamers nennt genau 19
Cross-Country-Strecken -- so viele, wie das Karussell Positionen hat. Dreizehn davon
stehen schon im Katalog. Es bleiben exakt sechs Namen fuer exakt sechs leere
Positionen uebrig. Das allein sagt noch nicht, WELCHER Name auf WELCHE Position
gehoert.

**2. Die Lesungen des Sweeps.** Der Navigator bestaetigt die Karussellposition,
BEVOR er den Namen liest -- der Index ist also sicher, nur der Name ist abgeschnitten.
Am 2026-09-13 gelesen (mehrfach je Position).

**3. Eine unabhaengige Aufnahme vom 2026-08-28** (`route_enumerations_unverified`).
Sie ist um EINS versetzt -- ihr idx N entspricht Position N+1 --, was sich an den
dreizehn bekannten Namen nachpruefen laesst.

Fuenf der sechs Positionen sind damit doppelt belegt: Bruchstueck aus Quelle 2 und
Bruchstueck aus Quelle 3 stimmen ueberein, und beide sind der Anfang genau eines
uebrig gebliebenen Namens aus Quelle 1.

**Die sechste (idx16) folgt aus dem Ausschluss** -- alle anderen sind vergeben. Sie
ist darum als solche gekennzeichnet.

## Was hier NICHT korrigiert wird

`idx3` heisst im Katalog 'Forest Cross-Country Circuit Snow'. Die Liste kennt
'Snow Forest Cross-Country Circuit' -- die Laufschrift war beim Ablesen verdreht.
Der falsche Name steht aber schon an gescannten Boards; ihn zu aendern verlangt,
den Auswertungsbestand mitzuziehen. Das ist eine eigene Aufgabe und keine
Nebenwirkung dieser hier.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
KATALOG = WORKSPACE / "config" / "fh6_board_catalogue.json"

# Position -> (Name, Beleg aus dem Sweep, Beleg aus der Aufnahme 28.08.)
ZUORDNUNG = {
    5:  ("Edogawa Cross-Country Circuit",
         "Edogawa CIN", "idx4: Edogawa Cras-eouniry Circ"),
    7:  ("Stadium Cross-Country Circuit",
         "Stadium Cross-Country Circt", "idx6: Stadium Cross-Country Circi"),
    8:  ("Nangan Cross-Country Circuit",
         "Nangan Cross-Country Circu", "idx7: Nangan Cross-Country Circu"),
    12: ("Tateyama Alpine Cross-Country",
         "Tateyama Alpine Cross-Cour", "idx11: Tateyama Alpine Cross-Cour"),
    15: ("Shimanoyama Cross-Country",
         "Shimanoyama Cross-Countr)", "idx14: Shimanoyama Cross-Country"),
    16: ("Shinjuku Gyoen Cross-Country",
         None, None),   # nur durch Ausschluss
}

# Die vollstaendige Liste, gegen die geprueft wird.
ALLE_19 = [
    "City Docks Cross-Country Circuit", "Edogawa Cross-Country Circuit",
    "Izu Cross-Country", "Legend Island Cross-Country Circuit",
    "Nangan Cross-Country Circuit", "Naruo Cross-Country Circuit",
    "Oka Cross-Country Circuit", "Ruriko-ji Cross-Country",
    "Shimanoyama Cross-Country", "Shinjuku Gyoen Cross-Country",
    "Snow Forest Cross-Country Circuit", "Soni Highlands Cross-Country",
    "Stadium Cross-Country Circuit", "Takashiro Cross-Country",
    "Tateyama Alpine Cross-Country", "Temple Cross-Country", "The Titan",
    "Wind Farm Cross-Country", "Yahikoyama Cross-Country",
]

QUELLE = "https://guides4gamers.com/forza-horizon-6/pois/cross-country/"

# NAMEN IM KATALOG, DIE DIESELBE STRECKE MEINEN WIE EIN LISTENNAME.
#
# Die Zaehlung "sechs leere Positionen, sechs uebrige Namen" ist der eigentliche
# Beleg dieser Zuordnung. Sie geht nur auf, wenn jeder schon vergebene Name auch als
# vergeben ERKANNT wird -- und idx3 heisst im Katalog 'Forest Cross-Country Circuit
# Snow', waehrend die Strecke 'Snow Forest Cross-Country Circuit' heisst. Dieselbe
# Strecke, aber die Laufschrift war beim Ablesen um ein Wort verdreht.
#
# Als Ausnahme AUFGESCHRIEBEN und nicht durch eine lockerere Vergleichsregel
# erschlagen: eine Aehnlichkeitsschwelle wuerde hier zufaellig passen und
# anderswo zwei echte Strecken zusammenwerfen. Der Eintrag sagt genau, welcher
# Name gemeint ist und warum.
VERDREHT = {
    "forestcrosscountrycircuitsnow": "Snow Forest Cross-Country Circuit",
}


def schluessel(text: str) -> str:
    return "".join(c for c in (text or "").lower() if c.isalnum())


def aehnlichkeit(bruchstueck: str, voll: str) -> float:
    """Wie gut erklaert `voll` das Bruchstueck?

    Verglichen wird gegen den ANFANG des vollen Namens, so lang wie das Bruchstueck
    -- denn abgeschnitten ist hinten, und der Rest des Namens darf nicht als
    Abweichung zaehlen. 'Edogawa CIN' gegen 'Edogawa Cross-Country Circuit' waere
    sonst schon deshalb schlecht, weil 17 Zeichen fehlen, die gar nicht gelesen
    werden konnten.
    """
    import difflib
    k, v = schluessel(bruchstueck), schluessel(voll)
    if not k:
        return 0.0
    return difflib.SequenceMatcher(None, k, v[:len(k)]).ratio()


def passt_am_besten(bruchstueck: str, name: str, kandidaten: list) -> tuple[bool, str]:
    """Ist `name` die BESTE Erklaerung fuer das Bruchstueck -- unter allen 19?

    ## Warum diese Frage und nicht "ist es plausibel"

    Die OCR laesst nicht nur Zeichen weg, sie ERSETZT sie: 'Cras-eouniry' fuer
    'Cross-Country', 'CIN' fuer etwas Laengeres. Eine Regel wie "Teilfolge des
    Anfangs" scheitert daran, und eine feste Aehnlichkeitsschwelle waere geraten --
    zu hoch weist Richtiges ab, zu tief laesst Falsches durch.

    "Erklaert dieser Name das Bruchstueck besser als jeder andere Name der Liste,
    und zwar deutlich" ist dagegen selbstpruefend: sie misst die Zuordnung gegen
    ihre eigenen Alternativen. Zwei Strecken, die sich nur im Suffix unterscheiden,
    fallen dabei von selbst als "nicht entscheidbar" auf, statt stillschweigend die
    erstbeste zu bekommen.
    """
    bewertet = sorted(((aehnlichkeit(bruchstueck, k), k) for k in kandidaten),
                      reverse=True)
    beste_punkte, bester = bewertet[0]
    zweite = bewertet[1][0] if len(bewertet) > 1 else 0.0
    if schluessel(bester) != schluessel(name):
        return False, ("%r passt besser auf %r (%.2f) als auf %r (%.2f)"
                       % (bruchstueck, bester, beste_punkte, name,
                          aehnlichkeit(bruchstueck, name)))
    if beste_punkte - zweite < 0.10:
        return False, ("%r passt fast gleich gut auf %r (%.2f) und %r (%.2f) -- "
                       "nicht entscheidbar"
                       % (bruchstueck, bester, beste_punkte, bewertet[1][1], zweite))
    return True, "%.2f, naechstbester %.2f" % (beste_punkte, zweite)


def main(argv: list | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--apply", action="store_true")
    args = parser.parse_args(argv)

    katalog = json.loads(KATALOG.read_text(encoding="utf-8-sig"))
    liste = katalog["route_carousel_by_category"]["Cross-Country"]
    if len(liste) != 19:
        print("Das Karussell hat %d Positionen, die Liste 19 -- hier stimmt etwas "
              "Grundsaetzliches nicht. Abbruch." % len(liste))
        return 1

    vergeben = {}
    leer = []
    for e in liste:
        idx = e.get("route_index") if isinstance(e, dict) else None
        name = e.get("name") if isinstance(e, dict) else e
        if name:
            k = schluessel(name)
            vergeben[k] = idx
            # Der Katalogname kann eine verdrehte Schreibweise sein; dann gilt auch
            # der Listenname als vergeben, sonst bliebe er faelschlich uebrig.
            if k in VERDREHT:
                vergeben[schluessel(VERDREHT[k])] = idx
                print("  idx%-3s %r == %r (verdreht abgelesen)"
                      % (idx, name, VERDREHT[k]))
        else:
            leer.append(idx)

    uebrig = [n for n in ALLE_19 if schluessel(n) not in vergeben]
    print("Im Katalog benannt: %d   leer: %d   aus der Liste uebrig: %d"
          % (len(vergeben), len(leer), len(uebrig)))
    print("Leere Positionen:  " + ", ".join("idx%d" % i for i in leer))
    print("Uebrige Namen:")
    for n in uebrig:
        print("   " + n)

    if len(uebrig) != len(leer):
        print("\nDie Zahlen gehen nicht auf -- so lange wird nichts gesetzt.")
        return 1
    if set(leer) != set(ZUORDNUNG):
        print("\nDie leeren Positionen sind andere als erwartet -- Abbruch.")
        return 1

    print("\n%-5s %-32s %s" % ("idx", "Name", "Belege"))
    setzen = []
    for idx in sorted(ZUORDNUNG):
        name, sweep, aufnahme = ZUORDNUNG[idx]
        if schluessel(name) not in {schluessel(u) for u in uebrig}:
            print("%-5d %-32s FEHLER: steht nicht unter den uebrigen Namen" % (idx, name))
            return 1
        belege = []
        for was, bruch in (("Sweep", sweep),
                           ("Aufnahme 28.08.",
                            aufnahme.split(": ", 1)[1] if aufnahme else None)):
            if not bruch:
                continue
            ok, warum = passt_am_besten(bruch, name, ALLE_19)
            if not ok:
                print("%-5d %-32s FEHLER (%s): %s" % (idx, name, was, warum))
                return 1
            belege.append("%s %r (%s)" % (was, bruch, warum))
        if not belege:
            belege.append("nur durch Ausschluss -- alle anderen sind vergeben")
        print("%-5d %-32s %s" % (idx, name, "; ".join(belege)))
        setzen.append((idx, name, belege))

    if not args.apply:
        print("\nProbelauf -- %d Namen waeren zu setzen. Mit --apply." % len(setzen))
        return 0

    nach_index = {e["route_index"]: e for e in liste if isinstance(e, dict)}
    for idx, name, belege in setzen:
        e = nach_index[idx]
        e["name"] = name
        e["name_source"] = QUELLE
        e["name_evidence"] = belege
        e["named_on"] = "2026-09-13"
    KATALOG.write_text(json.dumps(katalog, ensure_ascii=False, indent=1),
                       encoding="utf-8")
    print("\n%d Namen gesetzt. Der Sweep kann diese Boards jetzt scannen." % len(setzen))
    print("Hinweis: idx3 heisst im Katalog 'Forest Cross-Country Circuit Snow', "
          "die Liste kennt 'Snow Forest Cross-Country Circuit'. Nicht angefasst -- "
          "der falsche Name klebt schon an gescannten Boards.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
