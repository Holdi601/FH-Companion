using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ForzaHaptics.Tester;

/// <summary>
/// "--ui-timing": wie lange das Hauptfenster zum Bauen braucht, wie lange jeder Reiter beim
/// ersten und beim zweiten Oeffnen, und wie lange ein Klick auf jedes Stueck im Reiter
/// "Lap delta HUD". Dazu Steuerelemente und Fenster-Handles je Reiter.
/// </summary>
/// <remarks>
/// Seit 2026-11-14 (Nutzer: "nach einem Klick auf einen Tab dauert es solide 5 Sekunden").
/// Das Fenster steht abseits des Schirms wie bei --main-preview (MainForm.NurVorschau).
/// </remarks>
internal static class UiTiming
{
    [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr prozess, int welche);

    private static int User => GetGuiResources(Process.GetCurrentProcess().Handle, 1);
    private static int Gdi => GetGuiResources(Process.GetCurrentProcess().Handle, 0);

    private static int Zaehle(Control c)
    {
        var n = 1;
        foreach (Control k in c.Controls) { n += Zaehle(k); }
        return n;
    }

    private static double Pumpe(Form f, Action tun)
    {
        var uhr = Stopwatch.StartNew();
        tun();
        f.Refresh();
        for (var i = 0; i < 5; i++) { Application.DoEvents(); }
        f.Refresh();
        return uhr.Elapsed.TotalMilliseconds;
    }

    public static void Run()
    {
        MainForm.NurVorschau = true;
        Loc.Waehle(Rivals.OverlaySettings.Load().Language);
        var uhr = Stopwatch.StartNew();
        using var fenster = new MainForm(false)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new System.Drawing.Point(-32000, -32000),
            ShowInTaskbar = false,
            Size = new System.Drawing.Size(1600, 950),
        };
        var bauen = uhr.Elapsed.TotalMilliseconds;
        var zeigen = Pumpe(fenster, fenster.Show);
        Console.WriteLine($"Hauptfenster bauen {bauen:0} ms, zeigen {zeigen:0} ms, USER {User}, GDI {Gdi}");

        var tabs = new Stack<Control>(new Control[] { fenster });
        TabControl? reiter = null;
        while (tabs.Count > 0 && reiter is null)
        {
            var c = tabs.Pop();
            if (c is TabControl t) { reiter = t; break; }
            foreach (Control k in c.Controls) { tabs.Push(k); }
        }
        if (reiter is null) { Console.WriteLine("kein TabControl"); return; }
        var start = reiter.SelectedTab;
        foreach (var runde in new[] { "erstes", "zweites" })
        {
            Console.WriteLine($"-- {runde} Oeffnen");
            foreach (TabPage seite in reiter.TabPages)
            {
                if (ReferenceEquals(seite, start) && runde == "erstes") { continue; }
                var u0 = User;
                var ms = Pumpe(fenster, () => reiter.SelectedTab = seite);
                Console.WriteLine($"  {seite.Text,-28} {ms,7:0} ms  Steuerelemente {Zaehle(seite),5}  USER {User} (+{User - u0})");
                foreach (var cc in Alle(seite).OfType<Rivals.CarCollectionTab>())
                {
                    Console.WriteLine($"      Car collection: rechnen {cc.RechnenMs:0} ms, Liste {cc.ListeMs:0} ms");
                }
            }
        }

        foreach (var cc in fenster.Controls.Cast<Control>().SelectMany(Alle).OfType<Rivals.CarCollectionTab>())
        {
            var seite = (TabPage)cc.Parent!;
            Pumpe(fenster, () => reiter.SelectedTab = seite);
            var u = Stopwatch.StartNew();
            cc.Zeigen();
            var z = u.Elapsed.TotalMilliseconds;
            u.Restart();
            fenster.Refresh();
            Console.WriteLine($"Car collection: Zeigen {z:0} ms, Zeichnen {u.Elapsed.TotalMilliseconds:0} ms");
        }
        var hud = fenster.Controls.Cast<Control>().SelectMany(Alle).OfType<Rivals.HudTab>().FirstOrDefault();
        if (hud is not null)
        {
            var seite = (TabPage)hud.Parent!;
            Pumpe(fenster, () => reiter.SelectedTab = seite);
            Console.WriteLine("-- Klick auf ein Stueck (Lap delta HUD)");
            foreach (var teil in Enum.GetValues<Rivals.HudPart>())
            {
                var u0 = User;
                var ms = Pumpe(fenster, () => hud.KlickAuf(teil));
                Console.WriteLine($"  {teil,-14} {ms,7:0} ms  USER +{User - u0}");
            }
        }
        Console.WriteLine($"Ende: USER {User}, GDI {Gdi}, Speicher {Environment.WorkingSet / 1048576} MB");
    }

    private static IEnumerable<Control> Alle(Control c)
    {
        yield return c;
        foreach (Control k in c.Controls)
        {
            foreach (var x in Alle(k)) { yield return x; }
        }
    }
}
