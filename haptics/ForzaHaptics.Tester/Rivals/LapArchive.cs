using System.Text.Json;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Jede gefahrene Runde als eigene Datei, nach Strecke, Klasse, Auto, Abstimmung
/// und Merkzettel einsortiert.
/// </summary>
/// <remarks>
/// Getrennt von <see cref="LapLibrary"/>, und das ist der Punkt: die Bibliothek
/// behaelt je Auswahl nur die SCHNELLSTE Runde, weil ein Delta gegen die eigene
/// Bestzeit misst. Eine Karte will das Gegenteil -- alle Runden, auch die
/// misslungenen, denn die zeigen erst, wo es schiefgeht.
///
/// Die Ordnerstruktur ist die Auswertung: wer die Bremspunkte eines Autos in einer
/// Abstimmung sehen will, nimmt einen Ordner. Ohne sie laege alles in einem Topf und
/// muesste bei jeder Frage neu gefiltert werden.
///
/// Die Strecke traegt keinen Namen -- die Telemetrie nennt keinen. Der Ordner heisst
/// darum nach dem Ort der Start-Ziel-Linie; in ihm liegt ein <c>course.json</c> mit
/// einem leeren Namensfeld, das sich von Hand ausfuellen laesst.
/// </remarks>
internal static class LapArchive
{
    private static readonly JsonSerializerOptions Format = new() { WriteIndented = false };

    public static string Root => Path.Combine(AppInfo.DataFolder, "laps");

    /// <summary>
    /// Die Leistungsklasse aus dem PI.
    /// </summary>
    /// <remarks>
    /// BERICHTIGT am 2026-09-14: hier stand "bis 500 ist D", und damit lag JEDE
    /// Klasse eine Stufe zu niedrig. Richtig ist D bis 400, C bis 500, B bis 600,
    /// A bis 700, S1 bis 800, S2 bis 900, R bis 998 -- so steht es auf der
    /// Klassenleiste des Spiels, so sagt es der Nutzer, und so ergibt es sich
    /// unabhaengig aus der Garagen-Datenbank.
    ///
    /// Die alte Anmerkung sagte, die Bedeutung des Feldes <c>CarClass</c> sei nicht
    /// belegt ("PI 800 kam als 4, PI 900 als 5 -- welche Klasse das sein soll, sagt
    /// niemand"). Das ist inzwischen belegt: <c>ClassID</c> in <c>Career_Garage</c>
    /// IST dasselbe Feld, und ueber die eigenen gefahrenen Autos geeicht ergibt sich
    /// 0..6 = D, C, B, A, S1, S2, R. 4 ist also S1 und 5 ist S2.
    ///
    /// ACHTUNG BEIM BESTAND: Runden, die vor dieser Berichtigung abgelegt wurden,
    /// liegen in einem um eine Stufe zu niedrigen Klassenordner.
    /// <c>scripts/fix_lap_classes.py</c> zieht sie um.
    /// </remarks>
    public static string ClassOf(int performanceIndex) => performanceIndex switch
    {
        <= 0 => "unknown",
        <= 400 => "D",
        <= 500 => "C",
        <= 600 => "B",
        <= 700 => "A",
        <= 800 => "S1",
        <= 900 => "S2",
        _ => "R",
    };

