"""Die HTTP-Schnittstelle neben der Seite: Zusammenfassung, Datensatz, Abgabe, Admin.

Eingehaengt von `serve_analytics.py`. Hier steht die Logik, dort das Netzwerk -- so
laesst sich jede Antwort pruefen, ohne einen Port zu oeffnen.

    GET  /api/summary          kleine Uebersicht: Version, Stand, Boards, Beitragende
    GET  /api/dataset          der ganze Datensatz (laps.json), fuer die App
    POST /api/contribute       eine signierte Abgabe einlassen
    GET  /api/admin/runs       alle Laeufe mit ihrem Sichtbarkeitszustand
    POST /api/admin/visibility einen Lauf aus- oder wieder einblenden

## Wie sich jemand ausweist

**Abgaben** tragen ihre Beglaubigung in sich: die ZIP ist mit dem Geheimnis des
Beitragenden signiert (siehe `contrib_format`). Der Server schaut das Geheimnis zu
dem Namen im Manifest nach und rechnet nach. Das Geheimnis wird nie uebertragen.

**Admin-Aufrufe** signieren `METHODE\\nPFAD\\nZEITSTEMPEL\\nSHA256(Rumpf)` mit dem
Admin-Geheimnis und schicken

    X-Forza-Timestamp: <Unixzeit in Sekunden>
    X-Forza-Signature: <hex>

Auch hier wandert das Geheimnis nicht mit. Der Zeitstempel darf hoechstens
`CLOCK_SKEW` Sekunden abweichen, sonst laesst sich ein mitgeschnittener Aufruf
spaeter wiederholen. Das ist noetig, weil ueber einfaches HTTP jeder Aufruf mitlesbar
ist -- verborgen wird hier nichts, aber niemand kann sich als Admin ausgeben und
nichts laesst sich unterwegs veraendern.

## Was diese Schnittstelle bewusst NICHT kann

Kein Loeschen. Ein Lauf laesst sich ausblenden, nie entfernen -- was heute falsch
aussieht, ist morgen vielleicht der einzige Beleg dafuer, WAS schiefging.
"""

from __future__ import annotations

import hashlib
import hmac
import json
import os
import re
import secrets
import sys
import threading
import time
from datetime import datetime
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import contrib_format as fmt  # noqa: E402
from import_contrib import import_archive  # noqa: E402
import ip_bans  # noqa: E402
import lap_submissions as laps  # noqa: E402
import race_submissions as rennen_api  # noqa: E402
import leaderboard_check as bestenliste  # noqa: E402
import usage  # noqa: E402

WORKSPACE = Path(__file__).resolve().parent.parent
KEYS_FILE = WORKSPACE / "config/contrib_keys.json"
VISIBILITY_FILE = WORKSPACE / "config/dataset_visibility.json"
CONTRIB_ROOT = WORKSPACE / "data/memory_scans/contrib"
SWEEP_ROOT = WORKSPACE / "data/memory_scans/full_sweep"
DATASET_FILE = WORKSPACE / "data/analytics/laps.json"
DIST_DIR = WORKSPACE / "dist"

# WAS OFFEN IST, STEHT IN EINER DATEI -- UND IM ZWEIFEL IST ES ZU.
#
# Am 2026-09-13 ging die Runden-Einreichung oeffentlich, weil die Pipeline Code und
# Daten als eine Einheit schickt: ich wollte den frischen Datensatz hinueberbringen
# und habe eine Schnittstelle mit freigeschaltet, von der vereinbart war, dass sie
# noch NICHT offen sein soll.
#
# Ein Schalter im Code haette das nicht verhindert -- der waere mitgereist. Darum
# eine eigene Datei, die die Pipeline NICHT anfasst: sie muss dort drueben von Hand
# gesetzt werden, und ein Deploy kann sie nicht umlegen.
#
# Vorgabe ist AUS. Eine fehlende Datei bedeutet zu, nicht offen -- sonst entscheidet
# ein Versehen zugunsten der Oeffnung, und das ist die falsche Richtung.
FEATURES_FILE = WORKSPACE / "config" / "features.json"


