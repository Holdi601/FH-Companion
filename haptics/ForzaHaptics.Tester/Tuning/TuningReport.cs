namespace ForzaHaptics.Tester.Tuning;

/// <summary>
/// Den Tuning-Bericht auf der Kommandozeile ausgeben.
/// </summary>
/// <remarks>
/// ## Wozu, wenn es den Reiter gibt
///
/// Weil ein Reiter sich nur pruefen laesst, wenn das Spiel laeuft -- und weil ein
/// Bau, der nur im Fenster geprueft wurde, genau der Bau ist, der beim Nutzer
/// scheitert. Dieser Befehl liest dieselben Klassen gegen eine Datenbank auf der
/// Platte, also gegen einen frueheren Speicherabzug. Damit ist der Leseweg pruefbar,
/// ohne Forza, ohne VM, ohne Fenster.
///
///     "FH Companion.exe" --tuning-report &lt;datei.db&gt; [CarId]
///     "FH Companion.exe" --tuning-report --from-memory [CarId]
///
/// Ohne CarId wird das Auto mit den meisten gekauften Teilen genommen -- das ist das
/// interessanteste und zeigt am ehesten, ob die Spalten stimmen.
/// </remarks>
internal static class TuningReport
{
    public static int Run(string[] args)
    {
        var i = Array.FindIndex(args, a =>
            string.Equals(a, "--tuning-report", StringComparison.OrdinalIgnoreCase));
        var rest = args.Skip(i + 1).Where(a => !a.StartsWith("--")).ToList();
        var ausSpeicher = args.Any(a =>
            string.Equals(a, "--from-memory", StringComparison.OrdinalIgnoreCase));

        string? db;
        if (ausSpeicher)
        {
            var ablage = Path.Combine(AppInfo.TempFolder, "garage");
            string? frueh = null;
            var funde = ForzaMemoryDb.Dump(
                ablage, m => Console.WriteLine("  " + m),
                pfad =>
                {
                    if (GarageReader.FindGarage(new[] { pfad }) is null) { return false; }
                    frueh = pfad;
                    return true;
                });
            db = frueh ?? GarageReader.FindGarage(funde.Select(f => f.Path));
        }
        else
        {
            db = rest.FirstOrDefault(f => File.Exists(f));
            if (db is null)
            {
                Console.Error.WriteLine("Bitte eine Datenbankdatei angeben "
                                        + "(oder --from-memory).");
                return 2;
            }
            db = GarageReader.FindGarage(new[] { db });
        }

        if (db is null)
        {
            Console.Error.WriteLine("Keine brauchbare Garage gefunden.");
            return 1;
        }

        var autos = GarageReader.Cars(db);
        Console.WriteLine($"Garage: {autos.Count} Auto(s) in {db}");

        var gewuenscht = rest.Select(a => int.TryParse(a, out var n) ? n : 0)
                             .FirstOrDefault(n => n > 0);
        var carId = gewuenscht > 0 ? gewuenscht : Reichstes(db, autos);
        var tune = GarageReader.Read(db, carId);
        if (tune is null)
        {
            Console.Error.WriteLine($"Auto {carId} steht nicht in dieser Garage.");
            return 1;
        }

        Console.WriteLine();
        // Kein PI: die Garagenspalte ist nicht der angezeigte Wert (siehe
        // GarageReader), und auf der Kommandozeile gibt es keine Telemetrie.
        Console.WriteLine($"Auto {tune.CarId}  Klasse {tune.ClassName}  "
                          + $"Teile fuer {tune.PartsValue:N0} CR  "
                          + $"(gespeicherter Index {tune.StoredIndex}, NICHT der PI)");
        if (!string.IsNullOrWhiteSpace(tune.TuneFileName))
        {
            Console.WriteLine($"Tune \"{tune.TuneFileName}\"  id {tune.VersionedTuneId}  "
                              + $"Ersteller-XUID {tune.VersionedTuneXuid}");
        }
        Console.WriteLine($"Erstbesitzer {tune.OriginalOwner ?? "-"}");

        Console.WriteLine();
        Console.WriteLine("TEILE  (Stufe 0 = Serie)");
        foreach (var t in tune.Parts)
        {
            var hinweis = "  " + t.Erklaerung;
            if (t.PricePaid is { } p) { hinweis += $", gekauft {p:N0} CR"; }
            Console.WriteLine($"  {t.Area,-14} {t.Label,-28} {t.Id,-9} Stufe {t.Step,-4}"
                              + hinweis);
        }

        Console.WriteLine();
        Console.WriteLine("REGLER  (Position 0..1, nicht der Anzeigewert)");
        foreach (var s in tune.Settings)
        {
            Console.WriteLine($"  {s.Area,-14} {s.Label,-28} "
                              + (s.Slider < 0 ? "--  (an diesem Auto nicht vorhanden)"
                                              : s.Slider.ToString("0.000")));
        }

        Console.WriteLine();
        Console.WriteLine($"{tune.Parts.Count} Teile, {tune.Settings.Count} Regler.");
        return 0;
    }

    private static int Reichstes(string db, List<int> autos)
    {
        var bestes = autos.FirstOrDefault();
        long meiste = -1;
        foreach (var a in autos)
        {
            try
            {
                var t = GarageReader.Read(db, a);
                if (t is not null && t.PartsValue > meiste)
                {
                    meiste = t.PartsValue;
                    bestes = a;
                }
            }
            catch (Exception)
            {
                // Ein unlesbares Auto ist kein Grund, den Bericht aufzugeben.
            }
        }
        return bestes;
    }
}
