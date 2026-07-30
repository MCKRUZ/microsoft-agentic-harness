namespace Application.AI.Common.CQRS.Workflows.CancelRun;

/// <summary>
/// What a cancellation actually achieved.
/// </summary>
/// <remarks>
/// The distinction this exists to make: a run that had not started yet is stopped the moment it is
/// asked for, whereas a run already executing can only be <em>told</em> to stop and will do so when
/// its work next reaches a point it can. Reporting both as "cancelled" would tell a caller its work
/// had stopped when a model call may still be in flight — and a caller that believed it had would
/// immediately start a replacement run, against a workflow the first run still holds.
/// </remarks>
public sealed record CancelWorkflowRunResult
{
    /// <summary>Whether the run had already stopped when this was answered.</summary>
    /// <remarks>
    /// <see langword="false"/> means the run was executing and has been signalled: it will stop, and
    /// the caller confirms by reading the run's status as it would for any other outcome.
    /// </remarks>
    public required bool StoppedImmediately { get; init; }

    /// <summary>How many pending approvals were withdrawn along with the run.</summary>
    /// <remarks>
    /// Reported rather than kept internal because it is the visible consequence for other people: each
    /// one is a request that an approver could see in their queue a moment ago and can no longer act
    /// on.
    /// </remarks>
    public required int WithdrawnApprovals { get; init; }
}
