# -*- coding: utf-8 -*-
"""Die Rivalen-Karten fuer die App aufbereiten: ein Bildausschnitt und ein
geordneter, glatter Linienzug je Strecke.

    python scripts/route_maps.py              alle benannten Strecken
    python scripts/route_maps.py --blatt      dazu ein Kontrollblatt (PNG)

## Warum ueberhaupt

Die Ernte (`route_shapes.py --ernten`) legt die Linie als BILDPUNKTE ab -- jeden
dritten Punkt des farbigen Bandes, in Bildzeilen-Reihenfolge. Das ist eine Menge,
kein Weg. Die App konnte sie nur als Punktwolke zeichnen, und so sah es auch aus:
"very pixely and low quality" (Nutzer, 2026-09-25).

Hier entstehen zwei Dinge, die die App direkt zeichnen kann:

1. **Der Ausschnitt**, wie ihn das Spiel zeigt: die Karte um die Strecke, als JPEG.
2. **Der Linienzug**: die Mittellinie des Bandes, in Fahrtrichtung geordnet, mit
   dem Start dort, wo das Spiel seinen weissen Pfeil hinsetzt.

## Nur Aufnahmen aus den Kartenlaeufen

Die Namen der Ernte stammen aus zwei Quellen: gelesen oder nach Karussellposition.
Nur die Kartenlaeufe vom 2026-09-24 (Bildnamen `map-N`) sind ueber die Position
geprueft, 86 von 86. Aeltere Aufnahmen tragen gelesene Namen, und die irren: 13
Aufnahmen heissen "Izu Cross-Country", mindestens eine zeigt im Titel "Shinjuku
Gyoen Cross-Country". Die bisherige Wahl "die meisten Bildpunkte" haette fuer Izu
ausgerechnet eine davon genommen. Im Bildmodus steht der Titel mit im Ausschnitt --
der Fehler waere sofort zu sehen gewesen.

## Wie aus dem Band ein Weg wird

Verduennen (Zhang-Suen) auf einen Bildpunkt Breite, dann als Graph lesen: Enden,
Kreuzungen, Stuecke dazwischen. Kurze Stummel (Ecken des Bandes, der Kreis am Ziel
der Drag-Strecken) fallen weg. Dann wird der Graph abgelaufen, an jeder Kreuzung
GERADEAUS -- eine Strecke, die sich selbst kreuzt, biegt dort nicht ab. Was der
Titel oder der Pfeil verdeckt, sind Luecken; die Stuecke werden ueber die naechsten
Enden verbunden.

## Der Pfeil

Weiss mit schwarzem Rand, auf der Linie, zeigt in Fahrtrichtung. Gefunden ueber
genau das: helle Flaeche, dunkler Saum, nahe am Band, nicht groesser als ein Pfeil
(die Buchstaben des Titels sind auch weiss mit dunklem Umfeld, aber viel groesser).
Ohne Pfeil bleibt der Start unbekannt, und die App zeichnet keinen Startpunkt --
ein geratener waere schlimmer als keiner.
"""
from __future__ import annotations

import argparse
import json
import math
import re
import sys
from pathlib import Path

import numpy as np
from PIL import Image
from scipy import ndimage

sys.path.insert(0, str(Path(__file__).resolve().parent))
import route_shapes as rs  # noqa: E402

WORKSPACE = rs.WORKSPACE
ERNTE = rs.ERNTE
ZIEL = WORKSPACE / "data" / "route_maps"

# Rand um die Strecke im Ausschnitt, in 1080p-Bildpunkten. Genug, dass die Linie
# nicht an der Kante klebt und man die Umgebung erkennt.
RAND = 26
# Laengste Seite des gespeicherten Bildes. Eine Kachel ist 150 Punkte bei 1080p,
# auf 4K und bei 200 % also bis 600; mehr als die Aufnahme hergibt waere nur
# groesser, nicht schaerfer.
BILD_MAX = 560
# Die grosse Titelzeile beginnt bei rund y 560 (1080p). Der Ausschnitt waechst nur
# bis dorthin nach unten, wenn die Strecke selbst nicht tiefer reicht.
TITEL_OBEN = 556


# ----------------------------------------------------------------- Auswahl

def beste_aufnahmen() -> dict[str, dict]:
    """Je Streckenname die Aufnahme mit den meisten Bildpunkten -- nur Kartenlaeufe."""
    beste: dict[str, dict] = {}
    for datei in sorted(ERNTE.glob("*.json")):
        try:
            d = json.loads(datei.read_text(encoding="utf-8"))
        except Exception:
            continue
        name = d.get("name")
        frame = d.get("frame") or ""
        if not name or not re.search(r"map-\d+\.png$", frame.replace("\\", "/")):
            continue
        if not (WORKSPACE / frame).exists():
            continue
        d["_datei"] = datei.name
        if name not in beste or d.get("pixels", 0) > beste[name].get("pixels", 0):
            beste[name] = d
    return beste


# ----------------------------------------------------------------- Maske

