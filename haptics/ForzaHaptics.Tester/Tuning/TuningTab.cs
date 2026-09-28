using System.Text;
using ForzaHaptics.Tester.Rivals;

namespace ForzaHaptics.Tester.Tuning;

/// <summary>
/// Der Tuning-Inspektor: woraus der Tune auf dem gerade gefahrenen Auto besteht.
/// </summary>
/// <remarks>
/// Die Telemetrie nennt das Auto (`CarOrdinal`), sagt aber kein Wort darueber, was
/// daran gemacht wurde. Das steht in der Garagen-Datenbank des Spiels, und die liegt
/// nur im Arbeitsspeicher -- <see cref="ForzaMemoryDb"/> holt sie heraus,
/// <see cref="GarageReader"/> liest sie.
///
/// ## Warum ein Knopf und kein Dauerbetrieb
///
/// Den Speicher eines fremden Prozesses abzusuchen kostet Sekunden und liest
/// Hunderte Megabyte. Das nebenher laufen zu lassen, waehrend die App Haptik
/// ausgeben und ein Overlay zeichnen soll, waere respektlos gegenueber dem Spiel.
/// Ein Tune aendert sich ausserdem nur, wenn jemand ihn aendert -- einmal lesen
/// genuegt, und der Knopf sagt, wann.
///
/// ## Was der Reiter ehrlich NICHT kann
///
/// Teile stehen als Nummern da. Das Woerterbuch, das daraus "Rennsport-Nockenwelle"
/// machen wuerde, liegt im Speicher nur als Seiten-Zwischenspeicher und kommt leer
/// heraus. Und die Tuning-Regler stehen als Position 0 bis 1, nicht in BAR oder
/// Grad. Beides steht so auf dem Schirm, statt eine Genauigkeit vorzutaeuschen, die
/// die Quelle nicht hergibt.
/// </remarks>
internal sealed class TuningTab : UserControl
{
    private static readonly Color Ink = Color.Gainsboro;
    private static readonly Color Dim = Color.FromArgb(150, 150, 150);

    private readonly Func<ForzaPacket?> _telemetrie;
    private readonly Label _status = new();
    private readonly Label _auto = new();
    private readonly Button _lesen = new();
    private readonly Button _kopieren = new();
    private readonly ComboBox _wagen = new();
    private readonly ListView _teile = new();
    private readonly ListView _regler = new();

    private string? _garageDb;
    private List<int> _garageAutos = new();
    private CarTune? _aktuell;
    private Dictionary<long, string>? _autonamen;

    public TuningTab(Func<ForzaPacket?> telemetrie)
    {
        _telemetrie = telemetrie;
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(32, 32, 32);
        Padding = new Padding(16, 12, 16, 12);
        AutoScroll = true;

        var stapel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = BackColor,
        };

        stapel.Controls.Add(Kopf("Tuning inspector", 15));
        stapel.Controls.Add(Notiz(
            "Forza's telemetry names the car but says nothing about what was done to "
            + "it. That lives in the game's own garage database -- which exists only "
            + "in memory. This reads it out and lists every part and every tuning "
            + "slider on the car you are driving."));

        _status.AutoSize = true;
        _status.ForeColor = Dim;
        _status.Margin = new Padding(0, 8, 0, 0);
        stapel.Controls.Add(_status);

        _auto.AutoSize = true;
        _auto.ForeColor = Ink;
        _auto.Font = new Font("Consolas", 10f);
        _auto.Margin = new Padding(0, 4, 0, 6);
        stapel.Controls.Add(_auto);

        _lesen.Text = Loc.T("Read the running game");
        _lesen.AutoSize = true;
        _lesen.Click += (_, _) => Lesen();
        _kopieren.Text = Loc.T("Copy as text");
        _kopieren.AutoSize = true;
        _kopieren.Enabled = false;
        _kopieren.Click += (_, _) => Kopieren();
        _wagen.Width = 380;
        _wagen.DropDownStyle = ComboBoxStyle.DropDownList;
        _wagen.SelectedIndexChanged += (_, _) => WagenGewaehlt();

