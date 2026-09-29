using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Ein Fenster als Bildquelle -- ueber Windows Graphics Capture, NICHT vom Bildschirm
/// (seit 2026-09-28): Xbox Remote Play, ein OBS-Projektor, die Vorschau einer
/// Aufnahmekarte.
/// </summary>
/// <remarks>
/// Vorher kopierte die App die Pixel an der Stelle des Fensters vom Schirm. Lag das
/// Dashboard darueber, las sie das Dashboard. Windows Graphics Capture bekommt das
/// Bild des Fensters selbst vom Fenstermanager -- verdeckt oder auf einem anderen
/// Schirm, egal. Nur MINIMIERT liefert ein Fenster nichts.
///
/// Windows zeichnet waehrenddessen einen gelben Rahmen um das Fenster: das ist die
/// Anzeige, dass ein Programm es aufnimmt, und bei dieser SDK-Fassung nicht
/// abzuschalten.
///
/// Aus dem Bild wird die Innenflaeche geschnitten (ohne Titelleiste), und davon das
/// 16:9-Spielbild, wenn es mit Balken darin liegt -- siehe <see cref="Bildquellen.Spielbild"/>.
/// </remarks>
internal sealed unsafe class FensterQuelle : IBildquelle, IFensterBild
{
    /// <summary>Was die Vorschau sagt, wenn das Fenster fehlt -- statt "Window not found: ...".</summary>
    internal string? NichtGefunden { get; init; }

    private static readonly Guid IidInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IidItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IidDxgiDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

    private readonly object _schloss = new();
    private readonly string _titel;
    private readonly Func<IntPtr> _finden;
    private readonly System.Threading.Timer _wache;
    private IntPtr _fenster;
    private IDirect3DDevice? _geraet;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _sitzung;
    private Direct3D11CaptureFrame? _letzter;
    private Windows.Graphics.SizeInt32 _groesse;
    private Bitmap? _bild;
    // Das vorige Bild lebt einen Umlauf laenger: ein Leser, der es gerade zeichnet,
    // soll es nicht unter der Hand verlieren.
    private Bitmap? _altesBild;
    private DateTime _bildAm = DateTime.MinValue;
    // Wo im aufgenommenen Bild das Spielbild zuletzt lag (Bildkoordinaten) -- fuer den
    // HUD ueber dem Fenster (Schirmflaeche).
    private Rectangle? _spielImBild;
    private bool _zu;

    public string Beschreibung { get; private set; }

    /// <summary>Kann dieses Windows Fenster so aufnehmen? (ab Windows 10 1903)</summary>
    public static bool Unterstuetzt
    {
        get
        {
            try { return GraphicsCaptureSession.IsSupported(); }
            catch (Exception) { return false; }
        }
    }

    public FensterQuelle(string titel) : this(titel, () => Fenster.Finden(titel))
    {
    }

    /// <summary>Mit eigener Suche -- etwa der Projektor von OBS, der je Sprache anders heisst.</summary>
    public FensterQuelle(string titel, Func<IntPtr> finden)
    {
        _titel = titel;
        _finden = finden;
        Beschreibung = "window: looking for \"" + titel + "\"";
        // Alle zwei Sekunden nachsehen: das Fenster kann spaeter kommen, geschlossen
        // und neu geoeffnet werden.
        _wache = new System.Threading.Timer(_ => Pruefen(), null, 0, 2000);
    }

    private void Pruefen()
    {
        lock (_schloss)
        {
            if (_zu) { return; }
            if (_sitzung is not null && _fenster != IntPtr.Zero && Fenster.IsWindow(_fenster))
            {
                Beschreibung = Fenster.IsIconic(_fenster)
                    ? Loc.T("The window is minimized. Restore it; it may stay behind other windows.")
                    : "window: " + Fenster.Titel(_fenster);
                return;
            }
            Schliessen();
            var h = _finden();
            if (h == IntPtr.Zero)
            {
                Beschreibung = NichtGefunden ?? string.Format(Loc.T("Window not found: “{0}”"), _titel);
                return;
            }
            try
            {
                Starten(h);
                Beschreibung = "window: " + Fenster.Titel(h);
            }
            catch (Exception e)
            {
                Schliessen();
                Beschreibung = "window capture failed: " + e.Message;
            }
        }
    }

