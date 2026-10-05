using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using ForzaHaptics.Tester.Rivals;

namespace ForzaHaptics.Tester.Scan;

/// <summary>
/// Der Reiter "Scan leaderboards": Rivals-Bestenlisten aus der App heraus ablesen -- Kategorie,
/// Strecken und Klassen ankreuzen, Start, und der Scanner arbeitet die Liste Board fuer Board ab.
/// </summary>
/// <remarks>
/// Seit 2026-10-04. Was der Reiter verspricht und wie er es haelt:
///
/// - NIE VON SELBST. Gestartet wird nur mit dem Knopf, und nur, wenn das Spiel laeuft und nicht
///   gerade gefahren wird (die Telemetrie sagt es, sofern sie ankommt). Waehrend des Laufs gehoert
///   der Rechner dem Scanner: Tasten gehen ans Spiel. Ein anderes Fenster, die Pause-Taste oder
///   "Stop" halten ihn an (ScanEingabe.PruefeWeiter).
/// - DIE OBERFLAECHE BLEIBT FREI. Texterkennung und Tastendruecke laufen auf einem eigenen Faden;
///   zurueck kommt nur Text, ueber BeginInvoke.
/// - "SERVER ERROR" HEISST ZURUECKWEICHEN ([[server-error-means-stop]]): acht Minuten Pause, dann
///   das Board EIN weiteres Mal. Kommt der Fehler in diesem Lauf noch einmal, ist Schluss --
///   ein Dienst, der gerade abgelehnt hat, wird nicht bestuermt.
/// - EIN ABSTURZ DES SPIELS haelt die Warteschlange sofort an ("game_crashed") -- auf ein
///   abgestuerztes Spiel acht Minuten zu warten, bringt nichts.
/// - ANGEHALTEN IST NICHT VERLOREN: wird ein Board mittendrin angehalten (Stop, Pause-Taste, ein
///   anderes Fenster vorn), steht es mit den bis dahin gelesenen Zeilen als "stopped" in der Tabelle
///   und geht hinaus wie ein fertiges. Ob es auf die Seite kommt, entscheidet der Server: ein
///   unbewiesenes Ende mit weniger als 500 Zeilen oder 80 % des Boards haelt er zurueck.
/// - Abgelegt wird wie mit --scan-board unter DataFolder\scans, im Format des alten Werkzeugs.
/// </remarks>
internal sealed class ScanTab : UserControl
{
    /// <summary>Die Klassen, die der Reiter anbietet, in der Reihenfolge des Spiels.</summary>
    internal static readonly string[] TabKlassen = { "D", "C", "B", "A", "S1", "S2", "R" };

    /// <summary>So lange wird nach einem "Server Error" gewartet, bevor das Board noch einmal versucht wird.</summary>
    internal static readonly TimeSpan ServerPause = TimeSpan.FromMinutes(8);

    private static readonly Color Grund = Color.FromArgb(24, 26, 31);
    private static readonly Color Feld = Color.FromArgb(18, 20, 24);
    private static readonly Color Warnfarbe = Color.FromArgb(240, 180, 80);

    private readonly OverlaySettings _einstellungen;
    private readonly Func<bool> _faehrt;
    private readonly ScanRoutes? _routen;

    private readonly ComboBox _kategorie = new();
    private readonly CheckBox _alle = new();
    private readonly CheckedListBox _strecken = new();
    private readonly FlowLayoutPanel _klassen = new();
    private readonly NumericUpDown _obergrenze = new();
    private readonly CheckBox _hochladen = new();
    private readonly Label _warnung = new();
    private readonly Button _start;
    private readonly Button _stop;
    private readonly Label _aktuell = new();
    private readonly Label _status = new();
    private readonly ListView _tabelle = new();
    private readonly FlowLayoutPanel _oben;
    private readonly Panel _links;

    private CancellationTokenSource? _lauf;
    /// <summary>Fuer die Versaende: sie laufen nach "Stop" weiter, nur nicht ueber das Ende der App hinaus.</summary>
    private readonly CancellationTokenSource _ende = new();
    private readonly object _logSperre = new();
    private bool _still;

    private static string Wurzel => Path.Combine(AppInfo.DataFolder, "scans");