def band_maske(rgb: np.ndarray) -> tuple[np.ndarray, tuple[int, int], str]:
    """Das farbige Band im Kartenfeld, als Maske; dazu der Versatz des Feldes."""
    hoch, breit = rgb.shape[:2]
    x0, y0, x1, y1 = rs.KARTENFELD
    ox, oy = int(x0 * breit), int(y0 * hoch)
    feld = rgb[oy:int(y1 * hoch) + 1, ox:int(x1 * breit) + 1]
    beste = None
    for name, maske_von in rs.LINIENFARBEN.items():
        m = maske_von(feld)
        n = int(m.sum())
        if beste is None or n > beste[1]:
            beste = (name, n, m)
    name, _, m = beste
    return saeubern(m), (ox, oy), name


def saeubern(m: np.ndarray) -> np.ndarray:
    """Die Farbmaske eines Bandes fuer das Verduennen vorbereiten."""
    # Kantenglaettung und JPEG-artige Kruemel: einmal schliessen, Kleinkram weg.
    m = ndimage.binary_closing(m, structure=np.ones((3, 3), bool), iterations=1)
    # KLEINE LOECHER FUELLEN, grosse nicht. Innerhalb des Bandes liegen einzelne
    # dunklere Bildpunkte (Strassen darunter scheinen durch); die wuerden beim
    # Verduennen zu Schleifen. Der Raum zwischen zwei parallelen Abschnitten ist
    # dagegen gross -- und muss offen bleiben.
    loch, n = ndimage.label(~m)
    if n:
        groesse = ndimage.sum(np.ones_like(loch), loch, index=np.arange(1, n + 1))
        klein = np.zeros(n + 1, bool)
        klein[1:] = groesse < 40
        m = m | klein[loch]
    teile, n = ndimage.label(m, structure=np.ones((3, 3), bool))
    if n:
        groesse = ndimage.sum(m, teile, index=np.arange(1, n + 1))
        gross = np.zeros(n + 1, bool)
        gross[1:] = groesse >= 60
        m = gross[teile]
    return m


def verduennen(m: np.ndarray) -> np.ndarray:
    """Zhang-Suen: das Band auf einen Bildpunkt Breite, ohne es zu zerreissen."""
    b = np.pad(m.astype(np.uint8), 1)
    while True:
        geaendert = False
        for schritt in (0, 1):
            p2 = b[:-2, 1:-1]; p3 = b[:-2, 2:]; p4 = b[1:-1, 2:]; p5 = b[2:, 2:]
            p6 = b[2:, 1:-1]; p7 = b[2:, :-2]; p8 = b[1:-1, :-2]; p9 = b[:-2, :-2]
            summe = p2 + p3 + p4 + p5 + p6 + p7 + p8 + p9
            folge = [p2, p3, p4, p5, p6, p7, p8, p9, p2]
            wechsel = sum(((folge[i] == 0) & (folge[i + 1] == 1)).astype(np.uint8)
                          for i in range(8))
            if schritt == 0:
                c1 = p2 * p4 * p6
                c2 = p4 * p6 * p8
            else:
                c1 = p2 * p4 * p8
                c2 = p2 * p6 * p8
            innen = b[1:-1, 1:-1]
            weg = ((innen == 1) & (summe >= 2) & (summe <= 6) & (wechsel == 1)
                   & (c1 == 0) & (c2 == 0))
            if weg.any():
                innen[weg] = 0
                geaendert = True
        if not geaendert:
            break
    return b[1:-1, 1:-1].astype(bool)


# ----------------------------------------------------------------- Graph

NACHBARN = [(-1, -1), (-1, 0), (-1, 1), (0, -1), (0, 1), (1, -1), (1, 0), (1, 1)]


def nachbarn_von(skel: set, p: tuple[int, int]) -> list[tuple[int, int]]:
    """8er-Nachbarn -- aber eine Schraege nur, wenn kein gerader Weg sie ersetzt.

    Sonst bildet jede Treppenstufe des verduennten Bandes ein Dreieck, und jedes
    Dreieck zaehlt als Kreuzung.
    """
    y, x = p
    raus = []
    for dy, dx in NACHBARN:
        q = (y + dy, x + dx)
        if q not in skel:
            continue
        if dy != 0 and dx != 0 and ((y + dy, x) in skel or (y, x + dx) in skel):
            continue
        raus.append(q)
    return raus


