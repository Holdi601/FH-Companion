using System.Drawing;
using System.Text.Json;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Woher der Umriss einer Strecke kommt.
/// </summary>
internal enum ShapeSource
{
    /// <summary>Aus den eigenen aufgezeichneten Runden -- X/Z von oben gesehen.</summary>
    Telemetry,

    /// <summary>Aus der magenta Linie des Rivalen-Schirms, sofern geerntet.</summary>
    Rivals,

    /// <summary>Erst Telemetrie, sonst Rivalen. Die uebliche Wahl.</summary>
    Auto,
}

/// <summary>
/// Der Umriss einer Strecke, als Linienzug in Bildkoordinaten.
/// </summary>
/// <remarks>
/// ## Warum die Telemetrie die bessere Quelle ist
///
/// Der Rivalen-Schirm zeigt hübsche Karten, aber nur für die Strecken, die das Spiel
/// selbst als Rivalen-Strecke führt. Eine EventLab-Strecke oder ein selbstgebauter
/// Kurs kommt dort nie vor -- und genau die will man am ehesten nachschlagen, weil
/// man sie nicht auswendig kennt.
///
/// Die eigenen Runden haben dagegen ALLES, was man je gefahren ist, mit
/// Weltkoordinaten alle fünf Meter. Von oben betrachtet ist das der Streckenverlauf.
/// Darum ist <see cref="ShapeSource.Telemetry"/> die Voreinstellung und die
/// Rivalen-Karte die Ausweiche.
///
/// ## Nordweisend, seitenrichtig
///
/// Z wächst im Spiel nach NORDEN, y im Bild nach UNTEN. Ohne das Vorzeichen wäre
/// jeder Umriss an der Waagerechten gespiegelt -- und eine gespiegelte Strecke sieht
/// aus wie eine plausible andere Strecke, nicht wie ein Fehler. Dieselbe Falle steht
/// in `name_courses.py`, dort mit derselben Begründung.
///
/// ## Seitenverhältnis bleibt
///
/// Der Umriss wird in den verfügbaren Kasten EINGEPASST, nicht auf ihn gezogen. Eine
/// in die Breite gezerrte Strecke ist als Form wertlos: man erkennt sie gerade daran,
/// wie lang und schmal sie ist.
/// </remarks>
internal static class CourseShape
{
    /// <summary>Ein fertiger Umriss: Punkte im Einheitsquadrat, plus Herkunft.</summary>
    /// <param name="Points">
    /// Punkte in 0..1, y schon nach unten gedreht. Leer heisst: nichts bekannt.
    /// </param>
    /// <param name="Source">Woher er stammt -- damit die Anzeige es sagen kann.</param>
    /// <param name="Metres">Gemessene Länge, 0 wenn unbekannt.</param>
    /// <param name="Ordered">
    /// Sind die Punkte ein WEG (erster Punkt, zweiter Punkt, ...) oder nur eine
    /// Menge?
    ///
    /// Die Telemetrie liefert einen Weg: so ist er gefahren worden. Die geerntete
    /// Rivalen-Karte liefert die magenta BILDPUNKTE der gezeichneten Linie, und die
    /// kommen in Bildzeilen-Reihenfolge -- oben links zuerst. Die mit DrawLines zu
    /// verbinden ergibt ein ausgefuelltes Vieleck, kein Streckenband; genau so sah
    /// es am 2026-09-24 auch aus.
    ///
    /// Darum zeichnet <see cref="Draw"/> beide Faelle verschieden: den Weg als
    /// Linienzug, die Menge als das, was sie ist -- eine Flaeche von Punkten, also
    /// wieder das dicke Band, das das Spiel gemalt hat.
    ///
    /// Seit 2026-09-25 ist auch die Rivalen-Karte ein Weg: `route_maps.py` zieht die
    /// Mittellinie des Bandes nach und legt sie geordnet ab. Die Punktmenge bleibt
    /// nur als Rueckfall, falls diese Aufbereitung fuer eine Strecke fehlt.
    /// </param>
    /// <param name="Closed">Ein Rundkurs: das Ende schliesst an den Anfang an.</param>
    /// <param name="StartIndex">
    /// Wo der Start liegt, als Index in <paramref name="Points"/>; -1 heisst
    /// unbekannt. Bei einer eigenen Runde ist das immer 0. Auf der Rivalen-Karte
    /// steht er dort, wo das Spiel seinen weissen Pfeil hinsetzt -- und bei manchen
    /// Strecken mitten im Weg, weil die Schleife einen Anlauf hat.
    /// </param>
    /// <param name="Image">Der Kartenausschnitt des Spiels als Datei, falls vorhanden.</param>
    internal sealed record Outline(IReadOnlyList<PointF> Points, ShapeSource Source,
                                   double Metres, bool Ordered = true,
                                   bool Closed = false, int StartIndex = 0,
                                   string? Image = null, bool Reliable = true)
    {
        public bool IsEmpty => Points.Count < 2;
    }

