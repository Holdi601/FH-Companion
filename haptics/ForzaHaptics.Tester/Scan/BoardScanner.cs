using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForzaHaptics.Tester.Rivals;

namespace ForzaHaptics.Tester.Scan;

/// <summary>
/// Eine Rivals-Bestenliste von oben nach unten ablesen und als Lauf ablegen -- im Format des alten
/// Werkzeugs (rows.jsonl + state.json), damit Einfuhr und Datensatzbau unveraendert bleiben.
/// </summary>
/// <remarks>
/// ## Der Ablauf
///
/// Der Cursor steht nach dem Oeffnen auf Platz 1. Je Schritt: das Bild lesen (elf Zeilen), dann
/// zehnmal RUNTER -- die Liste rueckt um zehn Zeilen, eine Zeile ueberlappt. Das alte Werkzeug
/// filmte eine fliessend scrollende Liste mit 22 Bildern je Sekunde und las jede Zeile 7-13 mal;
/// hier steht die Liste beim Lesen still, und die Plaetze kommen aus der Folge der elf Zeilen
/// (BoardReader.MitFolge), nicht aus jeder Zeile einzeln.
///
/// ## Was das alte Werkzeug lernte und hier gilt
///
/// - Das Spiel laedt die naechste Seite erst, wenn der Cursor ueber das Ende der geladenen
///   hinausgeht; Druecke dahinter verschluckt es ([[on-demand-paging]]). Darum wird bei
///   Stillstand weiter gedrueckt und gewartet, nicht aufgegeben.
/// - "Keine neuen Zeilen" ist nie das Ende: erst der Daumen des Scrollbalkens ganz unten
///   ([[no-rows-is-not-board-end]]).
/// - Nach den gueltigen Runden kommt der Block der ungueltigen, wieder ab einer schnelleren
///   Zeit ([[board-has-an-invalid-tail]]). Dort wird angehalten: "valid_section_complete".
/// - "Server Error" heisst aufhoeren ([[server-error-means-stop]]).
/// - Keine Gamertags: sie werden nicht gelesen.
///
/// ## Angehalten ist nicht verloren (2026-10-04)
///
/// Stop, die Pause-Taste oder ein anderes Fenster vorn werfen <see cref="ScanNavigator.Abbruch"/>
/// -- bis dahin fiel damit alles weg, was gelesen war, auch nach zwanzig Minuten. Steht der Scanner
/// schon auf der Liste, wird jetzt erst abgelegt (status und end_status "stopped"), dann weiter
/// geworfen; der Lauf haengt an <see cref="ScanNavigator.Abbruch.Lauf"/>. Ein Abbruch auf dem Weg
/// zur Liste legt nichts ab. Der Server haelt ein solches Board zurueck, wenn es weniger als 500
/// Zeilen oder weniger als 80 % der geschaetzten Laenge hat -- wie das alte Werkzeug.
///
/// Ein Absturzdialog des Spiels ist "game_crashed", nicht "server_error": nach einem Server-Fehler
/// wartet der Reiter acht Minuten und versucht es noch einmal, nach einem Absturz gibt es nichts
/// zu warten.
/// </remarks>
internal sealed class BoardScanner
{
    internal sealed record Ergebnis(string RunId, string Ordner, string Status, int Zeilen, int HoechsterPlatz, TimeSpan Dauer);

    internal sealed class Lesung
    {
        public required BoardRow Zeile { get; init; }
        public required string Bild { get; init; }
    }

    private readonly ScanRoutes _routen;
    private readonly Action<string> _melden;
    private readonly CancellationToken _stop;
    private readonly ScanEingabe _eingabe;
    private readonly WindowsOcr _ocr = new();
    private readonly string? _bilder;

    /// <summary>So viele Zeilen je Schritt -- elf sichtbar, zwei ueberlappen: eine einzelne Fehllesung reisst keine Luecke.</summary>
    public int Schritt { get; init; } = 9;

    /// <summary>
    /// So weit darf ein Bild ueber den bisher hoechsten Platz hinausgehen: ein leeres Bild im Fliessband
    /// (die Seite laedt) und ein Schritt im Voraus -- alles darueber ist eine Fehllesung, keine Luecke.
    /// </summary>
    private int MaxSprung => (3 * Schritt) + 11;

    /// <summary>Pause nach jedem Tastendruck (ms); der Druck selbst haelt 30 ms.</summary>
    public int TasteNach { get; init; } = 15;

