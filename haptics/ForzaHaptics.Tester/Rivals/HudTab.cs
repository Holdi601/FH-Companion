using System.Drawing;
using System.Drawing.Drawing2D;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>Welches der drei Anzeigestuecke gemeint ist.</summary>
// Course: die Umrisse der angebotenen Strecken. Anders als die uebrigen Teile
// gehoert er zum Anmeldeschirm und nicht ins Rennen -- geschoben wird er aber
// genauso, und darum steht er hier.
internal enum HudPart { Delta, Ghost, Note, Inputs, Course, CarNote, LiveMap, Tyres }

/// <summary>
/// Der Reiter, auf dem der Streifen eingerichtet wird -- durch Ziehen, nicht Tippen.
/// </summary>
/// <remarks>
/// Die erste Fassung waren Zahlenfelder: "X (0 = links, 1 = rechts) = 0,5". Das ist
/// keine Einstellung, das ist eine Rechenaufgabe -- wer eine Anzeige an eine Stelle
/// setzen will, will sie DORTHIN ziehen und dabei sehen, wie sie aussieht. Genau das
/// hat der Nutzer am 2026-09-12 verlangt, und er hatte recht.
///
/// Die Flaeche links zeigt den Bildschirm im selben Seitenverhaeltnis und rechnet mit
/// denselben Groessen wie <see cref="DeltaHud"/> -- was hier steht, steht im Rennen
/// genauso. Das graue Feld oben links ist die Anzeige des SPIELS (Rundenzeit und
/// Fortschritt); sie steht dort, sie laesst sich nicht verschieben, und alles, was
/// darauf liegt, verdeckt sie.
/// </remarks>
internal sealed class HudTab : UserControl
{
    private readonly OverlaySettings _settings;
    private readonly Action _changed;
    private readonly Action<bool> _preview;
    private readonly HudLayoutCanvas _canvas;
    private readonly HudPartPanel _side;
    private bool _shown;

    /// <summary>"Try it" bei der Feier: das Hauptfenster reicht es an das Overlay weiter.</summary>
    public event Action? FeierProbe;
    /// <summary>"Try it" bei der Meldung "neues Auto".</summary>
    public event Action? NeuesAutoProbe;
    /// <summary>"Try it" bei den eigenen Rekorden.</summary>
    public event Action? PersoenlichProbe;
    /// <summary>Das Aufnahmefenster oeffnen (Hauptfenster -> Rivals-Reiter).</summary>
    public event Action? AufnahmeFensterWunsch;

    public HudTab(OverlaySettings settings, Action changed, Action<bool> preview)
    {
        _settings = settings;
        _changed = changed;
        _preview = preview;

        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(23, 27, 33);

        _canvas = new HudLayoutCanvas(settings) { Dock = DockStyle.Fill };
        _side = new HudPartPanel(settings) { Dock = DockStyle.Fill };

        _canvas.Moved += () => { _changed(); Repaint(); };
        _canvas.Committed += () => { _settings.Save(); _side.RefreshPlacements(); _changed(); Repaint(); };
        _canvas.SelectionChanged += (part, klick) => _side.Select(part, springen: klick);
        _side.Changed += () =>
        {
            _settings.Save();
            _canvas.Invalidate();
            _changed();
            Repaint();
        };
        _side.QuelleGeaendert += () => _canvas.VorschauVergessen();
        _side.FeierProbe += () => FeierProbe?.Invoke();
        _side.NeuesAutoProbe += () => NeuesAutoProbe?.Invoke();
        _side.PersoenlichProbe += () => PersoenlichProbe?.Invoke();
        _side.AufnahmeFensterWunsch += () => AufnahmeFensterWunsch?.Invoke();
        _side.Select(HudPart.Delta);

        // HOCH WIE SEIN TEXT (seit 2026-09-29): mit fester Hoehe war die zweite Zeile in
        // schmalen Fenstern und langen Sprachen abgeschnitten. Und beide Saetze uebersetzt --
        // vorher nur der Anfang des ersten (zusammengesetzte Texte findet der Sprachscanner nicht).
        var hint = new Label
        {
            Dock = DockStyle.Top,
            ForeColor = Color.Gainsboro,
            Padding = new Padding(12, 7, 12, 6),
            Text = Loc.T("Drag the blocks where you want them. Mouse wheel over a block resizes it. Everything saves itself.")
                   + "\r\n"
                   + Loc.T("The grey box is the game's own lap time and progress -- it sits top left and cannot be moved, so keep clear of it."),
        };
        void HinweisHoehe()
        {
            var breite = Math.Max(200, Width - hint.Padding.Horizontal);
            hint.Height = TextRenderer.MeasureText(hint.Text, hint.Font, new Size(breite, 0),
                                                   TextFormatFlags.WordBreak).Height + hint.Padding.Vertical + 2;
        }
        HinweisHoehe();
        SizeChanged += (_, _) => HinweisHoehe();

        var toggle = new Button
        {
            Dock = DockStyle.Bottom,
            Height = 36,
            Text = Loc.T("Show it on the real screen while I set it up"),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(38, 44, 52),
            ForeColor = Color.WhiteSmoke,
        };
        toggle.FlatAppearance.BorderColor = Color.FromArgb(70, 80, 92);
        toggle.Click += (_, _) =>
        {
            _shown = !_shown;
            toggle.Text = _shown
                ? Loc.T("Hide it from the real screen")
                : Loc.T("Show it on the real screen while I set it up");
            _preview(_shown);
        };

        // Die Vorschau darf nicht ueber dem Spiel haengen bleiben.
        Disposed += (_, _) => { if (_shown) { try { _preview(false); } catch (Exception) { } } };

        // DER TEILER (seit 2026-09-28): die Einstellungen rechts sind breiter zu
        // ziehen; ist Platz, stehen ihre Abschnitte nebeneinander. Die Breite bleibt
        // gemerkt. Mindestbreiten erst setzen, wenn der Teiler seine Groesse hat --
        // vorher wirft WinForms, weil 150 Bildpunkte nicht fuer beide Seiten reichen.
        var teiler = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            FixedPanel = FixedPanel.Panel2,
            SplitterWidth = 6,
            BackColor = Color.FromArgb(58, 68, 80),
        };
        teiler.Panel1.BackColor = BackColor;
        teiler.Panel2.BackColor = Color.FromArgb(28, 33, 40);
        teiler.Panel1.Controls.Add(_canvas);
        teiler.Panel2.Controls.Add(_side);
        var eingerichtet = false;
        void Einrichten()
        {
            if (eingerichtet || teiler.Width < 700) { return; }
            try
            {
                teiler.Panel1MinSize = 320;
                teiler.Panel2MinSize = SeiteMindestens;
                var seite = Math.Clamp(_settings.HudEditorSideWidth, SeiteMindestens,
                                       teiler.Width - 320 - teiler.SplitterWidth);
                teiler.SplitterDistance = teiler.Width - seite - teiler.SplitterWidth;
                eingerichtet = true;
            }
            catch (Exception)
            {
                // Zu schmal fuer beide Seiten: beim naechsten Mal.
            }
        }
        teiler.SizeChanged += (_, _) => Einrichten();
        teiler.HandleCreated += (_, _) => Einrichten();
        teiler.SplitterMoved += (_, _) =>
        {
            if (!eingerichtet) { return; }
            _settings.HudEditorSideWidth = teiler.Panel2.Width;
            _settings.Save();
        };
        _teiler = teiler;

        Controls.Add(teiler);
        Controls.Add(toggle);
        Controls.Add(hint);
    }

    /// <summary>So schmal darf die Seite werden: eine Spalte samt Rollleiste.</summary>
    internal static int SeiteMindestens => AbschnittSpalten.SpaltenBreite + SystemInformation.VerticalScrollBarWidth + 8;

    private readonly SplitContainer _teiler;

    /// <summary>Fuer Test und Vorschau: die Seite auf eine Breite stellen.</summary>
    internal void SeitenBreite(int breite)
    {
        try { _teiler.SplitterDistance = Math.Max(320, _teiler.Width - breite - _teiler.SplitterWidth); }
        catch (Exception) { }
    }

    /// <summary>Fuer den Grenzfalltest: die Seite mit ihren Abschnitten.</summary>
    internal HudPartPanel Seite => _side;

    /// <summary>Fuer den Grenzfalltest: ein Klick auf ein Stueck in der Vorschau.</summary>
    internal void KlickAuf(HudPart part) => _side.Select(part, springen: true);

    /// <summary>Die Anzeige nachziehen, wenn im Spiel umgeschaltet wurde.</summary>
    public void RefreshMode()
    {
        _side.RefreshMode();
        _canvas.Invalidate();
        Repaint();
    }

    /// <summary>Fuer --hud-editor-preview: dieses Stueck ausgewaehlt zeigen.</summary>
    internal void ShowPart(HudPart part) => _canvas.ChooseForPreview(part);

    private void Repaint()
    {
        if (_shown) { try { _preview(true); } catch (Exception) { } }
    }
}

/// <summary>Die Bildschirmflaeche, auf der die Stuecke liegen und gezogen werden.</summary>
internal sealed class HudLayoutCanvas : Control
{
    private readonly OverlaySettings _settings;
    private readonly Dictionary<HudPart, RectangleF> _boxes = new();
    private HudPart _selected = HudPart.Delta;
    private HudPart? _dragging;
    private PointF _grab;
    private RectangleF _screen;

    public event Action? Moved;
    public event Action? Committed;
    /// <summary>Ein Stueck wurde gewaehlt; true, wenn per Klick (dann springt die Seite dorthin).</summary>
    public event Action<HudPart, bool>? SelectionChanged;

    public HudLayoutCanvas(OverlaySettings settings)
    {
        _settings = settings;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        BackColor = Color.FromArgb(18, 21, 26);
        Cursor = Cursors.Hand;
    }

    /// <summary>Fuer --hud-editor-preview: ein Stueck auswaehlen wie per Klick.</summary>
    internal void ChooseForPreview(HudPart part) => Choose(part);

    /// <summary>Ein Stueck auswaehlen -- Methode, keine Eigenschaft.</summary>
    /// <remarks>
    /// Eine oeffentliche Eigenschaft auf einem Control will der WinForms-Pruefer als
    /// entwurfszeit-serialisierbar sehen; das ist sie nicht und soll sie nicht sein.
    /// </remarks>
    private void Choose(HudPart part, bool klick = false)
    {
        // Ein Klick meldet sich immer, auch auf das schon gewaehlte Stueck: wer
        // inzwischen weggerollt hat, will mit einem Klick zurueck zu dessen Knoepfen.
        if (_selected == part && !klick) { return; }
        _selected = part;
        SelectionChanged?.Invoke(part, klick);
        Invalidate();
    }

