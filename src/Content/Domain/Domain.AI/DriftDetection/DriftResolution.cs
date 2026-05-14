namespace Domain.AI.DriftDetection;

/// <summary>
/// How a detected drift was ultimately resolved.
/// </summary>
public enum DriftResolutionType
{
    /// <summary>A learning entry was applied that corrected the drift cause.</summary>
    LearningApplied,
    /// <summary>The baseline was adjusted to reflect intentional quality changes.</summary>
    BaselineAdjusted,
    /// <summary>An operator manually dismissed the drift as a false positive.</summary>
    ManualDismissal,
    /// <summary>A Phase 2 escalation resolved the underlying issue.</summary>
    EscalationResolved
}

/// <summary>
/// Records how and when a <see cref="DriftEvent"/> was resolved.
/// </summary>
public sealed record DriftResolution
{
    /// <summary>The mechanism by which this drift was resolved.</summary>
    public required DriftResolutionType ResolvedBy { get; init; }

    /// <summary>
    /// Identifier linking to the resolving entity (learning ID, escalation ID, etc.).
    /// </summary>
    public required string ResolutionId { get; init; }

    /// <summary>When the drift was resolved.</summary>
    public required DateTimeOffset ResolvedAt { get; init; }
}
