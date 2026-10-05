using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Which telemetry car ordinal is which car in the dataset, learned and remembered.
/// </summary>
/// <remarks>
/// Telemetry gives a car ORDINAL. The dataset knows cars by the leaderboard's own
/// <c>car_id</c>. There is good reason to think those are the same number -- the
/// codename catalogue is "a contiguous array indexed by carId"
/// (<c>scripts/dump_car_catalogue.py</c>), and an index into the game's car array is
/// exactly what an ordinal is -- but it is NOT proved: on the 46 ids where the
/// codename catalogue and the lap-time-joined names both exist they disagree 40
/// times, so one of those two mappings is wrong and it is not yet known which.
///
/// So the overlay treats a direct match as an assumption and labels it, while a
/// pair learned from the screen -- the ordinal on the wire while the car's name was
/// drawn -- counts as evidence and wins. Learned pairs are written to
/// <c>config/fh6_car_ordinals.json</c>, shared with the Python tooling.
/// </remarks>
internal sealed class OrdinalMap
{
    internal sealed class Entry
    {
        [JsonPropertyName("car_index")] public int CarIndex { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("pi")] public int? Pi { get; set; }
        [JsonPropertyName("learned")] public string? Learned { get; set; }
    }

    private sealed class Payload
    {
        [JsonPropertyName("source")] public string Source { get; set; } = string.Empty;
        [JsonPropertyName("by_ordinal")] public Dictionary<string, Entry> ByOrdinal { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string? _path;
    private readonly Dictionary<string, Entry> _byOrdinal;

    public OrdinalMap(string? path = null)
    {
        _path = path ?? FindPath();
        _byOrdinal = new Dictionary<string, Entry>(StringComparer.Ordinal);
        if (_path is not null && File.Exists(_path))
        {
            try
            {
                var payload = JsonSerializer.Deserialize<Payload>(
                    File.ReadAllText(_path), Options);
                if (payload?.ByOrdinal is not null)
                {
                    _byOrdinal = payload.ByOrdinal;
                }
            }
            catch (Exception)
            {
                // A map that will not parse is not worth failing the overlay for.
            }
        }
    }

    public int Count => _byOrdinal.Count;
    public int LearnedThisRun { get; private set; }

    private static string? FindPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "config", "fh6_car_ordinals.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            var configDir = Path.Combine(dir.FullName, "config");
            if (Directory.Exists(configDir))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        return null;
    }

    public Entry? Lookup(int? ordinal) =>
        ordinal is not null && _byOrdinal.TryGetValue(ordinal.Value.ToString(), out var entry)
            ? entry
            : null;

    /// <summary>Remember an ordinal seen while that car's name was on the screen.</summary>
    public bool Learn(int ordinal, int carIndex, string name, int? pi)
    {
        // NUR ECHTE NAMEN. "Car #3118" ist eine Kennung im Namensfeld, keine
        // Auskunft -- sie hier einzulagern hiesse, eine Nicht-Antwort als gelernte
        // Tatsache zu speichern, und ab dann sieht sie aus wie eine. Im Datensatz
        // stehen 14 solche Eintraege; die duerfen nicht weiterwandern.
        //
        // Die Zuordnung Ordinal -> Auto-Index BLEIBT trotzdem wertvoll, auch ohne
        // Namen. Darum wird sie mit leerem Namen abgelegt statt verworfen: die
        // Anzeige kann dann selbst entscheiden, ob sie eine namenlose Zeile zeigt.
        var sauber = RivalsAdvisor.IsRealCarName(name) ? name : string.Empty;
        var key = ordinal.ToString();
        if (_byOrdinal.TryGetValue(key, out var known) && known.CarIndex == carIndex)
        {
            return false;
        }
        _byOrdinal[key] = new Entry
        {
            CarIndex = carIndex,
            Name = sauber,
            Pi = pi,
            Learned = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        };
        LearnedThisRun++;
        Save();
        return true;
    }

    private void Save()
    {
        if (_path is null)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(new Payload
            {
                Source = "ordinal seen on the telemetry wire while the car name was "
                         + "on the screen; see haptics/.../Rivals/OrdinalMap.cs",
                ByOrdinal = _byOrdinal,
            }, Options));
        }
        catch (Exception)
        {
            // Losing a learned pair is not worth interrupting play for.
        }
    }
}
