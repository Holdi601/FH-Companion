using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Der Reiter "G-meter" (seit 2026-10-24): Galerie, bewegte Vorschau, Baukasten -- wie beim Tacho.
/// Erst die Dimension (1, 2 oder 3), dann die Formen, die dazu gehoeren.
/// </summary>
internal sealed class GMeterTab : UserControl
{
    private static readonly Color Grund = Color.FromArgb(24, 26, 31);
    private static readonly Color Feld = Color.FromArgb(18, 20, 24);
    private static readonly Color Leise = Color.FromArgb(150, 160, 172);
    private static readonly Dictionary<string, Bitmap> Kacheln = new();

    private readonly OverlaySettings _settings;
    private readonly Action<GMeterDesign?> _geaendert;
    private readonly Action _zuruecksetzen;
    private readonly FlowLayoutPanel _galerie = new();
    private readonly Panel _vorschau;
    private readonly FlowLayoutPanel _baukasten = new();
    private readonly Label _titel = new();
    private readonly Label _status = new();
    private readonly CheckBox _an = new();
    private readonly Button _loeschen;
    private readonly System.Windows.Forms.Timer _takt = new() { Interval = 16 };
    private readonly System.Windows.Forms.Timer _speichern = new() { Interval = 700 };
    private readonly GMeterRenderer _maler = new();
    private readonly System.Diagnostics.Stopwatch _uhr = System.Diagnostics.Stopwatch.StartNew();
    private GMeterDesign _design;
    private string? _gespeichertAls;
    private bool _still;

