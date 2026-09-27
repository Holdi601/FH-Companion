namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Der Ton zur Feier (CelebrationHud): zwei Knalle, ein aufsteigender Glockenklang,
/// ein wenig Funkeln -- gut anderthalb Sekunden.
/// </summary>
/// <remarks>
/// Im Programm GERECHNET statt als Datei mitgeliefert: keine Lizenzfrage, nichts,
/// was im Paket fehlen kann, und die Lautstaerke steht hier fest. Spitze bei 32 %
/// des Vollausschlags -- der Ton spielt ueber einem laufenden Rennen und soll es
/// nicht uebertoenen. Abschaltbar fuer sich (celebrate_sound).
/// </remarks>
internal static class CelebrationSound
{
    internal const int Rate = 44100;
    internal const float Spitze = 0.32f;

    private static readonly Lazy<byte[]> _wav = new(() => AlsWav(Proben()));
    private static System.Media.SoundPlayer? _spieler;

    /// <summary>Der Ton als WAV (16 Bit, mono).</summary>
    public static byte[] Wav => _wav.Value;

    /// <summary>Abspielen, ohne zu warten. Ein Fehler (kein Ausgabegeraet) bleibt still.</summary>
    public static void Play()
    {
        try
        {
            _spieler?.Stop();
            _spieler?.Dispose();
            // Das Objekt BEHALTEN: es haelt den Puffer, aus dem Windows gerade spielt.
            _spieler = new System.Media.SoundPlayer(new MemoryStream(Wav, writable: false));
            _spieler.Play();
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Die Proben, -1..1, auf <see cref="Spitze"/> gebracht.</summary>
    internal static float[] Proben()
    {
        var y = new float[(int)(Rate * 2.0)];

        // Zwei Knalle -- die beiden Knallbonbons, links zuerst (siehe Konfetti).
        Knall(y, 0.05, 1.0f, 11);
        Knall(y, 0.11, 0.8f, 23);

        // C-Dur aufwaerts, der letzte Ton klingt lange nach.
        double[] noten = { 523.25, 659.25, 783.99, 1046.50 };
        for (var i = 0; i < noten.Length; i++)
        {
            var letzter = i == noten.Length - 1;
            Glocke(y, 0.17 + (i * 0.085), noten[i], letzter ? 0.6 : 0.36, letzter ? 0.9f : 0.65f);
        }
        Glocke(y, 0.43, 1318.51, 0.55, 0.22f);
        Glocke(y, 0.445, 1567.98, 0.55, 0.18f);

        // Funkeln: hohe Pentatonik, leise.
        var r = new Random(7);
        double[] funken = { 2093.0, 2349.3, 2637.0, 3136.0, 3520.0 };
        for (var i = 0; i < 9; i++)
        {
            Glocke(y, 0.55 + (i * 0.1) + (r.NextDouble() * 0.04), funken[r.Next(funken.Length)], 0.07, 0.12f);
        }

        var hoechst = 0f;
        foreach (var v in y) { hoechst = Math.Max(hoechst, Math.Abs(v)); }
        var faktor = hoechst > 0 ? Spitze / hoechst : 0f;
        // Sanft auslaufen lassen statt abzuschneiden: die letzten 0,3 s.
        var auslauf = (int)(Rate * 0.3);
        for (var i = 0; i < y.Length; i++)
        {
            var ende = Math.Min(1f, (y.Length - i) / (float)auslauf);
            y[i] *= faktor * ende;
        }
        return y;
    }

    /// <summary>Ein Glockenton: Grundton mit zwei schneller verklingenden Obertoenen.</summary>
    private static void Glocke(float[] y, double beginn, double f, double tau, float lautheit)
    {
        var start = (int)(beginn * Rate);
        var ende = Math.Min(y.Length, start + (int)(Rate * tau * 6));
        for (var i = Math.Max(0, start); i < ende; i++)
        {
            var t = (i - start) / (double)Rate;
            var huelle = (1 - Math.Exp(-t / 0.004)) * Math.Exp(-t / tau);
            var w = 2 * Math.PI * f * t;
            var klang = Math.Sin(w)
                        + (0.35 * Math.Sin(2 * w) * Math.Exp(-t / (tau * 0.5)))
                        + (0.15 * Math.Sin(3.01 * w) * Math.Exp(-t / (tau * 0.35)));
            y[i] += (float)(lautheit * huelle * klang);
        }
    }

    /// <summary>Ein Knall: ein kurzes Rauschen ueber einem tiefen, fallenden Schlag.</summary>
    private static void Knall(float[] y, double beginn, float lautheit, int saat)
    {
        var r = new Random(saat);
        var start = (int)(beginn * Rate);
        var ende = Math.Min(y.Length, start + (int)(Rate * 0.16));
        double tief = 0, phase = 0;
        for (var i = Math.Max(0, start); i < ende; i++)
        {
            var t = (i - start) / (double)Rate;
            // Rauschen durch einen einfachen Tiefpass: weicher als weisses Rauschen.
            tief += 0.35 * ((r.NextDouble() * 2) - 1 - tief);
            var rauschen = tief * Math.Exp(-t / 0.012);
            phase += 2 * Math.PI * (70 + (90 * Math.Exp(-t / 0.03))) / Rate;
            var schlag = Math.Sin(phase) * Math.Exp(-t / 0.035);
            y[i] += (float)(lautheit * ((0.9 * rauschen) + (0.7 * schlag)));
        }
    }

    internal static byte[] AlsWav(float[] proben)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var daten = proben.Length * 2;
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + daten);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);      // PCM
        w.Write((short)1);      // mono
        w.Write(Rate);
        w.Write(Rate * 2);      // Bytes je Sekunde
        w.Write((short)2);      // Bytes je Probe
        w.Write((short)16);
        w.Write("data"u8.ToArray());
        w.Write(daten);
        foreach (var v in proben)
        {
            w.Write((short)Math.Clamp((int)Math.Round(v * 32767f), short.MinValue, short.MaxValue));
        }
        w.Flush();
        return ms.ToArray();
    }
}
