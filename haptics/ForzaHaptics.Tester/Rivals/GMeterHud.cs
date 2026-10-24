using System.Drawing;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Das G-Meter ueber dem Spiel (seit 2026-10-24, OPT-IN) -- gezeichnet von <see cref="GMeterRenderer"/>
/// auf dem Faden des Tachos (<see cref="SpeedoMotor"/>), im Takt des Bildschirms.
/// </summary>
internal sealed class GMeterHud : LayeredHud
{
    private readonly OverlaySettings _settings;
    private readonly GMeterRenderer _maler = new();
    private readonly object _sperre = new();
    private GMeterDesign _design;
    private GMeterBild? _bild;

    public GMeterHud(OverlaySettings settings, Rectangle flaeche) : base(flaeche)
    {
        _settings = settings;
        _design = GMeterDesigns.Finde(settings.GMeterDesign);
    }

    public GMeterDesign Design => _design;

    public void DesignNeu(GMeterDesign? design = null)
    {
        lock (_sperre)
        {
            _design = design ?? GMeterDesigns.Finde(_settings.GMeterDesign);
            _maler.Vergessen();
        }
    }

    public void Setze(GMeterBild bild)
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
            if (_bild is { } b) { _maler.Male(g, _design, b, Lege(_settings, _design, AreaSize), GMeterEinheit.Aus(_settings)); }
        }
    }

    internal static float Skala(OverlaySettings s, Size flaeche) =>
        GameArea.ResolutionScale(new Rectangle(Point.Empty, flaeche)) * (float)Math.Clamp(s.HudGMeterScale, 0.3, 3.0);

    internal static Rectangle Lege(OverlaySettings s, GMeterDesign d, Size flaeche)
    {
        var platz = s.GMeterPlacement;
        var groesse = GMeterRenderer.Groesse(d, Skala(s, flaeche));
        var w = (int)Math.Ceiling(groesse.Width);
        var h = (int)Math.Ceiling(groesse.Height);
        var x0 = (int)(platz.X * flaeche.Width);
        var y0 = (int)(platz.Y * flaeche.Height);
        x0 = platz.Align.ToLowerInvariant() switch { "left" => x0, "right" => x0 - w, _ => x0 - (w / 2) };
        x0 = Math.Max(0, Math.Min(x0, flaeche.Width - w));
        y0 = Math.Max(0, Math.Min(y0, flaeche.Height - h));
        return new Rectangle(x0, y0, w, h);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _maler.Dispose(); }
        base.Dispose(disposing);
    }
}
