using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Permissions;
using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Agent;
using Application.AI.Common.Services.Governance;
using Application.Core.Permissions;
using Domain.AI.Bundles;
using Domain.AI.Changes;
using Domain.AI.Governance;
using Domain.AI.Permissions;
using Domain.AI.Planner;
using Domain.Common;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Permissions;
using Domain.Common.Config.AI.Sandbox;
using Infrastructure.AI.Permissions;
using Infrastructure.AI.Planner.StepExecutors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Planner.StepExecutors;

/// <summary>
/// End-to-end proof that the capability envelope confines sub-plans: the parent arms an envelope and
/// identity, <see cref="SubPlanStepExecutor"/> recurses into a fresh DI scope, and inside that child
/// scope the <em>real</em> <see cref="ToolInvocationGovernor"/> — driven by the <em>real</em>
/// <see cref="ThreePhasePermissionResolver"/> and <see cref="EnvelopePermissionRuleProvider"/> —
/// still denies an ungranted tool while a granted tool keeps working. Two ambient facts must survive
/// the recursion for this to hold: the envelope (AsyncLocal, flows on its own) and the governance
/// identity (DI-scoped, re-stamped by the executor).
/// </summary>
public sealed class SubPlanEnvelopeConfinementTests
{
    private const string GrantedTool = "file_system";
    private const string DeniedTool = "bash";

    [Fact]
    public async Task ExecuteAsync_DeniedToolStaysDenied_InsideSubPlan()
    {
        var decisions = new Dictionary<string, bool>();
        var childPlanId = new PlanId(Guid.NewGuid());
        var scopeFactory = BuildChildServices(decisions).GetRequiredService<IServiceScopeFactory>();

        var parentContext = new Mock<IAgentExecutionContext>();
        parentContext.SetupGet(c => c.AgentId).Returns("caller-agent");
        parentContext.SetupGet(c => c.ConversationId).Returns("conv-1");
        parentContext.SetupGet(c => c.TurnNumber).Returns(1);

        var sut = new SubPlanStepExecutor(
            scopeFactory,
            Mock.Of<IPlanStateStore>(),
            Mock.Of<IPlanProgressNotifier>(),
            new PlanExecutionContext { Depth = 0, MaxDepth = 3 },
            parentContext.Object,
            NullLogger<SubPlanStepExecutor>.Instance);

        var step = new PlanStep
        {
            Id = new PlanStepId(Guid.NewGuid()),
            Name = "sub-plan-step",
            Type = StepType.SubPlanInvocation,
            Configuration = new SubPlanConfig { ChildPlanId = childPlanId },
            RetryPolicy = new RetryPolicy { MaxRetries = 0 }
        };

        StepExecutionResult result;
        using (CapabilityEnvelopeAccessor.Begin(new CapabilityEnvelope
        {
            AllowedTools = [GrantedTool],
            AutonomyCeiling = AutonomyLevel.Autonomous
        }))
        {
            result = await sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);
        }

