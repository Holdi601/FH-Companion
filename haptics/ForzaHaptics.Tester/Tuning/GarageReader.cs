using Microsoft.Data.Sqlite;

namespace ForzaHaptics.Tester.Tuning;

/// <summary>Ein verbautes Teil, so weit die Garage es hergibt.</summary>
/// <param name="Area">Grobe Einordnung fuer die Anzeige.</param>
/// <param name="Label">Wie das Spiel den Teilebereich nennt.</param>
/// <param name="Id">Die Teilenummer, wie sie in der Garage steht.</param>
/// <param name="Step">Die letzten drei Stellen -- die Ausbaustufe.</param>
/// <param name="Family">Die vorderen Stellen -- zu welchem Auto das Teil gehoert.</param>
/// <param name="Origin">
/// Woher das Teil stammt -- <c>own</c> aus dem eigenen Teilekatalog des Autos,
/// <c>universal</c> aus einem Katalog, den viele Autos teilen, <c>swap</c> aus einem
/// ANDEREN Auto (dann steht sein Name in <paramref name="FamilyName"/>).
/// </param>
/// <param name="FamilyName">Das Auto hinter der Familie, wenn es eines ist.</param>
/// <param name="PricePaid">Was dafuer bezahlt wurde, oder null (Serienteil).</param>
internal sealed record TunePart(string Area, string Label, long Id, int Step,
                                long Family, string Origin, string? FamilyName,
                                long? PricePaid)
{
    /// <summary>Ein Satz, der erklaert, was die Nummer bedeutet.</summary>
    /// <remarks>
    /// "foreign family 3899" stand hier zuerst, und der Nutzer hat zu Recht gesagt,
    /// dass das niemandem etwas sagt. Die Zahl BEDEUTET etwas, und das laesst sich
    /// hinschreiben -- gemessen an 587 Autos:
    ///
    /// - Ist die Familie ein bekanntes Auto, steht sie AUSSCHLIESSLICH in den
    ///   Motor-Innereien (Nockenwelle, Ventile, Hubraum, Kolben, Kraftstoff,
    ///   Zuendung, Auspuff, Ansaugung, Schwungrad, Oelkuehlung) -- alle 103 Faelle
    ///   gemeinsam, und die Spalte `Engine` traegt sie NIE mit. Das ist ein
    ///   Motortausch.
    /// - Ist sie kein Auto, ist es ein Universalteil: Kupplung, Getriebe, Antrieb
    ///   und Differential tragen es je 545-mal.
    ///
    /// 512 verschiedene Familien, 7.679 Teile -- davon 1.149 aus einem Auto und
    /// 6.530 aus einem geteilten Katalog.
    /// </remarks>
    public string Erklaerung
    {
        get
        {
            // DER NAME ZUERST, wenn es einen belegten gibt. Genau darum ging es dem
            // Nutzer: "es muss schon der teilename da stehen wie bspw race
            // transmission or rallye".
            var name = PartNames.Name(Label, Step);
            if (name is not null)
            {
                return Origin == "swap" && FamilyName is not null
                    ? $"{name} -- engine swap: {FamilyName}"
                    : name;
            }

            // Kein belegter Name: dann sagen, unter welchen er zu suchen ist, statt
            // einen zu erfinden. Die Stufen ab 4 sind Sonderteile, und welche Stufe
            // welchen Namen traegt, steht nirgends im Speicher.
            var sonder = PartNames.Sondernamen(Label);
            var woraus = sonder.Count > 0
                ? $" -- one of: {string.Join(", ", sonder)}"
                : string.Empty;
            return Origin switch
            {
                "swap" => FamilyName is null
                    ? $"engine swap (from car {Family})"
                    : $"engine swap: {FamilyName}",
                "universal" => $"upgrade, step {Step}{woraus}",
                _ => Step == 0 ? "stock" : $"upgrade, step {Step}{woraus}",
            };
        }
    }
}

/// <summary>Eine Tuning-Einstellung als REGLERPOSITION, 0 bis 1.</summary>
internal sealed record TuneSetting(string Area, string Label, double Slider);

