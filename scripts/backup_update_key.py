"""Den privaten Update-Schluessel sichern -- verschluesselt, verteilt, pruefbar.

    python scripts/backup_update_key.py                  sichern und pruefen
    python scripts/backup_update_key.py --verify         nur pruefen (entschluesseln, vergleichen)
    python scripts/backup_update_key.py --copy-passphrase  Passphrase in die Zwischenablage (fuer 1Password)
    python scripts/backup_update_key.py --restore ZIEL   aus der Sicherung wiederherstellen
    python scripts/backup_update_key.py --1password      Schluessel UND Passphrase in 1Password ablegen
    python scripts/backup_update_key.py --1password --verify   nur pruefen, ob 1Password aktuell ist
    python scripts/backup_update_key.py --restore-from-1password ZIEL   Schluessel aus 1Password zurueckholen

## Warum verschluesselt und verteilt

Ohne diesen Schluessel laesst sich kein Update mehr unterschreiben, und jede
installierte App muesste von Hand neu installiert werden -- er muss also den
Verlust dieses Rechners ueberleben. Zugleich darf er nie offen herumliegen: die
Freigabe "Data" auf GNAS2 lesen mehrere Konten.

Darum:
  * GNAS2 (Z:\\Backups\\forza-signing\\) bekommt nur den VERSCHLUESSELTEN Schluessel
    (AES-256-GCM, Schluessel aus der Passphrase ueber scrypt), dazu diese Anleitung;
  * die Passphrase liegt auf GNAS in einem Ordner, den nur das eigene Konto lesen
    darf (forza-secrets neben dem Seitenordner, 700), und in der Windows-Anmeldeinformations-
    verwaltung dieses Rechners -- und gehoert zusaetzlich in 1Password.
Wer nur GNAS2 hat, hat nur Rauschen; wer nur GNAS hat, hat keinen Schluessel.

Die Passphrase wird NIE ausgegeben. --copy-passphrase legt sie in die
Zwischenablage, damit sie in 1Password eingefuegt werden kann.

## 1Password

--1password legt EINEN Eintrag an ("Forza update signing key backup", Kategorie
Passwort, Schlagwort forza): Passwortfeld = Passphrase der GNAS2-Sicherung, dazu
der Schluessel selbst als verdecktes Feld. 1Password ist Ende-zu-Ende verschluesselt
und ueberlebt den Verlust von Rechner, GNAS und GNAS2 zugleich -- der einzige Ort,
an dem der Schluessel ohne die anderen beiden wiederherstellbar ist.

Der Weg dorthin ist die 1Password-CLI (op.exe) ueber die Desktop-App: jeder Zugriff
wird in der App bestaetigt (Windows Hello oder Kennwort), ein Konto-Kennwort liegt
nirgends. Die Geheimnisse gehen ueber die STANDARDEINGABE an op -- nie als
Argument, das in der Prozessliste stuende. Und weil op.exe den Schluessel in die
Hand bekommt, wird vorher seine Authenticode-Unterschrift geprueft: ein fremdes
op.exe im PATH bekaeme sonst den Schluessel geschenkt.

Aendert sich Schluessel oder Passphrase, wird ein neuer Eintrag angelegt und der
alte ARCHIVIERT (nicht geloescht) -- mit ihm liessen sich alte Fassungen noch pruefen.
"""
from __future__ import annotations

import argparse
import base64
import ctypes
import ctypes.wintypes as wt
import hashlib
import json
import os
import secrets
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

WS = Path(__file__).resolve().parent.parent
SCHLUESSEL = Path(os.environ.get("FORZA_UPDATE_KEY") or
                  Path.home() / ".forza-signing" / "update_signing_key.pkcs8.b64")
GNAS2 = Path(r"Z:\Backups\forza-signing")
DATEI = "update_signing_key.enc.json"
CRED_ZIEL = "ForzaGripHaptics/update-key-backup"
GNAS_DATEI = "update_key_backup_passphrase.txt"  # im geheim_ordner des GNAS (deploy_gnas)
AAD = b"forza-update-signing-key-v1"


