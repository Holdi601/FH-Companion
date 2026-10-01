using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Der Reiter "Race statistics": Platzierung, Siege und Startplatz je Modus und Rennart,
/// dazu die Verteilung von Start- und Zielplatz -- alles filterbar.
/// </summary>
/// <remarks>
/// Seit 2026-09-30. Plaetze werden am FELD gemessen: 0 % ist Erster, 100 % Letzter. So
/// lassen sich ein Sieg gegen vier und ein Sieg gegen elf nebeneinanderstellen.
///
/// Die Feldgroesse kennt nur ein Rennen, dessen Startaufstellung gelesen wurde (RaceGrid).
/// Aeltere Rennen, aus dem Rundenbestand nachgebaut (RaceArchive), zaehlen fuer Siege und
/// Plaetze; fuer die Prozentwerte nur, wenn "Feldgroesse schaetzen" an ist -- dann gilt der
/// schlechteste gesehene Platz als Feld, und das ist eine Untergrenze.
/// </remarks>
internal sealed class RaceStatsTab : UserControl
{
    private readonly Func<RivalsAdvisor?> _advisor;
    private readonly RaceFilter _f = new();
    private readonly FlowLayoutPanel _kacheln = new();
    private readonly FlowLayoutPanel _oben;
    private readonly BellChart _glocke = new();
    private readonly ListView _tabelle = new();
    private readonly Label _status = new();
    private readonly FlowLayoutPanel _klassen = new();
    private readonly CheckBox _nurFertig = new();
    private readonly CheckBox _schaetzen = new();
    private readonly Dictionary<string, Button> _mehr = new();
    private readonly Dictionary<string, string> _titel = new();
    private List<RaceRecord> _rennen = new();
    private CancellationTokenSource? _nachbau;
    private string _nachbauText = string.Empty;
    private bool _still;
    private readonly ComboBox _balken = new();
    private bool _balkenStill;
    private static readonly int[] BalkenZahlen = { 4, 5, 6, 8, 10, 12, 15, 20, 25 };

    private static readonly Color Grund = Color.FromArgb(24, 26, 31);
    private static readonly Color Feld = Color.FromArgb(18, 20, 24);

