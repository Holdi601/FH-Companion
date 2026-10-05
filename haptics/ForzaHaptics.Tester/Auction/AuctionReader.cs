using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text.RegularExpressions;
using ForzaHaptics.Tester.Rivals;
using ForzaHaptics.Tester.Scan;

namespace ForzaHaptics.Tester.Auction;

/// <summary>Die Schirme des Auktionshauses, wie die App sie unterscheidet.</summary>
internal enum AuktionsSchirm { Unbekannt, Menue, Liste, Optionen, Suche, Ergebnis }

/// <summary>Eine Karte der Liste (My Bids oder Suchergebnis), wie sie auf dem Schirm steht.</summary>
internal sealed record AuktionsKarte
{
    public int Platz { get; init; }
    public string? Auto { get; init; }
    public int? Pi { get; init; }
    /// <summary>"OUTBID", "WINNING", "SOLD", "WON", "LOST" -- in der Spielsprache gelesen, hier englisch.</summary>
    public string? Status { get; init; }
    public long? Gebot { get; init; }
    public long? Sofortkauf { get; init; }
    /// <summary>Restzeit in Minuten; 0 bei "Ending Soon" (unter fuenf Minuten, laut Spiel "bald").</summary>
    public int? RestMinuten { get; init; }
    public bool BaldZuEnde { get; init; }
    public bool Eigen { get; init; }
    public bool Markiert { get; init; }
    public bool Verblasst { get; init; }
    /// <summary>Was je Feld gelesen wurde (Name | PI | Abzeichen | Gebot | Sofortkauf | Zeit), zur Nachpruefung.</summary>
    public string Roh { get; init; } = string.Empty;
}

/// <summary>
/// Liest das Auktionshaus von Forza Horizon 6 -- aufgenommen am 2026-10-04 an My Bids und Search
/// Auctions (1080p): links bis zu vier Karten, 202 px Abstand, rechts "Auction Details".
/// </summary>
/// <remarks>
/// Je Karte (k = 0..3, dy = 202 k): Name x 315-820 / y 172-212; Klasse + PI x 832-938 / y 172-206;
/// Status-Abzeichen (rosa "OUTBID") x 212-304 / y 272-304; Hoechstgebot x 410-585 / y 288-326;
/// Sofortkauf (rot) x 650-880 / y 288-326; Restzeit (gelbes Feld mit Uhr) x 60-340 / y 321-355;
/// "OWNED" oben links x 75-150 / y 168-188. Ein verkaufter Eintrag ist blass (Name grau) und traegt
/// den gelben "SOLD!"-Aufkleber ueber dem Bild.
///
/// Alle Woerter kommen aus den Stringtabellen des Spiels (GameText), in jeder Spielsprache.
/// </remarks>
internal static class AuctionReader
{
    internal const int KartenAbstand = 202, Karten = 4;

    private static readonly Rectangle Name = new(315, 172, 505, 40);
    private static readonly Rectangle KlassePi = new(832, 172, 106, 34);
    private static readonly Rectangle Abzeichen = new(212, 272, 92, 32);
    private static readonly Rectangle Gebot = new(417, 289, 175, 42);
    private static readonly Rectangle Sofort = new(655, 289, 230, 42);
    private static readonly Rectangle Zeit = new(60, 321, 280, 34);
    private static readonly Rectangle Eigen = new(75, 166, 80, 24);

