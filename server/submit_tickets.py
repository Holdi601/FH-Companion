"""Einmal-Tickets fuer Einreichungen aus der App (seit 2026-10-11).

Jede Einreichung -- Runde, Rennergebnis, Bestenlisten-Scan -- ist mit dem Geheimnis der
Installation unterschrieben (lap_submissions.verify). Dieses Geheimnis liegt beim Nutzer auf
der Platte (submit_identity.json) und gilt fuer immer. Der Nutzer wollte dazu einen Schluessel,
"den die App von der Website nachfragt, der nur fuer kurze Zeit aktiv ist und den RAM nicht
verlassen darf ... und nach dem Submit invalide wird".

Darum vor jeder Einreichung:

    POST /api/lap/ticket   {"for": "/api/scan/submit"}   (mit dem Geheimnis unterschrieben)
      -> {"ticket": "<32 Hex>", "key": "<32 Byte, Base64>", "expires_in": 120}

und die Einreichung traegt zusaetzlich

    X-Forza-Ticket: <ticket>
    X-Forza-Ticket-Signature: HMAC-SHA256(key, dieselbe Zeile wie die Hauptunterschrift)

Ein Ticket gilt GUELTIG Sekunden, fuer genau einen Endpunkt und eine Installation, und genau
einmal: es wird beim ersten Einloesen entfernt -- auch wenn die Unterschrift nicht passt, damit
sich ein abgefangenes Ticket nicht durchprobieren laesst. Die Tickets stehen nur im Speicher
dieses Prozesses; ein Neustart des Dienstes verwirft sie (die App holt sich dann ein neues).

Was das NICHT leistet, damit es niemand ueberschaetzt: wer die App selbst nachbaut oder ihr
Geheimnis von der Platte liest, kann sich auch Tickets holen. Das Ticket macht jede einzelne
Einreichung kurzlebig, einmalig und an den Endpunkt gebunden; gegen erfundene Daten pruefen
die Inhalte (scan_submissions.freigabe, lap_submissions.pruefe_runde).
"""

from __future__ import annotations

import base64
import hashlib
import hmac
import secrets
import threading
import time

import lap_submissions as laps

GUELTIG = 120
# Immer an. Nur Tests, die etwas anderes als die Tickets pruefen, schalten es ab.
PFLICHT = True
OFFEN_JE_INSTALLATION = 8
ZWECKE = ("/api/lap/submit", "/api/race/submit", "/api/scan/submit")

_SCHLOSS = threading.Lock()
# Ticket -> (Installation, Endpunkt, Schluessel, Ablauf)
_OFFEN: dict[str, tuple[str, str, bytes, float]] = {}


def _aufraeumen(jetzt: float) -> None:
    for tid in [t for t, (_, _, _, ab) in _OFFEN.items() if ab < jetzt]:
        _OFFEN.pop(tid, None)


def ausstellen(install_id: str, zweck: str, now: float | None = None) -> dict:
    """Ein Ticket fuer `zweck` ausstellen. Wirft SubmitError(400/429)."""
    if zweck not in ZWECKE:
        raise laps.SubmitError(400, "'for' must be one of %s." % ", ".join(ZWECKE))
    jetzt = now or time.time()
    with _SCHLOSS:
        _aufraeumen(jetzt)
        if sum(1 for inst, _, _, _ in _OFFEN.values() if inst == install_id) >= OFFEN_JE_INSTALLATION:
            raise laps.SubmitError(429, "Too many open tickets for this installation; use one first.")
        tid = secrets.token_hex(16)
        schluessel = secrets.token_bytes(32)
        _OFFEN[tid] = (install_id, zweck, schluessel, jetzt + GUELTIG)
    return {"ticket": tid, "key": base64.b64encode(schluessel).decode("ascii"),
            "expires_in": GUELTIG, "for": zweck}


def einloesen(headers, install_id: str, method: str, path: str, body: bytes,
              now: float | None = None) -> None:
    """Das Ticket dieser Einreichung pruefen und verbrauchen. Wirft SubmitError(401)."""
    def kopf(name: str) -> str:
        try:
            return (headers.get(name) or "").strip()
        except AttributeError:
            return ""

    tid = kopf("X-Forza-Ticket")
    unterschrift = kopf("X-Forza-Ticket-Signature")
    if not PFLICHT and not tid:
        return
    if not tid or not unterschrift:
        raise laps.SubmitError(401, "This submission needs a ticket (POST /api/lap/ticket). "
                                    "Update FH Companion.")
    jetzt = now or time.time()
    with _SCHLOSS:
        eintrag = _OFFEN.pop(tid, None)
    if eintrag is None:
        raise laps.SubmitError(401, "The ticket is unknown, used or expired.")
    inst, zweck, schluessel, ablauf = eintrag
    if inst != install_id or zweck != path:
        raise laps.SubmitError(401, "The ticket belongs to another installation or endpoint.")
    if jetzt > ablauf:
        raise laps.SubmitError(401, "The ticket expired.")
    erwartet = unterschrift_mit(schluessel, method, path, kopf("X-Forza-Timestamp"),
                                kopf("X-Forza-Nonce"), body)
    if not hmac.compare_digest(erwartet, unterschrift):
        raise laps.SubmitError(401, "The ticket signature does not match.")


def unterschrift_mit(schluessel: bytes, method: str, path: str, stamp: str, nonce: str,
                     body: bytes) -> str:
    """Dieselbe Zeile wie lap_submissions.signature, mit dem Ticket-Schluessel (Bytes)."""
    rumpf = hashlib.sha256(body or b"").hexdigest()
    text = "\n".join([method.upper(), path, stamp, nonce, rumpf])
    return hmac.new(schluessel, text.encode("utf-8"), hashlib.sha256).hexdigest()
