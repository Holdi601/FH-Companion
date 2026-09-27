using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Eine selbst gefahrene Runde beim Server einreichen.
/// </summary>
/// <remarks>
/// ## Der Handschlag
///
/// Es gibt kein Geheimnis, das man vorher bekommen koennte -- niemand kennt den
/// Spieler. Also holt sich diese Installation eines, einmal:
///
///     POST /api/lap/register   {"hardware": "&lt;Hash&gt;", "gamertag": "..."}
///     -&gt;                       {"install_id": "...", "secret": "..."}
///
/// Danach wandert das Geheimnis nie wieder durchs Netz: jede Einreichung RECHNET
/// damit (HMAC-SHA256) statt es mitzuschicken. Wer den Verkehr mitliest, sieht die
/// Unterschrift, aber nicht, womit sie gebildet wurde.
///
/// ## Warum ueberhaupt unterschrieben wird
///
/// Nicht um jemanden fernzuhalten -- mitmachen soll jeder. Sondern damit eine
/// Einreichung einem Konto ZUZUORDNEN ist. Ohne das laesst sich niemand sperren,
/// und ohne Sperre ist jede Bestenliste beliebig.
///
/// ## Die Hardware-Kennung
///
/// Hier wird ein SHA-256 gebildet und NIE die Kennung selbst geschickt. Der Server
/// hasht ihn ein zweites Mal mit einem eigenen Geheimnis. Der Zweck ist eng: eine
/// Sperre soll eine Neuanmeldung ueberleben. Sie taugt zu nichts anderem, und
/// genau darum wird sie so und nicht anders uebertragen.
///
/// ## Was NICHT hier steht
///
/// Wann eingereicht wird. Das entscheidet der Nutzer, und der Aufrufer fragt ihn.
/// Ein Programm, das ungefragt Daten verschickt, waere hier besonders unangenehm --
/// die Telemetrie einer Runde sagt mehr ueber eine Fahrweise als eine Zeit.
/// </remarks>
internal static class LapSubmit
{
    private static readonly JsonSerializerOptions Lesbar = new() { WriteIndented = false };

    internal sealed class Identity
    {
        [JsonPropertyName("install_id")] public string? InstallId { get; set; }
        [JsonPropertyName("secret")] public string? Secret { get; set; }
        [JsonPropertyName("gamertag")] public string? Gamertag { get; set; }
        [JsonPropertyName("server")] public string? Server { get; set; }
    }

    /// <summary>Wo die Kennung liegt -- beim Nutzer, nie neben der Anwendung.</summary>
    /// <remarks>
    /// Ein veroeffentlichtes Programm kann in Programme\ liegen, wo ein normaler
    /// Benutzer nicht schreiben darf. Und ein Geheimnis im Installationsordner
    /// wanderte bei jedem Kopieren des Ordners mit -- zwei Rechner haetten dann
    /// dieselbe Kennung, und eine Sperre traefe beide.
    /// </remarks>
    public static string IdentityPath => Path.Combine(SubmitHome, "submit_identity.json");

    // FORZA_SUBMIT_HOME: ein eigener Ordner fuer Kennung und Buch -- damit ein Test
    // mit einem Test-Gamertag nie die echte Anmeldung des Nutzers ueberschreibt.
    internal static string SubmitHome =>
        Environment.GetEnvironmentVariable("FORZA_SUBMIT_HOME") is { Length: > 0 } eigener
            ? eigener
            : AppInfo.DataFolder;

    public static Identity? Load()
    {
        try
        {
            if (!File.Exists(IdentityPath)) { return null; }
            var d = JsonSerializer.Deserialize<Identity>(File.ReadAllText(IdentityPath));
            return string.IsNullOrWhiteSpace(d?.InstallId)
                   || string.IsNullOrWhiteSpace(d?.Secret) ? null : d;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void Save(Identity d)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(IdentityPath)!);
        File.WriteAllText(IdentityPath, JsonSerializer.Serialize(d, Lesbar));
    }

    /// <summary>Die Anmeldung vergessen -- danach ist diese Installation unbekannt.</summary>
    public static void Forget()
    {
        try { File.Delete(IdentityPath); } catch (Exception) { }
    }

