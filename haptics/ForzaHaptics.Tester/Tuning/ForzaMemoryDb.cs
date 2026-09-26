using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ForzaHaptics.Tester.Tuning;

/// <summary>
/// Die SQLite-Datenbanken des laufenden Spiels aus dem Arbeitsspeicher holen.
/// </summary>
/// <remarks>
/// ## Warum das geht
///
/// Forza legt auf der Platte keine `.db`-Datei ab -- im Steam-Ordner steht nur die
/// exe. Im Heap des laufenden Spiels stehen aber fertige SQL-Abfragen, und wo
/// Abfragen laufen, gibt es eine Datenbank. Am 2026-08-30 wurden dort sechs
/// SQLite-Koepfe gefunden; zwei davon oeffnen sich mit `sqlite3` und tragen neun
/// Tabellen, darunter `Career_Garage` mit einer Zeile je Auto.
///
/// Diese Klasse ist die Uebertragung von `scripts/dump_sqlite_from_memory.py` nach
/// C#. Bewusst ohne Python und ohne Frida: die App wird als eigenstaendiges Paket
/// ausgeliefert, und ein Werkzeug, das eine fremde Laufzeitumgebung voraussetzt,
/// liefe beim Nutzer nie (siehe die Erfahrung mit dem Namensabgleich, der zwei
/// Wochen lang gebaut dalag und nie aufgerufen wurde).
///
/// ## Was ein Kopf nicht beweist
///
/// Die Zeichenkette "SQLite format 3" steht auch als blosser Text im Speicher, etwa
/// in einer Fehlermeldung der Bibliothek. Darum wird der Kopf GEPRUEFT: Seitengroesse
/// muss eine Zweierpotenz zwischen 512 und 65536 sein, Lese- und Schreibversion 1
/// oder 2. Erst dann wird gelesen.
///
/// ## Und was der Speicher nicht hergibt
///
/// Die grossen Abbilder (52 und 22 MB) lassen sich zwar herausholen, sind aber leer:
/// eine In-Memory-SQLite ist ein Seiten-Zwischenspeicher, kein zusammenhaengendes
/// Dateiabbild. Nur was als ganzes Abbild geladen wurde, kommt brauchbar heraus --
/// und das ist genau die Garage. Der TEILEKATALOG (die `List_Upgrade*`-Tabellen, die
/// aus einer Teile-Nummer einen Namen machen wuerden) gehoert zu den leeren.
/// </remarks>
internal static class ForzaMemoryDb
{
    /// <summary>
    /// Der Prozess, in dem gesucht wird.
    /// </summary>
    /// <remarks>
    /// EINSTELLBAR, damit sich der Suchweg ohne Forza pruefen laesst. Der Scanner
    /// ist eine Portierung nach C# und hatte vor dem 2026-09-14 noch nie einen
    /// echten Prozess abgesucht -- "gebaut, aber nie am Ziel gelaufen" ist in diesem
    /// Projekt schon zweimal teuer geworden. Der Selbsttest laedt darum eine
    /// SQLite-Datei in den EIGENEN Speicher und sucht sich selbst ab; damit sind
    /// Regionsdurchlauf, Kopfpruefung, Lesen und Schreiben belegt.
    /// </remarks>
    private const string ProcessName = "forzahorizon6";
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SQLite format 3\0");
    private const int HeaderSize = 100;

    /// <summary>Groesser als das ist kein Abbild mehr, sondern ein Irrtum.</summary>
    private const long MaxDbBytes = 256L << 20;

    /// <summary>Kandidaten ueber dieser Groesse werden gar nicht erst gelesen.</summary>
    /// <remarks>
    /// Die Garage ist rund 1 MB gross. Die 52-MB- und 22-MB-Abbilder sind gemessen
    /// leer; sie zu schreiben kostet Sekunden und bringt nichts. Wer sie doch will,
    /// nimmt das Python-Werkzeug.
    /// </remarks>
    private const long ReadLimitBytes = 24L << 20;

