"""Die selbstgeschriebene SHA-256/HMAC der Admin-Seite gegen Pythons eigene.

    python scripts/test_admin_crypto.py     (braucht node)

## Warum es diese Umsetzung ueberhaupt gibt

`crypto.subtle` steht nur in einem "sicheren Kontext" zur Verfuegung -- HTTPS oder
localhost. Die Admin-Seite wird ueber einfaches HTTP von einer duckdns-Adresse
geladen; dort ist `crypto.subtle` schlicht `undefined`. Ohne eigene Umsetzung
muesste das Admin-Geheimnis im Klartext mitgeschickt werden.

## Warum es diesen Prueflauf braucht

Eine falsche HMAC scheitert nicht auffaellig -- sie erzeugt eine Unterschrift, die
der Server ablehnt, und das sieht aus wie ein falsch eingetipptes Geheimnis. Der
Fehler waere also am ehesten als "das Passwort stimmt nicht" fehlgedeutet worden.
Geprueft wird deshalb gegen `hashlib`/`hmac` und ueber die Faelle, an denen eine
handgeschriebene Umsetzung wirklich bricht: leere Eingabe, genau ein Block, ein
Byte darueber, Schluessel laenger als der Block, und Zeichen ausserhalb von ASCII.

Das JavaScript wird bei jedem Lauf frisch aus `admin_page.html` geschnitten -- eine
Kopie hier wuerde von der ausgelieferten Fassung abdriften, ohne dass es auffaellt.
"""

from __future__ import annotations

import hashlib
import hmac
import json
import subprocess
import sys
import tempfile
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
PAGE = WORKSPACE / "scripts/admin_page.html"

CASES = [
    "",                      # leer
    "a",                     # ein Byte
    "abc",
    "hallo welt",
    "x" * 55,                # ein Byte unter der Polstergrenze
    "x" * 56,                # genau an der Grenze: braucht einen zweiten Block
    "y" * 64,                # genau ein Block
    "z" * 65,                # ein Byte darueber
    "w" * 1000,              # viele Bloecke
    "ä ö ü ß — Straße",      # nicht-ASCII: Zeichen != Bytes
]

SECRETS = [
    "k",                     # sehr kurz
    "geheim",
    "s" * 63,
    "t" * 64,                # genau die Blockgroesse
    "l" * 100,               # laenger als der Block: wird gehasht
    "ä" * 40,                # nicht-ASCII im Schluessel
    "x" * 256,               # so lang wie das echte Zugangspasswort
]


def main() -> int:
    if not PAGE.exists():
        print(f"{PAGE} fehlt")
        return 1
    html = PAGE.read_text(encoding="utf-8")
    try:
        js = html.split("<script>")[1].split("/* ---------- Aufrufe ---------- */")[0]
    except IndexError:
        print("Die Krypto-Passage liess sich nicht aus der Seite schneiden -- "
              "wurde der Kommentar '/* ---------- Aufrufe ---------- */' entfernt?")
        return 1

    expect = {f"{s}||{m}": hmac.new(s.encode(), m.encode(), hashlib.sha256).hexdigest()
              for s in SECRETS for m in CASES}
    sha_expect = {m: hashlib.sha256(m.encode()).hexdigest() for m in CASES}

    harness = js + f"""
const expect = {json.dumps(expect, ensure_ascii=False)};
const shaExpect = {json.dumps(sha_expect, ensure_ascii=False)};
let bad = 0, total = 0;
for (const [key, want] of Object.entries(expect)) {{
  const cut = key.indexOf("||");
  const got = hmacSha256(key.slice(0, cut), key.slice(cut + 2));
  total++;
  if (got !== want) {{ bad++; console.log("  FAIL  HMAC", JSON.stringify(key).slice(0, 70)); }}
}}
for (const [message, want] of Object.entries(shaExpect)) {{
  const got = hex(sha256(encoder.encode(message)));
  total++;
  if (got !== want) {{ bad++; console.log("  FAIL  SHA-256", JSON.stringify(message).slice(0, 50)); }}
}}
console.log(bad === 0
  ? `  ok    alle ${{total}} Vergleiche stimmen mit Pythons hashlib/hmac ueberein`
  : `  FAIL  ${{bad}} von ${{total}} falsch`);
process.exit(bad === 0 ? 0 : 1);
"""
    with tempfile.TemporaryDirectory() as folder:
        script = Path(folder) / "check.js"
        script.write_text(harness, encoding="utf-8")
        result = subprocess.run(["node", str(script)], capture_output=True, text=True)
    print(result.stdout.strip() or result.stderr.strip())
    print()
    print("alles bestanden" if result.returncode == 0 else "fehlgeschlagen")
    return result.returncode


if __name__ == "__main__":
    raise SystemExit(main())
