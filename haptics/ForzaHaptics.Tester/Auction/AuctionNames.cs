using System.Text.Json;
using ForzaHaptics.Tester.Rivals;

namespace ForzaHaptics.Tester.Auction;

/// <summary>
/// Den Autonamen einer Auktionskarte gegen die Modellnamen des Spiels berichtigen.
/// </summary>
/// <remarks>
/// Die Karten zeigen den Modellnamen in Grossbuchstaben ("ESSENZA SCV12", "CHALLENGER R/T"). Die
/// Texterkennung verliest ihn manchmal -- am 2026-10-04 "ESSENZA SCVu" und "CHALLENGER RIT" --, und weil
/// eine Auktion ueber ihren Namen wiedergefunden wird, wurde aus einem Verleser ein zweiter Eintrag.
/// Die Modellnamen stehen in config/fh6_car_short_names.json ("model", aus den Stringtabellen des Spiels,
/// 648 Autos). Berichtigt wird wie bei den Kurznamen (<see cref="CarShortNames.Finde"/>): genau, sonst
/// der EINE naechste mit hoechstens zwei Zeichen Abstand. Zwei gleich nahe Modelle: nichts. Unter acht
/// Zeichen nur genau -- "M3" darf nie "M5" werden, am Namen haengen Eintrag und Preisgeschichte.
/// </remarks>
internal static class AuctionNames
{
    private static readonly object Schloss = new();
    private static Dictionary<string, string>? _modelle;

    /// <summary>Fuer Tests: eine andere Datei.</summary>
    internal static string? Pfad { get; set; }

    internal static void Vergessen()
    {
        lock (Schloss) { _modelle = null; }
    }

    /// <summary>Gefalteter Modellname -> Modellname, wie die Karte ihn zeigt (Grossbuchstaben).</summary>
    private static Dictionary<string, string> Laden()
    {
        lock (Schloss)
        {
            if (_modelle is not null) { return _modelle; }
            var raus = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pfad in Pfad is { } p ? new[] { p } : Kandidaten())
            {
                if (!File.Exists(pfad)) { continue; }
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(pfad));
                    if (doc.RootElement.TryGetProperty("cars", out var autos))
                    {
                        foreach (var e in autos.EnumerateObject())
                        {
                            if (e.Value.TryGetProperty("model", out var m) && m.GetString() is { Length: > 0 } modell)
                            {
                                raus.TryAdd(CarShortNames.Schluessel(modell), modell.Trim().ToUpperInvariant());
                            }
                        }
                    }
                    break;
                }
                catch (Exception)
                {
                    // Eine kaputte Datei: ohne Berichtigung weiter.
                }
            }
            return _modelle = raus;
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

    /// <summary>Der berichtigte Name -- oder der gelesene, wenn kein Modell eindeutig passt.</summary>
    public static string? Berichtige(string? gelesen)
    {
        if (string.IsNullOrWhiteSpace(gelesen)) { return gelesen; }
        var roh = gelesen.Trim();
        var modelle = Laden();
        var s = CarShortNames.Schluessel(roh);
        if (modelle.TryGetValue(s, out var genau)) { return genau; }
        if (s.Length < 8) { return roh; }
        var f = CarShortNames.Falte(s);
        string? bester = null;
        var besterAbstand = int.MaxValue;
        var gleich = false;
        foreach (var (k, anzeige) in modelle)
        {
            if (Math.Abs(k.Length - s.Length) > 2) { continue; }
            var d = CarShortNames.Abstand(CarShortNames.Falte(k), f, 2);
            if (d < besterAbstand) { besterAbstand = d; bester = anzeige; gleich = false; }
            else if (d == besterAbstand && bester != anzeige) { gleich = true; }
        }
        return besterAbstand <= 2 && !gleich && bester is not null ? bester : roh;
    }
}
