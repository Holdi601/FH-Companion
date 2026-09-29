"""Runden, die Spieler selbst einreichen -- annehmen, pruefen, verwalten.

Getrennt von `contrib_format`, und das ist der Punkt: dort gibt ein VORHER
EINGETRAGENER Beitragender gescannte Bestenlisten ab, mit einem Geheimnis, das er
von Hand bekommen hat. Hier reicht ein beliebiger Spieler eine selbst gefahrene
Runde ein, ohne dass ihn jemand kennt.

## Der Handschlag

Es gibt kein Geheimnis, das man jemandem vorher geben koennte. Also holt sich die
App eines:

    POST /api/lap/register   {"hardware": "<Hash>", "gamertag": "..."}
    ->                       {"install_id": "...", "secret": "..."}

Der Gamertag ist freiwillig (seit 2026-09-27): gesperrt wird ueber Kennung und
Hardware-Hash, nicht ueber einen Namen. Die App schickt ihn mit jeder Einreichung
(set_gamertag), und angezeigt wird er erst beim Ausliefern der Liste (spielernamen):
ein Spieler ist ein Hardware-Hash, sein Name der zuletzt geschickte, und der gilt
fuer ALLE seine Runden, auch die frueheren. Ohne je einen Namen bekommt er einen
vorlaeufigen ("Player-7F3A2C"); ein leeres Feld spaeter loescht den Namen nicht.

Das Geheimnis wird EINMAL uebertragen und danach nie wieder -- jede Einreichung
rechnet damit, statt es mitzuschicken.

## Warum ueberhaupt unterschreiben, wenn sich jeder anmelden kann

Nicht um zu verhindern, dass jemand mitmacht -- das soll er ja. Sondern damit eine
Einreichung einem Konto ZUZUORDNEN ist. Ohne das laesst sich niemand sperren: wer
Unsinn einreicht, tut es unter einer Kennung, und die laesst sich stilllegen.

Wer sich neu anmeldet, faengt neu an -- aber der Hardware-Hash bleibt, und darueber
faellt eine Kette von Neuanmeldungen auf.

## Der Hardware-Hash und warum er nochmal gehasht wird

Die App schickt einen Hash, keine Kennung. Der Server hasht ihn ein zweites Mal mit
einem eigenen Geheimnis (dem "Pfeffer"), bevor er ihn ablegt.

Der Grund ist nicht Misstrauen gegen die App, sondern gegen die eigene Datei: ein
Hash ohne Pfeffer laesst sich durchprobieren, wenn man weiss, WORAUS er gebildet
wurde. Mit Pfeffer geht das nur, wenn man auch den Pfeffer hat -- und der liegt
nicht in derselben Datei wie die Auswertung, sondern in `config/submit_keys.json`,
die nie das Haus verlaesst.

Der Gamertag steht dagegen im Klartext da. Er ist der Name, unter dem die Zeit
erscheinen soll; ihn zu verbergen waere sinnlos.

## Was hier als Betrugsschutz NICHT behauptet wird

Diese Pruefungen finden UNMOEGLICHES, nicht Unwahrscheinliches: eine Runde, die
schneller ist als die Physik erlaubt, ein Sprung von 400 m zwischen zwei Messpunkten,
eine Uhr, die rueckwaerts laeuft. Wer mit einem veraenderten Spiel eine plausible
Zeit faehrt, kommt hier durch -- dagegen hilft nur ein Mensch, der sich die
Telemetrie ansieht. Genau dafuer wird sie mitgespeichert.
"""

from __future__ import annotations

import base64
import binascii
import gzip
import hashlib
import hmac
import json
import math
import os
import re
import secrets
import threading
import time
import zlib
from datetime import datetime, timezone
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
KEYS_FILE = WORKSPACE / "config" / "submit_keys.json"
LAPS_DIR = WORKSPACE / "data" / "submissions" / "laps"

CLOCK_SKEW = 300
SECRET_BYTES = 32
# 16 MB: seit 2026-09-28 reist die volle Telemetrie mit (gepackt, als Base64). Eine
# 214-s-Runde sind 1,8 MB gepackt, eine Viertelstunde waeren rund 8 MB.
MAX_BODY = 16 * 1024 * 1024

# Mit '#': neuere Xbox-Gamertags tragen eine Nummer ("Name#1234").
GAMERTAG_RE = re.compile(r"^[\w][\w .'#-]{0,29}$", re.UNICODE)
HARDWARE_RE = re.compile(r"^[0-9a-f]{32,128}$")
INSTALL_RE = re.compile(r"^[A-Za-z0-9_-]{16,64}$")

# Physikalische Grenzen. Sie sind ABSICHTLICH weit: sie sollen Unmoegliches
# abweisen, nicht Ungewoehnliches. Das schnellste Auto im Spiel liegt bei rund
# 500 km/h; 600 laesst Raum fuer einen Abhang und eine Rampe.
MAX_SPEED_MS = 600 / 3.6
# Unter 10 km/h Schnitt ist keine gewertete Runde mehr, sondern ein Spaziergang.
MIN_SCHNITT_MS = 10 / 3.6
# Zwischen zwei Messpunkten liegen 5 m Weg; ein Sprung von mehr als 300 m ist kein
# Fahren mehr, sondern ein Versetzen.
MAX_SPRUNG_M = 300.0
# Wie viele Kennungen sich EINE Maschine holen darf.
#
# ACHTUNG, DIESE GRENZE IST SCHWACH -- und das ist bekannt, nicht uebersehen: sie
# haengt am Hardware-Hash, und den schickt der CLIENT. Ein Skript setzt jedes Mal
# einen anderen hinein und umgeht sie muehelos. Sie haelt Versehen ab, keinen
# Angriff.
MAX_INSTALLS_JE_MASCHINE = 5

# DIE GRENZE, DIE WIRKLICH TRAEGT: je Absenderadresse.
#
# Die IP-Adresse behauptet der Aufrufer nicht, sie entsteht an der Leitung. Sie ist
# damit das Einzige an einer offenen Anmeldung, das sich nicht frei erfinden laesst.
#
# Die Zahlen sind grosszuegig genug fuer einen ganzen Haushalt hinter einer Adresse
# und eng genug, dass niemand in einer Nacht die Schluesseldatei aufblaeht: ohne
# Bremse legt ein Skript mit wechselndem Hardware-Hash beliebig viele Konten an.
#
# Was sie NICHT kann: jemanden aufhalten, der ueber viele Adressen verfuegt. Dagegen
# hilft nur, die Anmeldung ganz zuzumachen -- wofuer der Schalter in
# config/features.json da ist.
ANMELDUNGEN_JE_IP_STUNDE = 5
ANMELDUNGEN_JE_IP_TAG = 20
ANMELDE_VERSUCHE = WORKSPACE / "data" / "runtime" / "register_attempts.json"