/// <summary>Was an einem Auto gemacht wurde.</summary>
/// <param name="StoredIndex">
/// Der Wert aus der Spalte <c>PerformanceIndex</c>, mal 1000. Das ist NICHT der PI,
/// den das Spiel anzeigt -- die Begruendung steht bei <see cref="GarageReader"/>.
/// </param>
internal sealed record CarTune(
    int CarId, int GarageId, int StoredIndex, int ClassId, string ClassName,
    long PartsValue, double CurbWeight, double TopSpeed, double PeakPower,
    string? TuneFileName, long VersionedTuneId, long VersionedTuneXuid,
    long SharedId, string? OriginalOwner,
    IReadOnlyList<TunePart> Parts, IReadOnlyList<TuneSetting> Settings);

/// <summary>
/// Die Garagen-Datenbank des Spiels lesen: welche Teile, welche Einstellungen.
/// </summary>
/// <remarks>
/// ## Was hier steht und was nicht
///
/// `Career_Garage` hat je Auto EINE Zeile mit 146 Spalten: alle Teile als eigene
/// Spalte, alle Tuning-Regler, und die Herkunft des Tunes. Das ist der Bauplan.
///
/// Was FEHLT, und das gehoert offen gesagt: die Teile stehen als NUMMERN da, nicht
/// als Namen. Das Woerterbuch dazu (die `List_Upgrade*`-Tabellen) liegt zwar auch im
/// Speicher, aber nur als Seiten-Zwischenspeicher -- es kommt als leeres Abbild
/// heraus. Aus "Camshaft 3899003" laesst sich darum nicht "Rennsport-Nockenwelle"
/// machen. Was sich sehr wohl machen laesst, steht weiter unten.
///
/// ## Zwei Dinge, die die Nummern doch verraten -- beide gemessen
///
/// **1. Die letzten drei Stellen sind die Ausbaustufe.** Gemessen an 569 Autos:
/// Autos ohne einen einzigen gekauften Teil tragen im Schnitt 0,56 Teile ueber
/// Stufe 0, Autos mit gekauften Teilen 9,53. Und in der Gegenprobe an einem
/// einzelnen Auto hatte JEDES Teil ueber Stufe 0 einen Kaufbeleg in
/// `Career_PurchasedParts` und jedes auf Stufe 0 keinen. Stufe 0 heisst also Serie.
///
/// **2. Die vorderen Stellen sind die Auto-Nummer -- wenn sie es nicht sind, ist es
/// ein fremdes Teil.** 57 % aller Teilewerte tragen die eigene `CarId` vorne. Der
/// Rest gehoert zu geteilten Familien (`2102xxx` fuer Kupplung, Getriebe,
/// Antriebsstrang, Differential) oder zu einem ANDEREN Auto -- und das ist dann ein
/// Motortausch: bei CarId 3761 tragen Nockenwelle, Ventile, Hubraum und Auspuff die
/// Vorsilbe 3899. Das sieht man sonst nirgends.
///
/// ## Die Spalte `PerformanceIndex` ist NICHT der angezeigte PI
///
/// Das war der naheliegende Schluss -- 0,7857 sieht nach PI 786 aus -- und er ist
/// falsch. Gemessen an 569 Autos widerspricht die so gewonnene Klasse bei **128**
/// von ihnen der Klasse aus `ClassID`, und zwar durchweg um genau eine Stufe: die
/// Obergrenze je ClassID liegt jedesmal rund hundert Punkte unter der Klassengrenze
/// (ClassID 3 reicht bis 605,8, die Klasse A aber bis 700).
///
/// Belegt ist dagegen `ClassID`: es ist dasselbe Feld wie `CarClass` im
/// Telemetriepaket, und ueber die eigenen gefahrenen Autos geeicht stimmt es bei 10
/// von 11 (das elfte wurde zwischen Abzug und Fahrt umgebaut).
///
/// Darum heisst der Wert hier `StoredIndex` und nicht "PI". Wer den echten PI will,
/// nimmt ihn aus der Telemetrie -- dort steht er so, wie das Spiel ihn anzeigt.
///
/// ## Die Tuning-Werte sind Reglerpositionen
///
/// Der Bildschirm zeigt 2,1 BAR, in der Datenbank steht 0,4. Das ist die Position
/// des Reglers, nicht der Anzeigewert. Am 2026-08-30 wurde stundenlang im Speicher
/// nach der Zahl 2,1 gesucht -- sie existiert dort nicht. Ohne eine Eichung je Feld
/// (Regler auf Anschlag, Wert ablesen) laesst sich der Anzeigewert nicht
/// zurueckrechnen, und darum steht hier ehrlich die Reglerposition.
/// </remarks>
internal static class GarageReader
{
    /// <summary>
    /// Die Klassen des Spiels, nach `ClassID`.
    /// </summary>
    /// <remarks>
    /// GEEICHT am 2026-09-14 gegen die Telemetrie der eigenen Runden: `ClassID` ist
    /// dasselbe Feld wie `CarClass` im Telemetriepaket (10 von 11 gefahrenen Autos
    /// stimmen ueberein; das elfte wurde zwischen Abzug und Fahrt umgebaut). Und die
    /// zugehoerigen PI-Werte sind eindeutig:
    ///
    ///     ClassID  0    1    2    3    4    5    6
    ///     PI-Kappe 400  500  600  700  800  900  998
    ///     Klasse   D    C    B    A    S1   S2   R
    ///
    /// Damit ist eine alte Notiz beantwortet: "PI 800 kam als 4, PI 900 als 5 --
    /// welche Klasse das sein soll, sagt niemand." 4 ist S1, 5 ist S2.
    ///
    /// Mein erster Entwurf las die Grenzen um eine Stufe zu niedrig (500 als D) und
    /// erfand darum eine Klasse "E" fuer die uebrig gebliebene 0. Der Nutzer hat es
    /// richtiggestellt; die Klassenleiste des Spiels sagt D 400 / C 500 / B 600 /
    /// A 700 / S1 800 / S2 900, und das steht sogar in einem Kommentar im eigenen
    /// Navigator.
    /// </remarks>
    private static readonly string[] Klassen =
        ["D", "C", "B", "A", "S1", "S2", "R"];

