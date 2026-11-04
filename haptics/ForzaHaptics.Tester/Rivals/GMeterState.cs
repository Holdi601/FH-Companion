namespace ForzaHaptics.Tester.Rivals;

/// <summary>Was das G-Meter ueber das gefahrene Auto weiss -- fuer den Reiter.</summary>
internal readonly record struct GMeterStand(
    int Auto, float Rechts, float Links, float Vor, float Brems, float Skala, float Fahrzeit,
    AufprallEreignis? Letzter, AufprallEreignis? Rekord, int Aufpralle);

/// <summary>Ein Punkt der Spur: G seitwaerts (rechts +), laengs (vorwaerts +), senkrecht, und wie alt (s).</summary>
internal readonly record struct GPunkt(float X, float Z, float Y, float Alter);

/// <summary>
/// Was ein G-Meter in einem Bild zeigt -- eine unveraenderliche Momentaufnahme (gezeichnet wird auf dem
/// Faden des Tachos UND, fuer Aufnahme- und Dashboardfenster, auf dem der Oberflaeche).
/// </summary>
/// <remarks>
/// Alles in G im Bezugssystem des AUTOS, wie Forza es schickt: X nach rechts, Z nach vorn, Y nach oben
/// (gemessen 2026-10-24: Rechtskurve AccelerationX +15 m/s², Bremsen AccelerationZ -18 m/s², Y ohne
/// Schwerkraft). Ob die Kraft gezeigt wird, die man spuert, entscheidet die Zeichnung.
/// </remarks>
internal sealed record GMeterBild(
    float X, float Z, float Y,
    GPunkt[] Spur,
    float[] Huelle,
    float Rechts, float Links, float Vor, float Brems, float Hoch, float Runter,
    float[] Waerme,
    double Uhr,
    AufprallEreignis? Aufprall = null,
    float AufprallAlter = 99f,
    float AufprallRekord = 0f,
    float Grenznaehe = 0f)
{
    public float Gesamt => MathF.Sqrt(X * X + Z * Z);

    /// <summary>Sektoren der Huelle (je 10 Grad) und Raster der Waermekarte (je 0,25 G, +-3 G).</summary>
    public const int Sektoren = 36;
    public const int Raster = 25;
    public const float RasterG = 3f;
}

/// <summary>
/// Die Daten des G-Meters aus der Telemetrie (seit 2026-10-24): jedes Paket in die Spur, die Huelle
/// (das Groesste je Richtung), die Spitzen und die Waermekarte (wo das Auto wie lange war).
/// </summary>
internal sealed class GMeterState
{
    private const float Erde = 9.80665f;
    private readonly List<(DateTime Zeit, float X, float Z, float Y)> _spur = new();
    private readonly float[] _huelle = new float[GMeterBild.Sektoren];
    private readonly float[] _waerme = new float[GMeterBild.Raster * GMeterBild.Raster];
    private float _rechts, _links, _vor, _brems, _hoch, _runter;
    private DateTime _zuletzt = DateTime.MinValue;
    private int _auto;
    // JE AUTO (seit 2026-11-01): Spitzen, Huelle, Fahrzeit und Einschlaege bleiben beim Auto -- auch
    // ueber einen Neustart der App. null: nichts merken (Vorschau, Pruefungen).
    private readonly GMeterRekorde? _rekorde;
    private float _fahrzeit;
    private AufprallEreignis? _aufprallRekord;
    private int _aufpralle;
    private AufprallEreignis? _letzter;
    private DateTime _gemerktAm = DateTime.MinValue;
    // Die Spitzen aus dem Mittel der letzten 100 ms: ein Bordstein schlaegt fuer ein Paket auf 7 G aus,
    // das ist keine Grenze des Autos (gemessen 2026-11-01, roh in 1 % der Pakete ueber 4 G).
    private readonly Queue<(DateTime Zeit, float X, float Z, float Y)> _glatt = new();
    private float _gsx, _gsz, _gsy;
    // GEHALTEN, NICHT GESTREIFT (seit 2026-11-04): ein Rekord zaehlt erst, wenn das Auto ihn 250 ms haelt.
    // Gemessen an 18 Runden: ein Kontakt trieb den Supra (echte Grenze 2 G) im 100-ms-Mittel auf 6,7 G, und
    // ein Rempler verdarb Skala und Warnfarbe des Autos fuer immer. Gehalten liegt er in jeder Runde bei
    // 1,83-1,97 G; Autos mit Abtrieb behalten ihre 5,5-6,6 G.
    private readonly Queue<(DateTime Zeit, float X, float Z, float Y)> _gehalten = new();

