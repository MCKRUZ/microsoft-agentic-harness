namespace Domain.AI.Compaction;

/// <summary>
/// The outcome of a compaction operation. Contains the boundary marker,
/// new message history, and metrics.
/// </summary>
public sealed record CompactionResult
{
    /// <summary>Whether the compaction succeeded.</summary>
    public required bool Success { get; init; }

    /// <summary>The boundary marker inserted into the history (null on failure).</summary>
    public CompactionBoundaryMessage? Boundary { get; init; }

    /// <summary>Error message if compaction failed.</summary>
    public string? Error { get; init; }

    /// <summary>Creates a successful result.</summary>
    public static CompactionResult Succeeded(CompactionBoundaryMessage boundary) =>
        new() { Success = true, Boundary = boundary };

    /// <summary>Creates a failed result.</summary>
    public static CompactionResult Failed(string error) =>
        new() { Success = false, Error = error };
}
