using System.Drawing;
using System.Drawing.Imaging;
using System.Text.RegularExpressions;
using ForzaHaptics.Tester.Rivals;

namespace ForzaHaptics.Tester.Scan;

/// <summary>Die Schirme auf dem Weg zu einer Bestenliste, wie der Scanner sie auseinanderhaelt.</summary>
internal enum ScanSchirm
{
    Unbekannt, Laden, Ladeschirm, Titel, Weiter, SerienUpdate, Garage, Pause, Karte, OnlineReiter,
    ForzaLink, RivalsHub, Kategorien, Streckenliste, RivalDetail, Klassenschirm, Bestenliste,
    Bestaetigung, ServerFehler, Abgestuerzt,
}

/// <summary>
/// Was der Scanner vom Schirm liest: welcher Schirm, welche Strecke, welche Klasse, wo der
/// gelbe Rahmen steht.
/// </summary>
/// <remarks>
/// Die Muster sind die des Python-Werkzeugs (`scripts/forza_navigator.ps1`, Get-ScreenState),
/// in derselben Reihenfolge -- jede Zeile dort ist eine Lehre aus einem verlorenen Lauf.
/// Heute nur Englisch, wie das alte Werkzeug; die Spielsprachen kommen mit den
/// Stringtabellen (GameText), sobald der Scanner laeuft.
///
/// Streckennamen: der Titel der gewaehlten Strecke ist ab ~25 Zeichen beschnitten und
/// LAEUFT ('Tateyama Alpine Cross-Coun' -> 'ateyama Alpine Cross-Count'), und die
/// Texterkennung verstuemmelt ('Edogawa Cross-Cdufitry Cire'). Eine Lesung wird darum
/// nicht mit einem Namen verglichen, sondern im Karussell VERORTET: der Kopf (Ort ohne
/// Gattungswort) ueber die Editierdistanz, bei langen Titeln auch verschoben; gleiche
/// Koepfe (Shimanoyama Circuit / Sprint) trennt die Art des Gattungsworts. Dieselbe
/// Regel steht in `scripts/build_scan_routes.py`, das sie gegen alle 86 Namen prueft.
/// </remarks>
internal static class ScanScreen
{
    private const RegexOptions O = RegexOptions.CultureInvariant;

    /// <summary>Die Klassen, wie die Texterkennung sie schreibt: nur Ziffern sind locker, S1 und S2 bleiben getrennt.</summary>
    internal const string KlassenAlternative = "D|C|B|A|S[1IilL]|S[2Zz]|R|X";

    /// <summary>
    /// '"D" Performance Class' -- auch so, wie die Texterkennung es auf hellen Karten liefert:
    /// '"D!'+erform\ufffdnce Class]' (20260914_142540). Der Buchstabe steht vorn und ist das
    /// Einzige, was zaehlt; "erf" und "Clas" machen die Zeile eindeutig.
    /// </summary>
    /// <remarks>
    /// Die Anfuehrungszeichen um den Buchstaben liest die Texterkennung auf hellen Karten als
    /// Buchstaben: 'S"DI' Performance', 'ITD" Performance' (Shinjuku Gyoen, live 2026-10-03,
    /// sechs Minuten lang). Darum duerfen vor und hinter dem Buchstaben bis zu drei solche
    /// Zeichen kleben -- aber kein Buchstabe davor, der zu einem Wort gehoert.
    /// </remarks>
    internal const string KlassenMuster =
        @"(?<![A-Za-z0-9])[SITtl|'""!“”]{0,3}(" + KlassenAlternative + @")[Il|'""!“”]{0,3}\W{0,4}P?[eo]rf\S*\s+Cl?ass?";

    /// <summary>Die Reihenfolge der Klassenleiste: so weit liegt R rechts von D.</summary>
    internal static readonly string[] Klassen = { "D", "C", "B", "A", "S1", "S2", "R", "X" };

