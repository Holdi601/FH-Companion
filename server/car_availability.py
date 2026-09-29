"""Welche Autos es in FH6 gibt und wie man an sie kommt -- fuer "Missing cars" in der App.

    python server/car_availability.py            bauen, nach data/cars/ schreiben
    python server/car_availability.py --check    bauen, nur zusammenfassen

ZWEI QUELLEN, beide oeffentlich und beide immer frisch geholt:

- forza.net/fh6cars: die offizielle Liste. Eine Tabelle, je Auto Marke, Name mit
  Jahr, Typ, PI und Klasse, Land, "Collection" (Autoshow, Wheelspin, Seasonal ...)
  und "Add-Ons" (Car Pass, Welcome Pack ...). Sie waechst mit jeder Series.
- forza.fandom.com: je Auto der Block {{CarStats|fh6 ...}}. Dort steht, was die
  offizielle Liste nur andeutet -- der Preis, in welcher Festival-Playlist-Saison
  das Auto zu gewinnen war, wo der Scheunenfund liegt, welcher Aftermarket-Haendler
  es anbietet, welches Auto es ueber Car Mastery freischaltet.

VERBUNDEN mit config/fh6_car_id_names.json (car_id je Name und Jahr): die Garage im
Spielspeicher und die Telemetrie kennen ein Auto nur als Nummer.

Der Server baut die Datei einmal am Tag neu (`im_hintergrund`); die App holt sie ueber
/api/cars. Eine Abfrage, die scheitert oder zu wenig Autos liefert, ERSETZT NICHTS:
eine halbe Liste liesse Hunderte Autos als "fehlt" erscheinen, die es gar nicht mehr gibt.

Nur Standardbibliothek: der Container auf dem GNAS hat nichts weiter.
"""
from __future__ import annotations

import argparse
import difflib
import json
import re
import threading
import time
import unicodedata
import urllib.parse
import urllib.request
from datetime import datetime, timezone
from html.parser import HTMLParser
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
AUSGABE = WORKSPACE / "data" / "cars" / "fh6_car_availability.json"
ID_NAMEN = WORKSPACE / "config" / "fh6_car_id_names.json"

LISTE_URL = "https://forza.net/fh6cars"
WIKI_API = "https://forza.fandom.com/api.php"
WIKI_SEITE = "https://forza.fandom.com/wiki/"
KENNUNG = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) FHCompanion-carlist/1.0"

FORMAT = "fhc-cars-1"
# Unter so vielen Autos ist die Liste kaputt (Seite umgebaut, halbe Antwort) -- nie speichern.
MINDESTENS = 400
# So alt darf die Datei werden, bevor der Server sie neu baut.
HOECHSTALTER_S = 20 * 3600


# ------------------------------------------------------------------ Abrufen
def _holen(url: str, params: dict | None = None, versuche: int = 3) -> bytes:
    if params:
        url += "?" + urllib.parse.urlencode(params)
    fehler: Exception | None = None
    for i in range(versuche):
        try:
            anfrage = urllib.request.Request(url, headers={"User-Agent": KENNUNG,
                                                           "Accept": "text/html,application/json"})
            with urllib.request.urlopen(anfrage, timeout=60) as antwort:
                return antwort.read()
        except Exception as e:          # noqa: BLE001 -- Netz: jeder Fehler heisst "nochmal"
            fehler = e
            time.sleep(2 + 3 * i)
    raise RuntimeError(f"{url}: {fehler}")


