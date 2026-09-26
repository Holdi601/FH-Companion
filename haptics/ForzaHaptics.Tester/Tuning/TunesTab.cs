using System.Drawing;
using System.Windows.Forms;

namespace ForzaHaptics.Tester.Tuning;

/// <summary>
/// Der Reiter "Tunes": wie voll der Tune-Speicher ist, und welche Tunes auf keinem
/// Auto liegen.
/// </summary>
/// <remarks>
/// Angefragt am 2026-09-26: das Spiel verweigerte weitere Downloads, und Aufraeumen
/// hiess, jedes Auto einzeln durchzugehen. Dieser Reiter beantwortet die Fragen
/// davor: wie viele sind es, wie viele passen, welche braucht kein Auto -- auch nur
/// die aus einem bestimmten Zeitraum.
/// </remarks>
internal sealed class TunesTab : UserControl
{
    private readonly Rivals.OverlaySettings _settings;
    private readonly Func<Rivals.RivalsAdvisor?> _berater;
    private readonly Label _stand = new();
    private readonly Panel _balken = new();
    private readonly Button _pruefen = new();
    private readonly Label _pruefStand = new();
    private readonly CheckBox _nurFrei = new();
    private readonly CheckBox _zeitraum = new();
    private readonly DateTimePicker _von = new();
    private readonly DateTimePicker _bis = new();
    private readonly NumericUpDown _grenze = new();
    private readonly ComboBox _ansicht = new();
    private readonly Button _probeKnopf = new();
    private readonly Button _loeschKnopf = new();
    private CancellationTokenSource? _laeuft;
    private readonly ListView _liste = new();
    private readonly Label _summe = new();
    private List<StoredTune> _tunes = new();
    private TuneStorage.Usage? _nutzung;
    private string? _root;

