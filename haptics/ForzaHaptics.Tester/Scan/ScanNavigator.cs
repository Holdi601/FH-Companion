using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ForzaHaptics.Tester.Rivals;

namespace ForzaHaptics.Tester.Scan;

/// <summary>
/// Das Spiel zu einer Rivals-Bestenliste fuehren: Kategorie, Strecke (ueber ihren Index im
/// Karussell) und Klasse -- Taste fuer Taste, mit einem Blick auf den Schirm vor jeder.
/// </summary>
/// <remarks>
/// Stufe 1 des Scanners in der App (Entscheidung 2026-10-03: ein Programm, der Scanner in
/// C#). Der Weg ist der des Python-Werkzeugs (`scripts/forza_navigator.ps1`, Invoke-Route):
/// eine Zustandsmaschine, kein fester Ablauf. Jeder Zyklus erkennt den Schirm und tut das
/// eine, was von dort weiterfuehrt -- so geht es von jedem Ausgangspunkt los: Titelbild,
/// Garage, Karte, oder mitten in Rivals.
///
/// Was das Python-Werkzeug gelernt hat und hier gilt:
/// - Der gelbe Rahmen auf dem Kategorieschirm beweist nichts; erst die geoeffnete Liste
///   zaehlt (Wahrzeichen oder ein Streckentitel, der in diese Kategorie gehoert).
/// - Das Karussell wird mit RECHTS bewegt, und jeder Schritt wird nachgesehen: Druecke gehen
///   verloren, und ein Schritt nach einem verlorenen Druck landet eins zu kurz.
/// - Die Klassenzeile ist weiss auf der Satellitenkarte und auf hellen Karten nur
///   sporadisch lesbar: dann warten, nie blind Y druecken -- ein Board unbekannter Klasse
///   schriebe seine Zeilen unter der falschen Klasse fort.
/// - Y oeffnet die Tabelle ("Change Rival"); ENTER auf dem Klassenschirm startet ein Rennen.
/// - "Server Error" heisst sofort aufhoeren; der Rueckzug gehoert eine Ebene hoeher.
///
/// Anhalten: die Pause-Taste, das Spiel verlassen (Alt+Tab) oder das Abbruchzeichen.
/// </remarks>
internal sealed class ScanNavigator
{
    internal sealed class Abbruch : Exception
    {
        public Abbruch(string grund) : base(grund) { }

        /// <summary>
        /// Der Lauf, den der Scanner vor dem Abbruch noch abgelegt hat -- nur wenn er schon auf der Liste
        /// stand und etwas gelesen war (BoardScanner.Abschliessen). Auf dem Weg dorthin bleibt es null.
        /// </summary>
        public BoardScanner.Ergebnis? Lauf { get; set; }

        /// <summary>Der Nutzer hat angehalten: Stopp in der App, Pause-Taste oder ein anderes Fenster nach vorn geholt.</summary>
        public bool VomNutzer { get; init; }
    }

    public sealed record Ziel(string Kategorie, int RouteIndex, string Klasse);

    public sealed record Ergebnis(string Kategorie, int RouteIndex, string? StreckeGelesen, string Klasse, int Zyklen, TimeSpan Dauer);

    private readonly WindowsOcr _ocr = new();
    private readonly ScanEingabe _eingabe;
    private readonly ScanRoutes _routen;
    private readonly Action<string> _melden;
    private readonly CancellationToken _stop;
    private readonly string? _bilder;
    private int _bildNr;

    /// <param name="bilderOrdner">Jeden Blick als kleines Bild ablegen (960x540), um einen Lauf nachzuvollziehen; null = keine.</param>
    public ScanNavigator(ScanRoutes routen, Action<string> melden, CancellationToken stop, string? bilderOrdner = null)
    {
        _routen = routen;
        _melden = melden;
        _stop = stop;
        _bilder = bilderOrdner;
        _eingabe = new ScanEingabe(stop) { Halten = 40 };
        if (_bilder is not null)
        {
            try
            {
                Directory.CreateDirectory(_bilder);
                foreach (var f in Directory.GetFiles(_bilder, "*.jpg")) { File.Delete(f); }
            }
            catch (Exception) { }
        }
    }