    public RaceStatsTab(Func<RivalsAdvisor?> advisor)
    {
        _advisor = advisor;
        BackColor = Grund;
        ForeColor = Color.WhiteSmoke;
        Dock = DockStyle.Fill;

        var oben = _oben = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 8, 8, 4),
            WrapContents = true,
        };
        foreach (var (key, text) in new[]
                 {
                     ("mode", Loc.T("Mode")), ("cat", Loc.T("Category")),
                     ("course", Loc.T("Course")), ("car", Loc.T("Car")),
                 })
        {
            var knopf = Knopf(text + ": " + Loc.T("all"));
            knopf.Click += (_, _) => Auswaehlen(key, knopf);
            _mehr[key] = knopf;
            _titel[key] = text;
            oben.Controls.Add(knopf);
        }
        oben.Controls.Add(Beschriftung(Loc.T("Class")));
        _klassen.AutoSize = true;
        _klassen.WrapContents = false;
        foreach (var k in new[] { "D", "C", "B", "A", "S1", "S2", "R", "X" })
        {
            var cb = new CheckBox { Text = k, AutoSize = true, ForeColor = Color.WhiteSmoke };
            cb.CheckedChanged += (_, _) =>
            {
                if (cb.Checked) { _f.Classes.Add(k); } else { _f.Classes.Remove(k); }
                Anwenden();
            };
            _klassen.Controls.Add(cb);
        }
        oben.Controls.Add(_klassen);

        _nurFertig.Text = Loc.T("finished races only");
        _nurFertig.Checked = true;
        _schaetzen.Text = Loc.T("estimate field size for older races");
        foreach (var c in new[] { _nurFertig, _schaetzen })
        {
            c.AutoSize = true;
            c.ForeColor = Color.WhiteSmoke;
            c.CheckedChanged += (_, _) => Anwenden();
            oben.Controls.Add(c);
        }
        // WIE VIELE BALKEN (seit 2026-10-01): von selbst mehr, je mehr Rennen es gibt; waehlbar.
        // Beschriftung und Auswahl in EINER Gruppe: frei im Umbruch stand die Beschriftung am
        // Ende der einen Zeile und die Auswahl am Anfang der naechsten.
        var balkenGruppe = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
            Margin = new Padding(0),
        };
        balkenGruppe.Controls.Add(Beschriftung(Loc.T("Bars in the chart")));
        _balken.DropDownStyle = ComboBoxStyle.DropDownList;
        // So breit wie der laengste Eintrag: "automatisch (25)" wurde abgeschnitten.
        _balken.Width = Math.Max(90, TextRenderer.MeasureText(string.Format(Loc.T("auto ({0})"), 25), _balken.Font).Width + 30);
        _balken.Items.Add(string.Format(Loc.T("auto ({0})"), RaceStats.Bins));
        foreach (var n in BalkenZahlen) { _balken.Items.Add(n.ToString()); }
        _balken.SelectedIndex = 0;
        _balken.SelectedIndexChanged += (_, _) =>
        {
            if (_balkenStill) { return; }
            _glocke.Waehle(_balken.SelectedIndex <= 0 ? null : BalkenZahlen[_balken.SelectedIndex - 1]);
            BalkenText();
        };
        balkenGruppe.Controls.Add(_balken);
        oben.Controls.Add(balkenGruppe);

        var zurueck = Knopf(Loc.T("Reset"));
        zurueck.Click += (_, _) => Zuruecksetzen();
        oben.Controls.Add(zurueck);

        _kacheln.Dock = DockStyle.Top;
        _kacheln.AutoSize = true;
        _kacheln.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _kacheln.Padding = new Padding(8, 2, 8, 2);
        _kacheln.WrapContents = true;

        _glocke.Dock = DockStyle.Top;
        _glocke.Height = 280;

        _tabelle.Dock = DockStyle.Fill;
        _tabelle.View = View.Details;
        _tabelle.FullRowSelect = true;
        _tabelle.BackColor = Feld;
        _tabelle.ForeColor = Color.WhiteSmoke;
        foreach (var (text, breite, rechts) in new[]
                 {
                     (Loc.T("Mode"), 150, false), (Loc.T("Category"), 130, false),
                     (Loc.T("Races"), 60, true), (Loc.T("Wins"), 70, true), (Loc.T("Podiums"), 80, true),
                     (Loc.T("Avg finish"), 90, true), (Loc.T("Avg start"), 90, true),
                     (Loc.T("Avg field"), 80, true), (Loc.T("Gained"), 70, true),
                     (Loc.T("Vs co-players"), 110, true), (Loc.T("Meta picks"), 90, true), (Loc.T("Avg car rank"), 110, true),
                 })
        {
            // Nie schmaler als die Ueberschrift: "Gewonnen" passte nicht in die Breite von "Gained".
            var kopf = TextRenderer.MeasureText(text, _tabelle.Font).Width + 18;
            _tabelle.Columns.Add(text, Math.Max(breite, kopf), rechts ? HorizontalAlignment.Right : HorizontalAlignment.Left);
        }

        _status.Dock = DockStyle.Bottom;
        _status.ForeColor = Color.Gray;
        _status.Padding = new Padding(8, 4, 8, 0);
        Resize += (_, _) => { Status(_status.Text); Hoehen(); };

        Controls.Add(_tabelle);
        Controls.Add(_glocke);
        Controls.Add(_kacheln);
        Controls.Add(oben);
        Controls.Add(_status);
    }

    /// <summary>Fuer --main-preview: die Schaetzung der Feldgroesse einschalten.</summary>
    internal void SetEstimate(bool an) => _schaetzen.Checked = an;

    /// <summary>Beim Oeffnen: die Rennen laden, aeltere aus dem Rundenbestand im Hintergrund nachbauen.</summary>
    public void Reload()
    {
        var live = RaceLog.LoadLive();
        var ids = new HashSet<string>(live.Select(r => r.Id), StringComparer.Ordinal);
        _kategorien.Clear();
        _meta.Clear();
        _rennen = Zusammen(live, _alt);
        Anwenden();
        if (_nachbau is not null) { return; }
        _nachbau = new CancellationTokenSource();
        var abbruch = _nachbau.Token;
        // Ein eigener Faden mit niedriger Prioritaet: der erste Nachbau liest jede volle
        // Spur einmal (rund zehn Sekunden), und das Spiel laeuft vielleicht gerade.
        new Thread(() =>
        {
            var alt = RaceArchive.Update(LapArchive.Root, RaceLog.ArchivePath, ids,
                (i, n) =>
                {
                    if (i % 10 != 0) { return; }
                    Zurueck(() =>
                    {
                        _nachbauText = string.Format(Loc.T("Rebuilding older races from your saved telemetry: {0} of {1}"), i, n);
                        Status(Zeile());
                    });
                }, abbruch);
            Zurueck(() =>
            {
                _nachbauText = string.Empty;
                _alt = alt;
                _rennen = Zusammen(RaceLog.LoadLive(), alt);
                Anwenden();
                _nachbau = null;
            });
        })
        { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Rennstatistik-Nachbau" }.Start();
    }

    private List<RaceRecord> _alt = new();
    private readonly Dictionary<string, string?> _kategorien = new(StringComparer.Ordinal);

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

    /// <summary>Live aufgezeichnete Rennen gehen vor nachgebauten derselben Kennung.</summary>
    private static List<RaceRecord> Zusammen(IEnumerable<RaceRecord> live, IEnumerable<RaceRecord> alt)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var raus = new List<RaceRecord>();
        foreach (var r in live.Concat(alt))
        {
            if (r.Id.Length > 0 && !ids.Add(r.Id)) { continue; }
            raus.Add(r);
        }
        return raus;
    }

    private readonly Dictionary<(string?, string, int), (int? Platz, int? Autos)> _meta = new();

    /// <summary>Platz des Autos auf der Bestenliste -- festgehalten oder, bei aelteren, aus der heutigen Liste.</summary>
    private (int? Platz, int? Autos) MetaVon(RaceRecord r)
    {
        if (r.MetaCars is not null) { return (r.MetaRank, r.MetaCars); }
        var schluessel = (r.Track, r.Klass, r.Car);
        if (_meta.TryGetValue(schluessel, out var wert)) { return wert; }
        return _meta[schluessel] = RaceStats.MetaVon(r, _advisor());
    }

    private string? KategorieVon(RaceRecord r)
    {
        if (r.Track is not { Length: > 0 } t) { return null; }
        if (_kategorien.TryGetValue(t, out var k)) { return k; }
        if (_advisor() is not { } adv) { return null; }
        return _kategorien[t] = adv.CategoryOf(new[] { t });
    }

    private string AutoName(int ordinal)
    {
        var adv = _advisor();
        var name = adv?.CarIndexForId(ordinal) is { } i ? adv.RealCarName(i) : null;
        return name ?? string.Format(Loc.T("car {0}"), ordinal);
    }

    private string KursName(string kurs)
    {
        var r = _rennen.FirstOrDefault(x => x.Course == kurs && x.Track is not null);
        return OwnTimes.CourseText(kurs, r?.Track);
    }

    private List<RaceRecord> _auswahl = new();

    private void Anwenden()
    {
        if (_still) { return; }
        _f.FinishedOnly = _nurFertig.Checked;
        _f.EstimateField = _schaetzen.Checked;
        _auswahl = RaceStats.Apply(_rennen, _f, KategorieVon).ToList();
        var s = RaceStats.Summarize(_auswahl, _f.EstimateField, MetaVon);
        Kacheln(s);
        _glocke.Show(s);
        BalkenText();
        Tabelle();
        Status(Zeile());
    }

    /// <summary>"auto (8)": was die Automatik gerade waehlt, sichtbar im ersten Eintrag.</summary>
    private void BalkenText()
    {
        var text = string.Format(Loc.T("auto ({0})"), _glocke.AutoFaecher());
        if (_balken.Items.Count == 0 || _balken.Items[0] as string == text) { return; }
        _balkenStill = true;
        try
        {
            var gewaehlt = _balken.SelectedIndex;
            _balken.Items[0] = text;
            _balken.SelectedIndex = gewaehlt;
        }
        finally
        {
            _balkenStill = false;
        }
    }

    private string Zeile()
    {
        var live = _rennen.Count(r => r.Source == "live");
        var alt = _rennen.Count - live;
        var mitFeld = _rennen.Count(r => r.Drivers is not null);
        var text = string.Format(
            Loc.T("{0} race(s) shown of {1}: {2} recorded while driving, {3} rebuilt from saved laps. The field size is read from the start grid before each race; {4} race(s) have it. Positions in % of the field: 0% = first, 100% = last."),
            _auswahl.Count, _rennen.Count, live, alt, mitFeld);
        return _nachbauText.Length > 0 ? _nachbauText + " — " + text : text;
    }

    private void Status(string text)
    {
        _status.Text = text;
        var breite = Math.Max(200, (_status.Width > 0 ? _status.Width : Width) - _status.Padding.Horizontal - 8);
        _status.Height = TextRenderer.MeasureText(text, _status.Font, new Size(breite, 0), TextFormatFlags.WordBreak).Height
                         + _status.Padding.Vertical + 6;
    }

    private static string Prozent(double? x) => x is { } v ? (v * 100).ToString("0") + " %" : "–";
    private static string Zahl(double? x, string format = "0.0") => x is { } v ? v.ToString(format) : "–";

    private void Kacheln(RaceSummary s)
    {
        _kacheln.SuspendLayout();
        _kacheln.Controls.Clear();
        _kacheln.Controls.Add(Kachel(Loc.T("Races"), s.Races.ToString(),
            string.Format(Loc.T("{0} with opponents"), s.Placed)));
        _kacheln.Controls.Add(Kachel(Loc.T("Win rate"), Prozent(s.WinRate),
            string.Format(Loc.T("{0} wins, {1} podiums"), s.Wins, s.Podiums)));
        // PODIUM, nur in Feldern ab fuenf Fahrern (RaceStats.PodiumAb).
        _kacheln.Controls.Add(Kachel(Loc.T("Podium rate"), Prozent(s.PodiumRate),
            string.Format(Loc.T("top 3 in {0} of {1} race(s) with 5 or more drivers"), s.Podiums, s.PodiumRaces)));
        _kacheln.Controls.Add(Kachel(Loc.T("Avg finish"), Prozent(s.AvgFinishPct),
            string.Format(Loc.T("P{0} on average"), Zahl(s.AvgFinish))));
        _kacheln.Controls.Add(Kachel(Loc.T("Avg start"), Prozent(s.AvgStartPct),
            string.Format(Loc.T("P{0} on average"), Zahl(s.AvgStart))));
        var geschaetzt = _f.EstimateField
            ? _auswahl.Count(r => r.Drivers is null && RaceStats.HasOpponents(r) && RaceStats.Field(r, true) is not null)
            : 0;
        _kacheln.Controls.Add(Kachel(Loc.T("Avg field"), Zahl(s.AvgField),
            geschaetzt > 0
                ? string.Format(Loc.T("{0} race(s), {1} of them estimated"), s.WithField, geschaetzt)
                : string.Format(Loc.T("{0} race(s) with a known field"), s.WithField)));
        _kacheln.Controls.Add(Kachel(Loc.T("Gained"), s.AvgGain is { } g ? (g >= 0 ? "+" : "") + g.ToString("0.0") : "–",
            Loc.T("places from start to finish")));
        // GEGEN DIE MITSPIELER (Koop): welcher Anteil von ihnen kam hinter dir ins Ziel. Nur,
        // wenn Koop-Rennen in der Auswahl sind -- sonst waere die Kachel immer leer.
        if (_auswahl.Any(r => r.Mode == "coop"))
        {
            _kacheln.Controls.Add(Kachel(Loc.T("Vs co-players"), Prozent(s.CoBeaten),
                s.CoRaces > 0
                    ? string.Format(Loc.T("first of the humans in {0} of {1} race(s)"), s.CoFirst, s.CoRaces)
                    : Loc.T("co-op races only")));
        }
        // GEGEN DIE ERWARTUNG: wo man nach der Bestenliste haette ankommen sollen, und wo man ankam.
        if (s.ExpRaces > 0)
        {
            var gewinn = s.ExpAvgGain!.Value;
            _kacheln.Controls.Add(Kachel(Loc.T("Vs expectation"),
                (gewinn >= 0 ? "+" : "") + gewinn.ToString("0.0"),
                string.Format(Loc.T("{0} better, {1} as expected, {2} worse than the leaderboard says, in {3} race(s)"),
                              Prozent(s.ExpBetter / (double)s.ExpRaces), Prozent(s.ExpSame / (double)s.ExpRaces),
                              Prozent((s.ExpRaces - s.ExpBetter - s.ExpSame) / (double)s.ExpRaces), s.ExpRaces)));
        }
        // META-WAHL (Horizon Play): wie oft ein Auto unter den ersten 25 der Bestenliste gewaehlt wurde.
        if (_auswahl.Any(r => r.Mode == "horizon-play"))
        {
            _kacheln.Controls.Add(Kachel(Loc.T("Meta picks"),
                s.MetaRaces > 0 ? Prozent((s.MetaHigh + s.MetaLow) / (double)s.MetaRaces) : "–",
                s.MetaRaces > 0
                    ? string.Format(Loc.T("{0} high (top 15), {1} low (16–25) of {2} Horizon Play race(s)"),
                                    Prozent(s.MetaHigh / (double)s.MetaRaces), Prozent(s.MetaLow / (double)s.MetaRaces), s.MetaRaces)
                    : Loc.T("Horizon Play races on a route with a leaderboard only")));
            // DER DURCHSCHNITTLICHE PLATZ der gewaehlten Autos auf den Bestenlisten.
            var ohneListe = s.MetaRaces - s.MetaRanked;
            _kacheln.Controls.Add(Kachel(Loc.T("Avg car rank"),
                s.MetaAvgRank is { } platz ? "#" + platz.ToString("0") : "–",
                s.MetaRanked > 0
                    ? string.Format(Loc.T("top {0} of the leaderboard in {1} Horizon Play race(s)"), Prozent(s.MetaAvgTop), s.MetaRanked)
                      + (ohneListe > 0 ? "\n" + string.Format(Loc.T("{0} more with a car not on the leaderboard"), ohneListe) : string.Empty)
                    : Loc.T("Horizon Play races on a route with a leaderboard only")));
        }
        _kacheln.ResumeLayout();
        Hoehen();
    }

    private static Control Kachel(string titel, string wert, string unter)
    {
        var p = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Feld,
            Margin = new Padding(4),
            Padding = new Padding(10, 6, 14, 6),
            MinimumSize = new Size(150, 0),
        };
        p.Controls.Add(new Label { Text = titel, AutoSize = true, ForeColor = Color.Gray });
        p.Controls.Add(new Label
        {
            Text = wert, AutoSize = true, ForeColor = Color.WhiteSmoke,
            Font = new Font("Segoe UI Semibold", 18f),
        });
        p.Controls.Add(new Label { Text = unter, AutoSize = true, ForeColor = Color.Gray });
        return p;
    }

    /// <summary>
    /// Die Glocke bekommt, was nach Filtern, Kacheln und Statuszeile bleibt -- hoechstens 300,
    /// mindestens 150 Punkte, und die Tabelle behaelt immer ihren Teil.
    /// </summary>
    private void Hoehen()
    {
        var rest = ClientSize.Height - _oben.Height - _kacheln.Height - _status.Height;
        _glocke.Height = Math.Clamp((int)(rest * 0.6), 150, 300);
    }

    private void Tabelle()
    {
        var gruppen = _auswahl
            .GroupBy(r => (Modus: r.Mode, Kat: KategorieVon(r) ?? string.Empty))
            .OrderBy(g => OwnTimes.ModeText(g.Key.Modus), StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(g => g.Key.Kat.Length == 0 ? 1 : 0)
            .ThenBy(g => g.Key.Kat, StringComparer.CurrentCultureIgnoreCase);
        _tabelle.BeginUpdate();
        _tabelle.Items.Clear();
        foreach (var g in gruppen)
        {
            var s = RaceStats.Summarize(g.ToList(), _f.EstimateField, MetaVon);
            var z = new ListViewItem(OwnTimes.ModeText(g.Key.Modus));
            z.SubItems.Add(g.Key.Kat.Length > 0 ? g.Key.Kat : Loc.T("unknown"));
            z.SubItems.Add(s.Races.ToString());
            z.SubItems.Add(Prozent(s.WinRate));
            z.SubItems.Add(Prozent(s.PodiumRate));
            z.SubItems.Add(Prozent(s.AvgFinishPct));
            z.SubItems.Add(Prozent(s.AvgStartPct));
            z.SubItems.Add(Zahl(s.AvgField));
            z.SubItems.Add(s.AvgGain is { } a ? (a >= 0 ? "+" : "") + a.ToString("0.0") : "–");
            z.SubItems.Add(s.CoRaces > 0 ? $"{Prozent(s.CoBeaten)} ({s.CoFirst}/{s.CoRaces})" : "–");
            z.SubItems.Add(s.MetaRaces > 0 ? Prozent((s.MetaHigh + s.MetaLow) / (double)s.MetaRaces) : "–");
            z.SubItems.Add(s.MetaAvgRank is { } schnitt ? $"#{schnitt:0} ({Prozent(s.MetaAvgTop)})" : "–");
            _tabelle.Items.Add(z);
        }
        _tabelle.EndUpdate();
    }

    /// <summary>Die Liste zum Ankreuzen fuer einen Mengen-Filter -- nur Werte, die in den eigenen Rennen vorkommen.</summary>
    private void Auswaehlen(string key, Button knopf)
    {
        List<(string Wert, string Text)> liste = key switch
        {
            "mode" => _rennen.Select(r => r.Mode).Distinct().Select(m => (m, OwnTimes.ModeText(m))).ToList(),
            "cat" => _rennen.Select(KategorieVon).Where(c => c is not null).Select(c => c!).Distinct()
                            .Select(c => (c, c)).ToList(),
            "course" => _rennen.Select(r => r.Course).Where(c => c.Length > 0).Distinct()
                               .Select(c => (c, KursName(c))).ToList(),
            _ => _rennen.Select(r => r.Car).Distinct().Select(c => (c.ToString(), AutoName(c))).ToList(),
        };
        liste = liste.OrderBy(t => t.Text, StringComparer.CurrentCultureIgnoreCase).ToList();
        var gewaehlt = key switch
        {
            "mode" => _f.Modes,
            "cat" => _f.Categories,
            "course" => _f.Courses,
            _ => new HashSet<string>(_f.Cars.Select(c => c.ToString())),
        };

        using var dlg = new Form
        {
            Text = _titel[key],
            Size = new Size(360, 480),
            StartPosition = FormStartPosition.CenterParent,
            BackColor = Grund,
            ForeColor = Color.WhiteSmoke,
            FormBorderStyle = FormBorderStyle.SizableToolWindow,
        };
        var cl = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, BackColor = Feld, ForeColor = Color.WhiteSmoke };
        foreach (var (wert, text) in liste) { cl.Items.Add(new Eintrag(wert, text), gewaehlt.Contains(wert)); }
        var unten = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36, FlowDirection = FlowDirection.RightToLeft };
        var ok = Knopf(Loc.T("Apply"));
        ok.DialogResult = DialogResult.OK;
        var alle = Knopf(Loc.T("all"));
        alle.Click += (_, _) => { for (var i = 0; i < cl.Items.Count; i++) { cl.SetItemChecked(i, false); } };
        unten.Controls.Add(ok);
        unten.Controls.Add(alle);
        dlg.Controls.Add(cl);
        dlg.Controls.Add(unten);
        dlg.AcceptButton = ok;
        if (dlg.ShowDialog(this) != DialogResult.OK) { return; }

        var neu = cl.CheckedItems.Cast<Eintrag>().Select(e => e.Wert).ToList();
        switch (key)
        {
            case "mode": _f.Modes.Clear(); foreach (var w in neu) { _f.Modes.Add(w); } break;
            case "cat": _f.Categories.Clear(); foreach (var w in neu) { _f.Categories.Add(w); } break;
            case "course": _f.Courses.Clear(); foreach (var w in neu) { _f.Courses.Add(w); } break;
            default:
                _f.Cars.Clear();
                foreach (var w in neu) { if (int.TryParse(w, out var c)) { _f.Cars.Add(c); } }
                break;
        }
        knopf.Text = _titel[key] + ": " + (neu.Count == 0 ? Loc.T("all") : neu.Count.ToString());
        Anwenden();
    }

    private sealed record Eintrag(string Wert, string Text)
    {
        public override string ToString() => Text;
    }

    private void Zuruecksetzen()
    {
        _balken.SelectedIndex = 0;
        _still = true;
        _f.Modes.Clear(); _f.Categories.Clear(); _f.Courses.Clear(); _f.Cars.Clear(); _f.Classes.Clear();
        foreach (Control c in _klassen.Controls) { if (c is CheckBox cb) { cb.Checked = false; } }
        foreach (var (key, k) in _mehr) { k.Text = _titel[key] + ": " + Loc.T("all"); }
        _nurFertig.Checked = true;
        _schaetzen.Checked = false;
        _still = false;
        Anwenden();
    }

    private static Label Beschriftung(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = Color.Gray, Padding = new Padding(8, 6, 2, 0),
    };

    private static Button Knopf(string text) => new()
    {
        Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat,
        BackColor = Feld, ForeColor = Color.WhiteSmoke, Margin = new Padding(3),
    };
}

