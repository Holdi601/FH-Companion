using System.Drawing;
using System.Windows.Forms;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Den Stil eines HUD-Stuecks selbst gestalten (seit 2026-10-24): Farben der Rollen, Rahmen, Ecken,
/// Leuchten, Schrift. Jede Aenderung liegt sofort auf dem Stueck -- die Vorschau im Reiter zeigt sie.
/// </summary>
internal sealed class HudStilDialog : Form
{
    private static readonly Color Grund = Color.FromArgb(24, 26, 31);
    private static readonly Color Leise = Color.FromArgb(150, 160, 172);

    private readonly OverlaySettings _settings;
    private readonly HudElement _element;
    private readonly Action _geaendert;
    private readonly HudStil _stil;

    public HudStilDialog(OverlaySettings settings, HudElement element, Action geaendert)
    {
        _settings = settings;
        _element = element;
        _geaendert = geaendert;
        _stil = HudStile.AusEinstellungen(settings, element, HudStile.WahlVon(settings, element).Name);
        Text = Loc.T("Edit style");
        BackColor = Grund;
        ForeColor = Color.WhiteSmoke;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(400, 620);
        var liste = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(12) };
        Controls.Add(liste);

        FlowLayoutPanel Zeile(string text)
        {
            var z = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
            z.Controls.Add(new Label { Text = text, Width = 150, Height = 24, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.Gainsboro });
            liste.Controls.Add(z);
            return z;
        }
        void Farbe(string text, Func<string> lesen, Action<string> setzen)
        {
            var z = Zeile(text);
            var c = SpeedoDesign.Farbe(lesen(), Color.White);
            var knopf = new Button { Width = 70, Height = 24, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(255, c) };
            var deck = new NumericUpDown { Minimum = 0, Maximum = 100, Value = (int)Math.Round(c.A / 2.55), Width = 56 };
            void Setzen()
            {
                setzen(OverlaySettings.ToHex(Color.FromArgb((int)Math.Round((double)deck.Value * 2.55), knopf.BackColor)));
                Anwenden();
            }
            knopf.Click += (_, _) =>
            {
                using var wahl = new ColorDialog { Color = knopf.BackColor, FullOpen = true };
                if (wahl.ShowDialog(this) != DialogResult.OK) { return; }
                knopf.BackColor = Color.FromArgb(255, wahl.Color);
                if (deck.Value == 0) { deck.Value = 100; }
                Setzen();
            };
            deck.ValueChanged += (_, _) => Setzen();
            z.Controls.Add(knopf);
            z.Controls.Add(deck);
            z.Controls.Add(new Label { Text = "%", AutoSize = true, ForeColor = Leise, Margin = new Padding(0, 6, 0, 0) });
        }
        void Wahl<T>(string text, T wert, Func<T, string> name, Action<T> setzen) where T : struct, Enum
        {
            var werte = Enum.GetValues<T>();
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
            foreach (var v in werte) { c.Items.Add(name(v)); }
            c.SelectedIndex = Math.Max(0, Array.IndexOf(werte, wert));
            c.SelectedIndexChanged += (_, _) => { setzen(werte[c.SelectedIndex]); Anwenden(); };
            Zeile(text).Controls.Add(c);
        }
        void Regler(string text, int wert, int bis, Action<int> setzen)
        {
            var r = new TrackBar { Minimum = 0, Maximum = bis, Value = Math.Clamp(wert, 0, bis), Width = 170, TickStyle = TickStyle.None, AutoSize = false, Height = 24 };
            r.ValueChanged += (_, _) => { setzen(r.Value); Anwenden(); };
            Zeile(text).Controls.Add(r);
        }