    public TunesTab(Rivals.OverlaySettings settings, Func<Rivals.RivalsAdvisor?> berater)
    {
        _settings = settings;
        _berater = berater;
        BackColor = Color.FromArgb(24, 26, 31);
        ForeColor = Color.WhiteSmoke;
        Dock = DockStyle.Fill;

        var kopf = new Panel { Dock = DockStyle.Top, Height = 150, Padding = new Padding(14, 10, 14, 6) };
        _stand.Location = new Point(14, 10);
        _stand.AutoSize = true;
        _stand.Font = new Font("Segoe UI Semibold", 13f);
        _balken.Location = new Point(16, 44);
        _balken.Size = new Size(520, 12);
        _balken.Paint += (_, e) => MaleBalken(e.Graphics);

        var grenzeText = new Label { Text = Loc.T("Limit"), AutoSize = true, Location = new Point(552, 42), ForeColor = Color.Gray };
        _grenze.Location = new Point(600, 39);
        _grenze.Width = 70;
        _grenze.Minimum = 10;
        _grenze.Maximum = 100000;
        _grenze.Value = Math.Clamp(_settings.TuneLimit, 10, 100000);
        _grenze.ValueChanged += (_, _) =>
        {
            _settings.TuneLimit = (int)_grenze.Value;
            _settings.Save();
            ZeigeStand();
        };

        _pruefen.Text = Loc.T("Check which tunes are on a car");
        _pruefen.AutoSize = true;
        _pruefen.FlatStyle = FlatStyle.Flat;
        _pruefen.Location = new Point(16, 68);
        _pruefen.Click += (_, _) => Pruefen();
        // DAS LOESCHEN IM SPIEL -- nur im Loeschplan sichtbar.
        _probeKnopf.Text = Loc.T("Test run in the game (deletes nothing)");
        _probeKnopf.AutoSize = true;
        _probeKnopf.FlatStyle = FlatStyle.Flat;
        _probeKnopf.Location = new Point(300, 68);
        _probeKnopf.Visible = false;
        _probeKnopf.Click += (_, _) => ImSpiel(probelauf: true);
        _loeschKnopf.Text = Loc.T("Delete these tunes in the game");
        _loeschKnopf.AutoSize = true;
        _loeschKnopf.FlatStyle = FlatStyle.Flat;
        _loeschKnopf.ForeColor = Color.FromArgb(255, 150, 150);
        _loeschKnopf.Location = new Point(600, 68);
        _loeschKnopf.Visible = false;
        _loeschKnopf.Click += (_, _) => ImSpiel(probelauf: false);
        _pruefStand.Location = new Point(18, 100);
        _pruefStand.Size = new Size(900, 20);
        _pruefStand.ForeColor = Color.Gray;

        _nurFrei.Text = Loc.T("Only tunes that are on no car");
        _nurFrei.AutoSize = true;
        _nurFrei.Location = new Point(16, 124);
        _nurFrei.CheckedChanged += (_, _) => Fuellen();
        _zeitraum.Text = Loc.T("Saved between");
        _zeitraum.AutoSize = true;
        _zeitraum.Location = new Point(260, 124);
        _zeitraum.CheckedChanged += (_, _) => Fuellen();
        // ZWEI ANSICHTEN. Die Rangliste ist fuer den, der im Spiel von Hand loescht
        // (Nutzerwunsch vom 2026-09-26): bei welchem Auto lohnt es sich am meisten,
        // hineinzugehen?
        _ansicht.DropDownStyle = ComboBoxStyle.DropDownList;
        _ansicht.Items.AddRange(new object[]
        {
            Loc.T("Every tune"), Loc.T("Cars with the most unused tunes"),
            Loc.T("Tuners behind your best laps"), Loc.T("Tuners by number of your tunes"),
            Loc.T("Deletion plan"),
        });
        _ansicht.SelectedIndex = 0;
        _ansicht.Location = new Point(680, 121);
        _ansicht.Width = 240;
        _ansicht.SelectedIndexChanged += (_, _) => Fuellen();
        _von.Location = new Point(390, 121);
        _von.Width = 120;
        _von.Format = DateTimePickerFormat.Short;
        _von.ValueChanged += (_, _) => { if (_zeitraum.Checked) { Fuellen(); } };
        _bis.Location = new Point(520, 121);
        _bis.Width = 120;
        _bis.Format = DateTimePickerFormat.Short;
        _bis.ValueChanged += (_, _) => { if (_zeitraum.Checked) { Fuellen(); } };

        kopf.Controls.AddRange(new Control[]
        {
            _stand, _balken, grenzeText, _grenze, _pruefen, _pruefStand, _nurFrei, _zeitraum, _von, _bis, _ansicht,
            _probeKnopf, _loeschKnopf,
        });

        _liste.Dock = DockStyle.Fill;
        _liste.View = View.Details;
        _liste.FullRowSelect = true;
        _liste.BackColor = Color.FromArgb(18, 20, 24);
        _liste.ForeColor = Color.WhiteSmoke;

        _summe.Dock = DockStyle.Bottom;
        _summe.Height = 44;
        _summe.Padding = new Padding(14, 4, 14, 4);
        _summe.ForeColor = Color.Gray;

        Controls.Add(_liste);
        Controls.Add(_summe);
        Controls.Add(kopf);
    }

    /// <summary>Nur fuer die Vorschau: eine Pruefung vorgeben, ohne sie zu speichern.</summary>
    internal void VorschauNutzung(TuneStorage.Usage u, int ansicht)
    {
        _nutzung = u;
        _ansicht.SelectedIndex = Math.Clamp(ansicht, 0, _ansicht.Items.Count - 1);
        ZeigeStand();
        Fuellen();
    }

    /// <summary>Neu von der Platte lesen -- beim Oeffnen des Reiters.</summary>
    public void Neu()
    {
        _root = TuneStorage.FindRoot();
        _tunes = TuneStorage.Read(_root).OrderBy(t => t.SavedAt).ToList();
        _nutzung = TuneStorage.LoadUsage();
        if (_tunes.Count > 0 && _von.Tag is null)
        {
            // Beim ersten Oeffnen den ganzen Bestand vorschlagen.
            _von.Value = _tunes[0].SavedAt.Date;
            _bis.Value = DateTime.Today;
            _von.Tag = true;
        }
        ZeigeStand();
        Fuellen();
    }

