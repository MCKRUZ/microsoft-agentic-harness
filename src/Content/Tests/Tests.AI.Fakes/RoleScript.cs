using Microsoft.Extensions.AI;

namespace Tests.AI.Fakes;

/// <summary>
/// The scripted responses for one agent role: a FIFO queue drained on each call, falling back to
/// a default once exhausted. Shared and mutated by every <see cref="RecordingChatClient"/> built
/// for the same role across repeated <c>IChatClientFactory.GetChatClientAsync</c> calls, so a
/// scenario's queue state survives agent reconstruction within one test.
/// </summary>
/// <remarks>
/// Thread-safe — <c>AgentFactory</c> sets <c>AllowConcurrentInvocation = true</c> for tool-bearing
/// turns, and plan steps run with bounded concurrency, so two calls against the same role's script
/// can race in a real scenario. Every mutating/reading operation takes <see cref="_gate"/>.
/// </remarks>
public sealed class RoleScript
{
    private readonly Lock _gate = new();
    private readonly Queue<ScriptedItem> _responses = new();
    private string _defaultResponseText = "fake response";

    /// <summary>Sets the text returned (as a freshly built response) once the queue is empty.</summary>
    public RoleScript WithDefaultResponse(string content)
    {
        lock (_gate) _defaultResponseText = content;
        return this;
    }

    /// <summary>Enqueues a plain-text response.</summary>
    public RoleScript Enqueue(string content)
    {
        lock (_gate) _responses.Enqueue(ScriptedItem.Of(new ChatResponse(new ChatMessage(ChatRole.Assistant, content))));
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
        lock (_gate) _responses.Enqueue(ScriptedItem.Of(response));
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
        lock (_gate) _responses.Enqueue(ScriptedItem.Of(new ChatResponse(message)));
        return this;
    }

    /// <summary>
    /// Enqueues an exception to be thrown the one time this item is dequeued — for scripting a
    /// single failed call within a larger sequence (e.g. "attempt one throws, attempt two
    /// succeeds"). This is <em>not</em> sticky: once dequeued, later calls proceed to whatever is
    /// enqueued next, or the default response if the queue is now empty. For a call that must fail
    /// every time it is invoked, enqueue this repeatedly or use <see cref="AlwaysThrow"/>.
    /// </summary>
    public RoleScript EnqueueThrow(Exception exception)
    {
        lock (_gate) _responses.Enqueue(ScriptedItem.Throw(exception));
        return this;
    }

    /// <summary>
    /// Makes every call against this role throw <paramref name="exception"/>, including calls made
    /// after the queue is otherwise exhausted — for a genuine total-failure scenario, where a
    /// single <see cref="EnqueueThrow"/> would let a later, unscripted call fall through to the
    /// default success response.
    /// </summary>
    public RoleScript AlwaysThrow(Exception exception)
    {
        lock (_gate) _alwaysThrow = exception;
        return this;
    }

    private Exception? _alwaysThrow;

    /// <summary>Dequeues the next scripted response, or the default if the queue is empty. Throws
    /// the scripted exception, if the dequeued item is one, or the sticky exception set via
    /// <see cref="AlwaysThrow"/> if one is active.</summary>
    internal ChatResponse Next()
    {
        lock (_gate)
        {
            if (_alwaysThrow is { } sticky) throw sticky;
            if (_responses.Count == 0) return new ChatResponse(new ChatMessage(ChatRole.Assistant, _defaultResponseText));
            var item = _responses.Dequeue();
            return item.Exception is { } exception ? throw exception : item.ResponseValue!;
        }
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
