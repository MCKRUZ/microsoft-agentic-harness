namespace Domain.AI.Compaction;

/// <summary>
/// A marker inserted into the message history at the point where compaction occurred.
/// Enables surgical replay, debugging, and chain relinking after compaction.
/// </summary>
public sealed record CompactionBoundaryMessage
{
    /// <summary>Unique identifier for this compaction event.</summary>
    public required string Id { get; init; }

    /// <summary>What triggered this compaction.</summary>
    public required CompactionTrigger Trigger { get; init; }

    /// <summary>Which strategy was used.</summary>
    public required CompactionStrategy Strategy { get; init; }

    /// <summary>Token count before compaction.</summary>
    public required int PreCompactionTokens { get; init; }

    /// <summary>Token count after compaction.</summary>
    public required int PostCompactionTokens { get; init; }

    /// <summary>Tokens saved by this compaction.</summary>
    public int TokensSaved => PreCompactionTokens - PostCompactionTokens;

    /// <summary>IDs of message segments preserved through compaction.</summary>
    public IReadOnlyList<string> PreservedSegmentIds { get; init; } = [];

    /// <summary>When the compaction occurred.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>The summary text that replaced the compacted messages.</summary>
    public required string Summary { get; init; }
}
