using System.Drawing;
using System.Drawing.Drawing2D;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Die Live-Karte im Rennen: EINE Strecke, der gefahrene Teil und das eigene Auto.
/// </summary>
/// <remarks>
/// ## Wozu, neben den drei Umrissen
///
/// Die drei Umrisse (<see cref="CourseShapeHud"/>) gehoeren auf den Anmeldeschirm:
/// welche Strecken kommen, bevor man ein Auto waehlt. Im Rennen will man etwas
/// anderes -- wo auf DIESER Strecke man gerade ist. Darum ein eigenes Stueck mit
/// eigener Lage, Groesse und eigenem Schalter (aus, bis man es anschaltet).
///
/// ## Woher die Strecke kommt
///
/// Aus der Referenzrunde, die auch der Delta-Streifen benutzt -- sie ist per
/// Startpunkt und gefahrenem Weg als DIESE Strecke erkannt (siehe OverlayController).
/// Ihre Messpunkte tragen Weltkoordinaten, dieselben wie PositionX/Z der Telemetrie;
/// darum sitzt der Punkt des Autos genau auf der Linie. Eine geerntete Rivalen-Karte
/// kann das nicht: sie ist ein Bild ohne Weltbezug, der Punkt stuende irgendwo.
///
/// Ohne Referenz (die erste Runde auf einer Strecke) zeichnet die Karte den Weg,
/// wie er gefahren wird -- sie waechst mit.
///
/// ## Nordweisend
///
/// Wie die Umrisse: Z waechst im Spiel nach Norden, im Bild nach oben. Eine Karte,
/// die sich mit dem Auto dreht, waere beim Zurechtfinden huebscher -- aber dann
/// saehe dieselbe Strecke jedes Mal anders aus als auf dem Anmeldeschirm.
/// </remarks>
internal sealed class LiveMapHud : LayeredHud
{
    private readonly OverlaySettings _settings;
    private List<PointF> _pfad = new();
    private readonly List<PointF> _spur = new();
    private PointF? _auto;
    private object? _referenz;
    private DateTime _gezeichnet;
    private bool _preview;

    public LiveMapHud(OverlaySettings settings, Rectangle screen) : base(screen)
    {
        _settings = settings;
    }

    /// <summary>Die Vorschau zeigt die Karte auch, wenn sie abgeschaltet ist.</summary>
    public void SetPreview(bool an) { _preview = an; Render(); }

    /// <summary>Die Strecke: die Referenzrunde des Delta-Streifens.</summary>
    public void SetReference(RecordedLap? referenz)
    {
        if (ReferenceEquals(referenz, _referenz)) { return; }
        _referenz = referenz;
        _pfad = referenz is null || !referenz.HasPositions
            ? new List<PointF>()
            : referenz.Samples.Where(p => p.X != 0f || p.Z != 0f)
                      .Select(p => new PointF(p.X, p.Z)).ToList();
        Render();
    }

    /// <summary>
    /// Ein gespeicherter Umriss als Beispielstrecke: dessen Punkte liegen im Bild
    /// (y nach UNTEN), die Karte rechnet in der Welt (Z nach NORDEN). Ohne das
    /// Umdrehen stuende die Vorschau auf dem Kopf.
    /// </summary>
    internal static List<PointF> SampleAus(CourseShape.Outline umriss) =>
        umriss.Points.Select(p => new PointF(p.X, 1f - p.Y)).ToList();

    /// <summary>Fuer die Vorschau: eine Strecke und ein Auto darauf, ohne Rennen.</summary>
    public void SetSample(IReadOnlyList<PointF> pfad, int autoBei)
    {
        _referenz = null;
        _pfad = pfad.ToList();
        _spur.Clear();
        var bis = Math.Clamp(autoBei, 0, Math.Max(0, _pfad.Count - 1));
        _spur.AddRange(_pfad.Take(bis + 1));
        _auto = _pfad.Count > 0 ? _pfad[bis] : null;
        Render();
    }

    /// <summary>Neue Runde: der gefahrene Teil beginnt von vorn.</summary>
    public void ClearTrail()
    {
        _spur.Clear();
        _auto = null;
        Render();
    }

    /// <summary>Wo das Auto gerade ist. Gezeichnet wird hoechstens 15-mal je Sekunde.</summary>
    public void Push(float x, float z)
    {
        // 0/0 ist kein Ort, sondern ein fehlender Wert (Menue, Ladeschirm).
        if (x == 0f && z == 0f) { return; }
        var p = new PointF(x, z);
        SpurFortsetzen(_spur, p);
        _auto = p;
        if ((DateTime.UtcNow - _gezeichnet).TotalMilliseconds >= 66)
        {
            _gezeichnet = DateTime.UtcNow;
            Render();
        }
    }

