using Application.AI.Common.CQRS.Schedules;
using Domain.AI.Runs;

namespace Presentation.ExecutionApi.DTOs;

/// <summary>Request body for creating a recurring schedule.</summary>
public sealed record CreateScheduleRequest
{
    /// <summary>Which kind of work each enqueued run performs.</summary>
    public required RunKind Kind { get; init; }

    /// <summary>Identifier of the thing each enqueued run targets — a stored workflow's id for <see cref="RunKind.Workflow"/>.</summary>
    public required string TargetId { get; init; }

    /// <summary>Standard five-field cron expression.</summary>
    public required string CronExpression { get; init; }

    /// <summary>IANA time zone the cron expression's fields are evaluated in.</summary>
    public required string TimeZoneId { get; init; }

    /// <summary>Earliest time of day, in <see cref="TimeZoneId"/>, a fire may occur.</summary>
    public TimeSpan? ActiveHoursStart { get; init; }

    /// <summary>Latest time of day, in <see cref="TimeZoneId"/>, a fire may occur.</summary>
    public TimeSpan? ActiveHoursEnd { get; init; }

    /// <summary>Minimum gap enforced between two fires of this schedule.</summary>
    public TimeSpan Cooldown { get; init; } = TimeSpan.Zero;

    /// <summary>What happens when the host was not running at one or more scheduled ticks.</summary>
    public MissedRunPolicy MissedRunPolicy { get; init; } = MissedRunPolicy.Skip;
}

/// <summary>Request body for pausing or resuming a schedule.</summary>
public sealed record ScheduleVersionRequest
{
    /// <summary>The <see cref="ScheduleSummary.Version"/> the caller last read.</summary>
    public required int ExpectedVersion { get; init; }
}

/// <summary>
/// The caller-visible view of a schedule returned over HTTP — a Presentation-layer projection of
/// <see cref="ScheduleSummary"/>, matching <c>WorkflowRunResponse</c>'s convention of never returning
/// an Application/CQRS type as the wire shape directly. <see cref="ScheduleSummary"/> already omits
/// the envelope, so this projection is a pure rename/reshape rather than a second redaction step —
/// but keeping the two types distinct means a future change to <see cref="ScheduleSummary"/> made for
/// internal (e.g. tool-facing) reasons cannot silently change the public REST contract too.
/// </summary>
public sealed record ScheduleResponse
{
    /// <summary>Maps from <see cref="ScheduleSummary.ScheduleId"/>.</summary>
    public required string ScheduleId { get; init; }

    /// <summary>Maps from <see cref="ScheduleSummary.Kind"/>.</summary>
    public required RunKind Kind { get; init; }

    /// <summary>Maps from <see cref="ScheduleSummary.TargetId"/>.</summary>
    public required string TargetId { get; init; }

    /// <summary>Maps from <see cref="ScheduleSummary.CronExpression"/>.</summary>
    public required string CronExpression { get; init; }

    /// <summary>Maps from <see cref="ScheduleSummary.TimeZoneId"/>.</summary>
    public required string TimeZoneId { get; init; }

    /// <summary>Maps from <see cref="ScheduleSummary.ActiveHoursStart"/>.</summary>
    public TimeSpan? ActiveHoursStart { get; init; }

    /// <summary>Maps from <see cref="ScheduleSummary.ActiveHoursEnd"/>.</summary>
    public TimeSpan? ActiveHoursEnd { get; init; }

    /// <summary>Maps from <see cref="ScheduleSummary.Cooldown"/>.</summary>
    public required TimeSpan Cooldown { get; init; }

    /// <summary>Maps from <see cref="ScheduleSummary.MissedRunPolicy"/>.</summary>
    public required MissedRunPolicy MissedRunPolicy { get; init; }

    /// <summary>Maps from <see cref="ScheduleSummary.Enabled"/>.</summary>
    public required bool Enabled { get; init; }

    /// <summary>Maps from <see cref="ScheduleSummary.NextFireAt"/>.</summary>
    public required DateTimeOffset NextFireAt { get; init; }

    /// <summary>Maps from <see cref="ScheduleSummary.LastFiredAt"/>.</summary>
    public DateTimeOffset? LastFiredAt { get; init; }

    /// <summary>Maps from <see cref="ScheduleSummary.CreatedAt"/>.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Maps from <see cref="ScheduleSummary.Version"/> — pass this back unchanged when pausing/resuming/deleting.</summary>
    public required int Version { get; init; }

    /// <summary>Projects an Application-layer summary onto the wire shape.</summary>
    public static ScheduleResponse FromSummary(ScheduleSummary summary) => new()
    {
        ScheduleId = summary.ScheduleId,
        Kind = summary.Kind,
        TargetId = summary.TargetId,
        CronExpression = summary.CronExpression,
        TimeZoneId = summary.TimeZoneId,
        ActiveHoursStart = summary.ActiveHoursStart,
        ActiveHoursEnd = summary.ActiveHoursEnd,
        Cooldown = summary.Cooldown,
        MissedRunPolicy = summary.MissedRunPolicy,
        Enabled = summary.Enabled,
        NextFireAt = summary.NextFireAt,
        LastFiredAt = summary.LastFiredAt,
        CreatedAt = summary.CreatedAt,
        Version = summary.Version,
    };
}
