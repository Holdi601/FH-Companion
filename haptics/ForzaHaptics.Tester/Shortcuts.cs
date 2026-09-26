namespace ForzaHaptics.Tester;

/// <summary>
/// Wo Windows eine Verknuepfung hinnehmen kann.
/// </summary>
internal enum ShortcutPlace
{
    /// <summary>Auf dem Schreibtisch.</summary>
    Desktop,

    /// <summary>Im Startmenue, also auch in der Suche.</summary>
    StartMenu,

    /// <summary>Im Autostart: laeuft mit, sobald sich jemand anmeldet.</summary>
    Autostart,
}

/// <summary>
/// Verknuepfungen anlegen -- wenn sie gewuenscht sind.
/// </summary>
/// <remarks>
/// ## Warum ueber COM und nicht als Datei
///
/// Eine `.lnk` ist ein Binaerformat (IShellLink), das sich nicht sinnvoll von Hand
/// schreiben laesst. Die Alternative waere eine `.url`-Datei -- die ist Text, kann
/// aber weder ein Symbol noch ein Arbeitsverzeichnis tragen und sieht auf dem
/// Schreibtisch aus wie eine Internetverknuepfung, was sie nicht ist.
///
/// `WScript.Shell` liegt in jedem Windows. Angesprochen wird es ueber Reflexion und
/// NICHT ueber `dynamic`: `dynamic` zieht `Microsoft.CSharp` in den Bau und
/// verschiebt jeden Tippfehler in die Laufzeit. Reflexion ist hier genauso kurz und
/// macht sichtbar, dass es ein fremdes Objekt ist.
///
/// ## Alle drei Orte sind derselbe Handgriff
///
/// Schreibtisch, Startmenue und Autostart sind nichts als drei Ordner, in denen
/// eine `.lnk` liegt. Es gibt dafuer keine je eigene Schnittstelle und keinen
/// Grund, drei Wege zu bauen.
///
/// ## WARUM ES KEIN "AN TASKLEISTE ANHEFTEN" GIBT
///
/// Weil Windows es nicht mehr zulaesst. Bis Windows 8 gab es das Kontextmenue-Verb
/// `taskbarpin`, das sich aus einem Programm heraus ausloesen liess. Ab Windows 10
/// 1607 hat Microsoft es entfernt: die Angeheftetenliste liegt seither in
/// `HKCU\...\Taskband` als undokumentierte Binaerstruktur, gegen die das System eine
/// Pruefsumme haelt, in die unter anderem die Programmkennung eingeht. Wer dort
/// hineinschreibt, faelscht diese Pruefsumme.
///
/// Das waere nicht nur unsauber, sondern genau das Verhalten, an dem
/// Schutzprogramme unsignierte Anwendungen erkennen -- und diese Anwendung hat
/// bereits einen Fehlalarm hinter sich (siehe docs/defender-false-positive.md).
/// Eine Anheftung zu erschleichen, die Windows absichtlich dem Nutzer vorbehaelt,
/// waere den Preis nicht wert.
///
/// Der ehrliche Weg ist der Eintrag im Startmenue: von dort ist "An Taskleiste
/// anheften" ein Rechtsklick, und die Entscheidung bleibt, wo sie hingehoert. Der
/// Text im Zustimmungsfenster sagt das auch so.
///
/// Der Knopf "Pin to taskbar" im Kopf des Hauptfensters (seit 2026-09-25) bleibt
/// bei dieser Linie: er legt den Startmenue-Eintrag an -- damit traegt die
/// Anheftung den richtigen Namen und das Symbol --, sagt, welcher eine Rechtsklick
/// noch fehlt, und zeigt ein Haekchen, sobald die Anheftung da ist
/// (<see cref="IsPinned"/> liest den Ordner, in den Windows sie legt; lesen ist
/// erlaubt, nur das Schreiben dort waere das Erschleichen).
///
/// ## Was hier NICHT passiert
///
/// Nie ungefragt. Der Aufrufer fragt beim ersten Start (siehe <see cref="Disclosure"/>),
/// und wer nicht zustimmt, bekommt nichts angelegt. Eine vorhandene Verknuepfung
/// wird nicht ueberschrieben: wer sie verschoben oder umbenannt hat, hat das so
/// gemeint.
/// </remarks>
internal static class Shortcuts
{
    /// <summary>Wie die Verknuepfung heisst.</summary>
    public const string Name = AppInfo.Name;

    /// <summary>Der Ordner zu einem Ort.</summary>
    private static Environment.SpecialFolder Ordner(ShortcutPlace ort) => ort switch
    {
        ShortcutPlace.StartMenu => Environment.SpecialFolder.Programs,
        ShortcutPlace.Autostart => Environment.SpecialFolder.Startup,
        _ => Environment.SpecialFolder.DesktopDirectory,
    };

