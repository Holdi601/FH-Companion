using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>Ein Fahrer auf dem Ergebnisschirm -- ohne Namen.</summary>
/// <remarks>
/// Der Gamertag wird nicht gelesen und nicht gespeichert (Entscheidung des Nutzers,
/// 2026-10-01): fuer Platzierung und Zeiten zaehlt das Auto, nicht wer es fuhr.
/// </remarks>
internal sealed class FieldEntry
{
    [JsonPropertyName("place")] public int Place { get; set; }

    /// <summary>"human" (Stufenabzeichen), "ai" (ohne) oder "left" (hat das Rennen verlassen).</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "ai";

    [JsonPropertyName("self")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Self { get; set; }

    /// <summary>Die car_id, wenn der Kurzname eindeutig ein Auto mit Kennung meint.</summary>
    [JsonPropertyName("car")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Car { get; set; }

    /// <summary>Was in der Spalte "Car" stand, so wie es gelesen wurde.</summary>
    [JsonPropertyName("carShort")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CarShort { get; set; }

    [JsonPropertyName("carName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CarName { get; set; }

    [JsonPropertyName("pi")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Pi { get; set; }

    /// <summary>
    /// Die schnellste Runde dieses Fahrers -- nur auf Rundkursen: dort steht statt "Progress"
    /// die Spalte "Best Lap" (gesehen 2026-10-01, Daikoku Circuit). Direkt vergleichbar mit
    /// einer Rivals-Runde, anders als die Rennzeit.
    /// </summary>
    [JsonPropertyName("bestLapMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? BestLapMs { get; set; }

    /// <summary>Fortschritt in Prozent; null bei "-" (verlassen).</summary>
    [JsonPropertyName("progress")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Progress { get; set; }

    /// <summary>Die Zeit im Ziel in Millisekunden; null, wer nicht ankam.</summary>
    [JsonPropertyName("ms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Ms { get; set; }
}

/// <summary>
/// Die Kurznamen der Autos auf car_ids (config/fh6_car_short_names.json, aus den
/// Stringtabellen des Spiels gebaut von scripts/build_car_short_names.py).
/// </summary>
internal static class CarShortNames
{
    internal sealed record Auto(int? Id, string Name);

    private static readonly object Schloss = new();
    private static Dictionary<string, Auto>? _namen;

    /// <summary>Fuer Tests: eine andere Datei.</summary>
    internal static string? Pfad { get; set; }

    internal static void Vergessen()
    {
        lock (Schloss) { _namen = null; }
    }

    private static Dictionary<string, Auto> Laden()
    {
        lock (Schloss)
        {
            if (_namen is not null) { return _namen; }
            var raus = new Dictionary<string, Auto>(StringComparer.Ordinal);
            foreach (var pfad in Pfad is { } p ? new[] { p } : Kandidaten())
            {
                if (!File.Exists(pfad)) { continue; }
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(pfad));
                    var wurzel = doc.RootElement;
                    var ids = new Dictionary<int, string>();
                    if (wurzel.TryGetProperty("cars", out var autos))
                    {
                        foreach (var e in autos.EnumerateObject())
                        {
                            int? id = e.Value.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : null;
                            var name = e.Value.TryGetProperty("name", out var n) ? n.GetString() ?? e.Name : e.Name;
                            var jahr = e.Value.TryGetProperty("year", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetInt32() : 0;
                            var voll = jahr > 0 ? $"{name} '{jahr % 100:00}" : name;
                            raus[Schluessel(e.Name)] = new Auto(id, voll);
                            if (id is { } k) { ids[k] = voll; }
                        }
                    }
                    if (wurzel.TryGetProperty("otherLanguages", out var andere))
                    {
                        foreach (var e in andere.EnumerateObject())
                        {
                            if (e.Value.ValueKind == JsonValueKind.Number && ids.TryGetValue(e.Value.GetInt32(), out var voll))
                            {
                                raus.TryAdd(Schluessel(e.Name), new Auto(e.Value.GetInt32(), voll));
                            }
                        }
                    }
                    break;
                }
                catch (Exception)
                {
                    // Eine kaputte Datei: ohne Namen weiter.
                }
            }
            return _namen = raus;
        }
    }

    private static IEnumerable<string> Kandidaten()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            yield return Path.Combine(dir.FullName, "config", "fh6_car_short_names.json");
            dir = dir.Parent;
        }
    }

    /// <summary>Gefaltet: Hochkommas vereinheitlicht, Leerraum zusammengezogen, klein.</summary>
    internal static string Schluessel(string text)
    {
        var t = text.Replace('’', '\'').Replace('‘', '\'').Replace('`', '\'').Replace('´', '\'');
        return Regex.Replace(t, @"\s+", " ").Trim().ToLowerInvariant();
    }

    /// <summary>Fuer den Vergleich: was die Texterkennung verwechselt, gleich gemacht ("Sl" und "S1").</summary>
    private static string Falte(string s) =>
        s.Replace('l', '1').Replace('|', '1').Replace('i', '1').Replace('o', '0');   // "Evo Ill" ist "Evo III"

    // Das Hochkomma vor dem Jahrgang, als Ziffer oder Buchstabe gelesen: "Honda Beat 191", "Focus r09".
    private static readonly Regex Jahrgang = new(@"\s[1rl|](\d\d)$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Das Auto zu einem gelesenen Kurznamen: genau, sonst der eine naechste mit hoechstens
    /// zwei Zeichen Abstand, sonst der eine, der auf das Gelesene ENDET (ein verlorenes "#2"
    /// vorn: "Audi S1" fuer "#2 Audi S1"). Nie geraten: zwei gleich gute heissen keins.
    /// </summary>
    public static Auto? Finde(string? gelesen)
    {
        if (string.IsNullOrWhiteSpace(gelesen)) { return null; }
        var namen = Laden();
        var s = Schluessel(gelesen);
        if (namen.TryGetValue(s, out var genau)) { return genau; }
        // Das verlesene Hochkomma vor dem Jahrgang gilt auch fuer die naeheren Vergleiche unten
        // ("Escort 177": Hochkomma als 1 UND das "#5" vorn verloren).
        if (Jahrgang.IsMatch(s))
        {
            s = Jahrgang.Replace(s, " '$1");
            if (namen.TryGetValue(s, out var mitJahr)) { return mitJahr; }
        }
        var f = Falte(s);
        var grenze = s.Length >= 8 ? 2 : 1;
        Auto? bester = null;
        var besterAbstand = int.MaxValue;
        var gleich = false;
        foreach (var (k, a) in namen)
        {
            if (Math.Abs(k.Length - s.Length) > grenze) { continue; }
            var d = Abstand(Falte(k), f, grenze);
            if (d < besterAbstand) { besterAbstand = d; bester = a; gleich = false; }
            else if (d == besterAbstand && bester is not null && a.Id != bester.Id) { gleich = true; }
        }
        if (besterAbstand <= grenze && !gleich) { return bester; }
        if (f.Length >= 6)
        {
            var enden = namen.Where(kv => Falte(kv.Key).EndsWith(" " + f, StringComparison.Ordinal))
                             .Select(kv => kv.Value).DistinctBy(a => (a.Id, a.Name)).ToList();
            if (enden.Count == 1) { return enden[0]; }
        }
        return null;
    }

    /// <summary>Levenshtein, mit Abbruch ueber der Grenze.</summary>
    internal static int Abstand(string a, string b, int grenze)
    {
        var vorher = new int[b.Length + 1];
        var jetzt = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) { vorher[j] = j; }
        for (var i = 1; i <= a.Length; i++)
        {
            jetzt[0] = i;
            var min = jetzt[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var kosten = a[i - 1] == b[j - 1] ? 0 : 1;
                jetzt[j] = Math.Min(Math.Min(jetzt[j - 1] + 1, vorher[j] + 1), vorher[j - 1] + kosten);
                min = Math.Min(min, jetzt[j]);
            }
            if (min > grenze) { return grenze + 1; }
            (vorher, jetzt) = (jetzt, vorher);
        }
        return vorher[b.Length];
    }
}

/// <summary>
/// Der Ergebnisschirm in Zeilen: Auto, PI, Fortschritt, Zeit je Fahrer (seit 2026-10-01).
/// </summary>
/// <remarks>
/// ## Wozu
///
/// Fuer die erwartete Platzierung (das eigene Auto gegen die anderen auf der Bestenliste)
/// und fuer Horizon-Play-Zeiten je Auto. RaceGrid liest nur, WIE VIELE und WER Mensch ist;
/// hier kommt hinzu, WAS jeder fuhr und WIE SCHNELL.
///
/// ## Wie
///
/// Am 1080p-Bild, Spalte fuer Spalte: jede Spalte wird als Streifen ueber alle Zeilen
/// ausgeschnitten, als dunkle Schrift auf Weiss neu gezeichnet (die eigene Zeile ist schwarz
/// mit weisser Schrift, das PI-Feld weiss auf dunkel) und einmal gelesen -- vier Lesungen je
/// Schirm statt einer je Zelle. Spaltenlagen gemessen an echten Schirmen (2026-10-01).
/// </remarks>
internal static class RaceResultsReader
{
    // Spalten im 1080p-Mass: Auto, die Zahl im PI-Feld, Fortschritt, Zeit.
    private static readonly (int X0, int X1) AutoSpalte = (705, 1072);
    private static readonly (int X0, int X1) PiSpalte = (1124, 1178);
    private static readonly (int X0, int X1) FortschrittSpalte = (1238, 1365);
    private static readonly (int X0, int X1) ZeitSpalte = (1398, 1580);
    private const int Rand = 20;

    private static readonly Lazy<WindowsOcr> Ocr = new(() => new WindowsOcr());

    /// <summary>Zur Fehlersuche (--results-read --dump): die Spaltenstreifen und was gelesen wurde.</summary>
    internal static string? Ablage { get; set; }

    private static readonly Regex Zeit = new(@"(\d{1,2})\s*[:;.,]+\s*(\d{2})\s*[.,:;]+\s*(\d{3})", RegexOptions.CultureInvariant);

    /// <summary>"01148.648": der Doppelpunkt als Ziffer 1 gelesen -- fuenf Ziffern vor dem Punkt gibt es sonst nicht.</summary>
    private static readonly Regex ZeitOhneDoppelpunkt = new(@"^\s*(\d{2})1(\d{2})[.,](\d{3})\s*$", RegexOptions.CultureInvariant);

    private static long? Millisekunden(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) { return null; }
        var z = Ziffern(text);
        var m = Zeit.Match(z);
        if (!m.Success) { m = ZeitOhneDoppelpunkt.Match(z); }
        if (!m.Success) { return null; }
        return (long.Parse(m.Groups[1].Value) * 60_000) + (long.Parse(m.Groups[2].Value) * 1000) + long.Parse(m.Groups[3].Value);
    }

    /// <summary>Was die Texterkennung in Zahlen verliest: "02•.35.736", "02:38.i70".</summary>
    private static string Ziffern(string text) =>
        text.Replace('\u2022', ':').Replace('\u00B7', ':').Replace('O', '0').Replace('o', '0')
            .Replace('i', '1').Replace('l', '1').Replace('I', '1').Replace('|', '1');
    private static readonly Regex Prozent = new(@"(\d{1,3})\s*%", RegexOptions.CultureInvariant);
    private static readonly Regex Zahl3 = new(@"\d{3}", RegexOptions.CultureInvariant);

    /// <summary>Die Zeilen des Ergebnisses; leer, wenn keine Texterkennung da ist.</summary>
    public static List<FieldEntry> Lies(Bitmap voll, GridRead e, WindowsOcr? ocr = null)
    {
        var raus = new List<FieldEntry>();
        ocr ??= Ocr.Value;
        if (!ocr.Available || e.Drivers <= 0) { return raus; }
        var sx = voll.Width / 1920f;
        var sy = voll.Height / 1080f;
        var versatz = e.Versatz * 1080f;

        string?[] Spalte((int X0, int X1) spalte, bool hellAufDunkel, int faktor, int dunkelGrenze = 120)
        {
            using var streifen = Streifen(voll, spalte, e.Drivers, versatz, sx, sy, hellAufDunkel, dunkelGrenze);
            // ZAHLEN DOPPELT SO GROSS: in Originalgroesse las die Texterkennung "02•.35.736" nur
            // halb. Die Autonamen dagegen in Originalgroesse -- doppelt gross wurde das Hochkomma
            // zur Ziffer ("Honda Beat 191").
            using var gross = new Bitmap(streifen.Width * faktor, streifen.Height * faktor, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(gross))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(streifen, new Rectangle(0, 0, gross.Width, gross.Height));
            }
            var texte = new string?[e.Drivers];
            var linien = ocr.Read(gross).Select(l => new OcrLine(l.Text, l.X / faktor, l.Y / faktor) { W = l.W / faktor, H = l.H / faktor }).ToList();
            if (Ablage is { } ordner)
            {
                Directory.CreateDirectory(ordner);
                var name = Path.Combine(ordner, $"spalte_{spalte.X0}");
                streifen.Save(name + ".png", ImageFormat.Png);
                File.WriteAllLines(name + ".txt", linien.Select(l => $"{l.Y,6:0} {l.H,4:0} {l.Text}"));
            }
            foreach (var linie in linien)
            {
                var mitte = linie.Y + (linie.H > 0 ? linie.H / 2 : 12);
                var s = (int)Math.Floor((mitte - Rand) / 54.0);
                if (s < 0 || s >= e.Drivers) { continue; }
                texte[s] = texte[s] is null ? linie.Text : texte[s] + " " + linie.Text;
            }
            return texte;
        }

        var autos = Spalte(AutoSpalte, false, 1, 150);
        // Zweimal gelesen: in Originalgroesse verlor die Texterkennung "Turbo" aus "911 Turbo S '23"
        // und "C-X75" aus "Jaguar C-X75"; doppelt gross machte sie aus dem Hochkomma eine Ziffer.
        // Es gilt die Lesung, die ein Auto ergibt -- die in Originalgroesse zuerst.
        var autosGross = Spalte(AutoSpalte, false, 2, 150);
        var pis = Spalte(PiSpalte, true, 2);
        var vierte = Spalte(FortschrittSpalte, false, 2);
        var zeiten = Spalte(ZeitSpalte, false, 2);
        for (var s = 0; s < e.Drivers; s++)
        {
            var art = s < e.Rows.Length ? e.Rows[s] : 'A';
            var eintrag = new FieldEntry
            {
                Place = s + 1,
                Kind = art switch { 'H' => "human", 'D' => "left", _ => "ai" },
                Self = e.Cursor == s + 1,
                CarShort = string.IsNullOrWhiteSpace(autos[s]) ? null : autos[s]!.Trim(),
            };
            var auto = CarShortNames.Finde(eintrag.CarShort);
            if (auto is null && !string.IsNullOrWhiteSpace(autosGross[s]) && CarShortNames.Finde(autosGross[s]) is { } zweite)
            {
                auto = zweite;
                eintrag.CarShort = autosGross[s]!.Trim();
            }
            if (auto is not null)
            {
                eintrag.Car = auto.Id;
                eintrag.CarName = auto.Name;
            }
            if (pis[s] is { } pi && Zahl3.Match(pi.Replace('O', '0').Replace('o', '0')) is { Success: true } p
                && int.TryParse(p.Value, out var piWert) && piWert is >= 100 and <= 999)
            {
                eintrag.Pi = piWert;
            }
            // Die vierte Spalte: "Progress" auf Strecken von A nach B, "Best Lap" auf Rundkursen.
            if (vierte[s] is { } v)
            {
                if (Millisekunden(v) is { } bestzeit)
                {
                    eintrag.BestLapMs = bestzeit;
                }
                else if (Prozent.Match(v) is { Success: true } fp && int.TryParse(fp.Groups[1].Value, out var pct))
                {
                    eintrag.Progress = Math.Clamp(pct, 0, 100);
                }
            }
            eintrag.Ms = Millisekunden(zeiten[s]);
            raus.Add(eintrag);
        }
        return raus;
    }

    /// <summary>
    /// Eine Spalte ueber alle Zeilen als dunkle Schrift auf Weiss.
    /// </summary>
    /// <param name="hellAufDunkel">Das PI-Feld: weisse Ziffern auf dunklem Grund, in jeder Zeile.</param>
    /// <param name="dunkelGrenze">
    /// Ab welcher Helligkeit ein Punkt auf weisser Zeile Tinte ist. Die Autonamen des Spiels sind
    /// nicht tiefschwarz: bei 120 fehlten "Turbo" und "C-X75", bei 150 kamen sie -- die Zahlen
    /// aber wurden bei 150 schlechter (8 statt 2 Zeiten verloren), darum je Spalte.
    /// </param>
    private static Bitmap Streifen(Bitmap voll, (int X0, int X1) spalte, int zeilen, float versatz,
                                   float sx, float sy, bool hellAufDunkel, int dunkelGrenze = 120)
    {
        var breite = spalte.X1 - spalte.X0;
        var bild = new Bitmap(breite + (2 * Rand), (zeilen * 54) + (2 * Rand), PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bild)) { g.Clear(Color.White); }
        var x0 = (int)(spalte.X0 * sx);
        var x1 = Math.Min(voll.Width, (int)(spalte.X1 * sx));
        var y0 = Math.Max(0, (int)((276 + versatz) * sy));
        var y1 = Math.Min(voll.Height, (int)((276 + versatz + (zeilen * 54)) * sy));
        if (x1 <= x0 || y1 <= y0) { return bild; }
        var quelle = voll.LockBits(new Rectangle(x0, y0, x1 - x0, y1 - y0), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var ziel = bild.LockBits(new Rectangle(0, 0, bild.Width, bild.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            var q = new byte[quelle.Stride * quelle.Height];
            System.Runtime.InteropServices.Marshal.Copy(quelle.Scan0, q, 0, q.Length);
            var z = new byte[ziel.Stride * ziel.Height];
            System.Runtime.InteropServices.Marshal.Copy(ziel.Scan0, z, 0, z.Length);
            for (var s = 0; s < zeilen; s++)
            {
                // Der Grund dieser Zeile: hell (weisse Zeile) oder dunkel (die eigene).
                var mitteQ = (int)(((300 + versatz + (54 * s)) * sy) - y0);
                var dunklerGrund = hellAufDunkel || Grund(q, quelle.Stride, quelle.Width, mitteQ) < 128;
                // Das PI-Feld ist niedriger als die Zeile: darueber und darunter waere weisser
                // Zeilengrund -- "hell auf dunkel" machte daraus schwarze Balken.
                var halb = hellAufDunkel ? 14 : 24;
                for (var dy = -halb; dy < halb; dy++)
                {
                    var yq = mitteQ + (int)(dy * sy);
                    if (yq < 0 || yq >= quelle.Height) { continue; }
                    var yz = Rand + (54 * s) + 27 + dy;
                    if (yz < 0 || yz >= bild.Height) { continue; }
                    for (var xz = 0; xz < breite; xz++)
                    {
                        var xq = (int)(xz * sx);
                        if (xq >= quelle.Width) { break; }
                        var i = (yq * quelle.Stride) + (xq * 3);
                        var hell = (q[i] + q[i + 1] + q[i + 2]) / 3;
                        // Auf dunklem Grund ist Schrift WEISS -- der limettengruene Rahmen der eigenen
                        // Zeile (Blau unter 90) wurde sonst zu schwarzen Balken.
                        // Auf dem Klassenkasten (rosa bei S1, blau bei S2, orange bei B) ist Tinte, was
                        // hell UND farblos ist -- die Kastenfarbe selbst ist hell, aber bunt.
                        var tinte = dunklerGrund
                            ? (hellAufDunkel
                                ? hell > 150 && Math.Abs(q[i] - q[i + 1]) < 40 && Math.Abs(q[i + 1] - q[i + 2]) < 40
                                : hell > 150 && q[i] > 120)
                            : hell < dunkelGrenze;
                        if (!tinte) { continue; }
                        var j = (yz * ziel.Stride) + ((Rand + xz) * 3);
                        z[j] = z[j + 1] = z[j + 2] = 0;
                    }
                }
            }
            System.Runtime.InteropServices.Marshal.Copy(z, 0, ziel.Scan0, z.Length);
        }
        finally
        {
            voll.UnlockBits(quelle);
            bild.UnlockBits(ziel);
        }
        return bild;
    }

    /// <summary>Die mittlere Helligkeit am linken Rand der Spalte -- dort steht keine Schrift.</summary>
    private static int Grund(byte[] q, int stride, int breite, int y)
    {
        if (y < 0 || y * stride >= q.Length) { return 255; }
        long summe = 0;
        var n = 0;
        for (var x = 0; x < Math.Min(8, breite); x++)
        {
            var i = (y * stride) + (x * 3);
            if (i + 2 >= q.Length) { break; }
            summe += (q[i] + q[i + 1] + q[i + 2]) / 3;
            n++;
        }
        return n == 0 ? 255 : (int)(summe / n);
    }
}
