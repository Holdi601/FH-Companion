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
/// ## JEDES Paket, JEDES Feld, JEDES Byte (Fassung 2, seit 2026-09-30)
///
/// Fassung 1 nahm 75 ausgewaehlte Felder, rundete auf drei Nachkommastellen, legte sie
/// als 32-Bit-Zahl ab (die Spieluhr TimestampMS verlor damit nach 4,6 Stunden Spielzeit
/// ihre Millisekunden) und verwarf jedes Paket, dessen Spielzeit der des vorigen glich.
/// Das letzte war das Teuerste: das Spiel schickt rund 110 Pakete je Sekunde, seine Uhr
/// springt aber nur etwa 64-mal -- in einer echten Runde fielen 4.689 von 11.247 Paketen
/// weg, ohne dass je geprueft war, ob sie wirklich gleich waren. Der Nutzer (2026-09-30):
/// aufgezeichnet und eingereicht wird ALLES, was vom Spiel kam.
///
/// Jetzt: das Paket wird unveraendert aufgehoben (<see cref="ForzaPacket.Raw"/>), und
/// erst beim Schreiben werden alle Felder gelesen -- jedes aus der Paketbeschreibung, in
/// voller Genauigkeit (Gleitkomma so kurz wie moeglich und doch exakt, Ganzzahlen genau),
/// dazu jedes Byte, das noch keinen Namen hat, als eigene Spalte ("Byte323"). Wiederholte
/// Spielzeiten bleiben drin; der Kopf der Datei zaehlt sie nur.
internal sealed class TelemetryTrack
{
    /// <summary>Die Dateiendung der vollen Spur, neben der Rundendatei.</summary>
    public const string Suffix = ".tele.gz";

    /// <summary>
    /// Die Spalten einer Spur aus Paketen der ueblichen Laenge (324 Bytes).
    /// </summary>
    /// <remarks>
    /// "t" und "metres" vom Aufzeichner (Rundenzeit und Weg), dann jedes Feld des Pakets in
    /// Paketreihenfolge, dann jedes Byte ohne Feld. Schickt das Spiel laengere Pakete,
    /// kommen deren Bytes als weitere Spalten dazu (<see cref="SpaltenFuer"/>). Gelesen wird
    /// eine Spur immer ueber die Namen im Kopf, nie ueber die Stelle.
    /// </remarks>
    public static readonly string[] Columns = SpaltenFuer(324);

    /// <summary>Die Spalten fuer Pakete dieser Laenge.</summary>
    internal static string[] SpaltenFuer(int laenge) =>
        new[] { "t", "metres" }
            .Concat(ForzaPacket.RawDescriptors.Select(d => d.Key))
            .Concat(FreieBytes(laenge).Select(i => "Byte" + i))
            .ToArray();

    /// <summary>Die Bytes eines Pakets dieser Laenge, die zu keinem Feld gehoeren.</summary>
    internal static IEnumerable<int> FreieBytes(int laenge)
    {
        var belegt = new bool[Math.Max(0, laenge)];
        foreach (var d in ForzaPacket.RawDescriptors)
        {
            for (var i = d.Offset; i < d.Offset + ForzaPacket.Size(d.Type) && i < belegt.Length; i++) { belegt[i] = true; }
        }
        return Enumerable.Range(0, belegt.Length).Where(i => !belegt[i]);
    }

    /// <summary>Das Format dieser Datei. Fassung 2: alle Felder, alle Pakete, volle Genauigkeit.</summary>
    public const int Version = 2;

    /// <summary>
    /// Welche Spaltennamen das Paket NICHT kennt -- sollte leer sein.
    /// </summary>
    /// <remarks>
    /// "t" und "metres" kommen vom Aufzeichner, "ByteN" sind Bytes ohne Feld; sie sind
    /// hier ausgenommen. Seit Fassung 2 folgt die Liste aus der Paketbeschreibung selbst.
    /// </remarks>
    public static IReadOnlyList<string> UnknownColumns { get; } = Columns
        .Where(c => c is not ("t" or "metres") && !c.StartsWith("Byte", StringComparison.Ordinal))
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
    /// Seit Fassung 2 je Datensatz das rohe Paket (324 Bytes) und zwei Zahlen, rund
    /// 370 Byte mit Verwaltung. Das Spiel schickt etwa 110 Pakete je Sekunde:
    ///
    ///     10 min bei 110 Hz =  66.000 Reihen = 24 MB
    ///     18 min bei 110 Hz = 120.000 Reihen = 44 MB
    ///
    /// 18 Minuten sind der Kompromiss: laenger als jede plausible Runde (die
    /// laengste im eigenen Bestand dauerte 187 s), und knapp genug, dass die App
    /// auch nach Stunden freier Fahrt nicht auffaellt.
    ///
    /// BEI UEBERLAUF WIRD VORN GELOESCHT, nicht hinten. Die letzten 15 Minuten
    /// enthalten die laufende Runde immer; die Zeit davor gehoert zu nichts.
    /// </remarks>
    public const int MaxRows = 120_000;

