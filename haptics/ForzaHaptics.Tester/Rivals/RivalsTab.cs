using System.Diagnostics;
using System.Drawing;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// The Rivals overlay's own tab: switch it on, bind its buttons, check its masks.
/// </summary>
/// <remarks>
/// The settings live in <c>config/overlay.json</c>, the same file the Python tooling
/// reads, so a mask checked with <c>scripts/screen_reader.py --boxes</c> is the mask
/// this app uses. Every change here is written straight back to that file.
/// </remarks>
internal sealed class RivalsTab : UserControl
{
    private static readonly Color Panel = Color.FromArgb(32, 35, 41);
    private static readonly Color Ink = Color.WhiteSmoke;
    private static readonly Color Dim = Color.FromArgb(150, 158, 172);

    private readonly ITelemetryHost? _host;
    private readonly OverlaySettings _settings;
    // Beide sind nicht mehr readonly: der Datensatz kann sich unter der laufenden
    // App aendern -- vom Server geholt, oder weil ein Sweep auf diesem Rechner
    // gerade ein Board fertig gemacht hat.
    private RivalsAdvisor? _advisor;
    private string? _datasetPath;
    private readonly Label _dataSource = Caption("", 9.5f, Dim);
    private readonly Label _updateState = Caption("", 9.5f, Dim);
    private FileSystemWatcher? _watcher;
    private DateTime _lastReload = DateTime.MinValue;
    private readonly Label _status = Caption("", 10, Dim);
    private readonly Label _lastRead = Caption("", 9.5f, Dim);
    private readonly Label _telemetry = Caption("", 9.5f, Dim);
    private readonly Button _toggle;
    // Reicht Runden nach, die warten mussten (LapQueue) -- unabhaengig vom
    // Controller, den es erst gibt, wenn das Overlay einmal lief.
    private readonly LapAutoSubmit _nachreicher;

    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public OverlayController? Controller { get; private set; }

    /// <summary>The one loaded copy of <c>config/overlay.json</c>.</summary>
    /// <remarks>
    /// Handed out so the main form can read the same file without loading it a
    /// second time -- two instances would write over each other's changes, and the
    /// file is saved from this tab on every edit.
    /// </remarks>
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public OverlaySettings Settings => _settings;

    public RivalsTab(ITelemetryHost? host = null)
    {
        _host = host;
        Dock = DockStyle.Fill;
        BackColor = Panel;
        ForeColor = Ink;
        AutoScroll = true;

        _settings = OverlaySettings.Load();
        // Wo die Overlays erscheinen, gilt ab dem ersten Fenster (siehe OverlayAusgabe).
        // Im Konsolenmodus liegt nichts ueber diesem Schirm -- das Dashboard zeigt es.
        OverlayAusgabe.SetzeImSpiel(_settings.OverlayInGame && !_settings.ConsoleMode);
        _nachreicher = new LapAutoSubmit(() => _advisor, _settings, OverlayController.WriteLapLog);
        // Bringt eine NACHGEREICHTE Runde ein Auto neu auf die Liste, meldet das Overlay es.
        _nachreicher.NeuesAutoEingetragen += r =>
        {
            try { BeginInvoke(() => Controller?.NeuesAutoFeiern(r)); } catch (Exception) { }
        };

        // Two steps on purpose. First whatever is already on this disk, synchronously,
        // so the tab is usable the moment it appears; then the server, in the
        // background. Asking the network before showing anything would mean a tab that
        // hangs for as long as a firewall takes to drop a packet.
        // Which local file, and what to call it, is DatasetSync's decision -- it also
        // owns the fallbacks after a failed fetch, and two copies of that order is how
        // the shipped dataset ends up visible on one path and invisible on the other.
        var start = DatasetSync.LocalBest();
        _datasetPath = start.Path;
        LoadDataset(_datasetPath, start.Detail);

        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(18),
        };

        layout.Controls.Add(Caption("Rivals overlay", 17, Ink, "Segoe UI Semibold"));
        layout.Controls.Add(Caption(
            "Two panels over the game, from the same records as the website. They "
            + "answer different questions, so they are never up together: each has "
            + "its own button, and neither stays.", 9.5f, Dim, wrap: 720));

        _toggle = Button("Start overlay", 190);
        _toggle.Click += (_, _) => Toggle();
        layout.Controls.Add(Row(_toggle, _status));
        layout.Controls.Add(_telemetry);

