"""Den Arbeitsbaum auf Geheimnisse durchsuchen, bevor etwas hochgeladen wird.

    python scripts/check_no_secrets.py            # alles, was Git sehen wuerde
    python scripts/check_no_secrets.py --staged   # nur, was gerade committet wird
    python scripts/check_no_secrets.py --alles    # auch ignorierte Dateien

Beendet sich mit Code 1, sobald etwas gefunden wird -- damit taugt es als
Pre-Commit-Haken und als Prüfung vor dem ersten Hochladen.

## Warum es das gibt

Das Repository hat bis zum 2026-09-13 keinen einzigen Commit, es ist also noch nie
etwas hinausgegangen. Genau das ist der Moment, in dem so eine Pruefung eingebaut
gehoert: ein einmal veroeffentlichter Schluessel laesst sich nicht zurueckholen --
er muss getauscht werden, und wer ihn schon kopiert hat, behaelt ihn. Die
Vorgeschichte nachtraeglich zu saeubern ist der schmerzhafte Teil, den wir uns hier
sparen koennen.

In `config/contrib_keys.json` liegen Beitragenden-Schluessel, das Admin-Geheimnis
und das Zugangspasswort im Klartext. Die Datei ist ignoriert; diese Pruefung sorgt
dafuer, dass sie es bleibt und dass nicht anderswo eine Kopie auftaucht.

## Was es NICHT kann

Ein Geheimnis erkennen, das wie normaler Text aussieht. Die Muster hier greifen
lange Zufallszeichenfolgen und die ueblichen Schluesselformate. Ein Passwort
"hunter2" faellt durch -- dagegen hilft nur, Geheimnisse gar nicht erst in Dateien
zu schreiben, die im Baum liegen.
"""

from __future__ import annotations

import argparse
import re
import subprocess
import sys
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent

# Dateien, die per Bauart Geheimnisse tragen: sie duerfen NIE verfolgt werden.
VERBOTEN = {
    "config/contrib_keys.json",
    "config/submit_keys.json",
    "config/admin_secret.txt",
}

MUSTER: list[tuple[str, re.Pattern[str]]] = [
    ("privater Schluessel",
     re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH |PGP )?PRIVATE KEY-----")),
    ("Zuweisung eines Geheimnisses",
     re.compile(r"""(?ix)
        \b(secret|password|passwort|passwd|api[_-]?key|apikey|token|
           auth[_-]?key|private[_-]?key|client[_-]?secret)
        \s*[:=]\s*
        ["'][A-Za-z0-9+/=_\-]{16,}["']""")),
    ("AWS-Zugangsschluessel", re.compile(r"\bAKIA[0-9A-Z]{16}\b")),
    # Der private Update-Schluessel (ECDSA P-256, PKCS#8 in Base64 ohne PEM-Kopf) --
    # er beginnt immer mit dieser Folge. Er gehoert nach ~/.forza-signing und
    # nirgends sonst hin; taucht er im Arbeitsbereich auf, ist das ein Leck.
    ("privater Update-Schluessel (PKCS#8 P-256)",
     re.compile(r"MIG[HI]AgEAMBMGByqGSM49AgEGCCqGSM49AwEH")),
    ("GitHub-Token", re.compile(r"\bgh[pousr]_[A-Za-z0-9]{30,}\b")),
    ("Slack-Token", re.compile(r"\bxox[baprs]-[A-Za-z0-9-]{10,}\b")),
    ("JSON-Web-Token", re.compile(r"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.")),
]

# Was harmlos ist und sonst bei jedem Lauf anschlaegt.
AUSNAHMEN = [
    re.compile(r"falsches?[-_]"),          # Testwerte wie "falsches-admin-geheimnis"
    re.compile(r"(?i)\b(beispiel|example|dummy|placeholder|xxx+|TODO)\b"),
    re.compile(r"(?i)(secret|token|key)\s*[:=]\s*[\"']\s*[\"']"),  # leer
]

TEXTENDUNGEN = {".py", ".ps1", ".cmd", ".sh", ".cs", ".json", ".html", ".md",
                ".txt", ".tsv", ".csv", ".xml", ".yml", ".yaml", ".ini", ".cfg"}

# PRIVATE WOERTER (seit 2026-09-26). Namen, Adressen, Kontonamen -- alles, was eine
# Datei mit einer Person verbindet. Die Liste selbst darf NICHT im Repository
# stehen, sonst veroeffentlichte sie genau das, was sie verhindern soll: sie liegt
# in config/private_words.txt (ignoriert), ein Wort je Zeile, "re:" davor fuer einen
# regulaeren Ausdruck. Dazu kommen die Werte aus config/local.json. Geprueft werden
# ALLE Textdateien und alle Pfade, nicht nur die Endungen oben.
PRIVAT_DATEI = WORKSPACE / "config" / "private_words.txt"


