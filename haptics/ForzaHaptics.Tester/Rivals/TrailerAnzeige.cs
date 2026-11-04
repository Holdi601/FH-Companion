using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Tacho und G-Meter einer aufgezeichneten Runde als Bildfolge -- fuer Videos (seit 2026-11-04).
/// </summary>
/// <remarks>
/// Dieselbe Rechnung wie im Spiel (<see cref="SpeedoState"/>, <see cref="GMeterState"/>, die Glaettung des
/// <see cref="SpeedoMotor"/>) und dieselbe Zeichnung, nur im Takt des Videos statt des Bildschirms. Jede Vorlage
/// laesst sich so auf jede Runde legen.
///
/// Das G-Meter kennt das Auto vorher: die Runde laeuft zuerst unsichtbar durch, bis es eine Minute gefahren ist
/// -- sonst haette es weder seine Skala noch seine Grenze, und die Farbe an der Grenze bliebe aus.
///
///     --trailer-gauge &lt;runde.json&gt; &lt;ordner&gt; --kind speedo|gmeter --design NAME --px 900 --from s --to s [--fps 60]
///     --trailer-wall  &lt;runde.json&gt; &lt;ordner&gt; --kind speedo|gmeter --from s --to s [--fps 30] [--size WxH] [--cols 8]
///
/// Heraus kommen f_00000.png ... (durchsichtig bis auf die Anzeige) und meta.json.
/// </remarks>
internal static partial class TrailerHud
{
    private static readonly DateTime FahrtBeginn = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Eine Runde, Paket fuer Paket durch die Rechnung der App.</summary>
    private sealed class Messfahrt
    {
        private readonly List<(double T, ForzaPacket P)> _pakete = new();
        private readonly OverlaySettings _s;
        private int _index;
        private double _versatz;

        public Messfahrt(Telemetrie tele, OverlaySettings s)
        {
            _s = s;
            foreach (var zeile in tele.Zeilen)
            {
                if (ForzaPacket.TryParse(tele.AlsPaket(zeile), out var p)) { _pakete.Add((tele.Wert(zeile, "t"), p)); }
            }
        }

        private DateTime Zeit(double t) => FahrtBeginn.AddSeconds(_versatz + t);

        public DateTime Jetzt(double t) => Zeit(t);

        /// <summary>Unsichtbar voraus: das G-Meter lernt das Auto, wie nach einer Minute Fahrt.</summary>
        public void Vorlauf(GMeterState g)
        {
            for (var runde = 0; runde < 40 && g.Fahrzeit < 61f; runde++)
            {
                foreach (var (t, p) in _pakete) { g.Aufnehmen(p, Zeit(t)); }
                _versatz += _pakete.Count > 0 ? _pakete[^1].T + 1.0 : 1.0;
            }
        }

        /// <summary>Alle Pakete bis zur Rundenzeit t.</summary>
        public void Bis(double t, Action<ForzaPacket, DateTime> je)
        {
            while (_index < _pakete.Count && _pakete[_index].T <= t)
            {
                je(_pakete[_index].P, Zeit(_pakete[_index].T));
                _index++;
            }
        }
    }

    /// <summary>Ein Tacho, geglaettet wie im <see cref="SpeedoMotor"/>.</summary>
    private sealed class TachoLauf : IDisposable
    {
        private readonly SpeedoState _daten = new(merken: false);
        private readonly SpeedoRenderer _maler = new();
        private readonly OverlaySettings _s;
        private SpeedoBild? _roh;
        private float _rpm, _tempo;
        public SpeedoDesign Design { get; }

        public TachoLauf(SpeedoDesign d, OverlaySettings s)
        {
            Design = d;
            _s = s;
        }

        public void Paket(ForzaPacket p, DateTime zeit)
        {
            var auto = (int)p.Get("CarOrdinal");
            _roh = _daten.Aufnehmen(p, 0f, Design, SpeedoSchwellen.Fuer(_s, auto), zeit);
        }

        public float Stab => _daten.Messwerte.Stab;

        public void Male(Graphics g, RectangleF ziel, float dt, double uhr)
        {
            if (_roh is not { } r) { return; }
            var tau = Design.Smoothing * Design.Smoothing * 0.15f;
            var k = tau <= 0.001f ? 1f : 1f - MathF.Exp(-dt / tau);
            _rpm += (r.Rpm - _rpm) * k;
            _tempo += (r.SpeedMs - _tempo) * k;
            if (MathF.Abs(r.Rpm - _rpm) < 1f) { _rpm = r.Rpm; }
            _maler.Male(g, Design, r with { Rpm = _rpm, SpeedMs = _tempo, Uhr = uhr }, ziel, SpeedoEinheiten.Aus(_s));
        }

        public void Dispose() => _maler.Dispose();
    }

    /// <summary>Ein G-Meter, geglaettet und gefaerbt wie im <see cref="SpeedoMotor"/>.</summary>
    private sealed class GMeterLauf : IDisposable
    {
        private readonly GMeterRenderer _maler = new();
        private readonly GMeterEinheit _einheit;
        private float _gx, _gz, _gy, _naehe;
        public GMeterState Daten { get; }
        public GMeterDesign Design { get; private set; }

