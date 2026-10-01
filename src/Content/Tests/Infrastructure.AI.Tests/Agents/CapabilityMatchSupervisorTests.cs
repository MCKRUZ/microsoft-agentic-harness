using Application.AI.Common.Factories;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Agents;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Skills;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Interfaces.Traces;
using Application.AI.Common.Services.Governance;
using Domain.AI.Agents;
using Domain.AI.Governance;
using Domain.AI.Identity;
using Domain.AI.Orchestration;
using Domain.AI.Skills;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Orchestration;
using FluentAssertions;
using Infrastructure.AI.Agents;
using Infrastructure.AI.Tests.Helpers;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Tests.Common;
using Xunit;

namespace Infrastructure.AI.Tests.Agents;

public sealed class CapabilityMatchSupervisorTests : IDisposable
{
    private readonly Mock<ISupervisorStrategy> _strategyMock = new();
    private readonly Mock<IDelegationStore> _storeMock = new();
    private readonly Mock<ISubagentProfileRegistry> _profileRegistryMock = new();
    private readonly Mock<ISubagentToolResolver> _toolResolverMock = new();
    private readonly Mock<IAutonomyTierResolver> _tierResolverMock = new();
    private readonly Mock<IGovernanceAuditService> _auditServiceMock = new();
    private readonly Mock<IAgentFactory> _agentFactoryMock = new();
    private readonly Mock<IAgentMetadataRegistry> _agentRegistryMock = new();
    private readonly Mock<ISkillCompletionTracker> _completionTrackerMock = new();
    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly CapabilityMatchSupervisor _supervisor;
    private readonly Mock<IAgentExecutionContext> _delegatedContextMock;
    private readonly Mock<IToolCallAdmissionPipeline> _delegatedPipelineMock;
    private readonly Mock<IAmbientRequestScope> _ambientScopeMock = new();

    private readonly SubagentDefinition _defaultDefinition = new()
    {
        AgentType = SubagentType.Execute,
        AutonomyLevel = AutonomyLevel.Supervised
    };

    private readonly AgentSelection _defaultSelection;

    public CapabilityMatchSupervisorTests()
    {
        var subagentConfig = new SubagentConfig
        {
            MaxDelegationDepth = 3,
            DelegationTimeoutSeconds = 30,
            MaxConcurrentDelegations = 5
        };

        var config = new AppConfig
        {
            AI = new AIConfig
            {
                Orchestration = new OrchestrationConfig { Subagent = subagentConfig }
            }
        };
        _options = Mock.Of<IOptionsMonitor<AppConfig>>(o => o.CurrentValue == config);
        _agentRegistryMock.Setup(r => r.GetAll()).Returns(new List<AgentDefinition>());

        _defaultSelection = new AgentSelection
        {
            SelectedAgent = new AgentCandidate
            {
                AgentId = "Execute",
                AgentType = SubagentType.Execute,
                AutonomyLevel = AutonomyLevel.Supervised,
                AvailableTools = ["tool_a"]
            },
            ConfidenceScore = 0.9,
            Reasoning = "Best match"
        };

        // Use a real factory instance -- CreateFromDelegation is non-virtual,
        // and its logic is trivial (builds an AgentExecutionContext from definition).
        var contextFactory = new AgentExecutionContextFactory(
            NullLogger<AgentExecutionContextFactory>.Instance,
            _options,
            Mock.Of<IServiceProvider>(),
            NullLoggerFactory.Instance,
            Mock.Of<IToolChainBuilder>(),
            Mock.Of<ISkillPrerequisiteResolver>(),
            new UnsandboxedSkillFileReader(),
            Infrastructure.AI.Tests.Planner.StepExecutors.PermissiveAdmission.PermissiveSanitizer(),
            _agentRegistryMock.Object);

        SetupDefaults();

        var scopeFactory = FakeGovernanceScopeFactory.Create(
            out _delegatedContextMock, out _delegatedPipelineMock);

        _supervisor = new CapabilityMatchSupervisor(
            _strategyMock.Object,
            _storeMock.Object,
            _profileRegistryMock.Object,
            _toolResolverMock.Object,
            _tierResolverMock.Object,
            _auditServiceMock.Object,
            contextFactory,
            _agentFactoryMock.Object,
            _agentRegistryMock.Object,
            _completionTrackerMock.Object,
            _options,
            NullLogger<CapabilityMatchSupervisor>.Instance,
            scopeFactory,
            _ambientScopeMock.Object);
    }

    public void Dispose()
    {
        _supervisor.Dispose();
    }

