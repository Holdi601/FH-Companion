using System.Drawing;
using System.Drawing.Imaging;
using System.Text.RegularExpressions;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Das Auto, auf dem im Automenue der Rahmen steht -- vom Bildschirm gelesen.
/// </summary>
/// <remarks>
/// ## Warum nicht aus der Telemetrie
///
/// Die Telemetrie beschreibt das Auto, das man FAEHRT. In "My Cars" wandert nur der
/// Rahmen ueber die Kacheln; gefahren wird weiter dasselbe Auto -- oben links steht es
/// ("1989 Nissan S-Cargo Forza Edition"), waehrend der Rahmen auf dem 595 esseesse
/// liegt. Bis zum 2026-09-25 hing die Autonotiz nur an der Telemetrie. Wer im Menue
/// zu einem Auto schreiben wollte, fand es im Reiter nicht, und eine Notiz erschien
/// nie ueber dem Menue.
///
/// ## Wie
///
/// Der Rahmen ist reines Gelbgruen, (202, 255, 2), gut 8 Punkte stark bei 4K, mit
/// einem schwarzen Spalt zur weissen Kachel. Nichts anderes auf diesem Schirm hat
/// diese Farbe in Kachelgroesse -- die gelbe Preisleiste liegt bei Rot 255, der
/// gruene Streifen "COMMON" viel dunkler. Gesucht wird auf einem kleinen Abbild;
/// gelesen wird nur der Titel darin: das Modell in der ersten Zeile, Baujahr und
/// Marke in der zweiten ("595 ESSEESSE" / "1968 ABARTH"). Gemessen am Bild des
/// Nutzers vom 2026-09-25: Titel von 6 bis 25 % der Rahmenhoehe.
///
/// Der Datensatz schreibt dasselbe Auto "Abarth 595 esseesse '68". Aus den zwei
/// Zeilen wird genau diese Form gebaut, und der Treffer muss das Baujahr teilen --
/// sonst waere "S-Cargo '89" dasselbe wie ein gleichnamiges Auto eines anderen Jahres.
/// </remarks>
internal static class CarGridReader
{
    /// <summary>Breite des Suchbilds. Bei 4K ist der Rahmen darin noch 2 bis 3 Punkte stark.</summary>
    internal const int Suchbreite = 1280;

    internal static bool IstRahmenFarbe(int r, int g, int b) =>
        r >= 150 && r <= 235 && g >= 225 && b <= 70;

    /// <summary>
    /// Den Rahmen im Bild finden -- ein hohles Rechteck in Kachelgroesse. Rechteck in
    /// Bildkoordinaten, oder null.
    /// </summary>
    internal static Rectangle? FindeRahmen(Bitmap bild)
    {
        var w = bild.Width;
        var h = bild.Height;
        if (w < 32 || h < 32) { return null; }
        var maske = new bool[w * h];
        var daten = bild.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly,
                                  PixelFormat.Format24bppRgb);
        try
        {
            unsafe
            {
                for (var y = 0; y < h; y++)
                {
                    var zeile = (byte*)daten.Scan0 + (y * daten.Stride);
                    for (var x = 0; x < w; x++)
                    {
                        // BGR
                        maske[(y * w) + x] = IstRahmenFarbe(zeile[(x * 3) + 2], zeile[(x * 3) + 1], zeile[x * 3]);
                    }
                }
            }
        }
        finally
        {
            bild.UnlockBits(daten);
        }

