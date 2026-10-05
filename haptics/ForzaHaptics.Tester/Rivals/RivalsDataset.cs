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

    public static RivalsDataset Load(string path)
    {
        using var stream = File.OpenRead(path);
        var loaded = JsonSerializer.Deserialize<RivalsDataset>(stream, Options)
                     ?? throw new InvalidDataException($"{path} did not parse as a dataset");
        if (loaded.Boards.Count == 0)
        {
            throw new InvalidDataException($"{path} holds no boards");
        }
        return loaded;
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
    }
}
