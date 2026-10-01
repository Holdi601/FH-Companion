using System.Globalization;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Was DU in dieser Klasse schon gefahren bist -- aus dem eigenen Rundenbestand.
/// </summary>
/// <remarks>
/// ## Wozu
///
/// Die Auto-Empfehlung beantwortet "welches Auto ist in dieser Klasse schnell",
/// gerechnet aus den Runden fremder Spieler. Sie beantwortet nicht "welches davon
/// habe ich ueberhaupt" -- und das ist die Frage, an der die Wahl im Menue
/// tatsaechlich haengt. Diese Klasse liefert die zweite Haelfte.
///
/// ## Warum hier KEINE Rangliste der eigenen Autos entsteht
///
/// Das war der Wunsch, und der Bestand gibt ihn nicht her. Am 2026-09-15 gemessen,
/// 178 eigene Runden auf 35 Kursen: von 110 vergleichbaren Gruppen
/// (Klasse x Kurs x stehend/fliegend x Sprint/Runde) enthalten ganze **vier** mehr
/// als ein Auto. In den Klassen A, C und S2 keine einzige.
///
/// Zeiten von VERSCHIEDENEN Kursen gegeneinanderzustellen waere keine Rangfolge,
/// sondern eine Erfindung: 68 s auf einem kurzen Sprint sind nicht besser als 146 s
/// auf einem langen. Darum gibt es hier zwei getrennte Auskuenfte:
///
///   <see cref="Bests"/>  je Auto eine TATSACHE -- deine Bestzeit und wo.
///   <see cref="Duels"/>  nur die Faelle, in denen zwei deiner Autos WIRKLICH
///                        denselben Kurs unter denselben Bedingungen gefahren sind.
///
/// ## Warum der Ordnerbaum gelesen wird und nicht die Dateien
///
/// `LapArchive.Save` legt so ab:
///
///     laps/&lt;kurs&gt;/&lt;klasse&gt;/car&lt;ordinal&gt;/&lt;tune&gt;/&lt;tag&gt;/&lt;zeit&gt;_&lt;sekunden&gt;s[_flags].json
///
/// Klasse, Auto, Sekunden und die Kennzeichen stehen also bereits im PFAD. Jede
/// Datei zu oeffnen hiesse, tausend JSON-Dokumente zu lesen, um Zahlen zu erfahren,
/// die im Namen stehen -- und das im Zeichenpfad einer Overlay-Aktualisierung.
///
/// ## Stehend gegen fliegend wird nie verglichen
///
/// Ein stehender Start kostet mehrere Sekunden. Die Kennzeichen `_standing` und
/// `_sprint` gehen darum in den Gruppenschluessel ein, nicht in eine Fussnote.
/// </remarks>
internal static class OwnCars
{
    /// <summary>Deine Bestzeit mit einem Auto.</summary>
    /// <param name="Ordinal">Die Telemetrie-Kennung des Autos.</param>
    /// <param name="BestSeconds">Die schnellste Runde, die davon vorliegt.</param>
    /// <param name="Course">Auf welchem Kurs sie gefahren wurde.</param>
    /// <param name="Standing">Stehender Start.</param>
    /// <param name="Sprint">Bis zur Ziellinie statt volle Runde.</param>
    /// <param name="Laps">Wie viele Runden insgesamt mit diesem Auto.</param>
    /// <param name="Courses">Auf wie vielen verschiedenen Kursen.</param>
    internal sealed record Best(int Ordinal, double BestSeconds, string Course,
                                bool Standing, bool Sprint, int Laps, int Courses);

    /// <summary>Zwei oder mehr deiner Autos auf demselben Kurs, gleiche Bedingungen.</summary>
    internal sealed record Duel(string Course, bool Standing, bool Sprint,
                                List<(int Ordinal, double Seconds)> Order);

    private sealed record Lauf(string Course, string Klass, int Ordinal,
                               double Seconds, bool Standing, bool Sprint);

    // Der Bestand aendert sich nur, wenn eine Runde dazukommt. Zweimal je Sekunde
    // den Baum abzulaufen waere Verschwendung; die Schreibzeit des Wurzelordners
    // reicht als Anlass, neu zu lesen.
    private static List<Lauf>? _zwischenspeicher;
    private static DateTime _gelesen = DateTime.MinValue;
    private static string? _wurzelGelesen;

