using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.Learnings;

/// <summary>
/// Soft-deletes a learning entry. The learning remains in the graph for audit
/// but is excluded from future search results.
/// </summary>
public sealed record ForgetCommand : IRequest<Result>
{
    /// <summary>ID of the learning to soft-delete.</summary>
    public required Guid LearningId { get; init; }

    /// <summary>Reason for deletion (audit trail).</summary>
    public required string Reason { get; init; }
}
