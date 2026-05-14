using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.MediatR;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Telemetry.Conventions;
using Domain.Common;
using Domain.Common.Config.AI;
using Domain.Common.Helpers;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.MediatRBehaviors;

/// <summary>
/// Sanitizes tool output after handler execution. Detects credentials,
/// prompt injection, and exfiltration URLs in the response before it
/// re-enters the LLM context.
/// </summary>
/// <remarks>
/// <para>Pipeline position: 9.5 (post-execution, after content safety at 8).</para>
/// <para>Only activates when <c>GovernanceConfig.Enabled</c> and
/// <c>GovernanceConfig.EnableResponseSanitization</c> are both true,
/// the request implements <see cref="IToolRequest"/>, and the response
/// value implements <see cref="IToolResponse"/>.</para>
/// </remarks>
public sealed class ResponseSanitizationBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    private readonly ICompositeResponseSanitizer _sanitizer;
    private readonly IGovernanceAuditService _auditService;
    private readonly IOptionsMonitor<GovernanceConfig> _config;
    private readonly ILogger<ResponseSanitizationBehavior<TRequest, TResponse>> _logger;

    public ResponseSanitizationBehavior(
        ICompositeResponseSanitizer sanitizer,
        IGovernanceAuditService auditService,
        IOptionsMonitor<GovernanceConfig> config,
        ILogger<ResponseSanitizationBehavior<TRequest, TResponse>> logger)
    {
        _sanitizer = sanitizer;
        _auditService = auditService;
        _config = config;
        _logger = logger;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (request is not IToolRequest toolRequest)
            return await next();

        var cfg = _config.CurrentValue;
        if (!cfg.Enabled || !cfg.EnableResponseSanitization)
            return await next();

        var response = await next();

        var toolOutput = ExtractToolOutput(response);
        if (toolOutput is null)
            return response;

        var result = _sanitizer.Sanitize(toolOutput, toolRequest.ToolName);

        if (!result.WasSanitized)
            return response;

        if (result.HighestThreatLevel >= cfg.ResponseBlockThreshold)
        {
            _logger.LogWarning(
                "Response blocked for tool {ToolName}: threat level {ThreatLevel} exceeds threshold ({Threshold}). Findings: {Count}",
                toolRequest.ToolName, result.HighestThreatLevel, cfg.ResponseBlockThreshold, result.Findings.Count);

            GovernanceMetrics.ResponseBlocks.Add(1,
                new KeyValuePair<string, object?>(GovernanceConventions.ToolName, toolRequest.ToolName));

            if (cfg.EnableAudit)
                _auditService.Log("system", "response_blocked", $"{result.HighestThreatLevel}:{toolRequest.ToolName}:{result.Findings.Count} findings");

            var reason = $"Tool response blocked: {result.HighestThreatLevel} threat detected ({result.Findings.Count} finding(s))";
            if (ResultHelper.TryCreateFailure<TResponse>(nameof(Result.GovernanceBlocked), reason, out var blocked))
                return blocked;

            throw new InvalidOperationException(reason);
        }

        _logger.LogInformation(
            "Response sanitized for tool {ToolName}: {Count} finding(s), highest threat {ThreatLevel}",
            toolRequest.ToolName, result.Findings.Count, result.HighestThreatLevel);

        if (cfg.EnableAudit)
        {
            var categories = string.Join(",", result.Findings.Select(f => f.Category).Distinct());
            _auditService.Log("system", "response_sanitized", $"{categories}:{toolRequest.ToolName}");
        }

        return ReplaceSanitizedOutput(response, result.SanitizedContent);
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

    private static TResponse ReplaceSanitizedOutput(TResponse response, string sanitizedContent)
    {
        if (response is Result { IsSuccess: true } resultBase)
        {
            var valueProperty = resultBase.GetType().GetProperty("Value");
            if (valueProperty?.GetValue(resultBase) is IToolResponse toolResponse)
            {
                var sanitizedValue = toolResponse.WithSanitizedOutput(sanitizedContent);
                var successMethod = resultBase.GetType().GetMethod("Success", [valueProperty.PropertyType]);
                if (successMethod is not null)
                    return (TResponse)successMethod.Invoke(null, [sanitizedValue])!;
            }
        }

        if (response is IToolResponse directToolResponse)
            return (TResponse)directToolResponse.WithSanitizedOutput(sanitizedContent);

        return response;
    }
}