def stuecke(skel: set) -> tuple[list[list], dict]:
    """Den verduennten Strich in Stuecke zwischen Knoten zerlegen.

    Knoten sind Bildpunkte mit einem oder mit drei und mehr Nachbarn. Benachbarte
    Kreuzungspunkte bilden EINEN Knoten.
    """
    nb = {p: nachbarn_von(skel, p) for p in skel}
    knotenpunkte = {p for p, n in nb.items() if len(n) != 2}
    # Kreuzungspunkte zusammenfassen
    knoten_von: dict = {}
    kid = 0
    for p in knotenpunkte:
        if p in knoten_von:
            continue
        stapel = [p]
        knoten_von[p] = kid
        while stapel:
            q = stapel.pop()
            for r in nb[q]:
                if r in knotenpunkte and r not in knoten_von:
                    knoten_von[r] = kid
                    stapel.append(r)
        kid += 1
    besucht_kante = set()
    teile: list[list] = []
    for p in knotenpunkte:
        for q in nb[p]:
            if q in knotenpunkte:
                continue
            if (p, q) in besucht_kante:
                continue
            zug = [p, q]
            vorher, jetzt = p, q
            while jetzt not in knotenpunkte:
                weiter = [r for r in nb[jetzt] if r != vorher]
                if not weiter:
                    break
                vorher, jetzt = jetzt, weiter[0]
                zug.append(jetzt)
            besucht_kante.add((zug[0], zug[1]))
            besucht_kante.add((zug[-1], zug[-2]))
            teile.append(zug)
    # Reine Ringe ohne jeden Knoten (ein sauberer Rundkurs ohne Luecke).
    in_teilen = {q for t in teile for q in t}
    rest = [p for p in skel if p not in in_teilen and p not in knotenpunkte]
    rest_set = set(rest)
    while rest_set:
        start = next(iter(rest_set))
        zug = [start]
        rest_set.discard(start)
        vorher, jetzt = None, start
        while True:
            weiter = [r for r in nb[jetzt] if r != vorher and r in rest_set]
            if not weiter:
                break
            vorher, jetzt = jetzt, weiter[0]
            zug.append(jetzt)
            rest_set.discard(jetzt)
        if len(zug) > 8:
            zug.append(zug[0])
            teile.append(zug)
    return teile, knoten_von


def laenge(zug) -> float:
    a = np.asarray(zug, float)
    if len(a) < 2:
        return 0.0
    return float(np.hypot(*np.diff(a, axis=0).T).sum())


def richtung(zug, von_anfang: bool, weit: int = 10) -> np.ndarray:
    """Richtung am Anfang (hinaus) oder am Ende (hinein) eines Stuecks."""
    a = np.asarray(zug, float)
    k = min(weit, len(a) - 1)
    v = (a[k] - a[0]) if von_anfang else (a[-1] - a[-1 - k])
    n = np.hypot(*v)
    return v / n if n > 0 else v


