using System.Drawing;
using System.Windows.Forms;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Die eigene Notiz zum gerade gewaehlten Auto, ueber dem Spiel.
/// </summary>
/// <remarks>
/// ## Wann sie steht und wann nicht
///
/// Sie steht, solange die Telemetrie DIESES Auto meldet. Wechselt der Wagen unter
/// dem Auswahlrahmen, verschwindet die alte Notiz sofort -- eine Notiz, die noch
/// vom vorigen Auto stammt, waere schlimmer als keine: man liest sie und haelt sie
/// fuer die Auskunft zum neuen.
///
/// Darum haelt dieses Fenster KEINEN eigenen Verfallszaehler. Es zeigt, was ihm
/// zuletzt gesagt wurde, und der Aufrufer sagt ihm beim Autowechsel sofort etwas
/// anderes oder nichts.
///
/// ## Warum das Spiel ueberhaupt etwas sendet, waehrend man waehlt
///
/// Forza sendet auch in Garage und Autoauswahl Pakete, und darin stehen Kennung,
/// PI, Antrieb und Drehzahlen des markierten Wagens. Genau daraus entsteht der
/// Fingerabdruck -- siehe <see cref="CarNotes"/>.
/// </remarks>
internal sealed class CarNoteHud : LayeredHud
{
    private readonly OverlaySettings _settings;
    private string _titel = string.Empty;
    private string _text = string.Empty;

    // Fenster, Alpha je Pixel, Aufnahme-Ausblendung: LayeredHud. Bis zum 2026-09-25
    // stand das hier selbst -- und das Fenster war dadurch nie zu sehen (siehe dort).
    public CarNoteHud(OverlaySettings settings, Rectangle screen) : base(screen)
    {
        _settings = settings;
    }

    /// <summary>Was gerade gilt. Leerer Text heisst: nichts zeigen.</summary>
    public void SetNote(string titel, string text)
    {
        if (_titel == titel && _text == text) { return; }
        _titel = titel ?? string.Empty;
        _text = text ?? string.Empty;
        Render();
    }

    public bool HasNote => !string.IsNullOrWhiteSpace(_text);

    /// <summary>Die Vorschau zeigt die Notiz auch bei abgeschalteten Notizen.</summary>
    public void SetPreview(bool an) { _preview = an; Render(); }

    private bool _preview;

    protected override Rectangle Extent(Graphics messung)
    {
        if ((!_settings.CarNotes && !_preview) || string.IsNullOrWhiteSpace(_text))
        {
            return Rectangle.Empty;
        }
        return Rectangle.Ceiling(Lege(messung, _settings, AreaSize, _titel, _text).Kasten);
    }

    protected override void Draw(Graphics g) =>
        Male(g, _settings, AreaSize, _titel, _text, auchWennAus: _preview);

    public new void Paint(Graphics g) => Paint(g, _titel, _text);

    /// <summary>Zeichnen. Text von aussen, damit der Einrichtungsreiter ihn stellt.</summary>
    public void Paint(Graphics g, string titel, string text) =>
        Male(g, _settings, AreaSize, titel, text);

    /// <summary>Wo die Notiz liegt und wie gross ihre Schrift ist.</summary>
    internal readonly record struct Aufbau(RectangleF Kasten, float KopfPunkt, float TextPunkt,
                                           float KopfHoehe, float TextHoehe);

