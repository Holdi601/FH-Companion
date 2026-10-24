namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Ein Farbthema -- fuer Tacho, G-Meter und jedes HUD-Stueck dasselbe (seit 2026-10-24, der Nutzer:
/// "mir sinds noch nicht genug Styles"). Ein Thema traegt Farben, Rahmen, Leuchten und Schrift, aber
/// keine Form: es liegt auf jedem Layout.
/// </summary>
internal sealed record Farbthema(
    string Name, string Hintergrund, SpeedoFrame Rahmen, string RahmenFarbe, string Gitter,
    string Tief, string Hoch, string Akzent, string Rot, string Text, string Leise,
    float Leuchten, SpeedoFont Schrift, bool Kursiv = false, float Ecken = 14f);

internal static class Farbthemen
{
    public static IReadOnlyList<Farbthema> Alle { get; } = new List<Farbthema>
    {
        new("Vaporwave 80s", "#C01A0B2E", SpeedoFrame.Glass, "#FFFF71CE", "#5501CDFE", "#FF01CDFE", "#FFFF71CE", "#FFB967FF", "#FFFFFB96", "#FFFFF1FB", "#FFB967FF", 0.7f, SpeedoFont.Condensed, true),
        new("Outrun sunset", "#C0200A28", SpeedoFrame.Outline, "#FFFF6C11", "#55FD3777", "#FFFFD319", "#FFFF2975", "#FFF222FF", "#FFFF2975", "#FFFFF5E1", "#FFFF901F", 0.6f, SpeedoFont.Condensed, true),
        new("Miami", "#B0081A2A", SpeedoFrame.Outline, "#FFFF5AB8", "#4400E5D4", "#FF00E5D4", "#FFFF5AB8", "#FFFFD166", "#FFFF5AB8", "#FFF2FFFE", "#FF7FDCD3", 0.5f, SpeedoFont.Sans, true),
        new("Tokyo neon", "#C00A0A14", SpeedoFrame.Outline, "#FF00F5FF", "#55FF0090", "#FF00F5FF", "#FFFF0090", "#FFFFF000", "#FFFF0090", "#FFFFFFFF", "#FF9AA0FF", 0.8f, SpeedoFont.Condensed, true),
        new("Sakura", "#C0201018", SpeedoFrame.Glass, "#FFFFB7C5", "#44FFB7C5", "#FFFFFFFF", "#FFFF6F91", "#FFFFB7C5", "#FFFF3860", "#FFFFF0F4", "#FFE8A6B6", 0.4f, SpeedoFont.Sans),
        new("Military radar", "#E0040D06", SpeedoFrame.Plate, "#FF1F4D2B", "#6633FF66", "#FF33FF66", "#FFB6FF33", "#FF33FF66", "#FFFF5533", "#FF8CFF9E", "#FF2E9C4A", 0.5f, SpeedoFont.Mono),
        new("Night vision", "#E0020A02", SpeedoFrame.Glass, "#FF1B3B1B", "#4400FF41", "#FF00FF41", "#FFCCFF00", "#FF00FF41", "#FFFFFF66", "#FFB3FFB8", "#FF3D8F45", 0.8f, SpeedoFont.Mono),
        new("Matrix", "#E0000000", SpeedoFrame.Outline, "#FF00FF41", "#3300FF41", "#FF008F11", "#FF00FF41", "#FFFFFFFF", "#FF00FF41", "#FF00FF41", "#FF008F11", 0.6f, SpeedoFont.Digital),
        new("Blueprint", "#E00B3A6E", SpeedoFrame.Outline, "#FFE0F0FF", "#80E0F0FF", "#FFFFFFFF", "#FFFFE066", "#FFFFFFFF", "#FFFF8A80", "#FFFFFFFF", "#FFB9D7F2", 0f, SpeedoFont.Mono, false, 2f),
        new("Engineer's paper", "#F0F4F1E8", SpeedoFrame.Plate, "#FF8A8170", "#66867D6C", "#FF2B2B2B", "#FFC0392B", "#FF1F5F99", "#FFC0392B", "#FF1A1A1A", "#FF6B6558", 0f, SpeedoFont.Mono, false, 3f),
        new("F1 broadcast", "#E0101114", SpeedoFrame.Plate, "#00000000", "#40FFFFFF", "#FFFFFFFF", "#FFE10600", "#FFE10600", "#FFE10600", "#FFFFFFFF", "#FFA0A0A8", 0f, SpeedoFont.Condensed, false, 4f),
        new("Carbon", "#F0111111", SpeedoFrame.Plate, "#FF2B2B2B", "#33FFFFFF", "#FFE5E5E5", "#FFFF7A00", "#FFFF7A00", "#FFFF3B30", "#FFFFFFFF", "#FF8E8E8E", 0f, SpeedoFont.Condensed),
        new("Gold luxury", "#E0100D08", SpeedoFrame.Bezel, "#FFD4AF37", "#44D4AF37", "#FFF5E6B8", "#FFD4AF37", "#FFD4AF37", "#FFB22222", "#FFFFF6DA", "#FFB8A06A", 0.2f, SpeedoFont.Sans),
        new("British racing green", "#E0062A1A", SpeedoFrame.Bezel, "#FFC9B37E", "#33FFFFFF", "#FFF2E8D0", "#FFC9B37E", "#FFC9B37E", "#FFD9534F", "#FFF8F1E0", "#FFA7B89F", 0f, SpeedoFont.Sans),
        new("Gulf rally", "#E00E2F44", SpeedoFrame.Plate, "#FFFF8200", "#44A6D6F0", "#FFA6D6F0", "#FFFF8200", "#FFFF8200", "#FFFF4500", "#FFFFFFFF", "#FFA6D6F0", 0.1f, SpeedoFont.Condensed, true),
        new("Arcade", "#E0000014", SpeedoFrame.Angled, "#FFFFEA00", "#55FF00AA", "#FF00F0FF", "#FFFF00AA", "#FFFFEA00", "#FFFF0033", "#FFFFFFFF", "#FF8A8AFF", 0.6f, SpeedoFont.Digital),
        new("Hologram", "#2A00E5FF", SpeedoFrame.Outline, "#FF7DF9FF", "#557DF9FF", "#FF7DF9FF", "#FFE0FFFF", "#FF7DF9FF", "#FFFF7DF9", "#FFE0FFFF", "#FF7DF9FF", 0.9f, SpeedoFont.Condensed),
        new("Ice", "#C0071A2B", SpeedoFrame.Glass, "#FFBDEBFF", "#4DBDEBFF", "#FF7DD3FC", "#FFFFFFFF", "#FFBDEBFF", "#FFF472B6", "#FFF0FAFF", "#FF8FC9E8", 0.4f, SpeedoFont.Sans),
        new("Lava", "#D0140602", SpeedoFrame.Outline, "#FFFF4500", "#44FF6A00", "#FFFFB300", "#FFFF2D00", "#FFFF6A00", "#FFFFFFFF", "#FFFFE7C2", "#FFFF8A3D", 0.7f, SpeedoFont.Condensed),
        new("Toxic", "#D0080C02", SpeedoFrame.Angled, "#FFB6FF00", "#44B6FF00", "#FF39FF14", "#FFDFFF00", "#FFB6FF00", "#FFFF3131", "#FFEFFFD6", "#FF7FB800", 0.7f, SpeedoFont.Mono, true),
        new("Royal", "#D010082A", SpeedoFrame.Glass, "#FF9B7BFF", "#449B7BFF", "#FF9B7BFF", "#FFFFD166", "#FFFFD166", "#FFFF5C8A", "#FFF3EEFF", "#FFB3A3E6", 0.4f, SpeedoFont.Sans),
        new("Stealth", "#80000000", SpeedoFrame.None, "#00000000", "#26FFFFFF", "#FFBFBFBF", "#FFFFFFFF", "#FFFFFFFF", "#FFFF453A", "#FFE6E6E6", "#FF7A7A7A", 0f, SpeedoFont.Sans),
        new("Monochrome", "#C0000000", SpeedoFrame.Plate, "#FF444444", "#44FFFFFF", "#FFFFFFFF", "#FFFFFFFF", "#FFFFFFFF", "#FFFFFFFF", "#FFFFFFFF", "#FF999999", 0f, SpeedoFont.Sans),
        new("Pastel", "#D0F7F3FF", SpeedoFrame.Plate, "#FFCDB4DB", "#66A2D2FF", "#FFA2D2FF", "#FFFFAFCC", "#FFCDB4DB", "#FFFF8FAB", "#FF3D3550", "#FF7D7396", 0f, SpeedoFont.Sans),
    };

    public static Farbthema? Finde(string? name) => Alle.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Das Thema auf einen Tacho legen -- die Form bleibt, Farben, Rahmen, Leuchten, Schrift wechseln.</summary>
    public static void Auf(SpeedoDesign d, Farbthema t)
    {
        d.Background = t.Hintergrund;
        d.Frame = d.Frame == SpeedoFrame.None ? SpeedoFrame.None : t.Rahmen;
        d.FrameColour = t.RahmenFarbe;
        d.TrackColour = t.Gitter;
        d.LowColour = t.Tief;
        d.HighColour = t.Hoch;
        d.RedlineColour = t.Rot;
        d.NeedleColour = t.Akzent;
        d.AccentColour = t.Akzent;
        d.TextColour = t.Text;
        d.DimColour = t.Leise;
        d.Glow = t.Leuchten;
        d.Font = t.Schrift;
        d.Italic = t.Kursiv;
        d.CornerRadius = t.Ecken;
    }

    public static void Auf(GMeterDesign d, Farbthema t)
    {
        d.Background = t.Hintergrund;
        d.Frame = d.Frame == SpeedoFrame.None ? SpeedoFrame.None : t.Rahmen;
        d.FrameColour = t.RahmenFarbe;
        d.GridColour = t.Gitter;
        d.LowColour = t.Tief;
        d.HighColour = t.Hoch;
        d.DotColour = t.Text;
        d.TrailColour = t.Akzent;
        d.EnvelopeColour = "#66" + t.Akzent[3..];
        d.TextColour = t.Text;
        d.DimColour = t.Leise;
        d.Glow = t.Leuchten;
        d.Font = t.Schrift;
        d.Italic = t.Kursiv;
        d.CornerRadius = t.Ecken;
    }

    public static HudStil AlsHudStil(Farbthema t) => new()
    {
        Name = t.Name, Background = t.Hintergrund, Text = t.Text, Dim = t.Leise, Accent = t.Akzent, Line = t.Tief,
        Good = t.Tief, Bad = t.Rot, Frame = t.Rahmen, FrameColour = t.RahmenFarbe, Corner = t.Ecken, Glow = t.Leuchten,
        Font = t.Schrift, Italic = t.Kursiv, BuiltIn = true,
    };

    /// <summary>Der Name, wie er dasteht.</summary>
    public static string Anzeige(string name) => name switch
    {
        "Vaporwave 80s" => Loc.T("Vaporwave 80s"),
        "Outrun sunset" => Loc.T("Outrun sunset"),
        "Miami" => Loc.T("Miami"),
        "Tokyo neon" => Loc.T("Tokyo neon"),
        "Sakura" => Loc.T("Sakura"),
        "Military radar" => Loc.T("Military radar"),
        "Night vision" => Loc.T("Night vision"),
        "Matrix" => Loc.T("Matrix"),
        "Blueprint" => Loc.T("Blueprint"),
        "Engineer's paper" => Loc.T("Engineer's paper"),
        "F1 broadcast" => Loc.T("F1 broadcast"),
        "Carbon" => Loc.T("Carbon"),
        "Gold luxury" => Loc.T("Gold luxury"),
        "British racing green" => Loc.T("British racing green"),
        "Gulf rally" => Loc.T("Gulf rally"),
        "Arcade" => Loc.T("Arcade"),
        "Hologram" => Loc.T("Hologram"),
        "Ice" => Loc.T("Ice"),
        "Lava" => Loc.T("Lava"),
        "Toxic" => Loc.T("Toxic"),
        "Royal" => Loc.T("Royal"),
        "Stealth" => Loc.T("Stealth"),
        "Monochrome" => Loc.T("Monochrome"),
        "Pastel" => Loc.T("Pastel"),
        _ => name,
    };
}
