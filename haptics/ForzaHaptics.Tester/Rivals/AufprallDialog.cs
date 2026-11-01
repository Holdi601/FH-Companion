using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Was das G-Meter ueber das Auto weiss, und welche Klaenge Einschlaege ausloesen (seit 2026-11-01).
/// </summary>
/// <remarks>
/// Der Nutzer: "das G-Meter soll sich an das Auto und seine Grenzen anpassen, Hochpunkte festhalten, auch
/// Einschlaege in Waende" und "Klaenge abspielen bei Einschlaegen aus einem Winkel, ueber oder zwischen
/// bestimmten G". Die Werte stehen live da, damit man die Schwelle an echten Einschlaegen einstellen kann.
/// </remarks>
internal sealed class AufprallDialog : Form
{
    private static readonly Color Grund = Color.FromArgb(24, 26, 31);
    private static readonly Color Feld = Color.FromArgb(40, 44, 52);
    private static readonly Color Leise = Color.FromArgb(150, 160, 172);

    private readonly OverlaySettings _settings;
    private readonly Func<GMeterStand?> _stand;
    private readonly Func<int, string?> _name;
    private readonly Action _zuruecksetzen;
    private readonly Label _auto = new() { AutoSize = true, ForeColor = Color.WhiteSmoke, MaximumSize = new Size(760, 0), Margin = new Padding(3, 2, 3, 2) };
    private readonly Label _rekord = new() { AutoSize = true, ForeColor = Color.Gainsboro, MaximumSize = new Size(760, 0), Margin = new Padding(3, 2, 3, 2) };
    private readonly Label _letzter = new() { AutoSize = true, ForeColor = Color.Gainsboro, MaximumSize = new Size(760, 0), Margin = new Padding(3, 2, 3, 8) };
    private readonly FlowLayoutPanel _regeln = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
    private readonly System.Windows.Forms.Timer _takt = new() { Interval = 500 };

    public AufprallDialog(OverlaySettings settings, Func<GMeterStand?> stand, Func<int, string?> name, Action zuruecksetzen)
    {
        _settings = settings;
        _stand = stand;
        _name = name;
        _zuruecksetzen = zuruecksetzen;
        Text = Loc.T("Car records and impacts");
        BackColor = Grund;
        ForeColor = Color.WhiteSmoke;
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(860, 680);
        MinimumSize = new Size(600, 400);
        var liste = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(14) };
        Controls.Add(liste);

        Label Titel(string text) => new() { Text = text, AutoSize = true, Font = new Font("Segoe UI Semibold", 11f), Margin = new Padding(3, 10, 3, 4) };
        Label Hinweis(string text) => new() { Text = text, AutoSize = true, ForeColor = Leise, MaximumSize = new Size(760, 0), Margin = new Padding(3, 2, 3, 6) };
        CheckBox Schalter(string text, bool wert, Action<bool> setzen)
        {
            var c = new CheckBox { Text = text, Checked = wert, AutoSize = true, ForeColor = Color.Gainsboro, Margin = new Padding(3, 4, 3, 2) };
            c.CheckedChanged += (_, _) => { setzen(c.Checked); _settings.Save(); };
            liste.Controls.Add(c);
            return c;
        }

        // ---- DAS AUTO --------------------------------------------------------------------
        liste.Controls.Add(Titel(Loc.T("This car")));
        liste.Controls.Add(_auto);
        liste.Controls.Add(_rekord);
        liste.Controls.Add(_letzter);
        var vergessen = new Button { Text = Loc.T("Reset this car"), AutoSize = true, FlatStyle = FlatStyle.Flat, ForeColor = Color.Gainsboro };
        vergessen.Click += (_, _) => { _zuruecksetzen(); Auffrischen(); };
        liste.Controls.Add(vergessen);
        Schalter(Loc.T("Adapt the scale to the car"), settings.GMeterAutoScale, v => _settings.GMeterAutoScale = v);
        liste.Controls.Add(Hinweis(Loc.T(
            "After 20 seconds of driving, the full scale sits just above the most this car has held -- each car keeps its own records, its outline and its hardest impact, also after a restart.")));
        Schalter(Loc.T("Show impacts on the G-meter"), settings.GMeterShowImpacts, v => _settings.GMeterShowImpacts = v);

