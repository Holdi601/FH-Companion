using System.Text.Json;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Zeiten je Auto, die NICHT aus den gescannten Bestenlisten stammen: eingereichte Runden
/// (/api/lap/list) und die Horizon-Play-Zeiten aus Rennergebnissen (/api/race/hp).
/// </summary>
/// <remarks>
/// Seit 2026-10-03, auf Wunsch des Nutzers: "always fastest time per car wins" -- eine mit
/// der App gefahrene Runde, die schneller ist als die Bestenliste, schlaegt sie, auch in
/// der Autowahl der App. Die Seite macht das seit dem 2026-09-27 (applySubmitted); die
/// App sah nur den Datensatz. Angewandt NACH der Rivals-Auswahl (RivalsAdvisor.PickCars),
/// als Ueberschreibung mit Herkunft -- aus demselben Grund wie auf der Seite: die
/// Rivals-Auswahl greift absichtlich nicht die schnellste Runde, eine eingereichte
/// Bestzeit als Zeile darunter zu mischen hiesse, sie zu uebergehen.
///
/// Geholt beim Abgleich des Datensatzes, abgelegt neben ihm (submitted.json, hp.json),
/// gelesen beim Laden des Datensatzes. Ohne Server bleibt der letzte Stand.
/// </remarks>
internal sealed class Zusatzzeiten
{
    internal sealed record Zeit(int Ms, string Source);

    private readonly Dictionary<(string Track, string Klass), Dictionary<int, Zeit>> _je = new();

    public static string SubmittedPath => Path.Combine(AppInfo.DataFolder, "submitted.json");
    public static string HpPath => Path.Combine(AppInfo.DataFolder, "hp.json");

    private static readonly string[] SaubereModi = { "rivals", "horizon-play" };
    private static readonly string[] PiReihe = { "D", "C", "B", "A", "S1", "S2", "R" };

    public int Count => _je.Values.Sum(k => k.Count);

    private static string Falte(string? s) => string.Join(' ', (s ?? string.Empty).Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Die schnellste Fremdzeit je Auto auf diesem Brett -- car_id -> Zeit.</summary>
    public IReadOnlyDictionary<int, Zeit>? Fuer(string track, string klass) =>
        _je.TryGetValue((Falte(track), klass.ToUpperInvariant()), out var k) ? k : null;

    private void Merke(string? track, string? klass, int carId, int ms, string source)
    {
        if (string.IsNullOrWhiteSpace(track) || string.IsNullOrWhiteSpace(klass) || carId <= 0 || ms <= 0) { return; }
        var key = (Falte(track), klass.ToUpperInvariant());
        if (!_je.TryGetValue(key, out var k)) { _je[key] = k = new Dictionary<int, Zeit>(); }
        if (!k.TryGetValue(carId, out var da) || ms < da.Ms) { k[carId] = new Zeit(ms, source); }
    }

    /// <summary>Aus den abgelegten Dateien; fehlt eine, zaehlt die andere.</summary>
    public static Zusatzzeiten Laden(string? submitted = null, string? hp = null)
    {
        var z = new Zusatzzeiten();
        try { if (File.Exists(submitted ?? SubmittedPath)) { z.LiesEingereicht(File.ReadAllText(submitted ?? SubmittedPath)); } }
        catch (Exception) { }
        try { if (File.Exists(hp ?? HpPath)) { z.LiesHp(File.ReadAllText(hp ?? HpPath)); } }
        catch (Exception) { }
        return z;
    }

    internal void LiesEingereicht(string json)
    {
        using var dok = JsonDocument.Parse(json);
        if (!dok.RootElement.TryGetProperty("laps", out var laps) || laps.ValueKind != JsonValueKind.Array) { return; }
        foreach (var e in laps.EnumerateArray())
        {
            if (e.TryGetProperty("hidden", out var h) && h.ValueKind == JsonValueKind.True) { continue; }
            if (!e.TryGetProperty("lap", out var lap) || lap.ValueKind != JsonValueKind.Object) { continue; }
            var mode = lap.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            if (!SaubereModi.Contains(mode ?? string.Empty)) { continue; }
            var sek = lap.TryGetProperty("lapSeconds", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0;
            var klasseNr = lap.TryGetProperty("carClass", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : -1;
            var klasse = klasseNr >= 0 && klasseNr < PiReihe.Length ? PiReihe[klasseNr] : null;
            var auto = lap.TryGetProperty("carOrdinal", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetInt32() : 0;
            var track = lap.TryGetProperty("track", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            Merke(track, klasse, auto, (int)Math.Round(sek * 1000), "submitted");
        }
    }

    internal void LiesHp(string json)
    {
        using var dok = JsonDocument.Parse(json);
        if (!dok.RootElement.TryGetProperty("boards", out var boards) || boards.ValueKind != JsonValueKind.Array) { return; }
        foreach (var b in boards.EnumerateArray())
        {
            var track = b.TryGetProperty("track", out var t) ? t.GetString() : null;
            var klasse = b.TryGetProperty("class", out var k) ? k.GetString() : null;
            var auto = b.TryGetProperty("car", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
            var ms = b.TryGetProperty("ms", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() : 0;
            Merke(track, klasse, auto, ms, "horizon-play");
        }
    }

    /// <summary>Beide Listen vom Server holen und ablegen. Scheitert leise -- die alten Dateien bleiben.</summary>
    public static async Task HolenAsync(string? baseUrl, TimeSpan timeout, CancellationToken token = default)
    {
        baseUrl = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (baseUrl.Length == 0) { return; }
        using var client = ServerHttp.Client(timeout);
        foreach (var (pfad, ziel, wurzel) in new[] { ("/api/lap/list", SubmittedPath, "laps"), ("/api/race/hp", HpPath, "boards") })
        {
            try
            {
                var text = await client.GetStringAsync(baseUrl + pfad, token).ConfigureAwait(false);
                using (var probe = JsonDocument.Parse(text))
                {
                    if (!probe.RootElement.TryGetProperty(wurzel, out _)) { continue; }
                }
                Directory.CreateDirectory(Path.GetDirectoryName(ziel)!);
                await File.WriteAllTextAsync(ziel + ".part", text, token).ConfigureAwait(false);
                File.Move(ziel + ".part", ziel, overwrite: true);
            }
            catch (Exception)
            {
                // Ein Server ohne Einreichung antwortet 404: dann gibt es diese Zeiten eben nicht.
            }
        }
    }
}
