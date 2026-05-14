namespace Domain.AI.DriftDetection;

/// <summary>
/// A detected drift occurrence, persisted as a knowledge graph node.
/// Links to the <see cref="DriftScore"/> that triggered it and optionally
/// to a <see cref="DriftResolution"/> when the drift is addressed.
/// </summary>
public sealed record DriftEvent
{
    /// <summary>Unique identifier for this event.</summary>
    public required Guid EventId { get; init; }

    /// <summary>The drift score that triggered this event.</summary>
    public required DriftScore DriftScore { get; init; }

    /// <summary>
    /// How this drift was resolved. Null while the drift is still outstanding.
    /// </summary>
    public DriftResolution? Resolution { get; init; }

    /// <summary>When the drift was first detected.</summary>
    public required DateTimeOffset DetectedAt { get; init; }
}
