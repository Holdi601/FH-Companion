using System.Drawing;
using System.Drawing.Imaging;

namespace ForzaHaptics.Tester;

/// <summary>
/// The awkward inputs: broken files, empty data, extreme screens, names that are not names.
/// </summary>
/// <remarks>
/// Added on 2026-09-24 as part of <c>--self-test</c>. The other checks prove each
/// feature works on good input; these prove it does not fall over on bad input --
/// which is what a user's machine actually delivers: a notes file cut off by a
/// crash, an archive with a stray file in it, a monitor nobody measured on.
///
/// Every case throws with a sentence saying what broke, like the rest of the
/// self-test, so a failure names itself.
/// </remarks>
internal static class EdgeCaseTest
{
    private static void Soll(bool gut, string was)
    {
        if (!gut) { throw new InvalidOperationException("Grenzfall: " + was); }
    }

    public static void Run()
    {
        CarNotesFiles();
        CarNamePlaceholders();
        RouteLookups();
        HudsAtExtremes();
        OwnTimesOnBadArchives();
        ScreenMaths();
        ReaderOnDegenerateFrames();
        SubmitDecision();
        HudPartsAreSeparate();
        OwnStandings();
        LiveMapOrientation();
        MapLines();
        CarMenuNotes();
        LapMessageAfterRace();
        TuneStorageReading();
        ChampionshipStates();
        CourseNamesFromLaps();
        ShapeDisplayTime();
        TuneForLap();
        AppliedTuneForNote();
        DeletionPlan();
        TuneDeleterReading();
        RenameMigration();
        BrandResources();
        SeriesStatusWords();
        CarNoteGoesWhenDriving();
        StartWithForza();
        LapsWaitForTheServer();
        CelebrationLooksRight();
        OverlayOutputAndCost();
        TyreOverview();
        ReplacedControllerGoesQuiet();
        LiveMapStaysWithoutStrip();
        LapModeFromMenus();
        PersonalRecordsDecide();
        CelebrationsWaitWhileDriving();
        ConsoleModeReadsItsSource();
        FullTelemetryTravelsWithTheLap();
        PictureSourcesFindTheGame();
        CarCollectionKnowsWhatIsMissing();
        XboxAndMemoryOptions();
    }

    /// <summary>Eine Bildquelle zum Testen: ein festes Bild.</summary>
    private sealed class FesteQuelle : Rivals.IBildquelle
    {
        public Bitmap Bild { get; } = new(1920, 1080, PixelFormat.Format32bppArgb);
        public string Beschreibung => "test";
        public Bitmap? Neuestes() => Bild;
        public void Dispose() => Bild.Dispose();
    }

    /// <summary>
    /// Konsolenmodus (2026-09-28): ohne Videoquelle wird nichts gelesen; mit einer liest
    /// GameArea aus IHREM Bild -- Flaeche, Ausschnitt und Farbe stimmen.
    /// </summary>
    /// <summary>
    /// Jede eingereichte Runde traegt ihre volle Telemetrie (seit 2026-09-28) -- aus dem
    /// Speicher, und fuer eine wartende Runde aus ihrer Nebendatei. Kein lokaler Pfad
    /// darf dabei in den Rumpf geraten.
    /// </summary>
    /// <summary>
    /// Die vier Wege zum Spielbild (seit 2026-09-28): das 16:9-Spielbild in einem Fenster
    /// mit Balken finden -- und einen gleichmaessigen Himmel NICHT fuer einen Balken halten;
    /// ein Fenster per Graphics Capture aufnehmen, auch ausserhalb des Schirms.
    /// </summary>
    /// <summary>
    /// "Car collection": die Liste vom Server, wer was besitzt, und die Saetze dazu.
    /// </summary>
    private static void CarCollectionKnowsWhatIsMissing()
    {
        static string Liste(int n, string format = "fhc-cars-1")
        {
            var autos = Enumerable.Range(1, n).Select(i =>
                "{\"name\":\"Car " + i + "\",\"year\":2020,\"id\":" + i
                + ",\"ways\":[{\"k\":\"autoshow\",\"price\":" + (1000 * i) + "}]}");
            return "{\"format\":\"" + format + "\",\"built\":\"2026-09-29T01:00:00Z\",\"cars\":["
                   + string.Join(",", autos) + "]}";
        }
        var liste = Rivals.CarCollection.Lesen(Liste(150));
        Soll(liste is { Autos.Count: 150 }, "eine vollstaendige Autoliste wird nicht gelesen");
        Soll(Rivals.CarCollection.Lesen(Liste(150, "other")) is null, "eine Liste in fremdem Format gilt");
        Soll(Rivals.CarCollection.Lesen(Liste(20)) is null, "eine halbe Liste gilt -- alles andere stuende als fehlend da");
        Soll(Rivals.CarCollection.Lesen("{") is null, "kaputtes JSON wirft statt null");

        var a1 = liste!.Autos[0];   // id 1
        var a2 = liste.Autos[1];    // id 2
        var gefahren = new HashSet<int> { 1 };
        var besitz = new Rivals.OwnedCars();
        Soll(besitz.Besitz(a1, gefahren) == (true, Rivals.OwnedCars.Grund.Gefahren)
             && !besitz.Besitz(a2, gefahren).Hat, "ohne Garage zaehlt das gefahrene Auto nicht als eigenes");
        besitz.GarageMerken(new[] { 2 });
        Soll(!besitz.Besitz(a1, gefahren).Hat && besitz.Besitz(a2, gefahren) == (true, Rivals.OwnedCars.Grund.Garage),
             "eine gelesene Garage entscheidet nicht allein (ein gefahrenes Leihauto gilt als gekauft)");
        besitz.Setze(a1, true, gefahren);
        Soll(besitz.Besitz(a1, gefahren) == (true, Rivals.OwnedCars.Grund.VonHand), "ein Haken von Hand zaehlt nicht");
        besitz.Setze(a2, true, gefahren);
        Soll(!besitz.Markiert.ContainsKey(a2.Schluessel), "ein Haken, der nichts aendert, wird gemerkt");
        besitz.Setze(a2, false, gefahren);
        Soll(besitz.Besitz(a2, gefahren) == (false, Rivals.OwnedCars.Grund.VonHand), "ein entfernter Haken ueberstimmt die Garage nicht");

        var w = Rivals.CarCollection.Lesen(
            "{\"format\":\"fhc-cars-1\",\"cars\":[" + string.Join(",", Enumerable.Repeat(
                "{\"name\":\"X\",\"year\":2001,\"ways\":[{\"k\":\"autoshow\",\"price\":65000},"
                + "{\"k\":\"playlist\",\"series\":2,\"season\":\"Winter\",\"wk\":\"champ\",\"wx\":\"Retro Rewind\"},"
                + "{\"k\":\"aftermarket\",\"where\":\"SHI6\",\"price\":18750},{\"k\":\"dlc\",\"pack\":\"Car Pass\"}]}", 120))
            + "]}")!.Autos[0].Wege;
        var texte = w.Select(Rivals.CarCollectionTab.WegLang).ToList();
        Soll(texte[0].Contains("65") && texte[0].Contains("CR"), "Autoshow ohne Preis: " + texte[0]);
        Soll(texte[1].Contains("2") && texte[1].Contains("Retro Rewind"), "Festival Playlist ohne Serie oder Meisterschaft: " + texte[1]);
        Soll(texte[2].Contains("SHI6") && texte[2].Contains("18"), "Aftermarket ohne Ort oder Preis: " + texte[2]);
        Soll(Rivals.CarCollectionTab.WegKurz(w[3]) == "DLC: Car Pass", "DLC ohne Paketnamen: " + Rivals.CarCollectionTab.WegKurz(w[3]));

        // JEDE ART, auch mit fehlenden Feldern und unbekannten Werten: nie leer, nie "{0}", nie eine Ausnahme.
        static Rivals.CarCollection.Weg W(string json) =>
            System.Text.Json.JsonSerializer.Deserialize<Rivals.CarCollection.Weg>(json)!;
        var alleWege = new[]
        {
            "{\"k\":\"autoshow\"}", "{\"k\":\"autoshow\",\"price\":\"viel\"}", "{\"k\":\"playlist\"}",
            "{\"k\":\"playlist\",\"series\":4,\"season\":\"Winter, week 2\"}",
            "{\"k\":\"playlist\",\"series\":1,\"season\":\"Monsoon\",\"wk\":\"lab\",\"what\":\"EventLab: \\\"X\\\"\"}",
            "{\"k\":\"playlist\",\"wk\":\"season\",\"wx\":\"20\"}", "{\"k\":\"playlist\",\"wk\":\"trial\",\"wx\":\"Y\"}",
            "{\"k\":\"playlist\",\"wk\":\"series\",\"wx\":\"30\"}", "{\"k\":\"playlist\",\"wk\":\"champ\"}",
            "{\"k\":\"wheelspin\"}", "{\"k\":\"aftermarket\"}", "{\"k\":\"aftermarket\",\"event\":\"E\"}",
            "{\"k\":\"barn\"}", "{\"k\":\"barn\",\"where\":\"Ito\"}", "{\"k\":\"treasure\"}",
            "{\"k\":\"treasure\",\"where\":\"Hokubu\"}", "{\"k\":\"mastery\"}", "{\"k\":\"mastery\",\"car\":\"C\"}",
            "{\"k\":\"journal\"}", "{\"k\":\"journal\",\"points\":100}", "{\"k\":\"campaign\"}",
            "{\"k\":\"campaign\",\"band\":\"Blue\"}", "{\"k\":\"gift\"}", "{\"k\":\"gift\",\"date\":\"D\"}",
            "{\"k\":\"loyalty\"}", "{\"k\":\"loyalty\",\"game\":\"FH4\"}", "{\"k\":\"dlc\"}", "{\"k\":\"auction\"}",
            "{\"k\":\"unobtainable\"}", "{\"k\":\"somethingnew\"}", "{\"k\":\"\"}",
        };
        foreach (var roh in alleWege)
        {
            var weg = W(roh);
            var lang = Rivals.CarCollectionTab.WegLang(weg);
            var kurz = Rivals.CarCollectionTab.WegKurz(weg);
            Soll(!string.IsNullOrWhiteSpace(lang + kurz) || weg.Art.Length == 0, "leerer Satz fuer " + roh);
            Soll(!lang.Contains("{0}") && !kurz.Contains("{0}"), "roher Platzhalter fuer " + roh + ": " + lang);
        }
        Soll(Rivals.CarCollectionTab.WegLang(W(alleWege[3])).Contains("4"), "Woche/Serie fehlt: " + Rivals.CarCollectionTab.WegLang(W(alleWege[3])));
        Soll(Rivals.CarCollectionTab.WegLang(W(alleWege[4])).Contains("EventLab"), "unbekannte Art faellt nicht auf den englischen Satz zurueck");
        Soll(Rivals.CarCollectionTab.WegKurz(W(alleWege[29])) == "somethingnew", "eine neue Art des Servers verschwindet statt angezeigt zu werden");

        // Eine Liste mit fremden Feldern, ids, leerem Namen und ohne Jahr.
        var gemischt = Rivals.CarCollection.Lesen("{\"format\":\"fhc-cars-1\",\"neu\":1,\"cars\":["
            + string.Join(",", Enumerable.Range(1, 110).Select(i =>
                i == 1 ? "{\"name\":\"\",\"ways\":[]}"
                : i == 2 ? "{\"name\":\"Ohne Jahr\",\"ids\":[5,6],\"zukunft\":{\"a\":1},\"ways\":[{\"k\":\"x\",\"y\":[1]}]}"
                : "{\"name\":\"A" + i + "\",\"year\":2020}")) + "]}");
        Soll(gemischt is { Autos.Count: 109 }, "leerer Name wird nicht uebersprungen oder fremde Felder brechen das Lesen");
        var ohneJahr = gemischt!.Autos[0];
        Soll(ohneJahr.Schluessel == "Ohne Jahr|" && ohneJahr.Anzeige == "Ohne Jahr" && ohneJahr.AlleIds.SequenceEqual(new[] { 5, 6 }),
             "Auto ohne Jahr oder mit ids-Liste falsch gelesen");
        Soll(gemischt.GebautAm is null, "ohne 'built' gibt es trotzdem einen Baustand");

        // Stromtitel: mehr echte und falsche Faelle.
        foreach (var ja in new[] { "Twitch", "twitch - Opera", "Live - TikTok", "Rumble", "Stream | Trovo", "(3) Facebook" })
        {
            Soll(Rivals.Fenster.IstStromTitel(ja), "kein Strom erkannt: " + ja);
        }
        foreach (var nein in new[] { "", "Twitcher", "YouTubers Club", "Kickoff meeting", "Facebooking", "Forza Horizon 6 - Discord" })
        {
            Soll(!Rivals.Fenster.IstStromTitel(nein), "Strom erkannt, wo keiner ist: " + nein);
        }
        Soll(Rivals.Fenster.StromPlattform("x - YouTube - Google Chrome") == "YouTube", "Plattform nicht genannt");

        // HUD ueber dem Fenster: jede Quelle, an und aus, Konsole und PC.
        foreach (var (quelle, erwartet) in new[] { ("window", true), ("obs", true), ("OBS", true), ("discord", false),
                                                   ("browser", false), ("device", false), ("url", false), ("none", false),
                                                   ("", false), (null, false) })
        {
            var e = new Rivals.OverlaySettings { ConsoleMode = true, ConsoleHudOverWindow = true, VideoSource = quelle };
            Soll(e.HudUeberFenster == erwartet, $"HUD ueber dem Fenster bei '{quelle}': {e.HudUeberFenster}");
            e.ConsoleHudOverWindow = false;
            Soll(!e.HudUeberFenster, $"HUD ueber dem Fenster ohne Wahl bei '{quelle}'");
            e.ConsoleHudOverWindow = true;
            e.ConsoleMode = false;
            Soll(!e.HudUeberFenster, $"HUD ueber dem Fenster im PC-Modus bei '{quelle}'");
        }

        // MY CARS NACH NAMEN: die Kacheln aus dem Screenshot des Nutzers (2026-09-29), gegen die echte Liste.
        if (Rivals.CarCollection.PaketPfad() is { } listePfad && Rivals.CarCollection.Lesen(File.ReadAllText(listePfad)) is { } echteListe)
        {
            static List<Rivals.OcrLine> Kachel(params string[] zeilen) =>
                zeilen.Select((t, i) => new Rivals.OcrLine(t, 10, 10 + i * 40)).ToList();
            foreach (var (zeilen, erwartet) in new (string[], string?)[]
                     {
                         (new[] { "UTODELTA TIPO 33/2 DAY'", "1968 ALFA ROMEO" }, "Alfa Romeo Autodelta Tipo 33/2 Daytona|1968"),
                         (new[] { "GIULIA TZ2", "1965 ALFA ROMEO" }, "Alfa Romeo Giulia TZ2|1965"),
                         (new[] { "33 STRADALE", "1968 ALFA ROMEO" }, "Alfa Romeo 33 Stradale|1968"),
                         (new[] { "#6165 TRICK TRUCK", "2022 ALUMICRAFT" }, "Alumicraft #6165 Trick Truck|2022"),
                         (new[] { "GIULIA SPRINT GTA STR...", "1965 ALFA ROMEO" }, "Alfa Romeo Giulia Sprint GTA Stradale|1965"),
                         (new[] { "ASTRA VXR", "2006 VAUXHALL" }, "Vauxhall Astra VXR|2006"),
                         (new[] { "M12S WA", "2554 AMG TR..." }, null),
                         (new[] { "ASTRA VXR", "2007 VAUXHALL" }, null),
                         (new[] { "ASTRA VXR" }, null),
                         (new[] { "1968 ALFA ROMEO" }, null),
                         (new[] { "XY", "1968 ALFA ROMEO" }, null),
                     })
            {
                var treffer = Rivals.CarGridReader.ImListe(Kachel(zeilen), echteListe.Autos);
                Soll(treffer?.Schluessel == erwartet,
                     $"My Cars '{string.Join(" / ", zeilen)}': {treffer?.Schluessel ?? "nichts"} statt {erwartet ?? "nichts"}");
            }
            Soll(Rivals.CarGridReader.IstMeineAutos(Kachel("My Cars")) && Rivals.CarGridReader.IstMeineAutos(Kachel("MY CARS", "ACURA"))
                 && !Rivals.CarGridReader.IstMeineAutos(Kachel("Autoshow")) && !Rivals.CarGridReader.IstMeineAutos(Kachel()),
                 "der Titel 'My Cars' wird falsch erkannt");
        }

        // Gesehen in My Cars: zaehlt als besessen, auch ohne car_id; ein Haken schlaegt es.
        {
            var ordner = Path.Combine(Path.GetTempPath(), "fhc-edge-owned-" + Guid.NewGuid().ToString("N"));
            try
            {
                var pfad = Path.Combine(ordner, "owned_cars.json");
                var l = Rivals.CarCollection.Lesen("{\"format\":\"fhc-cars-1\",\"cars\":[" + string.Join(",",
                    Enumerable.Range(1, 110).Select(i => i == 1 ? "{\"name\":\"Ohne Id\",\"year\":2006}"
                                                        : "{\"name\":\"A" + i + "\",\"year\":2020,\"id\":" + i + "}")) + "]}")!;
                var ohneId = l.Autos[0];
                var leer = new HashSet<int>();
                var o = Rivals.OwnedCars.Laden(pfad);
                o.GarageMerken(new[] { 2, 3, 9999 });
                o.Speichern();
                o = Rivals.OwnedCars.Laden(pfad);
                var fremd = o.NichtErkannt(l);
                Soll(fremd == 1 && o.Unsicher(ohneId, leer, fremd) && !o.Unsicher(l.Autos[5], leer, fremd),
                     "ein Auto ohne id gilt bei nicht erkannten Garagenautos nicht als 'nicht sicher'");
                Soll(!o.Unsicher(ohneId, leer, 0), "ohne nicht erkannte Garagenautos ist ein Auto ohne id 'nicht sicher'");
                Soll(Rivals.OwnedCars.GesehenMerken(ohneId, pfad) && !Rivals.OwnedCars.GesehenMerken(ohneId, pfad),
                     "My Cars merkt nicht oder doppelt");
                o = Rivals.OwnedCars.Laden(pfad);
                Soll(o.Besitz(ohneId, leer) == (true, Rivals.OwnedCars.Grund.MeineAutos) && !o.Unsicher(ohneId, leer, fremd),
                     "ein in My Cars gesehenes Auto gilt nicht als besessen");
                o.Setze(ohneId, true, leer);
                Soll(!o.Markiert.ContainsKey(ohneId.Schluessel), "ein Haken, der My Cars nur bestaetigt, wird gemerkt");
                o.Setze(ohneId, false, leer);
                Soll(o.Besitz(ohneId, leer) == (false, Rivals.OwnedCars.Grund.VonHand), "ein entfernter Haken schlaegt My Cars nicht");
            }
            finally
            {
                try { Directory.Delete(ordner, true); } catch (Exception) { }
            }
        }

        // Die mitgelieferte Liste: jedes Auto hat mindestens einen Weg.
        if (Rivals.CarCollection.PaketPfad() is { } echt)
        {
            var e = Rivals.CarCollection.Lesen(File.ReadAllText(echt));
            Soll(e is { Autos.Count: >= 600 }, "die mitgelieferte Autoliste ist unvollstaendig");
            Soll(e!.Autos.All(a => a.Wege.Count > 0), "ein Auto der Liste hat keinen Weg");
        }
    }

