using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Die eigenen Rundenzeiten -- mit denselben Filtern wie die Auswertungsseite.
/// </summary>
/// <remarks>
/// ## Woher die Zeilen kommen
///
/// Aus dem eigenen Rundenbestand, NICHT von der Seite: die App laedt keine eigenen
/// Zeiten hoch, auf der Seite stehen sie also gar nicht.
///
/// ## Warum fast alles aus dem PFAD gelesen wird
///
/// 522 Runden sind am 2026-09-24 gut 100 MB -- fast alles davon Messpunkte. Sie
/// jedes Mal vollstaendig zu lesen, kostete Sekunden je Oeffnen des Reiters. Der
/// Pfad traegt aber schon fast alles, was ein Filter braucht:
///
///     laps/&lt;kurs&gt;/&lt;klasse&gt;/car&lt;ordinal&gt;/&lt;ordinal-PI-antrieb-zyl-drehzahl-leerlauf&gt;/&lt;tag&gt;/
///          &lt;datum&gt;_&lt;uhrzeit&gt;_&lt;sekunden&gt;s[_standing][_sprint].json
///
/// Nur der MODUS (Freifahrt, Rivalen, ...) steht nicht darin -- und der steht in der
/// Datei ganz am Ende, hinter den Messpunkten. Gelesen wird darum das letzte
/// Kilobyte, nicht die ganze Datei.
///
/// ## Welche Filter der Seite hier NICHT greifen, und warum
///
/// Die Fahrhilfen (ABS, TCS, STM, Reibung, Bremshilfe, Super Easy), die Schaltung
/// und "gueltig/ungueltig". Das sind Angaben der BESTENLISTE des Spiels; die
/// Telemetrie meldet keine davon. Sie hier anzubieten hiesse, einen Filter zu
/// zeigen, der jede Runde durchlaesst -- ein Schalter, der nichts tut, ist
/// schlimmer als keiner, weil man sich auf ihn verlaesst.
/// </remarks>
internal static class OwnTimes
{
    internal sealed record Lap(
        string Course, string CourseName, string Klass, int Ordinal, string TuneKey,
        int Pi, double Seconds, bool Standing, bool Sprint, DateTime When,
        string Mode, string Path);

