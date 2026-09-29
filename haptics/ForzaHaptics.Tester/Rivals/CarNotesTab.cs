using System.Drawing;
using System.Windows.Forms;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Der Reiter, auf dem die Notizen zu den eigenen Autos stehen.
/// </summary>
/// <remarks>
/// Links die Autos, rechts der Text. Gespeichert wird beim Verlassen des Feldes und
/// beim Wechsel der Auswahl -- nicht erst auf Knopfdruck. Ein Knopf, den man
/// vergisst, ist eine verlorene Notiz.
///
/// ## Woher die Autos kommen (seit 2026-09-25)
///
/// Bis dahin nur aus der Telemetrie, nur fuer diese Sitzung, und die Liste baute sich
/// nur beim Oeffnen des Reiters. Der Nutzer stand in "My Cars" auf seinem 595
/// esseesse, hatte die App offen -- und sah "No cars seen yet". Jetzt:
///
/// - das Auto unter dem Rahmen im Automenue, vom Bildschirm gelesen
///   (<see cref="CarGridReader"/>) -- es wird hier auch gleich ausgewaehlt;
/// - das gefahrene Auto aus der Telemetrie;
/// - auf Knopfdruck die ganze Garage aus dem Speicher des Spiels.
///
/// Alles landet in <c>config/car_notes.json</c> und ist nach einem Neustart noch da.
/// Von Hand anlegen laesst sich weiterhin nichts: ein Tippfehler ergaebe eine Notiz an
/// einem Auto, das es nicht gibt.
/// </remarks>
internal sealed class CarNotesTab : UserControl
{
    private readonly Func<CarNotes> _notes;
    private readonly Func<OverlayController?> _regler;
    private readonly ListView _liste = new();
    private readonly TextBox _text = new();
    private readonly TextBox _filter = new();
    private readonly Label _kopf = new();
    private readonly Label _hinweis = new();
    private readonly Label _jetzt = new();
    private readonly Label _garageStand = new();
    private readonly Button _garage = new();
    private string _key = string.Empty;
    private bool _still;
    private bool _speichert;
    private bool _liegenGeblieben;
    private OverlayController? _angebunden;

    public CarNotesTab(Func<CarNotes> notes, Func<OverlayController?>? regler = null)
    {
        _notes = notes;
        _regler = regler ?? (() => null);
        BackColor = Color.FromArgb(24, 26, 31);
        ForeColor = Color.WhiteSmoke;
        Dock = DockStyle.Fill;

        _jetzt.Dock = DockStyle.Top;
        _jetzt.Height = 30;
        _jetzt.Padding = new Padding(12, 8, 12, 0);
        _jetzt.ForeColor = Color.FromArgb(255, 210, 90);
        _jetzt.Font = new Font("Segoe UI Semibold", 9.5f);

        var links = new Panel { Dock = DockStyle.Left, Width = 440, Padding = new Padding(8, 6, 0, 8) };

        _filter.Dock = DockStyle.Top;
        _filter.PlaceholderText = Loc.T("Filter by name");
        _filter.BackColor = Color.FromArgb(18, 20, 24);
        _filter.ForeColor = Color.WhiteSmoke;
        _filter.TextChanged += (_, _) => Refresh();

        _liste.Dock = DockStyle.Fill;
        _liste.View = View.Details;
        _liste.FullRowSelect = true;
        _liste.MultiSelect = false;
        _liste.HideSelection = false;
        _liste.BackColor = Color.FromArgb(18, 20, 24);
        _liste.ForeColor = Color.WhiteSmoke;
        _liste.Columns.Add(Loc.T("Car"), 215);
        _liste.Columns.Add("PI", 45);
        _liste.Columns.Add(Loc.T("hp"), 50);
        _liste.Columns.Add(Loc.T("From"), 75);
        _liste.Columns.Add(Loc.T("Note"), 40);
        _liste.SelectedIndexChanged += (_, _) => Auswahl();

        var unten = new Panel { Dock = DockStyle.Bottom, Height = 64 };
        _garage.Text = Loc.T("Add every car in my garage");
        _garage.AutoSize = true;
        _garage.FlatStyle = FlatStyle.Flat;
        _garage.ForeColor = Color.WhiteSmoke;
        _garage.Location = new Point(0, 6);
        _garage.Click += (_, _) => GarageLesen();
        _garageStand.AutoSize = false;
        _garageStand.Location = new Point(0, 38);
        _garageStand.Size = new Size(430, 24);
        _garageStand.ForeColor = Color.Gray;
        _garageStand.Text = Loc.T("Reads the game's memory while it runs -- takes up to a minute.");
        unten.Controls.Add(_garage);
        unten.Controls.Add(_garageStand);

        links.Controls.Add(_liste);
        links.Controls.Add(_filter);
        links.Controls.Add(unten);

        var rechts = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };

