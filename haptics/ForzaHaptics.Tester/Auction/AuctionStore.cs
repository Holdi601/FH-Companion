using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForzaHaptics.Tester.Auction;

/// <summary>Eine Auktion, auf die der Nutzer geboten hat (aus "My Bids").</summary>
internal sealed class VerfolgteAuktion
{
    /// <summary>Kennung: Auto + PI + Ende auf zehn Minuten gerundet (bei Neuanlage).</summary>
    public string Id { get; set; } = string.Empty;
    public string Auto { get; set; } = string.Empty;
    public int? Pi { get; set; }
    /// <summary>OUTBID, WINNING, SOLD, WON, LOST, ENDED -- oder leer.</summary>
    public string? Status { get; set; }
    public long? Gebot { get; set; }
    public long? Sofortkauf { get; set; }
    /// <summary>Geschaetztes Ende (UTC), aus der Restzeit beim letzten Blick.</summary>
    public DateTime? EndeUtc { get; set; }
    /// <summary>
    /// So viele Minuten kann das wahre Ende nach <see cref="EndeUtc"/> liegen: 60 nach einer Lesung nur in
    /// Stunden ("2 hours" = 2:00 bis 2:59), 15 nach einer mit Minuten (Spielraum fuer eine verlesene Ziffer),
    /// 0 nach "Ending Soon" (jetzt + 5 ist schon die Obergrenze). Alte Eintraege ohne Angabe: 60.
    /// </summary>
    public int EndeSpielraum { get; set; } = 60;
    public DateTime ZuletztGesehenUtc { get; set; }
    public bool Gewarnt5 { get; set; }
    public bool Gewarnt2 { get; set; }
    /// <summary>Ende: WON, LOST, SOLD; mit Endpreis.</summary>
    public string? Ergebnis { get; set; }
    public long? Endpreis { get; set; }

    [JsonIgnore] public bool Laeuft => Ergebnis is null && Status is not ("SOLD" or "NOT_SOLD" or "WON" or "LOST" or "ENDED");

    /// <summary>
    /// Vorbei, aber wie sie ausging, hat niemand gesehen: <see cref="AuctionStore.Verfallen"/> hat sie eine
    /// Stunde nach dem geschaetzten Ende beendet, waehrend das Spiel zu war. Der naechste Blick auf "My Bids"
    /// traegt das Ergebnis nach.
    /// </summary>
    [JsonIgnore] public bool OhneAusgang => !Laeuft && Ergebnis is null;

    [JsonIgnore] public TimeSpan? Rest => EndeUtc is { } e ? e - DateTime.UtcNow : null;
}

/// <summary>Ein gesehener Preis (Suchergebnis oder My Bids) -- die Preisgeschichte je Auto.</summary>
internal sealed record Preis(DateTime ZeitUtc, string Auto, int? Pi, long? Gebot, long? Sofortkauf, int? RestMinuten, string Quelle, string? Ergebnis = null);

/// <summary>
/// Was die App ueber Auktionen weiss -- lokal, nie gesendet (%LOCALAPPDATA%\FHCompanion\auctions.json).
/// </summary>
/// <remarks>
/// Gefuellt wird es aus dem, was die App auf dem Schirm liest: jedes Bild von "My Bids" aktualisiert
/// die verfolgten Auktionen (Status, Gebot, geschaetztes Ende), jedes Suchergebnis die
/// Preisgeschichte. Das Ende ist eine Schaetzung aus der Restzeit -- das Spiel zeigt "2 hours",
/// "1 hour 59 mins" und unter fuenf Minuten nur "Ending Soon". Eine genauere Lesung (weniger Rest)
/// ersetzt eine groebere.
/// </remarks>
internal sealed class AuctionStore
{
    public List<VerfolgteAuktion> Auktionen { get; set; } = new();
    public List<Preis> Preise { get; set; } = new();
    /// <summary>Warnungen 5 und 2 Minuten vor Ende.</summary>
    public bool Warnen { get; set; } = true;

    private static readonly object Schloss = new();
    private static readonly JsonSerializerOptions Optionen = new() { WriteIndented = true };

    internal static string Datei => Path.Combine(AppInfo.DataFolder, "auctions.json");

    public static AuctionStore Laden(string? datei = null)
    {
        lock (Schloss)
        {
            try
            {
                var p = datei ?? Datei;
                if (File.Exists(p)) { return JsonSerializer.Deserialize<AuctionStore>(File.ReadAllText(p)) ?? new AuctionStore(); }
            }
            catch (Exception) { }
            return new AuctionStore();
        }
    }