    /// <summary>Pause nach den Tasten eines Schritts, bevor das Bild genommen wird (ms).</summary>
    public int Ruhe { get; init; } = 140;

    /// <summary>Auch den Block der ungueltigen Runden lesen, bis zum echten Ende (wie -ScanInvalidLaps im alten Werkzeug).</summary>
    public bool UngueltigeMitlesen { get; init; }

    /// <summary>Hoechstens so viele Plaetze (0 = ganze Liste).</summary>
    public int Obergrenze { get; init; }

    public BoardScanner(ScanRoutes routen, Action<string> melden, CancellationToken stop, string? bilderOrdner = null)
    {
        _routen = routen;
        _melden = melden;
        _stop = stop;
        _eingabe = new ScanEingabe(stop) { Halten = 20 };
        _bilder = bilderOrdner;
    }

    /// <summary>
    /// Hinfahren und die Liste ablesen. Wirft <see cref="ScanNavigator.Abbruch"/>, wenn es nicht geht --
    /// nach dem Erreichen der Liste mit dem bis dahin Gelesenen als <see cref="ScanNavigator.Abbruch.Lauf"/>.
    /// </summary>
    public Ergebnis Scanne(ScanNavigator.Ziel ziel, bool vomBoard, string wurzel)
    {
        var uhr = ScanEingabe.Uhr();
        var nav = new ScanNavigator(_routen, _melden, _stop, _bilder);
        var weg = nav.Fahre(ziel, vomBoard);
        var kat = _routen.Finde(ziel.Kategorie)!;
        var strecke = kat.Strecken[ziel.RouteIndex];
        var runId = $"ocr_{kat.Name.Replace(" ", string.Empty)}_idx{ziel.RouteIndex:00}_{weg.Klasse}_{DateTime.Now:yyyyMMdd_HHmmss}";
        _melden($"scanning {kat.Name} / {strecke} / {weg.Klasse} as {runId}");

        var lesungen = new Dictionary<int, List<Lesung>>();
        var daumen = new List<(int Platz, double Position)>();
        var hoechster = 0;
        var stillstand = 0;
        var status = "chunk_budget_spent";
        var ungueltigFolge = 0;
        var luecken = 0;
        var schritte = 0;
        var gelesen = 0;
        ScanNavigator.Abbruch? abbruch = null;
        Thread.Sleep(800);   // die Tabelle baut sich auf

        // FLIESSBAND: das Bild von Schritt n wird gelesen, waehrend die Tasten fuer Schritt n+1
        // schon fallen. Zwei Texterkennungen im Wechsel, weil zwei Lesungen gleichzeitig laufen.
        // Entscheidungen (Ende, Stillstand) fallen damit einen Schritt spaeter -- unschaedlich:
        // Druecke hinter dem Ende verschluckt das Spiel.
        var ocrs = new[] { _ocr, new WindowsOcr() };
        Task<(List<BoardRow> Zeilen, ScanScrollbar.Messung? Balken)> Lies(Bitmap bild, int nr, bool balkenMessen)
        {
            var ocr = ocrs[nr % 2];
            return Task.Run(() =>
            {
                try
                {
                    var zeilen = BoardReader.Lies(bild, ocr);
                    if (_bilder is not null && BoardReader.Ungelesen > 0) { Merke(bild, $"miss_{nr:000000}"); }
                    var balken = balkenMessen ? ScanScrollbar.Messen(bild) : null;
                    return (zeilen, balken);
                }
                finally
                {
                    bild.Dispose();
                }
            });
        }
        long tastenMs = 0, wartenMs = 0;
        var tastenUhr = new System.Diagnostics.Stopwatch();
        schritte = 1;
        var laufend = Lies(Aufnahme(1), 1, true);
        var laufendName = "s000001";

        // Ab hier steht der Scanner auf der Liste: ein Abbruch legt erst ab, was gelesen ist.
        try
        {
            while (true)
            {
                _eingabe.PruefeWeiter();
                // Tasten fuer den naechsten Schritt -- ausser bei Stillstand: dann erst das Ergebnis abwarten.
                var vorausGedrueckt = stillstand == 0;
                if (vorausGedrueckt) { Druecke(); }
                var (zeilen, balken) = WarteAuf(laufend);
                var bildName = laufendName;
                var verbraucht = laufend;

                zeilen = AusZeiten(zeilen, lesungen);
                var brauchbar = Glaubhaft(zeilen.Where(z => z.Brauchbar).ToList());
                Sammle(brauchbar, bildName);
                var oben = brauchbar.Count > 0 ? brauchbar.Max(z => z.Rank!.Value) : 0;

                // LUECKE HINTER DER FRONT: dieses Bild beginnt mehr als einen Platz hinter dem bisher
                // hoechsten -- ein Bild dazwischen kam leer an (die naechste Seite lud noch; live
                // 2026-10-04 an Platz 398-404, genau an einer 50er-Grenze), waehrend die Tasten im
                // Fliessband schon weiterliefen. Zurueck nach oben, die Luecke lesen, wieder hinunter.
                var unten = brauchbar.Count > 0 ? brauchbar.Min(z => z.Rank!.Value) : 0;
                if (hoechster > 0 && unten > hoechster + 1 && luecken < 200)
                {
                    luecken++;
                    // Der Cursor steht unten in der Ansicht (Zeile 10); erst 10 Druecke bis nach oben, dann
                    // rollt die Ansicht. Die Ansicht beginnt jetzt bei (unten + Schritt), wenn schon
                    // vorausgedrueckt wurde -- sonst bei unten.
                    var ansichtOben = unten + (vorausGedrueckt ? Schritt : 0);
                    // Gedeckelt: Glaubhaft laesst hoechstens einen Sprung von MaxSprung zu -- mehr Druecke
                    // als dafuer noetig waeren nie richtig.
                    var hoch = Math.Min(ansichtOben - hoechster + 10, MaxSprung + Schritt + 10);
                    _melden($"gap {hoechster + 1}-{unten - 1}: {hoch} up, read, {hoch} down");
                    for (var i = 0; i < hoch; i++) { _eingabe.Taste(ScanEingabe.Hoch, TasteNach); }
                    Thread.Sleep(Ruhe + 200);
                    for (var versuch = 0; versuch < 3; versuch++)
                    {
                        using var bild = ScanEingabe.Aufnahme();
                        if (_bilder is not null) { Merke(bild, $"gap_{hoechster + 1}_{versuch}"); }
                        var alle = AusZeiten(BoardReader.Lies(bild, _ocr), lesungen);
                        var nach = Glaubhaft(alle.Where(z => z.Brauchbar).ToList());
                        Sammle(nach, $"g{schritte:000000}");
                        if (Enumerable.Range(hoechster + 1, unten - hoechster - 1).All(lesungen.ContainsKey)) { break; }
                        Thread.Sleep(700);
                    }
                    for (var i = 0; i < hoch; i++) { _eingabe.Taste(ScanEingabe.Runter, TasteNach); }
                    Thread.Sleep(Ruhe);
                }
                if (balken is { Gefunden: true } b && !double.IsNaN(b.Position) && oben > 0
                    && (daumen.Count == 0 || Math.Abs(daumen[^1].Position - b.Position) > 0.01))
                {
                    daumen.Add((oben, b.Position));
                }

                // Der ungueltige Block: die meisten neuen Zeilen ungueltig, zwei Schritte nacheinander.
                var neu = brauchbar.Where(z => z.Rank > hoechster).ToList();
                ungueltigFolge = neu.Count >= 5 && neu.Count(z => z.IsClean == false) > 0.6 * neu.Count ? ungueltigFolge + 1 : 0;
                if (ungueltigFolge >= 2 && !UngueltigeMitlesen)
                {
                    status = "invalid_tail";
                    _melden($"invalid laps from about rank {neu.Min(z => z.Rank)}; the valid section is complete");
                    break;
                }

                if (oben > hoechster)
                {
                    hoechster = oben;
                    stillstand = 0;
                    if (schritte % 40 == 0)
                    {
                        _melden($"rank {hoechster:n0}, {lesungen.Count:n0} ranks read, {uhr.Elapsed.TotalMinutes:0.0} min "
                                + $"(keys {tastenMs / Math.Max(1, schritte)} ms, waiting for reads {wartenMs / Math.Max(1, schritte)} ms per step)");
                    }
                    if (Obergrenze > 0 && hoechster >= Obergrenze)
                    {
                        status = "row_cap_reached";
                        break;
                    }
                }
                else if (oben > 0 || brauchbar.Count == 0)
                {
                    stillstand++;
                    // Steht der Daumen unten, ist die Liste zu Ende -- sonst laedt das Spiel noch.
                    if (balken is { Gefunden: true, Unten: true } && stillstand >= 2)
                    {
                        status = "end_detected";
                        _melden($"end of the board at rank {hoechster:n0} (scrollbar at the bottom)");
                        break;
                    }
                    if (stillstand >= 3 && Dialog(out var text) is { } dialog)
                    {
                        status = DialogStatus(dialog);
                        _melden("the game shows: " + text);
                        break;
                    }
                    if (stillstand >= 12)
                    {
                        status = balken is { Gefunden: true } ? "truncated" : "end_unverified";
                        _melden($"no progress past rank {hoechster:n0} for {stillstand} looks; stopping as {status}");
                        break;
                    }
                    // Warten, mit wachsender Pause: die naechste Seite kommt erst nach einem Druck UEBER das Ende.
                    Thread.Sleep(Math.Min(6000, 400 * stillstand));
                }
                if (!vorausGedrueckt) { Druecke(); }
                schritte++;
                laufendName = $"s{schritte:000000}";
                laufend = Lies(Aufnahme(schritte), schritte, stillstand > 0 || schritte % 25 == 1);
            }
        }
        catch (ScanNavigator.Abbruch a)
        {
            abbruch = a;
            status = "stopped";
            _melden($"stopped at rank {hoechster:n0} ({a.Message}); keeping the {lesungen.Count:n0} ranks read so far");
        }
        // Die letzte Lesung noch einsammeln -- nur, wenn sie nicht schon verarbeitet ist.
        try
        {
            if (laufend.IsCompleted && lesungen.Values.Any(l => l.Any(x => x.Bild == laufendName))) { throw new OperationCanceledException(); }
            var (rest, _) = laufend.GetAwaiter().GetResult();
            foreach (var z in Glaubhaft(rest.Where(z => z.Brauchbar).ToList()))
            {
                if (!lesungen.TryGetValue(z.Rank!.Value, out var liste)) { lesungen[z.Rank.Value] = liste = new List<Lesung>(); }
                liste.Add(new Lesung { Zeile = z, Bild = laufendName });
                gelesen++;
            }
        }
        catch (Exception) { }
        _melden($"timing: {schritte} steps, keys {tastenMs / Math.Max(1, schritte)} ms and waiting for reads {wartenMs / Math.Max(1, schritte)} ms per step");

        // NUR GLAUBHAFTE PLAETZE (2026-10-04): ein Bild mit ein, zwei geladenen Zeilen hat keine Mehrheit,
        // dann bleibt die rohe Lesung einer Zelle stehen -- und "1,401" als "4,401" gelesen setzte die Front
        // um 3000 Plaetze weiter: tausende Tasten fuer die Luecke, danach Stillstand bis "truncated".
        // Darum: nichts ueber hoechster + MaxSprung, und ein Platz ohne Folge nur nahe der Front.
        List<BoardRow> Glaubhaft(List<BoardRow> zeilen)
        {
            if (hoechster == 0) { return zeilen; }
            var raus = zeilen.Where(z => z.Rank <= hoechster + MaxSprung
                                         && (z.Folge || z.Rank >= hoechster - MaxSprung && z.Rank <= hoechster + Schritt + 11)).ToList();
            if (raus.Count < zeilen.Count)
            {
                _melden($"ignored rank(s) {string.Join(", ", zeilen.Except(raus).Select(z => z.Rank))} near rank {hoechster:n0}: misread");
            }
            return raus;
        }

        void Sammle(IEnumerable<BoardRow> zeilen, string bild)
        {
            foreach (var z in zeilen)
            {
                if (!lesungen.TryGetValue(z.Rank!.Value, out var liste)) { lesungen[z.Rank.Value] = liste = new List<Lesung>(); }
                liste.Add(new Lesung { Zeile = z, Bild = bild });
                gelesen++;
            }
        }

        void Druecke()
        {
            tastenUhr.Restart();
            for (var i = 0; i < Schritt; i++) { _eingabe.Taste(ScanEingabe.Runter, TasteNach); }
            Thread.Sleep(Ruhe);
            tastenMs += tastenUhr.ElapsedMilliseconds;
        }

        (List<BoardRow>, ScanScrollbar.Messung?) WarteAuf(Task<(List<BoardRow> Zeilen, ScanScrollbar.Messung? Balken)> t)
        {
            tastenUhr.Restart();
            var r = t.GetAwaiter().GetResult();
            wartenMs += tastenUhr.ElapsedMilliseconds;
            return r;
        }

        Bitmap Aufnahme(int nr)
        {
            var bild = ScanEingabe.Aufnahme();
            if (_bilder is not null && (nr <= 8 || nr % 200 == 0 || stillstand > 0)) { Merke(bild, $"s{nr:000000}"); }
            return bild;
        }

        var dauer = uhr.Elapsed;
        var eingang = lesungen.ToDictionary(k => k.Key, k => k.Value.Select(l => (l.Zeile, l.Bild)).ToList());
        Ergebnis? ergebnis;
        try
        {
            ergebnis = Abschliessen(wurzel, runId, strecke, kat.Name, ziel.RouteIndex, weg.Klasse, status, eingang, daumen,
                                    dauer, gelesen, hoechster, Obergrenze, abbruch, _melden);
        }
        catch (Exception x) when (abbruch is not null && x is IOException or UnauthorizedAccessException)
        {
            // Der Abbruch bleibt die Nachricht; dass die Zeilen nicht abgelegt werden konnten, steht im Protokoll.
            _melden("could not keep the rows read so far: " + x.Message);
            ergebnis = null;
        }
        if (abbruch is not null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(abbruch).Throw(); }
        return ergebnis!;
    }

