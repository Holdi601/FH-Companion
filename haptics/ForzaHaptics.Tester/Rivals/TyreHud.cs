using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Die vier Reifen auf einen Blick: Temperatur, Grip, Schlupf, Schraeglauf, Federweg,
/// Pfuetze, Curb, Fahrbahnrumpeln -- alles, was die Telemetrie ueber Reifen und
/// Fahrwerk sagt, als Bild statt Zahlenkolonne (seit 2026-09-28, auf Wunsch; OPT-IN).
/// </summary>
/// <remarks>
/// ## Wie es sich liest
///
///   * FARBE des Reifens = Temperatur: blau kalt, gruen im Fenster, gelb, rot heiss.
///     Die Zahl darin in Grad Celsius (die Telemetrie liefert Fahrenheit).
///   * RAND = kombinierter Schlupf: duenn, solange er haftet; gelb am Limit; rot und
///     leuchtend, wenn er rutscht.
///   * NEIGUNG des Reifens = Schraeglaufwinkel, bis 15 Grad bei eineinhalbfacher
///     Haftgrenze.
///   * Schild ueber dem Reifen: LOCK (blockiert), SPIN (dreht durch), SLIDE (rutscht quer).
///   * Balken darunter = Laengsschlupf: links rot beim Bremsen, rechts orange beim
///     Durchdrehen, der Strich bei 1 ist die Haftgrenze.
///   * Balken daneben = Federweg (voll = eingefedert, rot = auf Anschlag), dazu Prozent
///     und Zentimeter.
///   * Prozent im Reifen = Grip-Reserve: 1 minus kombinierter Schlupf, 0 % rutscht.
///   * Zeichen darunter: Tropfen = Pfuetze, Streifen = Curb, Wellen = Fahrbahnrumpeln.
///
/// ## Warum ab Werk aus
///
/// Es ist viel Information, und nicht jeder will sie im Rennen sehen. Einzuschalten im
/// Reiter "Lap delta HUD" (hud_tyres), frei zu platzieren und zu skalieren.
/// </remarks>
internal sealed class TyreHud : LayeredHud
{
    internal readonly record struct Rad(float TempC, float SlipRatio, float SlipAngle, float Combined,
                                        float Travel, float TravelM, float Puddle, bool Rumble,
                                        float SurfaceRumble, float Grip, float Lock);

    internal readonly record struct Zustand(Rad VL, Rad VR, Rad HL, Rad HR);

    private readonly OverlaySettings _settings;
    private Zustand? _jetzt;

    public TyreHud(OverlaySettings settings, Rectangle flaeche) : base(flaeche)
    {
        _settings = settings;
    }

    public void Setze(Zustand zustand)
    {
        _jetzt = zustand;
        Render();
    }

    protected override Rectangle Extent(Graphics messung) =>
        _jetzt is null ? Rectangle.Empty : Lege(_settings, AreaSize);

    protected override void Draw(Graphics g)
    {
        if (_jetzt is { } z) { Male(g, _settings, AreaSize, z); }
    }

    // Grundmass bei 1080p und Skala 1.
    internal const int Breite = 330;
    internal const int Hoehe = 350;
    private const float RadB = 58f;
    private const float RadH = 92f;

    internal static float Skala(OverlaySettings s, Size flaeche) =>
        GameArea.ResolutionScale(new Rectangle(Point.Empty, flaeche)) * (float)Math.Clamp(s.HudTyresScale, 0.4, 3.0);

