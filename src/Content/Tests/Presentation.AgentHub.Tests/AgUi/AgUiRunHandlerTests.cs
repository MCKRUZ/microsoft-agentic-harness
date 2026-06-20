using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Application.AI.Common.Interfaces;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
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
        MakeInput(threadId, userContent, Guid.NewGuid().ToString());

    private static RunAgentInput MakeInput(string threadId, string userContent, string userMessageId) =>
        new()
        {
            ThreadId = threadId,
            RunId = Guid.NewGuid().ToString(),
            Messages =
            [
                new AgUiMessage { Id = userMessageId, Role = "user", Content = userContent }
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

    private static AgentTurnResult MakeSuccessResultWithUsage(
        string response, int inputTokens = 150, int outputTokens = 80,
        int cacheRead = 40, int cacheWrite = 10, decimal costUsd = 0.003m,
        string model = "gpt-4o", List<string>? tools = null) =>
        new()
        {
            Success = true,
            Response = response,
            UpdatedHistory = [new ChatMessage(ChatRole.Assistant, response)],
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CacheRead = cacheRead,
            CacheWrite = cacheWrite,
            CostUsd = costUsd,
            Model = model,
            ToolsInvoked = tools ?? [],
        };

    private static AgentTurnResult MakeFailureResult(string error) =>
        new()
        {
            Success = false,
            Response = string.Empty,
            UpdatedHistory = [],
            Error = error
        };

    private static AgentTurnResult MakeConfigFailureResult(string error) =>
        new()
        {
            Success = false,
            Response = string.Empty,
            UpdatedHistory = [],
            Error = error,
            ErrorKind = AgentTurnErrorKind.Configuration
        };

    private static (Mock<IMediator> Mediator, Mock<IConversationStore> Store) SetupFailingTurn(
        string threadId, string userId, AgentTurnResult failure)
    {
        var mediator = new Mock<IMediator>();
        var store = new Mock<IConversationStore>();
        store.Setup(s => s.GetAsync(threadId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(MakeRecord(threadId, userId));
        store.Setup(s => s.GetHistoryForDispatch(threadId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync([]);
        store.Setup(s => s.AppendMessageAsync(threadId, It.IsAny<ConversationMessage>(), It.IsAny<CancellationToken>()))
             .Returns(Task.CompletedTask);
        mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(failure);
        return (mediator, store);
    }

    private static string RunErrorMessage(IEnumerable<JsonDocument> frames) =>
        frames.First(f => EventType(f) == AgUiEventType.RunError)
              .RootElement.GetProperty("message").GetString()!;

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
        Mock<IConversationStore> store,
        Mock<IObservabilityStore>? observability = null,
        string environmentName = "Development")
    {
        if (observability is null)
        {
            observability = new Mock<IObservabilityStore>();
            observability.Setup(o => o.StartSessionAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Guid.NewGuid());
        }

        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns(environmentName);

        return new AgUiRunHandler(
            mediator.Object,
            store.Object,
            observability.Object,
            new ConversationLockRegistry(),
            new AgUiEventWriterAccessor(),
            environment.Object,
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
    public async Task HandleRunAsync_ConfigurationError_InDevelopment_SurfacesActionableMessage()
    {
        const string threadId = "conv-cfg-dev";
        const string userId = "user-cfg";
        const string actionable =
            "Anthropic client is not configured. Set AppConfig:AI:AgentFramework:Endpoint and ApiKey.";

        var (mediator, store) = SetupFailingTurn(threadId, userId, MakeConfigFailureResult(actionable));
        var handler = BuildHandler(mediator, store, environmentName: "Development");

        using var ms = new MemoryStream();
        await handler.HandleRunAsync(MakeInput(threadId, "Hi"), new AgUiEventWriter(ms), MakeUser(userId));

        RunErrorMessage(ParseSseFrames(ms)).Should().Be(actionable);
    }

    [Fact]
    public async Task HandleRunAsync_ConfigurationError_InProduction_StaysGeneric()
    {
        const string threadId = "conv-cfg-prod";
        const string userId = "user-cfg";
        const string actionable =
            "Anthropic client is not configured. Set AppConfig:AI:AgentFramework:Endpoint and ApiKey.";

        var (mediator, store) = SetupFailingTurn(threadId, userId, MakeConfigFailureResult(actionable));
        var handler = BuildHandler(mediator, store, environmentName: "Production");

        using var ms = new MemoryStream();
        await handler.HandleRunAsync(MakeInput(threadId, "Hi"), new AgUiEventWriter(ms), MakeUser(userId));

        RunErrorMessage(ParseSseFrames(ms)).Should().Be("The agent was unable to process your request.");
    }

    [Fact]
    public async Task HandleRunAsync_CancelledTurn_PropagatesCancellation_NoRunError()
    {
        // A cancelled turn (e.g. caller disconnect) is routine — it funnels into the
        // handler's central cancellation sink (no event emitted) rather than surfacing a
        // user-facing RunError. The run aborts gracefully without a RunError or RunFinished.
        const string threadId = "conv-cancel";
        const string userId = "user-cancel";
        var cancelled = new AgentTurnResult
        {
            Success = false,
            Response = string.Empty,
            UpdatedHistory = [],
            Error = "cancelled",
            ErrorKind = AgentTurnErrorKind.Cancelled,
        };

        var (mediator, store) = SetupFailingTurn(threadId, userId, cancelled);
        var handler = BuildHandler(mediator, store);

        using var ms = new MemoryStream();
        var act = () => handler.HandleRunAsync(MakeInput(threadId, "Hi"), new AgUiEventWriter(ms), MakeUser(userId));

        await act.Should().NotThrowAsync();
        var frames = ParseSseFrames(ms);
        frames.Should().NotContain(f => EventType(f) == AgUiEventType.RunError);
        frames.Should().NotContain(f => EventType(f) == AgUiEventType.RunFinished);
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
    public async Task HandleRunAsync_ClientSuppliesUserMessageId_PersistsUserMessageUnderThatId()
    {
        const string threadId = "conv-clientid";
        const string userId = "user-clientid";
        var clientUserId = Guid.NewGuid();

        var mediator = new Mock<IMediator>();
        var store = new Mock<IConversationStore>();
        var appended = new List<ConversationMessage>();

        store.Setup(s => s.GetAsync(threadId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(MakeRecord(threadId, userId));
        store.Setup(s => s.GetHistoryForDispatch(threadId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync([]);
        store.Setup(s => s.AppendMessageAsync(threadId, It.IsAny<ConversationMessage>(), It.IsAny<CancellationToken>()))
             .Callback<string, ConversationMessage, CancellationToken>((_, m, _) => appended.Add(m))
             .Returns(Task.CompletedTask);
        mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(MakeSuccessResult("reply"));

        var handler = BuildHandler(mediator, store);

        using var ms = new MemoryStream();
        await handler.HandleRunAsync(
            MakeInput(threadId, "Hi", clientUserId.ToString()), new AgUiEventWriter(ms), MakeUser(userId));

        var userMsg = appended.Single(m => m.Role == MessageRole.User);
        userMsg.Id.Should().Be(clientUserId, "the server must persist the user message under the client-supplied id so retry/edit can reference it");
    }

    [Fact]
    public async Task HandleRunAsync_ClientSuppliesNonGuidId_GeneratesServerSideId()
    {
        const string threadId = "conv-badid";
        const string userId = "user-badid";

        var mediator = new Mock<IMediator>();
        var store = new Mock<IConversationStore>();
        var appended = new List<ConversationMessage>();

        store.Setup(s => s.GetAsync(threadId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(MakeRecord(threadId, userId));
        store.Setup(s => s.GetHistoryForDispatch(threadId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync([]);
        store.Setup(s => s.AppendMessageAsync(threadId, It.IsAny<ConversationMessage>(), It.IsAny<CancellationToken>()))
             .Callback<string, ConversationMessage, CancellationToken>((_, m, _) => appended.Add(m))
             .Returns(Task.CompletedTask);
        mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(MakeSuccessResult("reply"));

        var handler = BuildHandler(mediator, store);

        using var ms = new MemoryStream();
        await handler.HandleRunAsync(
            MakeInput(threadId, "Hi", "not-a-guid"), new AgUiEventWriter(ms), MakeUser(userId));

        var userMsg = appended.Single(m => m.Role == MessageRole.User);
        userMsg.Id.Should().NotBe(Guid.Empty, "a non-GUID client id must fall back to a server-generated id");
    }

    [Fact]
    public async Task HandleRunAsync_AssistantMessage_StreamedIdMatchesPersistedId()
    {
        const string threadId = "conv-asstid";
        const string userId = "user-asstid";

        var mediator = new Mock<IMediator>();
        var store = new Mock<IConversationStore>();
        var appended = new List<ConversationMessage>();

        store.Setup(s => s.GetAsync(threadId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(MakeRecord(threadId, userId));
        store.Setup(s => s.GetHistoryForDispatch(threadId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync([]);
        store.Setup(s => s.AppendMessageAsync(threadId, It.IsAny<ConversationMessage>(), It.IsAny<CancellationToken>()))
             .Callback<string, ConversationMessage, CancellationToken>((_, m, _) => appended.Add(m))
             .Returns(Task.CompletedTask);
        mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(MakeSuccessResult("assistant reply"));

        var handler = BuildHandler(mediator, store);

        using var ms = new MemoryStream();
        await handler.HandleRunAsync(MakeInput(threadId, "Hi"), new AgUiEventWriter(ms), MakeUser(userId));

        var frames = ParseSseFrames(ms);
        var streamedId = frames.First(f => EventType(f) == AgUiEventType.TextMessageStart)
                               .RootElement.GetProperty("messageId").GetString();
        var persistedAssistant = appended.Single(m => m.Role == MessageRole.Assistant);

        persistedAssistant.Id.ToString().Should().Be(
            streamedId, "the streamed assistant message id must equal the persisted id so retry-from-assistant resolves");
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

    [Fact]
    public async Task HandleRunAsync_FirstTurn_CreatesSessionAndPersistsMetrics()
    {
        const string threadId = "conv-telemetry-1";
        const string userId = "user-tel-1";
        const string agentResponse = "Here are your tools.";

        var mediator = new Mock<IMediator>();
        var store = new Mock<IConversationStore>();
        var observability = new Mock<IObservabilityStore>();
        var sessionId = Guid.NewGuid();

        var record = MakeRecord(threadId, userId);
        store.Setup(s => s.GetAsync(threadId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(record);
        store.Setup(s => s.GetHistoryForDispatch(threadId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync([]);
        store.Setup(s => s.AppendMessageAsync(threadId, It.IsAny<ConversationMessage>(), It.IsAny<CancellationToken>()))
             .Returns(Task.CompletedTask);
        store.Setup(s => s.UpdateTelemetryAsync(threadId, It.IsAny<Guid>(), It.IsAny<TelemetryAccumulator>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(record);

        observability.Setup(o => o.StartSessionAsync(threadId, "test-agent", null, It.IsAny<CancellationToken>()))
                     .ReturnsAsync(sessionId);

        mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(MakeSuccessResultWithUsage(agentResponse, inputTokens: 100, outputTokens: 50, costUsd: 0.01m));

        var handler = BuildHandler(mediator, store, observability);
        var input = MakeInput(threadId, "What tools do you have?");
        var user = MakeUser(userId);

        using var ms = new MemoryStream();
        var writer = new AgUiEventWriter(ms);

        await handler.HandleRunAsync(input, writer, user);

        // Session was started in the observability store
        observability.Verify(o => o.StartSessionAsync(threadId, "test-agent", null, It.IsAny<CancellationToken>()), Times.Once);

        // Session metrics were updated with non-zero values
        observability.Verify(o => o.UpdateSessionMetricsAsync(
            sessionId,
            1, 0, 0,
            100, 50,
            It.IsAny<int>(), It.IsAny<int>(),
            0.01m,
            It.IsAny<decimal>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // Telemetry was persisted to conversation store (twice: once for Zero on session start, once after turn)
        store.Verify(s => s.UpdateTelemetryAsync(
            threadId, sessionId,
            It.IsAny<TelemetryAccumulator>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task HandleRunAsync_SecondTurn_ReusesSessionAndAccumulatesMetrics()
    {
        const string threadId = "conv-telemetry-2";
        const string userId = "user-tel-2";
        var sessionId = Guid.NewGuid();

        var mediator = new Mock<IMediator>();
        var store = new Mock<IConversationStore>();
        var observability = new Mock<IObservabilityStore>();

        var existingTelemetry = new TelemetryAccumulator(1, 0, 100, 50, 40, 10, 0.01m);
        var record = new ConversationRecord(
            threadId, "test-agent", userId,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new ConversationMessage(Guid.NewGuid(), MessageRole.User, "first msg", DateTimeOffset.UtcNow),
             new ConversationMessage(Guid.NewGuid(), MessageRole.Assistant, "first reply", DateTimeOffset.UtcNow)],
            "first msg", null, sessionId, existingTelemetry);

        store.Setup(s => s.GetAsync(threadId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(record);
        store.Setup(s => s.GetHistoryForDispatch(threadId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(record.Messages);
        store.Setup(s => s.AppendMessageAsync(threadId, It.IsAny<ConversationMessage>(), It.IsAny<CancellationToken>()))
             .Returns(Task.CompletedTask);
        store.Setup(s => s.UpdateTelemetryAsync(threadId, sessionId, It.IsAny<TelemetryAccumulator>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(record);

        mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(MakeSuccessResultWithUsage("second reply", inputTokens: 200, outputTokens: 100,
                    cacheRead: 60, cacheWrite: 20, costUsd: 0.02m, tools: ["file_system"]));

        var handler = BuildHandler(mediator, store, observability);
        var input = MakeInput(threadId, "Use a tool please");
        var user = MakeUser(userId);

        using var ms = new MemoryStream();
        var writer = new AgUiEventWriter(ms);

        await handler.HandleRunAsync(input, writer, user);

        // Should NOT start a new session
        observability.Verify(o => o.StartSessionAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

        // Session metrics should be accumulated (turn1 + turn2)
        observability.Verify(o => o.UpdateSessionMetricsAsync(
            sessionId,
            2, 1, 0,
            300, 150, 100, 30,
            0.03m,
            It.IsAny<decimal>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // Telemetry persisted once (only after turn, no session start)
        store.Verify(s => s.UpdateTelemetryAsync(
            threadId, sessionId,
            It.Is<TelemetryAccumulator>(t => t.TurnCount == 2 && t.ToolCallCount == 1),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
