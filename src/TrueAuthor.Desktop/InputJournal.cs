using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace TrueAuthor.Desktop;

/// <summary>Append-only, Windows-user-encrypted input batches. No plaintext key log or cloud upload.</summary>
static class InputJournal
{
    static readonly byte[] Entropy = "TrueAuthor approved document input journal v1"u8.ToArray();
    const long MaxBytes = 50_000_000;
    public static string PathFor(Guid id, string? directory = null) => Path.Combine(directory ?? AuditLocalStore.DirectoryPath, id + ".tinputs");
    public static void Append(Guid id, IReadOnlyList<InputObservation> events, string? directory = null)
    {
        if (events.Count == 0) return;
        var path = PathFor(id, directory); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var payload = JsonSerializer.SerializeToUtf8Bytes(events, TrueAuthor.Core.SourceInfo.Json);
        if (payload.Length > 4_000_000) throw new IOException("Input batch exceeds the local history limit.");
        var encrypted = ProtectedData.Protect(payload, Entropy, DataProtectionScope.CurrentUser);
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        if (stream.Length + encrypted.Length + 4 > MaxBytes) throw new IOException("This document's input history reached 50 MB. Capture was paused; export its history before starting a new document.");
        stream.Position = stream.Length;
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(encrypted.Length); writer.Write(encrypted); writer.Flush(); stream.Flush(true);
    }
    public static IReadOnlyList<InputObservation> Read(Guid id, string? directory = null)
    {
        var path = PathFor(id, directory); if (!File.Exists(path)) return [];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length > MaxBytes) throw new InvalidDataException("Input history exceeds the supported size.");
        using var reader = new BinaryReader(stream);
        var result = new List<InputObservation>();
        while (stream.Position < stream.Length)
        {
            if (stream.Length - stream.Position < 4) throw new InvalidDataException("Input history ends with an incomplete batch. The file has been kept.");
            var length = reader.ReadInt32();
            if (length <= 0 || length > 5_000_000 || length > stream.Length - stream.Position) throw new InvalidDataException("Input history has an incomplete or invalid batch. The file has been kept.");
            var data = ProtectedData.Unprotect(reader.ReadBytes(length), Entropy, DataProtectionScope.CurrentUser);
            result.AddRange(JsonSerializer.Deserialize<InputObservation[]>(data, TrueAuthor.Core.SourceInfo.Json) ?? []);
        }
        return result;
    }
}
