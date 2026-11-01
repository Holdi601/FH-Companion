using System.Text.Json.Serialization;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Ein Einschlag: wie hart (G), woher (Grad: 0 vorn, 90 rechts, 180 hinten, 270 links), bei welchem Tempo.
/// </summary>
internal readonly record struct AufprallEreignis(float G, float Winkel, float Kmh, DateTime Zeit)
{
    /// <summary>Die Richtung in Worten: vorn, vorn rechts, rechts ... (je 45 Grad).</summary>
    public string Richtung => AufprallMelder.Richtungsname(Winkel);
}

/// <summary>
/// Erkennt Einschlaege in der Telemetrie (seit 2026-11-01) -- an einem SPRUNG, nicht an der Hoehe.
/// </summary>
/// <remarks>
/// Gemessen am 2026-11-01 an 240 Runden aus dem Archiv (2 Millionen Pakete):
/// * Hohe G allein sind kein Einschlag. 4-6 G haelt ein Auto mit Abtrieb beim Bremsen aus 260 km/h
///   ueber 20 Pakete lang; das waeren Hunderte falsche Einschlaege.
/// * Ein Einschlag kommt von einem Paket aufs naechste. Gegen das Mittel der 150 ms davor springt die
///   Beschleunigung in 0,1 % der Pakete um mehr als 4,2 G. Ab 3 G Sprung verlor das Auto im Mittel
///   17 km/h, ab 6 G immer, ab 10 G im Mittel 66 km/h.
/// * Forza kappt die Beschleunigung bei 20 G; schwerere Einschlaege sehen alle gleich aus.
/// Die Richtung ist die des Sprungs, umgedreht: wer vorn einschlaegt, wird nach hinten gerissen.
/// </remarks>
internal sealed class AufprallMelder
{
    private const float Fenster = 0.15f;
    private const float Sammeln = 0.12f;
    private const float Ruhe = 0.6f;
    private readonly Queue<(DateTime Zeit, float X, float Z)> _davor = new();
    private float _sx, _sz;
    private bool _laeuft;
    private DateTime _start;
    private float _mx, _mz, _spitze, _winkel, _kmh;
    private DateTime _ruheBis = DateTime.MinValue;

    /// <summary>Ab welchem Sprung (G) es ein Einschlag ist. Ab Werk 3,5.</summary>
    public float Schwelle { get; set; } = 3.5f;

    /// <summary>Gerade ein Einschlag im Gang (oder eben vorbei)? Dann zaehlt nichts als Fahrdynamik.</summary>
    public bool Unruhig(DateTime jetzt) => _laeuft || jetzt < _ruheBis;

    public void Vergessen()
    {
        _davor.Clear();
        _sx = _sz = 0f;
        _laeuft = false;
    }

    /// <summary>
    /// Ein Paket: Beschleunigung in G (x rechts, z vorwaerts, ungekappt). Gibt den Einschlag zurueck,
    /// sobald er fertig gesammelt ist (120 ms nach dem Beginn), sonst null.
    /// </summary>
    public AufprallEreignis? Aufnehmen(float x, float z, float kmh, bool zaehlt, DateTime jetzt)
    {
        if (!float.IsFinite(x) || !float.IsFinite(z)) { return null; }
        if (_laeuft)
        {
            var dx = x - _mx;
            var dz = z - _mz;
            var sprung = MathF.Sqrt(dx * dx + dz * dz);
            if (sprung > _spitze)
            {
                _spitze = sprung;
                _winkel = Winkel(dx, dz);
            }
            if ((jetzt - _start).TotalSeconds < Sammeln) { return null; }
            _laeuft = false;
            _ruheBis = jetzt.AddSeconds(Ruhe);
            Vergessen();
            return new AufprallEreignis(MathF.Min(_spitze, 40f), _winkel, _kmh, _start);
        }
        while (_davor.Count > 0 && (jetzt - _davor.Peek().Zeit).TotalSeconds > Fenster)
        {
            var alt = _davor.Dequeue();
            _sx -= alt.X;
            _sz -= alt.Z;
        }
        if (_davor.Count >= 3 && zaehlt && kmh > 5f && jetzt >= _ruheBis)
        {
            var mx = _sx / _davor.Count;
            var mz = _sz / _davor.Count;
            var dx = x - mx;
            var dz = z - mz;
            var sprung = MathF.Sqrt(dx * dx + dz * dz);
            if (sprung >= Schwelle)
            {
                _laeuft = true;
                _start = jetzt;
                _mx = mx;
                _mz = mz;
                _spitze = sprung;
                _winkel = Winkel(dx, dz);
                _kmh = kmh;
                return null;
            }
        }
        _davor.Enqueue((jetzt, x, z));
        _sx += x;
        _sz += z;
        return null;
    }

    /// <summary>Woher der Stoss kam: der Sprung umgedreht, 0 vorn, im Uhrzeigersinn.</summary>
    internal static float Winkel(float dx, float dz)
    {
        var w = MathF.Atan2(-dx, -dz) * 180f / MathF.PI;
        return (w % 360f + 360f) % 360f;
    }

