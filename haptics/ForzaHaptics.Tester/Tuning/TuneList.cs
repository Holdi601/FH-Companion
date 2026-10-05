using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ForzaHaptics.Tester.Rivals;

namespace ForzaHaptics.Tester.Tuning;

/// <summary>
/// Die Tunes-Liste des Spiels vom Bildschirm lesen: welcher Schirm, welche Kachel gewaehlt ist, welches
/// Tune darauf liegt und ob es das aufgespielte ist (graues Zeichen). Nur Texterkennung und Bildpunkte.
/// </summary>
internal static class TuneList
{
    internal enum Schirm
    {
        Unbekannt, CarsMenu, MyCars, ActionMenu, Upgrades, TunesList, TuneBrowser,
        FileOptions, DeleteConfirm, Warten,
    }

    internal readonly record struct TuneAufSchirm(string Name, string Creator, string Datum, string AutoZeile,
                                                  Rectangle Kachel, char Symbol);

    /// <summary>Welcher Schirm ist das -- an den Texten, die nur er hat.</summary>
    /// <remarks>
    /// In jeder Spielsprache (GameText, seit 2026-09-29) -- die Texte stammen aus den
    /// Tabellen des Spiels. "Jump to Manufacturer" gibt es dort nicht mehr; "Toggle
    /// Stats" allein erkennt My Cars.
    /// </remarks>
    internal static Schirm Einordnen(IReadOnlyList<OcrLine> z)
    {
        bool Hat(string schluessel, string englisch) => z.Any(l => GameText.Enthaelt(l.Text, schluessel, englisch));
        if (Hat("delete_file", "Delete File")
            && z.Any(l => GameText.BeginntWie(l.Text, "are_you_sure_delete", "Are you sure you want to delete this file?")))
        {
            return Schirm.DeleteConfirm;
        }
        if (Hat("file_options", "File Options")) { return Schirm.FileOptions; }
        if (Hat("please_wait", "Please Wait")) { return Schirm.Warten; }
        if (Hat("select_an_action", "Select an Action")) { return Schirm.ActionMenu; }
        if (Hat("date_created", "Date Created") && Hat("tuner_rank", "Tuner Rank"))
        {
            return Hat("trending", "TRENDING") || Hat("all_time_greats", "ALL TIME GREATS") ? Schirm.TuneBrowser : Schirm.TunesList;
        }
        if (Hat("my_tuning_setups", "My Tuning Setups") && Hat("custom_tuning", "Custom Tuning")) { return Schirm.Upgrades; }
        if (Hat("upgrades_tuning", "Upgrades & Tuning") && Hat("designs_paints", "Designs & Paints")) { return Schirm.CarsMenu; }
        if (Hat("toggle_stats", "Toggle Stats")) { return Schirm.MyCars; }
        return Schirm.Unbekannt;
    }

    internal static bool IstRahmen(int r, int g, int b) => CarGridReader.IstRahmenFarbe(r, g, b);

    /// <summary>
    /// Ist die Menuezeile mit diesem Text markiert? Der gelbgruene Rahmen reicht von 19
    /// Punkten ueber bis 38 Punkte unter die Textoberkante (gemessen in allen Menues und
    /// Dialogen). OBEN UND UNTEN muss er stehen: die Unterkante des markierten Eintrags
    /// liegt genau dort, wo der naechste seine Oberkante haette -- nur oben zu pruefen
    /// meldete immer zwei Eintraege als markiert.
    /// </summary>
    internal static bool Markiert(Bitmap bild, OcrLine zeile)
    {
        bool Band(int y0)
        {
            var treffer = 0;
            for (var y = Math.Max(0, y0); y < Math.Min(bild.Height, y0 + 7); y++)
            {
                var zeileTreffer = 0;
                for (var x = Math.Max(0, (int)zeile.X - 60); x < Math.Min(bild.Width, (int)zeile.X + 400); x += 2)
                {
                    var c = bild.GetPixel(x, y);
                    if (IstRahmen(c.R, c.G, c.B)) { zeileTreffer++; }
                }
                treffer = Math.Max(treffer, zeileTreffer);
            }
            return treffer >= 60;
        }
        return Band((int)zeile.Y - 22) && Band((int)zeile.Y + 34);
    }

