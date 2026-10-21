using System.Globalization;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Compliance;
using Application.Core.CQRS.Compliance.EraseSubjectData;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common;
using Domain.Common.Models;
using Microsoft.Extensions.Logging;

namespace Application.Core.Compliance;

/// <summary>
/// Runs one right-to-erasure request and records it in the audit trail, for both the self-service and the operator path
/// (#900). The record proves the erasure happened without keeping the identity it erased.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What the record holds.</strong> Who asked (<see cref="ErasureActor"/>), a pseudonymous subject reference from
/// <see cref="IErasureSubjectReference"/> when a reference key is configured, the receipt's request id, its completeness,
/// and per-store counts. Never the subject's id, never a node, memory key or conversation id: an audit trail that kept
/// those would keep the person the erasure removed.
/// </para>
/// <para>
/// <strong>Two records per request.</strong> An <see cref="AuditOutcome.Attempted"/> record before anything is deleted and
/// an outcome after, sharing one correlation id, so a request that dies mid-way leaves a trace that it began rather than a
/// claim that it succeeded. The outcome is written even when the caller cancels part-way. Writing a record is
/// best-effort, and so is the subject reference: a broken audit sink or an unusable key is logged and never refuses an
/// erasure, because refusing a person their legal right to protect the audit trail would invert the priorities.
/// </para>
/// <para>
/// Store faults are logged with full detail and surfaced as a fixed message, since store exceptions can carry
/// connection strings and paths. Logs carry the correlation id and the subject reference, never the subject's id.
/// </para>
/// </remarks>
public sealed class AuditedErasure
{
    /// <summary>The audit action every erasure record carries.</summary>
    public const string AuditAction = "right_to_erasure";

    /// <summary>The message returned when a store fault stopped the request.</summary>
    public const string FailedMessage = "Erasure could not be completed. The failure has been logged for investigation.";

    /// <summary>Recorded as the subject reference when a reference key is configured but unusable.</summary>
    public const string ReferenceUnavailable = "unavailable";

    /// <summary>Recorded as the subject reference of a refused request whose subject id no valid request could carry.</summary>
    public const string ReferenceInvalid = "invalid";

    private readonly IPersonalDataErasure _erasure;
    private readonly IErasureSubjectReference _subjectReference;
    private readonly IAuditSink _auditSink;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AuditedErasure> _logger;

    /// <summary>Initializes a new instance of the <see cref="AuditedErasure"/> class.</summary>
    /// <param name="erasure">Erases one person from the knowledge graph and every registered store.</param>
    /// <param name="subjectReference">Pseudonymises the subject for the audit record.</param>
    /// <param name="auditSink">The audit trail the attempt and outcome are written to.</param>
    /// <param name="timeProvider">Time source for record timestamps.</param>
    /// <param name="logger">Logger for store faults and audit-write failures.</param>
    public AuditedErasure(
        IPersonalDataErasure erasure,
        IErasureSubjectReference subjectReference,
        IAuditSink auditSink,
        TimeProvider timeProvider,
        ILogger<AuditedErasure> logger)
    {
        ArgumentNullException.ThrowIfNull(erasure);
        ArgumentNullException.ThrowIfNull(subjectReference);
        ArgumentNullException.ThrowIfNull(auditSink);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _erasure = erasure;
        _subjectReference = subjectReference;
        _auditSink = auditSink;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Erases <paramref name="subjectId"/> and records the attempt and its outcome.</summary>
    /// <param name="subjectId">The person to erase, exactly as their identity provider issues the id.</param>
    /// <param name="actor">Who asked for the erasure.</param>
    /// <param name="requestType">The command that carried the request, for the audit record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The receipt, or a scrubbed failure when a fault stopped the request.</returns>
    public async Task<Result<ErasureReceipt>> EraseAsync(
        string subjectId, ErasureActor actor, string requestType, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestType);

        var correlationId = Guid.NewGuid().ToString("N");
        var subjectReference = ReferenceFor(subjectId, correlationId);
        var record = new AuditEntry
        {
            RequestType = requestType,
            Action = AuditAction,
            ExecutorId = actor.ExecutorId,
            CorrelationId = correlationId,
            Timestamp = _timeProvider.GetUtcNow(),
            Outcome = AuditOutcome.Attempted,
            Metadata = Metadata("attempt", actor, subjectReference, receipt: null),
        };
        await RecordSafelyAsync(record);

        ErasureReceipt receipt;
        try
        {
            receipt = await _erasure.EraseOwnerAsync(subjectId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await RecordFailureAsync(record, actor, subjectReference, "cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Right-to-erasure {CorrelationId} failed (subject reference {SubjectReference}).",
                correlationId, subjectReference);
            await RecordFailureAsync(record, actor, subjectReference, ex.GetType().Name);
            return Result<ErasureReceipt>.Fail(FailedMessage);
        }

        _logger.LogInformation(
            "Right-to-erasure {CorrelationId} completed: receipt {RequestId}, completeness {Completeness}, "
            + "{Stores} stores reached.",
            correlationId, receipt.RequestId, receipt.Completeness, receipt.Stores.Count);
        await RecordSafelyAsync(record with
        {
            Timestamp = _timeProvider.GetUtcNow(),
            Outcome = AuditOutcome.Success,
            Metadata = Metadata("outcome", actor, subjectReference, receipt),
        });

        return Result<ErasureReceipt>.Success(receipt);
    }

    /// <summary>Records a request refused before anything was erased.</summary>
    /// <param name="actor">Who asked for the erasure.</param>
    /// <param name="requestType">The command that carried the request.</param>
    /// <param name="reason">Why it was refused; a fixed phrase, never an identity.</param>
    /// <param name="subjectId">
    /// The person an operator asked to erase, recorded only by reference, so a refused attempt against someone can still
    /// be found. <see langword="null"/> when the request named no one.
    /// </param>
    public Task RecordDeniedAsync(ErasureActor actor, string requestType, string reason, string? subjectId = null)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var correlationId = Guid.NewGuid().ToString("N");
        // A refused request's subject is unvalidated input of any length; hash only what a valid request could carry.
        var subjectReference = string.IsNullOrWhiteSpace(subjectId) ? null
            : subjectId.Length > EraseSubjectDataCommandValidator.MaxSubjectIdLength ? ReferenceInvalid
            : ReferenceFor(subjectId, correlationId);
        return RecordSafelyAsync(new AuditEntry
        {
            RequestType = requestType,
            Action = AuditAction,
            ExecutorId = actor.ExecutorId,
            CorrelationId = correlationId,
            Timestamp = _timeProvider.GetUtcNow(),
            Outcome = AuditOutcome.Denied,
            FailureReason = reason,
            Metadata = Metadata("outcome", actor, subjectReference, receipt: null),
        });
    }

