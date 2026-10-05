using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForzaHaptics.Tester.Scan;

/// <summary>
/// Die Strecken je Rivals-Kategorie in der Reihenfolge des Karussells (Index 0 = Anker),
/// dazu das Wahrzeichen, an dem die geoeffnete Liste erkannt wird -- aus
/// <c>config/fh6_scan_routes.json</c> (gebaut von <c>scripts/build_scan_routes.py</c>).
/// </summary>
internal sealed class ScanRoutes
{
    public sealed record Kategorie(string Name, Regex Wahrzeichen, IReadOnlyList<string> Strecken, IReadOnlyList<double?> Laengen)
    {
        /// <summary>Die Position einer Lesung des Streckentitels im Karussell, -1 wenn unklar.</summary>
        public int Lokalisiere(string? gelesen) => ScanScreen.Lokalisiere(gelesen, Strecken);

        /// <summary>Die Laenge einer Strecke, wie der Schirm sie zeigt (km), oder null.</summary>
        public double? LaengeVon(int index) => index >= 0 && index < Laengen.Count ? Laengen[index] : null;

        /// <summary>
        /// Wo das Karussell steht -- aus Titel, Kachelleiste und Laenge zusammen. Titel und Leiste
        /// sind unabhaengige Belege: widersprechen sie sich, ist es -1, und eine gelesene Laenge,
        /// die nicht zur Position passt, verwirft sie ebenso.
        /// </summary>
        public (int Index, string Beleg) Verorte(ScanScreen.Lesung l)
        {
            var nachTitel = Lokalisiere(l.Titel);
            var nachLeiste = ScanScreen.LeisteVerorten(l.Leiste, l.Laenge, Laengen);
            if (nachTitel >= 0 && nachLeiste >= 0 && nachTitel != nachLeiste) { return (-1, $"title says {nachTitel}, strip says {nachLeiste}"); }
            var idx = nachTitel >= 0 ? nachTitel : nachLeiste;
            if (idx < 0) { return (-1, "neither title nor strip placed"); }
            if (l.Laenge is { } gelesen && LaengeVon(idx) is { } soll && Math.Abs(gelesen - soll) > 0.05)
            {
                return (-1, $"route {idx} is {soll:0.0} km, the screen says {gelesen:0.0}");
            }
            var beleg = nachTitel >= 0 && nachLeiste >= 0 ? "title+strip" : nachTitel >= 0 ? "title" : "strip";
            return (idx, beleg);
        }
    }

    private readonly Dictionary<string, Kategorie> _kategorien = new(StringComparer.OrdinalIgnoreCase);

    public ScanRoutes(IEnumerable<Kategorie> kategorien)
    {
        foreach (var k in kategorien) { _kategorien[k.Name] = k; }
    }

    public IReadOnlyCollection<Kategorie> Kategorien => _kategorien.Values;

    /// <summary>Die Kategorie zu einem Namen -- "cross country" und "Cross-Country" sind dieselbe.</summary>
    public Kategorie? Finde(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) { return null; }
        if (_kategorien.TryGetValue(name.Trim(), out var genau)) { return genau; }
        var gesucht = ScanScreen.Falte(name);
        return _kategorien.Values.FirstOrDefault(k => ScanScreen.Falte(k.Name) == gesucht);
    }

    /// <summary>Wo die Datei liegt: config/ neben dem Programm, sonst aufwaerts (im Quellbaum).</summary>
    public static string? Datei()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var pfad = Path.Combine(dir.FullName, "config", "fh6_scan_routes.json");
            if (File.Exists(pfad)) { return pfad; }
            dir = dir.Parent;
        }
        return null;
    }

    public static ScanRoutes Laden(string? pfad = null)
    {
        pfad ??= Datei() ?? throw new FileNotFoundException(
            "config/fh6_scan_routes.json is missing -- build it with scripts/build_scan_routes.py.");
        return AusJson(File.ReadAllText(pfad));
    }

    public static ScanRoutes AusJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var liste = new List<Kategorie>();
        if (doc.RootElement.TryGetProperty("categories", out var kategorien))
        {
            foreach (var k in kategorien.EnumerateObject())
            {
                var muster = k.Value.TryGetProperty("landmark", out var l) ? l.GetString() ?? string.Empty : string.Empty;
                var strecken = k.Value.TryGetProperty("routes", out var r)
                    ? r.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0).ToList()
                    : new List<string>();
                var laengen = new List<double?>();
                if (k.Value.TryGetProperty("lengths", out var ls))
                {
                    laengen.AddRange(ls.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetDouble() : (double?)null));
                }
                // Ohne Laengen (oder zu wenige) bleibt nur der Titel als Beleg.
                while (laengen.Count < strecken.Count) { laengen.Add(null); }
                liste.Add(new Kategorie(k.Name, new Regex(muster.Length > 0 ? muster : "(?!)", RegexOptions.CultureInvariant), strecken,
                                        laengen.Take(strecken.Count).ToList()));
            }
        }
        return new ScanRoutes(liste);
    }
}