# WIE LANGE EIN HARDWARE-HASH BLEIBT, DER NICHTS MEHR TUT.
#
# Im Datenschutzhinweis steht zugesagt: zwoelf Monate ohne Einreichung, dann weg.
# Eine Zusage, die nur in einem Dokument steht und nirgends im Code, ist keine --
# sie faellt beim ersten Auskunftsersuchen auf, und dann ist sie eine Unwahrheit
# statt einer Absicht.
#
# GESPERRTE bleiben laenger. Sonst hoebe sich jede Sperre nach einem Jahr von
# selbst auf, und zwar lautlos: der Gesperrte meldet sich neu an und ist wieder da.
# Das ist der eine Fall, in dem Aufbewahren dem Zweck dient statt ihm zu schaden.
HASH_FRIST_TAGE = 365
HASH_FRIST_TAGE_GESPERRT = 365 * 3


# EIN SCHLOSS FUER DEN SCHLUESSELBUND. Der Server beantwortet jede Anfrage in einem
# eigenen Faden; register, verify und store lesen die Datei, aendern sie und
# schreiben sie zurueck. Ohne Schloss gewinnt der letzte Schreiber -- eine Sperre,
# eine Nonce oder eine Anmeldung, die dazwischen lag, ist dann still verloren.
_SCHLOSS = threading.RLock()

# Wie viele Runden EINE Installation an einem Tag einreichen darf. Grosszuegig fuer
# jemanden, der einen Abend lang faehrt; eng genug, dass ein Skript mit einer
# Kennung den Datentraeger nicht in einer Nacht fuellt (je Runde bis 8 MB).
RUNDEN_JE_INSTALL_TAG = 100
# Die meisten Messpunkte, die eine Runde tragen darf: alle 5 m einer, also reicht
# das fuer 250 km -- laenger als jede Strecke im Spiel.
MAX_PROBEN = 50_000

# Die volle Telemetrie (die .tele.gz der App): gepackt hoechstens so gross, und beim
# Entpacken nie mehr als das -- eine kleine Datei, die zu Gigabytes aufgeht, soll
# den Server nicht in die Knie zwingen.
MAX_TELE_GEPACKT = 12 * 1024 * 1024
MAX_TELE_ENTPACKT = 200 * 1024 * 1024
MAX_TELE_ZEILEN = 60_000     # die App kappt bei 54 000 (TelemetryTrack.MaxRows)
MAX_TELE_SPALTEN = 256

# Wie viele Runden je Auto, Strecke und Leistungsklasse aufgehoben werden -- und
# davon hoechstens EINE je Mensch (Hardware-Kennung). Wer zehn gefaelschte Runden
# schickt, belegt so einen Platz, nicht alle: die neun davor bleiben als Rueckfall,
# wenn seine Runde ausgeblendet oder er gesperrt wird.
BEHALTEN_JE_GRUPPE = 10


def _atomar_schreiben(path: Path, text: str) -> None:
    """Erst daneben schreiben, dann tauschen: ein Absturz mitten im Schreiben
    hinterlaesst die alte Datei, nie eine halbe."""
    tmp = path.with_suffix(path.suffix + ".tmp")
    tmp.write_text(text, encoding="utf-8")
    os.replace(tmp, path)


class SubmitError(Exception):
    def __init__(self, status: int, message: str):
        super().__init__(message)
        self.status = status
        self.message = message


# --------------------------------------------------------------------------- #
# Schluesselbund


def load_keys(path: Path | None = None) -> dict:
    path = path or KEYS_FILE
    if not path.exists():
        return {"pepper": "", "installs": {}}
    try:
        d = json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        return {"pepper": "", "installs": {}}
    d.setdefault("pepper", "")
    d.setdefault("installs", {})
    return d


def save_keys(data: dict, path: Path | None = None) -> None:
    path = path or KEYS_FILE
    path.parent.mkdir(parents=True, exist_ok=True)
    _atomar_schreiben(path, json.dumps(data, indent=1, ensure_ascii=False))
    try:
        path.chmod(0o600)
    except OSError:
        # Unter Windows ohne Wirkung; die Datei steht dort ohnehin im Benutzerprofil.
        pass


def ensure_pepper(path: Path | None = None) -> str:
    """Den Pfeffer holen, und ihn anlegen, falls es noch keinen gibt.

    Einmal erzeugt, darf er sich NIE mehr aendern: alle abgelegten Hardware-Hashes
    sind mit ihm gebildet. Ein neuer Pfeffer machte jede Sperre wirkungslos, ohne
    dass irgendwo eine Fehlermeldung erschiene -- der Gesperrte waere einfach wieder
    da.
    """
    data = load_keys(path)
    if not data.get("pepper"):
        data["pepper"] = secrets.token_urlsafe(SECRET_BYTES)
        data["pepper_created"] = datetime.now(timezone.utc).isoformat(timespec="seconds")
        data["_"] = ("Geheimnisse fuer eingereichte Runden. Der Pfeffer darf sich NIE "
                     "aendern -- alle Hardware-Hashes sind mit ihm gebildet. Nicht "
                     "weitergeben, nicht in ein Repository legen.")
        save_keys(data, path)
    return data["pepper"]


def hardware_id(roh: str, path: Path | None = None) -> str:
    return hashlib.sha256((ensure_pepper(path) + ":" + roh).encode("utf-8")).hexdigest()


# --------------------------------------------------------------------------- #
# Anmelden


