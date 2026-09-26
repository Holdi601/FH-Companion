using System.Drawing;
using System.Drawing.Imaging;
using System.Text.RegularExpressions;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>Wie lang eine angebotene Strecke ist, wie der Schirm es sagt.</summary>
/// <param name="TotalKm">Die Gesamtstrecke der Veranstaltung.</param>
/// <param name="Laps">Wie viele Runden. 1, wenn keine Rundenzahl dasteht.</param>
internal readonly record struct TrackLength(double TotalKm, int Laps)
{
    /// <summary>Wie lang eine EINZELNE Runde ist, in Metern.</summary>
    public double LapMetres => Laps > 0 ? TotalKm * 1000.0 / Laps : TotalKm * 1000.0;
}

/// <summary>What one frame of the Event Sign Up screen said.</summary>
internal sealed class ScreenState
{
    public List<string> Tracks { get; } = new();
    public Dictionary<string, double> TrackScores { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Was unter dem Streckennamen steht: "8.5 KM - 3 LAPS".
    /// </summary>
    /// <remarks>
    /// DIE EINZIGE BRUECKE ZWISCHEN DEM SCHIRM UND DEM EIGENEN RUNDENBESTAND.
    ///
    /// Der Schirm nennt Namen, die eigenen Runden kennen keine: das Feld `track`
    /// ist bei allen aufgezeichneten Runden leer, und die Kursordner heissen nach
    /// den Koordinaten ihrer Start-Ziel-Linie. Beide kennen aber eine LAENGE -- der
    /// Schirm als Zahl, der Kursordner als gemessene `ShortestMetres`. Ueber die
    /// laesst sich sagen, welcher eigene Kurs eine angebotene Strecke ist.
    ///
    /// ACHTUNG, DIE ZAHL IST DIE GESAMTSTRECKE. "8.5 KM - 3 LAPS" heisst 8,5 km
    /// insgesamt, also rund 2,83 km je Runde -- die drei Zahlen des Bildschirms
    /// (8.5 + 5.5 + 15.0) ergeben genau die 29.1 KM der Ueberschrift. Wer hier die
    /// Gesamtstrecke gegen eine Rundenlaenge haelt, vergleicht das Dreifache.
    /// </remarks>
    public Dictionary<string, TrackLength> TrackLengths { get; } =
        new(StringComparer.Ordinal);
    public string? Klass { get; set; }
    public string KlassSource { get; set; } = string.Empty;
    public bool Spec { get; set; }
    public List<OcrLine> Lines { get; } = new();
    public double ReadMilliseconds { get; set; }

    /// <summary>
    /// Horizon Play und andere Reihen: was rechts neben einer Strecke steht.
    /// </summary>
    /// <remarks>
    /// Seit 2026-09-26. Der Anmeldeschirm einer laufenden Reihe ist derselbe Schirm
    /// wie sonst, nur mit einer Statusspalte: "In Progress" neben dem Rennen, das die
    /// anderen gerade fahren, "Up Next" neben dem, in das man einsteigt. Die
    /// Texterkennung lieferte beides schon immer -- es passte nur auf keinen
    /// Streckennamen und fiel darum weg.
    /// </remarks>
    public Dictionary<string, RouteStatus> TrackStatus { get; } = new(StringComparer.Ordinal);

    /// <summary>"Joining Horizon Play Racing 2/3": der Name der Reihe.</summary>
    public string? Series { get; set; }

    /// <summary>Das wievielte Rennen der Reihe man betritt (1-basiert, 0 = unbekannt).</summary>
    public int SeriesIndex { get; set; }

    /// <summary>Wie viele Rennen die Reihe hat (0 = unbekannt).</summary>
    public int SeriesCount { get; set; }

    public bool IsHorizonPlay =>
        Series?.Contains("Horizon Play", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Ab welcher Strecke (Index in <see cref="Tracks"/>) man selbst faehrt.
    /// </summary>
    /// <remarks>
    /// Wer bei "2/3" einsteigt, faehrt Strecke 01 nie: die laeuft schon. Zuerst gilt
    /// "Up Next", dann die Zahl der Ueberschrift, dann die Strecke nach "In Progress".
    /// Ohne jede Angabe (der gewoehnliche Anmeldeschirm) faehrt man alle.
    /// </remarks>
    public int FirstOwnIndex
    {
        get
        {
            var weiter = Tracks.FindIndex(t => TrackStatus.GetValueOrDefault(t) == RouteStatus.UpNext);
            if (weiter >= 0) { return weiter; }
            if (SeriesIndex >= 1 && SeriesIndex <= Tracks.Count) { return SeriesIndex - 1; }
            var laeuft = Tracks.FindIndex(t => TrackStatus.GetValueOrDefault(t) == RouteStatus.InProgress);
            return laeuft >= 0 && laeuft + 1 < Tracks.Count ? laeuft + 1 : 0;
        }
    }

    /// <summary>Die Strecken, die man ab jetzt noch selbst faehrt, in ihrer Reihenfolge.</summary>
    public List<string> RemainingTracks => Tracks.Skip(FirstOwnIndex).ToList();

    /// <summary>Enough to answer with: at least two routes and a class.</summary>
    public bool IsOffer => !string.IsNullOrEmpty(Klass) && Tracks.Count >= 2;

    // Der Einstieg gehoert zum Schluessel: rueckt "Up Next" weiter, ist das eine neue
    // Antwort (andere Reststrecken), auch wenn Strecken und Klasse gleich bleiben.
    public string Key => string.Join('|', Tracks) + "/" + (Klass ?? "-")
                         + (SeriesIndex > 0 || TrackStatus.Count > 0 ? "/ab" + FirstOwnIndex : string.Empty);
}

/// <summary>Was in der Statusspalte einer Reihe neben einer Strecke steht.</summary>
internal enum RouteStatus
{
    None,
    InProgress,
    UpNext,
}

/// <summary>
/// Read the Event Sign Up screen: which routes are offered, and in which class.
/// </summary>
/// <remarks>
/// Two masked regions, given as FRACTIONS of the frame so one setting fits 1080p,
/// 1440p and 4K, and only a change of aspect ratio needs a new measurement.
/// Everything outside them is never shown to the OCR, which is both the fast path
/// and the correct one: the card's lower half prints the FEATURED car's own class
/// and PI ("C 484"), and on a Spec Racing event that is the spec car, not the
/// restriction. A whole-frame read finds both, cannot tell them apart, and answers
/// the wrong question with total confidence.
///
/// Inside a region, matching is against the dataset's closed vocabulary -- 23 route
/// names, 7 classes -- not against a row geometry. A patch that moves the cards
/// costs nothing.
/// </remarks>
internal sealed class RivalsScreenReader
{
    /// <summary>Forza's PI bands. A fallback only; a badge always wins.</summary>
    /// <summary>
    /// Die Obergrenze je Leistungsklasse.
    /// </summary>
    /// <remarks>
    /// BERICHTIGT am 2026-09-14. Hier stand 500 fuer D, und damit war jede Klasse um
    /// eine Stufe verschoben: ein Auto mit PI 500 galt als D statt als C, PI 998 als
    /// S2 statt als R.
    ///
    /// Die richtigen Grenzen standen die ganze Zeit im eigenen Quelltext -- in
    /// `forza_navigator.ps1` beschreibt ein Kommentar die Klassenleiste des Spiels
    /// mit "D 400 / C 500 / B 600 / A 700 / S1 800 / S2 900". Der Nutzer hat es
    /// bestaetigt, und die Garagen-Datenbank tut es unabhaengig davon auch: dort ist
    /// `ClassID` dasselbe Feld wie `CarClass` im Telemetriepaket, und die gefahrenen
    /// Autos ergeben ClassID 0..6 = D, C, B, A, S1, S2, R.
    ///
    /// Ueber 998 gibt es nichts mehr: die Bestenlisten des Spiels kennen genau diese
    /// sieben Klassen.
    /// </remarks>
    public static readonly (int Cap, string Name)[] PiBands =
    {
        (400, "D"), (500, "C"), (600, "B"), (700, "A"), (800, "S1"), (900, "S2"),
        (9999, "R"),
    };

    private const string ClassToken = @"(S\s?1|S\s?2|[ABCDRX])";

    private static readonly Regex[] ClassPatterns =
    {
        new(ClassToken + @"\s*[-:]?\s*CLASS\b", RegexOptions.IgnoreCase),
        new(@"\bCLASS\s*[-:]?\s*" + ClassToken + @"\b", RegexOptions.IgnoreCase),
        new(@"\b" + ClassToken + @"\s*[-:]?\s*(\d{3})\b", RegexOptions.IgnoreCase),
    };

    // The lone badge in the card's corner, with no word "class" anywhere near it.
    private static readonly Regex LoneClass =
        new(@"^(S\s?1|S\s?2|[ABCDRX])$", RegexOptions.IgnoreCase);

    private static readonly Regex PiPattern = new(@"\b([1-9]\d{2})\b");

    // A one-make event: everyone drives the same car, so a ranking of 400 is noise.
    private static readonly Regex SpecPattern =
        new(@"\bSPEC\s*RACING\b|\bONE[\s-]?MAKE\b", RegexOptions.IgnoreCase);

    private readonly RivalsAdvisor _advisor;
    private readonly OverlaySettings _settings;
    private readonly WindowsOcr _ocr = new();
    private readonly HashSet<string> _classNames;

    public RivalsScreenReader(RivalsAdvisor advisor, OverlaySettings settings)
    {
        _advisor = advisor;
        _settings = settings;
        _classNames = new HashSet<string>(advisor.ClassNames, StringComparer.Ordinal) { "X" };
    }

    public bool OcrAvailable => _ocr.Available;

    /// <summary>Ein fertiges Bild lesen -- fuer den Titel der Kachel im Automenue.</summary>
    public List<OcrLine> ReadLines(Bitmap bild) => _ocr.Read(bild);
    public string OcrLanguage => _ocr.LanguageTag;

    // ------------------------------------------------------------------ //
    // capture
    // ------------------------------------------------------------------ //

    /// <summary>The primary screen, or one fractional region of it.</summary>
    public static Bitmap Grab(Rectangle bounds)
    {
        var shot = new Bitmap(Math.Max(1, bounds.Width), Math.Max(1, bounds.Height),
                              PixelFormat.Format32bppArgb);
        using var canvas = Graphics.FromImage(shot);
        canvas.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size,
                              CopyPixelOperation.SourceCopy);
        return shot;
    }

    /// <summary>
    /// The class badge, as lines -- read as a word because it cannot be read alone.
    /// </summary>
    /// <remarks>
    /// Windows OCR returns nothing for a single character and nothing for two, so
    /// the glyph is cut out and repeated into a three-letter word first. See
    /// <see cref="BadgeReader"/>. The returned line carries the UNREPEATED token, so
    /// everything downstream sees a plain "C" or "S1".
    /// </remarks>
    private List<OcrLine> ReadBadge(Bitmap region)
    {
        using var word = BadgeReader.Wordify(region);
        // FORZA_BADGE_DEBUG=<folder> writes what the badge step actually saw. The
        // badge is the one part of the read with no text to check it against.
        var debug = Environment.GetEnvironmentVariable("FORZA_BADGE_DEBUG");
        if (!string.IsNullOrEmpty(debug))
        {
            try
            {
                Directory.CreateDirectory(debug);
                var stamp = DateTime.Now.ToString("HHmmss.fff");
                region.Save(Path.Combine(debug, $"badge-{stamp}-region.png"),
                            ImageFormat.Png);
                word?.Save(Path.Combine(debug, $"badge-{stamp}-word.png"),
                           ImageFormat.Png);
            }
            catch (Exception)
            {
                // Diagnostics must never be the thing that breaks a read.
            }
        }
        var lines = new List<OcrLine>();
        if (word is not null)
        {
            foreach (var line in _ocr.Read(word))
            {
                var token = BadgeReader.Unrepeat(line.Text);
                if (!string.IsNullOrEmpty(token) && _classNames.Contains(token))
                {
                    lines.Add(new OcrLine(token, 0, 0));
                }
            }
        }
        if (lines.Count > 0)
        {
            return lines;
        }

        // The engine came back empty, which for a one- or two-glyph badge is its
        // normal behaviour rather than a sign that nothing is there. Decide by
        // shape instead, among the eight classes a badge can possibly be.
        using var glyph = BadgeReader.Isolate(region);
        if (glyph is not null)
        {
            var shape = BadgeShapes.Identify(glyph, _classNames);
            if (shape is not null)
            {
                lines.Add(new OcrLine(shape.Value.Token, 0, 0));
            }
        }
        return lines;
    }

    public static Rectangle RegionOf(Rectangle screen, double[]? fractions)
    {
        if (fractions is null || fractions.Length != 4)
        {
            return screen;
        }
        var x0 = screen.Left + (int)Math.Round(fractions[0] * screen.Width);
        var y0 = screen.Top + (int)Math.Round(fractions[1] * screen.Height);
        var x1 = screen.Left + (int)Math.Round(fractions[2] * screen.Width);
        var y1 = screen.Top + (int)Math.Round(fractions[3] * screen.Height);
        var rect = Rectangle.FromLTRB(Math.Min(x0, x1), Math.Min(y0, y1),
                                      Math.Max(x0, x1), Math.Max(y0, y1));
        rect.Intersect(screen);
        return rect.Width < 2 || rect.Height < 2 ? screen : rect;
    }

    // ------------------------------------------------------------------ //
    // reading
    // ------------------------------------------------------------------ //

    /// <summary>Read the live screen: two crops, or the whole thing for diagnosis.</summary>
    /// <summary>
    /// The height the Event Sign Up screen was measured at (2560x1440, 2026-08-26).
    /// </summary>
    /// <remarks>
    /// Every region is read at THIS scale, whatever the real resolution: 720p grows
    /// by two, 4K shrinks by a third, 16K by six. <see cref="NaheY"/> is a distance
    /// in these points, so it holds everywhere -- read at native size it was a
    /// different distance at every resolution. And Windows OCR gets text at the size
    /// it was tuned on instead of images past its maximum dimension.
    /// </remarks>
    private const double MeasuredHeight = 1440;

    private static Size Normalised(Size size, double k) =>
        new(Math.Max(1, (int)Math.Round(size.Width * k)), Math.Max(1, (int)Math.Round(size.Height * k)));

    public ScreenState ReadScreen(Rectangle area, bool full = false)
    {
        var started = DateTime.UtcNow;
        // Measured at 16:9; at another aspect ratio the centred 16:9 part.
        var screen = GameArea.SixteenNine(area);
        var k = MeasuredHeight / Math.Max(1, screen.Height);
        ScreenState state;
        if (full || _settings.RegionRoutes is null || _settings.RegionClass is null)
        {
            using var frame = GameArea.Capture(screen, Normalised(screen.Size, k));
            state = Interpret(_ocr.Read(frame), null, Point.Empty);
        }
        else
        {
            var routesRect = RegionOf(screen, _settings.RegionRoutes);
            var classRect = RegionOf(screen, _settings.RegionClass);
            using var routes = GameArea.Capture(routesRect, Normalised(routesRect.Size, k));
            using var badge24 = GameArea.Capture(classRect, Normalised(classRect.Size, k));
            using var badge = badge24.Clone(new Rectangle(0, 0, badge24.Width, badge24.Height),
                                            PixelFormat.Format32bppArgb);
            var origin = new Point((int)Math.Round((routesRect.X - screen.X) * k),
                                   (int)Math.Round((routesRect.Y - screen.Y) * k));
            state = Interpret(Offset(_ocr.Read(routes), origin), ReadBadge(badge), Point.Empty);
        }
        state.ReadMilliseconds = (DateTime.UtcNow - started).TotalMilliseconds;
        return state;
    }

    private static Bitmap Resized(Bitmap source, Rectangle rect, double k, PixelFormat format)
    {
        var size = Normalised(rect.Size, k);
        var bmp = new Bitmap(size.Width, size.Height, format);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(source, new Rectangle(0, 0, size.Width, size.Height), rect, GraphicsUnit.Pixel);
        return bmp;
    }

    /// <summary>Read one bitmap, for tests and for a saved capture.</summary>
    public ScreenState ReadBitmap(Bitmap frame, bool full = false)
    {
        var started = DateTime.UtcNow;
        // Same scale as a live read, so a saved capture at any resolution is judged
        // exactly as the screen it came from would have been.
        var screen = GameArea.SixteenNine(new Rectangle(0, 0, frame.Width, frame.Height));
        var k = MeasuredHeight / Math.Max(1, screen.Height);
        ScreenState state;
        if (full || _settings.RegionRoutes is null || _settings.RegionClass is null)
        {
            using var whole = Resized(frame, screen, k, PixelFormat.Format24bppRgb);
            state = Interpret(_ocr.Read(whole), null, Point.Empty);
        }
        else
        {
            var routesRect = RegionOf(screen, _settings.RegionRoutes);
            var classRect = RegionOf(screen, _settings.RegionClass);
            using var routes = Resized(frame, routesRect, k, PixelFormat.Format24bppRgb);
            using var badge = Resized(frame, classRect, k, PixelFormat.Format32bppArgb);
            var origin = new Point((int)Math.Round((routesRect.X - screen.X) * k),
                                   (int)Math.Round((routesRect.Y - screen.Y) * k));
            state = Interpret(Offset(_ocr.Read(routes), origin), ReadBadge(badge), Point.Empty);
        }
        state.ReadMilliseconds = (DateTime.UtcNow - started).TotalMilliseconds;
        return state;
    }

    private static List<OcrLine> Offset(List<OcrLine> lines, Point origin) =>
        lines.Select(l => new OcrLine(l.Text, l.X + origin.X, l.Y + origin.Y)).ToList();

    /// <summary>Routes from one set of lines, the class from another.</summary>
    public ScreenState Interpret(List<OcrLine> routeLines, List<OcrLine>? classLines,
                                 Point origin)
    {
        var state = new ScreenState();
        state.Lines.AddRange(routeLines);
        var classPool = classLines ?? routeLines;

        // Keep the better read of a route, and the position of that better read.
        var hits = new List<(double Y, string Track, double Score)>();
        var seen = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var line in routeLines)
        {
            var match = _advisor.MatchTrack(line.Text, _settings.TrackCutoff);
            if (match is null)
            {
                continue;
            }
            var (name, score) = match.Value;
            if (seen.TryGetValue(name, out var had) && had >= score)
            {
                continue;
            }
            seen[name] = score;
            hits.RemoveAll(h => h.Track == name);
            hits.Add((line.Y, name, score));
        }

        var keep = hits.OrderByDescending(h => h.Score).ThenBy(h => h.Y)
                       .Take(Math.Max(1, _settings.MaxTracks))
                       // Report in reading order: that is the order they are offered.
                       .OrderBy(h => h.Y).ToList();
        foreach (var (y, name, score) in keep)
        {
            state.Tracks.Add(name);
            state.TrackScores[name] = score;
            var laenge = LaengeUnter(routeLines, y);
            if (laenge is { } l) { state.TrackLengths[name] = l; }
        }

        // DIE STATUSSPALTE EINER REIHE. "In Progress" und "Up Next" stehen in derselben
        // Zeile wie der Streckenname, weiter rechts -- mal als eigene Zeile der
        // Texterkennung, mal an den Namen angehaengt. Beides landet bei der Strecke,
        // deren Zeile am naechsten liegt.
        foreach (var line in routeLines)
        {
            var status = StatusIn(line.Text);
            if (status == RouteStatus.None || keep.Count == 0) { continue; }
            var zeile = keep.MinBy(h => Math.Abs(h.Y - line.Y));
            if (Math.Abs(zeile.Y - line.Y) > StatusNaheY) { continue; }
            state.TrackStatus[zeile.Track] = status;
        }
        foreach (var line in routeLines)
        {
            var reihe = ReiheMuster.Match(line.Text);
            if (!reihe.Success) { continue; }
            var k = reihe.Groups["k"].Value[0] - '0';
            var n = reihe.Groups["n"].Value[0] - '0';
            if (k < 1 || k > n) { continue; }
            state.Series = reihe.Groups["name"].Value.Trim();
            state.SeriesIndex = k;
            state.SeriesCount = n;
            break;
        }

        // A masked class region is confirmation enough on its own; a whole-frame
        // read still has to earn it with routes, or a desktop reading "played for
        // 538 hours" comes back as class C.
        var confirmed = classLines is not null || state.Tracks.Count >= _settings.MinTracks;
        var (klass, source) = FindClass(classPool, confirmed);
        state.Klass = klass;
        state.KlassSource = source;
        state.Spec = routeLines.Any(IsSpecLine);
        if (classLines is not null)
        {
            state.Lines.AddRange(classLines);
        }
        return state;
    }

