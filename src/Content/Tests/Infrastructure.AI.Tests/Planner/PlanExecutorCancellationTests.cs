using System.Collections.Concurrent;
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
/// Tests for cooperative run cancellation.
/// </summary>
/// <remarks>
/// <para>
/// The defect these cover: <c>CancelAsync</c> used to acquire the same per-plan lock that a running
/// <c>ExecuteAsync</c> holds for the whole run, so cancelling an in-flight plan blocked until that
/// plan had finished on its own and then rewrote state for work that was already over. There was no
/// signalling path at all — a cancel returned success having stopped nothing, which on an operation
/// whose entire purpose is halting expensive work is worse than having no cancel.
/// </para>
/// <para>
/// <see cref="CancelAsync_WhileStepInFlight_SignalsPromptlyWithoutDeadlocking"/> is the reproducer:
/// against the pre-fix executor it does not fail on an assertion, it hangs — the cancel waits on the
/// run and the run waits for a cancel that never arrives.
/// </para>
/// </remarks>
public sealed class PlanExecutorCancellationTests
{
    /// <summary>Bounds the reproducer so a regression fails the run instead of hanging it.</summary>
    private static readonly TimeSpan DeadlockBudget = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task CancelAsync_WhileStepInFlight_SignalsPromptlyWithoutDeadlocking()
    {
        var planId = PlanId.New();
        var plan = BuildPlan(planId, stepCount: 1);

        var stepEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stepObservedCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var harness = new Harness(plan);
        harness.OnStep = async (_, ct) =>
        {
            stepEntered.TrySetResult();
            try
            {
                // Runs until cancelled. Pre-fix nothing ever signals this token, so the plan timeout
                // is the only thing that ends the run — which is the hang being reproduced.
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                stepObservedCancellation.TrySetResult();
                throw;
            }

            return Completed();
        };

        var sut = harness.CreateSut();
        var run = Task.Run(() => sut.ExecuteAsync(planId, CancellationToken.None));

        await stepEntered.Task.WaitAsync(DeadlockBudget);

        var cancel = sut.CancelAsync(planId, CancellationToken.None);

        // The signal must reach the running step without the cancel first waiting on the run.
        await stepObservedCancellation.Task.WaitAsync(DeadlockBudget);

        var cancelResult = await cancel.WaitAsync(DeadlockBudget);
        Assert.True(cancelResult.IsSuccess);

        await run.WaitAsync(DeadlockBudget);
    }

    [Fact]
    public async Task CancelAsync_BetweenCheckpointAndNextStep_KeepsFinishedStepCompletedAndLeavesRestResumable()
    {
        var planId = PlanId.New();
        var plan = BuildPlan(planId, stepCount: 2, chained: true);
        var firstStepId = plan.Steps[0].Id;
        var secondStepId = plan.Steps[1].Id;

        var firstStepDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondStep = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStepStarted = false;

        var harness = new Harness(plan);
        harness.OnStep = async (step, ct) =>
        {
            if (step.Id == firstStepId)
                return Completed("first-output");

            // The window under test: the first step is checkpointed Completed and the second has
            // been dispatched but is held at its threshold. The cancel lands here.
            secondStepStarted = true;
            firstStepDone.TrySetResult();
            await releaseSecondStep.Task.WaitAsync(ct);
            return Completed("second-output");
        };

        var sut = harness.CreateSut();
        var run = Task.Run(() => sut.ExecuteAsync(planId, CancellationToken.None));

        await firstStepDone.Task.WaitAsync(DeadlockBudget);
        var cancelResult = await sut.CancelAsync(planId, CancellationToken.None).WaitAsync(DeadlockBudget);
        await run.WaitAsync(DeadlockBudget);

        Assert.True(cancelResult.IsSuccess);

        // Guards the premise: if the second step never ran, the "unstarted step" assertion below
        // would pass vacuously against a plan that had simply stopped after step one.
        Assert.True(secondStepStarted, "Second step never started — the checkpoint window under test was not reached.");

        var persisted = harness.PersistedStates;
        Assert.Equal(StepExecutionStatus.Completed, persisted[firstStepId].Status);
        Assert.Equal("first-output", persisted[firstStepId].Output);
        Assert.Equal(StepExecutionStatus.Cancelled, persisted[secondStepId].Status);

        // Resumability is the property that matters, so assert it by actually resuming: the
        // completed step must not re-run, and the cancelled step must.
        var executions = new ConcurrentBag<PlanStepId>();
        harness.OnStep = (step, _) =>
        {
            executions.Add(step.Id);
            return Task.FromResult(Completed($"{step.Name}-resumed"));
        };

        var resumed = await harness.CreateSut().ExecuteAsync(planId, CancellationToken.None).WaitAsync(DeadlockBudget);

        Assert.True(resumed.IsSuccess);
        Assert.Equal(StepExecutionStatus.Completed, resumed.Value!.FinalStatus);
        Assert.DoesNotContain(firstStepId, executions);
        Assert.Contains(secondStepId, executions);
    }

