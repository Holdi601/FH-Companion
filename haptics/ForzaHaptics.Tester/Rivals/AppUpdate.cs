using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Sieht nach, ob auf dem Server eine neuere App liegt -- und ersetzt sie, wenn der
/// Nutzer zustimmt.
/// </summary>
/// <remarks>
/// ## Warum ueberhaupt
///
/// Die App wird als ZIP weitergegeben. Wer sie einmal entpackt hat, hat sie fuer
/// immer in genau dieser Fassung -- auch dann noch, wenn ein Fehler laengst behoben
/// ist. Genau das ist hier passiert: ein Delta, das im freien Fahren Unsinn zeigte,
/// stand wochenlang auf Rechnern, waehrend die Ursache hier schon bekannt war.
///
/// ## Warum mit Rueckfrage und nicht still
///
/// Weil die App waehrend des Spielens laeuft. Ein Austausch heisst Beenden und
/// Neustarten, und das mitten in einer Runde zu tun, waere ein Fehler, den niemand
/// erwartet. Der Nutzer entscheidet, WANN.
///
/// ## Was verglichen wird
///
/// Die Kennung aus <c>app.meta.json</c> -- eine Pruefsumme ueber den Inhalt des
/// Pakets, vom Paketbauer vergeben. Nicht das Datum: zwei Pakete desselben Tages
/// sind verschieden, und ein zurueckgenommener Stand traegt ein aelteres Datum als
/// das, was schon installiert ist. Und nicht die Dateizeit: eine Uebertragung darf
/// sie veraendern.
///
/// ## Was beim Austausch NICHT ueberschrieben wird
///
/// <c>config/</c>. Dort liegt <c>overlay.json</c> mit allem, was der Nutzer je
/// eingestellt hat -- Positionen, Farben, Liniendicken, Ghost-Zeiten. Ein Update,
/// das die Arbeit eines Abends wegwirft, ist schlimmer als gar kein Update. Neue
/// Dateien in <c>config/</c> kommen dazu, vorhandene bleiben unberuehrt.
/// </remarks>
internal static class AppUpdate
{
    /// <summary>Die Beschreibung einer Fassung, wie der Server sie nennt.</summary>
    internal sealed class Fassung
    {
        [JsonPropertyName("build")] public string? Build { get; set; }
        [JsonPropertyName("built_at")] public string? BuiltAt { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("bytes")] public long Bytes { get; set; }
        [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        /// <summary>ECDSA signature of the ZIP -- see <see cref="UpdateSignature"/>.</summary>
        [JsonPropertyName("sig")] public string? Signature { get; set; }
    }

    // Nur ein schlichter Dateiname. Der Name kam bisher ungeprueft vom Server in
    // Path.Combine -- "..\\..\\Startup\\x.cmd" haette die Datei ausserhalb des
    // Arbeitsordners abgelegt.
    private static readonly System.Text.RegularExpressions.Regex SichererName =
        new(@"^[A-Za-z0-9._-]{1,100}\.zip$");

    internal static string ZielName(Fassung fassung)
    {
        var name = Path.GetFileName(fassung.Name ?? string.Empty);
        return SichererName.IsMatch(name) ? name : "fh-companion.zip";
    }

    /// <summary>Die Download-Adresse -- nur auf DEMSELBEN Server wie die Auskunft.</summary>
    internal static Uri DownloadUri(string baseUrl, Fassung fassung)
    {
        var basis = new Uri(baseUrl.TrimEnd('/') + "/");
        var ziel = new Uri(basis, (fassung.Url ?? "/download/haptics").TrimStart('/'));
        if (!string.Equals(ziel.Host, basis.Host, StringComparison.OrdinalIgnoreCase)
            || ziel.Port != basis.Port || ziel.Scheme != basis.Scheme)
        {
            throw new InvalidDataException("Der Server nennt eine Download-Adresse auf einem "
                                           + "anderen Rechner. Nichts wurde geladen.");
        }
        return ziel;
    }

    /// <summary>Was ein Blick auf den Server ergeben hat.</summary>
    internal sealed record Befund(bool Neuer, string? EigeneKennung, Fassung? Server,
                                  string Text);

