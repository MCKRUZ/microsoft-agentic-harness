using Domain.AI.DriftDetection;

namespace Application.AI.Common.Interfaces.DriftDetection;

/// <summary>
/// Query DTO for retrieving drift audit records. All fields are optional filters.
/// </summary>
public sealed record DriftAuditQuery
{
    /// <summary>Start of the query window. When both Start and End are provided, Start must be before End.</summary>
    public DateTimeOffset? Start { get; init; }

    /// <summary>End of the query window.</summary>
    public DateTimeOffset? End { get; init; }

    /// <summary>Filter by audit record type.</summary>
    public DriftAuditRecordType? RecordType { get; init; }

    /// <summary>Filter by originating drift event ID.</summary>
    public Guid? EventId { get; init; }
}