    /// <summary>Die Entfernungszeile, die zu einem Streckennamen gehoert.</summary>
    /// <remarks>
    /// Sie steht DIREKT UNTER dem Namen: "8.5 KM - 3 LAPS". Gesucht wird darum die
    /// naechste Zeile unterhalb, nicht die naechstgelegene -- ueber dem Namen steht
    /// die Entfernungszeile der VORIGEN Strecke, und die zu nehmen hiesse, jede
    /// Strecke mit der Laenge ihres Vorgaengers zu beschriften.
    ///
    /// Der Abstand ist begrenzt, damit bei einer Strecke ohne eigene Zeile nicht
    /// die Zeile der uebernaechsten einspringt. <see cref="NaheY"/> ist in denselben
    /// Einheiten wie die Y-Werte der Texterkennung, also Bildpunkte des Ausschnitts.
    /// </remarks>
    private static TrackLength? LaengeUnter(List<OcrLine> lines, double y)
    {
        OcrLine? beste = null;
        foreach (var line in lines)
        {
            if (line.Y <= y || line.Y - y > NaheY) { continue; }
            if (!KmMuster.IsMatch(line.Text)) { continue; }
            if (beste is null || line.Y < beste.Value.Y) { beste = line; }
        }
        if (beste is null) { return null; }

        var m = KmMuster.Match(beste.Value.Text);
        if (!double.TryParse(m.Groups[1].Value,
                             System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture,
                             out var km) || km <= 0)
        {
            return null;
        }
        var runden = 1;
        if (m.Groups[2].Success && int.TryParse(m.Groups[2].Value, out var r) && r > 0)
        {
            runden = r;
        }
        return new TrackLength(km, runden);
    }

