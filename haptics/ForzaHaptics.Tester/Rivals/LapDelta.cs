using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>Woran sich das Delta misst.</summary>
internal enum DeltaReference
{
    /// <summary>Dieses Auto in genau dieser Abstimmung (ueber den PI erkannt).</summary>
    SameCarSameTune,
    /// <summary>Dieses Auto, gleich wie abgestimmt.</summary>
    SameCar,
    /// <summary>Jedes Auto derselben Leistungsklasse.</summary>
    SameClass,
    /// <summary>Dieses Auto, und nur in dieser Leistungsklasse.</summary>
    SameCarSameClass,
    // "Anything" -- die eigene Bestzeit mit was auch immer, ueber ALLE Klassen -- gibt
    // es seit 2026-09-27 nicht mehr: ein A-Klasse-Auto lief auf Narai gegen die
    // R-Klasse-Zeit eines Skyline, zwoelf Sekunden Abstand, die nichts sagen. Auf die
    // eigene Klasse beschraenkt waere es dasselbe wie SameClass.
}

/// <summary>Ein Messpunkt einer Runde: wie weit, und wie lange bis dahin.</summary>
/// <summary>
/// Ein Messpunkt der Runde: wie weit, wie lange -- und WO.
/// </summary>
/// <remarks>
/// Die Weltkoordinaten stehen hier, damit sich aus dem Bestand spaeter Karten
/// zeichnen lassen: wo gebremst wurde, wo Zeit verloren ging, wo die Linie anders
/// lief. Ohne sie ist eine Runde nur eine Zahlenreihe ohne Ort, und die Frage
/// "wo genau war ich langsamer" bleibt unbeantwortbar.
/// </remarks>
internal readonly record struct LapSample(
    float Metres, float Seconds,
    float X = 0f, float Y = 0f, float Z = 0f,
    float Speed = 0f, float Throttle = 0f, float Brake = 0f, float Steer = 0f,
    float LatG = 0f, float LongG = 0f, float Clutch = 0f, float HandBrake = 0f,
    float Gear = 0f, float Puddle = 0f);

/// <summary>Eine vollstaendig gefahrene Runde mit allem, was sie vergleichbar macht.</summary>
internal sealed class RecordedLap
{
    [JsonPropertyName("lapSeconds")] public float LapSeconds { get; set; }
    [JsonPropertyName("lengthMetres")] public float LengthMetres { get; set; }
    [JsonPropertyName("carOrdinal")] public int CarOrdinal { get; set; }
    [JsonPropertyName("performanceIndex")] public int PerformanceIndex { get; set; }
    [JsonPropertyName("carClass")] public int CarClass { get; set; }
    [JsonPropertyName("drivetrain")] public int Drivetrain { get; set; }
    [JsonPropertyName("cylinders")] public int Cylinders { get; set; }
    [JsonPropertyName("maxRpm")] public int MaxRpm { get; set; }
    [JsonPropertyName("idleRpm")] public int IdleRpm { get; set; }
    /// <summary>Hoechste waehrend der Runde gesehene Leistung, in kW.</summary>
    [JsonPropertyName("peakKilowatts")] public int PeakKilowatts { get; set; }
    [JsonPropertyName("track")] public string? Track { get; set; }

    /// <summary>Woher der Streckenname stammt.</summary>
    /// <remarks>
    /// "none" heisst: es gibt keinen. "signup+length" heisst: der Anmeldeschirm
    /// bot diese Strecke an, und die gefahrene Rundenlaenge passte zu GENAU EINER
    /// der angebotenen -- zwei Anhaltspunkte, nicht einer. "series-order+length"
    /// (seit 2026-09-26): der Schirm einer Reihe (Horizon Play) zeigte, welche
    /// Strecke dran war, und die Rundenlaenge passte zu ihr -- auch wenn eine andere
    /// angebotene Strecke fast gleich lang ist.
    ///
    /// Der Beleg steht daneben, weil ein Name ohne Herkunft spaeter nicht mehr zu
    /// bewerten ist. Dieses Projekt hat schon einmal geratene Autonamen als
    /// gesicherte behandelt und 36 belegte davon ueberschrieben.
    /// </remarks>
    [JsonPropertyName("trackEvidence")]
    public string TrackEvidence { get; set; } = "none";
    [JsonPropertyName("recordedAt")] public DateTimeOffset RecordedAt { get; set; }
    [JsonPropertyName("samples")] public List<LapSample> Samples { get; set; } = new();

    /// <summary>
    /// Welche Strecke das war -- ohne dass die Telemetrie es sagt.
    /// </summary>
    /// <remarks>
    /// Forza sendet keine Streckenkennung. Zwei Runden derselben Strecke sind aber
    /// praktisch gleich lang, waehrend verschiedene Strecken sich um viel mehr als
    /// zehn Meter unterscheiden. Auf zehn Meter gerundet ist die Laenge damit ein
    /// brauchbarer Fingerabdruck -- und wo der Bildschirmleser den Namen kennt, steht
    /// er zusaetzlich daneben.
    /// </remarks>
    /// <summary>
    /// Ob diese Runde aus dem STAND begann.
    /// </summary>
    /// <remarks>
    /// Der entscheidende Unterschied, und mir am 2026-09-12 um die Ohren geflogen:
    /// eine Runde von der Startaufstellung weg dauert mehrere Sekunden laenger als
    /// dieselbe Runde fliegend. Wer das eine gegen das andere haelt, bekommt ein
    /// Delta, das nichts ueber das Fahren aussagt -- der Nutzer sah, dass er laut
    /// Spiel schneller war, waehrend mein Streifen "langsamer" zeigte.
    ///
    /// Verglichen wird darum nur Gleiches mit Gleichem: stehend gegen stehend,
    /// fliegend gegen fliegend.
    /// </remarks>
    public bool StandingStart { get; set; }

    /// <summary>Wie schnell es losging, in m/s -- die Begruendung fuer das Obige.</summary>
    public float StartSpeed { get; set; }

    /// <summary>
    /// Die Fahrt wurde in der freien Welt gemessen, nicht vom Spiel gewertet.
    /// </summary>
    /// <remarks>
    /// Solche Fahrten werden von den gewerteten GETRENNT gehalten -- beim Massstab
    /// wie bei der Ablage. Nicht aus Ordnungsliebe, sondern weil sie nicht dasselbe
    /// messen: in der freien Welt gibt es keine Streckengrenzen, keine Strafe fuers
    /// Abkuerzen und kein Zuruecksetzen. Eine Zeit, die quer ueber eine Wiese
    /// entstanden ist, waere als Bestzeit einer Rivalen-Strecke keine Auskunft,
    /// sondern eine Taeuschung -- und sie wuerde jedes spaetere Delta dort
    /// entwerten, lautlos.
    ///
    /// Vergleichbar sind sie trotzdem, naemlich untereinander: dieselbe Linie,
    /// dieselbe Uhr, dieselben Regeln.
    /// </remarks>
    [JsonPropertyName("freeRoam")] public bool FreeRoam { get; set; }

    /// <summary>
    /// In welchem Spielmodus die Runde entstand -- zum spaeteren FILTERN.
    /// </summary>
    /// <remarks>
    /// Auf Wunsch des Nutzers am 2026-09-15: "einfach dass man spaeter evtl daten
    /// filtern kann". Ausdruecklich NICHT, um Modi gegeneinander zu rechnen -- eine
    /// Rivals-Runde gegen eine Koop-Runde zu stellen waere so falsch wie stehend
    /// gegen fliegend.
    ///
    /// Erlaubte Werte, klein geschrieben:
    ///
    ///   freeroam      selbst gestoppte Fahrt in der offenen Welt
    ///   rivals        Rivals-Bestenlistenlauf
    ///   solo          Einzelspieler-Rennen
    ///   coop          Koop
    ///   horizon-play  Playlist / Horizon Open
    ///   unknown       nicht feststellbar
    ///
    /// DIE TELEMETRIE SAGT DEN MODUS NICHT. Er wird darum erschlossen, und wie
    /// sicher das ist, steht in <see cref="ModeEvidence"/> -- eine Angabe ohne ihre
    /// Herkunft waere hier eine Behauptung. Was nicht feststeht, bleibt "unknown";
    /// geraten wird nicht.
    /// </remarks>
    [JsonPropertyName("mode")] public string Mode { get; set; } = "unknown";

    /// <summary>Woher <see cref="Mode"/> stammt.</summary>
    /// <remarks>
    /// "timer" heisst: die eigene Freiwelt-Uhr hat die Fahrt gestoppt, der Modus
    /// steht damit fest. "screen:12s" heisst: zwoelf Sekunden vor dem Rundenende
    /// wurde ein Schirm dieses Modus gelesen -- ein starkes Indiz, kein Beweis, denn
    /// zwischen Anmeldung und Fahrt kann man das Menue wieder verlassen. "none"
    /// heisst: nichts davon.
    /// </remarks>
    [JsonPropertyName("modeEvidence")] public string ModeEvidence { get; set; } = "none";

    /// <summary>
    /// Die volle Telemetrie dieser Runde -- steht NICHT in der Rundendatei.
    /// </summary>
    /// <remarks>
    /// Sie wird von <see cref="LapArchive.Save"/> in eine eigene, gepackte Datei
    /// daneben geschrieben (<c>.tele.gz</c>). In der Rundendatei haette sie nichts
    /// verloren: die wird von allem gelesen, was es hier gibt, und niemand soll
    /// Megabytes durch den Parser schieben, um an eine Rundenzeit zu kommen.
    /// </remarks>
    [JsonIgnore] public TelemetryTrack? FullTrack { get; set; }

    /// <summary>
    /// Die Fahrt endete am Ziel (oder mit dem Rennen), nicht an einer Rundenlinie.
    /// </summary>
    /// <remarks>
    /// Punkt-zu-Punkt-Rennen haben keinen Rundenwechsel: `LapNumber` bleibt die
    /// ganze Fahrt auf 0. GEMESSEN am 2026-09-12/13: in 312 protokollierten Sekunden
    /// dreier Sprints stand durchgehend "runde=0" -- und weil die Aufzeichnung nur
    /// beim Rundenwechsel ablegte, wurde KEINE davon gespeichert. Eine Fahrt ueber
    /// 6.579 m in 139 s war danach einfach weg.
    ///
    /// Solche Fahrten sind trotzdem verwertbar; sie unterscheiden sich aber von
    /// einer fliegenden Runde und werden darum wie stehende Starts getrennt
    /// gehalten. Ausserdem kann eine so beendete Fahrt ABGEBROCHEN sein (Pause,
    /// Rennen verlassen) -- die faellt dann kuerzer aus, bildet ueber ihre Laenge
    /// eine eigene "Strecke" und verdirbt keine echte.
    /// </remarks>
    public bool EndedAtFinish { get; set; }

