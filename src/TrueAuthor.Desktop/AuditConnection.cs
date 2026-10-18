using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using TrueAuthor.Core;

namespace TrueAuthor.Desktop;

sealed class AuditConnection : IDisposable
{
    readonly HttpClient client = new() { Timeout = TimeSpan.FromSeconds(20) };
    readonly DeviceIdentity identity;
    public Uri Origin { get; }
    public bool Linked => identity.DeviceId is not null;
    public Uri? LinkUrl { get; private set; }
    public AuditConnection(string address)
    {
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !(uri.Scheme == "https" || uri.Scheme == "http" && (uri.Host == "localhost" || IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip)))) throw new InvalidOperationException("Use the True Author HTTPS server origin, or HTTP on localhost for testing.");
        Origin = uri; identity = DeviceIdentity.LoadOrCreate(uri.GetLeftPart(UriPartial.Authority));
    }
    async Task<JsonElement> Post(string path, string body, bool signed, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Origin, path)) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (signed) foreach (var header in identity.SignRequest("POST", path, body)) request.Headers.Add(header.Key, header.Value);
        using var response = await client.SendAsync(request, cancel);
        var text = await response.Content.ReadAsStringAsync(cancel);
        JsonElement result;
        try { result = JsonSerializer.Deserialize<JsonElement>(text); } catch (JsonException) { throw new InvalidOperationException($"Server returned {(int)response.StatusCode}. Check the server address."); }
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(result.TryGetProperty("error", out var error) ? error.GetString() : "Server request failed.");
        return result;
    }
    public async Task Link(Action<string> progress, CancellationToken cancel, bool openBrowser = true)
    {
        var name = "True Author Windows desktop";
        var body = JsonSerializer.Serialize(new { publicKey = identity.PublicKey, name, platform = "windows", proof = identity.LinkProof(name, "windows") }, SourceInfo.Json);
        var result = await Post("/api/devices/link/start", body, false, cancel);
        var code = result.GetProperty("userCode").GetString()!;
        var deviceCode = result.GetProperty("deviceCode").GetString()!;
        var verification = new Uri(Origin, "/devices/link?code=" + Uri.EscapeDataString(code));
        LinkUrl = verification;
        progress("Confirm in your browser · " + code);
        if (openBrowser) Process.Start(new ProcessStartInfo(verification.AbsoluteUri) { UseShellExecute = true });
        var expiry = DateTimeOffset.UtcNow.AddMinutes(10);
        while (DateTimeOffset.UtcNow < expiry)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancel);
            var poll = await Post("/api/devices/link/poll", JsonSerializer.Serialize(new { deviceCode }, SourceInfo.Json), false, cancel);
            var state = poll.GetProperty("status").GetString();
            if (state == "approved") { identity.SetDeviceId(poll.GetProperty("deviceId").GetString()!); LinkUrl = null; progress("You’re connected. Start a project when you’re ready."); return; }
            if (state is "denied" or "expired") throw new InvalidOperationException("Device link " + state + ".");
        }
        throw new InvalidOperationException("Device linking expired. Try again.");
    }
    public async Task<string> Upload(string body, CancellationToken cancel)
    {
        var result = await Post("/api/audit/checkpoints", body, true, cancel);
        return result.GetProperty("digest").GetString()!;
    }
    public void Dispose() { identity.Dispose(); client.Dispose(); }
}
