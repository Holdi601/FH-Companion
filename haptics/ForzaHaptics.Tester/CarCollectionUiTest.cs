using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace ForzaHaptics.Tester;

/// <summary>
/// "Car collection" am echten Reiter (--car-collection-ui-test): ein Fenster abseits des
/// Schirms, Haken setzen und entfernen, jeder Filter, jede Sortierung, ein echter
/// Doppelklick, die Liste vom eigenen kleinen Server -- und die mitgelieferte Liste auf
/// Tempo.
/// </summary>
/// <remarks>
/// Nicht in --edge-case-test: der laeuft auch waehrend gespielt wird, und dieser hier
/// oeffnet ein Fenster. Alles Persoenliche geht in einen eigenen Temp-Ordner; die echten
/// config/owned_cars.json und car_notes.json werden nicht beruehrt.
/// </remarks>
internal static class CarCollectionUiTest
{
    private static void Soll(bool gut, string was)
    {
        if (!gut) { throw new InvalidOperationException("Car collection: " + was); }
    }

    public static void Run()
    {
        var ordner = Path.Combine(Path.GetTempPath(), "fhc-cc-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ordner);
        try
        {
            BesitzDatei(ordner);
            Reiter(ordner);
            NichtSicher(ordner);
            Doppelklick(ordner);
            Server(ordner).GetAwaiter().GetResult();
            EchteListe(ordner);
        }
        finally
        {
            try { Directory.Delete(ordner, true); } catch (Exception) { }
        }
    }

    private static readonly string[] Klassen = { "D", "C", "B", "A", "S1", "S2", "R", "X" };

    /// <summary>120 erfundene Autos: jedes vierte je Autoshow, Playlist, DLC, Scheunenfund.</summary>
    internal static string ListeJson(int anzahl, DateTime gebaut)
    {
        var autos = new List<string>();
        for (var i = 1; i <= anzahl; i++)
        {
            var weg = (i % 4) switch
            {
                0 => $"{{\"k\":\"autoshow\",\"price\":{i * 1000}}}",
                1 => "{\"k\":\"playlist\",\"series\":3,\"season\":\"Autumn\",\"wk\":\"champ\",\"wx\":\"Retro Rewind\"}",
                2 => "{\"k\":\"dlc\",\"pack\":\"Car Pass\"},{\"k\":\"autoshow\",\"price\":1000000}",
                _ => "{\"k\":\"barn\",\"where\":\"Southwest Ito\"}",
            };
            var ids = i == anzahl ? $"\"id\":{i},\"ids\":[{i},{i + 1000}]" : $"\"id\":{i}";
            autos.Add($"{{\"name\":\"Testwagen {i:000}\",\"make\":\"Test\",\"year\":{2000 + i % 25},"
                      + $"\"type\":\"{(i % 2 == 0 ? "Hot Hatch" : "Rally")}\",\"pi\":{100 + i * 7},"
                      + $"\"class\":\"{Klassen[i % 8]}\",{ids},\"wiki\":\"Testwagen {i} (Coupé)\",\"ways\":[{weg}]}}");
        }
        return "{\"format\":\"fhc-cars-1\",\"built\":\"" + gebaut.ToString("yyyy-MM-ddTHH:mm:ssZ")
               + "\",\"list_updated\":\"8 September 2026\",\"cars\":[" + string.Join(",", autos) + "]}";
    }

    private static Rivals.CarCollection Liste() =>
        Rivals.CarCollection.Lesen(ListeJson(120, DateTime.UtcNow)) ?? throw new InvalidOperationException("Testliste unlesbar");

    private static Rivals.CarCollection.Auto Auto(Rivals.CarCollection l, int nr) => l.Autos[nr - 1];

    private static (Form Fenster, Rivals.CarCollectionTab Reiter, List<string> Geoeffnet) Aufbauen(
        Rivals.CarCollection liste, string besitz, Rivals.CarNotes? notizen, bool konsole)
    {
        var form = new Form
        {
            StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000),
            Size = new Size(1400, 900), ShowInTaskbar = false,
        };
        var geoeffnet = new List<string>();
        var tab = new Rivals.CarCollectionTab(() => null, () => notizen, () => null, konsole, () => liste, besitz)
        {
            OeffneUrl = u => geoeffnet.Add(u),
        };
        form.Controls.Add(tab);
        form.Show();
        Application.DoEvents();
        tab.Zeigen();
        Application.DoEvents();
        return (form, tab, geoeffnet);
    }

