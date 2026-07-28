using Application.AI.Common.Interfaces.Governance;
using Domain.AI.Agents;
using Domain.AI.Changes;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Core.CQRS.Autonomy;

/// <summary>
/// Computes a decision preview by chaining the two shared governance services the enforcement
/// path itself uses: <see cref="IAutonomyTierResolver"/> for the subagent's effective tier,
/// then <see cref="IAutonomyDecisionEvaluator"/> for the graded-autonomy verdict. Because the
/// preview and the enforcement path call the <em>same</em> evaluator with the same inputs, the
/// preview can never drift from what enforcement would actually decide.
/// </summary>
/// <remarks>
/// Side-effect-free by construction: both dependencies are pure lookup/evaluation services.
/// The handler writes nothing, audits nothing, and raises no escalations — it answers a
/// hypothetical.
/// </remarks>
public sealed class PreviewAutonomyDecisionQueryHandler
    : IRequestHandler<PreviewAutonomyDecisionQuery, Result<AutonomyDecisionPreviewResult>>
{
    private readonly IAutonomyTierResolver _tierResolver;
    private readonly IAutonomyDecisionEvaluator _decisionEvaluator;
    private readonly ILogger<PreviewAutonomyDecisionQueryHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="PreviewAutonomyDecisionQueryHandler"/> class.</summary>
    /// <param name="tierResolver">The shared tier resolver the enforcement path also uses.</param>
    /// <param name="decisionEvaluator">The shared graded-autonomy evaluator the enforcement path also uses.</param>
    /// <param name="logger">Logger for malformed-input diagnostics (caller-supplied values are never logged verbatim).</param>
    public PreviewAutonomyDecisionQueryHandler(
        IAutonomyTierResolver tierResolver,
        IAutonomyDecisionEvaluator decisionEvaluator,
        ILogger<PreviewAutonomyDecisionQueryHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(tierResolver);
        ArgumentNullException.ThrowIfNull(decisionEvaluator);
        ArgumentNullException.ThrowIfNull(logger);
        _tierResolver = tierResolver;
        _decisionEvaluator = decisionEvaluator;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<Result<AutonomyDecisionPreviewResult>> Handle(
        PreviewAutonomyDecisionQuery request, CancellationToken cancellationToken)
    {
        if (!AutonomyValidationRules.TryParseEnumName<SubagentType>(
                request.SubagentType, out var subagentType))
        {
            // The raw value is caller-controlled and deliberately kept out of the log line.
            _logger.LogWarning("Autonomy decision preview for an unknown subagent type returned NotFound");
            return Task.FromResult(Result<AutonomyDecisionPreviewResult>.NotFound(
                AutonomyValidationRules.UnknownSubagentTypeMessage));
        }

        // The FluentValidation validator already rejects malformed names with a 400 before the
        // handler runs; these re-checks keep the handler safe when dispatched outside the
        // MediatR pipeline (defense in depth, same failure classification).
        if (!AutonomyValidationRules.TryParseEnumName<BlastRadius>(
                request.BlastRadius, out var blastRadius))
        {
            return Task.FromResult(Result<AutonomyDecisionPreviewResult>.ValidationFailure(
                [AutonomyValidationRules.InvalidBlastRadiusMessage]));
        }

        if (!AutonomyValidationRules.TryParseEnumName<ChangeTargetKind>(
                request.TargetKind, out var targetKind))
        {
            return Task.FromResult(Result<AutonomyDecisionPreviewResult>.ValidationFailure(
                [AutonomyValidationRules.InvalidTargetKindMessage]));
        }

        var tier = _tierResolver.Resolve(subagentType);

        var decision = _decisionEvaluator.Evaluate(
            tier,
            blastRadius,
            targetKind,
            request.IsStateChange,
            string.IsNullOrWhiteSpace(request.SkillKey) ? null : request.SkillKey);

        return Task.FromResult(Result<AutonomyDecisionPreviewResult>.Success(
            AutonomyDecisionPreviewResult.FromResult(subagentType, decision)));
    }
}
