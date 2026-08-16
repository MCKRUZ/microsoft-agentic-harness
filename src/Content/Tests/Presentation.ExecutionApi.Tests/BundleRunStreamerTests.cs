using System.Text;
using System.Text.Json;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Bundles;
using Application.AI.Common.Services;
using Domain.AI.Bundles;
using FluentAssertions;
using Presentation.ExecutionApi.Streaming;
using Xunit;

namespace Presentation.ExecutionApi.Tests;

/// <summary>
/// Tests for <see cref="BundleRunStreamer"/> — the event sequence it writes, the load-bearing guarantee that
/// the assistant-text sink is armed for the duration of the run and cleared afterward, and that failures are
/// surfaced as caller-safe messages rather than raw internal detail.
/// </summary>
public sealed class BundleRunStreamerTests
{
    /// <summary>A fake executor that records whether the ambient sink was armed, optionally drives it through
    /// a caller-supplied script (deltas and/or tool-call events, in any order), then returns a configured
    /// outcome.</summary>
    private sealed class FakeExecutor : IBundleRunExecutor
    {
        private readonly BundleRunExecution _result;
        private readonly Func<IAgentTurnStreamSink, CancellationToken, Task> _script;

        public bool SinkArmedDuringRun { get; private set; }

        public FakeExecutor(BundleRunExecution result, params string[] deltas)
            : this(result, async (sink, ct) =>
            {
                foreach (var delta in deltas)
                    await sink.EmitAsync(delta, ct);
            })
        {
        }

        public FakeExecutor(BundleRunExecution result, Func<IAgentTurnStreamSink, CancellationToken, Task> script)
        {
            _result = result;
            _script = script;
        }

        public async Task<BundleRunExecution> ExecuteAsync(string jobId, CancellationToken cancellationToken)
        {
            var sink = AgentTurnStreamSink.Current;
            SinkArmedDuringRun = sink is not null;
            if (sink is not null)
                await _script(sink, cancellationToken);
            return _result;
        }
    }

    private static BundleRunRecord Record(
        BundleRunStatus status = BundleRunStatus.Queued,
        bool conversationSucceeded = true,
        DateTimeOffset? startedAt = null,
        string? error = null) => new()
    {
        JobId = "job-1",
        Handle = "handle-1",
        OwnerId = "owner-1",
        AgentName = "agent-1",
        UserMessages = ["hi"],
        MaxTurns = 3,
        Envelope = new CapabilityEnvelope(),
        Status = status,
        Streaming = true,
        StartedAt = startedAt,
        Error = error,
        CreatedAt = DateTimeOffset.UnixEpoch,
        Outcome = status == BundleRunStatus.Succeeded
            ? new BundleRunOutcome
            {
                ConversationSucceeded = conversationSucceeded,
                FinalResponse = "done",
                TurnCount = 1,
                TotalToolInvocations = 0
            }
            : null
    };

    private static async Task<IReadOnlyList<JsonElement>> RunAndParseAsync(FakeExecutor executor, BundleRunRecord record)
    {
        using var stream = new MemoryStream();
        using (var writer = new BundleStreamEventWriter(stream))
        {
            var streamer = new BundleRunStreamer(executor);
            await streamer.StreamAsync(record, writer, CancellationToken.None);
        }

        return ParseFrames(Encoding.UTF8.GetString(stream.ToArray()));
    }

    /// <summary>Parses an SSE body into its event JSON objects, in order.</summary>
    private static IReadOnlyList<JsonElement> ParseFrames(string body) => body
        .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
        .Select(chunk => chunk.Trim())
        .Where(chunk => chunk.StartsWith("data: ", StringComparison.Ordinal))
        .Select(chunk => JsonDocument.Parse(chunk["data: ".Length..]).RootElement.Clone())
        .ToList();

    private static string Type(JsonElement e) => e.GetProperty("type").GetString()!;

    [Fact]
    public async Task StreamAsync_SuccessWithText_EmitsRunStarted_OneMessage_RunFinished()
    {
        var executor = new FakeExecutor(BundleRunExecution.Ran(Record(BundleRunStatus.Succeeded)), "Hello", " world");

        var frames = await RunAndParseAsync(executor, Record());

        frames.Select(Type).Should().Equal(
            "RUN_STARTED", "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_CONTENT",
            "TEXT_MESSAGE_END", "RUN_FINISHED");

        // Every text frame shares one messageId, and the deltas arrive verbatim and in order.
        var messageIds = frames.Where(f => Type(f).StartsWith("TEXT_MESSAGE", StringComparison.Ordinal))
            .Select(f => f.GetProperty("messageId").GetString()).Distinct().ToList();
        messageIds.Should().HaveCount(1);
        frames.Where(f => Type(f) == "TEXT_MESSAGE_CONTENT").Select(f => f.GetProperty("delta").GetString())
            .Should().Equal("Hello", " world");
    }

    [Fact]
    public async Task StreamAsync_NoText_SkipsMessageFrames()
    {
        var executor = new FakeExecutor(BundleRunExecution.Ran(Record(BundleRunStatus.Succeeded)));

        var frames = await RunAndParseAsync(executor, Record());

        frames.Select(Type).Should().Equal("RUN_STARTED", "RUN_FINISHED");
    }