    public GMeterState(GMeterRekorde? rekorde = null)
    {
        _rekorde = rekorde;
    }

    public AufprallMelder Melder { get; } = new();

    public float X { get; private set; }
    public float Z { get; private set; }
    public float Y { get; private set; }

    /// <summary>Das Auto, dessen Werte gerade gelten (CarOrdinal; 0: noch keins).</summary>
    public int Auto => _auto;

    /// <summary>Wie lange dieses Auto schon gefahren ist (Sekunden, ueber alle Sitzungen).</summary>
    public float Fahrzeit => _fahrzeit;

    /// <summary>Was dieses Auto gehalten hat: die groesste Spitze seitwaerts, beim Bremsen oder Beschleunigen.</summary>
    public float Grenze
    {
        get
        {
            var groesste = 0f;
            foreach (var h in _huelle) { groesste = MathF.Max(groesste, h); }
            return groesste;
        }
    }

    public float Rechts => _rechts;
    public float Links => _links;
    public float Vor => _vor;
    public float Brems => _brems;

    public AufprallEreignis? LetzterAufprall => _letzter;
    public AufprallEreignis? AufprallRekord => _aufprallRekord;
    public int Aufpralle => _aufpralle;

    /// <summary>
    /// Wie nah diese Beschleunigung an dem ist, was das Auto in DIESE Richtung schon gehalten hat (1 = so
    /// viel wie sein Rekord). 0, solange es keine Minute gefahren ist: vorher ist jede Spitze ein Rekord.
    /// </summary>
    public float Grenznaehe(float x, float z)
    {
        if (_fahrzeit < 60f) { return 0f; }
        var betrag = MathF.Sqrt(x * x + z * z);
        if (betrag < 0.2f) { return 0f; }
        var s = Sektor(x, z);
        var n = GMeterBild.Sektoren;
        var grenze = MathF.Max(_huelle[s], MathF.Max(_huelle[(s + 1) % n], _huelle[(s + n - 1) % n]));
        return grenze < 0.3f ? 0f : betrag / grenze;
    }

    /// <summary>
    /// Die Skala fuer dieses Auto: etwas ueber dem, was es gehalten hat, auf runde Werte. Bevor es 20
    /// Sekunden gefahren ist, die des Designs.
    /// </summary>
    public float AutoSkala(float sonst) => _fahrzeit < 20f || Grenze < 0.3f ? sonst : Runde(Grenze * 1.1f);

    internal static float Runde(float g)
    {
        var schritt = g <= 2f ? 0.25f : g <= 4f ? 0.5f : 1f;
        // Bis 8 G: darueber nennt auch die Anzeige es einen Unfall, und Abtrieb haelt bis 6,6 G.
        return Math.Clamp(MathF.Ceiling(g / schritt - 0.001f) * schritt, 0.5f, 8f);
    }

    /// <summary>Spitzen, Huelle, Waermekarte und Einschlaege DIESES Autos auf null -- Knopf im Reiter.</summary>
    public void Zuruecksetzen()
    {
        Array.Clear(_huelle);
        Array.Clear(_waerme);
        _rechts = _links = _vor = _brems = _hoch = _runter = 0f;
        _fahrzeit = 0f;
        _aufprallRekord = null;
        _letzter = null;
        _aufpralle = 0;
        _spur.Clear();
        _gehalten.Clear();
        if (_auto != 0 && _rekorde is not null)
        {
            _rekorde.Vergessen(_auto);
            _rekorde.Speichern();
        }
    }