    private static readonly (ScanSchirm Schirm, Regex Muster)[] Reihenfolge =
    {
        // Ein abgestuerztes Spiel zuerst: dagegen zu druecken ist verschwendet.
        (ScanSchirm.Abgestuerzt, new(@"terminated\s+unexpectedly|has\s+\w+\s+an\s+unexpected\s+error|Video\s+Card\s+Crash", O)),
        // Dann der Serverfehler: der einzige Schirm, bei dem Warten nachweislich sinnlos ist.
        (ScanSchirm.ServerFehler, new(@"Server\s+Error|error\s+communicating\s+with\s+the\s+server", O)),
        (ScanSchirm.Bestaetigung, new(@"Are you sure you want to|Return to Horizon Solo\?", O)),
        // Nicht 'Change Rival': das steht auch auf dem Klassenschirm. Filter und Spielerzahl
        // gibt es nur auf der Tabelle selbst.
        (ScanSchirm.Bestenliste, new(@"Filter:\s*Global|[0-9][0-9,]{2,}\s+Players", O)),
        (ScanSchirm.Klassenschirm, new(KlassenMuster, O)),
        // Derselbe Schirm wie der Klassenschirm, nur ohne lesbare Klassenzeile (weiss auf
        // heller Karte). 'Time to Beat' steht nur dort; 'Gap to Rival' auch auf der Leiste.
        (ScanSchirm.RivalDetail, new(@"Time\s+to\s+Beat", O)),
        // "Roule Length": so liest die Windows-Texterkennung es auf hellen Karten (6 von 96 Listen).
        (ScanSchirm.Streckenliste, new(@"Rou[lt]e\s+Length|Select\s+Route|Choose\s+Route", O)),
        (ScanSchirm.Kategorien, new(@"Road Racing|Street Racing|Cross-Country|Dirt Racing|Drag Racing|Touge", O)),
        (ScanSchirm.RivalsHub, new(@"My Rivals|Showcase Rivals|Monthly Rivals", O)),
        (ScanSchirm.ForzaLink, new(@"Forza LINK Selection|The Eliminator|Horizon Stunt Party", O)),
        (ScanSchirm.OnlineReiter, new(@"Top the Leaderboards|Horizon Open|Horizon Tour", O)),
        (ScanSchirm.Karte, new(@"Close Map|Toggle Map Regions|Set Route", O)),
        (ScanSchirm.Pause, new(@"CREATIVE HUB|Reset Car Position|Exit Game", O)),
        (ScanSchirm.Garage, new(@"CUSTOMISABLE GARAGE|Forzavista", O)),
        (ScanSchirm.Weiter, new(@"(?m)^\s*Continue\s*$", O)),
        (ScanSchirm.Titel, new(@"Start\s*Game|Accessibility.{0,2}Settings", O)),
        // Nach jedem Spielstart modal; ENTER blaettert durch. Hinter den Menues, weil das
        // Pausenmenue 'Festival Playlist' ebenfalls zeigt.
        (ScanSchirm.SerienUpdate, new(@"Series\s+Update|Series\s+Ends\s+In|Festival\s+Playlist", O)),
        // Die Ladekarten wechseln staendig; zuletzt, damit ein echtes Menue immer gewinnt.
        (ScanSchirm.Ladeschirm, new(@"LOADING|PLEASE WAIT|Favourite Radio Station|HORIZON PULSE|Rivals Beaten|Number of Podiums|Ultimate Draft Skills|Skill Points|Distance Driven", O)),
    };

    /// <summary>Zeilen, die kein Menue sind: Windows-Hinweis und die FPS-Anzeigen des Rechners.</summary>
    private static readonly Regex Rauschen =
        new(@"Activate Windows|Go to Settings to activate Windows|^\s*\d+\s*FPS\b|\bFPS\s+\d+|\bGPU:?\s*\d+\s*%", O);

    // Nicht am Zeilenanfang verankert: auf dem Klassenschirm steht es hinter Klasse und
    // Jahreszeit ('"D" Performance Class / Summer Season / Route Length: 6.9 KM').
    private static readonly Regex RouteLength = new(@"Rou[lt]e\s+Length", O);
    private static readonly Regex LaengeWert = new(@"Rou[lt]e\s+Length\W*(\d{1,2})\s*[.,]\s*(\d)", O);
    private static readonly Regex KmWert = new(@"^\W*(\d{1,2})\s*[.,]\s*(\d)\s*K\s*M\b", O | RegexOptions.IgnoreCase);
    private static readonly Regex RoutesKopf = new(@"^\s*Routes\s*$", O);
    private static readonly Regex RivalsKopf = new(@"^\s*Rivals\s*$", O);
    private static readonly Regex KlassenZeile = new(KlassenMuster, O);
    private static readonly Regex RoutesAvailable = new(@"Routes\s+Available", O);