    [Fact]
    public async Task CancelAsync_CalledTwiceWhileRunning_IsIdempotent()
    {
        var planId = PlanId.New();
        var plan = BuildPlan(planId, stepCount: 1);

        var stepEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = new Harness(plan);
        harness.OnStep = async (_, ct) =>
        {
            stepEntered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Completed();
        };

        var sut = harness.CreateSut();
        var run = Task.Run(() => sut.ExecuteAsync(planId, CancellationToken.None));
        await stepEntered.Task.WaitAsync(DeadlockBudget);

        var first = await sut.CancelAsync(planId, CancellationToken.None).WaitAsync(DeadlockBudget);
        await run.WaitAsync(DeadlockBudget);
        var second = await sut.CancelAsync(planId, CancellationToken.None).WaitAsync(DeadlockBudget);
        var third = await sut.CancelAsync(planId, CancellationToken.None).WaitAsync(DeadlockBudget);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.True(third.IsSuccess);

        var persisted = harness.PersistedStates;
        Assert.All(persisted.Values, s => Assert.Equal(StepExecutionStatus.Cancelled, s.Status));
    }

    /// <summary>
    /// Concurrency evidence for the question "is signalling a run from another thread safe next to
    /// the executor's static per-plan lock dictionary?". The registry is a separate structure and is
    /// never touched by the lock bookkeeping, but the two run concurrently on the same plan id, so
    /// this hammers register/signal/release against acquire/release/dispose of the plan lock. A
    /// registration disposed mid-signal would surface as <see cref="ObjectDisposedException"/>.
    /// </summary>
    [Fact]
    public async Task Registry_ConcurrentRegisterCancelRelease_NeverThrowsAndAlwaysSignalsLiveRuns()
    {
        var registry = new PlanRunCancellationRegistry();
        var planId = PlanId.New();
        var failures = new ConcurrentBag<Exception>();
        var observedCancellations = 0;

        // Iteration-bounded rather than wall-clock bounded, and yielding rather than spinning: this
        // assembly also holds child-process tests whose timing a CPU-saturating loop destabilises.
        const int Iterations = 4000;

        var registrars = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < Iterations; i++)
            {
                try
                {
                    using var registration = registry.Register(planId);
                    Thread.Yield();
                    if (registration.Token.IsCancellationRequested)
                        Interlocked.Increment(ref observedCancellations);
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }
        })).ToArray();

        var cancellers = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < Iterations; i++)
            {
                try
                {
                    registry.TryCancel(planId);
                    Thread.Yield();
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }
        })).ToArray();

        await Task.WhenAll(registrars.Concat(cancellers));

        Assert.True(failures.IsEmpty, $"Expected no exceptions; first was: {failures.FirstOrDefault()}");

        // Proves the run did what it claims: if no registration ever observed a signal, the loop
        // above would pass while testing nothing.
        Assert.True(observedCancellations > 0, "No registration ever observed cancellation — the race was never exercised.");

        // Every registration was disposed, so the index must be empty and a further cancel a no-op.
        Assert.False(registry.TryCancel(planId));
    }

    [Fact]
    public void Registry_ReleaseIsIdempotentAndUnregisteredCancelReportsNothingSignalled()
    {
        var registry = new PlanRunCancellationRegistry();
        var planId = PlanId.New();

        Assert.False(registry.TryCancel(planId));

        var registration = registry.Register(planId);
        Assert.True(registry.TryCancel(planId));
        Assert.True(registration.Token.IsCancellationRequested);

        registration.Dispose();
        registration.Dispose();

        Assert.False(registry.TryCancel(planId));
    }

    [Fact]
    public void Registry_ConcurrentRunsOfSamePlan_ReleaseOfOneLeavesTheOtherSignallable()
    {
        var registry = new PlanRunCancellationRegistry();
        var planId = PlanId.New();

        using var queued = registry.Register(planId);
        var running = registry.Register(planId);

        // The finishing run releases. Releasing by plan id alone would tear down the queued run's
        // registration too, leaving it silently uncancellable.
        running.Dispose();

        Assert.True(registry.TryCancel(planId));
        Assert.True(queued.Token.IsCancellationRequested);
    }

    /// <summary>
    /// Cancel latency is bounded by the slowest step executor, so each must let cancellation
    /// propagate rather than converting it into a step failure. <c>SubPlanStepExecutor</c> caught
    /// every exception, which swallowed <see cref="OperationCanceledException"/> and turned a plan
    /// cancellation into a retryable failure of the sub-plan step.
    /// </summary>
    [Fact]
    public async Task SubPlanStepExecutor_WhenChildPlanIsCancelled_PropagatesInsteadOfReportingStepFailure()
    {
        var childPlanId = PlanId.New();
        var childExecutor = new Mock<IPlanExecutor>();
        childExecutor
            .Setup(e => e.ExecuteAsync(It.IsAny<PlanId>(), It.IsAny<PlanExecutionContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var services = new ServiceCollection();
        services.AddScoped(_ => childExecutor.Object);

        var sut = new global::Infrastructure.AI.Planner.StepExecutors.SubPlanStepExecutor(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<IPlanStateStore>(),
            Mock.Of<IPlanProgressNotifier>(),
            new PlanExecutionContext(),
            NullLogger<global::Infrastructure.AI.Planner.StepExecutors.SubPlanStepExecutor>.Instance);

        var step = new PlanStep
        {
            Id = new PlanStepId(Guid.NewGuid()),
            Name = "sub-plan-step",
            Type = StepType.SubPlanInvocation,
            Configuration = new SubPlanConfig { ChildPlanId = childPlanId },
            RetryPolicy = new RetryPolicy { MaxRetries = 0 }
        };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None));
    }

    private static StepExecutionResult Completed(string output = "ok") => new()
    {
        Status = StepExecutionStatus.Completed,
        Output = output,
        Duration = TimeSpan.FromMilliseconds(1)
    };

    private static PlanGraph BuildPlan(PlanId planId, int stepCount, bool chained = false)
    {
        var steps = Enumerable.Range(0, stepCount).Select(i => new PlanStep
        {
            Id = new PlanStepId(Guid.NewGuid()),
            Name = $"step-{i}",
            Type = StepType.LlmCall,
            Configuration = new LlmCallConfig { SystemPrompt = "test", ModelDeploymentKey = "gpt-4" },
            RetryPolicy = new RetryPolicy { MaxRetries = 0 },
            Timeout = TimeSpan.FromSeconds(30)
        }).ToList();

        var edges = chained
            ? Enumerable.Range(0, stepCount - 1)
                .Select(i => new PlanEdge(steps[i].Id, steps[i + 1].Id, EdgeType.ControlFlow))
                .ToList()
            : [];

        return new PlanGraph
        {
            Id = planId,
            Name = "cancellation-plan",
            Steps = steps,
            Edges = edges,
            Configuration = new PlanConfiguration
            {
                PlanTimeout = TimeSpan.FromSeconds(60),
                MaxParallelSteps = 2
            }
        };
    }

    /// <summary>
    /// A <see cref="PlanExecutor"/> wired to an in-memory state store, so resume genuinely reads back
    /// what the previous run persisted rather than a canned dictionary. The registry is shared across
    /// every executor the harness builds, mirroring its singleton registration.
    /// </summary>
    private sealed class Harness
    {
        private readonly PlanGraph _plan;
        private readonly PlanRunCancellationRegistry _registry = new();
        private readonly ConcurrentDictionary<PlanStepId, StepExecutionState> _states = new();

        public Harness(PlanGraph plan) => _plan = plan;

        public Func<PlanStep, CancellationToken, Task<StepExecutionResult>> OnStep { get; set; } =
            (_, _) => Task.FromResult(Completed());

        public IReadOnlyDictionary<PlanStepId, StepExecutionState> PersistedStates => _states;

        public PlanExecutor CreateSut()
        {
            var validator = new Mock<IPlanValidator>();
            validator.Setup(v => v.ValidateAsync(It.IsAny<PlanGraph>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result<PlanValidationResult>.Success(new PlanValidationResult { IsValid = true }));

            var stateStore = new Mock<IPlanStateStore>();
            stateStore.Setup(s => s.LoadPlanAsync(It.IsAny<PlanId>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result<PlanGraph?>.Success(_plan));
            stateStore.Setup(s => s.ResumeAsync(It.IsAny<PlanId>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Result<IReadOnlyDictionary<PlanStepId, StepExecutionState>>.Success(Snapshot()));
            stateStore.Setup(s => s.LoadStepStatesAsync(It.IsAny<PlanId>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Result<IReadOnlyDictionary<PlanStepId, StepExecutionState>>.Success(Snapshot()));
            stateStore.Setup(s => s.UpdateStepStateAsync(It.IsAny<StepExecutionState>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((StepExecutionState state, CancellationToken _) =>
                {
                    _states[state.StepId] = state;
                    return Result.Success();
                });
            stateStore.Setup(s => s.CheckpointAsync(
                    It.IsAny<PlanId>(), It.IsAny<IReadOnlyList<StepExecutionState>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((PlanId _, IReadOnlyList<StepExecutionState> states, CancellationToken _) =>
                {
                    foreach (var state in states)
                        _states[state.StepId] = state;
                    return Result.Success();
                });

            var services = new ServiceCollection();
            var stepExecutor = new Mock<IPlanStepExecutor>();
            stepExecutor.Setup(e => e.ExecuteAsync(
                    It.IsAny<PlanStep>(), It.IsAny<IReadOnlyDictionary<PlanStepId, string>>(), It.IsAny<CancellationToken>()))
                .Returns<PlanStep, IReadOnlyDictionary<PlanStepId, string>, CancellationToken>((step, _, ct) => OnStep(step, ct));
            services.AddKeyedSingleton<IPlanStepExecutor>(StepType.LlmCall, stepExecutor.Object);

            return new PlanExecutor(
                validator.Object,
                stateStore.Object,
                Mock.Of<IPlanProgressNotifier>(),
                Mock.Of<IEscalationService>(),
                services.BuildServiceProvider(),
                _registry,
                TimeProvider.System,
                NullLogger<PlanExecutor>.Instance);
        }

        private IReadOnlyDictionary<PlanStepId, StepExecutionState> Snapshot()
            => new Dictionary<PlanStepId, StepExecutionState>(_states);
    }
}