    /// <summary>
    /// Den Lauf ablegen und das Ergebnis bauen -- auch nach einem <paramref name="abbruch"/>: dann haengt das
    /// Ergebnis an <see cref="ScanNavigator.Abbruch.Lauf"/>. Ohne eine einzige Lesung wird nach einem Abbruch
    /// nichts abgelegt (null): ein leerer Lauf ist kein Board, und der Server naehme ihn ohnehin nicht.
    /// </summary>
    internal static Ergebnis? Abschliessen(string wurzel, string runId, string strecke, string kategorie, int index, string klasse,
                                           string status, Dictionary<int, List<(BoardRow Zeile, string Bild)>> lesungen,
                                           List<(int Platz, double Position)> daumen, TimeSpan dauer, int gelesen, int hoechster,
                                           int obergrenze, ScanNavigator.Abbruch? abbruch, Action<string> melden)
    {
        if (abbruch is not null && lesungen.Count == 0) { return null; }
        var ordner = Schreibe(wurzel, runId, strecke, kategorie, index, klasse, status, lesungen, daumen, dauer, gelesen, obergrenze);
        var anzahl = File.ReadLines(Path.Combine(ordner, "rows.jsonl")).Count();
        melden($"{status}: {anzahl:n0} rows up to rank {hoechster:n0} in {dauer.TotalMinutes:0.0} min ({anzahl / Math.Max(0.1, dauer.TotalMinutes):0} rows/min)");
        var ergebnis = new Ergebnis(runId, ordner, status, anzahl, hoechster, dauer);
        if (abbruch is not null) { abbruch.Lauf = ergebnis; }
        return ergebnis;
    }