    /// <summary>Die Bildschirmflaeche im Seitenverhaeltnis des echten Schirms.</summary>
    private RectangleF ScreenArea()
    {
        // Das Seitenverhaeltnis der SPIELFLAECHE, nicht des Hauptschirms.
        var echt = GameArea.Find(_settings.ForzaProcess);
        var verhaeltnis = (float)echt.Width / Math.Max(1, echt.Height);
        var rand = 14f;
        var breite = Width - 2 * rand;
        var hoehe = breite / verhaeltnis;
        if (hoehe > Height - 2 * rand)
        {
            hoehe = Height - 2 * rand;
            breite = hoehe * verhaeltnis;
        }
        return new RectangleF((Width - breite) / 2, (Height - hoehe) / 2, breite, hoehe);
    }

    // Lage, Groesse, Anker: alles ueber OverlaySettings, dort fuer jedes der sechs
    // Stuecke ausdruecklich (siehe PlacementOf -- warum es dort steht).
    private HudPlacement Place(HudPart part) => _settings.PlacementOf(part);

    private void SetPlace(HudPart part, float x, float y) => _settings.SetPosition(part, x, y);

    /// <summary>Die Kopfzeile der Beispielnotiz -- im Aufbau wie die echte (OverlayController).</summary>
    private static string NotizKopf => "Porsche 911 GT3 RS '19  ·  PI 900  ·  " + string.Format(Loc.T("{0} hp"), 513);

    /// <summary>
    /// Die echte Spielflaeche und der Faktor, mit dem sie auf die Vorschau passt.
    /// Die Autonotiz wird auf die ECHTE Flaeche gerechnet und gezeichnet und nur
    /// verkleinert -- so gelten Schrift, Umbruch, Mindestgroessen und Randklemme
    /// genau wie im Overlay, und der Kasten zum Anfassen IST die sichtbare Platte.
    /// </summary>
    private (Size Echt, float Faktor) NotizMassstab(RectangleF schirm)
    {
        var echt = GameArea.Find(_settings.ForzaProcess).Size;
        if (echt.Width <= 0 || echt.Height <= 0) { echt = new Size(1920, 1080); }
        return (echt, schirm.Width / echt.Width);
    }

    // DIESELBEN SCHLUESSEL WIE IM OVERLAY (OverlayController, DeltaHud): die Vorschau
    // zeigt genau die Worte, die im Rennen stehen -- in jeder Sprache.
    private static string BigText(HudPart part) => part switch
    {
        HudPart.Ghost => string.Format(Loc.T("GHOST {0}"), 12.4.ToString("0.0")),
        HudPart.Note => string.Format(Loc.T("lap stored: {0} s, {1} m"), 83.706.ToString("0.000"), "5949"),
        // Der Umriss-Block wird eigens gezeichnet (BoxOf und Draw kehren vorher um);
        // dieser Text erscheint nie und bleibt darum unuebersetzt.
        HudPart.Course => "course shapes",
        HudPart.CarNote => Loc.T("understeers from turn 3, tyres go off after 4 laps"),
        _ => "-0.734",
    };

    private static string SmallText(HudPart part) => part switch
    {
        HudPart.Delta => Loc.T("same car, this course"),
        _ => string.Empty,
    };

    private Color Ink(HudPart part) => part switch
    {
        // Auf der Ampelflaeche steht die Schrift in ihrer eigenen Farbe.
        HudPart.Ghost => OverlaySettings.ParseColour(_settings.HudColorGhostText, Color.Black),
        HudPart.Note => OverlaySettings.ParseColour(_settings.HudColorAhead, Color.LightGreen),
        _ => OverlaySettings.ParseColour(_settings.HudColorAhead, Color.LightGreen),
    };

    /// <summary>Groesse und Lage eines Stuecks -- dieselbe Rechnung wie im Streifen.</summary>
    private RectangleF BoxOf(Graphics g, HudPart part, RectangleF schirm)
    {
        var einheit = Math.Max(3f, schirm.Height / 36f);
        var lage = Place(part);
        if (part == HudPart.Course)
        {
            // DIESELBE RECHNUNG WIE IM SPIEL (CourseShapeHud.Lege), auf der echten
            // Flaeche und dann verkleinert -- samt Randklemme: vorher lief der Block
            // hier rechts aus dem Bild, im Overlay aber nicht.
            var (echt, f) = NotizMassstab(schirm);
            var b = CourseShapeHud.Lege(_settings, echt, VorschauKurse().Count).Block;
            return new RectangleF(schirm.X + b.X * f, schirm.Y + b.Y * f, b.Width * f, b.Height * f);
        }
        if (part == HudPart.LiveMap)
        {
            var (echt, f) = NotizMassstab(schirm);
            var b = LiveMapHud.Lege(_settings, echt);
            return new RectangleF(schirm.X + b.X * f, schirm.Y + b.Y * f, b.Width * f, b.Height * f);
        }
        if (part == HudPart.Tyres)
        {
            var (echt, f) = NotizMassstab(schirm);
            var b = TyreHud.Lege(_settings, echt);
            return new RectangleF(schirm.X + b.X * f, schirm.Y + b.Y * f, b.Width * f, b.Height * f);
        }
        if (part == HudPart.CarNote)
        {
            // DIESELBE RECHNUNG WIE IM SPIEL -- nicht nachgebaut, sondern aufgerufen
            // (CarNoteHud.Lege), auf der echten Flaeche und dann verkleinert.
            var (echt, f) = NotizMassstab(schirm);
            var zustand = g.Save();
            CarNoteHud.Aufbau a;
            try
            {
                g.TranslateTransform(schirm.X, schirm.Y);
                g.ScaleTransform(f, f);
                a = CarNoteHud.Lege(g, _settings, echt, NotizKopf, BigText(part));
            }
            finally { g.Restore(zustand); }
            return new RectangleF(schirm.X + a.Kasten.X * f, schirm.Y + a.Kasten.Y * f,
                                  a.Kasten.Width * f, a.Kasten.Height * f);
        }
        if (part == HudPart.Inputs)
        {
            // Feste Masse wie im Streifen: vier Spuren untereinander.
            var b = einheit * 9f * lage.Scale;
            var spur = einheit * 1.05f * lage.Scale;
            var h = 5 * spur + 4 * (einheit * 0.30f * lage.Scale) + einheit * 0.5f;
            var l = schirm.X + schirm.Width * lage.X;
            l = lage.Align.ToLowerInvariant() switch
            {
                "left" => l,
                "right" => l - b,
                _ => l - b / 2,
            };
            return new RectangleF(l, schirm.Y + schirm.Height * lage.Y, b, h);
        }
        var grossGroesse = part switch
        {
            HudPart.Ghost => einheit * 1.2f * lage.Scale,
            HudPart.Note => einheit * 0.72f * lage.Scale,
            _ => einheit * 2.2f * lage.Scale,
        };
        using var gross = new Font("Segoe UI Semibold", Math.Max(2f, grossGroesse));
        using var klein = new Font("Segoe UI", Math.Max(2f, einheit * 0.72f * lage.Scale));
        var g1 = g.MeasureString(BigText(part), gross);
        var g2 = string.IsNullOrEmpty(SmallText(part))
            ? SizeF.Empty
            : g.MeasureString(SmallText(part), klein);
        var breite = Math.Max(g1.Width, g2.Width) + 4 * einheit;
        var hoehe = g1.Height + g2.Height + einheit;

        var links = schirm.X + schirm.Width * lage.X;
        links = lage.Align.ToLowerInvariant() switch
        {
            "left" => links,
            "right" => links - breite,
            _ => links - breite / 2,
        };
        return new RectangleF(links, schirm.Y + schirm.Height * lage.Y, breite, hoehe);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        _screen = ScreenArea();

        // Der Schirm selbst, mit einer angedeuteten Strasse, damit sichtbar wird,
        // dass die Anzeigen ueber etwas liegen.
        using (var grund = new LinearGradientBrush(_screen,
                   Color.FromArgb(48, 56, 66), Color.FromArgb(28, 33, 40), 90f))
        {
            g.FillRectangle(grund, _screen);
        }
        using (var rahmen = new Pen(Color.FromArgb(80, 92, 106)))
        {
            g.DrawRectangle(rahmen, _screen.X, _screen.Y, _screen.Width, _screen.Height);
        }

        // Die Anzeige des SPIELS: oben links, unverrueckbar.
        var einheit = Math.Max(3f, _screen.Height / 36f);
        var spiel = new RectangleF(_screen.X + _screen.Width * 0.012f,
                                   _screen.Y + _screen.Height * 0.012f,
                                   _screen.Width * 0.20f, _screen.Height * 0.13f);
        using (var grau = new SolidBrush(Color.FromArgb(70, 255, 255, 255)))
        using (var strich = new Pen(Color.FromArgb(120, 255, 255, 255)) { DashStyle = DashStyle.Dash })
        using (var schrift = new Font("Segoe UI", Math.Max(6f, einheit * 0.6f)))
        {
            g.FillRectangle(grau, spiel);
            g.DrawRectangle(strich, spiel.X, spiel.Y, spiel.Width, spiel.Height);
            // EIN SATZ, im Kasten umbrochen (seit 2026-09-29): die festen Zeilenwechsel
            // passten nur zum Englischen, und uebersetzen liess sich der Text so nicht.
            g.DrawString(Loc.T("the game writes lap time and progress here"), schrift, Brushes.White,
                         new RectangleF(spiel.X + 6, spiel.Y + 5, Math.Max(1f, spiel.Width - 12),
                                        Math.Max(1f, spiel.Height - 8)));
        }

        _boxes.Clear();
        foreach (var part in new[]
                 { HudPart.Course, HudPart.LiveMap, HudPart.Tyres, HudPart.CarNote, HudPart.Inputs, HudPart.Note,
                   HudPart.Ghost, HudPart.Delta })
        {
            var kasten = BoxOf(g, part, _screen);
            _boxes[part] = kasten;
            Draw(g, part, kasten, einheit);
        }
    }

