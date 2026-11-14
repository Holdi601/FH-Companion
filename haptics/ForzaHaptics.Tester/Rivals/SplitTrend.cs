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

    internal enum Tendenz { Gleich, Besser, Schlechter, Ich }

    /// <summary>Eine Zelle der Spalte: wo (Koordinaten der Spielflaeche), was, welche Farbe.</summary>
    internal readonly record struct Zelle(RectangleF Box, string Text, Tendenz Farbe, float Schrift, bool Deckend = false);

    // "00:13.423" (der Erste), "+0.948", "+1:02.345". Die Texterkennung liest den Punkt auch als Komma --
    // und verschluckt den Doppelpunkt in der Zeit des Ersten ("0020.403", an Aufnahmen 2026-11-14): ohne
    // ihn fehlte die Zeile des Fuehrenden in jeder Lesung. Minuten ohne Doppelpunkt nur vor zwei Sekundenziffern.
    private static readonly Regex ZeitMuster = new(@"^([+\-])?(?:(\d{1,2})(?:[:;]|(?=\d{2}[.,])))?(\d{1,2})[.,](\d{3})$", RegexOptions.CultureInvariant);
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

    /// <summary>Was eine Zelle zeigt: der Abstand, seine Farbe, und wie er sich seit dem vorigen Split veraendert hat.</summary>
    internal readonly record struct Wert(string Text, Tendenz Farbe, string Aenderung);

    /// <summary>Eine Zahl mit Vorzeichen und drei Nachkommastellen, wie das Spiel sie schreibt.</summary>
    internal static string Zahl(double d) => (d >= 0 ? "+" : "-") + Math.Abs(d).ToString("0.000", CultureInfo.InvariantCulture);

    /// <summary>
    /// Was die Liste zwischen den Splits gesagt hat, je Fahrer.
    /// </summary>
    internal sealed class Verlauf
    {
        // JE FAHRER (seit 2026-11-11, an Aufnahmen geprueft): der bestaetigte Abstand zu mir, der davor,
        // und ein Kandidat. Pro Fahrer, nicht ueber die ganze Liste: die Texterkennung verliert mal die
        // eine, mal die andere Zeile.
        //
        // FARBE NUR BEI EINEM ECHTEN SPLIT (seit 2026-11-14, Nutzer: "wechselt die Farbe, obwohl kein
        // Checkpoint da war"). Zwei gleiche Lesungen reichten: ein zweimal gleich verlesener Wert wurde
        // zum Split, die naechste richtige Lesung zum zweiten -- die Farbe sprang hin und zurueck. Jetzt:
        // ein neuer Wert gilt erst nach DREI gleichen Lesungen, und ein Wert, der schon einmal galt, wird
        // nie wieder zum Split (das Spiel zeigt keinen alten Abstand ein zweites Mal; ein Ruecksprung ist
        // ein Lesefehler). Zahl und Farbe wechseln zusammen, beim Bestaetigen.
        private const int Noetig = 3;

        private sealed class Fahrer
        {
            public double? Bestaetigt;
            public double? Vorher;
            public double? Kandidat;
            public int Gesehen;
            public readonly List<double> Frueher = new();
        }

        private readonly Dictionary<string, Fahrer> _fahrer = new(StringComparer.Ordinal);

        public void Vergessen() => _fahrer.Clear();

        /// <summary>Die neue Lesung einordnen; gibt je Zeile (in Reihenfolge der Liste) Text, Farbe und Aenderung.</summary>
        public List<Wert> Einordnen(IReadOnlyList<Zeile> zeilen)
        {
            var ich = zeilen.First(z => z.Ich);
            var raus = new List<Wert>();
            var vergeben = new HashSet<Fahrer>();
            var ohneNamen = new List<(int Stelle, double D)>();
            foreach (var z in zeilen)
            {
                if (z.Ich) { raus.Add(new Wert(string.Empty, Tendenz.Gleich, string.Empty)); continue; }
                var d = Math.Round(z.Abstand - ich.Abstand, 3);
                if (ScanFalte(z.Name).Length == 0)
                {
                    // Ohne Namen erst nach den anderen: dann zu dem Fahrer, dessen bestaetigter Abstand es ist.
                    ohneNamen.Add((raus.Count, d));
                    raus.Add(new Wert(Zahl(d), Tendenz.Gleich, string.Empty));
                    continue;
                }
                var f = Finde(ScanFalte(z.Name), vergeben);
                vergeben.Add(f);
                if (f.Bestaetigt is { } b && Gleich(d, b))
                {
                    f.Kandidat = null;
                    f.Gesehen = 0;
                }
                else if (f.Frueher.Any(x => Gleich(d, x)))
                {
                    // Ein Wert, der schon galt: verlesen oder ein Zwischenbild, kein neuer Split.
                }
                else if (f.Kandidat is { } k && Gleich(d, k))
                {
                    if (++f.Gesehen >= Noetig)
                    {
                        if (f.Bestaetigt is { } alt)
                        {
                            f.Frueher.Add(alt);
                            if (f.Frueher.Count > 6) { f.Frueher.RemoveAt(0); }
                        }
                        f.Vorher = f.Bestaetigt;
                        f.Bestaetigt = d;
                        f.Kandidat = null;
                        f.Gesehen = 0;
                    }
                }
                else
                {
                    f.Kandidat = d;
                    f.Gesehen = 1;
                }
                raus.Add(Zeige(f, d));
            }
            foreach (var (stelle, d) in ohneNamen)
            {
                var f = _fahrer.Values.FirstOrDefault(x => !vergeben.Contains(x) && x.Bestaetigt is { } b && Gleich(d, b));
                if (f is null) { continue; }
                vergeben.Add(f);
                raus[stelle] = Zeige(f, d);
            }
            return raus;
        }

        private static bool Gleich(double a, double b) => Math.Abs(a - b) < 0.0005;

        private static Wert Zeige(Fahrer f, double gelesen)
        {
            var farbe = Tendenz.Gleich;
            var aenderung = string.Empty;
            if (f.Bestaetigt is { } jetzt && f.Vorher is { } vorher)
            {
                // (sein Abstand - meiner) ist gewachsen = zu meinen Gunsten = gruen, "+".
                var a = Math.Round(jetzt - vorher, 3);
                farbe = a > 0.0005 ? Tendenz.Besser : a < -0.0005 ? Tendenz.Schlechter : Tendenz.Gleich;
                aenderung = Zahl(a);
            }
            return new Wert(Zahl(f.Bestaetigt ?? gelesen), farbe, aenderung);
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

    /// <summary>
    /// DAS RASTER DER LISTE, GEGLAETTET (seit 2026-11-14, Nutzer: "spastet rum, wird gross, passt nicht
    /// mehr in die Boxen"). Zeilenabstand, Schrifthoehe, Mitte der Zeitspalte und Lage der Zeilen wurden
    /// je Lesung neu gemessen -- eine schlechte Lesung (zwei Zeilen zu einer verschmolzen, eine Zeile
    /// fehlt) verschob und vergroesserte die Spalte fuer einen Augenblick. Jetzt gilt der Median der
    /// letzten sieben Lesungen, und Ausreisser kommen gar nicht erst hinein.
    /// </summary>
    internal sealed class Raster
    {
        private const int Laenge = 7;
        private readonly List<float> _abstand = new(), _hoehe = new(), _mitte = new(), _anker = new();

        public void Vergessen()
        {
            _abstand.Clear();
            _hoehe.Clear();
            _mitte.Clear();
            _anker.Clear();
        }

        private static float Median(List<float> l)
        {
            var s = l.OrderBy(x => x).ToList();
            return s[s.Count / 2];
        }

        private static void Rein(List<float> l, float wert, bool pruefen)
        {
            // Ab drei Lesungen: was mehr als ein Drittel vom Median abweicht, ist eine schlechte Lesung.
            if (pruefen && l.Count >= 3)
            {
                var m = Median(l);
                if (wert > m * 1.33f || wert < m * 0.75f) { return; }
            }
            l.Add(wert);
            if (l.Count > Laenge) { l.RemoveAt(0); }
        }

        public (float Abstand, float Hoehe, float Mitte, float Anker) Glaetten(float abstand, float hoehe, float mitte, IReadOnlyList<float> zeilen)
        {
            Rein(_abstand, abstand, true);
            Rein(_hoehe, hoehe, true);
            Rein(_mitte, mitte, true);
            var a = Median(_abstand);
            // Die Lage des Rasters: jede Zeile aufs Raster um den bisherigen Anker zurueckgerechnet --
            // so verschiebt eine fehlende oberste Zeile nichts.
            var bezug = _anker.Count > 0 ? Median(_anker) : zeilen.Min();
            var lagen = zeilen.Select(y => y - ((float)Math.Round((y - bezug) / a) * a)).OrderBy(y => y).ToList();
            Rein(_anker, lagen[lagen.Count / 2], false);
            return (a, Median(_hoehe), Median(_mitte), Median(_anker));
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
    internal static int Pruefen(string ordner, bool? decken = null, bool? aenderung = null)
    {
        var ocr = new WindowsOcr();
        var verlauf = new Verlauf();
        var raster = new Raster();
        var einstellungen = OverlaySettings.Load();
        var gamertag = einstellungen.Gamertag;
        var mitDecke = decken ?? einstellungen.HudSplitTrendCover;
        var mitAenderung = aenderung ?? einstellungen.HudSplitTrendChange;
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
            var zellen = Zellen(tabelle, werte, new PointF(region.X, region.Y), (float)(1.0 / k), raster, mitDecke, mitAenderung);
            log.AppendLine($"{name}: {tabelle.Count} Zeilen, {uhr.ElapsedMilliseconds} ms");
            if (Environment.GetEnvironmentVariable("FHC_SPLIT_OCR") == "1")
            {
                log.AppendLine("    OCR: " + string.Join(" | ", ocrZeilen.Select(z => $"{z.Text}@{z.X:0},{z.Y:0}")));
            }
            for (var i = 0; i < tabelle.Count; i++)
            {
                log.AppendLine($"    {(tabelle[i].Ich ? "ICH" : "   ")} {tabelle[i].Abstand,8:0.000}  {werte[i].Text,7} {werte[i].Farbe,-10} {werte[i].Aenderung,7} {tabelle[i].Name}");
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
    /// so gross geschrieben wie ihre Zeiten -- an Aufnahmen vom 2026-11-10 abgemessen: der Balken der
    /// Zeitspalte ist 2,92 Zeilenabstaende breit und steht mittig um ihre Ziffern.
    /// <paramref name="decken"/>: die Spalte liegt deckend auf der Zeitspalte des Spiels.
    /// <paramref name="aenderung"/>: links daneben eine Spalte mit der Veraenderung seit dem vorigen Split.
    /// </summary>
    internal static List<Zelle> Zellen(IReadOnlyList<Zeile> zeilen, IReadOnlyList<Wert> werte, PointF ursprung, float massstab,
                                      Raster? raster = null, bool decken = false, bool aenderung = false)
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
        // Die Mitte der Zeitspalte: die Zeiten stehen mittig in ihrem Balken.
        var zeitMitte = zeilen.Select(z => (z.Zeit.Left + z.Zeit.Right) / 2 * massstab).OrderBy(x => x).ElementAt(zeilen.Count / 2);
        var anker = sortiert[0];
        if (raster is not null) { (abstand, texthoehe, zeitMitte, anker) = raster.Glaetten(abstand, texthoehe, zeitMitte, mitten); }
        var fuge = Math.Max(2f, abstand * 0.06f);
        var hoehe = abstand - fuge;
        var schrift = texthoehe * 1.42f;
        var balken = abstand * 2.92f;
        var eigene = abstand * 2.1f;
        // Die rechte Kante der Abstandsspalte: deckend die des Zeitbalkens, sonst links davon mit Fuge.
        var rechts = decken ? zeitMitte + (balken / 2) : zeitMitte - (balken / 2) - fuge;
        var breite = decken ? balken : eigene;
        var ich = zeilen.FirstOrDefault(z => z.Ich);
        for (var i = 0; i < zeilen.Count; i++)
        {
            var n = (float)Math.Round((mitten[i] - anker) / abstand);
            var oben = ursprung.Y + anker + (n * abstand) - (hoehe / 2);
            var text = werte[i].Text;
            var farbe = werte[i].Farbe;
            if (decken && zeilen[i].Ich)
            {
                // Meine Zeile: mein Abstand zum Ersten, gelb wie im Spiel (als Erster nichts).
                farbe = Tendenz.Ich;
                text = ich is not null && ich.Abstand > 0.0005 ? Zahl(ich.Abstand) : string.Empty;
            }
            raus.Add(new Zelle(new RectangleF(ursprung.X + rechts - breite, oben, breite, hoehe), text, farbe, schrift, decken));
            if (aenderung && !zeilen[i].Ich)
            {
                var box = new RectangleF(ursprung.X + rechts - breite - fuge - eigene, oben, eigene, hoehe);
                raus.Add(new Zelle(box, werte[i].Aenderung, werte[i].Aenderung.Length > 0 ? werte[i].Farbe : Tendenz.Gleich, schrift, decken));
            }
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
            // Deckend (auf der Zeitspalte des Spiels): ganz undurchsichtig, damit dessen Zahlen nicht durchscheinen.
            var farbe = z.Farbe switch
            {
                SplitTrend.Tendenz.Besser => Color.FromArgb(z.Deckend ? 255 : 205, 38, 150, 72),
                SplitTrend.Tendenz.Schlechter => Color.FromArgb(z.Deckend ? 255 : 205, 190, 46, 46),
                SplitTrend.Tendenz.Ich => Color.FromArgb(255, 34, 36, 40),
                _ => z.Deckend ? Color.FromArgb(255, 52, 56, 62) : Color.FromArgb(170, 95, 100, 108),
            };
            using (var grund = new SolidBrush(farbe)) { g.FillRectangle(grund, z.Box); }
            if (z.Text.Length == 0) { continue; }
            // Die Zahl passt immer in ihre Zelle: notfalls kleiner, nie groesser als die Zelle.
            var groesse = Math.Max(6f, Math.Min(z.Schrift, z.Box.Height * 0.9f));
            var schrift = new Font("Bahnschrift SemiBold", groesse, FontStyle.Regular, GraphicsUnit.Pixel);
            var breite = g.MeasureString(z.Text, schrift).Width;
            if (breite > z.Box.Width * 0.94f)
            {
                schrift.Dispose();
                schrift = new Font("Bahnschrift SemiBold", Math.Max(6f, groesse * z.Box.Width * 0.94f / breite), FontStyle.Regular, GraphicsUnit.Pixel);
            }
            using (schrift)
            {
                var tinte = z.Farbe == SplitTrend.Tendenz.Ich ? Brushes.Yellow : Brushes.White;
                g.DrawString(z.Text, schrift, tinte, z.Box, format);
            }
        }
    }
}