    /// <summary>
    /// Der Anteil der Runde, auf dem mindestens ein Rad im Wasser stand (0 bis 1).
    /// </summary>
    /// <remarks>
    /// Ein WETTERFELD gibt es in der Telemetrie nicht -- ich habe alle 88 Felder des
    /// Pakets durchgesehen. `WheelInPuddle` je Rad ist das Naechste daran und misst,
    /// was fuers Fahren zaehlt: Wasser unter dem Reifen.
    ///
    /// Es ist KEIN Wettermesser. Eine Pfuetze nach dem Regen sieht aus wie Regen, und
    /// Schnee sieht nach gar nichts aus. Fuer das, was sich damit nicht trennen
    /// laesst, gibt es den frei waehlbaren Merkzettel.
    /// </remarks>
    public float WetFraction { get; set; }

    /// <summary>Wo die Runde begann -- Weltkoordinaten, die Start-Ziel-Linie.</summary>
    /// <remarks>
    /// Das ist die eigentliche Streckenkennung. Die Telemetrie nennt keine Strecke,
    /// aber sie nennt den Ort: wer zweimal dieselbe Runde faehrt, ueberquert zweimal
    /// dieselbe Linie, auf ein paar Meter genau. Rundenlaengen tun das NICHT -- am
    /// 2026-09-12 gemessen: 6271 m und 5935 m auf demselben Kurs, 5 % auseinander,
    /// allein aus der gefahrenen Linie.
    ///
    /// 0/0 heisst "unbekannt": Runden aus der Zeit vor dieser Aenderung haben keinen
    /// Startpunkt, und fuer die bleibt die Laenge das einzige Merkmal.
    /// </remarks>
    public float StartX { get; set; }
    public float StartZ { get; set; }

    /// <summary>
    /// Ob der Startpunkt dieser Fahrt bekannt ist.
    /// </summary>
    /// <remarks>
    /// Nicht nur "die Koordinate ist ungleich null": das verwechselt einen
    /// UNBEKANNTEN Start mit einem, der zufaellig im Ursprung liegt. Eine Fahrt mit
    /// Wegpunkten kennt ihren Start immer -- es ist ihr erster Punkt. Ohne diese
    /// Ergaenzung galten zwei Fahrten von (0,0) als startlos und landeten als zwei
    /// verschiedene Strassen im Bestand.
    ///
    /// Nur Runden aus der Zeit vor den Koordinaten haben wirklich keinen Start.
    /// </remarks>
    [JsonIgnore]
    public bool HasStart => StartX != 0f || StartZ != 0f || HasPositions;

    public int LengthKey => (int)MathF.Round(LengthMetres / 10f);

    /// <summary>
    /// Ob zwei Rundenlaengen dieselbe Strecke meinen koennen.
    /// </summary>
    /// <remarks>
    /// GEMESSEN am 2026-09-12: zwei Runden desselben Kurses hintereinander ergaben
    /// 6069 m und 5949 m -- 2 % Unterschied, allein aus der gefahrenen Linie, aus
    /// Abkuerzungen und Ausritten. Ein Schluessel auf 10 m genau haette die beiden nie
    /// miteinander verglichen, und der Streifen haette fuer immer "--.---" gezeigt.
    ///
    /// 4 % sind darum die Grenze, mindestens aber 40 m, damit kurze Strecken nicht an
    /// einem Prozentwert scheitern. Zwei VERSCHIEDENE Strecken gleicher Laenge fallen
    /// damit zusammen -- das ist der Preis dafuer, dass die Telemetrie keine
    /// Streckenkennung sendet, und die Alternative waere gar kein Vergleich.
    /// </remarks>
    public static bool SameCourse(float a, float b)
    {
        // Nur die Laenge, und darum weit gefasst: 8 % sind das, was die gefahrene
        // Linie auf denselben Kurs draufschlagen kann (gemessen: 5,3 %). Enger
        // gefasst faende der Streifen nie eine Referenz -- und lieber ein Vergleich
        // mit einem aehnlich langen Kurs als gar keiner. Wo ein Startpunkt vorliegt,
        // entscheidet ohnehin der (siehe die Ueberladung unten).
        var spielraum = Math.Max(60f, 0.08f * Math.Max(a, b));
        return Math.Abs(a - b) <= spielraum;
    }

    /// <summary>Wie weit zwei Startpunkte auseinander liegen duerfen.</summary>
    /// <remarks>
    /// 120 m: die Startaufstellung eines Rennens ist breit und tief, und wer in Runde
    /// 2 ueber die Linie faehrt, tut das nicht auf demselben Zentimeter. Zwei
    /// verschiedene Strecken starten dagegen selten im selben Block -- und wenn doch,
    /// trennt sie die Laenge, die hier zusaetzlich geprueft wird.
    /// </remarks>
    public const float StartTolerance = 120f;

    /// <summary>
    /// Wie weit seitlich von der Strassenmitte noch dieselbe Strasse ist.
    /// </summary>
    /// <remarks>
    /// 50 m. Breiter als jede Fahrbahn samt Randstreifen, schmaler als der Abstand
    /// zur naechsten Strasse -- und weit genug, dass eine andere Linie, ein Ausritt
    /// ins Kiesbett oder ein Ueberholmanoever noch dazugehoert.
    /// </remarks>
    public const float CorridorMetres = 50f;

    /// <summary>
    /// Erreicht diese Fahrt den Ort, an dem die andere endete?
    /// </summary>
    /// <remarks>
    /// Das ist die Frage, die "wer ist weiter gekommen" beantwortet, OHNE Laengen zu
    /// vergleichen. Laengen sind ein Abbild der Strecke und kein Mass fuer sie: eine
    /// engere Linie ist kuerzer und trotzdem vollstaendig, ein Abbruch ist kuerzer
    /// und unvollstaendig, und beides sieht in Metern gleich aus. Am 2026-09-13 hat
    /// genau diese Verwechslung einen Abbruch 522 m vor dem Ziel zur "Bestzeit"
    /// gemacht und die vollstaendige Fahrt verdraengt.
    ///
    /// Gefragt wird stattdessen am ORT: liegt das Ziel der anderen Fahrt auf meinem
    /// Weg? Dann bin ich mindestens so weit gekommen.
    /// </remarks>
    public bool ReachesEndOf(RecordedLap other)
    {
        if (!HasPositions || !other.HasPositions || other.Samples.Count == 0)
        {
            return false;
        }
        var ziel = other.Samples[^1];
        var hinweis = 0;
        var zeit = SecondsAtPosition(ziel.X, ziel.Y, ziel.Z, ref hinweis, out var seitlich);
        return zeit is not null && seitlich <= CorridorMetres;
    }

    /// <summary>
    /// Fahren beide auf derselben Strasse? Gemessen am Weg, nicht an der Laenge.
    /// </summary>
    /// <remarks>
    /// Dieselbe Startlinie, und die kuerzere Fahrt liegt ueber ihre ganze Laenge im
    /// Korridor der laengeren. Ein Abbruch erfuellt das ebenso wie eine
    /// vollstaendige Fahrt -- er ist dieselbe STRASSE, nur nicht dieselbe Strecke.
    /// Wer weiter kam, beantwortet <see cref="ReachesEndOf"/>.
    /// </remarks>
    public static bool SameRoad(RecordedLap a, RecordedLap b)
    {
        if (!StartsTogether(a, b)) { return false; }
        if (!a.HasPositions || !b.HasPositions) { return false; }

        var kurz = a.Samples.Count <= b.Samples.Count ? a : b;
        var lang = ReferenceEquals(kurz, a) ? b : a;
        if (kurz.Samples.Count < 3) { return false; }

        // Stichproben genuegen: 20 Punkte ueber die kuerzere Fahrt verteilt. Jeder
        // muss im Korridor der laengeren liegen.
        var schritt = Math.Max(1, kurz.Samples.Count / 20);
        var hinweis = 0;
        var drin = 0;
        var geprueft = 0;
        for (var i = 0; i < kurz.Samples.Count; i += schritt)
        {
            var punkt = kurz.Samples[i];
            geprueft++;
            lang.SecondsAtPosition(punkt.X, punkt.Y, punkt.Z, ref hinweis, out var seitlich);
            if (seitlich <= CorridorMetres) { drin++; }
        }
        // Ein einzelner Ausreisser (Ruecksetzer, Abkuerzung) kippt das Urteil nicht.
        return geprueft > 0 && drin >= geprueft * 0.9;
    }

    /// <summary>
    /// Wo die Strecke endet -- bestimmt aus den Fahrten, nicht aus einer Schwelle.
    /// </summary>
    /// <remarks>
    /// DIE IDEE, und sie kommt vom Nutzer: vollstaendige Fahrten hoeren am SELBEN
    /// ORT auf, naemlich am Ziel. Abbrueche hoeren irgendwo auf. Also ist das Ziel
    /// dort, wo sich die Endpunkte haeufen -- das laesst sich messen, statt es mit
    /// Laengenbaendern zu umgehen.
    ///
    /// Bis dahin habe ich eine Nacht lang Schwellwerte gestapelt: 12 % Laengenband,
    /// dann 4 %, dann 150 m Nachlauftoleranz. Jeder Wert war geraten, und jeder hat
    /// eine neue Panne erzeugt -- zuletzt verdraengte eine um 1,07 s langsamere
    /// Fahrt die Bestzeit, weil das Spiel sie nach dem Ziel 79 m laenger gemeldet
    /// hatte.
    ///
    /// Das einzige Mass hier ist RELATIV (5 % der weitesten Fahrt) und skaliert
    /// damit von selbst: auf 6,4 km sind das 320 m, deutlich mehr als jeder
    /// gemessene Nachlauf (77-80 m) und deutlich weniger als jeder Abbruch.
    /// Genommen wird dann der Median der Gruppe -- ein einzelner langer Nachlauf
    /// verschiebt das Ziel damit nicht.
    /// </remarks>
    public static float FinishArc(RecordedLap route, IReadOnlyList<RecordedLap> fahrten)
    {
        var enden = new List<float>();
        foreach (var fahrt in fahrten)
        {
            var arc = ReferenceEquals(fahrt, route)
                ? route.RouteLength
                : fahrt.ArcReachedOn(route);
            if (arc > 0) { enden.Add(arc); }
        }
        if (enden.Count == 0) { return route.RouteLength; }

        // Die Gruppe der Fahrten, die bis ans Ende gekommen sind -- 5 % Spielraum,
        // relativ und damit unabhaengig von der Streckenlaenge.
        var weiteste = enden.Max();
        var gruppe = enden.Where(e => e >= weiteste * 0.95f).ToList();

        // Und daraus der KLEINSTE Wert, nicht der Median.
        //
        // Das Ziel muss fuer jede Fahrt der Gruppe erreichbar sein: wer es um einen
        // Meter verfehlt, hat dort keine Zeit und faellt aus dem Vergleich. Genau so
        // ist beim ersten Anlauf die schnellere Runde herausgefallen und die
        // langsamere zum Massstab geworden. Der kleinste Endpunkt der Gruppe ist der
        // letzte Ort, an dem ALLE noch fuhren -- und genau dort gehoert verglichen.
        return gruppe.Min();
    }

