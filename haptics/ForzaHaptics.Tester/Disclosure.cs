using System.Diagnostics;

namespace ForzaHaptics.Tester;

/// <summary>
/// Beim ersten Start sagen, was das Programm tut -- und eine Zustimmung einholen.
/// </summary>
/// <remarks>
/// ## Warum es das gibt
///
/// Dieses Programm tut drei Dinge, die ein Nutzer wissen MUSS, bevor sie geschehen:
/// es liest den Arbeitsspeicher eines fremden Prozesses, es tauscht sich beim Update
/// selbst aus, und es holt sich Daten von einem Server. Jedes davon hat einen guten
/// Grund. Keines davon darf ungefragt passieren.
///
/// Dazu kommt ein praktischer Anlass: Windows Defender meldet die App bei neuen
/// Nutzern als <c>Trojan:Win32/Bearfoos.A!ml</c> -- ein Fehlalarm des Lernverfahrens
/// (siehe <c>docs/defender-false-positive.md</c>). Wer die Warnung ungewarnt
/// bekommt, loescht die Datei. Wer vorher gelesen hat, WARUM das Programm so
/// aussieht, kann selbst urteilen.
///
/// ## Warum der Haken erst unten freigeschaltet wird
///
/// Weil "gelesen" sonst eine Behauptung ist. Der Haken wird erst anklickbar, wenn
/// der Text bis zum Ende gescrollt wurde. Das ist keine Schikane, sondern der
/// Unterschied zwischen einer Zustimmung und einem Wegklicken.
///
/// Passt der Text ohne Scrollen ins Fenster, ist er damit auch gelesen -- dann wird
/// sofort freigeschaltet. Sonst haette ein grosser Bildschirm den Haken tot gemacht.
///
/// ## Was NICHT drinsteht
///
/// Nichts, was die App nicht tut. Bis 2026-09-15 hatte <c>LapSubmit</c> keinen
/// Aufrufer; seit 2026-09-24 reicht <c>LapAutoSubmit</c> Runden ein, die die
/// Bestenliste schlagen -- das steht darum im Text, und <see cref="Fassung"/> ist
/// dafuer auf 3 gegangen. Jede weitere Aenderung an dem, was hinausgeht: Text UND
/// Fassung, sonst gilt eine Zustimmung fuer etwas, das nicht mehr stimmt.
/// </remarks>
internal static class Disclosure
{
    /// <summary>
    /// Die Fassung dieses Textes. HOCHZAEHLEN, wenn sich aendert, WAS die App tut.
    /// </summary>
    /// <remarks>
    /// Eine Zustimmung gilt fuer den Text, dem zugestimmt wurde. Wer Fassung 1
    /// zugestimmt hat und in Fassung 2 wuerde plotzlich Telemetrie verschickt,
    /// hat dem nicht zugestimmt. Darum haengt die Zustimmung an dieser Zahl und
    /// nicht an einem blossen "ja".
    /// </remarks>
    // 3 seit 2026-09-24: die App reicht jetzt beste Runden ein (LapAutoSubmit),
    // voreingestellt AN. Wer Fassung 2 zugestimmt hat, stimmte "sendet nichts" zu.
    // 4 seit 2026-09-26: der Text sagte, der Spielspeicher werde nur im Reiter
    // Tuning gelesen -- inzwischen auch in "Tunes" und "Car notes" (je auf Knopf).
    // Neu genannt: das Lesen des Spielbilds (Anmeldeschirm, Automenue), das Lesen
    // des Spielstand-Ordners (Tunes) und das Loeschen, das Tasten ans Spiel schickt.
    // 5 seit 2026-09-27: die Haptik laeuft ab Werk (Standardgraph, Schalter an) -- und
    // eine Vibration kostet Akku. Wer Fassung 4 zugestimmt hat, kannte einen
    // Controller, der erst nach eigenem Einschalten vibrierte. Am selben Tag, noch
    // vor der Auslieferung, dazu: "Mit Forza starten" und Runden, die warten und
    // SPAETER gesendet werden -- auch solche, die bei ausgeschaltetem Einreichen
    // gefahren wurden. Wer "aus" gewaehlt hatte, muss davon lesen, bevor er es
    // wieder einschaltet. Und: ohne Gamertag wird jetzt AUCH eingereicht (als
    // vorlaeufiger Spielername) -- Fassung 4 versprach "Nothing is sent without a
    // gamertag".
    public const int Fassung = 5;

    private const string Titel = "What " + AppInfo.Name + " does";

