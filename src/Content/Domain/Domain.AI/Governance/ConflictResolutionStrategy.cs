namespace Domain.AI.Governance;

/// <summary>
/// Strategy for resolving conflicts when multiple policy rules match the same action.
/// </summary>
public enum ConflictResolutionStrategy
{
    /// <summary>Any deny rule wins regardless of priority (safest).</summary>
    DenyOverrides,
    /// <summary>Any allow rule wins regardless of priority (most permissive).</summary>
    AllowOverrides,
    /// <summary>Highest-priority matching rule wins (default).</summary>
    PriorityFirstMatch,
    /// <summary>Most specific scope wins: Agent > Tenant > Global.</summary>
    MostSpecificWins
}