        // Which numbers are on screen, and how old they are. Without this line a
        // stale copy and a fresh download look exactly the same -- and the panel
        // would answer "where does my car place" from last week's boards without
        // ever saying so.
        var refresh = Button("Refresh data", 130);
        refresh.Click += (_, _) => SyncFromServer(announce: true);
        var update = Button("Check for app updates", 190);
        update.Click += (_, _) => CheckForUpdate(announce: true);
        layout.Controls.Add(Row(refresh, _dataSource));
        layout.Controls.Add(Row(update, _updateState));

        // BESTE RUNDEN AN DIE SEITE -- siehe LapAutoSubmit. Voreingestellt AN
        // (so gewollt; der Hinweis beim ersten Start sagt es), hier abzuschalten.
        var einreichen = Check(Loc.T("Submit my laps when they beat the leaderboard"),
                               _settings.SubmitLaps);
        einreichen.CheckedChanged += (_, _) =>
        {
            _settings.SubmitLaps = einreichen.Checked;
            _settings.Save();
            // Eingeschaltet: was waehrenddessen gewartet hat, jetzt nachreichen.
            if (einreichen.Checked) { WartendeAnstossen(); }
        };
        var tag = new TextBox
        {
            Width = 190,
            Text = _settings.Gamertag ?? string.Empty,
            PlaceholderText = Loc.T("your gamertag (optional)"),
            MaxLength = 30,
            Margin = new Padding(12, 4, 0, 4),
        };
        tag.Leave += (_, _) =>
        {
            var neu = tag.Text.Trim();
            if (neu == (_settings.Gamertag ?? string.Empty)) { return; }
            _settings.Gamertag = neu;
            _settings.Save();
        };
        layout.Controls.Add(Row(einreichen, tag));
        layout.Controls.Add(Caption(Loc.T("Only a lap that is faster than that car's best leaderboard time is sent: your gamertag, the car, the route, the time and the lap's telemetry. The server checks it again before it appears on the site."),
                                    9f, Dim, wrap: 720));
        var submitState = Caption("", 9.5f, Dim, wrap: 720);
        layout.Controls.Add(submitState);
        // RUNDEN, DIE WARTEN (seit 2026-09-27): schneller als die Bestenliste, aber
        // nicht abzuschicken -- ausgeschaltet, offline, Server weg. Sie gehen
        // spaeter raus; wer das nicht will, wirft sie hier weg.
        var wartend = Caption("", 9.5f, Dim, wrap: 520);
        var verwerfen = Button(Loc.T("Discard waiting laps"), 190);
        verwerfen.Click += (_, _) =>
        {
            var n = LapQueue.Anzahl();
            if (n == 0) { return; }
            var ja = MessageBox.Show(this,
                string.Format(Loc.T("Discard {0} waiting lap(s)? They will not be submitted."), n),
                AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (ja == DialogResult.Yes) { LapQueue.AllesVergessen(); }
        };
        var wartendZeile = Row(verwerfen, wartend);
        wartendZeile.Visible = false;
        layout.Controls.Add(wartendZeile);
        var wartendTakt = 0;
        var submitPoll = new System.Windows.Forms.Timer { Interval = 1000 };
        submitPoll.Tick += (_, _) =>
        {
            if (wartendTakt++ % 5 == 0)
            {
                var n = LapQueue.Anzahl();
                wartendZeile.Visible = n > 0;
                wartend.Text = n == 0 ? string.Empty : string.Format(
                    Loc.T("{0} lap(s) beat the leaderboard and wait to be submitted. They are sent once submission is on and the server answers -- checked again against the leaderboard of that day."),
                    n);
            }
            var letzte = LapAutoSubmit.Last;
            submitState.Text = letzte is null
                ? (_settings.SubmitLaps && string.IsNullOrWhiteSpace(_settings.Gamertag)
                    ? Loc.T("Without a gamertag your laps appear on the website under a temporary player name. A gamertag entered later replaces it on all your laps, including the earlier ones.")
                    : string.Empty)
                : $"{LapAutoSubmit.LastAt:HH:mm} {letzte}";
        };
        submitPoll.Start();
        Disposed += (_, _) => submitPoll.Dispose();
        // Stuendlich nachsehen, solange Runden warten: die App kann jetzt tagelang
        // im Infobereich laufen, und "irgendwann erreichbar" soll auch dann reichen.
        var nachreichUhr = new System.Windows.Forms.Timer { Interval = 60 * 60 * 1000 };
        nachreichUhr.Tick += (_, _) => WartendeAnstossen();
        nachreichUhr.Start();
        Disposed += (_, _) => nachreichUhr.Dispose();
        WatchLocalDataset();
        // At startup, quietly: an unreachable server is the normal case and must not
        // put an error in front of someone who only wanted the overlay.
        //
        // NOT here in the constructor, though it reads better there: BeginInvoke needs
        // a window handle, and inside a control's constructor there is none yet -- it
        // throws, the exception leaves MainForm's constructor, and the whole app dies
        // before a single window appears. It did exactly that from the day this tab was
        // added until 2026-08-27, unnoticed, because every test since is headless.
        var synced = false;
        HandleCreated += (_, _) =>
        {
            if (synced) return;
            synced = true;
            BeginInvoke(new Action(() => SyncFromServer(announce: false)));
            // Und die App selbst. Still, aber mit Rueckfrage, sobald wirklich
            // etwas Neues da ist -- siehe CheckForUpdate.
            BeginInvoke(new Action(() => CheckForUpdate(announce: false)));
        };
        // The stream is the app's, not the overlay's: shown here so the port never
        // has to be looked for, and set in exactly one place -- the telemetry tab.
        var poll = new System.Windows.Forms.Timer { Interval = 1000 };
        poll.Tick += (_, _) => UpdateTelemetryLine();
        poll.Start();
        Disposed += (_, _) => poll.Dispose();

        layout.Controls.Add(Gap());
        layout.Controls.Add(Caption("What it shows", 11, Ink, "Segoe UI Semibold"));

        var points = Radio("Points — beating the field on every route", true);
        var times = Radio("Time sum — raw total pace", false);
        points.Checked = _settings.ShowsPoints;
        times.Checked = !_settings.ShowsPoints;
        points.CheckedChanged += (_, _) =>
        {
            if (points.Checked)
            {
                _settings.ScoreMode = "points";
                _settings.Save();
            }
        };
        times.CheckedChanged += (_, _) =>
        {
            if (times.Checked)
            {
                _settings.ScoreMode = "time";
                _settings.Save();
            }
        };
        layout.Controls.Add(points);
        layout.Controls.Add(times);
        layout.Controls.Add(Caption(
            "One list, never both: a glance mid-menu can only hold one order.",
            9f, Dim, wrap: 720));

        var seconds = Spin(_settings.ShowSeconds, 3, 600);
        seconds.ValueChanged += (_, _) =>
        {
            _settings.ShowSeconds = (double)seconds.Value;
            _settings.Save();
        };
        layout.Controls.Add(Row(Caption("Seconds on screen per press", 10, Ink, width: 260),
                                seconds));

        var auto = Check("Show the route panel by itself when it recognises the screen",
                         _settings.AutoShow);
        auto.CheckedChanged += (_, _) =>
        {
            _settings.AutoShow = auto.Checked;
            _settings.Save();
        };
        layout.Controls.Add(auto);

        layout.Controls.Add(Gap());
        layout.Controls.Add(Caption("Buttons", 11, Ink, "Segoe UI Semibold"));
        layout.Controls.Add(KeyRow("What to drive", _settings.HotkeyRight,
                                   v => _settings.HotkeyRight = v,
                                   _settings.GamepadRight,
                                   v => _settings.GamepadRight = new List<string> { v }));
        layout.Controls.Add(KeyRow("Your car", _settings.HotkeyLeft,
                                   v => _settings.HotkeyLeft = v,
                                   _settings.GamepadLeft,
                                   v => _settings.GamepadLeft = new List<string> { v }));
        layout.Controls.Add(KeyRow("Switch points / time", _settings.HotkeyScore,
                                   v => _settings.HotkeyScore = v, null, null));
        layout.Controls.Add(KeyRow("Pin the panel that is up", _settings.HotkeyPin,
                                   v => _settings.HotkeyPin = v, null, null));

        layout.Controls.Add(Gap());
        layout.Controls.Add(Caption("Reading the screen", 11, Ink, "Segoe UI Semibold"));
        layout.Controls.Add(Caption(
            "Only two masked regions are read: the 01/02/03 route list on the left, "
            + "and the lone class badge in the card's top-right corner. The card's "
            + "lower half prints the featured car's own class and PI, which on a Spec "
            + "Racing event is the spec car and not the restriction — reading it "
            + "would answer the wrong question. The masks are fractions of the "
            + "screen, so they hold at any resolution.", 9.5f, Dim, wrap: 720));

        var readNow = Button("Read the screen now", 190);
        readNow.Click += (_, _) => ReadNow(false);
        var readFull = Button("Read the whole screen", 190);
        readFull.Click += (_, _) => ReadNow(true);
        var preview = Button("Save mask preview", 190);
        preview.Click += (_, _) => SavePreview();
        layout.Controls.Add(Row(readNow, readFull, preview));
        layout.Controls.Add(_lastRead);

        layout.Controls.Add(Gap());
        layout.Controls.Add(Caption(
            "One socket, one port: the overlay reads the very same telemetry the "
            + "haptics use, so nothing has to be started twice and there is no "
            + "second port to keep in step.  "
            + "The game must run BORDERLESS windowed: an exclusive-fullscreen swap "
            + "chain draws over every other window, so no overlay of any kind can "
            + "appear on top of it.", 9f, Dim, wrap: 720));

        Controls.Add(layout);
        UpdateStatus();
        UpdateTelemetryLine();

        // Den Controller gibt es ab sofort IMMER, auch ohne gestartetes Overlay.
        //
        // Warum: die Telemetrie geht ueber `Controller.OnTelemetry` -- und dort haengt
        // seit dem 2026-09-12 auch die Rundenaufzeichnung samt Delta-Streifen. Ohne
        // Controller kam nie ein Paket an, und der Streifen konnte gar nicht
        // erscheinen; genau so ist das erste Rennen des Nutzers ohne jede Anzeige
        // geblieben. Die PANELS bleiben am Knopf -- nur der stille Teil laeuft mit.
        EnsureController();

        // --overlay starts it without a click: for launching straight into a play
        // session, and for checking the panels without a hand on the mouse.
        // Und ebenso, wenn es beim letzten Beenden lief (OverlayRunning) -- sonst ist es
        // nach jedem Update aus.
        if (Environment.GetCommandLineArgs()
                .Contains("--overlay", StringComparer.OrdinalIgnoreCase)
            || _settings.OverlayRunning)
        {
            HandleCreated += (_, _) => BeginInvoke(() =>
            {
                if (Controller is not { Running: true })
                {
                    Toggle();
                }
            });
        }
    }