def _versuche_laden(path: Path | None = None) -> dict:
    try:
        return json.loads((path or ANMELDE_VERSUCHE).read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        return {}


def _versuche_sichern(data: dict, path: Path | None = None) -> None:
    ziel = path or ANMELDE_VERSUCHE
    try:
        ziel.parent.mkdir(parents=True, exist_ok=True)
        ziel.write_text(json.dumps(data, indent=1), encoding="utf-8")
    except OSError:
        # Eine Anmeldung soll nicht daran scheitern, dass das Zaehlwerk nicht
        # schreiben kann. Beim LESEN wird dagegen nichts geschluckt: ein Zaehlwerk,
        # das sich nicht lesen laesst, muss wie ein volles wirken -- sonst waere die
        # Bremse durch eine kaputte Datei auszuhebeln.
        pass


def anmeldebremse(client: str, now: float | None = None,
                  path: Path | None = None) -> None:
    """Darf von dieser Adresse noch eine Anmeldung kommen? Sonst SubmitError.

    Auf Platte und nicht im Speicher: ein Neustart des Servers darf keine Bremse
    loesen, sonst ist sie nur eine Bitte -- dieselbe Ueberlegung wie beim Zaehlwerk
    der Hochladeversuche.
    """
    if not client:
        return
    jetzt = now or time.time()
    data = _versuche_laden(path)
    zeiten = [float(z) for z in data.get(client, []) if isinstance(z, (int, float))]
    # Alles aelter als ein Tag faellt weg -- die Datei soll nicht ewig wachsen.
    zeiten = [z for z in zeiten if jetzt - z < 86400]

    letzte_stunde = sum(1 for z in zeiten if jetzt - z < 3600)
    if letzte_stunde >= ANMELDUNGEN_JE_IP_STUNDE:
        raise SubmitError(429, "This address has already registered %d times in the "
                               "last hour. Try again later."
                               % letzte_stunde)
    if len(zeiten) >= ANMELDUNGEN_JE_IP_TAG:
        raise SubmitError(429, "This address has already registered %d times in the "
                               "last 24 hours. Try again tomorrow."
                               % len(zeiten))

    zeiten.append(jetzt)
    data[client] = zeiten
    # Adressen, von denen seit einem Tag nichts kam, ganz entfernen.
    for anderer in [k for k, v in data.items()
                    if k != client and not any(jetzt - float(z) < 86400
                                               for z in v if isinstance(z, (int, float)))]:
        data.pop(anderer, None)
    _versuche_sichern(data, path)


def register(hardware: str, gamertag: str, path: Path | None = None,
             now: float | None = None, client: str = "",
             attempts_path: Path | None = None) -> dict:
    with _SCHLOSS:
        return _register(hardware, gamertag, path, now, client, attempts_path)


def _register(hardware: str, gamertag: str, path: Path | None = None,
              now: float | None = None, client: str = "",
              attempts_path: Path | None = None) -> dict:
    # ZUERST die Bremse, dann alles andere. Wer zu oft anklopft, soll nicht erst
    # eine Pruefung der Eingaben bekommen -- das waere Arbeit fuer den Server und
    # eine Auskunft fuer den Anklopfenden.
    anmeldebremse(client, now, attempts_path)
    hardware = (hardware or "").strip().lower()
    gamertag = (gamertag or "").strip()
    if not HARDWARE_RE.match(hardware):
        raise SubmitError(400, "'hardware' must be a hex hash of 32 to 128 characters.")
    # OHNE GAMERTAG GEHT ES AUCH -- siehe oben. Nur ein unbrauchbarer wird abgewiesen.
    if gamertag and not GAMERTAG_RE.match(gamertag):
        raise SubmitError(400, "'gamertag' contains characters that are not allowed.")

    gepfeffert = hardware_id(hardware, path)
    data = load_keys(path)

    # SCHON GESPERRT? DANN GAR NICHT ERST ANMELDEN.
    #
    # Sonst waere jede Sperre einen Programmneustart lang gueltig. Die Antwort nennt
    # den Grund: wer zu Unrecht gesperrt ist, soll wissen, woran er ist, statt auf
    # einen Fehler zu tippen.
    gleiche = 0
    for kennung, eintrag in data["installs"].items():
        if eintrag.get("hw") != gepfeffert:
            continue
        gleiche += 1
        if eintrag.get("banned"):
            raise SubmitError(403, "This machine is banned: "
                                   + (eintrag.get("ban_reason") or "no reason given"))

    # EINE MASCHINE BRAUCHT KEINE ZWANZIG KENNUNGEN.
    #
    # Das Anmelden ist offen -- es muss offen sein, sonst kann niemand mitmachen.
    # Offen heisst aber auch: ein Skript koennte in einer Minute tausend Kennungen
    # erzeugen und die Schluesseldatei aufblaehen, bis sie nicht mehr zu lesen ist.
    # Der Hardware-Hash begrenzt das auf ein vernuenftiges Mass, ohne jemanden
    # auszusperren: Neuinstallation, zweites Windows, ein zurueckgesetztes Profil --
    # fuenf davon sind reichlich.
    if gleiche >= MAX_INSTALLS_JE_MASCHINE:
        raise SubmitError(429, "This machine already has %d installation IDs. If "
                               "you really need more, contact the operator."
                               % gleiche)

    install_id = secrets.token_urlsafe(24)[:32]
    angelegt = datetime.fromtimestamp(now or time.time(), timezone.utc).isoformat(timespec="seconds")
    data["installs"][install_id] = {
        "secret": secrets.token_urlsafe(SECRET_BYTES),
        "hw": gepfeffert,
        "gamertag": gamertag,
        "created": angelegt,
        "banned": False,
        "laps": 0,
    }
    if gamertag:
        data["installs"][install_id]["gamertag_set"] = angelegt
    save_keys(data, path)
    ergebnis = {"install_id": install_id,
                "secret": data["installs"][install_id]["secret"]}
    # Die Zusage aus dem Datenschutzhinweis einloesen, im selben Atemzug: hier
    # wird die Datei ohnehin geschrieben, und ein Aufraeumen, das jemand von Hand
    # anstossen muesste, findet nicht statt.
    try:
        aufraeumen(now, path)
    except Exception:
        # Eine Anmeldung darf nicht daran scheitern, dass das Aufraeumen stolpert.
        pass
    return ergebnis


# --------------------------------------------------------------------------- #
# Namen


def set_gamertag(install_id: str, gamertag, eintrag: dict | None = None,
                 path: Path | None = None, now: float | None = None) -> bool:
    """Den Namen einer Installation nachziehen -- aus einer UNTERSCHRIEBENEN Einreichung.

    Fehlt das Feld (aeltere App) oder ist es LEER, bleibt der bisherige Name: wer
    das Feld in der App leert, behaelt auf der Seite den Namen, den er hatte. Ein
    unbrauchbarer wird still uebergangen -- die Runde selbst ist deswegen nicht
    falsch und soll nicht abgewiesen werden. `eintrag` (aus verify) wird mit
    geaendert, damit store() schon den neuen Namen ablegt.
    """
    if not isinstance(gamertag, str):
        return False
    gamertag = gamertag.strip()
    if not gamertag or not GAMERTAG_RE.match(gamertag):
        return False
    with _SCHLOSS:
        data = load_keys(path)
        e = data["installs"].get(install_id)
        if not e or (e.get("gamertag") or "") == gamertag:
            return False
        e["gamertag"] = gamertag
        e["gamertag_set"] = (datetime.fromtimestamp(now or time.time(), timezone.utc)
                             .isoformat(timespec="seconds"))
        save_keys(data, path)
    if eintrag is not None:
        eintrag["gamertag"] = gamertag
    return True


def vorlaeufiger_name(schluessel: str) -> str:
    """Ein Anzeigename fuer einen Spieler, der nie einen Gamertag geschickt hat.

    Aus dem (gepfefferten) Hardware-Hash, noch einmal gehasht: immer derselbe fuer
    dieselbe Maschine, und nichts daran fuehrt zum Hash zurueck.
    """
    roh = ("anzeige|" + (schluessel or "")).encode("utf-8")
    return "Player-" + hashlib.sha256(roh).hexdigest()[:6].upper()


def spielernamen(keys_path: Path | None = None) -> dict:
    """Installation -> (Anzeigename, vorlaeufig?) -- so, wie die Seite ihn zeigt.

    EIN SPIELER IST EIN HARDWARE-HASH, nicht eine Installation: eine Neuinstallation
    oder (bei aelteren Fassungen der App) ein neuer Gamertag legte eine neue
    Kennung an, und doch ist es derselbe Mensch. Sein Name ist der zuletzt
    geschickte unter allen seinen Kennungen, und der gilt fuer alle seine Runden --
    wer sich umbenennt, dessen fruehere Zeiten tragen den neuen Namen. Darum wird
    der Name beim Ausliefern eingesetzt und nicht aus der abgelegten Runde gelesen.
    """
    installs = load_keys(keys_path)["installs"]
    letzter: dict = {}
    for kennung, e in installs.items():
        name = (e.get("gamertag") or "").strip()
        if not name:
            continue
        spieler = e.get("hw") or kennung
        wann = e.get("gamertag_set") or e.get("created") or ""
        if spieler not in letzter or wann >= letzter[spieler][0]:
            letzter[spieler] = (wann, name)
    namen = {}
    for kennung, e in installs.items():
        spieler = e.get("hw") or kennung
        namen[kennung] = ((letzter[spieler][1], False) if spieler in letzter
                          else (vorlaeufiger_name(spieler), True))
    return namen


def mit_spielernamen(runden: list, keys_path: Path | None = None) -> list:
    """Jede Runde mit dem HEUTIGEN Namen ihres Spielers (siehe spielernamen)."""
    namen = spielernamen(keys_path)
    for r in runden:
        kennung = r.get("install_id") or ""
        if kennung in namen:
            r["gamertag"], r["gamertag_temporary"] = namen[kennung]
        elif not (r.get("gamertag") or "").strip():
            # Eine Runde, deren Kennung nicht mehr bekannt ist (aufgeraeumt).
            r["gamertag"], r["gamertag_temporary"] = vorlaeufiger_name(kennung or r.get("id", "")), True
    return runden


# --------------------------------------------------------------------------- #
# Unterschrift


def signature(secret: str, method: str, path_: str, stamp: str, nonce: str,
              body: bytes) -> str:
    rumpf = hashlib.sha256(body or b"").hexdigest()
    text = "\n".join([method.upper(), path_, stamp, nonce, rumpf])
    return hmac.new(secret.encode("utf-8"), text.encode("utf-8"),
                    hashlib.sha256).hexdigest()


def verify(headers, method: str, path_: str, body: bytes,
           path: Path | None = None, now: float | None = None) -> tuple[str, dict]:
    """Die Unterschrift pruefen. Gibt (install_id, Eintrag) zurueck."""
    with _SCHLOSS:
        return _verify(headers, method, path_, body, path, now)


def _verify(headers, method: str, path_: str, body: bytes,
            path: Path | None = None, now: float | None = None) -> tuple[str, dict]:
    def kopf(name: str) -> str:
        try:
            return (headers.get(name) or "").strip()
        except AttributeError:
            return ""

    install_id = kopf("X-Forza-Install")
    stamp = kopf("X-Forza-Timestamp")
    nonce = kopf("X-Forza-Nonce")
    unterschrift = kopf("X-Forza-Signature")

    if not INSTALL_RE.match(install_id):
        raise SubmitError(401, "X-Forza-Install is missing or invalid.")
    if not (stamp and nonce and unterschrift):
        raise SubmitError(401, "X-Forza-Timestamp, -Nonce or -Signature is missing.")
    if not re.match(r"^[A-Za-z0-9_-]{8,64}$", nonce):
        raise SubmitError(401, "X-Forza-Nonce is invalid.")

    data = load_keys(path)
    eintrag = data["installs"].get(install_id)
    if not eintrag:
        raise SubmitError(401, "This installation is not registered. "
                               "Register again: POST /api/lap/register")
    if eintrag.get("banned"):
        raise SubmitError(403, "This installation is banned: "
                               + (eintrag.get("ban_reason") or "no reason given"))

    try:
        drift = abs((now or time.time()) - float(stamp))
    except ValueError:
        raise SubmitError(401, "X-Forza-Timestamp is not a number.") from None
    if drift > CLOCK_SKEW:
        raise SubmitError(401, "The timestamp is off by %.0f s; %d s are allowed. "
                               "Check this PC's clock." % (drift, CLOCK_SKEW))

    erwartet = signature(eintrag["secret"], method, path_, stamp, nonce, body)
    if not hmac.compare_digest(erwartet, unterschrift):
        raise SubmitError(401, "The signature does not match.")

    # WIEDERHOLUNG AUSSCHLIESSEN.
    #
    # Ueber einfaches HTTP liest jeder im selben Netz den ganzen Aufruf mit -- samt
    # Unterschrift. Ohne diese Sperre koennte er ihn beliebig oft erneut senden und
    # dieselbe Runde hundertmal einreichen. Der Zeitstempel begrenzt das Fenster auf
    # CLOCK_SKEW; innerhalb dieses Fensters zaehlt die Nonce.
    benutzt = eintrag.setdefault("nonces", {})
    jetzt = now or time.time()
    for alt in [n for n, t in benutzt.items() if jetzt - t > CLOCK_SKEW * 2]:
        benutzt.pop(alt, None)
    if nonce in benutzt:
        raise SubmitError(409, "This nonce was already used.")
    benutzt[nonce] = jetzt
    save_keys(data, path)
    return install_id, eintrag


# --------------------------------------------------------------------------- #
# Pruefung einer eingereichten Runde


def _zahl(wert, name: str, klein: float, gross: float) -> float:
    try:
        z = float(wert)
    except (TypeError, ValueError):
        raise SubmitError(400, "'%s' is not a number." % name) from None
    if math.isnan(z) or math.isinf(z):
        raise SubmitError(400, "'%s' is not a finite number." % name)
    if not (klein <= z <= gross):
        raise SubmitError(400, "'%s' is outside the possible range (%s)." % (name, z))
    return z


def pruefe_runde(runde: dict) -> list:
    """Die Runde auf Unmoegliches pruefen. Gibt die Auffaelligkeiten zurueck.

    Wirft bei allem, was die Runde unbrauchbar macht; gibt zurueck, was nur
    merkwuerdig ist. Die Unterscheidung ist wichtig: eine unbrauchbare Runde darf
    gar nicht erst abgelegt werden, eine merkwuerdige gehoert abgelegt UND
    gekennzeichnet, damit ein Mensch sie ansehen kann. Wer Merkwuerdiges gleich
    verwirft, verliert genau die Faelle, aus denen sich lernen liesse.
    """
    sekunden = _zahl(runde.get("lapSeconds"), "lapSeconds", 1.0, 7200.0)
    meter = _zahl(runde.get("lengthMetres"), "lengthMetres", 100.0, 200000.0)

    auffaellig = []
    schnitt = meter / sekunden
    if schnitt > MAX_SPEED_MS:
        raise SubmitError(422, "The average speed would be %.0f km/h -- that is "
                               "not possible." % (schnitt * 3.6))
    if schnitt < MIN_SCHNITT_MS:
        auffaellig.append("average only %.0f km/h" % (schnitt * 3.6))

    proben = runde.get("samples")
    if not isinstance(proben, list) or len(proben) < 10:
        raise SubmitError(422, "Laps without telemetry are not accepted -- "
                               "at least 10 samples are needed.")
    if len(proben) > MAX_PROBEN:
        raise SubmitError(413, "More than %d samples." % MAX_PROBEN)

    # Die Messpunkte muessen eine FAHRT beschreiben: Zeit und Weg laufen vorwaerts,
    # der Abstand zwischen zwei Punkten ist fahrbar, und kein Punkt liegt weiter weg
    # als ein Auto in dieser Zeit kommt.
    letzte_zeit = -1.0
    letzter_weg = -1.0
    letzte_pos = None
    for i, p in enumerate(proben):
        if not isinstance(p, dict):
            raise SubmitError(400, "Sample %d is not an object." % i)
        t = _zahl(p.get("Seconds", p.get("seconds")), "Seconds", 0.0, 7200.0)
        m = _zahl(p.get("Metres", p.get("metres")), "Metres", 0.0, 200000.0)
        if t < letzte_zeit:
            raise SubmitError(422, "The clock runs backwards at sample %d." % i)
        if m < letzter_weg:
            raise SubmitError(422, "The distance gets shorter at sample %d." % i)
        x = _zahl(p.get("X", p.get("x", 0)), "X", -1e6, 1e6)
        z = _zahl(p.get("Z", p.get("z", 0)), "Z", -1e6, 1e6)
        if letzte_pos is not None:
            sprung = math.dist((x, z), letzte_pos)
            if sprung > MAX_SPRUNG_M:
                raise SubmitError(422, "Samples %d and %d are %.0f m apart "
                                       "-- that is not driving." % (i - 1, i, sprung))
            dt = t - letzte_zeit
            if dt > 0 and sprung / dt > MAX_SPEED_MS:
                raise SubmitError(422, "Between samples %d and %d the car would "
                                       "need %.0f km/h." % (i - 1, i,
                                                              sprung / dt * 3.6))
        letzte_zeit, letzter_weg, letzte_pos = t, m, (x, z)

    # Der letzte Messpunkt muss zur behaupteten Rundenzeit passen. Sonst laege die
    # Telemetrie einer langsamen Runde unter einer schnellen Zeit -- die einfachste
    # denkbare Faelschung, und ohne diese Zeile die wirksamste.
    if abs(letzte_zeit - sekunden) > max(2.0, 0.05 * sekunden):
        raise SubmitError(422, "The telemetry ends at %.1f s, but the lap time "
                               "given is %.1f s." % (letzte_zeit, sekunden))
    if abs(letzter_weg - meter) > max(100.0, 0.10 * meter):
        auffaellig.append("telemetry ends at %.0f m, %.0f m given"
                          % (letzter_weg, meter))

    pi = int(_zahl(runde.get("performanceIndex", 0), "performanceIndex", 0, 999))
    if pi <= 0:
        auffaellig.append("no performance index")
    return auffaellig


# --------------------------------------------------------------------------- #
# Ablegen und verwalten


# ---------------------------------------------------------------- saeubern

_FELDNAME = re.compile(r"^[A-Za-z][A-Za-z0-9_]{0,40}$")


def _saubere_zeichen(text: str, laenge: int) -> str:
    # Steuerzeichen raus, Laenge begrenzt. Das Maskieren fuer HTML ist Sache der
    # Seiten -- hier geht es darum, dass nichts Beliebiges abgelegt wird.
    return "".join(c for c in str(text) if c >= " " and c != "\x7f")[:laenge]


def _wert(v, tiefe: int = 0):
    if isinstance(v, bool):
        return v
    if isinstance(v, (int, float)):
        return v if math.isfinite(float(v)) else None
    if isinstance(v, str):
        return _saubere_zeichen(v, 200)
    if isinstance(v, dict) and tiefe < 2:
        return {k: w for k, w in ((k, _wert(x, tiefe + 1)) for k, x in v.items()
                                  if isinstance(k, str) and _FELDNAME.match(k))
                if w is not None}
    if isinstance(v, list) and tiefe < 2 and len(v) <= 1000:
        return [w for w in (_wert(x, tiefe + 1) for x in v) if w is not None]
    return None


def saeubere_runde(runde: dict) -> dict:
    """Die eingereichte Runde auf das bringen, was abgelegt werden darf.

    WARUM: die Runde wurde bis 2026-09-24 so abgelegt, wie sie kam, und die
    Verwaltungsseite setzte `carOrdinal` ungeprueft in ihr HTML. Eine Runde mit
    `"carOrdinal": "<img src=x onerror=...>"` haette im Browser des Verwalters
    Skript ausgefuehrt -- in dem Tab, der das Admin-Geheimnis haelt.

    Nach Art, nicht nach Namen: die App schickt ihr ganzes Rundenobjekt, und eine
    feste Namensliste braeche beim naechsten neuen Feld still die Einreichung.
    Die Felder, die Seiten ANZEIGEN, bekommen zusaetzlich ihren genauen Typ.
    """
    sauber = {}
    for k, v in runde.items():
        if not isinstance(k, str) or not _FELDNAME.match(k):
            continue
        if k == "samples":
            proben = []
            for p in (v if isinstance(v, list) else [])[:MAX_PROBEN]:
                if isinstance(p, dict):
                    proben.append({pk: pv for pk, pv in p.items()
                                   if isinstance(pk, str) and _FELDNAME.match(pk)
                                   and isinstance(pv, (int, float, bool))
                                   and (isinstance(pv, bool) or math.isfinite(float(pv)))})
            sauber["samples"] = proben
            continue
        w = _wert(v)
        if w is not None:
            sauber[k] = w
    # Was die Seiten zeigen, in genau der erwarteten Form.
    for feld in ("carOrdinal", "carClass"):
        if feld in sauber:
            try:
                sauber[feld] = int(float(sauber[feld]))
            except (TypeError, ValueError):
                sauber.pop(feld, None)
    for feld in ("performanceIndex", "lapSeconds", "lengthMetres"):
        if feld in sauber and not isinstance(sauber[feld], (int, float)):
            sauber.pop(feld, None)
    for feld in ("carName", "track", "course", "recordedAt", "mode"):
        if feld in sauber:
            sauber[feld] = _saubere_zeichen(sauber[feld], 120)
    return sauber


# STRAFPUNKTE. Eine Installation, die immer wieder Unbrauchbares schickt --
# unmoegliche Zeiten, kaputte Telemetrie, oder Runden, die DEUTLICH langsamer sind
# als die Bestenliste --, ist entweder manipuliert oder kaputt. Die echte App prueft
# selbst vorher, ob eine Runde schneller ist; bei ihr kommt so etwas nicht vor.
# Knapp langsamer (unter 3 %) zaehlt NICHT: der Datensatz der App kann etwas
# aelter sein als der des Servers, und ein ehrlicher Nutzer soll dafuer nicht
# gesperrt werden.
STRAFPUNKTE_GRENZE = 5
STRAFPUNKTE_FENSTER = 24 * 3600
DEUTLICH_LANGSAMER = 0.03


def strafpunkt(install_id: str, grund: str, now: float | None = None,
               keys_path: Path | None = None) -> bool:
    """Einen Strafpunkt vergeben. True, wenn die Installation damit gesperrt ist.

    Gesperrt wird dann auch jede andere Installation mit demselben Hardware-Hash,
    und die Anmeldung weist diese Maschine ab (register prueft das) -- sonst waere
    die Sperre einen Neustart der App lang gueltig.
    """
    jetzt = now or time.time()
    with _SCHLOSS:
        data = load_keys(keys_path)
        e = data["installs"].get(install_id)
        if not e:
            return False
        punkte = [t for t in e.get("strikes", []) if jetzt - float(t) < STRAFPUNKTE_FENSTER]
        punkte.append(jetzt)
        e["strikes"] = punkte[-20:]
        e["last_strike"] = _saubere_zeichen(grund, 200)
        neu_gesperrt = len(punkte) >= STRAFPUNKTE_GRENZE and not e.get("banned")
        betroffen = []
        if neu_gesperrt:
            grund_text = ("auto: %d rejected submissions within 24 h (last: %s)"
                          % (len(punkte), _saubere_zeichen(grund, 120)))
            for kennung, andere in data["installs"].items():
                if andere.get("hw") == e.get("hw") and not andere.get("banned"):
                    betroffen.append(kennung)
        save_keys(data, keys_path)
    for kennung in betroffen:
        set_banned(kennung, True, grund_text, keys_path=keys_path)
        with _SCHLOSS:
            data = load_keys(keys_path)
            if kennung in data["installs"]:
                data["installs"][kennung]["banned_auto"] = True
                save_keys(data, keys_path)
    return neu_gesperrt


def install_von_runde(kennung: str, root: Path | None = None) -> str:
    """Wem gehoert diese eingereichte Runde? Fuer "Spieler sperren" in der Verwaltung."""
    root = root or LAPS_DIR
    if not re.match(r"^[0-9a-f]{20}$", kennung or ""):
        raise SubmitError(400, "Not a valid ID.")
    p = root / (kennung + ".json")
    if not p.exists():
        raise SubmitError(404, "No lap with the ID %r." % kennung)
    return str(json.loads(p.read_text(encoding="utf-8-sig")).get("install_id") or "")


def tageskontingent(install_id: str, now: float | None = None,
                    keys_path: Path | None = None) -> None:
    """Noch Platz fuer eine Runde heute? Sonst SubmitError 429."""
    with _SCHLOSS:
        data = load_keys(keys_path)
        e = data["installs"].get(install_id)
        if not e:
            return
        tag = datetime.fromtimestamp(now or time.time(), timezone.utc).strftime("%Y-%m-%d")
        if e.get("day") != tag:
            e["day"], e["day_count"] = tag, 0
        if int(e.get("day_count", 0)) >= RUNDEN_JE_INSTALL_TAG:
            raise SubmitError(429, "Already %d laps submitted today -- try again tomorrow."
                                   % RUNDEN_JE_INSTALL_TAG)
        e["day_count"] = int(e.get("day_count", 0)) + 1
        save_keys(data, keys_path)


def _entpacken(gepackt: bytes) -> bytes:
    """gzip entpacken, aber nie ueber MAX_TELE_ENTPACKT hinaus."""
    d = zlib.decompressobj(16 + zlib.MAX_WBITS)
    teile, summe = [], 0
    rest = gepackt
    while rest:
        stueck = d.decompress(rest, 1 << 20)
        summe += len(stueck)
        if summe > MAX_TELE_ENTPACKT:
            raise SubmitError(413, "The telemetry would be larger than %d MB unpacked."
                                   % (MAX_TELE_ENTPACKT // (1024 * 1024)))
        teile.append(stueck)
        rest = d.unconsumed_tail
        if d.eof:
            break
    return b"".join(teile)


def pruefe_telemetrie(tele, runde: dict):
    """Die volle Telemetrie einer Einreichung pruefen.

    Gibt (gepackte Bytes, Kurzinfo, Auffaelligkeiten) zurueck. Fehlt sie, ist das
    kein Grund zur Ablehnung -- aeltere Fassungen der App schicken sie nicht --,
    aber die Runde traegt dann einen Hinweis. Ist sie DA und kaputt oder passt
    nicht zur Runde, wird abgelehnt: dann stimmt an der Einreichung etwas nicht.
    """
    if tele is None:
        return None, None, ["no full telemetry"]
    if not isinstance(tele, dict) or tele.get("encoding") != "gzip+base64" \
            or not isinstance(tele.get("data"), str):
        raise SubmitError(400, "'telemetry' must be {encoding: gzip+base64, data}.")
    if len(tele["data"]) > MAX_TELE_GEPACKT * 4 // 3 + 4:
        raise SubmitError(413, "The packed telemetry is larger than %d MB."
                               % (MAX_TELE_GEPACKT // (1024 * 1024)))
    try:
        gepackt = base64.b64decode(tele["data"], validate=True)
    except (binascii.Error, ValueError):
        raise SubmitError(400, "The telemetry is not valid Base64.") from None
    try:
        roh = _entpacken(gepackt)
        spur = json.loads(roh.decode("utf-8"))
    except SubmitError:
        raise
    except Exception:
        raise SubmitError(400, "The telemetry is not gzipped JSON.") from None
    if not isinstance(spur, dict):
        raise SubmitError(400, "The telemetry must be an object.")
    spalten, daten = spur.get("columns"), spur.get("data")
    if not isinstance(spalten, list) or not 1 <= len(spalten) <= MAX_TELE_SPALTEN \
            or not all(isinstance(c, str) and _FELDNAME.match(c) for c in spalten):
        raise SubmitError(400, "The telemetry columns are invalid.")
    if not isinstance(daten, list) or not 10 <= len(daten) <= MAX_TELE_ZEILEN:
        raise SubmitError(422, "The telemetry has %s rows -- expected 10 to %d."
                               % (len(daten) if isinstance(daten, list) else "no",
                                  MAX_TELE_ZEILEN))
    breite = len(spalten)
    for i, zeile in enumerate(daten):
        if not isinstance(zeile, list) or len(zeile) != breite:
            raise SubmitError(400, "Telemetry row %d does not have %d values." % (i, breite))
        if not all(isinstance(v, (int, float)) and not isinstance(v, bool)
                   and math.isfinite(v) for v in zeile):
            raise SubmitError(400, "Telemetry row %d contains a value that is not a number." % i)
    # Sie muss zu DIESER Runde gehoeren: ihre Uhr laeuft vorwaerts und endet bei der
    # Rundenzeit -- sonst laege die Telemetrie einer anderen Fahrt unter der Zeit.
    if "t" in spalten:
        k = spalten.index("t")
        letzte = -1.0
        for i, zeile in enumerate(daten):
            if zeile[k] < letzte - 1e-6:
                raise SubmitError(422, "The telemetry clock runs backwards at row %d." % i)
            letzte = zeile[k]
        sekunden = float(runde.get("lapSeconds") or 0)
        if sekunden > 0 and abs(letzte - sekunden) > max(2.0, 0.05 * sekunden):
            raise SubmitError(422, "The telemetry ends at %.1f s, the lap at %.1f s."
                                   % (letzte, sekunden))
    info = {"rows": len(daten), "columns": breite, "bytes": len(gepackt),
            "format": str(tele.get("format") or "")[:40]}
    return gepackt, info, []


def ohne_telemetrie(datensatz: dict) -> dict:
    """Eine Runde fuer Listen: alles ausser den Messpunkten.

    Die Liste ist oeffentlich und wurde mit jeder Runde samt Telemetrie
    ausgeliefert -- Megabytes je Runde, an jeden, bei jedem Aufruf.
    """
    d = {k: v for k, v in datensatz.items() if k != "lap"}
    lap = dict(datensatz.get("lap") or {})
    proben = lap.pop("samples", None)
    lap["samplesCount"] = len(proben) if isinstance(proben, list) else 0
    d["lap"] = lap
    return d


def lap_id(install_id: str, runde: dict) -> str:
    """Eine Kennung, die aus der Runde selbst folgt.

    Damit ist dieselbe Runde zweimal eingereicht dieselbe Datei und nicht zwei --
    ein erneuter Versuch nach einem Abbruch soll keine Doppelung erzeugen.
    """
    roh = "%s|%s|%s|%s|%s" % (
        install_id, runde.get("course"), runde.get("lapSeconds"),
        runde.get("carOrdinal"), runde.get("recordedAt"))
    return hashlib.sha256(roh.encode("utf-8")).hexdigest()[:20]


def store(install_id: str, eintrag: dict, runde: dict, auffaellig: list,
          root: Path | None = None, keys_path: Path | None = None,
          now: float | None = None, volle_spur: bytes | None = None,
          spur_info: dict | None = None) -> dict:
    root = root or LAPS_DIR
    root.mkdir(parents=True, exist_ok=True)
    kennung = lap_id(install_id, runde)
    datensatz = {
        "id": kennung,
        "install_id": install_id,
        "gamertag": eintrag.get("gamertag"),
        "received": datetime.fromtimestamp(now or time.time(), timezone.utc)
                            .isoformat(timespec="seconds"),
        "flags": auffaellig,
        # Sichtbar, aber hinter einem Filter: die Seite zeigt eingereichte Runden
        # nur, wenn danach gefragt wird. Eine fremde Zeit soll die eigene Wertung
        # nicht stillschweigend veraendern.
        "hidden": False,
        "lap": runde,
    }
    if volle_spur is not None:
        # Die volle Spur DANEBEN, wie bei der App: die Rundendatei lesen alle
        # Listen, und die sollen keine Megabytes durch den Parser schieben.
        ziel = root / (kennung + ".tele.gz")
        tmp = ziel.with_suffix(".gz.tmp")
        tmp.write_bytes(volle_spur)
        os.replace(tmp, ziel)
        datensatz["fullTelemetry"] = spur_info or {}
    _atomar_schreiben(root / (kennung + ".json"),
                      json.dumps(datensatz, ensure_ascii=False, indent=1))

    with _SCHLOSS:
        data = load_keys(keys_path)
        if install_id in data["installs"]:
            data["installs"][install_id]["laps"] = \
                int(data["installs"][install_id].get("laps", 0)) + 1
            # Der Zeitpunkt, an dem die Frist fuer den Hardware-Hash neu beginnt.
            data["installs"][install_id]["last_seen"] = datensatz["received"]
            save_keys(data, keys_path)
    return datensatz


def list_laps(root: Path | None = None, include_hidden: bool = False) -> list:
    root = root or LAPS_DIR
    if not root.exists():
        return []
    raus = []
    for p in sorted(root.glob("*.json")):
        try:
            d = json.loads(p.read_text(encoding="utf-8-sig"))
        except (OSError, ValueError):
            continue
        if d.get("hidden") and not include_hidden:
            continue
        raus.append(d)
    return raus


def get_lap(kennung: str, root: Path | None = None) -> dict:
    """Eine Runde vollstaendig, MIT den Messpunkten -- fuer die Verwaltung.

    Die Listen lassen die Messpunkte weg (ohne_telemetrie); wer eine verdaechtige
    Zeit beurteilen will, braucht sie aber ganz. install_id bleibt draussen wie
    ueberall, wo etwas die Seite verlaesst.
    """
    root = root or LAPS_DIR
    # Die Kennung wird zum Dateinamen -- nur, was lap_id() selbst erzeugt.
    if not re.match(r"^[0-9a-f]{20}$", kennung or ""):
        raise SubmitError(400, "Not a valid ID.")
    p = root / (kennung + ".json")
    if not p.exists():
        raise SubmitError(404, "No lap with the ID %r." % kennung)
    d = json.loads(p.read_text(encoding="utf-8-sig"))
    return {k: v for k, v in d.items() if k != "install_id"}


def gruppe_von(runde: dict) -> tuple:
    """Auto, Strecke und Klasse -- dieselbe Einteilung wie der Abgleich mit der Bestenliste."""
    strecke = " ".join(str(runde.get("track") or runde.get("course") or "").strip().lower().split())
    return (strecke, str(runde.get("carClass")), str(runde.get("carOrdinal")))


def _mensch(install_id: str, installs: dict) -> str:
    """Wer eine Runde gefahren hat: die (gepfefferte) Hardware-Kennung der Installation.

    Eine Neuinstallation bekommt eine neue install_id, aber dieselbe Hardware -- sie
    ist derselbe Mensch. Ist die Installation nicht mehr bekannt (aufgeraeumt), zaehlt
    ihre Kennung allein.
    """
    hw = (installs.get(install_id) or {}).get("hw")
    return "hw:" + hw if hw else "install:" + str(install_id)


def nur_die_besten(gruppe: tuple, root: Path | None = None,
                   keys_path: Path | None = None) -> list:
    """Eine Gruppe auf die BEHALTEN_JE_GRUPPE schnellsten Runden stutzen, je Mensch eine.

    Gibt die Kennungen der entfernten Runden zurueck. Ausgeblendete Runden zaehlen
    nicht mit und werden nie entfernt: sie sind der Beleg dafuer, WAS ausgeblendet
    wurde (dieselbe Regel wie beim Ausblenden selbst). Entfernt wird mit der
    Rundendatei auch ihre volle Telemetrie.
    """
    root = root or LAPS_DIR
    with _SCHLOSS:
        installs = load_keys(keys_path).get("installs", {})
        sichtbar = [d for d in list_laps(root)
                    if d.get("id") and gruppe_von(d.get("lap") or {}) == gruppe]
        sichtbar.sort(key=lambda d: (float((d.get("lap") or {}).get("lapSeconds") or 9e9),
                                     str(d.get("received") or "")))
        gesehen, behalten, weg = set(), 0, []
        for d in sichtbar:
            wer = _mensch(d.get("install_id") or "", installs)
            if wer in gesehen or behalten >= BEHALTEN_JE_GRUPPE:
                weg.append(d["id"])
                continue
            gesehen.add(wer)
            behalten += 1
        for kennung in weg:
            if not re.match(r"^[0-9a-f]{20}$", kennung or ""):
                continue
            for datei in (root / (kennung + ".json"), root / (kennung + ".tele.gz")):
                try:
                    datei.unlink()
                except FileNotFoundError:
                    pass
        return weg


def get_full_telemetry(kennung: str, root: Path | None = None) -> dict:
    """Die volle Telemetrie einer Runde, entpackt -- fuer die Verwaltung."""
    root = root or LAPS_DIR
    if not re.match(r"^[0-9a-f]{20}$", kennung or ""):
        raise SubmitError(400, "Not a valid ID.")
    p = root / (kennung + ".tele.gz")
    if not p.exists():
        raise SubmitError(404, "There is no full telemetry for this lap.")
    return json.loads(gzip.decompress(p.read_bytes()).decode("utf-8"))


def set_hidden(kennung: str, hidden: bool, grund: str = "",
               root: Path | None = None) -> dict:
    """Eine Runde aus- oder wieder einblenden.

    AUSBLENDEN UND NICHT LOESCHEN -- dieselbe Regel wie bei den Scans. Was heute
    falsch aussieht, ist morgen vielleicht der einzige Beleg dafuer, WAS schiefging;
    und wer eine Runde faelschlich ausgeblendet hat, soll das zuruecknehmen koennen,
    ohne den Einreichenden um eine erneute Fahrt bitten zu muessen.

    Die naechste Zeit rueckt dadurch von selbst auf: die Wertung baut sich aus den
    sichtbaren Runden, eine ausgeblendete ist fuer sie nicht da. Ein eigener
    "nachruecken"-Schritt waere ein zweiter Ort, an dem dieselbe Entscheidung faellt.
    """
    root = root or LAPS_DIR
    # Die Kennung wird zum Dateinamen -- nur, was lap_id() selbst erzeugt.
    if not re.match(r"^[0-9a-f]{20}$", kennung or ""):
        raise SubmitError(400, "Not a valid ID.")
    p = root / (kennung + ".json")
    if not p.exists():
        raise SubmitError(404, "No lap with the ID %r." % kennung)
    d = json.loads(p.read_text(encoding="utf-8-sig"))
    d["hidden"] = bool(hidden)
    d["hidden_reason"] = _saubere_zeichen(grund, 300)
    d["hidden_at"] = datetime.now(timezone.utc).isoformat(timespec="seconds")
    _atomar_schreiben(p, json.dumps(d, ensure_ascii=False, indent=1))
    return ohne_telemetrie(d)


def set_banned(install_id: str, banned: bool, grund: str = "",
               keys_path: Path | None = None,
               root: Path | None = None, hide_laps: bool = True) -> dict:
    """Eine Installation sperren oder wieder freigeben.

    Beim Sperren verschwinden die Runden dieser Installation mit -- sonst haette
    eine Sperre keine Wirkung auf das, was schon auf der Seite steht. Beim
    Entsperren kommen sie wieder: eine zurueckgenommene Sperre soll nichts
    zuruecklassen.
    """
    with _SCHLOSS:
        data = load_keys(keys_path)
        eintrag = data["installs"].get(install_id)
        if not eintrag:
            raise SubmitError(404, "No installation with the ID %r." % install_id)
        eintrag["banned"] = bool(banned)
        eintrag["ban_reason"] = _saubere_zeichen(grund, 300)
        eintrag["ban_changed"] = datetime.now(timezone.utc).isoformat(timespec="seconds")
        save_keys(data, keys_path)

    betroffen = 0
    if hide_laps:
        for runde in list_laps(root, include_hidden=True):
            if runde.get("install_id") != install_id:
                continue
            grund_da = runde.get("hidden_reason") or ""
            # Eine Runde, die AUS EINEM ANDEREN GRUND ausgeblendet wurde, bleibt es
            # auch nach dem Entsperren. Sonst hoebe eine Entsperrung stillschweigend
            # ein Urteil auf, das mit ihr nichts zu tun hat.
            if not banned and grund_da.startswith("gesperrt:"):
                set_hidden(runde["id"], False, "", root)
                betroffen += 1
            elif banned and not runde.get("hidden"):
                set_hidden(runde["id"], True,
                           "gesperrt: " + (grund or "ohne Angabe"), root)
                betroffen += 1
    return {"install_id": install_id, "banned": bool(banned),
            "laps_touched": betroffen, "gamertag": eintrag.get("gamertag")}


def aufraeumen(now: float | None = None, keys_path: Path | None = None,
               dry_run: bool = False) -> dict:
    """Kennungen entfernen, die seit der Frist nichts mehr getan haben.

    ## Was genau geloescht wird

    Der ganze Eintrag: Geheimnis, Hardware-Hash, Gamertag. Danach ist die
    Installation dem Server unbekannt; sie kann sich neu anmelden und faengt bei
    null an. Die EINGEREICHTEN RUNDEN bleiben -- sie sind der Zweck der Sache und
    tragen den Gamertag, nicht den Hash.

    ## Warum "ohne Einreichung" und nicht "ohne Anmeldung"

    Wer die App installiert und nie etwas einreicht, hinterlaesst nichts, was
    jemandem nuetzt -- also auch keinen Grund, seinen Hash zu behalten. Wer regelmaessig
    einreicht, ist aktiv, und seine Sperrbarkeit muss bestehen bleiben.

    ## Warum das hier steht und nicht in einem Wartungsskript allein

    Ein Skript, das jemand von Hand starten muss, laeuft nie. Diese Funktion wird
    bei jeder Anmeldung mitgerufen -- der Moment, in dem die Datei ohnehin
    geschrieben wird. Sie ist billig: die Datei hat so viele Eintraege wie es
    Installationen gibt, nicht wie es Runden gibt.
    """
    jetzt = now or time.time()
    data = load_keys(keys_path)
    weg, bleibt = [], {}
    for kennung, e in data.get("installs", {}).items():
        letzte = e.get("last_seen") or e.get("created") or ""
        try:
            stand = datetime.fromisoformat(letzte).timestamp()
        except (TypeError, ValueError):
            # Ohne lesbares Datum bleibt der Eintrag. Etwas zu loeschen, weil man
            # sein Alter nicht kennt, ist die falsche Richtung.
            bleibt[kennung] = e
            continue
        frist = (HASH_FRIST_TAGE_GESPERRT if e.get("banned") else HASH_FRIST_TAGE)
        if jetzt - stand > frist * 86400:
            weg.append({"install_id": kennung, "gamertag": e.get("gamertag"),
                        "last_seen": letzte, "banned": bool(e.get("banned"))})
        else:
            bleibt[kennung] = e

    if weg and not dry_run:
        data["installs"] = bleibt
        data["last_cleanup"] = datetime.fromtimestamp(jetzt, timezone.utc) \
                                       .isoformat(timespec="seconds")
        save_keys(data, keys_path)
    return {"entfernt": len(weg), "geblieben": len(bleibt), "welche": weg}


def installs(keys_path: Path | None = None) -> list:
    """Die angemeldeten Installationen -- OHNE die Geheimnisse.

    Die Admin-Ansicht braucht Gamertag, Anzahl und Sperrzustand. Das Geheimnis
    braucht sie nie, und was nicht herausgegeben wird, kann auch nicht
    versehentlich in einem Protokoll landen.
    """
    data = load_keys(keys_path)
    namen = spielernamen(keys_path)
    raus = []
    for kennung, e in sorted(data["installs"].items()):
        raus.append({
            "install_id": kennung,
            "gamertag": e.get("gamertag"),
            # So, wie die Seite ihn zeigt: der letzte Name dieser Maschine, oder
            # ein vorlaeufiger (siehe spielernamen).
            "display_name": namen.get(kennung, ("", True))[0],
            "name_temporary": namen.get(kennung, ("", True))[1],
            "created": e.get("created"),
            "laps": e.get("laps", 0),
            "banned": bool(e.get("banned")),
            "ban_reason": e.get("ban_reason", ""),
            "ban_changed": e.get("ban_changed", ""),
            "banned_auto": bool(e.get("banned_auto")),
            "strikes_24h": sum(1 for t in e.get("strikes", [])
                               if time.time() - float(t) < STRAFPUNKTE_FENSTER),
            "last_strike": e.get("last_strike", ""),
            # Nur die ersten Zeichen: genug, um zwei Anmeldungen derselben Maschine
            # zu erkennen, zu wenig, um damit sonst etwas anzufangen.
            "hw_prefix": (e.get("hw") or "")[:12],
        })
    return raus