    /// <summary>Die Bestzeiten je Auto in dieser Klasse, schnellstes zuerst.</summary>
    /// <remarks>
    /// ACHTUNG BEIM LESEN DER REIHENFOLGE: sie ist NUR dann eine Rangfolge, wenn
    /// alle Eintraege denselben Kurs nennen. Sonst stehen hier Tatsachen
    /// nebeneinander, keine Wertung -- der Aufrufer muss den Kurs mit anzeigen.
    /// </remarks>
    public static List<Best> Bests(string klass, string? root = null)
    {
        var laeufe = Laden(root).Where(l => Passt(l.Klass, klass)).ToList();
        var ergebnis = new List<Best>();
        foreach (var gruppe in laeufe.GroupBy(l => l.Ordinal))
        {
            var beste = gruppe.OrderBy(l => l.Seconds).First();
            ergebnis.Add(new Best(
                gruppe.Key, beste.Seconds, beste.Course, beste.Standing, beste.Sprint,
                gruppe.Count(), gruppe.Select(l => l.Course).Distinct().Count()));
        }
        return ergebnis.OrderBy(b => b.BestSeconds).ToList();
    }

    /// <summary>Nur die echten Vergleiche: zwei deiner Autos, ein Kurs, gleiche Art.</summary>
    public static List<Duel> Duels(string klass, string? root = null)
    {
        var ergebnis = new List<Duel>();
        var laeufe = Laden(root).Where(l => Passt(l.Klass, klass));
        foreach (var gruppe in laeufe.GroupBy(
                     l => (l.Course, l.Standing, l.Sprint)))
        {
            var jeAuto = gruppe.GroupBy(l => l.Ordinal)
                               .Select(g => (Ordinal: g.Key,
                                             Seconds: g.Min(l => l.Seconds)))
                               .OrderBy(x => x.Seconds)
                               .ToList();
            if (jeAuto.Count >= 2)
            {
                ergebnis.Add(new Duel(gruppe.Key.Course, gruppe.Key.Standing,
                                      gruppe.Key.Sprint, jeAuto));
            }
        }
        // Der aussagekraeftigste zuerst: mehr Autos schlaegt weniger.
        return ergebnis.OrderByDescending(d => d.Order.Count).ToList();
    }

    /// <summary>Eine Spalte der Uebersicht: ein Kurs.</summary>
    /// <param name="Key">Der Ordnername des Kurses.</param>
    /// <param name="Label">Wie er ueberschrieben wird.</param>
    /// <param name="Cars">Wie viele deiner Autos dort eine Zeit haben.</param>
    /// <param name="Laps">Wie viele deiner Runden dort liegen.</param>
    /// <param name="Ambiguous">
    /// Wahr, wenn mehrere eigene Kurse auf die Laenge dieser Strecke passen. Dann
    /// bleibt die Spalte ABSICHTLICH leer -- siehe <see cref="Table"/>.
    /// </param>
    internal sealed record CourseColumn(string Key, string Label, int Cars, int Laps,
                                        bool Ambiguous = false);

    /// <summary>Eine Zeile der Uebersicht: ein Auto und seine Zeiten je Kurs.</summary>
    internal sealed record MatrixRow(int Ordinal, double BestSeconds, int Laps,
                                     Dictionary<string, double> ByCourse);

    /// <summary>Die Uebersicht: Autos mal Kurse.</summary>
    internal sealed record Matrix(List<CourseColumn> Courses, List<MatrixRow> Rows);

