using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.Planner;
using Domain.AI.Orchestration;
using Domain.AI.Planner;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Agents;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Planner;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.BackgroundServices;

/// <summary>
/// The delegation and plan retention sweeps (#901) on their schedule: an expired record goes on the tick, and a period
/// under a day deletes nothing, because a reloaded value is not re-validated.
/// </summary>
public sealed class RetentionSweepServiceTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset Origin = new(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);

    private readonly ObservableFakeClock _clock = new(Origin);
    private readonly AppConfig _config = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"retention-sweeps-{Guid.NewGuid():N}");
    private readonly SqliteConnection _planner = new("DataSource=:memory:");

    public RetentionSweepServiceTests()
    {
        _config.AI.Orchestration.Subagent.DelegationStoragePath = _dir;
        _config.AI.Orchestration.Subagent.DelegationRetention.SweepInterval = TimeSpan.FromHours(1);
        _config.AI.Planner.Retention.SweepInterval = TimeSpan.FromHours(1);
        _planner.Open();
    }

    public void Dispose()
    {
        _planner.Dispose();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private IOptionsMonitor<AppConfig> Monitor()
    {
        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(() => _config);
        return monitor.Object;
    }

    [Theory]
    [InlineData(90, 0)]
    [InlineData(0.5, 1)]
    public async Task Delegation_AnIdleSessionFileGoesOnTheTick_UnlessThePeriodIsUnderADay(double days, int filesLeft)
    {
        _config.AI.Orchestration.Subagent.DelegationRetention.RetainFor = TimeSpan.FromDays(days);
        using var store = new JsonlDelegationStore(Monitor(), NullLogger<JsonlDelegationStore>.Instance);
        await store.AppendAsync(Record());
        var file = Directory.GetFiles(_dir, "*.jsonl", SearchOption.AllDirectories).Single();
        File.SetLastWriteTimeUtc(file, Origin.UtcDateTime.AddDays(-100));
        using var service = new DelegationRetentionService(store, Monitor(), _clock, NullLogger<DelegationRetentionService>.Instance);

        await StartAndTickAsync(service);

        Directory.GetFiles(_dir, "*.jsonl", SearchOption.AllDirectories).Should().HaveCount(filesLeft);
    }

    [Theory]
    [InlineData(90, 0)]
    [InlineData(0.5, 1)]
    public async Task Plans_AnIdlePlanGoesOnTheTick_UnlessThePeriodIsUnderADay(double days, int plansLeft)
    {
        _config.AI.Planner.Retention.RetainFor = TimeSpan.FromDays(days);
        var factory = new PlannerFactory(new DbContextOptionsBuilder<PlannerDbContext>().UseSqlite(_planner).Options);
        var savedAt = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Origin.AddDays(-100));
        var store = new EfCorePlanStateStore(
            factory, NullLogger<EfCorePlanStateStore>.Instance, savedAt, new Scope(), new SchemaInitializer<PlannerDbContext>(factory));
        (await store.SavePlanAsync(Plan(), CancellationToken.None)).IsSuccess.Should().BeTrue();

        var services = new ServiceCollection().AddScoped<IPlanStateStore>(_ => store).BuildServiceProvider();
        using var service = new PlanRetentionService(
            services.GetRequiredService<IServiceScopeFactory>(), Monitor(), _clock, NullLogger<PlanRetentionService>.Instance);

        await StartAndTickAsync(service);

        await using var ctx = factory.CreateDbContext();
        (await ctx.PlanGraphs.CountAsync()).Should().Be(plansLeft);
    }

    private async Task StartAndTickAsync(Microsoft.Extensions.Hosting.BackgroundService service)
    {
        var armed = _clock.NextTimer;
        await service.StartAsync(CancellationToken.None);
        await armed.WaitAsync(Patience);
        var reArmed = _clock.NextTimer;
        _clock.Advance(TimeSpan.FromHours(1));
        await reArmed.WaitAsync(Patience);
    }

    private static DelegationRecord Record() => new()
    {
        DelegationId = Guid.NewGuid(),
        SupervisorId = "supervisor",
        DelegateAgentId = "agent",
        DelegateAgentType = Domain.AI.Agents.SubagentType.Execute,
        TaskDescription = "task",
        RequiredCapabilities = [],
        AutonomyLevel = Domain.AI.Governance.AutonomyLevel.Supervised,
        State = DelegationState.Pending,
        DelegationDepth = 0,
        StartedAt = DateTimeOffset.UtcNow,
    };

    private static PlanGraph Plan()
    {
        var step = new PlanStep
        {
            Id = PlanStepId.New(),
            Name = "Step",
            Type = StepType.LlmCall,
            Configuration = new LlmCallConfig { SystemPrompt = "Prompt", ModelDeploymentKey = "gpt-4o" },
            RetryPolicy = new RetryPolicy { MaxRetries = 0 },
            Timeout = TimeSpan.FromSeconds(30),
        };
        return new PlanGraph
        {
            Id = PlanId.New(),
            Name = "Plan",
            Steps = [step],
            Edges = [],
            Configuration = new PlanConfiguration { MaxParallelSteps = 1, PlanTimeout = TimeSpan.FromMinutes(10) },
        };
    }

    private sealed class Scope : IKnowledgeScope
    {
        public string? UserId => "alice";
        public string? TenantId => null;
        public string? DatasetId => null;
        public string? DatasetName => null;
        public string? DatasetOwnerId => null;
        public string? AgentId => null;
        public string? ConversationId => null;
    }

    private sealed class PlannerFactory(DbContextOptions<PlannerDbContext> options) : IDbContextFactory<PlannerDbContext>
    {
        public PlannerDbContext CreateDbContext() => new(options);
    }
}
