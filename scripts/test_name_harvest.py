"""Die Namensernte pruefen -- die Regeln, die eine Falschbeschriftung verhindern.

    python scripts/test_name_harvest.py

## Warum es diesen Test gibt

Eine falsch beschriftete Strecke ist der teuerste Fehler in diesem Projekt: 20.000
Zeilen landen unter dem Namen einer ANDEREN echten Strecke, und nichts weist darauf
hin. Am 2026-08-31 ist genau das passiert.

Die Ernte hat darum vier Regeln, und jede davon ist aus einem beobachteten Fehler
entstanden. Sie stehen hier als Test, weil sie sonst beim naechsten Aufraeumen als
"ueberfluessige Sonderfaelle" verschwinden.
"""

from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import harvest_names_from_sweep as h  # noqa: E402

fehler = 0


def pruefe(bedingung: bool, text: str) -> None:
    global fehler
    if bedingung:
        print("  ok    " + text)
    else:
        fehler += 1
        print("  FEHL  " + text)


print("Abgeschnittene Lesungen sind dieselbe Strecke")
# Die OCR laesst Zeichen weg -- hinten UND in der Mitte. Ohne diese Regel spaltet
# sich die Mehrheit und die Strecke bekommt gar keinen Namen.
for roh, erwartet, anzahl in [
    (["Stadium Cross-Country Circt", "Stadium Cross-Country Circuit"],
     "Stadium Cross-Country Circuit", 2),
    (["Nangan Cross-Country Circu"] * 2 + ["Nangan Cross-Country Circuit"],
     "Nangan Cross-Country Circuit", 3),
    (["Tateyama Alpine Cross-Cour", "Tateyama Alpine Cross-Country"],
     "Tateyama Alpine Cross-Country", 2),
    (["Shimanoyama Cross-Countr)", "Shimanoyama Cross-Country"],
     "Shimanoyama Cross-Country", 2),
]:
    name, n = h.beste(roh)
    pruefe(name == erwartet and n == anzahl,
           "%r -> %r (%dx)" % (roh[0][:26], name, n))

print("\nVerschiedene Strecken verschmelzen NICHT -- in beiden Reihenfolgen")
# 'Naruo' und 'Nangan' unterscheiden sich nur im ersten Wort. Ein Aehnlichkeitsmass
# wuerde sie zusammenwerfen; die Teilfolge-Regel darf das nicht.
a = ["Naruo Cross-Country Circuit", "Nangan Cross-Country Circuit",
     "Nangan Cross-Country Circuit"]
b = ["Nangan Cross-Country Circuit", "Naruo Cross-Country Circuit",
     "Naruo Cross-Country Circuit"]
pruefe(h.beste(a) == ("Nangan Cross-Country Circuit", 2), "Nangan gewinnt mit 2")
pruefe(h.beste(b) == ("Naruo Cross-Country Circuit", 2), "Naruo gewinnt mit 2")

print("\nEine kurze Lesung haengt sich nicht an einen langen Namen")
# 'Izu' ist eine Teilfolge von fast allem. Die Laengenschranke haelt es getrennt.
name, n = h.beste(["Izu Cross-Country", "Temple Cross-Country Circuit",
                   "Temple Cross-Country Circuit"])
pruefe((name, n) == ("Temple Cross-Country Circuit", 2),
       "Temple gewinnt, Izu bleibt eigenstaendig")

print("\nBedienelemente sind keine Streckennamen")
# 'Back' stand als "route index 16 confirmed on screen as 'Back'" im Protokoll.
# 'I @ 2,666,890' war einmal ein "Streckenname" im Bestand -- gelesen aus einer
# Punktestandszeile.
for text, erwartet in [("Back", False), ("Routes", False), ("Select", False),
                       ("I @ 2,666,890", False), ("Izu", False), ("", False),
                       ("Temple Cross-Country", True), ("Edogawa CIN", True),
                       ("The Titan", True)]:
    pruefe(h.plausibel(text) == erwartet,
           "plausibel(%r) = %s" % (text, h.plausibel(text)))

print("\nEine Position bekommt ihren EIGENEN Namen zurueck")
# idx08 las am 2026-09-13 dreimal den Namen von idx07, weil das Karussell dort
# systematisch nicht weiterschaltet -- und EINMAL seinen eigenen. Wer die Position
# deswegen ganz verwirft, laesst sie fuer immer namenlos.
kandidaten, verlierer, geklaut = h.zuordnen({
    7: ["Stadium Cross-Country Circuit"] * 3,
    8: ["Stadium Cross-Country Circuit"] * 2 + ["Nangan Cross-Country Circuit"],
}, 19)
pruefe(kandidaten[7][0] == "Stadium Cross-Country Circuit", "idx7 behaelt seinen Namen")
pruefe(kandidaten[8][0] == "Nangan Cross-Country Circuit",
       "idx8 bekommt seine eigene Lesung zurueck, nicht die geklaute")
pruefe(8 not in verlierer, "und gilt nicht als hoffnungslos")
pruefe(geklaut.get(8) == (2, 7), "die zwei geklauten Lesungen sind vermerkt")

print("\nWer NUR Fremdes gelesen hat, bekommt gar nichts")
# Wenn das Karussell bei JEDER Anfahrt stehen blieb, gibt es hier keinen Namen --
# und das ist die richtige Antwort. Lieber ein offenes Board als ein falsch
# beschriftetes.
kandidaten, verlierer, _ = h.zuordnen({
    7: ["Stadium Cross-Country Circuit"] * 3,
    8: ["Stadium Cross-Country Circuit"] * 2,
}, 19)
pruefe(verlierer.get(8) == 7, "idx8 ist als hoffnungslos vermerkt")

print("\nDer Schluessel ignoriert Satzzeichen und Gross-/Kleinschreibung")
pruefe(h.schluessel("Cross-Country Circuit") == h.schluessel("crosscountry circuit"),
       "Bindestrich und Leerzeichen zaehlen nicht")

print()
if fehler:
    print("%d Pruefung(en) fehlgeschlagen." % fehler)
    raise SystemExit(1)
print("alles bestanden")