    /// <summary>
    /// Plaetze aus den ZEITEN, wenn die Folge keinen ergibt: die Texterkennung verweigert manche
    /// Platzzahlen ganz ("1,010" bis "1,016" -- wie die Betraege im Auktionshaus), dann fiel das ganze
    /// Bild aus, auf jeder Liste an denselben Plaetzen (live 2026-10-04). Zwei Zeilen dieses Bilds mit
    /// Zeiten, die zwei schon gelesenen aufeinanderfolgenden Plaetzen gleichen, legen fest, welcher Platz
    /// oben steht. Mehrdeutig (gleiche Zeiten anderswo): nichts.
    /// </summary>
    internal static List<BoardRow> AusZeiten(List<BoardRow> zeilen, IReadOnlyDictionary<int, List<Lesung>> lesungen)
    {
        if (zeilen.Any(z => z.Rank is > 0) || lesungen.Count == 0) { return zeilen; }
        var nachZeit = new Dictionary<long, List<int>>();
        foreach (var (platz, liste) in lesungen)
        {
            foreach (var ms in liste.Select(l => l.Zeile.Ms!.Value).Distinct())
            {
                if (!nachZeit.TryGetValue(ms, out var p)) { nachZeit[ms] = p = new List<int>(); }
                p.Add(platz);
            }
        }
        var kandidaten = new HashSet<int>();
        for (var i = 0; i + 1 < zeilen.Count; i++)
        {
            if (zeilen[i].Ms is not { } a || zeilen[i + 1].Ms is not { } b) { continue; }
            if (zeilen[i + 1].Zeile != zeilen[i].Zeile + 1) { continue; }
            if (!nachZeit.TryGetValue(a, out var pa) || !nachZeit.TryGetValue(b, out var pb)) { continue; }
            foreach (var p in pa.Where(p => pb.Contains(p + 1))) { kandidaten.Add(p - zeilen[i].Zeile); }
        }
        if (kandidaten.Count != 1) { return zeilen; }
        var oben = kandidaten.First();
        if (oben < 1) { return zeilen; }
        return zeilen.Select(z => z with { Rank = z.Ms is > 0 ? oben + z.Zeile : z.Rank }).ToList();
    }

