using Application.AI.Common.Interfaces.DriftDetection;
using Domain.AI.DriftDetection;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Core.CQRS.DriftDetection;

/// <summary>
/// Resolves the target baseline by id, then delegates to
/// <see cref="IDriftDetectionService.UpdateBaselineAsync"/> — the same recalculation path the
/// learnings-drift bridge uses — bracketed by
/// <see cref="DriftAuditRecordType.BaselineRecalculationRequested"/> audit records. The service
/// additionally audits the successful recalculation as
/// <see cref="DriftAuditRecordType.BaselineUpdated"/> with the new baseline serialized.
/// </summary>
/// <remarks>
/// The outcome record captures what the recalculation <em>changed</em> — the replaced
/// baseline's id plus the sample count and window the new one consumed. Recalculation overwrites
/// the previous snapshot, so without those fields an operator could push poisoned evaluations,
/// recalculate to launder them into the new "normal", and leave a trail proving only <em>that</em>
/// they recalculated, never what it did.
/// </remarks>
public sealed class RecalculateDriftBaselineCommandHandler
    : IRequestHandler<RecalculateDriftBaselineCommand, Result<DriftBaseline>>
{
    private readonly IDriftDetectionService _driftService;
    private readonly IDriftBaselineStore _baselineStore;
    private readonly IDriftAuditStore _auditStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RecalculateDriftBaselineCommandHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="RecalculateDriftBaselineCommandHandler"/> class.</summary>
    /// <param name="driftService">The drift detection service that owns baseline recalculation.</param>
    /// <param name="baselineStore">The baseline store used to resolve the id to a scope.</param>
    /// <param name="auditStore">The append-only drift audit store the caller identity is recorded in.</param>
    /// <param name="timeProvider">Time provider for audit timestamps.</param>
    /// <param name="logger">Logger for recalculation diagnostics.</param>
    public RecalculateDriftBaselineCommandHandler(
        IDriftDetectionService driftService,
        IDriftBaselineStore baselineStore,
        IDriftAuditStore auditStore,
        TimeProvider timeProvider,
        ILogger<RecalculateDriftBaselineCommandHandler> logger)
    {
        _driftService = driftService;
        _baselineStore = baselineStore;
        _auditStore = auditStore;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<DriftBaseline>> Handle(
        RecalculateDriftBaselineCommand request, CancellationToken cancellationToken)
    {
        var actionId = Guid.NewGuid();

        var attempt = await DriftOperatorAuditRecorder.RecordAttemptAsync(
            _auditStore,
            DriftAuditRecordType.BaselineRecalculationRequested,
            BuildAudit(request, actionId, DriftOperatorActionPhase.Attempt),
            _timeProvider.GetUtcNow(),
            _logger,
            cancellationToken);

        if (!attempt.IsSuccess)
        {
            // Fail-closed: recalculation re-anchors what "normal" means and destroys the
            // previous snapshot. It must not run while that cannot be attributed.
            return Result<DriftBaseline>.Conflict(
                "Drift audit trail is unavailable; baseline recalculation is refused while changes cannot be attributed.");
        }

        var lookup = await _baselineStore.GetBaselineByIdAsync(request.BaselineId, cancellationToken);
        if (!lookup.IsSuccess)
        {
            await RecordOutcomeAsync(request, actionId, target: null, outcome: null,
                ResultFailureType.General, cancellationToken);
            return Result<DriftBaseline>.Fail([.. lookup.Errors]);
        }

        if (lookup.Value is not { } target)
        {
            await RecordOutcomeAsync(request, actionId, target: null, outcome: null,
                ResultFailureType.NotFound, cancellationToken);
            return Result<DriftBaseline>.NotFound("No baseline with the given id.");
        }

        var result = await _driftService.UpdateBaselineAsync(new DriftBaselineUpdateRequest
        {
            Scope = target.Scope,
            ScopeIdentifier = target.ScopeIdentifier
        }, cancellationToken);

        await RecordOutcomeAsync(request, actionId, target, result, result.FailureType, cancellationToken);

        if (result.IsSuccess)
        {
            _logger.LogInformation(
                "Baseline {PreviousBaselineId} ({Scope}:{ScopeIdentifier}) recalculated by {CallerId} (action {ActionId}); new baseline {NewBaselineId} from {SampleCount} samples",
                request.BaselineId, target.Scope, target.ScopeIdentifier, request.CallerId, actionId,
                result.Value!.BaselineId, result.Value.SampleCount);
        }
        else
        {
            _logger.LogWarning(
                "Baseline recalculation for {BaselineId} requested by {CallerId} (action {ActionId}) failed ({FailureType})",
                request.BaselineId, request.CallerId, actionId, result.FailureType);
        }

        return result;
    }

    private Task RecordOutcomeAsync(
        RecalculateDriftBaselineCommand request,
        Guid actionId,
        DriftBaseline? target,
        Result<DriftBaseline>? outcome,
        ResultFailureType failureType,
        CancellationToken ct)
    {
        var succeeded = outcome?.IsSuccess ?? false;
        var newBaseline = succeeded ? outcome!.Value! : null;

        var audit = BuildAudit(request, actionId, DriftOperatorActionPhase.Outcome) with
        {
            Scope = target?.Scope,
            ScopeIdentifier = target?.ScopeIdentifier,
            Succeeded = succeeded,
            CorrelationId = newBaseline?.BaselineId,
            FailureCode = succeeded ? null : DriftOperatorActionAudit.FailureCodeFor(failureType),
            // Always the id the caller asked for — which on success IS the replaced snapshot's
            // id (the lookup resolves by it), and on failure is the id that was probed. Taking
            // it from the resolved target instead would blank this field on a not-found, so an
            // attacker sweeping ids to discover which baselines exist would leave outcome
            // records naming no id at all, reconstructible only by joining back to the attempt
            // record on ActionId. Enumeration is exactly when the id matters most, so each
            // record stands on its own. The remaining three fields describe what the new
            // baseline was built from; all come from objects already in hand.
            PreviousBaselineId = request.BaselineId,
            SampleCount = newBaseline?.SampleCount,
            WindowStart = newBaseline?.WindowStart,
            WindowEnd = newBaseline?.WindowEnd
        };

        return DriftOperatorAuditRecorder.RecordOutcomeAsync(
            _auditStore,
            DriftAuditRecordType.BaselineRecalculationRequested,
            audit,
            eventId: newBaseline?.BaselineId ?? actionId,
            recordedAt: _timeProvider.GetUtcNow(),
            _logger,
            ct);
    }

    private static DriftOperatorActionAudit BuildAudit(
        RecalculateDriftBaselineCommand request, Guid actionId, DriftOperatorActionPhase phase) => new()
        {
            ActionId = actionId,
            Phase = phase,
            CallerId = request.CallerId,
            Action = DriftOperatorActionAudit.BaselineRecalculateAction,
            // The attempt record names the requested target before it is resolved; the outcome
            // record overwrites these with the scope the id actually resolved to.
            PreviousBaselineId = request.BaselineId
        };
}
