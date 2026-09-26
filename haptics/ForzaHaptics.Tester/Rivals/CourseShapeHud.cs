using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Die Umrisse der angebotenen Strecken, ueber dem Spiel.
/// </summary>
/// <remarks>
/// ## Warum ein eigenes Fenster und nicht im Empfehlungsstreifen
///
/// Der Nutzer will Ort, Groesse, Farben und Sichtbarkeit selbst bestimmen. Alles,
/// was im <see cref="OverlayPanel"/> steht, haengt dagegen an dessen Spalten und
/// waechst mit dessen Text. Ein eigenes Fenster ist genau das, was sich frei
/// schieben laesst.
///
/// ## Warum nicht in <see cref="DeltaHud"/>
///
/// Der Streifen wird ausgeblendet, sobald kein Rennen laeuft
/// (<c>if (!rennen) { HideHud(); }</c>) -- und die Streckenwahl ist genau die Zeit
/// VOR dem Rennen. Die Umrisse waeren dort nie zu sehen.
///
/// ## Durchklickbar und unsichtbar fuer Aufnahmen
///
/// Dieselben Fensterstile wie die anderen Overlays: WS_EX_TRANSPARENT (die Maus
/// geht hindurch), WS_EX_NOACTIVATE (kein Fokusklau mitten im Menue). Die
/// Aufnahme-Ausblendung ist wichtiger, als sie klingt: der Bildschirmleser
/// fotografiert den Schirm, und ein Overlay, das sich selbst mitfotografiert,
/// verdirbt die Erkennung -- das ist hier schon einmal passiert.
/// </remarks>
internal sealed class CourseShapeHud : LayeredHud
{
    /// <summary>Wo eine Strecke in der Meisterschaft steht.</summary>
    internal enum TileState { None, Done, Now, Next }

    private readonly OverlaySettings _settings;
    private List<(string Name, CourseShape.Outline? Shape)> _kurse = new();
    private List<TileState> _stati = new();

    // Fenster, Alpha je Pixel, Aufnahme-Ausblendung: LayeredHud. Bis zum 2026-09-25
    // stand das hier selbst -- und das Fenster war dadurch nie zu sehen (siehe dort).
    public CourseShapeHud(OverlaySettings settings, Rectangle screen) : base(screen)
    {
        _settings = settings;
    }

    /// <summary>Welche Strecken gerade angeboten werden -- und wie weit die Meisterschaft ist.</summary>
    public void SetCourses(IReadOnlyList<(string Name, CourseShape.Outline? Shape)> kurse,
                           IReadOnlyList<TileState>? stati = null)
    {
        _kurse = kurse.ToList();
        _stati = stati?.ToList() ?? new List<TileState>();
        Render();
    }

    /// <summary>Die Vorschau zeigt den Block auch bei abgeschalteten Umrissen.</summary>
    public void SetPreview(bool an) { _preview = an; Render(); }

    private bool _preview;

    protected override Rectangle Extent(Graphics messung) =>
        (_settings.CourseShapes || _preview) && _kurse.Count > 0
            ? Lege(_settings, AreaSize, _kurse.Count).Block
            : Rectangle.Empty;

    protected override void Draw(Graphics g) =>
        Male(g, _settings, AreaSize, _kurse, auchWennAus: _preview, stati: _stati);

    /// <summary>Zeichnen in eine beliebige Flaeche -- fuer Selbsttest und Vorschaubilder.</summary>
    public new void Paint(Graphics g) => Male(g, _settings, AreaSize, _kurse, stati: _stati);

    /// <summary>Lage des Blocks, Kachelgroesse, Fuge, Kopfzeile und Schriftgrad.</summary>
    internal readonly record struct Aufbau(Rectangle Block, int Kachel, int Fuge, int Kopf,
                                           float Punkt);

