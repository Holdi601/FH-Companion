using System.Drawing;
using System.Text.Json;
using ForzaHaptics.Tester.Scan;

namespace ForzaHaptics.Tester.Auction;

/// <summary>
/// Betraege im Auktionshaus Ziffer fuer Ziffer erkennen -- weil die Windows-Texterkennung sie
/// verweigert.
/// </summary>
/// <remarks>
/// Gemessen 2026-10-04: "184,000" und "283,000" liest Windows OCR, "10,000,000", "20,000,000" und
/// "3,000,000" dagegen GAR NICHT -- auch sauber schwarz auf weiss, mit mehr Rand, in jeder Groesse
/// und mit einem Wort davor (das Wort kam, die Zahl nicht). Die Ziffern sind eine einzige fette
/// Schrift; also werden sie ausgeschnitten und mit Vorlagen verglichen.
///
/// Die Vorlagen LERNT die App: jedes Feld, das die Texterkennung doch liest und dessen Ziffernzahl
/// zur Zahl der ausgeschnittenen Zeichen passt, liefert je Ziffer ein Beispiel. Mitgeliefert wird ein
/// Grundstock (config/fh6_auction_digits.json), gelernt wird dazu (Datenordner).
/// </remarks>
internal static class AuctionDigits
{
    private const int B = 10, H = 16;
    private static readonly object Schloss = new();
    // GRUNDSTOCK UND GELERNTES GETRENNT (2026-10-04): der Grundstock entstand an Bildern mit HDR und traegt
    // schon acht Nullen. Lag beides in einer Liste mit hoechstens acht je Ziffer, konnte eine Installation
    // ohne HDR nie eine eigene "0" lernen -- und gerade "10,000,000" liest nur die Vorlage. Die Grenze gilt
    // jetzt nur fuers Gelernte, und die Lerndatei traegt nur noch das Gelernte (vorher den Grundstock mit,
    // der beim naechsten Start ein zweites Mal dazukam).
    private static Dictionary<char, List<bool[]>>? _grund, _gelernt;

    internal static string LernDatei => Path.Combine(AppInfo.DataFolder, "auction_digits.json");