    /// <summary>Ob beide Runden an derselben Linie begonnen haben.</summary>
    /// <summary>Wo diese Fahrt begann -- notfalls ihr erster Wegpunkt.</summary>
    private (float X, float Z) Anfang()
    {
        if (StartX != 0f || StartZ != 0f) { return (StartX, StartZ); }
        if (Samples.Count > 0) { return (Samples[0].X, Samples[0].Z); }
        return (0f, 0f);
    }

    public static bool StartsTogether(RecordedLap a, RecordedLap b)
    {
        if (!a.HasStart || !b.HasStart) { return false; }
        var (ax, az) = a.Anfang();
        var (bx, bz) = b.Anfang();
        var dx = ax - bx;
        var dz = az - bz;
        return MathF.Sqrt(dx * dx + dz * dz) <= StartTolerance;
    }

    /// <summary>
    /// Zwei Laengen derselben Strecke -- eng gefasst, und das aus Messung.
    /// </summary>
    /// <remarks>
    /// Hier standen 12 %. Die stammten aus einer Zeit, als die Laenge aus
    /// `DistanceTraveled` kam und auf derselben Strecke um 5 % schwankte. Seit die
    /// Strecke aus dem gefahrenen Weg gerechnet wird, ist sie stabil: zwei Fahrten
    /// bis zum selben Punkt lagen am 2026-09-13 nur 0,6 % auseinander (4.032 und
    /// 4.057 m), eine andere Linie kostet kaum Weg.
    ///
    /// 12 % waren damit viel zu weit -- ein Abbruch 522 m vor dem Ziel (5.909 statt
    /// 6.474 m, 8,7 %) galt als dieselbe Fahrt und verdraengte als vermeintlich
    /// schnellere die vollstaendige Bestzeit. Pro Meter war er sogar langsamer:
    /// 15,1 gegen 14,9 ms. Gesamtzeiten verschieden langer Fahrten zu vergleichen
    /// ist sinnlos, und 4 % sorgen dafuer, dass es gar nicht erst dazu kommt.
    /// </remarks>
    public static bool LengthsPlausible(float a, float b) =>
        Math.Abs(a - b) <= Math.Max(60f, 0.04f * Math.Max(a, b));

    /// <summary>Ob zwei Runden auf derselben Strecke gefahren wurden.</summary>
    public static bool SameCourse(RecordedLap a, RecordedLap b)
    {
        if (a.HasStart && b.HasStart)
        {
            if (!StartsTogether(a, b)) { return false; }
            // Gleiche Linie, aber andere Laenge: von derselben Stelle gehen mehrere
            // Strecken los. 12 % trennen die, ohne die eigene Linie zu bestrafen.
            return LengthsPlausible(a.LengthMetres, b.LengthMetres);
        }
        // Mindestens eine Runde stammt aus der Zeit ohne Startpunkt.
        return SameCourse(a.LengthMetres, b.LengthMetres);
    }

    /// <summary>
    /// Der Fingerabdruck der Abstimmung -- so weit die Telemetrie sie verraet.
    /// </summary>
    /// <remarks>
    /// Ein Tune-Name steht in keinem Feld. Was drinsteht, sind die Kennwerte, die ein
    /// Umbau VERAENDERT: der Leistungsindex, der Antrieb (Umbau auf Allrad), die
    /// Zylinderzahl (Motortausch), und die Drehzahlgrenze samt Leerlauf (Motor und
    /// Tuning). Zusammen trennen die ein aufgebautes Auto zuverlaessig von seinem
    /// Serienzustand.
    ///
    /// `Power` und `Torque` stehen bewusst NICHT drin, obwohl sie verlockend aussehen:
    /// beide sind Momentanwerte, keine Fahrzeugkennwerte. Sie schwanken mit jedem
    /// Paket, und die hoechste gesehene Leistung haengt davon ab, ob man im Rennen je
    /// Vollgas im oberen Gang gefahren ist. Als Schluessel waere das unzuverlaessig --
    /// dieselbe Abstimmung bekaeme je nach Runde verschiedene Schluessel. Aufgezeichnet
    /// wird die Spitzenleistung trotzdem, als Auskunft, nicht als Kennung.
    ///
    /// Die Drehzahlen werden auf 10 gerundet: das Spiel liefert Gleitkomma, und zwei
    /// Runden desselben Autos duerfen nicht an der letzten Nachkommastelle scheitern.
    /// </remarks>
    public string TuneKey =>
        $"{CarOrdinal}/{PerformanceIndex}/{Drivetrain}/{Cylinders}/"
        + $"{MaxRpm / 10}/{IdleRpm / 10}";

    /// <summary>Die Zeit, zu der diese Runde bei <paramref name="metres"/> stand.</summary>
    /// <remarks>
    /// Zwischen den Messpunkten linear interpoliert. Der Abstand betraegt
    /// <see cref="LapRecorder.SampleMetres"/>, bei ueblichen Geschwindigkeiten also
    /// deutlich weniger als eine Zehntelsekunde -- der Interpolationsfehler liegt
    /// unter der Anzeigegenauigkeit.
    /// </remarks>
    [JsonIgnore]
    private float[]? _bogen;

    /// <summary>
    /// Die Route dieser Fahrt als Bogenlaengen -- wie weit man bis zu jedem
    /// Messpunkt gefahren ist.
    /// </summary>
    /// <remarks>
    /// Damit wird aus einer Punktwolke eine STRECKE: jeder Ort auf ihr hat eine
    /// Position "wie weit hinein", und jede andere Fahrt laesst sich darauf abbilden.
    /// Ohne das bleibt nur der Vergleich von Gesamtlaengen -- und der hat in der
    /// Nacht zum 2026-09-13 dreimal die falsche Fahrt zur Bestzeit gemacht.
    /// </remarks>
    [JsonIgnore]
    public float[] ArcLengths
    {
        get
        {
            if (_bogen is not null && _bogen.Length == Samples.Count) { return _bogen; }
            var raus = new float[Samples.Count];
            for (var i = 1; i < Samples.Count; i++)
            {
                var a = Samples[i - 1];
                var b = Samples[i];
                var dx = b.X - a.X;
                var dy = b.Y - a.Y;
                var dz = b.Z - a.Z;
                raus[i] = raus[i - 1] + MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            }
            _bogen = raus;
            return raus;
        }
    }

    /// <summary>Die gesamte Routenlaenge dieser Fahrt.</summary>
    [JsonIgnore]
    public float RouteLength => Samples.Count < 2 ? 0f : ArcLengths[^1];

    /// <summary>Der Ort auf dieser Route nach so vielen Metern.</summary>
    public LapSample PositionAtArc(float arc)
    {
        if (Samples.Count == 0) { return default; }
        var bogen = ArcLengths;
        if (arc <= 0) { return Samples[0]; }
        if (arc >= bogen[^1]) { return Samples[^1]; }
        var tief = 0;
        var hoch = Samples.Count - 1;
        while (hoch - tief > 1)
        {
            var mitte = (tief + hoch) / 2;
            if (bogen[mitte] <= arc) { tief = mitte; } else { hoch = mitte; }
        }
        var spanne = bogen[hoch] - bogen[tief];
        var t = spanne <= 0.0001f ? 0f : (arc - bogen[tief]) / spanne;
        return Mischen(Samples[tief], Samples[hoch], t);
    }

    /// <summary>
    /// Wie weit diese Fahrt auf der Route einer anderen gekommen ist.
    /// </summary>
    /// <remarks>
    /// Der eigene Endpunkt, auf die fremde Route gelotet. Das ist die Antwort auf
    /// "wie weit bin ich gekommen", die NICHT von der eigenen Aufzeichnungslaenge
    /// abhaengt -- und damit die einzige, die zwischen einem Abbruch und einem
    /// laengeren Nachlauf hinter dem Ziel unterscheidet.
    /// </remarks>
    public float ArcReachedOn(RecordedLap route) => MapOnto(route).Weiteste;

    /// <summary>Die eigene Zeit an einer Stelle der fremden Route.</summary>
    /// <remarks>
    /// NICHT ueber den blossen Ort: auf einem RUNDKURS ist das Ziel derselbe Ort wie
    /// der Start, und eine Ortssuche liefert dort die Zeit 0 statt der Rundenzeit.
    /// Genau daran ist die erste Fassung gescheitert -- sie erklaerte die langsamere
    /// Runde zum Massstab.
    ///
    /// Gefragt ist der FORTSCHRITT entlang der Route, und der ist eindeutig: die
    /// eigenen Messpunkte werden der Reihe nach auf die Route abgebildet, und
    /// zwischen den beiden, die die gesuchte Stelle einschliessen, wird die Zeit
    /// geteilt.
    /// </remarks>
    public float? SecondsAtArcOf(RecordedLap route, float arc) =>
        MapOnto(route).ZeitBei(arc);

    /// <summary>
    /// Die eigene Fahrt auf eine fremde Route abgebildet: (Fortschritt, Zeit).
    /// </summary>
    /// <remarks>
    /// Vorwaerts, mit Gedaechtnis: die Suche beginnt dort, wo der vorige Punkt lag.
    /// Ohne das springt die Zuordnung an einer Kreuzung oder auf einem Rundkurs
    /// beliebig zwischen Anfang und Ende.
    /// </remarks>
    private ArcMap MapOnto(RecordedLap route)
    {
        var bogen = route.ArcLengths;
        var stellen = new List<(float Arc, float Zeit)>(Samples.Count);
        var hinweis = 0;
        var weiteste = 0f;
        foreach (var punkt in Samples)
        {
            route.SecondsAtPosition(punkt.X, punkt.Y, punkt.Z, ref hinweis,
                                    out var seitlich);
            if (seitlich > CorridorMetres) { continue; }
            var arc = bogen[Math.Min(hinweis, bogen.Length - 1)];
            // Nur vorwaerts: ein Ruecksprung waere eine Fehlzuordnung.
            if (stellen.Count > 0 && arc <= stellen[^1].Arc) { continue; }
            stellen.Add((arc, punkt.Seconds));
            if (arc > weiteste) { weiteste = arc; }
        }
        return new ArcMap(stellen, weiteste);
    }

