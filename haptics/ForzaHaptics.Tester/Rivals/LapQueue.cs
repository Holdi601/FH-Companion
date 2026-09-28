using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Laps that beat the leaderboard but could not be sent yet -- kept until they can.
/// </summary>
/// <remarks>
/// ## Why (2026-09-27)
///
/// A lap that beat the leaderboard used to be lost for the site whenever it could
/// not be sent at that moment: submission switched off, offline, or the server
/// unreachable. Wanted: send it later -- once submission is on, once the
/// server answers again -- even if that is weeks or months away.
///
/// ## One file per route, class and car
///
/// The same key the ledger uses. Only the FASTEST waiting lap of a key is kept: the
/// server takes one lap per car and route anyway, and a slower one behind it would
/// only be refused. One file each so a crash while writing one lap never takes the
/// others with it; a lap with its telemetry is about 150 KB.
///
/// ## What this class does NOT decide
///
/// Whether a waiting lap is still worth sending. That is <see cref="LapAutoSubmit"/>,
/// against the leaderboard of the day it is finally sent -- see NachreichenAsync.
/// </remarks>
internal static class LapQueue
{
    internal sealed class Eintrag
    {
        [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;
        [JsonPropertyName("track")] public string? Track { get; set; }
        [JsonPropertyName("course")] public string Course { get; set; } = string.Empty;
        [JsonPropertyName("queued_at")] public DateTimeOffset QueuedAt { get; set; }
        [JsonPropertyName("reason")] public string? Reason { get; set; }
        [JsonPropertyName("attempts")] public int Attempts { get; set; }
        [JsonPropertyName("last_try")] public DateTimeOffset? LastTry { get; set; }
        [JsonPropertyName("last_result")] public string? LastResult { get; set; }
        [JsonPropertyName("lap")] public RecordedLap Lap { get; set; } = new();
    }

    public static string Folder => Path.Combine(LapSubmit.SubmitHome, "pending_laps");

    private static string RefusalsPath => Path.Combine(Folder, "refusals.json");

