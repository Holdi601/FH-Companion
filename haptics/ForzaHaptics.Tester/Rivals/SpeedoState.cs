using System.Text.Json;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>Was ein Tacho in einem Bild zeigt -- schon geglaettet, schon in Anzeigeeinheiten.</summary>
internal readonly record struct SpeedoBild(
    float Rpm, float MaxRpm, float IdleRpm, float RedlineRpm,
    float SpeedMs, float TopSpeedMs, int Gear,
    float PowerW, float BoostPsi, float Throttle, float Brake,
    bool Wheelspin, bool WheelLock, bool AtLimiter,
    float TripM, float RaceM, float OdometerM,
    float RaceTime, float LapTime,
    // Animation: Sekunden seit dem letzten Gangwechsel, laufende Uhr fuer Pulsieren und Blinken.
    float SeitGangwechsel, double Uhr)
{
    public float RpmAnteil => MaxRpm > 0f ? Math.Clamp(Rpm / MaxRpm, 0f, 1.05f) : 0f;
    public float RotAnteil => MaxRpm > 0f ? Math.Clamp(RedlineRpm / MaxRpm, 0.3f, 1f) : 0.9f;
    public bool ImRoten => MaxRpm > 0f && Rpm >= RedlineRpm;
}

/// <summary>
/// Die Daten des Tachos aus der Telemetrie (seit 2026-10-18): geglaettete Nadel, gelernter
/// Schaltpunkt, Tages- und Gesamtstrecke, Schlupf und Blockieren.
/// </summary>
/// <remarks>
/// ## Der rote Bereich
///
/// Das Spiel schickt <c>EngineMaxRpm</c> (den Begrenzer), aber keinen Schaltpunkt. Gelernt wird
/// er je Auto: bei Vollgas die Drehzahl mit der hoechsten Leistung (<c>Power</c>), in 100er-
/// Schritten. Erst wenn Vollgas ueber mehr als 30 % des Drehzahlbands gesehen wurde, gilt der
/// gelernte Wert -- vorher 90 % der Hoechstdrehzahl. Gemerkt in DataFolder/speedo_shift.json.
///
/// ## Leistung
///
/// <c>Power</c> ist, was der Motor nach Angabe des Spiels gerade abgibt -- das Spiel nennt
/// keine Leistung am Rad. Bremst der Motor, ist sie negativ; angezeigt wird dann 0.
///
/// ## TC und ABS
///
/// Die Telemetrie sagt nicht, ob eine Fahrhilfe eingreift. Die Lampen leuchten, wenn die
/// angetriebenen Raeder durchdrehen (Schlupf ueber 0,2) oder ein Rad beim Bremsen blockiert
/// (unter -0,2) -- also genau dann, wenn eine Fahrhilfe eingreifen wuerde.
///
/// ## Die Uhr des Spiels
///
/// Die Tageszeit im Spiel steht in keinem Telemetriefeld. Angeboten werden die echte Uhr,
/// die Renn- und die Rundenzeit.
/// </remarks>
internal sealed class SpeedoState
{
    private float _rpm;
    private float _speed;
    private int _gang;
    private DateTime _gangSeit = DateTime.MinValue;
    private DateTime _zuletzt = DateTime.MinValue;
    private float _trip;
    private float _odometer;
    private float _odometerGespeichert;
    private float _topSpeed = 200f / 3.6f;
    private int _auto;
    private readonly Dictionary<int, Dictionary<int, float>> _leistung = new();
    private readonly Dictionary<int, float> _maxRpm = new();
    private bool _geaendert;
    private DateTime _gespeichert = DateTime.UtcNow;
    private readonly DateTime _start = DateTime.UtcNow;

    public SpeedoBild Letztes { get; private set; }

    public static string SchaltDatei => Path.Combine(AppInfo.DataFolder, "speedo_shift.json");
    public static string StreckeDatei => Path.Combine(AppInfo.DataFolder, "speedo_odometer.json");

    private readonly bool _merken;

    /// <param name="merken">false: nichts lesen, nichts schreiben (Tests -- nie die Daten des Nutzers).</param>
    public SpeedoState(bool merken = true)
    {
        _merken = merken;
        if (merken) { Laden(); }
    }

    /// <summary>Die Tagesstrecke auf null.</summary>
    public void TripZurueck() => _trip = 0f;

