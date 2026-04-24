using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.MediatR;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Models;
using Domain.AI.Telemetry.Conventions;
using Domain.Common;
using Domain.Common.Helpers;
using MediatR;

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
/// Only screens <see cref="ContentScreeningTarget.Input"/>. Output screening is handled
/// at the agent orchestration loop level, where the LLM response is inspected before
/// returning to the user. The pipeline behavior handles input because it has access
/// to the typed request; output screening requires inspecting the generic response.
/// </para>
/// </remarks>
public sealed class ContentSafetyBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    private readonly ITextContentSafetyService _safetyService;
    private readonly IObservabilityStore _observabilityStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentSafetyBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    public ContentSafetyBehavior(
        ITextContentSafetyService safetyService,
        IObservabilityStore observabilityStore)
    {
        _safetyService = safetyService;
        _observabilityStore = observabilityStore;
    }

    /// <inheritdoc />
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (request is not IContentScreenable screenable)
            return await next();

        if (screenable.ScreeningTarget is ContentScreeningTarget.Input or ContentScreeningTarget.Both)
        {
            var result = await _safetyService.ScreenAsync(screenable.ContentToScreen, cancellationToken);

            ContentSafetyMetrics.Evaluations.Add(1,
                new KeyValuePair<string, object?>(SafetyConventions.Phase, SafetyConventions.PhaseValues.Prompt),
                new KeyValuePair<string, object?>(SafetyConventions.Filter, "pipeline_behavior"),
                new KeyValuePair<string, object?>(SafetyConventions.Outcome, result.IsBlocked ? SafetyConventions.OutcomeValues.Block : SafetyConventions.OutcomeValues.Pass));

            if (request is IHasObservabilitySession obs && obs.ObservabilitySessionId != Guid.Empty)
            {
                await _observabilityStore.RecordSafetyEventAsync(
                    obs.ObservabilitySessionId,
                    "prompt",
                    result.IsBlocked ? "block" : "pass",
                    result.Category,
                    null,
                    "pipeline_behavior",
                    cancellationToken);
            }

            if (result.IsBlocked)
            {
                ContentSafetyMetrics.Blocks.Add(1,
                    new KeyValuePair<string, object?>(SafetyConventions.Phase, SafetyConventions.PhaseValues.Prompt),
                    new KeyValuePair<string, object?>(SafetyConventions.Filter, "pipeline_behavior"),
                    new KeyValuePair<string, object?>(SafetyConventions.Category, result.Category ?? "unknown"));

                var reason = result.BlockReason ?? "Content blocked by safety policy.";
                if (ResultHelper.TryCreateFailure<TResponse>(nameof(Result.ContentBlocked), reason, out var blockedResult))
                    return blockedResult;
                throw new ContentSafetyException(reason, result.Category);
            }
        }

        return await next();
    }
}
