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
    private static readonly Color Gruen = Color.FromArgb(130, 220, 150);
    private static readonly Color Gelb = Color.FromArgb(255, 200, 90);

    /// <summary>
    /// Was an der Konsole einzutragen ist, als Tabelle statt als Satz (seit 2026-09-29):
    /// die Adresse muss man abtippen, und in einem Satz ging sie unter.
    /// </summary>
    private static void DataOutBlock(FlowLayoutPanel stapel, int port)
    {
        stapel.Controls.Add(Notiz(Loc.T("Enter this in Forza on the Xbox or the other PC, under Settings › HUD and Gameplay:")));
        var tabelle = new TableLayoutPanel
        {
            ColumnCount = 2, AutoSize = true, Margin = new Padding(12, 2, 0, 6), BackColor = Color.FromArgb(20, 24, 30),
            Padding = new Padding(8, 4, 8, 4),
        };
        var adressen = Adressen();
        void Zeile(string feld, string wert)
        {
            tabelle.Controls.Add(new Label { Text = feld, AutoSize = true, ForeColor = Grau, Margin = new Padding(0, 3, 18, 3) });
            tabelle.Controls.Add(new Label
            {
                Text = wert, AutoSize = true, ForeColor = Color.White, Margin = new Padding(0, 3, 0, 3),
                Font = new Font("Segoe UI Semibold", 10.5f),
            });
        }
        // Die Namen, wie sie im Spiel stehen -- in der Uebersetzung mit dem Wortlaut, den
        // das Spiel in dieser Sprache zeigt (Datenausgabe, Salida de datos ...), sonst sucht
        // man im Menue nach einem Wort, das dort nicht steht.
        Zeile(Loc.T("Data Out"), Loc.T("On"));
        Zeile(Loc.T("Data Out IP Address"), adressen.Count == 0 ? Loc.T("this PC's network address") : adressen[0]);
        Zeile(Loc.T("Data Out IP Port"), port.ToString());
        stapel.Controls.Add(tabelle);
        if (adressen.Count > 1)
        {
            stapel.Controls.Add(Notiz(string.Format(Loc.T("If that address does not work, try: {0}"), string.Join(", ", adressen.Skip(1)))));
        }
    }

    /// <summary>
    /// Laesst die Windows-Firewall die Telemetrie herein? Geprueft im Hintergrund, mit
    /// Knopf, wenn nicht (siehe <see cref="Firewall"/>).
    /// </summary>
    private static void FirewallZeile(FlowLayoutPanel stapel, int port)
    {
        var zeile = Notiz(Loc.T("Checking Windows Firewall ..."));
        var knopf = new Button
        {
            Text = Loc.T("Allow in Windows Firewall"), AutoSize = true, FlatStyle = FlatStyle.Flat, ForeColor = Color.White,
            Visible = false, Margin = new Padding(0, 2, 0, 6),
        };
        stapel.Controls.Add(zeile);
        stapel.Controls.Add(knopf);
        var programm = Application.ExecutablePath;
        var zuletzt = (Stand: Firewall.Stand.Unbekannt, Oeffentlich: false);

        void Zeigen((Firewall.Stand Stand, bool Oeffentlich) e)
        {
            zuletzt = e;
            var oeffentlich = e.Oeffentlich ? "  " + Loc.T("Your network is set to Public; the rule then covers that too.") : string.Empty;
            (zeile.Text, zeile.ForeColor, knopf.Visible) = e.Stand switch
            {
                Firewall.Stand.Offen => (Loc.T("Windows Firewall lets the telemetry in."), Gruen, false),
                Firewall.Stand.Blockiert => (Loc.T("Windows Firewall blocks this program, so nothing from the Xbox arrives. Allow it once:") + oeffentlich, Gelb, true),
                Firewall.Stand.Fehlt => (Loc.T("Windows Firewall may block the telemetry. Allow it once:") + oeffentlich, Gelb, true),
                _ => (string.Format(Loc.T("Could not check Windows Firewall. If nothing arrives, allow UDP port {0} there:"), port), Gelb, true),
            };
            // Die Zeile hatte die Breite ihres ersten Textes behalten ("Windows Firewall lets the teler").
            zeile.Parent?.PerformLayout();
        }

        // In der Vorschau (Anleitungsbilder) gleich der Endstand -- vor dem ersten Layout.
        if (MainForm.NurVorschau) { Zeigen((Firewall.Stand.Offen, false)); }

        void Pruefen()
        {
            if (MainForm.NurVorschau) { return; }
            Task.Run(() =>
            {
                var e = Firewall.Pruefen(port, programm);
                try { if (!zeile.IsDisposed) { zeile.BeginInvoke(() => Zeigen(e)); } } catch (Exception) { }
            });
        }

        knopf.Click += (_, _) =>
        {
            knopf.Enabled = false;
            zeile.Text = Loc.T("Windows asks for permission ...");
            var e = zuletzt;
            Task.Run(() =>
            {
                var ok = Firewall.Anlegen(port, e.Oeffentlich, e.Stand == Firewall.Stand.Blockiert, programm);
                var neu = Firewall.Pruefen(port, programm);
                try
                {
                    zeile.BeginInvoke(() =>
                    {
                        knopf.Enabled = true;
                        Zeigen(neu);
                        if (!ok && neu.Stand != Firewall.Stand.Offen)
                        {
                            zeile.Text = Loc.T("Windows did not allow the change.") + "  " + zeile.Text;
                        }
                    });
                }
                catch (Exception)
                {
                }
            });
        };
        zeile.HandleCreated += (_, _) => Pruefen();
    }

    /// <summary>
    /// Xbox Remote Play: der Controller haengt an DIESEM Rechner. Dann gehen die
    /// Vibrationen der App wie am PC (seit 2026-09-29) -- nach einem Neustart, weil die
    /// Reiter beim Start entstehen.
    /// </summary>
    private static CheckBox ControllerHier(FlowLayoutPanel stapel, Rivals.OverlaySettings s)
    {
        stapel.Controls.Add(Kopf(Loc.T("Controller")));
        // Mit der Quelle "Xbox Remote Play" gilt der Haken von selbst (OverlaySettings.ControllerHier).
        var haken = new CheckBox
        {
            Text = Loc.T("My controller is connected to this PC (Xbox Remote Play)"), AutoSize = true,
            ForeColor = Color.Gainsboro, Checked = s.ControllerHier, Enabled = !s.IstRemotePlay,
            Margin = new Padding(0, 3, 0, 0),
        };
        stapel.Controls.Add(haken);
        stapel.Controls.Add(Notiz(Loc.T(
            "Then the app's vibrations work as on the PC: the Vibration test and the Blueprint editor come back after a restart. While telemetry arrives, the app overwrites the vibration that Remote Play passes on, as it does with the game on the PC. Only the game's own vibration setting on the Xbox silences it completely.")));
        haken.CheckedChanged += (_, _) =>
        {
            if (!haken.Enabled) { return; }
            s.ConsoleControllerHere = haken.Checked;
            s.Save();
            NeustartFragen();
        };
        return haken;
    }

    /// <summary>Die Reiter entstehen beim Start: Neustart anbieten.</summary>
    private static void NeustartFragen()
    {
        if (MainForm.NurVorschau) { return; }
        var frage = MessageBox.Show(Loc.T("Restart the app now to apply this?"), AppInfo.Name,
                                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (frage == DialogResult.Yes) { Application.Restart(); }
    }

    /// <summary>
    /// Ein Bericht fuer den, der hilft (seit 2026-09-29): Fassung, Einstellungen, Quelle,
    /// alle Fenster und die letzten Zeilen der Protokolle. Nur auf Klick, in die
    /// Zwischenablage -- nichts verlaesst den Rechner von selbst.
    /// </summary>
    internal static string Bericht(Rivals.OverlaySettings s)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"console mode {s.ConsoleMode}, source {s.VideoSource} \"{s.VideoWindow}\", hud over window {s.HudUeberFenster}, "
                      + $"controller here {s.ControllerHier}, language {s.Language}, lap mode {s.LapMode}");
        var q = Rivals.Bildquellen.Aktiv;
        sb.AppendLine("source: " + (q is null ? "none" : q.Beschreibung)
                      + (q is Rivals.FensterQuelle fq ? ", captured " + fq.Aufgenommen : string.Empty)
                      + (q is Rivals.IFensterBild fb ? $", in front {fb.IstVorne}, area {fb.Schirmflaeche?.ToString() ?? "none"}" : string.Empty));
        sb.AppendLine("overlays over the game: " + Rivals.OverlayAusgabe.ImSpiel);
        sb.AppendLine();
        sb.Append(Rivals.Fenster.Bericht(s.VideoWindow));
        foreach (var (datei, zeilen) in new[] { ("reads.log", 80), ("cars.log", 40) })
        {
            sb.AppendLine();
            sb.AppendLine("---- last lines of " + datei);
            foreach (var z in LetzteZeilen(Path.Combine(Path.GetTempPath(), "forza-overlay", datei), zeilen)) { sb.AppendLine(z); }
        }
        return sb.ToString();
    }

    /// <summary>Die letzten Zeilen einer Datei, ohne sie ganz zu lesen (reads.log wird gross).</summary>
    internal static List<string> LetzteZeilen(string pfad, int anzahl)
    {
        try
        {
            using var f = new FileStream(pfad, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var ab = Math.Max(0, f.Length - (anzahl * 400L));
            f.Seek(ab, SeekOrigin.Begin);
            using var leser = new StreamReader(f);
            var alle = leser.ReadToEnd().Split('\n').Select(z => z.TrimEnd('\r')).Where(z => z.Length > 0).ToList();
            if (ab > 0 && alle.Count > 0) { alle.RemoveAt(0); }
            return alle.Skip(Math.Max(0, alle.Count - anzahl)).ToList();
        }
        catch (Exception)
        {
            return new List<string> { "(not found)" };
        }
    }

    private static void BerichtKopieren(Control wo, Rivals.OverlaySettings s)
    {
        try
        {
            Clipboard.SetText(Bericht(s));
            MessageBox.Show(wo.FindForm(),
                            Loc.T("The report is on the clipboard. Paste it into a message to whoever helps you."),
                            AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Was ins Feld "Fenster" gehoert: der Titel -- auch wenn dort gerade "Titel — Programm" steht.</summary>
    internal static string FensterSchluessel(ComboBox feld) =>
        feld.Items.OfType<Rivals.Fenster.Eintrag>().FirstOrDefault(e => e.ToString() == feld.Text)?.Schluessel
        ?? feld.Text.Trim();

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
        DataOutBlock(stapel, port?.Invoke() ?? 5300);
        FirewallZeile(stapel, port?.Invoke() ?? 5300);

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
                         + " " + Loc.T("Any other window that shows the game works too: pick it from the list, or with a preview of every window."),
            ["discord"] = Loc.T("Watch the Xbox's stream in Discord on this PC, popped out or full screen. Your own stream needs a second Discord account here: your call moves to the Xbox."),
            ["browser"] = Loc.T("Open your stream in the browser, full screen or in theater mode. It runs a few seconds late, which is fine for reading menus; the HUD stays in the dashboard."),
            ["url"] = string.Empty,
        };

        CheckBox? controllerHaken = null;
        var controllerBeimStart = MainForm.ControllerReiterDa ?? s.ControllerHier;
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
        // Neben der Liste das Auswahlfenster von Windows, mit einem Vorschaubild je Fenster
        // (seit 2026-09-29): da erkennt man sein Remote-Play-Fenster, wie immer es heisst.
        var waehlen = new Button
        {
            Text = Loc.T("Pick with a preview …"), AutoSize = true, FlatStyle = FlatStyle.Flat,
            ForeColor = Color.Gainsboro, Margin = new Padding(22, 2, 0, 4), Visible = Rivals.FensterQuelle.Unterstuetzt,
        };
        // Untereinander: nebeneinander passt es in langen Sprachen nicht, und umgebrochen
        // stand der Knopf schief unter der Liste.
        var fensterZeile = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
            FlowDirection = FlowDirection.TopDown, Margin = new Padding(0),
        };
        // FUER DIE FEHLERSUCHE (seit 2026-09-29): steht das eigene Fenster nicht in der
        // Liste, kopiert dieser Verweis alle Fenster in die Zwischenablage -- zum Schicken an
        // den, der hilft. Nur auf Klick; nichts verlaesst den Rechner von selbst.
        var bericht = new LinkLabel
        {
            Text = Loc.T("Your window is not in the list? Copy the list of all windows"), AutoSize = true,
            LinkColor = Color.FromArgb(120, 170, 255), ActiveLinkColor = Color.White,
            Margin = new Padding(22, 0, 0, 4), MaximumSize = new Size(450, 0),
        };
        bericht.LinkClicked += (_, _) => BerichtKopieren(stapel, s);
        fensterZeile.Controls.Add(fenster);
        fensterZeile.Controls.Add(waehlen);
        fensterZeile.Controls.Add(bericht);
        felder["device"] = geraet;
        felder["window"] = fensterZeile;
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
            fensterZeile.Visible = art == "window";
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
                if (art == "window") { s.VideoWindow = FensterSchluessel(fenster); }
                ZeigeFelder();
                Anwenden();
                ControllerAbgleichen();
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
            s.VideoWindow = FensterSchluessel(fenster);
            Spaeter();
        };
        // In der Liste steht "Titel — Programm"; ins Feld gehoert nur der Titel, nach dem gesucht wird.
        fenster.SelectionChangeCommitted += (_, _) =>
        {
            if (fenster.SelectedItem is Rivals.Fenster.Eintrag e)
            {
                fenster.BeginInvoke(() => { fenster.Text = e.Schluessel; fenster.SelectionLength = 0; });
            }
        };
        waehlen.Click += async (_, _) =>
        {
            string? titel;
            try { titel = await Rivals.Fenster.MitWindowsWaehlenAsync(stapel.FindForm()?.Handle ?? IntPtr.Zero); }
            catch (Exception) { titel = string.Empty; }
            if (titel is { Length: 0 }) { return; }
            if (titel is null || (Rivals.Fenster.BildschirmNummer(titel) is null && Rivals.Fenster.Finden(titel) == IntPtr.Zero))
            {
                // Das Auswahlfenster bietet auch ganze Bildschirme an -- die haben keinen Fenstertitel.
                MessageBox.Show(stapel.FindForm(), Loc.T("That is a whole screen, not a window. Pick the window that shows the game."),
                                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            fenster.Text = titel;
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
            foreach (var e in Rivals.Fenster.Waehlbare()) { fenster.Items.Add(e); }
            // Die Liste so breit wie ihr laengster Eintrag, das Feld bleibt, wie es ist.
            using (var g = fenster.CreateGraphics())
            {
                var breit = fenster.Items.Cast<object>()
                                   .Select(i => (int)g.MeasureString(i.ToString(), fenster.Font).Width + 24)
                                   .DefaultIfEmpty(fenster.Width).Max();
                fenster.DropDownWidth = Math.Clamp(breit, fenster.Width, 700);
            }
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
                // Unter der Sperre des Bildes: ein Leser im Hintergrund zeichnet es vielleicht gerade.
                if (Rivals.Bildquellen.Mit(bild, b =>
                    {
                        var k = new Bitmap(320, 180);
                        using var g = Graphics.FromImage(k);
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                        g.DrawImage(b, new Rectangle(0, 0, 320, 180));
                        return (Bild: k, Groesse: b.Size);
                    }, ((Bitmap Bild, Size Groesse)?)null) is { } klein)
                {
                    vorschau.Image?.Dispose();
                    vorschau.Image = klein.Bild;
                    zustand.Text = $"{quelle.Beschreibung} -- {klein.Groesse.Width}×{klein.Groesse.Height}";
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

        // Der Controller unter den Bildquellen: wichtiger ist, dass ueberhaupt etwas ankommt.
        controllerHaken = ControllerHier(stapel, s);

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

        // FUER DIE FEHLERSUCHE: ganz unten, wo man nach allem anderen landet.
        var hilfe = new LinkLabel
        {
            Text = Loc.T("Something not working? Copy a report for whoever helps you"), AutoSize = true,
            LinkColor = Color.FromArgb(120, 170, 255), ActiveLinkColor = Color.White,
            Margin = new Padding(0, 10, 0, 4), MaximumSize = new Size(470, 0),
        };
        hilfe.LinkClicked += (_, _) => BerichtKopieren(stapel, s);
        stapel.Controls.Add(hilfe);
        return stapel;

        // Die Quelle entscheidet mit, ob der Controller hier haengt (Remote Play). Die
        // Reiter dafuer entstehen beim Start -- aendert sich das, Neustart anbieten.
        void ControllerAbgleichen()
        {
            if (controllerHaken is null) { return; }
            controllerHaken.Enabled = false;
            controllerHaken.Checked = s.ControllerHier;
            controllerHaken.Enabled = !s.IstRemotePlay;
            if (s.ControllerHier != controllerBeimStart) { NeustartFragen(); }
        }
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
