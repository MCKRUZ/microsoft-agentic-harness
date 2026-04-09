namespace Domain.AI.Prompts;

/// <summary>
/// A point-in-time hash snapshot of the system prompt and tool schemas.
/// Used to detect what changed between turns for debugging and cost optimization.
/// </summary>
public sealed record PromptHashSnapshot
{
    /// <summary>SHA256 hash of the full system prompt text.</summary>
    public required string SystemHash { get; init; }

    /// <summary>SHA256 hash of all tool schemas combined.</summary>
    public required string ToolsHash { get; init; }

    /// <summary>Per-tool schema hashes for fine-grained change detection.</summary>
    public required IReadOnlyDictionary<string, string> PerToolHashes { get; init; }

    /// <summary>When this snapshot was taken.</summary>
    public required DateTimeOffset Timestamp { get; init; }
}
