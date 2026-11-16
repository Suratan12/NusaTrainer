using System.Security.Cryptography;

namespace NusaTrainer.Core.Database;

/// <summary>
/// Tanda tangan ECDSA P-256 / SHA-256 (format IEEE P1363, base64) atas byte mentah index.json.
/// Mencegah database palsu disuntikkan walau server/CDN dibobol.
/// </summary>
public static class SignatureVerifier
{
    public static bool Verify(ReadOnlySpan<byte> data, string signatureBase64, string publicKeyPem)
    {
        byte[] sig;
        try { sig = Convert.FromBase64String(signatureBase64.Trim()); }
        catch (FormatException) { return false; }

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(publicKeyPem);
        return ecdsa.VerifyData(data, sig, HashAlgorithmName.SHA256);
    }

    public static string Sign(ReadOnlySpan<byte> data, string privateKeyPem)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(privateKeyPem);
        return Convert.ToBase64String(ecdsa.SignData(data, HashAlgorithmName.SHA256));
    }

    public static (string privatePem, string publicPem) GenerateKeyPair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (ecdsa.ExportPkcs8PrivateKeyPem(), ecdsa.ExportSubjectPublicKeyInfoPem());
    }
}
