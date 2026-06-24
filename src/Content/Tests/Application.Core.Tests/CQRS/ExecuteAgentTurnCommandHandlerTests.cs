using Application.AI.Common.Categorization;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Notifications;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Application.Core.Tests.Helpers;
using Domain.AI.Skills;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS;

public class ExecuteAgentTurnCommandHandlerTests
{
    private readonly Mock<IAgentConversationCache> _agentCache = new();
    private readonly Mock<IAgentMetadataRegistry> _agentRegistry = new();
    private readonly ExecuteAgentTurnCommandHandler _handler;

    public ExecuteAgentTurnCommandHandlerTests()
    {
        // Default: registry knows nothing, so the handler falls back to treating
        // AgentName as a skill id — matches legacy behaviour these tests rely on.
        _agentRegistry
            .Setup(r => r.TryGet(It.IsAny<string>()))
            .Returns((Domain.AI.Agents.AgentDefinition?)null);

        var usageCapture = new Mock<ILlmUsageCapture>();
        usageCapture.Setup(c => c.TakeSnapshot())
            .Returns(new LlmUsageSnapshot(0, 0, 0, 0, null, 0m, 0m, Array.Empty<string>()));

        _handler = new ExecuteAgentTurnCommandHandler(
            _agentCache.Object,
            new Mock<Application.AI.Common.Interfaces.Governance.IToolInvocationGovernor>().Object,
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

    private static ExecuteAgentTurnCommand CreateCommand(
        string agentName = "TestAgent",
        string userMessage = "Hello",
        IReadOnlyList<ChatMessage>? history = null,
        string? systemPromptOverride = null,
        int turnNumber = 1) => new()
    {
        AgentName = agentName,
        UserMessage = userMessage,
        ConversationHistory = history ?? [],
        SystemPromptOverride = systemPromptOverride,
        TurnNumber = turnNumber
    };

    [Fact]
    public async Task Handle_ValidRequest_ReturnsSuccessResult()
    {
        // Arrange
        var agent = new TestableAIAgent("Agent response text");
        _agentCache
            .Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(),
                It.Is<IReadOnlyList<string>>(ids => ids.Count == 1 && ids[0] == "TestAgent"),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);

        var command = CreateCommand();

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.Response.Should().Be("Agent response text");
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task Handle_ActiveStreamSink_StreamsDeltasAndReturnsConcatenatedText()
    {
        // Arrange — multi-chunk streaming agent + an attached sink.
        var agent = TestableAIAgent.Streaming("Hello ", "from ", "the agent");
        _agentCache
            .Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);

        var deltas = new List<string>();
        Application.AI.Common.Services.AgentTurnStreamSink.Current =
            new Application.AI.Common.Services.AgentTurnStreamSink(
                (delta, _) => { deltas.Add(delta); return Task.CompletedTask; });

        try
        {
            // Act
            var result = await _handler.Handle(CreateCommand(), CancellationToken.None);

            // Assert — each delta streamed in order; full text is their concatenation.
            deltas.Should().Equal("Hello ", "from ", "the agent");
            result.Success.Should().BeTrue();
            result.Response.Should().Be("Hello from the agent");
        }
        finally
        {
            Application.AI.Common.Services.AgentTurnStreamSink.Current = null;
        }
    }

    [Fact]
    public async Task Handle_CallerCancelled_ReturnsCancelledErrorKind()
    {
        // A cancellation via the caller's token (e.g. client disconnect) is routine — it must
        // be classified Cancelled, not Internal, so the transport can abort without recording
        // an agent error.
        var agent = TestableAIAgent.Throwing(new OperationCanceledException());
        _agentCache
            .Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await _handler.Handle(CreateCommand(), cts.Token);

        result.Success.Should().BeFalse();
        result.ErrorKind.Should().Be(AgentTurnErrorKind.Cancelled);
    }

    [Fact]
    public async Task Handle_NoStreamSink_DoesNotStream_AndStillReturnsResponse()
    {
        // Arrange — sink is null (default), so the handler uses the blocking path.
        var agent = TestableAIAgent.Streaming("A", "B");
        _agentCache
            .Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);

        Application.AI.Common.Services.AgentTurnStreamSink.Current.Should().BeNull();

        // Act
        var result = await _handler.Handle(CreateCommand(), CancellationToken.None);

        // Assert — blocking path returns the full text without a sink.
        result.Success.Should().BeTrue();
        result.Response.Should().Be("AB");
    }

    [Fact]
    public async Task Handle_ValidRequest_UpdatedHistoryContainsUserAndAssistantMessages()
    {
        // Arrange
        var agent = new TestableAIAgent("Response");
        _agentCache
            .Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);

