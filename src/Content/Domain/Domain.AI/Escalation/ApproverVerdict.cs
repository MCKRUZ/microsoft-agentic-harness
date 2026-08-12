namespace Domain.AI.Escalation;

/// <summary>
/// An approver's verdict on an escalation request.
/// </summary>
/// <remarks>
/// <see cref="Deny"/> is deliberately the zero value: an absent, corrupted, or otherwise
/// unreadable verdict — a hand-edited durable row, a forward-versioned value this build does not
/// recognize, a legacy record predating this type — must default-construct or deserialize to a
/// denial, never to an approval or a revision. This is the fail-closed contract every reader of
/// this enum relies on.
/// </remarks>
public enum ApproverVerdict
{
    /// <summary>The approver refused the action.</summary>
    Deny = 0,

    /// <summary>The approver granted the action.</summary>
    Approve = 1,

    /// <summary>
    /// The approver asked the agent to revise its approach before retrying, carrying instructions
    /// on <see cref="ApproverDecision.Instructions"/>. Not an approval: the action does not
    /// proceed on this verdict alone.
    /// </summary>
    Revise = 2
}
