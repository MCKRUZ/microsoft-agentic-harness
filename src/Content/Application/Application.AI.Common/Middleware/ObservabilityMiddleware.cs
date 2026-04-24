using Application.AI.Common.Interfaces;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

namespace Application.AI.Common.Middleware;

/// <summary>
/// Chat client middleware that logs diagnostic information for AI chat interactions
/// including message counts, token usage metrics, and streaming chunk details.
/// Also captures usage data into <see cref="ILlmUsageCapture"/> so handlers can
/// record real token counts to the observability store.
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
    private readonly ILlmUsageCapture? _usageCapture;

    /// <summary>
    /// Initializes a new instance of the <see cref="ObservabilityMiddleware"/> class.
    /// </summary>
    /// <param name="innerClient">The inner chat client to wrap with observability.</param>
    /// <param name="logger">Logger for recording chat interaction events.</param>
    /// <param name="usageCapture">Optional scoped capture for accumulating token usage across calls.</param>
    public ObservabilityMiddleware(
        IChatClient innerClient,
        ILogger<ObservabilityMiddleware> logger,
        ILlmUsageCapture? usageCapture = null)
        : base(innerClient)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _usageCapture = usageCapture;
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

            var inputTokens = (int)Math.Min(usage.InputTokenCount ?? 0, int.MaxValue);
            var outputTokens = (int)Math.Min(usage.OutputTokenCount ?? 0, int.MaxValue);
            var cacheRead = GetAdditionalCount(usage, "cache_read_input_tokens");
            var cacheWrite = GetAdditionalCount(usage, "cache_creation_input_tokens");
            var model = options?.ModelId;

            _usageCapture?.Record(inputTokens, outputTokens, cacheRead, cacheWrite, model);
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

        int chunkCount = 0;
        await foreach (var chunk in base.GetStreamingResponseAsync(messageList, options, cancellationToken))
        {
            chunkCount++;
            _logger.LogDebug("Received chunk with {ContentCount} content item(s)", chunk.Contents?.Count ?? 0);
            yield return chunk;
        }

        // ChatResponseUpdate does not expose UsageDetails — token usage is not captured for streaming calls.
        // When accurate usage tracking is required, prefer the non-streaming GetResponseAsync path.
        _logger.LogDebug("Streaming completed: {ChunkCount} chunks. Token usage unavailable for streaming path", chunkCount);
    }

    private static int GetAdditionalCount(UsageDetails usage, string key)
    {
        if (usage.AdditionalCounts?.TryGetValue(key, out var value) == true)
            return (int)Math.Min(value, int.MaxValue);
        return 0;
    }
}
