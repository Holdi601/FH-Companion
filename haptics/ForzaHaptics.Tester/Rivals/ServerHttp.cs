using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Authentication;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Der eine Weg, auf dem die App den Server anspricht: HTTPS zuerst, HTTP nur als
/// Notnagel.
/// </summary>
/// <remarks>
/// ## Warum ueberhaupt ein Rueckfall
///
/// Seit dem 2026-09-25 spricht die App den Server ueber HTTPS an. Das Zertifikat
/// (Let's Encrypt) haengt an ISRG Root X2. Ein aktuelles Windows kennt die, ein
/// altes holt sie bei Bedarf nach -- aber ein Rechner, auf dem das Nachladen von
/// Stammzertifikaten abgeschaltet ist (Firmenrichtlinie, keine Verbindung zu
/// Windows Update), lehnt den Handschlag ab. Ohne Rueckfall kaeme diese App dann
/// NIE WIEDER an den Server -- auch nicht an das Update, das den Fehler behebt.
///
/// ## Warum das nicht schlechter ist als vorher
///
/// Vorher lief ALLES ueber HTTP. Faellt die App zurueck, ist sie genau dort, wo
/// sie vorher immer war: Updates sind trotzdem unterschrieben (UpdateSignature),
/// eingereichte Runden tragen trotzdem ihr HMAC, und das Geheimnis dazu geht nie
/// ueber die Leitung. Zurueckgefallen wird NUR bei einem gescheiterten
/// TLS-Handschlag -- nicht bei einer Zeitueberschreitung, nicht bei einem
/// Fehlercode --, und dann fuer den Rest der Sitzung, damit nicht jede Anfrage erst
/// den Handschlag versucht.
///
/// Der Handschlag scheitert, BEVOR die Anfrage den Server erreicht. Ein neuer
/// Versuch ueber HTTP schickt sie also zum ersten Mal, nicht zum zweiten -- auch
/// die Einmal-Nonce einer Einreichung ist dann noch unverbraucht.
/// </remarks>
internal static class ServerHttp
{
    private static readonly ConcurrentDictionary<string, bool> OhneTls = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ein Client mit Rueckfall. Wie <c>new HttpClient { Timeout = ... }</c>.</summary>
    /// <remarks>
    /// Mit Kennung "FHCompanion/Fassung" (bis 2026-09-26 "ForzaGripHaptics/...", seit 2026-09-25): so zaehlt der Server
    /// ein Update als "app" und nicht als Browser-Download (server/downloads.py). Nur
    /// Name und Fassung -- nichts ueber den Rechner.
    /// </remarks>
    public static HttpClient Client(TimeSpan timeout)
    {
        var client = new HttpClient(new Rueckfall(new SocketsHttpHandler()), disposeHandler: true)
        {
            Timeout = timeout,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.Id + "/" + Fassung);
        return client;
    }

    private static readonly string Fassung =
        typeof(ServerHttp).Assembly.GetName().Version?.ToString() ?? "0";

    /// <summary>Ist diese Sitzung schon einmal auf HTTP zurueckgefallen? (Fuer die Anzeige.)</summary>
    public static bool FellBack => !OhneTls.IsEmpty;

    /// <summary>
    /// Derselbe Server, nur vielleicht mit anderem Schema? <c>http://x:8787</c> und
    /// <c>https://x:8787/</c> sind derselbe -- sonst meldete sich nach der Umstellung
    /// jede Installation als neue an.
    /// </summary>
    public static bool SameServer(string? a, string? b)
    {
        if (!Uri.TryCreate(a?.Trim(), UriKind.Absolute, out var x)
            || !Uri.TryCreate(b?.Trim(), UriKind.Absolute, out var y))
        {
            return false;
        }
        return string.Equals(x.Host, y.Host, StringComparison.OrdinalIgnoreCase)
               && EffektiverPort(x) == EffektiverPort(y)
               && x.AbsolutePath.TrimEnd('/') == y.AbsolutePath.TrimEnd('/');
    }

    // Ohne Portangabe haben http und https verschiedene Standardports; hier zaehlt,
    // ob ausdruecklich derselbe genannt ist (8787) -- dann ist es derselbe Dienst.
    private static int EffektiverPort(Uri u) => u.IsDefaultPort ? -1 : u.Port;

    /// <summary>Ein gescheiterter TLS-Handschlag -- und nur der.</summary>
    internal static bool IstTlsFehler(HttpRequestException e) =>
        e.HttpRequestError == HttpRequestError.SecureConnectionError
        || e.InnerException is AuthenticationException;

    // Ein ausdruecklicher Port (8787) bleibt; ohne Angabe gilt der von http (80).
    private static Uri AlsHttp(Uri u) =>
        new UriBuilder(u) { Scheme = Uri.UriSchemeHttp, Port = u.IsDefaultPort ? -1 : u.Port }.Uri;

    private sealed class Rueckfall : DelegatingHandler
    {
        public Rueckfall(HttpMessageHandler innen) : base(innen) { }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage anfrage,
                                                                     CancellationToken ct)
        {
            var ziel = anfrage.RequestUri;
            if (ziel is null || ziel.Scheme != Uri.UriSchemeHttps)
            {
                return await base.SendAsync(anfrage, ct).ConfigureAwait(false);
            }
            if (OhneTls.ContainsKey(ziel.Authority))
            {
                anfrage.RequestUri = AlsHttp(ziel);
                return await base.SendAsync(anfrage, ct).ConfigureAwait(false);
            }
            try
            {
                return await base.SendAsync(anfrage, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException e) when (IstTlsFehler(e))
            {
                OhneTls[ziel.Authority] = true;
                // NICHT entsorgen: eine gestreamte Antwort (der Datensatz, das
                // Update) wird noch gelesen, wenn diese Methode laengst zurueck ist.
                var neu = Kopie(anfrage, AlsHttp(ziel));
                return await base.SendAsync(neu, ct).ConfigureAwait(false);
            }
        }

        // Eine Anfrage laesst sich nicht zweimal senden; also eine Kopie mit
        // demselben Inhalt. Der Inhalt ist gepuffert (String- oder Byte-Inhalt) und
        // wurde beim gescheiterten Handschlag nicht gelesen.
        private static HttpRequestMessage Kopie(HttpRequestMessage alt, Uri ziel)
        {
            var neu = new HttpRequestMessage(alt.Method, ziel)
            {
                Content = alt.Content,
                Version = alt.Version,
                VersionPolicy = alt.VersionPolicy,
            };
            foreach (var kopf in alt.Headers)
            {
                neu.Headers.TryAddWithoutValidation(kopf.Key, kopf.Value);
            }
            return neu;
        }
    }
}
