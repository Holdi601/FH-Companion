using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>Ein gefahrenes Rennen: Startplatz, Zielplatz, wie viele im Feld.</summary>
/// <remarks>
/// Seit 2026-09-30, fuer den Reiter "Race statistics". Die Telemetrie nennt den Platz
/// (`RacePosition`), aber nicht, wie viele mitfahren. Die Feldgroesse kommt vom
/// Startaufstellungs-Schirm vor dem Rennen (<see cref="RaceGrid"/>); aeltere Rennen,
/// aus dem Rundenbestand nachgebaut, kennen sie nicht.
/// </remarks>
internal sealed class RaceRecord
{
    /// <summary>Auto und Zeitstempel der letzten Runde -- dieselbe Kennung wie ihr Dateiname.</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("at")] public DateTime At { get; set; }
    /// <summary>Die Kurskennung; bei einer abgebrochenen Fahrt die der Strecke, zu der sie gehoert.</summary>
    [JsonPropertyName("course")] public string Course { get; set; } = string.Empty;
    [JsonPropertyName("track")] public string? Track { get; set; }
    [JsonPropertyName("mode")] public string Mode { get; set; } = "unknown";
    [JsonPropertyName("car")] public int Car { get; set; }
    [JsonPropertyName("pi")] public int Pi { get; set; }
    [JsonPropertyName("class")] public string Klass { get; set; } = string.Empty;
    [JsonPropertyName("start")] public int? Start { get; set; }
    /// <summary>"grid" (Startaufstellung), "telemetry" (erstes Paket), "telemetry-late" (erste aufgezeichnete Zeile).</summary>
    [JsonPropertyName("startFrom")] public string? StartFrom { get; set; }
    [JsonPropertyName("finish")] public int? Finish { get; set; }
    /// <summary>Wie viele im Feld -- nur vom Startaufstellungs-Schirm, sonst leer.</summary>
    [JsonPropertyName("drivers")] public int? Drivers { get; set; }
    /// <summary>Der schlechteste Platz, der im Rennen zu sehen war: mindestens so viele fuhren mit.</summary>
    [JsonPropertyName("highest")] public int Highest { get; set; }
    [JsonPropertyName("laps")] public int Laps { get; set; }
    [JsonPropertyName("seconds")] public float Seconds { get; set; }
    /// <summary>Ueber die Ziellinie -- nicht neu gestartet, nicht verlassen.</summary>
    [JsonPropertyName("finished")] public bool Finished { get; set; } = true;
    /// <summary>"live" (beim Fahren aufgezeichnet) oder "archive" (aus dem Rundenbestand nachgebaut).</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = "live";

    /// <summary>Die Kennung aus Auto und Zeitstempel der letzten Runde.</summary>
    internal static string IdFor(int car, DateTimeOffset at) => $"{car}|{at:yyyy-MM-dd_HH-mm-ss}";
}

/// <summary>Die aufgezeichneten Rennen: eine Zeile JSON je Rennen.</summary>
internal static class RaceLog
{
    public static string LivePath => Path.Combine(AppInfo.DataFolder, "races.jsonl");
    public static string ArchivePath => Path.Combine(AppInfo.DataFolder, "races_archive.json");

    private static readonly object Schloss = new();

    public static void Append(RaceRecord r, string? pfad = null)
    {
        try
        {
            lock (Schloss)
            {
                File.AppendAllText(pfad ?? LivePath, JsonSerializer.Serialize(r) + "\n");
            }
        }
        catch (Exception)
        {
            // Ein verlorenes Rennen in der Statistik ist aergerlich, ein Absturz im Rennen mehr.
        }
    }

    public static List<RaceRecord> LoadLive(string? pfad = null)
    {
        var raus = new List<RaceRecord>();
        try
        {
            pfad ??= LivePath;
            if (!File.Exists(pfad)) { return raus; }
            string[] zeilen;
            lock (Schloss) { zeilen = File.ReadAllLines(pfad); }
            foreach (var z in zeilen)
            {
                if (string.IsNullOrWhiteSpace(z)) { continue; }
                try
                {
                    if (JsonSerializer.Deserialize<RaceRecord>(z) is { } r) { raus.Add(r); }
                }
                catch (Exception)
                {
                    // Eine abgeschnittene Zeile (Absturz beim Schreiben) kostet ein Rennen, nicht alle.
                }
            }
        }
        catch (Exception)
        {
        }
        return raus;
    }
}