    private void ZeigeStand()
    {
        if (_root is null)
        {
            _stand.Text = Loc.T("The game's save folder was not found.");
            _balken.Invalidate();
            return;
        }
        var grenze = Math.Max(1, _settings.TuneLimit);
        var frei = grenze - _tunes.Count;
        _stand.Text = string.Format(Loc.T("{0} of {1} tune slots in use -- {2} free"), _tunes.Count, grenze, Math.Max(0, frei));
        _stand.ForeColor = frei <= 0 ? Color.FromArgb(255, 107, 107)
                         : frei <= _settings.TuneWarnFree ? Color.FromArgb(255, 210, 90)
                         : Color.FromArgb(126, 231, 135);
        _pruefStand.Text = _nutzung is null
            ? Loc.T("Not checked yet -- reads the game's memory while it runs, takes up to a minute.")
            : string.Format(Loc.T("Last checked {0:yyyy-MM-dd HH:mm}: {1} tunes are on a car. Tunes saved after that show \"?\"."),
                            _nutzung.CheckedAt, _nutzung.Applied.Count);
        _balken.Invalidate();
    }

    private void MaleBalken(Graphics g)
    {
        var r = _balken.ClientRectangle;
        using var grund = new SolidBrush(Color.FromArgb(48, 54, 61));
        g.FillRectangle(grund, r);
        var grenze = Math.Max(1, _settings.TuneLimit);
        var anteil = Math.Clamp(_tunes.Count / (double)grenze, 0, 1);
        using var farbe = new SolidBrush(_stand.ForeColor);
        g.FillRectangle(farbe, 0, 0, (int)(r.Width * anteil), r.Height);
    }

    /// <summary>Liegt dieses Tune auf einem Auto? null: seit der Pruefung dazugekommen oder nie geprueft.</summary>
    private bool? AufAuto(StoredTune t)
    {
        if (_nutzung is null || t.SavedAt > _nutzung.CheckedAt) { return null; }
        return _nutzung.Applied.Contains(t.Folder, StringComparer.OrdinalIgnoreCase);
    }

    private string AutoName(int id)
    {
        var rat = _berater();
        if (rat?.CarIndexForId(id) is { } ix && rat.RealCarName(ix) is { } name) { return name; }
        return $"car {id}";
    }

    private void Spalten(params (string Text, int Breite)[] spalten)
    {
        // Gruppen gibt es nur im Loeschplan; jede andere Ansicht raeumt sie weg.
        _liste.ShowGroups = false;
        _liste.Groups.Clear();
        _liste.Columns.Clear();
        foreach (var (text, breite) in spalten) { _liste.Columns.Add(text, breite); }
    }

