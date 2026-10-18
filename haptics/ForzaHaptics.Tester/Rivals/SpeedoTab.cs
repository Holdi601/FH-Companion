using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Der Reiter "Speedometer" (seit 2026-10-18): die Galerie der Tachos, eine bewegte Vorschau und
/// der Baukasten fuer eigene.
/// </summary>
/// <remarks>
/// ## Eingebaut und eigen
///
/// Die eingebauten Designs bleiben, wie sie sind. Wer eines aendert, aendert eine Kopie: beim
/// ersten Regler entsteht "&lt;Name&gt; (mine)" und wird gewaehlt -- niemand muss vorher
/// "kopieren" druecken. Eigene Designs liegen als JSON unter DataFolder/speedo und lassen sich
/// weitergeben (Export/Import).
///
/// ## Die Vorschau bewegt sich nur, wenn man sie sieht
///
/// 30 Bilder je Sekunde, nur solange dieser Reiter sichtbar ist, das Fenster nicht verkleinert
/// und das Spiel nicht vorn ist. Gezeichnet mit derselben Zeichnung wie im Spiel.
/// </remarks>
internal sealed class SpeedoTab : UserControl
{
    private static readonly Color Grund = Color.FromArgb(24, 26, 31);
    private static readonly Color Feld = Color.FromArgb(18, 20, 24);
    private static readonly Color Leise = Color.FromArgb(150, 160, 172);

    private readonly OverlaySettings _settings;
    private readonly Action<SpeedoDesign?> _designGeaendert;
    private readonly Action _tripZurueck;
    private readonly FlowLayoutPanel _galerie = new();
    private readonly Panel _vorschau;
    private readonly FlowLayoutPanel _baukasten = new();
    private readonly Label _titel = new();
    private readonly Label _status = new();
    private readonly CheckBox _an = new();
    private readonly System.Windows.Forms.Timer _takt = new() { Interval = 33 };
    private readonly System.Windows.Forms.Timer _speichern = new() { Interval = 700 };
    private readonly SpeedoRenderer _maler = new();
    private readonly System.Diagnostics.Stopwatch _uhr = System.Diagnostics.Stopwatch.StartNew();
    private readonly Button _loeschen;
    private SpeedoDesign _design;
    /// <summary>Unter welchem Namen das bearbeitete Design zuletzt gespeichert wurde (fuer Umbenennen).</summary>
    private string? _gespeichertAls;
    private bool _still;

