using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// DIE SPLIT-TENDENZ NEBEN DER POSITIONSLISTE DES SPIELS (seit 2026-11-11).
/// </summary>
/// <remarks>
/// Oben rechts steht im Rennen die Liste der Fahrer, mit der Zeit des Ersten und fuer jeden
/// anderen dem Abstand ZUM ERSTEN ("+0.948"). Der Nutzer will wissen, wie weit er von jedem
/// einzelnen weg ist, und ob er seit dem letzten Split auf ihn aufgeholt hat. Darum eine Spalte
/// links daneben, gleich hoch und gleich gross geschrieben wie die Liste:
///
/// - DIE ZAHL: der Abstand zwischen dem Fahrer und mir, in Sekunden -- "-1.24" vor mir,
///   "+0.87" hinter mir. Aus der Liste: sein Abstand zum Ersten minus meiner.
/// - DIE FARBE: wie sich dieser Abstand seit dem vorigen Split veraendert hat. Gruen, wenn er
///   sich zu meinen Gunsten bewegt hat (vor mir: kleiner geworden; hinter mir: groesser), rot
///   umgekehrt, grau bei gleich. In einer Zahl: (sein Abstand - meiner) ist gewachsen = gruen.
///
/// Gelesen wird die Liste per Texterkennung, zweimal je Sekunde, auf einem eigenen Faden mit
/// niedriger Prioritaet und auf 1440p verkleinert -- der Nutzer: keine Ruckler, wenig
/// Ressourcen. Gezeichnet wird nur, wenn sich etwas aendert. Ist die Liste nicht (mehr) zu
/// sehen, verschwindet die Spalte mit ihr.
/// </remarks>
internal static class SplitTrend
{
    /// <summary>Eine Zeile der Liste: Name, Abstand zum Ersten (s), ob ich es bin, wo sie steht (Bildpunkte des Ausschnitts).</summary>
    internal sealed record Zeile(string Name, double Abstand, bool Ich, RectangleF Text, RectangleF Zeit);

    internal enum Tendenz { Gleich, Besser, Schlechter }

    /// <summary>Eine Zelle der Spalte: wo (Koordinaten der Spielflaeche), was, welche Farbe.</summary>
    internal readonly record struct Zelle(RectangleF Box, string Text, Tendenz Farbe, float Schrift);

    // "00:13.423" (der Erste), "+0.948", "+1:02.345". Die Texterkennung liest den Punkt auch als Komma.
    private static readonly Regex ZeitMuster = new(@"^([+\-])?(?:(\d{1,2})[:;])?(\d{1,2})[.,](\d{3})$", RegexOptions.CultureInvariant);
    private static readonly Regex PlatzMuster = new(@"^[0O]?\d{1,2}$", RegexOptions.CultureInvariant);

    /// <summary>Eine Zeitangabe als Sekunden und ob sie ein Abstand ("+") ist; null, wenn es keine ist.</summary>
    internal static (double Sekunden, bool Abstand)? AlsZeit(string token)
    {
        var t = token.Trim().Replace(" ", string.Empty);
        var m = ZeitMuster.Match(t);
        if (!m.Success) { return null; }
        var min = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        var s = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        var ms = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
        var wert = min * 60 + s + ms / 1000.0;
        var plus = m.Groups[1].Success;
        if (plus && m.Groups[1].Value == "-") { wert = -wert; }
        return (wert, plus);
    }