    private void Starten(IntPtr fenster)
    {
        _geraet ??= D3dGeraet();
        var item = ItemFuer(fenster);
        _item = item;
        _groesse = item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _geraet, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _groesse);
        _pool.FrameArrived += Angekommen;
        _sitzung = _pool.CreateCaptureSession(item);
        try { _sitzung.IsCursorCaptureEnabled = false; } catch (Exception) { }
        _sitzung.StartCapture();
        _fenster = fenster;
    }

    /// <summary>
    /// Nur das neueste Bild behalten -- umgewandelt wird erst, wenn ein Leser fragt.
    /// </summary>
    private void Angekommen(Direct3D11CaptureFramePool pool, object _)
    {
        Direct3D11CaptureFrame? bild;
        try { bild = pool.TryGetNextFrame(); }
        catch (Exception) { return; }
        if (bild is null) { return; }
        lock (_schloss)
        {
            if (_zu || !ReferenceEquals(pool, _pool))
            {
                bild.Dispose();
                return;
            }
            _letzter?.Dispose();
            _letzter = bild;
            var neu = bild.ContentSize;
            if (neu.Width > 0 && neu.Height > 0 && (neu.Width != _groesse.Width || neu.Height != _groesse.Height))
            {
                // Das Fenster hat seine Groesse geaendert: der Vorrat muss mitwachsen.
                _groesse = neu;
                try { pool.Recreate(_geraet, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _groesse); }
                catch (Exception) { }
            }
        }
    }

    public Bitmap? Neuestes()
    {
        lock (_schloss)
        {
            if (_letzter is null) { return _bild; }
            // Hoechstens alle 150 ms neu umwandeln -- ein Lesevorgang fragt mehrmals.
            if (_bild is not null && DateTime.UtcNow - _bildAm < TimeSpan.FromMilliseconds(150)) { return _bild; }
            try
            {
                using var weich = SoftwareBitmap.CreateCopyFromSurfaceAsync(_letzter.Surface, BitmapAlphaMode.Ignore)
                                                .AsTask().GetAwaiter().GetResult();
                using var roh = Bildquellen.AlsBitmap(weich, ohneAlpha: true);
                var inhalt = _letzter.ContentSize;
                var flaeche = Innenflaeche(new Rectangle(0, 0,
                    Math.Min(roh.Width, Math.Max(1, inhalt.Width)), Math.Min(roh.Height, Math.Max(1, inhalt.Height))));
                var spiel = Bildquellen.Spielbild(roh, flaeche);
                _spielImBild = spiel;
                var neu = roh.Clone(spiel, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                Bildquellen.Entsorgen(_altesBild);
                _altesBild = _bild;
                _bild = neu;
                _bildAm = DateTime.UtcNow;
            }
            catch (Exception)
            {
                // Ein verlorenes Bild ist kein Fehler; das naechste kommt.
            }
            return _bild;
        }
    }

    /// <summary>
    /// Das Spielbild auf dem Schirm: die Lage des Fensters plus die Stelle, an der das
    /// 16:9-Bild zuletzt im Fensterbild lag. Vor dem ersten Bild: die Innenflaeche, mittig 16:9.
    /// </summary>
    public Rectangle? Schirmflaeche
    {
        get
        {
            lock (_schloss)
            {
                var h = _fenster;
                if (h == IntPtr.Zero || !Fenster.IsWindow(h) || Fenster.IsIconic(h)) { return null; }
                if (Fenster.DwmGetWindowAttribute(h, 9, out var aussen, sizeof(Fenster.RECT)) != 0) { return null; }
                var imBild = _spielImBild;
                if (imBild is null)
                {
                    var ganz = new Rectangle(0, 0, aussen.Right - aussen.Left, aussen.Bottom - aussen.Top);
                    var innen = Innenflaeche(ganz);
                    var b = Math.Min(innen.Width, (int)Math.Round(innen.Height * 16.0 / 9));
                    var hh = Math.Min(innen.Height, (int)Math.Round(b * 9.0 / 16));
                    imBild = new Rectangle(innen.X + ((innen.Width - b) / 2), innen.Y + ((innen.Height - hh) / 2), b, hh);
                }
                var r = imBild.Value;
                return new Rectangle(aussen.Left + r.X, aussen.Top + r.Y, r.Width, r.Height);
            }
        }
    }

    /// <summary>Ist das aufgenommene Fenster das aktive? (Der Controller spielt nur dann hinein.)</summary>
    public bool IstVorne
    {
        get
        {
            var h = _fenster;
            return h != IntPtr.Zero && !Fenster.IsIconic(h) && Fenster.GetAncestor(Fenster.GetForegroundWindow(), 2) == h;
        }
    }

    /// <summary>Die Innenflaeche des Fensters (ohne Titelleiste und Rand) im aufgenommenen Bild.</summary>
    private Rectangle Innenflaeche(Rectangle ganz)
    {
        try
        {
            if (Fenster.DwmGetWindowAttribute(_fenster, 9, out var aussen, sizeof(Fenster.RECT)) == 0
                && Fenster.GetClientRect(_fenster, out var innen))
            {
                var p = new Fenster.POINT();
                if (Fenster.ClientToScreen(_fenster, ref p))
                {
                    var r = new Rectangle(p.X - aussen.Left, p.Y - aussen.Top, innen.Right - innen.Left,
                                          innen.Bottom - innen.Top);
                    r.Intersect(ganz);
                    if (r.Width >= 320 && r.Height >= 180) { return r; }
                }
            }
        }
        catch (Exception)
        {
        }
        return ganz;
    }

    private void Schliessen()
    {
        try { _sitzung?.Dispose(); } catch (Exception) { }
        try
        {
            if (_pool is not null) { _pool.FrameArrived -= Angekommen; }
            _pool?.Dispose();
        }
        catch (Exception) { }
        try { _letzter?.Dispose(); } catch (Exception) { }
        _sitzung = null;
        _pool = null;
        _letzter = null;
        _item = null;
        _fenster = IntPtr.Zero;
    }

    public void Dispose()
    {
        _wache.Dispose();
        lock (_schloss)
        {
            _zu = true;
            Schliessen();
            Bildquellen.Entsorgen(_bild);
            Bildquellen.Entsorgen(_altesBild);
            _bild = null;
            _altesBild = null;
            try { (_geraet as IDisposable)?.Dispose(); } catch (Exception) { }
            _geraet = null;
        }
    }

    // ------------------------------------------------------------------ Windows-Anbindung

    /// <summary>Ein Aufnahme-Objekt fuer ein Fenster (IGraphicsCaptureItemInterop.CreateForWindow).</summary>
    private static GraphicsCaptureItem ItemFuer(IntPtr fenster)
    {
        var fabrik = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var iid = IidInterop;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(fabrik.ThisPtr, ref iid, out var interop));
        try
        {
            // vtable: IUnknown (3 Eintraege), dann CreateForWindow, CreateForMonitor.
            var tabelle = *(IntPtr**)interop;
            var erzeuge = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)tabelle[3];
            var itemIid = IidItem;
            IntPtr zeiger;
            Marshal.ThrowExceptionForHR(erzeuge(interop, fenster, &itemIid, &zeiger));
            try { return GraphicsCaptureItem.FromAbi(zeiger); }
            finally { Marshal.Release(zeiger); }
        }
        finally
        {
            Marshal.Release(interop);
        }
    }

    /// <summary>Ein Direct3D-11-Geraet als WinRT-IDirect3DDevice fuer den Bildvorrat.</summary>
    private static IDirect3DDevice D3dGeraet()
    {
        const int Hardware = 1;
        const uint BgraSupport = 0x20;
        var hr = D3D11CreateDevice(IntPtr.Zero, Hardware, IntPtr.Zero, BgraSupport, IntPtr.Zero, 0, 7,
                                   out var d3d, out _, out var kontext);
        if (hr < 0)
        {
            // Ohne Grafikkarte (Fernsitzung, VM): der Software-Rasterer.
            const int Warp = 5;
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(IntPtr.Zero, Warp, IntPtr.Zero, BgraSupport, IntPtr.Zero,
                                                          0, 7, out d3d, out _, out kontext));
        }
        try
        {
            var iid = IidDxgiDevice;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3d, ref iid, out var dxgi));
            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var winrt));
                try { return MarshalInterface<IDirect3DDevice>.FromAbi(winrt); }
                finally { Marshal.Release(winrt); }
            }
            finally
            {
                Marshal.Release(dxgi);
            }
        }
        finally
        {
            if (kontext != IntPtr.Zero) { Marshal.Release(kontext); }
            Marshal.Release(d3d);
        }
    }

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
                                                IntPtr featureLevels, uint featureLevelCount, uint sdkVersion,
                                                out IntPtr device, out int featureLevel, out IntPtr context);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
}