    internal sealed record Fund(string Path, long Address, int PageSize, uint PageCount);

    public static bool GameRunning => Process.GetProcessesByName(ProcessName).Length > 0;

    /// <summary>Wie <see cref="Dump"/>, aber in einem beliebigen Prozess.</summary>
    public static List<Fund> DumpFrom(int pid, string ordner,
                                      Action<string>? sagen = null,
                                      Func<string, bool>? genug = null) =>
        Sammeln(pid, ordner, sagen, genug);

    /// <summary>
    /// Alle plausiblen Datenbanken in eine eigene Ablage schreiben.
    /// </summary>
    /// <remarks>
    /// Die kleinsten zuerst: die Garage ist klein, die leeren Riesen sind gross, und
    /// wer zuerst findet was er sucht, spart dem Nutzer das Warten.
    /// </remarks>
    public static List<Fund> Dump(string ordner, Action<string>? sagen = null,
                                  Func<string, bool>? genug = null)
    {
        var spiel = Process.GetProcessesByName(ProcessName).FirstOrDefault();
        if (spiel is null)
        {
            sagen?.Invoke("forzahorizon6.exe laeuft nicht.");
            return new List<Fund>();
        }
        return Sammeln(spiel.Id, ordner, sagen, genug);
    }

    /// <summary>
    /// Den Speicher eines Prozesses absuchen und brauchbare Abbilder herausholen.
    /// </summary>
    /// <remarks>
    /// ## Warum hier waehrend der Suche schon geschrieben und geprueft wird
    ///
    /// Forza belegte am 2026-09-14 gemessen **14,33 GB**. Erst alles abzusuchen und
    /// dann zu pruefen heisst, immer die vollen 14 GB zu lesen -- auch wenn die
    /// Garage nach zwei Gigabyte schon dalag. Darum wird jeder Fund sofort
    /// geschrieben und dem Aufrufer vorgelegt; sagt der "das genuegt", hoert die
    /// Suche auf.
    /// </remarks>
    private static List<Fund> Sammeln(int pid, string ordner, Action<string>? sagen,
                                      Func<string, bool>? genug = null)
    {
        var ergebnis = new List<Fund>();
        var handle = OpenProcess(ProcessVmRead | ProcessQueryInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            sagen?.Invoke("Der Prozess laesst sich nicht oeffnen (Fehler "
                          + Marshal.GetLastWin32Error()
                          + "). Meist hilft es, dieses Programm als Administrator zu "
                          + "starten.");
            return ergebnis;
        }

        try
        {
            Directory.CreateDirectory(ordner);
            Suchen(handle, ordner, ergebnis, sagen, genug);
        }
        finally
        {
            CloseHandle(handle);
        }
        return ergebnis;
    }