/// <summary>
/// Start- und Zielplatz als Verteilung: je Zehntel des Felds ein Balkenpaar, darueber die
/// Glockenkurve (Normalverteilung mit Mittelwert und Streuung der Auswahl).
/// </summary>
/// <remarks>
/// Beide Reihen als Anteil ihrer Rennen, nicht als Anzahl: der Startplatz ist bei weniger
/// Rennen bekannt als der Zielplatz, und zwei Zaehlungen verschiedener Groesse auf einer
/// Achse sahen aus wie ein Unterschied, der keiner ist. Farben nach dem Leitfaden der
/// Diagramme geprueft (Blau/Orange, dunkler Grund #121418: Farbsehschwaeche ΔE 26,8,
/// Kontrast ueber 3:1).
/// </remarks>
internal sealed class BellChart : Control
{
    internal static readonly Color StartFarbe = ColorTranslator.FromHtml("#3987e5");
    internal static readonly Color ZielFarbe = ColorTranslator.FromHtml("#d95926");
    private static readonly Color Flaeche = Color.FromArgb(18, 20, 24);
    private static readonly Color Gitter = Color.FromArgb(44, 48, 56);
    private static readonly Color Leise = Color.FromArgb(150, 154, 162);

    private RaceSummary? _s;
    private int _ueber = -1;
    // Die Faecher des Bilds: gewaehlt oder von selbst (RaceStats.AutoBins).
    private int? _wahl;
    private int _faecher = RaceStats.Bins;
    private int[] _startB = new int[RaceStats.Bins];
    private int[] _zielB = new int[RaceStats.Bins];

