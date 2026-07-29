using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Services.Governance;
using Domain.AI.Bundles;
using Domain.AI.Escalation;
using Domain.AI.Governance;
using Domain.AI.Planner;
using Domain.Common;
using Infrastructure.AI.Planner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Planner;

/// <summary>
/// Verifies the scheduler-level autonomy ceiling: when a capability envelope is ambient, a step whose
/// <see cref="PlanStep.RequiredAutonomyLevel"/> exceeds the envelope's
/// <see cref="CapabilityEnvelope.AutonomyCeiling"/> must never reach its executor — it fails through
/// the step's normal OnExhausted recovery. With no envelope (every direct in-process caller) the
/// ceiling does not apply and behavior is unchanged.
/// </summary>
public sealed class PlanExecutorEnvelopeCeilingTests : IDisposable
{
    private readonly Mock<IPlanValidator> _validator = new();
    private readonly Mock<IPlanStateStore> _stateStore = new();
    private readonly Mock<IPlanProgressNotifier> _notifier = new();
    private readonly Mock<IEscalationService> _escalation = new();
    private readonly Mock<IPlanStepExecutor> _stepExecutor = new();
    private readonly ServiceProvider _serviceProvider;
    private readonly PlanExecutor _sut;
    private int _executorInvocations;

    public PlanExecutorEnvelopeCeilingTests()
    {
        _validator.Setup(v => v.ValidateAsync(It.IsAny<PlanGraph>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanValidationResult>.Success(new PlanValidationResult { IsValid = true }));

        _stateStore.Setup(s => s.UpdateStepStateAsync(It.IsAny<StepExecutionState>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        _stateStore.Setup(s => s.ResumeAsync(It.IsAny<PlanId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyDictionary<PlanStepId, StepExecutionState>>.Success(
                new Dictionary<PlanStepId, StepExecutionState>()));

        _stepExecutor
            .Setup(e => e.ExecuteAsync(It.IsAny<PlanStep>(), It.IsAny<IReadOnlyDictionary<PlanStepId, string>>(), It.IsAny<CancellationToken>()))
            .Callback(() => Interlocked.Increment(ref _executorInvocations))
            .ReturnsAsync(new StepExecutionResult
            {
                Status = StepExecutionStatus.Completed,
                Output = "ok",
                Duration = TimeSpan.FromMilliseconds(1)
            });

        var services = new ServiceCollection();
        services.AddKeyedSingleton(StepType.LlmCall, _stepExecutor.Object);
        _serviceProvider = services.BuildServiceProvider();

        _sut = new PlanExecutor(
            _validator.Object,
            _stateStore.Object,
            _notifier.Object,
            _escalation.Object,
            _serviceProvider,
            new PlanRunCancellationRegistry(),
            TimeProvider.System,
            NullLogger<PlanExecutor>.Instance);
    }

    public void Dispose() => _serviceProvider.Dispose();

    private PlanGraph SingleStepPlan(
        AutonomyLevel? requiredAutonomy, ErrorRecovery onExhausted = ErrorRecovery.FailStep)
    {
        var step = new PlanStep
        {
            Id = new PlanStepId(Guid.NewGuid()),
            Name = "governed-step",
            Type = StepType.LlmCall,
            Configuration = new LlmCallConfig { SystemPrompt = "test", ModelDeploymentKey = "gpt-4" },
            RetryPolicy = new RetryPolicy { MaxRetries = 0, OnExhausted = onExhausted },
            RequiredAutonomyLevel = requiredAutonomy
        };

        var plan = new PlanGraph
        {
            Id = PlanId.New(),
            Name = "ceiling-plan",
            Steps = [step],
            Edges = [],
            Configuration = new PlanConfiguration { PlanTimeout = TimeSpan.FromSeconds(10) }
        };

        _stateStore.Setup(s => s.LoadPlanAsync(plan.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanGraph?>.Success(plan));

        return plan;
    }

    [Fact]
    public async Task ExecuteAsync_StepAboveEnvelopeCeiling_FailsWithoutInvokingExecutor()
    {
        var plan = SingleStepPlan(AutonomyLevel.Autonomous);

        Result<PlanExecutionSummary> result;
        using (CapabilityEnvelopeAccessor.Begin(new CapabilityEnvelope { AutonomyCeiling = AutonomyLevel.Supervised }))
        {
            result = await _sut.ExecuteAsync(plan.Id, CancellationToken.None);
        }

        Assert.True(result.IsSuccess);
        Assert.Equal(StepExecutionStatus.Failed, result.Value!.FinalStatus);
        var stepState = Assert.Single(result.Value.StepStates);
        Assert.Contains("autonomy", stepState.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, _executorInvocations);
    }

    [Fact]
    public async Task ExecuteAsync_StepAtEnvelopeCeiling_Executes()
    {
        var plan = SingleStepPlan(AutonomyLevel.Supervised);

        Result<PlanExecutionSummary> result;
        using (CapabilityEnvelopeAccessor.Begin(new CapabilityEnvelope { AutonomyCeiling = AutonomyLevel.Supervised }))
        {
            result = await _sut.ExecuteAsync(plan.Id, CancellationToken.None);
        }

        Assert.True(result.IsSuccess);
        Assert.Equal(StepExecutionStatus.Completed, result.Value!.FinalStatus);
        Assert.Equal(1, _executorInvocations);
    }

    [Fact]
    public async Task ExecuteAsync_NoRequiredAutonomy_RunsUnderMostRestrictiveCeiling()
    {
        var plan = SingleStepPlan(requiredAutonomy: null);

        Result<PlanExecutionSummary> result;
        using (CapabilityEnvelopeAccessor.Begin(new CapabilityEnvelope { AutonomyCeiling = AutonomyLevel.Restricted }))
        {
            result = await _sut.ExecuteAsync(plan.Id, CancellationToken.None);
        }

        Assert.True(result.IsSuccess);
        Assert.Equal(StepExecutionStatus.Completed, result.Value!.FinalStatus);
        Assert.Equal(1, _executorInvocations);
    }

    [Theory]
    [InlineData(ErrorRecovery.SkipStep)]
    [InlineData(ErrorRecovery.Escalate)]
    public async Task ExecuteAsync_CeilingDenial_IsTerminalRegardlessOfPlanAuthoredRecovery(ErrorRecovery recovery)
    {
        // MED-1: RetryPolicy.OnExhausted is plan-authored data. A plan must not be able to choose the
        // disposition of the check that constrains it — SkipStep would have marked the step Skipped,
        // leaving no Failed state and reporting the run Completed with the denial silently dropped;
        // Escalate would have queued an un-actionable approval and looped on approve → re-run → deny.
        var plan = SingleStepPlan(AutonomyLevel.Autonomous, recovery);

        Result<PlanExecutionSummary> result;
        using (CapabilityEnvelopeAccessor.Begin(new CapabilityEnvelope { AutonomyCeiling = AutonomyLevel.Restricted }))
        {
            result = await _sut.ExecuteAsync(plan.Id, CancellationToken.None);
        }

        Assert.True(result.IsSuccess);
        Assert.Equal(StepExecutionStatus.Failed, result.Value!.FinalStatus);
        Assert.Equal(StepExecutionStatus.Failed, Assert.Single(result.Value.StepStates).Status);
        Assert.Equal(0, _executorInvocations);
        _escalation.Verify(
            e => e.QueueEscalationAsync(It.IsAny<Domain.AI.Escalation.EscalationRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_StepExecutorPolicyDenial_IsTerminalUnderSkipStep()
    {
        // The same guarantee for a denial raised inside a step executor (the governor refusing a tool,
        // retrieval, or inference call) rather than by the scheduler's ceiling check.
        var plan = SingleStepPlan(requiredAutonomy: null, ErrorRecovery.SkipStep);
        _stepExecutor
            .Setup(e => e.ExecuteAsync(It.IsAny<PlanStep>(), It.IsAny<IReadOnlyDictionary<PlanStepId, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StepExecutionResult
            {
                Status = StepExecutionStatus.Failed,
                ErrorMessage = GovernanceDenials.NotPermitted(PlanCapabilities.LlmCall),
                Duration = TimeSpan.Zero,
                IsPolicyDenial = true
            });

        var result = await _sut.ExecuteAsync(plan.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(StepExecutionStatus.Failed, result.Value!.FinalStatus);
        Assert.Equal(StepExecutionStatus.Failed, Assert.Single(result.Value.StepStates).Status);
    }

    [Fact]
    public async Task ExecuteAsync_PolicyDenial_IsNotRetried()
    {
        // Retrying cannot change the envelope's answer; spending the retry budget on it only delays
        // the denial and multiplies audit noise.
        var plan = SingleStepPlan(requiredAutonomy: null);
        var attempts = 0;
        _stepExecutor
            .Setup(e => e.ExecuteAsync(It.IsAny<PlanStep>(), It.IsAny<IReadOnlyDictionary<PlanStepId, string>>(), It.IsAny<CancellationToken>()))
            .Callback(() => Interlocked.Increment(ref attempts))
            .ReturnsAsync(new StepExecutionResult
            {
                Status = StepExecutionStatus.Failed,
                ErrorMessage = GovernanceDenials.NotPermitted("file_system"),
                Duration = TimeSpan.Zero,
                IsPolicyDenial = true
            });

        await _sut.ExecuteAsync(plan.Id, CancellationToken.None);

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_NoEnvelope_CeilingDoesNotApply()
    {
        // Absent-envelope posture: direct in-process callers see today's behavior unchanged.
        var plan = SingleStepPlan(AutonomyLevel.Autonomous);

        var result = await _sut.ExecuteAsync(plan.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(StepExecutionStatus.Completed, result.Value!.FinalStatus);
        Assert.Equal(1, _executorInvocations);
    }
}