    /// <summary>
    /// Deine Autos einer Klasse, und je ANGEBOTENER STRECKE deine Bestzeit.
    /// </summary>
    /// <remarks>
    /// ## Der Fehler, der das hier ausgeloest hat
    ///
    /// Bis zum 2026-09-16 waehlte diese Funktion die Spalten selbst: die drei
    /// eigenen Kurse, auf denen die meisten eigenen Autos eine Zeit hatten. Auf dem
    /// Anmeldeschirm stand dann eine Tabelle mit Laengen wie "6.0km", die mit den
    /// drei angebotenen Strecken nichts zu tun hatten, und mit Autos, die der
    /// Nutzer dort nie gefahren war. Der Nutzer hat es als Zufallszahlen gelesen,
    /// und er hatte recht: es WAREN Zahlen zu einer anderen Frage.
    ///
    /// Die Spalten sind darum jetzt die drei Strecken des Schirms, in der
    /// Reihenfolge des Schirms. Eine Zelle bleibt leer, wenn dort keine eigene
    /// Runde liegt -- das ist eine Auskunft, die vorige Fassung war eine
    /// Verwechslung.
    ///
    /// ## Wie eine Strecke zu einem eigenen Kursordner findet
    ///
    /// Zwei Wege, in dieser Reihenfolge:
    ///
    ///   1. NAME. Traegt ein `course.json` den Streckennamen, ist die Sache klar.
    ///      So wird es, sobald Runden mit Streckennamen aufgezeichnet werden.
    ///   2. LAENGE. Der Schirm nennt die Gesamtstrecke und die Rundenzahl, daraus
    ///      die Rundenlaenge; der Kursordner nennt seine gemessene `ShortestMetres`.
    ///      Passen sie auf <see cref="Toleranz"/> genau zusammen, und passt GENAU
    ///      EIN Kurs, gilt er als diese Strecke.
    ///
    /// Passen mehrere, gilt KEINER. Zwei Kurse gleicher Laenge lassen sich ueber
    /// die Laenge nicht unterscheiden, und einen davon zu waehlen hiesse, in der
    /// Haelfte der Faelle eine fremde Zeit unter einen Streckennamen zu schreiben
    /// -- genau der Fehler, der gerade behoben wird. Die Spalte bleibt dann leer
    /// und <see cref="CourseColumn.Ambiguous"/> sagt, warum.
    /// </remarks>
    /// <param name="routen">
    /// Die angebotenen Strecken in der Reihenfolge des Schirms, je mit der Laenge
    /// EINER Runde in Metern. Ist die Laenge unbekannt, 0 uebergeben -- dann bleibt
    /// nur der Weg ueber den Namen.
    /// </param>
    public static Matrix Table(string klass,
                               IReadOnlyList<(string Name, double LapMetres)> routen,
                               string? root = null)
    {
        var wurzel = root ?? LapArchive.Root;
        var vermerke = Kursvermerke(wurzel);
        var laeufe = Laden(root).Where(l => Passt(l.Klass, klass)).ToList();

        var spalten = new List<CourseColumn>();
        foreach (var (name, meter) in routen)
        {
            var (kurs, mehrdeutig) = KursZurStrecke(vermerke, name, meter);
            spalten.Add(new CourseColumn(kurs ?? string.Empty, name,
                                         kurs is null ? 0
                                         : laeufe.Where(l => l.Course == kurs)
                                                 .Select(l => l.Ordinal).Distinct().Count(),
                                         kurs is null ? 0
                                         : laeufe.Count(l => l.Course == kurs),
                                         mehrdeutig));
        }

        // Auto -> Kurs -> beste Zeit, aber nur fuer die Kurse, die eine Spalte sind.
        var gesucht = new HashSet<string>(
            spalten.Where(s => s.Key.Length > 0).Select(s => s.Key),
            StringComparer.Ordinal);
        var jeAuto = new Dictionary<int, Dictionary<string, double>>();
        foreach (var l in laeufe)
        {
            if (!gesucht.Contains(l.Course)) { continue; }
            if (!jeAuto.TryGetValue(l.Ordinal, out var kurse))
            {
                kurse = new Dictionary<string, double>(StringComparer.Ordinal);
                jeAuto[l.Ordinal] = kurse;
            }
            if (!kurse.TryGetValue(l.Course, out var da) || l.Seconds < da)
            {
                kurse[l.Course] = l.Seconds;
            }
        }

        var zeilen = jeAuto.Select(a => new MatrixRow(
                               a.Key, a.Value.Values.Min(),
                               laeufe.Count(l => l.Ordinal == a.Key
                                                 && a.Value.ContainsKey(l.Course)),
                               a.Value))
                           .ToList();

        // SORTIERT NACH DER ERSTEN SPALTE -- das ist es, was "nach der kuerzesten
        // Rundenzeit sortiert" auf dem Schirm bedeutet: die Zahlen der ersten
        // Spalte laufen von oben nach unten aufwaerts. Wer dort keine Zeit hat,
        // wird nach der zweiten einsortiert, dann nach der dritten. Ueber Spalten
        // hinweg wird NICHT verglichen; es sind verschiedene Strecken.
        double Schluessel(MatrixRow r, int spalte)
        {
            if (spalte >= spalten.Count) { return double.MaxValue; }
            var key = spalten[spalte].Key;
            return key.Length > 0 && r.ByCourse.TryGetValue(key, out var s)
                ? s : double.MaxValue;
        }

        zeilen = zeilen.OrderBy(r => Schluessel(r, 0))
                       .ThenBy(r => Schluessel(r, 1))
                       .ThenBy(r => Schluessel(r, 2))
                       .ThenBy(r => r.BestSeconds)
                       .ToList();

        return new Matrix(spalten, zeilen);
    }

