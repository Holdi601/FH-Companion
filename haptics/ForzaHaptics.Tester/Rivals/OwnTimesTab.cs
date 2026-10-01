using System.Drawing;
using System.Windows.Forms;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Der Reiter "My times": die eigenen Rundenzeiten mit den Filtern der Seite.
/// </summary>
/// <remarks>
/// Oben die Filter, in derselben Reihenfolge und mit denselben Namen wie auf der
/// Auswertungsseite, damit man nicht umdenken muss. Darunter die Tabelle.
///
/// MEHRFACHAUSWAHL WIE AUF DER SEITE. Dort ist jeder Filter eine Menge ("Ford
/// oder Toyota", "B und A"), eine leere Menge heisst "alle". Hier ebenso: die
/// Knoepfe oeffnen eine Liste zum Ankreuzen. Ein einfaches Auswahlfeld waere
/// kuerzer, koennte aber nur einen Wert -- und dann waere es nicht mehr derselbe
/// Filter.
/// </remarks>
internal sealed class OwnTimesTab : UserControl
{
    private readonly Func<RivalsAdvisor?> _advisor;
    private readonly OwnTimes.Filter _f = new();
    private readonly ListView _tabelle = new();
    private readonly Label _zahl = new();
    private readonly TextBox _suche = new();
    // TEXTFELDER, keine Drehfelder: ein NumericUpDown kann nicht leer sein und
    // zeigt dann "0" -- das liest sich als Baujahr 0, nicht als "alle".
    private readonly TextBox _vonJahr = new();
    private readonly TextBox _bisJahr = new();
    private readonly ComboBox _start = new();
    private readonly ComboBox _art = new();
    private readonly CheckBox _beste = new();
    // DIE ANSICHT: die Runden selbst, oder die eigenen Autos gegeneinander gewertet
    // -- mit den Punkten der Auswertungsseite (siehe OwnTimes.Standings).
    private readonly ComboBox _ansicht = new();
    private List<OwnTimes.Standing> _wertung = new();
    private bool Wertung => _ansicht.SelectedIndex > 0;
    private readonly FlowLayoutPanel _klassen = new();
    private readonly FlowLayoutPanel _tunes = new();
    private readonly Dictionary<string, Button> _mehr = new();
    // Die Beschriftung je Mengen-Filter, gemerkt statt aus dem Knopftext
    // zurueckgelesen: eine Uebersetzung mit Doppelpunkt zerlegte sonst den Namen.
    private readonly Dictionary<string, string> _titel = new();
    private IReadOnlyList<OwnTimes.Lap> _laps = Array.Empty<OwnTimes.Lap>();
    private List<OwnTimes.Row> _zeilen = new();
    private int _sortSpalte = 0;
    private bool _sortAb;
    private bool _still;
    private Button? _aufraeumen;

    private static readonly Color Grund = Color.FromArgb(24, 26, 31);
    private static readonly Color Feld = Color.FromArgb(18, 20, 24);
    private static readonly Color Leit = Color.FromArgb(127, 211, 255);