# ---------------------------------------------------------------- Windows-Tresor
class CREDENTIAL(ctypes.Structure):
    _fields_ = [("Flags", wt.DWORD), ("Type", wt.DWORD), ("TargetName", wt.LPWSTR),
                ("Comment", wt.LPWSTR), ("LastWritten", wt.FILETIME),
                ("CredentialBlobSize", wt.DWORD), ("CredentialBlob", ctypes.POINTER(ctypes.c_char)),
                ("Persist", wt.DWORD), ("AttributeCount", wt.DWORD), ("Attributes", ctypes.c_void_p),
                ("TargetAlias", wt.LPWSTR), ("UserName", wt.LPWSTR)]


def tresor_schreiben(geheim: str) -> None:
    blob = geheim.encode("utf-16-le")
    c = CREDENTIAL()
    c.Type = 1                      # CRED_TYPE_GENERIC
    c.TargetName = CRED_ZIEL
    c.UserName = "backup-passphrase"
    c.Comment = "Passphrase of the encrypted Forza update signing key on GNAS2"
    c.CredentialBlobSize = len(blob)
    c.CredentialBlob = ctypes.cast(ctypes.create_string_buffer(blob, len(blob)), ctypes.POINTER(ctypes.c_char))
    c.Persist = 2                   # CRED_PERSIST_LOCAL_MACHINE
    if not ctypes.windll.advapi32.CredWriteW(ctypes.byref(c), 0):
        raise OSError("CredWriteW fehlgeschlagen")


def tresor_lesen() -> str | None:
    zeiger = ctypes.POINTER(CREDENTIAL)()
    if not ctypes.windll.advapi32.CredReadW(CRED_ZIEL, 1, 0, ctypes.byref(zeiger)):
        return None
    try:
        c = zeiger.contents
        return ctypes.string_at(c.CredentialBlob, c.CredentialBlobSize).decode("utf-16-le")
    finally:
        ctypes.windll.advapi32.CredFree(zeiger)


# ---------------------------------------------------------------- Verschluesselung
def ableiten(passphrase: str, salz: bytes) -> bytes:
    return hashlib.scrypt(passphrase.encode("utf-8"), salt=salz, n=2 ** 17, r=8, p=1,
                          maxmem=256 * 1024 * 1024, dklen=32)


def verschluesseln(klar: bytes, passphrase: str) -> dict:
    from cryptography.hazmat.primitives.ciphers.aead import AESGCM
    salz, nonce = secrets.token_bytes(16), secrets.token_bytes(12)
    geheim = AESGCM(ableiten(passphrase, salz)).encrypt(nonce, klar, AAD)
    return {"format": "forza-update-key-backup/1", "kdf": "scrypt n=2^17 r=8 p=1",
            "cipher": "AES-256-GCM", "aad": AAD.decode(),
            "salt": base64.b64encode(salz).decode(), "nonce": base64.b64encode(nonce).decode(),
            "ciphertext": base64.b64encode(geheim).decode(),
            "plaintext_sha256": hashlib.sha256(klar).hexdigest(),
            "created": datetime.now(timezone.utc).isoformat(timespec="seconds")}


def entschluesseln(d: dict, passphrase: str) -> bytes:
    from cryptography.hazmat.primitives.ciphers.aead import AESGCM
    return AESGCM(ableiten(passphrase, base64.b64decode(d["salt"]))).decrypt(
        base64.b64decode(d["nonce"]), base64.b64decode(d["ciphertext"]), d["aad"].encode())


LIESMICH = """FH Companion -- encrypted backup of the UPDATE SIGNING KEY
=================================================================

update_signing_key.enc.json is the private key that signs app updates, encrypted
with AES-256-GCM (key derived from a passphrase with scrypt). Without the key no
future update can be signed and every installed app would need a manual reinstall.

The passphrase is NOT here. It is kept in:
  * 1Password, item "Forza update signing key backup" (tag: forza) -- the password
    field is the passphrase, and the item also holds the key itself,
  * GNAS: forza-secrets/update_key_backup_passphrase.txt next to the site folder (owner only),
  * Windows Credential Manager on the build PC: ForzaGripHaptics/update-key-backup.

Restore (Python 3 with the 'cryptography' package):
    python scripts/backup_update_key.py --restore %USERPROFILE%\\.forza-signing\\update_signing_key.pkcs8.b64
It asks for the passphrase if the Credential Manager entry is gone.

If this folder is gone too, take the key straight from 1Password:
    python scripts/backup_update_key.py --restore-from-1password %USERPROFILE%\\.forza-signing\\update_signing_key.pkcs8.b64
"""