    public bool HasContent => _pfad.Count > 1 || _spur.Count > 1;

    /// <summary>Einen Weg an jedem Sprung auftrennen.</summary>
    internal static List<List<PointF>> Stuecke(IReadOnlyList<PointF> pfad)
    {
        var raus = new List<List<PointF>>();
        var jetzt = new List<PointF>();
        for (var i = 0; i < pfad.Count; i++)
        {
            if (i > 0 && Abstand(pfad[i - 1], pfad[i]) > Sprung)
            {
                if (jetzt.Count >= 2) { raus.Add(jetzt); }
                jetzt = new List<PointF>();
            }
            jetzt.Add(pfad[i]);
        }
        if (jetzt.Count >= 2) { raus.Add(jetzt); }
        return raus;
    }

    /// <summary>Einen Ort an die gefahrene Spur haengen -- Spruenge abgefangen.</summary>
    internal static void SpurFortsetzen(List<PointF> spur, PointF p)
    {
        if (spur.Count > 0 && Abstand(spur[^1], p) > Sprung)
        {
            // EIN SPRUNG IST KEINE FAHRT. Zurueckgespult liegt der neue Ort auf dem
            // eigenen Weg: dort abschneiden. Sonst ist es ein Szenenwechsel -- das
            // Spiel setzt das Auto an die Startlinie, laedt das naechste Rennen --, und
            // dann gehoert nichts von der alten Spur zur neuen. Vorher verband die
            // Karte beides mit einer langen geraden Linie.
            var naechster = -1;
            var abstand = float.MaxValue;
            for (var i = 0; i < spur.Count; i++)
            {
                var d = Abstand(spur[i], p);
                if (d < abstand) { abstand = d; naechster = i; }
            }
            if (naechster >= 0 && abstand < 40f)
            {
                spur.RemoveRange(naechster + 1, spur.Count - naechster - 1);
            }
            else
            {
                spur.Clear();
            }
        }
        if (spur.Count == 0 || Abstand(spur[^1], p) > 4f) { spur.Add(p); }
    }

    /// <summary>
    /// Ab so vielen Metern zwischen zwei gezeichneten Orten ist es ein Sprung. Die
    /// Karte bekommt zehn Orte je Sekunde; selbst mit 400 km/h und einer Stockung von
    /// einer Sekunde sind das gut 110 m.
    /// </summary>
    internal const float Sprung = 150f;

    private static float Abstand(PointF a, PointF b) =>
        MathF.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    protected override Rectangle Extent(Graphics messung) =>
        (_settings.LiveMap || _preview) && HasContent ? Lege(_settings, AreaSize) : Rectangle.Empty;

    protected override void Draw(Graphics g) =>
        Male(g, _settings, AreaSize, _pfad, _spur, _auto, auchWennAus: _preview);

    /// <summary>Wo die Karte auf einer Spielflaeche dieser Groesse liegt -- fuer Overlay UND Vorschau.</summary>
    internal static Rectangle Lege(OverlaySettings s, Size flaeche)
    {
        var platz = s.LiveMapPlacement;
        var aufloesung = GameArea.ResolutionScale(new Rectangle(Point.Empty, flaeche));
        var seite = Math.Max(60, (int)(s.LiveMapSize * platz.Scale * aufloesung));
        var x0 = (int)(platz.X * flaeche.Width);
        var y0 = (int)(platz.Y * flaeche.Height);
        x0 = platz.Align.ToLowerInvariant() switch
        {
            "left" => x0,
            "right" => x0 - seite,
            _ => x0 - (seite / 2),
        };
        x0 = Math.Max(0, Math.Min(x0, flaeche.Width - seite));
        y0 = Math.Max(0, Math.Min(y0, flaeche.Height - seite));
        return new Rectangle(x0, y0, seite, seite);
    }

