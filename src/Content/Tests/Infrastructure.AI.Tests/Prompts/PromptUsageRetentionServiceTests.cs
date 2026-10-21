using Application.AI.Common.Prompts.Models;
using Domain.AI.Prompts;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Prompts.Persistence;
using Infrastructure.AI.Tests.BackgroundServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Prompts;

/// <summary>
/// Prompt-usage retention (#901) on its schedule: rows past the period are deleted on the tick and recent ones kept, and
/// a period under a day deletes nothing.
/// </summary>
public sealed class PromptUsageRetentionServiceTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset Origin = new(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);

    private readonly ObservableFakeClock _clock = new(Origin);
    private readonly AppConfig _config = new();
    private readonly SqliteConnection _keepAlive;
    private readonly EfCorePromptUsageStore _store;

    public PromptUsageRetentionServiceTests()
    {
        var connectionString = $"Data Source=file:promptretention-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(connectionString);
        _keepAlive.Open();
        var factory = new Factory(new DbContextOptionsBuilder<PromptUsageDbContext>().UseSqlite(connectionString).Options);
        _store = new EfCorePromptUsageStore(factory, new SchemaInitializer<PromptUsageDbContext>(factory));
        _config.AI.PromptUsage.Retention.SweepInterval = TimeSpan.FromHours(1);
        _config.AI.PromptUsage.Retention.RetainFor = TimeSpan.FromDays(90);
    }

    /// <inheritdoc />
    public void Dispose() => _keepAlive.Dispose();

    [Fact]
    public async Task RowsPastThePeriod_AreDeletedOnTheTick_AndRecentRowsKept()
    {
        await _store.AppendAsync(Record("old", Origin - TimeSpan.FromDays(100)), CancellationToken.None);
        await _store.AppendAsync(Record("recent", Origin - TimeSpan.FromDays(10)), CancellationToken.None);
        using var service = CreateService();
        await StartAsync(service);

        (await Traces()).Should().BeEquivalentTo(["old", "recent"], "control: nothing is deleted before a tick");
        await AdvanceAndSettle(TimeSpan.FromHours(1));

        (await Traces()).Should().BeEquivalentTo(["recent"]);
    }

    [Fact]
    public async Task APeriodUnderADay_DeletesNothing()
    {
        // A reload is not re-validated, so a mistyped period must be refused by the sweep itself.
        _config.AI.PromptUsage.Retention.RetainFor = TimeSpan.FromHours(23);
        await _store.AppendAsync(Record("old", Origin - TimeSpan.FromDays(100)), CancellationToken.None);
        using var service = CreateService();
        await StartAsync(service);

        await AdvanceAndSettle(TimeSpan.FromHours(1));

        (await Traces()).Should().BeEquivalentTo(["old"]);
    }

    private PromptUsageRetentionService CreateService()
    {
        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(() => _config);
        return new PromptUsageRetentionService(_store, monitor.Object, _clock, NullLogger<PromptUsageRetentionService>.Instance);
    }

    private async Task<List<string>> Traces()
    {
        var all = await _store.QueryByPromptNameAsync("p", CancellationToken.None);
        return all.Select(r => r.TraceId!).ToList();
    }

    private async Task StartAsync(PromptUsageRetentionService service)
    {
        var armed = _clock.NextTimer;
        await service.StartAsync(CancellationToken.None);
        await armed.WaitAsync(Patience);
    }

    private async Task AdvanceAndSettle(TimeSpan by)
    {
        var reArmed = _clock.NextTimer;
        _clock.Advance(by);
        await reArmed.WaitAsync(Patience);
    }

    private static PromptUsageRecord Record(string traceId, DateTimeOffset at) => new()
    {
        Descriptor = new PromptDescriptor { Name = "p", Version = new PromptVersion(1, 0), ContentHash = "deadbeef", Body = "body" },
        CaseId = "case",
        MetricKey = "m",
        TraceId = traceId,
        SpanId = "span",
        RecordedAtUtc = at,
    };

    private sealed class Factory(DbContextOptions<PromptUsageDbContext> options) : IDbContextFactory<PromptUsageDbContext>
    {
        public PromptUsageDbContext CreateDbContext() => new(options);
    }
}
