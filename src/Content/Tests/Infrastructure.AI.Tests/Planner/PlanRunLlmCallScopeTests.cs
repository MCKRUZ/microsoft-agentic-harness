using Application.AI.Common;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Services.AI;
using Application.AI.Common.Services.Agent;
using Application.AI.Common.Services.Governance;
using Application.Core.CQRS.Agents.RunConversation;
using Domain.AI.Bundles;
using Domain.AI.Governance;
using Domain.AI.Planner;
using Domain.Common;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Infrastructure.AI.Planner;
using Infrastructure.AI.Planner.StepExecutors;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Planner;

/// <summary>
/// End-to-end regression coverage for the scoped-identity ownership split in an enveloped plan run,
/// exercising the <em>real</em> <see cref="AgentExecutionContext"/>, the real
/// <see cref="PlanRunExecutor"/>, and the real <see cref="LlmCallStepExecutor"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this proves.</strong> <c>PlanRunExecutor</c> binds the plan's scoped execution context
/// to the caller's identity and the run's conversation. Each LlmCall step then drives a conversation
/// whose nested agent-turn request binds a context to the step's <em>deployment key</em> and the
/// step's own conversation id. <see cref="AgentExecutionContext.Initialize"/> throws when one instance
/// is re-bound to a different agent or conversation, so unless every step's conversation is dispatched
/// in its own DI scope, every LlmCall step in an enveloped run throws — the exact path this PR exists
/// to make work.
/// </para>
/// <para>
/// <strong>Why nothing here is a mock of the thing under test.</strong> Earlier revisions of this suite
/// mocked <c>IAgentExecutionContext</c> and <c>ISender</c>, which meant the real <c>Initialize</c>
/// guard never ran and the defect stayed invisible. The context is real, and the fake sender
/// faithfully reproduces what <c>AgentContextPropagationBehavior</c> does for the nested turn: resolve
/// the scoped context <em>from its own scope</em> and initialize it from the request. The assertions
/// read the values back, so this suite also fails if <c>Initialize</c> were reduced to a no-op.
/// </para>
/// </remarks>
public sealed class PlanRunLlmCallScopeTests
{
    private const string CallerAgentId = "caller-agent";
    private const string RunConversation = "run-conversation";

    /// <summary>What a turn observed on the scoped context it actually ran under.</summary>
    private sealed record TurnObservation(string AgentId, string ConversationId, string CommandConversationId);

    private readonly List<TurnObservation> _observed = [];
    private readonly object _observedGate = new();