/// <summary>
/// Verfolgt das laufende Rennen an jedem Paket: Startplatz, letzter Platz, schlechtester Platz.
/// </summary>
/// <remarks>
/// Im Paketpfad -- darum nur Vergleiche. Ein Rennen beginnt, wenn die Rennuhr
/// (`CurrentRaceTime`) zurueckspringt; ein Rundkurs laesst sie weiterlaufen.
/// </remarks>
internal sealed class RaceWatcher
{
    private float _letzteZeit = -1f;
    private bool _aktiv;

    /// <summary>Der Platz im ersten Paket des Rennens -- nur, wenn es von Anfang an gesehen wurde.</summary>
    public int? Start { get; private set; }
    public DateTime StartUtc { get; private set; } = DateTime.MinValue;
    public int Last { get; private set; }
    public int Highest { get; private set; }
    public float RaceSeconds { get; private set; }
    public int Laps { get; private set; }

    public void Packet(int platz, float rennzeit, DateTime jetztUtc)
    {
        if (platz < 1) { return; }
        if (!_aktiv || rennzeit + 1f < _letzteZeit)
        {
            _aktiv = true;
            // Spaeter eingestiegen (App mitten im Rennen gestartet): kein Startplatz.
            Start = rennzeit <= 8f ? platz : null;
            StartUtc = jetztUtc - TimeSpan.FromSeconds(Math.Max(0f, rennzeit));
            Highest = platz;
            Laps = 0;
        }
        _letzteZeit = rennzeit;
        Last = platz;
        RaceSeconds = rennzeit;
        if (platz > Highest) { Highest = platz; }
    }

    public void LapDone() => Laps++;

    /// <summary>Das Rennen ist vorbei: das naechste Paket beginnt ein neues.</summary>
    public void End()
    {
        _aktiv = false;
        _letzteZeit = -1f;
    }
}

/// <summary>Was der Startaufstellungs-Schirm vor dem Rennen zeigt.</summary>
/// <param name="Drivers">Wie viele im Feld.</param>
/// <param name="OwnSlot">Die markierte Zeile -- der eigene Startplatz, solange niemand den Zeiger bewegt; oder null.</param>
internal readonly record struct GridRead(int Drivers, int? OwnSlot);

/// <summary>
/// Der Startaufstellungs-Schirm vor jedem Rennen, nur aus Pixeln gelesen -- in jeder Sprache gleich.
/// </summary>
/// <remarks>
/// Seit 2026-09-30. Der Schirm: eine limettengruene Kopfzeile (Driver, Car, Class), rechts
/// daneben ein zweiter limettengruener Kasten (Event 1/3, Karte), darunter zwoelf Zeilen.
/// Jeder Fahrer eine WEISSE Zeile, der eigene Platz SCHWARZ mit Limettenrahmen, leere
/// Plaetze dunkel tuerkis; dahinter der tuerkise Hintergrund der Menues.
///
/// Geprueft an 1.314 Bildern (alle 2 s aus 44 Minuten Aufnahme): alle vier Aufstellungen
/// gefunden (5, 9, 9 und 10 Fahrer), kein einziger Fehltreffer. Der Ergebnisschirm nach dem
/// Rennen hat dieselbe Kopfzeile, liegt aber auf der dunklen Rennszene -- er faellt am
/// Hintergrund durch (0 von 25 Proben tuerkis).
///
/// Die markierte Zeile ist ein Zeiger, den man bewegen kann; der Startplatz aus der
/// Telemetrie prueft sie gegen (siehe OverlayController).
/// </remarks>
internal static class RaceGrid
{
    /// <summary>So klein wird der Schirm aufgenommen: 384 Punkte breit genuegen fuer zwoelf Zeilen.</summary>
    public static readonly Size Aufnahme = new(384, 216);

    private static readonly float[] KopfY = { 228f / 1080f, 245f / 1080f, 262f / 1080f };

