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

    public TuneDeleter(RivalsAdvisor rat, bool probelauf, Action<string> melden, CancellationToken stop)
    {
        _rat = rat;
        _probe = probelauf;
        _melden = melden;
        _stop = stop;
        var dir = Path.Combine(Path.GetTempPath(), "forza-overlay");
        Directory.CreateDirectory(dir);
        _logPfad = Path.Combine(dir, "tune_delete.log");
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

    /// <summary>Welcher Schirm ist das -- an den Texten, die nur er hat.</summary>
    internal static Schirm Einordnen(IReadOnlyList<OcrLine> z)
    {
        bool Hat(string s) => z.Any(l => l.Text.Contains(s, StringComparison.OrdinalIgnoreCase));
        if (Hat("Delete File") && Hat("Are you sure")) { return Schirm.DeleteConfirm; }
        if (Hat("File Options")) { return Schirm.FileOptions; }
        if (Hat("Please Wait")) { return Schirm.Warten; }
        if (Hat("Select an Action")) { return Schirm.ActionMenu; }
        if (Hat("Date Created") && Hat("Tuner Rank"))
        {
            return Hat("TRENDING") || Hat("ALL TIME GREATS") ? Schirm.TuneBrowser : Schirm.TunesList;
        }
        if (Hat("My Tuning Setups") && Hat("Custom Tuning")) { return Schirm.Upgrades; }
        if (Hat("Upgrades & Tuning") && Hat("Designs & Paints")) { return Schirm.CarsMenu; }
        if (Hat("Jump to Manufacturer") || Hat("Toggle Stats")) { return Schirm.MyCars; }
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
            if (ziele.Contains(letzter.Schirm)) { return letzter; }
            if (DateTime.UtcNow > bis)
            {
                var s = letzter.Schirm;
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

    private static OcrLine? Zeile(IReadOnlyList<OcrLine> z, string text)
    {
        foreach (var l in z) { if (l.Text.Trim().Equals(text, StringComparison.OrdinalIgnoreCase)) { return l; } }
        foreach (var l in z) { if (l.Text.Contains(text, StringComparison.OrdinalIgnoreCase)) { return l; } }
        return null;
    }

    /// <summary>In einem Menue den Eintrag anwaehlen: hoch oder runter, bis sein Rahmen steht.</summary>
    private Blick Waehle(Blick blick, string eintrag, Schirm schirm)
    {
        for (var versuch = 0; versuch < 16; versuch++)
        {
            var gefunden = Zeile(blick.Zeilen, eintrag)
                           ?? throw new Abbruch($"menu entry '{eintrag}' not found on {blick.Schirm}");
            var ziel = gefunden;
            if (Markiert(blick.Bild, ziel)) { return blick; }
            // Welcher Eintrag ist markiert? Dann in die richtige Richtung.
            var markiert = blick.Zeilen.Where(l => Math.Abs(l.X - ziel.X) < 250 && Math.Abs(l.Y - ziel.Y) < 400
                                                   && !l.Text.Contains("File Options") && !l.Text.Contains("Delete File")
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
        var datumZeile = z.FirstOrDefault(l => l.Text.Contains("Date Created", StringComparison.OrdinalIgnoreCase));
        var datum = datumZeile.Text is null ? string.Empty
            : string.Join(" ", z.Where(l => Math.Abs(l.Y - datumZeile.Y) < 12 && l.X > datumZeile.X + 100 && l.X < 560)
                               .Select(l => l.Text.Trim()));
        var modell = In(255, 285, 560);
        var jahr = In(290, 320, 560);
        var kachel = GewaehlteKachel(bild);
        return new TuneAufSchirm(name, creator, datum, $"{modell} / {jahr}", kachel,
                                 kachel.IsEmpty ? '?' : Symbol(bild, kachel));
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

    /// <summary>Name und Tuner im Dialog "File Options".</summary>
    internal static (string Name, string Creator, string Auto) LiesDialog(IReadOnlyList<OcrLine> z)
    {
        var titel = z.FirstOrDefault(l => l.Text.Contains("File Options", StringComparison.OrdinalIgnoreCase));
        var name = titel.Text is null ? string.Empty
            : z.Where(l => l.Y > titel.Y + 40 && l.Y < titel.Y + 110).OrderBy(l => l.Y).Select(l => l.Text.Trim()).FirstOrDefault() ?? "";
        var creatorKopf = z.FirstOrDefault(l => l.Text.Trim().Equals("Creator", StringComparison.OrdinalIgnoreCase));
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
        var passend = offen.Where(t => Aehnlich(t.Name, name) >= 0.85 && Aehnlich(t.Creator, creator) >= 0.8).ToList();
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
            Durchs_Raster(offen);
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
        if (kopf.Text is null) { return null; }
        var m = System.Text.RegularExpressions.Regex.Match(kopf.Text.Trim(), @"^((?:19|20)\d\d)\s+(.+)$");
        if (!m.Success) { return null; }
        var jahr = int.Parse(m.Groups[1].Value);
        var index = _rat.MatchCarName($"{m.Groups[2].Value} '{jahr % 100:00}", 0.8);
        if (index is null) { return null; }
        var name = _rat.RealCarName(index.Value);
        return name is not null && CarGridReader.JahrVon(name) == jahr ? _rat.CarIdOf(index.Value) : null;
    }

    /// <summary>Die Karte unter dem Rahmen in My Cars.</summary>
    private (string Gelesen, int? Auto, Rectangle Rahmen) Karte()
    {
        PruefeWeiter();
        using var bild = Aufnahme();
        var r = CarGridReader.LiesBild(bild, _ocr.Read, _rat);
        return r is null ? (string.Empty, null, Rectangle.Empty) : (r.Value.Gelesen, r.Value.Auto?.Ordinal, r.Value.Rahmen);
    }

    /// <summary>
    /// My Cars Spalte fuer Spalte ablaufen und jedes Auto aus dem Plan bearbeiten.
    /// </summary>
    /// <remarks>
    /// Nach jedem bearbeiteten Auto beginnt das Raster wieder vorn ("CURRENT CAR").
    /// Darum merkt sich der Lauf je Spalte deren oberste Karte und spult bis dorthin
    /// vor -- erst schnell mit gezaehlten Tastendruecken, dann lesend nachgeregelt.
    /// </remarks>
    private void Durchs_Raster(Dictionary<int, List<StoredTune>> offen)
    {
        ZuMyCars();
        Taste(Rechts, 350);   // weg von "CURRENT CAR"
        var spalte = 1;
        var vorigeOben = string.Empty;
        while (offen.Count > 0)
        {
            var oben = Karte();
            if (oben.Gelesen.Length == 0) { throw new Abbruch("no car card under the frame in My Cars"); }
            if (oben.Gelesen == vorigeOben) { break; }   // RIGHT bewegt nichts mehr: Ende des Rasters
            // Die Spalte lesen: oben, Mitte, unten.
            var zeilen = new List<(string Gelesen, int? Auto)> { (oben.Gelesen, oben.Auto) };
            for (var r = 1; r < 3; r++)
            {
                Taste(Runter, 300);
                var k = Karte();
                if (k.Gelesen.Length == 0 || k.Gelesen == zeilen[^1].Gelesen) { break; }
                // Sprang der Rahmen in eine andere Spalte (weiter oben als erwartet)? Dann nicht mitzaehlen.
                zeilen.Add((k.Gelesen, k.Auto));
            }
            for (var r = zeilen.Count - 1; r > 0; r--) { Taste(Hoch, 200); }
            Log($"column {spalte}: {string.Join(" | ", zeilen.Select(z => z.Gelesen))}");
            for (var r = 0; r < zeilen.Count; r++)
            {
                if (zeilen[r].Auto is not { } auto || !offen.ContainsKey(auto)) { continue; }
                for (var i = 0; i < r; i++) { Taste(Runter, 250); }
                var pruef = Karte();
                if (pruef.Auto != auto)
                {
                    Log($"  row {r} is '{pruef.Gelesen}', expected car {auto} -- skipped");
                    for (var i = 0; i < r; i++) { Taste(Hoch, 200); }
                    continue;
                }
                Taste(Enter, 600);
                var aktion = Warte("opening the car", 5000, Schirm.ActionMenu);
                aktion.Dispose();
                Auto(auto, offen, einsteigen: true);
                // Zurueck an diese Spalte.
                ZuMyCars();
                Vorspulen(spalte, oben.Gelesen);
            }
            vorigeOben = oben.Gelesen;
            Taste(Rechts, 300);
            spalte++;
        }
        Uebersprungen += offen.Values.Sum(v => v.Count);
        foreach (var (auto, rest) in offen)
        {
            Log($"not found in My Cars: car {auto} ({rest.Count} tunes)");
        }
    }

    /// <summary>Vom Cars-Menue nach My Cars.</summary>
    private void ZuMyCars()
    {
        var blick = Warte("back to the Cars menu", 8000, Schirm.CarsMenu, Schirm.MyCars);
        if (blick.Schirm == Schirm.MyCars) { blick.Dispose(); return; }
        blick = Waehle(blick, "My Cars", Schirm.CarsMenu);
        blick.Dispose();
        Taste(Enter, 800);
        Warte("opening My Cars", 8000, Schirm.MyCars).Dispose();
    }

    /// <summary>Im Raster bis zur Spalte mit dieser obersten Karte vorspulen.</summary>
    private void Vorspulen(int spalte, string oben)
    {
        for (var i = 0; i < spalte; i++) { Taste(Rechts, 110); }
        Thread.Sleep(300);
        for (var nachregeln = 0; nachregeln < 12; nachregeln++)
        {
            var k = Karte();
            if (k.Gelesen == oben) { return; }
            Taste(Rechts, 300);
        }
        throw new Abbruch($"could not find the column starting with '{oben}' again");
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
            aktion = Waehle(aktion, "Get In Car", Schirm.ActionMenu);
            aktion.Dispose();
            Taste(Enter, 1500);
        }
        var menue = Warte("Cars menu after getting in", 30000, Schirm.CarsMenu);
        menue = Waehle(menue, "Upgrades & Tuning", Schirm.CarsMenu);
        menue.Dispose();
        Taste(Enter, 900);
        var upgrades = Warte("Upgrades menu", 8000, Schirm.Upgrades);
        upgrades = Waehle(upgrades, "My Tuning Setups", Schirm.Upgrades);
        upgrades.Dispose();
        Taste(Enter, 1200);
        var liste = Warte("tune list", 10000, Schirm.TunesList);
        liste.Dispose();

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
        for (var schritt = 0; schritt < 200 && offen.Count > 0; schritt++)
        {
            var (blick, t) = FertigeListe();
            if (t.Kachel == vorige && t.Name == vorigerName)
            {
                if (++gleichBlieb >= 2) { return; }   // RIGHT bewegt nichts: Ende der Liste
            }
            else { gleichBlieb = 0; }
            vorige = t.Kachel;
            vorigerName = t.Name;

            var treffer = Treffer(offen, t.Name, t.Creator, t.Datum);
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
            if (Aehnlich(dName, treffer.Name) < 0.85 || Aehnlich(dCreator, treffer.Creator) < 0.8)
            {
                dialog.Dispose();
                Log($"  dialog says '{dName}' by {dCreator} -- does not match, cancelled");
                Taste(Esc, 600);
                Taste(Rechts, 350);
                continue;
            }
            if (_probe)
            {
                dialog.Dispose();
                Log($"  PROBE: would delete '{dName}' by {dCreator}");
                Geloescht++;
                offen.Remove(treffer);
                Taste(Esc, 600);
                Taste(Rechts, 350);
                continue;
            }
            dialog = Waehle(dialog, "Delete", Schirm.FileOptions);
            dialog.Dispose();
            Taste(Enter, 700);
            var frage = Warte("Delete File", 6000, Schirm.DeleteConfirm);
            frage = Waehle(frage, "Yes", Schirm.DeleteConfirm);
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