    // ------------------------------------------------------------------ //

    private void Toggle()
    {
        if (Controller is { Running: true })
        {
            Controller.Stop();
            _toggle.Text = Loc.T("Start overlay");
            // Von Hand ausgeschaltet: beim naechsten Start aus lassen.
            _settings.OverlayRunning = false;
            _settings.Save();
            UpdateStatus();
            return;
        }
        if (_advisor is null)
        {
            _status.Text = _datasetPath is null
                ? "no records to answer from — check the server address, or unzip the "
                  + "package again: it ships data/analytics/laps.json"
                : $"could not load {_datasetPath}";
            return;
        }
        Controller ??= Create();
        // One button: the panel is useless without the stream, and the stream has
        // exactly one owner, so starting the overlay starts it if it is not up.
        _host?.EnsureTelemetryStarted();
        Controller.Start();
        _toggle.Text = Loc.T("Stop overlay");
        _settings.OverlayRunning = true;
        _settings.Save();
        UpdateStatus();
    }

    /// <summary>Load a dataset file and say where it came from. Never throws.</summary>
    private void LoadDataset(string? path, string origin)
    {
        if (path is null || !File.Exists(path))
        {
            _advisor = null;
            _dataSource.Text = Loc.T("no dataset — set dataset_url, or restore the shipped ")
                               + "data/analytics/laps.json";
            return;
        }
        try
        {
            _advisor = new RivalsAdvisor(RivalsDataset.Load(path));
            _datasetPath = path;
            _dataSource.Text =
                $"{_advisor.BoardCount} boards · {origin} · "
                + File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm");
        }
        catch (Exception exception)
        {
            // Keep the advisor that already works. A dataset half-written by a
            // rebuild is the common case for the watcher below, and dropping the
            // working one for it would make the panel worse, not more current.
            _dataSource.Text = _advisor is null
                ? $"dataset failed to load: {exception.Message}"
                : $"{_advisor.BoardCount} boards · kept the previous one "
                  + $"({exception.Message})";
        }
    }

