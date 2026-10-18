using System.Security.Cryptography;
using System.Text.Json;
using TrueAuthor.Core;

namespace TrueAuthor.Desktop;

/// <summary>
/// This install's device key. The private key is encrypted with Windows DPAPI for the current user and never
/// leaves the machine; only the public key is registered with True Author when the writer links the device.
/// </summary>
sealed class DeviceIdentity : IDisposable
{
    sealed record Stored(string ProtectedKey, string? DeviceId);
    static readonly byte[] Entropy = "TrueAuthor device key v1"u8.ToArray();
    readonly string storePath;

    readonly ECDsa key;
    public string? DeviceId { get; private set; }
    public string PublicKey => DeviceSigning.PublicKey(key);
    public static string InstallationId()
    {
        using var installation = LoadOrCreate("installation");
        return "TA-" + Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(installation.PublicKey)));
    }

    DeviceIdentity(ECDsa key, string? deviceId, string path) { this.key = key; DeviceId = deviceId; storePath = path; }

    public static DeviceIdentity LoadOrCreate(string serverOrigin = "legacy")
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrueAuthor", "device-" + Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(serverOrigin))) + ".json");
        if (File.Exists(path))
        {
            try
            {
                var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path));
                if (stored is not null)
                {
                    var key = ECDsa.Create();
                    key.ImportPkcs8PrivateKey(ProtectedData.Unprotect(Convert.FromBase64String(stored.ProtectedKey), Entropy, DataProtectionScope.CurrentUser), out _);
                    return new DeviceIdentity(key, stored.DeviceId, path);
                }
            }
            catch (Exception error) when (error is CryptographicException or JsonException or FormatException)
            {
                // A key that cannot be decrypted (another Windows account, a restored profile) is replaced; the writer links again.
            }
        }
        var identity = new DeviceIdentity(DeviceSigning.NewKey(), null, path);
        identity.Save();
        return identity;
    }

    void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
        var protectedKey = ProtectedData.Protect(key.ExportPkcs8PrivateKey(), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllText(storePath, JsonSerializer.Serialize(new Stored(Convert.ToBase64String(protectedKey), DeviceId)));
    }

    public void SetDeviceId(string deviceId) { DeviceId = deviceId; Save(); }

    public string LinkProof(string name, string platform) => DeviceSigning.Sign(key, DeviceSigning.LinkMessage(PublicKey, name, platform));

    public Dictionary<string, string> SignRequest(string method, string pathAndQuery, string body) =>
        DeviceSigning.SignRequest(key, DeviceId ?? throw new InvalidOperationException("Device is not linked."), method, pathAndQuery, body, DateTimeOffset.UtcNow);

    public void Dispose() => key.Dispose();
}