# ---------------------------------------------------------------- 1Password
OP_TITEL = "Forza update signing key backup"
OP_SCHLAGWORT = "forza"
OP_FELD_SCHLUESSEL = "private key (PKCS#8, base64)"
OP_FELD_SUMME = "SHA-256 of the key file"
OP_NOTIZ = ("Private ECDSA P-256 key that signs FH Companion app updates. Without it no "
            "update can be signed and every installed app needs a manual reinstall.\n\n"
            "Password field = passphrase of the encrypted copy on GNAS2 "
            "(Z:\\Backups\\forza-signing\\update_signing_key.enc.json).\n"
            "The key itself is in the section below. Restore on the build PC with\n"
            "  python scripts/backup_update_key.py --restore-from-1password "
            "%USERPROFILE%\\.forza-signing\\update_signing_key.pkcs8.b64\n\n"
            "Written by scripts/backup_update_key.py --1password.")
OP_ANLEITUNG = """1Password CLI is installed but not connected to the 1Password app yet. In the app:
    Settings (Ctrl+,) > Developer > "Integrate with 1Password CLI" -> on
Then run this again. 1Password asks once to allow the access (Windows Hello or your password)."""
OP: str | None = None


def op_pfad() -> str:
    """op.exe finden -- fehlt es, ueber winget nachinstallieren -- und seine Unterschrift pruefen."""
    import glob
    import shutil

    def suchen() -> str | None:
        lokal = os.environ.get("LOCALAPPDATA", "")
        for p in [shutil.which("op"), os.path.join(lokal, "Microsoft", "WinGet", "Links", "op.exe"),
                  *glob.glob(os.path.join(lokal, "Microsoft", "WinGet", "Packages",
                                          "AgileBits.1Password.CLI_*", "op.exe"))]:
            if p and os.path.isfile(p):
                return p
        return None

    p = suchen()
    if not p:
        print("1Password CLI fehlt -- wird ueber winget installiert ...")
        subprocess.run(["winget", "install", "-e", "--id", "AgileBits.1Password.CLI", "--silent",
                        "--accept-source-agreements", "--accept-package-agreements"], check=False)
        p = suchen()
    if not p:
        raise SystemExit("1Password CLI (op.exe) nicht gefunden und nicht installierbar")
    # op.exe bekommt gleich den Schluessel: nur das echte, von AgileBits unterschriebene.
    r = subprocess.run(["powershell", "-NoProfile", "-Command",
                        "$s = Get-AuthenticodeSignature -LiteralPath $env:OP_PRUEFEN; "
                        "'' + $s.Status + '|' + $s.SignerCertificate.Subject"],
                       env={**os.environ, "OP_PRUEFEN": p}, capture_output=True, text=True, timeout=60)
    status, _, wer = r.stdout.strip().partition("|")
    if status != "Valid" or "O=Agilebits" not in wer:
        raise SystemExit(f"{p} ist nicht von AgileBits unterschrieben ({status or 'keine Unterschrift'}) "
                         "-- nichts uebergeben")
    return p


def op(*argumente: str, eingabe: str | None = None, zeit: int = 180) -> subprocess.CompletedProcess:
    """op.exe aufrufen. Die Verbindung zur App ist launisch: ein Aufruf direkt nach
    einem anderen scheitert oft mit "cannot connect to 1Password app, make sure it is
    running", obwohl die App laeuft (op 2.39, App 8.12 aus dem Store) -- derselbe
    Aufruf gelingt Sekunden spaeter. Der Fehler faellt, BEVOR op etwas an den Server
    schickt; ein neuer Versuch legt also nichts doppelt an."""
    global OP
    OP = OP or op_pfad()
    import time
    for versuch in range(6):
        r = subprocess.run([OP, *argumente], input=eingabe, capture_output=True, text=True,
                           encoding="utf-8", errors="replace", timeout=zeit)
        if r.returncode == 0 or "cannot connect to 1Password app" not in r.stderr:
            return r
        time.sleep(1.5 * (versuch + 1))
    return r