    /// <summary>
    /// Swap in a newly loaded dataset while the overlay may be running.
    /// </summary>
    /// <remarks>
    /// The controller takes its advisor once, in its constructor, so a new dataset
    /// means a new controller. If the overlay was up it is put back up -- silently
    /// dropping it because data arrived would be a surprising way to lose the panel
    /// mid-race.
    /// </remarks>
    private void ApplyDataset(string? path, string origin)
    {
        var wasRunning = Controller?.Running == true;
        var previous = _advisor;
        LoadDataset(path, origin);
        if (ReferenceEquals(previous, _advisor)) return;

        Controller?.Stop();
        Controller?.Dispose();
        Controller = null;
        if (wasRunning && _advisor is not null)
        {
            Controller = Create();
            _host?.EnsureTelemetryStarted();
            Controller.Start();
        }
        UpdateStatus();
    }

    /// <summary>
    /// Den Bestand abgleichen, auch wenn diese Karteikarte nie geoeffnet wurde.
    /// </summary>
    /// <remarks>
    /// Der Abgleich hing allein an <c>HandleCreated</c>. Ein WinForms-TabPage bekommt
    /// sein Handle aber erst, wenn es einmal ANGEZEIGT wurde -- wer die App startet und
    /// nie auf "Rivals overlay" klickt, lief dauerhaft auf altem Bestand. Gemessen am
    /// 2026-09-11: der Zwischenspeicher stand auf 492 Boards vom 09.09., waehrend der
    /// Server 542 anbot; vier Minuten mit laufender App aenderten daran nichts.
    ///
    /// Darum ruft das Hauptfenster dies beim Start auf. Zweimal schadet nicht: der
    /// Abgleich laedt nur, wenn die Version des Servers von der eigenen abweicht.
    /// </remarks>
    public void RefreshDatasetInBackground() => SyncFromServer(announce: false);

