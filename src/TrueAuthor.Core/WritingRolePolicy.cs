namespace TrueAuthor.Core;

/// <summary>Reference and communication surfaces supply provenance automatically; they are not destination document prompts.</summary>
public static class WritingRolePolicy
{
    public static bool IsAutomaticSource(Classification classification) => classification is Classification.Allowed allowed &&
        allowed.Source.Category is "ai" or "academic" or "reference" or "pdf" or "reference-manager" or "communication" or "code";
}
