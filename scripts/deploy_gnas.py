"""Die Seite auf das GNAS bringen -- und mitbringen, was dort entstanden ist.

    python scripts/deploy_gnas.py                  Probelauf: was wuerde geschehen
    python scripts/deploy_gnas.py --apply          holen, bauen, schicken, neu starten
    python scripts/deploy_gnas.py --apply --nur-daten   nur Seite/Datensatz/Pakete
    python scripts/deploy_gnas.py --apply --nur-code    nur den Server-Code
    python scripts/deploy_gnas.py --apply --nur-holen
    python scripts/deploy_gnas.py --einrichten     einmalig: Ordner, Image, Dienst
    python scripts/deploy_gnas.py --status         laeuft der Dienst, was liegt dort

## Warum ueberhaupt ein Umzug

Diese Maschine spielt Forza. Sie friert ein, sie startet neu, sie ist tagsueber
belegt -- als Webserver ist sie deshalb die falsche. Das GNAS laeuft ohnehin durch.

## Warum die Richtung nicht nur eine ist

Gescannt und gebaut wird HIER: nur hier laeuft das Spiel, nur hier liegen die
Sweeps. Besucht wird DORT. Damit entstehen an beiden Enden Daten:

    hier -> dort    die gebaute Seite, der Datensatz, das Haptik-Paket, der Code
    dort -> hier    Fremdbeitraege (jemand laedt einen Scan hoch) und der
                    Sichtbarkeits-Schalter (jemand blendet im Admin einen Lauf aus)

Wer nur schickt, ueberschreibt beim naechsten Lauf genau das, was Besucher
beigetragen haben. Darum HOLT dieser Lauf zuerst und schickt danach.

## Der Sichtbarkeits-Schalter und warum er drei Staende vergleicht

`config/dataset_visibility.json` laesst sich an beiden Enden aendern: dort ueber die
Admin-Ansicht, hier ueber das Werkzeug. Ein blosser Vergleich "beide verschieden ->
einer gewinnt" wirft die Aenderung der anderen Seite weg, ohne dass es auffaellt.
Darum liegt unter data/runtime/gnas_sync/ eine Kopie des zuletzt abgeglichenen
Standes. Aus drei Staenden laesst sich sagen, WER geaendert hat -- und wenn beide
es taten, bricht der Lauf ab, statt zu raten.

## Geheimnisse

`config/contrib_keys.json` traegt die Beitragenden-Schluessel und das
Admin-Geheimnis. Die Datei muss dorthin, sonst kann der Server keine Abgabe
pruefen -- aber sie steht in .gitignore, ihr Inhalt wird hier nie ausgegeben, und
sie bekommt drueben 600. Der Zugang selbst laeuft ueber einen Schluessel in
~/.ssh/gnas_deploy, also ausserhalb dieses Baums: das Schloss und der Schluessel
sollen nicht nebeneinander liegen.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import posixpath
import shutil
import subprocess
import urllib.error
import urllib.request
import socket
import sys
import tempfile
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
EINSTELLUNG = WORKSPACE / "config" / "gnas_deploy.json"
ABGLEICH = WORKSPACE / "data" / "runtime" / "gnas_sync"

# Adresse, Benutzername und Zielordner stehen NICHT hier, sondern in
# config/gnas_deploy.json -- die Datei ist in .gitignore.
#
# Keines davon ist ein Geheimnis im engeren Sinn: 192.168.x.x fuehrt von aussen
# nirgendwohin. Aber Adresse, Kontoname und Pfad sind zusammen genau die Angaben,
# die man braucht, um es an einer Tuer zu versuchen -- und sie nuetzen niemandem
# ausser dieser einen Maschine. Was keinen Nutzen hat, veroeffentlicht zu werden,
# wird nicht veroeffentlicht.
VORGABE = {
    "key": str(Path.home() / ".ssh" / "gnas_deploy"),
    "port": 8787,
}
PFLICHT = ("host", "user", "remote_root")

# Der Code, den der Spiegel braucht. Es ist der Inhalt von server/ und sonst
# nichts -- das ist der Grund, warum dieser Ordner ueberhaupt existiert: was auf
# einem erreichbaren Rechner laeuft, soll man an einer Stelle aufzaehlen koennen.
#
# NICHT dabei: build_analytics_site.py (drueben wird nichts gebaut) und
# build_contrib_package.py. Letzteres schnuert das Scanner-Werkzeug aus rund zehn
# OCR- und Fernsteuerungsdateien, die drueben gar nicht liegen -- der Bau muesste
# also scheitern. Das fertige Werkzeug geht stattdessen als dist/-ZIP mit.
CODE = [
    "server/serve_analytics.py",
    "server/analytics_api.py",
    "server/lap_submissions.py",
    # Eingereichte Rennergebnisse (2026-10-02). analytics_api importiert sie -- fehlte sie
    # drueben, startete der ganze Server nicht.
    "server/race_submissions.py",
    # Bestenlisten-Scans aus der App (2026-10-04) -- ebenso von analytics_api importiert.
    "server/scan_submissions.py",
    "server/contrib_format.py",
    "server/import_contrib.py",
    "server/contrib_keys.py",
    # DIE SEITEN NEBEN DEM SERVER.
    #
    # `serve_analytics.py` sucht sie ausdruecklich in SEINEM eigenen Ordner und
    # nicht im Ordner der Auswertungsseite -- der wird alle fuenf Boards neu
    # gebaut, dort haetten sie nichts verloren.
    #
    # Sie gehoeren darum in diese Liste und nicht zu INHALT. Bis zum 2026-09-15
    # stand hier keine davon, waehrend die Auswertungsseite auf /app, /mitmachen
    # und /admin verlinkte: jeder Klick endete in "404 -- app_page.html is
    # missing". Aufgefallen ist es erst, als der Nutzer den Downloadlink anklickte.
    "server/app_page.html",
    "server/contribute_page.html",
    "server/admin_page.html",
    "server/usage.py",
    # Seit 2026-09-24: die Pruefung gegen die Bestenliste und die Sperrliste der
    # Adressen. Fehlten sie drueben, scheiterte JEDE Einreichung am Import.
    "server/leaderboard_check.py",
    "server/ip_bans.py",
    # Besuche, Downloads je Fassung und Laender (2026-09-25).
    "server/visits.py",
    # Die Werte dieser Installation (Name, Kontakt) -- aus config/local.json, die
    # darum mitgeht (nichts Geheimes darin, alles steht auf der Webseite).
    "server/local_settings.py",
    "config/local.json",
    "server/downloads.py",
    "server/geoip.py",
    # Die Autoliste der App (/api/cars, 2026-09-29): der Server baut sie taeglich
    # selbst aus forza.net und dem Wiki; die car_id je Auto kommt aus dieser Datei.
    "server/car_availability.py",
    "config/fh6_car_id_names.json",
    # Die car_ids aus den Dateien des Spiels (scripts/build_car_game_ids.py) -- gehen vor.
    "config/fh6_car_game_ids.json",
    "server/assets/world-110m.json",
    "server/deploy/Dockerfile",
    "server/deploy/docker-compose.yml",
]

# DIE SCHRIFTEN (server/fonts/), seit 2026-09-24 selbst ausgeliefert statt von
# Google. Aus dem Ordner gelesen statt aufgezaehlt: 20 Dateien, und eine vergessene
# waere eine Seite, die still auf eine Ersatzschrift faellt.
CODE += sorted("server/fonts/" + p.name
               for p in (Path(__file__).resolve().parent.parent / "server" / "fonts").glob("*")
               if p.is_file())
# LOGO UND SYMBOLE (server/brand/, seit 2026-09-26): /favicon.ico und /brand/...
CODE += sorted("server/brand/" + p.name
               for p in (Path(__file__).resolve().parent.parent / "server" / "brand").glob("*")
               if p.is_file())

# Die Seite liegt in ihrem eigenen Ordner, und der Server gibt GENAU diesen Ordner
# frei (http.server bekommt page.parent als Wurzel). Drueben liegen darum nur diese
# Dateien -- waehrend data/analytics/ auf dieser Maschine auch Arbeitsstaende wie
# unmatched_car_names_matched.tsv enthaelt. Die haben auf einem erreichbaren Server
# nichts zu suchen, und diese Liste ist der Grund, warum sie nie dorthin kommen.
INHALT = [
    "data/analytics/rivals_auto_wertung.html",
    "data/analytics/rivals_auto_wertung.html.built-from",
    "data/analytics/laps.json",
    "dist/fh-companion-latest.zip",
    # DER ZETTEL NEBEN DER ZIP.
    #
    # Er traegt die Inhaltskennung, die auch IM Paket liegt (app.meta.json).
    # Ohne ihn faellt /api/haptics auf eine Schaetzung aus Dateizeit und
    # Groesse zurueck, nennt gar kein "build" -- und der Selbstaktualisierer
    # der App hat nichts zu vergleichen. Er wuerde still nie aktualisieren,
    # ohne dass irgendwo ein Fehler erschiene.
    "dist/fh-companion-latest.zip.meta.json",
    "dist/forza-contrib-tool.zip",
    # Die Anleitungsvideos fuer die Download-Seite (/guide/...).
    "dist/tutorials/fh-companion-setup-pc.mp4",
    "dist/tutorials/fh-companion-setup-xbox.mp4",
    "dist/tutorials/fh-companion-setup-pc.jpg",
    "dist/tutorials/fh-companion-setup-xbox.jpg",
]

GEHEIM = ["config/contrib_keys.json"]

# Was an beiden Enden entsteht und darum mit drei Staenden verglichen wird.
BEIDSEITIG = ["config/dataset_visibility.json"]

# Was drueben entsteht und hier gebraucht wird.
HOLEN_BAEUME = ["data/memory_scans/contrib"]


class Fehler(RuntimeError):
    pass


def importe_pruefen() -> None:
    """Schickt die Liste alles mit, was der Server-Code importiert?

    ## Warum das eine eigene Pruefung ist

    Am 2026-09-13 bekam `analytics_api.py` einen Import auf das neue
    `lap_submissions.py` -- und die Liste `CODE` wusste nichts davon. Der naechste
    Lauf haette die geaenderte `analytics_api.py` hinuebergeschickt, den Container
    neu gestartet, und der waere beim Import gestorben. Die Seite waere weg gewesen,
    und der Grund haette nur im Container-Protokoll gestanden.

    Das ist genau die Art Fehler, die man beim Schreiben nicht sieht und beim
    Ausliefern zu spaet: hier steht der Code vollstaendig, drueben fehlt eine Datei.

    Die Pruefung kostet nichts -- sie liest die Importe aus dem Syntaxbaum, statt den
    Code auszufuehren -- und sie laeuft VOR dem ersten uebertragenen Byte.
    """
    import ast

    geschickt = {Path(n).name for n in CODE if n.endswith(".py")}
    fehlend = []
    for name in sorted(geschickt):
        datei = WORKSPACE / "server" / name
        if not datei.exists():
            continue
        baum = ast.parse(datei.read_text(encoding="utf-8"))
        for knoten in ast.walk(baum):
            module = []
            if isinstance(knoten, ast.Import):
                module = [a.name for a in knoten.names]
            elif isinstance(knoten, ast.ImportFrom):
                if knoten.module and knoten.level == 0:
                    module = [knoten.module]
            for m in module:
                nachbar = WORKSPACE / "server" / (m.split(".")[0] + ".py")
                if nachbar.exists() and nachbar.name not in geschickt:
                    fehlend.append("%s importiert %s" % (name, nachbar.name))
    if fehlend:
        raise Fehler(
            "Der Server-Code importiert Dateien, die nicht mitgeschickt werden:"
            + chr(10) + "  " + (chr(10) + "  ").join(sorted(set(fehlend)))
            + chr(10) + "Sie gehoeren in die Liste CODE -- sonst stirbt der "
            "Container drueben beim Import und die Seite ist weg.")


def einstellung() -> dict:
    werte = dict(VORGABE)
    if EINSTELLUNG.exists():
        werte.update(json.loads(EINSTELLUNG.read_text(encoding="utf-8")))
    fehlt = [f for f in PFLICHT if not werte.get(f)]
    if fehlt:
        beispiel = EINSTELLUNG.with_name("gnas_deploy.example.json")
        raise Fehler(
            "In " + EINSTELLUNG.as_posix() + " fehlt: " + ", ".join(fehlt) +
            ".\nVorlage: " + beispiel.as_posix() + " -- kopieren und ausfuellen. "
            "Die Datei steht in .gitignore und geht nicht mit hinaus.")
    return werte


_HOST_GEWAEHLT = {}


def erreichbarer_host(cfg: dict) -> str:
    """Den ersten Eintrag aus `host` nehmen, der wirklich antwortet.

    ## Warum eine Liste und nicht eine Adresse

    Eine feste IP ist falsch, sobald DHCP sie neu vergibt. Ein Name ist falsch,
    sobald ihn niemand aufloest -- und genau das ist hier der Fall: dieser Rechner
    haengt an ZWEI Netzen. Die Firmenkarte bringt ihren eigenen DNS mit, der
    antwortet zuerst und sagt zu `GNAS.home` "gibt es nicht", obwohl das Modem am
    anderen Anschluss den Namen kennt.

    Beides einzeln ist also unzuverlaessig, und zwar aus verschiedenen Gruenden.
    Zusammen nicht: der Name gewinnt, wenn er geht, die Adresse faengt ihn auf.

    ## Warum gemerkt wird

    Ein Lauf macht ein Dutzend SSH-Aufrufe. Jeden davon mit einem Fehlversuch am
    Namen zu beginnen, kostet jedesmal die Wartezeit -- einmal pruefen genuegt.
    """
    roh = cfg.get("host")
    kandidaten = [roh] if isinstance(roh, str) else list(roh or [])
    kandidaten = [k for k in kandidaten if k]
    if not kandidaten:
        raise Fehler("In der Einstellung steht kein 'host'.")
    if len(kandidaten) == 1:
        return kandidaten[0]

    schluessel = tuple(kandidaten)
    if schluessel in _HOST_GEWAEHLT:
        return _HOST_GEWAEHLT[schluessel]

    for k in kandidaten:
        probe = subprocess.run(
            ["ssh", "-i", cfg["key"], "-o", "IdentitiesOnly=yes",
             "-o", "BatchMode=yes", "-o", "ConnectTimeout=6",
             "-o", "StrictHostKeyChecking=accept-new",
             cfg["user"] + "@" + k, "true"],
            capture_output=True, text=True)
        if probe.returncode == 0:
            if k != kandidaten[0]:
                print("  (Ausweichweg: %r antwortet nicht, benutze %r)"
                      % (kandidaten[0], k))
            _HOST_GEWAEHLT[schluessel] = k
            return k
    raise Fehler("Keiner dieser Wege fuehrt zum GNAS: " + ", ".join(kandidaten)
                 + "\nLaeuft es? Stimmt der Schluessel in " + cfg["key"] + "?")


def ssh_basis(cfg: dict) -> list:
    return ["ssh", "-i", cfg["key"], "-o", "IdentitiesOnly=yes",
            "-o", "BatchMode=yes", "-o", "ConnectTimeout=15",
            "-o", "StrictHostKeyChecking=accept-new",
            cfg["user"] + "@" + erreichbarer_host(cfg)]


def fern(cfg: dict, befehl: str, binaer: bool = False):
    return subprocess.run(ssh_basis(cfg) + [befehl],
                          capture_output=True, text=not binaer)


def sha(pfad: Path) -> str:
    h = hashlib.sha256()
    with pfad.open("rb") as f:
        for stueck in iter(lambda: f.read(1 << 20), b""):
            h.update(stueck)
    return h.hexdigest()


def ferne_hashes(cfg: dict, namen: list) -> dict:
    """Alle Pruefsummen drueben in EINEM Aufruf.

    Je Datei eine SSH-Sitzung waere je Datei ein Handschlag; bei neun Dateien sind
    das ueber zehn Sekunden, in denen nichts uebertragen wird.
    """
    root = cfg["remote_root"]
    liste = " ".join("'" + root + "/" + n + "'" for n in namen)
    aus = fern(cfg, "for f in " + liste + "; do "
                    "if [ -f \"$f\" ]; then sha256sum \"$f\"; "
                    "else echo \"- $f\"; fi; done")
    ergebnis = {}
    for zeile in aus.stdout.splitlines():
        teile = zeile.split(None, 1)
        if len(teile) != 2:
            continue
        summe, pfad = teile[0], teile[1].strip()
        name = pfad[len(root) + 1:] if pfad.startswith(root + "/") else pfad
        ergebnis[name] = summe
    return ergebnis


def schicken(cfg: dict, namen: list, apply: bool, geheim: bool = False) -> int:
    fehlend = [n for n in namen if not (WORKSPACE / n).exists()]
    for n in fehlend:
        print("  fehlt hier, uebersprungen: " + n)
    namen = [n for n in namen if n not in fehlend]
    if not namen:
        return 0

    drueben = ferne_hashes(cfg, namen)
    faellig = []
    for n in namen:
        hier = sha(WORKSPACE / n)
        if drueben.get(n) != hier:
            faellig.append(n)
            groesse = (WORKSPACE / n).stat().st_size / 1e6
            was = "neu" if drueben.get(n, "-") == "-" else "geaendert"
            # Der Inhalt eines Geheimnisses wird nie ausgegeben -- die Tatsache,
            # dass es sich geaendert hat, schon: sonst waere nicht zu sehen, ob
            # ein neu eingetragener Beitragender drueben ueberhaupt ankommt.
            print("  -> %s  (%s, %.1f MB)" % (n, was, groesse))
        else:
            print("     " + n + "  (gleich)")

    if not apply or not faellig:
        return len(faellig)

    root = cfg["remote_root"]
    ordner = sorted({Path(n).parent.as_posix() for n in faellig})
    fern(cfg, "mkdir -p " + " ".join("'" + root + "/" + o + "'" for o in ordner))
    for n in faellig:
        ziel = (cfg["user"] + "@" + erreichbarer_host(cfg) + ":"
                + root + "/" + n)
        # -p haelt die Aenderungszeit fest. Das ist keine Kosmetik: der
        # Download-Name des Haptik-Pakets wird aus der mtime gebildet
        # (fh-companion-JJJJMMTT.zip). Ohne -p hiesse jedes Paket nach dem
        # Datum seiner Uebertragung statt nach dem seines Baus.
        r = subprocess.run(["scp", "-p", "-q", "-i", cfg["key"],
                            "-o", "IdentitiesOnly=yes", "-o", "BatchMode=yes",
                            "-o", "StrictHostKeyChecking=accept-new",
                            str(WORKSPACE / n), ziel],
                           capture_output=True, text=True)
        if r.returncode != 0:
            raise Fehler("scp " + n + ": " + r.stderr.strip())
    if geheim:
        fern(cfg, "chmod 600 " + " ".join("'" + root + "/" + n + "'" for n in faellig))
    return len(faellig)


def holen_baeume(cfg: dict, apply: bool) -> int:
    """Fremdbeitraege abholen -- nur die Laeufe, die es hier noch nicht gibt.

    Ein rohes Auspacken ueber den Baum wuerde einen hier bereits eingelesenen und
    vielleicht korrigierten Lauf mit dem Stand von drueben ueberschreiben. Darum
    erst in einen Nebenordner, dann nur das Fehlende hinueber.
    """
    root = cfg["remote_root"]
    neu = 0
    for baum in HOLEN_BAEUME:
        p = subprocess.run(
            ssh_basis(cfg) + ["cd '" + root + "' && [ -d '" + baum + "' ] && "
                              "tar -cz '" + baum + "' 2>/dev/null || true"],
            capture_output=True)
        if not p.stdout:
            print("  " + baum + ": nichts drueben")
            continue
        with tempfile.TemporaryDirectory() as tmp:
            t = subprocess.run(["tar", "-xz", "-C", tmp], input=p.stdout,
                               capture_output=True)
            if t.returncode != 0:
                print("  " + baum + ": nicht auszupacken")
                continue
            quelle = Path(tmp) / baum
            if not quelle.exists():
                continue
            # ERST NACH dem Packen gelesen: der Scan-Eingang setzt einen zurueckgehaltenen
            # Lauf auf die Liste, BEVOR er ihn in contrib/ zieht -- jeder Lauf im Paket
            # steht darum schon in dieser Fassung.
            zurueck = ferne_zurueckgehaltene(cfg)
            # contrib/<wer>/<lauf>/ -- der Lauf ist die Einheit.
            for lauf in sorted(quelle.glob("*/*")):
                if not lauf.is_dir():
                    continue
                rel = lauf.relative_to(Path(tmp))
                ziel = WORKSPACE / rel
                if ziel.exists():
                    continue
                # Ein App-Scan, den das Tor drueben zurueckhaelt (oder der Admin
                # ausgeblendet hat), kommt erst her, wenn er eingeblendet ist. Holte man
                # ihn jetzt, laege er hier ohne seinen Eintrag, sobald der Abgleich der
                # Liste scheitert (beide Seiten geaendert, Verbindung weg) -- und jeder
                # Bau bis dahin naehme ihn mit.
                if lauf.parent.name.startswith("app-") and (zurueck is None or lauf.name in zurueck):
                    print(("  Liste drueben nicht lesbar, App-Scan nicht geholt: " if zurueck is None
                           else "  zurueckgehalten drueben, nicht geholt: ") + rel.as_posix())
                    continue
                neu += 1
                print("  <- " + rel.as_posix())
                if apply:
                    ziel.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copytree(lauf, ziel)
    if neu == 0:
        print("  keine neuen Fremdbeitraege")
    return neu


def ferne_zurueckgehaltene(cfg: dict) -> set | None:
    """Die run_ids, die drueben auf der Sichtbarkeitsliste stehen -- None, wenn unklar.

    Unklar (keine Verbindung, kaputte Datei) heisst fuer App-Scans: diesmal keinen holen.
    """
    p = fern(cfg, "cat '" + cfg["remote_root"] + "/" + BEIDSEITIG[0] + "' 2>/dev/null || true",
             binaer=True)
    if p.returncode != 0:
        return None
    if not (p.stdout or b"").strip():
        return set()
    try:
        data = json.loads(p.stdout.decode("utf-8-sig"))
        return {e if isinstance(e, str) else str(e["run_id"])
                for e in data.get("hidden", []) if isinstance(e, str) or e.get("run_id")}
    except Exception:
        return None


def abgleich_beidseitig(cfg: dict, apply: bool) -> bool:
    """Den Sichtbarkeits-Schalter zwischen beiden Enden fuehren.

    Rueckgabe: True, wenn hier etwas uebernommen wurde -- dann ist die gebaute
    Seite veraltet und muss neu gebaut werden, bevor sie zurueckgeht.
    """
    root = cfg["remote_root"]
    ABGLEICH.mkdir(parents=True, exist_ok=True)
    uebernommen = False
    for name in BEIDSEITIG:
        hier = WORKSPACE / name
        merk = ABGLEICH / Path(name).name
        p = fern(cfg, "cat '" + root + "/" + name + "' 2>/dev/null || true",
                 binaer=True)
        drueben_roh = p.stdout or b""

        h_hier = sha(hier) if hier.exists() else None
        h_merk = sha(merk) if merk.exists() else None
        h_dort = hashlib.sha256(drueben_roh).hexdigest() if drueben_roh else None

        if h_hier == h_dort:
            print("  " + name + ": gleich")
            if apply and drueben_roh and h_merk != h_dort:
                merk.write_bytes(drueben_roh)
            continue

        hier_neu = h_hier != h_merk
        dort_neu = h_dort != h_merk

        if dort_neu and not hier_neu:
            print("  " + name + ": drueben geaendert -> hier uebernehmen")
            uebernommen = True
            if apply:
                hier.parent.mkdir(parents=True, exist_ok=True)
                hier.write_bytes(drueben_roh)
                merk.write_bytes(drueben_roh)
        elif hier_neu and not dort_neu:
            print("  " + name + ": hier geaendert -> mitschicken")
            if apply and hier.exists():
                schicken(cfg, [name], apply=True)
                merk.write_bytes(hier.read_bytes())
        else:
            nebenan = ABGLEICH / (Path(name).name + ".drueben")
            nebenan.write_bytes(drueben_roh)
            raise Fehler(
                name + " wurde an BEIDEN Enden geaendert. Automatisch zu mischen "
                "hiesse, eine der beiden Entscheidungen stillschweigend zu "
                "verwerfen. Der Stand von drueben liegt jetzt in " +
                nebenan.as_posix() + " -- von Hand zusammenfuehren, dann erneut "
                "laufen lassen.")
    return uebernommen


def seite_veraltet() -> bool:
    sys.path.insert(0, str(WORKSPACE / "server"))
    import serve_analytics as srv  # noqa: E402
    seite = WORKSPACE / "data/analytics/rivals_auto_wertung.html"
    if not seite.exists():
        return True
    quelle = srv.newest_source(WORKSPACE / "data/memory_scans/full_sweep")
    return quelle > srv.built_from(seite)


def bauen() -> None:
    # EINE STUNDE, NICHT EINE MINUTE. Hier stand bis zum 2026-09-15 "etwa eine
    # Minute" -- das galt bei einer Handvoll Boards. Am 2026-09-15 mit 598 Boards
    # und 7,2 Mio. Rohzeilen gemessen: ueber 70 Minuten, und `dataset.build()` gibt
    # dabei keine einzige Zeile aus. Wer das nicht weiss, haelt den Lauf fuer
    # haengend und bricht ab -- genau das ist mir passiert.
    #
    # Wenn nur Code auszuliefern ist, spart `--nur-code --kein-bau` die ganze Zeit.
    print("  Die Seite wird gebaut. Das dauert bei diesem Datenbestand deutlich")
    print("  laenger als eine Stunde und meldet sich zwischendurch NICHT.")
    print("  Nur Code ausliefern? Dann: --nur-code --kein-bau")
    r = subprocess.run([sys.executable,
                        str(WORKSPACE / "scripts" / "build_analytics_site.py")],
                       cwd=str(WORKSPACE))
    if r.returncode != 0:
        raise Fehler("der Bau der Seite ist fehlgeschlagen -- es wurde nichts geschickt")


def einrichten(cfg: dict) -> int:
    root = cfg["remote_root"]
    print("GNAS einrichten unter " + root)
    ordner = ["server/deploy", "config", "dist", "data/analytics",
              "data/runtime", "data/memory_scans/contrib"]
    befehl = ("mkdir -p " + " ".join("'" + root + "/" + o + "'" for o in ordner) +
              " && chmod 700 '" + root + "/config' && echo bereit")
    p = fern(cfg, befehl)
    print("  " + (p.stdout.strip() or p.stderr.strip()))
    if "bereit" not in p.stdout:
        raise Fehler("die Ordner liessen sich nicht anlegen")

    importe_pruefen()
    print("  Code und Inhalt schicken")
    schicken(cfg, CODE + INHALT, apply=True)
    schicken(cfg, GEHEIM, apply=True, geheim=True)

    print("  Image bauen und Dienst starten (dauert, pandas wird geholt)")
    p = fern(cfg, "cd '" + root + "/server/deploy' && "
                  "docker compose up -d --build 2>&1 | tail -8")
    print("  " + (p.stdout or p.stderr).strip().replace("\n", "\n  "))
    return status(cfg)


def geheim_ordner(cfg: dict) -> str:
    """Der Ordner fuer Schluessel und Zertifikate auf dem GNAS: neben remote_root.

    docker-compose.yml bindet ihn relativ ein (../../../forza-secrets von
    server/deploy aus) -- beide Stellen muessen dasselbe meinen.
    """
    if cfg.get("secrets_dir"):
        return str(cfg["secrets_dir"]).rstrip("/")
    return posixpath.dirname(str(cfg["remote_root"]).rstrip("/")) + "/forza-secrets"


def adresse_pruefen(cfg: dict) -> None:
    """Zeigt die oeffentliche Adresse noch auf diesen Anschluss?

    ## Warum das hierher gehoert

    Die App sucht den Server unter seinem DNS-Namen (public_url) und nicht unter einer
    IP-Adresse -- gerade WEIL die IP sich aendert. Aktualisiert wird der Eintrag von
    aussen; dieses Projekt hat darauf keinen Zugriff und soll ihn auch nicht haben.

    Was es aber kann, ist hinsehen. Driftet der Eintrag ab, laeuft hier alles weiter
    wie bisher und NUR die App draussen findet den Server nicht mehr -- ein Fehler,
    der sich auf dieser Seite durch nichts bemerkbar macht. Eine Zeile im Protokoll
    kostet nichts und beantwortet die Frage jedesmal.

    Es wird nur GEMELDET, nie eingegriffen: eine misslungene Namensaufloesung ist
    kein Grund, eine fertige Auslieferung nachtraeglich schlechtzureden.
    """
    url = cfg.get("public_url") or ""
    name = url.split("//", 1)[-1].split(":", 1)[0].split("/", 1)[0]
    if not name or name.replace(".", "").isdigit():
        return
    try:
        zeigt_auf = socket.gethostbyname(name)
    except OSError as fehler:
        print("  %s laesst sich nicht aufloesen (%s)" % (name, fehler))
        return
    try:
        with urllib.request.urlopen("https://api.ipify.org", timeout=20) as antwort:
            hier = antwort.read().decode("ascii", "replace").strip()
    except Exception:
        print("  %s -> %s (die eigene oeffentliche Adresse war nicht zu ermitteln)"
              % (name, zeigt_auf))
        return
    if zeigt_auf == hier:
        print("  %s -> %s, stimmt mit diesem Anschluss ueberein" % (name, zeigt_auf))
    elif _antwortet_dort(url):
        # DIE ADRESSEN WEICHEN AB, DER SERVER ANTWORTET TROTZDEM. Am 2026-09-25 ging
        # dieser Rechner ueber einen anderen Anschluss hinaus (165.85.x statt 81.204.x),
        # der Server stand aber unveraendert unter dem Namen. Die blosse Abweichung
        # als "die App findet den Server nicht mehr" zu melden war ein Fehlalarm;
        # entscheidend ist, ob unter dem Namen wirklich dieser Server antwortet.
        print("  %s -> %s, dieser Rechner geht ueber %s hinaus (VPN?); der Server "
              "antwortet unter dem Namen -- in Ordnung" % (name, zeigt_auf, hier))
    else:
        print("  ACHTUNG: %s zeigt auf %s, dieser Anschluss ist %s, und dort "
              "antwortet kein forza-site." % (name, zeigt_auf, hier))
        print("           Die App findet den Server dann nicht mehr. Der Eintrag "
              "wird von aussen aktualisiert -- dort nachsehen.")


def _antwortet_dort(url: str) -> bool:
    """Antwortet unter der oeffentlichen Adresse wirklich dieser Server?"""
    try:
        anfrage = urllib.request.Request(url.rstrip("/") + "/", method="HEAD")
        with urllib.request.urlopen(anfrage, timeout=20) as antwort:
            return antwort.headers.get("Server", "").startswith("forza-site")
    except urllib.error.HTTPError as fehler:
        return (fehler.headers.get("Server", "") or "").startswith("forza-site")
    except Exception:
        return False


def status(cfg: dict) -> int:
    root, port = cfg["remote_root"], str(cfg["port"])
    befehl = "\n".join([
        'echo "== Dienst =="',
        "docker ps --filter name=forza-site "
        "--format '{{.Status}} | {{.Ports}}' 2>/dev/null || echo 'kein Docker-Zugriff'",
        'echo "== Antwort =="',
        "curl -s -o /dev/null -w 'HTTP %{http_code} nach %{time_total}s\\n' "
        "http://127.0.0.1:" + port + "/ || echo 'keine Antwort'",
        'echo "== Stand =="',
        "curl -s http://127.0.0.1:" + port + "/api/summary 2>/dev/null | head -20",
        'echo "== Platz =="',
        "du -sh '" + root + "' 2>/dev/null; df -h '" + root + "' | tail -1",
    ])
    p = fern(cfg, befehl)
    print(p.stdout.strip() or p.stderr.strip())
    print("\nIm LAN:   http://" + erreichbarer_host(cfg) + ":" + port + "/")
    if cfg.get("public_url"):
        # Die Adresse, unter der die App den Server sucht. Sie steht in der
        # Einstellung und nicht hier, weil sie sich aendern kann, ohne dass
        # dieses Skript etwas davon wissen muesste.
        print("Im Netz:  " + cfg["public_url"])
        adresse_pruefen(cfg)
    return 0


def main(argv: list | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--apply", action="store_true", help="wirklich tun")
    parser.add_argument("--einrichten", action="store_true")
    parser.add_argument("--status", action="store_true")
    parser.add_argument("--nur-holen", action="store_true")
    parser.add_argument("--nur-schicken", action="store_true")
    # CODE UND DATEN SIND ZWEI ENTSCHEIDUNGEN.
    #
    # Am 2026-09-13 wollte ich nur den frischen Datensatz hinueberbringen, damit
    # die Seite beim Umstellen der Portweiterleitung nicht elf Stunden alt ist.
    # Der Lauf hat dabei auch `lap_submissions.py` mitgenommen -- und damit eine
    # Schnittstelle freigeschaltet, von der ausdruecklich vereinbart war, dass sie
    # noch NICHT offen sein soll.
    #
    # Der Grund war nicht Unachtsamkeit im Einzelfall, sondern dass es die
    # Unterscheidung gar nicht gab: `--apply` hiess "alles". Eine Entscheidung, die
    # man nicht ausdruecken kann, trifft man versehentlich.
    parser.add_argument("--nur-daten", action="store_true",
                        help="nur Seite, Datensatz und Pakete -- KEIN Code, "
                             "kein Neustart des Dienstes")
    parser.add_argument("--nur-code", action="store_true",
                        help="nur den Server-Code -- keine Daten")
    parser.add_argument("--kein-bau", action="store_true",
                        help="die Seite nicht bauen, auch wenn sie veraltet ist")
    args = parser.parse_args(argv)
    try:
        cfg = einstellung()
    except Fehler as e:
        print("ABBRUCH: " + str(e))
        return 2

    if not Path(cfg["key"]).exists():
        print("Kein Schluessel unter " + cfg["key"] + ".\nAnlegen und eintragen:\n"
              "  ssh-keygen -t ed25519 -f \"" + cfg["key"] + "\" -N \"\"\n"
              "  ssh-copy-id -i \"" + cfg["key"] + ".pub\" " +
              cfg["user"] + "@" + cfg["host"])
        return 2

    try:
        if args.status:
            return status(cfg)
        if args.einrichten:
            return einrichten(cfg)

        if not args.nur_schicken:
            print("HOLEN (was drueben entstanden ist)")
            neu = holen_baeume(cfg, args.apply)
            uebernommen = abgleich_beidseitig(cfg, args.apply)
            if (neu or uebernommen) and args.apply and not args.kein_bau:
                print("\nBAUEN (Fremdbeitraege oder Sichtbarkeit haben sich geaendert)")
                bauen()

        if not args.nur_holen:
            # Bei --nur-code geht die Seite gar nicht mit -- sie dafuer zu
            # bauen waere eine Minute Rechenzeit fuer nichts, und auf dieser
            # Maschine konkurriert sie mit der OCR des laufenden Sweeps.
            if (args.apply and not args.kein_bau and not args.nur_code
                    and seite_veraltet()):
                print("\nBAUEN (die Seite ist hinter ihren Quellen zurueck)")
                bauen()
            code_faellig = 0
            if not args.nur_daten:
                importe_pruefen()
                print("\nSCHICKEN (Code)")
                code_faellig = schicken(cfg, CODE, args.apply)
            else:
                print("\nCODE wird NICHT geschickt (--nur-daten)")
            if not args.nur_code:
                print("SCHICKEN (Seite, Datensatz, Pakete)")
                schicken(cfg, INHALT, args.apply)
                print("SCHICKEN (Schluessel -- der Inhalt wird nicht ausgegeben)")
                schicken(cfg, GEHEIM, args.apply, geheim=True)
            else:
                print("DATEN werden NICHT geschickt (--nur-code)")

            if args.apply:
                # Nur bei Code neu starten: Seite und Datensatz werden bei jedem
                # Aufruf frisch vom Datentraeger gelesen, ein Neustart brauchte es
                # dafuer nicht -- und jeder Neustart wirft einen laufenden Upload weg.
                if code_faellig:
                    print("\nDienst neu starten (der Code hat sich geaendert)")
                    # RESTART UND NICHT "up -d".
                    #
                    # Der Code liegt als eingehaengter Ordner im Container, nicht im
                    # Image. `up -d` vergleicht die Compose-Angaben -- die sind
                    # unveraendert, also passiert NICHTS, und der Server laeuft mit
                    # dem Modul weiter, das er beim Start geladen hat. Genau so ging
                    # am 2026-09-13 eine neue Schnittstelle raus, die drueben
                    # weiterhin "does not exist here" antwortete. `restart` beendet
                    # den Prozess und laedt neu -- das ist hier der ganze Zweck.
                    # ERST `up -d`, DANN `restart`. `up -d` allein laedt den Code
                    # nicht neu (siehe oben); `restart` allein uebernimmt keine
                    # geaenderte Compose-Datei -- neue Einhaengungen oder Befehle
                    # (am 2026-09-24 die TLS-Pfade) blieben dann still aus. `up -d`
                    # erneuert den Container nur, wenn sich die Angaben geaendert
                    # haben, und rueckt das Profil "tls" nicht an.
                    # SCHREIBRECHTE NUR FUER DEN EIGENTUEMER. scp -p uebernimmt die
                    # Rechte von Windows, und die kamen als rw-rw-rw- an: jedes andere
                    # Konto auf dem NAS haette den Servercode aendern koennen.
                    p = fern(cfg, "chmod -R go-w '" + cfg["remote_root"] + "' && "
                                  "cd '" + cfg["remote_root"] + "/server/deploy' && "
                                  "mkdir -p '" + geheim_ordner(cfg) + "/tls' && "
                                  "docker compose up -d forza-site 2>&1 | tail -3 && "
                                  "docker compose restart forza-site 2>&1 | tail -3")
                    print("  " + (p.stdout or p.stderr).strip())
                else:
                    print("\nKein Neustart noetig (nur Daten haben sich geaendert)")

        adresse_pruefen(cfg)

        if not args.apply:
            print("\nProbelauf. Mit --apply wird es getan.")
        return 0
    except Fehler as e:
        print("\nABBRUCH: " + str(e))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