        public GMeterLauf(GMeterDesign d, GMeterState daten, GMeterEinheit einheit)
        {
            Design = d;
            Daten = daten;
            _einheit = einheit;
            var skala = daten.AutoSkala(d.MaxG);
            if (MathF.Abs(skala - d.MaxG) > 0.001f)
            {
                Design = d.Clone();
                Design.MaxG = skala;
            }
        }

        public void Male(Graphics g, RectangleF ziel, float dt, double uhr, DateTime jetzt, float schlupf)
        {
            var tau = Design.Smoothing * Design.Smoothing * 0.2f;
            var k = tau <= 0.001f ? 1f : 1f - MathF.Exp(-dt / tau);
            _gx += (Daten.X - _gx) * k;
            _gz += (Daten.Z - _gz) * k;
            _gy += (Daten.Y - _gy) * k;
            var nah = MathF.Max(schlupf, Daten.Grenznaehe(_gx, _gz));
            _naehe += (nah - _naehe) * (1f - MathF.Exp(-dt / 0.08f));
            var bild = Daten.Bild(_gx, _gz, _gy, Design.TrailSeconds, jetzt, uhr) with { Grenznaehe = _naehe };
            _maler.Male(g, Design, bild, ziel, _einheit);
        }

        public void Dispose() => _maler.Dispose();
    }

    private static (string Pfad, string Ordner, Func<string, string?> Wert) Aufruf(string[] args, string befehl)
    {
        var i = Array.FindIndex(args, a => string.Equals(a, befehl, StringComparison.OrdinalIgnoreCase));
        string? Wert(string name)
        {
            var k = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return k >= 0 && k + 1 < args.Length ? args[k + 1] : null;
        }
        return (Path.GetFullPath(args[i + 1]), Directory.CreateDirectory(args[i + 2]).FullName, Wert);
    }

    private static double Zahl(string? s, double sonst) =>
        double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : sonst;

    public static int Anzeige(string[] args)
    {
        var (pfad, ordner, wert) = Aufruf(args, "--trailer-gauge");
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;
        var tele = LadeTelemetrie(pfad + TelemetryTrack.Suffix) ?? throw new InvalidOperationException("no telemetry next to " + pfad);
        var art = (wert("--kind") ?? "speedo").ToLowerInvariant();
        var px = (float)Zahl(wert("--px"), 900);
        var fps = Zahl(wert("--fps"), 60);
        var von = Zahl(wert("--from"), 0);
        var bis = Zahl(wert("--to"), 10);
        var s = new OverlaySettings();
        var fahrt = new Messfahrt(tele, s);
        var tachoDesign = art == "speedo" ? SpeedoDesigns.Finde(wert("--design")) : SpeedoDesigns.Eingebaut[0];
        using var tacho = new TachoLauf(tachoDesign, s);
        var daten = new GMeterState();
        GMeterLauf? meter = null;
        SizeF groesse;
        if (art == "speedo")
        {
            groesse = SpeedoRenderer.Groesse(tachoDesign, px / tachoDesign.Size);
        }
        else
        {
            fahrt.Vorlauf(daten);
            var einheit = new GMeterEinheit((wert("--unit") ?? "g") switch { "ms2" => GUnit.MetresPerSecond2, "fts2" => GUnit.FeetPerSecond2, _ => GUnit.G });
            meter = new GMeterLauf(GMeterDesigns.Finde(wert("--design")), daten, einheit);
            groesse = GMeterRenderer.Groesse(meter.Design, px / meter.Design.Size);
        }
        // Rand fuers Leuchten und das Zeichen eines Einschlags.
        const int rand = 40;
        var w = (int)Math.Ceiling(groesse.Width) + (2 * rand);
        var h = (int)Math.Ceiling(groesse.Height) + (2 * rand);
        var ziel = new RectangleF(rand, rand, groesse.Width, groesse.Height);
        using var bild = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        var dt = (float)(1.0 / fps);
        // Schon vor dem ersten Bild laufen, damit Nadel und Spur eingeschwungen sind.
        var nummer = 0;
        var n = (int)Math.Round((bis - von) * fps);
        var t0 = Math.Max(0.0, von - 3.0);
        var schritte = (int)Math.Round((von - t0) * fps);
        for (var k = -schritte; k < n; k++)
        {
            var t = von + (k / fps);
            fahrt.Bis(t, (p, zeit) =>
            {
                tacho.Paket(p, zeit);
                daten.Aufnehmen(p, zeit);
            });
            using var g = Graphics.FromImage(bild);
            g.Clear(Color.Transparent);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var schw = SpeedoSchwellen.Fuer(s, daten.Auto);
            if (art == "speedo") { tacho.Male(g, ziel, dt, t); }
            else { meter!.Male(g, ziel, dt, t, fahrt.Jetzt(t), schw.StabRutsch > 0f ? tacho.Stab / schw.StabRutsch : 0f); }
            if (k < 0) { continue; }
            bild.Save(Path.Combine(ordner, $"f_{nummer:00000}.png"), ImageFormat.Png);
            nummer++;
        }
        File.WriteAllText(Path.Combine(ordner, "meta.json"), JsonSerializer.Serialize(new { lap = pfad, fps, from = von, to = bis, size = new[] { w, h }, rand, frames = nummer }));
        meter?.Dispose();
        Console.WriteLine($"{nummer} frames {w}x{h} -> {ordner}");
        return 0;
    }

