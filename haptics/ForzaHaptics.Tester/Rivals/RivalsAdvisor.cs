using System.Globalization;
using System.Text;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// The website's scoring, in C#: which cars for these routes, and where one car stands.
/// </summary>
/// <remarks>
/// This is a port of <c>pickCars</c>, <c>classTable</c> and the place ordering in
/// <c>scripts/build_analytics_site.py</c>, keeping their names so a change there is
/// easy to carry over. It is the THIRD implementation of those rules (the page's
/// JavaScript, the Python advisor, this), and three implementations of a changing
/// rule drift silently -- so <c>--dump-class-table</c> exists purely so
/// <c>scripts/test_rivals_advisor.py</c> can run the page's own JavaScript over the
/// same payload and fail on any divergence. Do not change a rule here without
/// running that test.
/// </remarks>
internal sealed class RivalsAdvisor
{
    /// <summary>Slowest to fastest. "X" exists only as a stock class, never as a board.</summary>
    public static readonly string[] ClassOrder = { "D", "C", "B", "A", "S1", "S2", "R" };
    private static readonly string[] FullClassOrder = { "D", "C", "B", "A", "S1", "S2", "R", "X" };

    /// <summary>A board thinner than this scores points but stays out of the time sum.</summary>
    private const int MinBoardForTime = 1000;

    public static readonly IReadOnlyDictionary<string, string> TuneLabel =
        new Dictionary<string, string>
        {
            ["down"] = "detuned", ["same"] = "stock class", ["up"] = "tuned up",
        };

    private readonly RivalsDataset _d;
    private readonly Dictionary<string, int> _bit = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _normTracks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _carByName = new(StringComparer.Ordinal);
    private readonly Dictionary<int, int> _carIndexById = new();
    private readonly Dictionary<string, ClassTable> _tableCache = new(StringComparer.Ordinal);

    public RivalsAdvisor(RivalsDataset dataset)
    {
        _d = dataset;
        for (var i = 0; i < _d.Flags.Count; i++)
        {
            _bit[_d.Flags[i]] = 1 << i;
        }
        foreach (var name in _d.Tracks)
        {
            _normTracks[TextMatch.Normalise(name)] = name;
        }
        for (var i = 0; i < _d.CarNames.Count; i++)
        {
            var key = TextMatch.Normalise(_d.CarNames[i]);
            if (!_carByName.ContainsKey(key))
            {
                _carByName[key] = i;
            }
        }
        for (var i = 0; i < _d.CarIds.Count; i++)
        {
            _carIndexById.TryAdd(_d.CarIds[i], i);
        }
    }

    public RivalsDataset Data => _d;
    public IReadOnlyList<string> Tracks => _d.Tracks;
    public IReadOnlyList<string> Categories => _d.Categories;
    public int BoardCount => _d.Boards.Count;

    /// <summary>Board classes present, slowest first.</summary>
    public IReadOnlyList<string> ClassNames
    {
        get
        {
            var seen = new HashSet<string>(_d.Classes, StringComparer.Ordinal);
            var order = ClassOrder.Where(seen.Contains).ToList();
            order.AddRange(_d.Classes.Where(k => !ClassOrder.Contains(k)));
            return order;
        }
    }

    public string CarName(int carIndex) =>
        carIndex >= 0 && carIndex < _d.CarNames.Count ? _d.CarNames[carIndex] : $"Car #{carIndex}";

