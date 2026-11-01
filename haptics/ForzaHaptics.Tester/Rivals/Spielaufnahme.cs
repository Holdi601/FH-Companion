using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Das Spielfenster als eigene Bildquelle am PC (seit 2026-11-01) -- damit der HUD in Bildschirmfreigaben
/// zu sehen sein darf.
/// </summary>
/// <remarks>
/// ## Warum
///
/// Der Nutzer: "wenn ich in Discord den ganzen Bildschirm teile, ist der HUD nicht da". Jedes Fenster
/// ueber dem Spiel hielt sich aus JEDER Aufnahme heraus (WDA_EXCLUDEFROMCAPTURE): die App liest den
/// Schirm selbst (Anmeldung, Ergebnisse, Scanner, Autonotizen) und haette sonst ihren eigenen HUD
/// gelesen -- am 2026-09-09 stand das rechte Panel genau auf dem Klassenabzeichen. Discord, OBS und
/// die Game Bar sahen den HUD darum nie.
///
/// ## Wie
///
/// Die Leser nehmen nicht mehr den Schirm, sondern das Fenster von Forza selbst (Windows Graphics
/// Capture, <see cref="FensterQuelle"/>): darin liegt nur, was das Spiel zeichnet, nie ein Fenster
/// darueber. Erst dann lassen die HUD-Fenster sich wieder aufnehmen (<see cref="HudAufnahme"/>).
///
/// Nur ohne den gelben Rahmen, den Windows um ein aufgenommenes Fenster zieht -- abschaltbar erst ab
/// Windows 11. Unter Windows 10 bleibt alles wie vorher: HUD unsichtbar fuer Aufnahmen, das
/// Aufnahmefenster zeigt ihn fuer OBS.
///
/// Liefert die Fensteraufnahme nur Schwarz, waehrend auf dem Schirm etwas zu sehen ist (manche
/// Vollbildmodi), gilt sie als unbrauchbar: zurueck zum Schirm, HUD wieder versteckt.
/// </remarks>
internal static class Spielaufnahme
{
    private static readonly object Sperre = new();
    private static FensterQuelle? _quelle;
    private static string _prozess = GameWatch.DefaultProcessName;
    private static bool _gewuenscht;
    private static bool _schwarz;
    private static DateTime _schwarzSeit = DateTime.MinValue;
    // Das Fenster, aus dem schon ein echtes (nicht schwarzes) Bild kam.
    private static IntPtr _bestaetigt;
    private static bool _bereit;

    /// <summary>Fuer --hud-capture-check: ein Ersatzfenster statt Forza.</summary>
    internal static Func<IntPtr>? TestFenster { get; set; }

    /// <summary>Kann dieses Windows das? (Fensteraufnahme ohne Rahmen: Windows 11.)</summary>
    public static bool Moeglich => FensterQuelle.Unterstuetzt && FensterQuelle.RahmenAbschaltbar;

    /// <summary>Liefert die Aufnahme des Spielfensters gerade brauchbare Bilder?</summary>
    public static bool Bereit
    {
        get { lock (Sperre) { return _bereit; } }
    }

    /// <summary>Unbrauchbar, weil das Fenster nur Schwarz lieferte (bis zum Neustart der App).</summary>
    public static bool NurSchwarz
    {
        get { lock (Sperre) { return _schwarz; } }
    }

    /// <summary>Ein- oder ausschalten; der Prozessname ist der des Spiels (Einstellung).</summary>
    public static void Einstellen(bool gewuenscht, string? prozess)
    {
        lock (Sperre)
        {
            _prozess = string.IsNullOrWhiteSpace(prozess) ? GameWatch.DefaultProcessName : prozess!;
            _gewuenscht = gewuenscht;
            var an = gewuenscht && Moeglich && !_schwarz;
            if (an && _quelle is null)
            {
                var test = TestFenster;
                _quelle = new FensterQuelle("forza", () => test?.Invoke() ?? Spielfenster(_prozess)) { Frische = TimeSpan.FromMilliseconds(30) };
            }
            else if (!an && _quelle is not null)
            {
                _quelle.Dispose();
                _quelle = null;
            }
        }
        Pruefen();
    }