    private void Draw(Graphics g, HudPart part, RectangleF kasten, float einheit)
    {
        var lage = Place(part);
        if (part == HudPart.Course)
        {
            var (echt, f) = NotizMassstab(_screen);
            var zustand = g.Save();
            try
            {
                g.TranslateTransform(_screen.X, _screen.Y);
                g.ScaleTransform(f, f);
                CourseShapeHud.Male(g, _settings, echt, VorschauKurse(), auchWennAus: true);
            }
            finally { g.Restore(zustand); }
            if (part == _selected) { Rahmen(g, kasten, part); }
            return;
        }
        if (part == HudPart.Inputs)
        {
            DrawInputsPreview(g, kasten, einheit, lage);
            if (part == _selected) { Rahmen(g, kasten, part); }
            return;
        }
        if (part == HudPart.LiveMap)
        {
            // Mit der Zeichnung des Overlays, auf einer echten eigenen Strecke; das
            // Auto steht nach einem Drittel, der gefahrene Teil ist hervorgehoben.
            var probe = VorschauKurse().Select(k => k.Shape).FirstOrDefault(u => u is { Ordered: true, IsEmpty: false });
            var (echt, f) = NotizMassstab(_screen);
            var zustand = g.Save();
            try
            {
                g.TranslateTransform(_screen.X, _screen.Y);
                g.ScaleTransform(f, f);
                if (probe is not null)
                {
                    var pfad = LiveMapHud.SampleAus(probe);
                    var bis = pfad.Count / 3;
                    LiveMapHud.Male(g, _settings, echt, pfad, pfad.Take(bis + 1).ToList(), pfad[bis], auchWennAus: true);
                }
                else
                {
                    var b = LiveMapHud.Lege(_settings, echt);
                    using var leer = new Pen(Color.FromArgb(120, 127, 211, 255), 1f) { DashStyle = DashStyle.Dot };
                    g.DrawRectangle(leer, b);
                }
            }
            finally { g.Restore(zustand); }
            if (part == _selected) { Rahmen(g, kasten, part); }
            return;
        }
        if (part == HudPart.Tyres)
        {
            // Mit der Zeichnung des Overlays und einem Beispiel, das jeden Zustand
            // einmal zeigt: heiss und rutschend, Curb, blockiert, Pfuetze.
            var (echt, f) = NotizMassstab(_screen);
            var zustand = g.Save();
            try
            {
                g.TranslateTransform(_screen.X, _screen.Y);
                g.ScaleTransform(f, f);
                TyreHud.Male(g, _settings, echt, TyreHud.Beispiel());
            }
            finally { g.Restore(zustand); }
            if (part == _selected) { Rahmen(g, kasten, part); }
            return;
        }
        if (part == HudPart.CarNote)
        {
            // Mit der Zeichnung des Overlays. Bis zum 2026-09-25 fiel die Notiz hier
            // in die allgemeine Textbox: Delta-Schrift, kein Umbruch -- der Text lief
            // weit ueber den Kasten hinaus, den man anfasst.
            var (echt, f) = NotizMassstab(_screen);
            var zustand = g.Save();
            try
            {
                g.TranslateTransform(_screen.X, _screen.Y);
                g.ScaleTransform(f, f);
                CarNoteHud.Male(g, _settings, echt, NotizKopf, BigText(part), auchWennAus: true);
            }
            finally { g.Restore(zustand); }
            if (part == _selected) { Rahmen(g, kasten, part); }
            return;
        }
        var grossGroesse = part switch
        {
            HudPart.Ghost => einheit * 1.2f * lage.Scale,
            HudPart.Note => einheit * 0.72f * lage.Scale,
            _ => einheit * 2.2f * lage.Scale,
        };
        using var gross = new Font("Segoe UI Semibold", Math.Max(2f, grossGroesse));
        using var klein = new Font("Segoe UI", Math.Max(2f, einheit * 0.72f * lage.Scale));

        using (var platte = new SolidBrush(
                   part == HudPart.Ghost
                       ? OverlaySettings.ParseColour(_settings.HudColorGhost,
                                                     Color.DeepSkyBlue)
                       : OverlaySettings.ParseColour(_settings.HudBackground,
                                                     Color.FromArgb(150, 8, 11, 16))))
        {
            g.FillRectangle(platte, kasten);
        }

        var text = BigText(part);
        var masse = g.MeasureString(text, gross);
        using (var stift = new SolidBrush(Ink(part)))
        {
            g.DrawString(text, gross, stift,
                         kasten.X + (kasten.Width - masse.Width) / 2, kasten.Y + 2);
        }
        var klein2 = SmallText(part);
        if (!string.IsNullOrEmpty(klein2))
        {
            var m2 = g.MeasureString(klein2, klein);
            using var leise = new SolidBrush(
                OverlaySettings.ParseColour(_settings.HudColorLabel, Color.Gray));
            g.DrawString(klein2, klein, leise,
                         kasten.X + (kasten.Width - m2.Width) / 2,
                         kasten.Y + masse.Height + 2);
        }

        // Das ausgewaehlte Stueck bekommt einen Rahmen -- sonst ist nicht zu sehen,
        // worauf sich die Knoepfe rechts beziehen.
        if (part == _selected) { Rahmen(g, kasten, part); }
    }

    /// <summary>Wie das Stueck im Rahmen heisst -- und ob es gerade abgeschaltet ist.</summary>
    /// <remarks>
    /// "course" hiess bis zum 2026-09-25 der Block der Anmeldung; in der Vorschau, die
    /// ueber einer Strasse liegt, las er sich wie eine Karte fuers Rennen. Jetzt sagt
    /// der Rahmen, WANN das Stueck erscheint.
    /// </remarks>
    private string Marke(HudPart part)
    {
        // Uebersetzt (seit 2026-09-29). Wo kein Zeitpunkt dazugehoert, heisst der Rahmen wie
        // der Abschnitt rechts (Titel) -- vorher kleingeschrieben und ohne Uebersetzung.
        var name = part switch
        {
            HudPart.Course => Loc.T("Event Sign Up maps (before the race)"),
            HudPart.LiveMap => Loc.T("Live map (during the race)"),
            HudPart.Tyres => Loc.T("Tyre overview (while driving)"),
            _ => HudPartPanel.Titel(part),
        };
        var aus = part switch
        {
            HudPart.Course => !_settings.CourseShapes,
            HudPart.LiveMap => !_settings.LiveMap,
            HudPart.Tyres => !_settings.HudTyres,
            HudPart.CarNote => !_settings.CarNotes,
            HudPart.Inputs => !_settings.HudInputs,
            _ => false,
        };
        return aus ? string.Format(Loc.T("{0} -- switched off"), name) : name;
    }

    private void Rahmen(Graphics g, RectangleF kasten, HudPart part)
    {
        using var rahmen = new Pen(Color.FromArgb(120, 200, 255), 2f);
        g.DrawRectangle(rahmen, kasten.X - 2, kasten.Y - 2,
                        kasten.Width + 4, kasten.Height + 4);
        using var marke = new Font("Segoe UI", 7.5f);
        // Die Beschriftung bleibt im Bild: an einem Block am rechten Rand lief
        // "Live map (during the race) -- switched off" sonst ueber die Kante.
        var text = Marke(part);
        var breite = g.MeasureString(text, marke).Width;
        var x = Math.Min(kasten.X - 2, Width - breite - 4);
        var y = kasten.Y - 18 >= 0 ? kasten.Y - 18 : kasten.Bottom + 4;
        g.DrawString(text, marke, Brushes.LightSkyBlue, Math.Max(0, x), y);
    }

    /// <summary>Drei Kurse aus dem eigenen Bestand fuer die Umriss-Vorschau.</summary>
    /// <remarks>
    /// Echte Strecken statt Platzhalter: sonst richtet man Groesse und Farbe nach
    /// etwas ein, das nachher anders aussieht -- und genau dafuer ist dieser Reiter da.
    /// </remarks>
    private List<(string Name, CourseShape.Outline? Shape)>? _vorschau;

    /// <summary>Nach dem Umstellen der Kartenquelle: die Beispielstrecken neu holen.</summary>
    internal void VorschauVergessen() { _vorschau = null; Invalidate(); }

    private List<(string Name, CourseShape.Outline? Shape)> VorschauKurse()
    {
        if (_vorschau is not null) { return _vorschau; }
        var raus = new List<(string, CourseShape.Outline?)>();
        try
        {
            var wurzel = LapArchive.Root;
            if (Directory.Exists(wurzel))
            {
                foreach (var ordner in Directory.EnumerateDirectories(wurzel))
                {
                    var kurs = Path.GetFileName(ordner);
                    var u = CourseShape.For(kurs, _settings.ShapeSourceChoice, wurzel);
                    if (u is null || u.IsEmpty) { continue; }
                    var name = CourseShape.KursName(wurzel, kurs);
                    // Der Platzhalter steht als Name ueber der Kachel (CourseShapeHud.Male).
                    raus.Add((string.IsNullOrEmpty(name) ? Loc.T("Course") : name, u));
                    if (raus.Count >= 3) { break; }
                }
            }
        }
        catch (Exception)
        {
            // Ohne Bestand bleibt die Vorschau leer; gestrichelte Kaesten genuegen.
        }
        // Immer drei, wie das Overlay sie anbietet: fehlende als leere Kacheln.
        while (raus.Count < 3) { raus.Add((Loc.T("Course"), null)); }
        _vorschau = raus;
        return raus;
    }

