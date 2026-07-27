using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Planner;
using Domain.AI.Planner;
using Domain.Common;
using Infrastructure.AI.Planner.StepExecutors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Planner.StepExecutors;

public sealed class SubPlanStepExecutorTests
{
    private readonly Mock<IPlanStateStore> _planStateStore = new();
    private readonly Mock<IPlanProgressNotifier> _notifier = new();
    private readonly Mock<IPlanExecutor> _childExecutor = new();
    private readonly Mock<IAgentExecutionContext> _parentAgentContext = new();
    private readonly PlanExecutionContext _context = new() { Depth = 0, MaxDepth = 3, CurrentPlanId = new PlanId(Guid.NewGuid()) };
    private readonly SubPlanStepExecutor _sut;

    public SubPlanStepExecutorTests()
    {
        _notifier.Setup(n => n.NotifyStepStartedAsync(
            It.IsAny<PlanId>(), It.IsAny<PlanStepId>(), It.IsAny<string>(), It.IsAny<StepType>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddSingleton<IPlanExecutor>(_childExecutor.Object);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        _sut = new SubPlanStepExecutor(
            scopeFactory,
            _planStateStore.Object,
            _notifier.Object,
            _context,
            _parentAgentContext.Object,
            NullLogger<SubPlanStepExecutor>.Instance);
    }

    private static PlanStep CreateStep(SubPlanConfig config) => new()
    {
        Id = new PlanStepId(Guid.NewGuid()),
        Name = "sub-plan-step",
        Type = StepType.SubPlanInvocation,
        Configuration = config,
        RetryPolicy = new RetryPolicy()
    };

    [Fact]
    public async Task ExecuteAsync_InvalidConfig_ReturnsFailed()
    {
        var step = new PlanStep
        {
            Id = new PlanStepId(Guid.NewGuid()),
            Name = "bad",
            Type = StepType.SubPlanInvocation,
            Configuration = new LlmCallConfig { SystemPrompt = "x", ModelDeploymentKey = "y" },
            RetryPolicy = new RetryPolicy()
        };

        var result = await _sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Failed, result.Status);
        Assert.Contains("invalid configuration type", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_DepthExceeded_ReturnsFailed()
    {
        var deepContext = new PlanExecutionContext { Depth = 5, MaxDepth = 3, CurrentPlanId = new PlanId(Guid.NewGuid()) };
        var services = new ServiceCollection();
        services.AddSingleton<IPlanExecutor>(_childExecutor.Object);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var sut = new SubPlanStepExecutor(
            scopeFactory,
            _planStateStore.Object,
            _notifier.Object,
            deepContext,
            _parentAgentContext.Object,
            NullLogger<SubPlanStepExecutor>.Instance);

        var config = new SubPlanConfig { ChildPlanId = new PlanId(Guid.NewGuid()) };
        var step = CreateStep(config);

        var result = await sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Failed, result.Status);
        Assert.Contains("depth", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_NoChildPlanIdOrInline_ReturnsFailed()
    {
        var config = new SubPlanConfig { ChildPlanId = null, InlinePlanDefinition = null };
        var step = CreateStep(config);

        var result = await _sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Failed, result.Status);
        Assert.Contains("Could not resolve child plan", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_WithChildPlanId_ExecutesChildPlan()
    {
        var childPlanId = new PlanId(Guid.NewGuid());
        var config = new SubPlanConfig { ChildPlanId = childPlanId };
        var step = CreateStep(config);

        _childExecutor.Setup(e => e.ExecuteAsync(childPlanId, It.IsAny<PlanExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanExecutionSummary>.Success(new PlanExecutionSummary
            {
                PlanId = childPlanId,
                FinalStatus = StepExecutionStatus.Completed,
                TotalDuration = TimeSpan.FromSeconds(5),
                StepStates = []
            }));

        var result = await _sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Completed, result.Status);
        Assert.NotNull(result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_ChildPlanFails_ReturnsFailed()
    {
        var childPlanId = new PlanId(Guid.NewGuid());
        var config = new SubPlanConfig { ChildPlanId = childPlanId };
        var step = CreateStep(config);

        _childExecutor.Setup(e => e.ExecuteAsync(childPlanId, It.IsAny<PlanExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanExecutionSummary>.Fail("Child step crashed"));

        var result = await _sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Failed, result.Status);
        Assert.Contains("Child step crashed", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_ParentIdentity_PropagatedIntoChildScope()
    {
        // Governance identity is DI-scoped, so it does not cross into the child's fresh scope by
        // itself — the executor must re-stamp it or the governor fails closed on every enveloped
        // tool call inside the sub-plan, granted or not.
        _parentAgentContext.SetupGet(c => c.AgentId).Returns("caller-agent");
        _parentAgentContext.SetupGet(c => c.ConversationId).Returns("conv-1");
        _parentAgentContext.SetupGet(c => c.TurnNumber).Returns(3);

        var (sut, childAgentContext, childPlanId) = BuildExecutorWithChildScope();

        var result = await sut.ExecuteAsync(
            CreateStep(new SubPlanConfig { ChildPlanId = childPlanId }),
            new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Completed, result.Status);
        childAgentContext.Verify(c => c.Initialize("caller-agent", "conv-1", 3), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_NoParentIdentity_ChildContextUntouched()
    {
        // An ungoverned direct run carries no identity; the child must not be stamped with one —
        // its behavior stays exactly as before envelope confinement existed.
        var (sut, childAgentContext, childPlanId) = BuildExecutorWithChildScope();

        await sut.ExecuteAsync(
            CreateStep(new SubPlanConfig { ChildPlanId = childPlanId }),
            new Dictionary<PlanStepId, string>(), CancellationToken.None);

        childAgentContext.Verify(
            c => c.Initialize(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    /// <summary>
    /// Builds an executor whose child scope exposes its own <see cref="IAgentExecutionContext"/>, so a
    /// test can assert what the parent did or did not stamp onto it. Returns the executor, the child
    /// scope's context mock, and the child plan id already wired to succeed.
    /// </summary>
    private (SubPlanStepExecutor Sut, Mock<IAgentExecutionContext> ChildContext, PlanId ChildPlanId)
        BuildExecutorWithChildScope()
    {
        var childAgentContext = new Mock<IAgentExecutionContext>();
        var childPlanId = new PlanId(Guid.NewGuid());

        var services = new ServiceCollection();
        services.AddSingleton<IPlanExecutor>(_childExecutor.Object);
        services.AddSingleton(childAgentContext.Object);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        SetupChildSuccess(childPlanId);

        var sut = new SubPlanStepExecutor(
            scopeFactory,
            _planStateStore.Object,
            _notifier.Object,
            _context,
            _parentAgentContext.Object,
            NullLogger<SubPlanStepExecutor>.Instance);

        return (sut, childAgentContext, childPlanId);
    }

    private void SetupChildSuccess(PlanId childPlanId) =>
        _childExecutor.Setup(e => e.ExecuteAsync(childPlanId, It.IsAny<PlanExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanExecutionSummary>.Success(new PlanExecutionSummary
            {
                PlanId = childPlanId,
                FinalStatus = StepExecutionStatus.Completed,
                TotalDuration = TimeSpan.Zero,
                StepStates = []
            }));

    [Fact]
    public async Task ExecuteAsync_ChildPlanThrows_ReturnsFailed()
    {
        var childPlanId = new PlanId(Guid.NewGuid());
        var config = new SubPlanConfig { ChildPlanId = childPlanId };
        var step = CreateStep(config);

        _childExecutor.Setup(e => e.ExecuteAsync(childPlanId, It.IsAny<PlanExecutionContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DataSource=/secret/plans.db;Password=hunter2"));

        var result = await _sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Failed, result.Status);
        // MED-2: step error state is persisted and returned to callers, so the raw exception text —
        // here an EF Core connection string — must never reach it.
        Assert.Equal(PlanStepErrors.SubPlanFailed, result.ErrorMessage);
        Assert.DoesNotContain("hunter2", result.ErrorMessage);
    }
}