    internal static OcrLine? Zeile(IReadOnlyList<OcrLine> z, string schluessel, string englisch)
    {
        foreach (var l in z) { if (GameText.Gleich(l.Text, schluessel, englisch)) { return l; } }
        foreach (var l in z) { if (GameText.Enthaelt(l.Text, schluessel, englisch)) { return l; } }
        return null;
    }

    /// <summary>Was die Tunes-Liste ueber das gewaehlte Tune sagt.</summary>
    /// <remarks>
    /// Lagen im 1080p-Bezug (gemessen am 2026-09-26): Tune-Name im gruenen Balken oben
    /// links (y ~196, x &lt; 570), Tuner unter dem Autobild (y ~566), "Date Created"
    /// mit Wert (y ~678), Modell und Baujahr des Autos (y ~269 und ~304). Die gewaehlte
    /// Kachel traegt den Rahmen im Band y 760-950.
    /// </remarks>
    /// <param name="nachForm">Die gewaehlte Kachel nach ihrer FORM suchen (<see cref="KachelNachForm"/>) statt
    /// als Umriss aller rahmenfarbenen Punkte. Nur fuer wer nur liest (die Autonotiz); der Loescher bleibt
    /// bei <see cref="GewaehlteKachel"/> mit der strengen Farbe.</param>
    internal static TuneAufSchirm LiesListe(IReadOnlyList<OcrLine> z, Bitmap bild, bool nachForm = false)
    {
        string In(int y0, int y1, int xMax) => string.Join(" ",
            z.Where(l => l.Y >= y0 && l.Y <= y1 && l.X < xMax).OrderBy(l => l.X).Select(l => l.Text.Trim()));
        var name = In(180, 215, 575);
        var creator = In(545, 590, 560);
        var datumZeile = z.FirstOrDefault(l => GameText.Enthaelt(l.Text, "date_created", "Date Created"));
        var datum = datumZeile.Text is null ? string.Empty
            : string.Join(" ", z.Where(l => Math.Abs(l.Y - datumZeile.Y) < 12 && l.X > datumZeile.X + 100 && l.X < 560)
                               .Select(l => l.Text.Trim()));
        var modell = In(255, 285, 560);
        var jahr = In(290, 320, 560);
        var kachel = nachForm ? KachelNachForm(bild) : GewaehlteKachel(bild);
        return new TuneAufSchirm(name, creator, datum, $"{modell} / {jahr}", kachel,
                                 kachel.IsEmpty ? '?' : Symbol(bild, kachel));
    }

