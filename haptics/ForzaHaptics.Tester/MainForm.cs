using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

using ForzaHaptics.Tester.Rivals;

namespace ForzaHaptics.Tester;

internal sealed class MainForm : Form, ITelemetryHost
{
    /// <summary>
    /// Nur ein Bild des Fensters (--main-preview, fuer Anleitungen): kein Controller,
    /// kein Empfang, kein Dashboard -- alles, was am ersten Zeigen haengt, bleibt aus --,
    /// und das Fenster nimmt niemandem den Fokus.
    /// </summary>
    internal static bool NurVorschau;

    /// <summary>Wurden beim Start die Controller-Reiter gebaut? null: noch kein Hauptfenster.</summary>
    internal static bool? ControllerReiterDa;

    protected override bool ShowWithoutActivation => NurVorschau;

    protected override void OnShown(EventArgs e)
    {
        if (NurVorschau) { return; }
        base.OnShown(e);
    }

    private static readonly Color WindowColor = Color.FromArgb(18, 20, 24);
    private static readonly Color PanelColor = Color.FromArgb(27, 30, 36);
    private static readonly Color MutedColor = Color.FromArgb(180, 185, 194);
    private static readonly Color SuccessColor = Color.FromArgb(112, 214, 142);
    private static readonly Color WarningColor = Color.FromArgb(242, 162, 96);
    private static readonly Color ErrorColor = Color.FromArgb(242, 112, 112);
    private static readonly Color AccentColor = Color.FromArgb(50, 111, 230);

    private readonly Label _controllerStatus;
    private readonly Button _reconnectButton;
    // Verknuepfungen aus dem Kopf des Fensters (2026-09-25). Siehe VerknuepfungenZeigen.
    private readonly Button _desktopKnopf;
    private readonly Button _anheftKnopf;
    private readonly Button _mitForzaKnopf;
    private readonly ToolTip _kopfTipps = new() { AutoPopDelay = 15000 };
    private System.Windows.Forms.Timer? _anheftUhr;
    private DateTime _anheftBis;
    private DateTime _verknuepfungGeprueft;
    private readonly ComboBox _controllerSelector;
    private readonly TrackBar _forceSlider;
    private readonly NumericUpDown _forceNumber;
    private readonly ComboBox _outputSelector;
    private readonly Button _testButton;
    private readonly Button _stopVibrationButton;
    private readonly Label _forceReadout;
    private readonly Label _countdownLabel;
    private readonly Label _testStatus;
    private readonly NumericUpDown _portNumber;
    private readonly Button _startTelemetryButton;
    private readonly Button _stopTelemetryButton;
    private readonly Label _telemetryStatus;
    private readonly Label _packetStatus;
    private readonly Label _frontLeftSlip;
    private readonly Label _frontRightSlip;
    private readonly Label _rearLeftSlip;
    private readonly Label _rearRightSlip;
    private readonly Label _leftGripLabel;
    private readonly Label _rightGripLabel;
    private readonly ProgressBar _leftGripBar;
    private readonly ProgressBar _rightGripBar;
    private readonly Label _frontLeftRatio;
    private readonly Label _frontRightRatio;
    private readonly Label _rearLeftRatio;
    private readonly Label _rearRightRatio;
    private readonly Label _leftLockLabel;
    private readonly Label _rightLockLabel;
    private readonly ProgressBar _leftLockBar;
    private readonly ProgressBar _rightLockBar;
    private readonly System.Windows.Forms.Timer _controllerTimer;
    private readonly System.Windows.Forms.Timer _mappingTimer;
    private readonly SteamControllerHaptics _haptics = new();
    private readonly GenericGamepadHaptics _gamepads = new();
    private readonly DualSenseHaptics _dualSense = new();
    private readonly BezierCurveEditor _vibrationCurve;
    private readonly BezierCurveEditor _toneCurve;
    private readonly CheckBox _liveMappingCheck;
    private readonly CheckBox _toneEnabledCheck;
    private readonly NumericUpDown _vibrationFrequency;
    private readonly NumericUpDown _vibrationMaximum;
    private readonly NumericUpDown _toneLowFrequency;
    private readonly NumericUpDown _toneHighFrequency;
    private readonly NumericUpDown _toneMaximum;
    private readonly Label _mappingStatus;
    private readonly Label _mappingValues;
    private readonly BezierCurveEditor _lockCurve;
    private readonly CheckBox _lockMappingCheck;
    private readonly NumericUpDown _lockFrequency;
    private readonly NumericUpDown _lockMaximum;
    private readonly Label _lockMappingStatus;
    private readonly Label _lockMappingValues;
    private readonly SignalGraph _signalGraph = SignalGraph.CreateDefault();
    private readonly SignalGraphEvaluator _signalGraphEvaluator = new();
    private readonly BlueprintEditor _blueprintEditor;
    private readonly TelemetryInspector _telemetryInspector;

    private DateTime _vibrationEndsAt;
    private bool _vibrating;
    private UdpClient? _telemetryClient;
    private CancellationTokenSource? _telemetryCancellation;
    private ForzaPacket _latestTelemetry = null!;
    // The overlay reads the car from the SAME stream: Forza sends to one endpoint
    // and a UDP port takes one listener, so a separate process cannot have it.
    private RivalsTab? _rivals;
    // Wer die Hand vom Controller nimmt, wenn das Spiel nicht laeuft.
    private GameWatch? _game;
    private bool _releasedForIdleGame;
    private string? _connectedDevices;
    private DateTime _lastStatusRender = DateTime.MinValue;
    private DateTime _latestTelemetryAt;
    private bool _hasTelemetry;
    private bool _toneTestRunning;
    private double _lastPacketRate;
    private GraphEvaluationResult? _graphEvaluation;
    private DateTime _lastInspectorUpdate;
    private string _selectedControllerId = OutputSignalNode.SteamNativeTargetId;
    private bool _updatingControllerSelector;
    private Task<(bool Steam, bool Generic, bool DualSense)>? _hardwareConnectionTask;

    /// <param name="imHintergrund">
    /// Gestartet mit "--tray" (Autostart "Mit Forza starten"): unsichtbar im
    /// Infobereich beginnen und erst aufgehen, wenn Forza startet. Siehe HintergrundBeginnen.
    /// </param>
    public MainForm(bool imHintergrund = false)
    {
        _imHintergrund = imHintergrund;
        if (imHintergrund)
        {
            // Unsichtbar, aber ECHT gezeigt: an "Shown" haengen der Controller, die
            // Telemetrie und der zuletzt benutzte Graph. Ein nie gezeigtes Fenster
            // bekaeme nichts davon -- darum durchsichtig zeigen und dann verstecken.
            //
            // NICHT ShowInTaskbar = false: ohne Taskleisten-Knopf landet ein
            // minimiertes Fenster als kleine Titelleiste unten links auf dem
            // Schreibtisch, und zurueckschalten baut das Fenster neu auf (siehe
            // HintergrundBeginnen). Der Knopf blitzt bei der Anmeldung kurz auf.
            Opacity = 0;
            WindowState = FormWindowState.Minimized;
        }
        Text = AppInfo.Name;
        Icon = Marke.Symbol() ?? Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        ClientSize = new Size(1100, 700);
        MinimumSize = new Size(1000, 660);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Padding = new Padding(12);
        Font = new Font("Segoe UI", 10);
        BackColor = WindowColor;
        ForeColor = Color.WhiteSmoke;

        // 120 statt 112: rechts stehen seit 2026-09-25 zwei Zeilen (Controller, dann
        // Sprache und Verknuepfungen). Vorher sass die Sprachwahl links unter dem
        // Link bei y=76 -- in einer Flaeche von 78 Pixeln Hoehe, also unsichtbar bis
        // auf einen zwei Pixel hohen Strich.
        // 153 seit 2026-09-27: eine dritte Zeile rechts ("Start with Forza"), 27 Pixel
        // Knopf und 6 Abstand.
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 153,
            Padding = new Padding(10, 6, 0, 4)
        };

        var logo = new PictureBox
        {
            Image = Marke.Logo() ?? Icon?.ToBitmap(),
            SizeMode = PictureBoxSizeMode.Zoom,
            Dock = DockStyle.Left,
            Width = 64,
            Margin = new Padding(0)
        };

        var textPanel = new Panel
        {
            Dock = DockStyle.Fill
        };

        var title = new Label
        {
            Text = AppInfo.Name,
            Font = new Font("Segoe UI Semibold", 22),
            AutoSize = true,
            Location = new Point(8, 7)
        };

        // Eigene Zeile ueber die ganze Breite, nicht mehr neben dem Titel: zwischen
        // Auswahlliste und Knopf blieben 425 Pixel, und AutoEllipsis schnitt genau die
        // Auskunft ab, die jetzt hier steht -- naemlich WARUM nichts vibriert.
        _controllerStatus = new Label
        {
            Text = Loc.T("Looking for a Steam Controller..."),
            ForeColor = MutedColor,
            AutoEllipsis = true,
            Dock = DockStyle.Bottom,
            Height = 24,
            Padding = new Padding(10, 4, 8, 0),
            TextAlign = ContentAlignment.MiddleLeft
        };

