namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Die eigene Bestenliste: war diese Runde ein persoenlicher Rekord? (seit 2026-09-28)
/// </summary>
/// <remarks>
/// ## Wozu
///
/// Auf Wunsch des Nutzers, "to gamify the players trying to reach faster times": kleine
/// Feiern fuer den eigenen Fortschritt, nicht nur fuer die Website. Vier Anlaesse, jeder
/// fuer sich abschaltbar (OverlaySettings, pb_*):
///
///   * Klassenrekord   -- schneller als jede eigene Runde in dieser Klasse auf diesem Kurs
///   * Autorekord      -- schneller als jede eigene Runde mit diesem Auto (Klasse, Kurs)
///   * Neues Auto      -- das Auto steht zum ersten Mal auf der eigenen Liste des Kurses
///   * Erste in Klasse -- die allererste eigene Runde in dieser Klasse auf diesem Kurs
///
/// Je Runde hoechstens EIN Anlass, der staerkste: Klassenrekord vor neuem Auto vor
/// Autorekord. Die erste Runde in einer Klasse ist nur das -- ein Rekord gegen nichts
/// ist keiner.
///
/// ## Was verglichen wird
///
/// Derselbe Kurs, dieselbe Klasse, derselbe Start (stehend gegen fliegend ist immer
/// falsch, siehe lap-identity-from-start-position) und Sprint mit Sprint. Auf Wunsch
/// (pb_per_mode, ab Werk an) auch derselbe Modus: eine Wandfahrt im Solo-Rennen soll
/// keinen Rivals-Rekord schlagen. Runden ohne bekannten Modus -- alles vor dem
/// 2026-09-28 -- zaehlen dabei fuer JEDEN Modus mit. Sonst waere direkt nach dem Update
/// jede erste Rivals-Runde ein "Rekord".
/// </remarks>
internal static class PersonalRecords
{
    internal enum Art { Keine, ErsteInKlasse, KlassenRekord, NeuesAuto, AutoRekord }

    /// <param name="VorherSekunden">Die geschlagene Zeit (Klasse oder Auto), sonst null.</param>
    /// <param name="Platz">Der Platz des Autos auf der eigenen Liste danach (1 = schnellstes).</param>
    /// <param name="Autos">Wie viele Autos danach auf der eigenen Liste stehen.</param>
    internal sealed record Ergebnis(Art Art, double? VorherSekunden, int Platz, int Autos);

    internal static bool Vergleichbar(OwnTimes.Lap a, OwnTimes.Lap neu, bool jeModus) =>
        a.Course == neu.Course && a.Klass == neu.Klass && a.Standing == neu.Standing && a.Sprint == neu.Sprint
        && !string.Equals(a.Path, neu.Path, StringComparison.OrdinalIgnoreCase)
        && (!jeModus || a.Mode == neu.Mode || a.Mode == "unknown" || neu.Mode == "unknown");

    /// <summary>Was die neue Runde gegen die bisherigen eigenen ist.</summary>
    internal static Ergebnis Werte(IEnumerable<OwnTimes.Lap> bisher, OwnTimes.Lap neu, bool jeModus)
    {
        var gruppe = bisher.Where(a => Vergleichbar(a, neu, jeModus)).ToList();
        var besteJeAuto = gruppe.GroupBy(a => a.Ordinal).ToDictionary(g => g.Key, g => g.Min(x => x.Seconds));
        var hatteAuto = besteJeAuto.TryGetValue(neu.Ordinal, out var autoVorher);
        var nachher = new Dictionary<int, double>(besteJeAuto)
        {
            [neu.Ordinal] = hatteAuto ? Math.Min(autoVorher, neu.Seconds) : neu.Seconds,
        };
        var eigene = nachher[neu.Ordinal];
        var platz = 1 + nachher.Count(kv => kv.Key != neu.Ordinal && kv.Value < eigene);
        var autos = nachher.Count;

        if (gruppe.Count == 0) { return new Ergebnis(Art.ErsteInKlasse, null, 1, 1); }
        var klasseVorher = gruppe.Min(x => x.Seconds);
        if (neu.Seconds < klasseVorher) { return new Ergebnis(Art.KlassenRekord, klasseVorher, platz, autos); }
        if (!hatteAuto) { return new Ergebnis(Art.NeuesAuto, null, platz, autos); }
        if (neu.Seconds < autoVorher) { return new Ergebnis(Art.AutoRekord, autoVorher, platz, autos); }
        return new Ergebnis(Art.Keine, null, platz, autos);
    }

    /// <summary>Ist dieser Anlass eingeschaltet?</summary>
    internal static bool Gewollt(Art art, OverlaySettings s) => art switch
    {
        Art.KlassenRekord => s.PbClassRecord,
        Art.AutoRekord => s.PbCarRecord,
        Art.NeuesAuto => s.PbNewCar,
        Art.ErsteInKlasse => s.PbFirstInClass,
        _ => false,
    };
}
