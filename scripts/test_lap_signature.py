"""Die Unterschrift der App gegen die des Servers -- Byte fuer Byte.

    python scripts/test_lap_signature.py     (braucht dotnet)

## Warum das geprueft gehoert

Die Krypto selbst ist auf beiden Seiten eingebaut (`HMACSHA256` in .NET,
`hmac`/`hashlib` in Python) und mit Sicherheit richtig. Was schiefgehen kann, ist
die NACHRICHT: Reihenfolge der Felder, das Trennzeichen, Gross- oder
Kleinschreibung des Hex, die Kodierung.

Und ein solcher Fehler meldet sich nicht als das, was er ist. Er erzeugt eine
Unterschrift, die der Server ablehnt -- und das sieht aus wie "die Installation ist
nicht angemeldet" oder "das Geheimnis stimmt nicht". Man wuerde tagelang am
falschen Ende suchen.

## Wie

Die Methode `Signature` wird bei jedem Lauf FRISCH aus `LapSubmit.cs`
herausgeschnitten und in ein Wegwerfprogramm gepackt. Eine Kopie hier wuerde von
der ausgelieferten Fassung abdriften, ohne dass es auffaellt -- dasselbe Verfahren
wie in `test_admin_crypto.py` fuer das JavaScript der Admin-Seite.
"""

from __future__ import annotations

import json
import re
import subprocess
import sys
import tempfile
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
QUELLE = WORKSPACE / "haptics/ForzaHaptics.Tester/Rivals/LapSubmit.cs"
sys.path.insert(0, str(WORKSPACE / "server"))

import lap_submissions as laps  # noqa: E402

# Faelle, an denen eine Zusammensetzung wirklich bricht: leerer Rumpf, Umlaute,
# Zeilenumbrueche IM Rumpf (die koennten mit dem Trennzeichen verwechselt werden),
# ein langes Geheimnis, und ein Pfad mit Sonderzeichen.
FAELLE = [
    ("geheim", "POST", "/api/lap/submit", "1757779200", "abc123", b""),
    ("geheim", "POST", "/api/lap/submit", "1757779200", "abc123", b'{"lap":{}}'),
    ("geheim", "post", "/api/lap/submit", "0", "n", b"x"),
    ("s" * 100, "POST", "/api/lap/submit", "1757779200", "nonce", b"y" * 5000),
    ("geheim", "POST", "/api/lap/submit", "1757779200", "abc",
     '{"gamertag":"Gött Ⅷ"}'.encode("utf-8")),
    # Zeilenumbrueche im Rumpf: sie duerfen die Nachricht NICHT zerlegen, denn der
    # Rumpf geht nur als Hash ein.
    ("geheim", "POST", "/api/lap/submit", "1757779200", "abc", b"a\nb\nc\n"),
    ("géheim", "POST", "/api/lap/submit", "1757779200", "abc", b"{}"),
]

PROGRAMM = """using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class Pruef
{
__SIGNATURE__

    private static void Main()
    {
        var roh = Console.In.ReadToEnd();
        using var doc = JsonDocument.Parse(roh);
        var raus = new System.Collections.Generic.List<string>();
        foreach (var f in doc.RootElement.EnumerateArray())
        {
            raus.Add(Signature(
                f[0].GetString()!, f[1].GetString()!, f[2].GetString()!,
                f[3].GetString()!, f[4].GetString()!,
                Convert.FromBase64String(f[5].GetString()!)));
        }
        Console.Out.Write(JsonSerializer.Serialize(raus));
    }
}
"""

CSPROJ = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>pruef</AssemblyName>
    <RootNamespace>pruef</RootNamespace>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
</Project>
"""


def schneide_signature(text: str) -> str:
    """Die Methode `Signature` aus dem Quelltext holen -- samt ihrer Klammern."""
    anfang = text.index("public static string Signature(")
    # Von dort bis zur schliessenden Klammer, mit Zaehlwerk.
    i = text.index("{", anfang)
    tiefe = 0
    for j in range(i, len(text)):
        if text[j] == "{":
            tiefe += 1
        elif text[j] == "}":
            tiefe -= 1
            if tiefe == 0:
                return text[anfang:j + 1]
    raise SystemExit("Die Methode Signature ist nicht zu Ende geklammert.")


def main() -> int:
    if not QUELLE.exists():
        print("Fehlt: " + QUELLE.as_posix())
        return 1
    quelltext = QUELLE.read_text(encoding="utf-8")
    methode = schneide_signature(quelltext)
    print("Signature aus LapSubmit.cs geschnitten: %d Zeichen" % len(methode))

    with tempfile.TemporaryDirectory() as tmp:
        ordner = Path(tmp)
        (ordner / "pruef.csproj").write_text(CSPROJ, encoding="utf-8")
        (ordner / "Program.cs").write_text(
            PROGRAMM.replace("__SIGNATURE__", "    " + methode), encoding="utf-8")

        import base64
        eingabe = json.dumps([[s, m, p, t, n, base64.b64encode(b).decode("ascii")]
                              for s, m, p, t, n, b in FAELLE])
        lauf = subprocess.run(
            ["dotnet", "run", "--project", str(ordner), "-c", "Release",
             "--nologo", "-v", "q"],
            input=eingabe, capture_output=True, text=True, cwd=str(ordner))
        if lauf.returncode != 0:
            print("dotnet run ist gescheitert:\n" + (lauf.stdout + lauf.stderr)[-2500:])
            return 1
        try:
            aus_csharp = json.loads(lauf.stdout.strip().splitlines()[-1])
        except (ValueError, IndexError):
            print("Unbrauchbare Ausgabe:\n" + lauf.stdout[-1500:])
            return 1

    fehler = 0
    for (s, m, p, t, n, b), aus_cs in zip(FAELLE, aus_csharp):
        aus_py = laps.signature(s, m, p, t, n, b)
        gleich = aus_cs == aus_py
        fehler += not gleich
        print("  %s %-28s Rumpf %5d B  %s" % (
            "ok  " if gleich else "FEHL", "%s %s" % (m, n), len(b), aus_cs[:24] + "..."))
        if not gleich:
            print("        C#:     " + aus_cs)
            print("        Python: " + aus_py)

    print()
    if fehler:
        print("%d von %d Faellen stimmen NICHT ueberein." % (fehler, len(FAELLE)))
        return 1
    print("alle %d Faelle stimmen ueberein -- alles bestanden" % len(FAELLE))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
