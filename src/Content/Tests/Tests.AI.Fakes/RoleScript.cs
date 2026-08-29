using Microsoft.Extensions.AI;

namespace Tests.AI.Fakes;

/// <summary>
/// The scripted responses for one agent role: a FIFO queue drained on each call, falling back to
/// a default once exhausted. Shared and mutated by every <see cref="RecordingChatClient"/> built
/// for the same role across repeated <c>IChatClientFactory.GetChatClientAsync</c> calls, so a
/// scenario's queue state survives agent reconstruction within one test.
/// </summary>
public sealed class RoleScript
{
    private readonly Queue<ScriptedItem> _responses = new();
    private ChatResponse _defaultResponse = new(new ChatMessage(ChatRole.Assistant, "fake response"));

    /// <summary>Sets the response returned once the queue is empty.</summary>
    public RoleScript WithDefaultResponse(string content)
    {
        _defaultResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, content));
        return this;
    }

    /// <summary>Enqueues a plain-text response.</summary>
    public RoleScript Enqueue(string content)
    {
        _responses.Enqueue(ScriptedItem.Of(new ChatResponse(new ChatMessage(ChatRole.Assistant, content))));
        return this;
    }

    /// <summary>
    /// Enqueues a response with usage metadata, for tests asserting per-call token accounting
    /// (e.g. <c>LlmUsageCapture.LastCallPromptTokens</c>).
    /// </summary>
    public RoleScript EnqueueWithUsage(string content, int inputTokens, int outputTokens)
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, content))
        {
            Usage = new UsageDetails
            {
                InputTokenCount = inputTokens,
                OutputTokenCount = outputTokens,
                TotalTokenCount = inputTokens + outputTokens,
            },
        };
        _responses.Enqueue(ScriptedItem.Of(response));
        return this;
    }

    /// <summary>
    /// Enqueues text that is deliberately not valid JSON — for scripting the "malformed, then
    /// repaired" half of a structured-output repair-round-trip scenario.
    /// </summary>
    public RoleScript EnqueueMalformed(string malformedJson = "{ this is not valid json") =>
        Enqueue(malformedJson);

    /// <summary>Enqueues a response carrying no content at all, for the empty-body abort path.</summary>
    public RoleScript EnqueueEmpty() => Enqueue(string.Empty);

    /// <summary>Enqueues a response whose assistant message carries a single tool call.</summary>
    public RoleScript EnqueueToolCall(string toolName, string callId, IDictionary<string, object?>? arguments = null)
    {
        var message = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent(callId, toolName, arguments ?? new Dictionary<string, object?>()),
        });
        _responses.Enqueue(ScriptedItem.Of(new ChatResponse(message)));
        return this;
    }

    /// <summary>Enqueues a response that always throws when requested, for total-failure scenarios.</summary>
    public RoleScript EnqueueThrow(Exception exception)
    {
        _responses.Enqueue(ScriptedItem.Throw(exception));
        return this;
    }

    /// <summary>Dequeues the next scripted response, or the default if the queue is empty. Throws
    /// the scripted exception, if one was enqueued, instead of returning.</summary>
    internal ChatResponse Next()
    {
        if (_responses.Count == 0) return _defaultResponse;
        var item = _responses.Dequeue();
        return item.Exception is { } exception ? throw exception : item.ResponseValue!;
    }

    /// <summary>
    /// A queued item is either a canned response or an exception to throw — kept as a plain
    /// discriminated struct rather than subclassing <see cref="ChatResponse"/>, whose sealed-ness
    /// is not part of its documented contract and should not be assumed.
    /// </summary>
    private readonly record struct ScriptedItem(ChatResponse? ResponseValue, Exception? Exception)
    {
        public static ScriptedItem Of(ChatResponse response) => new(response, null);
        public static ScriptedItem Throw(Exception exception) => new(null, exception);
    }
}
