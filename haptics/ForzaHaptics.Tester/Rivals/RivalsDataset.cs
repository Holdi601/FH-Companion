using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// The Rivals dataset, exactly as <c>data/analytics/laps.json</c> ships it.
/// </summary>
/// <remarks>
/// The same file the website is built from. Nothing is recomputed on load and no
/// field is renamed: the scoring below has to stay comparable to the page's own
/// JavaScript line for line, and a helpful rename here is the first step towards
/// a scoreboard that quietly disagrees with the site.
///
/// Boards carry their laps in five parallel arrays -- a group table (gcar, gsig,
/// gcount) and a lap table pointing into it (lgrp, lms, lrank). That is a lot
/// cheaper than an object per lap: a board can hold tens of thousands.
///
/// DER STICHTAG (seit 2026-11-10, <see cref="Stichtag"/>): <see cref="Load(string)"/>
/// liefert die Sicht ab dem eingestellten Tag -- Boards ohne Zeile ab ihm fehlen, die
/// uebrigen tragen nur die Runden und Zaehlungen ab ihm. Die Felder je Tag (days, lday,
/// gdays, dstat) beschreibt server/stichtag.py; die Rechnung hier ist dieselbe.
/// </remarks>
internal sealed class RivalsDataset
{
    [JsonPropertyName("categories")] public List<string> Categories { get; set; } = new();
    [JsonPropertyName("tracks")] public List<string> Tracks { get; set; } = new();
    [JsonPropertyName("classes")] public List<string> Classes { get; set; } = new();
    [JsonPropertyName("carNames")] public List<string> CarNames { get; set; } = new();
    [JsonPropertyName("carIds")] public List<int> CarIds { get; set; } = new();
    [JsonPropertyName("carMeta")] public List<CarMetaEntry?> CarMeta { get; set; } = new();
    [JsonPropertyName("flags")] public List<string> Flags { get; set; } = new();
    [JsonPropertyName("boards")] public List<Board> Boards { get; set; } = new();
    [JsonPropertyName("meta")] public JsonElement Meta { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Ab welchem Tag diese Sicht gilt (JJJJ-MM-TT) -- null: alle Daten.</summary>
    [JsonIgnore] public string? Seit { get; private set; }

    /// <summary>Den Datensatz laden, ab dem eingestellten Stichtag (<see cref="Stichtag.Aktuell"/>).</summary>
    public static RivalsDataset Load(string path) => Load(path, Stichtag.Aktuell);

    /// <summary>Den Datensatz laden, ab <paramref name="seit"/> (JJJJ-MM-TT; null = alle Daten).</summary>
    /// <remarks>
    /// Wirft nur, wenn die DATEI keine Boards hat. Bleibt ab dem Stichtag keins uebrig, ist
    /// das kein Fehler: nach der Loeschung der Bestenlisten ist erst einmal nichts gescannt.
    /// </remarks>
    public static RivalsDataset Load(string path, string? seit)
    {
        using var stream = File.OpenRead(path);
        var loaded = JsonSerializer.Deserialize<RivalsDataset>(stream, Options)
                     ?? throw new InvalidDataException($"{path} did not parse as a dataset");
        if (loaded.Boards.Count == 0)
        {
            throw new InvalidDataException($"{path} holds no boards");
        }
        return loaded.Ab(seit);
    }

    /// <summary>
    /// Die Sicht ab dem Stichtag <paramref name="seit"/>: dieselben Autos, Strecken und Klassen,
    /// nur die Boards mit einer Zeile ab ihm -- und darin nur, was ab ihm gescannt wurde.
    /// </summary>
    public RivalsDataset Ab(string? seit)
    {
        if (seit is null) { return this; }
        // Wie viele Runden je Gruppe der Bau behielt (meta.laps_per_group) -- so viele bleiben.
        var jeGruppe = 5;
        if (Meta.ValueKind == JsonValueKind.Object && Meta.TryGetProperty("laps_per_group", out var n)
            && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var zahl) && zahl >= 1)
        {
            jeGruppe = zahl;
        }
        var boards = new List<Board>();
        foreach (var b in Boards)
        {
            if (b.Ab(seit, jeGruppe) is { } ab) { boards.Add(ab); }
        }
        return new RivalsDataset
        {
            Categories = Categories, Tracks = Tracks, Classes = Classes, CarNames = CarNames,
            CarIds = CarIds, CarMeta = CarMeta, Flags = Flags, Meta = Meta,
            Boards = boards, Seit = seit,
        };
    }