    /// <summary>Wie weit die gemessene Laenge von der angesagten abweichen darf.</summary>
    /// <remarks>
    /// Sechs Hundertstel. Die Zahl des Schirms ist gerundet ("8.5 KM"), und die
    /// eigene Messung ist die WIRKLICH gefahrene Strecke: wer weit schneidet oder
    /// weit aussen faehrt, misst nicht dasselbe wie die Streckenlaenge des Spiels.
    /// Enger waere ein Feature, das bei sauberem Fahren funktioniert und sonst
    /// nicht; weiter faengt Nachbarkurse ein, und dann greift die
    /// Mehrdeutigkeitsregel und die Spalte bleibt ohnehin leer.
    /// </remarks>
    private const double Toleranz = 0.06;

    /// <summary>Welcher eigene Kurs ist diese Strecke?</summary>
    private static (string? Kurs, bool Mehrdeutig) KursZurStrecke(
        IReadOnlyDictionary<string, (string Name, double Metres)> vermerke,
        string strecke, double meter)
    {
        // 1. Der Name. Wer ihn gesetzt hat, meint ihn.
        foreach (var (kurs, v) in vermerke)
        {
            if (v.Name.Length > 0
                && string.Equals(v.Name, strecke, StringComparison.OrdinalIgnoreCase))
            {
                return (kurs, false);
            }
        }

        if (meter <= 0) { return (null, false); }

        // 2. Die Laenge, aber nur wenn sie EINEN Kurs meint.
        var treffer = new List<string>();
        foreach (var (kurs, v) in vermerke)
        {
            if (v.Metres <= 0) { continue; }
            if (Math.Abs(v.Metres - meter) / meter <= Toleranz) { treffer.Add(kurs); }
        }
        if (treffer.Count == 1) { return (treffer[0], false); }
        return (null, treffer.Count > 1);
    }

    private static Dictionary<string, (string Name, double Metres)>? _vermerke;
    private static DateTime _vermerkeStand = DateTime.MinValue;

    /// <summary>Name und gemessene Laenge je Kursordner.</summary>
    /// <remarks>
    /// Zwischengespeichert wie der Rundenbestand und aus demselben Grund: das hier
    /// laeuft im Zeichenpfad einer Overlay-Aktualisierung, zweimal je Sekunde.
    /// </remarks>
    private static Dictionary<string, (string Name, double Metres)> Kursvermerke(
        string wurzel)
    {
        DateTime stand;
        try
        {
            stand = Directory.Exists(wurzel)
                ? Directory.GetLastWriteTimeUtc(wurzel)
                : DateTime.MinValue;
        }
        catch (Exception)
        {
            stand = DateTime.MinValue;
        }
        if (_vermerke is not null && stand == _vermerkeStand) { return _vermerke; }

        var ergebnis = new Dictionary<string, (string, double)>(StringComparer.Ordinal);
        try
        {
            if (Directory.Exists(wurzel))
            {
                foreach (var ordner in Directory.EnumerateDirectories(wurzel))
                {
                    var datei = Path.Combine(ordner, "course.json");
                    if (!File.Exists(datei)) { continue; }
                    var name = string.Empty;
                    var meter = 0.0;
                    try
                    {
                        using var strom = File.OpenRead(datei);
                        using var dok = System.Text.Json.JsonDocument.Parse(strom);
                        if (dok.RootElement.TryGetProperty("Name", out var n)
                            && n.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            name = n.GetString() ?? string.Empty;
                        }
                        if (dok.RootElement.TryGetProperty("ShortestMetres", out var m)
                            && m.TryGetDouble(out var mm))
                        {
                            meter = mm;
                        }
                    }
                    catch (Exception)
                    {
                        // Ein unlesbarer Vermerk kostet einen Kurs, nicht die Tabelle.
                        continue;
                    }
                    ergebnis[LapArchive.KennungAus(Path.GetFileName(ordner)) ?? Path.GetFileName(ordner)] = (name, meter);
                }
            }
        }
        catch (Exception)
        {
            // Siehe Laden(): ein unlesbarer Bestand laesst das Overlay nicht scheitern.
        }

        _vermerke = ergebnis;
        _vermerkeStand = stand;
        return ergebnis;
    }