    private readonly record struct ArcMap(
        List<(float Arc, float Zeit)> Stellen, float Weiteste)
    {
        public float? ZeitBei(float arc)
        {
            if (Stellen.Count < 2 || arc > Weiteste) { return null; }
            for (var i = 1; i < Stellen.Count; i++)
            {
                if (Stellen[i].Arc < arc) { continue; }
                var a = Stellen[i - 1];
                var b = Stellen[i];
                var spanne = b.Arc - a.Arc;
                var t = spanne <= 0.0001f ? 0f : (arc - a.Arc) / spanne;
                return a.Zeit + (b.Zeit - a.Zeit) * t;
            }
            return Stellen[^1].Zeit;
        }
    }

    [JsonIgnore]
    private bool? _hasPositions;

    /// <summary>Ob diese Runde Weltkoordinaten traegt.</summary>
    [JsonIgnore]
    public bool HasPositions
    {
        get
        {
            _hasPositions ??= Samples.Count > 1
                && Samples.Any(s => s.X != 0f || s.Z != 0f);
            return _hasPositions.Value;
        }
    }

    /// <summary>
    /// Die Zeit dieser Runde AN EINEM ORT -- nicht nach einer Anzahl Meter.
    /// </summary>
    /// <param name="hint">
    /// Der zuletzt getroffene Abschnitt. Wird fortgeschrieben, damit die Suche in der
    /// Naehe bleibt: eine Strecke kreuzt sich selbst, und ohne Gedaechtnis springt der
    /// Vergleich an einer Bruecke auf die Strasse darunter.
    /// </param>
    /// <param name="offLine">Wie weit der Ort von der Linie der Runde weg liegt.</param>
    /// <remarks>
    /// WARUM NICHT NACH METERN: die zurueckgelegte Strecke ist keine Ortsangabe. Wer
    /// ueber eine Klippe fliegt, abkuerzt oder ausritt, hat nach 3000 gefahrenen
    /// Metern einen voellig anderen Punkt erreicht als die Bestzeit nach ihren 3000 --
    /// und das Delta vergleicht dann zwei Stellen, die nichts miteinander zu tun
    /// haben. Der Ort ist die Frage, die Strecke war nur eine bequeme Naeherung.
    ///
    /// Gerechnet wird gegen die STRECKENABSCHNITTE, nicht gegen die Messpunkte: der
    /// Ort wird auf die Verbindung zweier Punkte gelotet und die Zeit dazwischen
    /// linear geteilt. Sonst waere die Aufloesung der Punktabstand (5 m).
    /// </remarks>
    public float? SecondsAtPosition(float x, float y, float z, ref int hint,
                                    out float offLine) =>
        SecondsAtPosition(x, y, z, ref hint, out offLine, out _, out _);

    public float? SecondsAtPosition(float x, float y, float z, ref int hint,
                                    out float offLine, out LapSample at) =>
        SecondsAtPosition(x, y, z, ref hint, out offLine, out at, out _);

    /// <summary>
    /// Wie oben, gibt zusaetzlich den Messpunkt an dieser Stelle heraus.
    /// </summary>
    /// <remarks>
    /// Damit sich nicht nur die ZEIT der Bestzeit vergleichen laesst, sondern auch,
    /// was dort am Lenkrad und an den Pedalen passiert ist. Zwischen zwei Punkten
    /// wird geteilt, wie bei der Zeit -- sonst spraenge die Anzeige alle 5 m.
    /// </remarks>
    public float? SecondsAtPosition(float x, float y, float z, ref int hint,
                                    out float offLine, out LapSample at,
                                    out bool pastEnd)
    {
        offLine = float.MaxValue;
        at = default;
        pastEnd = false;
        if (!HasPositions || Samples.Count < 2) { return null; }

        var treffer = Suche(x, y, z, Math.Max(0, hint - 30),
                            Math.Min(Samples.Count - 2, hint + 250));
        // Zu weit weg? Dann stimmt die Vermutung nicht mehr -- einmal alles
        // absuchen. Das kostet, passiert aber nur nach einem Ausritt oder Ruecksetzen.
        if (treffer.Abstand > 25f)
        {
            var weit = Suche(x, y, z, 0, Samples.Count - 2);
            if (weit.Abstand < treffer.Abstand) { treffer = weit; }
        }

        hint = treffer.Index;
        offLine = treffer.Abstand;
        at = Mischen(Samples[treffer.Index],
                     Samples[Math.Min(Samples.Count - 1, treffer.Index + 1)],
                     treffer.Anteil);

        // AM ENDE ANGEKOMMEN -- gemeldet, nicht verschwiegen.
        //
        // GEMESSEN am 2026-09-13: die eigene Fahrt rollte 71 m weiter aus als die
        // Referenz. Ab deren letztem Messpunkt blieb die Suche auf Segment 1211
        // stehen, die Referenzzeit auf 95,557 s -- und das Delta zaehlte von da an
        // nur noch die eigene Uhr hoch: von -705 ms glatt auf +121 ms ueber 70 m.
        //
        // Die Zeit wird trotzdem zurueckgegeben: "ich stehe am Ziel dieser Fahrt"
        // ist eine gueltige Auskunft und wird gebraucht, um zu entscheiden, wer
        // weiter gekommen ist. Ob daraus noch ein Delta werden darf, entscheidet
        // der Streifen anhand dieser Flagge -- und wie WEIT hinter dem Ende man
        // ist, sagt ohnehin `offLine`.
        pastEnd = treffer.Index >= Samples.Count - 2 && treffer.Anteil >= 0.999f;
        return treffer.Sekunden;
    }

    private static LapSample Mischen(LapSample a, LapSample b, float t) => new(
        a.Metres + (b.Metres - a.Metres) * t,
        a.Seconds + (b.Seconds - a.Seconds) * t,
        a.X + (b.X - a.X) * t,
        a.Y + (b.Y - a.Y) * t,
        a.Z + (b.Z - a.Z) * t,
        a.Speed + (b.Speed - a.Speed) * t,
        a.Throttle + (b.Throttle - a.Throttle) * t,
        a.Brake + (b.Brake - a.Brake) * t,
        a.Steer + (b.Steer - a.Steer) * t,
        a.LatG + (b.LatG - a.LatG) * t,
        a.LongG + (b.LongG - a.LongG) * t,
        a.Clutch + (b.Clutch - a.Clutch) * t,
        a.HandBrake + (b.HandBrake - a.HandBrake) * t,
        // Der Gang wird NICHT geteilt: zwischen dem dritten und dem vierten gibt es
        // keinen dreieinhalbten. Bis zum Schaltpunkt gilt der alte.
        t < 1f ? a.Gear : b.Gear,
        a.Puddle + (b.Puddle - a.Puddle) * t);

    private (int Index, float Abstand, float Sekunden, float Anteil) Suche(
        float x, float y, float z, int von, int bis)
    {
        var besterIndex = von;
        var besterAbstand = float.MaxValue;
        var besteZeit = 0f;
        var besterAnteil = 0f;
        for (var i = von; i <= bis; i++)
        {
            var a = Samples[i];
            var b = Samples[i + 1];
            var abx = b.X - a.X;
            var aby = b.Y - a.Y;
            var abz = b.Z - a.Z;
            var laenge = abx * abx + aby * aby + abz * abz;
            var t = laenge <= 0.000001f
                ? 0f
                : Math.Clamp(((x - a.X) * abx + (y - a.Y) * aby + (z - a.Z) * abz)
                             / laenge, 0f, 1f);
            var px = a.X + abx * t;
            var py = a.Y + aby * t;
            var pz = a.Z + abz * t;
            var dx = x - px;
            var dy = y - py;
            var dz = z - pz;
            var abstand = dx * dx + dy * dy + dz * dz;
            if (abstand >= besterAbstand) { continue; }
            besterAbstand = abstand;
            besterIndex = i;
            besteZeit = a.Seconds + (b.Seconds - a.Seconds) * t;
            besterAnteil = t;
        }
        return (besterIndex, MathF.Sqrt(besterAbstand), besteZeit, besterAnteil);
    }

    /// <summary>
    /// Der Messpunkt dieser Runde zu einem Zeitpunkt -- auch VOR mir liegend.
    /// </summary>
    /// <remarks>
    /// Dafuer, dass sich die Eingaben der Bestzeit ein, zwei Sekunden im VORAUS
    /// ablesen lassen: wer sehen will, wo die Bestzeit gleich bremst, braucht ihre
    /// Zukunft, nicht ihre Vergangenheit. Ueber die Zeit und nicht ueber den Ort,
    /// weil "gleich" eine Zeitangabe ist.
    ///
    /// Null ausserhalb der Runde -- vor dem Start und nach dem Ziel gibt es nichts
    /// zu zeigen, und eine fortgeschriebene letzte Zeile waere eine Erfindung.
    /// </remarks>
    public LapSample? SampleAtSeconds(float seconds)
    {
        if (Samples.Count < 2) { return null; }
        if (seconds < Samples[0].Seconds || seconds > Samples[^1].Seconds) { return null; }

        var tief = 0;
        var hoch = Samples.Count - 1;
        while (hoch - tief > 1)
        {
            var mitte = (tief + hoch) / 2;
            if (Samples[mitte].Seconds <= seconds) { tief = mitte; } else { hoch = mitte; }
        }
        var a = Samples[tief];
        var b = Samples[hoch];
        var spanne = b.Seconds - a.Seconds;
        var t = spanne <= 0.000001f ? 0f : (seconds - a.Seconds) / spanne;
        return Mischen(a, b, t);
    }

    public float? SecondsAt(float metres)
    {
        if (Samples.Count == 0 || metres < 0) { return null; }
        if (metres <= Samples[0].Metres) { return Samples[0].Seconds; }
        if (metres >= Samples[^1].Metres) { return null; }

        var low = 0;
        var high = Samples.Count - 1;
        while (high - low > 1)
        {
            var mid = (low + high) / 2;
            if (Samples[mid].Metres <= metres) { low = mid; } else { high = mid; }
        }
        var a = Samples[low];
        var b = Samples[high];
        var span = b.Metres - a.Metres;
        if (span <= 0) { return a.Seconds; }
        var t = (metres - a.Metres) / span;
        return a.Seconds + (b.Seconds - a.Seconds) * t;
    }
}