    /// <summary>Ein Paket der Runde: Rundenzeit und Weg beim Empfang, dazu das Paket selbst.</summary>
    private readonly record struct Reihe(float T, float Metres, byte[] Raw);

    private readonly List<Reihe> _reihen = new();
    private uint? _letzteSpielzeit;
    private byte[]? _letztesPaket;
    private byte[]? _gepackt;

    public int Count => _reihen.Count;

    /// <summary>Wovon die Spur handelt -- steht im Kopf der Datei, damit sie auch allein lesbar ist.</summary>
    internal sealed record Kopfdaten(int CarOrdinal, string? CarName, string? Track, string? CarClass,
                                     int Pi, float LapSeconds);

    private Kopfdaten? _kopf;

    /// <summary>
    /// Auto, Strecke, Klasse und Zeit in den Kopf schreiben (seit 2026-10-01). Vor dem Packen
    /// aufrufen; ein spaeterer Aufruf packt neu.
    /// </summary>
    public void Beschreibe(Kopfdaten kopf)
    {
        lock (_packSchloss)
        {
            _kopf = kopf;
            _gepackt = null;
        }
    }

    /// <summary>Wie viele Pakete dieselbe Spielzeit trugen wie das vorige -- aufgenommen, nur gezaehlt.</summary>
    public int RepeatedTimestamps { get; private set; }

