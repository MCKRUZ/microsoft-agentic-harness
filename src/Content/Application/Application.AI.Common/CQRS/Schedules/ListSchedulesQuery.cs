using Domain.Common;
using MediatR;

namespace Application.AI.Common.CQRS.Schedules;

/// <summary>Lists every schedule visible to the caller.</summary>
public sealed record ListSchedulesQuery : IRequest<Result<IReadOnlyList<ScheduleSummary>>>
{
    /// <summary>Stable identity of the calling principal, resolved from its token or ambient scope.</summary>
    public required string OwnerId { get; init; }

    /// <summary>Tenant of the calling principal, when the host resolves one.</summary>
    public string? TenantId { get; init; }
}