    /// <summary>Alle Zeilen von oben nach unten als ein Text.</summary>
    public static string Text(IReadOnlyList<OcrLine> zeilen) =>
        string.Join("\n", zeilen.OrderBy(z => z.Y).ThenBy(z => z.X).Select(z => z.Text));

    public static ScanSchirm Einordnen(IReadOnlyList<OcrLine> zeilen)
    {
        // Jeder Ja/Nein-Dialog ist eine Bestaetigung ("SETUP FILE LOCKED ... remove the setup?").
        if (ScanDialog.IstJaNein(zeilen)) { return ScanSchirm.Bestaetigung; }
        var text = Text(zeilen);
        foreach (var (schirm, muster) in Reihenfolge)
        {
            if (!muster.IsMatch(text)) { continue; }
            // Kategorienamen stehen auch in Streckentiteln ("Legend Island Cross-Country"):
            // der Kategorieschirm ist es nur mit "N Routes Available" unter den Kacheln.
            if (schirm == ScanSchirm.Kategorien && !RoutesAvailable.IsMatch(text)) { continue; }
            // "Route Length" steht auf Streckenliste UND Klassenschirm (siehe route-carousel-is-solved).
            // Die Liste hat die Kopfzeile "Routes" oder Kilometer-Kacheln; der Klassenschirm die
            // Kopfzeile "Rivals" -- dann ist er es, nur mit unlesbarer Klassenzeile: warten.
            // Als Streckenliste haette der Navigator RECHTS auf der Klassenleiste gedrueckt.
            if (schirm == ScanSchirm.Streckenliste && !zeilen.Any(z => RoutesKopf.IsMatch(z.Text)) && Leiste(zeilen).Count(v => v is not null) < 2)
            {
                if (zeilen.Any(z => RivalsKopf.IsMatch(z.Text))) { return ScanSchirm.RivalDetail; }
                continue;
            }
            return schirm;
        }
        // Ladekarten sind ein, zwei Zeilen; jedes echte Menue hat Reiter, Liste und Tastenleiste.
        var sichtbar = zeilen.Count(z => !string.IsNullOrWhiteSpace(z.Text) && !Rauschen.IsMatch(z.Text));
        return sichtbar <= 3 ? ScanSchirm.Laden : ScanSchirm.Unbekannt;
    }

    /// <summary>
    /// Der Titel der gewaehlten Strecke: die Zeile ueber "Route Length" (Streckenliste UND
    /// Klassenschirm), sonst die unter der Kopfzeile "Routes".
    /// </summary>
    public static string? GewaehlteStrecke(IReadOnlyList<OcrLine> zeilen)
    {
        var sortiert = zeilen.Where(z => !string.IsNullOrWhiteSpace(z.Text)).OrderBy(z => z.Y).ThenBy(z => z.X).ToList();
        for (var i = 1; i < sortiert.Count; i++)
        {
            if (RouteLength.IsMatch(sortiert[i].Text)) { return sortiert[i - 1].Text.Trim(); }
        }
        for (var i = 0; i + 1 < sortiert.Count; i++)
        {
            if (RoutesKopf.IsMatch(sortiert[i].Text)) { return sortiert[i + 1].Text.Trim(); }
        }
        return null;
    }

    /// <summary>Die Klasse aus '"D" Performance Class' -- "SI" ist S1, "SZ" ist S2.</summary>
    public static string? KlasseAufSchirm(IReadOnlyList<OcrLine> zeilen)
    {
        // Ganze Aufnahme und Ausschnitte lesen dieselbe Zeile bis zu dreimal. Sagen sie
        // Verschiedenes, gilt die Klasse als unlesbar -- dann wird gewartet, nicht geraten.
        static string Klasse(Match m)
        {
            var roh = m.Groups[1].Value.ToUpperInvariant();
            return roh.Length == 2 && roh[0] == 'S' ? (roh[1] is '2' or 'Z' ? "S2" : "S1") : roh;
        }
        var gelesen = KlassenZeile.Matches(Text(zeilen)).Select(Klasse).Distinct().ToList();
        return gelesen.Count == 1 ? gelesen[0] : null;
    }

    // ------------------------------------------------------------------ //
    // Ein Blick: ganze Aufnahme plus zwei Ausschnitte
    // ------------------------------------------------------------------ //