    /// <summary>Was dieses Auto bisher hat, in den Speicher (und auf die Platte, wenn faellig).</summary>
    public void Merken(bool sofort = false)
    {
        if (_rekorde is null || _auto == 0) { return; }
        _rekorde.Setze(_auto, new GMeterRekord
        {
            Huelle = (float[])_huelle.Clone(), Rechts = _rechts, Links = _links, Vor = _vor, Brems = _brems,
            Hoch = _hoch, Runter = _runter, Fahrzeit = _fahrzeit, Aufpralle = _aufpralle, Fassung = GMeterRekord.AktuelleFassung,
            AufprallG = _aufprallRekord?.G ?? 0f, AufprallWinkel = _aufprallRekord?.Winkel ?? 0f,
            AufprallKmh = _aufprallRekord?.Kmh ?? 0f, AufprallAm = _aufprallRekord?.Zeit,
        });
        _gemerktAm = DateTime.UtcNow;
        if (sofort) { _rekorde.Speichern(); }
    }

    private void Wechseln(int auto)
    {
        Merken(sofort: true);
        Array.Clear(_waerme);
        _spur.Clear();
        _glatt.Clear();
        _gehalten.Clear();
        _gsx = _gsz = _gsy = 0f;
        Melder.Vergessen();
        _letzter = null;
        _auto = auto;
        var r = _rekorde?.Fuer(auto);
        Array.Clear(_huelle);
        // Alte Rekorde (vor dem Halten) koennen von einem Rempler stammen: Spitzen neu lernen, Einschlaege bleiben.
        var taugt = r is { Fassung: >= GMeterRekord.AktuelleFassung };
        if (taugt && r?.Huelle is { Length: GMeterBild.Sektoren } h) { Array.Copy(h, _huelle, h.Length); }
        _rechts = taugt ? r!.Rechts : 0f;
        _links = taugt ? r!.Links : 0f;
        _vor = taugt ? r!.Vor : 0f;
        _brems = taugt ? r!.Brems : 0f;
        _hoch = taugt ? r!.Hoch : 0f;
        _runter = taugt ? r!.Runter : 0f;
        _fahrzeit = taugt ? r!.Fahrzeit : 0f;
        _aufpralle = r?.Aufpralle ?? 0;
        _aufprallRekord = r is { AufprallG: > 0f }
            ? new AufprallEreignis(r.AufprallG, r.AufprallWinkel, r.AufprallKmh, r.AufprallAm ?? DateTime.MinValue)
            : null;
    }

