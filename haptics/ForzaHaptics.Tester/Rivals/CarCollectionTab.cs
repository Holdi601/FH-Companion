using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Der Reiter "Car collection": welche Autos fehlen noch, und wie kommt man an sie.
/// </summary>
/// <remarks>
/// Die Liste aller Autos kommt vom eigenen Server (<see cref="CarCollection"/>), der
/// Besitz aus <see cref="OwnedCars"/>: am PC aus der Garage im Spielspeicher, auf der
/// Xbox aus den gefahrenen Autos und aus Haken von Hand -- dort gibt es keinen
/// Speicher, den die App lesen koennte.
///
/// Der Haken in jeder Zeile IST der Besitz. Wer ihn setzt oder entfernt, ueberstimmt
/// die Garage fuer dieses eine Auto; faellt er mit ihr zusammen, wird er vergessen.
/// </remarks>
internal sealed class CarCollectionTab : UserControl
{
    private readonly Func<string?> _server;
    private readonly Func<CarNotes?> _notizen;
    private readonly Func<RivalsAdvisor?> _rat;
    private readonly bool _konsole;

    private readonly Label _kopf = new();
    private readonly Label _quelle = new();
    private readonly Label _stand = new();
    private readonly Button _garage = new();
    private readonly Button _neuHolen = new();
    private readonly TextBox _filter = new();
    private readonly ComboBox _zeige = new();
    private readonly ComboBox _weg = new();
    private readonly ComboBox _klasse = new();
    private readonly ListView _liste = new();
    private readonly Label _detailKopf = new();
    private readonly Label _detail = new();
    private readonly LinkLabel _wiki = new();

    private CarCollection? _sammlung;
    private OwnedCars _besitz = OwnedCars.Laden();
    private HashSet<int> _gefahren = new();
    private bool _geladen;
    private bool _still;
    private int _sortSpalte;
    private bool _sortAb;

    private static readonly string[] Arten =
        { "autoshow", "playlist", "wheelspin", "aftermarket", "journal", "barn", "treasure", "mastery",
          "campaign", "gift", "loyalty", "dlc", "auction" };

    private static readonly string[] Klassen = { "D", "C", "B", "A", "S1", "S2", "R", "X" };

    public CarCollectionTab(Func<string?> server, Func<CarNotes?> notizen, Func<RivalsAdvisor?> rat, bool konsole)
    {
        _server = server;
        _notizen = notizen;
        _rat = rat;
        _konsole = konsole;
        BackColor = Color.FromArgb(24, 26, 31);
        ForeColor = Color.WhiteSmoke;
        Dock = DockStyle.Fill;

        var oben = new Panel { Dock = DockStyle.Top, Height = 118, Padding = new Padding(12, 8, 12, 0) };
        _kopf.SetBounds(12, 8, 900, 28);
        _kopf.Font = new Font("Segoe UI Semibold", 13f);
        _kopf.ForeColor = Color.FromArgb(127, 211, 255);
        _quelle.SetBounds(12, 38, 1100, 20);
        _quelle.ForeColor = Color.Gainsboro;

        _garage.Text = Loc.T("Read my garage");
        _garage.AutoSize = true;
        _garage.FlatStyle = FlatStyle.Flat;
        _garage.Location = new Point(12, 62);
        _garage.Visible = !_konsole;
        _garage.Click += (_, _) => GarageLesen();
        _neuHolen.Text = Loc.T("Check for a newer list");
        _neuHolen.AutoSize = true;
        _neuHolen.FlatStyle = FlatStyle.Flat;
        _neuHolen.Location = new Point(_konsole ? 12 : 170, 62);
        _neuHolen.Click += (_, _) => VomServer(zeigen: true);
        _stand.SetBounds(12, 94, 1100, 20);
        _stand.ForeColor = Color.Gray;
        oben.Controls.AddRange(new Control[] { _kopf, _quelle, _garage, _neuHolen, _stand });

        var filter = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(10, 4, 10, 0),
                                           WrapContents = false };
        _filter.Width = 260;
        _filter.PlaceholderText = Loc.T("Filter by name");
        _filter.BackColor = Color.FromArgb(18, 20, 24);
        _filter.ForeColor = Color.WhiteSmoke;
        foreach (var box in new[] { _zeige, _weg, _klasse })
        {
            box.DropDownStyle = ComboBoxStyle.DropDownList;
            box.BackColor = Color.FromArgb(18, 20, 24);
            box.ForeColor = Color.WhiteSmoke;
            box.FlatStyle = FlatStyle.Flat;
        }
        _zeige.Width = 130;
        _zeige.Items.AddRange(new object[] { Loc.T("Missing"), Loc.T("Owned"), Loc.T("All cars") });
        _zeige.SelectedIndex = 0;
        _weg.Width = 190;
        _weg.Items.Add(Loc.T("Any way"));
        foreach (var a in Arten) { _weg.Items.Add(ArtName(a)); }
        _weg.SelectedIndex = 0;
        _klasse.Width = 110;
        _klasse.Items.Add(Loc.T("Any class"));
        _klasse.Items.AddRange(Klassen);
        _klasse.SelectedIndex = 0;
        filter.Controls.AddRange(new Control[] { _filter, _zeige, _weg, _klasse });
        _filter.TextChanged += (_, _) => Fuellen();
        _zeige.SelectedIndexChanged += (_, _) => Fuellen();
        _weg.SelectedIndexChanged += (_, _) => Fuellen();
        _klasse.SelectedIndexChanged += (_, _) => Fuellen();