    public static string ClassName(int classId) =>
        classId >= 0 && classId < Klassen.Length ? Klassen[classId] : "?";

    /// <summary>Spalte, Bereich, Beschriftung -- in der Reihenfolge der Anzeige.</summary>
    private static readonly (string Column, string Area, string Label)[] PartColumns =
    [
        ("Engine", "Engine", "Engine block"),
        ("Camshaft", "Engine", "Camshaft"),
        ("Valves", "Engine", "Valves"),
        ("Displacement", "Engine", "Displacement"),
        ("PistonsCompression", "Engine", "Pistons & compression"),
        ("FuelSystem", "Engine", "Fuel system"),
        ("Ignition", "Engine", "Ignition"),
        ("Exhaust", "Engine", "Exhaust"),
        ("Intake", "Engine", "Intake"),
        ("Flywheel", "Engine", "Flywheel"),
        ("Manifold", "Engine", "Manifold"),
        ("RestrictorPlate", "Engine", "Restrictor plate"),
        ("OilCooling", "Engine", "Oil & cooling"),
        ("SingleTurbo", "Aspiration", "Single turbo"),
        ("TwinTurbo", "Aspiration", "Twin turbo"),
        ("QuadTurbo", "Aspiration", "Quad turbo"),
        ("SuperchargerCSC", "Aspiration", "Supercharger (centrifugal)"),
        ("SuperchargerDSC", "Aspiration", "Supercharger (positive)"),
        ("Intercooler", "Aspiration", "Intercooler"),
        ("Motor", "Electric", "Electric motor"),
        ("MotorParts", "Electric", "Motor parts"),
        ("Clutch", "Drivetrain", "Clutch"),
        ("Transmission", "Drivetrain", "Transmission"),
        ("Driveline", "Drivetrain", "Driveline"),
        ("Differential", "Drivetrain", "Differential"),
        ("Drivetrain", "Drivetrain", "Drivetrain layout"),
        ("Brakes", "Platform", "Brakes"),
        ("SpringDamper", "Platform", "Springs & dampers"),
        ("AntiSwayFront", "Platform", "Anti-roll bar, front"),
        ("AntiSwayRear", "Platform", "Anti-roll bar, rear"),
        ("ChassisStiffness", "Platform", "Chassis reinforcement"),
        ("WeightReduction", "Platform", "Weight reduction"),
        ("TrackSpacingFront", "Platform", "Track width, front"),
        ("TrackSpacingRear", "Platform", "Track width, rear"),
        ("TireCompound", "Tyres & wheels", "Tyre compound"),
        ("TireWidthFront", "Tyres & wheels", "Tyre width, front"),
        ("TireWidthRear", "Tyres & wheels", "Tyre width, rear"),
        ("FrontAspectRatio", "Tyres & wheels", "Aspect ratio, front"),
        ("RearAspectRatio", "Tyres & wheels", "Aspect ratio, rear"),
        ("RimSizeFront", "Tyres & wheels", "Rim size, front"),
        ("RimSizeRear", "Tyres & wheels", "Rim size, rear"),
        ("TireBrand", "Tyres & wheels", "Tyre brand"),
        ("WheelStyle", "Tyres & wheels", "Wheel style"),
        ("WheelStyleRear", "Tyres & wheels", "Wheel style, rear"),
        ("CarBody", "Body & aero", "Car body"),
        ("FrontBumper", "Body & aero", "Front bumper"),
        ("RearBumper", "Body & aero", "Rear bumper"),
        ("Hood", "Body & aero", "Hood"),
        ("SideSkirts", "Body & aero", "Side skirts"),
        ("RearWing", "Body & aero", "Rear wing"),
    ];

