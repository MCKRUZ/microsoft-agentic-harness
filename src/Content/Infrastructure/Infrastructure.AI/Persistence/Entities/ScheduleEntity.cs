using Domain.AI.Runs;

namespace Infrastructure.AI.Persistence.Entities;

/// <summary>
/// EF Core entity representing a <see cref="ScheduleRecord"/>. Tracks optimistic concurrency via an
/// integer version token, incremented on save by <see cref="SqliteVersionInterceptor"/> — the durable
/// claim-once primitive <c>EfScheduleStore.TryClaimAsync</c> relies on.
/// </summary>
public sealed class ScheduleEntity
{
    /// <summary>Maps from <see cref="ScheduleRecord.ScheduleId"/>.</summary>
    public required string ScheduleId { get; set; }

    /// <summary>Which kind of work each enqueued run performs. Stored as its string name for resilience across enum reordering.</summary>
    public required string Kind { get; set; }

    /// <summary>Maps from <see cref="ScheduleRecord.TargetId"/>.</summary>
    public required string TargetId { get; set; }

    /// <summary>Maps from <see cref="ScheduleRecord.OwnerId"/>. Never null — a schedule always has a creator.</summary>
    public required string OwnerId { get; set; }

    /// <summary>Maps from <see cref="ScheduleRecord.TenantId"/>.</summary>
    public string? TenantId { get; set; }

    /// <summary>Serialized <see cref="Domain.AI.Bundles.CapabilityEnvelope"/>.</summary>
    public required string EnvelopeJson { get; set; }

    /// <summary>Maps from <see cref="ScheduleRecord.CronExpression"/>.</summary>
    public required string CronExpression { get; set; }

    /// <summary>Maps from <see cref="ScheduleRecord.TimeZoneId"/>.</summary>
    public required string TimeZoneId { get; set; }

    /// <summary>Maps from <see cref="ScheduleRecord.ActiveHoursStart"/>.</summary>
    public TimeSpan? ActiveHoursStart { get; set; }

    /// <summary>Maps from <see cref="ScheduleRecord.ActiveHoursEnd"/>.</summary>
    public TimeSpan? ActiveHoursEnd { get; set; }

    /// <summary>Maps from <see cref="ScheduleRecord.Cooldown"/>.</summary>
    public TimeSpan Cooldown { get; set; }

    /// <summary>Maps from <see cref="ScheduleRecord.MissedRunPolicy"/>, stored as its string name.</summary>
    public required string MissedRunPolicy { get; set; }

    /// <summary>Maps from <see cref="ScheduleRecord.Enabled"/>.</summary>
    public bool Enabled { get; set; }

    /// <summary>Maps from <see cref="ScheduleRecord.NextFireAt"/>.</summary>
    public DateTimeOffset NextFireAt { get; set; }

    /// <summary>Maps from <see cref="ScheduleRecord.LastFiredAt"/>.</summary>
    public DateTimeOffset? LastFiredAt { get; set; }

    /// <summary>When this schedule was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Optimistic concurrency token incremented on each save.</summary>
    public int Version { get; set; }
}
