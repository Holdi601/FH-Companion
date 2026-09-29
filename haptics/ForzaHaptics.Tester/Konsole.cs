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
/// oder den Spielstand des Spiels liest oder den Controller ansteuert, faellt weg.
///
/// Umgeschaltet wird oben im Fenster ("This PC" / "Xbox / 2nd PC", siehe
/// <see cref="Modusschalter"/>). Hier stehen die Schritte danach: was an der Konsole
/// einzutragen ist, woher das Spielbild kommt (vier Wege), welcher Modus gefahren wird.
/// </remarks>
internal static class Konsole
{
    /// <summary>Die IPv4-Adressen dieses Rechners im Netz, fuer "Data Out" an der Konsole.</summary>
    /// <remarks>
    /// NUR KARTEN MIT EINEM GATEWAY, wenn es solche gibt: Hyper-V, WSL und VPNs legen
    /// virtuelle Karten an (172.x, 10.x), die die Xbox nie erreicht. Gemessen am
    /// 2026-09-28: neben 192.168.2.4 stand 172.18.144.1 -- die WSL-Bruecke.
    /// </remarks>
    internal static List<string> Adressen()
    {
        // Fuer Anleitungsbilder (--main-preview): eine Beispieladresse statt der echten.
        if (MainForm.NurVorschau && Environment.GetEnvironmentVariable("FHC_PREVIEW_ADDRESS") is { Length: > 0 } beispiel)
        {
            return new List<string> { beispiel };
        }
        var mitGateway = new List<string>();
        var alle = new List<string>();
        try
        {
            foreach (var karte in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (karte.OperationalStatus != OperationalStatus.Up
                    || karte.NetworkInterfaceType == NetworkInterfaceType.Loopback) { continue; }
                var eigenschaften = karte.GetIPProperties();
                var gateway = eigenschaften.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                                                                     && !g.Address.Equals(System.Net.IPAddress.Any));
                foreach (var a in eigenschaften.UnicastAddresses)
                {
                    if (a.Address.AddressFamily != AddressFamily.InterNetwork) { continue; }
                    alle.Add(a.Address.ToString());
                    if (gateway) { mitGateway.Add(a.Address.ToString()); }
                }
            }
        }
        catch (Exception)
        {
        }
        return (mitGateway.Count > 0 ? mitGateway : alle).Distinct().ToList();
    }

    internal static string AdressenText()
    {
        var a = Adressen();
        return a.Count == 0 ? Loc.T("this PC's network address") : string.Join(", ", a);
    }

    /// <summary>Die vier Wege zum Spielbild, dazu "keins" -- Schluessel wie in OverlaySettings.VideoSource.</summary>
    internal static readonly string[] Quellen = { "none", "device", "obs", "window", "discord", "browser", "url" };

    private static readonly Color Grau = Color.FromArgb(147, 162, 181);

    /// <summary>Das Bedienfeld. Im PC-Modus nur der Hinweis auf den Schalter oben.</summary>
    internal static Control Feld(Rivals.OverlaySettings s, Point ort, Func<int>? port = null,
                                 Action? dashboardOeffnen = null)
    {
        var stapel = new FlowLayoutPanel
        {
            Location = ort, Width = 500, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown, WrapContents = false,
            BackColor = Color.FromArgb(28, 33, 40), Padding = new Padding(12, 8, 12, 12),
        };
        if (!s.ConsoleMode)
        {
            stapel.Controls.Add(Kopf(Loc.T("Playing on an Xbox or another PC?")));
            stapel.Controls.Add(Notiz(Loc.T("Switch to “Xbox / 2nd PC” at the top of the window.")));
            return stapel;
        }

        stapel.Controls.Add(Kopf(Loc.T("Xbox / 2nd PC")));
        stapel.Controls.Add(Notiz(string.Format(Loc.T(
            "In Forza on the Xbox or the other PC: Settings → HUD and Gameplay → Data Out: On, Data Out IP Address: {0}, Data Out IP Port: {1}."),
            AdressenText(), port?.Invoke() ?? 5300)));

        // ---- DAS SPIELBILD: vier Wege ------------------------------------------------
        stapel.Controls.Add(Kopf(Loc.T("Game picture (optional)")));
        stapel.Controls.Add(Notiz(Loc.T(
            "With the game's picture on this PC the app also reads the sign-up screen, My Cars and which mode you play.")));

        var texte = new Dictionary<string, string>
        {
            ["none"] = Loc.T("None -- telemetry only"),
            ["device"] = Loc.T("Capture card"),
            ["obs"] = "OBS",
            ["window"] = Loc.T("Xbox Remote Play"),
            ["discord"] = "Discord",
            ["browser"] = Loc.T("Stream in the browser (Twitch, YouTube, Kick …)"),
            ["url"] = Loc.T("Stream address"),
        };
        var hilfen = new Dictionary<string, string>
        {
            ["none"] = string.Empty,
            ["device"] = Loc.T("Connect the Xbox to the capture card and pick the card here."),
            ["obs"] = Loc.T("In OBS: right-click the preview → Windowed Projector (Program). It may sit behind other windows on a screen, just not minimized."),
            ["window"] = Loc.T("Start Remote Play in the Xbox app or at xbox.com/play. The window may sit behind other windows on a screen, just not minimized.")
                         + " " + Loc.T("Any other window that shows the game works too: pick it from the list."),
            ["discord"] = Loc.T("Watch the Xbox's stream in Discord on this PC, popped out or full screen. Your own stream needs a second Discord account here: your call moves to the Xbox."),
            ["browser"] = Loc.T("Open your stream in the browser, full screen or in theater mode. It runs a few seconds late, which is fine for reading menus; the HUD stays in the dashboard."),
            ["url"] = string.Empty,
        };

        var jetzt = Array.IndexOf(Quellen, (s.VideoSource ?? "none").ToLowerInvariant());
        if (jetzt < 0) { jetzt = 0; }
        var knoepfe = new List<RadioButton>();
        var felder = new Dictionary<string, Control>();

        // Aufnahmekarte: die Videogeraete dieses Rechners (ohne virtuelle Kameras).
        var geraet = new ComboBox { Width = 300, DropDownStyle = ComboBoxStyle.DropDown, Text = s.VideoDevice ?? string.Empty,
                                    Margin = new Padding(22, 0, 0, 2) };
        // Xbox Remote Play: ein Fenster; vorgeschlagen wird das der Xbox-App.
        var fenster = new ComboBox { Width = 300, DropDownStyle = ComboBoxStyle.DropDown,
                                     Text = string.Equals(s.VideoSource, "window", StringComparison.OrdinalIgnoreCase)
                                         ? s.VideoWindow ?? string.Empty : string.Empty,
                                     Margin = new Padding(22, 0, 0, 2) };
        var adresse = new TextBox { Width = 440, Text = s.VideoUrl ?? string.Empty,
                                    PlaceholderText = "srt://… / rtmp://… / https://….m3u8", Margin = new Padding(22, 0, 0, 2) };
        var ffmpeg = Notiz(string.Empty);
        ffmpeg.Margin = new Padding(22, 0, 0, 2);
        felder["device"] = geraet;
        felder["window"] = fenster;
        felder["url"] = adresse;

        var vorschau = new PictureBox
        {
            Size = new Size(320, 180), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black,
            Margin = new Padding(0, 8, 0, 2),
        };
        var zustand = Notiz(Loc.T("No picture yet"));

        void Anwenden()
        {
            s.Save();
            Rivals.Bildquellen.Anwenden(s);
        }

        void ZeigeFelder()
        {
            var art = Quellen[Math.Max(0, knoepfe.FindIndex(k => k.Checked))];
            geraet.Visible = art == "device";
            fenster.Visible = art == "window";
            adresse.Visible = art == "url";
            ffmpeg.Visible = art == "url";
            foreach (var (schluessel, hilfe) in hilfen)
            {
                if (felder.TryGetValue("hilfe:" + schluessel, out var h)) { h.Visible = art == schluessel && hilfe.Length > 0; }
            }
            vorschau.Visible = art != "none";
            zustand.Visible = art != "none";
            if (art == "url")
            {
                var gefunden = Rivals.Ffmpeg.Finden(s.FfmpegPath);
                ffmpeg.Text = gefunden is null
                    ? Loc.T("Needs ffmpeg: run “winget install Gyan.FFmpeg” once.")
                    : Loc.T("ffmpeg found.");
            }
        }

        for (var i = 0; i < Quellen.Length; i++)
        {
            var art = Quellen[i];
            var knopf = new RadioButton
            {
                Text = texte[art], AutoSize = true, ForeColor = Color.Gainsboro, Checked = i == jetzt,
                Margin = new Padding(0, 3, 0, 0),
            };
            knopf.CheckedChanged += (_, _) =>
            {
                if (!knopf.Checked) { return; }
                s.VideoSource = art;
                if (art == "window" && string.IsNullOrWhiteSpace(fenster.Text))
                {
                    fenster.Text = Rivals.Fenster.XboxVorschlag() ?? "Xbox";
                }
                if (art == "window") { s.VideoWindow = fenster.Text.Trim(); }
                ZeigeFelder();
                Anwenden();
            };
            knoepfe.Add(knopf);
            stapel.Controls.Add(knopf);
            if (felder.TryGetValue(art, out var feld)) { stapel.Controls.Add(feld); }
            if (art == "url") { stapel.Controls.Add(ffmpeg); }
            if (hilfen[art].Length > 0)
            {
                var h = Notiz(hilfen[art]);
                h.Margin = new Padding(22, 0, 0, 4);
                felder["hilfe:" + art] = h;
                stapel.Controls.Add(h);
            }
        }

        // Aenderungen gelten sofort -- erst nach einer kurzen Tipp-Pause, damit nicht
        // jeder Buchstabe eine Quelle startet.
        var pause = new System.Windows.Forms.Timer { Interval = 700 };
        pause.Tick += (_, _) => { pause.Stop(); Anwenden(); };
        void Spaeter() { pause.Stop(); pause.Start(); }
        geraet.TextChanged += (_, _) => { s.VideoDevice = geraet.Text.Trim(); Spaeter(); };
        fenster.TextChanged += (_, _) =>
        {
            if (!string.Equals(s.VideoSource, "window", StringComparison.OrdinalIgnoreCase)) { return; }
            s.VideoWindow = fenster.Text.Trim();
            Spaeter();
        };
        adresse.TextChanged += (_, _) => { s.VideoUrl = adresse.Text.Trim(); Spaeter(); };

        // Die Listen erst beim Aufklappen fuellen: Geraete zu suchen dauert einen Moment.
        geraet.DropDown += async (_, _) =>
        {
            var liste = await Rivals.Bildquellen.GeraeteAsync();
            var text = geraet.Text;
            geraet.Items.Clear();
            foreach (var n in liste.Where(n => !n.Contains("OBS", StringComparison.OrdinalIgnoreCase)))
            {
                geraet.Items.Add(n);
            }
            geraet.Text = text;
        };
        fenster.DropDown += (_, _) =>
        {
            var text = fenster.Text;
            fenster.Items.Clear();
            foreach (var (_, t) in Rivals.Fenster.Sichtbare().OrderBy(f => f.Titel)) { fenster.Items.Add(t); }
            fenster.Text = text;
        };
        // Ein Geraet vorschlagen, wenn noch keines gewaehlt ist -- meist gibt es genau eines.
        if (string.IsNullOrWhiteSpace(geraet.Text))
        {
            _ = Task.Run(async () =>
            {
                var liste = await Rivals.Bildquellen.GeraeteAsync();
                var erstes = liste.FirstOrDefault(n => !n.Contains("OBS", StringComparison.OrdinalIgnoreCase));
                if (erstes is null) { return; }
                try { geraet.BeginInvoke(() => { if (string.IsNullOrWhiteSpace(geraet.Text)) { geraet.Text = erstes; } }); }
                catch (Exception) { }
            });
        }

        stapel.Controls.Add(vorschau);
        stapel.Controls.Add(zustand);

        // WO DER HUD ERSCHEINT (seit 2026-09-29): mit Remote Play oder dem OBS-Projektor
        // liegt das Spiel in einem Fenster auf diesem Schirm -- der HUD kann darueber,
        // wie beim Spielen am PC. Sonst im Dashboard.
        var hudKopf = Kopf(Loc.T("Where the HUD shows"));
        var imDashboard = new RadioButton
        {
            Text = Loc.T("In the dashboard window"), AutoSize = true, ForeColor = Color.Gainsboro,
            Checked = !s.ConsoleHudOverWindow, Margin = new Padding(0, 3, 0, 0),
        };
        var ueberFenster = new RadioButton
        {
            Text = Loc.T("Over the game window, like playing on the PC"), AutoSize = true, ForeColor = Color.Gainsboro,
            Checked = s.ConsoleHudOverWindow, Margin = new Padding(0, 3, 0, 0),
        };
        var hudNotiz = Notiz(Loc.T(
            "It shows while that window is in front. Move and size the HUD in the Lap delta HUD tab, as on the PC."));
        imDashboard.CheckedChanged += (_, _) =>
        {
            if (!imDashboard.Checked) { return; }
            s.ConsoleHudOverWindow = false;
            s.Save();
            Rivals.OverlayAusgabe.SetzeImSpiel(false);
            dashboardOeffnen?.Invoke();
        };
        ueberFenster.CheckedChanged += (_, _) =>
        {
            if (!ueberFenster.Checked) { return; }
            s.ConsoleHudOverWindow = true;
            s.Save();
        };
        stapel.Controls.Add(hudKopf);
        stapel.Controls.Add(imDashboard);
        stapel.Controls.Add(ueberFenster);
        stapel.Controls.Add(hudNotiz);
        // Nur mit einem Fenster als Quelle gibt es ein "darueber".
        void HudWahlZeigen()
        {
            var fenster = (s.VideoSource ?? "none").ToLowerInvariant() is "window" or "obs";
            hudKopf.Visible = imDashboard.Visible = ueberFenster.Visible = hudNotiz.Visible = fenster;
        }
        foreach (var k in knoepfe) { k.CheckedChanged += (_, _) => HudWahlZeigen(); }
        HudWahlZeigen();

        // DIE VORSCHAU: was die App gerade sieht, damit man es nicht erraten muss. Nur
        // solange der Reiter offen ist -- sonst kostet sie nichts.
        var takt = new System.Windows.Forms.Timer { Interval = 700 };
        takt.Tick += (_, _) =>
        {
            if (!stapel.Visible || stapel.FindForm() is not { WindowState: not FormWindowState.Minimized }) { return; }
            var quelle = Rivals.Bildquellen.Aktiv;
            if (quelle is null)
            {
                vorschau.Image?.Dispose();
                vorschau.Image = null;
                zustand.Text = s.VideoSource == "none" ? string.Empty : Loc.T("No picture yet");
                return;
            }
            try
            {
                var bild = quelle.Neuestes();
                if (bild is not null)
                {
                    var klein = new Bitmap(320, 180);
                    using (var g = Graphics.FromImage(klein))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                        g.DrawImage(bild, new Rectangle(0, 0, 320, 180));
                    }
                    vorschau.Image?.Dispose();
                    vorschau.Image = klein;
                    zustand.Text = $"{quelle.Beschreibung} -- {bild.Width}×{bild.Height}";
                }
                else
                {
                    zustand.Text = quelle.Beschreibung;
                }
            }
            catch (Exception)
            {
                // Das Bild wurde gerade ersetzt -- beim naechsten Takt.
            }
        };
        takt.Start();
        stapel.Disposed += (_, _) => { takt.Dispose(); pause.Dispose(); };
        ZeigeFelder();

        // ---- DER MODUS -----------------------------------------------------------------
        stapel.Controls.Add(Kopf(Loc.T("Which mode you are playing")));
        var modus = new ComboBox { Width = 300, DropDownStyle = ComboBoxStyle.DropDownList, FormattingEnabled = true };
        foreach (var m in new[] { "auto", "rivals", "horizon-play", "race", "freeroam" }) { modus.Items.Add(m); }
        modus.Format += (_, e) => e.Value = Rivals.OwnTimes.ModeText(e.ListItem as string);
        modus.SelectedItem = modus.Items.Contains(s.LapMode ?? "auto") ? s.LapMode : "auto";
        modus.SelectedIndexChanged += (_, _) => { s.LapMode = modus.SelectedItem as string ?? "auto"; s.Save(); };
        stapel.Controls.Add(modus);
        stapel.Controls.Add(Notiz(Loc.T(
            "Without a game picture the app cannot see which menu a lap came from. Set it here -- only Rivals and Horizon Play laps count on the website.")));
        return stapel;
    }

    private static Label Kopf(string text) => new()
    {
        Text = text, ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 11f), AutoSize = true,
        Margin = new Padding(0, 8, 0, 4),
    };

    private static Label Notiz(string text) => new()
    {
        Text = text, ForeColor = Grau, AutoSize = true, MaximumSize = new Size(470, 0),
        Margin = new Padding(0, 2, 0, 3),
    };

    /// <summary>
    /// Der Schalter oben im Fenster: "This PC" | "Xbox / 2nd PC" (seit 2026-09-28).
    /// </summary>
    /// <remarks>
    /// Umschalten startet die App neu: der Modus entscheidet schon beim Start, woran der
    /// Empfang gebunden wird (nur dieser Rechner oder das Netz), ob ein Controller gesucht
    /// wird und welche Reiter es gibt. Das alles zur Laufzeit umzubauen waere viel
    /// Maschinerie fuer einen seltenen Klick -- dieselbe Abwaegung wie bei der Sprache.
    /// </remarks>
    internal static Control Modusschalter(bool konsole, Action<bool> umschalten)
    {
        var zeile = new FlowLayoutPanel
        {
            AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
            Margin = new Padding(0), Padding = new Padding(0),
        };
        RadioButton Knopf(string text, bool an) => new()
        {
            Text = text, Appearance = Appearance.Button, AutoSize = true, Checked = an,
            FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleCenter,
            Padding = new Padding(10, 2, 10, 2), Margin = new Padding(0, 0, 2, 0),
            BackColor = an ? Color.FromArgb(125, 211, 252) : Color.FromArgb(38, 44, 52),
            ForeColor = an ? Color.FromArgb(11, 15, 20) : Color.Gainsboro,
            Font = new Font("Segoe UI Semibold", 9.5f),
            Cursor = Cursors.Hand,
        };
        var pc = Knopf(Loc.T("This PC"), !konsole);
        var xbox = Knopf(Loc.T("Xbox / 2nd PC"), konsole);
        foreach (var k in new[] { pc, xbox })
        {
            k.FlatAppearance.BorderColor = Color.FromArgb(70, 82, 100);
            k.FlatAppearance.CheckedBackColor = Color.FromArgb(125, 211, 252);
        }
        void Geklickt(bool nachKonsole, RadioButton knopf)
        {
            if (!knopf.Checked || nachKonsole == konsole) { return; }
            umschalten(nachKonsole);
            // Abgebrochen (oder noch nicht neu gestartet): den alten Zustand zeigen.
            (konsole ? xbox : pc).Checked = true;
        }
        pc.CheckedChanged += (_, _) => Geklickt(false, pc);
        xbox.CheckedChanged += (_, _) => Geklickt(true, xbox);
        var titel = new Label
        {
            Text = Loc.T("The game runs on:"), AutoSize = true, ForeColor = Grau,
            Margin = new Padding(0, 5, 8, 0),
        };
        zeile.Controls.Add(titel);
        zeile.Controls.Add(pc);
        zeile.Controls.Add(xbox);
        return zeile;
    }
}
