using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.Learnings;

/// <summary>
/// Fire-and-forget command dispatched by RecallQueryHandler for CQRS-clean access tracking.
/// Updates <c>LastAccessedAt</c> on retrieved learning entries.
/// </summary>
public sealed record RecordLearningAccessCommand : IRequest<Result>
{
    /// <summary>IDs of the learnings that were accessed during recall.</summary>
    public required IReadOnlyList<Guid> LearningIds { get; init; }

    /// <summary>When the access occurred.</summary>
    public required DateTimeOffset AccessedAt { get; init; }
}
