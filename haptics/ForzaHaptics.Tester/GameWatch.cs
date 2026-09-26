using System.Diagnostics;

namespace ForzaHaptics.Tester;

/// <summary>
/// Is Forza actually running? Asked rarely, answered from cache.
/// </summary>
/// <remarks>
/// Two things in this app reach past their own window and touch the machine: the
/// 50 Hz firewall, which overwrites whatever the selected controller is doing, and
/// the overlay, which puts panels on top of everything. Both are wanted during a
/// race and neither is wanted otherwise -- a controller that keeps buzzing in
/// another game, or a panel hanging over the desktop, is the app misbehaving.
///
/// So both ask here first. The process is the honest test: the telemetry port only
/// says whether Data Out is configured, and it goes quiet in menus and on a pause,
/// where the panels are precisely what someone wants to look at.
///
/// Polled every two seconds, not per frame. `GetProcessesByName` walks the process
/// table -- at 50 Hz that would be the most expensive thing in the loop, and the
/// answer cannot change meaningfully between two frames anyway.
/// </remarks>
internal sealed class GameWatch
{
    /// <summary>The Steam build's process name, as the scanner's own checks use it.</summary>
    public const string DefaultProcessName = "forzahorizon6";

    private static readonly TimeSpan Period = TimeSpan.FromSeconds(2);

    private readonly string _name;
    private DateTime _asked = DateTime.MinValue;
    private bool _running;

    public GameWatch(string? processName = null)
    {
        var wanted = (processName ?? string.Empty).Trim();
        if (wanted.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            wanted = wanted[..^4];
        }
        _name = wanted.Length > 0 ? wanted : DefaultProcessName;
    }

    /// <summary>True while a matching process exists. Never throws.</summary>
    public bool Running
    {
        get
        {
            var now = DateTime.UtcNow;
            if (now - _asked < Period)
            {
                return _running;
            }
            _asked = now;
            _running = Look();
            return _running;
        }
    }

    /// <summary>What is being looked for, for a status line that has to explain itself.</summary>
    public string ProcessName => _name;

    // ------------------------------------------------------------------ //
    // Ist das Spiel auch VORNE?
    // ------------------------------------------------------------------ //

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private DateTime _fokusGefragt = DateTime.MinValue;
    private bool _imVordergrund;

    /// <summary>
    /// Ob das Spiel gerade das aktive Fenster ist.
    /// </summary>
    /// <remarks>
    /// "Laeuft der Prozess" und "ist er vorne" sind zwei verschiedene Fragen, und
    /// bis zum 2026-09-15 wurde nur die erste gestellt. Wer auf den Desktop wechselte,
    /// behielt das Overlay ueber allem stehen -- es ist ja immer im Vordergrund und
    /// nimmt keinen Klick an, also auch nicht wegzuklicken.
    ///
    /// Gefragt wird ueber das Fenster im Vordergrund und dessen Prozess. Nicht ueber
    /// `MainWindowHandle` des Spiels: der Handle ist bei laufendem Spiel zeitweise 0
    /// (siehe die Notiz "Forza window handle is zero"), und dann saehe es aus, als
    /// waere das Spiel weg, waehrend es nur laedt.
    ///
    /// Derselbe Takt wie <see cref="Running"/>: zweimal je Sekunde den
    /// Prozessnamen eines Fensters nachzuschlagen ist billig, aber sechzigmal --
    /// `PollTriggers` laeuft alle 60 ms -- waere Verschwendung.
    /// </remarks>
    public bool IsForeground
    {
        get
        {
            var jetzt = DateTime.UtcNow;
            if (jetzt - _fokusGefragt < ForegroundPeriod)
            {
                return _imVordergrund;
            }
            _fokusGefragt = jetzt;
            _imVordergrund = LookForeground();
            return _imVordergrund;
        }
    }

    private static readonly TimeSpan ForegroundPeriod = TimeSpan.FromMilliseconds(400);

    /// <summary>Welcher Prozess gerade vorn ist -- fuer Pruefung und Fehlersuche.</summary>
    public static string ForegroundProcessName()
    {
        try
        {
            var fenster = GetForegroundWindow();
            if (fenster == IntPtr.Zero) { return "(kein Vordergrundfenster)"; }
            if (GetWindowThreadProcessId(fenster, out var pid) == 0 || pid == 0)
            {
                return "(kein Prozess zum Fenster)";
            }
            using var prozess = Process.GetProcessById((int)pid);
            return prozess.ProcessName;
        }
        catch (Exception fehler)
        {
            return "(" + fehler.GetType().Name + ")";
        }
    }

    private bool LookForeground()
    {
        try
        {
            var fenster = GetForegroundWindow();
            if (fenster == IntPtr.Zero) { return false; }
            if (GetWindowThreadProcessId(fenster, out var pid) == 0 || pid == 0)
            {
                return false;
            }
            using var prozess = Process.GetProcessById((int)pid);
            var name = prozess.ProcessName;
            if (string.Equals(name, _name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Die Ausweichform faengt das Spiel unter einem anderen Baunamen -- aber
            // NUR, wenn ueberhaupt nach dem Spiel gesucht wird.
            //
            // HIER STRENGER ALS IN Look(), und das mit Absicht. "Laeuft es
            // ueberhaupt" darf grosszuegig sein: eine falsche Ja-Antwort kostet
            // eine unnoetig wache Haptik. "Ist DIESES Fenster vorn" darf es nicht:
            // eine falsche Ja-Antwort laesst das Panel ueber dem Desktop stehen,
            // und weil es keinen Klick annimmt, bekommt man es nicht weg. Ohne diese Schranke
            // meldete eine Wache fuer "notepad" das laufende Forza als Treffer; am
            // 2026-09-15 genau so gemessen, als die Probe nach notepad fragte und
            // istVorne=True bekam.
            if (!string.Equals(_name, DefaultProcessName,
                               StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return name.Contains("forza", StringComparison.OrdinalIgnoreCase)
                   && name.Contains("horizon", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // Ein Fenster, dessen Prozess nicht mehr existiert, oder ein Prozess,
            // an den wir nicht herankommen. Beides heisst: nicht unser Spiel.
            return false;
        }
    }

    private bool Look()
    {
        try
        {
            // Exact name first -- it is one call and it is what a Steam install gives.
            foreach (var process in Process.GetProcessesByName(_name))
            {
                process.Dispose();
                return true;
            }

            // Then anything that is obviously the game under a different build name.
            // A wrong guess here costs a needlessly idle firewall, which is why the
            // fallback demands BOTH words rather than just "forza".
            foreach (var process in Process.GetProcesses())
            {
                var name = process.ProcessName;
                process.Dispose();
                if (name.Contains("forza", StringComparison.OrdinalIgnoreCase)
                    && name.Contains("horizon", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception exception)
        {
            // A process table that cannot be read must not stop the haptics: better to
            // assume the game is there than to sit silent through a whole race.
            Debug.WriteLine($"[game] {exception.Message}");
            return true;
        }
        return false;
    }
}