    /// <summary>Die Eingabespuren als Andeutung -- zwei erfundene Kurven.</summary>
    /// <remarks>
    /// Es geht hier um Lage und Groesse, nicht um Messwerte. Zwei verschiedene Linien
    /// genuegen, um zu sehen, wie viel Platz das Ding braucht und ob es etwas verdeckt.
    /// </remarks>
    private void DrawInputsPreview(Graphics g, RectangleF kasten, float einheit,
                                   HudPlacement lage)
    {
        using (var platte = new SolidBrush(
                   OverlaySettings.ParseColour(_settings.HudBackground,
                                               Color.FromArgb(150, 8, 11, 16))))
        {
            g.FillRectangle(platte, kasten);
        }
        var meine = OverlaySettings.ParseColour(_settings.HudColorMine, Color.White);
        var fremde = OverlaySettings.ParseColour(_settings.HudColorTheirs, Color.Orange);
        var beschriftung = OverlaySettings.ParseColour(_settings.HudColorLabel, Color.Gray);

        // Dieselben Schluessel wie die Spuren im Rennen (DeltaHud).
        var namen = new[] { Loc.T("THR"), Loc.T("BRK"), Loc.T("CLU"), Loc.T("STR"), Loc.T("GEAR") };
        var spur = einheit * 1.05f * lage.Scale;
        var luft = einheit * 0.30f * lage.Scale;
        using var klein = new Font("Segoe UI", Math.Max(4f, einheit * 0.46f * lage.Scale));
        // Dieselbe Staerke wie im Rennen, nur auf die kleinere Flaeche umgerechnet.
        var strich = (float)Math.Clamp(_settings.HudInputsLineWidth, 0.2, 6.0);
        using var stiftMeins = new Pen(meine, Math.Max(0.5f, 1.4f * strich));
        using var stiftFremd = new Pen(fremde, Math.Max(0.5f, 1.4f * strich));
        using var grund = new SolidBrush(beschriftung);

        var x0 = kasten.X + einheit * 1.5f * lage.Scale;
        var breite = kasten.Right - x0 - einheit * 0.4f * lage.Scale;

        // Die Jetzt-Linie sitzt dort, wo sie auch im Rennen sitzt: nach dem Anteil
        // der Rueckschau an der ganzen Spanne.
        var rueck = (float)Math.Clamp(_settings.HudInputsSeconds, 1, 60);
        var vor = (float)Math.Clamp(_settings.HudInputsLookahead, 0, 10);
        var jetztX = x0 + breite * (rueck / (rueck + vor));
        if (vor > 0)
        {
            using var kommt = new SolidBrush(Color.FromArgb(26, 255, 255, 255));
            g.FillRectangle(kommt, jetztX, kasten.Y, kasten.Right - jetztX, kasten.Height);
        }

        for (var i = 0; i < namen.Length; i++)
        {
            var y0 = kasten.Y + einheit * 0.25f + i * (spur + luft);
            g.DrawString(namen[i], klein, grund, kasten.X + einheit * 0.2f * lage.Scale, y0);
            for (var k = 0; k < 2; k++)
            {
                var stift = k == 0 ? stiftFremd : stiftMeins;
                // Meine Spur (k = 1) endet an der Jetzt-Linie, die der Bestzeit
                // laeuft weiter -- genau der Unterschied, um den es geht.
                var bis = k == 0 ? kasten.Right - x0 - einheit * 0.4f * lage.Scale
                                 : jetztX - x0;
                var punkte = new List<PointF>();
                for (var t = 0; t <= 24; t++)
                {
                    var a = t / 24f;
                    var welle = MathF.Sin((a * 6f) + i * 1.7f + k * 0.6f) * 0.5f + 0.5f;
                    punkte.Add(new PointF(x0 + bis * a, y0 + spur * (1f - welle)));
                }
                g.DrawLines(stift, punkte.ToArray());
            }
        }

        using var stiftJetzt = new Pen(
            OverlaySettings.ParseColour(_settings.HudColorNow, Color.LightGreen),
            Math.Max(0.5f, 1.4f * strich));
        g.DrawLine(stiftJetzt, jetztX, kasten.Y + 1, jetztX, kasten.Bottom - 1);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        // Zuerst die obenauf liegenden: der Umriss-Block ist der groesste und
        // wuerde sonst jeden Griff auf die kleineren Anzeigen abfangen.
        foreach (var part in new[]
                 { HudPart.Delta, HudPart.Ghost, HudPart.Note, HudPart.Inputs,
                   HudPart.CarNote, HudPart.Tyres, HudPart.LiveMap, HudPart.Course })
        {
            if (!_boxes.TryGetValue(part, out var kasten)) { continue; }
            if (!kasten.Contains(e.Location)) { continue; }
            Choose(part, klick: true);
            _dragging = part;
            _grab = new PointF(e.X - kasten.X, e.Y - kasten.Y);
            Cursor = Cursors.SizeAll;
            return;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging is null) { return; }
        if (!_boxes.TryGetValue(_dragging.Value, out var kasten)) { return; }

        var links = e.X - _grab.X;
        var oben = e.Y - _grab.Y;

        // Am Rand ist Schluss. Ein Kasten, der halb ueber die Kante gezogen wurde,
        // ist im Rennen abgeschnitten -- und das faellt erst im Rennen auf, wo es
        // niemand mehr richten kann.
        // Ist ein Kasten breiter als die Flaeche, bleibt er links oben stehen -- statt
        // dass Math.Clamp an einer Grenze unter der Untergrenze abstuerzt.
        links = Math.Clamp(links, _screen.X, Math.Max(_screen.X, _screen.Right - kasten.Width));
        oben = Math.Clamp(oben, _screen.Y, Math.Max(_screen.Y, _screen.Bottom - kasten.Height));

        var lage = Place(_dragging.Value);

        // Der Ankerpunkt haengt von der Ausrichtung ab: "center" misst die Mitte,
        // "right" die rechte Kante. Wer das verwechselt, laesst den Kasten beim
        // Loslassen springen.
        var anker = lage.Align.ToLowerInvariant() switch
        {
            "left" => links,
            "right" => links + kasten.Width,
            _ => links + kasten.Width / 2,
        };
        var x = (anker - _screen.X) / Math.Max(1f, _screen.Width);
        var y = (oben - _screen.Y) / Math.Max(1f, _screen.Height);

        // Sanft in die Mitte einrasten: fast jede Anzeige soll mittig sitzen, und
        // 0,4997 statt 0,5 ist auf dem Schirm zu sehen.
        if (Math.Abs(x - 0.5f) < 0.012f) { x = 0.5f; }

        SetPlace(_dragging.Value, x, y);
        Invalidate();
        Moved?.Invoke();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragging is null) { return; }
        _dragging = null;
        Cursor = Cursors.Hand;
        // Erst beim Loslassen auf die Platte schreiben, nicht bei jedem Pixel.
        Committed?.Invoke();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        var schritt = e.Delta > 0 ? 1.08f : 1f / 1.08f;
        var lage = Place(_selected);
        var neu = Math.Clamp(lage.Scale * schritt, 0.35f, 3.0f);
        _settings.SetScale(_selected, neu);
        Invalidate();
        Committed?.Invoke();
    }
}

/// <summary>Die Knoepfe rechts: je Stueck ein Abschnitt, dazu das Allgemeine.</summary>
/// <remarks>
/// SEIT 2026-09-28 EIN ABSCHNITT JE STUECK (Nutzerwunsch): Schalter, Anker, Groesse,
/// eigene Einstellungen und Farben stehen beieinander. Vorher lag alles in einer
/// langen Spalte, Anker und Groesse des gewaehlten Stuecks weit unten, die Farben
/// aller Stuecke gemischt am Ende -- ein Klick auf ein Stueck in der Vorschau
/// aenderte nur eine Ueberschrift, die man nicht sah.
///
/// Jetzt springt ein Klick auf ein Stueck zu seinem Abschnitt und hebt ihn in der
/// Farbe des Auswahlrahmens hervor. Jeder Abschnitt hat seine EIGENEN Knoepfe fuer
/// Anker und Groesse: so bleibt jeder Abschnitt gleich hoch, egal was gewaehlt ist,
/// und die Spalten springen beim Waehlen nicht umher.
/// </remarks>
internal sealed class HudPartPanel : Panel
{
    private readonly OverlaySettings _settings;
    private readonly FlowLayoutPanel _allgemeineFarben;
    private readonly ComboBox _layouts;
    private readonly ComboBox _reference;
    private readonly AbschnittSpalten _spalten;
    private readonly Dictionary<HudPart, FlowLayoutPanel> _abschnitte = new();
    private readonly Dictionary<HudPart, Label> _koepfe = new();
    private readonly Dictionary<HudPart, FlowLayoutPanel> _farben = new();
    private readonly Dictionary<HudPart, (ComboBox Anker, TrackBar Groesse)> _lage = new();
    private HudPart _part = HudPart.Delta;
    private bool _quiet;

    private static readonly Color Grund = Color.FromArgb(28, 33, 40);
    private static readonly Color Gewaehlt = Color.FromArgb(34, 46, 60);
    private static readonly Color Auswahlfarbe = Color.FromArgb(120, 200, 255);

    public event Action? Changed;
    /// <summary>Die Kartenquelle wurde umgestellt -- die Vorschau muss neu laden.</summary>
    public event Action? QuelleGeaendert;
    /// <summary>"Try it" bei der Feier gedrueckt.</summary>
    public event Action? FeierProbe;
    /// <summary>"Try it" bei der Meldung "neues Auto" gedrueckt.</summary>
    public event Action? NeuesAutoProbe;
    /// <summary>"Try it" bei den eigenen Rekorden gedrueckt.</summary>
    public event Action? PersoenlichProbe;
    /// <summary>"Open the recording window" gedrueckt.</summary>
    public event Action? AufnahmeFensterWunsch;

    /// <summary>Die Namen der Stuecke, wie sie ueber ihrem Abschnitt stehen.</summary>
    internal static string Titel(HudPart part) => part switch
    {
        HudPart.Ghost => Loc.T("Ghost countdown"),
        HudPart.Note => Loc.T("Note line"),
        HudPart.Inputs => Loc.T("Input traces"),
        HudPart.Course => Loc.T("Event Sign Up maps"),
        HudPart.CarNote => Loc.T("Car note"),
        HudPart.LiveMap => Loc.T("Live map"),
        HudPart.Tyres => Loc.T("Tyre overview"),
        _ => Loc.T("Delta"),
    };

