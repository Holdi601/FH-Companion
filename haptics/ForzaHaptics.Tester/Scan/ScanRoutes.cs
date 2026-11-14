using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
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
            var nachLeiste = ScanScreen.LeisteVerorten(l.Leiste, l.Laenge, Laengen, l.Toleranz);
            if (nachTitel >= 0 && nachLeiste >= 0 && nachTitel != nachLeiste) { return (-1, $"title says {nachTitel}, strip says {nachLeiste}"); }
            var idx = nachTitel >= 0 ? nachTitel : nachLeiste;
            if (idx < 0) { return (-1, "neither title nor strip placed"); }
            if (l.Laenge is { } gelesen && LaengeVon(idx) is { } soll && Math.Abs(gelesen - soll) > l.Toleranz)
            {
                return (-1, $"route {idx} is {soll:0.0} km, the screen says {gelesen:0.0}");
            }
            var beleg = nachTitel >= 0 && nachLeiste >= 0 ? "title+strip" : nachTitel >= 0 ? "title" : "strip";
            return (idx, beleg);
        }
    }

    private readonly Dictionary<string, Kategorie> _kategorien = new(StringComparer.OrdinalIgnoreCase);

    // GELERNTE LISTEN (seit 2026-11-11). Mit einem Spiel-Update kamen neue Strecken ins Karussell;
    // die mitgelieferte Tabelle kennt sie nicht, und der Scanner faehrt Strecken ueber ihre Position
    // an. Der Scanner geht darum das Karussell einer Kategorie einmal ganz durch (ScanNavigator.Lerne)
    // und legt die Reihenfolge hier ab -- sie gilt vor der mitgelieferten, bis das Spiel sich wieder
    // aendert.
    private readonly Dictionary<string, DateTime> _gelernt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Wo die gelernten Listen liegen (je Rechner, im Datenordner der App).</summary>
    public static string LernDatei => Path.Combine(AppInfo.DataFolder, "scan_routes_learned.json");

    /// <summary>Wann die Liste dieser Kategorie zuletzt im Spiel gelernt wurde (UTC) -- null: nie.</summary>
    public DateTime? GelerntAm(string kategorie) =>
        Finde(kategorie) is { } k && _gelernt.TryGetValue(k.Name, out var t) ? t : null;

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
        return MitGelerntem(AusJson(File.ReadAllText(pfad)), LernDatei);
    }

    /// <summary>
    /// Die mitgelieferte Tabelle, ueberschrieben von den im Spiel gelernten Listen. Eine gelernte
    /// Liste, die weniger als vier Fuenftel der mitgelieferten Strecken enthaelt, gilt nicht --
    /// dann lief beim Lernen etwas schief, und die alte Tabelle ist die bessere.
    /// </summary>
    internal static ScanRoutes MitGelerntem(ScanRoutes basis, string? datei)
    {
        if (datei is null || !File.Exists(datei)) { return basis; }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(datei));
            if (!doc.RootElement.TryGetProperty("categories", out var gelernt) || gelernt.ValueKind != JsonValueKind.Object)
            {
                return basis;
            }
            var liste = new List<Kategorie>();
            var am = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in basis.Kategorien)
            {
                if (gelernt.TryGetProperty(k.Name, out var g) && g.ValueKind == JsonValueKind.Object
                    && g.TryGetProperty("routes", out var r) && r.ValueKind == JsonValueKind.Array)
                {
                    // Die uebliche Schreibweise: am 2026-11-12 wurde "SHUTO CHARGE" gelernt.
                    var strecken = r.EnumerateArray().Select(x => Rivals.GameText.Schreibweise(x.GetString() ?? string.Empty))
                                    .Where(x => x.Length > 0).ToList();
                    var laengen = new List<double?>();
                    if (g.TryGetProperty("lengths", out var ls) && ls.ValueKind == JsonValueKind.Array)
                    {
                        laengen.AddRange(ls.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetDouble() : (double?)null));
                    }
                    while (laengen.Count < strecken.Count) { laengen.Add(null); }
                    var bekannt = k.Strecken.Count(s => strecken.Contains(s, StringComparer.OrdinalIgnoreCase));
                    if (strecken.Count > 0 && bekannt * 5 >= k.Strecken.Count * 4)
                    {
                        liste.Add(k with { Strecken = strecken, Laengen = laengen.Take(strecken.Count).ToList() });
                        if (g.TryGetProperty("learned", out var l) && l.ValueKind == JsonValueKind.String
                            && DateTime.TryParse(l.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t))
                        {
                            am[k.Name] = t.ToUniversalTime();
                        }
                        continue;
                    }
                }
                liste.Add(k);
            }
            var neu = new ScanRoutes(liste);
            foreach (var e in am) { neu._gelernt[e.Key] = e.Value; }
            return neu;
        }
        catch (Exception)
        {
            return basis;
        }
    }

    /// <summary>Eine im Spiel gelernte Liste ablegen; die anderen Kategorien der Datei bleiben.</summary>
    public static void Speichere(string kategorie, IReadOnlyList<string> strecken, IReadOnlyList<double?> laengen,
                                 string? datei = null)
    {
        datei ??= LernDatei;
        JsonObject wurzel;
        try
        {
            wurzel = File.Exists(datei) && JsonNode.Parse(File.ReadAllText(datei)) is JsonObject o ? o : new JsonObject();
        }
        catch (Exception)
        {
            wurzel = new JsonObject();
        }
        wurzel["format"] = 1;
        if (wurzel["categories"] is not JsonObject kategorien)
        {
            kategorien = new JsonObject();
            wurzel["categories"] = kategorien;
        }
        var ls = new JsonArray();
        foreach (var l in laengen) { ls.Add(l is { } km ? JsonValue.Create(Math.Round(km, 1)) : null); }
        kategorien[kategorie] = new JsonObject
        {
            ["routes"] = new JsonArray(strecken.Select(s => (JsonNode?)JsonValue.Create(Rivals.GameText.Schreibweise(s))).ToArray()),
            ["lengths"] = ls,
            ["learned"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(datei)!);
        var tmp = datei + ".tmp";
        File.WriteAllText(tmp, wurzel.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, datei, overwrite: true);
    }

    /// <summary>
    /// Wann das Spiel zuletzt seine Texte geaendert hat (UTC) -- ein Update, das Strecken bringt,
    /// bringt auch ihre Namen. Aus dem Ordner des laufenden Spiels; null, wenn es nicht zu sehen ist.
    /// </summary>
    public static DateTime? SpielStand()
    {
        try
        {
            var p = System.Diagnostics.Process.GetProcessesByName(GameWatch.DefaultProcessName).FirstOrDefault();
            var exe = p?.MainModule?.FileName;
            p?.Dispose();
            if (exe is null) { return null; }
            var en = Path.Combine(Path.GetDirectoryName(exe)!, "media", "Stripped", "StringTables", "EN.zip");
            return File.Exists(en) ? File.GetLastWriteTimeUtc(en) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Muss die Liste dieser Kategorie im Spiel (neu) gelernt werden?</summary>
    public bool LernenNoetig(string kategorie, DateTime? spielStand)
    {
        var am = GelerntAm(kategorie);
        if (am is null) { return true; }
        return spielStand is { } s && s > am.Value;
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
