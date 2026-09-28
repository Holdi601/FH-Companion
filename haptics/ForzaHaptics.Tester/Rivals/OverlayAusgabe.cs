using System.Drawing;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>Ein Overlay, das auch im Aufnahmefenster erscheinen kann.</summary>
internal interface IAufnahmeQuelle
{
    /// <summary>Soll es gerade zu sehen sein -- ueber dem Spiel oder nur im Aufnahmefenster.</summary>
    bool Gewollt { get; }

    /// <summary>Reihenfolge im Aufnahmefenster: kleinere Zahl weiter unten.</summary>
    int Ebene { get; }

    /// <summary>In Koordinaten der Spielflaeche zeichnen (0,0 = linke obere Ecke des Spiels).</summary>
    void MaleFuerAufnahme(Graphics g);

    /// <summary>Die Sichtbarkeit ueber dem Spiel neu anwenden -- der Schalter wurde umgelegt.</summary>
    void AusgabeAnwenden();
}

/// <summary>
/// Wo die Overlays erscheinen: ueber dem Spiel, im Aufnahmefenster, oder beides.
/// </summary>
/// <remarks>
/// ## Warum es ein Aufnahmefenster gibt (seit 2026-09-28)
///
/// Die Overlays halten sich aus jeder Bildschirmaufnahme heraus
/// (WDA_EXCLUDEFROMCAPTURE) -- sonst saehe der eigene Bildschirmleser sie und laese
/// das Klassenabzeichen hinter dem Panel nicht mehr. Die Folge: in OBS oder der
/// Xbox Game Bar fehlen sie. Das Aufnahmefenster zeichnet dieselben Overlays in ein
/// gewoehnliches Fenster auf einer Schluesselfarbe; OBS nimmt es als Fensteraufnahme
/// und stanzt die Farbe aus. Die Overlays ueber dem Spiel bleiben dabei unsichtbar
/// fuer Aufnahmen, der Bildschirmleser liest weiter richtig.
///
/// ## Warum sich die Overlays ueber dem Spiel ganz abschalten lassen
///
/// Jedes Fenster ueber einem Vollbildspiel kann Windows dazu bringen, das Spielbild
/// ueber den Fenstermanager zusammenzusetzen, statt es direkt anzuzeigen. Wer das
/// ausschliessen will, schaltet die Overlays ueber dem Spiel ab: dann liegt NICHTS
/// ueber Forza, und das Aufnahmefenster -- etwa auf dem zweiten Schirm -- zeigt sie.
/// </remarks>
internal static class OverlayAusgabe
{
    private static readonly List<WeakReference<IAufnahmeQuelle>> Quellen = new();

    /// <summary>Werden die Overlays ueber dem Spiel gezeigt? (Einstellung overlay_in_game)</summary>
    public static bool ImSpiel { get; private set; } = true;

    /// <summary>Die Spielflaeche in Bildschirmkoordinaten -- der Massstab des Aufnahmefensters.</summary>
    public static Rectangle Flaeche { get; set; } = new(0, 0, 1920, 1080);

    /// <summary>
    /// Ein Hinweis fuers Dashboard, solange nichts zu zeigen ist -- etwa "warte auf
    /// Telemetrie an 192.168.1.20:5300" (Konsolenmodus, seit 2026-09-28). null: keiner.
    /// </summary>
    public static string? Hinweis { get; set; }

    /// <summary>Wann das letzte Paket kam (fuer den Hinweis im Dashboard).</summary>
    public static DateTime LetztesPaket { get; set; } = DateTime.MinValue;

    public static void Melde(IAufnahmeQuelle quelle)
    {
        lock (Quellen)
        {
            Quellen.RemoveAll(w => !w.TryGetTarget(out var q) || q is System.Windows.Forms.Control { IsDisposed: true });
            Quellen.Add(new WeakReference<IAufnahmeQuelle>(quelle));
        }
    }

    /// <summary>Alle lebenden Overlays, von unten nach oben.</summary>
    public static List<IAufnahmeQuelle> Alle()
    {
        var alle = new List<IAufnahmeQuelle>();
        lock (Quellen)
        {
            foreach (var w in Quellen)
            {
                if (w.TryGetTarget(out var q) && q is not System.Windows.Forms.Control { IsDisposed: true })
                {
                    alle.Add(q);
                }
            }
        }
        return alle.OrderBy(q => q.Ebene).ToList();
    }

    public static void SetzeImSpiel(bool an)
    {
        if (ImSpiel == an) { return; }
        ImSpiel = an;
        foreach (var q in Alle())
        {
            try { q.AusgabeAnwenden(); } catch (Exception) { }
        }
    }
}