    /// <summary>Welcher Dialog steht ueber der Liste -- "Server Error" oder ein Absturz? Sonst null.</summary>
    private ScanSchirm? Dialog(out string text)
    {
        using var bild = ScanEingabe.Aufnahme();
        var zeilen = _ocr.Read(bild);
        text = string.Join(" / ", zeilen.Select(z => z.Text).Take(12));
        var schirm = ScanScreen.Einordnen(zeilen);
        return schirm is ScanSchirm.ServerFehler or ScanSchirm.Abgestuerzt ? schirm : null;
    }

    /// <summary>
    /// Der Status eines Boards, ueber dem ein Dialog steht: "game_crashed" fuer einen Absturz (der Reiter
    /// haelt die Warteschlange sofort an), sonst "server_error" (der Reiter wartet und versucht es noch einmal).
    /// </summary>
    internal static string DialogStatus(ScanSchirm dialog) => dialog == ScanSchirm.Abgestuerzt ? "game_crashed" : "server_error";

    private void Merke(Bitmap bild, string name)
    {
        try
        {
            Directory.CreateDirectory(_bilder!);
            using var klein = new Bitmap(bild, new Size(960, 540));
            klein.Save(Path.Combine(_bilder!, $"board_{name}.jpg"), ImageFormat.Jpeg);
        }
        catch (Exception) { }
    }

