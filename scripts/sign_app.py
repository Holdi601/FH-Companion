# -*- coding: utf-8 -*-
"""Die EXE signieren -- wenn ein Zertifikat eingerichtet ist.

    python scripts/sign_app.py                 # signiert, was in dist/ liegt
    python scripts/sign_app.py --pruefen       # nur nachsehen, was da ist

## Warum das ueberhaupt gebraucht wird

Windows schreibt "Unbekannter Herausgeber", solange die Datei nicht signiert ist.
Dieses Feld kommt AUSSCHLIESSLICH aus der digitalen Signatur -- die
Versionsressource (Company, Product, Version) fuellt es nicht, und zwar mit Absicht:
dort kann jeder alles hineinschreiben. Am 2026-09-15 nachgemessen: die EXE traegt
vollstaendige Versionsangaben und `Get-AuthenticodeSignature` sagt trotzdem
`NotSigned`, also bleibt es bei "unbekannt".

Ein SELBSTSIGNIERTES Zertifikat hilft dabei nicht, solange es nicht auf dem
Zielrechner als vertrauenswuerdig eingetragen ist. Fuer einen bekannten Kreis ist
genau das aber ein gangbarer Weg -- siehe `docs/code-signing.md`.

## Wie es eingerichtet wird

`config/signing.json` anlegen (steht in .gitignore, kommt NIE ins Git):

    {"modus": "thumbprint", "thumbprint": "AB12...", "zeitstempel": "http://timestamp.digicert.com"}
    {"modus": "pfx", "pfx": "C:/pfad/zum.pfx", "passwort_env": "SIGNPW"}
    {"modus": "trusted-signing", "endpoint": "https://eus.codesigning.azure.net",
     "konto": "meinkonto", "profil": "meinprofil"}

Das Passwort steht als NAME EINER UMGEBUNGSVARIABLEN darin, nie als Wert. So kann
die Datei notfalls auch versehentlich gelesen werden, ohne dass ein Geheimnis
mitkommt.

Ist die Datei nicht da, tut dieses Skript nichts und meldet das auch so. Der Bau
laeuft dann unsigniert weiter -- was heute der Normalfall ist.
"""
from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
CONFIG = WORKSPACE / "config" / "signing.json"
APP_DIR = WORKSPACE / "dist" / "fh-companion" / "app"

#: Nur was wir selbst gebaut haben. Die Laufzeitdateien von .NET sind bereits von
#: Microsoft signiert -- sie noch einmal zu signieren waere falsch und nutzlos.
EIGENE = ("FH Companion.exe", "FH Companion.dll", "Forza Grip Haptics.exe")


def signtool() -> str | None:
    """signtool.exe finden -- es liegt im Windows SDK, nicht im PATH."""
    gefunden = shutil.which("signtool")
    if gefunden:
        return gefunden
    basis = Path(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)"))
    kandidaten = sorted(
        (basis / "Windows Kits" / "10" / "bin").glob("*/x64/signtool.exe"),
        reverse=True)
    return str(kandidaten[0]) if kandidaten else None


def laden() -> dict | None:
    try:
        return json.loads(CONFIG.read_text(encoding="utf-8"))
    except OSError:
        return None
    except ValueError as fehler:
        print(f"config/signing.json ist kaputt: {fehler}")
        return None


def befehl(cfg: dict, datei: Path) -> list[str] | None:
    """Die Befehlszeile fuer eine Datei -- oder None, wenn der Modus unbekannt ist."""
    werkzeug = signtool()
    if werkzeug is None:
        print("signtool.exe nicht gefunden. Es kommt mit dem Windows SDK.")
        return None

    stempel = cfg.get("zeitstempel") or "http://timestamp.digicert.com"
    # ZEITSTEMPEL IST PFLICHT, NICHT KUER: ohne ihn wird jede Signatur ungueltig,
    # sobald das Zertifikat ablaeuft -- auch bei Dateien, die laengst ausgeliefert
    # sind. Mit Zeitstempel bleibt sie gueltig, weil belegt ist, dass sie waehrend
    # der Laufzeit des Zertifikats entstand.
    gemeinsam = ["/fd", "SHA256", "/tr", stempel, "/td", "SHA256", "/v"]

    modus = (cfg.get("modus") or "").lower()
    if modus == "thumbprint":
        daumen = (cfg.get("thumbprint") or "").replace(" ", "")
        if not daumen:
            print("modus 'thumbprint', aber kein thumbprint angegeben.")
            return None
        return [werkzeug, "sign", "/sha1", daumen, *gemeinsam, str(datei)]

    if modus == "pfx":
        pfx = cfg.get("pfx") or ""
        if not Path(pfx).exists():
            print(f"die PFX-Datei {pfx!r} gibt es nicht.")
            return None
        name = cfg.get("passwort_env") or "SIGNPW"
        pw = os.environ.get(name)
        if not pw:
            print(f"die Umgebungsvariable {name} ist nicht gesetzt.")
            return None
        return [werkzeug, "sign", "/f", pfx, "/p", pw, *gemeinsam, str(datei)]

    if modus == "trusted-signing":
        # Azure Trusted Signing. Braucht die Dlib von Microsoft und eine
        # Metadatendatei; der Weg steht in docs/code-signing.md.
        dlib = cfg.get("dlib")
        meta = cfg.get("metadata")
        if not dlib or not meta:
            print("modus 'trusted-signing' braucht 'dlib' und 'metadata'.")
            return None
        return [werkzeug, "sign", "/v", "/debug", "/fd", "SHA256",
                "/tr", stempel, "/td", "SHA256",
                "/dlib", dlib, "/dmdf", meta, str(datei)]

    print(f"unbekannter modus {modus!r}. Erlaubt: thumbprint, pfx, trusted-signing.")
    return None


def main(argv: list[str] | None = None) -> int:
    argv = sys.argv[1:] if argv is None else argv
    nur_pruefen = "--pruefen" in argv

    cfg = laden()
    if cfg is None:
        print("Keine config/signing.json -- es wird nicht signiert.")
        print("Die App bleibt damit 'Unbekannter Herausgeber'.")
        print("Die Moeglichkeiten stehen in docs/code-signing.md.")
        return 0

    dateien = [APP_DIR / n for n in EIGENE]
    fehlend = [d for d in dateien if not d.exists()]
    if fehlend:
        print("Diese Dateien gibt es nicht (erst das Paket bauen):")
        for d in fehlend:
            print("  " + str(d))
        return 1

    if nur_pruefen:
        print(f"modus: {cfg.get('modus')!r}, signtool: {signtool()}")
        for d in dateien:
            print(f"  bereit: {d.name}")
        return 0

    for d in dateien:
        zeile = befehl(cfg, d)
        if zeile is None:
            return 1
        # Die Befehlszeile NICHT ausgeben: bei modus 'pfx' steht das Passwort drin.
        print(f"  signiere {d.name} ...")
        r = subprocess.run(zeile, capture_output=True, text=True)
        if r.returncode != 0:
            # Auch hier nur die Ausgabe von signtool, nicht unser Aufruf.
            print("  gescheitert:")
            for zl in (r.stdout + r.stderr).strip().splitlines()[-8:]:
                print("    " + zl)
            return 1
    print("signiert.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
