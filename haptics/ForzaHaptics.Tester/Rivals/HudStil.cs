using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>Die HUD-Stuecke, die einen Stil tragen (Tacho und G-Meter haben ihren eigenen Baukasten).</summary>
internal enum HudElement { Delta, Tyres, LiveMap, Course, CarNote }

/// <summary>Was ein Element vom gewaehlten Stil behaelt, ueber die Farben hinaus: Rahmen, Ecken, Leuchten, Schrift.</summary>
internal sealed class HudStilWahl
{
    [JsonPropertyName("name")] public string Name { get; set; } = "Default";
    [JsonPropertyName("frame")] public SpeedoFrame Frame { get; set; } = SpeedoFrame.Plate;
    [JsonPropertyName("frameColour")] public string FrameColour { get; set; } = "#00000000";
    [JsonPropertyName("corner")] public float Corner { get; set; } = -1f;
    [JsonPropertyName("glow")] public float Glow { get; set; }
    [JsonPropertyName("font")] public SpeedoFont Font { get; set; } = SpeedoFont.Sans;
    [JsonPropertyName("italic")] public bool Italic { get; set; }
}

/// <summary>
/// EIN STIL FUER DIE UEBRIGEN HUD-STUECKE (seit 2026-10-24, Wunsch des Nutzers: "alle UI-Elemente, die
/// man auf dem HUD anzeigen kann, sollten verschiedene Stile haben, die man selbst designen kann").
/// </summary>
/// <remarks>
/// Ein Stil nennt ROLLEN -- Hintergrund, Text, Leise, Akzent, Linie, Gut, Schlecht -- und das Element
/// legt sie auf seine Farben: der Delta-Streifen "Gut" auf "vorn", die Live-Karte "Akzent" auf die
/// Spur. Dazu Rahmen, Ecken, Leuchten und Schrift (<see cref="HudStilWahl"/>). Eigene Stile entstehen
/// aus dem, was gerade eingestellt ist ("Save as my style"), und liegen unter DataFolder/hudstyles.
/// </remarks>
internal sealed class HudStil
{
    [JsonPropertyName("name")] public string Name { get; set; } = "My style";
    [JsonPropertyName("background")] public string Background { get; set; } = "#96080B10";
    [JsonPropertyName("text")] public string Text { get; set; } = "#FFE7ECF3";
    [JsonPropertyName("dim")] public string Dim { get; set; } = "#FF93A2B5";
    [JsonPropertyName("accent")] public string Accent { get; set; } = "#FF22B8E6";
    [JsonPropertyName("line")] public string Line { get; set; } = "#FF7FD3FF";
    [JsonPropertyName("good")] public string Good { get; set; } = "#FF7EE787";
    [JsonPropertyName("bad")] public string Bad { get; set; } = "#FFFF6B6B";
    [JsonPropertyName("frame")] public SpeedoFrame Frame { get; set; } = SpeedoFrame.Plate;
    [JsonPropertyName("frameColour")] public string FrameColour { get; set; } = "#00000000";
    [JsonPropertyName("corner")] public float Corner { get; set; } = -1f;
    [JsonPropertyName("glow")] public float Glow { get; set; }
    [JsonPropertyName("font")] public SpeedoFont Font { get; set; } = SpeedoFont.Sans;
    [JsonPropertyName("italic")] public bool Italic { get; set; }

    [JsonIgnore] public bool BuiltIn { get; set; }

    private static readonly JsonSerializerOptions Optionen = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public string ToJson() => JsonSerializer.Serialize(this, Optionen);

    public static HudStil? FromJson(string text)
    {
        try { return JsonSerializer.Deserialize<HudStil>(text, Optionen); }
        catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentException) { return null; }
    }
}

internal static class HudStile
{
    private static string Mit(string farbe, int deck)
    {
        var c = SpeedoDesign.Farbe(farbe, Color.Black);
        return OverlaySettings.ToHex(Color.FromArgb(Math.Clamp(deck, 0, 255), c));
    }

    public static string Ordner => Path.Combine(AppInfo.DataFolder, "hudstyles");

