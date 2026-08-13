using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Application.Core.Tests.Helpers;

/// <summary>
/// A concrete AIAgent subclass for testing. Overrides the abstract core methods
/// so tests can control agent behavior without external dependencies.
/// </summary>
public sealed class TestableAIAgent : AIAgent
{
    private readonly Func<IEnumerable<ChatMessage>, CancellationToken, Task<AgentResponse>> _runHandler;
    private readonly IReadOnlyList<IReadOnlyList<AIContent>>? _streamingContentUpdates;

    public TestableAIAgent(string responseText)
        : this(_ => new AgentResponse(new ChatMessage(ChatRole.Assistant, responseText)))
    {
    }

    public TestableAIAgent(Func<IEnumerable<ChatMessage>, AgentResponse> handler)
        : this((msgs, _) => Task.FromResult(handler(msgs)))
    {
    }

    public TestableAIAgent(Func<IEnumerable<ChatMessage>, CancellationToken, Task<AgentResponse>> handler)
        : this(handler, streamingContentUpdates: null)
    {
    }

    private TestableAIAgent(
        Func<IEnumerable<ChatMessage>, CancellationToken, Task<AgentResponse>> handler,
        IReadOnlyList<IReadOnlyList<AIContent>>? streamingContentUpdates)
    {
        _runHandler = handler;
        _streamingContentUpdates = streamingContentUpdates;
    }

    /// <summary>
    /// Creates a TestableAIAgent that emits the given <paramref name="chunks"/> as distinct
    /// streaming updates (and their concatenation for the blocking path), so streaming
    /// callers can assert per-delta emission. A thin wrapper over <see cref="StreamingContent"/>:
    /// each chunk becomes a single-item <see cref="TextContent"/> update.
    /// </summary>
    public static TestableAIAgent Streaming(params string[] chunks) =>
        StreamingContent([.. chunks.Select(c => (IReadOnlyList<AIContent>)[new TextContent(c)])]);

    /// <summary>
    /// Creates a TestableAIAgent whose streaming path yields exactly the given content-bearing
    /// updates, in order — e.g. a text delta, then a <see cref="FunctionCallContent"/>, then a
    /// <see cref="FunctionResultContent"/> — so tests can simulate tool-call activity interleaved
    /// with text mid-stream. The blocking path concatenates every <see cref="TextContent"/> across
    /// all updates.
    /// </summary>
    public static TestableAIAgent StreamingContent(params IReadOnlyList<AIContent>[] updates)
    {
        var full = string.Concat(updates.SelectMany(u => u).OfType<TextContent>().Select(t => t.Text));
        return new TestableAIAgent(
            (_, _) => Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, full))),
            streamingContentUpdates: updates);
    }

    /// <summary>
    /// Creates a TestableAIAgent that throws the specified exception on RunAsync.
    /// </summary>
    public static TestableAIAgent Throwing(Exception exception)
    {
        return new TestableAIAgent((_, _) => throw exception);
    }

    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        CancellationToken cancellationToken)
    {
        return _runHandler(messages, cancellationToken);
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_streamingContentUpdates is not null)
        {
            // Surface configured failures (e.g. Throwing) on the streaming path too.
            await _runHandler(messages, cancellationToken);
            foreach (var contents in _streamingContentUpdates)
                yield return new AgentResponseUpdate(ChatRole.Assistant, contents.ToList());
            yield break;
        }

        var response = await _runHandler(messages, cancellationToken);
        yield return new AgentResponseUpdate(ChatRole.Assistant, response.Text ?? string.Empty);
    }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult<AgentSession>(new TestableAgentSession());
    }

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions,
        CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(JsonDocument.Parse("{}").RootElement);
    }

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions,
        CancellationToken cancellationToken)
    {
        return ValueTask.FromResult<AgentSession>(new TestableAgentSession());
    }
}
