using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Eigene Notizen zu Autos -- "untersteuert ab Kurve 3", "Reifen halten nicht".
/// </summary>
/// <remarks>
/// ## Wonach ein Auto wiedererkannt wird
///
/// Nach dem FINGERABDRUCK, nicht nach dem Namen. Der Name kommt aus dem Datensatz
/// und fehlt bei 14 von 653 Autos ganz; die Telemetrie liefert dagegen bei jedem
/// Paket Kennung, PI, Antrieb, Zylinderzahl und Drehzahlen.
///
/// Der Abdruck ist derselbe wie <see cref="RecordedLap.TuneKey"/>:
///
///     Kennung / PI / Antrieb / Zylinder / MaxDrehzahl:10 / Leerlauf:10
///
/// ## WARUM DIE LEISTUNG NICHT IM SCHLUESSEL STEHT
///
/// Weil sie keine Fahrzeugkonstante ist. `Power` ist ein Momentanwert, und die
/// hoechste je gesehene Leistung haengt davon ab, ob man mit diesem Auto schon
/// einmal im oberen Gang Vollgas gefahren ist. Als Schluesselbestandteil bekaeme
/// dasselbe Auto je nach Sitzung einen anderen Schluessel -- und damit waere die
/// Notiz weg. Die Leistung wird MITGESCHRIEBEN, als Auskunft neben dem Namen, aber
/// sie entscheidet nichts. Dieselbe Begruendung steht schon an TuneKey; hier gilt
/// sie aus demselben Grund.
///
/// ## Warum PI im Schluessel steht, obwohl es sich aendert
///
/// Weil eine andere Abstimmung ein anderes Auto ist, was Notizen angeht. "Zieht in
/// schnellen Kurven" gilt fuer den Aufbau, mit dem man das erlebt hat, nicht fuer
/// dasselbe Blech mit anderem Fahrwerk. Wer die Notiz fuer jede Abstimmung haben
/// will, traegt sie am Auto-EINTRAG ein (siehe <see cref="ByOrdinal"/>), der nur
/// die Kennung vergleicht.
///
/// ## Zwei Arten von Eintraegen (seit 2026-09-25)
///
/// Neben den Aufbauten aus der Telemetrie gibt es je Auto EINEN Modell-Eintrag,
/// Schluessel <c>car:&lt;Kennung&gt;</c>. Er entsteht, wenn das Auto gefahren, im
/// Automenue unter dem Rahmen gelesen oder aus der Garage uebernommen wird. Vorher
/// kannte der Reiter nur Autos, die in DIESER Sitzung gefahren wurden, und hielt sie
/// nur im Speicher -- nach jedem Neustart war die Liste leer, und ein Auto aus dem
/// Menue kam gar nicht hinein (die Telemetrie nennt nur das gefahrene).
///
/// Die Notiz am Modell gilt fuer jeden Aufbau; eine Notiz an einem Aufbau geht ihr vor.
/// </remarks>
internal sealed class CarNotes
{
    internal sealed class Entry
    {
        /// <summary>Der Fingerabdruck -- Schluessel und zugleich Inhalt.</summary>
        [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;

        [JsonPropertyName("ordinal")] public int Ordinal { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("pi")] public int Pi { get; set; }

        /// <summary>Spitzenleistung in kW, nur zur Auskunft.</summary>
        [JsonPropertyName("kw")] public int Kilowatts { get; set; }

        [JsonPropertyName("comment")] public string Comment { get; set; } = string.Empty;
        [JsonPropertyName("seen")] public string? LastSeen { get; set; }

        /// <summary>Woher das Auto bekannt ist: "driven", "menu", "garage".</summary>
        [JsonPropertyName("from")] public string? Source { get; set; }

        /// <summary>Der Eintrag fuer das Auto ueberhaupt, nicht fuer einen Aufbau.</summary>
        [JsonIgnore] public bool IsModel => Key.StartsWith(ModelPrefix, StringComparison.Ordinal);

        /// <summary>Die Leistung, wie ein Mensch sie nennt.</summary>
        public int HorsePower => (int)Math.Round(Kilowatts * 1.34102);
    }

