using System.Text.Json;

namespace ForzaHaptics.Tester.Tuning;

/// <summary>
/// Aus einer Teilenummer einen Namen machen -- so weit es belegt ist.
/// </summary>
/// <remarks>
/// ## Woher die Namen kommen
///
/// Aus dem Speicher des Spiels, herausgeholt von
/// `scripts/extract_part_names.py` nach `config/fh6_part_names.json`. Der
/// Teilekatalog als Datenbank ist dort NICHT zu bekommen -- am 2026-09-14 geprueft,
/// kein einziges SQLite-Abbild traegt eine `List_Upgrade*`-Tabelle. Die Namen selbst
/// liegen aber als null-getrennter Zeichenketten-Vorrat da, 32 Teilearten mit je
/// vier Stufen und 42 Sonderteile.
///
/// ## Was belegt ist
///
/// Die letzten drei Stellen der Teilenummer sind die Ausbaustufe, und **0 bis 3 sind
/// Stock, Street, Sport und Race**. Gemessen an 587 Autos: bei Kupplung, Bremsen,
/// Nockenwelle, Auspuff, Ansaugung, Schwungrad, Ladeluftkuehler, Stabilisator und
/// Antriebsstrang kommen genau die Stufen 0..3 vor -- und der Vorrat enthaelt zu
/// jeder dieser Teilearten genau die vier Namen. Vier Stufen, vier Namen, keine
/// Luecke.
///
/// ## Die Stufen ab 4: die Zahl im Namen ist die Stufe
///
/// Im Speicher steht die Zuordnung nicht -- eine gepruefte Sackgasse: die Namen
/// liegen in einem Zeiger-Array, und neben keiner Teilenummer steht ein Zeiger
/// darauf. Der Nutzer hat sie am 2026-09-14 beigesteuert: "genau die gangzahlen
/// stehen im namen drin". Ein "Race Transmission: 7 Speed" sitzt also auf Stufe 7.
///
/// Damit schliesst sich beim Getriebe sogar die Rechnung: sieben Sondernamen fuer
/// die Stufen 4 bis 10, sechs davon mit eigener Zahl, und der eine ohne ("Rally
/// Transmission") kann nur auf der einen freien Stufe sitzen.
///
/// Wo das nicht aufgeht -- beim Differential stehen drei Sondernamen ohne Zahl drei
/// Stufen gegenueber -- wird KEIN Name behauptet, sondern die Kandidaten genannt.
/// Ein erfundener Name waere schlimmer als eine Nummer: er saehe aus wie eine
/// Auskunft.
/// </remarks>
internal static class PartNames
{
    private sealed class Vorrat
    {
        public List<string> Tiers { get; set; } = new();
        public Dictionary<string, List<string>> ByPart { get; set; } = new();
        public List<string> Other { get; set; } = new();
    }

    /// <summary>
    /// Wie die Spalte der Garage im Namensvorrat heisst.
    /// </summary>
    /// <remarks>
    /// Die Garage nennt ihre Spalten anders als das Spiel seine Teile. Wo kein
    /// Eintrag steht, gibt es im Vorrat keine passende Reihe -- dann bleibt es bei
    /// der Stufe, statt eine Aehnlichkeit zu erfinden.
    /// </remarks>
    private static readonly Dictionary<string, string> Rumpf = new()
    {
        ["Engine block"] = "Engine Block",
        ["Camshaft"] = "Cams and Valves",
        ["Valves"] = "Valves",
        ["Pistons & compression"] = "Pistons / Compression",
        ["Fuel system"] = "Fuel System",
        ["Ignition"] = "Ignition",
        ["Exhaust"] = "Exhaust",
        ["Intake"] = "Intake",
        ["Flywheel"] = "Flywheel",
        ["Manifold"] = "Intake Manifold / Throttle Body",
        ["Oil & cooling"] = "Oil / Cooling",
        ["Single turbo"] = "Turbo",
        ["Twin turbo"] = "Twin Turbo",
        ["Supercharger (centrifugal)"] = "Centrifugal Supercharger",
        ["Supercharger (positive)"] = "Positive Displacement Supercharger",
        ["Intercooler"] = "Intercooler",
        ["Electric motor"] = "Motor and Battery Parts",
        ["Motor parts"] = "Motor and Battery Parts",
        ["Clutch"] = "Clutch",
        ["Transmission"] = "Transmission",
        ["Driveline"] = "Driveline",
        ["Differential"] = "Diff",
        ["Brakes"] = "Brakes",
        ["Springs & dampers"] = "Spring and Dampers",
        ["Anti-roll bar, front"] = "Front Anti-roll Bars",
        ["Anti-roll bar, rear"] = "Rear Anti-roll Bars",
        ["Chassis reinforcement"] = "Chassis Reinforcement / Roll Cage",
        ["Weight reduction"] = "Weight Reduction",
        ["Front bumper"] = "Front Bumper",
        ["Rear bumper"] = "Rear Bumper",
        ["Rear wing"] = "Rear Wing",
    };

    private static Vorrat? _vorrat;
    private static bool _versucht;