/// <summary>
/// Nimmt Runden aus dem Telemetriestrom auf.
/// </summary>
/// <remarks>
/// WARUM UEBER DIE STRECKE UND NICHT UEBER CHECKPOINTS: Forza sendet keine
/// Checkpoints. Es sendet `DistanceTraveled`, und daraus laesst sich mehr machen als
/// aus festen Punkten -- der Vergleich "wo stand die Bestzeit nach genau so vielen
/// Metern" ergibt sowohl das Delta an jedem gewuenschten Zwischenpunkt als auch ein
/// fortlaufendes Live-Delta. Feste Checkpoints waeren eine Teilmenge davon.
///
/// Eine Runde beginnt, wenn `LapNumber` sich aendert, und sie zaehlt nur, wenn sie
/// vollstaendig war: wer mittendrin einsteigt, hat keine Nullmarke, und eine Runde
/// ohne Nullmarke waere als Vergleich wertlos.
/// </remarks>
internal sealed class LapRecorder
{
    /// <summary>Abstand der Messpunkte. 5 m sind bei 300 km/h rund 60 ms.</summary>
    public const float SampleMetres = 5f;

    private readonly List<LapSample> _samples = new();

    /// <summary>
    /// Zaehlt jedes Mal hoch, wenn die Aufzeichnung von vorn beginnt: neue Runde,
    /// Start ("GO" setzt die Uhr zurueck), neues Rennen, Abbruch.
    /// </summary>
    /// <remarks>
    /// Seit 2026-09-25, fuer die Live-Karte. Sie sah bis dahin nur "abgeschlossene
    /// Runde" -- und zog darum beim stehenden Start eine Linie vom Ort vor dem
    /// Rennen zur Startlinie, und blieb in Rennen 2 und 3 einer Meisterschaft auf der
    /// Strecke von Rennen 1 stehen.
    /// </remarks>
    public int LapGeneration { get; private set; }

    private void NeuBeginnen()
    {
        _samples.Clear();
        LapGeneration++;
    }
    private int _lapNumber = -1;
    private float _lapStartTime;
    private float _lastLapTime;
    private float _lastX, _lastY, _lastZ;
    private bool _hasLast;
    private int _ordinal, _pi, _class, _drivetrain, _cylinders, _maxRpm, _idleRpm;
    private float _nextSampleAt;

    /// <summary>
    /// Die volle Telemetrie dieser Runde, ein Datensatz je Paket.
    /// </summary>
    /// <remarks>
    /// Laeuft NEBEN <c>_samples</c>, nicht statt ihnen: die Messpunkte stehen alle
    /// fuenf Meter und dienen dem Vergleich zweier Runden am selben ORT, die volle
    /// Spur haengt an der Uhr und dient dem Nachsimulieren. Siehe
    /// <see cref="TelemetryTrack"/>.
    /// </remarks>
    private readonly TelemetryTrack _vollspur = new();

    /// <summary>Die volle Spur der zuletzt fertiggestellten Runde.</summary>
    public TelemetryTrack FullTrack => _vollspur;
    private bool _armed;
    private bool _startGesehen;
    private float _peakWatts;
    private float _lapStartSpeed;

    // ---- freies Fahren -------------------------------------------------- //
    private FreeRoamTimer? _linien;
    /// <summary>Unsere eigene Uhr, in Sekunden seit dem Beginn der Sitzung.</summary>
    private float _eigeneUhr;
    private uint _letzterStempel;
    private bool _hatStempel;
    private bool _freiesFahren;
    private float _meterOhneUhr;
    private FreeRoamAnchor? _freiAnker;
    private float _freiStartSekunden;
    private float _freiRichtungX, _freiRichtungZ;

    /// <summary>
    /// Ab so vielen Metern bei stehender Spieluhr ist es freies Fahren.
    /// </summary>
    /// <remarks>
    /// `IsRaceOn` taugt nicht: in Horizon steht es auch beim freien Fahren auf 1.
    /// Die stehende Rundenuhr ist das Merkmal -- aber sie steht auch in den ersten
    /// Augenblicken eines Rennens, solange der Countdown laeuft. 150 m sind mehr,
    /// als man vor dem "GO" rollt, und weniger als jede Anfahrt.
    /// </remarks>
    private const float FreiAbMetern = 150f;

    /// <summary>Die laufende Runde: wie weit sie ist und wie lange sie schon dauert.</summary>
    public float CurrentMetres { get; private set; }
    public float CurrentSeconds { get; private set; }
    public bool HasLapUnderway => _armed;

    /// <summary>
    /// Die Laenge der zuletzt in dieser Sitzung beendeten Runde -- 0, wenn noch keine.
    /// </summary>
    /// <remarks>
    /// Das ist der einzige belastbare Anhaltspunkt dafuer, WIE LANG die Strecke ist,
    /// auf der gerade gefahren wird: die laufende Runde weiss es erst, wenn sie vorbei
    /// ist. Ohne ihn muss die Referenzsuche raten, und eine 20-km-Runde sieht in den
    /// ersten Kilometern genauso aus wie eine 6-km-Runde.
    /// </remarks>
    public float LastLapMetres { get; private set; }

    /// <summary>Wo die laufende Runde begann -- 0/0, solange keine laeuft.</summary>
    public float StartX { get; private set; }
    public float StartZ { get; private set; }

    /// <summary>Ob die laufende Runde aus dem Stand begann.</summary>
    public bool StandingStart { get; private set; }

    /// <summary>Die Start-Ziel-Linien, an denen in der freien Welt gemessen wird.</summary>
    public FreeRoamTimer Lines
    {
        get => _linien ??= FreeRoamTimer.Load();
        set => _linien = value;
    }

    /// <summary>Es wird gefahren, ohne dass das Spiel die Zeit nimmt.</summary>
    public bool InFreeRoam => _freiesFahren;

    /// <summary>Die Linie, an der die laufende Zeitjagd begann -- oder null.</summary>
    public FreeRoamAnchor? FreeRoamCourse => _freiAnker;

    /// <summary>
    /// Hier eine eigene Start-Ziel-Linie setzen.
    /// </summary>
    /// <remarks>
    /// Fuer alles, was noch nie als Rennen gefahren wurde: eine Landstrasse, eine
    /// Bergstrecke, ein eigener Rundkurs. Die Linien aus dem Rundenbestand kommen
    /// von selbst dazu und muessen nicht gesetzt werden.
    /// </remarks>
    public FreeRoamAnchor? DropLineHere(string name)
    {
        if (!_hasLast) { return null; }
        var neu = Lines.Add(new FreeRoamAnchor
        {
            Name = name, X = _lastX, Z = _lastZ, Source = "manual",
        });
        Lines.SaveLines();
        return neu;
    }

    /// <summary>Faellt an, sobald eine Runde vollstaendig gefahren wurde.</summary>
    public event EventHandler<RecordedLap>? LapCompleted;

    /// <summary>
    /// Die laufende Fahrt abschliessen, weil das Rennen endet.
    /// </summary>
    /// <remarks>
    /// Die Fahrzeugdaten kommen aus gemerkten Werten und NICHT aus dem letzten
    /// Paket: sobald `IsRaceOn` auf 0 faellt, sind die uebrigen Felder des Pakets
    /// nicht mehr verlaesslich befuellt. Ein Eintrag mit Auto 0 und PI 0 waere im
    /// Bestand wertlos.
    ///
    /// Die Schwellen (300 m, 15 s) trennen eine gefahrene Strecke von einem
    /// Fehlstart oder einem sofortigen Verlassen. Sie sind bewusst niedrig: lieber
    /// eine kurze Fahrt im Bestand, die sich ueber ihre Laenge selbst einordnet,
    /// als eine verlorene lange.
    /// </remarks>
    private void FinishOpenRun()
    {
        if (!_armed || !_startGesehen || _samples.Count < 3) { return; }
        // EINE FREIE FAHRT HAT KEIN ZIEL.
        //
        // Sie endet, wo der Fahrer aufhoert -- an keiner Linie. Ihre Zeit misst
        // darum nichts, und sie waere als "Fahrt bis zum Ziel" abgelegt schlicht
        // falsch. Gewertet wird in der freien Welt nur, was zwischen zwei
        // Ueberfahrten derselben Linie liegt.
        if (_freiesFahren) { return; }
        if (CurrentMetres < 300f || CurrentSeconds < 15f) { return; }

        var fertig = new RecordedLap
        {
            LapSeconds = CurrentSeconds,
            LengthMetres = CurrentMetres,
            CarOrdinal = _ordinal,
            PerformanceIndex = _pi,
            CarClass = _class,
            Drivetrain = _drivetrain,
            Cylinders = _cylinders,
            MaxRpm = _maxRpm,
            IdleRpm = _idleRpm,
            PeakKilowatts = (int)MathF.Round(_peakWatts / 1000f),
            RecordedAt = DateTimeOffset.Now,
            StartX = StartX,
            StartZ = StartZ,
            StandingStart = StandingStart,
            StartSpeed = _lapStartSpeed,
            EndedAtFinish = true,
            WetFraction = _samples.Count == 0
                ? 0f
                : (float)_samples.Count(m => m.Puddle > 0.05f) / _samples.Count,
            Samples = new List<LapSample>(_samples),
            FullTrack = _vollspur.Detach(),
        };
        LastLapMetres = fertig.LengthMetres;
        _armed = false;
        LapCompleted?.Invoke(this, fertig);
    }

    public void Reset()
    {
        NeuBeginnen();
        _vollspur.Clear();
        _lapNumber = -1;
        _armed = false;
        _startGesehen = false;
        _lastLapTime = 0f;
        _hasLast = false;
        CurrentMetres = 0;
        CurrentSeconds = 0;
        _freiesFahren = false;
        _freiAnker = null;
        _meterOhneUhr = 0f;
        _linien?.Reset();
        // LastLapMetres bleibt stehen: nach dem Rennen ist die Strecke dieselbe wie
        // davor, und beim naechsten Start ist sie sofort wieder der Massstab.
        //
        // Die eigene Uhr (_eigeneUhr) laeuft ebenfalls weiter. Sie ist kein
        // Rundenzaehler, sondern ein Zeitstrahl: Anfang und Ende einer Fahrt sind
        // Punkte auf ihm, und die Differenz ist die Zeit. Sie zurueckzusetzen
        // brauchte es nur, wenn sie ueberliefe -- nach 49 Tagen Dauerbetrieb.
    }

