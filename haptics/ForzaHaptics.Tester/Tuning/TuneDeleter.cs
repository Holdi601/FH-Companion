using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ForzaHaptics.Tester.Rivals;

namespace ForzaHaptics.Tester.Tuning;

/// <summary>
/// Ungenutzte Tunes im Spiel loeschen -- ueber die Menues des Spiels, Taste fuer Taste.
/// </summary>
/// <remarks>
/// ## Warum ueber die Menues
///
/// Die Tunes liegen als Container im Xbox-Spielstand, der mit der Cloud abgeglichen
/// wird (siehe <see cref="TuneStorage"/>). Dort von aussen zu loeschen koennte den
/// Spielstand beschaedigen. Das Spiel selbst loescht sauber -- also bedient die App die
/// Menues, wie der Nutzer es von Hand taete.
///
/// ## Der Weg (am 2026-09-26 aufgenommen)
///
///     Cars-Menue -> My Cars -> Auto -> "Select an Action": Get In Car -> (laedt) ->
///     Cars-Menue -> Upgrades & Tuning -> Upgrades: My Tuning Setups -> Tunes-Liste ->
///     Tune -> "File Options": Delete -> "Delete File": Yes -> "Please Wait"
///
/// ## Was verhindert, dass das Falsche geloescht wird
///
/// - Nur Tunes aus dem Loeschplan: laut Garagen-Pruefung auf keinem Auto.
/// - Nie eine Kachel mit grauem Symbol: grau heisst "unveraendert gegenueber dem Auto",
///   also das gerade aufgespielte Tune.
/// - Vor jedem Loeschen zweimal gelesen: Name und Tuner in der Liste UND im Dialog
///   "File Options"; das Auto in der Liste muss das gesuchte sein.
/// - Jeder Schirm wird gelesen, bevor eine Taste faellt. Passt er nicht zu dem, was
///   erwartet wird, haelt die Automatik an -- sie raet nicht.
/// - Anhalten: das Spiel verlassen (Alt+Tab) oder die Pause-Taste.
/// </remarks>
internal sealed class TuneDeleter
{
    internal enum Schirm
    {
        Unbekannt, CarsMenu, MyCars, ActionMenu, Upgrades, TunesList, TuneBrowser,
        FileOptions, DeleteConfirm, Warten,
    }

    internal sealed class Abbruch : Exception
    {
        public Abbruch(string grund) : base(grund) { }
    }

    private readonly WindowsOcr _ocr = new();
    private readonly RivalsAdvisor _rat;
    private readonly bool _probe;
    private readonly Action<string> _melden;
    private readonly CancellationToken _stop;
    private readonly string _logPfad;

    public int Geloescht { get; private set; }
    public int Uebersprungen { get; private set; }

    /// <summary>Nach so vielen bearbeiteten Autos anhalten -- fuer einen kurzen ersten Lauf.</summary>
    public int MaxAutos { get; init; } = int.MaxValue;

    private int _autos;

    public TuneDeleter(RivalsAdvisor rat, bool probelauf, Action<string> melden, CancellationToken stop)
    {
        _rat = rat;
        _probe = probelauf;
        _melden = melden;
        _stop = stop;
        var dir = Path.Combine(Path.GetTempPath(), "forza-overlay");
        Directory.CreateDirectory(dir);
        _logPfad = Path.Combine(dir, "tune_delete.log");
        try
        {
            var bilder = Path.Combine(dir, "tune_frames");
            if (Directory.Exists(bilder)) { foreach (var f in Directory.GetFiles(bilder, "*.jpg")) { File.Delete(f); } }
        }
        catch (Exception) { }
    }

    // ------------------------------------------------------------------ //
    // Eingabe und Aufnahme
    // ------------------------------------------------------------------ //

    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    internal const byte Enter = 0x0D, Esc = 0x1B, Links = 0x25, Hoch = 0x26, Rechts = 0x27, Runter = 0x28;
    private const int PauseTaste = 0x13;

