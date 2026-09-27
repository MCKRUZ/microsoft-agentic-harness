namespace Application.AI.Common.Interfaces.Governance;

/// <summary>
/// Query DTO for retrieving governance audit records via
/// <see cref="IGovernanceAuditService.GetRecordsAsync"/>. All fields are optional filters.
/// </summary>
public sealed record GovernanceAuditQuery
{
    /// <summary>
    /// Start of the query window (inclusive). When both <see cref="Start"/> and <see cref="End"/>
    /// are provided, Start must be before End.
    /// </summary>
    public DateTimeOffset? Start { get; init; }

    /// <summary>End of the query window (inclusive).</summary>
    public DateTimeOffset? End { get; init; }

    /// <summary>Filter by the agent whose action was evaluated.</summary>
    public string? AgentId { get; init; }
}
