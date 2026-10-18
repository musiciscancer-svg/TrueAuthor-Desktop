namespace TrueAuthor.Desktop;

/// <summary>One offer per document per session, one visible prompt, with a cooldown between offers.</summary>
sealed class DocumentPromptPolicy
{
    readonly HashSet<string> offered = new(StringComparer.Ordinal);
    readonly HashSet<string> approved = new(StringComparer.Ordinal);
    DateTimeOffset nextOffer;
    public bool Visible { get; private set; }
    public bool Approved(string key) => approved.Contains(key);
    public void Approve(string key) { approved.Add(key); Visible = false; }
    public void Close() => Visible = false;
    public bool Offer(string key, bool paused, bool busy, DateTimeOffset now)
    {
        if (paused || busy || Visible || now < nextOffer || approved.Contains(key) || offered.Contains(key)) return false;
        offered.Add(key); Visible = true; nextOffer = now.AddSeconds(20); return true;
    }
}