    private static ListViewItem Zeile(Rivals.CarCollectionTab t, int nr) =>
        t.Liste.Items.Cast<ListViewItem>().First(z => ((Rivals.CarCollection.Auto)z.Tag!).Name == $"Testwagen {nr:000}");

    private static bool Hat(Rivals.CarCollectionTab t, int nr) =>
        t.Liste.Items.Cast<ListViewItem>().Any(z => ((Rivals.CarCollection.Auto)z.Tag!).Name == $"Testwagen {nr:000}");

    // ------------------------------------------------------------------ Besitzdatei

    private static void BesitzDatei(string ordner)
    {
        var pfad = Path.Combine(ordner, "kaputt", "owned_cars.json");
        Directory.CreateDirectory(Path.GetDirectoryName(pfad)!);
        File.WriteAllText(pfad, "{ das ist kein JSON");
        var b = Rivals.OwnedCars.Laden(pfad);
        Soll(b.GarageGelesen is null && b.Markiert.Count == 0, "eine kaputte Besitzdatei ergibt keinen leeren Stand");
        var l = Liste();
        b.Setze(Auto(l, 3), true, new HashSet<int>());
        b.Speichern();
        var wieder = Rivals.OwnedCars.Laden(pfad);
        Soll(wieder.Markiert.TryGetValue(Auto(l, 3).Schluessel, out var hat) && hat, "ein Haken ueberlebt Speichern und Laden nicht");
        Soll(!File.Exists(pfad + ".tmp"), "die Zwischendatei bleibt liegen");

        File.WriteAllText(pfad, "{\"garage_ids\":[1,2,999,1120],\"garage_read\":\"2026-09-29T01:00:00+00:00\",\"marked\":null}");
        var g = Rivals.OwnedCars.Laden(pfad);
        Soll(g.NichtErkannt(l) == 1, $"nicht erkannte Garagenautos: {g.NichtErkannt(l)} statt 1 (999)");
        Soll(g.Besitz(Auto(l, 120), new HashSet<int>()).Hat, "ein Auto mit zwei ids gilt nur ueber die erste als besessen");
        Soll(!g.Besitz(Auto(l, 3), new HashSet<int>()).Hat, "ein Auto ausserhalb der Garage gilt als besessen");
        g.GarageMerken(new[] { 5, 5, 4 });
        Soll(g.GarageIds.SequenceEqual(new[] { 4, 5 }), "die Garage wird nicht entdoppelt und sortiert");
        Soll(Rivals.OwnedCars.Laden(Path.Combine(ordner, "gibt-es-nicht", "x.json")).Markiert.Count == 0,
             "eine fehlende Besitzdatei wirft");
    }

    // ------------------------------------------------------------------ der Reiter