    /// <summary>Wie viele davon Byte fuer Byte dem vorigen glichen.</summary>
    public int IdenticalRepeats { get; private set; }

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
        abgegeben.RepeatedTimestamps = RepeatedTimestamps;
        abgegeben.IdenticalRepeats = IdenticalRepeats;
        abgegeben.Truncated = Truncated;
        Clear();
        return abgegeben;
    }

    public void Clear()
    {
        _reihen.Clear();
        _letzteSpielzeit = null;
        _letztesPaket = null;
        _gepackt = null;
        RepeatedTimestamps = 0;
        IdenticalRepeats = 0;
        Truncated = false;
    }

    /// <summary>
    /// Ein Paket aufnehmen -- JEDES, unveraendert.
    /// </summary>
    /// <remarks>
    /// Im Paketpfad, darum nur ein Verweis auf die schon kopierten Bytes und zwei Zahlen.
    /// Gelesen wird erst beim Schreiben. Eine wiederholte Spielzeit wird gezaehlt, nicht
    /// verworfen (siehe oben: Fassung 1 verwarf so 42 % der Pakete).
    /// </remarks>
    /// <returns>Immer <c>true</c> -- aufgenommen wird jedes Paket.</returns>
    public bool Add(ForzaPacket packet, float seconds, float metres)
    {
        var roh = packet.Raw;
        if (roh.Length >= 8)
        {
            var spielzeit = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(roh.AsSpan(4, 4));
            if (_letzteSpielzeit == spielzeit)
            {
                RepeatedTimestamps++;
                if (_letztesPaket is not null && roh.AsSpan().SequenceEqual(_letztesPaket)) { IdenticalRepeats++; }
            }
            _letzteSpielzeit = spielzeit;
        }
        _letztesPaket = roh;
        if (_reihen.Count >= MaxRows)
        {
            // In einem Block statt bei jedem Datensatz einen: `RemoveRange(0, 1)`
            // verschiebt 120.000 Verweise, und das hundertmal je Sekunde.
            _reihen.RemoveRange(0, MaxRows / 10);
            Truncated = true;
        }
        _reihen.Add(new Reihe(seconds, metres, roh));
        _gepackt = null;
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
            // Erst ganz schreiben, dann umbenennen: endet die App mittendrin, liegt keine
            // halbe Datei da, die wie eine kurze Runde aussaehe.
            var tmp = ziel + ".tmp";
            File.WriteAllBytes(tmp, Gepackt()!);
            File.Move(tmp, ziel, overwrite: true);
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
    /// <remarks>
    /// Einmal gepackt und gemerkt: Archiv und Einreichung bekommen dieselben Bytes, und
    /// das Packen (alle Felder aller Pakete) laeuft nur einmal je Runde.
    /// </remarks>
    public byte[]? Gepackt()
    {
        // Archiv und Einreichung packen im Hintergrund, womoeglich gleichzeitig -- gepackt
        // wird trotzdem nur einmal.
        lock (_packSchloss)
        {
            if (_reihen.Count == 0) { return null; }
            if (_gepackt is { } fertig) { return fertig; }
            using var speicher = new MemoryStream();
            Packen(speicher);
            return _gepackt = speicher.ToArray();
        }
    }

    private readonly object _packSchloss = new();

    private void Packen(Stream ausgabe)
    {
        using var packer = new GZipStream(ausgabe, CompressionLevel.Optimal, leaveOpen: true);
        using var schreiber = new StreamWriter(packer, new UTF8Encoding(false));

        // Die laengste Paketlaenge dieser Runde bestimmt die Spalten: jedes Byte ohne Feld
        // bekommt eine. Kuerzere Pakete gibt es nicht (TryParse verlangt 324 Bytes).
        var laenge = _reihen.Max(r => r.Raw.Length);
        var spalten = SpaltenFuer(laenge);
        var felder = ForzaPacket.RawDescriptors;
        var freie = FreieBytes(laenge).ToArray();

        // Von Hand gebaut statt ueber einen Serialisierer: das sind Zehntausende Zeilen,
        // und die Zahlen sollen mit PUNKT als Dezimaltrennzeichen herauskommen,
        // unabhaengig von der Spracheinstellung. "1,234" in einer Zahlenreihe waere zwei Werte.
        schreiber.Write("{\"version\":");
        schreiber.Write(Version);
        schreiber.Write(",\"rows\":");
        schreiber.Write(_reihen.Count);
        schreiber.Write(",\"packetBytes\":");
        schreiber.Write(laenge);
        schreiber.Write(",\"repeatedTimestamps\":");
        schreiber.Write(RepeatedTimestamps);
        schreiber.Write(",\"identicalRepeats\":");
        schreiber.Write(IdenticalRepeats);
        schreiber.Write(",\"truncated\":");
        schreiber.Write(Truncated ? "true" : "false");
        if (_kopf is { } k)
        {
            // WAS DAS FUER EINE RUNDE IST -- ein Werkzeug, das nur diese Datei bekommt,
            // soll das Auto beim Namen nennen koennen und nicht nur bei der Nummer.
            schreiber.Write(",\"car\":{\"ordinal\":");
            schreiber.Write(k.CarOrdinal.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(k.CarName))
            {
                schreiber.Write(",\"name\":");
                schreiber.Write(System.Text.Json.JsonSerializer.Serialize(k.CarName, AutoNamen.Lesbar));
            }
            if (!string.IsNullOrWhiteSpace(k.CarClass))
            {
                schreiber.Write(",\"class\":");
                schreiber.Write(System.Text.Json.JsonSerializer.Serialize(k.CarClass, AutoNamen.Lesbar));
            }
            if (k.Pi > 0)
            {
                schreiber.Write(",\"pi\":");
                schreiber.Write(k.Pi.ToString(CultureInfo.InvariantCulture));
            }
            schreiber.Write('}');
            if (!string.IsNullOrWhiteSpace(k.Track))
            {
                schreiber.Write(",\"track\":");
                schreiber.Write(System.Text.Json.JsonSerializer.Serialize(k.Track, AutoNamen.Lesbar));
            }
            if (k.LapSeconds > 0)
            {
                schreiber.Write(",\"lapSeconds\":");
                schreiber.Write(Gleitkomma(k.LapSeconds));
            }
        }
        schreiber.Write(",\"columns\":[");
        for (var i = 0; i < spalten.Length; i++)
        {
            if (i > 0) { schreiber.Write(','); }
            schreiber.Write('"');
            schreiber.Write(spalten[i]);
            schreiber.Write('"');
        }
        schreiber.Write("],\"data\":[");
        for (var r = 0; r < _reihen.Count; r++)
        {
            if (r > 0) { schreiber.Write(','); }
            var reihe = _reihen[r];
            schreiber.Write('[');
            schreiber.Write(Gleitkomma(reihe.T));
            schreiber.Write(',');
            schreiber.Write(Gleitkomma(reihe.Metres));
            foreach (var d in felder)
            {
                schreiber.Write(',');
                schreiber.Write(Feld(reihe.Raw, d));
            }
            foreach (var i in freie)
            {
                schreiber.Write(',');
                schreiber.Write(i < reihe.Raw.Length ? reihe.Raw[i].ToString(CultureInfo.InvariantCulture) : "0");
            }
            schreiber.Write(']');
        }
        schreiber.Write("]}");
    }

    /// <summary>Ein Feld, wie es im Paket steht: Ganzzahlen genau, Gleitkomma exakt.</summary>
    private static string Feld(byte[] roh, TelemetryDescriptor d)
    {
        var wert = ForzaPacket.Decode(roh, d);
        return d.Type is TelemetryValueType.Float32 or TelemetryValueType.FlagOrFloat32
            ? Gleitkomma((float)wert)
            : ((long)wert).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Eine 32-Bit-Gleitkommazahl so kurz wie moeglich und doch EXAKT: wieder eingelesen
    /// ergibt sie dieselben Bits. "0" statt "0.0" -- Handbremse, Kupplung und Pfuetzen
    /// stehen die meiste Zeit auf null.
    /// </summary>
    internal static string Gleitkomma(float wert)
    {
        if (wert == 0f) { return "0"; }
        if (!float.IsFinite(wert)) { return "0"; }
        return wert.ToString("R", CultureInfo.InvariantCulture);
    }
}
