using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>In welcher Einheit das G-Meter Zahlen zeigt (seit 2026-10-24): G, m/s² oder ft/s².</summary>
internal enum GUnit { G, MetresPerSecond2, FeetPerSecond2 }

internal readonly record struct GMeterEinheit(GUnit Unit)
{
    public static GMeterEinheit Aus(OverlaySettings s) => new(s.GMeterUnit);

    public float Faktor => Unit switch { GUnit.MetresPerSecond2 => 9.80665f, GUnit.FeetPerSecond2 => 32.17405f, _ => 1f };

    public string Kurz => Unit switch { GUnit.MetresPerSecond2 => "m/s²", GUnit.FeetPerSecond2 => "ft/s²", _ => "G" };

    /// <summary>Ein Messwert: G mit zwei Stellen, die anderen mit einer.</summary>
    public string Wert(float g) => Unit == GUnit.G ? g.ToString("0.00", CultureInfo.InvariantCulture) : (g * Faktor).ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>Eine Skalenzahl.</summary>
    public string Skala(float g) => Unit == GUnit.G ? g.ToString("0.#", CultureInfo.InvariantCulture) : (g * Faktor).ToString("0", CultureInfo.InvariantCulture);
}

/// <summary>
/// ZEICHNET EIN G-METER (seit 2026-10-24) -- im Spiel (<see cref="GMeterHud"/>), in der Vorschau und in
/// der Galerie: eine Zeichnung. Wie der Tacho in zwei Schichten: Rahmen, Ringe und Beschriftung einmal
/// in ein Bild, je Bild nur Punkt, Spur, Huelle und Zahlen. Rahmen und Schrift teilt es mit dem Tacho
/// (<see cref="SpeedoRenderer"/>), damit beide zusammen aussehen.
/// </summary>
internal sealed class GMeterRenderer : IDisposable
{
    private Bitmap? _grund;
    private string _schluessel = string.Empty;
    private GMeterDesign? _designZuletzt;
    private int _hash;
    private long _geprueft;
    private SpeedoDesign? _stil;
    private Bitmap? _waermeBild;

    private enum Schicht { Grund, Licht }

    /// <summary>Die Einheit des Bildes, das gerade gezeichnet wird (je Faden).</summary>
    [ThreadStatic] private static GMeterEinheit _e;

    public void Dispose()
    {
        _grund?.Dispose();
        _grund = null;
        _waermeBild?.Dispose();
        _waermeBild = null;
    }

    public void Vergessen()
    {
        _designZuletzt = null;
        _schluessel = string.Empty;
    }

    public static SizeF Groesse(GMeterDesign d, float k) => new(d.Size * k, d.Size * d.Hochkant * k);

    public static void Einmal(Graphics g, GMeterDesign d, GMeterBild b, RectangleF ziel, GMeterEinheit e = default)
    {
        using var r = new GMeterRenderer();
        r.Male(g, d, b, ziel, e);
    }

    private int Hash(GMeterDesign d)
    {
        var jetzt = Environment.TickCount64;
        if (!ReferenceEquals(d, _designZuletzt) || jetzt - _geprueft > 500)
        {
            _hash = d.ToJson().GetHashCode();
            _designZuletzt = d;
            _geprueft = jetzt;
            _stil = Stil(d);
        }
        return _hash;
    }

    /// <summary>Die Werte, die Rahmen und Schrift des Tachos brauchen.</summary>
    private static SpeedoDesign Stil(GMeterDesign d) => new()
    {
        Frame = d.Frame, Background = d.Background, FrameColour = d.FrameColour, CornerRadius = d.CornerRadius,
        Glow = d.Glow, Font = d.Font, Italic = d.Italic, TextColour = d.TextColour, DimColour = d.DimColour, ShowPlate = d.ShowPlate,
    };

    public void Male(Graphics g, GMeterDesign d, GMeterBild b, RectangleF ziel, GMeterEinheit e = default)
    {
        if (ziel.Width < 4f || ziel.Height < 4f) { return; }
        _e = e;
        var w = (float)d.Size;
        var h = d.Size * d.Hochkant;
        var k = Math.Min(ziel.Width / w, ziel.Height / h);
        var platz = new RectangleF(ziel.X + (ziel.Width - w * k) / 2f, ziel.Y + (ziel.Height - h * k) / 2f, w * k, h * k);
        var schluessel = Hash(d) + "|" + (int)platz.Width + "|" + (int)platz.Height + "|" + e.Unit;
        if (_grund is null || schluessel != _schluessel)
        {
            _grund?.Dispose();
            _grund = new Bitmap(Math.Max(1, (int)Math.Ceiling(platz.Width)), Math.Max(1, (int)Math.Ceiling(platz.Height)), PixelFormat.Format32bppPArgb);
            using (var gg = Graphics.FromImage(_grund))
            {
                gg.Clear(Color.Transparent);
                Vorbereiten(gg, true);
                gg.ScaleTransform(k, k);
                Lage(gg, d, _stil!, b, w, h, Schicht.Grund);
            }
            _schluessel = schluessel;
        }
        var im = g.InterpolationMode;
        var cq = g.CompositingQuality;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.CompositingQuality = CompositingQuality.HighSpeed;
        g.DrawImage(_grund, platz.X, platz.Y, _grund.Width, _grund.Height);
        g.InterpolationMode = im;
        g.CompositingQuality = cq;
        var zustand = g.Save();
        try
        {
            Vorbereiten(g, false);
            g.TranslateTransform(platz.X, platz.Y);
            g.ScaleTransform(k, k);
            Lage(g, d, _stil!, b, w, h, Schicht.Licht);
        }
        finally
        {
            g.Restore(zustand);
        }
    }

