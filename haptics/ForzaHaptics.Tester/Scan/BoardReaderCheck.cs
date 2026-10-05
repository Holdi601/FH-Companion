using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using ForzaHaptics.Tester.Rivals;

namespace ForzaHaptics.Tester.Scan;

/// <summary>
/// Den Zeilenleser gegen fertige Laeufe des alten Werkzeugs pruefen: aus den Belegbildern
/// (proof.zip, je Platz ein 1700x50-Streifen) werden Tafeln zu elf Zeilen gesetzt und gelesen,
/// und jede Zeile wird mit dem verglichen, was das alte Werkzeug aus 7-13 Bildern abgestimmt hat.
/// </summary>
internal static class BoardReaderCheck
{
    /// <summary>Wo das alte Werkzeug seinen Ausschnitt nahm: x ab 78, Zeile k ab 276 + 54 k.</summary>
    internal const int AusschnittX = 78;

    public static int Run(string lauf, int hoechstens, string? ablage)
    {
        var belege = Path.Combine(lauf, "proof.zip");
        var zeilen = Path.Combine(lauf, "rows.jsonl");
        if (!File.Exists(belege) || !File.Exists(zeilen)) { Console.WriteLine("needs proof.zip and rows.jsonl in " + lauf); return 2; }
        var alt = new Dictionary<int, JsonElement>();
        foreach (var l in File.ReadLines(zeilen))
        {
            if (string.IsNullOrWhiteSpace(l)) { continue; }
            var e = JsonDocument.Parse(l).RootElement.Clone();
            alt[e.GetProperty("rank").GetInt32()] = e;
        }
        using var zip = ZipFile.OpenRead(belege);
        var eintraege = zip.Entries.Where(e => e.Name.StartsWith('r') && e.Name.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
                                   .OrderBy(e => e.Name).ToList();
        var ocr = new WindowsOcr();
        int geprueft = 0, rang = 0, zeit = 0, pi = 0, auto = 0, antrieb = 0, gang = 0, hilfen = 0, hilfenGesehen = 0, autoGesehen = 0, sauber = 0;
        var fehler = new List<string>();
        var schritt = Math.Max(1, eintraege.Count / Math.Max(1, hoechstens / 11 * 11));
        var auswahl = eintraege.Where((_, i) => i % schritt == 0).Take(hoechstens).ToList();
        for (var start = 0; start < auswahl.Count; start += BoardReader.Zeilen)
        {
            var tafel = auswahl.Skip(start).Take(BoardReader.Zeilen).ToList();
            using var bild = new Bitmap(1920, 1080, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bild))
            {
                g.Clear(Color.FromArgb(40, 160, 150));
                for (var k = 0; k < tafel.Count; k++)
                {
                    // Das alte Werkzeug schnitt Zeile i ab y = 268 + int(12 + 54.2 i) -- 4 bis 7 px
                    // unter dem Zeilenanfang (276 + 54 i). Genau so weit tiefer gehoert der Streifen.
                    var ri = alt.TryGetValue(int.Parse(tafel[k].Name[1..^5], CultureInfo.InvariantCulture), out var ar)
                             && ar.TryGetProperty("row_index", out var rie) ? rie.GetInt32() : 0;
                    var tiefer = 268 + (int)(12 + (54.2 * ri)) - (BoardReader.Basis + (BoardReader.Abstand * ri));
                    using var s = tafel[k].Open();
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    using var streifen = LiesWebp(ms.ToArray());
                    if (streifen is null) { continue; }
                    g.DrawImage(streifen, AusschnittX, BoardReader.Basis + (BoardReader.Abstand * k) + tiefer, streifen.Width, streifen.Height);
                }
            }
            if (ablage is not null && start == 0)
            {
                Directory.CreateDirectory(ablage);
                bild.Save(Path.Combine(ablage, "tafel0.png"), ImageFormat.Png);
            }
            var gelesen = BoardReader.Lies(bild, ocr);
            for (var k = 0; k < tafel.Count; k++)
            {
                var soll = int.Parse(tafel[k].Name[1..^5], CultureInfo.InvariantCulture);
                if (!alt.TryGetValue(soll, out var a)) { continue; }
                var r = gelesen[k];
                geprueft++;
                var okRang = r.Rank == soll;
                var altMs = (long)Math.Round(a.GetProperty("lap_time_seconds").GetDouble() * 1000);
                var okZeit = r.Ms == altMs;
                var altPi = a.TryGetProperty("pi", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : (int?)null;
                var okPi = altPi is null || r.Pi == altPi;
                var altAuto = a.TryGetProperty("car_name", out var c) ? c.GetString() : null;
                // Verglichen wird das AUTO, nicht der Text: beide Lesungen haben Fehler ("ofl Alfa SE 048SP").
                var altId = Rivals.CarShortNames.Finde(altAuto);
                var okAuto = altId is null || Rivals.CarShortNames.Finde(r.Car)?.Id == altId.Id;
                if (altId is not null) { autoGesehen++; }
                var altAntrieb = a.TryGetProperty("drivetrain", out var d) ? d.GetString() : null;
                var okAntrieb = altAntrieb is null || r.Drivetrain == altAntrieb;
                var altGang = a.TryGetProperty("gearbox", out var gb) ? gb.GetString() : null;
                var okGang = altGang is null || r.Gearbox == altGang;
                var altHilfen = new[] { a.GetProperty("used_abs").GetBoolean(), a.GetProperty("used_tcs").GetBoolean(), a.GetProperty("used_stm").GetBoolean() };
                var neuHilfen = new[] { r.Abs, r.Tcs, r.Stm };
                if (neuHilfen.All(h => h is not null)) { hilfenGesehen++; if (neuHilfen.Select(h => h!.Value).SequenceEqual(altHilfen)) { hilfen++; } }
                var altSauber = a.TryGetProperty("is_clean", out var ic) && ic.ValueKind is JsonValueKind.True or JsonValueKind.False ? ic.GetBoolean() : (bool?)null;
                if (altSauber is null || r.IsClean == altSauber) { sauber++; }
                if (okRang) { rang++; }
                if (okZeit) { zeit++; }
                if (okPi) { pi++; }
                if (okAuto) { auto++; }
                if (okAntrieb) { antrieb++; }
                if (okGang) { gang++; }
                if (!(okRang && okZeit && okAuto && okGang) && fehler.Count < 30)
                {
                    fehler.Add($"  #{soll}: rank={r.Rank} ms={r.Ms}/{altMs} car={r.Car}/{altAuto} gear={r.Gearbox}/{altGang} | {r.Line}");
                }
            }
        }
        Console.WriteLine($"{geprueft} rows checked against the old tool's voted rows:");
        Console.WriteLine($"  rank {rang}, time {zeit}, PI {pi}, car {auto} ({autoGesehen} resolvable), drivetrain {antrieb}, gearbox {gang}, "
                          + $"assists {hilfen}/{hilfenGesehen}, clean flag {sauber}");
        foreach (var f in fehler) { Console.WriteLine(f); }
        return 0;
    }

    /// <summary>Autonamen vergleichen, ohne Leerzeichen und Gross/Klein -- das alte Werkzeug liest ebenfalls mit Fehlern.</summary>
    private static bool Gleich(string? a, string? b) =>
        a is not null && b is not null && ScanScreen.Falte(a) == ScanScreen.Falte(b);

    /// <summary>WebP ueber den Windows-Bilddecoder (WIC); GDI+ kann es nicht.</summary>
    private static Bitmap? LiesWebp(byte[] daten)
    {
        try
        {
            using var strom = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var schreiber = new Windows.Storage.Streams.DataWriter(strom.GetOutputStreamAt(0)))
            {
                schreiber.WriteBytes(daten);
                schreiber.StoreAsync().AsTask().GetAwaiter().GetResult();
                schreiber.FlushAsync().AsTask().GetAwaiter().GetResult();
            }
            var decoder = Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(strom).AsTask().GetAwaiter().GetResult();
            var pixel = decoder.GetPixelDataAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Ignore,
                new Windows.Graphics.Imaging.BitmapTransform(), Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
                Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage).AsTask().GetAwaiter().GetResult();
            var bytes = pixel.DetachPixelData();
            var w = (int)decoder.PixelWidth;
            var h = (int)decoder.PixelHeight;
            var bild = new Bitmap(w, h, PixelFormat.Format32bppRgb);
            var d = bild.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try
            {
                for (var y = 0; y < h; y++) { System.Runtime.InteropServices.Marshal.Copy(bytes, y * w * 4, d.Scan0 + (y * d.Stride), w * 4); }
            }
            finally
            {
                bild.UnlockBits(d);
            }
            return bild;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
