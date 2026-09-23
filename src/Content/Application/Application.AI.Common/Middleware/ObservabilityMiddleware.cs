using Application.AI.Common.Extensions;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Services;
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

        RecordUsage(response, options);

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

        // Accumulate updates so usage capture matches the non-streaming path exactly.
        // Streaming providers emit token counts as a UsageContent item in the final
        // chunk; ToChatResponse() coalesces the stream and surfaces it as Usage.
        var updates = new List<ChatResponseUpdate>();
        await foreach (var chunk in base.GetStreamingResponseAsync(messageList, options, cancellationToken))
        {
            updates.Add(chunk);
            _logger.LogDebug("Received chunk with {ContentCount} content item(s)", chunk.Contents?.Count ?? 0);
            yield return chunk;
        }

        _logger.LogDebug("Streaming completed: {ChunkCount} chunks", updates.Count);
        RecordUsage(updates.ToChatResponse(), options);
    }

    /// <summary>
    /// Logs and records token usage from a (possibly stream-reconstructed) response.
    /// Shared by the streaming and non-streaming paths so both capture identically.
    /// </summary>
    private void RecordUsage(ChatResponse response, ChatOptions? options)
    {
        if (response.Usage is not { } usage)
            return;

        _logger.LogInformation(
            "Token usage — Input: {InputTokens}, Output: {OutputTokens}, Total: {TotalTokens}",
            usage.InputTokenCount,
            usage.OutputTokenCount,
            usage.TotalTokenCount);

        var inputTokens = (int)Math.Min(usage.InputTokenCount ?? 0, int.MaxValue);
        var outputTokens = (int)Math.Min(usage.OutputTokenCount ?? 0, int.MaxValue);
        var cacheRead = (int)Math.Min(usage.GetCacheReadTokens(), int.MaxValue);
        var cacheWrite = (int)Math.Min(usage.GetCacheCreationTokens(), int.MaxValue);
        // Prefer the model the provider actually reported on the response; the per-call
        // ChatOptions.ModelId is usually null because the model is configured on the client.
        // A still-null model is defaulted to a priced model downstream in LlmUsageCapture.
        var model = response.ModelId ?? options?.ModelId;

        (_usageCapture ?? LlmUsageCapture.Current)?.Record(inputTokens, outputTokens, cacheRead, cacheWrite, model);
    }
}
