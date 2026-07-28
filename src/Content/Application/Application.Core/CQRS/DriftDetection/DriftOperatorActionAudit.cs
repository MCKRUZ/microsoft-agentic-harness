using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.AI.DriftDetection;
using Domain.Common;

namespace Application.Core.CQRS.DriftDetection;

/// <summary>
/// Which half of an operator action a <see cref="DriftOperatorActionAudit"/> record describes.
/// Every drift write produces both, in order.
/// </summary>
public enum DriftOperatorActionPhase
{
    /// <summary>
    /// Recorded <em>before</em> the write is dispatched, and required to succeed for the write
    /// to proceed. This is the attributability guarantee: no evaluation reaches the EWMA
    /// pipeline without a durable record naming who asked for it.
    /// </summary>
    Attempt,

    /// <summary>
    /// Recorded after the write returns, carrying the outcome and the evidence of what changed.
    /// Best-effort: the mutation has already happened, so a failed append is logged rather than
    /// reported as an operation failure.
    /// </summary>
    Outcome
}

/// <summary>
/// The audit payload stamped onto <see cref="DriftAuditRecordType.EvaluationPushed"/> and
/// <see cref="DriftAuditRecordType.BaselineRecalculationRequested"/> records: who performed the
/// operator action, what it targeted, and — for the outcome record — how it ended and what it
/// changed. Serialized into <see cref="DriftAuditRecord.Payload"/> by the drift command handlers.
/// </summary>
/// <remarks>
/// <para>
/// Evaluation pushes are a history-poisoning vector — a caller who can push scores can shift
/// baselines and EWMA state to mask real drift, or fabricate drift to trigger escalations. The
/// audit trail is the compensating control, so every write through the HTTP surface is recorded
/// twice: a fail-closed <see cref="DriftOperatorActionPhase.Attempt"/> record before dispatch
/// and a best-effort <see cref="DriftOperatorActionPhase.Outcome"/> record after. Both carry
/// the same <see cref="ActionId"/>, so an attempt with no matching outcome is itself a
/// detectable signal.
/// </para>
/// <para>
/// <see cref="FailureCode"/> carries a stable classification (never raw error or exception
/// text), keeping the audit trail free of internal detail.
/// </para>
/// </remarks>
public sealed record DriftOperatorActionAudit
{
    /// <summary>Stable action code for an evaluation push.</summary>
    public const string EvaluationPushAction = "drift.evaluation_push";

    /// <summary>Stable action code for a baseline recalculation request.</summary>
    public const string BaselineRecalculateAction = "drift.baseline_recalculate";

    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Correlates this record's attempt and outcome halves. Minted per operator action by the
    /// handler; unrelated to any artifact id.
    /// </summary>
    public required Guid ActionId { get; init; }

    /// <summary>Which half of the action this record describes.</summary>
    public required DriftOperatorActionPhase Phase { get; init; }

    /// <summary>
    /// The authenticated caller's identity, resolved by the controller from the validated
    /// token's configured claim (<c>DriftDetectionConfig.CallerIdentityClaimType</c>). Never
    /// sourced from a request body.
    /// </summary>
    public required string CallerId { get; init; }

    /// <summary>
    /// Which operator action this record describes: <see cref="EvaluationPushAction"/> or
    /// <see cref="BaselineRecalculateAction"/>.
    /// </summary>
    public required string Action { get; init; }

    /// <summary>The targeted scope. Null when the target could not be resolved (e.g. an unknown baseline id).</summary>
    public DriftScope? Scope { get; init; }

    /// <summary>The targeted scope identifier. Null when the target could not be resolved.</summary>
    public string? ScopeIdentifier { get; init; }

    /// <summary>
    /// Whether the action succeeded. Null on an <see cref="DriftOperatorActionPhase.Attempt"/>
    /// record, where the outcome is not yet known.
    /// </summary>
    public bool? Succeeded { get; init; }

    /// <summary>
    /// Correlates the action to the artifact it produced: the resulting <c>DriftScore.ScoreId</c>
    /// for an evaluation push, the new <c>DriftBaseline.BaselineId</c> for a recalculation.
    /// Null on attempt records and on failures.
    /// </summary>
    public Guid? CorrelationId { get; init; }

    /// <summary>
    /// Stable failure classification when <see cref="Succeeded"/> is false (e.g.
    /// <c>drift.conflict</c>, <c>drift.not_found</c>). Never raw error text.
    /// </summary>
    public string? FailureCode { get; init; }

    /// <summary>
    /// The <see cref="DriftBaseline.BaselineId"/> this recalculation replaced. Recalculation
    /// overwrites the previous snapshot in the store, so this is the only surviving pointer to
    /// what "normal" meant beforehand — without it, laundering poisoned evaluations into a new
    /// baseline leaves no reconstructible before/after. Null for evaluation pushes.
    /// </summary>
    public Guid? PreviousBaselineId { get; init; }

    /// <summary>
    /// Number of evaluations the recalculation consumed. Null for evaluation pushes and failures.
    /// </summary>
    public int? SampleCount { get; init; }

    /// <summary>
    /// Start of the history window the recalculation consumed. Together with
    /// <see cref="WindowEnd"/> this pins which evaluations fed the new baseline, so a reviewer
    /// can re-query <c>GET /api/drift/history</c> for exactly that range. Null for evaluation
    /// pushes and failures.
    /// </summary>
    public DateTimeOffset? WindowStart { get; init; }

    /// <summary>End of the history window the recalculation consumed.</summary>
    public DateTimeOffset? WindowEnd { get; init; }

    /// <summary>Serializes this envelope to the JSON stored in <see cref="DriftAuditRecord.Payload"/>.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, SerializeOptions);

    /// <summary>
    /// Maps a <see cref="ResultFailureType"/> to the stable audit failure code recorded in
    /// <see cref="FailureCode"/>.
    /// </summary>
    /// <param name="failureType">The failure classification of the failed operation.</param>
    public static string FailureCodeFor(ResultFailureType failureType) => failureType switch
    {
        ResultFailureType.Conflict => "drift.conflict",
        ResultFailureType.NotFound => "drift.not_found",
        ResultFailureType.Validation => "drift.validation",
        _ => "drift.failed"
    };
}
