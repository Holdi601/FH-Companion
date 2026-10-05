using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Alle Autos von FH6 und wie man an sie kommt -- fuer den Reiter "Car collection".
/// </summary>
/// <remarks>
/// Gebaut von <c>server/car_availability.py</c> aus der offiziellen Liste
/// (forza.net/fh6cars) und dem Wiki (forza.fandom.com). Der Server baut sie taeglich
/// neu, die App holt sie ueber <c>/api/cars</c> und legt sie im Datenordner ab; das
/// Paket bringt eine Fassung mit, damit der Reiter auch ohne Netz etwas zeigt.
///
/// DIE APP FRAGT KEINE FREMDE SEITE. Nur den eigenen Server -- wie beim Datensatz.
/// </remarks>
internal sealed class CarCollection
{
    internal const string Format = "fhc-cars-1";
    internal const string DateiName = "fh6_car_availability.json";

    // Unter so vielen Autos ist eine Liste kaputt -- nie laden, nie speichern.
    private const int Mindestens = 100;

    internal sealed class Weg
    {
        [JsonPropertyName("k")] public string Art { get; set; } = string.Empty;
        [JsonExtensionData] public Dictionary<string, JsonElement> Werte { get; set; } = new();

        public string? Text(string schluessel) =>
            Werte.TryGetValue(schluessel, out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() : null;

        public long? Zahl(string schluessel) =>
            Werte.TryGetValue(schluessel, out var w) && w.ValueKind == JsonValueKind.Number && w.TryGetInt64(out var z)
                ? z : null;
    }

    internal sealed class Auto
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("make")] public string Marke { get; set; } = string.Empty;
        [JsonPropertyName("year")] public int? Jahr { get; set; }
        [JsonPropertyName("type")] public string Typ { get; set; } = string.Empty;
        [JsonPropertyName("pi")] public int? Pi { get; set; }
        [JsonPropertyName("class")] public string? Klasse { get; set; }
        [JsonPropertyName("country")] public string Land { get; set; } = string.Empty;
        [JsonPropertyName("id")] public int? Id { get; set; }

        /// <summary>Alle car_ids dieses Autos (seit 2026-09-29; aeltere Listen nennen nur "id").</summary>
        [JsonPropertyName("ids")] public List<int>? IdListe { get; set; }

        [JsonIgnore]
        public IReadOnlyList<int> AlleIds =>
            IdListe is { Count: > 0 } l ? l : Id is { } i ? new[] { i } : Array.Empty<int>();
        [JsonPropertyName("collection")] public List<string> Sammlung { get; set; } = new();
        [JsonPropertyName("addons")] public List<string> Zusatz { get; set; } = new();
        [JsonPropertyName("price")] public long? Preis { get; set; }
        [JsonPropertyName("wiki")] public string? Wiki { get; set; }
        [JsonPropertyName("ways")] public List<Weg> Wege { get; set; } = new();

        /// <summary>Name und Jahr -- so heisst ein Auto in der Liste eindeutig, auch ohne car_id.</summary>
        [JsonIgnore] public string Schluessel => Name + "|" + (Jahr?.ToString() ?? string.Empty);