    public void Speichern(string? datei = null)
    {
        lock (Schloss)
        {
            try
            {
                var p = datei ?? Datei;
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                var tmp = p + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, Optionen));
                File.Move(tmp, p, overwrite: true);
            }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// Gleiche Auktion? Auto und PI gleich, der Sofortkaufpreis gleich (er steht je Auktion fest), und das
    /// Ende passt -- wo etwas unbekannt ist, entscheidet es nicht.
    /// </summary>
    private static bool Gleich(VerfolgteAuktion a, AuktionsKarte k, DateTime? ende)
    {
        if (!string.Equals(Scan.ScanScreen.Falte(a.Auto), Scan.ScanScreen.Falte(k.Auto), StringComparison.Ordinal)) { return false; }
        if (a.Pi is { } p && k.Pi is { } q && p != q) { return false; }
        if (a.Sofortkauf is { } s && k.Sofortkauf is { } t && s != t) { return false; }
        if (a.EndeUtc is { } e && ende is { } f)
        {
            if (Math.Abs((e - f).TotalMinutes) > 75) { return false; }
            // SPAETER ALS MOEGLICH: die Restzeit des Spiels ist nie zu kurz, das wahre Ende eines Eintrags liegt
            // hoechstens um seinen Spielraum nach der Schaetzung; die Karte schaetzt "Ending Soon" als jetzt + 5.
            // Liegt ihr Ende noch spaeter, ist es eine andere Auktion desselben Autos -- sonst landete sie im
            // alten Eintrag (der nach seinem Ende noch eine Stunde "laeuft", bis er verfaellt), und Status,
            // Gebot und Warnungen der neuen Auktion stuenden bei der alten.
            if (f > e.AddMinutes(a.EndeSpielraum + 6)) { return false; }
        }
        return true;
    }

    private static DateTime? EndeAus(AuktionsKarte k, DateTime jetztUtc) =>
        k.RestMinuten is { } m ? jetztUtc.AddMinutes(k.BaldZuEnde ? 5 : m) : null;

    /// <summary>Wie weit das wahre Ende nach der Schaetzung aus dieser Karte liegen kann (<see cref="VerfolgteAuktion.EndeSpielraum"/>).</summary>
    private static int Spielraum(AuktionsKarte k) =>
        k.BaldZuEnde ? 0 : k.RestMinuten is { } m && m >= 60 && m % 60 == 0 ? 60 : 15;

    /// <summary>Eine Karte, deren Auktion vorbei ist (verblasst, mit Aufkleber oder Abzeichen).</summary>
    internal static bool IstEnde(string? status) => status is "SOLD" or "NOT_SOLD" or "WON" or "LOST" or "ENDED";