    private void Fuellen()
    {
        var von = _von.Value.Date;
        var bis = _bis.Value.Date.AddDays(1);
        var imZeitraum = _tunes.Where(t => !_zeitraum.Checked || (t.SavedAt >= von && t.SavedAt < bis)).ToList();
        _probeKnopf.Visible = _loeschKnopf.Visible = _ansicht.SelectedIndex == 4 && _nutzung is not null;
        if (_ansicht.SelectedIndex == 1)
        {
            Rangliste(imZeitraum);
            return;
        }
        if (_ansicht.SelectedIndex == 4)
        {
            Loeschplan();
            return;
        }
        if (_ansicht.SelectedIndex >= 2)
        {
            Tunerliste(imZeitraum, nachBestzeiten: _ansicht.SelectedIndex == 2);
            return;
        }
        var zeilen = imZeitraum.Where(t => !_nurFrei.Checked || AufAuto(t) == false).ToList();
        _liste.BeginUpdate();
        _liste.Items.Clear();
        Spalten((Loc.T("Car"), 260), (Loc.T("Tune"), 200), (Loc.T("Creator"), 140),
                (Loc.T("Saved"), 130), (Loc.T("On a car"), 90));
        foreach (var t in zeilen)
        {
            var z = new ListViewItem(AutoName(t.CarId)) { Tag = t.Folder };
            z.SubItems.Add(t.Name);
            z.SubItems.Add(t.Creator);
            z.SubItems.Add(t.SavedAt.ToString("yyyy-MM-dd HH:mm"));
            var auf = AufAuto(t);
            z.SubItems.Add(auf switch { true => Loc.T("yes"), false => Loc.T("no"), _ => "?" });
            if (auf == false) { z.ForeColor = Color.FromArgb(255, 210, 90); }
            _liste.Items.Add(z);
        }
        _liste.EndUpdate();
        var frei = _tunes.Count(t => AufAuto(t) == false);
        _summe.Text = string.Format(Loc.T("{0} tunes shown. {1} tunes are on no car and could go."), zeilen.Count, frei)
                      + "  " + Loc.T("Deleting has to happen in the game -- this app only reads the save.");
    }

    /// <summary>
    /// Je Auto: wie viele Tunes liegen auf keinem Auto? Die meisten zuerst.
    /// </summary>
    /// <remarks>
    /// Ohne Pruefung ist "ungenutzt" unbekannt; dann ordnet die Liste nach der Zahl
    /// aller Tunes und sagt, warum.
    /// </remarks>
    private void Rangliste(List<StoredTune> tunes)
    {
        var geprueft = _nutzung is not null;
        var autos = tunes.GroupBy(t => t.CarId)
            .Select(g => new
            {
                Auto = g.Key,
                Alle = g.Count(),
                Frei = g.Count(t => AufAuto(t) == false),
                Offen = g.Count(t => AufAuto(t) is null),
                Aeltestes = g.Where(t => AufAuto(t) == false).Select(t => (DateTime?)t.SavedAt).Min(),
            })
            .Where(a => !geprueft || !_nurFrei.Checked || a.Frei > 0)
            .OrderByDescending(a => geprueft ? a.Frei : a.Alle)
            .ThenByDescending(a => a.Alle)
            .ToList();
        _liste.BeginUpdate();
        _liste.Items.Clear();
        Spalten(("#", 45), (Loc.T("Car"), 300), (Loc.T("Unused tunes"), 110), (Loc.T("All tunes"), 90),
                (Loc.T("Oldest unused"), 130));
        var platz = 0;
        foreach (var a in autos)
        {
            platz++;
            var z = new ListViewItem(platz.ToString());
            z.SubItems.Add(AutoName(a.Auto));
            z.SubItems.Add(geprueft ? (a.Offen > 0 ? $"{a.Frei} (+{a.Offen} ?)" : a.Frei.ToString()) : "?");
            z.SubItems.Add(a.Alle.ToString());
            z.SubItems.Add(a.Aeltestes?.ToString("yyyy-MM-dd") ?? string.Empty);
            if (geprueft && a.Frei > 0) { z.ForeColor = Color.FromArgb(255, 210, 90); }
            _liste.Items.Add(z);
        }
        _liste.EndUpdate();
        var frei = autos.Sum(a => a.Frei);
        _summe.Text = geprueft
            ? string.Format(Loc.T("{0} cars; {1} tunes on no car. Start at the top when you delete in the game."), autos.Count, frei)
            : Loc.T("Not checked yet, so this is sorted by all tunes -- press \"Check which tunes are on a car\" to rank by unused ones.");
    }