    // ------------------------------------------------------------------ //
    // Eingabe und Aufnahme
    // ------------------------------------------------------------------ //

    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);

    internal const byte Enter = 0x0D, Esc = 0x1B, Links = 0x25, Hoch = 0x26, Rechts = 0x27, Runter = 0x28, TasteY = 0x59, Tab = 0x09;
    private const int PauseTaste = 0x13;

    private static readonly Regex RivalsKachel = new(@"^\s*(?:Rivals|Top the Leaderboards)\s*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Die Kachel im Rivals-Hub, die zu den Kategorien fuehrt (links; daneben Monthly, My und Showcase Rivals).
    /// Ihr Titel steht zweizeilig: die Texterkennung liefert "Horizon" und "Rivals" getrennt (live 2026-10-04).
    /// </summary>
    private static readonly Regex HorizonRivalsKachel = new(@"^\s*Horizon(?:\s+Rivals)?\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private void PruefeWeiter()
    {
        if (_stop.IsCancellationRequested) { throw new Abbruch("stopped in the app"); }
        if ((GetAsyncKeyState(PauseTaste) & 0x8000) != 0) { throw new Abbruch("stopped with the Pause key"); }
        if (GameWatch.ForegroundProcessName() != GameWatch.DefaultProcessName)
        {
            throw new Abbruch("the game is no longer in front");
        }
    }

    private static string Name(byte vk) => vk switch
    {
        Enter => "ENTER", Esc => "ESC", Links => "LEFT", Hoch => "UP", Rechts => "RIGHT", Runter => "DOWN", TasteY => "Y",
        _ => vk.ToString("X2"),
    };

    private void Taste(byte vk, int nachher = 300)
    {
        PruefeWeiter();
        var scan = (byte)MapVirtualKey(vk, 0);
        var erweitert = vk is Links or Hoch or Rechts or Runter ? 1u : 0u;
        keybd_event(vk, scan, erweitert, UIntPtr.Zero);
        Thread.Sleep(45);
        keybd_event(vk, scan, erweitert | 2u, UIntPtr.Zero);
        Thread.Sleep(nachher);
    }

    private static Rectangle Flaeche() => GameArea.SixteenNine(GameArea.Find(GameWatch.DefaultProcessName));

    private static Bitmap Aufnahme() => GameArea.Capture(Flaeche(), new Size(1920, 1080));

    /// <summary>Den Zeiger auf einen Punkt des 1080p-Bezugsbilds setzen -- zweimal, denn die Markierung folgt einer BEWEGUNG.</summary>
    private static void MausAuf(double x, double y)
    {
        var f = Flaeche();
        var sx = f.X + (int)Math.Round(x * f.Width / 1920.0);
        var sy = f.Y + (int)Math.Round(y * f.Height / 1080.0);
        SetCursorPos(sx, sy);
        SetCursorPos(sx + 1, sy);
        SetCursorPos(sx, sy);
    }

    /// <summary>In die tote Ecke, damit kein Menueeintrag am Zeiger haengen bleibt.</summary>
    private static void MausParken() => MausAuf(1918, 1078);

    // ------------------------------------------------------------------ //
    // Sehen
    // ------------------------------------------------------------------ //

    private sealed record Blick(ScanScreen.Lesung Lesung, Bitmap Bild) : IDisposable
    {
        public ScanSchirm Schirm => Lesung.Schirm;
        public List<OcrLine> Zeilen => Lesung.Zeilen;
        public void Dispose() => Bild.Dispose();
    }

    private Blick Sehen(string was)
    {
        PruefeWeiter();
        var bild = Aufnahme();
        var lesung = ScanScreen.Lies(_ocr, bild);
        Merke(bild, was + "_" + lesung.Schirm);
        return new Blick(lesung, bild);
    }

    private void Merke(Bitmap bild, string was)
    {
        if (_bilder is null) { return; }
        try
        {
            using var klein = new Bitmap(bild, new Size(960, 540));
            var name = $"{++_bildNr:0000}_{string.Concat(was.Select(c => char.IsLetterOrDigit(c) ? c : '_'))}.jpg";
            klein.Save(Path.Combine(_bilder, name), ImageFormat.Jpeg);
        }
        catch (Exception) { }
    }

    private void Melde(string text) => _melden(text);

    private static string Kurz(IReadOnlyList<OcrLine> zeilen)
    {
        var s = string.Join(" / ", zeilen.OrderBy(z => z.Y).Select(z => z.Text.Trim()).Where(t => t.Length > 0).Take(14));
        return s.Length > 300 ? s[..300] + "..." : s;
    }

    /// <summary>Eine Zeile suchen -- eine Weile lang, denn "noch nicht gezeichnet" und "nicht da" sehen in EINEM Bild gleich aus.</summary>
    private OcrLine? SucheText(Regex muster, int sekunden, string was)
    {
        var bis = DateTime.UtcNow.AddSeconds(sekunden);
        var blicke = 0;
        while (true)
        {
            blicke++;
            using var b = Sehen($"{was}-{blicke}");
            foreach (var z in b.Zeilen)
            {
                if (muster.IsMatch(z.Text)) { return z; }
            }
            if (DateTime.UtcNow >= bis) { return null; }
            Thread.Sleep(700);
        }
    }

    private bool WarteAuf(ScanSchirm erwartet, int sekunden, string was)
    {
        var bis = DateTime.UtcNow.AddSeconds(sekunden);
        var blicke = 0;
        while (true)
        {
            blicke++;
            using var b = Sehen($"{was}-{blicke}");
            if (b.Schirm == erwartet) { return true; }
            if (DateTime.UtcNow >= bis)
            {
                Melde($"timeout waiting for {erwartet}; last screen {b.Schirm}: {Kurz(b.Zeilen)}");
                return false;
            }
            Thread.Sleep(450);
        }
    }

    /// <summary>Eine Taste wiederholen, bis der Schirm passt -- vor jedem Druck ein Blick.</summary>
    private bool DrueckeBis(byte taste, Func<Blick, bool> passt, int maxDruecke, int ruheMs, string was)
    {
        for (var i = 0; ; i++)
        {
            using var b = Sehen($"{was}-{i}");
            if (passt(b)) { return true; }
            if (i >= maxDruecke) { return false; }
            Taste(taste, ruheMs);
        }
    }

    /// <summary>
    /// Einen Menueeintrag ueber seinen Schriftzug waehlen: der Zeiger darauf, dann ENTER. Forzas
    /// Menues folgen dem Zeiger -- das ist der einzige bestimmte Weg zu einer benannten Kachel,
    /// wo die Markierung in keinem Text steht. (Nicht auf dem Kategorieschirm: dort folgt die
    /// Markierung dem Zeiger nicht, gemessen 2026-09-13.)
    /// </summary>
    private bool SchwebeWaehlen(Regex muster, ScanSchirm erwartet, int sekunden, string was)
    {
        var ziel = SucheText(muster, 10, was + "-look");
        if (ziel is null)
        {
            Melde($"hover target '{muster}' is not on screen");
            return false;
        }
        var z = ziel.Value;
        var cx = z.X + (z.W > 0 ? z.W / 2 : 60);
        var cy = z.Y + (z.H > 0 ? z.H / 2 : 14);
        Melde($"hovering '{z.Text}' at {cx:0},{cy:0}");
        try
        {
            MausAuf(cx - 4, cy + 2);
            Thread.Sleep(150);
            MausAuf(cx, cy);
            Thread.Sleep(700);
            Taste(Enter, 250);
        }
        finally
        {
            Thread.Sleep(250);
            MausParken();
        }
        return WarteAuf(erwartet, sekunden, was);
    }

    /// <summary>
    /// Der gelbe Markierungsrahmen als Rechteck: Spalten und Zeilen, in denen fast durchgehend
    /// Markierungsfarbe steht (die Kanten des Rahmens). Null, wenn keiner zu sehen ist.
    /// </summary>
    internal static Rectangle? Markierungsrahmen(Bitmap bild)
    {
        var px = BoardReader.Pixel.Aus(bild);
        var spalten = new int[1920];
        var zeilen = new int[1080];
        for (var y = 130; y < 960; y += 2)
        {
            for (var x = 40; x < 1880; x += 2)
            {
                var (r, g, b) = px.Farbe(x, y);
                if (!ScanScreen.IstMarkierung(r, g, b)) { continue; }
                spalten[x]++;
                zeilen[y]++;
            }
        }
        // Eine Kante ist mindestens 60 Punkte (jeder zweite gezaehlt: 120 px) lang.
        var kantenX = Enumerable.Range(0, 1920).Where(x => spalten[x] >= 60).ToList();
        var kantenY = Enumerable.Range(0, 1080).Where(y => zeilen[y] >= 60).ToList();
        if (kantenX.Count < 2 || kantenY.Count < 2) { return null; }
        var r0 = new Rectangle(kantenX.Min(), kantenY.Min(), kantenX.Max() - kantenX.Min(), kantenY.Max() - kantenY.Min());
        return r0.Width >= 120 && r0.Height >= 80 ? r0 : null;
    }

    /// <summary>Steht der Rahmen um die Kachel mit diesem Text? Siehe <see cref="ScanDialog.KachelMarkiert"/>.</summary>
    internal static bool RahmenUnterText(Bitmap bild, OcrLine text) => ScanDialog.KachelMarkiert(bild, text);

    /// <summary>
    /// Die Kachel mit diesem Text anwaehlen und oeffnen -- ENTER nur, wenn der Rahmen sie umschliesst
    /// (<see cref="RahmenUnterText"/>). Der Mauszeiger hilft hier nicht (live 2026-10-04: die
    /// Markierung folgte ihm nicht); die Pfeiltasten probieren der Reihe nach RUNTER, LINKS, RECHTS,
    /// HOCH und sehen nach jedem Druck nach. False, wenn es nicht gelingt -- dann faellt kein ENTER.
    /// </summary>
    /// <param name="schirm">Der Schirm, auf dem die Kachel steht; jeder andere bricht ohne ENTER ab.</param>
    /// <param name="ersatz">Die Taste, wenn kein Rahmen zu sehen ist (ONLINE: RUNTER; Rivals-Hub, eine Reihe: LINKS).</param>
    private bool KachelWaehlen(Regex text, string was, ScanSchirm schirm = ScanSchirm.OnlineReiter, byte ersatz = Runter)
    {
        for (var schritt = 0; schritt < 10; schritt++)
        {
            using var b = Sehen($"{was}-frame-{schritt}");
            if (b.Schirm != schirm)
            {
                Melde($"tile select: not on {schirm} ({b.Schirm}) -- no ENTER");
                return false;
            }
            var ziel = b.Zeilen.Where(l => text.IsMatch(l.Text)).Select(l => (OcrLine?)l).FirstOrDefault();
            if (ziel is null) { Thread.Sleep(600); continue; }
            if (RahmenUnterText(b.Bild, ziel.Value))
            {
                Melde($"frame on '{ziel.Value.Text}'; ENTER");
                Taste(Enter, 2500);
                return true;
            }
            // WELCHE Kachel ist markiert? Jede Kachelbeschriftung pruefen (schwarzes Band + gelbe Linie
            // unter der Kachel) und von dort auf das Ziel zu: links davon -> RECHTS, darueber -> RUNTER ...
            var markiert = b.Zeilen.Where(l => l.Y is > 280 and < 860 && l.Text.Trim().Length >= 3 && !text.IsMatch(l.Text))
                                   .Where(l => RahmenUnterText(b.Bild, l)).Select(l => (OcrLine?)l).FirstOrDefault();
            byte taste;
            if (markiert is { } m)
            {
                var dx = ziel.Value.X - m.X;
                var dy = ziel.Value.Y - m.Y;
                taste = Math.Abs(dx) > 150 ? (dx > 0 ? Rechts : Links) : (dy > 0 ? Runter : Hoch);
                Melde($"frame on '{m.Text}', not on '{ziel.Value.Text}' -> {Name(taste)}");
            }
            else
            {
                // Keine Markierung erkannt (Bild noch im Aufbau?): kurz warten, dann einmal die Ersatztaste.
                Thread.Sleep(700);
                if (schritt % 2 == 0) { continue; }
                taste = ersatz;
                Melde($"no tile frame seen -> {Name(taste)}");
            }
            Taste(taste, 800);
        }
        return false;
    }

    // ------------------------------------------------------------------ //
    // Der Weg
    // ------------------------------------------------------------------ //

    /// <summary>Zur Bestenliste von Kategorie, Strecke und Klasse. Wirft <see cref="Abbruch"/>, wenn es nicht geht.</summary>
    /// <param name="vomBoard">Das Spiel steht noch auf der vorigen Bestenliste: erst bis zur Streckenliste zurueck.</param>
    public Ergebnis Fahre(Ziel ziel, bool vomBoard)
    {
        var kat = _routen.Finde(ziel.Kategorie)
                  ?? throw new Abbruch($"unknown category '{ziel.Kategorie}'; known: {string.Join(", ", _routen.Kategorien.Select(k => k.Name))}");
        if (ziel.RouteIndex < 0 || ziel.RouteIndex >= kat.Strecken.Count)
        {
            throw new Abbruch($"route index {ziel.RouteIndex} is outside {kat.Name}'s 0..{kat.Strecken.Count - 1}");
        }
        var klasse = ziel.Klasse.Trim().ToUpperInvariant();
        if (!ScanScreen.Klassen.Contains(klasse))
        {
            throw new Abbruch($"unknown class '{ziel.Klasse}'; one of {string.Join(" ", ScanScreen.Klassen)}");
        }

        var uhr = Stopwatch.StartNew();
        var zaehler = new Dictionary<ScanSchirm, int>();
        ScanSchirm? letzter = null;
        var abseits = 0;
        var verlassen = vomBoard;
        var listeGesehen = false;
        var hubVersuch = 0;
        var kachelVersuch = 0;
        string? strecke = null;
        string? klasseGesehen = null;
        var falscheStrecke = 0;
        // Eine Bestenliste zaehlt nur, wenn DIESER Lauf sie mit Y von einem gepruefeten
        // Klassenschirm geoeffnet hat -- sonst ist es ein Ueberbleibsel, womoeglich eines anderen Boards.
        var yGedrueckt = false;
        var kategorieBestaetigt = false;
        var kategorieFalsch = 0;

        MausParken();
        for (var zyklus = 0; zyklus < 200; zyklus++)
        {
            using var blick = Sehen($"cycle-{zyklus}");
            var s = blick.Schirm;
            if (s != letzter)
            {
                Melde($"screen: {s}");
                letzter = s;
            }
            zaehler[s] = zaehler.GetValueOrDefault(s) + 1;
            // Ein Schirm, der nicht weitergeht, ist ein Fehler im Weg -- ausser den beiden, auf
            // denen gewartet wird, bis die Klassenzeile lesbar ist (bis zu sechs Minuten).
            var grenze = s is ScanSchirm.RivalDetail or ScanSchirm.Unbekannt ? 150 : 14;
            if (zaehler[s] > grenze && s is not (ScanSchirm.Laden or ScanSchirm.Ladeschirm))
            {
                throw new Abbruch($"stuck on {s} after {zaehler[s]} looks. Screen: {Kurz(blick.Zeilen)}");
            }
            // Laenger auf keinem Menue: ein langsamer Ladevorgang oder freies Fahren mit
            // verstecktem HUD -- aus dem Text nicht zu unterscheiden. ESC schadet beim Laden
            // nicht und ist aus dem freien Fahren der einzige Weg ins Menue.
            abseits = s is ScanSchirm.Laden or ScanSchirm.Ladeschirm or ScanSchirm.Unbekannt ? abseits + 1 : 0;
            if (abseits >= 5)
            {
                Melde("off any menu for 5 looks; ESC to reach a known screen");
                Taste(Esc, 1800);
                abseits = 0;
                continue;
            }

            switch (s)
            {
                case ScanSchirm.Abgestuerzt:
                    throw new Abbruch("the game has crashed: " + Kurz(blick.Zeilen));
                case ScanSchirm.ServerFehler:
                    throw new Abbruch("Server Error: the game cannot reach the leaderboard service");
                case ScanSchirm.Bestenliste:
                    if (verlassen || !yGedrueckt)
                    {
                        Melde(verlassen ? "on the previous board; backing out to re-select" : "a leaderboard this run did not open; backing out");
                        verlassen = true;
                        Taste(Esc, 1200);
                        break;
                    }
                    Melde($"leaderboard reached after {zyklus + 1} looks in {uhr.Elapsed.TotalSeconds:0} s");
                    return new Ergebnis(kat.Name, ziel.RouteIndex, strecke, klasseGesehen ?? klasse, zyklus + 1, uhr.Elapsed);
                case ScanSchirm.Laden:
                case ScanSchirm.Ladeschirm:
                    Thread.Sleep(2000);
                    break;
                case ScanSchirm.Titel:
                    Taste(Enter, 3000);
                    break;
                case ScanSchirm.SerienUpdate:
                    // ENTER blaettert durch die Seiten der Serie und schliesst sie am Ende.
                    Taste(Enter, 2000);
                    break;
                case ScanSchirm.Weiter:
                    Taste(Enter, 8000);
                    Melde("Continue; waiting for the world");
                    break;
                case ScanSchirm.Garage:
                    // Die Garage hat keinen Online-Reiter; raus mit "Drive". TAB ist die Taste dafuer in
                    // jedem Reiter des Garagenmenues ("TABULATOR Drive" in der Fussleiste) -- vorher wurde
                    // erst der Reiter CAMPAIGN gesucht und ENTER gedrueckt, und vom Reiter BUY & SELL aus
                    // landete das im Kreis (live 2026-10-04). Danach dauert das Laden: 30 s kein ESC.
                    Taste(Tab, 3000);
                    Melde("Drive -- leaving the garage");
                    abseits = -12;
                    break;
                case ScanSchirm.Karte:
                    Taste(Esc, 1200);
                    break;
                case ScanSchirm.Pause:
                    if (!DrueckeBis(Rechts, b => b.Schirm == ScanSchirm.OnlineReiter, 10, 900, "online-tab"))
                    {
                        throw new Abbruch("could not reach the ONLINE tab of the pause menu");
                    }
                    break;
                case ScanSchirm.OnlineReiter:
                    // NUR MIT GEPRUEFTEM RAHMEN. Am 2026-10-04 griff das Schweben nicht, ENTER oeffnete
                    // die markierte Kachel (Horizon Play), und der blinde Ersatzplan [RECHTS, RUNTER, ENTER]
                    // trug den Spieler dort in "The Eliminator" ein. Jetzt faellt ENTER nur, wenn der gelbe
                    // Rahmen die Kachel mit "Rivals" umschliesst; sonst Pfeiltasten auf sie zu.
                    if (++kachelVersuch > 3) { throw new Abbruch("could not put the frame on the Rivals tile of the ONLINE tab"); }
                    if (KachelWaehlen(RivalsKachel, "rivals-tile") && !WarteAuf(ScanSchirm.RivalsHub, 20, "rivals-hub"))
                    {
                        // Eine andere Kachel ging auf: nur ESC, nie weitere Tasten in einem fremden Menue
                        // (dort liegen Online-Veranstaltungen, die ENTER betritt).
                        Taste(Esc, 1500);
                    }
                    break;
                case ScanSchirm.Bestaetigung:
                    // ESC schliesst diese Dialoge nicht. "No" -- aber nur mit geprueftem Rahmen: "LINKS,
                    // dann ENTER" haette auf einem senkrechten Dialog "Yes" bestaetigt (ScanDialog).
                    ScanDialog.NeinWaehlen(_eingabe, _ocr, Melde);
                    break;
                case ScanSchirm.ForzaLink:
                    Taste(Esc, 1200);
                    break;
                case ScanSchirm.RivalsHub:
                    // NUR MIT GEPRUEFTEM RAHMEN, wie auf dem ONLINE-Reiter: vorher fiel hier ENTER nach
                    // einem blinden Tastenplan ([], [RECHTS], [RECHTS, RECHTS] ...) auf die Kachel, die gerade
                    // markiert war -- und die Plaene addierten sich von Versuch zu Versuch. Jetzt faellt
                    // ENTER nur, wenn der gelbe Rahmen "Horizon Rivals" umschliesst.
                    if (++hubVersuch > 3) { throw new Abbruch("could not put the frame on the Horizon Rivals tile of the Rivals hub"); }
                    if (!KachelWaehlen(HorizonRivalsKachel, "hub-tile", ScanSchirm.RivalsHub, Links))
                    {
                        Melde("Rivals hub: frame not verified on 'Horizon Rivals' -- no ENTER");
                    }
                    else if (!WarteAuf(ScanSchirm.Kategorien, 20, "categories"))
                    {
                        Melde("Rivals hub: no category screen yet; looking again");
                    }
                    break;
                case ScanSchirm.Kategorien:
                    switch (KategorieOeffnen(kat))
                    {
                        case KategorieWahl.Bestaetigt:
                            kategorieBestaetigt = true;
                            break;
                        case KategorieWahl.Falsch when ++kategorieFalsch >= 3:
                            throw new Abbruch($"no confirmed category '{kat.Name}' after 3 tries; refusing to scan the wrong category");
                    }
                    break;
                case ScanSchirm.Streckenliste:
                {
                    if (verlassen)
                    {
                        Melde("back on the route list");
                        verlassen = false;
                    }
                    // EINE LISTE, DIE DIESER LAUF NICHT SELBST GEOEFFNET HAT, gilt nur, wenn Titel
                    // und Kachelleiste sie in die Zielkategorie stellen (2026-09-24: ein Lauf fing
                    // auf der offenen Road-Racing-Liste des vorigen an und nahm sie fuer Cross-Country).
                    if (!kategorieBestaetigt)
                    {
                        var (wo, titel, beleg, _) = LeseStrecke(kat);
                        if (wo < 0)
                        {
                            Melde($"a route list this run did not open, not placed in {kat.Name} ({beleg}; '{titel}'); back to the categories");
                            Taste(Esc, 2000);
                            break;
                        }
                        Melde($"the open route list is {kat.Name} (route {wo} by {beleg})");
                        kategorieBestaetigt = true;
                    }
                    listeGesehen = true;
                    var (erreicht, gelesen) = StreckeWaehlen(kat, ziel.RouteIndex);
                    if (!erreicht) { break; }
                    strecke = gelesen;
                    // UNMITTELBAR VOR DEM ENTER noch einmal hinsehen: ging ein frueheres ENTER doch durch
                    // und der Klassenschirm kam erst jetzt (er laedt den Rivalen nach), traegt er Titel
                    // und Laenge derselben Strecke -- und ENTER dort startet ein Rennen.
                    using (var vorEnter = Sehen("route-enter"))
                    {
                        var (dort, dortBeleg) = kat.Verorte(vorEnter.Lesung);
                        if (vorEnter.Schirm != ScanSchirm.Streckenliste || dort != ziel.RouteIndex)
                        {
                            Melde($"before ENTER: {vorEnter.Schirm}, route {dort} ({dortBeleg}) -- no ENTER, looking again");
                            break;
                        }
                    }
                    Taste(Enter, 3000);
                    break;
                }
                case ScanSchirm.RivalDetail:
                    if (verlassen)
                    {
                        Taste(Esc, 1200);
                    }
                    else if (!listeGesehen)
                    {
                        // Ein Ueberbleibsel einer frueheren Navigation, womoeglich einer anderen
                        // Kategorie. Warten macht es nicht richtig: zur Streckenliste zurueck.
                        Melde("a rival card left over from earlier; backing out to the route list");
                        Taste(Esc, 1200);
                    }
                    else
                    {
                        // Der Klassenschirm mit unlesbarer Zeile. Keine Taste: nur neu lesen,
                        // gestreut, bis die Zeile durchkommt.
                        Thread.Sleep(2500);
                    }
                    break;
                case ScanSchirm.Klassenschirm:
                {
                    if (verlassen || !listeGesehen)
                    {
                        Melde(verlassen ? "backing out past the class strip" : "class screen of an earlier route; backing out");
                        Taste(Esc, 1200);
                        break;
                    }
                    // GEHOERT DIESER KLASSENSCHIRM ZUR ZIELSTRECKE? Titel und Laenge stehen auch
                    // hier. Ein Widerspruch heisst: das ENTER traf eine andere Strecke -- dann
                    // zurueck zur Liste, nie ein Board unter falschem Namen.
                    var (hier, beleg) = kat.Verorte(blick.Lesung);
                    var sollLaenge = kat.LaengeVon(ziel.RouteIndex);
                    var falsch = hier >= 0 && hier != ziel.RouteIndex
                                 || hier < 0 && blick.Lesung.Laenge is { } km && sollLaenge is { } soll && Math.Abs(km - soll) > 0.05;
                    if (falsch)
                    {
                        if (++falscheStrecke > 3) { throw new Abbruch($"the class screen keeps showing another route ({beleg})"); }
                        Melde($"class screen of the wrong route ({beleg}; title '{blick.Lesung.Titel}'); back to the route list");
                        Taste(Esc, 1500);
                        break;
                    }
                    switch (KlasseWaehlen(klasse, out klasseGesehen))
                    {
                        case Wahl.Gewaehlt:
                            // Erst ruhen lassen: beide verschluckten Y (live 2026-10-03) fielen in der
                            // ersten Sekunde nach dem Erscheinen des Schirms bzw. dem Klassenwechsel,
                            // waehrend das Details-Feld den Rivalen nachlaedt.
                            Thread.Sleep(2000);
                            Melde($"class {klasseGesehen} selected; opening the board with Y");
                            Taste(TasteY, 500);
                            yGedrueckt = true;
                            // Bis zu 8 s auf die Tabelle warten, bevor ein zweites Y faellt: das Spiel
                            // verschluckt manchmal den ersten Druck nach einem Schirmwechsel (live
                            // 2026-10-03), aber ein Y auf der schon offenen Tabelle oeffnet "Player options".
                            if (!WarteAuf(ScanSchirm.Bestenliste, 8, "board-open"))
                            {
                                Melde("the board did not open; looking again");
                            }
                            break;
                        case Wahl.Unlesbar:
                            Melde("class line became unreadable; waiting for a legible frame");
                            break;
                        default:
                            throw new Abbruch($"could not select class {klasse}");
                    }
                    break;
                }
                default:
                    Thread.Sleep(2000);
                    break;
            }
        }
        throw new Abbruch("did not reach the leaderboard within 200 looks");
    }

    private enum KategorieWahl { Bestaetigt, NochNicht, Falsch }

    /// <summary>
    /// Die Kategorie oeffnen und die geoeffnete Liste pruefen -- EIN Anlauf je Blick der
    /// Hauptschleife. Kein Rahmen oder noch keine Liste heisst "noch nicht": dann sieht die
    /// Schleife neu hin, statt mit ESC zu fliehen (im ersten Lauf am 2026-10-03 fuehrte genau
    /// dieses ESC zurueck in den Rivals-Hub, und der zweite Anlauf brach dort ab).
    /// Beweis ist die geoeffnete Liste: Titel und Kachelleiste muessen in die Kategorie passen;
    /// das Wahrzeichen zaehlt nur, wenn nichts auf eine andere Kategorie zeigt.
    /// </summary>
    private KategorieWahl KategorieOeffnen(ScanRoutes.Kategorie kat)
    {
        // Der Schirm baut sich auf; wer sofort drueckt, drueckt auf ein Bild, das das Menue noch nicht annimmt.
        Thread.Sleep(1500);
        Melde($"category: moving the frame to '{kat.Name}'");
        if (!RahmenAufKachel(kat.Name)) { return KategorieWahl.NochNicht; }
        if (!WarteAuf(ScanSchirm.Streckenliste, 20, "category-open"))
        {
            Melde("category: no route list yet; looking again");
            return KategorieWahl.NochNicht;
        }
        var (idx, titel, beleg, _) = LeseStrecke(kat);
        if (idx >= 0)
        {
            Melde($"category confirmed: route {idx} of {kat.Name} by {beleg} ('{titel}')");
            return KategorieWahl.Bestaetigt;
        }
        var fremd = _routen.Kategorien.FirstOrDefault(k => k != kat && k.Lokalisiere(titel) >= 0);
        if (fremd is null && SucheText(kat.Wahrzeichen, 8, "category-check") is { } wz)
        {
            Melde($"category confirmed by its landmark: '{wz.Text}'");
            return KategorieWahl.Bestaetigt;
        }
        Melde(fremd is not null
            ? $"category: the open list shows '{titel}', a {fremd.Name} route -- ESC and again"
            : $"category: the open list is not placed in {kat.Name} ({beleg}; '{titel}') -- ESC and again");
        Taste(Esc, 2000);
        return KategorieWahl.Falsch;
    }

    /// <summary>
    /// Den Rahmen Schritt fuer Schritt auf die Kachel bringen und ENTER druecken. true heisst
    /// nur: ENTER ging auf der richtigen Kachel raus. Ob sich die richtige Liste geoeffnet hat,
    /// entscheidet danach der Aufrufer.
    /// </summary>
    private bool RahmenAufKachel(string kategorie)
    {
        var ziel = ScanScreen.Kacheln.FirstOrDefault(k => string.Equals(k.Name, kategorie, StringComparison.OrdinalIgnoreCase));
        if (ziel.Name is null)
        {
            Melde($"category frame: '{kategorie}' is not one of the six tiles");
            return false;
        }
        for (var schritt = 0; schritt <= 8; schritt++)
        {
            using var b = Sehen($"category-frame-{schritt}");
            // Ein Rahmen allein beweist nichts: der Klassenschirm hat auch einen, an fast derselben Stelle.
            if (!ScanScreen.IstKategorieSchirm(b.Zeilen))
            {
                Melde($"category frame: not the category screen ({b.Schirm}) -- stopping");
                return false;
            }
            var zelle = ScanScreen.KategorieRahmen(b.Bild);
            if (zelle is null)
            {
                Melde("category frame: no highlight frame visible");
                return false;
            }
            var (zeile, spalte) = zelle.Value;
            if (zeile == ziel.Zeile && spalte == ziel.Spalte)
            {
                Melde($"category frame on '{kategorie}' after {schritt} step(s); ENTER");
                Taste(Enter, 300);
                return true;
            }
            // Immer nur EINE Taste, dann wieder hinsehen.
            var taste = spalte != ziel.Spalte ? (ziel.Spalte > spalte ? Rechts : Links) : (ziel.Zeile > zeile ? Runter : Hoch);
            Melde($"category frame on {zeile},{spalte}, target {ziel.Zeile},{ziel.Spalte} -> {Name(taste)}");
            Taste(taste, 1200);
        }
        Melde($"category frame: not on '{kategorie}' after 8 steps");
        return false;
    }

    /// <summary>
    /// Den Titel der gewaehlten Strecke lesen und im Karussell verorten -- erst, wenn zwei
    /// Lesungen hintereinander dieselbe Position ergeben: direkt nach einem Druck zeigt der
    /// Schirm noch die vorige Strecke, und eine Laufschrift aendert sich von Blick zu Blick.
    /// Nur auf der Streckenliste: der Klassenschirm zeigt Titel und Laenge derselben Strecke, und
    /// ENTER dort startet ein Rennen -- auf jedem anderen Schirm gibt es -1 und Liste = false.
    /// </summary>
    private (int Index, string? Titel, string Beleg, bool Liste) LeseStrecke(ScanRoutes.Kategorie kat)
    {
        var vorher = -1;
        string? titel = null;
        var beleg = "nothing read";
        for (var i = 0; i < 6; i++)
        {
            using var b = Sehen($"route-{i}");
            if (b.Schirm is ScanSchirm.Klassenschirm or ScanSchirm.RivalDetail or ScanSchirm.Bestenliste)
            {
                return (-1, b.Lesung.Titel ?? titel, $"not the route list ({b.Schirm})", false);
            }
            if (b.Schirm != ScanSchirm.Streckenliste)
            {
                // Ein Zwischenbild (Laden, unlesbar): zaehlt fuer keine Position.
                vorher = -1;
                beleg = $"not the route list ({b.Schirm})";
                Thread.Sleep(300);
                continue;
            }
            var (idx, wie) = kat.Verorte(b.Lesung);
            titel = b.Lesung.Titel ?? titel;
            beleg = wie;
            if (idx >= 0 && idx == vorher) { return (idx, titel, wie, true); }
            vorher = idx;
            Thread.Sleep(300);
        }
        return (-1, titel, beleg, true);
    }

    /// <summary>
    /// Das Karussell auf den Index stellen. Bestaetigte Schritte zaehlen, nicht Tastendruecke:
    /// nach jedem Druck wird neu verortet, denn Druecke gehen verloren (idx05, 12 und 15
    /// landeten am 2026-09-13 wiederholt eins zu kurz).
    /// </summary>
    /// <returns>Erreicht = false: der Schirm ist nicht mehr die Streckenliste -- dann keine Taste, die Hauptschleife sieht neu hin.</returns>
    private (bool Erreicht, string? Titel) StreckeWaehlen(ScanRoutes.Kategorie kat, int zielIndex)
    {
        var n = kat.Strecken.Count;
        var unbekannt = 0;
        for (var druck = 0; druck <= n + 4; druck++)
        {
            var (idx, titel, beleg, liste) = LeseStrecke(kat);
            if (!liste)
            {
                Melde($"route select: {beleg} -- no key");
                return (false, titel);
            }
            if (idx == zielIndex)
            {
                Melde($"route {zielIndex} '{kat.Strecken[zielIndex]}' confirmed by {beleg} ('{titel}') after {druck} press(es)");
                return (true, titel);
            }
            byte taste = Rechts;
            if (idx < 0)
            {
                if (++unbekannt > 8)
                {
                    throw new Abbruch($"the route titles match nothing in {kat.Name} (last read '{titel}'): wrong category, or the route table is out of date");
                }
                Melde($"route not placed ({beleg}; title '{titel}'); stepping on");
            }
            else
            {
                unbekannt = 0;
                var vor = ((zielIndex - idx) % n + n) % n;
                var zurueck = ((idx - zielIndex) % n + n) % n;
                // Knapp daneben (ein verlorener oder ein doppelter Druck): kurz zurueck statt einmal rundherum.
                if (zurueck <= 2 && zurueck < vor) { taste = Links; }
                Melde($"on route {idx} '{kat.Strecken[idx]}' by {beleg}, {(taste == Links ? zurueck : vor)} step(s) {(taste == Links ? "back" : "on")}");
            }
            Taste(taste, 450);
        }
        throw new Abbruch($"route index {zielIndex} of {kat.Name} not reached within {n + 5} presses");
    }

    private enum Wahl { Gewaehlt, Unlesbar, Fehlgeschlagen }

    /// <summary>
    /// Die Klasse auf der Leiste waehlen. Die Leiste ist waagerecht, nur ein Teil ist zu sehen
    /// (R rechts von S2) und sie oeffnet auf der zuletzt benutzten Klasse; die Kopfzeile nennt
    /// die gewaehlte, also wird gelesen und in die richtige Richtung gedrueckt. Die Kacheln
    /// taugen nicht: ihre Zahlen sind immer alle lesbar, sie zeigen nicht die Auswahl.
    /// </summary>
    private Wahl KlasseWaehlen(string klasse, out string? gesehen)
    {
        gesehen = null;
        var zielIdx = Array.IndexOf(ScanScreen.Klassen, klasse);
        for (var druck = 0; druck < 12; druck++)
        {
            string? jetzt = null;
            for (var i = 0; i < 6 && jetzt is null; i++)
            {
                using var b = Sehen($"class-{druck}-{i}");
                jetzt = b.Lesung.Klasse;
                if (jetzt is null) { Thread.Sleep(600); }
            }
            if (jetzt is null) { return Wahl.Unlesbar; }
            if (jetzt == klasse)
            {
                gesehen = jetzt;
                return Wahl.Gewaehlt;
            }
            var jetztIdx = Array.IndexOf(ScanScreen.Klassen, jetzt);
            var taste = jetztIdx < zielIdx ? Rechts : Links;
            Melde($"class {jetzt} on screen, want {klasse} -> {Name(taste)}");
            Taste(taste, 700);
        }
        return Wahl.Fehlgeschlagen;
    }
}
