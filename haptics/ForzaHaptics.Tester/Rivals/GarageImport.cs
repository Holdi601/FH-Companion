namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Die ganze Garage aus dem Speicher des laufenden Spiels -- fuer "Car notes" und
/// "Car collection" derselbe Weg.
/// </summary>
/// <remarks>
/// Derselbe Weg wie im Reiter "Tuning inspector" (<see cref="Tuning.ForzaMemoryDb"/>):
/// die Garage liegt als SQLite-Datenbank im Speicher, eine Datei auf der Platte gibt
/// es nicht. Nur auf Knopfdruck: das Durchsuchen des Speichers kostet Sekunden und
/// gehoert in ein Menue, nicht in ein Rennen.
///
/// Das Ergebnis landet an BEIDEN Stellen -- in den Notizen und in der Liste der
/// eigenen Autos. Zwei Knoepfe, die je nur ihren Reiter fuellten, liessen den anderen
/// mit einer Garage von vorgestern stehen.
/// </remarks>
internal static class GarageImport
{
    /// <summary>Blockiert Sekunden lang -- nur im Hintergrund aufrufen.</summary>
    public static (List<int> Ids, string? Fehler) Lesen(Action<string> fortschritt)
    {
        var ablage = Path.Combine(AppInfo.TempFolder, "garage");
        string? fund = null;
        var meldung = string.Empty;
        try
        {
            var funde = Tuning.ForzaMemoryDb.Dump(
                ablage,
                fortschritt,
                pfad =>
                {
                    if (Tuning.GarageReader.FindGarage(new[] { pfad }) is null) { return false; }
                    fund = pfad;
                    return true;
                });
            fund ??= Tuning.GarageReader.FindGarage(funde.Select(f => f.Path));
        }
        catch (Exception fehler)
        {
            meldung = fehler.Message;
        }
        var autos = new List<int>();
        if (fund is not null)
        {
            try { autos = Tuning.GarageReader.Cars(fund).Distinct().ToList(); } catch (Exception) { }
        }
        if (autos.Count > 0) { return (autos, null); }
        return (autos, meldung.Length > 0
            ? meldung
            : Loc.T("No garage found in the game's memory -- open My Cars once, then try again."));
    }

    /// <summary>Im UI-Faden: in die Notizen und in die eigenen Autos. Gibt zurueck, wie viele neu in den Notizen sind.</summary>
    public static int Merken(IReadOnlyList<int> ids, CarNotes? notizen, RivalsAdvisor? rat, string? besitzPfad = null)
    {
        var besitz = OwnedCars.Laden(besitzPfad);
        besitz.GarageMerken(ids);
        besitz.Speichern();
        GarageGelesen?.Invoke();
        if (notizen is null) { return 0; }
        var mitNamen = ids.Select(id => (id, rat?.CarIndexForId(id) is { } ix ? rat.RealCarName(ix) : null)).ToList();
        return notizen.NoteModels(mitNamen, "garage");
    }

    /// <summary>Nach jedem Lesen: der andere Reiter baut sich neu.</summary>
    public static event Action? GarageGelesen;
}
