using System.Security.Cryptography;
using System.Text;

namespace TrueAuthor.Core;

/// <summary>
/// Port of packages/protocol/src/device.ts. A device signs every request with its own ECDSA P-256 key
/// (IEEE P1363 r||s signatures, base64url). The canonical request covers method, path with query, timestamp and
/// the SHA-256 of the exact body bytes. Verified against vectors produced by the TypeScript implementation.
/// </summary>
public static class DeviceSigning
{
    public const string DeviceHeader = "x-trueauthor-device", TimestampHeader = "x-trueauthor-timestamp", SignatureHeader = "x-trueauthor-signature";

    public static string CanonicalRequest(string method, string pathAndQuery, string timestamp, string body) =>
        string.Join('\n', "TA-DEVICE-V1", method.ToUpperInvariant(), pathAndQuery, timestamp, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body))));

    public static string LinkMessage(string publicKey, string name, string platform) => string.Join('\n', "TA-LINK-V1", publicKey, name, platform);

    public static ECDsa NewKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>Base64 DER SubjectPublicKeyInfo, the form the server registers.</summary>
    public static string PublicKey(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    static byte[] FromBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(normalized + new string('=', (4 - normalized.Length % 4) % 4));
    }

    public static string Sign(ECDsa key, string message) => Base64Url(key.SignData(Encoding.UTF8.GetBytes(message), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    public static bool Verify(string publicKey, string message, string signature)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return key.VerifyData(Encoding.UTF8.GetBytes(message), FromBase64Url(signature), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (FormatException) { return false; }
        catch (CryptographicException) { return false; }
    }

    public static Dictionary<string, string> SignRequest(ECDsa key, string deviceId, string method, string pathAndQuery, string body, DateTimeOffset now)
    {
        var timestamp = now.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new()
        {
            [DeviceHeader] = deviceId,
            [TimestampHeader] = timestamp,
            [SignatureHeader] = Sign(key, CanonicalRequest(method, pathAndQuery, timestamp, body)),
        };
    }
}
