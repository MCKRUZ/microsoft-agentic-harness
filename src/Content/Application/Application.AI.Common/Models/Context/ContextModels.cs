namespace Application.AI.Common.Models.Context;

/// <summary>
/// A file loaded into agent context with token tracking.
/// </summary>
/// <param name="Name">Display name of the file.</param>
/// <param name="Path">Source file path.</param>
/// <param name="Content">Loaded content (may be truncated).</param>
/// <param name="TokenCount">Estimated token count of the loaded content.</param>
/// <param name="IsTruncated">Whether the content was truncated to fit budget.</param>
/// <param name="OriginalTokenCount">Original token count before truncation, if truncated.</param>
public record LoadedContextFile(
    string Name,
    string Path,
    string Content,
    int TokenCount,
    bool IsTruncated = false,
    int? OriginalTokenCount = null);

/// <summary>
/// Information about an artifact that was truncated or skipped during context loading.
/// </summary>
public record TruncatedArtifactInfo(
    string Name,
    int OriginalTokens,
    int? IncludedTokens,
    TruncationReason Reason);

/// <summary>
/// Why an artifact was not fully included in context.
/// </summary>
public enum TruncationReason
{
    /// <summary>Content was truncated to fit within token budget.</summary>
    Truncated,
    /// <summary>Content was skipped entirely — would exceed budget.</summary>
    Skipped,
    /// <summary>Content was skipped as low priority after budget was mostly consumed.</summary>
    SkippedLowPriority
}

/// <summary>
/// Result of loading Tier 1 context (always-loaded organizational context).
/// </summary>
public record Tier1LoadedContext(
    IReadOnlyList<LoadedContextFile> Files,
    int TotalTokens,
    int MaxTokens);

/// <summary>
/// Result of loading Tier 2 context (on-demand project/skill context).
/// </summary>
public record Tier2LoadedContext(
    IReadOnlyList<LoadedContextFile> Files,
    int TotalTokens,
    int MaxTokens,
    IReadOnlyList<TruncatedArtifactInfo> TruncatedFiles);

/// <summary>
/// Configuration for Tier 3 on-demand context access.
/// </summary>
public record Tier3AccessConfig(
    IReadOnlyList<string> AllowedLookupPaths,
    string? FallbackPrompt);

/// <summary>
/// Complete assembled context from all three tiers.
/// </summary>
public record AssembledContext(
    Tier1LoadedContext Tier1,
    Tier2LoadedContext Tier2,
    Tier3AccessConfig Tier3,
    int TotalTokens,
    string FormattedPromptSection);
