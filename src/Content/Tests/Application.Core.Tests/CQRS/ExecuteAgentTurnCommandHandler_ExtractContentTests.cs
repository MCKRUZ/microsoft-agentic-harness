using Application.AI.Common.Interfaces;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Application.Core.Tests.Helpers;
using Domain.AI.Agents;
using Domain.AI.Skills;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS;

/// <summary>
/// Tests for the private <c>ExtractContent</c> method in <see cref="ExecuteAgentTurnCommandHandler"/>
/// exercised indirectly through <see cref="ExecuteAgentTurnCommandHandler.Handle"/>.
/// Covers null response, string response, and empty response paths.
/// </summary>
public class ExecuteAgentTurnCommandHandler_ExtractContentTests
{
    private readonly Mock<IAgentFactory> _agentFactory = new();
    private readonly Mock<IAgentMetadataRegistry> _agentRegistry = new();
    private readonly ExecuteAgentTurnCommandHandler _handler;

    public ExecuteAgentTurnCommandHandler_ExtractContentTests()
    {
        _agentRegistry
            .Setup(r => r.TryGet(It.IsAny<string>()))
            .Returns((AgentDefinition?)null);

        var usageCapture = new Mock<ILlmUsageCapture>();
        usageCapture.Setup(c => c.TakeSnapshot())
            .Returns(new LlmUsageSnapshot(0, 0, 0, 0, null, 0m, 0m));

        _handler = new ExecuteAgentTurnCommandHandler(
            _agentFactory.Object,
            _agentRegistry.Object,
            new Mock<IObservabilityStore>().Object,
            usageCapture.Object,
            NullLogger<ExecuteAgentTurnCommandHandler>.Instance);
    }

    private static ExecuteAgentTurnCommand CreateCommand() => new()
    {
        AgentName = "TestAgent",
        UserMessage = "test"
    };

    [Fact]
    public async Task Handle_AgentReturnsNullTextContent_ResponseIsNotNull()
    {
        // Arrange
        var agent = new TestableAIAgent((_, _) =>
            Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, (string?)null))));
        _agentFactory
            .Setup(f => f.CreateAgentFromSkillAsync(
                It.IsAny<string>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);

        // Act
        var result = await _handler.Handle(CreateCommand(), CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.Response.Should().NotBeNull();
    }

    [Fact]
    public async Task Handle_AgentReturnsStringDirectly_ExtractsString()
    {
        // Arrange
        var agent = new TestableAIAgent("Direct string response");
        _agentFactory
            .Setup(f => f.CreateAgentFromSkillAsync(
                It.IsAny<string>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);

        // Act
        var result = await _handler.Handle(CreateCommand(), CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.Response.Should().Be("Direct string response");
    }

    [Fact]
    public async Task Handle_AgentReturnsLongResponse_PreservesFullText()
    {
        // Arrange
        var longText = new string('A', 10_000);
        var agent = new TestableAIAgent(longText);
        _agentFactory
            .Setup(f => f.CreateAgentFromSkillAsync(
                It.IsAny<string>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);

        // Act
        var result = await _handler.Handle(CreateCommand(), CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.Response.Should().Be(longText);
    }

    [Fact]
    public async Task Handle_AgentReturnsEmptyResponse_ResponseIsEmpty()
    {
        // Arrange
        var agent = new TestableAIAgent("");
        _agentFactory
            .Setup(f => f.CreateAgentFromSkillAsync(
                It.IsAny<string>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);

        // Act
        var result = await _handler.Handle(CreateCommand(), CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.Response.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_AgentReturnsWhitespaceResponse_PreservesWhitespace()
    {
        // Arrange
        var agent = new TestableAIAgent("   ");
        _agentFactory
            .Setup(f => f.CreateAgentFromSkillAsync(
                It.IsAny<string>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);

        // Act
        var result = await _handler.Handle(CreateCommand(), CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.Response.Should().Be("   ");
    }
}