    /// <summary>Ask the server, in the background, and swap in anything newer.</summary>
    private async void SyncFromServer(bool announce)
    {
        if (announce) _dataSource.Text = Loc.T("checking the server ...");
        var result = await DatasetSync.SyncAsync(
            // ServerUrl statt DatasetUrl: sie ist leer, wenn der Nutzer offline
            // bleiben will, und eine leere Adresse heisst hier seit je "nicht fragen".
            _settings.ServerUrl,
            TimeSpan.FromSeconds(Math.Max(1, _settings.DatasetProbeSeconds)),
            TimeSpan.FromSeconds(Math.Max(5, _settings.DatasetDownloadSeconds)),
            progress: announce ? new Progress<string>(text => _dataSource.Text = text)
                               : null).ConfigureAwait(true);

        if (result.Path is null)
        {
            if (announce) _dataSource.Text = result.Detail;
            return;
        }
        // Nothing changed: keep the loaded data and just retitle it, so the line
        // still distinguishes "current" from "could not ask".
        if (result.Source == DatasetSync.Origin.Cache && result.Path == _datasetPath
            && _advisor is not null)
        {
            _dataSource.Text = $"{_advisor.BoardCount} boards · {result.Detail}";
        }
        else
        {
            ApplyDataset(result.Path, result.Detail);
        }
        // The server just answered, and the board loaded now is its newest: the
        // moment to send laps that had to wait (LapQueue) -- not a minute before.
        if (result.Reached && LapQueue.Anzahl() > 0)
        {
            _ = Task.Run(() => _nachreicher.NachreichenAsync());
        }
    }

    /// <summary>
    /// Laps are waiting and nothing on this side stops them: ask the server. If it
    /// answers, <see cref="SyncFromServer"/> sends them against the fresh board.
    /// </summary>
    private void WartendeAnstossen()
    {
        if (LapQueue.Anzahl() == 0 || _nachreicher.Hindernis() is not null) { return; }
        SyncFromServer(announce: false);
    }

