namespace Domain.AI.KnowledgeGraph.Models;

/// <summary>
/// A discrete unit of knowledge persisted across agent sessions. Memory records
/// carry a feedback-adjusted weight that decays over time via EMA if not accessed.
/// </summary>
public sealed record MemoryRecord
{
    /// <summary>Unique identifier, typically a deterministic hash of Content.</summary>
    public required string Id { get; init; }

    /// <summary>The knowledge content (a fact, observation, or learned pattern).</summary>
    public required string Content { get; init; }

    /// <summary>Origin of this memory (e.g., session ID, agent name).</summary>
    public required string Source { get; init; }

    /// <summary>Feedback-adjusted weight (0.0–1.0). Subject to EMA decay.</summary>
    public required double Weight { get; init; }

    /// <summary>When this memory was first created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When last accessed via Recall or Improve. Used for decay calculation.</summary>
    public required DateTimeOffset LastAccessedAt { get; init; }

    /// <summary>Number of times recalled. Higher counts indicate frequently useful knowledge.</summary>
    public required int AccessCount { get; init; }

    /// <summary>Arbitrary metadata (entity types, topic tags). Strings for graph portability.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}
