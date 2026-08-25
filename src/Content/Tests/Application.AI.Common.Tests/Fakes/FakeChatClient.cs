using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Application.AI.Common.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IChatClient"/> that returns configurable canned responses.
/// Tracks all requests for assertion in integration tests.
/// </summary>
public sealed class FakeChatClient : IChatClient
{
    private readonly List<IList<ChatMessage>> _requestHistory = [];
    private readonly Queue<ChatResponse> _responses = new();
    private ChatResponse _defaultResponse = new(new ChatMessage(ChatRole.Assistant, "fake response"));

    /// <summary>All message lists sent to this client, in order.</summary>
    public IReadOnlyList<IList<ChatMessage>> RequestHistory => _requestHistory;

    /// <summary>Sets the default response returned when the queue is empty.</summary>
    public FakeChatClient WithDefaultResponse(string content)
    {
        _defaultResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, content));
        return this;
    }

    /// <summary>Enqueues a response to be returned on the next call (FIFO).</summary>
    public FakeChatClient EnqueueResponse(string content)
    {
        _responses.Enqueue(new ChatResponse(new ChatMessage(ChatRole.Assistant, content)));
        return this;
    }

    /// <summary>Enqueues a response with usage metadata for token tracking tests.</summary>
    public FakeChatClient EnqueueResponseWithUsage(string content, int inputTokens, int outputTokens)
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, content))
        {
            Usage = new UsageDetails { InputTokenCount = inputTokens, OutputTokenCount = outputTokens, TotalTokenCount = inputTokens + outputTokens }
        };
        _responses.Enqueue(response);
        return this;
    }

    /// <summary>
    /// Enqueues a response whose assistant message carries a tool call, for tool-capture tests.
    /// </summary>
    /// <param name="toolName">The tool name the call targets.</param>
    /// <param name="callId">The provider-assigned call id.</param>
    /// <param name="arguments">
    /// The call's arguments. Defaults to empty — pass a populated dictionary when a test needs to
    /// prove arguments specifically survive a round trip (an empty dictionary never exercises that).
    /// </param>
    public FakeChatClient EnqueueResponseWithToolCall(
        string toolName, string callId, IDictionary<string, object?>? arguments = null)
    {
        var message = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent(callId, toolName, arguments ?? new Dictionary<string, object?>())
        });
        _responses.Enqueue(new ChatResponse(message));
        return this;
    }

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messageList = messages.ToList();
        _requestHistory.Add(messageList);
        var response = _responses.Count > 0 ? _responses.Dequeue() : _defaultResponse;
        return Task.FromResult(response);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);

        // Stream each content item (text, tool calls) as its own update so middleware
        // reconstructing via ToChatResponse() sees the same shape as the blocking path.
        foreach (var message in response.Messages)
            foreach (var content in message.Contents)
                yield return new ChatResponseUpdate(message.Role, new List<AIContent> { content });

        // Mirror real providers: token usage arrives as a UsageContent item in a final chunk.
        if (response.Usage is { } usage)
            yield return new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent> { new UsageContent(usage) });
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc />
    public void Dispose() { }
}