    /// <summary>Welcher Auktionsschirm ist das?</summary>
    public static AuktionsSchirm Einordnen(IReadOnlyList<OcrLine> z)
    {
        bool Hat(string k, string en) => z.Any(l => GameText.Enthaelt(l.Text, k, en));
        if (Hat("auction_options", "Auction Options") && Hat("place_bid", "Place Bid")) { return AuktionsSchirm.Optionen; }
        if (Hat("bid_successful", "Bid Successful") || z.Any(l => GameText.BeginntWie(l.Text, "auction_closed", "Your bid failed."))
            || z.Any(l => GameText.BeginntWie(l.Text, "not_enough_credits", "You do not have enough credits.")))
        {
            return AuktionsSchirm.Ergebnis;
        }
        if (Hat("auction_details", "Auction Details")) { return AuktionsSchirm.Liste; }
        // LEER: "NO AUCTIONS TO DISPLAY" -- eine Liste ohne Karten (My Bids, nachdem das Spiel beendete Auktionen
        // entfernt hat; ebenso eine leere Suche). Vorher "unbekannt", und der Bieter oeffnete My Bids in einer Schleife.
        if (Hat("no_auctions", "NO AUCTIONS TO DISPLAY")) { return AuktionsSchirm.Liste; }
        if (Hat("search_auctions", "Search Auctions") && Hat("my_bids", "My Bids")) { return AuktionsSchirm.Menue; }
        return AuktionsSchirm.Unbekannt;
    }

    /// <summary>
    /// Ist im Menue der Eintrag mit diesem Wort markiert? Der gelbe Rahmen steht links neben dem Text
    /// (x - 40 bis x - 10) auf Hoehe der Zeilenmitte; Nachbarzeilen teilen nur die waagerechten Kanten.
    /// </summary>
    public static bool MenueMarkiert(Bitmap bild, IReadOnlyList<OcrLine> zeilen, string key, string en)
    {
        var ziel = zeilen.Where(z => GameText.Enthaelt(z.Text, key, en)).Select(z => (OcrLine?)z).FirstOrDefault();
        if (ziel is not { } z) { return false; }
        var px = BoardReader.Pixel.Aus(bild);
        var y = (int)(z.Y + (z.H > 0 ? z.H / 2 : 12));
        var n = 0;
        for (var x = (int)z.X - 40; x < (int)z.X - 10; x++)
        {
            var (r, g, b) = px.Farbe(x, y);
            if (ScanScreen.IstMarkierung(r, g, b)) { n++; }
        }
        return n >= 2;
    }

    /// <summary>"Place Bid - CR +291,000": das naechste Gebot, das ENTER abgeben wuerde.</summary>
    public static long? NaechstesGebot(IReadOnlyList<OcrLine> z)
    {
        foreach (var l in z)
        {
            if (!GameText.Enthaelt(l.Text, "place_bid", "Place Bid")) { continue; }
            return Zahl(l.Text);
        }
        return null;
    }

