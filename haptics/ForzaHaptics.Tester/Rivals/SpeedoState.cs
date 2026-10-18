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
    /// <summary>Stabilitaet: 0 ruhig, 1 an der Grenze (gleich rutscht es), 2 rutscht.</summary>
    public int Stab { get; init; }

    /// <summary>Schaltempfehlung: +1 jetzt hochschalten, -1 jetzt runterschalten, 0 nichts.</summary>
    public int Rat { get; init; }

    /// <summary>Der Gang mit der meisten Leistung bei diesem Tempo -- 0, solange unbekannt.</summary>
    public int BesterGang { get; init; }

    public float RpmAnteil => MaxRpm > 0f ? Math.Clamp(Rpm / MaxRpm, 0f, 1.05f) : 0f;
    public float RotAnteil => MaxRpm > 0f ? Math.Clamp(RedlineRpm / MaxRpm, 0.3f, 1f) : 0.9f;
    public bool ImRoten => MaxRpm > 0f && Rpm >= RedlineRpm;
}

/// <summary>
/// Ab wann die Lampen TC, ABS und STAB angehen -- aus den drei Reglern (0..100, 50 = Vorgabe).
/// </summary>
/// <remarks>
/// GEMESSEN an 60 eigenen Runden (2026-10-18; Forza normiert Schlupf so, dass 1 die Haftgrenze
/// ist): beim Bremsen liegt der kleinste Schlupf im Median bei -1,6 -- das ABS des Spiels haelt das
/// Rad an der Grenze --, echtes Blockieren erst unter -3 (10 % der Bremszeit). Am Gas liegt das
/// angetriebene Rad nur 5 % der Zeit ueber 1,5. In Kurven liegt der kombinierte Schlupf 9 % der Zeit
/// ueber 2, 3 % ueber 3. Die erste Fassung schaltete bei 0,2 -- beim Bremsen 96 % der Zeit an.
/// Ein Regler verdoppelt (0) oder halbiert (100) die Schwelle.
/// </remarks>
internal readonly record struct SpeedoSchwellen(float Spin, float Lock, float StabWarn, float StabRutsch)
{
    public static SpeedoSchwellen Standard { get; } = Aus(50, 50, 50);

    /// <summary>Die Werte dieses Autos, wenn es eigene hat -- sonst die allgemeinen.</summary>
    public static SpeedoSchwellen Fuer(OverlaySettings s, int auto)
    {
        var w = Regler(s, auto);
        return Aus(w[0], w[1], w[2]);
    }

    /// <summary>Die drei Reglerwerte fuer dieses Auto: eigene oder allgemeine.</summary>
    public static int[] Regler(OverlaySettings s, int auto) =>
        s.SpeedoCarSensitivity is { } je
        && je.TryGetValue(auto.ToString(System.Globalization.CultureInfo.InvariantCulture), out var w) && w is { Length: 3 }
            ? w
            : new[] { s.SpeedoSpinSensitivity, s.SpeedoLockSensitivity, s.SpeedoStabSensitivity };

    public static SpeedoSchwellen Aus(int spin, int sperre, int stab)
    {
        static float F(int regler) => (float)Math.Pow(2.0, (50 - Math.Clamp(regler, 0, 100)) / 50.0);
        var stabF = F(stab);
        return new SpeedoSchwellen(1.5f * F(spin), 3f * F(sperre), 2f * stabF, 3f * stabF);
    }
}

