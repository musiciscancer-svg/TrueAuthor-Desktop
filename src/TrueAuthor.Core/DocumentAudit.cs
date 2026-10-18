namespace TrueAuthor.Core;

public sealed record AuditOrigin(string Kind, string Category, string Detection, string Source = "unknown", string Host = "", string PassageId = "", string LocalUrl = "");
public sealed record AuditSpan(int Start, int Length, AuditOrigin Origin);
public sealed record AuditPassage(string Id, int Characters, DateTimeOffset RecordedAt, AuditOrigin Origin, string OriginalText);
public sealed record AuditActivity(int TypedInserted, int PasteInserted, int PreexistingInserted, int UnobservedInserted, int EditEvents, int PasteEvents, int DeleteEvents, bool Complete);
public sealed record AuditSnapshot(string Text, int Inserted, int Deleted, int Gaps, IReadOnlyList<AuditSpan> Spans, IReadOnlyList<AuditPassage>? Passages = null, AuditActivity? Activity = null);

/// <summary>Conservative local lineage. Unexplained changes never become typing.</summary>
public sealed class DocumentAudit
{
    public string Text { get; private set; } = "";
    readonly List<AuditOrigin> origins = [];
    readonly List<AuditPassage> passages = [];
    public IReadOnlyList<AuditPassage> Passages => passages;
    public int Inserted { get; private set; }
    public int Deleted { get; private set; }
    public int Gaps { get; private set; }
    public AuditActivity Activity { get; private set; } = new(0, 0, 0, 0, 0, 0, 0, true);

