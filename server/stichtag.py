"""Der Stichtag fuer die Rivals-Daten: nur Runden, die seit diesem Tag gescannt wurden.

## Warum (seit 2026-11-10)

Am 2026-11-03 hat das Spiel Fahrzeuge abgeschwaecht und seine Rivals-Bestenlisten
geloescht. Der Datensatz behaelt aber die Geschichte aller Laeufe (siehe "boards keep
history" in build_analytics_dataset.py) -- ohne Stichtag stuenden die alten Zeiten weiter
neben den neuen, und ein Auto, das es so nicht mehr gibt, gewaenne die Wertung.

Seite, App und Server sehen darum standardmaessig nur, was seit dem Stichtag gescannt
wurde; wer will, waehlt "alle Daten". Diese Datei ist die EINE Stelle, an der der Server
den Stichtag kennt: VORGABE, ueberschreibbar mit "rivals_since" in config/local.json oder
der Umgebung (FHC_RIVALS_SINCE), "all" fuer alles.

## Das Format (build_analytics_dataset.py), je Board, alles optional

    days   die Tage (JJJJ-MM-TT, aufsteigend), an denen die Zeilen dieses Boards ZULETZT
           gescannt wurden -- je Zeile zaehlt der juengste Lauf, der sie enthielt
    lday   je Runde (parallel zu lgrp) der Index in days; fehlt bei nur einem Tag
    gdays  je Gruppe und Tag die Zahl der Zeilen, flach (Gruppe * len(days) + Tag);
           fehlt bei nur einem Tag (dann gilt gcount)
    dstat  je Tag [rows, valid, invalid, maxRank], flach; fehlt bei nur einem Tag
    ddist  je Tag die Verteilung der gueltigen Zeilen AB diesem Tag (ddist[0] ist null,
           dafuer steht dist); fehlt bei nur einem Tag
    isz    die Groessenmessungen [Tag, impl, iest], die beste zuerst
    xgrp, xms, xrank, xpi, xday
           Runden NUR fuer Sichten ab einem Stichtag (siehe unten); fehlen meist

Die Felder, die es vorher gab, bleiben, wie sie waren -- ueber ALLE Daten, die fuenf
schnellsten Runden je Gruppe in lgrp/lms/lrank/lpi. Ein alter Leser sieht keinen
Unterschied. Eine Sicht ab einem Stichtag braucht aber die fuenf schnellsten AB DIESEM Tag,
und die koennen hinter aelteren, schnelleren Runden liegen. Darum stehen in x... die Runden,
die keine der fuenf schnellsten ueber alles sind, aber weniger als fuenf schnellere aus
ihrem Tag oder spaeter haben -- getrennt, weil eine solche Runde (aus einem anderen Lauf)
einen kleineren Rang tragen kann und bei einem alten Leser bestRank verschoben haette.
Eine Sicht nimmt beide Listen ab dem Stichtag und behaelt je Gruppe die fuenf schnellsten.

Ein Board ohne "days" stammt aus einem Datensatz vor diesem Format, also von vor dem
Stichtag: es faellt bei jedem Stichtag weg. Ein Board ohne eine Zeile ab dem Stichtag
ist "noch kein Board".

Dieselbe Rechnung steht in scripts/dataset_cutoff.js (Seite) und in
haptics/ForzaHaptics.Tester/Rivals/RivalsDataset.cs (App). Wer hier etwas aendert,
aendert alle drei; scripts/test_dataset_cutoff.py vergleicht Python und JavaScript.
"""
from __future__ import annotations

import re
from datetime import datetime, timezone

try:
    import local_settings
except ImportError:  # ausserhalb des Servers ohne dessen Pfad
    local_settings = None

#: Der Tag, an dem das Spiel seine Rivals-Bestenlisten geloescht hat.
VORGABE = "2026-11-03"

#: Was "alle Daten" heisst, im Text einer Einstellung.
ALLE = ("all", "alle", "none", "")