        // Zusammenhaengende Flaechen, mit einem Stapel statt Rekursion.
        var besucht = new bool[w * h];
        var stapel = new Stack<int>();
        Rectangle? bester = null;
        var besteFlaeche = 0;
        for (var start = 0; start < maske.Length; start++)
        {
            if (!maske[start] || besucht[start]) { continue; }
            int x0 = w, y0 = h, x1 = -1, y1 = -1, anzahl = 0;
            stapel.Push(start);
            besucht[start] = true;
            while (stapel.Count > 0)
            {
                var p = stapel.Pop();
                var px = p % w;
                var py = p / w;
                anzahl++;
                if (px < x0) { x0 = px; }
                if (px > x1) { x1 = px; }
                if (py < y0) { y0 = py; }
                if (py > y1) { y1 = py; }
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var nx = px + dx;
                        var ny = py + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) { continue; }
                        var q = (ny * w) + nx;
                        if (maske[q] && !besucht[q]) { besucht[q] = true; stapel.Push(q); }
                    }
                }
            }
            var bw = x1 - x0 + 1;
            var bh = y1 - y0 + 1;
            // KACHELGROESSE: gemessen 0,177 x 0,251 des Bildes. Grosszuegig, weil die
            // Kacheln je nach Seitenverhaeltnis und Menue etwas anders ausfallen.
            if (bw < w * 0.08 || bw > w * 0.40 || bh < h * 0.12 || bh > h * 0.50) { continue; }
            // HOHL: ein Rahmen deckt einen kleinen Teil seiner eigenen Flaeche.
            if (anzahl > bw * bh * 0.25) { continue; }
            if (!Seiten(maske, w, x0, y0, x1, y1)) { continue; }
            var flaeche = bw * bh;
            if (flaeche > besteFlaeche)
            {
                besteFlaeche = flaeche;
                bester = new Rectangle(x0, y0, bw, bh);
            }
        }
        return bester;
    }

    /// <summary>
    /// Die vier Seiten muessen Rahmenfarbe tragen: drei fast ganz, die vierte
    /// wenigstens zur Haelfte.
    /// </summary>
    /// <remarks>
    /// Nicht alle vier ganz: ein fremdes Fenster darf eine Ecke verdecken. Am Bild des
    /// Nutzers vom 2026-09-25 lag die App selbst ueber der Kachel -- rechte Seite zu
    /// 51 % sichtbar, die anderen zu 82 bis 97 %. Ein Strich oder ein Logo erfuellt
    /// die Regel trotzdem nie: dort fehlen zwei Seiten ganz.
    /// </remarks>
    private static bool Seiten(bool[] maske, int w, int x0, int y0, int x1, int y1)
    {
        double Waagrecht(int yVon, int yBis)
        {
            var treffer = 0;
            for (var x = x0; x <= x1; x++)
            {
                for (var y = yVon; y <= yBis; y++)
                {
                    if (maske[(y * w) + x]) { treffer++; break; }
                }
            }
            return treffer / (double)(x1 - x0 + 1);
        }
        double Senkrecht(int xVon, int xBis)
        {
            var treffer = 0;
            for (var y = y0; y <= y1; y++)
            {
                for (var x = xVon; x <= xBis; x++)
                {
                    if (maske[(y * w) + x]) { treffer++; break; }
                }
            }
            return treffer / (double)(y1 - y0 + 1);
        }
        const int Band = 3;
        var seiten = new[]
        {
            Waagrecht(y0, Math.Min(y1, y0 + Band)), Waagrecht(Math.Max(y0, y1 - Band), y1),
            Senkrecht(x0, Math.Min(x1, x0 + Band)), Senkrecht(Math.Max(x0, x1 - Band), x1),
        };
        return seiten.All(s => s >= 0.45) && seiten.Count(s => s >= 0.75) >= 3;
    }

    /// <summary>Wo der Titel in einem Rahmen steht: 6 bis 25 % der Hoehe, innen.</summary>
    internal static Rectangle TitelIn(Rectangle rahmen) =>
        Rectangle.FromLTRB(rahmen.Left + (int)(rahmen.Width * 0.03),
                           rahmen.Top + (int)(rahmen.Height * 0.055),
                           rahmen.Right - (int)(rahmen.Width * 0.03),
                           rahmen.Top + (int)(rahmen.Height * 0.255));

    private static readonly Regex JahrUndMarke =
        new(@"^\s*((?:19|20)\d{2})\s+(.+?)\s*$", RegexOptions.Compiled);

    /// <summary>
    /// Aus den gelesenen Zeilen den Namen im Stil des Datensatzes bauen:
    /// "Abarth 595 esseesse '68". Dazu das Baujahr, das der Treffer teilen muss.
    /// </summary>
    internal static (string Name, int Jahr)? NameAus(IReadOnlyList<OcrLine> zeilen)
    {
        var sortiert = zeilen.Where(z => !string.IsNullOrWhiteSpace(z.Text))
                             .OrderBy(z => z.Y).ToList();
        for (var i = 0; i < sortiert.Count; i++)
        {
            var m = JahrUndMarke.Match(sortiert[i].Text);
            if (!m.Success) { continue; }
            var modell = string.Join(' ', sortiert.Take(i).Select(z => z.Text.Trim()));
            if (modell.Length == 0) { continue; }
            var jahr = int.Parse(m.Groups[1].Value);
            var marke = m.Groups[2].Value.Trim();
            return ($"{marke} {modell} '{jahr % 100:00}", jahr);
        }
        return null;
    }

    /// <summary>Aus den Titelzeilen ein Auto des Datensatzes -- Name UND Baujahr muessen passen.</summary>
    internal static (int Ordinal, string Name)? Erkenne(IReadOnlyList<OcrLine> zeilen, RivalsAdvisor advisor)
    {
        var gebaut = NameAus(zeilen);
        if (gebaut is null) { return null; }
        var index = advisor.MatchCarName(gebaut.Value.Name, 0.8) ?? Abgeschnitten(gebaut.Value.Name, gebaut.Value.Jahr, advisor);
        if (index is null) { return null; }
        var name = advisor.RealCarName(index.Value);
        if (name is null || JahrVon(name) != gebaut.Value.Jahr) { return null; }
        var id = advisor.CarIdOf(index.Value);
        return id is > 0 ? (id.Value, name) : null;
    }

    /// <summary>
    /// Ein auf der Kachel abgeschnittener Name ("AUTODELTA TIPO 33/2 DA'"): der Anfang
    /// muss passen, das Baujahr auch -- und nur EIN Auto darf so anfangen.
    /// </summary>
    private static int? Abgeschnitten(string name, int jahr, RivalsAdvisor advisor)
    {
        // Ohne das Jahr am Ende und ohne das letzte, angeschnittene Wort.
        var ohneJahr = Regex.Replace(name, @"\s*'\d\d\s*$", string.Empty);
        var worte = TextMatch.Normalise(ohneJahr).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (worte.Length < 3) { return null; }
        var anfang = string.Join(' ', worte.Take(worte.Length - 1)) + " " + worte[^1];
        var kurz = string.Join(' ', worte.Take(worte.Length - 1));
        var treffer = new List<int>();
        for (var i = 0; i < advisor.CarCount; i++)
        {
            var n = advisor.RealCarName(i);
            if (n is null || JahrVon(n) != jahr) { continue; }
            var norm = TextMatch.Normalise(Regex.Replace(n, @"\s*'\d\d\s*$", string.Empty));
            if (norm.StartsWith(anfang, StringComparison.Ordinal) || norm.StartsWith(kurz + " ", StringComparison.Ordinal))
            {
                treffer.Add(i);
            }
        }
        return treffer.Count == 1 ? treffer[0] : null;
    }

    /// <summary>
    /// Denselben Weg an einem gespeicherten Bild gehen -- fuer Test und Kommandozeile.
    /// </summary>
    /// <remarks>
    /// Wie im Spiel: erst im verkleinerten Bild den Rahmen suchen, dann den Titel aus
    /// dem vollen Bild schneiden und auf 1440p-Mass bringen.
    /// </remarks>
    internal static (Rectangle Rahmen, string Gelesen, (int Ordinal, string Name)? Auto)? LiesBild(
        Bitmap ganz, Func<Bitmap, List<OcrLine>> ocr, RivalsAdvisor advisor)
    {
        var hoehe = Math.Max(1, Suchbreite * ganz.Height / Math.Max(1, ganz.Width));
        using var klein = new Bitmap(Suchbreite, hoehe, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(klein))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.DrawImage(ganz, new Rectangle(0, 0, klein.Width, klein.Height));
        }
        if (FindeRahmen(klein) is not { } k) { return null; }
        var f = ganz.Width / (double)klein.Width;
        var tk = TitelIn(k);
        var titel = Rectangle.Intersect(new Rectangle((int)(tk.X * f), (int)(tk.Y * f), (int)(tk.Width * f), (int)(tk.Height * f)),
                                        new Rectangle(0, 0, ganz.Width, ganz.Height));
        var skala = Math.Max(1.0, 1440.0 / Math.Max(1, ganz.Height));
        using var bild = new Bitmap(Math.Max(1, (int)(titel.Width * skala)), Math.Max(1, (int)(titel.Height * skala)),
                                    PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bild))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(ganz, new Rectangle(0, 0, bild.Width, bild.Height), titel, GraphicsUnit.Pixel);
        }
        var zeilen = ocr(bild);
        var gelesen = string.Join(" / ", zeilen.OrderBy(z => z.Y).Select(z => z.Text));
        var rahmen = new Rectangle((int)(k.X * f), (int)(k.Y * f), (int)(k.Width * f), (int)(k.Height * f));
        return (rahmen, gelesen, Erkenne(zeilen, advisor));
    }

    /// <summary>Das Baujahr eines Datensatz-Namens ("... '68" -> 1968), oder null.</summary>
    internal static int? JahrVon(string? name)
    {
        var m = Regex.Match(name ?? string.Empty, @"'(\d{2})\s*$");
        if (!m.Success) { return null; }
        var yy = int.Parse(m.Groups[1].Value);
        return yy >= 30 ? 1900 + yy : 2000 + yy;
    }

    /// <summary>Ein kleines Graubild einer Flaeche -- um zu merken, ob sie sich geaendert hat.</summary>
    internal static byte[] Fingerabdruck(Bitmap bild, Rectangle flaeche)
    {
        const int B = 48, H = 12;
        var raus = new byte[B * H];
        flaeche.Intersect(new Rectangle(0, 0, bild.Width, bild.Height));
        if (flaeche.Width < 2 || flaeche.Height < 2) { return raus; }
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < B; x++)
            {
                var p = bild.GetPixel(flaeche.X + (x * flaeche.Width / B), flaeche.Y + (y * flaeche.Height / H));
                raus[(y * B) + x] = (byte)((p.R + p.G + p.B) / 3);
            }
        }
        return raus;
    }

    internal static bool Gleich(byte[]? a, byte[]? b)
    {
        if (a is null || b is null || a.Length != b.Length) { return false; }
        long summe = 0;
        for (var i = 0; i < a.Length; i++) { summe += Math.Abs(a[i] - b[i]); }
        return summe / (double)a.Length < 6;
    }
}
