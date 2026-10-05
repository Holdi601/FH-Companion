using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using ForzaHaptics.Tester.Rivals;

namespace ForzaHaptics.Tester.Auction;

/// <summary>
/// Der Reiter "Auction House": die eigenen Gebote mit Restzeit, die Warnungen und die
/// Preisgeschichte je Auto. Die App bietet nie selbst.
/// </summary>
/// <remarks>
/// Seit 2026-10-04. Gefuellt wird der Stand nicht hier, sondern vom <see cref="AuctionWatch"/>:
/// aus dem, was das Overlay im Auktionshaus sieht, und aus dem, was "Check my bids now" liest. Der
/// Reiter zeigt ihn und aendert nur die Einstellung der Warnungen -- ueber <see cref="AuctionWatch.Mit"/>,
/// damit Takt, Leser und Reiter nie gegeneinander schreiben.
///
/// DIE OBERFLAECHE BLEIBT FREI. Geaendert kommt auf einem Hintergrundfaden; hier wird nur ein
/// Abbild unter der Sperre kopiert (kleines JSON, keine Texterkennung) und per BeginInvoke gezeigt.
/// Die Restzeit zaehlt eine Uhr im Sekundentakt aus dem gemerkten Ende herunter.
/// </remarks>
internal sealed class AuctionTab : UserControl
{
    private static readonly Color Grund = Color.FromArgb(24, 26, 31);
    private static readonly Color Feld = Color.FromArgb(18, 20, 24);
    private static readonly Color Warnfarbe = Color.FromArgb(240, 180, 80);
    private static readonly Color Rot = Color.FromArgb(255, 107, 107);

    /// <summary>So viele Preise zeigt die Geschichte hoechstens (die neuesten); gespeichert sind bis zu 20 000.</summary>
    internal const int PreiseHoechstens = 3000;

    private const int SpalteRest = 5;

    private readonly Func<bool> _spielLaeuft;
    private readonly CheckBox _warnen = new();
    private readonly Label _status = new();
    private readonly Button _pruefen;
    private readonly Button _stop;
    private readonly DataGridView _tabelle;
    private readonly TextBox _filter = new();
    private readonly ListView _preise = new();
    private readonly FlowLayoutPanel _oben;
    private readonly System.Windows.Forms.Timer _uhr = new() { Interval = 1000 };
    private readonly Action _geaendert;

    // Bis der Konstruktor durch ist, schreibt kein Feld in den Stand zurueck.
    private bool _still = true;
    private int _neuAngefordert;
    private List<Zeile> _stand = new();
    private List<Preis> _preisStand = new();
    private (int Anzahl, Preis? Letzter, string Filter) _preiseGezeigt = (-1, null, string.Empty);
    private string _eigenerStatus = string.Empty;

    /// <summary>Was der Reiter von einer Auktion zeigt -- unter der Sperre kopiert.</summary>
    internal sealed record Zeile(string Id, string Auto, int? Pi, string? Status, long? Gebot, long? Sofortkauf,
                                 DateTime? EndeUtc, DateTime ZuletztGesehenUtc, string? Ergebnis,
                                 long? Endpreis, bool Laeuft);

    /// <param name="spielLaeuft">Ob das Spiel laeuft -- sonst faehrt "Check my bids now" nicht los.</param>
    public AuctionTab(Func<bool> spielLaeuft)
    {
        _spielLaeuft = spielLaeuft;
        BackColor = Grund;
        ForeColor = Color.WhiteSmoke;
        Dock = DockStyle.Fill;

        // OBEN: Einstellung, Knoepfe, Stand.
        _oben = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(8, 8, 8, 4),
        };

        _warnen.Text = Loc.T("Warn 5 and 2 minutes before the end");
        Haken(_warnen);
        _warnen.CheckedChanged += (_, _) =>
        {
            if (_still) { return; }
            var an = _warnen.Checked;
            AuctionWatch.Jetzt.Mit(s => s.Warnen = an);
        };
        _oben.Controls.Add(_warnen);

