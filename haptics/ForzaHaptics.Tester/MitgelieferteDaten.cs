using ForzaHaptics.Tester.Rivals;

namespace ForzaHaptics.Tester;

/// <summary>
/// DIE MITGELIEFERTEN DATEN IN config/ AUF DEN STAND DES PROGRAMMS BRINGEN (seit 2026-11-11).
/// </summary>
/// <remarks>
/// Der Selbstaktualisierer ersetzt den Programmordner, ergaenzt in <c>config/</c> aber nur, was
/// fehlt -- dort liegen die Einstellungen des Nutzers. Damit blieben auch die mitgelieferten
/// Daten auf dem Stand der ersten Installation: Streckennamen des Spiels, Routentabelle des
/// Scanners, Uebersetzungen. Nach einem Spiel-Update mit neuen Strecken fand der Scanner sie
/// deshalb nicht (2026-11-11: game_text.json vom 29.09., ohne "Edo Circuit" und "Shuto Charge").
///
/// Das Paket legt diese Daten darum ein zweites Mal in <c>app/shipped/</c> ab -- der Ordner wird
/// bei jedem Update ganz ersetzt, auch von einem alten Aktualisierer. Beim Start kopiert die App
/// von dort nach <c>config/</c>, was sich unterscheidet. NIE <c>overlay.json</c> (die
/// Einstellungen); persoenliche Dateien (Notizen, eigene Autos) liegen gar nicht in shipped/.
/// Ein eigener Ordnername und nicht <c>app/config/</c>: Notizen und Sammlung schreiben in den
/// ersten <c>config/</c>-Ordner, den sie aufwaerts finden -- in app/ gingen sie beim Update verloren.
/// </remarks>
internal static class MitgelieferteDaten
{
    private static readonly HashSet<string> NieUeberschreiben = new(StringComparer.OrdinalIgnoreCase) { "overlay.json" };

    /// <summary>Beim Start: aus app/shipped/ nach config/. Wirft nie; zaehlt, was erneuert wurde.</summary>
    public static int Abgleichen()
    {
        try
        {
            var quelle = Path.Combine(AppContext.BaseDirectory, "shipped");
            if (!Directory.Exists(quelle)) { return 0; }
            var einstellungen = OverlaySettings.FindPath();
            var ziel = einstellungen is not null
                ? Path.GetDirectoryName(einstellungen)!
                : Path.Combine(Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))?.FullName
                               ?? AppContext.BaseDirectory, "config");
            return Abgleichen(quelle, ziel);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>Jede Datei aus <paramref name="quelle"/>, die in <paramref name="ziel"/> fehlt oder anders ist, dorthin.</summary>
    internal static int Abgleichen(string quelle, string ziel)
    {
        var erneuert = 0;
        foreach (var datei in Directory.EnumerateFiles(quelle, "*", SearchOption.AllDirectories))
        {
            var relativ = Path.GetRelativePath(quelle, datei);
            if (NieUeberschreiben.Contains(Path.GetFileName(relativ))) { continue; }
            var dort = Path.Combine(ziel, relativ);
            try
            {
                if (File.Exists(dort) && Gleich(datei, dort)) { continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(dort)!);
                var tmp = dort + ".tmp";
                File.Copy(datei, tmp, overwrite: true);
                File.Move(tmp, dort, overwrite: true);
                erneuert++;
            }
            catch (Exception)
            {
                // Eine gesperrte Datei: beim naechsten Start noch einmal.
            }
        }
        return erneuert;
    }

    private static bool Gleich(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        if (fa.Length != fb.Length) { return false; }
        return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }
}