    /// <summary>
    /// Nachsehen, ob es eine neuere App gibt -- und fragen, bevor etwas geschieht.
    /// </summary>
    /// <remarks>
    /// ## Warum gefragt wird
    ///
    /// Der Austausch beendet die App und startet sie neu. Waehrend eines Rennens ist
    /// das ein Uebergriff -- der Nutzer entscheidet, wann.
    ///
    /// ## Warum beim Start still
    ///
    /// Mit <c>announce: false</c> erscheint gar nichts, solange alles aktuell ist
    /// oder der Server nicht erreichbar ist. Beides ist der Normalfall und keine
    /// Nachricht wert. Nur wenn es wirklich eine andere Fassung gibt, kommt die
    /// Frage -- und zwar genau einmal je Programmlauf.
    ///
    /// ## Warum eine uebersprungene Fassung gemerkt wird
    ///
    /// Wer "nicht jetzt" sagt, meint nicht "frag mich in zwei Minuten nochmal".
    /// Die uebersprungene Kennung steht in den Einstellungen; erst die naechste
    /// Fassung fragt wieder.
    /// </remarks>
    private async void CheckForUpdate(bool announce)
    {
        if (announce) { _updateState.Text = Loc.T("asking the server ..."); }
        var befund = await AppUpdate.CheckAsync(
            _settings.ServerUrl,
            TimeSpan.FromSeconds(Math.Max(1, _settings.DatasetProbeSeconds)))
            .ConfigureAwait(true);

        if (!befund.Neuer || befund.Server is null)
        {
            _updateState.Text = announce
                ? befund.Text
                : (befund.EigeneKennung is null ? "" : "App: " + befund.Text);
            return;
        }

        var kennung = befund.Server.Build ?? "";
        if (!announce && string.Equals(_settings.SkippedUpdate, kennung,
                                       StringComparison.Ordinal))
        {
            _updateState.Text = Loc.T("a newer version is ready (skipped)");
            return;
        }

        _updateState.Text = befund.Text;
        var groesse = befund.Server.Bytes / 1e6;
        var antwort = MessageBox.Show(
            $"Es gibt eine neuere Fassung der App.\n\n"
            + $"    neu:    {befund.Server.Name}  ({groesse:0} MB)\n"
            + $"    gebaut: {befund.Server.BuiltAt}\n"
            + $"    deine:  {befund.EigeneKennung}\n\n"
            + "Jetzt herunterladen und ersetzen? Die App beendet sich dabei und "
            + "startet neu. Deine Einstellungen bleiben erhalten.\n\n"
            + "Ja = jetzt\nNein = beim naechsten Start wieder fragen\n"
            + "Abbrechen = diese Fassung ueberspringen",
            "Neue Fassung verfuegbar", MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question);

        if (antwort == DialogResult.Cancel)
        {
            _settings.SkippedUpdate = kennung;
            _settings.Save();
            _updateState.Text = Loc.T("this version stays skipped");
            return;
        }
        if (antwort != DialogResult.Yes)
        {
            _updateState.Text = Loc.T("later");
            return;
        }

        try
        {
            var zip = await AppUpdate.DownloadAsync(
                _settings.DatasetUrl!, befund.Server,
                TimeSpan.FromSeconds(Math.Max(60, _settings.DatasetDownloadSeconds)),
                new Progress<string>(t => _updateState.Text = t))
                .ConfigureAwait(true);

            _updateState.Text = Loc.T("replacing ...");
            AppUpdate.StageAndRestart(zip, new Progress<string>(t => _updateState.Text = t),
                                      befund.Server.Signature);
            // Der Helfer wartet auf das Ende dieses Prozesses. Also muss er kommen.
            Application.Exit();
        }
        catch (Exception e)
        {
            _updateState.Text = Loc.T("Update failed");
            MessageBox.Show(
                "Das Update ist nicht durchgelaufen. Es wurde NICHTS ersetzt -- "
                + "die App laeuft unveraendert weiter.\n\n" + e.Message,
                "Update fehlgeschlagen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// Notice when the dataset file on this machine is rewritten.
    /// </summary>
    /// <remarks>
    /// Only the scanning machine ever sees this fire -- a sweep rewrites the dataset
    /// every five boards, and before this the app kept whatever it read at startup.
    /// The rebuild writes the file in one go but not atomically, so a change is acted
    /// on after a short settle and never more than once a minute.
    /// </remarks>
    private void WatchLocalDataset()
    {
        var local = RivalsDataset.FindDefaultPath();
        if (!_settings.DatasetWatchLocal || local is null) return;
        try
        {
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(local)!,
                                             Path.GetFileName(local))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) =>
            {
                if (DateTime.UtcNow - _lastReload < TimeSpan.FromMinutes(1)) return;
                _lastReload = DateTime.UtcNow;
                try
                {
                    BeginInvoke(new Action(async () =>
                    {
                        await Task.Delay(3000).ConfigureAwait(true);
                        ApplyDataset(local, "rebuilt on this machine");
                    }));
                }
                catch (InvalidOperationException)
                {
                    // The tab is going away. Nothing to reload into.
                }
            };
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[dataset] watcher: {exception.Message}");
        }
    }

    /// <summary>
    /// Der Streifen soll sich zeigen, damit eine Einstellung zu sehen ist.
    /// </summary>
    /// <remarks>
    /// Im Rennen erscheint er von selbst. Wer aber gerade Farben waehlt, sitzt im
    /// Menue -- und ohne diesen Weg muesste er jede Aenderung im naechsten Rennen
    /// erraten.
    /// </remarks>
    public void PreviewHud(bool show)
    {
        EnsureController();
        if (show) { Controller?.ShowHudPreview(); } else { Controller?.HideHudPreview(); }
    }

    /// <summary>Die Feier einmal zeigen (Knopf "Try it" im Reiter "Lap delta HUD").</summary>
    /// <summary>Eine Feier fuer einen eigenen Rekord zeigen (ihr "Try it").</summary>
    public void PreviewPersonalRecord()
    {
        EnsureController();
        Controller?.PersoenlichProbe();
    }

    public void PreviewCelebration()
    {
        EnsureController();
        Controller?.FeierProbe();
    }

    private AufnahmeFenster? _aufnahme;

    /// <summary>Das Aufnahmefenster oeffnen -- oder das offene nach vorne holen.</summary>
    public void OeffneAufnahmefenster()
    {
        if (_aufnahme is null || _aufnahme.IsDisposed)
        {
            _aufnahme = new AufnahmeFenster(_settings);
            _aufnahme.FormClosing += (_, e) =>
            {
                // Nur wer es selbst schliesst, will es beim naechsten Start nicht wieder.
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    _settings.RecordingWindow = false;
                    _settings.Save();
                }
            };
            _aufnahme.Show();
            _settings.RecordingWindow = true;
            _settings.Save();
            return;
        }
        if (_aufnahme.WindowState == FormWindowState.Minimized) { _aufnahme.WindowState = FormWindowState.Normal; }
        _aufnahme.Activate();
    }

