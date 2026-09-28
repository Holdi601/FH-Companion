using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Die volle Telemetrie einer Runde, ein Datensatz je Paket -- zum Nachsimulieren.
/// </summary>
/// <remarks>
/// ## Warum NEBEN den vorhandenen Messpunkten und nicht statt ihnen
///
/// `LapRecorder` nimmt alle **5 Meter** einen Messpunkt (`SampleMetres`), nicht nach
/// der Uhr. Das ist fuer den Vergleich zweier Runden genau richtig: beide werden am
/// selben ORT gemessen, und nur so beantwortet das Delta "wo genau war ich
/// langsamer". Fuer eine Nachsimulation ist es unbrauchbar -- die braucht einen
/// gleichmaessigen Zeittakt.
///
/// Zwei verschiedene Fragen, zwei Spuren. Die vorhandene bleibt unangetastet, damit
/// Delta, Geist und Karten weiterlaufen wie bisher.
///
/// ## Warum eine eigene Datei
///
/// Die Rundendatei wird von allem gelesen, was es hier gibt. Haenge man 11.000
/// Datensaetze mit 56 Spalten hinein, muessten `OwnCars`, `FreeRoamReplay`, das
/// Delta und die Migrationsskripte jedes Mal Megabytes durch den Parser schieben,
/// um an eine Rundenzeit zu kommen. Die volle Spur liegt darum daneben:
///
///     &lt;runde&gt;.json        wie bisher, klein, von allem gelesen
///     &lt;runde&gt;.tele.gz     die volle Spur, nur wer sie braucht
///
/// ## Warum Spaltenkopf und Zahlenreihen, und warum gepackt
///
/// Am 2026-09-15 an einer ECHTEN Runde nachgemessen (187 s, auf 60 Hz gebracht,
/// 73 Spalten, die Radwerte so duenn besetzt wie im Bestand):
///
///     Spaltenkopf + reine Zahlenreihen  3,90 MB
///     dasselbe, gzip                    0,45 MB   (8,7-fach)
///
/// Auf 178 Runden hochgerechnet: 0,08 GB. Zum Vergleich dieselbe Runde als JSON
/// mit Feldnamen je Datensatz: rund 9 MB, also 1,6 GB -- die Feldnamen
/// elftausendmal zu wiederholen ist der groesste Einzelposten.
///
/// Eine erste Schaetzung mit Zufallszahlen kam auf 1,51 MB je Runde. Das war zu
/// pessimistisch: echte Telemetrie ist glatt, haelt Gaenge und Radwerte lange
/// konstant und steht oft auf null -- genau das, wovon ein Packer lebt.
///
/// ## Die Spaltenliste wird gegen das Paket geprueft
///
/// `ForzaPacket.Get` gibt fuer einen unbekannten Namen **0** zurueck, nicht einen
/// Fehler. Ein Tippfehler in der Liste erzeugte also eine Spalte voller Nullen, die
/// erst beim Auswerten auffiele -- und dann sieht sie aus wie ein Sensor, der nichts
/// liefert. <see cref="UnknownColumns"/> nennt sie beim Namen, und der Selbsttest
/// besteht darauf, dass die Liste leer ist.
/// </remarks>
internal sealed class TelemetryTrack
{
    /// <summary>Die Dateiendung der vollen Spur, neben der Rundendatei.</summary>
    public const string Suffix = ".tele.gz";