    /// <summary>Die Filter -- dieselben Namen wie auf der Seite, soweit anwendbar.</summary>
    internal sealed class Filter
    {
        public HashSet<string> Categories { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Classes { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Courses { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Makes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Countries { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Types { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>down | same | up -- wie auf der Seite.</summary>
        public HashSet<string> Tunes { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Modes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int? YearFrom { get; set; }
        public int? YearTo { get; set; }
        /// <summary>any | standing | flying</summary>
        public string Start { get; set; } = "any";
        /// <summary>any | lap | sprint</summary>
        public string Kind { get; set; } = "any";
        public string CarSearch { get; set; } = string.Empty;
        /// <summary>Je Auto und Kurs nur die schnellste Runde -- wie die Seite.</summary>
        public bool BestOnly { get; set; } = true;
    }

    /// <summary>Eine Zeile der Anzeige: die Runde plus das, was man ueber das Auto weiss.</summary>
    internal sealed record Row(Lap Lap, string CarName, string? Make, string? Country,
                               string? Type, int? Year, string? Tune, string? Category,
                               double BestOnCourse);

    private static readonly Regex Datei = new(
        @"^(\d{4}-\d{2}-\d{2})_(\d{2}-\d{2}-\d{2})_([0-9]+(?:\.[0-9]+)?)s(.*)\.json$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ModusAmEnde = new(
        "\"mode\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.CultureInvariant);

    private static List<Lap>? _speicher;
    private static DateTime _stand = DateTime.MinValue;
    private static string? _wurzel;

    /// <summary>Alle eigenen Runden. Zwischengespeichert nach Schreibzeit des Bestands.</summary>
    public static IReadOnlyList<Lap> All(string? root = null)
    {
        var wurzel = root ?? LapArchive.Root;
        DateTime stand;
        try
        {
            stand = Directory.Exists(wurzel) ? Directory.GetLastWriteTimeUtc(wurzel)
                                             : DateTime.MinValue;
        }
        catch (Exception) { stand = DateTime.MinValue; }
        if (_speicher is not null && _wurzel == wurzel && stand == _stand)
        {
            return _speicher;
        }

        var raus = Einlesen(wurzel);
        _speicher = raus;
        _stand = stand;
        _wurzel = wurzel;
        return raus;
    }

    /// <summary>Alle eigenen Runden, frisch gelesen -- ohne den Zwischenspeicher.</summary>
    /// <remarks>Fuer die eigenen Rekorde, die im Hintergrund laden (PersonalRecords).</remarks>
    internal static List<Lap> Einlesen(string wurzel)
    {
        var namen = new Dictionary<string, string>(StringComparer.Ordinal);
        var raus = new List<Lap>();
        if (Directory.Exists(wurzel))
        {
            foreach (var datei in Directory.EnumerateFiles(wurzel, "*.json",
                                                           SearchOption.AllDirectories))
            {
                var lap = AusPfad(wurzel, datei, namen);
                if (lap is not null) { raus.Add(lap); }
            }
        }
        return raus;
    }

    /// <summary>Eine einzelne, eben abgelegte Runde -- genau wie der Reiter sie liest.</summary>
    internal static Lap? AusDatei(string wurzel, string datei) =>
        AusPfad(wurzel, datei, new Dictionary<string, string>(StringComparer.Ordinal));

    private static Lap? AusPfad(string wurzel, string datei,
                                Dictionary<string, string> namen)
    {
        var name = System.IO.Path.GetFileName(datei);
        if (name.Equals("course.json", StringComparison.OrdinalIgnoreCase)) { return null; }

        var teile = System.IO.Path.GetRelativePath(wurzel, datei)
                        .Split(System.IO.Path.DirectorySeparatorChar,
                               System.IO.Path.AltDirectorySeparatorChar);
        // kurs / klasse / carN / tune / tag / datei
        if (teile.Length < 6) { return null; }
        // Die Kennung, nicht der Ordnername: der heisst inzwischen "Soni Circuit (course_…)".
        var kurs = LapArchive.KennungAus(teile[0]) ?? teile[0];
        var klasse = teile[1];
        if (!teile[2].StartsWith("car", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(teile[2].AsSpan(3), NumberStyles.Integer,
                             CultureInfo.InvariantCulture, out var ordinal))
        {
            return null;
        }
        var tune = teile[3];
        var m = Datei.Match(name);
        if (!m.Success
            || !double.TryParse(m.Groups[3].Value, NumberStyles.Float,
                                CultureInfo.InvariantCulture, out var sekunden))
        {
            return null;
        }
        var wann = DateTime.TryParseExact(
            m.Groups[1].Value + " " + m.Groups[2].Value.Replace('-', ':'),
            "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out var w) ? w : DateTime.MinValue;
        var flags = m.Groups[4].Value.ToLowerInvariant();

        // PI aus dem Abstimmungsschluessel: ordinal-PI-antrieb-zyl-drehzahl-leerlauf.
        var pi = 0;
        var tuneTeile = tune.Split('-');
        if (tuneTeile.Length >= 2) { int.TryParse(tuneTeile[1], out pi); }

        if (!namen.TryGetValue(kurs, out var kursName))
        {
            kursName = CourseShape.KursName(wurzel, kurs);
            // Ein unbenannter Kurs traegt in course.json seinen Ordnernamen als
            // "Name". Das ist KEIN Name: als solcher sortierte er zwischen
            // "Coastline" und "Daikoku" und zeigte "course_-1850_1575_to_...".
            if (OrdnerKurs.IsMatch(kursName)) { kursName = string.Empty; }
            namen[kurs] = kursName;
        }

        return new Lap(kurs, kursName, klasse, ordinal, tune, pi, sekunden,
                       flags.Contains("_standing"), flags.Contains("_sprint"), wann,
                       ModusVon(datei), datei);
    }

    /// <summary>Den Modus aus dem letzten Kilobyte lesen -- er steht hinter den Messpunkten.</summary>
    private static string ModusVon(string datei)
    {
        try
        {
            using var strom = File.OpenRead(datei);
            var laenge = strom.Length;
            var n = (int)Math.Min(1024, laenge);
            strom.Seek(laenge - n, SeekOrigin.Begin);
            var puffer = new byte[n];
            var gelesen = strom.Read(puffer, 0, n);
            var m = ModusAmEnde.Match(Encoding.UTF8.GetString(puffer, 0, gelesen));
            return m.Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : "unknown";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    /// <summary>Filter anwenden. Der Berater liefert Name, Marke, Herkunft und Kategorie.</summary>
    public static List<Row> Query(IReadOnlyList<Lap> laps, Filter f, RivalsAdvisor? adv)
    {
        var data = adv?.Data;
        string? KategorieVon(string kursName)
        {
            if (adv is null || string.IsNullOrEmpty(kursName)) { return null; }
            return adv.CategoryOf(new[] { kursName });
        }

        // Die schnellste Runde je VERGLEICHSGRUPPE: Kurs, Klasse, Startart, Sprint.
        //
        // Nicht nur je Kurs. Erst stand hier der Kurs allein, und dann lag eine
        // STEHEND gestartete Runde 1,4 s "hinter" einer fliegenden -- ein Abstand,
        // der fast ganz aus dem Start besteht und nicht aus dem Auto. Stehend gegen
        // fliegend wird in diesem Projekt nie verglichen (siehe Duels in OwnCars),
        // und ein C-Auto 47 s hinter einem S1-Auto sagt auch nichts. Gemessen wird
        // darum nur gegen Runden, gegen die ein Vergleich etwas bedeutet.
        string Gruppe(Lap l) => l.Course + "|" + l.Klass + "|" + l.Standing + "|" + l.Sprint;
        var bestJeKurs = new Dictionary<string, double>(StringComparer.Ordinal);

        var zeilen = new List<Row>();
        foreach (var lap in laps)
        {
            if (f.Classes.Count > 0 && !f.Classes.Contains(lap.Klass)) { continue; }
            if (f.Courses.Count > 0 && !f.Courses.Contains(lap.Course)) { continue; }
            if (f.Modes.Count > 0 && !f.Modes.Contains(lap.Mode)) { continue; }
            if (f.Start == "standing" && !lap.Standing) { continue; }
            if (f.Start == "flying" && lap.Standing) { continue; }
            if (f.Kind == "sprint" && !lap.Sprint) { continue; }
            if (f.Kind == "lap" && lap.Sprint) { continue; }

            var index = adv?.CarIndexForId(lap.Ordinal);
            var name = index is { } ix ? adv!.RealCarName(ix) : null;
            var meta = index is { } mi && data is not null && mi < data.CarMeta.Count
                ? data.CarMeta[mi] : null;

            if (f.Makes.Count > 0 && (meta?.Make is null || !f.Makes.Contains(meta.Make))) { continue; }
            if (f.Countries.Count > 0 && (meta?.Country is null || !f.Countries.Contains(meta.Country))) { continue; }
            if (f.Types.Count > 0 && (meta?.Type is null || !f.Types.Contains(meta.Type))) { continue; }
            if (f.YearFrom is { } von && (meta?.Year is null || meta.Year < von)) { continue; }
            if (f.YearTo is { } bis && (meta?.Year is null || meta.Year > bis)) { continue; }

            // "tune" wie auf der Seite: Klasse der Runde gegen Serienklasse des Autos.
            var tune = TuneVon(meta?.StockClass, lap.Klass);
            if (f.Tunes.Count > 0 && (tune is null || !f.Tunes.Contains(tune))) { continue; }

            var kategorie = KategorieVon(lap.CourseName);
            if (f.Categories.Count > 0 && (kategorie is null || !f.Categories.Contains(kategorie)))
            {
                continue;
            }

            // KEINE ERFUNDENE BESCHRIFTUNG. "car 4118" sieht aus wie ein Name und ist
            // keiner -- genau das hat der Nutzer am Streifen vor dem Rennen
            // beanstandet. Hier wird die Zeile aber NICHT weggelassen: es ist eine
            // EIGENE Runde, und sie zu verstecken hiesse, dem Nutzer seine eigenen
            // Daten zu unterschlagen. Also bleibt sie, und sagt, was sie ist.
            var anzeigename = name ?? $"Unknown car (id {lap.Ordinal})";
            if (f.CarSearch.Length > 0
                && anzeigename.IndexOf(f.CarSearch, StringComparison.OrdinalIgnoreCase) < 0
                && (meta?.Make ?? string.Empty).IndexOf(f.CarSearch,
                                                        StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            if (!bestJeKurs.TryGetValue(Gruppe(lap), out var b) || lap.Seconds < b)
            {
                bestJeKurs[Gruppe(lap)] = lap.Seconds;
            }
            zeilen.Add(new Row(lap, anzeigename, meta?.Make, meta?.Country, meta?.Type,
                               meta?.Year, tune, kategorie, 0));
        }

        if (f.BestOnly)
        {
            // Je Auto, Kurs, Klasse und Startart nur die schnellste -- stehend gegen
            // fliegend wird nie verglichen, das kostet mehrere Sekunden.
            zeilen = zeilen
                .GroupBy(r => (r.Lap.Course, r.Lap.Ordinal, r.Lap.Klass, r.Lap.Standing,
                               r.Lap.Sprint))
                .Select(g => g.OrderBy(r => r.Lap.Seconds).First())
                .ToList();
        }

        return zeilen
            .Select(r => r with { BestOnCourse = bestJeKurs[Gruppe(r.Lap)] })
            .OrderBy(r => r.Lap.CourseName.Length == 0 ? "~" + r.Lap.Course : r.Lap.CourseName,
                     StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Lap.Seconds)
            .ToList();
    }

    /// <summary>Ein Auto in der eigenen Wertung einer Klasse.</summary>
    internal sealed record Standing(string Klass, int Place, int Ordinal, string CarName,
                                    int Points, int Present, int Boards, int Wins,
                                    double TotalSeconds, int Inherited, string? Tune);

    /// <summary>
    /// Die eigenen Autos gegeneinander -- mit der Wertung der Auswertungsseite.
    /// </summary>
    /// <remarks>
    /// ## Dieselbe Rechnung wie die Seite (RivalsAdvisor.BuildClassTable)
    ///
    /// Jede Vergleichsgruppe ist ein eigenes kleines Board: Kurs, Klasse, Startart,
    /// Sprint -- stehend gegen fliegend wird nie verglichen (siehe Query). Darauf
    /// steht je Auto seine beste eigene Runde. Platz 1 bekommt so viele Punkte, wie
    /// Autos dort gefahren sind, Platz 2 einen weniger, und so fort. Die Punkte
    /// zaehlen je Klasse zusammen. Fuer die Zeitsumme erbt ein Auto, das einen Kurs
    /// nicht gefahren ist, die langsamste Zeit dort -- sonst waere ein Auto mit
    /// weniger Kursen immer "schneller".
    ///
    /// Gleichstaende fallen wie auf der Seite nach der Reihenfolge des Auftretens
    /// (stabile Sortierung): die Boards nach Schluessel, darin nach Zeit.
    /// </remarks>
    /// <param name="zeilen">Die gefilterten Zeilen aus <see cref="Query"/>; je Auto
    /// und Gruppe zaehlt ohnehin nur die schnellste.</param>
    /// <param name="nachPunkten">Nach Punkten (wie die Seite vorgibt) oder nach Zeitsumme.</param>
    public static List<Standing> Standings(IEnumerable<Row> zeilen, bool nachPunkten)
    {
        string Gruppe(Lap l) => l.Course + "|" + l.Klass + "|" + l.Standing + "|" + l.Sprint;
        var beste = zeilen
            .GroupBy(r => (Gruppe(r.Lap), r.Lap.Ordinal))
            .Select(g => g.OrderBy(r => r.Lap.Seconds).First())
            .ToList();

        var raus = new List<Standing>();
        foreach (var klasse in beste.Select(r => r.Lap.Klass).Distinct()
                                    .OrderBy(k => Array.IndexOf(Reihe, k) is var i && i < 0 ? 99 : i)
                                    .ThenBy(k => k, StringComparer.Ordinal))
        {
            var boards = beste.Where(r => r.Lap.Klass == klasse)
                              .GroupBy(r => Gruppe(r.Lap))
                              .OrderBy(g => g.Key, StringComparer.Ordinal)
                              .Select(g => g.OrderBy(r => r.Lap.Seconds).ToList())
                              .ToList();
            var reihenfolge = new List<int>();
            var punkte = new Dictionary<int, int>();
            var dabei = new Dictionary<int, int>();
            var siege = new Dictionary<int, int>();
            var namen = new Dictionary<int, (string Name, string? Tune)>();
            foreach (var board in boards)
            {
                var n = board.Count;
                for (var i = 0; i < n; i++)
                {
                    var r = board[i];
                    var auto = r.Lap.Ordinal;
                    if (!punkte.ContainsKey(auto))
                    {
                        reihenfolge.Add(auto);
                        punkte[auto] = 0; dabei[auto] = 0; siege[auto] = 0;
                        namen[auto] = (r.CarName, r.Tune);
                    }
                    // Platz 1 bekommt so viele Punkte, wie Autos auf diesem Board stehen.
                    punkte[auto] += n - i;
                    dabei[auto] += 1;
                    if (i == 0) { siege[auto] += 1; }
                }
            }

            var summe = new Dictionary<int, (double Sekunden, int Geerbt)>();
            foreach (var auto in reihenfolge)
            {
                double s = 0;
                var geerbt = 0;
                foreach (var board in boards)
                {
                    var eigene = board.FirstOrDefault(r => r.Lap.Ordinal == auto);
                    if (eigene is not null) { s += eigene.Lap.Seconds; continue; }
                    s += board[^1].Lap.Seconds;
                    geerbt++;
                }
                summe[auto] = (s, geerbt);
            }

            var geordnet = nachPunkten
                ? reihenfolge.OrderByDescending(a => punkte[a]).ToList()
                : reihenfolge.OrderBy(a => summe[a].Sekunden).ToList();
            for (var i = 0; i < geordnet.Count; i++)
            {
                var a = geordnet[i];
                raus.Add(new Standing(klasse, i + 1, a, namen[a].Name, punkte[a], dabei[a],
                                      boards.Count, siege[a], summe[a].Sekunden,
                                      summe[a].Geerbt, namen[a].Tune));
            }
        }
        return raus;
    }

    // GENAU FULL_CLASS_ORDER der Seite, samt "X" am Ende. Ohne das X bekaeme ein
    // X-Auto auf der Seite "up" oder "down" und hier gar nichts -- derselbe Filter
    // haette an zwei Stellen zwei Antworten.
    private static readonly string[] Reihe = { "D", "C", "B", "A", "S1", "S2", "R", "X" };

    /// <summary>down | same | up, oder null -- dieselbe Regel wie tuneOf auf der Seite.</summary>
    public static string? TuneVon(string? serienKlasse, string rundenKlasse)
    {
        var s = Array.IndexOf(Reihe, serienKlasse ?? string.Empty);
        var r = Array.IndexOf(Reihe, rundenKlasse);
        if (s < 0 || r < 0) { return null; }
        return r < s ? "down" : r > s ? "up" : "same";
    }

    public static string TimeText(double s) => OwnCars.TimeText(s);

    // ANZEIGE, NICHT KENNUNG. Gespeichert und gefiltert wird immer die Kennung
    // ("freeroam", "same", der Ordnername); gezeigt wird der Name in der Sprache
    // der App. So bleibt ein Filter gueltig, wenn jemand die Sprache wechselt.

    /// <summary>Anzeigename eines Modus, wie ihn die Runde gespeichert hat.</summary>
    public static string ModeText(string? mode) => mode switch
    {
        "auto" => Loc.T("automatic"),
        "rivals" => Loc.T("Rivals"),
        "solo" => Loc.T("Solo"),
        "coop" => Loc.T("Co-op"),
        // Anmeldeschirm ohne Horizon-Play-Reihe: allein oder Koop -- der Schirm sagt
        // nicht, welches von beiden (seit 2026-09-28).
        "race" => Loc.T("Solo / co-op race"),
        // Eigenname des Spiels, in jeder Sprache derselbe.
        "horizon-play" => "Horizon Play",
        "freeroam" => Loc.T("Free roam"),
        null or "" or "unknown" => Loc.T("unknown"),
        _ => mode,
    };

    /// <summary>Anzeigename von down | same | up.</summary>
    public static string TuneText(string? tune) => tune switch
    {
        "down" => Loc.T("down"),
        "same" => Loc.T("stock class"),
        "up" => Loc.T("up"),
        _ => "-",
    };

    // Der Ordnername eines unbenannten Kurses: Start- und Zielpunkt, auf Meter
    // gerundet. "course_-1850_1575_to_-1850_1575" liest sich wie ein Fehler.
    private static readonly System.Text.RegularExpressions.Regex OrdnerKurs = new(
        @"^course_(-?\d+)_(-?\d+)_to_(-?\d+)_(-?\d+)$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Ist das ein Ordnername ("course_x_z_to_x_z") statt eines Namens?</summary>
    public static bool IsFolderKey(string? name) => name is not null && OrdnerKurs.IsMatch(name);

    /// <summary>
    /// Der Name des Kurses, oder fuer einen unbenannten (EventLab, freie Fahrt)
    /// "Unnamed course · x/z → x/z". Rundkurse nennen nur einen Punkt.
    /// </summary>
    public static string CourseText(string course, string? courseName)
    {
        if (!string.IsNullOrEmpty(courseName)) { return courseName; }
        var m = OrdnerKurs.Match(course);
        if (!m.Success) { return course; }
        var start = $"{m.Groups[1].Value}/{m.Groups[2].Value}";
        var ziel = $"{m.Groups[3].Value}/{m.Groups[4].Value}";
        return Loc.T("Unnamed course") + " · " + (start == ziel ? start : start + " → " + ziel);
    }
}