    /// <summary>Die Meldung "neues Auto" einmal zeigen (ihr eigener Knopf "Try it").</summary>
    public void PreviewNewCar()
    {
        EnsureController();
        Controller?.NeuesAutoProbe();
    }

    /// <summary>Den Controller anlegen, ohne die Panels zu starten.</summary>
    private void EnsureController()
    {
        if (Controller is not null || _advisor is null) { return; }
        Controller = Create();
        _host?.EnsureTelemetryStarted();
    }

    private OverlayController Create()
    {
        var controller = new OverlayController(this, _advisor!, _settings, _host);
        controller.StatusChanged += (_, _) =>
        {
            if (IsHandleCreated)
            {
                BeginInvoke(UpdateStatus);
            }
        };
        return controller;
    }

    private void UpdateTelemetryLine()
    {
        try
        {
            RefreshTelemetryLine();
        }
        catch (Exception)
        {
            // The host is still being built, or is going away: never let a status
            // line take the application down with it.
        }
    }

    private void RefreshTelemetryLine()
    {
        if (_host is null)
        {
            _telemetry.Text = Loc.T("telemetry: not connected to the app's listener");
            return;
        }
        _telemetry.Text = _host.TelemetryRunning
            ? $"telemetry: the app is listening on UDP {_host.TelemetryPort} — "
              + "this panel reads that same stream, there is no second port"
            : $"telemetry: the listener is stopped (port {_host.TelemetryPort}) — "
              + "starting the overlay starts it";
        _telemetry.ForeColor = _host.TelemetryRunning
            ? Color.FromArgb(126, 231, 135) : Dim;
    }

    private void UpdateStatus()
    {
        var boards = _advisor?.BoardCount ?? 0;
        var state = Controller is { Running: true } ? Controller.Status : "stopped";
        var ocr = Controller?.OcrAvailable == false ? " · NO OCR LANGUAGE" : string.Empty;
        _status.Text = $"{state} · {boards} boards{ocr}";
        _status.ForeColor = Controller is { Running: true } ? Color.FromArgb(126, 231, 135) : Dim;
    }

    private void ReadNow(bool full)
    {
        if (_advisor is null)
        {
            return;
        }
        Controller ??= Create();
        try
        {
            var state = Controller.ReadOnce(full);
            var routes = state.Tracks.Count > 0
                ? string.Join(", ", state.Tracks)
                : "none matched";
            _lastRead.Text = $"{state.ReadMilliseconds:0} ms · routes: {routes} · "
                             + $"class: {state.Klass ?? "none"}"
                             + (state.KlassSource.Length > 0 ? $" ({state.KlassSource})" : "")
                             + (state.Spec ? " · one-make event" : "")
                             + $" · usable: {(state.IsOffer ? "yes" : "no")}";
            _lastRead.ForeColor = state.IsOffer ? Color.FromArgb(126, 231, 135) : Dim;
            if (state.IsOffer)
            {
                Controller.RenderAdvice(state);
            }
        }
        catch (Exception exception)
        {
            _lastRead.Text = $"read failed: {exception.GetType().Name}: {exception.Message}";
            _lastRead.ForeColor = Color.FromArgb(240, 162, 46);
        }
    }

