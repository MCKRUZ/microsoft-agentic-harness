using Application.AI.Common.Interfaces.DriftDetection;
using Domain.AI.DriftDetection;
using Domain.Common;
using Domain.Common.Config;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.Core.CQRS.DriftDetection;

/// <summary>
/// Runs the pushed scores through <see cref="IDriftDetectionService.EvaluateDriftAsync"/> — the
/// exact pipeline internal callers use (baseline fallback, EWMA scoring, severity
/// classification, graph persistence, notification, escalation) — bracketed by
/// <see cref="DriftAuditRecordType.EvaluationPushed"/> audit records carrying the caller
/// identity, so every externally-sourced data point that moved EWMA state is attributable.
/// </summary>
/// <remarks>
/// The fail-closed attempt record is appended <em>before</em> dispatch: a push that cannot be
/// durably attributed is refused rather than allowed through unattributed. See
/// <see cref="DriftOperatorAuditRecorder"/> for why the ordering, not just the posture, is what
/// makes the audit trail a real compensating control.
/// </remarks>
public sealed class PushDriftEvaluationCommandHandler
    : IRequestHandler<PushDriftEvaluationCommand, Result<DriftScore>>
{
    private readonly IDriftDetectionService _driftService;
    private readonly IDriftAuditStore _auditStore;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PushDriftEvaluationCommandHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="PushDriftEvaluationCommandHandler"/> class.</summary>
    /// <param name="driftService">The drift detection service that owns the evaluation pipeline.</param>
    /// <param name="auditStore">The append-only drift audit store the caller identity is recorded in.</param>
    /// <param name="config">Application configuration; supplies the drift subsystem's master toggle.</param>
    /// <param name="timeProvider">Time provider for audit timestamps.</param>
    /// <param name="logger">Logger for push diagnostics.</param>
    public PushDriftEvaluationCommandHandler(
        IDriftDetectionService driftService,
        IDriftAuditStore auditStore,
        IOptionsMonitor<AppConfig> config,
        TimeProvider timeProvider,
        ILogger<PushDriftEvaluationCommandHandler> logger)
    {
        _driftService = driftService;
        _auditStore = auditStore;
        _config = config;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<DriftScore>> Handle(
        PushDriftEvaluationCommand request, CancellationToken cancellationToken)
    {
        var actionId = Guid.NewGuid();
        var now = _timeProvider.GetUtcNow();

        var attempt = await DriftOperatorAuditRecorder.RecordAttemptAsync(
            _auditStore,
            DriftAuditRecordType.EvaluationPushed,
            BuildAudit(request, actionId, DriftOperatorActionPhase.Attempt),
            now,
            _logger,
            cancellationToken);

        if (!attempt.IsSuccess)
        {
            // Fail-closed: without a durable attempt record this push would advance EWMA state
            // and feed future baselines with nothing in the trail naming who caused it.
            return Result<DriftScore>.Conflict(
                "Drift audit trail is unavailable; evaluation pushes are refused while pushes cannot be attributed.");
        }

        var result = await EvaluateAsync(request, cancellationToken);

        await DriftOperatorAuditRecorder.RecordOutcomeAsync(
            _auditStore,
            DriftAuditRecordType.EvaluationPushed,
            BuildAudit(request, actionId, DriftOperatorActionPhase.Outcome) with
            {
                Succeeded = result.IsSuccess,
                CorrelationId = result.IsSuccess ? result.Value!.ScoreId : null,
                FailureCode = result.IsSuccess
                    ? null
                    : DriftOperatorActionAudit.FailureCodeFor(result.FailureType)
            },
            eventId: result.IsSuccess ? result.Value!.ScoreId : actionId,
            recordedAt: _timeProvider.GetUtcNow(),
            _logger,
            cancellationToken);

        if (result.IsSuccess)
        {
            _logger.LogInformation(
                "Evaluation pushed by {CallerId} for {Scope}:{ScopeIdentifier} (action {ActionId}): severity {Severity}, overall {OverallDrift:F2}σ",
                request.CallerId, request.Scope, request.ScopeIdentifier, actionId,
                result.Value!.Severity, result.Value.OverallDrift);
        }
        else
        {
            _logger.LogWarning(
                "Evaluation push by {CallerId} for {Scope}:{ScopeIdentifier} (action {ActionId}) failed ({FailureType})",
                request.CallerId, request.Scope, request.ScopeIdentifier, actionId, result.FailureType);
        }

        return result;
    }

    /// <summary>
    /// Dispatches to the drift pipeline, but only while the subsystem is enabled.
    /// </summary>
    /// <remarks>
    /// The service's disabled arm deliberately returns a no-op <c>Success</c> with an all-zero,
    /// never-persisted score (ConsoleUI's walkthrough relies on that), which through an HTTP
    /// surface would be a lie: 200 plus an audit record claiming a successful push whose
    /// CorrelationId points at a score no store ever held. Flipping one config value would then
    /// silently stop monitoring while the API and the trail both kept reporting success — the
    /// threat model's "mask real drift" outcome reached through configuration instead of data.
    /// The push therefore refuses with the same <c>Conflict</c> posture
    /// <c>UpdateBaselineAsync</c> already uses for the identical condition, and the caller sees
    /// the 409 the controller documents.
    /// </remarks>
    private async Task<Result<DriftScore>> EvaluateAsync(
        PushDriftEvaluationCommand request, CancellationToken cancellationToken)
    {
        if (!_config.CurrentValue.AI.DriftDetection.Enabled)
        {
            _logger.LogWarning(
                "Evaluation push by {CallerId} for {Scope}:{ScopeIdentifier} refused: drift detection is disabled",
                request.CallerId, request.Scope, request.ScopeIdentifier);
            return Result<DriftScore>.Conflict("Drift detection is disabled");
        }

        return await _driftService.EvaluateDriftAsync(new DriftEvaluationRequest
        {
            Scope = request.Scope,
            ScopeIdentifier = request.ScopeIdentifier,
            Dimensions = request.Dimensions
        }, cancellationToken);
    }

    private static DriftOperatorActionAudit BuildAudit(
        PushDriftEvaluationCommand request, Guid actionId, DriftOperatorActionPhase phase) => new()
        {
            ActionId = actionId,
            Phase = phase,
            CallerId = request.CallerId,
            Action = DriftOperatorActionAudit.EvaluationPushAction,
            Scope = request.Scope,
            ScopeIdentifier = request.ScopeIdentifier
        };
}