    /// <summary>
    /// Ist das ein WIRKLICHER Autoname -- oder nur eine Kennung im Namensfeld?
    /// </summary>
    /// <remarks>
    /// Der Datensatz traegt fuer unbenannte Autos ausdruecklich "Car #3118" ein, und
    /// die Auswertungsseite will das auch so: dort bleibt das Auto damit sichtbar und
    /// ueber seine Kennung durchsuchbar, und die Seite schreibt selbst dazu, dass ein
    /// geratener Name schlimmer waere als keiner. Am 2026-09-24 sind 14 von 653
    /// Eintraegen so.
    ///
    /// IM OVERLAY IST DAS WERTLOS. Der Streifen vor dem Rennen beantwortet "welches
    /// Auto nehme ich" -- und eine Zeile "Car #3118" kann man im Automenue nicht
    /// finden. Was man nicht auswaehlen kann, hilft bei der Auswahl nicht.
    ///
    /// Beide Seiten haben recht, sie beantworten nur verschiedene Fragen. Darum
    /// entscheidet hier EINE Stelle, was ein Name ist, statt an jeder Anzeige neu
    /// gegen "Car #" zu pruefen.
    /// </remarks>
    public static bool IsRealCarName(string? name)
    {
        var text = (name ?? string.Empty).Trim();
        if (text.Length == 0) { return false; }
        // "Car #3118", "car 3118", "Car#3118" -- alles dieselbe Nicht-Auskunft.
        return !System.Text.RegularExpressions.Regex.IsMatch(
            text, @"^car\s*#?\s*\d+$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>Der Name dieses Autos, oder <c>null</c>, wenn keiner bekannt ist.</summary>
    public string? RealCarName(int carIndex)
    {
        if (carIndex < 0 || carIndex >= _d.CarNames.Count) { return null; }
        var name = _d.CarNames[carIndex];
        return IsRealCarName(name) ? name : null;
    }

    public int? CarIndexForId(int carId) =>
        _carIndexById.TryGetValue(carId, out var index) ? index : null;

    /// <summary>Wie viele Autos der Datensatz kennt.</summary>
    public int CarCount => _d.CarNames.Count;

    /// <summary>Die Kennung (= Telemetrie-Ordinal) zu einem Platz im Datensatz.</summary>
    public int? CarIdOf(int carIndex) =>
        carIndex >= 0 && carIndex < _d.CarIds.Count ? _d.CarIds[carIndex] : null;

    /// <summary>Which race type these routes belong to, or null if they disagree.</summary>
    /// <remarks>
    /// The screen does not say it anywhere worth reading -- the card's own word for it
    /// is artwork over a photograph -- but the ROUTES say it: a route name belongs to
    /// exactly one category in the dataset. The class is deliberately ignored here, so
    /// the race type is still named on a screen whose class has not been swept yet.
    /// A tie means the answer is not known, and an unknown race type is left off the
    /// panel rather than guessed.
    /// </remarks>
    public string? CategoryOf(IReadOnlyList<string> tracks)
    {
        var count = new int[_d.Categories.Count];
        foreach (var track in tracks.Distinct(StringComparer.Ordinal))
        {
            var found = new HashSet<int>();
            foreach (var board in _d.Boards)
            {
                if (_d.Tracks[board.Track] == track)
                {
                    found.Add(board.Category);
                }
            }
            // A name that sits in two categories says nothing about either.
            if (found.Count == 1)
            {
                count[found.First()]++;
            }
        }
        var best = -1;
        var tied = false;
        for (var at = 0; at < count.Length; at++)
        {
            if (count[at] == 0)
            {
                continue;
            }
            if (best < 0 || count[at] > count[best])
            {
                best = at;
                tied = false;
            }
            else if (count[at] == count[best])
            {
                tied = true;
            }
        }
        return best < 0 || tied ? null : _d.Categories[best];
    }

    public bool HasBoard(string track, string klass, string? category = null) =>
        _d.Boards.Any(b => _d.Tracks[b.Track] == track && _d.Classes[b.Klass] == klass
                           && (category is null || _d.Categories[b.Category] == category));

    // ----------------------------------------------------------------- //
    // filters
    // ----------------------------------------------------------------- //

    /// <summary>Filter to (need, forbid) signature masks, as the page's <c>masks()</c>.</summary>
    /// <remarks>
    /// The overlay defaults to clean="valid": a dirty lap is not a time you can aim
    /// at. Everything else stays open, because narrowing the assists also narrows
    /// which car is represented at all.
    /// </remarks>
    public (int Need, int Forbid) Masks(string clean = "valid", string gear = "any")
    {
        var need = 0;
        var forbid = 0;
        switch (clean)
        {
            case "valid": need |= Bit("clean"); break;
            case "invalid": forbid |= Bit("clean"); break;
        }
        switch (gear)
        {
            case "auto": need |= Bit("autoshift"); break;
            case "manual": forbid |= Bit("autoshift") | Bit("clutch"); break;
            case "clutch": need |= Bit("clutch"); forbid |= Bit("autoshift"); break;
        }
        return (need, forbid);
    }

    private int Bit(string flag) => _bit.TryGetValue(flag, out var value) ? value : 0;

    // ----------------------------------------------------------------- //
    // core scoring
    // ----------------------------------------------------------------- //

    /// <summary>Which lap represents each car on one board -- the page's <c>pickCars</c>.</summary>
    public Dictionary<int, Pick> PickCars(RivalsDataset.Board board, int need, int forbid)
    {
        var gsig = board.GroupSignature;
        var gcar = board.GroupCar;
        var gcount = board.GroupCount;
        var ok = new bool[gsig.Length];
        var laps = new Dictionary<int, List<(int Ms, int Rank)>>();
        var counts = new Dictionary<int, int>();

        for (var g = 0; g < gsig.Length; g++)
        {
            var sig = gsig[g];
            if ((sig & need) != need || (sig & forbid) != 0)
            {
                continue;
            }
            ok[g] = true;
            var car = gcar[g];
            counts[car] = counts.GetValueOrDefault(car) + gcount[g];
            if (!laps.ContainsKey(car))
            {
                laps[car] = new List<(int, int)>();
            }
        }

        var lgrp = board.LapGroup;
        for (var i = 0; i < lgrp.Length; i++)
        {
            var g = lgrp[i];
            if (!ok[g])
            {
                continue;
            }
            laps[gcar[g]].Add((board.LapMs[i], board.LapRank[i]));
        }

        var picks = new Dictionary<int, Pick>();
        foreach (var (car, entries) in laps)
        {
            if (entries.Count == 0)
            {
                continue;
            }
            entries.Sort((a, b) => a.Ms.CompareTo(b.Ms));
            var bestRank = entries.Min(e => e.Rank);
            // The better the car's best lap looks, the deeper into its own times we
            // reach, because a lap at the very top is usually an outlier.
            var wanted = bestRank <= 100 ? 5 : bestRank <= 1000 ? 2 : 1;
            var have = Math.Min(wanted, entries.Count);
            var chosen = entries[have - 1];
            var count = counts[car];
            picks[car] = new Pick(chosen.Ms, chosen.Rank, bestRank, have, wanted,
                                  count, count < wanted);
        }
        return picks;
    }

    /// <summary>Points and time sum for one class -- the page's <c>classTable</c>.</summary>
    public ClassTable BuildClassTable(string klass, IReadOnlyCollection<string>? tracks,
                                      string? category, int need, int forbid)
    {
        var cacheKey = string.Join('', klass,
            tracks is null ? "*" : string.Join(',', tracks.OrderBy(t => t, StringComparer.Ordinal)),
            category ?? "*", need, forbid);
        if (_tableCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var wanted = tracks is null ? null : new HashSet<string>(tracks, StringComparer.Ordinal);
        var entries = new List<TrackEntry>();
        var shallow = new List<string>();
        foreach (var board in _d.Boards)
        {
            if (_d.Classes[board.Klass] != klass)
            {
                continue;
            }
            if (category is not null && _d.Categories[board.Category] != category)
            {
                continue;
            }
            var name = _d.Tracks[board.Track];
            if (wanted is not null && !wanted.Contains(name))
            {
                continue;
            }
            var picks = PickCars(board, need, forbid);
            if (picks.Count == 0)
            {
                continue;
            }
            var worst = picks.Values.Max(p => p.Ms);
            var rows = board.Valid != 0 ? board.Valid : board.Rows;
            var deep = rows >= MinBoardForTime;
            if (!deep)
            {
                shallow.Add(name);
            }
            entries.Add(new TrackEntry(name, klass, picks, worst, deep, rows));
        }

        // Insertion order decides how ties fall, and it has to match the page's Map,
        // so the order is kept explicitly rather than trusted to a Dictionary.
        var order = new List<CarAgg>();
        var byCar = new Dictionary<int, CarAgg>();
        foreach (var entry in entries)
        {
            var ordered = entry.Picks.OrderBy(kv => kv.Value.Ms).ToList();
            var n = ordered.Count;
            for (var index = 0; index < n; index++)
            {
                var (car, pick) = (ordered[index].Key, ordered[index].Value);
                if (!byCar.TryGetValue(car, out var agg))
                {
                    agg = new CarAgg(car);
                    byCar[car] = agg;
                    order.Add(agg);
                }
                // Place 1 scores as many points as there are cars on this board.
                var points = n - index;
                agg.Points += points;
                agg.Present += 1;
                if (pick.Thin)
                {
                    agg.Thin += 1;
                }
                agg.Per[entry.Track] = new CarCell(pick.Ms, points, index + 1, n,
                                                   false, false, false, pick.Rank, pick.Took);
            }
        }

        // A car missing on a track inherits that track's slowest time, so the sum
        // stays comparable across cars that did not run everywhere.
        foreach (var agg in order)
        {
            foreach (var entry in entries)
            {
                if (agg.Per.TryGetValue(entry.Track, out var own))
                {
                    if (entry.Deep)
                    {
                        agg.Ms += own.Ms ?? 0;
                    }
                    else
                    {
                        agg.Per[entry.Track] = own with { OutOfTimeSum = true };
                    }
                    continue;
                }
                if (!entry.Deep)
                {
                    agg.Per[entry.Track] = new CarCell(null, 0, null, entry.Picks.Count,
                                                       false, true, true, null, 0);
                    continue;
                }
                agg.Ms += entry.Worst;
                agg.Per[entry.Track] = new CarCell(entry.Worst, 0, null, entry.Picks.Count,
                                                   true, false, false, null, 0);
            }
        }

        var table = new ClassTable(klass, entries, order,
                                   entries.Count(e => e.Deep), shallow);
        _tableCache[cacheKey] = table;
        return table;
    }

    // ----------------------------------------------------------------- //
    // question 1: what to drive
    // ----------------------------------------------------------------- //

    /// <summary>Rank the cars for a set of routes in one class.</summary>
    /// <remarks>
    /// Routes without a board in this class land in <see cref="Advice.MissingTracks"/>
    /// rather than being dropped silently: the order then answers a smaller question
    /// than was asked, and the panel has to say so.
    /// </remarks>
    public Advice Advise(IReadOnlyList<string> tracks, string klass,
                         string? category = null, string clean = "valid",
                         string gear = "any")
    {
        if (category is null && _d.Categories.Count == 1)
        {
            category = _d.Categories[0];
        }
        var seen = new List<string>();
        foreach (var track in tracks)
        {
            if (!seen.Contains(track, StringComparer.Ordinal))
            {
                seen.Add(track);
            }
        }
        var have = seen.Where(t => HasBoard(t, klass, category)).ToList();
        var missing = seen.Where(t => !have.Contains(t, StringComparer.Ordinal)).ToList();

        var (need, forbid) = Masks(clean, gear);
        var table = BuildClassTable(klass, have.Count > 0 ? have : null, category,
                                    need, forbid);
        return new Advice(klass, have, missing, table.ShallowTracks,
                          Rank(table, "points"), Rank(table, "time"), table.TimeTracks);
    }

    /// <summary>Places by one metric. OrderBy is stable, as the page's Array.sort is.</summary>
    public List<Row> Rank(ClassTable table, string mode)
    {
        var ordered = mode == "points"
            ? table.Cars.OrderByDescending(a => a.Points).ToList()
            : table.Cars.OrderBy(a => a.Ms).ToList();
        var rows = new List<Row>(ordered.Count);
        for (var index = 0; index < ordered.Count; index++)
        {
            var agg = ordered[index];
            var meta = agg.Car < _d.CarMeta.Count ? _d.CarMeta[agg.Car] : null;
            rows.Add(new Row(index + 1, agg.Car, CarName(agg.Car), agg.Points, agg.Ms,
                             agg.Present, agg.Thin, agg.Per, meta?.StockClass,
                             meta?.StockPi, TuneOf(agg.Car, table.Klass)));
        }
        return rows;
    }

    /// <summary>Where the car started, against the class of the board it ran on.</summary>
    public string? TuneOf(int car, string boardClass)
    {
        var meta = car < _d.CarMeta.Count ? _d.CarMeta[car] : null;
        var stock = meta?.StockClass;
        if (string.IsNullOrEmpty(stock))
        {
            return null;
        }
        var si = Array.IndexOf(FullClassOrder, stock);
        var bi = Array.IndexOf(FullClassOrder, boardClass);
        if (si < 0 || bi < 0)
        {
            return null;
        }
        return bi < si ? "down" : bi > si ? "up" : "same";
    }

    // ----------------------------------------------------------------- //
    // question 2: where does my car stand
    // ----------------------------------------------------------------- //

    /// <summary>Every category and class in which this car has a lap at all.</summary>
    /// <remarks>
    /// One entry per (category, class), placed over ALL routes of that class, which
    /// is the website's default view. A class the car never ran is left out -- that
    /// is the answer to "which class should I build this car for": the ones where it
    /// places well.
    /// </remarks>
    public List<Standing> Standings(int car, string clean = "valid", string gear = "any")
    {
        var (need, forbid) = Masks(clean, gear);
        var result = new List<Standing>();
        foreach (var category in _d.Categories)
        {
            foreach (var klass in ClassNames)
            {
                var table = BuildClassTable(klass, null, category, need, forbid);
                if (table.Tracks.Count == 0)
                {
                    continue;
                }
                var mine = table.Cars.FirstOrDefault(a => a.Car == car);
                if (mine is null)
                {
                    continue;
                }
                var byPoints = table.Cars.OrderByDescending(a => a.Points).ToList();
                var byTime = table.Cars.OrderBy(a => a.Ms).ToList();
                result.Add(new Standing(category, klass,
                    byPoints.IndexOf(mine) + 1, byTime.IndexOf(mine) + 1,
                    table.Cars.Count, mine.Points, mine.Ms, mine.Present,
                    table.Tracks.Count, TuneOf(car, klass)));
            }
        }
        return result;
    }

    // ----------------------------------------------------------------- //
    // matching what the screen says
    // ----------------------------------------------------------------- //

    /// <summary>Map one OCR'd line to a route name, or null.</summary>
    /// <remarks>
    /// Matching against a closed vocabulary of 23 names is what makes the screen
    /// reader layout-independent: no row geometry has to be known, only that the
    /// name appears somewhere in the region.
    /// </remarks>
    public (string Track, double Score)? MatchTrack(string text, double cutoff = 0.62)
    {
        var probe = TextMatch.Normalise(text);
        if (probe.Length == 0)
        {
            return null;
        }
        if (_normTracks.TryGetValue(probe, out var exact))
        {
            return (exact, 1.0);
        }
        string? bestName = null;
        var bestScore = 0.0;
        foreach (var (norm, name) in _normTracks)
        {
            var score = TextMatch.Similarity(probe, norm);
            if ((norm.Contains(probe, StringComparison.Ordinal)
                 || probe.Contains(norm, StringComparison.Ordinal)) && probe.Length >= 5)
            {
                score = Math.Max(score, 0.90);
            }
            if (bestName is null || score > bestScore)
            {
                bestName = name;
                bestScore = score;
            }
        }
        return bestName is not null && bestScore >= cutoff ? (bestName, bestScore) : null;
    }

    public int? MatchCarName(string text, double cutoff = 0.72)
    {
        var probe = TextMatch.Normalise(text);
        if (probe.Length == 0)
        {
            return null;
        }
        if (_carByName.TryGetValue(probe, out var hit))
        {
            return hit;
        }
        int? bestIndex = null;
        var bestScore = 0.0;
        foreach (var (norm, index) in _carByName)
        {
            var score = TextMatch.Similarity(probe, norm);
            if ((norm.Contains(probe, StringComparison.Ordinal)
                 || probe.Contains(norm, StringComparison.Ordinal)) && probe.Length >= 6)
            {
                score = Math.Max(score, 0.88);
            }
            if (score > bestScore)
            {
                bestIndex = index;
                bestScore = score;
            }
        }
        return bestScore >= cutoff ? bestIndex : null;
    }

    // ----------------------------------------------------------------- //
    // formatting
    // ----------------------------------------------------------------- //

    public static string LapText(int? ms)
    {
        if (ms is null)
        {
            return "--";
        }
        var minutes = ms.Value / 60_000;
        var seconds = ms.Value % 60_000 / 1000;
        var milli = ms.Value % 1000;
        return minutes > 0
            ? $"{minutes}:{seconds:00}.{milli:000}"
            : $"{seconds}.{milli:000}";
    }

    public static string SumText(long? ms)
    {
        if (ms is null)
        {
            return "--";
        }
        var hours = ms.Value / 3_600_000;
        var minutes = ms.Value % 3_600_000 / 60_000;
        var seconds = ms.Value % 60_000 / 1000.0;
        return hours > 0
            ? $"{hours}:{minutes:00}:{seconds.ToString("00.000", CultureInfo.InvariantCulture)}"
            : $"{minutes}:{seconds.ToString("00.000", CultureInfo.InvariantCulture)}";
    }

    // ----------------------------------------------------------------- //
    // records
    // ----------------------------------------------------------------- //

    internal readonly record struct Pick(int Ms, int Rank, int BestRank, int Took,
                                         int Wanted, int Count, bool Thin);

    internal sealed record TrackEntry(string Track, string Klass,
                                      Dictionary<int, Pick> Picks, int Worst,
                                      bool Deep, int Rows);

    internal sealed record CarCell(int? Ms, int Points, int? Pos, int Of,
                                   bool Substituted, bool Missing, bool OutOfTimeSum,
                                   int? Rank, int Took);

    internal sealed class CarAgg
    {
        public CarAgg(int car) => Car = car;
        public int Car { get; }
        public int Points { get; set; }
        public long Ms { get; set; }
        public int Present { get; set; }
        public int Thin { get; set; }
        public Dictionary<string, CarCell> Per { get; } = new(StringComparer.Ordinal);
    }

    internal sealed record ClassTable(string Klass, List<TrackEntry> Tracks,
                                      List<CarAgg> Cars, int TimeTracks,
                                      List<string> ShallowTracks);

    internal sealed record Row(int Place, int Car, string Name, int Points, long Ms,
                               int Present, int Thin,
                               Dictionary<string, CarCell> Per,
                               string? StockClass, int? StockPi, string? Tune);

    internal sealed record Advice(string Klass, List<string> Tracks,
                                  List<string> MissingTracks, List<string> ShallowTracks,
                                  List<Row> ByPoints, List<Row> ByTime, int TimeTracks);

    internal sealed record Standing(string Category, string Klass, int PlacePoints,
                                    int PlaceTime, int Of, int Points, long Ms,
                                    int Present, int Tracks, string? Tune);
}

/// <summary>Fold OCR noise out of a string, and score two of them against each other.</summary>
internal static class TextMatch
{
    /// <summary>Case, accents and punctuation folded away.</summary>
    public static string Normalise(string text)
    {
        var folded = (text ?? string.Empty).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(folded.Length);
        foreach (var ch in folded)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }
            builder.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }
        return string.Join(' ', builder.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Token-aware ratio: OCR drops and doubles characters, not whole words.</summary>
    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return 0.0;
        }
        var plain = SequenceRatio(a, b);
        var ta = new HashSet<string>(a.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                                     StringComparer.Ordinal);
        var tb = new HashSet<string>(b.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                                     StringComparer.Ordinal);
        if (ta.Count == 0 || tb.Count == 0)
        {
            return plain;
        }
        var union = new HashSet<string>(ta, StringComparer.Ordinal);
        union.UnionWith(tb);
        var jaccard = (double)ta.Count(tb.Contains) / union.Count;
        return Math.Min(1.0, Math.Max(plain, (plain + jaccard) / 2 + jaccard / 4));
    }

    /// <summary>
    /// Python's <c>difflib.SequenceMatcher.ratio()</c>, which the Python advisor uses.
    /// </summary>
    /// <remarks>
    /// Ported rather than swapped for an edit distance on purpose: the cutoffs
    /// (0.62 for a route, 0.72 for a car) were tuned against difflib's numbers, and
    /// a different metric would quietly move every threshold.
    /// </remarks>
    private static double SequenceRatio(string a, string b)
    {
        var matches = MatchingBlocks(a, b, 0, a.Length, 0, b.Length);
        return 2.0 * matches / (a.Length + b.Length);
    }

    private static int MatchingBlocks(string a, string b, int alo, int ahi, int blo, int bhi)
    {
        var (i, j, k) = LongestMatch(a, b, alo, ahi, blo, bhi);
        if (k == 0)
        {
            return 0;
        }
        return k
               + MatchingBlocks(a, b, alo, i, blo, j)
               + MatchingBlocks(a, b, i + k, ahi, j + k, bhi);
    }

    private static (int I, int J, int K) LongestMatch(string a, string b,
                                                      int alo, int ahi, int blo, int bhi)
    {
        var b2j = new Dictionary<char, List<int>>();
        for (var j = blo; j < bhi; j++)
        {
            if (!b2j.TryGetValue(b[j], out var list))
            {
                list = new List<int>();
                b2j[b[j]] = list;
            }
            list.Add(j);
        }

        int besti = alo, bestj = blo, bestsize = 0;
        var j2len = new Dictionary<int, int>();
        for (var i = alo; i < ahi; i++)
        {
            var newj2len = new Dictionary<int, int>();
            if (b2j.TryGetValue(a[i], out var positions))
            {
                foreach (var j in positions)
                {
                    if (j < blo)
                    {
                        continue;
                    }
                    if (j >= bhi)
                    {
                        break;
                    }
                    var k = j2len.GetValueOrDefault(j - 1) + 1;
                    newj2len[j] = k;
                    if (k > bestsize)
                    {
                        besti = i - k + 1;
                        bestj = j - k + 1;
                        bestsize = k;
                    }
                }
            }
            j2len = newj2len;
        }
        return (besti, bestj, bestsize);
    }
}