    /// <summary>Wie weit unter dem Namen die Entfernungszeile hoechstens steht.</summary>
    private const double NaheY = 140;

    /// <summary>Wie weit ein Statuswort hoechstens neben der Zeile seiner Strecke steht.</summary>
    /// <remarks>
    /// Dieselbe Zeile, also wenige Bildpunkte; die naechste Strecke steht rund 175
    /// Bildpunkte (1440p) tiefer. 50 laesst Spiel fuer schief gelesene Zeilen, ohne
    /// je die Nachbarzeile zu erreichen.
    /// </remarks>
    private const double StatusNaheY = 50;

    /// <summary>"In Progress" oder "Up Next" in einer Zeile -- auch angehaengt und verlesen.</summary>
    internal static RouteStatus StatusIn(string text)
    {
        var norm = TextMatch.Normalise(text);
        if (norm.Length == 0) { return RouteStatus.None; }
        if (norm.EndsWith("in progress", StringComparison.Ordinal)) { return RouteStatus.InProgress; }
        if (norm.EndsWith("up next", StringComparison.Ordinal)) { return RouteStatus.UpNext; }
        // Verlesen ("In Pr0gress", "Up Nexl"): die letzten zwei Woerter gegen beide halten.
        var woerter = norm.Split(' ');
        if (woerter.Length < 2) { return RouteStatus.None; }
        var schluss = woerter[^2] + " " + woerter[^1];
        if (TextMatch.Similarity(schluss, "in progress") >= 0.8) { return RouteStatus.InProgress; }
        if (TextMatch.Similarity(schluss, "up next") >= 0.8) { return RouteStatus.UpNext; }
        return RouteStatus.None;
    }

