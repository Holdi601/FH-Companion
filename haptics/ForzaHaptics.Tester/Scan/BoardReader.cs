using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text.RegularExpressions;
using ForzaHaptics.Tester.Rivals;

namespace ForzaHaptics.Tester.Scan;

/// <summary>Eine Zeile der Rivals-Bestenliste, wie sie auf dem Schirm steht -- ohne Gamertag.</summary>
internal sealed record BoardRow
{
    /// <summary>Die Zeile im Bild, 0 = oben.</summary>
    public int Zeile { get; init; }
    public int? Rank { get; init; }
    public string? Car { get; init; }
    public int? Pi { get; init; }
    public string? Drivetrain { get; init; }
    public long? Ms { get; init; }
    public string? Gearbox { get; init; }
    public bool? Abs { get; init; }
    public bool? Tcs { get; init; }
    public bool? Stm { get; init; }
    /// <summary>Gueltige Runde? Das ungueltige Ende einer Liste traegt einen gelben Markierungskasten vor den Hilfen.</summary>
    public bool? IsClean { get; init; }
    /// <summary>Die markierte Zeile (schwarzer Grund, Schrift weiss) -- der Cursor der Liste.</summary>
    public bool Cursor { get; init; }
    /// <summary>
    /// Der Platz kommt aus der Folge (<see cref="BoardReader.MitFolge"/>, Mehrheit von mindestens zwei
    /// Zeilen) -- sonst ist er die rohe Lesung einer einzelnen Zelle und kann um Tausende daneben liegen.
    /// </summary>
    public bool Folge { get; init; }
    /// <summary>Was die Texterkennung je Spalte las, fuer die Nachpruefung.</summary>
    public string Line { get; init; } = string.Empty;

    /// <summary>Eine Zeile, die zaehlt: Platz und Zeit gelesen.</summary>
    public bool Brauchbar => Rank is > 0 && Ms is > 0;
}

/// <summary>
/// Die Zeilen der Rivals-Bestenliste ("Change Rival") aus einem 1080p-Bild.
/// </summary>
/// <remarks>
/// Die Tabelle liegt genau wie die des Rennergebnisses (RaceResultsReader): elf Zeilen, 48 px
/// hoch, 54 px Abstand, die erste ab y = 276 (gemessen 2026-10-03 am Hauptrechner). Gelesen wird
/// je Spalte ein Streifen ueber alle Zeilen, als dunkle Schrift auf Weiss -- in jeder Zelle gilt
/// als Tinte, was sich deutlich vom Grund DIESER Zelle abhebt. So liest dieselbe Regel weisse
/// Schrift auf dunklem Kasten (Platz, PI), schwarze auf weisser Zeile und die umgekehrte
/// Cursorzeile.
///
/// Spalten (1080p): Platz 60-188, Auto 600-862, PI 900-960, Antrieb 1040-1112, Zeit 1240-1382,
/// Gang 1676-1744; die drei Hilfen sind Kreise bei x = 1414 / 1513 / 1612, gefuellt = an.
/// Kein Gamertag: er wird weder gelesen noch gespeichert.
/// </remarks>
internal static class BoardReader
{
    internal const int Basis = 276, Abstand = 54, Zeilen = 11, Halb = 22;
    private const int Rand = 20;

    internal static readonly (int X0, int X1) RangSpalte = (60, 188);
    internal static readonly (int X0, int X1) AutoSpalte = (600, 862);
    internal static readonly (int X0, int X1) PiSpalte = (900, 960);
    internal static readonly (int X0, int X1) AntriebSpalte = (1040, 1112);
    internal static readonly (int X0, int X1) ZeitSpalte = (1240, 1382);
    internal static readonly (int X0, int X1) GangSpalte = (1676, 1744);
    internal static readonly int[] HilfenX = { 1414, 1513, 1612 };

