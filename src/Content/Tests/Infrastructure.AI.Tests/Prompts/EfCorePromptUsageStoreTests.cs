using Application.AI.Common.Prompts.Models;
using Domain.AI.Prompts;
using FluentAssertions;
using Infrastructure.AI.Prompts.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Infrastructure.AI.Tests.Prompts;

/// <summary>
/// EF Core tests for the durable prompt-usage store. Uses a shared-cache in-memory
/// SQLite database per test (held open via a long-lived connection) so the schema
/// survives between DbContext instances created by the factory.
/// </summary>
public sealed class EfCorePromptUsageStoreTests : IDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly IDbContextFactory<PromptUsageDbContext> _factory;
    private readonly EfCorePromptUsageStore _sut;

    public EfCorePromptUsageStoreTests()
    {
        var connectionString = $"Data Source=file:promptusage-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(connectionString);
        _keepAlive.Open();

        var options = new DbContextOptionsBuilder<PromptUsageDbContext>()
            .UseSqlite(connectionString)
            .Options;

        _factory = new TestDbContextFactory(options);
        using (var ctx = _factory.CreateDbContext())
        {
            ctx.Database.EnsureCreated();
        }

        _sut = new EfCorePromptUsageStore(_factory);
    }

    public void Dispose()
    {
        _keepAlive.Dispose();
    }

    private static PromptUsageRecord Record(
        string name = "p",
        string? traceId = "trace-1",
        string? caseId = "case-1",
        string? metricKey = "m",
        DateTimeOffset? at = null)
        => new()
        {
            Descriptor = new PromptDescriptor
            {
                Name = name,
                Version = new PromptVersion(1, 0),
                ContentHash = "deadbeef",
                Body = "body",
            },
            CaseId = caseId,
            MetricKey = metricKey,
            TraceId = traceId,
            SpanId = "span-1",
            RecordedAtUtc = at ?? DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task AppendAsync_then_QueryByTraceId_roundtrips_the_record()
    {
        await _sut.AppendAsync(Record(), CancellationToken.None);

        var results = await _sut.QueryByTraceIdAsync("trace-1", CancellationToken.None);

        results.Should().HaveCount(1);
        var actual = results[0];
        actual.Descriptor.Name.Should().Be("p");
        actual.Descriptor.Version.Should().Be(new PromptVersion(1, 0));
        actual.Descriptor.ContentHash.Should().Be("deadbeef");
        actual.CaseId.Should().Be("case-1");
        actual.MetricKey.Should().Be("m");
        actual.TraceId.Should().Be("trace-1");
        actual.SpanId.Should().Be("span-1");
    }

    [Fact]
    public async Task QueryByTraceId_returns_empty_for_unknown_trace()
    {
        await _sut.AppendAsync(Record(), CancellationToken.None);

        var results = await _sut.QueryByTraceIdAsync("nope", CancellationToken.None);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryByTraceId_returns_records_ordered_by_recorded_at_utc()
    {
        var t0 = new DateTimeOffset(2026, 5, 30, 12, 0, 0, TimeSpan.Zero);
        await _sut.AppendAsync(Record(name: "third", at: t0.AddMinutes(2)), CancellationToken.None);
        await _sut.AppendAsync(Record(name: "first", at: t0), CancellationToken.None);
        await _sut.AppendAsync(Record(name: "second", at: t0.AddMinutes(1)), CancellationToken.None);

        var results = await _sut.QueryByTraceIdAsync("trace-1", CancellationToken.None);

        results.Select(r => r.Descriptor.Name).Should().Equal("first", "second", "third");
    }

    [Fact]
    public async Task QueryByCaseId_partitions_by_case_id()
    {
        await _sut.AppendAsync(Record(caseId: "case-a"), CancellationToken.None);
        await _sut.AppendAsync(Record(caseId: "case-b"), CancellationToken.None);
        await _sut.AppendAsync(Record(caseId: "case-a"), CancellationToken.None);

        var a = await _sut.QueryByCaseIdAsync("case-a", CancellationToken.None);
        var b = await _sut.QueryByCaseIdAsync("case-b", CancellationToken.None);

        a.Should().HaveCount(2);
        b.Should().HaveCount(1);
    }

    [Fact]
    public async Task AppendAsync_tolerates_concurrent_writes()
    {
        var tasks = Enumerable.Range(0, 32)
            .Select(i => Task.Run(() => _sut.AppendAsync(
                Record(traceId: $"trace-{i}", caseId: $"case-{i}"),
                CancellationToken.None)))
            .ToArray();

        await Task.WhenAll(tasks);

        await using var ctx = await _factory.CreateDbContextAsync();
        var count = await ctx.PromptUsages.CountAsync();
        count.Should().Be(32);
    }

    [Fact]
    public async Task AppendAsync_rejects_null_record()
    {
        Func<Task> act = () => _sut.AppendAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    /// <summary>
    /// Minimal <see cref="IDbContextFactory{TContext}"/> for tests that own their own
    /// <see cref="DbContextOptions{TContext}"/> — avoids depending on EF Core's internal
    /// pooled factory type.
    /// </summary>
    private sealed class TestDbContextFactory : IDbContextFactory<PromptUsageDbContext>
    {
        private readonly DbContextOptions<PromptUsageDbContext> _options;
        public TestDbContextFactory(DbContextOptions<PromptUsageDbContext> options) => _options = options;
        public PromptUsageDbContext CreateDbContext() => new(_options);
    }
}
