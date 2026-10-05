using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Die Texte des Spiels, die die Leser auf dem Bildschirm suchen -- in allen 24
/// Sprachen des Spiels (seit 2026-09-29).
/// </summary>
/// <remarks>
/// ## Warum
///
/// Jeder Leser suchte englische Worte: "In Progress", "Joining ...", "My Cars", die
/// englischen Streckennamen. Ein Spieler mit spanischem Spiel sah darum weder die
/// Streckenvorschau noch die Autowahl -- "Descenso del puente Rainbow" ist "Rainbow
/// Bridge Descent", nur wusste die App das nicht.
///
/// ## Woher
///
/// Nicht selbst uebersetzt: aus den Stringtabellen des Spiels, von
/// <c>scripts/extract_game_text.py</c> nach <c>config/game_text.json</c> gezogen. Das
/// sind genau die Texte, die das Spiel zeigt, in jeder Sprache, die es hat.
///
/// ## Ohne die Datei
///
/// Jeder Aufruf nennt das englische Wort mit; fehlt die Datei (Test, altes Paket),
/// gilt nur das -- so wie bisher.
///
/// ## Vergleich
///
/// Alles gefaltet wie <see cref="TextMatch.Normalise"/> (klein, ohne Akzente), dazu das
/// tuerkische "ɨ" des Spiels (in seiner Tabelle steht es fuer "i") und das punktlose
/// "ı" als "i" -- die Texterkennung liest beides mal so, mal so.
/// </remarks>
internal static class GameText
{
    private sealed class Daten
    {
        public Dictionary<string, List<string>> Worte { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<string>> Strecken { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Sprachen { get; } = new();
    }

    private static Daten? _daten;
    private static readonly object Schloss = new();

    /// <summary>Wo die Datei gesucht wird -- sonst die Suche vom Programmordner aufwaerts.</summary>
    internal static string? Pfad { get; set; }

    private static Daten Laden()
    {
        lock (Schloss)
        {
            if (_daten is not null) { return _daten; }
            var d = new Daten();
            try
            {
                var datei = Pfad ?? Kandidaten().FirstOrDefault(File.Exists);
                if (datei is not null && File.Exists(datei))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(datei));
                    var wurzel = doc.RootElement;
                    if (wurzel.TryGetProperty("words", out var worte))
                    {
                        foreach (var w in worte.EnumerateObject())
                        {
                            d.Worte[w.Name] = w.Value.EnumerateArray().Select(x => x.GetString() ?? string.Empty)
                                               .Where(x => x.Length > 0).ToList();
                        }
                    }
                    if (wurzel.TryGetProperty("routes", out var strecken))
                    {
                        foreach (var s in strecken.EnumerateObject())
                        {
                            d.Strecken[s.Name] = s.Value.EnumerateArray().Select(x => x.GetString() ?? string.Empty)
                                                  .Where(x => x.Length > 0).ToList();
                        }
                    }
                    if (wurzel.TryGetProperty("languages", out var sprachen))
                    {
                        d.Sprachen.AddRange(sprachen.EnumerateArray().Select(x => x.GetString() ?? string.Empty));
                    }
                }
            }
            catch (Exception)
            {
                // Kaputt oder fehlend: dann eben nur Englisch, wie vorher.
            }
            _daten = d;
            return d;
        }
    }

