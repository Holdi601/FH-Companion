using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using WinRT;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Woher das Bild des Spiels kommt, wenn das Spiel NICHT auf diesem Rechner laeuft
/// (Konsolenmodus, seit 2026-09-28).
/// </summary>
/// <remarks>
/// Die Leser (Anmeldeschirm, Automenue, Rivals-Schirm) holen ihre Pixel ueber
/// GameArea.Capture. Ist hier eine Quelle aktiv, schneidet GameArea.Capture aus
/// deren letztem Bild aus statt vom Bildschirm -- alles Weitere bleibt, wie es ist.
///
/// Gelesen wird nur auf Anfrage: die Quelle wandelt nicht jedes eintreffende Bild um,
/// sondern das neueste, wenn ein Leser danach fragt (hoechstens alle 150 ms neu).
/// </remarks>
/// <summary>Eine Quelle, deren Bild in einem Fenster auf diesem Schirm liegt (Remote Play, Projektor).</summary>
internal interface IFensterBild
{
    /// <summary>Das 16:9-Spielbild in Bildschirmkoordinaten -- oder null (kein Fenster, minimiert).</summary>
    Rectangle? Schirmflaeche { get; }

    /// <summary>Ist dieses Fenster gerade das aktive (und nicht minimiert)?</summary>
    bool IstVorne { get; }
}

internal interface IBildquelle : IDisposable
{
    /// <summary>Was die Quelle ist -- oder warum sie nicht laeuft. Fuer die Statuszeile.</summary>
    string Beschreibung { get; }

    /// <summary>Das neueste Bild, oder null, solange keines da ist. Gehoert dem Aufrufer NICHT.</summary>
    Bitmap? Neuestes();
}

internal static class Bildquellen
{
    private static IBildquelle? _aktiv;

    /// <summary>Die Quelle, aus der GameArea gerade liest -- null: vom Bildschirm.</summary>
    public static IBildquelle? Aktiv
    {
        get => _aktiv;
        set
        {
            if (ReferenceEquals(_aktiv, value)) { return; }
            try { _aktiv?.Dispose(); } catch (Exception) { }
            _aktiv = value;
            GameArea.Invalidate();
        }
    }

    /// <summary>Der Name, unter dem OBS seine virtuelle Kamera anmeldet.</summary>
    public const string ObsKamera = "OBS Virtual Camera";

    /// <summary>Der Titelteil von OBS' Fensterprojektor ("Windowed Projector (Program)").</summary>
    public const string ObsProjektor = "Projector";

    /// <summary>
    /// Die Quelle zu den Einstellungen -- die vier Wege, das Spielbild herzuholen (seit
    /// 2026-09-28): Aufnahmekarte ("device"), OBS ("obs"), ein Fenster wie Xbox Remote
    /// Play ("window"), eine Stromadresse ("url").
    /// </summary>
    public static IBildquelle? Erzeuge(OverlaySettings s) => StandbildQuelle.FuerVorschau(s) ?? (s.VideoSource ?? "none").ToLowerInvariant() switch
    {
        "device" when !string.IsNullOrWhiteSpace(s.VideoDevice) => new GeraeteQuelle(s.VideoDevice!),
        "obs" => new ObsQuelle(),
        "discord" when FensterQuelle.Unterstuetzt => new FensterQuelle("Discord", Fenster.DiscordFenster)
        {
            NichtGefunden = Loc.T("No Discord window found. Open Discord and watch the stream, popped out or full screen."),
        },
        "browser" when FensterQuelle.Unterstuetzt => new FensterQuelle("a stream in the browser", Fenster.BrowserStrom)
        {
            NichtGefunden = Loc.T("No stream found in a browser window. Open it on Twitch, YouTube or Kick; keep its tab in front."),
        },
        "window" when !string.IsNullOrWhiteSpace(s.VideoWindow) && FensterQuelle.Unterstuetzt
            => new FensterQuelle(s.VideoWindow!),
        "url" when !string.IsNullOrWhiteSpace(s.VideoUrl) => new StromQuelle(s.VideoUrl!, Ffmpeg.Finden(s.FfmpegPath)),
        _ => null,
    };