    /// <summary>
    /// Was je Paket festgehalten wird.
    /// </summary>
    /// <remarks>
    /// Die Reihenfolge ist Teil des Formats: die Datei nennt sie einmal im Kopf,
    /// die Datensaetze sind danach reine Zahlenreihen. Anhaengen ist unbedenklich,
    /// UMSORTIEREN macht alte Dateien unlesbar -- wer umsortiert, muss `version`
    /// hochzaehlen.
    ///
    /// Aufgenommen ist, was eine Nachsimulation braucht (Ort, Lage, Geschwindigkeit,
    /// Drehraten) und was eine Auswertung will (Eingaben, Motor, je Rad). Bewusst
    /// NICHT dabei: die abgeleiteten Groessen aus `Derived.*` -- die lassen sich aus
    /// diesen hier jederzeit nachrechnen, und ein zweiter Weg zu derselben Zahl ist
    /// eine Gelegenheit, zwei verschiedene zu bekommen.
    /// </remarks>
    public static readonly string[] Columns =
    [
        // Zeit und Weg -- aus dem Aufzeichner, nicht aus dem Paket.
        "t", "metres",
        // DIE UHR DES SPIELS. Sie ist zugleich das Mittel gegen Doppelte: dasselbe
        // Netzwerkpaket zweimal zugestellt traegt denselben Wert, und ein zweiter
        // Datensatz mit derselben Spielzeit waere keine Messung, sondern eine
        // Wiederholung -- beim Nachsimulieren ein Standbild mitten in der Fahrt.
        "TimestampMS",
        // Ort und Lage. Yaw/Pitch/Roll sind die Ausrichtung des Fahrzeugs; sie
        // fehlten den 5-Meter-Messpunkten ganz, und ohne sie laesst sich eine
        // Fahrt nicht nachstellen.
        "PositionX", "PositionY", "PositionZ", "Yaw", "Pitch", "Roll",
        // Bewegung.
        "VelocityX", "VelocityY", "VelocityZ",
        "AccelerationX", "AccelerationY", "AccelerationZ",
        "AngularVelocityX", "AngularVelocityY", "AngularVelocityZ",
        "Speed",
        // Antrieb.
        "CurrentEngineRpm", "Gear", "Power", "Torque", "Boost", "Fuel",
        // Eingaben, roh wie im Paket (0..255 bzw. -127..127). Umgerechnet wird
        // beim Auswerten; hier soll stehen, was ankam.
        "Accel", "Brake", "Clutch", "HandBrake", "Steer",
        // Rennzustand.
        "IsRaceOn", "LapNumber", "RacePosition", "CurrentRaceTime",
        "DistanceTraveled", "NormalizedDrivingLine", "NormalizedAIBrakeDifference",
        // Je Rad: vorn links, vorn rechts, hinten links, hinten rechts.
        "NormalizedSuspensionTravelFrontLeft", "NormalizedSuspensionTravelFrontRight",
        "NormalizedSuspensionTravelRearLeft", "NormalizedSuspensionTravelRearRight",
        "TireSlipRatioFrontLeft", "TireSlipRatioFrontRight",
        "TireSlipRatioRearLeft", "TireSlipRatioRearRight",
        "TireSlipAngleFrontLeft", "TireSlipAngleFrontRight",
        "TireSlipAngleRearLeft", "TireSlipAngleRearRight",
        "TireCombinedSlipFrontLeft", "TireCombinedSlipFrontRight",
        "TireCombinedSlipRearLeft", "TireCombinedSlipRearRight",
        "TireTempFrontLeft", "TireTempFrontRight",
        "TireTempRearLeft", "TireTempRearRight",
        "WheelRotationSpeedFrontLeft", "WheelRotationSpeedFrontRight",
        "WheelRotationSpeedRearLeft", "WheelRotationSpeedRearRight",
        "WheelInPuddleFrontLeft", "WheelInPuddleFrontRight",
        "WheelInPuddleRearLeft", "WheelInPuddleRearRight",
        "WheelOnRumbleStripFrontLeft", "WheelOnRumbleStripFrontRight",
        "WheelOnRumbleStripRearLeft", "WheelOnRumbleStripRearRight",
        "SurfaceRumbleFrontLeft", "SurfaceRumbleFrontRight",
        "SurfaceRumbleRearLeft", "SurfaceRumbleRearRight",
    ];

    /// <summary>Das Format dieser Datei. Hochzaehlen, wenn Spalten umsortiert werden.</summary>
    public const int Version = 1;

    /// <summary>
    /// Welche Spaltennamen das Paket NICHT kennt -- sollte leer sein.
    /// </summary>
    /// <remarks>
    /// "t" und "metres" kommen vom Aufzeichner und stehen darum nicht im Paket;
    /// sie sind hier ausgenommen.
    /// </remarks>
    public static IReadOnlyList<string> UnknownColumns { get; } = Columns
        .Where(c => c is not ("t" or "metres"))
        .Where(c => !ForzaPacket.AllDescriptors.Any(
            d => string.Equals(d.Key, c, StringComparison.OrdinalIgnoreCase)))
        .ToArray();