    private const string Text = """
Please read this once. It explains what the program does on your PC, what leaves
your computer, and why Windows may warn you about it.


WHAT IT DOES ON YOUR PC

  Reads telemetry from the game.
      Forza Horizon 6 can send its own telemetry to your own machine over UDP.
      You switch that on yourself in the game under Settings > HUD and Gameplay.
      The program listens on 127.0.0.1 -- your own computer, not the network --
      and uses it for the lap delta, the live map, your lap records and the
      controller vibration.

  Reads the memory of the running game -- only when you press a button.
      The list of parts fitted to your car, and which tune sits on which car, are
      not in the telemetry. They sit in a small database the game keeps in its
      own memory. To show them, the program opens the Forza process read-only and
      searches that memory.

      It only READS. It never writes to the game and never changes anything in
      it. It does this only when you press the button for it on the Tuning,
      Tunes or Car notes tab. If you never press one, the game's memory is never
      touched.

  Reads what the game shows on screen.
      To know which routes the Event Sign Up screen offers and which car is
      highlighted in the car menu, the program takes pictures of the game window
      and reads the text with the text recognition built into Windows. This
      happens on your PC; the pictures are not sent anywhere.

  Reads your downloaded tunes from the game's save folder -- read-only.
      The Tunes tab counts them and shows which ones no car uses. The save folder
      is never changed by this.

  Deletes tunes in the game -- only when you start it.
      On the Tunes tab you can let the program delete unused tunes through the
      game's own menus. It then switches to the game and presses the keys itself.
      It asks first, offers a test run that deletes nothing, never deletes a
      tune that is on a car, and stops on Alt+Tab or the Pause key.

      This is also the single biggest reason antivirus software distrusts the
      program: reading another process's memory is what a cheat or a password
      stealer does. Here it reads the part list of your own car in a single-player
      racing game.

  Makes your controller vibrate -- and that drains its battery faster.
      While Forza runs and sends telemetry, the program drives the vibration of
      your controller: tyre grip becomes rumble, a locking wheel a short buzz. This
      is on from the start. A wireless controller will run out of battery
      noticeably sooner while it vibrates, and the stronger and faster (higher
      frequency) you set the vibration in the Blueprint editor, the more power it
      draws. Untick "Graph output enabled" in the Blueprint editor to switch it
      off; the program remembers that.

  Watches for key presses.
      Only to catch the hotkeys -- F10 sets a start/finish line in free roam.
      It checks whether those specific keys are down. It does not record what you
      type and there is no keystroke log anywhere.

  Draws an overlay on top of the game.
      The lap delta, the maps, the car notes and the rivals panels are separate
      windows drawn over Forza.

  Records your laps -- on your disk.
      Times, positions and speeds are written into the program's own folder so it
      can show you a delta against your own best. Only a lap that beats the
      leaderboard is sent anywhere -- see below.


WHAT LEAVES YOUR COMPUTER

  The program contacts the server for exactly four things:

      1. To ask whether the car ratings are newer than the ones you have,
         and to fetch them if they are.
      2. To ask whether a newer version of this program exists.
      3. To download that new version, when you press the update button.
      4. To submit a lap that BEATS the leaderboard -- ON unless you switch it off.

  About 4: a lap is sent only if it is faster than the best leaderboard time of
  that same car on that route and class, and faster than anything you sent for it
  before. Sent are: the car, the route, the class, the time, and the lap's
  telemetry (positions and speeds along the lap, so the server can check the time
  is real), plus a hashed identifier of this PC so abuse can be blocked -- and your
  gamertag, if you set one in the Rivals tab. A gamertag is not required: without
  one the lap is still sent and appears on the website under a temporary player
  name. A gamertag you set later replaces it on all your laps, also the earlier
  ones, and emptying the field later keeps the last name. The server checks the
  lap again before it shows it. Switch it off in the Rivals tab
  ("Submit my laps ...") or set "submit_laps": false in config/overlay.json.

  A lap that beats the leaderboard but cannot be sent right then -- submission
  switched off, offline, or the server not answering -- is kept on
  this computer and sent later, once all of that is fine again, even weeks later.
  Before it goes, it is checked once more against the leaderboard of that day and
  dropped if it is no longer faster. So switching submission back on also sends
  the best laps you drove while it was off. "Discard waiting laps" in the Rivals
  tab deletes them instead.

  Nothing else is sent. Your ordinary laps are not uploaded. Your tune is not
  uploaded. There is no account and nothing to sign in to.

  Because your computer asks those questions, the server sees that some
  installation asked -- that is unavoidable for any program that checks for
  updates. It is counted so we know roughly how many people use this, and the
  identifier is hashed with a secret that stays on the server and is deleted after
  40 days. If that bothers you, set "offline": true in config/overlay.json and the
  program will never contact the server again -- it then runs entirely on the data
  that came in the download, and everything except updating keeps working.


HOW IT UPDATES ITSELF

  When you press the update button, the program downloads a ZIP, verifies its
  publisher's signature, unpacks it, and replaces its own files. A package that is
  not signed by the publisher is refused. A small console window appears
  while that happens -- that window is meant to be visible, so you can see what is
  being replaced. Your settings folder is kept.

  Nothing is installed. Nothing is written to the Windows registry. Delete the
  folder and the program is gone.


WHY WINDOWS MAY CALL IT A TROJAN

  Windows Defender may report this program as Trojan:Win32/Bearfoos.A!ml and block
  it. The suffix "!ml" means this is not a match against any known malware -- it is
  a guess by a machine-learning model about a file it has never seen.

  It guesses that way because the program is not code-signed, almost nobody has it
  yet, and it does the three things listed above: reads another process's memory,
  replaces its own files, and downloads from the internet. Those are exactly the
  behaviours the model was trained to distrust.

  Every release is scanned before it is published, and the download page shows a
  checksum so you can verify that the file you got is the file that was built. If
  you would rather not take that on trust, do not run it -- that is a legitimate
  choice and this program will not nag you about it.


SHORTCUTS, IF YOU WANT THEM

  Below this text you can ask for a desktop shortcut, a Start menu entry, and for
  the program to start with Forza. All three are off unless you tick them, all
  three are just a .lnk file in a folder, and you can delete any of them later
  without touching the program.

  "Start with Forza" puts the program in your Windows startup folder. From
  sign-in it waits invisibly in the notification area and opens (minimized) when
  Forza starts; after the game it goes back there. Windows does not let a program
  start when another program starts without administrator rights -- waiting in
  the notification area needs none and costs practically nothing. Closing the
  window keeps it waiting; right-click the icon to quit. The button "Start with
  Forza" at the top right of the main window switches it on and off.

  Windows does not allow a program to pin itself to the taskbar -- that has been
  reserved for you since Windows 10 version 1607, and getting around it means
  forging a checksum in the registry. This program does not do that. Tick the Start
  menu entry, then right-click it and choose "Pin to taskbar".


NO WARRANTY

  This is a hobby project, given away for free, with no warranty of any kind. It is
  not affiliated with, endorsed by, or connected to Microsoft, Turn 10, Playground
  Games or the Forza Horizon series.


You can read this again at any time: the link "What this program does" at the
top of the main window.
""";

