namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Der Name eines Autos zu seiner Nummer (car_id / CarOrdinal) -- fuer die Rundendateien.
/// </summary>
/// <remarks>
/// Seit 2026-10-01. Ein Auswertungswerkzeug, das die Runden liest, zeigte nur "car 1269":
/// in der Rundendatei stand allein die Nummer. Dieselbe Reihenfolge wie die Anzeige der
/// App: ein vom Schirm gelerntes Paar zuerst (Beleg), dann der Datensatz der Bestenlisten
/// (Annahme, dass Ordinal = car_id), sonst die Autoliste (forza.net und Wiki, mit car_ids).
/// </remarks>
internal static class AutoNamen
{
    /// <summary>Wie die Namen in die Dateien kommen: "Coupé '13" lesbar, nicht "Coup\u00E9 \u002713".</summary>
    internal static readonly System.Text.Json.JsonSerializerOptions Lesbar = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string? Fuer(int ordinal, RivalsAdvisor? berater, CarCollection? liste,
                               OrdinalMap? gelernt = null)
    {
        if (ordinal <= 0) { return null; }
        try
        {
            var paar = gelernt?.Lookup(ordinal);
            if (RivalsAdvisor.IsRealCarName(paar?.Name)) { return paar!.Name; }
            if ((paar?.CarIndex ?? berater?.CarIndexForId(ordinal)) is { } i
                && berater?.RealCarName(i) is { Length: > 0 } name)
            {
                return name;
            }
            var auto = liste?.Autos.FirstOrDefault(a => a.AlleIds.Contains(ordinal));
            return auto is not null && auto.Name.Length > 0 ? auto.Anzeige : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static readonly System.Text.RegularExpressions.Regex Nummer =
        new(@"""carOrdinal""\s*:\s*(\d+)\s*,", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Den abgelegten Runden, denen er fehlt, den Autonamen nachtragen.
    /// </summary>
    /// <remarks>
    /// Ohne die Datei zu zerlegen: der Name wird direkt hinter "carOrdinal" eingesetzt, das
    /// vorn in jeder Rundendatei steht. Eine Runde ist bis zu einem Megabyte gross; tausend
    /// davon zu parsen und neu zu schreiben hiesse, den Bestand einmal ganz umzuwaelzen.
    /// Geschrieben wird ueber eine Zwischendatei, gewartet wird zwischen den Dateien -- das
    /// laeuft neben dem Spiel und soll dort nicht auffallen.
    /// </remarks>
    /// <returns>Wie viele Dateien einen Namen bekamen.</returns>
    private static readonly System.Text.RegularExpressions.Regex NameImKopf =
        new(@"""carName""\s*:\s*""((?:[^""\\]|\\.)*)""\s*,", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <param name="gelernt">
    /// Der vom Schirm gelernte Name je Nummer (seit 2026-10-03): steht in einer Datei ein anderer,
    /// wird er berichtigt -- die 2177 hiess drei Runden lang "Corvette '53", das Spiel sagt '15.
    /// </param>
    public static int Nachtragen(string wurzel, Func<int, string?> name, CancellationToken abbruch = default,
                                 int pauseMs = 40, Func<int, string?>? gelernt = null)
    {
        if (!Directory.Exists(wurzel)) { return 0; }
        var n = 0;
        foreach (var datei in Directory.EnumerateFiles(wurzel, "*.json", SearchOption.AllDirectories))
        {
            if (abbruch.IsCancellationRequested) { break; }
            if (Path.GetFileName(datei).Equals("course.json", StringComparison.OrdinalIgnoreCase)) { continue; }
            try
            {
                string kopf;
                using (var strom = File.OpenRead(datei))
                {
                    var puffer = new byte[1024];
                    var gelesen = strom.Read(puffer, 0, puffer.Length);
                    kopf = System.Text.Encoding.UTF8.GetString(puffer, 0, gelesen);
                }
                var m = Nummer.Match(kopf);
                if (!m.Success || !int.TryParse(m.Groups[1].Value, out var ordinal)) { continue; }
                string neu;
                if (kopf.Contains("\"carName\"", StringComparison.Ordinal))
                {
                    // Schon benannt: nur ein gelernter Name darf einen anderen ersetzen.
                    if (gelernt?.Invoke(ordinal) is not { Length: > 0 } richtig) { continue; }
                    var da = NameImKopf.Match(kopf);
                    if (!da.Success || LapAutoSubmit.GleichesAuto(System.Text.RegularExpressions.Regex.Unescape(da.Groups[1].Value), richtig)) { continue; }
                    var text = File.ReadAllText(datei, System.Text.Encoding.UTF8);
                    var alt = NameImKopf.Match(text);
                    if (!alt.Success) { continue; }
                    neu = text[..alt.Index] + "\"carName\":" + System.Text.Json.JsonSerializer.Serialize(richtig, Lesbar) + "," + text[(alt.Index + alt.Length)..];
                }
                else
                {
                    if (name(ordinal) is not { Length: > 0 } autoname) { continue; }
                    var text = File.ReadAllText(datei, System.Text.Encoding.UTF8);
                    var stelle = Nummer.Match(text);
                    if (!stelle.Success) { continue; }
                    var einschub = "\"carName\":" + System.Text.Json.JsonSerializer.Serialize(autoname, Lesbar) + ",";
                    neu = text.Insert(stelle.Index + stelle.Length, einschub);
                }
                var tmp = datei + ".tmp";
                File.WriteAllText(tmp, neu, new System.Text.UTF8Encoding(false));
                File.Move(tmp, datei, overwrite: true);
                n++;
                if (pauseMs > 0) { Thread.Sleep(pauseMs); }
            }
            catch (Exception)
            {
                // Eine gesperrte oder kaputte Datei behaelt ihre Form.
            }
        }
        return n;
    }
}