        var command = CreateCommand(userMessage: "My question");

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.UpdatedHistory.Should().HaveCount(2);
        result.UpdatedHistory[0].Role.Should().Be(ChatRole.User);
        result.UpdatedHistory[0].Text.Should().Be("My question");
        result.UpdatedHistory[1].Role.Should().Be(ChatRole.Assistant);
    }

    [Fact]
    public async Task Handle_AgentCacheThrows_ReturnsFailureResult()
    {
        // Arrange
        _agentCache
            .Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Agent not found"));

        var command = CreateCommand();

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.Response.Should().BeEmpty();
        result.Error.Should().Be("An internal error occurred during the agent turn.");
    }

    [Fact]
    public async Task Handle_AgentRunAsyncThrows_ReturnsFailureResult()
    {
        // Arrange
        var agent = TestableAIAgent.Throwing(new TimeoutException("Model timed out"));
        _agentCache
            .Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);

        var command = CreateCommand();

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.Response.Should().BeEmpty();
        result.Error.Should().Be("An internal error occurred during the agent turn.");
    }

    [Fact]
    public async Task Handle_AgentRunAsyncThrows_UpdatedHistoryContainsUserMessage()
    {
        // Arrange
        var agent = TestableAIAgent.Throwing(new Exception("fail"));
        _agentCache
            .Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);

        var command = CreateCommand(userMessage: "Test message");

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.UpdatedHistory.Should().ContainSingle();
        result.UpdatedHistory[0].Role.Should().Be(ChatRole.User);
        result.UpdatedHistory[0].Text.Should().Be("Test message");
    }

    [Fact]
    public async Task Handle_WithConversationHistory_IncludesHistoryInMessages()
    {
        // Arrange
        IEnumerable<ChatMessage>? capturedMessages = null;
        var agent = new TestableAIAgent((msgs, _) =>
        {
            capturedMessages = msgs.ToList();
            return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, "reply")));
        });

        _agentCache
            .Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);

        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "Previous question"),
            new(ChatRole.Assistant, "Previous answer")
        };
        var command = CreateCommand(userMessage: "Follow-up", history: history);

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        var messageList = capturedMessages!.ToList();
        messageList.Should().HaveCount(3);
        messageList[0].Text.Should().Be("Previous question");
        messageList[1].Text.Should().Be("Previous answer");
        messageList[2].Text.Should().Be("Follow-up");
    }

    [Fact]
    public async Task Handle_WithConversationHistory_UpdatedHistoryPreservesFullChain()
    {
        // Arrange
        var agent = new TestableAIAgent("New reply");
        _agentCache
            .Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);

        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "First"),
            new(ChatRole.Assistant, "First reply")
        };
        var command = CreateCommand(userMessage: "Second", history: history);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.UpdatedHistory.Should().HaveCount(4);
        result.UpdatedHistory[^1].Role.Should().Be(ChatRole.Assistant);
    }

    [Fact]
    public async Task Handle_WithSystemPromptOverride_PassesToSkillOptions()
    {
        // Arrange
        SkillAgentOptions? capturedOptions = null;
        var agent = new TestableAIAgent("ok");

        _agentCache
            .Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<string>, SkillAgentOptions, CancellationToken>((_, _, opts, _) => capturedOptions = opts)
            .ReturnsAsync(agent);

        var command = CreateCommand(systemPromptOverride: "You are a pirate.");

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        capturedOptions.Should().NotBeNull();
        capturedOptions!.AdditionalContext.Should().Be("You are a pirate.");
    }

    [Fact]
    public async Task Handle_NullSystemPromptOverride_PassesNullAdditionalContext()
    {
        // Arrange
        SkillAgentOptions? capturedOptions = null;
        var agent = new TestableAIAgent("ok");

        _agentCache
            .Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<string>, SkillAgentOptions, CancellationToken>((_, _, opts, _) => capturedOptions = opts)
            .ReturnsAsync(agent);

        var command = CreateCommand(systemPromptOverride: null);

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        capturedOptions.Should().NotBeNull();
        capturedOptions!.AdditionalContext.Should().BeNull();
    }

    [Fact]
    public async Task Handle_PassesCorrectAgentNameToCache()
    {
        // Arrange
        var agent = new TestableAIAgent("ok");
        _agentCache
            .Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(),
                It.Is<IReadOnlyList<string>>(ids => ids.Count == 1 && ids[0] == "SpecificAgent"),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);

        var command = CreateCommand(agentName: "SpecificAgent");

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        _agentCache.Verify(c => c.GetOrCreateAsync(
            It.IsAny<string>(),
            It.Is<IReadOnlyList<string>>(ids => ids.Count == 1 && ids[0] == "SpecificAgent"),
            It.IsAny<SkillAgentOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
