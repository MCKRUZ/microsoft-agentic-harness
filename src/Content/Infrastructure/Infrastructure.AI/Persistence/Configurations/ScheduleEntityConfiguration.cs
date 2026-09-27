using Infrastructure.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.AI.Persistence.Configurations;

/// <summary>
/// EF Core configuration for <see cref="ScheduleEntity"/>. Defines primary key, concurrency token,
/// and the index the tick service's due-schedule scan relies on.
/// </summary>
public sealed class ScheduleEntityConfiguration : IEntityTypeConfiguration<ScheduleEntity>
{
    public void Configure(EntityTypeBuilder<ScheduleEntity> builder)
    {
        builder.ToTable("Schedules");

        builder.HasKey(e => e.ScheduleId);
        builder.Property(e => e.ScheduleId).ValueGeneratedNever();

        builder.Property(e => e.Kind).IsRequired().HasMaxLength(64);
        builder.Property(e => e.TargetId).IsRequired().HasMaxLength(256);
        builder.Property(e => e.OwnerId).IsRequired().HasMaxLength(PlannerScopeFilter.MaxIdentityLength);
        builder.Property(e => e.TenantId).HasMaxLength(PlannerScopeFilter.MaxIdentityLength);
        builder.Property(e => e.EnvelopeJson).IsRequired();
        builder.Property(e => e.CronExpression).IsRequired().HasMaxLength(128);
        builder.Property(e => e.TimeZoneId).IsRequired().HasMaxLength(64);
        builder.Property(e => e.MissedRunPolicy).IsRequired().HasMaxLength(32);

        builder.Property(e => e.Version).IsConcurrencyToken();

        // SQLite cannot translate a WHERE/ORDER BY predicate over EF's default DateTimeOffset
        // mapping (a text+offset tuple) — GetDueSchedulesAsync's "NextFireAt <= @now" filter and
        // ListForOwnerAsync's "ORDER BY CreatedAt" both need this. See SqliteValueConverters' own
        // remarks.
        builder.Property(e => e.NextFireAt).HasConversion(SqliteValueConverters.DateTimeOffsetAsUtcTicks);
        builder.Property(e => e.LastFiredAt).HasConversion(SqliteValueConverters.DateTimeOffsetAsUtcTicks);
        builder.Property(e => e.CreatedAt).HasConversion(SqliteValueConverters.DateTimeOffsetAsUtcTicks);

        // Serves the tick service's GetDueSchedulesAsync scan (Enabled = 1 AND NextFireAt <= @now)
        // and the ListForOwnerAsync scope filter.
        builder.HasIndex(e => new { e.Enabled, e.NextFireAt });
        builder.HasIndex(e => new { e.TenantId, e.OwnerId });
    }
}
