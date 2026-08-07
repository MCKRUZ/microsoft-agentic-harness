using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Services.AI;
using Application.Core.CQRS.Agents.RunConversation;
using Domain.AI.Planner;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Infrastructure.AI.Planner.StepExecutors;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Planner.StepExecutors;

/// <summary>
/// Regression coverage for the two identities a plan run's LlmCall steps depend on, both of which a
/// mocked collaborator previously hid.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Conversation id must be per step.</strong> It is the sole key of
/// <c>IAgentConversationCache</c> — a hit returns the cached agent and ignores the requested skills
/// and options entirely — as well as the skill-completion and observability-session key. Sharing one
/// id across steps makes a step run under another step's agent and lets the first step to finish
/// evict state belonging to steps still in flight.
/// </para>
/// <para>
/// <strong>The run budget uses the REAL <see cref="InProcessConversationBudgetTracker"/>.</strong> A mocked
/// tracker cannot reproduce <c>RunConversationCommandHandler</c>'s <c>Release</c>-in-<c>finally</c>,
/// which removes the entry and makes any budget keyed on a conversation id read back as zero. This
/// fixture simulates that release explicitly so the cross-step accounting is proven against real
/// behavior, not against a stub that always agrees.
/// </para>
/// </remarks>
public sealed class LlmCallStepExecutorRunIdentityTests : IDisposable
{
    private const string RunScope = "plan-run-conversation";

    private readonly Mock<ISender> _sender = new();
    private readonly Mock<IAgentExecutionContext> _agentContext = new();
    private readonly List<RunConversationCommand> _dispatched = [];
    private readonly List<ServiceProvider> _providers = [];

    public LlmCallStepExecutorRunIdentityTests()
    {
        _agentContext.SetupGet(c => c.ConversationId).Returns(RunScope);
    }

    public void Dispose()
    {
        foreach (var provider in _providers)
            provider.Dispose();
    }

    /// <summary>
    /// Builds an executor over the real budget tracker. Each dispatched conversation records
    /// <paramref name="tokensPerStep"/> tokens against its own conversation id, exactly as the real
    /// handler does — and, exactly as the real handler now does, releases nothing: a conversation
    /// outlives one run and one host (issue #235). Cleaning up the step's entry is the executor's job,
    /// which is what these tests then hold it to.
    /// </summary>
    private LlmCallStepExecutor BuildExecutor(
        IConversationBudgetTracker budget, int tokensPerStep, PlanId? currentPlanId = null)
    {
        _sender
            .Setup(s => s.Send(It.IsAny<RunConversationCommand>(), It.IsAny<CancellationToken>()))
            .Returns<object, CancellationToken>(async (c, _) =>
            {
                var command = (RunConversationCommand)c;
                _dispatched.Add(command);

                // What the real handler does: record the turn's usage under its own conversation id,
                // and release nothing.
                await budget.RecordUsageAsync(command.ConversationId, tokensPerStep);

                return new ConversationResult
                {
                    Success = true,
                    Turns = [],
                    FinalResponse = "ok",
                    TotalTokens = tokensPerStep
                };
            });

        var governor = new Mock<IToolInvocationGovernor>();
        governor.Setup(g => g.AuthorizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromResult(ToolInvocationDecision.Allow()));

        // Dispatch happens through an ISender resolved from a per-step scope; see
        // PlanRunLlmCallScopeTests for the end-to-end proof that this is what keeps the real
        // AgentExecutionContext from being re-bound across steps.
        var services = new ServiceCollection();
        services.AddScoped(_ => _sender.Object);
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        return new LlmCallStepExecutor(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<IPlanProgressNotifier>(),
            governor.Object,
            Mock.Of<IToolCallObserverChain>(),
            budget,
            _agentContext.Object,
            new PlanExecutionContext { CurrentPlanId = currentPlanId },
            NullLogger<LlmCallStepExecutor>.Instance);
    }