    /// <summary>Ein Paket aufnehmen; <paramref name="glaettung"/> 0 = roh, 0,95 = sehr traege.</summary>
    public SpeedoBild Aufnehmen(ForzaPacket p, float glaettung, SpeedoDesign design, DateTime? jetzt = null)
    {
        var nun = jetzt ?? DateTime.UtcNow;
        var dt = _zuletzt == DateTime.MinValue ? 0f : (float)Math.Clamp((nun - _zuletzt).TotalSeconds, 0, 0.5);
        _zuletzt = nun;

        var rpm = (float)p.Get("CurrentEngineRpm");
        var max = (float)p.Get("EngineMaxRpm");
        var idle = (float)p.Get("EngineIdleRpm");
        var speed = Math.Max(0f, (float)p.Get("Speed"));
        var gang = (int)p.Get("Gear");
        var leistung = (float)p.Get("Power");
        var gas = (float)p.Get("Accel") / 255f;
        var bremse = (float)p.Get("Brake") / 255f;
        var auto = (int)p.Get("CarOrdinal");
        if (auto != _auto)
        {
            _auto = auto;
            _rpm = rpm;
            _speed = speed;
        }

        // EINE TRAEGE NADEL, DIE NICHT HINTERHERHINKT: je Paket ein Schritt Richtung Ziel; bei 60 Hz
        // und Glaettung 0,5 ist die Nadel nach drei Paketen fast da.
        var k = 1f - Math.Clamp(glaettung, 0f, 0.95f);
        _rpm += (rpm - _rpm) * k;
        _speed += (speed - _speed) * Math.Min(1f, k * 1.4f);
        if (gang != _gang)
        {
            _gang = gang;
            _gangSeit = nun;
        }
        _topSpeed = Math.Max(_topSpeed, speed);

        var rennt = p.Get("IsRaceOn") >= 0.5;
        if (rennt && dt > 0f)
        {
            _trip += speed * dt;
            _odometer += speed * dt;
        }

        if (auto > 0 && max > 1000f)
        {
            _maxRpm[auto] = max;
            if (gas > 0.97f && leistung > 1000f && rpm > idle + 200f)
            {
                var stufe = (int)(rpm / 100f);
                var tabelle = _leistung.TryGetValue(auto, out var t) ? t : _leistung[auto] = new Dictionary<int, float>();
                if (!tabelle.TryGetValue(stufe, out var alt) || leistung > alt)
                {
                    tabelle[stufe] = leistung;
                    _geaendert = true;
                }
            }
        }
        if ((nun - _gespeichert).TotalSeconds > 60) { Speichern(); }

        var rot = design.Redline == SpeedoRedline.Learned ? Schaltpunkt(auto, max) : max * design.RedlinePercent;
        var schlupf = Raeder(p, (int)p.Get("DrivetrainType"));
        var bild = new SpeedoBild(
            _rpm, max, idle, rot, _speed, _topSpeed, gang,
            Math.Max(0f, leistung), (float)p.Get("Boost"), gas, bremse,
            schlupf.Dreht && gas > 0.1f, schlupf.Blockiert && bremse > 0.1f, max > 0f && rpm >= max * 0.985f,
            _trip, (float)p.Get("DistanceTraveled"), _odometer,
            (float)p.Get("CurrentRaceTime"), (float)p.Get("CurrentLap"),
            _gangSeit == DateTime.MinValue ? 99f : (float)(nun - _gangSeit).TotalSeconds,
            (nun - _start).TotalSeconds);
        Letztes = bild;
        return bild;
    }

    /// <summary>Der gelernte Schaltpunkt dieses Autos -- oder 90 % der Hoechstdrehzahl.</summary>
    public float Schaltpunkt(int auto, float max)
    {
        if (max <= 0f) { return 0f; }
        if (_leistung.TryGetValue(auto, out var t) && t.Count >= 8)
        {
            var tief = t.Keys.Min() * 100f;
            var hoch = t.Keys.Max() * 100f;
            if ((hoch - tief) / max > 0.3f)
            {
                var beste = t.MaxBy(e => e.Value).Key * 100f + 50f;
                return Math.Clamp(beste, max * 0.6f, max * 0.99f);
            }
        }
        return max * 0.9f;
    }

