using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Compression;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Interfaces.MediatR;
using Application.AI.Common.Services.Governance;
using Domain.Common;
using Domain.Common.Config.AI;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.MediatRBehaviors;

/// <summary>
/// Compresses large tool output before it re-enters the LLM context window.
/// Stores the full output via <see cref="IToolResultStore"/> and replaces
/// the response content with a compressed summary plus a retrieval reference.
/// </summary>
/// <remarks>
/// <para>Pipeline position: 9 (post-execution, before response sanitization at 9.5).</para>
/// <para>Only activates when <c>ToolOutputCompressionConfig.Enabled</c> is true,
/// the request implements <see cref="IToolRequest"/>, and the response
/// value implements <see cref="IToolResponse"/> with output exceeding the token threshold.</para>
/// <para>
/// Extract/Replace pattern mirrors <see cref="ResponseSanitizationBehavior{TRequest,TResponse}"/>
/// to handle both <c>Result&lt;IToolResponse&gt;</c> and direct <c>IToolResponse</c> responses
/// via reflection-based <c>Result&lt;T&gt;</c> unwrapping.
/// </para>
/// </remarks>
public sealed class ToolOutputCompressionBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    private readonly IToolOutputCompressor _compressor;
    private readonly IToolResultStore _resultStore;
    private readonly IAgentExecutionContext _executionContext;
    private readonly ISecretRedactor _secretRedactor;
    private readonly ToolOutputCompressionConfig _config;
    private readonly ILogger<ToolOutputCompressionBehavior<TRequest, TResponse>> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="ToolOutputCompressionBehavior{TRequest, TResponse}"/>.
    /// </summary>
    public ToolOutputCompressionBehavior(
        IToolOutputCompressor compressor,
        IToolResultStore resultStore,
        IAgentExecutionContext executionContext,
        ISecretRedactor secretRedactor,
        IOptions<ToolOutputCompressionConfig> config,
        ILogger<ToolOutputCompressionBehavior<TRequest, TResponse>> logger)
    {
        _compressor = compressor;
        _resultStore = resultStore;
        _executionContext = executionContext;
        _secretRedactor = secretRedactor;
        _config = config.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (request is not IToolRequest toolRequest)
            return await next();

        if (!_config.Enabled)
            return await next();

        var response = await next();

        try
        {
            return await CompressIfNeeded(response, toolRequest, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Tool output compression failed for {ToolName}; returning original response",
                toolRequest.ToolName);
            return response;
        }
    }

    private async Task<TResponse> CompressIfNeeded(
        TResponse response,
        IToolRequest toolRequest,
        CancellationToken cancellationToken)
    {
        var toolOutput = ExtractToolOutput(response);
        if (toolOutput is null)
            return response;

        var estimatedTokens = TokenEstimationHelper.EstimateTokens(toolOutput);
        if (estimatedTokens <= _config.DefaultTokenThreshold)
        {
            _logger.LogDebug(
                "Tool {ToolName} output ({Tokens} tokens) below threshold ({Threshold}); skipping compression",
                toolRequest.ToolName, estimatedTokens, _config.DefaultTokenThreshold);
            return response;
        }

        // This behavior runs BEFORE ResponseSanitizationBehavior (registered outer), so the
        // store write would otherwise persist raw, unsanitized tool output to disk — credentials
        // and tokens included — even when the sanitizer later blocks the response. Redact secrets
        // at this persistence boundary so they never land at rest. ISecretRedactor is idempotent,
        // so the model-visible summary the sanitizer later scans is unaffected by also redacting here.
        // Routed through ToolPayloadRedactor.Redact (not a bare `?? toolOutput`) so a redaction-contract
        // violation fails loudly instead of silently persisting the raw, unredacted output — the same
        // fail-closed rule this helper enforces at every other redaction boundary.
        var redactedOutput = ToolPayloadRedactor.Redact(toolOutput, _secretRedactor);

        var compressionResult = await _compressor.CompressAsync(
            redactedOutput,
            category: null,
            _config.DefaultTokenThreshold,
            cancellationToken);

        // #559: a direct tool invocation mints a fresh, call-scoped ToolResultScopeId that dies with
        // the call — nothing durable can ever ask for it again. Skip the write itself here too, the
        // same guard ToolCallAdmissionPipeline.SpillAndBuildMarkerAsync applies — this sibling path
        // was left open when that guard was added and would otherwise still persist an unreachable
        // file on every compression on this path.
        if (!_executionContext.HasRetrievableToolResultScope)
            return ReplaceToolOutput(response, compressionResult.Output);

        // The retrieval scope, not ConversationId — the same scope tool_result_fetch reads back with
        // (IAgentExecutionContext.ToolResultScopeId), and never null by construction, so no "?? unknown"
        // fallback is needed. Storing under a different key than retrieval reads from would silently
        // make this behavior's spill unreachable even with a resolvable marker. Read here, after the
        // guard above, rather than at the top of the method: ToolResultScopeId freezes on first read
        // (#562) and a later Initialize with a differing scope throws, so a read that is then discarded
        // by an early return is a coupling worth avoiding even though nothing reaches it today.
        var sessionId = _executionContext.ToolResultScopeId;

        var reference = await _resultStore.StoreIfLargeAsync(
            sessionId,
            toolRequest.ToolName,
            operation: null,
            redactedOutput,
            cancellationToken: cancellationToken);

        // #561/correctness: StoreIfLargeAsync keeps small content inline (FullContentPath null, no
        // file written) whenever redactedOutput is at or under PerResultCharLimit — reachable here
        // because this behavior's OWN threshold is token-based (DefaultTokenThreshold, ~2000 tokens)
        // and can trip well below that char limit. Appending the retrieval marker unconditionally, as
        // an earlier version of this fix did, advertised an id tool_result_fetch could never resolve
        // for every output in that band. Mirrors SpillAndBuildMarkerAsync's identical guard.
        var compressedWithRef = reference.FullContentPath is null
            ? compressionResult.Output
            : compressionResult.Output
                + string.Format(ToolCallAdmissionPipeline.SpilledResultMarkerFormat, reference.ResultId);

        _logger.LogInformation(
            "Compressed tool {ToolName} output from {OriginalTokens} to {CompressedTokens} tokens (strategy: {Strategy}, ref: {ResultId})",
            toolRequest.ToolName,
            compressionResult.OriginalTokens,
            compressionResult.CompressedTokens,
            compressionResult.Strategy,
            reference.ResultId);

        return ReplaceToolOutput(response, compressedWithRef);
    }

    private static string? ExtractToolOutput(TResponse response)
    {
        if (response is Result { IsSuccess: true } resultBase)
        {
            var valueProperty = resultBase.GetType().GetProperty("Value");
            if (valueProperty?.GetValue(resultBase) is IToolResponse toolResponse)
                return toolResponse.ToolOutput;
        }

        if (response is IToolResponse directToolResponse)
            return directToolResponse.ToolOutput;

        return null;
    }

    private static TResponse ReplaceToolOutput(TResponse response, string compressedContent)
    {
        if (response is Result { IsSuccess: true } resultBase)
        {
            var valueProperty = resultBase.GetType().GetProperty("Value");
            if (valueProperty?.GetValue(resultBase) is IToolResponse toolResponse)
            {
                var replacedValue = toolResponse.WithSanitizedOutput(compressedContent);
                var successMethod = resultBase.GetType().GetMethod("Success", [valueProperty.PropertyType]);
                if (successMethod is not null)
                    return (TResponse)successMethod.Invoke(null, [replacedValue])!;
            }
        }

        if (response is IToolResponse directToolResponse)
            return (TResponse)directToolResponse.WithSanitizedOutput(compressedContent);

        return response;
    }
}