    /// <summary>
    /// Zeigen, falls dieser Fassung noch nicht zugestimmt wurde.
    /// </summary>
    /// <returns><c>false</c>, wenn der Nutzer ablehnt -- dann startet die App nicht.</returns>
    public static bool SicherstellenAkzeptiert(Rivals.OverlaySettings settings)
    {
        if (settings.DisclosureAcknowledged >= Fassung) { return true; }
        if (!Zeigen(erstesMal: true, out var gewuenscht)) { return false; }
        settings.DisclosureAcknowledged = Fassung;
        settings.Save();

        // ERST NACH DER ZUSTIMMUNG. Wer den Dialog abbricht, bekommt nichts
        // angelegt -- auch dann nicht, wenn die Kaestchen vorher angekreuzt waren.
        //
        // Scheitert eines, wird das GESAMMELT gemeldet. Drei Meldungsfenster
        // nacheinander sind keine Auskunft, sondern eine Strafe.
        var schiefgegangen = new List<string>();
        foreach (var ort in gewuenscht)
        {
            if (!Shortcuts.Create(ort, out var fehler))
            {
                schiefgegangen.Add(string.IsNullOrWhiteSpace(fehler)
                                   ? ort.ToString() : ort + ": " + fehler);
            }
        }
        if (schiefgegangen.Count > 0)
        {
            MessageBox.Show(
                Loc.T("The desktop shortcut could not be created.")
                + Environment.NewLine + Environment.NewLine
                + string.Join(Environment.NewLine, schiefgegangen),
                Titel, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        return true;
    }

    /// <summary>Den Text zeigen. Beim Nachlesen ohne Haken und ohne Abbruch.</summary>
    /// <summary>Zum Nachlesen: ohne Haken, ohne Abbruch, ohne Verknuepfungsfrage.</summary>
    /// <summary>
    /// Wird aufgerufen, sobald das Fenster steht -- fuer die Vorschau.
    /// </summary>
    /// <remarks>
    /// Damit sich das Zustimmungsfenster abbilden laesst, ohne es wirklich zu
    /// bedienen. Ein Fenster, das nur der Nutzer je zu sehen bekommt, ist ein
    /// Fenster, dessen Aufteilung niemand prueft -- und genau dort sind diesem
    /// Projekt schon Zeilen ineinandergelaufen.
    ///
    /// Im Betrieb ist der Haken <c>null</c> und kostet einen Vergleich.
    /// </remarks>
    internal static Action<Form>? BeimZeigen;

    public static bool Zeigen(bool erstesMal) => Zeigen(erstesMal, out _);

    /// <param name="gewuenscht">
    /// Welche Verknuepfungen angelegt werden sollen. Leer heisst: keine.
    /// </param>
    public static bool Zeigen(bool erstesMal, out List<ShortcutPlace> gewuenscht)
    {
        gewuenscht = new List<ShortcutPlace>();
        using var f = new Form
        {
            Text = Titel,
            Icon = Marke.Symbol(),
            StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = erstesMal,
            FormBorderStyle = FormBorderStyle.Sizable,
            ClientSize = new Size(760, 620),
            MinimumSize = new Size(560, 420),
        };

        // RichTextBox und nicht TextBox: nur die erste meldet VScroll, und ohne
        // dieses Ereignis laesst sich nicht feststellen, ob jemand unten angekommen
        // ist -- also auch nicht, ob er gelesen hat.
        var text = new RichTextBox
        {
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            DetectUrls = false,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Text = Text.Replace("\n", Environment.NewLine),
            Font = new Font(FontFamily.GenericMonospace, 9.25f),
            BackColor = SystemColors.Window,
            WordWrap = true,
        };

        // Hoeher als frueher: drei Angebote statt einem, dazu die Zeile, die
        // erklaert, warum die Taskleiste nicht dabei ist.
        var unten = new Panel { Dock = DockStyle.Bottom, Height = erstesMal ? 196 : 56 };

        var haken = new CheckBox
        {
            Text = Loc.T("I have read the above and understand what this program does."),
            AutoSize = true,
            Enabled = false,
            Location = new Point(14, 12),
            Visible = erstesMal,
        };

        // NICHTS IST VORAUSGEWAEHLT. Etwas auf den Schreibtisch eines anderen zu
        // legen, weil er ein Kaestchen uebersehen hat, ist kein Angebot -- es ist
        // eine Zumutung, die man hinterher wegraeumen muss. Wer es will, kreuzt an.
        // Fuer den Autostart gilt das doppelt: ungefragt mitzustarten ist genau
        // das, was man Programmen vorwirft.
        var verknuepfung = new CheckBox
        {
            Text = Loc.T("Put a shortcut on my desktop"),
            AutoSize = true,
            Checked = false,
            Location = new Point(14, 38),
            Visible = erstesMal && !Shortcuts.Exists(ShortcutPlace.Desktop),
        };

        var startmenue = new CheckBox
        {
            Text = Loc.T("Add it to the Start menu"),
            AutoSize = true,
            Checked = false,
            Location = new Point(14, 64),
            Visible = erstesMal && !Shortcuts.Exists(ShortcutPlace.StartMenu),
        };

        // Seit 2026-09-27 "Mit Forza starten": dieselbe Autostart-Verknuepfung, aber
        // mit --tray -- die App wartet unsichtbar im Infobereich und geht erst mit
        // Forza auf (siehe Shortcuts.TrayArgument).
        var autostart = new CheckBox
        {
            Text = Loc.T("Start with Forza (waits in the notification area from sign-in)"),
            AutoSize = true,
            Checked = false,
            Location = new Point(14, 90),
            Visible = erstesMal && !Shortcuts.Exists(ShortcutPlace.Autostart),
        };

        // WARUM DIE TASKLEISTE NICHT DABEI IST -- im Fenster und nicht nur im
        // Quelltext, weil genau danach gefragt wurde. Windows laesst das Anheften
        // seit Version 1607 nur noch von Hand zu; der Weg dorthin fuehrt ueber das
        // Startmenue. Ausfuehrlich in Shortcuts.cs.
        var hinweis = new Label
        {
            // EIN EINZIGES LITERAL. Zusammengesetzte Zeichenketten findet der
            // Schluesselauszug in scripts/lang_keys.py nicht -- der Satz waere
            // stillschweigend nie uebersetzt worden.
            Text = Loc.T("Windows only lets you pin to the taskbar yourself: right-click the Start menu entry and choose “Pin to taskbar”."),
            AutoSize = false,
            Location = new Point(32, 116),
            Size = new Size(640, 34),
            ForeColor = SystemColors.GrayText,
            Visible = erstesMal,
        };

        var weiter = new Button
        {
            Text = erstesMal ? "Continue" : "Close",
            DialogResult = DialogResult.OK,
            Enabled = !erstesMal,
            AutoSize = true,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };

        var abbrechen = new Button
        {
            Text = Loc.T("Quit"),
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            Visible = erstesMal,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };

        void Platzieren()
        {
            hinweis.Width = Math.Max(200, unten.Width - 60);
            var y = unten.Height - weiter.Height - 14;
            weiter.Location = new Point(unten.Width - weiter.Width - 14, y);
            abbrechen.Location = new Point(weiter.Left - abbrechen.Width - 10, y);
        }

        unten.Controls.Add(haken);
        unten.Controls.Add(verknuepfung);
        unten.Controls.Add(startmenue);
        unten.Controls.Add(autostart);
        unten.Controls.Add(hinweis);
        unten.Controls.Add(weiter);
        unten.Controls.Add(abbrechen);
        unten.Resize += (_, _) => Platzieren();

        haken.CheckedChanged += (_, _) => weiter.Enabled = haken.Checked;

        // GELESEN HEISST UNTEN ANGEKOMMEN. Der Haken bleibt gesperrt, bis der Text
        // bis zum Ende gescrollt wurde -- oder bis feststeht, dass es nichts zu
        // scrollen gibt, weil er ohnehin ganz ins Fenster passt.
        void Pruefen()
        {
            if (!erstesMal || haken.Enabled) { return; }
            if (GanzGesehen(text)) { haken.Enabled = true; }
        }

        text.VScroll += (_, _) => Pruefen();
        text.KeyUp += (_, _) => Pruefen();
        text.Resize += (_, _) => Pruefen();
        f.Shown += (_, _) => { Platzieren(); Pruefen(); BeimZeigen?.Invoke(f); };

        f.Controls.Add(text);
        f.Controls.Add(unten);
        f.AcceptButton = weiter;
        if (erstesMal) { f.CancelButton = abbrechen; }

        var ergebnis = f.ShowDialog();
        if (ergebnis == DialogResult.OK)
        {
            if (verknuepfung.Visible && verknuepfung.Checked)
            {
                gewuenscht.Add(ShortcutPlace.Desktop);
            }
            if (startmenue.Visible && startmenue.Checked)
            {
                gewuenscht.Add(ShortcutPlace.StartMenu);
            }
            if (autostart.Visible && autostart.Checked)
            {
                gewuenscht.Add(ShortcutPlace.Autostart);
            }
        }
        return ergebnis == DialogResult.OK;
    }

    /// <summary>Ist das Ende des Textes sichtbar gewesen?</summary>
    /// <remarks>
    /// Ueber die erste sichtbare Zeile plus die Anzahl sichtbarer Zeilen. Das
    /// funktioniert auch dann, wenn gar nicht gescrollt werden muss -- und genau
    /// dieser Fall haette den Haken sonst auf einem grossen Schirm nie freigegeben.
    /// </remarks>
    private static bool GanzGesehen(RichTextBox box)
    {
        try
        {
            var ersteSichtbar = box.GetLineFromCharIndex(box.GetCharIndexFromPosition(
                new Point(1, 1)));
            var letzteSichtbar = box.GetLineFromCharIndex(box.GetCharIndexFromPosition(
                new Point(1, box.ClientSize.Height - 2)));
            var letzte = box.GetLineFromCharIndex(box.TextLength);
            return letzteSichtbar >= letzte - 1 && letzteSichtbar >= ersteSichtbar;
        }
        catch (Exception)
        {
            // Lieber freigeben als jemanden aussperren: der Text stand sichtbar da.
            return true;
        }
    }

    /// <summary>Die ausfuehrliche Fassung im Browser.</summary>
    public static void Nachlesen(string? baseUrl)
    {
        var ziel = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl!.TrimEnd('/') + "/app";
        if (ziel is null) { return; }
        try
        {
            Process.Start(new ProcessStartInfo { FileName = ziel, UseShellExecute = true });
        }
        catch (Exception)
        {
            // Kein Browser, kein Drama.
        }
    }
}