    private static IEnumerable<string> Kandidaten()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            yield return Path.Combine(dir.FullName, "config", "game_text.json");
            dir = dir.Parent;
        }
    }

    /// <summary>Fuer Tests: die Datei neu laden (nach dem Setzen von <see cref="Pfad"/>).</summary>
    internal static void Vergessen()
    {
        lock (Schloss) { _daten = null; }
    }

    /// <summary>Wie viele Spielsprachen die Datei kennt (0: keine Datei).</summary>
    internal static int SprachenAnzahl => Laden().Sprachen.Count;

    /// <summary>Gefaltet wie Normalise, dazu das "ɨ"/"ı" des Tuerkischen als "i".</summary>
    internal static string Falte(string? text) =>
        TextMatch.Normalise((text ?? string.Empty).Replace('ɨ', 'i').Replace('Ɨ', 'I').Replace('ı', 'i'));

    /// <summary>Alle Schreibweisen eines Wortes, gefaltet -- das englische immer dabei.</summary>
    internal static IReadOnlyList<string> Varianten(string schluessel, string englisch)
    {
        var roh = Laden().Worte.TryGetValue(schluessel, out var w) ? w : new List<string>();
        return roh.Append(englisch).Select(Falte).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Die rohen Schreibweisen (ungefaltet), fuer Muster mit Platzhaltern.</summary>
    internal static IReadOnlyList<string> Roh(string schluessel, string englisch)
    {
        var roh = Laden().Worte.TryGetValue(schluessel, out var w) ? w : new List<string>();
        return roh.Append(englisch).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Die Namen einer Strecke in den anderen Spielsprachen (roh).</summary>
    internal static IReadOnlyList<string> Streckennamen(string englisch) =>
        Laden().Strecken.TryGetValue(englisch, out var n) ? n : new List<string>();

    /// <summary>Steht das Wort in diesem Text? Als ganzes Wort -- "ja" steckt sonst in "jaguar".</summary>
    internal static bool Enthaelt(string? text, string schluessel, string englisch)
    {
        var t = Falte(text);
        if (t.Length == 0) { return false; }
        var mitRand = " " + t + " ";
        foreach (var v in Varianten(schluessel, englisch))
        {
            // Schriften ohne Leerzeichen zwischen Worten (Chinesisch, Japanisch): einfach enthalten.
            if (OhneWortgrenzen(v) ? t.Contains(v, StringComparison.Ordinal)
                                   : mitRand.Contains(" " + v + " ", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Ist der Text genau dieses Wort (in irgendeiner Sprache)?</summary>
    internal static bool Gleich(string? text, string schluessel, string englisch)
    {
        var t = Falte(text);
        return t.Length > 0 && Varianten(schluessel, englisch).Contains(t);
    }

    /// <summary>
    /// Endet der Text mit dem Wort -- auch verlesen? Die Statusspalte haengt die
    /// Texterkennung gern an den Streckennamen an ("Norikura Descent In Progress").
    /// </summary>
    internal static bool EndetMit(string? text, string schluessel, string englisch, double schwelle = 0.8)
    {
        var t = Falte(text);
        if (t.Length == 0) { return false; }
        var woerter = t.Split(' ');
        foreach (var v in Varianten(schluessel, englisch))
        {
            if (t.EndsWith(v, StringComparison.Ordinal)) { return true; }
            var n = v.Split(' ').Length;
            if (woerter.Length < n || OhneWortgrenzen(v)) { continue; }
            var schluss = string.Join(' ', woerter[^n..]);
            if (schluss.Length >= 4 && TextMatch.Similarity(schluss, v) >= schwelle) { return true; }
        }
        return false;
    }

    /// <summary>Beginnt der Text wie dieser Satz (in irgendeiner Sprache)? Fuer umbrochene Fragen.</summary>
    internal static bool BeginntWie(string? text, string schluessel, string englisch)
    {
        var t = Falte(text);
        if (t.Length < 6) { return false; }
        foreach (var v in Varianten(schluessel, englisch))
        {
            var anfang = v[..Math.Min(v.Length, 14)];
            if (t.StartsWith(anfang, StringComparison.Ordinal) || (v.StartsWith(t, StringComparison.Ordinal) && t.Length >= 10))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Aehnelt der ganze Text einer Schreibweise (verlesen)?</summary>
    internal static bool Aehnlich(string? text, string schluessel, string englisch, double schwelle = 0.8)
    {
        var t = Falte(text);
        return t.Length > 0 && Varianten(schluessel, englisch).Any(v => v == t || TextMatch.Similarity(t, v) >= schwelle);
    }

    private static bool OhneWortgrenzen(string v) => v.Any(c => c >= 0x2E80);

    /// <summary>
    /// Die Kopfzeile einer Reihe als Muster, je Sprache: "Joining {0} {1}/{2}" wird zu
    /// (?&lt;name&gt;...) und (?&lt;k&gt;)/(?&lt;n&gt;). Der Schraegstrich darf verlesen sein wie bisher.
    /// </summary>
    internal static IReadOnlyList<Regex> ReihenMuster()
    {
        lock (Schloss)
        {
            if (_reihen is not null) { return _reihen; }
            var raus = new List<Regex>();
            foreach (var v in Roh("joining", "Joining {0} {1}/{2}"))
            {
                if (!v.Contains("{0}") || !v.Contains("{1}") || !v.Contains("{2}")) { continue; }
                var muster = Regex.Escape(v.Replace('ɨ', 'i').Replace('Ɨ', 'I'))
                    .Replace(@"\ ", @"\s+")
                    .Replace(@"\{0}", @"(?<name>.+?)")
                    .Replace(@"\{1}/\{2}", @"(?<k>[1-9])\s*[/1Il|\\]\s*(?<n>[1-9])(?!\d)")
                    .Replace(@"\{1}", @"(?<k>[1-9])")
                    .Replace(@"\{2}", @"(?<n>[1-9])(?!\d)");
                try { raus.Add(new Regex(muster, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)); }
                catch (ArgumentException) { }
            }
            _reihen = raus;
            return raus;
        }
    }

    private static IReadOnlyList<Regex>? _reihen;
}
