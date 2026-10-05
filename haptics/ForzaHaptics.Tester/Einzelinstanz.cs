using System.Security.Cryptography;
using System.Text;

namespace ForzaHaptics.Tester;

/// <summary>
/// Hoechstens ein Fenster je Kopie der App -- ein zweiter Start holt das erste hervor.
/// </summary>
/// <remarks>
/// Seit 2026-09-27, mit "Mit Forza starten": die App wartet dann unsichtbar im
/// Infobereich, und wer sie ueber den Schreibtisch startet, sieht sie nicht laufen.
/// Ein zweiter Prozess waere nicht nur ein zweites Fenster -- der UDP-Port 5300 hat
/// genau einen Zuhoerer (siehe OverlayController), der zweite bekaeme keine
/// Telemetrie, und zwei Prozesse trieben denselben Controller.
///
/// JE KOPIE, nicht je Rechner: der Name haengt am Programmordner. Ein Testbau neben
/// der installierten App (dist/ neben dem entpackten Paket) laeuft weiter parallel,
/// so wie bisher -- nur dieselbe Kopie zweimal nicht.
///
/// Der zweite Start weckt den ersten ueber ein benanntes Ereignis und beendet sich.
/// Beides liegt im Namensraum der Sitzung ("Local\"): ein anderer angemeldeter
/// Nutzer auf demselben Rechner hat seine eigene App.
/// </remarks>
internal sealed class Einzelinstanz : IDisposable
{
    private readonly Mutex _riegel;
    private readonly EventWaitHandle _wecker;
    private Thread? _horcher;
    private volatile bool _vorbei;

    private Einzelinstanz(Mutex riegel, EventWaitHandle wecker, bool erste)
    {
        _riegel = riegel;
        _wecker = wecker;
        Erste = erste;
    }

    /// <summary>Diese Instanz ist die einzige (und haelt den Riegel).</summary>
    public bool Erste { get; }

    /// <summary>Der Name fuer diese Kopie: aus dem Programmordner abgeleitet.</summary>
    internal static string NameFuer(string ordner)
    {
        var norm = Path.GetFullPath(ordner).TrimEnd('\\', '/').ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(norm)))[..16];
        return @"Local\FHCompanion." + hash;
    }

    public static Einzelinstanz Anmelden(string? ordner = null)
    {
        var name = NameFuer(ordner ?? AppContext.BaseDirectory);
        var riegel = new Mutex(initiallyOwned: true, name + ".Instanz", out var neu);
        var wecker = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Zeigen");
        return new Einzelinstanz(riegel, wecker, neu);
    }

    /// <summary>Die erste Instanz bitten, ihr Fenster zu zeigen.</summary>
    public void ErsteWecken() => _wecker.Set();

    /// <summary>Auf das Wecken horchen (nur die erste Instanz) und dann <paramref name="tun"/> rufen.</summary>
    public void Horchen(Action tun)
    {
        if (!Erste || _horcher is not null) { return; }
        _horcher = new Thread(() =>
        {
            while (!_vorbei)
            {
                try
                {
                    if (_wecker.WaitOne(1000) && !_vorbei) { tun(); }
                }
                catch (Exception)
                {
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "Einzelinstanz",
        };
        _horcher.Start();
    }

    public void Dispose()
    {
        _vorbei = true;
        try
        {
            if (Erste) { _riegel.ReleaseMutex(); }
        }
        catch (Exception)
        {
            // Der Riegel gehoert einem anderen Faden -- beim Beenden gleichgueltig.
        }
        _riegel.Dispose();
        _wecker.Dispose();
    }
}
