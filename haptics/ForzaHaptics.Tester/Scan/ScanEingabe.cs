using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace ForzaHaptics.Tester.Scan;

/// <summary>
/// Tasten ans Spiel und Bilder vom Spiel -- fuer den Scanner. Vor jedem Druck die Pruefung, ob
/// weitergemacht werden darf: Abbruch in der App, die Pause-Taste, oder das Spiel nicht mehr vorn.
/// </summary>
internal sealed class ScanEingabe
{
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);

    internal const byte Enter = 0x0D, Esc = 0x1B, Links = 0x25, Hoch = 0x26, Rechts = 0x27, Runter = 0x28, TasteY = 0x59;
    private const int PauseTaste = 0x13;

    private readonly CancellationToken _stop;

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);

    /// <summary>Wie lange eine Taste gehalten wird (ms).</summary>
    public int Halten { get; init; } = 20;

    public ScanEingabe(CancellationToken stop)
    {
        _stop = stop;
        // Ohne das schlaeft Thread.Sleep in 15,6-ms-Stufen: aus 30 + 25 ms je Taste wurden ~62.
        timeBeginPeriod(1);
    }

    public void PruefeWeiter()
    {
        if (_stop.IsCancellationRequested) { throw new ScanNavigator.Abbruch("stopped in the app") { VomNutzer = true }; }
        if ((GetAsyncKeyState(PauseTaste) & 0x8000) != 0) { throw new ScanNavigator.Abbruch("stopped with the Pause key") { VomNutzer = true }; }
        if (GameWatch.ForegroundProcessName() != GameWatch.DefaultProcessName)
        {
            throw new ScanNavigator.Abbruch("the game is no longer in front") { VomNutzer = true };
        }
    }

    public void Taste(byte vk, int nachher)
    {
        PruefeWeiter();
        var scan = (byte)MapVirtualKey(vk, 0);
        var erweitert = vk is Links or Hoch or Rechts or Runter ? 1u : 0u;
        keybd_event(vk, scan, erweitert, UIntPtr.Zero);
        Thread.Sleep(Halten);
        keybd_event(vk, scan, erweitert | 2u, UIntPtr.Zero);
        if (nachher > 0) { Thread.Sleep(nachher); }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    /// <summary>
    /// Das Spiel nach vorn holen, auch wenn dieses Programm selbst nicht vorn ist: Windows laesst
    /// SetForegroundWindow nur dem Vordergrund-Thread zu, also wird dessen Eingabe kurz angehaengt.
    /// Nur auf ausdruecklichen Wunsch (--focus bzw. der Knopf im Reiter).
    /// </summary>
    public static bool SpielNachVorn()
    {
        try
        {
            var p = System.Diagnostics.Process.GetProcessesByName(GameWatch.DefaultProcessName).FirstOrDefault();
            if (p is null || p.MainWindowHandle == IntPtr.Zero) { return false; }
            var spiel = p.MainWindowHandle;
            var vorn = GetForegroundWindow();
            var vornThread = GetWindowThreadProcessId(vorn, out _);
            var ich = GetCurrentThreadId();
            var angehaengt = vornThread != 0 && vornThread != ich && AttachThreadInput(ich, vornThread, true);
            try
            {
                ShowWindow(spiel, 9);   // SW_RESTORE
                BringWindowToTop(spiel);
                SetForegroundWindow(spiel);
            }
            finally
            {
                if (angehaengt) { AttachThreadInput(ich, vornThread, false); }
            }
            Thread.Sleep(400);
            return GameWatch.ForegroundProcessName() == GameWatch.DefaultProcessName;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static Bitmap Aufnahme() =>
        GameArea.Capture(GameArea.SixteenNine(GameArea.Find(GameWatch.DefaultProcessName)), new Size(1920, 1080));

    /// <summary>Wie lange etwas dauert, fuer die Statistik des Laufs.</summary>
    public static Stopwatch Uhr() => Stopwatch.StartNew();
}

/// <summary>
/// Nur EINE Automatik fuehrt das Spiel: Bestenlisten-Scan, Auktionsbieter oder Tune-Loeschen.
/// </summary>
/// <remarks>
/// Jede prueft den Schirm und drueckt dann ENTER -- drueckt dazwischen eine zweite ESC oder eine
/// Pfeiltaste, faellt das gepruefte ENTER auf etwas anderes (eine Online-Veranstaltung, "Buy Out",
/// einen Loeschdialog). Darum haelt jede den Griff fuer ihren ganzen Lauf, auf dem Faden, der die
/// Tasten drueckt. Ein benannter Mutex, damit auch eine zweite Instanz (die Befehlszeile neben der
/// App) nicht dazwischenfaehrt; ein Mutex gehoert seinem Faden -- abgegeben wird auf demselben.
/// </remarks>
internal static class SpielSperre
{
    private const string Name = @"Local\FHCompanion-GameInput";
    private static readonly object Schloss = new();
    private static string? _besitzer;

    /// <summary>Wer das Spiel in DIESEM Prozess gerade fuehrt; null, wenn niemand.</summary>
    public static string? Besitzer => Volatile.Read(ref _besitzer);

    /// <summary>Fuehrt gerade eine Automatik das Spiel -- in diesem oder einem anderen Prozess?</summary>
    public static bool Belegt()
    {
        if (Besitzer is not null) { return true; }
        using var griff = Nehmen("check");
        return griff is null;
    }

    /// <summary>Den Griff nehmen, ohne zu warten; null, wenn eine andere Automatik ihn haelt.</summary>
    public static Griff? Nehmen(string wer)
    {
        lock (Schloss)
        {
            if (_besitzer is not null) { return null; }
            Mutex m;
            try { m = new Mutex(false, Name); }
            catch (Exception) { return null; }
            bool hat;
            try { hat = m.WaitOne(0); }
            catch (AbandonedMutexException) { hat = true; }   // der vorige Halter ist gestorben
            catch (Exception) { hat = false; }
            if (!hat)
            {
                m.Dispose();
                return null;
            }
            _besitzer = wer;
            return new Griff(m);
        }
    }

    internal sealed class Griff : IDisposable
    {
        private Mutex? _m;

        internal Griff(Mutex m) => _m = m;

        public void Dispose()
        {
            var m = Interlocked.Exchange(ref _m, null);
            if (m is null) { return; }
            lock (Schloss) { _besitzer = null; }
            try { m.ReleaseMutex(); }
            catch (Exception) { }   // auf einem fremden Faden: der Mutex faellt mit dem Faden
            m.Dispose();
        }
    }
}