    /// <summary>Wo der Grundstock liegt: config/ neben dem Programm, sonst aufwaerts.</summary>
    internal static string? Grundstock()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var p = Path.Combine(dir.FullName, "config", "fh6_auction_digits.json");
            if (File.Exists(p)) { return p; }
            dir = dir.Parent;
        }
        return null;
    }

    private static Dictionary<char, List<bool[]>> Lies(string? datei)
    {
        var v = new Dictionary<char, List<bool[]>>();
        if (datei is null || !File.Exists(datei)) { return v; }
        try
        {
            var d = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(datei)) ?? new();
            foreach (var (k, liste) in d)
            {
                if (k.Length != 1 || !char.IsDigit(k[0])) { continue; }
                if (!v.TryGetValue(k[0], out var l)) { v[k[0]] = l = new List<bool[]>(); }
                l.AddRange(liste.Where(s => s.Length == B * H).Select(s => s.Select(c => c == '1').ToArray()));
            }
        }
        catch (Exception) { }
        return v;
    }

    /// <summary>Unter der Sperre: Grundstock und Gelerntes laden (einmal).</summary>
    private static void Laden()
    {
        if (_grund is not null && _gelernt is not null) { return; }
        _grund = Lies(Grundstock());
        _gelernt = Lies(LernDatei);
        // Alte Lerndateien trugen den Grundstock mit: was dort genau so im Grundstock steht, gilt nicht als gelernt.
        foreach (var (k, liste) in _gelernt)
        {
            if (_grund.TryGetValue(k, out var g)) { liste.RemoveAll(f => g.Any(t => t.SequenceEqual(f))); }
        }
    }

    /// <summary>Alle Vorlagen, Grundstock und Gelerntes zusammen (eine Kopie -- fuer den Vergleich).</summary>
    private static Dictionary<char, List<bool[]>> Vorlagen()
    {
        lock (Schloss)
        {
            Laden();
            var v = _grund!.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
            foreach (var (k, l) in _gelernt!)
            {
                if (!v.TryGetValue(k, out var ziel)) { v[k] = ziel = new List<bool[]>(); }
                ziel.AddRange(l);
            }
            return v;
        }
    }

    internal static void Vergessen()
    {
        lock (Schloss) { _grund = null; _gelernt = null; }
    }

    /// <summary>So viele Vorlagen dieser Ziffer hat die Installation selbst gelernt (fuer den Grenzfalltest).</summary>
    internal static int Gelernt(char ziffer)
    {
        lock (Schloss) { Laden(); return _gelernt!.TryGetValue(ziffer, out var l) ? l.Count : 0; }
    }

    internal static int Bekannt => Vorlagen().Count;

    /// <summary>Ein Zeichen: Lage im Feld und Form (10x16).</summary>
    internal sealed record Zeichen(int Links, int Rechts, int Oben, int Unten, bool[] Form, bool Komma);

    /// <summary>
    /// Die Zeichen eines Feldes: Tinte ist, was sich um mehr als 70 vom Feldgrund (Median) abhebt.
    /// Kommas (niedrig, unten) werden markiert; zusammengewachsene Ziffern geteilt.
    /// </summary>
    internal static List<Zeichen> Zerlege(BoardReader.Pixel px, Rectangle r)
    {
        var werte = new List<int>();
        for (var y = r.Top; y < r.Bottom; y += 2) { for (var x = r.Left; x < r.Right; x += 2) { werte.Add(px.Hell(x, y)); } }
        werte.Sort();
        var grund = werte.Count > 0 ? werte[werte.Count / 2] : 255;
        var tinte = new bool[r.Width, r.Height];
        for (var y = 0; y < r.Height; y++)
        {
            for (var x = 0; x < r.Width; x++) { tinte[x, y] = Math.Abs(px.Hell(r.X + x, r.Y + y) - grund) > 70; }
        }
        var spalten = new int[r.Width];
        for (var x = 0; x < r.Width; x++) { for (var y = 0; y < r.Height; y++) { if (tinte[x, y]) { spalten[x]++; } } }

        var laeufe = new List<(int L, int R)>();
        for (var x = 0; x < r.Width;)
        {
            if (spalten[x] == 0) { x++; continue; }
            var s = x;
            while (x < r.Width && spalten[x] > 0) { x++; }
            laeufe.Add((s, x - 1));
        }
        (int O, int U) Hoehe(int l, int rr)
        {
            int o = int.MaxValue, u = -1;
            for (var x = l; x <= rr; x++) { for (var y = 0; y < r.Height; y++) { if (tinte[x, y]) { o = Math.Min(o, y); u = Math.Max(u, y); } } }
            return (o, u);
        }
        var hoehen = laeufe.Select(l => Hoehe(l.L, l.R)).ToList();
        if (laeufe.Count == 0) { return new List<Zeichen>(); }
        var ziffernHoehe = hoehen.Max(h => h.U - h.O + 1);
        var grundlinie = hoehen.Where(h => h.U - h.O + 1 >= ziffernHoehe * 0.8).Select(h => h.U).DefaultIfEmpty(0).Max();
        // Typische Ziffernbreite: Median der hohen Laeufe ohne die schmalen Einsen.
        var breiten = laeufe.Zip(hoehen).Where(p => p.Second.U - p.Second.O + 1 >= ziffernHoehe * 0.8)
                            .Select(p => p.First.R - p.First.L + 1).Where(w => w > ziffernHoehe * 0.45).OrderBy(w => w).ToList();
        var breite = breiten.Count > 0 ? breiten[breiten.Count / 2] : ziffernHoehe * 0.6;

        var raus = new List<Zeichen>();
        for (var i = 0; i < laeufe.Count; i++)
        {
            var (l, rr) = laeufe[i];
            var (o, u) = hoehen[i];
            var h = u - o + 1;
            if (h < ziffernHoehe * 0.6)
            {
                // Niedrig: ein Komma (reicht unter die Grundlinie) -- oder Stoerung.
                raus.Add(new Zeichen(l, rr, o, u, Array.Empty<bool>(), u >= grundlinie - 2));
                continue;
            }
            // Zusammengewachsen ("0," oder "00"): an den duennsten Spalten teilen.
            var w = rr - l + 1;
            var teile = Math.Max(1, (int)Math.Round(w / breite));
            if (teile == 1 || w < breite * 1.45)
            {
                raus.Add(Form(tinte, l, rr, o, u));
                continue;
            }
            var grenzen = new List<int> { l };
            for (var t = 1; t < teile; t++)
            {
                var soll = l + (int)Math.Round(w * t / (double)teile);
                var beste = soll;
                for (var x = Math.Max(l + 2, soll - 4); x <= Math.Min(rr - 2, soll + 4); x++) { if (spalten[x] < spalten[beste]) { beste = x; } }
                grenzen.Add(beste);
            }
            grenzen.Add(rr + 1);
            for (var t = 0; t < grenzen.Count - 1; t++)
            {
                var (to, tu) = Hoehe(grenzen[t], grenzen[t + 1] - 1);
                if (tu < 0) { continue; }
                if (tu - to + 1 < ziffernHoehe * 0.6)
                {
                    raus.Add(new Zeichen(grenzen[t], grenzen[t + 1] - 1, to, tu, Array.Empty<bool>(), tu >= grundlinie - 2));
                }
                else
                {
                    raus.Add(Form(tinte, grenzen[t], grenzen[t + 1] - 1, to, tu));
                }
            }
        }
        return raus;
    }

    private static Zeichen Form(bool[,] tinte, int l, int r, int o, int u)
    {
        var form = new bool[B * H];
        var w = r - l + 1;
        var h = u - o + 1;
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < B; x++)
            {
                int an = 0, alle = 0;
                for (var yy = o + (y * h / H); yy < o + ((y + 1) * h / H) || yy == o + (y * h / H); yy++)
                {
                    for (var xx = l + (x * w / B); xx < l + ((x + 1) * w / B) || xx == l + (x * w / B); xx++)
                    {
                        alle++;
                        if (tinte[Math.Min(xx, r), Math.Min(yy, u)]) { an++; }
                    }
                }
                form[(y * B) + x] = an * 2 >= alle;
            }
        }
        return new Zeichen(l, r, o, u, form, false);
    }

    /// <summary>
    /// Den Betrag lesen; null, wenn ein Zeichen keiner Vorlage sicher gleicht, keine Vorlagen da sind
    /// oder die Kommas nicht aufgehen (erste Gruppe 1-3 Ziffern, jede weitere genau 3).
    /// </summary>
    /// <remarks>
    /// Gelesen wird auf einer Abschrift der Vorlagen: <see cref="Lerne"/> fuegt aus einem anderen Faden
    /// (Overlay und Bieter lesen dieselben Schirme) Vorlagen hinzu, und eine Liste, die waehrend des
    /// Durchlaufens waechst, wirft "Collection was modified" -- mitten in der Fahrt des Bieters.
    /// Ohne die Kommapruefung wurde aus "10,000,000" mit EINER unsicheren Ziffer 10000.
    /// </remarks>
    internal static long? Lies(BoardReader.Pixel px, Rectangle r)
    {
        Dictionary<char, bool[][]> vorlagen;
        lock (Schloss) { vorlagen = Vorlagen().ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()); }
        if (vorlagen.Count < 10) { return null; }
        var zeichen = Zerlege(px, r);
        // Ziffern je Gruppe; ein Komma beginnt eine neue.
        var gruppen = new List<int> { 0 };
        var ziffern = new List<char>();
        foreach (var z in zeichen)
        {
            if (z.Form.Length == 0)
            {
                // Komma oder Stoerung
                if (z.Komma && gruppen[^1] > 0) { gruppen.Add(0); }
                continue;
            }
            var (ziffer, sicher) = Erkenne(z.Form, vorlagen);
            if (!sicher)
            {
                // Hinter den Ziffern darf etwas Fremdes stehen (der rote Pfeil beim Sofortkauf) -- aber
                // nur hinter einer vollen Dreiergruppe nach einem Komma, sonst fehlt eine Ziffer.
                if (gruppen.Count >= 2 && gruppen[^1] == 3) { break; }
                return null;
            }
            ziffern.Add(ziffer);
            gruppen[^1]++;
        }
        if (gruppen.Count > 1 && gruppen[^1] == 0) { gruppen.RemoveAt(gruppen.Count - 1); }
        if (gruppen[0] is < 1 or > 3 && gruppen.Count > 1) { return null; }
        if (gruppen.Skip(1).Any(g => g != 3)) { return null; }
        return ziffern.Count >= 1 && long.TryParse(new string(ziffern.ToArray()), out var v) ? v : null;
    }

    private static (char Ziffer, bool Sicher) Erkenne(bool[] form, Dictionary<char, bool[][]> vorlagen)
    {
        var abstaende = vorlagen.Select(kv => (Ziffer: kv.Key, Abstand: kv.Value.Min(v => Abstand(form, v))))
                                .OrderBy(a => a.Abstand).ToList();
        var beste = abstaende[0];
        var zweite = abstaende.Count > 1 ? abstaende[1].Abstand : int.MaxValue;
        return (beste.Ziffer, beste.Abstand <= B * H * 0.16 && zweite - beste.Abstand >= 6);
    }

    private static int Abstand(bool[] a, bool[] b)
    {
        var n = 0;
        for (var i = 0; i < a.Length; i++) { if (a[i] != b[i]) { n++; } }
        return n;
    }

    /// <summary>
    /// Aus einem Feld lernen, das die Texterkennung gelesen hat: passt die Zahl der Ziffern zur Zahl der
    /// Zeichen, wird jede Ziffer eine Vorlage (hoechstens acht je Ziffer, keine Beinahe-Doppel).
    /// </summary>
    internal static bool Lerne(BoardReader.Pixel px, Rectangle r, long wert, bool speichern = true)
    {
        var ziffern = wert.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var formen = Zerlege(px, r).Where(z => z.Form.Length > 0).ToList();
        if (formen.Count < ziffern.Length) { return false; }
        // Hinter dem Betrag darf noch etwas stehen (Pfeil); die ersten Zeichen sind die Ziffern.
        var neu = false;
        lock (Schloss)
        {
            Laden();
            for (var i = 0; i < ziffern.Length; i++)
            {
                var grund = _grund!.TryGetValue(ziffern[i], out var g) ? g : new List<bool[]>();
                if (!_gelernt!.TryGetValue(ziffern[i], out var liste)) { _gelernt[ziffern[i]] = liste = new List<bool[]>(); }
                // Hoechstens acht GELERNTE je Ziffer; keine Beinahe-Doppel zu irgendeiner Vorlage.
                if (liste.Count >= 8 || liste.Concat(grund).Any(t => Abstand(t, formen[i].Form) <= 4)) { continue; }
                liste.Add(formen[i].Form);
                neu = true;
            }
        }
        if (neu && speichern) { Speichern(LernDatei, nurGelernt: true); }
        return neu;
    }

    /// <param name="nurGelernt">Die Lerndatei: nur das Gelernte. Sonst (Bau des Grundstocks) alles.</param>
    internal static void Speichern(string datei, bool nurGelernt = false)
    {
        Dictionary<string, List<string>> d;
        lock (Schloss)
        {
            Laden();
            var quelle = nurGelernt ? _gelernt! : Vorlagen();
            d = quelle.Where(k => k.Value.Count > 0).OrderBy(k => k.Key).ToDictionary(k => k.Key.ToString(),
                k => k.Value.Select(f => new string(f.Select(b => b ? '1' : '0').ToArray())).ToList());
        }
        // Overlay und Bieter lernen gleichzeitig: nacheinander schreiben, ueber eine Zwischendatei.
        lock (DateiSchloss)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(datei)!);
                var tmp = datei + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, datei, overwrite: true);
            }
            catch (Exception) { }
        }
    }

    private static readonly object DateiSchloss = new();
}
