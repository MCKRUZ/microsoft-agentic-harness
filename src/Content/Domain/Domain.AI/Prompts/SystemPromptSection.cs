namespace Domain.AI.Prompts;

/// <summary>
/// A single composable section of the system prompt. Sections are computed by providers,
/// cached when cacheable, ordered by priority, and concatenated to form the full prompt.
/// </summary>
/// <param name="Name">Human-readable name for debugging and telemetry.</param>
/// <param name="Type">The section type, used for caching and invalidation.</param>
/// <param name="Priority">Sort order — lower values appear earlier in the prompt.</param>
/// <param name="IsCacheable">When true, the section is memoized until explicitly invalidated.</param>
/// <param name="EstimatedTokens">Estimated token count for budget-aware assembly.</param>
/// <param name="Content">The rendered text content of this section.</param>
public sealed record SystemPromptSection(
    string Name,
    SystemPromptSectionType Type,
    int Priority,
    bool IsCacheable,
    int EstimatedTokens,
    string Content);
