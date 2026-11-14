using System.IO.Compression;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// RUNDEN MIT FREUNDEN TEILEN (seit 2026-11-14, Nutzer: "es sollte wesentlich einfacher sein,
/// Telemetrie-Runden mit Freunden zu teilen").
/// </summary>
/// <remarks>
/// Bisher hiess Teilen: den Rundenordner oeffnen, sich durch Kurs-, Klassen-, Auto- und Tune-Ordner
/// klicken und zwei Dateien je Runde finden (die Runde und daneben ihre volle Telemetrie). Jetzt:
/// Runden in "Meine Zeiten" markieren, "Teilen" -- ein Zip mit sprechendem Namen, in dem jede Runde
/// mit ihrer Telemetrie an ihrem gewohnten Platz liegt; der Ordner oeffnet sich mit der Datei
/// markiert, zum Hineinziehen in einen Chat. Der Freund nimmt es mit "Runden eines Freundes
/// oeffnen" an: entpackt nach friend_laps, getrennt vom eigenen Bestand -- fremde Runden duerfen nie
/// als eigene Zeiten zaehlen.
/// </remarks>
internal static class LapShare
{
    /// <summary>Wohin Runden von Freunden kommen -- nicht unter laps, sonst zaehlten sie als eigene.</summary>
    public static string FreundeOrdner => Path.Combine(AppInfo.DataFolder, "friend_laps");

    /// <summary>Ein Vorschlag fuer den Namen des Zips: Auto und Strecke bei einer Runde, sonst die Anzahl.</summary>
    public static string Dateiname(IReadOnlyList<(string Auto, string Strecke, double Sekunden)> runden, DateTime jetzt)
    {
        string name;
        if (runden.Count == 1)
        {
            var r = runden[0];
            name = $"FH laps - {r.Auto} - {r.Strecke} - {OwnCars.TimeText(r.Sekunden)}";
        }
        else
        {
            var autos = runden.Select(r => r.Auto).Distinct().ToList();
            var strecken = runden.Select(r => r.Strecke).Distinct().ToList();
            name = $"FH laps - {runden.Count} laps"
                   + (autos.Count == 1 ? " - " + autos[0] : string.Empty)
                   + (strecken.Count == 1 ? " - " + strecken[0] : string.Empty);
        }
        name += $" - {jetzt:yyyy-MM-dd}";
        var schlecht = Path.GetInvalidFileNameChars();
        name = new string(name.Select(c => schlecht.Contains(c) ? '-' : c).ToArray()).Trim();
        if (name.Length > 120) { name = name[..120].TrimEnd(); }
        return name + ".zip";
    }

    /// <summary>Die Runden (Pfade der Rundendateien) samt Telemetrie in ein Zip; gibt die Zahl der Runden.</summary>
    public static int Packen(IEnumerable<string> runden, string wurzel, string ziel)
    {
        var zahl = 0;
        if (File.Exists(ziel)) { File.Delete(ziel); }
        using var zip = ZipFile.Open(ziel, ZipArchiveMode.Create);
        foreach (var datei in runden.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(datei)) { continue; }
            var rel = Path.GetRelativePath(wurzel, datei);
            if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) { rel = Path.GetFileName(datei); }
            rel = "laps/" + rel.Replace('\\', '/');
            zip.CreateEntryFromFile(datei, rel, CompressionLevel.Optimal);
            var spur = datei + TelemetryTrack.Suffix;
            if (File.Exists(spur)) { zip.CreateEntryFromFile(spur, rel + TelemetryTrack.Suffix, CompressionLevel.NoCompression); }
            zahl++;
        }
        return zahl;
    }

    /// <summary>
    /// Ein Zip eines Freundes entpacken: nur Runden und ihre Telemetrie, nur innerhalb des Zielordners
    /// (ein Eintrag wie "../../x" wird uebergangen). Gibt die Zahl der Runden und den Ordner.
    /// </summary>
    public static (int Runden, string Ordner) Entpacken(string zipPfad, string? basis = null)
    {
        var schlecht = Path.GetInvalidFileNameChars();
        var name = new string(Path.GetFileNameWithoutExtension(zipPfad).Select(c => schlecht.Contains(c) ? '-' : c).ToArray()).Trim();
        var ziel = Path.GetFullPath(Path.Combine(basis ?? FreundeOrdner, name.Length == 0 ? "laps" : name));
        var praefix = ziel.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var zahl = 0;
        using var zip = ZipFile.OpenRead(zipPfad);
        foreach (var eintrag in zip.Entries)
        {
            var n = eintrag.FullName;
            var istRunde = n.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            if (!istRunde && !n.EndsWith(TelemetryTrack.Suffix, StringComparison.OrdinalIgnoreCase)) { continue; }
            var pfad = Path.GetFullPath(Path.Combine(ziel, n.Replace('/', Path.DirectorySeparatorChar)));
            if (!pfad.StartsWith(praefix, StringComparison.OrdinalIgnoreCase)) { continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(pfad)!);
            eintrag.ExtractToFile(pfad, overwrite: true);
            if (istRunde) { zahl++; }
        }
        return (zahl, ziel);
    }
}