    /// <summary>Das Spiel nach vorn holen -- nur moeglich, solange die App selbst vorn ist.</summary>
    public static bool SpielNachVorn()
    {
        try
        {
            var p = Process.GetProcessesByName("forzahorizon6").FirstOrDefault();
            if (p is null || p.MainWindowHandle == IntPtr.Zero) { return false; }
            ShowWindow(p.MainWindowHandle, 9);   // SW_RESTORE
            return SetForegroundWindow(p.MainWindowHandle);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void PruefeWeiter()
    {
        if (_stop.IsCancellationRequested) { throw new Abbruch("stopped in the app"); }
        if ((GetAsyncKeyState(PauseTaste) & 0x8000) != 0) { throw new Abbruch("stopped with the Pause key"); }
        if (GameWatch.ForegroundProcessName() != "forzahorizon6")
        {
            throw new Abbruch("the game is no longer in front");
        }
    }

    private void Taste(byte vk, int nachher = 260)
    {
        PruefeWeiter();
        var scan = (byte)MapVirtualKey(vk, 0);
        var erweitert = vk is Links or Hoch or Rechts or Runter ? 1u : 0u;
        keybd_event(vk, scan, erweitert, UIntPtr.Zero);
        Thread.Sleep(45);
        keybd_event(vk, scan, erweitert | 2u, UIntPtr.Zero);
        Thread.Sleep(nachher);
    }

    private static Bitmap Aufnahme()
    {
        var flaeche = GameArea.SixteenNine(GameArea.Find("forzahorizon6"));
        return GameArea.Capture(flaeche, new Size(1920, 1080));
    }

    private int _bildNr;

    /// <summary>
    /// Ein kleines Bild des Schirms ablegen, damit sich ein Lauf hinterher nachvollziehen
    /// laesst (%TEMP%orza-overlay	une_frames, bei jedem Lauf geleert).
    /// </summary>
    private void Merke(Bitmap bild, string was)
    {
        try
        {
            var dir = Path.Combine(Path.GetDirectoryName(_logPfad)!, "tune_frames");
            Directory.CreateDirectory(dir);
            using var klein = new Bitmap(bild, new Size(960, 540));
            var name = $"{++_bildNr:0000}_{string.Concat(was.Select(c => char.IsLetterOrDigit(c) ? c : '_'))}.jpg";
            klein.Save(Path.Combine(dir, name), ImageFormat.Jpeg);
        }
        catch (Exception) { }
    }

    private void Log(string text)
    {
        try { File.AppendAllText(_logPfad, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {text}{Environment.NewLine}"); }
        catch (Exception) { }
    }

    // ------------------------------------------------------------------ //
    // Schirme erkennen
    // ------------------------------------------------------------------ //

    internal sealed record Blick(Schirm Schirm, List<OcrLine> Zeilen, Bitmap Bild) : IDisposable
    {
        public void Dispose() => Bild.Dispose();
    }

    /// <summary>Steht das Spiel gerade auf dem Cars-Menue oder in My Cars? Fuer den Start von aussen.</summary>
    public static bool AufStartSchirm()
    {
        try
        {
            if (GameWatch.ForegroundProcessName() != "forzahorizon6") { return false; }
            using var bild = Aufnahme();
            var s = Einordnen(new WindowsOcr().Read(bild));
            return s is Schirm.CarsMenu or Schirm.MyCars;
        }
        catch (Exception) { return false; }
    }

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

    private Blick Sehen()
    {
        PruefeWeiter();
        var bild = Aufnahme();
        var zeilen = _ocr.Read(bild);
        return new Blick(Einordnen(zeilen), zeilen, bild);
    }

    /// <summary>Warten, bis einer der erwarteten Schirme steht.</summary>
    private Blick Warte(string wozu, int ms, params Schirm[] ziele)
    {
        var bis = DateTime.UtcNow.AddMilliseconds(ms);
        Blick? letzter = null;
        while (true)
        {
            letzter?.Dispose();
            letzter = Sehen();
            if (ziele.Contains(letzter.Schirm))
            {
                Log($"    {wozu}: {letzter.Schirm}");
                Merke(letzter.Bild, wozu);
                return letzter;
            }
            if (DateTime.UtcNow > bis)
            {
                var s = letzter.Schirm;
                Merke(letzter.Bild, "TIMEOUT " + wozu);
                letzter.Dispose();
                throw new Abbruch($"{wozu}: expected {string.Join("/", ziele)}, saw {s}");
            }
            Thread.Sleep(250);
        }
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

    private static OcrLine? Zeile(IReadOnlyList<OcrLine> z, string schluessel, string englisch)
    {
        foreach (var l in z) { if (GameText.Gleich(l.Text, schluessel, englisch)) { return l; } }
        foreach (var l in z) { if (GameText.Enthaelt(l.Text, schluessel, englisch)) { return l; } }
        return null;
    }

    /// <summary>In einem Menue den Eintrag anwaehlen: hoch oder runter, bis sein Rahmen steht.</summary>
    /// <param name="schluessel">Das Wort in GameText -- der Eintrag heisst in jeder Spielsprache anders.</param>
    private Blick Waehle(Blick blick, string schluessel, string eintrag, Schirm schirm)
    {
        for (var versuch = 0; versuch < 16; versuch++)
        {
            var gefunden = Zeile(blick.Zeilen, schluessel, eintrag)
                           ?? throw new Abbruch($"menu entry '{eintrag}' not found on {blick.Schirm}");
            var ziel = gefunden;
            if (Markiert(blick.Bild, ziel)) { return blick; }
            // Welcher Eintrag ist markiert? Dann in die richtige Richtung.
            var markiert = blick.Zeilen.Where(l => Math.Abs(l.X - ziel.X) < 250 && Math.Abs(l.Y - ziel.Y) < 400
                                                   && !GameText.Enthaelt(l.Text, "file_options", "File Options")
                                                   && !GameText.Enthaelt(l.Text, "delete_file", "Delete File")
                                                   && Markiert(blick.Bild, l))
                                       .OrderBy(l => Math.Abs(l.Y - ziel.Y)).Select(l => (OcrLine?)l).FirstOrDefault();
            blick.Dispose();
            // NICHTS MARKIERT: der Dialog blendet noch ein. Warten, nicht druecken -- eine
            // Taste ins Leere haette "No" statt "Yes" oder "Like" statt "Delete" gewaehlt.
            if (markiert is null)
            {
                Thread.Sleep(300);
                blick = Warte($"selecting {eintrag}", 4000, schirm);
                continue;
            }
            Taste(markiert.Value.Y < ziel.Y ? Runter : Hoch, 200);
            blick = Warte($"selecting {eintrag}", 4000, schirm);
        }
        blick.Dispose();
        throw new Abbruch($"could not select '{eintrag}'");
    }

    // ------------------------------------------------------------------ //
    // Tunes-Liste und Dialog lesen
    // ------------------------------------------------------------------ //

    internal readonly record struct TuneAufSchirm(string Name, string Creator, string Datum, string AutoZeile,
                                                  Rectangle Kachel, char Symbol);

    /// <summary>Was die Tunes-Liste ueber das gewaehlte Tune sagt.</summary>
    /// <remarks>
    /// Lagen im 1080p-Bezug (gemessen am 2026-09-26): Tune-Name im gruenen Balken oben
    /// links (y ~196, x &lt; 570), Tuner unter dem Autobild (y ~566), "Date Created"
    /// mit Wert (y ~678), Modell und Baujahr des Autos (y ~269 und ~304). Die gewaehlte
    /// Kachel traegt den Rahmen im Band y 760-950.
    /// </remarks>
    internal static TuneAufSchirm LiesListe(IReadOnlyList<OcrLine> z, Bitmap bild)
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
        var kachel = GewaehlteKachel(bild);
        return new TuneAufSchirm(name, creator, datum, $"{modell} / {jahr}", kachel,
                                 kachel.IsEmpty ? '?' : Symbol(bild, kachel));
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

    /// <summary>
    /// Ist in der Rueckfrage "Delete File" das "No" markiert und das "Yes" nicht? Die
    /// markierte Zeile ist schwarz mit gelbgruenem Rahmen, die andere weiss; "No" steht
    /// 54 Punkte unter "Yes" (Aufnahme 2026-09-26). "No" selbst liest die Texterkennung
    /// nicht -- zwei Zeichen sind ihr zu wenig --, darum wird an "Yes" gemessen.
    /// </summary>
    private bool NeinMarkiert()
    {
        PruefeWeiter();
        using var bild = Aufnahme();
        var zeilen = _ocr.Read(bild);
        if (Einordnen(zeilen) != Schirm.DeleteConfirm) { return false; }
        var ja = zeilen.FirstOrDefault(l => GameText.Gleich(l.Text, "yes", "Yes"));
        if (ja.Text is null) { return false; }
        return !Dunkel(bild, (int)ja.Y + 12) && Dunkel(bild, (int)ja.Y + 12 + 54);
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

    /// <summary>
    /// Zeigt die Tunes-Liste dieses Auto? Oben links steht "MODELL / JAHR MARKE". Das
    /// Jahr muss stimmen, und Marke plus Modell muessen dem Namen im Datensatz aehneln.
    /// </summary>
    private bool ListeGehoertZu(int auto, string zeile)
    {
        var name = _rat.CarIndexForId(auto) is { } ix ? _rat.RealCarName(ix) : null;
        if (name is null) { return false; }
        var teile = zeile.Split(" / ", 2);
        if (teile.Length < 2) { return false; }
        var m = System.Text.RegularExpressions.Regex.Match(teile[1], @"((?:19|20)\d\d)\s+(.+)$");
        if (!m.Success || CarGridReader.JahrVon(name) != int.Parse(m.Groups[1].Value)) { return false; }
        var ohneJahr = System.Text.RegularExpressions.Regex.Replace(name, @"\s*'\d\d\s*$", string.Empty);
        return Aehnlich(ohneJahr, m.Groups[2].Value + " " + teile[0]) >= 0.75;
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

    // ------------------------------------------------------------------ //
    // Vergleichen
    // ------------------------------------------------------------------ //

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

    /// <summary>Welcher Eintrag des Plans ist dieses Tune -- Name UND Tuner muessen passen.</summary>
    internal static StoredTune? Treffer(IEnumerable<StoredTune> offen, string name, string creator, string datum)
    {
        // Name UND Tuner. Die Schrift im gruenen Balken liest die Erkennung manchmal
        // schlecht ("A700" als "moo", Probelauf 2026-09-26): stimmen Tuner und
        // Erstelldatum genau, genuegt ein aehnlicher Name.
        var passend = offen.Where(t =>
                Aehnlich(t.Creator, creator) >= 0.8
                && (Aehnlich(t.Name, name) >= 0.85
                    || (Aehnlich(t.Name, name) >= 0.6 && Aehnlich(t.Creator, creator) >= 0.9 && AmTag(t, datum))))
            .ToList();
        if (passend.Count <= 1) { return passend.FirstOrDefault(); }
        // Mehrere gleichnamige: das Erstelldatum entscheidet, wenn es lesbar ist.
        foreach (var format in new[] { "dd/MM/yyyy", "MM/dd/yyyy", "dd.MM.yyyy", "yyyy-MM-dd" })
        {
            if (DateTime.TryParseExact(datum.Trim(), format, System.Globalization.CultureInfo.InvariantCulture,
                                       System.Globalization.DateTimeStyles.None, out var d))
            {
                var amTag = passend.FirstOrDefault(t => t.CreatedAt?.Date == d.Date);
                if (amTag is not null) { return amTag; }
            }
        }
        return passend[0];
    }

    /// <summary>
    /// Nennt der Dialog "File Options" dasselbe Tune? Name und Tuner -- oder, wenn der
    /// Tuner genau stimmt, ein Name, der ein Stueck des geplanten ist: die Erkennung
    /// liess "A700" auch im Dialog weg und las nur "Road AWD" (Probelauf 2026-09-26).
    /// </summary>
    internal static bool DialogPasst(string dName, string dCreator, StoredTune treffer)
    {
        if (Aehnlich(dCreator, treffer.Creator) < 0.8) { return false; }
        if (Aehnlich(dName, treffer.Name) >= 0.85) { return true; }
        if (Aehnlich(dCreator, treffer.Creator) < 0.9) { return false; }
        var gelesen = Norm(dName);
        return Aehnlich(dName, treffer.Name) >= 0.6
               || (gelesen.Length >= 5 && Norm(treffer.Name).Contains(gelesen, StringComparison.Ordinal));
    }

    /// <summary>Wurde das Tune an dem Tag erstellt, den die Liste nennt?</summary>
    private static bool AmTag(StoredTune t, string datum)
    {
        if (t.CreatedAt is not { } erstellt) { return false; }
        foreach (var format in new[] { "dd/MM/yyyy", "MM/dd/yyyy", "dd.MM.yyyy", "yyyy-MM-dd" })
        {
            if (DateTime.TryParseExact((datum ?? string.Empty).Trim(), format, System.Globalization.CultureInfo.InvariantCulture,
                                       System.Globalization.DateTimeStyles.None, out var d)
                && d.Date == erstellt.Date)
            {
                return true;
            }
        }
        return false;
    }

    // ------------------------------------------------------------------ //
    // Der Lauf
    // ------------------------------------------------------------------ //

    /// <summary>Den Plan abarbeiten. Gibt eine Zusammenfassung zurueck; bricht nie mit einer Ausnahme ab.</summary>
    public string Run(IReadOnlyList<StoredTune> plan)
    {
        Log($"=== start: {plan.Count} tunes on {plan.Select(t => t.CarId).Distinct().Count()} cars, probe={_probe}");
        var offen = plan.GroupBy(t => t.CarId).ToDictionary(g => g.Key, g => g.ToList());
        try
        {
            if (!_ocr.Available) { throw new Abbruch("no Windows OCR language installed"); }
            var blick = Sehen();
            if (blick.Schirm == Schirm.MyCars)
            {
                blick.Dispose();
                Taste(Esc, 700);
                blick = Warte("leaving My Cars", 5000, Schirm.CarsMenu);
            }
            if (blick.Schirm != Schirm.CarsMenu)
            {
                var s = blick.Schirm;
                blick.Dispose();
                throw new Abbruch($"start on the pause menu's CARS tab (My Cars / Upgrades & Tuning) -- found {s}");
            }
            // DAS AKTUELLE AUTO ZUERST: fuer das braucht es kein "Get In Car".
            var jetzt = AutoAusKopf(blick.Zeilen);
            blick.Dispose();
            if (jetzt is { } a && offen.ContainsKey(a))
            {
                Auto(a, offen, einsteigen: false);
            }
            // Nichts mehr offen: im Cars-Menue bleiben, nicht noch My Cars oeffnen.
            if (offen.Count > 0) { Durchs_Raster(offen); }
        }
        catch (Abbruch ab)
        {
            Log("STOP: " + ab.Message);
            return Fertig(ab.Message);
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex);
            return Fertig(ex.GetType().Name + ": " + ex.Message);
        }
        return Fertig(null);
    }

    private string Fertig(string? grund)
    {
        var text = (_probe ? $"Test run: {Geloescht} tunes would be deleted" : $"{Geloescht} tunes deleted")
                   + (Uebersprungen > 0 ? $", {Uebersprungen} left" : "")
                   + (grund is null ? "." : $" -- stopped: {grund}");
        Log("=== " + text);
        return text;
    }

    /// <summary>Das aktuelle Auto aus der Kopfzeile ("2009 Audi RS 6"), als Kennung.</summary>
    private int? AutoAusKopf(IReadOnlyList<OcrLine> z)
    {
        var kopf = z.FirstOrDefault(l => l.Y < 80 && System.Text.RegularExpressions.Regex.IsMatch(l.Text, @"^(19|20)\d\d\s"));
        if (kopf.Text is null)
        {
            Log("current car: no header line with a year -- " + string.Join(" | ", z.Where(l => l.Y < 90).Select(l => l.Text)));
            return null;
        }
        Log("current car header: " + kopf.Text);
        var m = System.Text.RegularExpressions.Regex.Match(kopf.Text.Trim(), @"^((?:19|20)\d\d)\s+(.+)$");
        if (!m.Success) { return null; }
        var jahr = int.Parse(m.Groups[1].Value);
        var index = _rat.MatchCarName($"{m.Groups[2].Value} '{jahr % 100:00}", 0.8);
        if (index is null) { return null; }
        var name = _rat.RealCarName(index.Value);
        return name is not null && CarGridReader.JahrVon(name) == jahr ? _rat.CarIdOf(index.Value) : null;
    }

    /// <summary>Die Karte unter dem Rahmen in My Cars, mit einem Abdruck ihrer ganzen Reihe.</summary>
    private sealed record KartenBlick(string Gelesen, int? Auto, Rectangle Rahmen, byte[]? Reihe);

    private KartenBlick Karte()
    {
        PruefeWeiter();
        using var bild = Aufnahme();
        var r = CarGridReader.LiesBild(bild, _ocr.Read, _rat);
        if (r is null) { return new KartenBlick(string.Empty, null, Rectangle.Empty, null); }
        var reihe = CarGridReader.Fingerabdruck(bild, new Rectangle(0, r.Value.Rahmen.Y, bild.Width, r.Value.Rahmen.Height));
        return new KartenBlick(r.Value.Gelesen, r.Value.Auto?.Ordinal, r.Value.Rahmen, reihe);
    }

    /// <summary>
    /// Hat sich etwas bewegt? DER RAHMEN STEHT, DAS RASTER LAEUFT DARUNTER (Aufnahme vom
    /// 2026-09-26): nach RECHTS sitzt der Rahmen am selben Fleck, die Karten ruecken
    /// nach. Darum zaehlt der Name, die Lage des Rahmens UND der Abdruck der Reihe --
    /// zwei gleiche Autos nebeneinander haben denselben Namen, aber andere Nachbarn.
    /// </summary>
    private static bool Bewegt(KartenBlick vorher, KartenBlick nachher) =>
        nachher.Gelesen.Length > 0
        && (nachher.Gelesen != vorher.Gelesen
            || Math.Abs(nachher.Rahmen.X - vorher.Rahmen.X) > 20
            || Math.Abs(nachher.Rahmen.Y - vorher.Rahmen.Y) > 20
            || !CarGridReader.Gleich(vorher.Reihe, nachher.Reihe));

    /// <summary>So lange nachlesen, bis zweimal dasselbe dasteht -- nicht mitten im Rollen lesen.</summary>
    private KartenBlick Stabil(KartenBlick k)
    {
        for (var i = 0; i < 4; i++)
        {
            Thread.Sleep(180);
            var n = Karte();
            if (n.Gelesen.Length > 0 && n.Gelesen == k.Gelesen) { return n; }
            if (n.Gelesen.Length > 0) { k = n; }
        }
        return k;
    }

    /// <summary>
    /// Einen Schritt in eine Richtung. Null, wenn sich nichts bewegt: Ende der Reihe (oder
    /// keine weitere Reihe darunter). Erst lange genug warten, dann ein zweites Mal
    /// druecken -- sonst risse ein langsamer Bildaufbau eine Karte mit.
    /// </summary>
    private KartenBlick? Schritt(KartenBlick jetzt, byte taste = Rechts)
    {
        for (var druck = 0; druck < 2; druck++)
        {
            Taste(taste, 300);
            foreach (var warte in new[] { 0, 350, 650 })
            {
                if (warte > 0) { Thread.Sleep(warte); }
                var n = Karte();
                // Ein erkanntes Auto ist fertig gelesen; nur Unerkanntes (vielleicht mitten
                // im Rollen gelesen) wird nachgelesen.
                if (Bewegt(jetzt, n)) { return n.Auto is null ? Stabil(n) : n; }
            }
        }
        return null;
    }

    /// <summary>
    /// My Cars nach Marken ablaufen und jedes Auto aus dem Plan bearbeiten.
    /// </summary>
    /// <remarks>
    /// ## Wie das Raster gebaut ist (Probelaeufe 2026-09-26)
    ///
    /// Jede Marke beginnt eine eigene Spaltengruppe, die Marken stehen alphabetisch
    /// (oben ist die Marke zu lesen). Innerhalb der Marke fuellen die Autos die
    /// Spalten von oben nach unten, drei je Spalte, das neueste zuerst: Abarth mit
    /// vier Autos ist eine volle Spalte und eine mit einem. RUNTER aus einer kurzen
    /// Spalte springt in die vorige -- das hielt der erste Lauf fuer das Ende des
    /// Rasters. Reihenweise ging es zwar, fand ein Auto in Reihe 2 aber erst nach einem
    /// ganzen Durchgang durch Reihe 1 (Hinweis des Nutzers: an der Marke sieht man,
    /// ob man zu weit ist).
    ///
    /// ## Der Weg
    ///
    /// Reihe 1 nach rechts, eine Spalte je Schritt. Hat die Marke der Spalte Autos im
    /// Plan, werden auch Reihe 2 und 3 dieser Spalte gelesen; liefert RUNTER eine
    /// Karte, die in dieser Marke schon gelesen war (oder eine andere Marke), ist die
    /// Spalte kuerzer, und es geht zurueck nach oben.
    ///
    /// Nach jedem bearbeiteten Auto hat sich das Raster verschoben: das Auto ist jetzt
    /// "CURRENT CAR" und fehlt, das vorige aktuelle Auto ist zurueck. Darum geht der
    /// Lauf an den Anfang derselben Marke zurueck -- gezaehlt, dann nach dem Alphabet
    /// nachgeregelt -- und liest die Marke noch einmal; Erledigtes ist nicht mehr im
    /// Plan und wird nur ueberlaufen.
    /// </remarks>
    private void Durchs_Raster(Dictionary<int, List<StoredTune>> offen)
    {
        var k = ErsteKarte();
        var spalte = 0;                 // Schritte nach rechts in Reihe 1, seit der ersten Karte
        var marke = string.Empty;
        var markeStart = 0;
        var inMarke = new HashSet<string>();   // Karten dieser Marke, schon gelesen
        var spitzen = new List<string>();      // die obersten Karten der Spalten dieser Marke
        var gelesen = 0;
        while (offen.Count > 0)
        {
            var m = MarkeVon(k.Gelesen);
            if (m != marke)
            {
                marke = m;
                markeStart = spalte;
                inMarke.Clear();
                spitzen.Clear();
            }
            inMarke.Add(k.Gelesen);
            spitzen.Add(k.Gelesen);
            if (++gelesen % 20 == 0) { Log($"  column {spalte + 1}: {k.Gelesen}"); }

            // Die Spalte: oben, und -- wenn die Marke im Plan steht -- darunter.
            var spalteKarten = new List<KartenBlick> { k };
            var gesprungen = false;
            if (MarkeImPlan(marke, offen))
            {
                var unten = k;
                for (var r = 1; r < 3; r++)
                {
                    var n = Schritt(unten, Runter);
                    if (n is null) { break; }
                    if (MarkeVon(n.Gelesen) != marke || inMarke.Contains(n.Gelesen))
                    {
                        gesprungen = true;   // kuerzere Spalte: RUNTER sprang anderswohin
                        break;
                    }
                    inMarke.Add(n.Gelesen);
                    spalteKarten.Add(n);
                    unten = n;
                }
            }

            // Ein Auto dieser Spalte im Plan? Dann hin, pruefen, bearbeiten.
            var ziel = spalteKarten.FindIndex(c => c.Auto is { } a && offen.ContainsKey(a));
            if (ziel >= 0)
            {
                var auto = spalteKarten[ziel].Auto!.Value;
                ZurSpitze(k, spitzen, spalte, marke, gesprungen, spalteKarten.Count);
                for (var r = 0; r < ziel; r++) { Taste(Runter, 350); }
                if (!Bestaetigt(auto))
                {
                    Log($"  {spalteKarten[ziel].Gelesen}: the card under the frame is not the same after a second look -- skipped");
                    offen.Remove(auto);
                    Uebersprungen++;
                }
                else
                {
                    Taste(Enter, 600);
                    Warte("opening the car", 5000, Schirm.ActionMenu).Dispose();
                    Auto(auto, offen, einsteigen: true);
                }
                if (offen.Count == 0) { break; }
                // Zurueck an den Anfang dieser Marke und sie noch einmal lesen.
                k = ZurMarke(marke, markeStart);
                spalte = markeStart;
                marke = string.Empty;
                continue;
            }

            // Nichts im Plan: zurueck nach oben, eine Spalte weiter.
            ZurSpitze(k, spitzen, spalte, marke, gesprungen, spalteKarten.Count);
            var weiter = Schritt(k);
            if (weiter is null) { break; }   // Ende des Rasters
            k = weiter;
            spalte++;
        }
        Log($"grid: {spalte + 1} columns walked");
        Uebersprungen += offen.Values.Sum(v => v.Count);
        foreach (var (auto, rest) in offen)
        {
            Log($"not found in My Cars: car {auto} ({rest.Count} tunes)");
        }
    }

    /// <summary>My Cars oeffnen und auf die erste Karte gehen (rechts neben "CURRENT CAR").</summary>
    private KartenBlick ErsteKarte()
    {
        ZuMyCars();
        Taste(Rechts, 450);
        var k = Stabil(Karte());
        if (k.Gelesen.Length == 0) { throw new Abbruch("no car card under the frame in My Cars"); }
        return k;
    }

    /// <summary>Die Marke einer Karte: "GIULIA QUADRIFOGLIO / 2017 ALFA ROMEO" -> "ALFA ROMEO".</summary>
    internal static string MarkeVon(string gelesen)
    {
        var m = System.Text.RegularExpressions.Regex.Match(gelesen ?? string.Empty, @"/\s*(?:19|20)\d\d\s+(.+)$");
        return m.Success ? Grundform(m.Groups[1].Value) : string.Empty;
    }

    /// <summary>Gross, ohne Akzente, einfache Leerzeichen -- fuer Vergleich und Reihenfolge.</summary>
    internal static string Grundform(string s)
    {
        var zerlegt = (s ?? string.Empty).Trim().ToUpperInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var raus = new System.Text.StringBuilder();
        foreach (var ch in zerlegt)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark) { continue; }
            raus.Append(ch);
        }
        return System.Text.RegularExpressions.Regex.Replace(raus.ToString(), @"\s+", " ");
    }

    /// <summary>Hat eine Marke noch Autos im Plan? Der Name im Datensatz beginnt mit der Marke.</summary>
    private bool MarkeImPlan(string marke, Dictionary<int, List<StoredTune>> offen)
    {
        if (marke.Length == 0) { return true; }   // unlesbar: lieber nachsehen
        foreach (var auto in offen.Keys)
        {
            var name = _rat.CarIndexForId(auto) is { } ix ? _rat.RealCarName(ix) : null;
            if (name is not null && Grundform(name).StartsWith(marke + " ", StringComparison.Ordinal)) { return true; }
        }
        return false;
    }

    /// <summary>
    /// Nach dem Lesen einer Spalte wieder auf ihre oberste Karte. Aus einem Sprung
    /// (kuerzere Spalte) fuehrt HOCH in eine andere Spalte -- dann nach der Reihenfolge
    /// der schon gelesenen Spaltenspitzen seitwaerts, und notfalls neu von vorn.
    /// </summary>
    private void ZurSpitze(KartenBlick spitze, List<string> spitzen, int spalte, string marke, bool gesprungen, int gelesen)
    {
        var hoch = gesprungen ? gelesen : gelesen - 1;
        for (var i = 0; i < hoch; i++) { Taste(Hoch, 300); }
        if (hoch == 0) { return; }
        Thread.Sleep(250);
        for (var versuch = 0; versuch < 6; versuch++)
        {
            var k = Stabil(Karte());
            if (k.Gelesen == spitze.Gelesen) { return; }
            var wo = spitzen.IndexOf(k.Gelesen);
            if (wo < 0 && k.Rahmen.Y > spitze.Rahmen.Y + 60) { Taste(Hoch, 300); continue; }   // noch nicht oben
            if (wo < 0) { break; }
            Taste(wo < spitzen.Count - 1 ? Rechts : Links, 350);
        }
        // Nicht wiedergefunden: von vorn bis zu dieser Spalte.
        var neu = ZurMarke(marke, spalte);
        if (neu.Gelesen != spitze.Gelesen)
        {
            for (var i = 0; i < 8 && Karte().Gelesen != spitze.Gelesen; i++) { Taste(Rechts, 350); }
        }
    }

    /// <summary>
    /// Von vorn an den Anfang einer Marke: <paramref name="schaetzung"/> Schritte nach
    /// rechts, dann nach dem Alphabet nachgeregelt, bis links davon eine andere Marke
    /// steht.
    /// </summary>
    private KartenBlick ZurMarke(string marke, int schaetzung)
    {
        var k = ErsteKarte();
        for (var i = 0; i < schaetzung; i++) { Taste(Rechts, 130); }
        Thread.Sleep(450);
        k = Stabil(Karte());
        if (marke.Length == 0) { return k; }
        for (var schritt = 0; schritt < 80; schritt++)
        {
            var m = MarkeVon(k.Gelesen);
            var vergleich = string.CompareOrdinal(m, marke);
            if (m.Length > 0 && vergleich < 0)
            {
                // Noch vor der Marke: rechts. Beim ersten Schritt in die Marke ist das ihr Anfang.
                var n = Schritt(k) ?? throw new Abbruch($"brand {marke} not found in My Cars");
                k = n;
                var nm = MarkeVon(k.Gelesen);
                if (nm == marke) { return k; }
                // Gleich hinter der Marke gelandet: sie ist weg (ihr einziges Auto ist jetzt
                // das aktuelle). Dann geht es hier weiter.
                if (nm.Length > 0 && string.CompareOrdinal(nm, marke) > 0) { return k; }
                continue;
            }
            // In der Marke oder dahinter: links, bis links davon eine fruehere Marke steht.
            var l = Schritt(k, Links);
            if (l is null) { return k; }   // Anfang des Rasters
            var lm = MarkeVon(l.Gelesen);
            if (vergleich == 0 && lm.Length > 0 && string.CompareOrdinal(lm, marke) < 0)
            {
                return Schritt(l) ?? k;    // einen zurueck nach rechts: der Anfang der Marke
            }
            k = l;
        }
        throw new Abbruch($"could not find the start of {marke} again");
    }

    /// <summary>Vor Enter: zweimal nachsehen, ob unter dem Rahmen wirklich dieses Auto steht.</summary>
    private bool Bestaetigt(int auto)
    {
        Thread.Sleep(300);
        var a = Karte();
        Thread.Sleep(300);
        var b = Karte();
        return a.Auto == auto && b.Auto == auto && a.Gelesen == b.Gelesen;
    }

    /// <summary>Vom Cars-Menue nach My Cars.</summary>
    private void ZuMyCars()
    {
        var blick = Warte("back to the Cars menu", 8000, Schirm.CarsMenu, Schirm.MyCars);
        if (blick.Schirm == Schirm.MyCars) { blick.Dispose(); return; }
        blick = Waehle(blick, "my_cars", "My Cars", Schirm.CarsMenu);
        blick.Dispose();
        Taste(Enter, 800);
        Warte("opening My Cars", 8000, Schirm.MyCars).Dispose();
    }

    /// <summary>Ein Auto bearbeiten: einsteigen, Tunes-Liste oeffnen, Plan-Tunes loeschen, zurueck.</summary>
    private void Auto(int auto, Dictionary<int, List<StoredTune>> offen, bool einsteigen)
    {
        var name = _rat.CarIndexForId(auto) is { } ix ? _rat.RealCarName(ix) ?? $"car {auto}" : $"car {auto}";
        _melden($"{name}: {offen[auto].Count} tunes");
        Log($"car {auto} {name}: {offen[auto].Count} planned");
        if (einsteigen)
        {
            var aktion = Warte("action menu", 4000, Schirm.ActionMenu);
            aktion = Waehle(aktion, "get_in_car", "Get In Car", Schirm.ActionMenu);
            aktion.Dispose();
            Taste(Enter, 1500);
        }
        var menue = Warte("Cars menu after getting in", 30000, Schirm.CarsMenu);
        menue = Waehle(menue, "upgrades_tuning", "Upgrades & Tuning", Schirm.CarsMenu);
        menue.Dispose();
        Taste(Enter, 900);
        var upgrades = Warte("Upgrades menu", 8000, Schirm.Upgrades);
        upgrades = Waehle(upgrades, "my_tuning_setups", "My Tuning Setups", Schirm.Upgrades);
        upgrades.Dispose();
        Taste(Enter, 1200);
        var liste = Warte("tune list", 10000, Schirm.TunesList);
        // DIE LISTE MUSS ZUM AUTO GEHOEREN. Auf einem falschen Auto koennte ein Tune mit
        // demselben Namen vom selben Tuner stehen ("A Road Strong" gibt es fuer viele
        // Autos) -- und das waere nicht das geplante.
        var zeile = LiesListe(liste.Zeilen, liste.Bild).AutoZeile;
        liste.Dispose();
        if (!ListeGehoertZu(auto, zeile))
        {
            Log($"  the tune list shows '{zeile}', expected {name} -- nothing touched on this car");
            Uebersprungen += offen[auto].Count;
            offen.Remove(auto);
            Taste(Esc, 700);
            Warte("back to Upgrades", 6000, Schirm.Upgrades).Dispose();
            Taste(Esc, 700);
            Warte("back to the Cars menu", 6000, Schirm.CarsMenu).Dispose();
            if (++_autos >= MaxAutos) { throw new Abbruch($"limit of {MaxAutos} cars reached"); }
            return;
        }

        Kacheln(auto, offen[auto]);
        if (offen[auto].Count == 0) { offen.Remove(auto); }
        else
        {
            Log($"  car {auto}: {offen[auto].Count} planned tunes not found in the list");
            Uebersprungen += offen[auto].Count;
            offen.Remove(auto);
        }
        // Zurueck: Liste -> Upgrades -> Cars-Menue.
        Taste(Esc, 700);
        Warte("back to Upgrades", 6000, Schirm.Upgrades).Dispose();
        Taste(Esc, 700);
        Warte("back to the Cars menu", 6000, Schirm.CarsMenu).Dispose();
        if (++_autos >= MaxAutos) { throw new Abbruch($"limit of {MaxAutos} cars reached"); }
    }

    /// <summary>
    /// Die Tunes-Liste lesen, sobald sie fertig aufgebaut ist: mit Tuner und gewaehlter
    /// Kachel. Direkt nach dem Loeschen steht der Name schon, Tuner und Kachel fehlen noch
    /// -- wer dann schon weitergeht, ueberspringt das nachgerueckte Tune.
    /// </summary>
    private (Blick Blick, TuneAufSchirm Tune) FertigeListe()
    {
        var bis = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var blick = Warte("tune list", 8000, Schirm.TunesList);
            var t = LiesListe(blick.Zeilen, blick.Bild);
            if ((!t.Kachel.IsEmpty && t.Creator.Length > 0 && t.Name.Length > 0) || DateTime.UtcNow > bis)
            {
                return (blick, t);
            }
            blick.Dispose();
            Thread.Sleep(300);
        }
    }

    /// <summary>Die Kacheln der Tunes-Liste von links nach rechts durchgehen.</summary>
    private void Kacheln(int auto, List<StoredTune> offen)
    {
        var vorige = Rectangle.Empty;
        var vorigerName = string.Empty;
        var gleichBlieb = 0;
        // DIE LISTE LAEUFT IM KREIS: nach der letzten Kachel fuehrt RECHTS zur ersten
        // (Probelauf 2026-09-26, zwei Tunes: 200 Schritte hin und her). Eine Kachel, die
        // schon einmal da war, heisst: alle gesehen.
        var gesehen = new HashSet<string>();
        for (var schritt = 0; schritt < 200 && offen.Count > 0; schritt++)
        {
            var (blick, t) = FertigeListe();
            if (!gesehen.Add($"{Norm(t.Name)}|{Norm(t.Creator)}|{t.Datum}"))
            {
                blick.Dispose();
                Log($"  tile {schritt + 1}: back at '{t.Name}' -- the list has been seen in full");
                return;
            }
            if (t.Kachel == vorige && t.Name == vorigerName)
            {
                if (++gleichBlieb >= 2) { return; }   // RIGHT bewegt nichts: Ende der Liste
            }
            else { gleichBlieb = 0; }
            vorige = t.Kachel;
            vorigerName = t.Name;

            var treffer = Treffer(offen, t.Name, t.Creator, t.Datum);
            Log($"  tile {schritt + 1}: '{t.Name}' by '{t.Creator}', created {t.Datum}, icon {t.Symbol}"
                + (treffer is null ? " -- not in the plan" : ""));
            Merke(blick.Bild, $"tile {schritt + 1}");
            if (treffer is null || t.Symbol == '-')
            {
                if (treffer is not null) { Log($"  '{t.Name}' by {t.Creator} is the applied tune (grey) -- kept"); offen.Remove(treffer); Uebersprungen++; }
                blick.Dispose();
                Taste(Rechts, 350);
                continue;
            }
            Log($"  list: '{t.Name}' by {t.Creator}, created {t.Datum}, icon {t.Symbol} -> plan {treffer.Folder}");
            blick.Dispose();
            Taste(Enter, 700);
            var dialog = Warte("File Options", 6000, Schirm.FileOptions);
            var (dName, dCreator, _) = LiesDialog(dialog.Zeilen);
            if (!DialogPasst(dName, dCreator, treffer))
            {
                dialog.Dispose();
                Log($"  dialog says '{dName}' by {dCreator} -- does not match, cancelled");
                Taste(Esc, 600);
                Taste(Rechts, 350);
                continue;
            }
            dialog = Waehle(dialog, "delete", "Delete", Schirm.FileOptions);
            dialog.Dispose();
            Taste(Enter, 700);
            var frage = Warte("Delete File", 6000, Schirm.DeleteConfirm);
            if (_probe)
            {
                // DER PROBELAUF GEHT BIS ZUR RUECKFRAGE (Wunsch vom 2026-09-26) und waehlt
                // "No". ESC tut dort NICHTS (Probelauf 2026-09-26: die Rueckfrage kennt nur
                // "Select"). Also RUNTER auf "No" -- und Enter erst, wenn das Bild zeigt,
                // dass "No" markiert ist und "Yes" nicht. Sonst anhalten, ohne zu druecken.
                frage.Dispose();
                Taste(Runter, 600);
                if (!NeinMarkiert())
                {
                    throw new Abbruch("could not move to \"No\" on the confirmation -- choose No yourself");
                }
                Taste(Enter, 700);
                Log($"  PROBE: confirmation for '{dName}' by {dCreator} reached -- answered No");
                Geloescht++;
                offen.Remove(treffer);
                var danach = Warte("after cancelling", 6000, Schirm.FileOptions, Schirm.TunesList);
                var zurueckInListe = danach.Schirm == Schirm.TunesList;
                danach.Dispose();
                if (!zurueckInListe)
                {
                    Taste(Esc, 700);
                    Warte("back to the tune list", 6000, Schirm.TunesList).Dispose();
                }
                Taste(Rechts, 350);
                continue;
            }
            frage = Waehle(frage, "yes", "Yes", Schirm.DeleteConfirm);
            frage.Dispose();
            Taste(Enter, 900);
            Warte("after deleting", 15000, Schirm.TunesList).Dispose();
            Geloescht++;
            offen.Remove(treffer);
            Log($"  DELETED '{treffer.Name}' by {treffer.Creator} ({treffer.Folder})");
            _melden($"{Geloescht} deleted");
            // Nach dem Loeschen rueckt die naechste Kachel an diese Stelle -- nicht weiter.
            vorige = Rectangle.Empty;
        }
    }
}