    private static string PfadFuer(string key) =>
        Path.Combine(Folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))
                                    .ToLowerInvariant()[..16] + ".json");

    /// <summary>Die volle Spur einer wartenden Runde, neben ihrer Datei.</summary>
    private static string SpurFuer(string key) =>
        Path.ChangeExtension(PfadFuer(key), null) + TelemetryTrack.Suffix;

    private static Eintrag? Lesen(string pfad)
    {
        try
        {
            var e = JsonSerializer.Deserialize<Eintrag>(File.ReadAllText(pfad));
            if (e is null || string.IsNullOrWhiteSpace(e.Key) || !(e.Lap.LapSeconds > 0)) { return null; }
            var spur = SpurFuer(e.Key);
            e.Lap.TelemetrieDatei = File.Exists(spur) ? spur : null;
            return e;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Keep a lap for later. Returns false when an equal or faster lap of the same
    /// route, class and car is already waiting -- the new one would add nothing.
    /// </summary>
    public static bool Vormerken(RecordedLap lap, string course, string? track, string key, string grund,
                                 byte[]? spur = null)
    {
        if (string.IsNullOrWhiteSpace(key)) { return false; }
        var pfad = PfadFuer(key);
        if (File.Exists(pfad) && Lesen(pfad) is { } alt && alt.Key == key
            && alt.Lap.LapSeconds <= lap.LapSeconds)
        {
            return false;
        }
        // Die volle Spur reist mit der Runde (seit 2026-09-28): daneben ablegen. Eine
        // alte Spur einer langsameren Runde desselben Schluessels muss dabei weg --
        // sonst ginge die neue Runde mit der Telemetrie der alten hinaus.
        var spurDatei = SpurFuer(key);
        try { if (File.Exists(spurDatei)) { File.Delete(spurDatei); } } catch (Exception) { }
        Directory.CreateDirectory(Folder);
        var gepackt = spur ?? LapSubmit.VolleSpur(lap);
        if (gepackt is not null) { File.WriteAllBytes(spurDatei, gepackt); }
        Sichern(new Eintrag
        {
            Key = key,
            Track = track,
            Course = course,
            QueuedAt = DateTimeOffset.Now,
            Reason = grund,
            Lap = lap,
        });
        return true;
    }

    /// <summary>Die Zeit der Runde, die fuer diesen Schluessel wartet -- oder null.</summary>
    public static float? WartendeSekunden(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) { return null; }
        var pfad = PfadFuer(key);
        return File.Exists(pfad) && Lesen(pfad) is { } e && e.Key == key ? e.Lap.LapSeconds : null;
    }

    public static void Sichern(Eintrag e)
    {
        Directory.CreateDirectory(Folder);
        var pfad = PfadFuer(e.Key);
        var tmp = pfad + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(e));
        File.Move(tmp, pfad, overwrite: true);
    }

    /// <summary>Every waiting lap, oldest first. A file that cannot be read is skipped.</summary>
    public static List<Eintrag> Alle()
    {
        var alle = new List<Eintrag>();
        try
        {
            if (!Directory.Exists(Folder)) { return alle; }
            foreach (var datei in Directory.EnumerateFiles(Folder, "*.json"))
            {
                if (Path.GetFileName(datei) == Path.GetFileName(RefusalsPath)) { continue; }
                if (Lesen(datei) is { } e) { alle.Add(e); }
            }
        }
        catch (Exception) { }
        return alle.OrderBy(e => e.QueuedAt).ToList();
    }

    public static int Anzahl()
    {
        try
        {
            return Directory.Exists(Folder)
                ? Directory.EnumerateFiles(Folder, "*.json")
                           .Count(d => Path.GetFileName(d) != Path.GetFileName(RefusalsPath))
                : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    public static void Entfernen(string key)
    {
        try { File.Delete(PfadFuer(key)); } catch (Exception) { }
        try { File.Delete(SpurFuer(key)); } catch (Exception) { }
    }

    /// <summary>Throw every waiting lap away -- the button in the Rivals tab.</summary>
    public static int AllesVergessen()
    {
        var weg = 0;
        foreach (var e in Alle())
        {
            Entfernen(e.Key);
            weg++;
        }
        return weg;
    }

    // DIE BREMSE GEGEN STRAFPUNKTE. Der Server zaehlt eine abgelehnte Runde als
    // Strafpunkt, wenn sie ungueltig oder deutlich langsamer ist, und sperrt nach
    // fuenf in 24 Stunden. Eine lange Warteschlange, die stuendlich nachgereicht
    // wird, koennte das in wenigen Stunden schaffen, wenn ihr Bild der Bestenliste
    // hinter dem des Servers liegt. Darum: nach ZWEI Ablehnungen binnen 24 Stunden
    // ruht das Nachreichen, bis die aeltere aus dem Fenster faellt.

    public static readonly TimeSpan RefusalWindow = TimeSpan.FromHours(24);
    public const int RefusalLimit = 2;

    private static List<DateTimeOffset> Ablehnungen()
    {
        try
        {
            if (File.Exists(RefusalsPath))
            {
                return JsonSerializer.Deserialize<List<DateTimeOffset>>(File.ReadAllText(RefusalsPath))
                       ?? new List<DateTimeOffset>();
            }
        }
        catch (Exception) { }
        return new List<DateTimeOffset>();
    }

    public static void AblehnungMerken(DateTimeOffset wann)
    {
        try
        {
            var liste = Ablehnungen().Where(t => wann - t < RefusalWindow).ToList();
            liste.Add(wann);
            Directory.CreateDirectory(Folder);
            File.WriteAllText(RefusalsPath, JsonSerializer.Serialize(liste));
        }
        catch (Exception) { }
    }

    /// <summary>Resting after refusals? Then until when.</summary>
    public static DateTimeOffset? RuhtBis(DateTimeOffset jetzt)
    {
        var frisch = Ablehnungen().Where(t => jetzt - t < RefusalWindow).OrderBy(t => t).ToList();
        return frisch.Count >= RefusalLimit ? frisch[frisch.Count - RefusalLimit] + RefusalWindow : null;
    }
}