    public HudPartPanel(OverlaySettings settings)
    {
        _settings = settings;
        BackColor = Grund;
        _spalten = new AbschnittSpalten { Dock = DockStyle.Fill, BackColor = Grund };

        // Wohin die naechsten Knoepfe kommen: der zuletzt begonnene Abschnitt.
        FlowLayoutPanel ziel = null!;
        void Rein(Control c) => ziel.Controls.Add(c);
        FlowLayoutPanel Abschnitt(string titel, HudPart? teil = null)
        {
            var a = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(12, 2, 12, 12),
                Margin = new Padding(0),
                BackColor = Grund,
            };
            var kopf = Head(titel);
            kopf.Margin = new Padding(0, 10, 0, 4);
            a.Controls.Add(kopf);
            if (teil is { } t)
            {
                _abschnitte[t] = a;
                _koepfe[t] = kopf;
                // Ein Klick irgendwo in den Abschnitt waehlt das Stueck nicht -- das
                // bleibt der Vorschau vorbehalten; hier wird nur eingestellt.
            }
            _spalten.Controls.Add(a);
            ziel = a;
            return a;
        }
        CheckBox Schalter(string text, bool an, Action<bool> setzen)
        {
            // UMBRECHEND: eine CheckBox mit AutoSize bricht nicht um, sie schneidet ab --
            // "Course maps on the Event Sign Up screen" endete bei "scr".
            var c = new CheckBox { Text = text, ForeColor = Color.Gainsboro, AutoSize = false,
                                   Width = 232, Checked = an, TextAlign = ContentAlignment.MiddleLeft };
            UmbruchHoehe(c);
            c.CheckedChanged += (_, _) => { if (_quiet) { return; } setzen(c.Checked); Changed?.Invoke(); };
            Rein(c);
            return c;
        }
        ComboBox Wahl(string text, (string Key, string Text)[] werte, string? jetzt, Action<string> setzen)
        {
            Rein(Note(text));
            var c = new ComboBox { Width = 232, DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (var (_, name) in werte) { c.Items.Add(name); }
            c.SelectedIndex = Math.Max(0, Array.FindIndex(werte, w =>
                string.Equals(w.Key, jetzt?.Trim(), StringComparison.OrdinalIgnoreCase)));
            c.SelectedIndexChanged += (_, _) =>
            {
                if (_quiet) { return; }
                setzen(werte[c.SelectedIndex].Key);
                Changed?.Invoke();
            };
            Rein(c);
            return c;
        }
        TrackBar Regler(string text, int von, int bis, int jetzt, Action<int> setzen)
        {
            Rein(Note(text));
            var r = new TrackBar
            {
                Width = 232, Minimum = von, Maximum = bis, TickFrequency = Math.Max(1, (bis - von) / 10),
                SmallChange = 1, LargeChange = Math.Max(1, (bis - von) / 10),
                Value = Math.Clamp(jetzt, von, bis),
            };
            r.ValueChanged += (_, _) =>
            {
                if (_quiet) { return; }
                setzen(r.Value);
                Changed?.Invoke();
            };
            Rein(r);
            return r;
        }
        NumericUpDown Zahl(double jetzt, double von, double bis, int stellen, decimal schritt, Action<double> setzen)
        {
            var n = new NumericUpDown
            {
                Width = 92, DecimalPlaces = stellen, Minimum = (decimal)von, Maximum = (decimal)bis,
                Increment = schritt, Value = (decimal)Math.Clamp(jetzt, von, bis),
            };
            n.ValueChanged += (_, _) =>
            {
                if (_quiet) { return; }
                setzen((double)n.Value);
                Changed?.Invoke();
            };
            Rein(n);
            return n;
        }
        // ANKER UND GROESSE, je Stueck eigene Knoepfe (siehe oben).
        void Lage(HudPart teil)
        {
            Rein(Note(Loc.T("Anchor")));
            var anker = new ComboBox { Width = 232, DropDownStyle = ComboBoxStyle.DropDownList };
            // Nach Stelle zugeordnet (0 links, 1 Mitte, 2 rechts) -- die Anzeige darf uebersetzt sein.
            anker.Items.AddRange(new object[] { Loc.T("Left edge"), Loc.T("Centre"), Loc.T("Right edge") });
            anker.SelectedIndexChanged += (_, _) =>
            {
                if (_quiet) { return; }
                var wert = anker.SelectedIndex switch { 0 => "left", 2 => "right", _ => "center" };
                _settings.SetAlign(teil, wert);
                Changed?.Invoke();
            };
            Rein(anker);
            Rein(Note(Loc.T("Size (mouse wheel works too)")));
            var groesse = new TrackBar
            {
                Width = 232, Minimum = 35, Maximum = 300, TickFrequency = 25,
                SmallChange = 5, LargeChange = 25,
            };
            groesse.ValueChanged += (_, _) =>
            {
                if (_quiet) { return; }
                // JEDES STUECK SEINE EIGENE GROESSE -- vorher landeten Umriss und
                // Autonotiz hier in "default" und verstellten die Delta-Zahl.
                _settings.SetScale(teil, groesse.Value / 100.0);
                Changed?.Invoke();
            };
            Rein(groesse);
            _lage[teil] = (anker, groesse);
        }
        FlowLayoutPanel Farben(HudPart? teil)
        {
            Rein(Note(Loc.T("Colours")));
            var f = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Width = 236, Margin = new Padding(0),
            };
            if (teil is { } t) { _farben[t] = f; }
            Rein(f);
            return f;
        }