    private static readonly Dictionary<string, Outline> Speicher =
        new(StringComparer.Ordinal);
    private static DateTime _stand = DateTime.MinValue;

    /// <summary>Den Umriss eines Kursordners holen.</summary>
    /// <param name="kurs">Der Ordnername im Rundenbestand.</param>
    /// <param name="quelle">Welche Quelle bevorzugt wird.</param>
    /// <param name="root">Rundenbestand; null heisst der übliche Ort.</param>
    public static Outline? For(string kurs, ShapeSource quelle = ShapeSource.Auto,
                               string? root = null)
    {
        if (string.IsNullOrEmpty(kurs)) { return null; }
        var wurzel = root ?? LapArchive.Root;

        // Der Zwischenspeicher verfällt, wenn eine Runde dazukommt -- sonst bliebe
        // ein Umriss aus drei Messpunkten für immer stehen.
        DateTime stand;
        try
        {
            stand = Directory.Exists(wurzel)
                ? Directory.GetLastWriteTimeUtc(wurzel) : DateTime.MinValue;
        }
        catch (Exception) { stand = DateTime.MinValue; }
        if (stand != _stand) { Speicher.Clear(); _stand = stand; }

        var schluessel = kurs + "|" + quelle;
        if (Speicher.TryGetValue(schluessel, out var da)) { return da; }

        Outline? umriss = null;
        if (quelle is ShapeSource.Telemetry or ShapeSource.Auto)
        {
            umriss = AusTelemetrie(wurzel, kurs);
        }
        if (umriss is null && quelle is ShapeSource.Rivals or ShapeSource.Auto)
        {
            umriss = AusRivalenKarte(kurs, wurzel);
        }
        if (umriss is not null) { Speicher[schluessel] = umriss; }
        return umriss;
    }

