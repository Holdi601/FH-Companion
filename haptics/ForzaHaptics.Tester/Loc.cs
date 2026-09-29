using System.Globalization;
using System.Text.Json;

namespace ForzaHaptics.Tester;

/// <summary>
/// Die Oberflaeche in der Sprache des Systems -- Englisch, wo es keine gibt.
/// </summary>
/// <remarks>
/// ## DER ENGLISCHE SATZ IST DER SCHLUESSEL
///
/// Kein `Strings.Tab_Rivals_Title`, sondern <c>Loc.T("Always on Top")</c>. Das hat
/// drei Folgen, und alle drei sind der Grund fuer diese Wahl:
///
/// 1. **Eine fehlende Uebersetzung faellt auf Englisch zurueck, ohne dass irgendwo
///    eine englische Datei gepflegt werden muesste.** Der Rueckfall IST der
///    Quelltext. Ein Schluesselsystem braucht dafuer eine `en.json`, die mit dem
///    Code auseinanderlaufen kann -- und dann steht `Tab_Rivals_Title` auf dem
///    Schirm.
/// 2. **Der Quelltext bleibt lesbar.** Wer `Loc.T("Stop now")` liest, weiss, was
///    auf dem Knopf steht.
/// 3. Preis: aendert sich der englische Satz, verwaisen die Uebersetzungen dieses
///    Satzes. Das ist sichtbar (`--lang-report` zeigt es) und billiger als die
///    Alternative.
///
/// ## Welche Sprache
///
/// `CultureInfo.CurrentUICulture` -- also die Anzeigesprache von Windows, nicht das
/// Tastaturlayout und nicht das Zahlenformat. Wer in den Einstellungen
/// <c>"language"</c> setzt, ueberstimmt das; <c>"auto"</c> ist die Vorgabe.
///
/// Gesucht wird zuerst die volle Kennung (<c>pt-BR</c>), dann die blosse Sprache
/// (<c>pt</c>). So kann brasilianisches Portugiesisch eine eigene Datei bekommen,
/// muss aber nicht.
///
/// ## Was NICHT uebersetzt wird
///
/// Fehlermeldungen fuer Fehlersuche, Protokollzeilen und die Ausgabe der kopflosen
/// Befehle. Die liest jemand, der hilft -- und der braucht den Wortlaut, nach dem
/// sich suchen laesst. Eine uebersetzte Fehlermeldung ist eine unauffindbare.
/// </remarks>
internal static class Loc
{
    private static Dictionary<string, string>? _tabelle;
    private static string _sprache = "en";
    private static bool _geladen;

    /// <summary>Die Sprache, die gerade gilt (<c>en</c>, <c>de</c>, ...).</summary>
    public static string Sprache
    {
        get { Sicherstellen(); return _sprache; }
    }

    /// <summary>Wie viele Saetze in dieser Sprache vorliegen.</summary>
    public static int Bekannt
    {
        get { Sicherstellen(); return _tabelle?.Count ?? 0; }
    }

    /// <summary>Den Satz in der geltenden Sprache -- oder den englischen.</summary>
    public static string T(string englisch)
    {
        Sicherstellen();
        if (_tabelle is not null
            && _tabelle.TryGetValue(englisch, out var wert)
            && !string.IsNullOrWhiteSpace(wert))
        {
            return wert;
        }
        return englisch;
    }

    /// <summary>Eine Sprache erzwingen. <c>null</c> oder "auto" heisst: das System.</summary>
    public static void Waehle(string? code)
    {
        _geladen = false;
        _tabelle = null;
        _erzwungen = string.IsNullOrWhiteSpace(code) || code == "auto" ? null : code;
        Sicherstellen();
    }

    private static string? _erzwungen;