    /// <summary>
    /// Die Karten von "My Bids" einarbeiten. Gibt die Auktionen zurueck, die sich geaendert haben.
    /// </summary>
    /// <remarks>
    /// ERST DIE LAUFENDEN KARTEN, dann die beendeten: jede laufende Karte belegt ihren Eintrag, bevor eine
    /// beendete Karte desselben Autos ihn als "vorzeitig beendet" nehmen koennte.
    /// <para>
    /// Eine LAUFENDE Karte gehoert nur zu einem laufenden Eintrag, dem mit dem naechstliegenden Ende -- nie zu
    /// einem beendeten (sie bekaeme dessen Ergebnis) und nie nach der Reihenfolge der Liste.
    /// </para>
    /// <para>
    /// Eine BEENDETE Karte zeigt My Bids bei jedem Blick wieder; ihr Zeitfeld sagt nichts mehr. Sie gehoert,
    /// in dieser Reihenfolge: zu dem Eintrag, der schon ein Ergebnis traegt (gleich, ob die Texterkennung den
    /// Aufkleber diesmal SOLD, ENDED oder WON liest -- der Endpreis steht fest); zu einem laufenden, dessen Ende
    /// da ist; zu einem verfallenen ohne gesehenen Ausgang (2026-10-04: die Challenger endete um 06:24 bei
    /// geschlossenem Spiel); erst zuletzt zu einem laufenden mit fernem Ende (Sofortkauf) -- und davon nur, wenn
    /// es genau einer ist. Sonst beendete sie die falsche laufende Auktion, und deren Warnungen fielen weg.
    /// </para>
    /// </remarks>
    public List<VerfolgteAuktion> MeineGebote(IReadOnlyList<AuktionsKarte> karten, DateTime jetztUtc)
    {
        var geaendert = new List<VerfolgteAuktion>();
        Verfallen(jetztUtc);
        // Zwei Karten EINES Blicks sind zwei Auktionen -- nie beide in denselben Eintrag (sonst wechselten
        // Status und Gebot von Lesung zu Lesung).
        var belegt = new HashSet<VerfolgteAuktion>();
        foreach (var k in karten.Where(k => !IstEnde(k.Status)).Concat(karten.Where(k => IstEnde(k.Status))))
        {
            if (string.IsNullOrWhiteSpace(k.Auto)) { continue; }
            var beendet = IstEnde(k.Status);
            var ende = beendet ? null : EndeAus(k, jetztUtc);
            VerfolgteAuktion? a;
            if (!beendet)
            {
                a = Auktionen.Where(x => !belegt.Contains(x) && x.Laeuft && Gleich(x, k, ende))
                             .OrderBy(x => ende is { } f && x.EndeUtc is { } e ? Math.Abs((e - f).TotalMinutes) : double.MaxValue)
                             .FirstOrDefault();
            }
            else
            {
                int Rang(VerfolgteAuktion x) =>
                    x.Ergebnis is not null ? (x.Endpreis is { } ep && k.Gebot is { } kg && ep != kg ? 4 : 0)
                    : x.Laeuft && (x.EndeUtc is not { } e || e <= jetztUtc.AddMinutes(6)) ? 1
                    : x.OhneAusgang ? 2
                    : 3;
                var kandidaten = Auktionen.Where(x => !belegt.Contains(x) && Gleich(x, k, null))
                                          .OrderBy(Rang)
                                          // Ein schon gesehenes Gebot ueber dem Endpreis gehoert nicht dazu.
                                          .ThenBy(x => k.Gebot is { } kg && x.Gebot is { } xg && xg > kg ? 1 : 0)
                                          .ThenBy(x => x.EndeUtc ?? DateTime.MaxValue)
                                          .ToList();
                a = kandidaten.FirstOrDefault();
                // Vorzeitig beendet, aber mehrere laufende kommen in Frage: nicht raten.
                if (a is not null && Rang(a) == 3 && kandidaten.Count(x => Rang(x) == 3) > 1) { a = null; }
            }
            if (a is null)
            {
                a = new VerfolgteAuktion
                {
                    Id = $"{Scan.ScanScreen.Falte(k.Auto)}-{k.Pi}-{k.Sofortkauf}-{(ende ?? jetztUtc):yyyyMMddHH}{(ende ?? jetztUtc).Minute / 10}",
                    Auto = k.Auto!,
                    Pi = k.Pi,
                };
                // Die Kennung muss eindeutig sein: der Reiter findet seine Zeilen ueber sie.
                while (Auktionen.Any(x => x.Id == a.Id)) { a.Id += "+"; }
                Auktionen.Add(a);
            }
            belegt.Add(a);
            var vorher = (a.Status, a.Gebot, a.EndeUtc);
            a.Pi ??= k.Pi;
            if (k.Status is not null) { a.Status = k.Status; }
            if (k.Gebot is not null) { a.Gebot = k.Gebot; }
            if (k.Sofortkauf is not null) { a.Sofortkauf = k.Sofortkauf; }
            // Eine genauere (spaetere, kuerzere) Schaetzung ersetzt die groebere; "Ending Soon" setzt
            // hoechstens auf fuenf Minuten, wenn die bisherige Schaetzung spaeter lag.
            if (ende is { } neu && (a.EndeUtc is null || neu < a.EndeUtc || (!k.BaldZuEnde && k.RestMinuten > 0)
                                    || (k.BaldZuEnde && a.EndeUtc <= jetztUtc)))
            {
                // "Ending Soon" laeuft noch: eine Schaetzung, die schon vorbei ist, war zu frueh.
                a.EndeUtc = k.BaldZuEnde && a.EndeUtc is { } alt && alt < neu && alt > jetztUtc ? alt : neu;
                if (a.EndeUtc == neu) { a.EndeSpielraum = Spielraum(k); }
            }
            // SPAETER ALS GEDACHT: Warnungen gegen die alte, zu fruehe Schaetzung zaehlen nicht -- sonst kaeme
            // "endet in 5 Minuten" eine Stunde zu frueh und am echten Ende keine.
            if (vorher.EndeUtc is { } frueher && a.EndeUtc is { } jetzt && jetzt - frueher > TimeSpan.FromMinutes(2))
            {
                a.Gewarnt5 = false;
                if ((jetzt - jetztUtc).TotalMinutes > 2) { a.Gewarnt2 = false; }
            }
            a.ZuletztGesehenUtc = jetztUtc;
            // Ein genaueres Ende als das schon gesehene: SOLD! sagt nur "verkauft", ein WON/LOST-Abzeichen wer.
            if (a.Ergebnis is "SOLD" or "NOT_SOLD" or "ENDED" && k.Status is "WON" or "LOST") { a.Ergebnis = k.Status; }
            if (beendet && a.Ergebnis is null)
            {
                // Spaetestens jetzt war sie vorbei: ein geschaetztes Ende danach (Sofortkauf, "Ending Soon" =
                // jetzt + 5) zeigte der Reiter sonst als "beendet ~" nach dem Blick, der sie beendet sah.
                if (a.EndeUtc is not { } bisher || bisher > jetztUtc) { a.EndeUtc = jetztUtc; }
                a.Ergebnis = k.Status;
                a.Endpreis = k.Gebot ?? a.Gebot;
                Preise.Add(new Preis(jetztUtc, a.Auto, a.Pi, a.Endpreis, a.Sofortkauf, 0, "my_bids", k.Status));
            }
            if (vorher != (a.Status, a.Gebot, a.EndeUtc)) { geaendert.Add(a); }
        }
        Aufraeumen(jetztUtc);
        return geaendert;
    }