#: Die Felder je Tag -- in einer Sicht ab einem Stichtag sind sie verbraucht.
JE_TAG = ("lday", "gdays", "dstat", "ddist", "xgrp", "xms", "xrank", "xpi", "xday")

#: Runden je Gruppe, wenn meta.laps_per_group fehlt (build_analytics_dataset.LAPS_PER_GROUP).
JE_GRUPPE = 5

_TAG = re.compile(r"^\d{4}-\d{2}-\d{2}$")


def lesen(text, vorgabe: str | None = VORGABE) -> str | None:
    """Ein Stichtag aus einer Einstellung: "JJJJ-MM-TT", None fuer alle Daten.

    Unlesbares gilt als `vorgabe` -- ein Tippfehler in der Einstellung soll nicht
    stillschweigend die alten Daten zurueckholen.
    """
    wert = str(text if text is not None else "").strip()
    if wert.lower() in ALLE:
        return None
    if _TAG.match(wert):
        try:
            datetime.strptime(wert, "%Y-%m-%d")
            return wert
        except ValueError:
            pass
    return vorgabe


def stichtag() -> str | None:
    """Der Stichtag des Servers: config/local.json "rivals_since", sonst VORGABE."""
    roh = local_settings.get("rivals_since", VORGABE) if local_settings is not None else VORGABE
    return lesen(roh)


def tag_von_zeitpunkt(sekunden) -> str:
    """Sekunden seit 1970 als Tag (UTC) -- so tragen eingereichte Runden und Rennen ihre Zeit."""
    try:
        return datetime.fromtimestamp(float(sekunden), timezone.utc).strftime("%Y-%m-%d")
    except (TypeError, ValueError, OverflowError, OSError):
        return ""


def gilt(tag, seit: str | None) -> bool:
    """Liegt `tag` (Text, beginnt mit JJJJ-MM-TT) am oder nach dem Stichtag?"""
    if seit is None:
        return True
    return str(tag or "")[:10] >= seit


def _ganz(liste, i, vorgabe=0):
    try:
        wert = liste[i]
    except (IndexError, TypeError, KeyError):
        return vorgabe
    return wert if isinstance(wert, int) and not isinstance(wert, bool) else vorgabe


