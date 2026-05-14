namespace Domain.AI.Learnings;

/// <summary>
/// Identifies what created a learning entry -- a human correction, drift event,
/// escalation resolution, or agent self-improvement. The <see cref="SourceId"/>
/// correlates back to the originating entity (e.g., escalation ID, drift event ID).
/// </summary>
public sealed record LearningSource
{
    /// <summary>The origin type that produced this learning.</summary>
    public required LearningSourceType SourceType { get; init; }

    /// <summary>
    /// Identifier of the originating entity (escalation ID, drift event ID, user session ID).
    /// Used for audit trail correlation.
    /// </summary>
    public required string SourceId { get; init; }

    /// <summary>Human-readable description of how this learning was created.</summary>
    public required string SourceDescription { get; init; }
}
