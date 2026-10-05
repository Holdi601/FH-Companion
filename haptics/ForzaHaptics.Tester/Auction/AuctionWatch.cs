using System.Diagnostics;
using System.Drawing;
using ForzaHaptics.Tester.Rivals;

namespace ForzaHaptics.Tester.Auction;

/// <summary>
/// Haelt die Auktionen im Blick: Warnungen 5 und 2 Minuten vor Ende. Geboten wird nie -- nur gelesen.
/// </summary>
/// <remarks>
/// Eine Instanz fuer die ganze App (<see cref="Jetzt"/>). Der Takt laeuft alle 15 Sekunden auf einem
/// Hintergrundfaden; alles, was einen Schirm braucht, laeuft ueber die Rueckrufe, die die App setzt
/// (Hinweis auf dem Schirm, Hinweis im Infobereich).
///
/// Gelesen wird aus zwei Quellen: was das Overlay sieht, waehrend der Nutzer selbst im Auktionshaus ist
/// (<see cref="BildGesehen"/>), und was der <see cref="AuctionBidder"/> liest, wenn der Nutzer ihn mit
/// "Check my bids now" einmal hinschickt.
/// </remarks>
internal sealed class AuctionWatch : IDisposable
{
    private static readonly Lazy<AuctionWatch> Einzig = new(() => new AuctionWatch());

    public static AuctionWatch Jetzt => Einzig.Value;

    private readonly object _schloss = new();
    private readonly System.Threading.Timer? _takt;
    private readonly WindowsOcr _ocr = new();
    private CancellationTokenSource? _bieter;
    private DateTime _letztesBildUtc = DateTime.MinValue;
    private int _liestBild;

    /// <summary>
    /// Ohne Takt: keine Warnungen. Fuer die Vorschau des Hauptfensters (--main-preview) und die
    /// Befehlszeile -- vor dem ersten Zugriff auf <see cref="Jetzt"/> setzen.
    /// </summary>
    internal static bool OhneTakt { get; set; }

    /// <summary>Der Stand -- immer unter <see cref="Mit"/> lesen oder aendern.</summary>
    public AuctionStore Store { get; private set; }

    /// <summary>Hinweis auf dem Schirm (Overlay): Titel, Text.</summary>
    public Action<string, string>? AufDemSchirm { get; set; }

    /// <summary>Hinweis im Infobereich von Windows: Titel, Text.</summary>
    public Action<string, string>? ImInfobereich { get; set; }

    /// <summary>Etwas hat sich geaendert (fuer den Reiter). Kommt auf einem Hintergrundfaden.</summary>
    public event Action? Geaendert;

    /// <summary>Was der Leser von "My Bids" gerade tut (fuer den Reiter); leer, wenn er nicht laeuft.</summary>
    public string BieterStatus { get; private set; } = string.Empty;

    public bool BieterLaeuft => _bieter is not null;

    private AuctionWatch()
    {
        Store = AuctionStore.Laden();
        // Auch MainForm.NurVorschau selbst: greift etwas vor dem Reiter auf Jetzt zu (das Overlay),
        // ist OhneTakt noch nicht gesetzt -- ein Bild des Fensters warnt trotzdem nicht.
        if (!OhneTakt && !MainForm.NurVorschau) { _takt = new System.Threading.Timer(_ => Takt(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15)); }
    }

    /// <summary>Den Stand unter der Sperre lesen -- ohne Speichern und ohne <see cref="Geaendert"/> (fuer den Reiter).</summary>
    public T Lies<T>(Func<AuctionStore, T> lesen)
    {
        lock (_schloss) { return lesen(Store); }
    }

    /// <summary>Den Stand unter der Sperre bearbeiten und speichern.</summary>
    public T Mit<T>(Func<AuctionStore, T> tun)
    {
        T r;
        lock (_schloss)
        {
            r = tun(Store);
            Store.Speichern();
        }
        Geaendert?.Invoke();
        return r;
    }

    /// <summary>
    /// Das Overlay sah einen Auktionshaus-Schirm (hoechstens alle 8 Sekunden ausgewertet). Karten mit
    /// einem Status (OUTBID, WINNING, SOLD ...) sind eigene Gebote; alle Karten gehen in die Preisgeschichte.
    /// </summary>
    public void BildGesehen(Bitmap voll1080)
    {
        // Das Overlay schickt aus Task.Run: dauert eine Lesung laenger als der Abstand der Bilder,
        // liefe sonst eine zweite auf derselben Texterkennung los.
        if (Interlocked.Exchange(ref _liestBild, 1) == 1) { return; }
        try
        {
            if (DateTime.UtcNow - _letztesBildUtc < TimeSpan.FromSeconds(8)) { return; }
            _letztesBildUtc = DateTime.UtcNow;
            var zeilen = _ocr.Read(voll1080);
            var schirm = AuctionReader.Einordnen(zeilen);
            if (schirm == AuktionsSchirm.Menue)
            {
                // Welcher Eintrag ist markiert? Fuehrt ENTER zu "My Bids", ist die naechste Liste die eigene.
                _zuletztMeineGebote = AuctionReader.MenueMarkiert(voll1080, zeilen, "my_bids", "My Bids");
                return;
            }
            if (schirm != AuktionsSchirm.Liste) { return; }
            var karten = AuctionReader.LiesKarten(voll1080, _ocr);
            // Eigene Gebote: die Liste kam aus "My Bids" -- oder eine Karte traegt OUTBID/WINNING
            // (das steht nur bei Auktionen, auf die der Nutzer geboten hat).
            Einarbeiten(karten, _zuletztMeineGebote || karten.Any(k => k.Status is "OUTBID" or "WINNING"));
        }
        catch (Exception) { }
        finally
        {
            Volatile.Write(ref _liestBild, 0);
        }
    }