    public void OnTelemetry(ForzaPacket packet)
    {
        // Ausserhalb eines Rennens sendet Forza weiter, aber die Rundenfelder stehen
        // still. Ohne diese Bremse wuerde die Freifahrt als eine endlose Runde
        // aufgezeichnet.
        if (packet.Get("IsRaceOn") < 0.5)
        {
            // Vorher aber: eine Fahrt, die gerade zu Ende ging, gehoert abgelegt --
            // sonst verschwindet jedes Punkt-zu-Punkt-Rennen spurlos.
            FinishOpenRun();
            Reset();
            return;
        }

        var lapNumber = (int)packet.Get("LapNumber");
        var lapTime = (float)packet.Get("CurrentLap");
        var x = (float)packet.Get("PositionX");
        var y = (float)packet.Get("PositionY");
        var z = (float)packet.Get("PositionZ");

        // Der Schritt seit dem letzten Paket -- beide Betriebsarten brauchen ihn,
        // und zweimal dieselbe Rechnung waere zweimal dieselbe Gelegenheit fuer
        // zwei verschiedene Antworten.
        var schritt = 0f;
        if (_hasLast)
        {
            var sx = x - _lastX;
            var sy = y - _lastY;
            var sz = z - _lastZ;
            var s = MathF.Sqrt(sx * sx + sy * sy + sz * sz);
            if (s < 50f) { schritt = s; }
        }

        UhrFortschreiben(packet);

        // RENNEN ODER FREIE WELT?
        //
        // Entschieden an der SPIELUHR, nicht an `IsRaceOn`: in Horizon steht dieses
        // Feld auch beim freien Fahren auf 1 -- gemessen an 18.676 Paketen. Laeuft
        // die Rundenuhr, nimmt das Spiel die Zeit und alles bleibt wie bisher.
        // Steht sie, waehrend gefahren wird, nehmen wir sie selbst.
        if (lapTime > 0f)
        {
            if (_freiesFahren)
            {
                // ZURUECK INS RENNEN -- UND DIE RUNDE MUSS NEU AUFGESETZT WERDEN.
                //
                // `_lapNumber` steht noch auf dem Wert von vorhin. Ohne die Null-
                // stellung waere `lapNumber != _lapNumber` falsch, der Rekorder
                // bliebe unbewaffnet, und in einem SPRINT kommt nie ein
                // Rundenwechsel, der das nachholen koennte: das ganze Rennen waere
                // verloren. Genau dieser Fehler hat am 2026-09-12 drei Sprints
                // gekostet, damals aus einem anderen Grund.
                //
                // -1 heisst "erster Blick". Steht die Rundenuhr dann noch fast auf
                // null, faengt die Runde hier wirklich an und wird abgelegt; ist sie
                // schon weiter, wird zwar gemessen aber nicht eingelagert -- das ist
                // die bestehende Regel fuer einen spaeten Einstieg.
                _freiesFahren = false;
                _lapNumber = -1;
                _armed = false;
                _freiAnker = null;
                NeuBeginnen();
                _vollspur.Clear();
                CurrentMetres = 0f;
                CurrentSeconds = 0f;
            }
            _meterOhneUhr = 0f;
        }
        else
        {
            _meterOhneUhr += schritt;
            if (_meterOhneUhr > FreiAbMetern && !_freiesFahren)
            {
                // Der Wechsel verwirft, was als Rennrunde begonnen wurde: eine
                // Aufzeichnung ohne laufende Uhr ist keine.
                _armed = false;
                NeuBeginnen();
                _vollspur.Clear();
                CurrentMetres = 0f;
                CurrentSeconds = 0f;
                _freiesFahren = true;
                _linien?.Reset();
            }
        }

        if (_freiesFahren)
        {
            FreieWelt(packet, x, y, z, schritt);
            return;
        }

        if (lapNumber != _lapNumber)
        {
            // DER ERSTE BLICK IST KEIN RUNDENWECHSEL.
            //
            // Wer die App mitten im Rennen startet -- oder sie wie ich am
            // 2026-09-12 mitten in einer Runde neu baut --, sieht eine Rundennummer
            // zum ersten Mal und weiss nicht, wo diese Runde angefangen hat. Im
            // Protokoll stand danach "m=19" bei einer Rundenzeit von 55 Sekunden:
            // eine Runde ohne Anfang, die als vollstaendige abgelegt worden waere.
            //
            // Ausnahme: steht die Rundenuhr noch fast auf null, hat sie gerade erst
            // begonnen -- dann ist der Anfang der Anfang.
            var ersterBlick = _lapNumber < 0;

            // Rundenwechsel. Die eben beendete Runde zaehlt nur, wenn sie bei uns von
            // vorne begonnen hat -- sonst fehlt ihr der Anfang.
            if (_armed && _startGesehen && _samples.Count > 2)
            {
                var last = packet.Get("LastLap");
                var fertig = new RecordedLap
                {
                    LapSeconds = last > 0 ? (float)last : _samples[^1].Seconds,
                    LengthMetres = CurrentMetres,
                    CarOrdinal = (int)packet.Get("CarOrdinal"),
                    PerformanceIndex = (int)packet.Get("CarPerformanceIndex"),
                    CarClass = (int)packet.Get("CarClass"),
                    Drivetrain = (int)packet.Get("DrivetrainType"),
                    Cylinders = (int)packet.Get("NumCylinders"),
                    MaxRpm = (int)MathF.Round((float)packet.Get("EngineMaxRpm")),
                    IdleRpm = (int)MathF.Round((float)packet.Get("EngineIdleRpm")),
                    PeakKilowatts = (int)MathF.Round(_peakWatts / 1000f),
                    RecordedAt = DateTimeOffset.Now,
                    StartX = StartX,
                    StartZ = StartZ,
                    StandingStart = StandingStart,
                    StartSpeed = _lapStartSpeed,
                    WetFraction = _samples.Count == 0
                        ? 0f
                        : (float)_samples.Count(m => m.Puddle > 0.05f) / _samples.Count,
                    Samples = new List<LapSample>(_samples),
            FullTrack = _vollspur.Detach(),
                };
                LastLapMetres = fertig.LengthMetres;
                LapCompleted?.Invoke(this, fertig);
            }
            _lapNumber = lapNumber;
            // Die Linie, ueber die eben gefahren wurde, ist der Anfang der neuen
            // Runde -- und zugleich die Kennung der Strecke.
            StartX = (float)packet.Get("PositionX");
            StartZ = (float)packet.Get("PositionZ");
            // Aus dem Stand? Zwei unabhaengige Merkmale, weil beide taeuschen
            // koennen: die Rundennummer 0 ist die Runde von der Aufstellung weg, und
            // unter 5 m/s (18 km/h) steht man praktisch. Eines von beiden genuegt.
            _lapStartSpeed = (float)packet.Get("Speed");
            StandingStart = lapNumber == 0 || _lapStartSpeed < 5f;
            _lapStartTime = lapTime;
            _lastLapTime = lapTime;
            CurrentMetres = 0f;
            NeuBeginnen();
            _vollspur.Clear();
            _nextSampleAt = 0;
            _peakWatts = 0;

            // IMMER mitschreiben -- aber nur ablegen, was von vorne gesehen wurde.
            //
            // Bis zum 2026-09-13 wurde beim ersten Blick gar nicht erst
            // aufgezeichnet, wenn die Rundenuhr nicht mehr fast auf null stand. In
            // einem Sprint gibt es danach keinen Rundenwechsel mehr, der das
            // nachholen koennte: nach jedem Neustart mitten in der Startsequenz
            // blieb die Aufzeichnung fuer das GANZE Rennen aus, und mit ihr das
            // Delta. Im Protokoll stand dann "ref=keine [same car, same tune]" --
            // ohne den Zusatz, den es nur bei laufender Runde gibt.
            //
            // Fuers DELTA ist ein spaeter Einstieg unschaedlich: verglichen wird
            // meine Rundenuhr mit der Zeit der Referenz AN DIESEM ORT, und beides
            // stimmt auch dann, wenn ich erst nach 300 m dazugekommen bin. Falsch
            // waere nur die aufgezeichnete LAENGE -- also wird eine solche Fahrt
            // zwar gefahren und verglichen, aber nicht in den Bestand gelegt.
            _armed = true;
            _startGesehen = !ersterBlick || lapTime < 1.0f;
        }

        if (!_armed) { _lastX = x; _lastY = y; _lastZ = z; _hasLast = true; return; }

        // ----------------------------------------------------------------- //
        // Die gefahrene Strecke: aus dem WEG, nicht aus `DistanceTraveled`
        // ----------------------------------------------------------------- //
        //
        // GEMESSEN am 2026-09-12 an fuenf aufgezeichneten Runden: das Feld
        // `DistanceTraveled` (Offset 292) lieferte fuer JEDE Runde rund 5.950 m --
        // ob sie 25 Sekunden dauerte oder 81. Zwei unabhaengige Quellen widersprachen
        // ihm und stimmten untereinander auf ein Prozent genau ueberein: die ueber
        // die Zeit integrierte `Speed` und der aus den Weltkoordinaten aufsummierte
        // Weg (1090 gegen 1090 m; 4341 gegen 4383 m). Was auch immer an Offset 292
        // steht -- die gefahrene Strecke ist es nicht.
        //
        // Daraus entstanden Runden mit 800 km/h Schnitt. Besonders teuer, weil die
        // Bibliothek je Strecke die SCHNELLSTE behaelt: eine unmoegliche Zeit wird
        // zum Massstab und macht jedes spaetere Delta sinnlos.
        //
        // Ein Sprung von mehr als 50 m zwischen zwei Paketen ist keine Fahrt, sondern
        // ein Zuruecksetzen, ein Rewind oder ein Szenenwechsel -- der zaehlt nicht.
        CurrentMetres += schritt;
        _lastX = x;
        _lastY = y;
        _lastZ = z;
        _hasLast = true;

        // DIE UHR SPRINGT BEIM "GO" ZURUECK.
        //
        // Das Spiel laesst das Auto durch die Einfahrt rollen und setzt beim Start
        // die Rundenuhr auf null (im Protokoll gesehen: 4,34 s -> 2,80 s). Alles,
        // was davor gefahren wurde, gehoert nicht zur Runde.
        if (lapTime < _lastLapTime - 0.05f)
        {
            _lapStartTime = lapTime;
            CurrentMetres = 0f;
            NeuBeginnen();
            _vollspur.Clear();
            _nextSampleAt = 0;
            _peakWatts = 0;
            StartX = x;
            StartZ = z;
            _lapStartSpeed = (float)packet.Get("Speed");
            StandingStart = lapNumber == 0 || _lapStartSpeed < 5f;
            // Hier faengt die Runde nachweislich an -- damit ist ihr Anfang gesehen,
            // auch wenn wir vorher mitten hineingeraten sind.
            _startGesehen = true;
        }
        _lastLapTime = lapTime;

        Fahrzeugdaten(packet);
        CurrentSeconds = lapTime;
        Abtasten(packet);
        // JEDES Paket, nicht nur alle fuenf Meter: die volle Spur ist
        // zum Nachsimulieren da, und die braucht den Zeittakt.
        _vollspur.Add(packet, CurrentSeconds, CurrentMetres);
    }

