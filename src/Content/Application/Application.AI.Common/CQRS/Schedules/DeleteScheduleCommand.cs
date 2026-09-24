using Domain.Common;
using MediatR;

namespace Application.AI.Common.CQRS.Schedules;

/// <summary>Deletes a schedule.</summary>
public sealed record DeleteScheduleCommand : IRequest<Result>
{
    /// <summary>The schedule to delete.</summary>
    public required string ScheduleId { get; init; }

    /// <summary>Stable identity of the calling principal, resolved from its token or ambient scope.</summary>
    public required string OwnerId { get; init; }

    /// <summary>Tenant of the calling principal, when the host resolves one.</summary>
    public string? TenantId { get; init; }
}
