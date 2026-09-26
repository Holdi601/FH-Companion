"""Rechnet die Verwaltungsseite ohne crypto.subtle dieselbe Unterschrift wie der Server?

    python scripts/test_admin_hmac.py

Zieht die SHA-256-/HMAC-Funktionen AUS DER AUSGELIEFERTEN SEITE (server/admin_page.html),
laesst sie in node rechnen und vergleicht mit Pythons hashlib/hmac -- ueber leere,
kurze, lange, Unicode- und blockgrenzen-nahe Eingaben und Schluessel ueber 64 Bytes.
"""
from __future__ import annotations

import hashlib
import hmac
import json
import subprocess
import sys
from pathlib import Path

WS = Path(__file__).resolve().parent.parent
seite = (WS / "server" / "admin_page.html").read_text(encoding="utf-8")
start = seite.index("// ---- SHA-256 / HMAC")
ende = seite.index("// ---- Ende SHA-256 / HMAC")
code = seite[start:ende]

faelle = []
for text in ["", "a", "abc", "x" * 55, "x" * 56, "x" * 63, "x" * 64, "x" * 65, "x" * 1000,
             "GET\n/api/admin/usage\n1727190000\n" + hashlib.sha256(b"").hexdigest(),
             "Grüße 🏎️ 高速", json.dumps({"id": "abc", "reason": "removed in admin"})]:
    for geheim in ["k", "s" * 64, "s" * 65, "ein geheimes Passwort mit Ümlaut"]:
        faelle.append((geheim, text))

js = code + """
const hex = (buf) => Array.from(new Uint8Array(buf)).map(b => b.toString(16).padStart(2, "0")).join("");
const faelle = JSON.parse(process.argv[1]);
const enc = new TextEncoder();
const aus = faelle.map(([g, t]) => [hex(sha256Bytes(enc.encode(t)).buffer),
                                    hex(hmacBytes(enc.encode(g), enc.encode(t)).buffer)]);
console.log(JSON.stringify(aus));
"""
# window/crypto existieren in node nicht; die Fallback-Funktionen brauchen sie nicht.
r = subprocess.run(["node", "-e", js, json.dumps(faelle)], capture_output=True, text=True,
                   encoding="utf-8")
if r.returncode != 0:
    print("node scheiterte:", r.stderr[-800:])
    sys.exit(1)
ergebnis = json.loads(r.stdout)
fehler = 0
for (geheim, text), (sha_js, mac_js) in zip(faelle, ergebnis):
    sha_py = hashlib.sha256(text.encode("utf-8")).hexdigest()
    mac_py = hmac.new(geheim.encode("utf-8"), text.encode("utf-8"), hashlib.sha256).hexdigest()
    if sha_js != sha_py or mac_js != mac_py:
        fehler += 1
        print(f"  FEHL len(text)={len(text)} len(key)={len(geheim)}")
print(f"{len(faelle)} Faelle, {fehler} abweichend")
sys.exit(1 if fehler else 0)
