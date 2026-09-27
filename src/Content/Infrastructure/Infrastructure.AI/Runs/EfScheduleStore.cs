using System.Text.Json;
using Application.AI.Common.Interfaces.Runs;
using Domain.AI.Bundles;
using Domain.AI.KnowledgeGraph.Scoping;
using Domain.AI.Runs;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.AI.Runs;

/// <summary>
/// EF Core implementation of <see cref="IScheduleStore"/>, backed by <see cref="ScheduleDbContext"/>.
/// Uses <see cref="IDbContextFactory{TContext}"/> for short-lived contexts, the same lifecycle
/// <c>EfCorePlanStateStore</c> and every other SQLite-backed store here use.
/// </summary>
/// <remarks>
/// The constructor demands <see cref="SchemaInitializer{TContext}"/> so resolving the store forces
/// the SQLite schema into existence before the first operation, visible to <c>ValidateOnBuild</c> —
/// the same pattern <c>EfCorePlanStateStore</c> uses.
/// </remarks>
public sealed class EfScheduleStore : IScheduleStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly IDbContextFactory<ScheduleDbContext> _factory;

    public EfScheduleStore(IDbContextFactory<ScheduleDbContext> factory, SchemaInitializer<ScheduleDbContext> schemaInitializer)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(schemaInitializer);
        _factory = factory;
    }

    /// <inheritdoc />
    public async Task CreateAsync(ScheduleRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using var ctx = await _factory.CreateDbContextAsync(cancellationToken);
        ctx.Schedules.Add(ToEntity(record));
        await ctx.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ScheduleRecord?> GetAsync(
        string scheduleId, string ownerId, string? tenantId, CancellationToken cancellationToken)
    {
        await using var ctx = await _factory.CreateDbContextAsync(cancellationToken);

        var entity = await VisibleTo(ctx, ownerId, tenantId)
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.ScheduleId == scheduleId, cancellationToken);

        return entity is null ? null : ToRecord(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ScheduleRecord>> ListForOwnerAsync(
        string ownerId, string? tenantId, CancellationToken cancellationToken)
    {
        await using var ctx = await _factory.CreateDbContextAsync(cancellationToken);

        var entities = await VisibleTo(ctx, ownerId, tenantId)
            .AsNoTracking()
            .OrderBy(e => e.CreatedAt)
            .ToListAsync(cancellationToken);

        return entities.Select(ToRecord).ToList();
    }

    /// <inheritdoc />
    public async Task<int> CountForOwnerAsync(string ownerId, string? tenantId, CancellationToken cancellationToken)
    {
        await using var ctx = await _factory.CreateDbContextAsync(cancellationToken);

        return await VisibleTo(ctx, ownerId, tenantId).AsNoTracking().CountAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> PauseAsync(
        string scheduleId, string ownerId, string? tenantId, int expectedVersion, CancellationToken cancellationToken) =>
        SetEnabledAsync(scheduleId, ownerId, tenantId, expectedVersion, enabled: false, cancellationToken);

    /// <inheritdoc />
    public Task<bool> ResumeAsync(
        string scheduleId, string ownerId, string? tenantId, int expectedVersion, CancellationToken cancellationToken) =>
        SetEnabledAsync(scheduleId, ownerId, tenantId, expectedVersion, enabled: true, cancellationToken);

    private async Task<bool> SetEnabledAsync(
        string scheduleId, string ownerId, string? tenantId, int expectedVersion, bool enabled, CancellationToken cancellationToken)
    {
        await using var ctx = await _factory.CreateDbContextAsync(cancellationToken);

        // Single round trip: the WHERE clause on Version IS the concurrency check, so a stale caller's
        // update matches zero rows instead of throwing. ExecuteUpdateAsync bypasses change tracking —
        // and therefore SqliteVersionInterceptor — so the version bump is explicit here, replicating
        // exactly what the interceptor does for every tracked-entity save elsewhere in this store.
        var updated = await VisibleTo(ctx, ownerId, tenantId)
            .Where(e => e.ScheduleId == scheduleId && e.Version == expectedVersion)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Enabled, enabled)
                .SetProperty(e => e.Version, e => e.Version + 1),
                cancellationToken);

        return updated > 0;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        string scheduleId, string ownerId, string? tenantId, CancellationToken cancellationToken)
    {
        await using var ctx = await _factory.CreateDbContextAsync(cancellationToken);

        var entity = await VisibleTo(ctx, ownerId, tenantId)
            .FirstOrDefaultAsync(e => e.ScheduleId == scheduleId, cancellationToken);

        if (entity is null)
            return false;

        ctx.Schedules.Remove(entity);
        await ctx.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ScheduleRecord>> GetDueSchedulesAsync(
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var ctx = await _factory.CreateDbContextAsync(cancellationToken);

        var entities = await ctx.Schedules
            .AsNoTracking()
            .Where(e => e.Enabled && e.NextFireAt <= now)
            .ToListAsync(cancellationToken);

        return entities.Select(ToRecord).ToList();
    }

    /// <inheritdoc />
    public async Task<bool> TryClaimAsync(
        string scheduleId, int expectedVersion, DateTimeOffset nextFireAt, DateTimeOffset firedAt,
        CancellationToken cancellationToken)
    {
        await using var ctx = await _factory.CreateDbContextAsync(cancellationToken);

        // Single round trip, no unscoped-by-owner concern here: the WHERE clause on Version is the
        // whole claim — a stale caller's update matches zero rows rather than throwing. Same
        // explicit-version-bump reasoning as SetEnabledAsync above.
        var updated = await ctx.Schedules
            .Where(e => e.ScheduleId == scheduleId && e.Version == expectedVersion)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.NextFireAt, nextFireAt)
                .SetProperty(e => e.LastFiredAt, firedAt)
                .SetProperty(e => e.Version, e => e.Version + 1),
                cancellationToken);

        return updated > 0;
    }

    private static IQueryable<ScheduleEntity> VisibleTo(ScheduleDbContext ctx, string ownerId, string? tenantId)
    {
        var canonicalOwner = ScopeIdentity.Canonicalize(ownerId);
        var canonicalTenant = ScopeIdentity.Canonicalize(tenantId);
        return ctx.Schedules.Where(e => e.OwnerId == canonicalOwner && e.TenantId == canonicalTenant);
    }

    private static ScheduleEntity ToEntity(ScheduleRecord record) => new()
    {
        ScheduleId = record.ScheduleId,
        Kind = record.Kind.ToString(),
        TargetId = record.TargetId,
        // Canonicalized on write, matching PlannerScopeFilter's convention — every identity comparison
        // in this store goes through the same canonicalization on both the write and read sides, so a
        // caller that supplies a differently-cased (but equal) identity on one path can never fail to
        // find what another path stored.
        OwnerId = ScopeIdentity.Canonicalize(record.OwnerId) ?? record.OwnerId,
        TenantId = ScopeIdentity.Canonicalize(record.TenantId),
        EnvelopeJson = JsonSerializer.Serialize(record.Envelope, JsonOptions),
        CronExpression = record.CronExpression,
        TimeZoneId = record.TimeZoneId,
        ActiveHoursStart = record.ActiveHoursStart,
        ActiveHoursEnd = record.ActiveHoursEnd,
        Cooldown = record.Cooldown,
        MissedRunPolicy = record.MissedRunPolicy.ToString(),
        Enabled = record.Enabled,
        NextFireAt = record.NextFireAt,
        LastFiredAt = record.LastFiredAt,
        CreatedAt = record.CreatedAt,
        Version = record.Version,
    };

    private static ScheduleRecord ToRecord(ScheduleEntity entity) => new()
    {
        ScheduleId = entity.ScheduleId,
        Kind = Enum.Parse<RunKind>(entity.Kind),
        TargetId = entity.TargetId,
        OwnerId = entity.OwnerId,
        TenantId = entity.TenantId,
        Envelope = JsonSerializer.Deserialize<CapabilityEnvelope>(entity.EnvelopeJson, JsonOptions)
            ?? new CapabilityEnvelope(),
        CronExpression = entity.CronExpression,
        TimeZoneId = entity.TimeZoneId,
        ActiveHoursStart = entity.ActiveHoursStart,
        ActiveHoursEnd = entity.ActiveHoursEnd,
        Cooldown = entity.Cooldown,
        MissedRunPolicy = Enum.Parse<MissedRunPolicy>(entity.MissedRunPolicy),
        Enabled = entity.Enabled,
        NextFireAt = entity.NextFireAt,
        LastFiredAt = entity.LastFiredAt,
        CreatedAt = entity.CreatedAt,
        Version = entity.Version,
    };
}