    internal static string Richtungsname(float winkel) => ((int)MathF.Round(((winkel % 360f) + 360f) % 360f / 45f) % 8) switch
    {
        0 => Loc.T("front"),
        1 => Loc.T("front right"),
        2 => Loc.T("right"),
        3 => Loc.T("rear right"),
        4 => Loc.T("rear"),
        5 => Loc.T("rear left"),
        6 => Loc.T("left"),
        _ => Loc.T("front left"),
    };
}

/// <summary>Woher ein Einschlag kommen muss, damit eine Regel greift.</summary>
internal enum AufprallSeite { Any, Front, Right, Rear, Left, Custom }

/// <summary>
/// Ein Klang fuer Einschlaege (seit 2026-11-01): diese Datei, wenn der Stoss aus dieser Richtung kommt
/// und so hart ist. Die erste passende Regel der Liste spielt.
/// </summary>
internal sealed class AufprallRegel
{
    [JsonPropertyName("on")] public bool On { get; set; } = true;
    [JsonPropertyName("file")] public string File { get; set; } = string.Empty;
    [JsonPropertyName("side")] public AufprallSeite Side { get; set; } = AufprallSeite.Any;
    /// <summary>Bei "Custom": von ... bis ... Grad, im Uhrzeigersinn, 0 vorn. 315 bis 45 geht ueber vorn.</summary>
    [JsonPropertyName("from")] public float From { get; set; }
    [JsonPropertyName("to")] public float To { get; set; } = 360f;
    [JsonPropertyName("minG")] public float MinG { get; set; } = 3.5f;
    /// <summary>0: nach oben offen.</summary>
    [JsonPropertyName("maxG")] public float MaxG { get; set; }
    [JsonPropertyName("volume")] public int Volume { get; set; } = 80;

    public bool Passt(AufprallEreignis e)
    {
        if (!On || string.IsNullOrWhiteSpace(File)) { return false; }
        if (e.G < MinG || (MaxG > 0f && e.G > MaxG)) { return false; }
        var (von, bis) = Side switch
        {
            AufprallSeite.Front => (315f, 45f),
            AufprallSeite.Right => (45f, 135f),
            AufprallSeite.Rear => (135f, 225f),
            AufprallSeite.Left => (225f, 315f),
            AufprallSeite.Custom => (From, To),
            _ => (0f, 360f),
        };
        return ImBogen(e.Winkel, von, bis);
    }

    /// <summary>Liegt der Winkel zwischen von und bis (im Uhrzeigersinn, auch ueber 0 hinweg)?</summary>
    internal static bool ImBogen(float winkel, float von, float bis)
    {
        static float N(float w) => ((w % 360f) + 360f) % 360f;
        if (bis - von >= 360f) { return true; }
        var w = N(winkel);
        var a = N(von);
        var b = N(bis);
        return a <= b ? w >= a && w <= b : w >= a || w <= b;
    }
}

/// <summary>Spielt den Klang zu einem Einschlag -- ohne den Faden aufzuhalten, der ihn meldet.</summary>
internal static class AufprallKlang
{
    // Ein Spieler lebt, bis sein Klang zu Ende ist -- sonst raeumt ihn die Muellabfuhr mitten darin weg.
    private static readonly HashSet<MediaPlayer> Laufend = new();

    /// <summary>Die erste passende Regel, oder null.</summary>
    public static AufprallRegel? Regel(OverlaySettings s, AufprallEreignis e) =>
        s.ImpactSounds.FirstOrDefault(r => r.Passt(e));

    public static void Melden(OverlaySettings s, AufprallEreignis e)
    {
        if (Regel(s, e) is { } regel) { Spielen(regel.File, regel.Volume); }
    }

    /// <summary>Spielt die Datei; <paramref name="fertig"/> sagt, ob sie zu Ende lief (fuer die Pruefung).</summary>
    public static void Spielen(string datei, int lautstaerke, Action<bool>? fertig = null)
    {
        if (!System.IO.File.Exists(datei))
        {
            fertig?.Invoke(false);
            return;
        }
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var spieler = new MediaPlayer
                {
                    Volume = Math.Clamp(lautstaerke, 0, 100) / 100.0,
                    AudioCategory = MediaPlayerAudioCategory.GameEffects,
                    Source = MediaSource.CreateFromUri(new Uri(Path.GetFullPath(datei))),
                };
                void Weg(bool gut)
                {
                    lock (Laufend) { Laufend.Remove(spieler); }
                    try { spieler.Dispose(); } catch (Exception) { }
                    fertig?.Invoke(gut);
                }
                spieler.MediaEnded += (_, _) => Weg(true);
                spieler.MediaFailed += (_, _) => Weg(false);
                lock (Laufend) { Laufend.Add(spieler); }
                spieler.Play();
            }
            catch (Exception)
            {
                // Eine Datei, die nicht spielt, ist kein Grund, das Fahren zu stoeren.
                fertig?.Invoke(false);
            }
        });
    }
}