    /// <summary>
    /// Die Einstellungen anwenden -- beim Start und SOFORT, wenn im Bedienfeld eine
    /// andere Quelle gewaehlt wird (kein Neustart mehr).
    /// </summary>
    /// <remarks>
    /// Ohne Graphics Capture (altes Windows) liest die Fenster-Quelle wie frueher vom
    /// Schirm: dann muss das Fenster sichtbar bleiben.
    /// </remarks>
    public static void Anwenden(OverlaySettings s)
    {
        if (!s.ConsoleMode)
        {
            GameArea.FensterTitel = null;
            Aktiv = null;
            return;
        }
        var fenster = string.Equals(s.VideoSource, "window", StringComparison.OrdinalIgnoreCase);
        GameArea.FensterTitel = fenster && !FensterQuelle.Unterstuetzt ? s.VideoWindow : null;
        Aktiv = Erzeuge(s);
    }

    /// <summary>
    /// Wo im Bild das 16:9-Spielbild liegt -- innerhalb der Flaeche <paramref name="flaeche"/>.
    /// </summary>
    /// <remarks>
    /// Ein Fenster (Xbox Remote Play, Browser, Projektor) zeigt das Spiel oft mit Balken:
    /// schwarz oder in einer einfarbigen Seitenfarbe. Gleichfoermige Randzeilen und
    /// -spalten werden abgeschnitten -- aber NUR, wenn danach 16:9 uebrig bleibt. Ein
    /// gleichmaessig blauer Himmel oben im Spiel ist auch gleichfoermig; abgeschnitten
    /// ergaebe er kein 16:9 und bleibt deshalb stehen. Passt nichts, wird die Flaeche
    /// mittig auf 16:9 gebracht.
    /// </remarks>
    internal static unsafe Rectangle Spielbild(Bitmap bild, Rectangle flaeche)
    {
        flaeche.Intersect(new Rectangle(0, 0, bild.Width, bild.Height));
        if (flaeche.Width < 16 || flaeche.Height < 9) { return new Rectangle(0, 0, bild.Width, bild.Height); }
        var daten = bild.LockBits(new Rectangle(0, 0, bild.Width, bild.Height),
                                  System.Drawing.Imaging.ImageLockMode.ReadOnly,
                                  System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            byte* basis = (byte*)daten.Scan0;
            int Hell(int x, int y)
            {
                var p = basis + (y * daten.Stride) + (x * 4);
                return (p[0] + (2 * p[1]) + p[2]) >> 2;
            }
            bool ZeileGleich(int y)
            {
                int min = 255, max = 0;
                for (var x = flaeche.Left; x < flaeche.Right; x += 4)
                {
                    var h = Hell(x, y);
                    if (h < min) { min = h; }
                    if (h > max) { max = h; }
                    if (max - min > 12) { return false; }
                }
                return true;
            }
            bool SpalteGleich(int x, int oben, int unten)
            {
                int min = 255, max = 0;
                for (var y = oben; y < unten; y += 4)
                {
                    var h = Hell(x, y);
                    if (h < min) { min = h; }
                    if (h > max) { max = h; }
                    if (max - min > 12) { return false; }
                }
                return true;
            }
            int o = flaeche.Top, u = flaeche.Bottom - 1;
            while (o < u && ZeileGleich(o)) { o++; }
            while (u > o && ZeileGleich(u)) { u--; }
            int l = flaeche.Left, r = flaeche.Right - 1;
            while (l < r && SpalteGleich(l, o, u + 1)) { l++; }
            while (r > l && SpalteGleich(r, o, u + 1)) { r--; }
            var innen = Rectangle.FromLTRB(l, o, r + 1, u + 1);
            if (innen.Width >= 320 && innen.Height >= 180 && Math.Abs((innen.Width / (double)innen.Height) - (16.0 / 9)) < 0.04)
            {
                return innen;
            }
        }
        finally
        {
            bild.UnlockBits(daten);
        }
        // Mittig auf 16:9.
        var breite = Math.Min(flaeche.Width, (int)Math.Round(flaeche.Height * 16.0 / 9));
        var hoehe = Math.Min(flaeche.Height, (int)Math.Round(breite * 9.0 / 16));
        return new Rectangle(flaeche.Left + ((flaeche.Width - breite) / 2), flaeche.Top + ((flaeche.Height - hoehe) / 2),
                             breite, hoehe);
    }