        _kopf.Dock = DockStyle.Top;
        _kopf.Height = 26;
        _kopf.ForeColor = Color.FromArgb(127, 211, 255);
        _kopf.Font = new Font("Segoe UI Semibold", 10f);
        _kopf.Text = Loc.T("Pick a car on the left");

        _text.Multiline = true;
        _text.Dock = DockStyle.Fill;
        _text.ScrollBars = ScrollBars.Vertical;
        _text.BackColor = Color.FromArgb(18, 20, 24);
        _text.ForeColor = Color.WhiteSmoke;
        _text.BorderStyle = BorderStyle.FixedSingle;
        _text.Enabled = false;
        _text.Leave += (_, _) =>
        {
            Sichern();
            // Was waehrend des Schreibens hereinkam, jetzt nachziehen.
            if (_liegenGeblieben) { _liegenGeblieben = false; Refresh(); }
        };
        _text.TextChanged += (_, _) => { if (!_still) { _hinweis.Text = Loc.T("unsaved"); } };

        _hinweis.Dock = DockStyle.Bottom;
        _hinweis.Height = 22;
        _hinweis.ForeColor = Color.Gray;
        _hinweis.Text = Loc.T(
            "The note shows over the game while this car is highlighted in My Cars or driven.");

        rechts.Controls.Add(_text);
        rechts.Controls.Add(_hinweis);
        rechts.Controls.Add(_kopf);

