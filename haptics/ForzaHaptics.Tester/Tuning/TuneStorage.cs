using System.Text;
using System.Text.Json;

namespace ForzaHaptics.Tester.Tuning;

/// <summary>Ein gespeichertes Tune, so wie es im Spielstand liegt.</summary>
/// <param name="Folder">Der Container, zugleich der Name, unter dem die Garage es fuehrt.</param>
/// <param name="CarId">Das Auto (dieselbe Kennung wie Telemetrie und Datensatz).</param>
/// <param name="SavedAt">Wann es bei dir gespeichert wurde -- steht im Containernamen.</param>
/// <param name="Name">Der Name, den der Ersteller ihm gab.</param>
/// <param name="Creator">Gamertag des Erstellers.</param>
/// <param name="CreatorXuid">Xbox-Kennung des Erstellers.</param>
/// <param name="CreatedAt">Wann der Ersteller es angelegt hat, falls lesbar.</param>
/// <param name="Description">Die Beschreibung des Erstellers, oft leer.</param>
internal sealed record StoredTune(string Folder, int CarId, DateTime SavedAt, string Name,
                                  string Creator, ulong CreatorXuid, DateTime? CreatedAt,
                                  string Description = "");

/// <summary>
/// Die heruntergeladenen Tunes im Spielstand -- zaehlen, auflisten, nachsehen, welche
/// auf einem Auto liegen. NUR LESEN.
/// </summary>
/// <remarks>
/// ## Wo sie liegen (gemessen am 2026-09-26)
///
/// Nicht in einer Datenbank, sondern als eigene Container im Xbox-Spielstand:
/// <c>C:\XboxGames\GameSave\pgs\u_&lt;xuid&gt;_16D460\current\ContainersRoot\Tuning_0247_20260606114055</c>
/// -- Auto 247, gespeichert am 06.06.2026 um 11:40:55. Den Ort nennt das Spiel selbst
/// in <c>%LOCALAPPDATA%\ForzaHorizon6\SaveFolderMetadata_*\LastSaveFolderLocation</c>.
/// Je Container drei Dateien: <c>Data</c> (die Reglerwerte), <c>Thumb.png</c> und
/// <c>header</c> mit Name, Datum, Ersteller-Kennung und Gamertag.
///
/// Welches Tune auf einem Auto liegt, sagt die Garage: <c>Career_Garage.TuneFileName</c>
/// ist genau dieser Containername. Am 14.09. lagen so 576 von 837 Tunes auf einem Auto
/// und 261 auf keinem.
///
/// ## Warum hier nichts geloescht wird
///
/// Der Ordner gehoert dem Spielstand-Abgleich von Xbox: jede Sicherung ist eine
/// nummerierte Version mit einem Verzeichnis aller Container, das in die Cloud geht.
/// Ein von Hand entfernter Ordner kann dort als saubere Loeschung ankommen -- oder
/// als beschaedigter Spielstand. Mit 287 Stunden Spielzeit darin wird das nicht
/// ausprobiert. Loeschen muss das Spiel selbst.
/// </remarks>
internal static class TuneStorage
{
    public const int DefaultLimit = 1000;