    /// <summary>Der Ordner, in dem die App liegt -- die Wurzel mit app/, config/, data/.</summary>
    /// <remarks>
    /// <c>AppContext.BaseDirectory</c> zeigt auf <c>app\</c>; die Wurzel ist der
    /// Ordner darueber. Liegt die App ausnahmsweise nicht in einem <c>app\</c>
    /// (etwa beim Entwickeln aus dem Bauordner), gibt es keine Wurzel und damit
    /// auch kein Update -- dann steht der Quelltext ja daneben.
    /// </remarks>
    public static string? InstallRoot
    {
        get
        {
            var hier = new DirectoryInfo(AppContext.BaseDirectory);
            if (!string.Equals(hier.Name, "app", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return hier.Parent?.FullName;
        }
    }

    public static string? MetaPath
    {
        get
        {
            var wurzel = InstallRoot;
            if (wurzel is null) { return null; }
            var pfad = Path.Combine(wurzel, "app.meta.json");
            return File.Exists(pfad) ? pfad : null;
        }
    }

    /// <summary>Die Kennung der installierten Fassung, oder null.</summary>
    public static string? OwnBuild()
    {
        var pfad = MetaPath;
        if (pfad is null) { return null; }
        try
        {
            var f = JsonSerializer.Deserialize<Fassung>(File.ReadAllText(pfad));
            return string.IsNullOrWhiteSpace(f?.Build) ? null : f!.Build;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Wohin heruntergeladen und ausgepackt wird.</summary>
    /// <remarks>
    /// Neben den Zwischenstand des Nutzers und NICHT in den Installationsordner:
    /// der kann in Programme\ liegen, wo ein normaler Nutzer nicht schreiben darf.
    /// Der Austausch selbst prueft das gesondert.
    /// </remarks>
    public static string WorkDirectory => Path.Combine(AppInfo.DataFolder, "update");

    /// <summary>Fragen, ob es etwas Neueres gibt. Jeder Fehlschlag ist normal.</summary>
    public static async Task<Befund> CheckAsync(string? baseUrl, TimeSpan timeout)
    {
        var eigene = OwnBuild();
        if (InstallRoot is null)
        {
            return new Befund(false, eigene, null,
                              "kein entpacktes Paket -- hier wird nicht aktualisiert");
        }
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return new Befund(false, eigene, null, "kein Server eingestellt");
        }
        try
        {
            using var client = ServerHttp.Client(timeout);
            var text = await client.GetStringAsync(
                new Uri(new Uri(baseUrl!.TrimEnd('/') + "/"), "api/haptics"))
                .ConfigureAwait(false);
            var fassung = JsonSerializer.Deserialize<Fassung>(text);
            if (fassung?.Build is null || fassung.Sha256 is null)
            {
                return new Befund(false, eigene, null,
                                  "der Server nennt keine Fassung");
            }
            if (eigene is null)
            {
                // Ohne eigene Kennung laesst sich nicht sagen, ob das dort drueben
                // neuer ist. Dann lieber nichts behaupten als raten -- ein Update
                // anzubieten, das dieselbe Fassung ist, verbrennt Vertrauen.
                return new Befund(false, null, fassung,
                                  "diese Fassung nennt ihre eigene Kennung nicht");
            }
            var neuer = !string.Equals(eigene, fassung.Build, StringComparison.Ordinal);
            return new Befund(neuer, eigene, fassung,
                              neuer ? $"neue Fassung: {fassung.Name}"
                                    : "die App ist aktuell");
        }
        catch (Exception e)
        {
            return new Befund(false, eigene, null, "Server nicht erreichbar: " + e.Message);
        }
    }

    /// <summary>Herunterladen und die Pruefsumme pruefen. Gibt den Pfad der ZIP zurueck.</summary>
    public static async Task<string> DownloadAsync(
        string baseUrl, Fassung fassung, TimeSpan timeout,
        IProgress<string>? fortschritt = null, CancellationToken abbruch = default)
    {
        Directory.CreateDirectory(WorkDirectory);
        var ziel = Path.Combine(WorkDirectory, ZielName(fassung));

        // Eine schon vorhandene Datei mit der richtigen Pruefsumme UND gueltiger
        // Unterschrift genuegt: ein abgebrochener Versuch soll 66 MB nicht zweimal
        // kosten.
        if (File.Exists(ziel) && Pruefsumme(ziel) == fassung.Sha256
            && UpdateSignature.Verify(ziel, fassung.Signature))
        {
            fortschritt?.Report("schon heruntergeladen, Unterschrift stimmt");
            return ziel;
        }

        var teil = ziel + ".teil";
        using (var client = ServerHttp.Client(timeout))
        using (var antwort = await client.GetAsync(
                   DownloadUri(baseUrl, fassung),
                   HttpCompletionOption.ResponseHeadersRead, abbruch).ConfigureAwait(false))
        {
            antwort.EnsureSuccessStatusCode();
            var gesamt = antwort.Content.Headers.ContentLength ?? fassung.Bytes;
            using var quelle = await antwort.Content.ReadAsStreamAsync(abbruch)
                                             .ConfigureAwait(false);
            using var datei = File.Create(teil);
            var puffer = new byte[1 << 16];
            long gelesen = 0;
            var zuletzt = DateTime.UtcNow;
            int n;
            while ((n = await quelle.ReadAsync(puffer, abbruch).ConfigureAwait(false)) > 0)
            {
                await datei.WriteAsync(puffer.AsMemory(0, n), abbruch).ConfigureAwait(false);
                gelesen += n;
                if ((DateTime.UtcNow - zuletzt).TotalMilliseconds > 400)
                {
                    zuletzt = DateTime.UtcNow;
                    fortschritt?.Report(gesamt > 0
                        ? $"lade ... {gelesen / 1e6:0} von {gesamt / 1e6:0} MB"
                        : $"lade ... {gelesen / 1e6:0} MB");
                }
            }
        }

        // ERST PRUEFEN, DANN AN DEN ENDGUELTIGEN NAMEN.
        //
        // Eine halb uebertragene ZIP unter dem richtigen Namen waere beim naechsten
        // Versuch "schon da" -- und wuerde den Austausch mit kaputten Dateien fuettern.
        var gemessen = Pruefsumme(teil);
        if (!string.Equals(gemessen, fassung.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(teil);
            throw new InvalidDataException(
                "Die heruntergeladene Datei stimmt nicht mit der Angabe des Servers "
                + $"ueberein (erwartet {fassung.Sha256![..12]}..., "
                + $"bekommen {gemessen[..12]}...). Nichts wurde ersetzt.");
        }
        // DIE UNTERSCHRIFT ENTSCHEIDET, nicht die Pruefsumme: die kam vom selben
        // Server wie die Datei und beweist darum nur, dass beides zusammenpasst.
        if (!UpdateSignature.Verify(teil, fassung.Signature))
        {
            File.Delete(teil);
            throw new InvalidDataException(
                "Das Paket traegt keine gueltige Unterschrift des Herausgebers. "
                + "Nichts wurde ersetzt -- entweder ist es unterwegs veraendert worden, "
                + "oder der Server liefert ein unsigniertes Paket.");
        }
        File.Delete(ziel);
        File.Move(teil, ziel);
        fortschritt?.Report("heruntergeladen, Unterschrift stimmt");
        return ziel;
    }

    private static string Pruefsumme(string pfad)
    {
        using var f = File.OpenRead(pfad);
        return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
    }

    /// <summary>
    /// Die ZIP auspacken und den Austausch anstossen. Die App beendet sich danach.
    /// </summary>
    /// <remarks>
    /// Ein laufendes Programm kann seine eigene .exe nicht ersetzen. Also macht es
    /// ein Helfer: eine Stapeldatei, die wartet, bis diese Prozessnummer weg ist,
    /// dann kopiert und dann neu startet. Sie laeuft losgeloest weiter, waehrend
    /// die App sich beendet.
    ///
    /// Der Helfer ist bewusst eine .cmd und kein zweites Programm: er muss nach dem
    /// Austausch selbst noch funktionieren, und alles, was aus dem ersetzten Ordner
    /// stammt, koennte in dem Moment gerade ueberschrieben werden.
    /// </remarks>
    public static void StageAndRestart(string zipPfad, IProgress<string>? fortschritt = null,
                                       string? signatur = null)
    {
        // Noch einmal UNMITTELBAR vor dem Auspacken: zwischen Download und Knopfdruck
        // liegt Zeit, und die Datei liegt in einem Ordner, in den der Nutzer schreibt.
        if (!UpdateSignature.Verify(zipPfad, signatur))
        {
            throw new InvalidDataException("Die Unterschrift des Pakets stimmt nicht (mehr). "
                                           + "Nichts wurde ersetzt.");
        }
        var wurzel = InstallRoot
            ?? throw new InvalidOperationException("Kein entpacktes Paket -- kein Austausch.");
        if (!Schreibbar(wurzel))
        {
            throw new UnauthorizedAccessException(
                $"In {wurzel} darf nicht geschrieben werden. Die App liegt an einem "
                + "geschuetzten Ort -- das Paket von Hand entpacken, oder die App "
                + "an eine Stelle legen, an der du schreiben darfst.");
        }

        var bereit = Path.Combine(WorkDirectory, "bereit");
        if (Directory.Exists(bereit)) { Directory.Delete(bereit, recursive: true); }
        Directory.CreateDirectory(bereit);
        fortschritt?.Report("packe aus ...");
        System.IO.Compression.ZipFile.ExtractToDirectory(zipPfad, bereit);

        // Die ZIP traegt ihren Ordnernamen als Wurzel.
        var neu = Directory.GetDirectories(bereit).FirstOrDefault()
            ?? throw new InvalidDataException("Die ZIP enthaelt keinen Ordner.");

        var helfer = Path.Combine(WorkDirectory, "austausch.cmd");
        var exe = AppInfo.ExePath;
        File.WriteAllText(helfer, HelferText(
            Environment.ProcessId, neu, wurzel, exe), new UTF8Encoding(false));

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{helfer}\"",
            UseShellExecute = false,
            // SICHTBAR, UND DAS MIT ABSICHT. Eine versteckte Konsole, die nach dem
            // Ende des Elternprozesses dessen Programmordner ueberschreibt, ist das
            // Lehrbuchmuster eines Droppers -- und Defender hat die App bei einem
            // Nutzer genau deshalb als Trojan:Win32/Bearfoos.A!ml gemeldet
            // (siehe docs/defender-false-positive.md). Verborgen gewinnen wir
            // nichts: wer auf Update drueckt, darf sehen, was ersetzt wird.
            CreateNoWindow = false,
            WorkingDirectory = WorkDirectory,
        });
    }

    private static bool Schreibbar(string ordner)
    {
        try
        {
            var probe = Path.Combine(ordner, ".schreibprobe");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Der Helfer, der wartet, kopiert und neu startet.</summary>
    /// <remarks>
    /// <c>robocopy /E</c> und ausdruecklich NICHT <c>/MIR</c>: /MIR loescht alles im
    /// Ziel, was in der Quelle fehlt -- und damit jede Rundenaufnahme und jede
    /// eigene Datei, die jemand danebengelegt hat.
    ///
    /// <c>config\</c> wird mit <c>/XC /XN /XO</c> kopiert: nur was es noch gar nicht
    /// gibt. Dort liegt overlay.json mit allen Einstellungen des Nutzers.
    /// </remarks>
    private static string HelferText(int pid, string quelle, string ziel, string exe)
    {
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine($"title {AppInfo.Name} -- update");
        sb.AppendLine("rem Dieses Fenster ist mit Absicht sichtbar, siehe StageAndRestart.");
        sb.AppendLine("rem Wird von der App geschrieben und gestartet. Sie beendet sich danach.");
        sb.AppendLine("setlocal");
        sb.AppendLine($"echo {AppInfo.Name} is updating itself.");
        sb.AppendLine("echo This window closes by itself. Nothing else is being installed.");
        sb.AppendLine("echo.");
        sb.AppendLine();
        sb.AppendLine("echo Waiting for the program to close ...");
        sb.AppendLine("rem 1. Warten, bis die App wirklich weg ist -- hoechstens 60 Sekunden.");
        sb.AppendLine("set /a versuche=0");
        sb.AppendLine(":warte");
        sb.AppendLine($"tasklist /fi \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul");
        sb.AppendLine("if errorlevel 1 goto weg");
        sb.AppendLine("set /a versuche+=1");
        sb.AppendLine("if %versuche% gtr 120 goto aufgeben");
        sb.AppendLine("timeout /t 1 /nobreak >nul");
        sb.AppendLine("goto warte");
        sb.AppendLine();
        sb.AppendLine(":weg");
        sb.AppendLine("echo Replacing the program files ...");
        sb.AppendLine("rem 2. Alles ausser config\\ ersetzen.");
        sb.AppendLine($"robocopy \"{quelle}\" \"{ziel}\" /E /XD \"{Path.Combine(quelle, "config")}\" /NFL /NDL /NJH /NJS /R:3 /W:2 >nul");
        sb.AppendLine("if errorlevel 8 goto fehler");
        sb.AppendLine();
        sb.AppendLine("rem 3. config\\ nur ergaenzen -- die Einstellungen des Nutzers bleiben.");
        sb.AppendLine($"robocopy \"{Path.Combine(quelle, "config")}\" \"{Path.Combine(ziel, "config")}\" /E /XC /XN /XO /NFL /NDL /NJH /NJS /R:3 /W:2 >nul");
        sb.AppendLine();
        sb.AppendLine("echo Done. Starting the new version.");
        sb.AppendLine("rem 4. Wieder starten.");
        sb.AppendLine($"start \"\" \"{exe}\"");
        sb.AppendLine("goto ende");
        sb.AppendLine();
        sb.AppendLine(":aufgeben");
        sb.AppendLine($"echo Die App lief nach 60 Sekunden noch. Nichts wurde ersetzt.>> \"{Path.Combine(WorkDirectory, "austausch.log")}\"");
        sb.AppendLine("goto ende");
        sb.AppendLine(":fehler");
        sb.AppendLine($"echo robocopy meldete einen Fehler. Das Paket liegt in {quelle}.>> \"{Path.Combine(WorkDirectory, "austausch.log")}\"");
        sb.AppendLine(":ende");
        return sb.ToString();
    }
}
