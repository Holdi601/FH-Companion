"""Eine neue Fassung von FH Companion in Discord ankuendigen (seit 2026-10-18).

    python scripts/discord_announce.py            # ankuendigen, wenn die Fassung neu ist
    python scripts/discord_announce.py --dry-run  # nur zeigen, was gesendet wuerde
    python scripts/discord_announce.py --force    # auch eine schon angekuendigte Fassung

Der Webhook steht in config/discord_keys.json (wie alle *_keys.json NIE im Repository):

    {"release_webhook": "https://discord.com/api/webhooks/<id>/<token>"}

deploy_gnas.py ruft announce() nach jedem Deploy. Gesendet wird nur, wenn die
ausgelieferte App (dist/fh-companion-latest.zip.meta.json, "build") eine andere ist als
die zuletzt angekuendigte -- ein Deploy nur der Seite oder des Datensatzes kuendigt
nichts an. Inhalt: die Commits seit der letzten Ankuendigung, nach "New" und "Fixes"
getrennt, und der Link zum Download.
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
import urllib.error
import urllib.request
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
SCHLUESSEL = WORKSPACE / "config" / "discord_keys.json"
META = WORKSPACE / "dist" / "fh-companion-latest.zip.meta.json"
LOKAL = WORKSPACE / "config" / "local.json"


def download_adresse() -> str:
    """Die Download-Seite -- die Adresse des Servers steht nur in config/local.json (gitignored)."""
    try:
        server = str(json.loads(LOKAL.read_text(encoding="utf-8")).get("server") or "").rstrip("/")
    except (OSError, ValueError):
        server = ""
    return server + "/app" if server else ""
# Ohne bisherige Ankuendigung: was seit dieser Zeit gebaut wurde.
ERSTE_SEIT = "2026-10-11"
HOECHSTENS = 15
# Commits, die kein Nutzer sehen muss.
UNWICHTIG = re.compile(r"^(merge\b|deploy:|readme\b|docs?:|tests?:)", re.I)
FIX = re.compile(r"\b(fix|fixed|fixes|bug|no longer|correct(ed|ly)?|wrong|broken|missing|repair)", re.I)


def _lies() -> dict:
    try:
        return json.loads(SCHLUESSEL.read_text(encoding="utf-8-sig"))
    except Exception:
        return {}


def _git(*args: str) -> str:
    return subprocess.run(["git", *args], cwd=WORKSPACE, capture_output=True, text=True,
                          encoding="utf-8").stdout


def commits(seit_commit: str | None) -> list[str]:
    """Die Betreffzeilen seit `seit_commit` (oder seit ERSTE_SEIT), aelteste zuletzt."""
    if seit_commit and _git("cat-file", "-t", seit_commit).strip() == "commit":
        roh = _git("log", "--no-merges", "--format=%s", seit_commit + "..HEAD")
    else:
        roh = _git("log", "--no-merges", "--format=%s", "--since=" + ERSTE_SEIT)
    return [z.strip() for z in roh.splitlines() if z.strip() and not UNWICHTIG.match(z.strip())]


def nachricht(meta: dict, zeilen: list[str]) -> dict:
    neu = [z for z in zeilen if not FIX.search(z)]
    fixes = [z for z in zeilen if FIX.search(z)]

    def liste(xs: list[str]) -> str:
        teil = ["- " + x for x in xs[:HOECHSTENS]]
        if len(xs) > HOECHSTENS:
            teil.append("- ... and %d more" % (len(xs) - HOECHSTENS))
        return "\n".join(teil)[:1000]

    felder = []
    if neu:
        felder.append({"name": "New", "value": liste(neu)})
    if fixes:
        felder.append({"name": "Fixes", "value": liste(fixes)})
    fassung = str(meta.get("name") or "").replace("fh-companion-", "").replace(".zip", "")
    return {
        "username": "FH Companion",
        "embeds": [{
            "title": "FH Companion %s is out" % (fassung or meta.get("build", "")),
            "url": download_adresse() or None,
            "description": ("The app updates itself on its next start, or download it here: %s"
                            % download_adresse()),
            "color": 0x7DD3FC,
            "fields": felder or [{"name": "Changes", "value": "Small fixes."}],
            "footer": {"text": "build %s" % meta.get("build", "?")},
        }],
    }


def senden(url: str, inhalt: dict) -> None:
    anfrage = urllib.request.Request(
        url, data=json.dumps(inhalt).encode("utf-8"), method="POST",
        headers={"Content-Type": "application/json", "User-Agent": "FHCompanion-Release/1.0"})
    with urllib.request.urlopen(anfrage, timeout=30) as antwort:
        if antwort.status not in (200, 204):
            raise RuntimeError("Discord answered HTTP %d" % antwort.status)


def announce(dry_run: bool = False, force: bool = False) -> str:
    """Ankuendigen, wenn die Fassung neu ist. Gibt zurueck, was geschah. Wirft nie."""
    try:
        keys = _lies()
        url = str(keys.get("release_webhook") or "")
        if not url:
            return "no release_webhook in config/discord_keys.json -- nothing announced"
        meta = json.loads(META.read_text(encoding="utf-8"))
        build = str(meta.get("build") or "")
        if not force and build and build == keys.get("last_build"):
            return "build %s was already announced" % build
        zeilen = commits(keys.get("last_commit"))
        inhalt = nachricht(meta, zeilen)
        if dry_run:
            return json.dumps(inhalt, indent=1, ensure_ascii=False)
        senden(url, inhalt)
        keys["last_build"] = build
        keys["last_commit"] = _git("rev-parse", "HEAD").strip()
        SCHLUESSEL.write_text(json.dumps(keys, indent=2, ensure_ascii=False), encoding="utf-8")
        return "announced build %s with %d change(s)" % (build, len(zeilen))
    except Exception as e:
        return "not announced: %s" % e


def main(argv: list | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--dry-run", action="store_true")
    p.add_argument("--force", action="store_true")
    a = p.parse_args(argv)
    print(announce(dry_run=a.dry_run, force=a.force))
    return 0


if __name__ == "__main__":
    sys.exit(main())
