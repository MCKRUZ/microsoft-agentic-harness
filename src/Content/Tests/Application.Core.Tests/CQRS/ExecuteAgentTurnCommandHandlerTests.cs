using Application.AI.Common.Categorization;
using Application.AI.Common.Helpers;
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
            Mock.Of<Application.AI.Common.Interfaces.Governance.IToolCallAdmissionPipeline>(
                p => p.GetTrace() == Domain.AI.Governance.GovernanceTrace.Empty),
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
    public async Task Handle_ActiveStreamSink_ToolCallActivity_EmitsInOrderAroundText()
    {
        // Arrange — text, then a tool call, then its result, then more text; the sink's tool-call
        // methods must fire in order and interleaved correctly with the text deltas.
        var agent = TestableAIAgent.StreamingContent(
            [new TextContent("Looking that up")],
            [new FunctionCallContent("call-1", "search", new Dictionary<string, object?> { ["q"] = "docs" })],
            [new FunctionResultContent("call-1", "42 results")],
            [new TextContent(" — found it.")]);
        _agentCache
            .Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);

        var events = new List<string>();
        Application.AI.Common.Services.AgentTurnStreamSink.Current =
            new Application.AI.Common.Services.AgentTurnStreamSink(
                onDelta: (delta, _) => { events.Add($"delta:{delta}"); return Task.CompletedTask; },
                onToolCall: (id, name, args, _) => { events.Add($"call:{id}:{name}:{args.Json}"); return Task.CompletedTask; },
                onToolCallResult: (id, result, _) => { events.Add($"result:{id}:{result}"); return Task.CompletedTask; });

        try
        {
            // Act
            var result = await _handler.Handle(CreateCommand(), CancellationToken.None);

            // Assert
            events.Should().Equal(
                "delta:Looking that up",
                "call:call-1:search:{\"q\":\"docs\"}",
                "result:call-1:42 results",
                "delta: — found it.");
            result.Success.Should().BeTrue();
        }
        finally
        {
            Application.AI.Common.Services.AgentTurnStreamSink.Current = null;
        }
    }

    [Fact]
    public async Task Handle_ActiveStreamSink_DuplicateFunctionCallContentForSameCallId_EmitsToolCallStartOnce()
    {
        // A provider connector surfacing the same FunctionCallContent twice for one CallId (a
        // duplicate update, a retry-shaped delta) must not double-emit a TOOL_CALL_START/ARGS triple
        // to the client — startedCallIds.Add's bool return gates the second occurrence.
        var duplicateCall = new FunctionCallContent("call-1", "search", new Dictionary<string, object?> { ["q"] = "docs" });
        var agent = TestableAIAgent.StreamingContent([duplicateCall], [duplicateCall]);
        _agentCache
            .Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);

        var callStarts = 0;
        Application.AI.Common.Services.AgentTurnStreamSink.Current =
            new Application.AI.Common.Services.AgentTurnStreamSink(
                onDelta: (_, _) => Task.CompletedTask,
                onToolCall: (_, _, _, _) => { callStarts++; return Task.CompletedTask; });

        try
        {
            // Act
            await _handler.Handle(CreateCommand(), CancellationToken.None);

            // Assert
            callStarts.Should().Be(1);
        }
        finally
        {
            Application.AI.Common.Services.AgentTurnStreamSink.Current = null;
        }
    }

    /// <summary>A value that throws when JSON-serialized, to prove a bad tool argument degrades the
    /// streamed args to a warning rather than aborting the whole turn.</summary>
    private sealed class UnserializableArgValue
    {
        public string Poison => throw new InvalidOperationException("cannot serialize this value");
    }

    [Fact]
    public async Task Handle_ActiveStreamSink_UnserializableToolArgs_DoesNotAbortTheTurn()
    {
        // A tool call whose Arguments dictionary holds a value System.Text.Json can't serialize must
        // degrade gracefully (like ToolDiagnosticsMiddleware's identical serialize call does), not
        // throw out of the streaming loop and lose text already streamed to the client.
        var agent = TestableAIAgent.StreamingContent(
            [new TextContent("Looking that up")],
            [new FunctionCallContent("call-1", "search", new Dictionary<string, object?> { ["bad"] = new UnserializableArgValue() })],
            [new TextContent(" — done.")]);
        _agentCache
            .Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);

        StreamedToolCallArguments toolCallArgs = default;
        Application.AI.Common.Services.AgentTurnStreamSink.Current =
            new Application.AI.Common.Services.AgentTurnStreamSink(
                onDelta: (_, _) => Task.CompletedTask,
                onToolCall: (_, _, args, _) => { toolCallArgs = args; return Task.CompletedTask; });

        try
        {
            // Act
            var result = await _handler.Handle(CreateCommand(), CancellationToken.None);

            // Assert — the turn still completes and streams the surrounding text; the tool call is
            // reported with a safe fallback instead of the handler throwing. Withheld must be true —
            // "{}" here is a degraded fallback, not the tool's real (empty) arguments, and a client
            // must be able to tell the two apart (a prior version of this code set Withheld: false
            // on this exact path).
            result.Success.Should().BeTrue();
            result.Response.Should().Be("Looking that up — done.");
            toolCallArgs.Json.Should().Be("{}");
            toolCallArgs.Withheld.Should().BeTrue();
        }
        finally
        {
            Application.AI.Common.Services.AgentTurnStreamSink.Current = null;
        }
    }

    [Fact]
    public async Task Handle_ActiveStreamSink_LongToolCallArgs_AreNotTruncated()
    {
        // BundleToolCallArgsEvent.Delta is documented as always carrying the complete JSON payload.
        // Truncating it at the same preview-length cap used for log strings would hand a client
        // invalid, unparseable JSON — arguments must be redacted only, never truncated. 600 chars is
        // well under the streaming size ceiling (16 KB), so this must stream whole and un-withheld.
        var longValue = new string('x', 600);
        var agent = TestableAIAgent.StreamingContent(
            [new FunctionCallContent("call-1", "search", new Dictionary<string, object?> { ["q"] = longValue })]);
        _agentCache
            .Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);

        StreamedToolCallArguments toolCallArgs = default;
        Application.AI.Common.Services.AgentTurnStreamSink.Current =
            new Application.AI.Common.Services.AgentTurnStreamSink(
                onDelta: (_, _) => Task.CompletedTask,
                onToolCall: (_, _, args, _) => { toolCallArgs = args; return Task.CompletedTask; });

        try
        {
            // Act
            await _handler.Handle(CreateCommand(), CancellationToken.None);

            // Assert
            toolCallArgs.Withheld.Should().BeFalse();
            toolCallArgs.Json.Length.Should().BeGreaterThan(500);
            var act = () => System.Text.Json.JsonDocument.Parse(toolCallArgs.Json);
            act.Should().NotThrow("the streamed args must remain valid, complete JSON");
        }
        finally
        {
            Application.AI.Common.Services.AgentTurnStreamSink.Current = null;
        }
    }

    [Fact]
    public async Task Handle_ActiveStreamSink_OversizedToolCallArgs_StreamsWithheldPlaceholder_NotRawPayload()
    {
        // Above ToolPayloadRedactor.MaxStreamedToolCallArgsLength (16 KB), the real arguments must
        // never reach the client — withheld whole (Json="{}", Withheld=true), not truncated.
        var oversizedValue = new string('x', ToolPayloadRedactor.MaxStreamedToolCallArgsLength + 1);
        var agent = TestableAIAgent.StreamingContent(
            [new FunctionCallContent("call-1", "search", new Dictionary<string, object?> { ["q"] = oversizedValue })]);
        _agentCache
            .Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);

        StreamedToolCallArguments toolCallArgs = default;
        Application.AI.Common.Services.AgentTurnStreamSink.Current =
            new Application.AI.Common.Services.AgentTurnStreamSink(
                onDelta: (_, _) => Task.CompletedTask,
                onToolCall: (_, _, args, _) => { toolCallArgs = args; return Task.CompletedTask; });

        try
        {
            // Act
            await _handler.Handle(CreateCommand(), CancellationToken.None);

            // Assert
            toolCallArgs.Withheld.Should().BeTrue();
            toolCallArgs.Json.Should().Be("{}");
            toolCallArgs.Json.Should().NotContain("x");
        }
        finally
        {
            Application.AI.Common.Services.AgentTurnStreamSink.Current = null;
        }
    }

    [Fact]
    public async Task Handle_ActiveStreamSink_ToolResultWithNoCallId_IsNotStreamed()
    {
        // A FunctionResultContent with no CallId can never be matched back to a TOOL_CALL_START, so
        // streaming it would only produce an orphaned frame a client cannot place.
        var agent = TestableAIAgent.StreamingContent(
            [new FunctionResultContent(string.Empty, "orphaned result")]);
        _agentCache
            .Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);

        var resultFired = false;
        Application.AI.Common.Services.AgentTurnStreamSink.Current =
            new Application.AI.Common.Services.AgentTurnStreamSink(
                onDelta: (_, _) => Task.CompletedTask,
                onToolCallResult: (_, _, _) => { resultFired = true; return Task.CompletedTask; });

        try
        {
            // Act
            await _handler.Handle(CreateCommand(), CancellationToken.None);

            // Assert
            resultFired.Should().BeFalse();
        }
        finally
        {
            Application.AI.Common.Services.AgentTurnStreamSink.Current = null;
        }
    }

    [Fact]
    public async Task Handle_ActiveStreamSink_ToolCallWithNoCallId_IsNotStreamed()
    {
        // A FunctionCallContent with a valid Name but no CallId used to stream anyway (the guard only
        // checked Name), producing a frame with an empty toolCallId that violates the wire contract's
        // required field. Guard on both now, mirroring the FunctionResultContent guard below it.
        var agent = TestableAIAgent.StreamingContent(
            [new FunctionCallContent(string.Empty, "search", new Dictionary<string, object?> { ["q"] = "docs" })]);
        _agentCache
            .Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);

        var callFired = false;
        Application.AI.Common.Services.AgentTurnStreamSink.Current =
            new Application.AI.Common.Services.AgentTurnStreamSink(
                onDelta: (_, _) => Task.CompletedTask,
                onToolCall: (_, _, _, _) => { callFired = true; return Task.CompletedTask; });

        try
        {
            // Act
            await _handler.Handle(CreateCommand(), CancellationToken.None);

            // Assert
            callFired.Should().BeFalse();
        }
        finally
        {
            Application.AI.Common.Services.AgentTurnStreamSink.Current = null;
        }
    }

    [Fact]
    public async Task Handle_ActiveStreamSink_CallWithEmptyNameButValidCallId_ResultIsAlsoNotStreamed()
    {
        // The asymmetry this guards against: a call skipped for having no Name (so no TOOL_CALL_START
        // ever fires) must not let its later result — which shares the same, valid CallId — stream
        // anyway. Before the fix, only the call side was skipped; the result side's independent
        // CallId-only guard let it through, producing a TOOL_CALL_RESULT with no preceding START.
        var agent = TestableAIAgent.StreamingContent(
            [new FunctionCallContent("call-1", string.Empty, new Dictionary<string, object?> { ["q"] = "docs" })],
            [new FunctionResultContent("call-1", "should not stream either")]);
        _agentCache
            .Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);

        var events = new List<string>();
        Application.AI.Common.Services.AgentTurnStreamSink.Current =
            new Application.AI.Common.Services.AgentTurnStreamSink(
                onDelta: (_, _) => Task.CompletedTask,
                onToolCall: (id, name, args, _) => { events.Add($"call:{id}:{name}:{args.Json}"); return Task.CompletedTask; },
                onToolCallResult: (id, result, _) => { events.Add($"result:{id}:{result}"); return Task.CompletedTask; });

        try
        {
            // Act
            await _handler.Handle(CreateCommand(), CancellationToken.None);

            // Assert — the call is skipped (empty Name) and its result must be too, since streaming it
            // alone would be an orphaned TOOL_CALL_RESULT with no preceding TOOL_CALL_START.
            events.Should().BeEmpty();
        }
        finally
        {
            Application.AI.Common.Services.AgentTurnStreamSink.Current = null;
        }
    }

    [Fact]
    public async Task Handle_ActiveStreamSink_ToolCallArgsAndResult_AreRedactedBeforeStreaming()
    {
        // The same payloads ToolDiagnosticsMiddleware redacts before persisting must also be
        // redacted before they reach a live SSE client — this handler is a second exposure point
        // for the identical sensitive data.
        var agent = TestableAIAgent.StreamingContent(
            [new FunctionCallContent("call-1", "search", new Dictionary<string, object?> { ["token"] = "secret-value" })],
            [new FunctionResultContent("call-1", "secret-value")]);
        _agentCache
            .Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);

        var redactor = new Mock<ISecretRedactor>();
        redactor.Setup(r => r.Redact(It.Is<string>(s => s.Contains("secret-value"))))
            .Returns<string>(s => s.Replace("secret-value", "[REDACTED]"));
        var handlerWithRedactor = new ExecuteAgentTurnCommandHandler(
            _agentCache.Object,
            Mock.Of<Application.AI.Common.Interfaces.Governance.IToolCallAdmissionPipeline>(
                p => p.GetTrace() == Domain.AI.Governance.GovernanceTrace.Empty),
            _agentRegistry.Object,
            new Mock<ISkillMetadataRegistry>().Object,
            new Application.AI.Common.Services.Context.ConversationRegistrationTracker(),
            new Mock<IObservabilityStore>().Object,
            Mock.Of<ILlmUsageCapture>(c => c.TakeSnapshot() == new LlmUsageSnapshot(0, 0, 0, 0, null, 0m, 0m, Array.Empty<string>())),
            new DefaultContextSnapshotComputer(),
            new NullContextSnapshotNotifier(),
            TimeProvider.System,
            NullLogger<ExecuteAgentTurnCommandHandler>.Instance,
            redactor.Object);

        var args = string.Empty;
        var toolResult = string.Empty;
        Application.AI.Common.Services.AgentTurnStreamSink.Current =
            new Application.AI.Common.Services.AgentTurnStreamSink(
                onDelta: (_, _) => Task.CompletedTask,
                onToolCall: (_, _, a, _) => { args = a.Json; return Task.CompletedTask; },
                onToolCallResult: (_, r, _) => { toolResult = r; return Task.CompletedTask; });

        try
        {
            // Act
            await handlerWithRedactor.Handle(CreateCommand(), CancellationToken.None);

            // Assert
            args.Should().NotContain("secret-value").And.Contain("[REDACTED]");
            toolResult.Should().Be("[REDACTED]");
            // TOOL_CALL_ARGS.delta is documented as always-complete, parseable JSON — a redactor that
            // corrupts the surrounding structure (e.g. a greedy value matcher, see
            // PatternSecretRedactorTests.Redact_SecretKeywordInsideJsonStringValue_DoesNotConsumeRestOfDocument
            // in Infrastructure.AI.Tests for the concrete regex bug this guards against) would break
            // this contract silently.
            var act = () => System.Text.Json.JsonDocument.Parse(args);
            act.Should().NotThrow();
        }
        finally
        {
            Application.AI.Common.Services.AgentTurnStreamSink.Current = null;
        }
    }

    [Fact]
    public async Task Handle_ActiveStreamSink_ToolCallFailed_StreamsGenericMessageNotRawException()
    {
        // FunctionInvokingChatClient's IncludeDetailedErrors option (set unconditionally by
        // AgentFactory) bakes Exception.Message verbatim into FunctionResultContent.Result — e.g.
        // "Error: Function failed. Exception: could not open '/etc/shadow'". That text can carry file
        // paths, connection details, or other internals that must never reach a browser, so a failed
        // call streams a generic message instead of the raw Result text.
        var failure = new FunctionResultContent("call-1", "Error: Function failed. Exception: /etc/shadow not found")
        {
            Exception = new InvalidOperationException("/etc/shadow not found")
        };
        var agent = TestableAIAgent.StreamingContent(
            [new FunctionCallContent("call-1", "read_file", new Dictionary<string, object?> { ["path"] = "x" })],
            [failure]);
        _agentCache
            .Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);

        var toolResult = string.Empty;
        Application.AI.Common.Services.AgentTurnStreamSink.Current =
            new Application.AI.Common.Services.AgentTurnStreamSink(
                onDelta: (_, _) => Task.CompletedTask,
                onToolCall: (_, _, _, _) => Task.CompletedTask,
                onToolCallResult: (_, r, _) => { toolResult = r; return Task.CompletedTask; });

        try
        {
            // Act
            await _handler.Handle(CreateCommand(), CancellationToken.None);

            // Assert
            toolResult.Should().NotContain("/etc/shadow");
            toolResult.Should().NotBeEmpty();
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