    // ------------------------------------------------------------------ //
    // Zusammenfuehren und Ablegen
    // ------------------------------------------------------------------ //

    /// <summary>PI-Grenzen je Klasse (unten ausschliesslich, oben einschliesslich) -- wie das alte Werkzeug.</summary>
    internal static (int Unten, int Oben)? PiBereich(string klasse) => klasse switch
    {
        "D" => (99, 400), "C" => (400, 500), "B" => (500, 600), "A" => (600, 700),
        "S1" => (700, 800), "S2" => (800, 900), "R" => (900, 998), _ => null,
    };

    /// <summary>
    /// Je Platz eine Zeile: die Zeit, die die meisten Lesungen sagen; die uebrigen Felder aus den
    /// Lesungen mit dieser Zeit (Mehrheit je Feld). Danach je gueltig/ungueltig die laengste nicht
    /// fallende Zeitfolge -- was sie bricht, war eine Fehllesung ("drop_stragglers" im alten Werkzeug).
    /// </summary>
    internal static List<JsonObject> Zusammenfuehren(Dictionary<int, List<(BoardRow Zeile, string Bild)>> lesungen, string klasse, out int verworfen)
    {
        var bereich = PiBereich(klasse);
        var zeilen = new List<(int Platz, long Ms, bool? Sauber, JsonObject Json)>();
        // Erst die eindeutigen Zeiten; ein Gleichstand (zwei Lesungen, zwei Zeiten) entscheidet
        // danach die Nachbarschaft: die Zeit, die zwischen die Nachbarn passt.
        var sicher = new SortedDictionary<int, long>();
        foreach (var (platz, liste) in lesungen)
        {
            var g = liste.GroupBy(l => l.Zeile.Ms!.Value).OrderByDescending(x => x.Count()).ToList();
            if (g.Count == 1 || g[0].Count() > g[1].Count()) { sicher[platz] = g[0].Key; }
        }
        foreach (var (platz, liste) in lesungen.OrderBy(k => k.Key))
        {
            var zeiten = liste.GroupBy(l => l.Zeile.Ms!.Value).OrderByDescending(g => g.Count()).ToList();
            if (zeiten.Count > 1 && zeiten[0].Count() == zeiten[1].Count())
            {
                var davor = sicher.Where(k => k.Key < platz).Select(k => (long?)k.Value).LastOrDefault();
                var danach = sicher.Where(k => k.Key > platz).Select(k => (long?)k.Value).FirstOrDefault();
                var passend = zeiten.Where(g => g.Count() == zeiten[0].Count()
                                                && (davor is null || g.Key >= davor) && (danach is null || g.Key <= danach)).ToList();
                if (passend.Count != 1) { continue; }   // umstritten
                zeiten.Remove(passend[0]);
                zeiten.Insert(0, passend[0]);
            }
            var ms = zeiten[0].Key;
            var gleich = zeiten[0].Select(l => l.Zeile).ToList();
            T? Mehrheit<T>(Func<BoardRow, T?> feld) where T : class =>
                gleich.Select(feld).Where(v => v is not null).GroupBy(v => v).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
            bool? MehrheitB(Func<BoardRow, bool?> feld) =>
                gleich.Select(feld).Where(v => v is not null).GroupBy(v => v!.Value).OrderByDescending(g => g.Count()).Select(g => (bool?)g.Key).FirstOrDefault();
            var pis = gleich.Select(z => z.Pi).Where(p => p is { } v && (bereich is null || (v > bereich.Value.Unten && v <= bereich.Value.Oben)))
                            .GroupBy(p => p!.Value).OrderByDescending(g => g.Count()).ToList();
            int? pi = pis.Count == 0 || (pis.Count > 1 && pis[0].Count() == pis[1].Count()) ? null : pis[0].Key;
            var auto = Mehrheit(z => z.Car);
            var voll = CarShortNames.Finde(auto);
            var erste = gleich[0];
            var sauber = MehrheitB(z => z.IsClean);
            var json = new JsonObject
            {
                ["rank"] = platz,
                ["lap_time_seconds"] = Math.Round(ms / 1000.0, 3),
            };
            if (auto is not null) { json["car_name"] = auto; }
            if (Mehrheit(z => z.Drivetrain) is { } antrieb) { json["drivetrain"] = antrieb; }
            if (Mehrheit(z => z.Gearbox) is { } gang) { json["gearbox"] = gang; }
            json["source_frame"] = liste.First(l => l.Zeile.Ms == ms).Bild;
            json["row_index"] = erste.Zeile;
            json["rank_anchored"] = true;
            json["line"] = erste.Line.Length > 160 ? erste.Line[..160] : erste.Line;
            json["used_abs"] = MehrheitB(z => z.Abs);
            json["used_tcs"] = MehrheitB(z => z.Tcs);
            json["used_stm"] = MehrheitB(z => z.Stm);
            json["is_clean"] = sauber;
            json["readings"] = liste.Count;
            json["agreement"] = zeiten[0].Count();
            json["pi"] = pi;
            json["pi_agreement"] = pi is null ? 0 : pis[0].Count();
            if (voll is not null)
            {
                json["car_full_name"] = voll.Name;
                if (voll.Id is { } id) { json["car_short_id"] = id; }
            }
            json["scanner"] = "fhc";
            zeilen.Add((platz, ms, sauber, json));
        }
        // Laengste nicht fallende Zeitfolge, getrennt fuer gueltige und den Rest.
        var behalten = new HashSet<int>();
        foreach (var gruppe in new[] { zeilen.Where(z => z.Sauber == true).ToList(), zeilen.Where(z => z.Sauber != true).ToList() })
        {
            foreach (var i in LaengsteFolge(gruppe.Select(z => z.Ms).ToList())) { behalten.Add(gruppe[i].Platz); }
        }
        verworfen = zeilen.Count - behalten.Count;
        return zeilen.Where(z => behalten.Contains(z.Platz)).Select(z => z.Json).ToList();
    }

