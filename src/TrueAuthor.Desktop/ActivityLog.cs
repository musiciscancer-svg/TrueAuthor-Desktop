namespace TrueAuthor.Desktop;

public sealed record ActivityItem(DateTimeOffset At, string Kind, string Detail);

/// <summary>What the app did, for the writer to inspect. Holds no copied text. In memory only.</summary>
public sealed class ActivityLog
{
    const int Max = 200;
    readonly List<ActivityItem> items = [];
    readonly Lock gate = new();
    public event Action? Changed;

    public void Add(string kind, string detail)
    {
        lock (gate)
        {
            items.Add(new ActivityItem(DateTimeOffset.Now, kind, detail));
            if (items.Count > Max) items.RemoveAt(0);
        }
        Changed?.Invoke();
    }

    public IReadOnlyList<ActivityItem> Snapshot() { lock (gate) return items.ToArray(); }
}
