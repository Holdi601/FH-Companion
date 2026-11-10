using System.Globalization;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Der Stichtag fuer die Rivals-Daten: nur Runden, die seit diesem Tag gescannt wurden.
/// </summary>
/// <remarks>
/// Seit 2026-11-10. Am 2026-11-03 hat das Spiel Fahrzeuge abgeschwaecht und seine
/// Rivals-Bestenlisten geloescht. Der Datensatz behaelt die Geschichte aller Scans --
/// ohne Stichtag stuenden die alten Zeiten weiter neben den neuen, und das Panel "what
/// to drive" empfaehle ein Auto, das es so nicht mehr gibt.
///
/// Die Einstellung steht in <c>config/overlay.json</c> ("rivals_since"): ein Tag
/// (JJJJ-MM-TT) oder "all" fuer alle Daten. Vorgabe ist <see cref="Vorgabe"/>.
/// Alles, was die App aus dem Datensatz rechnet, geht ueber
/// <see cref="RivalsDataset.Load(string)"/> und sieht damit nur die Sicht ab dem
/// Stichtag -- dieselbe Rechnung wie <c>server/stichtag.py</c> und
/// <c>scripts/dataset_cutoff.js</c>.
/// </remarks>
internal static class Stichtag
{
    /// <summary>Der Tag, an dem das Spiel seine Rivals-Bestenlisten geloescht hat.</summary>
    public const string Vorgabe = "2026-11-03";

    /// <summary>Der Wert der Einstellung fuer "alle Daten".</summary>
    public const string Alle = "all";

    private static readonly object Schloss = new();
    private static string? _wert;
    private static bool _gesetzt;

    /// <summary>Der geltende Stichtag (JJJJ-MM-TT), oder null fuer alle Daten.</summary>
    /// <remarks>Beim ersten Zugriff aus der Einstellung gelesen, danach ueber <see cref="Setzen"/>.</remarks>
    public static string? Aktuell
    {
        get
        {
            lock (Schloss)
            {
                if (!_gesetzt)
                {
                    string? roh;
                    try { roh = OverlaySettings.Load().RivalsSince; }
                    catch (Exception) { roh = Vorgabe; }
                    _wert = Lesen(roh);
                    _gesetzt = true;
                }
                return _wert;
            }
        }
    }

    /// <summary>Den Stichtag aus einer Einstellung uebernehmen (siehe <see cref="Lesen"/>).</summary>
    public static void Setzen(string? einstellung)
    {
        lock (Schloss)
        {
            _wert = Lesen(einstellung);
            _gesetzt = true;
        }
    }

    /// <summary>
    /// Eine Einstellung als Stichtag: "JJJJ-MM-TT", null fuer alle Daten ("all" oder leer).
    /// Unlesbares gilt als <see cref="Vorgabe"/> -- ein Tippfehler soll nicht stillschweigend
    /// die alten Daten zurueckholen.
    /// </summary>
    public static string? Lesen(string? text)
    {
        var wert = (text ?? string.Empty).Trim();
        if (wert.Length == 0 || wert.Equals(Alle, StringComparison.OrdinalIgnoreCase)
            || wert.Equals("alle", StringComparison.OrdinalIgnoreCase)
            || wert.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return DateTime.TryParseExact(wert, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                      DateTimeStyles.None, out var tag)
            ? tag.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : Vorgabe;
    }

    /// <summary>Liegt <paramref name="tag"/> (Text, beginnt mit JJJJ-MM-TT) am oder nach <paramref name="seit"/>?</summary>
    public static bool Gilt(string? tag, string? seit)
    {
        if (seit is null) { return true; }
        var t = tag ?? string.Empty;
        if (t.Length > 10) { t = t[..10]; }
        return string.CompareOrdinal(t, seit) >= 0;
    }
}
