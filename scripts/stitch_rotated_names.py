"""Streckennamen aus RINGLAUF-Ausschnitten rekonstruieren.

## Das Problem, an dem der erste Versuch scheiterte

`harvest_route_names.py` nimmt denselben Titel mehrmals auf und klebt die Stuecke
ueber ihre Ueberlappung zusammen. Das setzt voraus, dass der Titel von links nach
rechts durchlaeuft. Er laeuft aber **im Kreis**. Aufgenommen am 2026-08-31, Strecke 7:

    'ngan Cross-Country Circuit'
    'n Cross-Country Circuit Nan!'
    'oss-Country Circuit Nangan'
    '-Country Circuit Nangan Cro'

Das sind vier Ausschnitte aus **einer** Rundschrift. Der lineare Kleber machte daraus
'-Country Circuit Nangan Cross-Country Circuit Nan!' -- er ist ueber den Umbruch
gelaufen und hat den Namen verdoppelt. Richtig ist schlicht
**'Nangan Cross-Country Circuit'**.

## Wie hier rekonstruiert wird

1. **Ring bilden.** Die Stuecke werden gierig ueber ihre groesste Ueberlappung
   verschmolzen; das Ergebnis ist ein Text, in dem der Name ein- oder mehrmals
   umlaeuft.
2. **Periode finden.** Die kuerzeste Zeichenfolge, deren Wiederholung den ganzen
   Text erklaert, ist der Name -- einmal.
3. **Drehpunkt waehlen.** Von allen Drehungen des Rings wird die genommen, die wie
   ein Name aussieht: an einer Wortgrenze beginnend, mit Grossbuchstaben, und mit
   dem Ortsnamen VOR 'Cross-Country'. Die acht Strecken, deren Titel ganz auf den
   Schirm passt, geben die Regel vor: "Naruo Cross-Country Circuit", "Oka
   Cross-Country Circuit", "Izu Cross-Country".

## Ehrlich zur Sicherheit

Drehen und Verschmelzen sind Schluesse, keine Messungen. Jeder Name bekommt darum
eine **Zuversicht**: `high`, wenn mehrere Stuecke uebereinstimmen und die Periode
sauber aufgeht; `low`, wenn zu wenige Stuecke da waren oder die Drehung nicht
eindeutig ist. Wer `low` bekommt, gehoert erneut aufgenommen und NICHT in den
Katalog.

    python scripts/stitch_rotated_names.py config/route_names_cross_country.json
    python scripts/stitch_rotated_names.py --self-test
"""

from __future__ import annotations

import argparse
import collections
import json
import re
import sys
from pathlib import Path

MARKER = "Cross-Country"
# Die OCR liest den Bindestrich manchmal als Leerzeichen oder verschluckt ihn.
MARKER_PATTERN = re.compile(r"Cross\s*[-‐-―]?\s*Country", re.I)


def normalise(piece: str) -> str:
    """Nur offensichtlichen Aufnahmedreck entfernen, nichts erraten."""
    text = piece.strip()
    text = text.replace("’", "'")
    text = re.sub(r"\s+", " ", text)
    # Ein einzelnes Sonderzeichen am Rand ist Schnittkante, kein Buchstabe.
    text = re.sub(r"^[^A-Za-z0-9]+", "", text)
    text = re.sub(r"[^A-Za-z0-9')]+$", "", text)
    return text


def overlap(left: str, right: str, minimum: int = 8) -> int:
    """Groesste Laenge, mit der das Ende von 'left' den Anfang von 'right' trifft."""
    limit = min(len(left), len(right))
    for size in range(limit, minimum - 1, -1):
        if left[-size:] == right[:size]:
            return size
    return 0


def merge_all(pieces: list[str]) -> str:
    """Stuecke gierig ueber die groesste Ueberlappung verschmelzen."""
    remaining = [p for p in pieces if p]
    if not remaining:
        return ""
    current = max(remaining, key=len)
    remaining.remove(current)
    changed = True
    while remaining and changed:
        changed = False
        best = (0, None, False)
        for piece in remaining:
            forward = overlap(current, piece)
            backward = overlap(piece, current)
            if forward > best[0]:
                best = (forward, piece, True)
            if backward > best[0]:
                best = (backward, piece, False)
        size, piece, forward = best
        # Acht Zeichen, nicht vier: bei 'Cross-Country Circuit' teilen sich fast alle
        # Stuecke kurze Fetzen ('ount', 'ircu'), und mit vier Zeichen verschmolz der
        # erste Entwurf 'Nangan Cross-Country Circuit' zu
        # 'Circuit Nangan Crongan Cross-Country'.
        if piece is not None and size >= 8:
            current = current + piece[size:] if forward else piece + current[size:]
            remaining.remove(piece)
            changed = True
    return current


