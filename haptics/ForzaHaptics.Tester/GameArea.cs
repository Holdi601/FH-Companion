using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace ForzaHaptics.Tester;

/// <summary>
/// Where the game is drawn, in screen pixels -- at any resolution, on any monitor.
/// </summary>
/// <remarks>
/// Everything that sits on or reads the game used to take the PRIMARY screen: the
/// panels, the HUDs, the screen reader. That is right exactly when the game runs
/// full screen on the primary monitor. On a second monitor the overlays hung over
/// the desktop and the reader photographed the wrong screen; in a window both were
/// off by the window's position and size.
///
/// The answer here is the game window's client area. When the window reports a
/// degenerate area (minimised, still loading) it is the monitor the window is on,
/// and without a window it is the primary screen -- the old behaviour, as the last
/// resort instead of the only one.
///
/// Measured layouts are 16:9. <see cref="SixteenNine"/> gives the centred 16:9 part
/// of any area; that Forza draws its menus there at 21:9 or 16:10 is an ASSUMPTION
/// (the user's display is 16:9, so it cannot be checked here).
/// </remarks>
internal static class GameArea
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Pt { public int X, Y; }

    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out Rect r);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref Pt p);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr prev);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr dst, int dx, int dy, int dw, int dh,
                                                                  IntPtr src, int sx, int sy, int sw, int sh, int rop);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out Rect r);

    /// <summary>
    /// Fuellt das Fenster im Vordergrund seinen ganzen Schirm -- ein Spiel im Vollbild?
    /// </summary>
    /// <remarks>
    /// Fuer die Pruefungen, die Fenster auf den Schirm legen und ihn fotografieren. Ueber
    /// einem Vollbild zeigt eine Aufnahme nur das Spiel, und die Probefenster stoeren
    /// jemanden, der gerade spielt -- am 2026-09-25 erst Forza, dann Counter-Strike.
    /// Der Schreibtisch selbst (Progman, WorkerW) zaehlt nicht.
    /// </remarks>
    public static bool VollbildVorne(out string wer)
    {
        wer = string.Empty;
        try
        {
            var h = GetForegroundWindow();
            if (h == IntPtr.Zero || !GetWindowRect(h, out var r)) { return false; }
            var klasse = new StringBuilder(64);
            GetClassNameW(h, klasse, 64);
            if (klasse.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd") { return false; }
            var schirm = System.Windows.Forms.Screen.FromHandle(h).Bounds;
            var voll = r.Left <= schirm.Left && r.Top <= schirm.Top
                       && r.Right >= schirm.Right && r.Bottom >= schirm.Bottom;
            if (!voll) { return false; }
            GetWindowThreadProcessId(h, out var pid);
            try { wer = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
            catch (Exception) { wer = "a fullscreen window"; }
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>The height every screen layout in this app was measured at.</summary>
    public const int ReferenceHeight = 1080;

    private static readonly object Gate = new();
    private static DateTime _cachedAt = DateTime.MinValue;
    private static string _cachedFor = string.Empty;
    private static Rectangle _cached;

    /// <summary>The game's drawing area; cached for two seconds.</summary>
    /// <remarks>
    /// Asked on every screen-reader poll. Walking the process table and the window
    /// list is cheap but not free, and a window does not move between two polls.
    /// </remarks>
    public static Rectangle Find(string? processName)
    {
        // KONSOLENMODUS MIT VIDEOQUELLE (seit 2026-09-28): die "Spielflaeche" ist das
        // Bild der Quelle, in dessen eigenen Koordinaten -- Capture schneidet daraus aus.
        if (Rivals.Bildquellen.Aktiv is { } quelle)
        {
            // Nicht bild.Width: das Bild zeichnet vielleicht gerade ein Leser (siehe Bildquellen.Mit).
            // UND NUR EIN BILD, DAS EIN SPIELBILD SEIN KANN (seit 2026-09-29): ein falsch
            // gewaehltes, winziges Fenster lieferte ein Bild von wenigen Pixeln Breite. Darauf
            // gerechnet wurde jede HUD-Anzeige zigtausend Pixel breit, und der HUD-Editor
            // stuerzte beim Ziehen ab ("'450' cannot be greater than -40378"). Am PC gilt
            // dieselbe Grenze schon lange (Compute: unter 640x360 der ganze Schirm).
            return Rivals.Bildquellen.Groesse(quelle.Neuestes()) is { Width: >= 320, Height: >= 180 } g
                ? new Rectangle(Point.Empty, g)
                : new Rectangle(0, 0, 1920, 1080);
        }
        var name = string.IsNullOrWhiteSpace(processName) ? GameWatch.DefaultProcessName : processName!;
        if (!string.IsNullOrWhiteSpace(FensterTitel)) { name = "title:" + FensterTitel; }
        lock (Gate)
        {
            if (name == _cachedFor && (DateTime.UtcNow - _cachedAt).TotalSeconds < 2)
            {
                return _cached;
            }
            _cached = Compute(name);
            _cachedFor = name;
            _cachedAt = DateTime.UtcNow;
            return _cached;
        }
    }

    /// <summary>Forget the cached area, e.g. when the game has just started.</summary>
    public static void Invalidate()
    {
        lock (Gate) { _cachedAt = DateTime.MinValue; }
    }

    /// <summary>
    /// Konsolenmodus, Quelle "Fenster": statt des Spiels das Fenster, dessen Titel dies
    /// enthaelt (OBS-Projektor, Software der Aufnahmekarte, Xbox-App). null: das Spiel.
    /// </summary>
    public static string? FensterTitel { get; set; }

    private static Rectangle Compute(string name)
    {
        var fenster = name.StartsWith("title:", StringComparison.Ordinal)
            ? FensterMitTitel(name["title:".Length..])
            : GameWindow(name);
        if (fenster != IntPtr.Zero)
        {
            if (!IsIconic(fenster) && GetClientRect(fenster, out var r))
            {
                var p = new Pt();
                if (ClientToScreen(fenster, ref p))
                {
                    var flaeche = new Rectangle(p.X, p.Y, r.Right - r.Left, r.Bottom - r.Top);
                    if (flaeche.Width >= 640 && flaeche.Height >= 360)
                    {
                        return flaeche;
                    }
                }
            }
            return Screen.FromHandle(fenster).Bounds;
        }
        return Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
    }

    /// <summary>
    /// The game's visible top-level "App" window -- the same class the navigator
    /// looks for. The game also owns a ForzaFullscreenShadeWindow, which is not it.
    /// </summary>
    private static IntPtr GameWindow(string name)
    {
        uint[] pids;
        try
        {
            pids = Process.GetProcessesByName(name).Select(p => (uint)p.Id).ToArray();
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
        if (pids.Length == 0)
        {
            return IntPtr.Zero;
        }
        var gefunden = IntPtr.Zero;
        var klasse = new StringBuilder(64);
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var pid);
            if (!pids.Contains(pid) || !IsWindowVisible(h))
            {
                return true;
            }
            klasse.Clear();
            GetClassNameW(h, klasse, klasse.Capacity);
            if (klasse.ToString() == "App")
            {
                gefunden = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return gefunden;
    }

    /// <summary>Das erste sichtbare Hauptfenster, dessen Titel <paramref name="teil"/> enthaelt.</summary>
    private static IntPtr FensterMitTitel(string teil)
    {
        var gefunden = IntPtr.Zero;
        var titel = new StringBuilder(256);
        var eigenes = Process.GetCurrentProcess().Id;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) { return true; }
            GetWindowThreadProcessId(h, out var pid);
            if (pid == eigenes) { return true; }
            titel.Clear();
            GetWindowTextW(h, titel, titel.Capacity);
            if (titel.ToString().Contains(teil, StringComparison.OrdinalIgnoreCase))
            {
                gefunden = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return gefunden;
    }

    /// <summary>The centred 16:9 part of an area; the area itself when it is 16:9.</summary>
    public static Rectangle SixteenNine(Rectangle area)
    {
        var w = area.Width;
        var h = area.Height;
        if ((long)w * 9 > (long)h * 16)
        {
            w = (int)Math.Round(h * 16.0 / 9.0);
        }
        else if ((long)w * 9 < (long)h * 16)
        {
            h = (int)Math.Round(w * 9.0 / 16.0);
        }
        return new Rectangle(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
    }

    /// <summary>How much bigger than 1080p the area is drawn: 0.667 at 720p, 4 at 8K.</summary>
    public static float ResolutionScale(Rectangle area) =>
        Math.Max(0.25f, SixteenNine(area).Height / (float)ReferenceHeight);

    /// <summary>
    /// Capture a screen rectangle straight into a bitmap of the given size.
    /// </summary>
    /// <remarks>
    /// GDI StretchBlt with HALFTONE averages every source pixel when shrinking, so a
    /// 16K region never needs a 16K bitmap -- and text arrives at the size the OCR
    /// was tuned on instead of eight times it (Windows OCR also refuses images past
    /// its maximum dimension). Growing a 720p region works the same way.
    /// </remarks>
    public static Bitmap Capture(Rectangle source, Size target)
    {
        // Aus der Videoquelle statt vom Schirm (Konsolenmodus), siehe Find.
        if (Rivals.Bildquellen.Aktiv is { } quelle)
        {
            var uhrQ = System.Diagnostics.Stopwatch.StartNew();
            try { return Rivals.Bildquellen.Ausschnitt(quelle.Neuestes(), source, target); }
            finally { Leistung.Griff(uhrQ.ElapsedTicks); }
        }
        // 24 Bit, nicht 32: GDI schreibt keinen Alphakanal, ein ARGB-Ziel kaeme mit
        // Alpha 0 zurueck -- voellig durchsichtig fuer alles, was danach zeichnet.
        var uhr = System.Diagnostics.Stopwatch.StartNew();
        var bmp = new Bitmap(Math.Max(1, target.Width), Math.Max(1, target.Height),
                             PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        var dst = g.GetHdc();
        var src = GetDC(IntPtr.Zero);
        try
        {
            SetStretchBltMode(dst, 4);
            SetBrushOrgEx(dst, 0, 0, IntPtr.Zero);
            if (!StretchBlt(dst, 0, 0, bmp.Width, bmp.Height, src,
                            source.X, source.Y, source.Width, source.Height, 0x00CC0020))
            {
                throw new InvalidOperationException("StretchBlt failed");
            }
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, src);
            g.ReleaseHdc(dst);
            Leistung.Griff(uhr.ElapsedTicks);
        }
        return bmp;
    }
}
