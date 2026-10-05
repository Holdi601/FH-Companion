using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForzaHaptics.Tester.Rivals;

namespace ForzaHaptics.Tester.Scan;

/// <summary>
/// Einen abgelegten Scan-Lauf (rows.jsonl + state.json) an den Server schicken -- unterschrieben
/// wie Runden und Rennergebnisse (LapSubmit / LapAutoSubmit).
/// </summary>
/// <remarks>
/// ## Was hinausgeht
///
/// <c>{"state": {...state.json...}, "rows": "&lt;rows.jsonl&gt;"}</c> an <c>POST /api/scan/submit</c>
/// (server/scan_submissions.py). Der Server legt den Lauf ab wie den Beitrag eines Freundes
/// (contrib/app-&lt;kurz&gt;/&lt;run_id&gt;/), und mit dem naechsten Datensatzbau steht er auf der Seite.
///
/// KEINE GAMERTAGS: der Scanner liest sie nicht, und jede Zeile wird vor dem Senden auf
/// <see cref="Felder"/> beschnitten -- dieselbe Liste, auf die auch der Server beschneidet
/// (scan_submissions.ZEILEN_FELDER). Wer dort ein Feld ergaenzt, ergaenzt es hier.
///
/// ## Wann
///
/// Nur wenn das Einreichen an ist (<c>submit_laps</c>, ab Werk an, im Reiter Rivals
/// abschaltbar) und die App nicht offline ist -- dasselbe Abschalten wie fuer Runden und
/// Rennergebnisse (docs/datenschutz.md, 2b). Ohne Anmeldung meldet sich die App dafuer an.
///
/// Ein angenommener Lauf bekommt eine Quittung (<see cref="Quittung"/>) in seinen Ordner;
/// ein zweites Senden fragt dann nicht mehr beim Server nach.
/// </remarks>
internal static class ScanUpload
{
    /// <summary>Ergebnis eines Versands: ob er ankam und was der Server sagte.</summary>
    internal sealed record Antwort(bool Ok, string Meldung);

    public const string Pfad = "/api/scan/submit";

    /// <summary>Die Datei im Laufordner, die sagt: angenommen.</summary>
    public const string Quittung = "upload.json";

    /// <summary>Was von einer Zeile hinausgeht -- wie server/scan_submissions.ZEILEN_FELDER.</summary>
    internal static readonly IReadOnlySet<string> Felder = new HashSet<string>(StringComparer.Ordinal)
    {
        "rank", "lap_time_seconds", "pi", "car_id", "car_short_id", "car_name", "car_codename", "car_full_name",
        "drivetrain", "gearbox", "source_frame", "row_index", "rank_anchored", "line", "readings", "agreement",
        "pi_agreement", "scanner", "is_clean", "used_tcs", "used_abs", "used_stm", "used_friction_assist",
        "used_auto_brake", "used_auto_shifting", "used_clutch", "used_super_easy_assist",
    };

    private static readonly JsonSerializerOptions Kompakt = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Die Zeilen von rows.jsonl, jede auf <see cref="Felder"/> beschnitten. Wirft
    /// <see cref="FormatException"/> bei einer Zeile, die kein JSON-Objekt ist -- ein kaputter
    /// Lauf geht nicht halb hinaus.
    /// </summary>
    internal static string Zeilen(string rohText)
    {
        var raus = new StringBuilder();
        var nummer = 0;
        foreach (var zeile in rohText.Split('\n'))
        {
            nummer++;
            var text = zeile.Trim();
            if (text.Length == 0) { continue; }
            JsonNode? knoten;
            try
            {
                knoten = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                knoten = null;
            }
            if (knoten is not JsonObject roh) { throw new FormatException("rows.jsonl, line " + nummer); }
            var sauber = new JsonObject();
            try
            {
                foreach (var (name, wert) in roh)
                {
                    if (Felder.Contains(name)) { sauber[name] = wert?.DeepClone(); }
                }
            }
            catch (ArgumentException)
            {
                // Ein Feld doppelt: JsonObject merkt es erst beim Aufzaehlen.
                throw new FormatException("rows.jsonl, line " + nummer);
            }
            raus.Append(sauber.ToJsonString(Kompakt)).Append('\n');
        }
        return raus.ToString();
    }

    /// <summary>Was an /api/scan/submit geht -- unterschrieben wird genau diese Bytefolge.</summary>
    internal static byte[] Rumpf(JsonObject zustand, string zeilen) =>
        Encoding.UTF8.GetBytes(new JsonObject
        {
            ["state"] = zustand.DeepClone(),
            ["rows"] = zeilen,
        }.ToJsonString(Kompakt));

