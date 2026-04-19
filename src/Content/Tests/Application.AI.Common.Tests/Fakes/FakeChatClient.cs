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
        var text = string.Join("", response.Messages.Select(m => m.Text));
        yield return new ChatResponseUpdate(ChatRole.Assistant, text);
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc />
    public void Dispose() { }
}
