using System.Drawing;
using ForzaHaptics.Tester.Rivals;

namespace ForzaHaptics.Tester.Scan;

/// <summary>
/// Ja/Nein-Dialoge des Spiels sicher mit "No" beantworten -- und die Markierung einer Kachel pruefen.
/// </summary>
/// <remarks>
/// Am 2026-10-04 stand die Automatik vor "SETUP FILE LOCKED -- Would you like to remove the setup?"
/// mit vorgewaehltem "Yes": ein ENTER haette das Tune vom Auto des Nutzers entfernt. Die alte Regel
/// "LINKS, dann ENTER" (fuer waagerechte Dialoge gedacht) haette auf diesem senkrechten Dialog genau
/// das getan. Jetzt: "No" suchen, den Rahmen darauf bringen, ENTER nur mit gepruefter Markierung.
/// </remarks>
internal static class ScanDialog
{
    /// <summary>Ein Ja/Nein-Dialog: eine Zeile "Yes" und eine Zeile "No" (in der Spielsprache).</summary>
    public static bool IstJaNein(IReadOnlyList<OcrLine> zeilen) =>
        zeilen.Any(z => GameText.Gleich(z.Text, "yes", "Yes")) && zeilen.Any(z => IstNein(z.Text));

    private static bool IstNein(string text) => GameText.Gleich(text, "no", "No");

    /// <summary>
    /// Ist die Dialogzeile markiert? Der gelbe Rahmen steht links und rechts der Zeile, auf ihrer Hoehe.
    /// </summary>
    internal static bool ZeileMarkiert(Bitmap bild, OcrLine zeile)
    {
        var px = BoardReader.Pixel.Aus(bild);
        var y = (int)(zeile.Y + (zeile.H > 0 ? zeile.H / 2 : 12));
        var links = 0;
        var rechts = 0;
        for (var x = 560; x < 700; x++)
        {
            var (r, g, b) = px.Farbe(x, y);
            if (ScanScreen.IstMarkierung(r, g, b)) { links++; }
        }
        for (var x = 1220; x < 1360; x++)
        {
            var (r, g, b) = px.Farbe(x, y);
            if (ScanScreen.IstMarkierung(r, g, b)) { rechts++; }
        }
        return links >= 2 && rechts >= 2;
    }

    /// <summary>
    /// "No" waehlen und bestaetigen. Erst RUNTER (senkrechter Dialog), dann LINKS (waagerechter), nach
    /// jedem Druck nachsehen; ENTER nur, wenn "No" markiert ist. Sonst ESC. True, wenn ENTER auf "No" fiel.
    /// </summary>
    public static bool NeinWaehlen(ScanEingabe eingabe, WindowsOcr ocr, Action<string> melden) =>
        Antworten(eingabe, ocr, melden, ja: false, nachher: 1500);

    /// <summary>
    /// "Yes" waehlen und bestaetigen -- nur fuer Fragen, deren Ja nichts kostet (die Schnellreise zum
    /// Festivalgelaende). Dieselbe Regel wie bei "No": ENTER nur, wenn "Yes" markiert ist, sonst ESC.
    /// </summary>
    public static bool JaWaehlen(ScanEingabe eingabe, WindowsOcr ocr, Action<string> melden, int nachher) =>
        Antworten(eingabe, ocr, melden, ja: true, nachher: nachher);

    private static bool Antworten(ScanEingabe eingabe, WindowsOcr ocr, Action<string> melden, bool ja, int nachher)
    {
        // Senkrecht steht "Yes" oben, waagerecht rechts -- fuer "No" umgekehrt.
        var tasten = ja
            ? new byte[] { 0, ScanEingabe.Hoch, ScanEingabe.Rechts, ScanEingabe.Hoch, ScanEingabe.Links }
            : new byte[] { 0, ScanEingabe.Runter, ScanEingabe.Links, ScanEingabe.Runter, ScanEingabe.Rechts };
        var wort = ja ? "Yes" : "No";
        foreach (var taste in tasten)
        {
            if (taste != 0) { eingabe.Taste(taste, 600); }
            using var bild = ScanEingabe.Aufnahme();
            var zeilen = ocr.Read(bild);
            if (!IstJaNein(zeilen)) { return false; }
            var ziel = zeilen.First(z => ja ? GameText.Gleich(z.Text, "yes", "Yes") : IstNein(z.Text));
            if (ZeileMarkiert(bild, ziel))
            {
                melden("dialog '" + string.Join(" ", zeilen.Take(3).Select(z => z.Text)) + "': " + wort);
                eingabe.Taste(ScanEingabe.Enter, nachher);
                return true;
            }
        }
        melden($"dialog: could not put the frame on {wort} -- ESC");
        eingabe.Taste(ScanEingabe.Esc, 1500);
        return false;
    }

    /// <summary>
    /// Ist die Kachel mit diesem Text markiert? Unter einer markierten Kachel folgt auf ihre Unterkante
    /// ein schwarzes Band (~7 px), dann die gelbe Rahmenlinie (2-6 px), dann der Seitengrund. Eine
    /// unmarkierte Kachel geht direkt in den Seitengrund ueber. Farbe allein taugt nicht: Kacheln
    /// sind selbst gelb (Horizon Play) oder limettengruen (Convoy, Buy New &amp; Used Cars) -- gemessen
    /// 2026-10-04: markiert 25 von 25 Spalten, unmarkiert hoechstens 10.
    /// </summary>
    internal static bool KachelMarkiert(Bitmap bild, OcrLine text)
    {
        var px = BoardReader.Pixel.Aus(bild);
        int treffer = 0, spalten = 0;
        var y0 = (int)text.Y + 20;
        var y1 = Math.Min(1075, (int)text.Y + 320);
        for (var x = (int)text.X; x < (int)text.X + 250; x += 10)
        {
            spalten++;
            bool Dunkel(int y) => px.Hell(x, y) < 60;
            bool Mark(int y)
            {
                var (r, g, b) = px.Farbe(x, y);
                return ScanScreen.IstMarkierung(r, g, b);
            }
            for (var y = y0; y < y1 - 14; y++)
            {
                if (!(Dunkel(y) && Dunkel(y + 1) && Dunkel(y + 2) && Dunkel(y + 3))) { continue; }
                var j = y + 4;
                while (j < y1 && Dunkel(j)) { j++; }
                var luecke = 0;
                while (j < y1 && !Mark(j) && luecke < 3) { j++; luecke++; }
                var k = j;
                while (k < y1 && Mark(k)) { k++; }
                if (k - j is >= 2 and <= 6 && k < y1 && !Mark(k)) { treffer++; break; }
            }
        }
        return spalten > 0 && treffer * 5 >= spalten * 4;
    }
}