    private static InProcessConversationBudgetTracker RealTracker(int ceiling)
    {
        var config = new AppConfig
        {
            AI = new AIConfig
            {
                AgentFramework = new AgentFrameworkConfig { ConversationTokenBudget = ceiling }
            }
        };

        return new InProcessConversationBudgetTracker(
            Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == config),
            TimeProvider.System,
            NullLogger<InProcessConversationBudgetTracker>.Instance);
    }

    private static PlanStep Step(string name, string deploymentKey) => new()
    {
        Id = PlanStepId.New(),
        Name = name,
        Type = StepType.LlmCall,
        Configuration = new LlmCallConfig { SystemPrompt = "p", ModelDeploymentKey = deploymentKey },
        RetryPolicy = new RetryPolicy { MaxRetries = 0 }
    };

    [Fact]
    public async Task ExecuteAsync_ParallelStepsWithDifferentDeployments_EachGetTheirOwnConversationId()
    {
        // BLOCKING-1: with MaxParallelSteps defaulting to 10, concurrent LlmCall steps are normal. If
        // both steps share a conversation id, the second gets a cache HIT and silently runs under the
        // first step's agent — its skills, instructions, tools and deployment.
        var sut = BuildExecutor(RealTracker(ceiling: 0), tokensPerStep: 0);
        var researcher = Step("S1", "researcher");
        var classifier = Step("S2", "classifier");

        await Task.WhenAll(
            sut.ExecuteAsync(researcher, new Dictionary<PlanStepId, string>(), CancellationToken.None),
            sut.ExecuteAsync(classifier, new Dictionary<PlanStepId, string>(), CancellationToken.None));

        var ids = _dispatched.Select(c => c.ConversationId).ToList();
        Assert.Equal(2, ids.Count);
        Assert.Equal(2, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.NotEqual(RunScope, id));
        // Each id is derived from its own step, so agent resolution cannot cross over.
        Assert.Contains(PlanRunKeys.StepConversationId(RunScope, researcher.Id), ids);
        Assert.Contains(PlanRunKeys.StepConversationId(RunScope, classifier.Id), ids);
    }

    [Fact]
    public async Task ExecuteAsync_ThreeSequentialSteps_ThirdIsRefusedByTheRunBudget()
    {
        // BLOCKING-2: steps each get their own conversation id, so spend keyed on one is spread across
        // an entry per step and never sums to what the run cost. The run-level key is owned by the
        // plan run, so spend genuinely accumulates: 5k + 5k reaches the 10k ceiling and the third step
        // is refused before dispatching any inference.
        var budget = RealTracker(ceiling: 10_000);
        var sut = BuildExecutor(budget, tokensPerStep: 5_000);

        var first = await sut.ExecuteAsync(Step("S1", "a"), new Dictionary<PlanStepId, string>(), CancellationToken.None);
        var second = await sut.ExecuteAsync(Step("S2", "a"), new Dictionary<PlanStepId, string>(), CancellationToken.None);
        var third = await sut.ExecuteAsync(Step("S3", "a"), new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Completed, first.Status);
        Assert.Equal(StepExecutionStatus.Completed, second.Status);
        Assert.Equal(StepExecutionStatus.Failed, third.Status);
        Assert.Equal(PlanStepErrors.BudgetExhausted, third.ErrorMessage);
        Assert.True(third.IsPolicyDenial);

        // The refused step never dispatched inference — only the first two did.
        Assert.Equal(2, _dispatched.Count);
    }

    [Fact]
    public async Task ExecuteAsync_RunBudgetKey_IsNamespacedAwayFromConversationIds()
    {
        // The run key must not be reachable as a conversation id, or a release keyed on a conversation
        // would erase the run's accumulated spend — the exact failure this design avoids.
        var budget = RealTracker(ceiling: 10_000);
        var sut = BuildExecutor(budget, tokensPerStep: 5_000);

        await sut.ExecuteAsync(Step("S1", "a"), new Dictionary<PlanStepId, string>(), CancellationToken.None);

        var runStatus = await budget.GetStatusAsync(PlanRunKeys.RunBudgetKey(RunScope));
        Assert.Equal(5_000, runStatus.ConsumedTokens);
        Assert.StartsWith(PlanRunKeys.RunBudgetPrefix, PlanRunKeys.RunBudgetKey(RunScope));
        Assert.All(_dispatched, c => Assert.DoesNotContain(PlanRunKeys.RunBudgetPrefix, c.ConversationId));
    }

    [Fact]
    public async Task ExecuteAsync_UngovernedInProcessRun_DoesNotAccumulateAnUnreleasableBudget()
    {
        // The run budget key is created by the armed run scope and released by PlanRunExecutor. An
        // earlier revision also derived a scope from the current plan id, which meant the ungoverned
        // in-process path (the default today, including checkpoint/resume) created a planrun: entry
        // that nothing released. Since the tracker is a singleton and an exhausted budget is a
        // TERMINAL, non-retryable denial, that made a plan id permanently un-runnable in-process
        // after one exhaustion. Running the same plan id twice must therefore stay unrefused.
        _agentContext.SetupGet(c => c.ConversationId).Returns((string?)null);
        var planId = PlanId.New();
        var budget = RealTracker(ceiling: 10_000);

        var firstRun = BuildExecutor(budget, tokensPerStep: 50_000, currentPlanId: planId);
        var first = await firstRun.ExecuteAsync(
            Step("S1", "a"), new Dictionary<PlanStepId, string>(), CancellationToken.None);

        // A second in-process execution of the SAME plan id — the resume/re-run case.
        var secondRun = BuildExecutor(budget, tokensPerStep: 50_000, currentPlanId: planId);
        var second = await secondRun.ExecuteAsync(
            Step("S2", "a"), new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Completed, first.Status);
        Assert.Equal(StepExecutionStatus.Completed, second.Status);
        Assert.NotEqual(PlanStepErrors.BudgetExhausted, second.ErrorMessage);

        // And no orphan key was created under the plan id.
        var orphan = await budget.GetStatusAsync(PlanRunKeys.RunBudgetKey(planId.Value.ToString()));
        Assert.Equal(0, orphan.ConsumedTokens);
    }

    [Fact]
    public async Task ExecuteAsync_NoRunScope_HasNoRunBudgetAndAThrowawayConversationId()
    {
        // An ad-hoc direct in-process call belongs to no run: unchanged from before this work.
        _agentContext.SetupGet(c => c.ConversationId).Returns((string?)null);
        var budget = RealTracker(ceiling: 10_000);
        var sut = BuildExecutor(budget, tokensPerStep: 50_000);

        var first = await sut.ExecuteAsync(Step("S1", "a"), new Dictionary<PlanStepId, string>(), CancellationToken.None);
        var second = await sut.ExecuteAsync(Step("S2", "a"), new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Completed, first.Status);
        Assert.Equal(StepExecutionStatus.Completed, second.Status);
        Assert.Equal(2, _dispatched.Select(c => c.ConversationId).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// The step's own conversation entry must be cleaned up here, because nothing else will. The
    /// command handler stopped releasing when a conversation became something that outlives one run
    /// and one host (issue #235) — but a step conversation is created here, used for one turn, and
    /// never resumed, so without this every step of every plan run leaves an entry behind.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ReleasesTheStepsOwnConversationEntry()
    {
        var budget = RealTracker(ceiling: 10_000);
        var sut = BuildExecutor(budget, tokensPerStep: 5_000);
        var step = Step("S1", "a");

        await sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        var stepKey = PlanRunKeys.StepConversationId(RunScope, step.Id);
        var status = await budget.GetStatusAsync(stepKey);

        Assert.Equal(0, status.ConsumedTokens);
    }

    /// <summary>
    /// And on the failure path too, which is where the old <c>finally</c> in the handler used to cover
    /// this: a throwing turn must not leave its step entry behind either.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ThrowingTurn_StillReleasesTheStepsOwnConversationEntry()
    {
        var budget = RealTracker(ceiling: 10_000);
        var sut = BuildExecutor(budget, tokensPerStep: 5_000);
        var step = Step("S1", "a");

        // Re-arm the sender to record the turn's usage and then throw, so there is something to leak.
        _sender
            .Setup(s => s.Send(It.IsAny<RunConversationCommand>(), It.IsAny<CancellationToken>()))
            .Returns<object, CancellationToken>(async (c, _) =>
            {
                await budget.RecordUsageAsync(((RunConversationCommand)c).ConversationId, 5_000);
                throw new InvalidOperationException("turn exploded");
            });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None));

        var stepKey = PlanRunKeys.StepConversationId(RunScope, step.Id);
        var status = await budget.GetStatusAsync(stepKey);

        Assert.Equal(0, status.ConsumedTokens);
    }
}