    /// <summary>
    /// "Joining Horizon Play Racing 2/3 - 25.8 KM": Name der Reihe, Rennen, Anzahl.
    /// </summary>
    /// <remarks>
    /// Die Texterkennung liest den Schraegstrich gern als 1 ("213" am 2026-09-09),
    /// darum ist 1, I, l und | als Trenner erlaubt. Eine Reihe hat hoechstens neun
    /// Rennen -- eine Ziffer je Seite, sonst waere "213" nicht zu zerlegen.
    /// </remarks>
    private static readonly Regex ReiheMuster = new(
        @"J[o0]in[il1]ng\s+(?<name>.+?)\s+(?<k>[1-9])\s*[/1Il|\\]\s*(?<n>[1-9])(?!\d)",
        RegexOptions.IgnoreCase);

    /// <summary>"8.5 KM - 3 LAPS", auch ohne den Rundenteil.</summary>
    private static readonly Regex KmMuster = new(
        @"([0-9]+(?:[.,][0-9]+)?)\s*KM(?:\s*[-\u2013]\s*([0-9]+)\s*LAPS?)?",
        RegexOptions.IgnoreCase);

    /// <summary>
    /// A one-make header, even when the OCR ate a letter of it.
    /// </summary>
    /// <remarks>
    /// A real read of the header came back as "S ec Racin 1/3 - 36.4 KM": a strict
    /// pattern misses that, and the panel then offers a car ranking for an event
    /// where the car is fixed. Fuzzy against the same folded form the route matcher
    /// uses.
    /// </remarks>
    private static bool IsSpecLine(OcrLine line)
    {
        if (SpecPattern.IsMatch(line.Text))
        {
            return true;
        }
        var probe = TextMatch.Normalise(line.Text);
        if (probe.Length == 0)
        {
            return false;
        }
        foreach (var phrase in new[] { "spec racing", "one make" })
        {
            // Compare only the leading window: the header carries a distance and an
            // event count after the name.
            var window = probe.Length > phrase.Length + 4
                ? probe[..(phrase.Length + 4)]
                : probe;
            if (TextMatch.Similarity(window, phrase) >= 0.80)
            {
                return true;
            }
        }
        return false;
    }

