using Application.AI.Common.Factories;
using Application.AI.Common.Interfaces;
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
using Infrastructure.AI.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Agents;

/// <summary>
/// Tests for <see cref="CapabilityMatchSupervisor"/> escalation integration.
/// Validates that autonomy tier violations trigger <see cref="IEscalationService"/>
/// and that approval-granted retries succeed with lowered tier.
/// </summary>
public sealed class CapabilityMatchSupervisorEscalationTests : IDisposable
{
    private readonly Mock<ISupervisorStrategy> _strategyMock = new();
    private readonly Mock<IDelegationStore> _storeMock = new();
    private readonly Mock<ISubagentProfileRegistry> _profileRegistryMock = new();
    private readonly Mock<ISubagentToolResolver> _toolResolverMock = new();
    private readonly Mock<IAutonomyTierResolver> _tierResolverMock = new();
    private readonly Mock<IGovernanceAuditService> _auditServiceMock = new();
    private readonly Mock<IAgentFactory> _agentFactoryMock = new();
    private readonly Mock<IEscalationService> _escalationServiceMock = new();
    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly CapabilityMatchSupervisor _supervisor;

    private readonly SubagentDefinition _defaultDefinition = new()
    {
        AgentType = SubagentType.Execute,
        AutonomyLevel = AutonomyLevel.Supervised
    };

    public CapabilityMatchSupervisorEscalationTests()
    {
        var config = new AppConfig
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
                        DefaultApprovalStrategy = "AnyOf"
                    }
                }
            }
        };
        _options = Mock.Of<IOptionsMonitor<AppConfig>>(o => o.CurrentValue == config);

        SetupDefaults();

        var contextFactory = new AgentExecutionContextFactory(
            NullLogger<AgentExecutionContextFactory>.Instance,
            _options,
            Mock.Of<IServiceProvider>(),
            NullLoggerFactory.Instance,
            Mock.Of<IToolChainBuilder>(),
            Mock.Of<ISkillPrerequisiteResolver>());

        _supervisor = new CapabilityMatchSupervisor(
            _strategyMock.Object,
            _storeMock.Object,
            _profileRegistryMock.Object,
            _toolResolverMock.Object,
            _tierResolverMock.Object,
            _auditServiceMock.Object,
            contextFactory,
            _agentFactoryMock.Object,
            _options,
            NullLogger<CapabilityMatchSupervisor>.Instance,
            modelRouter: null,
            escalationService: _escalationServiceMock.Object);
    }

    public void Dispose() => _supervisor.Dispose();

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
    }

    [Fact]
    public async Task DelegateAsync_AutonomyExceeded_TriggersEscalation()
    {
        _strategyMock
            .Setup(s => s.SelectAgent(It.IsAny<SupervisorDecisionContext>()))
            .Returns((AgentSelection?)null);

        _escalationServiceMock
            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EscalationOutcome
            {
                EscalationId = Guid.NewGuid(),
                IsApproved = false,
                Decisions = [],
                ResolutionType = EscalationResolutionType.Denied,
                ResolvedAt = DateTimeOffset.UtcNow
            });

        // minimumTier = Autonomous (higher than agents available) triggers autonomy violation
        var result = await _supervisor.DelegateAsync(
            "deploy to production",
            ["tool_a"],
            AutonomyLevel.Autonomous,
            ct: CancellationToken.None);

        _escalationServiceMock.Verify(
            x => x.RequestEscalationAsync(
                It.Is<EscalationRequest>(r =>
                    r.Description.Contains("deploy to production") &&
                    r.AgentId == nameof(CapabilityMatchSupervisor)),
                It.IsAny<CancellationToken>()),
            Times.Once);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task DelegateAsync_AutonomyExceeded_Approved_RetriesWithLoweredTier()
    {
        var callCount = 0;
        _strategyMock
            .Setup(s => s.SelectAgent(It.IsAny<SupervisorDecisionContext>()))
            .Returns(() =>
            {
                callCount++;
                // First call: tier too high, return null
                // Second call (retry with Restricted): return selection
                if (callCount == 1) return null;
                return new AgentSelection
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
            });

        _escalationServiceMock
            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EscalationOutcome
            {
                EscalationId = Guid.NewGuid(),
                IsApproved = true,
                Decisions = [],
                ResolutionType = EscalationResolutionType.Approved,
                ResolvedAt = DateTimeOffset.UtcNow
            });

        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<AIAgent>());

        var result = await _supervisor.DelegateAsync(
            "deploy to production",
            ["tool_a"],
            AutonomyLevel.Autonomous,
            ct: CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task DelegateAsync_MinimumTierRestricted_NoEscalation()
    {
        _strategyMock
            .Setup(s => s.SelectAgent(It.IsAny<SupervisorDecisionContext>()))
            .Returns((AgentSelection?)null);

        // minimumTier = Restricted (lowest) -- no autonomy violation, just no match
        var result = await _supervisor.DelegateAsync(
            "unknown task",
            ["tool_z"],
            AutonomyLevel.Restricted,
            ct: CancellationToken.None);

        _escalationServiceMock.Verify(
            x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);

        Assert.False(result.IsSuccess);
    }
}
