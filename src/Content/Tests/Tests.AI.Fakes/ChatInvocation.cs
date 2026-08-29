namespace Tests.AI.Fakes;

/// <summary>
/// One recorded call into a <see cref="RecordingChatClient"/> — the agent role that made it, the
/// shape of what it sent, and when.
/// </summary>
/// <param name="AgentId">
/// The <c>IAgentExecutionContext.AgentId</c> active when the call was made, or <c>null</c> if no
/// agent context was in scope.
/// </param>
/// <param name="MessageCount">The number of messages in the request.</param>
/// <param name="HadResponseFormat">
/// Whether the caller populated <c>ChatOptions.ResponseFormat</c> — the signal a structured-output
/// contract (see the invoker built for #323) attaches a schema to the request. Recording this is
/// what makes a repair round-trip assertable end-to-end: a scenario can assert not just that the
/// final output is correct, but that the retry attempt actually requested the schema again.
/// </param>
/// <param name="ElapsedFromStart">
/// Time since the log was created, sourced from the <see cref="TimeProvider"/> the client was
/// constructed with — never wall-clock, so tests are deterministic under a fake time provider.
/// </param>
public sealed record ChatInvocation(
    string? AgentId,
    int MessageCount,
    bool HadResponseFormat,
    TimeSpan ElapsedFromStart);

/// <summary>
/// Ordered, thread-safe log of every call made across every <see cref="RecordingChatClient"/>
/// instance sharing this log. Registered singleton so it survives the scoped
/// <see cref="ScriptedChatClientFactory"/> lifetime described on that type — a test asserting an
/// invocation sequence needs one log for the whole run, not one per DI scope.
/// </summary>
public sealed class ChatInvocationLog
{
    private readonly List<ChatInvocation> _invocations = [];
    private readonly Lock _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly long _startTimestamp;

    /// <summary>Creates a log whose <see cref="ChatInvocation.ElapsedFromStart"/> values are
    /// measured from construction time using <paramref name="timeProvider"/>.</summary>
    public ChatInvocationLog(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _startTimestamp = _timeProvider.GetTimestamp();
    }

    /// <summary>All invocations recorded so far, in call order.</summary>
    public IReadOnlyList<ChatInvocation> Invocations
    {
        get { lock (_gate) return [.. _invocations]; }
    }

    /// <summary>The ordered sequence of <see cref="ChatInvocation.AgentId"/> values recorded so far.</summary>
    public IReadOnlyList<string?> RoleSequence => [.. Invocations.Select(i => i.AgentId)];

    /// <summary>Counts how many times a given agent id was invoked.</summary>
    public int CountFor(string? agentId) => Invocations.Count(i => i.AgentId == agentId);

    /// <summary>Records one invocation. Thread-safe — parallel tool calls may invoke concurrently.</summary>
    internal void Record(string? agentId, int messageCount, bool hadResponseFormat)
    {
        var elapsed = _timeProvider.GetElapsedTime(_startTimestamp);
        lock (_gate) _invocations.Add(new ChatInvocation(agentId, messageCount, hadResponseFormat, elapsed));
    }
}