    [Fact]
    public async Task StreamAsync_ToolCallActivity_EmitsInterleavedWithText()
    {
        var executor = new FakeExecutor(BundleRunExecution.Ran(Record(BundleRunStatus.Succeeded)), async (sink, ct) =>
        {
            await sink.EmitAsync("Looking that up", ct);
            await sink.EmitToolCallAsync("call-1", "search", new StreamedToolCallArguments("{\"q\":\"docs\"}", false), ct);
            await sink.EmitToolCallResultAsync("call-1", "42 results", ct);
            await sink.EmitAsync(" — found it.", ct);
        });

        var frames = await RunAndParseAsync(executor, Record());

        frames.Select(Type).Should().Equal(
            "RUN_STARTED", "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT",
            "TOOL_CALL_START", "TOOL_CALL_ARGS", "TOOL_CALL_END", "TOOL_CALL_RESULT",
            "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END", "RUN_FINISHED");

        var start = frames.Single(f => Type(f) == "TOOL_CALL_START");
        start.GetProperty("toolCallId").GetString().Should().Be("call-1");
        start.GetProperty("toolCallName").GetString().Should().Be("search");

        frames.Single(f => Type(f) == "TOOL_CALL_ARGS").GetProperty("delta").GetString()
            .Should().Be("{\"q\":\"docs\"}");
        frames.Single(f => Type(f) == "TOOL_CALL_END").GetProperty("toolCallId").GetString()
            .Should().Be("call-1");
        frames.Single(f => Type(f) == "TOOL_CALL_RESULT").GetProperty("result").GetString()
            .Should().Be("42 results");
    }

    [Fact]
    public async Task StreamAsync_ToolCallBeforeAnyText_DoesNotOpenAStrayTextMessage()
    {
        var executor = new FakeExecutor(BundleRunExecution.Ran(Record(BundleRunStatus.Succeeded)), async (sink, ct) =>
        {
            await sink.EmitToolCallAsync("call-1", "search", new StreamedToolCallArguments("{}", false), ct);
        });

        var frames = await RunAndParseAsync(executor, Record());

        frames.Select(Type).Should().Equal(
            "RUN_STARTED", "TOOL_CALL_START", "TOOL_CALL_ARGS", "TOOL_CALL_END", "RUN_FINISHED");
    }

    /// <summary>
    /// A withheld tool-call args emission (real arguments exceeded the streaming size ceiling) sets
    /// the wire frame's <c>withheld</c> flag and streams the placeholder, never the raw payload —
    /// #392.
    /// </summary>
    [Fact]
    public async Task StreamAsync_OversizedToolCallArgs_EmitsWithheldFrame_NotRawPayload()
    {
        var executor = new FakeExecutor(BundleRunExecution.Ran(Record(BundleRunStatus.Succeeded)), async (sink, ct) =>
        {
            await sink.EmitToolCallAsync("call-1", "search", new StreamedToolCallArguments("{}", true), ct);
        });

        var frames = await RunAndParseAsync(executor, Record());

        var argsFrame = frames.Single(f => Type(f) == "TOOL_CALL_ARGS");
        argsFrame.GetProperty("delta").GetString().Should().Be("{}");
        argsFrame.GetProperty("withheld").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task StreamAsync_ArmsSinkDuringRun_AndClearsAfterward()
    {
        var executor = new FakeExecutor(BundleRunExecution.Ran(Record(BundleRunStatus.Succeeded)), "x");

        await RunAndParseAsync(executor, Record());

        executor.SinkArmedDuringRun.Should().BeTrue("assistant deltas must flow to the stream during the run");
        AgentTurnStreamSink.Current.Should().BeNull("the sink must never leak past the request");
    }

    [Fact]
    public async Task StreamAsync_RanButFailed_EmitsRunError_WithScrubbedMessage()
    {
        // A run that started then failed carries an internal scrubbed code — it must NOT reach the client.
        var failed = Record(BundleRunStatus.Failed, startedAt: DateTimeOffset.UnixEpoch,
            error: "bundle_run.unhandled_exception");
        var executor = new FakeExecutor(BundleRunExecution.Ran(failed));

        var frames = await RunAndParseAsync(executor, Record());

        Type(frames[^1]).Should().Be("RUN_ERROR");
        var message = frames[^1].GetProperty("message").GetString()!;
        message.Should().Be("The agent run did not complete successfully.");
        message.Should().NotContain("bundle_run");
    }

    [Fact]
    public async Task StreamAsync_HandleExpired_EmitsRunError_ExpiredMessage()
    {
        // Failed with no start time == the handle expired before the run began.
        var expired = Record(BundleRunStatus.Failed, startedAt: null, error: "The bundle handle expired ...");
        var executor = new FakeExecutor(BundleRunExecution.Ran(expired));

        var frames = await RunAndParseAsync(executor, Record());

        frames.Select(Type).Should().Equal("RUN_STARTED", "RUN_ERROR");
        frames[^1].GetProperty("message").GetString().Should().Be(
            "The bundle handle expired before the run could start.");
    }

    [Fact]
    public async Task StreamAsync_ConversationReportedFailure_EmitsRunError()
    {
        // The run reached Succeeded, but the conversation itself reported failure — not a clean success.
        var executor = new FakeExecutor(
            BundleRunExecution.Ran(Record(BundleRunStatus.Succeeded, conversationSucceeded: false)));

        var frames = await RunAndParseAsync(executor, Record());

        Type(frames[^1]).Should().Be("RUN_ERROR");
    }

    [Fact]
    public async Task StreamAsync_AlreadyClaimed_EmitsRunError()
    {
        var executor = new FakeExecutor(BundleRunExecution.AlreadyClaimed(Record(BundleRunStatus.Running)));

        var frames = await RunAndParseAsync(executor, Record());

        Type(frames[^1]).Should().Be("RUN_ERROR");
        frames[^1].GetProperty("message").GetString().Should().Contain("already being streamed");
    }
}