    public static GridRead? Read(Bitmap bild)
    {
        if (bild.Width < 64 || bild.Height < 36) { return null; }
        using var klar = bild.PixelFormat == PixelFormat.Format24bppRgb ? null
            : bild.Clone(new Rectangle(0, 0, bild.Width, bild.Height), PixelFormat.Format24bppRgb);
        var quelle = klar ?? bild;
        var daten = quelle.LockBits(new Rectangle(0, 0, quelle.Width, quelle.Height),
                                    ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var w = quelle.Width;
            var h = quelle.Height;
            var schritt = daten.Stride;
            var puffer = new byte[schritt * h];
            System.Runtime.InteropServices.Marshal.Copy(daten.Scan0, puffer, 0, puffer.Length);
            (int R, int G, int B) Px(float fx, float fy)
            {
                var x = Math.Clamp((int)(fx * w), 0, w - 1);
                var y = Math.Clamp((int)(fy * h), 0, h - 1);
                var i = (y * schritt) + (x * 3);
                return (puffer[i + 2], puffer[i + 1], puffer[i]);
            }
            static bool Limette((int R, int G, int B) p) => p.R > 150 && p.G > 220 && p.B < 90;
            static bool Tuerkis((int R, int G, int B) p) => p.G - p.R > 50 && p.G > 90 && p.B - p.R > 30;
            // Die schwarze Schrift der Kopfzeile verdeckt einzelne Proben -- darum drei Hoehen je Spalte.
            bool Spalte(float fx) => KopfY.Any(y => Limette(Px(fx, y)));

            var kopf = Enumerable.Range(0, 20).Count(i => Spalte(0.18f + (0.45f * i / 19f)));
            var kasten = Enumerable.Range(0, 10).Count(i => Spalte(0.66f + (0.16f * i / 9f)));
            var grund = 0;
            for (var i = 0; i < 5; i++)
            {
                for (var j = 0; j < 5; j++)
                {
                    if (Tuerkis(Px(0.02f + (0.10f * i / 4f), 0.35f + (0.30f * j / 4f)))) { grund++; }
                }
            }
            if (kopf < 17 || kasten < 9 || grund < 18) { return null; }

            // Die zwoelf Plaetze: weiss (Fahrer), dunkel neutral (man selbst), dunkel tuerkis (leer).
            var arten = new char[12];
            var x0 = (int)(0.18f * w);
            var x1 = (int)(0.63f * w);
            for (var s = 0; s < 12; s++)
            {
                var y = Math.Clamp((int)((300f + (54f * s)) / 1080f * h), 0, h - 1);
                long r = 0, g = 0, b = 0;
                var n = 0;
                for (var x = x0; x < x1; x++)
                {
                    var i = (y * schritt) + (x * 3);
                    b += puffer[i]; g += puffer[i + 1]; r += puffer[i + 2];
                    n++;
                }
                var mr = r / (double)n;
                var mg = g / n;
                var mb = b / n;
                var hell = (mr + mg + mb) / 3;
                var tint = mg - mr;
                arten[s] = hell > 150 && Math.Abs(tint) < 25 ? 'w'
                         : hell < 120 && tint < 20 ? 'b'
                         : hell < 120 && tint >= 25 ? '-'
                         : '?';
            }
            var belegt = 0;
            while (belegt < 12 && arten[belegt] is 'w' or 'b') { belegt++; }
            if (belegt == 0) { return null; }
            for (var s = belegt; s < 12; s++) { if (arten[s] != '-') { return null; } }
            var schwarz = Enumerable.Range(0, belegt).Where(s => arten[s] == 'b').ToList();
            if (schwarz.Count > 1) { return null; }
            return new GridRead(belegt, schwarz.Count == 1 ? schwarz[0] + 1 : null);
        }
        finally
        {
            quelle.UnlockBits(daten);
        }
    }
}

/// <summary>
/// Aeltere Rennen aus dem Rundenbestand nachbauen: aus den vollen Spuren (.tele.gz) der
/// letzten Runde jedes Rennens und, bei Rundkursen, der ersten.
/// </summary>
/// <remarks>
/// Einmal je Datei; das Ergebnis liegt in races_archive.json und wird beim naechsten Mal
/// nur um neue Rennen ergaenzt. Die Feldgroesse kennen diese Rennen nicht (sie stand nur
/// auf dem Schirm) -- bekannt ist, wie viele mindestens mitfuhren: der schlechteste Platz,
/// der zu sehen war. Der Startplatz ist die erste AUFGEZEICHNETE Zeile, die einige Sekunden
/// nach dem Start liegen kann.
/// </remarks>
internal static class RaceArchive
{
    private sealed class Stand
    {
        [JsonPropertyName("races")] public List<RaceRecord> Races { get; set; } = new();
        [JsonPropertyName("seen")] public HashSet<string> Seen { get; set; } = new(StringComparer.Ordinal);
    }