    public void Gap() => Gaps++;
    public IReadOnlyList<AuditSpan>? CopyLineage(string text, int? knownStart = null)
    {
        if (text.Length == 0) return null;
        var start = knownStart ?? Text.IndexOf(text, StringComparison.Ordinal);
        if (start < 0 || start + text.Length > Text.Length || Text.Substring(start, text.Length) != text || (knownStart is null && Text.IndexOf(text, start + 1, StringComparison.Ordinal) >= 0)) return null;
        return Spans().Where(s => s.Start < start + text.Length && start < s.Start + s.Length).Select(s => new AuditSpan(Math.Max(start, s.Start) - start, Math.Min(start + text.Length, s.Start + s.Length) - Math.Max(start, s.Start), s.Origin)).ToList();
    }
    public void Update(string next, AuditOrigin origin, int? selectionAnchor = null, int selectionLength = 0)
    {
        int prefix = 0, suffix = 0;
        while (prefix < Text.Length && prefix < next.Length && Text[prefix] == next[prefix]) prefix++;
        if (selectionAnchor is not null) prefix = Math.Min(prefix, Math.Clamp(selectionAnchor.Value, 0, Text.Length));
        while (suffix < Text.Length - prefix && suffix < next.Length - prefix && Text[^(suffix + 1)] == next[^(suffix + 1)]) suffix++;
        if (selectionAnchor is not null) suffix = Math.Min(suffix, Math.Max(0, Text.Length - selectionAnchor.Value - selectionLength));
        var removed = Text.Length - prefix - suffix;
        var added = next.Length - prefix - suffix;
        Activity = Activity with { TypedInserted = Activity.TypedInserted + (origin.Kind == "typed" ? added : 0), PasteInserted = Activity.PasteInserted + (origin.Kind == "paste" ? added : 0), PreexistingInserted = Activity.PreexistingInserted + (origin.Kind == "preexisting" ? added : 0), UnobservedInserted = Activity.UnobservedInserted + (origin.Kind is not ("typed" or "paste" or "preexisting") ? added : 0), EditEvents = Activity.EditEvents + (added > 0 || removed > 0 ? 1 : 0), DeleteEvents = Activity.DeleteEvents + (removed > 0 ? 1 : 0), PasteEvents = Activity.PasteEvents + (origin.Kind == "paste" && added > 0 ? 1 : 0) };
        if (added > 0 && origin.Kind == "paste" && origin.PassageId != "") passages.Add(new(origin.PassageId, added, DateTimeOffset.UtcNow, origin, next.Substring(prefix, added)));
        origins.RemoveRange(prefix, removed);
        origins.InsertRange(prefix, Enumerable.Repeat(origin, added));
        Inserted += added; Deleted += removed; Text = next;
    }
    public AuditSnapshot Snapshot() => new(Text, Inserted, Deleted, Gaps, Spans(), Passages, Activity);
    public void UpdateFromInput(string next, IReadOnlyList<AuditSpan> attribution, int? anchor = null, int selectionLength = 0)
    {
        int prefix = 0, suffix = 0;
        while (prefix < Text.Length && prefix < next.Length && Text[prefix] == next[prefix]) prefix++;
        if (anchor is not null) prefix = Math.Min(prefix, Math.Clamp(anchor.Value, 0, Text.Length));
        while (suffix < Text.Length - prefix && suffix < next.Length - prefix && Text[^(suffix + 1)] == next[^(suffix + 1)]) suffix++;
        if (anchor is not null) suffix = Math.Min(suffix, Math.Max(0, Text.Length - anchor.Value - selectionLength));
        var end = next.Length - suffix; var expected = prefix;
        foreach (var span in attribution)
        {
            if (span.Start != expected || span.Length <= 0 || span.Start + span.Length > end || span.Origin.Kind is not ("typed" or "paste" or "preexisting" or "unobserved")) throw new InvalidDataException("Input attribution must exactly cover only the changed insertion.");
            expected += span.Length;
        }
        if (expected != end) throw new InvalidDataException("Input attribution is incomplete.");
        Update(next, new("unobserved", "unknown", "none"), anchor, selectionLength);
        foreach (var span in attribution)
        {
            for (int at = span.Start; at < span.Start + span.Length; at++) origins[at] = span.Origin;
            if (span.Origin.Kind == "paste" && span.Origin.PassageId != "") passages.Add(new(span.Origin.PassageId, span.Length, DateTimeOffset.UtcNow, span.Origin, next.Substring(span.Start, span.Length)));
            if (span.Origin.Kind != "unobserved") Activity = Activity with { UnobservedInserted = Activity.UnobservedInserted - span.Length, TypedInserted = Activity.TypedInserted + (span.Origin.Kind == "typed" ? span.Length : 0), PasteInserted = Activity.PasteInserted + (span.Origin.Kind == "paste" ? span.Length : 0), PreexistingInserted = Activity.PreexistingInserted + (span.Origin.Kind == "preexisting" ? span.Length : 0), PasteEvents = Activity.PasteEvents + (span.Origin.Kind == "paste" ? 1 : 0) };
        }
    }
    /// <summary>Reconcile a delayed input notification only against the single unchanged recent edit, without inventing new edit counters.</summary>
    public bool AttributeRecentChange(AuditSnapshot before, IReadOnlyList<AuditSpan> attribution)
    {
        if (attribution.Count == 0 || !attribution.Any(s => s.Origin.Kind == "paste") || before.Text == Text) return false;
        var start = attribution[0].Start; var length = attribution.Sum(s => s.Length);
        if (start < 0 || start + length > origins.Count || origins.Skip(start).Take(length).Any(o => o.Kind != "unobserved")) return false;
        var replay = Restore(before); replay.UpdateFromInput(Text, attribution);
        if (replay.Inserted != Inserted || replay.Deleted != Deleted || replay.Gaps != Gaps || replay.Activity.EditEvents != Activity.EditEvents) return false;
        origins.Clear(); origins.AddRange(replay.origins);
        passages.Clear(); passages.AddRange(replay.passages); Activity = replay.Activity;
        return true;
    }
    public static DocumentAudit Restore(AuditSnapshot snapshot)
    {
        if (snapshot.Text.Length > 1_000_000 || snapshot.Inserted < 0 || snapshot.Deleted < 0 || snapshot.Gaps < 0 || snapshot.Inserted - snapshot.Deleted != snapshot.Text.Length) throw new InvalidDataException("Invalid saved audit counters.");
        var result = new DocumentAudit { Text = snapshot.Text, Inserted = snapshot.Inserted, Deleted = snapshot.Deleted, Gaps = snapshot.Gaps };
        foreach (var span in snapshot.Spans)
        {
            if (span.Start != result.origins.Count || span.Length < 1 || span.Length > snapshot.Text.Length - result.origins.Count) throw new InvalidDataException("Invalid saved passage map.");
            result.origins.AddRange(Enumerable.Repeat(span.Origin, span.Length));
        }
        if (result.origins.Count != snapshot.Text.Length) throw new InvalidDataException("Incomplete saved passage map.");
        result.passages.AddRange(snapshot.Passages ?? []);
        if (snapshot.Activity is not null)
        {
            var a = snapshot.Activity;
            if (new[] { a.TypedInserted, a.PasteInserted, a.PreexistingInserted, a.UnobservedInserted, a.EditEvents, a.PasteEvents, a.DeleteEvents }.Any(n => n < 0) || (long)a.TypedInserted + a.PasteInserted + a.PreexistingInserted + a.UnobservedInserted != snapshot.Inserted) throw new InvalidDataException("Invalid saved activity totals.");
            result.Activity = a;
        }
        else
        {
            int typed = snapshot.Spans.Where(s => s.Origin.Kind == "typed").Sum(s => s.Length), pasted = result.passages.Sum(p => p.Characters), preexisting = snapshot.Spans.Where(s => s.Origin.Kind == "preexisting").Sum(s => s.Length);
            result.Activity = new(typed, pasted, preexisting, Math.Max(0, snapshot.Inserted - typed - pasted - preexisting), 0, result.passages.Count, 0, false);
        }
        return result;
    }
    public IReadOnlyList<AuditSpan> Spans()
    {
        List<AuditSpan> result = [];
        for (int start = 0; start < origins.Count;)
        {
            int end = start + 1;
            while (end < origins.Count && origins[end] == origins[start]) end++;
            result.Add(new(start, end - start, origins[start])); start = end;
        }
        return result;
    }
}