        var schwelle = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
        schwelle.Controls.Add(new Label { Text = Loc.T("An impact is a jump of at least"), AutoSize = true, ForeColor = Color.Gainsboro, Margin = new Padding(3, 6, 3, 3) });
        var zahl = new NumericUpDown { Minimum = 1.5m, Maximum = 20m, DecimalPlaces = 1, Increment = 0.5m, Width = 64, BackColor = Feld, ForeColor = Color.White,
                                       Value = (decimal)Math.Clamp(settings.ImpactThreshold, 1.5, 20.0) };
        zahl.ValueChanged += (_, _) => { _settings.ImpactThreshold = (double)zahl.Value; _settings.Save(); };
        schwelle.Controls.Add(zahl);
        schwelle.Controls.Add(new Label { Text = "G", AutoSize = true, ForeColor = Color.Gainsboro, Margin = new Padding(3, 6, 3, 3) });
        liste.Controls.Add(schwelle);
        liste.Controls.Add(Hinweis(Loc.T(
            "Measured on recorded laps: a hit makes the acceleration jump from one moment to the next, hard braking does not. From 3 G of jump the car lost 17 km/h on average, from 6 G always. Lower finds light touches, higher only real crashes.")));

        // ---- KLAENGE ---------------------------------------------------------------------
        liste.Controls.Add(Titel(Loc.T("Sounds on impact")));
        Schalter(Loc.T("Play sounds"), settings.ImpactSoundsOn, v => _settings.ImpactSoundsOn = v);
        liste.Controls.Add(Hinweis(Loc.T(
            "The first rule that matches plays. Direction is where the hit came from: front, right, rear, left, or your own angles (0 = front, clockwise; 315 to 45 is the front). Max 0 means no upper limit. WAV, MP3, M4A, WMA and FLAC work.")));
        liste.Controls.Add(_regeln);
        var neu = new Button { Text = Loc.T("Add a sound ..."), AutoSize = true, FlatStyle = FlatStyle.Flat, ForeColor = Color.Gainsboro, Margin = new Padding(3, 6, 3, 3) };
        neu.Click += (_, _) =>
        {
            if (Waehlen(null) is not { } datei) { return; }
            _settings.ImpactSounds.Add(new AufprallRegel { File = datei });
            _settings.Save();
            RegelnAufbauen();
        };
        liste.Controls.Add(neu);
        RegelnAufbauen();

        var unten = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var fertig = new Button { Text = Loc.T("Done"), AutoSize = true };
        fertig.Click += (_, _) => Close();
        unten.Controls.Add(fertig);
        Controls.Add(unten);