    /// <summary>
    /// Was geloescht wuerde: nur Tunes, die sicher auf keinem Auto liegen, im gewaehlten
    /// Zeitraum -- dieselbe Liste, die das automatische Loeschen abarbeiten wird.
    /// </summary>
    /// <remarks>
    /// "Sicher" heisst: die Garagen-Pruefung hat sie gesehen und auf keinem Auto
    /// gefunden. Nach der Pruefung geladene ("?") sind nie dabei -- ein frisch geladenes
    /// Tune liegt meist gerade auf dem Auto.
    /// </remarks>
    internal static List<StoredTune> Plan(IReadOnlyList<StoredTune> tunes, TuneStorage.Usage? nutzung,
                                          DateTime? von, DateTime? bis)
    {
        if (nutzung is null) { return new List<StoredTune>(); }
        return tunes.Where(t => t.SavedAt <= nutzung.CheckedAt
                                && !nutzung.Applied.Contains(t.Folder, StringComparer.OrdinalIgnoreCase))
                    .Where(t => von is null || t.SavedAt >= von)
                    .Where(t => bis is null || t.SavedAt < bis)
                    .OrderBy(t => t.CarId).ThenBy(t => t.SavedAt)
                    .ToList();
    }

    /// <summary>
    /// Den Loeschplan im Spiel abarbeiten lassen -- oder erst probeweise, ohne zu loeschen.
    /// </summary>
    private void ImSpiel(bool probelauf)
    {
        if (_laeuft is not null)
        {
            _laeuft.Cancel();
            return;
        }
        var plan = AktuellerPlan();
        var rat = _berater();
        if (plan.Count == 0) { _summe.Text = Loc.T("Nothing to delete in this selection."); return; }
        if (rat is null) { _summe.Text = Loc.T("The car list is not loaded yet -- wait a moment and try again."); return; }
        if (!ForzaMemoryDb.GameRunning)
        {
            _summe.Text = Loc.T("Start the game first -- the garage is read from its memory.");
            return;
        }
        var autos = plan.Select(t => t.CarId).Distinct().Count();
        var frage = string.Format(
            probelauf
                ? Loc.T("Test run: the app goes through {1} cars in the game and checks {0} tunes, without deleting anything.")
                : Loc.T("The app will delete {0} tunes on {1} cars in the game."), plan.Count, autos)
            + "\n\n" + Loc.T("1. In the game, open the pause menu on the CARS tab (My Cars, Upgrades & Tuning).")
            + "\n" + Loc.T("2. Click OK. The app switches to the game and drives the menus.")
            + "\n" + Loc.T("3. Do not touch keyboard or controller while it runs. Alt+Tab or the Pause key stops it.")
            + "\n\n" + Loc.T("A tune that is on a car is never deleted.");
        if (MessageBox.Show(this, frage, Loc.T("Delete tunes in the game"), MessageBoxButtons.OKCancel,
                            probelauf ? MessageBoxIcon.Information : MessageBoxIcon.Warning) != DialogResult.OK)
        {
            return;
        }
        _laeuft = new CancellationTokenSource();
        var stop = _laeuft.Token;
        (probelauf ? _probeKnopf : _loeschKnopf).Text = Loc.T("Stop");
        (probelauf ? _loeschKnopf : _probeKnopf).Enabled = false;
        TuneDeleter.SpielNachVorn();
        Task.Run(() =>
        {
            Thread.Sleep(1500);
            var loescher = new TuneDeleter(rat, probelauf, m => BeiUns(() => _summe.Text = m), stop);
            var ergebnis = loescher.Run(plan);
            BeiUns(() =>
            {
                _laeuft = null;
                _probeKnopf.Text = Loc.T("Test run in the game (deletes nothing)");
                _loeschKnopf.Text = Loc.T("Delete these tunes in the game");
                _probeKnopf.Enabled = _loeschKnopf.Enabled = true;
                Neu();
                // Der Pfad ausserhalb des Schluessels: Rueckstriche im Schluessel liest
                // lang_keys.py als Steuerzeichen ("\t"), und die Uebersetzung griffe nie.
                _summe.Text = ergebnis + "  " + string.Format(Loc.T("Details: {0}"), @"%TEMP%\forza-overlay\tune_delete.log");
            });
        });
    }