class _Tabelle(HTMLParser):
    """Die Seite ist eine einzige statische Tabelle -- keine Schnittstelle, kein Skriptzustand."""

    def __init__(self) -> None:
        super().__init__()
        self.zeilen: list[list[str]] = []
        self._zeile: list[str] | None = None
        self._zelle: list[str] | None = None

    def handle_starttag(self, tag, attrs):
        if tag == "tr":
            self._zeile = []
        elif tag in ("td", "th") and self._zeile is not None:
            self._zelle = []

    def handle_endtag(self, tag):
        if tag in ("td", "th") and self._zelle is not None and self._zeile is not None:
            self._zeile.append(re.sub(r"\s+", " ", "".join(self._zelle)).strip())
            self._zelle = None
        elif tag == "tr" and self._zeile is not None:
            if self._zeile:
                self.zeilen.append(self._zeile)
            self._zeile = None

    def handle_data(self, data):
        if self._zelle is not None:
            self._zelle.append(data)


_JAHR = re.compile(r"^(\d{4})\s+(.*)$")
_KLASSE = re.compile(r"^(\d{2,3})\s*([A-Z][0-9]?)$")


def _liste_teilen(text: str) -> list[str]:
    return [t.strip() for t in text.split(",") if t.strip()]


def offizielle_liste(html: str) -> list[dict]:
    """Die Zeilen von forza.net/fh6cars. Name ohne Jahr; das Jahr steht daneben."""
    leser = _Tabelle()
    leser.feed(html)
    autos = []
    for z in leser.zeilen:
        if len(z) < 5 or z[0].lower() == "make":
            continue
        name, jahr = z[1], None
        if m := _JAHR.match(z[1]):
            jahr, name = int(m.group(1)), m.group(2)
        pi = klasse = None
        if m := _KLASSE.match(z[3]):
            pi, klasse = int(m.group(1)), m.group(2)
        autos.append({
            "name": name, "make": z[0], "year": jahr, "type": z[2], "pi": pi, "class": klasse,
            "country": z[4],
            "collection": _liste_teilen(z[5]) if len(z) > 5 else [],
            "addons": _liste_teilen(z[6]) if len(z) > 6 else [],
        })
    return autos


def _stand_der_liste(html: str) -> str | None:
    m = re.search(r"[Uu]pdated\s+(?:on\s+)?(\d{1,2}\s+[A-Z][a-z]+\s+\d{4}|[A-Z][a-z]+\s+\d{1,2},?\s+\d{4})", html)
    return m.group(1) if m else None


def wiki_seiten(pause: float = 0.5) -> list[tuple[str, str]]:
    """(Titel, Wikitext) aller Seiten in "Category:Cars (FH6)", je 50 auf einmal."""
    seiten: list[tuple[str, str]] = []
    weiter: dict = {}
    while True:
        params = {"action": "query", "format": "json", "generator": "categorymembers",
                  "gcmtitle": "Category:Cars (FH6)", "gcmlimit": 50, "gcmnamespace": 0,
                  "prop": "revisions", "rvprop": "content", "rvslots": "main", **weiter}
        antwort = json.loads(_holen(WIKI_API, params))
        for seite in (antwort.get("query", {}).get("pages") or {}).values():
            try:
                text = seite["revisions"][0]["slots"]["main"]["*"]
            except (KeyError, IndexError):
                continue
            seiten.append((seite.get("title", ""), text))
        if "continue" not in antwort:
            return seiten
        weiter = antwort["continue"]
        time.sleep(pause)


# ------------------------------------------------------------------ Wikitext
def _vorlage(text: str, kopf: str) -> str | None:
    """Den ganzen Block {{kopf ...}} -- mit verschachtelten {{...}} darin."""
    i = text.find("{{" + kopf)
    if i < 0:
        return None
    tiefe, j = 0, i
    while j < len(text) - 1:
        if text.startswith("{{", j):
            tiefe += 1
            j += 2
            continue
        if text.startswith("}}", j):
            tiefe -= 1
            j += 2
            if tiefe == 0:
                return text[i + 2:j - 2]
            continue
        j += 1
    return None