    private static readonly Regex Zeit = new(@"(\d{1,2})\s*[:;.,]+\s*(\d{2})\s*[.,:;]+\s*(\d{3})", RegexOptions.CultureInvariant);
    private static readonly Regex ZeitOhneDoppelpunkt = new(@"^\s*(\d{2})1(\d{2})[.,](\d{3})\s*$", RegexOptions.CultureInvariant);
    /// <summary>"0254.132": der Doppelpunkt ganz verloren.</summary>
    private static readonly Regex ZeitOhneTrenner = new(@"^\s*(\d{2})(\d{2})[.,](\d{3})\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex Rang = new(@"\d{1,3}(?:[,.\s]?\d{3})*", RegexOptions.CultureInvariant);
    private static readonly Regex Antrieb = new(@"\b([ARF])\s*W\s*D\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    internal static int Mitte(int zeile) => Basis + (Abstand * zeile) + 24;

    /// <summary>Zeilen des letzten Bilds mit Auto, aber ohne lesbare Zeit (je Faden).</summary>
    [ThreadStatic] internal static int Ungelesen;

    /// <summary>Was die Texterkennung in Zahlen verliest: "02•.35.736", "02:38.i70", "1,1l7".</summary>
    internal static string Ziffern(string text) =>
        text.Replace('•', ':').Replace('·', ':').Replace('�', ':').Replace('O', '0').Replace('o', '0')
            .Replace('i', '1').Replace('l', '1').Replace('I', '1').Replace('|', '1').Replace('S', '5').Replace('B', '8');

    internal static long? Millisekunden(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) { return null; }
        var z = Ziffern(text);
        var m = Zeit.Match(z);
        if (!m.Success) { m = ZeitOhneDoppelpunkt.Match(z); }
        if (!m.Success) { m = ZeitOhneTrenner.Match(z); }
        if (!m.Success) { return null; }
        var sek = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        if (sek > 59) { return null; }
        return (long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 60_000) + (sek * 1000L)
               + long.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>"1,117" -> 1117; "10,117" -> 10117. Tausender nur mit genau drei Ziffern danach.</summary>
    internal static int? Platz(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) { return null; }
        var m = Rang.Match(Ziffern(text));
        if (!m.Success) { return null; }
        var ziffern = new string(m.Value.Where(char.IsDigit).ToArray());
        return ziffern.Length is >= 1 and <= 6 && int.TryParse(ziffern, out var r) && r > 0 ? r : null;
    }

    /// <summary>Die elf Zeilen eines Bilds (1920x1080 oder skaliert). Leere Zeilen kommen mit Rank = null.</summary>
    public static List<BoardRow> Lies(Bitmap bild, WindowsOcr ocr)
    {
        var raus = new List<BoardRow>();
        if (!ocr.Available) { return raus; }
        var px = Pixel.Aus(bild);
        // Zwischen STM und Gang steht keine Schrift: schwarz heisst Cursorzeile.
        var cursor = Enumerable.Range(0, Zeilen).Select(z => px.Hell(1650, Mitte(z)) < 60 && px.Hell(1650, Mitte(z) - 18) < 60).ToArray();

        string?[] Spalte((int X0, int X1) spalte, int faktor, bool platz = false, bool pi = false)
        {
            using var streifen = platz ? PlatzStreifen(px) : pi ? PiStreifen(px) : Streifen(px, spalte);
            using var gross = Vergroessert(streifen, faktor);
            var texte = new string?[Zeilen];
            foreach (var linie in ocr.Read(gross))
            {
                var mitte = (linie.Y + (linie.H > 0 ? linie.H / 2 : 12)) / faktor;
                var s = (int)Math.Floor((mitte - Rand) / (double)Abstand);
                if (s < 0 || s >= Zeilen) { continue; }
                texte[s] = texte[s] is null ? linie.Text : texte[s] + " " + linie.Text;
            }
            return texte;
        }

        var raenge = Spalte(RangSpalte, 2, platz: true);
        var autos = Spalte(AutoSpalte, 1);
        // Autonamen, die keinem Auto zugeordnet werden koennen ("Ford SO 8450" fuer "Ford SD F-450"),
        // noch einmal doppelt gross -- es gilt die Lesung, die ein Auto ergibt (wie im Rennergebnis).
        if (Enumerable.Range(0, Zeilen).Any(z => autos[z] is not null && CarShortNames.Finde(autos[z]) is null))
        {
            var gross = Spalte(AutoSpalte, 2);
            for (var z = 0; z < Zeilen; z++)
            {
                if (autos[z] is not null && CarShortNames.Finde(autos[z]) is null && CarShortNames.Finde(gross[z]) is not null) { autos[z] = gross[z]; }
            }
        }
        var pis = Spalte(PiSpalte, 2, pi: true);
        var antriebe = Spalte(AntriebSpalte, 2);
        var zeiten = Spalte(ZeitSpalte, 2);
        // Wo die Zeit nicht ging, obwohl die Zeile ein Auto hat (Doppelpunkt verschluckt, Ziffer
        // zerlegt -- oder die Zeile gar nicht erkannt: live 2026-10-04 fehlten so dieselben Plaetze
        // in jedem Lauf): noch einmal dreifach gross, dann in Originalgroesse.
        bool Fehlt(int z) => Millisekunden(zeiten[z]) is null && (zeiten[z] is not null || autos[z] is not null);
        foreach (var faktor in new[] { 3, 1 })
        {
            if (!Enumerable.Range(0, Zeilen).Any(Fehlt)) { break; }
            var nochmal = Spalte(ZeitSpalte, faktor);
            for (var z = 0; z < Zeilen; z++)
            {
                if (Fehlt(z) && Millisekunden(nochmal[z]) is not null) { zeiten[z] = nochmal[z]; }
            }
        }
        Ungelesen = Enumerable.Range(0, Zeilen).Count(Fehlt);

        for (var z = 0; z < Zeilen; z++)
        {
            int? pi = null;
            if (pis[z] is { } p && Regex.Match(Ziffern(p), @"\d{3}") is { Success: true } pm
                && int.TryParse(pm.Value, out var piWert) && piWert is >= 100 and <= 999)
            {
                pi = piWert;
            }
            var hilfen = HilfenX.Select(x => Gefuellt(px, x, Mitte(z))).ToArray();
            raus.Add(new BoardRow
            {
                Zeile = z,
                Rank = Platz(raenge[z]),
                Car = string.IsNullOrWhiteSpace(autos[z]) ? null : autos[z]!.Trim(),
                Pi = pi,
                Drivetrain = antriebe[z] is { } a && Antrieb.Match(a) is { Success: true } am ? am.Groups[1].Value.ToUpperInvariant() + "WD" : null,
                Ms = Millisekunden(zeiten[z]),
                Gearbox = Getriebe(px, Mitte(z)),
                Abs = hilfen[0], Tcs = hilfen[1], Stm = hilfen[2],
                IsClean = Sauber(px, Mitte(z)),
                Cursor = cursor[z],
                Line = string.Join(" | ", new[] { raenge[z], autos[z], pis[z], antriebe[z], zeiten[z] }.Select(t => t ?? "")),
            });
        }
        return MitFolge(raus);
    }

    /// <summary>
    /// Die Plaetze aus der Folge: die elf Zeilen eines Bilds sind aufeinanderfolgende Plaetze, also
    /// sagt jede lesbare Zeile, welcher Platz oben steht (gelesen minus Zeilennummer). Die Mehrheit
    /// entscheidet und setzt jeden Platz -- auch die zweistelligen "01".."09", die die
    /// Texterkennung unter drei Zeichen nicht zuverlaessig liest, und einzelne Fehllesungen.
    /// Ohne Mehrheit von mindestens zwei Stimmen bleibt alles, wie es gelesen wurde.
    /// </summary>
    internal static List<BoardRow> MitFolge(List<BoardRow> zeilen)
    {
        var stimmen = zeilen.Where(r => r.Rank is > 0).GroupBy(r => r.Rank!.Value - r.Zeile)
                            .Select(g => (Oben: g.Key, Anzahl: g.Count())).OrderByDescending(g => g.Anzahl).ToList();
        if (stimmen.Count == 0 || stimmen[0].Anzahl < 2 || stimmen[0].Oben < 1) { return zeilen; }
        if (stimmen.Count > 1 && stimmen[1].Anzahl == stimmen[0].Anzahl) { return zeilen; }
        var oben = stimmen[0].Oben;
        // Nur Zeilen mit einer Zeit bekommen einen Platz: eine leere Zeile ist noch nicht geladen.
        return zeilen.Select(r => r.Ms is > 0 ? r with { Rank = oben + r.Zeile, Folge = true } : r).ToList();
    }

    /// <summary>
    /// Das Getriebe aus der Form, nicht per Texterkennung (die braucht drei Zeichen): "MC" ist
    /// breit; bei "A" steht oben nur die Spitze in der Mitte, bei "M" stehen oben beide Striche.
    /// </summary>
    internal static string? Getriebe(Pixel px, int my)
    {
        var (x0, x1) = GangSpalte;
        var werte = new List<int>();
        for (var y = my - 14; y <= my + 14; y += 2) { for (var x = x0; x < x1; x += 2) { werte.Add(px.Hell(x, y)); } }
        werte.Sort();
        var grund = werte[werte.Count / 2];
        int links = int.MaxValue, rechts = -1, oben = int.MaxValue, unten = -1;
        for (var y = my - 16; y <= my + 16; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                if (Math.Abs(px.Hell(x, y) - grund) <= 90) { continue; }
                links = Math.Min(links, x); rechts = Math.Max(rechts, x);
                oben = Math.Min(oben, y); unten = Math.Max(unten, y);
            }
        }
        if (rechts < 0) { return null; }
        var breite = rechts - links + 1;
        var hoehe = unten - oben + 1;
        if (hoehe < 10 || breite < 6) { return null; }
        if (breite > 1.45 * hoehe) { return "MC"; }
        // Oberes Viertel: wo liegt dort Tinte?
        int lo = int.MaxValue, ro = -1;
        for (var y = oben; y <= oben + (hoehe / 4); y++)
        {
            for (var x = links; x <= rechts; x++)
            {
                if (Math.Abs(px.Hell(x, y) - grund) <= 90) { continue; }
                lo = Math.Min(lo, x); ro = Math.Max(ro, x);
            }
        }
        if (ro < 0) { return null; }
        var mitteNur = lo > links + (breite * 0.2) && ro < rechts - (breite * 0.2);
        return mitteNur ? "A" : "M";
    }