    public SpeedoTab(OverlaySettings settings, Action<SpeedoDesign?> designGeaendert, Action tripZurueck)
    {
        _settings = settings;
        _designGeaendert = designGeaendert;
        _tripZurueck = tripZurueck;
        _design = SpeedoDesigns.Finde(settings.SpeedoDesign);
        Dock = DockStyle.Fill;
        BackColor = Grund;
        ForeColor = Color.WhiteSmoke;

        // ---- OBEN: an/aus, Einheiten, Takt ----
        var oben = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 8, 8, 4), WrapContents = true };
        _an.Text = Loc.T("Show the speedometer while driving");
        _an.AutoSize = true;
        _an.Checked = settings.HudSpeedo;
        _an.Margin = new Padding(3, 6, 18, 3);
        _an.CheckedChanged += (_, _) =>
        {
            if (_still) { return; }
            _settings.HudSpeedo = _an.Checked;
            _settings.Save();
            _designGeaendert(null);
        };
        oben.Controls.Add(_an);
        oben.Controls.Add(Auswahl(Loc.T("Speed"), new[] { ("kmh", "km/h"), ("mph", "mph") }, settings.SpeedoMph ? "mph" : "kmh",
            v => _settings.SpeedoMph = v == "mph"));
        oben.Controls.Add(Auswahl(Loc.T("Power"), new[] { ("hp", "hp"), ("kW", "kW"), ("PS", "PS") }, settings.SpeedoPowerUnit,
            v => _settings.SpeedoPowerUnit = v));
        oben.Controls.Add(Auswahl(Loc.T("Frames per second"), new[] { ("15", "15"), ("30", "30"), ("60", "60") },
            settings.SpeedoFps.ToString(System.Globalization.CultureInfo.InvariantCulture),
            v => _settings.SpeedoFps = int.Parse(v, System.Globalization.CultureInfo.InvariantCulture)));
        var trip = Knopf(Loc.T("Reset trip"));
        trip.Click += (_, _) => { _tripZurueck(); Melde(Loc.T("Trip distance set to zero.")); };
        oben.Controls.Add(trip);
        var hinweis = new Label
        {
            Text = Loc.T("Position and size: drag it in the tab Lap delta HUD. Fewer frames per second cost the game less."),
            AutoSize = true, ForeColor = Leise, Margin = new Padding(12, 8, 3, 3),
        };
        oben.Controls.Add(hinweis);

        // ---- LINKS: die Galerie ----
        _galerie.Dock = DockStyle.Left;
        _galerie.Width = 214;
        _galerie.AutoScroll = true;
        _galerie.FlowDirection = FlowDirection.TopDown;
        _galerie.WrapContents = false;
        _galerie.BackColor = Feld;
        _galerie.Padding = new Padding(6);

        // ---- RECHTS: der Baukasten ----
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

        // ---- MITTE: die Vorschau ----
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

        _takt.Tick += (_, _) =>
        {
            if (!SollBewegen()) { return; }
            _vorschau.Invalidate();
        };
        _speichern.Tick += (_, _) => { _speichern.Stop(); Speichern(); };
        VisibleChanged += (_, _) =>
        {
            if (Visible)
            {
                _still = true;
                _an.Checked = _settings.HudSpeedo;
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

    /// <summary>Nur bewegen, wenn es jemand sieht -- und nie, waehrend das Spiel vorn ist.</summary>
    private bool SollBewegen()
    {
        if (!Visible || !IsHandleCreated || FindForm() is not { } f || f.WindowState == FormWindowState.Minimized) { return false; }
        try
        {
            return GameWatch.ForegroundProcessName() != GameWatch.DefaultProcessName;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private void VorschauMalen(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var flaeche = _vorschau.ClientRectangle;
        var platz = RectangleF.Inflate(flaeche, -24f, -24f);
        var groesse = SpeedoRenderer.Groesse(_design, 1f);
        var k = Math.Min(platz.Width / groesse.Width, platz.Height / groesse.Height);
        k = Math.Min(k, 2.2f);
        var w = groesse.Width * k;
        var h = groesse.Height * k;
        var ziel = new RectangleF(platz.X + (platz.Width - w) / 2f, platz.Y + (platz.Height - h) / 2f, w, h);
        _maler.Male(g, _design, SpeedoState.Vorfuehrung(_uhr.Elapsed.TotalSeconds), ziel, SpeedoEinheiten.Aus(_settings));
    }

    // ------------------------------------------------------------------ //
    // Galerie
    // ------------------------------------------------------------------ //

    /// <summary>Der Name, wie er dasteht -- die eingebauten uebersetzt.</summary>
    internal static string Anzeige(SpeedoDesign d) => !d.BuiltIn ? d.Name : d.Name switch
    {
        "Classic analogue" => Loc.T("Classic analogue"),
        "Horizon style" => Loc.T("Horizon style"),
        "Neon glow" => Loc.T("Neon glow"),
        "Futuristic" => Loc.T("Futuristic"),
        "Cyberpunk" => Loc.T("Cyberpunk"),
        "Motorcycle" => Loc.T("Motorcycle"),
        "Race dash" => Loc.T("Race dash"),
        "Retro LCD" => Loc.T("Retro LCD"),
        "Minimal" => Loc.T("Minimal"),
        "Vertical column" => Loc.T("Vertical column"),
        "Twin gauges" => Loc.T("Twin gauges"),
        "Sport sweep" => Loc.T("Sport sweep"),
        "Rally" => Loc.T("Rally"),
        "Synthwave" => Loc.T("Synthwave"),
        "Track day" => Loc.T("Track day"),
        _ => d.Name,
    };

    private void GalerieAufbauen()
    {
        _galerie.SuspendLayout();
        foreach (Control c in _galerie.Controls) { c.Dispose(); }
        _galerie.Controls.Clear();
        var eigene = SpeedoDesigns.Eigene();
        foreach (var (titel, liste) in new[] { (Loc.T("Styles"), SpeedoDesigns.Eingebaut.ToList()), (Loc.T("My designs"), eigene) })
        {
            if (liste.Count == 0) { continue; }
            _galerie.Controls.Add(new Label { Text = titel, AutoSize = true, ForeColor = Leise, Margin = new Padding(4, 8, 3, 4) });
            foreach (var d in liste) { _galerie.Controls.Add(Kachel(d)); }
        }
        _galerie.ResumeLayout();
    }

    private Control Kachel(SpeedoDesign d)
    {
        var breite = 176;
        var bildHoehe = (int)Math.Clamp(breite * d.Hochkant, 60, 230);
        var bild = new Bitmap(breite, bildHoehe, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bild))
        {
            g.Clear(Color.FromArgb(52, 58, 66));
            SpeedoRenderer.Einmal(g, d, SpeedoState.Vorfuehrung(0, ruhig: true), new RectangleF(4, 4, breite - 8, bildHoehe - 8), SpeedoEinheiten.Aus(_settings));
        }
        var aktiv = string.Equals(d.Name, _settings.SpeedoDesign, StringComparison.OrdinalIgnoreCase);
        var kachel = new Panel { Width = breite + 8, Height = bildHoehe + 30, Margin = new Padding(3), Cursor = Cursors.Hand, Tag = d.Name };
        kachel.BackColor = ReferenceEquals(d, _design) || string.Equals(d.Name, _design.Name, StringComparison.OrdinalIgnoreCase)
            ? Color.FromArgb(48, 92, 140) : Feld;
        var box = new PictureBox { Image = bild, Width = breite, Height = bildHoehe, Location = new Point(4, 4), SizeMode = PictureBoxSizeMode.Normal };
        var name = new Label
        {
            Text = Anzeige(d) + (aktiv ? "  ✓" : string.Empty), AutoSize = false, Width = breite, Height = 22,
            Location = new Point(4, bildHoehe + 6), ForeColor = aktiv ? Color.FromArgb(134, 239, 172) : Color.Gainsboro,
        };
        kachel.Controls.Add(box);
        kachel.Controls.Add(name);
        void Klick(object? _, EventArgs __) => Waehlen(d);
        void Doppel(object? _, EventArgs __) => Nutzen(d);
        foreach (Control c in new Control[] { kachel, box, name })
        {
            c.Click += Klick;
            c.DoubleClick += Doppel;
        }
        kachel.Disposed += (_, _) => bild.Dispose();
        return kachel;
    }

    private void Markieren()
    {
        foreach (Control c in _galerie.Controls)
        {
            if (c is Panel p && p.Tag is string name)
            {
                p.BackColor = string.Equals(name, _design.Name, StringComparison.OrdinalIgnoreCase) ? Color.FromArgb(48, 92, 140) : Feld;
            }
        }
    }

    private void Waehlen(SpeedoDesign d)
    {
        _speichern.Stop();
        if (!_design.BuiltIn && _gespeichertAls is not null) { Speichern(); }
        _design = d.BuiltIn ? d : d.Clone();
        _gespeichertAls = d.BuiltIn ? null : d.Name;
        _titel.Text = Anzeige(_design) + (_design.BuiltIn ? "  ·  " + Loc.T("built-in, changes make your own copy") : string.Empty);
        _loeschen.Enabled = !_design.BuiltIn;
        Markieren();
        BaukastenAufbauen();
        _vorschau.Invalidate();
    }

    private void Nutzen(SpeedoDesign d)
    {
        _settings.SpeedoDesign = d.Name;
        _settings.Save();
        SpeedoDesigns.Vergessen();
        _designGeaendert(d.BuiltIn ? d : d.Clone());
        GalerieAufbauen();
        Waehlen(d);
        Melde(string.Format(Loc.T("\"{0}\" is the speedometer now."), Anzeige(d)));
    }

    private void Kopieren()
    {
        var kopie = _design.Clone();
        kopie.Name = SpeedoDesigns.FreierName(_design.Name);
        SpeedoDesigns.Speichern(kopie);
        GalerieAufbauen();
        Waehlen(kopie);
        Melde(string.Format(Loc.T("Copied as \"{0}\". Change it on the right; it saves itself."), kopie.Name));
    }

    private void Loeschen()
    {
        if (_design.BuiltIn) { return; }
        if (MessageBox.Show(this, string.Format(Loc.T("Delete \"{0}\"?"), _design.Name), Loc.T("Delete"),
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) { return; }
        try
        {
            SpeedoDesigns.Loeschen(_design);
            var warAktiv = string.Equals(_settings.SpeedoDesign, _design.Name, StringComparison.OrdinalIgnoreCase);
            _gespeichertAls = null;
            if (warAktiv)
            {
                _settings.SpeedoDesign = SpeedoDesigns.Eingebaut[1].Name;
                _settings.Save();
                _designGeaendert(null);
            }
            GalerieAufbauen();
            Waehlen(SpeedoDesigns.Finde(_settings.SpeedoDesign));
        }
        catch (Exception e)
        {
            Melde(e.Message);
        }
    }

    private void Importieren()
    {
        using var wahl = new OpenFileDialog { Filter = Loc.T("Speedometer design") + " (*.json)|*.json", Title = Loc.T("Import ...") };
        if (wahl.ShowDialog(this) != DialogResult.OK) { return; }
        try
        {
            var text = File.ReadAllText(wahl.FileName);
            if (text.Length > 64_000 || SpeedoDesign.FromJson(text) is not { } d)
            {
                Melde(Loc.T("This file is not a speedometer design."));
                return;
            }
            if (SpeedoDesigns.Alle().Any(x => string.Equals(x.Name, d.Name, StringComparison.OrdinalIgnoreCase)))
            {
                d.Name = SpeedoDesigns.FreierName(d.Name);
            }
            SpeedoDesigns.Speichern(d);
            GalerieAufbauen();
            Waehlen(d);
            Melde(string.Format(Loc.T("Imported \"{0}\"."), d.Name));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Melde(e.Message);
        }
    }

    private void Exportieren()
    {
        using var wahl = new SaveFileDialog
        {
            Filter = Loc.T("Speedometer design") + " (*.json)|*.json",
            FileName = Path.GetFileName(SpeedoDesigns.DateiVon(_design.Name)),
            Title = Loc.T("Export ..."),
        };
        if (wahl.ShowDialog(this) != DialogResult.OK) { return; }
        try
        {
            File.WriteAllText(wahl.FileName, _design.ToJson());
            Melde(string.Format(Loc.T("Saved to {0}"), wahl.FileName));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Melde(e.Message);
        }
    }

    private void Melde(string text) => _status.Text = text;

    // ------------------------------------------------------------------ //
    // Baukasten
    // ------------------------------------------------------------------ //

    /// <summary>Eine Aenderung: an einer eigenen Kopie, gespeichert kurz danach, im Spiel sofort.</summary>
    private void Aendern(Action<SpeedoDesign> aenderung)
    {
        if (_still) { return; }
        if (_design.BuiltIn)
        {
            var kopie = _design.Clone();
            kopie.Name = SpeedoDesigns.FreierName(_design.Name);
            var warAktiv = string.Equals(_settings.SpeedoDesign, _design.Name, StringComparison.OrdinalIgnoreCase);
            _design = kopie;
            _gespeichertAls = null;
            _loeschen.Enabled = true;
            _titel.Text = kopie.Name;
            aenderung(_design);
            _design.Bereinigt();
            Speichern();
            if (warAktiv) { _settings.SpeedoDesign = kopie.Name; _settings.Save(); }
            GalerieAufbauen();
            Markieren();
            Melde(string.Format(Loc.T("Built-in styles stay as they are -- your change went into \"{0}\"."), kopie.Name));
            // Der Name im Baukasten zeigt jetzt die Kopie.
            BeginInvoke(new Action(BaukastenAufbauen));
        }
        else
        {
            aenderung(_design);
            _design.Bereinigt();
            _speichern.Stop();
            _speichern.Start();
        }
        if (string.Equals(_settings.SpeedoDesign, _design.Name, StringComparison.OrdinalIgnoreCase)) { _designGeaendert(_design.Clone()); }
        _vorschau.Invalidate();
    }

    private void Speichern()
    {
        if (_design.BuiltIn) { return; }
        try
        {
            // UMBENANNT: unter dem neuen Namen ablegen, die alte Datei weg, die Wahl mitnehmen.
            if (_gespeichertAls is not null && !string.Equals(_gespeichertAls, _design.Name, StringComparison.OrdinalIgnoreCase))
            {
                if (SpeedoDesigns.Alle().Any(x => string.Equals(x.Name, _design.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    _design.Name = SpeedoDesigns.FreierName(_design.Name);
                }
                var alt = SpeedoDesigns.DateiVon(_gespeichertAls);
                SpeedoDesigns.Speichern(_design);
                if (!string.Equals(alt, SpeedoDesigns.DateiVon(_design.Name), StringComparison.OrdinalIgnoreCase) && File.Exists(alt)) { File.Delete(alt); }
                if (string.Equals(_settings.SpeedoDesign, _gespeichertAls, StringComparison.OrdinalIgnoreCase))
                {
                    _settings.SpeedoDesign = _design.Name;
                    _settings.Save();
                }
                _gespeichertAls = _design.Name;
                GalerieAufbauen();
                return;
            }
            SpeedoDesigns.Speichern(_design);
            _gespeichertAls = _design.Name;
            SpeedoDesigns.Vergessen();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Melde(e.Message);
        }
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
        Wahl(Loc.T("Layout"), d.Layout, v => Aendern(x => x.Layout = v), Layoutname);
        Zahl(Loc.T("Size (pixels at 1080p)"), d.Size, 120, 900, v => Aendern(x => x.Size = v));

        Gruppe(Loc.T("Gauge"));
        Wahl(Loc.T("Rev fill"), d.Fill, v => Aendern(x => x.Fill = v), Fuellname);
        Zahl(Loc.T("Segments"), d.Segments, 4, 120, v => Aendern(x => x.Segments = v));
        Regler(Loc.T("Thickness"), d.ArcThickness, 1f, 40f, v => Aendern(x => x.ArcThickness = v));
        Regler(Loc.T("Start angle"), d.StartAngle, 0f, 359f, v => Aendern(x => x.StartAngle = v));
        Regler(Loc.T("Sweep"), d.SweepAngle, 60f, 340f, v => Aendern(x => x.SweepAngle = v));
        Wahl(Loc.T("Needle"), d.Needle, v => Aendern(x => x.Needle = v), Nadelname);
        Schalter(Loc.T("Scale marks"), d.Ticks, v => Aendern(x => x.Ticks = v));
        Schalter(Loc.T("Scale numbers"), d.TickNumbers, v => Aendern(x => x.TickNumbers = v));
        Zahl(Loc.T("Small marks between"), d.MinorTicks, 0, 9, v => Aendern(x => x.MinorTicks = v));
        Schalter(Loc.T("Colour runs from low to high"), d.Gradient, v => Aendern(x => x.Gradient = v));

        Gruppe(Loc.T("Colours"));
        Farbe(Loc.T("Background"), d.Background, v => Aendern(x => x.Background = v));
        Farbe(Loc.T("Frame"), d.FrameColour, v => Aendern(x => x.FrameColour = v));
        Farbe(Loc.T("Unlit scale"), d.TrackColour, v => Aendern(x => x.TrackColour = v));
        Farbe(Loc.T("Low revs"), d.LowColour, v => Aendern(x => x.LowColour = v));
        Farbe(Loc.T("High revs"), d.HighColour, v => Aendern(x => x.HighColour = v));
        Farbe(Loc.T("Redline"), d.RedlineColour, v => Aendern(x => x.RedlineColour = v));
        Farbe(Loc.T("Needle"), d.NeedleColour, v => Aendern(x => x.NeedleColour = v));
        Farbe(Loc.T("Numbers"), d.TextColour, v => Aendern(x => x.TextColour = v));
        Farbe(Loc.T("Small text"), d.DimColour, v => Aendern(x => x.DimColour = v));
        Farbe(Loc.T("Accent (limiter flash)"), d.AccentColour, v => Aendern(x => x.AccentColour = v));

        Gruppe(Loc.T("Light and motion"));
        Regler(Loc.T("Glow"), d.Glow * 100f, 0f, 100f, v => Aendern(x => x.Glow = v / 100f));
        Schalter(Loc.T("Glow grows with the revs"), d.GlowFollowsRpm, v => Aendern(x => x.GlowFollowsRpm = v));
        Schalter(Loc.T("Pulse in the redline"), d.RedlinePulse, v => Aendern(x => x.RedlinePulse = v));
        Schalter(Loc.T("Flash at the rev limiter"), d.LimiterFlash, v => Aendern(x => x.LimiterFlash = v));
        Schalter(Loc.T("Gear pops on a shift"), d.GearPop, v => Aendern(x => x.GearPop = v));
        Regler(Loc.T("Needle smoothing"), d.Smoothing * 100f, 0f, 95f, v => Aendern(x => x.Smoothing = v / 100f));

        Gruppe(Loc.T("Frame and font"));
        Wahl(Loc.T("Frame"), d.Frame, v => Aendern(x => x.Frame = v), Rahmenname);
        Regler(Loc.T("Corner radius"), d.CornerRadius, 0f, 60f, v => Aendern(x => x.CornerRadius = v));
        Wahl(Loc.T("Font"), d.Font, v => Aendern(x => x.Font = v), Schriftname);
        Schalter(Loc.T("Italic"), d.Italic, v => Aendern(x => x.Italic = v));
        Regler(Loc.T("Speed size"), d.SpeedSize * 100f, 40f, 250f, v => Aendern(x => x.SpeedSize = v / 100f));
        Regler(Loc.T("Gear size"), d.GearSize * 100f, 40f, 250f, v => Aendern(x => x.GearSize = v / 100f));

        Gruppe(Loc.T("What it shows"));
        Schalter(Loc.T("Speed"), d.ShowSpeed, v => Aendern(x => x.ShowSpeed = v));
        Schalter(Loc.T("Unit under the speed"), d.ShowUnit, v => Aendern(x => x.ShowUnit = v));
        Schalter(Loc.T("Revs as a number"), d.ShowRpm, v => Aendern(x => x.ShowRpm = v));
        Schalter(Loc.T("Gear"), d.ShowGear, v => Aendern(x => x.ShowGear = v));
        Schalter(Loc.T("Shift lights"), d.ShowShiftLights, v => Aendern(x => x.ShowShiftLights = v));
        Zahl(Loc.T("Number of shift lights"), d.ShiftLights, 3, 20, v => Aendern(x => x.ShiftLights = v));
        Regler(Loc.T("First shift light at (% of the redline)"), d.ShiftLightsFrom * 100f, 30f, 98f, v => Aendern(x => x.ShiftLightsFrom = v / 100f));
        Schalter(Loc.T("Engine power"), d.ShowPower, v => Aendern(x => x.ShowPower = v));
        Schalter(Loc.T("Boost"), d.ShowBoost, v => Aendern(x => x.ShowBoost = v));
        Schalter(Loc.T("Throttle and brake"), d.ShowPedals, v => Aendern(x => x.ShowPedals = v));
        Schalter(Loc.T("Wheelspin and lock lamps"), d.ShowTraction, v => Aendern(x => x.ShowTraction = v));
        Text(Loc.T("Wheelspin lamp text"), d.TractionText, v => Aendern(x => x.TractionText = v));
        Text(Loc.T("Lock lamp text"), d.LockText, v => Aendern(x => x.LockText = v));
        Wahl(Loc.T("Clock"), d.Clock, v => Aendern(x => x.Clock = v), Uhrname);
        Wahl(Loc.T("Distance"), d.Distance, v => Aendern(x => x.Distance = v), Streckename);

        Gruppe(Loc.T("Redline"));
        Wahl(Loc.T("Where the redline starts"), d.Redline, v => Aendern(x => x.Redline = v), Rotname);
        Regler(Loc.T("Fixed redline (% of the maximum revs)"), d.RedlinePercent * 100f, 50f, 100f, v => Aendern(x => x.RedlinePercent = v / 100f));

        _baukasten.Controls.Add(new Label
        {
            Text = Loc.T("Engine power is what the game reports for the engine right now; the game sends no figure for the power at the wheels. The lamps light when the driven wheels spin or a wheel locks -- whether or not the assist is on. The time of day in the game is not in the telemetry, so the clock is your real one, or race or lap time."),
            AutoSize = true, MaximumSize = new Size(320, 0), ForeColor = Leise, Margin = new Padding(3, 14, 3, 10),
        });
        _baukasten.ResumeLayout();
        _still = false;
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
        var z = Zeile(text);
        var t = new TextBox { Text = wert, Width = 160, BackColor = Grund, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
        t.TextChanged += (_, _) => { if (t.Text.Trim().Length > 0) { setzen(t.Text); } };
        z.Controls.Add(t);
    }

    private void Schalter(string text, bool wert, Action<bool> setzen)
    {
        var c = new CheckBox { Text = text, Checked = wert, AutoSize = true, ForeColor = Color.Gainsboro, Margin = new Padding(3, 3, 3, 3) };
        c.CheckedChanged += (_, _) => setzen(c.Checked);
        _baukasten.Controls.Add(c);
    }

    private void Zahl(string text, int wert, int min, int max, Action<int> setzen)
    {
        var z = Zeile(text);
        var n = new NumericUpDown { Minimum = min, Maximum = max, Value = Math.Clamp(wert, min, max), Width = 80, BackColor = Grund, ForeColor = Color.White };
        n.ValueChanged += (_, _) => setzen((int)n.Value);
        z.Controls.Add(n);
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

    private void Wahl<T>(string text, T wert, Action<T> setzen, Func<T, string> name) where T : struct, Enum
    {
        var z = Zeile(text);
        var werte = Enum.GetValues<T>();
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, BackColor = Grund, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        foreach (var v in werte) { c.Items.Add(name(v)); }
        c.SelectedIndex = Math.Max(0, Array.IndexOf(werte, wert));
        c.SelectedIndexChanged += (_, _) => { if (c.SelectedIndex >= 0) { setzen(werte[c.SelectedIndex]); } };
        z.Controls.Add(c);
    }

    private void Farbe(string text, string wert, Action<string> setzen)
    {
        var z = Zeile(text);
        var farbe = SpeedoDesign.Farbe(wert, Color.White);
        var knopf = new Button { Width = 70, Height = 24, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(255, farbe), Text = string.Empty };
        var deck = new NumericUpDown { Minimum = 0, Maximum = 100, Value = (int)Math.Round(farbe.A / 2.55), Width = 56, BackColor = Grund, ForeColor = Color.White };
        var prozent = new Label { Text = "%", AutoSize = true, ForeColor = Leise, Margin = new Padding(0, 6, 0, 0) };
        void Setzen()
        {
            var c = Color.FromArgb((int)Math.Round((double)deck.Value * 2.55), knopf.BackColor);
            setzen(OverlaySettings.ToHex(c));
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
        z.Controls.Add(prozent);
    }

    /// <summary>Titel und Auswahl in einer Zeile, fuer die Leiste oben.</summary>
    private FlowLayoutPanel Auswahl(string text, (string Key, string Text)[] werte, string? jetzt, Action<string> setzen)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80, BackColor = Feld, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Margin = new Padding(3, 4, 14, 3) };
        foreach (var w in werte) { c.Items.Add(w.Text); }
        c.SelectedIndex = Math.Max(0, Array.FindIndex(werte, w => string.Equals(w.Key, jetzt, StringComparison.OrdinalIgnoreCase)));
        c.SelectedIndexChanged += (_, _) =>
        {
            if (c.SelectedIndex < 0) { return; }
            setzen(werte[c.SelectedIndex].Key);
            _settings.Save();
            _designGeaendert(null);
            _vorschau.Invalidate();
        };
        var feld = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        feld.Controls.Add(new Label { Text = text, AutoSize = true, ForeColor = Leise, Margin = new Padding(3, 8, 3, 3) });
        feld.Controls.Add(c);
        return feld;
    }

    private static Button Knopf(string text) => new()
    {
        Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Feld, ForeColor = Color.WhiteSmoke, Margin = new Padding(3),
    };

    // ---- Namen der Auswahlwerte ----

    private static string Layoutname(SpeedoLayout v) => v switch
    {
        SpeedoLayout.Dial => Loc.T("Round dial"),
        SpeedoLayout.Arc => Loc.T("Open arc"),
        SpeedoLayout.Bar => Loc.T("Horizontal bar"),
        SpeedoLayout.Vertical => Loc.T("Vertical column"),
        SpeedoLayout.Twin => Loc.T("Twin gauges"),
        SpeedoLayout.Digital => Loc.T("Race display"),
        _ => Loc.T("Motorcycle"),
    };

    private static string Fuellname(SpeedoFill v) => v switch
    {
        SpeedoFill.Smooth => Loc.T("Smooth"),
        SpeedoFill.Segments => Loc.T("Segments"),
        SpeedoFill.Blocks => Loc.T("Blocks"),
        _ => Loc.T("Lines"),
    };

    private static string Nadelname(SpeedoNeedle v) => v switch
    {
        SpeedoNeedle.None => Loc.T("None"),
        SpeedoNeedle.Classic => Loc.T("Classic"),
        SpeedoNeedle.Line => Loc.T("Thin line"),
        SpeedoNeedle.Glow => Loc.T("Glowing line"),
        _ => Loc.T("Wedge"),
    };

    private static string Schriftname(SpeedoFont v) => v switch
    {
        SpeedoFont.Sans => Loc.T("Plain"),
        SpeedoFont.Condensed => Loc.T("Condensed"),
        SpeedoFont.Mono => Loc.T("Monospace"),
        _ => Loc.T("Seven-segment"),
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

    private static string Uhrname(SpeedoClock v) => v switch
    {
        SpeedoClock.Off => Loc.T("Off"),
        SpeedoClock.Real24 => Loc.T("Real time (24 h)"),
        SpeedoClock.Real12 => Loc.T("Real time (12 h)"),
        SpeedoClock.RaceTime => Loc.T("Race time"),
        _ => Loc.T("Lap time"),
    };

    private static string Streckename(SpeedoDistance v) => v switch
    {
        SpeedoDistance.Off => Loc.T("Off"),
        SpeedoDistance.Trip => Loc.T("Trip (since start or reset)"),
        SpeedoDistance.Race => Loc.T("This race"),
        _ => Loc.T("Odometer (all driving)"),
    };

    private static string Rotname(SpeedoRedline v) => v switch
    {
        SpeedoRedline.Learned => Loc.T("Learned per car (peak power)"),
        _ => Loc.T("Fixed share of the maximum revs"),
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