    /// <summary>Die Tunes, die im Loeschplan stehen -- fuer das automatische Loeschen.</summary>
    internal List<StoredTune> AktuellerPlan() =>
        Plan(_tunes, _nutzung,
             _zeitraum.Checked ? _von.Value.Date : null,
             _zeitraum.Checked ? _bis.Value.Date.AddDays(1) : null);

    /// <summary>
    /// Der Loeschplan: nach Auto gruppiert, jedes Tune mit Tuner und Datum; oben die
    /// Zusammenfassung mit der Zahl je Tuner (Nutzerwunsch vom 2026-09-26).
    /// </summary>
    private void Loeschplan()
    {
        _liste.BeginUpdate();
        _liste.Items.Clear();
        // DAS AUTO AUCH ALS SPALTE, nicht nur als Gruppenkopf: Gruppen zeichnet eine
        // Liste ohne Visual Styles gar nicht, und dann stuende nirgends, welches Auto.
        Spalten((Loc.T("Car"), 250), (Loc.T("Tune"), 240), (Loc.T("Creator"), 170), (Loc.T("Saved"), 130));
        if (_nutzung is null)
        {
            _liste.EndUpdate();
            _summe.Text = Loc.T("Press \"Check which tunes are on a car\" first -- without it the app cannot tell which tunes are safe to delete.");
            return;
        }
        var plan = AktuellerPlan();
        _liste.ShowGroups = true;
        foreach (var auto in plan.GroupBy(t => t.CarId).OrderByDescending(g => g.Count()).ThenBy(g => AutoName(g.Key)))
        {
            var gruppe = new ListViewGroup(auto.Key.ToString(),
                string.Format(Loc.T("{0} -- {1} tunes"), AutoName(auto.Key), auto.Count()));
            _liste.Groups.Add(gruppe);
            foreach (var t in auto)
            {
                var z = new ListViewItem(AutoName(t.CarId), gruppe) { Tag = t.Folder };
                z.SubItems.Add(string.IsNullOrWhiteSpace(t.Name) ? "-" : t.Name);
                z.SubItems.Add(string.IsNullOrWhiteSpace(t.Creator) ? Loc.T("(unknown)") : t.Creator);
                z.SubItems.Add(t.SavedAt.ToString("yyyy-MM-dd HH:mm"));
                z.ForeColor = Color.FromArgb(255, 210, 90);
                _liste.Items.Add(z);
            }
        }
        _liste.EndUpdate();
        var tuner = plan.GroupBy(t => string.IsNullOrWhiteSpace(t.Creator) ? Loc.T("(unknown)") : t.Creator)
                        .OrderByDescending(g => g.Count()).ToList();
        var namen = string.Join(", ", tuner.Take(8).Select(g => $"{g.Key} {g.Count()}"))
                    + (tuner.Count > 8 ? ", …" : string.Empty);
        _summe.Text = plan.Count == 0
            ? Loc.T("Nothing to delete in this selection.")
            : string.Format(Loc.T("{0} tunes on {1} cars would be deleted, by {2} tuners: {3}"),
                            plan.Count, plan.Select(t => t.CarId).Distinct().Count(), tuner.Count, namen);
    }

    /// <summary>
    /// Welches Tune lag auf diesem Auto, als die Runde gefahren wurde?
    /// </summary>
    /// <remarks>
    /// Das Spiel schreibt es nirgends mit -- weder die Telemetrie noch die Garage nennt
    /// ein Tune zu einer Runde. Darum zwei Stufen:
    ///
    /// 1. SICHER: das Tune, das laut Garagen-Pruefung heute auf dem Auto liegt, war schon
    ///    vor der Runde gespeichert -- dann lag es damals auch drauf (es sei denn, es
    ///    wurde zwischendurch getauscht und zurueckgetauscht).
    /// 2. GESCHAETZT: sonst das zuletzt vor der Runde gespeicherte Tune dieses Autos. Im
    ///    Spiel wird ein heruntergeladenes Tune beim Laden aufgespielt.
    ///
    /// Gibt es keins, war es ein eigenes Setup oder das Serienauto.
    /// </remarks>
    internal static (StoredTune? Tune, bool Sicher) TuneZurRunde(
        IReadOnlyList<StoredTune> tunes, IReadOnlyCollection<string>? belegt, int auto, DateTime wann)
    {
        var vorher = tunes.Where(t => t.CarId == auto && t.SavedAt <= wann).ToList();
        if (belegt is not null)
        {
            var aufgespielt = vorher.Where(t => belegt.Contains(t.Folder, StringComparer.OrdinalIgnoreCase))
                                    .OrderByDescending(t => t.SavedAt).FirstOrDefault();
            if (aufgespielt is not null) { return (aufgespielt, true); }
        }
        return (vorher.OrderByDescending(t => t.SavedAt).FirstOrDefault(), false);
    }