# "wort | README.md, docs/x.md" erlaubt das Wort in genau diesen Dateien (Pfade wie
# git sie nennt) und nirgends sonst.
FREIGABEN: dict[int, set[str]] = {}


def private_muster() -> list[re.Pattern[str]]:
    muster: list[re.Pattern[str]] = []
    try:
        for zeile in PRIVAT_DATEI.read_text(encoding="utf-8-sig").splitlines():
            zeile = zeile.strip()
            if not zeile or zeile.startswith("#"):
                continue
            wort, _, erlaubt = zeile.partition(" | ")
            wort = wort.strip()
            if erlaubt.strip():
                FREIGABEN[len(muster) + 1] = {e.strip() for e in erlaubt.split(",") if e.strip()}
            if wort.startswith("re:"):
                muster.append(re.compile(wort[3:], re.IGNORECASE))
            else:
                muster.append(re.compile(re.escape(wort), re.IGNORECASE))
    except OSError:
        pass
    sys.path.insert(0, str(WORKSPACE / "server"))
    try:
        import local_settings
        for wert in (local_settings.public_host(), local_settings.contact_email()):
            # Deckt schon ein Wort der Liste den Wert ab, gilt dessen Freigabe.
            if wert and len(wert) >= 4 and not any(m.search(wert) for m in muster):
                muster.append(re.compile(re.escape(wert), re.IGNORECASE))
    except Exception:
        pass
    return muster


def ist_text(pfad: Path) -> bool:
    try:
        with pfad.open("rb") as f:
            return b"\0" not in f.read(8192)
    except OSError:
        return False


def dateien(staged: bool, alles: bool) -> list[Path]:
    if staged:
        roh = subprocess.run(["git", "diff", "--cached", "--name-only", "--diff-filter=ACM"],
                             cwd=WORKSPACE, capture_output=True, text=True).stdout
    elif alles:
        roh = subprocess.run(["git", "ls-files", "--others", "--cached"],
                             cwd=WORKSPACE, capture_output=True, text=True).stdout
    else:
        roh = subprocess.run(["git", "ls-files", "--others", "--cached",
                              "--exclude-standard"],
                             cwd=WORKSPACE, capture_output=True, text=True).stdout
    return [WORKSPACE / z.strip() for z in roh.splitlines() if z.strip()]


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--staged", action="store_true")
    parser.add_argument("--alles", action="store_true")
    args = parser.parse_args(argv)

    funde: list[str] = []
    geprueft = 0

    verfolgt = subprocess.run(["git", "ls-files"], cwd=WORKSPACE,
                              capture_output=True, text=True).stdout.splitlines()
    for name in VERBOTEN:
        if name in verfolgt:
            funde.append(f"{name}: diese Datei traegt Geheimnisse und darf nicht "
                         f"verfolgt werden (git rm --cached)")

    privat = private_muster()
    for pfad in dateien(args.staged, args.alles):
        if not pfad.is_file():
            continue
        rel = pfad.relative_to(WORKSPACE).as_posix()
        if privat:
            for nr, m in enumerate(privat, 1):
                if m.search(rel):
                    funde.append(f"{rel}: privates Wort Nr. {nr} im PFAD")
            if pfad.stat().st_size <= 20_000_000 and ist_text(pfad):
                inhalt = pfad.read_text(encoding="utf-8", errors="replace")
                for nr, m in enumerate(privat, 1):
                    if rel in FREIGABEN.get(nr, set()):
                        continue
                    treffer = m.search(inhalt)
                    if treffer:
                        zeile = inhalt.count("\n", 0, treffer.start()) + 1
                        funde.append(f"{rel}:{zeile}: privates Wort Nr. {nr} "
                                     f"(config/private_words.txt bzw. config/local.json)")
        if pfad.suffix.lower() not in TEXTENDUNGEN:
            continue
        if pfad.stat().st_size > 5_000_000:
            continue
        try:
            text = pfad.read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
        geprueft += 1
        for nummer, zeile in enumerate(text.splitlines(), 1):
            if any(a.search(zeile) for a in AUSNAHMEN):
                continue
            for was, muster in MUSTER:
                if muster.search(zeile):
                    funde.append(f"{rel}:{nummer}: {was} -- {zeile.strip()[:90]}")
                    break

    if funde:
        print(f"GEHEIMNISSE GEFUNDEN ({len(funde)}):\n")
        for f in funde:
            print(f"  {f}")
        print("\nNichts hochladen, bevor das geklaert ist. Ein veroeffentlichter "
              "Schluessel laesst sich nicht zurueckholen -- er muss getauscht werden.")
        return 1

    print(f"{geprueft} Dateien geprueft, nichts gefunden.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