    /// <summary>Die Videogeraete dieses Rechners -- Aufnahmekarten, Kameras, OBS Virtual Camera.</summary>
    public static async Task<List<string>> GeraeteAsync()
    {
        try
        {
            var gruppen = await MediaFrameSourceGroup.FindAllAsync();
            return gruppen.Where(g => g.SourceInfos.Any(i => i.SourceKind == MediaFrameSourceKind.Color))
                          .Select(g => g.DisplayName).Distinct().OrderBy(n => n).ToList();
        }
        catch (Exception)
        {
            return new List<string>();
        }
    }

    /// <summary>Einen Ausschnitt eines Bildes auf eine Zielgroesse bringen -- wie StretchBlt vom Schirm.</summary>
    internal static Bitmap Ausschnitt(Bitmap? bild, Rectangle quelle, Size ziel)
    {
        var raus = new Bitmap(Math.Max(1, ziel.Width), Math.Max(1, ziel.Height), PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(raus);
        g.Clear(Color.Black);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        Mit(bild, b =>
        {
            g.DrawImage(b, new Rectangle(0, 0, raus.Width, raus.Height), quelle, GraphicsUnit.Pixel);
            return true;
        }, false);
        return raus;
    }

    /// <summary>
    /// Mit dem Bild einer Quelle arbeiten -- nie aus zwei Faeden zugleich (seit 2026-09-29).
    /// </summary>
    /// <remarks>
    /// GDI+ verbietet, dasselbe Bild aus zwei Faeden anzufassen, und wirft dann "Object
    /// is currently in use elsewhere". Neuestes() gibt aber allen dasselbe Bild: den
    /// Lesern im Hintergrund, GameArea.Find im Takt der Oberflaeche, der Vorschau. Mit
    /// Xbox Remote Play kam das beim Wechsel des Fensters als Absturzmeldung. Jeder
    /// Zugriff sperrt darum das Bild selbst, und die Quellen entsorgen ein Bild nur unter
    /// derselben Sperre (Entsorgen). Ist es dann schon entsorgt, gilt das als "kein Bild".
    /// </remarks>
    internal static T Mit<T>(Bitmap? bild, Func<Bitmap, T> tun, T ohne)
    {
        if (bild is null) { return ohne; }
        lock (bild)
        {
            try { return tun(bild); }
            catch (ArgumentException) { return ohne; }
            catch (InvalidOperationException) { return ohne; }
        }
    }

    /// <summary>Ein Bild einer Quelle entsorgen -- erst, wenn es gerade niemand benutzt.</summary>
    internal static void Entsorgen(Bitmap? bild)
    {
        if (bild is null) { return; }
        lock (bild) { bild.Dispose(); }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Bitmap, object> Groessen = new();

    /// <summary>
    /// Die Groesse eines Quellbildes, einmal je Bild gemerkt: GameArea.Find fragt im Takt
    /// der Oberflaeche und soll dafuer nicht warten, bis ein Leser mit Zeichnen fertig ist.
    /// </summary>
    internal static Size? Groesse(Bitmap? bild)
    {
        if (bild is null) { return null; }
        if (Groessen.TryGetValue(bild, out var gemerkt)) { return (Size)gemerkt; }
        var groesse = Mit(bild, b => (Size?)b.Size, null);
        if (groesse is { } g) { Groessen.AddOrUpdate(bild, g); }
        return groesse;
    }

    /// <summary>Ein WinRT-Bild (BGRA8) in ein GDI+-Bild, eine Zeilenkopie.</summary>
    /// <param name="ohneAlpha">
    /// Den Alphakanal nicht beachten. Fenster, die mit GDI zeichnen (WinForms, viele
    /// Spieler-Programme), hinterlassen dort 0 -- als ARGB gelesen waere das Bild
    /// voellig durchsichtig.
    /// </param>
    internal static unsafe Bitmap AlsBitmap(SoftwareBitmap bgra, bool ohneAlpha = false)
    {
        var format = ohneAlpha ? PixelFormat.Format32bppRgb : PixelFormat.Format32bppArgb;
        var bmp = new Bitmap(bgra.PixelWidth, bgra.PixelHeight, format);
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, format);
        try
        {
            using var puffer = bgra.LockBuffer(BitmapBufferAccessMode.Read);
            using var verweis = puffer.CreateReference();
            verweis.As<IMemoryBufferByteAccess>().GetBuffer(out var quelle, out var groesse);
            var ebene = puffer.GetPlaneDescription(0);
            var breite = bmp.Width * 4;
            for (var zeile = 0; zeile < bmp.Height; zeile++)
            {
                var von = ebene.StartIndex + (ebene.Stride * zeile);
                if (von + breite > groesse) { break; }
                Buffer.MemoryCopy(quelle + von, (void*)(data.Scan0 + (data.Stride * zeile)), breite, breite);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return bmp;
    }
}

/// <summary>
/// Ein Videogeraet: Aufnahmekarte, Kamera -- oder die OBS Virtual Camera, ueber die sich
/// jeder Strom in OBS (Aufnahmekarte, Xbox-App, NDI, Netzwerk) weiterreichen laesst.
/// </summary>
internal sealed class GeraeteQuelle : IBildquelle
{
    private readonly object _schloss = new();
    private MediaCapture? _aufnahme;
    private MediaFrameReader? _leser;
    private Bitmap? _letztes;
    private Bitmap? _vorletztes;
    private DateTime _letztesAm = DateTime.MinValue;
    private bool _zu;

    public string Beschreibung { get; private set; }

    public GeraeteQuelle(string name)
    {
        Beschreibung = "video device: starting " + name;
        _ = StartenAsync(name);
    }

    private async Task StartenAsync(string name)
    {
        try
        {
            var gruppen = await MediaFrameSourceGroup.FindAllAsync();
            var gruppe = gruppen.FirstOrDefault(g => string.Equals(g.DisplayName, name, StringComparison.OrdinalIgnoreCase))
                         ?? gruppen.FirstOrDefault(g => g.DisplayName.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (gruppe is null) { Beschreibung = "video device not found: " + name; return; }
            var aufnahme = new MediaCapture();
            await aufnahme.InitializeAsync(new MediaCaptureInitializationSettings
            {
                SourceGroup = gruppe,
                // Nur lesen: ein anderes Programm (OBS) darf das Geraet weiter steuern.
                SharingMode = MediaCaptureSharingMode.SharedReadOnly,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
            });
            var quelle = aufnahme.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color);
            if (quelle is null) { Beschreibung = "video device has no colour stream: " + name; aufnahme.Dispose(); return; }
            var leser = await aufnahme.CreateFrameReaderAsync(quelle, MediaEncodingSubtypes.Bgra8);
            // Nur das neueste Bild zaehlt; aeltere verwirft das Geraet selbst.
            leser.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            var status = await leser.StartAsync();
            if (status != MediaFrameReaderStartStatus.Success)
            {
                Beschreibung = $"video device did not start ({status}): {name}";
                leser.Dispose();
                aufnahme.Dispose();
                return;
            }
            lock (_schloss)
            {
                if (_zu) { leser.Dispose(); aufnahme.Dispose(); return; }
                _aufnahme = aufnahme;
                _leser = leser;
            }
            Beschreibung = "video device: " + gruppe.DisplayName;
        }
        catch (Exception e)
        {
            Beschreibung = "video device failed: " + e.Message;
        }
    }

    public Bitmap? Neuestes()
    {
        lock (_schloss)
        {
            if (_leser is null) { return _letztes; }
            // Hoechstens alle 150 ms neu umwandeln -- ein Lesevorgang fragt mehrmals.
            if (_letztes is not null && DateTime.UtcNow - _letztesAm < TimeSpan.FromMilliseconds(150)) { return _letztes; }
            try
            {
                using var bild = _leser.TryAcquireLatestFrame();
                var weich = bild?.VideoMediaFrame?.SoftwareBitmap;
                if (weich is null && bild?.VideoMediaFrame?.Direct3DSurface is { } flaeche)
                {
                    weich = SoftwareBitmap.CreateCopyFromSurfaceAsync(flaeche).AsTask().GetAwaiter().GetResult();
                }
                if (weich is null) { return _letztes; }
                using var bgra = weich.BitmapPixelFormat == BitmapPixelFormat.Bgra8
                    ? SoftwareBitmap.Copy(weich)
                    : SoftwareBitmap.Convert(weich, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                var neu = Bildquellen.AlsBitmap(bgra);
                Bildquellen.Entsorgen(_vorletztes);
                _vorletztes = _letztes;
                _letztes = neu;
                _letztesAm = DateTime.UtcNow;
            }
            catch (Exception)
            {
                // Ein verlorenes Bild ist kein Fehler; das naechste kommt.
            }
            return _letztes;
        }
    }

    public void Dispose()
    {
        lock (_schloss)
        {
            _zu = true;
            try { _leser?.StopAsync().AsTask().Wait(1000); } catch (Exception) { }
            _leser?.Dispose();
            _aufnahme?.Dispose();
            Bildquellen.Entsorgen(_letztes);
            Bildquellen.Entsorgen(_vorletztes);
            _vorletztes = null;
            _leser = null;
            _aufnahme = null;
            _letztes = null;
        }
    }
}

/// <summary>
/// OBS als Quelle: seine virtuelle Kamera, wenn Windows sie als Videogeraet kennt --
/// sonst ein Fensterprojektor von OBS, aufgenommen wie jedes andere Fenster.
/// </summary>
/// <remarks>
/// GEMESSEN am 2026-09-28 (OBS 32, Windows 11): die OBS Virtual Camera ist ein
/// DirectShow-Filter, und Media Foundation -- womit die App Videogeraete oeffnet --
/// fuehrt sie nicht. Der Fensterprojektor geht immer: in OBS Rechtsklick auf die
/// Vorschau, "Windowed Projector (Program)". Er darf verdeckt sein, nur nicht minimiert.
/// </remarks>
internal sealed class ObsQuelle : IBildquelle, IFensterBild
{
    private readonly object _schloss = new();
    private IBildquelle _jetzt;
    private bool _zu;

    public ObsQuelle()
    {
        _jetzt = new FensterQuelle("OBS projector", Fenster.ObsProjektor);
        _ = KameraSuchenAsync();
    }

    private async Task KameraSuchenAsync()
    {
        var geraete = await Bildquellen.GeraeteAsync();
        var kamera = geraete.FirstOrDefault(g => g.Contains("OBS", StringComparison.OrdinalIgnoreCase));
        if (kamera is null) { return; }
        lock (_schloss)
        {
            if (_zu) { return; }
            var alt = _jetzt;
            _jetzt = new GeraeteQuelle(kamera);
            try { alt.Dispose(); } catch (Exception) { }
        }
    }

    public string Beschreibung
    {
        get { lock (_schloss) { return "OBS -- " + _jetzt.Beschreibung; } }
    }

    public Bitmap? Neuestes()
    {
        lock (_schloss) { return _jetzt.Neuestes(); }
    }

    public Rectangle? Schirmflaeche
    {
        get { lock (_schloss) { return (_jetzt as IFensterBild)?.Schirmflaeche; } }
    }

    public bool IstVorne
    {
        get { lock (_schloss) { return (_jetzt as IFensterBild)?.IstVorne == true; } }
    }

    public void Dispose()
    {
        lock (_schloss)
        {
            _zu = true;
            try { _jetzt.Dispose(); } catch (Exception) { }
        }
    }
}

/// <summary>
/// Nur fuer Anleitungsbilder (--main-preview): ein Standbild des Spiels anstelle der
/// gewaehlten Quelle, damit die Vorschau zeigt, was mit angeschlossener Xbox zu sehen
/// waere. Greift ausschliesslich mit MainForm.NurVorschau und FHC_PREVIEW_PICTURE.
/// </summary>
internal sealed class StandbildQuelle : IBildquelle
{
    private readonly Bitmap _bild;
    private readonly string _art;

    private StandbildQuelle(Bitmap bild, string art)
    {
        _bild = bild;
        _art = art;
    }

    public static IBildquelle? FuerVorschau(OverlaySettings s)
    {
        if (!MainForm.NurVorschau || (s.VideoSource ?? "none") == "none") { return null; }
        var pfad = Environment.GetEnvironmentVariable("FHC_PREVIEW_PICTURE");
        if (string.IsNullOrWhiteSpace(pfad) || !File.Exists(pfad)) { return null; }
        var art = (s.VideoSource ?? string.Empty).ToLowerInvariant() switch
        {
            "device" => "video device: " + (s.VideoDevice ?? "capture card"),
            "obs" => "OBS -- window: Windowed Projector (Program)",
            "discord" => "window: Discord",
            "browser" => "window: a stream in the browser",
            "window" => "window: " + (s.VideoWindow ?? "Xbox"),
            _ => "stream: " + (s.VideoUrl ?? string.Empty),
        };
        return new StandbildQuelle(new Bitmap(pfad), art);
    }

    public string Beschreibung => _art;

    public Bitmap? Neuestes() => _bild;

    public void Dispose() => Bildquellen.Entsorgen(_bild);
}

/// <summary>Wo ffmpeg liegt: Einstellung, PATH, winget (Gyan.FFmpeg / BtbN) -- oder null.</summary>
internal static class Ffmpeg
{
    public static string? Finden(string? eingestellt)
    {
        if (!string.IsNullOrWhiteSpace(eingestellt) && File.Exists(eingestellt)) { return eingestellt; }
        try
        {
            foreach (var ordner in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(ordner)) { continue; }
                var p = Path.Combine(ordner.Trim('"'), "ffmpeg.exe");
                if (File.Exists(p)) { return p; }
            }
            var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                      "Microsoft", "WinGet");
            var link = Path.Combine(winget, "Links", "ffmpeg.exe");
            if (File.Exists(link)) { return link; }
            var pakete = Path.Combine(winget, "Packages");
            if (Directory.Exists(pakete))
            {
                foreach (var paket in Directory.EnumerateDirectories(pakete, "*FFmpeg*"))
                {
                    var treffer = Directory.EnumerateFiles(paket, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
                    if (treffer is not null) { return treffer; }
                }
            }
        }
        catch (Exception)
        {
        }
        return null;
    }
}

/// <summary>
/// Ein Strom ueber eine Adresse (HLS, RTSP, RTMP, SRT ...), gelesen von ffmpeg --
/// zwei Bilder je Sekunde in 1080p, das genuegt den Lesern. ffmpeg wird nicht
/// mitgeliefert; ohne sagt die Statuszeile es, und die OBS Virtual Camera geht immer.
/// </summary>
internal sealed class StromQuelle : IBildquelle
{
    private const int Breite = 1920;
    private const int Hoehe = 1080;
    private readonly object _schloss = new();
    private readonly string _adresse;
    private readonly string? _ffmpeg;
    private readonly Thread _faden;
    private Process? _prozess;
    private byte[]? _letztes;
    private Bitmap? _bild;
    private Bitmap? _altesBild;
    private bool _bildAktuell;
    private volatile bool _zu;

    public string Beschreibung { get; private set; }

    public StromQuelle(string adresse, string? ffmpeg)
    {
        _adresse = adresse;
        _ffmpeg = ffmpeg;
        Beschreibung = "stream: connecting";
        _faden = new Thread(Lesen) { IsBackground = true, Name = "stream source", Priority = ThreadPriority.BelowNormal };
        _faden.Start();
    }

    private void Lesen()
    {
        var bildBytes = Breite * Hoehe * 4;
        if (_ffmpeg is null)
        {
            Beschreibung = "stream needs ffmpeg (not found) -- run: winget install Gyan.FFmpeg";
            return;
        }
        while (!_zu)
        {
            try
            {
                var start = new ProcessStartInfo(_ffmpeg!)
                {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-i", _adresse, "-an",
                                          "-vf", $"fps=2,scale={Breite}:{Hoehe}", "-pix_fmt", "bgra",
                                          "-f", "rawvideo", "-" })
                {
                    start.ArgumentList.Add(a);
                }
                var p = Process.Start(start)!;
                try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { }
                _ = p.StandardError.ReadToEndAsync();
                lock (_schloss) { _prozess = p; }
                Beschreibung = "stream: " + _adresse;
                var puffer = new byte[bildBytes];
                var strom = p.StandardOutput.BaseStream;
                while (!_zu)
                {
                    var n = 0;
                    while (n < bildBytes)
                    {
                        var k = strom.Read(puffer, n, bildBytes - n);
                        if (k <= 0) { break; }
                        n += k;
                    }
                    if (n < bildBytes) { break; }
                    lock (_schloss)
                    {
                        _letztes ??= new byte[bildBytes];
                        Buffer.BlockCopy(puffer, 0, _letztes, 0, bildBytes);
                        _bildAktuell = false;
                    }
                }
                try { if (!p.HasExited) { p.Kill(); } } catch (Exception) { }
                if (!_zu) { Beschreibung = "stream ended -- reconnecting"; }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Beschreibung = "stream needs ffmpeg (not found) -- run: winget install Gyan.FFmpeg";
                return;
            }
            catch (Exception e)
            {
                Beschreibung = "stream failed: " + e.Message;
            }
            for (var i = 0; i < 50 && !_zu; i++) { Thread.Sleep(100); }
        }
    }

    public Bitmap? Neuestes()
    {
        lock (_schloss)
        {
            if (_letztes is null) { return null; }
            if (_bild is not null && _bildAktuell) { return _bild; }
            // Ein NEUES Bild: das bisherige zeichnet vielleicht gerade ein Leser.
            var neu = new Bitmap(Breite, Hoehe, PixelFormat.Format32bppArgb);
            var data = neu.LockBits(new Rectangle(0, 0, Breite, Hoehe), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (var zeile = 0; zeile < Hoehe; zeile++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(_letztes, zeile * Breite * 4,
                                                                data.Scan0 + (zeile * data.Stride), Breite * 4);
                }
            }
            finally
            {
                neu.UnlockBits(data);
            }
            Bildquellen.Entsorgen(_altesBild);
            _altesBild = _bild;
            _bild = neu;
            _bildAktuell = true;
            return _bild;
        }
    }

    public void Dispose()
    {
        _zu = true;
        lock (_schloss)
        {
            try { if (_prozess is { HasExited: false }) { _prozess.Kill(); } } catch (Exception) { }
            Bildquellen.Entsorgen(_bild);
            Bildquellen.Entsorgen(_altesBild);
            _bild = null;
            _altesBild = null;
        }
    }
}