    /// <summary>Firewall, Speicherschalter, Controller mit Remote Play, gemerkte Tunes, ganze My-Cars-Seite.</summary>
    private static void XboxAndMemoryOptions()
    {
        // --- Firewall: die Regel ohne Windows
        const string App = @"C:\Games\FHC\FH Companion.exe";
        static Firewall.Regel R(int aktion, string? programm = null, string? ports = "5300", int profile = 2, int proto = 17,
                                bool an = true, int richtung = 1) => new(an, richtung, profile, proto, aktion, programm, ports);
        Soll(Firewall.Bewerten(new[] { R(1) }, 2, 5300, App) == Firewall.Stand.Offen, "eine Port-Regel oeffnet nicht");
        Soll(Firewall.Bewerten(new[] { R(1, App, "*") }, 2, 5300, App) == Firewall.Stand.Offen, "eine Programm-Regel oeffnet nicht");
        Soll(Firewall.Bewerten(new[] { R(1), R(0, App, "*") }, 2, 5300, App) == Firewall.Stand.Blockiert,
             "eine Sperre fuer das Programm gewinnt nicht gegen die Port-Regel");
        Soll(Firewall.Bewerten(new[] { R(1, @"C:\other.exe", "*") }, 2, 5300, App) == Firewall.Stand.Fehlt,
             "die Regel eines fremden Programms oeffnet den Port");
        Soll(Firewall.Bewerten(new[] { R(1, profile: 4) }, 2, 5300, App) == Firewall.Stand.Fehlt, "eine Regel fuer ein anderes Profil zaehlt");
        Soll(Firewall.Bewerten(new[] { R(1, an: false) }, 2, 5300, App) == Firewall.Stand.Fehlt, "eine abgeschaltete Regel zaehlt");
        Soll(Firewall.Bewerten(new[] { R(1, proto: 6) }, 2, 5300, App) == Firewall.Stand.Fehlt, "eine TCP-Regel oeffnet UDP");
        Soll(Firewall.Bewerten(new[] { R(1, richtung: 2) }, 2, 5300, App) == Firewall.Stand.Fehlt, "eine ausgehende Regel zaehlt");
        Soll(Firewall.Bewerten(new[] { R(1, ports: "5000-5400") }, 6, 5300, App) == Firewall.Stand.Offen, "ein Portbereich passt nicht");
        Soll(Firewall.Bewerten(new[] { R(1, ports: "80,443") }, 2, 5300, App) == Firewall.Stand.Fehlt, "fremde Ports oeffnen");
        Soll(Firewall.Bewerten(Array.Empty<Firewall.Regel>(), 2, 5300, App) == Firewall.Stand.Fehlt, "ohne Regeln ist offen");
        Soll(Firewall.PortPasst(null, 1) && Firewall.PortPasst("*", 1) && Firewall.PortPasst("1, 5300 ,9", 5300) && !Firewall.PortPasst("x", 5300),
             "Portangaben falsch gelesen");
        var befehl = Firewall.Befehl(5301, oeffentlich: true, sperreWeg: true, App);
        Soll(befehl.Contains("localport=5301") && befehl.Contains("profile=private,domain,public") && befehl.Contains("protocol=UDP")
             && befehl.Contains($"program=\"{App}\"") && befehl.Contains("dir=in action=allow"),
             "der Firewall-Befehl ist falsch: " + befehl);
        Soll(!Firewall.Befehl(5300, false, false, App).Contains("program=") && Firewall.Befehl(5300, false, false, App).EndsWith("profile=private,domain"),
             "ohne Sperre wird trotzdem eine Programm-Regel geloescht oder das oeffentliche Netz geoeffnet");

        // --- Schalter
        var e = new Rivals.OverlaySettings();
        Soll(e.ControllerHier && e.SpeicherLesen, "Vorgaben: Controller hier und Speicher lesen");
        e.ConsoleMode = true;
        Soll(!e.ControllerHier && !e.SpeicherLesen, "Konsole: Controller oder Speicher gelten als hier");
        e.ConsoleControllerHere = true;
        Soll(e.ControllerHier && !e.SpeicherLesen, "Remote Play: Controller nicht hier, oder Speicher gelesen");
        e.ConsoleMode = false;
        e.ReadGameMemory = false;
        Soll(!e.SpeicherLesen, "abgeschalteter Speicher wird trotzdem gelesen");

        // --- Speicher-Tor: jeder Weg wirft, wenn es aus ist
        var vorher = Tuning.ForzaMemoryDb.Erlaubt;
        try
        {
            Tuning.ForzaMemoryDb.Erlaubt = false;
            var geworfen = false;
            try { Tuning.ForzaMemoryDb.Dump(Path.GetTempPath()); } catch (InvalidOperationException) { geworfen = true; }
            Soll(geworfen, "trotz abgeschaltetem Speicher wird gelesen (Dump)");
            geworfen = false;
            try { Tuning.ForzaMemoryDb.DumpFrom(Environment.ProcessId, Path.GetTempPath()); } catch (InvalidOperationException) { geworfen = true; }
            Soll(geworfen, "trotz abgeschaltetem Speicher wird gelesen (DumpFrom)");
            var (ids, fehler) = Rivals.GarageImport.Lesen(_ => { });
            Soll(ids.Count == 0 && fehler is { Length: > 0 }, "die Garage meldet den abgeschalteten Speicher nicht");
        }
        finally
        {
            Tuning.ForzaMemoryDb.Erlaubt = vorher;
        }

        // --- Gemerkte Tunes
        var altPfad = Rivals.SeenTunes.Pfad;
        var tunePfad = Path.Combine(Path.GetTempPath(), "fhc-seen-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Rivals.SeenTunes.Pfad = tunePfad;
            Rivals.SeenTunes.Vergessen();
            Soll(Rivals.SeenTunes.Fuer(42) is null, "ein ungesehenes Tune ist bekannt");
            Soll(Rivals.SeenTunes.Merken(42, "X '20", " A700 Road AWD ", "x ShadowsBane x", "14/05/2026"), "ein Tune wird nicht gemerkt");
            Soll(!Rivals.SeenTunes.Merken(42, "X '20", "A700 Road AWD", "x ShadowsBane x", "14/05/2026"), "dasselbe Tune gilt als neu");
            Soll(!Rivals.SeenTunes.Merken(42, "X '20", "  ", "y", "z") && !Rivals.SeenTunes.Merken(0, "X", "N", "y", "z"),
                 "ein leeres Tune oder Auto 0 wird gemerkt");
            Rivals.SeenTunes.Vergessen();
            Soll(Rivals.SeenTunes.Fuer(42)?.Name == "A700 Road AWD", "ein gemerktes Tune ueberlebt das Neuladen nicht");
        }
        finally
        {
            Rivals.SeenTunes.Pfad = altPfad;
            Rivals.SeenTunes.Vergessen();
            try { File.Delete(tunePfad); } catch (Exception) { }
        }

        // --- Die ganze My-Cars-Seite: Zeilen wie aus der Texterkennung (Mitte an Mitte).
        if (Rivals.CarCollection.PaketPfad() is { } pfad && Rivals.CarCollection.Lesen(File.ReadAllText(pfad)) is { } liste)
        {
            static Rivals.OcrLine L(string t, double x, double y, double w) => new(t, x, y) { W = w, H = 22 };
            var seite = new List<Rivals.OcrLine>
            {
                L("My Cars", 140, 110, 120),
                L("S-CARGO FORZA EDITION", 420, 220, 300), L("1989 NISSAN", 505, 250, 130),
                L("124 SPIDER", 820, 220, 140), L("2017 ABARTH", 820, 250, 140),
                L("595 ESSEESSE", 1150, 220, 170), L("1968 ABARTH", 1165, 250, 140),
                L("INTEGRA A-SPEC", 1500, 220, 200), L("2023 ACURA", 1530, 250, 140),
                L("INTEGR", 1800, 220, 90), L("2001 A", 1805, 250, 80),
                L("695 BIPOSTO", 830, 470, 150), L("2016 ABARTH", 835, 500, 140),
                L("NSX TYPE S", 1520, 470, 150), L("2022 ACURA", 1525, 500, 140),
                L("SPEED", 130, 500, 60),
            };
            var gefunden = Rivals.CarGridReader.AlleImBild(seite, liste.Autos).Select(t => t.Auto.Schluessel).ToList();
            var erwartet = new[] { "Nissan S-Cargo Forza Edition|1989", "Abarth 124 Spider|2017", "Abarth 595 esseesse|1968",
                                   "Acura Integra A-Spec|2023", "Abarth 695 Biposto|2016", "Acura NSX Type S|2022" };
            Soll(erwartet.All(gefunden.Contains) && gefunden.Count == erwartet.Length,
                 "My-Cars-Seite: " + string.Join(", ", gefunden));
            Soll(Rivals.CarGridReader.IstMeineAutos(seite), "der Seitentitel 'My Cars' wird nicht erkannt");
        }
    }

