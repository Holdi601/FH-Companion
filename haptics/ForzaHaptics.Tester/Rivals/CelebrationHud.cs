using System.Drawing;
using System.Drawing.Drawing2D;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Ein paar Sekunden Feier ueber dem Spiel, wenn eine Runde die Bestzeit der Website
/// schlaegt: Karte mit Pokal und Zeit, Konfetti aus zwei Knallbonbons, Funkeln.
/// </summary>
/// <remarks>
/// ## Wann (seit 2026-09-27, auf Wunsch)
///
/// Genau dann, wenn <see cref="LapAutoSubmit"/> eine Runde als schneller als die
/// beste GUELTIGE Zeit desselben Autos auf dieser Strecke und Klasse erkennt -- ob
/// sie dann gesendet wird oder wartet, spielt keine Rolle: gefeiert wird die Fahrt,
/// nicht die Uebertragung. Abschaltbar im Reiter "Lap delta HUD", ab Werk an.
///
/// ## Warum oben und kurz
///
/// Die Runde endet mitten im Rennen, und die naechste laeuft schon. Die Feier steht
/// darum im oberen Band, wo Himmel und Horizon-Anzeige sind, nicht auf der Strasse,
/// und ist nach gut fuenf Sekunden weg ("Overlay must not linger"). Sie ist
/// durchklickbar und nimmt nie den Fokus (siehe LayeredHud).
///
/// ## Zeichnen als reine Funktion der Zeit
///
/// <see cref="Male"/> bekommt die Sekunden seit Beginn und rechnet jedes Teilchen
/// geschlossen aus (Wurf mit Luftwiderstand, siehe <see cref="Konfetti.Lage"/>) --
/// kein Zustand, der von Bild zu Bild weitergeschrieben wird. So zeichnen Fenster,
/// Selbsttest und Einzelbilder genau dasselbe, und ein verspaeteter Takt ruckelt
/// nur, statt die Flugbahnen zu verbiegen.
/// </remarks>
internal sealed class CelebrationHud : LayeredHud
{
    /// <summary>
    /// Welche Feier: die Rekordrunde (Gold, Pokal) -- oder ein Auto, das durch die eigene
    /// Runde NEU auf eine Bestenliste kommt (Tuerkis, ein Auto mit Plus, ruhiger;
    /// seit 2026-09-27, auf Wunsch "a bit celebratory but more like thanks to you").
    /// </summary>
    internal enum FeierArt { Rekord, NeuesAuto }

    /// <summary>Was die Karte sagt -- fertig formuliert, in der Sprache des Nutzers.</summary>
    /// <param name="Chip">Fester Text fuer den Chip (etwa "NEW"); sonst zaehlt der Vorsprung hoch.</param>
    internal sealed record Anlass(string Titel, string Zeit, double VorsprungSekunden, string Vergleich, string Detail,
                                  FeierArt Art = FeierArt.Rekord, string? Chip = null)
    {
        /// <summary>Der fertige Chip, wie er am Ende steht.</summary>
        public string Vorsprung => Chip ?? VorsprungText(VorsprungSekunden);
    }

    /// <summary>"−0.556 s" -- mit echtem Minuszeichen, im Zahlenformat des Nutzers.</summary>
    internal static string VorsprungText(double sekunden) => $"−{Math.Max(0, sekunden):0.000} s";

    /// <summary>So lange steht sie, in Sekunden -- danach ist sie weg.</summary>
    internal const double Dauer = 5.2;

    private const double Ausblenden = 0.9;

    private readonly System.Windows.Forms.Timer _takt = new() { Interval = 40 };
    private Anlass? _anlass;
    private Konfetti? _konfetti;
    private DateTime _beginn;
    private double _t;

    public override int Ebene => 5;

    public CelebrationHud(Rectangle flaeche) : base(flaeche)
    {
        _takt.Tick += (_, _) => Weiter();
    }

    /// <summary>Die Feier beginnen; eine laufende faengt von vorn an.</summary>
    public void Zeige(Anlass anlass, int zufall)
    {
        _anlass = anlass;
        _konfetti = new Konfetti(zufall, anlass.Art);
        _beginn = DateTime.UtcNow;
        _t = 0;
        Render();
        if (!Visible) { Show(); }
        TopMost = true;
        _takt.Start();
    }

    private void Weiter()
    {
        _t = (DateTime.UtcNow - _beginn).TotalSeconds;
        if (_t >= Dauer)
        {
            Beenden();
            return;
        }
        Render();
    }

    public void Beenden()
    {
        _takt.Stop();
        _anlass = null;
        _konfetti = null;
        Render();
        if (Visible) { Hide(); }
    }

    protected override Rectangle Extent(Graphics messung) =>
        _anlass is null ? Rectangle.Empty : Rectangle.Ceiling(Buehne(AreaSize));

