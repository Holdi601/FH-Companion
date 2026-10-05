using System.Drawing;

namespace ForzaHaptics.Tester.Scan;

/// <summary>
/// Der Scrollbalken der Bestenliste -- der einzige direkte Beleg, dass eine Liste zu Ende ist.
/// </summary>
/// <remarks>
/// Der Balken ist eine dunkle senkrechte Linie rechts neben der Tabelle (1080p: x ~1773-1780,
/// y 277-868), der Daumen ein WEISSES Stueck darin -- bei langen Listen nur ein Punkt von ~7 px.
///
/// Gemessen wird, was zu sehen ist: die Spalte mit der laengsten zusammenhaengenden Folge aus
/// "dunkel oder weiss" ist der Balken; die weissen Punkte darin sind der Daumen. Die erste Fassung
/// (uebertragen aus scripts/detect_leaderboard_scrollbar.py) verglich den Balken mit seinen
/// Nachbarspalten -- links davon liegen aber die weissen Tabellenzeilen, und am 2026-10-04 galt der
/// ganze Balken als Daumen: "Ende der Liste" bei Platz 1.702 von rund 18.000. Das Python-Werkzeug
/// las dasselbe Bild genauso falsch.
///
/// "Keine Zeilen mehr" ist nie ein Ende ([[no-rows-is-not-board-end]]): erst der Daumen ganz unten.
///
/// DUNKEL UND WEISS GEGEN DEN SEITENGRUND, nicht als feste Helligkeit: die erste Fassung (dunkel unter
/// 120, weiss ueber 200) war an Bildern mit HDR gemessen -- Seitengrund ~150, Balken ~81. Ohne HDR ist
/// der Grund ~93 und der Balken ~58 (Aufnahme vom 2026-08-21): dann war der GRUND "dunkel", eine
/// Spalte daneben schlug den Balken, kein Daumen, nie ein Ende -- jedes Board haette als abgebrochen
/// gegolten (Pruefung vom 2026-10-04). In beiden Faellen ist der Balken gut 0,6 mal so hell wie der Grund.
/// </remarks>
internal static class ScanScrollbar
{
    internal sealed record Messung(bool Gefunden, double Position, bool Unten, int BalkenOben, int BalkenUnten, int DaumenOben, int DaumenUnten);

    private const int Y0 = 255, Y1 = 895;

    public static Messung? Messen(Bitmap bild)
    {
        var px = BoardReader.Pixel.Aus(bild);
        // Der Seitengrund rechts neben dem Balken (links liegen die weissen Tabellenzeilen).
        var grund = new List<int>();
        for (var x = 1786; x <= 1805; x += 3)
        {
            for (var y = Y0; y <= Y1; y += 8) { grund.Add(px.Hell(x, y)); }
        }
        grund.Sort();
        var g = grund[grund.Count / 2];
        var dunkelGrenze = g - 20;
        var weissGrenze = Math.Min(235, g + 60);
        bool Dunkel(int x, int y) => px.Hell(x, y) < dunkelGrenze;
        bool Weiss(int x, int y) => px.Hell(x, y) > weissGrenze;

        // Die Spalte mit der laengsten Folge aus dunkel-oder-weiss, die mindestens zur Haelfte dunkel ist.
        (int X, int Oben, int Unten)? beste = null;
        for (var x = 1740; x <= 1805; x++)
        {
            int start = -1, dunkel = 0;
            for (var y = Y0; y <= Y1; y++)
            {
                var teil = Dunkel(x, y) || Weiss(x, y);
                if (teil)
                {
                    if (start < 0) { start = y; dunkel = 0; }
                    if (Dunkel(x, y)) { dunkel++; }
                }
                if ((!teil || y == Y1) && start >= 0)
                {
                    var ende = teil ? y : y - 1;
                    var laenge = ende - start + 1;
                    if (laenge >= 300 && dunkel * 2 >= laenge && (beste is null || laenge > beste.Value.Unten - beste.Value.Oben + 1))
                    {
                        beste = (x, start, ende);
                    }
                    start = -1;
                }
            }
        }
        if (beste is null) { return null; }
        var bx = beste.Value.X;
        // Die Enden: die AEUSSERSTEN dunklen oder weissen Punkte dieser Spalte, nicht die laengste Folge --
        // eine unscharfe Zeile zwischen Daumen und Balken haette sonst den Daumen zum unteren Ende gemacht
        // (ein falsches "Ende der Liste").
        var oben = Enumerable.Range(Y0, Y1 - Y0 + 1).First(y => Dunkel(bx, y) || Weiss(bx, y));
        var unten = Enumerable.Range(Y0, Y1 - Y0 + 1).Last(y => Dunkel(bx, y) || Weiss(bx, y));
        // Der Daumen: weisse Zeilen auf der Balkenmitte (eine Spalte daneben zaehlt mit, falls die Mitte verfehlt ist).
        // Der Daumen kann an einem Balkenende sitzen, durch eine unscharfe Zeile von der dunklen Folge
        // getrennt: 20 px ueber die Enden hinaus suchen und den Balken bis zu ihm verlaengern.
        var daumen = Enumerable.Range(Math.Max(Y0, oben - 20), Math.Min(Y1, unten + 20) - Math.Max(Y0, oben - 20) + 1)
                               .Where(y => Weiss(bx, y) || (Weiss(bx - 1, y) && Weiss(bx + 1, y))).ToList();
        if (daumen.Count == 0) { return new Messung(true, double.NaN, false, oben, unten, -1, -1); }
        int dOben = daumen.Min(), dUnten = daumen.Max();
        oben = Math.Min(oben, dOben);
        unten = Math.Max(unten, dUnten);
        var balkenH = unten - oben + 1;
        var weg = Math.Max(1, balkenH - (dUnten - dOben + 1));
        var position = Math.Round((dOben - oben) / (double)weg, 4);
        var toleranz = Math.Max(3.0, balkenH * 0.01);
        return new Messung(true, position, unten - dUnten <= toleranz, oben, unten, dOben, dUnten);
    }
}
