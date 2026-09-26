"""Geheimnisse fuer Beitragende anlegen, ansehen und zurueckziehen.

    python server/contrib_keys.py list
    python server/contrib_keys.py add kai            # legt ein Geheimnis an
    python server/contrib_keys.py show kai           # zeigt es, zum Weitergeben
    python server/contrib_keys.py revoke kai         # zieht es zurueck
    python server/contrib_keys.py admin              # Admin-Geheimnis (Webseite)

Alles landet in `config/contrib_keys.json`. **Diese Datei ist der Schluesselbund** --
wer sie hat, kann Daten einspielen und die Admin-Ansicht bedienen.

## Warum je Person ein eigenes Geheimnis

Ein gemeinsames muesste bei jedem Zerwuerfnis fuer alle getauscht werden, und der
Server koennte nicht sagen, wer etwas geschickt hat. Mit einem Geheimnis je Person
ist "Kai zurueckziehen" eine Zeile, und die Unterschrift sagt selbst, von wem sie ist.

## Wie das Geheimnis zum Freund kommt

Ueber einen Kanal, der nicht das Netz ist, ueber das er spaeter hochlaedt -- Signal,
ein Anruf, ein Zettel. Es wird nie uebertragen, wenn er hochlaedt: das Werkzeug
signiert damit, und der Server rechnet die Signatur nach.
"""

from __future__ import annotations

import argparse
import json
import secrets
import sys
from datetime import datetime
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from contrib_format import ContribError, check_contributor  # noqa: E402

WORKSPACE = Path(__file__).resolve().parent.parent
KEYS_FILE = WORKSPACE / "config/contrib_keys.json"

TEMPLATE = {
    "_": ("Schluesselbund fuer Fremdbeitraege. 'keys' haelt je Beitragendem ein "
          "Geheimnis, 'admin' das fuer die Admin-Ansicht der Webseite. Gepflegt mit "
          "server/contrib_keys.py. Nicht weitergeben und nicht in ein oeffentliches "
          "Repository legen."),
    "keys": {},
    "admin": "",
    # Der Notschalter: ein langes Passwort, das in jedem ausgegebenen Paket steckt.
    # Neu erzeugen macht alle bisher verteilten Pakete ungueltig.
    "upload_password": "",
    "issued": {},
}


# 192 Zufallsbytes ergeben in der URL-sicheren Schreibweise genau 256 Zeichen.
GATE_BYTES = 192


def load(path: Path) -> dict:
    if not path.exists():
        return json.loads(json.dumps(TEMPLATE))
    data = json.loads(path.read_text(encoding="utf-8-sig"))
    data.setdefault("keys", {})
    data.setdefault("issued", {})
    data.setdefault("admin", "")
    data.setdefault("upload_password", "")
    return data


def save(path: Path, data: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, indent=1, ensure_ascii=False), encoding="utf-8")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("action",
                        choices=["list", "add", "show", "revoke", "admin", "gate"])
    parser.add_argument("name", nargs="?")
    parser.add_argument("--keys", type=Path, default=KEYS_FILE)
    parser.add_argument("--rotate", action="store_true",
                        help="bei add/admin: ein vorhandenes Geheimnis ersetzen")
    args = parser.parse_args(argv)

    data = load(args.keys)

    if args.action == "list":
        if not data["keys"]:
            print("noch niemand eingetragen.")
        for name in sorted(data["keys"]):
            when = data["issued"].get(name, "?")
            print(f"  {name:<20} angelegt {when}")
        print(f"  {'(admin)':<20} "
              + ("gesetzt" if data.get("admin") else "NICHT gesetzt"))
        gate = data.get("upload_password") or ""
        print(f"  {'(Zugangspasswort)':<20} "
              + (f"gesetzt, {len(gate)} Zeichen" if gate else "NICHT gesetzt"))
        return 0

    if args.action == "gate":
        if data.get("upload_password") and not args.rotate:
            print("Zugangspasswort steht ("
                  f"{len(data['upload_password'])} Zeichen):")
            print(f"  {data['upload_password']}")
            print("Mit --rotate neu erzeugen -- danach gilt KEIN bisher "
                  "ausgegebenes Paket mehr.")
            return 0
        data["upload_password"] = secrets.token_urlsafe(GATE_BYTES)
        data["issued"]["(gate)"] = datetime.now().astimezone().isoformat(
            timespec="seconds")
        save(args.keys, data)
        print(f"Neues Zugangspasswort ({len(data['upload_password'])} Zeichen). "
              "Jedes bisher ausgegebene Paket ist ab sofort ungueltig:")
        print(f"  {data['upload_password']}")
        return 0

    if args.action == "admin":
        if data.get("admin") and not args.rotate:
            print("Admin-Geheimnis steht schon. Mit --rotate ersetzen.")
            return 1
        data["admin"] = secrets.token_urlsafe(32)
        data["issued"]["(admin)"] = datetime.now().astimezone().isoformat(
            timespec="seconds")
        save(args.keys, data)
        print("Admin-Geheimnis (fuer die Admin-Ansicht der Webseite):")
        print(f"  {data['admin']}")
        return 0

    if not args.name:
        print(f"{args.action} braucht einen Namen.")
        return 2
    try:
        name = check_contributor(args.name)
    except ContribError as error:
        print(error)
        return 2

    if args.action == "add":
        if name in data["keys"] and not args.rotate:
            print(f"{name} hat schon ein Geheimnis. Mit --rotate ersetzen "
                  "(das alte gilt dann nicht mehr).")
            return 1
        data["keys"][name] = secrets.token_urlsafe(32)
        data["issued"][name] = datetime.now().astimezone().isoformat(
            timespec="seconds")
        save(args.keys, data)
        print(f"Geheimnis fuer {name} angelegt. In das Paket des Freundes gehoert:")
        print(f'  {{"contributor": "{name}", "secret": "{data["keys"][name]}"}}')
        print("Ueber einen anderen Kanal schicken als den, ueber den er hochlaedt.")
        return 0

    if args.action == "show":
        if name not in data["keys"]:
            print(f"{name} ist nicht eingetragen.")
            return 1
        print(f'  {{"contributor": "{name}", "secret": "{data["keys"][name]}"}}')
        return 0

    if args.action == "revoke":
        if data["keys"].pop(name, None) is None:
            print(f"{name} war nicht eingetragen.")
            return 1
        data["issued"].pop(name, None)
        save(args.keys, data)
        print(f"{name} zurueckgezogen. Schon eingelassene Laeufe bleiben liegen -- "
              "zum Entfernen die Admin-Ansicht oder config/dataset_visibility.json.")
        return 0
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
