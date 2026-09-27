using Domain.Common;
using MediatR;

namespace Application.AI.Common.CQRS.Schedules;

/// <summary>Pauses a schedule. A paused schedule is never claimed by the tick service.</summary>
public sealed record PauseScheduleCommand : IRequest<Result>
{
    /// <summary>The schedule to pause.</summary>
    public required string ScheduleId { get; init; }

    /// <summary>Stable identity of the calling principal, resolved from its token or ambient scope.</summary>
    public required string OwnerId { get; init; }

    /// <summary>Tenant of the calling principal, when the host resolves one.</summary>
    public string? TenantId { get; init; }

    /// <summary>The <c>ScheduleSummary.Version</c> the caller last read.</summary>
    public required int ExpectedVersion { get; init; }
}
