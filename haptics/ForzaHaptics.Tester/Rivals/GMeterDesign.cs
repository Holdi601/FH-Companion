using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>Wie viele Richtungen das G-Meter zeigt.</summary>
internal enum GMeterDimension { One, Two, Three }

/// <summary>Die Form des G-Meters -- welche zu welcher Dimension gehoert, sagt <see cref="GMeterDesign.DimensionVon"/>.</summary>
internal enum GMeterLayout
{
    // 1D
    Bar, Column, Arc, Needle,
    // 2D
    Circle, Crosshair, Radar, Diamond, Heat, Cartesian,
    // 3D
    Perspective, Sphere, CircleWithHeight, Cartesian3D,
}

/// <summary>Welche Richtung ein eindimensionales G-Meter zeigt.</summary>
internal enum GMeterAxis { Lateral, Longitudinal, Vertical, Combined }

/// <summary>Wie die Spur der letzten Sekunden aussieht.</summary>
internal enum GMeterTrail { Off, Dots, Line, Glow }

/// <summary>
/// EIN G-METER, WIE ES AUSSIEHT (seit 2026-10-24, Wunsch des Nutzers: "Gmeter mit Richtungen der Gs
/// und selbst stylebar ... 1, 2 oder 3 dimensional"). Wie <see cref="SpeedoDesign"/>: jede Vorlage ist
/// ein Satz Werte, eigene liegen unter DataFolder/gmeter/*.json.
/// </summary>
internal sealed class GMeterDesign
{
    [JsonPropertyName("name")] public string Name { get; set; } = "My G-meter";
    [JsonPropertyName("layout")] public GMeterLayout Layout { get; set; } = GMeterLayout.Circle;
    [JsonPropertyName("axis")] public GMeterAxis Axis { get; set; } = GMeterAxis.Lateral;
    [JsonPropertyName("size")] public int Size { get; set; } = 240;

    // ---- Skala ----
    [JsonPropertyName("maxG")] public float MaxG { get; set; } = 2f;
    [JsonPropertyName("rings")] public int Rings { get; set; } = 4;
    [JsonPropertyName("scaleNumbers")] public bool ScaleNumbers { get; set; } = true;
    [JsonPropertyName("directionLabels")] public bool DirectionLabels { get; set; } = true;
    /// <summary>Die Kraft, die man spuert (nach aussen in der Kurve, nach vorn beim Bremsen) -- sonst die Beschleunigung des Autos.</summary>
    [JsonPropertyName("feltForce")] public bool FeltForce { get; set; } = true;

    // ---- Was es zeigt ----
    [JsonPropertyName("trail")] public GMeterTrail Trail { get; set; } = GMeterTrail.Line;
    [JsonPropertyName("trailSeconds")] public float TrailSeconds { get; set; } = 2f;
    [JsonPropertyName("envelope")] public bool Envelope { get; set; } = true;
    [JsonPropertyName("peaks")] public bool Peaks { get; set; } = true;
    [JsonPropertyName("numbers")] public bool Numbers { get; set; } = true;
    [JsonPropertyName("colourByG")] public bool ColourByG { get; set; } = true;
    [JsonPropertyName("dotSize")] public float DotSize { get; set; } = 1f;

    // ---- Farben ----
    [JsonPropertyName("background")] public string Background { get; set; } = "#C0101216";
    [JsonPropertyName("frameColour")] public string FrameColour { get; set; } = "#FF505A66";
    [JsonPropertyName("gridColour")] public string GridColour { get; set; } = "#40FFFFFF";
    [JsonPropertyName("dotColour")] public string DotColour { get; set; } = "#FFFFFFFF";
    [JsonPropertyName("lowColour")] public string LowColour { get; set; } = "#FF22C55E";
    [JsonPropertyName("highColour")] public string HighColour { get; set; } = "#FFEF4444";
    [JsonPropertyName("trailColour")] public string TrailColour { get; set; } = "#FF38BDF8";
    [JsonPropertyName("envelopeColour")] public string EnvelopeColour { get; set; } = "#66F59E0B";
    [JsonPropertyName("textColour")] public string TextColour { get; set; } = "#FFF5F7FA";
    [JsonPropertyName("dimColour")] public string DimColour { get; set; } = "#FF8B95A3";

