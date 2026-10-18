using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrueAuthor.Core;

/// <summary>Where a piece of copied text came from. Mirrors <c>SourceInfo</c> in packages/source-map.</summary>
public sealed record SourceInfo
{
    public static readonly string[] Categories = ["ai", "academic", "reference", "document", "pdf", "reference-manager", "code", "communication", "web", "app", "unknown"];
    public static readonly string[] Detections = ["extension", "clipboard-html", "window-title", "process", "none"];
    public static readonly string[] Confidences = ["high", "medium", "low"];

    public required string Category { get; init; }
    public required string Name { get; init; }
    public required string Detection { get; init; }
    public required string Confidence { get; init; }
    public string? Url { get; init; }
    public string? Domain { get; init; }
    public string? Title { get; init; }
    public string? App { get; init; }

    public static SourceInfo Unknown { get; } = new() { Category = "unknown", Name = "Unknown source", Detection = "none", Confidence = "low" };

    /// <summary>Ranks evidence strength so a richer report (for example from the browser extension) can replace a weaker one.</summary>
    public int Strength => Array.IndexOf(Confidences, Confidence) switch { 0 => 30, 1 => 20, _ => 10 } + (Detections.Length - Array.IndexOf(Detections, Detection));

    /// <summary>Validates a source received over the local API: known enum values only and bounded strings.</summary>
    public static SourceInfo? Sanitize(SourceInfo? source)
    {
        if (source is null || !Categories.Contains(source.Category) || !Detections.Contains(source.Detection) || !Confidences.Contains(source.Confidence)) return null;
        static string? Clip(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];
        var name = Clip(source.Name, 200);
        if (name is null) return null;
        var url = Clip(source.Url, 2048);
        if (url is not null && !(Uri.TryCreate(url, UriKind.Absolute, out var parsed) && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))) url = null;
        return source with { Name = name, Url = url, Domain = Clip(source.Domain, 253), Title = Clip(source.Title, 300), App = Clip(source.App, 120) };
    }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
}

public abstract record Classification
{
    public sealed record Blocked(string Reason) : Classification;
    public sealed record Allowed(SourceInfo Source) : Classification;
}

public sealed record WritingSurface(string Name, string DocumentTitle);