    internal static Rectangle Lege(OverlaySettings s, Size flaeche)
    {
        var platz = s.TyresPlacement;
        var k = Skala(s, flaeche);
        var w = (int)Math.Ceiling(Breite * k);
        var h = (int)Math.Ceiling(Hoehe * k);
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

    /// <summary>Aus einem Telemetriepaket -- Temperatur von Fahrenheit nach Celsius.</summary>
    internal static Zustand AusPaket(ForzaPacket p)
    {
        Rad R(string ecke) => new(
            ((float)p.Get("TireTemp" + ecke) - 32f) * 5f / 9f,
            (float)p.Get("TireSlipRatio" + ecke),
            (float)p.Get("TireSlipAngle" + ecke),
            (float)p.Get("TireCombinedSlip" + ecke),
            (float)p.Get("NormalizedSuspensionTravel" + ecke),
            (float)p.Get("SuspensionTravelMeters" + ecke),
            (float)p.Get("WheelInPuddle" + ecke),
            p.Get("WheelOnRumbleStrip" + ecke) >= 0.5,
            (float)p.Get("SurfaceRumble" + ecke),
            (float)p.Get("Derived.Grip" + ecke),
            (float)p.Get("Derived.Lock" + ecke));
        return new Zustand(R("FrontLeft"), R("FrontRight"), R("RearLeft"), R("RearRight"));
    }

    /// <summary>Ein Beispiel, das jeden Zustand einmal zeigt -- fuer Vorschau und Einrichtung.</summary>
    internal static Zustand Beispiel()
    {
        // Grip wie in ForzaPacket.AddDerived: 1 minus kombinierter Schlupf.
        static Rad R(float c, float sr, float sa, float kombi, float weg, float pfuetze, bool curb,
                     float rumpeln, float sperre) =>
            new(c, sr, sa, kombi, weg, weg * 0.125f, pfuetze, curb, rumpeln,
                1f - Math.Clamp(Math.Abs(kombi), 0f, 1f), sperre);
        return new Zustand(
            R(121f, 0.3f, 1.25f, 1.25f, 0.72f, 0f, false, 0f, 0f),
            R(99f, 0.2f, 0.6f, 0.85f, 0.61f, 0f, true, 0.45f, 0f),
            R(88f, -1.35f, 0.2f, 1.1f, 0.40f, 0f, false, 0f, 0.9f),
            R(68f, 0.1f, 0.1f, 0.3f, 0.97f, 1f, false, 0.2f, 0f));
    }

    private static GraphicsPath Rund(RectangleF r, float radius)
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

    /// <summary>Temperatur (Grad C) als Farbe: blau kalt, gruen im Fenster, rot heiss.</summary>
    /// <remarks>
    /// Geeicht an einer echten Runde (2026-09-28): Median 90 bis 95 Grad, Spitzen bis
    /// 132 Grad beim Rutschen. Eine normale Runde soll gruen lesen, nur die Spitzen rot.
    /// Wo das Spiel den meisten Grip hat, verraet die Telemetrie nicht -- die Farbe ist
    /// ein Massstab, keine Grip-Kurve.
    /// </remarks>
    internal static Color TempFarbe(float c)
    {
        (float T, Color F)[] stufen =
        {
            (60f, Color.FromArgb(80, 140, 255)), (75f, Color.FromArgb(60, 200, 230)),
            (90f, Color.FromArgb(70, 210, 120)), (105f, Color.FromArgb(240, 210, 80)),
            (118f, Color.FromArgb(250, 150, 60)), (130f, Color.FromArgb(240, 80, 70)),
        };
        if (float.IsNaN(c) || c <= stufen[0].T) { return stufen[0].F; }
        for (var i = 1; i < stufen.Length; i++)
        {
            if (c <= stufen[i].T)
            {
                var u = (c - stufen[i - 1].T) / (stufen[i].T - stufen[i - 1].T);
                var a = stufen[i - 1].F;
                var b = stufen[i].F;
                return Color.FromArgb((int)(a.R + (b.R - a.R) * u), (int)(a.G + (b.G - a.G) * u), (int)(a.B + (b.B - a.B) * u));
            }
        }
        return stufen[^1].F;
    }

    /// <summary>Was ueber dem Reifen steht, wenn etwas los ist -- als englische Kennung; die
    /// Anzeige uebersetzt MaleRad.</summary>
    internal static (string Text, Color Farbe)? Schild(Rad r)
    {
        if (r.Lock > 0.5f || r.SlipRatio < -1f) { return ("LOCK", Color.FromArgb(240, 80, 70)); }
        if (r.SlipRatio > 1f) { return ("SPIN", Color.FromArgb(250, 150, 60)); }
        if (Math.Abs(r.SlipAngle) > 1f) { return ("SLIDE", Color.FromArgb(240, 210, 80)); }
        return null;
    }

    internal static void Male(Graphics g, OverlaySettings s, Size flaeche, Zustand z)
    {
        var b = Lege(s, flaeche);
        var k = Skala(s, flaeche);
        var gemerkt = g.Save();
        try
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.TranslateTransform(b.X, b.Y);
            g.ScaleTransform(k, k);
            using (var platte = new SolidBrush(OverlaySettings.ParseColour(s.HudBackground, Color.FromArgb(150, 8, 11, 16))))
            using (var form = Rund(new RectangleF(0, 0, Breite, Hoehe), 12f))
            {
                g.FillPath(platte, form);
            }
            using var titel = new Font("Segoe UI Semibold", 11f, GraphicsUnit.Pixel);
            using var leise = new SolidBrush(Color.FromArgb(160, 175, 192));
            g.DrawString(Loc.T("Tyres").ToUpperInvariant(), titel, leise, 12f, 7f);

            // Die Karosserie zwischen den Raedern -- nur ein Schatten, damit vorne und
            // hinten sofort klar sind.
            using (var koerper = Rund(new RectangleF(141f, 46f, 48f, 248f), 20f))
            using (var stift = new Pen(Color.FromArgb(45, 255, 255, 255), 2f))
            {
                g.DrawPath(stift, koerper);
            }
            using (var pfeil = new SolidBrush(Color.FromArgb(55, 255, 255, 255)))
            {
                g.FillPolygon(pfeil, new[] { new PointF(165f, 56f), new PointF(157f, 68f), new PointF(173f, 68f) });
            }

            // Keine Legende im Bild: im Rennen ist sie nur Rauschen. Erklaert wird im
            // Reiter, neben dem Schalter.
            var f = s.TyresFahrenheit;
            MaleRad(g, z.VL, links: true, 62f, 40f, f);
            MaleRad(g, z.VR, links: false, 210f, 40f, f);
            MaleRad(g, z.HL, links: true, 62f, 200f, f);
            MaleRad(g, z.HR, links: false, 210f, 200f, f);
        }
        finally
        {
            g.Restore(gemerkt);
        }
    }

