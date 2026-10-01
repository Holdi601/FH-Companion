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

    /// <summary>
    /// Der Platz des Autos auf der Bestenliste dieser Strecke in dieser Klasse, ZUM ZEITPUNKT des
    /// Rennens (seit 2026-10-01) -- die Liste aendert sich, die Wahl war gegen die damalige.
    /// null mit <see cref="MetaCars"/>: das Auto steht nicht darauf.
    /// </summary>
    [JsonPropertyName("metaRank")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MetaRank { get; set; }

    /// <summary>Wie viele Autos diese Bestenliste reiht; null: keine Bestenliste bekannt.</summary>
    [JsonPropertyName("metaCars")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MetaCars { get; set; }
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
    /// <summary>Der Modus, wie ihn der Menueschirm nannte -- vor der Unterscheidung Solo/Koop.</summary>
    [JsonPropertyName("modeScreen")] public string? ModeScreen { get; set; }
    /// <summary>Die Aufstellung, je Zeile 'H' (Mensch) oder 'A' (KI) -- zum Nachpruefen der Regel.</summary>
    [JsonPropertyName("gridRows")] public string? GridRows { get; set; }
    /// <summary>Das Ergebnis, je Zeile in der Reihenfolge des Ziels.</summary>
    [JsonPropertyName("resultRows")] public string? ResultRows { get; set; }
    /// <summary>Menschen im Rennen, man selbst eingeschlossen.</summary>
    [JsonPropertyName("humans")] public int? Humans { get; set; }
    /// <summary>Mitspieler (Menschen ausser einem selbst).</summary>
    [JsonPropertyName("coPlayers")] public int? CoPlayers { get; set; }
    /// <summary>Wie viele Mitspieler vor einem ins Ziel kamen.</summary>
    [JsonPropertyName("coAhead")] public int? CoAhead { get; set; }

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
            // Eine spaetere Zeile derselben Kennung ersetzt die fruehere: das Ergebnis nach
            // dem Rennen wird als vollstaendige Zeile nachgetragen.
            var stelle = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var z in zeilen)
            {
                if (string.IsNullOrWhiteSpace(z)) { continue; }
                try
                {
                    if (JsonSerializer.Deserialize<RaceRecord>(z) is not { } r) { continue; }
                    if (r.Id.Length > 0 && stelle.TryGetValue(r.Id, out var i)) { raus[i] = r; continue; }
                    if (r.Id.Length > 0) { stelle[r.Id] = raus.Count; }
                    raus.Add(r);
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
    /// <summary>Die Rennuhr beim ersten Paket: unter 2 s ist der Startplatz der aus der Aufstellung.</summary>
    public float StartSeconds { get; private set; }
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
            StartSeconds = rennzeit;
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

/// <summary>Was ein Tabellenschirm zeigt: die Startaufstellung vor dem Rennen oder das Ergebnis danach.</summary>
/// <param name="Drivers">Wie viele Zeilen belegt sind.</param>
/// <param name="Cursor">Die schwarz markierte Zeile (1-basiert) -- ein Zeiger, den man bewegen kann, NICHT
/// verlaesslich der eigene Platz; oder null.</param>
/// <param name="Rows">Je belegter Zeile 'H' (mit Stufenabzeichen: ein Mensch) oder 'A' (ohne: KI).</param>
internal readonly record struct GridRead(int Drivers, int? Cursor, string Rows)
{
    /// <summary>Menschen in der Aufstellung, man selbst eingeschlossen.</summary>
    public int Humans => Rows.Count(c => c == 'H');
}

/// <summary>
/// Die Tabellenschirme um ein Rennen, nur aus Pixeln gelesen -- in jeder Sprache gleich.
/// </summary>
/// <remarks>
/// Seit 2026-09-30. Beide Schirme: eine limettengruene Kopfzeile (Driver, Car, Class ...),
/// darunter zwoelf Zeilen im Abstand von 54 Punkten (1080p), jeder Fahrer eine WEISSE Zeile,
/// die markierte SCHWARZ mit Limettenrahmen.
///
/// DIE STARTAUFSTELLUNG vor dem Rennen: rechts ein zweiter Limettenkasten (Event 1/3, Karte),
/// leere Plaetze dunkel tuerkis, dahinter der tuerkise Hintergrund der Menues. Geprueft an
/// 1.314 Bildern (alle 2 s aus 44 Minuten Aufnahme): alle vier Aufstellungen gefunden (5, 9,
/// 9 und 10 Fahrer), kein Fehltreffer.
///
/// DAS ERGEBNIS nach dem Rennen: dieselbe Tabelle, breiter (Best Lap, Time), auf der
/// dunklen Rennszene; Zeilen in der Reihenfolge des Ziels. Wer das Rennen verlassen hat,
/// steht dort OHNE Abzeichen -- auf dem Ergebnis heisst "kein Abzeichen" also nicht "KI".
///
/// DAS ABZEICHEN (Stern mit Stufe, oder bei niedriger Stufe eine graue Zahl) vor dem Namen
/// zeigt einen Menschen; eine KI-Zeile hat keines (in einer Horizon-Play-Aufstellung
/// "giangcc2024": reines Weiss, Helligkeit 253). Gemessen: Stern 84-139, graue Zahl 212-222
/// mit dunklen Ziffern (Minimum ~130), leer 253/253. Auf der markierten Zeile liegt das
/// Abzeichen auf Schwarz: mit Abzeichen Mittel ~90, ohne ~0.
///
/// DIE MARKIERUNG ist ein Zeiger: in einer Aufstellung stand er auf Platz 1, der Spieler
/// auf 3. Der Startplatz kommt darum aus der Telemetrie, nicht von hier.
/// </remarks>
internal static class RaceGrid
{
    /// <summary>So klein wird der Schirm aufgenommen: 384 Punkte breit genuegen fuer zwoelf Zeilen.</summary>
    public static readonly Size Aufnahme = new(384, 216);

    private static readonly float[] KopfY = { 228f / 1080f, 245f / 1080f, 262f / 1080f };

    /// <summary>Die Startaufstellung vor dem Rennen -- oder null.</summary>
    public static GridRead? Read(Bitmap bild) => Lies(bild, ergebnis: false);

    /// <summary>Das Ergebnis nach dem Rennen -- oder null.</summary>
    public static GridRead? ReadResults(Bitmap bild) => Lies(bild, ergebnis: true);

    private static GridRead? Lies(Bitmap bild, bool ergebnis)
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
            (double Hell, double Tint, double Min) Streifen(float y, float fx0, float fx1)
            {
                var yy = Math.Clamp((int)(y * h), 0, h - 1);
                long r = 0, g = 0, b = 0;
                var n = 0;
                var min = 255.0;
                for (var x = (int)(fx0 * w); x < (int)(fx1 * w); x++)
                {
                    var i = (yy * schritt) + (x * 3);
                    b += puffer[i]; g += puffer[i + 1]; r += puffer[i + 2];
                    min = Math.Min(min, (puffer[i] + puffer[i + 1] + puffer[i + 2]) / 3.0);
                    n++;
                }
                if (n == 0) { return (0, 0, 0); }
                return ((r + g + b) / (3.0 * n), (g - r) / (double)n, min);
            }
            static bool Limette((int R, int G, int B) p) => p.R > 150 && p.G > 220 && p.B < 90;
            static bool Tuerkis((int R, int G, int B) p) => p.G - p.R > 50 && p.G > 90 && p.B - p.R > 30;
            static bool Dunkel((int R, int G, int B) p) => (p.R + p.G + p.B) / 3 < 90 && p.G - p.R < 30;
            // Die schwarze Schrift der Kopfzeile verdeckt einzelne Proben -- darum drei Hoehen je Spalte.
            bool Spalte(float fx) => KopfY.Any(y => Limette(Px(fx, y)));

            var grund = 0;
            for (var i = 0; i < 5; i++)
            {
                for (var j = 0; j < 5; j++)
                {
                    var p = Px(0.02f + (0.08f * i / 4f), 0.35f + (0.30f * j / 4f));
                    if (ergebnis ? Dunkel(p) : Tuerkis(p)) { grund++; }
                }
            }
            if (grund < 18) { return null; }
            if (ergebnis)
            {
                if (Enumerable.Range(0, 30).Count(i => Spalte(0.19f + (0.62f * i / 29f))) < 26) { return null; }
            }
            else
            {
                if (Enumerable.Range(0, 20).Count(i => Spalte(0.18f + (0.45f * i / 19f))) < 17) { return null; }
                if (Enumerable.Range(0, 10).Count(i => Spalte(0.66f + (0.16f * i / 9f))) < 9) { return null; }
            }

            // Die zwoelf Plaetze: weiss (Fahrer), dunkel neutral (markiert), leer.
            var x0 = ergebnis ? 0.19f : 0.18f;
            var x1 = ergebnis ? 0.64f : 0.63f;
            var arten = new char[12];
            for (var s = 0; s < 12; s++)
            {
                var (hell, tint, _) = Streifen((300f + (54f * s)) / 1080f, x0, x1);
                arten[s] = hell > 150 && Math.Abs(tint) < 25 ? 'w'
                         : ergebnis
                             ? (hell >= 40 && hell < 120 && Math.Abs(tint) < 25 ? 'b' : hell < 40 ? '-' : '?')
                             : (hell < 120 && tint < 20 ? 'b' : hell < 120 && tint >= 25 ? '-' : '?');
            }
            if (ergebnis)
            {
                // Auf dem Ergebnis sind leere Plaetze und die markierte Zeile beide dunkel. Eine
                // dunkle Zeile VOR einer weissen kann aber kein leerer Platz sein -- die Plaetze
                // fuellen sich von oben.
                var letzteWeisse = Array.LastIndexOf(arten, 'w');
                for (var s = 0; s < letzteWeisse; s++) { if (arten[s] == '-') { arten[s] = 'b'; } }
            }
            var belegt = 0;
            while (belegt < 12 && arten[belegt] is 'w' or 'b') { belegt++; }
            if (belegt == 0) { return null; }
            for (var s = belegt; s < 12; s++) { if (arten[s] != '-') { return null; } }
            var schwarz = Enumerable.Range(0, belegt).Where(s => arten[s] == 'b').ToList();
            if (schwarz.Count > 1) { return null; }

            // Das Abzeichen vor dem Namen: Mensch oder KI.
            var bx0 = ergebnis ? 0.186f : 0.174f;
            var bx1 = ergebnis ? 0.216f : 0.204f;
            var zeilen = new char[belegt];
            for (var s = 0; s < belegt; s++)
            {
                var (hell, _, min) = Streifen((300f + (54f * s)) / 1080f, bx0, bx1);
                zeilen[s] = arten[s] == 'b'
                    ? (hell > 55 ? 'H' : 'A')
                    : (hell < 245 || min < 200 ? 'H' : 'A');
            }
            return new GridRead(belegt, schwarz.Count == 1 ? schwarz[0] + 1 : null, new string(zeilen));
        }
        finally
        {
            quelle.UnlockBits(daten);
        }
    }

    /// <summary>
    /// Solo oder Koop, aus der Aufstellung: allein hinten ist Solo, ein Block von Menschen
    /// hinten ist Koop. Horizon Play (Menschen ueberall) bleibt, was der Schirm sagte.
    /// </summary>
    /// <remarks>
    /// Im Solo startet man immer als Letzter, im Koop stehen die Mitspieler hinten (Hinweis
    /// des Nutzers, 2026-09-30). Tragen die KI-Fahrer doch Abzeichen, bleibt bei einem als
    /// Solo/Koop gelesenen Anmeldeschirm ("race") die Regel: als Letzter gestartet ist Solo.
    /// </remarks>
    internal static string Modus(string modus, string? zeilen, int? start, int? feld)
    {
        if (modus is "horizon-play" or "rivals" or "freeroam") { return modus; }
        if (!string.IsNullOrEmpty(zeilen))
        {
            var menschen = zeilen.Count(c => c == 'H');
            var ki = zeilen.Length - menschen;
            if (ki > 0)
            {
                var erster = zeilen.IndexOf('H');
                var hinten = erster >= 0 && zeilen[erster..].All(c => c == 'H');
                if (menschen <= 1 && (start is null || start == zeilen.Length)) { return "solo"; }
                if (menschen >= 2 && hinten) { return "coop"; }
            }
        }
        if (modus == "race" && start is { } s && feld is { } f && f >= 2)
        {
            return s == f ? "solo" : "coop";
        }
        return modus;
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
    /// <remarks>
    /// Der Autoordner heisst seit 2026-10-01 "Name (carN)"; die Kennung bleibt "N|…" -- sonst
    /// baute die Rennstatistik jedes schon gesehene Rennen ein zweites Mal nach.
    /// </remarks>
    internal static string? IdAus(string pfad)
    {
        var name = Path.GetFileName(pfad);
        if (name.Length < 19) { return null; }
        var teile = pfad.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var auto = teile.Reverse().Skip(1).Select(LapArchive.AutoNummerAus).FirstOrDefault(z => z is not null);
        return auto is null ? null : $"{auto}|{name[..19]}";
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
    (double Mean, double Sd)? StartFit, (double Mean, double Sd)? FinishFit,
    int CoRaces, int CoFirst, double? CoBeaten,
    int PodiumRaces, IReadOnlyList<double> StartShares, IReadOnlyList<double> FinishShares,
    int MetaRaces, int MetaHigh, int MetaLow,
    int MetaRanked, double? MetaAvgRank, double? MetaAvgTop);

internal static class RaceStats
{
    /// <summary>Die festen Faecher der Kennzahlen (StartBins, FinishBins); das Bild waehlt seine eigenen.</summary>
    public const int Bins = 10;

    /// <summary>
    /// Ab wie vielen Fahrern ein Podium zaehlt (seit 2026-10-01, auf Wunsch des Nutzers): unter
    /// fuenf ist ein Platz unter den ersten drei kaum etwas -- von vieren standen drei drauf.
    /// </summary>
    public const int PodiumAb = 5;

    /// <summary>
    /// Zaehlt das Rennen fuer die Podiumsquote? Die bekannte Feldgroesse entscheidet; ohne sie
    /// genuegt jeder gesehene Platz ab fuenf als Beweis. Ein Feld, das nur unbekannt ist, wird nie
    /// gross geschaetzt -- die Quote waere sonst um genau die leichten Rennen geschoent.
    /// </summary>
    public static bool PodiumZaehlt(RaceRecord r) =>
        r.Drivers is { } d ? d >= PodiumAb : Math.Max(r.Highest, Math.Max(r.Start ?? 0, r.Finish ?? 0)) >= PodiumAb;

    /// <summary>
    /// Wie viele Balken das Bild von sich aus zeigt: die Wurzel aus der Zahl der Rennen, also
    /// mehr, je mehr Rennen es gibt (4 bei wenigen, 10 bei hundert, 20 ab vierhundert).
    /// </summary>
    /// <remarks>
    /// Nie feiner als das Feld: bei zwoelf Fahrern gibt es nur zwoelf moegliche Plaetze, und mehr
    /// Faecher haetten leere Luecken zwischen den Balken, die nichts bedeuten.
    /// </remarks>
    public static int AutoBins(int n, double? feld)
    {
        var b = (int)Math.Round(Math.Sqrt(Math.Max(1, n)));
        var grenze = feld is { } f ? (int)Math.Round(f) : 20;
        return Math.Clamp(b, 4, Math.Clamp(grenze, 4, 20));
    }

    // ------------------------------------------------------------------ Meta-Wahl
    //
    // WIE "META" DAS AUTO WAR (seit 2026-10-01, auf Wunsch des Nutzers): sein Platz auf der
    // Bestenliste der Strecke in der Klasse des Rennens, nach Zeit gereiht wie im Rivalen-Panel.
    // Unter den ersten 15 ist es eine hohe Meta-Wahl, 16 bis 25 eine niedrige, darunter -- oder
    // gar nicht auf der Liste -- keine. Ohne Streckennamen oder ohne Bestenliste: unbekannt, und
    // dann zaehlt das Rennen nicht mit.

    public enum MetaArt { Unbekannt, Hoch, Niedrig, Keine }

    public const int MetaHochBis = 15;
    public const int MetaNiedrigBis = 25;

    public static MetaArt Meta(int? platz, int? autos) =>
        autos is null or <= 0 ? MetaArt.Unbekannt
        : platz is { } p && p <= MetaHochBis ? MetaArt.Hoch
        : platz is { } q && q <= MetaNiedrigBis ? MetaArt.Niedrig
        : MetaArt.Keine;

    /// <summary>Platz des Autos (Index im Datensatz) auf der Bestenliste der Strecke in der Klasse; null ohne Liste.</summary>
    public static (int? Platz, int Autos)? MetaPlatz(RivalsAdvisor berater, string? strecke, string? klasse, int? autoIndex)
    {
        if (string.IsNullOrWhiteSpace(strecke) || string.IsNullOrWhiteSpace(klasse)) { return null; }
        var kategorie = berater.CategoryOf(new[] { strecke });
        if (!berater.HasBoard(strecke, klasse, kategorie)) { return null; }
        var reihe = berater.Advise(new[] { strecke }, klasse, kategorie).ByTime;
        if (reihe.Count == 0) { return null; }
        var platz = autoIndex is { } i ? reihe.FirstOrDefault(r => r.Car == i)?.Place : null;
        return (platz, reihe.Count);
    }

    /// <summary>
    /// Platz und Laenge der Bestenliste fuer ein Rennen: beim Rennen festgehalten, sonst aus der
    /// heutigen. (null, null) ohne Liste; (null, n) wenn das Auto nicht darauf steht.
    /// </summary>
    public static (int? Platz, int? Autos) MetaVon(RaceRecord r, RivalsAdvisor? berater)
    {
        if (r.MetaCars is not null) { return (r.MetaRank, r.MetaCars); }
        if (berater is null) { return (null, null); }
        return MetaPlatz(berater, r.Track, r.Klass, berater.CarIndexForId(r.Car)) is { } m
            ? (m.Platz, m.Autos) : (null, null);
    }

    /// <summary>Anteile (0 = Erster, 1 = Letzter) auf gleich breite Faecher verteilen.</summary>
    public static int[] Faecher(IEnumerable<double> werte, int faecher)
    {
        var b = new int[Math.Max(1, faecher)];
        foreach (var w in werte) { b[Math.Clamp((int)Math.Floor(w * b.Length), 0, b.Length - 1)]++; }
        return b;
    }

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

    /// <param name="meta">Platz und Listenlaenge je Rennen (MetaVon); gezaehlt nur in Horizon Play, ohne sie gar nicht.</param>
    public static RaceSummary Summarize(IReadOnlyCollection<RaceRecord> races, bool schaetzen,
                                        Func<RaceRecord, (int? Platz, int? Autos)>? meta = null)
    {
        var metaWerte = meta is null
            ? new List<(int? Platz, int? Autos)>()
            : races.Where(r => r.Mode == "horizon-play").Select(meta).Where(m => m.Autos is > 0).ToList();
        var metaArten = metaWerte.Select(m => Meta(m.Platz, m.Autos)).ToList();
        // DER DURCHSCHNITTLICHE PLATZ des gewaehlten Autos auf der Liste -- nur, wo es darauf steht.
        // Dazu der Anteil der Liste ("Top 9 %"), weil die Listen verschieden lang sind.
        var gereiht = metaWerte.Where(m => m.Platz is not null).ToList();
        var mitGegnern = races.Where(r => r.Finished && r.Finish is not null && HasOpponents(r)).ToList();
        var siege = mitGegnern.Count(r => r.Finish == 1);
        var podiumRennen = mitGegnern.Where(PodiumZaehlt).ToList();
        var podium = podiumRennen.Count(r => r.Finish <= 3);
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

        static (double, double)? Glocke(List<double> werte)
        {
            if (werte.Count < 2) { return null; }
            var m = werte.Average();
            var sd = Math.Sqrt(werte.Sum(w => (w - m) * (w - m)) / (werte.Count - 1));
            return (m, Math.Max(sd, 0.02));
        }
        double? Schnitt(IEnumerable<double> x) => x.Any() ? x.Average() : null;

        // GEGEN DIE MITSPIELER (Koop): welcher Anteil von ihnen kam hinter einem ins Ziel?
        var koop = races.Where(r => r.Finished && r.CoPlayers is > 0 && r.CoAhead is not null).ToList();
        var geschlagen = koop.Select(r => (r.CoPlayers!.Value - Math.Min(r.CoAhead!.Value, r.CoPlayers.Value)) / (double)r.CoPlayers.Value);

        return new RaceSummary(
            races.Count, mitGegnern.Count, siege, podium, feld.Count,
            mitGegnern.Count > 0 ? siege / (double)mitGegnern.Count : null,
            podiumRennen.Count > 0 ? podium / (double)podiumRennen.Count : null,
            Schnitt(ziel), Schnitt(start),
            Schnitt(mitGegnern.Select(r => (double)r.Finish!.Value)),
            Schnitt(races.Where(r => r.Start is not null && HasOpponents(r)).Select(r => (double)r.Start!.Value)),
            Schnitt(feld.Select(x => (double)x)),
            Schnitt(gewinn.Select(x => (double)x)),
            Faecher(start, Bins), Faecher(ziel, Bins), start.Count, ziel.Count,
            Glocke(start), Glocke(ziel),
            koop.Count, koop.Count(r => r.CoAhead == 0), Schnitt(geschlagen),
            podiumRennen.Count, start, ziel,
            metaArten.Count, metaArten.Count(a => a == MetaArt.Hoch), metaArten.Count(a => a == MetaArt.Niedrig),
            gereiht.Count,
            gereiht.Count > 0 ? gereiht.Average(m => (double)m.Platz!.Value) : null,
            gereiht.Count > 0 ? gereiht.Average(m => m.Platz!.Value / (double)m.Autos!.Value) : null);
    }
}
