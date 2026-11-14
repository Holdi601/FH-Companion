namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Ein Abschnitt, der sich mit einem Klick auf seine Ueberschrift ein- und ausklappt.
/// </summary>
/// <remarks>
/// Seit 2026-11-14 (Nutzer: "die Sachen rechts sollten collapsen und expanden koennen, damit
/// man schneller navigieren kann"). Der Zustand wird je Abschnitt gemerkt (ui_open). Nebenbei
/// spart es Zeit: ein zugeklappter Inhalt bekommt keine Fenster, solange er zu bleibt --
/// der Reiter "Lap delta HUD" legte beim ersten Oeffnen 360 davon an.
/// </remarks>
internal sealed class Klappe
{
    private readonly Label _kopf;
    private readonly Control _inhalt;
    private readonly OverlaySettings _settings;
    private readonly string _schluessel;
    private readonly string _titel;
    private bool _offen;

    public Klappe(Label kopf, Control inhalt, OverlaySettings settings, string schluessel, bool vorgabeOffen)
    {
        _kopf = kopf;
        _inhalt = inhalt;
        _settings = settings;
        _schluessel = schluessel;
        _titel = kopf.Text;
        kopf.Cursor = Cursors.Hand;
        kopf.Click += (_, _) => Offen = !Offen;
        Setzen(settings.UiOpen.TryGetValue(schluessel, out var o) ? o : vorgabeOffen, merken: false);
    }

    public bool Offen
    {
        get => _offen;
        set { if (value != _offen) { Setzen(value, merken: true); } }
    }

    private void Setzen(bool offen, bool merken)
    {
        _offen = offen;
        _inhalt.Visible = offen;
        _kopf.Text = (offen ? "▾  " : "▸  ") + _titel;
        if (!merken) { return; }
        _settings.UiOpen[_schluessel] = offen;
        try { _settings.Save(); } catch (Exception) { }
    }

    /// <summary>Zwei kleine Knoepfe: alles auf, alles zu.</summary>
    public static FlowLayoutPanel Leiste(Func<IEnumerable<Klappe>> klappen, Color schrift, Action? danach = null)
    {
        var leiste = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0), Padding = new Padding(6, 4, 6, 2) };
        foreach (var (text, offen) in new[] { (Loc.T("Expand all"), true), (Loc.T("Collapse all"), false) })
        {
            var k = new LinkLabel
            {
                Text = text, AutoSize = true, LinkColor = schrift, ActiveLinkColor = Color.White,
                LinkBehavior = LinkBehavior.HoverUnderline, Margin = new Padding(4, 2, 10, 2),
            };
            k.LinkClicked += (_, _) =>
            {
                var form = k.FindForm();
                form?.SuspendLayout();
                foreach (var kl in klappen()) { kl.Offen = offen; }
                form?.ResumeLayout();
                danach?.Invoke();
            };
            leiste.Controls.Add(k);
        }
        return leiste;
    }
}