    [Fact]
    public async Task EnvelopedRun_MultipleLlmCallSteps_AllSucceedAndBindTheirOwnIdentities()
    {
        var (executor, planExecutor) = BuildRun(stepCount: 3);

        var result = await executor.ExecuteAsync(
            new PlanRunRequest
            {
                PlanId = PlanId.New(),
                Envelope = new CapabilityEnvelope
                {
                    AllowedTools = [PlanCapabilities.LlmCall],
                    AutonomyCeiling = AutonomyLevel.Autonomous
                },
                AgentId = CallerAgentId,
                ConversationId = RunConversation
            },
            CancellationToken.None);

        // Before the per-step scope, the very first step threw an AgentExecutionContext scope conflict
        // and the run failed with plan_run.execution_failed.
        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.Equal(3, planExecutor.StepResults.Count);
        Assert.All(planExecutor.StepResults, r => Assert.Equal(StepExecutionStatus.Completed, r.Status));

        // Each turn bound its OWN identity: the step's deployment key, not the caller's id.
        Assert.Equal(3, _observed.Count);
        Assert.All(_observed, o => Assert.NotEqual(CallerAgentId, o.AgentId));
        Assert.All(_observed, o => Assert.NotEqual(RunConversation, o.ConversationId));

        // Reading the values back is what makes this fail if Initialize became a no-op: the context
        // would report nulls rather than the request's identity.
        Assert.All(_observed, o => Assert.Equal(o.CommandConversationId, o.ConversationId));
        Assert.Equal(
            ["deployment-0", "deployment-1", "deployment-2"],
            _observed.Select(o => o.AgentId).OrderBy(a => a, StringComparer.Ordinal));

        // And every step's conversation is distinct — the cache-isolation property, end to end.
        Assert.Equal(3, _observed.Select(o => o.ConversationId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task EnvelopedRun_RunScopeIdentity_SurvivesTheStepConversations()
    {
        // The plan's own scope must still hold the caller identity after the steps have run: the run
        // binding is what the governor uses to authorize ToolUse and Retrieval steps.
        var (executor, planExecutor) = BuildRun(stepCount: 2);

        await executor.ExecuteAsync(
            new PlanRunRequest
            {
                PlanId = PlanId.New(),
                Envelope = new CapabilityEnvelope { AllowedTools = [PlanCapabilities.LlmCall] },
                AgentId = CallerAgentId,
                ConversationId = RunConversation
            },
            CancellationToken.None);

        Assert.Equal(CallerAgentId, planExecutor.RunScopeAgentId);
        Assert.Equal(RunConversation, planExecutor.RunScopeConversationId);
    }

    /// <summary>
    /// Wires the real executor graph: a root provider whose scoped services are the real
    /// <see cref="AgentExecutionContext"/> and a turn-simulating <see cref="ISender"/>, plus a plan
    /// executor that resolves the real <see cref="LlmCallStepExecutor"/> from the run's scope.
    /// </summary>
    private (PlanRunExecutor Executor, RecordingPlanExecutor PlanExecutor) BuildRun(int stepCount)
    {
        var budget = new InProcessConversationBudgetTracker(
            Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == new AppConfig { AI = new AIConfig() }),
            TimeProvider.System,
            NullLogger<InProcessConversationBudgetTracker>.Instance);

        var governor = new Mock<IToolInvocationGovernor>();
        governor.Setup(g => g.AuthorizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromResult(ToolInvocationDecision.Allow()));

        var services = new ServiceCollection();

        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));

        // The real scoped context — the whole point of this suite.
        services.AddScoped<IAgentExecutionContext, AgentExecutionContext>();

        // Reproduces AgentContextPropagationBehavior for the nested agent-turn request: bind the
        // scope's own context to the turn's agent id (the deployment key) and conversation id.
        services.AddScoped<ISender>(sp => new TurnSimulatingSender(
            sp.GetRequiredService<IAgentExecutionContext>(), Record));

        services.AddSingleton<IConversationBudgetTracker>(budget);
        services.AddSingleton(governor.Object);
        services.AddSingleton(Mock.Of<IToolCallObserverChain>());
        services.AddSingleton(StepExecutors.PermissiveAdmission.ClassificationGate());
        services.AddSingleton(StepExecutors.PermissiveAdmission.AuthorizationGate());
        services.AddSingleton(StepExecutors.PermissiveAdmission.ProgressGuard());
        services.AddSingleton(StepExecutors.PermissiveAdmission.TraceRecorder());
        services.AddSingleton(Mock.Of<IApprovalExecutionReporter>());
        // Step executors require the admission chain rather than defaulting it to null, so that a
        // composition which forgets it fails at resolution instead of running unguarded. The real
        // chain over the gates above, built the same way the production root builds it — a mock of
        // the chain would not exercise the code an enveloped run actually goes through.
        services.AddToolCallAdmissionChain();
        services.AddSingleton(Mock.Of<IPlanProgressNotifier>());
        services.AddScoped(_ => new PlanExecutionContext());
        services.AddScoped<LlmCallStepExecutor>();

        var planExecutor = new RecordingPlanExecutor(stepCount);
        // sp is the run scope's provider — the same scope PlanRunExecutor bound the identity on, so
        // the step executor resolved from it is genuinely the one the run would use.
        services.AddScoped<IPlanExecutor>(sp =>
        {
            planExecutor.Scope = sp;
            return planExecutor;
        });

        var provider = services.BuildServiceProvider();

        var executor = new PlanRunExecutor(
            provider.GetRequiredService<IServiceScopeFactory>(),
            budget,
            NullLogger<PlanRunExecutor>.Instance);

        return (executor, planExecutor);
    }

