using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Planner;
using Domain.AI.Planner;
using Domain.Common;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Planner;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Planner;

/// <summary>
/// Crash-resume and manual-retry recovery tests that exercise the <see cref="PlanExecutor"/>
/// against the real <see cref="EfCorePlanStateStore"/> backed by in-memory SQLite. These use the
/// production state store — not a mock — because the resume bug lived in the interaction between
/// the store's persisted states and the executor's scheduling, which a mocked store hides.
/// </summary>
public sealed class PlanExecutorResumeRecoveryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<PlannerDbContext> _options;
    private readonly FakeTimeProvider _timeProvider;
    private readonly EfCorePlanStateStore _store;
    private readonly Mock<IPlanValidator> _validator = new();
    private readonly Mock<IPlanProgressNotifier> _notifier = new();
    private readonly Mock<IEscalationService> _escalation = new();

    public PlanExecutorResumeRecoveryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<PlannerDbContext>()
            .UseSqlite(_connection)
            .Options;

        using (var ctx = new PlannerDbContext(_options))
        {
            ctx.Database.EnsureCreated();
        }

        _timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 5, 15, 12, 0, 0, TimeSpan.Zero));
        _store = new EfCorePlanStateStore(
            new TestDbContextFactory(_options), NullLogger<EfCorePlanStateStore>.Instance, _timeProvider);

        _validator.Setup(v => v.ValidateAsync(It.IsAny<PlanGraph>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanValidationResult>.Success(new PlanValidationResult { IsValid = true }));

        _notifier.Setup(n => n.NotifyPlanStartedAsync(It.IsAny<PlanId>(), It.IsAny<string>(), It.IsAny<PlanGraph>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _notifier.Setup(n => n.NotifyStepStartedAsync(It.IsAny<PlanId>(), It.IsAny<PlanStepId>(), It.IsAny<string>(), It.IsAny<StepType>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _notifier.Setup(n => n.NotifyStepCompletedAsync(It.IsAny<PlanId>(), It.IsAny<PlanStepId>(), It.IsAny<StepExecutionStatus>(), It.IsAny<TimeSpan>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _notifier.Setup(n => n.NotifyStateUpdateAsync(It.IsAny<PlanId>(), It.IsAny<PlanStepId>(), It.IsAny<StepExecutionStatus>(), It.IsAny<StepExecutionStatus>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _notifier.Setup(n => n.NotifyPlanCompletedAsync(It.IsAny<PlanId>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _notifier.Setup(n => n.NotifyPlanFailedAsync(It.IsAny<PlanId>(), It.IsAny<PlanStepId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    public void Dispose() => _connection.Dispose();

    // === Crash-resume: interrupted (Running) step must re-execute ===

    [Fact]
    public async Task Execute_ResumeAfterCrashMidStep_ReExecutesInterruptedStepAndCompletes()
    {
        var invocations = new List<PlanStepId>();
        var executor = BuildExecutor(RecordingCompletingExecutor(invocations));
        var plan = SingleStepPlan();
        await _store.SavePlanAsync(plan, CancellationToken.None);
        await CrashStepAsRunning(plan.Steps[0].Id);

        var result = await executor.ExecuteAsync(plan.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        // The step that was interrupted mid-flight must actually re-run — this is the core resume fix.
        Assert.Single(invocations);
        Assert.Equal(plan.Steps[0].Id, invocations[0]);
        // And because it genuinely re-ran to success, Completed is now honest.
        Assert.Equal(StepExecutionStatus.Completed, result.Value!.FinalStatus);
    }

    [Fact]
    public async Task Execute_ResumeAfterCrash_WhenReRunFails_DoesNotReportCompleted()
    {
        // Guards the false-completed bug: a plan whose interrupted step re-runs and fails
        // must NOT be summarised as Completed. Before the fix the step never re-ran yet the
        // summary reported Completed.
        var invocations = new List<PlanStepId>();
        var executor = BuildExecutor(RecordingFailingExecutor(invocations));
        var plan = SingleStepPlan();
        await _store.SavePlanAsync(plan, CancellationToken.None);
        await CrashStepAsRunning(plan.Steps[0].Id);

        var result = await executor.ExecuteAsync(plan.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(invocations);
        Assert.NotEqual(StepExecutionStatus.Completed, result.Value!.FinalStatus);
        Assert.Equal(StepExecutionStatus.Failed, result.Value.FinalStatus);
    }

    // === Summary honesty: a step that never terminalises must not read as Completed ===

    [Fact]
    public async Task Execute_SchedulerExitsWithNonTerminalStep_DoesNotReportCompleted()
    {
        // An executor that returns a status the result handler does not terminalise leaves the
        // step non-terminal (Running) when the scheduling loop drains. BuildSummary must not
        // paper over this by reporting Completed.
        var executor = BuildExecutor((_, _, _) => Task.FromResult(new StepExecutionResult
        {
            Status = StepExecutionStatus.Skipped, // not handled by HandleStepResultAsync -> step stays non-terminal
            Duration = TimeSpan.FromMilliseconds(1)
        }));
        var plan = SingleStepPlan();
        await _store.SavePlanAsync(plan, CancellationToken.None);

        var result = await executor.ExecuteAsync(plan.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotEqual(StepExecutionStatus.Completed, result.Value!.FinalStatus);
    }

    // === Manual retry: a retried failed step must re-execute on next run ===

    [Fact]
    public async Task RetryStep_ThenExecute_ReExecutesFailedStep()
    {
        var invocations = new List<PlanStepId>();
        var fail = true;
        var executor = BuildExecutor((step, _, _) =>
        {
            invocations.Add(step.Id);
            var status = fail ? StepExecutionStatus.Failed : StepExecutionStatus.Completed;
            return Task.FromResult(new StepExecutionResult
            {
                Status = status,
                Output = status == StepExecutionStatus.Completed ? "ok" : null,
                ErrorMessage = status == StepExecutionStatus.Failed ? "boom" : null,
                Duration = TimeSpan.FromMilliseconds(1)
            });
        });
        var plan = SingleStepPlan();
        await _store.SavePlanAsync(plan, CancellationToken.None);

        var first = await executor.ExecuteAsync(plan.Id, CancellationToken.None);
        Assert.Equal(StepExecutionStatus.Failed, first.Value!.FinalStatus);
        Assert.Single(invocations);

        var retry = await executor.RetryStepAsync(plan.Id, plan.Steps[0].Id, CancellationToken.None);
        Assert.True(retry.IsSuccess);

        fail = false;
        var second = await executor.ExecuteAsync(plan.Id, CancellationToken.None);

        Assert.True(second.IsSuccess);
        // The retried step must actually run a second time.
        Assert.Equal(2, invocations.Count);
        Assert.Equal(StepExecutionStatus.Completed, second.Value!.FinalStatus);
    }

    // --- Helpers ---

    private PlanExecutor BuildExecutor(
        Func<PlanStep, IReadOnlyDictionary<PlanStepId, string>, CancellationToken, Task<StepExecutionResult>> handler)
    {
        var mock = new Mock<IPlanStepExecutor>();
        mock.Setup(e => e.ExecuteAsync(It.IsAny<PlanStep>(), It.IsAny<IReadOnlyDictionary<PlanStepId, string>>(), It.IsAny<CancellationToken>()))
            .Returns<PlanStep, IReadOnlyDictionary<PlanStepId, string>, CancellationToken>(handler);

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IPlanStepExecutor>(StepType.LlmCall, mock.Object);
        var provider = services.BuildServiceProvider();

        return new PlanExecutor(
            _validator.Object,
            _store,
            _notifier.Object,
            _escalation.Object,
            provider,
            NullLogger<PlanExecutor>.Instance);
    }

    private static Func<PlanStep, IReadOnlyDictionary<PlanStepId, string>, CancellationToken, Task<StepExecutionResult>>
        RecordingCompletingExecutor(List<PlanStepId> invocations) => (step, _, _) =>
    {
        invocations.Add(step.Id);
        return Task.FromResult(new StepExecutionResult
        {
            Status = StepExecutionStatus.Completed,
            Output = $"output-{step.Name}",
            Duration = TimeSpan.FromMilliseconds(1)
        });
    };

    private static Func<PlanStep, IReadOnlyDictionary<PlanStepId, string>, CancellationToken, Task<StepExecutionResult>>
        RecordingFailingExecutor(List<PlanStepId> invocations) => (step, _, _) =>
    {
        invocations.Add(step.Id);
        return Task.FromResult(new StepExecutionResult
        {
            Status = StepExecutionStatus.Failed,
            ErrorMessage = "boom",
            Duration = TimeSpan.FromMilliseconds(1)
        });
    };

    private async Task CrashStepAsRunning(PlanStepId stepId)
    {
        await using var ctx = new PlannerDbContext(_options);
        var state = await ctx.StepExecutionStates.FirstAsync(s => s.StepId == stepId.Value);
        state.Status = StepExecutionStatus.Running;
        state.AttemptCount = 1;
        state.StartedAt = _timeProvider.GetUtcNow();
        await ctx.SaveChangesAsync();
    }

    private static PlanGraph SingleStepPlan()
    {
        var step = new PlanStep
        {
            Id = PlanStepId.New(),
            Name = "step-0",
            Type = StepType.LlmCall,
            Configuration = new LlmCallConfig { SystemPrompt = "test", ModelDeploymentKey = "gpt-4" },
            RetryPolicy = new RetryPolicy { MaxRetries = 0 },
            Timeout = TimeSpan.FromSeconds(30)
        };

        return new PlanGraph
        {
            Id = PlanId.New(),
            Name = "resume-plan",
            Steps = [step],
            Edges = [],
            Configuration = new PlanConfiguration { MaxParallelSteps = 2, PlanTimeout = TimeSpan.FromSeconds(10) }
        };
    }

    private sealed class TestDbContextFactory(DbContextOptions<PlannerDbContext> options)
        : IDbContextFactory<PlannerDbContext>
    {
        public PlannerDbContext CreateDbContext() => new(options);
    }
}