    /// <summary>"Default" ist das Aussehen bis 2026-10-24 -- wer nichts waehlt, sieht nichts Neues.</summary>
    public static IReadOnlyList<HudStil> Eingebaut { get; } = new List<HudStil>
    {
        new() { Name = "Default" },
        new() { Name = "Neon", Background = "#B0050510", Text = "#FFE9FDFF", Dim = "#FF8DE9F5", Accent = "#FFFF2BD6", Line = "#FF00E5FF",
                Good = "#FF00FFA3", Bad = "#FFFF2B6A", Frame = SpeedoFrame.Outline, FrameColour = "#FFFF2BD6", Glow = 0.8f, Font = SpeedoFont.Condensed },
        new() { Name = "Cyberpunk", Background = "#D0100A12", Text = "#FFFCEE09", Dim = "#FF02D7F2", Accent = "#FFFF003C", Line = "#FF02D7F2",
                Good = "#FF02D7F2", Bad = "#FFFF003C", Frame = SpeedoFrame.Angled, FrameColour = "#FFFCEE09", Glow = 0.5f, Font = SpeedoFont.Mono, Italic = true },
        new() { Name = "Glass", Background = "#70FFFFFF", Text = "#FF0B1220", Dim = "#FF334155", Accent = "#FF0EA5E9", Line = "#FF0F172A",
                Good = "#FF15803D", Bad = "#FFB91C1C", Frame = SpeedoFrame.Glass, FrameColour = "#AAFFFFFF", Corner = 16f },
        new() { Name = "Classic", Background = "#E8121418", Text = "#FFF2F2F2", Dim = "#FF9AA3AD", Accent = "#FFFF3B30", Line = "#FFE8E8E8",
                Good = "#FF4ADE80", Bad = "#FFFF3B30", Frame = SpeedoFrame.Bezel, FrameColour = "#FFB8C0CA", Corner = 10f },
        new() { Name = "Minimal", Background = "#40000000", Text = "#FFFFFFFF", Dim = "#B3FFFFFF", Accent = "#FFFFFFFF", Line = "#FFFFFFFF",
                Good = "#FFFFFFFF", Bad = "#FFFF453A", Frame = SpeedoFrame.None, Corner = 4f },
        new() { Name = "Racing", Background = "#F0050607", Text = "#FFFFFFFF", Dim = "#FF9CA3AF", Accent = "#FFFACC15", Line = "#FFFACC15",
                Good = "#FF22C55E", Bad = "#FFEF4444", Frame = SpeedoFrame.Plate, FrameColour = "#FF2A2E33", Corner = 6f, Font = SpeedoFont.Condensed, Italic = true },
        new() { Name = "Synthwave", Background = "#B0140A26", Text = "#FFFFE3F3", Dim = "#FFC9A3E8", Accent = "#FFFF2E97", Line = "#FFFF7A18",
                Good = "#FF2DE2E6", Bad = "#FFFF2E97", Frame = SpeedoFrame.Glass, FrameColour = "#FFB15CFF", Glow = 0.6f, Font = SpeedoFont.Condensed, Italic = true },
    }.Select(s => { s.BuiltIn = true; return s; }).ToList();