    private void Record(TurnObservation observation)
    {
        lock (_observedGate)
            _observed.Add(observation);
    }

    /// <summary>
    /// Stands in for the conversation pipeline: binds the scoped context exactly as
    /// <c>AgentContextPropagationBehavior</c> does for <c>ExecuteAgentTurnCommand</c> (whose
    /// <c>AgentId</c> is the agent name), then reports what the context actually holds.
    /// </summary>
    private sealed class TurnSimulatingSender(
        IAgentExecutionContext scopedContext, Action<TurnObservation> record) : ISender
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            var command = (RunConversationCommand)(object)request!;

            scopedContext.Initialize(command.AgentName, command.ConversationId, turnNumber: 1);

            record(new TurnObservation(
                scopedContext.AgentId!, scopedContext.ConversationId!, command.ConversationId));

            return Task.FromResult((TResponse)(object)new ConversationResult
            {
                Success = true,
                Turns = [],
                FinalResponse = "ok",
                TotalTokens = 0
            });
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
            IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Drives N LlmCall steps through the real <see cref="LlmCallStepExecutor"/> resolved from the
    /// run's scope, and captures what the run scope's context holds — standing in for the real
    /// scheduler without dragging plan persistence into the test.
    /// </summary>
    private sealed class RecordingPlanExecutor(int stepCount) : IPlanExecutor
    {
        public List<StepExecutionResult> StepResults { get; } = [];
        public string? RunScopeAgentId { get; private set; }
        public string? RunScopeConversationId { get; private set; }

        /// <summary>Set by the DI factory so the executor can resolve from the run's own scope.</summary>
        public IServiceProvider? Scope { get; set; }

        public async Task<Result<PlanExecutionSummary>> ExecuteAsync(PlanId planId, CancellationToken ct)
        {
            var scope = Scope ?? throw new InvalidOperationException("run scope not wired");

            var runContext = scope.GetRequiredService<IAgentExecutionContext>();
            RunScopeAgentId = runContext.AgentId;
            RunScopeConversationId = runContext.ConversationId;

            var stepExecutor = scope.GetRequiredService<LlmCallStepExecutor>();

            for (var i = 0; i < stepCount; i++)
            {
                var step = new PlanStep
                {
                    Id = PlanStepId.New(),
                    Name = $"step-{i}",
                    Type = StepType.LlmCall,
                    Configuration = new LlmCallConfig
                    {
                        SystemPrompt = "p",
                        ModelDeploymentKey = $"deployment-{i}"
                    },
                    RetryPolicy = new RetryPolicy { MaxRetries = 0 }
                };

                StepResults.Add(await stepExecutor.ExecuteAsync(
                    step, new Dictionary<PlanStepId, string>(), ct));
            }

            return Result<PlanExecutionSummary>.Success(new PlanExecutionSummary
            {
                PlanId = planId,
                FinalStatus = StepExecutionStatus.Completed,
                TotalDuration = TimeSpan.Zero,
                StepStates = []
            });
        }

        public Task<Result<PlanExecutionSummary>> ExecuteAsync(
            PlanId planId, PlanExecutionContext context, CancellationToken ct) => ExecuteAsync(planId, ct);

        public Task<Result> CancelAsync(PlanId planId, CancellationToken ct)
            => Task.FromResult(Result.Success());

        public Task<Result> RetryStepAsync(PlanId planId, PlanStepId stepId, CancellationToken ct)
            => Task.FromResult(Result.Success());
    }
}
