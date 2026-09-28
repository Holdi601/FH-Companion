using System.Drawing;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ForzaHaptics.Tester;

/// <summary>
/// Konsolen- bzw. Zweitrechner-Modus: das Bedienfeld im Reiter "Live grip telemetry"
/// und was es dafuer braucht (seit 2026-09-28).
/// </summary>
/// <remarks>
/// Das Spiel laeuft auf einer Xbox oder einem anderen PC und schickt seine Telemetrie
/// hierher. Was nur Telemetrie braucht, laeuft weiter (Delta, Eingabespuren, Live-Karte,
/// Reifen, Rundenaufzeichnung, eigene Rekorde) und steht im Dashboard. Was den Speicher
/// oder den Spielstand des Spiels liest oder den Controller ansteuert, faellt weg. Den
/// Schirm lesen geht mit einer Videoquelle (Fenster, Videogeraet, Stromadresse).
/// </remarks>
internal static class Konsole
{
    /// <summary>Die IPv4-Adressen dieses Rechners im Netz, fuer "Data Out" an der Konsole.</summary>
    internal static List<string> Adressen()
    {
        var raus = new List<string>();
        try
        {
            foreach (var karte in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (karte.OperationalStatus != OperationalStatus.Up
                    || karte.NetworkInterfaceType == NetworkInterfaceType.Loopback) { continue; }
                foreach (var a in karte.GetIPProperties().UnicastAddresses)
                {
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork) { raus.Add(a.Address.ToString()); }
                }
            }
        }
        catch (Exception)
        {
        }
        return raus.Distinct().ToList();
    }

    internal static string AdressenText()
    {
        var a = Adressen();
        return a.Count == 0 ? Loc.T("this PC's network address") : string.Join(", ", a);
    }

    /// <summary>Das Bedienfeld: Schalter, Adresse, Videoquelle, Modus der Runden, Neustart.</summary>
    internal static Control Feld(Rivals.OverlaySettings s, Point ort)
    {
        var grau = Color.FromArgb(147, 162, 181);
        var stapel = new FlowLayoutPanel
        {
            Location = ort, Width = 500, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown, WrapContents = false,
            BackColor = Color.FromArgb(28, 33, 40), Padding = new Padding(12, 8, 12, 12),
        };
        Label Kopf(string text) => new()
        {
            Text = text, ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 11f), AutoSize = true,
            Margin = new Padding(0, 6, 0, 4),
        };
        Label Notiz(string text) => new()
        {
            Text = text, ForeColor = grau, AutoSize = true, MaximumSize = new Size(470, 0),
            Margin = new Padding(0, 3, 0, 3),
        };
        var neustart = new Button
        {
            Text = Loc.T("Restart the app to apply"), AutoSize = true, FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(38, 44, 52), ForeColor = Color.WhiteSmoke, Visible = false,
            Margin = new Padding(0, 8, 0, 0),
        };
        neustart.Click += (_, _) => Application.Restart();
        void Geaendert()
        {
            s.Save();
            neustart.Visible = true;
        }

        stapel.Controls.Add(Kopf(Loc.T("Console or second PC")));
        var an = new CheckBox
        {
            Text = Loc.T("The game runs on an Xbox or another PC"), ForeColor = Color.Gainsboro,
            AutoSize = true, Checked = s.ConsoleMode,
        };
        an.CheckedChanged += (_, _) => { s.ConsoleMode = an.Checked; Geaendert(); };
        stapel.Controls.Add(an);
        stapel.Controls.Add(Notiz(string.Format(Loc.T(
            "The game sends its telemetry here over the network. On the Xbox or the other PC set Data Out to "
            + "this address: {0}, with the port on the left. Everything that only needs telemetry keeps "
            + "working and shows in a dashboard window (double-click or F11 for full screen): delta, input "
            + "traces, live map, tyre overview, lap recording and your records. Memory reading, the tune "
            + "tools and controller haptics are off -- the game and the controller are on the other device."),
            AdressenText())));

        stapel.Controls.Add(Kopf(Loc.T("Video source for screen reading")));
        stapel.Controls.Add(Notiz(Loc.T(
            "Optional. With the game's picture on this PC the sign-up maps, car recommendations, car notes "
            + "and mode detection work too. A window must stay visible; a video device (capture card or the "
            + "OBS Virtual Camera) or a stream address works in the background. A stream address needs "
            + "ffmpeg; the OBS Virtual Camera passes any source on without it.")));
        var wahl = new ComboBox { Width = 300, DropDownStyle = ComboBoxStyle.DropDownList };
        var arten = new[] { ("none", Loc.T("None -- telemetry only")), ("window", Loc.T("A window on this PC")),
                            ("device", Loc.T("A video device")), ("url", Loc.T("A stream address")) };
        foreach (var (_, text) in arten) { wahl.Items.Add(text); }
        wahl.SelectedIndex = Math.Max(0, Array.FindIndex(arten, a => string.Equals(a.Item1, s.VideoSource, StringComparison.OrdinalIgnoreCase)));
        stapel.Controls.Add(wahl);

        var fenster = new TextBox { Width = 300, Text = s.VideoWindow ?? string.Empty, PlaceholderText = Loc.T("part of the window title, e.g. Projector") };
        fenster.TextChanged += (_, _) => { s.VideoWindow = fenster.Text.Trim(); Geaendert(); };
        var geraet = new ComboBox { Width = 300, DropDownStyle = ComboBoxStyle.DropDown, Text = s.VideoDevice ?? string.Empty };
        geraet.TextChanged += (_, _) => { s.VideoDevice = geraet.Text.Trim(); Geaendert(); };
        var adresse = new TextBox { Width = 460, Text = s.VideoUrl ?? string.Empty, PlaceholderText = "rtsp://… / https://….m3u8 / srt://…" };
        adresse.TextChanged += (_, _) => { s.VideoUrl = adresse.Text.Trim(); Geaendert(); };
        stapel.Controls.Add(fenster);
        stapel.Controls.Add(geraet);
        stapel.Controls.Add(adresse);
        var geladen = false;
        void Zeigen()
        {
            var art = arten[Math.Max(0, wahl.SelectedIndex)].Item1;
            fenster.Visible = art == "window";
            geraet.Visible = art == "device";
            adresse.Visible = art == "url";
            if (art == "device" && !geladen)
            {
                geladen = true;
                _ = Task.Run(async () =>
                {
                    var liste = await Rivals.Bildquellen.GeraeteAsync();
                    try
                    {
                        geraet.BeginInvoke(() =>
                        {
                            var jetzt = geraet.Text;
                            geraet.Items.Clear();
                            foreach (var n in liste) { geraet.Items.Add(n); }
                            geraet.Text = jetzt;
                        });
                    }
                    catch (Exception) { }
                });
            }
        }
        wahl.SelectedIndexChanged += (_, _) =>
        {
            s.VideoSource = arten[Math.Max(0, wahl.SelectedIndex)].Item1;
            Zeigen();
            Geaendert();
        };
        Zeigen();

        stapel.Controls.Add(Kopf(Loc.T("Which mode you are playing")));
        var modus = new ComboBox { Width = 300, DropDownStyle = ComboBoxStyle.DropDownList, FormattingEnabled = true };
        foreach (var m in new[] { "auto", "rivals", "horizon-play", "race", "freeroam" }) { modus.Items.Add(m); }
        modus.Format += (_, e) => e.Value = Rivals.OwnTimes.ModeText(e.ListItem as string);
        modus.SelectedItem = modus.Items.Contains(s.LapMode ?? "auto") ? s.LapMode : "auto";
        modus.SelectedIndexChanged += (_, _) => { s.LapMode = modus.SelectedItem as string ?? "auto"; s.Save(); };
        stapel.Controls.Add(modus);
        stapel.Controls.Add(Notiz(Loc.T(
            "Without a video source the app cannot see which menu a lap came from. Set it here so your laps "
            + "carry the right mode -- only Rivals and Horizon Play laps count on the website.")));
        stapel.Controls.Add(neustart);
        return stapel;
    }
}