        _liste.Dock = DockStyle.Fill;
        _liste.View = View.Details;
        _liste.CheckBoxes = true;
        _liste.FullRowSelect = true;
        _liste.MultiSelect = false;
        _liste.HideSelection = false;
        _liste.BackColor = Color.FromArgb(18, 20, 24);
        _liste.ForeColor = Color.WhiteSmoke;
        _liste.Columns.Add(Loc.T("Car"), 330);
        _liste.Columns.Add(Loc.T("Class"), 70);
        _liste.Columns.Add(Loc.T("Type"), 150);
        _liste.Columns.Add(Loc.T("Price"), 100, HorizontalAlignment.Right);
        _liste.Columns.Add(Loc.T("How to get it"), 520);
        _liste.ItemChecked += (_, e) => Abgehakt(e.Item);
        _liste.SelectedIndexChanged += (_, _) => Auswahl();
        _liste.ColumnClick += (_, e) =>
        {
            _sortAb = e.Column == _sortSpalte ? !_sortAb : false;
            _sortSpalte = e.Column;
            Fuellen();
        };
        _liste.DoubleClick += (_, _) => WikiOeffnen();

        var unten = new Panel { Dock = DockStyle.Bottom, Height = 132, Padding = new Padding(12, 6, 12, 6) };
        _detailKopf.Dock = DockStyle.Top;
        _detailKopf.Height = 24;
        _detailKopf.Font = new Font("Segoe UI Semibold", 10f);
        _detailKopf.ForeColor = Color.FromArgb(127, 211, 255);
        _detail.Dock = DockStyle.Fill;
        _detail.ForeColor = Color.Gainsboro;
        _wiki.Dock = DockStyle.Bottom;
        _wiki.Height = 20;
        _wiki.Text = Loc.T("Open on the wiki");
        _wiki.LinkColor = Color.FromArgb(127, 211, 255);
        _wiki.Visible = false;
        _wiki.LinkClicked += (_, _) => WikiOeffnen();
        unten.Controls.Add(_detail);
        unten.Controls.Add(_wiki);
        unten.Controls.Add(_detailKopf);

        Controls.Add(_liste);
        Controls.Add(unten);
        Controls.Add(filter);
        Controls.Add(oben);

