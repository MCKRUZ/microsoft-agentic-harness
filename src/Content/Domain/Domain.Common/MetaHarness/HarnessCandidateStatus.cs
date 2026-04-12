namespace Domain.Common.MetaHarness;

/// <summary>
/// Lifecycle states for a <see cref="HarnessCandidate"/> within an optimization run.
/// </summary>
public enum HarnessCandidateStatus
{
    /// <summary>The candidate has been proposed but not yet evaluated.</summary>
    Proposed,
    /// <summary>The candidate has been fully evaluated and scored.</summary>
    Evaluated,
    /// <summary>Evaluation or proposal failed; see <see cref="HarnessCandidate.FailureReason"/>.</summary>
    Failed,
    /// <summary>The candidate has been promoted to the active harness configuration.</summary>
    Promoted
}