    /// <summary>Der Containerordner des Spielstands, oder null.</summary>
    public static string? FindRoot()
    {
        try
        {
            var basis = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                     "ForzaHorizon6");
            if (Directory.Exists(basis))
            {
                foreach (var meta in Directory.EnumerateDirectories(basis, "SaveFolderMetadata_*"))
                {
                    var datei = Path.Combine(meta, "LastSaveFolderLocation");
                    if (!File.Exists(datei)) { continue; }
                    var pfad = File.ReadAllText(datei, Encoding.UTF8).Trim('\0', ' ', '\r', '\n', '\uFEFF');
                    if (Directory.Exists(pfad)) { return pfad; }
                }
            }
            // Rueckfall: der uebliche Ort, falls die Verweisdatei fehlt.
            var pgs = @"C:\XboxGames\GameSave\pgs";
            if (Directory.Exists(pgs))
            {
                foreach (var u in Directory.EnumerateDirectories(pgs, "u_*_16D460"))
                {
                    var pfad = Path.Combine(u, "current", "ContainersRoot");
                    if (Directory.Exists(pfad)) { return pfad; }
                }
            }
        }
        catch (Exception)
        {
            // Kein Zugriff -- dann eben keine Zaehlung.
        }
        return null;
    }

    /// <summary>Nur zaehlen -- billig genug fuer alle paar Minuten.</summary>
    public static int? Count(string? root = null)
    {
        root ??= FindRoot();
        if (root is null) { return null; }
        try { return Directory.EnumerateDirectories(root, "Tuning_*").Count(); }
        catch (Exception) { return null; }
    }

    /// <summary>Alle Tunes mit Kopfdaten.</summary>
    public static List<StoredTune> Read(string? root = null)
    {
        root ??= FindRoot();
        var raus = new List<StoredTune>();
        if (root is null) { return raus; }
        foreach (var ordner in Directory.EnumerateDirectories(root, "Tuning_*"))
        {
            var name = Path.GetFileName(ordner);
            try
            {
                var kopf = Path.Combine(ordner, "header");
                var t = Parse(name, File.Exists(kopf) ? File.ReadAllBytes(kopf) : Array.Empty<byte>());
                if (t is not null) { raus.Add(t); }
            }
            catch (Exception)
            {
                // Ein unlesbarer Kopf kostet diesen Eintrag, nicht die Liste.
            }
        }
        return raus;
    }

    /// <summary>
    /// Containername und Kopf lesen. Der Name allein genuegt fuer Auto und Datum; der
    /// Kopf liefert Name und Ersteller.
    /// </summary>
    /// <remarks>
    /// Aufbau des Kopfs, Fassung 7: Fassung (int), Name (Laenge + UTF-16), BESCHREIBUNG
    /// (Laenge + UTF-16, oft leer), Erstelldatum als SYSTEMTIME (8 x uint16), 4 Byte,
    /// Ersteller-Kennung (uint64), Gamertag (Laenge + UTF-16), dann Kennungen, die hier
    /// nicht gebraucht werden.
    ///
    /// Die Beschreibung stand hier bis zum 2026-09-26 als "4 Byte" -- das stimmt nur,
    /// wenn sie leer ist. Bei 258 von 992 Tunes war sie es nicht ("Balanced config.
    /// Enjoy"), und der Ersteller kam als leer oder als Zeichensalat heraus.
    /// </remarks>
    internal static StoredTune? Parse(string ordner, byte[] kopf)
    {
        var teile = ordner.Split('_');
        if (teile.Length < 3 || teile[0] != "Tuning" || !int.TryParse(teile[1], out var auto)) { return null; }
        if (!DateTime.TryParseExact(teile[2], "yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture,
                                    System.Globalization.DateTimeStyles.None, out var gespeichert))
        {
            return null;
        }
        string name = string.Empty, ersteller = string.Empty, beschreibung = string.Empty;
        ulong xuid = 0;
        DateTime? angelegt = null;
        try
        {
            var i = 4;
            name = Text(kopf, ref i);
            beschreibung = Text(kopf, ref i);
            var st = new ushort[8];
            for (var k = 0; k < 8; k++) { st[k] = BitConverter.ToUInt16(kopf, i + (2 * k)); }
            i += 16;
            if (st[0] is > 2000 and < 2100 && st[1] is >= 1 and <= 12 && st[3] is >= 1 and <= 31)
            {
                angelegt = new DateTime(st[0], st[1], st[3], Math.Min((int)st[4], 23), Math.Min((int)st[5], 59), 0);
            }
            i += 4;
            xuid = BitConverter.ToUInt64(kopf, i);
            i += 8;
            ersteller = Text(kopf, ref i);
        }
        catch (Exception)
        {
            // Kopf fehlt oder hat eine andere Fassung: Auto und Datum reichen.
        }
        return new StoredTune(ordner, auto, gespeichert, name, ersteller, xuid, angelegt, beschreibung.Trim());
    }

    private static string Text(byte[] b, ref int i)
    {
        var n = BitConverter.ToInt32(b, i);
        i += 4;
        if (n < 0 || n > 4000 || i + (2 * n) > b.Length) { throw new FormatException("kein Text"); }
        var s = Encoding.Unicode.GetString(b, i, 2 * n);
        i += 2 * n;
        return s;
    }

    // ---- DAS AUFGESPIELTE TUNE EINES AUTOS (fuer die Autonotiz) -------------------

    private static List<StoredTune>? _alle;
    private static DateTime _alleStand;
    private static string? _alleWurzel;
    private static Usage? _nutzung;
    private static DateTime _nutzungStand;

    /// <summary>
    /// Welches Tune liegt auf diesem Auto? Mit "sicher" = laut Garagen-Pruefung.
    /// </summary>
    /// <remarks>
    /// Seit 2026-09-26 fuer die Autonotiz (Nutzerwunsch: die Beschreibung des Tunes
    /// unter der eigenen Notiz, wenn es aufgespielt ist). Regel siehe
    /// <see cref="Aufgespielt"/>. Zwischengespeichert: der Ordner wird nur neu gelesen,
    /// wenn er sich geaendert hat -- tausend Koepfe je Autowechsel waeren zu viel.
    /// </remarks>
    public static (StoredTune Tune, bool Sicher)? AppliedFor(int carId)
    {
        try
        {
            var root = FindRoot();
            if (root is null) { return null; }
            var stand = Directory.GetLastWriteTimeUtc(root);
            if (_alle is null || _alleWurzel != root || stand != _alleStand)
            {
                _alle = Read(root);
                _alleStand = stand;
                _alleWurzel = root;
            }
            var datei = UsagePath;
            var nutzungStand = File.Exists(datei) ? File.GetLastWriteTimeUtc(datei) : DateTime.MinValue;
            if (nutzungStand != _nutzungStand)
            {
                _nutzung = LoadUsage();
                _nutzungStand = nutzungStand;
            }
            return Aufgespielt(_alle, _nutzung, carId);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Die Regel, ohne Platte -- fuer den Test.</summary>
    /// <remarks>
    /// 1. Nach der Garagen-Pruefung gespeichert: das juengste davon. Heruntergeladen
    ///    heisst im Spiel aufgespielt, und die Pruefung kennt es noch nicht.
    /// 2. Sonst das Tune, das die Pruefung auf dem Auto fand -- sicher.
    /// 3. Ohne Pruefung: das zuletzt gespeicherte -- nur vermutet.
    /// </remarks>
    internal static (StoredTune Tune, bool Sicher)? Aufgespielt(IReadOnlyList<StoredTune> alle, Usage? nutzung, int carId)
    {
        var eigene = alle.Where(t => t.CarId == carId).OrderByDescending(t => t.SavedAt).ToList();
        if (eigene.Count == 0) { return null; }
        if (nutzung is null) { return (eigene[0], false); }
        var neuer = eigene.FirstOrDefault(t => t.SavedAt > nutzung.CheckedAt);
        if (neuer is not null) { return (neuer, false); }
        var belegt = eigene.FirstOrDefault(t => nutzung.Applied.Contains(t.Folder, StringComparer.OrdinalIgnoreCase));
        return belegt is null ? null : (belegt, true);
    }

    // ---- WELCHE AUF EINEM AUTO LIEGEN -------------------------------------------

    /// <summary>Das zuletzt gelesene Ergebnis, auf der Platte gemerkt.</summary>
    internal sealed class Usage
    {
        public DateTime CheckedAt { get; set; }
        public List<string> Applied { get; set; } = new();
    }

    private static string UsagePath => Path.Combine(AppInfo.DataFolder, "tune_usage.json");

    public static Usage? LoadUsage()
    {
        try
        {
            return File.Exists(UsagePath)
                ? JsonSerializer.Deserialize<Usage>(File.ReadAllText(UsagePath))
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void SaveUsage(Usage u)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UsagePath)!);
            File.WriteAllText(UsagePath, JsonSerializer.Serialize(u));
        }
        catch (Exception)
        {
            // Nur ein Zwischenspeicher.
        }
    }
}
