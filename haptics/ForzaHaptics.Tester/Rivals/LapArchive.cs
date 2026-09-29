using System.Text.Json;
using System.Text.RegularExpressions;

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

    // ------------------------------------------------------------------ Kursordner
    //
    // DER ORDNER TRAEGT DEN NAMEN DER STRECKE (seit 2026-09-30): "Soni Circuit
    // (course_2800_5000_to_2775_5000)". Vorher hiess er nur nach den Koordinaten, und
    // wer seine Runden suchte, musste jede course.json oeffnen. Die KENNUNG bleibt
    // dieselbe -- in jeder Rundendatei, bei der Einreichung, auf dem Server. Nur der
    // Ordner auf der Platte heisst anders; wer von einer Kennung zum Ordner will, geht
    // ueber KursPfad, wer vom Ordner zur Kennung, ueber KennungAus.

    private static readonly Regex KennungAmEnde = new(@"\((course_[^()\s]+)\)\s*$", RegexOptions.CultureInvariant);

    /// <summary>Ablegen und Umbenennen nie gleichzeitig -- sonst entstuende ein zweiter Ordner.</summary>
    private static readonly object OrdnerSchloss = new();

    /// <summary>Die Kennung zu einem Ordnernamen: "Soni Circuit (course_…)" oder "course_…"; sonst null.</summary>
    internal static string? KennungAus(string? ordnerName)
    {
        var n = (ordnerName ?? string.Empty).Trim();
        if (n.StartsWith("course_", StringComparison.OrdinalIgnoreCase) && !n.Contains(' ')) { return n; }
        var m = KennungAmEnde.Match(n);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Alle Kursordner, benannt oder nicht.</summary>
    internal static IEnumerable<string> KursOrdner(string wurzel)
    {
        if (!Directory.Exists(wurzel)) { return Array.Empty<string>(); }
        return Directory.EnumerateDirectories(wurzel).Where(d => KennungAus(Path.GetFileName(d)) is not null);
    }

    /// <summary>Der Ordner einer Kennung, so wie er heute heisst; gibt es ihn nicht, die Kennung selbst.</summary>
    internal static string KursPfad(string wurzel, string kennung)
    {
        var direkt = Path.Combine(wurzel, Clean(kennung));
        if (Directory.Exists(direkt)) { return direkt; }
        try
        {
            foreach (var d in KursOrdner(wurzel))
            {
                if (string.Equals(KennungAus(Path.GetFileName(d)), kennung, StringComparison.OrdinalIgnoreCase)) { return d; }
            }
        }
        catch (Exception)
        {
            // Nicht lesbar: dann der Ordner, den ein neuer Kurs bekaeme.
        }
        return direkt;
    }

    /// <summary>Wie der Ordner eines Kurses heissen soll: "Name (Kennung)", ohne Namen nur die Kennung.</summary>
    internal static string OrdnerName(string kennung, string? name)
    {
        if (!IstStreckenname(name)) { return kennung; }
        var sauber = Clean(name!.Trim());
        // Kurz genug, dass der ganze Pfad bis zur Runde unter den alten 260 Zeichen
        // bleibt -- der Explorer und manches Werkzeug kennen nichts anderes.
        if (sauber.Length > 48) { sauber = sauber[..48].TrimEnd(' ', '.'); }
        return $"{sauber} ({kennung})";
    }

    /// <summary>
    /// Die Kursordner nach ihren Strecken benennen: einmal beim Start, nach
    /// <see cref="NamenNachtragen"/>. Nie mitten in der Sitzung -- ein zurueckgegebener
    /// Rundenpfad soll gueltig bleiben, solange die App laeuft.
    /// </summary>
    /// <remarks>
    /// Ein Ordner, in dem gerade etwas offen ist (der Explorer, ein Editor), bleibt,
    /// wie er heisst; beim naechsten Start wieder. Aendert sich der Name in course.json,
    /// folgt der Ordner beim naechsten Start.
    /// </remarks>
    /// <returns>Wie viele Ordner umbenannt wurden.</returns>
    public static int OrdnerBenennen(string? wurzel = null)
    {
        wurzel ??= Root;
        var n = 0;
        lock (OrdnerSchloss)
        {
            foreach (var ordner in KursOrdner(wurzel).ToList())
            {
                try
                {
                    var alt = Path.GetFileName(ordner);
                    var kennung = KennungAus(alt)!;
                    var pfad = Path.Combine(ordner, "course.json");
                    var name = File.Exists(pfad)
                        ? JsonSerializer.Deserialize<CourseNote>(File.ReadAllText(pfad))?.Name
                        : null;
                    var soll = OrdnerName(kennung, name);
                    if (string.Equals(alt, soll, StringComparison.Ordinal)) { continue; }
                    var ziel = Path.Combine(wurzel, soll);
                    // Nur Gross-/Kleinschreibung anders: Windows sieht denselben Ordner.
                    if (Directory.Exists(ziel) && !string.Equals(alt, soll, StringComparison.OrdinalIgnoreCase)) { continue; }
                    Directory.Move(ordner, ziel);
                    n++;
                }
                catch (Exception)
                {
                    // Gesperrt oder unlesbar: der Ordner bleibt, wie er heisst.
                }
            }
        }
        return n;
    }

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

    // ------------------------------------------------------------------ Abgebrochene Fahrten
    //
    // EIN ABBRUCH IST KEINE STRECKE (seit 2026-09-30). Das Spiel meldet das Ende eines
    // Rennens gleich, ob man ueber die Ziellinie faehrt, neu startet, pausiert oder das
    // Rennen verlaesst. Jeder Neustart wurde darum als "Sprint bis hierher" abgelegt,
    // und weil der Endpunkt jedes Mal woanders lag, bekam er einen eigenen Kursordner:
    // 31 der 70 Ordner im eigenen Bestand waren solche Stuecke, 16 davon allein von
    // Shimanoyama Sprint. Zwei trugen sogar einen falschen Namen -- 5,9 km Matsumi Climb
    // passten der Laenge nach auf "Norikura Descent" (5,8 km) vom Anmeldeschirm.
    //
    // Erkannt am Weg: dieselbe Startlinie wie eine LAENGERE bekannte Strecke, die ganze
    // Fahrt liegt auf deren Weg, und sie endet deutlich vor deren Ziel. Gemessen am
    // Bestand: echte Ziele streuen 2,5 bis 9 m, Abbrueche 16 bis 132 m; 440 von 445
    // beendeten Fahrten gehen mit Vollgas ueber die Linie, 6 von 78 Abbruechen.
    // Solche Fahrten liegen unter "unfinished", bekommen keinen Streckennamen und
    // zaehlen fuer keine Bestenliste und keine Einreichung.

    /// <summary>Der Ordner fuer abgebrochene Fahrten, neben den Kursen.</summary>
    public const string UnfertigOrdner = "unfinished";

    /// <summary>So nah muss eine Fahrt am Weg der laengeren Strecke bleiben, in Metern.</summary>
    private const float AmWeg = 25f;

    /// <summary>So weit vor deren Ziel muss sie enden, um als Abbruch zu gelten, in Metern.</summary>
    private const float VorDemZiel = 80f;

    /// <summary>Der Weg der laengsten Fahrt eines Kurses -- gemerkt, bis eine Runde dazukommt.</summary>
    private sealed record Referenz(int Laps, float[] X, float[] Z, float[] M);

    private static readonly Dictionary<string, Referenz> Referenzen = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Regex LaengeImKopf = new(@"""lengthMetres""\s*:\s*([0-9.eE+-]+)", RegexOptions.CultureInvariant);

    private static readonly Regex LetzterPunkt = new(
        @"""X""\s*:\s*(-?[0-9.eE+-]+)\s*,\s*""Y""\s*:\s*-?[0-9.eE+-]+\s*,\s*""Z""\s*:\s*(-?[0-9.eE+-]+)",
        RegexOptions.CultureInvariant | RegexOptions.RightToLeft);

    /// <summary>
    /// Eine eben beendete Fahrt einordnen: war sie der abgebrochene Anfang einer
    /// bekannten, laengeren Strecke, wird sie so markiert und verliert ihren Namen.
    /// </summary>
    /// <remarks>
    /// VOR dem Benennen und Ablegen aufrufen. Ein Name, der ueber die Laenge vom
    /// Anmeldeschirm kam, ist bei einer abgebrochenen Fahrt Zufall -- ihre Laenge ist
    /// nicht die der Strecke.
    /// </remarks>
    /// <returns>True, wenn die Fahrt ein Abbruch ist.</returns>
    public static bool Einordnen(RecordedLap lap, string? root = null)
    {
        if (UnfertigVon(lap, root) is not { } voll) { return false; }
        lap.Unfinished = true;
        lap.UnfinishedOf = voll;
        lap.Track = null;
        lap.TrackEvidence = "unfinished";
        return true;
    }

    /// <summary>
    /// Ist diese Fahrt der abgebrochene Anfang einer bekannten, laengeren Strecke?
    /// Dann deren Kennung, sonst null.
    /// </summary>
    public static string? UnfertigVon(RecordedLap lap, string? root = null)
    {
        try
        {
            // Nur eine Fahrt, die mit dem Rennen endete, kann abgebrochen sein; eine
            // Runde, die an der Linie in die naechste ueberging, ist vollstaendig.
            if (!lap.EndedAtFinish || lap.FreeRoam || lap.Samples.Count < 3 || !lap.HasStart) { return null; }
            var ende = lap.Samples[^1];
            // Wer am Start endet, ist einmal herum -- die letzte Runde eines Rundkurses.
            if (Abstand(lap.StartX, lap.StartZ, ende.X, ende.Z) <= RecordedLap.StartTolerance) { return null; }
            var punkte = lap.Samples.Select(s => (s.X, s.Z)).ToList();
            var wurzel = root ?? Root;
            lock (OrdnerSchloss)
            {
                // Endet sie dort, wo schon drei Fahrten auf wenige Meter genau endeten, ist
                // sie angekommen -- das ist eine Ziellinie. Spart auch die Suche.
                if (FindCourseFolder(wurzel, lap) is { } k && EngesZiel(KursPfad(wurzel, k))) { return null; }
                return UnfertigVon(wurzel, lap.StartX, lap.StartZ, punkte, lap.LengthMetres, null);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? UnfertigVon(string wurzel, float sx, float sz,
                                       IReadOnlyList<(float X, float Z)> punkte, float meter, string? ausser)
    {
        string? beste = null;
        var besteLaenge = 0f;
        var ende = punkte[^1];
        foreach (var ordner in KursOrdner(wurzel))
        {
            var kennung = KennungAus(Path.GetFileName(ordner))!;
            if (string.Equals(kennung, ausser, StringComparison.OrdinalIgnoreCase)) { continue; }
            var notiz = LiesNotiz(ordner);
            if (notiz is null || (notiz.StartX == 0f && notiz.StartZ == 0f)) { continue; }
            if (Abstand(notiz.StartX, notiz.StartZ, sx, sz) > RecordedLap.StartTolerance) { continue; }
            if (notiz.LongestMetres < meter + VorDemZiel) { continue; }
            var r = ReferenzVon(ordner, notiz.Laps);
            if (r is null || r.M[^1] < meter + VorDemZiel) { continue; }
            // Endet sie am Ziel dieser Strecke, IST sie diese Strecke.
            if (Abstand(ende.X, ende.Z, r.X[^1], r.Z[^1]) <= VorDemZiel) { continue; }
            var (ab, bei) = Naechster(r, ende.X, ende.Z);
            if (ab > AmWeg || r.M[^1] - bei < VorDemZiel) { continue; }
            // UND DER GANZE WEG DAVOR liegt auch auf ihr -- sonst ist es eine andere
            // Strasse, die zufaellig auf ihr endet.
            var schritt = Math.Max(1, punkte.Count / 60);
            int gesamt = 0, daneben = 0;
            for (var i = 0; i < punkte.Count; i += schritt)
            {
                gesamt++;
                if (Naechster(r, punkte[i].X, punkte[i].Z).Abstand > AmWeg) { daneben++; }
            }
            if (daneben * 10 > gesamt) { continue; }
            if (r.M[^1] > besteLaenge)
            {
                beste = kennung;
                besteLaenge = r.M[^1];
            }
        }
        return beste;
    }

    /// <summary>
    /// Kursordner, die nur abgebrochene Fahrten einer anderen Strecke enthalten, nach
    /// "unfinished" verlegen -- einmal beim Start, vor dem Benennen.
    /// </summary>
    /// <remarks>
    /// Verlegt, nicht geloescht: die Fahrten bleiben, nur stehen sie nicht mehr als
    /// eigene Strecken zwischen den echten. Der Ordner heisst dann nach der Strecke, zu
    /// der er gehoert: "unfinished\Shimanoyama Sprint (course_…)".
    ///
    /// Ausgenommen: ein von Hand benannter Kurs, und ein Kurs, dessen Fahrten (drei
    /// oder mehr) alle auf wenige Meter am selben Punkt enden -- das ist eine Ziellinie.
    /// </remarks>
    /// <returns>Wie viele Ordner verlegt wurden.</returns>
    public static int UnfertigeAussortieren(string? wurzel = null)
    {
        wurzel ??= Root;
        var n = 0;
        lock (OrdnerSchloss)
        {
            var alle = KursOrdner(wurzel).Select(o => (Ordner: o, Notiz: LiesNotiz(o))).ToList();
            foreach (var (ordner, notiz) in alle)
            {
                try
                {
                    if (notiz is null || (notiz.StartX == 0f && notiz.StartZ == 0f)) { continue; }
                    if (IstStreckenname(notiz.Name) && VonHand(notiz.NameEvidence)) { continue; }
                    if ((notiz.FinishX != 0f || notiz.FinishZ != 0f)
                        && Abstand(notiz.StartX, notiz.StartZ, notiz.FinishX, notiz.FinishZ) <= RecordedLap.StartTolerance)
                    {
                        continue;
                    }
                    // Billig vorab: gibt es ueberhaupt eine laengere Strecke vom selben Start?
                    if (!alle.Any(c => c.Ordner != ordner && c.Notiz is { } cn
                                       && Abstand(cn.StartX, cn.StartZ, notiz.StartX, notiz.StartZ) <= RecordedLap.StartTolerance
                                       && cn.LongestMetres >= notiz.LongestMetres + VorDemZiel))
                    {
                        continue;
                    }
                    if (!Directory.Exists(ordner) || EngesZiel(ordner)) { continue; }
                    var eigen = ReferenzVon(ordner, notiz.Laps);
                    if (eigen is null) { continue; }
                    var kennung = KennungAus(Path.GetFileName(ordner))!;
                    var punkte = eigen.X.Zip(eigen.Z, (x, z) => (x, z)).ToList();
                    var voll = UnfertigVon(wurzel, notiz.StartX, notiz.StartZ, punkte, eigen.M[^1], kennung);
                    if (voll is null) { continue; }

                    var vollName = LiesNotiz(KursPfad(wurzel, voll))?.Name;
                    var alterName = notiz.Name?.Trim() ?? string.Empty;
                    notiz.UnfinishedOf = voll;
                    notiz.NameEvidence = "unfinished" + (IstStreckenname(alterName) ? $" (was: {alterName})" : string.Empty);
                    notiz.Name = string.Empty;
                    File.WriteAllText(Path.Combine(ordner, "course.json"), JsonSerializer.Serialize(
                        notiz, new JsonSerializerOptions { WriteIndented = true }));

                    var basis = Path.Combine(wurzel, UnfertigOrdner);
                    Directory.CreateDirectory(basis);
                    var ziel = Path.Combine(basis, OrdnerName(kennung, vollName));
                    for (var i = 2; Directory.Exists(ziel); i++)
                    {
                        ziel = Path.Combine(basis, OrdnerName($"{kennung}_{i}", vollName));
                    }
                    Directory.Move(ordner, ziel);
                    n++;
                }
                catch (Exception)
                {
                    // Gesperrt oder unlesbar: der Ordner bleibt, wo er ist.
                }
            }
        }
        return n;
    }

    /// <summary>Enden drei oder mehr Fahrten eines Ordners alle auf wenige Meter am selben Punkt?</summary>
    private static bool EngesZiel(string ordner)
    {
        var enden = new List<(float X, float Z)>();
        foreach (var datei in Directory.EnumerateFiles(ordner, "*.json", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(datei).Equals("course.json", StringComparison.OrdinalIgnoreCase)) { continue; }
            if (EndeAusDatei(datei) is { } e) { enden.Add(e); }
            if (enden.Count >= 40) { break; }
        }
        if (enden.Count < 3) { return false; }
        var mx = enden.Average(e => e.X);
        var mz = enden.Average(e => e.Z);
        return enden.All(e => Abstand(e.X, e.Z, mx, mz) <= 15f);
    }

    /// <summary>Der letzte Messpunkt einer abgelegten Fahrt, aus dem Dateiende gelesen.</summary>
    private static (float X, float Z)? EndeAusDatei(string datei)
    {
        try
        {
            using var strom = File.OpenRead(datei);
            var n = (int)Math.Min(8192, strom.Length);
            strom.Seek(-n, SeekOrigin.End);
            var puffer = new byte[n];
            var gelesen = strom.Read(puffer, 0, n);
            var m = LetzterPunkt.Match(System.Text.Encoding.UTF8.GetString(puffer, 0, gelesen));
            if (!m.Success) { return null; }
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return (float.Parse(m.Groups[1].Value, inv), float.Parse(m.Groups[2].Value, inv));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Der Weg der laengsten Fahrt eines Kursordners.</summary>
    private static Referenz? ReferenzVon(string ordner, int laps)
    {
        lock (Referenzen)
        {
            if (Referenzen.TryGetValue(ordner, out var gemerkt) && gemerkt.Laps == laps) { return gemerkt; }
        }
        // Die Laenge steht vorn in jeder Datei; nur die laengste wird ganz gelesen.
        string? laengste = null;
        var meter = 0f;
        foreach (var datei in Directory.EnumerateFiles(ordner, "*.json", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(datei).Equals("course.json", StringComparison.OrdinalIgnoreCase)) { continue; }
            var m = LaengeImKopf.Match(Kopf(datei));
            if (m.Success
                && float.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                                  System.Globalization.CultureInfo.InvariantCulture, out var l)
                && l > meter)
            {
                meter = l;
                laengste = datei;
            }
        }
        if (laengste is null) { return null; }
        var lap = JsonSerializer.Deserialize<ArchivedLap>(File.ReadAllText(laengste))?.Lap;
        if (lap is null || lap.Samples.Count < 3) { return null; }
        var neu = new Referenz(laps,
                               lap.Samples.Select(s => s.X).ToArray(),
                               lap.Samples.Select(s => s.Z).ToArray(),
                               lap.Samples.Select(s => s.Metres).ToArray());
        lock (Referenzen) { Referenzen[ordner] = neu; }
        return neu;
    }

    private static string Kopf(string datei)
    {
        using var strom = File.OpenRead(datei);
        var puffer = new byte[600];
        var n = strom.Read(puffer, 0, puffer.Length);
        return System.Text.Encoding.UTF8.GetString(puffer, 0, n);
    }

    /// <summary>Der naechste Punkt des Weges: wie weit weg, und bei welchem Meter.</summary>
    private static (float Abstand, float Bei) Naechster(Referenz r, float x, float z)
    {
        var best = float.MaxValue;
        var bei = 0f;
        for (var i = 0; i < r.X.Length; i++)
        {
            var dx = r.X[i] - x;
            var dz = r.Z[i] - z;
            var d = (dx * dx) + (dz * dz);
            if (d < best)
            {
                best = d;
                bei = r.M[i];
            }
        }
        return (MathF.Sqrt(best), bei);
    }

    private static float Abstand(float ax, float az, float bx, float bz) =>
        MathF.Sqrt(((ax - bx) * (ax - bx)) + ((az - bz) * (az - bz)));

    private static CourseNote? LiesNotiz(string ordner)
    {
        try
        {
            var pfad = Path.Combine(ordner, "course.json");
            return File.Exists(pfad) ? JsonSerializer.Deserialize<CourseNote>(File.ReadAllText(pfad)) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Ein von Hand gesetzter Name: leerer Beleg oder "manual".</summary>
    internal static bool VonHand(string? beleg) =>
        string.IsNullOrWhiteSpace(beleg) || string.Equals(beleg.Trim(), "manual", StringComparison.OrdinalIgnoreCase);

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
            foreach (var ordner in KursOrdner(wurzel))
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
                // DIE KENNUNG, nicht der Ordnername: sie wandert in die Rundendatei und
                // zum Server, und der Ordner heisst inzwischen "Soni Circuit (course_…)".
                beste = KennungAus(Path.GetFileName(ordner));
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
        // Nie waehrend des Umbenennens beim Start (OrdnerBenennen).
        lock (OrdnerSchloss) { return Ablegen(lap, tag, root); }
    }

    private static string? Ablegen(RecordedLap lap, string? tag, string? root)
    {
        try
        {
            if (lap.Samples.Count < 3) { return null; }
            var wurzel = root ?? Root;
            // EINMAL bestimmt und dann weitergereicht: die Suche liest Ordner, und
            // dreimal dieselbe Suche koennte dreimal verschieden ausgehen, wenn
            // dazwischen geschrieben wird.
            // Eine abgebrochene Fahrt (Einordnen) liegt neben den Kursen, nicht zwischen ihnen.
            var basis = lap.Unfinished ? Path.Combine(wurzel, UnfertigOrdner) : wurzel;
            var kurs = CourseKey(lap, basis);
            // Der Ordner, wie er heute heisst. Ein NEUER Kurs traegt den Streckennamen
            // gleich mit; ein alter bekommt ihn beim naechsten Start (OrdnerBenennen).
            // Ein Abbruch heisst nach der Strecke, deren Anfang er ist.
            var kursOrdner = KursPfad(basis, kurs);
            var ordnerName = lap.Unfinished
                ? (lap.UnfinishedOf is { } voll ? LiesNotiz(KursPfad(wurzel, voll))?.Name : null)
                : lap.Track;
            if (!Directory.Exists(kursOrdner) && IstStreckenname(ordnerName))
            {
                kursOrdner = Path.Combine(basis, OrdnerName(kurs, ordnerName));
            }
            var ordner = Path.Combine(
                kursOrdner,
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

            NoteCourse(kursOrdner, lap);

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
            if (lap.Unfinished && lap.UnfinishedOf is { } voll) { notiz.UnfinishedOf = voll; }
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
                if (notiz is null) { continue; }
                var schonBenannt = IstStreckenname(notiz.Name);
                // Von Hand gesetzt: eine Entscheidung, keine Vermutung -- bleibt.
                if (schonBenannt && VonHand(notiz.NameEvidence)) { continue; }
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
                if (!schonBenannt)
                {
                    if (namen.Count != 1) { continue; }
                    var (einzig, anzahl) = namen.First();
                    if (anzahl < 2) { continue; }
                    notiz.Name = einzig;
                    notiz.NameEvidence = "laps:" + (beleg ?? "unknown");
                }
                else
                {
                    // DIE RUNDEN WIDERSPRECHEN DEM NAMEN (seit 2026-09-30).
                    //
                    // Ein Kurs erbt den Namen seiner ersten benannten Runde und behielt
                    // ihn fuer immer. "course_100_4375" hiess so "Venus Sprint" -- eine
                    // Runde vom 2026-09-22, deren Laenge 4 % neben Venus Sprint lag --,
                    // waehrend fuenf spaetere Runden "Shikisai Sprint" sagten und Form wie
                    // Laenge auch. Ein automatisch vergebener Name weicht darum, wenn
                    // mindestens drei Runden einen anderen nennen und dreimal so viele
                    // wie den bisherigen.
                    var jetzt = namen.GetValueOrDefault(notiz.Name.Trim());
                    var anders = namen.Where(x => !string.Equals(x.Key, notiz.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                                      .OrderByDescending(x => x.Value).FirstOrDefault();
                    if (anders.Key is null || anders.Value < 3 || anders.Value < 3 * Math.Max(1, jetzt)) { continue; }
                    notiz.NameEvidence = $"laps-majority {anders.Value}:{jetzt} (was: {notiz.Name.Trim()})";
                    notiz.Name = anders.Key;
                }
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

        /// <summary>Nur unter "unfinished": die Strecke, deren abgebrochener Anfang das ist.</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? UnfinishedOf { get; set; }
    }
}
