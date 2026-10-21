using Domain.AI.Bundles;
using Domain.AI.Runs;
using Domain.Common.Compliance;
using FluentAssertions;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Runs;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Infrastructure.AI.Tests.Runs;

/// <summary>
/// Right-to-erasure (#900) for <see cref="EfScheduleStore"/>: every schedule the person created is removed, under every
/// tenant, and no other owner's schedule is touched.
/// </summary>
/// <remarks>
/// Over-deletion destroys another person's data; under-deletion leaves the person's data behind while the erasure
/// receipt claims success. Schedules store the owner canonicalized, so the subject is built from a differently-cased,
/// space-padded form of the id. Rows are counted in the table directly: the store's own reads filter by tenant and would
/// hide a schedule that survived under another one.
/// </remarks>
public sealed class EfScheduleStoreErasureTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ScheduleDbContext> _options;
    private readonly EfScheduleStore _store;

    public EfScheduleStoreErasureTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ScheduleDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new SqliteVersionInterceptor())
            .Options;

        var factory = new TestDbContextFactory(_options);
        _store = new EfScheduleStore(factory, new SchemaInitializer<ScheduleDbContext>(factory));
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task EraseSubjectAsync_TwoOwnersAcrossTenants_RemovesOnlyTheSubjectsSchedules()
    {
        await _store.CreateAsync(NewRecord("a-acme", " Alice ", "acme"), CancellationToken.None);
        await _store.CreateAsync(NewRecord("a-globex", "ALICE", "globex"), CancellationToken.None);
        await _store.CreateAsync(NewRecord("a-none", "alice", tenantId: null), CancellationToken.None);
        await _store.CreateAsync(NewRecord("b-acme", "bob", "acme"), CancellationToken.None);
        (await ScheduleIdsAsync("alice")).Should().HaveCount(3, "the seed must be stored canonicalized");

        var removed = await _store.EraseSubjectAsync(new ErasureSubject("  aLiCe ", []), CancellationToken.None);

        removed.Should().Be(3, "erasure is about the person, every tenant included");
        (await ScheduleIdsAsync("alice")).Should().BeEmpty();
        (await ScheduleIdsAsync("bob")).Should().Equal(["b-acme"], "another owner's schedule must survive");
    }

    [Fact]
    public async Task EraseSubjectAsync_OwnerWithNothingStored_ReturnsZeroAndChangesNothing()
    {
        await _store.CreateAsync(NewRecord("b-acme", "bob", "acme"), CancellationToken.None);

        var removed = await _store.EraseSubjectAsync(new ErasureSubject("nobody", []), CancellationToken.None);

        removed.Should().Be(0);
        (await ScheduleIdsAsync("bob")).Should().Equal(["b-acme"]);
    }

    private async Task<List<string>> ScheduleIdsAsync(string canonicalOwner)
    {
        await using var ctx = new ScheduleDbContext(_options);
        return await ctx.Schedules
            .Where(e => e.OwnerId == canonicalOwner)
            .Select(e => e.ScheduleId)
            .OrderBy(id => id)
            .ToListAsync();
    }

    private static ScheduleRecord NewRecord(string scheduleId, string ownerId, string? tenantId) => new()
    {
        ScheduleId = scheduleId,
        Kind = RunKind.Workflow,
        TargetId = Guid.NewGuid().ToString(),
        OwnerId = ownerId,
        TenantId = tenantId,
        Envelope = new CapabilityEnvelope(),
        CronExpression = "*/30 * * * *",
        TimeZoneId = "UTC",
        Enabled = true,
        NextFireAt = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero),
        CreatedAt = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero),
        Version = 0,
    };

    private sealed class TestDbContextFactory(DbContextOptions<ScheduleDbContext> options)
        : IDbContextFactory<ScheduleDbContext>
    {
        public ScheduleDbContext CreateDbContext() => new(options);
    }
}