        liste.Controls.Add(new Label { Text = Loc.T("Colours"), AutoSize = true, Font = new Font("Segoe UI Semibold", 10f), Margin = new Padding(3, 4, 3, 4) });
        Farbe(Loc.T("Background"), () => _stil.Background, v => _stil.Background = v);
        Farbe(Loc.T("Text"), () => _stil.Text, v => _stil.Text = v);
        Farbe(Loc.T("Small text"), () => _stil.Dim, v => _stil.Dim = v);
        Farbe(Loc.T("Accent"), () => _stil.Accent, v => _stil.Accent = v);
        Farbe(Loc.T("Lines"), () => _stil.Line, v => _stil.Line = v);
        if (element is HudElement.Delta or HudElement.Ghost or HudElement.Panels or HudElement.Inputs)
        {
            Farbe(Loc.T("Ahead"), () => _stil.Good, v => _stil.Good = v);
            Farbe(Loc.T("Behind"), () => _stil.Bad, v => _stil.Bad = v);
        }
        liste.Controls.Add(new Label { Text = Loc.T("Frame and font"), AutoSize = true, Font = new Font("Segoe UI Semibold", 10f), Margin = new Padding(3, 12, 3, 4) });
        Wahl(Loc.T("Frame"), _stil.Frame, Rahmenname, v => _stil.Frame = v);
        Farbe(Loc.T("Frame colour"), () => _stil.FrameColour, v => _stil.FrameColour = v);
        Regler(Loc.T("Corner radius"), (int)Math.Max(0f, _stil.Corner), 40, v => _stil.Corner = v);
        Regler(Loc.T("Glow"), (int)(_stil.Glow * 100f), 100, v => _stil.Glow = v / 100f);
        Wahl(Loc.T("Font"), _stil.Font, Schriftname, v => _stil.Font = v);
        var kursiv = new CheckBox { Text = Loc.T("Italic"), Checked = _stil.Italic, AutoSize = true, ForeColor = Color.Gainsboro };
        kursiv.CheckedChanged += (_, _) => { _stil.Italic = kursiv.Checked; Anwenden(); };
        liste.Controls.Add(kursiv);
        var platte = new CheckBox { Text = Loc.T("Background plate (off: only the content over the game)"), Checked = _stil.ShowPlate, AutoSize = true, ForeColor = Color.Gainsboro };
        platte.CheckedChanged += (_, _) => { _stil.ShowPlate = platte.Checked; Anwenden(); };
        liste.Controls.Add(platte);

        var unten = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var fertig = new Button { Text = Loc.T("Done"), AutoSize = true };
        fertig.Click += (_, _) => Close();
        var speichern = new Button { Text = Loc.T("Save as my style ..."), AutoSize = true };
        speichern.Click += (_, _) =>
        {
            if (FrageName(this, _stil.BuiltIn || _stil.Name == "Default" ? Loc.T("My style") : _stil.Name) is not { } name) { return; }
            _stil.Name = name;
            _stil.BuiltIn = false;
            HudStile.Speichern(_stil);
            Anwenden();
        };
        unten.Controls.Add(fertig);
        unten.Controls.Add(speichern);
        Controls.Add(unten);
    }

    private void Anwenden()
    {
        HudStile.Anwenden(_settings, _element, _stil);
        _geaendert();
    }

    /// <summary>Einen Namen erfragen -- null, wenn abgebrochen.</summary>
    internal static string? FrageName(IWin32Window? besitzer, string vorschlag)
    {
        using var f = new Form
        {
            Text = Loc.T("Name of the style"), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            Size = new Size(340, 140), MinimizeBox = false, MaximizeBox = false,
        };
        var feld = new TextBox { Text = vorschlag, Dock = DockStyle.Top, Margin = new Padding(8) };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Dock = DockStyle.Bottom };
        f.Controls.Add(feld);
        f.Controls.Add(ok);
        f.AcceptButton = ok;
        return f.ShowDialog(besitzer) == DialogResult.OK && feld.Text.Trim().Length > 0 ? feld.Text.Trim()[..Math.Min(40, feld.Text.Trim().Length)] : null;
    }

    internal static string StilName(HudStil s) => !s.BuiltIn ? s.Name : s.Name switch
    {
        "Default" => Loc.T("Default"),
        "Neon" => Loc.T("Neon"),
        "Cyberpunk" => Loc.T("Cyberpunk"),
        "Glass" => Loc.T("Glass"),
        "Classic" => Loc.T("Classic"),
        "Minimal" => Loc.T("Minimal"),
        "Racing" => Loc.T("Racing"),
        "Synthwave" => Loc.T("Synthwave"),
        "No plate" => Loc.T("No plate"),
        _ => Farbthemen.Anzeige(s.Name),
    };

    private static string Rahmenname(SpeedoFrame v) => v switch
    {
        SpeedoFrame.None => Loc.T("None"),
        SpeedoFrame.Plate => Loc.T("Plate"),
        SpeedoFrame.Bezel => Loc.T("Chrome ring"),
        SpeedoFrame.Glass => Loc.T("Glass"),
        SpeedoFrame.Outline => Loc.T("Outline"),
        _ => Loc.T("Angled"),
    };

    private static string Schriftname(SpeedoFont v) => v switch
    {
        SpeedoFont.Sans => Loc.T("Plain"),
        SpeedoFont.Condensed => Loc.T("Condensed"),
        SpeedoFont.Mono => Loc.T("Monospace"),
        _ => Loc.T("Seven-segment"),
    };
}
