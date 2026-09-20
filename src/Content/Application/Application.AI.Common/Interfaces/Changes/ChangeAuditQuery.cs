using Domain.AI.Changes;

namespace Application.AI.Common.Interfaces.Changes;

/// <summary>
/// Query DTO for retrieving change audit records via
/// <see cref="IChangeAuditWriter.GetRecordsAsync"/>. All fields are optional filters.
/// </summary>
public sealed record ChangeAuditQuery
{
    /// <summary>Start of the query window (inclusive). Null leaves the window open on the left.</summary>
    public DateTimeOffset? Start { get; init; }

    /// <summary>End of the query window (inclusive). Null leaves the window open on the right.</summary>
    public DateTimeOffset? End { get; init; }

    /// <summary>Filter by the proposal this decision was made on.</summary>
    public string? ProposalId { get; init; }

    /// <summary>Filter by the gate that produced the decision.</summary>
    public string? GateKey { get; init; }

    /// <summary>Filter by the gate's verdict.</summary>
    public GateAction? Decision { get; init; }

    /// <summary>Filter by correlation id.</summary>
    public string? CorrelationId { get; init; }
}