    private static void Reiter(string ordner)
    {
        var besitz = Path.Combine(ordner, "owned_cars.json");
        var notizen = new Rivals.CarNotes(Path.Combine(ordner, "car_notes.json"));
        notizen.NoteModel(7, "Testwagen 007", "driven");
        var liste = Liste();
        var (form, t, geoeffnet) = Aufbauen(liste, besitz, notizen, konsole: true);
        using (form)
        {
            Soll(!t.GarageKnopfSichtbar, "auf der Xbox gibt es einen Garagen-Knopf");
            Soll(t.Liste.Items.Count == 119 && !Hat(t, 7), $"'fehlt' zeigt {t.Liste.Items.Count} statt 119 (Nr. 7 ist gefahren)");
            Soll(t.KopfText.Contains("1") && t.KopfText.Contains("120") && t.KopfText.Contains("119"), "Kopfzeile: " + t.KopfText);

            // Haken setzen: bleibt in der Zeile, zaehlt sofort, steht in der Datei.
            Zeile(t, 10).Checked = true;
            Zeile(t, 20).Checked = true;
            Application.DoEvents();
            Soll(t.Liste.Items.Count == 119, "abgehakte Zeilen verschwinden unter dem Mauszeiger");
            Soll(t.KopfText.Contains("3"), "die Kopfzeile zaehlt die Haken nicht: " + t.KopfText);
            var datei = Rivals.OwnedCars.Laden(besitz);
            Soll(datei.Markiert.Count == 2 && datei.Markiert.Values.All(v => v), "Haken nicht gespeichert");
            t.NeuFuellen();
            Soll(t.Liste.Items.Count == 117, $"nach dem Neuaufbau {t.Liste.Items.Count} statt 117");

            // Besessen: 7 (gefahren), 10, 20.
            t.Zeige.SelectedIndex = 1;
            Application.DoEvents();
            Soll(t.Liste.Items.Count == 3 && Hat(t, 7) && Hat(t, 10) && Hat(t, 20), $"'besessen' zeigt {t.Liste.Items.Count}");
            Soll(t.Liste.Items.Cast<ListViewItem>().All(z => z.Checked), "eine besessene Zeile ohne Haken");

            // Ein gefahrenes Auto abwaehlen (Leihwagen): zaehlt dann als fehlend -- ausdruecklich.
            Zeile(t, 7).Checked = false;
            Application.DoEvents();
            Soll(Rivals.OwnedCars.Laden(besitz).Markiert.TryGetValue(Auto(liste, 7).Schluessel, out var v7) && !v7,
                 "ein abgewaehltes gefahrenes Auto wird nicht als 'hab ich nicht' gemerkt");
            // Den Haken zuruecksetzen, der dem Automatischen entspricht: wird vergessen.
            Zeile(t, 7).Checked = true;
            Application.DoEvents();
            Soll(!Rivals.OwnedCars.Laden(besitz).Markiert.ContainsKey(Auto(liste, 7).Schluessel),
                 "ein Haken, der nichts aendert, bleibt in der Datei");

            // Alle, dann jeder Filter.
            t.Zeige.SelectedIndex = 2;
            Application.DoEvents();
            Soll(t.Liste.Items.Count == 120, "'alle' zeigt nicht alle");
            var dlc = Array.IndexOf(new[] { "autoshow", "playlist", "wheelspin", "aftermarket", "journal", "barn",
                                            "treasure", "mastery", "campaign", "gift", "loyalty", "dlc", "auction" }, "dlc") + 1;
            t.WegFilter.SelectedIndex = dlc;
            Application.DoEvents();
            Soll(t.Liste.Items.Count == 30, $"Filter DLC: {t.Liste.Items.Count} statt 30");
            t.WegFilter.SelectedIndex = 1;   // autoshow: jedes 4. + die DLC-Autos
            Application.DoEvents();
            Soll(t.Liste.Items.Count == 60, $"Filter Autoshow: {t.Liste.Items.Count} statt 60");
            t.WegFilter.SelectedIndex = 0;
            t.KlassenFilter.SelectedIndex = Array.IndexOf(Klassen, "S1") + 1;
            Application.DoEvents();
            Soll(t.Liste.Items.Count == 15, $"Filter S1: {t.Liste.Items.Count} statt 15");
            t.WegFilter.SelectedIndex = dlc;
            Application.DoEvents();
            Soll(t.Liste.Items.Count == 0, "S1 und DLC schliessen sich hier aus (i%8==4 und i%4==2)");
            t.WegFilter.SelectedIndex = 0;
            t.KlassenFilter.SelectedIndex = 0;
            t.Suche.Text = "Testwagen 11";
            Application.DoEvents();
            Soll(t.Liste.Items.Count == 10, $"Suche 'Testwagen 11': {t.Liste.Items.Count} statt 10 (110-119)");
            t.Suche.Text = "hot hatch";
            Application.DoEvents();
            Soll(t.Liste.Items.Count == 60, "die Suche findet den Typ nicht (ohne Gross/klein)");
            t.Suche.Text = "gibt es nicht";
            Application.DoEvents();
            Soll(t.Liste.Items.Count == 0, "eine Suche ohne Treffer zeigt etwas");
            Soll(t.DetailText.Length > 0, "ohne Auswahl bleibt die Einzelheit leer statt eines Hinweises");
            t.Suche.Text = string.Empty;
            Application.DoEvents();

            // Sortieren: Preis auf, Preis ab, PI, Name ab.
            static long? P(ListViewItem z) =>
                ((Rivals.CarCollection.Auto)z.Tag!).Wege.FirstOrDefault(w => w.Art == "autoshow")?.Zahl("price");
            t.Sortiere(3);
            var preise = t.Liste.Items.Cast<ListViewItem>().Select(P).ToList();
            var mit = preise.Where(p => p is not null).Select(p => p!.Value).ToList();
            Soll(mit.SequenceEqual(mit.OrderBy(p => p)) && preise.TakeWhile(p => p is not null).Count() == mit.Count,
                 "Preis aufsteigend: falsche Reihenfolge oder Autos ohne Preis nicht am Ende");
            t.Sortiere(3);
            var ab = t.Liste.Items.Cast<ListViewItem>().Select(P).Where(p => p is not null).Select(p => p!.Value).ToList();
            Soll(ab.SequenceEqual(ab.OrderByDescending(p => p)), "Preis absteigend: falsche Reihenfolge");
            t.Sortiere(1);
            var pis = t.Liste.Items.Cast<ListViewItem>().Select(z => ((Rivals.CarCollection.Auto)z.Tag!).Pi ?? 0).ToList();
            Soll(pis.SequenceEqual(pis.OrderBy(p => p)), "PI aufsteigend: falsche Reihenfolge");
            t.Sortiere(0);
            t.Sortiere(0);
            Soll(((Rivals.CarCollection.Auto)t.Liste.Items[0].Tag!).Name == "Testwagen 120", "Name absteigend beginnt nicht mit 120");

            // Auswahl bleibt ueber einen Neuaufbau; Einzelheit nennt alle Wege; Wiki-Verweis.
            Zeile(t, 42).Selected = true;
            Application.DoEvents();
            t.Sortiere(1);
            Soll(t.Liste.SelectedItems.Count == 1 && ((Rivals.CarCollection.Auto)t.Liste.SelectedItems[0].Tag!).Name == "Testwagen 042",
                 "die Auswahl geht beim Sortieren verloren");
            Soll(t.DetailText.Contains("Car Pass") && t.DetailText.Contains("1") , "Einzelheit ohne DLC-Paket: " + t.DetailText);
            Soll(Rivals.CarCollectionTab.WikiUrl("Testwagen 42 (Coupé)")
                 == "https://forza.fandom.com/wiki/Testwagen_42_%28Coup%C3%A9%29", "Wiki-Adresse falsch kodiert: "
                 + Rivals.CarCollectionTab.WikiUrl("Testwagen 42 (Coupé)"));
            Soll(geoeffnet.Count == 0, "ohne Doppelklick wurde eine Seite geoeffnet");
        }

        // PC-Modus mit gelesener Garage: der Knopf ist da, nicht erkannte Autos werden genannt.
        // (Die Testliste hat fuer jedes Auto eine id -- "nicht sicher" prueft der eigene Teil unten.)
        var g = Rivals.OwnedCars.Laden(besitz);
        g.GarageMerken(new[] { 1, 2, 3 });
        g.Speichern();
        var (f2, t2, _) = Aufbauen(liste, besitz, notizen, konsole: false);
        using (f2)
        {
            Soll(t2.GarageKnopfSichtbar, "am PC fehlt der Garagen-Knopf");
            // 1,2,3 aus der Garage + 10, 20 von Hand; 7 ist gefahren, zaehlt mit Garage aber nicht mehr.
            Soll(t2.Liste.Items.Count == 115 && !Hat(t2, 1) && Hat(t2, 7), $"PC mit Garage: {t2.Liste.Items.Count} statt 115");
            var ohne = t2.QuellText;
            g.GarageMerken(new[] { 1, 2, 3, 4242 });
            g.Speichern();
            t2.Zeigen();
            Soll(t2.QuellText.Length > ohne.Length && t2.QuellText.Contains("1"),
                 "ein nicht erkanntes Garagenauto wird nicht genannt: " + t2.QuellText);
        }
    }