    public GMeterTab(OverlaySettings settings, Action<GMeterDesign?> geaendert, Action zuruecksetzen)
    {
        _settings = settings;
        _geaendert = geaendert;
        _zuruecksetzen = zuruecksetzen;
        _design = GMeterDesigns.Finde(settings.GMeterDesign);
        Dock = DockStyle.Fill;
        BackColor = Grund;
        ForeColor = Color.WhiteSmoke;

        var oben = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 8, 8, 4), WrapContents = true };
        _an.Text = Loc.T("Show the G-meter while driving");
        _an.AutoSize = true;
        _an.Checked = settings.HudGMeter;
        _an.Margin = new Padding(3, 6, 18, 3);
        _an.CheckedChanged += (_, _) =>
        {
            if (_still) { return; }
            _settings.HudGMeter = _an.Checked;
            _settings.Save();
            _geaendert(null);
        };
        oben.Controls.Add(_an);
        var einheitFeld = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        einheitFeld.Controls.Add(new Label { Text = Loc.T("Unit"), AutoSize = true, ForeColor = Leise, Margin = new Padding(3, 8, 3, 3) });
        var einheit = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90, BackColor = Feld, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Margin = new Padding(3, 4, 14, 3) };
        einheit.Items.AddRange(new object[] { "G", "m/s²", "ft/s²" });
        einheit.SelectedIndex = (int)settings.GMeterUnit;
        einheit.SelectedIndexChanged += (_, _) =>
        {
            _settings.GMeterUnit = (GUnit)Math.Max(0, einheit.SelectedIndex);
            _settings.Save();
            _geaendert(null);
            _vorschau?.Invalidate();
        };
        einheitFeld.Controls.Add(einheit);
        oben.Controls.Add(einheitFeld);
        var zurueck = Knopf(Loc.T("Reset peaks"));
        zurueck.Click += (_, _) => { _zuruecksetzen(); Melde(Loc.T("Peaks, outline and heat map set to zero.")); };
        oben.Controls.Add(zurueck);
        oben.Controls.Add(new Label
        {
            Text = Loc.T("Position and size: drag it in the tab Lap delta HUD. It draws with the speedometer, on its own thread, only when something changes."),
            AutoSize = true, ForeColor = Leise, Margin = new Padding(12, 8, 3, 3),
        });

        _galerie.Dock = DockStyle.Left;
        _galerie.Width = 214;
        _galerie.AutoScroll = true;
        _galerie.FlowDirection = FlowDirection.TopDown;
        _galerie.WrapContents = false;
        _galerie.BackColor = Feld;
        _galerie.Padding = new Padding(6);

        var rechts = new Panel { Dock = DockStyle.Right, Width = 360, BackColor = Feld };
        var knoepfe = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6), WrapContents = true };
        var nutzen = Knopf(Loc.T("Use this design"));
        nutzen.Click += (_, _) => Nutzen(_design);
        var kopie = Knopf(Loc.T("Copy as my design"));
        kopie.Click += (_, _) => Kopieren();
        _loeschen = Knopf(Loc.T("Delete"));
        _loeschen.Click += (_, _) => Loeschen();
        var import = Knopf(Loc.T("Import ..."));
        import.Click += (_, _) => Importieren();
        var export = Knopf(Loc.T("Export ..."));
        export.Click += (_, _) => Exportieren();
        knoepfe.Controls.AddRange(new Control[] { nutzen, kopie, _loeschen, import, export });
        _baukasten.Dock = DockStyle.Fill;
        _baukasten.AutoScroll = true;
        _baukasten.FlowDirection = FlowDirection.TopDown;
        _baukasten.WrapContents = false;
        _baukasten.Padding = new Padding(8, 4, 8, 8);
        rechts.Controls.Add(_baukasten);
        rechts.Controls.Add(knoepfe);

        var mitte = new Panel { Dock = DockStyle.Fill, BackColor = Grund, Padding = new Padding(10) };
        _titel.Dock = DockStyle.Top;
        _titel.Height = 30;
        _titel.Font = new Font("Segoe UI Semibold", 13f);
        _status.Dock = DockStyle.Bottom;
        _status.Height = 44;
        _status.ForeColor = Leise;
        _vorschau = new Doppelpuffer { Dock = DockStyle.Fill, BackColor = Color.FromArgb(52, 58, 66) };
        _vorschau.Paint += (_, e) => VorschauMalen(e.Graphics);
        mitte.Controls.Add(_vorschau);
        mitte.Controls.Add(_status);
        mitte.Controls.Add(_titel);

        Controls.Add(mitte);
        Controls.Add(rechts);
        Controls.Add(_galerie);
        Controls.Add(oben);

        _takt.Tick += (_, _) => { if (SollBewegen()) { _vorschau.Invalidate(); } };
        _speichern.Tick += (_, _) => { _speichern.Stop(); Speichern(); };
        VisibleChanged += (_, _) =>
        {
            if (Visible)
            {
                _still = true;
                _an.Checked = _settings.HudGMeter;
                _still = false;
                _takt.Start();
            }
            else
            {
                _takt.Stop();
            }
        };
        GalerieAufbauen();
        Waehlen(_design);
    }

    private bool SollBewegen()
    {
        if (!Visible || !IsHandleCreated || FindForm() is not { } f || f.WindowState == FormWindowState.Minimized) { return false; }
        try { return !GameWatch.IstForza(GameWatch.ForegroundProcessName()); }
        catch (Exception) { return true; }
    }

    private void VorschauMalen(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var platz = RectangleF.Inflate(_vorschau.ClientRectangle, -24f, -24f);
        var groesse = GMeterRenderer.Groesse(_design, 1f);
        var k = Math.Min(Math.Min(platz.Width / groesse.Width, platz.Height / groesse.Height), 2.4f);
        var w = groesse.Width * k;
        var h = groesse.Height * k;
        var ziel = new RectangleF(platz.X + (platz.Width - w) / 2f, platz.Y + (platz.Height - h) / 2f, w, h);
        _maler.Male(g, _design, GMeterState.Vorfuehrung(_uhr.Elapsed.TotalSeconds), ziel, GMeterEinheit.Aus(_settings));
    }

    // ---- Galerie ----

    internal static string Anzeige(GMeterDesign d) => !d.BuiltIn ? d.Name : d.Thema is { } thema
        ? Farbthemen.Anzeige(thema) + " · " + Layoutname(d.Layout)
        : d.Name switch
    {
        "Cartesian axes" => Loc.T("Cartesian axes"),
        "Cartesian 3D" => Loc.T("Cartesian 3D"),
        "Classic circle" => Loc.T("Classic circle"),
        "Racing crosshair" => Loc.T("Racing crosshair"),
        "Neon radar" => Loc.T("Neon radar"),
        "Heat map" => Loc.T("Heat map"),
        "Friction diamond" => Loc.T("Friction diamond"),
        "Cyberpunk" => Loc.T("Cyberpunk"),
        "Minimal dot" => Loc.T("Minimal dot"),
        "Lateral bar" => Loc.T("Lateral bar"),
        "Brake and throttle" => Loc.T("Brake and throttle"),
        "Combined arc" => Loc.T("Combined arc"),
        "G needle" => Loc.T("G needle"),
        "Perspective disc" => Loc.T("Perspective disc"),
        "Sphere" => Loc.T("Sphere"),
        "Circle and height" => Loc.T("Circle and height"),
        _ => d.Name,
    };

    private void GalerieAufbauen()
    {
        _galerie.SuspendLayout();
        foreach (Control c in _galerie.Controls) { c.Dispose(); }
        _galerie.Controls.Clear();
        foreach (var (titel, liste) in new[]
                 {
                     (Loc.T("1D: one direction"), GMeterDesigns.Eingebaut.Where(d => d.Dimension == GMeterDimension.One).ToList()),
                     (Loc.T("2D: sideways and braking"), GMeterDesigns.Eingebaut.Where(d => d.Dimension == GMeterDimension.Two).ToList()),
                     (Loc.T("3D: with up and down"), GMeterDesigns.Eingebaut.Where(d => d.Dimension == GMeterDimension.Three).ToList()),
                     (Loc.T("My designs"), GMeterDesigns.Eigene()),
                 })
        {
            if (liste.Count == 0) { continue; }
            _galerie.Controls.Add(new Label { Text = titel, AutoSize = true, ForeColor = Leise, Margin = new Padding(4, 8, 3, 4) });
            foreach (var d in liste) { _galerie.Controls.Add(Kachel(d)); }
        }
        _galerie.ResumeLayout();
    }

    private Control Kachel(GMeterDesign d)
    {
        const int breite = 176;
        var hoehe = (int)Math.Clamp(breite * d.Hochkant, 50, 230);
        var schluessel = d.Name + "|" + d.ToJson().GetHashCode() + "|" + _settings.GMeterUnit;
        if (!Kacheln.TryGetValue(schluessel, out var bild))
        {
            bild = new Bitmap(breite, hoehe, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bild))
            {
                g.Clear(Color.FromArgb(52, 58, 66));
                GMeterRenderer.Einmal(g, d, GMeterState.Vorfuehrung(0, ruhig: true), new RectangleF(4, 4, breite - 8, hoehe - 8), GMeterEinheit.Aus(_settings));
            }
            if (Kacheln.Count > 80)
            {
                foreach (var alt in Kacheln.Values) { alt.Dispose(); }
                Kacheln.Clear();
            }
            Kacheln[schluessel] = bild;
        }
        var aktiv = string.Equals(d.Name, _settings.GMeterDesign, StringComparison.OrdinalIgnoreCase);
        var kachel = new Panel { Width = breite + 8, Height = hoehe + 30, Margin = new Padding(3), Cursor = Cursors.Hand, Tag = d.Name };
        kachel.BackColor = string.Equals(d.Name, _design.Name, StringComparison.OrdinalIgnoreCase) ? Color.FromArgb(48, 92, 140) : Feld;
        var box = new PictureBox { Image = bild, Width = breite, Height = hoehe, Location = new Point(4, 4) };
        var name = new Label
        {
            Text = Anzeige(d) + (aktiv ? "  ✓" : string.Empty), AutoSize = false, Width = breite, Height = 22,
            Location = new Point(4, hoehe + 6), ForeColor = aktiv ? Color.FromArgb(134, 239, 172) : Color.Gainsboro,
        };
        kachel.Controls.Add(box);
        kachel.Controls.Add(name);
        foreach (Control c in new Control[] { kachel, box, name })
        {
            c.Click += (_, _) => Waehlen(d);
            c.DoubleClick += (_, _) => Nutzen(d);
        }
        return kachel;
    }

    private void Markieren()
    {
        foreach (Control c in _galerie.Controls)
        {
            if (c is Panel p && p.Tag is string n)
            {
                p.BackColor = string.Equals(n, _design.Name, StringComparison.OrdinalIgnoreCase) ? Color.FromArgb(48, 92, 140) : Feld;
            }
        }
    }

    private void Waehlen(GMeterDesign d)
    {
        _speichern.Stop();
        if (!_design.BuiltIn && _gespeichertAls is not null) { Speichern(); }
        _design = d.BuiltIn ? d : d.Clone();
        _gespeichertAls = d.BuiltIn ? null : d.Name;
        _titel.Text = Anzeige(_design) + (_design.BuiltIn ? "  ·  " + Loc.T("built-in, changes make your own copy") : string.Empty);
        _loeschen.Enabled = !_design.BuiltIn;
        _maler.Vergessen();
        Markieren();
        BaukastenAufbauen();
        _vorschau.Invalidate();
    }

    private void Nutzen(GMeterDesign d)
    {
        _settings.GMeterDesign = d.Name;
        _settings.Save();
        GMeterDesigns.Vergessen();
        _geaendert(d.BuiltIn ? d : d.Clone());
        GalerieAufbauen();
        Waehlen(d);
        Melde(string.Format(Loc.T("\"{0}\" is the G-meter now."), Anzeige(d)));
    }

    private void Kopieren()
    {
        var kopie = _design.Clone();
        kopie.Name = GMeterDesigns.FreierName(_design.Name);
        GMeterDesigns.Speichern(kopie);
        GalerieAufbauen();
        Waehlen(kopie);
        Melde(string.Format(Loc.T("Copied as \"{0}\". Change it on the right; it saves itself."), kopie.Name));
    }

    private void Loeschen()
    {
        if (_design.BuiltIn) { return; }
        if (MessageBox.Show(this, string.Format(Loc.T("Delete \"{0}\"?"), _design.Name), Loc.T("Delete"), MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) { return; }
        try
        {
            GMeterDesigns.Loeschen(_design);
            if (string.Equals(_settings.GMeterDesign, _design.Name, StringComparison.OrdinalIgnoreCase))
            {
                _settings.GMeterDesign = GMeterDesigns.Eingebaut[0].Name;
                _settings.Save();
                _geaendert(null);
            }
            _gespeichertAls = null;
            GalerieAufbauen();
            Waehlen(GMeterDesigns.Finde(_settings.GMeterDesign));
        }
        catch (Exception e) { Melde(e.Message); }
    }

    private void Importieren()
    {
        using var wahl = new OpenFileDialog { Filter = Loc.T("G-meter design") + " (*.json)|*.json", Title = Loc.T("Import ...") };
        if (wahl.ShowDialog(this) != DialogResult.OK) { return; }
        try
        {
            var text = File.ReadAllText(wahl.FileName);
            if (text.Length > 64_000 || GMeterDesign.FromJson(text) is not { } d) { Melde(Loc.T("This file is not a G-meter design.")); return; }
            if (GMeterDesigns.Alle().Any(x => string.Equals(x.Name, d.Name, StringComparison.OrdinalIgnoreCase))) { d.Name = GMeterDesigns.FreierName(d.Name); }
            GMeterDesigns.Speichern(d);
            GalerieAufbauen();
            Waehlen(d);
            Melde(string.Format(Loc.T("Imported \"{0}\"."), d.Name));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Melde(e.Message); }
    }

    private void Exportieren()
    {
        using var wahl = new SaveFileDialog { Filter = Loc.T("G-meter design") + " (*.json)|*.json", FileName = Path.GetFileName(GMeterDesigns.DateiVon(_design.Name)), Title = Loc.T("Export ...") };
        if (wahl.ShowDialog(this) != DialogResult.OK) { return; }
        try
        {
            File.WriteAllText(wahl.FileName, _design.ToJson());
            Melde(string.Format(Loc.T("Saved to {0}"), wahl.FileName));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Melde(e.Message); }
    }

    private void Melde(string text) => _status.Text = text;

    // ---- Baukasten ----

    private void Aendern(Action<GMeterDesign> aenderung, bool neuAufbauen = false)
    {
        if (_still) { return; }
        if (_design.BuiltIn)
        {
            var kopie = _design.Clone();
            kopie.Name = GMeterDesigns.FreierName(_design.Name);
            var warAktiv = string.Equals(_settings.GMeterDesign, _design.Name, StringComparison.OrdinalIgnoreCase);
            _design = kopie;
            _gespeichertAls = null;
            _loeschen.Enabled = true;
            _titel.Text = kopie.Name;
            aenderung(_design);
            _design.Bereinigt();
            Speichern();
            if (warAktiv) { _settings.GMeterDesign = kopie.Name; _settings.Save(); }
            GalerieAufbauen();
            Markieren();
            Melde(string.Format(Loc.T("Built-in styles stay as they are -- your change went into \"{0}\"."), kopie.Name));
            neuAufbauen = true;
        }
        else
        {
            aenderung(_design);
            _design.Bereinigt();
            _speichern.Stop();
            _speichern.Start();
        }
        _maler.Vergessen();
        if (string.Equals(_settings.GMeterDesign, _design.Name, StringComparison.OrdinalIgnoreCase)) { _geaendert(_design.Clone()); }
        if (neuAufbauen) { BeginInvoke(new Action(BaukastenAufbauen)); }
        _vorschau.Invalidate();
    }

    private void Speichern()
    {
        if (_design.BuiltIn) { return; }
        try
        {
            if (_gespeichertAls is not null && !string.Equals(_gespeichertAls, _design.Name, StringComparison.OrdinalIgnoreCase))
            {
                if (GMeterDesigns.Alle().Any(x => string.Equals(x.Name, _design.Name, StringComparison.OrdinalIgnoreCase))) { _design.Name = GMeterDesigns.FreierName(_design.Name); }
                var alt = GMeterDesigns.DateiVon(_gespeichertAls);
                GMeterDesigns.Speichern(_design);
                if (!string.Equals(alt, GMeterDesigns.DateiVon(_design.Name), StringComparison.OrdinalIgnoreCase) && File.Exists(alt)) { File.Delete(alt); }
                if (string.Equals(_settings.GMeterDesign, _gespeichertAls, StringComparison.OrdinalIgnoreCase)) { _settings.GMeterDesign = _design.Name; _settings.Save(); }
                _gespeichertAls = _design.Name;
                GalerieAufbauen();
                return;
            }
            GMeterDesigns.Speichern(_design);
            _gespeichertAls = _design.Name;
            GMeterDesigns.Vergessen();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Melde(e.Message); }
    }

    private void BaukastenAufbauen()
    {
        _still = true;
        _baukasten.SuspendLayout();
        foreach (Control c in _baukasten.Controls) { c.Dispose(); }
        _baukasten.Controls.Clear();
        var d = _design;

        Gruppe(Loc.T("General"));
        Text(Loc.T("Name"), d.Name, v => Aendern(x => x.Name = v));
        Thema(t => Aendern(x => Farbthemen.Auf(x, t), neuAufbauen: true));
        // ERST DIE DIMENSION, DANN DIE FORMEN DAZU: wechselt die Dimension, nimmt sie ihre erste Form.
        Wahl(Loc.T("Dimensions"), d.Dimension, v => Aendern(x =>
        {
            if (x.Dimension != v) { x.Layout = GMeterDesign.LayoutsVon(v).First(); }
        }, neuAufbauen: true), Dimensionsname, Enum.GetValues<GMeterDimension>());
        Wahl(Loc.T("Style"), d.Layout, v => Aendern(x => x.Layout = v, neuAufbauen: true), Layoutname, GMeterDesign.LayoutsVon(d.Dimension).ToArray());
        if (d.Dimension == GMeterDimension.One)
        {
            Wahl(Loc.T("Direction"), d.Axis, v => Aendern(x => x.Axis = v), Achsenname, Enum.GetValues<GMeterAxis>());
        }
        Zahl(Loc.T("Size (pixels at 1080p)"), d.Size, 80, 800, v => Aendern(x => x.Size = v));

        Gruppe(Loc.T("Scale"));
        Regler(Loc.T("Full scale (G x 10)"), d.MaxG * 10f, 5f, 60f, v => Aendern(x => x.MaxG = v / 10f));
        Zahl(Loc.T("Rings or marks"), d.Rings, 1, 10, v => Aendern(x => x.Rings = v));
        Schalter(Loc.T("Scale numbers"), d.ScaleNumbers, v => Aendern(x => x.ScaleNumbers = v));
        Schalter(Loc.T("Direction labels"), d.DirectionLabels, v => Aendern(x => x.DirectionLabels = v));
        Schalter(Loc.T("Show the felt force (outward, forward on braking)"), d.FeltForce, v => Aendern(x => x.FeltForce = v));

        Gruppe(Loc.T("What it shows"));
        Wahl(Loc.T("Trail"), d.Trail, v => Aendern(x => x.Trail = v), Spurname, Enum.GetValues<GMeterTrail>());
        Regler(Loc.T("Trail (0.1 s)"), d.TrailSeconds * 10f, 0f, 100f, v => Aendern(x => x.TrailSeconds = v / 10f));
        Schalter(Loc.T("Outline of the highest reached"), d.Envelope, v => Aendern(x => x.Envelope = v));
        Schalter(Loc.T("Peak marks"), d.Peaks, v => Aendern(x => x.Peaks = v));
        Schalter(Loc.T("Numbers"), d.Numbers, v => Aendern(x => x.Numbers = v));
        Schalter(Loc.T("Colour by strength"), d.ColourByG, v => Aendern(x => x.ColourByG = v));
        Regler(Loc.T("Dot size"), d.DotSize * 100f, 30f, 300f, v => Aendern(x => x.DotSize = v / 100f));

        Gruppe(Loc.T("Colours"));
        Farbe(Loc.T("Background"), d.Background, v => Aendern(x => x.Background = v));
        Farbe(Loc.T("Frame"), d.FrameColour, v => Aendern(x => x.FrameColour = v));
        Farbe(Loc.T("Grid"), d.GridColour, v => Aendern(x => x.GridColour = v));
        Farbe(Loc.T("Dot"), d.DotColour, v => Aendern(x => x.DotColour = v));
        Farbe(Loc.T("Low"), d.LowColour, v => Aendern(x => x.LowColour = v));
        Farbe(Loc.T("High"), d.HighColour, v => Aendern(x => x.HighColour = v));
        Farbe(Loc.T("Trail"), d.TrailColour, v => Aendern(x => x.TrailColour = v));
        Farbe(Loc.T("Outline and peaks"), d.EnvelopeColour, v => Aendern(x => x.EnvelopeColour = v));
        Farbe(Loc.T("Numbers"), d.TextColour, v => Aendern(x => x.TextColour = v));
        Farbe(Loc.T("Small text"), d.DimColour, v => Aendern(x => x.DimColour = v));

        Gruppe(Loc.T("Light, frame and font"));
        Regler(Loc.T("Glow"), d.Glow * 100f, 0f, 100f, v => Aendern(x => x.Glow = v / 100f));
        Regler(Loc.T("Smoothing"), d.Smoothing * 100f, 0f, 95f, v => Aendern(x => x.Smoothing = v / 100f));
        Wahl(Loc.T("Frame"), d.Frame, v => Aendern(x => x.Frame = v), Rahmenname, Enum.GetValues<SpeedoFrame>());
        Schalter(Loc.T("Background plate"), d.ShowPlate, v => Aendern(x => x.ShowPlate = v));
        Regler(Loc.T("Corner radius"), d.CornerRadius, 0f, 60f, v => Aendern(x => x.CornerRadius = v));
        Wahl(Loc.T("Font"), d.Font, v => Aendern(x => x.Font = v), Schriftname, Enum.GetValues<SpeedoFont>());
        Schalter(Loc.T("Italic"), d.Italic, v => Aendern(x => x.Italic = v));

        _baukasten.Controls.Add(new Label
        {
            Text = Loc.T("Sideways and lengthways are the forces in the plane of the car; up and down are bumps, crests and compressions (the game leaves gravity out). Above 8 G is a crash, not driving, and is ignored."),
            AutoSize = true, MaximumSize = new Size(320, 0), ForeColor = Leise, Margin = new Padding(3, 14, 3, 10),
        });
        _baukasten.ResumeLayout();
        _still = false;
    }

    private void Thema(Action<Farbthema> anwenden)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180, BackColor = Grund, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, DropDownWidth = 240 };
        c.Items.Add(Loc.T("-- pick a theme --"));
        foreach (var t in Farbthemen.Alle) { c.Items.Add(Farbthemen.Anzeige(t.Name)); }
        c.SelectedIndex = 0;
        c.SelectedIndexChanged += (_, _) => { if (c.SelectedIndex > 0) { anwenden(Farbthemen.Alle[c.SelectedIndex - 1]); } };
        Zeile(Loc.T("Colour theme")).Controls.Add(c);
    }

    private void Gruppe(string titel) =>
        _baukasten.Controls.Add(new Label { Text = titel, AutoSize = true, Font = new Font("Segoe UI Semibold", 10f), ForeColor = Color.White, Margin = new Padding(3, 12, 3, 4) });

    private FlowLayoutPanel Zeile(string text)
    {
        var z = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 1, 0, 1) };
        z.Controls.Add(new Label { Text = text, AutoSize = false, Width = 150, Height = 24, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.Gainsboro });
        _baukasten.Controls.Add(z);
        return z;
    }

    private void Text(string text, string wert, Action<string> setzen)
    {
        var t = new TextBox { Text = wert, Width = 160, BackColor = Grund, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
        t.TextChanged += (_, _) => { if (t.Text.Trim().Length > 0) { setzen(t.Text); } };
        Zeile(text).Controls.Add(t);
    }

    private void Schalter(string text, bool wert, Action<bool> setzen)
    {
        var c = new CheckBox { Text = text, Checked = wert, AutoSize = true, ForeColor = Color.Gainsboro, Margin = new Padding(3) };
        c.CheckedChanged += (_, _) => setzen(c.Checked);
        _baukasten.Controls.Add(c);
    }

    private void Zahl(string text, int wert, int min, int max, Action<int> setzen)
    {
        var n = new NumericUpDown { Minimum = min, Maximum = max, Value = Math.Clamp(wert, min, max), Width = 80, BackColor = Grund, ForeColor = Color.White };
        n.ValueChanged += (_, _) => setzen((int)n.Value);
        Zeile(text).Controls.Add(n);
    }

    private void Regler(string text, float wert, float min, float max, Action<float> setzen)
    {
        var z = Zeile(text);
        var r = new TrackBar { Minimum = (int)min, Maximum = (int)max, Value = (int)Math.Clamp(Math.Round(wert), min, max), Width = 120, TickStyle = TickStyle.None, AutoSize = false, Height = 24 };
        var zahl = new Label { Text = r.Value.ToString(System.Globalization.CultureInfo.CurrentCulture), AutoSize = false, Width = 36, Height = 24, TextAlign = ContentAlignment.MiddleRight, ForeColor = Leise };
        r.ValueChanged += (_, _) => { zahl.Text = r.Value.ToString(System.Globalization.CultureInfo.CurrentCulture); setzen(r.Value); };
        z.Controls.Add(r);
        z.Controls.Add(zahl);
    }

    private void Wahl<T>(string text, T wert, Action<T> setzen, Func<T, string> name, T[] werte) where T : struct, Enum
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180, BackColor = Grund, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, DropDownWidth = 260 };
        foreach (var v in werte) { c.Items.Add(name(v)); }
        c.SelectedIndex = Math.Max(0, Array.IndexOf(werte, wert));
        c.SelectedIndexChanged += (_, _) => { if (c.SelectedIndex >= 0) { setzen(werte[c.SelectedIndex]); } };
        Zeile(text).Controls.Add(c);
    }

    private void Farbe(string text, string wert, Action<string> setzen)
    {
        var z = Zeile(text);
        var farbe = SpeedoDesign.Farbe(wert, Color.White);
        var knopf = new Button { Width = 70, Height = 24, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(255, farbe) };
        var deck = new NumericUpDown { Minimum = 0, Maximum = 100, Value = (int)Math.Round(farbe.A / 2.55), Width = 56, BackColor = Grund, ForeColor = Color.White };
        void Setzen() => setzen(OverlaySettings.ToHex(Color.FromArgb((int)Math.Round((double)deck.Value * 2.55), knopf.BackColor)));
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

    private static Button Knopf(string text) => new()
    {
        Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Feld, ForeColor = Color.WhiteSmoke, Margin = new Padding(3),
    };

    private static string Dimensionsname(GMeterDimension v) => v switch
    {
        GMeterDimension.One => Loc.T("1D: one direction"),
        GMeterDimension.Two => Loc.T("2D: sideways and braking"),
        _ => Loc.T("3D: with up and down"),
    };

    private static string Layoutname(GMeterLayout v) => v switch
    {
        GMeterLayout.Bar => Loc.T("Horizontal bar"),
        GMeterLayout.Column => Loc.T("Vertical column"),
        GMeterLayout.Arc => Loc.T("Arc"),
        GMeterLayout.Needle => Loc.T("Needle dial"),
        GMeterLayout.Circle => Loc.T("Circle"),
        GMeterLayout.Crosshair => Loc.T("Crosshair"),
        GMeterLayout.Radar => Loc.T("Radar"),
        GMeterLayout.Diamond => Loc.T("Diamond"),
        GMeterLayout.Heat => Loc.T("Heat map"),
        GMeterLayout.Perspective => Loc.T("Perspective disc"),
        GMeterLayout.Sphere => Loc.T("Sphere"),
        GMeterLayout.Cartesian => Loc.T("Cartesian axes"),
        GMeterLayout.Cartesian3D => Loc.T("Cartesian 3D"),
        _ => Loc.T("Circle and height"),
    };

    private static string Achsenname(GMeterAxis v) => v switch
    {
        GMeterAxis.Lateral => Loc.T("Sideways"),
        GMeterAxis.Longitudinal => Loc.T("Braking and accelerating"),
        GMeterAxis.Vertical => Loc.T("Up and down"),
        _ => Loc.T("Total"),
    };

    private static string Spurname(GMeterTrail v) => v switch
    {
        GMeterTrail.Off => Loc.T("Off"),
        GMeterTrail.Dots => Loc.T("Dots"),
        GMeterTrail.Line => Loc.T("Line"),
        _ => Loc.T("Glowing line"),
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _takt.Dispose();
            if (_speichern.Enabled) { _speichern.Stop(); Speichern(); }
            _speichern.Dispose();
            _maler.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class Doppelpuffer : Panel
    {
        public Doppelpuffer()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }
    }
}
