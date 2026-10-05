namespace ForzaHaptics.Tester;

/// <summary>
/// Anknuepfpunkte fuer Teile, die nur in einem lokalen Bau dabei sind. Sie liegen nicht im Repository
/// und nicht im veroeffentlichten Programm; ohne sie tut jeder Aufruf hier nichts (partielle Methoden
/// ohne Umsetzung entfallen beim Uebersetzen).
/// </summary>
internal static partial class Erweiterung
{
    /// <summary>Was ein Reiter braucht: Telemetrie, Einstellungen, Berater, Notizen.</summary>
    internal sealed record Umgebung(
        Func<ForzaPacket?> Telemetrie,
        Rivals.OverlaySettings Einstellungen,
        Func<Rivals.RivalsAdvisor?> Berater,
        Func<Rivals.CarNotes> Notizen);

    /// <summary>Weitere Reiter anhaengen.</summary>
    internal static void Reiter(TabControl reiter, Umgebung umgebung) => ReiterLokal(reiter, umgebung);

    static partial void ReiterLokal(TabControl reiter, Umgebung umgebung);

    /// <summary>Ein weiterer Kommandozeilenbefehl. True, wenn er erledigt wurde.</summary>
    internal static bool Befehl(string[] args)
    {
        var erledigt = false;
        BefehlLokal(args, ref erledigt);
        return erledigt;
    }

    static partial void BefehlLokal(string[] args, ref bool erledigt);

    /// <summary>Weitere Pruefungen in den Grenzfalltests.</summary>
    internal static void Pruefen(Action<bool, string> soll) => PruefenLokal(soll);

    static partial void PruefenLokal(Action<bool, string> soll);

    /// <summary>Weitere Pruefungen im Selbsttest.</summary>
    internal static void Selbsttest() => SelbsttestLokal();

    static partial void SelbsttestLokal();
}