        // ---- ANORDNUNGEN ------------------------------------------------------------
        Abschnitt(Loc.T("Layout"));
        _layouts = new ComboBox { Width = 232, DropDownStyle = ComboBoxStyle.DropDown };
        RefreshLayouts();
        Rein(_layouts);
        // UMBRECHEND (seit 2026-09-29): jeder Knopf ist so breit wie sein Wort (Small), und
        // drei lange Woerter ("Enregistrer", "Αποθήκευση") passen nicht nebeneinander in die
        // Spalte -- dann rutscht der letzte in eine zweite Reihe, statt ueber den Rand.
        var knoepfe = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight, WrapContents = true,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MaximumSize = new Size(236, 0), Margin = new Padding(0, 4, 0, 0),
        };
        knoepfe.Controls.Add(Small(Loc.T("Save"), () =>
        {
            var name = _layouts.Text.Trim();
            if (name.Length == 0)
            {
                MessageBox.Show(this, Loc.T("Type a name for the layout first."),
                                Loc.T("Save layout"));
                return;
            }
            if (!_settings.SaveLayout(name))
            {
                MessageBox.Show(this, Loc.T("The layout could not be written."), Loc.T("Save layout"));
                return;
            }
            RefreshLayouts();
            _layouts.Text = name;
        }));
        knoepfe.Controls.Add(Small(Loc.T("Load"), () =>
        {
            var name = _layouts.Text.Trim();
            if (name.Length == 0) { return; }
            if (!_settings.LoadLayout(name))
            {
                MessageBox.Show(this, string.Format(Loc.T("No layout called \"{0}\"."), name), Loc.T("Load layout"));
                return;
            }
            Select(_part);
            Changed?.Invoke();
        }));
        knoepfe.Controls.Add(Small(Loc.T("Delete"), () =>
        {
            var name = _layouts.Text.Trim();
            if (name.Length == 0) { return; }
            if (MessageBox.Show(this, string.Format(Loc.T("Delete the layout \"{0}\"?"), name), Loc.T("Delete layout"),
                                MessageBoxButtons.YesNo) != DialogResult.Yes)
            {
                return;
            }
            _settings.DeleteLayout(name);
            RefreshLayouts();
        }));
        // Luft unter jedem Knopf, falls die Reihe umbricht.
        foreach (Control k in knoepfe.Controls) { k.Margin = new Padding(0, 0, 3, 3); }
        Rein(knoepfe);
        Rein(Note(Loc.T(
            "Position, size and colours are stored under a name. Hotkeys and screen regions are not part of it. Click a block in the picture to jump to its settings; drag the divider on the left to widen this side.")));

        // ---- DELTA --------------------------------------------------------------------
        Abschnitt(Titel(HudPart.Delta), HudPart.Delta);
        Schalter(Loc.T("Show the strip while racing"), settings.DeltaHud, v => _settings.DeltaHud = v);
        Lage(HudPart.Delta);
        Rein(Note(Loc.T("What it compares against")));
        var referenz = new ComboBox { Width = 232, DropDownStyle = ComboBoxStyle.DropDownList };
        _reference = referenz;
        foreach (var (_, text) in References) { referenz.Items.Add(text()); }
        referenz.SelectedIndex = Math.Max(0, Array.FindIndex(
            References, r => string.Equals(r.Key, settings.DeltaReferenceMode,
                                           StringComparison.OrdinalIgnoreCase)));
        referenz.SelectedIndexChanged += (_, _) =>
        {
            if (_quiet) { return; }
            _settings.DeltaReferenceMode = References[referenz.SelectedIndex].Key;
            Changed?.Invoke();
        };
        Rein(referenz);
        Rein(Note(Loc.T("Switch this while racing")));
        var taste = new ComboBox { Width = 232, DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var (_, name) in Keys) { taste.Items.Add(name); }
        taste.SelectedIndex = Math.Max(0, Array.FindIndex(Keys, k => k.Vk == settings.HotkeyDeltaMode));
        taste.SelectedIndexChanged += (_, _) =>
        {
            if (_quiet) { return; }
            _settings.HotkeyDeltaMode = Keys[taste.SelectedIndex].Vk;
            Changed?.Invoke();
        };
        Rein(taste);
        Rein(Note(Loc.T(
            "Each press takes the next step, from the narrowest to the widest, and says so on screen. The key only does this while Forza is running.")));
        Schalter(Loc.T("Show the time to beat for the website's leaderboard"), settings.HudTarget,
                 v => _settings.HudTarget = v);
        Rein(Note(Loc.T(
            "A line under the delta: your car's best time on the website for this route and class. Beat it and the lap goes onto the leaderboard.")));
        Farben(HudPart.Delta);

        // ---- GEISTER-COUNTDOWN ------------------------------------------------------
        Abschnitt(Titel(HudPart.Ghost), HudPart.Ghost);
        Lage(HudPart.Ghost);
        Rein(Note(Loc.T("No-contact start: seconds the ghost lasts after GO")));
        Zahl(settings.GhostSeconds, 0, 120, 1, 0.5m, v => _settings.GhostSeconds = v);
        Rein(Note(Loc.T(
            "Counted down in blue. Telemetry does not carry it, so correct it here if the countdown ends at the wrong moment.")));
        Rein(Note(Loc.T("Pop-in phase (seconds)")));
        Zahl(settings.PopInSeconds, 0, 60, 1, 0.5m, v => _settings.PopInSeconds = v);
        Rein(Note(Loc.T(
            "Counts UP once the ghost ends -- the seconds the others take to pop back in, one after another.")));
        Rein(Note(Loc.T("Warn this many seconds before the ghost ends")));
        Zahl(settings.GhostWarnSeconds, 0, 60, 1, 0.5m, v => _settings.GhostWarnSeconds = v);
        Rein(Note(Loc.T(
            "The countdown colours its BACKGROUND, not its digits: calm, then warning, then pop-in. The text colour is yours to pick.")));
        Farben(HudPart.Ghost);

        // ---- EINGABESPUREN ------------------------------------------------------------
        Abschnitt(Titel(HudPart.Inputs), HudPart.Inputs);
        // UMBRECHEND wie Schalter(): mit AutoSize lief der Text in langen Sprachen ueber die
        // Spalte hinaus und lag unter der naechsten -- auch mit Rollen nicht zu lesen.
        var spuren = new CheckBox
        {
            Text = Loc.T("Show throttle, brake, clutch, steering, gear"),
            ForeColor = Color.Gainsboro, AutoSize = false, Width = 232,
            TextAlign = ContentAlignment.MiddleLeft,
            Checked = settings.HudInputs,
        };
        UmbruchHoehe(spuren);
        spuren.CheckedChanged += (_, _) =>
        {
            if (_quiet) { return; }
            _settings.HudInputs = spuren.Checked;
            Changed?.Invoke();
        };
        Rein(spuren);
        Lage(HudPart.Inputs);
        Rein(Note(Loc.T("Seconds of history behind the now line")));
        Zahl(settings.HudInputsSeconds, 1, 60, 0, 1m, v => _settings.HudInputsSeconds = v);
        Rein(Note(Loc.T("Seconds of the reference lap shown ahead")));
        Zahl(settings.HudInputsLookahead, 0, 10, 1, 0.5m, v => _settings.HudInputsLookahead = v);
        Rein(Note(Loc.T(
            "Shown AHEAD of the now line, on the shaded side -- what the reference is about to do, so you can copy it. Your own line stops at now: it has no future to show. The reference is read at the place you are, not at the same clock time.")));
        Rein(Note(Loc.T("Line thickness")));
        var dicke = new TrackBar
        {
            Width = 232, Minimum = 20, Maximum = 600, TickFrequency = 50,
            SmallChange = 10, LargeChange = 50,
            Value = (int)Math.Clamp(settings.HudInputsLineWidth * 100, 20, 600),
        };
        dicke.ValueChanged += (_, _) =>
        {
            if (_quiet) { return; }
            _settings.HudInputsLineWidth = dicke.Value / 100.0;
            Changed?.Invoke();
        };
        Rein(dicke);
        Farben(HudPart.Inputs);

        // ---- NOTIZZEILE ---------------------------------------------------------------
        Abschnitt(Titel(HudPart.Note), HudPart.Note);
        Lage(HudPart.Note);
        Rein(Note(Loc.T(
            "Short messages under the strip, for example when a lap was stored. It uses the delta colours.")));

        // ---- REIFENUEBERSICHT (seit 2026-09-28, ab Werk aus) ------------------------
        Abschnitt(Titel(HudPart.Tyres), HudPart.Tyres);
        Schalter(Loc.T("Tyre overview while driving"), settings.HudTyres, v => _settings.HudTyres = v);
        Lage(HudPart.Tyres);
        Rein(Note(Loc.T(
            "All four tyres at a glance: colour is temperature, the outline turns yellow at the limit and red when the tyre slides, and the tyre tilts with its slip angle. The bars show wheelspin or locking and the suspension travel; icons show puddles, kerbs and bumpy ground.")));
        Schalter(Loc.T("Tyre temperature in Fahrenheit"), settings.TyresFahrenheit,
                 v => _settings.TyresFahrenheit = v);

        // ---- LIVE-KARTE ---------------------------------------------------------------
        Abschnitt(Titel(HudPart.LiveMap), HudPart.LiveMap);
        Schalter(Loc.T("Live map during the race"), settings.LiveMap, v => _settings.LiveMap = v);
        Lage(HudPart.LiveMap);
        Rein(Note(Loc.T(
            "One map of the course you are driving, with your car on it. It is drawn from your own laps -- only they know where on the map the car is. On a course you have not driven yet it grows as you drive.")));
        Regler(Loc.T("Live map: line thickness"), 5, 100, (int)Math.Round(settings.LiveMapWidth * 10),
               v => _settings.LiveMapWidth = v / 10.0);
        Regler(Loc.T("Live map: smoothing"), 0, 100, settings.LiveMapSmooth,
               v => _settings.LiveMapSmooth = v);
        Farben(HudPart.LiveMap);

        // ---- KARTEN DER ANMELDUNG -----------------------------------------------------
        Abschnitt(Titel(HudPart.Course), HudPart.Course);
        Schalter(Loc.T("Course maps on the Event Sign Up screen"), settings.CourseShapes,
                 v => _settings.CourseShapes = v);
        Lage(HudPart.Course);
        Rein(Note(Loc.T("Before the race: one map per offered route, until you pick a car.")));
        Rein(Note(Loc.T("Where the sign-up maps come from")));
        var quelle = new ComboBox { Width = 232, DropDownStyle = ComboBoxStyle.DropDownList };
        var quellen = new[] { ("auto", Loc.T("Automatic: your laps, else Rivals")),
                              ("telemetry", Loc.T("Your laps only (telemetry)")),
                              ("rivals", Loc.T("Rivals maps only")) };
        foreach (var (_, name) in quellen) { quelle.Items.Add(name); }
        quelle.SelectedIndex = Math.Max(0, Array.FindIndex(quellen, q =>
            string.Equals(q.Item1, settings.CourseShapeSource, StringComparison.OrdinalIgnoreCase)));
        quelle.SelectedIndexChanged += (_, _) =>
        {
            if (_quiet) { return; }
            _settings.CourseShapeSource = quellen[quelle.SelectedIndex].Item1;
            QuelleGeaendert?.Invoke();
            Changed?.Invoke();
        };
        Rein(quelle);
        Rein(Note(Loc.T(
            "Your laps are drawn from where you actually drove; the Rivals maps are the game's own drawings, for routes you have never driven.")));
        // WIE LANGE DIE ANMELDEKARTEN STEHEN -- wie beim Autovorschlag (Nutzerwunsch
        // vom 2026-09-26). 0 heisst: bis das Rennen beginnt.
        Rein(Note(Loc.T("Sign-up maps disappear after (seconds, 0 = when the race starts)")));
        Zahl(settings.CourseShapeSeconds, 0, 600, 0, 5m, v => _settings.CourseShapeSeconds = v);
        Wahl(Loc.T("Rivals maps are shown as"),
             new[] { ("line", Loc.T("Smooth traced line")),
                     ("image", Loc.T("The game's map picture")) },
             settings.CourseRivalsStyle, v => _settings.CourseRivalsStyle = v);
        Wahl(Loc.T("Sign-up maps arranged"),
             new[] { ("horizontal", Loc.T("Side by side")), ("vertical", Loc.T("Stacked")) },
             settings.CourseShapeLayout, v => _settings.CourseShapeLayout = v);
        // Staerke in Zehntelpunkten: 0,5 bis 10 Bildpunkte bei 1080p.
        Regler(Loc.T("Sign-up maps: line thickness"), 5, 100, (int)Math.Round(settings.CourseShapeWidth * 10),
               v => _settings.CourseShapeWidth = v / 10.0);
        Regler(Loc.T("Sign-up maps: smoothing"), 0, 100, settings.CourseShapeSmooth,
               v => _settings.CourseShapeSmooth = v);
        Rein(Note(Loc.T("Smoothing takes out the pixel steps of the line. Turned up high, tight hairpins get rounder too.")));
        Farben(HudPart.Course);

        // ---- AUTONOTIZ ----------------------------------------------------------------
        Abschnitt(Titel(HudPart.CarNote), HudPart.CarNote);
        Schalter(Loc.T("Car note"), settings.CarNotes, v => _settings.CarNotes = v);
        Lage(HudPart.CarNote);
        Schalter(Loc.T("Show the applied tune's name and description in the car note"), settings.CarNoteTune,
                 v => _settings.CarNoteTune = v);
        Farben(HudPart.CarNote);

        // ---- FEIERN (seit 2026-09-27): ab Werk an, hier abzuschalten -- und zum
        // Ausprobieren, ohne erst eine Rekordrunde fahren zu muessen.
        Abschnitt(Loc.T("Celebrations"));
        Schalter(Loc.T("Celebrate when a lap beats the website's time"), settings.CelebrateRecord,
                 v => _settings.CelebrateRecord = v);
        Schalter(Loc.T("Play a sound with it"), settings.CelebrateSound, v => _settings.CelebrateSound = v);
        var probe = Small(Loc.T("Try it"), () => FeierProbe?.Invoke(), mindestens: 110);
        probe.Margin = new Padding(0, 2, 0, 4);
        Rein(probe);
        Rein(Note(Loc.T(
            "A card with your time, confetti and a short sound, near the top of the screen for about five seconds.")));
        Schalter(Loc.T("Say thanks when your lap adds a new car to the leaderboard"), settings.CelebrateNewCar,
                 v => _settings.CelebrateNewCar = v);
        var probeNeu = Small(Loc.T("Try it"), () => NeuesAutoProbe?.Invoke(), mindestens: 110);
        probeNeu.Margin = new Padding(0, 2, 0, 4);
        Rein(probeNeu);
        Rein(Note(Loc.T(
            "Calmer, in teal: when the server accepts a lap of a car that was not on that route and class board yet.")));

        // ---- EIGENE REKORDE (seit 2026-09-28): die eigene Bestenliste, klein gefeiert.
        Abschnitt(Loc.T("Personal records"));
        Rein(Note(Loc.T(
            "Your own laps decide, not the website. Smaller and shorter than the website celebration: green for a personal best, blue for a new car on your list.")));
        Schalter(Loc.T("Personal best in a class on a course"), settings.PbClassRecord, v => _settings.PbClassRecord = v);
        Schalter(Loc.T("Personal best with a car (every car, class and course)"), settings.PbCarRecord,
                 v => _settings.PbCarRecord = v);
        Schalter(Loc.T("A car's first time on your list for a course and class"), settings.PbNewCar,
                 v => _settings.PbNewCar = v);
        Schalter(Loc.T("Your first lap in a class on a course"), settings.PbFirstInClass,
                 v => _settings.PbFirstInClass = v);
        Schalter(Loc.T("Separate records per mode"), settings.PbPerMode, v => _settings.PbPerMode = v);
        Rein(Note(Loc.T(
            "Rivals, Horizon Play, races and free roam each keep their own records, so a wall-riding lap never beats a Rivals best. Laps from before the mode was recorded count for every mode.")));
        Schalter(Loc.T("Play a short sound with them"), settings.PbSound, v => _settings.PbSound = v);
        var probePb = Small(Loc.T("Try it"), () => PersoenlichProbe?.Invoke(), mindestens: 110);
        probePb.Margin = new Padding(0, 2, 0, 4);
        Rein(probePb);
        Rein(Note(Loc.T(
            "Compared are laps on the same course, in the same class and with the same start (standing or flying). Each lap shows at most one: a class best before a new car before a car best.")));

        // ---- FARBEN FUER ALLE --------------------------------------------------------
        Abschnitt(Loc.T("Shared colours"));
        _allgemeineFarben = Farben(null);
        Rein(Note(Loc.T(
            "The small labels and the backing plate behind the strip, the note line, the input traces and the tyre overview.")));

        // ---- AUFNAHME UND STREAM (seit 2026-09-28): die Overlays halten sich aus
        // Aufnahmen heraus; das Aufnahmefenster zeigt sie fuer OBS. Und wer ganz sicher
        // gehen will, dass nichts ueber dem Spiel liegt, schaltet sie dort ab.
        Abschnitt(Loc.T("Recording and streaming"));
        Schalter(Loc.T("Show overlays over the game"), settings.OverlayInGame, v =>
        {
            _settings.OverlayInGame = v;
            OverlayAusgabe.SetzeImSpiel(v);
        });
        Rein(Note(Loc.T(
            "Off: nothing is drawn over Forza, so the overlays cannot affect its frames. The recording window still shows them -- for example on a second screen.")));
        var fenster = Small(Loc.T("Open the recording window"), () => AufnahmeFensterWunsch?.Invoke(), mindestens: 232);
        fenster.Margin = new Padding(0, 4, 0, 4);
        Rein(fenster);
        Wahl(Loc.T("Key colour for OBS"),
             new[] { ("green", Loc.T("Green")), ("magenta", Loc.T("Magenta")), ("black", Loc.T("Black")) },
             settings.RecordingKey, v => _settings.RecordingKey = v);
        Rein(Note(Loc.T(
            "OBS: add a Window Capture of this window above the game capture, then a Color Key filter in the same colour. Keep the window open -- on another screen or behind the game, not minimised.")));

        // ---- AUFZEICHNUNG -------------------------------------------------------------
        Abschnitt(Loc.T("Recording"));
        var merken = new CheckBox
        {
            Text = Loc.T("Archive every lap for heatmaps"),
            ForeColor = Color.Gainsboro, AutoSize = false, Width = 232,
            TextAlign = ContentAlignment.MiddleLeft,
            Checked = settings.ArchiveLaps,
        };
        UmbruchHoehe(merken);
        merken.CheckedChanged += (_, _) =>
        {
            if (_quiet) { return; }
            _settings.ArchiveLaps = merken.Checked;
            Changed?.Invoke();
        };
        Rein(merken);
        var zettel = new TextBox { Width = 232, Text = settings.LapTag };
        zettel.TextChanged += (_, _) =>
        {
            if (_quiet) { return; }
            _settings.LapTag = zettel.Text;
            Changed?.Invoke();
        };
        Rein(zettel);
        Rein(Note(Loc.T(
            "Tag written with every recorded lap -- it becomes a folder, so \"wet\" or \"tune-b\" keeps those laps apart. Laps are split by course, PI class, car and tune on their own.")));
        // DER MODUS. Zum Filtern spaeter, nicht zum Vergleichen -- eine
        // Rivals-Runde gegen eine Koop-Runde zu stellen waere so falsch wie
        // stehend gegen fliegend.
        var modus = new ComboBox { Width = 232, DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var m in new[] { "auto", "rivals", "solo", "coop", "horizon-play", "freeroam" })
        {
            modus.Items.Add(m);
        }
        // Die Eintraege bleiben Kennungen -- sie landen so in jeder Runde und in
        // den Einstellungen. Nur die Anzeige wird uebersetzt.
        modus.FormattingEnabled = true;
        modus.Format += (_, e) => e.Value = OwnTimes.ModeText(e.ListItem as string);
        modus.SelectedItem = modus.Items.Contains(settings.LapMode ?? "auto")
            ? settings.LapMode : "auto";
        modus.SelectedIndexChanged += (_, _) =>
        {
            if (_quiet) { return; }
            _settings.LapMode = modus.SelectedItem as string ?? "auto";
            Changed?.Invoke();
        };
        Rein(modus);
        // "automatic" wie der Eintrag in der Liste (OwnTimes.ModeText) -- nicht die Kennung "auto".
        Rein(Note(Loc.T(
            "Which mode you are playing, written with every lap. \"automatic\" works it out from the menu you came from: the Rivals screen, a Horizon Play sign-up, or an ordinary sign-up (a solo or co-op race). Free-roam runs prove themselves by their own clock. Only Rivals and Horizon Play laps count on the website; a lap whose mode is unknown is not sent.")));

        // ---- ZEITFAHREN IN DER OFFENEN WELT ------------------------------------------
        Abschnitt(Loc.T("Time attack in the free world"));
        Rein(Note(Loc.T(
            "Forza does not run a clock outside a race -- this app does. Every route you have driven already has a start line, so you can practise one in free roam and the strip works as it does in a race. Custom routes from EventLab count too: a course is recognised by where its line is, never by a name.")));
        var linie = new ComboBox { Width = 232, DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var (_, name) in Keys) { linie.Items.Add(name); }
        linie.SelectedIndex = Math.Max(0, Array.FindIndex(
            Keys, k => k.Vk == settings.HotkeyFreeRoamLine));
        linie.SelectedIndexChanged += (_, _) =>
        {
            if (_quiet) { return; }
            _settings.HotkeyFreeRoamLine = Keys[linie.SelectedIndex].Vk;
            Changed?.Invoke();
        };
        Rein(linie);
        Rein(Note(Loc.T(
            "Press this where you want a start line of your own -- a back road, a pass, a circuit nobody built. Drive over it again and you have a time. Free-roam times are kept apart from race times: out here there are no track limits.")));

        BuildColours();
        Controls.Add(_spalten);
    }

    /// <summary>
    /// Die Tasten, die zur Auswahl stehen.
    /// </summary>
    /// <remarks>
    /// Nur Funktionstasten: alles andere ist im Spiel belegt, und eine Taste, die
    /// beim Umschalten zugleich den Gang wechselt, ist schlimmer als keine.
    /// </remarks>
    private static readonly (int Vk, string Name)[] Keys =
    {
        (112, "F1"), (113, "F2"), (114, "F3"), (115, "F4"), (116, "F5"), (117, "F6"),
        (118, "F7"), (119, "F8"), (120, "F9"), (121, "F10"), (122, "F11"), (123, "F12"),
    };

    /// <summary>Die Stufen des Vergleichs: Kennung (gespeichert, von der Taste geschaltet) und Anzeige.</summary>
    /// <remarks>
    /// Die Anzeige als Funktion: uebersetzt wird beim Fuellen der Liste, nicht einmal beim
    /// ersten Zugriff auf die Klasse -- die Sprache kann danach noch wechseln.
    /// </remarks>
    internal static readonly (string Key, Func<string> Text)[] References =
    {
        ("tune", () => Loc.T("Same car, same tune")),
        ("car", () => Loc.T("Same car, any tune")),
        ("class", () => Loc.T("Same PI class, any car")),
        ("carclass", () => Loc.T("Same car in this PI class")),
        ("any", () => Loc.T("My best here with anything")),
        ("dual", () => Loc.T("Two figures: same car, and same PI class")),
    };

    /// <summary>
    /// Die Auswahlliste nachziehen, nachdem im Spiel umgeschaltet wurde.
    /// </summary>
    /// <remarks>
    /// Ohne das stuende hier eine Stufe, die seit drei Tastendruecken nicht mehr
    /// gilt -- und die naechste Aenderung an irgendetwas anderem wuerde sie
    /// zurueckschreiben.
    /// </remarks>
    public void RefreshMode()
    {
        _quiet = true;
        var i = Array.FindIndex(References, r => string.Equals(
            r.Key, _settings.DeltaReferenceMode, StringComparison.OrdinalIgnoreCase));
        if (i >= 0 && _reference.SelectedIndex != i) { _reference.SelectedIndex = i; }
        _quiet = false;
    }

    /// <summary>Anker und Groesse aller Stuecke aus den Einstellungen nachziehen.</summary>
    /// <remarks>Nach dem Mausrad in der Vorschau und nach dem Laden einer Anordnung.</remarks>
    public void RefreshPlacements()
    {
        var vorher = _quiet;
        _quiet = true;
        foreach (var (teil, (anker, groesse)) in _lage)
        {
            var lage = _settings.PlacementOf(teil);
            var index = lage.Align.ToLowerInvariant() switch { "left" => 0, "right" => 2, _ => 1 };
            if (anker.SelectedIndex != index) { anker.SelectedIndex = index; }
            var wert = (int)Math.Clamp(lage.Scale * 100f, groesse.Minimum, groesse.Maximum);
            if (groesse.Value != wert) { groesse.Value = wert; }
        }
        _quiet = vorher;
    }

    /// <summary>Den Abschnitt eines Stuecks hervorheben -- und auf Wunsch hinspringen.</summary>
    /// <param name="springen">True bei einem Klick in der Vorschau: der Abschnitt
    /// kommt in den Blick, falls er es nicht schon ganz ist.</param>
    public void Select(HudPart part, bool springen = false)
    {
        _part = part;
        _quiet = true;
        RefreshPlacements();
        // Nach dem Laden einer Anordnung stimmen sonst die Farbknoepfe nicht mehr
        // mit dem ueberein, was gezeichnet wird.
        BuildColours();
        foreach (var (teil, abschnitt) in _abschnitte)
        {
            var an = teil == part;
            abschnitt.BackColor = an ? Gewaehlt : Grund;
            if (_koepfe.TryGetValue(teil, out var kopf)) { kopf.ForeColor = an ? Auswahlfarbe : Color.White; }
        }
        _quiet = false;
        if (springen && _abschnitte.TryGetValue(part, out var ziel)) { _spalten.Zeige(ziel); }
    }

    /// <summary>Welcher Abschnitt zu einem Stueck gehoert (fuer den Grenzfalltest).</summary>
    internal Control? AbschnittVon(HudPart part) => _abschnitte.GetValueOrDefault(part);

    /// <summary>Wie weit die Abschnitte gerade gerollt sind (fuer den Grenzfalltest).</summary>
    internal AbschnittSpalten Spalten => _spalten;

    private void BuildColours()
    {
        foreach (var f in _farben.Values) { f.Controls.Clear(); }
        _allgemeineFarben.Controls.Clear();

        var delta = _farben[HudPart.Delta];
        Add(delta, Loc.T("Ahead of the reference"), () => _settings.HudColorAhead,
            v => _settings.HudColorAhead = v);
        Add(delta, Loc.T("Behind the reference"), () => _settings.HudColorBehind,
            v => _settings.HudColorBehind = v);
        Add(delta, Loc.T("No reference yet"), () => _settings.HudColorNeutral,
            v => _settings.HudColorNeutral = v);

        var geist = _farben[HudPart.Ghost];
        Add(geist, Loc.T("Ghost, calm phase"), () => _settings.HudColorGhost,
            v => _settings.HudColorGhost = v);
        Add(geist, Loc.T("Ghost, warning phase"), () => _settings.HudColorGhostWarn,
            v => _settings.HudColorGhostWarn = v);
        Add(geist, Loc.T("Pop-in phase"), () => _settings.HudColorPopIn,
            v => _settings.HudColorPopIn = v);
        Add(geist, Loc.T("Ghost text"), () => _settings.HudColorGhostText,
            v => _settings.HudColorGhostText = v);

        var spuren = _farben[HudPart.Inputs];
        Add(spuren, Loc.T("My inputs"), () => _settings.HudColorMine,
            v => _settings.HudColorMine = v);
        Add(spuren, Loc.T("Reference inputs"), () => _settings.HudColorTheirs,
            v => _settings.HudColorTheirs = v);
        Add(spuren, Loc.T("Now line"), () => _settings.HudColorNow,
            v => _settings.HudColorNow = v);

        Add(_allgemeineFarben, Loc.T("Small label"), () => _settings.HudColorLabel,
            v => _settings.HudColorLabel = v);
        Add(_allgemeineFarben, Loc.T("Backing plate"), () => _settings.HudBackground,
            v => _settings.HudBackground = v, alpha: true);

        // Der Umriss-Block hat eigene Farben: er liegt auf dem MENUE, nicht auf
        // der Strasse, und was dort lesbar ist, ist hier oft zu blass.
        var kurs = _farben[HudPart.Course];
        Add(kurs, Loc.T("Course outline"), () => _settings.CourseShapeLine,
            v => _settings.CourseShapeLine = v);
        Add(kurs, Loc.T("Course start point"), () => _settings.CourseShapeStart,
            v => _settings.CourseShapeStart = v);
        Add(kurs, Loc.T("Course plate"), () => _settings.CourseShapeBack,
            v => _settings.CourseShapeBack = v, alpha: true);

        var notiz = _farben[HudPart.CarNote];
        Add(notiz, Loc.T("Car note text"), () => _settings.CarNoteInk,
            v => _settings.CarNoteInk = v);
        Add(notiz, Loc.T("Car note heading"), () => _settings.CarNoteTitle,
            v => _settings.CarNoteTitle = v);
        Add(notiz, Loc.T("Car note plate"), () => _settings.CarNoteBack,
            v => _settings.CarNoteBack = v, alpha: true);

        // Die Live-Karte teilt Linie, Start und Platte mit den Umrissen (dieselbe Art
        // Bild); eigen sind nur das Auto und der schon gefahrene Teil.
        var karte = _farben[HudPart.LiveMap];
        Add(karte, Loc.T("Live map: your car"), () => _settings.LiveMapCar,
            v => _settings.LiveMapCar = v);
        Add(karte, Loc.T("Live map: driven part"), () => _settings.LiveMapTrail,
            v => _settings.LiveMapTrail = v);
    }

    private void Add(FlowLayoutPanel ziel, string text, Func<string> get, Action<string> set, bool alpha = false)
    {
        var zeile = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            Margin = new Padding(0, 2, 0, 2),
        };
        var knopf = new Button
        {
            Width = 34, Height = 22, FlatStyle = FlatStyle.Flat,
            BackColor = OverlaySettings.ParseColour(get(), Color.Gray),
            Text = string.Empty,
        };
        knopf.FlatAppearance.BorderColor = Color.FromArgb(90, 100, 112);
        // UMBRECHEND (seit 2026-09-29): Knopf 3 + 34 + 3, Abstand 6, Name hoechstens 186 --
        // zusammen 232 wie jede Notiz. Ohne Grenze lief ein langer uebersetzter Name ueber
        // die Spalte hinaus.
        var name = new Label
        {
            Text = text, ForeColor = Color.Gainsboro, AutoSize = true,
            MaximumSize = new Size(186, 0),
            Margin = new Padding(6, 5, 0, 0),
        };
        knopf.Click += (_, _) =>
        {
            using var dialog = new ColorDialog
            {
                Color = OverlaySettings.ParseColour(get(), Color.Gray),
                FullOpen = true,
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) { return; }
            // Die Deckkraft der Platte bleibt erhalten -- der Farbwaehler kennt sie
            // nicht, und eine undurchsichtige Platte verdeckt die Strecke.
            var alt = OverlaySettings.ParseColour(get(), Color.Gray);
            var neu = alpha
                ? Color.FromArgb(alt.A, dialog.Color)
                : dialog.Color;
            set(OverlaySettings.ToHex(neu));
            knopf.BackColor = neu;
            Changed?.Invoke();
        };
        zeile.Controls.Add(knopf);
        zeile.Controls.Add(name);

        if (alpha)
        {
            var deckung = new TrackBar
            {
                Width = 232, Minimum = 0, Maximum = 255, TickFrequency = 32,
                Value = OverlaySettings.ParseColour(get(), Color.Gray).A,
            };
            deckung.ValueChanged += (_, _) =>
            {
                var farbe = OverlaySettings.ParseColour(get(), Color.Gray);
                set(OverlaySettings.ToHex(Color.FromArgb(deckung.Value, farbe)));
                Changed?.Invoke();
            };
            ziel.Controls.Add(zeile);
            // Mit Doppelpunkt statt angehaengtem Wort: so muss keine Sprache den Namen beugen.
            ziel.Controls.Add(Note(string.Format(Loc.T("{0}: opacity"), text)));
            ziel.Controls.Add(deckung);
            return;
        }
        ziel.Controls.Add(zeile);
    }

    private void RefreshLayouts()
    {
        var stand = _layouts.Text;
        _layouts.Items.Clear();
        foreach (var name in OverlaySettings.LayoutNames()) { _layouts.Items.Add(name); }
        _layouts.Text = stand;
    }

    /// <summary>Ein kleiner Knopf: mindestens <paramref name="mindestens"/> breit, sonst so breit wie sein Text.</summary>
    /// <remarks>
    /// SO BREIT WIE SEIN TEXT (seit 2026-09-29): fest 74 passte nur zum Englischen --
    /// "Enregistrer" oder "Αποθήκευση" wurden abgeschnitten. Hoechstens 232, die Breite der
    /// Spalte. Neu gemessen, sobald die Schrift des Fensters ankommt (10 pt statt 9 pt).
    /// </remarks>
    private static Button Small(string text, Action tun, int mindestens = 74)
    {
        var knopf = new Button
        {
            Text = text, Width = mindestens, Height = 26, FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(38, 44, 52), ForeColor = Color.WhiteSmoke,
            Margin = new Padding(0, 0, 3, 0),
        };
        knopf.FlatAppearance.BorderColor = Color.FromArgb(70, 80, 92);
        knopf.Click += (_, _) => tun();
        void Breite() => knopf.Width = Math.Max(mindestens,
            Math.Min(232, TextRenderer.MeasureText(knopf.Text, knopf.Font).Width + 16));
        Breite();
        knopf.FontChanged += (_, _) => Breite();
        knopf.TextChanged += (_, _) => Breite();
        return knopf;
    }

    /// <summary>
    /// So breit darf der Text neben dem Kaestchen einer 232 breiten CheckBox sein. 210 war
    /// zu knapp: "Mostrar la franja durante la carrera" mass eine Zeile, stand aber in zwei
    /// -- und die zweite war abgeschnitten.
    /// </summary>
    internal const int HakenText = 232 - 34;

    /// <summary>
    /// Eine umbrechende CheckBox so hoch wie ihr Text -- und NEU, sobald sich Schrift oder
    /// Breite aendern (seit 2026-09-29). Gemessen wurde beim Anlegen mit der Vorgabeschrift
    /// (9 pt); im Hauptfenster erbt sie Segoe UI 10 pt, die zweite Zeile war abgeschnitten.
    /// </summary>
    internal static void UmbruchHoehe(CheckBox c)
    {
        var breite = -1;
        void Neu()
        {
            breite = c.Width;
            c.Height = TextRenderer.MeasureText(c.Text, c.Font, new Size(Math.Max(40, c.Width - 34), 0),
                                                TextFormatFlags.WordBreak).Height + 8;
        }
        Neu();
        c.FontChanged += (_, _) => Neu();
        c.TextChanged += (_, _) => Neu();
        c.SizeChanged += (_, _) => { if (c.Width != breite) { Neu(); } };
    }

    private static Label Head(string text) => new()
    {
        Text = text,
        ForeColor = Color.White,
        Font = new Font("Segoe UI Semibold", 9.5f),
        AutoSize = true,
        // Ein langer uebersetzter Titel bricht um, statt ueber die Spalte zu laufen.
        MaximumSize = new Size(232, 0),
        Margin = new Padding(0, 12, 0, 4),
    };

    private static Label Note(string text) => new()
    {
        Text = text,
        ForeColor = Color.FromArgb(147, 162, 181),
        AutoSize = true,
        MaximumSize = new Size(232, 0),
        Margin = new Padding(0, 4, 0, 2),
    };
}