    /// <summary>
    /// Wo der Block auf einer Spielflaeche dieser Groesse liegt -- EINE Rechnung fuer
    /// das Overlay UND die Vorschau im Reiter "Lap delta HUD".
    /// </summary>
    /// <remarks>
    /// Bis zum 2026-09-25 zeichnete die Vorschau selbst: feste Schrift, keine
    /// Randklemme. Der Block lief dort rechts aus dem Bild, das Overlay aber nicht,
    /// und bei kleiner Groesse liefen die Namen ineinander. Jetzt ruft die Vorschau
    /// diese Funktion auf der echten Flaeche auf und verkleinert nur.
    /// </remarks>
    internal static Aufbau Lege(OverlaySettings s, Size flaeche, int anzahl)
    {
        var platz = s.CoursePlacement;
        // DIE MASSE SIND 1080p-MASSE: auf 4K doppelt, auf 8K vierfach.
        var aufloesung = GameArea.ResolutionScale(new Rectangle(Point.Empty, flaeche));
        var kachel = Math.Max(40, (int)(s.CourseShapeTile * platz.Scale * aufloesung));
        var punkt = Math.Max(8f, 9f * platz.Scale * aufloesung);
        using var schrift = Schrift(punkt);
        var kopf = (int)(schrift.Height * 1.4f);
        // Auch Fugen und Rand in 1080p-Massen, sonst kleben die Kacheln auf 8K aneinander.
        var fuge = Math.Max(2, (int)Math.Round(6 * aufloesung));

        // NEBENEINANDER oder UNTEREINANDER (seit 2026-09-25 waehlbar). Untereinander
        // passt der Block an den Rand des Bildes, wo neben der Streckenliste des Spiels
        // Platz ist; nebeneinander liegt er darueber.
        var breite = s.CourseVertical ? kachel + (2 * fuge)
                                      : (kachel * anzahl) + (fuge * (anzahl + 1));
        var hoehe = s.CourseVertical ? (anzahl * (kopf + kachel + fuge)) + fuge
                                     : kachel + kopf + (2 * fuge);

        var x0 = (int)(platz.X * flaeche.Width);
        var y0 = (int)(platz.Y * flaeche.Height);
        // "right" heisst: der Block endet hier, statt hier zu beginnen. Ohne das
        // laeuft ein rechts abgelegter Block aus dem Bild heraus, sobald eine
        // Strecke mehr angeboten wird.
        if (string.Equals(platz.Align, "right", StringComparison.OrdinalIgnoreCase))
        {
            x0 -= breite;
        }
        else if (string.Equals(platz.Align, "center", StringComparison.OrdinalIgnoreCase))
        {
            x0 -= breite / 2;
        }
        x0 = Math.Max(0, Math.Min(x0, flaeche.Width - breite));
        y0 = Math.Max(0, Math.Min(y0, flaeche.Height - hoehe));
        return new Aufbau(new Rectangle(x0, y0, breite, hoehe), kachel, fuge, kopf, punkt);
    }

    /// <summary>Den Block auf eine Spielflaeche dieser Groesse zeichnen.</summary>
    /// <param name="auchWennAus">Die Vorschau zeigt ihn auch, wenn die Umrisse aus sind.</param>
    internal static void Male(Graphics g, OverlaySettings s, Size flaeche,
                              IReadOnlyList<(string Name, CourseShape.Outline? Shape)> kurse,
                              bool auchWennAus = false, IReadOnlyList<TileState>? stati = null)
    {
        if ((!s.CourseShapes && !auchWennAus) || kurse.Count == 0) { return; }
        var a = Lege(s, flaeche, kurse.Count);
        var platz = s.CoursePlacement;
        var aufloesung = GameArea.ResolutionScale(new Rectangle(Point.Empty, flaeche));
        int x0 = a.Block.X, y0 = a.Block.Y, kachel = a.Kachel, fuge = a.Fuge, kopf = a.Kopf;
        using var schrift = Schrift(a.Punkt);

        var linie = Parse(s.CourseShapeLine, ColorTranslator.FromHtml("#7fd3ff"));
        var start = Parse(s.CourseShapeStart, ColorTranslator.FromHtml("#ffd25a"));
        var grund = Parse(s.CourseShapeBack, ColorTranslator.FromHtml("#0d1117"));

        var alpha = Math.Max(0, Math.Min(255, s.CourseShapeBackAlpha));
        if (alpha > 0)
        {
            using var platte = new SolidBrush(Color.FromArgb(alpha, grund));
            g.FillRectangle(platte, a.Block);
        }

        using var text = new SolidBrush(linie);
        using var schwach = new SolidBrush(Color.FromArgb(150, linie));
        // JEDER NAME BLEIBT UEBER SEINER KACHEL. Bei kleiner Groesse ist die Kachel
        // schmaler als "Daikoku Circuit" in der Mindestschrift -- vorher lief der Name
        // dann in den der Nachbarkachel. Jetzt endet er mit "…" an der eigenen Kante.
        using var eineZeile = new StringFormat(StringFormatFlags.NoWrap)
        {
            Trimming = StringTrimming.EllipsisCharacter,
        };
        for (var i = 0; i < kurse.Count; i++)
        {
            var (name, umriss) = kurse[i];
            // Die Zelle einer Strecke: Name oben, Kachel darunter -- in beiden
            // Anordnungen gleich, nur die Zellen liegen anders.
            var kx = s.CourseVertical ? x0 + fuge : x0 + fuge + (i * (kachel + fuge));
            var zy = s.CourseVertical ? y0 + (i * (kopf + kachel + fuge)) : y0;
            var ky = zy + kopf;

            // Ein unbenannter Kurs heisst nach seinem Ordner -- lesbar machen.
            var anzeige = OwnTimes.CourseText(name, OwnTimes.IsFolderKey(name) ? null : name);
            var kurz = anzeige.Length <= 18 ? anzeige : anzeige[..17] + "…";
            g.DrawString(kurz, schrift, text,
                         new RectangleF(kx, zy + (fuge * 0.7f), kachel, kopf), eineZeile);

            if (umriss is null || umriss.IsEmpty)
            {
                // EHRLICH LEER. Ein leerer Kasten sagt "diese Strecke bin ich noch
                // nie gefahren" -- eine erfundene Form waere schlimmer als nichts.
                using var strich = new Pen(Color.FromArgb(90, linie), 1f)
                {
                    DashStyle = System.Drawing.Drawing2D.DashStyle.Dot,
                };
                g.DrawRectangle(strich, kx, ky, kachel, kachel);
                // DER GRUND GEHOERT DAZU. "not driven yet" waere bei gewaehlter
                // Rivalen-Quelle schlicht falsch: dort fehlt nicht die eigene
                // Runde, sondern die geerntete Karte. Zwei verschiedene Luecken
                // mit derselben Beschriftung schickt den Leser auf die falsche
                // Suche.
                var warum = s.ShapeSourceChoice == ShapeSource.Rivals
                    ? Loc.T("no map harvested")
                    : Loc.T("not driven yet");
                g.DrawString(warum, schrift, schwach,
                             new RectangleF(kx + fuge, ky + (kachel / 2f) - schrift.Height,
                                            kachel - (2 * fuge), schrift.Height * 2.2f));
                continue;
            }

            var stand = stati is not null && i < stati.Count ? stati[i] : TileState.None;
            // EINE GEFAHRENE STRECKE TRITT ZURUECK: blasser, damit das Auge bei der
            // naechsten landet und nicht bei der, die schon vorbei ist.
            var linieHier = stand == TileState.Done ? Color.FromArgb(110, linie) : linie;
            CourseShape.Draw(g, umriss,
                             new RectangleF(kx, ky, kachel, kachel),
                             linieHier, (float)(s.CourseShapeWidth * platz.Scale * aufloesung),
                             stand == TileState.Done ? Color.Empty : start,
                             s.CourseShapeSmooth, alsBild: s.RivalsAsImage);
            MaleStand(g, stand, new RectangleF(kx, ky, kachel, kachel), schrift, aufloesung);
        }
    }

