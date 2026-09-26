using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Gets the newest dataset from the server when it can, and falls back to what is on
/// disk when it cannot.
/// </summary>
/// <remarks>
/// The app used to read <c>data/analytics/laps.json</c> once, at startup, from the
/// repository beside it. That works on the machine that does the scanning and nowhere
/// else, and even there it goes stale within the hour: a sweep writes a new dataset
/// every five boards.
///
/// So there are four sources, tried in this order:
///
/// <list type="number">
///   <item>the server (<c>/api/summary</c>, then <c>/api/dataset</c> if it is newer)</item>
///   <item>the local cache from the last successful fetch</item>
///   <item>the dataset that shipped inside the download, beside the executable</item>
///   <item>the repository file, if this happens to be the scanning machine</item>
/// </list>
///
/// The shipped copy is what makes a packaged build useful before it has ever reached
/// the network. Without it the first start shows an empty panel, and the only cure is
/// a server that may well be off -- because the machine that serves it is the machine
/// someone is playing on.
///
/// The summary is fetched first on purpose. It is a few hundred bytes against seven
/// megabytes, and it carries a <c>version</c> -- the hash of the dataset itself, not a
/// timestamp, so a rebuild that changed nothing does not provoke a download.
///
/// **Every failure here is normal.** No network, a laptop on a train, the server off
/// because the scanning machine is being used for gaming: none of that is exceptional
/// and none of it may cost more than a short wait. Hence the tight timeouts, and hence
/// a result that always names its source rather than pretending the numbers are fresh.
/// </remarks>
internal static class DatasetSync
{
    /// <summary>Where a fetched dataset is kept, per user.</summary>
    /// <remarks>
    /// Not beside the executable: a published build can sit in Program Files, where a
    /// normal user cannot write, and the failure would only show up on someone else's
    /// machine.
    /// </remarks>
    public static string CacheDirectory => AppInfo.DataFolder;

    public static string CachePath => Path.Combine(CacheDirectory, "laps.json");
    public static string CacheMetaPath => Path.Combine(CacheDirectory, "laps.meta.json");

    /// <summary>The dataset that came inside the download, at or above the binary.</summary>
    /// <remarks>
    /// Deliberately the very same <c>data/analytics/laps.json</c> layout the
    /// repository uses, and found the same way -- by walking up from the binary --
    /// so <see cref="RivalsDataset.FindDefaultPath"/> finds it too. One file in one
    /// place beats a second location that can quietly disagree with the first, and
    /// walking up is what lets the package keep its two hundred runtime files in
    /// <c>app\</c> while the data a human might want to look at stays at the top.
    ///
    /// What separates "shipped" from "the scanning machine's repository" is the meta
    /// file beside it: the packager writes one, the site build does not. So the meta
    /// is the marker, and a dataset without one is reported as a plain local file.
    ///
    /// Read-only as far as this app is concerned. A published build may sit somewhere
    /// a normal user cannot write, so downloads still go to
    /// <see cref="CacheDirectory"/> -- the shipped copy is a floor, never a target.
    /// </remarks>
    public static string? BundledMetaPath => FindUpwards(
        Path.Combine("data", "analytics", "laps.meta.json"));

    public static string? BundledPath
    {
        get
        {
            var meta = BundledMetaPath;
            if (meta is null) return null;
            var beside = Path.Combine(Path.GetDirectoryName(meta)!, "laps.json");
            return File.Exists(beside) ? beside : null;
        }
    }

    private static string? FindUpwards(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    public enum Origin { Server, Cache, Bundled, Repository, None }

    public sealed record Result(
        Origin Source, string? Path, string? Version, string? BuiltAt,
        int Boards, string Detail);

    internal sealed class Summary
    {
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("built_at")] public string? BuiltAt { get; set; }
        [JsonPropertyName("boards")] public int Boards { get; set; }
        [JsonPropertyName("bytes")] public long Bytes { get; set; }
        [JsonPropertyName("contributors")] public List<string> Contributors { get; set; } = new();
    }

