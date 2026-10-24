using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ForzaHaptics.Tester.Scan;

/// <summary>
/// "SERVER ERROR" -- MITTEN AUF DEM SCHIRM, WENN JEMAND DA IST (seit 2026-10-24).
/// </summary>
/// <remarks>
/// Der Nutzer: hat zwischen den Fehlern niemand Maus oder Tastatur angefasst, wird immer weiter
/// versucht (Nachtscans hoeren nicht auf -- der Rechner taete sonst ohnehin nichts); ist jemand da,
/// soll er sehen, was los ist, und reagieren koennen. Dieses Fenster erscheint erst, wenn waehrend
/// der Wartezeit eine Eingabe kam, nimmt dem Spiel nicht von selbst den Fokus und haelt sich aus
/// Bildschirmaufnahmen heraus -- der Scanner liest das Spielbild und darf es nicht sehen.
/// </remarks>
internal sealed class ServerFehlerHinweis : Form
{
    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);

    /// <summary>Zeitpunkt der letzten Maus- oder Tastatureingabe (Windows-Takt in ms) -- 0, wenn unbekannt.</summary>
    public static uint LetzteEingabe()
    {
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? info.dwTime : 0u;
    }

    private readonly Label _text = new();
    private readonly System.Windows.Forms.Timer _uhr = new() { Interval = 1000 };
    private DateTime _naechster;
    private string _kopf = string.Empty;

    /// <summary>Weiter versuchen (true) oder den Scan beenden (false) -- vom Nutzer.</summary>
    public event Action<bool>? Entschieden;

    public ServerFehlerHinweis()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(28, 31, 38);
        ForeColor = Color.WhiteSmoke;
        Padding = new Padding(22);
        Size = new Size(560, 230);

        var titel = new Label
        {
            Text = Loc.T("Leaderboard scan: Server Error"), Dock = DockStyle.Top, Height = 34,
            Font = new Font("Segoe UI Semibold", 14f), ForeColor = Color.FromArgb(240, 180, 80),
        };
        _text.Dock = DockStyle.Fill;
        _text.Font = new Font("Segoe UI", 10.5f);
        var knoepfe = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft };
        var stopp = Knopf(Loc.T("Stop the scan"));
        stopp.Click += (_, _) => { Entschieden?.Invoke(false); Close(); };
        var weiter = Knopf(Loc.T("Keep trying"));
        weiter.Click += (_, _) => { Entschieden?.Invoke(true); Close(); };
        knoepfe.Controls.Add(stopp);
        knoepfe.Controls.Add(weiter);
        Controls.Add(_text);
        Controls.Add(knoepfe);
        Controls.Add(titel);
        _uhr.Tick += (_, _) => Zeigen();
        Paint += (_, e) =>
        {
            using var rand = new Pen(Color.FromArgb(240, 180, 80), 2f);
            e.Graphics.DrawRectangle(rand, 1, 1, Width - 3, Height - 3);
        };
    }

    private static Button Knopf(string text) => new()
    {
        Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(18, 20, 24),
        ForeColor = Color.WhiteSmoke, Margin = new Padding(8, 6, 0, 0), Padding = new Padding(10, 4, 10, 4),
    };

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW: nicht in der Taskleiste, nicht im Alt-Tab
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { SetWindowDisplayAffinity(Handle, 0x00000011); } catch (Exception) { }
    }

    /// <summary>Was zu sagen ist und wann der naechste Versuch kommt; mittig auf den Hauptschirm.</summary>
    public void Melde(int fehler, int hoechstens, DateTime naechster)
    {
        _naechster = naechster;
        _kopf = string.Format(Loc.T("The game cannot reach its leaderboards (Server Error {0} in a row). The scanner waits and then tries this board again -- {1} times while you are at the PC; without input at the mouse or keyboard it keeps trying, so a scan at night does not stop."),
                              fehler, hoechstens);
        var schirm = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Location = new Point(schirm.X + (schirm.Width - Width) / 2, schirm.Y + (schirm.Height - Height) / 2);
        Zeigen();
        _uhr.Start();
    }

    private void Zeigen()
    {
        var rest = _naechster - DateTime.Now;
        if (rest < TimeSpan.Zero) { rest = TimeSpan.Zero; }
        _text.Text = _kopf + Environment.NewLine + Environment.NewLine
                     + string.Format(Loc.T("Next try at {0} (in {1}:{2:00})."), _naechster.ToString("HH:mm"), (int)rest.TotalMinutes, rest.Seconds);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _uhr.Dispose(); }
        base.Dispose(disposing);
    }
}
