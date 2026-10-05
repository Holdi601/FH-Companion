namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Der Verlauf der letzten Sekunden an Pedalen und Lenkung -- meiner und der der
/// Bestzeit.
/// </summary>
/// <remarks>
/// Fuenf Kanaele in fester Reihenfolge: Gas, Bremse, Kupplung, Lenkung, Gang.
///
/// NUR MEINE WERTE. Die der Bestzeit werden nicht mitgeschrieben, sondern beim
/// Zeichnen aus der Referenzrunde geholt -- sonst liesse sich ihre ZUKUNFT nicht
/// zeigen, und genau die ist der Grund, warum jemand auf diese Spuren sieht: was
/// gleich kommt, kann man nachfahren.
///
/// Meine Spur endet zwangslaeufig am jetzigen Punkt.
///
/// Die Laenge richtet sich nach der eingestellten Zeitspanne. Gefuettert wird im Takt
/// der Anzeige (rund zehnmal je Sekunde), nicht im Takt der Telemetrie: mehr Punkte
/// als Pixel bringen nichts.
/// </remarks>
internal sealed class InputTrace
{
    private const int Channels = 5;
    private const double FeedsPerSecond = 10;

    private readonly List<float?>[] _mine = Make();

    private static List<float?>[] Make()
    {
        var listen = new List<float?>[Channels];
        for (var i = 0; i < Channels; i++) { listen[i] = new List<float?>(); }
        return listen;
    }

    public IReadOnlyList<float?> Mine(int channel) => _mine[channel];

    public void Clear()
    {
        foreach (var liste in _mine) { liste.Clear(); }
    }

    public void Push(float throttle, float brake, float clutch, float steer,
                     float gear, float seconds)
    {
        var behalten = Math.Max(8, (int)Math.Round(
            Math.Clamp(seconds, 1, 60) * FeedsPerSecond));

        Add(_mine[0], throttle, behalten);
        Add(_mine[1], brake, behalten);
        Add(_mine[2], clutch, behalten);
        Add(_mine[3], steer, behalten);
        Add(_mine[4], gear, behalten);
    }

    private static void Add(List<float?> liste, float? wert, int behalten)
    {
        liste.Add(wert);
        // Vorne abraeumen, nicht die ganze Liste neu bauen: das hier laeuft, waehrend
        // jemand faehrt.
        var zuviel = liste.Count - behalten;
        if (zuviel > 0) { liste.RemoveRange(0, zuviel); }
    }
}
