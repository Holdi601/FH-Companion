using System.Drawing;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Der eigene Tacho ueber dem Spiel (seit 2026-10-18, OPT-IN): Drehzahl, Gang, Tempo, roter
/// Bereich, Schaltlichter, TC/ABS, Leistung, Strecke, Uhr -- in einem der Designs aus
/// <see cref="SpeedoDesigns"/>, gezeichnet von <see cref="SpeedoRenderer"/>.
/// </summary>
/// <remarks>
/// Ab Werk aus, wie die Reifenuebersicht: ein neues Fenster ueber dem Spiel soll niemand
/// ungefragt bekommen. Gezeichnet wird im eigenen Takt (<see cref="OverlaySettings.SpeedoFps"/>,
/// ab Werk 30 je Sekunde) und nur, wenn sich etwas sichtbar geaendert hat.
/// </remarks>
internal sealed class SpeedoHud : LayeredHud
{
    private readonly OverlaySettings _settings;
    private readonly SpeedoRenderer _maler = new();
    private SpeedoDesign _design;
    private SpeedoBild? _bild;
    /// <summary>
    /// Gezeichnet wird auf dem Faden des Tachos (SpeedoMotor) UND, fuer Aufnahme- und
    /// Dashboardfenster, auf dem der Oberflaeche -- der Merker des Zeichners vertraegt nur einen.
    /// </summary>
    private readonly object _sperre = new();

    public SpeedoHud(OverlaySettings settings, Rectangle flaeche) : base(flaeche)
    {
        _settings = settings;
        _design = SpeedoDesigns.Finde(settings.SpeedoDesign);
    }

    public SpeedoDesign Design => _design;

    /// <summary>Das gewaehlte Design neu laden (nach einer Aenderung im Reiter).</summary>
    public void DesignNeu(SpeedoDesign? design = null)
    {
        lock (_sperre) { _design = design ?? SpeedoDesigns.Finde(_settings.SpeedoDesign); }
    }

    public void Setze(SpeedoBild bild)
    {
        lock (_sperre) { _bild = bild; }
        Render();
    }

    protected override Rectangle Extent(Graphics messung) =>
        _bild is null ? Rectangle.Empty : Rectangle.Inflate(Lege(_settings, _design, AreaSize), 8, 8);

    protected override void Draw(Graphics g)
    {
        lock (_sperre)
        {
            if (_bild is { } b)
            {
                _maler.Male(g, _design, b, Lege(_settings, _design, AreaSize), SpeedoEinheiten.Aus(_settings));
            }
        }
    }

    internal static float Skala(OverlaySettings s, Size flaeche) =>
        GameArea.ResolutionScale(new Rectangle(Point.Empty, flaeche)) * (float)Math.Clamp(s.HudSpeedoScale, 0.3, 3.0);

    /// <summary>Wo der Tacho steht -- dieselbe Rechnung wie im Einrichtungsreiter.</summary>
    internal static Rectangle Lege(OverlaySettings s, SpeedoDesign d, Size flaeche)
    {
        var platz = s.SpeedoPlacement;
        var groesse = SpeedoRenderer.Groesse(d, Skala(s, flaeche));
        var w = (int)Math.Ceiling(groesse.Width);
        var h = (int)Math.Ceiling(groesse.Height);
        var x0 = (int)(platz.X * flaeche.Width);
        var y0 = (int)(platz.Y * flaeche.Height);
        x0 = platz.Align.ToLowerInvariant() switch
        {
            "left" => x0,
            "right" => x0 - w,
            _ => x0 - (w / 2),
        };
        x0 = Math.Max(0, Math.Min(x0, flaeche.Width - w));
        y0 = Math.Max(0, Math.Min(y0, flaeche.Height - h));
        return new Rectangle(x0, y0, w, h);
    }

    /// <summary>Einmal zeichnen, ohne Fenster -- Vorschau im Einrichtungsreiter und Bilder.</summary>
    internal static void Male(Graphics g, OverlaySettings s, SpeedoDesign d, Size flaeche, SpeedoBild b) =>
        SpeedoRenderer.Einmal(g, d, b, Lege(s, d, flaeche), SpeedoEinheiten.Aus(s));

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _maler.Dispose(); }
        base.Dispose(disposing);
    }
}