def _teile(inhalt: str) -> list[str]:
    """An '|' der obersten Ebene trennen -- nicht in {{...}} oder [[...]]."""
    teile, tiefe, anfang, i = [], 0, 0, 0
    while i < len(inhalt):
        zwei = inhalt[i:i + 2]
        if zwei in ("{{", "[["):
            tiefe += 1
            i += 2
            continue
        if zwei in ("}}", "]]"):
            tiefe -= 1
            i += 2
            continue
        if inhalt[i] == "|" and tiefe == 0:
            teile.append(inhalt[anfang:i])
            anfang = i + 1
        i += 1
    teile.append(inhalt[anfang:])
    return teile


def vorlage_felder(inhalt: str) -> tuple[list[str], dict[str, str]]:
    """"Name|a|b|k = v" -> (["a", "b"], {"k": "v"}); der Name selbst faellt weg."""
    teile = _teile(inhalt)[1:]
    reihe, benannt = [], {}
    for t in teile:
        t = t.strip()
        k, gleich, v = t.partition("=")
        if gleich and re.fullmatch(r"[A-Za-z0-9_ -]+", k.strip()) and "{{" not in k:
            benannt[k.strip().lower()] = v.strip()
        else:
            reihe.append(t)
    return reihe, benannt


def _klar(text: str) -> str:
    """Wiki-Auszeichnung weg: [[a|b]] -> b, [[a]] -> a, ''x'' -> x, <ref>, Kategorien."""
    text = re.sub(r"<ref[^>]*>.*?</ref>|<ref[^/]*/>", "", text, flags=re.S)
    text = re.sub(r"\[\[(?:Category|File):[^\]]*\]\]", "", text)
    text = re.sub(r"\[\[(?:[^|\]]*\|)?([^\]]*)\]\]", r"\1", text)
    text = re.sub(r"'{2,}", "", text)
    text = re.sub(r"<[^>]+>", "", text)
    return re.sub(r"\s+", " ", text).strip()


_SAISON = {"su": "Summer", "summer": "Summer", "a": "Autumn", "au": "Autumn", "autumn": "Autumn",
           "w": "Winter", "wi": "Winter", "winter": "Winter", "sp": "Spring", "spring": "Spring",
           "w1": "Winter, week 1", "w2": "Winter, week 2", "w3": "Winter, week 3", "w4": "Winter, week 4"}

_WAS = {
    "champ": '"{x}" championship', "trial": 'The Trial: "{x}"', "season": "season milestone, {x} points",
    "series": "Series milestone, {x} points", "h": "{x} points", "history": "{x} points",
    "lab": 'EventLab: "{x}"', "remix": 'Rush Remix: "{x}"', "collect": 'collectibles: "{x}"',
    "gift": "gift: {x}", "play": 'Horizon Play: "{x}"', "playd": 'Horizon Drift: "{x}"',
    "touge": 'Touge Showdown: "{x}"', "eliminator": 'The Eliminator: "{x}"',
    "hide": 'Hide & Seek: "{x}"', "hideseek": 'Hide & Seek: "{x}"', "seek": 'Hide & Seek: "{x}"',
    "event": '"{x}"', "pg": 'Playground Games: "{x}"', "games": 'Playground Games: "{x}"',
    "playground": 'Playground Games: "{x}"', "ds": 'Danger Sign: "{x}"', "danger": 'Danger Sign: "{x}"',
    "sz": 'Speed Zone: "{x}"', "speedzone": 'Speed Zone: "{x}"', "st": 'Speed Trap: "{x}"',
    "trap": 'Speed Trap: "{x}"', "dz": 'Drift Zone: "{x}"', "drift": 'Drift Zone: "{x}"',
    "tb": 'Trailblazer: "{x}"', "trail": 'Trailblazer: "{x}"', "ta": 'Time Attack: "{x}"',
    "time": 'Time Attack: "{x}"', "dm": 'Drag Meet: "{x}"', "drag": 'Drag Meet: "{x}"',
    "j": 'Seasonal Job: "{x}"', "job": 'Seasonal Job: "{x}"', "sp": 'Stunt Party: "{x}"',
    "stunt": 'Stunt Party: "{x}"',
}


