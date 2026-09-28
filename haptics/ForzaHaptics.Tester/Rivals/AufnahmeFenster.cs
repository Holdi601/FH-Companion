using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Die Overlays in einem gewoehnlichen Fenster, auf einer Schluesselfarbe -- fuer OBS
/// und andere Aufnahmeprogramme (seit 2026-09-28). Siehe OverlayAusgabe.
/// </summary>
/// <remarks>
/// In OBS: eine Fensteraufnahme dieses Fensters ueber die Spielaufnahme legen und
/// einen Farbschluessel-Filter in derselben Farbe dazu. Das Fenster darf auf einem
/// anderen Schirm oder hinter dem Spiel liegen, nur nicht minimiert sein -- ein
/// minimiertes Fenster zeichnet Windows nicht.
///
/// Gezeichnet wird 30-mal je Sekunde, aber nur, solange das Fenster offen ist. Die
/// Seitenverhaeltnisse folgen dem Fenster: ist es wie das Spiel 16:9, liegt alles
/// genau dort, wo es im Spiel liegt.
/// </remarks>
internal sealed class AufnahmeFenster : Form
{
    private readonly OverlaySettings _settings;
    private readonly System.Windows.Forms.Timer _takt = new() { Interval = 33 };

    public AufnahmeFenster(OverlaySettings settings)
    {
        _settings = settings;
        Text = AppInfo.Name + " – " + Loc.T("overlay for recording");
        Icon = Marke.Symbol() ?? Icon;
        StartPosition = FormStartPosition.WindowsDefaultLocation;
        var f = OverlayAusgabe.Flaeche;
        var breite = 1280;
        var hoehe = f.Width > 0 ? breite * f.Height / f.Width : 720;
        ClientSize = new Size(breite, Math.Max(200, hoehe));
        MinimumSize = new Size(320, 200);
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _takt.Tick += (_, _) => Invalidate();
        _takt.Start();
    }

    /// <summary>Die Farbe, die OBS ausstanzt.</summary>
    internal static Color Schluessel(string? name) => (name ?? "green").Trim().ToLowerInvariant() switch
    {
        "magenta" => Color.FromArgb(255, 0, 255),
        "black" => Color.Black,
        _ => Color.FromArgb(0, 255, 0),
    };

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Schluessel(_settings.RecordingKey));
        var f = OverlayAusgabe.Flaeche;
        if (f.Width <= 0 || f.Height <= 0) { return; }
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.ScaleTransform(ClientSize.Width / (float)f.Width, ClientSize.Height / (float)f.Height);
        foreach (var quelle in OverlayAusgabe.Alle())
        {
            if (!quelle.Gewollt) { continue; }
            var zustand = g.Save();
            try { quelle.MaleFuerAufnahme(g); } catch (Exception) { }
            g.Restore(zustand);
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _takt.Stop();
        _takt.Dispose();
        base.OnFormClosed(e);
    }
}