    public static int Wand(string[] args)
    {
        var (pfad, ordner, wert) = Aufruf(args, "--trailer-wall");
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;
        var tele = LadeTelemetrie(pfad + TelemetryTrack.Suffix) ?? throw new InvalidOperationException("no telemetry next to " + pfad);
        var art = (wert("--kind") ?? "speedo").ToLowerInvariant();
        var fps = Zahl(wert("--fps"), 30);
        var von = Zahl(wert("--from"), 0);
        var bis = Zahl(wert("--to"), 4);
        var spalten = (int)Zahl(wert("--cols"), 8);
        var groesse = new Size(3840, 2160);
        if (wert("--size") is { } gs && gs.Split('x') is { Length: 2 } wh && int.TryParse(wh[0], out var bw) && int.TryParse(wh[1], out var bh)) { groesse = new Size(bw, bh); }
        var s = new OverlaySettings();
        var fahrt = new Messfahrt(tele, s);
        var daten = new GMeterState();
        var tachos = new List<TachoLauf>();
        var meter = new List<GMeterLauf>();
        using var schlupfTacho = new TachoLauf(SpeedoDesigns.Eingebaut[0], s);
        if (art == "speedo")
        {
            tachos.AddRange(SpeedoDesigns.Eingebaut.Select(d => new TachoLauf(d, s)));
        }
        else
        {
            fahrt.Vorlauf(daten);
            meter.AddRange(GMeterDesigns.Eingebaut.Select(d => new GMeterLauf(d, daten, new GMeterEinheit(GUnit.G))));
        }
        var anzahl = Math.Max(tachos.Count, meter.Count);
        var zeilen = (int)Math.Ceiling(anzahl / (double)spalten);
        var zw = groesse.Width / (float)spalten;
        var zh = groesse.Height / (float)zeilen;
        using var bild = new Bitmap(groesse.Width, groesse.Height, PixelFormat.Format32bppPArgb);
        var dt = (float)(1.0 / fps);
        var n = (int)Math.Round((bis - von) * fps);
        var t0 = Math.Max(0.0, von - 3.0);
        var schritte = (int)Math.Round((von - t0) * fps);
        var nummer = 0;
        for (var k = -schritte; k < n; k++)
        {
            var t = von + (k / fps);
            fahrt.Bis(t, (p, zeit) =>
            {
                foreach (var a in tachos) { a.Paket(p, zeit); }
                schlupfTacho.Paket(p, zeit);
                daten.Aufnehmen(p, zeit);
            });
            using var g = Graphics.FromImage(bild);
            g.Clear(Color.Transparent);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var schw = SpeedoSchwellen.Fuer(s, daten.Auto);
            for (var i = 0; i < anzahl; i++)
            {
                var feld = new RectangleF((i % spalten) * zw + 8f, (i / spalten) * zh + 8f, zw - 16f, zh - 16f);
                if (art == "speedo")
                {
                    var d = tachos[i].Design;
                    var groe = SpeedoRenderer.Groesse(d, Math.Min(feld.Width / d.Size, feld.Height / (d.Size * d.Hochkant)));
                    tachos[i].Male(g, new RectangleF(feld.X + (feld.Width - groe.Width) / 2f, feld.Y + (feld.Height - groe.Height) / 2f, groe.Width, groe.Height), dt, t);
                }
                else
                {
                    var d = meter[i].Design;
                    var groe = GMeterRenderer.Groesse(d, Math.Min(feld.Width / d.Size, feld.Height / (d.Size * d.Hochkant)));
                    meter[i].Male(g, new RectangleF(feld.X + (feld.Width - groe.Width) / 2f, feld.Y + (feld.Height - groe.Height) / 2f, groe.Width, groe.Height), dt, t,
                                  fahrt.Jetzt(t), schw.StabRutsch > 0f ? schlupfTacho.Stab / schw.StabRutsch : 0f);
                }
            }
            if (k < 0) { continue; }
            bild.Save(Path.Combine(ordner, $"f_{nummer:00000}.png"), ImageFormat.Png);
            nummer++;
        }
        File.WriteAllText(Path.Combine(ordner, "meta.json"), JsonSerializer.Serialize(new { lap = pfad, fps, from = von, to = bis, size = new[] { groesse.Width, groesse.Height }, anzahl, frames = nummer }));
        foreach (var a in tachos) { a.Dispose(); }
        foreach (var m in meter) { m.Dispose(); }
        Console.WriteLine($"{nummer} frames, {anzahl} designs -> {ordner}");
        return 0;
    }
}