    // ---- Licht, Rahmen, Schrift ----
    [JsonPropertyName("glow")] public float Glow { get; set; } = 0.3f;
    [JsonPropertyName("smoothing")] public float Smoothing { get; set; } = 0.4f;
    [JsonPropertyName("frame")] public SpeedoFrame Frame { get; set; } = SpeedoFrame.Bezel;
    [JsonPropertyName("plate")] public bool ShowPlate { get; set; } = true;
    [JsonPropertyName("cornerRadius")] public float CornerRadius { get; set; } = 14f;
    [JsonPropertyName("font")] public SpeedoFont Font { get; set; } = SpeedoFont.Condensed;
    [JsonPropertyName("italic")] public bool Italic { get; set; }

    [JsonIgnore] public bool BuiltIn { get; set; }

    /// <summary>Bei Vorlagen aus einem Farbthema: welches -- fuer den Namen in der Galerie.</summary>
    [JsonIgnore] public string? Thema { get; set; }

    [JsonIgnore] public GMeterDimension Dimension => DimensionVon(Layout);

    public static GMeterDimension DimensionVon(GMeterLayout l) => l switch
    {
        GMeterLayout.Bar or GMeterLayout.Column or GMeterLayout.Arc or GMeterLayout.Needle => GMeterDimension.One,
        GMeterLayout.Perspective or GMeterLayout.Sphere or GMeterLayout.CircleWithHeight or GMeterLayout.Cartesian3D => GMeterDimension.Three,
        _ => GMeterDimension.Two,
    };

    public static IEnumerable<GMeterLayout> LayoutsVon(GMeterDimension d) =>
        Enum.GetValues<GMeterLayout>().Where(l => DimensionVon(l) == d);

    /// <summary>Hoehe zu Breite je Layout.</summary>
    [JsonIgnore]
    public float Hochkant => Layout switch
    {
        GMeterLayout.Bar => 0.3f,
        GMeterLayout.Column => 2.4f,
        GMeterLayout.Arc => 0.62f,
        GMeterLayout.Perspective => 0.9f,
        GMeterLayout.CircleWithHeight => 0.82f,
        GMeterLayout.Cartesian3D => 0.86f,
        _ => 1f,
    };

    private static readonly JsonSerializerOptions Optionen = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public GMeterDesign Clone()
    {
        var k = JsonSerializer.Deserialize<GMeterDesign>(JsonSerializer.Serialize(this, Optionen), Optionen)!;
        k.BuiltIn = false;
        return k;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Optionen);

    public static GMeterDesign? FromJson(string text)
    {
        try { return JsonSerializer.Deserialize<GMeterDesign>(text, Optionen)?.Bereinigt(); }
        catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentException) { return null; }
    }

    public GMeterDesign Bereinigt()
    {
        Name = string.IsNullOrWhiteSpace(Name) ? "G-meter" : Name.Trim()[..Math.Min(48, Name.Trim().Length)];
        Size = Math.Clamp(Size, 80, 800);
        MaxG = Math.Clamp(float.IsNaN(MaxG) ? 2f : MaxG, 0.5f, 6f);
        Rings = Math.Clamp(Rings, 1, 10);
        TrailSeconds = Math.Clamp(float.IsNaN(TrailSeconds) ? 2f : TrailSeconds, 0f, 10f);
        DotSize = Math.Clamp(float.IsNaN(DotSize) ? 1f : DotSize, 0.3f, 3f);
        Glow = Math.Clamp(float.IsNaN(Glow) ? 0f : Glow, 0f, 1f);
        Smoothing = Math.Clamp(float.IsNaN(Smoothing) ? 0.4f : Smoothing, 0f, 0.95f);
        CornerRadius = Math.Clamp(CornerRadius, 0f, 80f);
        return this;
    }
}

/// <summary>Die eingebauten G-Meter und die eigenen aus DataFolder/gmeter.</summary>
internal static class GMeterDesigns
{
    public static string Ordner => Path.Combine(AppInfo.DataFolder, "gmeter");

    public static IReadOnlyList<GMeterDesign> Eingebaut { get; } = Baue();