def op_konto(konto: str | None) -> list[str]:
    """Konto waehlen und die Freigabe in der App anstossen. Gibt ['--account', X] zurueck."""
    r = op("account", "list", "--format", "json")
    konten = json.loads(r.stdout or "[]") if r.returncode == 0 else []
    if not konten:
        print(OP_ANLEITUNG)
        raise SystemExit(1)
    if konto is None:
        if len(konten) > 1:
            print("Mehrere 1Password-Konten -- eines mit --op-account waehlen:")
            for k in konten:
                print(f"    --op-account {k.get('url')}   ({k.get('email')})")
            raise SystemExit(1)
        # Die Anmeldeadresse, NICHT account_uuid: mit der Kennung aus "account list"
        # scheitert jeder Befehl mit "cannot connect to 1Password app" (op 2.39).
        konto = konten[0].get("url") or konten[0].get("user_uuid")
    print("1Password: Zugriff in der App bestaetigen, falls sie fragt ...")
    op("signin", "--account", konto)
    r = op("vault", "list", "--account", konto, "--format", "json")
    if r.returncode != 0:
        print("1Password: kein Zugriff --", r.stderr.strip()[-300:])
        if "integration" in r.stderr.lower() or "no accounts" in r.stderr.lower():
            print(OP_ANLEITUNG)
        raise SystemExit(1)
    return ["--account", konto]


def op_holen(kennung: str, konto: list[str]) -> dict:
    r = op("item", "get", kennung, "--reveal", "--format", "json", *konto)
    if r.returncode != 0:
        raise SystemExit("1Password: Eintrag nicht lesbar -- " + r.stderr.strip()[-300:])
    return json.loads(r.stdout)


def op_eintraege(konto: list[str]) -> list[dict]:
    """Alle nicht archivierten Eintraege dieses Namens (per Schlagwort gesucht, nicht ueber den ganzen Tresor)."""
    r = op("item", "list", "--tags", OP_SCHLAGWORT, "--format", "json", *konto)
    if r.returncode != 0:
        raise SystemExit("1Password: Liste nicht lesbar -- " + r.stderr.strip()[-300:])
    return [op_holen(e["id"], konto) for e in json.loads(r.stdout or "[]") if e.get("title") == OP_TITEL]


def op_feld(eintrag: dict, label: str | None = None, zweck: str | None = None) -> str | None:
    for f in eintrag.get("fields", []):
        if (zweck and f.get("purpose") == zweck) or (label and f.get("label") == label):
            return f.get("value")
    return None


def op_vorlage(passphrase: str, schluessel: str, summe: str) -> dict:
    abschnitt = {"id": "signing_key", "label": "Update signing key"}
    return {
        "title": OP_TITEL, "category": "PASSWORD", "tags": [OP_SCHLAGWORT, "signing-key"],
        "sections": [abschnitt],
        "fields": [
            {"id": "password", "type": "CONCEALED", "purpose": "PASSWORD", "label": "password",
             "value": passphrase},
            {"id": "notesPlain", "type": "STRING", "purpose": "NOTES", "label": "notesPlain",
             "value": OP_NOTIZ},
            {"id": "private_key", "section": abschnitt, "type": "CONCEALED",
             "label": OP_FELD_SCHLUESSEL, "value": schluessel},
            {"id": "key_sha256", "section": abschnitt, "type": "STRING",
             "label": OP_FELD_SUMME, "value": summe},
            {"id": "key_file", "section": abschnitt, "type": "STRING", "label": "key file on the build PC",
             "value": "%USERPROFILE%\\.forza-signing\\update_signing_key.pkcs8.b64"},
            {"id": "encrypted_copy", "section": abschnitt, "type": "STRING", "label": "encrypted copy",
             "value": "GNAS2 Z:\\Backups\\forza-signing\\update_signing_key.enc.json"},
        ]}