/// <summary>Die sichtbaren Fenster anderer Programme -- zum Auswaehlen und Wiederfinden.</summary>
internal static class Fenster
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int X, Y; }

    private delegate bool Aufzaehlen(IntPtr h, IntPtr l);

    [DllImport("user32.dll")] private static extern bool EnumWindows(Aufzaehlen f, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern int GetWindowTextLengthW(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] internal static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] internal static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int wert, int size);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(IntPtr h, int index);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? klasse, string? titel);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint zugriff, bool erben, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr prozess, int flags, StringBuilder pfad, ref int laenge);

    internal static string Titel(IntPtr h)
    {
        var n = GetWindowTextLengthW(h);
        if (n <= 0) { return string.Empty; }
        var s = new StringBuilder(n + 1);
        GetWindowTextW(h, s, s.Capacity);
        return s.ToString();
    }

    /// <summary>Sichtbare Fenster mit Titel, ohne die eigenen und ohne versteckte ("cloaked") Fenster.</summary>
    internal static List<(IntPtr Handle, string Titel)> Sichtbare()
    {
        var eigene = (uint)Environment.ProcessId;
        var raus = new List<(IntPtr, string)>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) { return true; }
            GetWindowThreadProcessId(h, out var pid);
            if (pid == eigene) { return true; }
            // Unsichtbar gemachte Fenster (Apps im Hintergrund, andere Desktops)
            if (DwmGetWindowAttribute(h, 14, out int versteckt, sizeof(int)) == 0 && versteckt != 0) { return true; }
            var t = Titel(h);
            if (t.Length > 0) { raus.Add((h, t)); }
            return true;
        }, IntPtr.Zero);
        return raus;
    }

    /// <summary>
    /// Das sichtbare Fenster zu einem Titel(-teil) -- sonst IntPtr.Zero.
    /// </summary>
    /// <remarks>
    /// Genau gleich vor "beginnt mit" vor "enthaelt": die Xbox-App hat neben ihrem
    /// Hauptfenster "XBOX" auch Chatfenster wie "Freund - XBOX". Wer "Xbox" eintippt,
    /// meint das Hauptfenster.
    /// </remarks>
    internal static IntPtr Finden(string teil)
    {
        if (string.IsNullOrWhiteSpace(teil)) { return IntPtr.Zero; }
        var t = teil.Trim();
        var alle = Sichtbare();
        foreach (var pruefe in new Func<string, bool>[]
                 {
                     x => x.Equals(t, StringComparison.OrdinalIgnoreCase),
                     x => x.StartsWith(t, StringComparison.OrdinalIgnoreCase),
                     x => x.Contains(t, StringComparison.OrdinalIgnoreCase),
                 })
        {
            foreach (var (h, titel) in alle)
            {
                if (pruefe(titel)) { return h; }
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Ein Projektorfenster von OBS: ein Fenster von obs64/obs32, das nicht das
    /// Hauptfenster ist (das heisst "OBS 32.x - Profil ..."). Nach dem Prozess und nicht
    /// nach dem Titel gesucht -- "Windowed Projector" heisst auf Deutsch "Fensterprojektor".
    /// </summary>
    internal static IntPtr ObsProjektor()
    {
        foreach (var (h, t) in Sichtbare())
        {
            if (t.StartsWith("OBS", StringComparison.OrdinalIgnoreCase)) { continue; }
            GetWindowThreadProcessId(h, out var pid);
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById((int)pid);
                if (p.ProcessName.StartsWith("obs", StringComparison.OrdinalIgnoreCase)) { return h; }
            }
            catch (Exception)
            {
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Das Fenster, in dem Discord einen Strom zeigt: ein herausgeloestes Fenster von
    /// Discord, wenn es eines gibt -- sonst das Hauptfenster (Strom im Vollbild).
    /// </summary>
    /// <remarks>
    /// Nach dem Prozess gesucht, nicht nach dem Titel: der Titel des Hauptfensters
    /// wechselt mit Server und Kanal ("#allgemein | Server - Discord").
    /// </remarks>
    internal static IntPtr DiscordFenster()
    {
        var eigene = new List<(IntPtr Handle, string Titel)>();
        foreach (var (h, t) in Sichtbare())
        {
            GetWindowThreadProcessId(h, out var pid);
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById((int)pid);
                if (p.ProcessName.StartsWith("Discord", StringComparison.OrdinalIgnoreCase)) { eigene.Add((h, t)); }
            }
            catch (Exception)
            {
            }
        }
        var herausgeloest = eigene.FirstOrDefault(f => !f.Titel.EndsWith("Discord", StringComparison.OrdinalIgnoreCase));
        return herausgeloest.Handle != IntPtr.Zero ? herausgeloest.Handle
             : eigene.Count > 0 ? eigene[0].Handle : IntPtr.Zero;
    }

    /// <summary>
    /// Plattformen, deren Name im Titel eines Browserfensters steht, wenn dort ein Strom
    /// laeuft ("name - Twitch - Google Chrome"). Als ganzes Wort: "Kickstarter" ist keiner.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex StromSeite = new(
        @"\b(Twitch|YouTube|Kick|Trovo|Facebook|TikTok|Rumble)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly string[] Browser =
        { "msedge", "chrome", "firefox", "brave", "opera", "vivaldi", "arc", "librewolf", "waterfox", "zen" };

    /// <summary>Nennt dieser Fenstertitel eine Strom-Plattform?</summary>
    internal static bool IstStromTitel(string titel) => StromSeite.IsMatch(titel);

    /// <summary>Welche Plattform der Titel nennt -- fuer die Probe, die keinen Titel ausgeben soll.</summary>
    internal static string? StromPlattform(string titel) => StromSeite.Match(titel) is { Success: true } m ? m.Value : null;

    /// <summary>
    /// Ein Browserfenster, das einen Strom zeigt (Twitch, YouTube, Kick ...): der Titel
    /// des Fensters ist der des aktiven Reiters, und der nennt die Plattform.
    /// </summary>
    internal static IntPtr BrowserStrom()
    {
        foreach (var (h, t) in Sichtbare())
        {
            // Sichtbare() liefert von vorn nach hinten: bei mehreren gewinnt das oberste.
            if (!IstStromTitel(t)) { continue; }
            GetWindowThreadProcessId(h, out var pid);
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById((int)pid);
                if (Browser.Any(b => p.ProcessName.StartsWith(b, StringComparison.OrdinalIgnoreCase))) { return h; }
            }
            catch (Exception)
            {
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>Ein Fenster, das nach Xbox Remote Play aussieht (Xbox-App oder xbox.com/play im Browser).</summary>
    internal static string? XboxVorschlag()
    {
        var titel = Sichtbare().Select(f => f.Titel).ToList();
        return titel.FirstOrDefault(t => t.Trim().Equals("Xbox", StringComparison.OrdinalIgnoreCase))
               ?? titel.FirstOrDefault(t => t.Contains("Xbox Cloud Gaming", StringComparison.OrdinalIgnoreCase)
                                            || t.Contains("xbox.com", StringComparison.OrdinalIgnoreCase))
               ?? titel.FirstOrDefault(t => t.StartsWith("Xbox", StringComparison.OrdinalIgnoreCase))
               // Sonst ein bekanntes Remote-Play-Programm, auch unter anderem Titel.
               ?? Waehlbare().FirstOrDefault(e => e.Rang == 0 && !e.Minimiert)?.Titel;
    }

    /// <summary>Ein Fenster in der Auswahlliste: gesucht wird nach dem Titel, angezeigt auch das Programm.</summary>
    internal sealed record Eintrag(string Titel, string Programm, bool Minimiert, int Rang)
    {
        public override string ToString() =>
            Titel + "   \u2014   " + Programm + (Minimiert ? "   (" + Loc.T("minimized") + ")" : string.Empty);
    }

    /// <summary>Programme, die ein Konsolenbild zeigen: Dateiname (ohne .exe) und wie es in der Liste heisst.</summary>
    private static readonly (string Datei, string Name)[] Spielbildprogramme =
    {
        ("XboxPcApp", "Xbox app"), ("XboxApp", "Xbox app"), ("GamingApp", "Xbox app"),
        ("RemotePlay", "PS Remote Play"), ("chiaki", "chiaki (PlayStation)"), ("Greenlight", "Greenlight (Xbox)"),
        ("Moonlight", "Moonlight"), ("parsecd", "Parsec"), ("streaming_client", "Steam Remote Play"),
    };

    /// <summary>
    /// Wie weit oben ein Fenster in der Liste steht: 0 ein Remote-Play-Programm oder ein
    /// Titel, der danach klingt; 1 was einen Strom zeigen kann (OBS, Discord, ein Browser
    /// mit Strom); 2 alles andere.
    /// </summary>
    internal static int Rang(string titel, string datei)
    {
        if (Spielbildprogramme.Any(p => datei.StartsWith(p.Datei, StringComparison.OrdinalIgnoreCase))
            || titel.Contains("Xbox", StringComparison.OrdinalIgnoreCase)
            || titel.Contains("Remote Play", StringComparison.OrdinalIgnoreCase)
            || titel.Contains("Cloud Gaming", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }
        if (datei.StartsWith("obs", StringComparison.OrdinalIgnoreCase)
            || datei.StartsWith("Discord", StringComparison.OrdinalIgnoreCase)
            || IstStromTitel(titel))
        {
            return 1;
        }
        return 2;
    }

    /// <summary>
    /// Die Fenster fuer die Auswahlliste (seit 2026-09-29): nur, was ein Spielbild sein
    /// kann, mit dem Programm dahinter, und die wahrscheinlichen zuerst.
    /// </summary>
    /// <remarks>
    /// Vorher stand dort jedes sichtbare Fenster mit Titel, nach dem Alphabet -- auch der
    /// Schreibtisch ("Program Manager"), das NVIDIA-Overlay und Leisten. Ein Nutzer mit
    /// Remote Play fand sein Fenster darin nicht; alles, was er sah, war falsch. Jetzt
    /// fallen Werkzeugfenster und winzige Fenster weg, das Programm steht daneben (die
    /// Xbox-App heisst im Titel nur "XBOX"), und Remote-Play-Programme stehen oben. Ein
    /// minimiertes Fenster bleibt drin, aber als solches markiert: minimiert liefert es
    /// kein Bild, und wer es nicht findet, sucht sonst am falschen Ende.
    /// </remarks>
    internal static List<Eintrag> Waehlbare()
    {
        var raus = new List<Eintrag>();
        foreach (var (h, titel) in Sichtbare())
        {
            // WS_EX_TOOLWINDOW: Overlays (NVIDIA, Game Bar), Leisten, Hilfsfenster.
            if ((GetWindowLongW(h, -20) & 0x80) != 0) { continue; }
            var klasse = Klasse(h);
            if (klasse is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") { continue; }
            var minimiert = IsIconic(h);
            if (!minimiert && GetWindowRect(h, out var r) && (r.Right - r.Left < 320 || r.Bottom - r.Top < 180)) { continue; }
            var (datei, name) = Programm(h, klasse);
            raus.Add(new Eintrag(titel, name, minimiert, Rang(titel, datei)));
        }
        return raus.OrderBy(e => e.Rang).ThenBy(e => e.Minimiert)
                   .ThenBy(e => e.Titel, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string Klasse(IntPtr h)
    {
        var s = new StringBuilder(128);
        GetClassNameW(h, s, s.Capacity);
        return s.ToString();
    }

    /// <summary>Das Programm hinter einem Fenster: Dateiname und ein lesbarer Name.</summary>
    /// <remarks>
    /// Store-Apps haengen in einem Rahmen von ApplicationFrameHost; das eigentliche
    /// Programm gehoert dem Kindfenster darin. Gefragt wird mit dem geringsten
    /// Zugriffsrecht -- das geht auch bei Programmen, die als Administrator laufen.
    /// </remarks>
    private static (string Datei, string Name) Programm(IntPtr h, string klasse)
    {
        var fenster = h;
        if (klasse == "ApplicationFrameWindow"
            && FindWindowExW(h, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null) is var kind && kind != IntPtr.Zero)
        {
            fenster = kind;
        }
        GetWindowThreadProcessId(fenster, out var pid);
        var pfad = string.Empty;
        var p = OpenProcess(0x1000, false, pid);
        if (p != IntPtr.Zero)
        {
            try
            {
                var s = new StringBuilder(1024);
                var n = s.Capacity;
                if (QueryFullProcessImageNameW(p, 0, s, ref n)) { pfad = s.ToString(); }
            }
            finally
            {
                CloseHandle(p);
            }
        }
        var datei = Path.GetFileNameWithoutExtension(pfad);
        if (datei.Length == 0) { return ("?", "?"); }
        var bekannt = Spielbildprogramme.FirstOrDefault(b => datei.StartsWith(b.Datei, StringComparison.OrdinalIgnoreCase));
        if (bekannt.Name is not null) { return (datei, bekannt.Name); }
        try
        {
            var beschreibung = System.Diagnostics.FileVersionInfo.GetVersionInfo(pfad).FileDescription?.Trim();
            if (!string.IsNullOrEmpty(beschreibung) && beschreibung.Length <= 40) { return (datei, beschreibung); }
        }
        catch (Exception)
        {
        }
        return (datei, datei);
    }

    /// <summary>
    /// Das Fenster mit dem Auswahlfenster von Windows waehlen -- dem, das auch Teams und
    /// der Browser beim Bildschirmteilen zeigen, mit einem Vorschaubild je Fenster.
    /// </summary>
    /// <returns>Der Titel des gewaehlten Fensters; null bei Abbruch.</returns>
    internal static async Task<string?> MitWindowsWaehlenAsync(IntPtr besitzer)
    {
        var waehler = new GraphicsCapturePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(waehler, besitzer);
        var item = await waehler.PickSingleItemAsync();
        return item?.DisplayName;
    }
}
