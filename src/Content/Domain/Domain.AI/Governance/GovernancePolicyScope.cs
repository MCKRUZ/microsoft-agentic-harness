namespace Domain.AI.Governance;

/// <summary>
/// The scope at which a governance policy applies.
/// Used in conflict resolution when multiple policies match.
/// </summary>
public enum GovernancePolicyScope
{
    /// <summary>Applies to all agents across all tenants.</summary>
    Global,
    /// <summary>Applies to all agents within a tenant.</summary>
    Tenant,
    /// <summary>Applies to all agents within an organization.</summary>
    Organization,
    /// <summary>Applies to a specific agent.</summary>
    Agent
}
