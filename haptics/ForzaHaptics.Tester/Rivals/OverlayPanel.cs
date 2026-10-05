using System.Drawing;
using System.Drawing.Drawing2D;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>One line of an overlay panel.</summary>
/// <param name="Left">The text. Gets whatever width the other columns leave.</param>
/// <param name="Right">The value, right-aligned at the edge.</param>
/// <param name="Mine">
/// A second value, right-aligned just left of <paramref name="Right"/>: what the
/// player's OWN laps say about this row. Empty for every line that has nothing of
/// their own to show, and then it costs no width at all -- a column that is blank
/// on every row is worse than no column.
/// </param>
/// <param name="Cells">
/// Mehrere rechtsbuendige Zellen statt einer einzelnen Wertspalte -- so entsteht
/// eine echte Tabelle: ein Auto links, je Kurs eine Zeit rechts. Leer heisst: diese
/// Zeile hat keine Tabellenform, dann gelten <paramref name="Mine"/> und
/// <paramref name="Right"/> wie bisher.
///
/// ALLE ZEILEN EINER TABELLE MUESSEN GLEICH VIELE ZELLEN HABEN, auch leere,
/// sonst steht die Kopfzeile nicht ueber den Zahlen.
/// </param>
internal readonly record struct PanelLine(string Left, string Right, Color Tone,
                                          bool Small = false, bool Heading = false,
                                          int Indent = 0, string Mine = "",
                                          string[]? Cells = null);

/// <summary>
/// One side of the screen: a borderless, always-on-top window that never takes a click.
/// </summary>
/// <remarks>
/// Owner-drawn rather than built from Labels. A panel can hold forty rows and is
/// rebuilt whenever the screen changes, and forty controls created and destroyed
/// on a timer flicker and cost far more than one OnPaint.
///
/// The window is click-through (WS_EX_TRANSPARENT) and never activates
/// (WS_EX_NOACTIVATE): the game keeps the mouse and the keyboard, which is the
/// whole point -- an overlay that steals focus mid-corner is worse than no overlay.
/// </remarks>
internal sealed class OverlayPanel : Form, IAufnahmeQuelle
{
    /// <summary>
    /// Wie breit die Spalte mit den eigenen Zeiten ist, wenn sie etwas zeigt.
    /// </summary>
    /// <remarks>
    /// Fest und nicht gemessen: eine Spalte, die je nach Inhalt die Breite
    /// wechselt, laesst die Autonamen daneben von Zeile zu Zeile springen. 96
    /// Pixel fassen "1:08.09" in der Notizschrift mit Luft.
    /// </remarks>
    private const int MineWidth = 96;

    /// <summary>Wie breit eine Zelle der Tabellenzeilen ist.</summary>
    /// <remarks>
    /// Aus demselben Grund fest wie <see cref="MineWidth"/>. 88 Pixel fassen
    /// "1:08.09" in der Notizschrift; mehr waere Platz, der links beim Autonamen
    /// fehlt, und Autonamen sind das Knappe an diesem Fenster.
    /// </remarks>
    private const int CellWidth = 104;

    private const int WsExLayered = 0x00080000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    // The website's dark palette, so the overlay reads as the same tool as the site.
    public static readonly Color Ink = ColorTranslator.FromHtml("#e7ecf3");
    public static readonly Color InkSoft = ColorTranslator.FromHtml("#c3ccd9");
    public static readonly Color Muted = ColorTranslator.FromHtml("#93a2b5");
    public static readonly Color Ground = ColorTranslator.FromHtml("#0b1017");
    public static readonly Color Surface = ColorTranslator.FromHtml("#131a23");
    public static readonly Color Bar = ColorTranslator.FromHtml("#22b8e6");
    public static readonly Color Warn = ColorTranslator.FromHtml("#f0a22e");
    public static readonly Color Good = ColorTranslator.FromHtml("#7ee787");

    private Font _titleFont = null!;
    private Font _subFont = null!;
    private Font _rowFont = null!;
    private Font _numFont = null!;
    private Font _noteFont = null!;
    private int _pad;

    private string _title = string.Empty;
    private string _subtitle = string.Empty;
    private string _note = string.Empty;
    private List<PanelLine> _lines = new();
    private int _offset;
    private int _pinned;
    private int _shown;

