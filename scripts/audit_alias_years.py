"""Alias-Eintraege finden, deren Jahreszahl dem zugeordneten Auto widerspricht.

    python scripts/audit_alias_years.py
    python scripts/audit_alias_years.py --zeigen 40
    python scripts/audit_alias_years.py --nach-quelle

## Die Pruefung

`config/fh6_screen_name_aliases.tsv` bildet einen normalisierten Bildschirmnamen auf
ein Auto samt Baujahr ab:

    0671 lambo countach   Lamborghini Countach LP5000 QV   1988   wiki_abbreviated_as

Viele Bildschirmnamen tragen das Baujahr selbst mit, zweistellig: `lambo countach 88`,
`nissan gtr 20`, `ford mustang 24`. Wo das der Fall ist, muss es zum Baujahr des
zugeordneten Autos passen. Tut es das nicht, ist die Zuordnung falsch -- und zwar
ohne dass es irgendwo auffiele: die Zeilen landen unter einem Auto, das es so nie
war.

Gefunden wurde das am 2026-09-14 an einem einzelnen Eintrag:

    ed gt r 12   ->   Mercedes-AMG GT R   2017

Aus einem Nissan-GT-R-Fragment mit Baujahr 2012 wurde ein Mercedes von 2017.

## Was diese Pruefung NICHT tut

Sie korrigiert nichts. Eine Zuordnung zu aendern heisst, Zeilen einem anderen Auto
zuzuschlagen -- das gehoert angesehen, nicht automatisch getan. Sie zaehlt und legt
vor.

## Die Grenze bei 30

`00`-`30` wird als `20xx` gelesen, `31`-`99` als `19xx`. Das Spiel erschien 2026.
Ein Eintrag, dessen Zahl sich so nicht deuten laesst, zaehlt als unauffaellig --
lieber einen echten Fall uebersehen als einen erfinden.
"""

from __future__ import annotations

import argparse
import re
from collections import Counter
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
DATEI = WORKSPACE / "config" / "fh6_screen_name_aliases.tsv"

# Zweistellige Zahl als eigenes Wort -- mit oder ohne Apostroph davor.
JAHR_KURZ = re.compile(r"(?:^|[\s'])(\d{2})(?=$|\s)")


def moegliche_jahre(bildschirmname: str, autoname: str = "") -> set:
    """Zweistellige Zahlen, die wirklich ein Baujahr sein koennen.

    ## Zwei Sorten Zahlen, die keine Jahre sind

    **Modellnamen.** 'toyota 86' meint den Toyota 86, 'm bg 65' den Mercedes-Benz
    G 65 AMG, 'gma t 50' die GMA T.50, '34 vw beetle' den Rallye-Beetle mit der
    Startnummer 34. Der erste Entwurf dieser Pruefung hielt alle vier fuer
    Baujahre und meldete 1.300 "Widersprueche", von denen die meisten keine waren.

    Die Regel dagegen ist einfach: taucht die Zahl im NAMEN DES AUTOS auf, ist sie
    Teil des Modells. 'Toyota 86' enthaelt '86', 'G 65 AMG' enthaelt '65' -- also
    kein Jahr.

    **Rangziffern.** Die OCR schleppt die Rangspalte mit: '0071 ferrari f80 24',
    '03 lambo huracan'. Eine Zahl GANZ AM ANFANG ist darum nie ein Baujahr.
    """
    n = (bildschirmname or "").strip()
    # Fuehrende Zahlengruppe wegschneiden: das ist die Rangspalte, nicht das Auto.
    n = re.sub(r"^\d+\s+", "", n)
    im_auto = set(re.findall(r"\d+", autoname or ""))
    raus = set()
    for m in JAHR_KURZ.finditer(n):
        zz = m.group(1)
        if zz in im_auto or any(zz in z for z in im_auto):
            continue
        raus.add(int(("20" if int(zz) <= 30 else "19") + zz))
    return raus


def main(argv: list | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--zeigen", type=int, default=25,
                        help="wie viele Widersprueche auflisten")
    parser.add_argument("--nach-quelle", action="store_true",
                        help="nach der Herkunftsspalte gruppieren")
    args = parser.parse_args(argv)

    if not DATEI.exists():
        print("Fehlt: " + DATEI.as_posix())
        return 1

    zeilen = [z for z in DATEI.read_text(encoding="utf-8", errors="replace").splitlines()
              if z and not z.startswith("#")]
    mit_jahr = 0
    widerspruch = []
    quellen = Counter()

    for z in zeilen:
        t = z.split("\t")
        if len(t) < 3:
            continue
        name, auto, jahr = t[0], t[1], t[2]
        quelle = t[3] if len(t) > 3 else ""
        try:
            auto_jahr = int(jahr)
        except (TypeError, ValueError):
            continue
        gemeint = moegliche_jahre(name, auto)
        if not gemeint:
            continue
        mit_jahr += 1
        if auto_jahr not in gemeint:
            widerspruch.append((name, auto, auto_jahr, sorted(gemeint), quelle))
            quellen[quelle] += 1

    print("Alias-Eintraege insgesamt: %s" % f"{len(zeilen):,}".replace(",", "."))
    print("Davon mit Jahreszahl im Bildschirmnamen: %s"
          % f"{mit_jahr:,}".replace(",", "."))
    print("Davon WIDERSPRUECHLICH: %s  (%.1f %%)"
          % (f"{len(widerspruch):,}".replace(",", "."),
             100.0 * len(widerspruch) / max(1, mit_jahr)))

    if args.nach_quelle:
        print("\nNach Herkunft:")
        for q, n in quellen.most_common():
            print("  %-28s %6d" % (q or "(ohne)", n))

    if widerspruch and args.zeigen:
        print("\nDie ersten %d:" % min(args.zeigen, len(widerspruch)))
        print("  %-28s %-38s %-6s %s" % ("Bildschirmname", "zugeordnet", "Jahr", "Name sagt"))
        for name, auto, auto_jahr, gemeint, _ in widerspruch[:args.zeigen]:
            print("  %-28s %-38s %-6d %s"
                  % (name[:28], auto[:38], auto_jahr,
                     "/".join(str(g) for g in gemeint)))

    if not widerspruch:
        print("\nKein Widerspruch -- jede Jahreszahl im Namen passt zum Auto.")
    else:
        print("\nJeder dieser Eintraege ordnet Zeilen einem Auto zu, das ein anderes "
              "Baujahr hat als der Bildschirm nennt.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