class Graph:
    """Stuecke zwischen Knoten. Ein Knoten ist eine Kreuzung, ein Ende -- oder,
    bei einem Ring ohne jede Kreuzung, dessen erster Bildpunkt."""

    def __init__(self, teile: list[list], knoten_von: dict):
        self.kanten: list[dict] = []
        for t in teile:
            a = knoten_von.get(t[0], ("frei", t[0]))
            b = knoten_von.get(t[-1], ("frei", t[-1]))
            self.kanten.append({"a": a, "b": b, "pts": t, "len": laenge(t), "luecke": False})

    def grad(self) -> dict:
        g: dict = {}
        for k in self.kanten:
            g[k["a"]] = g.get(k["a"], 0) + 1
            g[k["b"]] = g.get(k["b"], 0) + 1
        return g

    def durchgaenge_zusammenlegen(self) -> None:
        """Ein Knoten mit genau zwei Stueck-Enden ist keiner: die beiden Stuecke
        werden eins. Sonst misst die Stummelregel ein halbes Stueck."""
        while True:
            g = self.grad()
            fertig = True
            for knoten, n in g.items():
                if n != 2:
                    continue
                an = [i for i, k in enumerate(self.kanten) if k["a"] == knoten or k["b"] == knoten]
                if len(an) != 2:
                    continue   # ein Ring an sich selbst
                i, j = an
                ki, kj = self.kanten[i], self.kanten[j]
                pi = ki["pts"] if ki["b"] == knoten else ki["pts"][::-1]
                pj = kj["pts"] if kj["a"] == knoten else kj["pts"][::-1]
                anfang = ki["a"] if ki["b"] == knoten else ki["b"]
                ende = kj["b"] if kj["a"] == knoten else kj["a"]
                neu = {"a": anfang, "b": ende, "pts": pi + pj[1:], "luecke": False}
                neu["len"] = laenge(neu["pts"])
                for x in sorted((i, j), reverse=True):
                    del self.kanten[x]
                self.kanten.append(neu)
                fertig = False
                break
            if fertig:
                return

    def stummel_weg(self, grenze: float) -> None:
        """Kurze Stuecke von einem Ende zu einer Kreuzung entfernen, eins nach dem
        anderen -- zwei Stummel an derselben Kreuzung sind oft ein Stummel und das
        echte Ende der Strecke."""
        while True:
            self.durchgaenge_zusammenlegen()
            g = self.grad()
            kandidaten = [i for i, k in enumerate(self.kanten)
                          if k["a"] != k["b"] and k["len"] < grenze
                          and ((g[k["a"]] == 1 and g[k["b"]] >= 3)
                               or (g[k["b"]] == 1 and g[k["a"]] >= 3))]
            # KLEINE RINGE UND BLASEN. Ein Loch im Band, das das Fuellen uebersehen
            # hat, wird beim Verduennen zu einem Ring an einem Knoten (Stueck von a
            # nach a) oder zu zwei Stuecken zwischen denselben zwei Knoten. Beides ist
            # kein Weg, den eine Strecke nimmt: der Ring faellt weg, von der Blase
            # bleibt das kuerzere Stueck.
            for i, k in enumerate(self.kanten):
                if k["a"] == k["b"] and k["len"] < 2 * grenze and g[k["a"]] > 2:
                    kandidaten.append(i)
            paare: dict = {}
            for i, k in enumerate(self.kanten):
                if k["a"] != k["b"]:
                    paare.setdefault(frozenset((k["a"], k["b"])), []).append(i)
            for liste in paare.values():
                if len(liste) > 1:
                    liste.sort(key=lambda i: self.kanten[i]["len"])
                    for i in liste[1:]:
                        if self.kanten[i]["len"] < 2 * grenze:
                            kandidaten.append(i)
            if not kandidaten:
                return
            del self.kanten[min(kandidaten, key=lambda i: self.kanten[i]["len"])]

    def luecken_ueberbruecken(self, weit: float) -> None:
        """Zwei Enden, die dicht beieinander liegen, sind eine verdeckte Stelle --
        der Pfeil, ein Buchstabe des Titels. Eine gerade Bruecke verbindet sie;
        die Suche nimmt sie nur, wenn dahinter mehr Strecke liegt, als sie kostet."""
        g = self.grad()
        enden = {}
        for k in self.kanten:
            # Mit der Richtung, in die das Ende HINAUS zeigt.
            for knoten, p, raus in ((k["a"], k["pts"][0], -richtung(k["pts"], True)),
                                    (k["b"], k["pts"][-1], richtung(k["pts"], False))):
                if g[knoten] == 1:
                    enden[knoten] = (p, raus)
        liste = list(enden.items())
        for x in range(len(liste)):
            for y in range(x + 1, len(liste)):
                (ka, (pa, ra)), (kb, (pb, rb)) = liste[x], liste[y]
                d = math.hypot(pa[0] - pb[0], pa[1] - pb[1])
                if d > weit:
                    continue
                # NUR ENDEN, DIE EINANDER ANSEHEN. Unter dem Pfeil laeuft die Linie
                # weiter: das eine Ende zeigt auf das andere. Am 2026-09-25 verband
                # eine Bruecke ohne diese Bedingung in "Shimanoyama Circuit" zwei
                # Schleifen quer ueber die Karte. Ganz kurze Luecken sind davon
                # ausgenommen -- dort ist die Richtung des Endes selbst Rauschen.
                if d > 2.5 * weit / 6.0:
                    v = np.array([pb[0] - pa[0], pb[1] - pa[1]], float) / max(d, 1e-6)
                    if float(np.dot(ra, v)) < 0.35 or float(np.dot(rb, -v)) < 0.35:
                        continue
                self.kanten.append({"a": ka, "b": kb, "pts": [pa, pb], "len": d,
                                    "luecke": True})

    def laengster_zug(self, budget: int = 400_000) -> list:
        """Der laengste Zug durch den Graphen: jedes Stueck hoechstens einmal, an
        Kreuzungen lieber geradeaus. Die Graphen sind klein (unter dreissig Stuecke),
        und die Schranke "was noch ungenutzt ist, kann hoechstens noch dazukommen"
        schneidet fast alles ab."""
        if not self.kanten:
            return []
        an: dict = {}
        for i, k in enumerate(self.kanten):
            an.setdefault(k["a"], []).append((i, 0))
            an.setdefault(k["b"], []).append((i, 1))
        echt_gesamt = sum(k["len"] for k in self.kanten if not k["luecke"])
        g = {}
        for k in self.kanten:
            if k["luecke"]:
                continue
            g[k["a"]] = g.get(k["a"], 0) + 1
            g[k["b"]] = g.get(k["b"], 0) + 1
        starts = [n for n, d in g.items() if d == 1] or list(g)
        best = [-1e18, []]
        rest = [budget]
        benutzt = [False] * len(self.kanten)
        zug: list = []

        def tiefer(knoten, rein, punkte, uebrig):
            rest[0] -= 1
            if punkte > best[0]:
                best[0] = punkte
                best[1] = list(zug)
            if rest[0] <= 0 or punkte + uebrig <= best[0]:
                return
            for i, seite in an.get(knoten, []):
                if benutzt[i]:
                    continue
                k = self.kanten[i]
                pts = k["pts"] if seite == 0 else k["pts"][::-1]
                ziel = k["b"] if seite == 0 else k["a"]
                gewinn = -0.6 * k["len"] if k["luecke"] else k["len"]
                if rein is not None:
                    raus = richtung(pts, True)
                    gewinn -= 24.0 * (1.0 - float(np.dot(rein, raus)))
                benutzt[i] = True
                zug.append((i, seite))
                tiefer(ziel, richtung(pts, False), punkte + gewinn,
                       uebrig - (0.0 if k["luecke"] else k["len"]))
                zug.pop()
                benutzt[i] = False

        for s in starts:
            tiefer(s, None, 0.0, echt_gesamt)
        weg: list = []
        for i, seite in best[1]:
            k = self.kanten[i]
            pts = k["pts"] if seite == 0 else k["pts"][::-1]
            weg.extend(pts if not weg else pts[1:])
        # Eine Bruecke am Ende ist nie Strecke.
        return weg


