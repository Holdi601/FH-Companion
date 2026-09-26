using System.Reflection;

namespace ForzaHaptics.Tester;

/// <summary>
/// Name, Ordner und Server der App an EINER Stelle -- und der Umzug vom alten Namen.
/// </summary>
/// <remarks>
/// ## Der Umzug (2026-09-26)
///
/// Bis dahin hiess die App "Forza Grip Haptics", ihre Daten lagen unter
/// %LOCALAPPDATA%\ForzaGripHaptics. Seitdem "FH Companion". Drei Dinge muessen
/// den Wechsel ueberleben:
///
///   1. Der Selbstaktualisierer der ALTEN Fassung startet nach dem Austausch fest
///      "Forza Grip Haptics.exe". robocopy loescht nichts, die alte Programmdatei
///      bliebe also liegen und startete die alte Fassung -- die sofort wieder das
///      Update anbietet. Darum liefert das Paket unter dem alten Namen eine KOPIE
///      des neuen Starters mit: der Starter traegt den Namen der DLL in sich
///      ("FH Companion.dll"), nicht seinen eigenen.
///   2. Die Daten (Runden, Anmeldung, Einstellungen der Overlays) ziehen beim
///      ersten Start in den neuen Ordner um. Scheitert das (eine Datei ist noch
///      offen), bleibt es fuer diesen Lauf beim alten Ordner -- nichts geht
///      verloren, der naechste Start versucht es wieder.
///   3. Verknuepfungen auf Schreibtisch, Startmenue und Autostart, die auf DIESE
///      Kopie zeigen, bekommen den neuen Namen. Eine Anheftung an die Taskleiste
///      darf die App nicht anfassen (siehe Shortcuts); sie zeigt weiter auf die
///      Kopie unter dem alten Namen und funktioniert darum weiter.
/// </remarks>
internal static class AppInfo
{
    /// <summary>Wie die App heisst -- Fenster, Verknuepfungen, Paket.</summary>
    public const string Name = "FH Companion";

    /// <summary>Die Programmdatei.</summary>
    public const string ExeName = "FH Companion.exe";

    /// <summary>Der Name bis 2026-09-26; eine Kopie des Starters liegt weiter so da.</summary>
    public const string OldName = "Forza Grip Haptics";

    public const string OldExeName = OldName + ".exe";

    /// <summary>Kennung fuer Browserkennung und Ordner -- ohne Leerzeichen.</summary>
    public const string Id = "FHCompanion";

    private const string OldId = "ForzaGripHaptics";

    /// <summary>Die Programmdatei DIESER Kopie, unter dem neuen Namen.</summary>
    public static string ExePath => Path.Combine(AppContext.BaseDirectory, ExeName);

    /// <summary>Dieselbe Kopie unter dem alten Namen.</summary>
    public static string OldExePath => Path.Combine(AppContext.BaseDirectory, OldExeName);

    private static readonly Lazy<string> _daten = new(DatenOrdner);

    /// <summary>
    /// %LOCALAPPDATA%\FHCompanion -- oder, solange der Umzug nicht gelang, der alte.
    /// </summary>
    public static string DataFolder => _daten.Value;

    /// <summary>Ein Ordner fuer Zwischenstaende im TEMP.</summary>
    public static string TempFolder => Path.Combine(Path.GetTempPath(), Id);

    private static string DatenOrdner()
    {
        var basis = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var neu = Path.Combine(basis, Id);
        var alt = Path.Combine(basis, OldId);
        return Umzug(alt, neu);
    }

    /// <summary>
    /// Den alten Ordner an den neuen Platz schieben, wenn es den neuen noch nicht
    /// gibt. Gibt zurueck, welcher gilt. Oeffentlich fuer den Selbsttest.
    /// </summary>
    internal static string Umzug(string alt, string neu)
    {
        if (Directory.Exists(neu) || !Directory.Exists(alt)) { return neu; }
        try
        {
            Directory.Move(alt, neu);
            return neu;
        }
        catch (Exception)
        {
            // Eine offene Datei (etwa das Austauschskript des alten Aktualisierers,
            // das gerade noch laeuft). Dann eben diesmal der alte Ordner.
            return alt;
        }
    }

    /// <summary>
    /// Der Server, den die App ohne eigene Einstellung fragt. Kommt beim Bauen aus
    /// config/local.json ("server"); ohne diese Datei ist er leer, und die App
    /// oeffnet keine Verbindung von sich aus.
    /// </summary>
    public static string DefaultServer { get; } =
        typeof(AppInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "DefaultServer")?.Value?.Trim() ?? string.Empty;

    /// <summary>
    /// Nach dem Umbenennen aufraeumen: Reste der alten Fassung im Programmordner und
    /// Verknuepfungen unter dem alten Namen. Jeder Schritt fuer sich; ein Fehler
    /// haelt nichts auf.
    /// </summary>
    public static void Aufraeumen()
    {
        // Die alte DLL samt Beschreibung braucht niemand mehr: die Kopie unter dem
        // alten Namen startet "FH Companion.dll". Nur loeschen, wenn der neue
        // Starter wirklich daneben liegt -- sonst waere das die einzige Fassung.
        if (File.Exists(ExePath))
        {
            foreach (var endung in new[] { ".dll", ".deps.json", ".runtimeconfig.json", ".pdb" })
            {
                try
                {
                    var rest = Path.Combine(AppContext.BaseDirectory, OldName + endung);
                    if (File.Exists(rest)) { File.Delete(rest); }
                }
                catch (Exception) { }
            }
        }

        foreach (var ort in new[] { ShortcutPlace.Desktop, ShortcutPlace.StartMenu, ShortcutPlace.Autostart })
        {
            try { Shortcuts.Umbenennen(ort); }
            catch (Exception) { }
        }
    }
}
