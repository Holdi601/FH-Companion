using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>Einheiten des Tachos -- gehoeren dem Nutzer, nicht dem Design.</summary>
internal readonly record struct SpeedoEinheiten(bool Mph, string Leistung)
{
    public static SpeedoEinheiten Aus(OverlaySettings s) => new(s.SpeedoMph, s.SpeedoPowerUnit ?? "hp");

    public float Tempo(float ms) => ms * (Mph ? 2.236936f : 3.6f);
    public string TempoEinheit => Mph ? "mph" : "km/h";

    public string LeistungText(float watt) => (Leistung ?? "hp").ToLowerInvariant() switch
    {
        "kw" => (watt / 1000f).ToString("0", CultureInfo.CurrentCulture) + " kW",
        "ps" => (watt / 735.499f).ToString("0", CultureInfo.CurrentCulture) + " PS",
        _ => (watt / 745.6999f).ToString("0", CultureInfo.CurrentCulture) + " hp",
    };

    public string StreckeText(float m)
    {
        var wert = Mph ? m / 1609.344f : m / 1000f;
        var einheit = Mph ? " mi" : " km";
        return (wert >= 1000f ? wert.ToString("n0", CultureInfo.CurrentCulture) : wert.ToString("0.0", CultureInfo.CurrentCulture)) + einheit;
    }
}

/// <summary>
/// ZEICHNET EINEN TACHO (seit 2026-10-18) -- im Spiel (<see cref="SpeedoHud"/>), in der Vorschau
/// und in der Galerie des Reiters: dieselbe Zeichnung, nie eine zweite.
/// </summary>
/// <remarks>
/// ## Zwei Schichten
///
/// Was sich nicht bewegt -- Rahmen, Platte, Skala, Ziffern der Skala, die dunklen Segmente --
/// wird einmal in ein Bild gezeichnet und je Bild nur noch kopiert. Je Bild neu gezeichnet wird
/// nur, was sich bewegt: die leuchtenden Segmente, die Nadel, die Zahlen, die Lampen. So kostet
/// ein Bild rund eine Millisekunde statt fuenf bis zehn (ein Bogen mit 70 Segmenten und drei
/// Leuchtschichten waren ueber 400 Zeichenaufrufe). Das Spiel geht vor.
/// </remarks>
internal sealed class SpeedoRenderer : IDisposable
{
    private Bitmap? _grund;
    private Bitmap? _fuellung;
    private Bitmap? _fuellungBlitz;
    private string _grundSchluessel = string.Empty;
    private SpeedoDesign? _designZuletzt;
    private int _designHash;
    private long _designGeprueft;

    private enum Schicht { Grund, Licht }

    // DIE LEUCHTENDE SKALA AUS DEM MERKER (seit 2026-10-18): einmal voll gezeichnet (und einmal
    // weiss, fuer das Blitzen am Begrenzer), je Bild nur der Ausschnitt bis zur Drehzahl kopiert --
    // ein DrawImage mit Ausschnitt statt dutzender Bogen- und Leuchtstriche. Gilt fuer den Faden,
    // der gerade zeichnet (Tacho und Oberflaeche zeichnen nebeneinander).
    [ThreadStatic] private static Bitmap? _fuellBild;
    [ThreadStatic] private static Bitmap? _blitzBild;
    [ThreadStatic] private static bool _blitzJetzt;
    [ThreadStatic] private static float _fuellW;
    [ThreadStatic] private static float _fuellH;
    /// <summary>Beim Bau des Merkers: nur die Skalen, voll; Text, Nadel, Lampen nicht.</summary>
    [ThreadStatic] private static bool _nurFuellung;
    [ThreadStatic] private static bool _blitzPass;

    public void Dispose()
    {
        _grund?.Dispose();
        _grund = null;
        _fuellung?.Dispose();
        _fuellung = null;
        _fuellungBlitz?.Dispose();
        _fuellungBlitz = null;
    }

    /// <summary>Das Design hat sich geaendert (an derselben Stelle): den Merker neu bauen.</summary>
    public void Vergessen()
    {
        _designZuletzt = null;
        _grundSchluessel = string.Empty;
    }

    /// <summary>
    /// Die Kennung des Designs -- je Objekt gemerkt und hoechstens zweimal je Sekunde neu gerechnet:
    /// das ganze Design als JSON in JEDEM Bild kostete mehr als das Zeichnen selbst.
    /// </summary>
    private int DesignHash(SpeedoDesign d)
    {
        var jetzt = Environment.TickCount64;
        if (!ReferenceEquals(d, _designZuletzt) || jetzt - _designGeprueft > 500)
        {
            _designHash = d.ToJson().GetHashCode();
            _designZuletzt = d;
            _designGeprueft = jetzt;
        }
        return _designHash;
    }

    /// <summary>Die Groesse des Tachos bei Skala <paramref name="k"/> (Bildpunkte).</summary>
    public static SizeF Groesse(SpeedoDesign d, float k) => new(d.Size * k, d.Size * d.Hochkant * k);

    /// <summary>Den Tacho in <paramref name="ziel"/> zeichnen (Seitenverhaeltnis des Designs).</summary>
    public void Male(Graphics g, SpeedoDesign d, SpeedoBild b, RectangleF ziel, SpeedoEinheiten e)
    {
        if (ziel.Width < 4f || ziel.Height < 4f) { return; }
        var w = (float)d.Size;
        var h = d.Size * d.Hochkant;
        var k = Math.Min(ziel.Width / w, ziel.Height / h);
        var platz = new RectangleF(ziel.X + (ziel.Width - w * k) / 2f, ziel.Y + (ziel.Height - h * k) / 2f, w * k, h * k);

        // 1. Der stehende Teil, aus dem Merker.
        var schluessel = string.Join("|", DesignHash(d), (int)platz.Width, (int)platz.Height,
            (int)(b.MaxRpm / 100f), (int)(b.RedlineRpm / 100f), e.Mph, SkalaTempo(b, e));
        if (_grund is null || schluessel != _grundSchluessel)
        {
            Bitmap Neu() => new(Math.Max(1, (int)Math.Ceiling(platz.Width)), Math.Max(1, (int)Math.Ceiling(platz.Height)), PixelFormat.Format32bppPArgb);
            _grund?.Dispose();
            _fuellung?.Dispose();
            _fuellungBlitz?.Dispose();
            _grund = Neu();
            _fuellung = Neu();
            _fuellungBlitz = Neu();
            using (var gg = Graphics.FromImage(_grund))
            {
                gg.Clear(Color.Transparent);
                Vorbereiten(gg);
                gg.ScaleTransform(k, k);
                Lage(gg, d, b, e, w, h, Schicht.Grund);
            }
            // Die volle Skala: Drehzahl am Anschlag, ohne Pulsieren -- einmal farbig, einmal weiss.
            var voll = b with { Rpm = Math.Max(1f, b.MaxRpm), AtLimiter = false, Uhr = 0 };
            foreach (var (bildZiel, weiss) in new[] { (_fuellung, false), (_fuellungBlitz, true) })
            {
                using var gg = Graphics.FromImage(bildZiel);
                gg.Clear(Color.Transparent);
                Vorbereiten(gg);
                gg.ScaleTransform(k, k);
                _nurFuellung = true;
                _blitzPass = weiss;
                try { Lage(gg, d, voll, e, w, h, Schicht.Licht); }
                finally { _nurFuellung = false; _blitzPass = false; }
            }
            _grundSchluessel = schluessel;
        }
        var alt = g.InterpolationMode;
        var mischen = g.CompositingQuality;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.CompositingQuality = CompositingQuality.HighSpeed;
        g.DrawImage(_grund, platz.X, platz.Y, _grund.Width, _grund.Height);
        g.InterpolationMode = alt;
        g.CompositingQuality = mischen;

        // 2. Was sich bewegt -- die Skala als Ausschnitt aus dem Merker.
        var zustand = g.Save();
        try
        {
            Vorbereiten(g, merker: false);
            g.TranslateTransform(platz.X, platz.Y);
            g.ScaleTransform(k, k);
            _fuellBild = _fuellung;
            _blitzBild = _fuellungBlitz;
            _fuellW = w;
            _fuellH = h;
            _blitzJetzt = Blitz(d, b);
            Lage(g, d, b, e, w, h, Schicht.Licht);
        }
        finally
        {
            _fuellBild = null;
            _blitzBild = null;
            g.Restore(zustand);
        }
    }

    /// <summary>Einmal zeichnen, ohne Merker -- fuer Galerie und Bilder.</summary>
    public static void Einmal(Graphics g, SpeedoDesign d, SpeedoBild b, RectangleF ziel, SpeedoEinheiten e)
    {
        using var r = new SpeedoRenderer();
        r.Male(g, d, b, ziel, e);
    }