    /// <summary>
    /// The subject's reference, or <see cref="ReferenceUnavailable"/> when the key is unusable: a bad key degrades the
    /// record, never the erasure.
    /// </summary>
    private string? ReferenceFor(string subjectId, string correlationId)
    {
        try
        {
            return _subjectReference.For(subjectId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Right-to-erasure {CorrelationId}: the subject reference could not be computed.",
                correlationId);
            return ReferenceUnavailable;
        }
    }

    private Task RecordFailureAsync(AuditEntry attempt, ErasureActor actor, string? subjectReference, string reason) =>
        RecordSafelyAsync(attempt with
        {
            Timestamp = _timeProvider.GetUtcNow(),
            Outcome = AuditOutcome.Failure,
            FailureReason = reason,
            Metadata = Metadata("outcome", actor, subjectReference, receipt: null),
        });

    private static Dictionary<string, string> Metadata(
        string phase, ErasureActor actor, string? subjectReference, ErasureReceipt? receipt)
    {
        var metadata = new Dictionary<string, string>
        {
            ["phase"] = phase,
            ["initiator"] = actor.IsOperator ? "operator" : "data_subject",
        };
        if (subjectReference is not null)
            metadata["subject_reference"] = subjectReference;
        if (receipt is null)
            return metadata;

        metadata["receipt_request_id"] = receipt.RequestId;
        metadata["completeness"] = receipt.Completeness.ToString();
        metadata["graph.nodes"] = Count(receipt.NodesDeleted);
        metadata["graph.edges"] = Count(receipt.EdgesDeleted);
        metadata["graph.feedback_weights"] = Count(receipt.FeedbackWeightsDeleted);
        metadata["graph.vector_embeddings"] = Count(receipt.VectorEmbeddingsDeleted);
        metadata["graph.bm25_documents"] = Count(receipt.Bm25DocumentsDeleted);
        metadata["graph.cross_session_memories"] = Count(receipt.CrossSessionMemoriesDeleted);
        foreach (var store in receipt.Stores)
            metadata[$"store.{store.Store}"] = $"{store.Status}:{Count(store.ItemsRemoved)}";

        return metadata;
    }

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Writes one record, never cancelled by the caller: an erasure that started must leave its outcome even when the
    /// request is abandoned part-way.
    /// </summary>
    private async Task RecordSafelyAsync(AuditEntry entry)
    {
        try
        {
            await _auditSink.RecordAsync(entry, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record the {Phase} of right-to-erasure {CorrelationId}.",
                entry.Metadata?.GetValueOrDefault("phase"), entry.CorrelationId);
        }
    }
}
