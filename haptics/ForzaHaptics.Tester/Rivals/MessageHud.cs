using System.Drawing;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Eine kurze Meldung oben mittig ueber dem Spiel -- ein paar Sekunden, dann weg.
/// </summary>
/// <remarks>
/// Fuer Dinge, die nichts mit dem laufenden Rennen zu tun haben und darum nicht auf
/// den Delta-Streifen gehoeren, der nur im Rennen steht: zuerst die Warnung, dass der
/// Tune-Speicher des Spiels voll wird (2026-09-26). Verschwindet von selbst -- siehe
/// "Overlay must not linger".
/// </remarks>
internal sealed class MessageHud : LayeredHud
{
    private readonly System.Windows.Forms.Timer _weg = new();
    private string _titel = string.Empty;
    private string _text = string.Empty;
    private Color _farbe = Color.FromArgb(255, 210, 90);

    public override int Ebene => 4;

    public MessageHud(Rectangle screen) : base(screen)
    {
        _weg.Tick += (_, _) =>
        {
            _weg.Stop();
            _text = string.Empty;
            if (Visible) { Hide(); }
        };
    }

    /// <summary>Die Meldung zeigen, fuer so viele Sekunden.</summary>
    public void Zeige(string titel, string text, Color farbe, double sekunden = 10)
    {
        _titel = titel;
        _text = text;
        _farbe = farbe;
        Render();
        if (!Visible) { Show(); }
        TopMost = true;
        _weg.Stop();
        _weg.Interval = Math.Max(1000, (int)(sekunden * 1000));
        _weg.Start();
    }

    protected override Rectangle Extent(Graphics messung) =>
        string.IsNullOrEmpty(_text) ? Rectangle.Empty : Rectangle.Ceiling(Lege(messung, AreaSize, _titel, _text).Kasten);

    protected override void Draw(Graphics g) => Male(g, AreaSize, _titel, _text, _farbe);

    internal readonly record struct Aufbau(RectangleF Kasten, Font Titel, Font Text, float Rand);

    internal static Aufbau Lege(Graphics g, Size flaeche, string titel, string text)
    {
        var skala = GameArea.ResolutionScale(new Rectangle(Point.Empty, flaeche));
        // In PIXELN, nicht in Punkten -- siehe CourseShapeHud.Schrift.
        var titelFont = new Font("Segoe UI Semibold", 15f * 96f / 72f * skala, GraphicsUnit.Pixel);
        var textFont = new Font("Segoe UI", 11f * 96f / 72f * skala, GraphicsUnit.Pixel);
        var rand = 14f * skala;
        var breite = Math.Min(flaeche.Width * 0.6f, 720f * skala);
        var t1 = g.MeasureString(titel, titelFont, (int)(breite - (2 * rand)));
        var t2 = g.MeasureString(text, textFont, (int)(breite - (2 * rand)));
        var hoehe = t1.Height + t2.Height + (2.4f * rand);
        var x = (flaeche.Width - breite) / 2f;
        var y = flaeche.Height * 0.07f;
        return new Aufbau(new RectangleF(x, y, breite, hoehe), titelFont, textFont, rand);
    }

    internal static void Male(Graphics g, Size flaeche, string titel, string text, Color farbe)
    {
        if (string.IsNullOrEmpty(text)) { return; }
        var a = Lege(g, flaeche, titel, text);
        try
        {
            using var grund = new SolidBrush(Color.FromArgb(225, 13, 17, 23));
            g.FillRectangle(grund, a.Kasten);
            using var leiste = new SolidBrush(farbe);
            g.FillRectangle(leiste, a.Kasten.X, a.Kasten.Y, Math.Max(4f, a.Rand * 0.35f), a.Kasten.Height);
            var innen = new RectangleF(a.Kasten.X + a.Rand, a.Kasten.Y + a.Rand,
                                       a.Kasten.Width - (2 * a.Rand), a.Kasten.Height - (2 * a.Rand));
            using var titelPinsel = new SolidBrush(farbe);
            var h1 = g.MeasureString(titel, a.Titel, (int)innen.Width).Height;
            g.DrawString(titel, a.Titel, titelPinsel, new RectangleF(innen.X, innen.Y, innen.Width, h1));
            using var textPinsel = new SolidBrush(Color.FromArgb(231, 236, 243));
            g.DrawString(text, a.Text, textPinsel,
                         new RectangleF(innen.X, innen.Y + h1 + (a.Rand * 0.4f), innen.Width, innen.Height - h1));
        }
        finally
        {
            a.Titel.Dispose();
            a.Text.Dispose();
        }
    }
}