    /// <summary>Suchergebnisse in die Preisgeschichte (je Auto und Gebot hoechstens einmal je Stunde).</summary>
    public void Gesehen(IReadOnlyList<AuktionsKarte> karten, string quelle, DateTime jetztUtc)
    {
        foreach (var k in karten)
        {
            if (string.IsNullOrWhiteSpace(k.Auto) || (k.Gebot is null && k.Sofortkauf is null)) { continue; }
            var doppelt = Preise.Any(p => p.Auto == k.Auto && p.Pi == k.Pi && p.Gebot == k.Gebot && p.Sofortkauf == k.Sofortkauf
                                          && p.ZeitUtc > jetztUtc.AddHours(-1));
            if (!doppelt) { Preise.Add(new Preis(jetztUtc, k.Auto!, k.Pi, k.Gebot, k.Sofortkauf, k.RestMinuten, quelle)); }
        }
        if (Preise.Count > 20000) { Preise.RemoveRange(0, Preise.Count - 20000); }
    }

    /// <summary>
    /// Laufende Auktionen, deren geschaetztes Ende seit einer Stunde vorbei ist und die seitdem niemand mehr
    /// gesehen hat, gelten als beendet ("ENDED"). Sonst blieben sie fuer immer "laufend", und eine neue
    /// Auktion desselben Autos landete im alten Eintrag. Die Stunde: "2 hours" kann bis zu 2:59 heissen.
    /// Taucht sie doch wieder auf, wird sie ein neuer Eintrag.
    /// </summary>
    public bool Verfallen(DateTime jetztUtc)
    {
        var geaendert = false;
        foreach (var a in Auktionen.Where(a => a.Laeuft && a.EndeUtc is { } e && e < jetztUtc.AddHours(-1) && a.ZuletztGesehenUtc < e))
        {
            a.Status = "ENDED";
            geaendert = true;
        }
        return geaendert;
    }

    private void Aufraeumen(DateTime jetztUtc)
    {
        // Beendete Auktionen nach drei Tagen weg; die Preisgeschichte bleibt.
        Auktionen.RemoveAll(a => !a.Laeuft && a.ZuletztGesehenUtc < jetztUtc.AddDays(-3));
    }

    /// <summary>Welche Warnungen jetzt faellig sind (5 und 2 Minuten vor Ende), und vermerkt sie.</summary>
    public List<(VerfolgteAuktion Auktion, int Minuten)> FaelligeWarnungen(DateTime jetztUtc)
    {
        var raus = new List<(VerfolgteAuktion, int)>();
        if (!Warnen) { return raus; }
        foreach (var a in Auktionen.Where(a => a.Laeuft && a.EndeUtc is not null))
        {
            var rest = (a.EndeUtc!.Value - jetztUtc).TotalMinutes;
            if (rest < -1) { continue; }
            if (!a.Gewarnt2 && rest <= 2) { a.Gewarnt2 = a.Gewarnt5 = true; raus.Add((a, 2)); }
            else if (!a.Gewarnt5 && rest <= 5) { a.Gewarnt5 = true; raus.Add((a, 5)); }
        }
        return raus;
    }
}
