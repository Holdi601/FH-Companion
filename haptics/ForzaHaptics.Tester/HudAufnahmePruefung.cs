using System.Drawing;
using System.Drawing.Imaging;
using ForzaHaptics.Tester.Rivals;

namespace ForzaHaptics.Tester;

/// <summary>
/// "--hud-capture-check": sieht Discord den HUD, und sehen die eigenen Leser ihn NICHT? (seit 2026-11-01)
/// </summary>
/// <remarks>
/// Ein rotes Ersatzfenster spielt das Spiel, ein magentafarbenes den HUD darueber. Geprueft wird mit
/// echten Fenstern auf dem Schirm, nicht mit Bildern: genau das hatten die Tests vorher verpasst
/// (siehe LayeredHud). Nie, waehrend gespielt wird -- die Fenster lagen sonst ueber dem Spiel.
/// </remarks>
internal static class HudAufnahmePruefung
{
    public static int Run()
    {
        if (GameArea.VollbildVorne(out var wer))
        {
            Console.WriteLine($"Abgebrochen: {wer} laeuft im Vollbild vorne.");
            return 2;
        }
        if (System.Diagnostics.Process.GetProcesses().Any(p => GameWatch.IstForza(p.ProcessName)))
        {
            Console.WriteLine("Abgebrochen: Forza laeuft.");
            return 2;
        }
        Console.WriteLine($"Fensteraufnahme: {FensterQuelle.Unterstuetzt}, ohne Rahmen: {FensterQuelle.RahmenAbschaltbar}");
        if (!Spielaufnahme.Moeglich)
        {
            Console.WriteLine("Nicht moeglich auf diesem Windows -- der HUD bleibt aus Aufnahmen heraus.");
            return 0;
        }
        var fehler = 0;
        var schirm = Screen.PrimaryScreen!.WorkingArea;
        using var spiel = new Form
        {
            FormBorderStyle = FormBorderStyle.None, BackColor = Color.FromArgb(200, 20, 20), TopMost = true,
            StartPosition = FormStartPosition.Manual, ShowInTaskbar = false,
            Bounds = new Rectangle(schirm.X + 100, schirm.Y + 100, 800, 450), Text = "Ersatzspiel",
        };
        using var hud = new Form
        {
            FormBorderStyle = FormBorderStyle.None, BackColor = Color.FromArgb(255, 0, 255), TopMost = true,
            StartPosition = FormStartPosition.Manual, ShowInTaskbar = false,
            Bounds = new Rectangle(schirm.X + 300, schirm.Y + 250, 200, 120), Text = "Ersatz-HUD",
        };
        spiel.Show();
        hud.Show();
        hud.BringToFront();
        Warten(300);
        var settings = new OverlaySettings { HudInCaptures = true };
        Spielaufnahme.TestFenster = () => spiel.Handle;
        HudAufnahme.Anmelden(hud);
        HudAufnahme.Einstellen(settings);
        var bis = DateTime.UtcNow.AddSeconds(8);
        while (!Spielaufnahme.Bereit && DateTime.UtcNow < bis)
        {
            Warten(200);
            Spielaufnahme.Pruefen();
        }
        Console.WriteLine($"Bereit: {Spielaufnahme.Bereit}, HUD sichtbar: {HudAufnahme.Sichtbar}");
        Warten(300);
        var mitte = new Rectangle(hud.Left + 60, hud.Top + 40, 80, 40);

        // 1. Was die App liest: das Spiel, ohne HUD.
        using (var gelesen = GameArea.Capture(mitte, mitte.Size))
        {
            var c = Mittel(gelesen);
            Console.WriteLine($"  App liest unter dem HUD: {c}");
            if (!Rot(c)) { Console.WriteLine("FEHLER: die App liest ihren eigenen HUD (oder nichts)"); fehler++; }
        }
        using (var gegriffen = RivalsScreenReader.Grab(mitte))
        {
            var c = Mittel(gegriffen);
            Console.WriteLine($"  Bildschirmleser greift:  {c}");
            if (!Rot(c)) { Console.WriteLine("FEHLER: der Bildschirmleser sieht den HUD"); fehler++; }
        }
        // 2. Was Discord sieht: den Schirm, mit HUD.
        var discord = Schirm(mitte);
        Console.WriteLine($"  Bildschirmaufnahme:      {discord}");
        if (!Magenta(discord)) { Console.WriteLine("FEHLER: der HUD fehlt in der Bildschirmaufnahme"); fehler++; }
        // 2b. Wie Discord und OBS wirklich aufnehmen: den ganzen Bildschirm ueber Windows Graphics Capture.
        var hier = Screen.FromControl(spiel).Bounds;
        var monitor = Fenster.Bildschirme().FindIndex(s => s.Bounds == hier) + 1;
        using (var ganz = new FensterQuelle("screen:" + monitor))
        {
            Bitmap? bild = null;
            var warten = DateTime.UtcNow.AddSeconds(5);
            while ((bild = ganz.Neuestes()) is null && DateTime.UtcNow < warten) { Warten(100); }
            Warten(300);
            bild = ganz.Neuestes();
            if (bild is null || ganz.Schirmflaeche is not { } flaeche)
            {
                Console.WriteLine("  Bildschirm per Graphics Capture: kein Bild");
            }
            else
            {
                var c = Bildquellen.Mit(bild, b =>
                {
                    var kx = b.Width / (double)flaeche.Width;
                    var ky = b.Height / (double)flaeche.Height;
                    var r = Rectangle.Intersect(new Rectangle((int)((mitte.X - flaeche.X) * kx), (int)((mitte.Y - flaeche.Y) * ky),
                        Math.Max(1, (int)(mitte.Width * kx)), Math.Max(1, (int)(mitte.Height * ky))), new Rectangle(Point.Empty, b.Size));
                    Console.WriteLine($"  (Bild {b.Width}x{b.Height}, Flaeche {flaeche}, Ausschnitt {r})");
                    if (r.Width <= 0 || r.Height <= 0) { return Color.Black; }
                    using var teil = b.Clone(r, PixelFormat.Format24bppRgb);
                    return Mittel(teil);
                }, Color.Black);
                Console.WriteLine($"  Bildschirm per Graphics Capture: {c}");
                if (!Magenta(c)) { Console.WriteLine("FEHLER: der HUD fehlt in der Bildschirmaufnahme wie Discord sie macht"); fehler++; }
            }
        }

        // 3. Kein gelber Rahmen um das Spiel.
        var rand = Schirm(new Rectangle(spiel.Left + 200, spiel.Top, 300, 3));
        Console.WriteLine($"  Oberkante des Spiels:    {rand}");
        if (rand.R > 150 && rand.G > 150 && rand.B < 100) { Console.WriteLine("FEHLER: gelber Aufnahmerahmen sichtbar"); fehler++; }

        // 4. Ausgeschaltet: der HUD verschwindet wieder aus Aufnahmen, die App liest den Schirm.
        settings.HudInCaptures = false;
        HudAufnahme.Einstellen(settings);
        Warten(400);
        var aus = Schirm(mitte);
        Console.WriteLine($"  Aus -- Bildschirmaufnahme: {aus}");
        if (!Rot(aus)) { Console.WriteLine("FEHLER: ausgeschaltet bleibt der HUD in Aufnahmen"); fehler++; }
        using (var gelesen = GameArea.Capture(mitte, mitte.Size))
        {
            var c = Mittel(gelesen);
            Console.WriteLine($"  Aus -- App liest:          {c}");
            if (!Rot(c)) { Console.WriteLine("FEHLER: ausgeschaltet liest die App den HUD"); fehler++; }
        }
        Spielaufnahme.TestFenster = null;
        hud.Close();
        spiel.Close();
        Console.WriteLine(fehler == 0 ? "HUD capture check OK." : $"{fehler} Fehler.");
        return fehler == 0 ? 0 : 1;
    }

    private static void Warten(int ms)
    {
        var bis = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < bis)
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }
    }

    private static Color Schirm(Rectangle r)
    {
        using var b = new Bitmap(r.Width, r.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(b)) { g.CopyFromScreen(r.Location, Point.Empty, r.Size); }
        return Mittel(b);
    }

    private static Color Mittel(Bitmap b)
    {
        long r = 0, g = 0, bl = 0, n = 0;
        for (var y = 0; y < b.Height; y += 2)
        {
            for (var x = 0; x < b.Width; x += 2)
            {
                var c = b.GetPixel(x, y);
                r += c.R; g += c.G; bl += c.B; n++;
            }
        }
        return n == 0 ? Color.Black : Color.FromArgb((int)(r / n), (int)(g / n), (int)(bl / n));
    }

    private static bool Rot(Color c) => c.R > 150 && c.G < 80 && c.B < 80;

    private static bool Magenta(Color c) => c.R > 200 && c.G < 80 && c.B > 200;
}
