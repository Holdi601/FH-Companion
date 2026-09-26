using System.Text.Json;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Sends a finished lap to the site -- but ONLY when it beats the leaderboard.
/// </summary>
/// <remarks>
/// ## When a lap is sent
///
/// All of these, checked here before anything leaves the computer:
///
///   * the switch is on (<c>submit_laps</c>, on by default -- the user asked for
///     opt-out, and the first-start notice says so), the app is not offline, and a
///     gamertag is set;
///   * the lap has a time, telemetry and a route name the leaderboard knows;
///   * it is FASTER than the fastest VALID leaderboard time of that same car on that
///     route and class -- the same comparison the server repeats (server/
///     leaderboard_check.py) -- or the car is not on that board at all;
///   * it is faster than anything this installation already sent for it.
///
/// Anything else is kept on disk and never sent. That keeps traffic to a trickle
/// (a handful of laps a week for an active player) instead of every lap driven.
///
/// ## Why the server checks again
///
/// A modified app could skip every rule here. The server does not take the app's
/// word -- it compares against its own copy of the leaderboard, refuses what is not
/// faster, and bans installations that keep sending rubbish. This class exists so
/// an honest app never gets near those limits.
///
/// ## The ledger
///
/// <see cref="LedgerPath"/> remembers, per route/class/car, the fastest time already
/// sent -- or refused by the server as not faster. The next lap has to beat that, so
/// a refused lap is never sent twice.
/// </remarks>
internal sealed class LapAutoSubmit
{
    /// <summary>The decision, with the reason in words.</summary>
    internal sealed record Befund(bool Senden, string Grund, int? BestenlisteMs = null,
                                  int? FrueherMs = null, string Schluessel = "");

    // Telemetry carClass is in PI order (0..6 = D C B A S1 S2 R); the dataset's
    // class list is alphabetical. Map by NAME -- see submitted_laps.js.
    private static readonly string[] PiOrder = { "D", "C", "B", "A", "S1", "S2", "R" };

    private readonly Func<RivalsAdvisor?> _advisor;
    private readonly OverlaySettings _settings;
    private readonly Action<string> _log;
    private static readonly SemaphoreSlim EinerZurZeit = new(1, 1);

    public LapAutoSubmit(Func<RivalsAdvisor?> advisor, OverlaySettings settings, Action<string> log)
    {
        _advisor = advisor;
        _settings = settings;
        _log = text =>
        {
            Last = text;
            LastAt = DateTime.Now;
            log(text);
        };
    }

    /// <summary>The last decision, for the status line in the Rivals tab.</summary>
    public static string? Last { get; private set; }
    public static DateTime LastAt { get; private set; }

    public static string LedgerPath => Path.Combine(LapSubmit.SubmitHome, "submitted_laps.json");

    private static string Falte(string? s) =>
        string.Join(" ", (s ?? string.Empty).Trim().ToLowerInvariant()
                                            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The fastest VALID time of a car on a board, or null if the car is not there.</summary>
    internal static int? BestValidMs(RivalsDataset data, RivalsDataset.Board board, int ordinal)
    {
        var sauber = data.Flags.IndexOf("clean");
        var bit = sauber >= 0 ? 1 << sauber : 0;
        int? beste = null;
        for (var i = 0; i < board.LapGroup.Length && i < board.LapMs.Length; i++)
        {
            var g = board.LapGroup[i];
            if (g < 0 || g >= board.GroupCar.Length) { continue; }
            if (g < board.GroupSignature.Length && (board.GroupSignature[g] & bit) != bit) { continue; }
            var car = board.GroupCar[g];
            if (car < 0 || car >= data.CarIds.Count || data.CarIds[car] != ordinal) { continue; }
            var ms = board.LapMs[i];
            if (beste is null || ms < beste) { beste = ms; }
        }
        return beste;
    }

    /// <summary>
    /// The route name for a lap: what the lap carries, else its course's harvested
    /// name -- but never a folder key ("course_x_z_to_x_z"), which is no name at all.
    /// </summary>
    internal static string? RouteName(RecordedLap lap, string root, params string[] kurse)
    {
        if (!string.IsNullOrWhiteSpace(lap.Track) && !OwnTimes.IsFolderKey(lap.Track)) { return lap.Track; }
        foreach (var kurs in kurse)
        {
            if (string.IsNullOrWhiteSpace(kurs)) { continue; }
            var name = CourseShape.KursName(root, kurs);
            if (!string.IsNullOrWhiteSpace(name) && !OwnTimes.IsFolderKey(name)) { return name; }
        }
        return null;
    }

    /// <summary>Pure decision -- no network, no disk. Also used by the tests.</summary>
    internal static Befund Pruefen(RecordedLap lap, string? track, RivalsDataset? data,
                                   IReadOnlyDictionary<string, int> ledger)
    {
        if (!(lap.LapSeconds > 0) || float.IsNaN(lap.LapSeconds) || float.IsInfinity(lap.LapSeconds))
        {
            return new Befund(false, "no lap time");
        }
        if (lap.Samples is null || lap.Samples.Count < 10)
        {
            return new Befund(false, "no telemetry for the lap");
        }
        if (string.IsNullOrWhiteSpace(track))
        {
            return new Befund(false, "route unknown -- nothing to compare with");
        }
        if (data is null)
        {
            return new Befund(false, "no leaderboard data loaded");
        }
        if (lap.CarClass < 0 || lap.CarClass >= PiOrder.Length)
        {
            return new Befund(false, $"unknown class {lap.CarClass}");
        }
        var klasse = PiOrder[lap.CarClass];
        var t = data.Tracks.FindIndex(x => Falte(x) == Falte(track));
        var k = data.Classes.IndexOf(klasse);
        var board = t < 0 || k < 0 ? null : data.Boards.FirstOrDefault(b => b.Track == t && b.Klass == k);
        if (board is null)
        {
            return new Befund(false, $"no leaderboard for {track} in class {klasse}");
        }
        var schluessel = $"{Falte(track)}|{klasse}|{lap.CarOrdinal}";
        var ms = (int)Math.Round(lap.LapSeconds * 1000.0);
        var bestenliste = BestValidMs(data, board, lap.CarOrdinal);
        ledger.TryGetValue(schluessel, out var frueher);
        if (bestenliste is int b && ms >= b)
        {
            return new Befund(false, $"not faster than the leaderboard ({ms / 1000.0:0.000} s vs {b / 1000.0:0.000} s)",
                              b, frueher > 0 ? frueher : null, schluessel);
        }
        if (frueher > 0 && ms >= frueher)
        {
            return new Befund(false, $"not faster than the time already sent ({frueher / 1000.0:0.000} s)",
                              bestenliste, frueher, schluessel);
        }
        return new Befund(true, bestenliste is null
                                    ? "car not on this leaderboard yet"
                                    : $"faster than the leaderboard ({ms / 1000.0:0.000} s vs {bestenliste / 1000.0:0.000} s)",
                          bestenliste, frueher > 0 ? frueher : null, schluessel);
    }

    internal static Dictionary<string, int> LedgerLaden()
    {
        try
        {
            if (File.Exists(LedgerPath))
            {
                return JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(LedgerPath))
                       ?? new Dictionary<string, int>();
            }
        }
        catch (Exception)
        {
            // Ein kaputtes Buch heisst hoechstens: eine Runde wird einmal zu viel
            // gefragt. Der Server weist sie dann ab -- ohne Strafpunkt, weil sie
            // nur knapp oder gar nicht langsamer ist.
        }
        return new Dictionary<string, int>();
    }