    public static List<HudStil> Eigene()
    {
        var raus = new List<HudStil>();
        try
        {
            if (!Directory.Exists(Ordner)) { return raus; }
            foreach (var datei in Directory.EnumerateFiles(Ordner, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                try { if (HudStil.FromJson(File.ReadAllText(datei)) is { } s) { raus.Add(s); } }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return raus;
    }

    public static List<HudStil> Alle() => Eingebaut.Concat(Eigene()).ToList();

    public static HudStil? Finde(string? name) =>
        Alle().FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    public static void Speichern(HudStil s)
    {
        Directory.CreateDirectory(Ordner);
        var ziel = Path.Combine(Ordner, Path.GetFileName(SpeedoDesigns.DateiVon(s.Name)));
        File.WriteAllText(ziel, s.ToJson());
    }

    /// <summary>Die Wahl eines Elements: Rahmen, Ecken, Leuchten, Schrift. Ohne Eintrag: das bisherige Aussehen.</summary>
    public static HudStilWahl WahlVon(OverlaySettings s, HudElement e) =>
        s.HudStyles is { } alle && alle.TryGetValue(e.ToString(), out var w) && w is not null ? w : Standard;

    private static readonly HudStilWahl Standard = new();

    /// <summary>
    /// Einen Stil auf ein Element legen: seine Farben und seine Wahl. "Default" setzt das bisherige
    /// Aussehen zurueck (die Werkswerte der Farben).
    /// </summary>
    public static void Anwenden(OverlaySettings s, HudElement e, HudStil st)
    {
        var werk = new OverlaySettings();
        var standard = st.BuiltIn && st.Name == "Default";
        string R(string rolle, string werkswert) => standard ? werkswert : rolle;
        switch (e)
        {
            case HudElement.Delta:
                s.HudBackground = R(st.Background, werk.HudBackground);
                s.HudColorNeutral = R(st.Text, werk.HudColorNeutral);
                s.HudColorLabel = R(st.Dim, werk.HudColorLabel);
                s.HudColorAhead = R(st.Good, werk.HudColorAhead);
                s.HudColorBehind = R(st.Bad, werk.HudColorBehind);
                s.HudColorGhost = R(st.Accent, werk.HudColorGhost);
                s.HudColorMine = R(st.Text, werk.HudColorMine);
                s.HudColorTheirs = R(st.Accent, werk.HudColorTheirs);
                s.HudColorNow = R(st.Good, werk.HudColorNow);
                break;
            case HudElement.Tyres:
                s.TyresBack = R(st.Background, string.Empty);
                s.TyresInk = R(st.Dim, string.Empty);
                break;
            case HudElement.LiveMap:
                s.LiveMapBack = R(st.Background, string.Empty);
                s.LiveMapLine = R(st.Line, string.Empty);
                s.LiveMapTrail = R(st.Accent, werk.LiveMapTrail);
                s.LiveMapCar = R(st.Text, werk.LiveMapCar);
                break;
            case HudElement.Course:
            {
                // Farbe und Deckkraft stehen dort getrennt.
                var (farbe, deck) = Getrennt(R(st.Background, werk.CourseShapeBack), werk.CourseShapeBackAlpha, standard);
                s.CourseShapeBack = farbe;
                s.CourseShapeBackAlpha = deck;
                s.CourseShapeLine = R(st.Line, werk.CourseShapeLine);
                s.CourseShapeStart = R(st.Accent, werk.CourseShapeStart);
                break;
            }
            case HudElement.CarNote:
            {
                var (farbe, deck) = Getrennt(R(st.Background, werk.CarNoteBack), werk.CarNoteBackAlpha, standard);
                s.CarNoteBack = farbe;
                s.CarNoteBackAlpha = deck;
                s.CarNoteInk = R(st.Text, werk.CarNoteInk);
                s.CarNoteTitle = R(st.Accent, werk.CarNoteTitle);
                break;
            }
        }
        s.HudStyles ??= new();
        s.HudStyles[e.ToString()] = new HudStilWahl
        {
            Name = st.Name, Frame = st.Frame, FrameColour = st.FrameColour, Corner = st.Corner, Glow = st.Glow, Font = st.Font, Italic = st.Italic,
        };
    }

    /// <summary>"#AARRGGBB" in "#RRGGBB" und Deckkraft -- dort, wo das Element beides getrennt fuehrt.</summary>
    private static (string Farbe, int Deck) Getrennt(string text, int werkDeck, bool standard)
    {
        var c = SpeedoDesign.Farbe(text, Color.Black);
        return ($"#{c.R:x2}{c.G:x2}{c.B:x2}", standard ? werkDeck : c.A);
    }

    /// <summary>Was ein Element gerade traegt, als Stil -- fuer "Save as my style".</summary>
    public static HudStil AusEinstellungen(OverlaySettings s, HudElement e, string name)
    {
        var w = WahlVon(s, e);
        var st = new HudStil { Name = name, Frame = w.Frame, FrameColour = w.FrameColour, Corner = w.Corner, Glow = w.Glow, Font = w.Font, Italic = w.Italic };
        switch (e)
        {
            case HudElement.Delta:
                st.Background = s.HudBackground; st.Text = s.HudColorNeutral; st.Dim = s.HudColorLabel; st.Good = s.HudColorAhead;
                st.Bad = s.HudColorBehind; st.Accent = s.HudColorGhost; st.Line = s.HudColorNow;
                break;
            case HudElement.Tyres:
                st.Background = string.IsNullOrEmpty(s.TyresBack) ? s.HudBackground : s.TyresBack;
                st.Dim = string.IsNullOrEmpty(s.TyresInk) ? st.Dim : s.TyresInk;
                break;
            case HudElement.LiveMap:
                st.Background = string.IsNullOrEmpty(s.LiveMapBack) ? s.CourseShapeBack : s.LiveMapBack;
                st.Line = string.IsNullOrEmpty(s.LiveMapLine) ? s.CourseShapeLine : s.LiveMapLine;
                st.Accent = s.LiveMapTrail; st.Text = s.LiveMapCar;
                break;
            case HudElement.Course:
                st.Background = Mit(s.CourseShapeBack, s.CourseShapeBackAlpha); st.Line = s.CourseShapeLine; st.Accent = s.CourseShapeStart;
                break;
            case HudElement.CarNote:
                st.Background = Mit(s.CarNoteBack, s.CarNoteBackAlpha); st.Text = s.CarNoteInk; st.Accent = s.CarNoteTitle;
                break;
        }
        return st;
    }
}

/// <summary>Zeichnen nach dem Stil eines Elements: Platte, Rahmen, Leuchten, Schrift.</summary>
internal static class HudZeichnen
{
    /// <summary>
    /// Die Platte eines Elements. Ohne gewaehlten Stil genau wie bisher (gefuellt, abgerundet); sonst
    /// mit dem Rahmen des Stils -- dieselbe Zeichnung wie beim Tacho.
    /// </summary>
    public static void Platte(Graphics g, OverlaySettings s, HudElement e, RectangleF r, Color hintergrund, float standardRadius)
    {
        var w = HudStile.WahlVon(s, e);
        var radius = w.Corner >= 0f ? w.Corner : standardRadius;
        if (w.Frame == SpeedoFrame.Plate && w.Glow <= 0.01f && SpeedoDesign.Farbe(w.FrameColour, Color.Transparent).A == 0)
        {
            if (hintergrund.A == 0) { return; }
            using var pinsel = new SolidBrush(hintergrund);
            using var form = SpeedoRenderer.Platte(new SpeedoDesign { Frame = SpeedoFrame.Plate, CornerRadius = radius }, r, kreis: false);
            g.FillPath(pinsel, form);
            return;
        }
        var stil = new SpeedoDesign
        {
            Frame = w.Frame, Background = OverlaySettings.ToHex(hintergrund), FrameColour = w.FrameColour, CornerRadius = radius, Glow = w.Glow,
        };
        SpeedoRenderer.Rahmen(g, stil, r, kreis: false);
    }

    /// <summary>
    /// Eine Schrift im Stil des Elements: "Segoe UI" bleibt bei "Plain", sonst die Familie des Stils --
    /// fett bleibt fett, kursiv kommt dazu, wenn der Stil es will.
    /// </summary>
    public static Font Schrift(OverlaySettings s, HudElement e, string familie, float groesse, FontStyle stil = FontStyle.Regular,
                               GraphicsUnit einheit = GraphicsUnit.Point)
    {
        var w = HudStile.WahlVon(s, e);
        // Ohne Stil genau wie bisher -- und ohne jeden Umweg: der Streifen zeichnet zehnmal je Sekunde.
        if (w.Font == SpeedoFont.Sans && !w.Italic) { return new Font(familie, groesse, stil, einheit); }
        var fett = familie.Contains("Semibold", StringComparison.OrdinalIgnoreCase) || (stil & FontStyle.Bold) != 0;
        var name = w.Font switch
        {
            SpeedoFont.Condensed => fett ? "Bahnschrift SemiBold SemiConden" : "Bahnschrift SemiCondensed",
            SpeedoFont.Mono or SpeedoFont.Digital => "Consolas",
            _ => familie,
        };
        if (w.Font is SpeedoFont.Mono or SpeedoFont.Digital && fett) { stil |= FontStyle.Bold; }
        if (w.Italic) { stil |= FontStyle.Italic; }
        try
        {
            using var probe = new FontFamily(name);
            return new Font(name, groesse, stil, einheit);
        }
        catch (ArgumentException)
        {
            return new Font(familie, groesse, stil, einheit);
        }
    }
}