    /// <param name="faehrt">Ob gerade gefahren wird -- dann startet der Scanner nicht.</param>
    public ScanTab(OverlaySettings einstellungen, Func<bool> faehrt)
    {
        _einstellungen = einstellungen;
        _faehrt = faehrt;
        BackColor = Grund;
        ForeColor = Color.WhiteSmoke;
        Dock = DockStyle.Fill;

        string? routenFehler = null;
        try { _routen = ScanRoutes.Laden(); }
        catch (Exception e) { routenFehler = e.Message; }

        // LINKS: Kategorie und ihre Strecken.
        _links = new Panel { Dock = DockStyle.Left, Width = 340, Padding = new Padding(8, 8, 4, 8) };
        var kopf = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown, WrapContents = false,
        };
        kopf.Controls.Add(Beschriftung(Loc.T("Category")));
        _kategorie.DropDownStyle = ComboBoxStyle.DropDownList;
        _kategorie.FlatStyle = FlatStyle.Flat;
        _kategorie.BackColor = Feld;
        _kategorie.ForeColor = Color.WhiteSmoke;
        _kategorie.Width = 300;
        foreach (var k in _routen?.Kategorien ?? Array.Empty<ScanRoutes.Kategorie>()) { _kategorie.Items.Add(k.Name); }
        _kategorie.SelectedIndexChanged += (_, _) => StreckenZeigen();
        kopf.Controls.Add(_kategorie);
        _alle.Text = Loc.T("All routes");
        _alle.AutoSize = true;
        _alle.ForeColor = Color.WhiteSmoke;
        _alle.Margin = new Padding(3, 8, 3, 2);
        _alle.CheckedChanged += (_, _) =>
        {
            if (_still) { return; }
            _still = true;
            try
            {
                for (var i = 0; i < _strecken.Items.Count; i++) { _strecken.SetItemChecked(i, _alle.Checked); }
            }
            finally { _still = false; }
            Bereit();
        };
        kopf.Controls.Add(_alle);
        _strecken.Dock = DockStyle.Fill;
        _strecken.CheckOnClick = true;
        _strecken.IntegralHeight = false;
        _strecken.BackColor = Feld;
        _strecken.ForeColor = Color.WhiteSmoke;
        _strecken.BorderStyle = BorderStyle.None;
        // ItemCheck kommt VOR der Aenderung -- der Haken "alle" wird danach nachgezogen.
        _strecken.ItemCheck += (_, _) =>
        {
            if (_still || !IsHandleCreated) { return; }
            BeginInvoke(new Action(AlleNachziehen));
        };
        _links.Controls.Add(_strecken);
        _links.Controls.Add(kopf);

