using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Traces;
using Application.AI.Common.Middleware;
using Application.AI.Common.Services;
using Domain.Common.MetaHarness;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Middleware;

/// <summary>
/// Tests for <see cref="ToolDiagnosticsMiddleware"/> covering trace appending for
/// function results, error resilience, tool deduplication, tool logging,
/// tool call response logging, response preview, streaming path, and null logger guard.
/// </summary>
public sealed class ToolDiagnosticsMiddlewareTests
{
    private static Mock<IChatClient> MakeChatClient(ChatResponse? response = null)
    {
        var mock = new Mock<IChatClient>();
        mock.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(response ?? new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));
        mock.Setup(c => c.GetService(It.IsAny<Type>())).Returns(null!);
        return mock;
    }

    private static Mock<IChatClient> MakeStreamingChatClient(params ChatResponseUpdate[] chunks)
    {
        var mock = new Mock<IChatClient>();
        mock.Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns(chunks.ToAsyncEnumerable());
        mock.Setup(c => c.GetService(It.IsAny<Type>())).Returns(null!);
        return mock;
    }

    private static (Mock<ITraceWriter> Writer, ToolDiagnosticsMiddleware Middleware)
        MakeMiddlewareWithWriter(Mock<IChatClient> innerClient)
    {
        var scope = TraceScope.ForExecution(Guid.NewGuid());
        var writerMock = new Mock<ITraceWriter>();
        writerMock.Setup(w => w.Scope).Returns(scope);
        writerMock
            .Setup(w => w.AppendTraceAsync(It.IsAny<ExecutionTraceRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var middleware = new ToolDiagnosticsMiddleware(
            innerClient.Object,
            NullLogger<ToolDiagnosticsMiddleware>.Instance,
            traceWriter: writerMock.Object);

        return (writerMock, middleware);
    }

    // --- Constructor validation ---

    [Fact]
    public void Ctor_NullLogger_ThrowsArgumentNull()
    {
        var innerClient = new Mock<IChatClient>().Object;

        var act = () => new ToolDiagnosticsMiddleware(innerClient, null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // --- Trace appending ---

    [Fact]
    public async Task InvokeNext_WhenFunctionResultsInMessages_AppendsTraceRecord()
    {
        var innerClient = MakeChatClient();
        var (writerMock, middleware) = MakeMiddlewareWithWriter(innerClient);

        var messages = new ChatMessage[]
        {
            new(ChatRole.Tool, [new FunctionResultContent("call-1", result: "file content")])
        };

        await middleware.GetResponseAsync(messages, null, CancellationToken.None);

        writerMock.Verify(
            w => w.AppendTraceAsync(
                It.Is<ExecutionTraceRecord>(r => r.Type == TraceRecordTypes.ToolResult),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task InvokeNext_FunctionResultHasException_TraceRecordDoesNotContainRawExceptionText()
    {
        // FunctionInvokingChatClient's IncludeDetailedErrors option (set unconditionally by
        // AgentFactory) bakes Exception.Message verbatim into Result — this trace record feeds the
        // dashboard's per-invocation page via ToolInvocationDetailDto, an exposure point just as real
        // as the streamed SSE frame ExecuteAgentTurnCommandHandler.RedactedResultForStreaming sanitizes.
        var innerClient = MakeChatClient();
        var (writerMock, middleware) = MakeMiddlewareWithWriter(innerClient);

        var failure = new FunctionResultContent("call-1", "Error: Function failed. Exception: /etc/shadow not found")
        {
            Exception = new InvalidOperationException("/etc/shadow not found")
        };
        var messages = new ChatMessage[] { new(ChatRole.Tool, [failure]) };

        await middleware.GetResponseAsync(messages, null, CancellationToken.None);

        writerMock.Verify(
            w => w.AppendTraceAsync(
                It.Is<ExecutionTraceRecord>(r => r.PayloadSummary != null && !r.PayloadSummary.Contains("/etc/shadow")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task InvokeNext_FunctionResultHasException_TraceRecordCategoryIsError()
    {
        // SafeResultText strips the raw exception text (the only signal a reader previously had that
        // the call failed) out of PayloadSummary — ResultCategory must carry that signal structurally
        // instead of silently staying hard-coded Success for a call that actually failed.
        var innerClient = MakeChatClient();
        var (writerMock, middleware) = MakeMiddlewareWithWriter(innerClient);

        var failure = new FunctionResultContent("call-1", "irrelevant")
        {
            Exception = new InvalidOperationException("boom")
        };
        var messages = new ChatMessage[] { new(ChatRole.Tool, [failure]) };

        await middleware.GetResponseAsync(messages, null, CancellationToken.None);

        writerMock.Verify(
            w => w.AppendTraceAsync(
                It.Is<ExecutionTraceRecord>(r => r.ResultCategory == TraceResultCategories.Error),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task InvokeNext_FunctionResultSucceeded_TraceRecordCategoryIsSuccess()
    {
        var innerClient = MakeChatClient();
        var (writerMock, middleware) = MakeMiddlewareWithWriter(innerClient);

        var messages = new ChatMessage[] { new(ChatRole.Tool, [new FunctionResultContent("call-1", "42 results")]) };

        await middleware.GetResponseAsync(messages, null, CancellationToken.None);

        writerMock.Verify(
            w => w.AppendTraceAsync(
                It.Is<ExecutionTraceRecord>(r => r.ResultCategory == TraceResultCategories.Success),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task InvokeNext_AppendTraceThrows_DoesNotRethrow()
    {
        var innerClient = MakeChatClient();
        var scope = TraceScope.ForExecution(Guid.NewGuid());
        var writerMock = new Mock<ITraceWriter>();
        writerMock.Setup(w => w.Scope).Returns(scope);
        writerMock
            .Setup(w => w.AppendTraceAsync(It.IsAny<ExecutionTraceRecord>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk full"));

        var middleware = new ToolDiagnosticsMiddleware(
            innerClient.Object,
            NullLogger<ToolDiagnosticsMiddleware>.Instance,
            traceWriter: writerMock.Object);

        var messages = new ChatMessage[]
        {
            new(ChatRole.Tool, [new FunctionResultContent("call-1", result: "content")])
        };

        var act = () => middleware.GetResponseAsync(messages, null, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task InvokeNext_NoFunctionResultsInMessages_DoesNotCallAppendTrace()
    {
        var innerClient = MakeChatClient();
        var (writerMock, middleware) = MakeMiddlewareWithWriter(innerClient);

        var messages = new ChatMessage[]
        {
            new(ChatRole.User, "What is the weather?")
        };

        await middleware.GetResponseAsync(messages, null, CancellationToken.None);

        writerMock.Verify(
            w => w.AppendTraceAsync(It.IsAny<ExecutionTraceRecord>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task InvokeNext_WithoutTraceWriter_DoesNotThrow()
    {
        var innerClient = MakeChatClient();
        var middleware = new ToolDiagnosticsMiddleware(
            innerClient.Object,
            NullLogger<ToolDiagnosticsMiddleware>.Instance);

        var messages = new ChatMessage[]
        {
            new(ChatRole.Tool, [new FunctionResultContent("call-1", result: "content")])
        };

        var act = () => middleware.GetResponseAsync(messages, null, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task InvokeNext_ResultCallIdInReplayedScope_DoesNotCallAppendTrace()
    {
        var innerClient = MakeChatClient();
        var (writerMock, middleware) = MakeMiddlewareWithWriter(innerClient);

        var messages = new ChatMessage[]
        {
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "42 results")])
        };

        ReplayedToolCallScope.Current = new ReplayedToolCallSet(["call-1"]);
        try
        {
            await middleware.GetResponseAsync(messages, null, CancellationToken.None);
        }
        finally
        {
            ReplayedToolCallScope.Current = null;
        }

        writerMock.Verify(
            w => w.AppendTraceAsync(It.IsAny<ExecutionTraceRecord>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task InvokeNext_ArmedWithCollidingCallId_StillRecordsUsageCaptureEvenThoughTraceIsSuppressed()
    {
        // #512: the armed path used to let one TryClaim decide both outputs together, so a colliding
        // call id (already present in ReplayedToolCallScope.Current, seeded from replayed history or
        // an earlier round of this same turn) silently dropped usage-capture along with the trace —
        // even though RecordToolResult is a dictionary upsert keyed by CallId, safe to call more than
        // once for the same id. This proves the split: usage-capture must still see the collision, the
        // trace-suppression test above proves the trace still correctly does not.
        var innerClient = MakeChatClient();
        var (writerMock, middleware) = MakeMiddlewareWithWriter(innerClient);

        var usageCapture = new Mock<ILlmUsageCapture>();
        LlmUsageCapture.Current = usageCapture.Object;

        var messages = new ChatMessage[]
        {
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "42 results")])
        };

        ReplayedToolCallScope.Current = new ReplayedToolCallSet(["call-1"]);
        try
        {
            await middleware.GetResponseAsync(messages, null, CancellationToken.None);
        }
        finally
        {
            ReplayedToolCallScope.Current = null;
            LlmUsageCapture.Current = null;
        }

        usageCapture.Verify(
            c => c.RecordToolResult("call-1", It.IsAny<string>()),
            Times.Once,
            "a call id collision on the armed path must still reach usage-capture — only the trace " +
            "write should be suppressed for a duplicate");
        writerMock.Verify(
            w => w.AppendTraceAsync(It.IsAny<ExecutionTraceRecord>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the trace itself must still be suppressed — this test is not proving #512 undid the " +
            "original dedup, only that it stopped taking usage-capture down with it");
    }

    [Fact]
    public async Task InvokeNext_ResultCallIdNotInReplayedScope_StillCallsAppendTrace()
    {
        var innerClient = MakeChatClient();
        var (writerMock, middleware) = MakeMiddlewareWithWriter(innerClient);

        var messages = new ChatMessage[]
        {
            new(ChatRole.Tool, [new FunctionResultContent("call-2", "42 results")])
        };

        ReplayedToolCallScope.Current = new ReplayedToolCallSet(["call-1"]);
        try
        {
            await middleware.GetResponseAsync(messages, null, CancellationToken.None);
        }
        finally
        {
            ReplayedToolCallScope.Current = null;
        }

        writerMock.Verify(
            w => w.AppendTraceAsync(It.IsAny<ExecutionTraceRecord>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task InvokeNext_SecondRoundOfSameTurnRescansFirstRoundsResult_DoesNotReRecordIt()
    {
        // Simulates how FunctionInvokingChatClient actually calls this middleware: once per model
        // round-trip, each time with the full, growing inbound message list — round 2 still contains
        // round 1's result. Both rounds share one ReplayedToolCallScope.Current instance, the same way
        // ExecuteAgentTurnCommandHandler seeds it once per turn, not once per round.
        var innerClient = MakeChatClient();
        var (writerMock, middleware) = MakeMiddlewareWithWriter(innerClient);

        var roundOneMessages = new ChatMessage[]
        {
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "first result")])
        };
        var roundTwoMessages = new ChatMessage[]
        {
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "first result")]),
            new(ChatRole.Tool, [new FunctionResultContent("call-2", "second result")])
        };

        ReplayedToolCallScope.Current = new ReplayedToolCallSet();
        try
        {
            await middleware.GetResponseAsync(roundOneMessages, null, CancellationToken.None);
            await middleware.GetResponseAsync(roundTwoMessages, null, CancellationToken.None);
        }
        finally
        {
            ReplayedToolCallScope.Current = null;
        }

        // call-1 recorded once (round 1), not again when round 2's scan re-encounters it; call-2
        // recorded once (round 2, genuinely new).
        writerMock.Verify(
            w => w.AppendTraceAsync(
                It.Is<ExecutionTraceRecord>(r => r.TurnId == "call-1"), It.IsAny<CancellationToken>()),
            Times.Once);
        writerMock.Verify(
            w => w.AppendTraceAsync(
                It.Is<ExecutionTraceRecord>(r => r.TurnId == "call-2"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task InvokeNext_SecondRoundWithNoAmbientScope_StillDoesNotReRecordFirstRoundsResult()
    {
        // The same shape as the test above, with the ambient scope deliberately absent. This is not
        // a hypothetical configuration: ExecuteAgentTurnCommandHandler is the only production armer,
        // so AgentEvaluationService, RunOrchestratedTaskCommandHandler and Presentation.FoundryHost
        // all reach this middleware unarmed — and AgentEvaluationService is precisely the path #505's
        // trace wiring exists to serve. Without the per-instance fallback the writer receives call-1
        // twice, so switching that path on would have replaced an empty traces.jsonl with one whose
        // tool counts are inflated once per model round: a worse failure than the one being fixed,
        // and one that reads as real data.
        var innerClient = MakeChatClient();
        var (writerMock, middleware) = MakeMiddlewareWithWriter(innerClient);

        var roundOneMessages = new ChatMessage[]
        {
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "first result")])
        };
        var roundTwoMessages = new ChatMessage[]
        {
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "first result")]),
            new(ChatRole.Tool, [new FunctionResultContent("call-2", "second result")])
        };

        ReplayedToolCallScope.Current.Should().BeNull("this test is only meaningful unarmed");

        await middleware.GetResponseAsync(roundOneMessages, null, CancellationToken.None);
        await middleware.GetResponseAsync(roundTwoMessages, null, CancellationToken.None);

        writerMock.Verify(
            w => w.AppendTraceAsync(
                It.Is<ExecutionTraceRecord>(r => r.TurnId == "call-1"), It.IsAny<CancellationToken>()),
            Times.Once);
        writerMock.Verify(
            w => w.AppendTraceAsync(
                It.Is<ExecutionTraceRecord>(r => r.TurnId == "call-2"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task InvokeNext_TwoMiddlewareInstances_DoNotShareClaims()
    {
        // The per-instance fallback must not become a process-wide filter. One instance is built per
        // chat client per agent construction, so a second run legitimately re-records a call id the
        // first run already saw — provider connectors that number call ids per-turn and reset them
        // make that a real collision, not a contrived one (see #512).
        var (firstWriter, firstMiddleware) = MakeMiddlewareWithWriter(MakeChatClient());
        var (secondWriter, secondMiddleware) = MakeMiddlewareWithWriter(MakeChatClient());

        var messages = new ChatMessage[]
        {
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "result")])
        };

        await firstMiddleware.GetResponseAsync(messages, null, CancellationToken.None);
        await secondMiddleware.GetResponseAsync(messages, null, CancellationToken.None);

        firstWriter.Verify(
            w => w.AppendTraceAsync(
                It.Is<ExecutionTraceRecord>(r => r.TurnId == "call-1"), It.IsAny<CancellationToken>()),
            Times.Once);
        secondWriter.Verify(
            w => w.AppendTraceAsync(
                It.Is<ExecutionTraceRecord>(r => r.TurnId == "call-1"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task InvokeNext_ProcessLivedInstanceExceedsTheFallbackBound_OldestClaimBecomesReclaimable()
    {
        // The correctness gate's own failing case, driven through the public surface rather than
        // ReplayedToolCallSet's own tests: Presentation.FoundryHost builds one middleware instance for
        // the whole process, unarmed (it never runs through ExecuteAgentTurnCommandHandler), so this is
        // the exact shape a long-lived deployment produces. Before the bound existed, "call-0" here
        // would be refused forever once claimed once. 10,001 rather than 10,000 specifically to force
        // one eviction, not merely fill the set to capacity.
        const int overCapacityBy = 1;
        var innerClient = MakeChatClient();
        var (writerMock, middleware) = MakeMiddlewareWithWriter(innerClient);

        ReplayedToolCallScope.Current.Should().BeNull("this test is only meaningful unarmed");

        var firstRound = Enumerable.Range(0, ToolDiagnosticsMiddleware.MaxFallbackClaimEntries + overCapacityBy)
            .Select(i => new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"call-{i}", "result")]))
            .ToArray();
        await middleware.GetResponseAsync(firstRound, null, CancellationToken.None);

        // call-0 is the oldest claim and must have fallen out of the bounded window; every id from
        // this same round is recorded once regardless (a first claim never checks the bound, only
        // eviction after does), so this is not yet observable from round one's call count alone.
        var secondRound = new ChatMessage[]
        {
            new(ChatRole.Tool, [new FunctionResultContent("call-0", "reclaimed after eviction")])
        };
        await middleware.GetResponseAsync(secondRound, null, CancellationToken.None);

        writerMock.Verify(
            w => w.AppendTraceAsync(
                It.Is<ExecutionTraceRecord>(r => r.TurnId == "call-0"), It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "call-0 was recorded once in round one as a genuinely new claim, then evicted by the " +
            "10,001st claim in that same round, then recorded again in round two as a legitimately " +
            "new claim — a process-lived instance must not refuse the second recording forever");
    }

    [Fact]
    public async Task InvokeNext_UnarmedWithNoTraceWriter_UsageCaptureIsNeverGatedByTheFallbackSet()
    {
        // The exact hole the local grader gate found on the previous version of this fix: the
        // fallback dedup, meant to protect trace-write eligibility, was accidentally gating
        // LlmUsageCapture too — a per-request AsyncLocal capture that this fix had no reason to
        // touch at all. Before #505, an unarmed caller with no trace writer recorded every
        // candidate into usage-capture unconditionally, every round; this proves that is still
        // true after the fix, not merely for one round but across the repeated-id shape a real
        // multi-round turn produces.
        var innerClient = MakeChatClient();
        var middleware = new ToolDiagnosticsMiddleware(
            innerClient.Object, NullLogger<ToolDiagnosticsMiddleware>.Instance);
        // No traceWriter, matching an unarmed host with ExecutionTracingEnabled off — the shipped
        // default this claim is specifically about.

        var usageCapture = new Mock<ILlmUsageCapture>();
        LlmUsageCapture.Current = usageCapture.Object;
        try
        {
            ReplayedToolCallScope.Current.Should().BeNull("this test is only meaningful unarmed");

            var roundOne = new ChatMessage[]
            {
                new(ChatRole.Tool, [new FunctionResultContent("call-1", "first result")])
            };
            var roundTwo = new ChatMessage[]
            {
                new(ChatRole.Tool, [new FunctionResultContent("call-1", "first result")]),
                new(ChatRole.Tool, [new FunctionResultContent("call-2", "second result")])
            };

            await middleware.GetResponseAsync(roundOne, null, CancellationToken.None);
            await middleware.GetResponseAsync(roundTwo, null, CancellationToken.None);

            usageCapture.Verify(
                c => c.RecordToolResult("call-1", It.IsAny<string>()),
                Times.Exactly(2),
                "call-1 appears in both rounds' cumulative inbound list, and usage-capture must see " +
                "it both times — the trace-only fallback dedup must never suppress this, unarmed or " +
                "not, tracing on or off");
            usageCapture.Verify(
                c => c.RecordToolResult("call-2", It.IsAny<string>()),
                Times.Once);
        }
        finally
        {
            LlmUsageCapture.Current = null;
        }
    }

    [Fact]
    public async Task InvokeNext_UnarmedWithNoTraceWriter_NeverConsumesTheFallbackClaimSet()
    {
        // The other half of the same fix, and what makes the "#541 is dormant unless
        // ExecutionTracingEnabled is on" claim true by construction rather than merely asserted: a
        // host with tracing off must never touch _intraRunToolCallClaims at all.
        //
        // Asserted directly on FallbackClaimCount rather than inferred from an absence of effect.
        // Nothing downstream ever reads a claim this set makes on an untraced instance — the
        // `_traceWriter is null` check that already gates every trace-append would suppress output
        // regardless of whether the set were consulted first, so a correctness-only assertion (every
        // id still reaches usage-capture) cannot distinguish "consumed but harmless" from "never
        // touched": both look identical from outside. Confirmed by measurement, not assumed — the
        // first version of this test asserted only the correctness-only shape and passed unchanged
        // when the `_traceWriter is not null &&` short-circuit was removed from the claim step.
        var innerClient = MakeChatClient();
        var middleware = new ToolDiagnosticsMiddleware(
            innerClient.Object, NullLogger<ToolDiagnosticsMiddleware>.Instance);

        var manyRounds = Enumerable.Range(0, ToolDiagnosticsMiddleware.MaxFallbackClaimEntries + 10)
            .Select(i => new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"call-{i}", "result")]))
            .ToArray();

        await middleware.GetResponseAsync(manyRounds, null, CancellationToken.None);

        middleware.FallbackClaimCount.Should().Be(0,
            "an untraced instance must never touch its own fallback claim set — there is nothing " +
            "downstream that would ever consult a claim made here, so any consumption is pure waste " +
            "at best and, on a process-lived FoundryHost instance, unnecessary lock contention with " +
            "every other concurrent request at worst");
    }

    // --- Tool deduplication ---

    [Fact]
    public async Task GetResponseAsync_DuplicateToolNames_DeduplicatesBeforeSendingToInner()
    {
        ChatOptions? capturedOptions = null;
        var innerClient = new Mock<IChatClient>();
        innerClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((_, opts, _) => capturedOptions = opts)
            .ReturnsAsync(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));
        innerClient.Setup(c => c.GetService(It.IsAny<Type>())).Returns(null!);

        var middleware = new ToolDiagnosticsMiddleware(
            innerClient.Object,
            NullLogger<ToolDiagnosticsMiddleware>.Instance);

        var tool1 = AIFunctionFactory.Create(() => "a", "my_tool");
        var tool2 = AIFunctionFactory.Create(() => "b", "my_tool");
        var tool3 = AIFunctionFactory.Create(() => "c", "other_tool");
        var options = new ChatOptions { Tools = [tool1, tool2, tool3] };

        await middleware.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options);

        capturedOptions.Should().NotBeNull();
        capturedOptions!.Tools.Should().HaveCount(2);
        capturedOptions.Tools.Select(t => t.Name).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task GetResponseAsync_NoTools_DoesNotDeduplicateOrFail()
    {
        var innerClient = MakeChatClient();
        var middleware = new ToolDiagnosticsMiddleware(
            innerClient.Object,
            NullLogger<ToolDiagnosticsMiddleware>.Instance);

        var act = () => middleware.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], null);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task GetResponseAsync_SingleTool_NoDeduplicationNeeded()
    {
        ChatOptions? capturedOptions = null;
        var innerClient = new Mock<IChatClient>();
        innerClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((_, opts, _) => capturedOptions = opts)
            .ReturnsAsync(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));
        innerClient.Setup(c => c.GetService(It.IsAny<Type>())).Returns(null!);

        var middleware = new ToolDiagnosticsMiddleware(
            innerClient.Object,
            NullLogger<ToolDiagnosticsMiddleware>.Instance);

        var tool = AIFunctionFactory.Create(() => "a", "my_tool");
        var options = new ChatOptions { Tools = [tool] };

        await middleware.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options);

        capturedOptions.Should().NotBeNull();
        capturedOptions!.Tools.Should().HaveCount(1);
    }

    // --- Tool call logging in response ---

    [Fact]
    public async Task GetResponseAsync_ResponseWithToolCalls_LogsToolCallInfo()
    {
        var logger = new Mock<ILogger<ToolDiagnosticsMiddleware>>();
        var response = new ChatResponse(
        [
            new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent("call-1", "search_tool")
            ])
        ]);
        var innerClient = MakeChatClient(response);

        var middleware = new ToolDiagnosticsMiddleware(
            innerClient.Object,
            logger.Object);

        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create(() => "ok", "search_tool")]
        };

        await middleware.GetResponseAsync([new ChatMessage(ChatRole.User, "search")], options);

        logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) => o.ToString()!.Contains("search_tool")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task GetResponseAsync_NoToolCallsButToolsConfigured_LogsWarning()
    {
        var logger = new Mock<ILogger<ToolDiagnosticsMiddleware>>();
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "I'll just respond with text.")]);
        var innerClient = MakeChatClient(response);

        var middleware = new ToolDiagnosticsMiddleware(
            innerClient.Object,
            logger.Object);

        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create(() => "ok", "available_tool")]
        };

        await middleware.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options);

        logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) => o.ToString()!.Contains("No tool calls")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task GetResponseAsync_NoToolCallsNoToolsConfigured_LogsDebug()
    {
        var logger = new Mock<ILogger<ToolDiagnosticsMiddleware>>();
        logger.Setup(l => l.IsEnabled(LogLevel.Debug)).Returns(true);
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "Just text")]);
        var innerClient = MakeChatClient(response);

        var middleware = new ToolDiagnosticsMiddleware(
            innerClient.Object,
            logger.Object);

        await middleware.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], null);

        logger.Verify(
            l => l.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) => o.ToString()!.Contains("generation-only")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    // --- Redactor integration ---

    [Fact]
    public async Task InvokeNext_WithRedactor_RedactsPayloadBeforeTracing()
    {
        var innerClient = MakeChatClient();
        var scope = TraceScope.ForExecution(Guid.NewGuid());
        var writerMock = new Mock<ITraceWriter>();
        writerMock.Setup(w => w.Scope).Returns(scope);
        writerMock
            .Setup(w => w.AppendTraceAsync(It.IsAny<ExecutionTraceRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var redactor = new Mock<ISecretRedactor>();
        redactor.Setup(r => r.Redact(It.IsAny<string>())).Returns("[REDACTED]");

        var middleware = new ToolDiagnosticsMiddleware(
            innerClient.Object,
            NullLogger<ToolDiagnosticsMiddleware>.Instance,
            traceWriter: writerMock.Object,
            redactor: redactor.Object);

        var messages = new ChatMessage[]
        {
            new(ChatRole.Tool, [new FunctionResultContent("call-1", result: "secret data")])
        };

        await middleware.GetResponseAsync(messages, null, CancellationToken.None);

        writerMock.Verify(
            w => w.AppendTraceAsync(
                It.Is<ExecutionTraceRecord>(r => r.PayloadSummary == "[REDACTED]"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // --- Multiple function results ---

    [Fact]
    public async Task InvokeNext_MultipleFunctionResults_AppendsTraceForEach()
    {
        var innerClient = MakeChatClient();
        var (writerMock, middleware) = MakeMiddlewareWithWriter(innerClient);

        var messages = new ChatMessage[]
        {
            new(ChatRole.Tool,
            [
                new FunctionResultContent("call-1", result: "result-1"),
                new FunctionResultContent("call-2", result: "result-2")
            ])
        };

        await middleware.GetResponseAsync(messages, null, CancellationToken.None);

        writerMock.Verify(
            w => w.AppendTraceAsync(It.IsAny<ExecutionTraceRecord>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    // --- Streaming path ---

    [Fact]
    public async Task GetStreamingResponseAsync_YieldsAllChunks()
    {
        var chunk1 = new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("Hello")] };
        var chunk2 = new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent(" world")] };
        var innerClient = MakeStreamingChatClient(chunk1, chunk2);

        var middleware = new ToolDiagnosticsMiddleware(
            innerClient.Object,
            NullLogger<ToolDiagnosticsMiddleware>.Instance);

        var received = new List<ChatResponseUpdate>();
        await foreach (var chunk in middleware.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
        {
            received.Add(chunk);
        }

        received.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_DeduplicatesTools()
    {
        ChatOptions? capturedOptions = null;
        var chunks = new[] { new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("ok")] } };
        var innerClient = new Mock<IChatClient>();
        innerClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((_, opts, _) => capturedOptions = opts)
            .Returns(chunks.ToAsyncEnumerable());
        innerClient.Setup(c => c.GetService(It.IsAny<Type>())).Returns(null!);

        var middleware = new ToolDiagnosticsMiddleware(
            innerClient.Object,
            NullLogger<ToolDiagnosticsMiddleware>.Instance);

        var tool1 = AIFunctionFactory.Create(() => "a", "search");
        var tool2 = AIFunctionFactory.Create(() => "b", "search");
        var options = new ChatOptions { Tools = [tool1, tool2] };

        await foreach (var _ in middleware.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], options))
        {
            // consume
        }

        capturedOptions.Should().NotBeNull();
        capturedOptions!.Tools.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WithTraceWriter_AppendsFunctionResultTraces()
    {
        var chunk = new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("ok")] };
        var innerClient = MakeStreamingChatClient(chunk);
        var (writerMock, middleware) = MakeMiddlewareWithWriter(innerClient);

        var messages = new ChatMessage[]
        {
            new(ChatRole.Tool, [new FunctionResultContent("call-1", result: "tool output")])
        };

        await foreach (var _ in middleware.GetStreamingResponseAsync(messages))
        {
            // consume
        }

        writerMock.Verify(
            w => w.AppendTraceAsync(
                It.Is<ExecutionTraceRecord>(r => r.Type == TraceRecordTypes.ToolResult),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WithoutTraceWriter_DoesNotThrow()
    {
        var chunk = new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("ok")] };
        var innerClient = MakeStreamingChatClient(chunk);

        var middleware = new ToolDiagnosticsMiddleware(
            innerClient.Object,
            NullLogger<ToolDiagnosticsMiddleware>.Instance);

        var messages = new ChatMessage[]
        {
            new(ChatRole.Tool, [new FunctionResultContent("call-1", result: "content")])
        };

        var act = async () =>
        {
            await foreach (var _ in middleware.GetStreamingResponseAsync(messages))
            {
                // consume
            }
        };

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task GetStreamingResponseAsync_LogsToolsInOptions()
    {
        var logger = new Mock<ILogger<ToolDiagnosticsMiddleware>>();
        var chunk = new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("ok")] };
        var innerClient = MakeStreamingChatClient(chunk);

        var middleware = new ToolDiagnosticsMiddleware(
            innerClient.Object,
            logger.Object);

        var tool = AIFunctionFactory.Create(() => "a", "my_streaming_tool");
        var options = new ChatOptions { Tools = [tool] };

        await foreach (var _ in middleware.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], options))
        {
            // consume
        }

        // Should log that tools were configured for streaming
        logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) =>
                    o.ToString()!.Contains("GetStreamingResponseAsync") &&
                    o.ToString()!.Contains("1")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }
}
