using Domain.AI.Escalation;

namespace Application.Core.Escalation.Strategies;

/// <summary>
/// Counts a scoped set of approver decisions by verdict and resolves the highest-precedence
/// verdict present, so all three approval strategies apply one shared, tested precedence rule
/// instead of each re-deriving it.
/// </summary>
/// <remarks>
/// Precedence is deny &gt; revise &gt; approve: a single denial always wins over any number of
/// revisions or approvals, and a revision always wins over an approval. A hard no is never
/// softened into "try again", and "try again" is never silently treated as "proceed".
/// </remarks>
internal readonly struct VerdictTally
{
    /// <summary>Initializes a tally over an already-roster-scoped set of decisions.</summary>
    public VerdictTally(IReadOnlyList<ApproverDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(decisions);

        foreach (var decision in decisions)
        {
            switch (decision.Verdict)
            {
                case ApproverVerdict.Deny:
                    DenyCount++;
                    break;
                case ApproverVerdict.Revise:
                    ReviseCount++;
                    break;
                case ApproverVerdict.Approve:
                    ApproveCount++;
                    break;
                default:
                    // An undefined ApproverVerdict must never be silently excluded from every
                    // count: that let AllOf treat the decision as if it never happened (a
                    // resolved-Approved outcome when a real approver's vote was simply dropped),
                    // let Quorum's remaining-vote arithmetic under-count how many approvers had
                    // actually responded, and left AnyOf's precedence resolution with nothing
                    // counted at all — a crash, not a decision. Fail-closed like everything else
                    // in this subsystem: count it as a denial.
                    DenyCount++;
                    break;
            }
        }
    }

    /// <summary>Number of counted denials.</summary>
    public int DenyCount { get; }

    /// <summary>Number of counted revise requests.</summary>
    public int ReviseCount { get; }

    /// <summary>Number of counted approvals.</summary>
    public int ApproveCount { get; }

    /// <summary>Total decisions counted, across all three verdicts.</summary>
    public int Total => DenyCount + ReviseCount + ApproveCount;

    /// <summary>
    /// The highest-precedence verdict present in this tally (deny &gt; revise &gt; approve), or
    /// null when nothing has been counted.
    /// </summary>
    public ApproverVerdict? Resolve() => this switch
    {
        { DenyCount: > 0 } => ApproverVerdict.Deny,
        { ReviseCount: > 0 } => ApproverVerdict.Revise,
        { ApproveCount: > 0 } => ApproverVerdict.Approve,
        _ => null
    };
}