    private static Vorrat? Laden()
    {
        if (_versucht) { return _vorrat; }
        _versucht = true;
        foreach (var pfad in Kandidaten())
        {
            try
            {
                if (!File.Exists(pfad)) { continue; }
                _vorrat = JsonSerializer.Deserialize<Vorrat>(
                    File.ReadAllText(pfad),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (_vorrat is not null && _vorrat.ByPart.Count > 0) { return _vorrat; }
            }
            catch (Exception)
            {
                // Ohne Namensvorrat bleibt die Nummer stehen -- unschoen, aber kein
                // Grund, den Reiter scheitern zu lassen.
            }
        }
        return _vorrat;
    }

    private static IEnumerable<string> Kandidaten()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            yield return Path.Combine(dir.FullName, "config", "fh6_part_names.json");
            dir = dir.Parent;
        }
    }

    /// <summary>Wie viele Namen es zu dieser Teileart ueberhaupt gibt.</summary>
    public static IReadOnlyList<string> Sondernamen(string label)
    {
        var v = Laden();
        if (v is null || !Rumpf.TryGetValue(label, out var rumpf)) { return []; }
        // Ein Sondername gehoert zur Teileart, wenn er ihren Rumpf enthaelt --
        // "Rally Transmission" zu "Transmission", "Drift Diff" zu "Diff".
        var kern = rumpf.Split('/')[0].Trim();
        return v.Other.Where(n => n.Contains(kern, StringComparison.OrdinalIgnoreCase))
                .ToList();
    }

    /// <summary>
    /// Der Name des Teils -- oder null, wenn er nicht belegt ist.
    /// </summary>
    /// <remarks>
    /// ## Stufe 0 bis 3
    ///
    /// Stock, Street, Sport, Race. Belegt an 587 Autos (siehe oben).
    ///
    /// ## Stufe 4 und hoeher: DIE ZAHL IM NAMEN IST DIE STUFE
    ///
    /// Vom Nutzer am 2026-09-14 bestaetigt: "genau die gangzahlen stehen im namen
    /// drin". Ein "Race Transmission: 7 Speed" sitzt also auf Stufe 7, ein
    /// "Drift Transmission: 4 Speed" auf Stufe 4. Das ist eine mechanische Regel und
    /// braucht kein Raten.
    ///
    /// ## Und was danach uebrig bleibt, ist manchmal erzwungen
    ///
    /// Beim Getriebe gibt es sieben Sondernamen fuer die Stufen 4 bis 10. Sechs
    /// davon tragen ihre Zahl (4, 6, 7, 8, 9, 10). Bleibt genau ein Name ohne Zahl
    /// ("Rally Transmission") und genau eine freie Stufe (5) -- dann ist die
    /// Zuordnung nicht geraten, sondern die einzig moegliche.
    ///
    /// Wo das nicht aufgeht -- beim Differential etwa, wo drei Sondernamen ohne Zahl
    /// drei Stufen gegenueberstehen -- wird KEIN Name behauptet. Der Aufrufer zeigt
    /// dann die Kandidaten.
    /// </remarks>
    public static string? Name(string label, int step)
    {
        var v = Laden();
        if (v is null || step < 0) { return null; }
        if (!Rumpf.TryGetValue(label, out var rumpf)) { return null; }
        if (!v.ByPart.TryGetValue(rumpf, out var reihe)) { return null; }
        if (step < 4) { return step < reihe.Count ? reihe[step] : null; }

        var sonder = Sondernamen(label);
        if (sonder.Count == 0) { return null; }

        // 1) Traegt ein Name genau diese Zahl?
        var mitZahl = new Dictionary<int, string>();
        var ohneZahl = new List<string>();
        foreach (var n in sonder)
        {
            var zahl = Zahl(n);
            if (zahl is { } z && z >= 4) { mitZahl[z] = n; }
            else { ohneZahl.Add(n); }
        }
        if (mitZahl.TryGetValue(step, out var treffer)) { return treffer; }

        // 2) Bleibt genau einer uebrig, und genau eine Luecke -- dann ist er es.
        if (ohneZahl.Count != 1) { return null; }
        var hoechste = mitZahl.Count > 0 ? mitZahl.Keys.Max() : 4;
        var frei = new List<int>();
        for (var s = 4; s <= hoechste; s++)
        {
            if (!mitZahl.ContainsKey(s)) { frei.Add(s); }
        }
        return frei.Count == 1 && frei[0] == step ? ohneZahl[0] : null;
    }

    /// <summary>Nur fuer die Fehlersuche: was der Nachschlageweg sieht.</summary>
    public static string Probe(string label, int step)
    {
        var v = Laden();
        if (v is null) { return "kein Vorrat geladen"; }
        if (!Rumpf.TryGetValue(label, out var rumpf)) { return "kein Rumpf zu " + label; }
        if (!v.ByPart.TryGetValue(rumpf, out var reihe))
        {
            return $"Rumpf '{rumpf}' nicht in byPart ({v.ByPart.Count} Schluessel: "
                   + string.Join(", ", v.ByPart.Keys.Take(5)) + ")";
        }
        var sonder = Sondernamen(label);
        var zahlen = string.Join(", ",
            sonder.Select(n => $"{n} => {(Zahl(n) is { } z ? z.ToString() : "-")}"));
        return $"Rumpf '{rumpf}', Reihe {reihe.Count}, Sonder {sonder.Count}: {zahlen}";
    }

    /// <summary>Die erste ganze Zahl in einem Namen, oder null.</summary>
    private static int? Zahl(string name)
    {
        // VON HAND STATT MIT EINEM MUSTER. Der regulaere Ausdruck lieferte fuer
        // jeden einzelnen Namen nichts -- sichtbar erst, als die Probe die
        // Zwischenschritte ausgab. Ziffern zu zaehlen braucht kein Muster, und was
        // kein Muster ist, kann auch keines verschlucken.
        var zahl = 0;
        var gesehen = false;
        foreach (var c in name)
        {
            if (c >= '0' && c <= '9')
            {
                zahl = zahl * 10 + (c - '0');
                gesehen = true;
            }
            else if (gesehen)
            {
                break;
            }
        }
        return gesehen ? zahl : null;
    }
}