    public OwnTimesTab(Func<RivalsAdvisor?> advisor)
    {
        _advisor = advisor;
        BackColor = Grund;
        ForeColor = Color.WhiteSmoke;
        Dock = DockStyle.Fill;

        var oben = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 8, 8, 4),
            WrapContents = true,
        };

        // --- Ansicht: zuerst, weil sie bestimmt, was die Tabelle ueberhaupt zeigt
        _ansicht.DropDownStyle = ComboBoxStyle.DropDownList;
        _ansicht.Items.AddRange(new object[]
        {
            Loc.T("Show my laps"), Loc.T("Standings by points"), Loc.T("Standings by total time"),
        });
        _ansicht.SelectedIndex = 0;
        _ansicht.SelectedIndexChanged += (_, _) =>
        {
            _sortSpalte = 0;
            _sortAb = false;
            Spalten();
            // "Beste je Auto und Kurs" gilt in der Wertung immer -- dort zaehlt je
            // Auto und Kurs ohnehin nur die schnellste Runde.
            _beste.Enabled = !Wertung;
            Anwenden();
        };
        Passend(_ansicht);
        oben.Controls.Add(_ansicht);

        // --- Klasse (Mehrfachwahl wie die Chips der Seite)
        oben.Controls.Add(Beschriftung(Loc.T("Class")));
        _klassen.AutoSize = true;
        _klassen.WrapContents = false;
        foreach (var k in new[] { "D", "C", "B", "A", "S1", "S2", "R" })
        {
            var cb = new CheckBox { Text = k, AutoSize = true, ForeColor = Color.WhiteSmoke };
            cb.CheckedChanged += (_, _) => Umschalten(_f.Classes, k, cb.Checked);
            _klassen.Controls.Add(cb);
        }
        oben.Controls.Add(_klassen);

        // --- Mengen-Filter als Knopf mit Ankreuzliste
        foreach (var (key, text) in new[]
                 {
                     ("cat", Loc.T("Category")), ("course", Loc.T("Course")),
                     ("make", Loc.T("Make")), ("country", Loc.T("Country")),
                     ("type", Loc.T("Type")), ("mode", Loc.T("Mode")),
                 })
        {
            var knopf = Knopf(text + ": " + Loc.T("all"));
            knopf.Click += (_, _) => Auswaehlen(key, knopf);
            _mehr[key] = knopf;
            _titel[key] = text;
            oben.Controls.Add(knopf);
        }

        // --- Abstimmung: down / same / up, wie auf der Seite
        oben.Controls.Add(Beschriftung(Loc.T("Tune")));
        _tunes.AutoSize = true;
        _tunes.WrapContents = false;
        foreach (var (key, text) in new[] { ("down", Loc.T("down")), ("same", Loc.T("stock class")),
                                            ("up", Loc.T("up")) })
        {
            var cb = new CheckBox { Text = text, AutoSize = true, ForeColor = Color.WhiteSmoke };
            cb.CheckedChanged += (_, _) => Umschalten(_f.Tunes, key, cb.Checked);
            _tunes.Controls.Add(cb);
        }
        oben.Controls.Add(_tunes);

        // --- Baujahr
        oben.Controls.Add(Beschriftung(Loc.T("Year")));
        _vonJahr.PlaceholderText = Loc.T("from");
        _bisJahr.PlaceholderText = Loc.T("to");
        foreach (var n in new[] { _vonJahr, _bisJahr })
        {
            n.Width = 56; n.MaxLength = 4;
            n.BackColor = Feld; n.ForeColor = Color.WhiteSmoke;
            n.TextChanged += (_, _) => Anwenden();
        }
        oben.Controls.Add(_vonJahr);
        oben.Controls.Add(Beschriftung("–"));
        oben.Controls.Add(_bisJahr);

        // --- Start und Art: nur bei eigenen Runden sinnvoll, darum zusaetzlich
        _start.DropDownStyle = ComboBoxStyle.DropDownList;
        _start.Items.AddRange(new object[] { Loc.T("any start"), Loc.T("standing"), Loc.T("flying") });
        _start.SelectedIndex = 0;
        _start.SelectedIndexChanged += (_, _) =>
        {
            _f.Start = _start.SelectedIndex switch { 1 => "standing", 2 => "flying", _ => "any" };
            Anwenden();
        };
        _art.DropDownStyle = ComboBoxStyle.DropDownList;
        _art.Items.AddRange(new object[] { Loc.T("laps and sprints"), Loc.T("laps"), Loc.T("sprints") });
        _art.SelectedIndex = 0;
        _art.SelectedIndexChanged += (_, _) =>
        {
            _f.Kind = _art.SelectedIndex switch { 1 => "lap", 2 => "sprint", _ => "any" };
            Anwenden();
        };
        // Breite nach dem laengsten Eintrag: "Runden und Sprints" passte nicht in
        // die Breite, die "laps and sprints" reichte.
        foreach (var c in new[] { _start, _art }) { Passend(c); }
        oben.Controls.Add(_start);
        oben.Controls.Add(_art);

        // --- Suche und "nur die beste je Auto und Kurs"
        _suche.Width = 160;
        _suche.BackColor = Feld;
        _suche.ForeColor = Color.WhiteSmoke;
        _suche.PlaceholderText = Loc.T("search car");
        _suche.TextChanged += (_, _) => { _f.CarSearch = _suche.Text.Trim(); Anwenden(); };
        oben.Controls.Add(_suche);

        _beste.Text = Loc.T("best per car and course");
        _beste.Checked = true;
        _beste.AutoSize = true;
        _beste.ForeColor = Color.WhiteSmoke;
        _beste.CheckedChanged += (_, _) => { _f.BestOnly = _beste.Checked; Anwenden(); };
        oben.Controls.Add(_beste);

        var zurueck = Knopf(Loc.T("Reset"));
        zurueck.Click += (_, _) => Zuruecksetzen();
        oben.Controls.Add(zurueck);

        // DER RUNDENBESTAND (seit 2026-10-01): wo die Runden liegen, und Platz schaffen.
        var ordner = Knopf(Loc.T("Open lap folder"));
        ordner.Click += (_, _) => OrdnerOeffnen();
        oben.Controls.Add(ordner);
        _aufraeumen = Knopf(Loc.T("Delete slower laps…"));
        _aufraeumen.Click += (_, _) => Aufraeumen();
        oben.Controls.Add(_aufraeumen);

        _zahl.Dock = DockStyle.Bottom;
        _zahl.Height = 24;
        // Breiter oder schmaler: der Satz bricht anders um, die Zeile muss mitwachsen.
        Resize += (_, _) => { if (_zahl.Text.Length > 0) { Status(_zahl.Text); } };
        _zahl.ForeColor = Color.Gray;
        _zahl.Padding = new Padding(8, 4, 0, 0);

        _tabelle.Dock = DockStyle.Fill;
        _tabelle.View = View.Details;
        _tabelle.FullRowSelect = true;
        _tabelle.BackColor = Feld;
        _tabelle.ForeColor = Color.WhiteSmoke;
        Spalten();
        _tabelle.ColumnClick += (_, e) =>
        {
            _sortAb = e.Column == _sortSpalte ? !_sortAb : false;
            _sortSpalte = e.Column;
            Zeichnen();
        };

        Controls.Add(_tabelle);
        Controls.Add(_zahl);
        Controls.Add(oben);
    }

    /// <summary>Den Ordner der Runden im Explorer zeigen.</summary>
    private static void OrdnerOeffnen()
    {
        try
        {
            var wurzel = LapArchive.Root;
            Directory.CreateDirectory(wurzel);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(wurzel)
            {
                UseShellExecute = true,
            })?.Dispose();
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Langsamere Runden loeschen -- erst zeigen, was weg ginge, dann nur auf Ja.</summary>
    private async void Aufraeumen()
    {
        if (_aufraeumen is not { Enabled: true } knopf) { return; }
        knopf.Enabled = false;
        var titel = Loc.T("Delete slower laps");
        try
        {
            var wurzel = LapArchive.Root;
            var plan = await Leise(() => LapCleanup.Planen(wurzel));
            if (IsDisposed) { return; }
            if (plan.Dateien == 0)
            {
                MessageBox.Show(this, Loc.T("Nothing to delete: every lap is already the fastest of its car, course and class."),
                                titel, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var frage = string.Format(Loc.T(
                "{0} slower laps and {1} unfinished runs will be deleted, {2} in total.\n\nKept: your fastest lap per course, PI class and car -- standing and flying starts and each game mode on their own, as your records count them. Race statistics stay complete.\n\nThis cannot be undone."),
                plan.Langsamere.Count, plan.Unfertige.Count, LapCleanup.Anzeige(plan.Bytes));
            if (MessageBox.Show(this, frage, titel, MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                return;
            }
            var e = await Leise(() => LapCleanup.Ausfuehren(wurzel, plan, RaceLog.ArchivePath));
            if (IsDisposed) { return; }
            var text = string.Format(Loc.T("Deleted: {0} laps. Freed: {1}."), e.Geloescht, LapCleanup.Anzeige(e.Bytes));
            if (e.Fehlgeschlagen > 0)
            {
                text += "\n\n" + string.Format(Loc.T(
                    "{0} file(s) could not be deleted -- another program may have them open. Try again later."),
                    e.Fehlgeschlagen);
            }
            MessageBox.Show(this, text, titel, MessageBoxButtons.OK, MessageBoxIcon.Information);
            Reload();
        }
        catch (Exception)
        {
        }
        finally
        {
            if (!knopf.IsDisposed) { knopf.Enabled = true; }
        }
    }

    /// <summary>Im Hintergrund, mit niedriger Prioritaet -- das Spiel laeuft vielleicht.</summary>
    private static Task<T> Leise<T>(Func<T> arbeit)
    {
        var fertig = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            try { fertig.SetResult(arbeit()); }
            catch (Exception e) { fertig.SetException(e); }
        })
        { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Rundenbestand" }.Start();
        return fertig.Task;
    }

    /// <summary>Fuer --mytimes-preview: 0 Runden, 1 Wertung nach Punkten, 2 nach Zeitsumme.</summary>
    internal void ShowView(int ansicht) => _ansicht.SelectedIndex = Math.Clamp(ansicht, 0, 2);

    /// <summary>Beim Oeffnen des Reiters neu einlesen.</summary>
    public void Reload()
    {
        _laps = OwnTimes.All();
        Anwenden();
    }

    private void Umschalten(HashSet<string> menge, string wert, bool an)
    {
        if (an) { menge.Add(wert); } else { menge.Remove(wert); }
        Anwenden();
    }

    private void Anwenden()
    {
        if (_still) { return; }
        // Nur ein vierstelliges Jahr gilt. "19" beim Tippen von "1995" als
        // Jahr 19 zu nehmen, liesse die Tabelle bei jedem Tastendruck springen.
        _f.YearFrom = Jahr(_vonJahr.Text);
        _f.YearTo = Jahr(_bisJahr.Text);
        _zeilen = OwnTimes.Query(_laps, _f, _advisor());
        _wertung = Wertung
            ? OwnTimes.Standings(_zeilen, nachPunkten: _ansicht.SelectedIndex == 1)
            : new List<OwnTimes.Standing>();
        Zeichnen();
    }

    /// <summary>Die Spalten der gewaehlten Ansicht.</summary>
    private void Spalten()
    {
        _tabelle.BeginUpdate();
        _tabelle.Items.Clear();
        _tabelle.Columns.Clear();
        if (Wertung)
        {
            _tabelle.Columns.Add("#", 40, HorizontalAlignment.Right);
            _tabelle.Columns.Add(Loc.T("Car"), 280);
            _tabelle.Columns.Add(Loc.T("Class"), 50);
            _tabelle.Columns.Add(Loc.T("Points"), 70, HorizontalAlignment.Right);
            _tabelle.Columns.Add(Loc.T("Courses"), 80, HorizontalAlignment.Right);
            _tabelle.Columns.Add(Loc.T("Wins"), 60, HorizontalAlignment.Right);
            _tabelle.Columns.Add(Loc.T("Total time"), 110, HorizontalAlignment.Right);
            _tabelle.Columns.Add(Loc.T("Tune"), 95);
        }
        else
        {
            _tabelle.Columns.Add(Loc.T("Course"), 230);
            _tabelle.Columns.Add(Loc.T("Car"), 250);
            _tabelle.Columns.Add(Loc.T("Class"), 50);
            _tabelle.Columns.Add(Loc.T("Time"), 80, HorizontalAlignment.Right);
            _tabelle.Columns.Add(Loc.T("Gap"), 70, HorizontalAlignment.Right);
            _tabelle.Columns.Add("PI", 45, HorizontalAlignment.Right);
            _tabelle.Columns.Add(Loc.T("Tune"), 95);
            _tabelle.Columns.Add(Loc.T("Start"), 118);
            _tabelle.Columns.Add(Loc.T("Mode"), 105);
            _tabelle.Columns.Add(Loc.T("Date"), 120);
        }
        _tabelle.EndUpdate();
    }

    // Die Klassen in der Reihenfolge der Seite; Unbekanntes ans Ende.
    private static int KlassenRang(string k) =>
        Array.IndexOf(new[] { "D", "C", "B", "A", "S1", "S2", "R", "X" }, k) is var i && i >= 0 ? i : 99;

    private void ZeichneWertung()
    {
        IEnumerable<OwnTimes.Standing> sortiert = _sortSpalte switch
        {
            1 => _wertung.OrderBy(s => s.CarName, StringComparer.OrdinalIgnoreCase),
            3 => _wertung.OrderByDescending(s => s.Points),
            4 => _wertung.OrderByDescending(s => s.Present),
            5 => _wertung.OrderByDescending(s => s.Wins),
            6 => _wertung.OrderBy(s => s.TotalSeconds),
            7 => _wertung.OrderBy(s => s.Tune ?? "~"),
            // Standard: je Klasse, darin nach Platz -- so, wie die Seite ihre Tabellen zeigt.
            _ => _wertung.OrderBy(s => KlassenRang(s.Klass)).ThenBy(s => s.Place),
        };
        if (_sortAb) { sortiert = sortiert.Reverse(); }

        _tabelle.BeginUpdate();
        _tabelle.Items.Clear();
        foreach (var s in sortiert)
        {
            var z = new ListViewItem(s.Place.ToString());
            z.SubItems.Add(s.CarName);
            z.SubItems.Add(s.Klass);
            z.SubItems.Add(s.Points.ToString());
            z.SubItems.Add($"{s.Present}/{s.Boards}");
            z.SubItems.Add(s.Wins.ToString());
            // Ein Stern, wenn die Summe geerbte Zeiten enthaelt -- sonst sieht ein Auto
            // mit drei gefahrenen Kursen aus, als habe es alle sieben so gefahren.
            z.SubItems.Add(OwnTimes.TimeText(s.TotalSeconds) + (s.Inherited > 0 ? " *" : string.Empty));
            z.SubItems.Add(OwnTimes.TuneText(s.Tune));
            if (s.Place == 1) { z.ForeColor = Leit; }
            _tabelle.Items.Add(z);
        }
        _tabelle.EndUpdate();

        Status(string.Format(
            Loc.T("{0} car(s) in {1} class(es), scored over {2} course(s). On each course, place 1 earns as many points as cars you drove there, like on the website; standing and flying starts count separately. * = the total includes the slowest time on courses that car did not drive."),
            _wertung.Count, _wertung.Select(s => s.Klass).Distinct().Count(),
            _wertung.GroupBy(s => s.Klass).Sum(g => g.First().Boards)));
    }

    private void Zeichnen()
    {
        if (Wertung) { ZeichneWertung(); return; }
        IEnumerable<OwnTimes.Row> sortiert = _sortSpalte switch
        {
            // NACH KURS, DARIN NACH ZEIT. Die Spalte "Gap" misst den Abstand zum
            // eigenen Besten AUF DIESEM KURS; nach Zeit quer ueber alle Kurse sortiert
            // steht eine +10-s-Zeile neben einer 0-s-Zeile eines anderen Kurses, und
            // die Spalte liest sich wie Unsinn.
            // Benannte Kurse zuerst: "Unnamed course" wuerde sonst je nach Sprache
            // unter U, N, S oder einem Schriftzeichen einsortiert.
            0 => _zeilen.OrderBy(r => r.Lap.CourseName.Length == 0 ? 1 : 0)
                        .ThenBy(r => KursText(r.Lap), StringComparer.OrdinalIgnoreCase)
                        .ThenBy(r => r.Lap.Seconds),
            1 => _zeilen.OrderBy(r => r.CarName, StringComparer.OrdinalIgnoreCase),
            2 => _zeilen.OrderBy(r => r.Lap.Klass, StringComparer.Ordinal),
            4 => _zeilen.OrderBy(r => r.Lap.Seconds - r.BestOnCourse),
            5 => _zeilen.OrderBy(r => r.Lap.Pi),
            6 => _zeilen.OrderBy(r => r.Tune ?? "~"),
            7 => _zeilen.OrderBy(r => r.Lap.Standing),
            8 => _zeilen.OrderBy(r => r.Lap.Mode, StringComparer.OrdinalIgnoreCase),
            9 => _zeilen.OrderBy(r => r.Lap.When),
            _ => _zeilen.OrderBy(r => r.Lap.Seconds),
        };
        if (_sortAb) { sortiert = sortiert.Reverse(); }

        _tabelle.BeginUpdate();
        _tabelle.Items.Clear();
        foreach (var r in sortiert)
        {
            var gap = r.Lap.Seconds - r.BestOnCourse;
            var z = new ListViewItem(KursText(r.Lap));
            z.SubItems.Add(r.CarName);
            z.SubItems.Add(r.Lap.Klass);
            z.SubItems.Add(OwnTimes.TimeText(r.Lap.Seconds));
            z.SubItems.Add(gap < 0.0005 ? "—" : "+" + gap.ToString("0.000",
                System.Globalization.CultureInfo.InvariantCulture));
            z.SubItems.Add(r.Lap.Pi > 0 ? r.Lap.Pi.ToString() : "-");
            z.SubItems.Add(OwnTimes.TuneText(r.Tune));
            z.SubItems.Add((r.Lap.Standing ? Loc.T("standing") : Loc.T("flying"))
                           + (r.Lap.Sprint ? " · " + Loc.T("sprint") : string.Empty));
            z.SubItems.Add(OwnTimes.ModeText(r.Lap.Mode));
            z.SubItems.Add(r.Lap.When == DateTime.MinValue ? "-"
                           : r.Lap.When.ToString("yyyy-MM-dd HH:mm"));
            // Die schnellste Runde je Kurs hervorheben -- auf einen Blick, welches
            // Auto dort das eigene Beste ist.
            if (gap < 0.0005) { z.ForeColor = Leit; }
            _tabelle.Items.Add(z);
        }
        // Tune, Start und Modus nach Inhalt breit machen: feste Breiten, die fuer
        // Englisch reichten, schnitten "stehend · Sprint" auf Japanisch ab.
        // Nie schmaler als die Ueberschrift.
        foreach (var i in new[] { 6, 7, 8 })
        {
            var kopf = TextRenderer.MeasureText(_tabelle.Columns[i].Text, _tabelle.Font).Width + 16;
            _tabelle.AutoResizeColumn(i, ColumnHeaderAutoResizeStyle.ColumnContent);
            if (_tabelle.Columns[i].Width < kopf) { _tabelle.Columns[i].Width = kopf; }
        }
        _tabelle.EndUpdate();

        var kurse = _zeilen.Select(r => r.Lap.Course).Distinct().Count();
        Status(string.Format(
            Loc.T("{0} lap(s) on {1} course(s), of {2} recorded. Assists, gearbox and valid/invalid are leaderboard facts the telemetry does not send, so they cannot filter your own laps."),
            _zeilen.Count, kurse, _laps.Count));
    }

    /// <summary>
    /// Die Zeile unter der Tabelle setzen -- umbrechend, und so hoch, wie der Satz es
    /// braucht. Fest 24 Pixel hoch schnitt die Erklaerung auf Deutsch mitten im Satz ab.
    /// </summary>
    private void Status(string text)
    {
        _zahl.Text = text;
        var breite = Math.Max(200, (_zahl.Width > 0 ? _zahl.Width : Width) - _zahl.Padding.Horizontal - 8);
        var hoch = TextRenderer.MeasureText(text, _zahl.Font, new Size(breite, 0),
                                            TextFormatFlags.WordBreak).Height;
        _zahl.Height = hoch + _zahl.Padding.Vertical + 6;
    }

    private static void Passend(ComboBox c)
    {
        var breit = c.Items.Cast<object>()
                     .Select(i => TextRenderer.MeasureText(i.ToString(), c.Font).Width)
                     .DefaultIfEmpty(60).Max();
        c.Width = breit + SystemInformation.VerticalScrollBarWidth + 12;
    }

    private static int? Jahr(string text) =>
        text.Trim().Length == 4 && int.TryParse(text.Trim(), out var j) ? j : null;

    private static string KursText(OwnTimes.Lap l) => OwnTimes.CourseText(l.Course, l.CourseName);

    /// <summary>Die Liste zum Ankreuzen fuer einen Mengen-Filter.</summary>
    private void Auswaehlen(string key, Button knopf)
    {
        var adv = _advisor();
        var data = adv?.Data;
        var (menge, werte) = key switch
        {
            "cat" => (_f.Categories, _laps.Select(l => adv?.CategoryOf(new[] { l.CourseName }))
                                            .Where(c => c is not null).Select(c => c!)),
            "course" => (_f.Courses, _laps.Select(l => l.Course)),
            "make" => (_f.Makes, Meta(m => m.Make)),
            "country" => (_f.Countries, Meta(m => m.Country)),
            "type" => (_f.Types, Meta(m => m.Type)),
            _ => (_f.Modes, _laps.Select(l => l.Mode)),
        };

        IEnumerable<string> Meta(Func<RivalsDataset.CarMetaEntry, string?> feld) =>
            _laps.Select(l => adv?.CarIndexForId(l.Ordinal))
                 .Where(i => i is not null && data is not null && i < data.CarMeta.Count)
                 .Select(i => data!.CarMeta[i!.Value])
                 .Where(m => m is not null)
                 .Select(m => feld(m!))
                 .Where(v => !string.IsNullOrWhiteSpace(v))
                 .Select(v => v!);

        // Nur Werte, die in den EIGENEN Runden vorkommen. Die Seite bietet alle
        // Marken des Datensatzes an; hier wuerde das Hunderte leerer Treffer
        // ergeben -- eine Marke, die man nie gefahren ist, filtert nichts.
        var liste = werte.Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(v => Anzeige(key, v),
                                  StringComparer.OrdinalIgnoreCase)
                         .ToList();

        using var dlg = new Form
        {
            Text = _titel[key],
            Size = new Size(340, 460),
            StartPosition = FormStartPosition.CenterParent,
            BackColor = Grund,
            ForeColor = Color.WhiteSmoke,
            FormBorderStyle = FormBorderStyle.SizableToolWindow,
        };
        var cl = new CheckedListBox
        {
            Dock = DockStyle.Fill,
            CheckOnClick = true,
            BackColor = Feld,
            ForeColor = Color.WhiteSmoke,
        };
        foreach (var w in liste)
        {
            cl.Items.Add(new Eintrag(w, Anzeige(key, w)), menge.Contains(w));
        }
        var unten = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36,
                                          FlowDirection = FlowDirection.RightToLeft };
        var ok = Knopf(Loc.T("Apply"));
        ok.DialogResult = DialogResult.OK;
        var alle = Knopf(Loc.T("all"));
        alle.Click += (_, _) => { for (var i = 0; i < cl.Items.Count; i++) cl.SetItemChecked(i, false); };
        unten.Controls.Add(ok);
        unten.Controls.Add(alle);
        dlg.Controls.Add(cl);
        dlg.Controls.Add(unten);
        dlg.AcceptButton = ok;

        if (dlg.ShowDialog(this) != DialogResult.OK) { return; }
        menge.Clear();
        foreach (var e in cl.CheckedItems.Cast<Eintrag>()) { menge.Add(e.Wert); }
        knopf.Text = _titel[key] + ": "
                     + (menge.Count == 0 ? Loc.T("all") : menge.Count.ToString());
        Anwenden();
    }

    // Was im Auswahlfenster steht. Gemerkt wird die Kennung, gezeigt ihr Name.
    private string Anzeige(string key, string wert) => key switch
    {
        "course" => KursName(wert),
        "mode" => OwnTimes.ModeText(wert),
        _ => wert,
    };

    private string KursName(string kurs)
    {
        var l = _laps.FirstOrDefault(x => x.Course == kurs);
        return OwnTimes.CourseText(kurs, l?.CourseName);
    }

    private sealed record Eintrag(string Wert, string Text)
    {
        public override string ToString() => Text;
    }

    private void Zuruecksetzen()
    {
        _still = true;
        _f.Categories.Clear(); _f.Classes.Clear(); _f.Courses.Clear(); _f.Makes.Clear();
        _f.Countries.Clear(); _f.Types.Clear(); _f.Tunes.Clear(); _f.Modes.Clear();
        foreach (Control c in _klassen.Controls) { if (c is CheckBox cb) cb.Checked = false; }
        foreach (Control c in _tunes.Controls) { if (c is CheckBox cb) cb.Checked = false; }
        foreach (var (key, k) in _mehr) { k.Text = _titel[key] + ": " + Loc.T("all"); }
        _vonJahr.Text = string.Empty; _bisJahr.Text = string.Empty;
        _start.SelectedIndex = 0; _art.SelectedIndex = 0;
        _suche.Text = string.Empty;
        _beste.Checked = true;
        _f.Start = "any"; _f.Kind = "any"; _f.CarSearch = string.Empty; _f.BestOnly = true;
        _still = false;
        Anwenden();
    }

    private static Label Beschriftung(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = Color.Gray,
        Padding = new Padding(8, 6, 2, 0),
    };

    private static Button Knopf(string text) => new()
    {
        Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat,
        BackColor = Feld, ForeColor = Color.WhiteSmoke, Margin = new Padding(3),
    };
}