    private static void Suchen(IntPtr handle, string ordner, List<Fund> ergebnis,
                               Action<string>? sagen, Func<string, bool>? genug)
    {
        ulong adresse = 0;
        var mbi = new MemoryBasicInformation();
        var groesse = (uint)Marshal.SizeOf<MemoryBasicInformation>();
        var puffer = new byte[(8 << 20) + HeaderSize];
        long durchsucht = 0;
        long gemeldet = 0;
        var gesehen = new HashSet<long>();

        while (adresse < 0x00007FFFFFFFFFFFUL)
        {
            if (VirtualQueryEx(handle, (IntPtr)adresse, out mbi, groesse) == 0) { break; }
            var basis = (ulong)mbi.BaseAddress.ToInt64();
            var laenge = (ulong)mbi.RegionSize.ToInt64();
            if (basis + laenge <= adresse) { break; }

            // NUR BESCHREIBBARE, NICHT AUSFUEHRBARE BEREICHE. Ein Datenbankabbild
            // liegt auf dem Heap, nicht im Code. Das spart bei 14 GB spuerbar Zeit,
            // ohne etwas zu verlieren -- gemessen wird der Rest weiterhin ganz.
            var schreibbar = (mbi.Protect & (PageReadWrite | PageWriteCopy)) != 0;
            var brauchbar = mbi.State == MemCommit && mbi.Type == MemPrivate
                            && schreibbar
                            && (mbi.Protect & PageNoAccess) == 0
                            && (mbi.Protect & PageGuard) == 0
                            && laenge >= 4096;
            if (brauchbar)
            {
                ulong offset = 0;
                while (offset < laenge)
                {
                    var stueck = (int)Math.Min((ulong)(8 << 20), laenge - offset);
                    var gelesen = Lesen(handle, basis + offset, puffer,
                                        stueck + HeaderSize);
                    durchsucht += stueck;
                    if (gelesen > HeaderSize
                        && Kopfstellen(handle, puffer, gelesen, basis + offset,
                                       ordner, ergebnis, gesehen, genug))
                    {
                        sagen?.Invoke($"gefunden nach {durchsucht / (1024.0 * 1024 * 1024):F1} GB.");
                        return;
                    }
                    offset += (ulong)stueck;

                    // Alle zwei Gigabyte ein Lebenszeichen: eine Suche ueber 14 GB
                    // ohne jede Rueckmeldung sieht aus wie ein haengendes Programm.
                    if (durchsucht - gemeldet >= (2L << 30))
                    {
                        gemeldet = durchsucht;
                        sagen?.Invoke($"{durchsucht / (1024.0 * 1024 * 1024):F1} GB "
                                      + $"durchsucht, {ergebnis.Count} Abbild(er) bisher ...");
                    }
                }
            }
            adresse = basis + laenge;
        }
        sagen?.Invoke($"{durchsucht / (1024.0 * 1024 * 1024):F1} GB durchsucht, "
                      + $"{ergebnis.Count} Abbild(er).");
    }

    /// <summary>
    /// Die Kennung in einem Block suchen, Funde gleich herausschreiben.
    /// </summary>
    /// <remarks>
    /// ## `Span.IndexOf` statt einer eigenen Schleife -- das war der Unterschied
    /// ## zwischen Minuten und Stunden
    ///
    /// Mein erster Entwurf lief Byte fuer Byte durch den Block und verglich von Hand.
    /// Bei Forzas 14,33 GB sind das vierzehn Milliarden Durchlaeufe in verwaltetem
    /// Code -- der Nutzer drueckte am 2026-09-14 auf "Read", und nach hundert
    /// Sekunden CPU war die App immer noch am Suchen, ohne ein Zeichen von sich zu
    /// geben.
    ///
    /// Das Python-Werkzeug hatte das nie: `bytes.find()` ist eine native,
    /// vektorisierte Suche. Ihr Gegenstueck in .NET ist
    /// <c>Span&lt;byte&gt;.IndexOf(ReadOnlySpan&lt;byte&gt;)</c> -- derselbe
    /// Befehlssatz, dieselbe Groessenordnung. Die Zeile, die eine Portierung
    /// unbrauchbar machte, war die eine, die ich nicht mit uebernommen hatte.
    /// </remarks>
    private static bool Kopfstellen(IntPtr handle, byte[] daten, int laenge, ulong basis,
                                    string ordner, List<Fund> ergebnis,
                                    HashSet<long> gesehen, Func<string, bool>? genug)
    {
        var block = daten.AsSpan(0, laenge);
        var ab = 0;
        while (ab + HeaderSize <= laenge)
        {
            var at = block[ab..].IndexOf(Magic.AsSpan());
            if (at < 0) { break; }
            at += ab;
            ab = at + 1;
            if (at + HeaderSize > laenge) { break; }

            var kopf = Kopf(daten, at);
            if (kopf is null) { continue; }

            var adresse = (long)(basis + (ulong)at);
            if (!gesehen.Add(adresse)) { continue; }
            var bytes = (long)kopf.PageSize * kopf.PageCount;
            // Seitenzahl unbekannt: eine Vorgabe lesen. 16 MB decken alles ab, was
            // hier je brauchbar war (die Garage misst rund 1 MB) und bleiben unter
            // der Lesegrenze.
            if (bytes <= 0) { bytes = Math.Min(16L << 20, ReadLimitBytes); }
            if (bytes > ReadLimitBytes) { continue; }

            var ziel = Path.Combine(ordner, $"forza_{adresse:x12}.db");
            if (!Schreiben(handle, adresse, bytes, ziel)) { continue; }
            ergebnis.Add(kopf with { Path = ziel, Address = adresse });
            if (genug is not null && genug(ziel)) { return true; }
        }
        return false;
    }

