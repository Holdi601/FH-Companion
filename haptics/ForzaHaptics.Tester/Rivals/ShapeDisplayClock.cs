namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Wie lange die Anmeldekarten stehen -- je Angebot, nicht je Erkennung.
/// </summary>
/// <remarks>
/// Seit 2026-09-26 (Nutzerwunsch: "nach 30-60 s weg, einstellbar wie beim
/// Autovorschlag"). Die Falle, um die es hier geht: der Leser erkennt den
/// Anmeldeschirm alle anderthalb Sekunden neu. Liefe die Zeit ab jeder Erkennung,
/// verschwaenden die Karten nie. Darum laeuft sie ab dem ersten Zeigen EINES
/// Angebots, und ist sie um, bleibt dieses Angebot dunkel -- bis ein neues kommt oder
/// ein Rennen der Meisterschaft endet (dann gibt es Neues zu sehen: den Stand).
/// </remarks>
internal sealed class ShapeDisplayClock
{
    private DateTime _bis = DateTime.MaxValue;

    /// <summary>Fuer dieses Angebot ist die Zeit schon um.</summary>
    public bool Expired { get; private set; }

    /// <summary>Laeuft gerade eine Frist?</summary>
    public bool Running => _bis != DateTime.MaxValue;

    /// <summary>Ein anderes Angebot als zuletzt: alles von vorn.</summary>
    public void NewOffer()
    {
        Expired = false;
        _bis = DateTime.MaxValue;
    }

    /// <summary>Ein Meisterschaftsrennen ist vorbei: frische Anzeige mit eigener Zeit.</summary>
    public void RaceEnded() => NewOffer();

    /// <summary>Das Rennen beginnt: die Karten sind ohnehin weg, die Frist ruht.</summary>
    public void RaceStarted() => _bis = DateTime.MaxValue;

    /// <summary>Die Karten stehen jetzt da. Die Frist beginnt, falls noch keine laeuft.</summary>
    /// <param name="sekunden">0 oder weniger: keine Frist, bis zum Rennstart.</param>
    public void Shown(DateTime jetzt, double sekunden)
    {
        if (Running) { return; }
        _bis = sekunden > 0 ? jetzt.AddSeconds(sekunden) : DateTime.MaxValue;
    }

    /// <summary>Ist die Frist jetzt um? Dann einmal true -- und das Angebot bleibt dunkel.</summary>
    public bool Due(DateTime jetzt)
    {
        if (!Running || jetzt < _bis) { return false; }
        _bis = DateTime.MaxValue;
        Expired = true;
        return true;
    }
}