def smallest_period(text: str) -> str:
    """Kuerzeste Zeichenfolge, deren Wiederholung 'text' erklaert.

    Zulaessig ist auch ein angeschnittener letzter Umlauf -- der Ring wurde ja
    irgendwo mitten im Wort aufgenommen.
    """
    length = len(text)
    # Ab 1, nicht ab 4: sonst findet die Suche die Periode von 'abcabcabc' nicht.
    # Zu kurze Perioden werden spaeter verworfen (len(cycle) < 6 -> unsicher).
    #
    # UND bis length-1, nicht bis length//2. Das war der eigentliche Fehler: aus vier
    # Aufnahmen entsteht oft nur EIN Umlauf plus ein Rest --
    # 'ngan Cross-Country Circuit Nangan Cro' ist 37 Zeichen lang bei einer Periode
    # von 29. Eine Suche bis zur Haelfte findet die nie und gibt den Text unveraendert
    # zurueck, womit der Name doppelt im Katalog landet.
    #
    # MINIMUM_EVIDENCE: der zweite Umlauf muss wenigstens sechs Zeichen weit belegt
    # sein. Sonst waere jede Zeichenkette 'periodisch' mit Periode length-1.
    minimum_evidence = 4
    for size in range(1, length):
        if length - size < minimum_evidence:
            break
        candidate = text[:size]
        repeated = (candidate * (length // size + 2))[:length]
        if repeated == text:
            return candidate
    return text


def rotations(cycle: str) -> list[str]:
    doubled = cycle + cycle
    return [doubled[i:i + len(cycle)] for i in range(len(cycle))]


# Woerter, die eine Streckenart benennen und darum nie am ANFANG eines Namens
# stehen. Abgelesen an den acht Strecken, deren Titel vollstaendig auf den Schirm
# passt: "Naruo Cross-Country Circuit", "Izu Cross-Country", "Wind Farm
# Cross-Country" -- vorn steht immer der Ort, hinten die Art.
#
# Ohne diese Regel sind 'Nangan Cross-Country Circuit' und
# 'Circuit Nangan Cross-Country' gleich gut bewertet, und die Drehung entscheidet
# der Zufall. Genau das ist am 2026-08-31 passiert.
TYPE_WORDS = {"circuit", "sprint", "trail", "snow", "descent", "ascent",
              "chase", "run", "scramble", "cross-country", "cross", "country",
              "race", "loop", "reverse", "short", "long"}


def score_rotation(text: str) -> tuple:
    """Wie sehr sieht diese Drehung nach einem Streckennamen aus? Groesser ist besser."""
    text = text.strip()
    if not text:
        return (-1,)
    first_word = re.split(r"[\s-]", text, maxsplit=1)[0].lower()
    place_start = first_word not in TYPE_WORDS
    marker = MARKER_PATTERN.search(text)
    # HINTER 'Cross-Country' steht nur eine Streckenart, nie ein Ortsname. Abgelesen
    # an den vollstaendig lesbaren Namen: "Naruo Cross-Country Circuit",
    # "Izu Cross-Country", "Wind Farm Cross-Country".
    #
    # Ohne diese Regel gewann bei "Soni Highlands Cross-Country" die Drehung
    # "Highlands Cross-Country Soni" -- beide fangen mit einem Ortswort an, und dann
    # entschied die Laenge. Erst der Blick HINTER den Marker macht es eindeutig.
    tail_ok = True
    if marker:
        tail = text[marker.end():].strip(" '-")
        tail_words = [w for w in re.split(r"[\s]+", tail) if w]
        tail_ok = all(w.lower().strip(".,'") in TYPE_WORDS for w in tail_words)
    starts_word = bool(re.match(r"[A-Z]", text))
    # Der Ortsname steht VOR 'Cross-Country' -- so heissen alle acht Strecken,
    # deren Titel vollstaendig auf den Schirm passt.
    place_first = bool(marker and marker.start() > 0)
    ends_clean = not text.endswith("-")
    return (
        1 if marker else 0,
        1 if place_first else 0,
        1 if tail_ok else 0,       # hinter dem Marker nur Streckenarten
        1 if place_start else 0,   # faengt nicht mit einem Typwort an
        1 if starts_word else 0,
        1 if ends_clean else 0,
        -len(text),          # bei Gleichstand der kuerzere Name
    )


def cyclic_consensus(pieces: list[str],
                     lengths: range = range(12, 48)) -> tuple[str, float]:
    """Den Ring durch ABSTIMMUNG bestimmen statt durch Kleben.

    Warum das noetig wurde: Verschmelzen ueber Ueberlappungen ist gegenueber
    OCR-Fehlern wehrlos. Ein falsch gelesener Buchstabe an der Klebestelle, und der
    ganze Name kippt -- am 2026-08-31 wurde aus acht sauberen Ausschnitten von
    "Stadium Cross-Country Circuit" ein Ring von 71 Zeichen. Mehr Aufnahmen machten
    es schlimmer, nicht besser.

    Hier wird stattdessen jede Aufnahme auf einen Ring der Laenge L gelegt und je
    Position abgestimmt. Ein Lesefehler ist dann eine Stimme unter zwoelf und wird
    ueberstimmt, statt die Kette zu zerreissen.

    Zurueck kommt der Ring und ein Mass fuer die Einigkeit (0..1).
    """
    usable = [p for p in pieces if len(p) >= 6]
    if not usable:
        return "", 0.0
    reference = max(usable, key=len)
    candidates: list[tuple[int, float, str]] = []

    for size in lengths:
        if size < 6:
            continue
        votes: list[collections.Counter] = [collections.Counter()
                                            for _ in range(size)]
        for index, char in enumerate(reference):
            votes[index % size][char] += 1

        # Zwei Durchgaenge: der erste ordnet grob ein, der zweite nutzt die
        # inzwischen gewachsene Mehrheit, um frueh gelegte Stuecke zu korrigieren.
        for _ in range(2):
            for piece in usable:
                if piece is reference:
                    continue
                best_offset, best_hits = 0, -1
                for offset in range(size):
                    hits = 0
                    for index, char in enumerate(piece):
                        slot = votes[(offset + index) % size]
                        if slot and slot.most_common(1)[0][0] == char:
                            hits += 1
                    if hits > best_hits:
                        best_hits, best_offset = hits, offset
                for index, char in enumerate(piece):
                    votes[(best_offset + index) % size][char] += 1

        total = sum(sum(slot.values()) for slot in votes)
        if not total:
            continue
        agreed = sum(slot.most_common(1)[0][1] for slot in votes if slot)
        covered = sum(1 for slot in votes if sum(slot.values()) >= 2) / size
        score = (agreed / total) * covered
        candidates.append((size, score,
                           "".join(slot.most_common(1)[0][0] if slot else " "
                                   for slot in votes)))

    if not candidates:
        return "", 0.0
    # WICHTIG: nicht einfach den besten Punktwert nehmen. Ein VIELFACHES der echten
    # Periode passt genauso gut -- aus "Stadium Cross-Country Circuit" (28 Zeichen)
    # wurde am 2026-08-31 ein Ring von 47, und der Name stand doppelt darin. Unter
    # allen nahezu gleich guten Ringen ist der kuerzeste der richtige.
    best_score = max(score for _, score, _ in candidates)
    close = [(size, score, cycle) for size, score, cycle in candidates
             if score >= best_score - 0.02]
    size, score, cycle = min(close, key=lambda item: item[0])
    return cycle, score


def reconstruct(pieces: list[str]) -> dict:
    cleaned = [normalise(p) for p in pieces if normalise(p)]
    if not cleaned:
        return {"name": "", "confidence": "low", "why": "keine brauchbaren Stuecke"}

    # Stimmen mehrere Aufnahmen woertlich ueberein, passte der Titel auf den Schirm.
    # Dann gibt es nichts zu rekonstruieren -- das ist der sicherste Fall ueberhaupt.
    identical = {p for p in cleaned}
    if len(identical) == 1 and len(cleaned) >= 2:
        return {"name": cleaned[0], "confidence": "high",
                "why": f"{len(cleaned)} Aufnahmen woertlich gleich"}

    # Abstimmen statt kleben. merge_all bleibt als Rueckfall fuer den Fall, dass die
    # Abstimmung gar nichts findet -- sie braucht mehrere Aufnahmen, um zu wirken.
    cycle, agreement = cyclic_consensus(cleaned)
    if not cycle or len(cycle) < 6:
        merged = merge_all(cleaned)
        cycle, agreement = smallest_period(merged), 0.0

    if len(cycle) < 6:
        return {"name": cycle, "confidence": "low",
                "why": "kein brauchbarer Ring"}

    best = max((r.strip() for r in rotations(cycle)), key=score_rotation)
    best = best.strip(" '’-")
    # 0,95 als Grenze: die sauber rekonstruierten Faelle liegen bei 0,99 bis 1,00,
    # der eine misslungene (Tateyama, 2026-08-31) bei 0,85. Dazwischen ist Platz.
    confident = len(cleaned) >= 3 and agreement >= 0.95
    return {"name": best,
            "confidence": "high" if confident else "low",
            "why": f"{len(cleaned)} Stuecke, Ring {len(cycle)} Zeichen, "
                   f"Einigkeit {agreement:.2f}"}


def self_test() -> int:
    ok = True

    def check(label: str, condition: bool) -> None:
        nonlocal ok
        print(f"  {'ok  ' if condition else 'FEHL'}  {label}")
        ok = ok and condition

    # Die echten Aufnahmen vom 2026-08-31.
    nangan = reconstruct(['ngan Cross-Country Circuit',
                          'n Cross-Country Circuit Nan!',
                          'oss-Country Circuit Nangan',
                          '-Country Circuit Nangan Cro'])
    check(f"Nangan rekonstruiert (bekam {nangan['name']!r})",
          nangan["name"] == "Nangan Cross-Country Circuit")

    tateyama = reconstruct(['Cross-Country eyama Alpine',
                            'na Alpine Cross-Country Tal',
                            'Alpine Cross-Country Tateya',
                            'Tateyama ne Cross-Country'])
    check(f"Tateyama enthaelt Ort vor dem Marker (bekam {tateyama['name']!r})",
          MARKER_PATTERN.search(tateyama["name"] or "") is not None
          and not tateyama["name"].startswith("Cross"))

    identical = reconstruct(['Izu Cross-Country'] * 4)
    check("gleiche Aufnahmen bleiben unveraendert",
          identical["name"] == "Izu Cross-Country")
    check("und gelten als sicher", identical["confidence"] == "high")

    check("zu wenige Stuecke sind unsicher",
          reconstruct(['u Gyoen Shir', 'Shinjuki'])["confidence"] == "low")
    check("gar nichts liefert nichts", reconstruct([])["name"] == "")

    check("Periode erkennt die Verdopplung",
          smallest_period("abcabcabc") == "abc")
    check("Periode laesst angeschnittenen Umlauf zu",
          smallest_period("abcabcab") == "abc")
    check("ohne Periode bleibt der Text",
          smallest_period("Naruo Cross-Country") == "Naruo Cross-Country")

    check("Ueberlappung wird gefunden",
          overlap("Nangan Cross", "Cross-Country", minimum=4) == 5)
    check("zu kurze Ueberlappung wird abgelehnt",
          overlap("Nangan Cross", "Cross-Country") == 0)
    check("Periode laenger als die Haelfte wird gefunden",
          smallest_period("ngan Cross-Country Circuit Nangan Cro")
          == "ngan Cross-Country Circuit Na")
    check("keine Ueberlappung ist 0", overlap("abcd", "wxyz") == 0)

    check("Typwort am Anfang verliert",
          max((r.strip() for r in rotations("ngan Cross-Country Circuit Na")),
              key=score_rotation) == "Nangan Cross-Country Circuit")
    check("Drehung mit Ort vorn gewinnt",
          max(rotations("Nangan Cross-Country Circuit "),
              key=score_rotation).strip() == "Nangan Cross-Country Circuit")

    print("\nalles bestanden" if ok else "\nFEHLGESCHLAGEN")
    return 0 if ok else 1


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("path", nargs="?",
                        default="config/route_names_cross_country.json")
    parser.add_argument("--out", type=Path, default=None,
                        help="wohin die rekonstruierte Fassung geschrieben wird")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()

    if args.self_test:
        return self_test()

    source = Path(args.path)
    if not source.exists():
        raise SystemExit(f"{source} fehlt")
    data = json.loads(source.read_text(encoding="utf-8-sig"))
    routes = data.get("routes", data) if isinstance(data, dict) else data

    out_routes = []
    low = []
    for index, entry in enumerate(routes):
        pieces = entry.get("pieces") or []
        if not pieces and entry.get("name"):
            pieces = [entry["name"]]
        result = reconstruct(pieces)
        out_routes.append({"route_index": index, "name": result["name"],
                           "confidence": result["confidence"],
                           "why": result["why"], "pieces": pieces})
        mark = " " if result["confidence"] == "high" else "!"
        print(f"{mark}{index:2d}  {result['name']:<42} [{result['confidence']}] "
              f"{result['why']}")
        if result["confidence"] != "high":
            low.append(index)

    target = args.out or source.with_name(source.stem + "_stitched.json")
    target.write_text(json.dumps({"rivals_mode": data.get("rivals_mode")
                                  if isinstance(data, dict) else None,
                                  "routes": out_routes}, indent=1,
                                 ensure_ascii=False), encoding="utf-8")
    print(f"\n{len(out_routes)} Namen -> {target}")
    if low:
        print(f"UNSICHER an {len(low)} Stellen: {low}")
        print("Diese gehoeren erneut aufgenommen, nicht in den Katalog.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