    /// <summary>Den Lauf im Ordner <paramref name="laufOrdner"/> senden. Wirft nie.</summary>
    public static async Task<Antwort> SendenAsync(string laufOrdner, CancellationToken stop)
    {
        // 1. Der Lauf selbst -- vor allem anderen, ohne Netz.
        var zustandPfad = Path.Combine(laufOrdner, "state.json");
        var zeilenPfad = Path.Combine(laufOrdner, "rows.jsonl");
        if (!File.Exists(zustandPfad) || !File.Exists(zeilenPfad))
        {
            return new Antwort(false, Loc.T("This folder holds no scan: state.json or rows.jsonl is missing."));
        }
        if (File.Exists(Path.Combine(laufOrdner, Quittung)))
        {
            return new Antwort(true, Loc.T("This scan was already sent."));
        }
        JsonObject zustand;
        string zeilen;
        try
        {
            zustand = JsonNode.Parse(await File.ReadAllTextAsync(zustandPfad, stop).ConfigureAwait(false)) as JsonObject
                      ?? throw new FormatException("state.json");
            zeilen = Zeilen(await File.ReadAllTextAsync(zeilenPfad, stop).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            return new Antwort(false, Loc.T("Sending was cancelled."));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            return new Antwort(false, Loc.T("The scan could not be read: ") + e.Message);
        }
        if (zeilen.Length == 0)
        {
            return new Antwort(false, Loc.T("This scan has no rows to send."));
        }

        // 2. Darf gesendet werden? Dasselbe Abschalten wie fuer Runden.
        var einstellungen = OverlaySettings.Load();
        if (einstellungen.Offline || string.IsNullOrWhiteSpace(einstellungen.DatasetUrl))
        {
            return new Antwort(false, Loc.T("The app is set to offline, so scans are not sent."));
        }
        if (!einstellungen.SubmitLaps)
        {
            return new Antwort(false, Loc.T("Sending is switched off (tab Rivals overlay, \"Submit my laps when they beat the leaderboard\"). Scans go out only while it is on."));
        }

        // 3. Anmelden (die gemerkte Anmeldung, sonst eine neue) und senden.
        var rumpf = Rumpf(zustand, zeilen);
        LapSubmit.Identity wer;
        try
        {
            wer = await new LapAutoSubmit(() => null, einstellungen, _ => { })
                .AusweisAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            return new Antwort(false, Loc.T("Could not sign up with the server: ") + e.Message);
        }
        if (string.IsNullOrWhiteSpace(wer.Server) || string.IsNullOrWhiteSpace(wer.Secret)
            || string.IsNullOrWhiteSpace(wer.InstallId))
        {
            return new Antwort(false, Loc.T("Could not sign up with the server: ") + "no identity");
        }
        try
        {
            var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
            // Ein volles Board sind einige Megabyte -- mehr Zeit als fuer eine Runde.
            using var client = LapSubmit.Client(TimeSpan.FromSeconds(Math.Max(120, einstellungen.DatasetDownloadSeconds)));
            using var inhalt = new ByteArrayContent(rumpf);
            inhalt.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
            using var anfrage = new HttpRequestMessage(HttpMethod.Post, LapSubmit.An(wer.Server!, Pfad)) { Content = inhalt };
            anfrage.Headers.Add("X-Forza-Install", wer.InstallId);
            anfrage.Headers.Add("X-Forza-Timestamp", stamp);
            anfrage.Headers.Add("X-Forza-Nonce", nonce);
            anfrage.Headers.Add("X-Forza-Signature", LapSubmit.Signature(wer.Secret!, "POST", Pfad, stamp, nonce, rumpf));
            using var antwort = await client.SendAsync(anfrage, stop).ConfigureAwait(false);
            var text = await antwort.Content.ReadAsStringAsync(stop).ConfigureAwait(false);
            if ((int)antwort.StatusCode == 409 && Fehlertext(text, 409).Contains("already on the server", StringComparison.Ordinal))
            {
                // Die erste Antwort ging verloren (Zeitueberschreitung nach dem Ablegen) oder die
                // Quittung liess sich nicht schreiben: der Lauf IST angekommen. Ohne Quittung fragte
                // jedes weitere Senden wieder und hoerte wieder "abgelehnt".
                Quittieren(laufOrdner, wer.Server, text);
                return new Antwort(true, Loc.T("This scan was already sent."));
            }
            if (!antwort.IsSuccessStatusCode)
            {
                return new Antwort(false, Loc.T("The server did not take the scan: ") + Fehlertext(text, (int)antwort.StatusCode));
            }
            var zahl = 0;
            try
            {
                zahl = JsonNode.Parse(text)?["rows"]?.GetValue<int>() ?? 0;
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
            {
                // Angenommen ist angenommen -- auch wenn die Antwort nicht die erwartete Form hat.
            }
            Quittieren(laufOrdner, wer.Server, text);
            return new Antwort(true, string.Format(CultureInfo.CurrentCulture,
                Loc.T("Sent: {0} rows. They reach the site with the next dataset update."), zahl));
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return new Antwort(false, Loc.T("Sending was cancelled."));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException
                                   or UriFormatException or InvalidOperationException)
        {
            return new Antwort(false, Loc.T("The server could not be reached: ") + e.Message);
        }
    }

    /// <summary>Die Quittung in den Laufordner: dieser Lauf ist angekommen.</summary>
    private static void Quittieren(string laufOrdner, string? server, string text)
    {
        try
        {
            File.WriteAllText(Path.Combine(laufOrdner, Quittung), new JsonObject
            {
                ["sent_at"] = DateTimeOffset.Now.ToString("s", CultureInfo.InvariantCulture),
                ["server"] = server,
                ["answer"] = text.Length > 2000 ? text[..2000] : text,
            }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Ohne Quittung fragt das naechste Senden den Server -- der meldet den Doppelten
            // (409), und das gilt dann als angekommen.
        }
    }

    /// <summary>Die Meldung des Servers ({"error": ...}), sonst der Anfang des Textes.</summary>
    internal static string Fehlertext(string text, int status)
    {
        try
        {
            if (JsonNode.Parse(text)?["error"]?.GetValue<string>() is { Length: > 0 } fehler)
            {
                return fehler.Length > 300 ? fehler[..300] : fehler;
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            // kein JSON -- dann der Text selbst
        }
        var kurz = text.Length > 200 ? text[..200] : text;
        return string.IsNullOrWhiteSpace(kurz) ? "HTTP " + status : kurz;
    }
}
