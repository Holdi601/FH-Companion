using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>Wo ein Element sitzt und wie es aussieht -- alles aus der Einstellung.</summary>
internal sealed record HudPlacement(float X, float Y, string Align, float Scale);

/// <summary>
/// The thin strip over the game: delta, ghost countdown, and a short note.
/// </summary>
/// <remarks>
/// Getrennt von <see cref="OverlayPanel"/>, obwohl beide ueber dem Spiel liegen. Die
/// Panels beantworten eine Frage auf Knopfdruck und verschwinden wieder; dieser
/// Streifen laeuft WAEHREND der Fahrt mit und darf deshalb nichts verdecken, keinen
/// Knopf brauchen und nie auf eine Eingabe warten.
///
/// Das Fenster liegt ueber dem GANZEN Schirm, nicht nur oben: nur so lassen sich die
/// Elemente frei setzen. Es ist durchklickbar (WS_EX_TRANSPARENT), nimmt nie den
/// Fokus (WS_EX_NOACTIVATE) und haelt sich aus jeder Bildschirmaufnahme heraus
/// (WDA_EXCLUDEFROMCAPTURE) -- Letzteres, weil der Rivals-Bildschirmleser sonst
/// seinen eigenen Streifen fotografiert und die Klasse nicht mehr findet. Genau das
/// ist am 2026-09-09 mit dem rechten Panel passiert.
///
/// Die Texte sind ENGLISCH, wie alles, was im Spiel steht.
/// </remarks>
internal sealed class DeltaHud : Form
{
    private const int WsExLayered = 0x00080000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const uint WdaExcludeFromCapture = 0x00000011;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(
        System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    // ---- Ein Fenster mit Alpha JE PIXEL ------------------------------- //
    //
    // WARUM NICHT TransparencyKey (bis 2026-09-12 hier verwendet):
    //
    // Der Farbschluessel kennt nur zwei Zustaende. Ein Pixel ist entweder exakt die
    // Schluesselfarbe -- dann vollstaendig durchsichtig -- oder er ist es nicht, dann
    // vollstaendig deckend. Eine Platte mit Alpha 150 wird vorher gegen den schwarzen
    // Fensterhintergrund verrechnet und landet bei RGB(5,6,9): nicht exakt schwarz,
    // also voll deckend. Bei Alpha 0 wird gar nichts gezeichnet, also exakt schwarz,
    // also ganz weg. Der Regler konnte darum nur "an" oder "aus", nie etwas
    // dazwischen -- genau so hat es der Nutzer beschrieben.
    //
    // UpdateLayeredWindow nimmt dagegen eine 32-Bit-Flaeche MIT Alphakanal entgegen
    // und blendet sie im Fenstermanager ueber das, was darunter liegt. Damit ist die
    // Platte wirklich halbdurchsichtig, waehrend die Schrift darauf deckend bleibt.
    private const int UlwAlpha = 0x00000002;
    private const byte AcSrcOver = 0x00;
    private const byte AcSrcAlpha = 0x01;
    private const int BiRgb = 0;
    private const uint DibRgbColors = 0;

    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(
        System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hWnd, IntPtr hdcDst, ref Point pptDst, ref Size psize, IntPtr hdcSrc,
        ref Point pptSrc, int crKey, ref BlendFunction pblend, int dwFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(
        IntPtr hdc, ref BitmapInfoHeader pbmi, uint usage, out IntPtr ppvBits,
        IntPtr hSection, uint offset);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDC);

    private bool _pushFailed;
    private IntPtr _memDc;
    private IntPtr _dib;
    private IntPtr _oldBitmap;
    private Bitmap? _surface;

    private readonly OverlaySettings _settings;
    private readonly float _unit;

    private float? _delta;
    private string _label = string.Empty;
    private float? _secondDelta;
    private string _secondLabel = string.Empty;
    private float _ghostLeft;
    private readonly InputTrace _inputs = new();
    private RecordedLap? _refLap;
    private float? _refSeconds;
    private string _note = string.Empty;
    private DateTime _noteUntil = DateTime.MinValue;

    /// <summary>Wie viele Streifen gerade offen sind.</summary>
    /// <remarks>
    /// Es darf immer nur EINER sein. Bis zum 2026-09-13 blieb beim Neuaufbau des
    /// Controllers der alte offen -- bildschirmfuellend, obenauf und mit
    /// eingefrorenem Inhalt. Auf dem Schirm sieht das aus, als liefe das Programm
    /// mehrfach, und niemand kann es fotografieren, weil sich der Streifen aus
    /// jeder Aufnahme heraushaelt. Diese Zahl macht daraus eine Protokollzeile.
    /// </remarks>
    private static int _offen;

    public DeltaHud(Rectangle screen, OverlaySettings settings)
    {
        _settings = settings;
        var jetzt = System.Threading.Interlocked.Increment(ref _offen);
        if (jetzt > 1)
        {
            OverlayController.WriteDiagnostic(
                $"ACHTUNG: {jetzt} Streifen gleichzeitig offen -- einer davon wird "
                + "nicht mehr gefuettert und zeigt alte Werte.");
        }
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = screen;
        TopMost = true;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                 | ControlStyles.OptimizedDoubleBuffer, true);
        _unit = Math.Max(10f, screen.Height / 36f);
    }

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

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetWindowDisplayAffinity(Handle, WdaExcludeFromCapture);
        BuildSurface();
        Render();
    }

    /// <summary>
    /// Die Zeichenflaeche EINMAL anlegen -- als DIB, direkt im Speicher des Fensters.
    /// </summary>
    /// <remarks>
    /// Auf einem 4K-Schirm sind das 33 MB. Sie je Bild neu zu erzeugen (etwa ueber
    /// <c>Bitmap.GetHbitmap</c>) hiesse, diese 33 MB zehnmal je Sekunde zu kopieren --
    /// waehrend jemand faehrt. So wird einmal belegt und danach nur noch
    /// hineingezeichnet.
    /// </remarks>
    private void BuildSurface()
    {
        DisposeSurface();
        var kopf = new BitmapInfoHeader
        {
            biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<BitmapInfoHeader>(),
            biWidth = Math.Max(1, Width),
            // Negativ: von oben nach unten, wie GDI+ zeichnet. Positiv stuende das
            // Bild auf dem Kopf, und zwar erst auf dem Schirm.
            biHeight = -Math.Max(1, Height),
            biPlanes = 1,
            biBitCount = 32,
            biCompression = BiRgb,
        };
        var schirmDc = GetDC(IntPtr.Zero);
        try
        {
            _memDc = CreateCompatibleDC(schirmDc);
            _dib = CreateDIBSection(schirmDc, ref kopf, DibRgbColors, out var bits,
                                    IntPtr.Zero, 0);
            if (_dib == IntPtr.Zero || _memDc == IntPtr.Zero) { return; }
            _oldBitmap = SelectObject(_memDc, _dib);
            // PArgb: die Farben liegen mit ihrem Alpha vormultipliziert vor -- genau
            // das erwartet UpdateLayeredWindow, und genau das liefert GDI+ beim
            // Zeichnen in eine solche Flaeche.
            _surface = new Bitmap(Math.Max(1, Width), Math.Max(1, Height),
                                  Math.Max(1, Width) * 4,
                                  System.Drawing.Imaging.PixelFormat.Format32bppPArgb,
                                  bits);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, schirmDc);
        }
    }

    private void DisposeSurface()
    {
        _surface?.Dispose();
        _surface = null;
        if (_memDc != IntPtr.Zero)
        {
            if (_oldBitmap != IntPtr.Zero) { SelectObject(_memDc, _oldBitmap); }
            DeleteDC(_memDc);
            _memDc = IntPtr.Zero;
            _oldBitmap = IntPtr.Zero;
        }
        if (_dib != IntPtr.Zero)
        {
            DeleteObject(_dib);
            _dib = IntPtr.Zero;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeSurface();
            System.Threading.Interlocked.Decrement(ref _offen);
        }
        base.Dispose(disposing);
    }

    /// <summary>Alles neu zeichnen und dem Fenstermanager hinlegen.</summary>
    private void Render()
    {
        if (!IsHandleCreated || IsDisposed || _surface is null || _memDc == IntPtr.Zero)
        {
            return;
        }
        try
        {
            using (var g = Graphics.FromImage(_surface))
            {
                // Durchsichtig anfangen: was nicht gezeichnet wird, bleibt das Spiel.
                g.Clear(Color.Transparent);
                // KEIN ClearType auf einer durchsichtigen Flaeche -- die
                // Subpixelglaettung rechnet mit einem deckenden Hintergrund und
                // hinterlaesst sonst farbige Raender. Graustufenglaettung kann mit
                // Alpha umgehen.
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Paint(g);
            }

            var lage = new Point(Left, Top);
            var groesse = new Size(_surface.Width, _surface.Height);
            var quelle = new Point(0, 0);
            var mischen = new BlendFunction
            {
                BlendOp = AcSrcOver,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AcSrcAlpha,
            };
            var schirmDc = GetDC(IntPtr.Zero);
            bool gelungen;
            var fehler = 0;
            try
            {
                gelungen = UpdateLayeredWindow(Handle, schirmDc, ref lage, ref groesse,
                                               _memDc, ref quelle, 0, ref mischen,
                                               UlwAlpha);
                if (!gelungen)
                {
                    fehler = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                }
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, schirmDc);
            }

            // EINMAL melden, wenn das Zeichnen scheitert. Sonst ist der Streifen
            // einfach unsichtbar -- und weil er sich aus jeder Bildschirmaufnahme
            // heraushaelt, liesse sich das hinterher durch nichts belegen.
            if (!gelungen && !_pushFailed)
            {
                _pushFailed = true;
                OverlayController.WriteDiagnostic(
                    $"UpdateLayeredWindow scheiterte (Win32 {fehler}) -- der Streifen "
                    + "bleibt unsichtbar.");
            }
            else if (gelungen && _pushFailed)
            {
                _pushFailed = false;
                OverlayController.WriteDiagnostic("UpdateLayeredWindow geht wieder.");
            }
        }
        catch (Exception)
        {
            // Ein Anzeigefehler darf das Rennen nicht stoeren.
        }
    }

    /// <summary>
    /// Was gerade an Pedalen und Lenkung passiert -- und was dort in der Bestzeit
    /// passiert ist.
    /// </summary>
    /// <remarks>
    /// Ein Wert je Aufruf; gezeichnet wird der Verlauf der letzten Sekunden. Genau
    /// darum geht es: eine Momentaufnahme sagt nichts darueber, ob zu frueh gebremst
    /// oder zu spaet ans Gas gegangen wurde.
    /// </remarks>
    public void PushInputs(float throttle, float brake, float clutch, float steer,
                           float gear, RecordedLap? reference, float? referenceSeconds)
    {
        _inputs.Push(throttle, brake, clutch, steer, gear,
                     (float)_settings.HudInputsSeconds);
        // Die Referenz wird NICHT mitgeschrieben, sondern bei jedem Zeichnen frisch
        // abgefragt: nur so laesst sich auch ihre Zukunft zeigen, und die ist der
        // Grund, warum man ueberhaupt hinsieht.
        _refLap = reference;
        _refSeconds = referenceSeconds;
        if (_settings.HudInputs) { Render(); }
    }

    /// <summary>Die Spuren leeren -- neue Runde, neuer Vergleich.</summary>
    public void ClearInputs()
    {
        _inputs.Clear();
        _refLap = null;
        _refSeconds = null;
    }

    /// <summary>A short line -- that a lap was stored, for instance.</summary>
    /// <remarks>
    /// Ohne sie ist "keine Anzeige" nicht von "nichts aufgezeichnet" zu unterscheiden,
    /// und die erste Runde auf einer Strecke sieht aus wie ein kaputter Streifen.
    /// </remarks>
    public void Note(string text, double seconds = 8)
    {
        _note = text;
        _noteUntil = DateTime.UtcNow.AddSeconds(seconds);
        Render();
    }

    /// <summary>Den Inhalt in ein Bild zeichnen -- fuer den Selbsttest, ohne Fenster.</summary>
    internal void PaintInto(Graphics g) => Paint(g);

    /// <summary>Steht gerade eine Meldung?</summary>
    public bool NoteActive => DateTime.UtcNow < _noteUntil && _note.Length > 0;

    private bool _nurNotiz;

    /// <summary>
    /// Nach dem Rennen: nur die Meldungszeile zeigen, nichts sonst.
    /// </summary>
    /// <remarks>
    /// Seit 2026-09-25. Ein Sprint endet im Ziel, und im selben Augenblick wird die
    /// Fahrt abgelegt -- die Meldung "lap stored" kam damit genau in dem Moment, in
    /// dem der Streifen verschwand. Der Nutzer sah sie nach einer halben Stunde
    /// Meisterschaften kein einziges Mal, obwohl jede Fahrt aufgezeichnet war.
    /// </remarks>
    public void NurNotiz(bool an)
    {
        if (_nurNotiz == an) { return; }
        _nurNotiz = an;
        Render();
    }

    /// <summary>What to show. A null delta means: no reference of that kind yet.</summary>
    public void Update(float? delta, string label, float ghostSecondsLeft,
                       float? secondDelta = null, string secondLabel = "")
    {
        var changed = _delta != delta || _label != label
                      || _secondDelta != secondDelta || _secondLabel != secondLabel
                      || Math.Abs(_ghostLeft - ghostSecondsLeft) > 0.05f;
        _delta = delta;
        _label = label;
        _secondDelta = secondDelta;
        _secondLabel = secondLabel;
        _ghostLeft = ghostSecondsLeft;
        if (changed) { Render(); }
    }

    // Ein vertippter Farbwert darf den Streifen nicht schwarz lassen -- die Regel
    // steht bei den Einstellungen, damit Streifen und Vorschau dieselbe befolgen.
    private static Color Parse(string? value, Color fallback) =>
        OverlaySettings.ParseColour(value, fallback);

    /// <summary>Einen Block zeichnen und seine Hoehe zurueckgeben.</summary>
    private float Draw(Graphics g, HudPlacement place, string big, Color colour,
                       string small, Font bigFont, Font smallFont,
                       Color? plate = null)
    {
        var bigSize = g.MeasureString(big, bigFont);
        var smallSize = string.IsNullOrEmpty(small)
            ? SizeF.Empty
            : g.MeasureString(small, smallFont);
        var width = Math.Max(bigSize.Width, smallSize.Width) + 4 * _unit;
        var height = bigSize.Height + smallSize.Height + _unit;

        var left = Width * place.X;
        left = place.Align.ToLowerInvariant() switch
        {
            "left" => left,
            "right" => left - width,
            _ => left - width / 2,
        };
        var top = Height * place.Y;

        using (var shade = new SolidBrush(
                   plate ?? Parse(_settings.HudBackground, Color.FromArgb(150, 8, 11, 16))))
        {
            g.FillRectangle(shade, left, top, width, height);
        }
        using (var pen = new SolidBrush(colour))
        {
            g.DrawString(big, bigFont, pen, left + (width - bigSize.Width) / 2, top + 2);
        }
        if (!string.IsNullOrEmpty(small))
        {
            using var quiet = new SolidBrush(Parse(_settings.HudColorLabel, Color.FromArgb(147, 162, 181)));
            g.DrawString(small, smallFont, quiet,
                         left + (width - smallSize.Width) / 2, top + bigSize.Height + 2);
        }
        return height;
    }

    /// <summary>
    /// Vier Spuren: Gas, Bremse, Kupplung, Lenkung -- meine hell, die der Bestzeit
    /// daneben, samt ihrer naechsten Sekunden.
    /// </summary>
    /// <remarks>
    /// Als Kurve ueber die Zeit und nicht als Balken: die Frage lautet nicht "wie
    /// weit ist das Gaspedal", sondern "wann bin ich gelupft und wann die Bestzeit".
    ///
    /// Die senkrechte Linie ist JETZT. Links davon liegt, was war -- beide Spuren.
    /// Rechts davon nur die Bestzeit, und das ist der Sinn der Sache: was sie gleich
    /// tun wird, laesst sich nachfahren. Die eigene Spur kann dort nichts zeigen,
    /// ohne zu luegen.
    ///
    /// Die Lenkung sitzt auf der Mittellinie, weil sie in beide Richtungen geht.
    /// </remarks>
    private void DrawInputs(Graphics g, HudPlacement place, Font labelFont)
    {
        var vorlauf = (float)Math.Clamp(_settings.HudInputsLookahead, 0, 10);
        var rueckschau = (float)Math.Clamp(_settings.HudInputsSeconds, 1, 60);
        var spanne = rueckschau + vorlauf;

        var breite = _unit * 9f * place.Scale;
        var spurHoehe = _unit * 1.05f * place.Scale;
        var luft = _unit * 0.30f * place.Scale;
        var hoehe = 5 * spurHoehe + 4 * luft + _unit * 0.5f;

        var links = Width * place.X;
        links = place.Align.ToLowerInvariant() switch
        {
            "left" => links,
            "right" => links - breite,
            _ => links - breite / 2,
        };
        var oben = Height * place.Y;

        using (var platte = new SolidBrush(
                   Parse(_settings.HudBackground, Color.FromArgb(150, 8, 11, 16))))
        {
            g.FillRectangle(platte, links, oben, breite, hoehe);
        }

        var meine = Parse(_settings.HudColorMine, ColorTranslator.FromHtml("#e7ecf3"));
        var fremde = Parse(_settings.HudColorTheirs, ColorTranslator.FromHtml("#ffb454"));
        var jetztFarbe = Parse(_settings.HudColorNow, ColorTranslator.FromHtml("#7ee787"));
        var beschriftung = Parse(_settings.HudColorLabel, Color.FromArgb(147, 162, 181));

        var spuren = new (string Name, bool Mittig)[]
        {
            ("THR", false), ("BRK", false), ("CLU", false), ("STR", true),
            ("GEAR", false),
        };
        using var klein = new Font("Segoe UI", Math.Max(5f, _unit * 0.46f * place.Scale));
        var strich = (float)Math.Clamp(_settings.HudInputsLineWidth, 0.2, 6.0);
        using var stiftMeins = new Pen(meine,
            Math.Max(0.5f, _unit * 0.09f * place.Scale * strich));
        using var stiftFremd = new Pen(fremde,
            Math.Max(0.5f, _unit * 0.09f * place.Scale * strich));
        using var stiftJetzt = new Pen(jetztFarbe,
            Math.Max(0.5f, _unit * 0.06f * place.Scale * strich));
        using var linie = new Pen(Color.FromArgb(60, 255, 255, 255), 1f);
        using var grund = new SolidBrush(beschriftung);

        var x0 = links + _unit * 1.5f * place.Scale;
        var spurBreite = breite - (x0 - links) - _unit * 0.4f * place.Scale;
        var jetztX = x0 + spurBreite * (rueckschau / spanne);

        // Die Zukunft liegt hinter der Jetzt-Linie und wird schwach hinterlegt,
        // damit "kommt noch" und "war schon" nicht zu verwechseln sind.
        if (vorlauf > 0)
        {
            using var kommt = new SolidBrush(Color.FromArgb(26, 255, 255, 255));
            g.FillRectangle(kommt, jetztX, oben, x0 + spurBreite - jetztX, hoehe);
        }

        for (var i = 0; i < spuren.Length; i++)
        {
            var y0 = oben + _unit * 0.25f + i * (spurHoehe + luft);
            var mitte = y0 + spurHoehe / 2;
            g.DrawString(spuren[i].Name, klein, grund,
                         links + _unit * 0.2f * place.Scale, y0);
            g.DrawLine(linie, x0, spuren[i].Mittig ? mitte : y0 + spurHoehe,
                       x0 + spurBreite, spuren[i].Mittig ? mitte : y0 + spurHoehe);

            // Der Gang ist eine Treppe, keine Kurve, und er braucht einen
            // Massstab: durch zehn geteilt sind sechs Gaenge ein Streifchen am
            // unteren Rand. Also nach dem hoechsten Gang, der gerade vorkommt.
            var stufen = i == 4;
            var massstab = stufen ? HoechsterGang(rueckschau, vorlauf) : 1f;

            ReferenzKurve(g, stiftFremd, i, x0, y0, spurBreite, spurHoehe,
                          spuren[i].Mittig, rueckschau, vorlauf, stufen, massstab);
            // Meine Spur endet an der Jetzt-Linie.
            Kurve(g, stiftMeins, Skaliert(_inputs.Mine(i), massstab), x0, y0,
                  spurBreite * (rueckschau / spanne), spurHoehe, spuren[i].Mittig,
                  stufen);
        }

        g.DrawLine(stiftJetzt, jetztX, oben + 1, jetztX, oben + hoehe - 1);
    }

    /// <summary>
    /// Die Spur der Bestzeit ueber das ganze Fenster -- Vergangenheit UND Zukunft.
    /// </summary>
    /// <remarks>
    /// Abgefragt wird die Runde selbst, Punkt fuer Punkt ueber ihre eigene Zeit.
    /// Ausserhalb der Runde (vor dem Start, nach dem Ziel) bleibt die Kurve leer,
    /// statt sich am Rand festzuhalten.
    /// </remarks>
    /// <summary>Der hoechste Gang, der im Fenster vorkommt -- mindestens sechs.</summary>
    /// <remarks>
    /// Sonst bliebe die Gangspur bei einem Sechsganggetriebe im unteren Drittel
    /// kleben, und ein Hochschalten waere ein Zucken statt einer Stufe.
    /// </remarks>
    private float HoechsterGang(float rueckschau, float vorlauf)
    {
        var hoechster = 6f;
        foreach (var wert in _inputs.Mine(4))
        {
            if (wert is not null && wert.Value > hoechster) { hoechster = wert.Value; }
        }
        if (_refLap is not null && _refSeconds is not null)
        {
            for (var t = -rueckschau; t <= vorlauf; t += 0.5f)
            {
                var punkt = _refLap.SampleAtSeconds(_refSeconds.Value + t);
                if (punkt is not null && punkt.Value.Gear > hoechster)
                {
                    hoechster = punkt.Value.Gear;
                }
            }
        }
        return hoechster;
    }

    private static IReadOnlyList<float?> Skaliert(IReadOnlyList<float?> werte, float durch)
    {
        if (Math.Abs(durch - 1f) < 0.0001f) { return werte; }
        var raus = new List<float?>(werte.Count);
        foreach (var wert in werte) { raus.Add(wert is null ? null : wert.Value / durch); }
        return raus;
    }

    private void ReferenzKurve(Graphics g, Pen stift, int kanal, float x0, float y0,
                               float breite, float hoehe, bool mittig,
                               float rueckschau, float vorlauf,
                               bool stufen = false, float massstab = 1f)
    {
        if (_refLap is null || _refSeconds is null) { return; }
        const int Schritte = 72;
        var spanne = rueckschau + vorlauf;
        var werte = new List<float?>(Schritte + 1);
        for (var i = 0; i <= Schritte; i++)
        {
            var t = -rueckschau + spanne * i / Schritte;
            var punkt = _refLap.SampleAtSeconds(_refSeconds.Value + t);
            werte.Add(punkt is null ? null : kanal switch
            {
                0 => punkt.Value.Throttle,
                1 => punkt.Value.Brake,
                2 => punkt.Value.Clutch,
                3 => punkt.Value.Steer,
                _ => punkt.Value.Gear / massstab,
            });
        }
        Kurve(g, stift, werte, x0, y0, breite, hoehe, mittig, stufen);
    }

    private static void Kurve(Graphics g, Pen stift, IReadOnlyList<float?> werte,
                              float x0, float y0, float breite, float hoehe, bool mittig,
                              bool stufen = false)
    {
        if (werte.Count < 2) { return; }
        PointF? vorher = null;
        for (var i = 0; i < werte.Count; i++)
        {
            var wert = werte[i];
            if (wert is null) { vorher = null; continue; }
            var anteil = werte.Count == 1 ? 1f : (float)i / (werte.Count - 1);
            var v = mittig
                ? 0.5f - Math.Clamp(wert.Value, -1f, 1f) / 2f
                : 1f - Math.Clamp(wert.Value, 0f, 1f);
            var punkt = new PointF(x0 + breite * anteil, y0 + hoehe * v);
            if (vorher is not null)
            {
                if (stufen)
                {
                    // Erst halten, dann springen: ein Gang wechselt schlagartig.
                    var ecke = new PointF(punkt.X, vorher.Value.Y);
                    g.DrawLine(stift, vorher.Value, ecke);
                    g.DrawLine(stift, ecke, punkt);
                }
                else
                {
                    g.DrawLine(stift, vorher.Value, punkt);
                }
            }
            vorher = punkt;
        }
    }

    private static string Format(float? delta) =>
        delta is null ? "--.---" : $"{(delta >= 0 ? "+" : "-")}{Math.Abs(delta.Value):0.000}";

    /// <summary>Der Inhalt. Die Flaeche ist schon geleert und eingestellt.</summary>
    private void Paint(Graphics g)
    {

        var ahead = Parse(_settings.HudColorAhead, ColorTranslator.FromHtml("#7ee787"));
        var behind = Parse(_settings.HudColorBehind, ColorTranslator.FromHtml("#ff6b6b"));
        var neutral = Parse(_settings.HudColorNeutral, ColorTranslator.FromHtml("#e7ecf3"));
        var ghost = Parse(_settings.HudColorGhost, ColorTranslator.FromHtml("#22b8e6"));

        var deltaPlace = _settings.DeltaPlacement;
        var ghostPlace = _settings.GhostPlacement;
        var notePlace = _settings.NotePlacement;

        using var deltaFont = new Font("Segoe UI Semibold", _unit * 2.2f * deltaPlace.Scale);
        using var labelFont = new Font("Segoe UI", _unit * 0.72f * deltaPlace.Scale);
        using var ghostFont = new Font("Segoe UI Semibold", _unit * 1.2f * ghostPlace.Scale);
        using var noteFont = new Font("Segoe UI", _unit * 0.72f * notePlace.Scale);

        if (_nurNotiz)
        {
            if (NoteActive) { Draw(g, notePlace, _note, ahead, string.Empty, noteFont, noteFont); }
            return;
        }

        // DIE AMPEL.
        //
        // Die Farbe traegt die FLAECHE, nicht die Schrift: im Augenwinkel ist ein
        // Farbfeld zu erkennen, eine Ziffernfarbe nicht. Die Schrift steht deshalb
        // in einer eigenen, frei waehlbaren Farbe darauf -- schwarz in der Vorgabe,
        // weil alle drei Flaechenfarben hell sind.
        var schrift = Parse(_settings.HudColorGhostText, Color.Black);
        var warnAb = (float)Math.Clamp(_settings.GhostWarnSeconds, 0, 60);
        if (_ghostLeft > warnAb)
        {
            // Ruhig: niemand kann dich anfassen.
            Draw(g, ghostPlace, $"GHOST {_ghostLeft:0.0}", schrift, string.Empty,
                 ghostFont, labelFont,
                 Parse(_settings.HudColorGhost, ColorTranslator.FromHtml("#22b8e6")));
        }
        else if (_ghostLeft > 0)
        {
            // Warnung: gleich ist es vorbei.
            Draw(g, ghostPlace, $"GHOST {_ghostLeft:0.0}", schrift, string.Empty,
                 ghostFont, labelFont,
                 Parse(_settings.HudColorGhostWarn, ColorTranslator.FromHtml("#ffd21e")));
        }
        else if (_ghostLeft > -(float)_settings.PopInSeconds)
        {
            // Und jetzt tauchen sie wieder auf, einer nach dem anderen.
            Draw(g, ghostPlace, $"POP-IN {-_ghostLeft:0.0}", schrift, string.Empty,
                 ghostFont, labelFont,
                 Parse(_settings.HudColorPopIn, ColorTranslator.FromHtml("#ff8c1a")));
        }

        if (_settings.HudInputs)
        {
            DrawInputs(g, _settings.InputsPlacement, labelFont);
        }

        if (DateTime.UtcNow < _noteUntil && _note.Length > 0)
        {
            Draw(g, notePlace, _note, ahead, string.Empty, noteFont, noteFont);
        }

        if (_delta is not null || !string.IsNullOrEmpty(_label))
        {
            // Vorzeichen immer mitschreiben -- ohne "+" ist eine Zahl im Augenwinkel
            // nicht als Rueckstand zu erkennen.
            var colour = _delta is null ? neutral : (_delta < 0 ? ahead : behind);
            var height = Draw(g, deltaPlace, Format(_delta), colour, _label,
                              deltaFont, labelFont);

            if (!string.IsNullOrEmpty(_secondLabel))
            {
                // Der zweite Vergleich sitzt direkt darunter, kleiner: er beantwortet
                // eine andere Frage und darf die erste nicht ueberstrahlen.
                var second = _settings.DeltaPlacement with
                {
                    Y = deltaPlace.Y + height / Math.Max(1, Height) + 0.004f,
                    Scale = deltaPlace.Scale * 0.62f,
                };
                using var secondFont = new Font("Segoe UI Semibold", _unit * 2.2f * second.Scale);
                using var secondLabelFont = new Font("Segoe UI", _unit * 0.72f * second.Scale);
                var secondColour = _secondDelta is null
                    ? neutral
                    : (_secondDelta < 0 ? ahead : behind);
                Draw(g, second, Format(_secondDelta), secondColour, _secondLabel,
                     secondFont, secondLabelFont);
            }
        }
    }
}
