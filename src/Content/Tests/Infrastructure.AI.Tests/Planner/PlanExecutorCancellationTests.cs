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
    /// Cancellation must survive one level of nesting. A sub-plan registers under the CHILD plan's
    /// identifier, so <c>TryCancel(parentPlanId)</c> does not reach it directly; without the
    /// registry linking a nested run to the run that invoked it, the child's interrupted step is
    /// recorded <c>Failed</c> rather than <c>Cancelled</c>. <c>Failed</c> is terminal on resume, so
    /// the child could never re-run, its downstream step would never become ready, and the parent
    /// step would fail on every subsequent resume — permanently unresumable.
    /// </summary>
    /// <remarks>
    /// Deliberately built from two REAL <see cref="PlanExecutor"/> instances over one shared
    /// registry and a real <see cref="global::Infrastructure.AI.Planner.StepExecutors.SubPlanStepExecutor"/>.
    /// An earlier version of this test stubbed the child <see cref="IPlanExecutor"/> to throw
    /// <see cref="OperationCanceledException"/>; the real executor catches that internally and
    /// returns a summary instead, so the stub tested a shape production never produces and the
    /// nesting defect stayed invisible. A test whose subject is nesting cannot stub the thing being
    /// nested.
    /// </remarks>
    /// <param name="childStepMaxRetries">
    /// Exercised at both 0 and 3. With no retry budget the parent step bypasses the backoff delay
    /// whose token check used to be the only thing recording it Cancelled, so the guarantee held
    /// only for retry-enabled steps.
    /// </param>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task CancelAsync_WhileSubPlanStepInFlight_RecordsChildAndParentCancelledAndStaysResumable(
        int childStepMaxRetries)
    {
        var registry = new PlanRunCancellationRegistry();
        var parentPlanId = PlanId.New();
        var childPlanId = PlanId.New();

        var childHarness = new Harness(BuildPlan(childPlanId, stepCount: 2, chained: true), registry);
        var childStep1 = childHarness.Plan.Steps[0].Id;
        var childStep2 = childHarness.Plan.Steps[1].Id;

        var childStep1Running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        childHarness.OnStep = async (step, ct) =>
        {
            if (step.Id != childStep1)
                return Completed("child-step-2");

            childStep1Running.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Completed("child-step-1");
        };

        // The parent's single step is a real SubPlanInvocation into the child plan.
        var parentStep = new PlanStep
        {
            Id = new PlanStepId(Guid.NewGuid()),
            Name = "invoke-child",
            Type = StepType.SubPlanInvocation,
            Configuration = new SubPlanConfig { ChildPlanId = childPlanId },
            RetryPolicy = new RetryPolicy { MaxRetries = childStepMaxRetries, InitialDelay = TimeSpan.FromMilliseconds(50) },
            Timeout = TimeSpan.FromSeconds(30)
        };
        var parentPlan = new PlanGraph
        {
            Id = parentPlanId,
            Name = "parent-plan",
            Steps = [parentStep],
            Edges = [],
            Configuration = new PlanConfiguration { PlanTimeout = TimeSpan.FromSeconds(60), MaxParallelSteps = 2 }
        };

        var parentHarness = new Harness(parentPlan, registry);
        var parentSut = parentHarness.CreateSut(subPlanChild: childHarness.CreateSut());

        var parentRun = Task.Run(() => parentSut.ExecuteAsync(parentPlanId, CancellationToken.None));
        await childStep1Running.Task.WaitAsync(DeadlockBudget);

        // Cancel the PARENT. Nothing here names the child plan.
        var cancelResult = await parentSut.CancelAsync(parentPlanId, CancellationToken.None).WaitAsync(DeadlockBudget);
        await parentRun.WaitAsync(DeadlockBudget);

        Assert.True(cancelResult.IsSuccess);

        // The child's in-flight step must be Cancelled, not Failed — Failed would be terminal.
        Assert.Equal(StepExecutionStatus.Cancelled, childHarness.PersistedStates[childStep1].Status);
        Assert.NotEqual(StepExecutionStatus.Failed, childHarness.PersistedStates[childStep1].Status);

        // The parent step likewise, independent of its retry budget.
        Assert.Equal(StepExecutionStatus.Cancelled, parentHarness.PersistedStates[parentStep.Id].Status);

        // Resumability is the property that matters: resume the child and confirm it finishes.
        var childExecutions = new ConcurrentBag<PlanStepId>();
        childHarness.OnStep = (step, _) =>
        {
            childExecutions.Add(step.Id);
            return Task.FromResult(Completed($"{step.Name}-resumed"));
        };

        var resumedChild = await childHarness.CreateSut()
            .ExecuteAsync(childPlanId, CancellationToken.None)
            .WaitAsync(DeadlockBudget);

        Assert.True(resumedChild.IsSuccess);
        Assert.Equal(StepExecutionStatus.Completed, resumedChild.Value!.FinalStatus);
        Assert.Contains(childStep1, childExecutions);
        Assert.Contains(childStep2, childExecutions);
    }

    /// <summary>
    /// Guards the premise of the nesting test above: the child plan must genuinely reach its second
    /// step when nothing cancels it. Without this, the resume assertions could pass against a child
    /// that had simply never progressed.
    /// </summary>
    [Fact]
    public async Task SubPlanInvocation_WithoutCancellation_RunsChildPlanToCompletion()
    {
        var registry = new PlanRunCancellationRegistry();
        var parentPlanId = PlanId.New();
        var childPlanId = PlanId.New();

        var childHarness = new Harness(BuildPlan(childPlanId, stepCount: 2, chained: true), registry);
        var childExecutions = new ConcurrentBag<PlanStepId>();
        childHarness.OnStep = (step, _) =>
        {
            childExecutions.Add(step.Id);
            return Task.FromResult(Completed($"{step.Name}-ok"));
        };

        var parentStep = new PlanStep
        {
            Id = new PlanStepId(Guid.NewGuid()),
            Name = "invoke-child",
            Type = StepType.SubPlanInvocation,
            Configuration = new SubPlanConfig { ChildPlanId = childPlanId },
            RetryPolicy = new RetryPolicy { MaxRetries = 0 },
            Timeout = TimeSpan.FromSeconds(30)
        };
        var parentPlan = new PlanGraph
        {
            Id = parentPlanId,
            Name = "parent-plan",
            Steps = [parentStep],
            Edges = [],
            Configuration = new PlanConfiguration { PlanTimeout = TimeSpan.FromSeconds(60), MaxParallelSteps = 2 }
        };

        var parentHarness = new Harness(parentPlan, registry);
        var parentSut = parentHarness.CreateSut(subPlanChild: childHarness.CreateSut());

        var result = await parentSut.ExecuteAsync(parentPlanId, CancellationToken.None).WaitAsync(DeadlockBudget);

        Assert.True(result.IsSuccess);
        Assert.Equal(StepExecutionStatus.Completed, result.Value!.FinalStatus);
        Assert.Equal(2, childExecutions.Distinct().Count());
        Assert.Equal(StepExecutionStatus.Completed, parentHarness.PersistedStates[parentStep.Id].Status);
    }

    /// <summary>
    /// A child plan cancelled directly, while its parent is not, is a genuine failure of the parent
    /// step — the parent was not asked to stop. Pins the boundary of the cascade so it is not
    /// mistaken for "any child cancellation cancels the parent".
    /// </summary>
    [Fact]
    public async Task Registry_CancellingChildOnly_DoesNotCancelParentRun()
    {
        var registry = new PlanRunCancellationRegistry();
        var parentPlanId = PlanId.New();
        var childPlanId = PlanId.New();

        using var parent = registry.Register(parentPlanId);

        // Simulates the child registering from within the parent's execution flow.
        using var child = registry.Register(childPlanId);

        Assert.True(registry.TryCancel(childPlanId));
        Assert.True(child.Token.IsCancellationRequested);
        Assert.False(parent.Token.IsCancellationRequested);
    }

    /// <summary>
    /// The cascade itself, at the registry level: a run registered inside another run's flow is
    /// signalled when the outer run is cancelled, without the outer cancel naming it.
    /// </summary>
    [Fact]
    public async Task Registry_CancellingParent_CascadesToRunRegisteredWithinItsFlow()
    {
        var registry = new PlanRunCancellationRegistry();
        var parentPlanId = PlanId.New();
        var childPlanId = PlanId.New();

        using var parent = registry.Register(parentPlanId);

        // Registered on a spawned flow, mirroring a step task invoking a sub-plan.
        var child = await Task.Run(() => registry.Register(childPlanId));

        Assert.True(registry.TryCancel(parentPlanId));
        Assert.True(child.Token.IsCancellationRequested);

        child.Dispose();
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
        private readonly PlanRunCancellationRegistry _registry;
        private readonly ConcurrentDictionary<PlanStepId, StepExecutionState> _states = new();

        public Harness(PlanGraph plan, PlanRunCancellationRegistry? registry = null)
        {
            _plan = plan;
            _registry = registry ?? new PlanRunCancellationRegistry();
        }

        public PlanGraph Plan => _plan;

        public Func<PlanStep, CancellationToken, Task<StepExecutionResult>> OnStep { get; set; } =
            (_, _) => Task.FromResult(Completed());

        public IReadOnlyDictionary<PlanStepId, StepExecutionState> PersistedStates => _states;

        /// <summary>
        /// Builds the executor. When <paramref name="subPlanChild"/> is supplied, a real
        /// <c>SubPlanStepExecutor</c> is registered for <see cref="StepType.SubPlanInvocation"/>
        /// and resolves that executor as the child — so nesting runs through production code rather
        /// than a stub.
        /// </summary>
        public PlanExecutor CreateSut(PlanExecutor? subPlanChild = null)
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

            if (subPlanChild is not null)
            {
                // The real SubPlanStepExecutor, resolving the real child PlanExecutor from the
                // scope it creates. Nothing about the nesting is stubbed.
                services.AddScoped<IPlanExecutor>(_ => subPlanChild);
                services.AddKeyedSingleton<IPlanStepExecutor>(
                    StepType.SubPlanInvocation,
                    (sp, _) => new global::Infrastructure.AI.Planner.StepExecutors.SubPlanStepExecutor(
                        sp.GetRequiredService<IServiceScopeFactory>(),
                        Mock.Of<IPlanStateStore>(),
                        Mock.Of<IPlanProgressNotifier>(),
                        new PlanExecutionContext(),
                        NullLogger<global::Infrastructure.AI.Planner.StepExecutors.SubPlanStepExecutor>.Instance));
            }

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
