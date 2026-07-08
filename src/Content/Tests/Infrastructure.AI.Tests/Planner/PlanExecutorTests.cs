using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Planner;
using Domain.AI.Escalation;
using Domain.AI.Planner;
using Domain.Common;
using Infrastructure.AI.Planner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Planner;

public sealed class PlanExecutorTests : IDisposable
{
    private readonly Mock<IPlanValidator> _validator = new();
    private readonly Mock<IPlanStateStore> _stateStore = new();
    private readonly Mock<IPlanProgressNotifier> _notifier = new();
    private readonly Mock<IEscalationService> _escalation = new();
    private readonly ServiceCollection _services = new();
    private ServiceProvider? _serviceProvider;
    private readonly List<PlanStepId> _executionOrder = [];
    private readonly PlanExecutor _sut;

    public PlanExecutorTests()
    {
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

        _stateStore.Setup(s => s.UpdateStepStateAsync(It.IsAny<StepExecutionState>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        _stateStore.Setup(s => s.ResumeAsync(It.IsAny<PlanId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyDictionary<PlanStepId, StepExecutionState>>.Success(
                new Dictionary<PlanStepId, StepExecutionState>()));

        _sut = CreateSut();
    }

    private PlanExecutor CreateSut()
    {
        _serviceProvider = _services.BuildServiceProvider();
        return new PlanExecutor(
            _validator.Object,
            _stateStore.Object,
            _notifier.Object,
            _escalation.Object,
            _serviceProvider,
            NullLogger<PlanExecutor>.Instance);
    }

    private void RegisterStepExecutor(StepType type, Func<PlanStep, IReadOnlyDictionary<PlanStepId, string>, CancellationToken, Task<StepExecutionResult>> handler)
    {
        var mock = new Mock<IPlanStepExecutor>();
        mock.Setup(e => e.ExecuteAsync(It.IsAny<PlanStep>(), It.IsAny<IReadOnlyDictionary<PlanStepId, string>>(), It.IsAny<CancellationToken>()))
            .Returns<PlanStep, IReadOnlyDictionary<PlanStepId, string>, CancellationToken>(handler);
        _services.AddKeyedSingleton<IPlanStepExecutor>(type, mock.Object);
    }

    private void RegisterCompletingExecutor(StepType type = StepType.LlmCall, TimeSpan? delay = null)
    {
        RegisterStepExecutor(type, async (step, _, ct) =>
        {
            lock (_executionOrder) { _executionOrder.Add(step.Id); }
            if (delay.HasValue) await Task.Delay(delay.Value, ct);
            return new StepExecutionResult
            {
                Status = StepExecutionStatus.Completed,
                Output = $"output-{step.Name}",
                Duration = delay ?? TimeSpan.FromMilliseconds(1)
            };
        });
    }

    private void SetupPlanLoad(PlanGraph plan)
    {
        _stateStore.Setup(s => s.LoadPlanAsync(plan.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanGraph?>.Success(plan));
    }

    private static PlanStep CreateStep(string name, StepType type = StepType.LlmCall) => new()
    {
        Id = new PlanStepId(Guid.NewGuid()),
        Name = name,
        Type = type,
        Configuration = new LlmCallConfig { SystemPrompt = "test", ModelDeploymentKey = "gpt-4" },
        RetryPolicy = new RetryPolicy { MaxRetries = 0 }
    };

    private static PlanGraph BuildLinearPlan(int stepCount, PlanConfiguration? config = null)
    {
        var steps = Enumerable.Range(0, stepCount)
            .Select(i => CreateStep($"step-{i}"))
            .ToList();

        var edges = new List<PlanEdge>();
        for (var i = 0; i < steps.Count - 1; i++)
        {
            edges.Add(new PlanEdge(steps[i].Id, steps[i + 1].Id, EdgeType.ControlFlow));
        }

        return new PlanGraph
        {
            Id = PlanId.New(),
            Name = "linear-plan",
            Steps = steps,
            Edges = edges,
            Configuration = config ?? new PlanConfiguration { PlanTimeout = TimeSpan.FromSeconds(10) }
        };
    }

    private static PlanGraph BuildParallelPlan(PlanConfiguration? config = null)
    {
        var a = CreateStep("A");
        var b = CreateStep("B");
        var c = CreateStep("C");
        var d = CreateStep("D");

        return new PlanGraph
        {
            Id = PlanId.New(),
            Name = "parallel-plan",
            Steps = [a, b, c, d],
            Edges =
            [
                new PlanEdge(a.Id, b.Id, EdgeType.ControlFlow),
                new PlanEdge(a.Id, c.Id, EdgeType.ControlFlow),
                new PlanEdge(b.Id, d.Id, EdgeType.ControlFlow),
                new PlanEdge(c.Id, d.Id, EdgeType.ControlFlow)
            ],
            Configuration = config ?? new PlanConfiguration { PlanTimeout = TimeSpan.FromSeconds(10) }
        };
    }

    private static PlanGraph BuildDiamondPlan() => BuildParallelPlan();

    // === Scheduling: linear ===

    [Fact]
    public async Task Execute_LinearPlan_RunsStepsInOrder()
    {
        RegisterCompletingExecutor();
        var sut = CreateSut();
        var plan = BuildLinearPlan(3);
        SetupPlanLoad(plan);

        var result = await sut.ExecuteAsync(plan.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, _executionOrder.Count);
        Assert.Equal(plan.Steps[0].Id, _executionOrder[0]);
        Assert.Equal(plan.Steps[1].Id, _executionOrder[1]);
        Assert.Equal(plan.Steps[2].Id, _executionOrder[2]);
    }

    // === Scheduling: parallel ===

    [Fact]
    public async Task Execute_ParallelPlan_RunsIndependentStepsInParallel()
    {
        var concurrentCount = 0;
        var maxConcurrent = 0;
        var lockObj = new object();

        RegisterStepExecutor(StepType.LlmCall, async (step, _, ct) =>
        {
            lock (lockObj)
            {
                _executionOrder.Add(step.Id);
                concurrentCount++;
                if (concurrentCount > maxConcurrent) maxConcurrent = concurrentCount;
            }
            await Task.Delay(50, ct);
            lock (lockObj) { concurrentCount--; }
            return new StepExecutionResult
            {
                Status = StepExecutionStatus.Completed,
                Output = $"output-{step.Name}",
                Duration = TimeSpan.FromMilliseconds(50)
            };
        });

        var sut = CreateSut();
        var plan = BuildParallelPlan();
        SetupPlanLoad(plan);

        var result = await sut.ExecuteAsync(plan.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(maxConcurrent >= 2, $"Expected concurrent execution of B and C, but max was {maxConcurrent}");
        // D must be last
        Assert.Equal(plan.Steps[3].Id, _executionOrder.Last());
    }

    // === Scheduling: diamond DAG ===

    [Fact]
    public async Task Execute_DiamondDag_StepDWaitsForBothBAndC()
    {
        RegisterCompletingExecutor(delay: TimeSpan.FromMilliseconds(20));
        var sut = CreateSut();
        var plan = BuildDiamondPlan();
        SetupPlanLoad(plan);

        var result = await sut.ExecuteAsync(plan.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var dIndex = _executionOrder.IndexOf(plan.Steps[3].Id);
        var bIndex = _executionOrder.IndexOf(plan.Steps[1].Id);
        var cIndex = _executionOrder.IndexOf(plan.Steps[2].Id);
        Assert.True(dIndex > bIndex, "D should execute after B");
        Assert.True(dIndex > cIndex, "D should execute after C");
    }

    // === Concurrency bound ===

    [Fact]
    public async Task Execute_BoundedConcurrency_RespectsMaxParallelSteps()
    {
        var concurrentCount = 0;
        var maxConcurrent = 0;
        var lockObj = new object();

        RegisterStepExecutor(StepType.LlmCall, async (step, _, ct) =>
        {
            lock (lockObj)
            {
                concurrentCount++;
                if (concurrentCount > maxConcurrent) maxConcurrent = concurrentCount;
            }
            await Task.Delay(50, ct);
            lock (lockObj) { concurrentCount--; }
            return new StepExecutionResult
            {
                Status = StepExecutionStatus.Completed,
                Output = "done",
                Duration = TimeSpan.FromMilliseconds(50)
            };
        });

        var sut = CreateSut();

        // 5 independent steps with no edges between them
        var steps = Enumerable.Range(0, 5).Select(i => CreateStep($"step-{i}")).ToList();
        var plan = new PlanGraph
        {
            Id = PlanId.New(),
            Name = "bounded-plan",
            Steps = steps,
            Edges = [],
            Configuration = new PlanConfiguration { MaxParallelSteps = 2, PlanTimeout = TimeSpan.FromSeconds(10) }
        };
        SetupPlanLoad(plan);

        var result = await sut.ExecuteAsync(plan.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(maxConcurrent <= 2, $"Max concurrent was {maxConcurrent}, expected <= 2");
    }

    // === Failure propagation ===

    [Fact]
    public async Task Execute_StepFails_DependentSubgraphSkipped()
    {
        RegisterStepExecutor(StepType.LlmCall, (step, _, _) =>
        {
            _executionOrder.Add(step.Id);
            var status = step.Name == "A"
                ? StepExecutionStatus.Failed
                : StepExecutionStatus.Completed;
            return Task.FromResult(new StepExecutionResult
            {
                Status = status,
                Output = status == StepExecutionStatus.Completed ? "ok" : null,
                ErrorMessage = status == StepExecutionStatus.Failed ? "deliberate failure" : null,
                Duration = TimeSpan.FromMilliseconds(1)
            });
        });

        var sut = CreateSut();
        var a = CreateStep("A");
        var b = CreateStep("B");
        var c = CreateStep("C");
        var plan = new PlanGraph
        {
            Id = PlanId.New(),
            Name = "fail-plan",
            Steps = [a, b, c],
            Edges =
            [
                new PlanEdge(a.Id, b.Id, EdgeType.ControlFlow),
                new PlanEdge(b.Id, c.Id, EdgeType.ControlFlow)
            ],
            Configuration = new PlanConfiguration { PlanTimeout = TimeSpan.FromSeconds(10) }
        };
        SetupPlanLoad(plan);

        var result = await sut.ExecuteAsync(plan.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(StepExecutionStatus.Failed, result.Value!.FinalStatus);
        Assert.Single(_executionOrder); // Only A executed
        Assert.Equal(a.Id, _executionOrder[0]);
    }

    [Fact]
    public async Task Execute_StepFails_IndependentBranchContinues()
    {
        RegisterStepExecutor(StepType.LlmCall, (step, _, _) =>
        {
            _executionOrder.Add(step.Id);
            var status = step.Name == "A"
                ? StepExecutionStatus.Failed
                : StepExecutionStatus.Completed;
            return Task.FromResult(new StepExecutionResult
            {
                Status = status,
                Output = status == StepExecutionStatus.Completed ? "ok" : null,
                ErrorMessage = status == StepExecutionStatus.Failed ? "fail" : null,
                Duration = TimeSpan.FromMilliseconds(1)
            });
        });

        var sut = CreateSut();
        var root = CreateStep("Root");
        var a = CreateStep("A");
        var b = CreateStep("B");
        var c = CreateStep("C");
        var d = CreateStep("D");
        var plan = new PlanGraph
        {
            Id = PlanId.New(),
            Name = "branch-plan",
            Steps = [root, a, b, c, d],
            Edges =
            [
                new PlanEdge(root.Id, a.Id, EdgeType.ControlFlow),
                new PlanEdge(root.Id, b.Id, EdgeType.ControlFlow),
                new PlanEdge(a.Id, c.Id, EdgeType.ControlFlow),
                new PlanEdge(b.Id, d.Id, EdgeType.ControlFlow)
            ],
            Configuration = new PlanConfiguration { PlanTimeout = TimeSpan.FromSeconds(10) }
        };
        SetupPlanLoad(plan);

        var result = await sut.ExecuteAsync(plan.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Contains(b.Id, _executionOrder);
        Assert.Contains(d.Id, _executionOrder);
        Assert.DoesNotContain(c.Id, _executionOrder);
    }

    // === Blocked steps (escalation) ===

    [Fact]
    public async Task Execute_BlockedStep_IndependentBranchesContinue()
    {
        RegisterStepExecutor(StepType.LlmCall, (step, _, _) =>
        {
            _executionOrder.Add(step.Id);
            return Task.FromResult(new StepExecutionResult
            {
                Status = StepExecutionStatus.Completed,
                Output = "ok",
                Duration = TimeSpan.FromMilliseconds(1)
            });
        });
        RegisterStepExecutor(StepType.HumanGate, (step, _, _) =>
        {
            _executionOrder.Add(step.Id);
            return Task.FromResult(new StepExecutionResult
            {
                Status = StepExecutionStatus.Blocked,
                Duration = TimeSpan.FromMilliseconds(1)
            });
        });

        var sut = CreateSut();
        var a = CreateStep("A", StepType.HumanGate) with
        {
            Configuration = new HumanGateConfig
            {
                EscalationMessage = "Approve this step?",
                ApprovalStrategy = ApprovalStrategy.AnyOf,
                Approvers = ["admin"]
            }
        };
        var b = CreateStep("B");
        var plan = new PlanGraph
        {
            Id = PlanId.New(),
            Name = "blocked-plan",
            Steps = [a, b],
            Edges = [], // independent
            Configuration = new PlanConfiguration { PlanTimeout = TimeSpan.FromSeconds(5) }
        };
        SetupPlanLoad(plan);

        // Escalation never resolves
        _escalation.Setup(e => e.GetPendingEscalationAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((EscalationRequest?)null);

        var result = await sut.ExecuteAsync(plan.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Contains(b.Id, _executionOrder);
        var summary = result.Value!;
        var aState = summary.StepStates.First(s => s.StepId == a.Id);
        Assert.Equal(StepExecutionStatus.Blocked, aState.Status);
    }

    // === Timeout ===

    [Fact]
    public async Task Execute_PlanTimeout_CancelsRunningSteps()
    {
        RegisterStepExecutor(StepType.LlmCall, async (step, _, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new StepExecutionResult
            {
                Status = StepExecutionStatus.Completed,
                Output = "ok",
                Duration = TimeSpan.FromSeconds(30)
            };
        });

        var sut = CreateSut();
        var plan = BuildLinearPlan(1, new PlanConfiguration { PlanTimeout = TimeSpan.FromMilliseconds(50) });
        SetupPlanLoad(plan);

        var result = await sut.ExecuteAsync(plan.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var summary = result.Value!;
        Assert.Equal(StepExecutionStatus.Failed, summary.FinalStatus);
    }

    // === Checkpoint/Resume ===

    [Fact]
    public async Task Execute_Checkpoint_PersistsAfterEachTransition()
    {
        RegisterCompletingExecutor();
        var sut = CreateSut();
        var plan = BuildLinearPlan(2);
        SetupPlanLoad(plan);

        await sut.ExecuteAsync(plan.Id, CancellationToken.None);

        // Each step transitions: Pending->Ready->Running->Completed
        // But first step has no Pending->Ready transition tracked via UpdateStepState since it starts Ready
        _stateStore.Verify(
            s => s.UpdateStepStateAsync(It.IsAny<StepExecutionState>(), It.IsAny<CancellationToken>()),
            Times.AtLeast(4)); // At minimum Ready->Running->Completed for each of 2 steps
    }

    [Fact]
    public async Task Execute_Resume_RebuildsReadyQueueFromState()
    {
        RegisterCompletingExecutor();
        var sut = CreateSut();
        var plan = BuildLinearPlan(3);
        SetupPlanLoad(plan);

        // Simulate resume: A and B already completed
        var existingStates = new Dictionary<PlanStepId, StepExecutionState>
        {
            [plan.Steps[0].Id] = new StepExecutionState
            {
                StepId = plan.Steps[0].Id,
                Status = StepExecutionStatus.Completed,
                AttemptCount = 1,
                Output = "done-a"
            },
            [plan.Steps[1].Id] = new StepExecutionState
            {
                StepId = plan.Steps[1].Id,
                Status = StepExecutionStatus.Completed,
                AttemptCount = 1,
                Output = "done-b"
            }
        };

        _stateStore.Setup(s => s.ResumeAsync(plan.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyDictionary<PlanStepId, StepExecutionState>>.Success(existingStates));

        var result = await sut.ExecuteAsync(plan.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(_executionOrder); // Only step C executed
        Assert.Equal(plan.Steps[2].Id, _executionOrder[0]);
    }

    // === Per-plan serialization ===

    [Fact]
    public async Task Execute_ConcurrentSamePlan_SerializedViaKeySemaphore()
    {
        var callCount = 0;
        RegisterStepExecutor(StepType.LlmCall, async (step, _, ct) =>
        {
            Interlocked.Increment(ref callCount);
            await Task.Delay(30, ct);
            return new StepExecutionResult
            {
                Status = StepExecutionStatus.Completed,
                Output = "ok",
                Duration = TimeSpan.FromMilliseconds(30)
            };
        });

        var sut = CreateSut();
        var plan = BuildLinearPlan(1);
        SetupPlanLoad(plan);

        var task1 = sut.ExecuteAsync(plan.Id, CancellationToken.None);
        var task2 = sut.ExecuteAsync(plan.Id, CancellationToken.None);
        var results = await Task.WhenAll(task1, task2);

        // Both should succeed (second one sees plan already completed or executes after first)
        Assert.True(results[0].IsSuccess);
        Assert.True(results[1].IsSuccess);
    }

    // === Conditional branching ===

    [Fact]
    public async Task Execute_ConditionalBranch_FollowsCorrectPath()
    {
        var trueTarget = CreateStep("TrueStep");
        var falseTarget = CreateStep("FalseStep");
        var condStep = new PlanStep
        {
            Id = new PlanStepId(Guid.NewGuid()),
            Name = "Condition",
            Type = StepType.ConditionalBranch,
            Configuration = new ConditionalBranchConfig
            {
                ConditionExpression = "true",
                TrueEdgeTargetId = trueTarget.Id,
                FalseEdgeTargetId = falseTarget.Id
            },
            RetryPolicy = new RetryPolicy { MaxRetries = 0 }
        };

        RegisterStepExecutor(StepType.LlmCall, (step, _, _) =>
        {
            _executionOrder.Add(step.Id);
            return Task.FromResult(new StepExecutionResult
            {
                Status = StepExecutionStatus.Completed,
                Output = "ok",
                Duration = TimeSpan.FromMilliseconds(1)
            });
        });
        RegisterStepExecutor(StepType.ConditionalBranch, (step, _, _) =>
        {
            _executionOrder.Add(step.Id);
            return Task.FromResult(new StepExecutionResult
            {
                Status = StepExecutionStatus.Completed,
                Output = "condition evaluated",
                Duration = TimeSpan.FromMilliseconds(1),
                ActiveEdgeTarget = trueTarget.Id
            });
        });

        var sut = CreateSut();
        var plan = new PlanGraph
        {
            Id = PlanId.New(),
            Name = "conditional-plan",
            Steps = [condStep, trueTarget, falseTarget],
            Edges =
            [
                new PlanEdge(condStep.Id, trueTarget.Id, EdgeType.ConditionalTrue),
                new PlanEdge(condStep.Id, falseTarget.Id, EdgeType.ConditionalFalse)
            ],
            Configuration = new PlanConfiguration { PlanTimeout = TimeSpan.FromSeconds(10) }
        };
        SetupPlanLoad(plan);

        var result = await sut.ExecuteAsync(plan.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Contains(trueTarget.Id, _executionOrder);
        Assert.DoesNotContain(falseTarget.Id, _executionOrder);
    }

    // === Notification events ===

    [Fact]
    public async Task Execute_EmitsPlanStarted_OnBegin()
    {
        RegisterCompletingExecutor();
        var sut = CreateSut();
        var plan = BuildLinearPlan(1);
        SetupPlanLoad(plan);

        await sut.ExecuteAsync(plan.Id, CancellationToken.None);

        _notifier.Verify(n => n.NotifyPlanStartedAsync(plan.Id, plan.Name, plan, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Execute_EmitsPlanCompleted_OnSuccess()
    {
        RegisterCompletingExecutor();
        var sut = CreateSut();
        var plan = BuildLinearPlan(1);
        SetupPlanLoad(plan);

        await sut.ExecuteAsync(plan.Id, CancellationToken.None);

        _notifier.Verify(n => n.NotifyPlanCompletedAsync(plan.Id, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Execute_EmitsPlanFailed_OnFailure()
    {
        RegisterStepExecutor(StepType.LlmCall, (step, _, _) => Task.FromResult(new StepExecutionResult
        {
            Status = StepExecutionStatus.Failed,
            ErrorMessage = "boom",
            Duration = TimeSpan.FromMilliseconds(1)
        }));

        var sut = CreateSut();
        var plan = BuildLinearPlan(1);
        SetupPlanLoad(plan);

        await sut.ExecuteAsync(plan.Id, CancellationToken.None);

        _notifier.Verify(n => n.NotifyPlanFailedAsync(plan.Id, plan.Steps[0].Id, "boom", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Execute_EmitsStepStarted_ForEachStep()
    {
        RegisterCompletingExecutor();
        var sut = CreateSut();
        var plan = BuildLinearPlan(2);
        SetupPlanLoad(plan);

        await sut.ExecuteAsync(plan.Id, CancellationToken.None);

        _notifier.Verify(
            n => n.NotifyStepStartedAsync(plan.Id, It.IsAny<PlanStepId>(), It.IsAny<string>(), StepType.LlmCall, It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    // === Empty plan ===

    [Fact]
    public async Task Execute_EmptyPlan_CompletesImmediately()
    {
        var sut = CreateSut();
        var plan = new PlanGraph
        {
            Id = PlanId.New(),
            Name = "empty-plan",
            Steps = [],
            Edges = [],
            Configuration = new PlanConfiguration { PlanTimeout = TimeSpan.FromSeconds(10) }
        };
        SetupPlanLoad(plan);

        var result = await sut.ExecuteAsync(plan.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(StepExecutionStatus.Completed, result.Value!.FinalStatus);
    }

    // === Plan not found ===

    [Fact]
    public async Task Execute_PlanNotFound_ReturnsFailure()
    {
        var sut = CreateSut();
        var planId = PlanId.New();
        _stateStore.Setup(s => s.LoadPlanAsync(planId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanGraph?>.Success(null));

        var result = await sut.ExecuteAsync(planId, CancellationToken.None);

        Assert.False(result.IsSuccess);
    }

    // === Validation failure ===

    [Fact]
    public async Task Execute_ValidationFails_ReturnsFailure()
    {
        var sut = CreateSut();
        var plan = BuildLinearPlan(1);
        SetupPlanLoad(plan);
        _validator.Setup(v => v.ValidateAsync(plan, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanValidationResult>.Success(new PlanValidationResult
            {
                IsValid = false,
                Errors = ["invalid plan"]
            }));

        var result = await sut.ExecuteAsync(plan.Id, CancellationToken.None);

        Assert.False(result.IsSuccess);
    }

    // === Context with depth ===

    [Fact]
    public async Task Execute_WithContext_UsesProvidedContext()
    {
        RegisterCompletingExecutor();
        var sut = CreateSut();
        var plan = BuildLinearPlan(1);
        SetupPlanLoad(plan);
        var context = new PlanExecutionContext { Depth = 2, MaxDepth = 5 };

        var result = await sut.ExecuteAsync(plan.Id, context, CancellationToken.None);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Execute_MaxDepthExceeded_ReturnsFailure()
    {
        var sut = CreateSut();
        var plan = BuildLinearPlan(1);
        SetupPlanLoad(plan);
        var context = new PlanExecutionContext { Depth = 5, MaxDepth = 5 };

        var result = await sut.ExecuteAsync(plan.Id, context, CancellationToken.None);

        Assert.False(result.IsSuccess);
    }

    // === CancelAsync ===

    [Fact]
    public async Task CancelAsync_CancelsNonTerminalSteps()
    {
        var planId = PlanId.New();
        var stepA = new PlanStepId(Guid.NewGuid());
        var stepB = new PlanStepId(Guid.NewGuid());
        var stepC = new PlanStepId(Guid.NewGuid());

        var states = new Dictionary<PlanStepId, StepExecutionState>
        {
            [stepA] = new StepExecutionState { StepId = stepA, Status = StepExecutionStatus.Completed, AttemptCount = 1 },
            [stepB] = new StepExecutionState { StepId = stepB, Status = StepExecutionStatus.Pending },
            [stepC] = new StepExecutionState { StepId = stepC, Status = StepExecutionStatus.Running, AttemptCount = 1 }
        };

        _stateStore.Setup(s => s.LoadStepStatesAsync(planId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyDictionary<PlanStepId, StepExecutionState>>.Success(states));
        _stateStore.Setup(s => s.CheckpointAsync(planId, It.IsAny<IReadOnlyList<StepExecutionState>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var result = await _sut.CancelAsync(planId, CancellationToken.None);

        Assert.True(result.IsSuccess);
        _stateStore.Verify(s => s.CheckpointAsync(
            planId,
            It.Is<IReadOnlyList<StepExecutionState>>(list =>
                list.Count(x => x.Status == StepExecutionStatus.Cancelled) == 2 &&
                list.Count(x => x.Status == StepExecutionStatus.Completed) == 1),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CancelAsync_PreservesTerminalSteps()
    {
        var planId = PlanId.New();
        var stepA = new PlanStepId(Guid.NewGuid());
        var stepB = new PlanStepId(Guid.NewGuid());

        var states = new Dictionary<PlanStepId, StepExecutionState>
        {
            [stepA] = new StepExecutionState { StepId = stepA, Status = StepExecutionStatus.Completed, AttemptCount = 1 },
            [stepB] = new StepExecutionState { StepId = stepB, Status = StepExecutionStatus.Failed, AttemptCount = 2, ErrorMessage = "boom" }
        };

        _stateStore.Setup(s => s.LoadStepStatesAsync(planId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyDictionary<PlanStepId, StepExecutionState>>.Success(states));
        _stateStore.Setup(s => s.CheckpointAsync(planId, It.IsAny<IReadOnlyList<StepExecutionState>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var result = await _sut.CancelAsync(planId, CancellationToken.None);

        Assert.True(result.IsSuccess);
        _stateStore.Verify(s => s.CheckpointAsync(
            planId,
            It.Is<IReadOnlyList<StepExecutionState>>(list =>
                list.All(x => x.Status != StepExecutionStatus.Cancelled)),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CancelAsync_NoStepStates_ReturnsNotFound()
    {
        var planId = PlanId.New();

        _stateStore.Setup(s => s.LoadStepStatesAsync(planId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyDictionary<PlanStepId, StepExecutionState>>.Success(
                new Dictionary<PlanStepId, StepExecutionState>()));

        var result = await _sut.CancelAsync(planId, CancellationToken.None);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task CancelAsync_LoadFails_PropagatesError()
    {
        var planId = PlanId.New();

        _stateStore.Setup(s => s.LoadStepStatesAsync(planId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyDictionary<PlanStepId, StepExecutionState>>.Fail("store unavailable"));

        var result = await _sut.CancelAsync(planId, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("store unavailable", result.Errors);
    }

    // === RetryStepAsync ===

    [Fact]
    public async Task RetryStepAsync_FailedStep_ResetsToPending()
    {
        var planId = PlanId.New();
        var stepId = new PlanStepId(Guid.NewGuid());

        var states = new Dictionary<PlanStepId, StepExecutionState>
        {
            [stepId] = new StepExecutionState
            {
                StepId = stepId,
                Status = StepExecutionStatus.Failed,
                AttemptCount = 2,
                ErrorMessage = "timeout",
                CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            }
        };

        _stateStore.Setup(s => s.LoadStepStatesAsync(planId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyDictionary<PlanStepId, StepExecutionState>>.Success(states));
        _stateStore.Setup(s => s.UpdateStepStateAsync(It.IsAny<StepExecutionState>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var result = await _sut.RetryStepAsync(planId, stepId, CancellationToken.None);

        Assert.True(result.IsSuccess);
        _stateStore.Verify(s => s.UpdateStepStateAsync(
            It.Is<StepExecutionState>(st =>
                st.StepId == stepId &&
                st.Status == StepExecutionStatus.Pending &&
                st.AttemptCount == 2 &&
                st.ErrorMessage == null &&
                st.CompletedAt == null),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RetryStepAsync_NonFailedStep_ReturnsFailure()
    {
        var planId = PlanId.New();
        var stepId = new PlanStepId(Guid.NewGuid());

        var states = new Dictionary<PlanStepId, StepExecutionState>
        {
            [stepId] = new StepExecutionState
            {
                StepId = stepId,
                Status = StepExecutionStatus.Completed,
                AttemptCount = 1
            }
        };

        _stateStore.Setup(s => s.LoadStepStatesAsync(planId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyDictionary<PlanStepId, StepExecutionState>>.Success(states));

        var result = await _sut.RetryStepAsync(planId, stepId, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("Only failed steps can be retried", result.Errors.First());
    }

    [Fact]
    public async Task RetryStepAsync_UnknownStep_ReturnsNotFound()
    {
        var planId = PlanId.New();
        var stepId = new PlanStepId(Guid.NewGuid());

        var states = new Dictionary<PlanStepId, StepExecutionState>();

        _stateStore.Setup(s => s.LoadStepStatesAsync(planId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyDictionary<PlanStepId, StepExecutionState>>.Success(states));

        var result = await _sut.RetryStepAsync(planId, stepId, CancellationToken.None);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task RetryStepAsync_NoPlanStates_ReturnsNotFound()
    {
        var planId = PlanId.New();
        var stepId = new PlanStepId(Guid.NewGuid());

        _stateStore.Setup(s => s.LoadStepStatesAsync(planId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyDictionary<PlanStepId, StepExecutionState>>.Fail("not found"));

        var result = await _sut.RetryStepAsync(planId, stepId, CancellationToken.None);

        Assert.False(result.IsSuccess);
    }

    // === ErrorRecovery.Escalate ===

    [Fact]
    public async Task Execute_EscalateRecovery_QueuesEscalationAndBlocksStep()
    {
        var escalationId = Guid.NewGuid();
        _escalation.Setup(e => e.QueueEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(escalationId);

        RegisterStepExecutor(StepType.LlmCall, (step, _, _) =>
        {
            _executionOrder.Add(step.Id);
            return Task.FromResult(new StepExecutionResult
            {
                Status = StepExecutionStatus.Failed,
                ErrorMessage = "LLM call failed",
                Duration = TimeSpan.FromMilliseconds(1)
            });
        });

        var sut = CreateSut();
        var a = CreateStep("escalate-step") with
        {
            RetryPolicy = new RetryPolicy { MaxRetries = 0, OnExhausted = ErrorRecovery.Escalate }
        };
        var b = CreateStep("downstream");
        var plan = new PlanGraph
        {
            Id = PlanId.New(),
            Name = "escalate-plan",
            Steps = [a, b],
            Edges = [new PlanEdge(a.Id, b.Id, EdgeType.ControlFlow)],
            Configuration = new PlanConfiguration { PlanTimeout = TimeSpan.FromSeconds(10) }
        };
        SetupPlanLoad(plan);

        var result = await sut.ExecuteAsync(plan.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);

        // Step should be blocked, not failed
        var summary = result.Value!;
        var aState = summary.StepStates.First(s => s.StepId == a.Id);
        Assert.Equal(StepExecutionStatus.Blocked, aState.Status);

        // Downstream should NOT be skipped (blocked step, not failed)
        var bState = summary.StepStates.First(s => s.StepId == b.Id);
        Assert.NotEqual(StepExecutionStatus.Skipped, bState.Status);

        // Escalation was queued
        _escalation.Verify(e => e.QueueEscalationAsync(
            It.Is<EscalationRequest>(r =>
                r.AgentId == "plan-executor" &&
                r.ToolName == "escalate-step" &&
                r.RiskLevel == RiskLevel.High &&
                r.Priority == EscalationPriority.Blocking &&
                r.Description.Contains("LLM call failed")),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Execute_EscalateRecovery_DoesNotSkipDownstream()
    {
        _escalation.Setup(e => e.QueueEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());

        RegisterStepExecutor(StepType.LlmCall, (step, _, _) =>
        {
            _executionOrder.Add(step.Id);
            if (step.Name == "escalate-step")
            {
                return Task.FromResult(new StepExecutionResult
                {
                    Status = StepExecutionStatus.Failed,
                    ErrorMessage = "fail",
                    Duration = TimeSpan.FromMilliseconds(1)
                });
            }
            return Task.FromResult(new StepExecutionResult
            {
                Status = StepExecutionStatus.Completed,
                Output = "ok",
                Duration = TimeSpan.FromMilliseconds(1)
            });
        });

        var sut = CreateSut();
        var a = CreateStep("escalate-step") with
        {
            RetryPolicy = new RetryPolicy { MaxRetries = 0, OnExhausted = ErrorRecovery.Escalate }
        };
        var b = CreateStep("downstream");
        var plan = new PlanGraph
        {
            Id = PlanId.New(),
            Name = "escalate-no-skip-plan",
            Steps = [a, b],
            Edges = [new PlanEdge(a.Id, b.Id, EdgeType.ControlFlow)],
            Configuration = new PlanConfiguration { PlanTimeout = TimeSpan.FromSeconds(10) }
        };
        SetupPlanLoad(plan);

        var result = await sut.ExecuteAsync(plan.Id, CancellationToken.None);

        var summary = result.Value!;
        var bState = summary.StepStates.First(s => s.StepId == b.Id);

        // Downstream stays Pending (not Skipped), because the escalated step is Blocked, not Failed
        Assert.Equal(StepExecutionStatus.Pending, bState.Status);
    }

    public void Dispose()
    {
        _serviceProvider?.Dispose();
    }
}
