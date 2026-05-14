namespace Domain.AI.Escalation;

/// <summary>
/// Result of evaluating collected approver decisions against an approval strategy.
/// Returned by <c>IApprovalStrategy.EvaluateDecision()</c>.
/// </summary>
public sealed record ApprovalEvaluation
{
    /// <summary>Whether enough decisions have been collected to resolve the escalation.</summary>
    public required bool IsResolved { get; init; }

    /// <summary>The approval verdict. Only meaningful when <see cref="IsResolved"/> is true.</summary>
    public required bool IsApproved { get; init; }

    /// <summary>Approvers who have not yet responded. Empty when fully resolved.</summary>
    public required IReadOnlyList<string> PendingApprovers { get; init; }
}
