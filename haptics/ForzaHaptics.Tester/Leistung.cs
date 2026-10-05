using System.Diagnostics;

namespace ForzaHaptics.Tester;

/// <summary>
/// Was diese App kostet, waehrend Forza laeuft -- gezaehlt, nicht geschaetzt
/// (seit 2026-09-28).
/// </summary>
/// <remarks>
/// Einmal je Minute eine Zeile in %TEMP%\forza-overlay\perf.log: Prozessorzeit der App
/// (in Prozent EINES Kerns), wie oft und wie lange vom Bildschirm gelesen wurde, wie
/// oft die Overlays gezeichnet und die Controller beschrieben wurden -- und ob das Spiel
/// lief und gefahren wurde. So laesst sich jede Frage "bremst FHC das Spiel?" an einer
/// echten Sitzung beantworten statt am Schreibtisch.
///
/// Die Zaehler sind Interlocked-Additionen: billiger als alles, was sie zaehlen.
/// </remarks>
internal static class Leistung
{
    private static long _griffe, _griffTicks, _zeichnen, _zeichenTicks, _padSchreiben, _ocr, _ocrTicks;
    private static long _faehrt, _steht;
    private static DateTime _seit = DateTime.UtcNow;
    private static TimeSpan _cpuSeit = Aktuell();

    private static TimeSpan Aktuell()
    {
        try { return Process.GetCurrentProcess().TotalProcessorTime; } catch (Exception) { return TimeSpan.Zero; }
    }

    /// <summary>Ein Griff auf den Bildschirm (GDI), mit seiner Dauer.</summary>
    public static void Griff(long ticks)
    {
        Interlocked.Increment(ref _griffe);
        Interlocked.Add(ref _griffTicks, ticks);
    }

    /// <summary>Ein Overlay hat gezeichnet und uebergeben.</summary>
    public static void Gezeichnet(long ticks)
    {
        Interlocked.Increment(ref _zeichnen);
        Interlocked.Add(ref _zeichenTicks, ticks);
    }

    /// <summary>Texterkennung gelaufen.</summary>
    public static void Ocr(long ticks)
    {
        Interlocked.Increment(ref _ocr);
        Interlocked.Add(ref _ocrTicks, ticks);
    }

    /// <summary>Berichte an Controller, die wirklich hinausgingen.</summary>
    public static void Pad(int anzahl)
    {
        if (anzahl > 0) { Interlocked.Add(ref _padSchreiben, anzahl); }
    }

    /// <summary>Ein Takt, in dem gefahren (oder gestanden) wurde -- fuer den Zusammenhang.</summary>
    public static void Zustand(bool faehrt)
    {
        if (faehrt) { Interlocked.Increment(ref _faehrt); } else { Interlocked.Increment(ref _steht); }
    }

    /// <summary>Hoechstens einmal je Minute eine Zeile schreiben -- wenn das Spiel lief.</summary>
    public static void Protokolliere(bool spielLaeuft, bool imVordergrund)
    {
        var jetzt = DateTime.UtcNow;
        var dauer = jetzt - _seit;
        if (dauer < TimeSpan.FromMinutes(1)) { return; }
        var cpu = Aktuell();
        var anteil = dauer.TotalMilliseconds > 0 ? (cpu - _cpuSeit).TotalMilliseconds / dauer.TotalMilliseconds * 100 : 0;
        static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        var faehrt = Interlocked.Exchange(ref _faehrt, 0);
        var steht = Interlocked.Exchange(ref _steht, 0);
        var zeile = $"cpu {anteil:0.0}% of one core"
                    + $" | screen grabs {Interlocked.Exchange(ref _griffe, 0)} ({Ms(Interlocked.Exchange(ref _griffTicks, 0)):0} ms)"
                    + $" | ocr {Interlocked.Exchange(ref _ocr, 0)} ({Ms(Interlocked.Exchange(ref _ocrTicks, 0)):0} ms)"
                    + $" | overlay draws {Interlocked.Exchange(ref _zeichnen, 0)} ({Ms(Interlocked.Exchange(ref _zeichenTicks, 0)):0} ms)"
                    + $" | controller writes {Interlocked.Exchange(ref _padSchreiben, 0)}"
                    + $" | game {(spielLaeuft ? (imVordergrund ? "foreground" : "background") : "not running")}"
                    + $" | driving {(faehrt + steht > 0 ? faehrt * 100 / (faehrt + steht) : 0)}% of the time"
                    + $" | overlays over the game {(Rivals.OverlayAusgabe.ImSpiel ? "on" : "off")}";
        _seit = jetzt;
        _cpuSeit = cpu;
        if (!spielLaeuft) { return; }
        try
        {
            var ordner = Path.Combine(Path.GetTempPath(), "forza-overlay");
            Directory.CreateDirectory(ordner);
            File.AppendAllText(Path.Combine(ordner, "perf.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {zeile}{Environment.NewLine}");
        }
        catch (Exception)
        {
        }
    }
}
