using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Planner;
using Domain.AI.Planner;
using Domain.Common;
using Infrastructure.AI.Planner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Planner;

/// <summary>
/// Regression tests for solution-review finding confirmed[31]: the per-plan
/// <see cref="System.Threading.SemaphoreSlim"/> was disposed under a check-remove-dispose
/// (TOCTOU) race. A caller that had obtained the semaphore via <c>GetOrAdd</c> but not yet
/// awaited it could have it disposed out from under them by a releasing caller observing
/// <c>CurrentCount == 1</c>, causing <see cref="System.ObjectDisposedException"/> on
/// <c>WaitAsync</c> or two callers holding distinct locks for the same plan.
/// </summary>
public sealed class PlanExecutorSolutionReviewFixTests
{
    private const int Iterations = 400;

    /// <summary>
    /// Hammers the same plan id with overlapping <see cref="IPlanExecutor.ExecuteAsync(PlanId, CancellationToken)"/>
    /// and <see cref="IPlanExecutor.CancelAsync"/> calls. The rapid acquire/release/dispose cycle on the
    /// shared per-plan lock reproduces the TOCTOU window: under the old pattern a concurrent caller's
    /// <c>WaitAsync</c> runs against a disposed semaphore and throws <see cref="System.ObjectDisposedException"/>,
    /// which escapes as an unhandled exception (not a <see cref="Result"/> failure). The fix keeps the
    /// semaphore alive via reference counting, so every operation must complete without throwing.
    /// </summary>
    [Fact]
    public async Task ExecuteAndCancel_ConcurrentOnSamePlan_NeverThrowsObjectDisposed()
    {
        var planId = PlanId.New();
        var plan = BuildSingleStepPlan(planId);
        var sut = CreateSut(plan);

        var thrown = new List<Exception>();
        var thrownGate = new object();

        var tasks = new List<Task>(Iterations * 2);
        for (var i = 0; i < Iterations; i++)
        {
            tasks.Add(RunGuarded(async () => await sut.ExecuteAsync(planId, CancellationToken.None), thrown, thrownGate));
            tasks.Add(RunGuarded(() => sut.CancelAsync(planId, CancellationToken.None), thrown, thrownGate));
        }

        await Task.WhenAll(tasks);

        Assert.True(
            thrown.Count == 0,
            $"Expected no exceptions from concurrent Execute/Cancel, but {thrown.Count} were thrown. " +
            $"First: {thrown.FirstOrDefault()}");
    }

    private static async Task RunGuarded(Func<Task> action, List<Exception> sink, object gate)
    {
        await Task.Yield();
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            lock (gate) { sink.Add(ex); }
        }
    }

    private static PlanExecutor CreateSut(PlanGraph plan)
    {
        var validator = new Mock<IPlanValidator>();
        validator.Setup(v => v.ValidateAsync(It.IsAny<PlanGraph>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanValidationResult>.Success(new PlanValidationResult { IsValid = true }));

        var stateStore = new Mock<IPlanStateStore>();
        stateStore.Setup(s => s.LoadPlanAsync(plan.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanGraph?>.Success(plan));
        stateStore.Setup(s => s.ResumeAsync(It.IsAny<PlanId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyDictionary<PlanStepId, StepExecutionState>>.Success(
                new Dictionary<PlanStepId, StepExecutionState>()));
        stateStore.Setup(s => s.UpdateStepStateAsync(It.IsAny<StepExecutionState>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        stateStore.Setup(s => s.LoadStepStatesAsync(It.IsAny<PlanId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyDictionary<PlanStepId, StepExecutionState>>.Success(
                BuildStepStates(plan)));
        stateStore.Setup(s => s.CheckpointAsync(
                It.IsAny<PlanId>(), It.IsAny<IReadOnlyList<StepExecutionState>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var notifier = new Mock<IPlanProgressNotifier>();
        notifier.Setup(n => n.NotifyPlanStartedAsync(It.IsAny<PlanId>(), It.IsAny<string>(), It.IsAny<PlanGraph>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        notifier.Setup(n => n.NotifyStepStartedAsync(It.IsAny<PlanId>(), It.IsAny<PlanStepId>(), It.IsAny<string>(), It.IsAny<StepType>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        notifier.Setup(n => n.NotifyStepCompletedAsync(It.IsAny<PlanId>(), It.IsAny<PlanStepId>(), It.IsAny<StepExecutionStatus>(), It.IsAny<TimeSpan>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        notifier.Setup(n => n.NotifyStateUpdateAsync(It.IsAny<PlanId>(), It.IsAny<PlanStepId>(), It.IsAny<StepExecutionStatus>(), It.IsAny<StepExecutionStatus>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        notifier.Setup(n => n.NotifyPlanCompletedAsync(It.IsAny<PlanId>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        notifier.Setup(n => n.NotifyPlanFailedAsync(It.IsAny<PlanId>(), It.IsAny<PlanStepId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var escalation = new Mock<IEscalationService>();

        var services = new ServiceCollection();
        var executorMock = new Mock<IPlanStepExecutor>();
        executorMock.Setup(e => e.ExecuteAsync(It.IsAny<PlanStep>(), It.IsAny<IReadOnlyDictionary<PlanStepId, string>>(), It.IsAny<CancellationToken>()))
            .Returns<PlanStep, IReadOnlyDictionary<PlanStepId, string>, CancellationToken>(async (step, _, ct) =>
            {
                await Task.Yield();
                return new StepExecutionResult
                {
                    Status = StepExecutionStatus.Completed,
                    Output = $"output-{step.Name}",
                    Duration = TimeSpan.FromMilliseconds(1)
                };
            });
        services.AddKeyedSingleton<IPlanStepExecutor>(StepType.LlmCall, executorMock.Object);
        var provider = services.BuildServiceProvider();

        return new PlanExecutor(
            validator.Object,
            stateStore.Object,
            notifier.Object,
            escalation.Object,
            provider,
            NullLogger<PlanExecutor>.Instance);
    }

    private static IReadOnlyDictionary<PlanStepId, StepExecutionState> BuildStepStates(PlanGraph plan)
        => plan.Steps.ToDictionary(
            s => s.Id,
            s => new StepExecutionState { StepId = s.Id, Status = StepExecutionStatus.Pending });

    private static PlanGraph BuildSingleStepPlan(PlanId planId)
    {
        var step = new PlanStep
        {
            Id = new PlanStepId(Guid.NewGuid()),
            Name = "step-0",
            Type = StepType.LlmCall,
            Configuration = new LlmCallConfig { SystemPrompt = "test", ModelDeploymentKey = "gpt-4" },
            RetryPolicy = new RetryPolicy { MaxRetries = 0 }
        };

        return new PlanGraph
        {
            Id = planId,
            Name = "single-step-plan",
            Steps = [step],
            Edges = [],
            Configuration = new PlanConfiguration { PlanTimeout = TimeSpan.FromSeconds(10) }
        };
    }
}
