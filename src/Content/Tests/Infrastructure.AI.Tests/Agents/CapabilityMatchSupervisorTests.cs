using Application.AI.Common.Factories;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agents;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Skills;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Agents;
using Domain.AI.Governance;
using Domain.AI.Orchestration;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Orchestration;
using FluentAssertions;
using Infrastructure.AI.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
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
    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly CapabilityMatchSupervisor _supervisor;

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
            Mock.Of<ISkillPrerequisiteResolver>());

        SetupDefaults();

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
            NullLogger<CapabilityMatchSupervisor>.Instance);
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
            .ReturnsAsync(Mock.Of<AIAgent>());
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

    [Fact]
    public async Task DelegateAsync_AgentFactoryThrows_RecordsFailureToStore()
    {
        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Agent creation failed"));

        var result = await _supervisor.DelegateAsync(
            "test task", ["tool_a"], AutonomyLevel.Supervised);

        result.IsSuccess.Should().BeFalse();
        result.FailureReason.Should().Contain("Agent creation failed");

        _storeMock.Verify(
            s => s.AppendAsync(
                It.Is<DelegationRecord>(r => r.State == DelegationState.Failed),
                It.IsAny<CancellationToken>()),
            Times.Once());
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