        Controls.Add(rechts);
        Controls.Add(links);
        Controls.Add(_jetzt);
        ZeigeJetzt();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) { Anbinden(); }
    }

    /// <summary>
    /// Auf Aenderungen hoeren: neue Autos, geaenderte Notizen, ein anderes Auto im Spiel.
    /// </summary>
    /// <remarks>
    /// Der Regler entsteht nicht vor diesem Reiter -- darum hier, beim ersten Zeigen,
    /// und nicht im Konstruktor.
    /// </remarks>
    private void Anbinden()
    {
        var regler = _regler();
        if (regler is null || ReferenceEquals(regler, _angebunden)) { return; }
        _angebunden = regler;
        regler.Notes.Changed += () => BeiUns(Geaendert);
        regler.CurrentCarChanged += (_, _) => BeiUns(AutoImSpiel);
    }

    private void BeiUns(Action was)
    {
        if (IsDisposed) { return; }
        if (IsHandleCreated && InvokeRequired) { BeginInvoke(was); }
        else { was(); }
    }

    private void Geaendert()
    {
        if (_speichert) { return; }
        // WAEHREND DES SCHREIBENS NICHT UMBAUEN: das naechste gesehene Auto wuerde dem
        // Nutzer sonst die Auswahl und den Cursor unter den Fingern wegziehen.
        if (_text.Focused) { _liegenGeblieben = true; return; }
        Refresh();
    }

    private void AutoImSpiel()
    {
        ZeigeJetzt();
        if (_text.Focused) { return; }
        if (_regler()?.CurrentCar is { } c) { Waehle(CarNotes.ModelKey(c.Ordinal)); }
    }

    private void ZeigeJetzt()
    {
        var c = _regler()?.CurrentCar;
        _jetzt.Text = c is { } a
            ? Loc.T("In the game now:") + " " + a.Name + "  ·  "
              + (a.Source == "menu" ? Loc.T("highlighted in My Cars") : Loc.T("driving"))
            : Loc.T("No car in view -- highlight one in My Cars, or drive one.");
    }

    private static string Quelle(string? quelle) => quelle switch
    {
        "driven" => Loc.T("driven"),
        "menu" => Loc.T("My Cars"),
        "garage" => Loc.T("garage"),
        _ => string.Empty,
    };

    private static string Anzeige(CarNotes.Entry e)
    {
        var name = string.IsNullOrEmpty(e.Name) ? string.Format(Loc.T("car {0}"), e.Ordinal) : e.Name;
        return e.IsModel || e.Pi <= 0 ? name : $"{name}  ·  PI {e.Pi}";
    }

    /// <summary>Die Liste neu aufbauen.</summary>
    public new void Refresh()
    {
        Anbinden();
        Sichern();
        ZeigeJetzt();
        var gewaehlt = _key;
        if (string.IsNullOrEmpty(gewaehlt) && _regler()?.CurrentCar is { } c)
        {
            gewaehlt = CarNotes.ModelKey(c.Ordinal);
        }
        var filter = _filter.Text.Trim();
        _liste.BeginUpdate();
        _liste.Items.Clear();
        ListViewItem? auswahl = null;
        // JE AUTO EINE ZEILE, dazu die Aufbauten, die eine eigene Notiz tragen. Jeder
        // gefahrene Aufbau als eigene Zeile ergaebe bei 569 Garagenautos eine Liste,
        // in der man sein Auto zweimal findet und nicht weiss, welche Zeile gilt.
        var zeilen = _notes().All
            .Where(e => e.IsModel || !string.IsNullOrWhiteSpace(e.Comment))
            .Where(e => filter.Length == 0
                        || Anzeige(e).Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => !string.IsNullOrWhiteSpace(x.Comment))
            .ThenBy(x => string.IsNullOrEmpty(x.Name) ? "zzz" : x.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.IsModel ? 0 : 1)
            .ThenBy(x => x.Pi);
        foreach (var e in zeilen)
        {
            var zeile = new ListViewItem(Anzeige(e)) { Tag = e.Key };
            zeile.SubItems.Add(e.Pi > 0 && !e.IsModel ? e.Pi.ToString() : "-");
            zeile.SubItems.Add(e.Kilowatts > 0 && !e.IsModel ? e.HorsePower.ToString() : "-");
            zeile.SubItems.Add(Quelle(e.Source));
            zeile.SubItems.Add(string.IsNullOrWhiteSpace(e.Comment) ? string.Empty : "●");
            if (!string.IsNullOrWhiteSpace(e.Comment))
            {
                zeile.ForeColor = Color.FromArgb(127, 211, 255);
            }
            _liste.Items.Add(zeile);
            if (e.Key == gewaehlt) { auswahl = zeile; }
        }
        _liste.EndUpdate();
        if (auswahl is not null)
        {
            auswahl.Selected = true;
            auswahl.EnsureVisible();
        }

        if (_liste.Items.Count == 0)
        {
            _kopf.Text = filter.Length > 0 ? Loc.T("No car matches the filter") : Loc.T("No cars seen yet");
            _hinweis.Text = Loc.T(
                "Highlight a car in My Cars, drive one, or add your whole garage below.");
            _text.Enabled = false;
        }
    }

    /// <summary>Eine Zeile auswaehlen, ohne die Liste neu zu bauen.</summary>
    private void Waehle(string key)
    {
        foreach (ListViewItem z in _liste.Items)
        {
            if ((string)(z.Tag ?? string.Empty) != key) { continue; }
            if (!z.Selected) { z.Selected = true; }
            z.EnsureVisible();
            return;
        }
        // Noch nicht in der Liste (eben erst gesehen): neu bauen, die Auswahl folgt.
        _key = key;
        Refresh();
    }

    private void Auswahl()
    {
        Sichern();
        if (_liste.SelectedItems.Count == 0)
        {
            _key = string.Empty;
            _text.Enabled = false;
            _still = true; _text.Text = string.Empty; _still = false;
            return;
        }
        _key = (string)(_liste.SelectedItems[0].Tag ?? string.Empty);
        var e = _notes().Lookup(_key);
        _kopf.Text = e is null
            ? Loc.T("Pick a car on the left")
            : e.IsModel
                ? $"{Anzeige(e)}   ·   {Loc.T("every build of this car")}"
                : Anzeige(e) + "   " + string.Format(Loc.T("{0} hp"), e.HorsePower)
                  + "   ·   " + Loc.T("only this build");
        _still = true;
        _text.Text = e?.Comment ?? string.Empty;
        _still = false;
        _text.Enabled = true;
        _hinweis.Text = Loc.T(
            "The note shows over the game while this car is highlighted in My Cars or driven.");
    }

    private void Sichern()
    {
        if (string.IsNullOrEmpty(_key) || !_text.Enabled) { return; }
        var da = _notes().Lookup(_key)?.Comment ?? string.Empty;
        if (da == _text.Text) { return; }
        _speichert = true;
        try { _notes().SetComment(_key, _text.Text); }
        finally { _speichert = false; }
        _hinweis.Text = Loc.T("saved");
        // Sofort ueber dem Spiel zeigen, nicht erst beim naechsten Autowechsel.
        _regler()?.RefreshCarNote();
        // Die Markierung in der Liste nachziehen, ohne alles neu zu bauen.
        foreach (ListViewItem z in _liste.Items)
        {
            if ((string)(z.Tag ?? string.Empty) != _key) { continue; }
            var hat = !string.IsNullOrWhiteSpace(_text.Text);
            z.SubItems[4].Text = hat ? "●" : string.Empty;
            z.ForeColor = hat ? Color.FromArgb(127, 211, 255) : Color.WhiteSmoke;
        }
    }

    /// <summary>
    /// Die ganze Garage aufnehmen -- aus dem Speicher des laufenden Spiels.
    /// </summary>
    /// <remarks>
    /// Derselbe Weg wie im Reiter "Tuning inspector" (<see cref="Tuning.ForzaMemoryDb"/>):
    /// die Garage liegt als SQLite-Datenbank im Speicher, eine Datei auf der Platte
    /// gibt es nicht. Die Namen kommen aus dem Datensatz; ein Auto, das er nicht kennt,
    /// steht als "car 1234" da -- lieber so als geraten.
    /// </remarks>
    private void GarageLesen()
    {
        if (!Tuning.ForzaMemoryDb.GameRunning)
        {
            _garageStand.Text = Loc.T("Start the game first -- the garage is read from its memory.");
            return;
        }
        _garage.Enabled = false;
        _garageStand.Text = Loc.T("searching the game's memory for its database ...");
        var rat = _regler()?.Advisor;
        Task.Run(() =>
        {
            var (autos, fehler) = GarageImport.Lesen(m => BeiUns(() => _garageStand.Text = m));
            BeiUns(() =>
            {
                _garage.Enabled = true;
                if (fehler is not null)
                {
                    _garageStand.Text = fehler;
                    return;
                }
                var neu = GarageImport.Merken(autos, _notes(), rat);
                _garageStand.Text = string.Format(
                    Loc.T("{0} cars in your garage, {1} new in the list."), autos.Count, neu);
                Refresh();
            });
        });
    }
}