    private static void MaleRad(Graphics g, Rad r, bool links, float x, float y, bool fahrenheit)
    {
        // FEDERWEG aussen neben dem Reifen: voll = eingefedert, rot = auf Anschlag.
        var bx = links ? x - 18f : x + RadB + 10f;
        using (var grund = new SolidBrush(Color.FromArgb(40, 255, 255, 255)))
        {
            g.FillRectangle(grund, bx, y, 8f, RadH);
        }
        var weg = Math.Clamp(float.IsNaN(r.Travel) ? 0f : r.Travel, 0f, 1f);
        var anschlag = weg > 0.95f;
        using (var feder = new SolidBrush(anschlag ? Color.FromArgb(240, 80, 70) : Color.FromArgb(150, 195, 255)))
        {
            g.FillRectangle(feder, bx, y + RadH * (1f - weg), 8f, RadH * weg);
        }
        using (var klein = new Font("Segoe UI Semibold", 11f, GraphicsUnit.Pixel))
        using (var hell = new SolidBrush(Color.FromArgb(205, 215, 228)))
        using (var leise = new SolidBrush(Color.FromArgb(140, 155, 172)))
        using (var seite = new StringFormat { Alignment = links ? StringAlignment.Far : StringAlignment.Near })
        {
            var tx = links ? new RectangleF(bx - 42f, y + RadH / 2f - 13f, 39f, 14f)
                           : new RectangleF(bx + 11f, y + RadH / 2f - 13f, 39f, 14f);
            g.DrawString($"{weg * 100f:0}%", klein, hell, tx, seite);
            if (!float.IsNaN(r.TravelM))
            {
                g.DrawString($"{r.TravelM * 100f:0.0}cm", klein, leise,
                             new RectangleF(tx.X, tx.Y + 13f, tx.Width, tx.Height), seite);
            }
        }

        // DER REIFEN, um den Schraeglauf gedreht.
        var winkel = Math.Clamp(float.IsNaN(r.SlipAngle) ? 0f : r.SlipAngle, -1.5f, 1.5f) * 10f;
        var zustand = g.Save();
        g.TranslateTransform(x + RadB / 2f, y + RadH / 2f);
        g.RotateTransform(winkel);
        var reifen = new RectangleF(-RadB / 2f, -RadH / 2f, RadB, RadH);
        var schlupf = float.IsNaN(r.Combined) ? 0f : Math.Abs(r.Combined);
        using (var form = Rund(reifen, 11f))
        {
            if (schlupf >= 1f)
            {
                using var glut = new Pen(Color.FromArgb(90, 240, 80, 70), 10f);
                g.DrawPath(glut, form);
            }
            using (var fuellung = new SolidBrush(Color.FromArgb(235, TempFarbe(r.TempC))))
            {
                g.FillPath(fuellung, form);
            }
            // Profil: ein paar Rillen, damit es wie ein Reifen aussieht.
            using (var rille = new Pen(Color.FromArgb(55, 0, 0, 0), 2f))
            {
                for (var i = 1; i <= 4; i++)
                {
                    var yy = reifen.Top + reifen.Height * i / 5f;
                    g.DrawLine(rille, reifen.Left + 6f, yy, reifen.Right - 6f, yy);
                }
            }
            var (farbe, breite) = schlupf >= 1f ? (Color.FromArgb(240, 80, 70), 4f)
                : schlupf >= 0.8f ? (Color.FromArgb(240, 210, 80), 3f)
                : (Color.FromArgb(80, 255, 255, 255), 1.5f);
            using var rand = new Pen(farbe, breite);
            g.DrawPath(rand, form);
        }
        using (var zahl = new Font("Segoe UI", 19f, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var klein = new Font("Segoe UI Semibold", 13f, GraphicsUnit.Pixel))
        using (var schatten = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
        using (var weiss = new SolidBrush(Color.White))
        using (var mitte = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            var temp = float.IsNaN(r.TempC) ? "--" : $"{(fahrenheit ? r.TempC * 9f / 5f + 32f : r.TempC):0}°";
            var grip = float.IsNaN(r.Grip) ? "" : $"{Math.Clamp(r.Grip, 0f, 1f) * 100f:0}%";
            var oben = new RectangleF(reifen.Left, reifen.Top + 14f, reifen.Width, 28f);
            var unten = new RectangleF(reifen.Left, reifen.Top + 48f, reifen.Width, 20f);
            g.DrawString(temp, zahl, schatten, new RectangleF(oben.X + 1f, oben.Y + 1f, oben.Width, oben.Height), mitte);
            g.DrawString(temp, zahl, weiss, oben, mitte);
            g.DrawString(grip, klein, schatten, new RectangleF(unten.X + 1f, unten.Y + 1f, unten.Width, unten.Height), mitte);
            g.DrawString(grip, klein, weiss, unten, mitte);
        }
        g.Restore(zustand);

        // SCHILD ueber dem Reifen. Schild() liefert die englische Kennung (der Grenzfalltest
        // vergleicht sie); uebersetzt wird erst hier, beim Zeichnen.
        if (Schild(r) is { } schild)
        {
            using var schrift = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Pixel);
            var wort = schild.Text switch
            {
                "LOCK" => Loc.T("LOCK"),
                "SPIN" => Loc.T("SPIN"),
                _ => Loc.T("SLIDE"),
            };
            // SO BREIT WIE DAS WORT (seit 2026-09-29), mindestens die alten 44 und hoechstens
            // 110 -- mittig ueber dem Reifen bleibt das auf der Platte. Vorher fest 44: ein
            // uebersetztes Wort lief ueber die Pille hinaus.
            var breite = Math.Clamp(g.MeasureString(wort, schrift).Width + 10f, 44f, 110f);
            var pille = new RectangleF(x + RadB / 2f - breite / 2f, y - 13f, breite, 15f);
            using (var form = Rund(pille, 7.5f))
            using (var fuellung = new SolidBrush(schild.Farbe))
            {
                g.FillPath(fuellung, form);
            }
            using var dunkel = new SolidBrush(Color.FromArgb(20, 22, 28));
            using var mitte = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(wort, schrift, dunkel, pille, mitte);
        }

        // LAENGSSCHLUPF als Balken unter dem Reifen: links rot (Bremsen), rechts orange.
        var sy = y + RadH + 9f;
        using (var grund = new SolidBrush(Color.FromArgb(40, 255, 255, 255)))
        {
            g.FillRectangle(grund, x, sy, RadB, 7f);
        }
        var sr = Math.Clamp(float.IsNaN(r.SlipRatio) ? 0f : r.SlipRatio, -1.5f, 1.5f);
        var halb = RadB / 2f;
        var laenge = halb * Math.Abs(sr) / 1.5f;
        using (var balken = new SolidBrush(sr < 0 ? Color.FromArgb(240, 80, 70) : Color.FromArgb(250, 150, 60)))
        {
            g.FillRectangle(balken, sr < 0 ? x + halb - laenge : x + halb, sy, laenge, 7f);
        }
        using (var strich = new Pen(Color.FromArgb(200, 255, 255, 255), 1f))
        {
            g.DrawLine(strich, x + halb, sy - 2f, x + halb, sy + 9f);
            var grenze = halb / 1.5f;
            g.DrawLine(strich, x + halb - grenze, sy + 1f, x + halb - grenze, sy + 6f);
            g.DrawLine(strich, x + halb + grenze, sy + 1f, x + halb + grenze, sy + 6f);
        }

        // ZEICHEN: Pfuetze, Curb, Rumpeln.
        var iy = y + RadH + 22f;
        var ix = x;
        using var zeichenSchrift = new Font("Segoe UI Semibold", 10f, GraphicsUnit.Pixel);
        // Horizon 6 meldet die Pfuetze als 0 oder 1 (siehe TelemetryValueType.FlagOrFloat32);
        // eine Tiefe in Prozent nur, falls je ein Bruchteil kommt.
        if (r.Puddle > 0.01f && !float.IsNaN(r.Puddle))
        {
            using var tropfen = new GraphicsPath();
            tropfen.AddBezier(ix + 6f, iy, ix + 11f, iy + 7f, ix + 12f, iy + 9f, ix + 12f, iy + 11f);
            tropfen.AddArc(ix, iy + 5f, 12f, 12f, 0, 180);
            tropfen.AddBezier(ix, iy + 11f, ix, iy + 9f, ix + 1f, iy + 7f, ix + 6f, iy);
            using var blau = new SolidBrush(Color.FromArgb(79, 195, 247));
            g.FillPath(blau, tropfen);
            if (r.Puddle < 0.99f)
            {
                g.DrawString($"{r.Puddle * 100f:0}%", zeichenSchrift, blau, ix + 14f, iy + 3f);
                ix += 44f;
            }
            else { ix += 18f; }
        }
        if (r.Rumble)
        {
            var feld = new RectangleF(ix, iy + 4f, 18f, 9f);
            using (var weiss = new SolidBrush(Color.White)) { g.FillRectangle(weiss, feld); }
            using var rot = new SolidBrush(Color.FromArgb(230, 60, 60));
            for (var i = 0; i < 3; i++) { g.FillRectangle(rot, feld.X + i * 6f, feld.Y, 3f, feld.Height); }
            ix += 24f;
        }
        var rumpeln = float.IsNaN(r.SurfaceRumble) ? 0f : Math.Clamp(r.SurfaceRumble, 0f, 1f);
        if (rumpeln > 0.05f)
        {
            using var welle = new Pen(Color.FromArgb((int)(90 + 165 * rumpeln), 200, 205, 215), 1.6f);
            for (var i = 0; i < 3; i++)
            {
                var yy = iy + 4f + i * 4f;
                g.DrawBezier(welle, ix, yy, ix + 4f, yy - 2.5f, ix + 8f, yy + 2.5f, ix + 12f, yy);
            }
        }
    }
}