    /// <summary>Der Ordnername einer Strecke: wo ihre Start-Ziel-Linie liegt.</summary>
    /// <remarks>
    /// ## Warum hier gesucht und nicht gerundet wird
    ///
    /// Bis zum 2026-09-13 stand hier eine Rundung auf 25 m. Das ist eine
    /// naheliegende Idee und sie ist falsch, weil eine Rundung GRENZEN hat: zwei
    /// Ueberfahrten wenige Meter auseinander koennen auf verschiedene Seiten einer
    /// solchen Grenze fallen und bekommen dann verschiedene Ordner.
    ///
    /// Im eigenen Bestand war das messbar -- 44 Ordner, davon 14 Paare naeher als
    /// 250 m beieinander:
    ///
    ///     course_1300_275   &lt;-&gt; course_1325_275     2,6 m
    ///     course_2775_5000  &lt;-&gt; course_2800_5000    5,9 m
    ///     course_-1675_-4425 &lt;-&gt; course_-1675_-4450  8,8 m
    ///
    /// Dieselbe Strecke, zwei Ordner, je eine Handvoll Runden darin. Fuer eine
    /// Karte ist das der schlimmste Fall: die Daten sind da, aber verteilt, und
    /// nichts weist darauf hin.
    ///
    /// ## Was stattdessen geschieht
    ///
    /// Es wird nachgesehen, ob es schon einen Ordner fuer diese Strecke GIBT --
    /// anhand der Startpunkte, die in den <c>course.json</c> stehen. Nur wenn
    /// keiner passt, entsteht ein neuer. Ein Ordner behaelt damit seinen Namen fuer
    /// immer, auch wenn spaetere Ueberfahrten ein paar Meter daneben liegen.
    ///
    /// ## Die Schwelle ist dieselbe wie beim Delta
    ///
    /// <see cref="RecordedLap.StartTolerance"/>, also 120 m. Absichtlich derselbe
    /// Wert: was das Delta als "dieselbe Strecke" behandelt, soll auch im selben
    /// Ordner liegen. Zwei Massstaebe fuer dieselbe Frage waeren zwei Antworten.
    /// </remarks>
    /// <summary>Wo eine Runde endet -- der letzte Messpunkt.</summary>
    /// <remarks>
    /// Aus den Messpunkten und nicht aus einem eigenen Feld: der Recorder schreibt
    /// den Weg ohnehin mit, und ein zweites Feld waere eine zweite Wahrheit, die
    /// auseinanderlaufen kann.
    /// </remarks>
    public static (float X, float Z)? Finish(RecordedLap lap)
    {
        if (lap.Samples.Count == 0) { return null; }
        var letzte = lap.Samples[^1];
        return (letzte.X, letzte.Z);
    }

    public static string CourseKey(RecordedLap lap, string? root = null)
    {
        if (!lap.HasStart) { return "course_unknown"; }
        var vorhanden = FindCourseFolder(root ?? Root, lap);
        if (vorhanden is not null) { return vorhanden; }
        var x = (int)MathF.Round(lap.StartX / 25f) * 25;
        var z = (int)MathF.Round(lap.StartZ / 25f) * 25;
        // Das Ziel gehoert in den Namen, nicht nur in die Notiz: zwei Strecken mit
        // derselben Startlinie sollen im Dateibaum auch verschieden HEISSEN, sonst
        // sieht man beim Durchsehen nicht, dass es zwei sind.
        var ziel = Finish(lap);
        if (ziel is null) { return $"course_{x}_{z}"; }
        var zx = (int)MathF.Round(ziel.Value.X / 25f) * 25;
        var zz = (int)MathF.Round(ziel.Value.Z / 25f) * 25;
        return $"course_{x}_{z}_to_{zx}_{zz}";
    }