        // ZWEI ZEILEN rechts: oben der Controller, darunter was das ganze Programm
        // betrifft -- Sprache, Schreibtisch, Taskleiste.
        var reconnectHost = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 635,
            Padding = new Padding(10, 4, 8, 0),
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false
        };
        var controllerZeile = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0)
        };
        var programmZeile = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 6, 0, 0)
        };
        _controllerSelector = new ComboBox
        {
            Width = 275,
            DropDownStyle = ComboBoxStyle.DropDownList,
            DisplayMember = nameof(ControllerOutputTarget.Name),
            Margin = new Padding(0, 3, 12, 0)
        };
        _controllerSelector.SelectedValueChanged += (_, _) =>
        {
            if (!_updatingControllerSelector &&
                _controllerSelector.SelectedItem is ControllerOutputTarget target)
            {
                SelectActiveController(target);
            }
        };
        var alwaysOnTop = new CheckBox
        {
            Text = Loc.T("Always on Top"),
            AutoSize = true,
            ForeColor = Color.WhiteSmoke,
            Margin = new Padding(0, 8, 12, 0)
        };
        alwaysOnTop.CheckedChanged += (_, _) => TopMost = alwaysOnTop.Checked;
        _reconnectButton = CreateButton(Loc.T("Reconnect controllers"), Point.Empty, new Size(172, 36));
        _reconnectButton.BackColor = Color.FromArgb(50, 54, 62);
        _reconnectButton.Click += async (_, _) => await ConnectControllerAsync();
        // NACHLESEN. Der Erklaerungstext kommt beim ersten Start und wird dann nie
        // wieder gezeigt -- eine Zustimmung, die man nicht wiederfinden kann, ist
        // nur halb ehrlich. Siehe Disclosure.
        var whatItDoes = new LinkLabel
        {
            Text = Loc.T("What this program does, and what leaves your computer"),
            AutoSize = true,
            Location = new Point(12, 52),
            LinkColor = Color.FromArgb(120, 200, 230),
            ActiveLinkColor = Color.White,
            LinkBehavior = LinkBehavior.HoverUnderline,
        };
        whatItDoes.LinkClicked += (_, _) => Disclosure.Zeigen(erstesMal: false);

        // DER SCHALTER PC / XBOX (seit 2026-09-28): oben, wo man ihn findet -- vorher
        // war es ein Haken im Reiter "Live grip telemetry", und danach ein Neustart von Hand.
        var imKonsolenModus = Rivals.OverlaySettings.Load().ConsoleMode;
        var modusSchalter = Konsole.Modusschalter(imKonsolenModus, ModusWechseln);
        modusSchalter.Location = new Point(12, 84);

        // SPRACHE. Vorgabe ist die von Windows; wer sie hier umstellt, bekommt die
        // Wahl beim naechsten Start wieder -- WinForms baut die schon gesetzten
        // Beschriftungen nicht von selbst neu, und sie zur Laufzeit alle
        // nachzuziehen waere viel Maschinerie fuer einen seltenen Klick.
        var sprachWahl = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 190,
            Margin = new Padding(0, 1, 10, 0),
        };
        sprachWahl.Items.Add(new SprachEintrag("auto", Loc.T("Language: system default")));
        foreach (var code in Loc.Verfuegbar())
        {
            sprachWahl.Items.Add(new SprachEintrag(code, SprachName(code)));
        }
        var jetzt = Rivals.OverlaySettings.Load();
        for (var i = 0; i < sprachWahl.Items.Count; i++)
        {
            if (sprachWahl.Items[i] is SprachEintrag e && e.Code == (jetzt.Language ?? "auto"))
            {
                sprachWahl.SelectedIndex = i;
                break;
            }
        }
        if (sprachWahl.SelectedIndex < 0) { sprachWahl.SelectedIndex = 0; }
        sprachWahl.SelectedIndexChanged += (_, _) =>
        {
            if (sprachWahl.SelectedItem is not SprachEintrag e) { return; }
            var s = Rivals.OverlaySettings.Load();
            if ((s.Language ?? "auto") == e.Code) { return; }
            s.Language = e.Code;
            s.Save();
            Loc.Waehle(e.Code);
            MessageBox.Show(this,
                Loc.T("The language changes when the program next starts."),
                Loc.T("Language"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        // Nur zeigen, wenn es ueberhaupt etwas zu waehlen gibt.
        sprachWahl.Visible = sprachWahl.Items.Count > 1;

        // SCHREIBTISCH UND TASKLEISTE -- jederzeit, nicht nur im Zustimmungsfenster
        // beim ersten Start. Wer dort nichts ankreuzte, fand den Weg sonst nie wieder.
        // Warum die Taskleiste ein Handgriff des Nutzers bleibt: Shortcuts.cs.
        _desktopKnopf = KopfKnopf(Loc.T("Desktop shortcut"));
        _desktopKnopf.Click += (_, _) => DesktopVerknuepfen();
        _kopfTipps.SetToolTip(_desktopKnopf, Loc.T("Puts a shortcut to this copy of the program on your desktop. One that points to a moved or deleted copy is replaced."));
        _anheftKnopf = KopfKnopf(Loc.T("Pin to taskbar"));
        _anheftKnopf.Click += (_, _) => AnTaskleiste();
        _kopfTipps.SetToolTip(_anheftKnopf, Loc.T("Windows leaves pinning to you. This prepares everything and shows the one click it takes."));
        // MIT FORZA STARTEN (seit 2026-09-27): eine eigene dritte Zeile -- in der
        // zweiten ist auf Franzoesisch kein Platz mehr (617 Pixel).
        _mitForzaKnopf = KopfKnopf(Loc.T("Start with Forza"));
        _mitForzaKnopf.Click += (_, _) => MitForzaUmschalten();
        _kopfTipps.SetToolTip(_mitForzaKnopf, Loc.T("Starts with Windows, waits invisibly in the notification area and opens when Forza starts. Click again to switch it off."));
        VerknuepfungenZeigen(sofort: true);
        // Beim Zurueckkehren ins Fenster neu nachsehen: die Verknuepfung wurde
        // vielleicht geloescht, die Anheftung vielleicht gerade gesetzt.
        Activated += (_, _) => VerknuepfungenZeigen(sofort: false);
        // IN JEDER SPRACHE PASSEND: erst beim Laden messen, wenn die Knoepfe die
        // Schrift des Fensters tragen und die DPI-Skalierung gilt.
        Load += (_, _) => KopfEinpassen(reconnectHost, sprachWahl, alwaysOnTop);

        controllerZeile.Controls.Add(_controllerSelector);
        controllerZeile.Controls.Add(alwaysOnTop);
        controllerZeile.Controls.Add(_reconnectButton);
        programmZeile.Controls.Add(sprachWahl);
        programmZeile.Controls.Add(_desktopKnopf);
        programmZeile.Controls.Add(_anheftKnopf);
        var startZeile = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 6, 0, 0)
        };
        startZeile.Controls.Add(_mitForzaKnopf);
        reconnectHost.Controls.Add(controllerZeile);
        reconnectHost.Controls.Add(programmZeile);
        reconnectHost.Controls.Add(startZeile);
        textPanel.Controls.Add(title);
        textPanel.Controls.Add(whatItDoes);
        textPanel.Controls.Add(modusSchalter);
        // Im Konsolenmodus haengt der Controller an der Konsole: keine Auswahl, kein
        // "Suche Steam Controller" -- sondern was hier stattdessen passiert.
        if (imKonsolenModus && !Rivals.OverlaySettings.Load().ControllerHier)
        {
            controllerZeile.Visible = false;
            _controllerStatus.Text = Loc.T("Xbox / 2nd PC mode: telemetry arrives over the network, the controller is on the console.");
        }
        header.Controls.Add(textPanel);
        header.Controls.Add(reconnectHost);
        header.Controls.Add(logo);
        // Zuletzt eingehaengt, damit das Andocken diese Zeile ZUERST bedient: sonst
        // teilen sich Logo und Knopfleiste die Breite vorher auf und die Zeile beginnt
        // wieder in der Mitte.
        header.Controls.Add(_controllerStatus);

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill
        };

        var testTab = new TabPage(Loc.T("Vibration test"))
        {
            BackColor = PanelColor,
            ForeColor = Color.WhiteSmoke,
            Padding = new Padding(18)
        };

        var telemetryTab = new TabPage(Loc.T("Live grip telemetry"))
        {
            BackColor = PanelColor,
            ForeColor = Color.WhiteSmoke,
            Padding = new Padding(18),
            // Das Xbox-Feld rechts ist hoeher als ein kleiner Schirm (seit 2026-09-29):
            // ohne Rollbalken war der Controller-Haken darunter nicht zu erreichen.
            AutoScroll = true,
        };

        var mappingTab = new TabPage("Mapping curves")
        {
            BackColor = PanelColor,
            ForeColor = Color.WhiteSmoke,
            Padding = new Padding(12)
        };

        var lockTab = new TabPage("Reifen blockieren")
        {
            BackColor = PanelColor,
            ForeColor = Color.WhiteSmoke,
            Padding = new Padding(12)
        };

        var inspectorTab = new TabPage(Loc.T("All telemetry + outputs"))
        {
            BackColor = PanelColor,
            ForeColor = Color.WhiteSmoke,
            Padding = new Padding(4)
        };

        var blueprintTab = new TabPage(Loc.T("Blueprint editor"))
        {
            BackColor = PanelColor,
            ForeColor = Color.WhiteSmoke,
            Padding = new Padding(0)
        };

        var rivalsTab = new TabPage(Loc.T("Rivals overlay"))
        {
            BackColor = PanelColor,
            ForeColor = Color.WhiteSmoke,
            Padding = new Padding(0)
        };

        var hudTab = new TabPage(Loc.T("Lap delta HUD"))
        {
            BackColor = PanelColor,
            ForeColor = Color.WhiteSmoke,
            Padding = new Padding(0)
        };

        var tunesTab = new TabPage(Loc.T("Tunes"))
        {
            BackColor = PanelColor,
            ForeColor = Color.WhiteSmoke,
            Padding = new Padding(0)
        };

        var carNotesTab = new TabPage(Loc.T("Car notes"))
        {
            BackColor = PanelColor,
            ForeColor = Color.WhiteSmoke,
            Padding = new Padding(0)
        };

        var carCollectionTab = new TabPage(Loc.T("Car collection"))
        {
            BackColor = PanelColor,
            ForeColor = Color.WhiteSmoke,
            Padding = new Padding(0)
        };

        var myTimesTab = new TabPage(Loc.T("My times"))
        {
            BackColor = PanelColor,
            ForeColor = Color.WhiteSmoke,
            Padding = new Padding(0)
        };

        var raceStatsTab = new TabPage(Loc.T("Race statistics"))
        {
            BackColor = PanelColor,
            ForeColor = Color.WhiteSmoke,
            Padding = new Padding(0)
        };
        var scanTab = new TabPage(Loc.T("Scan leaderboards"))
        {
            BackColor = PanelColor,
            ForeColor = Color.WhiteSmoke,
            Padding = new Padding(0)
        };
        var auctionTab = new TabPage(Loc.T("Auction House"))
        {
            BackColor = PanelColor,
            ForeColor = Color.WhiteSmoke,
            Padding = new Padding(0)
        };

        _telemetryInspector = new TelemetryInspector(ForzaPacket.AllDescriptors);
        inspectorTab.Controls.Add(_telemetryInspector);
        _blueprintEditor = new BlueprintEditor(
            _signalGraph,
            ForzaPacket.AllDescriptors,
            GetControllerTargets,
            () => _selectedControllerId);
        blueprintTab.Controls.Add(_blueprintEditor);
        // Den zuletzt benutzten Graphen gleich beim Start laden, nicht erst wenn
        // jemand den Reiter anklickt: der Graph entscheidet, WAS die Haptik ausgibt --
        // wer die App startet und losfaehrt, bekaeme sonst den Standard statt seiner
        // eigenen Abstimmung, ohne dass irgendetwas darauf hinweist.
        //
        // Danach der Schalter "Graph output enabled", wie er zuletzt stand (Vorgabe: an).
        // Bis 2026-09-26 begann er bei jedem Start aus -- eine frische Installation
        // vibrierte nie, bis jemand den Schalter im Blueprint-Editor fand.
        Shown += (_, _) =>
        {
            _blueprintEditor?.LoadLastGraph();
            _blueprintEditor?.SetOutputEnabled(_rivals?.Settings.HapticsGraphEnabled ?? true);
        };
        _blueprintEditor.OutputEnabledChanged += an =>
        {
            if (_rivals?.Settings is { } einstellungen)
            {
                einstellungen.HapticsGraphEnabled = an;
                einstellungen.Save();
            }
        };

        // Den Rundenbestand gleich beim Start abgleichen, nicht erst wenn jemand die
        // Rivals-Karteikarte anklickt -- sonst rechnet die App mit Zahlen von vorletzter
        // Woche, ohne dass irgendetwas darauf hinweist.
        Shown += (_, _) => _rivals?.RefreshDatasetInBackground();

        // KONSOLENMODUS: ohne Vibrationstest (kein Controller hier); Blueprint, Tuning und
        // Tunes folgen unten und fallen ebenso weg (Controller, Spielspeicher, Spielstand).
        var konsole = Rivals.OverlaySettings.Load().ConsoleMode;
        // Vibrationen gibt es, wo der Controller an diesem Rechner haengt: am PC immer,
        // auf der Xbox mit Remote Play (seit 2026-09-29).
        var controllerHier = Rivals.OverlaySettings.Load().ControllerHier;
        ControllerReiterDa = controllerHier;
        if (controllerHier) { tabs.TabPages.Add(testTab); }
        tabs.TabPages.Add(telemetryTab);
        tabs.TabPages.Add(inspectorTab);
        _rivals = new RivalsTab(this);
        rivalsTab.Controls.Add(_rivals);

        // Lage, Groesse und Farbe des Streifens gehoeren IN DIE APP, nicht in eine
        // Textdatei: der Nutzer hat die JSON-Schluessel nicht gefunden, und das ist
        // kein Vorwurf an ihn -- eine Einstellung, die man suchen muss, gibt es nicht.
        var hudEinstellungen = new Rivals.HudTab(
            _rivals.Settings,
            () => _rivals.Controller?.RefreshHud(),
            show => _rivals.PreviewHud(show));
        hudTab.Controls.Add(hudEinstellungen);
        hudEinstellungen.FeierProbe += () => _rivals.PreviewCelebration();
        hudEinstellungen.PersoenlichProbe += () => _rivals.PreviewPersonalRecord();
        hudEinstellungen.NeuesAutoProbe += () => _rivals.PreviewNewCar();
        hudEinstellungen.AufnahmeFensterWunsch += () => _rivals.OeffneAufnahmefenster();
        // War das Aufnahmefenster beim letzten Beenden offen, kommt es wieder.
        Shown += (_, _) =>
        {
            if (_rivals.Settings.RecordingWindow) { _rivals.OeffneAufnahmefenster(); }
        };
        // Wird die Stufe im Rennen per Taste gewechselt, muss der Reiter das zeigen --
        // sonst schreibt die naechste Aenderung hier die alte Stufe zurueck.
        if (_rivals.Controller is not null)
        {
            _rivals.Controller.DeltaModeChanged += (_, _) => hudEinstellungen.RefreshMode();
        }


        // Die Tunes aus dem Spielstand-Ordner: wie viele, welche auf keinem Auto liegen, wessen Tunes.
        var tunes = new Tuning.TunesTab(_rivals.Settings, () => _rivals.Controller?.Advisor);
        tunesTab.Controls.Add(tunes);

        var carNotes = new Rivals.CarNotesTab(
            () => _rivals.Controller?.Notes ?? new Rivals.CarNotes(),
            () => _rivals.Controller);
        carNotesTab.Controls.Add(carNotes);

        // Welche Autos fehlen noch, und wie kommt man an sie. Die Liste vom eigenen
        // Server, der Besitz aus der Garage (PC) oder aus Haken und gefahrenen Autos.
        var carCollection = new Rivals.CarCollectionTab(
            () => _rivals.Settings.ServerUrl,
            () => _rivals.Controller?.Notes ?? new Rivals.CarNotes(),
            () => _rivals.Controller?.Advisor,
            konsole);
        carCollectionTab.Controls.Add(carCollection);

        // Der Berater kommt vom Overlay-Regler: er haelt den geladenen Datensatz,
        // aus dem Name, Marke, Herkunft und Kategorie jedes Autos stammen. Ohne ihn
        // zeigt der Reiter trotzdem alle Zeiten, nur ohne diese Filter.
        var myTimes = new Rivals.OwnTimesTab(() => _rivals.Controller?.Advisor);
        myTimesTab.Controls.Add(myTimes);
        var raceStats = new Rivals.RaceStatsTab(() => _rivals.Controller?.Advisor);
        raceStatsTab.Controls.Add(raceStats);
        // Der Bestenlisten-Scanner braucht das Spiel auf DIESEM Rechner (Tasten, Bildschirm) --
        // im Konsolenmodus gibt es nichts zu scannen. Er startet nur auf Knopfdruck, und nicht,
        // waehrend gefahren wird.
        if (!konsole)
        {
            scanTab.Controls.Add(new Scan.ScanTab(_rivals.Settings, () =>
                TelemetryFlowing && _latestTelemetry is { } t && t.IsRaceOn && t.Get("Speed") > 2.0));

            // DAS AUKTIONSHAUS (seit 2026-10-04): der Reiter zeigt, was die Auktionswache weiss;
            // die Wache selbst laeuft ab hier im Hintergrund (Warnungen). In der Vorschau ohne
            // Takt -- ein Bild des Fensters warnt nicht.
            Auction.AuctionWatch.OhneTakt = NurVorschau;
            auctionTab.Controls.Add(new Auction.AuctionTab(Auction.AuctionWatch.SpielLaeuft));
            if (!NurVorschau) { AuktionsHinweiseVerdrahten(); }
        }
        // Beim Wechsel auf den Reiter nachsehen, ob das Spiel inzwischen laeuft --
        // sonst steht dort "Spiel laeuft nicht", waehrend es laengst laeuft.
        tabs.Selected += (_, e) =>
        {
            // Beim Oeffnen neu aufbauen: waehrend gefahren wurde, sind vielleicht
            // Autos dazugekommen, und eine Liste, die das erst beim Neustart zeigt,
            // sieht aus wie eine, die nichts gemerkt hat.
            if (ReferenceEquals(e.TabPage, tunesTab)) { tunes.Neu(); }
            if (ReferenceEquals(e.TabPage, carNotesTab)) { carNotes.Refresh(); }
            if (ReferenceEquals(e.TabPage, carCollectionTab)) { carCollection.Zeigen(); }
            if (ReferenceEquals(e.TabPage, myTimesTab)) { myTimes.Reload(); }
            if (ReferenceEquals(e.TabPage, raceStatsTab)) { raceStats.Reload(); }
        };

        if (controllerHier) { tabs.TabPages.Add(blueprintTab); }
        tabs.TabPages.Add(rivalsTab);
        tabs.TabPages.Add(hudTab);
        if (!konsole)
        {
            Erweiterung.Reiter(tabs, new Erweiterung.Umgebung(
                () => _latestTelemetry, _rivals.Settings, () => _rivals.Controller?.Advisor,
                () => _rivals.Controller?.Notes ?? new Rivals.CarNotes()));
            tabs.TabPages.Add(tunesTab);
        }
        tabs.TabPages.Add(carNotesTab);
        tabs.TabPages.Add(carCollectionTab);
        tabs.TabPages.Add(myTimesTab);
        tabs.TabPages.Add(raceStatsTab);
        if (!konsole)
        {
            tabs.TabPages.Add(scanTab);
            tabs.TabPages.Add(auctionTab);
        }
        if (Environment.GetCommandLineArgs()
                .Contains("--overlay", StringComparer.OrdinalIgnoreCase))
        {
            tabs.SelectedTab = rivalsTab;
        }

        var testTitle = new Label
        {
            Text = Loc.T("Test the exact vibration force"),
            Font = new Font("Segoe UI Semibold", 17),
            AutoSize = true,
            Location = new Point(22, 20)
        };

        var testExplanation = new Label
        {
            Text = Loc.T("This stays inside the final tool so you can calibrate the controller at any time."),
            ForeColor = MutedColor,
            AutoSize = true,
            Location = new Point(25, 57)
        };

        _forceReadout = new Label
        {
            Text = "20%",
            Font = new Font("Segoe UI Semibold", 40),
            AutoSize = true,
            Location = new Point(21, 92)
        };

        _forceSlider = new TrackBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 20,
            TickFrequency = 10,
            LargeChange = 10,
            SmallChange = 1,
            Location = new Point(19, 164),
            Size = new Size(510, 55)
        };
        _forceSlider.ValueChanged += (_, _) => SetForce(_forceSlider.Value);

        _forceNumber = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 100,
            Value = 20,
            TextAlign = HorizontalAlignment.Center,
            Location = new Point(550, 168),
            Size = new Size(75, 30)
        };
        _forceNumber.ValueChanged += (_, _) => SetForce((int)_forceNumber.Value);

        var percentLabel = new Label
        {
            Text = "%",
            AutoSize = true,
            Location = new Point(630, 172)
        };

        var outputLabel = new Label
        {
            Text = Loc.T("Haptic output"),
            AutoSize = true,
            Location = new Point(25, 230)
        };

        _outputSelector = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(139, 226),
            Size = new Size(165, 32)
        };
        _outputSelector.Items.AddRange([Loc.T("Both sides"), Loc.T("Left side"), Loc.T("Right side")]);
        _outputSelector.SelectedIndex = 0;

        _testButton = CreateButton(Loc.T("Vibrate for 10 seconds"), new Point(25, 285), new Size(260, 52));
        _testButton.BackColor = AccentColor;
        _testButton.Click += async (_, _) => await StartVibrationTestAsync();

        _stopVibrationButton = CreateButton(Loc.T("Stop now"), new Point(300, 285), new Size(145, 52));
        _stopVibrationButton.BackColor = Color.FromArgb(116, 42, 48);
        _stopVibrationButton.Enabled = false;
        _stopVibrationButton.Click += (_, _) => StopVibration(Loc.T("Stopped."));

        _countdownLabel = new Label
        {
            Text = Loc.T("Ready"),
            Font = new Font("Segoe UI Semibold", 16),
            AutoSize = true,
            Location = new Point(25, 360)
        };

        _testStatus = new Label
        {
            Text = Loc.T("Select a force and start the test."),
            ForeColor = MutedColor,
            AutoEllipsis = true,
            Location = new Point(26, 404),
            Size = new Size(630, 25)
        };

        testTab.Controls.AddRange([
            testTitle,
            testExplanation,
            _forceReadout,
            _forceSlider,
            _forceNumber,
            percentLabel,
            outputLabel,
            _outputSelector,
            _testButton,
            _stopVibrationButton,
            _countdownLabel,
            _testStatus
        ]);

        var telemetryTitle = new Label
        {
            Text = Loc.T("Verify FH6 grip data"),
            Font = new Font("Segoe UI Semibold", 17),
            AutoSize = true,
            Location = new Point(22, 18)
        };

        var telemetryInstructions = new Label
        {
            // Im Konsolenmodus nicht 127.0.0.1: das Spiel laeuft woanders und schickt hierher.
            Text = Rivals.OverlaySettings.Load().ConsoleMode
                ? string.Format(Loc.T("FH6 on the Xbox / other PC: Data Out On  |  IP {0}  |  Port must match below"),
                                Konsole.AdressenText())
                : Loc.T("FH6: Data Out On  |  IP 127.0.0.1  |  Format Sled  |  Port must match below"),
            ForeColor = MutedColor,
            AutoSize = true,
            Location = new Point(25, 54)
        };

        var portLabel = new Label
        {
            Text = Loc.T("UDP port"),
            AutoSize = true,
            Location = new Point(25, 101)
        };

        _portNumber = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 65535,
            Value = 5300,
            Location = new Point(102, 97),
            Size = new Size(90, 30)
        };

        _startTelemetryButton = CreateButton(Loc.T("Start listening"), new Point(214, 91), new Size(145, 42));
        _startTelemetryButton.BackColor = AccentColor;
        _startTelemetryButton.Click += async (_, _) => await StartTelemetryAsync();

        _stopTelemetryButton = CreateButton(Loc.T("Stop"), new Point(371, 91), new Size(95, 42));
        _stopTelemetryButton.BackColor = Color.FromArgb(116, 42, 48);
        _stopTelemetryButton.Enabled = false;
        _stopTelemetryButton.Click += (_, _) => StopTelemetry(Loc.T("Listener stopped."));

        _telemetryStatus = new Label
        {
            Text = Loc.T("Not listening."),
            ForeColor = MutedColor,
            AutoEllipsis = true,
            Location = new Point(25, 148),
            Size = new Size(630, 24)
        };

        _packetStatus = new Label
        {
            Text = Loc.T("Packets: --   Rate: --   Race: --"),
            ForeColor = MutedColor,
            AutoSize = true,
            Location = new Point(25, 178)
        };

        var slipHeading = new Label
        {
            Text = Loc.T("Normalized combined tire slip"),
            Font = new Font("Segoe UI Semibold", 12),
            AutoSize = true,
            Location = new Point(25, 218)
        };

        _frontLeftSlip = CreateValueLabel(string.Format(Loc.T("Front left: {0}"), "--"), new Point(26, 248));
        _frontRightSlip = CreateValueLabel(string.Format(Loc.T("Front right: {0}"), "--"), new Point(340, 248));
        _rearLeftSlip = CreateValueLabel(string.Format(Loc.T("Rear left: {0}"), "--"), new Point(26, 275));
        _rearRightSlip = CreateValueLabel(string.Format(Loc.T("Rear right: {0}"), "--"), new Point(340, 275));

        _leftGripLabel = CreateGripLabel(string.Format(Loc.T("Left grip: {0}"), "--"), new Point(25, 307));
        _rightGripLabel = CreateGripLabel(string.Format(Loc.T("Right grip: {0}"), "--"), new Point(340, 307));

        _leftGripBar = CreateGripBar(new Point(26, 340));
        _rightGripBar = CreateGripBar(new Point(341, 340));

        var lockHeading = new Label
        {
            Text = Loc.T("Wheel-lock detection from braking slip ratio"),
            Font = new Font("Segoe UI Semibold", 12),
            AutoSize = true,
            Location = new Point(25, 382)
        };

        _frontLeftRatio = CreateValueLabel(string.Format(Loc.T("Front left ratio: {0}"), "--"), new Point(26, 414));
        _frontRightRatio = CreateValueLabel(string.Format(Loc.T("Front right ratio: {0}"), "--"), new Point(340, 414));
        _rearLeftRatio = CreateValueLabel(string.Format(Loc.T("Rear left ratio: {0}"), "--"), new Point(26, 441));
        _rearRightRatio = CreateValueLabel(string.Format(Loc.T("Rear right ratio: {0}"), "--"), new Point(340, 441));

        _leftLockLabel = CreateGripLabel(string.Format(Loc.T("Left lock: {0}"), "--"), new Point(25, 473));
        _rightLockLabel = CreateGripLabel(string.Format(Loc.T("Right lock: {0}"), "--"), new Point(340, 473));
        _leftLockBar = CreateGripBar(new Point(26, 506));
        _rightLockBar = CreateGripBar(new Point(341, 506));

        var mappingNote = new Label
        {
            Text = Loc.T("The next tab will map these live grip values through your Bézier curve to the haptics."),
            ForeColor = MutedColor,
            AutoSize = true,
            Location = new Point(690, 506)
        };

        telemetryTab.Controls.Add(Konsole.Feld(_rivals!.Settings, new Point(700, 18), () => TelemetryPort,
                                               () => _rivals.OeffneAufnahmefenster()));
        telemetryTab.Controls.AddRange([
            telemetryTitle,
            telemetryInstructions,
            portLabel,
            _portNumber,
            _startTelemetryButton,
            _stopTelemetryButton,
            _telemetryStatus,
            _packetStatus,
            slipHeading,
            _frontLeftSlip,
            _frontRightSlip,
            _rearLeftSlip,
            _rearRightSlip,
            _leftGripLabel,
            _rightGripLabel,
            _leftGripBar,
            _rightGripBar,
            lockHeading,
            _frontLeftRatio,
            _frontRightRatio,
            _rearLeftRatio,
            _rearRightRatio,
            _leftLockLabel,
            _rightLockLabel,
            _leftLockBar,
            _rightLockBar
        ]);

        var mappingTitle = new Label
        {
            Text = Loc.T("Draw the grip → haptics curve"),
            Font = new Font("Segoe UI Semibold", 17),
            AutoSize = true,
            Location = new Point(18, 15)
        };

        var mappingInstructions = new Label
        {
            Text = Loc.T("Drag the yellow end points vertically and the grey Bézier handles anywhere. Left is 0% grip, right is 100%."),
            ForeColor = MutedColor,
            AutoSize = true,
            Location = new Point(21, 50)
        };

        var vibrationHeading = new Label
        {
            Text = Loc.T("Vibration in the grips"),
            Font = new Font("Segoe UI Semibold", 13),
            AutoSize = true,
            Location = new Point(20, 82)
        };

        var toneHeading = new Label
        {
            Text = Loc.T("Haptic tone on the trackpads"),
            Font = new Font("Segoe UI Semibold", 13),
            AutoSize = true,
            Location = new Point(535, 82)
        };

        _vibrationCurve = new BezierCurveEditor
        {
            Location = new Point(15, 112),
            HorizontalCaption = "Grip",
            VerticalCaption = "Vibration"
        };

        _toneCurve = new BezierCurveEditor
        {
            Location = new Point(530, 112),
            HorizontalCaption = "Grip",
            VerticalCaption = "Ton"
        };
        _toneCurve.ResetLinear();

        var resetVibrationButton = CreateButton(Loc.T("Reset vibration"), new Point(20, 374), new Size(185, 34));
        resetVibrationButton.BackColor = Color.FromArgb(50, 54, 62);
        resetVibrationButton.Click += (_, _) => _vibrationCurve.ResetInverted();

        var addVibrationPointButton = CreateButton(Loc.T("Point +"), new Point(215, 374), new Size(85, 34));
        addVibrationPointButton.BackColor = Color.FromArgb(45, 88, 72);
        addVibrationPointButton.Click += (_, _) => _vibrationCurve.AddPoint();

        var removeVibrationPointButton = CreateButton(Loc.T("Point -"), new Point(310, 374), new Size(85, 34));
        removeVibrationPointButton.BackColor = Color.FromArgb(105, 54, 58);
        removeVibrationPointButton.Click += (_, _) => _vibrationCurve.RemoveSelectedPoint();

        var resetToneButton = CreateButton(Loc.T("Reset tone"), new Point(535, 374), new Size(155, 34));
        resetToneButton.BackColor = Color.FromArgb(50, 54, 62);
        resetToneButton.Click += (_, _) => _toneCurve.ResetLinear();

        var addTonePointButton = CreateButton(Loc.T("Point +"), new Point(700, 374), new Size(80, 34));
        addTonePointButton.BackColor = Color.FromArgb(45, 88, 72);
        addTonePointButton.Click += (_, _) => _toneCurve.AddPoint();

        var removeTonePointButton = CreateButton(Loc.T("Point -"), new Point(790, 374), new Size(80, 34));
        removeTonePointButton.BackColor = Color.FromArgb(105, 54, 58);
        removeTonePointButton.Click += (_, _) => _toneCurve.RemoveSelectedPoint();

        _liveMappingCheck = new CheckBox
        {
            Text = Loc.T("Enable live mapping"),
            AutoSize = true,
            Location = new Point(22, 443),
            Font = new Font("Segoe UI Semibold", 11)
        };
        _toneEnabledCheck = new CheckBox
        {
            Text = Loc.T("Also enable the haptic tone"),
            AutoSize = true,
            Location = new Point(535, 443),
            Font = new Font("Segoe UI Semibold", 11)
        };

        var vibrationFrequencyLabel = CreateValueLabel("Vibrationsfrequenz", new Point(22, 481));
        _vibrationFrequency = CreateNumber(157, 477, 20, 800, 70);
        var vibrationHzLabel = CreateValueLabel("Hz", new Point(242, 481));

        var vibrationMaximumLabel = CreateValueLabel("Maximale Kraft", new Point(286, 481));
        _vibrationMaximum = CreateNumber(397, 477, 0, 100, 100);
        var vibrationPercentLabel = CreateValueLabel("%", new Point(482, 481));

        var toneRangeLabel = CreateValueLabel("Tonhöhe", new Point(535, 481));
        _toneLowFrequency = CreateNumber(600, 477, 20, 800, 180);
        var toneToLabel = CreateValueLabel("bis", new Point(684, 481));
        _toneHighFrequency = CreateNumber(712, 477, 20, 800, 520);
        var toneHzLabel = CreateValueLabel("Hz", new Point(797, 481));

        var toneMaximumLabel = CreateValueLabel("Max. Ton", new Point(837, 481));
        _toneMaximum = CreateNumber(918, 477, 0, 100, 45);
        var tonePercentLabel = CreateValueLabel("%", new Point(1003, 481));

        var toneTestButton = CreateButton(Loc.T("Test tone"), new Point(880, 374), new Size(140, 34));
        toneTestButton.BackColor = Color.FromArgb(87, 65, 145);
        toneTestButton.Click += async (_, _) => await TestToneAsync();

        _mappingValues = new Label
        {
            Text = Loc.T("Grip L/R: -- / --   Vibration: -- / --   Tone: -- / --"),
            ForeColor = MutedColor,
            Location = new Point(22, 520),
            Size = new Size(490, 24),
            AutoSize = false
        };

        _mappingStatus = new Label
        {
            Text = Loc.T("Live mapping off."),
            ForeColor = MutedColor,
            AutoEllipsis = true,
            Location = new Point(535, 520),
            Size = new Size(490, 24)
        };

        var audioExplanation = new Label
        {
            Text = Loc.T("There is no speaker: audible sound comes straight out of the haptic motors, so it sounds more like a synth than a recording."),
            ForeColor = WarningColor,
            AutoSize = false,
            Location = new Point(535, 410),
            Size = new Size(490, 35)
        };

        mappingTab.Controls.AddRange([
            mappingTitle,
            mappingInstructions,
            vibrationHeading,
            toneHeading,
            _vibrationCurve,
            _toneCurve,
            resetVibrationButton,
            addVibrationPointButton,
            removeVibrationPointButton,
            resetToneButton,
            addTonePointButton,
            removeTonePointButton,
            audioExplanation,
            _liveMappingCheck,
            _toneEnabledCheck,
            vibrationFrequencyLabel,
            _vibrationFrequency,
            vibrationHzLabel,
            vibrationMaximumLabel,
            _vibrationMaximum,
            vibrationPercentLabel,
            toneRangeLabel,
            _toneLowFrequency,
            toneToLabel,
            _toneHighFrequency,
            toneHzLabel,
            toneMaximumLabel,
            _toneMaximum,
            tonePercentLabel,
            toneTestButton,
            _mappingValues,
            _mappingStatus
        ]);

        var lockTitle = new Label
        {
            Text = Loc.T("Feel wheel lock separately"),
            Font = new Font("Segoe UI Semibold", 17),
            AutoSize = true,
            Location = new Point(20, 18)
        };

        var lockExplanation = new Label
        {
            Text = Loc.T("FH6 reports a slip ratio for each tyre. Negative slip under braking is normalised here to a lock level of 0-100%."),
            ForeColor = MutedColor,
            AutoSize = true,
            Location = new Point(23, 55)
        };

        _lockCurve = new BezierCurveEditor
        {
            Location = new Point(20, 95),
            HorizontalCaption = "Blockieren",
            VerticalCaption = "Vibration"
        };
        _lockCurve.ResetLinear();

        var resetLockButton = CreateButton(Loc.T("Reset curve"), new Point(25, 360), new Size(160, 36));
        resetLockButton.BackColor = Color.FromArgb(50, 54, 62);
        resetLockButton.Click += (_, _) => _lockCurve.ResetLinear();

        var addLockPointButton = CreateButton(Loc.T("Point +"), new Point(195, 360), new Size(90, 36));
        addLockPointButton.BackColor = Color.FromArgb(45, 88, 72);
        addLockPointButton.Click += (_, _) => _lockCurve.AddPoint();

        var removeLockPointButton = CreateButton(Loc.T("Point -"), new Point(295, 360), new Size(90, 36));
        removeLockPointButton.BackColor = Color.FromArgb(105, 54, 58);
        removeLockPointButton.Click += (_, _) => _lockCurve.RemoveSelectedPoint();

        var lockCurveHint = new Label
        {
            Text = Loc.T("Double-click: add a point  |  Right-click an inner point: remove it"),
            ForeColor = MutedColor,
            AutoSize = true,
            Location = new Point(25, 410)
        };

        _lockMappingCheck = new CheckBox
        {
            Text = Loc.T("Enable lock mapping"),
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 12),
            Location = new Point(575, 112)
        };
        _liveMappingCheck.CheckedChanged += (_, _) =>
        {
            if (!_liveMappingCheck.Checked && !_lockMappingCheck.Checked)
            {
                _haptics.StopAll();
            }

            if (!_liveMappingCheck.Checked)
            {
                _mappingStatus.Text = Loc.T("Live mapping off.");
                _mappingStatus.ForeColor = MutedColor;
            }
        };

        var lockFrequencyLabel = CreateValueLabel("Blockierfrequenz", new Point(577, 165));
        _lockFrequency = CreateNumber(710, 160, 20, 800, 140);
        var lockHzLabel = CreateValueLabel("Hz", new Point(795, 165));

        var lockMaximumLabel = CreateValueLabel("Maximale Kraft", new Point(577, 205));
        _lockMaximum = CreateNumber(710, 200, 0, 100, 100);
        var lockPercentLabel = CreateValueLabel("%", new Point(795, 205));

        var lockBehavior = new Label
        {
            Text = Loc.T("When a lock-up is detected, this signal overrides the normal grip vibration on the side of the car it happens on."),
            ForeColor = WarningColor,
            AutoSize = false,
            Location = new Point(577, 255),
            Size = new Size(400, 55)
        };

        _lockMappingValues = new Label
        {
            Text = Loc.T("Lock L/R: -- / --   Output: -- / --"),
            ForeColor = MutedColor,
            AutoSize = false,
            Location = new Point(577, 330),
            Size = new Size(420, 25)
        };

        _lockMappingStatus = new Label
        {
            Text = Loc.T("Lock mapping off."),
            ForeColor = MutedColor,
            AutoEllipsis = true,
            Location = new Point(577, 365),
            Size = new Size(420, 55)
        };

        _lockMappingCheck.CheckedChanged += (_, _) =>
        {
            if (!_lockMappingCheck.Checked && !_liveMappingCheck.Checked)
            {
                _haptics.StopAll();
            }

            if (!_lockMappingCheck.Checked)
            {
                _lockMappingStatus.Text = Loc.T("Lock mapping off.");
                _lockMappingStatus.ForeColor = MutedColor;
            }
        };

        lockTab.Controls.AddRange([
            lockTitle,
            lockExplanation,
            _lockCurve,
            resetLockButton,
            addLockPointButton,
            removeLockPointButton,
            lockCurveHint,
            _lockMappingCheck,
            lockFrequencyLabel,
            _lockFrequency,
            lockHzLabel,
            lockMaximumLabel,
            _lockMaximum,
            lockPercentLabel,
            lockBehavior,
            _lockMappingValues,
            _lockMappingStatus
        ]);

        Controls.Add(tabs);
        Controls.Add(header);

        _controllerTimer = new System.Windows.Forms.Timer { Interval = 20 };
        _controllerTimer.Tick += (_, _) => UpdateController();
        _controllerTimer.Start();

        _mappingTimer = new System.Windows.Forms.Timer { Interval = 20 };
        _mappingTimer.Tick += (_, _) =>
        {
            if (_hardwareConnectionTask is { IsCompleted: false })
            {
                return;
            }

            // Ein ausdruecklicher Test am Knopf laeuft immer -- da sitzt jemand davor
            // und hat gerade gedrueckt. Alles andere wartet auf das Spiel.
            var mayDrive = _vibrating || _toneTestRunning || GraphMayDrive;

            // Gerechnet wird auch im Leerlauf: die Werte im Editor und im
            // Telemetrie-Inspektor sollen weiterlaufen. Nur ausgegeben wird nichts --
            // sonst waere "still, weil das Spiel aus ist" von "kaputt" nicht zu
            // unterscheiden, und genau daran sucht man dann eine Stunde.
            UpdateGraphMapping(mayDrive);

            // Zweimal je Sekunde nachsehen, ob die Zeile noch stimmt. Der Uebergang
            // allein genuegt nicht: der GRUND wechselt auch ohne einen -- von "Forza
            // laeuft nicht" zu "Forza laeuft, aber Data Out ist aus" -- und wer beim
            // ersten Satz stehen bleibt, sucht am falschen Ende.
            if (DateTime.UtcNow - _lastStatusRender > TimeSpan.FromMilliseconds(500))
            {
                RenderControllerStatus();
            }

            if (!mayDrive)
            {
                ReleaseForIdleGame();
                return;
            }
            _releasedForIdleGame = false;

            if (_selectedControllerId == OutputSignalNode.SteamNativeTargetId)
            {
                _haptics.EnforceOwnedOutputs();
            }
            else if (IsSelectedDualSense())
            {
                Leistung.Pad(_dualSense.EnforceOwnedOutputs());
            }
            else
            {
                Leistung.Pad(_gamepads.EnforceOwnedOutputs());
            }
        };
        _mappingTimer.Start();

        Shown += async (_, _) =>
        {
            if (_rivals?.Settings.ConsoleMode == true)
            {
                // KONSOLENMODUS: dafuer das Dashboard -- ausser der HUD liegt ueber dem
                // Fenster von Remote Play (wie am PC). Mit Remote Play haengt der
                // Controller an diesem Rechner: dann wird er auch hier verbunden.
                if (_rivals.Settings.ControllerHier)
                {
                    // Die Vibration, die Remote Play von der Konsole weiterreicht, jeden
                    // Takt ueberschreiben -- wie das Rumpeln von Forza am PC.
                    if (_rivals.Settings.VibrationJedenTakt)
                    {
                        _gamepads.Auffrischen = TimeSpan.Zero;
                        _dualSense.Auffrischen = TimeSpan.Zero;
                    }
                    await ConnectControllerAsync();
                }
                _ = StartTelemetryAsync();
                if (!_rivals.Settings.HudUeberFenster) { _rivals.OeffneAufnahmefenster(); }
                return;
            }
            await ConnectControllerAsync();
            _ = StartTelemetryAsync();
        };
        FormClosing += (_, _) =>
        {
            StopTelemetry();
            StopVibration();
            _haptics.Dispose();
            _gamepads.Dispose();
            _dualSense.Dispose();
        };

        // ALS LETZTES an "Shown": erst laufen Controller, Telemetrie und Graph an,
        // dann geht das Fenster (im Hintergrundbetrieb) in den Infobereich.
        Shown += (_, _) => HintergrundBeginnen();
    }

    // ------------------------------------------------------------------ //
    // Mit Forza starten: unsichtbar im Infobereich warten (seit 2026-09-27)
    // ------------------------------------------------------------------ //

    private readonly bool _imHintergrund;
    private NotifyIcon? _tray;
    private System.Windows.Forms.Timer? _forzaWache;
    private GameWatch? _forzaSicht;
    private bool _forzaLief;
    private bool _wirklichBeenden;
    private bool _schliessHinweisGezeigt;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>Minimiert zeigen, OHNE das Fenster zu aktivieren (SW_SHOWMINNOACTIVE).</summary>
    private const int NurMinimiertZeigen = 7;

    /// <summary>
    /// Nach dem ersten, durchsichtigen Zeigen: Symbol in den Infobereich, auf Forza warten.
    /// </summary>
    /// <remarks>
    /// Alles andere laeuft wie immer weiter -- die Haptik und die Overlays warten
    /// ohnehin, bis Forza laeuft (siehe GraphMayDrive). Neu ist nur, dass das Fenster
    /// dabei nicht im Weg ist: unsichtbar, bis Forza startet, dann minimiert in der
    /// Taskleiste, damit man sieht, dass die App bereit ist -- und nach dem Spiel
    /// wieder unsichtbar, solange niemand das Fenster geoeffnet hat.
    ///
    /// Fenster ein- und ausblenden statt ShowInTaskbar umzuschalten: das Umschalten
    /// baut das Fenster in WinForms neu auf, und am Fenster haengen die
    /// Geraetebenachrichtigungen fuer den Controller.
    /// </remarks>
    private void HintergrundBeginnen()
    {
        if (!_imHintergrund || _tray is not null) { return; }
        _tray = new NotifyIcon
        {
            Icon = Marke.Symbol() ?? Icon,
            Text = AppInfo.Name,
            Visible = true,
            ContextMenuStrip = TrayMenue(),
        };
        _tray.DoubleClick += (_, _) => VonAussenZeigen();
        _forzaSicht = new GameWatch(_rivals?.Settings.ForzaProcess);
        _forzaLief = _forzaSicht.Running;
        if (_forzaLief) { MinimiertZeigen(); }
        else { Hide(); }
        Opacity = 1;
        TrayTextSetzen();
        _forzaWache = new System.Windows.Forms.Timer { Interval = 3000 };
        _forzaWache.Tick += (_, _) => ForzaPruefen();
        _forzaWache.Start();
    }

    private ContextMenuStrip TrayMenue()
    {
        var menue = new ContextMenuStrip();
        menue.Items.Add(Loc.T("Open FH Companion"), null, (_, _) => VonAussenZeigen());
        menue.Items.Add(new ToolStripSeparator());
        menue.Items.Add(Loc.T("Quit"), null, (_, _) =>
        {
            _wirklichBeenden = true;
            Close();
        });
        return menue;
    }

    private void TrayTextSetzen()
    {
        if (_tray is null) { return; }
        var text = AppInfo.Name + " · " + (_forzaLief ? Loc.T("Forza is running") : Loc.T("waiting for Forza"));
        // NotifyIcon.Text wirft ueber 63 Zeichen -- in manchen Sprachen ist der Satz lang.
        _tray.Text = text.Length > 63 ? text[..63] : text;
    }

    /// <summary>Alle drei Sekunden: Forza gestartet oder beendet?</summary>
    private void ForzaPruefen()
    {
        var laeuft = _forzaSicht?.Running == true;
        if (laeuft == _forzaLief) { return; }
        _forzaLief = laeuft;
        TrayTextSetzen();
        if (laeuft)
        {
            if (!Visible) { MinimiertZeigen(); }
        }
        else if (Visible && WindowState == FormWindowState.Minimized)
        {
            // Nur, wenn niemand das Fenster geoeffnet hat: wer gerade darin arbeitet,
            // dem nimmt das Spielende es nicht weg.
            Hide();
        }
    }

    /// <summary>Minimiert in die Taskleiste, ohne das Spiel aus dem Vordergrund zu holen.</summary>
    private void MinimiertZeigen()
    {
        WindowState = FormWindowState.Minimized;
        ShowWindow(Handle, NurMinimiertZeigen);
    }

    /// <summary>
    /// Das Fenster nach vorne holen: Doppelklick auf das Symbol, der Menuepunkt, oder
    /// ein zweiter Start derselben Kopie (siehe Einzelinstanz).
    /// </summary>
    public void VonAussenZeigen()
    {
        if (!Visible) { Show(); }
        if (WindowState == FormWindowState.Minimized) { WindowState = FormWindowState.Normal; }
        Activate();
        BringToFront();
    }

    /// <summary>
    /// Im Hintergrundbetrieb schliesst das X nur das Fenster -- die App wartet weiter.
    /// </summary>
    /// <remarks>
    /// Sonst liefe "Mit Forza starten" nur bis zum ersten Schliessen und dann erst
    /// wieder nach der naechsten Anmeldung. Beenden geht ueber das Menue am Symbol;
    /// Abmelden und Herunterfahren beenden immer. Ohne base-Aufruf laeuft der
    /// FormClosing-Ereignisweg gar nicht erst -- Telemetrie und Controller bleiben an.
    /// </remarks>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_imHintergrund && !_wirklichBeenden && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            if (!_schliessHinweisGezeigt && _tray is not null)
            {
                _schliessHinweisGezeigt = true;
                _tray.ShowBalloonTip(6000, AppInfo.Name,
                    Loc.T("Still waiting for Forza in the notification area. Right-click the icon to quit."),
                    ToolTipIcon.Info);
            }
            return;
        }
        base.OnFormClosing(e);
    }

    // ------------------------------------------------------------------ //
    // Hinweise der Auktionswache (seit 2026-10-04)
    // ------------------------------------------------------------------ //

    private bool _auktionVerdrahtet;
    private NotifyIcon? _hinweisSymbol;
    private System.Windows.Forms.Timer? _hinweisWeg;

    /// <summary>
    /// Die Warnungen 5 und 2 Minuten vor Ende: ins Meldungsfeld des Overlays (ueber dem Spiel) und
    /// als Sprechblase im Infobereich -- die kommt auch im Vollbild an, ohne dem Spiel den Fokus zu nehmen.
    /// </summary>
    /// <remarks>
    /// Die Wache ruft auf einem Hintergrundfaden; beides geht darum per BeginInvoke in den
    /// Fenster-Faden. Den Overlay-Regler erst dort nachschlagen: er wird bei jedem frischen
    /// Datensatz ersetzt.
    /// </remarks>
    private void AuktionsHinweiseVerdrahten()
    {
        _auktionVerdrahtet = true;
        var wache = Auction.AuctionWatch.Jetzt;
        wache.AufDemSchirm = (titel, text) => ImFensterFaden(() => _rivals?.Controller?.AuktionsHinweis(titel, text));
        wache.ImInfobereich = (titel, text) => ImFensterFaden(() => Sprechblase(titel, text));
    }

    private void ImFensterFaden(Action tun)
    {
        try
        {
            if (IsHandleCreated && !IsDisposed) { BeginInvoke(tun); }
        }
        catch (Exception)
        {
            // Ein Hinweis darf nichts anhalten -- auch nicht beim Schliessen der App.
        }
    }

    /// <summary>
    /// Eine Sprechblase im Infobereich. Im Hintergrundbetrieb am vorhandenen Symbol; sonst an einem
    /// eigenen, das nach 15 Sekunden wieder verschwindet (jeder neue Hinweis verlaengert).
    /// </summary>
    private void Sprechblase(string titel, string text)
    {
        try
        {
            // Windows schneidet nicht selbst ab, sondern wirft: Titel bis 63, Text bis 255 Zeichen.
            titel = titel.Length > 63 ? titel[..63] : titel;
            text = text.Length > 255 ? text[..255] : text;
            var symbol = _tray;
            if (symbol is null)
            {
                if (_hinweisSymbol is null)
                {
                    _hinweisSymbol = new NotifyIcon { Icon = Marke.Symbol() ?? Icon, Text = AppInfo.Name, Visible = true };
                    _hinweisSymbol.BalloonTipClicked += (_, _) => VonAussenZeigen();
                    _hinweisSymbol.DoubleClick += (_, _) => VonAussenZeigen();
                }
                if (_hinweisWeg is null)
                {
                    _hinweisWeg = new System.Windows.Forms.Timer { Interval = 15000 };
                    _hinweisWeg.Tick += (_, _) => HinweisSymbolWeg();
                }
                _hinweisWeg.Stop();
                _hinweisWeg.Start();
                symbol = _hinweisSymbol;
            }
            symbol.ShowBalloonTip(10000, titel, text, ToolTipIcon.Warning);
        }
        catch (Exception)
        {
        }
    }

    private void HinweisSymbolWeg()
    {
        _hinweisWeg?.Stop();
        if (_hinweisSymbol is null) { return; }
        // Sonst bleibt ein totes Symbol stehen, bis jemand mit der Maus darueberfaehrt.
        _hinweisSymbol.Visible = false;
        _hinweisSymbol.Dispose();
        _hinweisSymbol = null;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (_auktionVerdrahtet)
        {
            var wache = Auction.AuctionWatch.Jetzt;
            wache.AufDemSchirm = null;
            wache.ImInfobereich = null;
            wache.StoppeBieter();
        }
        HinweisSymbolWeg();
        _hinweisWeg?.Dispose();
        _forzaWache?.Stop();
        if (_tray is not null)
        {
            // Sonst bleibt ein totes Symbol stehen, bis jemand mit der Maus darueberfaehrt.
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
        base.OnFormClosed(e);
    }

    private static Button CreateButton(string text, Point location, Size size) =>
        new()
        {
            Text = text,
            Location = location,
            Size = size,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false
        };

    /// <summary>Ein kleiner Knopf fuer die Programmzeile im Kopf.</summary>
    private static Button KopfKnopf(string text)
    {
        var knopf = CreateButton(text, Point.Empty, Size.Empty);
        knopf.AutoSize = true;
        knopf.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        knopf.MinimumSize = new Size(0, 27);
        knopf.Padding = new Padding(6, 0, 6, 0);
        knopf.Margin = new Padding(0, 0, 8, 0);
        knopf.BackColor = Color.FromArgb(50, 54, 62);
        knopf.FlatAppearance.BorderColor = Color.FromArgb(80, 86, 98);
        return knopf;
    }

    /// <summary>
    /// Die beiden Knoepfe zeigen, was IST: ein Haekchen nur, wenn die Verknuepfung
    /// auf DIESE Kopie zeigt -- eine auf einen verschobenen Ordner zaehlt nicht --
    /// und wenn Windows diese Kopie wirklich angeheftet hat.
    /// </summary>
    /// <param name="sofort">
    /// <c>false</c> beim Aktivieren des Fensters: das feuert bei jedem Wechsel, und
    /// jede Pruefung liest Verknuepfungen ueber COM. Hoechstens alle zwei Sekunden.
    /// </param>
    private void VerknuepfungenZeigen(bool sofort)
    {
        if (!sofort && (DateTime.UtcNow - _verknuepfungGeprueft).TotalSeconds < 2) { return; }
        _verknuepfungGeprueft = DateTime.UtcNow;
        var desktop = Shortcuts.IsCurrent(ShortcutPlace.Desktop);
        var angeheftet = Shortcuts.IsPinned();
        var mitForza = Shortcuts.StartsWithForza();
        _desktopKnopf.Text = desktop ? Loc.T("Desktop shortcut ✓") : Loc.T("Desktop shortcut");
        _anheftKnopf.Text = angeheftet ? Loc.T("Pinned to taskbar ✓") : Loc.T("Pin to taskbar");
        _mitForzaKnopf.Text = mitForza ? Loc.T("Start with Forza ✓") : Loc.T("Start with Forza");
        var fertig = Color.FromArgb(130, 215, 150);
        _desktopKnopf.ForeColor = desktop ? fertig : Color.White;
        _anheftKnopf.ForeColor = angeheftet ? fertig : Color.White;
        _mitForzaKnopf.ForeColor = mitForza ? fertig : Color.White;
        if (angeheftet || DateTime.UtcNow > _anheftBis) { _anheftUhr?.Stop(); }
    }

    /// <summary>
    /// Beide Zeilen rechts im Kopf in jeder Sprache passend machen.
    /// </summary>
    /// <remarks>
    /// OBEN war "Reconnect controllers" fest 172 Pixel breit: auf Franzoesisch,
    /// Deutsch und Finnisch lief die Beschriftung aus dem Knopf, weil daneben "Always
    /// on Top" laenger wird. Jetzt ist der Knopf so breit wie sein Text, und die
    /// Controllerauswahl bekommt, was uebrig bleibt.
    ///
    /// UNTEN bekommen beide Knoepfe die Breite ihrer LAENGEREN Fassung (mit und ohne
    /// Haekchen) -- sonst springt die Zeile, sobald eines erscheint. Und die
    /// Sprachwahl gibt nach, wo die Beschriftungen lang sind: auf Franzoesisch heisst
    /// der Knopf "Épingler à la barre des tâches", und die Zeile ist 617 Pixel breit.
    /// </remarks>
    private void KopfEinpassen(Control spalte, ComboBox sprachWahl, CheckBox immerOben)
    {
        var innen = spalte.ClientSize.Width - spalte.Padding.Horizontal;

        _reconnectButton.AutoSize = true;
        _reconnectButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _reconnectButton.MinimumSize = new Size(150, _reconnectButton.Height);
        _reconnectButton.Padding = new Padding(8, 0, 8, 0);
        var knopf = Math.Max(150, _reconnectButton.GetPreferredSize(Size.Empty).Width);
        _controllerSelector.Width = Math.Clamp(
            innen - _controllerSelector.Margin.Horizontal
            - immerOben.GetPreferredSize(Size.Empty).Width - immerOben.Margin.Horizontal
            - knopf - _reconnectButton.Margin.Horizontal,
            180, 360);

        static int Breiteste(Button knopf, params string[] texte)
        {
            var vorher = knopf.Text;
            var breit = 0;
            foreach (var text in texte)
            {
                knopf.Text = text;
                breit = Math.Max(breit, knopf.GetPreferredSize(Size.Empty).Width);
            }
            knopf.Text = vorher;
            return breit;
        }
        var b1 = Breiteste(_desktopKnopf, Loc.T("Desktop shortcut"), Loc.T("Desktop shortcut ✓"));
        var b2 = Breiteste(_anheftKnopf, Loc.T("Pin to taskbar"), Loc.T("Pinned to taskbar ✓"));
        _desktopKnopf.MinimumSize = new Size(b1, _desktopKnopf.MinimumSize.Height);
        _anheftKnopf.MinimumSize = new Size(b2, _anheftKnopf.MinimumSize.Height);
        var frei = innen
                   - b1 - _desktopKnopf.Margin.Horizontal
                   - b2 - _anheftKnopf.Margin.Horizontal
                   - sprachWahl.Margin.Horizontal;
        sprachWahl.Width = Math.Clamp(frei, 110, sprachWahl.Width);
    }

    /// <summary>"Mit Forza starten" an oder aus: die Autostart-Verknuepfung mit "--tray".</summary>
    private void MitForzaUmschalten()
    {
        if (Shortcuts.StartsWithForza())
        {
            Shortcuts.Remove(ShortcutPlace.Autostart);
        }
        else if (!Shortcuts.Create(ShortcutPlace.Autostart, out var fehler, ersetzen: true))
        {
            MessageBox.Show(this,
                Loc.T("The autostart entry could not be created.")
                + Environment.NewLine + Environment.NewLine + fehler,
                Loc.T("Start with Forza"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        VerknuepfungenZeigen(sofort: true);
    }

    private void DesktopVerknuepfen()
    {
        // ersetzen: wer HIER klickt, will diese Kopie erreichen. Eine Verknuepfung
        // auf einen verschobenen Ordner waere ein toter Knopf auf dem Schreibtisch.
        if (!Shortcuts.Create(ShortcutPlace.Desktop, out var fehler, ersetzen: true))
        {
            MessageBox.Show(this,
                Loc.T("The desktop shortcut could not be created.")
                + Environment.NewLine + Environment.NewLine + fehler,
                Loc.T("Desktop shortcut"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        VerknuepfungenZeigen(sofort: true);
    }

    private void AnTaskleiste()
    {
        if (Shortcuts.IsPinned()) { VerknuepfungenZeigen(sofort: true); return; }

        // ERST der Startmenue-Eintrag: an ihm erkennt Windows das laufende Fenster,
        // und die Anheftung uebernimmt von dort Namen und Symbol -- sonst hiesse sie
        // nach der Versionsressource (ohne "Forza", siehe csproj).
        // Scheitert er, geht das Anheften trotzdem; es wird nur gesagt.
        if (!Shortcuts.Create(ShortcutPlace.StartMenu, out var fehler, ersetzen: true))
        {
            MessageBox.Show(this,
                Loc.T("The Start menu entry could not be created.")
                + Environment.NewLine + Environment.NewLine + fehler,
                Loc.T("Pin to taskbar"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // DEN EINEN HANDGRIFF sagen, den Windows dem Nutzer vorbehaelt -- und danach
        // fuenf Minuten lang nachsehen, damit das Haekchen erscheint, sobald er getan
        // ist, und nicht erst beim naechsten Start.
        MessageBox.Show(this,
            Loc.T("Windows only lets you pin programs yourself, so this takes one click from you: right-click this program's icon in the taskbar and choose “Pin to taskbar”. The Start menu entry it needs is in place, and this button shows a tick once the pin is there."),
            Loc.T("Pin to taskbar"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        if (_anheftUhr is null)
        {
            _anheftUhr = new System.Windows.Forms.Timer { Interval = 2000 };
            _anheftUhr.Tick += (_, _) => VerknuepfungenZeigen(sofort: true);
        }
        _anheftBis = DateTime.UtcNow.AddMinutes(5);
        _anheftUhr.Start();
        VerknuepfungenZeigen(sofort: true);
    }

    private static Label CreateValueLabel(string text, Point location) =>
        new()
        {
            Text = text,
            ForeColor = MutedColor,
            AutoSize = true,
            Location = location
        };

    private static Label CreateGripLabel(string text, Point location) =>
        new()
        {
            Text = text,
            Font = new Font("Segoe UI Semibold", 15),
            AutoSize = true,
            Location = location
        };

    private static ProgressBar CreateGripBar(Point location) =>
        new()
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Location = location,
            Size = new Size(285, 24)
        };

    private static NumericUpDown CreateNumber(
        int x,
        int y,
        decimal minimum,
        decimal maximum,
        decimal value) =>
        new()
        {
            Minimum = minimum,
            Maximum = maximum,
            Value = value,
            TextAlign = HorizontalAlignment.Center,
            Location = new Point(x, y),
            Size = new Size(78, 30)
        };

    private void SetForce(int force)
    {
        if (_forceSlider.Value != force)
        {
            _forceSlider.Value = force;
        }

        if (_forceNumber.Value != force)
        {
            _forceNumber.Value = force;
        }

        _forceReadout.Text = $"{force}%";
    }

    private (bool Steam, bool Generic, bool DualSense) ConnectHardware()
    {
        var steamConnected = _haptics.Connect();
        var dualSenseConnected = _dualSense.Connect();
        var genericConnected = _gamepads.Connect();
        return (steamConnected, genericConnected, dualSenseConnected);
    }

    /// <summary>Die Statuszeile neu setzen, ohne die Auswahlliste anzufassen.</summary>
    /// <remarks>
    /// Sie muss sich aendern, wenn Forza startet oder endet, und das passiert ohne
    /// jedes Ereignis der App. Die Geraeteliste dabei neu zu bauen waere fatal: sie
    /// enthaelt die Auswahl des Nutzers, und ein Neubau alle zwei Sekunden wuerde sie
    /// wegwerfen. Also nur der Text.
    /// </remarks>
    private void RenderControllerStatus()
    {
        if (_connectedDevices is null)
        {
            return;
        }
        _lastStatusRender = DateTime.UtcNow;
        var text = string.Join("  |  ", new[]
        {
            _connectedDevices,
            string.Format(Loc.T("Selected: {0}"), SelectedControllerName()),
            FirewallLine(),
        });
        // Nur bei echter Aenderung schreiben: sonst flackert die Zeile und die
        // Zeichenkette wird zwanzigmal je Sekunde umsonst gebaut.
        if (_controllerStatus.Text == text)
        {
            return;
        }
        _controllerStatus.Text = text;
        _controllerStatus.ForeColor = SuccessColor;
    }

    /// <summary>Sagt, ob die Durchsetzung laeuft -- und wenn nicht, worauf sie wartet.</summary>
    private string FirewallLine()
    {
        var settings = _rivals?.Settings;
        if (settings is null)
        {
            return Loc.T("Haptic/trigger firewall active");
        }
        if (settings.HapticsRequireForza)
        {
            _game ??= new GameWatch(settings.ForzaProcess);
            if (!_game.Running)
            {
                return string.Format(Loc.T("idle - waiting for {0}; tests still work"), _game.ProcessName + ".exe");
            }
        }
        if (settings.HapticsRequireTelemetry && !TelemetryFlowing)
        {
            // Kurz halten: die Zeile hat die Breite des Fensters und nicht mehr.
            return string.Format(Loc.T("idle - no telemetry on UDP {0}; set FH6 Data Out On"), TelemetryPort);
        }
        return Loc.T("Haptic/trigger firewall active");
    }

    private void ApplyControllerConnection(
        bool steamConnected,
        bool genericConnected,
        bool dualSenseConnected)
    {
        RefreshControllerSelector();
        if (steamConnected || genericConnected || dualSenseConnected)
        {
            var parts = new List<string>();
            if (steamConnected)
            {
                parts.Add(string.Format(Loc.T("Steam Controller: {0} native channels"), _haptics.DevicePathCount));
            }

            if (genericConnected)
            {
                parts.Add(string.Format(Loc.T("{0} Xbox/PlayStation/8BitDo-compatible gamepad(s)"), _gamepads.DeviceCount));
            }

            if (dualSenseConnected)
            {
                parts.Add(string.Format(Loc.T("{0} native DualSense controller(s)"), _dualSense.DeviceCount));
            }

            // Warum es still ist, muss dranstehen. Eine absichtlich stille Ausgabe
            // und ein kaputter Controller sehen sonst gleich aus, und geraten wird
            // dann am Kabel und nicht am Spiel.
            _connectedDevices = string.Join("  |  ", parts);
            RenderControllerStatus();
            _controllerStatus.ForeColor = SuccessColor;
            SetTestControlsEnabled(HasSelectedController());
            return;
        }

        _controllerStatus.Text =
            string.Format(Loc.T("No writable controller found. Steam: {0}  |  DualSense: {1}  |  Standard: {2}"),
                          _haptics.LastError, _dualSense.LastError, _gamepads.LastError);
        _controllerStatus.ForeColor = WarningColor;
        SetTestControlsEnabled(false);
    }

    private async Task ConnectControllerAsync()
    {
        StopVibration();
        _reconnectButton.Enabled = false;
        _controllerStatus.Text = Loc.T("Discovering controllers...");
        _controllerStatus.ForeColor = WarningColor;

        _hardwareConnectionTask ??= Task.Run(ConnectHardware);
        var hardwareTask = _hardwareConnectionTask;
        var completed = await Task.WhenAny(
            hardwareTask,
            Task.Delay(TimeSpan.FromSeconds(2)));
        if (completed != hardwareTask)
        {
            _controllerStatus.Text =
                Loc.T("Controller discovery timed out. The window remains usable; reconnect or power-cycle the controller.");
            _controllerStatus.ForeColor = WarningColor;
            _reconnectButton.Enabled = true;
            return;
        }

        var connection = await hardwareTask;
        _hardwareConnectionTask = null;
        ApplyControllerConnection(
            connection.Steam,
            connection.Generic,
            connection.DualSense);

        if (!connection.Steam &&
            !connection.Generic &&
            !connection.DualSense &&
            !Sdl.IsGamepadInitialized)
        {
            _controllerStatus.Text =
                Loc.T("No XInput controller found; checking direct PlayStation/SDL devices...");
            if (await Sdl.WaitForGamepadInitializationAsync(TimeSpan.FromSeconds(2)))
            {
                var directGamepads = await Task.Run(() => _gamepads.Connect());
                ApplyControllerConnection(false, directGamepads, false);
            }
        }

        _reconnectButton.Enabled = true;
    }

    private IReadOnlyList<ControllerOutputTarget> GetControllerTargets()
    {
        var targets = new List<ControllerOutputTarget>();
        if (_haptics.DevicePathCount > 0)
        {
            targets.Add(new ControllerOutputTarget(
                OutputSignalNode.SteamNativeTargetId,
                Loc.T("Steam Controller · native four-channel haptics"),
                true,
                false,
                true,
                false));
        }

        targets.AddRange(_dualSense.Targets);
        targets.AddRange(_gamepads.Targets.Where(target =>
            target.Id != GenericGamepadHaptics.AllTargetId));
        return targets;
    }

    private void RefreshControllerSelector()
    {
        var previous = _selectedControllerId;
        var targets = GetControllerTargets();
        _updatingControllerSelector = true;
        _controllerSelector.BeginUpdate();
        _controllerSelector.Items.Clear();
        _controllerSelector.Items.AddRange(targets.Cast<object>().ToArray());
        _controllerSelector.EndUpdate();

        var selected = targets.FirstOrDefault(target => target.Id == previous) ??
                       targets.FirstOrDefault();
        _controllerSelector.SelectedItem = selected;
        _controllerSelector.Enabled = selected is not null;
        _updatingControllerSelector = false;

        if (selected is not null)
        {
            SelectActiveController(selected);
        }
        else
        {
            _selectedControllerId = string.Empty;
            _gamepads.ActiveTargetId = null;
            _dualSense.ActiveTargetId = null;
            _blueprintEditor.RefreshControllerTargets();
        }
    }

    private void SelectActiveController(ControllerOutputTarget target)
    {
        StopVibration();
        _haptics.StopAll();
        _haptics.EnforceOwnedOutputs();
        _gamepads.ForceZeroAllDevices();
        _dualSense.ForceZeroAllDevices();

        _selectedControllerId = target.Id;
        _gamepads.ActiveTargetId =
            target.IsSteamNative || target.IsDualSenseNative ? null : target.Id;
        _dualSense.ActiveTargetId = target.IsDualSenseNative ? target.Id : null;
        _blueprintEditor.SetActiveController(target);
        SetTestOutputNames(target);
        SetTestControlsEnabled(true);

        _controllerStatus.Text =
            string.Format(Loc.T("Selected: {0}"), target.Name) + "  |  "
            + Loc.T("Haptic/trigger firewall applies only to this controller");
        _controllerStatus.ForeColor = SuccessColor;
    }

    private bool HasSelectedController() =>
        GetControllerTargets().Any(target => target.Id == _selectedControllerId);

    private string SelectedControllerName() =>
        GetControllerTargets()
            .FirstOrDefault(target => target.Id == _selectedControllerId)?.Name ??
        Loc.T("none");

    private bool IsSelectedDualSense() =>
        GetControllerTargets().Any(target =>
            target.Id == _selectedControllerId &&
            target.IsDualSenseNative);

    private void SetTestOutputNames(ControllerOutputTarget target)
    {
        var selectedIndex = Math.Max(0, _outputSelector.SelectedIndex);
        _outputSelector.Items.Clear();
        _outputSelector.Items.AddRange(target.IsSteamNative
            ? [Loc.T("Both grips"), Loc.T("Left grip"), Loc.T("Right grip")]
            : [Loc.T("Both body motors"), Loc.T("Low-frequency motor"), Loc.T("High-frequency motor")]);
        _outputSelector.SelectedIndex = Math.Min(selectedIndex, 2);
    }

    private async Task StartVibrationTestAsync()
    {
        if (!HasSelectedController())
        {
            await ConnectControllerAsync();
            if (!HasSelectedController())
            {
                return;
            }
        }

        _haptics.StopAll();
        _gamepads.StopAll();
        _dualSense.StopAll();
        var strength = _forceSlider.Value / 100.0;
        var writes = 0;
        if (_selectedControllerId == OutputSignalNode.SteamNativeTargetId)
        {
            if (_outputSelector.SelectedIndex != 2)
            {
                writes += _haptics.Play(
                    SteamControllerHaptics.LeftGrip,
                    (double)_vibrationFrequency.Value,
                    strength);
            }

            if (_outputSelector.SelectedIndex != 1)
            {
                writes += _haptics.Play(
                    SteamControllerHaptics.RightGrip,
                    (double)_vibrationFrequency.Value,
                    strength);
            }
        }
        else if (IsSelectedDualSense())
        {
            var channel = _outputSelector.SelectedIndex switch
            {
                1 => DualSenseHaptics.LowBodyMotor,
                2 => DualSenseHaptics.HighBodyMotor,
                _ => DualSenseHaptics.BothBodyMotors
            };
            _dualSense.ApplyOutputs([
                CreateTestOutput(channel, strength)
            ]);
            writes = _dualSense.EnforceOwnedOutputs();
        }
        else
        {
            var channel = _outputSelector.SelectedIndex switch
            {
                1 => GenericGamepadHaptics.LowMotor,
                2 => GenericGamepadHaptics.HighMotor,
                _ => GenericGamepadHaptics.BothMotors
            };
            _gamepads.ApplyOutputs([
                CreateTestOutput(channel, strength)
            ]);
            writes = _gamepads.EnforceOwnedOutputs();
        }

        if (writes == 0)
        {
            var error = _selectedControllerId == OutputSignalNode.SteamNativeTargetId
                ? _haptics.LastError
                : IsSelectedDualSense()
                    ? _dualSense.LastError
                    : _gamepads.LastError;
            _testStatus.Text = string.Format(Loc.T("Haptic command failed: {0}"), error);
            _testStatus.ForeColor = ErrorColor;
            return;
        }

        _vibrating = true;
        _vibrationEndsAt = DateTime.UtcNow.AddSeconds(10);
        SetTestControlsEnabled(false);
        _stopVibrationButton.Enabled = true;
        _countdownLabel.Text = string.Format(Loc.T("Vibrating at {0}%  |  {1} seconds left"),
                                             _forceSlider.Value, 10.0.ToString("F1"));
        _testStatus.Text = Loc.T("Test running. Press Stop now at any time.");
        _testStatus.ForeColor = SuccessColor;
    }

    private GraphHapticOutput CreateTestOutput(int channel, double strength) =>
        new(
            Guid.NewGuid(),
            Loc.T("Vibration test"),
            _selectedControllerId,
            channel,
            HapticEffectMode.Rumble,
            strength,
            (double)_vibrationFrequency.Value,
            DualSenseTriggerEffectMode.Resistance,
            0.2,
            0.8,
            0.25,
            0.75);

    private void UpdateController()
    {
        if (!_vibrating)
        {
            return;
        }

        var remaining = _vibrationEndsAt - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            StopVibration(Loc.T("Finished 10-second vibration test."));
            return;
        }

        _countdownLabel.Text =
            string.Format(Loc.T("Vibrating at {0}%  |  {1} seconds left"),
                          _forceSlider.Value, remaining.TotalSeconds.ToString("F1"));
    }

    private void StopVibration(string? message = null)
    {
        _haptics.StopAll();
        _gamepads.StopAll();
        _dualSense.StopAll();

        _vibrating = false;
        SetTestControlsEnabled(HasSelectedController());
        _stopVibrationButton.Enabled = false;
        _countdownLabel.Text = message ?? Loc.T("Ready");
    }

    private void SetTestControlsEnabled(bool enabled)
    {
        _forceSlider.Enabled = enabled;
        _forceNumber.Enabled = enabled;
        _outputSelector.Enabled = enabled;
        _testButton.Enabled = enabled;
    }

    private async Task StartTelemetryAsync()
    {
        StopTelemetry();

        var port = (int)_portNumber.Value;
        try
        {
            _telemetryCancellation = new CancellationTokenSource();
            // NUR DIESER RECHNER, ausser es ist ausdruecklich anders eingestellt --
            // siehe OverlaySettings.TelemetryFromLan.
            // Im Konsolenmodus kommt die Telemetrie IMMER aus dem Netz (Xbox, anderer PC).
            var einstellungen = Rivals.OverlaySettings.Load();
            var ausDemNetz = einstellungen.TelemetryFromLan || einstellungen.ConsoleMode;
            _telemetryClient = new UdpClient(new IPEndPoint(
                ausDemNetz ? IPAddress.Any : IPAddress.Loopback, port));
        }
        catch (Exception exception)
        {
            _telemetryStatus.Text = string.Format(Loc.T("Could not listen on UDP port {0}: {1}"), port, exception.Message);
            _telemetryStatus.ForeColor = ErrorColor;
            return;
        }

        _portNumber.Enabled = false;
        _startTelemetryButton.Enabled = false;
        _stopTelemetryButton.Enabled = true;
        _telemetryStatus.Text = _rivals?.Settings.ConsoleMode == true
            ? string.Format(Loc.T("Listening on UDP port {0} on {1}. Set Data Out on the Xbox/PC to one of these."),
                            port, Konsole.AdressenText())
            : string.Format(Loc.T("Listening on UDP port {0}. Drive in FH6 to produce packets..."), port);
        if (_rivals?.Settings.ConsoleMode == true)
        {
            Rivals.OverlayAusgabe.Hinweis = string.Format(Loc.T("Waiting for telemetry on {0}, port {1}"),
                                                          Konsole.AdressenText(), port);
        }
        _telemetryStatus.ForeColor = WarningColor;

        var client = _telemetryClient;
        var cancellation = _telemetryCancellation;
        var stopwatch = Stopwatch.StartNew();
        var rateWindow = stopwatch.Elapsed;
        var packetsInWindow = 0;
        var totalPackets = 0L;

        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var result = await client.ReceiveAsync(cancellation.Token);
                totalPackets++;
                packetsInWindow++;

                if (!ForzaPacket.TryParse(result.Buffer, out var telemetry))
                {
                    _telemetryStatus.Text =
                        string.Format(Loc.T("UDP received, but packet length {0} is not a supported Forza packet."),
                                      result.Buffer.Length);
                    _telemetryStatus.ForeColor = ErrorColor;
                    continue;
                }

                // Die Rundenaufzeichnung bekommt JEDES Paket. Sie zeichnet nicht,
                // sie rechnet kaum -- und alles, was hier verworfen wird, fehlt
                // hinterher als Luecke in der Runde.
                _rivals?.Controller?.OnTelemetryRaw(telemetry);

                var now = stopwatch.Elapsed;
                var elapsed = now - rateWindow;
                if (elapsed < TimeSpan.FromMilliseconds(100))
                {
                    continue;
                }

                var packetRate = packetsInWindow / elapsed.TotalSeconds;
                packetsInWindow = 0;
                rateWindow = now;
                ShowTelemetry(telemetry, totalPackets, packetRate, result.Buffer.Length);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal stop.
        }
        catch (ObjectDisposedException)
        {
            // Normal stop.
        }
        catch (Exception exception)
        {
            _telemetryStatus.Text = string.Format(Loc.T("Telemetry listener failed: {0}"), exception.Message);
            _telemetryStatus.ForeColor = ErrorColor;
            StopTelemetryControls();
        }
    }

    // ---- ITelemetryHost: one socket, one port, one owner ----

    // The tab is built before the port box is, so this has to hold up during
    // construction: it crashed the app on start-up when it did not.
    public int TelemetryPort => _portNumber is null ? 5300 : (int)_portNumber.Value;

    /// <summary>PC- oder Konsolenmodus -- nach einer Rueckfrage, denn es heisst Neustart.</summary>
    private void ModusWechseln(bool konsole)
    {
        var text = konsole
            ? Loc.T("FH Companion restarts in Xbox / 2nd PC mode: the telemetry comes over the network and the HUD shows in a dashboard window.")
            : Loc.T("FH Companion restarts in PC mode: the game runs on this PC.");
        if (MessageBox.Show(this, text, Loc.T("Switch mode"), MessageBoxButtons.OKCancel,
                            MessageBoxIcon.Information) != DialogResult.OK)
        {
            return;
        }
        var s = _rivals?.Settings ?? Rivals.OverlaySettings.Load();
        s.ConsoleMode = konsole;
        s.Save();
        Application.Restart();
    }

    public bool TelemetryRunning => _telemetryClient is not null;

    public void EnsureTelemetryStarted()
    {
        if (_telemetryClient is null)
        {
            _ = StartTelemetryAsync();
        }
    }

    private void ShowTelemetry(ForzaPacket telemetry, long totalPackets, double packetRate, int packetLength)
    {
        Rivals.OverlayAusgabe.LetztesPaket = DateTime.UtcNow;
        _latestTelemetry = telemetry;
        _rivals?.Controller?.OnTelemetry(telemetry);
        _latestTelemetryAt = DateTime.UtcNow;
        _hasTelemetry = true;
        _lastPacketRate = packetRate;

        // Die Anzeige nur, wenn jemand hinsieht (siehe SiehtJemandHin): sechzehn
        // Beschriftungen zehnmal je Sekunde, sonst auch hinter dem Spiel.
        if (!SiehtJemandHin(_packetStatus)) { return; }

        _telemetryStatus.Text = Loc.T("Live FH6 telemetry connected.");
        _telemetryStatus.ForeColor = SuccessColor;
        _packetStatus.Text =
            string.Format(Loc.T("Packets: {0}   Rate: {1}/s   Size: {2} bytes   Race: {3}"),
                          totalPackets.ToString("N0"), packetRate.ToString("F1"), packetLength,
                          telemetry.IsRaceOn ? Loc.T("On") : Loc.T("Off"));

        _frontLeftSlip.Text = string.Format(Loc.T("Front left: {0}"), telemetry.CombinedSlipFrontLeft.ToString("F3"));
        _frontRightSlip.Text = string.Format(Loc.T("Front right: {0}"), telemetry.CombinedSlipFrontRight.ToString("F3"));
        _rearLeftSlip.Text = string.Format(Loc.T("Rear left: {0}"), telemetry.CombinedSlipRearLeft.ToString("F3"));
        _rearRightSlip.Text = string.Format(Loc.T("Rear right: {0}"), telemetry.CombinedSlipRearRight.ToString("F3"));

        var leftGrip = (int)Math.Round(telemetry.LeftGrip * 100);
        var rightGrip = (int)Math.Round(telemetry.RightGrip * 100);
        _leftGripLabel.Text = string.Format(Loc.T("Left grip: {0}"), leftGrip + "%");
        _rightGripLabel.Text = string.Format(Loc.T("Right grip: {0}"), rightGrip + "%");
        _leftGripBar.Value = Math.Clamp(leftGrip, 0, 100);
        _rightGripBar.Value = Math.Clamp(rightGrip, 0, 100);

        _frontLeftRatio.Text = string.Format(Loc.T("Front left ratio: {0}"), telemetry.SlipRatioFrontLeft.ToString("F3"));
        _frontRightRatio.Text = string.Format(Loc.T("Front right ratio: {0}"), telemetry.SlipRatioFrontRight.ToString("F3"));
        _rearLeftRatio.Text = string.Format(Loc.T("Rear left ratio: {0}"), telemetry.SlipRatioRearLeft.ToString("F3"));
        _rearRightRatio.Text = string.Format(Loc.T("Rear right ratio: {0}"), telemetry.SlipRatioRearRight.ToString("F3"));

        var leftLock = (int)Math.Round(telemetry.LeftLock * 100);
        var rightLock = (int)Math.Round(telemetry.RightLock * 100);
        _leftLockLabel.Text = string.Format(Loc.T("Left lock: {0}"), leftLock + "%");
        _rightLockLabel.Text = string.Format(Loc.T("Right lock: {0}"), rightLock + "%") + "  |  "
                              + string.Format(Loc.T("Brake: {0}"), telemetry.Brake.ToString("P0"));
        _leftLockBar.Value = Math.Clamp(leftLock, 0, 100);
        _rightLockBar.Value = Math.Clamp(rightLock, 0, 100);
    }

    /// <summary>Darf die Grafik gerade den Controller ansteuern?</summary>
    /// <remarks>
    /// Die 50-Hz-Durchsetzung ueberschreibt absichtlich alles, was sonst am
    /// gewaehlten Controller haengt. Waehrend Forza laeuft, ist das der Zweck;
    /// danach ist es ein Programm, das ein fremdes Rumpeln plattmacht. Abschaltbar
    /// ueber `haptics_require_forza` in `config/overlay.json`, fuer den Fall, dass
    /// jemand einen Aktuator mit einem Konstantknoten ohne Spiel pruefen will.
    /// </remarks>
    private bool GraphMayDrive
    {
        get
        {
            var settings = _rivals?.Settings;
            if (settings is null)
            {
                return true;
            }
            // Konsolenmodus: der Controller haengt an der Konsole -- ausser mit Remote Play.
            if (settings.ConsoleMode && !settings.ControllerHier)
            {
                return false;
            }
            // Das Spiel laeuft dann nicht auf diesem Rechner: es zaehlt, dass Telemetrie kommt.
            if (settings.HapticsRequireForza && !settings.ConsoleMode)
            {
                _game ??= new GameWatch(settings.ForzaProcess);
                if (!_game.Running)
                {
                    return false;
                }
            }
            return !settings.HapticsRequireTelemetry || TelemetryFlowing;
        }
    }

    /// <summary>Kommen gerade Pakete? Mit zwei Sekunden Nachsicht.</summary>
    /// <remarks>
    /// Nicht die 300 ms der Auswertung: die entscheiden, ob ein Wert FRISCH ist, und
    /// duerfen streng sein. Hier geht es darum, ob der Strom laeuft -- ein Ladebild
    /// oder eine kurze Luecke darf nicht dazu fuehren, dass der Controller im
    /// Sekundentakt abgegeben und wieder genommen wird.
    /// </remarks>
    private bool TelemetryFlowing =>
        _hasTelemetry && DateTime.UtcNow - _latestTelemetryAt < TimeSpan.FromSeconds(2);

    /// <summary>Einmal alles auf null und dann die Finger davon.</summary>
    /// <remarks>
    /// Nicht bei jedem Tick nullen: genau das WAERE das Ueberschreiben, das hier
    /// unterbleiben soll. Einmal beim Uebergang, damit nichts weiterbrummt, und
    /// danach gehoert der Controller wieder dem, der ihn benutzt.
    /// </remarks>
    private void ReleaseForIdleGame()
    {
        if (_releasedForIdleGame)
        {
            return;
        }
        _releasedForIdleGame = true;
        _haptics.StopAll();
        _gamepads.StopAll();
        _dualSense.StopAll();
        RenderControllerStatus();
    }

    private void UpdateGraphMapping(bool mayDrive = true)
    {
        var now = DateTime.UtcNow;
        if (!_vibrating && !_toneTestRunning)
        {
            var freshTelemetry =
                _hasTelemetry &&
                now - _latestTelemetryAt <= TimeSpan.FromMilliseconds(300)
                    ? _latestTelemetry
                    : null;
            _graphEvaluation = _signalGraphEvaluator.Evaluate(
                _signalGraph,
                freshTelemetry,
                now);
            // DIE LIVE-WERTE IM EDITOR nur, wenn jemand hinsieht (seit 2026-09-28): ein
            // Bild des Editors kostet gemessen 25 ms Prozessorzeit, und er bekam 50-mal je
            // Sekunde neue Werte -- auch hinter dem Spiel, denn verdeckte Fenster zeichnen
            // unter Windows weiter. Der Reiter ist der erste, also offen, sobald das
            // Fenster offen ist. Gerechnet wird weiter (die Haptik braucht es), nur das
            // Zeichnen wartet.
            if (now - _liveValuesAt >= TimeSpan.FromMilliseconds(100) && SiehtJemandHin(_blueprintEditor))
            {
                _liveValuesAt = now;
                _blueprintEditor.SetLiveValues(_graphEvaluation.NodeValues);
            }
            // VERALTETE TELEMETRIE HEISST STILLE. Nach 300 ms ohne Paket liefert jeder
            // Telemetrie-Knoten 0 -- und ein Graph, der den Grip erst in einer Kurve
            // umkehrt, macht daraus volle Staerke, bis GraphMayDrive nach zwei Sekunden
            // abschaltet. Ohne je ein Paket (ein Konstantknoten wird ohne Spiel
            // geprueft) wird dagegen weiter ausgegeben.
            var veraltet = _hasTelemetry && freshTelemetry is null;
            if (_signalGraph.Enabled && mayDrive && !veraltet)
            {
                ApplyGraphOutputs(_graphEvaluation.Outputs);
            }
            else if (mayDrive)
            {
                _haptics.StopAll();
                _gamepads.StopAll();
                _dualSense.StopAll();
            }
            // Und im Leerlauf: nichts. Jeden Tick auf null zu schreiben WAERE das
            // Ueberschreiben, das hier unterbleiben soll -- einmal genullt hat
            // ReleaseForIdleGame() beim Uebergang.
        }

        UpdateTelemetryReadouts();
    }

    /// <summary>Die Anzeige weiterfuehren, auch wenn nichts ausgegeben wird.</summary>
    /// <remarks>
    /// Wichtig fuer den Fall "Spiel laeuft nicht": die Zahlen sollen weiter zu sehen
    /// sein, sonst wirkt eine bewusst stille Ausgabe wie eine abgestuerzte App.
    /// </remarks>
    private DateTime _liveValuesAt = DateTime.MinValue;

    /// <summary>
    /// Ist dieses Stueck gerade zu sehen -- und nicht nur vorhanden? Sein Reiter offen,
    /// das Fenster weder versteckt noch minimiert, und Forza nicht im Vordergrund: wer
    /// faehrt, schaut nicht auf den Editor. Dann wird nichts neu gezeichnet.
    /// </summary>
    private bool SiehtJemandHin(Control stueck)
    {
        if (!stueck.Visible || !Visible || WindowState == FormWindowState.Minimized) { return false; }
        var settings = _rivals?.Settings;
        if (settings is null) { return true; }
        _game ??= new GameWatch(settings.ForzaProcess);
        return !(_game.Running && _game.IsForeground);
    }

    private void UpdateTelemetryReadouts()
    {
        var now = DateTime.UtcNow;
        if (_hasTelemetry && now - _lastInspectorUpdate >= TimeSpan.FromMilliseconds(100)
            && SiehtJemandHin(_telemetryInspector))
        {
            _lastInspectorUpdate = now;
            _telemetryInspector.UpdateValues(
                _latestTelemetry,
                _haptics.CurrentOutputs
                    .Concat(_dualSense.CurrentOutputs)
                    .Concat(_gamepads.CurrentOutputs)
                    .ToArray(),
                _graphEvaluation,
                _lastPacketRate);
        }
    }

    private void ApplyGraphOutputs(IReadOnlyList<GraphHapticOutput> outputs)
    {
        if (_selectedControllerId == OutputSignalNode.SteamNativeTargetId)
        {
            _gamepads.StopAll();
            _dualSense.StopAll();
            for (var channel = 0; channel < 4; channel++)
            {
                var strongest = outputs
                    .Where(output =>
                        output.TargetId == _selectedControllerId &&
                        output.Channel == channel)
                    .OrderByDescending(output => output.Strength)
                    .FirstOrDefault();

                if (strongest is null || strongest.Strength <= 0.0001)
                {
                    _haptics.Stop(channel);
                }
                else
                {
                    _haptics.Play(channel, strongest.FrequencyHz, strongest.Strength);
                }
            }

            return;
        }

        if (IsSelectedDualSense())
        {
            _haptics.StopAll();
            _gamepads.StopAll();
            _dualSense.ApplyOutputs(outputs
                .Where(output => output.TargetId == _selectedControllerId)
                .ToArray());
            return;
        }

        _haptics.StopAll();
        _dualSense.StopAll();
        _gamepads.ApplyOutputs(outputs
            .Where(output => output.TargetId == _selectedControllerId)
            .ToArray());
    }

    private void UpdateLiveMapping()
    {
        var gripEnabled = _liveMappingCheck.Checked;
        var lockEnabled = _lockMappingCheck.Checked;
        if ((!gripEnabled && !lockEnabled) || _vibrating || _toneTestRunning)
        {
            return;
        }

        if (_haptics.DevicePathCount == 0)
        {
            _mappingStatus.Text = Loc.T("No native haptic channel is open.");
            _mappingStatus.ForeColor = ErrorColor;
            return;
        }

        if (!_hasTelemetry || DateTime.UtcNow - _latestTelemetryAt > TimeSpan.FromMilliseconds(300))
        {
            _haptics.StopAll();
            _mappingStatus.Text = Loc.T("Waiting for fresh FH6 telemetry; haptics stopped as a precaution.");
            _mappingStatus.ForeColor = WarningColor;
            return;
        }

        var leftVibration = 0.0;
        var rightVibration = 0.0;
        var vibrationFrequency = (double)_vibrationFrequency.Value;

        if (gripEnabled)
        {
            var vibrationMaximum = (double)_vibrationMaximum.Value / 100.0;
            leftVibration = _vibrationCurve.Curve.Evaluate(_latestTelemetry.LeftGrip) * vibrationMaximum;
            rightVibration = _vibrationCurve.Curve.Evaluate(_latestTelemetry.RightGrip) * vibrationMaximum;
        }

        var leftLockOutput = 0.0;
        var rightLockOutput = 0.0;
        if (lockEnabled)
        {
            var lockMaximum = (double)_lockMaximum.Value / 100.0;
            leftLockOutput = _lockCurve.Curve.Evaluate(_latestTelemetry.LeftLock) * lockMaximum;
            rightLockOutput = _lockCurve.Curve.Evaluate(_latestTelemetry.RightLock) * lockMaximum;
        }

        var leftOutput = Math.Max(leftVibration, leftLockOutput);
        var rightOutput = Math.Max(rightVibration, rightLockOutput);
        var lockFrequency = (double)_lockFrequency.Value;
        var leftOutputFrequency = leftLockOutput > 0.01 ? lockFrequency : vibrationFrequency;
        var rightOutputFrequency = rightLockOutput > 0.01 ? lockFrequency : vibrationFrequency;

        var writes = 0;
        writes += _haptics.Play(SteamControllerHaptics.LeftGrip, leftOutputFrequency, leftOutput);
        writes += _haptics.Play(SteamControllerHaptics.RightGrip, rightOutputFrequency, rightOutput);

        var leftTone = 0.0;
        var rightTone = 0.0;
        if (gripEnabled && _toneEnabledCheck.Checked)
        {
            var toneMaximum = (double)_toneMaximum.Value / 100.0;
            leftTone = _toneCurve.Curve.Evaluate(_latestTelemetry.LeftGrip) * toneMaximum;
            rightTone = _toneCurve.Curve.Evaluate(_latestTelemetry.RightGrip) * toneMaximum;
            var low = (double)_toneLowFrequency.Value;
            var high = (double)_toneHighFrequency.Value;
            var leftFrequency = low + (high - low) * _latestTelemetry.LeftGrip;
            var rightFrequency = low + (high - low) * _latestTelemetry.RightGrip;
            writes += _haptics.Play(SteamControllerHaptics.LeftPad, leftFrequency, leftTone);
            writes += _haptics.Play(SteamControllerHaptics.RightPad, rightFrequency, rightTone);
        }
        else
        {
            _haptics.Stop(SteamControllerHaptics.LeftPad);
            _haptics.Stop(SteamControllerHaptics.RightPad);
        }

        _mappingValues.Text =
            $"Grip L/R: {_latestTelemetry.LeftGrip:P0} / {_latestTelemetry.RightGrip:P0}   " +
            $"Vibration: {leftVibration:P0} / {rightVibration:P0}   " +
            $"Ton: {leftTone:P0} / {rightTone:P0}";

        _lockMappingValues.Text =
            $"Blockieren L/R: {_latestTelemetry.LeftLock:P0} / {_latestTelemetry.RightLock:P0}   " +
            $"Ausgabe: {leftLockOutput:P0} / {rightLockOutput:P0}";

        if (writes > 0)
        {
            _mappingStatus.Text = Loc.T("Live mapping is sending through the four native haptic channels.");
            _mappingStatus.ForeColor = SuccessColor;
            if (lockEnabled)
            {
                _lockMappingStatus.Text =
                    "Blockier-Mapping aktiv. Die Blockierfrequenz überlagert Grip auf der betroffenen Seite.";
                _lockMappingStatus.ForeColor = SuccessColor;
            }
        }
        else if (!string.IsNullOrWhiteSpace(_haptics.LastError))
        {
            _mappingStatus.Text = $"Haptik-Schreibfehler: {_haptics.LastError}";
            _mappingStatus.ForeColor = ErrorColor;
            _lockMappingStatus.Text = _mappingStatus.Text;
            _lockMappingStatus.ForeColor = ErrorColor;
        }
    }

    private async Task TestToneAsync()
    {
        if (_haptics.DevicePathCount == 0)
        {
            await ConnectControllerAsync();
            if (_haptics.DevicePathCount == 0)
            {
                return;
            }
        }

        _toneTestRunning = true;
        _haptics.StopAll();
        var strength = (double)_toneMaximum.Value / 100.0;
        var frequency = ((double)_toneLowFrequency.Value + (double)_toneHighFrequency.Value) / 2.0;
        var writes =
            _haptics.Play(SteamControllerHaptics.LeftPad, frequency, strength) +
            _haptics.Play(SteamControllerHaptics.RightPad, frequency, strength);

        _mappingStatus.Text = writes > 0
            ? $"Spiele {frequency:F0} Hz für zwei Sekunden über beide Trackpad-Haptiken."
            : $"Tontest fehlgeschlagen: {_haptics.LastError}";
        _mappingStatus.ForeColor = writes > 0 ? SuccessColor : ErrorColor;

        await Task.Delay(2000);
        _haptics.Stop(SteamControllerHaptics.LeftPad);
        _haptics.Stop(SteamControllerHaptics.RightPad);
        _toneTestRunning = false;
    }

    private void StopTelemetry(string? message = null)
    {
        _telemetryCancellation?.Cancel();
        _telemetryClient?.Dispose();
        _telemetryCancellation?.Dispose();
        _telemetryCancellation = null;
        _telemetryClient = null;
        StopTelemetryControls();

        if (message is not null)
        {
            _telemetryStatus.Text = message;
            _telemetryStatus.ForeColor = MutedColor;
        }
    }

    private void StopTelemetryControls()
    {
        _portNumber.Enabled = true;
        _startTelemetryButton.Enabled = true;
        _stopTelemetryButton.Enabled = false;
    }


    /// <summary>Ein Eintrag der Sprachauswahl: Kennung und lesbarer Name.</summary>
    private sealed record SprachEintrag(string Code, string Name)
    {
        public override string ToString() => Name;
    }

    /// <summary>Der Name einer Sprache IN DIESER SPRACHE.</summary>
    /// <remarks>
    /// Endonyme, nicht englische Namen: wer die Liste aufklappt, weil er kein
    /// Englisch liest, findet "Polski" und nicht "Polish".
    ///
    /// .NET kennt die Namen selbst -- `CultureInfo.NativeName`. Das wird hier
    /// zuerst versucht; die Liste darunter ist nur der Rueckfall, falls Windows
    /// eine Kennung nicht kennt. So muss diese Datei nicht jedesmal angefasst
    /// werden, wenn eine Sprachdatei dazukommt.
    /// </remarks>
    private static string SprachName(string code)
    {
        try
        {
            var kultur = System.Globalization.CultureInfo.GetCultureInfo(code);
            var name = kultur.NativeName;
            if (!string.IsNullOrWhiteSpace(name))
            {
                // "Deutsch (Deutschland)" -> "Deutsch"
                var klammer = name.IndexOf(" (", StringComparison.Ordinal);
                if (klammer > 0) { name = name[..klammer]; }
                return char.ToUpper(name[0]) + name[1..];
            }
        }
        catch (System.Globalization.CultureNotFoundException)
        {
            // Dann eben die Kennung.
        }
        return code;
    }
}