    /// <summary>Was ein Blick auf den Schirm ergibt (alle Lagen im 1080p-Bezugsbild).</summary>
    internal sealed record Lesung(ScanSchirm Schirm, List<OcrLine> Zeilen, string? Titel, string? Klasse,
                                  double? Laenge, double?[] Leiste);

    /// <summary>
    /// Die Zeile unter dem Titel: '"D" Performance Class / Summer Season / Route Length: 6.9 KM'
    /// auf dem Klassenschirm, 'Route Length: 5.0 KM' auf der Streckenliste. Gemessen bei y=666.
    /// </summary>
    internal static readonly Rectangle ZeilenAusschnitt = new(90, 640, 1000, 60);

    /// <summary>Der grosse Titel der gewaehlten Strecke (y 560-650, bis zum Kartenrand).</summary>
    internal static readonly Rectangle TitelAusschnitt = new(120, 548, 1320, 108);

    /// <summary>Die Kilometerzahlen der Kachelleiste: Kachel k endet rechts bei 590 + 241 (k - 1).</summary>
    private const double LeisteRechts0 = 349, LeisteSchritt = 241;

    /// <summary>
    /// Den Schirm lesen. Wo Titel oder Klassenzeile stehen koennen, kommen zwei Ausschnitte
    /// dazu: in der ganzen Aufnahme geht die kurze Klassenzeile unter -- das alte Werkzeug
    /// las sie so in 22 von 24 Bildern, auf denen die ganze Aufnahme sie verlor (2026-08-26).
    /// </summary>
    public static Lesung Lies(WindowsOcr ocr, Bitmap bild)
    {
        var zeilen = ocr.Read(bild);
        var schirm = Einordnen(zeilen);
        string? titel = null;
        if (schirm is ScanSchirm.Streckenliste or ScanSchirm.Klassenschirm or ScanSchirm.RivalDetail
                   or ScanSchirm.Unbekannt or ScanSchirm.Kategorien)
        {
            var zeile = Ausschnitt(ocr, bild, ZeilenAusschnitt);
            // Dreifach vergroessert liest die Texterkennung die Zeile, wo sie in Originalgroesse
            // "Performance" zerlegt (20260914_142540: '"D!'+erform\ufffdnce' -> '"D" Performance').
            if (!KlassenZeile.IsMatch(Text(zeile)) && (zeile.Count > 0 || schirm != ScanSchirm.Unbekannt))
            {
                zeile = zeile.Concat(Ausschnitt(ocr, bild, ZeilenAusschnitt, 3)).ToList();
            }
            // Und schwarz auf weiss: nur die weisse und gelbe Schrift, die Karte dahinter weg.
            // Auf Shinjuku Gyoen las erst diese Fassung '"D" Performance Class' sauber.
            if (!KlassenZeile.IsMatch(Text(zeile)) && (zeile.Count > 0 || schirm != ScanSchirm.Unbekannt))
            {
                zeile = zeile.Concat(Ausschnitt(ocr, bild, ZeilenAusschnitt, 3, schwarzWeiss: true)).ToList();
            }
            var oben = Ausschnitt(ocr, bild, TitelAusschnitt);
            if (zeile.Count > 0 || oben.Count > 0)
            {
                zeilen = zeilen.Concat(zeile).Concat(oben).ToList();
                schirm = Einordnen(zeilen);
            }
            // Der Titel ist die hoechste Zeile im Titelausschnitt -- er ist der groesste Text dort.
            titel = oben.OrderByDescending(z => z.H).ThenBy(z => z.Y).Select(z => z.Text.Trim()).FirstOrDefault(t => t.Length >= 3);
        }
        if (schirm is ScanSchirm.Streckenliste or ScanSchirm.Klassenschirm or ScanSchirm.RivalDetail)
        {
            titel ??= GewaehlteStrecke(zeilen);
        }
        else
        {
            titel = null;
        }
        return new Lesung(schirm, zeilen, titel, KlasseAufSchirm(zeilen), Laenge(zeilen), Leiste(zeilen));
    }