    /// <summary>Die Indizes der laengsten nicht fallenden Teilfolge (O(n log n)).</summary>
    internal static List<int> LaengsteFolge(IReadOnlyList<long> werte)
    {
        var enden = new List<int>();          // Index des kleinsten Endes je Laenge
        var vorher = new int[werte.Count];
        for (var i = 0; i < werte.Count; i++)
        {
            int lo = 0, hi = enden.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (werte[enden[mid]] <= werte[i]) { lo = mid + 1; } else { hi = mid; }
            }
            vorher[i] = lo > 0 ? enden[lo - 1] : -1;
            if (lo == enden.Count) { enden.Add(i); } else { enden[lo] = i; }
        }
        var raus = new List<int>();
        for (var k = enden.Count > 0 ? enden[^1] : -1; k >= 0; k = vorher[k]) { raus.Add(k); }
        raus.Reverse();
        return raus;
    }

    /// <summary>Die Laenge der Liste aus dem Daumen: Steigung zwischen erstem und letztem Punkt, wie das alte Werkzeug.</summary>
    internal static (int? Gesamt, string Art) Laenge(IReadOnlyList<(int Platz, double Position)> punkte)
    {
        if (punkte.Count >= 2)
        {
            var (r0, p0) = punkte[0];
            var (r1, p1) = punkte[^1];
            if (r1 - r0 > 0 && p1 - p0 > 0.002) { return ((int)Math.Round((r1 - r0) / (p1 - p0)), "slope"); }
        }
        if (punkte.Count >= 1 && punkte[^1].Position >= 0.02) { return ((int)Math.Round(punkte[^1].Platz / punkte[^1].Position), "single_point"); }
        return (null, "thumb_pinned");
    }

    private static string Schreibe(string wurzel, string runId, string strecke, string kategorie, int index, string klasse, string ende,
                                   Dictionary<int, List<(BoardRow Zeile, string Bild)>> eingang, List<(int Platz, double Position)> daumen,
                                   TimeSpan dauer, int gelesen, int obergrenze)
    {
        var ordner = Path.Combine(wurzel, runId);
        Directory.CreateDirectory(Path.Combine(ordner, "combined"));
        var zeilen = Zusammenfuehren(eingang, klasse, out var verworfen);
        var platzListe = zeilen.Select(z => (int)z["rank"]!).ToList();
        var min = platzListe.Count > 0 ? platzListe.Min() : 0;
        var max = platzListe.Count > 0 ? platzListe.Max() : 0;
        var fehlend = max - min + 1 - platzListe.Count;
        var optionen = new JsonSerializerOptions { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        File.WriteAllLines(Path.Combine(ordner, "rows.jsonl"), zeilen.Select(z => z.ToJsonString(optionen)), new UTF8Encoding(false));
        var (gesamt, art) = Laenge(daumen);
        var status = ende switch { "end_detected" => "end_detected", "invalid_tail" => "valid_section_complete", "" => "truncated", var s => s };
        var zustand = new JsonObject
        {
            ["run_id"] = runId,
            ["track"] = strecke,
            ["performance_class"] = klasse,
            ["rivals_mode"] = kategorie,
            ["route_index"] = index,
            ["end_status"] = ende,
            ["end_rejections"] = 0,
            ["row_cap"] = obergrenze > 0 ? obergrenze : null,
            ["implied_total"] = gesamt,
            ["length_estimator"] = art,
            ["thumb_points"] = new JsonArray(daumen.Select(d => (JsonNode)new JsonObject { ["rank"] = d.Platz, ["position"] = d.Position }).ToArray()),
            ["row_coverage"] = gesamt is > 0 ? Math.Round(platzListe.Count / (double)gesamt.Value, 4) : null,
            ["status"] = status,
            ["gapless"] = fehlend == 0,
            ["rows_collected"] = platzListe.Count,
            ["minimum_rank"] = min,
            ["maximum_rank"] = max,
            ["missing_ranks"] = fehlend,
            ["scanner"] = "ocr",
            ["scanner_tool"] = "fhc/" + (typeof(BoardScanner).Assembly.GetName().Version?.ToString() ?? "?"),
            ["readings"] = gelesen,
            ["seconds"] = Math.Round(dauer.TotalSeconds),
            ["completed_at"] = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture),
        };
        File.WriteAllText(Path.Combine(ordner, "state.json"), zustand.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        var bericht = new JsonObject
        {
            ["rows"] = platzListe.Count, ["minimum_rank"] = min, ["maximum_rank"] = max,
            ["missing_rank_count"] = fehlend, ["stragglers_dropped"] = verworfen,
            ["pi_read"] = zeilen.Count(z => z["pi"] is not null), ["source"] = "ocr",
        };
        File.WriteAllText(Path.Combine(ordner, "combined", "merge_report.json"), bericht.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        return ordner;
    }
}
