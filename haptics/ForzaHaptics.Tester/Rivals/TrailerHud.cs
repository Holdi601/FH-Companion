using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Text.Json;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Die Overlays einer aufgezeichneten Runde als Einzelbilder -- fuer Videos (seit 2026-09-28).
/// </summary>
/// <remarks>
/// ## Wozu
///
/// Die Overlays halten sich aus Bildschirmaufnahmen heraus (WDA_EXCLUDEFROMCAPTURE):
/// die App liest den Schirm selbst und darf ihre eigenen Fenster nicht mitlesen. Ein
/// Video vom Spiel zeigt sie darum nicht. Jede Runde liegt aber mit voller Telemetrie
/// im Archiv (.tele.gz) -- daraus zeichnet dieses Werkzeug dieselben Overlays mit
/// DENSELBEN Zeichenroutinen wie im Spiel, in beliebiger Aufloesung, zum Unterlegen.
///
/// ## Ehrlich wie im Spiel
///
/// Die App zeichnet die Overlays zehnmal je Sekunde (OnTelemetry). Genau so hier: je
/// Zehntelsekunde Rundenzeit ein Bild. Weicher waere moeglich, aber dann zeigte das
/// Video etwas, das die App nicht tut.
///
/// ## Aufruf
///
///     --trailer-hud &lt;runde.json&gt; &lt;ordner&gt; [--ref &lt;runde.json&gt;] [--size 3840x2160]
///                   [--parts delta,inputs,tyres,map] [--from s] [--to s]
///
/// Ohne --ref die schnellste andere eigene Runde auf demselben Kurs, in derselben
/// Klasse, mit demselben Start. Heraus kommen step_00000.png ... (10 je Sekunde, voll
/// durchsichtig bis auf die Overlays) und timing.json (Rundenzeit -> TimestampMS),
/// mit dem sich das Video anlegen laesst.
/// </remarks>
internal static class TrailerHud
{
    public static int Run(string[] args)
    {
        var i = Array.FindIndex(args, a => string.Equals(a, "--trailer-hud", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 2 >= args.Length)
        {
            Console.WriteLine("usage: --trailer-hud <lap.json> <outdir> [--ref <lap.json>] [--size WxH] [--parts delta,inputs,tyres,map]");
            return 2;
        }
        var pfad = Path.GetFullPath(args[i + 1]);
        var ordner = Directory.CreateDirectory(args[i + 2]).FullName;
        string? Wert(string name)
        {
            var k = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return k >= 0 && k + 1 < args.Length ? args[k + 1] : null;
        }
        var groesse = new Size(3840, 2160);
        if (Wert("--size") is { } g && g.Split('x') is { Length: 2 } wh
            && int.TryParse(wh[0], out var w) && int.TryParse(wh[1], out var h))
        {
            groesse = new Size(w, h);
        }
        var teile = (Wert("--parts") ?? "delta,inputs,tyres,map").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var lap = LadeRunde(pfad) ?? throw new InvalidOperationException("lap not readable: " + pfad);
        var tele = LadeTelemetrie(pfad + TelemetryTrack.Suffix)
                   ?? throw new InvalidOperationException("no full telemetry next to the lap (" + TelemetryTrack.Suffix + ")");
        var refPfad = Wert("--ref") ?? SucheReferenz(pfad);
        var referenz = refPfad is null ? null : LadeRunde(refPfad);
        Console.WriteLine($"lap {lap.LapSeconds:0.000} s, {tele.Zeilen.Count} rows; reference: "
                          + (referenz is null ? "none" : $"{referenz.LapSeconds:0.000} s ({Path.GetFileName(refPfad)})"));

        var s = new OverlaySettings
        {
            DeltaHud = true, HudInputs = teile.Contains("inputs"), LiveMap = true, HudTyres = true,
            // Etwas tiefer als ab Werk: mit der Live-Karte zusammen beruehren sie sich sonst.
            HudTyresY = 0.56,
        };
        using var streifen = new DeltaHud(new Rectangle(Point.Empty, groesse), s);
        var karte = (referenz ?? lap).Samples.Select(p => new PointF(p.X, p.Z)).ToList();
        var spur = new List<PointF>();
        var zeiten = new List<(int Schritt, double RundenZeit, double TimestampMs)>();
        var bezeichnung = OverlayController.KlassenName(lap.CarClass) is { Length: > 0 } kl
            ? $"Same car in this PI class · {kl}" : "Same car in this PI class";

        var schritte = (int)Math.Floor(lap.LapSeconds * 10) + 1;
        // Nur ein Stueck der Runde als Bild ausgeben (die Spuren und die Karte werden
        // trotzdem von Anfang an gefuehrt, damit sie aussehen wie im Rennen).
        var von = double.TryParse(Wert("--from"), System.Globalization.NumberStyles.Float,
                                  System.Globalization.CultureInfo.InvariantCulture, out var f0) ? f0 : 0;
        var bis = double.TryParse(Wert("--to"), System.Globalization.NumberStyles.Float,
                                  System.Globalization.CultureInfo.InvariantCulture, out var f1) ? f1 : double.MaxValue;
        using var bild = new Bitmap(groesse.Width, groesse.Height, PixelFormat.Format32bppPArgb);
        for (var k = 0; k < schritte; k++)
        {
            var t = k / 10.0;
            var zeile = tele.Bei(t);
            if (!ForzaPacket.TryParse(tele.AlsPaket(zeile), out var paket)) { continue; }
            var meter = (float)tele.Wert(zeile, "metres");
            float? refSek = referenz?.SecondsAt(meter);
            float? delta = refSek is float r ? (float)(t - r) : null;
            if (teile.Contains("inputs"))
            {
                streifen.PushInputs((float)paket.Get("Accel") / 255f, (float)paket.Get("Brake") / 255f,
                                    (float)paket.Get("Clutch") / 255f, (float)paket.Get("Steer") / 127f,
                                    (float)paket.Get("Gear"), referenz, refSek);
            }
            streifen.Update(delta, bezeichnung, 0f);
            spur.Add(new PointF((float)paket.Get("PositionX"), (float)paket.Get("PositionZ")));
            if (t < von - 1e-6 || t > bis + 1e-6) { continue; }

            using (var gfx = Graphics.FromImage(bild))
            {
                gfx.Clear(Color.Transparent);
                gfx.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                gfx.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                if (teile.Contains("delta") || teile.Contains("inputs")) { streifen.PaintInto(gfx); }
                if (teile.Contains("tyres")) { TyreHud.Male(gfx, s, groesse, TyreHud.AusPaket(paket)); }
                if (teile.Contains("map")) { LiveMapHud.Male(gfx, s, groesse, karte, spur, spur[^1], auchWennAus: true); }
            }
            bild.Save(Path.Combine(ordner, $"step_{k:00000}.png"), ImageFormat.Png);
            zeiten.Add((k, t, tele.Wert(zeile, "TimestampMS")));
        }
        File.WriteAllText(Path.Combine(ordner, "timing.json"), JsonSerializer.Serialize(new
        {
            lap = pfad,
            reference = refPfad,
            lapSeconds = lap.LapSeconds,
            size = new[] { groesse.Width, groesse.Height },
            stepsPerSecond = 10,
            steps = zeiten.Select(z => new[] { z.Schritt, z.RundenZeit, z.TimestampMs }),
        }));
        Console.WriteLine($"{zeiten.Count} frames -> {ordner}");
        return 0;
    }

    /// <summary>
    /// Eine Feier als Bildfolge, 60 je Sekunde, in beliebiger Aufloesung -- dieselbe
    /// Zeichnung wie im Fenster (CelebrationHud.Male).
    /// </summary>
    /// <remarks>
    ///     --trailer-celebration &lt;ordner&gt; &lt;art: rekord|neu|pb|rep&gt; &lt;titel&gt; &lt;zeit&gt;
    ///                           &lt;vorsprung s&gt; &lt;vergleich&gt; &lt;detail&gt; [--size WxH] [--chip TEXT]
    /// </remarks>
    public static int Feier(string[] args)
    {
        var i = Array.FindIndex(args, a => string.Equals(a, "--trailer-celebration", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 7 >= args.Length) { Console.WriteLine("usage: --trailer-celebration <dir> <kind> <title> <time> <gap> <compare> <detail> [--size WxH] [--chip TEXT]"); return 2; }
        var ordner = Directory.CreateDirectory(args[i + 1]).FullName;
        var art = args[i + 2].ToLowerInvariant() switch
        {
            "neu" => CelebrationHud.FeierArt.NeuesAuto,
            "pb" => CelebrationHud.FeierArt.Persoenlich,
            "rep" => CelebrationHud.FeierArt.Repertoire,
            _ => CelebrationHud.FeierArt.Rekord,
        };
        var vorsprung = double.Parse(args[i + 5], System.Globalization.CultureInfo.InvariantCulture);
        var k = Array.FindIndex(args, a => string.Equals(a, "--chip", StringComparison.OrdinalIgnoreCase));
        var chip = k >= 0 && k + 1 < args.Length ? args[k + 1] : null;
        var groesse = new Size(3840, 2160);
        var g0 = Array.FindIndex(args, a => string.Equals(a, "--size", StringComparison.OrdinalIgnoreCase));
        if (g0 >= 0 && g0 + 1 < args.Length && args[g0 + 1].Split('x') is { Length: 2 } wh) { groesse = new Size(int.Parse(wh[0]), int.Parse(wh[1])); }
        var anlass = new CelebrationHud.Anlass(args[i + 3], args[i + 4], vorsprung, args[i + 6], args[i + 7], art, chip);
        var konfetti = new CelebrationHud.Konfetti(20260928, art);
        var bilder = (int)Math.Ceiling(CelebrationHud.DauerVon(art) * 60);
        using var bild = new Bitmap(groesse.Width, groesse.Height, PixelFormat.Format32bppPArgb);
        for (var n = 0; n < bilder; n++)
        {
            using (var g = Graphics.FromImage(bild))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                CelebrationHud.Male(g, groesse, anlass, konfetti, n / 60.0);
            }
            bild.Save(Path.Combine(ordner, $"cel_{n:00000}.png"), ImageFormat.Png);
        }
        Console.WriteLine($"{bilder} frames -> {ordner}");
        return 0;
    }

    /// <summary>
    /// Die Autonotiz als Bild -- dieselbe Zeichnung wie im Automenue (CarNoteHud.Male).
    ///     --trailer-carnote &lt;datei.png&gt; &lt;kopf&gt; &lt;text&gt; [--size WxH]
    /// </summary>
    public static int Notiz(string[] args)
    {
        var i = Array.FindIndex(args, a => string.Equals(a, "--trailer-carnote", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 3 >= args.Length) { Console.WriteLine("usage: --trailer-carnote <out.png> <title> <text> [--size WxH]"); return 2; }
        var groesse = new Size(3840, 2160);
        var g0 = Array.FindIndex(args, a => string.Equals(a, "--size", StringComparison.OrdinalIgnoreCase));
        if (g0 >= 0 && g0 + 1 < args.Length && args[g0 + 1].Split('x') is { Length: 2 } wh) { groesse = new Size(int.Parse(wh[0]), int.Parse(wh[1])); }
        var s = OverlaySettings.Load();
        s.CarNotes = true;
        using var bild = new Bitmap(groesse.Width, groesse.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bild))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            CarNoteHud.Male(g, s, groesse, args[i + 2], args[i + 3].Replace("\n", "
"), auchWennAus: true);
        }
        bild.Save(args[i + 1], ImageFormat.Png);
        Console.WriteLine(args[i + 1]);
        return 0;
    }

    private static RecordedLap? LadeRunde(string pfad)
    {
        using var strom = File.OpenRead(pfad);
        return JsonSerializer.Deserialize<LapArchive.ArchivedLap>(strom)?.Lap;
    }

    /// <summary>Die schnellste andere eigene Runde: gleicher Kurs, Klasse, Start, Art.</summary>
    private static string? SucheReferenz(string pfad)
    {
        var wurzel = LapArchive.Root;
        var diese = OwnTimes.AusDatei(wurzel, pfad);
        if (diese is null) { return null; }
        return OwnTimes.Einlesen(wurzel)
            .Where(l => l.Course == diese.Course && l.Klass == diese.Klass && l.Standing == diese.Standing
                        && l.Sprint == diese.Sprint
                        && !string.Equals(l.Path, diese.Path, StringComparison.OrdinalIgnoreCase))
            .OrderBy(l => l.Seconds).FirstOrDefault()?.Path;
    }

    private sealed class Telemetrie
    {
        public required List<string> Spalten { get; init; }
        public required List<double[]> Zeilen { get; init; }
        private Dictionary<string, int>? _index;

        public double Wert(double[] zeile, string spalte)
        {
            _index ??= Spalten.Select((n, k) => (n, k)).ToDictionary(x => x.n, x => x.k, StringComparer.Ordinal);
            return _index.TryGetValue(spalte, out var k) && k < zeile.Length ? zeile[k] : 0;
        }

        /// <summary>Die Zeile zur Rundenzeit t (Spalte "t"), die letzte davor.</summary>
        public double[] Bei(double t)
        {
            var lo = 0;
            var hi = Zeilen.Count - 1;
            while (lo < hi)
            {
                var m = (lo + hi + 1) / 2;
                if (Wert(Zeilen[m], "t") <= t) { lo = m; } else { hi = m - 1; }
            }
            return Zeilen[lo];
        }

        /// <summary>
        /// Eine Zeile zurueck in ein Paket des Spiels -- damit DIESELBEN Routinen rechnen
        /// wie im Rennen (abgeleitete Werte wie Grip und Blockieren eingeschlossen).
        /// </summary>
        public byte[] AlsPaket(double[] zeile)
        {
            var paket = new byte[324];
            foreach (var d in ForzaPacket.Descriptors)
            {
                if (d.Offset < 0 || !Spalten.Contains(d.Key)) { continue; }
                var v = Wert(zeile, d.Key);
                switch (d.Type)
                {
                    case TelemetryValueType.Float32:
                        BitConverter.GetBytes((float)v).CopyTo(paket, d.Offset); break;
                    case TelemetryValueType.Signed32:
                    case TelemetryValueType.FlagOrFloat32:
                        BitConverter.GetBytes((int)Math.Round(v)).CopyTo(paket, d.Offset); break;
                    case TelemetryValueType.Unsigned32:
                        BitConverter.GetBytes((uint)Math.Max(0, Math.Round(v))).CopyTo(paket, d.Offset); break;
                    case TelemetryValueType.Unsigned16:
                        BitConverter.GetBytes((ushort)Math.Clamp(Math.Round(v), 0, ushort.MaxValue)).CopyTo(paket, d.Offset); break;
                    case TelemetryValueType.Unsigned8:
                        paket[d.Offset] = (byte)Math.Clamp(Math.Round(v), 0, 255); break;
                    case TelemetryValueType.Signed8:
                        paket[d.Offset] = unchecked((byte)(sbyte)Math.Clamp(Math.Round(v), -128, 127)); break;
                }
            }
            return paket;
        }
    }

    private static Telemetrie? LadeTelemetrie(string pfad)
    {
        if (!File.Exists(pfad)) { return null; }
        using var roh = File.OpenRead(pfad);
        using var gz = new GZipStream(roh, CompressionMode.Decompress);
        using var doc = JsonDocument.Parse(gz);
        var spalten = doc.RootElement.GetProperty("columns").EnumerateArray().Select(e => e.GetString() ?? "").ToList();
        var zeilen = doc.RootElement.GetProperty("data").EnumerateArray()
            .Select(z => z.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0).ToArray())
            .ToList();
        return new Telemetrie { Spalten = spalten, Zeilen = zeilen };
    }
}