    /// <summary>
    /// Die Liste aus den Zeilen der Texterkennung -- oder null, wenn keine zu sehen ist (weniger als
    /// zwei Zeilen mit Zeit, oder nicht genau eine davon meine).
    /// </summary>
    /// <param name="istGelb">Ist der Text in diesem Kasten gelb? Das Spiel schreibt die eigene Zeile gelb.</param>
    internal static List<Zeile>? Lesen(IReadOnlyList<OcrLine> ocr, Func<RectangleF, double> gelbAnteil, string? gamertag)
    {
        // 1. Zeilen nach ihrer Mitte gruppieren: die Texterkennung trennt Zeit, Name und Platz oft.
        var nachY = ocr.Where(l => l.H > 0 && !string.IsNullOrWhiteSpace(l.Text))
                       .OrderBy(l => l.Y + l.H / 2).ToList();
        var gruppen = new List<List<OcrLine>>();
        foreach (var l in nachY)
        {
            var mitte = l.Y + l.H / 2;
            var g = gruppen.LastOrDefault();
            if (g is not null)
            {
                var gm = g.Average(x => x.Y + x.H / 2);
                var gh = g.Max(x => x.H);
                if (Math.Abs(mitte - gm) < 0.6 * Math.Max(gh, l.H)) { g.Add(l); continue; }
            }
            gruppen.Add(new List<OcrLine> { l });
        }

        // 2. Je Gruppe: die Zeit (ganz links in der Liste), der Platz (rechts), dazwischen der Name.
        var zeilen = new List<(Zeile Z, double Gelb)>();
        foreach (var g in gruppen)
        {
            var woerter = new List<(string T, RectangleF Box)>();
            foreach (var l in g.OrderBy(x => x.X))
            {
                // Eine zusammengefasste Zeile ("+0.948 Fahrername 02") in Woerter, ihre Breite anteilig.
                var teile = l.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var gesamt = Math.Max(1, l.Text.Length);
                var x = l.X;
                foreach (var t in teile)
                {
                    var w = l.W * t.Length / gesamt;
                    woerter.Add((t, new RectangleF((float)x, (float)l.Y, (float)Math.Max(1, w), (float)l.H)));
                    x += l.W * (t.Length + 1) / gesamt;
                }
            }
            // "+0. 948": die Texterkennung setzt manchmal ein Leerzeichen in die Zeit -- dann zwei Woerter zusammen.
            for (var i = 0; i + 1 < woerter.Count; i++)
            {
                if (AlsZeit(woerter[i].T) is null && AlsZeit(woerter[i].T + woerter[i + 1].T) is not null)
                {
                    woerter[i] = (woerter[i].T + woerter[i + 1].T, RectangleF.Union(woerter[i].Box, woerter[i + 1].Box));
                    woerter.RemoveAt(i + 1);
                }
            }
            var zeitIndex = woerter.FindIndex(w => AlsZeit(w.T) is not null);
            if (zeitIndex < 0) { continue; }
            var (sekunden, abstand) = AlsZeit(woerter[zeitIndex].T)!.Value;
            // Was links der Zeit steht, gehoert nicht zur Liste -- auch nicht die eigene Spalte,
            // falls die Aufnahme sie mitliest (ohne Fensteraufnahme, Windows 10).
            var rest = woerter.Skip(zeitIndex + 1).ToList();
            if (rest.Count > 0 && PlatzMuster.IsMatch(rest[^1].T)) { rest.RemoveAt(rest.Count - 1); }
            var name = string.Join(" ", rest.Select(r => r.T)).Trim();
            if (name.Length == 0) { continue; }
            var zeit = woerter[zeitIndex].Box;
            var links = zeit.Left;
            var rechts = woerter.Max(w => w.Box.Right);
            var oben = woerter.Min(w => w.Box.Top);
            var unten = woerter.Max(w => w.Box.Bottom);
            var text = RectangleF.FromLTRB(links, oben, rechts, unten);
            zeilen.Add((new Zeile(name, abstand ? sekunden : 0.0, false, text, zeit), gelbAnteil(text)));
        }
        if (zeilen.Count < 2) { return null; }

        // 3. Wer bin ich? Der Gamertag, sonst die gelbe Zeile.
        var ich = -1;
        if (!string.IsNullOrWhiteSpace(gamertag))
        {
            var gt = ScanFalte(gamertag);
            var treffer = zeilen.Select((z, i) => (i, Wert: Aehnlich(ScanFalte(z.Z.Name), gt))).OrderByDescending(e => e.Wert).First();
            if (treffer.Wert >= 0.8) { ich = treffer.i; }
        }
        if (ich < 0)
        {
            var gelb = zeilen.Select((z, i) => (i, z.Gelb)).OrderByDescending(e => e.Gelb).ToList();
            if (gelb[0].Gelb >= 0.3 && (gelb.Count < 2 || gelb[1].Gelb < 0.3)) { ich = gelb[0].i; }
        }
        if (ich < 0) { return null; }
        return zeilen.Select((z, i) => z.Z with { Ich = i == ich }).ToList();
    }