    public OverlayPanel(Rectangle bounds, double opacity)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        BackColor = Ground;
        Opacity = Math.Clamp(opacity, 0.2, 1.0);
        TopMost = true;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                 | ControlStyles.OptimizedDoubleBuffer, true);

        MakeFonts(bounds);
        OverlayAusgabe.Melde(this);
    }

    // ---- Aufnahmefenster und "Overlays ueber dem Spiel" (seit 2026-09-28) ----------
    //
    // Gewollt ist, was das Overlay zeigen WILL; ueber dem Spiel erscheint es nur, wenn
    // die Overlays dort eingeschaltet sind. "Visible" meint hier das Gewollte: Stellen
    // wie "if (Visible) Hide()" sollen auch dann richtig entscheiden, wenn ueber dem
    // Spiel nichts gezeigt wird -- sonst bliebe eine Meldung im Aufnahmefenster ewig.
    private bool _gewollt;

    public bool Gewollt => _gewollt && !IsDisposed;

    public new bool Visible => Gewollt;

    protected override void SetVisibleCore(bool value)
    {
        _gewollt = value;
        // Ein geschlossenes Fenster zeigt nichts mehr -- und wirft nicht: ein spaeter
        // Rueckruf des alten Controllers darf die App nicht mitnehmen (2026-09-28).
        if (IsDisposed || Disposing) { return; }
        base.SetVisibleCore(value && OverlayAusgabe.ImSpiel);
    }

    public void AusgabeAnwenden()
    {
        if (IsDisposed) { return; }
        base.SetVisibleCore(_gewollt && OverlayAusgabe.ImSpiel);
        if (base.Visible) { TopMost = true; }
    }

    public int Ebene => 0;

    private Bitmap? _aufnahme;
    private DateTime _aufnahmeZeit;

    /// <summary>
    /// Fuer das Aufnahmefenster: das Panel, wie es sich selbst zeichnet, mit seiner
    /// Deckkraft. Hoechstens viermal je Sekunde neu gerendert -- der Inhalt wechselt
    /// beim Blaettern, nicht mit jedem Bild.
    /// </summary>
    public void MaleFuerAufnahme(Graphics g)
    {
        if (!Gewollt || ClientSize.Width <= 0 || ClientSize.Height <= 0) { return; }
        var jetzt = DateTime.UtcNow;
        if (_aufnahme is null || _aufnahme.Size != ClientSize || jetzt - _aufnahmeZeit > TimeSpan.FromMilliseconds(250))
        {
            _aufnahme?.Dispose();
            _aufnahme = new Bitmap(ClientSize.Width, ClientSize.Height);
            using (var bg = Graphics.FromImage(_aufnahme))
            using (var pe = new PaintEventArgs(bg, new Rectangle(Point.Empty, ClientSize)))
            {
                OnPaint(pe);
            }
            _aufnahmeZeit = jetzt;
        }
        var f = OverlayAusgabe.Flaeche;
        using var attr = new System.Drawing.Imaging.ImageAttributes();
        attr.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = (float)Opacity });
        g.DrawImage(_aufnahme, new Rectangle(Left - f.X, Top - f.Y, Width, Height),
                    0, 0, _aufnahme.Width, _aufnahme.Height, GraphicsUnit.Pixel, attr);
    }

    // Every size is derived from the screen, so a 4K panel is not a strip of ants.
    private void MakeFonts(Rectangle bounds)
    {
        var unit = Math.Max(9f, bounds.Height / 108f);
        _titleFont = new Font("Segoe UI Semibold", unit + 3f);
        _subFont = new Font("Segoe UI", unit - 1.5f);
        _rowFont = new Font("Segoe UI", unit - 0.5f);
        _numFont = new Font("Consolas", unit - 0.5f);
        _noteFont = new Font("Segoe UI", unit - 2.5f);
        _pad = (int)Math.Max(8, unit);
    }

    /// <summary>
    /// Move to a new area -- the game turned up on another monitor, in a window, or
    /// at another resolution. Fonts follow, because they are derived from the height.
    /// </summary>
    public void Reposition(Rectangle bounds)
    {
        if (bounds == Bounds) { return; }
        var alt = new[] { _titleFont, _subFont, _rowFont, _numFont, _noteFont };
        Bounds = bounds;
        MakeFonts(bounds);
        foreach (var f in alt) { f.Dispose(); }
        Invalidate();
    }

    /// <summary>Never take focus, never take a click, never appear in Alt+Tab.</summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExLayered | WsExTransparent | WsExNoActivate | WsExToolWindow;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    private const uint WdaNone = 0x00000000;
    private const uint WdaExcludeFromCapture = 0x00000011;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(
        System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    /// <summary>Whether Windows agreed to keep this window out of screen captures.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ExcludedFromCapture { get; private set; }

    /// <summary>
    /// Take this window out of every screen capture, while leaving it visible on screen.
    /// </summary>
    /// <remarks>
    /// WHY: the reader answers by photographing the screen -- and this panel sits on the
    /// screen. Measured 2026-09-09 on the Event Sign Up screen: the three routes read
    /// perfectly (1.00 / 0.97 / 1.00) and the class came back "not found", because the
    /// right panel covers exactly the badge's corner (region_class starts at 0.76 of the
    /// width, the panel at about 0.71). The first press worked, every press after it
    /// photographed this window instead of the game.
    ///
    /// Hiding the panel around the grab would work but costs a visible flash on every
    /// read -- and a PINNED panel would blink out, which is the one thing pinning is for.
    /// WDA_EXCLUDEFROMCAPTURE (Windows 10 2004, and this app targets 10.0.19041) tells the
    /// compositor to leave the window out of BitBlt and the duplication API while it stays
    /// on screen. If the call fails, the caller falls back to hiding.
    /// </remarks>
    public bool ExcludeFromCapture()
    {
        if (!IsHandleCreated)
        {
            return false;
        }
        ExcludedFromCapture = SetWindowDisplayAffinity(Handle, WdaExcludeFromCapture);
        return ExcludedFromCapture;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ExcludeFromCapture();
    }

    /// <summary>When this panel takes itself away again; MinValue means it is not up.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public DateTime Until { get; set; } = DateTime.MinValue;

    public bool Up => Until > DateTime.MinValue;

    /// <summary>How many rows fit, once the head and the footnote are paid for.</summary>
    public int RowBudget
    {
        get
        {
            var line = _rowFont.Height + 6;
            var head = _titleFont.Height + _subFont.Height + 3 * _pad;
            var note = _noteFont.Height * 3 + _pad;
            return Math.Max(6, (Height - head - note) / Math.Max(1, line) - 1);
        }
    }

    public void SetContent(string title, string subtitle, List<PanelLine> lines,
                           string note)
    {
        // Where the reader had got to is kept while the same content is redrawn --
        // the loop rebuilds a panel whenever the screen changes, and a list that
        // jumped back to the top under someone reading it would be unusable.
        var same = title == _title && subtitle == _subtitle
                   && lines.Count == _lines.Count;
        _title = title;
        _subtitle = subtitle;
        _lines = lines;
        _note = note;
        // Everything up to and including the first heading stays put while the rest
        // scrolls: that line says what the list is and how far down it you are.
        _pinned = 0;
        for (var at = 0; at < lines.Count; at++)
        {
            if (lines[at].Heading)
            {
                _pinned = at + 1;
                break;
            }
        }
        if (!same)
        {
            _offset = 0;
        }
        Clamp();
        Invalidate();
    }

    /// <summary>Move a page down (+1) or up (-1). True if anything moved.</summary>
    public bool ScrollPage(int pages)
    {
        var step = Math.Max(1, _shown - 1);
        var before = _offset;
        _offset += pages * step;
        Clamp();
        if (_offset == before)
        {
            return false;
        }
        Invalidate();
        return true;
    }

    /// <summary>Back to the top, for a panel that is being opened afresh.</summary>
    public void Rewind()
    {
        if (_offset == 0)
        {
            return;
        }
        _offset = 0;
        Invalidate();
    }

    /// <summary>Never past the last page, and never before the first.</summary>
    private void Clamp()
    {
        var scrollable = Math.Max(0, _lines.Count - _pinned);
        var last = Math.Max(0, scrollable - Math.Max(1, _shown));
        _offset = Math.Clamp(_offset, 0, last);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Ground);

        var width = ClientSize.Width;
        var textWidth = width - 2 * _pad;

        // head
        var headHeight = _pad + _titleFont.Height + 2 + _subFont.Height + _pad;
        using (var surface = new SolidBrush(Surface))
        {
            g.FillRectangle(surface, 0, 0, width, headHeight);
        }
        using (var ink = new SolidBrush(Ink))
        using (var muted = new SolidBrush(Muted))
        {
            g.DrawString(_title, _titleFont, ink,
                         new RectangleF(_pad, _pad, textWidth, _titleFont.Height + 4));
            g.DrawString(_subtitle, _subFont, muted,
                         new RectangleF(_pad, _pad + _titleFont.Height + 2, textWidth,
                                        _subFont.Height + 4));
        }

        // footnote, claimed from the bottom first so a long list can never take it
        using var noteFormat = new StringFormat { Trimming = StringTrimming.EllipsisWord };
        var noteHeight = 0;
        if (!string.IsNullOrEmpty(_note))
        {
            noteHeight = (int)g.MeasureString(_note, _noteFont, textWidth).Height + _pad / 2;
            using var muted = new SolidBrush(Muted);
            g.DrawString(_note, _noteFont, muted,
                         new RectangleF(_pad, Height - noteHeight, textWidth,
                                        noteHeight), noteFormat);
        }

        // rows: the pinned head, then a window into the rest
        var y = headHeight + _pad / 2;
        var limit = Height - noteHeight - 2;
        using var right = new StringFormat
        {
            Alignment = StringAlignment.Far,
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter,
        };
        // EIN ZU LANGER AUTONAME MUSS SICHTBAR ABGESCHNITTEN SEIN.
        // Ohne das umbricht GDI+ den Namen in eine zweite Zeile, die nicht mehr in
        // das eine Zeile hohe Rechteck passt und deshalb gar nicht gezeichnet wird:
        // aus "Nissan #11 Tomica Skyline Turbo Super Silhouette '83" wuerde
        // wortlos "Nissan #11 Tomica Skyline Turbo Super", und niemand saehe, dass
        // etwas fehlt. Mit Ellipse steht dort ein Zeichen, das es sagt.
        using var links = new StringFormat
        {
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter,
        };
        var scrollable = Math.Max(0, _lines.Count - _pinned);
        var drawn = 0;
        for (var at = 0; at < _lines.Count; at++)
        {
            if (at >= _pinned && at < _pinned + _offset)
            {
                continue;
            }
            var line = _lines[at];
            var font = line.Heading || line.Small ? _noteFont : _rowFont;
            var height = font.Height + (line.Heading ? 8 : 3);
            if (y + height > limit)
            {
                break;
            }
            if (at >= _pinned)
            {
                drawn++;
            }
            if (line.Heading)
            {
                using var bar = new SolidBrush(Bar);
                g.DrawString(line.Left.ToUpperInvariant(), _noteFont, bar,
                             new PointF(_pad, y + 5));
                // How far down the list this window is. Drawn here rather than
                // written into the text, because only the panel knows how many rows
                // its own height can hold.
                if (at + 1 == _pinned && scrollable > 0)
                {
                    var first = _offset + 1;
                    var last = Math.Min(scrollable, _offset + Math.Max(1, _shown));
                    var where = _offset == 0 && last >= scrollable
                        ? $"{scrollable}"
                        : $"{first}\u2013{last} / {scrollable}";
                    g.DrawString(where, _noteFont, bar,
                                 new RectangleF(_pad, y + 5, textWidth,
                                                _noteFont.Height + 2), right);
                }
            }
            else if (line.Cells is { Length: > 0 } zellen)
            {
                // TABELLENZEILE: links der Name, rechts je Kurs eine Zelle.
                // Die Zellen liegen an FESTEN Stellen, die nur von ihrer Anzahl
                // abhaengen -- nicht vom Inhalt. Nur so steht die Zeit unter der
                // Kopfzeile, zu der sie gehoert, auch wenn die Zelle leer ist.
                var breite = zellen.Length * CellWidth;
                using var tone = new SolidBrush(line.Tone);
                g.DrawString(line.Left, font, tone,
                             new RectangleF(_pad + line.Indent, y,
                                            Math.Max(40, textWidth - line.Indent - breite),
                                            font.Height + 2), links);
                for (var i = 0; i < zellen.Length; i++)
                {
                    if (string.IsNullOrEmpty(zellen[i])) { continue; }
                    var x = _pad + textWidth - breite + (i * CellWidth);
                    g.DrawString(zellen[i], _noteFont, tone,
                                 new RectangleF(x, y, CellWidth, font.Height + 2),
                                 right);
                }
            }
            else
            {
                // DIE EIGENE SPALTE KOSTET NUR BREITE, WENN SIE ETWAS ZU SAGEN HAT.
                // Sonst bliebe auf jeder Zeile ohne eigene Runde ein Loch im Namen
                // stehen, und Autonamen sind ohnehin zu lang fuer diese Fenster.
                var meinBreite = string.IsNullOrEmpty(line.Mine) ? 0 : MineWidth;
                using var tone = new SolidBrush(line.Tone);
                g.DrawString(line.Left, font, tone,
                             new RectangleF(_pad + line.Indent, y,
                                            textWidth - line.Indent - 120 - meinBreite,
                                            font.Height + 2), links);
                if (!string.IsNullOrEmpty(line.Mine))
                {
                    // Rechtsbuendig, aber um die Wertspalte nach links geholt. In
                    // der Leitfarbe, damit auf einen Blick zu sehen ist, welche der
                    // empfohlenen Autos man ueberhaupt schon gefahren hat.
                    using var mein = new SolidBrush(Bar);
                    g.DrawString(line.Mine, _noteFont, mein,
                                 new RectangleF(_pad, y, textWidth - 120,
                                                font.Height + 2),
                                 right);
                }
                if (!string.IsNullOrEmpty(line.Right))
                {
                    using var value = new SolidBrush(line.Small ? Muted : line.Tone);
                    g.DrawString(line.Right, line.Small ? _noteFont : _numFont, value,
                                 new RectangleF(_pad, y, textWidth, font.Height + 2),
                                 right);
                }
            }
            y += height;
        }
        // What actually fitted, which is what a page of scrolling is worth.
        _shown = Math.Max(1, drawn);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _titleFont.Dispose();
            _subFont.Dispose();
            _rowFont.Dispose();
            _numFont.Dispose();
            _noteFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
