using Domain.AI.Runs;

namespace Application.AI.Common.CQRS.Schedules;

/// <summary>Caller-facing projection of a <see cref="ScheduleRecord"/> — never carries <see cref="ScheduleRecord.Envelope"/>.</summary>
public sealed record ScheduleSummary
{
    /// <summary>Maps from <see cref="ScheduleRecord.ScheduleId"/>.</summary>
    public required string ScheduleId { get; init; }

    /// <summary>Maps from <see cref="ScheduleRecord.Kind"/>.</summary>
    public required RunKind Kind { get; init; }

    /// <summary>Maps from <see cref="ScheduleRecord.TargetId"/>.</summary>
    public required string TargetId { get; init; }

    /// <summary>Maps from <see cref="ScheduleRecord.CronExpression"/>.</summary>
    public required string CronExpression { get; init; }

    /// <summary>Maps from <see cref="ScheduleRecord.TimeZoneId"/>.</summary>
    public required string TimeZoneId { get; init; }

    /// <summary>Maps from <see cref="ScheduleRecord.ActiveHoursStart"/>.</summary>
    public TimeSpan? ActiveHoursStart { get; init; }

    /// <summary>Maps from <see cref="ScheduleRecord.ActiveHoursEnd"/>.</summary>
    public TimeSpan? ActiveHoursEnd { get; init; }

    /// <summary>Maps from <see cref="ScheduleRecord.Cooldown"/>.</summary>
    public required TimeSpan Cooldown { get; init; }

    /// <summary>Maps from <see cref="ScheduleRecord.MissedRunPolicy"/>.</summary>
    public required MissedRunPolicy MissedRunPolicy { get; init; }

    /// <summary>Maps from <see cref="ScheduleRecord.Enabled"/>.</summary>
    public required bool Enabled { get; init; }

    /// <summary>Maps from <see cref="ScheduleRecord.NextFireAt"/>.</summary>
    public required DateTimeOffset NextFireAt { get; init; }

    /// <summary>Maps from <see cref="ScheduleRecord.LastFiredAt"/>.</summary>
    public DateTimeOffset? LastFiredAt { get; init; }

    /// <summary>Maps from <see cref="ScheduleRecord.CreatedAt"/>.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Maps from <see cref="ScheduleRecord.Version"/> — a caller needing to pause/resume/delete passes this back unchanged.</summary>
    public required int Version { get; init; }

    /// <summary>Projects a <see cref="ScheduleRecord"/>, omitting its <see cref="ScheduleRecord.Envelope"/>.</summary>
    public static ScheduleSummary FromRecord(ScheduleRecord record) => new()
    {
        ScheduleId = record.ScheduleId,
        Kind = record.Kind,
        TargetId = record.TargetId,
        CronExpression = record.CronExpression,
        TimeZoneId = record.TimeZoneId,
        ActiveHoursStart = record.ActiveHoursStart,
        ActiveHoursEnd = record.ActiveHoursEnd,
        Cooldown = record.Cooldown,
        MissedRunPolicy = record.MissedRunPolicy,
        Enabled = record.Enabled,
        NextFireAt = record.NextFireAt,
        LastFiredAt = record.LastFiredAt,
        CreatedAt = record.CreatedAt,
        Version = record.Version,
    };
}