    protected override void Draw(Graphics g)
    {
        if (_anlass is { } a && _konfetti is { } k) { Male(g, AreaSize, a, k, _t); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _takt.Dispose(); }
        base.Dispose(disposing);
    }

    // ------------------------------------------------------------------ //
    // Aufbau
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Das Band, in dem gefeiert wird: obere 46 %, mittlere 64 % der Breite. Fest, damit
    /// die Zeichenflaeche nicht in jedem Bild neu angelegt werden muss.
    /// </summary>
    internal static RectangleF Buehne(Size flaeche) =>
        new(flaeche.Width * 0.18f, 0, flaeche.Width * 0.64f, flaeche.Height * 0.46f);

    private static float Skala(Size flaeche) =>
        GameArea.ResolutionScale(new Rectangle(Point.Empty, flaeche));

    private sealed class Schriften : IDisposable
    {
        public readonly Font Titel, Zeit, Chip, Klein;

        public Schriften(float s)
        {
            // In PIXELN, nicht in Punkten -- siehe CourseShapeHud.Schrift.
            static float Px(float pt, float s) => pt * 96f / 72f * s;
            Titel = new Font("Segoe UI Semibold", Px(19f, s), GraphicsUnit.Pixel);
            Zeit = new Font("Segoe UI Semibold", Px(30f, s), GraphicsUnit.Pixel);
            Chip = new Font("Segoe UI Semibold", Px(14f, s), GraphicsUnit.Pixel);
            Klein = new Font("Segoe UI", Px(11.5f, s), GraphicsUnit.Pixel);
        }

        public void Dispose()
        {
            Titel.Dispose();
            Zeit.Dispose();
            Chip.Dispose();
            Klein.Dispose();
        }
    }

    internal readonly record struct Karte(RectangleF Kasten, float Rand, RectangleF Pokal, float TextX,
                                          float TitelH, SizeF ZeitGroesse, SizeF ChipGroesse,
                                          float VergleichH, float DetailH);

    private static Karte Lege(Graphics g, Size flaeche, Anlass a, Schriften f, float s)
    {
        var buehne = Buehne(flaeche);
        var rand = 20f * s;
        var pokal = 80f * s;
        var maxText = Math.Max(120f * s, (buehne.Width * 0.9f) - (3 * rand) - pokal);
        var titel = g.MeasureString(a.Titel, f.Titel, (int)maxText);
        var zeit = g.MeasureString(a.Zeit, f.Zeit);
        var chipText = g.MeasureString(a.Vorsprung, f.Chip);
        var chip = new SizeF(chipText.Width + (14f * s), chipText.Height + (4f * s));
        var vergleich = g.MeasureString(a.Vergleich, f.Klein, (int)maxText);
        var detail = string.IsNullOrWhiteSpace(a.Detail) ? SizeF.Empty : g.MeasureString(a.Detail, f.Klein, (int)maxText);
        var textBreite = Math.Min(maxText, Math.Max(Math.Max(titel.Width, zeit.Width + (10f * s) + chip.Width),
                                                    Math.Max(vergleich.Width, detail.Width)));
        var textHoehe = titel.Height + zeit.Height + vergleich.Height + detail.Height;
        var breite = (3 * rand) + pokal + textBreite;
        var hoehe = (2 * rand) + Math.Max(pokal, textHoehe);
        var x = (flaeche.Width - breite) / 2f;
        var y = flaeche.Height * 0.11f;
        var kasten = new RectangleF(x, y, breite, hoehe);
        var pokalRahmen = new RectangleF(x + rand, y + ((hoehe - pokal) / 2f), pokal, pokal);
        return new Karte(kasten, rand, pokalRahmen, pokalRahmen.Right + rand, titel.Height, zeit, chip,
                         vergleich.Height, detail.Height);
    }

    // ------------------------------------------------------------------ //
    // Zeichnen
    // ------------------------------------------------------------------ //

    private static readonly Color Gold = Color.FromArgb(255, 209, 102);
    private static readonly Color GoldHell = Color.FromArgb(255, 236, 170);
    private static readonly Color GoldTief = Color.FromArgb(217, 119, 6);

    /// <summary>Die Farben einer Feier. Alpha steht jeweils in der Zeichnung.</summary>
    private readonly record struct Palette(Color Titel, Color Hell, Color Tief, Color Licht, Color Chip, int StrahlAlpha);

    private static Palette PaletteFuer(FeierArt art) => art == FeierArt.NeuesAuto
        ? new Palette(Color.FromArgb(94, 234, 212), Color.FromArgb(167, 243, 229), Color.FromArgb(13, 148, 136),
                      Color.FromArgb(120, 232, 214), Color.FromArgb(13, 148, 136), 44)
        : new Palette(Gold, GoldHell, GoldTief, Color.FromArgb(255, 214, 120), Color.FromArgb(16, 185, 129), 70);

    private static Color A(Color c, float f) =>
        Color.FromArgb(Math.Clamp((int)(c.A * f), 0, 255), c.R, c.G, c.B);