        _takt.Tick += (_, _) => Auffrischen();
        _takt.Start();
        Auffrischen();
        FormClosed += (_, _) => _takt.Dispose();
    }

    private string Datei(string pfad) => string.IsNullOrWhiteSpace(pfad) ? Loc.T("(no file)") : Path.GetFileName(pfad);

    private string? Waehlen(string? jetzt)
    {
        using var wahl = new OpenFileDialog
        {
            Filter = Loc.T("Sound files") + "|*.wav;*.mp3;*.m4a;*.wma;*.flac|" + Loc.T("All files") + "|*.*",
            FileName = jetzt ?? string.Empty,
        };
        return wahl.ShowDialog(this) == DialogResult.OK ? wahl.FileName : null;
    }

    private void RegelnAufbauen()
    {
        _regeln.SuspendLayout();
        foreach (Control c in _regeln.Controls) { c.Dispose(); }
        _regeln.Controls.Clear();
        for (var i = 0; i < _settings.ImpactSounds.Count; i++) { _regeln.Controls.Add(Zeile(_settings.ImpactSounds[i])); }
        _regeln.ResumeLayout();
    }

    private Control Zeile(AufprallRegel r)
    {
        var z = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(812, 0), Margin = new Padding(0, 3, 0, 3), BackColor = Color.FromArgb(32, 35, 42), Padding = new Padding(4) };
        void Gespeichert() => _settings.Save();
        var an = new CheckBox { Checked = r.On, AutoSize = true, Margin = new Padding(3, 7, 3, 3) };
        an.CheckedChanged += (_, _) => { r.On = an.Checked; Gespeichert(); };
        z.Controls.Add(an);
        var datei = new Button { Text = Datei(r.File), Width = 124, AutoEllipsis = true, FlatStyle = FlatStyle.Flat, ForeColor = Color.Gainsboro };
        datei.Click += (_, _) =>
        {
            if (Waehlen(r.File) is not { } neu) { return; }
            r.File = neu;
            datei.Text = Datei(neu);
            Gespeichert();
        };
        z.Controls.Add(datei);
        var seite = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 96, BackColor = Feld, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        seite.Items.AddRange(new object[] { Loc.T("any side"), Loc.T("front"), Loc.T("right"), Loc.T("rear"), Loc.T("left"), Loc.T("own angles") });
        seite.SelectedIndex = (int)r.Side;
        z.Controls.Add(seite);
        NumericUpDown Zahl(decimal wert, decimal bis, int stellen, Action<decimal> setzen)
        {
            var n = new NumericUpDown { Minimum = 0, Maximum = bis, DecimalPlaces = stellen, Increment = stellen > 0 ? 0.5m : 5m, Width = 52,
                                        BackColor = Feld, ForeColor = Color.White, Value = Math.Clamp(wert, 0, bis) };
            n.ValueChanged += (_, _) => { setzen(n.Value); Gespeichert(); };
            return n;
        }
        Label Text(string t) => new() { Text = t, AutoSize = true, ForeColor = Leise, Margin = new Padding(4, 7, 2, 3) };
        var von = Zahl((decimal)r.From, 360, 0, v => r.From = (float)v);
        var bis = Zahl((decimal)r.To, 360, 0, v => r.To = (float)v);
        void Winkel() { von.Enabled = bis.Enabled = r.Side == AufprallSeite.Custom; }
        seite.SelectedIndexChanged += (_, _) => { r.Side = (AufprallSeite)Math.Max(0, seite.SelectedIndex); Winkel(); Gespeichert(); };
        Winkel();
        z.Controls.Add(von);
        z.Controls.Add(Text("–"));
        z.Controls.Add(bis);
        z.Controls.Add(Text("°"));
        z.Controls.Add(Text(Loc.T("from")));
        z.Controls.Add(Zahl((decimal)r.MinG, 40, 1, v => r.MinG = (float)v));
        z.Controls.Add(Text(Loc.T("to")));
        z.Controls.Add(Zahl((decimal)r.MaxG, 40, 1, v => r.MaxG = (float)v));
        z.Controls.Add(Text("G"));
        z.Controls.Add(Text(Loc.T("Volume")));
        z.Controls.Add(Zahl(r.Volume, 100, 0, v => r.Volume = (int)v));
        var probe = new Button { Text = "▶", Width = 32, FlatStyle = FlatStyle.Flat, ForeColor = Color.Gainsboro };
        probe.Click += (_, _) => AufprallKlang.Spielen(r.File, r.Volume);
        z.Controls.Add(probe);
        var weg = new Button { Text = "✕", Width = 32, FlatStyle = FlatStyle.Flat, ForeColor = Color.Gainsboro };
        weg.Click += (_, _) =>
        {
            _settings.ImpactSounds.Remove(r);
            Gespeichert();
            RegelnAufbauen();
        };
        z.Controls.Add(weg);
        return z;
    }

    private void Auffrischen()
    {
        var kultur = CultureInfo.CurrentCulture;
        if (_stand() is not { Auto: > 0 } s)
        {
            _auto.Text = Loc.T("No car yet -- drive, and the G-meter learns what it holds.");
            _rekord.Text = string.Empty;
            _letzter.Text = string.Empty;
            return;
        }
        var name = _name(s.Auto) ?? "#" + s.Auto.ToString(CultureInfo.InvariantCulture);
        _auto.Text = string.Format(kultur, Loc.T("{0}: held {1:0.00} G sideways, {2:0.00} G braking, {3:0.00} G accelerating in {4:0} minutes of driving. Scale now {5:0.##} G."),
            name, MathF.Max(s.Rechts, s.Links), s.Brems, s.Vor, s.Fahrzeit / 60f, s.Skala);
        _rekord.Text = s.Rekord is { } r
            ? string.Format(kultur, Loc.T("Hardest impact: {0:0.0} G, direction {1}, at {2:0} km/h ({3:d}) -- {4} impacts in all."),
                r.G, r.Richtung, r.Kmh, r.Zeit.ToLocalTime(), s.Aufpralle)
            : Loc.T("No impact yet.");
        _letzter.Text = s.Letzter is { } l
            ? string.Format(kultur, Loc.T("Last impact: {0:0.0} G, direction {1}, at {2:0} km/h, {3:T}."), l.G, l.Richtung, l.Kmh, l.Zeit.ToLocalTime())
            : string.Empty;
    }
}