    /// <summary>Wie viel des hellen Texts in einem Kasten gelb ist, 0..1 -- die eigene Zeile schreibt das Spiel gelb.</summary>
    internal static double GelbAnteil(Bitmap bild, RectangleF kasten)
    {
        var r = Rectangle.Intersect(Rectangle.Round(kasten), new Rectangle(Point.Empty, bild.Size));
        if (r.Width < 2 || r.Height < 2) { return 0.0; }
        var daten = bild.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            int hell = 0, gelb = 0;
            unsafe
            {
                for (var y = 0; y < r.Height; y += 2)
                {
                    var zeile = (byte*)daten.Scan0 + (y * daten.Stride);
                    for (var x = 0; x < r.Width; x += 2)
                    {
                        var b = zeile[x * 3];
                        var g = zeile[(x * 3) + 1];
                        var rr = zeile[(x * 3) + 2];
                        if (Math.Max(rr, Math.Max(g, b)) < 170) { continue; }
                        hell++;
                        if (rr > 170 && g > 140 && b < 120) { gelb++; }
                    }
                }
            }
            return hell < 6 ? 0.0 : gelb / (double)hell;
        }
        finally
        {
            bild.UnlockBits(daten);
        }
    }

    internal static string ScanFalte(string? s) =>
        new((s ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    internal static double Aehnlich(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) { return 0.0; }
        var v = new int[b.Length + 1];
        var j0 = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) { v[j] = j; }
        for (var i = 1; i <= a.Length; i++)
        {
            j0[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                j0[j] = Math.Min(Math.Min(v[j] + 1, j0[j - 1] + 1), v[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }
            (v, j0) = (j0, v);
        }
        return 1.0 - (v[b.Length] / (double)Math.Max(a.Length, b.Length));
    }

    /// <summary>
    /// Was die Liste zwischen den Splits gesagt hat. Ein Split gilt, wenn zwei Lesungen
    /// hintereinander dieselben Abstaende zeigen (eine einzelne kann ein Zwischenbild sein).
    /// </summary>
    internal sealed class Verlauf
    {
        private Dictionary<string, double>? _letzter;   // Name -> (sein Abstand - meiner), beim juengsten Split
        private Dictionary<string, double>? _voriger;   // dasselbe beim Split davor
        private string? _kandidat;
        private string? _stand;

        public void Vergessen()
        {
            _letzter = _voriger = null;
            _kandidat = _stand = null;
        }

        /// <summary>Die neue Lesung einordnen; gibt je Zeile (in Reihenfolge der Liste) Text und Farbe.</summary>
        public List<(string Text, Tendenz Farbe)> Einordnen(IReadOnlyList<Zeile> zeilen)
        {
            var ich = zeilen.First(z => z.Ich);
            var jetzt = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var z in zeilen.Where(z => !z.Ich))
            {
                jetzt[ScanFalte(z.Name)] = z.Abstand - ich.Abstand;
            }
            var stand = string.Join("|", zeilen.Select(z => z.Abstand.ToString("0.000", CultureInfo.InvariantCulture)));
            if (stand != _stand)
            {
                if (stand == _kandidat)
                {
                    _voriger = _letzter;
                    _letzter = jetzt;
                    _stand = stand;
                }
                _kandidat = stand;
            }
            else
            {
                _kandidat = stand;
            }
            var raus = new List<(string, Tendenz)>();
            foreach (var z in zeilen)
            {
                if (z.Ich) { raus.Add((string.Empty, Tendenz.Gleich)); continue; }
                var name = ScanFalte(z.Name);
                var d = Finde(_letzter, name) ?? (z.Abstand - ich.Abstand);
                var vorher = Finde(_voriger, name);
                var farbe = Tendenz.Gleich;
                if (vorher is { } v && Finde(_letzter, name) is { } l)
                {
                    var aenderung = l - v;
                    farbe = aenderung > 0.0005 ? Tendenz.Besser : aenderung < -0.0005 ? Tendenz.Schlechter : Tendenz.Gleich;
                }
                raus.Add(((d >= 0 ? "+" : "-") + Math.Abs(d).ToString("0.00", CultureInfo.InvariantCulture), farbe));
            }
            return raus;
        }

        private static double? Finde(Dictionary<string, double>? stand, string name)
        {
            if (stand is null) { return null; }
            if (stand.TryGetValue(name, out var d)) { return d; }
            // Die Texterkennung liest einen Namen nicht jedes Mal gleich: der aehnlichste, wenn er nah genug ist.
            var beste = stand.Select(e => (e.Value, Wert: Aehnlich(e.Key, name))).OrderByDescending(e => e.Wert).FirstOrDefault();
            return beste.Wert >= 0.75 ? beste.Value : null;
        }
    }

    /// <summary>Die Zellen der Spalte in Koordinaten der Spielflaeche: links an die Zeit der Liste angesetzt, so hoch wie ihre Zeilen.</summary>
    internal static List<Zelle> Zellen(IReadOnlyList<Zeile> zeilen, IReadOnlyList<(string Text, Tendenz Farbe)> werte,
                                      PointF ursprung, float massstab)
    {
        var raus = new List<Zelle>();
        if (zeilen.Count == 0) { return raus; }
        // Zeilenabstand: der Median der Abstaende -- die Zellen schliessen dann aneinander an wie die Liste.
        var mitten = zeilen.Select(z => (z.Text.Top + z.Text.Bottom) / 2 * massstab).OrderBy(y => y).ToList();
        var schritte = mitten.Zip(mitten.Skip(1), (a, b) => b - a).Where(s => s > 1).OrderBy(s => s).ToList();
        var texthoehe = zeilen.Select(z => z.Text.Height * massstab).OrderBy(h => h).ElementAt(zeilen.Count / 2);
        var hoehe = schritte.Count > 0 ? schritte[schritte.Count / 2] : texthoehe * 1.6f;
        var schrift = texthoehe * 1.05f;
        var zeitLinks = zeilen.Min(z => z.Zeit.Left) * massstab;
        var breite = schrift * 3.6f;
        var luecke = schrift * 0.35f;
        for (var i = 0; i < zeilen.Count; i++)
        {
            var mitte = (zeilen[i].Text.Top + zeilen[i].Text.Bottom) / 2 * massstab;
            var box = new RectangleF(ursprung.X + zeitLinks - luecke - breite, ursprung.Y + mitte - (hoehe / 2), breite, hoehe - 1);
            raus.Add(new Zelle(box, werte[i].Text, werte[i].Farbe, schrift));
        }
        return raus;
    }
}

