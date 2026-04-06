using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

namespace Application.AI.Common.Middleware;

/// <summary>
/// Chat client middleware that logs diagnostic information for AI chat interactions
/// including message counts, token usage metrics, and streaming chunk details.
/// </summary>
/// <remarks>
/// <para>
/// Complements OpenTelemetry instrumentation with human-readable log entries for
/// quick diagnostics. OpenTelemetry provides structured tracing and metrics;
/// this middleware provides contextual log lines.
/// </para>
/// <para>
/// <strong>What gets logged:</strong>
/// <list type="number">
///   <item>Pre-request: message count for both streaming and non-streaming</item>
///   <item>Post-response (non-streaming): input/output/total token usage</item>
///   <item>During streaming: content length of each chunk</item>
/// </list>
/// </para>
/// </remarks>
public sealed class ObservabilityMiddleware : DelegatingChatClient
{
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ObservabilityMiddleware"/> class.
    /// </summary>
    /// <param name="innerClient">The inner chat client to wrap with observability.</param>
    /// <param name="logger">Logger for recording chat interaction events.</param>
    public ObservabilityMiddleware(IChatClient innerClient, ILogger<ObservabilityMiddleware> logger)
        : base(innerClient)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messageList = messages as ICollection<ChatMessage> ?? messages.ToList();
        _logger.LogInformation("Invoking ChatClient with {MessageCount} messages", messageList.Count);

        var response = await base.GetResponseAsync(messageList, options, cancellationToken);

        if (response.Usage is { } usage)
        {
            _logger.LogInformation(
                "Token usage — Input: {InputTokens}, Output: {OutputTokens}, Total: {TotalTokens}",
                usage.InputTokenCount,
                usage.OutputTokenCount,
                usage.TotalTokenCount);
        }

        return response;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messageList = messages as ICollection<ChatMessage> ?? messages.ToList();
        _logger.LogInformation("Invoking streaming ChatClient with {MessageCount} messages", messageList.Count);

        await foreach (var chunk in base.GetStreamingResponseAsync(messageList, options, cancellationToken))
        {
            _logger.LogDebug("Received chunk with {ContentCount} content item(s)", chunk.Contents?.Count ?? 0);
            yield return chunk;
        }
    }
}