    private static void Vorbereiten(Graphics g, bool merker)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.PixelOffsetMode = merker ? PixelOffsetMode.HighQuality : PixelOffsetMode.Half;
        g.CompositingQuality = merker ? CompositingQuality.HighQuality : CompositingQuality.HighSpeed;
    }

    private void Lage(Graphics g, GMeterDesign d, SpeedoDesign stil, GMeterBild b, float w, float h, Schicht s)
    {
        switch (d.Layout)
        {
            case GMeterLayout.Bar: Balken(g, d, stil, b, w, h, s, waagrecht: true); break;
            case GMeterLayout.Column: Balken(g, d, stil, b, w, h, s, waagrecht: false); break;
            case GMeterLayout.Arc: Bogen(g, d, stil, b, w, h, s); break;
            case GMeterLayout.Needle: Zeiger(g, d, stil, b, w, h, s); break;
            case GMeterLayout.Perspective: Perspektive(g, d, stil, b, w, h, s); break;
            case GMeterLayout.Sphere: Kugel(g, d, stil, b, w, h, s); break;
            case GMeterLayout.Cartesian: Kartesisch(g, d, stil, b, w, h, s); break;
            case GMeterLayout.Cartesian3D: Kartesisch3D(g, d, stil, b, w, h, s); break;
            case GMeterLayout.CircleWithHeight:
            {
                if (s == Schicht.Grund) { SpeedoRenderer.Rahmen(g, stil, new RectangleF(1f, 1f, w - 2f, h - 2f), kreis: false); }
                var feld = new RectangleF(h * 0.06f, h * 0.06f, h * 0.88f, h * 0.88f);
                Flaeche(g, d, stil, b, feld, s, GMeterLayout.Circle, rahmen: false);
                Hoehenbalken(g, d, stil, b, new RectangleF(feld.Right + w * 0.06f, h * 0.1f, w - feld.Right - w * 0.12f, h * 0.8f), s);
                break;
            }
            default:
                Flaeche(g, d, stil, b, new RectangleF(0f, 0f, w, h), s, d.Layout, rahmen: true);
                break;
        }
    }

    // ================================================================== //
    // Werkzeuge
    // ================================================================== //

    private static Color F(string? t, Color v) => SpeedoDesign.Farbe(t, v);

    private static Color Alpha(Color c, float f) => Color.FromArgb(Math.Clamp((int)(c.A * f), 0, 255), c.R, c.G, c.B);

    private static Color Mische(Color a, Color c, float u)
    {
        u = Math.Clamp(u, 0f, 1f);
        return Color.FromArgb((int)(a.A + (c.A - a.A) * u), (int)(a.R + (c.R - a.R) * u), (int)(a.G + (c.G - a.G) * u), (int)(a.B + (c.B - a.B) * u));
    }

    /// <summary>Vom Auto zur Anzeige: (rechts, oben) in G. "Gespuert": in der Kurve nach aussen, beim Bremsen nach vorn (oben).</summary>
    private static (float Rechts, float Oben) Anzeige(GMeterDesign d, float x, float z) => d.FeltForce ? (-x, -z) : (x, z);

    private static Color Punktfarbe(GMeterDesign d, float betrag) =>
        d.ColourByG ? Mische(F(d.LowColour, Color.LimeGreen), F(d.HighColour, Color.Red), betrag / Math.Max(0.1f, d.MaxG)) : F(d.DotColour, Color.White);

    private static void Punkt(Graphics g, GMeterDesign d, PointF p, float r, Color c)
    {
        if (d.Glow > 0.05f)
        {
            for (var i = 3; i >= 1; i--)
            {
                using var schein = new SolidBrush(Alpha(c, 0.14f * d.Glow * (4 - i)));
                var rr = r * (1f + i * 0.55f);
                g.FillEllipse(schein, p.X - rr, p.Y - rr, rr * 2f, rr * 2f);
            }
        }
        using var pinsel = new SolidBrush(c);
        g.FillEllipse(pinsel, p.X - r, p.Y - r, r * 2f, r * 2f);
        using var rand = new Pen(Alpha(Color.Black, 0.5f), Math.Max(1f, r * 0.18f));
        g.DrawEllipse(rand, p.X - r, p.Y - r, r * 2f, r * 2f);
    }

    private static string G(float wert) => _e.Wert(wert);

    /// <summary>Ein Messwert mit Einheit: "1.32 G", "12.9 m/s²", "42.5 ft/s²".</summary>
    private static string GE(float wert) => _e.Wert(wert) + " " + _e.Kurz;

    // ================================================================== //
    // 2D: Kreis, Fadenkreuz, Radar, Raute, Waermekarte -- und die Scheibe der 3D-Formen
    // ================================================================== //

    /// <summary>
    /// Die Flaeche eines zweidimensionalen G-Meters in <paramref name="feld"/>. Die Abbildung von G auf Punkte
    /// gibt <paramref name="projektion"/> (fuer die perspektivische Scheibe gestaucht), sonst ein Kreis.
    /// </summary>
    private void Flaeche(Graphics g, GMeterDesign d, SpeedoDesign stil, GMeterBild b, RectangleF feld, Schicht s,
                         GMeterLayout form, bool rahmen)
    {
        var rund = form is GMeterLayout.Circle or GMeterLayout.Radar;
        var c = new PointF(feld.X + feld.Width / 2f, feld.Y + feld.Height / 2f);
        var rand = feld.Width * 0.07f;
        var r = feld.Width / 2f - rand;
        PointF P(float rechts, float oben)
        {
            var betrag = MathF.Sqrt(rechts * rechts + oben * oben);
            var grenze = form == GMeterLayout.Diamond ? MathF.Abs(rechts) + MathF.Abs(oben) : form is GMeterLayout.Crosshair or GMeterLayout.Heat ? MathF.Max(MathF.Abs(rechts), MathF.Abs(oben)) : betrag;
            if (grenze > d.MaxG && grenze > 0f) { rechts *= d.MaxG / grenze; oben *= d.MaxG / grenze; }
            return new PointF(c.X + rechts / d.MaxG * r, c.Y - oben / d.MaxG * r);
        }
        var gitter = F(d.GridColour, Color.FromArgb(60, 255, 255, 255));
        var leise = F(d.DimColour, Color.Gray);
        var hell = F(d.TextColour, Color.White);

        if (s == Schicht.Grund)
        {
            if (rahmen) { SpeedoRenderer.Rahmen(g, stil, RectangleF.Inflate(feld, -2f, -2f), kreis: rund); }
            using var stift = new Pen(gitter, Math.Max(1f, feld.Width * 0.004f));
            using var fein = new Pen(Alpha(gitter, 0.6f), Math.Max(1f, feld.Width * 0.003f)) { DashStyle = DashStyle.Dot };
            for (var i = 1; i <= d.Rings; i++)
            {
                var rr = r * i / d.Rings;
                var ring = i == d.Rings ? stift : fein;
                switch (form)
                {
                    case GMeterLayout.Diamond:
                        g.DrawPolygon(ring, new[] { new PointF(c.X, c.Y - rr), new PointF(c.X + rr, c.Y), new PointF(c.X, c.Y + rr), new PointF(c.X - rr, c.Y) });
                        break;
                    case GMeterLayout.Crosshair:
                    case GMeterLayout.Heat:
                        g.DrawRectangle(ring, c.X - rr, c.Y - rr, rr * 2f, rr * 2f);
                        break;
                    default:
                        g.DrawEllipse(ring, c.X - rr, c.Y - rr, rr * 2f, rr * 2f);
                        break;
                }
            }
            g.DrawLine(stift, c.X - r, c.Y, c.X + r, c.Y);
            g.DrawLine(stift, c.X, c.Y - r, c.X, c.Y + r);
            if (form == GMeterLayout.Radar)
            {
                for (var a = 30; a < 360; a += 30)
                {
                    if (a % 90 == 0) { continue; }
                    var rad = a * MathF.PI / 180f;
                    g.DrawLine(fein, c.X, c.Y, c.X + MathF.Cos(rad) * r, c.Y + MathF.Sin(rad) * r);
                }
            }
            if (form == GMeterLayout.Crosshair)
            {
                // Striche alle 0,25 G auf den Achsen.
                for (var v = 0.25f; v < d.MaxG + 0.01f; v += 0.25f)
                {
                    var o = v / d.MaxG * r;
                    var lang = Math.Abs(v % 1f) < 0.01f ? r * 0.06f : r * 0.03f;
                    g.DrawLine(stift, c.X + o, c.Y - lang, c.X + o, c.Y + lang);
                    g.DrawLine(stift, c.X - o, c.Y - lang, c.X - o, c.Y + lang);
                    g.DrawLine(stift, c.X - lang, c.Y + o, c.X + lang, c.Y + o);
                    g.DrawLine(stift, c.X - lang, c.Y - o, c.X + lang, c.Y - o);
                }
            }
            var px = Math.Max(7f, feld.Width * 0.045f);
            if (d.ScaleNumbers)
            {
                for (var i = 1; i <= d.Rings; i++)
                {
                    var wert = d.MaxG * i / d.Rings;
                    var x = c.X + r * i / d.Rings;
                    SpeedoRenderer.Schreibe(g, stil, _e.Skala(wert), new RectangleF(x - px * 1.6f, c.Y + px * 0.2f, px * 1.5f, px), px * 0.85f, leise, SpeedoRenderer.Rechts);
                }
            }
            if (d.DirectionLabels)
            {
                var oben = d.FeltForce ? Loc.T("BRAKE") : Loc.T("ACCEL");
                var unten = d.FeltForce ? Loc.T("ACCEL") : Loc.T("BRAKE");
                // INNERHALB des aeusseren Rings: davor lag die Schrift auf dem Rahmen und wurde abgeschnitten.
                SpeedoRenderer.Schreibe(g, stil, oben, new RectangleF(c.X - r * 0.4f, c.Y - r + rand * 0.12f, r * 0.8f, rand * 0.75f), rand * 0.6f, leise);
                SpeedoRenderer.Schreibe(g, stil, unten, new RectangleF(c.X - r * 0.4f, c.Y + r - rand * 0.87f, r * 0.8f, rand * 0.75f), rand * 0.6f, leise);
            }
            return;
        }

        // ---- LICHT ----
        if (form == GMeterLayout.Heat) { Waerme(g, d, b, c, r); }
        if (form == GMeterLayout.Radar)
        {
            // Der Strahl dreht sich, eine Umdrehung in vier Sekunden.
            var winkel = (float)(b.Uhr * 90.0 % 360.0);
            using var strahl = new GraphicsPath();
            strahl.AddPie(c.X - r, c.Y - r, r * 2f, r * 2f, winkel - 30f, 30f);
            using var verlauf = new PathGradientBrush(strahl) { CenterColor = Alpha(F(d.TrailColour, Color.Cyan), 0.05f), SurroundColors = new[] { Alpha(F(d.TrailColour, Color.Cyan), 0.25f) } };
            g.FillPath(verlauf, strahl);
        }
        if (d.Envelope)
        {
            var punkte = new List<PointF>();
            for (var i = 0; i < GMeterBild.Sektoren; i++)
            {
                var wert = b.Huelle[i];
                if (wert <= 0.02f) { continue; }
                var a = ((i + 0.5f) / GMeterBild.Sektoren) * 2f * MathF.PI - MathF.PI;
                var (rechts, oben) = Anzeige(d, MathF.Cos(a) * wert, MathF.Sin(a) * wert);
                punkte.Add(P(rechts, oben));
            }
            if (punkte.Count >= 3)
            {
                using var flaeche = new SolidBrush(Alpha(F(d.EnvelopeColour, Color.Orange), 0.35f));
                using var linie = new Pen(F(d.EnvelopeColour, Color.Orange), Math.Max(1f, feld.Width * 0.006f));
                g.FillPolygon(flaeche, punkte.ToArray());
                g.DrawPolygon(linie, punkte.ToArray());
            }
        }
        Spur(g, d, b, (x, z) => { var (rr, oo) = Anzeige(d, x, z); return P(rr, oo); }, feld.Width);
        if (d.Peaks)
        {
            using var spitze = new Pen(F(d.EnvelopeColour, Color.Orange), Math.Max(1.5f, feld.Width * 0.008f));
            var lang = r * 0.05f;
            foreach (var (rechts, oben) in new[]
                     {
                         Anzeige(d, b.Rechts, 0f), Anzeige(d, -b.Links, 0f), Anzeige(d, 0f, b.Vor), Anzeige(d, 0f, -b.Brems),
                     })
            {
                if (MathF.Abs(rechts) + MathF.Abs(oben) < 0.05f) { continue; }
                var p = P(rechts, oben);
                if (MathF.Abs(rechts) > 0f) { g.DrawLine(spitze, p.X, p.Y - lang, p.X, p.Y + lang); }
                else { g.DrawLine(spitze, p.X - lang, p.Y, p.X + lang, p.Y); }
            }
        }
        var (jr, jo) = Anzeige(d, b.X, b.Z);
        Punkt(g, d, P(jr, jo), feld.Width * 0.035f * d.DotSize, Punktfarbe(d, b.Gesamt));
        if (d.Numbers)
        {
            var px = Math.Max(8f, feld.Width * 0.075f);
            var unten = new RectangleF(c.X - r * 0.9f, c.Y + r * 0.42f, r * 1.8f, px * 1.2f);
            SpeedoRenderer.Schreibe(g, stil, GE(b.Gesamt), unten, px, hell, gross: true);
            var klein = Math.Max(7f, feld.Width * 0.042f);
            SpeedoRenderer.Schreibe(g, stil, string.Format(CultureInfo.InvariantCulture, "{0} {1}   {2} {3}", Loc.T("lat"), G(MathF.Abs(b.X)), Loc.T("long"), G(MathF.Abs(b.Z))),
                                    new RectangleF(c.X - r * 0.9f, unten.Bottom, r * 1.8f, klein * 1.3f), klein, leise);
        }
    }

    /// <summary>Die Spur: Punkte, Linie oder leuchtend -- je juenger, desto deckender.</summary>
    private static void Spur(Graphics g, GMeterDesign d, GMeterBild b, Func<float, float, PointF> abbilden, float breite)
    {
        if (d.Trail == GMeterTrail.Off || b.Spur.Length < 2 || d.TrailSeconds <= 0f) { return; }
        var farbe = F(d.TrailColour, Color.DeepSkyBlue);
        var dicke = Math.Max(1f, breite * 0.008f);
        var punkte = b.Spur.Where(p => p.Alter <= d.TrailSeconds).OrderByDescending(p => p.Alter).ToArray();
        if (punkte.Length < 2) { return; }
        if (d.Trail == GMeterTrail.Dots)
        {
            for (var i = 0; i < punkte.Length; i += 3)
            {
                var p = abbilden(punkte[i].X, punkte[i].Z);
                using var pinsel = new SolidBrush(Alpha(farbe, 1f - punkte[i].Alter / d.TrailSeconds));
                g.FillEllipse(pinsel, p.X - dicke, p.Y - dicke, dicke * 2f, dicke * 2f);
            }
            return;
        }
        // In bis zu 16 Stuecken, jedes in seiner Deckkraft -- je Strich ein Aufruf waere zu teuer.
        var stuecke = Math.Min(16, punkte.Length - 1);
        for (var s = 0; s < stuecke; s++)
        {
            var von = s * (punkte.Length - 1) / stuecke;
            var bis = (s + 1) * (punkte.Length - 1) / stuecke;
            if (bis <= von) { continue; }
            var linie = new PointF[bis - von + 1];
            for (var i = von; i <= bis; i++) { linie[i - von] = abbilden(punkte[i].X, punkte[i].Z); }
            var deck = 1f - punkte[von].Alter / d.TrailSeconds;
            if (d.Trail == GMeterTrail.Glow)
            {
                using var schein = new Pen(Alpha(farbe, 0.25f * deck), dicke * 3.5f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLines(schein, linie);
            }
            using var stift = new Pen(Alpha(farbe, deck), dicke) { LineJoin = LineJoin.Round };
            g.DrawLines(stift, linie);
        }
    }

    /// <summary>Die Waermekarte: wo das Auto wie lange war, weich gezeichnet.</summary>
    private void Waerme(Graphics g, GMeterDesign d, GMeterBild b, PointF c, float r)
    {
        var n = GMeterBild.Raster;
        var hoechstes = b.Waerme.Max();
        if (hoechstes <= 0f) { return; }
        _waermeBild ??= new Bitmap(n, n, PixelFormat.Format32bppArgb);
        var tief = F(d.LowColour, Color.Blue);
        var heiss = F(d.HighColour, Color.Yellow);
        for (var zz = 0; zz < n; zz++)
        {
            for (var zx = 0; zx < n; zx++)
            {
                var u = b.Waerme[zz * n + zx] / hoechstes;
                // Wurzel: auch selten besuchte Stellen sollen sichtbar sein.
                u = MathF.Sqrt(u);
                // Die Zelle in Anzeigerichtung: gespuert = gespiegelt.
                var ax = d.FeltForce ? n - 1 - zx : zx;
                var ay = d.FeltForce ? zz : n - 1 - zz;
                _waermeBild.SetPixel(ax, ay, u < 0.02f ? Color.Transparent : Alpha(Mische(tief, heiss, u), 0.25f + 0.75f * u));
            }
        }
        // Das Raster deckt +-3 G; gezeigt wird bis MaxG.
        var halb = r * GMeterBild.RasterG / d.MaxG;
        var alt = g.Clip;
        g.SetClip(new RectangleF(c.X - r, c.Y - r, r * 2f, r * 2f), CombineMode.Intersect);
        var im = g.InterpolationMode;
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.DrawImage(_waermeBild, new RectangleF(c.X - halb, c.Y - halb, halb * 2f, halb * 2f));
        g.InterpolationMode = im;
        g.Clip = alt;
    }

    // ================================================================== //
    // 1D
    // ================================================================== //

    /// <summary>Der Wert der gewaehlten Achse -- mit Vorzeichen, ausser bei "Combined".</summary>
    private static float Wert(GMeterDesign d, float x, float z, float y)
    {
        var (rechts, oben) = Anzeige(d, x, z);
        return d.Axis switch
        {
            GMeterAxis.Lateral => rechts,
            GMeterAxis.Longitudinal => oben,
            GMeterAxis.Vertical => y,
            _ => MathF.Sqrt(x * x + z * z),
        };
    }

    private static (float Minus, float Plus) Spitzen(GMeterDesign d, GMeterBild b) => d.Axis switch
    {
        GMeterAxis.Lateral => d.FeltForce ? (b.Rechts, b.Links) : (b.Links, b.Rechts),
        GMeterAxis.Longitudinal => d.FeltForce ? (b.Vor, b.Brems) : (b.Brems, b.Vor),
        GMeterAxis.Vertical => (b.Runter, b.Hoch),
        _ => (0f, MathF.Max(MathF.Max(b.Rechts, b.Links), MathF.Max(b.Vor, b.Brems))),
    };

    private static string AchsenName(GMeterDesign d) => d.Axis switch
    {
        GMeterAxis.Lateral => Loc.T("LATERAL"),
        GMeterAxis.Longitudinal => Loc.T("BRAKE / ACCEL"),
        GMeterAxis.Vertical => Loc.T("VERTICAL"),
        _ => Loc.T("TOTAL"),
    };

    private static void Balken(Graphics g, GMeterDesign d, SpeedoDesign stil, GMeterBild b, float w, float h, Schicht s, bool waagrecht)
    {
        var mitVorzeichen = d.Axis != GMeterAxis.Combined;
        var rand = Math.Min(w, h) * 0.12f;
        var leise = F(d.DimColour, Color.Gray);
        var hell = F(d.TextColour, Color.White);
        RectangleF bahn;
        RectangleF titel;
        RectangleF zahl;
        if (waagrecht)
        {
            titel = new RectangleF(rand, h * 0.06f, w - rand * 2f, h * 0.24f);
            bahn = new RectangleF(rand, h * 0.36f, w - rand * 2f, h * 0.22f);
            zahl = new RectangleF(rand, h * 0.62f, w - rand * 2f, h * 0.32f);
        }
        else
        {
            titel = new RectangleF(w * 0.05f, h * 0.02f, w * 0.9f, w * 0.3f);
            bahn = new RectangleF(w * 0.3f, h * 0.1f, w * 0.4f, h * 0.74f);
            zahl = new RectangleF(w * 0.05f, h * 0.86f, w * 0.9f, h * 0.12f);
        }
        float Lage(float v)
        {
            var u = mitVorzeichen ? (v / d.MaxG + 1f) / 2f : v / d.MaxG;
            u = Math.Clamp(u, 0f, 1f);
            return waagrecht ? bahn.X + bahn.Width * u : bahn.Bottom - bahn.Height * u;
        }
        if (s == Schicht.Grund)
        {
            SpeedoRenderer.Rahmen(g, stil, new RectangleF(1f, 1f, w - 2f, h - 2f), kreis: false);
            using var spur = new SolidBrush(F(d.GridColour, Color.FromArgb(50, 255, 255, 255)));
            g.FillRectangle(spur, bahn);
            using var strich = new Pen(leise, 1.5f);
            var teile = d.Rings * (mitVorzeichen ? 2 : 1);
            for (var i = 0; i <= teile; i++)
            {
                var v = mitVorzeichen ? -d.MaxG + 2f * d.MaxG * i / teile : d.MaxG * i / teile;
                var o = Lage(v);
                if (waagrecht) { g.DrawLine(strich, o, bahn.Bottom, o, bahn.Bottom + bahn.Height * 0.3f); }
                else { g.DrawLine(strich, bahn.Right, o, bahn.Right + bahn.Width * 0.25f, o); }
            }
            if (d.DirectionLabels) { SpeedoRenderer.Schreibe(g, stil, AchsenName(d), titel, titel.Height * 0.7f, leise); }
            return;
        }
        var wert = Wert(d, b.X, b.Z, b.Y);
        var null0 = Lage(0f);
        var jetzt = Lage(wert);
        var farbe = Punktfarbe(d, MathF.Abs(wert));
        var teil = waagrecht
            ? new RectangleF(Math.Min(null0, jetzt), bahn.Y, Math.Abs(jetzt - null0), bahn.Height)
            : new RectangleF(bahn.X, Math.Min(null0, jetzt), bahn.Width, Math.Abs(jetzt - null0));
        if (d.Glow > 0.05f)
        {
            using var schein = new SolidBrush(Alpha(farbe, 0.25f * d.Glow));
            g.FillRectangle(schein, RectangleF.Inflate(teil, 4f, 4f));
        }
        using (var pinsel = new SolidBrush(farbe)) { g.FillRectangle(pinsel, teil); }
        if (d.Peaks)
        {
            var (minus, plus) = Spitzen(d, b);
            using var spitze = new Pen(F(d.EnvelopeColour, Color.Orange), 2f);
            foreach (var v in mitVorzeichen ? new[] { -minus, plus } : new[] { plus })
            {
                if (MathF.Abs(v) < 0.03f) { continue; }
                var o = Lage(v);
                if (waagrecht) { g.DrawLine(spitze, o, bahn.Y - 2f, o, bahn.Bottom + 2f); }
                else { g.DrawLine(spitze, bahn.X - 2f, o, bahn.Right + 2f, o); }
            }
        }
        if (d.Numbers) { SpeedoRenderer.Schreibe(g, stil, GE(MathF.Abs(wert)), zahl, zahl.Height * 0.85f, hell, gross: true); }
    }

    private static void Bogen(Graphics g, GMeterDesign d, SpeedoDesign stil, GMeterBild b, float w, float h, Schicht s)
    {
        var mitVorzeichen = d.Axis != GMeterAxis.Combined;
        var c = new PointF(w / 2f, h * 0.88f);
        var r = Math.Min(w * 0.44f, h * 0.8f);
        var dicke = r * 0.14f;
        var rect = new RectangleF(c.X - r, c.Y - r, r * 2f, r * 2f);
        float Winkel(float v)
        {
            var u = mitVorzeichen ? (v / d.MaxG + 1f) / 2f : v / d.MaxG;
            return 180f + 180f * Math.Clamp(u, 0f, 1f);
        }
        var leise = F(d.DimColour, Color.Gray);
        if (s == Schicht.Grund)
        {
            if (d.Frame != SpeedoFrame.None) { SpeedoRenderer.Rahmen(g, stil, new RectangleF(1f, 1f, w - 2f, h - 2f), kreis: false); }
            using var spur = new Pen(F(d.GridColour, Color.FromArgb(50, 255, 255, 255)), dicke);
            g.DrawArc(spur, rect, 180f, 180f);
            if (d.ScaleNumbers)
            {
                var teile = d.Rings * (mitVorzeichen ? 2 : 1);
                for (var i = 0; i <= teile; i++)
                {
                    var v = mitVorzeichen ? -d.MaxG + 2f * d.MaxG * i / teile : d.MaxG * i / teile;
                    var a = Winkel(v) * MathF.PI / 180f;
                    var p = new PointF(c.X + MathF.Cos(a) * (r - dicke * 1.6f), c.Y + MathF.Sin(a) * (r - dicke * 1.6f));
                    var px = r * 0.1f;
                    SpeedoRenderer.Schreibe(g, stil, _e.Skala(MathF.Abs(v)), new RectangleF(p.X - px * 1.5f, p.Y - px * 0.6f, px * 3f, px * 1.2f), px, leise);
                }
            }
            if (d.DirectionLabels) { SpeedoRenderer.Schreibe(g, stil, AchsenName(d), new RectangleF(c.X - r * 0.6f, c.Y - r * 0.18f, r * 1.2f, r * 0.14f), r * 0.1f, leise); }
            return;
        }
        var wert = Wert(d, b.X, b.Z, b.Y);
        var von = mitVorzeichen ? 270f : 180f;
        var bis = Winkel(wert);
        var farbe = Punktfarbe(d, MathF.Abs(wert));
        if (d.Glow > 0.05f)
        {
            using var schein = new Pen(Alpha(farbe, 0.25f * d.Glow), dicke * 1.8f);
            g.DrawArc(schein, rect, Math.Min(von, bis), Math.Abs(bis - von) + 0.1f);
        }
        using (var stift = new Pen(farbe, dicke)) { g.DrawArc(stift, rect, Math.Min(von, bis), Math.Abs(bis - von) + 0.1f); }
        if (d.Peaks)
        {
            var (minus, plus) = Spitzen(d, b);
            using var spitze = new Pen(F(d.EnvelopeColour, Color.Orange), Math.Max(2f, r * 0.02f));
            foreach (var v in mitVorzeichen ? new[] { -minus, plus } : new[] { plus })
            {
                if (MathF.Abs(v) < 0.03f) { continue; }
                var a = Winkel(v) * MathF.PI / 180f;
                g.DrawLine(spitze, c.X + MathF.Cos(a) * (r - dicke * 0.7f), c.Y + MathF.Sin(a) * (r - dicke * 0.7f),
                           c.X + MathF.Cos(a) * (r + dicke * 0.7f), c.Y + MathF.Sin(a) * (r + dicke * 0.7f));
            }
        }
        if (d.Numbers)
        {
            SpeedoRenderer.Schreibe(g, stil, G(MathF.Abs(wert)), new RectangleF(c.X - r * 0.5f, c.Y - r * 0.62f, r, r * 0.42f), r * 0.36f, F(d.TextColour, Color.White), gross: true);
        }
    }

    private static void Zeiger(Graphics g, GMeterDesign d, SpeedoDesign stil, GMeterBild b, float w, float h, Schicht s)
    {
        var mitVorzeichen = d.Axis != GMeterAxis.Combined;
        var c = new PointF(w / 2f, h / 2f);
        var r = w / 2f - w * 0.1f;
        float Winkel(float v)
        {
            var u = mitVorzeichen ? (v / d.MaxG + 1f) / 2f : v / d.MaxG;
            return 135f + 270f * Math.Clamp(u, 0f, 1.03f);
        }
        var leise = F(d.DimColour, Color.Gray);
        if (s == Schicht.Grund)
        {
            SpeedoRenderer.Rahmen(g, stil, new RectangleF(4f, 4f, w - 8f, h - 8f), kreis: true);
            using var gross = new Pen(F(d.TextColour, Color.White), Math.Max(1.5f, r * 0.02f));
            using var klein = new Pen(leise, Math.Max(1f, r * 0.01f));
            var teile = d.Rings * (mitVorzeichen ? 2 : 1) * 2;
            for (var i = 0; i <= teile; i++)
            {
                var v = mitVorzeichen ? -d.MaxG + 2f * d.MaxG * i / teile : d.MaxG * i / teile;
                var a = Winkel(v) * MathF.PI / 180f;
                var lang = i % 2 == 0 ? r * 0.1f : r * 0.05f;
                g.DrawLine(i % 2 == 0 ? gross : klein, c.X + MathF.Cos(a) * r, c.Y + MathF.Sin(a) * r, c.X + MathF.Cos(a) * (r - lang), c.Y + MathF.Sin(a) * (r - lang));
                if (i % 2 == 0 && d.ScaleNumbers)
                {
                    var p = new PointF(c.X + MathF.Cos(a) * (r - r * 0.24f), c.Y + MathF.Sin(a) * (r - r * 0.24f));
                    SpeedoRenderer.Schreibe(g, stil, _e.Skala(MathF.Abs(v)), new RectangleF(p.X - r * 0.14f, p.Y - r * 0.07f, r * 0.28f, r * 0.14f), r * 0.12f, F(d.TextColour, Color.White));
                }
            }
            if (d.DirectionLabels) { SpeedoRenderer.Schreibe(g, stil, AchsenName(d), new RectangleF(c.X - r * 0.5f, c.Y + r * 0.3f, r, r * 0.12f), r * 0.09f, leise); }
            return;
        }
        var wert = Wert(d, b.X, b.Z, b.Y);
        var aw = Winkel(wert) * MathF.PI / 180f;
        if (d.Peaks)
        {
            var (minus, plus) = Spitzen(d, b);
            using var spitze = new Pen(F(d.EnvelopeColour, Color.Orange), Math.Max(2f, r * 0.025f));
            foreach (var v in mitVorzeichen ? new[] { -minus, plus } : new[] { plus })
            {
                if (MathF.Abs(v) < 0.03f) { continue; }
                var a = Winkel(v) * MathF.PI / 180f;
                g.DrawLine(spitze, c.X + MathF.Cos(a) * r * 0.86f, c.Y + MathF.Sin(a) * r * 0.86f, c.X + MathF.Cos(a) * r, c.Y + MathF.Sin(a) * r);
            }
        }
        var farbe = F(d.DotColour, Color.Red);
        using (var nadel = new Pen(farbe, Math.Max(2f, r * 0.03f)) { EndCap = LineCap.Round })
        {
            g.DrawLine(nadel, c.X - MathF.Cos(aw) * r * 0.15f, c.Y - MathF.Sin(aw) * r * 0.15f, c.X + MathF.Cos(aw) * r * 0.92f, c.Y + MathF.Sin(aw) * r * 0.92f);
        }
        using (var nabe = new SolidBrush(Color.FromArgb(255, 40, 44, 50))) { g.FillEllipse(nabe, c.X - r * 0.07f, c.Y - r * 0.07f, r * 0.14f, r * 0.14f); }
        if (d.Numbers)
        {
            SpeedoRenderer.Schreibe(g, stil, GE(MathF.Abs(wert)), new RectangleF(c.X - r * 0.4f, c.Y + r * 0.45f, r * 0.8f, r * 0.22f), r * 0.2f, F(d.TextColour, Color.White), gross: true);
        }
    }

    // ================================================================== //
    // 3D
    // ================================================================== //

    private void Perspektive(Graphics g, GMeterDesign d, SpeedoDesign stil, GMeterBild b, float w, float h, Schicht s)
    {
        // Eine geneigte Scheibe fuer seitwaerts und laengs, ein Stab darueber fuer senkrecht.
        var c = new PointF(w / 2f, h * 0.62f);
        var rx = w * 0.44f;
        var ry = rx * 0.42f;
        PointF Auf(float rechts, float oben)
        {
            var betrag = MathF.Sqrt(rechts * rechts + oben * oben);
            if (betrag > d.MaxG && betrag > 0f) { rechts *= d.MaxG / betrag; oben *= d.MaxG / betrag; }
            return new PointF(c.X + rechts / d.MaxG * rx, c.Y - oben / d.MaxG * ry);
        }
        var gitter = F(d.GridColour, Color.FromArgb(60, 255, 255, 255));
        var leise = F(d.DimColour, Color.Gray);
        if (s == Schicht.Grund)
        {
            if (d.Frame != SpeedoFrame.None) { SpeedoRenderer.Rahmen(g, stil, new RectangleF(1f, 1f, w - 2f, h - 2f), kreis: false); }
            using var flaeche = new SolidBrush(Alpha(gitter, 0.25f));
            g.FillEllipse(flaeche, c.X - rx, c.Y - ry, rx * 2f, ry * 2f);
            using var stift = new Pen(gitter, 1.2f);
            for (var i = 1; i <= d.Rings; i++)
            {
                var u = (float)i / d.Rings;
                g.DrawEllipse(stift, c.X - rx * u, c.Y - ry * u, rx * u * 2f, ry * u * 2f);
            }
            g.DrawLine(stift, c.X - rx, c.Y, c.X + rx, c.Y);
            g.DrawLine(stift, c.X, c.Y - ry, c.X, c.Y + ry);
            if (d.DirectionLabels)
            {
                var px = w * 0.04f;
                SpeedoRenderer.Schreibe(g, stil, d.FeltForce ? Loc.T("BRAKE") : Loc.T("ACCEL"), new RectangleF(c.X - rx * 0.4f, c.Y - ry - px * 1.6f, rx * 0.8f, px * 1.3f), px, leise);
                SpeedoRenderer.Schreibe(g, stil, d.FeltForce ? Loc.T("ACCEL") : Loc.T("BRAKE"), new RectangleF(c.X - rx * 0.4f, c.Y + ry + px * 0.3f, rx * 0.8f, px * 1.3f), px, leise);
            }
            return;
        }
        Spur(g, d, b, (x, z) => { var (rr, oo) = Anzeige(d, x, z); return Auf(rr, oo); }, w);
        var (jr, jo) = Anzeige(d, b.X, b.Z);
        var fuss = Auf(jr, jo);
        // Der Stab: senkrechte G nach oben (Kompression) oder unten (Kuppe), bis 1 G = halbe Hoehe.
        var hoehe = Math.Clamp(b.Y, -1.5f, 1.5f) * h * 0.3f;
        var kopf = new PointF(fuss.X, fuss.Y - hoehe);
        using (var schatten = new SolidBrush(Color.FromArgb(110, 0, 0, 0)))
        {
            g.FillEllipse(schatten, fuss.X - w * 0.03f, fuss.Y - w * 0.012f, w * 0.06f, w * 0.024f);
        }
        using (var stab = new Pen(Alpha(F(d.TextColour, Color.White), 0.8f), Math.Max(1.5f, w * 0.008f)))
        {
            g.DrawLine(stab, fuss, kopf);
        }
        Punkt(g, d, kopf, w * 0.028f * d.DotSize, Punktfarbe(d, b.Gesamt));
        if (d.Numbers)
        {
            var px = w * 0.06f;
            SpeedoRenderer.Schreibe(g, stil, GE(b.Gesamt), new RectangleF(w * 0.05f, h * 0.03f, w * 0.5f, px * 1.2f), px, F(d.TextColour, Color.White), SpeedoRenderer.Links, gross: true);
            SpeedoRenderer.Schreibe(g, stil, string.Format(CultureInfo.InvariantCulture, "{0} {1}", Loc.T("vert"), (b.Y >= 0 ? "+" : "") + G(b.Y)),
                                    new RectangleF(w * 0.5f, h * 0.05f, w * 0.45f, px), px * 0.7f, leise, SpeedoRenderer.Rechts);
        }
    }

    private void Kugel(Graphics g, GMeterDesign d, SpeedoDesign stil, GMeterBild b, float w, float h, Schicht s)
    {
        var c = new PointF(w / 2f, h / 2f);
        var r = w * 0.43f;
        PointF Auf(float rechts, float oben)
        {
            var betrag = MathF.Sqrt(rechts * rechts + oben * oben);
            if (betrag > d.MaxG && betrag > 0f) { rechts *= d.MaxG / betrag; oben *= d.MaxG / betrag; }
            return new PointF(c.X + rechts / d.MaxG * r, c.Y - oben / d.MaxG * r);
        }
        if (s == Schicht.Grund)
        {
            // Eine beleuchtete Kugel: hell oben links, dunkel unten rechts.
            using var form = new GraphicsPath();
            form.AddEllipse(c.X - r, c.Y - r, r * 2f, r * 2f);
            using var licht = new PathGradientBrush(form)
            {
                CenterPoint = new PointF(c.X - r * 0.35f, c.Y - r * 0.4f),
                CenterColor = Alpha(F(d.GridColour, Color.White), 1.4f),
                SurroundColors = new[] { Color.FromArgb(200, 8, 10, 14) },
            };
            g.FillPath(licht, form);
            using var stift = new Pen(Alpha(F(d.GridColour, Color.White), 0.8f), 1f);
            for (var i = 1; i <= d.Rings; i++)
            {
                var u = (float)i / d.Rings;
                g.DrawEllipse(stift, c.X - r * u, c.Y - r * u, r * u * 2f, r * u * 2f);
            }
            // Laengen- und Breitenkreise andeuten.
            g.DrawEllipse(stift, c.X - r, c.Y - r * 0.35f, r * 2f, r * 0.7f);
            g.DrawEllipse(stift, c.X - r * 0.35f, c.Y - r, r * 0.7f, r * 2f);
            return;
        }
        Spur(g, d, b, (x, z) => { var (rr, oo) = Anzeige(d, x, z); return Auf(rr, oo); }, w);
        var (jr, jo) = Anzeige(d, b.X, b.Z);
        // Senkrechte G als Naehe: nach oben gedrueckt = groesser und heller.
        var naehe = 1f + Math.Clamp(b.Y, -1f, 1f) * 0.5f;
        Punkt(g, d, Auf(jr, jo), w * 0.035f * d.DotSize * naehe, Mische(Color.FromArgb(255, 60, 60, 70), Punktfarbe(d, b.Gesamt), 0.55f + 0.45f * naehe / 1.5f));
        if (d.Numbers)
        {
            var px = w * 0.065f;
            SpeedoRenderer.Schreibe(g, stil, GE(b.Gesamt), new RectangleF(c.X - r * 0.6f, c.Y + r * 0.45f, r * 1.2f, px * 1.2f), px, F(d.TextColour, Color.White), gross: true);
        }
    }

    // ================================================================== //
    // KARTESISCH (seit 2026-10-24, der Nutzer: "ein kartesisches System, wo man die Achsen sieht und den
    // Punkt im System, sowie auf der jeweiligen Achse am Balken den Ausschlag")
    // ================================================================== //

    /// <summary>Ein Balken entlang einer Achse, vom Ursprung bis zum Wert.</summary>
    private static void Achsbalken(Graphics g, GMeterDesign d, PointF von, PointF bis, float wert, float dicke)
    {
        if (MathF.Abs(wert) < 0.005f) { return; }
        var farbe = Punktfarbe(d, MathF.Abs(wert));
        if (d.Glow > 0.05f)
        {
            using var schein = new Pen(Alpha(farbe, 0.25f * d.Glow), dicke * 2.2f) { StartCap = LineCap.Flat, EndCap = LineCap.Round };
            g.DrawLine(schein, von, bis);
        }
        using (var stift = new Pen(Alpha(farbe, 0.85f), dicke) { StartCap = LineCap.Flat, EndCap = LineCap.Flat })
        {
            g.DrawLine(stift, von, bis);
        }
    }

    /// <summary>
    /// Die Zahl am Ende eines Achsbalkens, um <paramref name="versatz"/> (in Schrifthoehen) daneben und auf einem
    /// kleinen Schild, damit sie ueber der Teilung lesbar bleibt.
    /// </summary>
    private static void Achszahl(Graphics g, GMeterDesign d, SpeedoDesign stil, PointF bis, float wert, float px, PointF versatz)
    {
        if (!d.Numbers || MathF.Abs(wert) < 0.005f) { return; }
        var p = new PointF(bis.X + versatz.X * px, bis.Y + versatz.Y * px);
        var schild = new RectangleF(p.X - px * 1.4f, p.Y - px * 0.62f, px * 2.8f, px * 1.24f);
        var grund = F(d.Background, Color.Black);
        using (var pinsel = new SolidBrush(Color.FromArgb(d.ShowPlate && grund.A > 0 ? 220 : 150, grund.A > 0 ? grund : Color.Black)))
        using (var pfad = SpeedoRenderer.Platte(stil, schild, kreis: false))
        {
            g.FillPath(pinsel, pfad);
        }
        SpeedoRenderer.Schreibe(g, stil, G(MathF.Abs(wert)), schild, px * 0.9f, F(d.TextColour, Color.White));
    }

    private static void Gestrichelt(Graphics g, Color c, float dicke, PointF a, PointF b)
    {
        using var stift = new Pen(c, dicke) { DashStyle = DashStyle.Dash };
        g.DrawLine(stift, a, b);
    }

    private void Kartesisch(Graphics g, GMeterDesign d, SpeedoDesign stil, GMeterBild b, float w, float h, Schicht s)
    {
        var rand = w * 0.12f;
        var c = new PointF(w / 2f, h / 2f);
        var r = w / 2f - rand;
        PointF P(float rechts, float oben) => new(c.X + Math.Clamp(rechts / d.MaxG, -1f, 1f) * r, c.Y - Math.Clamp(oben / d.MaxG, -1f, 1f) * r);
        var gitter = F(d.GridColour, Color.FromArgb(60, 255, 255, 255));
        var leise = F(d.DimColour, Color.Gray);
        var px = Math.Max(7f, w * 0.04f);
        if (s == Schicht.Grund)
        {
            SpeedoRenderer.Rahmen(g, stil, new RectangleF(1f, 1f, w - 2f, h - 2f), kreis: false);
            using var fein = new Pen(Alpha(gitter, 0.55f), 1f);
            using var achse = new Pen(Alpha(gitter, 1.6f), Math.Max(1.5f, w * 0.006f)) { EndCap = LineCap.ArrowAnchor };
            for (var i = -d.Rings; i <= d.Rings; i++)
            {
                if (i == 0) { continue; }
                var o = r * i / d.Rings;
                g.DrawLine(fein, c.X + o, c.Y - r, c.X + o, c.Y + r);
                g.DrawLine(fein, c.X - r, c.Y + o, c.X + r, c.Y + o);
                if (d.ScaleNumbers)
                {
                    var wert = d.MaxG * i / d.Rings;
                    SpeedoRenderer.Schreibe(g, stil, _e.Skala(MathF.Abs(wert)), new RectangleF(c.X + o - px, c.Y + px * 0.25f, px * 2f, px), px * 0.8f, leise);
                    SpeedoRenderer.Schreibe(g, stil, _e.Skala(MathF.Abs(wert)), new RectangleF(c.X - px * 2.3f, c.Y - o - px * 0.5f, px * 2f, px), px * 0.8f, leise, SpeedoRenderer.Rechts);
                }
            }
            g.DrawLine(achse, c.X - r - rand * 0.3f, c.Y, c.X + r + rand * 0.45f, c.Y);
            g.DrawLine(achse, c.X, c.Y + r + rand * 0.3f, c.X, c.Y - r - rand * 0.45f);
            if (d.DirectionLabels)
            {
                SpeedoRenderer.Schreibe(g, stil, Loc.T("LATERAL"), new RectangleF(c.X + r * 0.45f, c.Y - px * 1.7f, r * 0.6f, px * 1.1f), px * 0.8f, leise, SpeedoRenderer.Rechts);
                SpeedoRenderer.Schreibe(g, stil, d.FeltForce ? Loc.T("BRAKE") : Loc.T("ACCEL"), new RectangleF(c.X + px * 0.5f, c.Y - r - rand * 0.7f, r * 0.8f, px * 1.1f), px * 0.8f, leise, SpeedoRenderer.Links);
            }
            return;
        }
        if (d.Envelope)
        {
            var punkte = new List<PointF>();
            for (var i = 0; i < GMeterBild.Sektoren; i++)
            {
                var wert = b.Huelle[i];
                if (wert <= 0.02f) { continue; }
                var a = ((i + 0.5f) / GMeterBild.Sektoren) * 2f * MathF.PI - MathF.PI;
                var (rr, oo) = Anzeige(d, MathF.Cos(a) * wert, MathF.Sin(a) * wert);
                punkte.Add(P(rr, oo));
            }
            if (punkte.Count >= 3)
            {
                using var flaeche = new SolidBrush(Alpha(F(d.EnvelopeColour, Color.Orange), 0.3f));
                g.FillPolygon(flaeche, punkte.ToArray());
            }
        }
        Spur(g, d, b, (x, z) => { var (rr, oo) = Anzeige(d, x, z); return P(rr, oo); }, w);
        var (jr, jo) = Anzeige(d, b.X, b.Z);
        var punkt = P(jr, jo);
        var lot = Alpha(F(d.DimColour, Color.Gray), 0.8f);
        Gestrichelt(g, lot, 1f, punkt, new PointF(punkt.X, c.Y));
        Gestrichelt(g, lot, 1f, punkt, new PointF(c.X, punkt.Y));
        var dicke = Math.Max(3f, w * 0.03f);
        // Die Zahl steht auf der dem Punkt abgewandten Seite -- dort liegt kein Lot.
        Achsbalken(g, d, c, new PointF(punkt.X, c.Y), jr, dicke);
        Achsbalken(g, d, c, new PointF(c.X, punkt.Y), jo, dicke);
        Punkt(g, d, punkt, w * 0.03f * d.DotSize, Punktfarbe(d, b.Gesamt));
        Achszahl(g, d, stil, new PointF(punkt.X, c.Y), jr, px, new PointF(0f, jo > 0 ? 1.1f : -1.1f));
        Achszahl(g, d, stil, new PointF(c.X, punkt.Y), jo, px, new PointF(jr > 0 ? -2f : 2f, 0f));
        if (d.Numbers)
        {
            SpeedoRenderer.Schreibe(g, stil, GE(b.Gesamt), new RectangleF(w * 0.05f, h - rand * 0.95f, w * 0.5f, rand * 0.8f), rand * 0.55f, F(d.TextColour, Color.White), SpeedoRenderer.Links, gross: true);
        }
    }

    private void Kartesisch3D(Graphics g, GMeterDesign d, SpeedoDesign stil, GMeterBild b, float w, float h, Schicht s)
    {
        // Die Achsen im Bild: seitwaerts waagrecht, laengs schraeg in die Tiefe, senkrecht nach oben.
        var o = new PointF(w * 0.46f, h * 0.6f);
        var l = w * 0.29f;
        var ex = new PointF(l, 0f);
        var ez = new PointF(l * 0.45f, -l * 0.55f);
        var ey = new PointF(0f, -l);
        PointF Ort(float x, float z, float y)
        {
            var ux = Math.Clamp(x / d.MaxG, -1.1f, 1.1f);
            var uz = Math.Clamp(z / d.MaxG, -1.1f, 1.1f);
            var uy = Math.Clamp(y / d.MaxG, -1.1f, 1.1f);
            return new PointF(o.X + ex.X * ux + ez.X * uz + ey.X * uy, o.Y + ex.Y * ux + ez.Y * uz + ey.Y * uy);
        }
        var gitter = F(d.GridColour, Color.FromArgb(60, 255, 255, 255));
        var leise = F(d.DimColour, Color.Gray);
        var px = Math.Max(7f, w * 0.036f);
        if (s == Schicht.Grund)
        {
            if (d.Frame != SpeedoFrame.None) { SpeedoRenderer.Rahmen(g, stil, new RectangleF(1f, 1f, w - 2f, h - 2f), kreis: false); }
            // Die Ebene: ein Gitter fuer seitwaerts und laengs.
            using (var ebene = new SolidBrush(Alpha(gitter, 0.25f)))
            {
                g.FillPolygon(ebene, new[] { Ort(-d.MaxG, -d.MaxG, 0), Ort(d.MaxG, -d.MaxG, 0), Ort(d.MaxG, d.MaxG, 0), Ort(-d.MaxG, d.MaxG, 0) });
            }
            using var fein = new Pen(Alpha(gitter, 0.6f), 1f);
            for (var i = -d.Rings; i <= d.Rings; i++)
            {
                var v = d.MaxG * i / d.Rings;
                g.DrawLine(fein, Ort(-d.MaxG, v, 0), Ort(d.MaxG, v, 0));
                g.DrawLine(fein, Ort(v, -d.MaxG, 0), Ort(v, d.MaxG, 0));
            }
            using var achse = new Pen(Alpha(gitter, 1.8f), Math.Max(1.5f, w * 0.006f)) { EndCap = LineCap.ArrowAnchor };
            g.DrawLine(achse, Ort(-d.MaxG * 1.05f, 0, 0), Ort(d.MaxG * 1.18f, 0, 0));
            g.DrawLine(achse, Ort(0, -d.MaxG * 1.05f, 0), Ort(0, d.MaxG * 1.18f, 0));
            g.DrawLine(achse, Ort(0, 0, -d.MaxG * 0.6f), Ort(0, 0, d.MaxG * 1.0f));
            if (d.ScaleNumbers)
            {
                for (var i = 1; i <= d.Rings; i++)
                {
                    var v = d.MaxG * i / d.Rings;
                    var p1 = Ort(v, 0, 0);
                    SpeedoRenderer.Schreibe(g, stil, _e.Skala(v), new RectangleF(p1.X - px, p1.Y + px * 0.2f, px * 2f, px), px * 0.75f, leise);
                    if (i * 2 <= d.Rings + 1)
                    {
                        var p3 = Ort(0, 0, v);
                        g.DrawLine(fein, p3.X - px * 0.4f, p3.Y, p3.X + px * 0.4f, p3.Y);
                        SpeedoRenderer.Schreibe(g, stil, _e.Skala(v), new RectangleF(p3.X + px * 0.5f, p3.Y - px * 0.5f, px * 2f, px), px * 0.75f, leise, SpeedoRenderer.Links);
                    }
                }
            }
            if (d.DirectionLabels)
            {
                var a1 = Ort(d.MaxG * 1.2f, 0, 0);
                SpeedoRenderer.Schreibe(g, stil, Loc.T("LATERAL"), new RectangleF(a1.X - px * 4f, a1.Y + px * 0.4f, px * 4.4f, px), px * 0.8f, leise, SpeedoRenderer.Rechts);
                var a2 = Ort(0, d.MaxG * 1.2f, 0);
                SpeedoRenderer.Schreibe(g, stil, d.FeltForce ? Loc.T("BRAKE") : Loc.T("ACCEL"), new RectangleF(a2.X + px * 0.3f, a2.Y - px * 1.2f, px * 5f, px), px * 0.8f, leise, SpeedoRenderer.Links);
                var a3 = Ort(0, 0, d.MaxG * 1.02f);
                SpeedoRenderer.Schreibe(g, stil, Loc.T("VERTICAL"), new RectangleF(a3.X + px * 0.4f, a3.Y - px * 0.4f, px * 5f, px), px * 0.8f, leise, SpeedoRenderer.Links);
            }
            return;
        }
        var (jr, jo) = Anzeige(d, b.X, b.Z);
        // Die Spur liegt im Raum -- mit ihrer senkrechten G.
        if (d.Trail != GMeterTrail.Off && b.Spur.Length > 1)
        {
            var spurFarbe = F(d.TrailColour, Color.DeepSkyBlue);
            var punkte = b.Spur.Where(p => p.Alter <= d.TrailSeconds).OrderByDescending(p => p.Alter).ToArray();
            for (var i = 1; i < punkte.Length; i += 2)
            {
                var (r0, o0) = Anzeige(d, punkte[i - 1].X, punkte[i - 1].Z);
                var (r1, o1) = Anzeige(d, punkte[i].X, punkte[i].Z);
                using var stift = new Pen(Alpha(spurFarbe, 1f - punkte[i].Alter / d.TrailSeconds), Math.Max(1f, w * 0.006f));
                g.DrawLine(stift, Ort(r0, o0, punkte[i - 1].Y), Ort(r1, o1, punkte[i].Y));
            }
        }
        var boden = Ort(jr, jo, 0);
        var punkt = Ort(jr, jo, b.Y);
        var lot = Alpha(F(d.DimColour, Color.Gray), 0.85f);
        // Vom Punkt auf die Ebene, und von dort parallel zu den Achsen auf die Achsen.
        Gestrichelt(g, lot, 1f, punkt, boden);
        Gestrichelt(g, lot, 1f, boden, Ort(jr, 0, 0));
        Gestrichelt(g, lot, 1f, boden, Ort(0, jo, 0));
        using (var schatten = new SolidBrush(Color.FromArgb(110, 0, 0, 0)))
        {
            g.FillEllipse(schatten, boden.X - w * 0.025f, boden.Y - w * 0.01f, w * 0.05f, w * 0.02f);
        }
        var dicke = Math.Max(3f, w * 0.026f);
        Achsbalken(g, d, o, Ort(jr, 0, 0), jr, dicke);
        Achsbalken(g, d, o, Ort(0, jo, 0), jo, dicke);
        Achsbalken(g, d, o, Ort(0, 0, b.Y), b.Y, dicke);
        Punkt(g, d, punkt, w * 0.028f * d.DotSize, Punktfarbe(d, MathF.Sqrt(b.Gesamt * b.Gesamt + b.Y * b.Y)));
        // Seitwaerts unter der Achse, laengs neben ihrem Ende, senkrecht links -- so treffen sie sich nicht im Ursprung.
        Achszahl(g, d, stil, Ort(jr, 0, 0), jr, px, new PointF(jr > 0 ? 0.8f : -0.8f, jo < 0 ? -1.3f : 1.3f));
        Achszahl(g, d, stil, Ort(0, jo, 0), jo, px, new PointF(jo > 0 ? 2f : -2f, jo > 0 ? -0.4f : 0.6f));
        Achszahl(g, d, stil, Ort(0, 0, b.Y), b.Y, px, new PointF(-2f, b.Y >= 0 ? -0.5f : 0.5f));
        if (d.Numbers)
        {
            SpeedoRenderer.Schreibe(g, stil, GE(b.Gesamt), new RectangleF(w * 0.04f, h * 0.03f, w * 0.5f, px * 1.5f), px * 1.3f, F(d.TextColour, Color.White), SpeedoRenderer.Links, gross: true);
        }
    }

    private static void Hoehenbalken(Graphics g, GMeterDesign d, SpeedoDesign stil, GMeterBild b, RectangleF r, Schicht s)
    {
        var grenze = Math.Max(0.5f, d.MaxG / 2f);
        var leise = F(d.DimColour, Color.Gray);
        var bahn = new RectangleF(r.X + r.Width * 0.3f, r.Y + r.Height * 0.08f, r.Width * 0.4f, r.Height * 0.8f);
        float Lage(float v) => bahn.Y + bahn.Height * (1f - (Math.Clamp(v / grenze, -1f, 1f) + 1f) / 2f);
        if (s == Schicht.Grund)
        {
            using var spur = new SolidBrush(F(d.GridColour, Color.FromArgb(50, 255, 255, 255)));
            g.FillRectangle(spur, bahn);
            using var mitte = new Pen(leise, 1.5f);
            g.DrawLine(mitte, bahn.X - 3f, Lage(0f), bahn.Right + 3f, Lage(0f));
            SpeedoRenderer.Schreibe(g, stil, Loc.T("vert"), new RectangleF(r.X, r.Y - r.Height * 0.02f, r.Width, r.Height * 0.08f), r.Height * 0.06f, leise);
            return;
        }
        var oben = Lage(Math.Max(0f, b.Y));
        var unten = Lage(Math.Min(0f, b.Y));
        using var pinsel = new SolidBrush(Punktfarbe(d, MathF.Abs(b.Y) * 2f));
        g.FillRectangle(pinsel, bahn.X, oben, bahn.Width, Math.Max(1f, unten - oben));
        if (d.Numbers)
        {
            SpeedoRenderer.Schreibe(g, stil, (b.Y >= 0 ? "+" : "") + G(b.Y), new RectangleF(r.X - r.Width * 0.2f, bahn.Bottom + 2f, r.Width * 1.4f, r.Height * 0.1f), r.Height * 0.07f, F(d.TextColour, Color.White));
        }
    }
}