    private readonly record struct Spur(int FirstPos, int FirstLap, float FirstTime, int LastPos, float LastTime, int MaxPos);

    /// <summary>Den gemerkten Stand laden und um neue Rennen ergaenzen.</summary>
    /// <param name="fortschritt">(erledigt, gesamt) -- nur, wenn es etwas zu tun gibt.</param>
    public static List<RaceRecord> Update(string wurzel, string stand, ISet<string> liveIds,
                                          Action<int, int>? fortschritt = null, CancellationToken abbruch = default)
    {
        var s = Laden(stand);
        try
        {
            if (!Directory.Exists(wurzel)) { return s.Races; }
            var neu = Directory.EnumerateFiles(wurzel, "*_sprint.json", SearchOption.AllDirectories)
                .Select(p => (Pfad: p, Id: IdAus(p)))
                .Where(t => t.Id is not null && !s.Seen.Contains(t.Id) && !liveIds.Contains(t.Id))
                .ToList();
            if (neu.Count == 0) { return s.Races; }
            for (var i = 0; i < neu.Count; i++)
            {
                if (abbruch.IsCancellationRequested) { break; }
                fortschritt?.Invoke(i, neu.Count);
                try
                {
                    if (Nachbauen(wurzel, neu[i].Pfad, neu[i].Id!) is { } r) { s.Races.Add(r); }
                }
                catch (Exception)
                {
                    // Eine unlesbare Datei kostet ein Rennen.
                }
                s.Seen.Add(neu[i].Id!);
            }
            Speichern(stand, s);
        }
        catch (Exception)
        {
        }
        return s.Races;
    }

    private static Stand Laden(string pfad)
    {
        try
        {
            if (File.Exists(pfad) && JsonSerializer.Deserialize<Stand>(File.ReadAllText(pfad)) is { } s)
            {
                s.Seen = new HashSet<string>(s.Seen, StringComparer.Ordinal);
                return s;
            }
        }
        catch (Exception)
        {
        }
        return new Stand();
    }

    private static void Speichern(string pfad, Stand s)
    {
        try
        {
            var tmp = pfad + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s));
            File.Move(tmp, pfad, overwrite: true);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>"Auto|Zeitstempel" aus dem Pfad: …/carN/tune/tag/yyyy-MM-dd_HH-mm-ss_….json.</summary>
    internal static string? IdAus(string pfad)
    {
        var name = Path.GetFileName(pfad);
        if (name.Length < 19) { return null; }
        var teile = pfad.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var auto = teile.Reverse().Skip(1).FirstOrDefault(t => t.StartsWith("car", StringComparison.OrdinalIgnoreCase)
                                                             && int.TryParse(t.AsSpan(3), out _));
        return auto is null ? null : $"{auto[3..]}|{name[..19]}";
    }

