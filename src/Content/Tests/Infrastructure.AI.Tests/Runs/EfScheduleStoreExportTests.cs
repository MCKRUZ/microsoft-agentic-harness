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
/// Personal-data export (#902) for <see cref="EfScheduleStore"/>: every schedule the person created, under every tenant,
/// and nobody else's. The same set erasure removes, so a copy and an erasure never disagree about what the store holds.
/// </summary>
public sealed class EfScheduleStoreExportTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly EfScheduleStore _store;

    public EfScheduleStoreExportTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<ScheduleDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new SqliteVersionInterceptor())
            .Options;

        var factory = new TestDbContextFactory(options);
        _store = new EfScheduleStore(factory, new SchemaInitializer<ScheduleDbContext>(factory));
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task ExportSubjectAsync_TwoOwnersAcrossTenants_ReturnsOnlyTheSubjectsSchedules()
    {
        await _store.CreateAsync(NewRecord("a-acme", " Alice ", "acme"), CancellationToken.None);
        await _store.CreateAsync(NewRecord("a-none", "ALICE", tenantId: null), CancellationToken.None);
        await _store.CreateAsync(NewRecord("b-acme", "bob", "acme"), CancellationToken.None);

        var records = await _store.ExportSubjectAsync(new ExportSubject("  aLiCe ", []), CancellationToken.None);

        records.Select(r => r.GetProperty("scheduleId").GetString()).Should().BeEquivalentTo(["a-acme", "a-none"]);
        records[0].GetProperty("cronExpression").GetString().Should().Be("*/30 * * * *", "the record, not just its id");
    }

    [Fact]
    public async Task ExportSubjectAsync_OwnerWithNothingStored_ReturnsNothing()
    {
        await _store.CreateAsync(NewRecord("b-acme", "bob", "acme"), CancellationToken.None);

        (await _store.ExportSubjectAsync(new ExportSubject("nobody", []), CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public void ExportStoreName_IsItsErasureName() => _store.ExportStoreName.Should().Be(_store.ErasureStoreName);

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