    private void SavePreview()
    {
        if (_advisor is null)
        {
            return;
        }
        Controller ??= Create();
        try
        {
            var path = Controller.SaveMaskPreview();
            _lastRead.Text = $"wrote {path} — the routes box must hold the 01/02/03 "
                             + "list, the class box only the badge";
            _lastRead.ForeColor = Dim;
        }
        catch (Exception exception)
        {
            _lastRead.Text = $"preview failed: {exception.Message}";
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Controller?.Dispose();
        }
        base.Dispose(disposing);
    }

    // ------------------------------------------------------------------ //
    // small builders, so the layout above stays readable
    // ------------------------------------------------------------------ //

    private static Label Caption(string text, float size, Color colour,
                              string family = "Segoe UI", int wrap = 0,
                              int width = 0)
    {
        var label = new Label
        {
            Text = text,
            Font = new Font(family, size),
            ForeColor = colour,
            AutoSize = wrap == 0 && width == 0,
            Margin = new Padding(0, 4, 0, 4),
        };
        if (wrap > 0)
        {
            label.MaximumSize = new Size(wrap, 0);
            label.AutoSize = true;
        }
        if (width > 0)
        {
            label.Size = new Size(width, 26);
            label.TextAlign = ContentAlignment.MiddleLeft;
        }
        return label;
    }

    private static Button Button(string text, int width) => new()
    {
        Text = text,
        Width = width,
        Height = 34,
        FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(50, 54, 62),
        ForeColor = Ink,
        Margin = new Padding(0, 6, 10, 6),
    };

    private static RadioButton Radio(string text, bool checkedByDefault) => new()
    {
        Text = text,
        AutoSize = true,
        Checked = checkedByDefault,
        ForeColor = Ink,
        Margin = new Padding(0, 2, 0, 2),
    };

    private static CheckBox Check(string text, bool value) => new()
    {
        Text = text,
        AutoSize = true,
        Checked = value,
        ForeColor = Ink,
        Margin = new Padding(0, 6, 0, 2),
    };

    private static NumericUpDown Spin(double value, int min, int max) => new()
    {
        Minimum = min,
        Maximum = max,
        Value = (decimal)Math.Clamp(value, min, max),
        Width = 90,
        BackColor = Color.FromArgb(50, 54, 62),
        ForeColor = Ink,
        Margin = new Padding(0, 4, 0, 4),
    };

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 2, 0, 2),
        };
        foreach (var control in controls)
        {
            control.Anchor = AnchorStyles.Left;
            row.Controls.Add(control);
        }
        return row;
    }

    private static Control Gap() => new Label { Height = 14, Width = 1, AutoSize = false };

    /// <summary>One action: a function key, and optionally a pad button.</summary>
    private Control KeyRow(string label, int currentVk, Action<int> setVk,
                           List<string>? currentPad, Action<string>? setPad)
    {
        var keys = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 90,
            BackColor = Color.FromArgb(50, 54, 62),
            ForeColor = Ink,
        };
        for (var i = 1; i <= 12; i++)
        {
            keys.Items.Add($"F{i}");
        }
        // F1 is virtual-key 112, so F8 (119) is index 7. Getting this off by one
        // showed F9 in the box while F8 was the key that actually worked.
        var index = currentVk - 112;
        keys.SelectedIndex = index is >= 0 and < 12 ? index : 7;
        keys.SelectedIndexChanged += (_, _) =>
        {
            setVk(112 + keys.SelectedIndex);
            _settings.Save();
        };

        var controls = new List<Control> { Caption(label, 10, Ink, width: 260), keys };

        if (setPad is not null)
        {
            var pads = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 150,
                BackColor = Color.FromArgb(50, 54, 62),
                ForeColor = Ink,
                Margin = new Padding(10, 0, 0, 0),
            };
            string[] names = { "BACK", "START", "LB", "RB", "LEFT_THUMB",
                               "RIGHT_THUMB", "DPAD_UP", "DPAD_DOWN", "DPAD_LEFT",
                               "DPAD_RIGHT", "(none)" };
            pads.Items.AddRange(names);
            var current = currentPad?.FirstOrDefault()?.ToUpperInvariant();
            pads.SelectedIndex = Math.Max(0, Array.IndexOf(names, current ?? "(none)"));
            pads.SelectedIndexChanged += (_, _) =>
            {
                var chosen = (string)pads.Items[pads.SelectedIndex]!;
                setPad(chosen == "(none)" ? string.Empty : chosen);
                _settings.Save();
            };
            controls.Add(pads);
        }

        return Row(controls.ToArray());
    }
}
