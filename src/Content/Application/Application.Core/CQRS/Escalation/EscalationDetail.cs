namespace Application.Core.CQRS.Escalation;

/// <summary>Lifecycle state of an escalation as observed by the read surface.</summary>
public enum EscalationReadStatus
{
    /// <summary>The escalation is awaiting approver decisions; <see cref="EscalationDetail.Pending"/> is populated.</summary>
    Pending = 0,

    /// <summary>The escalation has a final verdict; <see cref="EscalationDetail.Outcome"/> is populated.</summary>
    Resolved
}

/// <summary>
/// The discriminated read model for a single escalation: exactly one of
/// <see cref="Pending"/> or <see cref="Outcome"/> is populated, selected by <see cref="Status"/>.
/// Construct via the static factories so the pairing invariant always holds.
/// </summary>
public sealed record EscalationDetail
{
    /// <summary>Whether the escalation is still pending or already resolved.</summary>
    public required EscalationReadStatus Status { get; init; }

    /// <summary>The pending request summary. Non-null iff <see cref="Status"/> is <see cref="EscalationReadStatus.Pending"/>.</summary>
    public EscalationSummary? Pending { get; init; }

    /// <summary>The resolved outcome. Non-null iff <see cref="Status"/> is <see cref="EscalationReadStatus.Resolved"/>.</summary>
    public EscalationOutcomeSummary? Outcome { get; init; }

    /// <summary>Creates a detail for a still-pending escalation.</summary>
    /// <param name="pending">The pending summary. Must not be null.</param>
    public static EscalationDetail ForPending(EscalationSummary pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        return new EscalationDetail { Status = EscalationReadStatus.Pending, Pending = pending };
    }

    /// <summary>Creates a detail for a resolved escalation.</summary>
    /// <param name="outcome">The resolved outcome summary. Must not be null.</param>
    public static EscalationDetail ForResolved(EscalationOutcomeSummary outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return new EscalationDetail { Status = EscalationReadStatus.Resolved, Outcome = outcome };
    }
}