    // ------------------------------------------------------------------ nicht sicher / My Cars

    private static void NichtSicher(string ordner)
    {
        var besitz = Path.Combine(ordner, "owned_unsure.json");
        // 120 Autos, die Nummern 5 und 6 ohne id.
        var json = ListeJson(120, DateTime.UtcNow).Replace("\"id\":5,", string.Empty).Replace("\"id\":6,", string.Empty);
        var liste = Rivals.CarCollection.Lesen(json) ?? throw new InvalidOperationException("Liste unlesbar");
        Soll(liste.Autos[4].AlleIds.Count == 0 && liste.Autos[5].AlleIds.Count == 0, "Testliste: Nr. 5 und 6 haben eine id");
        var g = Rivals.OwnedCars.Laden(besitz);
        g.GarageMerken(new[] { 1, 2, 777777 });
        g.Speichern();
        var (form, t, _) = Aufbauen(liste, besitz, null, konsole: false);
        using (form)
        {
            // Fehlt: alle ausser 1, 2 -- 5 und 6 darunter, aber als "nicht sicher".
            Soll(t.Liste.Items.Count == 118, $"'fehlt' zeigt {t.Liste.Items.Count} statt 118");
            Soll(Zeile(t, 5).ForeColor != Zeile(t, 7).ForeColor, "'nicht sicher' sieht aus wie 'fehlt'");
            Soll(t.KopfText.Contains("116") && t.KopfText.Contains("2"), "Kopfzeile ohne 'nicht sicher': " + t.KopfText);
            t.Zeige.SelectedIndex = 3;
            Application.DoEvents();
            Soll(t.Liste.Items.Count == 2 && Hat(t, 5) && Hat(t, 6), $"'nicht sicher' zeigt {t.Liste.Items.Count} statt 2");
            Zeile(t, 5).Selected = true;
            Application.DoEvents();
            Soll(t.DetailText.Split('\n').Length >= 3, "die Einzelheit erklaert 'nicht sicher' nicht: " + t.DetailText);

            // Im Spiel unter My Cars gesehen: wandert sofort zu 'besessen'.
            Rivals.OwnedCars.GesehenMerken(Auto(liste, 6), besitz);
            Application.DoEvents();
            Soll(t.Liste.Items.Count == 1 && Hat(t, 5), "ein in My Cars gesehenes Auto bleibt 'nicht sicher'");
            t.Zeige.SelectedIndex = 1;
            Application.DoEvents();
            Soll(Hat(t, 6) && t.Liste.Items.Count == 3, $"'besessen' zeigt {t.Liste.Items.Count} statt 3 (1, 2, 6)");
        }
    }