    /// <summary>Wie viele eigene Runden ueberhaupt vorliegen.</summary>
    public static int TotalLaps(string? root = null) => Laden(root).Count;

    /// <summary>Eine Zeit lesbar machen: 1:08.09 statt 68.090.</summary>
    public static string TimeText(double seconds)
    {
        if (seconds < 60)
        {
            return seconds.ToString("0.00", CultureInfo.InvariantCulture) + "s";
        }
        var m = (int)(seconds / 60);
        var s = seconds - (m * 60);
        return m + ":" + s.ToString("00.00", CultureInfo.InvariantCulture);
    }

    /// <summary>Die Bedingungen kurz: was hier verglichen werden darf.</summary>
    public static string ConditionText(bool standing, bool sprint) =>
        (standing ? "standing" : "flying") + (sprint ? " sprint" : " lap");

    private static bool Passt(string abgelegt, string gesucht) =>
        string.Equals(abgelegt, gesucht, StringComparison.OrdinalIgnoreCase);

    private static List<Lauf> Laden(string? root)
    {
        var wurzel = root ?? LapArchive.Root;
        DateTime stand;
        try
        {
            stand = Directory.Exists(wurzel)
                ? Directory.GetLastWriteTimeUtc(wurzel)
                : DateTime.MinValue;
        }
        catch (Exception)
        {
            stand = DateTime.MinValue;
        }

        if (_zwischenspeicher is not null && _wurzelGelesen == wurzel
            && stand == _gelesen)
        {
            return _zwischenspeicher;
        }

        var laeufe = new List<Lauf>();
        try
        {
            if (Directory.Exists(wurzel))
            {
                foreach (var datei in Directory.EnumerateFiles(
                             wurzel, "*.json", SearchOption.AllDirectories))
                {
                    var lauf = AusPfad(wurzel, datei);
                    if (lauf is not null) { laeufe.Add(lauf); }
                }
            }
        }
        catch (Exception)
        {
            // Ein unlesbarer Bestand ist kein Grund, das Overlay scheitern zu
            // lassen -- dann steht die Spalte eben leer.
        }

        _zwischenspeicher = laeufe;
        _gelesen = stand;
        _wurzelGelesen = wurzel;
        return laeufe;
    }

    /// <summary>Aus dem Pfad einen Lauf machen -- ohne die Datei zu oeffnen.</summary>
    private static Lauf? AusPfad(string wurzel, string datei)
    {
        var name = Path.GetFileName(datei);
        if (name.Equals("course.json", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rel = Path.GetRelativePath(wurzel, datei)
                      .Split(Path.DirectorySeparatorChar,
                             Path.AltDirectorySeparatorChar);
        // kurs / klasse / carN / tune / tag / datei
        if (rel.Length < 6) { return null; }

        // Die Kennung, nicht der Ordnername: der heisst inzwischen "Soni Circuit (course_…)".
        var kurs = LapArchive.KennungAus(rel[0]) ?? rel[0];
        var klasse = rel[1];
        // Ebenso beim Auto: "BMW 2002 Turbo '73 (car1269)" oder "car1269".
        if (LapArchive.AutoNummerAus(rel[2]) is not { } ordinal) { return null; }

        // <zeit>_<sekunden>s[_flags].json -- die Sekunden stehen zwischen dem
        // LETZTEN Unterstrich vor dem 's' und diesem 's'. Mit Punkt als
        // Dezimaltrennzeichen, unabhaengig von der Spracheinstellung: so legt
        // LapArchive.Save es ausdruecklich ab.
        var ohneEndung = name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? name[..^5] : name;
        var teile = ohneEndung.Split('_');
        double? sekunden = null;
        var stehend = false;
        var sprint = false;
        foreach (var t in teile)
        {
            if (t.Equals("standing", StringComparison.OrdinalIgnoreCase)) { stehend = true; }
            else if (t.Equals("sprint", StringComparison.OrdinalIgnoreCase)) { sprint = true; }
            else if (sekunden is null && t.EndsWith("s", StringComparison.Ordinal)
                     && double.TryParse(t[..^1], NumberStyles.Float,
                                        CultureInfo.InvariantCulture, out var s)
                     && s > 0)
            {
                sekunden = s;
            }
        }
        if (sekunden is null) { return null; }
        return new Lauf(kurs, klasse, ordinal, sekunden.Value, stehend, sprint);
    }
}
