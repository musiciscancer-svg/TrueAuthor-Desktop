using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TrueAuthor.Core;

namespace TrueAuthor.Desktop;

sealed record InputObservation(DateTimeOffset At, string Kind, int Units, string? Text = null, SourceInfo? Source = null);
sealed record DesktopObservations(int TypedKeys, int PasteAttempts, int UncertainEvents, List<InputObservation> Recent);
sealed record SavedAudit(Guid DocumentId, DateTimeOffset StartedAt, string ServerOrigin, int Sequence, string Previous, bool Paused, AuditSnapshot Document, List<string> Pending, string Adapter = "desktop-reference-editor", string? Title = null, string? ExternalId = null, string CaptureMode = "desktop-only", DesktopObservations? Observations = null);
static class AuditLocalStore
{
    static readonly byte[] Entropy = "TrueAuthor private document audit v3"u8.ToArray();
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrueAuthor", "audits");
    public static string PathFor(Guid id) => Path.Combine(DirectoryPath, id + ".taudit");
    public static void Save(SavedAudit audit, string? directory = null)
    {
        directory ??= DirectoryPath;
        Directory.CreateDirectory(directory);
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(audit, SourceInfo.Json)), Entropy, DataProtectionScope.CurrentUser);
        var path = Path.Combine(directory, audit.DocumentId + ".taudit");
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, true);
    }
    public static SavedAudit Load(string path)
    {
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
        return JsonSerializer.Deserialize<SavedAudit>(Encoding.UTF8.GetString(bytes), SourceInfo.Json) ?? throw new InvalidDataException("Invalid local audit.");
    }
    public static SavedAudit? FindExternal(string externalId, string? directory = null)
    {
        directory ??= DirectoryPath;
        foreach (var path in (Directory.Exists(directory) ? Directory.GetFiles(directory, "*.taudit") : []).OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try { var saved = Load(path); if (saved.ExternalId == externalId) return saved; }
            catch (Exception error) when (error is CryptographicException or JsonException or IOException or FormatException) { }
        }
        return null;
    }
}
