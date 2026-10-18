namespace TrueAuthor.Core;

public sealed record LedgerEntry(string Hash, int Length, SourceInfo Source, DateTimeOffset CapturedAt, string Via, string? Excerpt);

/// <summary>
/// In-memory record of recent copies. Entries expire after <see cref="Ttl"/> and are never written to disk.
/// Lookups use the SHA-256 of the normalized text. The short excerpt exists only so the writer can see what
/// was captured in the local activity window; the local API never returns it.
/// </summary>
public sealed class ClipboardLedger(Func<DateTimeOffset>? clock = null)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);
    /// <summary>An extension report for the same text within this window describes the same copy.</summary>
    public static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(20);
    public const int MaxEntries = 200, ExcerptLength = 80;

    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    readonly List<LedgerEntry> entries = [];
    readonly Lock gate = new();

    public event Action? Changed;

    void Purge()
    {
        var cutoff = now() - Ttl;
        entries.RemoveAll(e => e.CapturedAt < cutoff);
        if (entries.Count > MaxEntries) entries.RemoveRange(0, entries.Count - MaxEntries);
    }

    public static string? MakeExcerpt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= ExcerptLength ? flat : flat[..ExcerptLength] + "…";
    }

    /// <summary>
    /// Records a copy. When the same text was recorded moments ago by another observer (the OS clipboard and the
    /// browser extension both see a browser copy), the stronger source wins and the app name is kept.
    /// </summary>
    public LedgerEntry Record(string hash, int length, SourceInfo source, string via, string? excerpt = null)
    {
        LedgerEntry result;
        lock (gate)
        {
            Purge();
            var at = now();
            var index = entries.FindLastIndex(e => e.Hash == hash);
            if (index >= 0 && at - entries[index].CapturedAt <= MergeWindow)
            {
                var existing = entries[index];
                var merged = source.Strength > existing.Source.Strength ? source with { App = source.App ?? existing.Source.App } : existing.Source;
                result = existing with { Source = merged, CapturedAt = at, Via = existing.Via == via ? via : "clipboard+extension", Excerpt = existing.Excerpt ?? excerpt };
                entries.RemoveAt(index);
            }
            else
            {
                if (index >= 0) entries.RemoveAt(index);
                result = new LedgerEntry(hash, length, source, at, via, excerpt);
            }
            entries.Add(result);
        }
        Changed?.Invoke();
        return result;
    }

    public LedgerEntry? Find(string hash)
    {
        lock (gate) { Purge(); return entries.FindLast(e => e.Hash == hash); }
    }

    public IReadOnlyList<LedgerEntry> Snapshot()
    {
        lock (gate) { Purge(); return entries.ToArray(); }
    }

    public int Count { get { lock (gate) { Purge(); return entries.Count; } } }

    public void Clear()
    {
        lock (gate) entries.Clear();
        Changed?.Invoke();
    }
}