    private static (bool Dreht, bool Blockiert) Raeder(ForzaPacket p, int antrieb)
    {
        float S(string ecke) => (float)p.Get("TireSlipRatio" + ecke);
        var vl = S("FrontLeft");
        var vr = S("FrontRight");
        var hl = S("RearLeft");
        var hr = S("RearRight");
        var angetrieben = antrieb switch
        {
            0 => new[] { vl, vr },
            1 => new[] { hl, hr },
            _ => new[] { vl, vr, hl, hr },
        };
        return (angetrieben.Any(s => s > 0.2f), new[] { vl, vr, hl, hr }.Any(s => s < -0.2f));
    }

    private void Laden()
    {
        try
        {
            if (File.Exists(SchaltDatei))
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, float>>>(File.ReadAllText(SchaltDatei));
                foreach (var (auto, tabelle) in d ?? new())
                {
                    if (!int.TryParse(auto, out var a)) { continue; }
                    _leistung[a] = tabelle.Where(e => int.TryParse(e.Key, out _)).ToDictionary(e => int.Parse(e.Key), e => e.Value);
                }
            }
            if (File.Exists(StreckeDatei))
            {
                using var j = JsonDocument.Parse(File.ReadAllText(StreckeDatei));
                if (j.RootElement.TryGetProperty("metres", out var m)) { _odometer = _odometerGespeichert = (float)m.GetDouble(); }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
        }
    }

    /// <summary>Gelernte Schaltpunkte und Gesamtstrecke ablegen -- hoechstens einmal je Minute.</summary>
    public void Speichern()
    {
        _gespeichert = DateTime.UtcNow;
        if (!_merken) { return; }
        try
        {
            Directory.CreateDirectory(AppInfo.DataFolder);
            if (_geaendert)
            {
                var raus = _leistung.ToDictionary(e => e.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    e => e.Value.ToDictionary(s => s.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), s => s.Value));
                File.WriteAllText(SchaltDatei, JsonSerializer.Serialize(raus));
                _geaendert = false;
            }
            if (Math.Abs(_odometer - _odometerGespeichert) > 1f)
            {
                File.WriteAllText(StreckeDatei, JsonSerializer.Serialize(new { metres = Math.Round(_odometer) }));
                _odometerGespeichert = _odometer;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Ein Beispiel, das sich bewegt -- fuer Vorschau und Galerie: ein Auto beschleunigt durch die
    /// Gaenge, schaltet am roten Bereich, bremst. <paramref name="t"/> in Sekunden.
    /// </summary>
    public static SpeedoBild Vorfuehrung(double t, bool ruhig = false)
    {
        const float max = 8500f;
        const float idle = 900f;
        if (ruhig) { t = 2.35; }
        var zyklus = t % 9.0;
        int gang;
        float rpm;
        float speed;
        float gas = 1f;
        float bremse = 0f;
        float seitWechsel;
        if (zyklus < 7.0)
        {
            // Fuenf Gaenge zu je 1,4 s: Drehzahl steigt von 4.800 bis 8.400.
            gang = 1 + (int)(zyklus / 1.4);
            var u = (float)(zyklus % 1.4 / 1.4);
            rpm = 4800f + u * 3600f;
            speed = (gang * 40f + u * 38f) / 3.6f;
            seitWechsel = (float)(zyklus % 1.4);
        }
        else
        {
            var u = (float)((zyklus - 7.0) / 2.0);
            gang = 5 - (int)(u * 3f);
            rpm = 7200f - u * 4200f;
            speed = (230f - u * 150f) / 3.6f;
            gas = 0f;
            bremse = 0.85f;
            seitWechsel = (float)((zyklus - 7.0) % 0.67);
        }
        var limiter = rpm > max * 0.985f;
        return new SpeedoBild(rpm, max, idle, 7600f, speed, 260f / 3.6f, gang,
            gas > 0.5f ? 380000f * (rpm / max) : 0f, gas > 0.5f ? 14f * (rpm / max) : -2f, gas, bremse,
            gas > 0.5f && gang == 1 && seitWechsel < 0.5f, bremse > 0.5f && seitWechsel < 0.25f, limiter,
            12_340f, 4_210f, 18_765_400f, (float)t + 63.4f, (float)(t % 80.0),
            seitWechsel, t);
    }
}