/// <summary>Die rohen Messwerte der Lampen -- fuer die Anzeige "jetzt" beim Einstellen.</summary>
internal readonly record struct SpeedoMesswerte(float Spin, float Lock, float Stab);

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
/// angetriebenen Raeder durchdrehen oder ein Rad beim Bremsen blockiert -- Schwellen und
/// Begruendung bei <see cref="SpeedoSchwellen"/>. STAB warnt, bevor das Auto in der Kurve
/// wegrutscht: der kombinierte Schlupf eines Rades, das quer laeuft (Schraeglauf ab 0,3), ueber
/// der Warnschwelle = gelb, ueber der Rutschschwelle = rot. Jede Lampe geht erst an, wenn der Wert
/// 80 ms ueber der Schwelle lag, und erst aus, wenn er 250 ms darunter (unter 85 %) blieb -- sonst
/// flackert sie mit jedem Paket.
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

    /// <summary>Die letzten rohen Messwerte der Lampen.</summary>
    public SpeedoMesswerte Messwerte { get; private set; }

    /// <summary>Das Auto des letzten Pakets (CarOrdinal).</summary>
    public int Auto => _auto;

    private readonly Lampe _tc = new();
    private readonly Lampe _abs = new();
    private readonly Lampe _stabWarn = new();
    private readonly Lampe _stabRutsch = new();

    /// <summary>Eine Lampe mit Verzoegerung beim An- und Ausgehen.</summary>
    private sealed class Lampe
    {
        private DateTime _ueberSeit = DateTime.MinValue;
        private DateTime _unterSeit = DateTime.MinValue;
        public bool An { get; private set; }

        public bool Setze(bool ueber, bool unter, DateTime nun)
        {
            if (ueber)
            {
                _unterSeit = DateTime.MinValue;
                if (_ueberSeit == DateTime.MinValue) { _ueberSeit = nun; }
                if (!An && (nun - _ueberSeit).TotalMilliseconds >= 80) { An = true; }
            }
            else
            {
                _ueberSeit = DateTime.MinValue;
                if (unter)
                {
                    if (_unterSeit == DateTime.MinValue) { _unterSeit = nun; }
                    if (An && (nun - _unterSeit).TotalMilliseconds >= 250) { An = false; }
                }
                else
                {
                    _unterSeit = DateTime.MinValue;
                }
            }
            return An;
        }
    }

    public static string SchaltDatei => Path.Combine(AppInfo.DataFolder, "speedo_shift.json");
    public static string StreckeDatei => Path.Combine(AppInfo.DataFolder, "speedo_odometer.json");
    public static string GangDatei => Path.Combine(AppInfo.DataFolder, "speedo_gears.json");

    /// <summary>Je Auto und Gang: Drehzahl je Meter pro Sekunde (die Uebersetzung bis zum Rad).</summary>
    private readonly Dictionary<int, Dictionary<int, float>> _uebersetzung = new();
    private bool _gaengeGeaendert;
    private readonly Lampe _hoch = new();
    private readonly Lampe _runter = new();

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
    public SpeedoBild Aufnehmen(ForzaPacket p, float glaettung, SpeedoDesign design, DateTime? jetzt = null) =>
        Aufnehmen(p, glaettung, design, SpeedoSchwellen.Standard, jetzt);

    public SpeedoBild Aufnehmen(ForzaPacket p, float glaettung, SpeedoDesign design, SpeedoSchwellen schwellen, DateTime? jetzt = null)
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
        var m = Messen(p, (int)p.Get("DrivetrainType"));
        // DIE UEBERSETZUNG JE GANG LERNEN (seit 2026-10-18): Drehzahl / Tempo, nur wenn die Kupplung
        // greift, das Rad nicht durchdreht und der Gang schon eine Weile drin ist -- gleitend, damit
        // ein neues Tuning (andere Uebersetzung, gleiches Auto) sich von selbst durchsetzt.
        var kupplung = (float)p.Get("Clutch") / 255f;
        if (auto > 0 && gang is >= 1 and <= 10 && speed > 6f && kupplung < 0.1f && m.Spin < 0.5f
            && max > 1000f && rpm < max * 0.99f && _gangSeit != DateTime.MinValue && (nun - _gangSeit).TotalSeconds > 0.4)
        {
            var je = _uebersetzung.TryGetValue(auto, out var u) ? u : _uebersetzung[auto] = new Dictionary<int, float>();
            var wert = rpm / speed;
            // Ein neuer Gang aendert die Schaltpunkte: die gemerkten weg.
            if (!je.ContainsKey(gang)) { _hochpunkte.Clear(); }
            je[gang] = je.TryGetValue(gang, out var alt) && alt > 0f ? alt + (wert - alt) * 0.05f : wert;
            _gaengeGeaendert = true;
        }
        if ((nun - _gespeichert).TotalSeconds > 60) { Speichern(); }

        var rat = Beraten(auto, gang, rpm, speed, max, idle, gas, nun);
        var rot = design.Redline == SpeedoRedline.Learned
            ? (rat.Hochpunkt > 0f ? rat.Hochpunkt : Schaltpunkt(auto, max))
            : max * design.RedlinePercent;
        // Nur, wo es etwas bedeutet: Gas fuer TC, Bremse fuer ABS, Fahrt (ueber 25 km/h) fuer STAB.
        var spin = gas > 0.1f ? m.Spin : 0f;
        var sperre = bremse > 0.1f ? m.Lock : 0f;
        var stab = speed > 7f ? m.Stab : 0f;
        Messwerte = new SpeedoMesswerte(spin, sperre, stab);
        var tc = _tc.Setze(spin > schwellen.Spin, spin < schwellen.Spin * 0.85f, nun);
        var abs = _abs.Setze(sperre > schwellen.Lock, sperre < schwellen.Lock * 0.85f, nun);
        var warn = _stabWarn.Setze(stab > schwellen.StabWarn, stab < schwellen.StabWarn * 0.85f, nun);
        var rutscht = _stabRutsch.Setze(stab > schwellen.StabRutsch, stab < schwellen.StabRutsch * 0.85f, nun);
        var bild = new SpeedoBild(
            _rpm, max, idle, rot, _speed, _topSpeed, gang,
            Math.Max(0f, leistung), (float)p.Get("Boost"), gas, bremse,
            tc, abs, max > 0f && rpm >= max * 0.985f,
            _trip, (float)p.Get("DistanceTraveled"), _odometer,
            (float)p.Get("CurrentRaceTime"), (float)p.Get("CurrentLap"),
            _gangSeit == DateTime.MinValue ? 99f : (float)(nun - _gangSeit).TotalSeconds,
            (nun - _start).TotalSeconds) { Stab = rutscht ? 2 : warn ? 1 : 0, Rat = rat.Rat, BesterGang = rat.BesterGang };
        Letztes = bild;
        return bild;
    }

    /// <summary>
    /// Die Leistung dieses Autos bei dieser Drehzahl, aus der gelernten Kurve (Vollgas, je 100 U/min,
    /// zwischen zwei bekannten Stufen bis 500 U/min Abstand verbunden) -- null, wo sie unbekannt ist.
    /// </summary>
    internal float? Leistung(int auto, float rpm)
    {
        if (!_leistung.TryGetValue(auto, out var t) || t.Count < 4) { return null; }
        var stufe = rpm / 100f;
        var unten = (int)Math.Floor(stufe);
        int? a = null;
        int? b = null;
        for (var i = unten; i >= unten - 5; i--) { if (t.ContainsKey(i)) { a = i; break; } }
        for (var i = unten + 1; i <= unten + 6; i++) { if (t.ContainsKey(i)) { b = i; break; } }
        if (a is null && b is null) { return null; }
        if (a is null) { return b!.Value - stufe <= 1f ? t[b.Value] : null; }
        if (b is null) { return stufe - a.Value <= 1f ? t[a.Value] : null; }
        var u = (stufe - a.Value) / (b.Value - a.Value);
        return t[a.Value] + (t[b.Value] - t[a.Value]) * Math.Clamp(u, 0f, 1f);
    }

    /// <summary>Was die Leistungskurve zum Schalten sagt.</summary>
    internal readonly record struct Ratschlag(int Rat, int BesterGang, float Hochpunkt);

    /// <summary>
    /// HOCH, RUNTER, BESTER GANG (seit 2026-10-18, Wunsch des Nutzers: "du siehst doch ab welcher rpm
    /// die PS abnehmen ... ein licht fuer jetzt runterschalten oder jetzt hochschalten").
    /// </summary>
    /// <remarks>
    /// * Hochschalten lohnt, sobald der naechste Gang nach dem Schalten MEHR Leistung hat als dieser
    ///   jetzt -- der Schnittpunkt der beiden Leistungskurven ist der beste Schaltpunkt. Er wird auch
    ///   zum roten Bereich und zum Ziel der Schaltlichter.
    /// * Runterschalten lohnt bei Gas, wenn der kleinere Gang mindestens 8 % mehr Leistung bringt und
    ///   dort nicht gleich wieder hochgeschaltet werden muesste.
    /// * Der beste Gang ist der mit der meisten Leistung bei diesem Tempo, ohne in den Begrenzer zu
    ///   laufen.
    /// Nur, wo Leistungskurve und Uebersetzungen gelernt sind; sonst schweigt der Rat.
    /// </remarks>
    internal Ratschlag Beraten(int auto, int gang, float rpm, float speed, float max, float idle, float gas, DateTime nun)
    {
        if (max <= 1000f || gang is < 1 or > 10 || !_uebersetzung.TryGetValue(auto, out var je)) { return new Ratschlag(0, 0, 0f); }
        var hochpunkt = Hochpunkt(auto, gang, max, je);
        var hoch = false;
        var runter = false;
        if (gas > 0.8f && je.TryGetValue(gang, out var jetzt) && jetzt > 0f)
        {
            if (rpm >= max * 0.985f && je.ContainsKey(gang + 1)) { hoch = true; }
            else if (hochpunkt > 0f && rpm >= hochpunkt) { hoch = true; }
            if (je.TryGetValue(gang - 1, out var klein) && klein > 0f)
            {
                var rpmKlein = rpm * klein / jetzt;
                var pJetzt = Leistung(auto, rpm);
                var pKlein = Leistung(auto, rpmKlein);
                var grenze = Hochpunkt(auto, gang - 1, max, je);
                if (pJetzt is { } a && pKlein is { } b && b > a * 1.08f
                    && rpmKlein < (grenze > 0f ? grenze : max * 0.95f) * 0.97f) { runter = true; }
            }
        }
        var besterGang = 0;
        var besteLeistung = 0f;
        if (speed > 3f)
        {
            foreach (var (g, verh) in je)
            {
                var r = speed * verh;
                if (r <= idle + 300f || r >= max * 0.97f) { continue; }
                if (Leistung(auto, r) is { } pw && pw > besteLeistung) { besteLeistung = pw; besterGang = g; }
            }
        }
        var hochAn = _hoch.Setze(hoch, !hoch, nun);
        var runterAn = _runter.Setze(runter, !runter, nun);
        return new Ratschlag(hochAn ? 1 : runterAn ? -1 : 0, besterGang, hochpunkt);
    }

    /// <summary>Wo in diesem Gang hochzuschalten ist: wo der naechste Gang die gleiche Leistung hat.</summary>
    private readonly Dictionary<(int, int), (float Wert, long Zeit)> _hochpunkte = new();

    private float Hochpunkt(int auto, int gang, float max, Dictionary<int, float> je)
    {
        // Gemerkt fuer zwei Sekunden: die Suche laeuft sonst mit jedem Paket, 110 Mal je Sekunde.
        var jetztMs = Environment.TickCount64;
        if (_hochpunkte.TryGetValue((auto, gang), out var g) && jetztMs - g.Zeit < 2000) { return g.Wert; }
        var wert = HochpunktSuchen(auto, gang, max, je);
        _hochpunkte[(auto, gang)] = (wert, jetztMs);
        return wert;
    }

    private float HochpunktSuchen(int auto, int gang, float max, Dictionary<int, float> je)
    {
        if (!je.TryGetValue(gang, out var jetzt) || !je.TryGetValue(gang + 1, out var naechst) || jetzt <= 0f || naechst <= 0f) { return 0f; }
        var faktor = naechst / jetzt;
        for (var r = max * 0.5f; r <= max * 0.99f; r += 50f)
        {
            if (Leistung(auto, r) is { } pJetzt && Leistung(auto, r * faktor) is { } pNaechst && pNaechst >= pJetzt) { return r; }
        }
        // Die Kurven schneiden sich nicht: bis kurz vor den Begrenzer.
        return Leistung(auto, max * 0.97f) is null ? 0f : max * 0.97f;
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

    /// <summary>
    /// Die drei Messwerte: Durchdrehen (groesster Schlupf eines angetriebenen Rades), Blockieren
    /// (groesster negativer Schlupf, als positive Zahl) und Stabilitaet (groesster kombinierter
    /// Schlupf eines Rades, das quer laeuft).
    /// </summary>
    internal static SpeedoMesswerte Messen(ForzaPacket p, int antrieb)
    {
        string[] ecken = { "FrontLeft", "FrontRight", "RearLeft", "RearRight" };
        var spin = 0f;
        var sperre = 0f;
        var stab = 0f;
        for (var i = 0; i < 4; i++)
        {
            var sr = (float)p.Get("TireSlipRatio" + ecken[i]);
            var winkel = Math.Abs((float)p.Get("TireSlipAngle" + ecken[i]));
            var kombi = Math.Abs((float)p.Get("TireCombinedSlip" + ecken[i]));
            if (float.IsNaN(sr) || float.IsNaN(winkel) || float.IsNaN(kombi)) { continue; }
            var getrieben = antrieb switch { 0 => i < 2, 1 => i >= 2, _ => true };
            if (getrieben) { spin = Math.Max(spin, sr); }
            sperre = Math.Max(sperre, -sr);
            if (winkel >= 0.3f) { stab = Math.Max(stab, kombi); }
        }
        return new SpeedoMesswerte(spin, sperre, stab);
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
            if (File.Exists(GangDatei))
            {
                var g = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, float>>>(File.ReadAllText(GangDatei));
                foreach (var (auto, tabelle) in g ?? new())
                {
                    if (!int.TryParse(auto, out var a)) { continue; }
                    _uebersetzung[a] = tabelle.Where(e => int.TryParse(e.Key, out _)).ToDictionary(e => int.Parse(e.Key), e => e.Value);
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
            if (_gaengeGeaendert)
            {
                var raus = _uebersetzung.ToDictionary(e => e.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    e => e.Value.ToDictionary(s => s.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), s => (float)Math.Round(s.Value, 3)));
                File.WriteAllText(GangDatei, JsonSerializer.Serialize(raus));
                _gaengeGeaendert = false;
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
            seitWechsel, t)
        {
            // Beim Anbremsen der Kurve erst die Warnung, dann kurz das Rutschen.
            Stab = zyklus is > 7.3 and < 7.75 ? 1 : zyklus is >= 7.75 and < 8.05 ? 2 : 0,
            // Kurz vor jedem Schalten: hoch; beim Bremsen: runter, und der beste Gang.
            Rat = zyklus < 7.0 && zyklus % 1.4 > 1.15 ? 1 : zyklus >= 7.0 && rpm < 4200f ? -1 : 0,
            BesterGang = zyklus < 7.0 ? gang : Math.Max(1, gang - 1),
        };
    }
}