def playlist_eintrag(wert: str) -> dict | None:
    """{{FH6Series|3|au|season|20}} -> {"series": 3, "season": "Autumn", "what": "season milestone, 20 points"}."""
    inhalt = _vorlage(wert, "FH6Series")
    if inhalt is None:
        return None
    reihe, _ = vorlage_felder(inhalt)
    reihe += [""] * (5 - len(reihe))
    serie, saison, art, x = reihe[0].strip(), reihe[1].strip().lower(), reihe[2].strip().lower(), _klar(reihe[3])
    eintrag: dict = {}
    if serie.isdigit():
        eintrag["series"] = int(serie)
    if saison in _SAISON:
        eintrag["season"] = _SAISON[saison]
    if art in _WAS and x:
        # Englisch fuer jeden Leser, dazu Art und Wert: die App uebersetzt die haeufigen
        # Arten (Meisterschaft, Meilenstein) selbst und zeigt den Rest wie hier.
        eintrag["what"] = _WAS[art].format(x=x)
        eintrag["wk"] = art
        eintrag["wx"] = x
    return eintrag


def carstats_fh6(text: str) -> dict[str, str] | None:
    """Die benannten Felder aus {{CarStats|fh6 ...}} -- roh, noch mit Vorlagen darin."""
    for m in re.finditer(r"\{\{CarStats\s*\|\s*fh6\b", text):
        inhalt = _vorlage(text[m.start():], "CarStats")
        if inhalt is not None:
            return vorlage_felder(inhalt)[1]
    return None


def infobox_jahr(text: str) -> int | None:
    inhalt = _vorlage(text, "CarInfobox")
    if inhalt is None:
        return None
    j = vorlage_felder(inhalt)[1].get("year", "")
    return int(j) if re.fullmatch(r"\d{4}", j.strip()) else None


def _zahl(text: str | None) -> int | None:
    ziffern = re.sub(r"[^\d]", "", text or "")
    return int(ziffern) if ziffern and len(ziffern) < 12 else None


