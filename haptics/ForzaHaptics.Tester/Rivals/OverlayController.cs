using System.Linq;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Drives the two overlay panels: triggers, reading, and what each one draws.
/// </summary>
/// <remarks>
/// The panels answer different questions, so they are never up together: each has
/// its own button, and showing one takes the other away. Nothing is on screen until
/// a button asks for it, and it takes itself away again after
/// <c>show_seconds</c> -- including when the read FAILED, so a "could not read the
/// routes" message is never left standing.
///
/// Telemetry is NOT read here. The host app already owns the UDP socket, and Forza
/// sends its stream to exactly one endpoint: a second listener on the same port
/// fails outright (WinError 10013/10048, measured). Living in the same process is
/// what makes both the haptics and this panel work at once.
/// </remarks>
internal sealed class OverlayController : IDisposable
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static readonly Dictionary<string, ushort> PadButtons =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["DPAD_UP"] = 0x0001, ["DPAD_DOWN"] = 0x0002, ["DPAD_LEFT"] = 0x0004,
            ["DPAD_RIGHT"] = 0x0008, ["START"] = 0x0010, ["MENU"] = 0x0010,
            ["BACK"] = 0x0020, ["VIEW"] = 0x0020, ["LEFT_THUMB"] = 0x0040,
            ["RIGHT_THUMB"] = 0x0080, ["LB"] = 0x0100, ["RB"] = 0x0200,
            ["A"] = 0x1000, ["B"] = 0x2000, ["X"] = 0x4000, ["Y"] = 0x8000,
        };

    private static readonly Dictionary<int, string> Drivetrain = new()
    {
        [0] = "FWD", [1] = "RWD", [2] = "AWD",
    };

    private readonly Control _owner;
    private readonly ITelemetryHost? _host;
    private readonly OverlaySettings _settings;
    private readonly RivalsAdvisor _advisor;

    /// <summary>Der geladene Datensatz, fuer Reiter, die Autonamen brauchen.</summary>
    public RivalsAdvisor Advisor => _advisor;
    private readonly RivalsScreenReader _reader;
    private readonly OrdinalMap _ordinals = new();

    private readonly OverlayPanel _right;
    private readonly CourseShapeHud _shapes;
    private readonly CarNoteHud _carNote;
    private readonly LiveMapHud _liveMap;
    private readonly TyreHud _reifen;
    public CarNotes Notes { get; } = new();
    private string _carKey = string.Empty;

    // DIE VORSCHAU AUF DEM ECHTEN SCHIRM ("Show it on the real screen while I set
    // it up"). Solange sie an ist, gehoeren Streifen, Umriss und Notiz ihr: weder die
    // Telemetrie ("kein Rennen -> ausblenden") noch der Vordergrund-Riegel duerfen sie
    // wegnehmen. Bis zum 2026-09-25 taten beide das -- beim naechsten Paket, also
    // nach Bruchteilen einer Sekunde.
    private bool _hudPreview;
    private readonly OverlayPanel _left;
    private readonly System.Windows.Forms.Timer _tick = new();
    private readonly System.Windows.Forms.Timer _triggerTick = new();
    private readonly System.Windows.Forms.Timer _menueTick = new();
    private readonly System.Windows.Forms.Timer _tuneTick = new();
    private MessageHud _meldung = null!;
    private CelebrationHud _feier = null!;
    private int _tuneStufeGezeigt;

    private readonly HashSet<int> _keysDown = new();
    private readonly HashSet<ushort> _padsDown = new();

    // Ohne Spiel keine Panels: sie beantworten die gerade angebotene Fahrt und lesen
    // dafuer den Spielbildschirm. Ueber dem Desktop ist eines davon nur im Weg.
    private readonly GameWatch _game;
    private bool _idleForGame;

    private ScreenState? _screen;
    private string? _lastAdviceKey;
    // The screen whose panel was pressed away. Auto-show would otherwise put it
    // straight back on the next read, a second later, and the button would look
    // broken. A DIFFERENT screen shows again on its own, as it should.
    private string? _waved;
    private string? _lastCarKey;
    private string? _pinned;
    private bool _reading;
    private bool _forced;
    private byte[]? _lastThumb;

    // Telemetry, handed over by the host app; no socket of our own.
    private int? _ordinal;
    private int? _pi;
    private int? _drivetrain;
    private int? _cylinders;
    private DateTime _telemetryAt = DateTime.MinValue;
    private float _tempo;

    // DAS AUTOMENUE NUR SO OFT WIE NOETIG (seit 2026-09-28): jeder Blick ist ein Griff
    // auf den ganzen Schirm (gemessen 57 ms auf 4K). Solange kein Automenue zu sehen
    // ist, genuegt alle 1,5 s -- steht eines offen, wird wieder alle 0,5 s geschaut,
    // damit die Notiz dem Rahmen folgt.
    private DateTime _menueNaechster = DateTime.MinValue;

    /// <summary>Weggeworfen: kein Zeitgeber und kein Rueckruf fasst danach noch etwas an.</summary>
    /// <remarks>
    /// Seit 2026-09-28. Der Controller wird bei jedem neuen Datensatz ersetzt (also bei
    /// jedem Start). Drei seiner Zeitgeber liefen danach weiter: der alte las das
    /// Automenue weiter zweimal je Sekunde vom Schirm, neben dem neuen. Seit Dispose
    /// auch die Fenster schliesst, warf derselbe Zeitgeber "Cannot access a disposed
    /// object" -- so ist es aufgefallen.
    /// </remarks>
    private bool _disposed;

    /// <summary>Jeder Zeitgeber des Controllers -- Dispose haelt sie alle an.</summary>
    /// <remarks>Ein neuer Zeitgeber gehoert hier hinein; der Grenzfalltest prueft das.</remarks>
    internal IEnumerable<System.Windows.Forms.Timer> AlleZeitgeber() =>
        new[] { _tick, _triggerTick, _menueTick, _tuneTick, _meisterschaftWeg };

    private LapAutoSubmit? _submitterFeld;
    private LapAutoSubmit _submitter =>
        _submitterFeld ??= new LapAutoSubmit(() => _advisor, _settings, LogLap);

    /// <summary>The submitter, for the Rivals tab (status line, test button).</summary>
    public LapAutoSubmit Submitter => _submitter;

    private void WireRecorder()
    {
        _recorder.LapCompleted += (_, lap) =>
        {
            var behalten = _laps.Add(lap);
            // Eine neue Bestzeit macht die gemerkte Referenz ungueltig.
            if (behalten) { _reference = null; _referenceForLength = -1; }
            // Neue Runde, neue Suche: die Stelle in der alten Referenz ist wertlos.
            _hint = 0;
            _hintZwei = 0;
            _referenceSample = null;
            _referenceSeconds = null;
            _letztesDelta = null;
            try { _hud?.ClearInputs(); } catch (Exception) { }
            // Die Live-Karte: der gefahrene Teil beginnt mit der naechsten Runde neu.
            try { _liveMap.ClearTrail(); } catch (Exception) { }

            // Ins Archiv geht JEDE Runde, auch die langsame: eine Karte lebt von
            // den misslungenen Runden, die Bibliothek nur von der besten.
            // DEN MODUS DAZUSCHREIBEN, BEVOR ABGELEGT WIRD.
            //
            // Nur wenn die Runde ihn nicht schon selbst kennt: eine Freiwelt-Fahrt
            // hat ihn aus der eigenen Uhr und der ist ein Beweis, die Einstellung
            // nur eine Angabe. Ein Beweis wird nicht von einer Angabe ueberschrieben.
            //
            // DER ZULETZT GELESENE MENUESCHIRM ist ebenfalls ein Beleg -- darum steht er
            // vor der Einstellung. Seit 2026-09-26 fuer Horizon Play, seit 2026-09-28
            // fuer jeden: Rivals-Schirm, Horizon-Play-Anmeldung, gewoehnliche Anmeldung.
            var platzMax = _platzMax;
            _platzMax = 0;
            ModusAusSchirm(lap, platzMax);
            if (lap.Mode == "unknown"
                && !string.IsNullOrWhiteSpace(_settings.LapMode)
                && _settings.LapMode != "auto")
            {
                lap.Mode = _settings.LapMode.Trim().ToLowerInvariant();
                lap.ModeEvidence = "setting";
            }

            // FUER DIE MEISTERSCHAFT: welche der angebotenen Strecken war das?
            // Eine Rivals-Runde gehoert zu keiner Anmeldung: dort kaeme der Name aus
            // einem laengst verlassenen Angebot, sobald eine Laenge zufaellig passt.
            _rundenImRennen++;
            var zuordnung = lap.Mode == "rivals" ? null : StreckeZurRunde(lap);
            _letzteStrecke = zuordnung?.Name ?? _letzteStrecke;

            // DEN STRECKENNAMEN DAZUSCHREIBEN, falls der Anmeldeschirm ihn hergibt.
            // Wie beim Modus: nur, wenn die Runde ihn nicht schon selbst kennt.
            if (string.IsNullOrWhiteSpace(lap.Track) && zuordnung is { } strecke)
            {
                lap.Track = strecke.Name;
                lap.TrackEvidence = strecke.Evidence;
                LogLap(strecke.Evidence == "series-order+length"
                    ? $"route: {strecke.Name} (series order on the sign-up screen, confirmed by length)"
                    : $"route: {strecke.Name} (sign-up screen, matched by length)");
            }

            // Die volle Spur nur, wenn sie gewollt ist -- sie kostet rund
            // 1,5 MB je Runde. Weggeworfen wird sie hier und nicht im Aufzeichner:
            // dort wird sie ohnehin gesammelt, und ein zweiter Schalter mitten im
            // Paketpfad waere eine zweite Stelle, an der man ihn vergessen kann.
            if (!_settings.FullTelemetry) { lap.FullTrack = null; }

            var abgelegt = _settings.ArchiveLaps
                ? LapArchive.Save(lap, _settings.LapTag)
                : null;

            // AN DIE SEITE -- nur, wenn die Runde die Bestenliste des Autos schlaegt.
            // Im Hintergrund: ein langsamer Server darf das Rennen nie aufhalten.
            try
            {
                var kurs = LapArchive.CourseKey(lap);
                var routeName = LapAutoSubmit.RouteName(lap, LapArchive.Root, kurs);
                // Fuer die Zeile "to beat" der naechsten Runde dieses Rennens.
                _rundenStrecke = routeName ?? _rundenStrecke;
                _zielBerechnet = DateTime.MinValue;
                _ = _submitter.ConsiderAsync(lap, kurs, routeName);
            }
            catch (Exception e)
            {
                LogLap("submission skipped: " + e.Message);
            }

            var text = behalten
                ? $"lap stored: {lap.LapSeconds:0.000} s, {lap.LengthMetres:0} m, "
                  + $"car {lap.CarOrdinal}, PI {lap.PerformanceIndex}, "
                  + $"wet {lap.WetFraction:0%}"
                : $"lap {lap.LapSeconds:0.000} s -- slower than your own best, kept the best";
            if (abgelegt is not null)
            {
                LogLap($"archived: {abgelegt}");
            }
            LogLap(text);
            try { _hud?.Note(text); } catch (Exception) { }
        };
    }

    /// <summary>
    /// Was gerade an Pedalen und Lenkung passiert, samt dem, was die Bestzeit hier tat.
    /// </summary>
    /// <remarks>
    /// Die Werte der Referenz stammen aus dem Messpunkt AN MEINER STELLE, nicht von
    /// vor genauso vielen Sekunden -- sonst verglichen sich zwei Runden, die
    /// verschieden weit gekommen sind.
    /// </remarks>
    /// <summary>
    /// Die Live-Karte: die Strecke der Delta-Referenz, der gefahrene Teil, das Auto.
    /// </summary>
    /// <remarks>
    /// Eigene Methode und NICHT in UpdateDelta: dort kehrt der Weg zurueck, sobald der
    /// Delta-Streifen abgeschaltet ist -- die Karte soll auch ohne ihn gehen. Ohne
    /// Streifen gibt es allerdings keine Referenz; dann zeigt sie nur den Weg, wie er
    /// gefahren wird. Sichtbar nur im Rennen mit laufender Runde, wie der Streifen.
    /// </remarks>
    private int _karteRunde = -1;

    /// <summary>Die Reifenuebersicht: sichtbar, solange gefahren wird und sie an ist.</summary>
    /// <remarks>
    /// Anders als die Live-Karte auch im freien Fahren -- Temperatur und Grip gelten
    /// ueberall, nicht nur mit laufender Runde. `IsRaceOn` faellt in Menue und Pause
    /// auf 0, dann geht sie weg. Gezeichnet wird im langsamen Takt (10 Hz) und nur
    /// der eigene Block.
    /// </remarks>
    private void UpdateReifen(ForzaPacket packet)
    {
        if (_hudPreview) { return; }
        try
        {
            // Wie GameIsUp, aber ohne dessen Fenstersuche -- das hier laeuft zehnmal
            // je Sekunde, und die Uebergaenge erledigt der Tick.
            var vorne = !_settings.OverlayRequireForza
                        || (_game.Running && (!_settings.OverlayRequireFocus || _game.IsForeground));
            if (!_settings.HudTyres || packet.Get("IsRaceOn") < 0.5 || !vorne)
            {
                if (_reifen.Visible) { _reifen.Hide(); }
                return;
            }
            _reifen.Setze(TyreHud.AusPaket(packet));
            if (!_reifen.Visible)
            {
                _reifen.Show();
                _reifen.TopMost = true;
            }
        }
        catch (Exception)
        {
            // Eine Anzeige darf nie die App mitnehmen.
        }
    }

    private void UpdateLiveMap(ForzaPacket packet)
    {
        if (_hudPreview) { return; }
        try
        {
            // NUR BEI LAUFENDER UHR. `IsRaceOn` steht in Horizon auch beim freien
            // Fahren auf 1, und eine Runde bleibt nach dem Ziel "unterwegs", bis die
            // naechste beginnt. Vorher stand die Karte darum nach dem letzten Rennen
            // einer Meisterschaft weiter da, mit einer Strecke, die nicht mehr gefahren
            // wurde -- und in der Startaufstellung vor dem "GO".
            var rennen = packet.Get("IsRaceOn") >= 0.5 && _recorder.HasLapUnderway
                         && _recorder.CurrentSeconds > 0f;
            if (!_settings.LiveMap || !rennen)
            {
                if (_liveMap.Visible) { _liveMap.Hide(); }
                return;
            }
            // JEDE NEUE RUNDE, JEDES NEUE RENNEN: neue Spur, und die Strecke neu suchen.
            // Rennen 2 einer Meisterschaft ist eine andere Strecke als Rennen 1.
            if (_recorder.LapGeneration != _karteRunde)
            {
                _karteRunde = _recorder.LapGeneration;
                _liveMap.ClearTrail();
                if (!_settings.DeltaHud) { _reference = null; _referenceForLength = -1; }
            }
            // Ohne Delta-Streifen sucht niemand sonst die Referenz -- dann hier. Sonst
            // bliebe die zuletzt gewaehlte stehen, auch auf einer ganz anderen Strecke.
            if (!_settings.DeltaHud) { ReferenzNachfuehren(packet); }
            _liveMap.SetReference(_reference);
            _liveMap.Push((float)packet.Get("PositionX"), (float)packet.Get("PositionZ"));
            if (_liveMap.HasContent && !_liveMap.Visible)
            {
                _liveMap.Show();
                _liveMap.TopMost = true;
            }
        }
        catch (Exception)
        {
            // Eine Karte darf das Rennen nie stoeren.
        }
    }

    private void PushInputs(ForzaPacket packet)
    {
        if (_hud is null) { return; }
        var haben = _reference is not null && !_abseits && _referenceSeconds is not null;
        _hud.PushInputs(
            (float)packet.Get("Accel") / 255f,
            (float)packet.Get("Brake") / 255f,
            (float)packet.Get("Clutch") / 255f,
            (float)packet.Get("Steer") / 127f,
            (float)packet.Get("Gear"),
            haben ? _reference : null,
            haben ? _referenceSeconds : null);
    }

    private DateTime _deltaLoggedAt = DateTime.MinValue;

    /// <summary>
    /// Einmal je Sekunde festhalten, WAS auf dem Streifen stand.
    /// </summary>
    /// <remarks>
    /// Der Streifen haelt sich aus jeder Bildschirmaufnahme heraus -- absichtlich,
    /// sonst liest der Rivals-Leser sich selbst. Die Folge: wenn er etwas Falsches
    /// zeigt, gibt es hinterher kein Bild davon, und die Frage "wieso stand da
    /// -0,7?" ist nicht mehr zu beantworten. Diese Zeilen sind die einzige Spur.
    /// </remarks>
    private void LogDelta(float? delta, string label, float ghost, int platz, int runde,
                          ForzaPacket? packet = null)
    {
        // JEDE FUENFTELSEKUNDE, nicht jede Sekunde.
        //
        // Eine Sekunde sind bei 240 km/h 67 Meter. Am 2026-09-13 sah eine
        // gleichmaessige Veraenderung ueber 100 m im Protokoll darum aus wie ein
        // Sprung von 170 ms -- die Stufe war die Abtastrate, nicht die Anzeige.
        // Erst bei voller Aufloesung liess sich das entscheiden, und dafuer musste
        // das Archiv herhalten. Feiner protokollieren kostet ein paar Zeilen mehr
        // und beantwortet die Frage sofort.
        var jetzt = DateTime.UtcNow;
        if ((jetzt - _deltaLoggedAt).TotalMilliseconds < 200) { return; }
        _deltaLoggedAt = jetzt;
        var referenz = _reference is null
            ? "keine"
            : $"{_reference.LapSeconds:0.000}s/{_reference.LengthMetres:0}m/"
              + $"auto {_reference.CarOrdinal}/"
              + (_reference.StandingStart ? "stehend" : "fliegend");
        // DIE ROHEN UHRENFELDER STEHEN MIT DABEI.
        //
        // Offen ist, woran eine Zeitjagd in der offenen Welt zu erkennen ist: sie
        // ist kein Rennen (die Platzierung duerfte 0 sein), aber sie ist gewertet.
        // Ob `CurrentLap` dabei laeuft, entscheidet, ob der Riegel oben sie
        // durchlaesst -- und das laesst sich nur FAHREND messen, nicht herleiten.
        // Eine einzige Zeitjagd mit dieser Fassung beantwortet es; ohne diese
        // Felder im Protokoll muesste danach wieder jemand losfahren.
        var roh = packet is null
            ? string.Empty
            : $" roh[lap={packet.Get("CurrentLap"):0.00} "
              + $"race={packet.Get("CurrentRaceTime"):0.00} "
              + $"last={packet.Get("LastLap"):0.00} "
              + $"raceOn={packet.Get("IsRaceOn"):0} "
              + $"speed={packet.Get("Speed"):0.0}]";
        Write("delta.log",
              $"m={_recorder.CurrentMetres:0} t={_recorder.CurrentSeconds:0.00} "
              + $"delta={(delta is null ? "--" : delta.Value.ToString("0.000"))} "
              + $"geist={ghost:0.0} platz={platz} runde={runde} "
              + $"start={(_recorder.StandingStart ? "stehend" : "fliegend")} "
              + $"abseits={_abseits} "
              + $"ref={referenz}{roh} [{label}]");
    }

    /// <summary>
    /// Jede abgeschlossene Runde ins Protokoll, damit sich das Ausbleiben erklaeren laesst.
    /// </summary>
    /// <remarks>
    /// Der Streifen haelt sich aus jeder Bildschirmaufnahme heraus -- "ich habe nichts
    /// gesehen" ist darum nicht durch ein Foto zu pruefen. Diese Zeilen sind die
    /// einzige Spur, die nach einem Rennen noch da ist.
    /// </remarks>
    private static void LogLap(string text) => Write("laps.log", text);

    /// <summary>Eine Zeile ins Rundenprotokoll -- auch fuer das Nachreichen im Rivals-Tab.</summary>
    public static void WriteLapLog(string text) => LogLap(text);

    /// <summary>Eine Zeile ins Protokoll, auch aus anderen Teilen des Overlays.</summary>
    public static void WriteDiagnostic(string text) => Write("delta.log", text);

    private static void Write(string datei, string text)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "forza-overlay");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, datei),
                               $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}"
                               + Environment.NewLine);
        }
        catch (Exception)
        {
            // Diagnose darf nie die Ursache eines Fehlers sein.
        }
    }

    public OverlayController(Control owner, RivalsAdvisor advisor,
                             OverlaySettings settings, ITelemetryHost? host = null)
    {
        _owner = owner;
        _host = host;
        _advisor = advisor;
        _settings = settings;
        _reader = new RivalsScreenReader(advisor, settings);
        _game = new GameWatch(settings.ForzaProcess);
        WireRecorder();

        // DIE SPIELFLAECHE, nicht der Hauptschirm -- siehe GameArea. Laeuft das Spiel
        // noch nicht, ist es vorerst der Hauptschirm; FollowGameArea zieht nach.
        var screen = GameArea.Find(_settings.ForzaProcess);
        _area = screen;
        OverlayAusgabe.Flaeche = screen;
        var (rechts, links) = PanelRects(screen);
        _right = new OverlayPanel(rechts, _settings.Opacity);
        _left = new OverlayPanel(links, _settings.Opacity);
        // Ueber den ganzen Schirm, gezeichnet wird nur der eigene Block: so kann er
        // ueberall hin, ohne dass das Fenster mitwandern muss.
        _shapes = new CourseShapeHud(_settings, screen);
        _carNote = new CarNoteHud(_settings, screen);
        _meldung = new MessageHud(screen);
        _feier = new CelebrationHud(screen);
        // DIE FEIER (seit 2026-09-27): der Einreicher meldet eine Runde, die die
        // Website schlaegt, aus einem Hintergrundfaden -- gezeigt wird im Fenster-Faden.
        _submitter.RekordGefahren += r =>
        {
            try { _owner.BeginInvoke(() => { if (!_disposed) { Feiern(r); } }); } catch (Exception) { }
        };
        _submitter.NeuesAutoEingetragen += r =>
        {
            try { _owner.BeginInvoke(() => { if (!_disposed) { NeuesAutoFeiern(r); } }); } catch (Exception) { }
        };
        _liveMap = new LiveMapHud(_settings, screen);
        _reifen = new TyreHud(_settings, screen);

        _tick.Interval = Math.Max(250, (int)(_settings.PollSeconds * 1000));
        _tick.Tick += (_, _) => Tick();
        _triggerTick.Interval = 60;
        _triggerTick.Tick += (_, _) => PollTriggers();

        // DAS AUTOMENUE WIRD IMMER BEOBACHTET, nicht nur bei gestartetem Overlay: die
        // Autonotiz ist ein eigenes Stueck und haengt nicht am Rivals-Panel.
        _menueTick.Interval = 500;
        _menueTick.Tick += (_, _) =>
        {
            if (_disposed) { return; }
            PruefeAutomenue();
            PruefeUmrisseZeit();
            AnmeldungOhneOverlay();
            // Was die App kostet, einmal je Minute ins perf.log (siehe Leistung).
            if (_game.Running) { Leistung.Zustand(FaehrtGerade()); }
            Leistung.Protokolliere(_game.Running, _game.Running && _game.IsForeground);
        };
        _menueTick.Start();

        // DER TUNE-SPEICHER: erst nach einer halben Minute, dann alle fuenf Minuten.
        // Zaehlen heisst nur, Ordnernamen aufzulisten -- billig.
        _meisterschaftWeg.Tick += (_, _) =>
        {
            _meisterschaftWeg.Stop();
            if (_disposed) { return; }
            _angebot.Clear();
            _erledigt.Clear();
            if (!_rennenLaeuft) { HideShapes(); }
        };

        // Kursen ihre Namen nachtragen, einmal, im Hintergrund -- und die Tune-Koepfe
        // schon einmal lesen, damit der erste Autowechsel nicht darauf wartet.
        Task.Run(() =>
        {
            try { _ = Tuning.TuneStorage.AppliedFor(0); } catch (Exception) { }
            try
            {
                var n = LapArchive.NamenNachtragen();
                if (n > 0) { WriteDiagnostic($"Kursnamen aus den Runden nachgetragen: {n}"); }
            }
            catch (Exception) { }
        });

        _tuneTick.Interval = 30_000;
        _tuneTick.Tick += (_, _) => { if (_disposed) { return; } _tuneTick.Interval = 300_000; PruefeTunes(); };
        _tuneTick.Start();
    }

    /// <summary>
    /// Wie voll ist der Tune-Speicher des Spiels? Bei wenigen freien Plaetzen und bei
    /// vollem Speicher je einmal melden.
    /// </summary>
    /// <remarks>
    /// Seit 2026-09-26: das Spiel verweigerte Downloads erst, als es zu spaet war --
    /// dann heisst es, Auto fuer Auto aufzuraeumen. Gemeldet wird nur, waehrend das
    /// Spiel vorne ist, und jede Stufe nur einmal, bis wieder Platz ist.
    /// </remarks>
    private void PruefeTunes()
    {
        try
        {
            if (!_game.Running || !_game.IsForeground) { return; }
            if (Tuning.TuneStorage.Count() is not { } anzahl) { return; }
            var grenze = Math.Max(1, _settings.TuneLimit);
            var frei = grenze - anzahl;
            var stufe = frei <= 0 ? 2 : frei <= _settings.TuneWarnFree ? 1 : 0;
            if (stufe == 0) { _tuneStufeGezeigt = 0; return; }
            if (stufe <= _tuneStufeGezeigt) { return; }
            _tuneStufeGezeigt = stufe;
            var titel = stufe == 2 ? Loc.T("Tune storage full") : Loc.T("Tune storage almost full");
            var text = string.Format(Loc.T("{0} of {1} tunes -- {2} free."), anzahl, grenze, Math.Max(0, frei))
                       + " " + (stufe == 2
                           ? Loc.T("The game will refuse new downloads.")
                           : Loc.T("Soon the game will refuse new downloads."))
                       + " " + Loc.T("The app's Tunes tab lists the ones that are on no car.");
            _meldung.Zeige(titel, text,
                           stufe == 2 ? Color.FromArgb(255, 107, 107) : Color.FromArgb(255, 210, 90), 12);
            WriteDiagnostic($"Tune-Speicher: {anzahl}/{grenze}, Stufe {stufe} gemeldet");
        }
        catch (Exception)
        {
            // Eine Warnung darf nichts anhalten.
        }
    }

    public bool Running { get; private set; }
    public string OcrLanguage => _reader.OcrLanguage;
    public bool OcrAvailable => _reader.OcrAvailable;
    public int LearnedOrdinals => _ordinals.Count;
    public string Status { get; private set; } = "stopped";

    public event EventHandler? StatusChanged;

    public void Start()
    {
        if (Running)
        {
            return;
        }
        Running = true;
        _tick.Start();
        _triggerTick.Start();
        SetStatus(_reader.OcrAvailable
            ? $"watching — OCR {_reader.OcrLanguage}"
            : "watching — no OCR language installed, the route panel cannot read");
    }

    public void Stop()
    {
        Running = false;
        _tick.Stop();
        _triggerTick.Stop();
        Hide(_right);
        HideShapes();
        Hide(_left);
        SetStatus("stopped");
    }

    private void SetStatus(string status)
    {
        Status = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// JEDES Paket, ungefiltert -- nur die Rundenaufzeichnung.
    /// </summary>
    /// <remarks>
    /// Die Anzeige der App laeuft aus gutem Grund nur zehnmal je Sekunde; die
    /// AUFZEICHNUNG darf das nicht. GEMESSEN am 2026-09-12: bei 10 Hz lagen die
    /// Messpunkte einer schnellen Runde 20 bis 46 m auseinander statt der
    /// vorgesehenen 5 m -- bei 400 km/h liegen zwischen zwei Paketen eben 11 m.
    /// Damit wird jedes Delta grob und jede spaetere Karte loechrig.
    ///
    /// Hier laeuft deshalb nur, was billig ist: ein Vergleich, eine Subtraktion,
    /// gelegentlich ein Listeneintrag. Gezeichnet wird weiterhin im langsamen Takt.
    /// </remarks>
    public void OnTelemetryRaw(ForzaPacket packet)
    {
        if (_disposed) { return; }
        // Der hoechste Platz der laufenden Runde: in Rivals faehrt niemand sonst, dort
        // steht immer 1. Ein Platz dahinter widerlegt einen Rivals-Schirm.
        var platz = (int)packet.Get("RacePosition");
        if (platz > _platzMax) { _platzMax = platz; }
        try { _recorder.OnTelemetry(packet); } catch (Exception) { }
    }

    /// <summary>Every telemetry packet the host app parses, at its own rate.</summary>
    public void OnTelemetry(ForzaPacket packet)
    {
        if (_disposed) { return; }
        _ordinal = (int)packet.Get("CarOrdinal");
        _pi = (int)packet.Get("CarPerformanceIndex");
        _drivetrain = (int)packet.Get("DrivetrainType");
        _cylinders = (int)packet.Get("NumCylinders");
        _tempo = (float)packet.Get("Speed");
        _telemetryAt = DateTime.UtcNow;
        UpdateCarNote(packet);
        UpdateDelta(packet);
        UpdateLiveMap(packet);
        UpdateReifen(packet);
        UpdateMeisterschaft(packet);
    }

    // ------------------------------------------------------------------ //
    // die Meisterschaft: angebotene Strecken, gefahren, jetzt, danach
    // ------------------------------------------------------------------ //

    private List<(string Name, string Key)> _angebot = new();
    private readonly HashSet<int> _erledigt = new();
    private DateTime _angebotZeit = DateTime.MinValue;
    private bool _rennenLaeuft;
    private DateTime _rennenStillSeit = DateTime.MinValue;
    private int _rundenImRennen;
    private string? _letzteStrecke;
    /// <summary>Der Streckenname der zuletzt beendeten Runde dieses Rennens, wie ihn das Einreichen nimmt.</summary>
    private string? _rundenStrecke;
    private readonly System.Windows.Forms.Timer _meisterschaftWeg = new() { Interval = 20_000 };

    // DIE ANZEIGEDAUER DER KARTEN, je Angebot (siehe ShapeDisplayClock).
    private readonly ShapeDisplayClock _umrisseUhr = new();

    /// <summary>So lange gilt ein gelesenes Angebot als die laufende Meisterschaft.</summary>
    private static readonly TimeSpan AngebotGilt = TimeSpan.FromMinutes(45);

    /// <summary>
    /// Laeuft ein Rennen? Beim Start die Umrisse weg, am Ende die Meisterschaft
    /// weiterzaehlen und zeigen, was als Naechstes kommt.
    /// </summary>
    /// <remarks>
    /// Seit 2026-09-26. Bis dahin blieben die drei Umrisse vom Anmeldeschirm bis in
    /// das Rennen hinein stehen: ausgeblendet wurden sie nur, wenn man das Panel
    /// wegdrueckte oder das Spiel verliess. "Laeuft" heisst wie beim Delta: die Uhr
    /// der Runde laeuft -- nicht `IsRaceOn`, das in Horizon auch beim freien Fahren
    /// auf 1 steht.
    /// </remarks>
    private void UpdateMeisterschaft(ForzaPacket packet)
    {
        if (_hudPreview) { return; }
        var laeuft = packet.Get("IsRaceOn") >= 0.5 && _recorder.HasLapUnderway
                     && _recorder.CurrentSeconds > 0f && !_recorder.InFreeRoam;
        if (laeuft)
        {
            _rennenStillSeit = DateTime.MinValue;
            if (!_rennenLaeuft)
            {
                _rennenLaeuft = true;
                _rundenImRennen = 0;
                _letzteStrecke = null;
                _rundenStrecke = null;
                _zielBerechnet = DateTime.MinValue;
                _meisterschaftWeg.Stop();
                _umrisseUhr.RaceStarted();
                // DAS RENNEN BEGINNT: die Umrisse gehoeren davor, nicht darueber --
                // und die Autonotiz samt Tune ebenso (Nutzermeldung 2026-09-27).
                HideShapes();
                NotizWeg();
            }
            return;
        }
        if (!_rennenLaeuft) { return; }
        // Zwei Sekunden Stillstand der Uhr, erst dann ist es vorbei -- zwischen zwei
        // Runden und beim "GO" steht sie fuer ein, zwei Pakete auf null.
        if (_rennenStillSeit == DateTime.MinValue) { _rennenStillSeit = DateTime.UtcNow; return; }
        if (DateTime.UtcNow - _rennenStillSeit < TimeSpan.FromSeconds(2)) { return; }
        _rennenLaeuft = false;
        RennenVorbei();
    }

    /// <summary>Ein Rennen ist zu Ende: die gefahrene Strecke abhaken, die naechste zeigen.</summary>
    private void RennenVorbei()
    {
        if (_angebot.Count == 0 || DateTime.UtcNow - _angebotZeit > AngebotGilt) { return; }
        // Nur, wenn wirklich eine Runde fertig wurde -- ein Abbruch ist keine gefahrene Strecke.
        if (_rundenImRennen == 0) { return; }
        var index = _letzteStrecke is { } s
            ? _angebot.FindIndex(a => string.Equals(a.Name, s, StringComparison.OrdinalIgnoreCase))
            : -1;
        // Sonst die erste noch offene: eine Meisterschaft faehrt die Strecken in der
        // Reihenfolge des Anmeldeschirms.
        if (index < 0 || _erledigt.Contains(index))
        {
            index = Enumerable.Range(0, _angebot.Count).FirstOrDefault(i => !_erledigt.Contains(i), -1);
        }
        if (index >= 0) { _erledigt.Add(index); }
        _angebotZeit = DateTime.UtcNow;
        WriteDiagnostic($"Meisterschaft: {(index >= 0 ? _angebot[index].Name : "?")} gefahren, "
                        + $"{_erledigt.Count}/{_angebot.Count}");
        // Nach jedem Rennen eine frische Anzeige mit eigener Zeit.
        _umrisseUhr.RaceEnded();
        ZeigeAngebot();
        if (_erledigt.Count >= _angebot.Count)
        {
            // ALLES GEFAHREN: noch kurz zeigen, dann weg -- und die Meisterschaft vergessen.
            _meisterschaftWeg.Stop();
            _meisterschaftWeg.Start();
        }
    }

    /// <summary>Die Strecken vor dem Einstieg in eine Reihe -- die faehrt man nicht mehr.</summary>
    internal static IEnumerable<int> Einstieg(int ab, int anzahl) => Enumerable.Range(0, Math.Clamp(ab, 0, anzahl));

    /// <summary>Wie jede angebotene Strecke steht: gefahren, jetzt, danach.</summary>
    internal static List<CourseShapeHud.TileState> Stati(int anzahl, IReadOnlySet<int> erledigt)
    {
        var raus = Enumerable.Repeat(CourseShapeHud.TileState.None, anzahl).ToList();
        if (erledigt.Count == 0) { return raus; }
        var offen = Enumerable.Range(0, anzahl).Where(i => !erledigt.Contains(i)).ToList();
        foreach (var i in erledigt) { if (i < anzahl) { raus[i] = CourseShapeHud.TileState.Done; } }
        if (offen.Count > 0) { raus[offen[0]] = CourseShapeHud.TileState.Now; }
        if (offen.Count > 1) { raus[offen[1]] = CourseShapeHud.TileState.Next; }
        return raus;
    }

    // ------------------------------------------------------------------ //
    // der Streifen oben: Delta und Geister-Countdown
    // ------------------------------------------------------------------ //

    private readonly LapRecorder _recorder = new();
    private readonly LapLibrary _laps = new();
    private DeltaHud? _hud;
    private RecordedLap? _reference;
    private int _referenceForLength = -1;

    /// <summary>Ob zwei Vergleiche nebeneinander gezeigt werden.</summary>
    private bool DualDelta =>
        string.Equals(_settings.DeltaReferenceMode, "dual", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Die Reihenfolge, in der die Taste durchschaltet -- von eng nach weit.
    /// </summary>
    /// <remarks>
    /// Nicht alphabetisch, sondern so, wie die Frage im Kopf enger wird: erst dieses
    /// Auto mit dieser Abstimmung, zuletzt "irgendwas von mir auf dieser Strecke".
    /// Wer die Taste dreimal drueckt, weiss dann, wo er ist, ohne nachzusehen.
    /// </remarks>
    internal static readonly string[] DeltaModes =
        { "tune", "car", "carclass", "class", "any", "dual" };

    /// <summary>
    /// Die naechste Vergleichsstufe waehlen -- im Rennen, ohne das Spiel zu verlassen.
    /// </summary>
    /// <remarks>
    /// Die Stufe entscheidet, WELCHE Frage die Zahl auf dem Schirm beantwortet. Die
    /// wechselt man mitten in einer Sitzung ("wie stehe ich gegen mein Bestes mit
    /// diesem Auto" gegen "gegen alles in dieser Klasse") -- und dafuer erst ins
    /// Menue zu gehen, heisst, es nie zu tun.
    ///
    /// Die gemerkte Referenz faellt dabei weg: sie gehoert zur alten Frage.
    /// </remarks>
    /// <summary>
    /// Hier eine Start-Ziel-Linie setzen -- fuer eine Strecke, die es im Spiel nicht
    /// gibt.
    /// </summary>
    /// <remarks>
    /// Die Linien aus dem Rundenbestand kommen von selbst dazu; diese Taste ist fuer
    /// alles andere: eine Landstrasse, eine Passhoehe, ein selbst gesteckter
    /// Rundkurs. Zweimal ueber dieselbe Linie, und es gibt eine Zeit.
    ///
    /// Der Name ist durchnummeriert und kein Dialog. Ein Eingabefenster mitten im
    /// Fahren waere genau das, was diese Taste vermeiden soll; umbenennen laesst
    /// sich die Linie hinterher in freeroam_lines.json.
    /// </remarks>
    private void SetzeFreieLinie()
    {
        try
        {
            var eigene = _recorder.Lines.Anchors.Count(a => a.Source == "manual");
            var gesetzt = _recorder.DropLineHere($"my line {eigene + 1}");
            var text = gesetzt is null
                ? "no position yet -- drive a moment, then press again"
                : $"start line \"{gesetzt.Name}\" set here; cross it again to stop the clock";
            LogLap(text);
            try { _hud?.Note(text); } catch (Exception) { }
        }
        catch (Exception)
        {
            // Eine nicht gesetzte Linie ist aergerlich, ein Absturz im Rennen mehr.
        }
    }

    public void CycleDeltaMode()
    {
        var jetzt = Array.FindIndex(DeltaModes, m =>
            string.Equals(m, _settings.DeltaReferenceMode, StringComparison.OrdinalIgnoreCase));
        var naechste = DeltaModes[(jetzt < 0 ? 0 : jetzt + 1) % DeltaModes.Length];
        _settings.DeltaReferenceMode = naechste;
        _settings.Save();

        _reference = null;
        _referenceForLength = -1;
        _hint = 0;
        _hintZwei = 0;

        var text = naechste == "dual"
            ? "compare: two figures -- same car, and same PI class"
            : $"compare: {DeltaModeLabel(DeltaMode)}";
        SetStatus(text);
        LogLap(text);
        // Auf dem Schirm, nicht nur in der App: gedrueckt wird im Rennen.
        try { EnsureHud(); _hud?.Note(text, 4); } catch (Exception) { }
        DeltaModeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Faellt an, wenn die Stufe im Spiel umgeschaltet wurde.</summary>
    public event EventHandler? DeltaModeChanged;

    /// <summary>Die Vergleichsstufe, wie sie in der Einstellung steht.</summary>
    private DeltaReference DeltaMode => (_settings.DeltaReferenceMode ?? "tune").ToLowerInvariant() switch
    {
        "car" => DeltaReference.SameCar,
        "class" => DeltaReference.SameClass,
        "carclass" => DeltaReference.SameCarSameClass,
        "any" => DeltaReference.Anything,
        // "dual" zeigt oben dasselbe Auto; die Klasse kommt als zweite Zahl dazu.
        "dual" => DeltaReference.SameCar,
        _ => DeltaReference.SameCarSameTune,
    };

    /// <summary>
    /// Die Beschriftung um das Auto der Referenz ergaenzen: sein Name, oder "this car",
    /// wenn die Referenz mit dem eigenen Auto gefahren wurde.
    /// </summary>
    /// <remarks>
    /// ", this course" faellt dafuer weg -- die Zeile ist klein, und dass es um diese
    /// Strecke geht, versteht sich.
    /// </remarks>
    private string MitReferenzAuto(string beschriftung, RecordedLap referenz, ForzaPacket packet)
    {
        var ordinal = referenz.CarOrdinal;
        if (ordinal <= 0) { return beschriftung; }
        var eigenes = (int)packet.Get("CarOrdinal");
        var name = ordinal == eigenes ? "this car" : AutoName(ordinal);
        // EINE ANDERE KLASSE WIRD GENANNT: "any car" darf eine R-Klasse-Zeit gegen ein
        // A-Klasse-Auto stellen -- aber nicht, ohne es zu sagen.
        var klasse = (int)packet.Get("CarClass");
        if (referenz.CarClass != klasse && KlassenName(referenz.CarClass) is { } fremd)
        {
            name += $" ({fremd})";
        }
        return beschriftung.Replace(", this course", string.Empty) + " · " + name;
    }

    private static readonly string[] Klassen = { "D", "C", "B", "A", "S1", "S2", "R" };

    /// <summary>Der Name einer Klasse aus der Telemetrie (0..6 = D bis R) -- oder null.</summary>
    internal static string? KlassenName(int klasse) =>
        klasse >= 0 && klasse < Klassen.Length ? Klassen[klasse] : null;

    // ------------------------------------------------------------------ //
    // Die Zeit zum Schlagen (seit 2026-09-27)
    // ------------------------------------------------------------------ //

    private DateTime _zielBerechnet = DateTime.MinValue;
    private string _zielZeile = string.Empty;

    /// <summary>
    /// Die Zeile unter dem Delta: welche Zeit diese Runde schlagen muss, um auf die
    /// Website zu kommen -- dieselbe Regel wie beim Einreichen (LapAutoSubmit.ZuSchlagen).
    /// Hoechstens einmal je Sekunde gerechnet; leer, solange die Strecke unklar ist.
    /// </summary>
    private string ZielZeile(ForzaPacket packet)
    {
        if (!_settings.HudTarget) { return string.Empty; }
        if (DateTime.UtcNow - _zielBerechnet < TimeSpan.FromSeconds(1)) { return _zielZeile; }
        _zielBerechnet = DateTime.UtcNow;
        try
        {
            var strecke = StreckeImRennen();
            var ziel = LapAutoSubmit.ZuSchlagen(_advisor.Data, strecke, (int)packet.Get("CarClass"),
                                                _ordinal ?? (int)packet.Get("CarOrdinal"), LapAutoSubmit.LedgerLaden());
            _zielZeile = ZielText(ziel);
        }
        catch (Exception)
        {
            _zielZeile = string.Empty;
        }
        return _zielZeile;
    }

    /// <summary>Der Text der Zeile -- English, wie alles, was im Spiel steht.</summary>
    internal static string ZielText(LapAutoSubmit.Ziel? ziel) => ziel switch
    {
        null => string.Empty,
        { Ms: null } => "to beat: nothing -- this car is not on the website's board yet",
        { Eigene: true } z => $"to beat: {RivalsAdvisor.LapText(z.Ms)} -- your submitted time",
        { } z => $"to beat: {RivalsAdvisor.LapText(z.Ms)} -- website best, this car",
    };

    /// <summary>
    /// Welche Strecke gerade gefahren wird -- waehrend der Runde, also bevor ihre
    /// Laenge sie verraet. Null, wenn es nicht eindeutig ist.
    /// </summary>
    /// <remarks>
    /// Der Reihe nach: die Strecke der schon beendeten Runde DIESES Rennens; der
    /// Anmeldeschirm (die naechste Strecke einer Reihe, oder die einzige angebotene);
    /// sonst die eigenen Runden von derselben Startlinie, wenn sie alle dieselbe
    /// Strecke nennen.
    /// </remarks>
    private string? StreckeImRennen()
    {
        if (_rennenLaeuft && (_letzteStrecke ?? _rundenStrecke) is { Length: > 0 } gefahren) { return gefahren; }
        if (_letzterSchirm is { } schirm && DateTime.UtcNow - schirm.Seen <= SchirmGilt && schirm.Routen.Count > 0
            && (string.IsNullOrEmpty(schirm.Klass)
                || string.Equals(LapArchive.ClassOf(_pi ?? 0), schirm.Klass, StringComparison.OrdinalIgnoreCase)))
        {
            if (_schirmReihe && ErwarteteStrecke(schirm.Routen.Count, _schirmAb, _erledigt) is var i and >= 0)
            {
                return schirm.Routen[i].Name;
            }
            if (schirm.Routen.Count == 1) { return schirm.Routen[0].Name; }
        }
        var muster = new RecordedLap
        {
            StartX = _recorder.StartX,
            StartZ = _recorder.StartZ,
            StandingStart = _recorder.StandingStart,
        };
        if (!muster.HasStart) { return null; }
        var namen = _laps.Laps
            .Where(l => l.StandingStart == muster.StandingStart && !l.FreeRoam
                        && !string.IsNullOrWhiteSpace(l.Track) && !OwnTimes.IsFolderKey(l.Track!)
                        && RecordedLap.StartsTogether(l, muster))
            .Select(l => l.Track!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return namen.Count == 1 ? namen[0] : null;
    }

    /// <summary>Der Name eines Autos nach seiner Kennung -- gelernt, sonst Datensatz, sonst die Kennung.</summary>
    private string AutoName(int ordinal)
    {
        var gelernt = _ordinals.Lookup(ordinal);
        if (RivalsAdvisor.IsRealCarName(gelernt?.Name)) { return gelernt!.Name; }
        var index = gelernt?.CarIndex ?? _advisor.CarIndexForId(ordinal);
        return (index is { } ix ? _advisor.RealCarName(ix) : null) ?? $"car {ordinal}";
    }

    /// <summary>English, wie alles, was im Spiel steht.</summary>
    private static string DeltaModeLabel(DeltaReference mode) => mode switch
    {
        DeltaReference.SameCar => "same car, this course",
        DeltaReference.SameClass => "same PI class, this course",
        DeltaReference.SameCarSameClass => "same car in this PI class",
        DeltaReference.Anything => "personal best, any car",
        _ => "same car, same tune",
    };

    /// <summary>
    /// Die eigene Notiz zum gerade gewaehlten Auto zeigen -- oder wegnehmen.
    /// </summary>
    /// <remarks>
    /// WECHSELT DAS AUTO, VERSCHWINDET DIE NOTIZ SOFORT. Genau darum haengt alles
    /// am Fingerabdruck und nicht an einem Zeitgeber: eine Notiz, die nach dem
    /// Wechsel noch eine Sekunde stehen bleibt, liest man am neuen Auto -- und
    /// haelt sie fuer dessen Auskunft.
    ///
    /// Gearbeitet wird nur, wenn sich der Abdruck geaendert hat. Bei 60 Paketen je
    /// Sekunde waere alles andere Verschwendung.
    /// </remarks>
    private void UpdateCarNote(ForzaPacket packet)
    {
        if (_hudPreview) { return; }
        if (!_settings.CarNotes)
        {
            if (_carNote is not null && !_carNote.IsDisposed && _carNote.Visible)
            {
                _carNote.Hide();
            }
            return;
        }

        var maxRpm = (int)MathF.Round((float)packet.Get("EngineMaxRpm"));
        var idleRpm = (int)MathF.Round((float)packet.Get("EngineIdleRpm"));
        // Ein Auto ohne Drehzahlen ist kein Auto, sondern ein Paket aus einem
        // Menue, in dem gar keins geladen ist.
        // Die Felder sind nullable: vor dem ersten Paket ist nichts bekannt.
        var ordinal = _ordinal ?? 0;
        var pi = _pi ?? 0;
        if (ordinal <= 0 || maxRpm <= 0) { return; }
        // IM AUTOMENUE GILT DAS AUTO UNTER DEM RAHMEN, nicht das gefahrene -- die
        // Telemetrie nennt dort weiter das Auto, mit dem man hereinkam.
        if (_menueAuto is not null) { return; }

        // WEG, SOBALD GEFAHREN WIRD (2026-09-27). Die Notiz ist eine Auskunft fuer die
        // Wahl des Autos, nicht fuers Fahren -- und in der freien Fahrt gibt es keinen
        // Rennstart, der sie wegnaehme. Drei Sekunden ueber 30 km/h: ein Rangieren am
        // Startplatz ist kein Losfahren.
        if (_losfahren.Update(packet.Get("Speed"), DateTime.UtcNow)) { NotizWeg(); }

        var key = CarNotes.Fingerprint(ordinal, pi, _drivetrain ?? 0,
                                       _cylinders ?? 0, maxRpm, idleRpm);
        if (key == _carKey) { return; }
        _carKey = key;

        var index = _ordinals.Lookup(ordinal)?.CarIndex
                    ?? _advisor.CarIndexForId(ordinal);
        var name = (RivalsAdvisor.IsRealCarName(_ordinals.Lookup(ordinal)?.Name)
                        ? _ordinals.Lookup(ordinal)!.Name : null)
                   ?? (index is { } ix ? _advisor.RealCarName(ix) : null);

        var kw = (int)MathF.Round((float)packet.Get("Power") / 1000f);
        var eintrag = Notes.Note(key, ordinal, name ?? string.Empty, pi,
                                 Math.Max(0, kw));
        // AUCH ALS MODELL, und damit auf der Platte: vorher lebte die Liste nur im
        // Speicher und war nach jedem Neustart leer.
        Notes.NoteModel(ordinal, name, "driven");

        var kopf = name ?? $"car {ordinal}";
        if (pi > 0) { kopf += $"  ·  PI {pi}"; }
        if (eintrag.Kilowatts > 0) { kopf += $"  ·  {eintrag.HorsePower} hp"; }

        // GERADE IM AUTOMENUE GEWAEHLT: dort stand die Notiz schon. Bis 2026-09-27 kam
        // sie nach dem Verlassen des Menues sofort wieder -- fuer das eben gewaehlte
        // Auto, sobald die Telemetrie es meldete -- und blieb bis ins Rennen stehen.
        // Der Reiter "Car notes" folgt dem gefahrenen Auto trotzdem.
        if (_nachAuswahl)
        {
            SetzeAktuell(ordinal, name ?? $"car {ordinal}", "driven");
            return;
        }
        ZeigeNotiz(ordinal, name ?? $"car {ordinal}", key, "driven", kopf);
    }

    /// <summary>Nach der Autowahl im Menue: keine Notiz fuer das gefahrene Auto zeigen.</summary>
    /// <remarks>Endet mit dem Losfahren oder dem Rennstart (NotizWeg).</remarks>
    private bool _nachAuswahl;

    private readonly LosfahrWaechter _losfahren = new();

    /// <summary>Die Autonotiz wegnehmen: Rennstart oder Losfahren.</summary>
    /// <remarks>
    /// Die Kennung des Autos bleibt stehen: dasselbe Auto zeigt seine Notiz danach
    /// nicht wieder. Erst ein anderes Auto -- ohne Umweg ueber das Automenue, etwa ein
    /// vom Rennen gestelltes -- oder das naechste Automenue zeigt wieder eine.
    /// </remarks>
    private void NotizWeg()
    {
        _nachAuswahl = false;
        if (_carNote.Visible) { _carNote.Hide(); }
    }

    // ------------------------------------------------------------------ //
    // das Auto unter dem Rahmen im Automenue (seit 2026-09-25)
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Das Auto, um das es gerade geht: unter dem Rahmen im Automenue, sonst das
    /// gefahrene. Der Reiter "Car notes" waehlt es damit von selbst aus.
    /// </summary>
    public (int Ordinal, string Name, string Source)? CurrentCar { get; private set; }

    public event EventHandler? CurrentCarChanged;

    private (int Ordinal, string Name)? _menueAuto;
    private volatile bool _menueLesen;
    private int _menueFehlt;
    private Rectangle _menueRahmen;
    private byte[]? _menueAbdruck;

    /// <summary>Die Notiz eines Autos zeigen -- oder wegnehmen, wenn es keine hat.</summary>
    private void ZeigeNotiz(int ordinal, string name, string? aufbau, string quelle, string kopf)
    {
        SetzeAktuell(ordinal, name, quelle);
        var kommentar = Notes.CommentFor(aufbau, ordinal);
        // DAS AUFGESPIELTE TUNE DARUNTER -- Name, Tuner, Beschreibung. Nutzerwunsch vom
        // 2026-09-26: was der Tuner zu seinem Tune schreibt, ist oft genau die Notiz,
        // die man sich sonst selbst machen muesste ("B Road Grip Shirakawa 01:06.2").
        if (_settings.CarNoteTune && TuneText(ordinal) is { } tune)
        {
            kommentar = string.IsNullOrWhiteSpace(kommentar) ? tune : kommentar.TrimEnd() + "\n\n" + tune;
        }
        _carNote.SetNote(kopf, kommentar);
        if (string.IsNullOrWhiteSpace(kommentar))
        {
            if (_carNote.Visible) { _carNote.Hide(); }
        }
        else if (!_carNote.Visible)
        {
            _carNote.Show();
            _carNote.TopMost = true;
        }
    }

    /// <summary>Das aufgespielte Tune als Zeilen fuer die Autonotiz, oder null.</summary>
    private static string? TuneText(int ordinal)
    {
        if (Tuning.TuneStorage.AppliedFor(ordinal) is not { } a) { return null; }
        var t = a.Tune;
        if (string.IsNullOrWhiteSpace(t.Name) && string.IsNullOrWhiteSpace(t.Description)) { return null; }
        var kopf = (a.Sicher ? "Tune: " : "Tune (probably): ") + t.Name.Trim();
        if (!string.IsNullOrWhiteSpace(t.Creator)) { kopf += "  ·  " + t.Creator.Trim(); }
        return string.IsNullOrWhiteSpace(t.Description) ? kopf : kopf + "\n" + t.Description.Trim();
    }

    private void SetzeAktuell(int ordinal, string name, string quelle)
    {
        var neu = (ordinal, name, quelle);
        if (CurrentCar == neu) { return; }
        CurrentCar = neu;
        CurrentCarChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Eine Notiz wurde im Reiter geaendert: sofort zeigen, nicht erst beim naechsten Auto.</summary>
    public void RefreshCarNote()
    {
        if (CurrentCar is not { } c || !_settings.CarNotes) { return; }
        if (_menueAuto is not null)
        {
            ZeigeNotiz(c.Ordinal, c.Name, null, "menu", c.Name);
        }
        else
        {
            // Beim naechsten Paket neu bestimmen, samt Kopfzeile mit PI und Leistung.
            _carKey = string.Empty;
        }
    }

    /// <summary>
    /// Steht im Spiel gerade ein Automenue mit Rahmen? Dann das Auto darunter lesen.
    /// </summary>
    /// <remarks>
    /// Zweimal je Sekunde, aber billig: ein kleines Abbild, darin der gelbgruene
    /// Rahmen. Gelesen (OCR) wird nur, wenn der Rahmen woanders steht oder sich sein
    /// Titel geaendert hat. NUR WENN DAS SPIEL VORNE IST: wer zur App wechselt, um
    /// eine Notiz zu schreiben, hat das Spiel nicht mehr im Bild -- das zuletzt
    /// gelesene Auto bleibt dann stehen, damit der Reiter es zeigen kann.
    /// </remarks>
    private void PruefeAutomenue()
    {
        try
        {
            if (!_settings.CarNotes || _menueLesen || _reading || _hudPreview || !_reader.OcrAvailable) { return; }
            if (!_game.Running || !_game.IsForeground) { return; }
            if (FaehrtGerade()) { return; }
            if (DateTime.UtcNow < _menueNaechster) { return; }
            var flaeche = GameArea.Find(_settings.ForzaProcess);
            if (flaeche.IsEmpty) { return; }
            _menueLesen = true;
            var vorherRahmen = _menueRahmen;
            var vorherAbdruck = _menueAbdruck;
            var vorherAuto = _menueAuto;
            Task.Run(() =>
            {
                Rectangle? rahmen = null;
                byte[]? abdruck = null;
                (int Ordinal, string Name)? auto = vorherAuto;
                string? gelesen = null;
                var neu = false;
                try
                {
                    var hoehe = Math.Max(1, CarGridReader.Suchbreite * flaeche.Height / Math.Max(1, flaeche.Width));
                    using var klein = GameArea.Capture(flaeche, new Size(CarGridReader.Suchbreite, hoehe));
                    if (CarGridReader.FindeRahmen(klein) is { } k)
                    {
                        rahmen = k;
                        var titelKlein = CarGridReader.TitelIn(k);
                        abdruck = CarGridReader.Fingerabdruck(klein, titelKlein);
                        if (k != vorherRahmen || vorherAuto is null || !CarGridReader.Gleich(abdruck, vorherAbdruck))
                        {
                            neu = true;
                            var f = flaeche.Width / (double)klein.Width;
                            var titel = new Rectangle(flaeche.X + (int)(titelKlein.X * f), flaeche.Y + (int)(titelKlein.Y * f),
                                                      (int)(titelKlein.Width * f), (int)(titelKlein.Height * f));
                            // Wie beim Anmeldeschirm auf 1440p-Mass lesen, nie kleiner als nativ.
                            var skala = Math.Max(1.0, 1440.0 / Math.Max(1, flaeche.Height));
                            using var bild = GameArea.Capture(titel, new Size(Math.Max(1, (int)(titel.Width * skala)),
                                                                              Math.Max(1, (int)(titel.Height * skala))));
                            var zeilen = _reader.ReadLines(bild);
                            gelesen = string.Join(" / ", zeilen.OrderBy(z => z.Y).Select(z => z.Text));
                            auto = ErkenneAuto(zeilen);
                        }
                    }
                }
                catch (Exception ex)
                {
                    gelesen = "FAILED " + ex.GetType().Name + ": " + ex.Message;
                }
                finally
                {
                    _menueNaechster = DateTime.UtcNow + (rahmen is null ? TimeSpan.FromSeconds(1.5) : TimeSpan.Zero);
                    _menueLesen = false;
                }
                try
                {
                    _owner.BeginInvoke(() => { if (!_disposed) { MenueGelesen(rahmen, abdruck, auto, gelesen, neu); } });
                }
                catch (Exception)
                {
                    // Das Fenster geht gerade zu.
                }
            });
        }
        catch (Exception)
        {
            _menueLesen = false;
        }
    }

    private (int Ordinal, string Name)? ErkenneAuto(IReadOnlyList<OcrLine> zeilen) =>
        CarGridReader.Erkenne(zeilen, _advisor);

    private void MenueGelesen(Rectangle? rahmen, byte[]? abdruck, (int Ordinal, string Name)? auto,
                              string? gelesen, bool neu)
    {
        if (rahmen is null)
        {
            // Zweimal nacheinander kein Rahmen: das Menue ist zu. Einmal kann ein
            // Uebergang sein (die Kachel blinkt beim Blaettern).
            if (_menueAuto is not null && ++_menueFehlt >= 2) { MenueVerlassen(); }
            return;
        }
        _menueFehlt = 0;
        _menueRahmen = rahmen.Value;
        _menueAbdruck = abdruck;
        if (!neu) { return; }
        LogMenue(gelesen, auto);
        if (auto is { } a)
        {
            _menueAuto = a;
            Notes.NoteModel(a.Ordinal, a.Name, "menu");
            ZeigeNotiz(a.Ordinal, a.Name, null, "menu", a.Name);
        }
        else if (_menueAuto is not null)
        {
            // Ein Rahmen, dessen Auto sich nicht bestimmen laesst: die Notiz des
            // VORIGEN Autos darf dann nicht stehen bleiben -- man laese sie am falschen.
            MenueVerlassen();
        }
    }

    private void MenueVerlassen()
    {
        _menueAuto = null;
        _menueFehlt = 0;
        _menueRahmen = default;
        _menueAbdruck = null;
        if (_carNote.Visible) { _carNote.Hide(); }
        _carNote.SetNote(string.Empty, string.Empty);
        // Beim naechsten Paket gilt wieder das gefahrene Auto -- fuer den Reiter. Ein
        // Overlay zeigt es erst nach dem Losfahren wieder (siehe _nachAuswahl).
        _carKey = string.Empty;
        _nachAuswahl = true;
        CurrentCar = null;
        CurrentCarChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void LogMenue(string? gelesen, (int Ordinal, string Name)? auto)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "forza-overlay");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "cars.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} menu read \"{gelesen}\" -> "
                + (auto is { } a ? $"{a.Name} (car {a.Ordinal})" : "no car")
                + Environment.NewLine);
        }
        catch (Exception)
        {
            // Diagnostics must never be the thing that breaks a read.
        }
    }

    /// <summary>
    /// Die Referenzrunde nachfuehren: in den ersten 400 m je zehn Meter neu suchen,
    /// danach steht sie (siehe UpdateDelta, warum).
    /// </summary>
    private void ReferenzNachfuehren(ForzaPacket packet)
    {
        var laenge = (int)MathF.Round(_recorder.CurrentMetres / 10f);
        var nochOffen = _reference is null || _recorder.CurrentMetres < 400f;
        if (nochOffen && laenge != _referenceForLength)
        {
            var vorher = _reference;
            _referenceForLength = laenge;
            _reference = FindReference(packet);
            // Andere Referenz: die gemerkte Stelle gilt nicht mehr.
            if (!ReferenceEquals(vorher, _reference)) { _hint = 0; }
        }
    }

    private void UpdateDelta(ForzaPacket packet)
    {
        // Bei abgeschaltetem Streifen NUR DEN STREIFEN verstecken (HideStrip). Bis zum
        // 2026-09-28 stand unten HideHud(), das auch die Live-Karte versteckt -- zehnmal
        // je Sekunde, und UpdateLiveMap zeigte sie gleich wieder: sie blinkte das ganze
        // Rennen, sobald der Streifen aus und die Karte an war.
        // Gefuettert wird der Recorder in OnTelemetryRaw, bei vollem Takt -- hier
        // wird nur gelesen, was dort schon steht.
        //
        // WAEHREND DER VORSCHAU NICHT. Das Spiel sendet auch im Menue Pakete, und
        // "kein Rennen" blendete den Streifen beim naechsten davon wieder aus.
        if (_hudPreview) { return; }
        if (!_settings.DeltaHud) { HideStrip(); return; }

        var rennen = packet.Get("IsRaceOn") >= 0.5;
        if (!rennen) { NachDemRennen(); return; }

        EnsureHud();
        if (_hud is null) { return; }
        _hud.NurNotiz(false);

        // Der Countdown der Kollisionsfreiheit. `CurrentRaceTime` laeuft ab dem "GO",
        // die Dauer steht in der Einstellung -- die Telemetrie kennt sie nicht.
        var rennzeit = (float)packet.Get("CurrentRaceTime");
        // NUR IM RENNEN.
        //
        // `IsRaceOn` taugt dafuer nicht: in Horizon steht das Feld auch beim freien
        // Fahren auf 1, und `CurrentRaceTime` faengt bei jedem Laden wieder bei null
        // an. Die Folge war ein Geister-Countdown nach JEDEM Ladevorgang -- vom
        // Nutzer am 2026-09-12 gemeldet, und genau so nachvollziehbar.
        //
        // `RacePosition` ist die Platzierung im laufenden Rennen. Gemessen am selben
        // Abend: unmittelbar nach einem beendeten Rennen stand dort die 3, und das
        // Spiel zeigte den dritten Platz -- das Feld beschreibt also wirklich das
        // Rennen und nicht die Welt. Es ist damit der beste Anhaltspunkt, den die
        // Telemetrie hergibt.
        //
        // Was dabei NICHT bewiesen ist: ob das Feld im freien Fahren auf 0 faellt.
        // Darum wird der gesehene Wert mitprotokolliert (siehe LogDelta): stimmt die
        // Annahme nicht, steht es dort, statt dass es wieder jemandem auffallen muss.
        var platz = (int)packet.Get("RacePosition");
        var imRennen = platz >= 1;
        var geist = imRennen
            ? (float)Math.Max(_settings.GhostSeconds - rennzeit,
                              -(_settings.PopInSeconds + 1))
            // Ausserhalb eines Rennens: hinter der Einblendphase, also zeichnet der
            // Streifen nichts. Keine Sonderfaelle im Zeichner.
            : -(float)(_settings.PopInSeconds + 1);

        float? delta = null;
        float? zweites = null;
        var zweiteBeschriftung = string.Empty;
        var ziel = string.Empty;
        var beschriftung = DeltaModeLabel(DeltaMode);
        // Ob die Laenge dieser Strecke ueberhaupt bekannt ist -- erst eine fertige
        // Runde beantwortet das, und davon haengt ab, ob umgerechnet werden kann.
        var streckeBekannt = _recorder.LastLapMetres > 0;

        // KEIN DELTA OHNE LAUFENDE UHR.
        //
        // Das war die Ursache des "es zaehlt einfach hoch". Gemessen am 2026-09-13
        // im eigenen Protokoll, 395 Zeilen dieser Form:
        //
        //     m=1977 t=0,00 delta=-51,907 platz=0 ref=52,103s/1890m
        //
        // Die eigene Uhr steht auf NULL, waehrend der Wegzaehler auf fast zwei
        // Kilometer klettert. `CurrentSeconds` ist `CurrentLap`, und dieses Feld
        // laeuft im freien Fahren nicht. Das Delta war damit rechnerisch
        // `0 - Referenzzeit an meinem Ort` -- also die Referenzzeit selbst, mit
        // Minuszeichen, wachsend je weiter man faehrt. Es SAH aus wie ein Delta.
        //
        // `IsRaceOn` taugt als Riegel nicht: in Horizon steht es auch beim freien
        // Fahren auf 1 (siehe unten). Die laufende Uhr dagegen ist genau das, was
        // eine gewertete Fahrt ausmacht -- und sie ist unabhaengig davon, ob die
        // Fahrt ein Rennen, ein Rivalenlauf oder eine Zeitjagd in der offenen Welt
        // ist. Darum haengt der Riegel an ihr und nicht an der Platzierung.
        var uhrLaeuft = _recorder.CurrentSeconds > 0f;

        if (_recorder.HasLapUnderway && uhrLaeuft)
        {
            // Die Referenz wird je Strecke EINMAL gesucht, nicht je Paket: die Suche
            // laeuft ueber den ganzen Bestand, und bei 60 Paketen je Sekunde waere das
            // sechzigmal dieselbe Antwort.
            // DIE REFERENZ WIRD EINMAL GEWAEHLT UND DANN NICHT MEHR GEWECHSELT.
            //
            // Bis zum 2026-09-13 wurde sie alle zehn Meter neu gesucht -- mit einem
            // Verfahren, das vom aktuellen Standort abhaengt. Am Ziel rollte die
            // eigene Fahrt 79 m weiter aus als die Bestzeit; die fiel damit aus dem
            // "liegt auf meiner Spur"-Filter, und in der ALLERLETZTEN Zeile sprang
            // die Referenz auf eine 1,070 s langsamere Fahrt. Genau dieser Wert
            // bleibt als Endergebnis stehen: der Streifen zeigte -0,4 s, wo +0,7
            // richtig gewesen waere.
            //
            // Die ersten 400 m darf sie sich noch aendern -- so lange liegen mehrere
            // Strecken derselben Startlinie noch uebereinander und der gefahrene Weg
            // hat sie noch nicht getrennt. Danach steht sie.
            ReferenzNachfuehren(packet);
            // WO in der Referenzrunde stehe ich gerade? Am ORT gemessen.
            var bestzeit = ZeitAuf(_reference, packet, ref _hint, ref _abseits,
                                   streckeBekannt, out var hinterDemEnde);
            if (bestzeit is not null)
            {
                delta = _recorder.CurrentSeconds - bestzeit.Value;
                _letztesDelta = delta;
            }
            else if (hinterDemEnde && _letztesDelta is not null)
            {
                // DAS ERGEBNIS STEHEN LASSEN.
                //
                // Hinter dem Ziel der Referenz ist nichts mehr zu vergleichen -- aber
                // der zuletzt gueltige Wert ist genau der, der zaehlt: der Vorsprung
                // im Ziel. Ihn auszublenden waere so falsch wie ihn weiterlaufen zu
                // lassen; er bleibt stehen und wird als endgueltig beschriftet.
                delta = _letztesDelta;
            }
            // WELCHES AUTO DIE REFERENZ IST -- wo es ein anderes sein kann (Nutzerwunsch
            // vom 2026-09-26). Gegen "dieselbe Klasse" zu fahren heisst gegen das beste
            // Auto der Klasse; welches das ist, sagte der Streifen bis dahin nicht.
            // Seit 2026-09-27 auch seine KLASSE, wenn sie nicht die eigene ist.
            if (_reference is { } referenz
                && DeltaMode is DeltaReference.SameClass or DeltaReference.Anything)
            {
                beschriftung = MitReferenzAuto(beschriftung, referenz, packet);
            }
            // DIE GEWAEHLTE STUFE STEHT IMMER VORN (Nutzerwunsch 2026-09-27). Bis dahin
            // ersetzten die Zustaende unten die Beschriftung ganz -- "off the reference
            // line · Nissan Skyline" liess nicht erkennen, dass "personal best, any car"
            // gewaehlt war, und eine R-Klasse-Zeit gegen ein A-Klasse-Auto sah aus wie
            // ein Fehler.
            if (_reference is null)
            {
                // Keine Referenz heisst KEINE ZAHL. Vorher lief hier eine Zahl mit,
                // die aus einer fremden Strecke stammte und einfach hochzaehlte --
                // das ist schlimmer als eine leere Anzeige, weil es wie eine Auskunft
                // aussieht.
                beschriftung += (_recorder.StandingStart
                    ? " -- no standing-start lap here yet, recording"
                    : " -- no flying lap here yet, recording")
                    + $" ({_recorder.CurrentMetres:0} m)";
            }
            else if (hinterDemEnde)
            {
                beschriftung += _letztesDelta is null ? " -- reference lap ended here" : " -- final";
            }
            else if (_abseits)
            {
                // Abseits der Linie gibt es keine ehrliche Zahl: der naechste Punkt
                // der Bestzeit liegt dann irgendwo, und "irgendwo" ist kein Vergleich.
                beschriftung += " -- off the reference line";
            }
            else if (_recorder.StandingStart)
            {
                beschriftung += " (standing start)";
            }
            ziel = ZielZeile(packet);

            if (DualDelta)
            {
                // Die zweite Zahl beantwortet eine ANDERE Frage: nicht "besser als
                // ich selbst mit diesem Auto", sondern "besser als mein Bestes in
                // dieser Leistungsklasse". Beides zugleich zu sehen ist der Sinn.
                var klassenBeste = FindReference(packet, DeltaReference.SameClass);
                var klassenZeit = ZeitAuf(klassenBeste, packet, ref _hintZwei,
                                          ref _abseitsZwei, streckeBekannt);
                if (klassenZeit is not null)
                {
                    zweites = _recorder.CurrentSeconds - klassenZeit.Value;
                }
                zweiteBeschriftung = DeltaModeLabel(DeltaReference.SameClass);
                if (klassenBeste is null) { zweiteBeschriftung += " -- none yet"; }
                else { zweiteBeschriftung = MitReferenzAuto(zweiteBeschriftung, klassenBeste, packet); }
            }
        }
        else if (_recorder.InFreeRoam)
        {
            // FREIE WELT, UHR NOCH NICHT GESTARTET.
            //
            // Hier stand bis zum 2026-09-14 "free roam -- no timed run". Das war
            // ehrlich und es war eine Sackgasse: es sagte, dass nichts gemessen
            // wird, aber nicht, wie sich das aendern laesst. Seit es eine eigene Uhr
            // gibt, ist die Antwort kurz -- ueber eine Startlinie fahren.
            var linien = _recorder.Lines.Anchors.Count;
            beschriftung = linien == 0
                ? "free roam -- no start lines known yet; drive a route once, or set one"
                : $"free roam -- cross one of your {linien} start lines to start the clock";
            _letztesDelta = null;
        }
        else if (_recorder.HasLapUnderway)
        {
            // Die Uhr des Spiels steht, und es ist keine freie Fahrt -- also etwas,
            // das noch niemand gesehen hat. Keine Zahl, und ausdruecklich gesagt,
            // warum: eine leere Anzeige ohne Grund sieht aus wie ein Fehler.
            beschriftung = "no running clock -- nothing is being timed";
            _letztesDelta = null;
        }

        if (_settings.HudInputs) { PushInputs(packet); }

        LogDelta(delta, beschriftung, geist, platz, (int)packet.Get("LapNumber"), packet);

        try
        {
            _hud.Update(delta, beschriftung, geist, zweites, zweiteBeschriftung, ziel);
        }
        catch (Exception)
        {
            // Ein Anzeigefehler darf das Rennen nicht stoeren.
        }
    }

    private int _hint;
    private int _hintZwei;
    private LapSample? _referenceSample;
    private float? _referenceSeconds;
    private float? _letztesDelta;
    private bool _abseits;
    private bool _abseitsZwei;

    /// <summary>
    /// Die Zeit der Referenzrunde dort, wo ich gerade bin.
    /// </summary>
    /// <remarks>
    /// Am Ort, wo die Referenz Koordinaten hat; nach Metern nur bei Runden aus der
    /// Zeit davor. Die Schwelle fuer "abseits" hat eine Hysterese -- 60 m hinein,
    /// 40 m heraus --, sonst flackert die Anzeige an jedem Randstein.
    /// </remarks>
    private float? ZeitAuf(RecordedLap? referenz, ForzaPacket packet, ref int hint,
                           ref bool abseits, bool streckeBekannt) =>
        ZeitAuf(referenz, packet, ref hint, ref abseits, streckeBekannt, out _);

    private float? ZeitAuf(RecordedLap? referenz, ForzaPacket packet, ref int hint,
                           ref bool abseits, bool streckeBekannt, out bool amEnde)
    {
        amEnde = false;
        if (referenz is null) { abseits = false; return null; }

        if (referenz.HasPositions)
        {
            amEnde = false;
            var zeit = referenz.SecondsAtPosition(
                (float)packet.Get("PositionX"),
                (float)packet.Get("PositionY"),
                (float)packet.Get("PositionZ"),
                ref hint, out var entfernung, out var dort, out var hinterDemZiel);
            abseits = entfernung > (abseits ? 40f : 60f);
            if (!abseits) { _referenceSample = dort; }
            if (!abseits && ReferenceEquals(referenz, _reference)) { _referenceSeconds = zeit; }
            // Am Ende der Referenz gibt es kein neues Delta mehr -- der letzte
            // gueltige Wert ist das Ergebnis.
            amEnde = !abseits && hinterDemZiel;
            return abseits || hinterDemZiel ? null : zeit;
        }

        // Runde ohne Koordinaten: die alte Naeherung ueber die Strecke, auf den
        // Anteil der Runde umgerechnet.
        abseits = false;
        return referenz.SecondsAt(streckeBekannt && _recorder.LastLapMetres > 0
            ? _recorder.CurrentMetres * (referenz.LengthMetres / _recorder.LastLapMetres)
            : _recorder.CurrentMetres);
    }

    private RecordedLap? FindReference(ForzaPacket packet) => FindReference(packet, DeltaMode);

    private RecordedLap? FindReference(ForzaPacket packet, DeltaReference mode)
    {
        // Die gefahrene Runde ist noch nicht fertig, ihre Laenge also unbekannt. Als
        // Schluessel dient darum die Laenge der zuletzt auf dieser Strecke
        // aufgezeichneten Runde: gesucht wird unter allen Runden, deren Laenge zu dem
        // passt, was hier gerade entsteht -- und das entscheidet sich erst, wenn genug
        // Meter gefahren sind. Bis dahin: die laengste passende.
        var muster = new RecordedLap
        {
            CarOrdinal = _ordinal ?? 0,
            PerformanceIndex = _pi ?? 0,
            Drivetrain = _drivetrain ?? 0,
            Cylinders = _cylinders ?? 0,
            MaxRpm = (int)MathF.Round((float)packet.Get("EngineMaxRpm")),
            IdleRpm = (int)MathF.Round((float)packet.Get("EngineIdleRpm")),
            // Der Startpunkt der LAUFENDEN Runde -- damit steht die Strecke schon
            // fest, bevor die Runde zu Ende ist.
            StartX = _recorder.StartX,
            StartZ = _recorder.StartZ,
            StandingStart = _recorder.StandingStart,
            FreeRoam = _recorder.InFreeRoam,
            LengthMetres = _recorder.LastLapMetres > 0
                ? _recorder.LastLapMetres
                : _recorder.CurrentMetres,
        };
        var klasse = (int)packet.Get("CarClass");
        var streckeBekannt = _recorder.LastLapMetres > 0;

        // ERST SAMMELN, DANN WAEHLEN.
        //
        // GESEHEN am 2026-09-13: nach einem Abbruch bei 4.057 m wurde genau dieser
        // Abbruch zur Referenz, obwohl eine vollstaendige Fahrt ueber 6.459 m
        // derselben Strecke im Bestand lag -- die Laengenschaetzung haengt an der
        // zuletzt beendeten Fahrt, und die war eben abgebrochen. Der naechste
        // Versuch waere nach vier Kilometern "off the reference line" gelaufen.
        //
        // Unter Fahrten von derselben Startlinie ist die LAENGSTE die
        // vollstaendigste. Kuerzere sind Abbrueche oder andere Strecken; beide
        // taugen nicht als Massstab, solange es eine laengere gibt.
        var passende = new List<RecordedLap>();
        foreach (var kandidat in _laps.Laps)
        {
            // Dieselbe Strecke?
            //
            // Der Startpunkt entscheidet, wo beide Runden einen haben -- er ist die
            // Kennung, die die Telemetrie sonst nicht liefert. Nur wenn einer fehlt
            // (Runden aus der Zeit davor), bleibt die Laenge, und die taugt nur, wenn
            // in dieser Sitzung schon eine Runde fertig geworden ist. Ganz ohne
            // beides gilt die alte, schwache Regel: die Runde muss wenigstens so
            // lang sein wie das, was gerade gefahren wurde.
            // Stehend gegen fliegend zu halten ergibt ein Delta von mehreren
            // Sekunden, das nur den Start beschreibt und nichts sonst.
            if (kandidat.StandingStart != muster.StandingStart) { continue; }
            // Eine selbst gestoppte Fahrt aus der freien Welt ist kein Massstab fuer
            // ein gewertetes Rennen und umgekehrt: dort gibt es Streckengrenzen,
            // hier nicht. Siehe RecordedLap.FreeRoam.
            if (kandidat.FreeRoam != muster.FreeRoam) { continue; }

            if (RecordedLap.StartsTogether(kandidat, muster))
            {
                // KEINE Laengenhuerde mehr an dieser Stelle.
                //
                // Hier stand: "nur, wenn die Laenge zur zuletzt beendeten Fahrt
                // passt". Das war der Fehler, der am 2026-09-13 auch nach der
                // ersten Reparatur blieb -- die zuletzt beendete Fahrt war ein
                // Abbruch ueber 1.189 m, und damit flog die vollstaendige Fahrt
                // ueber 6.474 m schon HIER raus, bevor die Auswahl sie ueberhaupt
                // sah. Der Streifen meldete mitten im Lauf "off the reference line".
                //
                // Wer dieselbe Startlinie hat, kommt jetzt in die Auswahl; welche
                // Fahrt die richtige ist, entscheidet weiter unten der gefahrene
                // WEG -- also das, worauf es ankommt.
            }
            else if (kandidat.HasStart && muster.HasStart)
            {
                // Beide kennen ihren Start, und er ist ein anderer: fremde Strecke.
                continue;
            }
            else if (streckeBekannt)
            {
                if (!RecordedLap.SameCourse(kandidat.LengthMetres,
                                            _recorder.LastLapMetres))
                {
                    continue;
                }
            }
            else if (kandidat.LengthMetres < _recorder.CurrentMetres)
            {
                continue;
            }
            var passt = mode switch
            {
                DeltaReference.SameCar => kandidat.CarOrdinal == (_ordinal ?? 0),
                DeltaReference.SameClass => kandidat.CarClass == klasse,
                DeltaReference.SameCarSameClass =>
                    kandidat.CarOrdinal == (_ordinal ?? 0) && kandidat.CarClass == klasse,
                DeltaReference.Anything => true,
                _ => kandidat.TuneKey == muster.TuneKey,
            };
            if (!passt) { continue; }
            passende.Add(kandidat);
        }

        if (passende.Count == 0) { return null; }
        if (passende.Count == 1) { return passende[0]; }

        // WELCHE DIESER FAHRTEN FAHRE ICH GERADE?
        //
        // Mehrere Strecken koennen an derselben Linie beginnen -- ein Rivals-Kurs
        // und ein Rennen etwa --, und Abbrueche sehen am Anfang aus wie die volle
        // Fahrt. Die Laenge kann das nicht entscheiden, solange die laufende Fahrt
        // noch nicht zu Ende ist.
        //
        // Der gefahrene WEG kann es: nach ein paar hundert Metern liegt meine Spur
        // auf genau einer der gespeicherten. Gemessen wird der Abstand zur Linie
        // jeder Fahrt an meiner jetzigen Stelle -- dieselbe Rechnung, die sonst
        // "off the reference line" meldet, hier als Auswahl statt als Urteil.
        var wo = new
        {
            X = (float)packet.Get("PositionX"),
            Y = (float)packet.Get("PositionY"),
            Z = (float)packet.Get("PositionZ"),
        };
        if (_recorder.CurrentMetres > 150f)
        {
            // Alle, auf deren Weg ich wirklich bin -- nicht nur die naechste.
            //
            // Abbrueche derselben Strecke liegen GENAUSO auf meiner Spur wie die
            // vollstaendige Fahrt; der Abstand trennt sie nicht und darf es auch
            // nicht. Wer hier die "naechste" nimmt, waehlt faktisch zufaellig, und
            // die Referenz wechselt mitten im Lauf -- am 2026-09-13 gesehen:
            // 1189 m, dann 1596 m, dann 2992 m, dann 4057 m, und die angezeigte
            // Zahl sprang bei jedem Wechsel um bis zu einer halben Sekunde. Das
            // beschreibt mein Verfahren, nicht die Fahrt.
            var aufMeinerSpur = new List<RecordedLap>();
            foreach (var kandidat in passende)
            {
                if (!kandidat.HasPositions) { continue; }
                var hinweis = 0;
                kandidat.SecondsAtPosition(wo.X, wo.Y, wo.Z, ref hinweis, out var weg);
                if (weg < 60f) { aufMeinerSpur.Add(kandidat); }
            }
            if (aufMeinerSpur.Count > 0) { passende = aufMeinerSpur; }
        }

        // DAS ZIEL BESTIMMEN, DANN DIE SCHNELLSTE FAHRT DORTHIN.
        //
        // Keine Laengenvergleiche mehr. Die Route der weitesten Fahrt ist das
        // Koordinatensystem; darauf wird jede andere Fahrt abgebildet, das Ziel
        // ergibt sich aus der Haeufung der Endpunkte, und verglichen werden die
        // Zeiten AN DIESEM EINEN ORT. Damit spielt es keine Rolle mehr, wie lange
        // das Spiel nach dem Ziel noch "Rennen laeuft" gemeldet hat oder wie eng
        // jemand die Kurven genommen hat.
        var route = passende[0];
        foreach (var kandidat in passende)
        {
            if (kandidat.RouteLength > route.RouteLength) { route = kandidat; }
        }

        var ziel = RecordedLap.FinishArc(route, passende);

        RecordedLap? beste = null;
        var besteZeit = float.MaxValue;
        foreach (var kandidat in passende)
        {
            var zeit = ReferenceEquals(kandidat, route)
                ? (float?)route.SecondsAtArcOf(route, ziel)
                : kandidat.SecondsAtArcOf(route, ziel);
            // Wer das Ziel nicht erreicht, ist ein Abbruch und kein Massstab.
            if (zeit is null) { continue; }
            if (zeit.Value < besteZeit) { besteZeit = zeit.Value; beste = kandidat; }
        }

        // Erreicht KEINE das Ziel (etwa, weil nur Abbrueche vorliegen), dann die
        // Fahrt, die am weitesten kam -- lieber ein kurzer Massstab als keiner.
        return beste ?? route;
    }

    private Rectangle _area;

    private (Rectangle Right, Rectangle Left) PanelRects(Rectangle screen)
    {
        var side = (int)(screen.Width * _settings.SideWidthFraction);
        return (new Rectangle(screen.Right - side, screen.Top, side, screen.Height),
                new Rectangle(screen.Left, screen.Top, (int)(side * 0.86), screen.Height));
    }

    /// <summary>
    /// Follow the game when it is on another monitor, in a window, or at another
    /// resolution than when the overlay started.
    /// </summary>
    /// <remarks>
    /// The overlay usually starts before the game, so the first answer is the
    /// primary screen. Asked on the regular tick; cheap, GameArea caches for 2 s.
    /// </remarks>
    private void FollowGameArea()
    {
        var jetzt = GameArea.Find(_settings.ForzaProcess);
        if (jetzt == _area || jetzt.Width < 640 || jetzt.Height < 360) { return; }
        _area = jetzt;
        OverlayAusgabe.Flaeche = jetzt;
        var (rechts, links) = PanelRects(jetzt);
        _right.Reposition(rechts);
        _left.Reposition(links);
        _shapes.SetArea(jetzt);
        _carNote.SetArea(jetzt);
        _meldung.SetArea(jetzt);
        _feier.SetArea(jetzt);
        _liveMap.SetArea(jetzt);
        _reifen.SetArea(jetzt);
        // Der Streifen rechnet seine Einheit einmal aus der Flaeche; neu anlegen.
        if (_hud is not null && !_hud.IsDisposed)
        {
            var sichtbar = _hud.Visible;
            _hud.Dispose();
            _hud = null;
            if (sichtbar) { EnsureHud(); }
        }
        SetStatus($"game area {jetzt.Width}x{jetzt.Height} at {jetzt.X},{jetzt.Y}");
    }

    private void EnsureHud()
    {
        if (_hud is not null && !_hud.IsDisposed)
        {
            if (!_hud.Visible) { _hud.Show(); _hud.TopMost = true; }
            return;
        }
        var schirm = _area.IsEmpty ? GameArea.Find(_settings.ForzaProcess) : _area;
        _hud = new DeltaHud(schirm, _settings);
        _hud.Show();
        _hud.TopMost = true;
    }

    /// <summary>
    /// Den Streifen mit Beispielwerten zeigen, damit eine Einstellung sichtbar wird.
    /// </summary>
    /// <remarks>
    /// Die Zahlen sind erfunden und als solche beschriftet -- aber Lage, Groesse und
    /// Farbe sind genau die, die im Rennen gelten.
    /// </remarks>
    public void ShowHudPreview()
    {
        try
        {
            _hudPreview = true;
            EnsureHud();
            _hud?.Update(-0.734f, "preview -- same car, this course", 12.4f,
                         0.286f, "preview -- same PI class",
                         _settings.HudTarget ? "preview -- to beat: 1:24.012 -- website best, this car" : string.Empty);
            _hud?.Note("preview: lap stored: 83.706 s, 5949 m", 20);

            // Umriss und Notiz gehoeren dazu: genau die richtet man in diesem Reiter
            // ein, und bis zum 2026-09-25 zeigte die Vorschau sie gar nicht.
            _shapes.SetPreview(true);
            _shapes.SetCourses(VorschauUmrisse());
            if (!_shapes.Visible) { _shapes.Show(); }
            _shapes.TopMost = true;
            _carNote.SetPreview(true);
            // Die Live-Karte mit einer echten Strecke und dem Auto nach einem Drittel.
            var probe = VorschauUmrisse().Select(u => u.Item2).FirstOrDefault(u => u is { Ordered: true, IsEmpty: false });
            if (probe is not null)
            {
                _liveMap.SetPreview(true);
                _liveMap.SetSample(LiveMapHud.SampleAus(probe), probe.Points.Count / 3);
                if (!_liveMap.Visible) { _liveMap.Show(); }
                _liveMap.TopMost = true;
            }
            if (_settings.HudTyres)
            {
                _reifen.Setze(TyreHud.Beispiel());
                if (!_reifen.Visible) { _reifen.Show(); }
                _reifen.TopMost = true;
            }
            _carNote.SetNote("Porsche 911 GT3 RS '19  \u00b7  PI 900  \u00b7  513 hp",
                             "preview: understeers from turn 3, tyres go off after 4 laps");
            if (!_carNote.Visible) { _carNote.Show(); }
            _carNote.TopMost = true;
        }
        catch (Exception)
        {
            // Eine Vorschau darf nie die App mitnehmen.
        }
    }

    /// <summary>Drei Strecken fuer die Vorschau: aus dem eigenen Bestand, sonst Rivalen-Karten.</summary>
    private List<(string, CourseShape.Outline?)> VorschauUmrisse()
    {
        var raus = new List<(string, CourseShape.Outline?)>();
        try
        {
            var wurzel = LapArchive.Root;
            if (Directory.Exists(wurzel))
            {
                foreach (var ordner in Directory.EnumerateDirectories(wurzel))
                {
                    var kurs = Path.GetFileName(ordner);
                    var u = CourseShape.For(kurs, _settings.ShapeSourceChoice, wurzel);
                    if (u is null || u.IsEmpty) { continue; }
                    var name = CourseShape.KursName(wurzel, kurs);
                    raus.Add((string.IsNullOrEmpty(name) ? kurs : name, u));
                    if (raus.Count >= 3) { break; }
                }
            }
        }
        catch (Exception) { }
        foreach (var route in new[] { "Daikoku Circuit", "Coastline Sprint", "Festival Sprint" })
        {
            if (raus.Count >= 3) { break; }
            raus.Add((route, CourseShape.ForRoute(route, _settings.ShapeSourceChoice)));
        }
        return raus;
    }

    /// <summary>
    /// Nach einer Aenderung an Lage, Groesse oder Farbe sofort neu zeichnen.
    /// </summary>
    /// <remarks>
    /// Ohne das haette eine Farbe erst im naechsten Paket gegriffen -- und ausserhalb
    /// eines Rennens kommt keines. Wer im Programm eine Farbe waehlt, will sie sehen,
    /// waehrend er sie waehlt.
    /// </remarks>
    public void RefreshHud()
    {
        try
        {
            if (_shapes.Visible) { _shapes.Render(); }
            if (_carNote.Visible) { _carNote.Render(); }
            if (_liveMap.Visible) { _liveMap.Render(); }
            if (_hudPreview)
            {
                if (_settings.HudTyres)
                {
                    _reifen.Setze(TyreHud.Beispiel());
                    if (!_reifen.Visible) { _reifen.Show(); _reifen.TopMost = true; }
                }
                else if (_reifen.Visible) { _reifen.Hide(); }
            }
            else if (!_settings.HudTyres && _reifen.Visible) { _reifen.Hide(); }
            else if (_reifen.Visible) { _reifen.Render(); }
            if (!_settings.DeltaHud) { HideStrip(); return; }
            if (_hud is not null && !_hud.IsDisposed) { _hud.Invalidate(); }
        }
        catch (Exception)
        {
            // Eine Einstellung darf nie die App mitnehmen.
        }
    }

    /// <summary>Die Vorschau wieder wegnehmen.</summary>
    public void HideHudPreview()
    {
        _hudPreview = false;
        _shapes.SetPreview(false);
        _carNote.SetPreview(false);
        _liveMap.SetPreview(false);
        _liveMap.SetReference(null);
        _liveMap.ClearTrail();
        if (_reifen.Visible) { _reifen.Hide(); }
        HideHud();
        HideShapes();
        if (_carNote.Visible) { _carNote.Hide(); }
        _carNote.SetNote(string.Empty, string.Empty);
        // Beim naechsten Paket die echte Notiz neu bestimmen, auch fuer dasselbe Auto.
        _carKey = string.Empty;
    }

    /// <summary>
    /// Kein Rennen mehr: den Streifen wegnehmen -- aber eine frische Meldung ihre
    /// acht Sekunden stehen lassen, allein.
    /// </summary>
    private void NachDemRennen()
    {
        if (_hud is not null && !_hud.IsDisposed && _hud.NoteActive)
        {
            _hud.NurNotiz(true);
            if (!_hud.Visible) { _hud.Show(); _hud.TopMost = true; }
            if (_liveMap.Visible) { _liveMap.Hide(); }
            return;
        }
        HideHud();
    }

    /// <summary>Streifen UND Live-Karte weg: Rennen vorbei, Spiel nicht vorne, Vorschau zu.</summary>
    private void HideHud()
    {
        if (_hudPreview) { return; }
        HideStrip();
        if (_liveMap.Visible) { _liveMap.Hide(); }
    }

    /// <summary>Nur den Delta-Streifen weg -- die Live-Karte hat ihre eigenen Regeln (UpdateLiveMap).</summary>
    private void HideStrip()
    {
        if (_hudPreview) { return; }
        if (_hud is not null && !_hud.IsDisposed && _hud.Visible) { _hud.Hide(); }
    }

    private bool TelemetryFresh => (DateTime.UtcNow - _telemetryAt).TotalSeconds < 15;

    // ------------------------------------------------------------------ //
    // triggers
    // ------------------------------------------------------------------ //

    /// <remarks>
    /// Polled, not hooked: a keyboard hook needs a message pump of its own and a
    /// gamepad hook needs a driver. Polling at 60 ms costs nothing measurable and
    /// works while the GAME has focus, which a WinForms key binding does not.
    /// </remarks>
    private void PollTriggers()
    {
        if (_disposed) { return; }
        if (!GameIsUp())
        {
            // Gedrueckt Gehaltenes vergessen, sonst loest die Taste in dem Moment aus,
            // in dem das Spiel wieder da ist -- ohne dass jemand neu gedrueckt hat.
            _keysDown.Clear();
            _padsDown.Clear();
            return;
        }
        Key(_settings.HotkeyRight, "right");
        Key(_settings.HotkeyLeft, "left");
        Key(_settings.HotkeyScore, "score");
        Key(_settings.HotkeyPin, "pin");
        Key(_settings.HotkeyScrollDown, "scroll_down");
        Key(_settings.HotkeyScrollUp, "scroll_up");
        Key(_settings.HotkeyDeltaMode, "delta_mode");
        Key(_settings.HotkeyFreeRoamLine, "free_line");

        ushort buttons = 0;
        for (uint pad = 0; pad < 4; pad++)
        {
            if (XInput.XInputGetState(pad, out var state) == XInput.Success)
            {
                buttons = state.Gamepad.Buttons;
                break;
            }
        }
        Pad(Mask(_settings.GamepadRight), buttons, "right");
        Pad(Mask(_settings.GamepadLeft), buttons, "left");
    }

    private static ushort Mask(IEnumerable<string>? names)
    {
        ushort mask = 0;
        foreach (var name in names ?? Enumerable.Empty<string>())
        {
            if (PadButtons.TryGetValue(name.Trim(), out var bit))
            {
                mask |= bit;
            }
        }
        return mask;
    }

    private void Key(int vk, string action)
    {
        if (vk == 0)
        {
            return;
        }
        if ((GetAsyncKeyState(vk) & 0x8000) != 0)
        {
            if (_keysDown.Add(vk))
            {
                Fire(action);
            }
        }
        else
        {
            _keysDown.Remove(vk);
        }
    }

    private void Pad(ushort mask, ushort buttons, string action)
    {
        if (mask == 0)
        {
            return;
        }
        if ((buttons & mask) != 0)
        {
            if (_padsDown.Add(mask))
            {
                Fire(action);
            }
        }
        else
        {
            _padsDown.Remove(mask);
        }
    }

    private void Fire(string action)
    {
        Act(action);
        switch (action)
        {
            case "pin":
                var up = _right.Up ? "right" : _left.Up ? "left" : null;
                if (up is null)
                {
                    return;
                }
                _pinned = _pinned == up ? null : up;
                Show(up == "right" ? _right : _left,
                     _pinned is null ? _settings.ShowSeconds : 86_400);
                break;
            case "score":
                _settings.ScoreMode = _settings.ShowsPoints ? "time" : "points";
                _settings.Save();
                _lastAdviceKey = null;
                _lastCarKey = null;
                if (_left.Up)
                {
                    RenderCar();
                    Show(_left, null);
                }
                else
                {
                    if (_screen is { IsOffer: true })
                    {
                        RenderAdvice(_screen);
                    }
                    Show(_right, null);
                }
                SetStatus($"showing the {_settings.ScoreMode} order");
                break;
            case "delta_mode":
                CycleDeltaMode();
                break;
            case "free_line":
                SetzeFreieLinie();
                break;
            case "scroll_down":
            case "scroll_up":
                // Only the panel that is up, and only while one is: the rest of the
                // time these keys belong to the game.
                var reading = _right.Up ? _right : _left.Up ? _left : null;
                if (reading is null)
                {
                    return;
                }
                if (reading.ScrollPage(action == "scroll_down" ? 1 : -1))
                {
                    // Someone paging down a list has not finished with it, so the
                    // thirty seconds start again from this press.
                    Show(reading, _pinned is null ? _settings.ShowSeconds : 86_400);
                }
                break;
            case "left":
                // Same button away again: it is the one people reach for, and
                // hunting for a second key with a panel over the screen is worse.
                if (_left.Up)
                {
                    Hide(_left);
                    break;
                }
                _lastCarKey = null;
                RenderCar();
                _left.Rewind();
                Show(_left, null);
                break;
            case "right":
                if (_right.Up)
                {
                    // Waved away for THIS screen: the automatic show is told to keep
                    // its hands off until the screen itself changes.
                    _waved = _screen?.Key;
                    Hide(_right);
                    HideShapes();
                    break;
                }
                // Answer at once from the last read so the press feels immediate;
                // the fresh read replaces it a second later.
                if (_screen is { IsOffer: true })
                {
                    RenderAdvice(_screen);
                }
                _right.Rewind();
                Show(_right, null);
                StartRead(force: true);
                break;
        }
    }

    // ------------------------------------------------------------------ //
    // showing and hiding
    // ------------------------------------------------------------------ //

    private void Show(OverlayPanel panel, double? seconds)
    {
        panel.Until = DateTime.UtcNow.AddSeconds(
            Math.Max(1, seconds ?? _settings.ShowSeconds));
        if (!panel.Visible)
        {
            panel.Show();
        }
        panel.TopMost = true;
        var other = ReferenceEquals(panel, _right) ? _left : _right;
        if (other.Up)
        {
            Hide(other);
        }
    }

    /// <summary>Den Umriss-Streifen mit ausblenden.</summary>
    /// <remarks>
    /// Er gehoert zum Anmeldeschirm. Bleibt er stehen, wenn der Streifen geht,
    /// klebt er ueber dem Rennen -- genau die Beschwerde, die es zum
    /// Vordergrund-Riegel gefuehrt hat.
    /// </remarks>
    private void HideShapes()
    {
        if (_hudPreview) { return; }
        if (_shapes is not null && !_shapes.IsDisposed && _shapes.Visible)
        {
            _shapes.Hide();
        }
    }

    private void Hide(OverlayPanel panel)
    {
        panel.Until = DateTime.MinValue;
        if (panel.Visible)
        {
            panel.Hide();
        }
        if ((_pinned == "right" && ReferenceEquals(panel, _right))
            || (_pinned == "left" && ReferenceEquals(panel, _left)))
        {
            _pinned = null;
        }
    }

    // ------------------------------------------------------------------ //
    // the loop
    // ------------------------------------------------------------------ //

    /// <summary>Laeuft Forza -- und was heisst das fuer die offenen Panels?</summary>
    /// <remarks>
    /// Der Uebergang ist der wichtige Teil: verlaesst jemand das Spiel mit einem
    /// angehefteten Panel, bliebe es sonst ueber dem Desktop stehen, immer im
    /// Vordergrund, ohne Taste zum Schliessen im Blick.
    /// </remarks>
    private bool GameIsUp()
    {
        if (!_settings.OverlayRequireForza)
        {
            return true;
        }
        // LAEUFT ES, UND IST ES AUCH VORNE?
        //
        // Zwei Fragen, und bis zum 2026-09-15 wurde nur die erste gestellt. Ein
        // Panel ueber dem Desktop ist nicht bloss unschoen: es liegt immer im
        // Vordergrund und nimmt keinen Klick an, laesst sich also nicht wegklicken.
        var vorne = !_settings.OverlayRequireFocus || _game.IsForeground;
        if (_game.Running && vorne)
        {
            FollowGameArea();
            if (_idleForGame)
            {
                _idleForGame = false;
                SetStatus(_reader.OcrAvailable
                    ? $"watching — OCR {_reader.OcrLanguage}"
                    : "watching — no OCR language installed, the route panel cannot read");
            }
            return true;
        }
        if (!_idleForGame)
        {
            _idleForGame = true;
            _pinned = null;
            Hide(_right);
            HideShapes();
            Hide(_left);
            // Der Delta-Streifen ist ein eigenes Fenster und blieb sonst stehen,
            // waehrend die beiden Panels verschwanden.
            HideHud();
            if (_reifen.Visible) { _reifen.Hide(); }
            SetStatus(_game.Running
                ? $"idle — {_game.ProcessName}.exe is not the active window"
                : $"idle — waiting for {_game.ProcessName}.exe");
        }
        return false;
    }

    /// <summary>
    /// Wird gerade gefahren? Dann wird NICHT vom Bildschirm gelesen (seit 2026-09-28).
    /// </summary>
    /// <remarks>
    /// Jedes Lesen holt Pixel per GDI vom Schirm zurueck -- das Automenue sogar den
    /// ganzen Schirm, zweimal je Sekunde: gemessen 62 ms je Griff auf 4K, dazu das
    /// Vergleichsbild der Streckenliste bei jeder Abfrage. Waehrend das Spiel rendert,
    /// muss die Grafikkarte dafuer ein fertiges Bild an den Prozessor zurueckgeben --
    /// genau die Art Stocken, die im Rennen niemand haben darf. Und waehrend der
    /// Fahrt gibt es weder Anmeldeschirm noch Automenue: zu lesen ist dann nichts.
    ///
    /// Gefahren wird, wenn die Telemetrie frisch ist und das Auto schneller als
    /// 2,5 m/s (9 km/h) faehrt, oder die Uhr einer Runde laeuft. Ohne frische
    /// Telemetrie (Menue, Pause, Data Out aus) wird gelesen wie bisher.
    /// </remarks>
    internal bool FaehrtGerade() =>
        DateTime.UtcNow - _telemetryAt < TimeSpan.FromSeconds(2)
        && (_tempo > 2.5f || (_recorder.HasLapUnderway && _recorder.CurrentSeconds > 0f));

    private void Tick()
    {
        if (_disposed) { return; }
        try
        {
            if (!GameIsUp())
            {
                return;
            }
            // Die Reifen zeigen Messwerte; kommen keine mehr (Data Out aus, Spiel
            // haengt), ist ein stehendes Bild eine Luege.
            if (_reifen.Visible && !_hudPreview && DateTime.UtcNow - _telemetryAt > TimeSpan.FromSeconds(1.5))
            {
                _reifen.Hide();
            }
            if (_left.Up)
            {
                RenderCar();
            }
            if (_settings.AutoShow && !_left.Up && !FaehrtGerade())
            {
                StartRead(force: false);
            }
            var now = DateTime.UtcNow;
            foreach (var (name, panel) in new[] { ("right", _right), ("left", _left) })
            {
                if (panel.Up && _pinned != name && now >= panel.Until)
                {
                    Hide(panel);
                }
            }
        }
        catch (Exception exception)
        {
            SetStatus($"tick failed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    /// <summary>
    /// Read the screen on a worker thread, and only when the pixels actually moved.
    /// </summary>
    /// <remarks>
    /// A menu is static, so the expensive step fires on transitions rather than on a
    /// timer. The comparison is a 160-pixel-wide grey thumbnail of the routes region
    /// -- cheap enough to run every poll while the game has the GPU.
    /// </remarks>
    private void StartRead(bool force)
    {
        if (_reading || _menueLesen || !_reader.OcrAvailable)
        {
            return;
        }
        if (!force && FaehrtGerade())
        {
            return;
        }
        var screen = GameArea.Find(_settings.ForzaProcess);
        if (screen.IsEmpty)
        {
            return;
        }
        if (!force && !ScreenMoved(GameArea.SixteenNine(screen)))
        {
            return;
        }
        _reading = true;
        if (force)
        {
            _forced = true;
        }
        // A panel stands over the very region we photograph -- the class badge sits
        // behind the right panel. Windows normally keeps our own windows out of the
        // picture (WDA_EXCLUDEFROMCAPTURE, asked for as each handle is created); where
        // it refuses, hiding them around the grab is the only correct read left, at
        // the cost of a visible blink.
        var blinked = new List<OverlayPanel>();
        foreach (var panel in new[] { _left, _right })
        {
            if (!panel.Visible || panel.ExcludedFromCapture || panel.ExcludeFromCapture())
            {
                continue;
            }
            panel.Hide();
            blinked.Add(panel);
        }
        Task.Run(() =>
        {
            ScreenState? state = null;
            string? failure = null;
            try
            {
                if (blinked.Count > 0)
                {
                    // Let the desktop under the panel repaint before the shutter opens.
                    Thread.Sleep(80);
                }
                state = _reader.ReadScreen(screen);
            }
            catch (Exception exception)
            {
                failure = $"{exception.GetType().Name}: {exception.Message}";
            }
            finally
            {
                _reading = false;
            }
            try
            {
                _owner.BeginInvoke(() =>
                {
                    if (_disposed) { return; }
                    foreach (var panel in blinked)
                    {
                        if (panel.Up)
                        {
                            panel.Show();
                            panel.TopMost = true;
                        }
                    }
                    Collect(state, failure);
                });
            }
            catch (Exception)
            {
                // The form is going away; nothing to deliver to.
            }
        });
    }

    private bool ScreenMoved(Rectangle screen)
    {
        try
        {
            var region = RivalsScreenReader.RegionOf(screen, _settings.RegionRoutes);
            // Direkt klein aufnehmen: bei 16K waere die volle Region allein ein
            // Bild von vielen Megabyte, nur um daraus 160 Punkte Breite zu machen.
            using var small24 = GameArea.Capture(region,
                new Size(160, Math.Max(1, 160 * region.Height / Math.Max(1, region.Width))));
            using var small = small24.Clone(new Rectangle(0, 0, small24.Width, small24.Height),
                                            PixelFormat.Format32bppArgb);
            var data = small.LockBits(new Rectangle(0, 0, small.Width, small.Height),
                                      ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var bytes = new byte[small.Width * small.Height];
            try
            {
                unsafe
                {
                    var scan = (byte*)data.Scan0;
                    for (var i = 0; i < bytes.Length; i++)
                    {
                        var at = i / small.Width * data.Stride + i % small.Width * 4;
                        bytes[i] = (byte)((scan[at] + scan[at + 1] + scan[at + 2]) / 3);
                    }
                }
            }
            finally
            {
                small.UnlockBits(data);
            }

            var previous = _lastThumb;
            _lastThumb = bytes;
            if (previous is null || previous.Length != bytes.Length)
            {
                return true;
            }
            long sum = 0;
            for (var i = 0; i < bytes.Length; i++)
            {
                sum += Math.Abs(bytes[i] - previous[i]);
            }
            return (double)sum / bytes.Length >= _settings.ChangeThreshold;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// Write what a read actually saw, so a failed press leaves evidence.
    /// </summary>
    /// <remarks>
    /// The panels are held out of every screenshot (WDA_EXCLUDEFROMCAPTURE), which is
    /// what makes the read correct -- and also means a screenshot cannot show what the
    /// panel said. Without this line a bad read is only ever a report from memory.
    /// </remarks>
    private static void LogRead(ScreenState? state, string? failure)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "forza-overlay");
            Directory.CreateDirectory(dir);
            var what = failure is not null
                ? $"FAILED {failure}"
                : state is null
                    ? "no state"
                    : $"offer={state.IsOffer} class={state.Klass ?? "-"}"
                      + $" [{state.KlassSource}]"
                      + $" tracks={string.Join(" | ", state.Tracks)}"
                      // Die gelesenen Laengen mit ins Protokoll. Sie entscheiden,
                      // welcher eigene Kurs eine angebotene Strecke ist; steht hier
                      // eine falsche Zahl, ist die Maske zu eng -- und das waere
                      // sonst erst an einer falsch beschrifteten Runde zu merken.
                      + $" km={string.Join(" | ", state.Tracks.Select(s => state.TrackLengths.TryGetValue(s, out var l) ? $"{l.TotalKm:0.0}/{l.Laps}" : "?"))}"
                      // Reihe und Statusspalte (Horizon Play), wenn der Schirm sie zeigt.
                      + (state.SeriesIndex > 0 ? $" series={state.Series} {state.SeriesIndex}/{state.SeriesCount}" : string.Empty)
                      + (state.TrackStatus.Count > 0
                          ? $" status={string.Join(" | ", state.TrackStatus.Select(p => $"{p.Key}={p.Value}"))} ab={state.FirstOwnIndex}"
                          : string.Empty);
            File.AppendAllText(Path.Combine(dir, "reads.log"),
                               $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {what}"
                               + Environment.NewLine);
        }
        catch (Exception)
        {
            // Diagnostics must never be the thing that breaks a read.
        }
    }

    /// <summary>Note a button press and what was on screen when it landed.</summary>
    /// <remarks>
    /// Same log as the reads, same reason: the panels are held out of every screen
    /// capture, so "the button did nothing" cannot be checked by looking. This says
    /// whether the press arrived at all and which panel was up when it did.
    /// </remarks>
    private void Act(string action)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "forza-overlay");
            Directory.CreateDirectory(dir);
            var up = _right.Up ? "right" : _left.Up ? "left" : "none";
            File.AppendAllText(Path.Combine(dir, "reads.log"),
                               $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} KEY {action}"
                               + $" (up={up}, pinned={_pinned ?? "-"})"
                               + Environment.NewLine);
        }
        catch (Exception)
        {
            // Diagnostics must never be the thing that breaks a press.
        }
    }

    private void Collect(ScreenState? state, string? failure)
    {
        LogRead(state, failure);
        if (failure is not null)
        {
            SetStatus($"read failed: {failure}");
            _forced = false;
            return;
        }
        if (state is null)
        {
            return;
        }
        MerkeModus(state);
        var forced = _forced;
        _forced = false;
        _screen = state;
        LearnOrdinal(state);

        if (!state.IsOffer)
        {
            if (forced)
            {
                RenderUnreadable(state);
                Show(_right, null);
            }
            return;
        }
        if (state.Key != _lastAdviceKey)
        {
            _lastAdviceKey = state.Key;
            RenderAdvice(state);
        }
        // An automatic show never interrupts the left panel: that was asked for.
        if (state.Key != _waved)
        {
            // A new screen is a new question, so the old refusal expires with it.
            _waved = null;
        }
        // DAS PANEL NUR BEI GESTARTETEM OVERLAY. Ohne liest der Schirm nur fuer die
        // Streckenkarten mit (AnmeldungOhneOverlay) -- ungefragt ein Panel aufzumachen,
        // das der Nutzer ausgeschaltet hat, waere das Gegenteil von dem, was er wollte.
        if (forced || (Running && _settings.AutoShow && !_left.Up && state.Key != _waved))
        {
            Show(_right, null);
        }
    }

    private void LearnOrdinal(ScreenState state)
    {
        // Only a car name read off the screen counts as evidence, and the masks do
        // not include the card's car by default -- so this stays quiet unless a
        // region for it is configured.
        _ = state;
    }

    // ------------------------------------------------------------------ //
    // the right panel: what to drive
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Ein Streckenname, kurz genug fuer eine Spaltenueberschrift.
    /// </summary>
    /// <remarks>
    /// "Bamboo Forest Scramble" passt in keine Zelle. Das erste Wort reicht, um
    /// die drei Spalten auseinanderzuhalten -- und die VOLLEN Namen stehen ohnehin
    /// direkt darueber in der Unterzeile des Fensters, in derselben Reihenfolge.
    /// </remarks>
    private static string Kurzname(string name)
    {
        if (name.Length <= 12) { return name; }
        var raum = name.IndexOf(' ');
        return raum > 0 ? name[..raum] : name;
    }

    /// <summary>
    /// Was der Anmeldeschirm zuletzt anbot, und wann.
    /// </summary>
    /// <remarks>
    /// DIE EINZIGE STELLE, AN DER EINE EIGENE RUNDE ZU EINEM STRECKENNAMEN KOMMT.
    ///
    /// Die Telemetrie nennt keine Strecke -- alle 178 bis zum 2026-09-16
    /// aufgezeichneten Runden haben ein leeres `track`. Der Anmeldeschirm nennt
    /// drei Namen mit ihren Laengen, und kurz darauf wird eine davon gefahren.
    /// Zusammen ergibt das einen Namen, den keine der beiden Quellen allein hat.
    /// </remarks>
    private (DateTime Seen, string? Klass,
             List<(string Name, double LapMetres)> Routen)? _letzterSchirm;

    /// <summary>Der zuletzt gelesene Schirm war der einer Reihe (Horizon Play, Spec Racing).</summary>
    private bool _schirmReihe;

    /// <summary>Ab welcher Strecke des Schirms man selbst faehrt (ScreenState.FirstOwnIndex).</summary>
    private int _schirmAb;

    /// <summary>Der zuletzt gelesene Menueschirm, der einen Modus belegt.</summary>
    private (string Modus, DateTime Seen, string? Strecke, double Km)? _modusSchirm;

    /// <summary>Der hoechste Platz seit Beginn der laufenden Runde (RacePosition).</summary>
    private int _platzMax;

    /// <summary>
    /// Wie lange ein Rivals-Schirm gilt. Laenger als eine Anmeldung: in Rivals faehrt man
    /// Runde um Runde und startet neu, ohne das Menue wiederzusehen. Was dazwischen
    /// geschieht, faengt der Platz ab (siehe ModusAusSchirm).
    /// </summary>
    private static readonly TimeSpan RivalsGilt = TimeSpan.FromHours(2);

    /// <summary>Welchen Modus der eben gelesene Schirm belegt -- der juengste gewinnt.</summary>
    private void MerkeModus(ScreenState state)
    {
        if (state.IsRivalsMenu)
        {
            _modusSchirm = ("rivals", DateTime.UtcNow, state.RivalsRoute, state.RivalsKm);
            LogLap($"screen: Rivals menu{(state.RivalsRoute is { } r ? " -- " + r : string.Empty)}"
                   + (state.RivalsKm > 0 ? $" ({state.RivalsKm:0.0} km)" : string.Empty));
        }
        else if (state.IsOffer)
        {
            _modusSchirm = (state.IsHorizonPlay ? "horizon-play" : "race", DateTime.UtcNow, null, 0);
        }
    }

    /// <summary>Den Modus einer Runde aus dem zuletzt gelesenen Menue -- wenn es frisch genug ist.</summary>
    /// <remarks>
    /// Rivals nur, wenn niemand vor einem lag: dort faehrt man allein, der Platz steht
    /// auf 1. Liegt er hoeher, war es ein Rennen, dessen Anmeldung nicht gelesen wurde --
    /// dann bleibt der Modus "unknown", statt Rivals zu behaupten.
    /// </remarks>
    internal void ModusAusSchirm(RecordedLap lap, int platzMax)
    {
        if (lap.Mode != "unknown" || _modusSchirm is not { } beleg) { return; }
        var alter = DateTime.UtcNow - beleg.Seen;
        if (alter > (beleg.Modus == "rivals" ? RivalsGilt : SchirmGilt)) { return; }
        if (beleg.Modus == "rivals" && platzMax > 1)
        {
            lap.ModeEvidence = $"conflict:rivals-screen-but-position-{platzMax}";
            return;
        }
        lap.Mode = beleg.Modus;
        lap.ModeEvidence = $"screen:{beleg.Modus}:{(int)alter.TotalSeconds}s";
        // Der Rivals-Schirm nennt die Strecke -- sie gilt, wenn die Laenge passt.
        if (beleg.Modus == "rivals" && string.IsNullOrWhiteSpace(lap.Track)
            && beleg.Strecke is { } name && LaengePasst(lap.LengthMetres, beleg.Km))
        {
            lap.Track = name;
            lap.TrackEvidence = "rivals-screen+length";
            LogLap($"route: {name} (Rivals screen, matched by length)");
        }
    }

    /// <summary>Passt eine gefahrene Runde zu "Route Length: x KM"? Eine Nachkommastelle, also grob.</summary>
    internal static bool LaengePasst(double meter, double km) =>
        km > 0 && meter > 0 && Math.Abs(meter / 1000.0 - km) <= Math.Max(0.08, km * 0.06);

    /// <summary>Den gelesenen Schirm fuer die naechsten Runden merken.</summary>
    /// <remarks>
    /// Seit 2026-09-26 VOR jeder Empfehlung: bis dahin stand das erst hinter dem
    /// Zweig ohne Bestenliste, und Strecken ohne Board -- die trotzdem gefahren
    /// werden -- bekamen nie einen Namen.
    /// </remarks>
    private void MerkeSchirm(ScreenState state)
    {
        var routen = state.Tracks
            .Select(s => (s, state.TrackLengths.TryGetValue(s, out var l) ? l.LapMetres : 0))
            .ToList();
        _letzterSchirm = (DateTime.UtcNow, state.Klass ?? string.Empty, routen);
        _schirmReihe = state.SeriesIndex > 0 || state.TrackStatus.Count > 0;
        _schirmAb = state.FirstOwnIndex;
    }

    /// <summary>
    /// Wie lange ein gelesener Anmeldeschirm fuer eine Runde noch als Beleg gilt.
    /// </summary>
    /// <remarks>
    /// Eine Horizon-Play-Reihe aus drei Rennen laeuft gut eine halbe Stunde, und
    /// zwischendurch wird der Schirm nicht wieder gezeigt. Kuerzer hiesse, die
    /// spaeteren Rennen der Reihe ohne Namen abzulegen. Laenger hiesse, eine
    /// Freifahrt am Abend noch mit dem Rennen vom Nachmittag zu beschriften.
    /// </remarks>
    private static readonly TimeSpan SchirmGilt = TimeSpan.FromMinutes(45);

    /// <summary>
    /// Welche der angebotenen Strecken war das gerade -- wenn es eindeutig ist.
    /// </summary>
    /// <remarks>
    /// Entschieden wird ueber die LAENGE, aber nur innerhalb der drei angebotenen
    /// Strecken. Das ist der Unterschied, auf den es ankommt: gegen alle 56 eigenen
    /// Kursordner gehalten passen auf jede dieser Laengen zwei bis vier Kurse, und
    /// eine Wahl daraus waere geraten. Gegen DREI Strecken gehalten, die im Schnitt
    /// weit auseinanderliegen, bleibt fast immer genau eine uebrig -- und wenn
    /// nicht, wird nichts eingetragen.
    ///
    /// Die Klasse muss auch stimmen. Ein Klasse-A-Angebot beschriftet keine
    /// Klasse-B-Runde, selbst wenn die Laenge passt.
    /// </remarks>
    private (string Name, string Evidence)? StreckeZurRunde(RecordedLap lap)
    {
        if (_letzterSchirm is not { } schirm) { return null; }
        if (DateTime.UtcNow - schirm.Seen > SchirmGilt) { return null; }
        if (lap.LengthMetres <= 0) { return null; }

        if (!string.IsNullOrEmpty(schirm.Klass))
        {
            var klasseDerRunde = LapArchive.ClassOf(lap.PerformanceIndex);
            if (!string.Equals(klasseDerRunde, schirm.Klass,
                               StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        // IN EINER REIHE IST DIE REIHENFOLGE BEKANNT. Die erste noch offene Strecke
        // ab dem Einstieg ist die, die gerade gefahren wird. Passt ihre Laenge, gilt
        // sie -- auch wenn eine andere fast gleich lang ist: Coastline Sprint und
        // Festival Sprint liegen 3,7 % auseinander, und die Laenge allein liess eine
        // solche Runde am 2026-09-25 ohne Namen.
        if (_schirmReihe)
        {
            var erwartet = ErwarteteStrecke(schirm.Routen.Count, _schirmAb, _erledigt);
            if (erwartet >= 0)
            {
                var (name, meter) = schirm.Routen[erwartet];
                if (meter > 0 && Math.Abs(meter - lap.LengthMetres) / meter <= 0.06)
                {
                    return (name, "series-order+length");
                }
            }
        }

        string? treffer = null;
        foreach (var (name, meter) in schirm.Routen)
        {
            if (meter <= 0) { continue; }
            if (Math.Abs(meter - lap.LengthMetres) / meter > 0.06) { continue; }
            if (treffer is not null) { return null; }   // zwei passen: keiner gilt
            treffer = name;
        }
        return treffer is null ? null : (treffer, "signup+length");
    }

    /// <summary>Die Strecke einer Reihe, die jetzt dran ist: die erste offene ab dem Einstieg.</summary>
    internal static int ErwarteteStrecke(int anzahl, int ab, IReadOnlySet<int> erledigt) =>
        Enumerable.Range(0, anzahl).FirstOrDefault(i => i >= ab && !erledigt.Contains(i), -1);

    public void RenderAdvice(ScreenState state)
    {
        var klass = state.Klass ?? string.Empty;
        // The race type comes from the routes, not from the screen. Passing it on
        // also keeps the ranking inside one category, which matters wherever two
        // categories share a route name.
        var category = _advisor.CategoryOf(state.Tracks);
        // EINE REIHE, MITTEN DRIN BETRETEN (Horizon Play "2/3"): die Empfehlung gilt den
        // Strecken, die man noch selbst faehrt. Die laufende faehrt man nicht mehr, und
        // ein Auto, das nur dort glaenzt, ist die falsche Wahl. Die Kategorie kommt
        // weiter aus ALLEN Strecken -- sie beschreibt die Reihe, nicht den Rest.
        var advice = _advisor.Advise(state.RemainingTracks, klass, category);
        var title = category is null ? $"Class {klass}" : $"Class {klass} · {category}";
        var reihe = state.SeriesIndex > 0
            ? $"{state.Series} {state.SeriesIndex}/{state.SeriesCount}: "
            : state.FirstOwnIndex > 0 ? "Remaining: " : string.Empty;
        var lines = new List<PanelLine>();
        var budget = _right.RowBudget;
        MerkeSchirm(state);

        if (advice.ByPoints.Count == 0)
        {
            // DIE UMRISSE HAENGEN NICHT AN DER BESTENLISTE. Bis zum 2026-09-25 kehrte
            // dieser Zweig vor ihnen zurueck: ohne Board in dieser Klasse keine Karten,
            // obwohl die Karten gar nichts mit den Zeiten zu tun haben.
            ZeigeUmrisse(state.Tracks.Select(n => (n, string.Empty)).ToList(), state.FirstOwnIndex);
            lines.Add(new PanelLine("No board for these routes in this class yet.",
                                    string.Empty, OverlayPanel.Warn));
            _right.SetContent($"{title} – what to drive",
                              reihe + string.Join(" · ", advice.Tracks),
                              lines,
                              "The sweep has not reached them. The site's Scan status "
                              + "tab lists what exists.");
            return;
        }

        if (state.Spec)
        {
            lines.Add(new PanelLine("One-make event — the car is fixed.",
                                    string.Empty, OverlayPanel.Warn));
            lines.Add(new PanelLine("The order below is for these routes in general.",
                                    string.Empty, OverlayPanel.InkSoft, Small: true));
        }

        // WAS DU SELBST IN DIESER KLASSE GEFAHREN BIST.
        //
        // Die Empfehlung sagt, welches Auto schnell IST -- gerechnet aus fremden
        // Runden. Sie sagt nicht, welches davon du ueberhaupt HAST, und daran
        // haengt die Wahl im Menue tatsaechlich. Darum die zweite Spalte.
        //
        // Der Uebergang geht ueber die Telemetrie-Kennung: eigene Runde -> Ordinal
        // -> OrdinalMap -> Auto-Kennung des Datensatzes. Wo die Zuordnung fehlt,
        // bleibt die Spalte leer; eine geratene Zuordnung waere hier schlimmer als
        // gar keine, weil sie an einem fremden Auto deine Zeit behaupten wuerde.
        var meineBesten = OwnCars.Bests(klass);
        var meineNachAuto = new Dictionary<int, OwnCars.Best>();
        var geraten = new HashSet<int>();
        foreach (var best in meineBesten)
        {
            // Erst das Gelernte, dann die Annahme -- genau wie RenderCar es unten
            // macht. Am 2026-09-15 ist config/fh6_car_ordinals.json noch gar nicht
            // vorhanden, es ist also IMMER die Annahme; ohne sie bliebe die Spalte
            // auf jeder Zeile leer und das ganze Feature waere unsichtbar.
            var eintrag = _ordinals.Lookup(best.Ordinal);
            var index = eintrag?.CarIndex;
            var angenommen = false;
            if (index is null)
            {
                index = _advisor.CarIndexForId(best.Ordinal);
                angenommen = index is not null;
            }
            if (index is null || index < 0) { continue; }

            // Zeigen zwei Ordinals auf dasselbe Auto, gewinnt die schnellere Zeit.
            if (!meineNachAuto.TryGetValue(index.Value, out var da)
                || best.BestSeconds < da.BestSeconds)
            {
                meineNachAuto[index.Value] = best;
                if (angenommen) { geraten.Add(index.Value); }
                else { geraten.Remove(index.Value); }
            }
        }

        // ONE list, not two: points and the time sum answer different questions and
        // a glance mid-menu can only hold one order.
        var byPoints = _settings.ShowsPoints;
        // The WHOLE ranking goes to the panel, hundreds of cars deep. It shows what
        // fits and Page Down walks the rest: a list cut off at the height of a
        // window looked like a ranking that ended there.
        var rows = byPoints ? advice.ByPoints : advice.ByTime;
        // DIE UEBERSICHT UEBER DIE EIGENEN AUTOS, ANGEHEFTET.
        //
        // Erst stand hier nur die schnellste Runde -- eine Zeile, ein Auto. Das war
        // zu wenig: die Rangliste darunter ist hunderte Zeilen tief, die eigenen
        // Autos liegen darin verstreut, und ohne Blaettern sieht man keines davon.
        // Der Nutzer hat es am 2026-09-16 so gesagt, und er hatte recht.
        //
        // KEINE UEBERSCHRIFT. Angeheftet ist alles bis zur ERSTEN Ueberschrift, und
        // an ihr haengt auch die Anzeige "wo in der Liste bin ich". Eine eigene
        // Ueberschrift hier wuerde die an sich reissen. Die Beschriftungszeile ist
        // darum eine gewoehnliche Zeile in der Leitfarbe.
        // DIE SPALTEN SIND DIE DREI STRECKEN DES SCHIRMS -- nichts anderes.
        //
        // Vorher suchte sich die Tabelle ihre Kurse selbst und stand mit Laengen
        // wie "6.0km" ueber Autos, die auf den angebotenen Strecken nie gefahren
        // waren. Das las sich wie Zufallszahlen, weil es welche waren: Antworten
        // auf eine Frage, die niemand gestellt hatte.
        //
        // Die Bruecke vom Namen zum eigenen Kursordner ist die Laenge, die der
        // Schirm unter dem Streckennamen nennt. Fehlt sie, bleibt die Spalte leer
        // -- das ist richtig so, siehe OwnCars.Table.
        //
        // ALLE STRECKEN DES SCHIRMS, auch bei einer Reihe: die Tabelle und die Karten
        // zeigen die ganze Reihe (die Karten mit Stand), nur die Rangliste darueber gilt
        // dem Rest. Seit 2026-09-26 aus dem Schirm statt aus der Empfehlung -- dieselbe
        // Liste, die MerkeSchirm fuer die Rundennamen ablegt, in derselben Reihenfolge.
        var routen = _letzterSchirm?.Routen ?? new List<(string Name, double LapMetres)>();

        var meineTabelle = OwnCars.Table(klass, routen);
        if (routen.Count > 0)
        {
            var platz = new Dictionary<int, int>();
            foreach (var r in rows)
            {
                platz[r.Car] = r.Place;
            }

            var kopf = new string[routen.Count];
            for (var i = 0; i < kopf.Length; i++)
            {
                // Der Streckenname, gekuerzt auf das, was in eine Zelle passt --
                // die Ueberschrift der Spalte ist der Name, nicht die Laenge.
                kopf[i] = Kurzname(meineTabelle.Courses[i].Label);
            }
            lines.Add(new PanelLine(
                $"YOUR TIMES ON THESE ROUTES ({klass})",
                string.Empty, OverlayPanel.Bar, Small: true, Cells: kopf));

            if (meineTabelle.Rows.Count == 0)
            {
                // EHRLICH LEER. Eine Zeile, die sagt warum, ist mehr wert als eine
                // gefuellte Tabelle ueber andere Strecken.
                var mehrdeutig = meineTabelle.Courses.Any(c => c.Ambiguous);
                var ohneLaenge = routen.Any(r => r.LapMetres <= 0);
                var grund = mehrdeutig
                    ? "several of your courses share these lengths — cannot tell them apart"
                    : ohneLaenge
                        ? "the screen did not give a distance for every route"
                        : "you have no recorded laps on these routes";
                lines.Add(new PanelLine(grund, string.Empty, OverlayPanel.Muted,
                                        Small: true, Indent: 10));
            }

            // OBERGRENZE, DAMIT DER ANGEHEFTETE KOPF DIE RANGLISTE NICHT VERDRAENGT.
            // Der Kopf scrollt nicht; ohne Grenze kaeme man an die Empfehlung auf
            // einem 1080er Schirm gar nicht mehr heran.
            const int Hoechstens = 12;
            var ohneNamenEigene = 0;
            foreach (var zeile in meineTabelle.Rows.Take(Hoechstens))
            {
                var eintrag = _ordinals.Lookup(zeile.Ordinal);
                var index = eintrag?.CarIndex ?? _advisor.CarIndexForId(zeile.Ordinal);
                // Erst das Gelernte, dann der Datensatz -- und BEIDES muss ein
                // echter Name sein. Frueher stand hier zuletzt $"car {Ordinal}",
                // also eine erfundene Beschriftung fuer genau den Fall, in dem
                // nichts bekannt ist.
                var name = (RivalsAdvisor.IsRealCarName(eintrag?.Name)
                                ? eintrag!.Name : null)
                           ?? (index is { } ix ? _advisor.RealCarName(ix) : null);
                if (name is null)
                {
                    ohneNamenEigene++;
                    continue;
                }
                // Die Tilde heisst: die Zuordnung Ordinal -> Auto ist angenommen,
                // nicht gelernt. Dieselbe Marke wie in der Rangliste.
                if (eintrag is null && index is not null) { name = "~" + name; }
                if (index is { } i3 && platz.TryGetValue(i3, out var pl))
                {
                    name = $"#{pl} {name}";
                }

                var zellen = new string[meineTabelle.Courses.Count];
                for (var i = 0; i < zellen.Length; i++)
                {
                    var key = meineTabelle.Courses[i].Key;
                    zellen[i] = key.Length > 0
                                && zeile.ByCourse.TryGetValue(key, out var s)
                        ? OwnCars.TimeText(s)
                        : "–";
                }

                lines.Add(new PanelLine(
                    name, string.Empty, OverlayPanel.Ink,
                    Small: true, Indent: 10, Cells: zellen));
            }

            if (meineTabelle.Rows.Count > Hoechstens)
            {
                var leer = new string[meineTabelle.Courses.Count];
                for (var i = 0; i < leer.Length; i++) { leer[i] = string.Empty; }
                lines.Add(new PanelLine(
                    $"+{meineTabelle.Rows.Count - Hoechstens} more of your cars",
                    string.Empty, OverlayPanel.Muted, Small: true, Indent: 10,
                    Cells: leer));
            }
        }

        lines.Add(new PanelLine((byPoints ? "by points" : "by time sum")
                                + $" · {advice.Tracks.Count} route(s)",
                                string.Empty, OverlayPanel.Bar, Heading: true));
        var ohneNamen = 0;
        foreach (var row in rows)
        {
            // KEIN "Car #3118" IN DER EMPFEHLUNG.
            //
            // Diese Liste beantwortet "welches Auto nehme ich". Eine Kennung statt
            // eines Namens beantwortet sie nicht: im Automenue des Spiels ist
            // "Car #3118" nicht zu finden. Die Zeile wird darum uebersprungen --
            // aber gezaehlt und unten genannt, denn stillschweigend verschwinden
            // duerfen Autos aus einer Rangliste nicht.
            //
            // Die PLATZZIFFER bleibt die echte. Dadurch entstehen sichtbare
            // Luecken (12., 14., 15.), und das ist richtig so: die Luecke sagt,
            // dass dort etwas steht, das hier nur nicht zu benennen ist.
            if (!RivalsAdvisor.IsRealCarName(row.Name))
            {
                ohneNamen++;
                continue;
            }
            var complete = row.Present == advice.Tracks.Count;
            var mark = complete ? string.Empty : $"  ({row.Present}/{advice.Tracks.Count})";
            // Eine Tilde heisst: die Zuordnung Ordinal -> Auto ist angenommen,
            // nicht gelernt. Markiert, nie stillschweigend -- sonst behauptet die
            // Spalte an einem fremden Auto deine Zeit.
            var mein = meineNachAuto.TryGetValue(row.Car, out var meins)
                ? (geraten.Contains(row.Car) ? "~" : string.Empty)
                  + OwnCars.TimeText(meins.BestSeconds)
                : string.Empty;
            lines.Add(new PanelLine($"{row.Place,2}. {row.Name}{mark}",
                                    byPoints ? $"{row.Points} pts"
                                             : RivalsAdvisor.SumText(row.Ms),
                                    complete ? OverlayPanel.Ink : OverlayPanel.InkSoft,
                                    Mine: mein));
        }

        var notes = new List<string>
        {
            byPoints ? "F7 switches to the time sum" : "F7 switches to points",
            "Page Down / Page Up walk the rest of the field",
        };
        if (advice.MissingTracks.Count > 0)
        {
            notes.Add("no board yet for " + string.Join(", ", advice.MissingTracks));
        }
        if (advice.ShallowTracks.Count > 0)
        {
            notes.Add("too thin for the time sum: "
                      + string.Join(", ", advice.ShallowTracks.Distinct()));
        }
        if (state.KlassSource.StartsWith("PI", StringComparison.Ordinal))
        {
            notes.Add($"class guessed from the {state.KlassSource}");
        }
        notes.Add("a low place usually means few surviving laps, not a slow car");
        // WAS FEHLT, WIRD GESAGT. Eine gefilterte Liste, die nicht zugibt, dass sie
        // gefiltert ist, ist eine falsche Liste.
        if (ohneNamen > 0)
        {
            notes.Add($"{ohneNamen} car(s) hidden: the dataset has no name for them, "
                      + "only an id");
        }
        if (meineTabelle.Rows.Count > 0)
        {
            // WAS DIE SPALTEN SIND. Innerhalb einer Spalte darf verglichen werden,
            // sie ist eine Strecke. Quer ueber die Spalten nicht.
            notes.Add("your best lap on each of the three routes above");
            var duelle = OwnCars.Duels(klass);
            if (duelle.Count > 0)
            {
                // Der eine Fall, in dem ein Vergleich wirklich zaehlt: dasselbe
                // Stueck Strasse, dieselbe Art Start, zwei eigene Autos.
                var d = duelle[0];
                var namen = d.Order
                    .Select(o => (RivalsAdvisor.IsRealCarName(
                                      _ordinals.Lookup(o.Ordinal)?.Name)
                                      ? _ordinals.Lookup(o.Ordinal)!.Name : null)
                                 ?? (_advisor.CarIndexForId(o.Ordinal) is { } ci
                                     ? _advisor.RealCarName(ci) : null))
                    .ToList();
                // NUR NENNEN, WENN BEIDE EINEN NAMEN HABEN. Ein Duell
                // "Subaru BRZ '13 > car 3118" sagt nichts darueber, was man
                // nehmen soll -- der Gewinner ist benannt, der Verlierer nicht,
                // und umgekehrt waere es noch schlimmer.
                if (namen.Count > 0 && namen.All(n => n is not null))
                {
                    notes.Add($"head to head ({OwnCars.ConditionText(d.Standing, d.Sprint)}): "
                              + string.Join(" > ", namen));
                }
            }
        }

        _right.SetContent($"{title} – what to drive",
                          reihe + string.Join(" · ", advice.Tracks), lines,
                          string.Join(" — ", notes));

        // DIE UMRISSE DER ANGEBOTENEN STRECKEN.
        //
        // Die Zuordnung Strecke -> eigener Kursordner ist schon gerechnet: sie
        // steckt in meineTabelle.Courses, in der Reihenfolge des Schirms. Sie hier
        // noch einmal zu suchen hiesse, zwei Wahrheiten ueber dieselbe Frage zu
        // haben -- und irgendwann widersprechen die sich.
        ZeigeUmrisse(Enumerable.Range(0, routen.Count)
            .Select(i => (routen[i].Name,
                          i < meineTabelle.Courses.Count ? meineTabelle.Courses[i].Key
                                                         : string.Empty))
            .ToList(), state.FirstOwnIndex);
    }

    /// <summary>Die Umrisse der angebotenen Strecken zeigen -- mit oder ohne Bestenliste.</summary>
    /// <param name="routen">Name je Strecke und, wenn bekannt, der eigene Kursordner.</param>
    /// <param name="ab">Ab welcher Strecke man selbst faehrt (Horizon Play "2/3": ab der zweiten).</param>
    private void ZeigeUmrisse(IReadOnlyList<(string Name, string Key)> routen, int ab = 0)
    {
        // DASSELBE ANGEBOT WIE ZULETZT? Dann bleibt, was schon gefahren ist -- zwischen
        // zwei Rennen einer Meisterschaft zeigt das Spiel die Liste womoeglich erneut.
        var gleich = routen.Select(r => r.Name).SequenceEqual(_angebot.Select(a => a.Name),
                                                              StringComparer.OrdinalIgnoreCase)
                     && DateTime.UtcNow - _angebotZeit <= AngebotGilt;
        if (!gleich)
        {
            _erledigt.Clear();
            _meisterschaftWeg.Stop();
            // Ein neues Angebot ist eine neue Frage: die Zeit laeuft neu.
            _umrisseUhr.NewOffer();
        }
        // MITTEN IN EINE REIHE EINGESTIEGEN: was vor dem Einstieg liegt, faehrt man
        // nicht mehr -- es zaehlt als erledigt. Bis 2026-09-26 hielt die Zaehlung die
        // laufende Strecke fuer die eigene, und die Karten nannten sie "jetzt". Auch
        // bei gleichem Angebot: der Schirm weiss es dann besser als die eigene Zaehlung.
        foreach (var i in Einstieg(ab, routen.Count)) { _erledigt.Add(i); }
        _angebot = routen.ToList();
        _angebotZeit = DateTime.UtcNow;
        // WAEHREND DES RENNENS NIE -- auch nicht, wenn der Leser mitten im Rennen
        // etwas fuer den Anmeldeschirm haelt.
        if (_rennenLaeuft) { return; }
        // Fuer DIESES Angebot schon abgelaufen: nicht wieder einblenden.
        if (_umrisseUhr.Expired) { return; }
        ZeigeAngebot();
    }

    private int _anmeldungTakt;

    /// <summary>
    /// Den Anmeldeschirm auch dann lesen, wenn das Overlay nicht gestartet ist -- fuer
    /// die Streckenkarten, die ein eigenes Element mit eigenem Schalter sind.
    /// </summary>
    /// <remarks>
    /// Seit 2026-09-26. Bis dahin las nur der Takt des gestarteten Overlays den Schirm;
    /// nach einem Update (Neustart ohne "--overlay") kamen die Karten darum gar nicht
    /// mehr, obwohl sie im HUD-Editor an waren. Das Vorschlags-PANEL bleibt am Knopf
    /// (siehe Collect) -- gelesen wird hier nur fuer die Karten. Im selben Takt wie das
    /// Overlay: jeder dritte Schlag des Halbsekunden-Takts.
    /// </remarks>
    private void AnmeldungOhneOverlay()
    {
        try
        {
            // Gelesen wird auch ohne Karten, solange Runden aufgezeichnet werden: der
            // Schirm belegt ihren Modus (Rivals, Horizon Play, Rennen) und ihre Strecke.
            if (Running || _hudPreview || (!_settings.CourseShapes && !_settings.ArchiveLaps)) { return; }
            // SPIEL NICHT VORNE: die Karten weg. Mit gestartetem Overlay erledigt das
            // GameIsUp; ohne bliebe sonst ein Fenster ueber dem Desktop stehen, immer
            // im Vordergrund und nicht wegzuklicken.
            if (!_game.Running || !_game.IsForeground) { HideShapes(); return; }
            if (++_anmeldungTakt % 3 != 0) { return; }
            if (FaehrtGerade()) { return; }
            FollowGameArea();
            StartRead(force: false);
        }
        catch (Exception)
        {
            // Ein Leseversuch darf nichts anhalten.
        }
    }

    /// <summary>Zweimal je Sekunde: ist die Anzeigedauer der Karten um?</summary>
    private void PruefeUmrisseZeit()
    {
        if (_hudPreview) { return; }
        if (_umrisseUhr.Due(DateTime.UtcNow)) { HideShapes(); }
    }

    /// <summary>Das gemerkte Angebot zeigen, mit dem Stand der Meisterschaft.</summary>
    private void ZeigeAngebot()
    {
        if (!_settings.CourseShapes)
        {
            HideShapes();
            return;
        }
        var routen = _angebot;
        var umrisse = new List<(string, CourseShape.Outline?)>();
        foreach (var (name, key) in routen)
        {
            // Ohne eigenen Kursordner (nie gefahren) gibt es keine Telemetrie --
            // aber die geerntete Rivalen-Karte, nach dem Namen der Strecke.
            var umriss = key.Length > 0 ? CourseShape.For(key, _settings.ShapeSourceChoice) : null;
            umriss ??= CourseShape.ForRoute(name, _settings.ShapeSourceChoice);
            umrisse.Add((name, umriss));
        }
        _shapes.SetCourses(umrisse, Stati(umrisse.Count, _erledigt));
        if (!_shapes.Visible) { _shapes.Show(); }
        _umrisseUhr.Shown(DateTime.UtcNow, _settings.CourseShapeSeconds);
        _shapes.TopMost = true;
        OverlayController.WriteDiagnostic(
            $"Umrisse gezeigt: {string.Join(" | ", umrisse.Select(u => u.Item1 + (u.Item2 is null ? " (keine Karte)" : "")))}");
    }

    private void RenderUnreadable(ScreenState state)
    {
        var lines = new List<PanelLine>();
        if (state.Tracks.Count > 0)
        {
            lines.Add(new PanelLine("routes recognised", string.Empty,
                                    OverlayPanel.Bar, Heading: true));
            foreach (var name in state.Tracks)
            {
                lines.Add(new PanelLine(name,
                    state.TrackScores.GetValueOrDefault(name).ToString("0.00"),
                    OverlayPanel.InkSoft));
            }
        }
        else
        {
            lines.Add(new PanelLine("No route name matched.", string.Empty,
                                    OverlayPanel.Warn));
        }
        lines.Add(new PanelLine($"class: {state.Klass ?? "not found"}", string.Empty,
                                state.Klass is null ? OverlayPanel.Warn : OverlayPanel.InkSoft));
        lines.Add(new PanelLine("what usually fixes it", string.Empty,
                                OverlayPanel.Bar, Heading: true));
        foreach (var hint in new[]
                 {
                     "Open the Event Sign Up screen, then press again.",
                     "The route names have to be on screen as text.",
                     "Exclusive fullscreen hides the overlay entirely — use borderless.",
                 })
        {
            lines.Add(new PanelLine(hint, string.Empty, OverlayPanel.InkSoft, Small: true));
        }
        _right.SetContent("Could not read the routes",
                          $"{state.Lines.Count} lines of text in the masks", lines,
                          "The Rivals overlay tab has a \"Save mask preview\" button: "
                          + "it draws both regions on a capture, which is how a mask "
                          + "that has drifted is spotted.");
    }

    // ------------------------------------------------------------------ //
    // the left panel: your car
    // ------------------------------------------------------------------ //

    public void RenderCar()
    {
        var known = _ordinals.Lookup(_ordinal);
        int? carIndex = known?.CarIndex;
        var assumed = false;
        if (carIndex is null && _ordinal is not null)
        {
            // The unproved but likely route: the ordinal used as a leaderboard
            // car_id. Marked, never silent -- if the name below is not the car under
            // you, that is the answer to whether the two numberings match.
            carIndex = _advisor.CarIndexForId(_ordinal.Value);
            assumed = carIndex is not null;
        }

        var key = $"{carIndex}/{_ordinal}/{_pi}/{_settings.ScoreMode}";
        if (key == _lastCarKey)
        {
            return;
        }
        _lastCarKey = key;
        var lines = new List<PanelLine>();

        if (!TelemetryFresh)
        {
            var port = _host?.TelemetryPort ?? 5300;
            var listening = _host?.TelemetryRunning ?? false;
            lines.Add(new PanelLine("Forza: Settings → HUD and Gameplay → Data Out",
                                    string.Empty, OverlayPanel.InkSoft, Small: true));
            lines.Add(new PanelLine($"On · 127.0.0.1 · port {port}",
                                    string.Empty, OverlayPanel.InkSoft, Small: true));
            _left.SetContent("Your car",
                             listening
                                 ? $"listening on {port}, no packets yet"
                                 : $"the listener is not running on {port}",
                             lines, string.Empty);
            return;
        }

        var piClass = RivalsScreenReader.ClassForPi(_pi);
        var head = new List<string>();
        if (_pi is > 0)
        {
            head.Add($"PI {_pi}");
        }
        if (piClass is not null)
        {
            head.Add($"class {piClass}");
        }
        if (_drivetrain is not null && Drivetrain.TryGetValue(_drivetrain.Value, out var dt))
        {
            head.Add(dt);
        }
        if (_cylinders is > 0)
        {
            head.Add($"{_cylinders} cyl");
        }
        // Always shown: a wrong name beside the right ordinal is the whole diagnosis.
        head.Add($"ordinal {_ordinal}");

        var title = known?.Name ?? (carIndex is not null
            ? _advisor.CarName(carIndex.Value)
            : $"Car ordinal {_ordinal}");

        if (carIndex is null)
        {
            lines.Add(new PanelLine("This ordinal matches no car in the records.",
                                    string.Empty, OverlayPanel.Warn));
            _left.SetContent(title, string.Join(" · ", head), lines, string.Empty);
            return;
        }

        if (assumed)
        {
            lines.Add(new PanelLine("Name assumed from the ordinal — not confirmed.",
                                    string.Empty, OverlayPanel.Warn, Small: true));
        }

        var stands = _advisor.Standings(carIndex.Value);
        if (stands.Count == 0)
        {
            lines.Add(new PanelLine("No lap by this car in the records yet.",
                                    string.Empty, OverlayPanel.Warn));
            _left.SetContent(title, string.Join(" · ", head), lines,
                             "Only cars that appear on a scanned leaderboard can be placed.");
            return;
        }

        // Ranked by the same metric the right panel is set to, so both sides of the
        // screen answer the same question.
        Func<RivalsAdvisor.Standing, int> place = _settings.ShowsPoints
            ? s => s.PlacePoints
            : s => s.PlaceTime;
        var best = stands.OrderBy(s => (double)place(s) / Math.Max(1, s.Of)).First();

        foreach (var group in stands.GroupBy(s => s.Category))
        {
            lines.Add(new PanelLine(group.Key, string.Empty, OverlayPanel.Bar,
                                    Heading: true));
            foreach (var stand in group)
            {
                var share = (double)place(stand) / Math.Max(1, stand.Of);
                var tone = ReferenceEquals(stand, best) ? OverlayPanel.Good
                    : share <= 0.25 ? OverlayPanel.Ink : OverlayPanel.InkSoft;
                var here = piClass == stand.Klass ? " ←" : string.Empty;
                lines.Add(new PanelLine($"{stand.Klass,-3}{here}",
                                        $"#{place(stand)} of {stand.Of}", tone));
                var tune = stand.Tune is not null
                    && RivalsAdvisor.TuneLabel.TryGetValue(stand.Tune, out var label)
                    ? $" · {label}" : string.Empty;
                lines.Add(new PanelLine($"{stand.Present}/{stand.Tracks} routes{tune}",
                                        RivalsAdvisor.SumText(stand.Ms),
                                        OverlayPanel.Muted, Small: true, Indent: 22));
            }
        }

        var missing = _settings.CategoriesExpected
            .Where(c => stands.All(s => s.Category != c)).ToList();
        var note = new List<string>
        {
            $"best fit: {best.Category} {best.Klass} (#{place(best)} of {best.Of})",
        };
        if (missing.Count > 0)
        {
            note.Add("not scanned yet: " + string.Join(", ", missing));
        }
        _left.SetContent(title, string.Join(" · ", head), lines,
                         string.Join(" — ", note));
    }

    // ------------------------------------------------------------------ //
    // calibration
    // ------------------------------------------------------------------ //

    /// <summary>Save a capture with both mask regions drawn on it.</summary>
    /// <remarks>
    /// The fractions were measured off one 1440p capture by eye. This is how they
    /// get checked against the screen they actually have to work on.
    /// </remarks>
    public string SaveMaskPreview()
    {
        // Die Spielflaeche, auf die Messhoehe gebracht: bei 16K kein Riesenbild.
        var screen = GameArea.SixteenNine(GameArea.Find(_settings.ForzaProcess));
        var k = Math.Min(1.0, 1440.0 / Math.Max(1, screen.Height));
        using var frame = GameArea.Capture(screen,
            new Size((int)Math.Round(screen.Width * k), (int)Math.Round(screen.Height * k)));
        using (var g = Graphics.FromImage(frame))
        {
            foreach (var (fractions, colour, label) in new[]
                     {
                         (_settings.RegionRoutes, Color.LimeGreen, "routes"),
                         (_settings.RegionClass, Color.Orange, "class"),
                     })
            {
                if (fractions is null || fractions.Length != 4)
                {
                    continue;
                }
                var rect = RivalsScreenReader.RegionOf(
                    new Rectangle(0, 0, frame.Width, frame.Height), fractions);
                using var pen = new Pen(colour, 3);
                using var brush = new SolidBrush(colour);
                using var font = new Font("Segoe UI Semibold", 14);
                g.DrawRectangle(pen, rect);
                g.DrawString(label, font, brush, rect.Left + 6,
                             Math.Max(0, rect.Top - 26));
            }
        }
        var dir = Path.Combine(Path.GetTempPath(), "forza-overlay");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir,
            $"mask-preview-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        frame.Save(path, ImageFormat.Png);
        return path;
    }

    /// <summary>Read the screen once and report what it found, for the tab.</summary>
    public ScreenState ReadOnce(bool full = false)
    {
        return _reader.ReadScreen(GameArea.Find(_settings.ForzaProcess), full);
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        Stop();
        _tick.Dispose();
        _triggerTick.Dispose();
        foreach (var zeitgeber in AlleZeitgeber())
        {
            try { zeitgeber.Stop(); zeitgeber.Dispose(); } catch (Exception) { }
        }
        _right.Dispose();
        _left.Dispose();

        // UND DER STREIFEN.
        //
        // Er fehlte hier, und das war teuer: `RivalsTab.ApplyDataset` wirft den
        // Controller weg und baut einen neuen, sobald ein frischer Datensatz da ist
        // -- also bei JEDEM Start der App. Der alte Streifen blieb dabei offen:
        // bildschirmfuellend, immer obenauf, durchklickbar und mit eingefrorenem
        // Inhalt, weil ihn niemand mehr fuettert. Der neue zeichnete daneben.
        //
        // Auf dem Schirm sieht das aus, als liefe das Overlay mehrfach -- und
        // genau so hat es der Nutzer am 2026-09-13 beschrieben. Ein stehender
        // Geister-Countdown, der zu nichts mehr gehoert, kommt aus derselben Quelle.
        if (_hud is not null && !_hud.IsDisposed)
        {
            try { _hud.Close(); _hud.Dispose(); } catch (Exception) { }
        }
        _hud = null;
        // Die Feier ebenso: ein neuer Controller (frischer Datensatz) darf keine
        // halbe Feier des alten stehen lassen.
        try { _feier.Beenden(); _feier.Close(); _feier.Dispose(); } catch (Exception) { }
        // Und alle uebrigen Fenster: bis zum 2026-09-28 blieben Umriss, Notiz, Meldung
        // und Live-Karte des alten Controllers offen -- sichtbar eingefroren, wenn sie
        // beim Wechsel gerade standen.
        foreach (var fenster in new LayeredHud[] { _shapes, _carNote, _meldung, _liveMap, _reifen })
        {
            try { fenster.Close(); fenster.Dispose(); } catch (Exception) { }
        }
    }

    // ------------------------------------------------------------------ //
    // Feier (seit 2026-09-27)
    // ------------------------------------------------------------------ //

    /// <summary>Eine Runde hat die Bestzeit der Website geschlagen -- feiern, wenn gewollt.</summary>
    private void Feiern(LapAutoSubmit.Rekord r)
    {
        if (!_settings.CelebrateRecord) { return; }
        var vorsprung = (r.BestenlisteMs - r.LapMs) / 1000.0;
        var detail = string.Join(" · ", new[] { r.Track, r.CarName, r.Klasse }
                                            .Where(x => !string.IsNullOrWhiteSpace(x)));
        ZeigeFeier(new CelebrationHud.Anlass(
            Loc.T("You beat the leaderboard!"),
            RivalsAdvisor.LapText(r.LapMs),
            vorsprung,
            string.Format(Loc.T("Website best {0}"), RivalsAdvisor.LapText(r.BestenlisteMs)),
            detail));
        LogLap($"record: {RivalsAdvisor.LapText(r.LapMs)} beats the website's {RivalsAdvisor.LapText(r.BestenlisteMs)} on {r.Track}");
    }

    /// <summary>
    /// Eine eingereichte Runde hat ein Auto NEU auf eine Bestenliste gebracht -- die
    /// ruhigere Feier mit Dank. Oeffentlich: das Nachreichen im Rivals-Tab meldet sich
    /// ebenfalls hier.
    /// </summary>
    /// <remarks>
    /// Eine NACHGEREICHTE Runde nur, waehrend Forza laeuft: sonst stuende die Feier
    /// Stunden spaeter ueber dem Schreibtisch, ohne Zusammenhang mit irgendetwas.
    /// </remarks>
    public void NeuesAutoFeiern(LapAutoSubmit.NeuesAuto r)
    {
        LogLap($"new car on the leaderboard: {r.CarName ?? AutoName(r.CarOrdinal)} on {r.Track} ({r.Klasse})");
        if (!_settings.CelebrateNewCar) { return; }
        if (r.Nachgereicht && !_game.Running) { return; }
        var auto = r.CarName ?? AutoName(r.CarOrdinal);
        var detail = string.Join(" · ", new[] { r.Track, auto, r.Klasse }.Where(x => !string.IsNullOrWhiteSpace(x)));
        ZeigeFeier(new CelebrationHud.Anlass(
            Loc.T("New car on the leaderboard!"),
            RivalsAdvisor.LapText(r.LapMs), 0,
            Loc.T("Its first time here on the website -- thanks to you!"),
            detail, CelebrationHud.FeierArt.NeuesAuto, Loc.T("NEW")));
    }

    /// <summary>"Try it" fuer die Meldung "neues Auto" -- mit Beispielwerten.</summary>
    public void NeuesAutoProbe() => ZeigeFeier(new CelebrationHud.Anlass(
        Loc.T("New car on the leaderboard!"), "1:31.208", 0,
        Loc.T("Its first time here on the website -- thanks to you!"),
        Loc.T("Example: this is how it looks when your lap adds a new car to the leaderboard."),
        CelebrationHud.FeierArt.NeuesAuto, Loc.T("NEW")));

    /// <summary>
    /// Der Knopf "Try it": dieselbe Feier mit Beispielwerten -- auch bei abgeschalteter
    /// Feier, denn danach wurde ausdruecklich gefragt. Der Ton folgt seinem Schalter.
    /// </summary>
    public void FeierProbe() => ZeigeFeier(new CelebrationHud.Anlass(
        Loc.T("You beat the leaderboard!"), "1:23.456", 0.556,
        string.Format(Loc.T("Website best {0}"), "1:24.012"),
        Loc.T("Example: this is how it looks when a lap beats the website's time.")));

    private void ZeigeFeier(CelebrationHud.Anlass anlass)
    {
        try
        {
            FollowGameArea();
            _feier.Zeige(anlass, Environment.TickCount);
            if (_settings.CelebrateSound) { CelebrationSound.Play(anlass.Art); }
        }
        catch (Exception e)
        {
            WriteDiagnostic("Feier: " + e.Message);
        }
    }
}

/// <summary>Faehrt das Auto los? Laenger als drei Sekunden schneller als 30 km/h.</summary>
/// <remarks>
/// Seit 2026-09-27 nimmt das die Autonotiz weg -- auch in der freien Fahrt, wo kein
/// Rennstart es tut. Ein kurzes Anrollen oder Rangieren am Startplatz zaehlt nicht:
/// die Geschwindigkeit muss durchgehend darueber bleiben.
/// </remarks>
internal sealed class LosfahrWaechter
{
    public const double GrenzeMs = 30 / 3.6;
    public static readonly TimeSpan Dauer = TimeSpan.FromSeconds(3);

    private DateTime _seit = DateTime.MinValue;

    /// <summary>Ein Paket: true, sobald die Dauer ueberschritten ist (und danach bei jedem schnellen Paket).</summary>
    public bool Update(double geschwindigkeitMs, DateTime jetzt)
    {
        if (geschwindigkeitMs <= GrenzeMs)
        {
            _seit = DateTime.MinValue;
            return false;
        }
        if (_seit == DateTime.MinValue)
        {
            _seit = jetzt;
            return false;
        }
        return jetzt - _seit > Dauer;
    }
}