def weg_aus_maske(m: np.ndarray) -> tuple[list, dict]:
    """Die geordnete Mittellinie des Bandes, als (y, x) im Feld."""
    skel = verduennen(m)
    # Breite des Bandes -- fuer die Stummelgrenze und die Bruecken.
    abstand = ndimage.distance_transform_edt(m)
    breite = 2.0 * float(np.median(abstand[skel])) if skel.any() else 6.0
    grenze = max(14.0, 2.5 * breite)

    lab, n = ndimage.label(skel, structure=np.ones((3, 3), bool))
    punkte: set = set()
    for k in range(1, n + 1):
        ys, xs = np.nonzero(lab == k)
        if len(ys) >= 12:
            punkte |= set(zip(ys.tolist(), xs.tolist()))
    if not punkte:
        return [], {"breite": breite}
    teile, knoten_von = stuecke(punkte)
    graph = Graph(teile, knoten_von)
    graph.stummel_weg(grenze)
    graph.luecken_ueberbruecken(max(48.0, 6.0 * breite))
    weg = graph.laengster_zug()
    gesamt = sum(k["len"] for k in graph.kanten if not k["luecke"])
    bruecken = [round(k["len"], 1) for k in graph.kanten if k["luecke"]]
    return weg, {"breite": round(breite, 1), "abdeckung": round(laenge(weg) / max(1.0, gesamt), 3),
                 "bruecken": bruecken}


# ----------------------------------------------------------------- Pfeil

def pfeil_finden(rgb: np.ndarray, band: np.ndarray, off: tuple[int, int]):
    """Der weisse Startpfeil: (x, y) im Feld und seine Spitzenrichtung, oder None."""
    hoch, breit = band.shape
    ox, oy = off
    feld = rgb[oy:oy + hoch, ox:ox + breit].astype(np.int16)
    mx = feld.max(axis=2)
    mn = feld.min(axis=2)
    hell = (mn > 185) & ((mx - mn) < 30)
    dunkel = mx < 55
    lab, n = ndimage.label(hell)
    if not n:
        return None
    naehe_band = ndimage.distance_transform_edt(~band)
    kandidaten = []
    for k, sl in enumerate(ndimage.find_objects(lab), start=1):
        if sl is None:
            continue
        h = sl[0].stop - sl[0].start
        w = sl[1].stop - sl[1].start
        if max(h, w) > 42 or max(h, w) < 6:
            continue
        teil = lab[sl] == k
        flaeche = int(teil.sum())
        if flaeche < 20 or flaeche > 900:
            continue
        # Der dunkle Saum: ein Ring um die Flaeche, der zum groessten Teil schwarz ist.
        y0 = max(0, sl[0].start - 4); y1 = min(hoch, sl[0].stop + 4)
        x0 = max(0, sl[1].start - 4); x1 = min(breit, sl[1].stop + 4)
        stueck = lab[y0:y1, x0:x1] == k
        ring = ndimage.binary_dilation(stueck, iterations=3) & ~stueck
        ring &= ~hell[y0:y1, x0:x1]
        if ring.sum() == 0:
            continue
        saum = float((dunkel[y0:y1, x0:x1] & ring).sum()) / float(ring.sum())
        ys, xs = np.nonzero(teil)
        cy = ys.mean() + sl[0].start
        cx = xs.mean() + sl[1].start
        nah = float(naehe_band[int(round(cy)), int(round(cx))])
        if saum < 0.35 or nah > 30:
            continue
        kandidaten.append((saum, flaeche, cx, cy, ys + sl[0].start, xs + sl[1].start))
    if not kandidaten:
        return None
    # HALBE PFEILE ZUSAMMENLEGEN. Die Flaeche ist zweifarbig, weiss und hellgrau,
    # getrennt von einer Schattierungskante; mit einer tieferen Hellschwelle oder
    # einem Schliessen verschmoelze sie dagegen mit dem Schnee der Karte (am
    # 2026-09-25 probiert: 63 von 86 Starts verloren). Also Teile, deren Mitten
    # dicht beieinander liegen, als einen Pfeil nehmen.
    gruppen: list[list] = []
    for kd in kandidaten:
        for gr in gruppen:
            if any(math.hypot(kd[2] - o[2], kd[3] - o[3]) <= 20 for o in gr):
                gr.append(kd)
                break
        else:
            gruppen.append([kd])
    beste = None
    for gr in gruppen:
        ys = np.concatenate([g[4] for g in gr])
        xs = np.concatenate([g[5] for g in gr])
        flaeche = len(ys)
        if flaeche < 40 or max(ys.max() - ys.min(), xs.max() - xs.min()) > 44:
            continue
        saum = sum(g[0] * g[1] for g in gr) / sum(g[1] for g in gr)
        if beste is None or saum > beste[0]:
            beste = (saum, ys, xs)
    if beste is None:
        return None
    saum, ys, xs = beste
    return {"x": float(xs.mean()), "y": float(ys.mean()), "saum": round(saum, 2),
            "ys": ys, "xs": xs}


