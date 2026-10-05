using System.Drawing;
using System.Text.RegularExpressions;
using ForzaHaptics.Tester.Rivals;
using ForzaHaptics.Tester.Scan;

namespace ForzaHaptics.Tester.Auction;

/// <summary>
/// Faehrt zum Auktionshaus, oeffnet "My Bids" und liest die Liste EINMAL -- sonst nichts. Er bietet
/// nicht, waehlt keine Karte an und oeffnet keine "Auction Options".
/// </summary>
/// <remarks>
/// ## Der Weg (am 2026-10-04 im Spiel aufgenommen)
///
///     Pausenmenue -> Reiter CARS -> Kachel "Buy New &amp; Used Cars" -> (nicht am Festival:
///     "Travel to Festival" -> Yes, Laden) -> Garagenmenue BUY &amp; SELL -> "Auction House" ->
///     Menue: Search Auctions / Start Auction / My Bids / My Auctions / Auction Alerts -> "My Bids"
///     -> Liste (bis zu vier Karten) mit "Auction Details" rechts.
///
/// ## Was er tut und was nie
///
/// - Er drueckt nur Tasten, die zur Liste "My Bids" fuehren, liest die Karten und arbeitet sie in den
///   Stand ein (<see cref="AuctionWatch.Einarbeiten"/>). Danach ist der Lauf zu Ende.
/// - Nie Y auf einer Karte, nie "Place Bid", nie "Buy Out": geboten wird ausschliesslich vom Nutzer.
/// - Vor jedem Tastendruck: Pause-Taste, Abbruch in der App, Spiel vorn (ScanEingabe).
/// </remarks>
internal sealed class AuctionBidder
{
    private readonly AuctionWatch _wache;
    private readonly Action<string> _melden;
    private readonly ScanEingabe _eingabe;
    private readonly WindowsOcr _ocr = new();

