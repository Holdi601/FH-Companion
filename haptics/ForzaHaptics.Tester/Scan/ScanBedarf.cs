using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForzaHaptics.Tester.Rivals;

namespace ForzaHaptics.Tester.Scan;

/// <summary>
/// Welche Boards am noetigsten einen Scan brauchen -- und das Anmelden eines Boards, bevor es
/// gescannt wird (seit 2026-10-24, server/scan_needs.py).
/// </summary>
/// <remarks>
/// Die Anmeldung sperrt nichts: ein angemeldetes Board steht beim Server nur hinten in der Liste,
/// damit mehrere Scanner nicht dasselbe lesen. Sie verfaellt nach 40 Minuten; ein angenommener Scan
/// des Boards loest sie sofort.
/// </remarks>
internal static class ScanBedarf
{
    internal sealed record Eintrag(string Kategorie, string Strecke, string Klasse, double? LetzterScan, int? Tage,
                                   int Tiefe, int Scans, double? WirdGescanntBis)
    {
        public bool WirdGescannt => WirdGescanntBis is { } bis && bis > DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    /// <summary>Die Liste vom Server -- oder eine Fehlermeldung. Wirft nie.</summary>
    public static async Task<(List<Eintrag> Liste, string? Fehler)> LadenAsync(OverlaySettings s, CancellationToken stop = default)
    {
        if (s.Offline || string.IsNullOrWhiteSpace(s.ServerUrl))
        {
            return (new(), Loc.T("The app is set to offline -- the list comes from the server."));
        }
        try
        {
            using var client = LapSubmit.Client(TimeSpan.FromSeconds(20));
            var text = await client.GetStringAsync(LapSubmit.An(s.ServerUrl!, "/api/scan/needs"), stop).ConfigureAwait(false);
            return (Lesen(text), null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or UriFormatException or InvalidOperationException)
        {
            return (new(), string.Format(Loc.T("The list could not be loaded: {0}"), e.Message));
        }
    }

    internal static List<Eintrag> Lesen(string text)
    {
        var raus = new List<Eintrag>();
        if (JsonNode.Parse(text)?["boards"] is not JsonArray boards) { return raus; }
        foreach (var b in boards)
        {
            if (b is not JsonObject o) { continue; }
            var strecke = o["track"]?.GetValue<string>();
            var klasse = o["class"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(strecke) || string.IsNullOrWhiteSpace(klasse)) { continue; }
            raus.Add(new Eintrag(o["category"]?.GetValue<string>() ?? string.Empty, strecke, klasse,
                Zahl(o["lastScan"]), (int?)Zahl(o["days"]), (int)(Zahl(o["depth"]) ?? 0), (int)(Zahl(o["scans"]) ?? 0),
                Zahl(o["beingScannedUntil"])));
        }
        return raus;
    }

    private static double? Zahl(JsonNode? n)
    {
        try { return n is null ? null : n.GetValue<double>(); }
        catch (Exception e) when (e is InvalidOperationException or FormatException) { return null; }
    }

    /// <summary>Ein Board anmelden: (true, "") -- oder (false, warum). Wirft nie.</summary>
    public static async Task<(bool Ok, string Meldung)> AnmeldenAsync(OverlaySettings s, string strecke, string klasse,
                                                                      bool freigeben = false)
    {
        var pfad = freigeben ? "/api/scan/release" : "/api/scan/claim";
        if (s.Offline || string.IsNullOrWhiteSpace(s.DatasetUrl) || !s.SubmitLaps)
        {
            // Ohne Server: scannen geht trotzdem, nur weiss kein anderer davon.
            return (true, string.Empty);
        }
        try
        {
            var wer = await new LapAutoSubmit(() => null, s, _ => { }).AusweisAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(wer.Server) || string.IsNullOrWhiteSpace(wer.Secret) || string.IsNullOrWhiteSpace(wer.InstallId))
            {
                return (true, string.Empty);
            }
            var rumpf = Encoding.UTF8.GetBytes(new JsonObject { ["track"] = strecke, ["class"] = klasse }.ToJsonString());
            var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
            using var client = LapSubmit.Client(TimeSpan.FromSeconds(20));
            using var inhalt = new ByteArrayContent(rumpf);
            inhalt.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
            using var anfrage = new HttpRequestMessage(HttpMethod.Post, LapSubmit.An(wer.Server!, pfad)) { Content = inhalt };
            anfrage.Headers.Add("X-Forza-Install", wer.InstallId);
            anfrage.Headers.Add("X-Forza-Timestamp", stamp);
            anfrage.Headers.Add("X-Forza-Nonce", nonce);
            anfrage.Headers.Add("X-Forza-Signature", LapSubmit.Signature(wer.Secret!, "POST", pfad, stamp, nonce, rumpf));
            using var antwort = await client.SendAsync(anfrage).ConfigureAwait(false);
            var text = await antwort.Content.ReadAsStringAsync().ConfigureAwait(false);
            if ((int)antwort.StatusCode == 409) { return (false, ScanUpload.Fehlertext(text, 409)); }
            // Ein anderer Fehler (Server weg, alte Fassung): scannen, nur ohne Anmeldung.
            return (true, antwort.IsSuccessStatusCode ? string.Empty : ScanUpload.Fehlertext(text, (int)antwort.StatusCode));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UriFormatException or InvalidOperationException)
        {
            return (true, e.Message);
        }
    }

    /// <summary>Ein Eintrag als Ziel des Scanners -- null, wenn der Scanner die Strecke nicht kennt.</summary>
    public static ScanNavigator.Ziel? AlsZiel(ScanRoutes routen, Eintrag e)
    {
        var kat = routen.Finde(e.Kategorie);
        if (kat is null) { return null; }
        var index = -1;
        for (var i = 0; i < kat.Strecken.Count; i++)
        {
            if (string.Equals(kat.Strecken[i], e.Strecke, StringComparison.OrdinalIgnoreCase)) { index = i; break; }
        }
        if (index < 0 || !ScanScreen.Klassen.Contains(e.Klasse.ToUpperInvariant())) { return null; }
        return new ScanNavigator.Ziel(kat.Name, index, e.Klasse.ToUpperInvariant());
    }

    /// <summary>
    /// Die naechsten <paramref name="wieViele"/> Boards von oben: keine, die gerade jemand scannt, keine,
    /// die der Scanner nicht findet. Die Reihenfolge des Servers bleibt.
    /// </summary>
    public static List<(Eintrag Eintrag, ScanNavigator.Ziel Ziel)> VonOben(ScanRoutes routen, IEnumerable<Eintrag> liste, int wieViele)
    {
        var raus = new List<(Eintrag, ScanNavigator.Ziel)>();
        foreach (var e in liste)
        {
            if (raus.Count >= wieViele) { break; }
            if (e.WirdGescannt) { continue; }
            if (AlsZiel(routen, e) is { } z) { raus.Add((e, z)); }
        }
        return raus;
    }
}
