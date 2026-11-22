using Application.AI.Common.Factories;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Agents;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Skills;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Agents;
using Domain.AI.Escalation;
using Domain.AI.Governance;
using Domain.AI.Orchestration;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Governance;
using Domain.Common.Config.AI.Orchestration;
using FluentAssertions;
using Infrastructure.AI.Agents;
using Infrastructure.AI.Tests.Helpers;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Tests.Common;
using Xunit;

namespace Infrastructure.AI.Tests.Agents;

/// <summary>
/// #395: once a human approves a delegation the autonomy tier blocked, the approver is told how the
/// delegation it unblocked ended — succeeded, failed, cancelled, or never started and why. A delegation
/// that never started must not be reported as failed: the caller's <see cref="DelegationResult"/> looks
/// the same for both, and the approver is owed the difference.
/// </summary>
public sealed class CapabilityMatchSupervisorApprovalReportingTests : IDisposable
{
    private readonly Mock<ISupervisorStrategy> _strategy = new();
    private readonly Mock<IDelegationStore> _store = new();
    private readonly Mock<ISubagentProfileRegistry> _profiles = new();
    private readonly Mock<IAgentFactory> _agentFactory = new();
    private readonly Mock<IEscalationService> _escalation = new();
    private readonly RecordingApprovalReporter _reporter = new();
    private readonly AppConfig _config;
    private CapabilityMatchSupervisor? _supervisor;

    private static readonly SubagentDefinition Definition = new()
    {
        AgentType = SubagentType.Execute,
        AutonomyLevel = AutonomyLevel.Supervised
    };

    private static readonly AgentSelection Selection = new()
    {
        SelectedAgent = new AgentCandidate
        {
            AgentId = "Execute",
            AgentType = SubagentType.Execute,
            AutonomyLevel = AutonomyLevel.Restricted,
            AvailableTools = ["tool_a"]
        },
        ConfidenceScore = 0.9,
        Reasoning = "Best match"
    };

    public CapabilityMatchSupervisorApprovalReportingTests()
    {
        _config = new AppConfig
        {
            AI = new AIConfig
            {
                Orchestration = new OrchestrationConfig
                {
                    Subagent = new SubagentConfig
                    {
                        MaxDelegationDepth = 3,
                        DelegationTimeoutSeconds = 30,
                        MaxConcurrentDelegations = 5
                    }
                },
                Governance = new GovernanceConfig
                {
                    Escalation = new EscalationConfig
                    {
                        Enabled = true,
                        DefaultTimeoutSeconds = 60,
                        DefaultTimeoutAction = "DenyAndEscalate",
                        DefaultApprovalStrategy = "AnyOf",
                        DelegationApprovers = ["senior-approver"],
                        DelegationEscalatedApprovers = ["senior-approver-2"]
                    }
                }
            }
        };

        _profiles.Setup(r => r.GetAllProfiles())
            .Returns(new Dictionary<SubagentType, SubagentDefinition> { [SubagentType.Execute] = Definition });
        _profiles.Setup(r => r.GetProfile(SubagentType.Execute)).Returns(Definition);
    }

    public void Dispose() => _supervisor?.Dispose();

    /// <summary>Built on first use, so a test can change the config the constructor reads first.</summary>
    private CapabilityMatchSupervisor Supervisor => _supervisor ??= Build();

    private CapabilityMatchSupervisor Build()
    {
        var options = Mock.Of<IOptionsMonitor<AppConfig>>(o => o.CurrentValue == _config);
        var registry = new Mock<IAgentMetadataRegistry>();
        registry.Setup(r => r.GetAll()).Returns(new List<AgentDefinition>());

        var toolResolver = new Mock<ISubagentToolResolver>();
        toolResolver
            .Setup(r => r.ResolveToolsForSubagent(It.IsAny<SubagentDefinition>(), It.IsAny<IReadOnlyList<AITool>>()))
            .Returns(new List<AITool> { AIFunctionFactory.Create(() => "stub", "tool_a") });
        var tierResolver = new Mock<IAutonomyTierResolver>();
        tierResolver.Setup(r => r.Resolve(It.IsAny<SubagentDefinition>())).Returns(AutonomyLevel.Supervised);

        var contextFactory = new AgentExecutionContextFactory(
            NullLogger<AgentExecutionContextFactory>.Instance,
            options,
            Mock.Of<IServiceProvider>(),
            NullLoggerFactory.Instance,
            Mock.Of<IToolChainBuilder>(),
            Mock.Of<ISkillPrerequisiteResolver>(),
            new UnsandboxedSkillFileReader(),
            Infrastructure.AI.Tests.Planner.StepExecutors.PermissiveAdmission.PermissiveSanitizer(),
            registry.Object);

        return new CapabilityMatchSupervisor(
            _strategy.Object,
            _store.Object,
            _profiles.Object,
            toolResolver.Object,
            tierResolver.Object,
            Mock.Of<IGovernanceAuditService>(),
            contextFactory,
            _agentFactory.Object,
            registry.Object,
            Mock.Of<ISkillCompletionTracker>(),
            options,
            NullLogger<CapabilityMatchSupervisor>.Instance,
            FakeGovernanceScopeFactory.Create(out _, out _, _reporter),
            Mock.Of<IAmbientRequestScope>(),
            modelRouter: null,
            escalationService: _escalation.Object);
    }