    /// <summary>Where the dataset lives, walking up from the running binary.</summary>
    /// <remarks>
    /// The app is normally started from bin/Debug/net9.0-windows.../, so the
    /// repository root is several levels up. Walking rather than hard-coding a
    /// depth keeps a published single-file build working too.
    /// </remarks>
    public static string? FindDefaultPath()
    {
        var candidates = new List<string>();
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            candidates.Add(Path.Combine(dir.FullName, "data", "analytics", "laps.json"));
            dir = dir.Parent;
        }
        candidates.Add(Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile), "Documents", "Forza",
            "data", "analytics", "laps.json"));
        return candidates.FirstOrDefault(File.Exists);
    }

    internal sealed class CarMetaEntry
    {
        [JsonPropertyName("make")] public string? Make { get; set; }
        [JsonPropertyName("year")] public int? Year { get; set; }
        [JsonPropertyName("country")] public string? Country { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("stockClass")] public string? StockClass { get; set; }
        [JsonPropertyName("stockPi")] public int? StockPi { get; set; }
    }

    internal sealed class Board
    {
        [JsonPropertyName("c")] public int Category { get; set; }
        [JsonPropertyName("t")] public int Track { get; set; }
        [JsonPropertyName("k")] public int Klass { get; set; }
        [JsonPropertyName("rows")] public int Rows { get; set; }
        [JsonPropertyName("valid")] public int Valid { get; set; }
        [JsonPropertyName("invalid")] public int Invalid { get; set; }
        [JsonPropertyName("maxRank")] public int MaxRank { get; set; }

        [JsonPropertyName("gcar")] public int[] GroupCar { get; set; } = Array.Empty<int>();
        [JsonPropertyName("gsig")] public int[] GroupSignature { get; set; } = Array.Empty<int>();
        [JsonPropertyName("gcount")] public int[] GroupCount { get; set; } = Array.Empty<int>();
        [JsonPropertyName("lgrp")] public int[] LapGroup { get; set; } = Array.Empty<int>();
        [JsonPropertyName("lms")] public int[] LapMs { get; set; } = Array.Empty<int>();
        [JsonPropertyName("lrank")] public int[] LapRank { get; set; } = Array.Empty<int>();

        // Die Felder je Tag (seit 2026-11-10, server/stichtag.py). Fehlt "days", stammt das
        // Board aus einem Datensatz vor diesem Format -- also von vor jedem Stichtag.
        [JsonPropertyName("days")] public string[]? Days { get; set; }
        [JsonPropertyName("lday")] public int[]? LapDay { get; set; }
        [JsonPropertyName("gdays")] public int[]? GroupDayCount { get; set; }
        [JsonPropertyName("dstat")] public int[]? DayStats { get; set; }
        // Runden NUR fuer Sichten ab einem Stichtag (server/stichtag.py): die fuenf schnellsten
        // ab einem spaeteren Tag, die keine der fuenf schnellsten ueber alles sind.
        [JsonPropertyName("xgrp")] public int[]? ExtraGroup { get; set; }
        [JsonPropertyName("xms")] public int[]? ExtraMs { get; set; }
        [JsonPropertyName("xrank")] public int[]? ExtraRank { get; set; }
        [JsonPropertyName("xday")] public int[]? ExtraDay { get; set; }

        /// <summary>Dieses Board ab dem Stichtag -- oder null, wenn ab ihm nichts gescannt wurde.</summary>
        /// <remarks>Dieselbe Rechnung wie board_ab in server/stichtag.py.</remarks>
        public Board? Ab(string seit, int jeGruppe = 5)
        {
            var tage = Days;
            if (tage is null || tage.Length == 0) { return null; }
            var j0 = Array.FindIndex(tage, t => string.CompareOrdinal(t ?? string.Empty, seit) >= 0);
            if (j0 < 0) { return null; }
            var neu = new Board
            {
                Category = Category, Track = Track, Klass = Klass,
                Rows = Rows, Valid = Valid, Invalid = Invalid, MaxRank = MaxRank,
                GroupCar = GroupCar, GroupSignature = GroupSignature, GroupCount = GroupCount,
                LapGroup = LapGroup, LapMs = LapMs, LapRank = LapRank,
                Days = tage[j0..],
            };
            if (j0 == 0) { return neu; }

            var r = tage.Length;
            var lday = LapDay;
            var gdays = GroupDayCount;
            var dstat = DayStats;
            if (lday is null || gdays is null || dstat is null
                || gdays.Length != GroupCar.Length * r || dstat.Length != 4 * r)
            {
                // Ohne die Angaben je Tag laesst sich nicht sagen, was neu ist.
                return null;
            }
            static int Zahl(int[] liste, int i, int vorgabe = 0) => i >= 0 && i < liste.Length ? liste[i] : vorgabe;
            int zeilen = 0, gueltig = 0, ungueltig = 0, tiefe = 0;
            for (var j = j0; j < r; j++)
            {
                zeilen += Zahl(dstat, 4 * j);
                gueltig += Zahl(dstat, 4 * j + 1);
                ungueltig += Zahl(dstat, 4 * j + 2);
                tiefe = Math.Max(tiefe, Zahl(dstat, 4 * j + 3));
            }
            if (zeilen <= 0) { return null; }
            neu.Rows = zeilen;
            neu.Valid = gueltig;
            neu.Invalid = ungueltig;
            neu.MaxRank = tiefe;

            var stelle = new Dictionary<int, int>();
            var gcar = new List<int>();
            var gsig = new List<int>();
            var gcount = new List<int>();
            for (var g = 0; g < GroupCar.Length; g++)
            {
                var n = 0;
                for (var j = j0; j < r; j++) { n += Zahl(gdays, g * r + j); }
                if (n <= 0) { continue; }
                stelle[g] = gcar.Count;
                gcar.Add(GroupCar[g]);
                gsig.Add(Zahl(GroupSignature, g));
                gcount.Add(n);
            }
            // Die Runden ab dem Stichtag aus beiden Listen, je Gruppe die schnellsten.
            var kandidaten = new Dictionary<int, List<(int Ms, int Rank)>>();
            void Sammeln(int[] grp, int[] ms, int[] rang, int[] tag)
            {
                for (var i = 0; i < grp.Length; i++)
                {
                    if (Zahl(tag, i, -1) < j0 || !stelle.TryGetValue(grp[i], out var g)) { continue; }
                    if (!kandidaten.TryGetValue(g, out var liste)) { kandidaten[g] = liste = new(); }
                    liste.Add((Zahl(ms, i), Zahl(rang, i)));
                }
            }
            Sammeln(LapGroup, LapMs, LapRank, lday);
            if (ExtraGroup is { } xg && ExtraMs is { } xm && ExtraRank is { } xr && ExtraDay is { } xd)
            {
                Sammeln(xg, xm, xr, xd);
            }
            var lgrp = new List<int>();
            var lms = new List<int>();
            var lrank = new List<int>();
            for (var g = 0; g < gcar.Count; g++)
            {
                if (!kandidaten.TryGetValue(g, out var liste)) { continue; }
                // OrderBy ist stabil (List.Sort nicht): bei gleicher Zeit die aus lgrp zuerst, wie in Python.
                foreach (var (ms, rang) in liste.OrderBy(e => e.Ms).Take(jeGruppe))
                {
                    lgrp.Add(g);
                    lms.Add(ms);
                    lrank.Add(rang);
                }
            }
            neu.GroupCar = gcar.ToArray();
            neu.GroupSignature = gsig.ToArray();
            neu.GroupCount = gcount.ToArray();
            neu.LapGroup = lgrp.ToArray();
            neu.LapMs = lms.ToArray();
            neu.LapRank = lrank.ToArray();
            return neu;
        }
    }
}
