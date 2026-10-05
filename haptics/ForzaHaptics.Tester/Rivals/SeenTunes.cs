using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Das aufgespielte Tune je Auto, wie es im Spiel auf der Tunes-Liste zu sehen war.
/// </summary>
/// <remarks>
/// Seit 2026-09-29, fuer die Xbox und fuer abgeschalteten Speicherzugriff: am PC
/// liest die App das Tune aus dem Spielstand und die Belegung aus dem Speicher. Ohne
/// beides bleibt der Schirm: in der Tunes-Liste traegt das aufgespielte Tune das graue
/// Zeichen "unveraendert gegenueber dem Auto". Steht die Auswahl darauf, stehen Name,
/// Tuner und Datum links -- das merkt sich die App fuer die Autonotiz.
///
/// Liegt im Datenordner, nicht in config/: es ist Gelerntes, keine Einstellung.
/// </remarks>
internal static class SeenTunes
{
    internal sealed class Eintrag
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("creator")] public string Creator { get; set; } = string.Empty;
        [JsonPropertyName("date")] public string Datum { get; set; } = string.Empty;
        [JsonPropertyName("car")] public string Auto { get; set; } = string.Empty;
        [JsonPropertyName("seen")] public DateTimeOffset Gesehen { get; set; }
    }

    private static readonly object Schloss = new();
    private static Dictionary<string, Eintrag>? _alle;

    internal static string Pfad { get; set; } = Path.Combine(AppInfo.DataFolder, "seen_tunes.json");

    private static Dictionary<string, Eintrag> Alle()
    {
        if (_alle is not null) { return _alle; }
        try
        {
            _alle = File.Exists(Pfad)
                ? JsonSerializer.Deserialize<Dictionary<string, Eintrag>>(File.ReadAllText(Pfad)) ?? new()
                : new();
        }
        catch (Exception)
        {
            _alle = new();
        }
        return _alle;
    }

    /// <summary>Das gemerkte Tune fuer diese car_id, oder null.</summary>
    public static Eintrag? Fuer(int carId)
    {
        lock (Schloss) { return Alle().TryGetValue(carId.ToString(), out var e) ? e : null; }
    }

    /// <summary>Merken. Gibt zurueck, ob sich etwas geaendert hat.</summary>
    public static bool Merken(int carId, string auto, string name, string creator, string datum)
    {
        name = name.Trim();
        if (carId <= 0 || name.Length == 0) { return false; }
        lock (Schloss)
        {
            var alle = Alle();
            if (alle.TryGetValue(carId.ToString(), out var alt) && alt.Name == name && alt.Creator == creator.Trim())
            {
                return false;
            }
            alle[carId.ToString()] = new Eintrag
            {
                Name = name, Creator = creator.Trim(), Datum = datum.Trim(), Auto = auto, Gesehen = DateTimeOffset.Now,
            };
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Pfad)!);
                var zwischen = Pfad + ".tmp";
                File.WriteAllText(zwischen, JsonSerializer.Serialize(alle, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(zwischen, Pfad, overwrite: true);
            }
            catch (Exception)
            {
            }
            return true;
        }
    }

    /// <summary>Nur fuer Tests: den Zwischenspeicher vergessen.</summary>
    internal static void Vergessen() { lock (Schloss) { _alle = null; } }
}
