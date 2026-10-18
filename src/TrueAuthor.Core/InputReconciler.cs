namespace TrueAuthor.Core;

/// <summary>Correlates input intent with an exact document change; ambiguity stays unobserved.</summary>
public sealed class InputReconciler
{
    readonly List<(string Text, AuditOrigin Origin, DateTimeOffset At, IReadOnlyList<AuditSpan>? Segments)> pending = [];
    int typedUnits;
    bool uncertain;
    public void Paste(string text, AuditOrigin origin, DateTimeOffset now, IReadOnlyList<AuditSpan>? segments = null)
    {
        if (pending.Count >= 20) { pending.Clear(); uncertain = true; }
        pending.Add((text, origin, now, segments));
    }
    public void Typed(int units) { if (units > 0 && units <= 1_000_000) typedUnits = Math.Min(1_000_000, typedUnits + units); }
    public void Uncertain() { uncertain = true; typedUnits = 0; }
    public void Clear() { pending.Clear(); typedUnits = 0; uncertain = false; }
    public void RefreshSources(Func<string, AuditOrigin, AuditOrigin> resolve)
    {
        for (int i = 0; i < pending.Count; i++) if (pending[i].Segments is null && pending[i].Origin.Detection != "local-lineage") pending[i] = pending[i] with { Origin = resolve(pending[i].Text, pending[i].Origin) };
    }
    public bool HasPending => pending.Count > 0 || typedUnits > 0;
    public IReadOnlyList<AuditSpan> Reconcile(string previous, string next, DateTimeOffset now, int? anchor = null, int selectionLength = 0)
    {
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
        var remainingOrigin = !uncertain && typedUnits == rest && typedUnits > 0 ? new AuditOrigin("typed", "none", "keyboard") : new("unobserved", "unknown", "none");
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