    /// <summary>
    /// Ist der Hilfe-Kreis gefuellt? Der Punkt in der Mitte (7 px) hebt sich vom Ring-Inneren ab;
    /// leer ist die Mitte so hell (bzw. in der Cursorzeile so dunkel) wie der Zwischenraum.
    /// Null, wenn dort gar kein Kreis zu sehen ist (leere Zeile).
    /// </summary>
    internal static bool? Gefuellt(Pixel px, int x, int y)
    {
        var mitte = (px.Hell(x, y) + px.Hell(x - 1, y) + px.Hell(x + 1, y) + px.Hell(x, y - 1) + px.Hell(x, y + 1)) / 5;
        var zwischen = (px.Hell(x, y - 6) + px.Hell(x, y + 6) + px.Hell(x - 6, y) + px.Hell(x + 6, y)) / 4;
        var ring = Math.Min(px.Hell(x, y - 10), px.Hell(x, y + 10));
        var ringHell = Math.Max(px.Hell(x, y - 10), px.Hell(x, y + 10));
        // Kein Ring (weder deutlich dunkler noch heller als der Zwischenraum): keine Aussage.
        if (Math.Abs(ring - zwischen) < 80 && Math.Abs(ringHell - zwischen) < 80) { return null; }
        return Math.Abs(mitte - zwischen) > 100;
    }

