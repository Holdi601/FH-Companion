namespace ForzaHaptics.Tester.Rivals;

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
    double Uhr)
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

    public float X { get; private set; }
    public float Z { get; private set; }
    public float Y { get; private set; }

    /// <summary>Spitzen, Huelle und Waermekarte auf null -- neues Auto oder Knopf im Reiter.</summary>
    public void Zuruecksetzen()
    {
        Array.Clear(_huelle);
        Array.Clear(_waerme);
        _rechts = _links = _vor = _brems = _hoch = _runter = 0f;
        _spur.Clear();
    }

    public void Aufnehmen(ForzaPacket p, DateTime jetzt)
    {
        var auto = (int)p.Get("CarOrdinal");
        if (auto != _auto) { _auto = auto; Zuruecksetzen(); }
        var x = (float)p.Get("AccelerationX") / Erde;
        var z = (float)p.Get("AccelerationZ") / Erde;
        var y = (float)p.Get("AccelerationY") / Erde;
        if (!float.IsFinite(x) || !float.IsFinite(z) || !float.IsFinite(y)) { return; }
        // Ein Stoss beim Aufprall ist keine Fahrdynamik: ueber 8 G ist es ein Unfall.
        if (MathF.Abs(x) > 8f || MathF.Abs(z) > 8f || MathF.Abs(y) > 8f) { return; }
        X = x;
        Z = z;
        Y = y;
        var dt = _zuletzt == DateTime.MinValue ? 0f : (float)Math.Clamp((jetzt - _zuletzt).TotalSeconds, 0, 0.25);
        _zuletzt = jetzt;
        if (p.Get("IsRaceOn") < 0.5 || p.Get("Speed") < 1.0) { return; }

        _rechts = MathF.Max(_rechts, x);
        _links = MathF.Max(_links, -x);
        _vor = MathF.Max(_vor, z);
        _brems = MathF.Max(_brems, -z);
        _hoch = MathF.Max(_hoch, y);
        _runter = MathF.Max(_runter, -y);
        var betrag = MathF.Sqrt(x * x + z * z);
        if (betrag > 0.05f)
        {
            var sektor = Sektor(x, z);
            _huelle[sektor] = MathF.Max(_huelle[sektor], betrag);
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
            _rechts, _links, _vor, _brems, _hoch, _runter, (float[])_waerme.Clone(), uhr);
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