    private static readonly (string Column, string Area, string Label)[] SettingColumns =
    [
        ("Tuning_frontTirePressure", "Tyres", "Tyre pressure, front"),
        ("Tuning_rearTirePressure", "Tyres", "Tyre pressure, rear"),
        ("Tuning_finalDriveRatio", "Gearing", "Final drive"),
        ("Tuning_firstGear", "Gearing", "1st gear"),
        ("Tuning_secondGear", "Gearing", "2nd gear"),
        ("Tuning_thirdGear", "Gearing", "3rd gear"),
        ("Tuning_fourthGear", "Gearing", "4th gear"),
        ("Tuning_fifthGear", "Gearing", "5th gear"),
        ("Tuning_sixthGear", "Gearing", "6th gear"),
        ("Tuning_seventhGear", "Gearing", "7th gear"),
        ("Tuning_eighthGear", "Gearing", "8th gear"),
        ("Tuning_ninthGear", "Gearing", "9th gear"),
        ("Tuning_tenthGear", "Gearing", "10th gear"),
        ("Tuning_frontCamber", "Alignment", "Camber, front"),
        ("Tuning_rearCamber", "Alignment", "Camber, rear"),
        ("Tuning_frontToe", "Alignment", "Toe, front"),
        ("Tuning_rearToe", "Alignment", "Toe, rear"),
        ("Tuning_frontCaster", "Alignment", "Caster"),
        ("Tuning_frontSwaybar", "Anti-roll bars", "Anti-roll bar, front"),
        ("Tuning_rearSwaybar", "Anti-roll bars", "Anti-roll bar, rear"),
        ("Tuning_frontSpring", "Springs", "Spring, front"),
        ("Tuning_rearSpring", "Springs", "Spring, rear"),
        ("Tuning_frontRideHeight", "Springs", "Ride height, front"),
        ("Tuning_rearRideHeight", "Springs", "Ride height, rear"),
        ("Tuning_frontDampingStiffness", "Damping", "Rebound, front"),
        ("Tuning_rearDampingStiffness", "Damping", "Rebound, rear"),
        ("Tuning_frontBumpRatio", "Damping", "Bump, front"),
        ("Tuning_rearBumpRatio", "Damping", "Bump, rear"),
        ("Tuning_frontDownforce", "Aero", "Downforce, front"),
        ("Tuning_rearDownforce", "Aero", "Downforce, rear"),
        ("Tuning_brakeBalance", "Brakes", "Brake balance"),
        ("Tuning_brakePressure", "Brakes", "Brake pressure"),
        ("Tuning_frontAccel", "Differential", "Acceleration, front"),
        ("Tuning_rearAccel", "Differential", "Acceleration, rear"),
        ("Tuning_frontDecel", "Differential", "Deceleration, front"),
        ("Tuning_rearDecel", "Differential", "Deceleration, rear"),
        ("Tuning_centerTorque", "Differential", "Centre balance"),
    ];