    private sealed class Payload
    {
        [JsonPropertyName("_")] public string Hinweis { get; set; } =
            "Notizen zu Autos. Schluessel ist der Fingerabdruck aus der Telemetrie.";

        [JsonPropertyName("by_key")]
        public Dictionary<string, Entry> ByKey { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Options =
        new() { WriteIndented = true };

    private readonly string? _path;
    private Dictionary<string, Entry> _byKey = new(StringComparer.Ordinal);

    public CarNotes(string? path = null)
    {
        _path = path ?? FindPath();
        Load();
    }

    public int Count => _byKey.Count;

    public IReadOnlyCollection<Entry> All => _byKey.Values;

    /// <summary>Ein Auto kam dazu oder eine Notiz wurde geaendert.</summary>
    public event Action? Changed;

    internal const string ModelPrefix = "car:";

    /// <summary>Der Schluessel des Modell-Eintrags eines Autos.</summary>
    public static string ModelKey(int ordinal) => ModelPrefix + ordinal;

    /// <summary>
    /// Ein Auto als MODELL vermerken -- gefahren, im Menue gesehen oder aus der Garage.
    /// </summary>
    /// <remarks>
    /// Gespeichert wird sofort, wenn das Auto neu ist oder einen Namen bekommt: das
    /// geschieht einmal je Auto, nicht einmal je Paket.
    /// </remarks>
    public Entry NoteModel(int ordinal, string? name, string source, bool save = true)
    {
        var key = ModelKey(ordinal);
        var neu = !_byKey.TryGetValue(key, out var eintrag);
        if (neu)
        {
            eintrag = new Entry { Key = key, Ordinal = ordinal, Source = source };
            _byKey[key] = eintrag;
        }
        var geaendert = neu;
        if (RivalsAdvisor.IsRealCarName(name) && eintrag!.Name != name)
        {
            eintrag.Name = name!;
            geaendert = true;
        }
        // "gefahren" schlaegt "Menue" schlaegt "Garage" -- die staerkste Auskunft bleibt.
        if (Rang(source) > Rang(eintrag!.Source)) { eintrag.Source = source; geaendert = true; }
        eintrag.LastSeen = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        if (geaendert && save)
        {
            Save();
            Changed?.Invoke();
        }
        return eintrag;
    }

    private static int Rang(string? quelle) => quelle switch
    {
        "driven" => 3,
        "menu" => 2,
        "garage" => 1,
        _ => 0,
    };

    /// <summary>Viele Autos auf einmal -- EIN Speichern, EINE Meldung.</summary>
    public int NoteModels(IEnumerable<(int Ordinal, string? Name)> autos, string source)
    {
        var vorher = _byKey.Count;
        foreach (var (ordinal, name) in autos)
        {
            if (ordinal > 0) { NoteModel(ordinal, name, source, save: false); }
        }
        Save();
        Changed?.Invoke();
        return _byKey.Count - vorher;
    }

    /// <summary>
    /// Die Notiz, die fuer dieses Auto gilt: die des Aufbaus, sonst die des Modells,
    /// sonst die eines anderen Aufbaus desselben Autos.
    /// </summary>
    public string CommentFor(string? buildKey, int ordinal)
    {
        var aufbau = Lookup(buildKey ?? string.Empty)?.Comment;
        if (!string.IsNullOrWhiteSpace(aufbau)) { return aufbau; }
        var modell = Lookup(ModelKey(ordinal))?.Comment;
        if (!string.IsNullOrWhiteSpace(modell)) { return modell; }
        return ByOrdinal(ordinal)?.Comment ?? string.Empty;
    }

    /// <summary>Der Fingerabdruck aus den Werten, die jedes Paket mitbringt.</summary>
    public static string Fingerprint(int ordinal, int pi, int drivetrain,
                                     int cylinders, int maxRpm, int idleRpm) =>
        $"{ordinal}/{pi}/{drivetrain}/{cylinders}/{maxRpm / 10}/{idleRpm / 10}";

    /// <summary>Die Notiz zu genau diesem Aufbau.</summary>
    public Entry? Lookup(string key) =>
        !string.IsNullOrEmpty(key) && _byKey.TryGetValue(key, out var e) ? e : null;

    /// <summary>
    /// Die Notiz zu diesem AUTO, gleich welcher Abstimmung.
    /// </summary>
    /// <remarks>
    /// Fuer den Fall, dass jemand eine Notiz am Auto haben will und nicht am
    /// Aufbau. Gibt es mehrere, gewinnt die zuletzt gesehene -- die ist am
    /// ehesten die, an die der Nutzer gerade denkt.
    /// </remarks>
    public Entry? ByOrdinal(int ordinal) =>
        _byKey.Values.Where(e => e.Ordinal == ordinal
                                 && !string.IsNullOrWhiteSpace(e.Comment))
                     .OrderByDescending(e => e.LastSeen ?? string.Empty)
                     .FirstOrDefault();

    /// <summary>Ein Auto vermerken, das gerade gesehen wurde.</summary>
    /// <returns>Den Eintrag, neu oder schon bekannt.</returns>
    public Entry Note(string key, int ordinal, string name, int pi, int kilowatts)
    {
        if (!_byKey.TryGetValue(key, out var eintrag))
        {
            eintrag = new Entry { Key = key, Ordinal = ordinal };
            _byKey[key] = eintrag;
        }
        // NAME UND LEISTUNG NACHFUEHREN, KOMMENTAR NIE.
        // Der Name kann sich bessern (der Datensatz lernt dazu), die Spitzenleistung
        // steigt, wenn man das Auto endlich einmal ausfaehrt. Was der Nutzer
        // geschrieben hat, fasst hier nichts an.
        if (RivalsAdvisor.IsRealCarName(name)) { eintrag.Name = name; }
        if (pi > 0) { eintrag.Pi = pi; }
        if (kilowatts > eintrag.Kilowatts) { eintrag.Kilowatts = kilowatts; }
        eintrag.LastSeen = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        eintrag.Source ??= "driven";
        return eintrag;
    }

    /// <summary>Den Kommentar setzen und ablegen.</summary>
    public void SetComment(string key, string comment)
    {
        if (string.IsNullOrEmpty(key)) { return; }
        if (!_byKey.TryGetValue(key, out var eintrag))
        {
            eintrag = new Entry { Key = key };
            _byKey[key] = eintrag;
        }
        eintrag.Comment = comment ?? string.Empty;
        Save();
        Changed?.Invoke();
    }

    public void Remove(string key)
    {
        if (_byKey.Remove(key)) { Save(); Changed?.Invoke(); }
    }

    public void Save()
    {
        if (_path is null) { return; }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path,
                JsonSerializer.Serialize(new Payload { ByKey = _byKey }, Options));
        }
        catch (Exception)
        {
            // Eine nicht schreibbare Notizdatei darf das Overlay nicht anhalten.
        }
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) { return; }
        try
        {
            // utf-8 MIT Stueckliste zulassen: wer die Datei in PowerShell oder
            // Notepad bearbeitet, bekommt eine -- und ein stilles Scheitern hier
            // saehe aus wie "alle Notizen weg".
            var text = File.ReadAllText(_path).TrimStart('﻿');
            var payload = JsonSerializer.Deserialize<Payload>(text, Options);
            if (payload?.ByKey is not null)
            {
                _byKey = new Dictionary<string, Entry>(payload.ByKey,
                                                       StringComparer.Ordinal);
                foreach (var (k, v) in _byKey) { v.Key = k; }
            }
        }
        catch (Exception)
        {
            _byKey = new Dictionary<string, Entry>(StringComparer.Ordinal);
        }
    }

    private static string? FindPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var configDir = Path.Combine(dir.FullName, "config");
            if (Directory.Exists(configDir))
            {
                return Path.Combine(configDir, "car_notes.json");
            }
        }
        return Path.Combine(AppContext.BaseDirectory, "config", "car_notes.json");
    }
}
