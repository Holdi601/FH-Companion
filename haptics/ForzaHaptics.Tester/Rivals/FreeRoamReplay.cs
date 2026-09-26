using System.Text.Json;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Eine abgelegte Runde noch einmal durch die Uhr der freien Welt schicken.
/// </summary>
/// <remarks>
/// ## Wozu
///
/// Die Pruefung im Selbsttest faehrt einen perfekten Kreis: gleichmaessiges Tempo,
/// gleichmaessige Schritte, die Linie exakt auf der Bahn. Damit laesst sich zeigen,
/// dass die Rechnung stimmt -- aber nicht, dass sie mit dem zurechtkommt, was das
/// Spiel wirklich schickt: schwankendes Tempo, Kurven, Spruenge zwischen zwei
/// Paketen, eine Fahrlinie, die bei jeder Runde ein paar Meter anders liegt.
///
/// Dafuer gibt es schon Material -- jede aufgezeichnete Runde bringt ihre Messpunkte
/// mit, alle 5 m einen, mit Weltkoordinaten und Zeiten. Dieser Befehl macht daraus
/// wieder Telemetrie, setzt eine Start-Ziel-Linie an ihren Anfang und misst sie neu.
/// Kommt dieselbe Laenge und dieselbe Zeit heraus, taugt die Uhr auf echten Daten.
///
///     "FH Companion.exe" --free-roam-replay &lt;datei.json&gt;
///     "FH Companion.exe" --free-roam-replay &lt;ordner&gt;
///
/// Ein Ordner nimmt jede Runde darin und fasst zusammen. Rueckgabe 0, wenn jede
/// gemessene Runde innerhalb der Toleranz liegt.
/// </remarks>
internal static class FreeRoamReplay
{
    /// <summary>Wie weit Laenge und Zeit abweichen duerfen, in Prozent.</summary>
    private const double Toleranz = 0.02;