    /// <summary>Die Karte zeichnen, in Koordinaten der ganzen Spielflaeche.</summary>
    /// <param name="pfad">Die Strecke (Welt-X/Z oder, in der Vorschau, beliebige Einheiten).</param>
    /// <param name="spur">Der gefahrene Teil dieser Runde.</param>
    /// <param name="auto">Wo das Auto gerade ist.</param>
    internal static void Male(Graphics g, OverlaySettings s, Size flaeche,
                              IReadOnlyList<PointF> pfad, IReadOnlyList<PointF> spur,
                              PointF? auto, bool auchWennAus = false)
    {
        if ((!s.LiveMap && !auchWennAus) || (pfad.Count < 2 && spur.Count < 2)) { return; }
        var k = Lege(s, flaeche);
        var platz = s.LiveMapPlacement;
        var aufloesung = GameArea.ResolutionScale(new Rectangle(Point.Empty, flaeche));
        var massstab = platz.Scale * aufloesung;

        var grund = OverlaySettings.ParseColour(s.CourseShapeBack, Color.FromArgb(13, 17, 23));
        var alpha = Math.Max(0, Math.Min(255, s.CourseShapeBackAlpha));
        if (alpha > 0)
        {
            using var platte = new SolidBrush(Color.FromArgb(alpha, grund));
            g.FillRectangle(platte, k);
        }

        // DIE AUSDEHNUNG STEHT FEST, sobald es eine Referenz gibt: sonst zoomte die
        // Karte bei jedem Meter der ersten Runde neu. Ohne Referenz waechst sie mit
        // dem gefahrenen Weg.
        var basis = pfad.Count >= 2 ? pfad : spur;
        float x0 = basis.Min(p => p.X), x1 = basis.Max(p => p.X);
        float z0 = basis.Min(p => p.Y), z1 = basis.Max(p => p.Y);
        var rand = Math.Max(6f, 12f * massstab);
        var breite = Math.Max(1f, x1 - x0);
        var hoehe = Math.Max(1f, z1 - z0);
        var f = Math.Min((k.Width - (2 * rand)) / breite, (k.Height - (2 * rand)) / hoehe);
        var mx = (x0 + x1) / 2f;
        var mz = (z0 + z1) / 2f;
        var cx = k.X + (k.Width / 2f);
        var cy = k.Y + (k.Height / 2f);
        PointF Ort(PointF p) => new(cx + ((p.X - mx) * f), cy - ((p.Y - mz) * f));

        var linie = OverlaySettings.ParseColour(s.CourseShapeLine, Color.FromArgb(127, 211, 255));
        // EIGENE STAERKE UND GLAETTUNG (seit 2026-09-25) -- vorher lieh sich die Karte
        // die Strichstaerke der Anmeldekarten und zeichnete jeden Messpunkt roh.
        var dicke = Math.Max(1f, (float)(s.LiveMapWidth * massstab));
        var sigma = CourseShape.SigmaFuer(s.LiveMapSmooth, k.Width);
        var zustand = g.Save();
        g.SetClip(k);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        if (pfad.Count >= 2)
        {
            // IN STUECKEN, getrennt an jedem Sprung: eine abgelegte Runde kann einen
            // enthalten (Zuruecksetzen auf die Strecke im Online-Rennen), und als
            // Linie gezeichnet saehe er aus wie eine Abkuerzung quer ueber die Karte.
            foreach (var stueck in Stuecke(pfad))
            {
                CourseShape.Linie(g, CourseShape.Glaetten(stueck.Select(Ort).ToArray(), false, sigma), false,
                                  Color.FromArgb(spur.Count >= 2 ? 150 : 255, linie), dicke);
            }
        }
        if (spur.Count >= 2)
        {
            var spurFarbe = OverlaySettings.ParseColour(s.LiveMapTrail, Color.FromArgb(255, 210, 90));
            CourseShape.Linie(g, CourseShape.Glaetten(spur.Select(Ort).ToArray(), false, sigma), false,
                              spurFarbe, dicke * 1.3f);
        }
        if (pfad.Count >= 2)
        {
            // Der Start: dort, wo die Referenz begann.
            var start = OverlaySettings.ParseColour(s.CourseShapeStart, Color.FromArgb(255, 210, 90));
            var sp = Ort(pfad[0]);
            var r = Math.Max(2.5f, 3.5f * (float)massstab);
            using var pinsel = new SolidBrush(start);
            g.FillEllipse(pinsel, sp.X - r, sp.Y - r, 2 * r, 2 * r);
        }
        if (auto is { } a)
        {
            var ap = Ort(a);
            var r = Math.Max(3.5f, 5.5f * (float)massstab);
            // Ein Ring in der Farbe der Platte haelt den Punkt auf der Linie lesbar.
            using var ring = new SolidBrush(Color.FromArgb(255, grund));
            g.FillEllipse(ring, ap.X - r - 2, ap.Y - r - 2, (2 * r) + 4, (2 * r) + 4);
            using var pinsel = new SolidBrush(OverlaySettings.ParseColour(s.LiveMapCar, Color.White));
            g.FillEllipse(pinsel, ap.X - r, ap.Y - r, 2 * r, 2 * r);
        }
        g.Restore(zustand);
    }
}
