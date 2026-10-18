using System.Drawing;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>Wie Drehzahl und Tempo gelegt sind -- das Grundgeruest eines Tachos.</summary>
internal enum SpeedoLayout
{
    /// <summary>Runder Zeiger-Tacho: Drehzahl auf dem Kreisbogen, Tempo in der Mitte.</summary>
    Dial,
    /// <summary>Offener Bogen, unten rechts gedacht (wie Rennspiele ihn zeigen): dicke Fuellung, keine Nadel.</summary>
    Arc,
    /// <summary>Waagrechter Balken, Tempo und Gang daneben.</summary>
    Bar,
    /// <summary>Senkrechte Saeule.</summary>
    Vertical,
    /// <summary>Zwei Rundinstrumente: Tempo links, Drehzahl rechts.</summary>
    Twin,
    /// <summary>Renn-Display: Schaltlichter oben, grosse Ziffern, duenne Drehzahllinie.</summary>
    Digital,
    /// <summary>Motorrad: ansteigende Drehzahlkurve ueber einem LCD-Feld.</summary>
    Moto,
}

/// <summary>Die Form der Drehzahlfuellung.</summary>
internal enum SpeedoFill { Smooth, Segments, Blocks, Ticks }

/// <summary>Die Nadel eines runden Instruments.</summary>
internal enum SpeedoNeedle { None, Classic, Line, Glow, Wedge }

/// <summary>Die Schrift der Ziffern.</summary>
internal enum SpeedoFont { Sans, Condensed, Mono, Digital }

/// <summary>Der Rahmen um das Ganze.</summary>
internal enum SpeedoFrame { None, Plate, Bezel, Glass, Outline, Angled }

/// <summary>Welche Uhr unten steht.</summary>
internal enum SpeedoClock { Off, Real24, Real12, RaceTime, LapTime }

/// <summary>Welche Strecke unten steht.</summary>
internal enum SpeedoDistance { Off, Trip, Race, Odometer }

/// <summary>Wo der rote Bereich beginnt.</summary>
internal enum SpeedoRedline
{
    /// <summary>Gelernt: dort, wo dieses Auto bei Vollgas die meiste Leistung hatte (sonst 90 % der Hoechstdrehzahl).</summary>
    Learned,
    /// <summary>Fest: ein Anteil der Hoechstdrehzahl.</summary>
    Percent,
}

/// <summary>
/// EIN TACHO, WIE ER AUSSIEHT (seit 2026-10-18, Wunsch des Nutzers: "a TON of options").
/// </summary>
/// <remarks>
/// Jede eingebaute Vorlage ist ein solcher Satz Werte, und jede eigene ebenso -- es gibt keinen
/// zweiten Weg, einen Tacho zu bauen. Gezeichnet wird er von <see cref="SpeedoRenderer"/>, im
/// Spiel wie in der Vorschau (eine Zeichnung, nie zwei). Farben als "#AARRGGBB", damit die
/// Datei lesbar bleibt; gespeichert unter DataFolder/speedo/*.json.
/// </remarks>
internal sealed class SpeedoDesign
{
    // ---- Allgemein ----
    [JsonPropertyName("name")] public string Name { get; set; } = "My speedometer";
    [JsonPropertyName("layout")] public SpeedoLayout Layout { get; set; } = SpeedoLayout.Dial;
    /// <summary>Grundgroesse in Bildpunkten bei 1080p (die Breite; die Hoehe folgt dem Layout).</summary>
    [JsonPropertyName("size")] public int Size { get; set; } = 300;

    // ---- Instrument ----
    [JsonPropertyName("startAngle")] public float StartAngle { get; set; } = 135f;
    [JsonPropertyName("sweepAngle")] public float SweepAngle { get; set; } = 270f;
    [JsonPropertyName("arcThickness")] public float ArcThickness { get; set; } = 10f;
    [JsonPropertyName("fill")] public SpeedoFill Fill { get; set; } = SpeedoFill.Smooth;
    [JsonPropertyName("segments")] public int Segments { get; set; } = 40;
    [JsonPropertyName("needle")] public SpeedoNeedle Needle { get; set; } = SpeedoNeedle.Classic;
    [JsonPropertyName("ticks")] public bool Ticks { get; set; } = true;
    [JsonPropertyName("tickNumbers")] public bool TickNumbers { get; set; } = true;
    [JsonPropertyName("minorTicks")] public int MinorTicks { get; set; } = 4;
    [JsonPropertyName("gradient")] public bool Gradient { get; set; } = true;