    /// <summary>
    /// Wie viele Datensaetze hoechstens im Speicher gehalten werden.
    /// </summary>
    /// <remarks>
    /// Geleert wird die Spur, sobald eine Runde fertig ist -- das ist der Normalfall
    /// und braucht keine Grenze. Wer aber eine Stunde durch die freie Welt faehrt,
    /// ohne je eine Linie zu kreuzen, schliesst nie eine Runde ab, und dann waechst
    /// die Liste unbegrenzt.
    ///
    /// Gerechnet am 2026-09-15 mit 72 Spalten, also 312 Byte je Datensatz:
    ///
    ///     10 min bei 60 Hz =  36.000 Reihen = 11,2 MB
    ///     15 min bei 60 Hz =  54.000 Reihen = 16,8 MB
    ///     60 min bei 60 Hz = 216.000 Reihen = 67,4 MB
    ///
    /// 15 Minuten sind der Kompromiss: laenger als jede plausible Runde (die
    /// laengste im eigenen Bestand dauerte 187 s), und knapp genug, dass die App
    /// auch nach Stunden freier Fahrt nicht auffaellt.
    ///
    /// BEI UEBERLAUF WIRD VORN GELOESCHT, nicht hinten. Die letzten 15 Minuten
    /// enthalten die laufende Runde immer; die Zeit davor gehoert zu nichts.
    /// </remarks>
    public const int MaxRows = 54_000;

    private readonly List<float[]> _reihen = new();
    private double _letzteSpielzeit = double.NaN;

    public int Count => _reihen.Count;

    /// <summary>Wie viele Pakete als Wiederholung verworfen wurden.</summary>
    public int Duplicates { get; private set; }

    /// <summary>
    /// Ob am Anfang Datensaetze weggefallen sind, weil die Grenze erreicht war.
    /// </summary>
    /// <remarks>
    /// Steht im Kopf der Datei. Eine stillschweigend beschnittene Spur saehe aus
    /// wie eine Fahrt, die spaeter begann -- und niemand koennte den Unterschied
    /// sehen.
    /// </remarks>
    public bool Truncated { get; private set; }

    /// <summary>
    /// Die gesammelten Datensaetze ABGEBEN und hier von vorn anfangen.
    /// </summary>
    /// <remarks>
    /// Nicht dieselbe Spur weiterreichen: sie wird beim Start der naechsten Runde
    /// geleert, und die fertige Runde haette dann eine leere Datei -- ohne dass
    /// irgendwo ein Fehler erschiene. Abgeben macht die Uebergabe eindeutig,
    /// unabhaengig davon, in welcher Reihenfolge der Aufzeichner aufraeumt.
    /// </remarks>
    public TelemetryTrack Detach()
    {
        var abgegeben = new TelemetryTrack();
        abgegeben._reihen.AddRange(_reihen);
        abgegeben.Duplicates = Duplicates;
        abgegeben.Truncated = Truncated;
        Clear();
        return abgegeben;
    }

    public void Clear()
    {
        _reihen.Clear();
        _letzteSpielzeit = double.NaN;
        Duplicates = 0;
        Truncated = false;
    }

    /// <summary>
    /// Einen Datensatz aufnehmen -- einen je Paket, und JEDES Paket.
    /// </summary>
    /// <remarks>
    /// Keine Drosselung: je dichter die Spur, desto besser laesst sich die Fahrt
    /// nachstellen. Was NICHT hineingehoert, ist dasselbe Paket zweimal --
    /// zugestellt wird es doppelt, wenn das Spiel es wiederholt oder der Socket es
    /// mehrfach liefert.
    ///
    /// Erkannt wird das an <c>TimestampMS</c>, der Uhr des Spiels selbst: zwei
    /// Pakete mit derselben Spielzeit sind dasselbe Paket. Nicht an der eigenen
    /// Uhr und nicht am Inhalt -- ein stehendes Auto schickt vollkommen gleiche
    /// Pakete, und die sind trotzdem verschiedene Messungen.
    /// </remarks>
    /// <returns><c>false</c>, wenn das Paket als Wiederholung verworfen wurde.</returns>
    public bool Add(ForzaPacket packet, float seconds, float metres)
    {
        var spielzeit = packet.Get("TimestampMS");
        if (spielzeit > 0 && spielzeit == _letzteSpielzeit)
        {
            Duplicates++;
            return false;
        }
        _letzteSpielzeit = spielzeit;

        var reihe = new float[Columns.Length];
        reihe[0] = seconds;
        reihe[1] = metres;
        for (var i = 2; i < Columns.Length; i++)
        {
            reihe[i] = (float)packet.Get(Columns[i]);
        }
        if (_reihen.Count >= MaxRows)
        {
            // In einem Block statt bei jedem Datensatz einen: `RemoveRange(0, 1)`
            // verschiebt 54.000 Verweise, und das sechzigmal je Sekunde.
            _reihen.RemoveRange(0, MaxRows / 10);
            Truncated = true;
        }
        _reihen.Add(reihe);
        return true;
    }

