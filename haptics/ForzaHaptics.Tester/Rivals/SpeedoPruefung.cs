using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// DEN DREHZAHLMESSER AN ECHTEN RUNDEN PRUEFEN (seit 2026-11-14): "--speedo-check &lt;ordner&gt; [anzahl]"
/// schickt die neuesten aufgezeichneten Spuren (*.tele.gz, jedes Paket) durch den Tacho, zweimal:
/// im ersten Durchgang lernt er, im zweiten wird gemessen. Je Auto und Tune: gelernter Begrenzer,
/// die hoechste gefahrene Drehzahl, und bei jedem Hochschalten des Fahrers, ob der Pfeil davor kam
/// und bei welcher Drehzahl. Gibt das Ergebnis auf der Konsole aus.
/// </summary>
internal static class SpeedoPruefung
{
    private sealed class Stand
    {
        public string Name = string.Empty;
        public int Pi;
        public float Max;
        public readonly List<float> Vollgas = new();
        public int Schaltungen;
        public int MitPfeil;
        public int AmBegrenzerGeschaltet;
        public readonly List<float> Abstand = new();
        public readonly List<float> PfeilRpm = new();
        public readonly List<float> SchaltRpm = new();
        public int Pakete;
        public int Begrenzerpakete;
        public readonly Dictionary<int, int> Fehlt = new();
    }