        var knoepfe = Reihe();
        _pruefen = Knopf(Loc.T("Check my bids now"));
        _pruefen.Click += (_, _) => Pruefen();
        _stop = Knopf(Loc.T("Stop"));
        _stop.Enabled = false;
        _stop.Click += (_, _) =>
        {
            AuctionWatch.Jetzt.StoppeBieter();
            Status(Loc.T("Stopping ..."));
        };
        var ordner = Knopf(Loc.T("Open pictures folder"));
        ordner.Click += (_, _) => OrdnerOeffnen();
        knoepfe.Controls.Add(_pruefen);
        knoepfe.Controls.Add(_stop);
        knoepfe.Controls.Add(ordner);
        _oben.Controls.Add(knoepfe);

        _status.AutoSize = true;
        _status.ForeColor = Color.Gray;
        _status.Margin = new Padding(8, 2, 3, 6);
        _oben.Controls.Add(_status);

        // MITTE: die verfolgten Auktionen. Nichts ist bearbeitbar.
        _tabelle = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
            ReadOnly = true,
            EditMode = DataGridViewEditMode.EditProgrammatically,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
            BackgroundColor = Feld,
            ForeColor = Color.WhiteSmoke,
            GridColor = Color.FromArgb(55, 60, 70),
            BorderStyle = BorderStyle.None,
            EnableHeadersVisualStyles = false,
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(43, 48, 58),
                ForeColor = Color.WhiteSmoke,
                SelectionBackColor = Color.FromArgb(43, 48, 58),
            },
            DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(27, 30, 36),
                ForeColor = Color.WhiteSmoke,
                SelectionBackColor = Color.FromArgb(50, 91, 150),
                SelectionForeColor = Color.White,
            },
        };
        foreach (var (text, breite, rechts) in new[]
                 {
                     (Loc.T("Car"), 260, false), (Loc.T("PI"), 50, true), (Loc.T("Status"), 100, false),
                     (Loc.T("Current bid"), 110, true), (Loc.T("Buyout"), 110, true), (Loc.T("Time left"), 130, true),
                     (Loc.T("Last seen"), 130, false), (Loc.T("Result"), 170, false),
                 })
        {
            var spalte = new DataGridViewTextBoxColumn
            {
                HeaderText = text,
                Width = Math.Max(breite, TextRenderer.MeasureText(text, Font).Width + 20),
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
            };
            if (rechts) { spalte.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight; }
            _tabelle.Columns.Add(spalte);
        }

        // UNTEN: die Preisgeschichte, je Auto gruppiert, neueste zuerst.
        var preisKopf = Reihe();
        preisKopf.Dock = DockStyle.Top;
        preisKopf.Padding = new Padding(4, 4, 4, 2);
        var preisTitel = Beschriftung(Loc.T("Price history"));
        preisTitel.ForeColor = Color.WhiteSmoke;
        preisTitel.Font = new Font(Font, FontStyle.Bold);
        preisKopf.Controls.Add(preisTitel);
        preisKopf.Controls.Add(Beschriftung(Loc.T("Filter by car")));
        _filter.Width = 260;
        _filter.BackColor = Feld;
        _filter.ForeColor = Color.WhiteSmoke;
        _filter.BorderStyle = BorderStyle.FixedSingle;
        _filter.TextChanged += (_, _) => PreiseZeigen();
        preisKopf.Controls.Add(_filter);

        _preise.Dock = DockStyle.Fill;
        _preise.View = View.Details;
        _preise.FullRowSelect = true;
        _preise.ShowGroups = true;
        _preise.BackColor = Feld;
        _preise.ForeColor = Color.WhiteSmoke;
        foreach (var (text, breite, rechts) in new[]
                 {
                     (Loc.T("Time"), 150, false), (Loc.T("PI"), 50, true), (Loc.T("Bid"), 110, true),
                     (Loc.T("Buyout"), 110, true), (Loc.T("Time left"), 90, true), (Loc.T("Source"), 110, false),
                     (Loc.T("Result"), 120, false),
                 })
        {
            var breiteKopf = TextRenderer.MeasureText(text, _preise.Font).Width + 18;
            _preise.Columns.Add(text, Math.Max(breite, breiteKopf), rechts ? HorizontalAlignment.Right : HorizontalAlignment.Left);
        }

        var auktionKopf = Beschriftung(Loc.T("My bids"));
        auktionKopf.Dock = DockStyle.Top;
        auktionKopf.ForeColor = Color.WhiteSmoke;
        auktionKopf.Font = new Font(Font, FontStyle.Bold);
        auktionKopf.Padding = new Padding(8, 6, 2, 4);
        var obenTeil = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4, 0, 4, 4) };
        obenTeil.Controls.Add(_tabelle);
        obenTeil.Controls.Add(auktionKopf);
        var untenTeil = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4, 0, 4, 4) };
        untenTeil.Controls.Add(_preise);
        untenTeil.Controls.Add(preisKopf);

        var raster = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0) };
        raster.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        raster.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        raster.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        raster.Controls.Add(obenTeil, 0, 0);
        raster.Controls.Add(untenTeil, 0, 1);

        Controls.Add(raster);
        Controls.Add(_oben);
        Resize += (_, _) => Umbruch();

        // Geaendert kommt auf einem Hintergrundfaden, oft mehrmals kurz hintereinander: gebuendelt.
        _geaendert = () =>
        {
            if (Interlocked.Exchange(ref _neuAngefordert, 1) == 1) { return; }
            // Kam der Auftrag nicht an (der Reiter war noch nie offen, also ohne Fenstergriff),
            // die Marke zuruecknehmen -- sonst bliebe sie stehen, und kein spaeterer Wechsel kaeme
            // je wieder durch. OnHandleCreated holt das Versaeumte nach.
            if (!Zurueck(() =>
                {
                    Volatile.Write(ref _neuAngefordert, 0);
                    Neu();
                }))
            {
                Volatile.Write(ref _neuAngefordert, 0);
            }
        };
        AuctionWatch.Jetzt.Geaendert += _geaendert;
        _uhr.Tick += (_, _) => Uhr();
        _uhr.Start();

        _still = false;
        Neu();
        Umbruch();
    }

    // ------------------------------------------------------------------ //
    // Rechnen (ohne Oberflaeche, im Grenzfalltest geprueft)
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Die Restzeit als "1:05:12" -- eine Schaetzung (die Restzeit des Spiels hat Minuten, unter fuenf
    /// Minuten gar nur "Ending Soon"), darum mit "~". Ohne Ende "?", vorbei "0:00".
    /// </summary>
    internal static string RestText(DateTime? endeUtc, DateTime jetztUtc)
    {
        if (endeUtc is not { } ende) { return "?"; }
        var rest = ende - jetztUtc;
        if (rest <= TimeSpan.Zero) { return "0:00"; }
        var stunden = (int)rest.TotalHours;
        return stunden > 0
            ? string.Create(CultureInfo.InvariantCulture, $"~{stunden}:{rest.Minutes:00}:{rest.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"~{rest.Minutes}:{rest.Seconds:00}");
    }

    /// <summary>
    /// Die Spalte "Time left" einer Auktion: laufend die Restzeit (<see cref="RestText"/>), vorbei
    /// "ended ~06:24" -- wann sie endete, so genau, wie die Schaetzung es weiss (an einem anderen Tag mit
    /// Wochentag: beendete Eintraege verschwinden nach drei Tagen); ohne Ende nur "ended". Vorher blieb die
    /// Spalte bei jeder beendeten Auktion leer.
    /// </summary>
    internal static string RestSpalte(bool laeuft, DateTime? endeUtc, DateTime jetztUtc)
    {
        if (laeuft) { return RestText(endeUtc, jetztUtc); }
        if (endeUtc is not { } ende) { return Loc.T("ended"); }
        var lokal = ende.ToLocalTime();
        // "t" ist nur ALLEIN die kurze Uhrzeit; in "ddd t" waere es der erste Buchstabe von AM/PM.
        var kultur = CultureInfo.CurrentCulture;
        var wann = lokal.Date == jetztUtc.ToLocalTime().Date
            ? lokal.ToString("t", kultur)
            : lokal.ToString("ddd", kultur) + " " + lokal.ToString("t", kultur);
        return string.Format(Loc.T("ended ~{0}"), wann);
    }

    /// <summary>
    /// Die Spalte "Result": das gesehene Ergebnis mit Endpreis; vorbei ohne gesehenes Ergebnis
    /// "outcome not seen" (das Spiel war zu, als sie endete); laufend leer.
    /// </summary>
    internal static string ErgebnisSpalte(bool laeuft, string? ergebnis, long? endpreis)
    {
        if (ergebnis is { } e) { return AuctionWatch.Status(e) + (endpreis is { } p ? " · " + Cr(p) + " CR" : string.Empty); }
        return laeuft ? string.Empty : Loc.T("outcome not seen");
    }

    /// <summary>
    /// Die Preisgeschichte je Auto: Gruppen nach gefaltetem Namen (Gross/Klein, Satzzeichen gleich),
    /// die Gruppe mit dem juengsten Preis zuerst, darin neueste zuerst. Der Filter sucht im gefalteten
    /// Namen; hoechstens <paramref name="hoechstens"/> Preise, die neuesten.
    /// </summary>
    internal static List<(string Auto, List<Preis> Preise)> PreiseJeAuto(IEnumerable<Preis> preise, string? filter, int hoechstens)
    {
        var such = Scan.ScanScreen.Falte(filter);
        return preise.Where(p => !string.IsNullOrWhiteSpace(p.Auto)
                                 && (such.Length == 0 || Scan.ScanScreen.Falte(p.Auto).Contains(such, StringComparison.Ordinal)))
                     .OrderByDescending(p => p.ZeitUtc)
                     .Take(Math.Max(0, hoechstens))
                     .GroupBy(p => Scan.ScanScreen.Falte(p.Auto))
                     .Select(g => (g.First().Auto, g.ToList()))
                     .ToList();
    }

    /// <summary>Erst die laufenden (naechstes Ende zuerst), dann die beendeten (zuletzt gesehen zuerst).</summary>
    internal static List<Zeile> Ordnen(IEnumerable<Zeile> zeilen) =>
        zeilen.OrderByDescending(z => z.Laeuft)
              .ThenBy(z => z.Laeuft ? z.EndeUtc ?? DateTime.MaxValue : DateTime.MaxValue)
              .ThenByDescending(z => z.ZuletztGesehenUtc)
              .ToList();

    internal static string Quelle(string quelle) => quelle switch
    {
        "my_bids" => Loc.T("My bids"),
        "search" => Loc.T("Search"),
        _ => quelle,
    };

    private static string Cr(long? betrag) => betrag is { } b ? b.ToString("n0", CultureInfo.CurrentCulture) : string.Empty;

    // ------------------------------------------------------------------ //
    // Zeigen
    // ------------------------------------------------------------------ //

    /// <summary>Den Stand unter der Sperre abbilden und alles neu zeigen.</summary>
    private void Neu()
    {
        if (IsDisposed) { return; }
        var (zeilen, preise, warnen) = AuctionWatch.Jetzt.Lies(s => (
            s.Auktionen.Select(a => new Zeile(a.Id, a.Auto, a.Pi, a.Status, a.Gebot, a.Sofortkauf, a.EndeUtc,
                                              a.ZuletztGesehenUtc, a.Ergebnis, a.Endpreis, a.Laeuft)).ToList(),
            s.Preise.ToList(), s.Warnen));
        // Aeltere Eintraege tragen noch den rohen OCR-Namen ("CHALLENGER RIT"): beim Zeigen
        // berichtigen, wie es das Lesen inzwischen gleich tut.
        _stand = Ordnen(zeilen.Select(z => z with { Auto = Name(z.Auto) }).ToList());
        _preisStand = preise.Select(p => p with { Auto = Name(p.Auto) }).ToList();
        _still = true;
        try
        {
            _warnen.Checked = warnen;
        }
        finally { _still = false; }
        TabelleZeigen();
        PreiseZeigen();
        Uhr();
    }

    private readonly Dictionary<string, string> _namen = new(StringComparer.Ordinal);

    private string Name(string gelesen)
    {
        if (string.IsNullOrWhiteSpace(gelesen)) { return gelesen; }
        if (!_namen.TryGetValue(gelesen, out var name))
        {
            name = AuctionNames.Berichtige(gelesen) ?? gelesen;
            _namen[gelesen] = name;
        }
        return name;
    }

    private void TabelleZeigen()
    {
        var ids = _tabelle.Rows.Cast<DataGridViewRow>().Select(r => r.Tag as string).ToList();
        if (!ids.SequenceEqual(_stand.Select(z => (string?)z.Id)))
        {
            var gewaehlt = _tabelle.CurrentCell is { } c ? (_tabelle.Rows[c.RowIndex].Tag as string, c.ColumnIndex) : (null, 0);
            _tabelle.Rows.Clear();
            foreach (var z in _stand)
            {
                var i = _tabelle.Rows.Add();
                _tabelle.Rows[i].Tag = z.Id;
            }
            if (gewaehlt.Item1 is { } id && _stand.FindIndex(z => z.Id == id) is var neu and >= 0)
            {
                try { _tabelle.CurrentCell = _tabelle.Rows[neu].Cells[gewaehlt.ColumnIndex]; } catch (Exception) { }
            }
        }
        var jetzt = DateTime.UtcNow;
        for (var i = 0; i < _stand.Count; i++)
        {
            var z = _stand[i];
            var r = _tabelle.Rows[i];
            Setze(r, 0, z.Auto);
            Setze(r, 1, z.Pi?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            Setze(r, 2, AuctionWatch.Status(z.Status));
            Setze(r, 3, Cr(z.Gebot));
            Setze(r, 4, Cr(z.Sofortkauf));
            Setze(r, SpalteRest, RestSpalte(z.Laeuft, z.EndeUtc, jetzt));
            Setze(r, 6, z.ZuletztGesehenUtc == default ? string.Empty : z.ZuletztGesehenUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
            Setze(r, 7, ErgebnisSpalte(z.Laeuft, z.Ergebnis, z.Endpreis));
            // Ueberboten und Ende in Sicht: rot. Beendet: grau.
            var farbe = !z.Laeuft ? Color.Gray
                : z.Status == "OUTBID" ? Rot
                : z.Status == "WINNING" ? Color.FromArgb(120, 220, 140)
                : Color.WhiteSmoke;
            if (r.DefaultCellStyle.ForeColor != farbe) { r.DefaultCellStyle.ForeColor = farbe; }
        }
    }

    private static void Setze(DataGridViewRow r, int spalte, string wert)
    {
        if (!Equals(r.Cells[spalte].Value as string, wert)) { r.Cells[spalte].Value = wert; }
    }

    private void PreiseZeigen()
    {
        var filter = _filter.Text.Trim();
        var signatur = (_preisStand.Count, _preisStand.Count > 0 ? _preisStand[^1] : null, filter);
        if (signatur == _preiseGezeigt) { return; }
        _preiseGezeigt = signatur;
        var gruppen = PreiseJeAuto(_preisStand, filter, PreiseHoechstens);
        _preise.BeginUpdate();
        try
        {
            _preise.Items.Clear();
            _preise.Groups.Clear();
            foreach (var (auto, liste) in gruppen)
            {
                var gruppe = new ListViewGroup($"{auto} ({liste.Count})");
                _preise.Groups.Add(gruppe);
                foreach (var p in liste)
                {
                    var eintrag = new ListViewItem(p.ZeitUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), gruppe);
                    eintrag.SubItems.Add(p.Pi?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
                    eintrag.SubItems.Add(Cr(p.Gebot));
                    eintrag.SubItems.Add(Cr(p.Sofortkauf));
                    eintrag.SubItems.Add(p.RestMinuten is { } m and > 0
                        ? string.Create(CultureInfo.InvariantCulture, $"{m / 60}:{m % 60:00}")
                        : string.Empty);
                    eintrag.SubItems.Add(Quelle(p.Quelle));
                    eintrag.SubItems.Add(p.Ergebnis is { } e ? AuctionWatch.Status(e) : string.Empty);
                    if (p.Ergebnis is not null) { eintrag.ForeColor = Warnfarbe; }
                    _preise.Items.Add(eintrag);
                }
            }
        }
        finally
        {
            _preise.EndUpdate();
        }
    }

    /// <summary>Jede Sekunde: Restzeiten, Stand des Lesers, Knoepfe.</summary>
    private void Uhr()
    {
        if (IsDisposed) { return; }
        var jetzt = DateTime.UtcNow;
        for (var i = 0; i < _stand.Count && i < _tabelle.Rows.Count; i++)
        {
            Setze(_tabelle.Rows[i], SpalteRest, RestSpalte(_stand[i].Laeuft, _stand[i].EndeUtc, jetzt));
        }
        var wache = AuctionWatch.Jetzt;
        var laeuft = wache.BieterLaeuft;
        _stop.Enabled = laeuft;
        _pruefen.Enabled = !laeuft;
        var bieter = wache.BieterStatus;
        var text = laeuft || (bieter.Length > 0 && _eigenerStatus.Length == 0) ? bieter : _eigenerStatus;
        if (_status.Text != text) { Status(text); }
    }

    // ------------------------------------------------------------------ //
    // Knoepfe
    // ------------------------------------------------------------------ //

    private void Pruefen()
    {
        if (!_spielLaeuft())
        {
            _eigenerStatus = Loc.T("The game is not running. Start it first; then the app drives to the auction house and reads My Bids.");
            Status(_eigenerStatus);
            return;
        }
        _eigenerStatus = string.Empty;
        if (!AuctionWatch.Jetzt.StarteBieter())
        {
            _eigenerStatus = Loc.T("The bidder is already running.");
        }
        Uhr();
    }

    private void OrdnerOeffnen()
    {
        try
        {
            var ordner = OverlayController.AuktionsbildOrdner;
            Directory.CreateDirectory(ordner);
            Process.Start(new ProcessStartInfo(ordner) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            _eigenerStatus = e.Message;
            Status(e.Message);
        }
    }

    // ------------------------------------------------------------------ //
    // Oberflaeche
    // ------------------------------------------------------------------ //

    private bool Zurueck(Action a)
    {
        try
        {
            if (IsHandleCreated && !IsDisposed) { BeginInvoke(a); return true; }
        }
        catch (Exception)
        {
        }
        return false;
    }

    /// <summary>
    /// Ein Reiter bekommt seinen Fenstergriff erst, wenn er zum ersten Mal gezeigt wird; was die Wache
    /// bis dahin meldete, kam nicht an. Darum hier einmal frisch abbilden.
    /// </summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Zurueck(Neu);
    }

    private void Status(string text)
    {
        _status.Text = text;
        Umbruch();
    }

    private void Umbruch()
    {
        var breite = Math.Max(240, ClientSize.Width - 40);
        _status.MaximumSize = new Size(breite, 0);
    }

    private static void Haken(CheckBox cb)
    {
        cb.AutoSize = true;
        cb.ForeColor = Color.WhiteSmoke;
        cb.Margin = new Padding(3, 3, 3, 2);
    }

    private static FlowLayoutPanel Reihe() => new()
    {
        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0),
    };

    private static Label Beschriftung(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = Color.Gray, Padding = new Padding(4, 6, 2, 0),
    };

    private static Button Knopf(string text) => new()
    {
        Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat,
        BackColor = Feld, ForeColor = Color.WhiteSmoke, Margin = new Padding(3),
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { AuctionWatch.Jetzt.Geaendert -= _geaendert; } catch (Exception) { }
            _uhr.Stop();
            _uhr.Dispose();
        }
        base.Dispose(disposing);
    }
}