    private static RaceRecord? Nachbauen(string wurzel, string pfad, string id)
    {
        var tele = pfad + TelemetryTrack.Suffix;
        if (!File.Exists(tele)) { return null; }
        using var dok = JsonDocument.Parse(File.ReadAllBytes(pfad));
        if (!dok.RootElement.TryGetProperty("Lap", out var lap)) { return null; }
        string? Text(string feld) => lap.TryGetProperty(feld, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int Zahl(string feld) => lap.TryGetProperty(feld, out var v) && v.TryGetInt32(out var z) ? z : 0;
        bool Ja(string feld) => lap.TryGetProperty(feld, out var v) && v.ValueKind == JsonValueKind.True;
        var modus = Text("mode") ?? "unknown";
        // Ein Rivalenlauf hat keine Gegner, eine Freifahrt kein Rennen.
        if (modus is "rivals" or "freeroam" || Ja("freeRoam")) { return null; }

        var ende = LiesSpur(tele);
        if (ende is not { } e || e.LastPos < 1) { return null; }
        var rel = Path.GetRelativePath(wurzel, pfad).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var abgebrochen = rel.Length > 0 && rel[0].Equals(LapArchive.UnfertigOrdner, StringComparison.OrdinalIgnoreCase);
        var kursOrdner = abgebrochen && rel.Length > 1 ? Path.Combine(wurzel, rel[0], rel[1]) : Path.Combine(wurzel, rel[0]);
        var notiz = LiesNotiz(kursOrdner);
        var kurs = abgebrochen
            ? notiz?.UnfinishedOf ?? LapArchive.KennungAus(rel.Length > 1 ? rel[1] : string.Empty) ?? string.Empty
            : LapArchive.KennungAus(rel[0]) ?? rel[0];
        var name = Text("track");
        if (!LapArchive.IstStreckenname(name))
        {
            name = abgebrochen ? LiesNotiz(LapArchive.KursPfad(wurzel, kurs))?.Name : notiz?.Name;
        }

        var r = new RaceRecord
        {
            Id = id,
            At = lap.TryGetProperty("recordedAt", out var wann) && wann.TryGetDateTimeOffset(out var w) ? w.LocalDateTime : File.GetLastWriteTime(pfad),
            Course = kurs,
            Track = LapArchive.IstStreckenname(name) ? name!.Trim() : null,
            Mode = modus,
            Car = Zahl("carOrdinal"),
            Pi = Zahl("performanceIndex"),
            Klass = LapArchive.ClassOf(Zahl("performanceIndex")),
            Finish = e.LastPos,
            Highest = e.MaxPos,
            Seconds = e.LastTime,
            Finished = !abgebrochen,
            Source = "archive",
            Laps = 1,
        };

        // DER START: steht die letzte Runde selbst am Anfang des Rennens (ein Sprint), ist
        // ihre erste Zeile der Start. Sonst die erste Runde desselben Rennens: im selben
        // Ordner, aus dem Stand, beendet zwischen Rennbeginn und dieser Runde.
        if (e.FirstLap == 0 && e.FirstTime <= 8f)
        {
            r.Start = e.FirstPos;
            r.StartFrom = "telemetry-late";
        }
        else
        {
            var beginn = r.At - TimeSpan.FromSeconds(e.LastTime);
            var ordner = Path.GetDirectoryName(pfad)!;
            var runden = Directory.EnumerateFiles(ordner, "*.json")
                .Where(f => !f.EndsWith("course.json", StringComparison.OrdinalIgnoreCase))
                .Select(f => (Datei: f, Wann: Stempel(f)))
                .Where(t => t.Wann is { } z && z > beginn.AddSeconds(-5) && z <= r.At.AddSeconds(1))
                .OrderBy(t => t.Wann)
                .ToList();
            r.Laps = Math.Max(1, runden.Count);
            var erste = runden.FirstOrDefault(t => Path.GetFileName(t.Datei).Contains("_standing", StringComparison.Ordinal));
            if (erste.Datei is not null && !string.Equals(erste.Datei, pfad, StringComparison.OrdinalIgnoreCase)
                && LiesSpur(erste.Datei + TelemetryTrack.Suffix) is { } a && a.FirstLap == 0 && a.FirstTime <= 8f)
            {
                r.Start = a.FirstPos;
                r.StartFrom = "telemetry-late";
                r.Highest = Math.Max(r.Highest, a.MaxPos);
            }
        }
        return r;
    }

    private static DateTime? Stempel(string datei)
    {
        var n = Path.GetFileName(datei);
        return n.Length >= 19 && DateTime.TryParseExact(n[..19], "yyyy-MM-dd_HH-mm-ss",
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var t)
            ? t : null;
    }

    private static LapArchive.CourseNote? LiesNotiz(string ordner)
    {
        try
        {
            var p = Path.Combine(ordner, "course.json");
            return File.Exists(p) ? JsonSerializer.Deserialize<LapArchive.CourseNote>(File.ReadAllText(p)) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Erste und letzte Zeile einer vollen Spur, dazu der schlechteste Platz -- ohne die Spur zu behalten.</summary>
    private static Spur? LiesSpur(string tele)
    {
        if (!File.Exists(tele)) { return null; }
        byte[] roh;
        using (var datei = File.OpenRead(tele))
        using (var gz = new GZipStream(datei, CompressionMode.Decompress))
        using (var ms = new MemoryStream())
        {
            gz.CopyTo(ms);
            roh = ms.ToArray();
        }
        var leser = new Utf8JsonReader(roh);
        var spalten = new List<string>();
        int iPos = -1, iRunde = -1, iZeit = -1;
        var imDaten = false;
        var zeile = -1;
        var feld = 0;
        double pos = 0, runde = 0, zeit = 0;
        var erste = default(Spur);
        var hatErste = false;
        var max = 0;
        double lPos = 0, lZeit = 0;
        string? eigenschaft = null;
        while (leser.Read())
        {
            switch (leser.TokenType)
            {
                case JsonTokenType.PropertyName when leser.CurrentDepth == 1:
                    eigenschaft = leser.GetString();
                    break;
                case JsonTokenType.String when eigenschaft == "columns" && leser.CurrentDepth == 2:
                    spalten.Add(leser.GetString() ?? string.Empty);
                    break;
                case JsonTokenType.StartArray when eigenschaft == "data" && leser.CurrentDepth == 1:
                    imDaten = true;
                    iPos = spalten.IndexOf("RacePosition");
                    iRunde = spalten.IndexOf("LapNumber");
                    iZeit = spalten.IndexOf("CurrentRaceTime");
                    if (iPos < 0 || iZeit < 0) { return null; }
                    break;
                case JsonTokenType.StartArray when imDaten && leser.CurrentDepth == 2:
                    zeile++;
                    feld = 0;
                    break;
                case JsonTokenType.Number when imDaten && leser.CurrentDepth == 3:
                    if (feld == iPos) { pos = leser.GetDouble(); }
                    else if (feld == iRunde) { runde = leser.GetDouble(); }
                    else if (feld == iZeit) { zeit = leser.GetDouble(); }
                    feld++;
                    break;
                case JsonTokenType.EndArray when imDaten && leser.CurrentDepth == 2:
                    if (pos >= 1 && !hatErste)
                    {
                        erste = new Spur((int)pos, (int)runde, (float)zeit, 0, 0f, 0);
                        hatErste = true;
                    }
                    if (pos >= 1)
                    {
                        lPos = pos;
                        lZeit = zeit;
                        max = Math.Max(max, (int)pos);
                    }
                    break;
                case JsonTokenType.EndArray when imDaten && leser.CurrentDepth == 1:
                    imDaten = false;
                    break;
            }
        }
        return hatErste ? erste with { LastPos = (int)lPos, LastTime = (float)lZeit, MaxPos = max } : null;
    }
}

/// <summary>Was die Statistik zeigen soll.</summary>
internal sealed class RaceFilter
{
    public HashSet<string> Modes { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Categories { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Courses { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Classes { get; } = new(StringComparer.Ordinal);
    public HashSet<int> Cars { get; } = new();
    /// <summary>Nur Rennen, die ueber die Ziellinie gingen.</summary>
    public bool FinishedOnly { get; set; } = true;
    /// <summary>Aeltere Rennen ohne bekannte Feldgroesse mitzaehlen: der schlechteste gesehene Platz als Feldgroesse.</summary>
    public bool EstimateField { get; set; }
}

/// <summary>Kennzahlen einer Auswahl von Rennen.</summary>
internal sealed record RaceSummary(
    int Races, int Placed, int Wins, int Podiums, int WithField,
    double? WinRate, double? PodiumRate,
    double? AvgFinishPct, double? AvgStartPct,
    double? AvgFinish, double? AvgStart, double? AvgField, double? AvgGain,
    int[] StartBins, int[] FinishBins, int StartN, int FinishN,
    (double Mean, double Sd)? StartFit, (double Mean, double Sd)? FinishFit);

internal static class RaceStats
{
    public const int Bins = 10;

    /// <summary>Die Feldgroesse eines Rennens -- oder null, wenn sie nicht bekannt ist.</summary>
    public static int? Field(RaceRecord r, bool schaetzen)
    {
        if (r.Drivers is { } d && d >= 1) { return d; }
        if (!schaetzen) { return null; }
        var mindestens = new[] { r.Highest, r.Start ?? 0, r.Finish ?? 0 }.Max();
        return mindestens >= 2 ? mindestens : null;
    }

    /// <summary>Platz als Anteil des Felds: 0 = Erster, 1 = Letzter.</summary>
    public static double? Share(int? platz, int? feld) =>
        platz is { } p && feld is { } f && f >= 2 && p >= 1 && p <= f ? (p - 1) / (double)(f - 1) : null;

    /// <summary>
    /// Zaehlt ein Rennen fuer Platzierungen? Nur mit Gegnern: bekannte Feldgroesse ab 2, oder
    /// irgendwann nicht vorn. Ein Lauf, der immer auf Platz 1 stand und dessen Feld unbekannt
    /// ist, kann ein Zeitfahren ohne Gegner gewesen sein -- der zaehlt nicht als Sieg.
    /// </summary>
    public static bool HasOpponents(RaceRecord r) => (r.Drivers ?? 0) >= 2 || r.Highest >= 2;

    public static IEnumerable<RaceRecord> Apply(IEnumerable<RaceRecord> races, RaceFilter f, Func<RaceRecord, string?> kategorie)
    {
        foreach (var r in races)
        {
            if (f.FinishedOnly && !r.Finished) { continue; }
            if (f.Modes.Count > 0 && !f.Modes.Contains(r.Mode)) { continue; }
            if (f.Courses.Count > 0 && !f.Courses.Contains(r.Course)) { continue; }
            if (f.Classes.Count > 0 && !f.Classes.Contains(r.Klass)) { continue; }
            if (f.Cars.Count > 0 && !f.Cars.Contains(r.Car)) { continue; }
            if (f.Categories.Count > 0 && !f.Categories.Contains(kategorie(r) ?? string.Empty)) { continue; }
            yield return r;
        }
    }

    public static RaceSummary Summarize(IReadOnlyCollection<RaceRecord> races, bool schaetzen)
    {
        var mitGegnern = races.Where(r => r.Finished && r.Finish is not null && HasOpponents(r)).ToList();
        var siege = mitGegnern.Count(r => r.Finish == 1);
        var podium = mitGegnern.Count(r => r.Finish <= 3);
        var ziel = new List<double>();
        var start = new List<double>();
        var feld = new List<int>();
        var gewinn = new List<int>();
        foreach (var r in races.Where(HasOpponents))
        {
            var f = Field(r, schaetzen);
            if (f is null) { continue; }
            feld.Add(f.Value);
            if (r.Finished && Share(r.Finish, f) is { } z) { ziel.Add(z); }
            if (Share(r.Start, f) is { } s) { start.Add(s); }
        }
        foreach (var r in mitGegnern.Where(r => r.Start is not null)) { gewinn.Add(r.Start!.Value - r.Finish!.Value); }

        static int[] Faecher(List<double> werte)
        {
            var b = new int[Bins];
            foreach (var w in werte) { b[Math.Min(Bins - 1, (int)Math.Floor(w * Bins))]++; }
            return b;
        }
        static (double, double)? Glocke(List<double> werte)
        {
            if (werte.Count < 2) { return null; }
            var m = werte.Average();
            var sd = Math.Sqrt(werte.Sum(w => (w - m) * (w - m)) / (werte.Count - 1));
            return (m, Math.Max(sd, 0.02));
        }
        double? Schnitt(IEnumerable<double> x) => x.Any() ? x.Average() : null;

        return new RaceSummary(
            races.Count, mitGegnern.Count, siege, podium, feld.Count,
            mitGegnern.Count > 0 ? siege / (double)mitGegnern.Count : null,
            mitGegnern.Count > 0 ? podium / (double)mitGegnern.Count : null,
            Schnitt(ziel), Schnitt(start),
            Schnitt(mitGegnern.Select(r => (double)r.Finish!.Value)),
            Schnitt(races.Where(r => r.Start is not null && HasOpponents(r)).Select(r => (double)r.Start!.Value)),
            Schnitt(feld.Select(x => (double)x)),
            Schnitt(gewinn.Select(x => (double)x)),
            Faecher(start), Faecher(ziel), start.Count, ziel.Count,
            Glocke(start), Glocke(ziel));
    }
}