        var reihe = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 8),
        };
        reihe.Controls.Add(_lesen);
        reihe.Controls.Add(_kopieren);
        reihe.Controls.Add(_wagen);
        stapel.Controls.Add(reihe);

        stapel.Controls.Add(Kopf("Parts", 11));
        Spalten(_teile, ("Area", 110), ("Part", 185), ("Number", 80), ("Step", 50),
                ("What it is", 330));
        _teile.Height = 300;
        _teile.Width = 760;
        stapel.Controls.Add(_teile);
        stapel.Controls.Add(Notiz(
            "Step 0 is the stock part. A part number carries the car it belongs to in "
            + "its leading digits: when those are not this car's, the part comes from "
            + "somewhere else -- either a catalogue many cars share (clutch, gearbox, "
            + "driveline, differential) or, for the engine's internals, from another "
            + "car entirely. That is an engine swap, and it is named."));

        stapel.Controls.Add(Kopf("Tuning sliders", 11));
        stapel.Controls.Add(Notiz(
            "Slider position, 0 to 1 -- not the number the game shows you. The screen "
            + "says 2.1 BAR where the database says 0.4; the display value simply is "
            + "not stored. Turning it back would need every field calibrated by hand "
            + "(slider to each end, value read off)."));
        Spalten(_regler, ("Area", 110), ("Setting", 190), ("Slider", 90), ("", 300));
        _regler.Height = 300;
        _regler.Width = 720;
        stapel.Controls.Add(_regler);

        Controls.Add(stapel);

        // Erst wenn es ein Fensterhandle gibt: ein BeginInvoke im Konstruktor hat
        // diese App am 2026-08-27 am Starten gehindert, waehrend jeder Test gruen war.
        HandleCreated += (_, _) => StatusZeigen();
    }

    private static Label Kopf(string text, float groesse) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Color.White,
        Font = new Font("Segoe UI Semibold", groesse),
        Margin = new Padding(0, 12, 0, 4),
    };

    private static Label Notiz(string text) => new()
    {
        Text = text,
        ForeColor = Dim,
        MaximumSize = new Size(700, 0),
        AutoSize = true,
        Margin = new Padding(0, 0, 0, 4),
    };

    private static void Spalten(ListView liste, params (string Kopf, int Breite)[] spalten)
    {
        liste.View = View.Details;
        liste.FullRowSelect = true;
        liste.GridLines = false;
        liste.BackColor = Color.FromArgb(24, 24, 24);
        liste.ForeColor = Ink;
        liste.Font = new Font("Consolas", 9f);
        liste.BorderStyle = BorderStyle.FixedSingle;
        foreach (var (kopf, breite) in spalten) { liste.Columns.Add(kopf, breite); }
    }

    public void StatusZeigen()
    {
        var laeuft = ForzaMemoryDb.GameRunning;
        var paket = _telemetrie();
        var ordinal = paket is null ? null : (int?)paket.Get("CarOrdinal");
        _status.Text = laeuft
            ? "forzahorizon6.exe is running."
            : "forzahorizon6.exe is not running -- start the game, then read.";
        _auto.Text = ordinal is > 0
            ? $"telemetry says you are in car {ordinal}"
            : "no car in the telemetry yet";
        _lesen.Enabled = laeuft;
    }

    private void Lesen()
    {
        _lesen.Enabled = false;
        _lesen.Text = Loc.T("reading ...");
        _status.Text = Loc.T("searching the game's memory for its database ...");
        var ablage = Path.Combine(AppInfo.TempFolder, "garage");

        Task.Run(() =>
        {
            var meldungen = new List<string>();
            List<ForzaMemoryDb.Fund> funde;
            string? fruehGefunden = null;
            try
            {
                // Aufhoeren, sobald eine brauchbare Garage dasteht: Forza belegt
                // ueber vierzehn Gigabyte, und die Garage liegt oft lange vor dem
                // Ende. Weitersuchen kostet Minuten und bringt dasselbe Ergebnis.
                funde = ForzaMemoryDb.Dump(
                    ablage,
                    m => { meldungen.Add(m); BeiUns(() => _status.Text = m); },
                    pfad =>
                    {
                        if (GarageReader.FindGarage(new[] { pfad }) is null) { return false; }
                        fruehGefunden = pfad;
                        return true;
                    });
            }
            catch (Exception fehler)
            {
                funde = new List<ForzaMemoryDb.Fund>();
                meldungen.Add(fehler.Message);
            }
            var garage = fruehGefunden
                         ?? GarageReader.FindGarage(funde.Select(f => f.Path));
            List<int> autos = new();
            if (garage is not null)
            {
                try { autos = GarageReader.Cars(garage); } catch (Exception) { }
            }
            var text = garage is null
                ? (meldungen.Count > 0 ? string.Join(" ", meldungen)
                   : "no garage database found in the game's memory.")
                : $"garage read: {autos.Count} car(s).";
            BeiUns(() => Fertig(garage, autos, text));
        });
    }

    /// <summary>
    /// Eine Garage aus einem alten Abzug zeigen, als waere sie eben gelesen --
    /// nur fuer das Vorschaubild; das Spiel muss dafuer nicht laufen.
    /// </summary>
    internal void Vorschau(string garage, int carId)
    {
        var autos = GarageReader.Cars(garage);
        Fertig(garage, autos, $"garage read: {autos.Count} car(s).");
        var stelle = autos.IndexOf(carId);
        if (stelle >= 0) { _wagen.SelectedIndex = stelle; }
    }

    private void BeiUns(Action was)
    {
        if (IsHandleCreated && InvokeRequired) { BeginInvoke(was); }
        else { was(); }
    }

    private void Fertig(string? garage, List<int> autos, string meldung)
    {
        _garageDb = garage;
        _garageAutos = autos;
        _status.Text = meldung;
        _lesen.Text = Loc.T("Read the running game");
        _lesen.Enabled = true;

        _wagen.Items.Clear();
        foreach (var a in autos) { _wagen.Items.Add(Bezeichnung(a)); }

        var paket = _telemetrie();
        var ordinal = paket is null ? 0 : (int)paket.Get("CarOrdinal");
        var stelle = autos.IndexOf(ordinal);
        if (stelle >= 0)
        {
            _wagen.SelectedIndex = stelle;
        }
        else if (autos.Count > 0)
        {
            _wagen.SelectedIndex = 0;
            if (ordinal > 0)
            {
                _status.Text += $" Car {ordinal} from the telemetry is not in this "
                                + "garage -- it was bought after the database was "
                                + "last written.";
            }
        }
    }

    /// <summary>
    /// Auto-Nummer zu Name -- aus demselben Datensatz, den die Seite benutzt.
    /// </summary>
    /// <remarks>
    /// Damit aus "foreign family 1022" ein "engine swap: Ferrari 430 Scuderia '07"
    /// wird. Die Nummern liegen im selben Namensraum: die Garage, die Telemetrie
    /// und der Datensatz meinen mit 1655 alle denselben Subaru BRZ '13.
    /// </remarks>
    private string? AutoName(long nummer)
    {
        if (_autonamen is null)
        {
            _autonamen = new Dictionary<long, string>();
            try
            {
                var pfad = File.Exists(DatasetSync.CachePath)
                    ? DatasetSync.CachePath
                    : RivalsDataset.FindDefaultPath();
                if (pfad is not null)
                {
                    var d = RivalsDataset.Load(pfad);
                    for (var i = 0; i < d.CarIds.Count && i < d.CarNames.Count; i++)
                    {
                        _autonamen[d.CarIds[i]] = d.CarNames[i];
                    }
                }
            }
            catch (Exception)
            {
                // Ohne Namen bleibt die Nummer stehen -- das ist schlechter, aber
                // kein Grund, den ganzen Reiter scheitern zu lassen.
            }
        }
        if (_autonamen.TryGetValue(nummer, out var name)) { return name; }
        // AUCH DIE EIGENE GARAGE IST EINE QUELLE. Der Datensatz kennt 653 von rund
        // 709 Autos; 72 der 512 fremden Familien sind Autos, die hier in der Garage
        // stehen, aber dort keinen Namen haben. Sie als "Universalteil" auszugeben
        // waere falsch -- es ist ein Motortausch, nur ohne Namen.
        return _garageAutos.Contains((int)nummer) ? $"car {nummer}" : null;
    }

    /// <summary>"Audi R8 Coupe V10 plus 5.2 FSI quattro '13 (2010)" -- die Nummer allein sagt niemandem etwas.</summary>
    private string Bezeichnung(int carId)
    {
        var name = AutoName(carId);
        return name is null || name == $"car {carId}" ? $"car {carId}" : $"{name} ({carId})";
    }

    private void WagenGewaehlt()
    {
        if (_garageDb is null || _wagen.SelectedIndex < 0
            || _wagen.SelectedIndex >= _garageAutos.Count)
        {
            return;
        }
        var carId = _garageAutos[_wagen.SelectedIndex];
        try
        {
            _aktuell = GarageReader.Read(_garageDb, carId, AutoName);
        }
        catch (Exception fehler)
        {
            _status.Text = Loc.T("the garage row could not be read: ") + fehler.Message;
            return;
        }
        Zeigen(_aktuell);
    }

    private void Zeigen(CarTune? tune)
    {
        _teile.Items.Clear();
        _regler.Items.Clear();
        _kopieren.Enabled = tune is not null;
        if (tune is null) { return; }

        // DER PI KOMMT AUS DER TELEMETRIE, nicht aus der Garage: deren Spalte
        // `PerformanceIndex` widerspricht bei 128 von 569 Autos der eigenen
        // Klassenangabe. Die Klasse dagegen ist belegt und kommt aus der Garage.
        var paket = _telemetrie();
        var pi = paket is null ? 0 : (int)paket.Get("CarPerformanceIndex");
        var gefahren = paket is not null && (int)paket.Get("CarOrdinal") == tune.CarId;
        _auto.Text =
            $"{Bezeichnung(tune.CarId)}   class {tune.ClassName}   "
            + (gefahren && pi > 0 ? $"PI {pi} (from telemetry)   " : string.Empty)
            + $"parts bought for {tune.PartsValue:N0} CR"
            + (string.IsNullOrWhiteSpace(tune.TuneFileName)
                ? "   (no shared tune)"
                : $"   tune \"{tune.TuneFileName}\"");

        foreach (var t in tune.Parts)
        {
            var hinweis = new List<string> { t.Erklaerung };
            if (t.PricePaid is { } preis) { hinweis.Add($"bought for {preis:N0} CR"); }
            var zeile = new ListViewItem(new[]
            {
                t.Area, t.Label, t.Id.ToString(), t.Step.ToString(),
                string.Join(" -- ", hinweis),
            });
            if (t.Origin == "own" && t.Step == 0) { zeile.ForeColor = Dim; }
            _teile.Items.Add(zeile);
        }

        foreach (var s in tune.Settings)
        {
            // -1 heisst "an diesem Auto nicht vorhanden" -- der achte Gang eines
            // Siebengang-Getriebes etwa. Als Regler auf Anschlag links waere das
            // gelogen.
            var fehlt = s.Slider < 0;
            var zeile = new ListViewItem(new[]
            {
                s.Area, s.Label,
                fehlt ? "--" : s.Slider.ToString("0.000"),
                fehlt ? "not on this car" : Balken(s.Slider),
            });
            if (fehlt) { zeile.ForeColor = Dim; }
            _regler.Items.Add(zeile);
        }
    }

    /// <summary>Ein Balken aus Zeichen -- eine Zahl allein sagt nichts ueber "weit".</summary>
    private static string Balken(double anteil)
    {
        var n = (int)Math.Round(Math.Clamp(anteil, 0, 1) * 30);
        return new string('#', n) + new string('.', 30 - n);
    }

    private void Kopieren()
    {
        if (_aktuell is null) { return; }
        var b = new StringBuilder();
        b.AppendLine($"Car {_aktuell.CarId} -- class {_aktuell.ClassName}");
        if (!string.IsNullOrWhiteSpace(_aktuell.TuneFileName))
        {
            b.AppendLine($"Tune: {_aktuell.TuneFileName} "
                         + $"(id {_aktuell.VersionedTuneId}, "
                         + $"by XUID {_aktuell.VersionedTuneXuid})");
        }
        b.AppendLine($"Parts bought for {_aktuell.PartsValue:N0} CR");
        b.AppendLine();
        b.AppendLine("PARTS  (step 0 = stock; the game's part names are not in the data)");
        foreach (var t in _aktuell.Parts)
        {
            b.AppendLine($"  {t.Area,-14} {t.Label,-28} {t.Id,-9} step {t.Step,-4}"
                         + $" {t.Erklaerung}"
                         + (t.PricePaid is { } p ? $", bought {p:N0} CR" : string.Empty));
        }
        b.AppendLine();
        b.AppendLine("TUNING  (slider position 0..1, not the displayed unit)");
        foreach (var s in _aktuell.Settings)
        {
            b.AppendLine($"  {s.Area,-14} {s.Label,-28} {s.Slider:0.000}");
        }
        try { Clipboard.SetText(b.ToString()); _status.Text = Loc.T("copied to the clipboard."); }
        catch (Exception) { _status.Text = Loc.T("the clipboard refused the text."); }
    }
}
