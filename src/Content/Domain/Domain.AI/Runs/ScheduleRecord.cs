using Domain.AI.Bundles;

namespace Domain.AI.Runs;

/// <summary>
/// A recurring definition that enqueues a <see cref="RunRecord"/> of a given <see cref="RunKind"/> on
/// a cron schedule. The schedule survives a host restart; the runs it enqueues do not — each fires
/// independently through the ordinary run substrate once claimed.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Mirrors <see cref="RunRecord"/>'s identity/ownership shape deliberately.</strong> Both
/// <see cref="OwnerId"/>/<see cref="TenantId"/> and <see cref="Envelope"/> are captured at creation,
/// for the same reason: the tick service that claims a due schedule runs on a background thread with
/// no caller attached, so the identity and grant that authorize each enqueued run have to be carried
/// on the schedule itself rather than re-resolved from an ambient scope that will not exist.
/// </para>
/// <para>
/// <strong><see cref="Version"/> is the durable claim-once primitive.</strong> Unlike the in-memory
/// run substrate's process-local lock, a schedule is claimed via optimistic concurrency against its
/// stored row: whichever process's write wins is the only one that may enqueue that tick's run. See
/// <c>IScheduleStore.TryClaimAsync</c>.
/// </para>
/// </remarks>
public sealed record ScheduleRecord
{
    /// <summary>Server-minted identifier.</summary>
    public required string ScheduleId { get; init; }

    /// <summary>Which kind of work each enqueued run performs.</summary>
    public required RunKind Kind { get; init; }

    /// <summary>Identifier of the thing each enqueued run targets, interpreted the same way as <see cref="RunRecord.TargetId"/>.</summary>
    public required string TargetId { get; init; }

    /// <summary>Stable identity of the caller that created the schedule.</summary>
    public required string OwnerId { get; init; }

    /// <summary>Tenant of the caller that created the schedule, when the host resolves one.</summary>
    public string? TenantId { get; init; }

    /// <summary>The grant each enqueued run executes under — captured at creation, never re-resolved on a tick.</summary>
    public required CapabilityEnvelope Envelope { get; init; }

    /// <summary>Standard five-field cron expression (minute hour day-of-month month day-of-week).</summary>
    public required string CronExpression { get; init; }

    /// <summary>IANA time zone the cron expression's fields are evaluated in.</summary>
    public required string TimeZoneId { get; init; }

    /// <summary>
    /// The earliest time of day, in <see cref="TimeZoneId"/>, at which a tick may fire. <see langword="null"/>
    /// means no lower bound.
    /// </summary>
    public TimeSpan? ActiveHoursStart { get; init; }

    /// <summary>
    /// The latest time of day, in <see cref="TimeZoneId"/>, at which a tick may fire. <see langword="null"/>
    /// means no upper bound. A tick computed outside <see cref="ActiveHoursStart"/>/<see cref="ActiveHoursEnd"/>
    /// is skipped forward to the next in-window occurrence rather than fired late.
    /// </summary>
    public TimeSpan? ActiveHoursEnd { get; init; }

    /// <summary>Minimum gap enforced between two fires of this schedule, regardless of what the cron expression alone would produce.</summary>
    public TimeSpan Cooldown { get; init; } = TimeSpan.Zero;

    /// <summary>What happens when the host was not running at one or more scheduled ticks.</summary>
    public MissedRunPolicy MissedRunPolicy { get; init; } = MissedRunPolicy.Skip;

    /// <summary>Whether this schedule is currently live. A paused schedule is never claimed by the tick service.</summary>
    public required bool Enabled { get; init; }

    /// <summary>The next time this schedule is due to fire.</summary>
    public required DateTimeOffset NextFireAt { get; init; }

    /// <summary>When this schedule last fired, if it ever has.</summary>
    public DateTimeOffset? LastFiredAt { get; init; }

    /// <summary>When this schedule was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Optimistic-concurrency version. See the type remarks.</summary>
    public required int Version { get; init; }
}