    internal sealed class CacheMeta
    {
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("built_at")] public string? BuiltAt { get; set; }
        [JsonPropertyName("boards")] public int Boards { get; set; }
        [JsonPropertyName("fetched_at")] public string? FetchedAt { get; set; }
        [JsonPropertyName("source")] public string? Source { get; set; }
    }

    public static CacheMeta? ReadCacheMeta() => ReadMeta(CacheMetaPath, CachePath);

    /// <summary>What the shipped copy says it is, if the package described it.</summary>
    public static CacheMeta? ReadBundledMeta()
    {
        var meta = BundledMetaPath;
        var dataset = BundledPath;
        return meta is null || dataset is null ? null : ReadMeta(meta, dataset);
    }

    private static CacheMeta? ReadMeta(string metaPath, string datasetPath)
    {
        try
        {
            if (!File.Exists(metaPath) || !File.Exists(datasetPath)) return null;
            return JsonSerializer.Deserialize<CacheMeta>(File.ReadAllText(metaPath));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The best dataset already on this disk, chosen without touching the network.
    /// </summary>
    /// <remarks>
    /// The order used to live in two places -- here and in the tab's constructor --
    /// and only one of them knew about the shipped copy. One function now decides it,
    /// and both the synchronous first paint and the fallbacks of
    /// <see cref="SyncAsync"/> ask that function.
    ///
    /// A download does NOT automatically beat what shipped. Unzipping a newer package
    /// beside a months-old cache is exactly the case where "fetched later" is the
    /// wrong test, so the two are compared by when their data was BUILT.
    /// </remarks>
    public static Result LocalBest()
    {
        var cache = ReadCacheMeta();
        var bundled = ReadBundledMeta();

        if (cache is not null && bundled is not null)
        {
            if (Newer(bundled.BuiltAt, cache.BuiltAt)) cache = null;
            else bundled = null;
        }

        if (cache is not null)
        {
            return new Result(Origin.Cache, CachePath, cache.Version, cache.BuiltAt,
                cache.Boards, $"copy fetched {Ago(cache.FetchedAt)}");
        }
        if (bundled is not null)
        {
            return new Result(Origin.Bundled, BundledPath, bundled.Version,
                bundled.BuiltAt, bundled.Boards, "shipped with this download");
        }

        var repository = RivalsDataset.FindDefaultPath();
        if (repository is not null)
        {
            return new Result(Origin.Repository, repository, null,
                File.GetLastWriteTime(repository).ToString("yyyy-MM-dd HH:mm"), 0,
                "the file beside this app");
        }
        return new Result(Origin.None, null, null, null, 0, "no dataset on this machine");
    }

    private static bool Newer(string? candidate, string? incumbent)
        => DateTimeOffset.TryParse(candidate, out var a)
           && DateTimeOffset.TryParse(incumbent, out var b)
           && a > b;

    /// <summary>
    /// Ask the server, download if it has something newer, and return what to load.
    /// Never throws: an unreachable server is the normal case, not an error.
    /// </summary>
    public static async Task<Result> SyncAsync(
        string? baseUrl, TimeSpan probeTimeout, TimeSpan downloadTimeout,
        IProgress<string>? progress = null, CancellationToken token = default)
    {
        var local = LocalBest();
        baseUrl = (baseUrl ?? string.Empty).Trim().TrimEnd('/');

        if (baseUrl.Length > 0)
        {
            try
            {
                progress?.Report($"asking {baseUrl} ...");
                using var client = ServerHttp.Client(probeTimeout);
                var summaryJson = await client.GetStringAsync(
                    $"{baseUrl}/api/summary", token).ConfigureAwait(false);
                var summary = JsonSerializer.Deserialize<Summary>(summaryJson);

                if (summary?.Version is null)
                {
                    progress?.Report("the server answered, but without a version");
                }
                else if (local.Version == summary.Version && local.Path is not null)
                {
                    // Nothing to do, and saying so matters: "already current" and
                    // "could not reach the server" both leave the same file in place,
                    // and only one of them means the numbers are fresh.
                    return local with
                    {
                        Version = summary.Version,
                        BuiltAt = summary.BuiltAt,
                        Boards = summary.Boards,
                        Detail = "already the newest -- nothing to download",
                    };
                }
                else
                {
                    progress?.Report(
                        $"downloading {summary.Bytes / 1_000_000.0:0.0} MB ...");
                    using var big = ServerHttp.Client(downloadTimeout);
                    var bytes = await big.GetByteArrayAsync(
                        $"{baseUrl}/api/dataset", token).ConfigureAwait(false);

                    // Parse BEFORE replacing the cache. A truncated download that
                    // overwrote a working cache would take the fallback down with it,
                    // and the next start would have nothing at all.
                    using (var probe = new MemoryStream(bytes))
                    {
                        var parsed = JsonSerializer.Deserialize<RivalsDataset>(probe,
                            new JsonSerializerOptions
                            {
                                NumberHandling = JsonNumberHandling.AllowReadingFromString,
                            });
                        if (parsed is null || parsed.Boards.Count == 0)
                            throw new InvalidDataException("the download holds no boards");
                    }

                    Directory.CreateDirectory(CacheDirectory);
                    var temporary = CachePath + ".part";
                    await File.WriteAllBytesAsync(temporary, bytes, token)
                        .ConfigureAwait(false);
                    File.Move(temporary, CachePath, overwrite: true);
                    File.WriteAllText(CacheMetaPath, JsonSerializer.Serialize(
                        new CacheMeta
                        {
                            Version = summary.Version,
                            BuiltAt = summary.BuiltAt,
                            Boards = summary.Boards,
                            FetchedAt = DateTimeOffset.Now.ToString("O"),
                            Source = baseUrl,
                        }, new JsonSerializerOptions { WriteIndented = true }));

                    return new Result(Origin.Server, CachePath, summary.Version,
                        summary.BuiltAt, summary.Boards,
                        $"downloaded from {baseUrl}");
                }
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"[dataset] {exception.Message}");
                progress?.Report($"server not reachable ({Short(exception)})");
            }
        }

        // Whatever is on this disk, named for what it is. The wording has to survive
        // being read by someone who never had a server: "server not reachable" in
        // front of a perfectly good shipped dataset reads as a failure when it is the
        // designed case.
        if (local.Path is not null)
        {
            return local with
            {
                Detail = baseUrl.Length == 0
                    ? local.Detail
                    : $"server not reachable -- using the {local.Detail}",
            };
        }

        return new Result(Origin.None, null, null, null, 0,
            baseUrl.Length == 0
                ? "no server configured and no dataset on this machine"
                : "no server, no cache, nothing shipped");
    }

    private static string Short(Exception exception) => exception switch
    {
        TaskCanceledException => "timed out",
        HttpRequestException http => http.Message.Split('(')[0].Trim(),
        _ => exception.GetType().Name,
    };

    private static string Ago(string? iso)
    {
        if (!DateTimeOffset.TryParse(iso, out var when)) return "at some point";
        var span = DateTimeOffset.Now - when;
        if (span < TimeSpan.FromMinutes(2)) return "just now";
        if (span < TimeSpan.FromHours(1)) return $"{span.TotalMinutes:0} min ago";
        if (span < TimeSpan.FromDays(1)) return $"{span.TotalHours:0} h ago";
        return $"{span.TotalDays:0} days ago";
    }
}