    /// <summary>Aus einer aufgezeichneten Runde: X/Z von oben.</summary>
    private static Outline? AusTelemetrie(string wurzel, string kurs)
    {
        var ordner = LapArchive.KursPfad(wurzel, kurs);
        if (!Directory.Exists(ordner)) { return null; }

        // DIE LÄNGSTE RUNDE, nicht die erste. Eine abgebrochene Runde liefert ein
        // Bruchstück, und ein Bruchstück sieht aus wie eine kurze Strecke.
        string? beste = null;
        var meisten = 0;
        List<PointF>? punkte = null;
        // Eine Runde mit Sprung (zurueckgesetzt, zurueckgespult) zeichnete eine gerade
        // Linie quer ueber die Kachel. Sie zaehlt nur, wenn es keine saubere gibt.
        var sauber = false;
        try
        {
            foreach (var datei in Directory.EnumerateFiles(
                         ordner, "*.json", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(datei).Equals("course.json",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var gelesen = PunkteAus(datei);
                if (gelesen is null) { continue; }
                var ohneSprung = !HatSprung(gelesen);
                if ((ohneSprung && !sauber) || (ohneSprung == sauber && gelesen.Count > meisten))
                {
                    meisten = gelesen.Count;
                    punkte = gelesen;
                    beste = datei;
                    sauber = ohneSprung;
                }
            }
        }
        catch (Exception) { return null; }

        if (punkte is null || punkte.Count < 8 || beste is null) { return null; }
        return new Outline(Normieren(punkte), ShapeSource.Telemetry,
                           LaengeVon(punkte));
    }

    /// <summary>Liegen irgendwo zwei Messpunkte mehr als 150 m auseinander?</summary>
    internal static bool HatSprung(IReadOnlyList<PointF> punkte)
    {
        for (var i = 1; i < punkte.Count; i++)
        {
            var dx = punkte[i].X - punkte[i - 1].X;
            var dy = punkte[i].Y - punkte[i - 1].Y;
            if ((dx * dx) + (dy * dy) > LiveMapHud.Sprung * LiveMapHud.Sprung) { return true; }
        }
        return false;
    }

    private static List<PointF>? PunkteAus(string datei)
    {
        try
        {
            using var strom = File.OpenRead(datei);
            var abgelegt = JsonSerializer.Deserialize<LapArchive.ArchivedLap>(strom);
            var proben = abgelegt?.Lap?.Samples;
            if (proben is null || proben.Count < 8) { return null; }
            var punkte = new List<PointF>(proben.Count);
            foreach (var p in proben)
            {
                // 0/0 ist kein Messpunkt, sondern ein fehlender -- der Ursprung der
                // Welt liegt nicht auf einer Rennstrecke.
                if (p.X == 0f && p.Z == 0f) { continue; }
                punkte.Add(new PointF(p.X, p.Z));
            }
            return punkte.Count >= 8 ? punkte : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Aus der geernteten Rivalen-Karte, falls für diesen Kurs eine vorliegt.</summary>
    /// <remarks>
    /// Die Ernte liegt als JSON unter <c>data/route_shapes</c> und trägt die magenta
    /// Linie in BILDpunkten. Zugeordnet wird über den Streckennamen im
    /// <c>course.json</c> -- den setzt `route_shapes.py --setzen`, wenn Form und Länge
    /// zusammenpassen. Ohne Namen gibt es hier nichts, und das ist richtig: eine
    /// Karte zu zeigen, von der man nicht weiss, ob sie diese Strecke ist, wäre
    /// schlimmer als keine.
    /// </remarks>
    private static Outline? AusRivalenKarte(string kurs, string wurzel)
    {
        var name = KursName(wurzel, kurs);
        if (string.IsNullOrEmpty(name)) { return null; }
        return AusRivalenName(name);
    }

    /// <summary>
    /// The Rivals map of a route BY NAME -- also for a route never driven.
    /// </summary>
    /// <remarks>
    /// The pre-race tiles used to reach a map only through an own course folder, so a
    /// route without an own lap showed "no map harvested" although its map was there.
    /// Telemetry cannot help such a route; the harvested map is the only outline.
    /// </remarks>
    public static Outline? ForRoute(string route, ShapeSource quelle = ShapeSource.Auto)
    {
        if (string.IsNullOrWhiteSpace(route) || quelle == ShapeSource.Telemetry) { return null; }
        var schluessel = "route:" + route;
        if (Speicher.TryGetValue(schluessel, out var da)) { return da; }
        var umriss = AusRivalenName(route);
        if (umriss is not null) { Speicher[schluessel] = umriss; }
        return umriss;
    }

    // NAME -> BESTE KARTE, einmal je Stand des Ernteordners. Auf dem Bau-Rechner
    // liegen dort ueber 2000 Aufnahmen (166 MB); jede bei jeder Anzeige ganz zu
    // parsen hiesse Sekunden je Aufruf. Gelesen wird nur der Kopf jeder Datei --
    // "name" und "pixels" stehen vor den Punkten.
    private static Dictionary<string, string>? _ernteIndex;
    private static string? _ernteFuer;
    private static DateTime _ernteStand;
    private static readonly System.Text.RegularExpressions.Regex KopfName =
        new(@"""name""\s*:\s*""((?:[^""\\]|\\.)*)""");
    private static readonly System.Text.RegularExpressions.Regex KopfPunkte =
        new(@"""pixels""\s*:\s*(\d+)");

    private static string? BesteKarte(string name)
    {
        var ernte = ErnteOrdner();
        if (ernte is null) { return null; }
        DateTime stand;
        try { stand = Directory.GetLastWriteTimeUtc(ernte); }
        catch (Exception) { return null; }
        if (_ernteIndex is null || _ernteFuer != ernte || stand != _ernteStand)
        {
            var index = new Dictionary<string, (string Datei, long Punkte)>(StringComparer.OrdinalIgnoreCase);
            var puffer = new char[1024];
            foreach (var datei in Directory.EnumerateFiles(ernte, "*.json"))
            {
                try
                {
                    using var leser = new StreamReader(datei);
                    var n = leser.ReadBlock(puffer, 0, puffer.Length);
                    var kopf = new string(puffer, 0, n);
                    var m = KopfName.Match(kopf);
                    if (!m.Success || m.Groups[1].Value.Length == 0) { continue; }
                    var wer = System.Text.RegularExpressions.Regex.Unescape(m.Groups[1].Value);
                    var p = KopfPunkte.Match(kopf);
                    var punkte = p.Success ? long.Parse(p.Groups[1].Value) : 0;
                    if (!index.TryGetValue(wer, out var alt) || punkte > alt.Punkte)
                    {
                        index[wer] = (datei, punkte);
                    }
                }
                catch (Exception)
                {
                    // Eine unlesbare Datei kostet ihre Karte, nicht den Index.
                }
            }
            _ernteIndex = index.ToDictionary(kv => kv.Key, kv => kv.Value.Datei,
                                             StringComparer.OrdinalIgnoreCase);
            _ernteFuer = ernte;
            _ernteStand = stand;
        }
        return _ernteIndex.TryGetValue(name, out var gefunden) ? gefunden : null;
    }

    private static Outline? AusRivalenName(string name) =>
        AusAufbereiteterKarte(name) ?? AusErnte(name);

    // ---- DIE AUFBEREITETEN KARTEN (data/route_maps, von route_maps.py) ---------

    private static Dictionary<string, string>? _kartenIndex;
    private static string? _kartenFuer;
    private static DateTime _kartenStand;

    /// <summary>Name -> Datei, einmal je Stand des Ordners.</summary>
    private static string? AufbereiteteKarte(string name)
    {
        var ordner = DatenOrdner("route_maps");
        if (ordner is null) { return null; }
        DateTime stand;
        try { stand = Directory.GetLastWriteTimeUtc(ordner); }
        catch (Exception) { return null; }
        if (_kartenIndex is null || _kartenFuer != ordner || stand != _kartenStand)
        {
            var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var datei in Directory.EnumerateFiles(ordner, "*.json"))
            {
                try
                {
                    using var strom = File.OpenRead(datei);
                    using var dok = JsonDocument.Parse(strom);
                    if (dok.RootElement.TryGetProperty("name", out var n)
                        && n.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(n.GetString()))
                    {
                        index[n.GetString()!] = datei;
                    }
                }
                catch (Exception)
                {
                    // Eine unlesbare Datei kostet ihre Karte, nicht den Index.
                }
            }
            _kartenIndex = index;
            _kartenFuer = ordner;
            _kartenStand = stand;
        }
        return _kartenIndex.TryGetValue(name, out var gefunden) ? gefunden : null;
    }

    /// <summary>Linienzug, Start und Bild einer aufbereiteten Rivalen-Karte.</summary>
    private static Outline? AusAufbereiteterKarte(string name)
    {
        var datei = AufbereiteteKarte(name);
        if (datei is null) { return null; }
        try
        {
            using var strom = File.OpenRead(datei);
            using var dok = JsonDocument.Parse(strom);
            var wurzel = dok.RootElement;
            if (!wurzel.TryGetProperty("path", out var weg) || weg.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            var punkte = new List<PointF>();
            foreach (var paar in weg.EnumerateArray())
            {
                if (paar.ValueKind != JsonValueKind.Array || paar.GetArrayLength() < 2) { continue; }
                // Bildkoordinaten, y nach unten: fuer Normieren negieren, das dreht zurueck.
                punkte.Add(new PointF((float)paar[0].GetDouble(), (float)-paar[1].GetDouble()));
            }
            if (punkte.Count < 2) { return null; }
            var geschlossen = wurzel.TryGetProperty("closed", out var c) && c.ValueKind == JsonValueKind.True;
            var start = wurzel.TryGetProperty("startIndex", out var si) && si.TryGetInt32(out var i) ? i : -1;
            if (start >= punkte.Count) { start = -1; }
            var km = wurzel.TryGetProperty("routeLengthKm", out var l) && l.ValueKind == JsonValueKind.Number
                ? l.GetDouble() : 0;
            string? bild = null;
            if (wurzel.TryGetProperty("image", out var b) && b.ValueKind == JsonValueKind.String)
            {
                var pfad = Path.Combine(Path.GetDirectoryName(datei)!, Path.GetFileName(b.GetString() ?? ""));
                if (File.Exists(pfad)) { bild = pfad; }
            }
            // UNSICHER nachgezeichnet (route_maps.py): die Strecke ist im Kartenbild zu
            // klein, eng anliegende Abschnitte laufen im Band zusammen.
            var sicher = !wurzel.TryGetProperty("reliable", out var r) || r.ValueKind != JsonValueKind.False;
            return new Outline(Normieren(punkte), ShapeSource.Rivals, km * 1000.0,
                               Ordered: true, Closed: geschlossen, StartIndex: start, Image: bild,
                               Reliable: sicher);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Rueckfall: die rohe Ernte, eine ungeordnete Punktmenge.</summary>
    private static Outline? AusErnte(string name)
    {
        var datei = BesteKarte(name);
        if (datei is null) { return null; }
        try
        {
            {
                using var strom = File.OpenRead(datei);
                using var dok = JsonDocument.Parse(strom);
                if (!dok.RootElement.TryGetProperty("points", out var ps)
                    || ps.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }
                var punkte = new List<PointF>();
                foreach (var paar in ps.EnumerateArray())
                {
                    if (paar.ValueKind != JsonValueKind.Array) { continue; }
                    var xy = paar.EnumerateArray().ToList();
                    if (xy.Count < 2) { continue; }
                    // Bildkoordinaten: y zeigt schon nach unten, also NICHT drehen.
                    punkte.Add(new PointF((float)xy[0].GetDouble(),
                                          (float)-xy[1].GetDouble()));
                }
                if (punkte.Count >= 8)
                {
                    return new Outline(Normieren(punkte), ShapeSource.Rivals, 0,
                                       Ordered: false);
                }
            }
        }
        catch (Exception)
        {
            // Eine unlesbare Ernte kostet den Umriss, nicht die Anzeige.
        }
        return null;
    }

    private static string? ErnteOrdner() => DatenOrdner("route_shapes");

    private static string? DatenOrdner(string name)
    {
        // ACHT EBENEN, NICHT SECHS. Von bin/Release/net9.0-.../win-x64 aus liegt der
        // Arbeitsbereich GENAU sechs Ebenen hoeher -- die Schleife hoerte bei sechs
        // SCHRITTEN auf, also eine Ebene zu frueh, und fand die Ernte nie.
        // Im mitgelieferten Paket liegt data/ dagegen direkt neben app/, also naeher.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var kandidat = Path.Combine(dir.FullName, "data", name);
            if (Directory.Exists(kandidat)) { return kandidat; }
        }
        return null;
    }

    /// <summary>Der gesetzte Name eines Kurses, oder leer.</summary>
    public static string KursName(string wurzel, string kurs)
    {
        try
        {
            var datei = Path.Combine(LapArchive.KursPfad(wurzel, kurs), "course.json");
            if (!File.Exists(datei)) { return string.Empty; }
            using var strom = File.OpenRead(datei);
            using var dok = JsonDocument.Parse(strom);
            return dok.RootElement.TryGetProperty("Name", out var n)
                   && n.ValueKind == JsonValueKind.String
                ? n.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>In das Einheitsquadrat einpassen, Seitenverhältnis erhalten.</summary>
    /// <remarks>
    /// Y wird hier gedreht: die Eingabe ist "wächst nach Norden", die Ausgabe ist
    /// "wächst nach unten", wie jede Zeichenfläche es will.
    /// </remarks>
    private static IReadOnlyList<PointF> Normieren(IReadOnlyList<PointF> punkte)
    {
        var minX = punkte.Min(p => p.X);
        var maxX = punkte.Max(p => p.X);
        var minY = punkte.Min(p => p.Y);
        var maxY = punkte.Max(p => p.Y);
        var breite = maxX - minX;
        var hoehe = maxY - minY;
        var spanne = Math.Max(breite, hoehe);
        if (spanne <= 0) { return Array.Empty<PointF>(); }

        // Mittig einpassen: die schmalere Richtung bekommt den Rest als Rand, damit
        // die Form in der Mitte des Kastens sitzt und nicht in einer Ecke klebt.
        var randX = (spanne - breite) / 2f;
        var randY = (spanne - hoehe) / 2f;

        var raus = new List<PointF>(punkte.Count);
        foreach (var p in punkte)
        {
            var x = (p.X - minX + randX) / spanne;
            var y = (p.Y - minY + randY) / spanne;
            raus.Add(new PointF(x, 1f - y));   // nach unten drehen
        }
        return raus;
    }

    private static double LaengeVon(IReadOnlyList<PointF> punkte)
    {
        double summe = 0;
        for (var i = 1; i < punkte.Count; i++)
        {
            var dx = punkte[i].X - punkte[i - 1].X;
            var dy = punkte[i].Y - punkte[i - 1].Y;
            summe += Math.Sqrt((dx * dx) + (dy * dy));
        }
        return summe;
    }

    /// <summary>Den Umriss in einen Kasten zeichnen.</summary>
    /// <param name="glaette">
    /// 0 (roh) bis 100. Wie stark die Linie geglaettet wird, gemessen an der
    /// Kachel -- dieselbe Einstellung sieht auf 1080p und auf 4K gleich aus.
    /// </param>
    /// <param name="alsBild">
    /// Eine Rivalen-Karte mit Ausschnitt als BILD zeigen statt als Linie.
    /// </param>
    public static void Draw(Graphics g, Outline umriss, RectangleF kasten,
                            Color linie, float dicke, Color start,
                            int glaette = 0, bool alsBild = false)
    {
        if (umriss.IsEmpty) { return; }
        // EINE UNSICHERE LINIE WIRD NIE GEZEICHNET, wenn es das Bild gibt: am 2026-09-26
        // zeigte die Kachel fuer "Shimanoyama Circuit" eine Acht, wo die Strecke eine
        // Haarnadel hat. Das Kartenbild des Spiels ist dort richtig, die Linie nicht.
        if ((alsBild || !umriss.Reliable) && umriss.Image is not null && ZeichneBild(g, umriss.Image, kasten)) { return; }
        var seite = Math.Min(kasten.Width, kasten.Height);
        var ox = kasten.X + ((kasten.Width - seite) / 2f);
        var oy = kasten.Y + ((kasten.Height - seite) / 2f);

        var punkte = new PointF[umriss.Points.Count];
        for (var i = 0; i < punkte.Length; i++)
        {
            punkte[i] = new PointF(ox + (umriss.Points[i].X * seite),
                                   oy + (umriss.Points[i].Y * seite));
        }

        var vorher = g.SmoothingMode;
        var versatz = g.PixelOffsetMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

        if (umriss.Ordered)
        {
            var glatt = Glaetten(punkte, umriss.Closed, SigmaFuer(glaette, seite));
            Linie(g, glatt, umriss.Closed, linie, Math.Max(1f, dicke));

            // Der Startpunkt als Scheibe. Ohne ihn sind zwei Richtungen derselben
            // Schleife nicht zu unterscheiden, und man sucht im Spiel die falsche
            // Ecke. Bei einer ungeordneten Menge waere der "erste" Punkt bloss der
            // oberste im Bild -- also keine Auskunft, und darum dort weggelassen.
            // Ebenso bei einer Rivalen-Karte ohne erkannten Pfeil (StartIndex -1).
            if (start.A > 0 && umriss.StartIndex >= 0 && umriss.StartIndex < punkte.Length)
            {
                // Auf der GEGLAETTETEN Linie: sonst stuende die Scheibe bei starker
                // Glaettung neben dem Strich.
                var s = Naechster(glatt, punkte[umriss.StartIndex]);
                var r = Math.Max(2.5f, dicke * 1.6f);
                using var pinsel = new SolidBrush(start);
                g.FillEllipse(pinsel, s.X - r, s.Y - r, r * 2, r * 2);
            }
        }
        else
        {
            // Die Punktmenge als Flaeche: je Punkt ein kleiner Fleck. Zusammen
            // ergibt das wieder das Band, das auf der Karte des Spiels steht.
            //
            // Der Fleck waechst mit der Kachel, nicht mit der Strichstaerke: die
            // Ernte speichert jeden dritten Bildpunkt einer Aufnahme in 1920 Punkten
            // Breite, und in einer 150 Punkte breiten Kachel liegen die entsprechend
            // dichter. Ein fester Radius liesse die Form bei kleinen Kacheln
            // zerfallen und bei grossen verschmieren.
            var seiteJePunkt = Math.Max(1.2f, seite / 90f);
            using var pinsel = new SolidBrush(linie);
            foreach (var punkt in punkte)
            {
                g.FillRectangle(pinsel, punkt.X - (seiteJePunkt / 2f),
                                punkt.Y - (seiteJePunkt / 2f),
                                seiteJePunkt, seiteJePunkt);
            }
        }
        g.SmoothingMode = vorher;
        g.PixelOffsetMode = versatz;
    }

    /// <summary>Einen Linienzug zeichnen, runde Ecken und Enden.</summary>
    internal static void Linie(Graphics g, IReadOnlyList<PointF> punkte, bool geschlossen,
                               Color farbe, float dicke)
    {
        if (punkte.Count < 2) { return; }
        using var stift = new Pen(farbe, dicke)
        {
            LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round,
        };
        // EIN PFAD statt DrawLines: mit einem geschlossenen Pfad hat ein Rundkurs
        // keine Naht, an der sich zwei runde Enden ueberlappen und dunkler werden.
        using var pfad = new System.Drawing.Drawing2D.GraphicsPath();
        pfad.AddLines(punkte as PointF[] ?? punkte.ToArray());
        if (geschlossen) { pfad.CloseFigure(); }
        g.DrawPath(stift, pfad);
    }

    /// <summary>Die Glaettung als Sigma in Bildpunkten, aus 0..100 und der Kachel.</summary>
    /// <remarks>
    /// Bis 3 % der Kachelseite: bei 50 und einer 150er-Kachel gut zwei Punkte --
    /// genug, dass die Treppen des Rasters und das Zittern der Messpunkte
    /// verschwinden, zu wenig, um eine Haarnadel zu einer Kurve zu machen. Erst am
    /// oberen Ende werden enge Kehren sichtbar weicher; das ist die Wahl des Nutzers.
    /// </remarks>
    internal static float SigmaFuer(int glaette, float seite) =>
        Math.Clamp(glaette, 0, 100) / 100f * 0.03f * seite;

    /// <summary>
    /// Einen Linienzug glaetten: gleichmaessig neu abtasten, dann mit einer
    /// Gausskurve mitteln.
    /// </summary>
    /// <remarks>
    /// ## Warum neu abtasten
    ///
    /// Die Punkte liegen ungleich: eine eigene Runde alle fuenf Meter, also auf einer
    /// Kachel oft zehn auf einem Bildpunkt; eine Rivalen-Linie nach dem Vereinfachen
    /// nur an den Knicken. Ein Mittel ueber "die naechsten n Punkte" glaettete die
    /// eine kaum und die andere viel zu stark. Nach dem Abtasten in festen Schritten
    /// heisst Sigma ueberall dasselbe: so viele Bildpunkte Linie.
    ///
    /// ## Die Enden bleiben stehen
    ///
    /// Ein offener Weg wird an den Enden nicht verlaengert, sondern mit dem Endpunkt
    /// aufgefuellt -- sonst zoege die Glaettung Start und Ziel nach innen, und die
    /// Startscheibe laege neben dem Anfang der Linie. Ein Rundkurs wird ringsum
    /// gemittelt, ohne Naht.
    /// </remarks>
    internal static PointF[] Glaetten(IReadOnlyList<PointF> punkte, bool geschlossen, float sigma)
    {
        var roh = punkte as PointF[] ?? punkte.ToArray();
        if (sigma < 0.3f || roh.Length < 3) { return roh; }

        // Laenge des Weges; beim Rundkurs samt dem Stueck vom Ende zurueck zum Anfang.
        var n = roh.Length + (geschlossen ? 1 : 0);
        var bogen = new double[n];
        for (var i = 1; i < n; i++)
        {
            var a = roh[i - 1];
            var b = roh[i % roh.Length];
            bogen[i] = bogen[i - 1] + Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));
        }
        var gesamt = bogen[n - 1];
        if (gesamt <= 0) { return roh; }

        // Schritt: ein halbes Sigma, mindestens ein Dreiviertelpunkt -- und nie mehr
        // als 6000 Proben, auch nicht fuer eine 20-km-Strecke auf 8K.
        var schritt = Math.Max(0.75, sigma / 2.0);
        schritt = Math.Max(schritt, gesamt / 6000.0);
        var anzahl = Math.Max(4, (int)(gesamt / schritt) + (geschlossen ? 0 : 1));
        var abstand = geschlossen ? gesamt / anzahl : gesamt / (anzahl - 1);
        var xs = new double[anzahl];
        var ys = new double[anzahl];
        var j = 0;
        for (var k = 0; k < anzahl; k++)
        {
            var s = Math.Min(k * abstand, gesamt);
            while (j < n - 2 && bogen[j + 1] < s) { j++; }
            var a = roh[j];
            var b = roh[(j + 1) % roh.Length];
            var laenge = bogen[j + 1] - bogen[j];
            var t = laenge > 0 ? (s - bogen[j]) / laenge : 0;
            xs[k] = a.X + ((b.X - a.X) * t);
            ys[k] = a.Y + ((b.Y - a.Y) * t);
        }

        // Gausskern, in Proben gemessen
        var sigmaProben = sigma / abstand;
        var radius = Math.Max(1, (int)Math.Ceiling(3 * sigmaProben));
        var kern = new double[(2 * radius) + 1];
        double summe = 0;
        for (var i = -radius; i <= radius; i++)
        {
            kern[i + radius] = Math.Exp(-(i * i) / (2 * sigmaProben * sigmaProben));
            summe += kern[i + radius];
        }
        for (var i = 0; i < kern.Length; i++) { kern[i] /= summe; }

        var raus = new PointF[anzahl];
        for (var k = 0; k < anzahl; k++)
        {
            double x = 0, y = 0;
            for (var i = -radius; i <= radius; i++)
            {
                var q = k + i;
                q = geschlossen ? ((q % anzahl) + anzahl) % anzahl : Math.Clamp(q, 0, anzahl - 1);
                x += xs[q] * kern[i + radius];
                y += ys[q] * kern[i + radius];
            }
            raus[k] = new PointF((float)x, (float)y);
        }
        if (!geschlossen)
        {
            raus[0] = roh[0];
            raus[^1] = roh[^1];
        }
        return raus;
    }

    private static PointF Naechster(IReadOnlyList<PointF> punkte, PointF ziel)
    {
        var best = punkte[0];
        var abstand = float.MaxValue;
        foreach (var p in punkte)
        {
            var d = ((p.X - ziel.X) * (p.X - ziel.X)) + ((p.Y - ziel.Y) * (p.Y - ziel.Y));
            if (d < abstand) { abstand = d; best = p; }
        }
        return best;
    }

    // ---- DER KARTENAUSSCHNITT ALS BILD ----------------------------------------

    // Einmal gelesen, dann gehalten: das Overlay zeichnet bei jeder Aenderung neu,
    // und drei JPEGs von der Platte je Bild waeren Ruckeln fuer nichts. Hoechstens
    // ein paar Dutzend -- angeboten werden drei Strecken zugleich.
    private static readonly Dictionary<string, Image> Bilder = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object BilderSperre = new();

    private static Image? Bild(string pfad)
    {
        lock (BilderSperre)
        {
            if (Bilder.TryGetValue(pfad, out var da)) { return da; }
            try
            {
                // Ueber einen Speicherstrom: Image.FromFile haelt die Datei offen, und
                // ein neuer route_maps.py-Lauf koennte sie dann nicht ersetzen.
                var bytes = File.ReadAllBytes(pfad);
                var bild = Image.FromStream(new MemoryStream(bytes));
                if (Bilder.Count >= 32)
                {
                    foreach (var alt in Bilder.Values) { alt.Dispose(); }
                    Bilder.Clear();
                }
                Bilder[pfad] = bild;
                return bild;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>Den Ausschnitt eingepasst zeichnen -- ganz, nicht beschnitten.</summary>
    /// <remarks>
    /// Die Kachel ist quadratisch, die Karten meist breiter als hoch. Eingepasst
    /// statt gefuellt: beschnitten fiele ausgerechnet der Teil der Strecke weg, der
    /// am Rand liegt -- oft der Start.
    /// </remarks>
    private static bool ZeichneBild(Graphics g, string pfad, RectangleF kasten)
    {
        lock (BilderSperre)
        {
            var bild = Bild(pfad);
            if (bild is null || bild.Width <= 0 || bild.Height <= 0) { return false; }
            var f = Math.Min(kasten.Width / bild.Width, kasten.Height / bild.Height);
            var w = bild.Width * f;
            var h = bild.Height * f;
            var ziel = new RectangleF(kasten.X + ((kasten.Width - w) / 2f),
                                      kasten.Y + ((kasten.Height - h) / 2f), w, h);
            var vorher = g.InterpolationMode;
            var versatz = g.PixelOffsetMode;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(bild, ziel);
            g.InterpolationMode = vorher;
            g.PixelOffsetMode = versatz;
            return true;
        }
    }
}