    /// <summary>
    /// Die gewaehlte Kachel nach ihrer Form: ein hohler Rahmen in Kachelgroesse mit vier Seiten, gesucht
    /// wie im Automenue (<see cref="CarGridReader.FindeRahmen"/>, gelbgruen bis reines Gelb).
    /// </summary>
    /// <remarks>
    /// Live am 2026-10-04 mit HDR: der Kachelbereich der Tunes-Liste glueht gelbgruen, und der Umriss ALLER
    /// rahmenfarbenen Punkte im Band reichte von x 252 bis 870 -- mit der strengen Farbe genauso. Nach Form
    /// blieb genau eine Flaeche: die gewaehlte Kachel, 168 x 168, zu 9 % gefuellt. Gesucht wird im Band
    /// y 650-1050 (1080p), damit eine Kachel darin Kachelgroesse hat.
    /// </remarks>
    internal static Rectangle KachelNachForm(Bitmap bild)
    {
        if (bild.Width < 1000 || bild.Height < 1050) { return Rectangle.Empty; }
        var band = new Rectangle(0, 650, bild.Width, 400);
        using var ausschnitt = bild.Clone(band, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        if (CarGridReader.FindeRahmen(ausschnitt) is not { } r) { return Rectangle.Empty; }
        var k = new Rectangle(r.X, r.Y + band.Y, r.Width, r.Height);
        // Wie GewaehlteKachel: eine Kachel ist 100 bis 260 breit und mindestens 100 hoch.
        return k.Width is >= 100 and <= 260 && k.Height >= 100 ? k : Rectangle.Empty;
    }

    /// <summary>
    /// Sieht das nach der Tunes-Liste aus? Drei gelbgruene Balken quer ueber die Seite auf
    /// Hoehe 190-200 (1080p-Bezug). Billig -- erst danach lohnt eine Texterkennung.
    /// </summary>
    /// <remarks>
    /// Gemessen an den Aufnahmen vom 2026-09-26: auf der Tunes-Liste 74 von 85 Proben
    /// gelbgruen, auf My Cars, im Upgrades- und im Cars-Menue keine einzige.
    /// </remarks>
    internal static bool SiehtNachTunesAus(Bitmap bild)
    {
        // In jeder Groesse: die Stellen sind im 1080p-Bezug gemessen und werden umgerechnet.
        if (bild.Width < 480 || bild.Height < 270) { return false; }
        double fx = bild.Width / 1920.0, fy = bild.Height / 1080.0;
        var beste = 0;
        foreach (var y in new[] { 190, 196 })
        {
            var n = 0;
            for (var x = 110; x < 1810; x += 20)
            {
                var c = bild.GetPixel((int)(x * fx), (int)(y * fy));
                if (c.G > 200 && c.R > 150 && c.B < 90) { n++; }
            }
            beste = Math.Max(beste, n);
        }
        return beste >= 42;
    }

    /// <summary>
    /// Den Tune-Namen im gelbgruenen Balken noch einmal lesen, dreifach vergroessert.
    /// Schwarz auf Gelbgruen liest die Erkennung in voller Groesse schlecht: aus
    /// "A700 Road AWD" wurde "moo Road AWD" (Loeschlauf 2026-09-26).
    /// </summary>
    internal static string NameScharf(Bitmap bild1080, Func<Bitmap, List<OcrLine>> ocr)
    {
        var balken = Rectangle.Intersect(new Rectangle(105, 178, 470, 40), new Rectangle(0, 0, bild1080.Width, bild1080.Height));
        if (balken.Width < 10 || balken.Height < 10) { return string.Empty; }
        using var gross = new Bitmap(balken.Width * 3, balken.Height * 3, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(gross))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(bild1080, new Rectangle(0, 0, gross.Width, gross.Height), balken, GraphicsUnit.Pixel);
        }
        return string.Join(" ", ocr(gross).OrderBy(l => l.X).Select(l => l.Text.Trim())).Trim();
    }

    /// <summary>Der Rahmen der gewaehlten Kachel im Kachelband unten.</summary>
    internal static Rectangle GewaehlteKachel(Bitmap bild)
    {
        int x0 = int.MaxValue, x1 = -1, y0 = int.MaxValue, y1 = -1;
        for (var y = 740; y < Math.Min(bild.Height, 970); y += 2)
        {
            for (var x = 0; x < bild.Width; x += 2)
            {
                var c = bild.GetPixel(x, y);
                if (!IstRahmen(c.R, c.G, c.B)) { continue; }
                x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
            }
        }
        if (x1 < 0 || x1 - x0 < 100 || x1 - x0 > 260 || y1 - y0 < 100) { return Rectangle.Empty; }
        return Rectangle.FromLTRB(x0, y0, x1, y1);
    }