    // ---- Farben ----
    [JsonPropertyName("background")] public string Background { get; set; } = "#C0101216";
    [JsonPropertyName("frameColour")] public string FrameColour { get; set; } = "#FF505A66";
    [JsonPropertyName("trackColour")] public string TrackColour { get; set; } = "#40FFFFFF";
    [JsonPropertyName("lowColour")] public string LowColour { get; set; } = "#FF38BDF8";
    [JsonPropertyName("highColour")] public string HighColour { get; set; } = "#FFF0F0F0";
    [JsonPropertyName("redlineColour")] public string RedlineColour { get; set; } = "#FFEF4444";
    [JsonPropertyName("needleColour")] public string NeedleColour { get; set; } = "#FFFF3B30";
    [JsonPropertyName("textColour")] public string TextColour { get; set; } = "#FFF5F7FA";
    [JsonPropertyName("dimColour")] public string DimColour { get; set; } = "#FF8B95A3";
    [JsonPropertyName("accentColour")] public string AccentColour { get; set; } = "#FF38BDF8";

    // ---- Leuchten ----
    [JsonPropertyName("glow")] public float Glow { get; set; }
    [JsonPropertyName("glowFollowsRpm")] public bool GlowFollowsRpm { get; set; } = true;
    [JsonPropertyName("redlinePulse")] public bool RedlinePulse { get; set; } = true;
    [JsonPropertyName("limiterFlash")] public bool LimiterFlash { get; set; } = true;
    [JsonPropertyName("gearPop")] public bool GearPop { get; set; } = true;
    [JsonPropertyName("smoothing")] public float Smoothing { get; set; } = 0.5f;

    // ---- Rahmen und Schrift ----
    [JsonPropertyName("frame")] public SpeedoFrame Frame { get; set; } = SpeedoFrame.Bezel;
    [JsonPropertyName("cornerRadius")] public float CornerRadius { get; set; } = 14f;
    [JsonPropertyName("font")] public SpeedoFont Font { get; set; } = SpeedoFont.Sans;
    [JsonPropertyName("speedSize")] public float SpeedSize { get; set; } = 1f;
    [JsonPropertyName("gearSize")] public float GearSize { get; set; } = 1f;
    [JsonPropertyName("italic")] public bool Italic { get; set; }

    // ---- Was angezeigt wird ----
    [JsonPropertyName("showSpeed")] public bool ShowSpeed { get; set; } = true;
    [JsonPropertyName("showUnit")] public bool ShowUnit { get; set; } = true;
    [JsonPropertyName("showRpm")] public bool ShowRpm { get; set; } = true;
    [JsonPropertyName("showGear")] public bool ShowGear { get; set; } = true;
    [JsonPropertyName("showShiftLights")] public bool ShowShiftLights { get; set; }
    [JsonPropertyName("shiftLights")] public int ShiftLights { get; set; } = 10;
    [JsonPropertyName("shiftLightsFrom")] public float ShiftLightsFrom { get; set; } = 0.75f;
    [JsonPropertyName("showPower")] public bool ShowPower { get; set; } = true;
    [JsonPropertyName("showBoost")] public bool ShowBoost { get; set; }
    [JsonPropertyName("showPedals")] public bool ShowPedals { get; set; }
    [JsonPropertyName("showTraction")] public bool ShowTraction { get; set; } = true;
    [JsonPropertyName("tractionText")] public string TractionText { get; set; } = "TC";
    [JsonPropertyName("lockText")] public string LockText { get; set; } = "ABS";
    /// <summary>Die Stabilitaetslampe (STAB): warnt vor dem Wegrutschen in der Kurve.</summary>
    [JsonPropertyName("showStability")] public bool ShowStability { get; set; } = true;
    /// <summary>Schaltempfehlung am Gang: Pfeil hoch/runter aus der gelernten Leistungskurve.</summary>
    [JsonPropertyName("showShiftAdvice")] public bool ShowShiftAdvice { get; set; } = true;
    /// <summary>Den besten Gang fuer das Tempo klein neben dem Gang.</summary>
    [JsonPropertyName("showBestGear")] public bool ShowBestGear { get; set; } = true;
    /// <summary>Leer: das Zeichen (Auto mit Schleuderspuren) statt eines Textes.</summary>
    [JsonPropertyName("stabilityText")] public string StabilityText { get; set; } = string.Empty;
    [JsonPropertyName("clock")] public SpeedoClock Clock { get; set; } = SpeedoClock.Off;
    [JsonPropertyName("distance")] public SpeedoDistance Distance { get; set; } = SpeedoDistance.Off;

