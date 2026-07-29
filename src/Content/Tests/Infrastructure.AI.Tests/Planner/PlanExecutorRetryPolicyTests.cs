using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Planner;
using Domain.AI.Planner;
using Domain.Common;
using Infrastructure.AI.Planner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Planner;

/// <summary>
/// Covers automatic retry of failed plan steps per <see cref="RetryPolicy"/>: budget consumption,
/// backoff computation and scheduling on the injected <see cref="TimeProvider"/>, exhaustion
/// routing into <see cref="RetryPolicy.OnExhausted"/>, timeout/exception retryability,
/// cancellation during backoff, and the exclusion of Blocked (human gate) results from retry.
/// </summary>
public sealed class PlanExecutorRetryPolicyTests : IDisposable
{
    private readonly Mock<IPlanValidator> _validator = new();
    private readonly Mock<IPlanStateStore> _stateStore = new();
    private readonly Mock<IPlanProgressNotifier> _notifier = new();
    private readonly Mock<IEscalationService> _escalation = new();
    private readonly ServiceCollection _services = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero));
    private ServiceProvider? _serviceProvider;

    public PlanExecutorRetryPolicyTests()
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
    }

    public void Dispose() => _serviceProvider?.Dispose();

    // === Retry budget & OnExhausted ===

    [Fact]
    public async Task Execute_FailingStep_RetriedUpToMaxRetriesThenOnExhaustedFires()
    {
        var flaky = Step("flaky", new RetryPolicy
        {
            MaxRetries = 2,
            InitialDelay = TimeSpan.Zero,
            Strategy = BackoffStrategy.Fixed,
            OnExhausted = ErrorRecovery.SkipStep
        });
        var down = Step("down", NoRetry());
        var plan = LinearPlan(flaky, down);
        SetupPlanLoad(plan);

        var flakyInvocations = 0;
        var downInvocations = 0;
        RegisterExecutor(StepType.LlmCall, (s, _, _) =>
        {
            if (s.Id == flaky.Id)
            {
                Interlocked.Increment(ref flakyInvocations);
                return Task.FromResult(FailedResult());
            }

            Interlocked.Increment(ref downInvocations);
            return Task.FromResult(CompletedResult());
        });
        var sut = CreateSut();

        var result = await sut.ExecuteAsync(plan.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, flakyInvocations); // 1 initial attempt + MaxRetries(2) retries
        var flakyState = result.Value!.StepStates.Single(s => s.StepId == flaky.Id);
        // OnExhausted (SkipStep) fired exactly once, only after the budget was spent.
        Assert.Equal(StepExecutionStatus.Skipped, flakyState.Status);
        Assert.Equal(3, flakyState.AttemptCount);
        Assert.Equal(1, downInvocations); // SkipStep released the downstream step
    }

    [Fact]
    public async Task Execute_FailsOnceThenSucceeds_CompletedWithAttemptCountTwo()
    {
        var step = Step("recovers", new RetryPolicy { MaxRetries = 3, InitialDelay = TimeSpan.Zero });
        var plan = SinglePlan(step);
        SetupPlanLoad(plan);

        var invocations = 0;
        RegisterExecutor(StepType.LlmCall, (_, _, _) =>
        {
            var n = Interlocked.Increment(ref invocations);
            return Task.FromResult(n == 1 ? FailedResult() : CompletedResult("second-time-lucky"));
        });
        var sut = CreateSut();

        var result = await sut.ExecuteAsync(plan.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, invocations);
        var state = result.Value!.StepStates.Single();
        Assert.Equal(StepExecutionStatus.Completed, state.Status);
        Assert.Equal(2, state.AttemptCount);
        Assert.Equal("second-time-lucky", state.Output);
    }

    [Fact]
    public async Task Execute_MaxRetriesZero_SingleAttemptThenImmediateOnExhausted()
    {
        var failing = Step("fails", NoRetry()); // OnExhausted defaults to FailStep
        var down = Step("down", NoRetry());
        var plan = LinearPlan(failing, down);
        SetupPlanLoad(plan);

        var invocations = 0;
        RegisterExecutor(StepType.LlmCall, (s, _, _) =>
        {
            if (s.Id == failing.Id)
            {
                Interlocked.Increment(ref invocations);
                return Task.FromResult(FailedResult());
            }

            return Task.FromResult(CompletedResult());
        });
        var sut = CreateSut();

        var result = await sut.ExecuteAsync(plan.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, invocations);
        var failingState = result.Value!.StepStates.Single(s => s.StepId == failing.Id);
        Assert.Equal(StepExecutionStatus.Failed, failingState.Status);
        Assert.Equal(1, failingState.AttemptCount);
        // FailStep skipped the downstream subgraph — no retry could have resurrected it.
        var downState = result.Value.StepStates.Single(s => s.StepId == down.Id);
        Assert.Equal(StepExecutionStatus.Skipped, downState.Status);
    }

    [Fact]
    public async Task Execute_ExecutorThrows_ExceptionCountsAsRetryableAttempt()
    {
        var step = Step("throws-once", new RetryPolicy { MaxRetries = 1, InitialDelay = TimeSpan.Zero });
        var plan = SinglePlan(step);
        SetupPlanLoad(plan);

        var invocations = 0;
        RegisterExecutor(StepType.LlmCall, (_, _, _) =>
        {
            var n = Interlocked.Increment(ref invocations);
            if (n == 1)
                throw new InvalidOperationException("transient explosion");
            return Task.FromResult(CompletedResult());
        });
        var sut = CreateSut();

        var result = await sut.ExecuteAsync(plan.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, invocations);
        var state = result.Value!.StepStates.Single();
        Assert.Equal(StepExecutionStatus.Completed, state.Status);
        Assert.Equal(2, state.AttemptCount);
    }

    // === Timeout interaction ===

    [Fact]
    public async Task Execute_AttemptTimesOut_TimeoutCountsAsRetryableAttempt()
    {
        // Per-attempt timeout: the first attempt hangs and is cancelled at 200 ms; the retry gets
        // the full timeout budget again and completes.
        var step = Step("timeouty", new RetryPolicy { MaxRetries = 1, InitialDelay = TimeSpan.Zero },
            timeout: TimeSpan.FromMilliseconds(200));
        var plan = SinglePlan(step);
        SetupPlanLoad(plan);

        var invocations = 0;
        RegisterExecutor(StepType.LlmCall, async (_, _, ct) =>
        {
            var n = Interlocked.Increment(ref invocations);
            if (n == 1)
                await Task.Delay(Timeout.InfiniteTimeSpan, ct); // hangs until the per-attempt timeout fires
            return CompletedResult();
        });
        var sut = CreateSut();

        var result = await sut.ExecuteAsync(plan.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, invocations);
        var state = result.Value!.StepStates.Single();
        Assert.Equal(StepExecutionStatus.Completed, state.Status);
        Assert.Equal(2, state.AttemptCount);
    }

    // === Blocked (human gate) is not a failure ===

    [Fact]
    public async Task Execute_HumanGateBlocked_NotRetried()
    {
        var gate = Step("gate", new RetryPolicy { MaxRetries = 3, InitialDelay = TimeSpan.Zero }, StepType.HumanGate);
        var plan = SinglePlan(gate);
        SetupPlanLoad(plan);

        var invocations = 0;
        RegisterExecutor(StepType.HumanGate, (_, _, _) =>
        {
            Interlocked.Increment(ref invocations);
            return Task.FromResult(new StepExecutionResult
            {
                Status = StepExecutionStatus.Blocked,
                Output = $$"""{"escalationId":"{{Guid.NewGuid()}}"}""",
                Duration = TimeSpan.FromMilliseconds(1)
            });
        });
        var sut = CreateSut();

        var result = await sut.ExecuteAsync(plan.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, invocations); // Blocked is a park, never a retryable failure
        var state = result.Value!.StepStates.Single();
        Assert.Equal(StepExecutionStatus.Blocked, state.Status);
        Assert.Equal(1, state.AttemptCount);
    }

    // === Backoff computation (pure) ===

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    public void ComputeBackoffDelay_Exponential_DoublesPerAttempt(int attemptsMade, int expectedSeconds)
    {
        var policy = new RetryPolicy { InitialDelay = TimeSpan.FromSeconds(1), Strategy = BackoffStrategy.Exponential };

        var delay = PlanExecutor.ComputeBackoffDelay(policy, attemptsMade);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    public void ComputeBackoffDelay_Linear_ScalesWithAttemptNumber(int attemptsMade, int expectedSeconds)
    {
        var policy = new RetryPolicy { InitialDelay = TimeSpan.FromSeconds(1), Strategy = BackoffStrategy.Linear };

        var delay = PlanExecutor.ComputeBackoffDelay(policy, attemptsMade);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Fact]
    public void ComputeBackoffDelay_Fixed_ConstantAcrossAttempts()
    {
        var policy = new RetryPolicy { InitialDelay = TimeSpan.FromSeconds(7), Strategy = BackoffStrategy.Fixed };

        Assert.Equal(TimeSpan.FromSeconds(7), PlanExecutor.ComputeBackoffDelay(policy, 1));
        Assert.Equal(TimeSpan.FromSeconds(7), PlanExecutor.ComputeBackoffDelay(policy, 5));
    }

    [Fact]
    public void ComputeBackoffDelay_Exponential_ExponentCappedToAvoidOverflow()
    {
        var policy = new RetryPolicy { InitialDelay = TimeSpan.FromMilliseconds(1), Strategy = BackoffStrategy.Exponential };

        // Attempt 100 would be 2^99 without the cap; the ladder plateaus at 2^20.
        var delay = PlanExecutor.ComputeBackoffDelay(policy, 100);

        Assert.Equal(TimeSpan.FromMilliseconds(1) * Math.Pow(2, 20), delay);
    }

    // === Backoff scheduling on the TimeProvider ===

    [Fact]
    public async Task Execute_RetryWaitsBackoffDelay_OnInjectedTimeProvider()
    {
        var step = Step("slow-retry", new RetryPolicy
        {
            MaxRetries = 1,
            InitialDelay = TimeSpan.FromMinutes(5),
            Strategy = BackoffStrategy.Fixed
        });
        var plan = SinglePlan(step, planTimeout: TimeSpan.FromMinutes(30));
        SetupPlanLoad(plan);

        var invocations = 0;
        RegisterExecutor(StepType.LlmCall, (_, _, _) =>
        {
            var n = Interlocked.Increment(ref invocations);
            return Task.FromResult(n == 1 ? FailedResult() : CompletedResult());
        });
        var sut = CreateSut();

        var task = sut.ExecuteAsync(plan.Id, CancellationToken.None);
        await WaitUntilAsync(() => Volatile.Read(ref invocations) == 1);
        await Task.Delay(100); // real yield: let the retry loop reach the backoff await

        // Clock frozen — the retry must not run.
        Assert.Equal(1, Volatile.Read(ref invocations));
        Assert.False(task.IsCompleted);

        // Advance fake time in 1-minute steps; the retry may fire no earlier than the 5-minute
        // backoff, so at least 5 advances are required.
        var advanced = TimeSpan.Zero;
        while (Volatile.Read(ref invocations) < 2 && advanced < TimeSpan.FromMinutes(20))
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            advanced += TimeSpan.FromMinutes(1);
            await Task.Delay(10);
        }

        Assert.Equal(2, Volatile.Read(ref invocations));
        Assert.True(advanced >= TimeSpan.FromMinutes(5), $"retry fired after only {advanced} of fake time");

        var result = await task;
        Assert.Equal(StepExecutionStatus.Completed, result.Value!.StepStates.Single().Status);
    }

    [Fact]
    public async Task Execute_CancelledDuringBackoffDelay_AbortsWithoutFurtherAttempts()
    {
        var step = Step("cancelled-mid-backoff", new RetryPolicy
        {
            MaxRetries = 5,
            InitialDelay = TimeSpan.FromHours(1),
            Strategy = BackoffStrategy.Fixed
        });
        var plan = SinglePlan(step, planTimeout: TimeSpan.FromHours(2));
        SetupPlanLoad(plan);

        var invocations = 0;
        RegisterExecutor(StepType.LlmCall, (_, _, _) =>
        {
            Interlocked.Increment(ref invocations);
            return Task.FromResult(FailedResult());
        });
        var sut = CreateSut();

        using var cts = new CancellationTokenSource();
        var task = sut.ExecuteAsync(plan.Id, cts.Token);
        await WaitUntilAsync(() => Volatile.Read(ref invocations) == 1);
        await Task.Delay(100); // real yield: let the retry loop reach the backoff await

        cts.Cancel();

        // Cancellation must abort the (1-hour, fake-clock) backoff promptly without a new attempt.
        var result = await task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, Volatile.Read(ref invocations));
        var state = result.Value!.StepStates.Single();
        Assert.Equal(StepExecutionStatus.Failed, state.Status);
        Assert.Equal(1, state.AttemptCount);
        Assert.Equal("Cancelled", state.ErrorMessage);
    }

    // === Harness ===

    private PlanExecutor CreateSut()
    {
        _serviceProvider = _services.BuildServiceProvider();
        return new PlanExecutor(
            _validator.Object,
            _stateStore.Object,
            _notifier.Object,
            _escalation.Object,
            _serviceProvider,
            new PlanRunCancellationRegistry(),
            _time,
            NullLogger<PlanExecutor>.Instance);
    }

    private void RegisterExecutor(
        StepType type,
        Func<PlanStep, IReadOnlyDictionary<PlanStepId, string>, CancellationToken, Task<StepExecutionResult>> handler)
    {
        var mock = new Mock<IPlanStepExecutor>();
        mock.Setup(e => e.ExecuteAsync(It.IsAny<PlanStep>(), It.IsAny<IReadOnlyDictionary<PlanStepId, string>>(), It.IsAny<CancellationToken>()))
            .Returns<PlanStep, IReadOnlyDictionary<PlanStepId, string>, CancellationToken>(handler);
        _services.AddKeyedSingleton<IPlanStepExecutor>(type, mock.Object);
    }

    private void SetupPlanLoad(PlanGraph plan)
        => _stateStore.Setup(s => s.LoadPlanAsync(plan.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanGraph?>.Success(plan));

    private static RetryPolicy NoRetry() => new() { MaxRetries = 0, InitialDelay = TimeSpan.Zero };

    private static PlanStep Step(string name, RetryPolicy retry, StepType type = StepType.LlmCall, TimeSpan? timeout = null) => new()
    {
        Id = PlanStepId.New(),
        Name = name,
        Type = type,
        Configuration = type == StepType.HumanGate
            ? new HumanGateConfig
            {
                EscalationMessage = "approve me",
                ApprovalStrategy = ApprovalStrategy.AnyOf,
                Approvers = ["supervisor"]
            }
            : new LlmCallConfig { SystemPrompt = "test", ModelDeploymentKey = "gpt-4" },
        RetryPolicy = retry,
        Timeout = timeout ?? TimeSpan.FromSeconds(30)
    };

    private static PlanGraph SinglePlan(PlanStep step, TimeSpan? planTimeout = null) => new()
    {
        Id = PlanId.New(),
        Name = "retry-plan",
        Steps = [step],
        Edges = [],
        Configuration = new PlanConfiguration { PlanTimeout = planTimeout ?? TimeSpan.FromSeconds(30) }
    };

    private static PlanGraph LinearPlan(PlanStep first, PlanStep second) => new()
    {
        Id = PlanId.New(),
        Name = "retry-linear-plan",
        Steps = [first, second],
        Edges = [new PlanEdge(first.Id, second.Id, EdgeType.ControlFlow)],
        Configuration = new PlanConfiguration { PlanTimeout = TimeSpan.FromSeconds(30) }
    };

    private static StepExecutionResult CompletedResult(string output = "ok") => new()
    {
        Status = StepExecutionStatus.Completed,
        Output = output,
        Duration = TimeSpan.FromMilliseconds(1)
    };

    private static StepExecutionResult FailedResult(string error = "deliberate failure") => new()
    {
        Status = StepExecutionStatus.Failed,
        ErrorMessage = error,
        Duration = TimeSpan.FromMilliseconds(1)
    };

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        for (var elapsed = 0; !condition(); elapsed += 10)
        {
            Assert.True(elapsed < timeoutMs, "condition not reached within timeout");
            await Task.Delay(10);
        }
    }
}
