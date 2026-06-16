using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Compression;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Interfaces.MediatR;
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

        var sessionId = _executionContext.ConversationId ?? "unknown";

        // This behavior runs BEFORE ResponseSanitizationBehavior (registered outer), so the
        // store write would otherwise persist raw, unsanitized tool output to disk — credentials
        // and tokens included — even when the sanitizer later blocks the response. Redact secrets
        // at this persistence boundary so they never land at rest. ISecretRedactor is idempotent,
        // so the model-visible summary the sanitizer later scans is unaffected by also redacting here.
        var redactedOutput = _secretRedactor.Redact(toolOutput) ?? toolOutput;

        var reference = await _resultStore.StoreIfLargeAsync(
            sessionId,
            toolRequest.ToolName,
            operation: null,
            redactedOutput,
            cancellationToken);

        var compressionResult = await _compressor.CompressAsync(
            redactedOutput,
            category: null,
            _config.DefaultTokenThreshold,
            cancellationToken);

        var compressedWithRef = $"{compressionResult.Output}\n[Full output: result://{reference.ResultId}]";

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