    private static List<GMeterDesign> Baue()
    {
        var liste = new List<GMeterDesign>
        {
            new() { Name = "Classic circle", Layout = GMeterLayout.Circle, Frame = SpeedoFrame.Bezel, Trail = GMeterTrail.Line },
            new()
            {
                Name = "Racing crosshair", Layout = GMeterLayout.Crosshair, Frame = SpeedoFrame.Plate, Background = "#E00A0B0D",
                GridColour = "#55FFFFFF", DotColour = "#FFFFD400", TrailColour = "#FFFFD400", LowColour = "#FFFFD400", HighColour = "#FFFF3B30",
                Trail = GMeterTrail.Dots, Envelope = false, Font = SpeedoFont.Mono,
            },
            new()
            {
                Name = "Neon radar", Layout = GMeterLayout.Radar, Frame = SpeedoFrame.Outline, Background = "#B0050510",
                FrameColour = "#FFFF2BD6", GridColour = "#5500E5FF", DotColour = "#FF00E5FF", TrailColour = "#FFFF2BD6",
                LowColour = "#FF00E5FF", HighColour = "#FFFF2BD6", EnvelopeColour = "#55FF2BD6", Glow = 0.9f, Trail = GMeterTrail.Glow,
                TrailSeconds = 3f,
            },
            new()
            {
                Name = "Heat map", Layout = GMeterLayout.Heat, Frame = SpeedoFrame.Glass, Background = "#D0101318",
                GridColour = "#30FFFFFF", Trail = GMeterTrail.Off, Envelope = false, LowColour = "#FF1D4ED8", HighColour = "#FFFDE047",
            },
            new()
            {
                Name = "Friction diamond", Layout = GMeterLayout.Diamond, Frame = SpeedoFrame.Angled, Background = "#C0081018",
                FrameColour = "#FF5EEAD4", GridColour = "#405EEAD4", DotColour = "#FFE6FFFB", TrailColour = "#FF5EEAD4",
                EnvelopeColour = "#555EEAD4", Font = SpeedoFont.Mono, Glow = 0.5f,
            },
            new()
            {
                Name = "Cyberpunk", Layout = GMeterLayout.Crosshair, Frame = SpeedoFrame.Angled, Background = "#D0100A12",
                FrameColour = "#FFFCEE09", GridColour = "#6602D7F2", DotColour = "#FFFCEE09", TrailColour = "#FF02D7F2",
                LowColour = "#FF02D7F2", HighColour = "#FFFF003C", EnvelopeColour = "#55FF003C", TextColour = "#FFFCEE09",
                DimColour = "#FF02D7F2", Glow = 0.6f, Font = SpeedoFont.Mono, Italic = true, Trail = GMeterTrail.Glow,
            },
            new() { Name = "Minimal dot", Layout = GMeterLayout.Circle, Frame = SpeedoFrame.None, Background = "#00000000", GridColour = "#33FFFFFF",
                    Trail = GMeterTrail.Off, Envelope = false, ScaleNumbers = false, DirectionLabels = false, Numbers = false, Glow = 0f, Size = 160 },
            new()
            {
                Name = "Lateral bar", Layout = GMeterLayout.Bar, Axis = GMeterAxis.Lateral, Size = 320, Frame = SpeedoFrame.Plate,
                Background = "#D0111317", LowColour = "#FF38BDF8", HighColour = "#FFF43F5E",
            },
            new()
            {
                Name = "Brake and throttle", Layout = GMeterLayout.Column, Axis = GMeterAxis.Longitudinal, Size = 90, Frame = SpeedoFrame.Plate,
                Background = "#D0111317", LowColour = "#FF22C55E", HighColour = "#FFEF4444",
            },
            new()
            {
                Name = "Combined arc", Layout = GMeterLayout.Arc, Axis = GMeterAxis.Combined, Size = 260, Frame = SpeedoFrame.None,
                Background = "#00000000", LowColour = "#FFFFFFFF", HighColour = "#FFFF2D55", GridColour = "#55FFFFFF", Italic = true,
            },
            new()
            {
                Name = "G needle", Layout = GMeterLayout.Needle, Axis = GMeterAxis.Combined, Frame = SpeedoFrame.Bezel, Background = "#E8121418",
                FrameColour = "#FFB8C0CA", DotColour = "#FFFF3B30", Font = SpeedoFont.Sans, Size = 200,
            },
            new()
            {
                Name = "Perspective disc", Layout = GMeterLayout.Perspective, Frame = SpeedoFrame.None, Background = "#00000000",
                GridColour = "#55FFFFFF", TrailColour = "#FF60A5FA", Glow = 0.4f, Size = 280,
            },
            new()
            {
                Name = "Sphere", Layout = GMeterLayout.Sphere, Frame = SpeedoFrame.None, Background = "#00000000",
                GridColour = "#40FFFFFF", DotColour = "#FFFDE68A", TrailColour = "#FFF59E0B", Glow = 0.5f,
            },
            new()
            {
                Name = "Circle and height", Layout = GMeterLayout.CircleWithHeight, Frame = SpeedoFrame.Plate, Background = "#C8101317",
                Trail = GMeterTrail.Line, Size = 280,
            },
        };
        liste.Add(new GMeterDesign
        {
            Name = "Cartesian axes", Layout = GMeterLayout.Cartesian, Frame = SpeedoFrame.Plate, Background = "#C8101317",
            Trail = GMeterTrail.Line, Envelope = false, Size = 260,
        });
        liste.Add(new GMeterDesign
        {
            Name = "Cartesian 3D", Layout = GMeterLayout.Cartesian3D, Frame = SpeedoFrame.Plate, Background = "#C8101317",
            Trail = GMeterTrail.Line, Envelope = false, Size = 300,
        });
        // JEDES FARBTHEMA AUF EINER FORM (seit 2026-10-24, der Nutzer: "viel zu wenig"): reihum durch alle
        // Formen, damit jede in mehreren Farben vorkommt. Der Name setzt sich in der Galerie zusammen.
        var formen = new[]
        {
            GMeterLayout.Cartesian3D, GMeterLayout.Radar, GMeterLayout.Circle, GMeterLayout.Cartesian, GMeterLayout.Crosshair,
            GMeterLayout.Perspective, GMeterLayout.Diamond, GMeterLayout.Heat, GMeterLayout.Sphere, GMeterLayout.CircleWithHeight,
            GMeterLayout.Arc, GMeterLayout.Needle, GMeterLayout.Bar, GMeterLayout.Column,
        };
        for (var i = 0; i < Farbthemen.Alle.Count; i++)
        {
            var thema = Farbthemen.Alle[i];
            var form = formen[i % formen.Length];
            var d = new GMeterDesign
            {
                Name = thema.Name + " " + form, Layout = form, Thema = thema.Name,
                Axis = form is GMeterLayout.Column ? GMeterAxis.Longitudinal : form is GMeterLayout.Bar ? GMeterAxis.Lateral : GMeterAxis.Combined,
                Frame = SpeedoFrame.Plate, Trail = i % 3 == 0 ? GMeterTrail.Glow : GMeterTrail.Line, Envelope = i % 2 == 0,
                Size = form is GMeterLayout.Bar ? 320 : form is GMeterLayout.Column ? 90 : 260,
            };
            Farbthemen.Auf(d, thema);
            liste.Add(d);
        }
        foreach (var d in liste) { d.BuiltIn = true; d.Bereinigt(); }
        return liste;
    }