    /// <summary>The first selection (at the blocked tier) finds nothing; the approved retry gets <paramref name="retry"/>.</summary>
    private void BlockedThenOnRetry(AgentSelection? retry)
    {
        var calls = 0;
        _strategy.Setup(s => s.SelectAgent(It.IsAny<SupervisorDecisionContext>()))
            .Returns(() => ++calls == 1 ? null : retry);
    }

    private static EscalationOutcome Approved(Guid id) => new()
    {
        EscalationId = id,
        IsApproved = true,
        Decisions = [],
        ResolutionType = EscalationResolutionType.Approved,
        ResolvedAt = DateTimeOffset.UtcNow
    };

    private Guid ApproveTier1()
    {
        var id = Guid.NewGuid();
        _escalation.Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Approved(id));
        return id;
    }

    private void DelegateRuns(AIAgent agent) =>
        _agentFactory.Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);

    private Task<DelegationResult> DelegateBlockedAsync(int depth = 0, CancellationToken ct = default) =>
        Supervisor.DelegateAsync("deploy to production", ["tool_a"], AutonomyLevel.Autonomous, depth, ct: ct);

    private RecordingApprovalReporter.Report SingleReport()
    {
        var report = _reporter.Reports.Should().ContainSingle().Subject;
        report.ReportedBy.Should().Be(CapabilityMatchSupervisor.ApprovalReportedBy);
        report.HadFailureKey.Should().BeFalse("nothing ever looks a delegation's failure-memory key up");
        return report;
    }

    [Fact]
    public async Task ApprovedRetry_Completes_ReportsSucceededUnderTheApprovingEscalation()
    {
        BlockedThenOnRetry(Selection);
        var escalationId = ApproveTier1();
        DelegateRuns(new TestableAIAgent("done"));

        var result = await DelegateBlockedAsync();

        result.IsSuccess.Should().BeTrue();
        SingleReport().Should().Match<RecordingApprovalReporter.Report>(r =>
            r.EscalationId == escalationId && r.Status == EscalationExecutionStatus.Succeeded);
    }

    [Fact]
    public async Task ApprovedRetry_FindsNoAgentEvenAtTheRelaxedTier_ReportsNeverExecutedNotFailed()
    {
        BlockedThenOnRetry(retry: null);
        var escalationId = ApproveTier1();

        var result = await DelegateBlockedAsync();

        result.IsSuccess.Should().BeFalse();
        SingleReport().Should().Match<RecordingApprovalReporter.Report>(r =>
            r.EscalationId == escalationId
            && r.Status == EscalationExecutionStatus.NeverExecuted
            && r.NotExecutedReason == EscalationNotExecutedReason.NoAgentAvailable);
    }

    [Fact]
    public async Task ApprovedRetry_HitsTheDepthLimit_ReportsNeverExecutedDepthExceeded()
    {
        // The retry counts one more level, so a delegation one short of the limit gets an approval it
        // can never use.
        BlockedThenOnRetry(Selection);
        ApproveTier1();

        await DelegateBlockedAsync(depth: 2);

        SingleReport().NotExecutedReason.Should().Be(EscalationNotExecutedReason.DelegationDepthExceeded);
    }

    [Fact]
    public async Task ApprovedRetry_DelegateThrows_ReportsFailedWithTheExceptionTypeOnly()
    {
        BlockedThenOnRetry(Selection);
        ApproveTier1();
        DelegateRuns(TestableAIAgent.Throwing(new InvalidOperationException("Server=db;Password=hunter2")));

        await DelegateBlockedAsync();

        var report = SingleReport();
        report.Status.Should().Be(EscalationExecutionStatus.Failed);
        report.FailureText.Should().Be("Delegation failed: InvalidOperationException.");
    }

    [Fact]
    public async Task ApprovedRetry_RunsPastItsTimeLimit_ReportsFailedNotCancelled()
    {
        // The caller did not cancel; the delegation ran and did not finish in time. That is a failure.
        _config.AI.Orchestration.Subagent.DelegationTimeoutSeconds = 1;
        BlockedThenOnRetry(Selection);
        ApproveTier1();
        DelegateRuns(TestableAIAgent.WithDelay("never", TimeSpan.FromMinutes(5)));

        await DelegateBlockedAsync();

        var report = SingleReport();
        report.Status.Should().Be(EscalationExecutionStatus.Failed);
        report.FailureText.Should().Be(CapabilityMatchSupervisor.ApprovedDelegationTimedOut);
    }

    [Fact]
    public async Task ApprovedRetry_CallerCancelsWhileItRuns_ReportsRunCancelled()
    {
        BlockedThenOnRetry(Selection);
        ApproveTier1();
        using var cts = new CancellationTokenSource();
        DelegateRuns(new TestableAIAgent(async (_, ct) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return new AgentResponse();
        }));

        await DelegateBlockedAsync(ct: cts.Token);

        var report = SingleReport();
        report.Status.Should().Be(EscalationExecutionStatus.NeverExecuted);
        report.NotExecutedReason.Should().Be(EscalationNotExecutedReason.RunCancelled);
    }

    [Fact]
    public async Task ApprovedRetry_CancelledBeforeItStarts_ReportsRunCancelledAndStillThrows()
    {
        BlockedThenOnRetry(Selection);
        ApproveTier1();
        _store.Setup(s => s.AppendAsync(It.IsAny<DelegationRecord>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var act = () => DelegateBlockedAsync();

        await act.Should().ThrowAsync<OperationCanceledException>();
        SingleReport().NotExecutedReason.Should().Be(EscalationNotExecutedReason.RunCancelled);
    }

    [Fact]
    public async Task ApprovedRetry_ThrowsBeforeItRuns_ReportsFailed()
    {
        BlockedThenOnRetry(Selection);
        ApproveTier1();
        _store.Setup(s => s.AppendAsync(It.IsAny<DelegationRecord>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk full at C:\\secret\\path"));

        var result = await DelegateBlockedAsync();

        result.IsSuccess.Should().BeFalse();
        var report = SingleReport();
        report.Status.Should().Be(EscalationExecutionStatus.Failed);
        report.FailureText.Should().Be("Delegation failed: IOException.");
    }

    [Fact]
    public async Task ApprovedRetry_NoFreeDelegationSlot_ReportsNeverExecutedCapacityExhausted()
    {
        _config.AI.Orchestration.Subagent.MaxConcurrentDelegations = 1;
        _config.AI.Orchestration.Subagent.DelegationTimeoutSeconds = 1;
        // Call 1 is the delegation holding the only slot; 2 is the blocked selection; 3 is the approved retry.
        var calls = 0;
        _strategy.Setup(s => s.SelectAgent(It.IsAny<SupervisorDecisionContext>()))
            .Returns(() => Interlocked.Increment(ref calls) == 2 ? null : Selection);
        ApproveTier1();
        var holding = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        // Ignores its token, so the slot stays taken after the holder's own timeout fires.
        DelegateRuns(new TestableAIAgent(async (_, _) =>
        {
            holding.TrySetResult();
            await release.Task;
            return new AgentResponse();
        }));

        var holder = Supervisor.DelegateAsync("hold the slot", ["tool_a"], AutonomyLevel.Restricted);
        await holding.Task;
        await DelegateBlockedAsync();
        release.SetResult();
        await holder;

        var report = _reporter.Reports.Should().ContainSingle().Subject;
        report.NotExecutedReason.Should().Be(EscalationNotExecutedReason.DelegationCapacityExhausted);
    }

    [Fact]
    public async Task Tier2Approval_ReportsUnderTheTier2EscalationOnly()
    {
        // Tier 1 went unanswered and was escalated, never approved; tier 2's approval is what let it run.
        BlockedThenOnRetry(Selection);
        var tier1 = Guid.NewGuid();
        var tier2 = Guid.NewGuid();
        _escalation.SetupSequence(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EscalationOutcome
            {
                EscalationId = tier1,
                IsApproved = false,
                Decisions = [],
                ResolutionType = EscalationResolutionType.Escalated,
                ResolvedAt = DateTimeOffset.UtcNow,
                EscalatedToTier = AutonomyLevel.Autonomous
            })
            .ReturnsAsync(Approved(tier2));
        DelegateRuns(new TestableAIAgent("done"));

        await DelegateBlockedAsync();

        SingleReport().EscalationId.Should().Be(tier2);
    }

    [Fact]
    public async Task DeniedEscalation_ReportsNothing()
    {
        BlockedThenOnRetry(Selection);
        _escalation.Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EscalationOutcome
            {
                EscalationId = Guid.NewGuid(),
                IsApproved = false,
                Decisions = [],
                ResolutionType = EscalationResolutionType.Denied,
                ResolvedAt = DateTimeOffset.UtcNow
            });

        await DelegateBlockedAsync();

        _reporter.Reports.Should().BeEmpty("nothing was approved, so there is no approver to report to");
    }

    [Fact]
    public async Task UnblockedDelegation_ReportsNothing()
    {
        _strategy.Setup(s => s.SelectAgent(It.IsAny<SupervisorDecisionContext>())).Returns(Selection);
        DelegateRuns(new TestableAIAgent("done"));

        await Supervisor.DelegateAsync("routine work", ["tool_a"], AutonomyLevel.Restricted);

        _reporter.Reports.Should().BeEmpty();
    }
}