    /// <summary>Wo die Verknuepfung laege.</summary>
    public static string PathFor(ShortcutPlace ort) =>
        System.IO.Path.Combine(Environment.GetFolderPath(Ordner(ort)),
                               Name + ".lnk");

    /// <summary>Gibt es sie schon?</summary>
    public static bool Exists(ShortcutPlace ort)
    {
        try { return File.Exists(PathFor(ort)); }
        catch (Exception) { return false; }
    }

    /// <summary>Die Programmdatei DIESER Kopie -- auf sie zeigt jede Verknuepfung.</summary>
    public static string ExePath => AppInfo.ExePath;

    /// <summary>
    /// Der Ordner, in den Windows die Anheftungen der Taskleiste legt. Hier wird nur
    /// GELESEN -- hineinzuschreiben waere genau das, was Windows dem Nutzer
    /// vorbehaelt (siehe oben).
    /// </summary>
    public static string PinnedFolder => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar");

    /// <summary>Wohin eine vorhandene Verknuepfung zeigt -- oder <c>null</c>.</summary>
    public static string? TargetOf(string lnk)
    {
        try
        {
            if (!File.Exists(lnk)) { return null; }
            return MitShell((typ, shell) =>
            {
                var obj = typ.InvokeMember("CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                if (obj is null) { return null; }
                try
                {
                    return obj.GetType().InvokeMember("TargetPath",
                        System.Reflection.BindingFlags.GetProperty, null, obj, null) as string;
                }
                finally { Freigeben(obj); }
            });
        }
        catch (Exception) { return null; }
    }

    /// <summary>Zeigt diese Verknuepfung auf DIESE Kopie des Programms?</summary>
    /// <remarks>
    /// Auch die Kopie unter dem alten Namen zaehlt (siehe AppInfo): eine Anheftung
    /// von vor der Umbenennung zeigt auf sie und startet trotzdem diese Fassung.
    /// </remarks>
    public static bool PointsHere(string lnk)
    {
        var ziel = TargetOf(lnk);
        return SamePath(ziel, ExePath) || SamePath(ziel, AppInfo.OldExePath);
    }

    /// <summary>
    /// Eine Verknuepfung unter dem alten Namen, die auf DIESE Kopie zeigt, durch eine
    /// unter dem neuen ersetzen. Eine auf eine andere Kopie bleibt, wie sie ist.
    /// <paramref name="ordner"/> nur fuer den Selbsttest.
    /// </summary>
    public static bool Umbenennen(ShortcutPlace ort, string? ordner = null)
    {
        var wo = ordner ?? Environment.GetFolderPath(Ordner(ort));
        var alt = System.IO.Path.Combine(wo, AppInfo.OldName + ".lnk");
        if (!File.Exists(alt) || !PointsHere(alt)) { return false; }
        var neu = System.IO.Path.Combine(wo, Name + ".lnk");
        if (!File.Exists(neu) && !Create(ort, out _, neu)) { return false; }
        File.Delete(alt);
        return true;
    }

    /// <summary>Da UND auf diese Kopie gerichtet -- nicht auf eine verschobene oder alte.</summary>
    public static bool IsCurrent(ShortcutPlace ort) => PointsHere(PathFor(ort));

    /// <summary>
    /// Ist diese Kopie an die Taskleiste angeheftet? <paramref name="ordner"/> nur
    /// fuer den Selbsttest; sonst <see cref="PinnedFolder"/>.
    /// </summary>
    public static bool IsPinned(string? ordner = null)
    {
        try
        {
            var wo = ordner ?? PinnedFolder;
            return Directory.Exists(wo)
                   && Directory.EnumerateFiles(wo, "*.lnk").Any(PointsHere);
        }
        catch (Exception) { return false; }
    }

    private static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) { return false; }
        try
        {
            return string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b),
                                 StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }

    private static T? MitShell<T>(Func<Type, object, T?> tun)
    {
        var typ = Type.GetTypeFromProgID("WScript.Shell")
                  ?? throw new InvalidOperationException("WScript.Shell ist auf diesem Windows nicht verfuegbar");
        var shell = Activator.CreateInstance(typ)
                    ?? throw new InvalidOperationException("WScript.Shell liess sich nicht erzeugen");
        try { return tun(typ, shell); }
        finally { Freigeben(shell); }
    }

    // Ein COM-Objekt, das niemand freigibt, haelt den Host am Leben.
    private static void Freigeben(object o)
    {
        if (System.Runtime.InteropServices.Marshal.IsComObject(o))
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(o);
        }
    }

    /// <summary>
    /// Anlegen. Gibt zurueck, ob danach eine Verknuepfung da ist.
    /// </summary>
    /// <remarks>
    /// Scheitern ist hier kein Grund, irgendetwas abzubrechen: die Verknuepfung ist
    /// eine Bequemlichkeit, das Programm laeuft ohne sie genauso. Der Aufrufer
    /// erfaehrt es und kann es sagen.
    /// </remarks>
    /// <param name="ziel">
    /// Wohin die Verknuepfung geschrieben wird. <c>null</c> heisst: an den Ort, den
    /// <paramref name="ort"/> nennt. Ueberschreibbar, damit der Selbsttest den
    /// COM-Weg wirklich gehen kann, ohne jemandem etwas auf den Schreibtisch zu
    /// legen -- ein Test, der den Schreibtisch des Nutzers vollstellt, wird beim
    /// ersten Mal abgeschaltet und prueft danach nie wieder etwas.
    /// </param>
    /// <param name="ersetzen">
    /// Eine vorhandene Verknuepfung, die NICHT auf diese Kopie zeigt, durch eine
    /// richtige ersetzen. Nur die Knoepfe im Hauptfenster setzen das: wer dort
    /// klickt, will genau diese Kopie erreichen -- und eine Verknuepfung auf einen
    /// verschobenen oder geloeschten Ordner ist ein toter Knopf auf dem Schreibtisch.
    /// Das Zustimmungsfenster ersetzt nie (siehe oben).
    /// </param>
    /// <param name="zielExe">Nur fuer den Selbsttest: eine andere Programmdatei.</param>
    public static bool Create(ShortcutPlace ort, out string? fehler,
                              string? ziel = null, bool ersetzen = false,
                              string? zielExe = null)
    {
        fehler = null;
        var wohin = ziel ?? PathFor(ort);
        try
        {
            var exe = zielExe ?? ExePath;
            if (File.Exists(wohin))
            {
                if (!ersetzen || SamePath(TargetOf(wohin), exe)) { return true; }
                File.Delete(wohin);
            }

            var ordner = System.IO.Path.GetDirectoryName(wohin);
            if (!string.IsNullOrEmpty(ordner) && !Directory.Exists(ordner))
            {
                Directory.CreateDirectory(ordner);
            }

            if (zielExe is null && !File.Exists(exe))
            {
                fehler = "die Programmdatei liegt nicht, wo sie erwartet wird";
                return false;
            }

            var typ = Type.GetTypeFromProgID("WScript.Shell");
            if (typ is null)
            {
                fehler = "WScript.Shell ist auf diesem Windows nicht verfuegbar";
                return false;
            }

            var shell = Activator.CreateInstance(typ);
            if (shell is null)
            {
                fehler = "WScript.Shell liess sich nicht erzeugen";
                return false;
            }

            try
            {
                var lnk = typ.InvokeMember(
                    "CreateShortcut", System.Reflection.BindingFlags.InvokeMethod,
                    null, shell, new object[] { wohin });
                if (lnk is null)
                {
                    fehler = "die Verknuepfung liess sich nicht anlegen";
                    return false;
                }

                var lnkTyp = lnk.GetType();
                void Setze(string feld, object wert) => lnkTyp.InvokeMember(
                    feld, System.Reflection.BindingFlags.SetProperty,
                    null, lnk, new[] { wert });

                Setze("TargetPath", exe);
                // Der Ordner UEBER app: dort liegen config und data, und wer die
                // Verknuepfung im Explorer "Dateipfad oeffnen" laesst, landet an der
                // Stelle, die er sucht. OHNE den abschliessenden Schraegstrich: mit
                // ihm lieferte GetParent den app-Ordner selbst statt den darueber.
                Setze("WorkingDirectory",
                      Directory.GetParent(AppContext.BaseDirectory.TrimEnd('\\', '/'))?.FullName
                      ?? AppContext.BaseDirectory);
                Setze("IconLocation", exe + ",0");
                Setze("Description", "Lap-time overlay and controller haptics "
                                     + "for Forza Horizon 6");

                lnkTyp.InvokeMember("Save",
                                    System.Reflection.BindingFlags.InvokeMethod,
                                    null, lnk, null);
                return File.Exists(wohin);
            }
            finally
            {
                // Ein COM-Objekt, das niemand freigibt, haelt den Host am Leben.
                if (System.Runtime.InteropServices.Marshal.IsComObject(shell))
                {
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
                }
            }
        }
        catch (Exception ausnahme)
        {
            fehler = ausnahme.Message;
            return false;
        }
    }
}
