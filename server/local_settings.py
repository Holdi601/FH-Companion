"""Die Werte, die EINE Installation ausmachen -- nie im Repository.

config/local.json haelt, was diesen Rechner und diesen Server von jedem anderen
unterscheidet: die Adresse des Servers, seinen oeffentlichen Namen, die
Kontaktadresse der Webseite. Die Datei steht in .gitignore; config/local.example.json
zeigt ihre Form. Nichts davon ist geheim (alles steht auf der Webseite), aber es
gehoert zu einer Person und nicht in den Quelltext.

Jeder Wert laesst sich ueber die Umgebung ueberschreiben: FHC_SERVER,
FHC_PUBLIC_HOST, FHC_CONTACT_EMAIL.

Fehlt die Datei, sind die Werte leer, und jeder Aufrufer muss damit leben koennen:
ein Werkzeug ohne Server fragt keinen, eine Seite ohne Kontakt nennt keinen.
"""
from __future__ import annotations

import json
import os
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
DATEI = WORKSPACE / "config" / "local.json"


def get(key: str, default: str = "") -> str:
    aussen = os.environ.get("FHC_" + key.upper())
    if aussen:
        return aussen.strip()
    try:
        wert = json.loads(DATEI.read_text(encoding="utf-8")).get(key)
    except (OSError, ValueError):
        return default
    return str(wert).strip() if wert else default


def server() -> str:
    """Die Adresse des Servers, ohne Schraegstrich am Ende -- oder leer."""
    return get("server").rstrip("/")


def old_server() -> str:
    """Derselbe Server ueber http, die Vorgabe vor dem 2026-09-25 -- oder leer."""
    s = server()
    return "http://" + s[len("https://"):] if s.startswith("https://") else ""


def public_host() -> str:
    """Der oeffentliche Name des Servers, ohne Schema und Port."""
    name = get("public_host")
    if name:
        return name
    s = server()
    return s.split("//", 1)[-1].split("/", 1)[0].rsplit(":", 1)[0] if s else ""


def contact_email() -> str:
    return get("contact_email")


def site_title() -> str:
    """Der Name der Webseite -- ohne Eintrag heisst sie wie die App."""
    return get("site_title", "FH Companion")


def app_data() -> Path:
    """Der Datenordner der App: FHCompanion, vor dem 2026-09-26 ForzaGripHaptics.

    Die App zieht beim ersten Start um. Bis dahin (oder wenn der Umzug scheiterte)
    liegt alles noch im alten Ordner -- dann gilt der.
    """
    basis = Path(os.environ.get("LOCALAPPDATA", "") or Path.home() / "AppData" / "Local")
    neu = basis / "FHCompanion"
    alt = basis / "ForzaGripHaptics"
    return alt if not neu.exists() and alt.exists() else neu