    /// <summary>Wie viele Balken die Automatik fuer die gezeigten Rennen waehlt.</summary>
    internal int AutoFaecher() => _autoFaecher;
    private int _autoFaecher = RaceStats.Bins;
    private readonly ToolTip _tipp = new() { InitialDelay = 0, ReshowDelay = 0 };

    public BellChart()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Flaeche;
        ForeColor = Color.WhiteSmoke;
        Font = new Font("Segoe UI", 9f);
    }

    public void Show(RaceSummary s)
    {
        _s = s;
        _ueber = -1;
        Faecher();
        Invalidate();
    }

    /// <summary>Die Zahl der Balken: eine feste, oder null fuer die Automatik.</summary>
    public void Waehle(int? faecher)
    {
        _wahl = faecher;
        _ueber = -1;
        Faecher();
        Invalidate();
    }

    private void Faecher()
    {
        if (_s is null) { return; }
        _autoFaecher = RaceStats.AutoBins(Math.Max(_s.StartN, _s.FinishN), _s.AvgField);
        _faecher = _wahl ?? _autoFaecher;
        _startB = RaceStats.Faecher(_s.StartShares, _faecher);
        _zielB = RaceStats.Faecher(_s.FinishShares, _faecher);
    }

    private Rectangle Plot => new(52, 34, Math.Max(10, Width - 52 - 20), Math.Max(10, Height - 34 - 48));

    private static double Anteil(int[] faecher, int n, int i) => n > 0 ? faecher[i] / (double)n : 0;

    // Die Dichte auf die Breite eines Fachs gerechnet -- so steht die Glocke auf derselben Skala wie die Balken.
    private double Glocke((double Mean, double Sd) fit, double x) =>
        Math.Exp(-0.5 * Math.Pow((x - fit.Mean) / fit.Sd, 2)) / (fit.Sd * Math.Sqrt(2 * Math.PI)) / _faecher;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var p = Plot;
        using var text = new SolidBrush(ForeColor);
        using var leise = new SolidBrush(Leise);
        g.DrawString(Loc.T("Where you start and finish, in % of the field"), Font, text, 8, 6);

        if (_s is null || (_s.StartN == 0 && _s.FinishN == 0))
        {
            var leer = Loc.T("No races with a known field size for these filters yet. It is read from the start grid before each race; for older races, switch on the estimate above.");
            var r = new RectangleF(p.X, p.Y + (p.Height / 3f), p.Width, p.Height / 2f);
            g.DrawString(leer, Font, leise, r, new StringFormat { Alignment = StringAlignment.Center });
            return;
        }

        // Hoechster Wert: Balken und Kurven, auf 5 % aufgerundet.
        var max = 0.05;
        for (var i = 0; i < _faecher; i++)
        {
            max = Math.Max(max, Math.Max(Anteil(_startB, _s.StartN, i), Anteil(_zielB, _s.FinishN, i)));
        }
        foreach (var fit in new[] { _s.StartFit, _s.FinishFit })
        {
            if (fit is { } f) { max = Math.Max(max, Glocke(f, Math.Clamp(f.Mean, 0, 1))); }
        }
        // Runde Teilstriche: 5, 10, 20 oder 25 Prozent je Schritt, hoechstens fuenf Schritte.
        var stufe = new[] { 0.05, 0.1, 0.2, 0.25 }.First(x => max / x <= 5 || x == 0.25);
        var stufen = Math.Max(1, (int)Math.Ceiling((max / stufe) - 1e-9));
        max = stufe * stufen;

        using var gitter = new Pen(Gitter, 1);
        for (var k = 0; k <= stufen; k++)
        {
            var y = p.Bottom - (float)(p.Height * k / (double)stufen);
            g.DrawLine(gitter, p.Left, y, p.Right, y);
            var t = (stufe * k * 100).ToString("0") + " %";
            var sz = g.MeasureString(t, Font);
            g.DrawString(t, Font, leise, p.Left - sz.Width - 4, y - (sz.Height / 2));
        }
        foreach (var (anteil, label) in new[] { (0.0, "0 %"), (0.25, "25 %"), (0.5, "50 %"), (0.75, "75 %"), (1.0, "100 %") })
        {
            var x = p.Left + (float)(p.Width * anteil);
            var sz = g.MeasureString(label, Font);
            g.DrawString(label, Font, leise, x - (sz.Width / 2), p.Bottom + 4);
        }
        // WELCHES ENDE VORN IST, an den Enden selbst (seit 2026-10-01): eine Zeile in der Mitte
        // las der Nutzer nicht -- "heisst nah an 0 Erster oder Letzter?". Fuer Start und Ziel gleich.
        var links = Loc.T("← 0 % = first place (pole, win)");
        var rechts = Loc.T("100 % = last place →");
        g.DrawString(links, Font, text, p.Left, p.Bottom + 22);
        var rz = g.MeasureString(rechts, Font);
        g.DrawString(rechts, Font, text, p.Right - rz.Width, p.Bottom + 22);

        // Balkenpaare je Fach, 2 Punkte Luft zwischen den Balken.
        var fach = p.Width / (float)_faecher;
        var rand = Math.Min(4f, fach / 8f);
        var balken = Math.Max(1f, (fach - (2 * rand) - 2f) / 2f);
        for (var i = 0; i < _faecher; i++)
        {
            var x0 = p.Left + (fach * i) + rand;
            Balken(g, StartFarbe, x0, balken, Anteil(_startB, _s.StartN, i), max, p, i == _ueber);
            Balken(g, ZielFarbe, x0 + balken + 2f, balken, Anteil(_zielB, _s.FinishN, i), max, p, i == _ueber);
        }

        // Die Glocken, 2 Punkte stark.
        foreach (var (fit, farbe) in new[] { (_s.StartFit, StartFarbe), (_s.FinishFit, ZielFarbe) })
        {
            if (fit is not { } f) { continue; }
            var punkte = new List<PointF>();
            for (var k = 0; k <= 100; k++)
            {
                var x = k / 100.0;
                var y = Glocke(f, x);
                punkte.Add(new PointF(p.Left + (float)(p.Width * x), p.Bottom - (float)(p.Height * Math.Min(1, y / max))));
            }
            using var stift = new Pen(farbe, 2f) { LineJoin = LineJoin.Round };
            g.DrawLines(stift, punkte.ToArray());
        }

        // Legende oben rechts, mit der Zahl der Rennen je Reihe.
        var lx = (float)p.Right;
        foreach (var (name, farbe, n) in new[]
                 {
                     (Loc.T("Finishing position"), ZielFarbe, _s.FinishN), (Loc.T("Starting position"), StartFarbe, _s.StartN),
                 })
        {
            var t = $"{name} ({n})";
            var sz = g.MeasureString(t, Font);
            lx -= sz.Width;
            g.DrawString(t, Font, text, lx, 8);
            lx -= 16;
            using var b = new SolidBrush(farbe);
            g.FillRectangle(b, lx, 11, 11, 11);
            lx -= 18;
        }
    }

    private static void Balken(Graphics g, Color farbe, float x, float breite, double anteil, double max, Rectangle p, bool hervor)
    {
        if (anteil <= 0) { return; }
        var h = (float)(p.Height * Math.Min(1, anteil / max));
        var r = new RectangleF(x, p.Bottom - h, breite, h);
        using var pfad = new GraphicsPath();
        var rad = Math.Min(4f, Math.Min(breite / 2f, h));
        pfad.AddArc(r.Left, r.Top, rad * 2, rad * 2, 180, 90);
        pfad.AddArc(r.Right - (rad * 2), r.Top, rad * 2, rad * 2, 270, 90);
        pfad.AddLine(r.Right, r.Bottom, r.Left, r.Bottom);
        pfad.CloseFigure();
        using var b = new SolidBrush(hervor ? farbe : Color.FromArgb(200, farbe));
        g.FillPath(b, pfad);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = Plot;
        var i = _s is null || e.X < p.Left || e.X > p.Right || e.Y < p.Top - 10 || e.Y > p.Bottom + 10
            ? -1 : Math.Clamp((int)((e.X - p.Left) / (p.Width / (float)_faecher)), 0, _faecher - 1);
        if (i == _ueber) { return; }
        _ueber = i;
        Invalidate();
        if (i < 0 || _s is null) { _tipp.Hide(this); return; }
        var von = (int)Math.Round(i * 100.0 / _faecher);
        var bis = (int)Math.Round((i + 1) * 100.0 / _faecher);
        var tipp = string.Format(Loc.T("{0}–{1}% of the field\nStarting position: {2} ({3} race(s))\nFinishing position: {4} ({5} race(s))"),
            von, bis,
            (Anteil(_startB, _s.StartN, i) * 100).ToString("0") + " %", _startB[i],
            (Anteil(_zielB, _s.FinishN, i) * 100).ToString("0") + " %", _zielB[i]);
        _tipp.Show(tipp, this, e.X + 14, e.Y + 14);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _ueber = -1;
        _tipp.Hide(this);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tipp.Dispose(); }
        base.Dispose(disposing);
    }
}