    /// <summary>
    /// Die Lage der Notiz auf einer Spielflaeche dieser Groesse -- EINE Rechnung fuer
    /// das Overlay UND die Vorschau im Reiter "Lap delta HUD".
    /// </summary>
    /// <remarks>
    /// Bis zum 2026-09-25 rechnete die Vorschau selbst: den Kasten wie hier, den Text
    /// aber mit der grossen Schrift der Delta-Zahl und ohne Umbruch. Der Text lief
    /// weit aus dem Kasten, und angefasst wurde ein kleines Rechteck, das nichts mit
    /// dem Sichtbaren zu tun hatte. Die Vorschau zeichnet jetzt mit genau dieser
    /// Funktion, auf die echte Flaeche gerechnet und dann verkleinert.
    /// </remarks>
    internal static Aufbau Lege(Graphics g, OverlaySettings s, Size flaeche, string titel,
                                string text)
    {
        var platz = s.CarNotePlacement;
        // 1080p-Masse, wie beim Umriss-Overlay: sonst ist die Notiz auf 8K ein
        // Briefmarkenrest und auf 720p ein halber Schirm.
        var aufloesung = GameArea.ResolutionScale(new Rectangle(Point.Empty, flaeche));
        var breite = Math.Max(120, (int)(s.CarNoteWidth * platz.Scale * aufloesung));
        var kopfPunkt = Math.Max(7f, 10f * platz.Scale * aufloesung);
        var textPunkt = Math.Max(7f, 9.5f * platz.Scale * aufloesung);
        using var kopfSchrift = Schrift("Segoe UI Semibold", kopfPunkt);
        using var textSchrift = Schrift("Segoe UI", textPunkt);

        // Erst messen, dann legen: die Hoehe haengt am Umbruch, und ein Kasten, der
        // vor dem Messen gelegt wird, schneidet lange Notizen ab.
        using var format = new StringFormat { Trimming = StringTrimming.Word };
        var textHoehe = g.MeasureString(text, textSchrift, breite - 16, format).Height;
        var kopfHoehe = string.IsNullOrWhiteSpace(titel) ? 0 : kopfSchrift.GetHeight(g) + 2;
        var hoehe = (int)(textHoehe + kopfHoehe + 16);

        var x = (int)(platz.X * flaeche.Width);
        var y = (int)(platz.Y * flaeche.Height);
        x = platz.Align.ToLowerInvariant() switch
        {
            "left" => x,
            "right" => x - breite,
            _ => x - (breite / 2),
        };
        x = Math.Max(0, Math.Min(x, flaeche.Width - breite));
        y = Math.Max(0, Math.Min(y, flaeche.Height - hoehe));
        return new Aufbau(new RectangleF(x, y, breite, hoehe), kopfPunkt, textPunkt,
                          kopfHoehe, textHoehe);
    }

    // In PIXELN (Punkt mal 96/72): eine Punktschrift haengt an der DPI der
    // Zeichenflaeche, und Vorschau (Schirm) und Overlay (Bitmap, 96 DPI) liefen
    // auf einem skalierten Schirm sonst auseinander. Siehe CourseShapeHud.Schrift.
    private static Font Schrift(string familie, float punkt) =>
        new(familie, punkt * 96f / 72f, GraphicsUnit.Pixel);

    /// <summary>Die Notiz auf eine Spielflaeche dieser Groesse zeichnen.</summary>
    /// <param name="auchWennAus">
    /// Die Vorschau zeichnet sie auch bei abgeschalteten Notizen -- sonst waere dort
    /// nur ein leerer Kasten, den man verschiebt, ohne zu sehen, wie gross er ist.
    /// </param>
    internal static void Male(Graphics g, OverlaySettings s, Size flaeche, string titel,
                              string text, bool auchWennAus = false)
    {
        if ((!s.CarNotes && !auchWennAus) || string.IsNullOrWhiteSpace(text)) { return; }
        var a = Lege(g, s, flaeche, titel, text);
        var k = a.Kasten;

        var grund = OverlaySettings.ParseColour(s.CarNoteBack, Color.FromArgb(13, 17, 23));
        var tinte = OverlaySettings.ParseColour(s.CarNoteInk, Color.FromArgb(231, 236, 243));
        var kopfFarbe = OverlaySettings.ParseColour(s.CarNoteTitle,
                                                    Color.FromArgb(127, 211, 255));

        var alpha = Math.Max(0, Math.Min(255, s.CarNoteBackAlpha));
        if (alpha > 0)
        {
            using var platte = new SolidBrush(Color.FromArgb(alpha, grund));
            g.FillRectangle(platte, k);
        }

        using var kopfSchrift = Schrift("Segoe UI Semibold", a.KopfPunkt);
        using var textSchrift = Schrift("Segoe UI", a.TextPunkt);
        using var format = new StringFormat { Trimming = StringTrimming.Word };
        var yy = k.Y + 8f;
        if (a.KopfHoehe > 0)
        {
            using var kopfPinsel = new SolidBrush(kopfFarbe);
            g.DrawString(titel, kopfSchrift, kopfPinsel, k.X + 8, yy);
            yy += a.KopfHoehe;
        }
        using var pinsel = new SolidBrush(tinte);
        g.DrawString(text, textSchrift, pinsel,
                     new RectangleF(k.X + 8, yy, k.Width - 16, a.TextHoehe + 2), format);
    }
}
