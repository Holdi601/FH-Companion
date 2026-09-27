using ForzaHaptics.Tester.Rivals;

namespace ForzaHaptics.Tester;

/// <summary>
/// Ein Fenster nur zum Abbilden: ausserhalb des Schirms, ohne Fokus, ohne Taskleiste.
/// </summary>
/// <remarks>
/// Die Vorschau-Befehle zeigten bis zum 2026-09-25 ein gewoehnliches Fenster -- das
/// nimmt den Fokus und holt einen Spieler aus einem Vollbild-Spiel heraus. So geschehen
/// waehrend einer Partie Counter-Strike. DrawToBitmap braucht nur ein Fenster, keinen
/// Platz auf dem Schirm.
/// </remarks>
internal sealed class VorschauForm : Form
{
    public VorschauForm()
    {
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
    }

    protected override bool ShowWithoutActivation => true;
}

internal static class Program
{
    [System.Runtime.InteropServices.DllImport("gdi32.dll", EntryPoint = "BitBlt")]
    private static extern bool ProbeBitBlt(IntPtr hdcDest, int x, int y, int w, int h,
                                           IntPtr hdcSrc, int x1, int y1, int rop);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetDC")]
    private static extern IntPtr ProbeGetDC(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "ReleaseDC")]
    private static extern int ProbeReleaseDC(IntPtr hWnd, IntPtr hDC);

    private static int TuneDeleteCli(string[] args)
    {
        var echt = args.Contains("--real", StringComparer.OrdinalIgnoreCase);
        var mp = Array.FindIndex(args, a => string.Equals(a, "--max-cars", StringComparison.OrdinalIgnoreCase));
        var max = mp >= 0 && mp + 1 < args.Length && int.TryParse(args[mp + 1], out var m) ? m : int.MaxValue;
        if (!Tuning.ForzaMemoryDb.GameRunning) { Console.WriteLine("The game is not running."); return 2; }

        var datensatz = Path.Combine(AppInfo.DataFolder, "laps.json");
        if (!File.Exists(datensatz)) { Console.WriteLine("No dataset at " + datensatz); return 2; }
        var rat = new RivalsAdvisor(RivalsDataset.Load(datensatz));

        Console.WriteLine("Reading the garage from the game's memory ...");
        string? fund = null;
        var funde = Tuning.ForzaMemoryDb.Dump(Path.Combine(AppInfo.TempFolder, "garage"), s => Console.WriteLine("  " + s),
            pfad =>
            {
                if (Tuning.GarageReader.FindGarage(new[] { pfad }) is null) { return false; }
                fund = pfad;
                return true;
            });
        fund ??= Tuning.GarageReader.FindGarage(funde.Select(f => f.Path));
        if (fund is null) { Console.WriteLine("No garage found in memory -- open My Cars once and try again."); return 2; }
        var nutzung = new Tuning.TuneStorage.Usage { CheckedAt = DateTime.Now, Applied = Tuning.GarageReader.AppliedTunes(fund).ToList() };
        Tuning.TuneStorage.SaveUsage(nutzung);

        var tunes = Tuning.TuneStorage.Read();
        var plan = Tuning.TunesTab.Plan(tunes, nutzung, null, null);
        // "--cars 2542,368": nur diese Autos -- um einen einzelnen Fall nachzustellen.
        var cp = Array.FindIndex(args, a => string.Equals(a, "--cars", StringComparison.OrdinalIgnoreCase));
        if (cp >= 0 && cp + 1 < args.Length)
        {
            var nur = args[cp + 1].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();
            plan = plan.Where(t => nur.Contains(t.CarId)).ToList();
        }
        Console.WriteLine($"{tunes.Count} tunes stored, {nutzung.Applied.Count} on a car, plan: {plan.Count} tunes on "
                          + $"{plan.Select(t => t.CarId).Distinct().Count()} cars. Mode: {(echt ? "DELETE" : "test run")}, "
                          + $"max cars: {(max == int.MaxValue ? "all" : max.ToString())}");
        if (plan.Count == 0) { return 0; }

        // NICHT nach drei Sekunden im Vordergrund loslegen (Probelauf 2026-09-26): wer ins
        // Spiel wechselt, muss erst noch das Pausenmenue oeffnen. Gewartet wird, bis der
        // Schirm zweimal hintereinander das Cars-Menue (oder My Cars) zeigt.
        Console.WriteLine("Waiting for the game's pause menu on the CARS tab ...");
        var treffer = 0;
        var bis = DateTime.UtcNow.AddMinutes(30);
        while (treffer < 2)
        {
            treffer = Tuning.TuneDeleter.AufStartSchirm() ? treffer + 1 : 0;
            if (DateTime.UtcNow > bis) { Console.WriteLine("The CARS tab never showed up."); return 3; }
            Thread.Sleep(700);
        }
        Console.WriteLine("Game in front -- running.");
        var loescher = new Tuning.TuneDeleter(rat, probelauf: !echt, s => Console.WriteLine("  " + s), CancellationToken.None)
        {
            MaxAutos = max,
        };
        Console.WriteLine(loescher.Run(plan));
        return 0;
    }

    [STAThread]
    private static void Main(string[] args)
    {
        // Headless paths run BEFORE any WinForms setup: this is a GUI-subsystem
        // binary, so an exception after Initialize() puts up a modal dialog that
        // nothing will ever click, and the caller waits for ever.
        if (args.Contains(RivalsDump.Flag, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                Environment.Exit(RivalsDump.Run(args));
            }
            catch (Exception exception)
            {
                RivalsDump.WriteFailure(args, exception);
                Environment.Exit(3);
            }
        }

        if (args.Contains(RivalsDump.SourceFlag, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                Environment.Exit(RivalsDump.DatasetSource(args));
            }
            catch (Exception exception)
            {
                RivalsDump.WriteFailure(args, exception, RivalsDump.SourceFlag);
                Environment.Exit(3);
            }
        }

        if (args.Contains(RivalsDump.ReadFlag, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                Environment.Exit(RivalsDump.ReadImage(args));
            }
            catch (Exception exception)
            {
                RivalsDump.WriteFailure(args, exception, "--out");
                Environment.Exit(3);
            }
        }

        // Vor dem Fensteraufbau: dieser Befehl braucht kein WinForms und darf
        // auch keinen Dialog aufmachen, den niemand wegklickt.
        if (args.Contains("--free-roam-replay", StringComparer.OrdinalIgnoreCase))
        {
            Environment.Exit(Rivals.FreeRoamReplay.Run(args));
        }

        // Kopflos pruefbar machen, was sonst nur am Fenster zu sehen waere: welche
        // Sprache gewaehlt wurde, wie viele Saetze vorliegen und wie eine Auswahl
        // tatsaechlich uebersetzt wird. Ohne das bleibt "die Sprache funktioniert"
        // eine Behauptung, die nur jemand mit dem passenden Windows pruefen kann.
        if (args.Contains("--lang-report", StringComparer.OrdinalIgnoreCase))
        {
            var lr = Array.FindIndex(args, a =>
                string.Equals(a, "--lang-report", StringComparison.OrdinalIgnoreCase));
            var wunsch = lr + 1 < args.Length && !args[lr + 1].StartsWith("--")
                ? args[lr + 1] : null;
            if (wunsch is not null) { Loc.Waehle(wunsch); }

            Console.WriteLine("Systemsprache: "
                + System.Globalization.CultureInfo.CurrentUICulture.Name);
            Console.WriteLine("gewaehlt:      " + Loc.Sprache);
            Console.WriteLine("Saetze:        " + Loc.Bekannt);
            Console.WriteLine("vorhanden:     " + string.Join(", ", Loc.Verfuegbar()));
            Console.WriteLine();
            foreach (var probe in new[] { "Stop now", "Always on Top", "Ready",
                                          "What this program does, and what leaves your computer" })
            {
                Console.WriteLine($"  {probe,-56} -> {Loc.T(probe)}");
            }
            Environment.Exit(0);
        }

        // Die eigene Auto-Spalte kopflos pruefbar machen. Ohne das laesst sich
        // "liest der Rundenbestand richtig" nur im laufenden Spiel im Menue
        // nachsehen -- und dort sieht man nicht, WARUM eine Zeile leer bleibt.
        // Was die App ueber die Zustimmung denkt -- kopflos, ohne Fenster.
        // Sehen, was die Vordergrund-Erkennung sieht. Ohne das laesst sich die
        // Win32-Abfrage nur mit laufendem Forza pruefen -- und dann weiss man bei
        // einem Fehlschlag nicht, ob die Abfrage falsch ist oder das Fenster.
        if (args.Contains("--foreground", StringComparer.OrdinalIgnoreCase))
        {
            var fg = Array.FindIndex(args, a =>
                string.Equals(a, "--foreground", StringComparison.OrdinalIgnoreCase));
            var gesucht = fg + 1 < args.Length && !args[fg + 1].StartsWith("--")
                ? args[fg + 1] : GameWatch.DefaultProcessName;
            var wache = new GameWatch(gesucht);
            Console.WriteLine($"gesucht wird:  {gesucht}");
            Console.WriteLine();
            for (var i = 0; i < 6; i++)
            {
                Console.WriteLine($"  vorne: {GameWatch.ForegroundProcessName(),-24} "
                                  + $"laeuft={wache.Running,-5} "
                                  + $"istVorne={wache.IsForeground}");
                System.Threading.Thread.Sleep(700);
            }
            Environment.Exit(0);
        }

        if (args.Contains("--disclosure-state", StringComparer.OrdinalIgnoreCase))
        {
            var s = Rivals.OverlaySettings.Load();
            Console.WriteLine($"Einstellungsdatei: {s.Path ?? "(keine gefunden)"}");
            Console.WriteLine($"disclosure_ack:    {s.DisclosureAcknowledged}");
            Console.WriteLine($"verlangte Fassung: {Disclosure.Fassung}");
            Console.WriteLine($"wuerde fragen:     "
                              + (s.DisclosureAcknowledged >= Disclosure.Fassung
                                 ? "nein" : "JA"));
            Environment.Exit(0);
        }

        if (args.Contains("--overlay-capture-probe", StringComparer.OrdinalIgnoreCase))
        {
            // KOMMT EIN OVERLAY AUF DEN SCHIRM? Jedes der drei Fenster einzeln zeigen,
            // in reinem Gruen und OHNE Aufnahme-Ausblendung, den ganzen Hauptschirm
            // fotografieren (BitBlt mit CAPTUREBLT) und gruene Punkte zaehlen.
            // Gebaut am 2026-09-25, als Umriss und Notiz im Spiel fehlten und sich
            // aus keiner Aufnahme belegen liess, woran es lag.
            var schirm = Screen.PrimaryScreen!.Bounds;
            // Nie ueber einem laufenden Vollbild-Spiel: dort misst die Aufnahme nichts,
            // und die Probefenster stoeren (so geschehen am 2026-09-25, zweimal).
            if (GameArea.VollbildVorne(out var vorne))
            {
                Console.WriteLine($"refused: {vorne} fills the screen -- close or minimise it first");
                return;
            }
            var s = new Rivals.OverlaySettings
            {
                HudBackground = "#ff00ff00", CarNoteBack = "#00ff00", CarNoteBackAlpha = 255,
                CourseShapeBack = "#00ff00", CourseShapeBackAlpha = 255,
            };
            int Gruen()
            {
                using var b = new Bitmap(schirm.Width, schirm.Height);
                using (var g = Graphics.FromImage(b))
                {
                    var ziel = g.GetHdc();
                    var quelle = ProbeGetDC(IntPtr.Zero);
                    ProbeBitBlt(ziel, 0, 0, b.Width, b.Height, quelle, schirm.X, schirm.Y, 0x00CC0020 | 0x40000000);
                    ProbeReleaseDC(IntPtr.Zero, quelle);
                    g.ReleaseHdc(ziel);
                }
                var n = 0;
                for (var y = 0; y < b.Height; y += 4)
                    for (var x = 0; x < b.Width; x += 4)
                    {
                        var p = b.GetPixel(x, y);
                        if (p.G > 225 && p.R < 40 && p.B < 40) { n++; }
                    }
                return n;
            }
            void Warte() { for (var i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(50); } }
            Console.WriteLine($"screen {schirm}, green before: {Gruen()}");
            // Vergleichsfenster: ein gewoehnliches und ein ueber Opacity geschichtetes.
            // Fehlt schon das gewoehnliche, liegt es an der Aufnahme, nicht an den Overlays.
            foreach (var deckung in new[] { 1.0, 0.99 })
            {
                using var f = new Form
                {
                    FormBorderStyle = FormBorderStyle.None, BackColor = Color.Lime, TopMost = true,
                    ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
                    Bounds = new Rectangle(schirm.X + 200, schirm.Y + 200, 300, 200), Opacity = deckung,
                };
                f.Show();
                Warte();
                Console.WriteLine($"plain form opacity={deckung}: green {Gruen()}");
                if (deckung == 1.0)
                {
                    using var b = new Bitmap(schirm.Width / 4, schirm.Height / 4);
                    using var voll = new Bitmap(schirm.Width, schirm.Height);
                    using (var g = Graphics.FromImage(voll))
                    {
                        var ziel = g.GetHdc();
                        var quelle = ProbeGetDC(IntPtr.Zero);
                        ProbeBitBlt(ziel, 0, 0, voll.Width, voll.Height, quelle, schirm.X, schirm.Y, 0x00CC0020 | 0x40000000);
                        ProbeReleaseDC(IntPtr.Zero, quelle);
                        g.ReleaseHdc(ziel);
                    }
                    using (var g = Graphics.FromImage(b)) { g.DrawImage(voll, 0, 0, b.Width, b.Height); }
                    var pfad = Path.Combine(Path.GetTempPath(), "forza-overlay", "probe-screen.png");
                    b.Save(pfad);
                    Console.WriteLine("screen capture: " + pfad);
                }
                f.Hide();
            }
            using (var hud = new Rivals.DeltaHud(schirm, s))
            {
                hud.Show();
                hud.Update(-0.734f, "probe", 12.4f, 0.286f, "probe");
                Warte();
                Console.WriteLine($"DeltaHud visible={hud.Visible} bounds={hud.Bounds}: green {Gruen()}");
                hud.Hide();
            }
            using (var note = new Rivals.CarNoteHud(s, schirm))
            {
                note.AllowCaptureForTest();
                note.SetNote("probe", "overlay capture probe");
                note.Show();
                Warte();
                Console.WriteLine($"CarNoteHud visible={note.Visible} bounds={note.Bounds} pushFailed={note.LastPushFailed}: green {Gruen()}");
                note.Hide();
            }
            using (var umriss = new Rivals.CourseShapeHud(s, schirm))
            {
                umriss.AllowCaptureForTest();
                umriss.SetCourses(new List<(string, Rivals.CourseShape.Outline?)> { ("probe", null) });
                umriss.Show();
                Warte();
                Console.WriteLine($"CourseShapeHud visible={umriss.Visible} bounds={umriss.Bounds} pushFailed={umriss.LastPushFailed}: green {Gruen()}");
                umriss.Hide();
            }
            return;
        }

        if (args.Contains("--hud-editor-preview", StringComparer.OrdinalIgnoreCase))
        {
            // "--hud-editor-preview carnote 150": den Reiter "Lap delta HUD" mit diesem
            // Stueck ausgewaehlt und in dieser Groesse zeichnen. Auf einer KOPIE der
            // Einstellungen -- eine Vorschau schreibt nie in die echte Datei.
            var hp = Array.FindIndex(args, a =>
                string.Equals(a, "--hud-editor-preview", StringComparison.OrdinalIgnoreCase));
            var teil = Rivals.HudPart.CarNote;
            if (hp + 1 < args.Length && Enum.TryParse<Rivals.HudPart>(args[hp + 1], true, out var t)) { teil = t; }
            var kopie = Path.Combine(Path.GetTempPath(), "forza-hud-preview-settings.json");
            var echt = Rivals.OverlaySettings.Load();
            if (echt.Path is not null && File.Exists(echt.Path)) { File.Copy(echt.Path, kopie, overwrite: true); }
            var einst = Rivals.OverlaySettings.Load(kopie);
            if (hp + 2 < args.Length && int.TryParse(args[hp + 2], out var prozent))
            {
                einst.SetScale(teil, prozent / 100.0);
            }
            // Eine vierte Zahl ist die Hoehe -- damit auch das Ende der Seitenleiste ins Bild kommt.
            var hoehe = hp + 3 < args.Length && int.TryParse(args[hp + 3], out var h) ? h : 820;
            using var form = new VorschauForm { Width = 1400, Height = hoehe, Text = "HUD editor" };
            var tab = new Rivals.HudTab(einst, () => { }, _ => { });
            form.Controls.Add(tab);
            form.Show();
            tab.ShowPart(teil);
            Application.DoEvents();
            using var bild = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
            tab.DrawToBitmap(bild, new Rectangle(0, 0, bild.Width, bild.Height));
            var ziel = Path.Combine(Path.GetTempPath(),
                                    $"forza-hud-editor-{teil.ToString().ToLowerInvariant()}.png");
            bild.Save(ziel, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine(ziel);
            return;
        }

        if (args.Contains("--mytimes-preview", StringComparer.OrdinalIgnoreCase))
        {
            // "--mytimes-preview de": in einer bestimmten Sprache zeichnen, damit
            // die Uebersetzung am Bild geprueft wird und nicht nur in der JSON-Datei.
            var mp = Array.FindIndex(args, a =>
                string.Equals(a, "--mytimes-preview", StringComparison.OrdinalIgnoreCase));
            if (mp + 1 < args.Length && !args[mp + 1].StartsWith("--")) { Loc.Waehle(args[mp + 1]); }
            Rivals.RivalsAdvisor? rat = null;
            var dp = Rivals.RivalsDataset.FindDefaultPath();
            if (dp is not null) { rat = new Rivals.RivalsAdvisor(Rivals.RivalsDataset.Load(dp)); }
            using var form = new VorschauForm { Width = 1500, Height = 820, Text = "My times" };
            var tab = new Rivals.OwnTimesTab(() => rat);
            form.Controls.Add(tab);
            form.Show();
            tab.Reload();
            // "--mytimes-preview de points" / "... en time": die Wertung statt der Runden.
            var ansicht = mp + 2 < args.Length ? args[mp + 2].ToLowerInvariant() : "";
            if (ansicht == "points") { tab.ShowView(1); }
            if (ansicht == "time") { tab.ShowView(2); }
            Application.DoEvents();
            using var bild = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bild, new Rectangle(0, 0, form.Width, form.Height));
            var ziel = Path.Combine(Path.GetTempPath(),
                                    "forza-mytimes" + (ansicht.Length > 0 ? "-" + ansicht : "") + ".png");
            bild.Save(ziel, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine(ziel);
            return;
        }

        if (args.Contains("--own-times-check", StringComparer.OrdinalIgnoreCase))
        {
            // Die Filter des Reiters "My times" gegen den ECHTEN Bestand pruefen.
            // Jeder Filter muss etwas tun: ein Filter, der alles durchlaesst, sieht
            // in der Oberflaeche genauso aus wie einer, der funktioniert.
            Rivals.RivalsAdvisor? rat = null;
            var dpfad = Rivals.RivalsDataset.FindDefaultPath();
            if (dpfad is not null) { rat = new Rivals.RivalsAdvisor(Rivals.RivalsDataset.Load(dpfad)); }
            var uhr = System.Diagnostics.Stopwatch.StartNew();
            var laps = Rivals.OwnTimes.All();
            Console.WriteLine($"{laps.Count} Runden gelesen in {uhr.ElapsedMilliseconds} ms");
            var fehler = 0;
            void Pruefe(bool gut, string text)
            {
                Console.WriteLine((gut ? "  ok   " : "  FEHL ") + text);
                if (!gut) { fehler++; }
            }
            int Zahl(Action<Rivals.OwnTimes.Filter> setze, bool beste = false)
            {
                var f = new Rivals.OwnTimes.Filter { BestOnly = beste };
                setze(f);
                return Rivals.OwnTimes.Query(laps, f, rat).Count;
            }

            var alle = Zahl(_ => { });
            Pruefe(alle == laps.Count, $"ohne Filter alle Runden ({alle} von {laps.Count})");

            var beste = Zahl(_ => { }, beste: true);
            Pruefe(beste > 0 && beste < alle, $"nur beste je Auto+Kurs: {beste} (weniger als {alle})");

            var nurB = Zahl(f => f.Classes.Add("B"));
            var echtB = laps.Count(l => l.Klass == "B");
            Pruefe(nurB == echtB && nurB > 0, $"Klasse B: {nurB} (erwartet {echtB})");

            var stehend = Zahl(f => f.Start = "standing");
            var fliegend = Zahl(f => f.Start = "flying");
            Pruefe(stehend + fliegend == alle && stehend > 0 && fliegend > 0,
                   $"stehend {stehend} + fliegend {fliegend} = {alle}");

            var sprint = Zahl(f => f.Kind = "sprint");
            var runde = Zahl(f => f.Kind = "lap");
            Pruefe(sprint + runde == alle, $"Sprint {sprint} + Runde {runde} = {alle}");

            var modi = laps.Select(l => l.Mode).Distinct().ToList();
            Console.WriteLine("       Modi im Bestand: " + string.Join(", ", modi));
            var einModus = modi.First();
            var mitModus = Zahl(f => f.Modes.Add(einModus));
            Pruefe(mitModus == laps.Count(l => l.Mode == einModus),
                   $"Modus '{einModus}': {mitModus}");

            // Kein Ordnername darf als Kursname durchgehen -- weder in der Anzeige
            // noch beim Sortieren, das benannte Kurse vor unbenannte stellt.
            var roh = laps.Where(l => Rivals.OwnTimes.CourseText(l.Course, l.CourseName)
                                          .StartsWith("course_", StringComparison.Ordinal)
                                      || l.CourseName.StartsWith("course_", StringComparison.Ordinal))
                          .Select(l => l.Course).Distinct().ToList();
            var unbenannt = laps.Where(l => l.CourseName.Length == 0).Select(l => l.Course)
                                .Distinct().Count();
            Pruefe(roh.Count == 0,
                   $"kein roher Ordnername als Kursname ({unbenannt} unbenannte Kurse, etwa '"
                   + (laps.FirstOrDefault(l => l.CourseName.Length == 0) is { } ohne ? Rivals.OwnTimes.CourseText(ohne.Course, null) : "-") + "'"
                   + (roh.Count > 0 ? $", roh: {roh[0]}" : string.Empty) + ")");

            var einKurs = laps.First().Course;
            var mitKurs = Zahl(f => f.Courses.Add(einKurs));
            Pruefe(mitKurs == laps.Count(l => l.Course == einKurs) && mitKurs < alle,
                   $"ein Kurs: {mitKurs} Runde(n)");

            if (rat is not null)
            {
                // Marke: eine, die im Bestand vorkommt.
                var marke = laps.Select(l => rat.CarIndexForId(l.Ordinal))
                    .Where(i => i is not null && i < rat.Data.CarMeta.Count)
                    .Select(i => rat.Data.CarMeta[i!.Value]?.Make)
                    .FirstOrDefault(m => !string.IsNullOrEmpty(m));
                if (marke is not null)
                {
                    var mitMarke = Zahl(f => f.Makes.Add(marke));
                    Pruefe(mitMarke > 0 && mitMarke < alle, $"Marke '{marke}': {mitMarke}");
                }
                var tunes = Zahl(f => f.Tunes.Add("same")) + Zahl(f => f.Tunes.Add("up"))
                            + Zahl(f => f.Tunes.Add("down"));
                Pruefe(tunes > 0 && tunes <= alle, $"Abstimmung same+up+down: {tunes} von {alle}");
                var kat = Rivals.OwnTimes.Query(laps, new Rivals.OwnTimes.Filter { BestOnly = false }, rat)
                    .Select(r => r.Category).FirstOrDefault(c => c is not null);
                if (kat is not null)
                {
                    var mitKat = Zahl(f => f.Categories.Add(kat));
                    Pruefe(mitKat > 0 && mitKat <= alle, $"Kategorie '{kat}': {mitKat}");
                }
            }
            var unsinn = Zahl(f => f.CarSearch = "zzzz-gibt-es-nicht");
            Pruefe(unsinn == 0, "Suche nach Unsinn findet nichts");

            Console.WriteLine(fehler == 0 ? "Alle Filter greifen." : $"{fehler} Filter fehlerhaft.");
            Environment.ExitCode = fehler == 0 ? 0 : 1;
            return;
        }

        if (args.Contains("--tune-delete", StringComparer.OrdinalIgnoreCase))
        {
            // "--tune-delete [--real] [--max-cars N]": den Loeschplan im Spiel abarbeiten,
            // ohne Fenster. OHNE --real ein Probelauf, der nichts loescht. Liest zuerst die
            // Garage (welche Tunes auf einem Auto liegen), wartet dann, bis das Spiel vorn
            // ist -- ein Prozess im Hintergrund darf es nicht selbst nach vorn holen.
            Environment.Exit(TuneDeleteCli(args));
        }

        if (args.Contains("--tune-ui-test", StringComparer.OrdinalIgnoreCase))
        {
            // "--tune-ui-test ordner [muster]": jede Aufnahme so auswerten, wie es das
            // automatische Loeschen taete -- Schirm, markierter Eintrag, gelesenes Tune.
            var tp = Array.FindIndex(args, a => string.Equals(a, "--tune-ui-test", StringComparison.OrdinalIgnoreCase));
            var ordner = args[tp + 1];
            var muster = tp + 2 < args.Length ? args[tp + 2] : "*.png";
            var ocr = new Rivals.WindowsOcr();
            foreach (var datei in Directory.GetFiles(ordner, muster).OrderBy(f => f))
            {
                using var bild = new Bitmap(datei);
                using var b = new Bitmap(bild, new Size(1920, 1080));
                var z = ocr.Read(b);
                var s = Tuning.TuneDeleter.Einordnen(z);
                var info = string.Empty;
                if (s is Tuning.TuneDeleter.Schirm.CarsMenu or Tuning.TuneDeleter.Schirm.Upgrades
                        or Tuning.TuneDeleter.Schirm.FileOptions or Tuning.TuneDeleter.Schirm.DeleteConfirm
                        or Tuning.TuneDeleter.Schirm.ActionMenu)
                {
                    info = "marked: " + string.Join(", ", z.Where(l => Tuning.TuneDeleter.Markiert(b, l)).Select(l => l.Text));
                }
                if (s == Tuning.TuneDeleter.Schirm.TunesList)
                {
                    var l = Tuning.TuneDeleter.LiesListe(z, b);
                    info = $"tune '{l.Name}' by '{l.Creator}' created '{l.Datum}' car '{l.AutoZeile}' tile {l.Kachel} icon {l.Symbol}";
                }
                if (s == Tuning.TuneDeleter.Schirm.FileOptions)
                {
                    var d = Tuning.TuneDeleter.LiesDialog(z);
                    info += $"  dialog '{d.Name}' by '{d.Creator}' car '{d.Auto}'";
                }
                Console.WriteLine($"{Path.GetFileName(datei)} {s,-13} {info}");
            }
            return;
        }

        if (args.Contains("--ocr-lines", StringComparer.OrdinalIgnoreCase))
        {
            // "--ocr-lines bild.png ...": jede erkannte Zeile mit Lage (1080p-Bezug) --
            // zum Vermessen neuer Spielschirme.
            var op = Array.FindIndex(args, a => string.Equals(a, "--ocr-lines", StringComparison.OrdinalIgnoreCase));
            var ocr = new Rivals.WindowsOcr();
            for (var i = op + 1; i < args.Length && !args[i].StartsWith("--"); i++)
            {
                using var bild = new Bitmap(args[i]);
                using var skaliert = new Bitmap(bild, new Size(1920, 1080));
                Console.WriteLine("== " + Path.GetFileName(args[i]));
                foreach (var z in ocr.Read(skaliert).OrderBy(z => z.Y).ThenBy(z => z.X))
                {
                    Console.WriteLine($"  {z.X,5:0} {z.Y,5:0}  {z.Text}");
                }
            }
            return;
        }

        if (args.Contains("--delta-preview", StringComparer.OrdinalIgnoreCase))
        {
            // Den Delta-Streifen mit einer Beschriftung zeichnen -- ohne Fenster.
            var dp = Array.FindIndex(args, a => string.Equals(a, "--delta-preview", StringComparison.OrdinalIgnoreCase));
            var text = dp + 1 < args.Length ? args[dp + 1] : "same PI class · Toyota GR Supra '20";
            var zweite = dp + 2 < args.Length ? args[dp + 2] : string.Empty;
            var einst = Rivals.OverlaySettings.Load();
            var schirm = new Rectangle(0, 0, 3840, 2160);
            using var streifen = new Rivals.DeltaHud(schirm, einst);
            streifen.Update(-0.734f, text, -30f, zweite.Length > 0 ? 1.212f : null, zweite);
            using var bild = new Bitmap(schirm.Width, schirm.Height);
            using (var g = Graphics.FromImage(bild))
            {
                g.Clear(Color.FromArgb(40, 46, 54));
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                streifen.PaintInto(g);
            }
            var ziel = Path.Combine(Path.GetTempPath(), "forza-delta.png");
            bild.Save(ziel, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine(ziel);
            return;
        }

        if (args.Contains("--tunes-preview", StringComparer.OrdinalIgnoreCase))
        {
            // Den Reiter "Tunes" zeichnen -- mit dem echten Spielstand, nur gelesen.
            // Mit Visual Styles wie die App selbst: ohne sie zeichnet die Liste keine Gruppen.
            Application.EnableVisualStyles();
            Rivals.RivalsAdvisor? rat = null;
            var dp = Rivals.RivalsDataset.FindDefaultPath();
            if (dp is not null) { rat = new Rivals.RivalsAdvisor(Rivals.RivalsDataset.Load(dp)); }
            var kopie = Path.Combine(Path.GetTempPath(), "forza-tunes-preview-settings.json");
            var echt = Rivals.OverlaySettings.Load();
            if (echt.Path is not null && File.Exists(echt.Path)) { File.Copy(echt.Path, kopie, overwrite: true); }
            var einst = Rivals.OverlaySettings.Load(kopie);
            using var form = new VorschauForm { Width = 1300, Height = 820, Text = "Tunes" };
            var tab = new Tuning.TunesTab(einst, () => rat);
            form.Controls.Add(tab);
            form.Show();
            tab.Neu();
            // "--tunes-preview <garage.db>": eine Garage aus einem alten Abzug als Pruefung
            // vorgeben -- nur fuer dieses Bild, gespeichert wird nichts.
            var tp = Array.FindIndex(args, a => string.Equals(a, "--tunes-preview", StringComparison.OrdinalIgnoreCase));
            if (tp + 1 < args.Length && File.Exists(args[tp + 1]))
            {
                var belegt = Tuning.GarageReader.AppliedTunes(args[tp + 1]);
                tab.VorschauNutzung(new Tuning.TuneStorage.Usage
                {
                    CheckedAt = File.GetLastWriteTime(args[tp + 1]),
                    Applied = belegt.ToList(),
                }, args.Contains("tuners", StringComparer.OrdinalIgnoreCase) ? 2
                   : args.Contains("tunecount", StringComparer.OrdinalIgnoreCase) ? 3
                   : args.Contains("plan", StringComparer.OrdinalIgnoreCase) ? 4 : 1);
            }
            Application.DoEvents();
            using var bild = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
            tab.DrawToBitmap(bild, new Rectangle(0, 0, bild.Width, bild.Height));
            var ziel = Path.Combine(Path.GetTempPath(), "forza-tunes.png");
            bild.Save(ziel, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine(ziel);
            return;
        }

        if (args.Contains("--car-grid-read", StringComparer.OrdinalIgnoreCase))
        {
            // "--car-grid-read bild.png": das Automenue aus einer Aufnahme lesen, genau wie
            // im Spiel -- Rahmen suchen, Titel lesen, Auto bestimmen.
            var gp = Array.FindIndex(args, a =>
                string.Equals(a, "--car-grid-read", StringComparison.OrdinalIgnoreCase));
            var dpfad = Rivals.RivalsDataset.FindDefaultPath();
            if (dpfad is null || gp + 1 >= args.Length)
            {
                Console.WriteLine(dpfad is null ? "no dataset" : "usage: --car-grid-read <png>");
                return;
            }
            var rat = new Rivals.RivalsAdvisor(Rivals.RivalsDataset.Load(dpfad));
            var leser = new Rivals.RivalsScreenReader(rat, Rivals.OverlaySettings.Load());
            for (var i = gp + 1; i < args.Length && !args[i].StartsWith("--"); i++)
            {
                using var bild = new Bitmap(args[i]);
                var r = Rivals.CarGridReader.LiesBild(bild, leser.ReadLines, rat);
                Console.WriteLine(r is null
                    ? $"{Path.GetFileName(args[i])}: no frame"
                    : $"{Path.GetFileName(args[i])}: frame {r.Value.Rahmen} read \"{r.Value.Gelesen}\" -> "
                      + (r.Value.Auto is { } a ? $"{a.Name} (car {a.Ordinal})" : "no car"));
            }
            return;
        }

        if (args.Contains("--shape-hud-preview", StringComparer.OrdinalIgnoreCase))
        {
            // Den Umriss-Streifen so zeichnen, wie er im Spiel liegt.
            //
            // Dahinter Schalter, die nur fuer DIESES Bild gelten (gespeichert wird nichts):
            //   source=rivals|telemetry|auto  style=image|line  layout=vertical|horizontal
            //   smooth=0..100  width=2.5  scale=1.5  out=datei.png
            //   routes="Daikoku Circuit;Hakone Nanamagari"  (Rivals-Karten nach Namen)
            var einst = Rivals.OverlaySettings.Load();
            string? Wert(string schluessel) => args.Select(a => a.Split('=', 2))
                .Where(t => t.Length == 2 && string.Equals(t[0], schluessel, StringComparison.OrdinalIgnoreCase))
                .Select(t => t[1]).LastOrDefault();
            if (Wert("source") is { } q) { einst.CourseShapeSource = q; }
            if (Wert("style") is { } st) { einst.CourseRivalsStyle = st; }
            if (Wert("layout") is { } la) { einst.CourseShapeLayout = la; }
            if (int.TryParse(Wert("smooth"), out var gl)) { einst.CourseShapeSmooth = gl; }
            if (double.TryParse(Wert("width"), System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var br)) { einst.CourseShapeWidth = br; }
            if (double.TryParse(Wert("scale"), System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var sk)) { einst.HudCourseScale = sk; }
            einst.CourseShapes = true;
            var schirm = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            using var hud = new Rivals.CourseShapeHud(einst, schirm);
            var wurzel = Rivals.LapArchive.Root;
            var drei = new List<(string, Rivals.CourseShape.Outline?)>();
            if (Wert("routes") is { } routen)
            {
                foreach (var r in routen.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    drei.Add((r, Rivals.CourseShape.ForRoute(r, einst.ShapeSourceChoice)));
                }
            }
            else
            {
                foreach (var ordner in Directory.EnumerateDirectories(wurzel))
                {
                    var kurs = Path.GetFileName(ordner);
                    var u = Rivals.CourseShape.For(kurs, einst.ShapeSourceChoice, wurzel);
                    if (u is null || u.IsEmpty) { continue; }
                    var name = Rivals.CourseShape.KursName(wurzel, kurs);
                    drei.Add((string.IsNullOrEmpty(name) ? kurs : name, u));
                    if (drei.Count >= 3) { break; }
                }
            }
            // Eine Strecke absichtlich ohne Umriss: der Kasten "not driven yet"
            // muss auch stimmen, und der faellt sonst nie auf.
            drei.Add(("Never Driven Circuit", null));
            // states=done,now,next -- der Stand einer Meisterschaft, je Kachel.
            var stati = (Wert("states") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => Enum.TryParse<Rivals.CourseShapeHud.TileState>(x, true, out var st) ? st : Rivals.CourseShapeHud.TileState.None)
                .ToList();
            hud.SetCourses(drei, stati);
            using var bild = new Bitmap(schirm.Width, schirm.Height);
            using (var g = Graphics.FromImage(bild))
            {
                g.Clear(Color.FromArgb(40, 46, 54));
                hud.Paint(g);
            }
            var ziel = Wert("out") ?? Path.Combine(Path.GetTempPath(), "forza-shape-hud.png");
            bild.Save(ziel, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"{drei.Count} Kacheln gezeichnet");
            Console.WriteLine(ziel);
            return;
        }

        // SCHLUESSEL UND UNTERSCHRIFT FUER UPDATES -- siehe Rivals.UpdateSignature.
        //   --update-key-new [pfad]          einmalig: Schluesselpaar anlegen
        //   --update-sign <zip> [schluessel]  Unterschrift (Base64) ausgeben
        //   --update-verify <zip> <sig>       0 = gueltig
        if (args.Length >= 1 && string.Equals(args[0], "--update-key-new", StringComparison.OrdinalIgnoreCase))
        {
            var pfad = args.Length > 1 ? args[1] : Rivals.UpdateSignature.DefaultKeyPath;
            Console.WriteLine(Rivals.UpdateSignature.NewKey(pfad));
            Environment.Exit(0);
        }
        if (args.Length >= 2 && string.Equals(args[0], "--update-sign", StringComparison.OrdinalIgnoreCase))
        {
            var schluessel = args.Length > 2 ? args[2] : Rivals.UpdateSignature.DefaultKeyPath;
            Console.WriteLine(Rivals.UpdateSignature.Sign(args[1], schluessel));
            Environment.Exit(0);
        }
        if (args.Length >= 3 && string.Equals(args[0], "--update-verify", StringComparison.OrdinalIgnoreCase))
        {
            var gut = Rivals.UpdateSignature.Verify(args[1], args[2]);
            Console.WriteLine(gut ? "valid" : "INVALID");
            Environment.Exit(gut ? 0 : 1);
        }

        if (args.Length >= 2 && string.Equals(args[0], "--submit-lap", StringComparison.OrdinalIgnoreCase))
        {
            // DENSELBEN WEG GEHEN WIE NACH EINER GEFAHRENEN RUNDE -- nur mit einer
            // Runde aus dem Archiv. Prueft Entscheidung, Anmeldung und Einreichung.
            //   --submit-lap <runde.json> [--dry-run] [--gamertag X] [--server URL]
            string? Opt(string name)
            {
                var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            }
            var pfad = Path.GetFullPath(args[1]);
            // Das Archiv legt {"Course", "Tag", "Class", "Lap": {...}} ab; eine blanke
            // Runde geht auch.
            var knoten = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(pfad))
                         ?? throw new InvalidDataException("kein JSON in " + pfad);
            var lapKnoten = knoten["Lap"] ?? knoten;
            var lap = System.Text.Json.JsonSerializer.Deserialize<Rivals.RecordedLap>(lapKnoten)
                      ?? throw new InvalidDataException("keine Runde in " + pfad);
            // laps/<kurs>/<klasse>/car<n>/<tune>/<tag>/<datei>
            var kursOrdner = new DirectoryInfo(Path.GetDirectoryName(pfad)!).Parent?.Parent?.Parent?.Parent;
            var kurs = (string?)knoten["Course"] ?? kursOrdner?.Name ?? string.Empty;
            var wurzel = kursOrdner?.Parent?.FullName ?? Rivals.LapArchive.Root;
            // Der Kurs aus der Datei, und falls der (alter Schluessel) nichts hergibt,
            // der Ordner, in dem sie heute liegt.
            var strecke = Rivals.LapAutoSubmit.RouteName(lap, wurzel, kurs, kursOrdner?.Name ?? string.Empty);
            var einst = Rivals.OverlaySettings.Load();
            if (Opt("--gamertag") is { } gt) { einst.Gamertag = gt; }
            if (Opt("--server") is { } sv) { einst.DatasetUrl = sv; }
            einst.SubmitLaps = true;
            Rivals.RivalsAdvisor? rat = null;
            var dp = Rivals.RivalsDataset.FindDefaultPath();
            if (dp is not null) { rat = new Rivals.RivalsAdvisor(Rivals.RivalsDataset.Load(dp)); }
            Console.WriteLine($"lap {lap.LapSeconds:0.000} s, car {lap.CarOrdinal}, class {lap.CarClass}, route '{strecke}', course {kurs}");
            var ergebnis = new Rivals.LapAutoSubmit(() => rat, einst, _ => { })
                .ConsiderAsync(lap, kurs, strecke, dryRun: args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase))
                .GetAwaiter().GetResult();
            Console.WriteLine(ergebnis);
            Environment.Exit(ergebnis.StartsWith("submitted") || ergebnis.StartsWith("would submit") ? 0 : 3);
        }

        if (args.Contains("--update-security-check", StringComparer.OrdinalIgnoreCase))
        {
            // Die Schutzregeln des Updaters, jede so gebaut, dass sie ohne die Regel
            // fehlschluege.
            var fehler = 0;
            void Pruefe(bool gut, string text)
            {
                Console.WriteLine((gut ? "  ok   " : "  FEHL ") + text);
                if (!gut) { fehler++; }
            }
            var ordner = Path.Combine(Path.GetTempPath(), "forza-update-check");
            Directory.CreateDirectory(ordner);
            var datei = Path.Combine(ordner, "paket.zip");
            File.WriteAllBytes(datei, System.Security.Cryptography.RandomNumberGenerator.GetBytes(4096));
            var schluessel = Rivals.UpdateSignature.DefaultKeyPath;
            if (File.Exists(schluessel))
            {
                var sig = Rivals.UpdateSignature.Sign(datei, schluessel);
                Pruefe(Rivals.UpdateSignature.Verify(datei, sig), "richtig unterschriebenes Paket gilt");
                var falsch = Path.Combine(ordner, "veraendert.zip");
                var bytes = File.ReadAllBytes(datei); bytes[1234] ^= 0x01;
                File.WriteAllBytes(falsch, bytes);
                Pruefe(!Rivals.UpdateSignature.Verify(falsch, sig), "ein veraendertes Byte macht die Unterschrift ungueltig");
            }
            else
            {
                Console.WriteLine("  (kein privater Schluessel auf diesem Rechner -- Signieren nicht geprueft)");
            }
            Pruefe(!Rivals.UpdateSignature.Verify(datei, null), "ohne Unterschrift wird nichts angenommen");
            Pruefe(!Rivals.UpdateSignature.Verify(datei, "AAAA"), "eine Unsinns-Unterschrift wird abgelehnt");
            var boese = new Rivals.AppUpdate.Fassung { Name = @"..\..\..\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\x.cmd" };
            Pruefe(Rivals.AppUpdate.ZielName(boese) == "fh-companion.zip", "Pfad im Paketnamen wird nicht als Pfad benutzt");
            var gut = new Rivals.AppUpdate.Fassung { Name = "fh-companion-20260924.zip" };
            Pruefe(Rivals.AppUpdate.ZielName(gut) == "fh-companion-20260924.zip", "ein normaler Paketname bleibt");
            try
            {
                Rivals.AppUpdate.DownloadUri("https://updates.example.org:8787", new Rivals.AppUpdate.Fassung { Url = "http://evil.example/x.zip" });
                Pruefe(false, "Download von einem fremden Rechner abgelehnt");
            }
            catch (InvalidDataException) { Pruefe(true, "Download von einem fremden Rechner abgelehnt"); }
            try
            {
                // Seit HTTPS (2026-09-25): eine Metadatei, die auf DENSELBEN Rechner,
                // aber ueber http:// verweist, wuerde den Download herabstufen.
                Rivals.AppUpdate.DownloadUri("https://updates.example.org:8787", new Rivals.AppUpdate.Fassung { Url = "http://updates.example.org:8787/download/haptics" });
                Pruefe(false, "ein Download-Verweis auf http:// bei HTTPS-Server abgelehnt");
            }
            catch (InvalidDataException) { Pruefe(true, "ein Download-Verweis auf http:// bei HTTPS-Server abgelehnt"); }
            var eigene = Rivals.AppUpdate.DownloadUri("https://updates.example.org:8787", new Rivals.AppUpdate.Fassung { Url = "/download/haptics" });
            Pruefe(eigene.ToString() == "https://updates.example.org:8787/download/haptics", "der eigene Download-Pfad bleibt erlaubt (und bleibt HTTPS)");
            // Die Vorgabe kommt beim Bauen aus config/local.json; ohne sie gibt es
            // keine alte http-Vorgabe, die zu heben waere.
            if (Rivals.OverlaySettings.OldDefaultServer is { } altServer)
            {
                Pruefe(Rivals.OverlaySettings.UpgradeServer(altServer + "/") == Rivals.OverlaySettings.DefaultServer
                       && Rivals.OverlaySettings.DefaultServer.StartsWith("https://"),
                       "die alte http-Vorgabe wird beim Laden auf HTTPS gehoben");
            }
            else
            {
                Console.WriteLine("  (ohne eingebaute Server-Vorgabe gebaut -- Anhebung nicht geprueft)");
            }
            Pruefe(Rivals.OverlaySettings.UpgradeServer("http://192.168.1.5:8787") == "http://192.168.1.5:8787",
                   "eine selbst eingetragene Adresse bleibt, wie sie ist");
            Pruefe(Rivals.ServerHttp.SameServer("http://updates.example.org:8787", "https://updates.example.org:8787/")
                   && !Rivals.ServerHttp.SameServer("https://updates.example.org:8787", "https://evil.example:8787")
                   && !Rivals.ServerHttp.SameServer("https://updates.example.org:8787", "https://updates.example.org:8788"),
                   "derselbe Server ueber http und https behaelt seine Anmeldung, ein anderer nicht");
            Console.WriteLine(fehler == 0 ? "Alle Update-Regeln greifen." : $"{fehler} Regel(n) greifen NICHT.");
            Environment.Exit(fehler == 0 ? 0 : 1);
        }

        if (args.Contains("--rivals-maps-check", StringComparer.OrdinalIgnoreCase))
        {
            // Hat JEDE Rivalen-Strecke des Katalogs eine Karte, nach ihrem Namen --
            // auch die nie gefahrenen? Genau so fragen die Kacheln vor dem Rennen.
            var katalogPfad = new[] { "config", Path.Combine("..", "config") }
                .Select(p => Path.Combine(AppContext.BaseDirectory, p, "fh6_board_catalogue.json"))
                .Concat(new[] { Path.Combine(Directory.GetCurrentDirectory(), "config", "fh6_board_catalogue.json") })
                .FirstOrDefault(File.Exists);
            if (katalogPfad is null) { Console.WriteLine("kein Katalog gefunden"); Environment.Exit(2); }
            using var katalog = System.Text.Json.JsonDocument.Parse(File.ReadAllText(katalogPfad));
            var uhr = System.Diagnostics.Stopwatch.StartNew();
            int alle = 0, mit = 0;
            foreach (var kat in katalog.RootElement.GetProperty("tracks").EnumerateObject())
            {
                var fehlt = new List<string>();
                int n = 0, da = 0;
                foreach (var e in kat.Value.GetProperty("verified").EnumerateArray())
                {
                    var name = e.GetProperty("name").GetString() ?? string.Empty;
                    n++;
                    if (Rivals.CourseShape.ForRoute(name, Rivals.ShapeSource.Rivals) is not null) { da++; }
                    else { fehlt.Add(name); }
                }
                alle += n; mit += da;
                Console.WriteLine($"  {kat.Name,-14} {da,2}/{n}" + (fehlt.Count > 0 ? "  fehlt: " + string.Join(", ", fehlt) : ""));
            }
            Console.WriteLine($"{mit} von {alle} Strecken mit Karte, {uhr.ElapsedMilliseconds} ms");
            Environment.Exit(mit == alle ? 0 : 1);
        }

        if (args.Contains("--shape-preview", StringComparer.OrdinalIgnoreCase))
        {
            // Jeden Kurs des Bestands als Umriss zeichnen, in ein Blatt.
            // Zum Hinsehen: eine Form, die falsch herum oder verzerrt ist, faellt
            // nur im Bild auf, nicht in einer Zahl.
            var sp = Array.FindIndex(args, a =>
                string.Equals(a, "--shape-preview", StringComparison.OrdinalIgnoreCase));
            var quelleWahl = sp + 1 < args.Length
                ? args[sp + 1].ToLowerInvariant() switch
                  {
                      "rivals" => Rivals.ShapeSource.Rivals,
                      "telemetry" => Rivals.ShapeSource.Telemetry,
                      _ => Rivals.ShapeSource.Auto,
                  }
                : Rivals.ShapeSource.Auto;
            var wurzel = Rivals.LapArchive.Root;
            var kurse = Directory.Exists(wurzel)
                ? Directory.GetDirectories(wurzel).Select(Path.GetFileName)
                           .Where(k => !string.IsNullOrEmpty(k)).OrderBy(k => k).ToList()
                : new List<string?>();
            const int Kachel = 190, Spalten = 8, Rand = 10;
            var reihen = Math.Max(1, (kurse.Count + Spalten - 1) / Spalten);
            using var blatt = new Bitmap(Spalten * Kachel, reihen * Kachel);
            using (var g = Graphics.FromImage(blatt))
            {
                g.Clear(Color.FromArgb(12, 16, 22));
                using var schrift = new Font("Segoe UI", 7f);
                using var text = new SolidBrush(Color.Gainsboro);
                var treffer = 0;
                for (var i = 0; i < kurse.Count; i++)
                {
                    var kurs = kurse[i]!;
                    // Quelle ueber die Kommandozeile waehlbar: --shape-preview rivals
            var u = Rivals.CourseShape.For(kurs, quelleWahl, wurzel);
                    var x = (i % Spalten) * Kachel;
                    var y = (i / Spalten) * Kachel;
                    var kasten = new RectangleF(x + Rand, y + Rand + 12,
                                                Kachel - (2 * Rand), Kachel - (2 * Rand) - 22);
                    if (u is not null && !u.IsEmpty)
                    {
                        treffer++;
                        Rivals.CourseShape.Draw(g, u, kasten,
                            u.Source == Rivals.ShapeSource.Telemetry
                                ? Color.FromArgb(120, 200, 255) : Color.FromArgb(230, 90, 200),
                            2.0f, Color.FromArgb(255, 210, 90));
                    }
                    var name = Rivals.CourseShape.KursName(wurzel, kurs);
                    g.DrawString(string.IsNullOrEmpty(name) ? kurs : name,
                                 schrift, text, x + 4, y + 2);
                }
                Console.WriteLine($"{treffer} von {kurse.Count} Kursen haben einen Umriss");
            }
            var ziel = Path.Combine(Path.GetTempPath(), "forza-course-shapes.png");
            blatt.Save(ziel, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine(ziel);
            return;
        }

        if (args.Contains("--own-cars", StringComparer.OrdinalIgnoreCase))
        {
            var oc = Array.FindIndex(args, a =>
                string.Equals(a, "--own-cars", StringComparison.OrdinalIgnoreCase));
            var klasse = oc + 1 < args.Length && !args[oc + 1].StartsWith("--")
                ? args[oc + 1] : null;
            var karte = new Rivals.OrdinalMap();
            // Den Ratgeber mitladen, damit dieser Bericht DENSELBEN Rueckfall zeigt
            // wie das Overlay: erst das Gelernte, dann das Ordinal als car_id.
            // Ohne ihn saehe hier alles unzugeordnet aus, waehrend im Spiel Namen
            // stuenden -- ein Bericht, der etwas anderes prueft als den Ernstfall.
            Rivals.RivalsAdvisor? ratgeber = null;
            try
            {
                var pfad = Rivals.RivalsDataset.FindDefaultPath();
                if (pfad is not null)
                {
                    ratgeber = new Rivals.RivalsAdvisor(Rivals.RivalsDataset.Load(pfad));
                }
            }
            catch (Exception fehler)
            {
                Console.WriteLine($"Datensatz nicht ladbar: {fehler.Message}");
            }

            string Name(int ordinal)
            {
                var e = karte.Lookup(ordinal);
                if (e is not null) { return e.Name + "  [gelernt]"; }
                var idx = ratgeber?.CarIndexForId(ordinal);
                return idx is not null
                    ? ratgeber!.CarName(idx.Value) + "  [angenommen]"
                    : "(nicht zugeordnet)";
            }

            Console.WriteLine($"Rundenbestand: {Rivals.LapArchive.Root}");
            Console.WriteLine($"Runden gesamt: {Rivals.OwnCars.TotalLaps()}");
            Console.WriteLine();

            foreach (var k in klasse is null
                         ? new[] { "D", "C", "B", "A", "S1", "S2", "R" }
                         : new[] { klasse })
            {
                var besten = Rivals.OwnCars.Bests(k);
                if (besten.Count == 0) { continue; }
                Console.WriteLine($"Klasse {k}: {besten.Count} Auto(s)");
                foreach (var b in besten)
                {
                    Console.WriteLine(
                        $"  {Rivals.OwnCars.TimeText(b.BestSeconds),9}  "
                        + $"{Rivals.OwnCars.ConditionText(b.Standing, b.Sprint),-15} "
                        + $"ordinal {b.Ordinal,-6} "
                        + $"{b.Laps,3} Runde(n) auf {b.Courses,2} Kurs(en)  "
                        + Name(b.Ordinal));
                }
                foreach (var d in Rivals.OwnCars.Duels(k))
                {
                    Console.WriteLine(
                        $"  Direktvergleich {d.Course} "
                        + $"({Rivals.OwnCars.ConditionText(d.Standing, d.Sprint)}): "
                        + string.Join(" > ", d.Order.Select(
                            o => Name(o.Ordinal).Split("  [")[0]
                                 + " " + Rivals.OwnCars.TimeText(o.Seconds))));
                }
                Console.WriteLine();
            }
            Environment.Exit(0);
        }

        if (args.Contains("--tuning-report", StringComparer.OrdinalIgnoreCase))
        {
            Environment.Exit(Tuning.TuningReport.Run(args));
        }

        ApplicationConfiguration.Initialize();

        // DAS PANEL ALS BILD. Eine Aenderung an der Darstellung laesst sich sonst
        // nur mit laufendem Spiel beurteilen -- und genau dort faellt zu spaet auf,
        // dass ein langer Autoname in die Zahlenspalte laeuft.
        if (args.Contains("--disclosure-preview", StringComparer.OrdinalIgnoreCase))
        {
            // Das Zustimmungsfenster abbilden, ohne es zu bedienen. Seit dem
            // 2026-09-16 stehen dort drei Angebote und ein erklaerender Absatz
            // untereinander; ob die noch nebeneinander passen, sieht man nur.
            string? ziel = null;
            Disclosure.BeimZeigen = f =>
            {
                using var bild = new Bitmap(f.Width, f.Height);
                f.DrawToBitmap(bild, new Rectangle(0, 0, f.Width, f.Height));
                ziel = Path.Combine(Path.GetTempPath(), "forza-disclosure.png");
                bild.Save(ziel, System.Drawing.Imaging.ImageFormat.Png);
                f.DialogResult = DialogResult.Cancel;
                f.Close();
            };
            Disclosure.Zeigen(erstesMal: true, out _);
            Disclosure.BeimZeigen = null;
            Console.WriteLine(ziel ?? "nichts gezeichnet");
            return;
        }

        if (args.Contains("--panel-preview", StringComparer.OrdinalIgnoreCase))
        {
            var pv = Array.FindIndex(args, a =>
                string.Equals(a, "--panel-preview", StringComparison.OrdinalIgnoreCase));
            var klasse = pv + 1 < args.Length && !args[pv + 1].StartsWith("--")
                ? args[pv + 1] : "B";

            var einst = Rivals.OverlaySettings.Load();
            var karte = new Rivals.OrdinalMap();
            Rivals.RivalsAdvisor? rat = null;
            try
            {
                var dpfad = Rivals.RivalsDataset.FindDefaultPath();
                if (dpfad is not null)
                {
                    rat = new Rivals.RivalsAdvisor(Rivals.RivalsDataset.Load(dpfad));
                }
            }
            catch (Exception fehler)
            {
                Console.WriteLine("Datensatz nicht ladbar: " + fehler.Message);
            }

            var zeilen = new List<Rivals.PanelLine>();
            // DIE STRECKEN DES ANMELDESCHIRMS, so wie sie dort stehen:
            //   --panel-preview B "Sunflower Scramble:8.5:3" "Kinkaku-ji Trail:5.5"
            // Name, Gesamtstrecke in km, Rundenzahl. Ohne Angabe die drei aus dem
            // Bildschirmfoto vom 2026-09-16, an dem der Fehler auffiel.
            var routen = new List<(string Name, double LapMetres)>();
            var namen = new List<string>();
            for (var a = pv + 2; a < args.Length; a++)
            {
                var stueck = args[a].Split(':');
                if (stueck.Length < 2) { continue; }
                if (!double.TryParse(stueck[1], System.Globalization.NumberStyles.Float,
                                     System.Globalization.CultureInfo.InvariantCulture,
                                     out var km))
                {
                    continue;
                }
                var runden = stueck.Length > 2
                             && int.TryParse(stueck[2], out var r) && r > 0 ? r : 1;
                routen.Add((stueck[0], km * 1000.0 / runden));
                namen.Add(stueck[0]);
            }
            if (routen.Count == 0)
            {
                routen.Add(("Sunflower Scramble", 8500.0 / 3));
                routen.Add(("Kinkaku-ji Trail", 5500.0));
                routen.Add(("Bamboo Forest Scramble", 15000.0 / 3));
                namen.AddRange(routen.Select(x => x.Name));
            }

            // GENAU DERSELBE AUFBAU WIE IN RenderAdvice. Eine Vorschau, die die
            // Zeilen anders baut als das Overlay, prueft ihre eigene Erfindung.
            static string Kurz(string name)
            {
                if (name.Length <= 12) { return name; }
                var raum = name.IndexOf(' ');
                return raum > 0 ? name[..raum] : name;
            }

            var tabelle = Rivals.OwnCars.Table(klasse, routen);
            var kopf = new string[routen.Count];
            for (var i = 0; i < kopf.Length; i++)
            {
                kopf[i] = Kurz(tabelle.Courses[i].Label);
            }
            zeilen.Add(new Rivals.PanelLine(
                $"YOUR TIMES ON THESE ROUTES ({klasse})",
                string.Empty, Rivals.OverlayPanel.Bar, Small: true, Cells: kopf));

            if (tabelle.Rows.Count == 0)
            {
                var mehrdeutig = tabelle.Courses.Any(c => c.Ambiguous);
                zeilen.Add(new Rivals.PanelLine(
                    mehrdeutig
                        ? "several of your courses share these lengths — cannot tell them apart"
                        : "you have no recorded laps on these routes",
                    string.Empty, Rivals.OverlayPanel.Muted, Small: true, Indent: 10));
            }

            var platz = 1;
            foreach (var zeile in tabelle.Rows.Take(12))
            {
                var e = karte.Lookup(zeile.Ordinal);
                var ix = e?.CarIndex ?? rat?.CarIndexForId(zeile.Ordinal);
                // Dieselbe Regel wie im Overlay: nur echte Namen, sonst gar nichts.
                var name = (Rivals.RivalsAdvisor.IsRealCarName(e?.Name) ? e!.Name : null)
                           ?? (ix is { } kx && rat is not null
                               ? rat.RealCarName(kx) : null);
                if (name is null) { continue; }
                if (e is null && ix is not null) { name = "~" + name; }
                if (platz <= 3) { name = $"#{platz * 7} {name}"; }

                var zellen = new string[tabelle.Courses.Count];
                for (var i = 0; i < zellen.Length; i++)
                {
                    var key = tabelle.Courses[i].Key;
                    zellen[i] = key.Length > 0
                                && zeile.ByCourse.TryGetValue(key, out var s)
                        ? Rivals.OwnCars.TimeText(s)
                        : "–";
                }
                zeilen.Add(new Rivals.PanelLine(
                    name, string.Empty, Rivals.OverlayPanel.Ink,
                    Small: true, Indent: 10, Cells: zellen));
                platz++;
            }
            zeilen.Add(new Rivals.PanelLine("by points · 3 route(s)", string.Empty,
                                            Rivals.OverlayPanel.Bar, Heading: true));
            if (rat is not null)
            {
                // Echte Autonamen aus dem Datensatz, damit die Laengen stimmen.
                for (var i = 0; i < 14; i++)
                {
                    var name = rat.CarName(i);
                    zeilen.Add(new Rivals.PanelLine(
                        $"{i + 1,2}. {name}", $"{900 - (i * 30)} pts",
                        Rivals.OverlayPanel.Ink,
                        Mine: i % 4 == 0 ? "~1:08.09" : string.Empty));
                }
            }

            var schirm = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            // Dieselbe Breite wie das echte Panel, sonst prueft die Vorschau eine
            // Spaltenaufteilung, die es so gar nicht gibt.
            var breite = (int)(schirm.Width * einst.SideWidthFraction);
            var hoehe = schirm.Height;
            using var panel = new Rivals.OverlayPanel(
                new Rectangle(0, 0, breite, hoehe), 1.0);
            panel.SetContent($"Class {klasse} – what to drive",
                             string.Join(" · ", namen), zeilen,
                             "your best lap on each of the three routes above");
            panel.Show();
            Application.DoEvents();
            using var bild = new Bitmap(panel.Width, panel.Height);
            panel.DrawToBitmap(bild, new Rectangle(0, 0, panel.Width, panel.Height));
            var ziel = Path.Combine(Path.GetTempPath(),
                                    $"forza-panel-{klasse}.png");
            bild.Save(ziel, System.Drawing.Imaging.ImageFormat.Png);
            panel.Hide();
            Console.WriteLine($"{zeilen.Count} Zeilen, {breite}x{hoehe} Pixel");
            Console.WriteLine(ziel);
            Environment.Exit(0);
        }

        // Nur die Grenzfaelle: kein Fenster, keine Overlays -- darf laufen, waehrend
        // jemand spielt (der volle Selbsttest zeigt Overlays auf dem Schirm).
        if (args.Contains("--edge-case-test", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                EdgeCaseTest.Run();
                Console.WriteLine("Edge cases OK.");
                Environment.Exit(0);
            }
            catch (Exception ausnahme)
            {
                Console.WriteLine(ausnahme.Message);
                Environment.Exit(1);
            }
        }

        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            SelfTest.Run();
            return;
        }

        // ERST ERKLAEREN, DANN LAUFEN. Das Programm liest fremden Prozessspeicher,
        // tauscht sich selbst aus und holt Daten von einem Server. Keines davon darf
        // geschehen, bevor der Nutzer weiss, dass es geschieht -- und ein Text, den
        // niemand liest, ist keine Erklaerung. Siehe Disclosure.
        //
        // Vor `new MainForm()`, weil RivalsTab die Einstellungen im Konstruktor
        // laedt: die Zustimmung steht dann schon in der Datei.
        //
        // Davor der Umzug vom alten Namen (siehe AppInfo): die Einstellungen der
        // Overlays liegen im Datenordner, und der muss schon am neuen Platz sein.
        _ = AppInfo.DataFolder;

        // EIN FENSTER JE KOPIE (siehe Einzelinstanz). Laeuft diese Kopie schon --
        // etwa unsichtbar im Infobereich, weil sie mit Forza startet --, holt ein
        // zweiter Start sie nach vorne und endet. Vor der Erklaerung: die hat die
        // erste Instanz schon gezeigt.
        var imHintergrund = args.Contains(Shortcuts.TrayArgument, StringComparer.OrdinalIgnoreCase);
        using var instanz = Einzelinstanz.Anmelden();
        if (!instanz.Erste)
        {
            if (!imHintergrund) { instanz.ErsteWecken(); }
            return;
        }

        var einstellungen = Rivals.OverlaySettings.Load();

        // Die Sprache VOR dem ersten Fenster. WinForms liest die Beschriftungen
        // beim Aufbau, nicht laufend -- wer sie danach setzt, aendert nichts mehr.
        Loc.Waehle(einstellungen.Language);

        if (!Disclosure.SicherstellenAkzeptiert(einstellungen))
        {
            return;
        }

        AppInfo.Aufraeumen();

        try
        {
            var fenster = new MainForm(imHintergrund);
            instanz.Horchen(() =>
            {
                try { fenster.BeginInvoke(fenster.VonAussenZeigen); }
                catch (Exception) { }
            });
            Application.Run(fenster);
        }
        finally
        {
            Sdl.QuitGamepads();
        }
    }
}