    /// <summary>
    /// Unsere eigene Uhr fortschreiben -- aus der Uhr des SPIELS, nicht aus unserer.
    /// </summary>
    /// <remarks>
    /// `TimestampMS` ist der Zeitstempel, den Forza jedem Paket mitgibt. Ihn zu
    /// nehmen statt `DateTime.Now` ist kein Feinschliff: unsere Wanduhr misst auch
    /// alles, was zwischen dem Absenden und dem Verarbeiten passiert -- Netzpuffer,
    /// Anzeige, Speicherbereinigung. Bei 60 Paketen je Sekunde sind das
    /// Millisekunden je Paket und ueber eine Runde leicht eine Zehntelsekunde, also
    /// genau die Groessenordnung, um die es beim Vergleich geht.
    ///
    /// Grosse Spruenge zaehlen nicht: eine Pause, ein Neustart des Spiels oder der
    /// Ueberlauf nach 49 Tagen sind keine gefahrene Zeit.
    /// </remarks>
    private void UhrFortschreiben(ForzaPacket packet)
    {
        var stempel = (uint)packet.Get("TimestampMS");
        if (_hatStempel)
        {
            var abstand = unchecked(stempel - _letzterStempel);
            if (abstand > 0u && abstand < 60000u) { _eigeneUhr += abstand / 1000f; }
        }
        _letzterStempel = stempel;
        _hatStempel = true;
    }

    /// <summary>
    /// Ein Messwert in der freien Welt: selbst gestoppt, an einer eigenen Linie.
    /// </summary>
    /// <remarks>
    /// Die Uhr laeuft zwischen zwei Ueberfahrten DERSELBEN Linie. Wer eine andere
    /// Linie ueberfaehrt, faengt dort neu an -- das ist die Regel, die sich einem
    /// Fahrer in einem Satz erklaeren laesst ("die Zeit laeuft, sobald du ueber eine
    /// Startlinie faehrst, und stoppt, wenn du wieder ueber dieselbe faehrst"), und
    /// sie braucht keine Bedienung.
    ///
    /// Die Richtung zaehlt mit: wer zurueckfaehrt und dieselbe Linie von der anderen
    /// Seite ueberquert, hat keine Runde gedreht, sondern gewendet.
    /// </remarks>
    private void FreieWelt(ForzaPacket packet, float x, float y, float z, float schritt)
    {
        if (_armed)
        {
            CurrentMetres += schritt;
            CurrentSeconds = _eigeneUhr - _freiStartSekunden;
            Fahrzeugdaten(packet);
            Abtasten(packet);
            // JEDES Paket, nicht nur alle fuenf Meter: die volle Spur ist
            // zum Nachsimulieren da, und die braucht den Zeittakt.
            _vollspur.Add(packet, CurrentSeconds, CurrentMetres);
            // Wer nach sechzig Kilometern noch nicht zurueck ist, faehrt keine
            // Runde. Die Aufzeichnung wird verworfen, sonst sammelt sie unbegrenzt
            // Messpunkte -- alle 5 m einen, eine Stunde lang.
            if (CurrentMetres > FreeRoamTimer.MaxLapMetres)
            {
                _armed = false;
                _freiAnker = null;
                NeuBeginnen();
                _vollspur.Clear();
                CurrentMetres = 0f;
                CurrentSeconds = 0f;
            }
        }

        var ueberfahrt = Lines.Step(x, z, _eigeneUhr);

        _lastX = x;
        _lastY = y;
        _lastZ = z;
        _hasLast = true;

        if (ueberfahrt is null) { return; }
        var punkt = ueberfahrt.Value;

        if (_armed && ReferenceEquals(punkt.Anchor, _freiAnker))
        {
            var dauer = punkt.Seconds - _freiStartSekunden;
            var vorwaerts = _freiRichtungX * punkt.HeadingX
                            + _freiRichtungZ * punkt.HeadingZ > 0f;
            if (dauer > 5f && CurrentMetres >= FreeRoamTimer.MinLapMetres && vorwaerts)
            {
                FreiAbschliessen(dauer);
            }
        }

        FreiStarten(punkt, packet);
    }

    /// <summary>Eine Zeitjagd beginnt -- an der eben ueberfahrenen Linie.</summary>
    /// <remarks>
    /// Als Startpunkt wird die LINIE vermerkt und nicht der Ort des Autos. Damit
    /// beginnt jede Fahrt ueber dieselbe Linie bei genau derselben Koordinate, und
    /// alle landen im selben Streckenordner -- waehrend der Ort des Autos je nach
    /// Fahrlinie ein paar Meter wandert und die Zuordnung ins Rutschen braechte.
    /// </remarks>
    private void FreiStarten(FreeRoamTimer.Pass punkt, ForzaPacket packet)
    {
        _freiAnker = punkt.Anchor;
        _freiStartSekunden = punkt.Seconds;
        _freiRichtungX = punkt.HeadingX;
        _freiRichtungZ = punkt.HeadingZ;
        StartX = punkt.Anchor.X;
        StartZ = punkt.Anchor.Z;
        _lapStartSpeed = (float)packet.Get("Speed");
        StandingStart = _lapStartSpeed < 5f;
        CurrentMetres = 0f;
        CurrentSeconds = 0f;
        NeuBeginnen();
        _vollspur.Clear();
        _nextSampleAt = 0f;
        _peakWatts = 0f;
        _armed = true;
        _startGesehen = true;
    }

    /// <summary>Eine Runde in der freien Welt ablegen.</summary>
    /// <remarks>
    /// ## Warum hier NICHTS abgeschnitten wird
    ///
    /// Eine Ueberfahrt steht erst fest, wenn man sich wieder entfernt hat -- bis
    /// dahin sind schon ein paar Dutzend Meter mehr gefahren. Mein erster Entwurf
    /// schnitt diesen "Schwanz" darum ab: alle Messpunkte hinter dem Zeitpunkt der
    /// Ueberfahrt weg, die Laenge aus dem letzten verbliebenen.
    ///
    /// Die eigene Pruefung hat das widerlegt -- ein Rundkurs von genau 2.000 m kam
    /// als 1.970 m heraus, und die fehlenden 30 m waren genau der Ueberhang. Der
    /// Denkfehler: die Aufzeichnung beginnt um DENSELBEN Betrag zu spaet, aus
    /// demselben Grund. Der Wegzaehler laeuft von "Linie plus 26 m" bis "Linie plus
    /// 26 m" -- das ist eine volle Runde, und zwar schon vor jeder Korrektur.
    /// Abschneiden entfernte das Ende und liess den fehlenden Anfang stehen.
    ///
    /// ## Warum die ZEIT davon unberuehrt ist
    ///
    /// Sie stammt nicht vom Wegzaehler, sondern von den beiden Zeitpunkten der
    /// groessten Annaeherung -- also von der Linie selbst, an beiden Enden. Sie ist
    /// darum exakt, unabhaengig davon, wie schnell man die Linie ueberquert hat.
    /// Die Messpunkte tragen ihre Zeit ebenfalls ab der Linie; der erste steht nur
    /// nicht bei 0,0 s, sondern bei dem Bruchteil, den er hinter ihr liegt. Fuers
    /// Delta ist genau das richtig: verglichen wird "wie lange brauchte die
    /// Bestzeit bis an DIESEN Ort".
    /// </remarks>
    private void FreiAbschliessen(float dauer)
    {
        if (_samples.Count < 3) { return; }
        var punkte = new List<LapSample>(_samples);

        var fertig = new RecordedLap
        {
            LapSeconds = dauer,
            LengthMetres = CurrentMetres,
            CarOrdinal = _ordinal,
            PerformanceIndex = _pi,
            CarClass = _class,
            Drivetrain = _drivetrain,
            Cylinders = _cylinders,
            MaxRpm = _maxRpm,
            IdleRpm = _idleRpm,
            PeakKilowatts = (int)MathF.Round(_peakWatts / 1000f),
            RecordedAt = DateTimeOffset.Now,
            StartX = StartX,
            StartZ = StartZ,
            StandingStart = StandingStart,
            StartSpeed = _lapStartSpeed,
            FreeRoam = true,
            // Hier ist der Modus KEINE Vermutung: diese Fahrt wurde von der eigenen
            // Uhr gegen die eigene Linie gestoppt, das geht nur in der freien Welt.
            Mode = "freeroam",
            ModeEvidence = "timer",
            // DER NAME DER LINIE IST NUR DANN EIN STRECKENNAME, wenn er keiner Ordnerkennung
            // gleicht: eine Linie aus einem unbenannten Kurs heisst "course_-1850_1575_...".
            // Am 2026-09-26 landete so eine Kennung als Streckenname im Kurs, und die
            // Umrisse fanden "Shimanoyama Circuit" nicht mehr, obwohl 34 Runden dalagen.
            Track = LapArchive.IstStreckenname(_freiAnker?.Name) ? _freiAnker!.Name : null,
            WetFraction = (float)punkte.Count(m => m.Puddle > 0.05f) / punkte.Count,
            Samples = punkte,
            FullTrack = _vollspur.Detach(),
        };
        LastLapMetres = fertig.LengthMetres;
        LapCompleted?.Invoke(this, fertig);
    }

    /// <summary>
    /// Die Kennwerte des Autos mitfuehren.
    /// </summary>
    /// <remarks>
    /// Beim Rennende sind die Paketfelder nicht mehr verlaesslich befuellt,
    /// gebraucht werden sie dort aber -- ein Eintrag mit Auto 0 und PI 0 waere im
    /// Bestand wertlos.
    /// </remarks>
    private void Fahrzeugdaten(ForzaPacket packet)
    {
        _ordinal = (int)packet.Get("CarOrdinal");
        _pi = (int)packet.Get("CarPerformanceIndex");
        _class = (int)packet.Get("CarClass");
        _drivetrain = (int)packet.Get("DrivetrainType");
        _cylinders = (int)packet.Get("NumCylinders");
        _maxRpm = (int)MathF.Round((float)packet.Get("EngineMaxRpm"));
        _idleRpm = (int)MathF.Round((float)packet.Get("EngineIdleRpm"));

        var watts = (float)packet.Get("Power");
        if (watts > _peakWatts) { _peakWatts = watts; }
    }

