using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Traces;
using Application.AI.Common.Services;
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

        // Record tool stdout against the matching call id for the per-invocation
        // observability page, and (when a trace writer is wired) append trace records.
        // AppendFunctionResultTracesAsync null-checks the writer internally, so this
        // must run unconditionally — otherwise RecordToolResult never fires.
        //
        // Scans the INBOUND messages, not the response — this middleware sits inside
        // FunctionInvokingChatClient (confirmed empirically: the first-registered .Use() in
        // AgentFactory.BuildMiddlewarePipeline is outermost), so a tool this turn actually invoked
        // has its FunctionResultContent appended to the NEXT round's inbound messages by
        // FunctionInvokingChatClient's own loop, not to this middleware's own outbound response.
        // Scanning the response would miss real tool activity entirely — see #249 item 6's PR2 for
        // the incident this comment exists to prevent repeating.
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

    /// <remarks>
    /// Scans the caller's <em>inbound</em> messages, not the response — correct given where this
    /// middleware sits in the pipeline (see the call site's comment): a genuinely new result from
    /// this turn's own tool-execution rounds arrives here as inbound content on the next round, not
    /// as this middleware's own outbound response. That same inbound scan would just as readily match
    /// a result already sitting in <em>replayed</em> conversation history (#249 item 6), or a result
    /// this same turn already recorded on an earlier round — content this middleware cannot tell apart
    /// from a genuinely new result by looking at it alone, since all three are an ordinary
    /// <see cref="FunctionResultContent"/> in the (cumulative, ever-growing) inbound list. Excluding
    /// any result whose <c>CallId</c> is present in <see cref="Services.ReplayedToolCallScope.Current"/>
    /// is what supplies that missing signal: the turn handler seeds it with the pre-dispatch history's
    /// call ids, and this method grows it in place as it records each result, so both the
    /// cross-turn (replayed history) and intra-turn (an earlier round of this same turn) duplicate
    /// cases are covered by one check.
    /// </remarks>
    private async Task AppendFunctionResultTracesAsync(IEnumerable<ChatMessage> messages, CancellationToken ct)
    {
        var alreadyReplayed = Services.ReplayedToolCallScope.Current;
        var functionResults = messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .Where(r => alreadyReplayed is null || string.IsNullOrEmpty(r.CallId) || !alreadyReplayed.Contains(r.CallId))
            .ToList();

        foreach (var result in functionResults)
        {
            // Mark this call id known the moment it's picked up for recording — not after the trace
            // write succeeds — so a later round's scan of the same (still-growing) inbound message
            // list skips it even if the trace write below fails. See ReplayedToolCallScope's remarks:
            // this is what closes the intra-turn duplicate-recording case a read-only, dispatch-time
            // snapshot alone cannot.
            if (!string.IsNullOrEmpty(result.CallId))
                alreadyReplayed?.Add(result.CallId);

            // A failed call's Result already carries the raw exception message baked in by
            // IncludeDetailedErrors (see ExecuteAgentTurnCommandHandler.RedactedResultForStreaming) — this
            // trace record feeds the dashboard's per-invocation page via ToolInvocationDetailDto,
            // which is just as much an exposure point as the streamed SSE frame, so it gets the same
            // generic-message substitution, not just redaction of the raw text.
            var rawPayload = ToolPayloadRedactor.SafeResultText(result);

            string trimmedPayload;
            if (rawPayload.Length > ToolPayloadRedactor.MaxStructuralRedactionCeiling)
            {
                // Above this size PatternSecretRedactor falls back to its regex-only pass, which
                // cannot see through the escaped-nested-JSON secret shape #391 closed for smaller
                // payloads — a 500-char slice of an oversized, only-partially-redacted result could
                // still contain an unredacted secret persisted to the trace store indefinitely. Same
                // guard ExecuteAgentTurnCommandHandler.RedactedResultForStreaming applies to the identical
                // exposure on the streamed path.
                trimmedPayload = "[result too large to preview safely]";
            }
            else
            {
                // A redaction-contract violation from _redactor must degrade this trace record, not
                // abort the chat call this diagnostics middleware is only observing.
                trimmedPayload = ToolPayloadRedactor.TryOrFallback(
                    () => ToolPayloadRedactor.RedactAndTruncate(rawPayload, _redactor),
                    _logger,
                    $"[ToolDiag] Failed to redact tool result for CallId={result.CallId}",
                    fallback: "[unavailable]");
            }

            // Always record the stdout against the matching call id so the
            // observability pipeline can render it on the per-invocation page
            // even when trace writer isn't wired.
            LlmUsageCapture.Current?.RecordToolResult(result.CallId, trimmedPayload);

            if (_traceWriter is null)
                continue;

            try
            {
                var record = new ExecutionTraceRecord
                {
                    Ts = DateTimeOffset.UtcNow,
                    Type = TraceRecordTypes.ToolResult,
                    ExecutionRunId = _traceWriter.Scope.ExecutionRunId.ToString("D"),
                    TurnId = result.CallId ?? Guid.NewGuid().ToString("D"),
                    // SafeResultText strips the raw exception text from PayloadSummary on failure — that
                    // text used to be the only signal a reader had that the call failed at all, so
                    // ResultCategory must now carry that signal structurally instead.
                    ResultCategory = result.Exception is not null ? TraceResultCategories.Error : TraceResultCategories.Success,
                    PayloadSummary = trimmedPayload
                };

                await _traceWriter.AppendTraceAsync(record, ct);
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
        var toolsWereConfigured = options?.Tools is { Count: > 0 };
        LogToolsInOptions(options, nameof(GetStreamingResponseAsync));

        // Unconditional: records tool stdout via LlmUsageCapture even when the trace
        // writer is null (the writer is null-checked inside the method).
        await AppendFunctionResultTracesAsync(messages, cancellationToken);

        // Accumulate updates so tool-call capture (names, args, call ids) matches the
        // non-streaming path. ToChatResponse() coalesces the stream into the same
        // ChatResponse shape LogToolCallsInResponse already understands.
        var updates = new List<ChatResponseUpdate>();
        await foreach (var chunk in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            updates.Add(chunk);
            yield return chunk;
        }

        LogToolCallsInResponse(updates.ToChatResponse(), toolsWereConfigured);
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

        var capture = LlmUsageCapture.Current;
        var count = 0;
        foreach (var call in toolCalls)
        {
            count++;
            _logger.LogInformation("[ToolDiag] Tool call: {FunctionName} (CallId: {CallId})",
                call.Name, call.CallId);

            if (string.IsNullOrEmpty(call.Name))
                continue;

            capture?.RecordToolCall(call.Name);

            // Deliberately independent of ExecuteAgentTurnCommandHandler.RedactedArgsJson, which
            // redacts the same call.Arguments a second time for the streaming SSE path (#389,
            // investigated and closed as won't-fix): the two values have different truncation
            // contracts — this one is capped to MaxPayloadSummaryLength for the persisted
            // observability record, the streamed one is never truncated (only withheld whole above
            // a size ceiling, since cutting mid-JSON would hand a client invalid data). A shared
            // cache would have to store the uncapped value and rely on every consumer remembering to
            // re-cap it — a footgun, not a saving — for a cost (one extra serialize + four redaction
            // passes per tool call) that is negligible next to an LLM round trip.
            string? argsJson = null;
            if (call.Arguments is { Count: > 0 } args)
            {
                try
                {
                    var serialized = System.Text.Json.JsonSerializer.Serialize(args);
                    // Above the structural-redaction ceiling, PatternSecretRedactor falls back to its
                    // regex-only pass, which cannot see through the escaped-nested-JSON secret shape
                    // #391 closed for smaller payloads — a 500-char preview sliced from this path could
                    // then still contain an unredacted secret. Withhold rather than preview, since this
                    // path (unlike the streaming path's 16KB ceiling) has no size cap of its own.
                    argsJson = serialized.Length > ToolPayloadRedactor.MaxStructuralRedactionCeiling
                        ? "[args too large to preview safely]"
                        : ToolPayloadRedactor.RedactAndTruncate(serialized, _redactor);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "[ToolDiag] Failed to serialize args for {Tool} CallId={CallId}",
                        call.Name, call.CallId);
                }
            }

            capture?.RecordToolRequest(call.CallId, call.Name, argsJson);
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