        Assert.Equal(StepExecutionStatus.Completed, result.Status);
        Assert.False(decisions[DeniedTool], "an ungranted tool must stay denied inside the sub-plan");
        Assert.True(decisions[GrantedTool], "a granted tool must keep working inside the sub-plan");
    }

    [Fact]
    public async Task ExecuteAsync_AmbientEnvelope_FlowsIntoChildPlanExecution()
    {
        CapabilityEnvelope? observed = null;
        var childPlanId = new PlanId(Guid.NewGuid());
        var childExecutor = new Mock<IPlanExecutor>();
        childExecutor
            .Setup(e => e.ExecuteAsync(childPlanId, It.IsAny<PlanExecutionContext>(), It.IsAny<CancellationToken>()))
            .Callback(() => observed = CapabilityEnvelopeAccessor.Current)
            .ReturnsAsync(Result<PlanExecutionSummary>.Success(new PlanExecutionSummary
            {
                PlanId = childPlanId,
                FinalStatus = StepExecutionStatus.Completed,
                TotalDuration = TimeSpan.Zero,
                StepStates = []
            }));

        var services = new ServiceCollection();
        services.AddSingleton(childExecutor.Object);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var sut = new SubPlanStepExecutor(
            scopeFactory,
            Mock.Of<IPlanStateStore>(),
            Mock.Of<IPlanProgressNotifier>(),
            new PlanExecutionContext { Depth = 0, MaxDepth = 3 },
            Mock.Of<IAgentExecutionContext>(),
            NullLogger<SubPlanStepExecutor>.Instance);

        var step = new PlanStep
        {
            Id = new PlanStepId(Guid.NewGuid()),
            Name = "sub-plan-step",
            Type = StepType.SubPlanInvocation,
            Configuration = new SubPlanConfig { ChildPlanId = childPlanId },
            RetryPolicy = new RetryPolicy { MaxRetries = 0 }
        };

        var envelope = new CapabilityEnvelope { AllowedTools = [GrantedTool] };
        using (CapabilityEnvelopeAccessor.Begin(envelope))
        {
            await sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);
        }

        Assert.Same(envelope, observed);
    }

    /// <summary>
    /// Builds the child-scope service graph with the real governance chain: real scoped
    /// <see cref="AgentExecutionContext"/> (stamped by the executor under test), real
    /// <see cref="ToolInvocationGovernor"/>, and the real three-phase resolver driven only by the
    /// envelope rule provider. Only leaf dependencies (risk classifier, policy engine, audit,
    /// capability enforcer, options) are test doubles.
    /// </summary>
    private static ServiceProvider BuildChildServices(Dictionary<string, bool> decisions)
    {
        var appConfig = new AppConfig
        {
            AI = new AIConfig { Permissions = new PermissionsConfig { DenialRateLimitThreshold = 5 } }
        };

        var safetyGates = new Mock<ISafetyGateRegistry>();
        safetyGates
            .Setup(r => r.CheckSafetyGate(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, object?>?>()))
            .Returns((SafetyGate?)null);

        var capabilityEnforcer = new Mock<ICapabilityEnforcer>();
        capabilityEnforcer
            .Setup(x => x.EnforceAsync(It.IsAny<string>(), It.IsAny<Domain.AI.Sandbox.ToolCapability>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var services = new ServiceCollection();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(decisions);
        services.AddScoped<IAgentExecutionContext, AgentExecutionContext>();
        services.AddSingleton<IToolPermissionService>(new ThreePhasePermissionResolver(
            [new EnvelopePermissionRuleProvider(NullLogger<EnvelopePermissionRuleProvider>.Instance)],
            safetyGates.Object,
            new GlobPatternMatcher(),
            new Mock<IDenialTracker>().Object,
            Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == appConfig),
            NullLogger<ThreePhasePermissionResolver>.Instance));
        services.AddSingleton(Mock.Of<IToolRiskClassifier>(
            c => c.Classify(It.IsAny<string>()) == new ToolRiskProfile(BlastRadius.Low, true)));
        // Nothing is known about the tools in this fixture, which is the fail-closed answer. It changes
        // no outcome here — the behaviour posture is off in this config — but the governor resolves the
        // registry regardless, and a container that cannot build one cannot build a governor.
        services.AddSingleton(Mock.Of<IToolBehaviorRegistry>(
            r => r.Resolve(It.IsAny<string>()) == ToolBehavior.Unknown));
        services.AddSingleton(new Mock<IAutonomyDecisionEvaluator>().Object);
        services.AddSingleton(Mock.Of<IGovernancePolicyEngine>(p => p.HasPolicies == false));
        services.AddSingleton(Mock.Of<IGovernanceAuditService>());
        services.AddSingleton(new Mock<IDenialTracker>().Object);
        services.AddSingleton(capabilityEnforcer.Object);
        services.AddSingleton(Mock.Of<IOptionsMonitor<GovernanceConfig>>(
            m => m.CurrentValue == new GovernanceConfig { EnforceToolInvocation = false }));
        services.AddSingleton(Mock.Of<IOptionsMonitor<PermissionsConfig>>(
            m => m.CurrentValue == new PermissionsConfig()));
        services.AddSingleton(Mock.Of<IOptionsMonitor<SandboxConfig>>(
            m => m.CurrentValue == new SandboxConfig()));
        // Approval routing is not what these tests exercise — they assert an envelope's deny survives
        // into a sub-plan. A router that never routes keeps the governor's block the envelope's own.
        services.AddSingleton(Mock.Of<IToolApprovalRouter>(r =>
            r.RequestApprovalAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Domain.AI.Changes.BlastRadius>(), It.IsAny<IReadOnlyDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>())
            == new ValueTask<ToolApprovalResult>(ToolApprovalResult.NotRouted("routing disabled"))));
        // Resolved from this container rather than stubbed, so the real governor writes its decisions
        // to a real trail reading the same governance config the envelope arms.
        services.AddScoped<IGovernanceTraceRecorder, GovernanceTraceRecorder>();
        services.AddScoped<IToolInvocationGovernor, ToolInvocationGovernor>();
        services.AddScoped<IPlanExecutor, GovernorProbePlanExecutor>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Stands in for the child plan's execution: asks the child scope's real governor to authorize a
    /// granted and an ungranted tool, recording both decisions — exactly what the child plan's
    /// ToolUse steps would do through the same governor.
    /// </summary>
    private sealed class GovernorProbePlanExecutor(
        IToolInvocationGovernor governor, Dictionary<string, bool> decisions) : IPlanExecutor
    {
        public async Task<Result<PlanExecutionSummary>> ExecuteAsync(
            PlanId planId, PlanExecutionContext context, CancellationToken ct)
        {
            decisions[DeniedTool] = (await governor.AuthorizeAsync(DeniedTool, ct)).IsAllowed;
            decisions[GrantedTool] = (await governor.AuthorizeAsync(GrantedTool, ct)).IsAllowed;

            return Result<PlanExecutionSummary>.Success(new PlanExecutionSummary
            {
                PlanId = planId,
                FinalStatus = StepExecutionStatus.Completed,
                TotalDuration = TimeSpan.Zero,
                StepStates = []
            });
        }

        public Task<Result<PlanExecutionSummary>> ExecuteAsync(PlanId planId, CancellationToken ct)
            => ExecuteAsync(planId, new PlanExecutionContext(), ct);

        public Task<Result> CancelAsync(PlanId planId, CancellationToken ct)
            => Task.FromResult(Result.Success());

        public Task<Result> RetryStepAsync(PlanId planId, PlanStepId stepId, CancellationToken ct)
            => Task.FromResult(Result.Success());
    }
}