    public static List<GMeterDesign> Eigene()
    {
        var raus = new List<GMeterDesign>();
        try
        {
            if (!Directory.Exists(Ordner)) { return raus; }
            foreach (var datei in Directory.EnumerateFiles(Ordner, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                try { if (GMeterDesign.FromJson(File.ReadAllText(datei)) is { } d) { raus.Add(d); } }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return raus;
    }

    public static List<GMeterDesign> Alle() => Eingebaut.Concat(Eigene()).ToList();

    public static GMeterDesign Finde(string? name) =>
        Eigene().FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? Eingebaut.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? Eingebaut[0];

    private static (string Name, GMeterDesign Design)? _gewaehlt;

    public static GMeterDesign Gewaehlt(OverlaySettings s)
    {
        var name = s.GMeterDesign ?? string.Empty;
        if (_gewaehlt is { } g && string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)) { return g.Design; }
        var d = Finde(name);
        _gewaehlt = (name, d);
        return d;
    }

    public static void Vergessen() => _gewaehlt = null;

    public static string DateiVon(string name) => Path.Combine(Ordner, Path.GetFileName(SpeedoDesigns.DateiVon(name)));

    public static void Speichern(GMeterDesign d)
    {
        if (d.BuiltIn) { throw new InvalidOperationException("built-in designs are read-only"); }
        Directory.CreateDirectory(Ordner);
        var ziel = DateiVon(d.Name);
        File.WriteAllText(ziel + ".tmp", d.ToJson());
        File.Move(ziel + ".tmp", ziel, overwrite: true);
    }

    public static void Loeschen(GMeterDesign d)
    {
        if (d.BuiltIn) { return; }
        var datei = DateiVon(d.Name);
        if (File.Exists(datei)) { File.Delete(datei); }
    }

    public static string FreierName(string basis)
    {
        var belegt = new HashSet<string>(Alle().Select(d => d.Name), StringComparer.OrdinalIgnoreCase);
        var kern = basis.EndsWith(')') && basis.Contains(" (", StringComparison.Ordinal) ? basis[..basis.LastIndexOf(" (", StringComparison.Ordinal)] : basis;
        var name = kern + " (mine)";
        for (var i = 2; belegt.Contains(name); i++) { name = $"{kern} (mine {i})"; }
        return name;
    }
}