/// <summary>Die Spalte selbst: Zellen mit farbigem Grund, weisse Zahl -- nur gezeichnet, wenn sich etwas aendert.</summary>
internal sealed class SplitTrendHud : LayeredHud
{
    private List<SplitTrend.Zelle> _zellen = new();
    private string _zuletzt = string.Empty;

    public SplitTrendHud(Rectangle flaeche) : base(flaeche)
    {
    }

    /// <summary>Neue Zellen; false, wenn sie dieselben sind wie die gezeigten (dann wird nicht neu gezeichnet).</summary>
    public bool Setze(List<SplitTrend.Zelle> zellen)
    {
        var kennung = string.Join(";", zellen.Select(z => $"{z.Box.X:0},{z.Box.Y:0},{z.Box.Width:0},{z.Box.Height:0},{z.Text},{z.Farbe}"));
        if (kennung == _zuletzt && Visible) { return false; }
        _zuletzt = kennung;
        _zellen = zellen;
        Render();
        return true;
    }

    protected override Rectangle Extent(Graphics messung)
    {
        if (_zellen.Count == 0) { return Rectangle.Empty; }
        var r = _zellen[0].Box;
        foreach (var z in _zellen.Skip(1)) { r = RectangleF.Union(r, z.Box); }
        return Rectangle.Intersect(Rectangle.Ceiling(RectangleF.Inflate(r, 2, 2)), new Rectangle(Point.Empty, AreaSize));
    }

    protected override void Draw(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        foreach (var z in _zellen)
        {
            var farbe = z.Farbe switch
            {
                SplitTrend.Tendenz.Besser => Color.FromArgb(205, 38, 150, 72),
                SplitTrend.Tendenz.Schlechter => Color.FromArgb(205, 190, 46, 46),
                _ => Color.FromArgb(170, 95, 100, 108),
            };
            using (var grund = new SolidBrush(farbe)) { g.FillRectangle(grund, z.Box); }
            if (z.Text.Length == 0) { continue; }
            using var schrift = new Font("Bahnschrift SemiBold", Math.Max(6f, z.Schrift), FontStyle.Regular, GraphicsUnit.Pixel);
            g.DrawString(z.Text, schrift, Brushes.White, z.Box, format);
        }
    }
}
