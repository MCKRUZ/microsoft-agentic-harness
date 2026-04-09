namespace Domain.AI.Prompts;

/// <summary>
/// Reports what changed between two prompt snapshots. Helps identify
/// which specific changes caused a prompt cache break.
/// </summary>
public sealed record PromptCacheBreakReport
{
    /// <summary>Whether the system prompt changed.</summary>
    public required bool SystemChanged { get; init; }

    /// <summary>Whether the combined tool schemas changed.</summary>
    public required bool ToolsChanged { get; init; }

    /// <summary>Names of specific tools whose schemas changed.</summary>
    public required IReadOnlyList<string> ChangedToolNames { get; init; }

    /// <summary>The previous snapshot.</summary>
    public required PromptHashSnapshot Previous { get; init; }

    /// <summary>The current snapshot.</summary>
    public required PromptHashSnapshot Current { get; init; }

    /// <summary>Whether any change was detected.</summary>
    public bool HasChanges => SystemChanged || ToolsChanged;
}