    private static void LedgerSichern(Dictionary<string, int> buch)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LedgerPath)!);
        var tmp = LedgerPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(buch));
        File.Move(tmp, LedgerPath, overwrite: true);
    }

    /// <summary>Consider a finished lap; sends it if (and only if) it beats the leaderboard.</summary>
    /// <returns>What happened, in one line -- also written to the lap log.</returns>
    public async Task<string> ConsiderAsync(RecordedLap lap, string course, string? track,
                                            bool dryRun = false)
    {
        if (!_settings.SubmitLaps) { return "lap submission is switched off"; }
        if (_settings.Offline) { return "offline -- nothing is sent"; }
        var gamertag = (_settings.Gamertag ?? string.Empty).Trim();
        if (gamertag.Length == 0) { return "no gamertag set -- laps are not submitted"; }
        if (string.IsNullOrWhiteSpace(_settings.DatasetUrl)) { return "no server set"; }

        await EinerZurZeit.WaitAsync().ConfigureAwait(false);
        try
        {
            var buch = LedgerLaden();
            var befund = Pruefen(lap, track, _advisor()?.Data, buch);
            if (!befund.Senden || dryRun)
            {
                var text = (dryRun && befund.Senden ? "would submit: " : "not submitted: ") + befund.Grund;
                _log(text);
                return text;
            }

            var timeout = TimeSpan.FromSeconds(Math.Max(20, _settings.DatasetDownloadSeconds / 4));
            var wer = LapSubmit.Load();
            // DERSELBE SERVER, NUR JETZT UEBER HTTPS: die Anmeldung behalten und nur
            // die Adresse nachziehen. Ein blosser Textvergleich hielte http:// und
            // https:// fuer zwei Server und meldete jede Installation neu an -- mit
            // neuer Kennung, ohne ihre bisherigen Runden.
            if (wer is not null && wer.Server != _settings.DatasetUrl
                && ServerHttp.SameServer(wer.Server, _settings.DatasetUrl))
            {
                wer.Server = _settings.DatasetUrl;
                LapSubmit.Save(wer);
            }
            if (wer is null || wer.Server != _settings.DatasetUrl || wer.Gamertag != gamertag)
            {
                wer = await LapSubmit.RegisterAsync(_settings.DatasetUrl!, gamertag, timeout)
                                     .ConfigureAwait(false);
            }
            lap.Track = track;
            var ms = (int)Math.Round(lap.LapSeconds * 1000.0);
            try
            {
                await LapSubmit.SubmitAsync(wer, lap, course, timeout).ConfigureAwait(false);
                buch[befund.Schluessel] = ms;
                LedgerSichern(buch);
                var ok = $"submitted: {lap.LapSeconds:0.000} s on {track} -- {befund.Grund}";
                _log(ok);
                return ok;
            }
            catch (LapSubmit.Rejected r) when (r.Status == 409)
            {
                // Der Server kennt eine schnellere Zeit (sein Datensatz ist neuer).
                // Merken, damit genau diese Runde nie wieder gefragt wird.
                buch[befund.Schluessel] = ms;
                LedgerSichern(buch);
                var nein = "server says not faster: " + r.Message;
                _log(nein);
                return nein;
            }
        }
        catch (Exception e)
        {
            var fehler = "submission failed: " + e.Message;
            _log(fehler);
            return fehler;
        }
        finally
        {
            EinerZurZeit.Release();
        }
    }
}
