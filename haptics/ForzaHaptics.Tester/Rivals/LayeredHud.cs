using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Ein Overlay-Fenster mit Alpha JE PIXEL, das nur so gross ist wie das, was es zeigt.
/// </summary>
/// <remarks>
/// ## Warum es diese Klasse gibt
///
/// Bis zum 2026-09-25 setzten der Umriss-Streifen und die Autonotiz selbst
/// <c>WS_EX_LAYERED</c> -- und riefen danach weder SetLayeredWindowAttributes noch
/// UpdateLayeredWindow auf. Ein solches Fenster zeigt Windows GAR NICHT an. Beide
/// waren also seit ihrem Bau unsichtbar. Aufgefallen ist es erst im Spiel: jeder
/// Test zeichnete sie in ein Bild, keiner schaute auf den Schirm, und aus jeder
/// Bildschirmaufnahme halten sie sich ohnehin heraus (WDA_EXCLUDEFROMCAPTURE).
///
/// Die Technik ist die des Delta-Streifens (siehe DeltaHud): eine 32-Bit-Flaeche
/// mit vormultipliziertem Alpha, per UpdateLayeredWindow dem Fenstermanager
/// uebergeben. Durchsichtige Platten sind damit wirklich durchsichtig.
///
/// ## Nur so gross wie der Inhalt
///
/// Der Delta-Streifen belegt den ganzen Schirm -- auf 8K 132 MB. Hier wird nur der
/// Block belegt, den <see cref="Extent"/> nennt: gerechnet wird weiter in
/// Koordinaten der ganzen Spielflaeche (dieselbe Rechnung wie in der Vorschau des
/// Einrichtungsreiters), gezeichnet und uebergeben nur das Rechteck darum.
/// </remarks>
internal abstract class LayeredHud : Form, IAufnahmeQuelle
{
    private const int WsExLayered = 0x00080000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const uint WdaExcludeFromCapture = 0x00000011;
    private const int UlwAlpha = 0x00000002;
    private const byte AcSrcOver = 0x00;
    private const byte AcSrcAlpha = 0x01;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(
        System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

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

    private Rectangle _flaeche;
    private IntPtr _memDc;
    private IntPtr _dib;
    private IntPtr _oldBitmap;
    private Bitmap? _surface;
    private Size _surfaceSize;
    private bool _pushFailed;

    protected LayeredHud(Rectangle flaeche)
    {
        _flaeche = flaeche;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        // Das Fenster selbst ist nur so gross wie sein Block; Lage und Groesse setzt
        // UpdateLayeredWindow bei jedem Zeichnen.
        Bounds = new Rectangle(flaeche.X, flaeche.Y, 1, 1);
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
        base.SetVisibleCore(value && OverlayAusgabe.ImSpiel);
    }

    public void AusgabeAnwenden()
    {
        if (IsDisposed) { return; }
        base.SetVisibleCore(_gewollt && OverlayAusgabe.ImSpiel);
        if (base.Visible) { TopMost = true; }
    }

    /// <summary>Reihenfolge im Aufnahmefenster (siehe IAufnahmeQuelle).</summary>
    public virtual int Ebene => 2;

    public void MaleFuerAufnahme(Graphics g)
    {
        if (Gewollt) { Draw(g); }
    }

    /// <summary>Die Spielflaeche in Bildschirmkoordinaten setzen -- und neu zeichnen.</summary>
    /// <remarks>Methode, keine Eigenschaft: eine setzbare oeffentliche Eigenschaft auf
    /// einem Control will der WinForms-Pruefer entwurfszeit-serialisierbar sehen.</remarks>
    public void SetArea(Rectangle flaeche)
    {
        if (_flaeche == flaeche) { return; }
        _flaeche = flaeche;
        Render();
    }

    /// <summary>Die Groesse der Spielflaeche -- die Grundlage jeder Rechnung.</summary>
    protected Size AreaSize => _flaeche.Size;

    /// <summary>Wo auf der Spielflaeche gezeichnet wird. Leer: nichts zu zeigen.</summary>
    protected abstract Rectangle Extent(Graphics messung);

    /// <summary>Zeichnen, in Koordinaten der ganzen Spielflaeche.</summary>
    protected abstract void Draw(Graphics g);

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
        if (!_aufnahmeErlaubt) { try { SetWindowDisplayAffinity(Handle, WdaExcludeFromCapture); } catch (Exception) { } }
        Render();
    }

    private bool _aufnahmeErlaubt;

