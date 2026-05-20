namespace Domain.AI.KnowledgeGraph.Models;

/// <summary>
/// Parameters for a cross-session memory recall operation. Supports filtering
/// by minimum weight and by source.
/// </summary>
public sealed record MemoryQuery
{
    /// <summary>Natural language query to match against stored memories.</summary>
    public required string Query { get; init; }

    /// <summary>Maximum number of memories to return (default: 10).</summary>
    public int TopK { get; init; } = 10;

    /// <summary>Minimum weight threshold. Memories below this are excluded (default: 0.1).</summary>
    public double MinWeight { get; init; } = 0.1;

    /// <summary>Optional source filter. Null means all sources.</summary>
    public string? Source { get; init; }
}