    // ------------------------------------------------------------------ Doppelklick

    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    private static void Klick(Control c, Point p, bool doppel)
    {
        var l = (IntPtr)((p.Y << 16) | (p.X & 0xFFFF));
        PostMessage(c.Handle, 0x0201, (IntPtr)1, l);   // WM_LBUTTONDOWN
        PostMessage(c.Handle, 0x0202, IntPtr.Zero, l); // WM_LBUTTONUP
        if (doppel)
        {
            PostMessage(c.Handle, 0x0203, (IntPtr)1, l); // WM_LBUTTONDBLCLK
            PostMessage(c.Handle, 0x0202, IntPtr.Zero, l);
        }
        var bis = DateTime.UtcNow.AddMilliseconds(400);
        while (DateTime.UtcNow < bis) { Application.DoEvents(); Thread.Sleep(10); }
    }

    private static void Doppelklick(string ordner)
    {
        var besitz = Path.Combine(ordner, "owned_dbl.json");
        var liste = Liste();
        var (form, t, geoeffnet) = Aufbauen(liste, besitz, null, konsole: true);
        using (form)
        {
            bool DoppelAuf(int zeile)
            {
                var z = t.Liste.Items[zeile];
                var name = ((Rivals.CarCollection.Auto)z.Tag!).Name;
                var vorher = z.Checked;
                var r = z.GetBounds(ItemBoundsPortion.Label);
                Klick(t.Liste, new Point(r.Left + 40, r.Top + r.Height / 2), doppel: true);
                return t.Liste.Items.Cast<ListViewItem>().First(x => ((Rivals.CarCollection.Auto)x.Tag!).Name == name).Checked != vorher;
            }

            // Gegenprobe: ohne Schutz kippt die Liste den Haken beim Doppelklick selbst.
            t.DoppelklickSchutz = false;
            var ohneSchutz = DoppelAuf(6);
            t.DoppelklickSchutz = true;
            var mitSchutz = DoppelAuf(2);
            Console.WriteLine($"  Doppelklick: ohne Schutz gekippt={ohneSchutz}, mit Schutz gekippt={mitSchutz}");
            if (ohneSchutz)
            {
                Soll(!mitSchutz, "trotz Schutz kippt ein Doppelklick den Haken");
            }
            else
            {
                Console.WriteLine("  (die Liste kippt bei nachgestellten Nachrichten nicht -- Schutz hier nicht pruefbar)");
            }
            Soll(!Rivals.OwnedCars.Laden(besitz).Markiert.ContainsKey(((Rivals.CarCollection.Auto)t.Liste.Items[2].Tag!).Schluessel),
                 "der geschuetzte Doppelklick wurde als Haken gespeichert");

            // Die Wiki-Seite der Auswahl.
            t.Liste.Items[2].Selected = true;
            t.WikiFuerAuswahl();
            Soll(geoeffnet.Count == 1 && geoeffnet[0].StartsWith("https://forza.fandom.com/wiki/Testwagen_"),
                 "die Wiki-Seite der Auswahl wird nicht geoeffnet: " + string.Join(" ", geoeffnet));
            t.Liste.SelectedItems.Clear();
            t.WikiFuerAuswahl();
            Soll(geoeffnet.Count == 1, "ohne Auswahl wird trotzdem eine Seite geoeffnet");

            // Ein einfacher Klick aufs Kaestchen muss weiter abhaken.
            var k = t.Liste.Items[4];
            var name4 = ((Rivals.CarCollection.Auto)k.Tag!).Name;
            var b = k.GetBounds(ItemBoundsPortion.Entire);
            Klick(t.Liste, new Point(b.Left + 9, b.Top + b.Height / 2), doppel: false);
            var jetzt = t.Liste.Items.Cast<ListViewItem>().First(x => ((Rivals.CarCollection.Auto)x.Tag!).Name == name4).Checked;
            Console.WriteLine($"  Klick aufs Kaestchen: abgehakt={jetzt}");
            Soll(jetzt, "ein Klick aufs Kaestchen hakt nicht mehr ab (der Doppelklick-Schutz blockiert zu viel)");
            Soll(Rivals.OwnedCars.Laden(besitz).Markiert.TryGetValue(liste.Autos.First(a => a.Name == name4).Schluessel, out var h) && h,
                 "ein Klick aufs Kaestchen wird nicht gespeichert");
        }
    }