    /// <summary>Ein Paket. Gibt einen Einschlag zurueck, wenn eben einer fertig erkannt ist.</summary>
    public AufprallEreignis? Aufnehmen(ForzaPacket p, DateTime jetzt)
    {
        var auto = (int)p.Get("CarOrdinal");
        if (auto != _auto) { Wechseln(auto); }
        var x = (float)p.Get("AccelerationX") / Erde;
        var z = (float)p.Get("AccelerationZ") / Erde;
        var y = (float)p.Get("AccelerationY") / Erde;
        if (!float.IsFinite(x) || !float.IsFinite(z) || !float.IsFinite(y)) { return null; }
        var faehrt = p.Get("IsRaceOn") >= 0.5 && p.Get("Speed") >= 1.0;
        // EINSCHLAEGE ZUERST, aus den rohen Werten: gerade die harten liegen ueber der Grenze unten.
        var aufprall = Melder.Aufnehmen(x, z, (float)(p.Get("Speed") * 3.6), faehrt, jetzt);
        if (aufprall is { } a)
        {
            _letzter = a;
            _aufpralle++;
            if (_aufprallRekord is not { } r || a.G > r.G) { _aufprallRekord = a; }
        }
        // Ein Stoss beim Aufprall ist keine Fahrdynamik: ueber 8 G ist es ein Unfall.
        if (MathF.Abs(x) > 8f || MathF.Abs(z) > 8f || MathF.Abs(y) > 8f) { return aufprall; }
        X = x;
        Z = z;
        Y = y;
        var dt = _zuletzt == DateTime.MinValue ? 0f : (float)Math.Clamp((jetzt - _zuletzt).TotalSeconds, 0, 0.25);
        _zuletzt = jetzt;
        if (!faehrt) { return aufprall; }
        _fahrzeit += dt;

        _glatt.Enqueue((jetzt, x, z, y));
        _gsx += x;
        _gsz += z;
        _gsy += y;
        while (_glatt.Count > 0 && (jetzt - _glatt.Peek().Zeit).TotalSeconds > 0.1)
        {
            var alt = _glatt.Dequeue();
            _gsx -= alt.X;
            _gsz -= alt.Z;
            _gsy -= alt.Y;
        }
        // Waehrend und kurz nach einem Einschlag zaehlt nichts als Grenze des Autos.
        if (Melder.Unruhig(jetzt))
        {
            _gehalten.Clear();
        }
        else if (_glatt.Count >= 3)
        {
            var gx = _gsx / _glatt.Count;
            var gz = _gsz / _glatt.Count;
            var gy = _gsy / _glatt.Count;
            _gehalten.Enqueue((jetzt, gx, gz, gy));
            while ((jetzt - _gehalten.Peek().Zeit).TotalSeconds > 0.25) { _gehalten.Dequeue(); }
            // Erst wenn das Fenster voll ist -- und dann das Kleinste darin: das hat das Auto wirklich gehalten.
            if ((jetzt - _gehalten.Peek().Zeit).TotalSeconds >= 0.2)
            {
                float rechts = float.MaxValue, links = float.MaxValue, vor = float.MaxValue, brems = float.MaxValue;
                float hoch = float.MaxValue, runter = float.MaxValue, betrag = float.MaxValue;
                foreach (var w in _gehalten)
                {
                    rechts = MathF.Min(rechts, w.X);
                    links = MathF.Min(links, -w.X);
                    vor = MathF.Min(vor, w.Z);
                    brems = MathF.Min(brems, -w.Z);
                    hoch = MathF.Min(hoch, w.Y);
                    runter = MathF.Min(runter, -w.Y);
                    betrag = MathF.Min(betrag, MathF.Sqrt(w.X * w.X + w.Z * w.Z));
                }
                _rechts = MathF.Max(_rechts, rechts);
                _links = MathF.Max(_links, links);
                _vor = MathF.Max(_vor, vor);
                _brems = MathF.Max(_brems, brems);
                _hoch = MathF.Max(_hoch, hoch);
                _runter = MathF.Max(_runter, runter);
                if (betrag > 0.05f)
                {
                    var sektor = Sektor(gx, gz);
                    _huelle[sektor] = MathF.Max(_huelle[sektor], betrag);
                }
            }
        }
        // Wo das Auto wie lange war -- langsam verblassend (Halbwertszeit rund eine Minute).
        if (dt > 0f)
        {
            var verblassen = MathF.Exp(-dt / 90f);
            for (var i = 0; i < _waerme.Length; i++) { _waerme[i] *= verblassen; }
            var (zx, zz) = Zelle(x, z);
            _waerme[zz * GMeterBild.Raster + zx] += dt;
        }
        // Die Spur: hoechstens 60 Punkte je Sekunde, hoechstens zehn Sekunden.
        if (_spur.Count == 0 || (jetzt - _spur[^1].Zeit).TotalSeconds >= 1.0 / 60.0)
        {
            _spur.Add((jetzt, x, z, y));
        }
        while (_spur.Count > 0 && (jetzt - _spur[0].Zeit).TotalSeconds > 10.0) { _spur.RemoveAt(0); }
        // Alle 30 Sekunden auf die Platte, und bei jedem Wechsel des Autos (Wechseln).
        if (DateTime.UtcNow - _gemerktAm > TimeSpan.FromSeconds(30)) { Merken(sofort: true); }
        return aufprall;
    }