    private static float Klemme(double x) => (float)Math.Clamp(x, 0.0, 1.0);

    private static float RausMitSchwung(float x)
    {
        const float c1 = 1.70158f;
        const float c3 = c1 + 1f;
        var u = x - 1f;
        return 1f + (c3 * u * u * u) + (c1 * u * u);
    }

    private static float Raus(float x) => 1f - ((1f - x) * (1f - x) * (1f - x));

    /// <summary>Ein Bild der Feier, <paramref name="t"/> Sekunden nach Beginn.</summary>
    internal static void Male(Graphics g, Size flaeche, Anlass a, Konfetti k, double t)
    {
        if (t < 0 || t >= Dauer) { return; }
        // KEIN ClearType auf durchsichtigem Grund (siehe LayeredHud) -- hier noch einmal
        // gesetzt, damit Selbsttest und Einzelbilder genauso zeichnen wie das Fenster.
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        var ganz = t > Dauer - Ausblenden ? (float)((Dauer - t) / Ausblenden) : 1f;
        var s = Skala(flaeche);
        var buehne = Buehne(flaeche);
        using var f = new Schriften(s);
        var karte = Lege(g, flaeche, a, f, s);
        var mitte = new PointF(karte.Kasten.X + (karte.Kasten.Width / 2f), karte.Kasten.Y + (karte.Kasten.Height / 2f));
        var zeit = (float)t;

        var farben = PaletteFuer(a.Art);
        Strahlen(g, buehne, mitte, zeit, ganz, farben);
        Ringe(g, buehne, karte.Kasten, mitte, zeit, ganz, s, farben);
        k.Male(g, buehne, karte.Kasten, zeit, ganz, s, vorne: false);
        KarteMalen(g, a, f, karte, mitte, zeit, ganz, s, farben);
        Funkeln(g, k, karte.Kasten, zeit, ganz, s, farben);
        k.Male(g, buehne, karte.Kasten, zeit, ganz, s, vorne: true);
    }

    /// <summary>Sanfte Lichtstrahlen hinter der Karte, langsam drehend.</summary>
    private static void Strahlen(Graphics g, RectangleF buehne, PointF mitte, float t, float ganz, Palette farben)
    {
        var staerke = t < 0.4f ? t / 0.4f : t < 2.2f ? 1f : Math.Max(0f, 1f - ((t - 2.2f) / 1.0f));
        if (staerke <= 0f) { return; }
        var rx = Math.Min(mitte.X - buehne.Left, buehne.Right - mitte.X) * 0.95f;
        var ry = (buehne.Bottom - mitte.Y) * 0.95f;
        if (rx <= 1 || ry <= 1) { return; }
        using var huelle = new GraphicsPath();
        huelle.AddEllipse(mitte.X - rx, mitte.Y - ry, 2 * rx, 2 * ry);
        using var pinsel = new PathGradientBrush(huelle)
        {
            CenterPoint = mitte,
            CenterColor = A(Color.FromArgb(farben.StrahlAlpha, farben.Licht), staerke * ganz),
            SurroundColors = new[] { Color.FromArgb(0, farben.Licht) },
        };
        using var strahlen = new GraphicsPath();
        const int anzahl = 14;
        var dreh = t * 10f;
        var r = Math.Max(rx, ry);
        for (var i = 0; i < anzahl; i++)
        {
            var w0 = (dreh + (i * 360f / anzahl)) * MathF.PI / 180f;
            var w1 = w0 + (360f / anzahl * 0.42f * MathF.PI / 180f);
            strahlen.AddPolygon(new[]
            {
                mitte,
                new PointF(mitte.X + (r * MathF.Cos(w0)), mitte.Y + (r * MathF.Sin(w0))),
                new PointF(mitte.X + (r * MathF.Cos(w1)), mitte.Y + (r * MathF.Sin(w1))),
            });
        }
        g.FillPath(pinsel, strahlen);
    }

    /// <summary>Zwei Lichtringe, die aus der Karte heraus auseinanderlaufen.</summary>
    private static void Ringe(Graphics g, RectangleF buehne, RectangleF kasten, PointF mitte, float t, float ganz, float s,
                              Palette farben)
    {
        var zustand = g.Save();
        g.SetClip(buehne);
        foreach (var beginn in new[] { 0.02f, 0.16f })
        {
            var u = (t - beginn) / 0.85f;
            if (u <= 0f || u >= 1f) { continue; }
            var e = Raus(u);
            var rx = (kasten.Width / 2f) * (0.7f + (0.75f * e));
            var ry = (kasten.Height / 2f) * (0.7f + (1.6f * e));
            using var stift = new Pen(A(Color.FromArgb(200, farben.Hell), (1f - u) * ganz), Math.Max(1f, 4f * s * (1f - u)));
            g.DrawEllipse(stift, mitte.X - rx, mitte.Y - ry, 2 * rx, 2 * ry);
        }
        g.Restore(zustand);
    }

