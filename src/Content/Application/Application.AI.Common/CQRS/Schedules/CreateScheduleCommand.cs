using Domain.AI.Bundles;
using Domain.AI.Runs;
using Domain.Common;
using MediatR;

namespace Application.AI.Common.CQRS.Schedules;

/// <summary>
/// Registers a recurring schedule that enqueues a run of <see cref="Kind"/> against
/// <see cref="TargetId"/> on a cron cadence.
/// </summary>
/// <remarks>
/// <strong><see cref="Envelope"/> is resolved at the transport boundary from the credential that
/// invoked this</strong>, exactly like <c>StartWorkflowRunCommand.Envelope</c> — and for the same
/// reason it cannot be resolved anywhere else: envelope resolution
/// (<c>ICapabilityEnvelopeResolver.Resolve</c>) reads an authenticated <c>ClaimsPrincipal</c>, which
/// only exists at an HTTP request boundary. A schedule fires with no live caller attached, so the
/// envelope it captures here is the one every future fire executes under — it is never re-resolved.
/// This is why schedule <em>creation</em> is an HTTP endpoint (<c>SchedulesController</c>) rather
/// than an agent tool operation; list/pause/resume/delete need no fresh envelope and are tool
/// operations too.
/// </remarks>
public sealed record CreateScheduleCommand : IRequest<Result<ScheduleSummary>>
{
    /// <summary>Which kind of work each enqueued run performs.</summary>
    public required RunKind Kind { get; init; }

    /// <summary>
    /// Identifier of the thing each enqueued run targets — a stored workflow's id for
    /// <see cref="RunKind.Workflow"/>.
    /// </summary>
    public required string TargetId { get; init; }

    /// <summary>Stable identity of the calling principal, resolved from its token.</summary>
    public required string OwnerId { get; init; }

    /// <summary>Tenant of the calling principal, when the host resolves one.</summary>
    public string? TenantId { get; init; }

    /// <summary>The grant every run this schedule fires executes under.</summary>
    public required CapabilityEnvelope Envelope { get; init; }

    /// <summary>Standard five-field cron expression.</summary>
    public required string CronExpression { get; init; }

    /// <summary>IANA time zone the cron expression's fields are evaluated in.</summary>
    public required string TimeZoneId { get; init; }

    /// <summary>Earliest time of day, in <see cref="TimeZoneId"/>, a fire may occur. Null means no lower bound.</summary>
    public TimeSpan? ActiveHoursStart { get; init; }

    /// <summary>Latest time of day, in <see cref="TimeZoneId"/>, a fire may occur. Null means no upper bound.</summary>
    public TimeSpan? ActiveHoursEnd { get; init; }

    /// <summary>Minimum gap enforced between two fires of this schedule.</summary>
    public TimeSpan Cooldown { get; init; } = TimeSpan.Zero;

    /// <summary>What happens when the host was not running at one or more scheduled ticks.</summary>
    public MissedRunPolicy MissedRunPolicy { get; init; } = MissedRunPolicy.Skip;
}