    /// <summary>
    /// Die erste mehrstellige Zahl eines Texts ("@ 283,000" -> 283000) -- nur als GANZES Wort: die
    /// Gruppen nach dem ersten Trenner haben genau drei Ziffern, die erste eine bis drei. Sonst null.
    /// </summary>
    /// <remarks>
    /// Vorher galt der erste Treffer mit drei Ziffern, und eine verrutschte Lesung wurde KLEINER:
    /// "+1,291,00" gab 1291, "4291,000" (das Plus als 4) gab 4291 -- und Gebot und Preisgeschichte
    /// stuenden falsch im Stand. Eine Lesung, die nicht aufgeht, ist keine.
    /// "291, 000" (Leerzeichen hinter dem Komma) ist dagegen dieselbe Zahl.
    /// </remarks>
    internal static long? Zahl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) { return null; }
        var t = text.Replace('O', '0').Replace('o', '0');
        var i = 0;
        while (i < t.Length)
        {
            if (!char.IsAsciiDigit(t[i])) { i++; continue; }
            // Ein Wort aus Zifferngruppen: Komma, Punkt, Apostroph (je mit hoechstens einem Leerzeichen
            // dahinter) trennen immer; ein Leerzeichen allein nur vor genau drei Ziffern.
            var gruppen = new List<string>();
            var j = i;
            var bruchstueck = false;
            while (true)
            {
                var s = j;
                while (j < t.Length && char.IsAsciiDigit(t[j])) { j++; }
                gruppen.Add(t[s..j]);
                var k = j;
                if (k < t.Length && t[k] is ',' or '.' or '\'' or '’')
                {
                    k++;
                    if (k < t.Length && char.IsWhiteSpace(t[k])) { k++; }
                    if (k < t.Length && char.IsAsciiDigit(t[k])) { j = k; continue; }
                    break;
                }
                if (k < t.Length && char.IsWhiteSpace(t[k]))
                {
                    k++;
                    var n = 0;
                    while (k + n < t.Length && char.IsAsciiDigit(t[k + n])) { n++; }
                    if (n == 3) { j = k; continue; }
                    // Gleich dahinter weitere Ziffern ("1,250 00"): ein Bruchstueck derselben Zahl.
                    bruchstueck = n > 0;
                }
                break;
            }
            var ziffern = string.Concat(gruppen);
            if (ziffern.Length >= 3)
            {
                var gueltig = !bruchstueck && (gruppen.Count == 1
                    ? ziffern.Length <= 10
                    : gruppen[0].Length is >= 1 and <= 3 && gruppen.Skip(1).All(g => g.Length == 3));
                return gueltig && long.TryParse(ziffern, out var v) ? v : null;
            }
            i = j;
        }
        return null;
    }

    /// <summary>
    /// Restzeit in Minuten aus "2 hours", "1 hour 59 mins", "45 mins", "1 h 5 min" -- oder 0 bei
    /// "Ending Soon". Zwei Zahlen sind Stunden und Minuten; eine Zahl ist Stunden, wenn ihr eine
    /// Stundeneinheit folgt (h, hour, Std ...), sonst Minuten.
    /// </summary>
    internal static (int? Minuten, bool Bald) Restzeit(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) { return (null, false); }
        if (GameText.Enthaelt(text, "ending_soon", "Ending Soon")) { return (0, true); }
        // Die Uhr vor der Zeit liest sich als "11 0" oder "O": es zaehlt die LETZTE Gruppe aus
        // Zahl + Einheit, notfalls mit einer zweiten Zahl + Einheit dahinter ("1 hour 59 mins").
        var gruppen = Regex.Matches(text, @"(\d{1,3})\s*(\p{L}[\p{L}.]*)(?:\s*(\d{1,2})\s*(\p{L}[\p{L}.]*))?", RegexOptions.CultureInvariant);
        if (gruppen.Count == 0) { return (null, false); }
        var m = gruppen[^1];
        var n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        if (m.Groups[3].Success) { return ((n * 60) + int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), false); }
        var einheit = m.Groups[2].Value.ToLowerInvariant();
        var minuten = GameText.Enthaelt(einheit, "unit_min", "min") || einheit.StartsWith('m') || einheit.StartsWith('λ');
        return (minuten ? n : n * 60, false);
    }

    /// <summary>Die Karten der Liste. Leere Plaetze (weniger als vier Auktionen) fehlen.</summary>
    public static List<AuktionsKarte> LiesKarten(Bitmap bild, WindowsOcr ocr)
    {
        var px = BoardReader.Pixel.Aus(bild);
        var raus = new List<AuktionsKarte>();
        for (var k = 0; k < Karten; k++)
        {
            var dy = KartenAbstand * k;
            // Leer? Die Karte ist weiss; ein leerer Platz ist der unscharfe dunkle Hintergrund.
            if (px.Hell(600, 250 + dy) < 120 && px.Hell(900, 300 + dy) < 120) { continue; }
            var name = Lies(ocr, px, Verschiebe(Name, dy), 1);
            var verblasst = Blass(px, Verschiebe(Name, dy));
            var piText = Lies(ocr, px, Verschiebe(KlassePi, dy), 2, hellAufDunkel: true);
            // Die PI ist die LETZTE dreistellige Zahl: davor stehen Klassenbuchstaben ("S2 839" sonst 2839).
            long? pi = piText is null ? null : Regex.Matches(piText.Replace('O', '0').Replace('o', '0'), @"\d{3}").LastOrDefault() is { } pm ? long.Parse(pm.Value, CultureInfo.InvariantCulture) : null;
            var abzeichen = Lies(ocr, px, Verschiebe(Abzeichen, dy), 3);
            var gebotText = Lies(ocr, px, Verschiebe(Gebot, dy), 2);
            var sofortText = Lies(ocr, px, Verschiebe(Sofort, dy), 2);
            var gebot = LiesBetrag(px, Verschiebe(Gebot, dy), gebotText);
            var sofort = LiesBetrag(px, Verschiebe(Sofort, dy), sofortText);
            var zeit = Lies(ocr, px, Verschiebe(Zeit, dy), 2);
            var (minuten, bald) = Restzeit(zeit);
            var eigen = Lies(ocr, px, Verschiebe(Eigen, dy), 3) is { } e && GameText.Enthaelt(e, "owned", "OWNED");
            raus.Add(new AuktionsKarte
            {
                Platz = k,
                // Gegen die Modellnamen des Spiels berichtigt (AuctionNames): ein Verleser ("ESSENZA SCVu")
                // waere sonst ein zweiter Eintrag derselben Auktion. Roh behaelt, was gelesen wurde.
                Auto = string.IsNullOrWhiteSpace(name) ? null : AuctionNames.Berichtige(name),
                Pi = pi is >= 100 and <= 999 ? (int)pi : null,
                Status = StatusAus(abzeichen, verblasst, ocr, px, dy),
                Gebot = gebot,
                Sofortkauf = sofort,
                RestMinuten = minuten,
                BaldZuEnde = bald,
                Eigen = eigen,
                Markiert = KarteMarkiert(px, dy),
                Verblasst = verblasst,
                Roh = string.Join(" | ", name, piText, abzeichen, gebotText, sofortText, zeit),
            });
        }
        return raus;
    }

    /// <summary>Lernen aus Lesungen der Texterkennung (sonst nur im Datenordner gespeichert).</summary>
    internal static bool Lernen { get; set; } = true;

    /// <summary>
    /// Ein Betrag: was die Texterkennung liest -- und daraus lernen; liest sie nichts (bei
    /// "10,000,000" verlaesslich), die Ziffernvorlagen.
    /// </summary>
    private static long? LiesBetrag(BoardReader.Pixel px, Rectangle feld, string? text)
    {
        var gelesen = Zahl(text);
        if (gelesen is { } wert)
        {
            if (Lernen) { AuctionDigits.Lerne(px, feld, wert, speichern: !LernenNurImSpeicher); }
            return wert;
        }
        return AuctionDigits.Lies(px, feld);
    }

    /// <summary>Beim Bau des Grundstocks: Vorlagen nur sammeln, nicht in den Datenordner schreiben.</summary>
    internal static bool LernenNurImSpeicher { get; set; }

    private static string? StatusAus(string? abzeichen, bool verblasst, WindowsOcr ocr, BoardReader.Pixel px, int dy)
    {
        foreach (var (key, en) in new[] { ("outbid", "OUTBID"), ("winning", "WINNING"), ("won", "WON"), ("lost", "LOST"), ("sold", "SOLD") })
        {
            if (abzeichen is not null && GameText.Aehnlich(abzeichen, key, en, 0.7)) { return en; }
        }
        if (verblasst)
        {
            // Der "SOLD!"-Aufkleber ueber dem Bild (gelb, schraeg) -- oder sonst ein beendeter Eintrag.
            var aufkleber = Lies(ocr, px, new Rectangle(55, 200 + dy, 140, 50), 2);
            if (aufkleber is not null && GameText.Enthaelt(aufkleber, "not_sold", "NOT SOLD")) { return "NOT_SOLD"; }
            return aufkleber is not null && GameText.Enthaelt(aufkleber, "sold", "SOLD") ? "SOLD" : "ENDED";
        }
        return null;
    }

    /// <summary>Ist der Name grau statt schwarz? So sieht ein beendeter Eintrag aus.</summary>
    private static bool Blass(BoardReader.Pixel px, Rectangle r)
    {
        int dunkel = 0, grau = 0;
        for (var y = r.Top; y < r.Bottom; y += 2)
        {
            for (var x = r.Left; x < r.Right; x += 2)
            {
                var l = px.Hell(x, y);
                if (l < 90) { dunkel++; } else if (l < 215) { grau++; }
            }
        }
        return grau > 40 && dunkel < grau / 4;
    }

    /// <summary>Der gelbe Rahmen um die gewaehlte Karte: oben und unten fast durchgehend Markierungsfarbe.</summary>
    private static bool KarteMarkiert(BoardReader.Pixel px, int dy)
    {
        int Kante(int y)
        {
            var n = 0;
            for (var x = 120; x < 900; x += 6)
            {
                for (var d = -6; d <= 6; d++)
                {
                    var (r, g, b) = px.Farbe(x, y + d);
                    if (ScanScreen.IstMarkierung(r, g, b)) { n++; break; }
                }
            }
            return n;
        }
        return Kante(160 + dy) > 100 && Kante(357 + dy) > 100;
    }

    private static Rectangle Verschiebe(Rectangle r, int dy) => new(r.X, r.Y + dy, r.Width, r.Height);

    /// <summary>Ein Feld als dunkle Schrift auf Weiss lesen: Tinte ist, was sich vom Feldgrund (Median) abhebt.</summary>
    private static string? Lies(WindowsOcr ocr, BoardReader.Pixel px, Rectangle r, int faktor, bool hellAufDunkel = false)
    {
        const int rand = 16;
        using var bild = new Bitmap((r.Width + (2 * rand)) * faktor, (r.Height + (2 * rand)) * faktor, PixelFormat.Format24bppRgb);
        var werte = new List<int>(r.Width * r.Height / 4);
        for (var y = r.Top; y < r.Bottom; y += 2) { for (var x = r.Left; x < r.Right; x += 2) { werte.Add(px.Hell(x, y)); } }
        werte.Sort();
        var grund = werte.Count > 0 ? werte[werte.Count / 2] : 255;
        var d = bild.LockBits(new Rectangle(0, 0, bild.Width, bild.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            var z = new byte[d.Stride * bild.Height];
            Array.Fill(z, (byte)255);
            for (var y = 0; y < r.Height; y++)
            {
                for (var x = 0; x < r.Width; x++)
                {
                    bool tinte;
                    if (hellAufDunkel)
                    {
                        var (rr, gg, bb) = px.Farbe(r.X + x, r.Y + y);
                        tinte = (rr + gg + bb) / 3 > 150 && Math.Abs(rr - gg) < 40 && Math.Abs(gg - bb) < 40;
                    }
                    else
                    {
                        tinte = Math.Abs(px.Hell(r.X + x, r.Y + y) - grund) > 70;
                    }
                    if (!tinte) { continue; }
                    for (var fy = 0; fy < faktor; fy++)
                    {
                        for (var fx = 0; fx < faktor; fx++)
                        {
                            var i = (((rand + y) * faktor + fy) * d.Stride) + (((rand + x) * faktor + fx) * 3);
                            z[i] = z[i + 1] = z[i + 2] = 0;
                        }
                    }
                }
            }
            System.Runtime.InteropServices.Marshal.Copy(z, 0, d.Scan0, z.Length);
        }
        finally
        {
            bild.UnlockBits(d);
        }
        var zeilen = ocr.Read(bild);
        return zeilen.Count == 0 ? null : string.Join(" ", zeilen.OrderBy(l => l.X).Select(l => l.Text));
    }
}