    private static void Sicherstellen()
    {
        if (_geladen) { return; }
        _geladen = true;
        _sprache = "en";
        _tabelle = null;

        foreach (var kandidat in Kandidaten())
        {
            var datei = Datei(kandidat);
            if (datei is null) { continue; }
            try
            {
                var roh = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(datei));
                if (roh is { Count: > 0 })
                {
                    _tabelle = roh;
                    _sprache = kandidat;
                    return;
                }
            }
            catch (Exception)
            {
                // Eine kaputte Sprachdatei darf das Programm nicht aufhalten.
                // Englisch ist immer da: es steht im Quelltext.
            }
        }
    }

    /// <summary>Welche Kennungen in Frage kommen, von genau nach grob.</summary>
    private static IEnumerable<string> Kandidaten()
    {
        var namen = new List<string>();
        try
        {
            // AUCH BEI ERZWUNGENER SPRACHE DIE ELTERNKETTE ABLAUFEN.
            //
            // Vorher wurde bei einer erzwungenen Kennung nur diese eine genommen --
            // ein erzwungenes zh-TW fand kein zh-TW.json, fiel auf die Kurzform "zh"
            // zurueck und lieferte VEREINFACHTES Chinesisch an jemanden, der
            // traditionelles wollte. Ueber die Kultur kommt dazwischen zh-Hant.
            var kultur = _erzwungen is null
                ? CultureInfo.CurrentUICulture
                : CultureInfo.GetCultureInfo(_erzwungen);
            while (kultur is not null && kultur.Name.Length > 0)
            {
                namen.Add(kultur.Name);
                kultur = kultur.Parent;
            }
        }
        catch (CultureNotFoundException)
        {
            // Eine Kennung, die Windows nicht kennt, kann trotzdem eine Datei
            // danebenliegen haben -- dann eben ohne Elternkette.
            if (_erzwungen is not null) { namen.Add(_erzwungen); }
        }
        catch (Exception)
        {
            // Ohne Kultur bleibt es bei Englisch.
        }

        // NORWEGISCH (seit 2026-09-29): es gibt nur nb.json. nn-NO endet bei "nn",
        // no-NO bei "no" -- keine der Ketten erreicht "nb", und Nynorsk bekaeme Englisch.
        if (namen.Any(n => n.Split('-')[0] is "no" or "nn") && !namen.Contains("nb"))
        {
            namen.Add("nb");
        }

        // ERST ALLE VOLLEN KENNUNGEN, DANN DIE KURZFORMEN.
        //
        // Nicht je Eintrag "voll, dann kurz": fuer zh-TW waere die Reihenfolge dann
        // zh-TW, zh, zh-Hant -- und ein Taiwaner bekaeme VEREINFACHTES Chinesisch,
        // weil zh.json vor zh-Hant.json geprueft wird. Die Elternkette liefert
        // bereits die richtige Reihenfolge (zh-TW, zh-Hant, zh); sie darf nur nicht
        // von den Kurzformen durchbrochen werden.
        foreach (var n in namen)
        {
            yield return n;                       // zh-TW, zh-Hant, zh
        }
        foreach (var n in namen)
        {
            var kurz = n.Split('-')[0];
            if (kurz != n) { yield return kurz; } // pt-BR -> pt
        }
    }

    /// <summary>Wo eine Sprachdatei liegen koennte.</summary>
    /// <remarks>
    /// Vom Programmordner aufwaerts, wie <see cref="Tuning.PartNames"/> es fuer die
    /// Teilenamen tut: im Paket liegt <c>config/</c> neben <c>app/</c>, beim Bauen
    /// aber viele Ebenen ueber <c>bin/Release/...</c>.
    /// </remarks>
    private static string? Datei(string code)
    {
        if (code.Length == 0 || code.Contains('.') || code.Contains(Path.DirectorySeparatorChar)
            || code.Contains(Path.AltDirectorySeparatorChar))
        {
            // Ein Kulturname mit Pfadtrennern kann nicht sein -- und darf darum
            // erst recht nicht in einen Dateipfad wandern.
            return null;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var pfad = Path.Combine(dir.FullName, "config", "lang", code + ".json");
            if (File.Exists(pfad)) { return pfad; }
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>Welche Sprachen ueberhaupt danebenliegen -- fuer die Auswahl.</summary>
    public static IReadOnlyList<string> Verfuegbar()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var ordner = Path.Combine(dir.FullName, "config", "lang");
            if (Directory.Exists(ordner))
            {
                return Directory.GetFiles(ordner, "*.json")
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(n => !string.IsNullOrEmpty(n))
                    // `_keys.json` ist die Liste der englischen Saetze, keine
                    // Sprache. Alles mit Unterstrich davor ist Werkzeug.
                    .Where(n => !n!.StartsWith('_'))
                    .Select(n => n!)
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToList();
            }
            dir = dir.Parent;
        }
        return [];
    }
}