    private static readonly Regex BuyNew = new(@"Buy\s+New", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Reise = new(@"Travel\s+to\s+Festival|Fast\s+Travel\s+to\s+the\s+nearest", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public AuctionBidder(AuctionWatch wache, Action<string> melden, CancellationToken stop)
    {
        _wache = wache;
        _melden = melden;
        _eingabe = new ScanEingabe(stop) { Halten = 30 };
    }

    // ------------------------------------------------------------------ //
    // Sehen
    // ------------------------------------------------------------------ //

    private sealed record Blick(List<OcrLine> Zeilen, Bitmap Bild, ScanSchirm Allgemein, AuktionsSchirm Auktion) : IDisposable
    {
        public void Dispose() => Bild.Dispose();
        public bool Hat(Regex m) => Zeilen.Any(z => m.IsMatch(z.Text));
        public OcrLine? Zeile(string key, string en) =>
            Zeilen.Where(z => GameText.Gleich(z.Text, key, en)).Select(z => (OcrLine?)z).FirstOrDefault()
            ?? Zeilen.Where(z => GameText.Enthaelt(z.Text, key, en)).Select(z => (OcrLine?)z).FirstOrDefault();
    }

    private Blick Sehen()
    {
        _eingabe.PruefeWeiter();
        var bild = ScanEingabe.Aufnahme();
        var zeilen = _ocr.Read(bild);
        return new Blick(zeilen, bild, ScanScreen.Einordnen(zeilen), AuctionReader.Einordnen(zeilen));
    }

    private void Taste(byte vk, int nachher = 400) => _eingabe.Taste(vk, nachher);

    /// <summary>
    /// Ist die Menuezeile markiert? Der gelbe Rahmen steht links neben dem Text (x 60-80) auf Hoehe der
    /// Zeilenmitte -- die Nachbarzeilen teilen nur die waagerechten Kanten.
    /// </summary>
    private static bool ZeileMarkiert(Bitmap bild, OcrLine zeile)
    {
        var px = BoardReader.Pixel.Aus(bild);
        var y = (int)(zeile.Y + (zeile.H > 0 ? zeile.H / 2 : 12));
        var n = 0;
        for (var x = (int)zeile.X - 40; x < (int)zeile.X - 10; x++)
        {
            var (r, g, b) = px.Farbe(x, y);
            if (ScanScreen.IstMarkierung(r, g, b)) { n++; }
        }
        return n >= 2;
    }

    /// <summary>In einer senkrechten Liste die Zeile mit diesem Wort anwaehlen (hoch/runter, nachsehen).</summary>
    private bool WaehleZeile(string key, string en)
    {
        for (var versuch = 0; versuch < 8; versuch++)
        {
            using var b = Sehen();
            var ziel = b.Zeile(key, en);
            if (ziel is null) { return false; }
            if (ZeileMarkiert(b.Bild, ziel.Value)) { return true; }
            // Welche Zeile ist markiert? Darueber oder darunter?
            var markiert = b.Zeilen.Where(z => z.X < 500 && Math.Abs(z.X - ziel.Value.X) < 60 && ZeileMarkiert(b.Bild, z))
                                   .Select(z => (OcrLine?)z).FirstOrDefault();
            Taste(markiert is { } m && m.Y > ziel.Value.Y ? ScanEingabe.Hoch : ScanEingabe.Runter, 600);
        }
        return false;
    }

    // ------------------------------------------------------------------ //
    // Hinfahren
    // ------------------------------------------------------------------ //

    /// <summary>Bis zur Liste "My Bids". Wirft <see cref="ScanNavigator.Abbruch"/>, wenn es nicht geht.</summary>
    private void ZuMeinenGeboten()
    {
        var abseits = 0;
        var ausMeinenGeboten = false;
        string? zuletzt = null;
        // Die Reiter der Garage: in welche Richtung, und welche Ueberschrift der letzte Druck zeigte.
        var garageTaste = ScanEingabe.Rechts;
        string? garageUeberschrift = null;
        for (var zyklus = 0; zyklus < 120; zyklus++)
        {
            using var b = Sehen();
            var name = b.Auktion != AuktionsSchirm.Unbekannt ? b.Auktion.ToString() : b.Allgemein.ToString();
            if (name != zuletzt) { _melden("screen: " + name); zuletzt = name; }
            // Eine Liste zaehlt nur, wenn DIESER Weg sie ueber "My Bids" geoeffnet hat -- das
            // Suchergebnis sieht genauso aus.
            if (b.Auktion == AuktionsSchirm.Liste)
            {
                if (ausMeinenGeboten) { return; }
                Taste(ScanEingabe.Esc, 1500);
                continue;
            }
            if (b.Auktion == AuktionsSchirm.Optionen || b.Auktion == AuktionsSchirm.Ergebnis) { Taste(ScanEingabe.Esc, 1200); continue; }
            if (b.Auktion == AuktionsSchirm.Menue)
            {
                _melden(Loc.T("Auction house: opening My Bids"));
                if (!WaehleZeile("my_bids", "My Bids")) { throw new ScanNavigator.Abbruch("My Bids not found in the auction house menu"); }
                Taste(ScanEingabe.Enter, 4000);
                ausMeinenGeboten = true;
                continue;
            }
            if (b.Hat(Reise) && ScanDialog.IstJaNein(b.Zeilen))
            {
                // "Do you want to Fast Travel to the nearest Festival Site?" -- Ja kostet nichts. Aber nur ein
                // Ja/Nein-Dialog, und ENTER nur mit dem Rahmen auf "Yes" (sonst ESC): ein blindes ENTER
                // bestaetigte, was gerade markiert ist -- auch eine Kachel, deren Text "Travel to Festival" enthielt.
                _melden(Loc.T("Auction house: fast travel to the festival site"));
                ScanDialog.JaWaehlen(_eingabe, _ocr, _melden, 15000);
                continue;
            }
            switch (b.Allgemein)
            {
                case ScanSchirm.Abgestuerzt:
                    throw new ScanNavigator.Abbruch(AbsturzText);
                case ScanSchirm.ServerFehler:
                    throw new ScanNavigator.Abbruch(ServerFehlerText);
                case ScanSchirm.Titel:
                    Taste(ScanEingabe.Enter, 3000);
                    continue;
                case ScanSchirm.Weiter:
                    Taste(ScanEingabe.Enter, 8000);
                    continue;
                case ScanSchirm.SerienUpdate:
                    Taste(ScanEingabe.Enter, 2000);
                    continue;
                case ScanSchirm.Garage:
                    // Das Garagenmenue: "Auction House" steht im Reiter BUY & SELL.
                    if (b.Zeile("auction_house", "Auction House") is not null)
                    {
                        _melden(Loc.T("Auction house: garage menu"));
                        if (WaehleZeile("auction_house", "Auction House")) { Taste(ScanEingabe.Enter, 4000); }
                    }
                    else
                    {
                        // NICHT NUR RECHTS (live 2026-10-04): stand die Garage auf CARS oder weiter rechts, lief
                        // der Bieter bis CHARACTER am Ende und drueckte dort zwei Minuten ins Leere -- BUY & SELL
                        // liegt links davon. Bleibt die Ueberschrift des Reiters nach einem Druck gleich, ist das
                        // Ende erreicht: die Richtung wechseln. Ohne Reiternamen, also in jeder Spielsprache.
                        var ueberschrift = GarageUeberschrift(b);
                        if (ueberschrift is not null && ueberschrift == garageUeberschrift)
                        {
                            garageTaste = garageTaste == ScanEingabe.Rechts ? ScanEingabe.Links : ScanEingabe.Rechts;
                        }
                        garageUeberschrift = ueberschrift;
                        Taste(garageTaste, 900);
                    }
                    continue;
                case ScanSchirm.Pause:
                case ScanSchirm.OnlineReiter:
                    // Pausenmenue: Reiter CARS mit der Kachel "Buy New & Used Cars".
                    if (b.Hat(BuyNew))
                    {
                        if (!KachelBuyNew(b)) { Taste(ScanEingabe.Links, 700); }
                    }
                    else
                    {
                        // Welcher Reiter ist offen? CARS ist der zweite (CAMPAIGN, CARS, MY HORIZON, ONLINE ...).
                        // Links vom ersten Reiter geht es nicht weiter -- also in die richtige Richtung.
                        var offen = OffenerReiter(b);
                        Taste(offen is { } o && o < 1 ? ScanEingabe.Rechts : ScanEingabe.Links, 900);
                    }
                    continue;
                case ScanSchirm.Bestenliste:
                case ScanSchirm.Klassenschirm:
                case ScanSchirm.RivalDetail:
                case ScanSchirm.Streckenliste:
                case ScanSchirm.Kategorien:
                case ScanSchirm.RivalsHub:
                case ScanSchirm.Karte:
                case ScanSchirm.ForzaLink:
                    Taste(ScanEingabe.Esc, 1500);
                    continue;
                case ScanSchirm.Bestaetigung:
                    ScanDialog.NeinWaehlen(_eingabe, _ocr, _melden);
                    continue;
            }
            // Laden, freies Fahren, das Leerlaufbild der Garage: eine Weile warten, dann wecken.
            abseits++;
            if (abseits % 4 == 0)
            {
                // Im freien Fahren oeffnet ESC die Pause (zuerst: HOCH waere dort Gas geben); in der
                // Garage weckt eine Pfeiltaste das ausgeblendete Menue (zweiter Versuch).
                Taste(abseits % 8 == 4 ? ScanEingabe.Esc : ScanEingabe.Hoch, 2000);
            }
            else
            {
                Thread.Sleep(1500);
            }
        }
        throw new ScanNavigator.Abbruch("did not reach My Bids");
    }

    /// <summary>
    /// Der offene Reiter des Pausenmenues (0 = CAMPAIGN, 1 = CARS ...): die Reiterzeile steht bei
    /// y ~224; der offene Reiter ist schwarz hinterlegt, die anderen weiss. Null, wenn nicht lesbar.
    /// </summary>
    /// <summary>
    /// Die grosse Ueberschrift des offenen Garagenreiters (1080p: links oben unter der Reiterzeile, y ~200-250,
    /// "Cars", "Character" ...). Null, wenn nicht lesbar.
    /// </summary>
    private static string? GarageUeberschrift(Blick b) =>
        b.Zeilen.Where(z => z.Y is > 195 and < 255 && z.X < 400 && z.Text.Trim().Length >= 3)
                .OrderBy(z => z.Y).Select(z => z.Text.Trim()).FirstOrDefault();

    private static int? OffenerReiter(Blick b)
    {
        var reiter = b.Zeilen.Where(z => z.Y is > 205 and < 245 && z.X is > 450 and < 1500).OrderBy(z => z.X).ToList();
        if (reiter.Count < 4) { return null; }
        var px = BoardReader.Pixel.Aus(b.Bild);
        var hell = reiter.Select(z =>
        {
            int summe = 0, n = 0;
            for (var x = (int)z.X - 8; x < (int)(z.X + Math.Max(20, z.W)); x += 3)
            {
                for (var y = (int)z.Y - 4; y < (int)(z.Y + Math.Max(16, z.H)) + 4; y += 3) { summe += px.Hell(x, y); n++; }
            }
            return n == 0 ? 255 : summe / n;
        }).ToList();
        var dunkelste = hell.IndexOf(hell.Min());
        return hell[dunkelste] < 110 ? dunkelste : null;
    }

    /// <summary>
    /// Die Kachel "Buy New &amp; Used Cars" (ganz links im Reiter CARS) oeffnen -- nur wenn der Rahmen
    /// sie umschliesst. Die Kachel ist selbst limettengruen; darum zaehlt die Unterkante des Rahmens
    /// ueber dem tuerkisen Seitengrund (ScanNavigator.RahmenUnterText), nicht die Farbe der Kachel.
    /// </summary>
    private bool KachelBuyNew(Blick b)
    {
        var text = b.Zeilen.Where(z => BuyNew.IsMatch(z.Text)).Select(z => (OcrLine?)z).FirstOrDefault();
        if (text is null || !ScanNavigator.RahmenUnterText(b.Bild, text.Value)) { return false; }
        _melden(Loc.T("Auction house: Buy New & Used Cars"));
        Taste(ScanEingabe.Enter, 3000);
        return true;
    }

    // ------------------------------------------------------------------ //
    // Lesen
    // ------------------------------------------------------------------ //

    /// <summary>Die Karten lesen und einarbeiten.</summary>
    private List<AuktionsKarte> Lesen()
    {
        using var b = Sehen();
        if (b.Auktion != AuktionsSchirm.Liste) { return new List<AuktionsKarte>(); }
        var karten = AuctionReader.LiesKarten(b.Bild, _ocr);
        _wache.Einarbeiten(karten, meineGebote: true);
        return karten;
    }

    /// <summary>Wie ein Lauf endete.</summary>
    internal enum Ende
    {
        /// <summary>"My Bids" gelesen.</summary>
        Fertig,
        /// <summary>Stopp in der App, Pause-Taste oder ein anderes Fenster vorn.</summary>
        VomNutzer,
        /// <summary>Das Spiel liess sich nicht nach vorn holen; nichts gedrueckt.</summary>
        NichtVorn,
        ServerFehler,
        Abgestuerzt,
        /// <summary>Weg nicht gefunden oder Spiel nicht da.</summary>
        Abgebrochen,
    }

    /// <summary>Zu "My Bids" fahren, die Liste einmal lesen und zurueckkehren.</summary>
    public Ende Laufe()
    {
        if (!AuctionWatch.SpielLaeuft()) { _melden(Loc.T("The game is not running.")); return Ende.Abgebrochen; }
        if (!ScanEingabe.SpielNachVorn())
        {
            _melden(Loc.T("Auction house: the game could not be brought to the front."));
            return Ende.NichtVorn;
        }
        Thread.Sleep(800);
        try
        {
            _melden(Loc.T("Auction house: on the way"));
            ZuMeinenGeboten();
            var karten = Lesen();
            _melden(string.Format(Loc.T("My Bids: {0} auctions read"), karten.Count));
            return Ende.Fertig;
        }
        catch (ScanNavigator.Abbruch e)
        {
            _melden(Loc.T("Auction house stopped: ") + e.Message);
            return e.VomNutzer ? Ende.VomNutzer
                 : e.Message == ServerFehlerText ? Ende.ServerFehler
                 : e.Message == AbsturzText ? Ende.Abgestuerzt
                 : Ende.Abgebrochen;
        }
        catch (OperationCanceledException)
        {
            _melden(Loc.T("Auction house stopped."));
            return Ende.VomNutzer;
        }
    }

    private const string ServerFehlerText = "Server Error", AbsturzText = "the game has crashed";
}
