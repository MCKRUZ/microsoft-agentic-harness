using Application.AI.Common.Interfaces;
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
    private readonly Mock<IAgentFactory> _agentFactory = new();
    private readonly Mock<IAgentMetadataRegistry> _agentRegistry = new();
    private readonly ExecuteAgentTurnCommandHandler _handler;

    public ExecuteAgentTurnCommandHandler_RegistryTests()
    {
        _handler = new ExecuteAgentTurnCommandHandler(
            _agentFactory.Object,
            _agentRegistry.Object,
            NullLogger<ExecuteAgentTurnCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_RegistryHasSkillMapping_UsesSkillIdFromRegistry()
    {
        // Arrange
        var agentDef = new AgentDefinition
        {
            Id = "my-agent",
            Name = "My Agent",
            Skill = "research_skill"
        };
        _agentRegistry
            .Setup(r => r.TryGet("my-agent"))
            .Returns(agentDef);

        var agent = new TestableAIAgent("response");
        _agentFactory
            .Setup(f => f.CreateAgentFromSkillAsync(
                "research_skill",
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
        _agentFactory.Verify(f => f.CreateAgentFromSkillAsync(
            "research_skill",
            It.IsAny<SkillAgentOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_RegistryHasNullSkill_FallsBackToAgentName()
    {
        // Arrange
        var agentDef = new AgentDefinition
        {
            Id = "my-agent",
            Name = "My Agent",
            Skill = null
        };
        _agentRegistry
            .Setup(r => r.TryGet("my-agent"))
            .Returns(agentDef);

        var agent = new TestableAIAgent("response");
        _agentFactory
            .Setup(f => f.CreateAgentFromSkillAsync(
                "my-agent",
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
        _agentFactory.Verify(f => f.CreateAgentFromSkillAsync(
            "my-agent",
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
        _agentFactory
            .Setup(f => f.CreateAgentFromSkillAsync(
                "unknown-agent",
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
        _agentFactory.Verify(f => f.CreateAgentFromSkillAsync(
            "unknown-agent",
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
        _agentFactory
            .Setup(f => f.CreateAgentFromSkillAsync(
                It.IsAny<string>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, SkillAgentOptions, CancellationToken>((_, opts, _) => capturedOptions = opts)
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
        _agentFactory
            .Setup(f => f.CreateAgentFromSkillAsync(
                It.IsAny<string>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, SkillAgentOptions, CancellationToken>((_, opts, _) => capturedOptions = opts)
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