def wege(auto: dict, felder: dict[str, str] | None) -> list[dict]:
    """Wie man an dieses Auto kommt: eine Liste kleiner Eintraege, die App uebersetzt die Art.

    Arten: autoshow, wheelspin, playlist, aftermarket, barn, treasure, mastery, journal,
    campaign, gift, loyalty, dlc, auction, unobtainable. Die offizielle Spalte
    "Collection" gibt die groben Wege, das Wiki die Einzelheiten.
    """
    f = felder or {}
    sammlung = {c.lower() for c in auto.get("collection", [])}
    unlock = f.get("unlock", "").strip().lower()
    raus: list[dict] = []

    for paket in auto.get("addons", []):
        raus.append({"k": "dlc", "pack": paket})
    if "autoshow" in sammlung or "autoshow dlc" in sammlung or unlock == "auto":
        raus.append({"k": "autoshow", **({"price": p} if (p := _zahl(f.get("price"))) else {})})
    playlist = playlist_eintrag(f.get("season", "")) if f.get("season") else None
    if playlist is not None or "seasonal" in sammlung or unlock in ("htf", "ex", "exclusive"):
        raus.append({"k": "playlist", **(playlist or {})})
    if "wheelspin" in sammlung or f.get("wheelspin", "").lower() in ("y", "un"):
        raus.append({"k": "wheelspin"})
    after = f.get("after", "").strip().lower()
    if after and after != "n" or "aftermarket" in sammlung:
        eintrag: dict = {"k": "aftermarket"}
        if p := _zahl(f.get("amprice")):
            eintrag["price"] = p
        if f.get("location"):
            ort = _vorlage(f["location"], "FH6AMCarLocation")
            orte = [o.strip() for o in vorlage_felder(ort)[0]] if ort else [_klar(f["location"])]
            if orte and orte[0]:
                eintrag["where"] = ", ".join(o for o in orte if o)
        if f.get("amtakeover"):
            tk = _vorlage(f["amtakeover"], "FH6AMLimited")
            if tk:
                r = vorlage_felder(tk)[0] + ["", "", ""]
                if r[1].strip():
                    eintrag["event"] = r[1].strip()
        raus.append(eintrag)
    if unlock == "barn":
        raus.append({"k": "barn", **({"where": w} if (w := _klar(f.get("barn", ""))) else {})})
    if unlock == "treasure":
        raus.append({"k": "treasure", **({"where": w} if (w := _klar(f.get("treasure", ""))) else {})})
    if unlock == "cm" or f.get("cm"):
        raus.append({"k": "mastery", **({"car": c} if (c := _klar(f.get("cm", ""))) else {})})
    if f.get("collection", "").lower() in ("y", "un") or "collection journal" in sammlung \
            or unlock in ("journal", "collection"):
        eintrag = {"k": "journal"}
        if c := _klar(f.get("cat", "")):
            eintrag["cat"] = c
        if p := _zahl(f.get("points")):
            eintrag["points"] = p
        raus.append(eintrag)
    if f.get("campaign", "").lower() in ("y", "s", "un") or unlock == "campaign":
        raus.append({"k": "campaign", **({"band": b} if (b := _klar(f.get("band", ""))) else {})})
    if f.get("gift", "").lower() in ("y", "un"):
        raus.append({"k": "gift", **({"date": d} if (d := _klar(f.get("giftd", ""))) else {})})
    if f.get("loyalty") or "loyalty" in sammlung:
        raus.append({"k": "loyalty", **({"game": g} if (g := _klar(f.get("loyalty", ""))) else {})})
    if unlock == "un":
        raus.append({"k": "unobtainable"})
    elif f.get("auction", "").lower() != "n":
        raus.append({"k": "auction"})
    return raus


# ------------------------------------------------------------------ Zusammenfuehren
def _norm(text: str) -> str:
    text = unicodedata.normalize("NFKD", text or "")
    text = "".join(c for c in text if not unicodedata.combining(c)).lower()
    text = re.sub(r"\([^)]*\)", " ", text)
    return re.sub(r"[^a-z0-9]+", " ", text).strip()


def _ids_je_auto(pfad: Path) -> dict[tuple[str, int], int]:
    try:
        roh = json.loads(pfad.read_text(encoding="utf-8"))["by_car_id"]
    except (OSError, ValueError, KeyError):
        return {}
    ids: dict[tuple[str, int], int] = {}
    for cid, e in roh.items():
        try:
            schluessel = (_norm(e["name"]), int(e["year"]))
            if cid.lstrip("-").isdigit() and int(cid) > 0:
                ids.setdefault(schluessel, int(cid))
        except (KeyError, TypeError, ValueError):
            continue
    return ids


def zusammenfuehren(liste: list[dict], seiten: list[tuple[str, str]],
                    ids: dict[tuple[str, int], int]) -> tuple[list[dict], dict]:
    """Jedem Auto der offiziellen Liste seine Wiki-Seite und seine car_id zuordnen."""
    je_jahr: dict[int, list[tuple[str, str, dict]]] = {}
    for titel, text in seiten:
        felder = carstats_fh6(text)
        jahr = infobox_jahr(text)
        if felder is None or jahr is None:
            continue
        je_jahr.setdefault(jahr, []).append((_norm(titel), titel, felder))
    zahlen = {"wiki": 0, "id": 0}
    raus = []
    for auto in liste:
        name, jahr = _norm(auto["name"]), auto.get("year")
        kandidaten = je_jahr.get(jahr or 0, [])
        treffer = next((k for k in kandidaten if k[0] == name), None)
        if treffer is None and kandidaten:
            beste = max(kandidaten, key=lambda k: difflib.SequenceMatcher(None, k[0], name).ratio())
            if difflib.SequenceMatcher(None, beste[0], name).ratio() >= 0.86:
                treffer = beste
        eintrag = dict(auto)
        if treffer is not None:
            zahlen["wiki"] += 1
            eintrag["wiki"] = treffer[1]
            if p := _zahl(treffer[2].get("price")):
                eintrag["price"] = p
        if jahr is not None and (cid := ids.get((name, jahr))) is not None:
            zahlen["id"] += 1
            eintrag["id"] = cid
        eintrag["ways"] = wege(auto, treffer[2] if treffer else None)
        raus.append(eintrag)
    return raus, zahlen


