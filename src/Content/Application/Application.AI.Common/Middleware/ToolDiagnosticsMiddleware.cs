using Domain.Common.Extensions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

namespace Application.AI.Common.Middleware;

/// <summary>
/// Chat client middleware that logs tool and function calling information for debugging.
/// Captures tool configurations in chat options and tool calls in responses.
/// </summary>
/// <remarks>
/// Useful during development to verify that tools are being registered correctly
/// and that the LLM is invoking them as expected.
/// </remarks>
public sealed class ToolDiagnosticsMiddleware : DelegatingChatClient
{
    private const int MaxToolsToLog = 5;
    private const int MaxPreviewLength = 200;

    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ToolDiagnosticsMiddleware"/> class.
    /// </summary>
    /// <param name="innerClient">The inner chat client to wrap with diagnostics.</param>
    /// <param name="logger">Logger for recording tool diagnostic events.</param>
    public ToolDiagnosticsMiddleware(IChatClient innerClient, ILogger<ToolDiagnosticsMiddleware> logger)
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
        var toolsWereConfigured = options?.Tools is { Count: > 0 };
        LogToolsInOptions(options, nameof(GetResponseAsync));

        var response = await base.GetResponseAsync(messages, options, cancellationToken);

        LogToolCallsInResponse(response, toolsWereConfigured);
        return response;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        LogToolsInOptions(options, nameof(GetStreamingResponseAsync));

        await foreach (var chunk in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            yield return chunk;
        }
    }

    private void LogToolsInOptions(ChatOptions? options, string method)
    {
        if (options?.Tools is not { Count: > 0 })
        {
            _logger.LogDebug("[ToolDiag] {Method}: No tools configured (generation-only)", method);
            return;
        }

        _logger.LogInformation("[ToolDiag] {Method}: {ToolCount} tools configured", method, options.Tools.Count);

        foreach (var tool in options.Tools.Take(MaxToolsToLog))
        {
            if (tool is AIFunction func)
            {
                _logger.LogInformation("[ToolDiag] Tool: {ToolName}, HasSchema: {HasSchema}",
                    func.Name,
                    func.JsonSchema.ValueKind != System.Text.Json.JsonValueKind.Undefined);
            }
            else
            {
                _logger.LogInformation("[ToolDiag] Tool type: {ToolType}", tool.GetType().Name);
            }
        }

        if (options.Tools.Count > MaxToolsToLog)
            _logger.LogInformation("[ToolDiag] ... and {MoreCount} more tools", options.Tools.Count - MaxToolsToLog);
    }

    private void LogToolCallsInResponse(ChatResponse response, bool toolsWereConfigured)
    {
        var toolCalls = response.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>();

        var count = 0;
        foreach (var call in toolCalls)
        {
            count++;
            _logger.LogInformation("[ToolDiag] Tool call: {FunctionName} (CallId: {CallId})",
                call.Name, call.CallId);
        }

        if (count == 0)
        {
            if (toolsWereConfigured)
                _logger.LogWarning("[ToolDiag] No tool calls in response (tools were available)");
            else
                _logger.LogDebug("[ToolDiag] No tool calls (generation-only mode)");

            LogResponsePreview(response);
            return;
        }

        _logger.LogInformation("[ToolDiag] {ToolCallCount} tool call(s) in response", count);
    }

    private void LogResponsePreview(ChatResponse response)
    {
        var textContent = response.Messages
            .SelectMany(m => m.Contents)
            .OfType<TextContent>()
            .FirstOrDefault();

        if (textContent?.Text is { } text)
            _logger.LogDebug("[ToolDiag] Response preview: {Preview}", text.Truncate(MaxPreviewLength));

        if (response.FinishReason is { } reason)
            _logger.LogInformation("[ToolDiag] Finish reason: {FinishReason}", reason);
    }
}
