namespace TrueAuthor.Core;

/// <summary>Correlates input intent with an exact document change; ambiguity stays unobserved.</summary>
public sealed class InputReconciler
{
    readonly List<(string Text, AuditOrigin Origin, DateTimeOffset At, IReadOnlyList<AuditSpan>? Segments)> pending = [];
    int typedUnits;
    readonly System.Text.StringBuilder typedText = new();
    bool exactTyping;
    public DateTimeOffset LastInputAt { get; private set; }
    bool uncertain;
    public void Paste(string text, AuditOrigin origin, DateTimeOffset now, IReadOnlyList<AuditSpan>? segments = null)
    {
        if (uncertain) Clear();
        if (pending.Count >= 20) { pending.Clear(); uncertain = true; }
        LastInputAt = now; pending.Add((text, origin, now, segments));
    }
    public void Typed(int units) { LastInputAt = DateTimeOffset.UtcNow; if (units > 0 && units <= 1_000_000) typedUnits = Math.Min(1_000_000, typedUnits + units); }
    public void Typed(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (typedUnits > 0 && !exactTyping) { Uncertain(); return; }
        exactTyping = true;
        if (typedText.Length + text.Length > 1_000_000) { Uncertain(); return; }
        typedText.Append(text); Typed(text.Length);
    }
    public void Uncertain() { LastInputAt = DateTimeOffset.UtcNow; pending.Clear(); uncertain = true; typedUnits = 0; typedText.Clear(); }
    public void Clear() { pending.Clear(); typedUnits = 0; typedText.Clear(); exactTyping = false; uncertain = false; LastInputAt = default; }
    public void RefreshSources(Func<string, AuditOrigin, AuditOrigin> resolve)
    {
        for (int i = 0; i < pending.Count; i++) if (pending[i].Segments is null && pending[i].Origin.Detection != "local-lineage") pending[i] = pending[i] with { Origin = resolve(pending[i].Text, pending[i].Origin) };
    }
    public bool HasPending => pending.Count > 0 || typedUnits > 0 || uncertain;
    public IReadOnlyList<AuditSpan> Reconcile(string previous, string next, DateTimeOffset now, int? anchor = null, int selectionLength = 0)
    {
        if (exactTyping && now - LastInputAt > TimeSpan.FromSeconds(20)) Uncertain();
        int prefix = 0, suffix = 0;
        while (prefix < previous.Length && prefix < next.Length && previous[prefix] == next[prefix]) prefix++;
        if (anchor is not null) prefix = Math.Min(prefix, Math.Clamp(anchor.Value, 0, previous.Length));
        while (suffix < previous.Length - prefix && suffix < next.Length - prefix && previous[^(suffix + 1)] == next[^(suffix + 1)]) suffix++;
        if (anchor is not null) suffix = Math.Min(suffix, Math.Max(0, previous.Length - anchor.Value - selectionLength));
        var addition = next.Substring(prefix, next.Length - prefix - suffix);
        pending.RemoveAll(p => now - p.At > TimeSpan.FromSeconds(20));
        var matches = new List<AuditSpan>();
        if (!uncertain)
        {
            foreach (var paste in pending)
            {
                var at = addition.IndexOf(paste.Text, StringComparison.Ordinal);
                if (paste.Text.Length == 0 || at < 0 || addition.IndexOf(paste.Text, at + 1, StringComparison.Ordinal) >= 0 || matches.Any(s => at < s.Start + s.Length && s.Start < at + paste.Text.Length)) { matches.Clear(); uncertain = true; break; }
                if (paste.Segments is null) matches.Add(new(at, paste.Text.Length, paste.Origin));
                else foreach (var span in paste.Segments) matches.Add(span with { Start = at + span.Start });
            }
        }
        var rest = addition.Length - matches.Sum(s => s.Length);
        var unmatched = new System.Text.StringBuilder(); int cursor = 0;
        foreach (var match in matches.OrderBy(s => s.Start)) { unmatched.Append(addition.AsSpan(cursor, match.Start - cursor)); cursor = match.Start + match.Length; }
        unmatched.Append(addition.AsSpan(cursor));
        var textMatches = !exactTyping || string.Equals(typedText.ToString(), unmatched.ToString(), StringComparison.Ordinal);
        var remainingOrigin = !uncertain && textMatches && typedUnits == rest && typedUnits > 0 ? new AuditOrigin("typed", "none", "keyboard") : new("unobserved", "unknown", "none");
        if (uncertain) matches.Clear();
        var result = new List<AuditSpan>(); int position = 0;
        foreach (var match in matches.OrderBy(s => s.Start))
        {
            if (match.Start > position) result.Add(new(prefix + position, match.Start - position, remainingOrigin));
            result.Add(match with { Start = prefix + match.Start }); position = match.Start + match.Length;
        }
        if (position < addition.Length) result.Add(new(prefix + position, addition.Length - position, remainingOrigin));
        Clear(); return result;
    }
    public AuditOrigin Resolve(string previous, string next, DateTimeOffset now)
    {
        var spans = Reconcile(previous, next, now);
        return spans.Count == 1 ? spans[0].Origin : new("unobserved", "unknown", "none");
    }
}