    /// <summary>Der Ordner einer schon bekannten Strecke, oder null.</summary>
    /// <remarks>
    /// Der NAECHSTE passende, nicht der erste: liegen ausnahmsweise zwei bekannte
    /// Startlinien innerhalb der Schwelle, gehoert die Runde zu der, der sie naeher
    /// liegt. "Der erste, den ich finde" haenge sonst von der Sortierung des
    /// Dateisystems ab -- und die ist kein Argument.
    /// </remarks>
    private static string? FindCourseFolder(string wurzel, RecordedLap lap)
    {
        try
        {
            if (!Directory.Exists(wurzel)) { return null; }
            string? beste = null;
            var besteEntfernung = float.MaxValue;
            foreach (var ordner in Directory.EnumerateDirectories(wurzel, "course_*"))
            {
                var notiz = Path.Combine(ordner, "course.json");
                if (!File.Exists(notiz)) { continue; }
                CourseNote? gelesen;
                try
                {
                    gelesen = JsonSerializer.Deserialize<CourseNote>(File.ReadAllText(notiz));
                }
                catch (Exception)
                {
                    continue;
                }
                if (gelesen is null || (gelesen.StartX == 0f && gelesen.StartZ == 0f))
                {
                    continue;
                }
                var dx = gelesen.StartX - lap.StartX;
                var dz = gelesen.StartZ - lap.StartZ;
                var entfernung = MathF.Sqrt(dx * dx + dz * dz);
                if (entfernung > RecordedLap.StartTolerance
                    || entfernung >= besteEntfernung)
                {
                    continue;
                }

                // UND DAS ZIEL MUSS AUCH PASSEN.
                //
                // Derselbe Massstab wie fuer den Start, absichtlich: was das Delta
                // als dieselbe Strecke behandelt, soll auch im selben Ordner liegen,
                // und zwei Massstaebe fuer dieselbe Frage waeren zwei Antworten.
                //
                // Ein Ordner OHNE Ziel in der Notiz stammt aus der Zeit vor dem
                // 2026-09-15. Der passt weiterhin auf den Start allein -- sonst
                // bekaeme jede neue Runde einen neuen Ordner, waehrend der alte
                // danebenliegt. `scripts/fix_course_keys.py` traegt die Ziele nach.
                var ziel = Finish(lap);
                if (ziel is not null
                    && (gelesen.FinishX != 0f || gelesen.FinishZ != 0f))
                {
                    var zdx = gelesen.FinishX - ziel.Value.X;
                    var zdz = gelesen.FinishZ - ziel.Value.Z;
                    if (MathF.Sqrt(zdx * zdx + zdz * zdz) > RecordedLap.StartTolerance)
                    {
                        continue;
                    }
                }

                besteEntfernung = entfernung;
                beste = Path.GetFileName(ordner);
            }
            return beste;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Clean(string text)
    {
        var schlecht = Path.GetInvalidFileNameChars();
        var sauber = new string(text.Select(c => schlecht.Contains(c) ? '-' : c).ToArray())
            .Trim().Trim('.');
        return sauber.Length == 0 ? "none" : sauber;
    }

    /// <summary>Eine Runde ablegen. Gibt den Pfad zurueck, oder null.</summary>
    public static string? Save(RecordedLap lap, string? tag, string? root = null)
    {
        try
        {
            if (lap.Samples.Count < 3) { return null; }
            var wurzel = root ?? Root;
            // EINMAL bestimmt und dann weitergereicht: die Suche liest Ordner, und
            // dreimal dieselbe Suche koennte dreimal verschieden ausgehen, wenn
            // dazwischen geschrieben wird.
            var kurs = CourseKey(lap, wurzel);
            var ordner = Path.Combine(
                wurzel,
                Clean(kurs),
                Clean(ClassOf(lap.PerformanceIndex)),
                Clean($"car{lap.CarOrdinal}"),
                Clean(lap.TuneKey.Replace('/', '-')),
                Clean(string.IsNullOrWhiteSpace(tag) ? "untagged" : tag!));
            Directory.CreateDirectory(ordner);

            // Punkt als Dezimaltrennzeichen, unabhaengig von der Spracheinstellung:
            // "80,705s" im Dateinamen kommt aus der deutschen Locale und stolpert
            // jedes Auswertungsskript, das die Zahl aus dem Namen liest.
            var sekunden = lap.LapSeconds.ToString(
                "0.000", System.Globalization.CultureInfo.InvariantCulture);
            var name = $"{lap.RecordedAt:yyyy-MM-dd_HH-mm-ss}_{sekunden}s"
                       + (lap.StandingStart ? "_standing" : string.Empty)
                       // Ab einem Viertel der Runde im Wasser ist sie nicht mehr mit
                       // einer trockenen zu vergleichen -- das gehoert in den Namen,
                       // nicht nur in die Datei.
                       + (lap.WetFraction >= 0.25f ? "_wet" : string.Empty)
                       + (lap.EndedAtFinish ? "_sprint" : string.Empty)
                       // Damit man es dem Dateinamen ansieht: selbst gestoppt, in
                       // der freien Welt, ohne Streckengrenzen.
                       + (lap.FreeRoam ? "_freeroam" : string.Empty) + ".json";
            var pfad = Path.Combine(ordner, name);
            File.WriteAllText(pfad, JsonSerializer.Serialize(new ArchivedLap
            {
                Course = kurs,
                Tag = string.IsNullOrWhiteSpace(tag) ? null : tag,
                Class = ClassOf(lap.PerformanceIndex),
                Lap = lap,
            }, Format));

            NoteCourse(Path.Combine(wurzel, Clean(kurs)), lap);

            // Die volle Spur DANEBEN, nicht hinein. Scheitert sie, ist die Runde
            // trotzdem abgelegt -- sie ist das Wichtige, die Spur ein Zusatz.
            lap.FullTrack?.Save(pfad);

            return pfad;
        }
        catch (Exception)
        {
            // Eine verlorene Aufzeichnung ist aergerlich, ein Absturz im Rennen mehr.
            return null;
        }
    }

    /// <summary>
    /// Je Strecke eine Notiz mit einem leeren Namensfeld.
    /// </summary>
    /// <remarks>
    /// Damit sich ein Ordner "course_1200_-430" nachtraeglich "Hokubu Sprint" nennen
    /// laesst -- die Telemetrie liefert den Namen nicht, ein Mensch kennt ihn.
    /// </remarks>
    private static void NoteCourse(string ordner, RecordedLap lap)
    {
        try
        {
            var pfad = Path.Combine(ordner, "course.json");
            var notiz = new CourseNote();
            if (File.Exists(pfad))
            {
                notiz = JsonSerializer.Deserialize<CourseNote>(File.ReadAllText(pfad))
                        ?? new CourseNote();
            }
            // DER ANKER BLEIBT, WO ER WAR.
            //
            // Vorher wurde er bei jeder Runde auf den neuesten Startpunkt gesetzt.
            // Solange der Ordnername aus einer Rundung entstand, war das folgenlos;
            // jetzt SUCHT die naechste Runde anhand dieses Punktes. Ein Anker, der
            // bei jeder Runde ein paar Meter mitwandert, wandert ueber hundert
            // Runden weit -- und trifft irgendwann die eigene Strecke nicht mehr.
            if (notiz.StartX == 0f && notiz.StartZ == 0f)
            {
                notiz.StartX = lap.StartX;
                notiz.StartZ = lap.StartZ;
            }
            // Das Ziel ebenso: einmal gesetzt, nie nachgezogen.
            if (notiz.FinishX == 0f && notiz.FinishZ == 0f
                && Finish(lap) is { } ende)
            {
                notiz.FinishX = ende.X;
                notiz.FinishZ = ende.Z;
            }
            // DER NAME, EINMAL UND DANN NIE WIEDER.
            //
            // Traegt die Runde einen Streckennamen, erbt ihn der Ordner -- damit
            // hoert der Kurs auf, "course_2075_-6625_to_4200_-3200" zu heissen, und
            // die Tabelle im Overlay kann die angebotene Strecke beim Namen finden
            // statt ueber die Laenge zu raten.
            //
            // Wie der Anker wird auch er nicht nachgezogen. Ein von Hand gesetzter
            // Name ist eine Entscheidung; sie von der naechsten Runde ueberschreiben
            // zu lassen, waere dieselbe Sorte Fehler wie ein wandernder Startpunkt.
            // UNBENANNT heisst auch: der Ordner traegt nur seine eigene Kennung als Namen
            // (siehe IstStreckenname) -- sonst bliebe er fuer immer bei ihr.
            if (!IstStreckenname(notiz.Name) && IstStreckenname(lap.Track))
            {
                notiz.Name = lap.Track!.Trim();
                notiz.NameEvidence = lap.TrackEvidence;
            }
            notiz.Laps += 1;
            notiz.ShortestMetres = notiz.ShortestMetres <= 0
                ? lap.LengthMetres
                : MathF.Min(notiz.ShortestMetres, lap.LengthMetres);
            notiz.LongestMetres = MathF.Max(notiz.LongestMetres, lap.LengthMetres);
            notiz.LastSeen = DateTimeOffset.Now;
            File.WriteAllText(pfad, JsonSerializer.Serialize(
                notiz, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // Eine Notiz ist nie den Preis eines Fehlers wert.
        }
    }

    internal sealed class ArchivedLap
    {
        public string? Course { get; set; }
        public string? Tag { get; set; }
        public string? Class { get; set; }
        public RecordedLap? Lap { get; set; }
    }

    /// <summary>
    /// Ist das ein Streckenname -- oder nur leer bzw. eine Ordnerkennung wie
    /// "course_-1850_1575_to_-1850_1575"?
    /// </summary>
    internal static bool IstStreckenname(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && !name.Trim().StartsWith("course_", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Unbenannten Kursen den Namen geben, den ihre abgelegten Runden einhellig nennen.
    /// </summary>
    /// <remarks>
    /// Einmal beim Start, im Hintergrund. Noetig seit 2026-09-26: ein Kurs mit 34
    /// Runden, 20 davon "Shimanoyama Circuit", hiess weiter nach seiner Kennung -- eine
    /// freie Fahrt hatte ihm die Kennung als Namen gegeben, und NoteCourse ueberschreibt
    /// einen Namen nie. Genannt wird nur, was mindestens zwei Runden sagen und keine
    /// Runde bestreitet; bei zwei verschiedenen Namen bleibt der Kurs unbenannt.
    /// </remarks>
    /// <returns>Wie viele Kurse einen Namen bekamen.</returns>
    public static int NamenNachtragen(string? wurzel = null)
    {
        wurzel ??= Root;
        var benannt = 0;
        if (!Directory.Exists(wurzel)) { return 0; }
        foreach (var ordner in Directory.EnumerateDirectories(wurzel))
        {
            try
            {
                var pfad = Path.Combine(ordner, "course.json");
                if (!File.Exists(pfad)) { continue; }
                var notiz = JsonSerializer.Deserialize<CourseNote>(File.ReadAllText(pfad));
                if (notiz is null || IstStreckenname(notiz.Name)) { continue; }
                var namen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                string? beleg = null;
                foreach (var datei in Directory.EnumerateFiles(ordner, "*.json", SearchOption.AllDirectories))
                {
                    if (Path.GetFileName(datei).Equals("course.json", StringComparison.OrdinalIgnoreCase)) { continue; }
                    try
                    {
                        using var dok = JsonDocument.Parse(File.ReadAllText(datei));
                        if (!dok.RootElement.TryGetProperty("Lap", out var runde)) { continue; }
                        var name = runde.TryGetProperty("track", out var t) && t.ValueKind == JsonValueKind.String
                            ? t.GetString() : null;
                        if (!IstStreckenname(name)) { continue; }
                        namen[name!.Trim()] = namen.GetValueOrDefault(name!.Trim()) + 1;
                        if (runde.TryGetProperty("trackEvidence", out var b) && b.ValueKind == JsonValueKind.String)
                        {
                            beleg ??= b.GetString();
                        }
                    }
                    catch (Exception)
                    {
                        // Eine unlesbare Runde zaehlt nicht.
                    }
                }
                if (namen.Count != 1) { continue; }
                var (einzig, anzahl) = namen.First();
                if (anzahl < 2) { continue; }
                notiz.Name = einzig;
                notiz.NameEvidence = "laps:" + (beleg ?? "unknown");
                File.WriteAllText(pfad, JsonSerializer.Serialize(
                    notiz, new JsonSerializerOptions { WriteIndented = true }));
                benannt++;
            }
            catch (Exception)
            {
                // Ein kaputter Kurs haelt die anderen nicht auf.
            }
        }
        return benannt;
    }

    internal sealed class CourseNote
    {
        /// <summary>
        /// Wie die Strecke heisst.
        /// </summary>
        /// <remarks>
        /// Von Hand zu fuellen (scripts/name_courses.py --setzen), oder von der
        /// ersten Runde geerbt, die einen Namen mitbrachte -- siehe NoteCourse.
        /// </remarks>
        public string Name { get; set; } = string.Empty;

        /// <summary>Woher der Name stammt. Leer heisst: von Hand.</summary>
        public string NameEvidence { get; set; } = string.Empty;
        public float StartX { get; set; }
        public float StartZ { get; set; }

        /// <summary>Wo die Strecke ENDET. 0/0 heisst: noch aus der Zeit davor.</summary>
        /// <remarks>
        /// Am 2026-09-15 dazugekommen. Vorher bestand die Kennung einer Strecke
        /// nur aus dem Startpunkt -- und im eigenen Bestand lagen dadurch in
        /// `course_-6350_-3750` 56 Laeufe mit Zielen 3.725 m auseinander und
        /// Laengen von 906 bis 6.576 m. Das Delta verglich dort gegen eine fremde
        /// Strecke, und niemand konnte es sehen.
        ///
        /// Der Hinweis kam vom Nutzer: Start UND Ziel bestimmen die Strecke, und
        /// zwar unabhaengig davon, ob sie in Rivals, im Einzelspieler, im Koop
        /// oder in der freien Welt gefahren wird.
        ///
        /// Wie der Startpunkt ein ANKER: einmal gesetzt, nie nachgezogen. Ein
        /// Punkt, der bei jeder Runde ein paar Meter mitwandert, wandert ueber
        /// hundert Runden weit.
        /// </remarks>
        public float FinishX { get; set; }
        public float FinishZ { get; set; }
        public int Laps { get; set; }
        public float ShortestMetres { get; set; }
        public float LongestMetres { get; set; }
        public DateTimeOffset LastSeen { get; set; }
    }
}
