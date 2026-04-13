using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Infrastructure.AI.Tests.Helpers;

/// <summary>
/// A concrete AIAgent subclass for testing. Overrides the abstract core methods
/// so tests can control agent behavior without external dependencies.
/// </summary>
public sealed class TestableAIAgent : AIAgent
{
    private readonly Func<IEnumerable<ChatMessage>, CancellationToken, Task<AgentResponse>> _runHandler;

    public TestableAIAgent(string responseText)
        : this(_ => new AgentResponse(new ChatMessage(ChatRole.Assistant, responseText)))
    {
    }

    public TestableAIAgent(Func<IEnumerable<ChatMessage>, AgentResponse> handler)
        : this((msgs, _) => Task.FromResult(handler(msgs)))
    {
    }

    public TestableAIAgent(Func<IEnumerable<ChatMessage>, CancellationToken, Task<AgentResponse>> handler)
    {
        _runHandler = handler;
    }

    /// <summary>Creates a TestableAIAgent that delays before returning, for parallelism tests.</summary>
    public static TestableAIAgent WithDelay(string responseText, TimeSpan delay)
    {
        return new TestableAIAgent(async (_, ct) =>
        {
            await Task.Delay(delay, ct);
            return new AgentResponse(new ChatMessage(ChatRole.Assistant, responseText));
        });
    }

    /// <summary>Creates a TestableAIAgent that throws the given exception on RunAsync.</summary>
    public static TestableAIAgent Throwing(Exception exception)
        => new TestableAIAgent((_, _) => throw exception);

    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        CancellationToken cancellationToken)
        => _runHandler(messages, cancellationToken);

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await _runHandler(messages, cancellationToken);
        yield return new AgentResponseUpdate(ChatRole.Assistant, response.Text ?? string.Empty);
    }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult<AgentSession>(new TestableAgentSession());

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(JsonDocument.Parse("{}").RootElement);

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions,
        CancellationToken cancellationToken)
        => ValueTask.FromResult<AgentSession>(new TestableAgentSession());
}