    /// <summary>
    /// Ein Ausschnitt des Spiels, in Schirmkoordinaten -- aus dem Fenster von Forza, nicht vom Schirm.
    /// null: die Aufnahme ist nicht bereit, der Aufrufer nimmt den Schirm.
    /// </summary>
    public static Bitmap? Ausschnitt(Rectangle schirm, Size ziel, PixelFormat format)
    {
        FensterQuelle? quelle;
        lock (Sperre)
        {
            if (!_bereit) { return null; }
            quelle = _quelle;
        }
        if (quelle is null) { return null; }
        var bild = quelle.Neuestes();
        if (bild is null || quelle.Schirmflaeche is not { Width: > 0, Height: > 0 } flaeche) { return null; }
        var groesse = Bildquellen.Groesse(bild);
        if (groesse is not { Width: > 0, Height: > 0 } g) { return null; }
        // Schirm -> Bild: das Bild IST die Schirmflaeche, nur vielleicht anders gross (DPI, Skalierung).
        var kx = g.Width / (double)flaeche.Width;
        var ky = g.Height / (double)flaeche.Height;
        var imBild = new Rectangle(
            (int)Math.Round((schirm.X - flaeche.X) * kx), (int)Math.Round((schirm.Y - flaeche.Y) * ky),
            Math.Max(1, (int)Math.Round(schirm.Width * kx)), Math.Max(1, (int)Math.Round(schirm.Height * ky)));
        var raus = new Bitmap(Math.Max(1, ziel.Width), Math.Max(1, ziel.Height), format);
        using var zeichner = Graphics.FromImage(raus);
        zeichner.Clear(Color.Black);
        // Gleiche Groesse: Pixel fuer Pixel wie vom Schirm; sonst gemittelt wie StretchBlt mit HALFTONE.
        zeichner.InterpolationMode = imBild.Size == raus.Size
            ? System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor
            : System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
        zeichner.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        var gemalt = Bildquellen.Mit(bild, b =>
        {
            zeichner.DrawImage(b, new Rectangle(0, 0, raus.Width, raus.Height), imBild, GraphicsUnit.Pixel);
            return true;
        }, false);
        if (!gemalt)
        {
            raus.Dispose();
            return null;
        }
        return raus;
    }

    /// <summary>
    /// Bereit oder nicht -- und schwarz oder nicht. Alle zwei Sekunden von <see cref="HudAufnahme"/>
    /// angestossen; aendert sich etwas, schalten die HUD-Fenster um.
    /// </summary>
    internal static void Pruefen()
    {
        FensterQuelle? quelle;
        lock (Sperre)
        {
            quelle = _quelle;
            if (quelle is null)
            {
                Setze(false);
                return;
            }
        }
        var fenster = quelle.Aufgenommenes;
        if (fenster == IntPtr.Zero || !quelle.HatBild)
        {
            Setze(false);
            return;
        }
        // Einmal ein echtes Bild aus diesem Fenster: die Aufnahme taugt -- kein Umwandeln mehr im Takt.
        if (fenster == _bestaetigt)
        {
            Setze(true);
            return;
        }
        var bild = quelle.Neuestes();
        if (bild is null)
        {
            Setze(false);
            return;
        }
        if (!Bildquellen.Mit(bild, Schwarz, true))
        {
            _bestaetigt = fenster;
            _schwarzSeit = DateTime.MinValue;
            Setze(true);
            return;
        }
        // Schwarz. Ladebildschirme sind das auch -- unbrauchbar erst, wenn das Spiel vorne ist, der
        // Schirm an seiner Stelle etwas zeigt und das zwoelf Sekunden lang so bleibt.
        if (GetForegroundWindow() == fenster && quelle.Schirmflaeche is { } flaeche && !SchirmSchwarz(flaeche))
        {
            if (_schwarzSeit == DateTime.MinValue) { _schwarzSeit = DateTime.UtcNow; }
            else if (DateTime.UtcNow - _schwarzSeit > TimeSpan.FromSeconds(12))
            {
                lock (Sperre) { _schwarz = true; }
                Einstellen(_gewuenscht, _prozess);
                return;
            }
        }
        else
        {
            _schwarzSeit = DateTime.MinValue;
        }
        Setze(false);
    }