    private static void Vorbereiten(Graphics g, bool merker = true)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        // Der Merker wird einmal gebaut: dort gammarichtig mischen. Je Bild NICHT -- das
        // gammarichtige Mischen machte jedes Kopieren des Merkers drei- bis viermal so teuer.
        g.PixelOffsetMode = merker ? PixelOffsetMode.HighQuality : PixelOffsetMode.Half;
        g.CompositingQuality = merker ? CompositingQuality.HighQuality : CompositingQuality.HighSpeed;
    }

    /// <summary>Die gemerkte volle Skala zeichnen, beschnitten auf <paramref name="ausschnitt"/>.</summary>
    private static bool AusDemMerker(Graphics g, GraphicsPath ausschnitt)
    {
        var bild = _blitzJetzt ? _blitzBild : _fuellBild;
        if (bild is null) { return false; }
        using var vorher = g.Clip;
        g.SetClip(ausschnitt, CombineMode.Intersect);
        var alt = g.InterpolationMode;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.DrawImage(bild, new RectangleF(0f, 0f, _fuellW, _fuellH));
        g.InterpolationMode = alt;
        g.Clip = vorher;
        return true;
    }

    private static bool AusDemMerker(Graphics g, RectangleF ausschnitt)
    {
        using var pfad = new GraphicsPath();
        pfad.AddRectangle(ausschnitt);
        return AusDemMerker(g, pfad);
    }

    /// <summary>Im Merker-Bau oder im Licht ohne Merker: direkt zeichnen.</summary>
    private static bool Direkt => _nurFuellung || _fuellBild is null;

    private static void Lage(Graphics g, SpeedoDesign d, SpeedoBild b, SpeedoEinheiten e, float w, float h, Schicht s)
    {
        switch (d.Layout)
        {
            case SpeedoLayout.Arc: Bogen(g, d, b, e, w, h, s); break;
            case SpeedoLayout.Bar: Balken(g, d, b, e, w, h, s); break;
            case SpeedoLayout.Vertical: Saeule(g, d, b, e, w, h, s); break;
            case SpeedoLayout.Twin: Doppel(g, d, b, e, w, h, s); break;
            case SpeedoLayout.Digital: Display(g, d, b, e, w, h, s); break;
            case SpeedoLayout.Moto: Motorrad(g, d, b, e, w, h, s); break;
            default: Rund(g, d, b, e, w, h, s); break;
        }
    }

    // ================================================================== //
    // Farben und Animation
    // ================================================================== //

    private static Color F(string? text, Color vorgabe) => SpeedoDesign.Farbe(text, vorgabe);

    private static Color Mische(Color a, Color c, float u)
    {
        u = Math.Clamp(u, 0f, 1f);
        return Color.FromArgb((int)(a.A + (c.A - a.A) * u), (int)(a.R + (c.R - a.R) * u),
                              (int)(a.G + (c.G - a.G) * u), (int)(a.B + (c.B - a.B) * u));
    }

    private static Color Alpha(Color c, float faktor) => Color.FromArgb(Math.Clamp((int)(c.A * faktor), 0, 255), c.R, c.G, c.B);

    /// <summary>Die Farbe eines leuchtenden Segments an der Stelle <paramref name="u"/> (0..1 der Skala).</summary>
    private static Color Segmentfarbe(SpeedoDesign d, SpeedoBild b, float u, float? rotAnteil = null)
    {
        var rot = rotAnteil ?? b.RotAnteil;
        if (u >= rot)
        {
            var c = F(d.RedlineColour, Color.Red);
            if (d.RedlinePulse && b.ImRoten && !_nurFuellung) { c = Alpha(c, 0.7f + 0.3f * (float)Math.Sin(b.Uhr * 14.0)); }
            return c;
        }
        var tief = F(d.LowColour, Color.DeepSkyBlue);
        return d.Gradient ? Mische(tief, F(d.HighColour, Color.White), u / Math.Max(0.01f, rot)) : tief;
    }

    /// <summary>Blinkt der Begrenzer gerade (an/aus mit 8 Hz)?</summary>
    private static bool Blitz(SpeedoDesign d, SpeedoBild b) =>
        _nurFuellung ? _blitzPass : d.LimiterFlash && b.AtLimiter && (int)(b.Uhr * 16.0) % 2 == 0;

    /// <summary>Wie stark das Leuchten gerade ist.</summary>
    private static float Leuchten(SpeedoDesign d, SpeedoBild b) =>
        d.Glow * (d.GlowFollowsRpm ? 0.35f + 0.65f * Math.Clamp(b.RpmAnteil, 0f, 1f) : 1f);

    /// <summary>Der Gang springt beim Wechsel kurz auf (0,25 s).</summary>
    private static float GangSprung(SpeedoDesign d, SpeedoBild b) =>
        d.GearPop && b.SeitGangwechsel < 0.25f ? 1f + 0.28f * (1f - b.SeitGangwechsel / 0.25f) : 1f;

    // ================================================================== //
    // Schrift
    // ================================================================== //

    private static readonly Dictionary<(string, int, FontStyle), Font> Schriften = new();
    private static readonly object SchriftSperre = new();
    private static string? _schmal;

    private static string Familie(SpeedoFont f) => f switch
    {
        SpeedoFont.Condensed => _schmal ??= Vorhanden("Bahnschrift SemiBold SemiConden", "Bahnschrift", "Arial Narrow", "Segoe UI Semibold"),
        SpeedoFont.Mono or SpeedoFont.Digital => "Consolas",
        _ => "Segoe UI Semibold",
    };

    private static string Vorhanden(params string[] namen)
    {
        using var alle = new InstalledFontCollection();
        var da = new HashSet<string>(alle.Families.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
        return namen.FirstOrDefault(da.Contains) ?? "Segoe UI";
    }

    private static Font Schrift(SpeedoDesign d, float px, bool fett = false)
    {
        var stil = (d.Italic ? FontStyle.Italic : FontStyle.Regular) | (fett ? FontStyle.Bold : FontStyle.Regular);
        var schluessel = (Familie(d.Font), (int)Math.Round(px * 2f), stil);
        lock (SchriftSperre)
        {
            if (!Schriften.TryGetValue(schluessel, out var f))
            {
                f = new Font(schluessel.Item1, Math.Max(1f, schluessel.Item2 / 2f), stil, GraphicsUnit.Pixel);
                Schriften[schluessel] = f;
            }
            return f;
        }
    }

    private static readonly StringFormat Mitte = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
    private static readonly StringFormat Links = new() { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
    private static readonly StringFormat Rechts = new() { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };

    /// <summary>Text in ein Feld -- bei Schrift "Digital" und Ziffern als Sieben-Segment-Anzeige.</summary>
    /// <summary>
    /// Text in ein Feld, an seinen TATSAECHLICHEN Umrissen ausgerichtet (seit 2026-10-18).
    /// </summary>
    /// <remarks>
    /// Die erste Fassung schrieb mit DrawString: GDI+ legt um jeden Text einen Rand und rechnet den
    /// Zeilenabstand der Schrift mit -- Ziffern standen dadurch je nach Schrift tiefer oder hoeher
    /// als die Mitte, Kursives lief rechts aus dem Feld, und schmale Felder schnitten ab (der Nutzer:
    /// "not centered or are clipping"). Jetzt wird der Text als Umriss gebaut: waagrecht nach seinen
    /// echten Grenzen gesetzt, senkrecht nach der Hoehe einer Ziffer (sonst huepfte "rpm" gegen
    /// "RPM"), und verkleinert, bis er in Breite UND Hoehe passt.
    /// </remarks>
    private static void Schreibe(Graphics g, SpeedoDesign d, string text, RectangleF r, float px, Color farbe,
                                 StringFormat? lage = null, bool gross = false, float leuchten = 0f)
    {
        if (_nurFuellung) { return; }
        if (string.IsNullOrEmpty(text) || r.Width < 1f || r.Height < 1f) { return; }
        lage ??= Mitte;
        if (gross && d.Font == SpeedoFont.Digital && SiebenSegment.Kann(text))
        {
            SiebenSegment.Male(g, text, r, px, farbe, Alpha(farbe, 0.05f), lage.Alignment, d.Italic, leuchten);
            return;
        }
        var stil = (d.Italic ? FontStyle.Italic : FontStyle.Regular) | (gross ? FontStyle.Bold : FontStyle.Regular);
        var familie = Familie(d.Font);
        var (oben, unten) = Ziffernhoehe(familie, stil);
        var (pfad, grenzen) = Umriss(text, familie, stil);
        if (grenzen.Width <= 0f) { return; }
        // Massstab: px ist die Schriftgroesse; kleiner, wenn Breite oder Ziffernhoehe nicht passt.
        var k = px / 100f;
        var zifferH = (unten - oben) * k;
        if (grenzen.Width * k > r.Width * 0.98f) { k = r.Width * 0.98f / grenzen.Width; }
        if (zifferH > r.Height * 0.98f && unten > oben) { k = Math.Min(k, r.Height * 0.98f / (unten - oben)); }
        var breite = grenzen.Width * k;
        var x = lage.Alignment switch
        {
            StringAlignment.Near => r.X,
            StringAlignment.Far => r.Right - breite,
            _ => r.X + (r.Width - breite) / 2f,
        } - grenzen.X * k;
        // Senkrecht: die Mitte der Ziffernhoehe auf die Mitte des Feldes.
        var y = r.Y + r.Height / 2f - (oben + unten) / 2f * k;
        var zustand = g.Save();
        try
        {
            g.TranslateTransform(x, y);
            g.ScaleTransform(k, k);
            if (leuchten > 0.05f)
            {
                for (var i = 3; i >= 1; i--)
                {
                    using var schein = new Pen(Alpha(farbe, 0.1f * leuchten), Math.Max(1f, px * 0.05f * i) / k) { LineJoin = LineJoin.Round };
                    g.DrawPath(schein, pfad);
                }
            }
            using var pinsel = new SolidBrush(farbe);
            g.FillPath(pinsel, pfad);
        }
        finally
        {
            g.Restore(zustand);
        }
    }

    // DIE UMRISSE DER TEXTE, gemerkt je Faden: "237", "km/h", "7,114 rpm" kommen Bild fuer Bild
    // wieder, und AddString war der teuerste Schritt eines Bildes.
    [ThreadStatic] private static Dictionary<(string, string, FontStyle), (GraphicsPath Pfad, RectangleF Grenzen)>? _umrisse;

    private static (GraphicsPath Pfad, RectangleF Grenzen) Umriss(string text, string familie, FontStyle stil)
    {
        _umrisse ??= new();
        if (_umrisse.TryGetValue((text, familie, stil), out var da)) { return da; }
        if (_umrisse.Count > 600)
        {
            foreach (var alt in _umrisse.Values) { alt.Pfad.Dispose(); }
            _umrisse.Clear();
        }
        // WINDING, nicht Alternate: kursive und fette Ziffern ("4") haben sich ueberlappende Umrisse,
        // und Alternate liess genau dort Loecher -- ein Schachbrett im Gang und im Tempo.
        var pfad = new GraphicsPath(FillMode.Winding);
        var ff = Familienobjekt(familie);
        pfad.AddString(text, ff, (int)(ff.IsStyleAvailable(stil) ? stil : FontStyle.Regular), 100f, PointF.Empty, StringFormat.GenericTypographic);
        var neu = (pfad, pfad.GetBounds());
        _umrisse[(text, familie, stil)] = neu;
        return neu;
    }

    private static readonly Dictionary<(string, FontStyle), (float Oben, float Unten)> Ziffern = new();
    private static readonly Dictionary<string, FontFamily> Familien = new();

    /// <summary>Eine Schriftfamilie, einmal angelegt -- je Bild entstuenden sonst Dutzende.</summary>
    private static FontFamily Familienobjekt(string name)
    {
        lock (SchriftSperre)
        {
            if (!Familien.TryGetValue(name, out var ff))
            {
                try { ff = new FontFamily(name); }
                catch (ArgumentException) { ff = new FontFamily("Segoe UI"); }
                Familien[name] = ff;
            }
            return ff;
        }
    }

    /// <summary>Oberkante und Grundlinie der Ziffer "0" bei Groesse 100 -- je Schrift einmal gemessen.</summary>
    private static (float Oben, float Unten) Ziffernhoehe(string familie, FontStyle stil)
    {
        lock (SchriftSperre)
        {
            if (Ziffern.TryGetValue((familie, stil), out var wert)) { return wert; }
            var ff = Familienobjekt(familie);
            using var pfad = new GraphicsPath();
            pfad.AddString("0", ff, (int)(ff.IsStyleAvailable(stil) ? stil : FontStyle.Regular), 100f, PointF.Empty, StringFormat.GenericTypographic);
            var b = pfad.GetBounds();
            wert = b.Height > 0f ? (b.Top, b.Bottom) : (20f, 90f);
            Ziffern[(familie, stil)] = wert;
            return wert;
        }
    }

    private static string GangText(int gang) => gang switch
    {
        0 => "R",
        > 10 => "N",
        _ => gang.ToString(CultureInfo.InvariantCulture),
    };

    private static string Uhrzeit(SpeedoDesign d, SpeedoBild b) => d.Clock switch
    {
        SpeedoClock.Real24 => DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture),
        SpeedoClock.Real12 => DateTime.Now.ToString("h:mm", CultureInfo.InvariantCulture) + (DateTime.Now.Hour < 12 ? " am" : " pm"),
        SpeedoClock.RaceTime => Laufzeit(b.RaceTime),
        SpeedoClock.LapTime => Laufzeit(b.LapTime),
        _ => string.Empty,
    };

    private static string Laufzeit(float s)
    {
        if (s <= 0f || float.IsNaN(s)) { return "0:00.0"; }
        var m = (int)(s / 60f);
        return m.ToString(CultureInfo.InvariantCulture) + ":" + (s - m * 60f).ToString("00.0", CultureInfo.InvariantCulture);
    }

    private static string Strecke(SpeedoDesign d, SpeedoBild b, SpeedoEinheiten e) => d.Distance switch
    {
        SpeedoDistance.Trip => e.StreckeText(b.TripM),
        SpeedoDistance.Race => e.StreckeText(Math.Max(0f, b.RaceM)),
        SpeedoDistance.Odometer => e.StreckeText(b.OdometerM),
        _ => string.Empty,
    };

    /// <summary>Leistung, Ladedruck, Uhr, Strecke -- was davon gewaehlt ist, in einer Zeile.</summary>
    private static List<string> Infos(SpeedoDesign d, SpeedoBild b, SpeedoEinheiten e, bool mitUhr = true, bool mitStrecke = true)
    {
        var raus = new List<string>();
        if (d.ShowPower) { raus.Add(e.LeistungText(b.PowerW)); }
        if (d.ShowBoost) { raus.Add(Math.Max(0f, b.BoostPsi).ToString("0.0", CultureInfo.CurrentCulture) + " psi"); }
        if (mitStrecke && d.Distance != SpeedoDistance.Off) { raus.Add(Strecke(d, b, e)); }
        if (mitUhr && d.Clock != SpeedoClock.Off) { raus.Add(Uhrzeit(d, b)); }
        return raus;
    }

    // ================================================================== //
    // Rahmen
    // ================================================================== //

    private static GraphicsPath Platte(SpeedoDesign d, RectangleF r, bool kreis)
    {
        var p = new GraphicsPath();
        if (kreis)
        {
            p.AddEllipse(r);
            return p;
        }
        if (d.Frame == SpeedoFrame.Angled)
        {
            var c = Math.Min(r.Height, r.Width) * 0.18f;
            p.AddPolygon(new[]
            {
                new PointF(r.X + c, r.Y), new PointF(r.Right, r.Y), new PointF(r.Right, r.Bottom - c),
                new PointF(r.Right - c, r.Bottom), new PointF(r.X, r.Bottom), new PointF(r.X, r.Y + c),
            });
            return p;
        }
        var rad = Math.Min(d.CornerRadius * 2f, Math.Min(r.Width, r.Height));
        if (rad < 1f)
        {
            p.AddRectangle(r);
            return p;
        }
        p.AddArc(r.X, r.Y, rad, rad, 180, 90);
        p.AddArc(r.Right - rad, r.Y, rad, rad, 270, 90);
        p.AddArc(r.Right - rad, r.Bottom - rad, rad, rad, 0, 90);
        p.AddArc(r.X, r.Bottom - rad, rad, rad, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Hintergrund und Rahmen; <paramref name="kreis"/> fuer runde Instrumente.</summary>
    private static void Rahmen(Graphics g, SpeedoDesign d, RectangleF r, bool kreis)
    {
        var grund = F(d.Background, Color.FromArgb(190, 12, 14, 18));
        var rahmen = F(d.FrameColour, Color.Gray);
        using var form = Platte(d, r, kreis);
        if (grund.A > 0)
        {
            using var pinsel = new SolidBrush(grund);
            g.FillPath(pinsel, form);
        }
        switch (d.Frame)
        {
            case SpeedoFrame.Bezel:
            {
                var dick = Math.Max(3f, r.Width * 0.025f);
                using var verlauf = new LinearGradientBrush(r, Mische(rahmen, Color.White, 0.45f), Mische(rahmen, Color.Black, 0.55f), 90f);
                using var stift = new Pen(verlauf, dick);
                var innen = RectangleF.Inflate(r, -dick / 2f, -dick / 2f);
                using var ring = Platte(d, innen, kreis);
                g.DrawPath(stift, ring);
                using var fein = new Pen(Alpha(Color.Black, 0.6f), 1f);
                using var innenRing = Platte(d, RectangleF.Inflate(r, -dick - 0.5f, -dick - 0.5f), kreis);
                g.DrawPath(fein, innenRing);
                break;
            }
            case SpeedoFrame.Glass:
            {
                using var stift = new Pen(rahmen, 1.5f);
                g.DrawPath(stift, form);
                var glanz = new RectangleF(r.X - r.Width * 0.1f, r.Y - r.Height * 0.55f, r.Width * 1.2f, r.Height * 0.9f);
                using var licht = new LinearGradientBrush(new RectangleF(r.X, r.Y, r.Width, r.Height * 0.36f),
                    Color.FromArgb(24, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f);
                using var oben = new GraphicsPath();
                oben.AddEllipse(glanz);
                var vorher = g.Clip;
                g.SetClip(form, CombineMode.Intersect);
                g.FillPath(licht, oben);
                g.Clip = vorher;
                break;
            }
            case SpeedoFrame.Outline:
            case SpeedoFrame.Angled:
            {
                if (d.Glow > 0.05f)
                {
                    for (var i = 3; i >= 1; i--)
                    {
                        using var schein = new Pen(Alpha(rahmen, 0.12f * d.Glow), 2f + i * 3f);
                        g.DrawPath(schein, form);
                    }
                }
                using var stift = new Pen(rahmen, 2f);
                g.DrawPath(stift, form);
                break;
            }
            case SpeedoFrame.Plate:
            {
                using var stift = new Pen(rahmen, 1.5f);
                g.DrawPath(stift, form);
                break;
            }
        }
    }

    // ================================================================== //
    // Runde Skala (Zeiger, Bogen, Doppel)
    // ================================================================== //

    private readonly record struct Kreis(PointF Mitte, float Radius, float Dicke, float Start, float Bogen);

    private static PointF Punkt(PointF c, float r, float grad)
    {
        var a = grad * Math.PI / 180.0;
        return new PointF(c.X + (float)(Math.Cos(a) * r), c.Y + (float)(Math.Sin(a) * r));
    }

    private static RectangleF Um(PointF c, float r) => new(c.X - r, c.Y - r, r * 2f, r * 2f);

    /// <summary>
    /// Ein Drehzahlbogen: im Grund die Spur, der rote Bereich und die Teilung, im Licht die Fuellung
    /// bis <paramref name="anteil"/>.
    /// </summary>
    private static void Bogenskala(Graphics g, SpeedoDesign d, SpeedoBild b, Kreis k, float anteil, float rotAnteil,
                                   Schicht s, int teilung, Func<int, string>? beschriftung, bool zahlenAussen = false)
    {
        var spur = F(d.TrackColour, Color.FromArgb(50, 255, 255, 255));
        var rot = F(d.RedlineColour, Color.Red);
        var rect = Um(k.Mitte, k.Radius);
        var n = d.Fill switch { SpeedoFill.Smooth => 48, _ => d.Segments };
        var luecke = d.Fill switch { SpeedoFill.Segments => 0.28f, SpeedoFill.Blocks => 0.45f, _ => 0f };
        var schritt = k.Bogen / n;

        if (s == Schicht.Grund)
        {
            // Spur und roter Bereich.
            if (d.Fill == SpeedoFill.Ticks)
            {
                using var stift = new Pen(spur, Math.Max(1.2f, k.Dicke * 0.22f));
                for (var i = 0; i < n; i++)
                {
                    var w = k.Start + schritt * (i + 0.5f);
                    g.DrawLine(stift, Punkt(k.Mitte, k.Radius - k.Dicke / 2f, w), Punkt(k.Mitte, k.Radius + k.Dicke / 2f, w));
                }
            }
            else if (spur.A > 0)
            {
                using var stift = new Pen(spur, k.Dicke) { StartCap = LineCap.Flat, EndCap = LineCap.Flat };
                if (luecke > 0f)
                {
                    for (var i = 0; i < n; i++) { g.DrawArc(stift, rect, k.Start + schritt * i, schritt * (1f - luecke)); }
                }
                else
                {
                    g.DrawArc(stift, rect, k.Start, k.Bogen);
                }
            }
            if (rotAnteil < 1f)
            {
                var r2 = Um(k.Mitte, k.Radius + k.Dicke / 2f + 3f);
                using var rotStift = new Pen(Alpha(rot, 0.85f), Math.Max(2f, k.Dicke * 0.28f));
                g.DrawArc(rotStift, r2, k.Start + k.Bogen * rotAnteil, k.Bogen * (1f - rotAnteil));
            }
            // Teilung: grosse Striche je Teil, kleine dazwischen, Ziffern.
            if (d.Ticks && teilung > 0)
            {
                var hell = F(d.TextColour, Color.White);
                var leise = F(d.DimColour, Color.Gray);
                using var gross = new Pen(hell, Math.Max(1.5f, k.Radius * 0.018f));
                using var klein = new Pen(leise, Math.Max(1f, k.Radius * 0.009f));
                using var grossRot = new Pen(rot, gross.Width);
                using var kleinRot = new Pen(Alpha(rot, 0.8f), klein.Width);
                var innen = k.Radius - k.Dicke / 2f - 3f;
                var teile = teilung * (d.MinorTicks + 1);
                for (var i = 0; i <= teile; i++)
                {
                    var u = (float)i / teile;
                    var w = k.Start + k.Bogen * u;
                    var istGross = i % (d.MinorTicks + 1) == 0;
                    var laenge = istGross ? k.Radius * 0.1f : k.Radius * 0.05f;
                    var stift = rotAnteil < 1f && u >= rotAnteil ? (istGross ? grossRot : kleinRot) : (istGross ? gross : klein);
                    g.DrawLine(stift, Punkt(k.Mitte, innen, w), Punkt(k.Mitte, innen - laenge, w));
                }
            }
            if (d.TickNumbers && teilung > 0 && beschriftung is not null)
            {
                var px = Math.Max(8f, k.Radius * 0.13f);
                var abstand = zahlenAussen ? k.Radius + k.Dicke / 2f + px * 0.85f : k.Radius - k.Dicke / 2f - k.Radius * 0.1f - px * 0.85f;
                for (var i = 0; i <= teilung; i++)
                {
                    var u = (float)i / teilung;
                    var text = beschriftung(i);
                    var ab = zahlenAussen ? abstand + px * 0.3f * (text.Length - 1) : abstand - px * 0.32f * (text.Length - 1);
                    var p = Punkt(k.Mitte, ab, k.Start + k.Bogen * u);
                    var farbe = rotAnteil < 1f && u >= rotAnteil ? rot : F(d.TextColour, Color.White);
                    Schreibe(g, d, text, new RectangleF(p.X - px * 1.6f, p.Y - px * 0.7f, px * 3.2f, px * 1.4f), px, farbe);
                }
            }
            return;
        }

        // LICHT: die Fuellung bis zum Anteil, dann das Leuchten.
        anteil = Math.Clamp(_nurFuellung ? 1f : anteil, 0f, 1f);
        if (anteil <= 0.001f) { return; }
        if (!Direkt)
        {
            // Bis zum Ende des letzten angefangenen Segments -- die Schnittkante liegt in der Luecke.
            var bisM = d.Fill == SpeedoFill.Smooth ? anteil : Math.Min(1f, (float)Math.Ceiling(anteil * n - 0.0001f) / n);
            var weit = k.Radius + k.Dicke / 2f + 3f * Math.Min(k.Dicke * 0.45f, 5f) + 3f;
            using (var torte = new GraphicsPath())
            {
                torte.AddPie(k.Mitte.X - weit, k.Mitte.Y - weit, weit * 2f, weit * 2f, k.Start, Math.Max(0.1f, k.Bogen * bisM));
                AusDemMerker(g, torte);
            }
            // Im roten Bereich pulsiert er: ein heller Schleier ueber dem roten Teil.
            if (d.RedlinePulse && anteil > rotAnteil && !_blitzJetzt)
            {
                var puls = 0.5f + 0.5f * (float)Math.Sin(b.Uhr * 14.0);
                using var schleier = new Pen(Alpha(Color.White, 0.22f * puls), k.Dicke) { StartCap = LineCap.Flat, EndCap = LineCap.Flat };
                g.DrawArc(schleier, rect, k.Start + k.Bogen * rotAnteil, k.Bogen * (bisM - rotAnteil));
            }
            return;
        }
        var blitz = Blitz(d, b);
        var leuchten = Leuchten(d, b);
        if (leuchten > 0.05f && d.Fill != SpeedoFill.Ticks)
        {
            // Je Stueck in seiner eigenen Farbe -- einmal, in den Merker.
            var stueckeL = _nurFuellung ? 24 : 1;
            for (var j = 0; j < stueckeL; j++)
            {
                var von = anteil * j / stueckeL;
                var bis = anteil * (j + 1) / stueckeL;
                var c = blitz ? Color.White : Segmentfarbe(d, b, _nurFuellung ? (von + bis) / 2f : anteil, rotAnteil);
                for (var i = 3; i >= 1; i--)
                {
                    using var schein = new Pen(Alpha(c, 0.09f * leuchten * (4 - i)), k.Dicke + i * Math.Min(k.Dicke * 0.45f, 5f));
                    g.DrawArc(schein, rect, k.Start + k.Bogen * von, k.Bogen * (bis - von) + (_nurFuellung ? 0.6f : 0f));
                }
            }
        }
        if (d.Fill == SpeedoFill.Ticks)
        {
            using var stift = new Pen(Color.White, Math.Max(1.6f, k.Dicke * 0.3f));
            var bis = (int)Math.Ceiling(anteil * n);
            for (var i = 0; i < bis; i++)
            {
                var u = (i + 0.5f) / n;
                stift.Color = blitz ? Color.White : Segmentfarbe(d, b, u, rotAnteil);
                var w = k.Start + schritt * (i + 0.5f);
                g.DrawLine(stift, Punkt(k.Mitte, k.Radius - k.Dicke / 2f, w), Punkt(k.Mitte, k.Radius + k.Dicke / 2f, w));
            }
            return;
        }
        if (luecke > 0f)
        {
            using var stift = new Pen(Color.White, k.Dicke) { StartCap = LineCap.Flat, EndCap = LineCap.Flat };
            var bis = (int)Math.Ceiling(anteil * n);
            for (var i = 0; i < bis; i++)
            {
                var u = (i + 0.5f) / n;
                stift.Color = blitz ? Color.White : Segmentfarbe(d, b, u, rotAnteil);
                g.DrawArc(stift, rect, k.Start + schritt * i, schritt * (1f - luecke));
            }
            return;
        }
        // Glatt: in Stuecken, damit der Verlauf der Skala folgt.
        var stuecke = Math.Max(1, (int)Math.Ceiling(anteil * 24f));
        using (var stift = new Pen(Color.White, k.Dicke) { StartCap = LineCap.Flat, EndCap = LineCap.Flat })
        {
            for (var i = 0; i < stuecke; i++)
            {
                var von = anteil * i / stuecke;
                var bis = anteil * (i + 1) / stuecke;
                stift.Color = blitz ? Color.White : Segmentfarbe(d, b, (von + bis) / 2f, rotAnteil);
                g.DrawArc(stift, rect, k.Start + k.Bogen * von, k.Bogen * (bis - von) + 0.6f);
            }
        }
    }

    /// <summary>Die Nadel auf einem runden Instrument.</summary>
    private static void Nadel(Graphics g, SpeedoDesign d, SpeedoBild b, Kreis k, float anteil)
    {
        if (_nurFuellung) { return; }
        if (d.Needle == SpeedoNeedle.None) { return; }
        var w = k.Start + k.Bogen * Math.Clamp(anteil, 0f, 1.02f);
        var farbe = F(d.NeedleColour, Color.Red);
        var spitze = Punkt(k.Mitte, k.Radius + k.Dicke * 0.3f, w);
        var leuchten = Leuchten(d, b);
        switch (d.Needle)
        {
            case SpeedoNeedle.Line:
            case SpeedoNeedle.Glow:
            {
                var fuss = Punkt(k.Mitte, k.Radius * 0.55f, w);
                if (d.Needle == SpeedoNeedle.Glow || leuchten > 0.05f)
                {
                    for (var i = 3; i >= 1; i--)
                    {
                        using var schein = new Pen(Alpha(farbe, 0.12f * Math.Max(0.5f, leuchten)), 2f + i * 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                        g.DrawLine(schein, fuss, spitze);
                    }
                }
                using var stift = new Pen(farbe, Math.Max(2f, k.Radius * 0.022f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(stift, fuss, spitze);
                break;
            }
            case SpeedoNeedle.Wedge:
            {
                var breit = k.Radius * 0.06f;
                var links = Punkt(k.Mitte, breit, w - 90f);
                var rechts = Punkt(k.Mitte, breit, w + 90f);
                using var pinsel = new SolidBrush(farbe);
                g.FillPolygon(pinsel, new[] { links, spitze, rechts });
                using var nabe = new SolidBrush(Mische(farbe, Color.Black, 0.55f));
                g.FillEllipse(nabe, Um(k.Mitte, breit * 1.4f));
                break;
            }
            default:
            {
                var schwanz = Punkt(k.Mitte, k.Radius * 0.18f, w + 180f);
                var breit = k.Radius * 0.028f;
                var a = Punkt(schwanz, breit, w - 90f);
                var c = Punkt(schwanz, breit, w + 90f);
                using var schatten = new SolidBrush(Color.FromArgb(90, 0, 0, 0));
                g.FillPolygon(schatten, new[] { Verschiebe(a, 2f), Verschiebe(spitze, 2f), Verschiebe(c, 2f) });
                using var pinsel = new SolidBrush(farbe);
                g.FillPolygon(pinsel, new[] { a, spitze, c });
                using var nabe = new LinearGradientBrush(Um(k.Mitte, k.Radius * 0.08f), Color.FromArgb(255, 90, 96, 104), Color.FromArgb(255, 20, 22, 26), 90f);
                g.FillEllipse(nabe, Um(k.Mitte, k.Radius * 0.08f));
                break;
            }
        }
    }

    private static PointF Verschiebe(PointF p, float um) => new(p.X + um, p.Y + um);

    // ================================================================== //
    // Gerade Skala (Balken, Saeule, Display)
    // ================================================================== //

    private static void Balkenskala(Graphics g, SpeedoDesign d, SpeedoBild b, RectangleF r, bool waagrecht, Schicht s)
    {
        var anteil = Math.Clamp(b.RpmAnteil, 0f, 1f);
        var rotAnteil = b.RotAnteil;
        var n = d.Fill == SpeedoFill.Smooth ? 1 : d.Segments;
        var luecke = d.Fill switch { SpeedoFill.Segments => 0.25f, SpeedoFill.Blocks => 0.4f, SpeedoFill.Ticks => 0.6f, _ => 0f };
        var laenge = waagrecht ? r.Width : r.Height;
        var stueck = laenge / n;

        RectangleF Teil(float von, float bis) => waagrecht
            ? new RectangleF(r.X + laenge * von, r.Y, laenge * (bis - von), r.Height)
            : new RectangleF(r.X, r.Bottom - laenge * bis, r.Width, laenge * (bis - von));

        if (s == Schicht.Grund)
        {
            using var spur = new SolidBrush(F(d.TrackColour, Color.FromArgb(50, 255, 255, 255)));
            for (var i = 0; i < n; i++)
            {
                var t = Teil((float)i / n, (i + 1f - luecke) / n);
                g.FillRectangle(spur, t);
            }
            if (rotAnteil < 1f)
            {
                using var rot = new SolidBrush(Alpha(F(d.RedlineColour, Color.Red), 0.8f));
                var z = Teil(rotAnteil, 1f);
                var marke = waagrecht
                    ? new RectangleF(z.X, r.Bottom + 2f, z.Width, Math.Max(2f, r.Height * 0.12f))
                    : new RectangleF(r.Right + 2f, z.Y, Math.Max(2f, r.Width * 0.12f), z.Height);
                g.FillRectangle(rot, marke);
            }
            return;
        }
        if (_nurFuellung) { anteil = 1f; }
        if (anteil <= 0.001f) { return; }
        if (!Direkt)
        {
            var bisM = n == 1 ? anteil : Math.Min(1f, (float)Math.Ceiling(anteil * n - 0.0001f) / n);
            var t = Teil(0f, bisM);
            var rand = 12f;
            AusDemMerker(g, waagrecht
                ? new RectangleF(t.X - rand, t.Y - rand, t.Width + rand, t.Height + rand * 2f)
                : new RectangleF(t.X - rand, t.Y, t.Width + rand * 2f, t.Height + rand));
            if (d.RedlinePulse && anteil > rotAnteil && !_blitzJetzt)
            {
                var puls = 0.5f + 0.5f * (float)Math.Sin(b.Uhr * 14.0);
                using var schleier = new SolidBrush(Alpha(Color.White, 0.2f * puls));
                g.FillRectangle(schleier, Teil(rotAnteil, bisM));
            }
            return;
        }
        var blitz = Blitz(d, b);
        var leuchten = Leuchten(d, b);
        if (leuchten > 0.05f)
        {
            var gesamt = Teil(0f, anteil);
            for (var i = 3; i >= 1; i--)
            {
                using var schein = new SolidBrush(Alpha(blitz ? Color.White : Segmentfarbe(d, b, anteil), 0.07f * leuchten * (4 - i)));
                g.FillRectangle(schein, RectangleF.Inflate(gesamt, i * 2.5f, i * 2.5f));
            }
        }
        if (n == 1)
        {
            var stuecke = Math.Max(1, (int)Math.Ceiling(anteil * 30f));
            for (var i = 0; i < stuecke; i++)
            {
                var von = anteil * i / stuecke;
                var bis = anteil * (i + 1) / stuecke;
                using var pinsel = new SolidBrush(blitz ? Color.White : Segmentfarbe(d, b, (von + bis) / 2f));
                var t = Teil(von, bis);
                g.FillRectangle(pinsel, waagrecht ? new RectangleF(t.X, t.Y, t.Width + 0.6f, t.Height) : new RectangleF(t.X, t.Y - 0.6f, t.Width, t.Height + 0.6f));
            }
            return;
        }
        var an = (int)Math.Ceiling(anteil * n);
        for (var i = 0; i < an; i++)
        {
            using var pinsel = new SolidBrush(blitz ? Color.White : Segmentfarbe(d, b, (i + 0.5f) / n));
            g.FillRectangle(pinsel, Teil((float)i / n, (i + 1f - luecke) / n));
        }
    }

    // ================================================================== //
    // Schaltlichter, Lampen, Pedale
    // ================================================================== //

    private static void Schaltlichter(Graphics g, SpeedoDesign d, SpeedoBild b, RectangleF r, Schicht s)
    {
        if (_nurFuellung) { return; }
        var n = d.ShiftLights;
        var abstand = r.Width / n;
        var durchmesser = Math.Min(abstand * 0.78f, r.Height);
        var blitz = Blitz(d, b) || b.AtLimiter && d.LimiterFlash && (int)(b.Uhr * 16.0) % 2 == 0;
        // Von "ab" bis zum roten Bereich gehen sie der Reihe nach an.
        var ab = d.ShiftLightsFrom * b.RotAnteil;
        var u = b.RotAnteil > ab ? (b.RpmAnteil - ab) / (b.RotAnteil - ab) : 0f;
        var an = (int)Math.Floor(Math.Clamp(u, 0f, 1f) * n + 0.0001f);
        for (var i = 0; i < n; i++)
        {
            var mitte = new PointF(r.X + abstand * (i + 0.5f), r.Y + r.Height / 2f);
            var kreis = Um(mitte, durchmesser / 2f);
            // Gruen, dann Gelb, dann Rot -- das letzte Drittel blinkt am Begrenzer blau.
            var stufe = (float)i / n;
            var farbe = stufe < 0.4f ? F(d.LowColour, Color.LimeGreen) : stufe < 0.75f ? F(d.HighColour, Color.Gold) : F(d.RedlineColour, Color.Red);
            if (s == Schicht.Grund)
            {
                using var aus = new SolidBrush(Alpha(farbe, 0.16f));
                g.FillEllipse(aus, kreis);
                continue;
            }
            var leuchtet = b.ImRoten ? true : i < an;
            if (!leuchtet) { continue; }
            if (b.ImRoten && blitz) { farbe = F(d.AccentColour, Color.DeepSkyBlue); }
            else if (b.ImRoten && (int)(b.Uhr * 10.0) % 2 == 0) { farbe = Alpha(farbe, 0.55f); }
            using var schein = new SolidBrush(Alpha(farbe, 0.28f));
            g.FillEllipse(schein, RectangleF.Inflate(kreis, durchmesser * 0.25f, durchmesser * 0.25f));
            using var pinsel = new SolidBrush(farbe);
            g.FillEllipse(pinsel, kreis);
        }
    }

    /// <summary>Die Lampen TC (dreht durch) und ABS (blockiert) nebeneinander in <paramref name="r"/>.</summary>
    private static readonly Color Gelb = Color.FromArgb(255, 251, 191, 36);
    private static readonly Color Orange = Color.FromArgb(255, 251, 146, 60);
    private static readonly Color Rot = Color.FromArgb(255, 239, 68, 68);

    /// <summary>TC, ABS und (wenn gewaehlt) STAB nebeneinander in <paramref name="r"/>.</summary>
    private static void Lampen(Graphics g, SpeedoDesign d, SpeedoBild b, RectangleF r, Schicht s)
    {
        if (_nurFuellung) { return; }
        var liste = new List<Action<RectangleF>>();
        if (d.ShowTraction)
        {
            liste.Add(f => Lampe(g, d, f, d.TractionText, b.Wheelspin, Gelb, s));
            liste.Add(f => Lampe(g, d, f, d.LockText, b.WheelLock, Orange, s));
        }
        if (d.ShowStability) { liste.Add(f => StabLampe(g, d, b, f, s)); }
        if (liste.Count == 0) { return; }
        var luecke = r.Height * 0.3f;
        var breite = (r.Width - luecke * (liste.Count - 1)) / liste.Count;
        for (var i = 0; i < liste.Count; i++) { liste[i](new RectangleF(r.X + i * (breite + luecke), r.Y, breite, r.Height)); }
    }

    /// <summary>Die Stabilitaetslampe: gelb an der Grenze, rot und blinkend, wenn das Auto rutscht.</summary>
    private static void StabLampe(Graphics g, SpeedoDesign d, SpeedoBild b, RectangleF r, Schicht s)
    {
        var an = b.Stab > 0 && !(b.Stab == 2 && (int)(b.Uhr * 8.0) % 2 == 1);
        var farbe = b.Stab == 2 ? Rot : Gelb;
        if (!string.IsNullOrWhiteSpace(d.StabilityText))
        {
            Lampe(g, d, r, d.StabilityText, an, farbe, s);
            return;
        }
        Lampe(g, d, r, string.Empty, an, farbe, s);
        var c = s == Schicht.Grund ? Alpha(F(d.DimColour, Color.Gray), 0.55f) : Color.FromArgb(255, 20, 16, 8);
        if (s == Schicht.Licht && !an) { return; }
        Schleudern(g, RectangleF.Inflate(r, -r.Height * 0.12f, -r.Height * 0.12f), c);
    }

    /// <summary>
    /// Das Zeichen der Stabilitaetskontrolle, wie im Auto: ein Wagen von hinten, darunter zwei
    /// S-foermige Schleuderspuren. Breit gezeichnet -- in 12 Bildpunkten Hoehe muss es lesbar sein.
    /// </summary>
    private static void Schleudern(Graphics g, RectangleF r, Color c)
    {
        var h = r.Height;
        var w = Math.Min(r.Width, h * 1.7f);
        var x = r.X + (r.Width - w) / 2f;
        using var pinsel = new SolidBrush(c);
        // Der Wagen: Dach (Trapez) auf einem breiten Koerper, oben 55 % der Hoehe.
        var koerperOben = r.Y + h * 0.22f;
        var koerperUnten = r.Y + h * 0.52f;
        using (var wagen = new GraphicsPath(FillMode.Winding))
        {
            wagen.AddPolygon(new[]
            {
                new PointF(x + w * 0.3f, r.Y), new PointF(x + w * 0.7f, r.Y),
                new PointF(x + w * 0.82f, koerperOben), new PointF(x + w * 0.18f, koerperOben),
            });
            var radius = h * 0.08f;
            var k = new RectangleF(x + w * 0.08f, koerperOben, w * 0.84f, koerperUnten - koerperOben);
            wagen.AddArc(k.X, k.Y, radius * 2f, radius * 2f, 180, 90);
            wagen.AddArc(k.Right - radius * 2f, k.Y, radius * 2f, radius * 2f, 270, 90);
            wagen.AddArc(k.Right - radius * 2f, k.Bottom - radius * 2f, radius * 2f, radius * 2f, 0, 90);
            wagen.AddArc(k.X, k.Bottom - radius * 2f, radius * 2f, radius * 2f, 90, 90);
            wagen.CloseFigure();
            // Die Raeder unter dem Koerper.
            wagen.AddRectangle(new RectangleF(x + w * 0.14f, koerperUnten - h * 0.02f, w * 0.14f, h * 0.12f));
            wagen.AddRectangle(new RectangleF(x + w * 0.72f, koerperUnten - h * 0.02f, w * 0.14f, h * 0.12f));
            g.FillPath(pinsel, wagen);
        }
        // Zwei Schleuderspuren, je ein "S".
        using var stift = new Pen(c, Math.Max(1.2f, h * 0.1f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        foreach (var mitte in new[] { 0.28f, 0.72f })
        {
            var sx = x + w * mitte;
            var y0 = r.Y + h * 0.7f;
            g.DrawBezier(stift, new PointF(sx - w * 0.1f, y0), new PointF(sx + w * 0.14f, y0 + h * 0.06f),
                         new PointF(sx - w * 0.14f, r.Bottom - h * 0.06f), new PointF(sx + w * 0.1f, r.Bottom));
        }
    }

    private static void Lampe(Graphics g, SpeedoDesign d, RectangleF r, string text, bool an, Color farbe, Schicht s)
    {
        using var form = Platte(new SpeedoDesign { Frame = SpeedoFrame.Plate, CornerRadius = r.Height * 0.3f }, r, kreis: false);
        if (s == Schicht.Grund)
        {
            using var rand = new Pen(Alpha(F(d.DimColour, Color.Gray), 0.55f), 1.2f);
            g.DrawPath(rand, form);
            Schreibe(g, d, text, r, r.Height * 0.62f, Alpha(F(d.DimColour, Color.Gray), 0.55f));
            return;
        }
        if (!an) { return; }
        using (var hof = Platte(new SpeedoDesign { Frame = SpeedoFrame.Plate, CornerRadius = r.Height * 0.45f }, RectangleF.Inflate(r, 3f, 3f), kreis: false))
        using (var schein = new SolidBrush(Alpha(farbe, 0.3f)))
        {
            g.FillPath(schein, hof);
        }
        using var pinsel = new SolidBrush(farbe);
        g.FillPath(pinsel, form);
        Schreibe(g, d, text, r, r.Height * 0.62f, Color.FromArgb(255, 20, 16, 8));
    }

    private static void Pedale(Graphics g, SpeedoDesign d, SpeedoBild b, RectangleF r, bool waagrecht)
    {
        if (_nurFuellung) { return; }
        if (!d.ShowPedals) { return; }
        var gas = Color.FromArgb(230, 74, 222, 128);
        var bremse = Color.FromArgb(230, 248, 113, 113);
        using var spur = new SolidBrush(F(d.TrackColour, Color.FromArgb(40, 255, 255, 255)));
        if (waagrecht)
        {
            var h = (r.Height - 2f) / 2f;
            var oben = new RectangleF(r.X, r.Y, r.Width, h);
            var unten = new RectangleF(r.X, r.Y + h + 2f, r.Width, h);
            g.FillRectangle(spur, oben);
            g.FillRectangle(spur, unten);
            using var pg = new SolidBrush(gas);
            using var pb = new SolidBrush(bremse);
            g.FillRectangle(pg, oben.X, oben.Y, oben.Width * Math.Clamp(b.Throttle, 0f, 1f), oben.Height);
            g.FillRectangle(pb, unten.X, unten.Y, unten.Width * Math.Clamp(b.Brake, 0f, 1f), unten.Height);
            return;
        }
        var w = (r.Width - 2f) / 2f;
        var l = new RectangleF(r.X, r.Y, w, r.Height);
        var rr = new RectangleF(r.X + w + 2f, r.Y, w, r.Height);
        g.FillRectangle(spur, l);
        g.FillRectangle(spur, rr);
        using var p1 = new SolidBrush(bremse);
        using var p2 = new SolidBrush(gas);
        var hb = r.Height * Math.Clamp(b.Brake, 0f, 1f);
        var hg = r.Height * Math.Clamp(b.Throttle, 0f, 1f);
        g.FillRectangle(p1, l.X, l.Bottom - hb, l.Width, hb);
        g.FillRectangle(p2, rr.X, rr.Bottom - hg, rr.Width, hg);
    }

    /// <summary>Der Gang in einem Feld, mit kurzem Aufspringen beim Wechsel.</summary>
    private static void Gang(Graphics g, SpeedoDesign d, SpeedoBild b, RectangleF r, float px, bool kasten, Schicht s)
    {
        if (_nurFuellung) { return; }
        if (!d.ShowGear) { return; }
        if (s == Schicht.Grund)
        {
            if (kasten)
            {
                using var form = Platte(new SpeedoDesign { Frame = d.Frame == SpeedoFrame.Angled ? SpeedoFrame.Angled : SpeedoFrame.Plate, CornerRadius = r.Height * 0.18f }, r, kreis: false);
                using var rand = new Pen(Alpha(F(d.DimColour, Color.Gray), 0.7f), 1.5f);
                g.DrawPath(rand, form);
            }
            return;
        }
        var sprung = GangSprung(d, b);
        var farbe = b.ImRoten ? F(d.RedlineColour, Color.Red) : F(d.TextColour, Color.White);
        var zentrum = new PointF(r.X + r.Width / 2f, r.Y + r.Height / 2f);
        var feld = new RectangleF(zentrum.X - r.Width * sprung / 2f, zentrum.Y - r.Height * sprung / 2f, r.Width * sprung, r.Height * sprung);
        Schreibe(g, d, GangText(b.Gear), feld, px * d.GearSize * sprung, farbe, gross: true, leuchten: Leuchten(d, b));
        Schaltrat(g, d, b, r);
    }

    /// <summary>
    /// Die Schaltempfehlung am Gang: ein Pfeil rechts oben (gruen hoch, orange runter, er blinkt
    /// leicht), und klein unten rechts der beste Gang fuer dieses Tempo, wenn es nicht dieser ist.
    /// </summary>
    private static void Schaltrat(Graphics g, SpeedoDesign d, SpeedoBild b, RectangleF r)
    {
        // Ein kleines Gangfeld (runde Instrumente): Pfeil darueber, bester Gang daneben -- darin
        // waeren beide unlesbar klein.
        var klein = Math.Min(r.Width, r.Height) < 60f;
        var groesse = klein ? Math.Max(10f, Math.Min(r.Width, r.Height) * 0.42f) : Math.Min(r.Width, r.Height) * 0.3f;
        if (d.ShowShiftAdvice && b.Rat != 0)
        {
            var hoch = b.Rat > 0;
            var hell = (int)(b.Uhr * 6.0) % 2 == 0 ? 1f : 0.7f;
            var farbe = Alpha(hoch ? Color.FromArgb(255, 74, 222, 128) : Color.FromArgb(255, 251, 146, 60), hell);
            // Rund- und Doppelinstrument: darueber liegen die Lampen -- dort links daneben.
            var links = klein && d.Layout is SpeedoLayout.Dial or SpeedoLayout.Twin;
            var x = links ? r.X - groesse * 0.75f : klein ? r.X + r.Width / 2f : r.Right - groesse * 0.65f;
            var y = links ? r.Y + r.Height / 2f : klein ? r.Y - groesse * 0.65f : r.Y + groesse * 0.7f;
            var spitze = hoch ? y - groesse * 0.5f : y + groesse * 0.5f;
            var fuss = hoch ? y + groesse * 0.35f : y - groesse * 0.35f;
            using var schein = new SolidBrush(Alpha(farbe, 0.3f));
            using var pinsel = new SolidBrush(farbe);
            var pfeil = new[] { new PointF(x, spitze), new PointF(x + groesse * 0.5f, fuss), new PointF(x - groesse * 0.5f, fuss) };
            g.FillEllipse(schein, x - groesse * 0.7f, y - groesse * 0.7f, groesse * 1.4f, groesse * 1.4f);
            g.FillPolygon(pinsel, pfeil);
        }
        if (d.ShowBestGear && b.BesterGang > 0 && b.BesterGang != b.Gear && b.Gear is >= 1 and <= 10)
        {
            var feld = klein
                ? new RectangleF(r.Right + groesse * 0.15f, r.Y + r.Height / 2f - groesse * 0.5f, groesse * 1.6f, groesse)
                : new RectangleF(r.Right - groesse * 1.4f, r.Bottom - groesse * 1.1f, groesse * 1.3f, groesse);
            Schreibe(g, d, "→" + b.BesterGang.ToString(CultureInfo.InvariantCulture), feld, groesse * 0.9f,
                     b.BesterGang > b.Gear ? Color.FromArgb(255, 74, 222, 128) : Color.FromArgb(255, 251, 146, 60), klein ? Links : Rechts);
        }
    }

    // ================================================================== //
    // Die Layouts
    // ================================================================== //

    private static void Rund(Graphics g, SpeedoDesign d, SpeedoBild b, SpeedoEinheiten e, float w, float h, Schicht s)
    {
        // VON OBEN NACH UNTEN: "x1000 rpm", Schaltlichter, Drehzahl, Pedale, NABE, Tempo, Einheit,
        // Infozeile, Lampen, Gang. Die Nadel dreht um die Nabe und kreuzt so keine Zahl -- in der
        // ersten Fassung lief sie mitten durch das Tempo.
        var rand = 4f;
        var aussen = new RectangleF(rand, rand, w - rand * 2f, h - rand * 2f);
        var c = new PointF(w / 2f, h / 2f);
        var radius = w / 2f - rand - w * 0.075f - d.ArcThickness / 2f;
        var tausender = Math.Max(1, (int)Math.Ceiling(b.MaxRpm / 1000f));
        var k = new Kreis(c, radius, d.ArcThickness, d.StartAngle, d.SweepAngle);
        var voll = Math.Max(1f, tausender * 1000f);
        if (s == Schicht.Grund) { Rahmen(g, d, aussen, kreis: true); }
        Bogenskala(g, d, b, k, b.Rpm / voll, b.RedlineRpm / voll, s, tausender, i => i.ToString(CultureInfo.InvariantCulture));
        var leise = F(d.DimColour, Color.Gray);
        var hell = F(d.TextColour, Color.White);
        var lampen = new RectangleF(c.X - w * 0.17f, c.Y + w * 0.305f, w * 0.34f, w * 0.05f);
        var gang = new RectangleF(c.X - w * 0.065f, c.Y + w * 0.365f, w * 0.13f, w * 0.095f);
        var schalt = new RectangleF(c.X - w * 0.2f, c.Y - w * 0.215f, w * 0.4f, w * 0.03f);
        if (s == Schicht.Grund)
        {
            if (d.TickNumbers) { Schreibe(g, d, "x1000 rpm", new RectangleF(c.X - w * 0.15f, c.Y - w * 0.085f, w * 0.3f, w * 0.04f), w * 0.03f, leise); }
            if (d.ShowSpeed && d.ShowUnit) { Schreibe(g, d, e.TempoEinheit, new RectangleF(c.X - w * 0.15f, c.Y + w * 0.205f, w * 0.3f, w * 0.045f), w * 0.04f, leise); }
            Lampen(g, d, b, lampen, s);
            Gang(g, d, b, gang, w * 0.08f, kasten: true, s);
            if (d.ShowShiftLights) { Schaltlichter(g, d, b, schalt, s); }
            return;
        }
        if (d.ShowRpm)
        {
            Schreibe(g, d, b.Rpm.ToString("n0", CultureInfo.CurrentCulture) + " rpm", new RectangleF(c.X - w * 0.2f, c.Y - w * 0.175f, w * 0.4f, w * 0.05f), w * 0.04f, leise);
        }
        Pedale(g, d, b, new RectangleF(c.X - w * 0.11f, c.Y - w * 0.13f, w * 0.22f, w * 0.022f), waagrecht: true);
        if (d.ShowSpeed)
        {
            Schreibe(g, d, e.Tempo(b.SpeedMs).ToString("0", CultureInfo.InvariantCulture),
                     new RectangleF(c.X - w * 0.24f, c.Y + w * 0.06f, w * 0.48f, w * 0.15f), w * 0.13f * d.SpeedSize, hell, gross: true, leuchten: Leuchten(d, b) * 0.6f);
        }
        var infos = Infos(d, b, e);
        if (infos.Count > 0)
        {
            // Ein weiter Bogen (ueber 280 Grad) hat seine Endziffern auf dieser Hoehe.
            var breit = d.SweepAngle > 280f ? w * 0.3f : w * 0.48f;
            Schreibe(g, d, string.Join("  ·  ", infos), new RectangleF(c.X - breit / 2f, c.Y + w * 0.25f, breit, w * 0.05f), w * 0.036f, leise);
        }
        Lampen(g, d, b, lampen, s);
        Gang(g, d, b, gang, w * 0.08f, kasten: true, s);
        if (d.ShowShiftLights) { Schaltlichter(g, d, b, schalt, s); }
        Nadel(g, d, b, k, b.Rpm / voll);
    }

    private static void Bogen(Graphics g, SpeedoDesign d, SpeedoBild b, SpeedoEinheiten e, float w, float h, Schicht s)
    {
        var c = new PointF(w * 0.5f, h * 0.56f);
        var radius = Math.Min(w * 0.4f, h * 0.48f) - d.ArcThickness / 2f;
        var k = new Kreis(c, radius, d.ArcThickness, d.StartAngle, d.SweepAngle);
        var tausender = Math.Max(1, (int)Math.Ceiling(b.MaxRpm / 1000f));
        if (s == Schicht.Grund && d.Frame != SpeedoFrame.None)
        {
            Rahmen(g, d, new RectangleF(2f, 2f, w - 4f, h - 4f), kreis: false);
        }
        var skala = k with { Bogen = d.SweepAngle * Math.Min(1f, tausender * 1000f / Math.Max(1f, b.MaxRpm)) };
        Bogenskala(g, d, b, skala, b.Rpm / Math.Max(1f, tausender * 1000f), b.RedlineRpm / Math.Max(1f, tausender * 1000f), s,
                   tausender, i => i.ToString(CultureInfo.InvariantCulture), zahlenAussen: false);
        var hell = F(d.TextColour, Color.White);
        var leise = F(d.DimColour, Color.Gray);
        var gangFeld = new RectangleF(c.X + radius * 0.2f, c.Y + radius * 0.46f, radius * 0.36f, radius * 0.36f);
        if (s == Schicht.Grund)
        {
            if (d.ShowSpeed && d.ShowUnit) { Schreibe(g, d, e.TempoEinheit.ToUpperInvariant(), new RectangleF(c.X - radius * 0.5f, c.Y + radius * 0.2f, radius, radius * 0.16f), radius * 0.12f, leise); }
            if (d.ShowGear)
            {
                using var ring = new Pen(Alpha(F(d.TextColour, Color.White), 0.8f), Math.Max(1.5f, radius * 0.025f));
                g.DrawEllipse(ring, gangFeld);
            }
            Lampen(g, d, b, new RectangleF(c.X - radius * 0.62f, c.Y + radius * 0.56f, radius * 0.72f, radius * 0.14f), s);
            if (d.ShowShiftLights) { Schaltlichter(g, d, b, new RectangleF(c.X - radius * 0.55f, c.Y - radius * 0.5f, radius * 1.1f, radius * 0.08f), s); }
            return;
        }
        if (d.ShowSpeed)
        {
            Schreibe(g, d, e.Tempo(b.SpeedMs).ToString("0", CultureInfo.InvariantCulture),
                     new RectangleF(c.X - radius * 0.8f, c.Y - radius * 0.38f, radius * 1.6f, radius * 0.62f), radius * 0.48f * d.SpeedSize, hell, gross: true, leuchten: Leuchten(d, b) * 0.5f);
        }
        if (d.ShowRpm)
        {
            Schreibe(g, d, b.Rpm.ToString("n0", CultureInfo.CurrentCulture) + " rpm", new RectangleF(c.X - radius * 0.6f, c.Y - radius * 0.56f, radius * 1.2f, radius * 0.16f), radius * 0.11f, leise);
        }
        if (d.ShowGear)
        {
            Gang(g, d, b, gangFeld, radius * 0.3f, kasten: false, s);
        }
        var infos = Infos(d, b, e);
        if (infos.Count > 0)
        {
            Schreibe(g, d, string.Join("  ·  ", infos), new RectangleF(c.X - radius * 0.9f, c.Y + radius * 0.88f, radius * 1.8f, radius * 0.13f), radius * 0.1f, leise);
        }
        Pedale(g, d, b, new RectangleF(c.X - radius * 0.62f, c.Y + radius * 0.76f, radius * 0.72f, radius * 0.05f), waagrecht: true);
        Lampen(g, d, b, new RectangleF(c.X - radius * 0.62f, c.Y + radius * 0.56f, radius * 0.72f, radius * 0.14f), s);
        if (d.ShowShiftLights) { Schaltlichter(g, d, b, new RectangleF(c.X - radius * 0.55f, c.Y - radius * 0.5f, radius * 1.1f, radius * 0.08f), s); }
        Nadel(g, d, b, skala, b.Rpm / Math.Max(1f, tausender * 1000f));
    }

    private static void Balken(Graphics g, SpeedoDesign d, SpeedoBild b, SpeedoEinheiten e, float w, float h, Schicht s)
    {
        var rand = h * 0.08f;
        var innen = new RectangleF(rand, rand, w - rand * 2f, h - rand * 2f);
        if (s == Schicht.Grund && d.Frame != SpeedoFrame.None) { Rahmen(g, d, new RectangleF(1f, 1f, w - 2f, h - 2f), kreis: false); }
        var oben = innen.Y;
        if (d.ShowShiftLights)
        {
            Schaltlichter(g, d, b, new RectangleF(innen.X + innen.Width * 0.1f, oben, innen.Width * 0.8f, innen.Height * 0.14f), s);
            oben += innen.Height * 0.2f;
        }
        var dicke = Math.Max(3f, Math.Min(innen.Height * 0.2f, d.ArcThickness * 1.6f));
        Balkenskala(g, d, b, new RectangleF(innen.X, oben, innen.Width, dicke), waagrecht: true, s);
        var unten = oben + dicke + innen.Height * 0.08f;
        var rest = innen.Bottom - unten;
        var hell = F(d.TextColour, Color.White);
        var leise = F(d.DimColour, Color.Gray);
        var tempoFeld = new RectangleF(innen.X, unten, innen.Width * 0.42f, rest);
        var gangFeld = new RectangleF(innen.Right - rest * 0.95f, unten, rest * 0.95f, rest);
        var mitte = new RectangleF(tempoFeld.Right, unten, gangFeld.X - tempoFeld.Right - 4f, rest);
        if (s == Schicht.Grund)
        {
            Gang(g, d, b, gangFeld, rest * 0.75f, kasten: true, s);
            Lampen(g, d, b, new RectangleF(mitte.X + mitte.Width * 0.1f, mitte.Bottom - rest * 0.32f, mitte.Width * 0.8f, rest * 0.28f), s);
            return;
        }
        if (d.ShowSpeed)
        {
            var zahl = e.Tempo(b.SpeedMs).ToString("0", CultureInfo.InvariantCulture);
            Schreibe(g, d, zahl, new RectangleF(tempoFeld.X, tempoFeld.Y, tempoFeld.Width * 0.74f, tempoFeld.Height), rest * 0.82f * d.SpeedSize, hell, Rechts, gross: true, leuchten: Leuchten(d, b) * 0.5f);
            if (d.ShowUnit) { Schreibe(g, d, e.TempoEinheit, new RectangleF(tempoFeld.X + tempoFeld.Width * 0.76f, tempoFeld.Y + rest * 0.35f, tempoFeld.Width * 0.24f, rest * 0.5f), rest * 0.24f, leise, Links); }
        }
        var zeilen = new List<string>();
        if (d.ShowRpm) { zeilen.Add(b.Rpm.ToString("n0", CultureInfo.CurrentCulture) + " rpm"); }
        zeilen.AddRange(Infos(d, b, e));
        if (zeilen.Count > 0)
        {
            var obenTeil = d.ShowTraction ? mitte.Height * 0.62f : mitte.Height;
            Schreibe(g, d, string.Join("  ·  ", zeilen), new RectangleF(mitte.X, mitte.Y, mitte.Width, obenTeil * 0.55f), rest * 0.2f, leise);
            Pedale(g, d, b, new RectangleF(mitte.X + mitte.Width * 0.15f, mitte.Y + obenTeil * 0.62f, mitte.Width * 0.7f, rest * 0.12f), waagrecht: true);
        }
        Lampen(g, d, b, new RectangleF(mitte.X + mitte.Width * 0.1f, mitte.Bottom - rest * 0.32f, mitte.Width * 0.8f, rest * 0.28f), s);
        Gang(g, d, b, gangFeld, rest * 0.75f, kasten: true, s);
    }

    private static void Saeule(Graphics g, SpeedoDesign d, SpeedoBild b, SpeedoEinheiten e, float w, float h, Schicht s)
    {
        if (s == Schicht.Grund && d.Frame != SpeedoFrame.None) { Rahmen(g, d, new RectangleF(1f, 1f, w - 2f, h - 2f), kreis: false); }
        var rand = w * 0.1f;
        var spalte = new RectangleF(rand, h * 0.06f, w * 0.26f, h * 0.88f);
        Balkenskala(g, d, b, spalte, waagrecht: false, s);
        var rechts = new RectangleF(spalte.Right + rand * 0.8f, h * 0.06f, w - spalte.Right - rand * 1.6f, h * 0.88f);
        var hell = F(d.TextColour, Color.White);
        var leise = F(d.DimColour, Color.Gray);
        var gangFeld = new RectangleF(rechts.X, rechts.Y, rechts.Width, rechts.Width * 1.05f);
        if (s == Schicht.Grund)
        {
            Gang(g, d, b, gangFeld, rechts.Width * 0.8f, kasten: true, s);
            if (d.ShowSpeed && d.ShowUnit) { Schreibe(g, d, e.TempoEinheit, new RectangleF(rechts.X, rechts.Y + rechts.Height * 0.53f, rechts.Width, rechts.Width * 0.22f), rechts.Width * 0.2f, leise); }
            SaeulenLampen(g, d, b, rechts, s);
            return;
        }
        Gang(g, d, b, gangFeld, rechts.Width * 0.8f, kasten: true, s);
        if (d.ShowSpeed)
        {
            Schreibe(g, d, e.Tempo(b.SpeedMs).ToString("0", CultureInfo.InvariantCulture), new RectangleF(rechts.X - rand * 0.5f, rechts.Y + rechts.Height * 0.38f, rechts.Width + rand, rechts.Width * 0.5f), rechts.Width * 0.42f * d.SpeedSize, hell, gross: true);
        }
        var y = rechts.Y + rechts.Height * 0.62f;
        if (d.ShowRpm)
        {
            Schreibe(g, d, b.Rpm.ToString("0", CultureInfo.InvariantCulture), new RectangleF(rechts.X - rand * 0.5f, y, rechts.Width + rand, rechts.Width * 0.2f), rechts.Width * 0.17f, leise);
            y += rechts.Width * 0.22f;
        }
        foreach (var info in Infos(d, b, e))
        {
            Schreibe(g, d, info, new RectangleF(rechts.X - rand * 0.5f, y, rechts.Width + rand, rechts.Width * 0.18f), rechts.Width * 0.15f, leise);
            y += rechts.Width * 0.18f;
        }
        SaeulenLampen(g, d, b, rechts, s);
        Pedale(g, d, b, new RectangleF(spalte.Right + 3f, spalte.Bottom - spalte.Height * 0.3f, rand * 0.5f, spalte.Height * 0.3f), waagrecht: false);
    }

    /// <summary>Die Lampen der Saeule untereinander, von unten.</summary>
    private static void SaeulenLampen(Graphics g, SpeedoDesign d, SpeedoBild b, RectangleF rechts, Schicht s)
    {
        if (_nurFuellung) { return; }
        var h = rechts.Width * 0.28f;
        var y = rechts.Bottom - h;
        if (d.ShowStability)
        {
            StabLampe(g, d, b, new RectangleF(rechts.X, y, rechts.Width, h), s);
            y -= h * 1.12f;
        }
        if (d.ShowTraction)
        {
            Lampe(g, d, new RectangleF(rechts.X, y, rechts.Width, h), d.LockText, b.WheelLock, Orange, s);
            y -= h * 1.12f;
            Lampe(g, d, new RectangleF(rechts.X, y, rechts.Width, h), d.TractionText, b.Wheelspin, Gelb, s);
        }
    }

    /// <summary>Die Skala des Tempoinstruments: bis zur naechsten runden Zahl ueber dem Hoechsttempo.</summary>
    private static int SkalaTempo(SpeedoBild b, SpeedoEinheiten e)
    {
        var schritt = e.Mph ? 20 : 40;
        var bis = Math.Max(e.Mph ? 140f : 220f, e.Tempo(b.TopSpeedMs) * 1.05f);
        return (int)Math.Ceiling(bis / schritt) * schritt;
    }

    private static void Doppel(Graphics g, SpeedoDesign d, SpeedoBild b, SpeedoEinheiten e, float w, float h, Schicht s)
    {
        var dm = Math.Min(h, w / 2f);
        var links = new RectangleF(w / 2f - dm - 2f, (h - dm) / 2f, dm, dm);
        var rechts = new RectangleF(w / 2f + 2f, (h - dm) / 2f, dm, dm);
        var hell = F(d.TextColour, Color.White);
        var leise = F(d.DimColour, Color.Gray);
        var rand = dm * 0.075f;
        // TEMPO links.
        var cl = new PointF(links.X + dm / 2f, links.Y + dm / 2f);
        var kl = new Kreis(cl, dm / 2f - rand - d.ArcThickness / 2f, d.ArcThickness, d.StartAngle, d.SweepAngle);
        var oben = SkalaTempo(b, e);
        var teile = oben / (e.Mph ? 20 : 40);
        // RPM rechts.
        var cr = new PointF(rechts.X + dm / 2f, rechts.Y + dm / 2f);
        var tausender = Math.Max(1, (int)Math.Ceiling(b.MaxRpm / 1000f));
        var kr = new Kreis(cr, dm / 2f - rand - d.ArcThickness / 2f, d.ArcThickness, d.StartAngle, d.SweepAngle);
        if (s == Schicht.Grund)
        {
            Rahmen(g, d, RectangleF.Inflate(links, -2f, -2f), kreis: true);
            Rahmen(g, d, RectangleF.Inflate(rechts, -2f, -2f), kreis: true);
        }
        var tempoAnteil = e.Tempo(b.SpeedMs) / Math.Max(1f, oben);
        Bogenskala(g, d, b with { RedlineRpm = b.MaxRpm * 2f }, kl, tempoAnteil, 1f, s, teile,
                   i => (i * (e.Mph ? 20 : 40)).ToString(CultureInfo.InvariantCulture));
        Bogenskala(g, d, b, kr, b.Rpm / Math.Max(1f, tausender * 1000f), b.RedlineRpm / Math.Max(1f, tausender * 1000f), s,
                   tausender, i => i.ToString(CultureInfo.InvariantCulture));
        if (s == Schicht.Grund)
        {
            if (d.ShowUnit) { Schreibe(g, d, e.TempoEinheit, new RectangleF(cl.X - dm * 0.2f, cl.Y + dm * 0.12f, dm * 0.4f, dm * 0.07f), dm * 0.055f, leise); }
            Schreibe(g, d, "x1000 rpm", new RectangleF(cr.X - dm * 0.2f, cr.Y - dm * 0.25f, dm * 0.4f, dm * 0.06f), dm * 0.045f, leise);
            Lampen(g, d, b, new RectangleF(cr.X - dm * 0.2f, cr.Y + dm * 0.25f, dm * 0.4f, dm * 0.065f), s);
            return;
        }
        if (d.ShowSpeed)
        {
            Schreibe(g, d, e.Tempo(b.SpeedMs).ToString("0", CultureInfo.InvariantCulture), new RectangleF(cl.X - dm * 0.3f, cl.Y + dm * 0.19f, dm * 0.6f, dm * 0.14f), dm * 0.11f * d.SpeedSize, hell, gross: true);
        }
        var unten = Infos(d, b, e);
        if (unten.Count > 0)
        {
            Schreibe(g, d, string.Join("  ·  ", unten), new RectangleF(cl.X - dm * 0.3f, cl.Y + dm * 0.33f, dm * 0.6f, dm * 0.06f), dm * 0.042f, leise);
        }
        Gang(g, d, b, new RectangleF(cr.X - dm * 0.12f, cr.Y + dm * 0.04f, dm * 0.24f, dm * 0.18f), dm * 0.15f, kasten: false, s);
        if (d.ShowRpm)
        {
            Schreibe(g, d, b.Rpm.ToString("n0", CultureInfo.CurrentCulture), new RectangleF(cr.X - dm * 0.2f, cr.Y - dm * 0.17f, dm * 0.4f, dm * 0.07f), dm * 0.055f, leise);
        }
        Lampen(g, d, b, new RectangleF(cr.X - dm * 0.2f, cr.Y + dm * 0.25f, dm * 0.4f, dm * 0.065f), s);
        Pedale(g, d, b, new RectangleF(cr.X - dm * 0.12f, cr.Y + dm * 0.35f, dm * 0.24f, dm * 0.03f), waagrecht: true);
        Nadel(g, d, b, kl, tempoAnteil);
        Nadel(g, d, b, kr, b.Rpm / Math.Max(1f, tausender * 1000f));
    }

    private static void Display(Graphics g, SpeedoDesign d, SpeedoBild b, SpeedoEinheiten e, float w, float h, Schicht s)
    {
        if (s == Schicht.Grund) { Rahmen(g, d, new RectangleF(1f, 1f, w - 2f, h - 2f), kreis: false); }
        var rand = h * 0.07f;
        var innen = new RectangleF(rand, rand, w - rand * 2f, h - rand * 2f);
        var y = innen.Y;
        if (d.ShowShiftLights)
        {
            Schaltlichter(g, d, b, new RectangleF(innen.X, y, innen.Width, innen.Height * 0.11f), s);
            y += innen.Height * 0.16f;
        }
        Balkenskala(g, d, b, new RectangleF(innen.X, y, innen.Width, Math.Max(3f, innen.Height * 0.05f)), waagrecht: true, s);
        y += innen.Height * 0.1f;
        var unten = innen.Bottom - innen.Height * 0.16f;
        var hoch = unten - y;
        var hell = F(d.TextColour, Color.White);
        var leise = F(d.DimColour, Color.Gray);
        var gangFeld = new RectangleF(innen.X + innen.Width * 0.36f, y, innen.Width * 0.28f, hoch * 0.8f);
        var links = new RectangleF(innen.X, y, innen.Width * 0.34f, hoch);
        var rechts = new RectangleF(innen.X + innen.Width * 0.66f, y, innen.Width * 0.34f, hoch);
        if (s == Schicht.Grund)
        {
            Gang(g, d, b, gangFeld, hoch * 0.72f, kasten: true, s);
            if (d.ShowSpeed && d.ShowUnit) { Schreibe(g, d, e.TempoEinheit, new RectangleF(links.X, links.Bottom - hoch * 0.24f, links.Width, hoch * 0.22f), hoch * 0.16f, leise); }
            if (d.ShowRpm) { Schreibe(g, d, "rpm", new RectangleF(rechts.X, rechts.Bottom - hoch * 0.24f, rechts.Width, hoch * 0.22f), hoch * 0.16f, leise); }
            Lampen(g, d, b, new RectangleF(innen.X + innen.Width * 0.3f, innen.Bottom - innen.Height * 0.13f, innen.Width * 0.4f, innen.Height * 0.12f), s);
            return;
        }
        Gang(g, d, b, gangFeld, hoch * 0.72f, kasten: true, s);
        if (d.ShowSpeed)
        {
            Schreibe(g, d, e.Tempo(b.SpeedMs).ToString("0", CultureInfo.InvariantCulture), new RectangleF(links.X, links.Y, links.Width, hoch * 0.76f), hoch * 0.55f * d.SpeedSize, hell, gross: true, leuchten: Leuchten(d, b) * 0.6f);
        }
        if (d.ShowRpm)
        {
            Schreibe(g, d, b.Rpm.ToString("0", CultureInfo.InvariantCulture), new RectangleF(rechts.X, rechts.Y, rechts.Width, hoch * 0.76f), hoch * 0.4f, b.ImRoten ? F(d.RedlineColour, Color.Red) : hell, gross: true);
        }
        var uhr = d.Clock != SpeedoClock.Off ? Uhrzeit(d, b) : string.Empty;
        var strecke = d.Distance != SpeedoDistance.Off ? Strecke(d, b, e) : string.Empty;
        var mitte = string.Join("  ·  ", Infos(d, b, e, mitUhr: false, mitStrecke: false));
        var zeile = new RectangleF(innen.X, innen.Bottom - innen.Height * 0.13f, innen.Width, innen.Height * 0.12f);
        Schreibe(g, d, uhr, zeile, zeile.Height * 0.8f, leise, Links);
        Schreibe(g, d, strecke, zeile, zeile.Height * 0.8f, leise, Rechts);
        if (mitte.Length > 0)
        {
            Schreibe(g, d, mitte, new RectangleF(gangFeld.X - innen.Width * 0.04f, gangFeld.Bottom, gangFeld.Width + innen.Width * 0.08f, hoch * 0.2f), hoch * 0.13f, leise);
        }
        Lampen(g, d, b, new RectangleF(innen.X + innen.Width * 0.3f, innen.Bottom - innen.Height * 0.13f, innen.Width * 0.4f, innen.Height * 0.12f), s);
        Pedale(g, d, b, new RectangleF(innen.X - rand * 0.7f, y, rand * 0.45f, hoch), waagrecht: false);
    }

    private static void Motorrad(Graphics g, SpeedoDesign d, SpeedoBild b, SpeedoEinheiten e, float w, float h, Schicht s)
    {
        if (s == Schicht.Grund) { Rahmen(g, d, new RectangleF(1f, 1f, w - 2f, h - 2f), kreis: false); }
        var rand = h * 0.07f;
        var innen = new RectangleF(rand, rand, w - rand * 2f, h - rand * 2f);
        // DIE ANSTEIGENDE KURVE: n Saeulen, jede hoeher als die vorige, leuchtend bis zur Drehzahl.
        var n = Math.Max(8, d.Fill == SpeedoFill.Smooth ? 48 : d.Segments);
        var kurve = new RectangleF(innen.X, innen.Y, innen.Width, innen.Height * 0.42f);
        var schritt = kurve.Width / n;
        var luecke = d.Fill == SpeedoFill.Smooth ? 0f : d.Fill == SpeedoFill.Blocks ? 0.4f : 0.22f;
        var anteil = _nurFuellung ? 1f : Math.Clamp(b.RpmAnteil, 0f, 1f);
        var blitz = Blitz(d, b);
        var leuchten = Leuchten(d, b);
        if (s == Schicht.Licht && !Direkt)
        {
            // Bis zur letzten Saeule, deren Mitte unter der Drehzahl liegt.
            var bisI = (int)Math.Floor(anteil * n + 0.5f);
            AusDemMerker(g, new RectangleF(kurve.X - 4f, kurve.Y - 6f, schritt * bisI + 4f, kurve.Height + 12f));
        }
        for (var i = 0; i < n && (s == Schicht.Grund || Direkt); i++)
        {
            var u = (i + 0.5f) / n;
            // Hoehe: weich ansteigend (Wurzel), oben nicht ganz voll.
            var hoehe = kurve.Height * (0.18f + 0.82f * (float)Math.Pow(u, 0.6));
            var r = new RectangleF(kurve.X + schritt * i, kurve.Bottom - hoehe, schritt * (1f - luecke) + (luecke == 0f ? 0.6f : 0f), hoehe * 0.32f + kurve.Height * 0.05f);
            if (s == Schicht.Grund)
            {
                using var spur = new SolidBrush(u >= b.RotAnteil ? Alpha(F(d.RedlineColour, Color.Red), 0.3f) : F(d.TrackColour, Color.FromArgb(40, 255, 255, 255)));
                g.FillRectangle(spur, r);
                continue;
            }
            if (u > anteil) { break; }
            var c = blitz ? Color.White : Segmentfarbe(d, b, u);
            if (leuchten > 0.05f)
            {
                using var schein = new SolidBrush(Alpha(c, 0.18f * leuchten));
                g.FillRectangle(schein, RectangleF.Inflate(r, 1.5f, 2.5f));
            }
            using var pinsel = new SolidBrush(c);
            g.FillRectangle(pinsel, r);
        }
        if (s == Schicht.Grund && d.TickNumbers)
        {
            var tausender = Math.Max(1, (int)Math.Ceiling(b.MaxRpm / 1000f));
            for (var i = 0; i <= tausender; i++)
            {
                var u = i * 1000f / Math.Max(1f, b.MaxRpm);
                if (u > 1.001f) { break; }
                var hoehe = kurve.Height * (0.18f + 0.82f * (float)Math.Pow(Math.Max(0.001f, u), 0.6));
                var x = kurve.X + kurve.Width * u;
                Schreibe(g, d, i.ToString(CultureInfo.InvariantCulture), new RectangleF(x - 10f, kurve.Bottom - hoehe - kurve.Height * 0.22f, 20f, kurve.Height * 0.2f),
                         kurve.Height * 0.16f, u >= b.RotAnteil ? F(d.RedlineColour, Color.Red) : F(d.DimColour, Color.Gray));
            }
        }
        // DAS LCD-FELD.
        var lcd = new RectangleF(innen.X, kurve.Bottom + innen.Height * 0.05f, innen.Width, innen.Bottom - kurve.Bottom - innen.Height * 0.05f);
        var hell = F(d.TextColour, Color.White);
        var leise = F(d.DimColour, Color.Gray);
        var gangFeld = new RectangleF(lcd.Right - lcd.Height * 0.8f, lcd.Y + lcd.Height * 0.05f, lcd.Height * 0.78f, lcd.Height * 0.9f);
        if (s == Schicht.Grund)
        {
            using (var feld = new SolidBrush(Alpha(F(d.AccentColour, Color.Orange), 0.07f)))
            using (var form = Platte(new SpeedoDesign { Frame = SpeedoFrame.Plate, CornerRadius = lcd.Height * 0.08f }, lcd, kreis: false))
            {
                g.FillPath(feld, form);
            }
            Gang(g, d, b, gangFeld, lcd.Height * 0.7f, kasten: true, s);
            if (d.ShowSpeed && d.ShowUnit) { Schreibe(g, d, e.TempoEinheit, new RectangleF(lcd.X + lcd.Width * 0.47f, lcd.Y + lcd.Height * 0.42f, lcd.Width * 0.14f, lcd.Height * 0.26f), lcd.Height * 0.18f, leise, Links); }
            Lampen(g, d, b, new RectangleF(lcd.X + lcd.Width * 0.05f, lcd.Bottom - lcd.Height * 0.25f, lcd.Width * 0.4f, lcd.Height * 0.19f), s);
            return;
        }
        if (d.ShowSpeed)
        {
            Schreibe(g, d, e.Tempo(b.SpeedMs).ToString("0", CultureInfo.InvariantCulture), new RectangleF(lcd.X + lcd.Width * 0.02f, lcd.Y, lcd.Width * 0.44f, lcd.Height * 0.72f), lcd.Height * 0.56f * d.SpeedSize, hell, Rechts, gross: true, leuchten: leuchten * 0.5f);
        }
        Gang(g, d, b, gangFeld, lcd.Height * 0.7f, kasten: true, s);
        var zeilen = Infos(d, b, e);
        if (d.ShowRpm) { zeilen.Insert(0, b.Rpm.ToString("n0", CultureInfo.CurrentCulture) + " rpm"); }
        var zy = lcd.Y + lcd.Height * 0.12f;
        foreach (var z in zeilen.Take(3))
        {
            Schreibe(g, d, z, new RectangleF(lcd.X + lcd.Width * 0.62f, zy, gangFeld.X - lcd.X - lcd.Width * 0.63f, lcd.Height * 0.18f), lcd.Height * 0.15f, leise, Links);
            zy += lcd.Height * 0.19f;
        }
        Lampen(g, d, b, new RectangleF(lcd.X + lcd.Width * 0.05f, lcd.Bottom - lcd.Height * 0.25f, lcd.Width * 0.4f, lcd.Height * 0.19f), s);
        Pedale(g, d, b, new RectangleF(lcd.X + lcd.Width * 0.62f, lcd.Bottom - lcd.Height * 0.14f, lcd.Width * 0.1f, lcd.Height * 0.08f), waagrecht: true);
    }
}

/// <summary>Ziffern als Sieben-Segment-Anzeige -- keine Schrift noetig, auf jedem Rechner gleich.</summary>
internal static class SiebenSegment
{
    // Segmente a b c d e f g als Bits 0..6.
    private static readonly Dictionary<char, int> Karte = new()
    {
        ['0'] = 0b0111111, ['1'] = 0b0000110, ['2'] = 0b1011011, ['3'] = 0b1001111, ['4'] = 0b1100110,
        ['5'] = 0b1101101, ['6'] = 0b1111101, ['7'] = 0b0000111, ['8'] = 0b1111111, ['9'] = 0b1101111,
        ['-'] = 0b1000000, [' '] = 0, ['R'] = 0b1010000, ['N'] = 0b1010100, ['P'] = 0b1110011,
    };

    public static bool Kann(string text) => text.All(c => Karte.ContainsKey(c) || c == ':' || c == '.');

    public static void Male(Graphics g, string text, RectangleF r, float hoehe, Color an, Color aus, StringAlignment lage, bool kursiv, float leuchten)
    {
        hoehe = Math.Min(hoehe, r.Height);
        var breite = hoehe * 0.52f;
        var abstand = hoehe * 0.14f;
        var gesamt = text.Sum(c => c is ':' or '.' ? breite * 0.35f : breite + abstand) - abstand;
        if (gesamt > r.Width && gesamt > 0f)
        {
            var f = r.Width / gesamt;
            hoehe *= f;
            breite *= f;
            abstand *= f;
            gesamt = r.Width;
        }
        var x = lage switch
        {
            StringAlignment.Near => r.X,
            StringAlignment.Far => r.Right - gesamt,
            _ => r.X + (r.Width - gesamt) / 2f,
        };
        var y = r.Y + (r.Height - hoehe) / 2f;
        var dicke = hoehe * 0.11f;
        var schraeg = kursiv ? 0.12f : 0.05f;
        foreach (var c in text)
        {
            if (c is ':' or '.')
            {
                using var pinsel = new SolidBrush(an);
                var p = dicke * 0.9f;
                if (c == ':') { g.FillRectangle(pinsel, x + breite * 0.1f, y + hoehe * 0.3f, p, p); }
                g.FillRectangle(pinsel, x + breite * 0.1f - (c == '.' ? schraeg * hoehe : 0f), y + hoehe * (c == ':' ? 0.68f : 0.9f), p, p);
                x += breite * 0.35f;
                continue;
            }
            var bits = Karte.GetValueOrDefault(c);
            for (var s = 0; s < 7; s++)
            {
                var istAn = (bits & (1 << s)) != 0;
                if (!istAn && aus.A == 0) { continue; }
                var form = Segment(s, x, y, breite, hoehe, dicke, schraeg);
                if (istAn && leuchten > 0.05f)
                {
                    using var schein = new Pen(Color.FromArgb(Math.Clamp((int)(70 * leuchten), 0, 255), an), dicke * 1.2f);
                    g.DrawPolygon(schein, form);
                }
                using var pinsel = new SolidBrush(istAn ? an : aus);
                g.FillPolygon(pinsel, form);
            }
            x += breite + abstand;
        }
    }

    private static PointF[] Segment(int s, float x, float y, float b, float h, float d, float schraeg)
    {
        PointF P(float px, float py) => new(x + px + (h - py) * schraeg, y + py);
        var m = h / 2f;
        var e = d / 2f;
        return s switch
        {
            0 => Waagrecht(P, e, b - e, e, d),             // a oben
            1 => Senkrecht(P, b - e, e, m, d),             // b oben rechts
            2 => Senkrecht(P, b - e, m, h - e, d),         // c unten rechts
            3 => Waagrecht(P, e, b - e, h - e, d),         // d unten
            4 => Senkrecht(P, e, m, h - e, d),             // e unten links
            5 => Senkrecht(P, e, e, m, d),                 // f oben links
            _ => Waagrecht(P, e, b - e, m, d),             // g Mitte
        };
    }

    private static PointF[] Waagrecht(Func<float, float, PointF> p, float x0, float x1, float y, float d)
    {
        var e = d / 2f;
        return new[] { p(x0 + e * 0.6f, y), p(x0 + d, y - e), p(x1 - d, y - e), p(x1 - e * 0.6f, y), p(x1 - d, y + e), p(x0 + d, y + e) };
    }

    private static PointF[] Senkrecht(Func<float, float, PointF> p, float x, float y0, float y1, float d)
    {
        var e = d / 2f;
        return new[] { p(x, y0 + e * 0.6f), p(x + e, y0 + d), p(x + e, y1 - d), p(x, y1 - e * 0.6f), p(x - e, y1 - d), p(x - e, y0 + d) };
    }
}
