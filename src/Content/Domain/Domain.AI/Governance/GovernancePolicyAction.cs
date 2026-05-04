namespace Domain.AI.Governance;

/// <summary>
/// The action a governance policy rule specifies when its condition matches.
/// Maps to AGT's PolicyAction but is owned by the harness domain.
/// </summary>
public enum GovernancePolicyAction
{
    /// <summary>Permit the action.</summary>
    Allow,
    /// <summary>Block the action.</summary>
    Deny,
    /// <summary>Log a warning but permit the action.</summary>
    Warn,
    /// <summary>Require human approval before proceeding.</summary>
    RequireApproval,
    /// <summary>Log the action without blocking.</summary>
    Log,
    /// <summary>Apply rate limiting to the action.</summary>
    RateLimit
}