    // ---- Roter Bereich ----
    [JsonPropertyName("redline")] public SpeedoRedline Redline { get; set; } = SpeedoRedline.Learned;
    [JsonPropertyName("redlinePercent")] public float RedlinePercent { get; set; } = 0.9f;

    /// <summary>Eine eingebaute Vorlage -- nicht zu ueberschreiben, nur zu kopieren.</summary>
    [JsonIgnore] public bool BuiltIn { get; set; }

    private static readonly JsonSerializerOptions Optionen = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public SpeedoDesign Clone()
    {
        var kopie = JsonSerializer.Deserialize<SpeedoDesign>(JsonSerializer.Serialize(this, Optionen), Optionen)!;
        kopie.BuiltIn = false;
        return kopie;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Optionen);

    /// <summary>Aus JSON -- und jeder Wert in seinen Grenzen, auch aus einer fremden Datei.</summary>
    public static SpeedoDesign? FromJson(string text)
    {
        try
        {
            var d = JsonSerializer.Deserialize<SpeedoDesign>(text, Optionen);
            return d?.Bereinigt();
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Unsinnige Werte zuruecksetzen: eine Datei von Hand oder von jemand anderem.</summary>
    public SpeedoDesign Bereinigt()
    {
        Name = string.IsNullOrWhiteSpace(Name) ? "Speedometer" : Name.Trim()[..Math.Min(48, Name.Trim().Length)];
        Size = Math.Clamp(Size, 120, 900);
        SweepAngle = Math.Clamp(SweepAngle, 60f, 340f);
        StartAngle = ((StartAngle % 360f) + 360f) % 360f;
        ArcThickness = Math.Clamp(ArcThickness, 1f, 60f);
        Segments = Math.Clamp(Segments, 4, 120);
        MinorTicks = Math.Clamp(MinorTicks, 0, 9);
        Glow = Math.Clamp(float.IsNaN(Glow) ? 0f : Glow, 0f, 1f);
        Smoothing = Math.Clamp(float.IsNaN(Smoothing) ? 0.5f : Smoothing, 0f, 0.95f);
        CornerRadius = Math.Clamp(CornerRadius, 0f, 80f);
        SpeedSize = Math.Clamp(SpeedSize, 0.4f, 2.5f);
        GearSize = Math.Clamp(GearSize, 0.4f, 2.5f);
        ShiftLights = Math.Clamp(ShiftLights, 3, 20);
        ShiftLightsFrom = Math.Clamp(ShiftLightsFrom, 0.3f, 0.98f);
        RedlinePercent = Math.Clamp(RedlinePercent, 0.5f, 1f);
        TractionText = Kurz(TractionText, "TC");
        LockText = Kurz(LockText, "ABS");
        StabilityText = string.IsNullOrWhiteSpace(StabilityText) ? string.Empty : StabilityText.Trim()[..Math.Min(6, StabilityText.Trim().Length)];
        return this;
    }

    private static string Kurz(string? text, string vorgabe) =>
        string.IsNullOrWhiteSpace(text) ? vorgabe : text.Trim()[..Math.Min(6, text.Trim().Length)];

    public static Color Farbe(string? text, Color vorgabe) => OverlaySettings.ParseColour(text, vorgabe);

    public static string Text(Color c) =>
        "#" + c.A.ToString("X2", CultureInfo.InvariantCulture) + c.R.ToString("X2", CultureInfo.InvariantCulture)
        + c.G.ToString("X2", CultureInfo.InvariantCulture) + c.B.ToString("X2", CultureInfo.InvariantCulture);

    /// <summary>Seitenverhaeltnis (Hoehe zu Breite) je Layout.</summary>
    [JsonIgnore]
    public float Hochkant => Layout switch
    {
        SpeedoLayout.Dial => 1f,
        SpeedoLayout.Arc => 0.86f,
        SpeedoLayout.Bar => 0.36f,
        SpeedoLayout.Vertical => 2.1f,
        SpeedoLayout.Twin => 0.5f,
        SpeedoLayout.Digital => 0.5f,
        SpeedoLayout.Moto => 0.55f,
        _ => 1f,
    };
}

/// <summary>
/// Die eingebauten Tachos und die eigenen aus DataFolder/speedo.
/// </summary>
internal static class SpeedoDesigns
{
    public static string Ordner => Path.Combine(AppInfo.DataFolder, "speedo");

    /// <summary>Die Vorlagen -- jede ein eigener Stil, keine zwei gleich.</summary>
    public static IReadOnlyList<SpeedoDesign> Eingebaut { get; } = Baue();

    private static List<SpeedoDesign> Baue()
    {
        var liste = new List<SpeedoDesign>
        {
            new() // KLASSISCH: weisses Zifferblatt-Gefuehl, rote Nadel, Chromring.
            {
                Name = "Classic analogue", Layout = SpeedoLayout.Dial, Frame = SpeedoFrame.Bezel, Needle = SpeedoNeedle.Classic,
                Fill = SpeedoFill.Ticks, Gradient = false, ArcThickness = 6f, Background = "#E8121418",
                FrameColour = "#FFB8C0CA", LowColour = "#FFE8E8E8", HighColour = "#FFE8E8E8", TrackColour = "#00000000",
                NeedleColour = "#FFFF3B30", TextColour = "#FFF2F2F2", DimColour = "#FF9AA3AD", Glow = 0f,
                ShowPower = false, Clock = SpeedoClock.Real24, Distance = SpeedoDistance.Odometer, Font = SpeedoFont.Sans,
            },
            new() // WIE IM SPIEL: offener Bogen unten rechts, dicke Segmente, grosse Ziffern, Gang im Kreis.
            {
                Name = "Horizon style", Layout = SpeedoLayout.Arc, Frame = SpeedoFrame.None, Needle = SpeedoNeedle.None,
                Fill = SpeedoFill.Segments, Segments = 46, ArcThickness = 16f, StartAngle = 150f, SweepAngle = 240f,
                Background = "#00000000", TrackColour = "#55FFFFFF", LowColour = "#FFFFFFFF", HighColour = "#FFFFFFFF",
                RedlineColour = "#FFFF2D55", AccentColour = "#FFFF2D55", TextColour = "#FFFFFFFF", DimColour = "#CCFFFFFF",
                Glow = 0.15f, Font = SpeedoFont.Condensed, Italic = true, SpeedSize = 1.25f, Ticks = false, TickNumbers = true,
                ShowPower = false, ShowTraction = true,
            },
            new() // NEON: zwei Farben, die mit der Drehzahl heller leuchten.
            {
                Name = "Neon glow", Layout = SpeedoLayout.Dial, Frame = SpeedoFrame.Outline, Needle = SpeedoNeedle.Glow,
                Fill = SpeedoFill.Smooth, ArcThickness = 8f, Background = "#B0050510", FrameColour = "#FFFF2BD6",
                TrackColour = "#30FF2BD6", LowColour = "#FF00E5FF", HighColour = "#FFFF2BD6", RedlineColour = "#FFFF2B6A",
                NeedleColour = "#FF00E5FF", TextColour = "#FFE9FDFF", DimColour = "#FF8DE9F5", AccentColour = "#FFFF2BD6",
                Glow = 0.9f, Font = SpeedoFont.Condensed, Ticks = true, TickNumbers = false, MinorTicks = 1,
            },
            new() // FUTURISTISCH: feine Linien, Winkelrahmen, Mono-Ziffern.
            {
                Name = "Futuristic", Layout = SpeedoLayout.Arc, Frame = SpeedoFrame.Angled, Needle = SpeedoNeedle.Line,
                Fill = SpeedoFill.Ticks, Segments = 72, ArcThickness = 12f, StartAngle = 160f, SweepAngle = 220f,
                Background = "#A0081018", FrameColour = "#FF5EEAD4", TrackColour = "#2A5EEAD4", LowColour = "#FF5EEAD4",
                HighColour = "#FFA7F3D0", RedlineColour = "#FFFB7185", NeedleColour = "#FFFFFFFF", TextColour = "#FFE6FFFB",
                DimColour = "#FF6FB7AE", AccentColour = "#FF5EEAD4", Glow = 0.45f, Font = SpeedoFont.Mono,
                Clock = SpeedoClock.Real24, Distance = SpeedoDistance.Trip, ShowBoost = true,
            },
            new() // CYBERPUNK: Gelb und Tuerkis, harte Bloecke, schraeg.
            {
                Name = "Cyberpunk", Layout = SpeedoLayout.Bar, Frame = SpeedoFrame.Angled, Fill = SpeedoFill.Blocks, Segments = 24,
                Background = "#D0100A12", FrameColour = "#FFFCEE09", TrackColour = "#3302D7F2", LowColour = "#FF02D7F2",
                HighColour = "#FFFCEE09", RedlineColour = "#FFFF003C", TextColour = "#FFFCEE09", DimColour = "#FF02D7F2",
                AccentColour = "#FFFF003C", Glow = 0.6f, Font = SpeedoFont.Mono, Italic = true, ShowShiftLights = true,
                ShiftLights = 12, ShowBoost = true, ShowPedals = true, Clock = SpeedoClock.Real24,
            },
            new() // MOTORRAD: ansteigende Kurve, LCD darunter.
            {
                Name = "Motorcycle", Layout = SpeedoLayout.Moto, Frame = SpeedoFrame.Plate, Fill = SpeedoFill.Segments, Segments = 36,
                Background = "#E0141618", FrameColour = "#FF3A3F45", TrackColour = "#30FFFFFF", LowColour = "#FFFFFFFF",
                HighColour = "#FFFFB020", RedlineColour = "#FFFF3B30", TextColour = "#FFFFFFFF", DimColour = "#FFA0A8B0",
                AccentColour = "#FFFFB020", Font = SpeedoFont.Digital, Clock = SpeedoClock.Real24, Distance = SpeedoDistance.Trip,
                ShowShiftLights = false, Glow = 0.2f,
            },
            new() // RENN-DISPLAY: Schaltlichter oben, Gang riesig.
            {
                Name = "Race dash", Layout = SpeedoLayout.Digital, Frame = SpeedoFrame.Plate, Fill = SpeedoFill.Smooth,
                Background = "#F0050607", FrameColour = "#FF2A2E33", TrackColour = "#30FFFFFF", LowColour = "#FF22C55E",
                HighColour = "#FFFACC15", RedlineColour = "#FFEF4444", TextColour = "#FFFFFFFF", DimColour = "#FF9CA3AF",
                AccentColour = "#FF3B82F6", Font = SpeedoFont.Mono, ShowShiftLights = true, ShiftLights = 15,
                ShiftLightsFrom = 0.7f, GearSize = 1.35f, ShowPedals = true, Clock = SpeedoClock.LapTime,
                Distance = SpeedoDistance.Race,
            },
            new() // RETRO-LCD: gruene Ziffern der Achtziger.
            {
                Name = "Retro LCD", Layout = SpeedoLayout.Digital, Frame = SpeedoFrame.Glass, Fill = SpeedoFill.Blocks, Segments = 20,
                Background = "#F0061006", FrameColour = "#FF1F3A1F", TrackColour = "#2033FF66", LowColour = "#FF33FF66",
                HighColour = "#FF9DFF4D", RedlineColour = "#FFFF5533", TextColour = "#FF6CFF8E", DimColour = "#FF2E9C4A",
                AccentColour = "#FF33FF66", Font = SpeedoFont.Digital, Glow = 0.5f, ShowShiftLights = false,
                Clock = SpeedoClock.Real12, Distance = SpeedoDistance.Odometer, ShowPower = false,
            },
            new() // SCHLICHT: nur Zahl und ein Strich.
            {
                Name = "Minimal", Layout = SpeedoLayout.Bar, Frame = SpeedoFrame.None, Fill = SpeedoFill.Smooth, Background = "#00000000",
                TrackColour = "#33FFFFFF", LowColour = "#FFFFFFFF", HighColour = "#FFFFFFFF", RedlineColour = "#FFFF453A",
                TextColour = "#FFFFFFFF", DimColour = "#B3FFFFFF", Font = SpeedoFont.Sans, ShowPower = false,
                ShowTraction = false, Glow = 0f, ArcThickness = 4f,
            },
            new() // SAEULE: senkrecht, fuer den Bildrand.
            {
                Name = "Vertical column", Layout = SpeedoLayout.Vertical, Size = 140, Frame = SpeedoFrame.Plate, Fill = SpeedoFill.Segments,
                Segments = 30, Background = "#C00B0D10", FrameColour = "#FF2E3640", TrackColour = "#28FFFFFF",
                LowColour = "#FF22D3EE", HighColour = "#FFA78BFA", RedlineColour = "#FFF43F5E", TextColour = "#FFFFFFFF",
                DimColour = "#FF94A3B8", Font = SpeedoFont.Condensed, Glow = 0.3f, ShowBoost = true,
            },
            new() // ZWEI INSTRUMENTE: Tempo und Drehzahl nebeneinander.
            {
                Name = "Twin gauges", Layout = SpeedoLayout.Twin, Size = 460, Frame = SpeedoFrame.Bezel, Needle = SpeedoNeedle.Wedge,
                Fill = SpeedoFill.Ticks, ArcThickness = 5f, Background = "#E00E1013", FrameColour = "#FF8792A0",
                TrackColour = "#00000000", LowColour = "#FFEDEDED", HighColour = "#FFEDEDED", RedlineColour = "#FFE11D48",
                NeedleColour = "#FFFF8A00", TextColour = "#FFFFFFFF", DimColour = "#FF9AA4B0", Gradient = false,
                Clock = SpeedoClock.Real24, Distance = SpeedoDistance.Odometer,
            },
            new() // SPORTBOGEN: weiter Bogen mit Verlauf und Nadel.
            {
                Name = "Sport sweep", Layout = SpeedoLayout.Dial, Frame = SpeedoFrame.Glass, Needle = SpeedoNeedle.Wedge,
                Fill = SpeedoFill.Smooth, ArcThickness = 18f, StartAngle = 120f, SweepAngle = 300f, Background = "#C8101317",
                FrameColour = "#66FFFFFF", TrackColour = "#22FFFFFF", LowColour = "#FF22C55E", HighColour = "#FFF59E0B",
                RedlineColour = "#FFEF4444", NeedleColour = "#FFFFFFFF", TextColour = "#FFFFFFFF", DimColour = "#FFA1A1AA",
                Glow = 0.35f, Font = SpeedoFont.Condensed, ShowBoost = true, ShowPedals = true,
            },
            new() // RALLYE: Lichterleiste, Gang, Tempo -- sonst nichts.
            {
                Name = "Rally", Layout = SpeedoLayout.Bar, Frame = SpeedoFrame.Plate, Fill = SpeedoFill.Blocks, Segments = 16,
                Background = "#E0181A1C", FrameColour = "#FF3F4448", TrackColour = "#26FFFFFF", LowColour = "#FF60A5FA",
                HighColour = "#FFFDE047", RedlineColour = "#FFF87171", TextColour = "#FFFFFFFF", DimColour = "#FFA3A3A3",
                Font = SpeedoFont.Condensed, ShowShiftLights = true, ShiftLights = 9, GearSize = 1.3f, ShowPower = false,
                Distance = SpeedoDistance.Race,
            },
            new() // SYNTHWAVE: Abendrot und Violett, weiche Fuellung.
            {
                Name = "Synthwave", Layout = SpeedoLayout.Arc, Frame = SpeedoFrame.Glass, Needle = SpeedoNeedle.None,
                Fill = SpeedoFill.Smooth, ArcThickness = 14f, StartAngle = 180f, SweepAngle = 180f, Background = "#B0140A26",
                FrameColour = "#FFB15CFF", TrackColour = "#30B15CFF", LowColour = "#FFFF7A18", HighColour = "#FFFF2E97",
                RedlineColour = "#FFFFFFFF", TextColour = "#FFFFE3F3", DimColour = "#FFC9A3E8", AccentColour = "#FFFF2E97",
                Glow = 0.75f, Font = SpeedoFont.Condensed, Italic = true, Clock = SpeedoClock.Real24,
            },
            new() // KOHLEFASER-RENNWAGEN: dunkel, orange Nadel, Leistung gross.
            {
                Name = "Track day", Layout = SpeedoLayout.Dial, Frame = SpeedoFrame.Plate, Needle = SpeedoNeedle.Line,
                Fill = SpeedoFill.Segments, Segments = 60, ArcThickness = 7f, StartAngle = 150f, SweepAngle = 240f,
                Background = "#E8111111", FrameColour = "#FF2B2B2B", TrackColour = "#22FFFFFF", LowColour = "#FFFFFFFF",
                HighColour = "#FFFF7A00", RedlineColour = "#FFFF3B30", NeedleColour = "#FFFF7A00", TextColour = "#FFFFFFFF",
                DimColour = "#FF8E8E8E", Font = SpeedoFont.Mono, ShowBoost = true, Clock = SpeedoClock.RaceTime,
                Distance = SpeedoDistance.Race,
            },
        };
        foreach (var d in liste) { d.BuiltIn = true; d.Bereinigt(); }
        return liste;
    }

    /// <summary>Die eigenen Tachos aus dem Ordner -- kaputte Dateien fallen still weg.</summary>
    public static List<SpeedoDesign> Eigene()
    {
        var raus = new List<SpeedoDesign>();
        try
        {
            if (!Directory.Exists(Ordner)) { return raus; }
            foreach (var datei in Directory.EnumerateFiles(Ordner, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (SpeedoDesign.FromJson(File.ReadAllText(datei)) is { } d) { raus.Add(d); }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return raus;
    }

    private static (string Name, SpeedoDesign Design)? _gewaehlt;

    /// <summary>Das gewaehlte Design, gemerkt -- die Einrichtung zeichnet es bei jeder Mausbewegung.</summary>
    public static SpeedoDesign Gewaehlt(OverlaySettings s)
    {
        var name = s.SpeedoDesign ?? string.Empty;
        if (_gewaehlt is { } g && string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)) { return g.Design; }
        var d = Finde(name);
        _gewaehlt = (name, d);
        return d;
    }

    /// <summary>Nach dem Speichern oder Wechseln: beim naechsten Mal neu lesen.</summary>
    public static void Vergessen() => _gewaehlt = null;

    /// <summary>Alle: erst die eingebauten, dann die eigenen.</summary>
    public static List<SpeedoDesign> Alle() => Eingebaut.Concat(Eigene()).ToList();

    /// <summary>Der Tacho dieses Namens -- sonst der erste eingebaute.</summary>
    public static SpeedoDesign Finde(string? name)
    {
        var eigene = Eigene();
        return eigene.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
               ?? Eingebaut.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
               ?? Eingebaut[0];
    }

    /// <summary>Ein Dateiname aus dem Namen -- nur Buchstaben, Ziffern, Strich.</summary>
    public static string DateiVon(string name)
    {
        var sauber = new string(name.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray()).Trim('-');
        while (sauber.Contains("--", StringComparison.Ordinal)) { sauber = sauber.Replace("--", "-", StringComparison.Ordinal); }
        return Path.Combine(Ordner, (sauber.Length == 0 ? "speedo" : sauber) + ".json");
    }

    public static void Speichern(SpeedoDesign d)
    {
        if (d.BuiltIn) { throw new InvalidOperationException("built-in designs are read-only"); }
        Directory.CreateDirectory(Ordner);
        var ziel = DateiVon(d.Name);
        var neu = ziel + ".tmp";
        File.WriteAllText(neu, d.ToJson());
        File.Move(neu, ziel, overwrite: true);
    }

    public static void Loeschen(SpeedoDesign d)
    {
        if (d.BuiltIn) { return; }
        var datei = DateiVon(d.Name);
        if (File.Exists(datei)) { File.Delete(datei); }
    }

    /// <summary>Ein freier Name: "Neon glow (mine)", "Neon glow (mine 2)", ...</summary>
    public static string FreierName(string basis)
    {
        var belegt = new HashSet<string>(Alle().Select(d => d.Name), StringComparer.OrdinalIgnoreCase);
        var kern = basis.EndsWith(')') && basis.Contains(" (", StringComparison.Ordinal) ? basis[..basis.LastIndexOf(" (", StringComparison.Ordinal)] : basis;
        var name = kern + " (mine)";
        for (var i = 2; belegt.Contains(name); i++) { name = $"{kern} (mine {i})"; }
        return name;
    }
}