    /// <summary>
    /// Nur fuer den Selbsttest: vor dem ersten Zeigen aufrufen, dann haelt sich das
    /// Fenster NICHT aus Bildschirmaufnahmen heraus -- sonst liesse sich gar nicht
    /// pruefen, ob es wirklich auf dem Schirm ankommt.
    /// </summary>
    internal void AllowCaptureForTest() => _aufnahmeErlaubt = true;

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (base.Visible) { Render(); }
    }

    // Ein Fenster, das UpdateLayeredWindow benutzt, bekommt kein WM_PAINT, das es
    // braucht -- und darf auch keinen Hintergrund loeschen, sonst blitzt es schwarz.
    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e) { }

    /// <summary>Neu rechnen, zeichnen und dem Fenstermanager uebergeben.</summary>
    public void Render()
    {
        // Nicht sichtbar ueber dem Spiel: nichts uebergeben. Das Aufnahmefenster
        // zeichnet selbst (MaleFuerAufnahme).
        if (!IsHandleCreated || IsDisposed || !base.Visible) { return; }
        var uhr = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            Rectangle block;
            using (var messbild = new Bitmap(1, 1))
            using (var mess = Graphics.FromImage(messbild))
            {
                mess.TextRenderingHint = TextRenderingHint.AntiAlias;
                block = Extent(mess);
            }
            // Nichts zu zeigen: ein durchsichtiges Pixel, damit kein alter Inhalt stehen bleibt.
            if (block.Width <= 0 || block.Height <= 0) { block = new Rectangle(0, 0, 1, 1); }

            if (_surface is null || _surfaceSize != block.Size) { BuildSurface(block.Size); }
            if (_surface is null || _memDc == IntPtr.Zero) { return; }

            using (var g = Graphics.FromImage(_surface))
            {
                g.Clear(Color.Transparent);
                // KEIN ClearType auf einer durchsichtigen Flaeche -- es rechnet mit
                // einem deckenden Hintergrund und hinterlaesst farbige Raender.
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TranslateTransform(-block.X, -block.Y);
                Draw(g);
            }

            var lage = new Point(_flaeche.X + block.X, _flaeche.Y + block.Y);
            var groesse = block.Size;
            var quelle = new Point(0, 0);
            var mischen = new BlendFunction
            {
                BlendOp = AcSrcOver,
                SourceConstantAlpha = 255,
                AlphaFormat = AcSrcAlpha,
            };
            var schirmDc = GetDC(IntPtr.Zero);
            bool gelungen;
            var fehler = 0;
            try
            {
                gelungen = UpdateLayeredWindow(Handle, schirmDc, ref lage, ref groesse,
                                               _memDc, ref quelle, 0, ref mischen, UlwAlpha);
                if (!gelungen) { fehler = System.Runtime.InteropServices.Marshal.GetLastWin32Error(); }
            }
            finally { ReleaseDC(IntPtr.Zero, schirmDc); }

            // EINMAL melden: ein unsichtbares Overlay laesst sich sonst durch nichts belegen.
            if (!gelungen && !_pushFailed)
            {
                _pushFailed = true;
                OverlayController.WriteDiagnostic(
                    $"{GetType().Name}: UpdateLayeredWindow scheiterte (Win32 {fehler}) -- unsichtbar.");
            }
            else if (gelungen && _pushFailed)
            {
                _pushFailed = false;
                OverlayController.WriteDiagnostic($"{GetType().Name}: UpdateLayeredWindow geht wieder.");
            }
        }
        catch (Exception)
        {
            // Ein Anzeigefehler darf nie das Rennen stoeren.
        }
        Leistung.Gezeichnet(uhr.ElapsedTicks);
    }

    /// <summary>Fuer den Selbsttest: hat das letzte Uebergeben geklappt?</summary>
    internal bool LastPushFailed => _pushFailed;

    private void BuildSurface(Size groesse)
    {
        DisposeSurface();
        var kopf = new BitmapInfoHeader
        {
            biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<BitmapInfoHeader>(),
            biWidth = Math.Max(1, groesse.Width),
            // Negativ: von oben nach unten, wie GDI+ zeichnet.
            biHeight = -Math.Max(1, groesse.Height),
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,
        };
        var schirmDc = GetDC(IntPtr.Zero);
        try
        {
            _memDc = CreateCompatibleDC(schirmDc);
            _dib = CreateDIBSection(schirmDc, ref kopf, 0, out var bits, IntPtr.Zero, 0);
            if (_dib == IntPtr.Zero || _memDc == IntPtr.Zero) { return; }
            _oldBitmap = SelectObject(_memDc, _dib);
            // PArgb: vormultipliziert, genau wie UpdateLayeredWindow es erwartet.
            _surface = new Bitmap(Math.Max(1, groesse.Width), Math.Max(1, groesse.Height),
                                  Math.Max(1, groesse.Width) * 4,
                                  System.Drawing.Imaging.PixelFormat.Format32bppPArgb, bits);
            _surfaceSize = groesse;
        }
        finally { ReleaseDC(IntPtr.Zero, schirmDc); }
    }

    private void DisposeSurface()
    {
        _surface?.Dispose();
        _surface = null;
        _surfaceSize = Size.Empty;
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
        if (disposing) { DisposeSurface(); }
        base.Dispose(disposing);
    }
}
