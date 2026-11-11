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
            // Ohne lesbaren Namen bleibt die Zeile: die Zeit genuegt, und die eigene Zeile erkennt man
            // dann an ihrer gelben Schrift (nachts las die Texterkennung den gelben Namen oft nicht).
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
        // JE FAHRER (seit 2026-11-11, an Aufnahmen geprueft): der bestaetigte Abstand zu mir, der davor,
        // und ein Kandidat. Ein neuer Wert gilt, wenn ihn zwei Lesungen hintereinander zeigen -- ein
        // einzelnes Zwischenbild loest so keine Farbe aus. Pro Fahrer, nicht ueber die ganze Liste:
        // die Texterkennung verliert mal die eine, mal die andere Zeile.
        private sealed class Fahrer
        {
            public double? Bestaetigt;
            public double? Vorher;
            public double? Kandidat;
        }

        private readonly Dictionary<string, Fahrer> _fahrer = new(StringComparer.Ordinal);

        public void Vergessen() => _fahrer.Clear();

        /// <summary>Die neue Lesung einordnen; gibt je Zeile (in Reihenfolge der Liste) Text und Farbe.</summary>
        public List<(string Text, Tendenz Farbe)> Einordnen(IReadOnlyList<Zeile> zeilen)
        {
            var ich = zeilen.First(z => z.Ich);
            var raus = new List<(string, Tendenz)>();
            var vergeben = new HashSet<Fahrer>();
            foreach (var z in zeilen)
            {
                if (z.Ich) { raus.Add((string.Empty, Tendenz.Gleich)); continue; }
                var d = Math.Round(z.Abstand - ich.Abstand, 3);
                if (ScanFalte(z.Name).Length == 0)
                {
                    // Ohne Namen kein Verlauf -- nur der Abstand.
                    raus.Add(((d >= 0 ? "+" : "-") + Math.Abs(d).ToString("0.00", CultureInfo.InvariantCulture), Tendenz.Gleich));
                    continue;
                }
                var f = Finde(ScanFalte(z.Name), vergeben);
                vergeben.Add(f);
                if (f.Bestaetigt is { } b && Math.Abs(d - b) < 0.0005)
                {
                    f.Kandidat = null;
                }
                else if (f.Kandidat is { } k && Math.Abs(d - k) < 0.0005)
                {
                    f.Vorher = f.Bestaetigt;
                    f.Bestaetigt = d;
                    f.Kandidat = null;
                }
                else
                {
                    f.Kandidat = d;
                }
                var farbe = Tendenz.Gleich;
                if (f.Bestaetigt is { } jetzt && f.Vorher is { } vorher)
                {
                    var aenderung = jetzt - vorher;
                    farbe = aenderung > 0.0005 ? Tendenz.Besser : aenderung < -0.0005 ? Tendenz.Schlechter : Tendenz.Gleich;
                }
                // Die Zahl ist immer die gelesene; nur die Farbe wartet auf die Bestaetigung.
                var zeige = d;
                raus.Add(((zeige >= 0 ? "+" : "-") + Math.Abs(zeige).ToString("0.00", CultureInfo.InvariantCulture), farbe));
            }
            return raus;
        }

        private Fahrer Finde(string name, HashSet<Fahrer> vergeben)
        {
            if (_fahrer.TryGetValue(name, out var f) && !vergeben.Contains(f)) { return f; }
            // Die Texterkennung liest einen Namen nicht jedes Mal gleich ("Washed xz", "Washed xzae"):
            // der aehnlichste bekannte, wenn er nah genug ist -- aber keiner, der in dieser Lesung schon
            // einem anderen Fahrer gehoert ("ErsterFahrer" und "DritterFahrer" liegen nah beieinander).
            var beste = _fahrer.Where(e => !vergeben.Contains(e.Value))
                               .Select(e => (e.Value, Wert: Aehnlich(e.Key, name))).OrderByDescending(e => e.Wert).FirstOrDefault();
            if (beste.Value is not null && beste.Wert >= 0.8) { return beste.Value; }
            f = new Fahrer();
            _fahrer[name] = f;
            return f;
        }
    }

    /// <summary>Wo die Liste gesucht wird: oben rechts in der 16:9-Spielflaeche.</summary>
    internal static Rectangle Gebiet(Rectangle flaeche) =>
        new(flaeche.X + (int)(flaeche.Width * 0.66), flaeche.Y + (int)(flaeche.Height * 0.03),
            (int)(flaeche.Width * 0.34), (int)(flaeche.Height * 0.52));

    /// <summary>
    /// PRUEFEN AN AUFNAHMEN: "--split-trend-check &lt;ordner&gt;" liest jedes f_*.jpg (ein ganzes
    /// Spielbild) wie im Rennen, schreibt split_check.txt und zeichnet die Spalte in Vorschaubilder.
    /// </summary>
    internal static int Pruefen(string ordner)
    {
        var ocr = new WindowsOcr();
        var verlauf = new Verlauf();
        var gamertag = OverlaySettings.Load().Gamertag;
        var log = new System.Text.StringBuilder();
        var aus = Path.Combine(ordner, "pruef");
        Directory.CreateDirectory(aus);
        var dateien = Directory.GetFiles(ordner, "f_*.jpg").OrderBy(p => p, StringComparer.Ordinal).ToList();
        int gelesen = 0, ohne = 0;
        long msGesamt = 0;
        foreach (var pfad in dateien)
        {
            using var voll = new Bitmap(pfad);
            var flaeche = GameArea.SixteenNine(new Rectangle(0, 0, voll.Width, voll.Height));
            var region = Gebiet(flaeche);
            var k = Math.Min(1.0, 1440.0 / Math.Max(1, flaeche.Height));
            using var bild = new Bitmap(Math.Max(1, (int)(region.Width * k)), Math.Max(1, (int)(region.Height * k)), PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bild))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(voll, new Rectangle(0, 0, bild.Width, bild.Height), region, GraphicsUnit.Pixel);
            }
            var uhr = System.Diagnostics.Stopwatch.StartNew();
            var ocrZeilen = ocr.Read(bild);
            var tabelle = Lesen(ocrZeilen, kasten => GelbAnteil(bild, kasten), gamertag);
            uhr.Stop();
            msGesamt += uhr.ElapsedMilliseconds;
            var name = Path.GetFileName(pfad);
            if (tabelle is null)
            {
                ohne++;
                log.AppendLine($"{name}: keine Liste ({ocrZeilen.Count} Textzeilen, {uhr.ElapsedMilliseconds} ms): "
                               + string.Join(" | ", ocrZeilen.Take(10).Select(z => $"{z.Text}@{z.X:0},{z.Y:0}")));
                continue;
            }
            gelesen++;
            var werte = verlauf.Einordnen(tabelle);
            var zellen = Zellen(tabelle, werte, new PointF(region.X, region.Y), (float)(1.0 / k));
            log.AppendLine($"{name}: {tabelle.Count} Zeilen, {uhr.ElapsedMilliseconds} ms");
            for (var i = 0; i < tabelle.Count; i++)
            {
                log.AppendLine($"    {(tabelle[i].Ich ? "ICH" : "   ")} {tabelle[i].Abstand,8:0.000}  {werte[i].Text,7} {werte[i].Farbe,-10} {tabelle[i].Name}");
            }
            using var vorschau = new Bitmap(region.Width, region.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(vorschau))
            {
                g.DrawImage(voll, new Rectangle(0, 0, region.Width, region.Height), region, GraphicsUnit.Pixel);
                g.TranslateTransform(-region.X, -region.Y);
                SplitTrendHud.Male(g, zellen);
            }
            vorschau.Save(Path.Combine(aus, name), ImageFormat.Jpeg);
        }
        log.Insert(0, $"{dateien.Count} Bilder, {gelesen} mit Liste, {ohne} ohne, im Mittel {(dateien.Count == 0 ? 0 : msGesamt / dateien.Count)} ms je Lesung{Environment.NewLine}");
        File.WriteAllText(Path.Combine(ordner, "split_check.txt"), log.ToString());
        return 0;
    }

    /// <summary>
    /// Die Zellen der Spalte in Koordinaten der Spielflaeche: links an die Zeitspalte der Liste gesetzt,
    /// auf ihr Zeilenraster eingerastet (gleich hoch wie ihre Balken, mit derselben schmalen Fuge) und
    /// so gross geschrieben wie ihre Zeiten -- an Aufnahmen vom 2026-11-10 abgemessen.
    /// </summary>
    internal static List<Zelle> Zellen(IReadOnlyList<Zeile> zeilen, IReadOnlyList<(string Text, Tendenz Farbe)> werte,
                                      PointF ursprung, float massstab)
    {
        var raus = new List<Zelle>();
        if (zeilen.Count == 0) { return raus; }
        var mitten = zeilen.Select(z => (z.Zeit.Top + z.Zeit.Bottom) / 2 * massstab).ToList();
        var sortiert = mitten.OrderBy(y => y).ToList();
        var schritte = sortiert.Zip(sortiert.Skip(1), (a, b) => b - a).Where(d => d > 1).OrderBy(d => d).ToList();
        var texthoehe = zeilen.Select(z => z.Zeit.Height * massstab).OrderBy(h => h).ElementAt(zeilen.Count / 2);
        // Der Zeilenabstand: der kleinste wiederkehrende Abstand (fehlt eine Zeile, ist ein Schritt doppelt).
        var abstand = schritte.Count > 0 ? schritte[0] : texthoehe * 1.75f;
        if (schritte.Count > 2) { abstand = schritte.Where(d => d < schritte[0] * 1.5f).Average(); }
        var anker = sortiert[0];
        var fuge = Math.Max(2f, abstand * 0.06f);
        var hoehe = abstand - fuge;
        var schrift = texthoehe * 1.42f;
        var zeitLinks = zeilen.Min(z => z.Zeit.Left) * massstab;
        var zeitBreite = zeilen.Max(z => z.Zeit.Width) * massstab;
        var breite = Math.Max(zeitBreite * 1.15f, schrift * 3.3f);
        // Der Balken der Zeitspalte beginnt etwas links ihrer Ziffern; die Fuge wie zwischen den Zeilen.
        var rechts = zeitLinks - (abstand * 0.22f) - fuge;
        for (var i = 0; i < zeilen.Count; i++)
        {
            var n = (float)Math.Round((mitten[i] - anker) / abstand);
            var mitte = anker + (n * abstand);
            var box = new RectangleF(ursprung.X + rechts - breite, ursprung.Y + mitte - (hoehe / 2), breite, hoehe);
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

    protected override void Draw(Graphics g) => Male(g, _zellen);

    /// <summary>Die Zellen zeichnen -- im Fenster wie in der Pruefung an Aufnahmen (--split-trend-check).</summary>
    internal static void Male(Graphics g, IEnumerable<SplitTrend.Zelle> zellen)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        foreach (var z in zellen)
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