def feature_an(name: str, path: Path | None = None) -> bool:
    """Ist dieser Teil freigeschaltet? Im Zweifel nein."""
    try:
        d = json.loads((path or FEATURES_FILE).read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        return False
    return bool(d.get(name) is True)

# Wie weit die Uhr des Aufrufers abweichen darf. Fuenf Minuten sind grosszuegig genug
# fuer eine Uhr, die nie gestellt wurde, und kurz genug, dass ein mitgeschnittener
# Admin-Aufruf nicht am naechsten Tag noch gilt.
CLOCK_SKEW = 300
MAX_UPLOAD_BYTES = 64 * 1024 * 1024
# 192 Zufallsbytes ergeben in der URL-sicheren Schreibweise genau 256 Zeichen.
GATE_BYTES = 192


class ApiError(Exception):
    def __init__(self, status: int, message: str):
        super().__init__(message)
        self.status = status
        self.message = message


def load_keys(path: Path | None = None) -> dict:
    path = path or KEYS_FILE
    if not path.exists():
        return {"keys": {}, "admin": ""}
    try:
        return json.loads(path.read_text(encoding="utf-8-sig"))
    except Exception:
        return {"keys": {}, "admin": ""}


def admin_signature(secret: str, method: str, path: str, timestamp: str,
                    body: bytes) -> str:
    message = "\n".join([method.upper(), path, str(timestamp),
                         hashlib.sha256(body or b"").hexdigest()])
    return hmac.new(secret.encode("utf-8"), message.encode("utf-8"),
                    hashlib.sha256).hexdigest()


# Fehlversuche je Maschine. Auf Platte, nicht im Speicher: ein Neustart des Servers
# darf keine Sperre aufheben, sonst ist sie nur eine Bitte.
UPLOAD_ATTEMPTS_FILE = WORKSPACE / "data/runtime/upload_attempts.json"
UPLOAD_MAX_FAILURES = 10
UPLOAD_BLOCK_SECONDS = 24 * 3600


def _attempts_load(path: Path | None = None) -> dict:
    path = path or UPLOAD_ATTEMPTS_FILE
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except Exception:
        return {}


def _attempts_save(data: dict, path: Path | None = None) -> None:
    path = path or UPLOAD_ATTEMPTS_FILE
    try:
        _atomar(path, json.dumps(data, indent=1))
    except OSError:
        # Kein Grund, eine Abgabe scheitern zu lassen, weil das Zaehlwerk nicht
        # schreiben kann -- aber es steht dann auch nichts im Weg. Deshalb nur hier
        # schlucken und nicht beim Lesen.
        pass


def upload_block_seconds(client: str, now: float | None = None,
                         path: Path | None = None) -> int:
    """Wie lange diese Maschine noch gesperrt ist. 0 heisst: darf.

    Gezaehlt werden **falsche Passwoerter**, nicht kaputte Pakete. Ein Paket, das
    nicht durch die Pruefung kommt, ist der normale Fehlerfall eines ehrlichen
    Beitragenden; ein falsches Passwort zehnmal hintereinander ist es nicht.
    """
    if not client:
        return 0
    now = time.time() if now is None else now
    entry = _attempts_load(path).get(client) or {}
    until = float(entry.get("blocked_until") or 0)
    return int(max(0, until - now))


def note_upload_failure(client: str, now: float | None = None,
                        path: Path | None = None) -> int:
    """Einen Fehlversuch vermerken und sagen, wie viele noch bleiben."""
    if not client:
        return UPLOAD_MAX_FAILURES
    now = time.time() if now is None else now
    data = _attempts_load(path)
    entry = data.get(client) or {"failures": 0}
    entry["failures"] = int(entry.get("failures", 0)) + 1
    entry["last"] = now
    if entry["failures"] >= UPLOAD_MAX_FAILURES:
        entry["blocked_until"] = now + UPLOAD_BLOCK_SECONDS
    data[client] = entry
    _attempts_save(data, path)
    return max(0, UPLOAD_MAX_FAILURES - entry["failures"])


def clear_upload_failures(client: str, path: Path | None = None) -> None:
    """Nach einer erfolgreichen Abgabe: Zaehler zurueck auf null."""
    if not client:
        return
    data = _attempts_load(path)
    if client in data:
        data.pop(client, None)
        _attempts_save(data, path)


def require_upload_password(keys: dict, headers, client: str,
                            now: float | None = None,
                            attempts_file: Path | None = None) -> None:
    """Das getippte Zugangspasswort pruefen -- zusaetzlich zur Unterschrift im Paket.

    WARUM ZUSAETZLICH: `gate.txt` liegt IM Paket. Wer eine fremde ZIP in die Finger
    bekommt, hat damit auch das Passwort -- die Unterschriften allein schuetzen also
    nur davor, dass jemand ein Paket selbst BAUT, nicht davor, dass er ein fremdes
    weiterschickt. Seit der Bereich oeffentlich erreichbar ist, gehoert darum ein
    zweiter, GETIPPTER Faktor dazu, der nirgends mitgeliefert wird.
    """
    secret = (keys or {}).get("upload_password") or ""
    if not secret:
        raise ApiError(503, "No upload password has been set. "
                            "Create one with: python server/contrib_keys.py gate")
    left = upload_block_seconds(client, now, attempts_file)
    if left > 0:
        raise ApiError(429, f"Too many failed attempts. This machine may try again in "
                            f"{left // 3600 + 1} hour(s).")
    given = str(headers.get("X-Forza-Upload-Password") or "").strip()
    if not given:
        raise ApiError(401, "Upload password missing (header "
                            "X-Forza-Upload-Password).")
    if not hmac.compare_digest(given, secret):
        remaining = note_upload_failure(client, now, attempts_file)
        if remaining <= 0:
            raise ApiError(429, "Wrong upload password. Ten failed attempts reached -- "
                                "this machine is blocked for 24 hours.")
        raise ApiError(401, f"Wrong upload password. {remaining} attempt(s) left, "
                            f"then this machine is blocked for 24 hours.")


_PAKET_HASH: dict = {}


def haptics_info(dist: Path | None = None) -> dict:
    """Was die aktuelle App-Fassung ist -- fuer den Selbstaktualisierer.

    Die Pruefsumme wird GEMERKT, nach Pfad, Groesse und Aenderungszeit. Sie ueber
    66 MB zu rechnen dauert eine knappe halbe Sekunde; bei einem Aufruf je
    Programmstart je Nutzer waere das egal, aber ein Browser, der die Seite offen
    laesst, fragt oefter. Gemerkt wird nur, solange die Datei sich nicht ruehrt --
    ein neues Paket hat eine andere Groesse oder Zeit und rechnet neu.

    WARUM EINE PRUEFSUMME UND KEIN DATUM: das Datum steht im Namen und taugt zum
    Anzeigen, aber nicht zum Entscheiden. Zwei Pakete desselben Tages sind
    verschieden, und ein zurueckgenommenes Paket traegt ein aelteres Datum als das,
    was der Nutzer schon hat. Die Pruefsumme beantwortet "ist das ein anderes
    Paket als meines" ohne solche Sonderfaelle -- und sie beantwortet zugleich
    "ist der Download heil angekommen".
    """
    paket = haptics_package(dist)
    if paket is None:
        raise ApiError(503, "No app has been built yet.")
    st = paket.stat()

    # DER ZETTEL DES PAKETBAUERS HAT VORRANG.
    #
    # Er traegt die Kennung, die auch IM Paket liegt (app.meta.json) -- damit
    # vergleicht die App ihre eigene Fassung mit derselben Zahl. Aus Dateidatum und
    # Groesse liesse sich das nicht herleiten: eine Uebertragung darf die
    # Aenderungszeit veraendern, und dann haette dieselbe Datei ploetzlich eine
    # andere "Version". Nur wenn kein Zettel da liegt, wird geschaetzt.
    zettel = paket.with_suffix(".zip.meta.json")
    if zettel.exists():
        try:
            notiz = json.loads(zettel.read_text(encoding="utf-8-sig"))
            if notiz.get("build") and notiz.get("sha256"):
                notiz["url"] = "/download/haptics"
                notiz.setdefault("bytes", st.st_size)
                return notiz
        except (OSError, ValueError):
            pass

    schluessel = (str(paket), st.st_size, int(st.st_mtime))
    if _PAKET_HASH.get("schluessel") != schluessel:
        h = hashlib.sha256()
        with paket.open("rb") as f:
            for stueck in iter(lambda: f.read(1 << 20), b""):
                h.update(stueck)
        _PAKET_HASH["schluessel"] = schluessel
        _PAKET_HASH["hex"] = h.hexdigest()
    gebaut = datetime.fromtimestamp(st.st_mtime).astimezone()
    return {
        "name": f"fh-companion-{gebaut:%Y%m%d}.zip",
        "built_at": gebaut.isoformat(timespec="seconds"),
        "bytes": st.st_size,
        "sha256": _PAKET_HASH["hex"],
        "url": "/download/haptics",
    }


def paketbauer():
    """Den Bauer des Beitragenden-Werkzeugs holen -- oder None auf dem Spiegel.

    Er lebt in scripts/ und nicht hier, weil er den SCANNER einschnuert: rund zehn
    OCR- und Fernsteuerungsdateien, die nur auf der Maschine mit dem Spiel liegen.
    Auf dem GNAS ist von alldem nichts da, ein Bau koennte dort also gar nicht
    gelingen.

    Darum die Rueckgabe None statt eines ImportError: der Spiegel soll sagen, WO
    ein Paket entsteht, statt mit einer Rueckverfolgung abzubrechen, aus der das
    niemand ablesen kann.
    """
    pfad = str(WORKSPACE / "scripts")
    if pfad not in sys.path:
        sys.path.insert(0, pfad)
    try:
        import build_contrib_package as packer
    except ImportError:
        return None
    return packer


def guide_video(name: str, dist: Path | None = None) -> Path | None:
    """Ein Anleitungsvideo oder sein Standbild aus dist/tutorials/ -- nur die bekannten Namen."""
    import re
    if not re.match(r"^fh-companion-setup-(pc|xbox)\.(mp4|jpg)$", name or ""):
        return None
    datei = (dist or DIST_DIR) / "tutorials" / name
    return datei if datei.is_file() else None


def haptics_package(dist: Path | None = None) -> Path | None:
    """Die neueste gebaute Haptik-App, oder None, wenn noch keine gebaut wurde.

    Gibt einen PFAD zurueck und keine Bytes: das Paket ist rund 64 MB, und der
    Server soll es vom Datentraeger weiterreichen koennen, statt es je Abruf
    vollstaendig in den Speicher zu holen.
    """
    folder = dist or DIST_DIR
    # Seit 2026-09-26 "fh-companion"; der alte Name gilt nur, solange noch kein
    # Paket unter dem neuen liegt.
    for vorsilbe in ("fh-companion", "forza-grip-haptics"):
        latest = folder / f"{vorsilbe}-latest.zip"
        if latest.exists():
            return latest
    for vorsilbe in ("fh-companion", "forza-grip-haptics"):
        dated = sorted(p for p in folder.glob(f"{vorsilbe}-*.zip") if not p.name.endswith("-latest.zip"))
        if dated:
            return dated[-1]
    return None


# BENUTZTE ADMIN-UNTERSCHRIFTEN. Der Zeitstempel allein liess ein mitgeschnittenes
# Kommando fuenf Minuten lang beliebig oft wiederholen -- etwa "Zugangspasswort
# erneuern", dessen Antwort das NEUE Passwort traegt. Eine Unterschrift gilt jetzt
# genau einmal; gemerkt wird sie so lange, wie ihr Zeitstempel ueberhaupt gilt.
_ADMIN_SCHLOSS = threading.Lock()
_ADMIN_BENUTZT: dict = {}


def _atomar(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(path.suffix + ".tmp")
    tmp.write_text(text, encoding="utf-8")
    os.replace(tmp, path)


def require_admin(keys: dict, method: str, path: str, headers, body: bytes,
                  now: float | None = None) -> None:
    secret = (keys or {}).get("admin") or ""
    if not secret:
        raise ApiError(503, "No admin secret has been set. "
                            "Create one with: python server/contrib_keys.py admin")
    stamp = str(headers.get("X-Forza-Timestamp") or "")
    signature = str(headers.get("X-Forza-Signature") or "")
    if not stamp or not signature:
        raise ApiError(401, "X-Forza-Timestamp and X-Forza-Signature are missing")
    try:
        drift = abs((now if now is not None else time.time()) - float(stamp))
    except ValueError:
        raise ApiError(401, "X-Forza-Timestamp is not a number") from None
    if drift > CLOCK_SKEW:
        raise ApiError(401, f"Timestamp is off by {drift:.0f}s -- at most "
                            f"{CLOCK_SKEW}s. Set the clock and try again.")
    if not hmac.compare_digest(admin_signature(secret, method, path, stamp, body),
                               signature.strip()):
        raise ApiError(401, "Signature does not match")
    # NUR AENDERNDE AUFRUFE gelten genau einmal. Ein wiederholtes GET liest nur,
    # was der Mithoerer ohnehin schon mitgelesen hat -- und zwei gleiche GETs in
    # derselben Sekunde (zweimal "Reload") tragen dieselbe Unterschrift; sie
    # abzuweisen hiesse, die Verwaltungsseite mit einem Doppelklick zu brechen.
    if method.upper() == "GET":
        return
    jetzt = now if now is not None else time.time()
    with _ADMIN_SCHLOSS:
        for alt in [s for s, t in _ADMIN_BENUTZT.items() if jetzt - t > CLOCK_SKEW * 2]:
            _ADMIN_BENUTZT.pop(alt, None)
        if signature.strip() in _ADMIN_BENUTZT:
            raise ApiError(409, "This signed request was already used -- sign a new one.")
        _ADMIN_BENUTZT[signature.strip()] = jetzt


def load_visibility(path: Path | None = None) -> dict:
    path = path or VISIBILITY_FILE
    if not path.exists():
        return {"hidden": []}
    try:
        data = json.loads(path.read_text(encoding="utf-8-sig"))
    except Exception:
        return {"hidden": []}
    data.setdefault("hidden", [])
    return data


def save_visibility(data: dict, path: Path | None = None) -> None:
    path = path or VISIBILITY_FILE
    _atomar(path, json.dumps(data, indent=1, ensure_ascii=False))


def list_runs(sweep_root: Path | None = None, contrib_root: Path | None = None,
              visibility: Path | None = None) -> list[dict]:
    """Jeder Lauf, den es gibt, mit Herkunft und Sichtbarkeit -- fuer die Admin-Ansicht."""
    sweep_root = sweep_root or SWEEP_ROOT
    contrib_root = contrib_root or CONTRIB_ROOT
    hidden = {}
    for entry in load_visibility(visibility).get("hidden", []):
        if isinstance(entry, str):
            hidden[entry] = ""
        elif isinstance(entry, dict) and entry.get("run_id"):
            hidden[str(entry["run_id"])] = str(entry.get("reason") or "")

    found: list[dict] = []
    sources = [(sweep_root.glob("*/state.json"), "local")]
    if contrib_root.exists():
        sources.append((contrib_root.glob("*/*/state.json"), None))
    for paths, fixed_origin in sources:
        for state_path in paths:
            try:
                state = json.loads(state_path.read_text(encoding="utf-8-sig"))
            except Exception:
                continue
            run_id = state_path.parent.name
            found.append({
                "run_id": run_id,
                "by": fixed_origin or state_path.parent.parent.name,
                "track": state.get("track"),
                "class": (state.get("performance_class") or "?").upper(),
                "mode": state.get("rivals_mode"),
                "rows": int(state.get("rows_collected") or 0),
                "maxRank": int(state.get("maximum_rank") or 0),
                "status": state.get("status"),
                "endStatus": state.get("end_status"),
                "when": str(state.get("completed_at") or "")[:19],
                "hidden": run_id in hidden,
                "reason": hidden.get(run_id, ""),
            })
    found.sort(key=lambda entry: (entry["when"], entry["run_id"]), reverse=True)
    return found


def set_visibility(run_id: str, hidden: bool, reason: str = "",
                   path: Path | None = None) -> dict:
    fmt.check_run_id(run_id)
    data = load_visibility(path)
    entries = [e for e in data.get("hidden", [])
               if (e if isinstance(e, str) else e.get("run_id")) != run_id]
    if hidden:
        entries.append({
            "run_id": run_id,
            "reason": str(reason or "")[:500],
            "hidden_at": datetime.now().astimezone().isoformat(timespec="seconds"),
        })
    data["hidden"] = entries
    save_visibility(data, path)
    return {"run_id": run_id, "hidden": hidden, "reason": reason}


def _visibility_newer_than(dataset_file: Path, visibility_file: Path | None) -> bool:
    """Steht in der Sichtbarkeitsliste etwas, das der gebaute Datensatz noch nicht kennt?"""
    path = visibility_file or VISIBILITY_FILE
    try:
        return path.stat().st_mtime > dataset_file.stat().st_mtime
    except OSError:
        return False


def summary(dataset_file: Path | None = None,
            visibility_file: Path | None = None) -> dict:
    """Die kleine Uebersicht, die die App beim Start zieht.

    Klein gehalten, weil ihr einziger Zweck ist zu entscheiden, ob sich der grosse
    Datensatz zu laden lohnt: eine Version, ein Stand, und so viel Inhalt, dass ein
    Mensch sieht was er bekaeme.
    """
    dataset_file = dataset_file or DATASET_FILE
    if not dataset_file.exists():
        raise ApiError(503, "No dataset has been built yet.")
    # GEMERKT nach Pfad, Groesse und Aenderungszeit. Vorher las und parste jeder
    # Aufruf die 27 MB -- eine halbe Sekunde Rechenzeit fuer eine Anfrage, die
    # jeder Fremde beliebig oft stellen kann.
    st = dataset_file.stat()
    schluessel = (str(dataset_file), st.st_size, st.st_mtime_ns)
    if _SUMMARY_CACHE.get("schluessel") == schluessel:
        ergebnis = dict(_SUMMARY_CACHE["wert"])
        ergebnis["stale"] = _visibility_newer_than(dataset_file, visibility_file)
        return ergebnis
    raw = dataset_file.read_bytes()
    payload = json.loads(raw.decode("utf-8"))
    meta = payload.get("meta", {})
    tracks = payload.get("tracks", [])
    classes = payload.get("classes", [])
    boards = [{
        "track": tracks[board["t"]] if board.get("t", -1) < len(tracks) else "?",
        "class": classes[board["k"]] if board.get("k", -1) < len(classes) else "?",
        "rows": board.get("rows", 0),
        "valid": board.get("valid", 0),
        "maxRank": board.get("maxRank", 0),
        # `impl` ist die GESCHAETZTE Gesamtlaenge des Boards, `iest` das Verfahren.
        # Beide gehoeren zusammen: eine Schaetzung ohne die Angabe, wie sie zustande
        # kam, wird gelesen wie eine Zaehlung.
        "estimatedTotal": board.get("impl") or None,
        "estimator": board.get("iest") or "",
    } for board in payload.get("boards", [])]
    ergebnis = {
        # Die Version ist der Hash des Datensatzes selbst. Ein Zeitstempel wuerde
        # auch bei einem Bau ohne Aenderung springen und die App zu einem
        # ueberfluessigen Download bewegen.
        "version": hashlib.sha256(raw).hexdigest()[:16],
        "built_at": datetime.fromtimestamp(
            dataset_file.stat().st_mtime).astimezone().isoformat(timespec="seconds"),
        "bytes": len(raw),
        "boards": len(boards),
        "cars": meta.get("cars", 0),
        "namedCars": meta.get("named_cars", 0),
        "rawRows": meta.get("raw_rows", 0),
        "keptLaps": meta.get("kept_laps", 0),
        "hiddenRuns": meta.get("hidden_runs", 0),
        "contributors": meta.get("contributors", []),
        # Ein Ausschluss wirkt erst, wenn der Datensatz NEU GEBAUT ist -- und ein Bau
        # dauert eine Viertelstunde. Ohne diese Angabe laedt ein Werkzeug den alten
        # Stand und haelt ihn fuer den neuen, waehrend im Admin-Bereich laengst
        # "ausgeblendet" steht. Wer das sieht, kann warten.
        "stale": _visibility_newer_than(dataset_file, visibility_file),
        "table": sorted(boards, key=lambda b: (b["track"], b["class"])),
    }
    _SUMMARY_CACHE.clear()
    _SUMMARY_CACHE.update({"schluessel": schluessel, "wert": ergebnis})
    return ergebnis


_SUMMARY_CACHE: dict = {}


def lap_endpunkt(method: str, path: str, headers, body: bytes,
                 now: float | None = None, client: str = "", keys: dict | None = None):
    """Die Pfade rund um eingereichte Runden.

    Eine eigene Funktion und kein weiterer Block in `handle`: `handle` ist schon
    lang, und diese Pfade haben eine ANDERE Art sich auszuweisen -- nicht das
    Beitragenden-Geheimnis, sondern das der Installation. Zwei Ausweisverfahren im
    selben Rumpf zu mischen ist genau die Stelle, an der spaeter das falsche
    genommen wird.

    Die Fehler von `lap_submissions` werden hier in `ApiError` uebersetzt, damit der
    Server nur EINE Fehlerart kennt.
    """
    def as_json(status: int, value):
        return (status, "application/json; charset=utf-8",
                json.dumps(value, ensure_ascii=False, indent=1).encode("utf-8"))

    def rumpf() -> dict:
        if len(body or b"") > laps.MAX_BODY:
            raise ApiError(413, "%.1f MB, the limit is %.0f MB"
                                % (len(body) / 1e6, laps.MAX_BODY / 1e6))
        try:
            d = json.loads((body or b"{}").decode("utf-8"))
        except Exception:
            raise ApiError(400, "The request body is not JSON.") from None
        if not isinstance(d, dict):
            raise ApiError(400, "The request body must be a JSON object.")
        return d

    try:
        if method == "POST" and path == "/api/lap/register":
            d = rumpf()
            return as_json(200, laps.register(d.get("hardware", ""),
                                              d.get("gamertag", ""), now=now,
                                              client=client))

        if method == "POST" and path == "/api/lap/submit":
            try:
                install_id, eintrag = laps.verify(headers, method, path, body, now=now)
            except laps.SubmitError as e:
                # Eine falsche Unterschrift ist ein fehlgeschlagener Anmeldeversuch.
                if e.status == 401:
                    ip_bans.fehlversuch(client, "lap signature", now)
                raise
            anfrage = rumpf()
            runde = anfrage.get("lap")
            if not isinstance(runde, dict):
                laps.strafpunkt(install_id, "no lap object", now)
                raise ApiError(400, "'lap' is missing from the request body.")
            # DER SERVER PRUEFT SELBST -- auf Moeglichkeit UND darauf, ob die Runde
            # die Bestenliste wirklich schlaegt. Nichts davon wird der App geglaubt.
            try:
                auffaellig = laps.pruefe_runde(runde)
                # Die volle Telemetrie (seit 2026-09-28): geprueft wie die Runde selbst.
                volle_spur, spur_info, ohne_spur = laps.pruefe_telemetrie(
                    anfrage.get("telemetry"), runde)
                auffaellig = list(auffaellig) + ohne_spur
                runde = laps.saeubere_runde(runde)
                befund = bestenliste.pruefe(runde, DATASET_FILE, laps.list_laps())
            except laps.SubmitError as e:
                if e.status in (400, 413, 422):
                    laps.strafpunkt(install_id, e.message, now)
                raise
            except bestenliste.NichtSchneller as e:
                if e.status in (400, 422) or (e.status == 409
                                              and e.langsamer > laps.DEUTLICH_LANGSAMER):
                    laps.strafpunkt(install_id, e.message, now)
                raise ApiError(e.status, e.message) from None
            laps.tageskontingent(install_id, now=now)
            # Der Name kommt mit jeder (unterschriebenen) Einreichung -- freiwillig;
            # angezeigt wird er ueber mit_spielernamen, auch an frueheren Runden.
            laps.set_gamertag(install_id, anfrage.get("gamertag"), eintrag)
            if befund.get("newCar"):
                auffaellig = list(auffaellig) + ["car not on this leaderboard yet"]
            # Oeffentlich zum Herunterladen nur, wenn die App es sagt -- sie tut es erst
            # mit dem Hinweis, der es ankuendigt (Fassung 10). Siehe telemetrie_oeffentlich.
            abgelegt = laps.store(install_id, eintrag, runde, auffaellig, now=now,
                                  volle_spur=volle_spur, spur_info=spur_info,
                                  veroeffentlichen=anfrage.get("publishTelemetry") is True)
            # Je Auto, Strecke und Klasse nur die zehn schnellsten, je Mensch eine.
            entfernt = laps.nur_die_besten(laps.gruppe_von(runde))
            if abgelegt["id"] in entfernt:
                return as_json(200, {"ok": True, "id": abgelegt["id"], "kept": False,
                                     "flags": auffaellig,
                                     "note": ("Accepted, but not kept: you already have a "
                                              "faster lap here, or it is not among the ten "
                                              "fastest.")})
            return as_json(200, {"ok": True, "id": abgelegt["id"],
                                 "flags": auffaellig,
                                 # Ehrlich sagen, was noch aussteht: die Runde ist
                                 # angekommen, sie ist damit noch nicht geprueft.
                                 "note": ("Accepted. It appears behind the filter "
                                          "for submitted times.")})

        if method == "GET" and path == "/api/lap/list":
            return as_json(200, {"laps": [
                dict({k: v for k, v in laps.ohne_telemetrie(runde).items() if k != "install_id"},
                     telemetryDownload=laps.telemetrie_oeffentlich(runde))
                for runde in laps.mit_spielernamen(laps.list_laps())]})

        # DIE VOLLE TELEMETRIE ZUM HERUNTERLADEN (seit 2026-09-30), fuer jeden -- aber nur
        # die von Runden, die es erlauben (telemetrie_oeffentlich). Als CSV oder als die
        # gepackte Datei der App. Den Dateinamen setzt serve_analytics (download_name).
        spur = SPUR_PFAD.fullmatch(path)
        if method == "GET" and spur:
            gepackt, _ = laps.oeffentliche_spur(spur.group(1))
            if spur.group(2) == "csv":
                return (200, "text/csv; charset=utf-8", laps.spur_als_csv(gepackt))
            return (200, "application/gzip", gepackt)

        # --- ab hier nur mit Admin-Unterschrift ---
        # Dieselben Schluessel wie der Rest von handle(), nicht frisch von Platte:
        # sonst pruefte dieser Zweig gegen etwas anderes als alle uebrigen.
        keys = load_keys() if keys is None else keys

        if method == "GET" and path == "/api/admin/lap/list":
            # DIESELBE LISTE, ABER MIT DEN AUSGEBLENDETEN.
            #
            # Ohne sie kann die Verwaltungsseite eine ausgeblendete Runde nicht
            # mehr anzeigen -- und damit auch nicht wieder einblenden. "Loeschen"
            # waere dann endgueltig, obwohl set_hidden ausdruecklich umkehrbar
            # gebaut ist.
            #
            # install_id bleibt auch hier draussen: der Verwalter braucht sie
            # nicht, um eine Runde zu beurteilen, und was nicht hinausgeht, kann
            # nicht verloren gehen. Zum Sperren eines Kontos gibt es
            # /api/admin/installs.
            require_admin(keys, method, path, headers, body, now)
            return as_json(200, {"laps": [
                {k: v for k, v in laps.ohne_telemetrie(runde).items() if k != "install_id"}
                for runde in laps.mit_spielernamen(laps.list_laps(include_hidden=True))]})

        if method == "POST" and path == "/api/admin/lap/fulltelemetry":
            # Jedes Paket der Runde (die .tele.gz der App), entpackt.
            require_admin(keys, method, path, headers, body, now)
            return as_json(200, laps.get_full_telemetry(str(rumpf().get("id") or "")))

        if method == "POST" and path == "/api/admin/lap/telemetry":
            # Die ganze Runde samt Messpunkten, zum Herunterladen in der Verwaltung.
            # POST und unterschrieben wie alles hier: die Messpunkte sind das, was
            # die oeffentliche Liste absichtlich nicht herausgibt.
            require_admin(keys, method, path, headers, body, now)
            runde = laps.get_lap(str(rumpf().get("id") or ""))
            return as_json(200, laps.mit_spielernamen([runde])[0])

        if method == "POST" and path in ("/api/admin/lap/hide",
                                         "/api/admin/lap/show"):
            require_admin(keys, method, path, headers, body, now)
            d = rumpf()
            return as_json(200, laps.set_hidden(
                str(d.get("id") or ""), path.endswith("hide"),
                str(d.get("reason") or "")))

        if method == "POST" and path in ("/api/admin/lap/ban",
                                         "/api/admin/lap/unban"):
            require_admin(keys, method, path, headers, body, now)
            d = rumpf()
            wer = str(d.get("install_id") or "")
            # "Spieler sperren" an einer Runde: die Verwaltung kennt die install_id
            # nicht (sie geht nie hinaus) -- der Server schlaegt sie selbst nach.
            if not wer and d.get("lap_id"):
                wer = laps.install_von_runde(str(d.get("lap_id")))
            ergebnis = laps.set_banned(wer, path.endswith("/ban"),
                                       str(d.get("reason") or ("banned in admin"
                                                               if path.endswith("/ban") else "")))
            if path.endswith("/unban"):
                # Aufheben heisst auch: Strafpunkte weg, sonst sperrt der naechste
                # Fehler sofort wieder.
                with laps._SCHLOSS:
                    k = laps.load_keys()
                    if wer in k["installs"]:
                        k["installs"][wer]["strikes"] = []
                        k["installs"][wer]["banned_auto"] = False
                        laps.save_keys(k)
            return as_json(200, ergebnis)

        raise ApiError(404, "%s %s does not exist here" % (method, path))
    except laps.SubmitError as e:
        raise ApiError(e.status, e.message) from None


def race_endpunkt(method: str, path: str, headers, body: bytes,
                  now: float | None = None, client: str = "", keys: dict | None = None,
                  root: Path | None = None):
    """Die Pfade rund um eingereichte Rennergebnisse (seit 2026-10-02).

    Ausgewiesen wie eine Runde (das Geheimnis der Installation), verwaltet wie eine Runde
    (Admin-Unterschrift). Die Logik steht in race_submissions.
    """
    def as_json(status: int, value):
        return (status, "application/json; charset=utf-8",
                json.dumps(value, ensure_ascii=False, indent=1).encode("utf-8"))

    def rumpf() -> dict:
        if len(body or b"") > 4 * 1024 * 1024:
            raise ApiError(413, "The request is larger than 4 MB.")
        try:
            d = json.loads((body or b"{}").decode("utf-8"))
        except Exception:
            raise ApiError(400, "The request body is not JSON.") from None
        if not isinstance(d, dict):
            raise ApiError(400, "The request body must be a JSON object.")
        return d

    try:
        if method == "POST" and path == "/api/race/submit":
            try:
                install_id, _ = laps.verify(headers, method, path, body, now=now)
            except laps.SubmitError as e:
                if e.status == 401:
                    ip_bans.fehlversuch(client, "race signature", now)
                raise
            anfrage = rumpf()
            try:
                rennen, auffaellig = rennen_api.pruefe_rennen(anfrage.get("race"))
                beleg = rennen_api.beleg_pruefen(anfrage.get("proof"))
            except laps.SubmitError as e:
                if e.status in (400, 413):
                    laps.strafpunkt(install_id, e.message, now)
                raise
            rennen_api.tageskontingent(install_id, root, now)
            d = rennen_api.store(install_id, rennen, auffaellig, beleg, root, now)
            return as_json(200, {"ok": True, "id": d["id"], "flags": auffaellig})

        if method == "GET" and path == "/api/race/hp":
            # OEFFENTLICH: je Strecke, Klasse und Auto die Zeit an der 1-%-Grenze. Ohne die
            # schnellste einzelne Zeit -- die ist genau die, die ein Betrueger stellen wuerde.
            return as_json(200, {"boards": [{k: v for k, v in b.items() if k != "fastest"}
                                            for b in rennen_api.hp_bretter(root)],
                                 "rule": "time at the 1 % mark of all human times, never the single fastest"})

        # --- ab hier nur mit Admin-Unterschrift ---
        keys = load_keys() if keys is None else keys

        if method == "GET" and path == "/api/admin/race/list":
            require_admin(keys, method, path, headers, body, now)
            # install_id bleibt draussen, wie bei den Runden.
            return as_json(200, {"races": [{k: v for k, v in d.items() if k != "install_id"}
                                           for d in rennen_api.list_races(root)],
                                 "boards": rennen_api.hp_bretter(root)})

        if method == "POST" and path == "/api/admin/race/proof":
            require_admin(keys, method, path, headers, body, now)
            return (200, "image/jpeg", rennen_api.beleg(str(rumpf().get("id") or ""), root))

        if method == "POST" and path in ("/api/admin/race/hide", "/api/admin/race/show"):
            require_admin(keys, method, path, headers, body, now)
            d = rumpf()
            try:
                platz = int(d.get("place"))
            except (TypeError, ValueError):
                raise ApiError(400, "'place' must be a number.") from None
            return as_json(200, rennen_api.set_row_hidden(
                str(d.get("id") or ""), platz, path.endswith("hide"), str(d.get("reason") or ""), root))

        raise ApiError(404, "%s %s does not exist here" % (method, path))
    except laps.SubmitError as e:
        raise ApiError(e.status, e.message) from None


SPUR_PFAD = re.compile(r"/api/lap/telemetry/([0-9a-f]{20})\.(csv|json\.gz)")


def download_name(path: str) -> str | None:
    """Der Dateiname fuer einen Telemetrie-Download -- oder None fuer alles andere."""
    spur = SPUR_PFAD.fullmatch(path or "")
    if not spur:
        return None
    try:
        _, datensatz = laps.oeffentliche_spur(spur.group(1))
        return laps.spur_dateiname(datensatz, spur.group(2))
    except Exception:
        return "FH6_lap_%s.%s" % (spur.group(1)[:8], spur.group(2))


def handle(method: str, path: str, headers, body: bytes, *,
           keys: dict | None = None, dataset_file: Path | None = None,
           contrib_root: Path | None = None, visibility: Path | None = None,
           sweep_root: Path | None = None, now: float | None = None,
           keys_file: Path | None = None, client: str = "",
           attempts_file: Path | None = None) -> tuple[int, str, bytes]:
    """Einen Aufruf beantworten. Gibt (Status, Inhaltstyp, Rumpf) zurueck.

    Kennt kein Socket und keinen Handler -- darum ist jede Antwort hier pruefbar.
    """
    keys = load_keys() if keys is None else keys

    def as_json(status: int, value) -> tuple[int, str, bytes]:
        return (status, "application/json; charset=utf-8",
                json.dumps(value, ensure_ascii=False, indent=1).encode("utf-8"))

    # WER SICH MELDET, WIRD GEZAEHLT.
    #
    # Die App fragt beim Start `/api/summary` (ist der Datensatz neuer?) und
    # `/api/haptics` (gibt es ein Update?). Das ist der einzige Zeitpunkt, an dem
    # der Server ueberhaupt von einer laufenden Installation erfaehrt -- gezaehlt
    # wird darum hier und nirgends sonst.
    #
    # Gespeichert wird kein Absender, sondern ein Hash mit einem geheimen Salz,
    # das nur auf diesem Server liegt. Die Begruendung steht vollstaendig in
    # `usage.py`.
    if method == "GET" and path in ("/api/summary", "/api/haptics"):
        try:
            usage.note_checkin(str(headers.get("X-Forza-Install") or ""),
                               client, now)
        except Exception:
            # Eine Zaehlung darf eine Auskunft nie verhindern.
            pass

    # GESPERRTE ADRESSEN: kein Anmelden, kein Einreichen, kein Hochladen. Die Seite
    # und die oeffentlichen Auskuenfte bleiben -- siehe ip_bans.
    braucht_anmeldung = (path.startswith("/api/admin") or path.startswith("/api/lap/")
                         or path.startswith("/api/race/")
                         or path in ("/api/contribute", "/api/package", "/download/tool"))
    if braucht_anmeldung and client:
        rest = ip_bans.gesperrt(client, now)
        if rest > 0:
            return as_json(403, {"ok": False,
                                 "error": f"This address is blocked for {rest // 3600 + 1} "
                                          f"more hour(s) after too many failed attempts."})

    try:
        if method == "GET" and path == "/api/summary":
            return as_json(200, summary(dataset_file, visibility))

        if method == "GET" and path == "/api/admin/bans":
            require_admin(keys, method, path, headers, body, now)
            return as_json(200, {
                "installs": [i for i in laps.installs() if i["banned"] or i["strikes_24h"]],
                "ips": ip_bans.liste(now),
                "rules": {"strikes_to_ban": laps.STRAFPUNKTE_GRENZE,
                          "strike_window_hours": laps.STRAFPUNKTE_FENSTER // 3600,
                          "slower_than_leaderboard_counts_from": laps.DEUTLICH_LANGSAMER,
                          "failed_logins_to_block_ip": ip_bans.FEHL_GRENZE,
                          "ip_block_hours": ip_bans.SPERRE_SEKUNDEN // 3600},
            })

        if method == "POST" and path == "/api/admin/ip/unban":
            require_admin(keys, method, path, headers, body, now)
            try:
                wunsch = json.loads((body or b"{}").decode("utf-8"))
            except Exception:
                raise ApiError(400, "body is not JSON") from None
            return as_json(200, ip_bans.entsperren(str(wunsch.get("ip") or "")))

        # ---------------------------------------------------------------- #
        # Eingereichte Runden. Die Logik steht in lap_submissions; hier steht
        # nur, welcher Pfad welche Funktion ruft.
        if path.startswith("/api/lap/") or path.startswith("/api/admin/lap"):
            if not feature_an("lap_submissions"):
                # 404 und nicht 403: eine abgeschaltete Schnittstelle soll aussehen,
                # als gaebe es sie nicht. Ein 403 verraet, dass hier etwas liegt,
                # und lockt jemanden dazu, es spaeter nochmal zu versuchen.
                raise ApiError(404, "%s %s does not exist here" % (method, path))
            return lap_endpunkt(method, path, headers, body, now, client, keys)

        # Eingereichte Rennergebnisse (seit 2026-10-02): hinter demselben Schalter wie die Runden.
        if path.startswith("/api/race/") or path.startswith("/api/admin/race"):
            if not feature_an("lap_submissions"):
                raise ApiError(404, "%s %s does not exist here" % (method, path))
            return race_endpunkt(method, path, headers, body, now, client, keys)

        if method == "GET" and path == "/api/admin/usage":
            # Hinter dem Admin-Geheimnis, obwohl nichts Personenbezogenes darin
            # steht: es ist eine Geschaeftszahl, keine Auskunft fuer jedermann.
            require_admin(keys, method, path, headers, body, now)
            return as_json(200, usage.summary(now))

        if method == "GET" and path == "/api/admin/downloads":
            # Je Werkzeug und Fassung, fuer immer -- siehe downloads.py.
            require_admin(keys, method, path, headers, body, now)
            import downloads
            return as_json(200, downloads.summary())

        if method == "GET" and path == "/api/admin/visits":
            # Besuche je Stunde, Tag, Land und Region -- ohne Personenbezug, siehe visits.py.
            require_admin(keys, method, path, headers, body, now)
            import visits
            return as_json(200, visits.summary(now))

        if method == "GET" and path == "/api/admin/installs":
            if not feature_an("lap_submissions"):
                raise ApiError(404, "%s %s does not exist here" % (method, path))
            require_admin(keys, method, path, headers, body, now)
            return as_json(200, {"installs": laps.installs()})

        if method == "GET" and path == "/api/haptics":
            # Frei zu haben, wie der Download selbst: wer die App hat, darf
            # erfahren, ob sie veraltet ist. Ein Passwort davor haette nur zur
            # Folge, dass alte Fassungen alt bleiben.
            return as_json(200, haptics_info())

        if method == "GET" and path == "/api/cars":
            # ALLE AUTOS UND WIE MAN AN SIE KOMMT, fuer "Car collection" in der App.
            # Frei zu haben wie /api/haptics: nichts darin ist mehr als die
            # offizielle Liste und das Wiki. Der Server baut sie selbst taeglich neu
            # (car_availability.im_hintergrund).
            import car_availability
            if not car_availability.AUSGABE.exists():
                raise ApiError(503, "The car list has not been built yet.")
            return 200, "application/json; charset=utf-8", car_availability.AUSGABE.read_bytes()

        if method == "GET" and path == "/api/dataset":
            target = dataset_file or DATASET_FILE
            if not target.exists():
                raise ApiError(503, "No dataset has been built yet.")
            return 200, "application/json; charset=utf-8", target.read_bytes()

        if method == "POST" and path == "/api/contribute":
            # Erst das Tor, dann die 200 MB lesen: eine gesperrte Maschine soll den
            # Server nicht mehr beschaeftigen als noetig.
            require_upload_password(keys, headers, client, now, attempts_file)
            if len(body) > MAX_UPLOAD_BYTES:
                raise ApiError(413, f"{len(body) / 1e6:.0f} MB, the limit is "
                                    f"{MAX_UPLOAD_BYTES / 1e6:.0f} MB")
            if not body:
                raise ApiError(400, "empty body -- the ZIP belongs in it directly")
            try:
                report = import_archive(
                    body, keys=keys,
                    root=contrib_root or CONTRIB_ROOT, force=False)
            except fmt.ContribError as error:
                # 403 und nicht 400: die Datei ist in Ordnung, der Absender nicht --
                # oder umgekehrt. Beides ist eine Frage der Berechtigung, und ein
                # unterschiedlicher Code je Grund wuerde einem Angreifer sagen,
                # welchen Teil er als naechstes variieren soll.
                raise ApiError(403, str(error)) from None
            # Angenommen: das Zaehlwerk dieser Maschine zurueck auf null, damit
            # frueheres Vertippen sich nicht ueber Wochen zu einer Sperre summiert.
            clear_upload_failures(client, attempts_file)
            return as_json(200, {"ok": True, **report})

        if method == "GET" and path == "/api/admin/runs":
            require_admin(keys, method, path, headers, body, now)
            return as_json(200, {"runs": list_runs(sweep_root, contrib_root,
                                                   visibility)})

        if method == "GET" and path in ("/api/package", "/download/tool"):
            # Hinter demselben getippten Passwort wie das Hochladen, und mit
            # Absicht demselben: das Werkzeug fahert fremde Menues fern und laedt am
            # Ende hier hoch. Wer es sinnvoll benutzen kann, braucht das Passwort
            # ohnehin -- ein zweites Geheimnis waere ein zweites, das veraltet.
            # Dieselbe Sperre nach zehn Fehlversuchen gilt damit auch hier.
            require_upload_password(keys, headers, client, now, attempts_file)
            # Neu gebaut, wenn sich am Quelltext etwas geruehrt hat -- sonst laedt
            # jemand wochenlang eine Fassung mit einem laengst behobenen Fehler.
            packer = paketbauer()
            if packer is not None:
                archive = packer.ensure_public_package()
            else:
                # Spiegel: hier wird nicht gebaut, die fertige ZIP kam mit der
                # Pipeline mit. Frisch ist sie damit genau so oft, wie ausgeliefert
                # wird -- und das ist bei jedem Lauf von deploy_gnas.py.
                archive = DIST_DIR / "forza-contrib-tool.zip"
                if not archive.exists():
                    raise ApiError(503, "The scan tool has not been deployed here "
                                        "yet. On the scan machine: "
                                        "python scripts/deploy_gnas.py --apply")
            return (200, "application/zip", archive.read_bytes())

        if method == "POST" and path == "/api/admin/package":
            require_admin(keys, method, path, headers, body, now)
            try:
                wish = json.loads((body or b"{}").decode("utf-8"))
            except Exception:
                raise ApiError(400, "body is not JSON") from None
            who = str(wish.get("contributor") or "")
            try:
                fmt.check_contributor(who)
            except fmt.ContribError as error:
                raise ApiError(400, str(error)) from None
            # Diese Fassung traegt das Geheimnis des Beitragenden UND das allgemeine
            # Zugangspasswort. Sie geht nur ueber einen unterschriebenen Aufruf
            # heraus -- ein Link waere weitergeleitet, bevor jemand nachdenkt.
            packer = paketbauer()
            if packer is None:
                raise ApiError(503,
                               "A personal package can only be built where the "
                               "scanner is -- on the machine with the game: "
                               "python scripts/build_contrib_package.py <name>. "
                               "Then bring it here with deploy_gnas.py.")
            import tempfile
            with tempfile.TemporaryDirectory() as folder:
                archive = packer.build(
                    who, str(wish.get("server") or packer.DEFAULT_SERVER),
                    Path(folder), keys_file or KEYS_FILE)
                return (200, "application/zip", archive.read_bytes())

        if method == "POST" and path == "/api/admin/rotate-gate":
            require_admin(keys, method, path, headers, body, now)
            target = keys_file or KEYS_FILE
            if not target.exists():
                raise ApiError(503, f"{target} does not exist")
            stored = json.loads(target.read_text(encoding="utf-8-sig"))
            stored["upload_password"] = secrets.token_urlsafe(GATE_BYTES)
            stored.setdefault("issued", {})["(gate)"] = (
                datetime.now().astimezone().isoformat(timespec="seconds"))
            target.write_text(json.dumps(stored, indent=1, ensure_ascii=False),
                              encoding="utf-8")
            # Einmal zurueckgegeben, damit es in ein neues Paket kann -- danach steht
            # es nur noch in der Schluesseldatei. Die Antwort sagt ausdruecklich, was
            # der Knopf angerichtet hat: jedes verteilte Paket ist ab jetzt tot.
            return as_json(200, {
                "ok": True,
                "upload_password": stored["upload_password"],
                "length": len(stored["upload_password"]),
                "warning": ("Every package handed out so far is now invalid. "
                            "Build and hand out new packages."),
            })

        if method == "POST" and path == "/api/admin/visibility":
            require_admin(keys, method, path, headers, body, now)
            try:
                wish = json.loads((body or b"{}").decode("utf-8"))
            except Exception:
                raise ApiError(400, "body is not JSON") from None
            run_id = str(wish.get("run_id") or "")
            if not run_id:
                raise ApiError(400, "run_id is missing")
            try:
                result = set_visibility(run_id, bool(wish.get("hidden")),
                                        str(wish.get("reason") or ""), visibility)
            except fmt.ContribError as error:
                raise ApiError(400, str(error)) from None
            return as_json(200, {"ok": True, **result})

        raise ApiError(404, f"{method} {path} does not exist here")
    except ApiError as error:
        # Eine falsche Admin-Unterschrift oder ein falsches Zugangspasswort ist ein
        # fehlgeschlagener Anmeldeversuch -- zehn in einer Stunde sperren die Adresse.
        if error.status == 401 and (path.startswith("/api/admin") or path in (
                "/api/contribute", "/api/package", "/download/tool")):
            ip_bans.fehlversuch(client, "admin" if path.startswith("/api/admin")
                                else "upload password", now)
        return as_json(error.status, {"ok": False, "error": error.message})
    except Exception as error:
        # Letztes Netz. Dieser Server ist von aussen erreichbar, und ein Aufruf, der
        # eine unerwartete Ausnahme ausloest, darf hoechstens diesen Aufruf kosten --
        # nicht den Dienst, und schon gar nicht den laufenden Sweep, dessen Seite er
        # ausliefert. Die Meldung bleibt allgemein: eine Zeilennummer aus dem
        # Quelltext gehoert nicht in eine Antwort an einen Fremden. Ins Protokoll
        # gehoert sie sehr wohl.
        sys.stderr.write(f"[api] {method} {path}: unerwartet: "
                         f"{type(error).__name__}: {error}\n")
        sys.stderr.flush()
        return as_json(500, {"ok": False,
                             "error": "unexpected error -- see the server log"})