    public static int Pruefen(string ordner, int anzahl)
    {
        var dateien = new DirectoryInfo(ordner).EnumerateFiles("*" + TelemetryTrack.Suffix, SearchOption.AllDirectories)
                                               .OrderByDescending(f => f.LastWriteTimeUtc).Take(anzahl).Reverse().ToList();
        var zustand = new SpeedoState(merken: false);
        var design = new SpeedoDesign();
        var stände = new Dictionary<int, Stand>();
        var uhr = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var durchgang = 0; durchgang < 2; durchgang++)
        {
            foreach (var datei in dateien)
            {
                uhr = uhr.AddHours(1);
                Spur(datei.FullName, zustand, design, ref uhr, durchgang == 1 ? stände : null);
            }
        }
        var log = new StringBuilder();
        log.AppendLine(CultureInfo.InvariantCulture, $"{dateien.Count} Spuren, {stände.Count} Autos/Tunes");
        foreach (var (tune, s) in stände.OrderByDescending(e => e.Value.Pakete))
        {
            var lim = zustand.Begrenzer(tune, s.Max);
            s.Vollgas.Sort();
            var p99 = s.Vollgas.Count == 0 ? 0f : s.Vollgas[(int)(s.Vollgas.Count * 0.995)];
            log.AppendLine(CultureInfo.InvariantCulture,
                $"{s.Name} PI {s.Pi}: Skala {s.Max:0}, Begrenzer {(Math.Abs(lim - s.Max) < 1 ? "ungelernt" : lim.ToString("0", CultureInfo.InvariantCulture))}, " +
                $"Vollgas-Spitze {p99:0}, am Begrenzer {100.0 * s.Begrenzerpakete / Math.Max(1, s.Pakete):0.0} % der Pakete");
            var punkte = zustand.Schaltpunkte(tune, s.Max);
            if (punkte.Count > 0)
            {
                log.AppendLine("    beste Schaltpunkte: " + string.Join(", ", punkte.Select(e =>
                    $"{e.Key}->{e.Key + 1} {(e.Value > 0 ? e.Value.ToString("0", CultureInfo.InvariantCulture) : "?")}")));
            }
            if (s.Schaltungen > 0)
            {
                s.Abstand.Sort();
                s.PfeilRpm.Sort();
                s.SchaltRpm.Sort();
                var med = s.Abstand.Count == 0 ? 0f : s.Abstand[s.Abstand.Count / 2];
                log.AppendLine(CultureInfo.InvariantCulture,
                    $"    {s.Schaltungen} Hochschaltungen unter Gas, Pfeil davor bei {s.MitPfeil} ({100.0 * s.MitPfeil / s.Schaltungen:0} %), " +
                    $"davon am Begrenzer geschaltet {s.AmBegrenzerGeschaltet}; Pfeil kam im Median {med:0.00} s vorher, " +
                    $"bei {(s.PfeilRpm.Count == 0 ? 0 : s.PfeilRpm[s.PfeilRpm.Count / 2]):0} U/min, geschaltet bei {s.SchaltRpm[s.SchaltRpm.Count / 2]:0}");
                if (s.Fehlt.Count > 0) { log.AppendLine("    ohne Pfeil (Gang: Anzahl): " + string.Join(", ", s.Fehlt.OrderBy(e => e.Key).Select(e => $"{e.Key}: {e.Value}"))); }
            }
        }
        Console.Out.Write(log.ToString());
        return 0;
    }

    private static void Spur(string pfad, SpeedoState zustand, SpeedoDesign design, ref DateTime uhr, Dictionary<int, Stand>? stände)
    {
        JsonDocument doc;
        try
        {
            using var gz = new GZipStream(File.OpenRead(pfad), CompressionMode.Decompress);
            doc = JsonDocument.Parse(gz);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException)
        {
            return;
        }
        using (doc)
        {
            var kopf = doc.RootElement;
            if (!kopf.TryGetProperty("columns", out var spalten) || !kopf.TryGetProperty("data", out var daten)) { return; }
            var name = kopf.TryGetProperty("car", out var car) && car.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?";
            var felder = ForzaPacket.Descriptors.ToDictionary(d => d.Key, StringComparer.Ordinal);
            var ziel = spalten.EnumerateArray().Select(c => felder.TryGetValue(c.GetString() ?? string.Empty, out var d) ? d : null).ToList();
            var tIndex = spalten.EnumerateArray().Select(c => c.GetString()).ToList().IndexOf("t");
            var start = uhr;
            var verlauf = new Queue<(DateTime Zeit, int Rat, float Rpm, float Gas)>();
            var gangVorher = 0;
            foreach (var reihe in daten.EnumerateArray())
            {
                var roh = new byte[324];
                var i = 0;
                foreach (var wert in reihe.EnumerateArray())
                {
                    var d = ziel[i++];
                    if (d is null || wert.ValueKind != JsonValueKind.Number) { continue; }
                    var w = wert.GetDouble();
                    var stelle = roh.AsSpan(d.Offset);
                    switch (d.Type)
                    {
                        case TelemetryValueType.Signed32: BitConverter.TryWriteBytes(stelle, (int)w); break;
                        case TelemetryValueType.Unsigned32: BitConverter.TryWriteBytes(stelle, (uint)w); break;
                        case TelemetryValueType.Unsigned16: BitConverter.TryWriteBytes(stelle, (ushort)w); break;
                        case TelemetryValueType.Unsigned8: roh[d.Offset] = (byte)w; break;
                        case TelemetryValueType.Signed8: roh[d.Offset] = unchecked((byte)(sbyte)w); break;
                        default: BitConverter.TryWriteBytes(stelle, (float)w); break;
                    }
                }
                if (!ForzaPacket.TryParse(roh, out var p)) { continue; }
                var t = tIndex >= 0 ? reihe[tIndex].GetDouble() : 0.0;
                var jetzt = start.AddSeconds(Math.Max(0.0, t));
                uhr = jetzt;
                var bild = zustand.Aufnehmen(p, 0f, design, SpeedoSchwellen.Standard, jetzt);
                if (stände is null) { continue; }
                var tune = zustand.Tune;
                if (tune == 0) { continue; }
                if (!stände.TryGetValue(tune, out var s))
                {
                    stände[tune] = s = new Stand { Name = name, Pi = (int)p.Get("CarPerformanceIndex"), Max = (float)p.Get("EngineMaxRpm") };
                }
                var gas = (float)p.Get("Accel") / 255f;
                var kupplung = (float)p.Get("Clutch") / 255f;
                s.Pakete++;
                if (bild.AtLimiter) { s.Begrenzerpakete++; }
                if (gas > 0.9f && kupplung < 0.1f && bild.Gear is >= 1 and <= 10) { s.Vollgas.Add((float)p.Get("CurrentEngineRpm")); }
                verlauf.Enqueue((jetzt, bild.Rat, (float)p.Get("CurrentEngineRpm"), gas));
                while (verlauf.Count > 0 && (jetzt - verlauf.Peek().Zeit).TotalSeconds > 1.5) { verlauf.Dequeue(); }
                if (gangVorher is >= 1 and <= 9 && bild.Gear == gangVorher + 1)
                {
                    // Ein Hochschalten unter Gas: die Drehzahl kurz davor, und ob der Pfeil davor kam.
                    var davor = verlauf.Where(v => (jetzt - v.Zeit).TotalSeconds is > 0.05 and <= 1.5).ToList();
                    var unterGas = davor.Where(v => (jetzt - v.Zeit).TotalSeconds <= 0.4).Any(v => v.Gas > 0.8f);
                    if (unterGas && davor.Count > 0)
                    {
                        s.Schaltungen++;
                        var vonGang = bild.Gear - 1;
                        var spitze = davor.Max(v => v.Rpm);
                        s.SchaltRpm.Add(spitze);
                        if (spitze >= zustand.Begrenzer(tune, s.Max) * 0.975f) { s.AmBegrenzerGeschaltet++; }
                        var erster = davor.FirstOrDefault(v => v.Rat == 1);
                        if (erster.Rat != 1) { s.Fehlt[vonGang] = s.Fehlt.GetValueOrDefault(vonGang) + 1; }
                        if (erster.Rat == 1)
                        {
                            s.MitPfeil++;
                            s.Abstand.Add((float)(jetzt - erster.Zeit).TotalSeconds);
                            s.PfeilRpm.Add(erster.Rpm);
                        }
                    }
                }
                if (bild.Gear is >= 1 and <= 10) { gangVorher = bild.Gear; }  // Leerlauf (11) beim Schalten ueberspringen
            }
        }
    }
}
