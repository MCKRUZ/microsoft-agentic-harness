using Application.AI.Common.Categorization;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Notifications;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Application.Core.Tests.Helpers;
using Domain.AI.Agents;
using Domain.AI.Skills;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS;

/// <summary>
/// Tests for <see cref="ExecuteAgentTurnCommandHandler"/> agent registry resolution logic.
/// Verifies that the handler correctly resolves skill IDs from the agent metadata registry
/// and falls back to treating AgentName as a skill ID when no metadata is found.
/// </summary>
public class ExecuteAgentTurnCommandHandler_RegistryTests
{
    private readonly Mock<IAgentConversationCache> _agentCache = new();
    private readonly Mock<IAgentMetadataRegistry> _agentRegistry = new();
    private readonly ExecuteAgentTurnCommandHandler _handler;

    public ExecuteAgentTurnCommandHandler_RegistryTests()
    {
        var usageCapture = new Mock<ILlmUsageCapture>();
        usageCapture.Setup(c => c.TakeSnapshot())
            .Returns(new LlmUsageSnapshot(0, 0, 0, 0, null, 0m, 0m, Array.Empty<string>()));

        _handler = new ExecuteAgentTurnCommandHandler(
            _agentCache.Object,
            new Mock<Application.AI.Common.Interfaces.Governance.IToolInvocationGovernor>().Object,
            new Mock<Application.AI.Common.Interfaces.Governance.IProgressEvaluator>().Object,
            _agentRegistry.Object,
            new Mock<ISkillMetadataRegistry>().Object,
            new Application.AI.Common.Services.Context.ConversationRegistrationTracker(),
            new Mock<IObservabilityStore>().Object,
            usageCapture.Object,
            new DefaultContextSnapshotComputer(),
            new NullContextSnapshotNotifier(),
            TimeProvider.System,
            NullLogger<ExecuteAgentTurnCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_RegistryHasSkillMapping_UsesSkillIdsFromRegistry()
    {
        // Arrange
        var agentDef = new AgentDefinition
        {
            Id = "my-agent",
            Name = "My Agent",
            Skills = ["research_skill"]
        };
        _agentRegistry
            .Setup(r => r.TryGet("my-agent"))
            .Returns(agentDef);

        var agent = new TestableAIAgent("response");
        _agentCache
            .Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(),
                It.Is<IReadOnlyList<string>>(ids => ids.Count == 1 && ids[0] == "research_skill"),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);

        var command = new ExecuteAgentTurnCommand
        {
            AgentName = "my-agent",
            UserMessage = "test"
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        _agentCache.Verify(c => c.GetOrCreateAsync(
            It.IsAny<string>(),
            It.Is<IReadOnlyList<string>>(ids => ids.Count == 1 && ids[0] == "research_skill"),
            It.IsAny<SkillAgentOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_RegistryHasMultipleSkills_PassesAllSkillIds()
    {
        // Arrange
        var agentDef = new AgentDefinition
        {
            Id = "my-agent",
            Name = "My Agent",
            Skills = ["research_skill", "write_skill"]
        };
        _agentRegistry
            .Setup(r => r.TryGet("my-agent"))
            .Returns(agentDef);

        var agent = new TestableAIAgent("response");
        _agentCache
            .Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(),
                It.Is<IReadOnlyList<string>>(ids => ids.Count == 2 && ids[0] == "research_skill" && ids[1] == "write_skill"),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);

        var command = new ExecuteAgentTurnCommand
        {
            AgentName = "my-agent",
            UserMessage = "test"
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        _agentCache.Verify(c => c.GetOrCreateAsync(
            It.IsAny<string>(),
            It.Is<IReadOnlyList<string>>(ids => ids.Count == 2),
            It.IsAny<SkillAgentOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_RegistryHasEmptySkills_FallsBackToAgentName()
    {
        // Arrange
        var agentDef = new AgentDefinition
        {
            Id = "my-agent",
            Name = "My Agent"
        };
        _agentRegistry
            .Setup(r => r.TryGet("my-agent"))
            .Returns(agentDef);

        var agent = new TestableAIAgent("response");
        _agentCache
            .Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(),
                It.Is<IReadOnlyList<string>>(ids => ids.Count == 1 && ids[0] == "my-agent"),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);

        var command = new ExecuteAgentTurnCommand
        {
            AgentName = "my-agent",
            UserMessage = "test"
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        _agentCache.Verify(c => c.GetOrCreateAsync(
            It.IsAny<string>(),
            It.Is<IReadOnlyList<string>>(ids => ids.Count == 1 && ids[0] == "my-agent"),
            It.IsAny<SkillAgentOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_RegistryReturnsNull_FallsBackToAgentName()
    {
        // Arrange
        _agentRegistry
            .Setup(r => r.TryGet("unknown-agent"))
            .Returns((AgentDefinition?)null);

        var agent = new TestableAIAgent("response");
        _agentCache
            .Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(),
                It.Is<IReadOnlyList<string>>(ids => ids.Count == 1 && ids[0] == "unknown-agent"),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);

        var command = new ExecuteAgentTurnCommand
        {
            AgentName = "unknown-agent",
            UserMessage = "test"
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        _agentCache.Verify(c => c.GetOrCreateAsync(
            It.IsAny<string>(),
            It.Is<IReadOnlyList<string>>(ids => ids.Count == 1 && ids[0] == "unknown-agent"),
            It.IsAny<SkillAgentOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WithDeploymentOverride_PassesToSkillOptions()
    {
        // Arrange
        SkillAgentOptions? capturedOptions = null;
        _agentRegistry
            .Setup(r => r.TryGet(It.IsAny<string>()))
            .Returns((AgentDefinition?)null);

        var agent = new TestableAIAgent("ok");
        _agentCache
            .Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<string>, SkillAgentOptions, CancellationToken>((_, _, opts, _) => capturedOptions = opts)
            .ReturnsAsync(agent);

        var command = new ExecuteAgentTurnCommand
        {
            AgentName = "TestAgent",
            UserMessage = "test",
            DeploymentOverride = "gpt-4o-mini"
        };

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        capturedOptions.Should().NotBeNull();
        capturedOptions!.DeploymentName.Should().Be("gpt-4o-mini");
    }

    [Fact]
    public async Task Handle_WithTemperature_PassesToSkillOptions()
    {
        // Arrange
        SkillAgentOptions? capturedOptions = null;
        _agentRegistry
            .Setup(r => r.TryGet(It.IsAny<string>()))
            .Returns((AgentDefinition?)null);

        var agent = new TestableAIAgent("ok");
        _agentCache
            .Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<string>, SkillAgentOptions, CancellationToken>((_, _, opts, _) => capturedOptions = opts)
            .ReturnsAsync(agent);

        var command = new ExecuteAgentTurnCommand
        {
            AgentName = "TestAgent",
            UserMessage = "test",
            Temperature = 0.3f
        };

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        capturedOptions.Should().NotBeNull();
        capturedOptions!.Temperature.Should().Be(0.3f);
    }
}
