namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Den Rundenbestand auf die schnellste Runde je Auto, Strecke und Klasse kuerzen -- nur auf Knopfdruck.
/// </summary>
/// <remarks>
/// Seit 2026-10-01, auf Wunsch des Nutzers: die App legt JEDE Runde ab (der Schalter dafuer
/// ist weg), und wer Platz will, raeumt im Reiter "My times" auf.
///
/// Eine Gruppe ist, was die eigenen Rekorde vergleichen (<see cref="PersonalRecords.Vergleichbar"/>),
/// und dazu das Auto: Kurs, Klasse, Auto, stehend oder fliegend, Runde oder Sprint, Modus.
/// Stehend und fliegend zusammenzuwerfen loeschte die stehende Bestzeit -- und mit ihr den
/// Vergleich, gegen den jeder stehende Start faehrt. Ebenso wuerde eine Horizon-Play-Runde
/// den Rivals-Rekord mitnehmen. Tune und Etikett zaehlen dagegen nicht: "je Auto" hat der
/// Nutzer gesagt.
///
/// Abgebrochene Laeufe (<see cref="LapArchive.UnfertigOrdner"/>) sind nie eine Bestzeit und
/// gehen mit. Vorher wird die Rennstatistik nachgezogen: sie baut aeltere Rennen aus den
/// Sprint-Runden nach und merkt sie sich -- was sie bis dahin nicht gesehen hat, ginge mit
/// der Runde verloren.
/// </remarks>
internal static class LapCleanup
{
    internal sealed record Plan(IReadOnlyList<string> Langsamere, IReadOnlyList<string> Unfertige,
                                int Behalten, long Bytes)
    {
        public int Dateien => Langsamere.Count + Unfertige.Count;
    }

    internal sealed record Ergebnis(int Geloescht, int Fehlgeschlagen, long Bytes);

    /// <summary>Was weg kann. Liest jede Runde kurz an (der Modus steht am Ende) -- nicht im UI-Faden rufen.</summary>
    public static Plan Planen(string wurzel)
    {
        var weg = new List<string>();
        var behalten = 0;
        foreach (var gruppe in OwnTimes.Einlesen(wurzel)
                     .GroupBy(l => (l.Course, l.Klass, l.Ordinal, l.Standing, l.Sprint, l.Mode)))
        {
            // Gleich schnell: die fruehere bleibt -- sie war zuerst die Bestzeit.
            var beste = gruppe.OrderBy(l => l.Seconds).ThenBy(l => l.When).First();
            behalten++;
            weg.AddRange(gruppe.Where(l => !ReferenceEquals(l, beste)).Select(l => l.Path));
        }

        var unfertig = new List<string>();
        var ordner = Path.Combine(wurzel, LapArchive.UnfertigOrdner);
        if (Directory.Exists(ordner))
        {
            unfertig.AddRange(Directory.EnumerateFiles(ordner, "*.json", SearchOption.AllDirectories)
                .Where(d => !Path.GetFileName(d).Equals("course.json", StringComparison.OrdinalIgnoreCase)));
        }

        long bytes = 0;
        foreach (var d in weg.Concat(unfertig))
        {
            bytes += DateiGroesse(d) + DateiGroesse(d + TelemetryTrack.Suffix);
        }
        return new Plan(weg, unfertig, behalten, bytes);
    }

    /// <summary>Den Plan ausfuehren: erst die Rennstatistik sichern, dann loeschen, dann leere Ordner weg.</summary>
    /// <param name="rennstand">races_archive.json; null laesst die Rennstatistik aus.</param>
    /// <param name="live">Die beim Fahren aufgezeichneten Rennen; null liest sie aus races.jsonl.</param>
    public static Ergebnis Ausfuehren(string wurzel, Plan plan, string? rennstand, ISet<string>? live = null)
    {
        if (rennstand is not null)
        {
            live ??= new HashSet<string>(RaceLog.LoadLive().Select(r => r.Id), StringComparer.Ordinal);
            RaceArchive.Update(wurzel, rennstand, live);
        }

        int geloescht = 0, fehl = 0;
        long bytes = 0;
        var ordner = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var datei in plan.Langsamere.Concat(plan.Unfertige))
        {
            // Die Runde zuerst: laesst sie sich nicht loeschen, bleibt sie samt Spur ganz.
            if (!Loeschen(datei, ref bytes)) { fehl++; continue; }
            geloescht++;
            if (!Loeschen(datei + TelemetryTrack.Suffix, ref bytes)) { fehl++; }
            if (Path.GetDirectoryName(datei) is { } o) { ordner.Add(o); }
        }
        foreach (var o in ordner.OrderByDescending(o => o.Length)) { LeereEntfernen(o, wurzel); }

        OwnTimes.Vergessen();
        LapArchive.ReferenzenVergessen();
        return new Ergebnis(geloescht, fehl, bytes);
    }

    /// <summary>"850 MB", "1,4 GB" -- in der Schreibweise des Systems.</summary>
    public static string Anzeige(long bytes)
    {
        const double MB = 1024d * 1024d;
        if (bytes >= 1024 * MB) { return (bytes / (1024 * MB)).ToString("0.0") + " GB"; }
        return Math.Max(1, Math.Round(bytes / MB)).ToString("0") + " MB";
    }

    private static bool Loeschen(string pfad, ref long bytes)
    {
        try
        {
            if (!File.Exists(pfad)) { return true; }
            var n = new FileInfo(pfad).Length;
            File.Delete(pfad);
            bytes += n;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static long DateiGroesse(string pfad)
    {
        try { return File.Exists(pfad) ? new FileInfo(pfad).Length : 0; }
        catch (Exception) { return 0; }
    }

    /// <summary>Leer gewordene Ordner nach oben hin entfernen, nie die Wurzel selbst.</summary>
    private static void LeereEntfernen(string ordner, string wurzel)
    {
        var oben = Path.GetFullPath(wurzel).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var o = Path.GetFullPath(ordner);
        while (o.Length > oben.Length
               && o.StartsWith(oben + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(o).Any()) { return; }
                Directory.Delete(o);
            }
            catch (Exception)
            {
                return;
            }
            o = Path.GetDirectoryName(o)!;
        }
    }
}