def op_ablegen(konto_wahl: str | None, nur_pruefen: bool) -> int:
    konto = op_konto(konto_wahl)
    schluessel = SCHLUESSEL.read_text(encoding="ascii")
    summe = hashlib.sha256(schluessel.encode("ascii")).hexdigest()
    passphrase = tresor_lesen() or _von_gnas()
    if not passphrase:
        print("Keine Passphrase im Windows-Tresor und keine auf GNAS -- erst ohne --1password sichern.")
        return 1
    vorhanden = op_eintraege(konto)
    aktuell = [e for e in vorhanden if op_feld(e, zweck="PASSWORD") == passphrase
               and op_feld(e, label=OP_FELD_SCHLUESSEL) == schluessel]
    if aktuell:
        eintrag = aktuell[0]
        print(f"1Password: \"{OP_TITEL}\" ist schon aktuell (Tresor {eintrag.get('vault', {}).get('name')})")
    elif nur_pruefen:
        print("  FEHL 1Password: kein aktueller Eintrag" + (" (nur ein veralteter)" if vorhanden else ""))
        return 1
    else:
        # Erst anlegen, DANN das Alte archivieren: scheitert das Anlegen, bleibt das Alte stehen.
        r = op("item", "create", "-", "--format", "json", *konto,
               eingabe=json.dumps(op_vorlage(passphrase, schluessel, summe)))
        if r.returncode != 0:
            print("  FEHL 1Password: Anlegen fehlgeschlagen --", r.stderr.strip()[-300:])
            return 1
        neu = json.loads(r.stdout)
        print(f"1Password: \"{OP_TITEL}\" angelegt (Tresor {neu.get('vault', {}).get('name')})")
        for alt in vorhanden:
            a = op("item", "delete", alt["id"], "--archive", *konto)
            print(f"1Password: veralteten Eintrag {alt['id']} "
                  + ("archiviert" if a.returncode == 0 else "NICHT archiviert -- " + a.stderr.strip()[-200:]))
        eintrag = op_holen(neu["id"], konto)

    # PRUEFEN -- am frisch gelesenen Eintrag, nicht an dem, was geschickt wurde.
    fehler = 0
    aus_op = op_feld(eintrag, zweck="PASSWORD")
    try:
        gnas2 = json.loads((GNAS2 / DATEI).read_text(encoding="utf-8"))
        gnas2_ok = hashlib.sha256(entschluesseln(gnas2, aus_op or "")).hexdigest() == summe
    except Exception:
        gnas2_ok = False
    for text, ok in (
            ("der Schluessel in 1Password gleicht Byte fuer Byte der Datei",
             op_feld(eintrag, label=OP_FELD_SCHLUESSEL) == schluessel),
            ("seine Pruefsumme steht mit im Eintrag", op_feld(eintrag, label=OP_FELD_SUMME) == summe),
            ("die Passphrase in 1Password gleicht der im Windows-Tresor", aus_op == passphrase),
            ("die Sicherung auf GNAS2 laesst sich mit der Passphrase aus 1Password entschluesseln", gnas2_ok)):
        print(("  ok   " if ok else "  FEHL ") + text)
        fehler += 0 if ok else 1
    if not nur_pruefen and GNAS2.exists():
        (GNAS2 / "README.txt").write_text(LIESMICH, encoding="utf-8")
    return 1 if fehler else 0


def op_wiederherstellen(ziel: Path, konto_wahl: str | None) -> int:
    konto = op_konto(konto_wahl)
    eintraege = op_eintraege(konto)
    if not eintraege:
        print(f"1Password: kein Eintrag \"{OP_TITEL}\"")
        return 1
    e = eintraege[0]
    schluessel, summe = op_feld(e, label=OP_FELD_SCHLUESSEL), op_feld(e, label=OP_FELD_SUMME)
    if not schluessel or hashlib.sha256(schluessel.encode("ascii")).hexdigest() != summe:
        print("1Password: Schluessel fehlt oder seine Pruefsumme stimmt nicht")
        return 1
    ziel.parent.mkdir(parents=True, exist_ok=True)
    ziel.write_bytes(schluessel.encode("ascii"))
    print(f"aus 1Password wiederhergestellt nach {ziel}")
    return 0


