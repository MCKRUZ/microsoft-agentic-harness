using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.MediatR;
using Domain.Common;
using Domain.Common.Config.AI;
using Domain.Common.Helpers;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.MediatRBehaviors;

/// <summary>
/// Deterministic prompt injection detection using pattern matching.
/// Runs before <see cref="ContentSafetyBehavior{TRequest,TResponse}"/> to catch
/// injection attempts at zero latency and zero cost before LLM-based screening.
/// </summary>
/// <remarks>
/// <para>Pipeline position: 7.5 (after governance policy at 7, before content safety at 8).</para>
/// <para>Only activates when <c>GovernanceConfig.EnablePromptInjectionDetection</c> is true.</para>
/// </remarks>
public sealed class PromptInjectionBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    private readonly IPromptInjectionScanner _scanner;
    private readonly IGovernanceAuditService _auditService;
    private readonly IOptionsMonitor<GovernanceConfig> _config;
    private readonly ILogger<PromptInjectionBehavior<TRequest, TResponse>> _logger;

    public PromptInjectionBehavior(
        IPromptInjectionScanner scanner,
        IGovernanceAuditService auditService,
        IOptionsMonitor<GovernanceConfig> config,
        ILogger<PromptInjectionBehavior<TRequest, TResponse>> logger)
    {
        _scanner = scanner;
        _auditService = auditService;
        _config = config;
        _logger = logger;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (request is not IContentScreenable screenable)
            return await next();

        var cfg = _config.CurrentValue;
        if (!cfg.Enabled || !cfg.EnablePromptInjectionDetection)
            return await next();

        var result = _scanner.Scan(screenable.ContentToScreen);

        if (!result.IsInjection || result.ThreatLevel < cfg.InjectionBlockThreshold)
            return await next();

        _logger.LogWarning(
            "Prompt injection detected: {InjectionType} (threat: {ThreatLevel}, confidence: {Confidence:F2})",
            result.InjectionType, result.ThreatLevel, result.Confidence);

        if (cfg.EnableAudit)
            _auditService.Log("system", "prompt_injection_scan", $"blocked:{result.InjectionType}");

        var reason = $"Prompt injection detected ({result.InjectionType}). Request blocked.";
        if (ResultHelper.TryCreateFailure<TResponse>(nameof(Result.GovernanceBlocked), reason, out var blocked))
            return blocked;

        throw new InvalidOperationException(reason);
    }
}