    /// <summary>Alle 5 m einen Messpunkt setzen.</summary>
    private void Abtasten(ForzaPacket packet)
    {
        if (CurrentMetres + 0.001f >= _nextSampleAt)
        {
            _samples.Add(new LapSample(
                CurrentMetres, CurrentSeconds,
                (float)packet.Get("PositionX"),
                (float)packet.Get("PositionY"),
                (float)packet.Get("PositionZ"),
                (float)packet.Get("Speed"),
                // Gas und Bremse kommen als 0..255; als Anteil gespeichert ist das
                // fuer eine Karte brauchbarer als eine Zahl ohne Einheit.
                (float)packet.Get("Accel") / 255f,
                (float)packet.Get("Brake") / 255f,
                (float)packet.Get("Steer") / 127f,
                (float)packet.Get("AccelerationX"),
                (float)packet.Get("AccelerationZ"),
                (float)packet.Get("Clutch") / 255f,
                (float)packet.Get("HandBrake") / 255f,
                (float)packet.Get("Gear"),
                // Wie nass es unter den Raedern ist. Ein eigenes Wetterfeld gibt es
                // in diesem Paket nicht -- `WheelInPuddle` ist das Naechste daran.
                MathF.Max(
                    MathF.Max((float)packet.Get("WheelInPuddleFrontLeft"),
                              (float)packet.Get("WheelInPuddleFrontRight")),
                    MathF.Max((float)packet.Get("WheelInPuddleRearLeft"),
                              (float)packet.Get("WheelInPuddleRearRight")))));
            _nextSampleAt = CurrentMetres + SampleMetres;
        }
    }
}

/// <summary>
/// Der eigene Rundenbestand: speichert, was gefahren wurde, und nennt die Referenz.
/// </summary>
/// <remarks>
/// Es wird je Auswahl die SCHNELLSTE Runde behalten, nicht die letzte: verglichen
/// wird gegen die eigene Bestzeit, und eine schlechtere Runde als Massstab waere
/// keine Auskunft, sondern Trost.
/// </remarks>
internal sealed class LapLibrary
{
    private static readonly JsonSerializerOptions Format = new() { WriteIndented = false };

    public static string DefaultPath => Path.Combine(AppInfo.DataFolder, "personal_laps.json");

    private readonly string _path;
    private readonly List<RecordedLap> _laps = new();

    public LapLibrary(string? path = null)
    {
        _path = path ?? DefaultPath;
        Load();
    }

    public IReadOnlyList<RecordedLap> Laps => _laps;

    public void Load()
    {
        _laps.Clear();
        try
        {
            if (!File.Exists(_path)) { return; }
            var gelesen = JsonSerializer.Deserialize<List<RecordedLap>>(
                File.ReadAllText(_path));
            if (gelesen is not null) { _laps.AddRange(gelesen); }
            foreach (var lap in _laps) { InferStandingStart(lap); }
        }
        catch (Exception)
        {
            // Ein kaputter Bestand darf die App nicht am Start hindern; er wird beim
            // naechsten Speichern ueberschrieben.
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".part";
            File.WriteAllText(temp, JsonSerializer.Serialize(_laps, Format));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception)
        {
            // Eine verlorene Runde ist aergerlich, ein Absturz mitten im Rennen mehr.
        }
    }

    /// <summary>
    /// Runden aus der Zeit vor der Unterscheidung nachtraeglich einordnen.
    /// </summary>
    /// <remarks>
    /// Die Messpunkte verraten es: die ersten 5 m dauern aus dem Stand rund eine
    /// Sekunde, fliegend ein Zwanzigstel davon. Ohne diese Nachbesserung blieben die
    /// bereits gefahrenen Runden als "fliegend" im Bestand und wuerden weiter gegen
    /// stehende Runden gehalten -- genau der Fehler, der behoben werden soll.
    /// </remarks>
    private static void InferStandingStart(RecordedLap lap)
    {
        if (lap.StartSpeed > 0f) { return; }
        if (lap.Samples.Count < 2) { return; }
        var ersteMeter = lap.Samples[1].Seconds - lap.Samples[0].Seconds;
        lap.StandingStart = ersteMeter > 0.5f;
    }

    /// <summary>Eine Runde aufnehmen. Behaelt je (Strecke, Auto, PI) nur die beste.</summary>
    /// <summary>Schnelligkeit, oberhalb derer keine Runde mehr glaubwuerdig ist.</summary>
    /// <remarks>
    /// 450 km/h liegt ueber allem, was in diesem Spiel eine ganze Runde lang zu
    /// halten ist, und weit unter den 800 km/h, die aus `DistanceTraveled` entstanden.
    /// Die Ursache ist behoben; diese Grenze steht fuer die naechste, die noch
    /// niemand kennt -- eine falsche Bestzeit im Bestand entwertet jedes Delta auf
    /// dieser Strecke, und zwar lautlos.
    /// </remarks>
    public const float MaxPlausibleMetresPerSecond = 125f;

    public bool Add(RecordedLap lap)
    {
        if (lap.LapSeconds <= 0 || lap.Samples.Count < 3) { return false; }
        var tempo = lap.LengthMetres / lap.LapSeconds;
        if (tempo > MaxPlausibleMetresPerSecond)
        {
            OverlayController.WriteDiagnostic(
                $"Runde verworfen: {lap.LengthMetres:0} m in {lap.LapSeconds:0.000} s "
                + $"sind {tempo * 3.6f:0} km/h -- das kann nicht sein.");
            return false;
        }
        // MEHRERE FAHRTEN JE STRASSE -- daraus ergibt sich das Ziel.
        //
        // Frueher wurde je Strasse genau eine behalten, "die schnellste". Wer die
        // schnellste ist, laesst sich aber erst sagen, wenn man weiss, WO die
        // Strecke endet -- und das wiederum steht nur in der Haeufung der
        // Endpunkte mehrerer Fahrten. Mit einer einzigen gespeicherten Fahrt gibt
        // es keine Haeufung, und dann bleiben nur Laengenvergleiche: 12 %, dann
        // 4 %, dann eine Nachlauftoleranz. Drei geratene Werte, drei Pannen in
        // einer Nacht.
        //
        // Es werden darum bis zu sechs Fahrten je Strasse gehalten. Ist die Gruppe
        // voll, faellt die heraus, die am WENIGSTEN weit gekommen ist -- der
        // schlechteste Abbruch. Die vollstaendigen Fahrten bleiben, und sie sind
        // es, die das Ziel bestimmen.
        const int JeStrasse = 6;

        var gruppe = new List<RecordedLap>();
        foreach (var vorhanden in _laps)
        {
            if (vorhanden.TuneKey == lap.TuneKey
                && vorhanden.StandingStart == lap.StandingStart
                && vorhanden.EndedAtFinish == lap.EndedAtFinish
                // Selbst gestoppt gegen vom Spiel gewertet waere kein Vergleich --
                // die Begruendung steht bei RecordedLap.FreeRoam.
                && vorhanden.FreeRoam == lap.FreeRoam
                && RecordedLap.SameRoad(vorhanden, lap))
            {
                gruppe.Add(vorhanden);
            }
        }

        // Wer war vor dieser Fahrt der Massstab? Das entscheidet die Meldung.
        var vorherBeste = SchnellsteZumZiel(gruppe);

        _laps.Add(lap);
        gruppe.Add(lap);

        if (gruppe.Count > JeStrasse)
        {
            var route = gruppe[0];
            foreach (var k in gruppe)
            {
                if (k.RouteLength > route.RouteLength) { route = k; }
            }
            RecordedLap? schwaechste = null;
            var wenigste = float.MaxValue;
            foreach (var k in gruppe)
            {
                if (ReferenceEquals(k, route)) { continue; }
                var weit = k.ArcReachedOn(route);
                if (weit < wenigste) { wenigste = weit; schwaechste = k; }
            }
            if (schwaechste is not null)
            {
                _laps.Remove(schwaechste);
                gruppe.Remove(schwaechste);
            }
        }

        Save();

        var jetztBeste = SchnellsteZumZiel(gruppe);
        // "true" heisst: ab jetzt wird gegen DIESE Fahrt gemessen.
        return ReferenceEquals(jetztBeste, lap) && !ReferenceEquals(vorherBeste, lap);
    }

    /// <summary>
    /// Die schnellste Fahrt bis zum Ziel der Gruppe -- oder null.
    /// </summary>
    /// <remarks>
    /// Dieselbe Rechnung wie im Overlay: die weiteste Fahrt gibt die Route, das Ziel
    /// ergibt sich aus der Haeufung der Endpunkte, verglichen wird die Zeit an
    /// diesem einen Ort. Gesamtzeiten werden NICHT verglichen -- in ihnen steckt,
    /// wie lange das Spiel nach dem Ziel noch gesendet hat.
    /// </remarks>
    public static RecordedLap? SchnellsteZumZiel(IReadOnlyList<RecordedLap> gruppe)
    {
        if (gruppe.Count == 0) { return null; }
        if (gruppe.Count == 1) { return gruppe[0]; }

        var route = gruppe[0];
        foreach (var k in gruppe)
        {
            if (k.RouteLength > route.RouteLength) { route = k; }
        }
        var ziel = RecordedLap.FinishArc(route, gruppe);

        RecordedLap? beste = null;
        var besteZeit = float.MaxValue;
        foreach (var k in gruppe)
        {
            var zeit = k.SecondsAtArcOf(route, ziel);
            if (zeit is null) { continue; }
            if (zeit.Value < besteZeit) { besteZeit = zeit.Value; beste = k; }
        }
        return beste ?? route;
    }

    /// <summary>
    /// Die Runde, gegen die gemessen wird -- oder null, wenn es noch keine gibt.
    /// </summary>
    /// <remarks>
    /// Die Auswahl wird nur ENGER, nie anders: "dieses Auto mit dieser Abstimmung" ist
    /// eine Teilmenge von "dieses Auto", und das wiederum von "diese Klasse". Wer eine
    /// enge Auswahl waehlt und dort noch nichts gefahren hat, bekommt darum NICHTS und
    /// nicht heimlich eine weiter gefasste Bestzeit -- sonst stuende ein Delta auf dem
    /// Schirm, das eine andere Frage beantwortet als die gestellte.
    /// </remarks>
    public RecordedLap? Reference(DeltaReference mode, float lengthMetres, string tuneKey,
                                  int carOrdinal, int carClass,
                                  float startX = 0f, float startZ = 0f,
                                  bool standingStart = false,
                                  bool freeRoam = false)
    {
        var muster = new RecordedLap
        {
            LengthMetres = lengthMetres, StartX = startX, StartZ = startZ,
        };
        IEnumerable<RecordedLap> kandidaten = _laps.Where(
            l => RecordedLap.SameCourse(l, muster) && l.StandingStart == standingStart
                 && l.FreeRoam == freeRoam);
        kandidaten = mode switch
        {
            DeltaReference.SameCarSameTune => kandidaten.Where(l => l.TuneKey == tuneKey),
            DeltaReference.SameCar => kandidaten.Where(l => l.CarOrdinal == carOrdinal),
            DeltaReference.SameCarSameClass => kandidaten.Where(
                l => l.CarOrdinal == carOrdinal && l.CarClass == carClass),
            DeltaReference.SameClass => kandidaten.Where(l => l.CarClass == carClass),
            _ => kandidaten,
        };
        return kandidaten.OrderBy(l => l.LapSeconds).FirstOrDefault();
    }
}
