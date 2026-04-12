using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Traces;
using Domain.Common.Extensions;
using Domain.Common.MetaHarness;
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
    private const int MaxPayloadSummaryLength = 500;

    private readonly ILogger _logger;
    private readonly ITraceWriter? _traceWriter;
    private readonly ISecretRedactor? _redactor;

    /// <summary>
    /// Initializes a new instance of the <see cref="ToolDiagnosticsMiddleware"/> class.
    /// </summary>
    /// <param name="innerClient">The inner chat client to wrap with diagnostics.</param>
    /// <param name="logger">Logger for recording tool diagnostic events.</param>
    /// <param name="traceWriter">Optional trace writer for recording tool result events.</param>
    /// <param name="redactor">Optional secret redactor applied to payloads before tracing.</param>
    public ToolDiagnosticsMiddleware(
        IChatClient innerClient,
        ILogger<ToolDiagnosticsMiddleware> logger,
        ITraceWriter? traceWriter = null,
        ISecretRedactor? redactor = null)
        : base(innerClient)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _traceWriter = traceWriter;
        _redactor = redactor;
    }

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options = DeduplicateTools(options);
        var toolsWereConfigured = options?.Tools is { Count: > 0 };
        LogToolsInOptions(options, nameof(GetResponseAsync));

        // Trace any function results being submitted to the LLM (i.e., tool calls that completed)
        if (_traceWriter != null)
            await AppendFunctionResultTracesAsync(messages, cancellationToken);

        try
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken);
            LogToolCallsInResponse(response, toolsWereConfigured);
            return response;
        }
        catch (System.ClientModel.ClientResultException ex) when (ex.Status == 404)
        {
            _logger.LogError(ex,
                "[ToolDiag] AI provider returned 404 — deployment not found. " +
                "Verify AppConfig:AI:AgentFramework:DefaultDeployment and Endpoint in user-secrets.");
            throw;
        }
    }

    private async Task AppendFunctionResultTracesAsync(IEnumerable<ChatMessage> messages, CancellationToken ct)
    {
        var functionResults = messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .ToList();

        foreach (var result in functionResults)
        {
            try
            {
                var rawPayload = result.Result?.ToString() ?? string.Empty;
                var payload = _redactor?.Redact(rawPayload) ?? rawPayload;
                if (payload.Length > MaxPayloadSummaryLength)
                    payload = payload[..MaxPayloadSummaryLength];

                var record = new ExecutionTraceRecord
                {
                    Ts = DateTimeOffset.UtcNow,
                    Type = TraceRecordTypes.ToolResult,
                    ExecutionRunId = _traceWriter!.Scope.ExecutionRunId.ToString("D"),
                    TurnId = result.CallId ?? Guid.NewGuid().ToString("D"),
                    ResultCategory = TraceResultCategories.Success,
                    PayloadSummary = payload
                };

                await _traceWriter!.AppendTraceAsync(record, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[ToolDiag] Failed to append trace record for CallId={CallId}", result.CallId);
            }
        }
    }

    // Deduplicate tools by name (case-insensitive) before they reach the HTTP layer.
    // The framework merges ChatOptions.Tools + AIContext.Tools from providers, which can
    // produce duplicates that the Anthropic API rejects with "Tool names must be unique".
    private static ChatOptions? DeduplicateTools(ChatOptions? options)
    {
        if (options?.Tools is not { Count: > 1 })
            return options;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduped = options.Tools.Where(t => seen.Add(t.Name)).ToList();

        if (deduped.Count == options.Tools.Count)
            return options;

        var cloned = options.Clone();
        cloned.Tools = deduped;
        return cloned;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options = DeduplicateTools(options);
        LogToolsInOptions(options, nameof(GetStreamingResponseAsync));

        if (_traceWriter != null)
            await AppendFunctionResultTracesAsync(messages, cancellationToken);

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