        [JsonIgnore] public string Anzeige => Jahr is { } j ? $"{Name} '{j % 100:00}" : Name;
    }

    private sealed class Datei
    {
        [JsonPropertyName("format")] public string? Format { get; set; }
        [JsonPropertyName("built")] public string? Gebaut { get; set; }
        [JsonPropertyName("list_updated")] public string? ListeStand { get; set; }
        [JsonPropertyName("cars")] public List<Auto>? Autos { get; set; }
    }

    public IReadOnlyList<Auto> Autos { get; }

    /// <summary>Wann der Server die Liste gebaut hat (UTC, ISO).</summary>
    public string? Gebaut { get; }

    /// <summary>Der Stand, den forza.net ueber seiner Liste nennt ("8 September 2026").</summary>
    public string? ListeStand { get; }

    private CarCollection(List<Auto> autos, string? gebaut, string? listeStand)
    {
        Autos = autos;
        Gebaut = gebaut;
        ListeStand = listeStand;
    }

    public DateTimeOffset? GebautAm =>
        DateTimeOffset.TryParse(Gebaut, System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    /// <summary>Eine Liste aus JSON -- oder null, wenn sie nicht vollstaendig ist.</summary>
    public static CarCollection? Lesen(string json)
    {
        try
        {
            var d = JsonSerializer.Deserialize<Datei>(json);
            if (d?.Format != Format || d.Autos is null || d.Autos.Count < Mindestens) { return null; }
            return new CarCollection(d.Autos.Where(a => a.Name.Length > 0).ToList(), d.Gebaut, d.ListeStand);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Die heruntergeladene Fassung im Datenordner.</summary>
    internal static string ZwischenPfad => Path.Combine(AppInfo.DataFolder, "cars", DateiName);

    /// <summary>Die mitgelieferte Fassung: data/cars/ neben der App (oder weiter oben im Baum).</summary>
    internal static string? PaketPfad()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "data", "cars", DateiName);
            if (File.Exists(p)) { return p; }
        }
        return null;
    }

    /// <summary>Die neueste Liste, die hier liegt: heruntergeladen oder mitgeliefert.</summary>
    public static CarCollection? Laden()
    {
        CarCollection? beste = null;
        foreach (var pfad in new[] { ZwischenPfad, PaketPfad() })
        {
            if (pfad is null || !File.Exists(pfad)) { continue; }
            try
            {
                var liste = Lesen(File.ReadAllText(pfad));
                if (liste is not null && (beste is null || (liste.GebautAm ?? DateTimeOffset.MinValue)
                                                           > (beste.GebautAm ?? DateTimeOffset.MinValue)))
                {
                    beste = liste;
                }
            }
            catch (IOException)
            {
            }
        }
        return beste;
    }

    /// <summary>
    /// Beim eigenen Server nach einer neueren Liste fragen. Null heisst: nichts Neueres
    /// oder nicht erreichbar -- beides kein Fehler.
    /// </summary>
    public static async Task<CarCollection?> VomServerAsync(string? basis, CarCollection? jetzt, CancellationToken ct = default,
                                                            string? ziel = null, TimeSpan? zeit = null)
    {
        ziel ??= ZwischenPfad;
        basis = (basis ?? string.Empty).Trim().TrimEnd('/');
        if (basis.Length == 0) { return null; }
        try
        {
            using var client = ServerHttp.Client(zeit ?? TimeSpan.FromSeconds(30));
            var json = await client.GetStringAsync(basis + "/api/cars", ct).ConfigureAwait(false);
            var neu = Lesen(json);
            if (neu is null) { return null; }
            if (jetzt?.GebautAm is { } alt && neu.GebautAm is { } frisch && frisch <= alt) { return null; }
            Directory.CreateDirectory(Path.GetDirectoryName(ziel)!);
            var zwischen = ziel + ".tmp";
            await File.WriteAllTextAsync(zwischen, json, ct).ConfigureAwait(false);
            File.Move(zwischen, ziel, overwrite: true);
            return neu;
        }
        catch (Exception)
        {
            // Offline, Server ohne /api/cars (alte Fassung), Zeitueberschreitung.
            return null;
        }
    }
}