    /// <summary>
    /// Ungueltige Runden tragen einen gelben Kasten zwischen Zeit und ABS (x 1366-1402). Regel des
    /// alten Werkzeugs (ocr_leaderboard_frames.dirty_marker): im mittleren Drittel der Zeile mindestens
    /// 12 Punkte mit Rot ueber 150, Gruen ueber 120, Blau unter 110 -- dann ungueltig.
    /// </summary>
    internal static bool? Sauber(Pixel px, int my)
    {
        var treffer = 0;
        for (var y = my - 8; y <= my + 8; y++)
        {
            for (var x = 1366; x <= 1402; x++)
            {
                var (r, g, b) = px.Farbe(x, y);
                if (r > 150 && g > 120 && b < 110) { treffer++; }
            }
        }
        return treffer < 12;
    }

    private static Bitmap Vergroessert(Bitmap quelle, int faktor)
    {
        var gross = new Bitmap(quelle.Width * faktor, quelle.Height * faktor, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(gross);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(quelle, new Rectangle(0, 0, gross.Width, gross.Height));
        return gross;
    }

    /// <summary>
    /// Eine Spalte ueber alle Zeilen als dunkle Schrift auf Weiss. Je Zelle ist der Grund der
    /// Median ihrer Helligkeit; Tinte ist, was um mehr als 90 davon abweicht.
    /// </summary>
    private static Bitmap Streifen(Pixel px, (int X0, int X1) spalte)
    {
        var breite = spalte.X1 - spalte.X0;
        var bild = new Bitmap(breite + (2 * Rand), (Zeilen * Abstand) + (2 * Rand), PixelFormat.Format24bppRgb);
        var ziel = bild.LockBits(new Rectangle(0, 0, bild.Width, bild.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            var z = new byte[ziel.Stride * ziel.Height];
            Array.Fill(z, (byte)255);
            var werte = new int[breite * ((2 * Halb) + 1)];
            for (var s = 0; s < Zeilen; s++)
            {
                var my = Mitte(s);
                var n = 0;
                for (var dy = -Halb; dy <= Halb; dy++)
                {
                    for (var dx = 0; dx < breite; dx += 2) { werte[n++] = px.Hell(spalte.X0 + dx, my + dy); }
                }
                var grund = Median(werte, n);
                for (var dy = -Halb; dy <= Halb; dy++)
                {
                    var yz = Rand + (Abstand * s) + 24 + dy;
                    for (var dx = 0; dx < breite; dx++)
                    {
                        if (Math.Abs(px.Hell(spalte.X0 + dx, my + dy) - grund) <= 90) { continue; }
                        var j = (yz * ziel.Stride) + ((Rand + dx) * 3);
                        z[j] = z[j + 1] = z[j + 2] = 0;
                    }
                }
            }
            System.Runtime.InteropServices.Marshal.Copy(z, 0, ziel.Scan0, z.Length);
        }
        finally
        {
            bild.UnlockBits(ziel);
        }
        return bild;
    }

    /// <summary>
    /// Die Platz-Spalte: der Platz steht in einem Kasten -- dunkel mit weisser Schrift, in der
    /// Cursorzeile weiss mit schwarzer. Links daneben ist der tuerkise Seitengrund, der weder als
    /// hell noch als dunkel zaehlt. Die Kastenfarbe kommt von oberhalb der Ziffern (y - 18).
    /// </summary>
    private static Bitmap PlatzStreifen(Pixel px)
    {
        var (x0, x1) = RangSpalte;
        var breite = x1 - x0;
        var bild = new Bitmap(breite + (2 * Rand), (Zeilen * Abstand) + (2 * Rand), PixelFormat.Format24bppRgb);
        var ziel = bild.LockBits(new Rectangle(0, 0, bild.Width, bild.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            var z = new byte[ziel.Stride * ziel.Height];
            Array.Fill(z, (byte)255);
            for (var s = 0; s < Zeilen; s++)
            {
                var my = Mitte(s);
                var kastenHell = px.Hell(x1 - 10, my - 18) > 160;
                var hoehe = (2 * Halb) + 1;
                var maske = new bool[breite, hoehe];
                var spalteZahl = new int[breite];
                for (var dy = -Halb; dy <= Halb; dy++)
                {
                    for (var dx = 0; dx < breite; dx++)
                    {
                        var l = px.Hell(x0 + dx, my + dy);
                        if (kastenHell ? l >= 80 : l <= 200) { continue; }
                        maske[dx, dy + Halb] = true;
                        spalteZahl[dx]++;
                    }
                }
                // Kastenraender und Zeilenkanten sind duenne Striche ueber (fast) die ganze Hoehe; Ziffern
                // sind hoechstens 26 px hoch. Solche Striche weg -- sonst liest sich "1,060" als "11,0601".
                for (var dx = 0; dx < breite;)
                {
                    if (spalteZahl[dx] == 0) { dx++; continue; }
                    var anfang = dx;
                    var hoch = 0;
                    while (dx < breite && spalteZahl[dx] > 0) { hoch = Math.Max(hoch, spalteZahl[dx]); dx++; }
                    if (dx - anfang <= 4 && hoch > 32)
                    {
                        for (var x = anfang; x < dx; x++) { for (var y = 0; y < hoehe; y++) { maske[x, y] = false; } }
                    }
                }
                for (var dy = -Halb; dy <= Halb; dy++)
                {
                    var yz = Rand + (Abstand * s) + 24 + dy;
                    for (var dx = 0; dx < breite; dx++)
                    {
                        if (!maske[dx, dy + Halb]) { continue; }
                        var j = (yz * ziel.Stride) + ((Rand + dx) * 3);
                        z[j] = z[j + 1] = z[j + 2] = 0;
                    }
                }
            }
            System.Runtime.InteropServices.Marshal.Copy(z, 0, ziel.Scan0, z.Length);
        }
        finally
        {
            bild.UnlockBits(ziel);
        }
        return bild;
    }

    /// <summary>
    /// Die PI-Spalte: weisse Ziffern auf einem schwarzen Kasten, der niedriger ist als die Zeile.
    /// Mit der Median-Regel zaehlte der weisse Zeilengrund ueber und unter dem Kasten als Tinte,
    /// und "500" kam als "506" oder gar nicht (Klasse C, live 2026-10-04: 607 von 1.414). Hier:
    /// nur die Kastenhoehe, und Tinte ist, was hell UND farblos ist.
    /// </summary>
    private static Bitmap PiStreifen(Pixel px)
    {
        var (x0, x1) = PiSpalte;
        var breite = x1 - x0;
        var bild = new Bitmap(breite + (2 * Rand), (Zeilen * Abstand) + (2 * Rand), PixelFormat.Format24bppRgb);
        var ziel = bild.LockBits(new Rectangle(0, 0, bild.Width, bild.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            var z = new byte[ziel.Stride * ziel.Height];
            Array.Fill(z, (byte)255);
            for (var s = 0; s < Zeilen; s++)
            {
                var my = Mitte(s);
                for (var dy = -13; dy <= 13; dy++)
                {
                    var yz = Rand + (Abstand * s) + 24 + dy;
                    for (var dx = 0; dx < breite; dx++)
                    {
                        var (r, g, b) = px.Farbe(x0 + dx, my + dy);
                        var hell = (r + g + b) / 3;
                        if (hell <= 150 || Math.Abs(r - g) >= 40 || Math.Abs(g - b) >= 40) { continue; }
                        // Weisser Zeilengrund neben dem Kasten: dort ist auch der Nachbarpunkt weiter oben weiss.
                        var (ro, go, bo) = px.Farbe(x0 + dx, my - 18);
                        if ((ro + go + bo) / 3 > 200 && px.Hell(x0 + dx, my) > 200 && dy is < -11 or > 11) { continue; }
                        var j = (yz * ziel.Stride) + ((Rand + dx) * 3);
                        z[j] = z[j + 1] = z[j + 2] = 0;
                    }
                }
            }
            System.Runtime.InteropServices.Marshal.Copy(z, 0, ziel.Scan0, z.Length);
        }
        finally
        {
            bild.UnlockBits(ziel);
        }
        return bild;
    }

    private static int Median(int[] werte, int n)
    {
        var kopie = new int[n];
        Array.Copy(werte, kopie, n);
        Array.Sort(kopie);
        return n == 0 ? 255 : kopie[n / 2];
    }

    /// <summary>Die Pixel eines Bilds, im 1080p-Bezug abgefragt -- einmal kopiert statt GetPixel.</summary>
    internal sealed class Pixel
    {
        private readonly byte[] _daten;
        private readonly int _stride, _breite, _hoehe;
        private readonly double _fx, _fy;

        private Pixel(byte[] daten, int stride, int breite, int hoehe)
        {
            _daten = daten;
            _stride = stride;
            _breite = breite;
            _hoehe = hoehe;
            _fx = breite / 1920.0;
            _fy = hoehe / 1080.0;
        }

        public static Pixel Aus(Bitmap bild)
        {
            var d = bild.LockBits(new Rectangle(0, 0, bild.Width, bild.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                var puffer = new byte[d.Stride * bild.Height];
                System.Runtime.InteropServices.Marshal.Copy(d.Scan0, puffer, 0, puffer.Length);
                return new Pixel(puffer, d.Stride, bild.Width, bild.Height);
            }
            finally
            {
                bild.UnlockBits(d);
            }
        }

        /// <summary>Farbe am Punkt (x, y) des 1080p-Bezugs; ausserhalb weiss.</summary>
        public (int R, int G, int B) Farbe(int x, int y)
        {
            var xi = (int)(x * _fx);
            var yi = (int)(y * _fy);
            if (xi < 0 || yi < 0 || xi >= _breite || yi >= _hoehe) { return (255, 255, 255); }
            var i = (yi * _stride) + (xi * 3);
            return (_daten[i + 2], _daten[i + 1], _daten[i]);
        }

        /// <summary>Helligkeit 0..255 am Punkt (x, y) des 1080p-Bezugs; ausserhalb weiss.</summary>
        public int Hell(int x, int y)
        {
            var xi = (int)(x * _fx);
            var yi = (int)(y * _fy);
            if (xi < 0 || yi < 0 || xi >= _breite || yi >= _hoehe) { return 255; }
            var i = (yi * _stride) + (xi * 3);
            return (_daten[i] + _daten[i + 1] + _daten[i + 2]) / 3;
        }
    }
}