    private (string? Klass, string Source) FindClass(List<OcrLine> lines, bool confirmed)
    {
        foreach (var pattern in ClassPatterns)
        {
            foreach (var line in lines)
            {
                var match = pattern.Match(line.Text);
                if (!match.Success)
                {
                    continue;
                }
                var token = match.Groups[1].Value.ToUpperInvariant().Replace(" ", "");
                if (_classNames.Contains(token))
                {
                    return (token, $"token \"{line.Text}\"");
                }
            }
        }
        if (!confirmed)
        {
            return (null, string.Empty);
        }
        foreach (var line in lines)
        {
            var trimmed = line.Text.Trim();
            var token = trimmed.ToUpperInvariant().Replace(" ", "");
            if (LoneClass.IsMatch(trimmed) && _classNames.Contains(token))
            {
                return (token, $"badge \"{trimmed}\"");
            }
        }
        // Last resort, and it says it guessed.
        foreach (var line in lines)
        {
            foreach (Match found in PiPattern.Matches(line.Text))
            {
                var pi = int.Parse(found.Groups[1].Value);
                foreach (var (cap, name) in PiBands)
                {
                    if (pi > cap)
                    {
                        continue;
                    }
                    return _classNames.Contains(name)
                        ? (name, $"PI {pi} in \"{line.Text}\"")
                        : (null, string.Empty);
                }
            }
        }
        return (null, string.Empty);
    }

    /// <summary>The class a PI falls in, for the car the player is driving.</summary>
    public static string? ClassForPi(int? pi)
    {
        if (pi is null)
        {
            return null;
        }
        foreach (var (cap, name) in PiBands)
        {
            if (pi <= cap)
            {
                return name;
            }
        }
        return null;
    }
}