    /// <summary>
    /// Die Meisterschaft sichtbar machen: gruen gefahren, gold jetzt, blau danach.
    /// </summary>
    /// <remarks>
    /// Seit 2026-09-26 (Nutzerwunsch). Ein farbiger Rahmen um die Kachel und ein
    /// Schild mit dem Wort -- die Farbe allein reicht nicht, wer Rot und Gruen schlecht
    /// unterscheidet, liest das Wort.
    /// </remarks>
    private static void MaleStand(Graphics g, TileState stand, RectangleF kachel, Font schrift, double aufloesung)
    {
        if (stand == TileState.None) { return; }
        var (farbe, wort) = stand switch
        {
            TileState.Done => (Color.FromArgb(126, 231, 135), Loc.T("done")),
            TileState.Now => (Color.FromArgb(255, 210, 90), Loc.T("now")),
            _ => (Color.FromArgb(127, 211, 255), Loc.T("next")),
        };
        var dicke = (float)Math.Max(2, (stand == TileState.Now ? 3 : 2) * aufloesung);
        using (var rahmen = new Pen(farbe, dicke))
        {
            g.DrawRectangle(rahmen, kachel.X + (dicke / 2), kachel.Y + (dicke / 2),
                            kachel.Width - dicke, kachel.Height - dicke);
        }
        var groesse = g.MeasureString(wort, schrift);
        var schild = new RectangleF(kachel.X + dicke, kachel.Bottom - groesse.Height - dicke,
                                    groesse.Width + (6 * (float)aufloesung), groesse.Height);
        using var grund = new SolidBrush(farbe);
        g.FillRectangle(grund, schild);
        using var dunkel = new SolidBrush(Color.FromArgb(13, 17, 23));
        g.DrawString(wort, schrift, dunkel, schild.X + (3 * (float)aufloesung), schild.Y);
    }

    /// <summary>
    /// Die Schrift in PIXELN (Punkt mal 96/72) statt in Punkten: eine Punktschrift
    /// haengt an der DPI der Zeichenflaeche -- die Bitmap des Overlays hat 96, die
    /// Vorschau im Reiter die des Schirms. Auf einem Schirm mit 150 % stand die
    /// Beschriftung in der Vorschau sonst anderthalbmal so gross wie im Spiel.
    /// </summary>
    private static Font Schrift(float punkt) =>
        new("Segoe UI", punkt * 96f / 72f, GraphicsUnit.Pixel);

    private static Color Parse(string? hex, Color fallback)
    {
        try
        {
            return string.IsNullOrWhiteSpace(hex) ? fallback
                                                  : ColorTranslator.FromHtml(hex.Trim());
        }
        catch (Exception)
        {
            return fallback;
        }
    }
}