    private static void PictureSourcesFindTheGame()
    {
        // Ein 1600x1000-Fenster, darin ein 1440x810-Spielbild mit schwarzen Balken.
        using (var bild = new Bitmap(1600, 1000, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bild))
            {
                g.Clear(Color.Black);
                using var pinsel = new System.Drawing.Drawing2D.LinearGradientBrush(
                    new Rectangle(80, 95, 1440, 810), Color.DarkGreen, Color.Orange, 30f);
                g.FillRectangle(pinsel, 80, 95, 1440, 810);
                g.FillEllipse(Brushes.White, 700, 400, 200, 120);
            }
            var r = Rivals.Bildquellen.Spielbild(bild, new Rectangle(0, 0, 1600, 1000));
            Soll(Math.Abs(r.X - 80) <= 2 && Math.Abs(r.Y - 95) <= 2 && Math.Abs(r.Width - 1440) <= 4
                 && Math.Abs(r.Height - 810) <= 4,
                 $"das Spielbild in den Balken wurde nicht gefunden: {r}");
        }
        // Ein Spielbild OHNE Balken mit einem einfarbigen Himmel oben: nichts abschneiden.
        using (var bild = new Bitmap(1920, 1080, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bild))
            {
                g.Clear(Color.FromArgb(90, 150, 220));
                g.FillRectangle(Brushes.DimGray, 0, 500, 1920, 580);
                g.FillRectangle(Brushes.Gold, 900, 700, 200, 120);
            }
            var r = Rivals.Bildquellen.Spielbild(bild, new Rectangle(0, 0, 1920, 1080));
            Soll(r == new Rectangle(0, 0, 1920, 1080), $"ein blauer Himmel wurde als Balken abgeschnitten: {r}");
        }
        // Eine Flaeche, die nicht 16:9 ist und keine Balken hat: mittig auf 16:9.
        using (var bild = new Bitmap(1000, 1000, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bild))
            {
                using var pinsel = new System.Drawing.Drawing2D.LinearGradientBrush(
                    new Rectangle(0, 0, 1000, 1000), Color.Red, Color.Blue, 45f);
                g.FillRectangle(pinsel, 0, 0, 1000, 1000);
            }
            var r = Rivals.Bildquellen.Spielbild(bild, new Rectangle(0, 0, 1000, 1000));
            Soll(r.Width == 1000 && Math.Abs(r.Height - 563) <= 1 && Math.Abs(r.Y - 218) <= 1,
                 $"eine quadratische Flaeche wurde nicht mittig auf 16:9 gebracht: {r}");
        }

        // DER HUD UEBER DEM FENSTER (seit 2026-09-29): nur mit einem Fenster als Quelle
        // (Remote Play, OBS-Projektor) -- eine Aufnahmekarte hat kein Fenster auf dem Schirm.
        var ueber = new Rivals.OverlaySettings { ConsoleMode = true, VideoSource = "window", VideoWindow = "Xbox",
                                                 ConsoleHudOverWindow = true };
        Soll(ueber.HudUeberFenster, "Remote Play mit 'ueber dem Fenster' legt den HUD nicht darueber");
        ueber.VideoSource = "obs";
        Soll(ueber.HudUeberFenster, "der OBS-Projektor mit 'ueber dem Fenster' legt den HUD nicht darueber");
        // Discord und ein Strom im Browser hinken hinterher: dort spielt niemand, der HUD bleibt im Dashboard.
        ueber.VideoSource = "discord";
        Soll(!ueber.HudUeberFenster && ueber.HasVideoSource, "Discord: der HUD laege ueber einem verspaeteten Bild");
        ueber.VideoSource = "browser";
        Soll(!ueber.HudUeberFenster && ueber.HasVideoSource, "Browser-Strom: der HUD laege ueber einem verspaeteten Bild");
        Soll(Rivals.Fenster.IstStromTitel("somebody - Twitch - Google Chrome")
             && Rivals.Fenster.IstStromTitel("FH6 live - YouTube \u2014 Mozilla Firefox")
             && Rivals.Fenster.IstStromTitel("somebody | Kick - Profile 1 - Microsoft Edge"),
             "ein Strom-Reiter wird nicht als Strom erkannt");
        Soll(!Rivals.Fenster.IstStromTitel("Kickstarter - Google Chrome") && !Rivals.Fenster.IstStromTitel("Forza Horizon 6"),
             "ein Fenster ohne Strom gilt als Strom (nur ganze Woerter zaehlen)");
        ueber.VideoSource = "device";
        Soll(!ueber.HudUeberFenster, "mit einer Aufnahmekarte gilt 'ueber dem Fenster' -- es gibt keines");
        ueber.VideoSource = "window";
        ueber.ConsoleMode = false;
        Soll(!ueber.HudUeberFenster, "im PC-Modus gilt 'ueber dem Fenster'");

        // Die Einstellungen: OBS braucht nichts weiter, die anderen ihren Namen.
        var s = new Rivals.OverlaySettings { ConsoleMode = true, VideoSource = "obs" };
        Soll(s.HasVideoSource, "OBS gilt nicht als Quelle");
        s.VideoSource = "window";
        Soll(!s.HasVideoSource, "ein Fenster ohne Titel gilt als Quelle");
        s.ConsoleMode = false;
        Rivals.Bildquellen.Anwenden(s);
        Soll(Rivals.Bildquellen.Aktiv is null && GameArea.FensterTitel is null,
             "im PC-Modus blieb eine Videoquelle aktiv");
        Soll(Rivals.Ffmpeg.Finden(Application.ExecutablePath) == Application.ExecutablePath,
             "ein eingestellter ffmpeg-Pfad wird nicht genommen");

        // Graphics Capture selbst wird hier NICHT live geprueft: Windows zeichnet Fenster
        // ausserhalb aller Schirme nicht (gemessen am 2026-09-28: schwarzes Bild, bei -32000
        // wie rechts daneben), und ein sichtbares Testfenster ist tabu. Von Hand:
        // "FH Companion.exe --video-sources window <Titel> bild.png".
        using var fehlt = new Rivals.FensterQuelle("fhc-edge-gibt-es-nicht-7d1c");
        for (var i = 0; i < 30 && !fehlt.Beschreibung.Contains("not found"); i++) { Thread.Sleep(50); }
        Soll(fehlt.Beschreibung.Contains("not found") && fehlt.Neuestes() is null,
             "ein fehlendes Fenster wird nicht als fehlend gemeldet: " + fehlt.Beschreibung);
    }

    private static Rivals.TelemetryTrack TestSpur()
    {
        var spur = new Rivals.TelemetryTrack();
        var paket = new byte[324];
        BitConverter.GetBytes(1).CopyTo(paket, 0);
        for (var i = 0; i < 40; i++)
        {
            BitConverter.GetBytes(1000u + (uint)i * 16).CopyTo(paket, 4);
            Soll(ForzaPacket.TryParse(paket, out var p), "Testpaket parst nicht");
            spur.Add(p, i * 0.016f, i * 0.5f);
        }
        return spur;
    }

    private static void FullTelemetryTravelsWithTheLap()
    {
        var spur = TestSpur();
        var lap = new Rivals.RecordedLap { LapSeconds = 0.64f, FullTrack = spur };
        var gepackt = Rivals.LapSubmit.VolleSpur(lap);
        Soll(gepackt is { Length: > 0 }, "keine gepackte Spur aus dem Speicher");

        // Genau die Bytes, die das Archiv schreibt.
        var ordner = Path.Combine(Path.GetTempPath(), "fhc-edge-tele-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(ordner);
        var alteHeimat = Environment.GetEnvironmentVariable("FORZA_SUBMIT_HOME");
        try
        {
            var datei = spur.Save(Path.Combine(ordner, "runde.json"));
            Soll(datei is not null && File.ReadAllBytes(datei).AsSpan().SequenceEqual(gepackt),
                 "gepackt im Speicher ist nicht dasselbe wie die .tele.gz");

            // Der Rumpf: Telemetrie als Base64, dieselben Bytes; kein Pfad darin.
            lap.TelemetrieDatei = datei;
            var rumpf = Rivals.LapSubmit.Rumpf(lap, "course_1_2_to_3_4", "Tester", gepackt);
            var knoten = System.Text.Json.Nodes.JsonNode.Parse(rumpf)!;
            var b64 = knoten["telemetry"]?["data"]?.GetValue<string>();
            Soll(b64 is not null && Convert.FromBase64String(b64).AsSpan().SequenceEqual(gepackt),
                 "die Telemetrie im Rumpf ist nicht die gepackte Spur");
            var text = System.Text.Encoding.UTF8.GetString(rumpf);
            Soll(!text.Contains(ordner.Replace("\\", "\\\\")) && !text.Contains("fhc-edge-tele"),
                 "ein lokaler Pfad steht im Rumpf");

            // Eine wartende Runde: die Spur liegt als Nebendatei und kommt wieder.
            Environment.SetEnvironmentVariable("FORZA_SUBMIT_HOME", ordner);
            var wartend = new Rivals.RecordedLap { LapSeconds = 0.64f, FullTrack = spur };
            Soll(Rivals.LapQueue.Vormerken(wartend, "c", "Testkurs", "schluessel-1", "Probe"),
                 "Vormerken schlug fehl");
            var zurueck = Rivals.LapQueue.Alle().Single(e => e.Key == "schluessel-1");
            Soll(zurueck.Lap.FullTrack is null && zurueck.Lap.TelemetrieDatei is not null,
                 "die wartende Runde kennt ihre Nebendatei nicht");
            Soll(Rivals.LapSubmit.VolleSpur(zurueck.Lap)?.AsSpan().SequenceEqual(gepackt) == true,
                 "die Nebendatei traegt nicht die Spur der Runde");
            // Eine schnellere Runde OHNE Spur darf die alte Spur nicht erben.
            var ohne = new Rivals.RecordedLap { LapSeconds = 0.5f };
            Soll(Rivals.LapQueue.Vormerken(ohne, "c", "Testkurs", "schluessel-1", "Probe"),
                 "die schnellere Runde wurde nicht vorgemerkt");
            var neu = Rivals.LapQueue.Alle().Single(e => e.Key == "schluessel-1");
            Soll(Rivals.LapSubmit.VolleSpur(neu.Lap) is null,
                 "die schnellere Runde erbte die Spur der langsameren");
            Rivals.LapQueue.Entfernen("schluessel-1");
            Soll(Directory.GetFiles(Rivals.LapQueue.Folder).Length == 0,
                 "Entfernen liess eine Datei zurueck");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FORZA_SUBMIT_HOME", alteHeimat);
            try { Directory.Delete(ordner, recursive: true); } catch (Exception) { }
        }
    }

    private static void ConsoleModeReadsItsSource()
    {
        var s = new Rivals.OverlaySettings();
        Soll(!s.ConsoleMode && !s.HasVideoSource, "der Konsolenmodus ist ab Werk an");
        s.ConsoleMode = true;
        Soll(!s.HasVideoSource, "ohne Angaben gilt eine Videoquelle");
        s.VideoSource = "window";
        Soll(!s.HasVideoSource, "Quelle Fenster ohne Titel gilt");
        s.VideoWindow = "Projector";
        Soll(s.HasVideoSource, "Quelle Fenster mit Titel gilt nicht");
        s.VideoSource = "url";
        Soll(!s.HasVideoSource, "Quelle Adresse ohne Adresse gilt");

        // SpielbildDa: Konsole ohne Quelle -> nichts zu lesen.
        var typ = typeof(Rivals.OverlayController);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var roh = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typ);
        var ohne = new Rivals.OverlaySettings { ConsoleMode = true };
        typ.GetField("_settings", flags)!.SetValue(roh, ohne);
        var da = typ.GetMethod("SpielbildDa", flags)!;
        Soll(!(bool)da.Invoke(roh, null)!, "Konsolenmodus ohne Videoquelle will den Schirm lesen");
        typ.GetField("_settings", flags)!.SetValue(roh, new Rivals.OverlaySettings { ConsoleMode = true, VideoSource = "window", VideoWindow = "x" });
        Soll((bool)da.Invoke(roh, null)!, "Konsolenmodus mit Videoquelle liest nicht");

        // GameArea liest aus der Quelle: linke Haelfte rot, rechte blau.
        using var quelle = new FesteQuelle();
        using (var g = Graphics.FromImage(quelle.Bild))
        {
            g.Clear(Color.Blue);
            g.FillRectangle(Brushes.Red, 0, 0, 960, 1080);
        }
        var vorher = Rivals.Bildquellen.Aktiv;
        try
        {
            Rivals.Bildquellen.Aktiv = quelle;
            var flaeche = GameArea.Find("forzahorizon6");
            Soll(flaeche == new Rectangle(0, 0, 1920, 1080), $"die Flaeche ist nicht das Bild der Quelle ({flaeche})");
            using var links = GameArea.Capture(new Rectangle(100, 100, 400, 300), new Size(200, 150));
            using var rechts = GameArea.Capture(new Rectangle(1200, 600, 400, 300), new Size(200, 150));
            var l = links.GetPixel(100, 75);
            var r = rechts.GetPixel(100, 75);
            Soll(l.R > 200 && l.B < 50 && r.B > 200 && r.R < 50 && links.Size == new Size(200, 150),
                 $"der Ausschnitt aus der Quelle stimmt nicht (links {l}, rechts {r})");
        }
        finally
        {
            // Nicht Dispose ueber den Setter: die Testquelle raeumt using auf.
            typeof(Rivals.Bildquellen).GetField("_aktiv", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(null, vorher);
            GameArea.Invalidate();
        }

        Soll(Konsole.Adressen().All(a => !a.StartsWith("127.")), "unter den Netzadressen steht die Loopback-Adresse");
        using var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), ShowInTaskbar = false };
        var feld = Konsole.Feld(new Rivals.OverlaySettings { ConsoleMode = true, VideoSource = "device" }, Point.Empty);
        form.Controls.Add(feld);
        Soll(feld.Controls.Count >= 8, "das Bedienfeld des Konsolenmodus ist unvollstaendig");
    }

    /// <summary>
    /// Keine Feier waehrend der Fahrt (2026-09-28, "no performance problems and hitching is
    /// prio #1"): sie wartet, der Website-Rekord geht vor, und im Stand kommt sie.
    /// </summary>
    private static void CelebrationsWaitWhileDriving()
    {
        var typ = typeof(Rivals.OverlayController);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var roh = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typ);
        typ.GetField("_settings", flags)!.SetValue(roh, new Rivals.OverlaySettings());
        var zeige = typ.GetMethod("ZeigeFeier", flags)!;
        var warten = typ.GetField("_wartendeFeier", flags)!;
        Rivals.CelebrationHud.Anlass A(Rivals.CelebrationHud.FeierArt art) => new("t", "1:00.000", 0.1, "v", "d", art);
        (Rivals.CelebrationHud.Anlass Anlass, DateTime Seit)? Wartet() =>
            ((Rivals.CelebrationHud.Anlass, DateTime)?)warten.GetValue(roh);

        // FAHRT: frische Telemetrie, 30 m/s.
        typ.GetField("_telemetryAt", flags)!.SetValue(roh, DateTime.UtcNow);
        typ.GetField("_tempo", flags)!.SetValue(roh, 30f);
        zeige.Invoke(roh, new object[] { A(Rivals.CelebrationHud.FeierArt.Persoenlich), false });
        Soll(Wartet()?.Anlass.Art == Rivals.CelebrationHud.FeierArt.Persoenlich, "eine eigene Feier wartet waehrend der Fahrt nicht");
        zeige.Invoke(roh, new object[] { A(Rivals.CelebrationHud.FeierArt.Rekord), false });
        Soll(Wartet()?.Anlass.Art == Rivals.CelebrationHud.FeierArt.Rekord, "der Website-Rekord verdraengt die eigene Feier nicht");
        zeige.Invoke(roh, new object[] { A(Rivals.CelebrationHud.FeierArt.Persoenlich), false });
        Soll(Wartet()?.Anlass.Art == Rivals.CelebrationHud.FeierArt.Rekord, "eine eigene Feier verdraengt den wartenden Website-Rekord");

        // STAND: keine frische Telemetrie mehr -> die wartende Feier kommt dran (und ist weg).
        typ.GetField("_telemetryAt", flags)!.SetValue(roh, DateTime.UtcNow.AddMinutes(-1));
        typ.GetMethod("WartendeFeierZeigen", flags)!.Invoke(roh, null);
        Soll(Wartet() is null, "im Stand wird die wartende Feier nicht gezeigt");
    }

    /// <summary>
    /// Die eigene Bestenliste (2026-09-28): welcher Anlass, gegen welche Runden, und die
    /// kleineren Feiern darauf.
    /// </summary>
    private static void PersonalRecordsDecide()
    {
        Rivals.OwnTimes.Lap L(int car, double sek, string klasse = "A", bool stehend = false, string modus = "rivals",
                              string kurs = "c1", bool sprint = false) =>
            new(kurs, "Test Circuit", klasse, car, "t", 700, sek, stehend, sprint, DateTime.Now, modus,
                $"x/{kurs}/{klasse}/{car}/{sek}/{modus}/{stehend}/{Guid.NewGuid():N}.json");
        var bisher = new List<Rivals.OwnTimes.Lap> { L(1, 60.0), L(1, 61.0), L(2, 59.5) };
        Rivals.PersonalRecords.Ergebnis W(Rivals.OwnTimes.Lap neu, bool jeModus = true, List<Rivals.OwnTimes.Lap>? b = null) =>
            Rivals.PersonalRecords.Werte(b ?? bisher, neu, jeModus);

        var k = W(L(1, 59.0));
        Soll(k.Art == Rivals.PersonalRecords.Art.KlassenRekord && k.VorherSekunden == 59.5 && k.Platz == 1 && k.Autos == 2,
             $"Klassenrekord: {k}");
        var a = W(L(1, 59.8));
        Soll(a.Art == Rivals.PersonalRecords.Art.AutoRekord && a.VorherSekunden == 60.0 && a.Platz == 2 && a.Autos == 2,
             $"Autorekord: {a}");
        var n = W(L(3, 65.0));
        Soll(n.Art == Rivals.PersonalRecords.Art.NeuesAuto && n.Platz == 3 && n.Autos == 3, $"neues Auto: {n}");
        Soll(W(L(3, 59.0)).Art == Rivals.PersonalRecords.Art.KlassenRekord, "ein neues Auto mit Klassenbestzeit ist kein Klassenrekord");
        Soll(W(L(1, 60.5)).Art == Rivals.PersonalRecords.Art.Keine, "eine langsamere Runde feiert");
        Soll(W(L(1, 50.0, klasse: "S1")).Art == Rivals.PersonalRecords.Art.ErsteInKlasse, "andere Klasse wird mitverglichen");
        Soll(W(L(1, 50.0, stehend: true)).Art == Rivals.PersonalRecords.Art.ErsteInKlasse,
             "stehender Start wird gegen fliegende Runden verglichen");
        Soll(W(L(1, 50.0, sprint: true)).Art == Rivals.PersonalRecords.Art.ErsteInKlasse,
             "Sprint wird gegen Rundenzeiten verglichen");
        // JE MODUS: ein Solo-Rennen hat eigene Rekorde ...
        Soll(W(L(1, 58.0, modus: "race")).Art == Rivals.PersonalRecords.Art.ErsteInKlasse,
             "je Modus: eine Rennrunde wird gegen Rivals-Runden gewertet");
        Soll(W(L(1, 58.0, modus: "race"), jeModus: false).Art == Rivals.PersonalRecords.Art.KlassenRekord,
             "ohne Trennung nach Modus zaehlt die Rennrunde nicht gegen Rivals");
        // ... und Runden ohne Modus (vor dem Update) zaehlen fuer jeden Modus.
        var mitAlt = new List<Rivals.OwnTimes.Lap>(bisher) { L(4, 58.0, modus: "unknown") };
        Soll(W(L(1, 58.5), b: mitAlt).Art != Rivals.PersonalRecords.Art.KlassenRekord,
             "eine alte Runde ohne Modus zaehlt nicht mit -- nach dem Update waere alles ein Rekord");
        // Dieselbe Datei zaehlt nie gegen sich selbst.
        var selbst = L(5, 57.0);
        Soll(W(selbst, b: new List<Rivals.OwnTimes.Lap>(bisher) { selbst }).Art == Rivals.PersonalRecords.Art.KlassenRekord,
             "die Runde wird gegen sich selbst verglichen");

        var s = new Rivals.OverlaySettings();
        Soll(Rivals.PersonalRecords.Gewollt(Rivals.PersonalRecords.Art.KlassenRekord, s)
             && Rivals.PersonalRecords.Gewollt(Rivals.PersonalRecords.Art.AutoRekord, s)
             && Rivals.PersonalRecords.Gewollt(Rivals.PersonalRecords.Art.NeuesAuto, s)
             && !Rivals.PersonalRecords.Gewollt(Rivals.PersonalRecords.Art.ErsteInKlasse, s)
             && !Rivals.PersonalRecords.Gewollt(Rivals.PersonalRecords.Art.Keine, s),
             "Vorgaben der eigenen Rekorde stimmen nicht (drei an, erste Runde aus)");
        s.PbCarRecord = false;
        Soll(!Rivals.PersonalRecords.Gewollt(Rivals.PersonalRecords.Art.AutoRekord, s), "Autorekord laesst sich nicht abschalten");

        // DIE FEIERN DAZU: kleiner, kuerzer, eigener Ton.
        var ton = Rivals.CelebrationSound.ProbenPersoenlich();
        Soll(ton.All(float.IsFinite) && ton.Max(Math.Abs) < Rivals.CelebrationSound.Spitze * 0.65f
             && ton[^((int)(0.05 * Rivals.CelebrationSound.Rate))..].Max(Math.Abs) < 0.01,
             "der Ton fuer eigene Rekorde ist nicht leiser als die Website-Feier oder endet nicht leise");
        var flaeche = new Size(1920, 1080);
        foreach (var art in new[] { Rivals.CelebrationHud.FeierArt.Persoenlich, Rivals.CelebrationHud.FeierArt.Repertoire })
        {
            var anlass = new Rivals.CelebrationHud.Anlass("Personal best in class A!", "1:02.345", 0.412,
                                                          "Your previous best here: 1:02.757", "Test Circuit · Car · A", art);
            var konfetti = new Rivals.CelebrationHud.Konfetti(3, art);
            var gross = new Rivals.CelebrationHud.Konfetti(3, Rivals.CelebrationHud.FeierArt.Rekord);
            Soll(konfetti.Teile.Length < gross.Teile.Length / 3, $"{art}: nicht weniger Konfetti als die Website-Feier");
            int Gezeichnet(double t)
            {
                using var bild = new Bitmap(flaeche.Width, flaeche.Height, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(bild)) { g.Clear(Color.Transparent); Rivals.CelebrationHud.Male(g, flaeche, anlass, konfetti, t); }
                var n2 = 0;
                for (var y = 0; y < 500; y += 5) { for (var x = 300; x < 1620; x += 7) { if (bild.GetPixel(x, y).A > 60) { n2++; } } }
                return n2;
            }
            Soll(Gezeichnet(1.2) > 200, $"{art}: die Karte erscheint nicht");
            Soll(Gezeichnet(3.9) == 0, $"{art}: steht nach 3,8 s noch da");
            Soll(Rivals.CelebrationHud.IstPersoenlich(art) && Rivals.CelebrationHud.DauerVon(art) < Rivals.CelebrationHud.Dauer,
                 $"{art}: nicht kuerzer als die Website-Feier");
        }
    }

    /// <summary>
    /// Der Modus einer Runde aus dem zuletzt gelesenen Menue (2026-09-28): Rivals-Schirm,
    /// Horizon-Play-Anmeldung, gewoehnliche Anmeldung -- und Rivals nur ohne Gegner.
    /// </summary>
    private static void LapModeFromMenus()
    {
        var typ = typeof(Rivals.OverlayController);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var feld = typ.GetField("_modusSchirm", flags)!;
        Rivals.RecordedLap Runde(float meter, string modus = "unknown") =>
            new() { LapSeconds = 60f, LengthMetres = meter, Mode = modus, ModeEvidence = "none" };
        Rivals.RecordedLap Fall(string modus, TimeSpan alter, int platz, float meter = 1880f,
                                string? strecke = "Soni Circuit", double km = 1.9, string vorher = "unknown")
        {
            var roh = (Rivals.OverlayController)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typ);
            (string, DateTime, string?, double)? beleg = (modus, DateTime.UtcNow - alter, strecke, km);
            feld.SetValue(roh, beleg);
            var lap = Runde(meter, vorher);
            roh.ModusAusSchirm(lap, platz);
            return lap;
        }

        var r = Fall("rivals", TimeSpan.FromMinutes(3), 1);
        Soll(r.Mode == "rivals" && r.Track == "Soni Circuit" && r.ModeEvidence.StartsWith("screen:rivals:"),
             $"Rivals-Schirm, allein gefahren: {r.Mode}/{r.Track}/{r.ModeEvidence}");
        var z = Fall("rivals", TimeSpan.FromMinutes(3), 4);
        Soll(z.Mode == "unknown" && z.ModeEvidence.StartsWith("conflict:"),
             $"Rivals-Schirm, aber Platz 4 -- das war ein Rennen: {z.Mode}/{z.ModeEvidence}");
        var lang = Fall("rivals", TimeSpan.FromMinutes(3), 1, meter: 2600f);
        Soll(lang.Mode == "rivals" && string.IsNullOrEmpty(lang.Track),
             "eine Runde anderer Laenge bekommt den Namen vom Rivals-Schirm");
        Soll(Fall("rivals", TimeSpan.FromHours(3), 1).Mode == "unknown", "ein drei Stunden alter Rivals-Schirm gilt noch");
        var rennen = Fall("race", TimeSpan.FromMinutes(10), 5, strecke: null, km: 0);
        Soll(rennen.Mode == "race" && string.IsNullOrEmpty(rennen.Track),
             $"eine gewoehnliche Anmeldung ergibt kein Rennen: {rennen.Mode}");
        Soll(Fall("horizon-play", TimeSpan.FromMinutes(50), 3).Mode == "unknown",
             "eine 50 Minuten alte Horizon-Play-Anmeldung gilt noch");
        Soll(Fall("horizon-play", TimeSpan.FromMinutes(20), 1).Mode == "horizon-play",
             "eine frische Horizon-Play-Anmeldung gilt nicht");
        Soll(Fall("rivals", TimeSpan.FromMinutes(1), 1, vorher: "freeroam").Mode == "freeroam",
             "der Menueschirm ueberschreibt die eigene Freiwelt-Uhr");
        Soll(Rivals.OverlayController.LaengePasst(1900, 1.9) && Rivals.OverlayController.LaengePasst(1850, 1.9)
             && !Rivals.OverlayController.LaengePasst(2500, 1.9) && !Rivals.OverlayController.LaengePasst(1900, 0),
             "die Laengenpruefung gegen 'Route Length' stimmt nicht");
        Soll(Rivals.OwnTimes.ModeText("race") != "race", "der Modus 'race' hat keinen Anzeigenamen");
    }

    /// <summary>
    /// Streifen aus, Live-Karte an: ein Paket darf die Karte nicht verstecken
    /// (2026-09-28: sie blinkte das ganze Rennen, weil UpdateDelta bei abgeschaltetem
    /// Streifen HideHud() rief -- und das nahm die Karte mit).
    /// </summary>
    private static void LiveMapStaysWithoutStrip()
    {
        var typ = typeof(Rivals.OverlayController);
        var roh = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typ);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var einst = new Rivals.OverlaySettings { DeltaHud = false, LiveMap = true };
        using var karte = new Rivals.LiveMapHud(einst, new Rectangle(-32000, -32000, 1920, 1080));
        karte.Show();
        typ.GetField("_settings", flags)!.SetValue(roh, einst);
        typ.GetField("_liveMap", flags)!.SetValue(roh, karte);
        Soll(ForzaPacket.TryParse(SelfTest.LapPacket(1, 1, 500f, 20f, 0f, 1234, 700, 3), out var paket),
             "Testpaket nicht lesbar");
        typ.GetMethod("UpdateDelta", flags)!.Invoke(roh, new object[] { paket });
        Soll(karte.Visible, "ein Paket bei abgeschaltetem Streifen versteckt die Live-Karte -- sie blinkt im Rennen");
        karte.Hide();
    }

    /// <summary>
    /// Ein ersetzter Controller haelt JEDEN Zeitgeber an, und seine geschlossenen
    /// Fenster nehmen spaete Aufrufe hin, ohne zu werfen (2026-09-28: "Cannot access a
    /// disposed object ... CarNoteHud" beim Start, weil das Automenue des alten
    /// Controllers weiterlief).
    /// </summary>
    private static void ReplacedControllerGoesQuiet()
    {
        // Ohne den Konstruktor (der schreibt im Hintergrund in den Rundenbestand):
        // ein leeres Objekt, jedes Zeitgeber-Feld mit einem eigenen Zeitgeber belegt.
        var typ = typeof(Rivals.OverlayController);
        var roh = (Rivals.OverlayController)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typ);
        var felder = typ.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                        .Where(f => f.FieldType == typeof(System.Windows.Forms.Timer)).ToList();
        foreach (var f in felder) { f.SetValue(roh, new System.Windows.Forms.Timer()); }
        var gestoppt = roh.AlleZeitgeber().ToHashSet();
        var vergessen = felder.Where(f => !gestoppt.Contains((System.Windows.Forms.Timer)f.GetValue(roh)!))
                              .Select(f => f.Name).ToList();
        Soll(felder.Count >= 5 && vergessen.Count == 0,
             $"Dispose haelt diese Zeitgeber nicht an: {string.Join(", ", vergessen)}");
        foreach (var t in gestoppt) { t.Dispose(); }

        var weg = new Rectangle(-32000, -32000, 1920, 1080);
        var notiz = new Rivals.CarNoteHud(new Rivals.OverlaySettings(), weg);
        notiz.Show();
        notiz.Dispose();
        try
        {
            notiz.Show();
            notiz.Hide();
            notiz.Render();
            notiz.SetArea(new Rectangle(-32000, -32000, 2560, 1440));
            notiz.TopMost = true;
            Soll(!notiz.Visible, "ein geschlossenes Fenster meldet sich sichtbar");
        }
        catch (ObjectDisposedException e)
        {
            Soll(false, "ein geschlossenes Overlay wirft bei einem spaeten Aufruf: " + e.Message);
        }
    }

    /// <summary>
    /// Die Reifenuebersicht (2026-09-28): ab Werk aus, Fahrenheit wird Celsius, jeder
    /// Zustand bekommt sein Schild, und gezeichnet wird nur im eigenen Block.
    /// </summary>
    private static void TyreOverview()
    {
        Soll(!new Rivals.OverlaySettings().HudTyres, "die Reifenuebersicht ist ab Werk an -- sie soll opt-in sein");

        var roh = SelfTest.LapPacket(1, 0, 100f, 10f, 0f, 1234, 700, 5);
        void Setze(string feld, float wert)
        {
            var d = ForzaPacket.Descriptors.First(x => x.Key == feld);
            if (d.Type == TelemetryValueType.Signed32) { BitConverter.GetBytes((int)wert).CopyTo(roh, d.Offset); }
            else if (d.Type == TelemetryValueType.Unsigned8) { roh[d.Offset] = (byte)wert; }
            else { BitConverter.GetBytes(wert).CopyTo(roh, d.Offset); }
        }
        Setze("TireTempFrontLeft", 212f);        // 100 Grad C
        Setze("TireTempRearRight", 150.8f);      // 66 Grad C
        Setze("Brake", 255);
        Setze("TireSlipRatioFrontLeft", -1.5f);  // blockiert
        Setze("TireCombinedSlipFrontLeft", 1.5f);
        Setze("TireSlipAngleFrontRight", 1.2f);  // rutscht quer
        Setze("TireSlipRatioRearRight", 1.3f);   // dreht durch
        Setze("WheelOnRumbleStripRearLeft", 1);
        Setze("WheelInPuddleRearLeft", 0.4f);    // Float nach der Beschreibung
        BitConverter.GetBytes(1).CopyTo(roh, ForzaPacket.Descriptors.First(x => x.Key == "WheelInPuddleRearRight").Offset);
        Setze("NormalizedSuspensionTravelRearLeft", 0.97f);
        Soll(ForzaPacket.TryParse(roh, out var paket), "das Testpaket liess sich nicht lesen");
        var z = Rivals.TyreHud.AusPaket(paket);
        Soll(Math.Abs(z.VL.TempC - 100f) < 0.05f && Math.Abs(z.HR.TempC - 66f) < 0.05f,
             $"Reifentemperatur nicht von Fahrenheit umgerechnet ({z.VL.TempC}, {z.HR.TempC})");
        Soll(Rivals.TyreHud.Schild(z.VL)?.Text == "LOCK", "ein blockiertes Rad bekommt kein LOCK");
        Soll(Rivals.TyreHud.Schild(z.VR)?.Text == "SLIDE", "ein quer rutschendes Rad bekommt kein SLIDE");
        Soll(Rivals.TyreHud.Schild(z.HR)?.Text == "SPIN", "ein durchdrehendes Rad bekommt kein SPIN");
        Soll(Rivals.TyreHud.Schild(z.HL) is null, "ein ruhiges Rad bekommt ein Schild");
        Soll(z.HL.Rumble && Math.Abs(z.HL.Puddle - 0.4f) < 1e-4 && z.HL.Travel > 0.9f && z.VL.Grip < 0.01f,
             $"Curb, Pfuetze, Federweg oder Grip kommen nicht an ({z.HL.Rumble}, {z.HL.Puddle}, {z.HL.Travel}, {z.VL.Grip})");
        // So sendet es Horizon 6 wirklich: eine Ganzzahl 1.
        Soll(z.HR.Puddle == 1f, $"die Pfuetze als Ganzzahl 1 kommt als {z.HR.Puddle} an");

        var kalt = Rivals.TyreHud.TempFarbe(55f);
        var gut = Rivals.TyreHud.TempFarbe(92f);
        var heiss = Rivals.TyreHud.TempFarbe(135f);
        Soll(kalt.B > kalt.R && gut.G > gut.R && gut.G > gut.B && heiss.R > heiss.G,
             "die Temperaturfarben lesen nicht kalt-blau, normal-gruen, heiss-rot");

        var s = new Rivals.OverlaySettings { HudTyres = true };
        var vorher = 0;
        foreach (var flaeche in new[] { new Size(1920, 1080), new Size(2560, 1440), new Size(3840, 2160) })
        {
            var b = Rivals.TyreHud.Lege(s, flaeche);
            Soll(b.Left >= 0 && b.Top >= 0 && b.Right <= flaeche.Width && b.Bottom <= flaeche.Height,
                 $"{flaeche.Height}p: der Block ragt aus dem Bild ({b})");
            Soll(b.Width > vorher, $"{flaeche.Height}p: der Block waechst nicht mit der Aufloesung");
            vorher = b.Width;
            using var bild = new Bitmap(flaeche.Width, flaeche.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bild)) { g.Clear(Color.Transparent); Rivals.TyreHud.Male(g, s, flaeche, Rivals.TyreHud.Beispiel()); }
            var innen = 0;
            var aussen = 0;
            for (var y = 0; y < flaeche.Height; y += 6)
            {
                for (var x = 0; x < flaeche.Width; x += 6)
                {
                    if (bild.GetPixel(x, y).A == 0) { continue; }
                    if (b.Contains(x, y)) { innen++; } else { aussen++; }
                }
            }
            Soll(innen > 100 && aussen == 0, $"{flaeche.Height}p: gezeichnet innen {innen}, ausserhalb {aussen}");
        }
    }

    /// <summary>
    /// Overlays ueber dem Spiel abschaltbar, trotzdem im Aufnahmefenster; der Delta-
    /// Streifen uebergibt nur seinen Inhalt, die Eingabespuren getrennt (2026-09-28).
    /// Alle Fenster ausserhalb des Schirms -- ein Test blitzt nie ueber dem Bild auf.
    /// </summary>
    private static void OverlayOutputAndCost()
    {
        var weg = new Rectangle(-32000, -32000, 1920, 1080);
        var vorher = Rivals.OverlayAusgabe.ImSpiel;
        var flaecheVorher = Rivals.OverlayAusgabe.Flaeche;
        Rivals.OverlayAusgabe.Flaeche = weg;
        try
        {
            // AUS: gewollt ja, ueber dem Spiel nein, im Aufnahmefenster ja.
            Rivals.OverlayAusgabe.SetzeImSpiel(false);
            using (var meldung = new Rivals.MessageHud(weg))
            {
                meldung.Zeige("Tune storage almost full", "962 of 1000 tunes", Color.Gold, 5);
                Soll(meldung.Gewollt && meldung.Visible && !((Control)meldung).Visible,
                     "bei abgeschalteten Overlays erscheint die Meldung trotzdem ueber dem Spiel");
                Soll(Rivals.OverlayAusgabe.Alle().Contains(meldung), "die Meldung fehlt im Aufnahmefenster");
                using var bild = new Bitmap(1920, 1080, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(bild)) { g.Clear(Color.Transparent); meldung.MaleFuerAufnahme(g); }
                var gefunden = false;
                for (var y = 60; y < 300 && !gefunden; y += 4)
                {
                    for (var x = 500; x < 1400 && !gefunden; x += 8) { gefunden = bild.GetPixel(x, y).A > 100; }
                }
                Soll(gefunden, "das Aufnahmefenster zeichnet die Meldung nicht");
                // EIN: jetzt erscheint sie auch ueber dem Spiel (ausserhalb des Schirms).
                Rivals.OverlayAusgabe.SetzeImSpiel(true);
                Soll(((Control)meldung).Visible, "nach dem Einschalten erscheint die gewollte Meldung nicht");
                meldung.Hide();
                Soll(!meldung.Gewollt && !((Control)meldung).Visible, "Verstecken nimmt die Meldung nicht weg");
            }

            // DER DELTA-STREIFEN uebergibt nur seinen Inhalt, die Spuren getrennt.
            using (var hud = new Rivals.DeltaHud(new Rectangle(-32000, -32000, 3840, 2160), new Rivals.OverlaySettings { HudInputs = true }))
            {
                hud.Show();
                Application.DoEvents();
                for (var i = 0; i < 5; i++)
                {
                    hud.PushInputs(0.5f, 0f, 0f, 0.1f, 3, null, null);
                    hud.Update(-0.25f - i * 0.01f, "same car, same tune", 20f, null, string.Empty, "to beat: 39.325 -- website best, this car");
                }
                var b = hud.Bereich;
                var s = hud.SpurBereich;
                Soll(!b.IsEmpty && b.Width * b.Height < 3840 * 2160 / 4,
                     $"der Delta-Streifen uebergibt fast den ganzen Schirm ({b.Width}x{b.Height})");
                Soll(!s.IsEmpty && !b.IntersectsWith(s), $"die Eingabespuren liegen nicht in ihrem eigenen Block ({s})");
                hud.Hide();
            }
        }
        finally
        {
            Rivals.OverlayAusgabe.SetzeImSpiel(vorher);
            Rivals.OverlayAusgabe.Flaeche = flaecheVorher;
        }
    }

    /// <summary>
    /// Die Feier (2026-09-27): der Ton hat seine Lautstaerke und endet leise, die
    /// Karte steht auf jeder Aufloesung im Band, und am Ende ist nichts mehr zu sehen.
    /// </summary>
    private static void CelebrationLooksRight()
    {
        var proben = Rivals.CelebrationSound.Proben();
        var spitze = proben.Max(Math.Abs);
        Soll(Math.Abs(spitze - Rivals.CelebrationSound.Spitze) < 0.005, $"der Feierton hat nicht seine Lautstaerke ({spitze:0.000})");
        Soll(proben.All(float.IsFinite), "der Feierton enthaelt ungueltige Werte");
        var rate = Rivals.CelebrationSound.Rate;
        double Rms(double von, double bis)
        {
            var a = (int)(von * rate);
            var b = Math.Min(proben.Length, (int)(bis * rate));
            var summe = 0.0;
            for (var i = a; i < b; i++) { summe += proben[i] * proben[i]; }
            return Math.Sqrt(summe / Math.Max(1, b - a));
        }
        Soll(Rms(0.15, 0.7) > 0.03, "der Glockenklang des Feiertons ist nicht zu hoeren");
        Soll(Rms(1.95, 2.0) < 0.003, "der Feierton endet nicht leise");
        // Der Ton fuer ein neues Auto: leiser, ohne Knall, endet ebenso leise.
        var neuTon = Rivals.CelebrationSound.ProbenNeuesAuto();
        Soll(Math.Abs(neuTon.Max(Math.Abs) - (Rivals.CelebrationSound.Spitze * 0.85f)) < 0.005 && neuTon.All(float.IsFinite),
             "der Ton fuer ein neues Auto hat nicht seine Lautstaerke");
        Soll(neuTon[^((int)(0.05 * rate))..].Max(Math.Abs) < 0.01, "der Ton fuer ein neues Auto endet nicht leise");
        Soll(Rivals.CelebrationSound.WavNeuesAuto.Length == 44 + (neuTon.Length * 2), "der Ton fuer ein neues Auto ist kein gueltiges WAV");
        var wav = Rivals.CelebrationSound.Wav;
        Soll(wav.Length == 44 + (proben.Length * 2) && System.Text.Encoding.ASCII.GetString(wav, 0, 4) == "RIFF"
             && System.Text.Encoding.ASCII.GetString(wav, 8, 4) == "WAVE" && BitConverter.ToInt32(wav, 24) == rate,
             "der Feierton ist kein gueltiges WAV");

        var anlass = new Rivals.CelebrationHud.Anlass("You beat the leaderboard!", "1:23.456", 0.556,
                                                      "Website best 1:24.012", "Goliath · Porsche 911 GT3 RS '19 · S1 900");
        Soll(anlass.Vorsprung.StartsWith('−') && anlass.Vorsprung.EndsWith(" s"), "der Vorsprung traegt kein Minuszeichen");
        var neuesAuto = new Rivals.CelebrationHud.Anlass("New car on the leaderboard!", "1:31.208", 0,
                                                         "Its first time here on the website -- thanks to you!",
                                                         "Goliath \u00b7 Toyota Supra RZ '98 \u00b7 A 800",
                                                         Rivals.CelebrationHud.FeierArt.NeuesAuto, "NEW");
        Soll(neuesAuto.Vorsprung == "NEW", "der feste Chip einer Meldung wird nicht gezeigt");
        foreach (var jetzt in new[] { anlass, neuesAuto })
        foreach (var groesse in new[] { new Size(1280, 720), new Size(1920, 1080), new Size(3840, 2160) })
        {
            var konfetti = new Rivals.CelebrationHud.Konfetti(1, jetzt.Art);
            var buehne = Rivals.CelebrationHud.Buehne(groesse);
            (int Sichtbar, int Unterhalb, int Deckend) Zaehle(double t)
            {
                using var bild = new Bitmap(groesse.Width, groesse.Height, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(bild))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    Rivals.CelebrationHud.Male(g, groesse, jetzt, konfetti, t);
                }
                var daten = bild.LockBits(new Rectangle(Point.Empty, groesse), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    var zeile = new byte[daten.Stride];
                    int sichtbar = 0, unterhalb = 0, deckend = 0;
                    var grenze = (int)Math.Ceiling(buehne.Bottom) + 2;
                    for (var y = 0; y < groesse.Height; y++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(daten.Scan0 + (y * daten.Stride), zeile, 0, daten.Stride);
                        for (var x = 0; x < groesse.Width; x++)
                        {
                            var alpha = zeile[(x * 4) + 3];
                            if (alpha <= 8) { continue; }
                            sichtbar++;
                            if (alpha > 230) { deckend++; }
                            if (y > grenze) { unterhalb++; }
                        }
                    }
                    return (sichtbar, unterhalb, deckend);
                }
                finally { bild.UnlockBits(daten); }
            }
            var mitten = Zaehle(1.0);
            Soll(mitten.Deckend > groesse.Width * groesse.Height / 200,
                 $"bei {groesse.Width}x{groesse.Height} steht mitten in der Feier keine Karte");
            Soll(mitten.Unterhalb == 0, $"bei {groesse.Width}x{groesse.Height} zeichnet die Feier unter ihr Band ({mitten.Unterhalb} Pixel)");
            Soll(Zaehle(Rivals.CelebrationHud.Dauer - 0.01).Deckend == 0,
                 $"bei {groesse.Width}x{groesse.Height} steht am Ende der Feier noch etwas deckend da");
            Soll(Zaehle(Rivals.CelebrationHud.Dauer).Sichtbar == 0, $"bei {groesse.Width}x{groesse.Height} bleibt nach der Feier etwas stehen");
        }
    }

    /// <summary>
    /// Runden, die warten muessen (2026-09-27): ausgeschaltet, Server weg, Server
    /// kaputt -- sie bleiben, die schnellste je Auto, und gehen raus, sobald es geht.
    /// Vor dem Senden zaehlt die Bestenliste DES TAGES; eine Ablehnung haelt an.
    /// </summary>
    private static void LapsWaitForTheServer()
    {
        var heim = Path.Combine(Path.GetTempPath(), "forza-edge-queue-" + Guid.NewGuid().ToString("N")[..8]);
        var vorher = Environment.GetEnvironmentVariable("FORZA_SUBMIT_HOME");
        var pauseVorher = Rivals.LapAutoSubmit.NachreichPause;
        Environment.SetEnvironmentVariable("FORZA_SUBMIT_HOME", heim);
        Rivals.LapAutoSubmit.NachreichPause = TimeSpan.Zero;

        static int FreierPort()
        {
            var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            l.Start();
            var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }
        Rivals.RivalsDataset Board(int bestMs) => new()
        {
            Tracks = new List<string> { "Test Circuit" },
            Classes = new List<string> { "A", "B", "C", "D", "R", "S1", "S2" },
            CarIds = new List<int> { 1234 },
            Flags = new List<string> { "clean" },
            Boards = new List<Rivals.RivalsDataset.Board>
            {
                new() { Track = 0, Klass = 0, GroupCar = new[] { 0 }, GroupSignature = new[] { 1 },
                        LapGroup = new[] { 0 }, LapMs = new[] { bestMs } },
            },
        };
        Rivals.RecordedLap Lap(float sek, int car = 1234)
        {
            // Mit voller Spur: ohne sie geht seit 2026-09-28 keine Runde mehr hinaus.
            var l = new Rivals.RecordedLap { LapSeconds = sek, CarOrdinal = car, CarClass = 3, Mode = "rivals",
                                             FullTrack = TestSpur() };
            for (var i = 0; i < 12; i++) { l.Samples.Add(new Rivals.LapSample()); }
            return l;
        }

        var port = FreierPort();
        var totPort = FreierPort();
        var antworten = new System.Collections.Concurrent.ConcurrentQueue<int>();
        var einreichungen = 0;
        var ohneSpur = 0;
        string? angemeldetAls = null;
        string? eingereichtAls = null;
        using var hoerer = new System.Net.HttpListener();
        hoerer.Prefixes.Add($"http://localhost:{port}/");
        hoerer.Start();
        var schleife = Task.Run(async () =>
        {
            while (hoerer.IsListening)
            {
                System.Net.HttpListenerContext k;
                try { k = await hoerer.GetContextAsync().ConfigureAwait(false); }
                catch (Exception) { break; }
                var pfad = k.Request.Url?.AbsolutePath ?? string.Empty;
                string anfrage;
                using (var r = new StreamReader(k.Request.InputStream)) { anfrage = await r.ReadToEndAsync().ConfigureAwait(false); }
                var name = System.Text.Json.Nodes.JsonNode.Parse(anfrage)?["gamertag"]?.GetValue<string>();
                int status;
                string rumpf;
                if (pfad.EndsWith("/api/lap/register"))
                {
                    // Wie der echte Server: ein Name mit unerlaubten Zeichen wird abgewiesen.
                    status = (name ?? string.Empty).Contains('@') ? 400 : 200;
                    rumpf = status == 200 ? """{"install_id":"edge","secret":"geheim"}"""
                                          : """{"error":"'gamertag' enthaelt unerlaubte Zeichen."}""";
                    if (status == 200) { angemeldetAls = name; }
                }
                else
                {
                    eingereichtAls = name;
                    Interlocked.Increment(ref einreichungen);
                    // Jede Einreichung traegt ihre volle Telemetrie (seit 2026-09-28).
                    var b64 = System.Text.Json.Nodes.JsonNode.Parse(anfrage)?["telemetry"]?["data"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(b64)) { Interlocked.Increment(ref ohneSpur); }
                    status = antworten.TryDequeue(out var s) ? s : 200;
                    rumpf = status == 200 ? """{"ok":true}""" : $$"""{"error":"test {{status}}"}""";
                }
                var bytes = System.Text.Encoding.UTF8.GetBytes(rumpf);
                k.Response.StatusCode = status;
                k.Response.ContentType = "application/json";
                await k.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                k.Response.Close();
            }
        });

        try
        {
            Rivals.RivalsDataset? data = Board(60000);
            var einst = new Rivals.OverlaySettings
            {
                SubmitLaps = false,
                Gamertag = "EdgeTester",
                DatasetUrl = $"http://localhost:{totPort}",
                DatasetDownloadSeconds = 20,
            };
            var sender = new Rivals.LapAutoSubmit(() => data is null ? null : new Rivals.RivalsAdvisor(data),
                                                  einst, _ => { });
            // DIE FEIER haengt an derselben Entscheidung: nur eine Runde, die die
            // Website schlaegt, und nicht zweimal fuer dieselbe Zeit.
            var rekorde = new List<Rivals.LapAutoSubmit.Rekord>();
            sender.RekordGefahren += rekorde.Add;
            string Fahre(Rivals.RecordedLap l) =>
                sender.ConsiderAsync(l, "course_test", "Test Circuit").GetAwaiter().GetResult();
            string? Nachreichen() => sender.NachreichenAsync().GetAwaiter().GetResult();
            Rivals.LapQueue.Eintrag? Wartend(int car = 1234) =>
                Rivals.LapQueue.Alle().FirstOrDefault(e => e.Key == "test circuit|A|" + car);

            // OHNE VOLLE SPUR geht keine Runde hinaus und keine wartet (seit 2026-09-28).
            var nackt = Lap(58f);
            nackt.FullTrack = null;
            Soll(Fahre(nackt).Contains("full telemetry") && Rivals.LapQueue.Anzahl() == 0,
                 "eine Runde ohne volle Telemetrie wurde vorgemerkt oder anders beschieden");
            // Die Feier davor zaehlt hier nicht mit: eine echte Runde hat ihre Spur immer.
            rekorde.Clear();

            // AUSGESCHALTET: die schnellere Runde wartet, nichts geht hinaus.
            Soll(Fahre(Lap(59f)).StartsWith("kept to submit later"), "ausgeschaltet wird eine schnelle Runde nicht vorgemerkt");
            Soll(Rivals.LapQueue.Anzahl() == 1 && einreichungen == 0, "ausgeschaltet ging etwas hinaus oder nichts wartet");
            Fahre(Lap(59.5f));
            Soll(Math.Abs(Wartend()!.Lap.LapSeconds - 59f) < 1e-3, "eine langsamere Runde verdraengt die wartende");
            Fahre(Lap(58.5f));
            Soll(Math.Abs(Wartend()!.Lap.LapSeconds - 58.5f) < 1e-3 && Rivals.LapQueue.Anzahl() == 1,
                 "eine schnellere Runde ersetzt die wartende nicht");
            Soll(Fahre(Lap(61f)).StartsWith("not submitted") && Rivals.LapQueue.Anzahl() == 1,
                 "eine Runde, langsamer als die Bestenliste, wird vorgemerkt");
            Soll(rekorde.Count == 2 && rekorde[0].LapMs == 59000 && rekorde[0].BestenlisteMs == 60000
                 && rekorde[1].LapMs == 58500 && rekorde[0].Track == "Test Circuit" && rekorde[0].Klasse.StartsWith("A"),
                 $"gefeiert wurde nicht genau 59 s und 58,5 s ({rekorde.Count} Feiern) -- auch eine langsamere als die wartende, "
                 + "oder eine langsamer als die Bestenliste?");
            Soll(Nachreichen() is null && einreichungen == 0, "ausgeschaltet wird nachgereicht");

            // EINGESCHALTET, SERVER WEG: bleibt, mit einem Versuch mehr.
            einst.SubmitLaps = true;
            Nachreichen();
            Soll(Wartend()?.Attempts == 1, "ohne Server verschwindet die Runde oder zaehlt keinen Versuch");

            // SERVER DA, ABER KAPUTT (503): bleibt.
            einst.DatasetUrl = $"http://localhost:{port}";
            antworten.Enqueue(503);
            Nachreichen();
            Soll(Wartend()?.Attempts == 2 && einreichungen == 1, "ein 503 verwirft die wartende Runde");

            // SERVER GESUND: raus, ins Buch, aus der Schlange.
            Nachreichen();
            Soll(Rivals.LapQueue.Anzahl() == 0 && einreichungen == 2, "die wartende Runde ging nicht hinaus");
            Soll(Rivals.LapAutoSubmit.LedgerLaden().TryGetValue("test circuit|A|1234", out var imBuch) && imBuch == 58500,
                 "die nachgereichte Runde steht nicht im Buch");

            // LIVE, SERVER KAPUTT: die Runde wartet, statt verloren zu gehen.
            antworten.Enqueue(503);
            Soll(Fahre(Lap(58f)).StartsWith("kept to submit later") && Rivals.LapQueue.Anzahl() == 1,
                 "eine gescheiterte Einreichung wird nicht vorgemerkt");

            // DIE BESTENLISTE DES TAGES: inzwischen 57 s -- die 58 s gehen still weg,
            // ohne Anfrage. Das Auto ohne Eintrag geht, der Server sagt 409: raus,
            // ins Buch, und eine Ablehnung ist gemerkt.
            einst.SubmitLaps = false;
            Fahre(Lap(80f, car: 9999));
            einst.SubmitLaps = true;
            data = Board(57000);
            var vorAnfragen = einreichungen;
            antworten.Enqueue(409);
            Nachreichen();
            Soll(Rivals.LapQueue.Anzahl() == 0 && einreichungen == vorAnfragen + 1,
                 "eine nicht mehr schnellere Runde wurde gesendet oder die 409 blieb liegen");
            Soll(Rivals.LapAutoSubmit.LedgerLaden().ContainsKey("test circuit|A|9999"),
                 "eine vom Server abgelehnte Runde steht nicht im Buch");

            // DIE BREMSE: zwei Ablehnungen binnen 24 h -- es ruht, ohne Anfrage.
            Soll(Rivals.LapQueue.RuhtBis(DateTimeOffset.Now) is null, "nach einer Ablehnung ruht das Nachreichen schon");
            Rivals.LapQueue.AblehnungMerken(DateTimeOffset.Now);
            Soll(Rivals.LapQueue.RuhtBis(DateTimeOffset.Now) is { } ruht && ruht > DateTimeOffset.Now.AddHours(23),
                 "nach zwei Ablehnungen ruht das Nachreichen nicht");
            Soll(Rivals.LapQueue.RuhtBis(DateTimeOffset.Now.AddHours(25)) is null, "die Bremse loest sich nach 24 h nicht");
            einst.SubmitLaps = false;
            Fahre(Lap(56f));
            einst.SubmitLaps = true;
            vorAnfragen = einreichungen;
            Soll((Nachreichen() ?? string.Empty).Contains("paused") && einreichungen == vorAnfragen
                 && Rivals.LapQueue.Anzahl() == 1, "trotz Bremse wurde nachgereicht");

            // OHNE DATEN: nicht entscheidbar -- wartet, statt verworfen zu werden.
            Rivals.LapQueue.AllesVergessen();
            data = null;
            Soll(Fahre(Lap(55f)).StartsWith("kept to submit later") && Rivals.LapQueue.Anzahl() == 1,
                 "eine Runde ohne geladene Bestenliste geht verloren");
            Soll(Rivals.LapQueue.AllesVergessen() == 1 && Rivals.LapQueue.Anzahl() == 0, "Verwerfen laesst Runden liegen");

            // OHNE GAMERTAG (seit 2026-09-27): wird trotzdem eingereicht -- gesperrt
            // wird ueber Kennung und Hardware-Hash. Der Name reist in der
            // unterschriebenen Einreichung mit, hier also leer.
            data = Board(57000);
            einst.Gamertag = string.Empty;
            Soll(sender.Hindernis() is null, "ohne Gamertag gilt das Einreichen als verhindert");
            Soll(Fahre(Lap(50f)).StartsWith("submitted") && eingereichtAls == string.Empty,
                 "ohne Gamertag wird nicht eingereicht, oder der leere Name reist nicht mit");

            // EIN NAME, DEN DER SERVER ABWEIST: ohne ihn anmelden, die Runde geht trotzdem.
            File.Delete(Rivals.LapSubmit.IdentityPath);
            einst.Gamertag = "bad@tag";
            Soll(Fahre(Lap(70f, car: 9999)).StartsWith("submitted") && angemeldetAls == string.Empty,
                 "ein abgewiesener Gamertag verhindert die Anmeldung");
            Soll(eingereichtAls == "bad@tag", "der eingetragene Name reist nicht mit der Einreichung");

            // EIN SPAETER GEAENDERTER NAME braucht keine neue Anmeldung.
            angemeldetAls = "unberuehrt";
            einst.Gamertag = "Spaeter";
            antworten.Enqueue(409);
            Fahre(Lap(69f, car: 9999));
            Soll(angemeldetAls == "unberuehrt" && eingereichtAls == "Spaeter",
                 "ein geaenderter Gamertag meldet die Installation neu an");

            // Gefeiert: 59, 58,5, 58 (live, dann wartend), 56 und 50 s -- nie das Auto ohne
            // Bestenlisten-Eintrag (9999) und nie eine Runde ohne geladene Bestenliste.
            Soll(rekorde.Count == 5 && rekorde.All(r => r.LapMs < r.BestenlisteMs),
                 $"die Feier kam {rekorde.Count}-mal statt 5-mal");

            // EIN NEUES AUTO AUF DER LISTE (2026-09-27): gemeldet erst, wenn der Server
            // angenommen hat, nur beim ersten Mal -- und nachgereicht als solches erkennbar.
            var neueAutos = new List<Rivals.LapAutoSubmit.NeuesAuto>();
            sender.NeuesAutoEingetragen += neueAutos.Add;
            Soll(Fahre(Lap(90f, car: 7777)).StartsWith("submitted") && neueAutos.Count == 1
                 && !neueAutos[0].Nachgereicht && neueAutos[0].LapMs == 90000 && neueAutos[0].CarOrdinal == 7777,
                 "ein neues Auto auf der Liste wird nicht gemeldet");
            Fahre(Lap(89f, car: 7777));
            Soll(neueAutos.Count == 1, "die zweite Runde desselben neuen Autos wird noch einmal als neu gemeldet");
            antworten.Enqueue(503);
            Fahre(Lap(92f, car: 7778));
            Soll(neueAutos.Count == 1, "ein neues Auto wird gemeldet, obwohl der Server nicht angenommen hat");
            // Die wartende 7778 geht nach -- die Bremse vorher loesen, sie stammt aus den Schritten oben.
            File.Delete(Path.Combine(Rivals.LapQueue.Folder, "refusals.json"));
            Nachreichen();
            Soll(neueAutos.Count == 2 && neueAutos[1].Nachgereicht && neueAutos[1].CarOrdinal == 7778,
                 "ein nachgereichtes neues Auto wird nicht (als nachgereicht) gemeldet");
            Soll(rekorde.Count == 5, "ein neues Auto wurde als Rekord gefeiert");

            Soll(Rivals.LapAutoSubmit.AusgangFuer(200) == Rivals.LapAutoSubmit.Ausgang.Gesendet
                 && Rivals.LapAutoSubmit.AusgangFuer(409) == Rivals.LapAutoSubmit.Ausgang.NichtSchneller
                 && Rivals.LapAutoSubmit.AusgangFuer(422) == Rivals.LapAutoSubmit.Ausgang.Ungueltig
                 && new[] { 401, 403, 429, 500, 503 }.All(c => Rivals.LapAutoSubmit.AusgangFuer(c)
                                                          == Rivals.LapAutoSubmit.Ausgang.SpaeterNochmal),
                 "ein Statuscode landet im falschen Ausgang");
            Soll(einreichungen > 0 && ohneSpur == 0,
                 $"{ohneSpur} von {einreichungen} Einreichungen gingen ohne volle Telemetrie hinaus");
        }
        finally
        {
            hoerer.Stop();
            try { schleife.Wait(2000); } catch (Exception) { }
            Environment.SetEnvironmentVariable("FORZA_SUBMIT_HOME", vorher);
            Rivals.LapAutoSubmit.NachreichPause = pauseVorher;
            try { Directory.Delete(heim, recursive: true); } catch (Exception) { }
        }
    }

    /// <summary>
    /// "Mit Forza starten": der Autostart traegt --tray, andere Verknuepfungen nicht,
    /// ein alter Autostart ohne Zusatz wird beim Ersetzen nachgezogen -- und dieselbe
    /// Kopie laeuft nur einmal, ein zweiter Start weckt die erste.
    /// </summary>
    private static void StartWithForza()
    {
        var wurzel = Path.Combine(Path.GetTempPath(), "fhc-mitforza-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(wurzel);
        try
        {
            var exe = Path.Combine(wurzel, "app", AppInfo.ExeName);
            var auto = Path.Combine(wurzel, "auto.lnk");
            var schreib = Path.Combine(wurzel, "desk.lnk");
            Soll(Shortcuts.Create(ShortcutPlace.Autostart, out _, auto, zielExe: exe), "Mit Forza: Autostart liess sich nicht anlegen");
            Soll(Shortcuts.ArgumentsOf(auto) == Shortcuts.TrayArgument,
                 $"Mit Forza: der Autostart traegt '{Shortcuts.ArgumentsOf(auto)}' statt {Shortcuts.TrayArgument}");
            Soll(Shortcuts.Create(ShortcutPlace.Desktop, out _, schreib, zielExe: exe), "Mit Forza: Schreibtisch liess sich nicht anlegen");
            Soll(string.IsNullOrEmpty(Shortcuts.ArgumentsOf(schreib)), "Mit Forza: die Schreibtisch-Verknuepfung bekam einen Zusatz");

            // Ein Autostart von vor 2026-09-27: dieselbe Kopie, kein Zusatz.
            var alt = Path.Combine(wurzel, "alt.lnk");
            Shortcuts.Create(ShortcutPlace.Desktop, out _, alt, zielExe: exe);
            Soll(Shortcuts.Create(ShortcutPlace.Autostart, out _, alt, ersetzen: true, zielExe: exe)
                 && Shortcuts.ArgumentsOf(alt) == Shortcuts.TrayArgument,
                 "Mit Forza: ein alter Autostart ohne Zusatz wird nicht nachgezogen");

            // Eine Instanz je Programmordner.
            var a = Einzelinstanz.NameFuer(Path.Combine(wurzel, "app"));
            Soll(a == Einzelinstanz.NameFuer(Path.Combine(wurzel, "APP") + "\\")
                 && a != Einzelinstanz.NameFuer(Path.Combine(wurzel, "andere")),
                 "Mit Forza: der Name je Kopie ist nicht stabil oder nicht verschieden");
            using var erste = Einzelinstanz.Anmelden(Path.Combine(wurzel, "app"));
            using var zweite = Einzelinstanz.Anmelden(Path.Combine(wurzel, "app"));
            Soll(erste.Erste && !zweite.Erste, "Mit Forza: zwei Instanzen derselben Kopie gelten beide als erste");
            using var geweckt = new ManualResetEventSlim();
            erste.Horchen(() => geweckt.Set());
            zweite.ErsteWecken();
            Soll(geweckt.Wait(TimeSpan.FromSeconds(3)), "Mit Forza: ein zweiter Start weckt die erste Instanz nicht");
        }
        finally
        {
            try { Directory.Delete(wurzel, recursive: true); } catch (Exception) { }
        }
    }

    /// <summary>
    /// Die Autonotiz geht beim Losfahren weg: laenger als drei Sekunden ueber 30 km/h,
    /// ohne Unterbrechung -- ein Anrollen am Startplatz zaehlt nicht.
    /// </summary>
    private static void CarNoteGoesWhenDriving()
    {
        var t0 = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
        var w = new Rivals.LosfahrWaechter();
        Soll(!w.Update(0, t0) && !w.Update(5, t0.AddSeconds(10)), "Notiz: Stillstand und Schritttempo gelten als Losfahren");
        Soll(!w.Update(20, t0.AddSeconds(11)) && !w.Update(20, t0.AddSeconds(13.5)),
             "Notiz: weg, bevor drei Sekunden schnelle Fahrt vergangen sind");
        Soll(w.Update(20, t0.AddSeconds(14.2)), "Notiz: nach ueber drei Sekunden ueber 30 km/h nicht weg");
        // Unterbrochen: ein Bremsen unter 30 km/h setzt die Zeit zurueck.
        var u = new Rivals.LosfahrWaechter();
        u.Update(20, t0);
        u.Update(20, t0.AddSeconds(2));
        u.Update(4, t0.AddSeconds(2.5));
        Soll(!u.Update(20, t0.AddSeconds(3.2)) && !u.Update(20, t0.AddSeconds(5.9)),
             "Notiz: ein Anhalten zwischendurch setzt die Uhr nicht zurueck");
        Soll(u.Update(20, t0.AddSeconds(6.4)), "Notiz: nach der Unterbrechung und drei neuen Sekunden nicht weg");
    }

    /// <summary>
    /// Horizon Play: die Statuswoerter, der Einstieg mitten in eine Reihe und welche
    /// Strecke danach dran ist.
    /// </summary>
    private static void SeriesStatusWords()
    {
        Rivals.RouteStatus S(string t) => Rivals.RivalsScreenReader.StatusIn(t);
        Soll(S("In Progress") == Rivals.RouteStatus.InProgress, "Reihe: 'In Progress' nicht erkannt");
        Soll(S("Up Next") == Rivals.RouteStatus.UpNext, "Reihe: 'Up Next' nicht erkannt");
        Soll(S("Izu Cross-Country In Progress") == Rivals.RouteStatus.InProgress, "Reihe: angehaengtes 'In Progress' nicht erkannt");
        Soll(S("Up Nexl") == Rivals.RouteStatus.UpNext && S("In Pr0gress") == Rivals.RouteStatus.InProgress,
             "Reihe: verlesene Statuswoerter nicht erkannt");
        foreach (var fremd in new[] { "", "Next", "Upgrades", "Winter / Late Afternoon / Cloudy", "11.4 KM - 3 LAPS", "Progress" })
        {
            Soll(S(fremd) == Rivals.RouteStatus.None, $"Reihe: '{fremd}' fuer ein Statuswort gehalten");
        }

        // Einstieg bei "2/3": Strecke 01 laeuft schon und zaehlt als erledigt.
        var erledigt = Rivals.OverlayController.Einstieg(1, 3).ToHashSet();
        Soll(erledigt.SetEquals(new[] { 0 }), "Reihe: der Einstieg bei 2/3 markiert nicht genau Strecke 01");
        var stati = Rivals.OverlayController.Stati(3, erledigt);
        Soll(stati.SequenceEqual(new[] { Rivals.CourseShapeHud.TileState.Done, Rivals.CourseShapeHud.TileState.Now,
                                         Rivals.CourseShapeHud.TileState.Next }),
             "Reihe: die Karten zeigen nach dem Einstieg bei 2/3 nicht erledigt/jetzt/danach");
        Soll(Rivals.OverlayController.ErwarteteStrecke(3, 1, erledigt) == 1, "Reihe: nach dem Einstieg ist nicht Strecke 02 dran");
        erledigt.Add(1);
        Soll(Rivals.OverlayController.ErwarteteStrecke(3, 1, erledigt) == 2, "Reihe: nach dem ersten Rennen ist nicht Strecke 03 dran");
        erledigt.Add(2);
        Soll(Rivals.OverlayController.ErwarteteStrecke(3, 1, erledigt) == -1, "Reihe: nach dem letzten Rennen ist noch eine Strecke dran");
        Soll(!Rivals.OverlayController.Einstieg(5, 3).Any(i => i >= 3) && !Rivals.OverlayController.Einstieg(-1, 3).Any(),
             "Reihe: ein Einstieg ausserhalb der Strecken wird nicht begrenzt");
    }

    /// <summary>
    /// Symbol und Logo liegen in der DLL und lassen sich laden -- sonst faellt die App
    /// still auf das unscharfe 32-Pixel-Bild zurueck, und niemand merkt es.
    /// </summary>
    private static void BrandResources()
    {
        using var symbol = Marke.Symbol();
        Soll(symbol is not null, "Marke: das Symbol fehlt in der DLL (" + Marke.SymbolName + ")");
        // Die 256er-Fassung waehlt .NET nie aus: es vergleicht mit dem Breiten-Byte des
        // Verzeichnisses, und das ist fuer 256 eine 0. Windows selbst liest sie richtig
        // (Explorer, Taskleiste) -- also im Verzeichnis nachsehen statt ueber Icon.
        using (var roh = typeof(Marke).Assembly.GetManifestResourceStream(Marke.SymbolName)!)
        using (var leser = new BinaryReader(roh))
        {
            leser.ReadBytes(4);
            var anzahl = leser.ReadUInt16();
            var breiten = Enumerable.Range(0, anzahl).Select(_ =>
            {
                var e = leser.ReadBytes(16);
                return e[0] == 0 ? 256 : e[0];
            }).ToList();
            Soll(breiten.Contains(256) && breiten.Contains(48) && breiten.Contains(16),
                 "Marke: dem Symbol fehlen Groessen, vorhanden sind " + string.Join(",", breiten));
        }
        using var gross = new Icon(symbol!, 128, 128);
        Soll(gross.Width == 128, "Marke: die 128-Pixel-Fassung laesst sich nicht laden, sondern " + gross.Width);
        using var klein = new Icon(symbol!, 16, 16);
        Soll(klein.Width == 16, "Marke: das Symbol hat keine 16-Pixel-Fassung, sondern " + klein.Width);
        using var logo = Marke.Logo();
        Soll(logo is not null && logo.Width >= 128, "Marke: das Logo fehlt in der DLL oder ist zu klein");
    }

    /// <summary>
    /// Was das Loeschen im Spiel liest: welcher Schirm, welcher Eintrag, welches Symbol.
    /// Ein Fehler hier loescht das falsche Tune -- darum jede Regel einzeln.
    /// </summary>
    private static void TuneDeleterReading()
    {
        Rivals.OcrLine L(string t, double y = 100) => new(t, 100, y);
        Tuning.TuneDeleter.Schirm S(params string[] t) => Tuning.TuneDeleter.Einordnen(t.Select(x => L(x)).ToList());
        Soll(S("Delete File", "Are you sure you want to delete this file?", "Yes", "No") == Tuning.TuneDeleter.Schirm.DeleteConfirm,
             "Loeschen: die Rueckfrage wird nicht erkannt");
        Soll(S("File Options", "Load Tuning Setup", "Delete") == Tuning.TuneDeleter.Schirm.FileOptions,
             "Loeschen: der Dialog File Options wird nicht erkannt");
        Soll(S("Date Created", "Tuner Rank", "Creator") == Tuning.TuneDeleter.Schirm.TunesList,
             "Loeschen: die eigene Tune-Liste wird nicht erkannt");
        Soll(S("Date Created", "Tuner Rank", "TRENDING") == Tuning.TuneDeleter.Schirm.TuneBrowser,
             "Loeschen: die Tune-SUCHE gilt als eigene Liste -- dort darf nichts geloescht werden");
        Soll(S("My Tuning Setups", "Custom Tuning") == Tuning.TuneDeleter.Schirm.Upgrades,
             "Loeschen: das Upgrades-Menue wird nicht erkannt");
        Soll(S("Upgrades & Tuning", "Designs & Paints") == Tuning.TuneDeleter.Schirm.CarsMenu,
             "Loeschen: das Autos-Menue wird nicht erkannt");
        Soll(S("Race", "Settings") == Tuning.TuneDeleter.Schirm.Unbekannt,
             "Loeschen: ein fremder Schirm wird einem bekannten zugeordnet");

        Soll(Tuning.TuneDeleter.MarkeVon("GIULIA QUADRIFOGLIO / 2017 ALFA ROMEO") == "ALFA ROMEO"
             && Tuning.TuneDeleter.MarkeVon("2CV / 1970 CITROËN") == "CITROEN"
             && Tuning.TuneDeleter.MarkeVon("unlesbar") == "",
             "Loeschen: die Marke einer Karte wird falsch gelesen -- die Reihenfolge im Raster haengt daran");
        Soll(string.CompareOrdinal(Tuning.TuneDeleter.Grundform("Alfa Romeo"), Tuning.TuneDeleter.Grundform("ALUMICRAFT")) < 0
             && string.CompareOrdinal(Tuning.TuneDeleter.Grundform("Abarth"), Tuning.TuneDeleter.Grundform("Acura")) < 0,
             "Loeschen: die alphabetische Reihenfolge der Marken stimmt nicht");
        Soll(Tuning.TuneDeleter.Norm("GT-R l0") == Tuning.TuneDeleter.Norm("gtr 10"),
             "Loeschen: OCR-Verwechsler (l/1, 0/o) werden nicht gleichgesetzt");
        Soll(Tuning.TuneDeleter.Aehnlich("Drift Setup", "Drift Setup") == 1
             && Tuning.TuneDeleter.Aehnlich("", "Drift") == 0
             && Tuning.TuneDeleter.Aehnlich("Drift Setup", "Grip Setup") < 0.85,
             "Loeschen: die Aehnlichkeit trennt verschiedene Namen nicht");

        Tuning.StoredTune T(string name, string tuner, int tag) =>
            new($"Tuning_0247_202607{tag:00}100000", 247, new DateTime(2026, 7, tag, 10, 0, 0), name, tuner, 1,
                new DateTime(2026, 7, tag));
        var plan = new[] { T("Race", "Alice", 1), T("Race", "Alice", 5), T("Drift", "Bob", 2) };
        Soll(Tuning.TuneDeleter.Treffer(plan, "Race", "Alice", "05/07/2026")?.CreatedAt?.Day == 5,
             "Loeschen: unter gleichnamigen Tunes entscheidet das Datum nicht");
        Soll(Tuning.TuneDeleter.Treffer(plan, "Race", "Bob", "") is null,
             "Loeschen: ein Tune eines ANDEREN Tuners gilt als Treffer");
        Soll(Tuning.TuneDeleter.Treffer(plan, "Rally", "Alice", "") is null,
             "Loeschen: ein Tune mit anderem Namen gilt als Treffer");
        var gelesenSchlecht = new[] { new Tuning.StoredTune("Tuning_2542_20260605201758", 2542, new DateTime(2026, 6, 5),
            "A700 Road AWD", "x ShadowsBane x", 1, new DateTime(2026, 5, 14)) };
        Soll(Tuning.TuneDeleter.Treffer(gelesenSchlecht, "moo Road AWD", "x ShadowsBane x", "14/05/2026") is not null,
             "Loeschen: ein schlecht gelesener Name mit genau passendem Tuner und Datum wird nicht erkannt");
        Soll(Tuning.TuneDeleter.Treffer(gelesenSchlecht, "moo Road AWD", "x ShadowsBane x", "15/05/2026") is null,
             "Loeschen: ein schlecht gelesener Name genuegt ohne passendes Datum");
        Soll(Tuning.TuneDeleter.DialogPasst("Road AWD", "x ShadowsBane x", gelesenSchlecht[0])
             && !Tuning.TuneDeleter.DialogPasst("Road AWD", "Someone Else", gelesenSchlecht[0])
             && !Tuning.TuneDeleter.DialogPasst("AWD", "x ShadowsBane x", gelesenSchlecht[0])
             && !Tuning.TuneDeleter.DialogPasst("Drift Setup", "x ShadowsBane x", gelesenSchlecht[0]),
             "Loeschen: der Dialog-Abgleich nimmt ein fremdes Tune oder verwirft ein angeschnittenes");

        using var bild = new Bitmap(200, 200);
        char Farbe(Color c)
        {
            using (var g = Graphics.FromImage(bild)) { g.Clear(Color.Black); }
            for (var y = 18; y < 24; y++) { for (var x = 19; x < 25; x++) { bild.SetPixel(x, y, c); } }
            return Tuning.TuneDeleter.Symbol(bild, new Rectangle(0, 0, 164, 164));
        }
        Soll(Farbe(Color.FromArgb(220, 40, 40)) == 'v', "Loeschen: das rote Symbol (schwaecher) wird nicht erkannt");
        Soll(Farbe(Color.FromArgb(60, 200, 60)) == '^', "Loeschen: das gruene Symbol (staerker) wird nicht erkannt");
        Soll(Farbe(Color.FromArgb(150, 150, 150)) == '-',
             "Loeschen: das graue Symbol (aufgespielt) wird nicht erkannt -- das aufgespielte Tune waere nicht geschuetzt");
    }

    /// <summary>
    /// Die Umbenennung in FH Companion: der Datenordner zieht um, Verknuepfungen
    /// unter dem alten Namen bekommen den neuen -- aber nur die auf DIESE Kopie.
    /// </summary>
    private static void RenameMigration()
    {
        var wurzel = Path.Combine(Path.GetTempPath(), "fhc-rename-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var alt = Path.Combine(wurzel, "ForzaGripHaptics");
            var neu = Path.Combine(wurzel, "FHCompanion");
            Directory.CreateDirectory(Path.Combine(alt, "laps"));
            File.WriteAllText(Path.Combine(alt, "laps", "a.json"), "{}");
            Soll(AppInfo.Umzug(alt, neu) == neu && File.Exists(Path.Combine(neu, "laps", "a.json")) && !Directory.Exists(alt),
                 "Umbenennung: der alte Datenordner zieht nicht um");
            Directory.CreateDirectory(alt);
            Soll(AppInfo.Umzug(alt, neu) == neu && Directory.Exists(alt),
                 "Umbenennung: ein vorhandener neuer Ordner wird ueberschrieben oder der alte angefasst");

            var ordner = Path.Combine(wurzel, "Desktop");
            Directory.CreateDirectory(ordner);
            var altLnk = Path.Combine(ordner, AppInfo.OldName + ".lnk");
            Soll(Shortcuts.Create(ShortcutPlace.Desktop, out _, altLnk, zielExe: AppInfo.OldExePath),
                 "Umbenennung: die Probe-Verknuepfung liess sich nicht anlegen");
            if (File.Exists(AppInfo.ExePath))
            {
                Soll(Shortcuts.Umbenennen(ShortcutPlace.Desktop, ordner)
                     && !File.Exists(altLnk)
                     && Shortcuts.PointsHere(Path.Combine(ordner, AppInfo.Name + ".lnk")),
                     "Umbenennung: die Verknuepfung auf diese Kopie bekommt den neuen Namen nicht");
            }
            var fremd = Path.Combine(wurzel, "Fremd");
            Directory.CreateDirectory(fremd);
            var fremdLnk = Path.Combine(fremd, AppInfo.OldName + ".lnk");
            Shortcuts.Create(ShortcutPlace.Desktop, out _, fremdLnk, zielExe: Path.Combine(wurzel, "anderswo", AppInfo.OldExeName));
            Soll(!Shortcuts.Umbenennen(ShortcutPlace.Desktop, fremd) && File.Exists(fremdLnk),
                 "Umbenennung: eine Verknuepfung auf eine ANDERE Kopie wurde umbenannt");
        }
        finally
        {
            try { Directory.Delete(wurzel, recursive: true); } catch (Exception) { }
        }
    }

    /// <summary>Der Loeschplan nimmt nur, was sicher auf keinem Auto liegt -- im Zeitraum.</summary>
    private static void DeletionPlan()
    {
        Tuning.StoredTune T(int auto, int monat, string name) =>
            new($"Tuning_{auto:0000}_2026{monat:00}01100000", auto, new DateTime(2026, monat, 1, 10, 0, 0), name, "Tuner" + auto, 1, null);
        var drauf = T(247, 6, "drauf");
        var frei = T(247, 7, "frei");
        var freiAlt = T(300, 5, "frei alt");
        var danach = T(247, 9, "nach der Pruefung");
        var alle = new List<Tuning.StoredTune> { drauf, frei, freiAlt, danach };
        var pruefung = new Tuning.TuneStorage.Usage { CheckedAt = new DateTime(2026, 8, 1), Applied = new List<string> { drauf.Folder } };
        var plan = Tuning.TunesTab.Plan(alle, pruefung, null, null).Select(t => t.Name).ToList();
        Soll(plan.SequenceEqual(new[] { "frei", "frei alt" }),
             $"Loeschplan: falsche Tunes ({string.Join(", ", plan)}) -- ein aufgespieltes oder ungeprueftes darf nie hinein");
        var juni = Tuning.TunesTab.Plan(alle, pruefung, new DateTime(2026, 6, 1), new DateTime(2026, 8, 1)).Select(t => t.Name).ToList();
        Soll(juni.SequenceEqual(new[] { "frei" }), $"Loeschplan: der Zeitraum wird nicht beachtet ({string.Join(", ", juni)})");
        Soll(Tuning.TunesTab.Plan(alle, null, null, null).Count == 0, "Loeschplan: ohne Garagen-Pruefung stehen Tunes im Plan");
    }

    /// <summary>Welches Tune in der Autonotiz steht -- nach, vor und ohne Garagen-Pruefung.</summary>
    private static void AppliedTuneForNote()
    {
        var alt = new Tuning.StoredTune("Tuning_0247_20260601100000", 247, new DateTime(2026, 6, 1), "Old", "Alice", 1, null, "old one");
        var auf = new Tuning.StoredTune("Tuning_0247_20260701100000", 247, new DateTime(2026, 7, 1), "Grip", "Bob", 2, null, "B Road Grip");
        var alle = new List<Tuning.StoredTune> { alt, auf };
        var pruefung = new Tuning.TuneStorage.Usage { CheckedAt = new DateTime(2026, 8, 1), Applied = new List<string> { alt.Folder } };
        Soll(Tuning.TuneStorage.Aufgespielt(alle, pruefung, 247) is { Tune.Name: "Old", Sicher: true },
             "Autonotiz-Tune: nicht das Tune, das die Garagen-Pruefung auf dem Auto fand");
        var danach = new Tuning.StoredTune("Tuning_0247_20260901100000", 247, new DateTime(2026, 9, 1), "New", "Cara", 3, null, "");
        Soll(Tuning.TuneStorage.Aufgespielt(new List<Tuning.StoredTune> { alt, auf, danach }, pruefung, 247) is { Tune.Name: "New", Sicher: false },
             "Autonotiz-Tune: ein nach der Pruefung geladenes Tune wird nicht genommen");
        Soll(Tuning.TuneStorage.Aufgespielt(alle, null, 247) is { Tune.Name: "Grip", Sicher: false },
             "Autonotiz-Tune: ohne Pruefung nicht das juengste Tune");
        var keinesDrauf = new Tuning.TuneStorage.Usage { CheckedAt = new DateTime(2026, 8, 1), Applied = new List<string>() };
        Soll(Tuning.TuneStorage.Aufgespielt(alle, keinesDrauf, 247) is null,
             "Autonotiz-Tune: ein Tune erscheint, obwohl laut Pruefung keins aufgespielt ist");
        Soll(Tuning.TuneStorage.Aufgespielt(alle, pruefung, 999) is null, "Autonotiz-Tune: ein fremdes Auto bekommt ein Tune");
    }

    /// <summary>Welches Tune lag bei einer Runde auf dem Auto -- sicher oder geschaetzt.</summary>
    private static void TuneForLap()
    {
        var a = new Tuning.StoredTune("Tuning_0247_20260601100000", 247, new DateTime(2026, 6, 1, 10, 0, 0), "A", "Alice", 1, null);
        var b = new Tuning.StoredTune("Tuning_0247_20260701100000", 247, new DateTime(2026, 7, 1, 10, 0, 0), "B", "Bob", 2, null);
        var c = new Tuning.StoredTune("Tuning_0300_20260601100000", 300, new DateTime(2026, 6, 1, 10, 0, 0), "C", "Cara", 3, null);
        var alle = new List<Tuning.StoredTune> { a, b, c };
        var juni = new DateTime(2026, 6, 15);
        var august = new DateTime(2026, 8, 1);
        Soll(Tuning.TunesTab.TuneZurRunde(alle, null, 247, juni) is { Tune.Creator: "Alice", Sicher: false },
             "Tune zur Runde: im Juni lag das Juni-Tune nicht drauf");
        Soll(Tuning.TunesTab.TuneZurRunde(alle, null, 247, august) is { Tune.Creator: "Bob", Sicher: false },
             "Tune zur Runde: im August nicht das juengste Tune vor der Runde");
        Soll(Tuning.TunesTab.TuneZurRunde(alle, new[] { a.Folder }, 247, august) is { Tune.Creator: "Alice", Sicher: true },
             "Tune zur Runde: das heute aufgespielte, schon vorher gespeicherte Tune wird nicht als sicher genommen");
        Soll(Tuning.TunesTab.TuneZurRunde(alle, new[] { b.Folder }, 247, juni) is { Tune.Creator: "Alice", Sicher: false },
             "Tune zur Runde: ein erst NACH der Runde gespeichertes Tune wird der Runde zugeschrieben");
        Soll(Tuning.TunesTab.TuneZurRunde(alle, null, 999, august).Tune is null,
             "Tune zur Runde: ein Auto ohne Tunes bekommt eines");
    }

    /// <summary>
    /// Die Anmeldekarten verschwinden nach der eingestellten Zeit -- auch wenn der Leser
    /// den Schirm alle anderthalb Sekunden neu erkennt.
    /// </summary>
    private static void ShapeDisplayTime()
    {
        var uhr = new Rivals.ShapeDisplayClock();
        var t0 = new DateTime(2026, 9, 26, 12, 0, 0);
        uhr.NewOffer();
        uhr.Shown(t0, 45);
        // Der Leser erkennt das Angebot immer wieder -- jedes Mal "gezeigt".
        for (var s = 1.5; s < 45; s += 1.5)
        {
            uhr.Shown(t0.AddSeconds(s), 45);
            Soll(!uhr.Due(t0.AddSeconds(s)), $"Kartenzeit: nach {s} s schon abgelaufen");
        }
        Soll(uhr.Due(t0.AddSeconds(45.2)), "Kartenzeit: nach 45 s nicht abgelaufen -- jede Erkennung startet die Zeit neu");
        Soll(uhr.Expired && !uhr.Due(t0.AddSeconds(46)), "Kartenzeit: laeuft nach dem Ablauf ein zweites Mal ab");
        uhr.Shown(t0.AddSeconds(50), 45);
        Soll(uhr.Expired, "Kartenzeit: dasselbe Angebot kommt nach dem Ablauf wieder");
        uhr.RaceEnded();
        Soll(!uhr.Expired, "Kartenzeit: nach einem Meisterschaftsrennen bleibt die Anzeige dunkel");
        uhr.Shown(t0.AddSeconds(300), 45);
        Soll(!uhr.Due(t0.AddSeconds(330)) && uhr.Due(t0.AddSeconds(346)), "Kartenzeit: nach dem Rennen laeuft keine neue Frist");
        uhr.NewOffer();
        uhr.Shown(t0.AddSeconds(400), 0);
        Soll(!uhr.Due(t0.AddSeconds(4000)), "Kartenzeit: 0 Sekunden laesst die Karten trotzdem verschwinden");
    }

    /// <summary>
    /// Ein Kurs, der nur seine Kennung als Namen traegt, bekommt den Namen seiner Runden --
    /// und eine unsichere Rivalen-Linie zeigt das Kartenbild.
    /// </summary>
    /// <remarks>
    /// Am 2026-09-26 zeigte die Kachel fuer "Shimanoyama Circuit" eine falsche Form: der
    /// Kursordner mit 34 Runden hiess nach seiner Kennung, also griff die Rivalen-Karte,
    /// und deren Linie war an einer zu kleinen Karte falsch nachgezeichnet.
    /// </remarks>
    private static void CourseNamesFromLaps()
    {
        Soll(!Rivals.LapArchive.IstStreckenname("course_-1850_1575_to_-1850_1575")
             && !Rivals.LapArchive.IstStreckenname("  ") && Rivals.LapArchive.IstStreckenname("Shimanoyama Circuit"),
             "Kursname: eine Ordnerkennung gilt als Streckenname");

        var wurzel = Path.Combine(Path.GetTempPath(), $"forza-names-test-{Environment.ProcessId}");
        try
        {
            void Kurs(string key, string name, params string?[] strecken)
            {
                var ordner = Path.Combine(wurzel, key);
                Directory.CreateDirectory(Path.Combine(ordner, "S1"));
                File.WriteAllText(Path.Combine(ordner, "course.json"),
                                  System.Text.Json.JsonSerializer.Serialize(new { Name = name, NameEvidence = "none" }));
                for (var i = 0; i < strecken.Length; i++)
                {
                    var runde = strecken[i] is null
                        ? (object)new { Lap = new { lapSeconds = 30 } }
                        : new { Lap = new { lapSeconds = 30, track = strecken[i], trackEvidence = "signup+length" } };
                    File.WriteAllText(Path.Combine(ordner, "S1", $"lap{i}.json"), System.Text.Json.JsonSerializer.Serialize(runde));
                }
            }
            Kurs("course_1_to_1", "course_1_to_1", "Shimanoyama Circuit", "Shimanoyama Circuit", null, "course_1_to_1");
            Kurs("course_2_to_2", "", "Daikoku Circuit", "Irokawa Circuit");
            Kurs("course_3_to_3", "", "Soni Circuit");
            Kurs("course_4_to_4", "Legend Island Circuit", "Other Name", "Other Name");
            var n = Rivals.LapArchive.NamenNachtragen(wurzel);
            string Name(string key) => System.Text.Json.JsonDocument.Parse(
                File.ReadAllText(Path.Combine(wurzel, key, "course.json"))).RootElement.GetProperty("Name").GetString() ?? "";
            Soll(Name("course_1_to_1") == "Shimanoyama Circuit",
                 $"Kursname: ein Kurs mit seiner Kennung als Namen heisst weiter '{Name("course_1_to_1")}'");
            Soll(Name("course_2_to_2") == "", "Kursname: bei zwei verschiedenen Namen wird trotzdem einer gesetzt");
            Soll(Name("course_3_to_3") == "", "Kursname: eine einzelne Runde genuegt schon");
            Soll(Name("course_4_to_4") == "Legend Island Circuit", "Kursname: ein gesetzter Name wird ueberschrieben");
            Soll(n == 1, $"Kursname: {n} statt 1 Kurs benannt");
        }
        finally
        {
            try { Directory.Delete(wurzel, true); } catch (Exception) { }
        }

        // Eine unsichere Rivalen-Linie zeigt das Kartenbild, auch im Linienmodus.
        var bildDatei = Path.Combine(Path.GetTempPath(), $"forza-unsicher-{Environment.ProcessId}.png");
        try
        {
            using (var probe = new Bitmap(40, 40))
            {
                using (var g = Graphics.FromImage(probe)) { g.Clear(Color.FromArgb(0, 255, 0)); }
                probe.Save(bildDatei, ImageFormat.Png);
            }
            var unsicher = new Rivals.CourseShape.Outline(
                new List<PointF> { new(0.1f, 0.1f), new(0.9f, 0.9f) }, Rivals.ShapeSource.Rivals, 1100,
                Image: bildDatei, Reliable: false);
            using var ziel = new Bitmap(100, 100);
            using (var g = Graphics.FromImage(ziel))
            {
                g.Clear(Color.Black);
                Rivals.CourseShape.Draw(g, unsicher, new RectangleF(0, 0, 100, 100), Color.Magenta, 3, Color.Empty, 50, alsBild: false);
            }
            var m = ziel.GetPixel(50, 20);
            Soll(m.G > 200 && m.R < 60, "Umriss: eine unsichere Rivalen-Linie wird gezeichnet statt des Kartenbilds");
        }
        finally
        {
            try { File.Delete(bildDatei); } catch (Exception) { }
        }
    }

    /// <summary>Gefahren, jetzt, danach -- in der Reihenfolge des Anmeldeschirms.</summary>
    private static void ChampionshipStates()
    {
        string Text(int anzahl, params int[] erledigt) => string.Join(",",
            Rivals.OverlayController.Stati(anzahl, erledigt.ToHashSet()));
        Soll(Text(3) == "None,None,None", $"Meisterschaft: vor dem ersten Rennen steht schon ein Stand ({Text(3)})");
        Soll(Text(3, 0) == "Done,Now,Next", $"Meisterschaft: nach Rennen 1 falsch ({Text(3, 0)})");
        Soll(Text(3, 0, 1) == "Done,Done,Now", $"Meisterschaft: nach Rennen 2 falsch ({Text(3, 0, 1)})");
        Soll(Text(3, 0, 1, 2) == "Done,Done,Done", $"Meisterschaft: nach dem letzten Rennen falsch ({Text(3, 0, 1, 2)})");
        Soll(Text(3, 1) == "Now,Done,Next", $"Meisterschaft: eine ausser der Reihe gefahrene Strecke falsch ({Text(3, 1)})");
    }

    // ------------------------------------------------------------ tune storage

    /// <summary>
    /// Die Tune-Container lesen: Name des Containers, Kopf, Zaehlung -- an nachgebauten
    /// Containern, nie am echten Spielstand.
    /// </summary>
    private static void TuneStorageReading()
    {
        // Ein Kopf wie gemessen (Fassung 7): Name, 4 Byte, SYSTEMTIME, 4 Byte, Kennung, Gamertag.
        byte[] Kopf(string name, ushort jahr, ushort monat, ushort tag, ulong xuid, string gt)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(7);
            w.Write(name.Length); w.Write(System.Text.Encoding.Unicode.GetBytes(name));
            w.Write(0);
            foreach (var v in new ushort[] { jahr, monat, 0, tag, 17, 21, 40, 194 }) { w.Write(v); }
            w.Write(1);
            w.Write(xuid);
            w.Write(gt.Length); w.Write(System.Text.Encoding.Unicode.GetBytes(gt));
            w.Write(new byte[40]);
            return ms.ToArray();
        }
        var t = Tuning.TuneStorage.Parse("Tuning_0247_20260606114055", Kopf("B - Road", 2026, 5, 14, 2535440000000000UL, "SolidMemo"));
        Soll(t is { CarId: 247, Name: "B - Road", Creator: "SolidMemo" }
             && t.SavedAt == new DateTime(2026, 6, 6, 11, 40, 55)
             && t.CreatedAt == new DateTime(2026, 5, 14, 17, 21, 0),
             $"Tunes: der Kopf wird falsch gelesen ({t})");
        // Mit Beschreibung -- bis zum 2026-09-26 verrutschte der Ersteller dann.
        byte[] MitBeschreibung()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(7);
            var n = "A700 Street Config"; w.Write(n.Length); w.Write(System.Text.Encoding.Unicode.GetBytes(n));
            var d = "Balanced config. Enjoy"; w.Write(d.Length); w.Write(System.Text.Encoding.Unicode.GetBytes(d));
            foreach (var v in new ushort[] { 2026, 5, 0, 16, 7, 56, 58, 77 }) { w.Write(v); }
            w.Write(1);
            w.Write(2535400000000000UL);
            var g = "Daslaker"; w.Write(g.Length); w.Write(System.Text.Encoding.Unicode.GetBytes(g));
            w.Write(new byte[40]);
            return ms.ToArray();
        }
        var beschrieben = Tuning.TuneStorage.Parse("Tuning_0269_20260606231522", MitBeschreibung());
        Soll(beschrieben is { Name: "A700 Street Config", Creator: "Daslaker" }
             && beschrieben.CreatedAt == new DateTime(2026, 5, 16, 7, 56, 0),
             $"Tunes: ein Kopf mit Beschreibung wird falsch gelesen ({beschrieben})");
        var ohneKopf = Tuning.TuneStorage.Parse("Tuning_3726_20260901080000", Array.Empty<byte>());
        Soll(ohneKopf is { CarId: 3726 } && ohneKopf.Name.Length == 0,
             "Tunes: ohne lesbaren Kopf geht auch das Auto verloren");
        Soll(Tuning.TuneStorage.Parse("Livery_0247_20260606114055", Array.Empty<byte>()) is null
             && Tuning.TuneStorage.Parse("Tuning_x_y", Array.Empty<byte>()) is null,
             "Tunes: ein fremder Container wird als Tune gezaehlt");

        var ordner = Path.Combine(Path.GetTempPath(), $"forza-tunes-test-{Environment.ProcessId}");
        try
        {
            Directory.CreateDirectory(Path.Combine(ordner, "Tuning_0247_20260606114055"));
            Directory.CreateDirectory(Path.Combine(ordner, "Tuning_0249_20260915175734"));
            Directory.CreateDirectory(Path.Combine(ordner, "Livery_0247_20260521222909"));
            File.WriteAllBytes(Path.Combine(ordner, "Tuning_0249_20260915175734", "header"),
                               Kopf("Road Purist", 2026, 5, 31, 1UL, "LetzeLU"));
            Soll(Tuning.TuneStorage.Count(ordner) == 2, "Tunes: die Zaehlung nimmt Lackierungen mit");
            var alle = Tuning.TuneStorage.Read(ordner);
            Soll(alle.Count == 2 && alle.Any(x => x.Creator == "LetzeLU"), "Tunes: die Liste liest die Koepfe nicht");
        }
        finally
        {
            try { Directory.Delete(ordner, true); } catch (Exception) { }
        }

        // Die Warnung: oben mittig, und nicht leer.
        using var bild = new Bitmap(1920, 1080, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bild))
        {
            g.Clear(Color.Transparent);
            Rivals.MessageHud.Male(g, new Size(1920, 1080), "Tune storage full",
                                   "996 of 1000 tunes -- 4 free.", Color.FromArgb(255, 107, 107));
        }
        Soll(bild.GetPixel(960, 100).A > 200 && bild.GetPixel(960, 700).A == 0,
             "Tunes: die Warnung erscheint nicht oben mittig");
    }

    // ------------------------------------------------------------ lap message after the race

    /// <summary>
    /// "lap stored" muss nach einem Sprint zu sehen sein -- allein, ohne Delta.
    /// </summary>
    /// <remarks>
    /// Gemeldet am 2026-09-25: eine halbe Stunde Meisterschaften, jede Fahrt abgelegt,
    /// keine einzige Meldung gesehen. Ein Sprint endet im Ziel, und in demselben
    /// Augenblick verschwand der Streifen, auf dem die Meldung stand.
    /// </remarks>
    private static void LapMessageAfterRace()
    {
        var s = new Rivals.OverlaySettings();
        using var streifen = new Rivals.DeltaHud(new Rectangle(0, 0, 1920, 1080), s);
        int Tinte(Action vorher)
        {
            vorher();
            using var bild = new Bitmap(1920, 1080, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bild))
            {
                g.Clear(Color.Transparent);
                streifen.PaintInto(g);
            }
            var n = 0;
            for (var y = 0; y < bild.Height; y += 3)
                for (var x = 0; x < bild.Width; x += 3)
                {
                    if (bild.GetPixel(x, y).A > 40) { n++; }
                }
            return n;
        }
        streifen.Update(-1.234f, "same car, this course", 3f);
        var imRennen = Tinte(() => { streifen.NurNotiz(false); streifen.Note("lap stored: 155.601 s, 7409 m"); });
        var nurMeldung = Tinte(() => streifen.NurNotiz(true));
        var abgelaufen = Tinte(() => streifen.Note("lap stored: 155.601 s, 7409 m", seconds: 0));
        Soll(nurMeldung > 0, "Nach dem Rennen: die Meldung \"lap stored\" erscheint nicht");
        Soll(nurMeldung < imRennen, "Nach dem Rennen: Delta und Ghost bleiben neben der Meldung stehen");
        Soll(abgelaufen == 0, $"Nach dem Rennen: ohne Meldung bleibt etwas stehen ({abgelaufen} Punkte)");

        // Eine abgelegte Runde mit einem Sprung zerfaellt in zwei Stuecke statt einer Linie quer ueber die Karte.
        var weg = new List<PointF>();
        for (var i = 0; i < 50; i++) { weg.Add(new PointF(i * 5f, 0f)); }
        for (var i = 0; i < 50; i++) { weg.Add(new PointF(2000f + (i * 5f), 900f)); }
        Soll(Rivals.LiveMapHud.Stuecke(weg).Count == 2, "Live-Karte: ein Sprung in der Referenz wird als Linie gezeichnet");
        Soll(Rivals.CourseShape.HatSprung(weg) && !Rivals.CourseShape.HatSprung(weg.Take(50).ToList()),
             "Umriss: ein Sprung in einer Runde wird nicht erkannt");
    }

    // ------------------------------------------------------------ car notes in My Cars

    /// <summary>
    /// Das Auto unter dem Rahmen im Automenue -- und dass seine Notiz bleibt.
    /// </summary>
    /// <remarks>
    /// Anlass (2026-09-25): der Nutzer stand in "My Cars" auf seinem 595 esseesse, der
    /// Reiter zeigte "No cars seen yet", und ueber dem Spiel erschien nichts. Die
    /// Telemetrie nennt dort nur das gefahrene Auto; die Liste lebte nur im Speicher.
    /// </remarks>
    private static void CarMenuNotes()
    {
        // ---- der Rahmen: gelbgruen, hohl, in Kachelgroesse -- nachgebaut wie gemessen
        Bitmap Menue(bool rahmen, bool verdeckt = false, bool nurZweiSeiten = false)
        {
            var b = new Bitmap(1280, 720, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(b);
            g.Clear(Color.FromArgb(46, 138, 126));
            using var weiss = new SolidBrush(Color.White);
            using var lime = new Pen(Color.FromArgb(202, 255, 2), 3);
            using var limeFlaeche = new SolidBrush(Color.FromArgb(202, 255, 2));
            for (var i = 0; i < 3; i++) { g.FillRectangle(weiss, 150 + (i * 250), 140, 220, 170); }
            // Koeder: ein gelbgruener Strich und ein Logo-Klecks, wie auf dem echten Schirm.
            g.FillRectangle(limeFlaeche, 300, 118, 180, 2);
            g.FillRectangle(limeFlaeche, 60, 300, 14, 10);
            if (rahmen)
            {
                if (nurZweiSeiten)
                {
                    g.DrawLine(lime, 395, 132, 625, 132);
                    g.DrawLine(lime, 395, 132, 395, 318);
                }
                else
                {
                    g.DrawRectangle(lime, 395, 132, 230, 186);
                }
            }
            if (verdeckt)
            {
                // Ein fremdes Fenster ueber der rechten unteren Ecke -- wie im Bild des Nutzers.
                using var fenster = new SolidBrush(Color.FromArgb(20, 22, 26));
                g.FillRectangle(fenster, 600, 230, 300, 200);
            }
            return b;
        }
        using (var b = Menue(true))
        {
            var r = Rivals.CarGridReader.FindeRahmen(b);
            Soll(r is { } k && Math.Abs(k.X - 395) <= 3 && Math.Abs(k.Width - 232) <= 4,
                 $"Automenue: der Rahmen wird nicht gefunden ({r})");
        }
        using (var b = Menue(true, verdeckt: true))
        {
            Soll(Rivals.CarGridReader.FindeRahmen(b) is not null,
                 "Automenue: ein halb verdeckter Rahmen wird nicht mehr erkannt");
        }
        using (var b = Menue(false))
        {
            Soll(Rivals.CarGridReader.FindeRahmen(b) is null,
                 "Automenue: ohne Rahmen wird trotzdem einer gefunden -- ein Strich oder Logo?");
        }
        using (var b = Menue(true, nurZweiSeiten: true))
        {
            Soll(Rivals.CarGridReader.FindeRahmen(b) is null,
                 "Automenue: zwei Striche gelten als Rahmen");
        }

        // ---- aus den zwei Titelzeilen der Name im Stil des Datensatzes
        var zeilen = new List<Rivals.OcrLine> { new("1968 ABARTH", 0, 40), new("595 ESSEESSE", 0, 10) };
        var gebaut = Rivals.CarGridReader.NameAus(zeilen);
        Soll(gebaut is { } nb && nb.Name == "ABARTH 595 ESSEESSE '68" && nb.Jahr == 1968,
             $"Automenue: aus den Titelzeilen wird '{gebaut?.Name}' statt \"ABARTH 595 ESSEESSE '68\"");
        Soll(Rivals.CarGridReader.NameAus(new List<Rivals.OcrLine> { new("595 ESSEESSE", 0, 10) }) is null,
             "Automenue: ohne Baujahr wird trotzdem ein Name gebaut");
        Soll(Rivals.CarGridReader.JahrVon("Abarth 595 esseesse '68") == 1968
             && Rivals.CarGridReader.JahrVon("Acura Integra A-Spec '23") == 2023,
             "Automenue: das Baujahr eines Datensatz-Namens wird falsch gelesen");

        // ---- die Notizen: bleiben, und die richtige gewinnt
        var datei = Path.Combine(Path.GetTempPath(), $"forza-notes-test-{Environment.ProcessId}.json");
        try
        {
            File.Delete(datei);
            var n = new Rivals.CarNotes(datei);
            var meldungen = 0;
            n.Changed += () => meldungen++;
            n.NoteModel(2017, "Abarth 595 esseesse '68", "menu");
            Soll(meldungen == 1, "Notizen: ein neues Auto meldet sich nicht");
            n.NoteModel(2017, "Abarth 595 esseesse '68", "menu");
            Soll(meldungen == 1, "Notizen: dasselbe Auto meldet sich bei jedem Lesen neu");
            Soll(new Rivals.CarNotes(datei).Lookup(Rivals.CarNotes.ModelKey(2017)) is not null,
                 "Notizen: ein gesehenes Auto ist nach einem Neustart weg");

            var aufbau = Rivals.CarNotes.Fingerprint(2017, 600, 1, 4, 7000, 900);
            n.Note(aufbau, 2017, "Abarth 595 esseesse '68", 600, 80);
            n.SetComment(Rivals.CarNotes.ModelKey(2017), "Modell");
            Soll(n.CommentFor(aufbau, 2017) == "Modell", "Notizen: die Modellnotiz gilt nicht fuer einen Aufbau");
            n.SetComment(aufbau, "Aufbau");
            Soll(n.CommentFor(aufbau, 2017) == "Aufbau", "Notizen: die Aufbaunotiz geht der Modellnotiz nicht vor");
            Soll(n.CommentFor(null, 2017) == "Modell", "Notizen: im Menue (ohne Aufbau) gilt nicht die Modellnotiz");

            var vorher = n.Count;
            var neu = n.NoteModels(new[] { (2017, (string?)"Abarth 595 esseesse '68"), (3726, "Acura Integra A-Spec '23"), (0, null) }, "garage");
            Soll(neu == 1 && n.Count == vorher + 1, $"Notizen: die Garage bringt {neu} statt 1 neues Auto");
            Soll(n.Lookup(Rivals.CarNotes.ModelKey(2017))?.Source == "menu",
                 "Notizen: die Garage ueberschreibt, woher ein Auto bekannt ist");
        }
        finally
        {
            try { File.Delete(datei); } catch (Exception) { }
        }

        // ---- der ganze Weg an einer gezeichneten Kachel, wenn Datensatz und OCR da sind
        var pfad = Rivals.RivalsDataset.FindDefaultPath();
        if (pfad is null) { return; }
        var rat = new Rivals.RivalsAdvisor(Rivals.RivalsDataset.Load(pfad));
        Soll(Rivals.CarGridReader.Erkenne(zeilen, rat) is { Ordinal: 2017 },
             "Automenue: \"595 ESSEESSE / 1968 ABARTH\" ergibt nicht den Abarth 595 esseesse '68");
        Soll(Rivals.CarGridReader.Erkenne(new List<Rivals.OcrLine> { new("595 ESSEESSE", 0, 10), new("1971 ABARTH", 0, 40) }, rat) is null,
             "Automenue: ein falsches Baujahr ergibt trotzdem ein Auto");
        var leser = new Rivals.RivalsScreenReader(rat, new Rivals.OverlaySettings());
        if (!leser.OcrAvailable) { return; }
        using var karte = new Bitmap(3840, 2160, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(karte))
        {
            g.Clear(Color.FromArgb(46, 138, 126));
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            using var lime = new Pen(Color.FromArgb(202, 255, 2), 8);
            g.DrawRectangle(lime, 800, 380, 677, 515);
            g.FillRectangle(Brushes.Black, 806, 386, 665, 503);
            g.FillRectangle(Brushes.White, 812, 392, 653, 491);
            using var gross = new Font("Arial", 34, FontStyle.Bold, GraphicsUnit.Pixel);
            using var klein = new Font("Arial", 30, FontStyle.Regular, GraphicsUnit.Pixel);
            using var mittig = new StringFormat { Alignment = StringAlignment.Center };
            g.DrawString("595 ESSEESSE", gross, Brushes.Black, new RectangleF(812, 415, 653, 44), mittig);
            g.DrawString("1968 ABARTH", klein, Brushes.Gray, new RectangleF(812, 466, 653, 40), mittig);
        }
        var gelesen = Rivals.CarGridReader.LiesBild(karte, leser.ReadLines, rat);
        Soll(gelesen is { Auto: { Ordinal: 2017 } },
             $"Automenue: eine gezeichnete Kachel wird als '{gelesen?.Gelesen}' gelesen, nicht als Abarth 595 esseesse");
    }

    // ------------------------------------------------------------ map lines

    /// <summary>
    /// Glaettung, Anordnung untereinander, Kartenbild -- seit 2026-09-25.
    /// </summary>
    /// <remarks>
    /// Anlass: "very pixely and low quality". Geprueft wird, was man sonst nur im Bild
    /// saehe: dass die Glaettung Treppen wirklich wegnimmt und die Enden stehen laesst,
    /// dass untereinander angeordnete Kacheln einander nicht ueberdecken, und dass der
    /// Bildmodus das Bild zeigt -- und ohne Bild auf die Linie zurueckfaellt.
    /// </remarks>
    private static void MapLines()
    {
        // ---- Glaettung: eine Treppe (die Rasterstufen einer Linie) wird gerade
        var treppe = new List<PointF>();
        for (var i = 0; i <= 200; i++) { treppe.Add(new PointF(i, (i / 4) % 2 == 0 ? 0f : 3f)); }
        var glatt = Rivals.CourseShape.Glaetten(treppe, false, 6f);
        var mitte = glatt.Skip(glatt.Length / 5).Take(glatt.Length * 3 / 5).ToList();
        Soll(mitte.All(q => Math.Abs(q.Y - 1.5f) < 0.6f),
             $"Glaettung: die Treppe bleibt stufig (Abweichung bis {mitte.Max(q => Math.Abs(q.Y - 1.5f)):0.00})");
        Soll(glatt[0] == treppe[0] && glatt[^1] == treppe[^1],
             "Glaettung: ein offener Weg verliert seine Enden -- die Startscheibe laege neben der Linie");
        Soll(Rivals.CourseShape.Glaetten(treppe, false, 0f).Length == treppe.Count,
             "Glaettung 0 veraendert die Linie");

        // Ein Rundkurs bleibt, wo er war, und bekommt keine Naht.
        var ring = new List<PointF>();
        for (var i = 0; i < 64; i++)
        {
            var w = i * Math.PI * 2 / 64;
            ring.Add(new PointF((float)(100 + (50 * Math.Cos(w))), (float)(100 + (50 * Math.Sin(w)))));
        }
        var rund = Rivals.CourseShape.Glaetten(ring, true, 4f);
        Soll(rund.All(q => float.IsFinite(q.X) && float.IsFinite(q.Y)), "Glaettung: ein Rundkurs ergibt NaN");
        Soll(Math.Abs(rund.Average(q => q.X) - 100) < 1.5 && Math.Abs(rund.Average(q => q.Y) - 100) < 1.5,
             "Glaettung: ein Rundkurs wandert beim Glaetten");
        var naht = Math.Sqrt(Math.Pow(rund[0].X - rund[^1].X, 2) + Math.Pow(rund[0].Y - rund[^1].Y, 2));
        Soll(naht < 10, $"Glaettung: der Rundkurs hat eine Naht von {naht:0.0} Punkten");

        // Grenzfaelle: zwei Punkte, lauter gleiche, ein sehr langer Weg.
        Rivals.CourseShape.Glaetten(new List<PointF> { new(1, 1), new(2, 2) }, false, 5f);
        Rivals.CourseShape.Glaetten(Enumerable.Repeat(new PointF(3, 3), 10).ToList(), true, 5f);
        var lang = Enumerable.Range(0, 100_000).Select(i => new PointF(i * 0.5f, (i % 7) * 0.1f)).ToList();
        Soll(Rivals.CourseShape.Glaetten(lang, false, 3f).Length <= 6001,
             "Glaettung: ein langer Weg wird nicht auf hoechstens 6000 Proben begrenzt");

        // ---- Untereinander: drei Kacheln, keine ueberdeckt die andere
        var quadrat = new Rivals.CourseShape.Outline(
            new List<PointF> { new(0.1f, 0.1f), new(0.9f, 0.1f), new(0.9f, 0.9f), new(0.1f, 0.9f) },
            Rivals.ShapeSource.Telemetry, 100, Closed: true);
        var s = new Rivals.OverlaySettings
        {
            CourseShapes = true, CourseShapeLine = "#ff00ff", CourseShapeBackAlpha = 0,
            CourseShapeLayout = "vertical", CourseShapeWidth = 3, HudCourseX = 0.1, HudCourseY = 0.1,
        };
        var flaeche = new Size(1920, 1080);
        var a = Rivals.CourseShapeHud.Lege(s, flaeche, 3);
        Soll(a.Block.Height > a.Block.Width * 2 && a.Block.Width == a.Kachel + (2 * a.Fuge),
             $"Untereinander: der Block ist nicht hoch und schmal ({a.Block})");
        Soll(a.Block.Bottom <= flaeche.Height && a.Block.Right <= flaeche.Width,
             $"Untereinander: der Block ragt aus dem Bild ({a.Block})");
        var drei = new List<(string, Rivals.CourseShape.Outline?)> { ("A", quadrat), ("B", quadrat), ("C", quadrat) };
        int Tinte(Bitmap b, Rectangle r)
        {
            var n = 0;
            for (var y = r.Top; y < r.Bottom; y += 2)
                for (var x = r.Left; x < r.Right; x += 2)
                {
                    var q = b.GetPixel(x, y);
                    if (q.R > 180 && q.G < 90 && q.B > 180) { n++; }
                }
            return n;
        }
        using (var bild = new Bitmap(flaeche.Width, flaeche.Height))
        {
            using (var g = Graphics.FromImage(bild))
            {
                g.Clear(Color.Black);
                Rivals.CourseShapeHud.Male(g, s, flaeche, drei);
            }
            for (var i = 0; i < 3; i++)
            {
                var zelle = new Rectangle(a.Block.X + a.Fuge, a.Block.Y + (i * (a.Kopf + a.Kachel + a.Fuge)) + a.Kopf,
                                          a.Kachel, a.Kachel);
                Soll(Tinte(bild, zelle) > 20, $"Untereinander: Kachel {i + 1} ist leer -- die Umrisse liegen woanders");
            }
        }

        // ---- Bildmodus: das Bild erscheint; fehlt es, bleibt die Linie
        var datei = Path.Combine(Path.GetTempPath(), $"forza-map-test-{Environment.ProcessId}.png");
        try
        {
            using (var probe = new Bitmap(60, 30))
            {
                using (var g = Graphics.FromImage(probe)) { g.Clear(Color.FromArgb(0, 255, 0)); }
                probe.Save(datei, ImageFormat.Png);
            }
            var mitBild = quadrat with { Source = Rivals.ShapeSource.Rivals, Image = datei };
            var kasten = new RectangleF(10, 10, 100, 100);
            using var ziel = new Bitmap(120, 120);
            using (var g = Graphics.FromImage(ziel))
            {
                g.Clear(Color.Black);
                Rivals.CourseShape.Draw(g, mitBild, kasten, Color.Magenta, 3, Color.Empty, 50, alsBild: true);
            }
            var m = ziel.GetPixel(60, 60);
            Soll(m.G > 200 && m.R < 60, "Bildmodus: der Kartenausschnitt erscheint nicht in der Kachel");
            Soll(ziel.GetPixel(60, 18).G < 60, "Bildmodus: das Bild wird verzerrt statt eingepasst");

            var ohneBild = mitBild with { Image = datei + ".fehlt" };
            using var ziel2 = new Bitmap(120, 120);
            using (var g = Graphics.FromImage(ziel2))
            {
                g.Clear(Color.Black);
                Rivals.CourseShape.Draw(g, ohneBild, kasten, Color.Magenta, 3, Color.Empty, 50, alsBild: true);
            }
            Soll(Tinte(ziel2, new Rectangle(0, 0, 120, 120)) > 20,
                 "Bildmodus: ohne Bilddatei erscheint gar nichts statt der Linie");
        }
        finally
        {
            try { File.Delete(datei); } catch (Exception) { }
        }

        // ---- Die aufbereiteten Rivalen-Karten, wo sie liegen (Bau-Rechner, Paket)
        var daikoku = Rivals.CourseShape.ForRoute("Daikoku Circuit", Rivals.ShapeSource.Rivals);
        if (daikoku is { Image: not null })
        {
            Soll(daikoku.Ordered && daikoku.Closed && daikoku.Points.Count > 20,
                 "Rivalen-Karte: Daikoku Circuit ist kein geschlossener Weg -- wieder eine Punktwolke?");
            Soll(File.Exists(daikoku.Image), "Rivalen-Karte: das Kartenbild fehlt");
        }
    }

    // ------------------------------------------------------------ live map

    /// <summary>
    /// Sitzt das Auto auf der Live-Karte an der richtigen Ecke -- und nordweisend?
    /// </summary>
    /// <remarks>
    /// Ein Quadrat in Weltkoordinaten, das Auto an der NORDOSTecke (grosses X, grosses
    /// Z). Auf der Karte muss es oben rechts stehen. Unten rechts hiesse: gespiegelt --
    /// und eine gespiegelte Strecke sieht aus wie eine andere, nicht wie ein Fehler.
    /// </remarks>
    private static void LiveMapOrientation()
    {
        var s = new Rivals.OverlaySettings
        {
            LiveMap = true, LiveMapCar = "#ff00ff", CourseShapeBack = "#000000", CourseShapeBackAlpha = 255,
            HudLiveMapX = 0.5, HudLiveMapY = 0.3, HudLiveMapAlign = "center",
        };
        var flaeche = new Size(1920, 1080);
        var quadrat = new List<PointF> { new(0, 0), new(100, 0), new(100, 100), new(0, 100), new(0, 0) };
        using var bild = new Bitmap(flaeche.Width, flaeche.Height);
        using (var g = Graphics.FromImage(bild))
        {
            g.Clear(Color.Black);
            Rivals.LiveMapHud.Male(g, s, flaeche, quadrat, quadrat.Take(3).ToList(), new PointF(100, 100));
        }
        var k = Rivals.LiveMapHud.Lege(s, flaeche);
        bool Magenta(int x, int y)
        {
            for (var dy = -3; dy <= 3; dy++)
                for (var dx = -3; dx <= 3; dx++)
                {
                    var p = bild.GetPixel(Math.Clamp(x + dx, 0, flaeche.Width - 1), Math.Clamp(y + dy, 0, flaeche.Height - 1));
                    if (p.R > 200 && p.G < 60 && p.B > 200) { return true; }
                }
            return false;
        }
        var rand = 12;
        Soll(Magenta(k.Right - rand, k.Top + rand), "Live-Karte: das Auto an der Nordostecke steht nicht oben rechts");
        Soll(!Magenta(k.Right - rand, k.Bottom - rand), "Live-Karte: das Auto steht unten rechts -- die Karte ist gespiegelt");

        foreach (var schirm in new[] { new Size(1280, 720), new Size(3840, 2160), new Size(7680, 4320) })
        {
            var b = Rivals.LiveMapHud.Lege(s, schirm);
            Soll(b.Left >= 0 && b.Top >= 0 && b.Right <= schirm.Width && b.Bottom <= schirm.Height && b.Width == b.Height,
                 $"Live-Karte bei {schirm.Width}x{schirm.Height} nicht quadratisch im Bild: {b}");
        }
        using var klein = new Bitmap(10, 10);
        using var gk = Graphics.FromImage(klein);
        Rivals.LiveMapHud.Male(gk, s, flaeche, new List<PointF>(), new List<PointF>(), null);
        Rivals.LiveMapHud.Male(gk, s, flaeche, new List<PointF> { new(5, 5) }, new List<PointF> { new(5, 5) }, new PointF(5, 5));
        var umriss = new Rivals.CourseShape.Outline(new List<PointF> { new(0.2f, 0.1f), new(0.8f, 0.9f) },
                                                    Rivals.ShapeSource.Telemetry, 100);
        var probe = Rivals.LiveMapHud.SampleAus(umriss);
        Soll(Math.Abs(probe[0].Y - 0.9f) < 1e-5 && Math.Abs(probe[1].Y - 0.1f) < 1e-5,
             "Live-Karte: die Beispielstrecke der Vorschau wird nicht auf Norden gedreht");
    }

    // ------------------------------------------------------------ own standings

    /// <summary>
    /// Die Wertung im Reiter "My times" -- an Runden, deren Ergebnis von Hand feststeht.
    /// </summary>
    private static void OwnStandings()
    {
        Rivals.OwnTimes.Row R(string kurs, string klasse, int auto, double s, bool stehend = false) =>
            new(new Rivals.OwnTimes.Lap(kurs, kurs, klasse, auto, "t", 800, s, stehend, false,
                                        DateTime.MinValue, "rivals", ""),
                "car" + auto, null, null, null, null, null, null, 0);

        var zeilen = new List<Rivals.OwnTimes.Row>
        {
            // Kurs X, fliegend: 1 vor 2 vor 3 -> 3, 2, 1 Punkte
            R("X", "A", 1, 60.0), R("X", "A", 2, 61.0), R("X", "A", 3, 62.0),
            R("X", "A", 1, 60.8),                       // langsamere zweite Runde: zaehlt nicht
            // Kurs Y, fliegend: 2 vor 1 -> 2, 1 Punkte; Auto 3 fehlt und erbt 51.0
            R("Y", "A", 2, 50.0), R("Y", "A", 1, 51.0),
            // Kurs X STEHEND ist ein eigenes Board: nur Auto 3 -> 1 Punkt, ein Sieg
            R("X", "A", 3, 55.0, stehend: true),
            // Klasse B zaehlt fuer sich
            R("X", "B", 1, 58.0),
        };

        var p = Rivals.OwnTimes.Standings(zeilen, nachPunkten: true);
        var a = p.Where(s => s.Klass == "A").OrderBy(s => s.Place).ToList();
        Soll(a.Count == 3 && a.All(s => s.Boards == 3), "Wertung: drei Autos auf drei Boards in A");
        // 1 und 2 haben beide 4 Punkte; wie auf der Seite entscheidet das erste
        // Auftreten (Board X, dort war 1 schneller).
        Soll(a[0].Ordinal == 1 && a[0].Points == 4 && a[1].Ordinal == 2 && a[1].Points == 4
             && a[2].Ordinal == 3 && a[2].Points == 2,
             $"Wertung nach Punkten: {string.Join(", ", a.Select(s => $"{s.Ordinal}={s.Points}"))}");
        Soll(a[0].Present == 2 && a[2].Present == 2 && a[2].Wins == 1 && a[0].Wins == 1,
             "Wertung: gefahrene Kurse oder Siege falsch gezaehlt");
        Soll(Math.Abs(a[0].TotalSeconds - (60.0 + 51.0 + 55.0)) < 1e-9 && a[0].Inherited == 1,
             $"Zeitsumme mit geerbter Zeit falsch: {a[0].TotalSeconds}");
        var b = p.Where(s => s.Klass == "B").ToList();
        Soll(b.Count == 1 && b[0].Place == 1 && b[0].Points == 1 && b[0].Boards == 1,
             "Klasse B vermischt sich mit A");

        var zeit = Rivals.OwnTimes.Standings(zeilen, nachPunkten: false)
                             .Where(s => s.Klass == "A").OrderBy(s => s.Place).ToList();
        // 1: 60+51+55 = 166, 2: 61+50+55 = 166, 3: 62+51+55 = 168
        Soll(zeit[2].Ordinal == 3 && Math.Abs(zeit[2].TotalSeconds - 168.0) < 1e-9,
             "Wertung nach Zeitsumme: das Auto mit den meisten geerbten Zeiten steht nicht hinten");
        Soll(Rivals.OwnTimes.Standings(new List<Rivals.OwnTimes.Row>(), true).Count == 0,
             "Wertung ohne Runden liefert Zeilen");
    }

    // ------------------------------------------------------------ HUD editor

    /// <summary>
    /// Verstellt jeder Regler NUR sein eigenes Stueck -- und traegt eine Anordnung alle?
    /// </summary>
    /// <remarks>
    /// Bis zum 2026-09-25 verstellten Groesse, Mausrad und Anker fuer den Umriss und
    /// die Autonotiz die Delta-Zahl, und eine gespeicherte Anordnung liess beide weg.
    /// </remarks>
    private static void HudPartsAreSeparate()
    {
        var teile = Enum.GetValues<Rivals.HudPart>();
        foreach (var teil in teile)
        {
            var s = new Rivals.OverlaySettings();
            var vorher = teile.ToDictionary(t => t, t => s.PlacementOf(t));
            s.SetScale(teil, 1.77);
            s.SetAlign(teil, "right");
            s.SetPosition(teil, 0.4321, 0.1234);
            var jetzt = s.PlacementOf(teil);
            Soll(Math.Abs(jetzt.Scale - 1.77f) < 1e-4 && jetzt.Align == "right"
                 && Math.Abs(jetzt.X - 0.4321f) < 1e-4 && Math.Abs(jetzt.Y - 0.1234f) < 1e-4,
                 $"{teil}: Groesse, Anker oder Lage kommen nicht an");
            foreach (var anderes in teile.Where(t => t != teil))
            {
                Soll(s.PlacementOf(anderes) == vorher[anderes],
                     $"{teil} zu verstellen hat {anderes} mitverstellt");
            }
        }

        Soll(!Rivals.OverlaySettings.UnknownLayoutKeys().Any(),
             "eine Anordnung nennt Felder, die es nicht gibt: "
             + string.Join(", ", Rivals.OverlaySettings.UnknownLayoutKeys()));

        // Hin und zurueck: Umriss und Notiz muessen mit der Anordnung reisen.
        var name = "selbsttest-" + Guid.NewGuid().ToString("N")[..8];
        var quelle = new Rivals.OverlaySettings();
        quelle.SetScale(Rivals.HudPart.Course, 1.6);
        quelle.SetPosition(Rivals.HudPart.CarNote, 0.33, 0.44);
        quelle.SetScale(Rivals.HudPart.CarNote, 0.8);
        quelle.CarNoteWidth = 420;
        try
        {
            Soll(quelle.SaveLayout(name), "Anordnung liess sich nicht ablegen");
            var ziel = new Rivals.OverlaySettings();
            Soll(ziel.LoadLayout(name), "Anordnung liess sich nicht laden");
            Soll(Math.Abs(ziel.PlacementOf(Rivals.HudPart.Course).Scale - 1.6f) < 1e-4
                 && Math.Abs(ziel.PlacementOf(Rivals.HudPart.CarNote).X - 0.33f) < 1e-4
                 && Math.Abs(ziel.PlacementOf(Rivals.HudPart.CarNote).Scale - 0.8f) < 1e-4
                 && ziel.CarNoteWidth == 420,
                 "Umriss oder Autonotiz reisen nicht mit der Anordnung");
        }
        finally { quelle.DeleteLayout(name); }

        // Die Vorschau der Notiz: der Kasten zum Anfassen muss die Platte sein, die
        // das Overlay zeichnen wuerde -- gleich breit, und der Text muss hineinpassen.
        using var bild = new Bitmap(10, 10);
        using var g = Graphics.FromImage(bild);
        var n = new Rivals.OverlaySettings();
        foreach (var flaeche in new[] { new Size(1280, 720), new Size(3840, 2160), new Size(7680, 4320) })
        {
            var a = Rivals.CarNoteHud.Lege(g, n, flaeche, "Porsche 911 GT3 RS '19",
                                           "understeers from turn 3, tyres go off after 4 laps");
            Soll(a.Kasten.Width >= 120 && a.Kasten.Right <= flaeche.Width + 0.5f
                 && a.Kasten.Bottom <= flaeche.Height + 0.5f
                 && a.Kasten.Height >= a.TextHoehe + a.KopfHoehe,
                 $"Notizkasten bei {flaeche.Width}x{flaeche.Height} passt nicht zum Text");
        }
    }

    // ------------------------------------------------------------ lap submission

    private static void SubmitDecision()
    {
        // Ein Board: Test Circuit, Klasse A. Auto 1234 gueltig 60,000 s, ungueltig 50,000 s.
        var data = new Rivals.RivalsDataset
        {
            Tracks = new List<string> { "Test Circuit" },
            Classes = new List<string> { "A", "B", "C", "D", "R", "S1", "S2" },
            CarIds = new List<int> { 1234, 5678 },
            Flags = new List<string> { "clean" },
            Boards = new List<Rivals.RivalsDataset.Board>
            {
                new() { Track = 0, Klass = 0, GroupCar = new[] { 0, 0, 1 }, GroupSignature = new[] { 1, 0, 1 },
                        LapGroup = new[] { 0, 1, 2 }, LapMs = new[] { 60000, 50000, 70000 } },
            },
        };
        Rivals.RecordedLap Lap(float sek, int car = 1234, int klasse = 3)
        {
            var l = new Rivals.RecordedLap { LapSeconds = sek, CarOrdinal = car, CarClass = klasse, Mode = "rivals" };
            for (var i = 0; i < 12; i++) { l.Samples.Add(new Rivals.LapSample()); }
            return l;
        }
        var leer = new Dictionary<string, int>();
        // OHNE MODUS NICHT (2026-09-28): die Seite trennt die Modi, "unknown" passt nirgends hin.
        var ohneModus = Lap(59.0f);
        ohneModus.Mode = "unknown";
        Soll(!Rivals.LapAutoSubmit.Pruefen(ohneModus, "Test Circuit", data, leer).Senden,
             "eine Runde ohne bekannten Modus wird gesendet");
        Soll(Rivals.LapAutoSubmit.SaubererModus("rivals") && Rivals.LapAutoSubmit.SaubererModus("horizon-play")
             && !Rivals.LapAutoSubmit.SaubererModus("race") && !Rivals.LapAutoSubmit.SaubererModus("freeroam"),
             "welche Modi in die Wertung kommen, stimmt nicht");
        Soll(Rivals.LapAutoSubmit.Pruefen(Lap(59.0f), "Test Circuit", data, leer).Senden,
             "eine schnellere Runde (59 < 60 s) wird nicht gesendet");
        Soll(!Rivals.LapAutoSubmit.Pruefen(Lap(60.0f), "Test Circuit", data, leer).Senden,
             "eine gleich schnelle Runde wird gesendet");
        Soll(!Rivals.LapAutoSubmit.Pruefen(Lap(55.0f), "test  circuit ", data,
                                           new Dictionary<string, int> { ["test circuit|A|1234"] = 54000 }).Senden,
             "eine Runde, langsamer als die schon gesendete, wird erneut gesendet");
        Soll(!Rivals.LapAutoSubmit.Pruefen(Lap(51.0f), "Test Circuit", data, leer).Senden == false,
             "eine ungueltige Bestzeit (50 s) blockiert eine gueltige Verbesserung (51 s)");
        Soll(Rivals.LapAutoSubmit.Pruefen(Lap(80.0f, car: 9999), "Test Circuit", data, leer).Senden,
             "ein Auto ohne Bestenlisten-Eintrag wird nicht gesendet");
        Soll(!Rivals.LapAutoSubmit.Pruefen(Lap(59.0f), "Nowhere Ring", data, leer).Senden,
             "eine Strecke ohne Bestenliste wird gesendet");
        Soll(!Rivals.LapAutoSubmit.Pruefen(Lap(59.0f, klasse: 2), "Test Circuit", data, leer).Senden,
             "eine Klasse ohne Board wird gesendet");
        Soll(!Rivals.LapAutoSubmit.Pruefen(Lap(-5.0f), "Test Circuit", data, leer).Senden,
             "eine negative Zeit wird gesendet");
        Soll(!Rivals.LapAutoSubmit.Pruefen(Lap(59.0f), null, data, leer).Senden,
             "eine Runde ohne Strecke wird gesendet");
        var ohneTelemetrie = new Rivals.RecordedLap { LapSeconds = 30f, CarOrdinal = 1234, CarClass = 3 };
        Soll(!Rivals.LapAutoSubmit.Pruefen(ohneTelemetrie, "Test Circuit", data, leer).Senden,
             "eine Runde ohne Telemetrie wird gesendet");
        Soll(Rivals.LapAutoSubmit.RouteName(new Rivals.RecordedLap { Track = "course_1_2_to_1_2" },
                                            Path.GetTempPath()) is null,
             "ein Ordnername gilt als Streckenname");

        // DIE ZEIT ZUM SCHLAGEN (2026-09-27): dieselbe Regel wie beim Einreichen --
        // die beste GUELTIGE Zeit des Autos (60 s, nicht die ungueltigen 50 s), oder die
        // schon eingereichte, wenn sie schneller ist.
        Soll(Rivals.LapAutoSubmit.ZuSchlagen(data, "Test Circuit", 3, 1234, leer) == new Rivals.LapAutoSubmit.Ziel(60000, false),
             "die Zeit zum Schlagen ist nicht die beste gueltige der Website");
        Soll(Rivals.LapAutoSubmit.ZuSchlagen(data, "test circuit", 3, 1234,
                                             new Dictionary<string, int> { ["test circuit|A|1234"] = 58000 })
             == new Rivals.LapAutoSubmit.Ziel(58000, true),
             "eine schon eingereichte schnellere Zeit ist nicht die zu schlagende");
        Soll(Rivals.LapAutoSubmit.ZuSchlagen(data, "Test Circuit", 3, 9999, leer) == new Rivals.LapAutoSubmit.Ziel(null, false),
             "ein Auto ohne Eintrag bekommt eine Zeit zum Schlagen");
        Soll(Rivals.LapAutoSubmit.ZuSchlagen(data, "Nowhere Ring", 3, 1234, leer) is null
             && Rivals.LapAutoSubmit.ZuSchlagen(data, "Test Circuit", 2, 1234, leer) is null
             && Rivals.LapAutoSubmit.ZuSchlagen(data, null, 3, 1234, leer) is null,
             "ohne Board oder Strecke steht trotzdem eine Zeit zum Schlagen da");
        Soll(Rivals.OverlayController.ZielText(new Rivals.LapAutoSubmit.Ziel(60000, false)) == "to beat: 1:00.000 -- website best, this car"
             && Rivals.OverlayController.ZielText(new Rivals.LapAutoSubmit.Ziel(58000, true)).Contains("your submitted time")
             && Rivals.OverlayController.ZielText(new Rivals.LapAutoSubmit.Ziel(null, false)).Contains("not on the website")
             && Rivals.OverlayController.ZielText(null) == string.Empty,
             "die Zeile \"to beat\" sagt etwas anderes als die Regel");
        Soll(Rivals.OverlayController.KlassenName(6) == "R" && Rivals.OverlayController.KlassenName(3) == "A"
             && Rivals.OverlayController.KlassenName(7) is null,
             "die Klasse einer fremden Referenz wird falsch benannt");
    }

    // ------------------------------------------------------------ car notes

    private static void CarNotesFiles()
    {
        var ordner = Path.Combine(Path.GetTempPath(), "forza-edge-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(ordner);
        try
        {
            // 1. Kaputte Datei (Absturz beim Schreiben): laden, nicht werfen, danach heil speichern.
            var kaputt = Path.Combine(ordner, "kaputt.json");
            File.WriteAllText(kaputt, "{\"entries\": [ {\"key\": \"a\", ");
            var n1 = new Rivals.CarNotes(kaputt);
            var k = Rivals.CarNotes.Fingerprint(1, 500, 0, 4, 6000, 800);
            n1.Note(k, 1, "Test '99", 500, 100);
            n1.SetComment(k, "nach dem Absturz");
            Soll(new Rivals.CarNotes(kaputt).Lookup(k)?.Comment == "nach dem Absturz",
                 "eine abgeschnittene Notizdatei wird nicht wieder brauchbar");

            // 2. Mit Bytefolgemarke (PowerShell schreibt so) -- darf nichts ausmachen.
            var bom = Path.Combine(ordner, "bom.json");
            File.WriteAllText(bom, File.ReadAllText(kaputt), new System.Text.UTF8Encoding(true));
            Soll(new Rivals.CarNotes(bom).Lookup(k)?.Comment == "nach dem Absturz",
                 "eine Notizdatei mit BOM wird nicht gelesen");

            // 3. Unicode, sehr lang, leer.
            var uni = "Kurve 3: 🏎️ untersteuert — 高速コーナーで不安定 — ü ß";
            n1.SetComment(k, uni);
            Soll(new Rivals.CarNotes(kaputt).Lookup(k)?.Comment == uni, "Unicode-Notiz ueberlebt das Speichern nicht");
            var lang = string.Concat(Enumerable.Repeat("sehr lange Notiz ", 800));
            n1.SetComment(k, lang);
            Soll((new Rivals.CarNotes(kaputt).Lookup(k)?.Comment ?? "").Length >= 1000,
                 "eine lange Notiz wird verstuemmelt");
            n1.SetComment(k, "");
            Soll(string.IsNullOrEmpty(new Rivals.CarNotes(kaputt).Lookup(k)?.Comment),
                 "eine geleerte Notiz bleibt stehen");
            n1.Remove(k);
            Soll(new Rivals.CarNotes(kaputt).Lookup(k) is null, "ein entferntes Auto bleibt in der Datei");

            // 4. Datei, die es nicht gibt, in einem Ordner, den es nicht gibt.
            var fehlt = Path.Combine(ordner, "gibt", "es", "nicht.json");
            var n2 = new Rivals.CarNotes(fehlt);
            n2.Note(k, 1, "Test '99", 500, 100);
            n2.SetComment(k, "neu");
            Soll(File.Exists(fehlt), "eine Notiz in einem fehlenden Ordner wird nicht angelegt");
        }
        finally
        {
            try { Directory.Delete(ordner, true); } catch (Exception) { }
        }
    }

    // ------------------------------------------------------------ car names

    private static void CarNamePlaceholders()
    {
        foreach (var platzhalter in new[] { "Car #123", "car 12", "CAR#5", "Car  # 7", "", "   ", null })
        {
            Soll(!Rivals.RivalsAdvisor.IsRealCarName(platzhalter),
                 $"'{platzhalter}' gilt als echter Autoname");
        }
        foreach (var echt in new[] { "Carrera GT", "Car #12 Edition", "Porsche 911 GT3 '21", "Cars 3 Lightning McQueen", "Nissan #32 Skyline" })
        {
            Soll(Rivals.RivalsAdvisor.IsRealCarName(echt), $"'{echt}' gilt NICHT als Autoname");
        }
    }

    // ------------------------------------------------------------ route lookups

    private static void RouteLookups()
    {
        Soll(Rivals.CourseShape.ForRoute("") is null, "eine leere Strecke hat eine Karte");
        Soll(Rivals.CourseShape.ForRoute("   ") is null, "eine Leerzeichen-Strecke hat eine Karte");
        Soll(Rivals.CourseShape.ForRoute("Nonexistent Route 42") is null, "eine erfundene Strecke hat eine Karte");
        Soll(Rivals.CourseShape.ForRoute("Highway Circuit", Rivals.ShapeSource.Telemetry) is null,
             "die Telemetrie-Quelle liefert eine Karte fuer eine nie gefahrene Strecke");
        // Ordnernamen, die keine sind, bleiben unveraendert statt zu zerfallen.
        Soll(Rivals.OwnTimes.CourseText("course_x", null) == "course_x", "ein unpassender Ordnername wird verbogen");
        Soll(Rivals.OwnTimes.CourseText("", null) == "", "ein leerer Kurs wirft oder wird erfunden");
        Soll(Rivals.OwnTimes.CourseText("course_-1_-2_to_-1_-2", null).Contains("-1/-2"),
             "ein Rundkurs mit negativen Koordinaten wird falsch benannt");
        Soll(Rivals.OwnTimes.CourseText("course_1_2_to_3_4", "Cedar Run") == "Cedar Run",
             "ein echter Name wird vom Ordnernamen verdraengt");
    }

    // ------------------------------------------------------------ HUDs

    private static void HudsAtExtremes()
    {
        var settings = new Rivals.OverlaySettings();
        var umriss = new Rivals.CourseShape.Outline(
            Enumerable.Range(0, 60).Select(i => new PointF((float)Math.Cos(i / 9.5), (float)Math.Sin(i / 9.5))).ToList(),
            Rivals.ShapeSource.Telemetry, 1000);
        foreach (var schirm in new[] { new Rectangle(0, 0, 1280, 720), new Rectangle(0, 0, 7680, 4320),
                                       new Rectangle(-1920, 0, 1920, 1080), new Rectangle(0, 0, 3440, 1440) })
        {
            // Klein halten, was nur gezeichnet wird: ein 8K-Bitmap waere 130 MB.
            var bild = new Size(Math.Min(schirm.Width, 3840), Math.Min(schirm.Height, 2160));
            foreach (var anzahl in new[] { 0, 1, 3, 5 })
            {
                using var hud = new Rivals.CourseShapeHud(settings, schirm);
                var liste = Enumerable.Range(0, anzahl)
                    .Select(i => ((string Name, Rivals.CourseShape.Outline? Shape))
                                 ($"Route {i} with a rather long name", i % 2 == 0 ? umriss : null))
                    .ToList();
                hud.SetCourses(liste);
                using var bmp = new Bitmap(bild.Width, bild.Height, PixelFormat.Format32bppArgb);
                using var g = Graphics.FromImage(bmp);
                hud.Paint(g);
            }
            using var note = new Rivals.CarNoteHud(settings, schirm);
            using var nbmp = new Bitmap(bild.Width, bild.Height, PixelFormat.Format32bppArgb);
            using var ng = Graphics.FromImage(nbmp);
            note.Paint(ng, "Porsche 911 GT3 '21", string.Concat(Enumerable.Repeat("long note ", 400)));
            note.Paint(ng, "", "");
        }
    }

    // ------------------------------------------------------------ My times

    private static void OwnTimesOnBadArchives()
    {
        var wurzel = Path.Combine(Path.GetTempPath(), "forza-edge-laps-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Soll(Rivals.OwnTimes.All(wurzel).Count == 0, "ein fehlendes Rundenarchiv liefert Runden");
            // Ein Archiv mit Muell darin: falsche Tiefe, kaputtes JSON, falscher Dateiname.
            var tief = Path.Combine(wurzel, "course_1_2_to_1_2", "A", "car100", "100-700-1-4-7000-800", "untagged");
            Directory.CreateDirectory(tief);
            File.WriteAllText(Path.Combine(tief, "2026-09-24_10-00-00_61.5s.json"), "{ kaputt");
            File.WriteAllText(Path.Combine(tief, "kein_zeitname.json"), "{}");
            File.WriteAllText(Path.Combine(wurzel, "stray.json"), "{}");
            Directory.CreateDirectory(Path.Combine(wurzel, "carFOO", "x"));
            var laps = Rivals.OwnTimes.All(wurzel);
            Soll(laps.Count == 1, $"das Archiv mit Muell liefert {laps.Count} statt 1 Runde");
            Soll(Math.Abs(laps[0].Seconds - 61.5) < 1e-6, "die Zeit wird aus dem Dateinamen falsch gelesen");

            var leer = new Rivals.OwnTimes.Filter { YearFrom = 2030, YearTo = 1990 };
            Soll(Rivals.OwnTimes.Query(laps, leer, null).Count == 0, "ein unmoeglicher Jahresbereich liefert Zeilen");
            var nichts = new Rivals.OwnTimes.Filter { CarSearch = "\u0000<script>" };
            Soll(Rivals.OwnTimes.Query(laps, nichts, null).Count == 0, "eine Unsinnssuche liefert Zeilen");
            Soll(Rivals.OwnTimes.Query(Array.Empty<Rivals.OwnTimes.Lap>(), new Rivals.OwnTimes.Filter(), null).Count == 0,
                 "ein leerer Bestand liefert Zeilen");
        }
        finally
        {
            try { Directory.Delete(wurzel, true); } catch (Exception) { }
        }
    }

    // ------------------------------------------------------------ screen maths

    private static void ScreenMaths()
    {
        foreach (var r in new[] { new Rectangle(0, 0, 1, 1), new Rectangle(0, 0, 0, 0), new Rectangle(5, 5, 16, 9),
                                  new Rectangle(0, 0, 15360, 8640), new Rectangle(-3840, -200, 3840, 2160),
                                  new Rectangle(0, 0, 5120, 1440), new Rectangle(0, 0, 1080, 1920) })
        {
            var s = GameArea.SixteenNine(r);
            Soll(s.Width <= Math.Max(0, r.Width) && s.Height <= Math.Max(0, r.Height),
                 $"der 16:9-Ausschnitt von {r} ragt heraus ({s})");
            Soll(s.X >= r.X && s.Y >= r.Y, $"der 16:9-Ausschnitt von {r} liegt daneben ({s})");
            var k = GameArea.ResolutionScale(r);
            Soll(k >= 0.25f && !float.IsNaN(k) && !float.IsInfinity(k), $"Massstab {k} fuer {r}");
        }
        Soll(Math.Abs(GameArea.ResolutionScale(new Rectangle(0, 0, 3840, 2160)) - 2f) < 0.01f, "4K ist nicht Massstab 2");
        Soll(Math.Abs(GameArea.ResolutionScale(new Rectangle(0, 0, 5120, 1440)) - 1.3333f) < 0.01f,
             "32:9 nimmt nicht den mittigen 16:9-Teil");
    }

    // ------------------------------------------------------------ reader

    private static void ReaderOnDegenerateFrames()
    {
        var pfad = Rivals.RivalsDataset.FindDefaultPath();
        if (pfad is null) { return; }
        var reader = new Rivals.RivalsScreenReader(
            new Rivals.RivalsAdvisor(Rivals.RivalsDataset.Load(pfad)), new Rivals.OverlaySettings());
        if (!reader.OcrAvailable) { return; }
        foreach (var groesse in new[] { new Size(1280, 720), new Size(3840, 2160), new Size(64, 36), new Size(2, 2) })
        {
            using var schwarz = new Bitmap(groesse.Width, groesse.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(schwarz)) { g.Clear(Color.Black); }
            var st = reader.ReadBitmap(schwarz);
            Soll(st.Tracks.Count == 0, $"ein schwarzes {groesse.Width}x{groesse.Height}-Bild zeigt Strecken");
        }
    }
}