def bauen(log=print) -> dict:
    html = _holen(LISTE_URL).decode("utf-8", "replace")
    liste = offizielle_liste(html)
    log(f"forza.net: {len(liste)} Autos")
    if len(liste) < MINDESTENS:
        raise RuntimeError(f"forza.net lieferte nur {len(liste)} Autos -- Seite umgebaut?")
    try:
        seiten = wiki_seiten()
    except Exception as e:          # noqa: BLE001 -- ohne Wiki bleibt die grobe Spalte "Collection"
        log(f"Wiki nicht erreichbar ({e}) -- nur die offizielle Liste")
        seiten = []
    log(f"Wiki: {len(seiten)} Seiten")
    autos, zahlen = zusammenfuehren(liste, seiten, _ids_je_auto(ID_NAMEN))
    log(f"verbunden: {zahlen['wiki']} mit Wiki-Seite, {zahlen['id']} mit car_id")
    return {
        "format": FORMAT,
        "built": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "list_updated": _stand_der_liste(html),
        "sources": {"list": LISTE_URL, "wiki": WIKI_SEITE},
        "cars": autos,
    }


def schreiben(daten: dict, ziel: Path = AUSGABE) -> None:
    """Erst daneben, dann austauschen: ein Abruf waehrend des Schreibens bekommt nie eine halbe Datei."""
    ziel.parent.mkdir(parents=True, exist_ok=True)
    zwischen = ziel.with_suffix(".tmp")
    zwischen.write_text(json.dumps(daten, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    zwischen.replace(ziel)


def aktualisieren(log=print, ziel: Path = AUSGABE) -> bool:
    try:
        daten = bauen(log)
    except Exception as e:          # noqa: BLE001 -- die alte Datei bleibt
        log(f"Autoliste nicht erneuert: {e}")
        return False
    schreiben(daten, ziel)
    log(f"Autoliste erneuert: {len(daten['cars'])} Autos")
    return True


_laeuft = False
_schloss = threading.Lock()


def im_hintergrund(log=print, ziel: Path = AUSGABE) -> None:
    """Im Server: bei Bedarf sofort, danach stuendlich nachsehen, ob die Datei aelter als 20 h ist."""
    global _laeuft
    with _schloss:
        if _laeuft:
            return
        _laeuft = True

    def lauf():
        while True:
            try:
                alter = time.time() - ziel.stat().st_mtime
            except OSError:
                alter = float("inf")
            if alter > HOECHSTALTER_S:
                aktualisieren(log, ziel)
            time.sleep(3600)

    threading.Thread(target=lauf, name="car-list", daemon=True).start()


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--check", action="store_true", help="nur bauen und zusammenfassen")
    ap.add_argument("--out", type=Path, default=AUSGABE)
    args = ap.parse_args(argv)
    daten = bauen()
    arten: dict[str, int] = {}
    for a in daten["cars"]:
        for w in a["ways"]:
            arten[w["k"]] = arten.get(w["k"], 0) + 1
    print("Wege:", ", ".join(f"{k} {n}" for k, n in sorted(arten.items(), key=lambda x: -x[1])))
    print("ohne Weg:", sum(1 for a in daten["cars"] if not a["ways"]))
    if not args.check:
        schreiben(daten, args.out)
        print(args.out)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