    /// <summary>
    /// Zwei persoenliche Ranglisten der Tuner: nach deinen Bestzeiten auf ihren Tunes,
    /// oder nach der Zahl ihrer Tunes bei dir.
    /// </summary>
    /// <remarks>
    /// Nutzerwunsch vom 2026-09-26. Bestzeit heisst: die schnellste eigene Runde je Kurs,
    /// Klasse und Startart -- dieselbe Einteilung wie im Reiter "My times".
    /// </remarks>
    private void Tunerliste(List<StoredTune> tunes, bool nachBestzeiten)
    {
        var belegt = _nutzung?.Applied;
        var niemand = Loc.T("(own setup or stock)");
        var zeilen = new Dictionary<string, (int Tunes, int Auf, HashSet<int> Autos, int Beste, int Geschaetzt)>(
            StringComparer.OrdinalIgnoreCase);
        (int Tunes, int Auf, HashSet<int> Autos, int Beste, int Geschaetzt) Zeile(string wer) =>
            zeilen.TryGetValue(wer, out var z) ? z : (0, 0, new HashSet<int>(), 0, 0);
        var besteAutos = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tunes)
        {
            var wer = string.IsNullOrWhiteSpace(t.Creator) ? Loc.T("(unknown)") : t.Creator;
            var z = Zeile(wer);
            z.Tunes++;
            if (AufAuto(t) == true) { z.Auf++; }
            z.Autos.Add(t.CarId);
            zeilen[wer] = z;
        }
        var besteZahl = 0;
        try
        {
            var runden = Rivals.OwnTimes.All();
            var besten = runden.GroupBy(l => (l.Course, l.Klass, l.Standing))
                               .Select(g => g.OrderBy(l => l.Seconds).First())
                               .ToList();
            besteZahl = besten.Count;
            foreach (var b in besten)
            {
                var (tune, sicher) = TuneZurRunde(_tunes, belegt, b.Ordinal, b.When);
                var wer = tune is null ? niemand
                        : string.IsNullOrWhiteSpace(tune.Creator) ? Loc.T("(unknown)") : tune.Creator;
                var z = Zeile(wer);
                z.Beste++;
                if (!sicher && tune is not null) { z.Geschaetzt++; }
                zeilen[wer] = z;
                if (!besteAutos.TryGetValue(wer, out var autos)) { besteAutos[wer] = autos = new HashSet<int>(); }
                autos.Add(b.Ordinal);
            }
        }
        catch (Exception)
        {
            // Ohne Rundenarchiv bleibt die Spalte leer -- die Tunes stehen trotzdem da.
        }