/// <summary>
/// Welche Autos der Nutzer hat: die zuletzt gelesene Garage und seine eigenen Haken.
/// </summary>
/// <remarks>
/// In <c>config/owned_cars.json</c> neben <c>car_notes.json</c> -- persoenlich, geht in
/// kein Paket und in kein git.
///
/// DREI QUELLEN, in dieser Rangfolge:
/// 1. Ein Haken von Hand -- gesetzt oder ausdruecklich entfernt. Er schlaegt alles:
///    auf der Xbox gibt es keine Garage zu lesen, und ein Leihwagen aus einem Rennen
///    ist gefahren, aber nicht gekauft.
/// 2. Die Garage aus dem Spielspeicher (nur am PC). Ist sie einmal gelesen, gilt sie
///    allein: ein verkauftes Auto steht dann nicht mehr als "hast du" da.
/// 3. Ohne gelesene Garage: jedes Auto, das mit der App gefahren wurde.
/// </remarks>
internal sealed class OwnedCars
{
    [JsonPropertyName("_")] public string Hinweis { get; set; } =
        "Cars you own, for the Car collection tab. Written by FH Companion.";
    [JsonPropertyName("garage_read")] public DateTimeOffset? GarageGelesen { get; set; }
    [JsonPropertyName("garage_ids")] public List<int> GarageIds { get; set; } = new();

    /// <summary>"Name|Jahr" -> true (hab ich) / false (hab ich nicht), von Hand.</summary>
    [JsonPropertyName("marked")] public Dictionary<string, bool> Markiert { get; set; } = new();

    /// <summary>
    /// "Name|Jahr" der Autos, die im Spiel unter "My Cars" gerahmt waren -- sie gehoeren dem
    /// Spieler, auch wenn die App ihre car_id nicht kennt (seit 2026-09-29).
    /// </summary>
    [JsonPropertyName("seen_in_my_cars")] public List<string> GesehenListe { get; set; } = new();

    [JsonIgnore] private HashSet<string>? _gesehen;

    private bool Gesehen(string schluessel) => (_gesehen ??= GesehenListe.ToHashSet()).Contains(schluessel);

    /// <summary>Nach jedem Lernen aus "My Cars": der Reiter baut sich neu.</summary>
    public static event Action? Geaendert;

    /// <summary>Den Besitz hat jemand anderes geaendert: die Reiter neu zeigen lassen.</summary>
    internal static void Melden() => Geaendert?.Invoke();

    /// <summary>Mehrere Autos einer My-Cars-Seite auf einmal merken -- ein Schreiben, nicht zwoelf.</summary>
    public static int GesehenMerkenAlle(IEnumerable<CarCollection.Auto> autos, string? pfad = null)
    {
        var o = Laden(pfad);
        var neu = autos.Where(a => !o.Gesehen(a.Schluessel)).Select(a => a.Schluessel).Distinct().ToList();
        if (neu.Count == 0) { return 0; }
        o.GesehenListe.AddRange(neu);
        o._gesehen = null;
        o.Speichern();
        Geaendert?.Invoke();
        return neu.Count;
    }

    /// <summary>Ein Auto aus "My Cars" merken. Gibt zurueck, ob es neu war.</summary>
    public static bool GesehenMerken(CarCollection.Auto auto, string? pfad = null)
    {
        var o = Laden(pfad);
        if (o.Gesehen(auto.Schluessel)) { return false; }
        o.GesehenListe.Add(auto.Schluessel);
        o._gesehen = null;
        o.Speichern();
        Geaendert?.Invoke();
        return true;
    }

    [JsonIgnore] private string _pfad = string.Empty;

    internal enum Grund { Keiner, VonHand, Garage, Gefahren, MeineAutos }

    /// <summary>Hat der Nutzer dieses Auto -- und woher die App das weiss.</summary>
    public (bool Hat, Grund Warum) Besitz(CarCollection.Auto auto, IReadOnlySet<int> gefahren)
    {
        if (Markiert.TryGetValue(auto.Schluessel, out var hand)) { return (hand, Grund.VonHand); }
        return OhneHand(auto, gefahren);
    }

    /// <summary>Was ohne Haken gilt: in "My Cars" gesehen, sonst Garage oder gefahren.</summary>
    private (bool Hat, Grund Warum) OhneHand(CarCollection.Auto auto, IReadOnlySet<int> gefahren) =>
        Gesehen(auto.Schluessel) ? (true, Grund.MeineAutos) : Automatisch(auto, gefahren);