    /// <summary>Welches der Abbilder traegt eine brauchbare Garage?</summary>
    public static string? FindGarage(IEnumerable<string> kandidaten)
    {
        foreach (var pfad in kandidaten)
        {
            try
            {
                using var db = Open(pfad);
                using var cmd = db.CreateCommand();
                cmd.CommandText = "select count(*) from Career_Garage";
                if (Convert.ToInt64(cmd.ExecuteScalar()) > 0) { return pfad; }
            }
            catch (Exception)
            {
                // Die grossen Abbilder sind Seiten-Zwischenspeicher und oeffnen sich
                // nicht -- das ist der Normalfall, kein Fehler.
            }
        }
        return null;
    }

    private static SqliteConnection Open(string pfad)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = pfad,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        db.Open();
        return db;
    }

    /// <summary>
    /// Welche Tune-Container auf einem Auto liegen: <c>TuneFileName</c> ist genau der
    /// Containername im Spielstand (siehe <see cref="TuneStorage"/>).
    /// </summary>
    public static HashSet<string> AppliedTunes(string pfad)
    {
        var aus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var db = Open(pfad);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "select TuneFileName from Career_Garage "
                          + "where TuneFileName is not null and TuneFileName <> ''";
        using var r = cmd.ExecuteReader();
        while (r.Read()) { aus.Add(r.GetString(0)); }
        return aus;
    }

    /// <summary>Alle Auto-Nummern, die in dieser Garage stehen.</summary>
    public static List<int> Cars(string pfad)
    {
        var aus = new List<int>();
        using var db = Open(pfad);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "select CarId from Career_Garage order by CarId";
        using var r = cmd.ExecuteReader();
        while (r.Read()) { aus.Add(r.GetInt32(0)); }
        return aus;
    }

    /// <summary>Den Bauplan eines Autos lesen. null, wenn es nicht in der Garage steht.</summary>
    /// <summary>
    /// Die Spalten, in denen ein Motortausch sichtbar wird.
    /// </summary>
    /// <remarks>
    /// GEMESSEN, nicht angenommen: wenn eine Teilefamilie zu einem bekannten Auto
    /// gehoert, steht sie in genau diesen zehn Spalten -- und in allen zehn
    /// gleichzeitig. Die Spalte `Engine` gehoert NICHT dazu; sie traegt in allen 103
    /// gefundenen Faellen eine andere Familie.
    /// </remarks>
    private static readonly HashSet<string> MotorInnereien =
    [
        "Camshaft", "Valves", "Displacement", "Pistons & compression", "Fuel system",
        "Ignition", "Exhaust", "Intake", "Flywheel", "Oil & cooling",
    ];

    public static CarTune? Read(string pfad, int carId,
                                Func<long, string?>? autoName = null)
    {
        using var db = Open(pfad);
        using var cmd = db.CreateCommand();
        // Das zuletzt gefahrene Exemplar zaehlt, wenn dasselbe Auto mehrfach in der
        // Garage steht -- `Id` waechst mit dem Kauf.
        cmd.CommandText = "select * from Career_Garage where CarId = $id "
                          + "order by Id desc limit 1";
        cmd.Parameters.AddWithValue("$id", carId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) { return null; }

        var spalten = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < r.FieldCount; i++) { spalten[r.GetName(i)] = i; }

        double Zahl(string name) =>
            spalten.TryGetValue(name, out var i) && !r.IsDBNull(i) ? r.GetDouble(i) : 0.0;
        long Ganz(string name) =>
            spalten.TryGetValue(name, out var i) && !r.IsDBNull(i) ? r.GetInt64(i) : -1;
        string? Text(string name) =>
            spalten.TryGetValue(name, out var i) && !r.IsDBNull(i) ? r.GetString(i) : null;

        var garageId = (int)Ganz("Id");
        var preise = Preise(pfad, garageId);

        var teile = new List<TunePart>();
        // EIN KAUF, MEHRERE ZEILEN. Ein Breitbau-Satz steht gleichzeitig unter
        // Karosserie, beiden Stossstangen, Haube, Schwellern und beiden Spurweiten --
        // siebenmal dieselbe Teilenummer, gemessen an CarId 3761. Den Preis in jede
        // Zeile zu schreiben liesse 20.000 CR wie 140.000 aussehen. Er steht darum
        // nur beim ersten Vorkommen.
        var schonBerechnet = new HashSet<long>();
        foreach (var (spalte, bereich, name) in PartColumns)
        {
            if (!spalten.TryGetValue(spalte, out var i) || r.IsDBNull(i)) { continue; }
            var wert = r.GetInt64(i);
            if (wert < 0) { continue; }
            var familie = wert / 1000;
            long? preis = null;
            if (preise.TryGetValue(wert, out var p) && schonBerechnet.Add(wert))
            {
                preis = p;
            }
            // WAS DIE FAMILIE BEDEUTET, entscheidet sich daran, ob sie ein Auto ist.
            var fremd = familie != carId && familie != 0;
            var fremdName = fremd ? autoName?.Invoke(familie) : null;
            var herkunft = !fremd ? "own"
                : (fremdName is not null && MotorInnereien.Contains(name)) ? "swap"
                : fremdName is not null ? "swap"
                : "universal";
            teile.Add(new TunePart(bereich, name, wert, (int)(wert % 1000), familie,
                                   herkunft, fremdName, preis));
        }

        var regler = new List<TuneSetting>();
        foreach (var (spalte, bereich, name) in SettingColumns)
        {
            if (!spalten.TryGetValue(spalte, out var i) || r.IsDBNull(i)) { continue; }
            regler.Add(new TuneSetting(bereich, name, r.GetDouble(i)));
        }

        var klasse = (int)Ganz("ClassID");
        return new CarTune(
            carId, garageId,
            // KEIN PI -- siehe oben. Nur der gespeicherte Index, mal 1000.
            (int)Math.Round(Zahl("PerformanceIndex") * 1000.0),
            klasse, ClassName(klasse),
            Ganz("PartsValue"), Zahl("CurbWeight"), Zahl("TopSpeed"), Zahl("SimPeakPower"),
            Text("TuneFileName"), Ganz("VersionedTuneId"), Ganz("VersionedTuneXUID"),
            Ganz("SharedID"), Text("OriginalOwner"), teile, regler);
    }

    private static Dictionary<long, long> Preise(string pfad, int garageId)
    {
        var aus = new Dictionary<long, long>();
        try
        {
            using var db = Open(pfad);
            using var cmd = db.CreateCommand();
            cmd.CommandText = "select PartId, PricePaid from Career_PurchasedParts "
                              + "where GarageId = $g";
            cmd.Parameters.AddWithValue("$g", garageId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (!r.IsDBNull(0)) { aus[r.GetInt64(0)] = r.IsDBNull(1) ? 0 : r.GetInt64(1); }
            }
        }
        catch (Exception)
        {
            // Ohne Kaufliste fehlen nur die Preise, nicht die Teile.
        }
        return aus;
    }
}
