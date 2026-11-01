using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>Was ein Auto im G-Meter gehalten hat (seit 2026-11-01): Spitzen, Huelle, Fahrzeit, Einschlaege.</summary>
internal sealed class GMeterRekord
{
    [JsonPropertyName("outline")] public float[] Huelle { get; set; } = new float[GMeterBild.Sektoren];
    [JsonPropertyName("right")] public float Rechts { get; set; }
    [JsonPropertyName("left")] public float Links { get; set; }
    [JsonPropertyName("accel")] public float Vor { get; set; }
    [JsonPropertyName("brake")] public float Brems { get; set; }
    [JsonPropertyName("up")] public float Hoch { get; set; }
    [JsonPropertyName("down")] public float Runter { get; set; }
    [JsonPropertyName("seconds")] public float Fahrzeit { get; set; }
    [JsonPropertyName("impacts")] public int Aufpralle { get; set; }
    [JsonPropertyName("impactG")] public float AufprallG { get; set; }
    [JsonPropertyName("impactAngle")] public float AufprallWinkel { get; set; }
    [JsonPropertyName("impactKmh")] public float AufprallKmh { get; set; }
    [JsonPropertyName("impactAt")] public DateTime? AufprallAm { get; set; }
}

/// <summary>
/// Die Rekorde aller Autos, unter DataFolder/gmeter_cars.json. Das G-Meter passt seine Skala daran an
/// und zeigt die Huelle des Autos, nicht die der Sitzung.
/// </summary>
internal sealed class GMeterRekorde
{
    private static readonly JsonSerializerOptions Optionen = new() { WriteIndented = false };
    private readonly object _sperre = new();
    private readonly string _datei;
    private Dictionary<string, GMeterRekord> _autos = new();
    private bool _geaendert;

    public GMeterRekorde(string? datei = null)
    {
        _datei = datei ?? Path.Combine(AppInfo.DataFolder, "gmeter_cars.json");
        try
        {
            if (File.Exists(_datei))
            {
                _autos = JsonSerializer.Deserialize<Dictionary<string, GMeterRekord>>(File.ReadAllText(_datei), Optionen) ?? new();
            }
        }
        catch (Exception)
        {
            // Eine kaputte Datei kostet die Rekorde, nicht das G-Meter.
            _autos = new();
        }
    }

    private static string Schluessel(int auto) => auto.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public GMeterRekord? Fuer(int auto)
    {
        lock (_sperre) { return _autos.TryGetValue(Schluessel(auto), out var r) ? r : null; }
    }

    public void Setze(int auto, GMeterRekord r)
    {
        lock (_sperre)
        {
            _autos[Schluessel(auto)] = r;
            _geaendert = true;
        }
    }

    public void Vergessen(int auto)
    {
        lock (_sperre) { _geaendert |= _autos.Remove(Schluessel(auto)); }
    }

    public int Anzahl
    {
        get { lock (_sperre) { return _autos.Count; } }
    }

    public void Speichern()
    {
        string text;
        lock (_sperre)
        {
            if (!_geaendert) { return; }
            text = JsonSerializer.Serialize(_autos, Optionen);
            _geaendert = false;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_datei)!);
            var neu = _datei + ".neu";
            File.WriteAllText(neu, text);
            File.Move(neu, _datei, overwrite: true);
        }
        catch (Exception)
        {
            lock (_sperre) { _geaendert = true; }
        }
    }
}