    /// <summary>Einen Ausschnitt lesen; die Zeilen kommen in Lagen des ganzen Bilds zurueck.</summary>
    private static List<OcrLine> Ausschnitt(WindowsOcr ocr, Bitmap bild, Rectangle r, int faktor = 1, bool schwarzWeiss = false)
    {
        try
        {
            var x = Math.Max(0, r.X * bild.Width / 1920);
            var y = Math.Max(0, r.Y * bild.Height / 1080);
            var w = Math.Min(bild.Width - x, r.Width * bild.Width / 1920);
            var h = Math.Min(bild.Height - y, r.Height * bild.Height / 1080);
            if (w < 8 || h < 8) { return new List<OcrLine>(); }
            using var stueck = bild.Clone(new Rectangle(x, y, w, h), PixelFormat.Format24bppRgb);
            if (faktor <= 1)
            {
                return ocr.Read(stueck).Select(z => z with { X = z.X + x, Y = z.Y + y }).ToList();
            }
            using var gross = new Bitmap(w * faktor, h * faktor, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(gross))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(stueck, 0, 0, gross.Width, gross.Height);
            }
            if (schwarzWeiss) { SchriftSchwarzAufWeiss(gross); }
            return ocr.Read(gross).Select(z => z with
            {
                X = x + (z.X / faktor), Y = y + (z.Y / faktor), W = z.W / faktor, H = z.H / faktor,
            }).ToList();
        }
        catch (Exception)
        {
            // Eine Zugabe zur ganzen Aufnahme: sie darf einen Blick nie scheitern lassen.
            return new List<OcrLine>();
        }
    }

    /// <summary>
    /// Weisse und gelbe Schrift schwarz, alles andere weiss -- die helle Karte hinter der
    /// Klassenzeile verschwindet. Weiss: alle drei Kanaele hell; Gelb: Rot und Gruen hell, Blau dunkel.
    /// </summary>
    private static void SchriftSchwarzAufWeiss(Bitmap bild)
    {
        var daten = bild.LockBits(new Rectangle(0, 0, bild.Width, bild.Height), ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
        try
        {
            var puffer = new byte[daten.Stride * bild.Height];
            System.Runtime.InteropServices.Marshal.Copy(daten.Scan0, puffer, 0, puffer.Length);
            for (var y = 0; y < bild.Height; y++)
            {
                var z = y * daten.Stride;
                for (var x = 0; x < bild.Width; x++)
                {
                    var i = z + (x * 3);
                    int b = puffer[i], g = puffer[i + 1], r = puffer[i + 2];
                    var schrift = r > 215 && g > 200 && (b > 200 || b < 90);
                    var v = schrift ? (byte)0 : (byte)255;
                    puffer[i] = v; puffer[i + 1] = v; puffer[i + 2] = v;
                }
            }
            System.Runtime.InteropServices.Marshal.Copy(puffer, 0, daten.Scan0, puffer.Length);
        }
        finally
        {
            bild.UnlockBits(daten);
        }
    }

    /// <summary>Die Streckenlaenge aus "Route Length: 6.9 KM", in km.</summary>
    public static double? Laenge(IReadOnlyList<OcrLine> zeilen)
    {
        foreach (var z in zeilen)
        {
            var m = LaengeWert.Match(z.Text);
            if (m.Success) { return int.Parse(m.Groups[1].Value) + (int.Parse(m.Groups[2].Value) / 10.0); }
        }
        return null;
    }

    /// <summary>
    /// Die Kilometerzahlen der Kachelleiste unter der Karte, je Kachel: [0] die vorige Strecke,
    /// [1] die gewaehlte, dann die folgenden. Leer, wo nichts gelesen wurde.
    /// </summary>
    public static double?[] Leiste(IReadOnlyList<OcrLine> zeilen)
    {
        var leiste = new double?[7];
        foreach (var z in zeilen)
        {
            if (z.Y < 815 || z.Y > 880) { continue; }
            var m = KmWert.Match(z.Text);
            if (!m.Success) { continue; }
            var rechts = z.X + (z.W > 0 ? z.W : 66);
            var k = (int)Math.Round((rechts - LeisteRechts0) / LeisteSchritt);
            if (k < 0 || k >= leiste.Length) { continue; }
            leiste[k] = int.Parse(m.Groups[1].Value) + (int.Parse(m.Groups[2].Value) / 10.0);
        }
        return leiste;
    }

    /// <summary>
    /// Der Index im Karussell aus der Kachelleiste: die gelesenen Zahlen muessen zu den Laengen
    /// ab diesem Index passen, die der gewaehlten zur Zeile "Route Length". Eine Fehllesung ist
    /// erlaubt ("23.1 KM" kam als "3.1" an); -1, wenn keine oder mehrere Positionen passen.
    /// </summary>
    /// <remarks>
    /// Die Leiste ist Zahlen, und Zahlen liest die Texterkennung zuverlaessig -- anders als die
    /// Titel, die auf hellen Karten zu "•iånjü% GY&en Cross-Coun" werden. Dass jede Position
    /// eindeutig ist (auch ohne die vorige Kachel und mit nur drei Zahlen), prueft
    /// scripts/build_scan_routes.py an allen 86 Strecken.
    /// </remarks>
    public static int LeisteVerorten(double?[] leiste, double? laenge, IReadOnlyList<double?> laengen)
    {
        var n = laengen.Count;
        if (n == 0) { return -1; }
        var kandidaten = new List<(int Wert, int Index)>();
        for (var i = 0; i < n; i++)
        {
            if (laenge is { } l && laengen[i] is { } li && Math.Abs(l - li) > 0.05) { continue; }
            int treffer = 0, fehler = 0;
            for (var k = 0; k < leiste.Length; k++)
            {
                if (leiste[k] is not { } wert) { continue; }
                if (laengen[(((i + k - 1) % n) + n) % n] is not { } soll) { continue; }
                if (Math.Abs(soll - wert) <= 0.05) { treffer++; } else { fehler++; }
            }
            if (fehler <= 1 && treffer >= Math.Min(3, n - 1)) { kandidaten.Add((treffer - (2 * fehler), i)); }
        }
        if (kandidaten.Count == 0) { return -1; }
        var sortiert = kandidaten.OrderByDescending(k => k.Wert).ToList();
        if (sortiert.Count > 1 && sortiert[1].Wert == sortiert[0].Wert) { return -1; }
        return sortiert[0].Index;
    }

    /// <summary>Das Suchmuster fuer eine Klasse, wie die Texterkennung sie schreibt.</summary>
    public static string KlassenToken(string klasse) => klasse.ToUpperInvariant() switch
    {
        "S1" => "S[1IilL]",
        "S2" => "S[2Zz]",
        var k => Regex.Escape(k),
    };

    // ------------------------------------------------------------------ //
    // Streckennamen verorten
    // ------------------------------------------------------------------ //

    /// <summary>Ab hier beginnt das Gattungswort -- auch verstuemmelt ("Cras-eouniry", "Cire").</summary>
    private static readonly Dictionary<string, string> Gattungen = new(StringComparer.Ordinal)
    {
        ["cro"] = "cross", ["cra"] = "cross", ["ero"] = "cross", ["era"] = "cross", ["ros"] = "cross", ["ras"] = "cross",
        ["cir"] = "cir", ["spr"] = "spr", ["scr"] = "scr", ["tra"] = "tra", ["dra"] = "dra", ["cha"] = "cha",
    };

    private const double Schwelle = 0.7;   // ab hier zeigt eine Lesung die Strecke
    private const double Abstand = 0.15;   // so weit muss die beste Strecke vor der zweitbesten liegen

    internal static string Falte(string? name) =>
        new string((name ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>Der Ort vor dem Gattungswort. Ab Stelle 3, damit "Hirosaki" sein "ros" behaelt.</summary>
    internal static string Kopf(string? name)
    {
        var s = Falte(name);
        for (var i = 3; i + 3 <= s.Length; i++)
        {
            if (Gattungen.ContainsKey(s.Substring(i, 3))) { return s[..i]; }
        }
        return s;
    }

    /// <summary>Die Art des Gattungsworts ("cross", "cir", "spr" ...), leer ohne eines.</summary>
    internal static string Gattung(string? name)
    {
        var s = Falte(name);
        var k = Kopf(name);
        return k.Length + 3 <= s.Length && Gattungen.TryGetValue(s.Substring(k.Length, 3), out var g) ? g : string.Empty;
    }

    private static int Levenshtein(string a, string b)
    {
        var vorher = new int[b.Length + 1];
        var jetzt = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) { vorher[j] = j; }
        for (var i = 1; i <= a.Length; i++)
        {
            jetzt[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                jetzt[j] = Math.Min(Math.Min(vorher[j] + 1, jetzt[j - 1] + 1), vorher[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }
            (vorher, jetzt) = (jetzt, vorher);
        }
        return vorher[b.Length];
    }

    private static double Aehnlichkeit(string a, string b) =>
        a.Length == 0 || b.Length == 0 ? 0.0 : 1.0 - (Levenshtein(a, b) / (double)Math.Max(a.Length, b.Length));

    /// <summary>Wie sehr eine Lesung (Laufschrift, OCR-Schaden) diese Strecke zeigt, 0..1.</summary>
    internal static double StreckenAehnlichkeit(string? gelesen, string name)
    {
        var x = Kopf(gelesen);
        var y = Kopf(name);
        if (x.Length < 3 || y.Length == 0) { return 0.0; }
        var ga = Gattung(gelesen);
        var gb = Gattung(name);
        if (ga.Length > 0 && gb.Length > 0 && ga != gb) { return 0.0; }
        // Nur ein langer Titel laeuft (ab ~25 Zeichen ist er beschnitten); ein kurzer steht --
        // und darf nicht verschoben verglichen werden, sonst wird aus "Irokawa" ein
        // verschobenes "sh|irakawa".
        var voll = Falte(name);
        var schuebe = voll.Length > 24 ? 5 : 1;
        var beste = 0.0;
        if (ga.Length == 0)
        {
            // Ohne erkanntes Gattungswort ist die Lesung vielleicht mitten darin beschnitten
            // ("Shimanoyama Sp"): dann gegen den gleich langen Anfang des vollen Namens.
            var xf = Falte(gelesen);
            beste = Aehnlichkeit(xf, voll[..Math.Min(voll.Length, xf.Length)]);
        }
        for (var schub = 0; schub < schuebe; schub++)
        {
            var rest = y[Math.Min(schub, y.Length)..];
            if (rest.Length < 3) { break; }
            // Hinten beschnitten, noch vor dem Gattungswort: dann zaehlt nur der Anfang.
            if (ga.Length == 0 && x.Length < rest.Length) { rest = rest[..x.Length]; }
            beste = Math.Max(beste, Aehnlichkeit(x, rest));
        }
        return beste;
    }

    internal static bool GleicheStrecke(string? gelesen, string name) => StreckenAehnlichkeit(gelesen, name) >= Schwelle;

    /// <summary>Die Position einer Lesung im Karussell; -1, wenn keine oder mehrere passen.</summary>
    public static int Lokalisiere(string? gelesen, IReadOnlyList<string> strecken)
    {
        if (string.IsNullOrWhiteSpace(gelesen) || strecken.Count == 0) { return -1; }
        var werte = strecken.Select((n, i) => (Wert: StreckenAehnlichkeit(gelesen, n), Index: i))
                            .OrderByDescending(w => w.Wert).ToList();
        if (werte[0].Wert < Schwelle) { return -1; }
        if (werte.Count > 1 && werte[0].Wert - werte[1].Wert < Abstand) { return -1; }
        return werte[0].Index;
    }

    // ------------------------------------------------------------------ //
    // Der Kategorieschirm: ein 2x3-Gitter mit gelbem Rahmen
    // ------------------------------------------------------------------ //

    /// <summary>Die sechs Kacheln (Zeile, Spalte) -- Stand 2026-09-12, mit Drag Racing und Touge freigeschaltet.</summary>
    internal static readonly (string Name, int Zeile, int Spalte)[] Kacheln =
    {
        ("Road Racing", 0, 0), ("Cross-Country", 0, 1), ("Street Racing", 0, 2),
        ("Dirt Racing", 1, 0), ("Drag Racing", 1, 1), ("Touge", 1, 2),
    };

    private static readonly double[] KachelX = { 0.198, 0.499, 0.798 };
    private static readonly double[] KachelY = { 0.3795, 0.653 };

    /// <summary>Ist das der Kategorieschirm? "Routes Available" plus drei Kategorienamen -- der Klassenschirm hat auch einen Rahmen.</summary>
    public static bool IstKategorieSchirm(IReadOnlyList<OcrLine> zeilen)
    {
        var text = Text(zeilen);
        return RoutesAvailable.IsMatch(text) && Kacheln.Count(k => text.Contains(k.Name, StringComparison.OrdinalIgnoreCase)) >= 3;
    }

    /// <summary>
    /// Die Farbe der Markierung. Sie PULSIERT zwischen Limette (202, 255, 2) und reinem Gelb
    /// (255, 255, 0): gemessen 2026-10-03 am Spiel auf dem Hauptrechner, (228, 254, 69) in
    /// einem Bild dazwischen. Die alte Regel "Gruen groesser Rot" (in der VM gemessen) traf nur
    /// die Limettenphase und meldete im ersten Lauf "kein Rahmen".
    /// </summary>
    internal static bool IstMarkierung(int r, int g, int b) => g >= 225 && r >= 150 && b <= 110;

    /// <summary>Halbe Breite und Hoehe des Rahmens um eine Kachel (1080p): x 88-674, y 262-560 um Road Racing.</summary>
    private const double RahmenHalbB = 292, RahmenHalbH = 149;

    /// <summary>
    /// Welche Kachel den Rahmen traegt -- am RING um jede Kachel, nicht an der Farbe allein:
    /// gelbe Autos (Touge, das Rallyeauto in Dirt) und die gelbe Schrift "Routes Available"
    /// liegen IN den Kacheln, der Rahmen auf ihrem Rand. Eine Kachel ist markiert, wenn mindestens
    /// drei ihrer vier Kanten fast durchgehend Markierungsfarbe tragen; die Nachbarkachel teilt
    /// hoechstens eine Kante (der Spalt zwischen den Kacheln ist schmaler als der Rahmen).
    /// </summary>
    public static (int Zeile, int Spalte)? KategorieRahmen(Bitmap bild)
    {
        var w = bild.Width;
        var h = bild.Height;
        var daten = bild.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        byte[] puffer;
        int stride;
        try
        {
            stride = daten.Stride;
            puffer = new byte[stride * h];
            System.Runtime.InteropServices.Marshal.Copy(daten.Scan0, puffer, 0, puffer.Length);
        }
        finally
        {
            bild.UnlockBits(daten);
        }
        var fx = w / 1920.0;
        var fy = h / 1080.0;
        bool Markiert(double x1080, double y1080)
        {
            var x = (int)Math.Round(x1080 * fx);
            var y = (int)Math.Round(y1080 * fy);
            if (x < 0 || y < 0 || x >= w || y >= h) { return false; }
            var i = (y * stride) + (x * 3);
            return IstMarkierung(puffer[i + 2], puffer[i + 1], puffer[i]);
        }
        // Anteil der Stellen entlang einer Kante, an denen quer zur Kante (+-7 px) Markierung liegt.
        double Waagerecht(double y, double x0, double x1)
        {
            int an = 0, alle = 0;
            for (var x = x0; x <= x1; x += 6)
            {
                alle++;
                for (var d = -7; d <= 7; d++) { if (Markiert(x, y + d)) { an++; break; } }
            }
            return alle == 0 ? 0 : an / (double)alle;
        }
        double Senkrecht(double x, double y0, double y1)
        {
            int an = 0, alle = 0;
            for (var y = y0; y <= y1; y += 6)
            {
                alle++;
                for (var d = -7; d <= 7; d++) { if (Markiert(x + d, y)) { an++; break; } }
            }
            return alle == 0 ? 0 : an / (double)alle;
        }

        var treffer = new List<(int Zeile, int Spalte, int Kanten)>();
        for (var z = 0; z < KachelY.Length; z++)
        {
            for (var sp = 0; sp < KachelX.Length; sp++)
            {
                var cx = KachelX[sp] * 1920;
                var cy = KachelY[z] * 1080;
                var kanten = 0;
                if (Waagerecht(cy - RahmenHalbH, cx - 240, cx + 240) >= 0.8) { kanten++; }
                if (Waagerecht(cy + RahmenHalbH, cx - 240, cx + 240) >= 0.8) { kanten++; }
                if (Senkrecht(cx - RahmenHalbB, cy - 110, cy + 110) >= 0.8) { kanten++; }
                if (Senkrecht(cx + RahmenHalbB, cy - 110, cy + 110) >= 0.8) { kanten++; }
                if (kanten >= 3) { treffer.Add((z, sp, kanten)); }
            }
        }
        if (treffer.Count == 0) { return null; }
        var beste = treffer.OrderByDescending(t => t.Kanten).ToList();
        // Zwei gleich gute Kacheln: kein eindeutiger Rahmen -- nicht raten.
        if (beste.Count > 1 && beste[1].Kanten == beste[0].Kanten) { return null; }
        return (beste[0].Zeile, beste[0].Spalte);
    }
}