def board_ab(b: dict, seit: str | None, je_gruppe: int = JE_GRUPPE) -> dict | None:
    """Ein Board, wie es ab dem Stichtag aussieht -- oder None, wenn davon nichts bleibt.

    Gibt ein neues dict zurueck (das alte bleibt unberuehrt). In der Sicht stimmen
    gcar/gsig/gcount/lgrp/lms/lrank/lpi, rows/valid/invalid/maxRank, dist und impl/iest
    fuer den Stichtag; die Felder je Tag sind darin verbraucht. `je_gruppe`: wie viele
    Runden je Gruppe bleiben (meta.laps_per_group).
    """
    if seit is None:
        return b
    tage = b.get("days")
    if not isinstance(tage, list) or not tage:
        return None
    j0 = next((j for j, t in enumerate(tage) if str(t) >= seit), None)
    if j0 is None:
        return None
    neu = {k: v for k, v in b.items() if k not in JE_TAG}
    neu["days"] = tage[j0:]
    groessen = b.get("isz")
    if isinstance(groessen, list):
        beste = next((e for e in groessen
                      if isinstance(e, list) and len(e) >= 3 and str(e[0]) >= seit), None)
        neu["impl"], neu["iest"] = (int(beste[1] or 0), str(beste[2] or "")) if beste else (0, "")
    if j0 == 0:
        return neu

    r = len(tage)
    lday, gdays, dstat = b.get("lday"), b.get("gdays"), b.get("dstat")
    gcar, gsig = b.get("gcar") or [], b.get("gsig") or []
    if not isinstance(lday, list) or not isinstance(gdays, list) or not isinstance(dstat, list) \
            or len(gdays) != len(gcar) * r or len(dstat) != 4 * r:
        # Ohne die Angaben je Tag laesst sich nicht sagen, was neu ist -- dann lieber nichts.
        return None
    zeilen = sum(_ganz(dstat, 4 * j) for j in range(j0, r))
    if zeilen <= 0:
        return None
    neu["rows"] = zeilen
    neu["valid"] = sum(_ganz(dstat, 4 * j + 1) for j in range(j0, r))
    neu["invalid"] = sum(_ganz(dstat, 4 * j + 2) for j in range(j0, r))
    neu["maxRank"] = max(_ganz(dstat, 4 * j + 3) for j in range(j0, r))
    verteilungen = b.get("ddist")
    neu["dist"] = verteilungen[j0] if isinstance(verteilungen, list) and j0 < len(verteilungen) else None

    stelle: dict[int, int] = {}
    ncar, nsig, ncount = [], [], []
    for g in range(len(gcar)):
        n = sum(_ganz(gdays, g * r + j) for j in range(j0, r))
        if n <= 0:
            continue
        stelle[g] = len(ncar)
        ncar.append(gcar[g])
        nsig.append(gsig[g] if g < len(gsig) else 0)
        ncount.append(n)
    # Die Runden ab dem Stichtag aus beiden Listen, je Gruppe die schnellsten `je_gruppe`.
    kandidaten: dict[int, list] = {}
    for gruppe, zeiten, raenge, pis, tage_je in (("lgrp", "lms", "lrank", "lpi", "lday"),
                                                  ("xgrp", "xms", "xrank", "xpi", "xday")):
        grp, ms, rang, pi, tag = (b.get(gruppe) or [], b.get(zeiten) or [], b.get(raenge) or [],
                                  b.get(pis), b.get(tage_je) or [])
        for i, g in enumerate(grp):
            if _ganz(tag, i, -1) < j0 or g not in stelle:
                continue
            kandidaten.setdefault(stelle[g], []).append(
                (_ganz(ms, i), _ganz(rang, i), pi[i] if isinstance(pi, list) and i < len(pi) else None))
    mgrp, mms, mrank, mpi = [], [], [], []
    for g in range(len(ncar)):
        for ms, rang, pi in sorted(kandidaten.get(g, []), key=lambda e: e[0])[:je_gruppe]:
            mgrp.append(g)
            mms.append(ms)
            mrank.append(rang)
            mpi.append(pi)
    neu.update({"gcar": ncar, "gsig": nsig, "gcount": ncount,
                "lgrp": mgrp, "lms": mms, "lrank": mrank})
    if any(p is not None for p in mpi):
        neu["lpi"] = mpi
    else:
        neu.pop("lpi", None)
    return neu


def zuschneiden(d: dict, seit: str | None) -> dict:
    """Der ganze Datensatz ab dem Stichtag: Boards und die Liste der Scans (meta.scans).

    Ein neues dict; Autos, Strecken, Klassen und Kategorien bleiben dieselben Listen --
    nur Boards ohne Zeile ab dem Stichtag fehlen. None heisst: alles, unveraendert.
    """
    if seit is None:
        return d
    neu = dict(d)
    meta = d.get("meta")
    je_gruppe = meta.get("laps_per_group") if isinstance(meta, dict) else None
    if not isinstance(je_gruppe, int) or isinstance(je_gruppe, bool) or je_gruppe < 1:
        je_gruppe = JE_GRUPPE
    neu["boards"] = [s for s in (board_ab(b, seit, je_gruppe) for b in d.get("boards") or []
                                 if isinstance(b, dict)) if s is not None]
    if isinstance(meta, dict):
        meta = dict(meta)
        meta["scans"] = [s for s in meta.get("scans") or []
                         if isinstance(s, dict) and gilt(s.get("when"), seit)]
        meta["since"] = seit
        neu["meta"] = meta
    return neu