    /// <summary>
    /// Nicht als fehlend zu melden, weil die App es nicht wissen KANN: die Garage ist
    /// gelesen, enthaelt Autos ohne Zuordnung, und diesem Auto fehlt die car_id.
    /// </summary>
    public bool Unsicher(CarCollection.Auto auto, IReadOnlySet<int> gefahren, int nichtErkannt) =>
        nichtErkannt > 0 && GarageGelesen is not null && auto.AlleIds.Count == 0 && !Besitz(auto, gefahren).Hat;

    /// <summary>Was ohne Haken gilt -- um einen Haken, der nichts aendert, gar nicht erst zu merken.</summary>
    public (bool Hat, Grund Warum) Automatisch(CarCollection.Auto auto, IReadOnlySet<int> gefahren)
    {
        if (GarageGelesen is not null)
        {
            return auto.AlleIds.Any(GarageIds.Contains) ? (true, Grund.Garage) : (false, Grund.Keiner);
        }
        return auto.AlleIds.Any(gefahren.Contains) ? (true, Grund.Gefahren) : (false, Grund.Keiner);
    }

    /// <summary>
    /// Wie viele Autos der Garage in der Liste kein Gegenstueck haben -- die App kennt
    /// ihre car_id noch nicht (kein gescanntes Board nannte sie) oder das Spiel fuehrt
    /// sie nicht in der offiziellen Liste (Verkehrsautos, Sonderfahrzeuge).
    /// </summary>
    public int NichtErkannt(CarCollection liste)
    {
        var bekannt = liste.Autos.SelectMany(a => a.AlleIds).ToHashSet();
        return GarageIds.Distinct().Count(i => !bekannt.Contains(i));
    }

    /// <summary>Einen Haken setzen -- faellt er mit dem Automatischen zusammen, wird er vergessen.</summary>
    public void Setze(CarCollection.Auto auto, bool hat, IReadOnlySet<int> gefahren)
    {
        if (OhneHand(auto, gefahren).Hat == hat) { Markiert.Remove(auto.Schluessel); }
        else { Markiert[auto.Schluessel] = hat; }
    }

    public void GarageMerken(IEnumerable<int> ids)
    {
        GarageIds = ids.Distinct().OrderBy(i => i).ToList();
        GarageGelesen = DateTimeOffset.Now;
    }

    public static OwnedCars Laden(string? pfad = null)
    {
        pfad ??= StandardPfad();
        OwnedCars? o = null;
        try
        {
            if (File.Exists(pfad)) { o = JsonSerializer.Deserialize<OwnedCars>(File.ReadAllText(pfad)); }
        }
        catch (Exception)
        {
        }
        o ??= new OwnedCars();
        // "marked": null oder "garage_ids": null -- von Hand bearbeitet oder von einer
        // anderen Fassung geschrieben. JSON setzt dann null statt der leeren Vorgabe, und
        // der Reiter fiele beim ersten Auto mit einer NullReferenceException um.
        o.Markiert ??= new Dictionary<string, bool>();
        o.GarageIds ??= new List<int>();
        o.GesehenListe ??= new List<string>();
        o._pfad = pfad;
        return o;
    }

    public void Speichern()
    {
        if (_pfad.Length == 0) { return; }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_pfad)!);
            var zwischen = _pfad + ".tmp";
            File.WriteAllText(zwischen, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(zwischen, _pfad, overwrite: true);
        }
        catch (Exception)
        {
            // Schreibgeschuetzter Ordner: die Haken gelten dann bis zum Beenden.
        }
    }

    private static string StandardPfad()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var config = Path.Combine(dir.FullName, "config");
            if (Directory.Exists(config)) { return Path.Combine(config, "owned_cars.json"); }
        }
        return Path.Combine(AppContext.BaseDirectory, "config", "owned_cars.json");
    }
}