    // ------------------------------------------------------------------ Server

    private static async Task Server(string ordner)
    {
        var horcher = new TcpListener(IPAddress.Loopback, 0);
        horcher.Start();
        var port = ((IPEndPoint)horcher.LocalEndpoint).Port;
        var antwort = string.Empty;
        var status = 200;
        using var ende = new CancellationTokenSource();
        var dienst = Task.Run(async () =>
        {
            while (!ende.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await horcher.AcceptTcpClientAsync(ende.Token); } catch (Exception) { break; }
                using (c)
                {
                    var s = c.GetStream();
                    var puffer = new byte[8192];
                    _ = await s.ReadAsync(puffer);
                    var rumpf = Encoding.UTF8.GetBytes(antwort);
                    var kopf = $"HTTP/1.1 {status} X\r\nContent-Type: application/json\r\nContent-Length: {rumpf.Length}\r\nConnection: close\r\n\r\n";
                    await s.WriteAsync(Encoding.ASCII.GetBytes(kopf));
                    await s.WriteAsync(rumpf);
                }
            }
        });
        var basis = $"http://127.0.0.1:{port}/";
        var ziel = Path.Combine(ordner, "cache", "cars.json");
        try
        {
            antwort = ListeJson(150, DateTime.UtcNow);
            var neu = await Rivals.CarCollection.VomServerAsync(basis, null, default, ziel);
            Soll(neu is { Autos.Count: 150 } && File.Exists(ziel), "eine gute Liste vom Server wird nicht angenommen oder nicht abgelegt");
            Soll(await Rivals.CarCollection.VomServerAsync(basis, neu, default, ziel) is null, "dieselbe Liste gilt als neuer");
            var stand = File.ReadAllText(ziel);

            antwort = ListeJson(40, DateTime.UtcNow.AddHours(1));
            Soll(await Rivals.CarCollection.VomServerAsync(basis, neu, default, ziel) is null, "eine halbe Liste wird angenommen");
            antwort = "<html>Fehler</html>";
            Soll(await Rivals.CarCollection.VomServerAsync(basis, neu, default, ziel) is null, "HTML statt JSON wird angenommen");
            status = 503;
            antwort = "{\"ok\":false}";
            Soll(await Rivals.CarCollection.VomServerAsync(basis, neu, default, ziel) is null, "eine 503 wird angenommen");
            status = 200;
            antwort = ListeJson(150, DateTime.UtcNow.AddHours(-30));
            Soll(await Rivals.CarCollection.VomServerAsync(basis, neu, default, ziel) is null, "eine AELTERE Liste ersetzt eine neuere");
            Soll(File.ReadAllText(ziel) == stand, "eine abgelehnte Antwort hat die abgelegte Liste veraendert");
            Soll(await Rivals.CarCollection.VomServerAsync("", null, default, ziel) is null, "ohne Server-Adresse wird gefragt");
        }
        finally
        {
            ende.Cancel();
            horcher.Stop();
            try { await dienst; } catch (Exception) { }
        }
        // Ein Anschluss, auf dem niemand horcht: null, und schnell.
        var uhr = Stopwatch.StartNew();
        Soll(await Rivals.CarCollection.VomServerAsync($"http://127.0.0.1:{port}", null, default, ziel, TimeSpan.FromSeconds(5)) is null,
             "ein toter Server liefert etwas");
        Soll(uhr.Elapsed < TimeSpan.FromSeconds(6), $"ein toter Server haelt {uhr.Elapsed.TotalSeconds:0.0} s auf");
    }

    // ------------------------------------------------------------------ die echte Liste

    private static void EchteListe(string ordner)
    {
        if (Rivals.CarCollection.PaketPfad() is not { } pfad)
        {
            Console.WriteLine("  (keine mitgelieferte Liste gefunden -- Tempo nicht gemessen)");
            return;
        }
        var liste = Rivals.CarCollection.Lesen(File.ReadAllText(pfad)) ?? throw new InvalidOperationException("mitgelieferte Liste unlesbar");
        Soll(liste.Autos.Count >= 600, "mitgelieferte Liste hat nur " + liste.Autos.Count);
        Soll(liste.Autos.Select(a => a.Schluessel).Distinct().Count() == liste.Autos.Count, "zwei Autos mit gleichem Namen und Jahr");
        var alleIds = liste.Autos.SelectMany(a => a.AlleIds).ToList();
        Soll(alleIds.Count == alleIds.Distinct().Count(), "eine car_id steht bei zwei Autos");
        foreach (var a in liste.Autos)
        {
            foreach (var w in a.Wege)
            {
                var t = Rivals.CarCollectionTab.WegLang(w);
                Soll(!string.IsNullOrWhiteSpace(t) && !t.Contains("{0}"), $"leerer oder roher Satz fuer {a.Name}: '{t}'");
            }
        }
        var (form, t2, _) = Aufbauen(liste, Path.Combine(ordner, "owned_real.json"), null, konsole: false);
        using (form)
        {
            t2.Zeige.SelectedIndex = 2;
            var zeiten = new List<(double Gesamt, double Rechnen, double Liste)>();
            for (var i = 0; i < 15; i++)
            {
                var u = Stopwatch.StartNew();
                t2.NeuFuellen();
                zeiten.Add((u.Elapsed.TotalMilliseconds, t2.RechnenMs, t2.ListeMs));
            }
            var sortiert = zeiten.OrderBy(z => z.Gesamt).ToList();
            var jeAufbau = sortiert[sortiert.Count / 2].Gesamt;
            Console.WriteLine($"  Neuaufbau 647 Zeilen: min {sortiert[0].Gesamt:0} / median {jeAufbau:0} / max {sortiert[^1].Gesamt:0} ms"
                              + $"  (median rechnen {zeiten.Select(z => z.Rechnen).OrderBy(x => x).ElementAt(7):0} ms,"
                              + $" Liste fuellen {zeiten.Select(z => z.Liste).OrderBy(x => x).ElementAt(7):0} ms)");
            var uhr = Stopwatch.StartNew();
            foreach (var zeichen in "Ferrari") { t2.Suche.Text += zeichen; Application.DoEvents(); }
            var tippen = uhr.Elapsed.TotalMilliseconds;
            Console.WriteLine($"  echte Liste: {liste.Autos.Count} Autos, Neuaufbau {jeAufbau:0} ms, 7 Tastendruecke {tippen:0} ms, "
                              + $"Ferrari: {t2.Liste.Items.Count}");
            Soll(jeAufbau < 400, $"ein Neuaufbau dauert {jeAufbau:0} ms");
            Soll(t2.Liste.Items.Count > 10, "die Suche nach Ferrari findet fast nichts");
        }
    }
}