def spitze_zeigt(pfeil: dict, v: np.ndarray) -> float:
    """Zeigt die Spitze in Richtung v (+) oder dagegen (-)?

    Der Pfeil ist vorne schmal und hinten breit: projiziert auf die Fahrtrichtung
    hat seine Flaeche einen langen duennen Schwanz zur Spitze hin. Die Schiefe
    dieser Verteilung nennt die Seite.
    """
    t = (pfeil["xs"] - pfeil["x"]) * v[0] + (pfeil["ys"] - pfeil["y"]) * v[1]
    s = t.std()
    if s <= 0:
        return 0.0
    return float(((t / s) ** 3).mean())


# ----------------------------------------------------------------- Glaetten

def glaetten(pfad: np.ndarray, geschlossen: bool, schritt: float = 2.0,
             sigma: float = 2.0) -> np.ndarray:
    """Gleichmaessig abtasten und die Treppenstufen des Rasters herausnehmen."""
    if len(pfad) < 3:
        return pfad
    a = pfad
    if geschlossen:
        a = np.vstack([a, a[:1]])
    seg = np.hypot(*np.diff(a, axis=0).T)
    s = np.concatenate([[0], np.cumsum(seg)])
    if s[-1] <= 0:
        return pfad
    n = max(4, int(s[-1] / schritt))
    t = np.linspace(0, s[-1], n, endpoint=not geschlossen)
    x = np.interp(t, s, a[:, 0])
    y = np.interp(t, s, a[:, 1])
    modus = "wrap" if geschlossen else "nearest"
    x = ndimage.gaussian_filter1d(x, sigma, mode=modus)
    y = ndimage.gaussian_filter1d(y, sigma, mode=modus)
    return np.stack([x, y], axis=1)


def vereinfachen(p: np.ndarray, eps: float) -> np.ndarray:
    """Douglas-Peucker: Punkte weg, die weniger als eps von der Linie abweichen."""
    if len(p) < 3:
        return p
    behalten = np.zeros(len(p), bool)
    behalten[0] = behalten[-1] = True
    stapel = [(0, len(p) - 1)]
    while stapel:
        i, j = stapel.pop()
        if j <= i + 1:
            continue
        a, b = p[i], p[j]
        d = b - a
        n = math.hypot(*d)
        mitte = p[i + 1:j]
        if n == 0:
            abst = np.hypot(*(mitte - a).T)
        else:
            abst = np.abs(d[0] * (mitte[:, 1] - a[1]) - d[1] * (mitte[:, 0] - a[0])) / n
        k = int(np.argmax(abst))
        if abst[k] > eps:
            m = i + 1 + k
            behalten[m] = True
            stapel.append((i, m))
            stapel.append((m, j))
    return p[behalten]


# ----------------------------------------------------------------- eine Strecke

