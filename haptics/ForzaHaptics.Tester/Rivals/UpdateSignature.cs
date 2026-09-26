using System.Security.Cryptography;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Signs and verifies app packages, so an update is only ever what was built here.
/// </summary>
/// <remarks>
/// ## Why the checksum alone was not enough
///
/// The updater compared the download against the SHA-256 the SERVER announced, and
/// took both over plain HTTP. Anyone able to change that traffic -- a hostile Wi-Fi,
/// a compromised router, a DNS entry that drifted to someone else -- could have sent
/// a different ZIP together with its matching checksum, and the app would have
/// unpacked and started it. One such moment would have run foreign code on every
/// machine that pressed Update.
///
/// ## What this adds
///
/// ECDSA P-256 over the whole ZIP. The PRIVATE key lives only on the build machine,
/// outside the repository (see <see cref="DefaultKeyPath"/>); the PUBLIC key is
/// compiled into the app below. A package without a valid signature is refused, and
/// nothing on the server can change that -- the server never holds the private key.
/// </remarks>
internal static class UpdateSignature
{
    /// <summary>The public half of the update key (SubjectPublicKeyInfo, base64).</summary>
    /// <remarks>
    /// Generated on 2026-09-24 with <c>--update-key-new</c>. Replacing it means every
    /// installed app refuses the next update -- only ever together with a manual
    /// reinstall announcement.
    /// </remarks>
    public const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEH6IlEcUJdmMWTootxemm+zXZYPv/HP0Efc5vifi8kmfqX/YafSywua/yBkTNAe+nskpN9nTocncAFo+4ljjRIA==";

    /// <summary>Where the private key lives on the build machine.</summary>
    /// <remarks>
    /// In the user profile and NOT in the repository: nothing that is committed,
    /// deployed or packed may ever carry it. FORZA_UPDATE_KEY overrides the path.
    /// BACK IT UP: without it no future update can be signed, and every installed
    /// app would have to be reinstalled by hand.
    /// </remarks>
    public static string DefaultKeyPath =>
        Environment.GetEnvironmentVariable("FORZA_UPDATE_KEY") is { Length: > 0 } eigener
            ? eigener
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                           ".forza-signing", "update_signing_key.pkcs8.b64");

    /// <summary>True only for a correct signature of exactly this file.</summary>
    public static bool Verify(string path, string? signatureBase64)
    {
        if (string.IsNullOrWhiteSpace(signatureBase64) || PublicKey.StartsWith("__"))
        {
            return false;
        }
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKey), out _);
            var signatur = Convert.FromBase64String(signatureBase64.Trim());
            using var datei = File.OpenRead(path);
            return ecdsa.VerifyData(datei, signatur, HashAlgorithmName.SHA256,
                                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Sign a file with the private key at <paramref name="keyPath"/>.</summary>
    public static string Sign(string path, string keyPath)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(File.ReadAllText(keyPath).Trim()), out _);
        using var datei = File.OpenRead(path);
        return Convert.ToBase64String(ecdsa.SignData(datei, HashAlgorithmName.SHA256,
                                                     DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    /// <summary>A new key pair; writes the private key, returns the public one.</summary>
    public static string NewKey(string keyPath)
    {
        if (File.Exists(keyPath))
        {
            throw new IOException($"{keyPath} exists already -- a new key would lock out every installed app.");
        }
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
        File.WriteAllText(keyPath, Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey()));
        return Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
    }
}