        // RECHTS OBEN: Klassen, Optionen, Warnung, Knoepfe, Stand.
        _oben = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(4, 8, 8, 4),
        };
        var klassenZeile = Zeile();
        klassenZeile.Controls.Add(Beschriftung(Loc.T("Class")));
        _klassen.AutoSize = true;
        _klassen.WrapContents = false;
        foreach (var k in TabKlassen)
        {
            var cb = new CheckBox { Text = k, AutoSize = true, ForeColor = Color.WhiteSmoke, Tag = k };
            cb.CheckedChanged += (_, _) => Bereit();
            _klassen.Controls.Add(cb);
        }
        klassenZeile.Controls.Add(_klassen);
        _oben.Controls.Add(klassenZeile);

        var optionen = Zeile();
        optionen.Controls.Add(Beschriftung(Loc.T("First ranks only (0 = whole board)")));
        _obergrenze.Minimum = 0;
        _obergrenze.Maximum = 200000;
        _obergrenze.Increment = 100;
        _obergrenze.Width = 90;
        _obergrenze.BackColor = Feld;
        _obergrenze.ForeColor = Color.WhiteSmoke;
        _obergrenze.Value = Math.Clamp(_einstellungen.ScanRowCap, 0, 200000);
        _obergrenze.ValueChanged += (_, _) =>
        {
            _einstellungen.ScanRowCap = (int)_obergrenze.Value;
            _einstellungen.Save();
        };
        optionen.Controls.Add(_obergrenze);
        _hochladen.Text = Loc.T("Upload each finished board");
        _hochladen.AutoSize = true;
        _hochladen.ForeColor = Color.WhiteSmoke;
        _hochladen.Margin = new Padding(16, 6, 3, 3);
        _hochladen.Checked = _einstellungen.ScanUpload;
        _hochladen.CheckedChanged += (_, _) =>
        {
            _einstellungen.ScanUpload = _hochladen.Checked;
            _einstellungen.Save();
        };
        optionen.Controls.Add(_hochladen);
        _oben.Controls.Add(optionen);

        _warnung.Text = Loc.T("While the scan runs, this PC belongs to the scanner: it sends keys to the game and reads the screen. Do not touch the mouse or keyboard. Switching to another window or pressing the Pause key stops it. Start it from a menu in the game, never while you drive.");
        _warnung.AutoSize = true;
        _warnung.ForeColor = Warnfarbe;
        _warnung.Margin = new Padding(8, 6, 3, 6);
        _oben.Controls.Add(_warnung);

        var knoepfe = Zeile();
        _start = Knopf(Loc.T("Start scan"));
        _start.Click += (_, _) => Starten();
        _stop = Knopf(Loc.T("Stop"));
        _stop.Enabled = false;
        _stop.Click += (_, _) =>
        {
            _lauf?.Cancel();
            Status(Loc.T("Stopping ..."));
        };
        var ordner = Knopf(Loc.T("Open scans folder"));
        ordner.Click += (_, _) => OrdnerOeffnen();
        knoepfe.Controls.Add(_start);
        knoepfe.Controls.Add(_stop);
        knoepfe.Controls.Add(ordner);
        _oben.Controls.Add(knoepfe);

        _aktuell.AutoSize = true;
        _aktuell.Font = new Font(Font, FontStyle.Bold);
        _aktuell.Margin = new Padding(8, 6, 3, 2);
        _oben.Controls.Add(_aktuell);
        _status.AutoSize = true;
        _status.ForeColor = Color.Gray;
        _status.Margin = new Padding(8, 2, 3, 6);
        _oben.Controls.Add(_status);

        // RECHTS UNTEN: je Board eine Zeile.
        _tabelle.Dock = DockStyle.Fill;
        _tabelle.View = View.Details;
        _tabelle.FullRowSelect = true;
        _tabelle.BackColor = Feld;
        _tabelle.ForeColor = Color.WhiteSmoke;
        foreach (var (text, breite, rechts) in new[]
                 {
                     (Loc.T("Board"), 330, false), (Loc.T("Status"), 170, false), (Loc.T("Rows"), 70, true),
                     (Loc.T("Duration"), 80, true), (Loc.T("Upload"), 260, false),
                 })
        {
            var breiteKopf = TextRenderer.MeasureText(text, _tabelle.Font).Width + 18;
            _tabelle.Columns.Add(text, Math.Max(breite, breiteKopf), rechts ? HorizontalAlignment.Right : HorizontalAlignment.Left);
        }

        Controls.Add(_tabelle);
        Controls.Add(_oben);
        Controls.Add(_links);
        Resize += (_, _) => Umbruch();

        if (_kategorie.Items.Count > 0) { _kategorie.SelectedIndex = 0; }
        if (_routen is null)
        {
            _start.Enabled = false;
            Status(string.Format(Loc.T("The route list could not be loaded: {0}"), routenFehler));
        }
        else
        {
            Bereit();
        }
        Umbruch();
    }

    // ------------------------------------------------------------------ //
    // Die Warteschlange
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Jede angekreuzte Strecke mal jede angekreuzte Klasse: Strecke fuer Strecke, darin die
    /// Klassen in der Reihenfolge des Spiels (D bis R). So wechselt der Scanner zwischen zwei
    /// Boards meist nur die Klasse -- der kurze Weg. Doppelte, unbekannte und Strecken ausserhalb
    /// der Kategorie fallen weg.
    /// </summary>
    internal static List<ScanNavigator.Ziel> Warteschlange(string kategorie, int streckenAnzahl,
                                                            IEnumerable<int> strecken, IEnumerable<string> klassen)
    {
        var gewaehlt = new HashSet<string>(klassen.Select(k => (k ?? string.Empty).Trim().ToUpperInvariant()));
        var geordnet = ScanScreen.Klassen.Where(gewaehlt.Contains).ToList();
        return strecken.Where(i => i >= 0 && i < streckenAnzahl).Distinct().OrderBy(i => i)
                       .SelectMany(i => geordnet.Select(k => new ScanNavigator.Ziel(kategorie, i, k)))
                       .ToList();
    }

    /// <summary>Ein "Server Error" -- vom Board (Status) oder schon auf dem Weg dorthin (Abbruch des Navigators).</summary>
    internal static bool IstServerFehler(string? status, string? abbruch) =>
        status == "server_error"
        || (abbruch is not null && abbruch.StartsWith("Server Error", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Was hinausgeht: alles ausser einem Board, das ein Server-Fehler abgeschnitten hat -- auch ein
    /// angehaltenes ("stopped") oder eines, ueber dem das Spiel abstuerzte. Der Server haelt zurueck,
    /// was davon kein ganzes Board ist (scan_submissions.ende_unbewiesen).
    /// </summary>
    internal static bool LohntVersand(string status) => status != "server_error";

    /// <summary>Haelt dieses Board die ganze Warteschlange an -- sofort, ohne Pause und zweiten Versuch?</summary>
    internal static bool HaeltSchlangeAn(string? status) => status == "game_crashed";

    internal static string StatusText(string status) => status switch
    {
        "end_detected" => Loc.T("complete"),
        "invalid_tail" => Loc.T("complete (valid laps)"),
        "row_cap_reached" => Loc.T("first ranks read"),
        "truncated" => Loc.T("cut short"),
        "end_unverified" => Loc.T("end not confirmed"),
        "server_error" => Loc.T("server error"),
        "stopped" => Loc.T("stopped"),
        "game_crashed" => Loc.T("game crashed"),
        _ => status,
    };

    private static string Dauer(TimeSpan d) => $"{(int)d.TotalMinutes}:{d.Seconds:00}";

    // ------------------------------------------------------------------ //
    // Auswahl
    // ------------------------------------------------------------------ //

    private ScanRoutes.Kategorie? Kategorie() =>
        _routen is not null && _kategorie.SelectedItem is string name ? _routen.Finde(name) : null;

    private void StreckenZeigen()
    {
        _still = true;
        try
        {
            _strecken.Items.Clear();
            foreach (var s in Kategorie()?.Strecken ?? Array.Empty<string>()) { _strecken.Items.Add(s); }
            _alle.Checked = false;
        }
        finally { _still = false; }
        Bereit();
    }

    private void AlleNachziehen()
    {
        _still = true;
        try { _alle.Checked = _strecken.Items.Count > 0 && _strecken.CheckedIndices.Count == _strecken.Items.Count; }
        finally { _still = false; }
        Bereit();
    }

    private List<ScanNavigator.Ziel> Auswahl()
    {
        var kat = Kategorie();
        if (kat is null) { return new List<ScanNavigator.Ziel>(); }
        var klassen = _klassen.Controls.OfType<CheckBox>().Where(c => c.Checked).Select(c => (string)c.Tag!);
        return Warteschlange(kat.Name, kat.Strecken.Count, _strecken.CheckedIndices.Cast<int>().ToList(), klassen);
    }

    /// <summary>Im Ruhezustand: wie viele Boards die Auswahl ergibt.</summary>
    private void Bereit()
    {
        if (_lauf is not null || _routen is null) { return; }
        _aktuell.Text = string.Format(Loc.T("{0} board(s) selected"), Auswahl().Count);
    }

    // ------------------------------------------------------------------ //
    // Start und Lauf
    // ------------------------------------------------------------------ //

    private void Starten()
    {
        if (_lauf is not null || _routen is null) { return; }
        var ziele = Auswahl();
        if (ziele.Count == 0)
        {
            Status(Loc.T("Tick at least one route and one class."));
            return;
        }
        if (!new GameWatch().Running)
        {
            Status(Loc.T("The game is not running. Start it, open any menu, then press Start scan."));
            return;
        }
        if (_faehrt())
        {
            Status(Loc.T("You are driving. Open a menu in the game first: the scanner takes over the keyboard."));
            return;
        }
        if (SpielSperre.Belegt())
        {
            Status(Loc.T("Another automation (board scan or auction check) is driving the game. Nothing was pressed."));
            return;
        }
        // Auf dem Faden der Oberflaeche: hier hat die App gerade den Klick und damit das Recht,
        // ein anderes Fenster nach vorn zu holen.
        if (!ScanEingabe.SpielNachVorn())
        {
            Status(Loc.T("The game could not be brought to the front. Click into the game once, come back and press Start scan again."));
            return;
        }
        var obergrenze = (int)_obergrenze.Value;
        var hochladen = _hochladen.Checked;
        _lauf = new CancellationTokenSource();
        var stop = _lauf.Token;
        Sperren(true);
        _tabelle.Items.Clear();
        Status(Loc.T("Waiting for the game to come to the front ..."));
        new Thread(() => Lauf(ziele, obergrenze, hochladen, stop))
        {
            IsBackground = true, Name = "Board-Scan",
        }.Start();
    }

    /// <summary>Die Warteschlange abarbeiten -- auf dem eigenen Faden, nie auf dem der Oberflaeche.</summary>
    private void Lauf(IReadOnlyList<ScanNavigator.Ziel> ziele, int obergrenze, bool hochladen, CancellationToken stop)
    {
        var schluss = string.Empty;
        var fertig = 0;
        // Die Zeile des Boards, das gerade laeuft -- bricht der Lauf mittendrin ab, steht dort
        // sonst fuer immer "scanning ...".
        ListViewItem? offen = null;
        // Der Griff fuer die ganze Warteschlange, auf diesem Faden: kein Bieter drueckt dazwischen.
        SpielSperre.Griff? griff = null;
        try
        {
            griff = SpielSperre.Nehmen("board scan");
            if (griff is null)
            {
                schluss = Loc.T("Another automation (board scan or auction check) is driving the game. Nothing was pressed.");
                return;
            }
            Directory.CreateDirectory(Wurzel);
            Log($"queue of {ziele.Count} board(s), first {obergrenze} ranks (0 = all), upload {(hochladen ? "on" : "off")}");
            if (!WarteAufSpiel(stop))
            {
                schluss = Loc.T("The game did not come to the front. Nothing was pressed.");
                return;
            }
            // Das erste Board von irgendwo im Spiel; danach steht es auf der vorigen Bestenliste.
            var vomBoard = false;
            var gewartet = false;
            for (var i = 0; i < ziele.Count; i++)
            {
                stop.ThrowIfCancellationRequested();
                var ziel = ziele[i];
                var name = BoardName(ziel);
                var nr = i + 1;
                Zurueck(() => _aktuell.Text = string.Format(Loc.T("Board {0} of {1}: {2}"), nr, ziele.Count, name));
                var zeile = new ListViewItem(new[] { name, Loc.T("scanning ..."), string.Empty, string.Empty, string.Empty });
                Zurueck(() => { _tabelle.Items.Add(zeile); zeile.EnsureVisible(); });
                offen = zeile;

                var uhr = Stopwatch.StartNew();
                BoardScanner.Ergebnis? e = null;
                string? abbruch = null;
                try
                {
                    var scanner = new BoardScanner(_routen!, Melden, stop) { Obergrenze = obergrenze };
                    e = scanner.Scanne(ziel, vomBoard, Wurzel);
                }
                catch (ScanNavigator.Abbruch a)
                {
                    abbruch = a.Message;
                    // Stand der Scanner schon auf der Liste, hat er das Gelesene abgelegt.
                    e = a.Lauf;
                }
                catch (Exception x) when (x is not OperationCanceledException)
                {
                    Log(x.ToString());
                    abbruch = x.Message;
                }
                vomBoard = true;
                offen = null;
                var dauer = Dauer(e?.Dauer ?? uhr.Elapsed);
                var zeilen = e?.Zeilen.ToString("n0") ?? string.Empty;

                if (IstServerFehler(e?.Status, abbruch))
                {
                    Log($"server error on {name}" + (abbruch is null ? string.Empty : ": " + abbruch));
                    Setze(zeile, Loc.T("server error"), zeilen, dauer, "–");
                    if (gewartet)
                    {
                        schluss = Loc.T("Server error again: the queue stops. Try again later.");
                        return;
                    }
                    gewartet = true;
                    var bis = DateTime.Now + ServerPause;
                    var text = string.Format(Loc.T("Server error: the game cannot reach the leaderboards. Waiting until {0}, then this board once more."),
                                             bis.ToString("HH:mm"));
                    Zurueck(() => Status(text));
                    if (stop.WaitHandle.WaitOne(ServerPause)) { throw new OperationCanceledException(stop); }
                    DialogWegdruecken(stop);
                    i--;
                    continue;
                }
                if (HaeltSchlangeAn(e?.Status))
                {
                    Log($"the game crashed on {name}");
                    Abgelegt(e!, zeile, name, zeilen, dauer, hochladen);
                    schluss = string.Format(Loc.T("Queue stopped: {0}"), Loc.T("game crashed"));
                    return;
                }
                if (abbruch is not null)
                {
                    Log($"stopped on {name}: {abbruch}");
                    if (e is not null)
                    {
                        Abgelegt(e, zeile, name, zeilen, dauer, hochladen);
                    }
                    else
                    {
                        Setze(zeile, Loc.T("stopped"), zeilen, dauer, "–");
                    }
                    schluss = stop.IsCancellationRequested ? Loc.T("Stopped.") : string.Format(Loc.T("Queue stopped: {0}"), abbruch);
                    return;
                }

                fertig++;
                Abgelegt(e!, zeile, name, zeilen, dauer, hochladen);
            }
            schluss = string.Format(Loc.T("Queue finished: {0} of {1} board(s) scanned."), fertig, ziele.Count);
        }
        catch (OperationCanceledException)
        {
            schluss = Loc.T("Stopped.");
        }
        catch (ScanNavigator.Abbruch a)
        {
            schluss = stop.IsCancellationRequested ? Loc.T("Stopped.") : string.Format(Loc.T("Queue stopped: {0}"), a.Message);
        }
        catch (Exception x)
        {
            Log(x.ToString());
            schluss = string.Format(Loc.T("Queue stopped: {0}"), x.Message);
        }
        finally
        {
            griff?.Dispose();
            if (offen is not null) { Setze(offen, Loc.T("stopped"), string.Empty, string.Empty, "–"); }
            Log("end: " + schluss);
            var text = schluss;
            Zurueck(() =>
            {
                _lauf?.Dispose();
                _lauf = null;
                Sperren(false);
                Bereit();
                Status(text);
            });
        }
    }

    /// <summary>
    /// Ein abgelegtes Board in die Tabelle -- fertig, angehalten oder abgestuerzt -- und, wenn gewuenscht,
    /// hinaus. Der Versand laeuft neben dem naechsten Board her (oder nach dem Ende der Warteschlange
    /// weiter); er braucht weder Tasten noch Bild.
    /// </summary>
    private void Abgelegt(BoardScanner.Ergebnis e, ListViewItem zeile, string name, string zeilen, string dauer, bool hochladen)
    {
        var versand = hochladen && LohntVersand(e.Status);
        Setze(zeile, StatusText(e.Status), zeilen, dauer, versand ? Loc.T("sending ...") : Loc.T("off"));
        Log($"{name}: {e.Status}, {e.Zeilen} rows up to rank {e.HoechsterPlatz} in {dauer} -> {e.Ordner}");
        if (versand) { Senden(e.Ordner, zeile); }
    }

    /// <summary>Wie --scan-board: das Spiel dreimal nacheinander vorn, sonst wird nichts gedrueckt.</summary>
    private static bool WarteAufSpiel(CancellationToken stop)
    {
        var treffer = 0;
        var bis = DateTime.UtcNow.AddSeconds(15);
        while (treffer < 3)
        {
            stop.ThrowIfCancellationRequested();
            treffer = GameWatch.ForegroundProcessName() == GameWatch.DefaultProcessName ? treffer + 1 : 0;
            if (DateTime.UtcNow > bis) { return false; }
            if (stop.WaitHandle.WaitOne(500)) { throw new OperationCanceledException(stop); }
        }
        return true;
    }

    /// <summary>
    /// Nach der Pause: steht der Fehlerdialog noch, ihn mit ENTER schliessen -- aber nur, wenn
    /// er wirklich zu sehen ist. ENTER auf dem Klassenschirm startet ein Rennen.
    /// </summary>
    private void DialogWegdruecken(CancellationToken stop)
    {
        var eingabe = new ScanEingabe(stop);
        var ocr = new WindowsOcr();
        for (var versuch = 0; versuch < 3; versuch++)
        {
            eingabe.PruefeWeiter();
            using (var bild = ScanEingabe.Aufnahme())
            {
                if (ScanScreen.Einordnen(ocr.Read(bild)) is not ScanSchirm.ServerFehler) { return; }
            }
            Melden("closing the Server Error dialog with ENTER");
            eingabe.Taste(ScanEingabe.Enter, 2500);
        }
    }

    private void Senden(string ordner, ListViewItem zeile)
    {
        var ende = _ende.Token;
        _ = Task.Run(async () =>
        {
            string text;
            try
            {
                var antwort = await ScanUpload.SendenAsync(ordner, ende).ConfigureAwait(false);
                text = antwort.Ok ? Loc.T("sent") : string.Format(Loc.T("not sent: {0}"), antwort.Meldung);
                Log($"upload {Path.GetFileName(ordner)}: {(antwort.Ok ? "ok" : "failed")} {antwort.Meldung}");
            }
            catch (Exception x)
            {
                text = string.Format(Loc.T("not sent: {0}"), x.Message);
                Log($"upload {Path.GetFileName(ordner)}: {x.Message}");
            }
            Zurueck(() => zeile.SubItems[4].Text = text);
        });
    }

    private string BoardName(ScanNavigator.Ziel z)
    {
        var kat = _routen?.Finde(z.Kategorie);
        var strecke = kat is not null && z.RouteIndex >= 0 && z.RouteIndex < kat.Strecken.Count ? kat.Strecken[z.RouteIndex] : $"#{z.RouteIndex}";
        return $"{kat?.Name ?? z.Kategorie} / {strecke} / {z.Klasse}";
    }

    private void Setze(ListViewItem zeile, string status, string zeilen, string dauer, string versand) =>
        Zurueck(() =>
        {
            zeile.SubItems[1].Text = status;
            zeile.SubItems[2].Text = zeilen;
            zeile.SubItems[3].Text = dauer;
            zeile.SubItems[4].Text = versand;
        });

    /// <summary>Die Meldungen des Scanners: in die Statuszeile und ins Protokoll neben den Laeufen.</summary>
    private void Melden(string text)
    {
        Log(text);
        Zurueck(() => Status(text));
    }

    private void Log(string text)
    {
        try
        {
            lock (_logSperre)
            {
                Directory.CreateDirectory(Wurzel);
                File.AppendAllText(Path.Combine(Wurzel, "scan_log.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}");
            }
        }
        catch (Exception) { }
    }

    // ------------------------------------------------------------------ //
    // Oberflaeche
    // ------------------------------------------------------------------ //

    private void Sperren(bool laeuft)
    {
        _start.Enabled = !laeuft && _routen is not null;
        _stop.Enabled = laeuft;
        _kategorie.Enabled = !laeuft;
        _alle.Enabled = !laeuft;
        _strecken.Enabled = !laeuft;
        _klassen.Enabled = !laeuft;
        _obergrenze.Enabled = !laeuft;
        _hochladen.Enabled = !laeuft;
    }

    private void OrdnerOeffnen()
    {
        try
        {
            Directory.CreateDirectory(Wurzel);
            Process.Start(new ProcessStartInfo(Wurzel) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Status(e.Message);
        }
    }

    private void Zurueck(Action a)
    {
        try
        {
            if (IsHandleCreated && !IsDisposed) { BeginInvoke(a); }
        }
        catch (Exception)
        {
        }
    }

    private void Status(string text)
    {
        _status.Text = text;
        Umbruch();
    }

    /// <summary>Lange Texte in die Breite der rechten Seite umbrechen.</summary>
    private void Umbruch()
    {
        var breite = Math.Max(240, ClientSize.Width - _links.Width - 40);
        _warnung.MaximumSize = new Size(breite, 0);
        _status.MaximumSize = new Size(breite, 0);
        _aktuell.MaximumSize = new Size(breite, 0);
    }

    private static FlowLayoutPanel Zeile() => new()
    {
        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0),
    };

    private static Label Beschriftung(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = Color.Gray, Padding = new Padding(4, 6, 2, 0),
    };

    private static Button Knopf(string text) => new()
    {
        Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat,
        BackColor = Feld, ForeColor = Color.WhiteSmoke, Margin = new Padding(3),
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _lauf?.Cancel(); } catch (Exception) { }
            try { _ende.Cancel(); } catch (Exception) { }
        }
        base.Dispose(disposing);
    }
}