        GarageImport.GarageGelesen += () => BeiUns(() => { _besitz = OwnedCars.Laden(); if (_geladen) { Fuellen(); } });
        _detailKopf.Text = Loc.T("Select a car to see every way to get it.");
    }

    /// <summary>Beim Oeffnen des Reiters: Liste laden (einmal), Besitz neu lesen, bei Bedarf beim Server nachfragen.</summary>
    public void Zeigen()
    {
        _besitz = OwnedCars.Laden();
        _gefahren = Gefahren();
        if (!_geladen)
        {
            _geladen = true;
            _sammlung = CarCollection.Laden();
            // Hoechstens alle zwoelf Stunden -- der Server baut sie nur einmal am Tag neu.
            var alter = DateTimeOffset.UtcNow - (_sammlung?.GebautAm ?? DateTimeOffset.MinValue);
            if (alter > TimeSpan.FromHours(12)) { VomServer(zeigen: false); }
        }
        Fuellen();
    }

    private HashSet<int> Gefahren()
    {
        var n = _notizen();
        if (n is null) { return new HashSet<int>(); }
        return n.All.Where(e => e.Source == "driven" || !e.IsModel).Select(e => e.Ordinal).Where(o => o > 0).ToHashSet();
    }

    private void Fuellen()
    {
        if (!_geladen) { return; }
        var s = _sammlung;
        if (s is null)
        {
            _kopf.Text = Loc.T("No car list yet -- it comes from the server.");
            _quelle.Text = Loc.T("Check your connection, then press “Check for a newer list”.");
            _stand.Text = string.Empty;
            _liste.Items.Clear();
            return;
        }

        var besitz = s.Autos.ToDictionary(a => a, a => _besitz.Besitz(a, _gefahren));
        var hat = besitz.Count(b => b.Value.Hat);
        _kopf.Text = string.Format(Loc.T("{0} of {1} cars owned -- {2} missing"), hat, s.Autos.Count, s.Autos.Count - hat);
        _quelle.Text = _besitz.GarageGelesen is { } wann
            ? string.Format(Loc.T("Owned cars: your garage, read {0}. Ticks you set count over it."),
                            wann.LocalDateTime.ToString("g"))
            : _gefahren.Count > 0
                ? Loc.T("Owned cars: the cars you have driven with the app. Tick the others you own.")
                : _konsole
                    ? Loc.T("Tick the cars you own. Cars you drive with the app count by themselves.")
                    : Loc.T("Read your garage while the game runs -- or tick the cars you own.");
        _stand.Text = string.Format(Loc.T("Car list from forza.net/fh6cars ({0}); ways to get them from the Forza Wiki (CC BY-SA)."),
                                    s.ListeStand ?? "?");

        var text = _filter.Text.Trim();
        var art = _weg.SelectedIndex > 0 ? Arten[_weg.SelectedIndex - 1] : null;
        var klasse = _klasse.SelectedIndex > 0 ? Klassen[_klasse.SelectedIndex - 1] : null;
        var zeige = _zeige.SelectedIndex;
        var treffer = s.Autos.Where(a =>
            (zeige == 2 || besitz[a].Hat == (zeige == 1))
            && (text.Length == 0 || a.Anzeige.Contains(text, StringComparison.OrdinalIgnoreCase)
                || a.Typ.Contains(text, StringComparison.OrdinalIgnoreCase))
            && (art is null || a.Wege.Any(w => w.Art == art))
            && (klasse is null || string.Equals(a.Klasse, klasse, StringComparison.OrdinalIgnoreCase)));
        treffer = Sortiert(treffer);

        var gewaehlt = (_liste.SelectedItems.Count > 0 ? _liste.SelectedItems[0].Tag as CarCollection.Auto : null)?.Schluessel;
        _still = true;
        _liste.BeginUpdate();
        try
        {
            _liste.Items.Clear();
            foreach (var a in treffer)
            {
                var z = new ListViewItem(a.Anzeige) { Tag = a, Checked = besitz[a].Hat };
                z.SubItems.Add(a.Klasse is null ? string.Empty : $"{a.Klasse} {a.Pi}");
                z.SubItems.Add(a.Typ);
                z.SubItems.Add(Preis(a) is { } p ? p.ToString("N0") + " CR" : string.Empty);
                z.SubItems.Add(string.Join("  ·  ", a.Wege.Where(w => w.Art != "auction").Take(3).Select(WegKurz)));
                z.ForeColor = besitz[a].Hat ? Color.Gray : Color.WhiteSmoke;
                _liste.Items.Add(z);
                if (a.Schluessel == gewaehlt) { z.Selected = true; }
            }
        }
        finally
        {
            _liste.EndUpdate();
            _still = false;
        }
        Auswahl();
    }

    private IEnumerable<CarCollection.Auto> Sortiert(IEnumerable<CarCollection.Auto> autos)
    {
        IOrderedEnumerable<CarCollection.Auto> o = _sortSpalte switch
        {
            1 => autos.OrderBy(a => a.Pi ?? int.MaxValue),
            2 => autos.OrderBy(a => a.Typ, StringComparer.CurrentCultureIgnoreCase),
            3 => autos.OrderBy(a => Preis(a) ?? long.MaxValue),
            4 => autos.OrderBy(a => a.Wege.FirstOrDefault()?.Art ?? string.Empty),
            _ => autos.OrderBy(a => a.Anzeige, StringComparer.CurrentCultureIgnoreCase),
        };
        var fertig = o.ThenBy(a => a.Anzeige, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (_sortAb) { fertig.Reverse(); }
        return fertig;
    }

    private static long? Preis(CarCollection.Auto a) =>
        a.Wege.FirstOrDefault(w => w.Art == "autoshow")?.Zahl("price");

    private void Abgehakt(ListViewItem z)
    {
        if (_still || z.Tag is not CarCollection.Auto a) { return; }
        _besitz.Setze(a, z.Checked, _gefahren);
        _besitz.Speichern();
        z.ForeColor = z.Checked ? Color.Gray : Color.WhiteSmoke;
        // Die Zeile bleibt, bis der Filter wechselt: wer mehrere nacheinander abhakt,
        // soll die Liste nicht unter dem Mauszeiger wegrutschen sehen.
        var alle = _sammlung?.Autos ?? Array.Empty<CarCollection.Auto>();
        var hat = alle.Count(x => _besitz.Besitz(x, _gefahren).Hat);
        _kopf.Text = string.Format(Loc.T("{0} of {1} cars owned -- {2} missing"), hat, alle.Count, alle.Count - hat);
        if (z.Selected) { Auswahl(); }
    }

    private void Auswahl()
    {
        if (_liste.SelectedItems.Count == 0 || _liste.SelectedItems[0].Tag is not CarCollection.Auto a)
        {
            _detailKopf.Text = Loc.T("Select a car to see every way to get it.");
            _detail.Text = string.Empty;
            _wiki.Visible = false;
            return;
        }
        var (hat, warum) = _besitz.Besitz(a, _gefahren);
        var zustand = hat
            ? warum switch
            {
                OwnedCars.Grund.Garage => Loc.T("In your garage"),
                OwnedCars.Grund.Gefahren => Loc.T("Driven with the app"),
                _ => Loc.T("Marked as yours"),
            }
            : Loc.T("Missing");
        _detailKopf.Text = $"{a.Anzeige}  ·  {zustand}";
        _detail.Text = string.Join(Environment.NewLine, a.Wege.Select(WegLang));
        _wiki.Visible = !string.IsNullOrEmpty(a.Wiki);
    }

    private void WikiOeffnen()
    {
        if (_liste.SelectedItems.Count == 0 || _liste.SelectedItems[0].Tag is not CarCollection.Auto { Wiki: { Length: > 0 } seite })
        {
            return;
        }
        try
        {
            var url = "https://forza.fandom.com/wiki/" + Uri.EscapeDataString(seite.Replace(' ', '_'));
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Kein Browser eingerichtet -- nichts zu tun.
        }
    }

    private void GarageLesen()
    {
        if (!Tuning.ForzaMemoryDb.GameRunning)
        {
            _quelle.Text = Loc.T("Start the game first -- the garage is read from its memory.");
            return;
        }
        _garage.Enabled = false;
        _quelle.Text = Loc.T("searching the game's memory for its database ...");
        var rat = _rat();
        var notizen = _notizen();
        Task.Run(() =>
        {
            var (ids, fehler) = GarageImport.Lesen(m => BeiUns(() => _quelle.Text = m));
            BeiUns(() =>
            {
                _garage.Enabled = true;
                if (fehler is not null)
                {
                    _quelle.Text = fehler;
                    return;
                }
                GarageImport.Merken(ids, notizen, rat);
                _besitz = OwnedCars.Laden();
                Fuellen();
            });
        });
    }

    private async void VomServer(bool zeigen)
    {
        _neuHolen.Enabled = false;
        if (zeigen) { _stand.Text = Loc.T("checking the server ..."); }
        var neu = await CarCollection.VomServerAsync(_server(), _sammlung);
        if (IsDisposed) { return; }
        _neuHolen.Enabled = true;
        if (neu is not null) { _sammlung = neu; }
        Fuellen();
        if (zeigen && neu is null)
        {
            _stand.Text = _sammlung is null
                ? Loc.T("The server could not be reached.")
                : Loc.T("No newer list on the server.") + "  " + _stand.Text;
        }
    }

    private void BeiUns(Action was)
    {
        if (IsDisposed) { return; }
        if (IsHandleCreated && InvokeRequired) { BeginInvoke(was); }
        else { was(); }
    }

    // ------------------------------------------------------------------ Texte

    internal static string ArtName(string art) => art switch
    {
        "autoshow" => Loc.T("Autoshow"),
        "playlist" => Loc.T("Festival Playlist"),
        "wheelspin" => Loc.T("Wheelspin"),
        "aftermarket" => Loc.T("Aftermarket dealer"),
        "journal" => Loc.T("Collection Journal"),
        "barn" => Loc.T("Barn Find"),
        "treasure" => Loc.T("Treasure car"),
        "mastery" => Loc.T("Car Mastery"),
        "campaign" => Loc.T("Campaign"),
        "gift" => Loc.T("Gift"),
        "loyalty" => Loc.T("Loyalty reward"),
        "dlc" => "DLC",
        "auction" => Loc.T("Auction House"),
        "unobtainable" => Loc.T("Not obtainable"),
        _ => art,
    };

    /// <summary>Fuer die Spalte: Art und das Wichtigste -- Paketname, Saison, Ort.</summary>
    internal static string WegKurz(CarCollection.Weg w) => w.Art switch
    {
        "dlc" => "DLC: " + (w.Text("pack") ?? "?"),
        "playlist" when w.Zahl("series") is { } s => ArtName(w.Art) + " (" + string.Format(Loc.T("Series {0}"), s) + ")",
        "barn" or "treasure" when w.Text("where") is { } o => ArtName(w.Art) + ": " + o,
        _ => ArtName(w.Art),
    };

    /// <summary>Fuer die Einzelheiten unten: alles, was die Quelle weiss, in einem Satz.</summary>
    internal static string WegLang(CarCollection.Weg w)
    {
        switch (w.Art)
        {
            case "autoshow":
                return w.Zahl("price") is { } preis
                    ? string.Format(Loc.T("Autoshow: {0} CR"), preis.ToString("N0")) : ArtName(w.Art);
            case "playlist":
            {
                var teile = new List<string>();
                if (w.Zahl("series") is { } s)
                {
                    var serie = string.Format(Loc.T("Series {0}"), s);
                    if (w.Text("season") is { } z) { serie += " (" + Saison(z) + ")"; }
                    teile.Add(serie);
                }
                if (Was(w) is { } was) { teile.Add(was); }
                var kopf = Loc.T("Festival Playlist");
                return teile.Count == 0
                    ? kopf + " -- " + Loc.T("a possible seasonal reward")
                    : kopf + ": " + string.Join(", ", teile) + " -- " + Loc.T("last time; it can come back");
            }
            case "aftermarket":
            {
                var t = ArtName(w.Art);
                if (w.Text("where") is { } o) { t += " " + o; }
                if (w.Zahl("price") is { } p) { t += ", " + string.Format(Loc.T("{0} CR"), p.ToString("N0")); }
                if (w.Text("event") is { } e) { t += " (" + e + ")"; }
                return t;
            }
            case "barn":
                return w.Text("where") is { } b ? string.Format(Loc.T("Barn Find: {0}"), b) : ArtName(w.Art);
            case "treasure":
                return w.Text("where") is { } t2 ? string.Format(Loc.T("Treasure car: {0}"), t2) : ArtName(w.Art);
            case "mastery":
                return w.Text("car") is { } c ? string.Format(Loc.T("Car Mastery of the {0}"), c) : ArtName(w.Art);
            case "journal":
            {
                var t = ArtName(w.Art);
                if (w.Text("cat") is { } k) { t += ": " + k; }
                if (w.Zahl("points") is { } p) { t += ", " + string.Format(Loc.T("{0} points"), p.ToString("N0")); }
                return t;
            }
            case "campaign":
                return w.Text("band") is { } band ? string.Format(Loc.T("Campaign: the {0} wristband"), band) : ArtName(w.Art);
            case "gift":
                return w.Text("date") is { } d ? ArtName(w.Art) + " (" + d + ")" : ArtName(w.Art);
            case "loyalty":
                return w.Text("game") is { } g ? string.Format(Loc.T("Loyalty reward for playing {0}"), g) : ArtName(w.Art);
            default:
                return WegKurz(w);
        }
    }

    private static string Saison(string englisch)
    {
        // "Winter, week 2" -> "Winter" + Woche
        var teile = englisch.Split(", week ");
        var name = teile[0] switch
        {
            "Summer" => Loc.T("Summer"),
            "Autumn" => Loc.T("Autumn"),
            "Winter" => Loc.T("Winter"),
            "Spring" => Loc.T("Spring"),
            _ => teile[0],
        };
        return teile.Length > 1 ? name + ", " + string.Format(Loc.T("week {0}"), teile[1]) : name;
    }

    private static string? Was(CarCollection.Weg w)
    {
        var x = w.Text("wx");
        return w.Text("wk") switch
        {
            "champ" when x is not null => string.Format(Loc.T("the “{0}” championship"), x),
            "season" when x is not null => string.Format(Loc.T("season milestone, {0} points"), x),
            "series" when x is not null => string.Format(Loc.T("Series milestone, {0} points"), x),
            "trial" when x is not null => string.Format(Loc.T("The Trial “{0}”"), x),
            _ => w.Text("what"),
        };
    }
}