    /// <summary>
    /// Ein Hash, der diese Maschine wiedererkennt -- und sonst nichts verraet.
    /// </summary>
    /// <remarks>
    /// Aus der MachineGuid, die Windows bei der Installation einmal wuerfelt, und
    /// dem Rechnernamen. Die MachineGuid allein waere genug; der Name kommt dazu,
    /// damit zwei Klone desselben Abbilds auseinanderfallen.
    ///
    /// Gehasht, BEVOR irgendetwas das Haus verlaesst. Der Server bekommt nie die
    /// Kennung selbst, und aus dem Hash laesst sie sich nicht zurueckrechnen.
    ///
    /// Faellt die Registrierung weg (etwa unter eingeschraenkten Rechten), tritt
    /// der Rechnername allein an ihre Stelle. Das ist schwaecher -- zwei gleich
    /// benannte Rechner sind dann eine Maschine --, aber es ist besser als ein
    /// Zufallswert, der bei jeder Neuinstallation eine frische Kennung ergaebe und
    /// damit jede Sperre wirkungslos machte.
    /// </remarks>
    public static string HardwareHash()
    {
        var teile = new List<string>();
        try
        {
            using var schluessel = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Cryptography");
            var guid = schluessel?.GetValue("MachineGuid") as string;
            if (!string.IsNullOrWhiteSpace(guid)) { teile.Add(guid!); }
        }
        catch (Exception)
        {
            // Kein Grund, deswegen nichts einreichen zu koennen.
        }
        teile.Add(Environment.MachineName);
        var roh = "forza-grip-haptics|" + string.Join("|", teile);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(roh)))
                      .ToLowerInvariant();
    }

    // Ueber ServerHttp: HTTPS, und nur bei gescheitertem Handschlag HTTP.
    private static HttpClient Client(TimeSpan timeout) => ServerHttp.Client(timeout);

    private static Uri An(string baseUrl, string pfad) =>
        new(new Uri(baseUrl.TrimEnd('/') + "/"), pfad.TrimStart('/'));

    /// <summary>Der Server hat abgelehnt -- mit seinem Statuscode, damit der Aufrufer
    /// "nicht schneller" (409) von einem echten Fehler unterscheiden kann.</summary>
    internal sealed class Rejected : InvalidOperationException
    {
        public int Status { get; }
        public Rejected(int status, string message) : base(message) { Status = status; }
    }

    /// <summary>Diese Installation beim Server anmelden.</summary>
    public static async Task<Identity> RegisterAsync(string baseUrl, string gamertag,
                                                     TimeSpan timeout)
    {
        var rumpf = JsonSerializer.SerializeToUtf8Bytes(new
        {
            hardware = HardwareHash(),
            gamertag,
        });
        using var client = Client(timeout);
        using var inhalt = new ByteArrayContent(rumpf);
        inhalt.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        using var antwort = await client.PostAsync(An(baseUrl, "api/lap/register"), inhalt)
                                        .ConfigureAwait(false);
        var text = await antwort.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!antwort.IsSuccessStatusCode)
        {
            throw new Rejected((int)antwort.StatusCode, Fehlertext(antwort.StatusCode, text));
        }
        var d = JsonSerializer.Deserialize<Identity>(text)
                ?? throw new InvalidOperationException("Der Server antwortete unbrauchbar.");
        d.Gamertag = gamertag;
        d.Server = baseUrl;
        Save(d);
        return d;
    }

    /// <summary>Die Unterschrift -- Wort für Wort wie auf der Serverseite.</summary>
    /// <remarks>
    /// METHODE, PFAD, ZEITSTEMPEL, NONCE und der SHA-256 des Rumpfes, mit
    /// Zeilenumbruechen verbunden. Jedes Stueck hat einen Grund: der PFAD, damit
    /// eine Unterschrift nicht an einem anderen Endpunkt gilt; der ZEITSTEMPEL,
    /// damit ein mitgeschnittener Aufruf nicht morgen noch zaehlt; die NONCE, damit
    /// er nicht innerhalb der erlaubten fuenf Minuten hundertmal zaehlt; der
    /// RUMPF-HASH, damit unterwegs nichts veraendert werden kann.
    /// </remarks>
    public static string Signature(string secret, string method, string pfad,
                                   string stamp, string nonce, byte[] body)
    {
        var rumpfHash = Convert.ToHexString(SHA256.HashData(body ?? Array.Empty<byte>()))
                               .ToLowerInvariant();
        var text = string.Join("\n", method.ToUpperInvariant(), pfad, stamp, nonce, rumpfHash);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(text)))
                      .ToLowerInvariant();
    }

    /// <summary>Eine Runde einreichen. Gibt die Antwort des Servers als Text zurueck.</summary>
    /// <param name="gamertag">
    /// Der Name, unter dem die Runde erscheinen soll -- leer fuer keinen, null fuer
    /// "nichts sagen" (dann bleibt der des Kontos). Er reist UNTERSCHRIEBEN mit, und
    /// der Server uebernimmt ihn fuer die Installation (seit 2026-09-27): ein spaeter
    /// eingetragener Gamertag braucht so keine neue Anmeldung.
    /// </param>
    public static async Task<string> SubmitAsync(Identity wer, RecordedLap lap,
                                                 string course, TimeSpan timeout,
                                                 string? gamertag = null)
    {
        if (string.IsNullOrWhiteSpace(wer.Server) || string.IsNullOrWhiteSpace(wer.Secret)
            || string.IsNullOrWhiteSpace(wer.InstallId))
        {
            throw new InvalidOperationException("Diese Installation ist nicht angemeldet.");
        }

        // Die Runde so, wie das Archiv sie ablegt -- plus den Streckenschluessel.
        // Der Server bildet daraus die Kennung, damit dieselbe Runde zweimal
        // eingereicht EINE Datei ergibt und nicht zwei.
        var knoten = JsonSerializer.SerializeToNode(lap, Lesbar)!.AsObject();
        knoten["course"] = course;
        var gesamt = new JsonObject { ["lap"] = knoten };
        if (gamertag is not null) { gesamt["gamertag"] = gamertag.Trim(); }
        var rumpf = JsonSerializer.SerializeToUtf8Bytes(gesamt, Lesbar);

        const string pfad = "/api/lap/submit";
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

        using var client = Client(timeout);
        using var inhalt = new ByteArrayContent(rumpf);
        inhalt.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        using var anfrage = new HttpRequestMessage(HttpMethod.Post, An(wer.Server!, pfad))
        {
            Content = inhalt,
        };
        anfrage.Headers.Add("X-Forza-Install", wer.InstallId);
        anfrage.Headers.Add("X-Forza-Timestamp", stamp);
        anfrage.Headers.Add("X-Forza-Nonce", nonce);
        anfrage.Headers.Add("X-Forza-Signature",
                            Signature(wer.Secret!, "POST", pfad, stamp, nonce, rumpf));

        using var antwort = await client.SendAsync(anfrage).ConfigureAwait(false);
        var text = await antwort.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!antwort.IsSuccessStatusCode)
        {
            throw new Rejected((int)antwort.StatusCode, Fehlertext(antwort.StatusCode, text));
        }
        return text;
    }

    /// <summary>
    /// Aus der Antwort des Servers einen Satz machen, den ein Mensch versteht.
    /// </summary>
    /// <remarks>
    /// Der Server schickt bei jedem Fehler ein Objekt mit `error`. Die rohe Antwort
    /// ins Fenster zu stellen waere JSON vor einem Menschen, der eine Runde
    /// hochladen wollte -- und die Statusnummer allein sagt ihm gar nichts.
    /// </remarks>
    private static string Fehlertext(System.Net.HttpStatusCode status, string text)
    {
        try
        {
            var knoten = JsonNode.Parse(text);
            var grund = knoten?["error"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(grund)) { return grund!; }
        }
        catch (Exception)
        {
            // Dann eben der rohe Text, gekuerzt.
        }
        var kurz = (text ?? string.Empty).Trim();
        if (kurz.Length > 200) { kurz = kurz[..200] + " ..."; }
        return $"Der Server antwortete mit {(int)status}: {kurz}";
    }
}
