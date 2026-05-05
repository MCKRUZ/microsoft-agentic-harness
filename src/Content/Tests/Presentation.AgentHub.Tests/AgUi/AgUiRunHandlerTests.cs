using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Presentation.AgentHub.AgUi;
using Presentation.AgentHub.DTOs;
using Presentation.AgentHub.Hubs;
using Presentation.AgentHub.Interfaces;
using Xunit;

namespace Presentation.AgentHub.Tests.AgUi;

public sealed class AgUiRunHandlerTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static ClaimsPrincipal MakeUser(string oid) =>
        new(new ClaimsIdentity([new Claim("oid", oid)], "test"));

    private static RunAgentInput MakeInput(string threadId, string userContent) =>
        new()
        {
            ThreadId = threadId,
            RunId = Guid.NewGuid().ToString(),
            Messages =
            [
                new AgUiMessage { Id = Guid.NewGuid().ToString(), Role = "user", Content = userContent }
            ]
        };

    private static ConversationRecord MakeRecord(string id, string userId, string agentName = "test-agent") =>
        new(id, agentName, userId,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [], null, null);

    private static AgentTurnResult MakeSuccessResult(string response) =>
        new()
        {
            Success = true,
            Response = response,
            UpdatedHistory = [new ChatMessage(ChatRole.Assistant, response)]
        };

    private static AgentTurnResult MakeFailureResult(string error) =>
        new()
        {
            Success = false,
            Response = string.Empty,
            UpdatedHistory = [],
            Error = error
        };

    /// <summary>
    /// Parses SSE frames from a MemoryStream and returns the deserialized event objects.
    /// Each frame has the form <c>data: {json}\n\n</c>.
    /// </summary>
    private static List<JsonDocument> ParseSseFrames(MemoryStream stream)
    {
        stream.Position = 0;
        var raw = Encoding.UTF8.GetString(stream.ToArray());
        var frames = raw.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var docs = new List<JsonDocument>();

        foreach (var frame in frames)
        {
            var line = frame.Trim();
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
                continue;

            var json = line["data: ".Length..];
            docs.Add(JsonDocument.Parse(json));
        }

        return docs;
    }

    private static string EventType(JsonDocument doc) =>
        doc.RootElement.GetProperty("type").GetString()!;

    private static AgUiRunHandler BuildHandler(
        Mock<IMediator> mediator,
        Mock<IConversationStore> store)
    {
        return new AgUiRunHandler(
            mediator.Object,
            store.Object,
            new ConversationLockRegistry(),
            NullLogger<AgUiRunHandler>.Instance);
    }

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task HandleRunAsync_ConversationNotFound_EmitsRunStartedThenRunError()
    {
        var mediator = new Mock<IMediator>();
        var store = new Mock<IConversationStore>();
        store.Setup(s => s.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((ConversationRecord?)null);

        var handler = BuildHandler(mediator, store);
        var input = MakeInput("no-such-thread", "hello");
        var user = MakeUser("user-1");

        using var ms = new MemoryStream();
        var writer = new AgUiEventWriter(ms);

        await handler.HandleRunAsync(input, writer, user);

        var frames = ParseSseFrames(ms);
        frames.Should().HaveCountGreaterThanOrEqualTo(2);
        EventType(frames[0]).Should().Be(AgUiEventType.RunStarted);
        EventType(frames[1]).Should().Be(AgUiEventType.RunError);
        frames.Should().NotContain(f => EventType(f) == AgUiEventType.RunFinished);

        mediator.Verify(m => m.Send(It.IsAny<IRequest<AgentTurnResult>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleRunAsync_WrongUser_EmitsRunError()
    {
        var mediator = new Mock<IMediator>();
        var store = new Mock<IConversationStore>();
        var record = MakeRecord("conv-1", "owner-user");
        store.Setup(s => s.GetAsync("conv-1", It.IsAny<CancellationToken>()))
             .ReturnsAsync(record);

        var handler = BuildHandler(mediator, store);
        var input = MakeInput("conv-1", "hello");
        var intruder = MakeUser("different-user");

        using var ms = new MemoryStream();
        var writer = new AgUiEventWriter(ms);

        await handler.HandleRunAsync(input, writer, intruder);

        var frames = ParseSseFrames(ms);
        frames.Should().Contain(f => EventType(f) == AgUiEventType.RunError);
        frames.Should().NotContain(f => EventType(f) == AgUiEventType.RunFinished);

        mediator.Verify(m => m.Send(It.IsAny<IRequest<AgentTurnResult>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleRunAsync_HappyPath_EmitsFullEventSequence()
    {
        const string threadId = "conv-happy";
        const string userId = "user-happy";
        const string agentResponse = "Hello! I am your AI assistant.";

        var mediator = new Mock<IMediator>();
        var store = new Mock<IConversationStore>();
        var record = MakeRecord(threadId, userId);

        store.Setup(s => s.GetAsync(threadId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(record);
        store.Setup(s => s.GetHistoryForDispatch(threadId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync([]);
        store.Setup(s => s.AppendMessageAsync(threadId, It.IsAny<ConversationMessage>(), It.IsAny<CancellationToken>()))
             .Returns(Task.CompletedTask);

        mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(MakeSuccessResult(agentResponse));

        var handler = BuildHandler(mediator, store);
        var input = MakeInput(threadId, "Hi there");
        var user = MakeUser(userId);

        using var ms = new MemoryStream();
        var writer = new AgUiEventWriter(ms);

        await handler.HandleRunAsync(input, writer, user);

        var frames = ParseSseFrames(ms);
        var types = frames.Select(EventType).ToList();

        // Required ordering
        types[0].Should().Be(AgUiEventType.RunStarted);
        types.Should().Contain(AgUiEventType.TextMessageStart);
        types.Should().Contain(AgUiEventType.TextMessageContent);
        types.Should().Contain(AgUiEventType.TextMessageEnd);
        types.Last().Should().Be(AgUiEventType.RunFinished);

        // TEXT_MESSAGE_START must precede TEXT_MESSAGE_END
        var startIdx = types.IndexOf(AgUiEventType.TextMessageStart);
        var endIdx = types.LastIndexOf(AgUiEventType.TextMessageEnd);
        startIdx.Should().BeLessThan(endIdx);

        // Reconstructed delta content must equal the full response
        var messageId = frames.First(f => EventType(f) == AgUiEventType.TextMessageStart)
                              .RootElement.GetProperty("messageId").GetString();
        var reconstructed = string.Concat(
            frames.Where(f => EventType(f) == AgUiEventType.TextMessageContent)
                  .Select(f => f.RootElement.GetProperty("delta").GetString()));
        reconstructed.Should().Be(agentResponse);

        // All content events share the same messageId as the start event
        frames.Where(f => EventType(f) == AgUiEventType.TextMessageContent)
              .All(f => f.RootElement.GetProperty("messageId").GetString() == messageId)
              .Should().BeTrue();

        // Conversation persistence: user msg + assistant msg both appended
        store.Verify(s => s.AppendMessageAsync(
            threadId,
            It.Is<ConversationMessage>(m => m.Role == MessageRole.User),
            It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.AppendMessageAsync(
            threadId,
            It.Is<ConversationMessage>(m => m.Role == MessageRole.Assistant),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleRunAsync_AgentFails_EmitsRunErrorNoTextEvents()
    {
        const string threadId = "conv-fail";
        const string userId = "user-fail";

        var mediator = new Mock<IMediator>();
        var store = new Mock<IConversationStore>();
        var record = MakeRecord(threadId, userId);

        store.Setup(s => s.GetAsync(threadId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(record);
        store.Setup(s => s.GetHistoryForDispatch(threadId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync([]);
        store.Setup(s => s.AppendMessageAsync(threadId, It.IsAny<ConversationMessage>(), It.IsAny<CancellationToken>()))
             .Returns(Task.CompletedTask);

        mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(MakeFailureResult("Internal agent error."));

        var handler = BuildHandler(mediator, store);
        var input = MakeInput(threadId, "Do something");
        var user = MakeUser(userId);

        using var ms = new MemoryStream();
        var writer = new AgUiEventWriter(ms);

        await handler.HandleRunAsync(input, writer, user);

        var frames = ParseSseFrames(ms);
        var types = frames.Select(EventType).ToList();

        types[0].Should().Be(AgUiEventType.RunStarted);
        types.Should().Contain(AgUiEventType.RunError);
        types.Should().NotContain(AgUiEventType.TextMessageStart);
        types.Should().NotContain(AgUiEventType.TextMessageContent);
        types.Should().NotContain(AgUiEventType.TextMessageEnd);
        types.Should().NotContain(AgUiEventType.RunFinished);
    }

    [Fact]
    public async Task HandleRunAsync_NoUserMessage_EmitsRunError()
    {
        const string threadId = "conv-nomsg";
        const string userId = "user-nomsg";

        var mediator = new Mock<IMediator>();
        var store = new Mock<IConversationStore>();
        var record = MakeRecord(threadId, userId);
        store.Setup(s => s.GetAsync(threadId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(record);

        var handler = BuildHandler(mediator, store);
        var input = new RunAgentInput
        {
            ThreadId = threadId,
            RunId = Guid.NewGuid().ToString(),
            Messages = [new AgUiMessage { Id = "1", Role = "assistant", Content = "hi" }]
        };
        var user = MakeUser(userId);

        using var ms = new MemoryStream();
        var writer = new AgUiEventWriter(ms);

        await handler.HandleRunAsync(input, writer, user);

        var frames = ParseSseFrames(ms);
        frames.Should().Contain(f => EventType(f) == AgUiEventType.RunError);
        frames.Should().NotContain(f => EventType(f) == AgUiEventType.RunFinished);
        mediator.Verify(m => m.Send(It.IsAny<IRequest<AgentTurnResult>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