    private static GraphicsPath Abgerundet(RectangleF r, float radius)
    {
        var d = Math.Min(radius * 2f, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static void KarteMalen(Graphics g, Anlass a, Schriften f, Karte karte, PointF mitte,
                                   float t, float ganz, float s, Palette farben)
    {
        // HERAUSSPRINGEN: von 60 % auf etwas ueber 100 % und zurueck, in einer halben Sekunde.
        var u = Klemme(t / 0.5);
        var massstab = 0.6f + (0.4f * RausMitSchwung(u));
        var sicht = Klemme(t / 0.18) * ganz;
        if (sicht <= 0f) { return; }

        var zustand = g.Save();
        g.TranslateTransform(mitte.X, mitte.Y);
        g.ScaleTransform(massstab, massstab);
        g.TranslateTransform(-mitte.X, -mitte.Y);

        var kasten = karte.Kasten;
        var radius = 14f * s;

        // Schein um die Karte.
        for (var i = 3; i >= 1; i--)
        {
            var aussen = RectangleF.Inflate(kasten, i * 5f * s, i * 5f * s);
            using var schein = Abgerundet(aussen, radius + (i * 5f * s));
            using var scheinPinsel = new SolidBrush(A(Color.FromArgb(34 / i, farben.Licht), sicht));
            g.FillPath(scheinPinsel, schein);
        }

        using var form = Abgerundet(kasten, radius);
        using (var grund = new LinearGradientBrush(kasten, A(Color.FromArgb(240, 26, 36, 53), sicht),
                                                   A(Color.FromArgb(240, 12, 17, 26), sicht), LinearGradientMode.Vertical))
        {
            g.FillPath(grund, form);
        }

        // GLANZ: ein schraeger Lichtstreif laeuft einmal ueber die Karte.
        var glanzU = (t - 0.45f) / 0.8f;
        if (glanzU > 0f && glanzU < 1f)
        {
            var innen = g.Save();
            g.SetClip(form, CombineMode.Intersect);
            var band = kasten.Height * 0.9f;
            var x = kasten.Left - band + ((kasten.Width + (2 * band)) * glanzU);
            var streif = new RectangleF(x - (band / 2f), kasten.Top, band, kasten.Height);
            using var glanz = new LinearGradientBrush(new PointF(streif.Left, 0), new PointF(streif.Right, 0),
                                                      Color.FromArgb(0, 255, 255, 255), Color.FromArgb(0, 255, 255, 255));
            glanz.InterpolationColors = new ColorBlend
            {
                Colors = new[] { Color.FromArgb(0, 255, 255, 255), A(Color.FromArgb(60, 255, 255, 255), sicht),
                                 Color.FromArgb(0, 255, 255, 255) },
                Positions = new[] { 0f, 0.5f, 1f },
            };
            var neigung = kasten.Height * 0.35f;
            g.FillPolygon(glanz, new[]
            {
                new PointF(streif.Left + neigung, streif.Top), new PointF(streif.Right + neigung, streif.Top),
                new PointF(streif.Right - neigung, streif.Bottom), new PointF(streif.Left - neigung, streif.Bottom),
            });
            g.Restore(innen);
        }

        using (var rahmen = new LinearGradientBrush(kasten, A(farben.Hell, sicht), A(farben.Tief, sicht), LinearGradientMode.ForwardDiagonal))
        using (var stift = new Pen(rahmen, Math.Max(1.5f, 2.2f * s)))
        {
            g.DrawPath(stift, form);
        }

        if (a.Art == FeierArt.NeuesAuto)
        {
            // EIN AUTO FAEHRT HEREIN, dann springt das Plus auf.
            NeuesAutoSymbol(g, karte.Pokal, t, sicht, farben);
        }
        else
        {
            // POKAL, mit einem kleinen gedaempften Wackeln.
            var wackeln = t < 0.25f ? 0f : 9f * MathF.Sin(13f * (t - 0.25f)) * MathF.Exp(-2.6f * (t - 0.25f));
            Pokal(g, karte.Pokal, wackeln, sicht, s);
        }

        // TEXT
        var y = kasten.Top + karte.Rand + Math.Max(0f, (kasten.Height - (2 * karte.Rand)
                - (karte.TitelH + karte.ZeitGroesse.Height + karte.VergleichH + karte.DetailH)) / 2f);
        var textBreite = kasten.Right - karte.Rand - karte.TextX;
        using (var titel = new SolidBrush(A(farben.Titel, sicht)))
        {
            g.DrawString(a.Titel, f.Titel, titel, new RectangleF(karte.TextX, y, textBreite, karte.TitelH));
        }
        y += karte.TitelH;
        using (var weiss = new SolidBrush(A(Color.FromArgb(248, 250, 252), sicht)))
        {
            g.DrawString(a.Zeit, f.Zeit, weiss, karte.TextX, y);
        }
        var chip = new RectangleF(karte.TextX + karte.ZeitGroesse.Width + (6f * s),
                                  y + ((karte.ZeitGroesse.Height - karte.ChipGroesse.Height) / 2f),
                                  karte.ChipGroesse.Width, karte.ChipGroesse.Height);
        // Der Vorsprung ZAEHLT HOCH, in der ersten Sekunde; die Breite steht schon fest.
        // Ein fester Chip ("NEW") zaehlt nicht.
        var zaehlen = Klemme((t - 0.2f) / 0.8f);
        var vorsprung = zaehlen >= 1f || a.Chip is not null
            ? a.Vorsprung
            : VorsprungText(a.VorsprungSekunden * Raus(zaehlen));
        using (var pille = Abgerundet(chip, chip.Height / 2f))
        using (var gruen = new SolidBrush(A(Color.FromArgb(235, farben.Chip), sicht)))
        using (var chipText = new SolidBrush(A(Color.White, sicht)))
        using (var mittig = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            g.FillPath(gruen, pille);
            g.DrawString(vorsprung, f.Chip, chipText, chip, mittig);
        }
        y += karte.ZeitGroesse.Height;
        using (var grau = new SolidBrush(A(Color.FromArgb(166, 180, 200), sicht)))
        {
            g.DrawString(a.Vergleich, f.Klein, grau, new RectangleF(karte.TextX, y, textBreite, karte.VergleichH));
        }
        y += karte.VergleichH;
        if (karte.DetailH > 0)
        {
            using var hell = new SolidBrush(A(Color.FromArgb(206, 214, 226), sicht));
            g.DrawString(a.Detail, f.Klein, hell, new RectangleF(karte.TextX, y, textBreite, karte.DetailH));
        }

        g.Restore(zustand);
    }

    /// <summary>Ein goldener Pokal, gezeichnet -- kein Emoji, das GDI+ nur einfarbig kann.</summary>
    private static void Pokal(Graphics g, RectangleF box, float winkel, float sicht, float s)
    {
        var p = box.Width;
        var zustand = g.Save();
        g.TranslateTransform(box.X + (p / 2f), box.Y + (p * 0.9f));
        g.RotateTransform(winkel);
        g.TranslateTransform(-(p / 2f), -(p * 0.9f));
        PointF P(float x, float y) => new(x * p, y * p);

        using var gold = new LinearGradientBrush(new RectangleF(0, 0, p, p), A(GoldHell, sicht), A(GoldTief, sicht),
                                                 LinearGradientMode.ForwardDiagonal);
        using (var henkel = new Pen(gold, 0.07f * p))
        {
            g.DrawArc(henkel, 0.06f * p, 0.14f * p, 0.26f * p, 0.26f * p, 90, 180);
            g.DrawArc(henkel, 0.68f * p, 0.14f * p, 0.26f * p, 0.26f * p, 270, 180);
        }
        using (var schale = new GraphicsPath())
        {
            schale.AddLine(P(0.2f, 0.1f), P(0.8f, 0.1f));
            schale.AddBezier(P(0.8f, 0.1f), P(0.8f, 0.46f), P(0.64f, 0.6f), P(0.5f, 0.62f));
            schale.AddBezier(P(0.5f, 0.62f), P(0.36f, 0.6f), P(0.2f, 0.46f), P(0.2f, 0.1f));
            schale.CloseFigure();
            g.FillPath(gold, schale);
        }
        g.FillRectangle(gold, 0.45f * p, 0.6f * p, 0.1f * p, 0.14f * p);
        using (var fuss = Abgerundet(new RectangleF(0.3f * p, 0.73f * p, 0.4f * p, 0.09f * p), 0.03f * p))
        {
            g.FillPath(gold, fuss);
        }
        using (var sockel = Abgerundet(new RectangleF(0.24f * p, 0.82f * p, 0.52f * p, 0.08f * p), 0.03f * p))
        using (var dunkel = new SolidBrush(A(Color.FromArgb(146, 64, 14), sicht)))
        {
            g.FillPath(dunkel, sockel);
        }
        // Lichtkante links auf der Schale.
        using (var licht = new Pen(A(Color.FromArgb(150, 255, 255, 255), sicht), Math.Max(1f, 0.035f * p)))
        {
            g.DrawBezier(licht, P(0.29f, 0.16f), P(0.29f, 0.36f), P(0.34f, 0.46f), P(0.42f, 0.52f));
        }
        g.Restore(zustand);
    }

    /// <summary>
    /// Ein Auto von der Seite, das in einer halben Sekunde hereinrollt, und ein goldenes
    /// Plus, das danach aufspringt -- "neu auf der Liste".
    /// </summary>
    private static void NeuesAutoSymbol(Graphics g, RectangleF box, float t, float sicht, Palette farben)
    {
        var p = box.Width;
        var rein = Raus(Klemme(t / 0.55));
        var zustand = g.Save();
        g.SetClip(RectangleF.Inflate(box, p * 0.12f, p * 0.12f));
        g.TranslateTransform(box.X - (p * 0.9f * (1f - rein)), box.Y);
        PointF P(float x, float y) => new(x * p, y * p);

        using var lack = new LinearGradientBrush(new RectangleF(0, 0.3f * p, p, 0.5f * p), A(farben.Hell, sicht),
                                                 A(farben.Tief, sicht), LinearGradientMode.Vertical);
        using (var koerper = new GraphicsPath())
        {
            koerper.AddLine(P(0.06f, 0.74f), P(0.06f, 0.6f));
            koerper.AddBezier(P(0.06f, 0.6f), P(0.08f, 0.53f), P(0.16f, 0.52f), P(0.26f, 0.51f));
            koerper.AddLine(P(0.26f, 0.51f), P(0.37f, 0.37f));
            koerper.AddLine(P(0.37f, 0.37f), P(0.63f, 0.37f));
            koerper.AddLine(P(0.63f, 0.37f), P(0.76f, 0.51f));
            koerper.AddBezier(P(0.76f, 0.51f), P(0.86f, 0.52f), P(0.94f, 0.55f), P(0.94f, 0.62f));
            koerper.AddLine(P(0.94f, 0.62f), P(0.94f, 0.74f));
            koerper.CloseFigure();
            g.FillPath(lack, koerper);
        }
        using (var scheibe = new SolidBrush(A(Color.FromArgb(215, 20, 30, 44), sicht)))
        {
            g.FillPolygon(scheibe, new[] { P(0.31f, 0.5f), P(0.39f, 0.41f), P(0.49f, 0.41f), P(0.49f, 0.5f) });
            g.FillPolygon(scheibe, new[] { P(0.53f, 0.5f), P(0.53f, 0.41f), P(0.61f, 0.41f), P(0.7f, 0.5f) });
        }
        using (var reifen = new SolidBrush(A(Color.FromArgb(17, 24, 39), sicht)))
        using (var felge = new SolidBrush(A(Color.FromArgb(203, 213, 225), sicht)))
        {
            foreach (var x in new[] { 0.27f, 0.73f })
            {
                g.FillEllipse(reifen, (x - 0.1f) * p, 0.64f * p, 0.2f * p, 0.2f * p);
                g.FillEllipse(felge, (x - 0.045f) * p, 0.695f * p, 0.09f * p, 0.09f * p);
            }
        }
        using (var licht = new SolidBrush(A(Color.FromArgb(255, 250, 225), sicht)))
        {
            g.FillEllipse(licht, 0.86f * p, 0.57f * p, 0.06f * p, 0.04f * p);
        }

        // Das Plus: springt nach dem Hereinrollen auf, mit etwas Schwung.
        var plus = RausMitSchwung(Klemme((t - 0.45f) / 0.35f));
        if (plus > 0f)
        {
            var r = 0.15f * p * plus;
            var m = P(0.8f, 0.22f);
            using var schein = new SolidBrush(A(Color.FromArgb(70, 255, 209, 102), sicht));
            g.FillEllipse(schein, m.X - (r * 1.45f), m.Y - (r * 1.45f), r * 2.9f, r * 2.9f);
            using var gold = new LinearGradientBrush(new RectangleF(m.X - r, m.Y - r, 2 * r, 2 * r),
                                                     A(GoldHell, sicht), A(GoldTief, sicht), LinearGradientMode.ForwardDiagonal);
            g.FillEllipse(gold, m.X - r, m.Y - r, 2 * r, 2 * r);
            using var weiss = new SolidBrush(A(Color.White, sicht));
            var b = r * 0.28f;
            g.FillRectangle(weiss, m.X - (r * 0.58f), m.Y - (b / 2f), r * 1.16f, b);
            g.FillRectangle(weiss, m.X - (b / 2f), m.Y - (r * 0.58f), b, r * 1.16f);
        }
        g.Restore(zustand);
    }

    private static void Stern(Graphics g, Brush pinsel, float x, float y, float r, float drehung)
    {
        var innen = r * 0.24f;
        var punkte = new PointF[8];
        for (var i = 0; i < 8; i++)
        {
            var w = ((i * 45f) + drehung) * MathF.PI / 180f;
            var l = i % 2 == 0 ? r : innen;
            punkte[i] = new PointF(x + (l * MathF.Sin(w)), y - (l * MathF.Cos(w)));
        }
        g.FillPolygon(pinsel, punkte);
    }

    /// <summary>Funkelnde Sterne rund um die Karte.</summary>
    private static void Funkeln(Graphics g, Konfetti k, RectangleF kasten, float t, float ganz, float s, Palette farben)
    {
        foreach (var f in k.Funken)
        {
            var u = (t - f.Start) / f.Leben;
            if (u <= 0f || u >= 1f) { continue; }
            var groesse = MathF.Sin(MathF.PI * u) * f.Groesse * s;
            var x = kasten.Left + (f.X * kasten.Width);
            var y = kasten.Top + (f.Y * kasten.Height);
            using var schein = new SolidBrush(A(Color.FromArgb(70, farben.Licht), ganz));
            Stern(g, schein, x, y, groesse * 1.9f, 45f * u);
            using var kern = new SolidBrush(A(Color.FromArgb(245, 255, 250, 225), ganz));
            Stern(g, kern, x, y, groesse, 45f * u);
        }
    }

    // ------------------------------------------------------------------ //
    // Konfetti
    // ------------------------------------------------------------------ //

    /// <summary>Alle Teilchen einer Feier, einmal ausgewuerfelt; die Lage rechnet <see cref="Lage"/>.</summary>
    internal sealed class Konfetti
    {
        private static readonly Color[] Bunt =
        {
            Color.FromArgb(255, 209, 102), Color.FromArgb(255, 107, 107), Color.FromArgb(45, 212, 191),
            Color.FromArgb(96, 165, 250), Color.FromArgb(167, 139, 250), Color.FromArgb(244, 114, 182),
            Color.FromArgb(248, 250, 252), Color.FromArgb(251, 146, 60),
        };

        // Ruhiger fuer ein neues Auto: Tuerkis, Himmelblau, Weiss und ein wenig Gold.
        private static readonly Color[] Ruhig =
        {
            Color.FromArgb(45, 212, 191), Color.FromArgb(94, 234, 212), Color.FromArgb(125, 211, 252),
            Color.FromArgb(248, 250, 252), Color.FromArgb(255, 209, 102),
        };

        /// <summary>Art: 0 Streifen, 1 Punkt, 2 Luftschlange. Quelle: -1/+1 Knallbonbon links/rechts, 0 von oben.</summary>
        internal readonly record struct Teil(int Art, int Quelle, Color Farbe, float X0, float Vx, float Vy,
                                             float Zug, float Schwere, float Breite, float Hoehe,
                                             float Dreh0, float DrehV, float Kipp0, float KippV,
                                             float PendelA, float PendelW, float PendelP, float Start, bool Vorne);

        internal readonly record struct Funke(float X, float Y, float Start, float Leben, float Groesse);

        public readonly Teil[] Teile;
        public readonly Funke[] Funken;

        public Konfetti(int zufall, FeierArt stil = FeierArt.Rekord)
        {
            var r = new Random(zufall);
            var ruhig = stil == FeierArt.NeuesAuto;
            var Farben = ruhig ? Ruhig : Bunt;
            float Z(float a, float b) => a + ((float)r.NextDouble() * (b - a));
            var teile = new List<Teil>();
            // ZWEI KNALLBONBONS an den unteren Ecken der Karte -- passend zu den zwei
            // Knallen im Ton (CelebrationSound), links zuerst.
            foreach (var (quelle, zuend) in new[] { (-1, 0.05f), (1, 0.11f) })
            {
                for (var i = 0; i < (ruhig ? 34 : 85); i++)
                {
                    var art = r.NextDouble() < 0.62 ? 0 : r.NextDouble() < 0.7 ? 1 : 2;
                    var zug = art == 1 ? Z(2.3f, 3.0f) : Z(3.2f, 4.2f);
                    var schwere = art == 1 ? Z(1150f, 1400f) : Z(820f, 980f);
                    teile.Add(new Teil(art, quelle, Farben[r.Next(Farben.Length)], 0f,
                        quelle * Z(120f, 680f), -Z(700f, 1300f), zug, schwere,
                        art == 2 ? Z(20f, 32f) : Z(9f, 15f), art == 2 ? 2.4f : Z(5f, 8f),
                        Z(0f, 360f), Z(-420f, 420f), Z(0f, 6.3f), Z(5f, 13f),
                        Z(6f, 22f), Z(2.2f, 4.5f), Z(0f, 6.3f), zuend + Z(0f, 0.08f), r.NextDouble() < 0.7));
                }
            }
            // UND EIN LEICHTER REGEN von oben, verteilt ueber gut eine Sekunde.
            for (var i = 0; i < (ruhig ? 55 : 90); i++)
            {
                var art = r.NextDouble() < 0.7 ? 0 : 1;
                teile.Add(new Teil(art, 0, Farben[r.Next(Farben.Length)], Z(0.03f, 0.97f),
                    Z(-40f, 40f), Z(40f, 160f), Z(3.3f, 3.9f), Z(640f, 760f),
                    Z(9f, 14f), Z(5f, 7.5f), Z(0f, 360f), Z(-300f, 300f), Z(0f, 6.3f), Z(4f, 10f),
                    Z(8f, 24f), Z(1.6f, 3.2f), Z(0f, 6.3f), Z(0.15f, 1.8f), false));
            }
            Teile = teile.ToArray();

            var funken = new List<Funke>();
            for (var i = 0; i < 18; i++)
            {
                // Am Rand der Karte, nicht auf dem Text.
                var seite = r.Next(4);
                var entlang = Z(-0.05f, 1.05f);
                var (x, y) = seite switch
                {
                    0 => (entlang, Z(-0.28f, -0.06f)),
                    1 => (entlang, Z(1.06f, 1.28f)),
                    2 => (Z(-0.1f, -0.03f), entlang),
                    _ => (Z(1.03f, 1.1f), entlang),
                };
                funken.Add(new Funke(x, y, Z(0.12f, 2.2f), Z(0.45f, 0.9f), Z(6f, 12f)));
            }
            Funken = funken.ToArray();
        }

        /// <summary>
        /// Wurf mit Luftwiderstand, geschlossen gerechnet: v(t) = v0 e^(-kt) + g/k (1 - e^(-kt)).
        /// Daraus die Lage -- steigt, bremst, sinkt mit Grenzgeschwindigkeit g/k und pendelt dabei.
        /// </summary>
        internal static PointF Lage(in Teil p, float t, PointF ursprung, float s)
        {
            var k = p.Zug;
            var gk = p.Schwere * s / k;
            var e = 1f - MathF.Exp(-k * t);
            var x = ursprung.X + (p.Vx * s / k * e);
            var y = ursprung.Y + (((p.Vy * s) - gk) / k * e) + (gk * t);
            x += p.PendelA * s * MathF.Sin((p.PendelW * t) + p.PendelP) * MathF.Min(1f, t / 0.6f);
            return new PointF(x, y);
        }

        internal static PointF Ursprung(in Teil p, RectangleF buehne, RectangleF kasten, float s) => p.Quelle switch
        {
            < 0 => new PointF(kasten.Left + (kasten.Width * 0.06f), kasten.Bottom - (4f * s)),
            > 0 => new PointF(kasten.Right - (kasten.Width * 0.06f), kasten.Bottom - (4f * s)),
            _ => new PointF(buehne.Left + (p.X0 * buehne.Width), buehne.Top - (12f * s)),
        };

        public void Male(Graphics g, RectangleF buehne, RectangleF kasten, float t, float ganz, float s, bool vorne)
        {
            var saum = 30f * s;
            var unten = buehne.Height * 0.24f;
            foreach (var p in Teile)
            {
                var eigen = t - p.Start;
                if (eigen <= 0f) { continue; }
                // VORNE NUR DER ERSTE AUSBRUCH aus den Ecken -- danach hinter der Karte,
                // sonst faellt ein Streifen quer ueber die Zeit und man liest sie nicht.
                if ((p.Vorne && eigen < 0.6f) != vorne) { continue; }
                var lage = Lage(p, eigen, Ursprung(p, buehne, kasten, s), s);
                // Zu den Raendern des Bandes hin ausblenden: ein hart abgeschnittenes
                // Teilchen saehe aus wie ein Fehler.
                var sicht = ganz * Klemme(eigen / 0.05)
                            * Klemme((lage.X - buehne.Left) / saum) * Klemme((buehne.Right - lage.X) / saum)
                            * Klemme((buehne.Bottom - lage.Y) / unten);
                if (sicht <= 0.01f) { continue; }
                var kipp = MathF.Cos(p.Kipp0 + (p.KippV * eigen));
                var farbe = kipp >= 0f ? p.Farbe
                    : Color.FromArgb((int)(p.Farbe.R * 0.62f), (int)(p.Farbe.G * 0.62f), (int)(p.Farbe.B * 0.62f));
                var zustand = g.Save();
                g.TranslateTransform(lage.X, lage.Y);
                g.RotateTransform(p.Dreh0 + (p.DrehV * eigen));
                if (p.Art == 2)
                {
                    using var stift = new Pen(A(farbe, sicht), Math.Max(1f, p.Hoehe * s)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    var l = p.Breite * s;
                    var welle = MathF.Sin((eigen * 9f) + p.PendelP) * 4f * s;
                    g.DrawBezier(stift, new PointF(-l / 2f, 0), new PointF(-l / 6f, -welle - (4f * s)),
                                 new PointF(l / 6f, welle + (4f * s)), new PointF(l / 2f, 0));
                }
                else
                {
                    g.ScaleTransform(1f, Math.Max(0.12f, Math.Abs(kipp)));
                    using var pinsel = new SolidBrush(A(farbe, sicht));
                    var w = p.Breite * s;
                    var h = p.Hoehe * s;
                    if (p.Art == 1) { g.FillEllipse(pinsel, -h / 2f, -h / 2f, h, h); }
                    else { g.FillRectangle(pinsel, -w / 2f, -h / 2f, w, h); }
                }
                g.Restore(zustand);
            }
        }
    }
}
