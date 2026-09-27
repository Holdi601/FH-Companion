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
///     opt-out, and the first-start notice says so) and the app is not offline. A
///     gamertag is NOT needed (since 2026-09-27): a ban hangs on the installation
///     and the peppered hardware hash, not on a name. Without one the lap shows on
///     the site as "a player";
///   * the lap has a time, telemetry and a route name the leaderboard knows;
///   * it is FASTER than the fastest VALID leaderboard time of that same car on that
///     route and class -- the same comparison the server repeats (server/
///     leaderboard_check.py) -- or the car is not on that board at all;
///   * it is faster than anything this installation already sent for it.
///
/// Anything else is kept on disk and never sent. That keeps traffic to a trickle
/// (a handful of laps a week for an active player) instead of every lap driven.
///
/// A lap that qualifies but cannot go out now -- the first condition fails, or the
/// server does not answer -- waits in <see cref="LapQueue"/> and is sent later, after
/// being checked once more against the leaderboard of that day.
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
        var schluessel = Schluessel(lap, track)!;
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

    /// <summary>The key of a lap in ledger and queue -- also without a leaderboard to check.</summary>
    internal static string? Schluessel(RecordedLap lap, string? track) =>
        string.IsNullOrWhiteSpace(track) || lap.CarClass < 0 || lap.CarClass >= PiOrder.Length
            ? null
            : $"{Falte(track)}|{PiOrder[lap.CarClass]}|{lap.CarOrdinal}";

    /// <summary>
    /// Why nothing can be sent right now -- or null if it can. Nothing here is a
    /// reason to forget a lap, only to keep it until it goes away.
    /// </summary>
    internal string? Hindernis()
    {
        if (!_settings.SubmitLaps) { return "lap submission is switched off"; }
        if (_settings.Offline) { return "offline"; }
        if (string.IsNullOrWhiteSpace(_settings.DatasetUrl)) { return "no server set"; }
        return null;
    }

    /// <summary>How a send ended -- and with it, what becomes of a waiting lap.</summary>
    internal enum Ausgang
    {
        /// <summary>Accepted. Into the ledger, out of the queue.</summary>
        Gesendet,
        /// <summary>409: the server knows a faster time. Into the ledger, out of the queue.</summary>
        NichtSchneller,
        /// <summary>400/413/422: the lap itself is refused. It never will pass; out of the queue.</summary>
        Ungueltig,
        /// <summary>Anything else -- no connection, 5xx, 429, a failed sign-up. Keep it.</summary>
        SpaeterNochmal,
    }

    /// <summary>Which outcome a server status is. Public for the tests.</summary>
    internal static Ausgang AusgangFuer(int status) => status switch
    {
        >= 200 and < 300 => Ausgang.Gesendet,
        409 => Ausgang.NichtSchneller,
        400 or 413 or 422 => Ausgang.Ungueltig,
        _ => Ausgang.SpaeterNochmal,
    };

    /// <summary>Consider a finished lap; sends it if (and only if) it beats the leaderboard.</summary>
    /// <returns>What happened, in one line -- also written to the lap log.</returns>
    /// <remarks>
    /// A lap that beats the leaderboard but cannot go out now -- switched off,
    /// offline, no server, or the send fails -- is kept in
    /// <see cref="LapQueue"/> and sent by <see cref="NachreichenAsync"/> later.
    /// So is a lap driven before any leaderboard was loaded: that it does not beat
    /// one is not known yet.
    /// </remarks>
    public async Task<string> ConsiderAsync(RecordedLap lap, string course, string? track,
                                            bool dryRun = false)
    {
        await EinerZurZeit.WaitAsync().ConfigureAwait(false);
        try
        {
            var buch = LedgerLaden();
            var data = _advisor()?.Data;
            var befund = Pruefen(lap, track, data, buch);
            var unentschieden = data is null && !befund.Senden && Schluessel(lap, track) is not null
                                && lap.Samples is { Count: >= 10 } && lap.LapSeconds > 0;
            if (dryRun || (!befund.Senden && !unentschieden))
            {
                var text = (dryRun && befund.Senden ? "would submit: " : "not submitted: ") + befund.Grund;
                _log(text);
                return text;
            }

            var hindernis = Hindernis() ?? (unentschieden ? befund.Grund : null);
            if (hindernis is not null)
            {
                return Behalten(lap, course, track, hindernis);
            }

            var (ausgang, meldung) = await SendenAsync(lap, course, track!, befund, buch).ConfigureAwait(false);
            if (ausgang == Ausgang.SpaeterNochmal)
            {
                return Behalten(lap, course, track, meldung);
            }
            if (ausgang != Ausgang.Gesendet) { LapQueue.AblehnungMerken(DateTimeOffset.Now); }
            _log(meldung);
            return meldung;
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

    private string Behalten(RecordedLap lap, string course, string? track, string grund)
    {
        lap.Track ??= track;
        var neu = LapQueue.Vormerken(lap, course, track, Schluessel(lap, track)!, grund);
        var text = neu
            ? $"kept to submit later ({grund}): {lap.LapSeconds:0.000} s on {track}"
            : $"not kept ({grund}): a faster lap of this car on {track} is already waiting";
        _log(text);
        return text;
    }

    /// <summary>Sign up if needed, send one lap, and say how it ended. Never throws.</summary>
    private async Task<(Ausgang, string)> SendenAsync(RecordedLap lap, string course, string track,
                                                      Befund befund, Dictionary<string, int> buch)
    {
        var gamertag = (_settings.Gamertag ?? string.Empty).Trim();
        var timeout = TimeSpan.FromSeconds(Math.Max(20, _settings.DatasetDownloadSeconds / 4));
        LapSubmit.Identity wer;
        try
        {
            var gemerkt = LapSubmit.Load();
            // DERSELBE SERVER, NUR JETZT UEBER HTTPS: die Anmeldung behalten und nur
            // die Adresse nachziehen. Ein blosser Textvergleich hielte http:// und
            // https:// fuer zwei Server und meldete jede Installation neu an -- mit
            // neuer Kennung, ohne ihre bisherigen Runden.
            if (gemerkt is not null && gemerkt.Server != _settings.DatasetUrl
                && ServerHttp.SameServer(gemerkt.Server, _settings.DatasetUrl))
            {
                gemerkt.Server = _settings.DatasetUrl;
                LapSubmit.Save(gemerkt);
            }
            // EIN GEAENDERTER GAMERTAG IST KEIN GRUND FUER EINE NEUE ANMELDUNG
            // (seit 2026-09-27): er reist mit jeder Einreichung mit. Frueher meldete
            // jeder neue Name die Installation neu an -- neue Kennung, und nach fuenf
            // davon nahm der Server diese Maschine gar nicht mehr an.
            wer = gemerkt is null || gemerkt.Server != _settings.DatasetUrl
                ? await Anmelden(gamertag, timeout).ConfigureAwait(false)
                : gemerkt;
        }
        catch (Exception e)
        {
            // Auch eine abgelehnte ANMELDUNG (gesperrte Maschine, unbrauchbarer
            // Gamertag, zu viele Anmeldungen) ist kein Grund, die Runde zu
            // vergessen: an ihr liegt es nicht.
            return (Ausgang.SpaeterNochmal, "could not sign up with the server: " + e.Message);
        }

        lap.Track = track;
        var ms = (int)Math.Round(lap.LapSeconds * 1000.0);
        try
        {
            await LapSubmit.SubmitAsync(wer, lap, course, timeout, gamertag).ConfigureAwait(false);
            InsBuch(buch, befund.Schluessel, ms);
            return (Ausgang.Gesendet, $"submitted: {lap.LapSeconds:0.000} s on {track} -- {befund.Grund}");
        }
        catch (LapSubmit.Rejected r) when (AusgangFuer(r.Status) == Ausgang.NichtSchneller)
        {
            // Der Server kennt eine schnellere Zeit (sein Datensatz ist neuer).
            // Merken, damit genau diese Runde nie wieder gefragt wird.
            InsBuch(buch, befund.Schluessel, ms);
            return (Ausgang.NichtSchneller, "server says not faster: " + r.Message);
        }
        catch (LapSubmit.Rejected r) when (AusgangFuer(r.Status) == Ausgang.Ungueltig)
        {
            return (Ausgang.Ungueltig, "server refused the lap: " + r.Message);
        }
        catch (Exception e)
        {
            return (Ausgang.SpaeterNochmal, "submission failed: " + e.Message);
        }
    }

    /// <summary>
    /// Anmelden -- und weist der Server den GAMERTAG ab (unerlaubte Zeichen), ohne
    /// ihn. Die Runde soll trotzdem hinaus; gesperrt wird ohnehin ueber Kennung und
    /// Hardware-Hash, nicht ueber den Namen.
    /// </summary>
    private async Task<LapSubmit.Identity> Anmelden(string gamertag, TimeSpan timeout)
    {
        try
        {
            return await LapSubmit.RegisterAsync(_settings.DatasetUrl!, gamertag, timeout).ConfigureAwait(false);
        }
        catch (LapSubmit.Rejected r) when (r.Status == 400 && gamertag.Length > 0)
        {
            _log($"the server refused the gamertag ({r.Message}) -- signing up without it");
            return await LapSubmit.RegisterAsync(_settings.DatasetUrl!, string.Empty, timeout).ConfigureAwait(false);
        }
    }

    private static void InsBuch(Dictionary<string, int> buch, string schluessel, int ms)
    {
        buch[schluessel] = ms;
        try { LedgerSichern(buch); } catch (Exception) { }
    }

    /// <summary>Pause between two waiting laps, so a long queue is not one burst.</summary>
    internal static TimeSpan NachreichPause { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Send the waiting laps -- each one only if it STILL beats the leaderboard.
    /// </summary>
    /// <returns>One line on what happened, or null if nothing was tried.</returns>
    /// <remarks>
    /// ## When
    ///
    /// The Rivals tab calls this right after the server answered a dataset check:
    /// that proves the server is reachable, and the leaderboard just loaded is the
    /// newest there is. It checks every hour while laps are waiting, and again when
    /// submission is switched on.
    ///
    /// ## Checked again, against today's leaderboard
    ///
    /// A lap that beat the board in March may be slow by June. Sent anyway, the
    /// server would refuse it -- and count a strike if it is clearly slower. So each
    /// waiting lap goes through <see cref="Pruefen"/> once more with the current
    /// data and ledger, and is dropped quietly if it no longer qualifies.
    ///
    /// ## Stops at the first thing that is not a success
    ///
    /// No connection: the next lap would fail the same way. A refusal: our picture
    /// of the board is behind the server's, and every further lap risks a strike.
    /// Two refusals within 24 hours pause it entirely (see <see cref="LapQueue"/>).
    /// </remarks>
    public async Task<string?> NachreichenAsync(CancellationToken token = default)
    {
        if (Hindernis() is not null) { return null; }
        var data = _advisor()?.Data;
        if (data is null) { return null; }
        if (LapQueue.RuhtBis(DateTimeOffset.Now) is { } ruht)
        {
            return $"waiting laps paused until {ruht.LocalDateTime:yyyy-MM-dd HH:mm} after two refusals";
        }
        var warten = LapQueue.Alle();
        if (warten.Count == 0) { return null; }

        if (!await EinerZurZeit.WaitAsync(0, token).ConfigureAwait(false)) { return null; }
        int gesendet = 0, verworfen = 0;
        string? halt = null;
        try
        {
            foreach (var e in warten)
            {
                if (token.IsCancellationRequested || Hindernis() is not null) { break; }
                var buch = LedgerLaden();
                var befund = Pruefen(e.Lap, e.Track, data, buch);
                if (!befund.Senden)
                {
                    LapQueue.Entfernen(e.Key);
                    verworfen++;
                    _log($"waiting lap dropped ({e.Lap.LapSeconds:0.000} s on {e.Track}): {befund.Grund}");
                    continue;
                }
                var (ausgang, meldung) = await SendenAsync(e.Lap, e.Course, e.Track!, befund, buch)
                                                .ConfigureAwait(false);
                if (ausgang == Ausgang.SpaeterNochmal)
                {
                    e.Attempts++;
                    e.LastTry = DateTimeOffset.Now;
                    e.LastResult = meldung;
                    try { LapQueue.Sichern(e); } catch (Exception) { }
                    halt = meldung;
                    break;
                }
                LapQueue.Entfernen(e.Key);
                if (ausgang == Ausgang.Gesendet)
                {
                    gesendet++;
                    _log($"{meldung} (waiting since {e.QueuedAt.LocalDateTime:yyyy-MM-dd})");
                    await Task.Delay(NachreichPause, token).ConfigureAwait(false);
                    continue;
                }
                LapQueue.AblehnungMerken(DateTimeOffset.Now);
                halt = meldung;
                break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            halt = "sending waiting laps failed: " + e.Message;
        }
        finally
        {
            EinerZurZeit.Release();
        }

        var uebrig = LapQueue.Anzahl();
        var zusammen = $"waiting laps: {gesendet} submitted, {verworfen} no longer faster, {uebrig} still waiting"
                       + (halt is null ? string.Empty : " -- " + halt);
        _log(zusammen);
        return zusammen;
    }
}
