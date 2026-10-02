using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text.Json;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Ein Rennergebnis an den Server: das gelesene Feld und ein Bild des Ergebnisschirms als Beleg.
/// </summary>
/// <remarks>
/// Seit 2026-10-02, auf Wunsch des Nutzers: daraus werden die Horizon-Play-Zeiten je Auto
/// (server/race_submissions.py). Mitgeschickt immer dann, wenn die App Runden einreicht
/// (submit_laps, ab Werk an, abschaltbar) -- so steht es in docs/lap_submissions.md und in
/// der Datenschutzerklaerung. Ohne Gamertags im Feld; das Bild sieht nur der Verwalter.
/// Unterschrieben wie eine Runde, nur mit eigenem Pfad.
/// </remarks>
internal static class RaceSubmit
{
    public const string Pfad = "/api/race/submit";

    /// <summary>Lohnt das Senden? Horizon Play, ein gelesenes Feld, mindestens ein Mensch mit Zeit.</summary>
    internal static bool Lohnt(RaceRecord r) =>
        r.Mode == "horizon-play" && !string.IsNullOrWhiteSpace(r.Track) && r.Field is { Count: > 1 } f
        && f.Any(x => x.Kind == "human" && x.Car is not null && (x.Ms is not null || x.BestLapMs is not null));

    /// <summary>Der Ergebnisschirm, 1280 Punkte breit, als JPEG -- rund 150 KB.</summary>
    internal static byte[] Beleg(Bitmap voll)
    {
        var breite = 1280;
        var hoehe = Math.Max(1, (int)Math.Round(1280.0 * voll.Height / Math.Max(1, voll.Width)));
        using var klein = new Bitmap(breite, hoehe, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(klein))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(voll, new Rectangle(0, 0, breite, hoehe));
        }
        using var strom = new MemoryStream();
        var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
        using (var guete = new EncoderParameters(1))
        {
            guete.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 80L);
            klein.Save(strom, jpeg, guete);
        }
        return strom.ToArray();
    }

    /// <summary>Was an /api/race/submit geht -- unterschrieben wird genau diese Bytefolge.</summary>
    internal static byte[] Rumpf(RaceRecord r, byte[] beleg) =>
        JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["race"] = new Dictionary<string, object?>
            {
                ["id"] = r.Id,
                ["at"] = r.At.ToString("s", System.Globalization.CultureInfo.InvariantCulture),
                ["track"] = r.Track,
                ["course"] = r.Course,
                ["class"] = r.Klass,
                ["mode"] = r.Mode,
                ["laps"] = r.Laps,
                ["drivers"] = r.Drivers,
                ["field"] = r.Field,
            },
            ["proof"] = Convert.ToBase64String(beleg),
        });

    /// <summary>Senden. Gibt die Antwort des Servers zurueck; wirft bei Ablehnung.</summary>
    public static async Task<string> SendenAsync(LapSubmit.Identity wer, RaceRecord r, byte[] beleg, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(wer.Server) || string.IsNullOrWhiteSpace(wer.Secret)
            || string.IsNullOrWhiteSpace(wer.InstallId))
        {
            throw new InvalidOperationException("this installation is not registered with the server");
        }
        var rumpf = Rumpf(r, beleg);
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        using var client = LapSubmit.Client(timeout);
        using var inhalt = new ByteArrayContent(rumpf);
        inhalt.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        using var anfrage = new HttpRequestMessage(HttpMethod.Post, LapSubmit.An(wer.Server!, Pfad)) { Content = inhalt };
        anfrage.Headers.Add("X-Forza-Install", wer.InstallId);
        anfrage.Headers.Add("X-Forza-Timestamp", stamp);
        anfrage.Headers.Add("X-Forza-Nonce", nonce);
        anfrage.Headers.Add("X-Forza-Signature", LapSubmit.Signature(wer.Secret!, "POST", Pfad, stamp, nonce, rumpf));
        using var antwort = await client.SendAsync(anfrage).ConfigureAwait(false);
        var text = await antwort.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!antwort.IsSuccessStatusCode)
        {
            throw new LapSubmit.Rejected((int)antwort.StatusCode, text.Length > 200 ? text[..200] : text);
        }
        return text;
    }
}
