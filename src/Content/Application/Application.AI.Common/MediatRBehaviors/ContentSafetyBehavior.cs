using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.MediatR;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Models;
using Domain.AI.Telemetry.Conventions;
using Domain.Common;
using Domain.Common.Config;
using Domain.Common.Helpers;
using MediatR;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.MediatRBehaviors;

/// <summary>
/// Screens request content against safety policies for requests implementing
/// <see cref="IContentScreenable"/>. Blocks harmful, unsafe, or policy-violating
/// content before it reaches the handler.
/// </summary>
/// <remarks>
/// <para>
/// Pipeline position: 8 (after validation — content must be structurally valid before screening).
/// </para>
/// <para>
/// Only screens <see cref="ContentScreeningTarget.Input"/>: the text a request names through
/// <see cref="IContentScreenable"/>. Nothing in the harness screens model output or ingested documents. A deployment
/// that needs either screened adds that itself.
/// </para>
/// <para>
/// <b>When the content cannot be screened</b> the service throws <see cref="ContentSafetyUnavailableException"/>, and
/// this behavior applies the deployment's policy, <c>AppConfig:AI:ContentSafety:BlockWhenUnavailable</c>, the same way
/// whichever provider is configured. Either way the evaluation is recorded as
/// <see cref="SafetyConventions.OutcomeValues.Unscreened"/>, never as a pass or a block: an outage is not a verdict on
/// the text, and a dashboard that showed it as one would report healthy screening while nothing was screened. With the
/// policy on (the default) the exception propagates as a service failure: HTTP 503 on hosts whose errors pass through
/// <c>GlobalExceptionMiddleware</c>; AgentHub's turn paths still report it as an internal error (#952). With the policy
/// off, the request goes on unscreened.
/// </para>
/// </remarks>
public sealed class ContentSafetyBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    private const string Filter = "pipeline_behavior";

    private readonly ITextContentSafetyService _safetyService;
    private readonly IObservabilityStore _observabilityStore;
    private readonly IOptions<AppConfig> _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentSafetyBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    /// <param name="safetyService">The configured screening service.</param>
    /// <param name="observabilityStore">Store for per-session safety events.</param>
    /// <param name="config">
    /// Application configuration holding the policy for content that cannot be screened. Read as it was at startup,
    /// like the screening service itself, so every content-safety setting takes effect on a restart and none half-applies.
    /// </param>
    public ContentSafetyBehavior(
        ITextContentSafetyService safetyService,
        IObservabilityStore observabilityStore,
        IOptions<AppConfig> config)
    {
        _safetyService = safetyService;
        _observabilityStore = observabilityStore;
        _config = config;
    }

    /// <inheritdoc />
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (request is not IContentScreenable { ScreeningTarget: ContentScreeningTarget.Input or ContentScreeningTarget.Both } screenable)
            return await next();

        ContentSafetyResult result;
        try
        {
            result = await _safetyService.ScreenAsync(screenable.ContentToScreen, cancellationToken);
        }
        catch (ContentSafetyUnavailableException)
        {
            await RecordAsync(request, SafetyConventions.OutcomeValues.Unscreened, category: null, cancellationToken);
            if (_config.Value.AI.ContentSafety.BlockWhenUnavailable)
                throw;
            return await next();
        }

        await RecordAsync(
            request,
            result.IsBlocked ? SafetyConventions.OutcomeValues.Block : SafetyConventions.OutcomeValues.Pass,
            result.Category,
            cancellationToken);

        if (!result.IsBlocked)
            return await next();

        ContentSafetyMetrics.Blocks.Add(1,
            new KeyValuePair<string, object?>(SafetyConventions.Phase, SafetyConventions.PhaseValues.Prompt),
            new KeyValuePair<string, object?>(SafetyConventions.Filter, Filter),
            new KeyValuePair<string, object?>(SafetyConventions.Category, result.Category ?? "unknown"));

        var reason = result.BlockReason ?? "Content blocked by safety policy.";
        if (ResultHelper.TryCreateFailure<TResponse>(nameof(Result.ContentBlocked), reason, out var blockedResult))
            return blockedResult;
        throw new ContentSafetyException(reason, result.Category);
    }

    // The evaluation metric for every screening, and the session's safety event when the request belongs to one.
    private async Task RecordAsync(TRequest request, string outcome, string? category, CancellationToken cancellationToken)
    {
        ContentSafetyMetrics.Evaluations.Add(1,
            new KeyValuePair<string, object?>(SafetyConventions.Phase, SafetyConventions.PhaseValues.Prompt),
            new KeyValuePair<string, object?>(SafetyConventions.Filter, Filter),
            new KeyValuePair<string, object?>(SafetyConventions.Outcome, outcome));

        if (request is IHasObservabilitySession obs && obs.ObservabilitySessionId != Guid.Empty)
            await _observabilityStore.RecordSafetyEventAsync(
                obs.ObservabilitySessionId, "prompt", outcome, category, null, Filter, cancellationToken);
    }
}