def main(argv=None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--verify", action="store_true")
    ap.add_argument("--copy-passphrase", action="store_true")
    ap.add_argument("--restore", type=Path)
    ap.add_argument("--1password", dest="einpass", action="store_true",
                    help="Schluessel und Passphrase in 1Password ablegen (mit --verify: nur pruefen)")
    ap.add_argument("--op-account", help="1Password-Konto, wenn die App mehrere kennt")
    ap.add_argument("--restore-from-1password", type=Path)
    args = ap.parse_args(argv)

    if args.restore_from_1password:
        return op_wiederherstellen(args.restore_from_1password, args.op_account)
    if args.einpass:
        return op_ablegen(args.op_account, nur_pruefen=args.verify)

    if args.copy_passphrase:
        p = tresor_lesen()
        if not p:
            print("keine Passphrase in der Anmeldeinformationsverwaltung")
            return 1
        subprocess.run(["clip.exe"], input=p.encode("utf-16-le"), check=True)
        print("Passphrase liegt in der Zwischenablage -- in 1Password einfuegen, dann die "
              "Zwischenablage leeren (z. B. etwas anderes kopieren).")
        return 0

    if args.restore:
        d = json.loads((GNAS2 / DATEI).read_text(encoding="utf-8"))
        p = tresor_lesen() or input("Passphrase: ")
        klar = entschluesseln(d, p)
        if hashlib.sha256(klar).hexdigest() != d["plaintext_sha256"]:
            print("Pruefsumme stimmt nicht")
            return 1
        args.restore.parent.mkdir(parents=True, exist_ok=True)
        args.restore.write_bytes(klar)
        print(f"wiederhergestellt nach {args.restore}")
        return 0

    if not args.verify:
        klar = SCHLUESSEL.read_bytes()
        passphrase = tresor_lesen() or secrets.token_urlsafe(32)
        GNAS2.mkdir(parents=True, exist_ok=True)
        (GNAS2 / DATEI).write_text(json.dumps(verschluesseln(klar, passphrase), indent=1), encoding="utf-8")
        (GNAS2 / "README.txt").write_text(LIESMICH, encoding="utf-8")
        tresor_schreiben(passphrase)
        # Auf GNAS ueber die Standardeingabe -- nicht als Befehlszeile, die in einer
        # Prozessliste oder einem Protokoll landen koennte.
        sys.path.insert(0, str(WS / "scripts"))
        import deploy_gnas as d
        cfg = d.einstellung()
        cfg["host"] = d.erreichbarer_host(cfg)
        ordner = d.geheim_ordner(cfg)
        pfad = f"{ordner}/{GNAS_DATEI}"
        r = subprocess.run(d.ssh_basis(cfg) + [
            f"umask 077; mkdir -p '{ordner}' && chmod 700 '{ordner}' "
            f"&& cat > '{pfad}' && chmod 600 '{pfad}' && stat -c '%a %U' '{pfad}'"],
            input=passphrase, capture_output=True, text=True, timeout=60)
        print(f"GNAS: Passphrase abgelegt ({r.stdout.strip() or r.stderr.strip()[-120:]})")
        print(f"GNAS2: {GNAS2 / DATEI}")

    # PRUEFEN: mit der Passphrase aus dem Tresor UND der von GNAS entschluesseln.
    d_datei = json.loads((GNAS2 / DATEI).read_text(encoding="utf-8"))
    original = hashlib.sha256(SCHLUESSEL.read_bytes()).hexdigest() if SCHLUESSEL.exists() else d_datei["plaintext_sha256"]
    fehler = 0
    for woher, p in (("Windows-Tresor", tresor_lesen()), ("GNAS", _von_gnas())):
        try:
            ok = p is not None and hashlib.sha256(entschluesseln(d_datei, p)).hexdigest() == original
        except Exception:
            ok = False
        print(("  ok   " if ok else "  FEHL ") + f"Sicherung auf GNAS2 laesst sich mit der Passphrase aus {woher} "
              f"zum Original entschluesseln")
        fehler += 0 if ok else 1
    try:
        entschluesseln(d_datei, "falsche-passphrase")
        print("  FEHL eine falsche Passphrase wurde angenommen")
        fehler += 1
    except Exception:
        print("  ok   eine falsche Passphrase wird abgewiesen")
    return 1 if fehler else 0


def _von_gnas() -> str | None:
    sys.path.insert(0, str(WS / "scripts"))
    import deploy_gnas as d
    cfg = d.einstellung()
    cfg["host"] = d.erreichbarer_host(cfg)
    r = subprocess.run(d.ssh_basis(cfg) + [f"cat '{GNAS_PFAD}'"], capture_output=True, text=True, timeout=60)
    return r.stdout.strip() if r.returncode == 0 and r.stdout.strip() else None


if __name__ == "__main__":
    sys.exit(main())