        _liste.BeginUpdate();
        _liste.Items.Clear();
        var platz = 0;
        if (nachBestzeiten)
        {
            // WER HINTER DEINEN BESTZEITEN STEHT -- nur Tuner mit mindestens einer.
            Spalten(("#", 45), (Loc.T("Tuner"), 240), (Loc.T("Your best laps"), 130), (Loc.T("Cars"), 70));
            foreach (var (wer, z) in zeilen.Where(kv => kv.Value.Beste > 0)
                                           .OrderByDescending(kv => kv.Value.Beste)
                                           .ThenBy(kv => kv.Value.Geschaetzt))
            {
                platz++;
                var item = new ListViewItem(platz.ToString());
                item.SubItems.Add(wer);
                item.SubItems.Add(z.Geschaetzt > 0 ? $"{z.Beste} ({z.Geschaetzt} ~)" : z.Beste.ToString());
                item.SubItems.Add(besteAutos.TryGetValue(wer, out var a) ? a.Count.ToString() : "0");
                if (wer != niemand) { item.ForeColor = Color.FromArgb(126, 231, 135); }
                _liste.Items.Add(item);
            }
            _summe.Text = string.Format(Loc.T("{0} best laps of yours, on tunes by {1} tuners."), besteZahl,
                                        zeilen.Count(kv => kv.Value.Beste > 0 && kv.Key != niemand))
                          + "  " + Loc.T("The game does not record which tune a lap was driven with: \"~\" marks laps given to the tune saved last before them.");
        }
        else
        {
            // DEINE PERSOENLICHE RANGLISTE: von wem du die meisten Tunes hast.
            Spalten(("#", 45), (Loc.T("Tuner"), 240), (Loc.T("Tunes saved"), 110), (Loc.T("Tunes on a car"), 120),
                    (Loc.T("Cars"), 70));
            foreach (var (wer, z) in zeilen.Where(kv => kv.Value.Tunes > 0)
                                           .OrderByDescending(kv => kv.Value.Tunes)
                                           .ThenByDescending(kv => kv.Value.Auf))
            {
                platz++;
                var item = new ListViewItem(platz.ToString());
                item.SubItems.Add(wer);
                item.SubItems.Add(z.Tunes.ToString());
                item.SubItems.Add(_nutzung is null ? "?" : z.Auf.ToString());
                item.SubItems.Add(z.Autos.Count.ToString());
                _liste.Items.Add(item);
            }
            _summe.Text = string.Format(Loc.T("{0} tunes by {1} tuners."), tunes.Count, platz);
        }
        _liste.EndUpdate();
    }

    /// <summary>Die Garage aus dem Spielspeicher lesen und merken, welche Tunes auf einem Auto liegen.</summary>
    private void Pruefen()
    {
        if (!ForzaMemoryDb.GameRunning)
        {
            _pruefStand.Text = Loc.T("Start the game first -- the garage is read from its memory.");
            return;
        }
        _pruefen.Enabled = false;
        _pruefStand.Text = Loc.T("searching the game's memory for its database ...");
        var ablage = Path.Combine(AppInfo.TempFolder, "garage");
        Task.Run(() =>
        {
            string? fund = null;
            var meldung = string.Empty;
            HashSet<string>? belegt = null;
            try
            {
                var funde = ForzaMemoryDb.Dump(
                    ablage,
                    m => BeiUns(() => _pruefStand.Text = m),
                    pfad =>
                    {
                        if (GarageReader.FindGarage(new[] { pfad }) is null) { return false; }
                        fund = pfad;
                        return true;
                    });
                fund ??= GarageReader.FindGarage(funde.Select(f => f.Path));
                if (fund is not null) { belegt = GarageReader.AppliedTunes(fund); }
            }
            catch (Exception fehler)
            {
                meldung = fehler.Message;
            }
            BeiUns(() =>
            {
                _pruefen.Enabled = true;
                if (belegt is null)
                {
                    _pruefStand.Text = meldung.Length > 0
                        ? meldung
                        : Loc.T("No garage found in the game's memory -- open My Cars once, then try again.");
                    return;
                }
                _nutzung = new TuneStorage.Usage { CheckedAt = DateTime.Now, Applied = belegt.ToList() };
                TuneStorage.SaveUsage(_nutzung);
                ZeigeStand();
                Fuellen();
            });
        });
    }

    private void BeiUns(Action was)
    {
        if (IsDisposed) { return; }
        if (IsHandleCreated && InvokeRequired) { BeginInvoke(was); }
        else { was(); }
    }
}
