namespace Domain.AI.Context;

/// <summary>
/// A reference to a stored tool result. When a tool produces output exceeding
/// the configured size limit, the full content is persisted to disk and this
/// reference (with a preview) is kept in the conversation context.
/// </summary>
public sealed record ToolResultReference
{
    /// <summary>Unique identifier for this stored result.</summary>
    public required string ResultId { get; init; }

    /// <summary>The tool that produced this result.</summary>
    public required string ToolName { get; init; }

    /// <summary>The operation that produced this result, if applicable.</summary>
    public string? Operation { get; init; }

    /// <summary>Preview content kept in the conversation context.</summary>
    public required string PreviewContent { get; init; }

    /// <summary>Path to the full content on disk. Null if content was small enough to keep inline.</summary>
    public string? FullContentPath { get; init; }

    /// <summary>Total size of the full content in characters.</summary>
    public required int SizeChars { get; init; }

    /// <summary>Estimated token count of the full content.</summary>
    public int EstimatedTokens => SizeChars / 4;

    /// <summary>Whether the full content was persisted to disk.</summary>
    public bool IsPersistedToDisk => FullContentPath is not null;

    /// <summary>When this result was stored.</summary>
    public required DateTimeOffset Timestamp { get; init; }
}