    private void SetupDefaults()
    {
        _profileRegistryMock
            .Setup(r => r.GetAllProfiles())
            .Returns(new Dictionary<SubagentType, SubagentDefinition>
            {
                [SubagentType.Execute] = _defaultDefinition
            });

        _profileRegistryMock
            .Setup(r => r.GetProfile(SubagentType.Execute))
            .Returns(_defaultDefinition);

        _toolResolverMock
            .Setup(r => r.ResolveToolsForSubagent(It.IsAny<SubagentDefinition>(), It.IsAny<IReadOnlyList<AITool>>()))
            .Returns(new List<AITool> { AIFunctionFactory.Create(() => "stub", "tool_a") });

        _tierResolverMock
            .Setup(r => r.Resolve(It.IsAny<SubagentDefinition>()))
            .Returns(AutonomyLevel.Supervised);

        _strategyMock
            .Setup(s => s.SelectAgent(It.IsAny<SupervisorDecisionContext>()))
            .Returns(_defaultSelection);

        _storeMock
            .Setup(s => s.AppendAsync(It.IsAny<DelegationRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestableAIAgent("stub output"));
    }

    [Fact]
    public async Task DelegateAsync_NoCapableAgent_ReturnsFailWithNoAgentReason()
    {
        _strategyMock
            .Setup(s => s.SelectAgent(It.IsAny<SupervisorDecisionContext>()))
            .Returns((AgentSelection?)null);

        var result = await _supervisor.DelegateAsync(
            "test task", ["tool_a"], AutonomyLevel.Supervised);

        result.IsSuccess.Should().BeFalse();
        result.FailureReason.Should().Contain("No capable agent");
    }

    [Fact]
    public async Task DelegateAsync_EmitsAuditEvents()
    {
        var result = await _supervisor.DelegateAsync(
            "test task", ["tool_a"], AutonomyLevel.Supervised);

        result.IsSuccess.Should().BeTrue();
        _auditServiceMock.Verify(
            a => a.Log(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.AtLeastOnce());
    }

    [Fact]
    public async Task DelegateAsync_RecordsPendingToStore()
    {
        await _supervisor.DelegateAsync(
            "test task", ["tool_a"], AutonomyLevel.Supervised);

        _storeMock.Verify(
            s => s.AppendAsync(
                It.Is<DelegationRecord>(r => r.State == DelegationState.Pending),
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    [Fact]
    public async Task CancelDelegationAsync_UnknownDelegationId_ReturnsFalse()
    {
        var result = await _supervisor.CancelDelegationAsync(Guid.NewGuid());

        result.Should().BeFalse();
    }

    [Fact]
    public async Task GetDelegationStatusAsync_DelegatesToStore()
    {
        var id = Guid.NewGuid();
        var expected = new DelegationRecord
        {
            DelegationId = id,
            SupervisorId = "CapabilityMatchSupervisor",
            DelegateAgentId = "Execute",
            DelegateAgentType = SubagentType.Execute,
            TaskDescription = "test",
            RequiredCapabilities = [],
            AutonomyLevel = AutonomyLevel.Supervised,
            State = DelegationState.Completed,
            DelegationDepth = 0,
            StartedAt = DateTimeOffset.UtcNow
        };

        _storeMock
            .Setup(s => s.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _supervisor.GetDelegationStatusAsync(id);

        result.Should().BeSameAs(expected);
        _storeMock.Verify(s => s.GetByIdAsync(id, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task GetActiveDelegationsAsync_FiltersToActiveStates()
    {
        var records = new List<DelegationRecord>
        {
            BuildStoreRecord(DelegationState.Pending),
            BuildStoreRecord(DelegationState.Completed),
            BuildStoreRecord(DelegationState.InProgress),
            BuildStoreRecord(DelegationState.Failed)
        };

        _storeMock
            .Setup(s => s.GetBySessionAsync("CapabilityMatchSupervisor", It.IsAny<CancellationToken>()))
            .ReturnsAsync(records);

        var result = await _supervisor.GetActiveDelegationsAsync();

        result.Should().HaveCount(2);
        result.Should().OnlyContain(r =>
            r.State == DelegationState.Pending || r.State == DelegationState.InProgress);
    }

    [Fact]
    public async Task DelegateAsync_HappyPath_RecordsCompletionToStore()
    {
        await _supervisor.DelegateAsync(
            "test task", ["tool_a"], AutonomyLevel.Supervised);

        _storeMock.Verify(
            s => s.AppendAsync(
                It.Is<DelegationRecord>(r => r.State == DelegationState.Completed),
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// Proves the fix for #757: a delegated subagent's tool calls must be authorized against ITS
    /// OWN agent id, not the entry agent's (the gap <c>CapabilityMatchSupervisor.Escalation.cs</c>
    /// used to leave as an explicitly deferred follow-up). The subagent now runs inside a fresh
    /// governance scope, Initialize()'d with the SELECTED agent's own id, AND that scope's own
    /// pipeline -- not whatever pipeline the entry turn had already published -- is what the
    /// delegated agent actually sees while it runs. A prior version of this test asserted only the
    /// Initialize() call, which stays green even if the <c>ToolAdmissionAccessor.Begin(...)</c> wrap
    /// around <c>agent.RunAsync</c> that actually closes the vulnerability is deleted.
    /// </summary>
    [Fact]
    public async Task DelegateAsync_InitializesTheDelegatedAgentsOwnGovernanceContextAndArmsItsPipeline()
    {
        IToolCallAdmissionPipeline? observedDuringRun = null;
        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestableAIAgent(_ =>
            {
                observedDuringRun = ToolAdmissionAccessor.Current;
                return new AgentResponse(new ChatMessage(ChatRole.Assistant, "stub output"));
            }));

        var outerPipeline = Mock.Of<IToolCallAdmissionPipeline>();
        using var outerScope = ToolAdmissionAccessor.Begin(outerPipeline);

        var result = await _supervisor.DelegateAsync(
            "test task", ["tool_a"], AutonomyLevel.Supervised);

        result.IsSuccess.Should().BeTrue();

        _delegatedContextMock.Verify(
            c => c.Initialize(
                _defaultSelection.SelectedAgent.AgentId,
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<string>()),
            Times.Once,
            "the delegated subagent's tool calls must be authorized against its own agent id, " +
            "read from a governance context Initialize()'d for THIS delegation -- not the entry " +
            "agent's context, which this call must never touch");

        observedDuringRun.Should().Be(_delegatedPipelineMock.Object,
            "the delegated agent must run under its OWN admission pipeline while it executes, " +
            "not whatever pipeline the entry turn had already published to the ambient");
        observedDuringRun.Should().NotBe(outerPipeline);

        ToolAdmissionAccessor.Current.Should().Be(outerPipeline,
            "the ambient must be restored to the entry turn's own pipeline once the delegation returns");

        _delegatedPipelineMock.Verify(p => p.Reset(), Times.Once,
            "the delegated pipeline must be explicitly reset, matching every other call site that " +
            "arms IToolCallAdmissionPipeline, so this one doesn't silently start relying on a " +
            "fresh-scope-means-default-state invariant if scope lifetimes ever change");
    }

    /// <summary>
    /// Proves the H1 fix: a call-once tool the parent turn already claimed must stay claimed for
    /// every delegation it spawns. Re-minting the scope from the delegation id (the pre-fix
    /// behavior) would let a delegation call a call-once tool again -- the identical rule
    /// <c>SubPlanStepExecutor.PropagateGovernanceIdentity</c> documents for sub-plans.
    /// </summary>
    [Fact]
    public async Task DelegateAsync_InheritsTheParentTurnsConversationIdAndCallOnceScope()
    {
        var parentContextMock = ArrangeAmbientParentContext();
        parentContextMock.SetupGet(c => c.ConversationId).Returns("parent-conversation");
        parentContextMock.SetupGet(c => c.CallOnceScopeId).Returns("parent-call-once-scope");

        await _supervisor.DelegateAsync("test task", ["tool_a"], AutonomyLevel.Supervised);

        _delegatedContextMock.Verify(
            c => c.Initialize(
                _defaultSelection.SelectedAgent.AgentId,
                "parent-conversation",
                It.IsAny<int>(),
                "parent-call-once-scope"),
            Times.Once,
            "a call-once tool the parent turn already claimed must stay claimed across every " +
            "delegation it spawns, not reset by a fresh per-delegation scope (#757 H1)");
    }

    /// <summary>
    /// An empty-string ConversationId/CallOnceScopeId on the parent context must fall back to the
    /// delegation id exactly as a null one does -- a bare `??` only catches null, and
    /// IAgentExecutionContext enforces no non-empty invariant on either property.
    /// </summary>
    [Fact]
    public async Task DelegateAsync_ParentHasEmptyConversationIdAndCallOnceScope_FallsBackToTheDelegationId()
    {
        var parentContextMock = ArrangeAmbientParentContext();
        parentContextMock.SetupGet(c => c.ConversationId).Returns(string.Empty);
        parentContextMock.SetupGet(c => c.CallOnceScopeId).Returns(string.Empty);

        DelegationRecord? pending = null;
        _storeMock
            .Setup(s => s.AppendAsync(It.IsAny<DelegationRecord>(), It.IsAny<CancellationToken>()))
            .Callback<DelegationRecord, CancellationToken>((r, _) => pending ??= r)
            .Returns(Task.CompletedTask);

        await _supervisor.DelegateAsync("test task", ["tool_a"], AutonomyLevel.Supervised);

        pending.Should().NotBeNull();
        _delegatedContextMock.Verify(
            c => c.Initialize(
                _defaultSelection.SelectedAgent.AgentId,
                pending!.DelegationId.ToString(),
                It.IsAny<int>(),
                pending.DelegationId.ToString()),
            Times.Once,
            "an empty-string parent scope must not collapse call-once/attribution isolation " +
            "across unrelated delegations that happen to share it");
    }

    /// <summary>
    /// A delegation run outside any governed turn (no ambient parent context) has nothing to
    /// inherit -- it must fall back to the delegation id, the pre-fix behavior, rather than throw
    /// or leave the scope null.
    /// </summary>
    [Fact]
    public async Task DelegateAsync_NoAmbientParentContext_FallsBackToTheDelegationId()
    {
        DelegationRecord? pending = null;
        _storeMock
            .Setup(s => s.AppendAsync(It.IsAny<DelegationRecord>(), It.IsAny<CancellationToken>()))
            .Callback<DelegationRecord, CancellationToken>((r, _) => pending ??= r)
            .Returns(Task.CompletedTask);

        await _supervisor.DelegateAsync("test task", ["tool_a"], AutonomyLevel.Supervised);

        pending.Should().NotBeNull();
        _delegatedContextMock.Verify(
            c => c.Initialize(
                _defaultSelection.SelectedAgent.AgentId,
                pending!.DelegationId.ToString(),
                It.IsAny<int>(),
                pending.DelegationId.ToString()),
            Times.Once);
    }

    /// <summary>
    /// Proves the M2 fix: when the parent turn carries a resolved workload identity (an A2A- or
    /// identity-propagated caller), the delegated agent is authorized as that same real caller
    /// instead of silently falling back to the host's own default identity.
    /// </summary>
    [Fact]
    public async Task DelegateAsync_ParentCarriesWorkloadIdentity_PropagatesItToTheDelegatedContext()
    {
        var parentIdentity = new AgentIdentity { Id = "caller-principal", Kind = AgentIdentityKind.Development };
        var parentContextMock = ArrangeAmbientParentContext();
        parentContextMock.SetupGet(c => c.AgentIdentity).Returns(parentIdentity);

        await _supervisor.DelegateAsync("test task", ["tool_a"], AutonomyLevel.Supervised);

        _delegatedContextMock.Verify(c => c.SetIdentity(parentIdentity), Times.Once);
    }

    /// <summary>No ambient parent identity -- SetIdentity must not be called with a null/invented value.</summary>
    [Fact]
    public async Task DelegateAsync_ParentHasNoWorkloadIdentity_NeverCallsSetIdentity()
    {
        await _supervisor.DelegateAsync("test task", ["tool_a"], AutonomyLevel.Supervised);

        _delegatedContextMock.Verify(
            c => c.SetIdentity(It.IsAny<AgentIdentity>()), Times.Never);
    }

    /// <summary>
    /// Wires a fresh <see cref="IAgentExecutionContext"/> mock as the parent turn's ambient context
    /// and returns it so the caller can set up only the one property its test cares about.
    /// </summary>
    private Mock<IAgentExecutionContext> ArrangeAmbientParentContext()
    {
        var parentContextMock = new Mock<IAgentExecutionContext>();
        var provider = new Mock<IServiceProvider>();
        provider.Setup(p => p.GetService(typeof(IAgentExecutionContext))).Returns(parentContextMock.Object);
        _ambientScopeMock.Setup(a => a.Current).Returns(provider.Object);
        return parentContextMock;
    }

    /// <summary>
    /// If governance setup throws AFTER the DI scope is opened (e.g. a misconfigured
    /// IToolCallAdmissionPipeline registration) but before ArmDelegationGovernance hands the scope
    /// back to its caller, nobody else holds a reference to dispose it -- it must dispose the scope
    /// itself before propagating, or the scope (and the delegate's external governance attribution
    /// Initialize() just published) leaks.
    /// </summary>
    [Fact]
    public async Task DelegateAsync_GovernanceSetupThrowsAfterScopeOpens_DisposesTheScopeBeforePropagating()
    {
        var throwingContext = new Mock<IAgentExecutionContext>();
        throwingContext
            .Setup(c => c.Initialize(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
            .Throws(new InvalidOperationException("simulated governance setup failure"));

        var provider = new Mock<IServiceProvider>();
        provider.Setup(p => p.GetService(typeof(IAgentExecutionContext))).Returns(throwingContext.Object);

        var scopeMock = new Mock<IServiceScope>();
        scopeMock.SetupGet(s => s.ServiceProvider).Returns(provider.Object);

        var throwingFactory = new Mock<IServiceScopeFactory>();
        throwingFactory.Setup(f => f.CreateScope()).Returns(scopeMock.Object);

        var contextFactory = new AgentExecutionContextFactory(
            NullLogger<AgentExecutionContextFactory>.Instance,
            _options,
            Mock.Of<IServiceProvider>(),
            NullLoggerFactory.Instance,
            Mock.Of<IToolChainBuilder>(),
            Mock.Of<ISkillPrerequisiteResolver>(),
            new UnsandboxedSkillFileReader(),
            Infrastructure.AI.Tests.Planner.StepExecutors.PermissiveAdmission.PermissiveSanitizer(),
            _agentRegistryMock.Object);

        using var supervisorWithThrowingScope = new CapabilityMatchSupervisor(
            _strategyMock.Object,
            _storeMock.Object,
            _profileRegistryMock.Object,
            _toolResolverMock.Object,
            _tierResolverMock.Object,
            _auditServiceMock.Object,
            contextFactory,
            _agentFactoryMock.Object,
            _agentRegistryMock.Object,
            _completionTrackerMock.Object,
            _options,
            NullLogger<CapabilityMatchSupervisor>.Instance,
            throwingFactory.Object,
            Mock.Of<IAmbientRequestScope>());

        var result = await supervisorWithThrowingScope.DelegateAsync(
            "test task", ["tool_a"], AutonomyLevel.Supervised);

        result.IsSuccess.Should().BeFalse();
        scopeMock.Verify(s => s.Dispose(), Times.Once,
            "a setup failure after the scope opened must still dispose it -- nothing else holds " +
            "a reference once ArmDelegationGovernance never hands the scope back to its caller");
    }

    /// <summary>
    /// Proves the fix for the CI correctness-review finding on this PR: once
    /// ArmDelegationGovernance hands the scope back, something must take ownership of disposing it
    /// for the REST of ExecuteAgent, not just for the RunAsync call -- if the agent-build step
    /// (which runs between ArmDelegationGovernance returning and RunAsync starting) throws, the
    /// scope must still be disposed rather than leaked.
    /// </summary>
    [Fact]
    public async Task DelegateAsync_AgentBuildThrows_StillDisposesTheGovernanceScope()
    {
        var provider = new Mock<IServiceProvider>();
        provider.Setup(p => p.GetService(typeof(IAgentExecutionContext))).Returns(_delegatedContextMock.Object);
        provider.Setup(p => p.GetService(typeof(IToolCallAdmissionPipeline))).Returns(_delegatedPipelineMock.Object);

        var scopeMock = new Mock<IServiceScope>();
        scopeMock.SetupGet(s => s.ServiceProvider).Returns(provider.Object);

        var factory = new Mock<IServiceScopeFactory>();
        factory.Setup(f => f.CreateScope()).Returns(scopeMock.Object);

        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated agent-build failure"));

        var contextFactory = new AgentExecutionContextFactory(
            NullLogger<AgentExecutionContextFactory>.Instance,
            _options,
            Mock.Of<IServiceProvider>(),
            NullLoggerFactory.Instance,
            Mock.Of<IToolChainBuilder>(),
            Mock.Of<ISkillPrerequisiteResolver>(),
            new UnsandboxedSkillFileReader(),
            Infrastructure.AI.Tests.Planner.StepExecutors.PermissiveAdmission.PermissiveSanitizer(),
            _agentRegistryMock.Object);

        using var supervisorWithThrowingAgentBuild = new CapabilityMatchSupervisor(
            _strategyMock.Object,
            _storeMock.Object,
            _profileRegistryMock.Object,
            _toolResolverMock.Object,
            _tierResolverMock.Object,
            _auditServiceMock.Object,
            contextFactory,
            _agentFactoryMock.Object,
            _agentRegistryMock.Object,
            _completionTrackerMock.Object,
            _options,
            NullLogger<CapabilityMatchSupervisor>.Instance,
            factory.Object,
            Mock.Of<IAmbientRequestScope>());

        var result = await supervisorWithThrowingAgentBuild.DelegateAsync(
            "test task", ["tool_a"], AutonomyLevel.Supervised);

        result.IsSuccess.Should().BeFalse();
        scopeMock.Verify(s => s.Dispose(), Times.Once,
            "the governance scope must be disposed even when the agent-build step between " +
            "ArmDelegationGovernance returning and RunAsync starting throws");
    }

    [Fact]
    public async Task DelegateAsync_RunsSubagentAndReturnsItsRealOutput()
    {
        // Regression for the inert delegation executor (GitHub #96, Issue 2): the supervisor
        // must actually RUN the selected subagent and surface its output — not return a
        // "Agent … created for delegation …" placeholder that discards the model's work.
        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestableAIAgent("SUBAGENT_REAL_OUTPUT"));

        var result = await _supervisor.DelegateAsync(
            "research the phases of a spec-driven project", ["tool_a"], AutonomyLevel.Supervised);

        result.IsSuccess.Should().BeTrue();
        result.Output.Should().Be("SUBAGENT_REAL_OUTPUT");
        result.Output.Should().NotContain("created for delegation");
    }

    [Fact]
    public async Task DelegateAsync_PassesTaskDescriptionToSubagent()
    {
        // The delegated work only reaches the model if the task description is sent as the
        // subagent's user message. Capture the messages the agent is run with and assert it.
        IEnumerable<ChatMessage>? captured = null;
        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestableAIAgent(msgs =>
            {
                captured = msgs.ToList();
                return new AgentResponse(new ChatMessage(ChatRole.Assistant, "done"));
            }));

        await _supervisor.DelegateAsync("UNIQUE_TASK_MARKER", ["tool_a"], AutonomyLevel.Supervised);

        captured.Should().NotBeNull();
        captured!.Should().Contain(m => m.Text != null && m.Text.Contains("UNIQUE_TASK_MARKER"));
    }

    [Fact]
    public async Task DelegateAsync_AgentFactoryThrows_RecordsFailureToStore()
    {
        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Agent creation failed"));

        var result = await _supervisor.DelegateAsync(
            "test task", ["tool_a"], AutonomyLevel.Supervised);

        result.IsSuccess.Should().BeFalse();
        // The exception's own message is never surfaced — only its type name (matching
        // MediatorDispatchRunner/WorkspaceCommandRunner's convention) — because it can carry a secret
        // this test's own "Agent creation failed" stands in for. It must NOT reach the failure record.
        result.FailureReason.Should().Contain(nameof(InvalidOperationException));
        result.FailureReason.Should().NotContain("Agent creation failed");

        _storeMock.Verify(
            s => s.AppendAsync(
                It.Is<DelegationRecord>(r =>
                    r.State == DelegationState.Failed
                    && r.FailureReason != null
                    && !r.FailureReason.Contains("Agent creation failed")),
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    // ── DelegateToNamedAgentAsync (#518) ──────────────────────────────────────

    [Fact]
    public async Task DelegateToNamedAgentAsync_UnregisteredTarget_ReturnsFailWithoutCallingAgentFactory()
    {
        _agentRegistryMock.Setup(r => r.TryGet("no-such-agent")).Returns((AgentDefinition?)null);

        var result = await _supervisor.DelegateToNamedAgentAsync("no-such-agent", "test task", "caller-agent");

        result.IsSuccess.Should().BeFalse();
        result.FailureReason.Should().Contain("no-such-agent");
        _agentFactoryMock.Verify(
            f => f.CreateAgentWithContextFromSkillsAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<SkillAgentOptions>(), It.IsAny<CancellationToken>()),
            Times.Never(),
            "an unregistered target must be refused before any agent is built");
    }

    [Fact]
    public async Task DelegateToNamedAgentAsync_TargetIsTheCallingAgentItself_ReturnsFailWithoutLookup()
    {
        var result = await _supervisor.DelegateToNamedAgentAsync(
            "same-agent", "test task", callingAgentId: "same-agent");

        result.IsSuccess.Should().BeFalse();
        result.FailureReason.Should().Contain("self");
        _agentRegistryMock.Verify(r => r.TryGet(It.IsAny<string>()), Times.Never(),
            "self-exclusion is a name comparison — it must not need a registry lookup to catch this case");
    }

    [Fact]
    public async Task DelegateToNamedAgentAsync_NoCallingAgentId_SkipsSelfExclusionAndStillDelegates()
    {
        // A caller with no resolvable identity (no ambient request scope) is not refused — self-
        // delegation by name is wasteful, not unsafe, and is still bounded by MaxDelegationDepth.
        var target = new AgentDefinition { Id = "peer-agent", Name = "Peer Agent", Description = "d" };
        _agentRegistryMock.Setup(r => r.TryGet("peer-agent")).Returns(target);
        _agentFactoryMock
            .Setup(f => f.CreateAgentWithContextFromSkillsAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<SkillAgentOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentBuildResult(new TestableAIAgent("NAMED_OUTPUT"), new AgentExecutionContext()));

        var result = await _supervisor.DelegateToNamedAgentAsync(
            "peer-agent", "test task", callingAgentId: null);

        result.IsSuccess.Should().BeTrue();
        result.Output.Should().Be("NAMED_OUTPUT");
    }

    [Fact]
    public async Task DelegateToNamedAgentAsync_RegisteredTarget_BuildsFromTheTargetsOwnSkillsAndRunsIt()
    {
        var target = new AgentDefinition
        {
            Id = "peer-agent",
            Name = "Peer Agent",
            Description = "d",
            Skills = ["peer-skill-a", "peer-skill-b"]
        };
        _agentRegistryMock.Setup(r => r.TryGet("peer-agent")).Returns(target);

        IReadOnlyList<string>? capturedSkillIds = null;
        SkillAgentOptions? capturedOptions = null;
        _agentFactoryMock
            .Setup(f => f.CreateAgentWithContextFromSkillsAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<SkillAgentOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<string>, SkillAgentOptions, CancellationToken>(
                (skillIds, options, _) =>
                {
                    capturedSkillIds = skillIds;
                    capturedOptions = options;
                })
            .ReturnsAsync(new AgentBuildResult(new TestableAIAgent("NAMED_OUTPUT"), new AgentExecutionContext()));

        var result = await _supervisor.DelegateToNamedAgentAsync(
            "peer-agent", "UNIQUE_NAMED_TASK", callingAgentId: "caller-agent");

        result.IsSuccess.Should().BeTrue();
        result.Output.Should().Be("NAMED_OUTPUT");
        // Bypasses ISubagentProfileRegistry entirely — it is built from the TARGET's own AGENT.md
        // skills, exactly like an ordinary turn for that agent, not a built-in profile's fixed set.
        capturedSkillIds.Should().Equal("peer-skill-a", "peer-skill-b");
        capturedOptions!.OwningAgentId.Should().Be("peer-agent");
        _profileRegistryMock.Verify(r => r.GetProfile(It.IsAny<SubagentType>()), Times.Never());
    }

    [Fact]
    public async Task DelegateToNamedAgentAsync_TargetHasAToolCeiling_PassesItThroughToTheBuiltAgent()
    {
        // Correctness-review finding on this PR: named delegation must not bypass the target's own
        // AGENT.md tool ceiling. ExecuteAgentTurnCommandHandler's ordinary-turn path always passes
        // AllowedTools = agentDef?.AllowedTools — this branch must mirror that exactly, or an operator
        // who locked an agent down to read-only tools via allowed-tools: sees that ceiling silently
        // ignored whenever the agent is reached through delegate_task's target_agent instead of a
        // normal turn.
        var target = new AgentDefinition
        {
            Id = "locked-down-agent",
            Name = "Locked Down Agent",
            Description = "d",
            AllowedTools = ["read_file", "list_files"]
        };
        _agentRegistryMock.Setup(r => r.TryGet("locked-down-agent")).Returns(target);

        SkillAgentOptions? capturedOptions = null;
        _agentFactoryMock
            .Setup(f => f.CreateAgentWithContextFromSkillsAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<SkillAgentOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<string>, SkillAgentOptions, CancellationToken>(
                (_, options, _) => capturedOptions = options)
            .ReturnsAsync(new AgentBuildResult(new TestableAIAgent("out"), new AgentExecutionContext()));

        await _supervisor.DelegateToNamedAgentAsync("locked-down-agent", "test task", "caller-agent");

        capturedOptions!.AllowedTools.Should().Equal("read_file", "list_files");
    }

    [Fact]
    public async Task DelegateToNamedAgentAsync_TargetHasSkillsWithPrerequisites_SuppliesAConversationScope()
    {
        // Correctness-review finding: AgentExecutionContextFactory stamps a prerequisite map whenever
        // any of the target's skills declares prerequisites, and AgentFactory.ChatClient's
        // ResolvePrerequisiteScope throws InvalidOperationException when no conversation scope is
        // present in SkillAgentOptions.AdditionalProperties[AgentFactory.ConversationIdPropertyKey].
        // The ordinary-turn path always supplies one (AgentConversationCache.WithConversationScope);
        // this branch must too, or every named delegation to a peer with prerequisite-declaring
        // skills fails deterministically.
        var target = new AgentDefinition
        {
            Id = "prerequisite-agent",
            Name = "Prerequisite Agent",
            Description = "d"
        };
        _agentRegistryMock.Setup(r => r.TryGet("prerequisite-agent")).Returns(target);

        SkillAgentOptions? capturedOptions = null;
        _agentFactoryMock
            .Setup(f => f.CreateAgentWithContextFromSkillsAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<SkillAgentOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<string>, SkillAgentOptions, CancellationToken>(
                (_, options, _) => capturedOptions = options)
            .ReturnsAsync(new AgentBuildResult(new TestableAIAgent("out"), new AgentExecutionContext()));

        await _supervisor.DelegateToNamedAgentAsync("prerequisite-agent", "test task", "caller-agent");

        capturedOptions!.AdditionalProperties.Should().NotBeNull();
        capturedOptions.AdditionalProperties!.Should().ContainKey(AgentFactory.ConversationIdPropertyKey);
        var scope = (string)capturedOptions.AdditionalProperties[AgentFactory.ConversationIdPropertyKey];
        Guid.TryParse(scope, out _).Should().BeTrue(
            "the scope must be the per-delegation id, not the target agent's own (stable, shared) id");
    }

    [Fact]
    public async Task DelegateToNamedAgentAsync_TwoDelegationsToTheSameTarget_GetIsolatedPrerequisiteScopes()
    {
        // #518 security-review finding: scoping to target.Id (constant for every caller) let one
        // tenant's unlocked prerequisite skill permanently unlock the gated tools for every other
        // caller who later delegates to the same target. Two separate delegations to the same target
        // must never share a scope value.
        var target = new AgentDefinition { Id = "shared-target", Name = "Shared Target", Description = "d" };
        _agentRegistryMock.Setup(r => r.TryGet("shared-target")).Returns(target);

        var capturedScopes = new List<string>();
        _agentFactoryMock
            .Setup(f => f.CreateAgentWithContextFromSkillsAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<SkillAgentOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<string>, SkillAgentOptions, CancellationToken>(
                (_, options, _) => capturedScopes.Add(
                    (string)options.AdditionalProperties![AgentFactory.ConversationIdPropertyKey]))
            .ReturnsAsync(new AgentBuildResult(new TestableAIAgent("out"), new AgentExecutionContext()));

        await _supervisor.DelegateToNamedAgentAsync("shared-target", "task one", "caller-a");
        await _supervisor.DelegateToNamedAgentAsync("shared-target", "task two", "caller-b");

        capturedScopes.Should().HaveCount(2);
        capturedScopes[0].Should().NotBe(capturedScopes[1],
            "two different delegations to the same target must never share prerequisite-unlock scope");
    }

    [Fact]
    public async Task DelegateToNamedAgentAsync_RunCompletes_ClearsThePrerequisiteScope()
    {
        // #518 security-review finding: the delegation-scoped unlock state must not outlive the
        // delegation it was created for, or it accumulates forever with nothing to clear it.
        var target = new AgentDefinition { Id = "cleared-target", Name = "Cleared Target", Description = "d" };
        _agentRegistryMock.Setup(r => r.TryGet("cleared-target")).Returns(target);

        string? capturedScope = null;
        _agentFactoryMock
            .Setup(f => f.CreateAgentWithContextFromSkillsAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<SkillAgentOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<string>, SkillAgentOptions, CancellationToken>(
                (_, options, _) => capturedScope =
                    (string)options.AdditionalProperties![AgentFactory.ConversationIdPropertyKey])
            .ReturnsAsync(new AgentBuildResult(new TestableAIAgent("out"), new AgentExecutionContext()));

        await _supervisor.DelegateToNamedAgentAsync("cleared-target", "test task", "caller-agent");

        capturedScope.Should().NotBeNull();
        _completionTrackerMock.Verify(t => t.ClearConversation(capturedScope!), Times.Once);
    }

    [Fact]
    public async Task DelegateToNamedAgentAsync_ExecutionTracingStampedAWriter_CompletesAndDisposesIt()
    {
        // Correctness-review finding: the profile branch's CreateFromDelegation context never holds a
        // trace writer, but CreateAgentWithContextFromSkillsAsync's context can when execution tracing
        // is enabled — and nothing on a one-shot delegation's path completed or disposed it, silently
        // leaving the run's manifest.json permanently stamped write_completed: false and its
        // SemaphoreSlim/file handle unreleased.
        var target = new AgentDefinition { Id = "traced-target", Name = "Traced Target", Description = "d" };
        _agentRegistryMock.Setup(r => r.TryGet("traced-target")).Returns(target);

        var writerMock = new Mock<ITraceWriter>();
        var builtContext = new AgentExecutionContext
        {
            AdditionalProperties = new Dictionary<string, object>
            {
                [ITraceWriter.AdditionalPropertiesKey] = writerMock.Object
            }
        };
        _agentFactoryMock
            .Setup(f => f.CreateAgentWithContextFromSkillsAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<SkillAgentOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentBuildResult(new TestableAIAgent("out"), builtContext));

        await _supervisor.DelegateToNamedAgentAsync("traced-target", "test task", "caller-agent");

        writerMock.Verify(w => w.CompleteAsync(It.IsAny<CancellationToken>()), Times.Once);
        writerMock.Verify(w => w.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task DelegateToNamedAgentAsync_TargetHasNoDeclaredSkills_FallsBackToItsOwnIdAsTheSkillId()
    {
        var target = new AgentDefinition { Id = "peer-agent", Name = "Peer Agent", Description = "d" };
        _agentRegistryMock.Setup(r => r.TryGet("peer-agent")).Returns(target);

        IReadOnlyList<string>? capturedSkillIds = null;
        _agentFactoryMock
            .Setup(f => f.CreateAgentWithContextFromSkillsAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<SkillAgentOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<string>, SkillAgentOptions, CancellationToken>(
                (skillIds, _, _) => capturedSkillIds = skillIds)
            .ReturnsAsync(new AgentBuildResult(new TestableAIAgent("out"), new AgentExecutionContext()));

        await _supervisor.DelegateToNamedAgentAsync("peer-agent", "test task", "caller-agent");

        capturedSkillIds.Should().Equal("peer-agent");
    }

    [Fact]
    public async Task DelegateToNamedAgentAsync_DepthLimitReached_ReturnsFailWithoutAnyLookup()
    {
        var result = await _supervisor.DelegateToNamedAgentAsync(
            "peer-agent", "test task", "caller-agent", currentDelegationDepth: 3);

        result.IsSuccess.Should().BeFalse();
        result.FailureReason.Should().Contain("depth");
        _agentRegistryMock.Verify(r => r.TryGet(It.IsAny<string>()), Times.Never());
    }

    private static DelegationRecord BuildStoreRecord(DelegationState state) => new()
    {
        DelegationId = Guid.NewGuid(),
        SupervisorId = "CapabilityMatchSupervisor",
        DelegateAgentId = "Execute",
        DelegateAgentType = SubagentType.Execute,
        TaskDescription = "test",
        RequiredCapabilities = [],
        AutonomyLevel = AutonomyLevel.Supervised,
        State = state,
        DelegationDepth = 0,
        StartedAt = DateTimeOffset.UtcNow
    };
}