def aufbereiten(d: dict) -> dict | None:
    frame = WORKSPACE / d["frame"]
    rgb = rs.frame_lesen(frame)
    hoch, breit = rgb.shape[:2]
    massstab = hoch / 1080.0
    band, (ox, oy), farbe = band_maske(rgb)
    if band.sum() < rs.MINDESTPUNKTE:
        return None
    pfeil = pfeil_finden(rgb, band, (ox, oy))
    weg, info = weg_aus_maske(band)
    if len(weg) < 8:
        return None
    pfad = np.asarray([(x, y) for y, x in weg], float)   # (x, y) im Feld

    # GESCHLOSSEN, wenn Anfang und Ende dicht beieinander liegen -- ein Rundkurs,
    # den der Pfeil an einer Stelle unterbricht, oder einer, dessen Zug an der
    # Kreuzung endet, an der er begann.
    luecke = math.hypot(*(pfad[0] - pfad[-1]))
    geschlossen = (luecke <= max(46.0 * massstab, 4 * info["breite"])
                   and laenge(weg) > 200 * massstab)

    def naechster(p: np.ndarray) -> int:
        return int(np.argmin(np.hypot(p[:, 0] - pfeil["x"], p[:, 1] - pfeil["y"])))

    def fahrtrichtung(p: np.ndarray, i: int) -> float:
        """Die Schiefe des Pfeils gegen die Richtung des Weges bei i."""
        a = p[max(0, i - 6)]
        b = p[min(len(p) - 1, i + 6)]
        v = b - a
        n = math.hypot(*v)
        return spitze_zeigt(pfeil, v / n) if n > 0 else 0.0

    start = "unknown"
    # Der Pfeil sitzt oft knapp hinter dem Ende des Bandes; die Stummelregel kuerzt
    # das Ende ausserdem um ein paar Punkte. Darum 50 und nicht 30.
    if pfeil is not None and math.hypot(*(pfad[naechster(pfad)] - (pfeil["x"], pfeil["y"]))) <= 50 * massstab:
        start = "arrow"
        if geschlossen:
            # Auf dem Ring beginnt der Weg am Pfeil ...
            i = naechster(pfad)
            pfad = np.vstack([pfad[i:], pfad[:i]])
            # ... und laeuft in die Richtung, in die seine Spitze zeigt.
            if fahrtrichtung(pfad, 0) < -0.15:
                pfad = np.vstack([pfad[:1], pfad[1:][::-1]])
        else:
            # Offen: der Pfeil steht am Anfang, der Weg soll dort beginnen. Steht
            # er mittendrin (eine Schleife mit Anlauf), entscheidet die Spitze.
            i = naechster(pfad)
            if i > len(pfad) - 1 - i:
                pfad = pfad[::-1]
            i = naechster(pfad)
            if i > 12 and fahrtrichtung(pfad, i) < -0.15:
                pfad = pfad[::-1]

    glatt = glaetten(pfad, geschlossen, schritt=2.0 * massstab, sigma=2.0)
    glatt = vereinfachen(glatt, 0.35 * massstab)
    start_bei = -1
    if start == "arrow":
        start_bei = 0 if geschlossen else naechster(glatt)
        # Ein Pfeil in den ersten paar Punkten IST der Anfang.
        if start_bei <= 2:
            start_bei = 0

    # Zurueck in Bildkoordinaten, auf 1080p bezogen.
    bild = (glatt + np.array([ox, oy])) / massstab

    # ---- der Ausschnitt: um das ganze BAND, nicht nur um den gefundenen Weg.
    # Laesst die Suche ein Stueck aus, soll das Bild es trotzdem zeigen.
    teile, _ = ndimage.label(band, structure=np.ones((3, 3), bool))
    getroffen = {int(teile[y, x]) for y, x in weg} - {0}
    ys, xs = np.nonzero(np.isin(teile, list(getroffen)))
    bx0, by0 = (xs.min() + ox) / massstab, (ys.min() + oy) / massstab
    bx1, by1 = (xs.max() + ox) / massstab, (ys.max() + oy) / massstab
    fx0, fy0, fx1, fy1 = (rs.KARTENFELD[0] * 1920, rs.KARTENFELD[1] * 1080,
                          rs.KARTENFELD[2] * 1920, rs.KARTENFELD[3] * 1080)
    x0, y0 = bx0 - RAND, by0 - RAND
    x1, y1 = bx1 + RAND, by1 + RAND
    # Die kurze Seite etwas strecken, damit das Bild die Kachel besser fuellt --
    # nach unten aber nicht in die Titelzeile, solange die Strecke nicht dort liegt.
    w, h = x1 - x0, y1 - y0
    if w > h:
        extra = min(w, h * 1.6) - h
        y0 -= extra / 2
        y1 += extra / 2
    else:
        extra = min(h, w * 1.6) - w
        x0 -= extra / 2
        x1 += extra / 2
    unten = max(by1 + RAND, TITEL_OBEN)
    if y1 > unten:
        y0 -= (y1 - unten)
        y1 = unten
    x0, y0 = max(fx0, x0), max(fy0, y0)
    x1, y1 = min(fx1, x1), min(fy1, y1)
    kasten = [round(x0, 1), round(y0, 1), round(x1, 1), round(y1, 1)]

    band_1080 = info["breite"] / massstab
    return {
        "name": d["name"],
        "routeLengthKm": d.get("routeLengthKm"),
        "reliable": verlaesslich(bild, band_1080, info.get("abdeckung")),
        "frame": d["frame"],
        "colour": farbe,
        "closed": bool(geschlossen),
        "start": start,
        "startIndex": start_bei,
        "crop": kasten,
        "bandWidth": round(info["breite"] / massstab, 1),
        "coverage": info.get("abdeckung"),
        "bridges": info.get("bruecken", []),
        "path": [[round(float(x), 1), round(float(y), 1)] for x, y in bild],
        "_rgb": rgb,
        "_massstab": massstab,
    }


def verlaesslich(pfad: np.ndarray, band: float, abdeckung: float | None) -> bool:
    """Ist die nachgezeichnete Linie verlaesslich -- oder zeigt die App besser das Bild?

    Zwei Faelle, beide am 2026-09-26 gemessen:

    - Die Strecke ist im Kartenbild KLEIN und dabei verschlungen. "Shimanoyama Circuit"
      misst 6,6 Bandbreiten bei 21 Bandbreiten Weg: die innere Haarnadel liegt am
      Aussenbogen an, beides wird ein Band, und die Linie macht daraus eine Kreuzung.
      Grenze: unter 14 Bandbreiten Ausdehnung und mehr als doppelt so viel Weg wie
      Ausdehnung. Eine gerade Drag-Strecke ist klein, aber nicht verschlungen.
    - Die Suche hat weniger als 85 % des Bandes erfasst.
    """
    if len(pfad) < 2 or band <= 0:
        return False
    ausdehnung = float(max(np.ptp(pfad[:, 0]), np.ptp(pfad[:, 1])))
    weg = float(np.hypot(*np.diff(pfad, axis=0).T).sum())
    if ausdehnung / band < 14 and weg > 2 * ausdehnung:
        return False
    if abdeckung is not None and abdeckung < 0.85:
        return False
    return True