    /// <summary>Seitengroesse und Seitenzahl -- oder null, wenn der Kopf nicht taugt.</summary>
    private static Fund? Kopf(byte[] daten, int at)
    {
        int roh = (daten[at + 16] << 8) | daten[at + 17];
        var seite = roh == 1 ? 65536 : roh;
        if (seite < 512 || seite > 65536 || (seite & (seite - 1)) != 0) { return null; }
        var schreib = daten[at + 18];
        var lese = daten[at + 19];
        if (schreib is not (1 or 2) || lese is not (1 or 2)) { return null; }
        uint zahl = ((uint)daten[at + 28] << 24) | ((uint)daten[at + 29] << 16)
                    | ((uint)daten[at + 30] << 8) | daten[at + 31];
        // EINE UNBRAUCHBARE SEITENZAHL IST KEIN GRUND ZU VERWERFEN.
        //
        // Im Speicher steht oft ein Kopf, dessen Seitenzahl nie frisch geschrieben
        // wurde -- dann ist das Feld 0 oder unsinnig gross. Das Python-Werkzeug
        // sagt dazu ausdruecklich: "Seitenzahl unbrauchbar ... dann wird spaeter
        // blockweise gelesen". Genau diese Zeile hatte ich beim Portieren nicht
        // mitgenommen, und darum fand die C#-Fassung am 2026-09-14 vier Koepfe und
        // keine einzige Garage.
        //
        // 0 heisst hier: nimm eine Vorgabe und lass SQLite entscheiden, ob etwas
        // Brauchbares dabei herauskommt.
        if (zahl == 0 || (long)seite * zahl > MaxDbBytes) { zahl = 0; }
        return new Fund(string.Empty, 0, seite, zahl);
    }

    private static bool Schreiben(IntPtr handle, long adresse, long gesamt, string ziel)
    {
        try
        {
            using var datei = File.Create(ziel);
            var puffer = new byte[4 << 20];
            long geschrieben = 0;
            while (geschrieben < gesamt)
            {
                var stueck = (int)Math.Min(puffer.Length, gesamt - geschrieben);
                var n = Lesen(handle, (ulong)(adresse + geschrieben), puffer, stueck);
                if (n <= 0) { break; }
                datei.Write(puffer, 0, n);
                geschrieben += n;
            }
            return geschrieben > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static int Lesen(IntPtr handle, ulong adresse, byte[] puffer, int laenge)
    {
        if (laenge > puffer.Length) { laenge = puffer.Length; }
        if (!ReadProcessMemory(handle, (IntPtr)adresse, puffer, laenge, out var gelesen))
        {
            return (int)gelesen;
        }
        return (int)gelesen;
    }

    // ------------------------------------------------------------------ //
    // Win32
    // ------------------------------------------------------------------ //

    private const int ProcessVmRead = 0x0010;
    private const int ProcessQueryInformation = 0x0400;
    private const uint MemCommit = 0x1000;
    private const uint MemPrivate = 0x20000;
    private const uint PageNoAccess = 0x01;
    private const uint PageReadWrite = 0x04;
    private const uint PageWriteCopy = 0x08;
    private const uint PageGuard = 0x100;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public int __alignment1;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public int __alignment2;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int VirtualQueryEx(IntPtr process, IntPtr address,
                                             out MemoryBasicInformation info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address,
                                                 byte[] buffer, int size,
                                                 out IntPtr read);
}