    public static int Run(string[] args)
    {
        var i = Array.FindIndex(args, a =>
            string.Equals(a, "--free-roam-replay", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= args.Length)
        {
            Console.Error.WriteLine("Bitte eine Rundendatei oder einen Ordner angeben.");
            return 2;
        }

        var ziel = args[i + 1];
        var dateien = Directory.Exists(ziel)
            ? Directory.EnumerateFiles(ziel, "*.json", SearchOption.AllDirectories)
                .Where(f => !string.Equals(Path.GetFileName(f), "course.json",
                                           StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f).ToList()
            : new List<string> { ziel };

        if (dateien.Count == 0)
        {
            Console.Error.WriteLine($"Keine Rundendateien unter {ziel}.");
            return 2;
        }

        int geprueft = 0, daneben = 0, ohne = 0;
        foreach (var datei in dateien)
        {
            var lap = Lies(datei);
            if (lap is null || lap.Samples.Count < 20) { ohne++; continue; }

            var (laenge, sekunden, runden) = Messe(lap);
            if (runden == 0)
            {
                Console.WriteLine($"{Path.GetFileName(datei),-46}  keine Runde erkannt");
                ohne++;
                continue;
            }

            var sollLaenge = lap.LengthMetres;
            var sollZeit = lap.LapSeconds;
            var dl = sollLaenge > 0 ? Math.Abs(laenge - sollLaenge) / sollLaenge : 1.0;
            var dz = sollZeit > 0 ? Math.Abs(sekunden - sollZeit) / sollZeit : 1.0;
            var ok = dl <= Toleranz && dz <= Toleranz;
            geprueft++;
            if (!ok) { daneben++; }

            Console.WriteLine(
                $"{Path.GetFileName(datei),-46}  {sollLaenge,7:0} m / {sollZeit,7:0.000} s"
                + $"  ->  {laenge,7:0} m / {sekunden,7:0.000} s"
                + $"  ({dl * 100,4:0.0}% / {dz * 100,4:0.0}%){(ok ? "" : "   ABWEICHUNG")}");
        }

        Console.WriteLine();
        Console.WriteLine($"{geprueft} Runde(n) nachgemessen, {daneben} ausserhalb von "
                          + $"{Toleranz * 100:0}%, {ohne} ohne verwertbare Messpunkte.");
        return daneben == 0 ? 0 : 1;
    }

    private static RecordedLap? Lies(string datei)
    {
        try
        {
            var text = File.ReadAllText(datei);
            var abgelegt = JsonSerializer.Deserialize<LapArchive.ArchivedLap>(
                text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (abgelegt?.Lap is not null) { return abgelegt.Lap; }
            return JsonSerializer.Deserialize<RecordedLap>(
                text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Die Runde dreimal hintereinander abspielen und messen, was dabei herauskommt.
    /// </summary>
    /// <remarks>
    /// DREIMAL, weil jede Ueberfahrt ihre Aufgabe hat. Die erste faellt noch in die
    /// Zeit, in der das freie Fahren gar nicht erkannt ist -- dafuer braucht es
    /// 150 m stehende Spieluhr. Die zweite startet die Uhr, die dritte haelt sie an.
    /// </remarks>
    private static (float Laenge, float Sekunden, int Runden) Messe(RecordedLap lap)
    {
        var aufnahme = new LapRecorder();
        var linien = new FreeRoamTimer();
        // Die Linie an den Anfang der Runde -- dorthin, wo sie auch im Bestand liegt.
        var startX = lap.HasStart ? lap.StartX : lap.Samples[0].X;
        var startZ = lap.HasStart ? lap.StartZ : lap.Samples[0].Z;
        linien.Add(new FreeRoamAnchor { Name = "replay", X = startX, Z = startZ });
        aufnahme.Lines = linien;

        var fertige = new List<RecordedLap>();
        aufnahme.LapCompleted += (_, fertig) => fertige.Add(fertig);

        var versatz = 0f;
        for (var durchgang = 0; durchgang < 3; durchgang++)
        {
            foreach (var s in lap.Samples)
            {
                var paket = Paket(lap, s, versatz + s.Seconds);
                if (ForzaPacket.TryParse(paket, out var gelesen))
                {
                    aufnahme.OnTelemetry(gelesen);
                }
            }
            // Ein wenig Abstand zwischen den Durchgaengen, damit die Zeiten steigen.
            versatz += lap.Samples[^1].Seconds + 0.1f;
        }

        if (fertige.Count == 0) { return (0f, 0f, 0); }
        var letzte = fertige[^1];
        return (letzte.LengthMetres, letzte.LapSeconds, fertige.Count);
    }

    /// <summary>Aus einem Messpunkt wieder ein Telemetriepaket machen.</summary>
    private static byte[] Paket(RecordedLap lap, LapSample s, float sekunden)
    {
        var p = new byte[324];
        BitConverter.GetBytes(1).CopyTo(p, 0);                       // IsRaceOn
        BitConverter.GetBytes((uint)(sekunden * 1000f)).CopyTo(p, 4); // TimestampMS
        BitConverter.GetBytes((float)Math.Max(lap.MaxRpm, 1000)).CopyTo(p, 8);
        BitConverter.GetBytes((float)Math.Max(lap.IdleRpm, 500)).CopyTo(p, 12);
        BitConverter.GetBytes(lap.Drivetrain).CopyTo(p, 224);
        BitConverter.GetBytes(lap.Cylinders).CopyTo(p, 228);
        BitConverter.GetBytes(lap.CarOrdinal).CopyTo(p, 212);
        BitConverter.GetBytes(lap.CarClass).CopyTo(p, 216);
        BitConverter.GetBytes(lap.PerformanceIndex).CopyTo(p, 220);
        BitConverter.GetBytes(s.X).CopyTo(p, 244);
        BitConverter.GetBytes(s.Y).CopyTo(p, 248);
        BitConverter.GetBytes(s.Z).CopyTo(p, 252);
        BitConverter.GetBytes(s.Speed).CopyTo(p, 256);
        // Die SPIELUHR bleibt auf null -- genau das macht es zur freien Fahrt.
        BitConverter.GetBytes(0f).CopyTo(p, 304);
        BitConverter.GetBytes((ushort)0).CopyTo(p, 312);
        return p;
    }
}