/// <summary>Die Abschnitte rechts, in so vielen Spalten, wie die Breite hergibt.</summary>
/// <remarks>
/// Seit 2026-09-28 (Nutzerwunsch: "resizeable so you can fully display it and not just
/// scroll it"). Jeder Abschnitt kommt in die gerade kuerzeste Spalte; weil jeder
/// Abschnitt immer gleich hoch ist, steht er bei gleicher Breite immer am selben Platz.
/// </remarks>
internal sealed class AbschnittSpalten : Panel
{
    // 268: jeder Abschnitt ist 262 breit (Farbzeile 236 plus Rand) -- bei 258 lagen die
    // Spalten 4 Punkte uebereinander, und eine Rollleiste zur Seite erschien ohne Grund.
    public const int SpaltenBreite = 268;
    private bool _ordnet;

    public AbschnittSpalten()
    {
        AutoScroll = true;
        DoubleBuffered = true;
    }

    /// <summary>Wie viele Spalten nebeneinander passen.</summary>
    /// <remarks>
    /// Gerechnet OHNE die Breite der Rollleiste: sonst nimmt die erscheinende Leiste
    /// eine Spalte weg, die Leiste verschwindet, die Spalte kommt wieder -- im Kreis.
    /// </remarks>
    public int Spaltenzahl =>
        Math.Max(1, (Width - SystemInformation.VerticalScrollBarWidth - 4) / SpaltenBreite);

    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        if (e.Control is { } c) { c.SizeChanged += (_, _) => Ordne(); }
        Ordne();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Ordne();
    }

    public void Ordne()
    {
        if (_ordnet) { return; }
        _ordnet = true;
        try
        {
            var n = Spaltenzahl;
            var hoehe = new int[n];
            var versatz = AutoScrollPosition;
            SuspendLayout();
            foreach (Control c in Controls)
            {
                var spalte = 0;
                for (var i = 1; i < n; i++) { if (hoehe[i] < hoehe[spalte]) { spalte = i; } }
                c.Location = new Point(versatz.X + spalte * SpaltenBreite, versatz.Y + hoehe[spalte]);
                hoehe[spalte] += c.Height + 1;
            }
            ResumeLayout();
        }
        finally { _ordnet = false; }
    }

    /// <summary>Einen Abschnitt in den Blick holen -- nur, wenn er es nicht schon ganz ist.</summary>
    public void Zeige(Control abschnitt)
    {
        if (abschnitt.Top >= 0 && abschnitt.Bottom <= ClientSize.Height) { return; }
        var oben = abschnitt.Top - AutoScrollPosition.Y;
        AutoScrollPosition = new Point(-AutoScrollPosition.X, Math.Max(0, oben - 4));
    }
}
