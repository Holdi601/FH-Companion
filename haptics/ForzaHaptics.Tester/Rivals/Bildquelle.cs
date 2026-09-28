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

    /// <summary>Die Quelle zu den Einstellungen: Geraet oder Adresse; Fenster und "keine" brauchen keine.</summary>
    public static IBildquelle? Erzeuge(OverlaySettings s) => (s.VideoSource ?? "none").ToLowerInvariant() switch
    {
        "device" when !string.IsNullOrWhiteSpace(s.VideoDevice) => new GeraeteQuelle(s.VideoDevice!),
        "url" when !string.IsNullOrWhiteSpace(s.VideoUrl) => new StromQuelle(s.VideoUrl!, s.FfmpegPath),
        _ => null,
    };

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
        if (bild is null) { return raus; }
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(bild, new Rectangle(0, 0, raus.Width, raus.Height), quelle, GraphicsUnit.Pixel);
        return raus;
    }

    /// <summary>Ein WinRT-Bild (BGRA8) in ein GDI+-Bild, eine Zeilenkopie.</summary>
    internal static unsafe Bitmap AlsBitmap(SoftwareBitmap bgra)
    {
        var bmp = new Bitmap(bgra.PixelWidth, bgra.PixelHeight, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly,
                                PixelFormat.Format32bppArgb);
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
                _letztes?.Dispose();
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
            _letztes?.Dispose();
            _leser = null;
            _aufnahme = null;
            _letztes = null;
        }
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
    private bool _bildAktuell;
    private volatile bool _zu;

    public string Beschreibung { get; private set; }

    public StromQuelle(string adresse, string? ffmpeg)
    {
        _adresse = adresse;
        _ffmpeg = string.IsNullOrWhiteSpace(ffmpeg) ? "ffmpeg" : ffmpeg;
        Beschreibung = "stream: connecting";
        _faden = new Thread(Lesen) { IsBackground = true, Name = "stream source", Priority = ThreadPriority.BelowNormal };
        _faden.Start();
    }

    private void Lesen()
    {
        var bildBytes = Breite * Hoehe * 4;
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
                Beschreibung = "stream needs ffmpeg (not found) -- install ffmpeg or use the OBS Virtual Camera";
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
            _bild ??= new Bitmap(Breite, Hoehe, PixelFormat.Format32bppArgb);
            var data = _bild.LockBits(new Rectangle(0, 0, Breite, Hoehe), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
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
                _bild.UnlockBits(data);
            }
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
            _bild?.Dispose();
            _bild = null;
        }
    }
}
