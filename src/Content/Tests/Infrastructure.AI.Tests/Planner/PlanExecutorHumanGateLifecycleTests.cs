using System.Text.Json;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Planner;
using Domain.AI.Escalation;
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
/// Human-gate lifecycle tests exercising the <see cref="PlanExecutor"/> against the real
/// <see cref="EfCorePlanStateStore"/> backed by in-memory SQLite. These pin the fix for the
/// dead-end <c>Blocked</c> bug: a step that reaches <c>Blocked</c> awaiting a human escalation must
/// (a) persist its escalation identifier so it survives a resume, and (b) become runnable again
/// once the escalation resolves — completing on approval, failing on rejection. A mocked store
/// would hide the persistence half of the bug, so the production store is used deliberately.
/// </summary>
public sealed class PlanExecutorHumanGateLifecycleTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<PlannerDbContext> _options;
    private readonly FakeTimeProvider _timeProvider;
    private readonly EfCorePlanStateStore _store;
    private readonly Mock<IPlanValidator> _validator = new();
    private readonly Mock<IPlanProgressNotifier> _notifier = new();
    private readonly Mock<IEscalationService> _escalation = new();

    public PlanExecutorHumanGateLifecycleTests()
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

        // Default: no escalation has resolved -> a blocked step stays blocked.
        _escalation.Setup(e => e.GetOutcomeAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((EscalationOutcome?)null);
    }

    public void Dispose() => _connection.Dispose();

    // (a) A step that reaches Blocked must persist its escalation id in the step's stored output,
    // otherwise a resume cannot correlate the parked step back to its escalation. Before the fix the
    // Blocked transition dropped the output entirely.
    [Fact]
    public async Task Execute_HumanGateBlocks_PersistsEscalationIdInStepOutput()
    {
        var escalationId = Guid.NewGuid();
        var (plan, gateId, _) = GatedPlan();
        var executor = BuildExecutor(BlockingGate(escalationId), CompletingWork(new List<PlanStepId>()));
        await _store.SavePlanAsync(plan, CancellationToken.None);

        var result = await executor.ExecuteAsync(plan.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(StepExecutionStatus.Blocked, result.Value!.FinalStatus);

        var states = await _store.LoadStepStatesAsync(plan.Id, CancellationToken.None);
        Assert.True(states.IsSuccess);
        var gateState = states.Value![gateId];
        Assert.Equal(StepExecutionStatus.Blocked, gateState.Status);
        Assert.NotNull(gateState.Output);
        Assert.Contains(escalationId.ToString(), gateState.Output);
    }

    // (b) Once the escalation is approved, re-executing the plan must complete the blocked gate and
    // run its downstream so the plan finishes. Before the fix nothing ever left Blocked, so the
    // downstream step never ran and the plan could never complete.
    [Fact]
    public async Task Execute_ResumeAfterEscalationApproved_RunsBlockedStepDownstreamAndCompletes()
    {
        var escalationId = Guid.NewGuid();
        var (plan, gateId, workId) = GatedPlan();
        var workInvocations = new List<PlanStepId>();
        var executor = BuildExecutor(BlockingGate(escalationId), CompletingWork(workInvocations));
        await _store.SavePlanAsync(plan, CancellationToken.None);

        var first = await executor.ExecuteAsync(plan.Id, CancellationToken.None);
        Assert.Equal(StepExecutionStatus.Blocked, first.Value!.FinalStatus);
        Assert.Empty(workInvocations);

        // The human approves the specific escalation the gate raised.
        _escalation.Setup(e => e.GetOutcomeAsync(escalationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Outcome(escalationId, approved: true));

        var second = await executor.ExecuteAsync(plan.Id, CancellationToken.None);

        Assert.True(second.IsSuccess);
        Assert.Equal(StepExecutionStatus.Completed, second.Value!.FinalStatus);
        // The downstream step must actually run now that the gate has cleared.
        Assert.Equal(new[] { workId }, workInvocations);

        var states = await _store.LoadStepStatesAsync(plan.Id, CancellationToken.None);
        Assert.Equal(StepExecutionStatus.Completed, states.Value![gateId].Status);
        Assert.Equal(StepExecutionStatus.Completed, states.Value![workId].Status);
    }

    // (c) A rejected escalation must fail the blocked step and route its downstream through failure
    // recovery. The plan must NOT falsely report Completed.
    [Fact]
    public async Task Execute_ResumeAfterEscalationRejected_FailsBlockedStepAndPlanDoesNotComplete()
    {
        var escalationId = Guid.NewGuid();
        var (plan, gateId, workId) = GatedPlan();
        var workInvocations = new List<PlanStepId>();
        var executor = BuildExecutor(BlockingGate(escalationId), CompletingWork(workInvocations));
        await _store.SavePlanAsync(plan, CancellationToken.None);

        var first = await executor.ExecuteAsync(plan.Id, CancellationToken.None);
        Assert.Equal(StepExecutionStatus.Blocked, first.Value!.FinalStatus);

        _escalation.Setup(e => e.GetOutcomeAsync(escalationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Outcome(escalationId, approved: false));

        var second = await executor.ExecuteAsync(plan.Id, CancellationToken.None);

        Assert.True(second.IsSuccess);
        Assert.NotEqual(StepExecutionStatus.Completed, second.Value!.FinalStatus);
        Assert.Equal(StepExecutionStatus.Failed, second.Value.FinalStatus);
        // The downstream step must never run behind a rejected gate.
        Assert.Empty(workInvocations);

        var states = await _store.LoadStepStatesAsync(plan.Id, CancellationToken.None);
        Assert.Equal(StepExecutionStatus.Failed, states.Value![gateId].Status);
        Assert.Equal(StepExecutionStatus.Skipped, states.Value![workId].Status);
    }

    // (Escalate producer, approved) A step whose REAL work fails and whose Escalate retry policy
    // blocks it must, on approval, RE-RUN its real work — not be faked Completed. Downstream must
    // receive the genuine re-run output, never the escalation-reference JSON.
    [Fact]
    public async Task Execute_EscalateOnFailureApproved_RerunsRealWorkAndDownstreamGetsRealOutput()
    {
        var escalationId = Guid.NewGuid();
        _escalation.Setup(e => e.QueueEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(escalationId);

        var (plan, stepAId, stepBId) = EscalatePlan();
        var stepACalls = 0;
        var downstreamUpstreamOutputs = new List<string>();
        var executor = BuildLlmExecutor((step, upstream, _) =>
        {
            if (step.Id == stepAId)
            {
                stepACalls++;
                return Task.FromResult(stepACalls == 1
                    ? new StepExecutionResult { Status = StepExecutionStatus.Failed, ErrorMessage = "boom", Duration = TimeSpan.FromMilliseconds(1) }
                    : new StepExecutionResult { Status = StepExecutionStatus.Completed, Output = "real-A-output", Duration = TimeSpan.FromMilliseconds(1) });
            }
            downstreamUpstreamOutputs.AddRange(upstream.Values);
            return Task.FromResult(new StepExecutionResult { Status = StepExecutionStatus.Completed, Output = "B", Duration = TimeSpan.FromMilliseconds(1) });
        });
        await _store.SavePlanAsync(plan, CancellationToken.None);

        var first = await executor.ExecuteAsync(plan.Id, CancellationToken.None);
        Assert.Equal(StepExecutionStatus.Blocked, first.Value!.FinalStatus);
        Assert.Equal(1, stepACalls);
        Assert.Empty(downstreamUpstreamOutputs);

        // The escalate-on-failure block must persist the escalation-ref just like a human gate.
        var blocked = await _store.LoadStepStatesAsync(plan.Id, CancellationToken.None);
        Assert.Equal(StepExecutionStatus.Blocked, blocked.Value![stepAId].Status);
        Assert.Contains(escalationId.ToString(), blocked.Value![stepAId].Output);

        _escalation.Setup(e => e.GetOutcomeAsync(escalationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Outcome(escalationId, approved: true));

        var second = await executor.ExecuteAsync(plan.Id, CancellationToken.None);

        Assert.True(second.IsSuccess);
        Assert.Equal(StepExecutionStatus.Completed, second.Value!.FinalStatus);
        // Real work re-ran (2 invocations), and downstream saw the genuine output, not the ref JSON.
        Assert.Equal(2, stepACalls);
        Assert.Equal(new[] { "real-A-output" }, downstreamUpstreamOutputs);
        Assert.DoesNotContain(escalationId.ToString(), string.Concat(downstreamUpstreamOutputs));

        var final = await _store.LoadStepStatesAsync(plan.Id, CancellationToken.None);
        Assert.Equal(StepExecutionStatus.Completed, final.Value![stepAId].Status);
        Assert.Equal("real-A-output", final.Value![stepAId].Output);
        Assert.Equal(StepExecutionStatus.Completed, final.Value![stepBId].Status);
    }

    // (Escalate producer, rejected) A rejected escalation is terminal: the step fails and its
    // downstream is skipped. It must NOT re-run the work and must NOT re-escalate (which the step's
    // own Escalate policy would otherwise do, looping forever).
    [Fact]
    public async Task Execute_EscalateOnFailureRejected_FailsStepAndSkipsDownstream()
    {
        var escalationId = Guid.NewGuid();
        _escalation.Setup(e => e.QueueEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(escalationId);

        var (plan, stepAId, stepBId) = EscalatePlan();
        var stepACalls = 0;
        var executor = BuildLlmExecutor((step, _, _) =>
        {
            if (step.Id == stepAId)
            {
                stepACalls++;
                return Task.FromResult(new StepExecutionResult { Status = StepExecutionStatus.Failed, ErrorMessage = "boom", Duration = TimeSpan.FromMilliseconds(1) });
            }
            return Task.FromResult(new StepExecutionResult { Status = StepExecutionStatus.Completed, Output = "B", Duration = TimeSpan.FromMilliseconds(1) });
        });
        await _store.SavePlanAsync(plan, CancellationToken.None);

        var first = await executor.ExecuteAsync(plan.Id, CancellationToken.None);
        Assert.Equal(StepExecutionStatus.Blocked, first.Value!.FinalStatus);
        Assert.Equal(1, stepACalls);

        _escalation.Setup(e => e.GetOutcomeAsync(escalationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Outcome(escalationId, approved: false));

        var second = await executor.ExecuteAsync(plan.Id, CancellationToken.None);

        Assert.True(second.IsSuccess);
        Assert.Equal(StepExecutionStatus.Failed, second.Value!.FinalStatus);
        // Rejected work is not re-run, and the block is not re-escalated (QueueEscalation stays at 1).
        Assert.Equal(1, stepACalls);
        _escalation.Verify(e => e.QueueEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()), Times.Once);

        var states = await _store.LoadStepStatesAsync(plan.Id, CancellationToken.None);
        Assert.Equal(StepExecutionStatus.Failed, states.Value![stepAId].Status);
        Assert.Equal(StepExecutionStatus.Skipped, states.Value![stepBId].Status);
    }

    // --- Helpers ---

    private PlanExecutor BuildExecutor(
        Func<PlanStep, IReadOnlyDictionary<PlanStepId, string>, CancellationToken, Task<StepExecutionResult>> gateHandler,
        Func<PlanStep, IReadOnlyDictionary<PlanStepId, string>, CancellationToken, Task<StepExecutionResult>> workHandler)
    {
        var gateMock = new Mock<IPlanStepExecutor>();
        gateMock.Setup(e => e.ExecuteAsync(It.IsAny<PlanStep>(), It.IsAny<IReadOnlyDictionary<PlanStepId, string>>(), It.IsAny<CancellationToken>()))
            .Returns<PlanStep, IReadOnlyDictionary<PlanStepId, string>, CancellationToken>(gateHandler);

        var workMock = new Mock<IPlanStepExecutor>();
        workMock.Setup(e => e.ExecuteAsync(It.IsAny<PlanStep>(), It.IsAny<IReadOnlyDictionary<PlanStepId, string>>(), It.IsAny<CancellationToken>()))
            .Returns<PlanStep, IReadOnlyDictionary<PlanStepId, string>, CancellationToken>(workHandler);

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IPlanStepExecutor>(StepType.HumanGate, gateMock.Object);
        services.AddKeyedSingleton<IPlanStepExecutor>(StepType.LlmCall, workMock.Object);
        var provider = services.BuildServiceProvider();

        return new PlanExecutor(
            _validator.Object,
            _store,
            _notifier.Object,
            _escalation.Object,
            provider,
            NullLogger<PlanExecutor>.Instance);
    }

    private PlanExecutor BuildLlmExecutor(
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
        BlockingGate(Guid escalationId) => (_, _, _) => Task.FromResult(new StepExecutionResult
        {
            Status = StepExecutionStatus.Blocked,
            Output = JsonSerializer.Serialize(new { escalationId }),
            Duration = TimeSpan.FromMilliseconds(1)
        });

    private static Func<PlanStep, IReadOnlyDictionary<PlanStepId, string>, CancellationToken, Task<StepExecutionResult>>
        CompletingWork(List<PlanStepId> invocations) => (step, _, _) =>
        {
            invocations.Add(step.Id);
            return Task.FromResult(new StepExecutionResult
            {
                Status = StepExecutionStatus.Completed,
                Output = $"output-{step.Name}",
                Duration = TimeSpan.FromMilliseconds(1)
            });
        };

    private static EscalationOutcome Outcome(Guid escalationId, bool approved) => new()
    {
        EscalationId = escalationId,
        IsApproved = approved,
        Decisions = [],
        ResolutionType = approved ? EscalationResolutionType.Approved : EscalationResolutionType.Denied,
        ResolvedAt = new DateTimeOffset(2026, 5, 15, 12, 5, 0, TimeSpan.Zero)
    };

    private static (PlanGraph Plan, PlanStepId GateId, PlanStepId WorkId) GatedPlan()
    {
        var gate = new PlanStep
        {
            Id = PlanStepId.New(),
            Name = "gate-0",
            Type = StepType.HumanGate,
            Configuration = new HumanGateConfig
            {
                EscalationMessage = "approve me",
                ApprovalStrategy = ApprovalStrategy.AnyOf,
                Approvers = ["supervisor"]
            },
            RetryPolicy = new RetryPolicy { MaxRetries = 0 },
            Timeout = TimeSpan.FromSeconds(30)
        };

        var work = new PlanStep
        {
            Id = PlanStepId.New(),
            Name = "work-1",
            Type = StepType.LlmCall,
            Configuration = new LlmCallConfig { SystemPrompt = "test", ModelDeploymentKey = "gpt-4" },
            RetryPolicy = new RetryPolicy { MaxRetries = 0 },
            Timeout = TimeSpan.FromSeconds(30)
        };

        var plan = new PlanGraph
        {
            Id = PlanId.New(),
            Name = "gated-plan",
            Steps = [gate, work],
            Edges = [new PlanEdge(gate.Id, work.Id, EdgeType.ControlFlow)],
            Configuration = new PlanConfiguration { MaxParallelSteps = 2, PlanTimeout = TimeSpan.FromSeconds(10) }
        };

        return (plan, gate.Id, work.Id);
    }

    private static (PlanGraph Plan, PlanStepId StepAId, PlanStepId StepBId) EscalatePlan()
    {
        var stepA = new PlanStep
        {
            Id = PlanStepId.New(),
            Name = "A",
            Type = StepType.LlmCall,
            Configuration = new LlmCallConfig { SystemPrompt = "test", ModelDeploymentKey = "gpt-4" },
            // Escalate on exhausted retries: a real-work failure escalates and blocks the step.
            RetryPolicy = new RetryPolicy { MaxRetries = 0, OnExhausted = ErrorRecovery.Escalate },
            Timeout = TimeSpan.FromSeconds(30)
        };

        var stepB = new PlanStep
        {
            Id = PlanStepId.New(),
            Name = "B",
            Type = StepType.LlmCall,
            Configuration = new LlmCallConfig { SystemPrompt = "test", ModelDeploymentKey = "gpt-4" },
            RetryPolicy = new RetryPolicy { MaxRetries = 0 },
            Timeout = TimeSpan.FromSeconds(30)
        };

        var plan = new PlanGraph
        {
            Id = PlanId.New(),
            Name = "escalate-plan",
            Steps = [stepA, stepB],
            Edges = [new PlanEdge(stepA.Id, stepB.Id, EdgeType.ControlFlow)],
            Configuration = new PlanConfiguration { MaxParallelSteps = 2, PlanTimeout = TimeSpan.FromSeconds(10) }
        };

        return (plan, stepA.Id, stepB.Id);
    }

    private sealed class TestDbContextFactory(DbContextOptions<PlannerDbContext> options)
        : IDbContextFactory<PlannerDbContext>
    {
        public PlannerDbContext CreateDbContext() => new(options);
    }
}
