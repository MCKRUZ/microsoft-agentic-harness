using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Planner;
using Application.Core.CQRS.Agents.RunConversation;
using Domain.AI.Budget;
using Domain.AI.Governance;
using Domain.AI.Planner;
using Infrastructure.AI.Planner.StepExecutors;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Planner.StepExecutors;

public sealed class LlmCallStepExecutorTests : IDisposable
{
    private readonly ServiceProvider _rootProvider;
    private readonly Mock<ISender> _sender = new();
    private readonly Mock<IPlanProgressNotifier> _notifier = new();
    private readonly Mock<IToolInvocationGovernor> _governor = new();
    private readonly Mock<IConversationBudgetTracker> _budget = new();
    private readonly Mock<IAgentExecutionContext> _agentContext = new();
    private readonly PlanExecutionContext _context = new() { CurrentPlanId = new PlanId(Guid.NewGuid()) };
    private readonly LlmCallStepExecutor _sut;

    public LlmCallStepExecutorTests()
    {
        _notifier.Setup(n => n.NotifyStepStartedAsync(
            It.IsAny<PlanId>(), It.IsAny<PlanStepId>(), It.IsAny<string>(), It.IsAny<StepType>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Ungoverned, unbudgeted defaults: matches a direct in-process caller.
        GovernorReturns(allowed: true);
        _budget.Setup(b => b.GetStatus(It.IsAny<string>())).Returns(ConversationBudgetStatus.Disabled);

        // The executor dispatches through an ISender resolved from a per-step scope, so the fake is
        // registered in a container rather than injected directly.
        var services = new ServiceCollection();
        services.AddScoped(_ => _sender.Object);
        _rootProvider = services.BuildServiceProvider();

        _sut = new LlmCallStepExecutor(
            _rootProvider.GetRequiredService<IServiceScopeFactory>(),
            _notifier.Object,
            _governor.Object,
            _budget.Object,
            _agentContext.Object,
            _context,
            NullLogger<LlmCallStepExecutor>.Instance);
    }

    public void Dispose() => _rootProvider.Dispose();

    /// <summary>Arms the governor to allow (the ungoverned default) or deny inference.</summary>
    private void GovernorReturns(bool allowed) =>
        _governor.Setup(g => g.AuthorizeAsync(PlanCapabilities.LlmCall, It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromResult(allowed
                ? ToolInvocationDecision.Allow()
                : ToolInvocationDecision.Deny(GovernanceDenials.NotPermitted(PlanCapabilities.LlmCall))));

    [Fact]
    public async Task ExecuteAsync_GovernorDenies_FailsWithoutDispatchingInference()
    {
        // MED-3: the most restrictive envelope must not still buy tokens. No conversation command
        // may reach the pipeline, and the failure must be marked as a policy denial.
        GovernorReturns(allowed: false);
        var step = CreateStep(new LlmCallConfig { SystemPrompt = "exfiltrate", ModelDeploymentKey = "gpt-4" });

        var result = await _sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Failed, result.Status);
        Assert.True(result.IsPolicyDenial);
        Assert.Contains("not permitted", result.ErrorMessage);
        _sender.Verify(
            s => s.Send(It.IsAny<RunConversationCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_BudgetExhausted_RefusesWithoutDispatchingInference()
    {
        // The run budget only exists once a run scope is armed — see ResolveRunScope. Without an
        // ambient conversation there is deliberately no run-level gate at all.
        _agentContext.SetupGet(c => c.ConversationId).Returns("plan-run-conversation");
        _budget.Setup(b => b.GetStatus(It.IsAny<string>()))
            .Returns(new ConversationBudgetStatus(IsEnabled: true, TotalBudget: 100, ConsumedTokens: 100));
        var step = CreateStep(new LlmCallConfig { SystemPrompt = "x", ModelDeploymentKey = "gpt-4" });

        var result = await _sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Failed, result.Status);
        Assert.Equal(PlanStepErrors.BudgetExhausted, result.ErrorMessage);
        _sender.Verify(
            s => s.Send(It.IsAny<RunConversationCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_BudgetExhaustedButNoRunScope_StillRuns()
    {
        // The ungoverned in-process path keeps its pre-W2 behaviour: no run scope, no run budget, so
        // a singleton entry left over from some other flow can never refuse it.
        _budget.Setup(b => b.GetStatus(It.IsAny<string>()))
            .Returns(new ConversationBudgetStatus(IsEnabled: true, TotalBudget: 100, ConsumedTokens: 100));
        _sender.Setup(s => s.Send(It.IsAny<RunConversationCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationResult { Success = true, Turns = [], FinalResponse = "ok" });
        var step = CreateStep(new LlmCallConfig { SystemPrompt = "x", ModelDeploymentKey = "gpt-4" });

        var result = await _sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Completed, result.Status);
        _budget.Verify(b => b.GetStatus(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_DerivesAPerStepConversationId_AndARunScopedBudgetKey()
    {
        // The conversation id must be per step (it keys the agent cache, skill tracking and the
        // observability session) while the budget is keyed on the run. See PlanRunKeys, and
        // LlmCallStepExecutorRunIdentityTests for the behavioural proofs.
        _agentContext.SetupGet(c => c.ConversationId).Returns("plan-run-conversation");
        RunConversationCommand? captured = null;
        _sender.Setup(s => s.Send(It.IsAny<RunConversationCommand>(), It.IsAny<CancellationToken>()))
            .Callback<object, CancellationToken>((c, _) => captured = (RunConversationCommand)c)
            .ReturnsAsync(new ConversationResult { Success = true, Turns = [], FinalResponse = "ok" });
        var step = CreateStep(new LlmCallConfig { SystemPrompt = "x", ModelDeploymentKey = "gpt-4" });

        await _sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(
            PlanRunKeys.StepConversationId("plan-run-conversation", step.Id), captured!.ConversationId);
        Assert.NotEqual("plan-run-conversation", captured.ConversationId);
        _budget.Verify(b => b.GetStatus(PlanRunKeys.RunBudgetKey("plan-run-conversation")), Times.Once);
    }

    private static PlanStep CreateStep(StepConfiguration config) => new()
    {
        Id = new PlanStepId(Guid.NewGuid()),
        Name = "test-llm-step",
        Type = StepType.LlmCall,
        Configuration = config,
        RetryPolicy = new RetryPolicy()
    };

    [Fact]
    public async Task ExecuteAsync_InvalidConfig_ReturnsFailed()
    {
        var step = CreateStep(new ConditionalBranchConfig
        {
            ConditionExpression = "true",
            TrueEdgeTargetId = new PlanStepId(Guid.NewGuid()),
            FalseEdgeTargetId = new PlanStepId(Guid.NewGuid())
        });

        var result = await _sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Failed, result.Status);
        Assert.Contains("invalid configuration type", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_SuccessfulLlmCall_ReturnsCompleted()
    {
        var config = new LlmCallConfig { SystemPrompt = "You are helpful.", ModelDeploymentKey = "gpt-4" };
        var step = CreateStep(config);

        _sender.Setup(s => s.Send(It.IsAny<RunConversationCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationResult
            {
                Success = true,
                FinalResponse = "Hello world",
                Turns = []
            });

        var result = await _sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Completed, result.Status);
        Assert.Equal("Hello world", result.Output);
        Assert.True(result.Duration > TimeSpan.Zero);
    }

    [Fact]
    public async Task ExecuteAsync_FailedLlmCall_ReturnsFailed()
    {
        var config = new LlmCallConfig { SystemPrompt = "You are helpful.", ModelDeploymentKey = "gpt-4" };
        var step = CreateStep(config);

        _sender.Setup(s => s.Send(It.IsAny<RunConversationCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationResult
            {
                Success = false,
                FinalResponse = "",
                Turns = [],
                Error = "Rate limited"
            });

        var result = await _sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Failed, result.Status);
        Assert.Equal("Rate limited", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_IncludesUpstreamOutputsInMessages()
    {
        var config = new LlmCallConfig { SystemPrompt = "Summarize.", ModelDeploymentKey = "gpt-4" };
        var step = CreateStep(config);
        var upstreamId = new PlanStepId(Guid.NewGuid());
        var upstreamOutputs = new Dictionary<PlanStepId, string> { [upstreamId] = "upstream data" };

        RunConversationCommand? captured = null;
        _sender.Setup(s => s.Send(It.IsAny<RunConversationCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<ConversationResult>, CancellationToken>((cmd, _) => captured = (RunConversationCommand)cmd)
            .ReturnsAsync(new ConversationResult { Success = true, FinalResponse = "done", Turns = [] });

        await _sut.ExecuteAsync(step, upstreamOutputs, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("Summarize.", captured!.SystemPrompt);
        Assert.Contains("upstream data", captured.UserMessages);
    }
}