    private bool _zuletztMeineGebote;

    /// <summary>
    /// Gelesene Karten einarbeiten (aus dem Overlay oder vom Bieter). Nur Karten aus "My Bids" sind
    /// eigene Gebote -- im Suchergebnis stehen auch fremde beendete Auktionen (SOLD!, NOT SOLD!).
    /// </summary>
    public void Einarbeiten(IReadOnlyList<AuktionsKarte> karten, bool meineGebote)
    {
        var jetzt = DateTime.UtcNow;
        Mit(s =>
        {
            if (meineGebote) { s.MeineGebote(karten.Where(k => k.Status is not null).ToList(), jetzt); }
            s.Gesehen(karten, meineGebote ? "my_bids" : "search", jetzt);
            return 0;
        });
    }

    private void Takt()
    {
        try
        {
            List<(VerfolgteAuktion Auktion, int Minuten)> faellig;
            bool verfallen;
            lock (_schloss)
            {
                verfallen = Store.Verfallen(DateTime.UtcNow);
                faellig = Store.FaelligeWarnungen(DateTime.UtcNow);
                if (faellig.Count > 0 || verfallen) { Store.Speichern(); }
            }
            foreach (var (a, minuten) in faellig)
            {
                var titel = string.Format(Loc.T("Auction ends in {0} minutes"), minuten);
                var text = $"{a.Auto} · {Status(a.Status)}" + (a.Gebot is { } g ? $" · {g:n0} CR" : string.Empty);
                AufDemSchirm?.Invoke(titel, text);
                ImInfobereich?.Invoke(titel, text);
            }
            // Auch ein Verfall: sonst zeigte der Reiter die Auktion bis zum Neustart als laufend mit "0:00".
            if (faellig.Count > 0 || verfallen) { Geaendert?.Invoke(); }
        }
        catch (Exception) { }
    }

    internal static string Status(string? s) => s switch
    {
        "OUTBID" => Loc.T("outbid"),
        "WINNING" => Loc.T("winning"),
        // SOLD! klebt auf JEDER verkauften Karte in My Bids, auch wenn ein anderer sie kaufte: kein Sieg.
        // Gewonnen/verloren nur aus einem WON/LOST-Abzeichen.
        "SOLD" => Loc.T("sold"),
        "WON" => Loc.T("won"),
        "LOST" => Loc.T("lost"),
        // NOT_SOLD (liest der Leser): niemand bot -- fuer den Reiter schlicht beendet.
        "ENDED" or "NOT_SOLD" => Loc.T("ended"),
        _ => Loc.T("unknown"),
    };

    internal static bool SpielLaeuft() => Process.GetProcessesByName(GameWatch.DefaultProcessName).Length > 0;

    /// <summary>
    /// Den Leser losschicken (Knopf "Check my bids now" im Reiter): zum Auktionshaus fahren, "My Bids"
    /// einmal lesen und zurueck. Er bietet nie.
    /// </summary>
    public bool StarteBieter()
    {
        CancellationToken stop;
        lock (_schloss)
        {
            if (_bieter is not null) { return false; }
            _bieter = new CancellationTokenSource();
            stop = _bieter.Token;
        }
        var t = new Thread(() =>
        {
            try
            {
                // Der Griff fuer den ganzen Lauf, auf diesem Faden: kein Scan und kein Tune-Loeschen dazwischen.
                using var griff = Scan.SpielSperre.Nehmen("auction check");
                if (griff is null)
                {
                    BieterStatus = Loc.T("Another automation (board scan or auction check) is driving the game. Nothing was pressed.");
                    return;
                }
                var bieter = new AuctionBidder(this, s => { BieterStatus = s; Geaendert?.Invoke(); }, stop);
                bieter.Laufe();
            }
            catch (Exception e)
            {
                BieterStatus = e.Message;
            }
            finally
            {
                lock (_schloss) { _bieter = null; }
                Geaendert?.Invoke();
            }
        })
        { IsBackground = true, Name = "AuctionBidder" };
        t.Start();
        return true;
    }

    /// <summary>Den laufenden Leser anhalten (Knopf "Stop", Schliessen der App).</summary>
    public void StoppeBieter() => _bieter?.Cancel();

    public void Dispose()
    {
        _takt?.Dispose();
        StoppeBieter();
    }
}