def slug(name: str) -> str:
    s = re.sub(r"[^A-Za-z0-9]+", "-", name).strip("-").lower()
    return s or "route"


def speichern(e: dict) -> None:
    rgb = e.pop("_rgb")
    m = e.pop("_massstab")
    x0, y0, x1, y1 = e["crop"]
    bild = Image.fromarray(rgb).crop((int(round(x0 * m)), int(round(y0 * m)),
                                      int(round(x1 * m)), int(round(y1 * m))))
    bild.thumbnail((BILD_MAX, BILD_MAX), Image.LANCZOS)
    name = slug(e["name"])
    bild.save(ZIEL / (name + ".jpg"), "JPEG", quality=86, optimize=True)
    e["image"] = name + ".jpg"
    e["imageSize"] = [bild.width, bild.height]
    (ZIEL / (name + ".json")).write_text(json.dumps(e, ensure_ascii=False), encoding="utf-8")


def kontrollblatt(eintraege: list[dict], ziel: Path) -> None:
    """Jede Strecke: Ausschnitt mit dem Linienzug darueber, Start gelb."""
    from PIL import ImageDraw
    k = 300
    spalten = 6
    reihen = (len(eintraege) + spalten - 1) // spalten
    blatt = Image.new("RGB", (spalten * k, reihen * (k + 18)), (16, 18, 22))
    zeichner = ImageDraw.Draw(blatt)
    for i, e in enumerate(eintraege):
        bild = Image.open(ZIEL / e["image"]).convert("RGB")
        x0, y0, x1, y1 = e["crop"]
        f = min(k / bild.width, k / bild.height)
        bild = bild.resize((max(1, int(bild.width * f)), max(1, int(bild.height * f))))
        bx = (i % spalten) * k
        by = (i // spalten) * (k + 18) + 18
        blatt.paste(bild, (bx, by))
        sx = bild.width / (x1 - x0)
        sy = bild.height / (y1 - y0)
        pts = [(bx + (x - x0) * sx, by + (y - y0) * sy) for x, y in e["path"]]
        if e["closed"]:
            pts.append(pts[0])
        zeichner.line(pts, fill=(255, 255, 255), width=2)
        if e["start"] == "arrow":
            sx0, sy0 = pts[e["startIndex"]]
            zeichner.ellipse((sx0 - 5, sy0 - 5, sx0 + 5, sy0 + 5), fill=(255, 210, 0))
            j = e["startIndex"]
            if len(pts) > j + 6:
                zeichner.line([pts[j], pts[j + 6]], fill=(255, 0, 0), width=3)
        text = f"{e['name'][:28]} {'O' if e['closed'] else '-'} {e['start'][0]}{e['startIndex']} c{e['coverage']}"
        zeichner.text((bx + 3, by - 16), text, fill=(255, 255, 0))
    blatt.save(ziel)


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--blatt", type=Path, nargs="?", const=ZIEL / "_kontrolle.png",
                    help="Kontrollblatt schreiben (Standard: data/route_maps/_kontrolle.png)")
    ap.add_argument("--nur", help="nur Strecken, deren Name dies enthaelt")
    a = ap.parse_args(argv)

    beste = beste_aufnahmen()
    if not beste:
        print("keine benannten Kartenaufnahmen -- erst route_shapes.py --ernten")
        return 1
    ZIEL.mkdir(parents=True, exist_ok=True)
    if not a.nur:
        for alt in list(ZIEL.glob("*.json")) + list(ZIEL.glob("*.jpg")):
            alt.unlink()
    fertig = []
    fehl = []
    for name in sorted(beste):
        if a.nur and a.nur.lower() not in name.lower():
            continue
        try:
            e = aufbereiten(beste[name])
        except Exception as ex:  # eine Strecke darf den Lauf nicht beenden
            fehl.append(f"{name}: {ex}")
            continue
        if e is None:
            fehl.append(f"{name}: keine Linie")
            continue
        speichern(e)
        fertig.append(e)
    ohne_start = [e["name"] for e in fertig if e["start"] != "arrow"]
    print(f"{len(fertig)} Strecke(n) aufbereitet, {sum(e['closed'] for e in fertig)} davon Rundkurse; "
          f"{len(ohne_start)} ohne erkannten Start")
    for f in fehl:
        print("  FEHLT", f)
    if ohne_start:
        print("  ohne Start:", ", ".join(ohne_start))
    if a.blatt:
        kontrollblatt(fertig, a.blatt)
        print(a.blatt)
    return 0 if not fehl else 2


if __name__ == "__main__":
    raise SystemExit(main())
