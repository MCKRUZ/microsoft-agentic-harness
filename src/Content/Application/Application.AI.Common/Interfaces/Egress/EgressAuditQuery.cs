namespace Application.AI.Common.Interfaces.Egress;

/// <summary>
/// Query DTO for retrieving egress audit records via
/// <see cref="IEgressAuditWriter.GetRecordsAsync"/>. All fields are optional filters.
/// </summary>
public sealed record EgressAuditQuery
{
    /// <summary>
    /// Start of the query window (inclusive). When both <see cref="Start"/> and <see cref="End"/>
    /// are provided, Start must be before End.
    /// </summary>
    public DateTimeOffset? Start { get; init; }

    /// <summary>End of the query window (inclusive).</summary>
    public DateTimeOffset? End { get; init; }

    /// <summary>Filter by decision verdict (true = allowed, false = denied).</summary>
    public bool? Allowed { get; init; }

    /// <summary>Filter by target host.</summary>
    public string? Host { get; init; }
}
