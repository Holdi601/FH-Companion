using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ForzaHaptics.Tester.Scan;

/// <summary>
/// WANN DER SCAN AUFHOERT -- UND WANN NICHT (seit 2026-11-10).
/// </summary>
/// <remarks>
/// Der Nutzer, nach einer Nacht, in der der Scan zweimal stehen blieb (03:00 "the game is no longer
/// in front", 09:39 nach einem Server Error): er soll nur aufhoeren, wenn jemand es ausdruecklich
/// will -- eine Stopptaste (Pause, Entf, Strg+C), "Stop" in der App, oder das Spiel ist zu (Alt+F4).
/// Alles andere ist kein Grund:
///
/// - IST DAS SPIEL NICHT MEHR VORN, holt der Scanner es zurueck. Fuenf Minuten lang, alle zwei
///   Sekunden; erst wenn es dann noch immer nicht vorn ist, hoert er auf. Bis dahin hiess ein
///   anderes Fenster vorn "angehalten" -- nachts reicht dafuer eine Meldung von Windows.
/// - DIE STOPPTASTEN werden auf einem eigenen Faden abgefragt, nicht erst vor dem naechsten Druck:
///   der Scanner drueckt waehrend einer Wartezeit minutenlang nichts. Er selbst sendet nur Pfeile,
///   Y, ENTER und ESC -- keine der Stopptasten kommt also von ihm.
/// </remarks>
internal static class ScanWache
{
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);

    private const int PauseTaste = 0x13, Entf = 0x2E, Strg = 0x11, TasteC = 0x43;

    /// <summary>So lange versucht der Scanner, das Spiel wieder nach vorn zu holen.</summary>
    public static readonly TimeSpan NachVornHoechstens = TimeSpan.FromMinutes(5);

    private static int _laeuft;
    private static string? _taste;

    /// <summary>Wohin die Wache berichtet (das Protokoll des Reiters); null = nirgends.</summary>
    public static Action<string>? Melden { get; set; }

    /// <summary>Welche Stopptaste gedrueckt wurde, seit die Wache laeuft -- null, wenn keine.</summary>
    public static string? Taste => Volatile.Read(ref _taste);

    /// <summary>Die Stopptasten abfragen, bis <see cref="Ende"/>. Was vorher gedrueckt war, zaehlt nicht.</summary>
    public static void Start()
    {
        Volatile.Write(ref _taste, null);
        if (Interlocked.Exchange(ref _laeuft, 1) == 1) { return; }
        // Das "seit der letzten Abfrage gedrueckt"-Bit leeren: ein Druck von vor einer Stunde haelt nichts an.
        Gedrueckt();
        new Thread(Lauschen) { IsBackground = true, Name = "Scan-Stopptasten" }.Start();
    }

    public static void Ende() => Interlocked.Exchange(ref _laeuft, 0);

    private static void Lauschen()
    {
        while (Volatile.Read(ref _laeuft) == 1)
        {
            if (Gedrueckt() is { } t) { Interlocked.CompareExchange(ref _taste, t, null); }
            Thread.Sleep(25);
        }
    }

    /// <summary>Welche Stopptaste gerade gedrueckt ist oder seit der letzten Abfrage gedrueckt wurde.</summary>
    private static string? Gedrueckt()
    {
        var pause = Unten(PauseTaste);
        var entf = Unten(Entf);
        var c = Unten(TasteC);
        if (pause) { return "the Pause key"; }
        if (entf) { return "the Delete key"; }
        return c && (GetAsyncKeyState(Strg) & 0x8000) != 0 ? "Ctrl+C" : null;
    }

    private static bool Unten(int vk) => (GetAsyncKeyState(vk) & 0x8001) != 0;

    /// <summary>Laeuft das Spiel ueberhaupt noch?</summary>
    public static bool SpielLaeuft()
    {
        var p = Process.GetProcessesByName(GameWatch.DefaultProcessName);
        var da = p.Length > 0;
        foreach (var x in p) { x.Dispose(); }
        return da;
    }

    private static bool Vorn() => GameWatch.ForegroundProcessName() == GameWatch.DefaultProcessName;

    /// <summary>Wirft, wenn der Scan aufhoeren soll: Stop in der App, eine Stopptaste, das Spiel ist zu.</summary>
    public static void Halt(CancellationToken stop)
    {
        if (stop.IsCancellationRequested) { throw new ScanNavigator.Abbruch("stopped in the app") { VomNutzer = true }; }
        if (Taste is { } t) { throw new ScanNavigator.Abbruch("stopped with " + t) { VomNutzer = true }; }
        if (!SpielLaeuft()) { throw new ScanNavigator.Abbruch("stopped: the game was closed") { VomNutzer = true }; }
    }

    /// <summary>
    /// Vor jedem Druck: darf weitergemacht werden? Ist das Spiel nicht vorn, wird es zurueckgeholt --
    /// bis zu fuenf Minuten; erst dann wirft es "did not come back".
    /// </summary>
    public static void Pruefe(CancellationToken stop)
    {
        Halt(stop);
        if (Vorn()) { return; }
        Melden?.Invoke("the game is not in front -- bringing it back");
        var uhr = Stopwatch.StartNew();
        var versuche = 0;
        while (uhr.Elapsed < NachVornHoechstens)
        {
            Halt(stop);
            versuche++;
            if (Vorn() || ScanEingabe.SpielNachVorn())
            {
                // Erst ruhig vorn, dann weiter: der erste Druck nach einem Fensterwechsel geht sonst verloren.
                if (stop.WaitHandle.WaitOne(1200)) { Halt(stop); }
                if (Vorn())
                {
                    Melden?.Invoke($"the game is back in front after {uhr.Elapsed.TotalSeconds:0} s ({versuche} tries)");
                    return;
                }
            }
            if (stop.WaitHandle.WaitOne(2000)) { Halt(stop); }
        }
        throw new ScanNavigator.Abbruch($"the game did not come back to the front within {NachVornHoechstens.TotalMinutes:0} minutes")
        {
            VomNutzer = true,
        };
    }

    /// <summary>Warten, ohne etwas zu druecken -- aber eine Stopptaste oder ein geschlossenes Spiel beenden es sofort.</summary>
    public static void Warte(TimeSpan dauer, CancellationToken stop)
    {
        var ende = DateTime.UtcNow + dauer;
        while (DateTime.UtcNow < ende)
        {
            Halt(stop);
            stop.WaitHandle.WaitOne(500);
        }
        Halt(stop);
    }
}