    private static void Setze(bool bereit)
    {
        bool anders;
        lock (Sperre)
        {
            anders = _bereit != bereit;
            _bereit = bereit;
        }
        if (anders) { HudAufnahme.Melden(); }
    }

    /// <summary>16 Stichproben ueber das Bild: alle fast schwarz?</summary>
    private static bool Schwarz(Bitmap b)
    {
        for (var i = 1; i <= 4; i++)
        {
            for (var j = 1; j <= 4; j++)
            {
                var c = b.GetPixel(b.Width * i / 5, b.Height * j / 5);
                if (c.R + c.G + c.B > 30) { return false; }
            }
        }
        return true;
    }

    private static bool SchirmSchwarz(Rectangle flaeche)
    {
        try
        {
            using var b = new Bitmap(flaeche.Width, flaeche.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(b)) { g.CopyFromScreen(flaeche.Location, Point.Empty, flaeche.Size); }
            return Schwarz(b);
        }
        catch (Exception) { return true; }
    }

    /// <summary>Das Fenster des Spiels: zuerst der eingestellte Prozess, sonst jedes laufende Forza.</summary>
    private static IntPtr Spielfenster(string prozess)
    {
        var h = GameArea.Spielfenster(prozess);
        if (h != IntPtr.Zero) { return h; }
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    if (!GameWatch.IstForza(p.ProcessName) || string.Equals(p.ProcessName, prozess, StringComparison.OrdinalIgnoreCase)) { continue; }
                    h = GameArea.Spielfenster(p.ProcessName);
                    if (h != IntPtr.Zero) { return h; }
                }
            }
        }
        catch (Exception) { }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}

/// <summary>
/// Ob die Fenster ueber dem Spiel in Aufnahmen und Bildschirmfreigaben erscheinen (seit 2026-11-01).
/// </summary>
/// <remarks>
/// Ja nur, wenn der Nutzer es will UND die eigenen Leser das Spielfenster selbst lesen
/// (<see cref="Spielaufnahme.Bereit"/>) -- sonst fotografierten sie den HUD mit. Jedes HUD-Fenster
/// meldet sich hier an und wird umgeschaltet, sobald sich das aendert.
/// </remarks>
internal static class HudAufnahme
{
    private const uint WdaNone = 0x00000000;
    private const uint WdaExcludeFromCapture = 0x00000011;
    private static readonly System.Threading.Timer Wache = new(_ => Spielaufnahme.Pruefen(), null, 2000, 2000);
    private static bool _gewuenscht;

    /// <summary>Sollen die HUD-Fenster gerade in Aufnahmen erscheinen?</summary>
    public static bool Sichtbar => _gewuenscht && Spielaufnahme.Bereit;

    public static event Action? Geaendert;

    /// <summary>Die Einstellung uebernehmen (Start, Schalter im Reiter).</summary>
    public static void Einstellen(OverlaySettings s)
    {
        GC.KeepAlive(Wache);
        _gewuenscht = s.HudInCaptures;
        Spielaufnahme.Einstellen(s.HudInCaptures, s.ForzaProcess);
        Melden();
    }

    internal static void Melden() => Geaendert?.Invoke();

    /// <summary>Ein Fenster anmelden: jetzt einstellen und bei jeder Aenderung wieder.</summary>
    public static void Anmelden(Form f)
    {
        Anwenden(f);
        Action wieder = () => Anwenden(f);
        Geaendert += wieder;
        f.Disposed += (_, _) => Geaendert -= wieder;
    }

    /// <summary>Die Aufnahme-Sperre dieses Fensters setzen -- auf seinem eigenen Faden.</summary>
    public static bool Anwenden(Form f)
    {
        if (f.IsDisposed || !f.IsHandleCreated) { return false; }
        var wert = Sichtbar ? WdaNone : WdaExcludeFromCapture;
        if (f.InvokeRequired)
        {
            try { f.BeginInvoke(() => { if (!f.IsDisposed) { SetWindowDisplayAffinity(f.Handle, wert); } }); }
            catch (Exception) { }
            return wert == WdaExcludeFromCapture;
        }
        try { return SetWindowDisplayAffinity(f.Handle, wert) && wert == WdaExcludeFromCapture; }
        catch (Exception) { return false; }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
}