    /// <summary>
    /// Das Symbol oben links in der Kachel: 'v' rot (schwaecher), '^' gruen
    /// (staerker), '-' grau (unveraendert = das aufgespielte Tune).
    /// </summary>
    internal static char Symbol(Bitmap bild, Rectangle kachel)
    {
        long r = 0, g = 0, b = 0, n = 0;
        for (var y = kachel.Y + 18; y < kachel.Y + 24; y++)
        {
            for (var x = kachel.X + 19; x < kachel.X + 25; x++)
            {
                if (x >= bild.Width || y >= bild.Height) { continue; }
                var c = bild.GetPixel(x, y);
                r += c.R; g += c.G; b += c.B; n++;
            }
        }
        if (n == 0) { return '?'; }
        r /= n; g /= n; b /= n;
        if (Math.Abs(r - g) < 30 && Math.Abs(g - b) < 30 && r > 100 && r < 215) { return '-'; }
        if (r > 170 && g < 120) { return 'v'; }
        if (g > 160 && r < 200 && b < 110) { return '^'; }
        return '?';
    }

    /// <summary>Ist die Dialogzeile auf dieser Hoehe dunkel (= markiert)? Links und rechts neben dem Text gemessen.</summary>
    internal static bool Dunkel(Bitmap bild, int y)
    {
        long summe = 0, n = 0;
        foreach (var x0 in new[] { 660, 1220 })
        {
            for (var dy = -6; dy <= 6; dy += 3)
            {
                for (var x = x0; x < x0 + 40; x += 4)
                {
                    if (x >= bild.Width || y + dy < 0 || y + dy >= bild.Height) { continue; }
                    var c = bild.GetPixel(x, y + dy);
                    summe += (c.R + c.G + c.B) / 3;
                    n++;
                }
            }
        }
        return n > 0 && summe / n < 90;
    }

    /// <summary>Name und Tuner im Dialog "File Options".</summary>
    internal static (string Name, string Creator, string Auto) LiesDialog(IReadOnlyList<OcrLine> z)
    {
        var titel = z.FirstOrDefault(l => GameText.Enthaelt(l.Text, "file_options", "File Options"));
        var name = titel.Text is null ? string.Empty
            : z.Where(l => l.Y > titel.Y + 40 && l.Y < titel.Y + 110).OrderBy(l => l.Y).Select(l => l.Text.Trim()).FirstOrDefault() ?? "";
        var creatorKopf = z.FirstOrDefault(l => GameText.Gleich(l.Text, "creator", "Creator"));
        var creator = creatorKopf.Text is null ? string.Empty
            : z.Where(l => l.Y > creatorKopf.Y + 10 && l.Y < creatorKopf.Y + 45 && Math.Abs(l.X - creatorKopf.X) < 60)
               .Select(l => l.Text.Trim()).FirstOrDefault() ?? "";
        var auto = string.Join(" ", z.Where(l => l.Y < 80 && l.X > 120 && l.X < 700).OrderBy(l => l.X).Select(l => l.Text.Trim()));
        return (name, creator, auto);
    }

    /// <summary>Fuer den Vergleich: klein, nur Buchstaben und Ziffern, OCR-Verwechsler gleichgesetzt.</summary>
    internal static string Norm(string s)
    {
        var raus = new System.Text.StringBuilder();
        foreach (var ch in (s ?? string.Empty).ToLowerInvariant())
        {
            if (!char.IsLetterOrDigit(ch)) { continue; }
            raus.Append(ch switch { '1' or 'l' or '|' => 'i', '0' => 'o', '5' => 's', '8' => 'b', _ => ch });
        }
        return raus.ToString();
    }

    internal static double Aehnlich(string a, string b)
    {
        var x = Norm(a);
        var y = Norm(b);
        if (x.Length == 0 || y.Length == 0) { return 0; }
        if (x == y) { return 1; }
        var d = new int[x.Length + 1, y.Length + 1];
        for (var i = 0; i <= x.Length; i++) { d[i, 0] = i; }
        for (var j = 0; j <= y.Length; j++) { d[0, j] = j; }
        for (var i = 1; i <= x.Length; i++)
        {
            for (var j = 1; j <= y.Length; j++)
            {
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                                   d[i - 1, j - 1] + (x[i - 1] == y[j - 1] ? 0 : 1));
            }
        }
        return 1.0 - ((double)d[x.Length, y.Length] / Math.Max(x.Length, y.Length));
    }
}