    internal static int Sektor(float x, float z)
    {
        var winkel = MathF.Atan2(z, x);
        var u = (winkel + MathF.PI) / (2f * MathF.PI);
        return Math.Clamp((int)(u * GMeterBild.Sektoren), 0, GMeterBild.Sektoren - 1);
    }

    internal static (int X, int Z) Zelle(float x, float z)
    {
        var n = GMeterBild.Raster;
        int I(float g) => Math.Clamp((int)MathF.Round((g + GMeterBild.RasterG) / (2f * GMeterBild.RasterG) * (n - 1)), 0, n - 1);
        return (I(x), I(z));
    }

    /// <summary>Ein Bild mit diesen (geglaetteten) Werten und der Spur bis <paramref name="sekunden"/>.</summary>
    public GMeterBild Bild(float x, float z, float y, float sekunden, DateTime jetzt, double uhr)
    {
        var spur = new List<GPunkt>(_spur.Count);
        foreach (var p in _spur)
        {
            var alter = (float)(jetzt - p.Zeit).TotalSeconds;
            if (alter <= sekunden) { spur.Add(new GPunkt(p.X, p.Z, p.Y, alter)); }
        }
        return new GMeterBild(x, z, y, spur.ToArray(), (float[])_huelle.Clone(),
            _rechts, _links, _vor, _brems, _hoch, _runter, (float[])_waerme.Clone(), uhr,
            _letzter, _letzter is { } l ? (float)(jetzt - l.Zeit).TotalSeconds : 99f, _aufprallRekord?.G ?? 0f);
    }

    /// <summary>
    /// Ein Beispiel, das sich bewegt -- Vorschau und Galerie: eine Acht aus Kurven, Bremsen und Gas,
    /// mit Spur, Huelle und Waermekarte aus den Sekunden davor.
    /// </summary>
    public static GMeterBild Vorfuehrung(double t, bool ruhig = false)
    {
        if (ruhig) { t = 3.1; }
        static (float X, float Z, float Y) Bei(double s) =>
            ((float)(1.35 * Math.Sin(s * 0.9)), (float)(0.95 * Math.Sin(s * 1.8) - 0.15), (float)(0.25 * Math.Sin(s * 5.3)));
        var zustand = new GMeterState();
        var basis = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        // Die Waermekarte und die Huelle aus 20 Sekunden davor, ohne Paket: direkt.
        for (var s = t - 20.0; s <= t; s += 1.0 / 60.0)
        {
            var (x, z, y) = Bei(s);
            var jetzt = basis.AddSeconds(s + 100.0);
            zustand.X = x; zustand.Z = z; zustand.Y = y;
            zustand._rechts = MathF.Max(zustand._rechts, x);
            zustand._links = MathF.Max(zustand._links, -x);
            zustand._vor = MathF.Max(zustand._vor, z);
            zustand._brems = MathF.Max(zustand._brems, -z);
            zustand._hoch = MathF.Max(zustand._hoch, y);
            zustand._runter = MathF.Max(zustand._runter, -y);
            var sek = Sektor(x, z);
            zustand._huelle[sek] = MathF.Max(zustand._huelle[sek], MathF.Sqrt(x * x + z * z));
            var (zx, zz) = Zelle(x, z);
            zustand._waerme[zz * GMeterBild.Raster + zx] += 1f / 60f;
            zustand._spur.Add((jetzt, x, z, y));
        }
        var ende = basis.AddSeconds(t + 100.0);
        while (zustand._spur.Count > 0 && (ende - zustand._spur[0].Zeit).TotalSeconds > 10.0) { zustand._spur.RemoveAt(0); }
        var (ex, ez, ey) = Bei(t);
        return zustand.Bild(ex, ez, ey, 10f, ende, t);
    }
}