    /// <summary>
    /// Die Spur neben die Rundendatei schreiben. Gibt den Pfad zurueck, oder null.
    /// </summary>
    /// <remarks>
    /// Scheitern darf hier NIE die Runde mitnehmen: die Rundendatei ist das
    /// Wichtige, die volle Spur ein Zusatz. Darum wird die Ausnahme geschluckt und
    /// null zurueckgegeben -- der Aufrufer schreibt das ins Protokoll.
    /// </remarks>
    public string? Save(string lapPath)
    {
        if (_reihen.Count == 0) { return null; }
        var ziel = lapPath + Suffix;
        try
        {
            using (var datei = File.Create(ziel)) { Packen(datei); }
            return ziel;
        }
        catch (Exception)
        {
            try { if (File.Exists(ziel)) { File.Delete(ziel); } } catch (Exception) { }
            return null;
        }
    }

    /// <summary>
    /// Die Spur gepackt im Speicher -- genau die Bytes, die <see cref="Save"/> schreibt.
    /// </summary>
    /// <remarks>
    /// Fuer die Einreichung (seit 2026-09-28): jede Runde, die an die Seite geht,
    /// traegt ihre volle Telemetrie mit, auch wenn sie nicht archiviert wird.
    /// </remarks>
    public byte[]? Gepackt()
    {
        if (_reihen.Count == 0) { return null; }
        using var speicher = new MemoryStream();
        Packen(speicher);
        return speicher.ToArray();
    }

    private void Packen(Stream ausgabe)
    {
        using var packer = new GZipStream(ausgabe, CompressionLevel.Optimal, leaveOpen: true);
        using var schreiber = new StreamWriter(packer, new UTF8Encoding(false));

        // Von Hand gebaut statt ueber einen Serialisierer: das sind Zehntausende
        // Zeilen, und die Zahlen sollen mit fester Stellenzahl und PUNKT als
        // Dezimaltrennzeichen herauskommen, unabhaengig von der
        // Spracheinstellung. "1,234" in einer Zahlenreihe waere zwei Werte.
        schreiber.Write("{\"version\":");
        schreiber.Write(Version);
        schreiber.Write(",\"rows\":");
        schreiber.Write(_reihen.Count);
        schreiber.Write(",\"duplicatesDropped\":");
        schreiber.Write(Duplicates);
        schreiber.Write(",\"truncated\":");
        schreiber.Write(Truncated ? "true" : "false");
        schreiber.Write(",\"columns\":[");
        for (var i = 0; i < Columns.Length; i++)
        {
            if (i > 0) { schreiber.Write(','); }
            schreiber.Write('"');
            schreiber.Write(Columns[i]);
            schreiber.Write('"');
        }
        schreiber.Write("],\"data\":[");
        for (var r = 0; r < _reihen.Count; r++)
        {
            if (r > 0) { schreiber.Write(','); }
            schreiber.Write('[');
            var reihe = _reihen[r];
            for (var c = 0; c < reihe.Length; c++)
            {
                if (c > 0) { schreiber.Write(','); }
                schreiber.Write(Kurz(reihe[c]));
            }
            schreiber.Write(']');
        }
        schreiber.Write("]}");
    }

    /// <summary>
    /// Eine Zahl so kurz wie moeglich, ohne dass etwas verlorengeht, das zaehlt.
    /// </summary>
    /// <remarks>
    /// Drei Nachkommastellen: ein Millimeter beim Ort, ein Tausendstel Grad bei der
    /// Lage. "0" statt "0.000" spart in einer Telemetriespur viel -- Handbremse,
    /// Kupplung und Pfuetzen stehen die meiste Zeit auf null, und das sind bei 56
    /// Spalten und elftausend Zeilen keine Kleinigkeit.
    /// </remarks>
    private static string Kurz(float wert)
    {
        if (wert == 0f) { return "0"; }
        var gerundet = MathF.Round(wert, 3);
        return gerundet == MathF.Round(gerundet)
            ? ((int)gerundet).ToString(CultureInfo.InvariantCulture)
            : gerundet.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
