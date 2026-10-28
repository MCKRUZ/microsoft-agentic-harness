using System.Globalization;
using System.Text.RegularExpressions;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common;
using Domain.Common.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Application.Core.Compliance;

/// <summary>
/// Removes one source document from the corpus and records it in the audit trail (#901), the way
/// <see cref="AuditedErasure"/> records an erasure.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What the record holds.</strong> Who asked (an operator, or the document age limit), the document id, the receipt's request id, its completeness,
/// whether the id matched anything, and per-store counts. A document id is a hash of where the document came from, not a person's identity, so it is kept: an
/// audit record that could not say which document went would prove little.
/// </para>
/// <para>
/// <strong>Two records per request.</strong> An <see cref="AuditOutcome.Attempted"/> record before anything is removed and
/// an outcome after, sharing one correlation id, so a request that dies part-way leaves a trace that it began. Writing a
/// record is best-effort: a broken audit sink is logged and never stops the removal. Each record goes to the
/// <see cref="IComplianceAuditTrail"/>, which keeps it as proof for good (#969), and to the audit sink, whose default log
/// line is deleted with the logs; the two writes are independent. Store faults are logged in full and
/// surfaced as a fixed message, because store exceptions can carry connection strings and paths.
/// </para>
/// <para>
/// <strong>The load record.</strong> A complete removal forgets the document in <see cref="IDocumentLoadLedger"/>, so the
/// document age limit does not reach for it again; a <c>Partial</c> one keeps it, so the age limit tries again. Only a
/// record no later than the load the caller decided on is forgotten, so a document loaded again meanwhile keeps its new
/// load time.
/// </para>
/// </remarks>
public sealed class AuditedDocumentRemoval
{
    /// <summary>The audit action every document-removal record carries.</summary>
    public const string AuditAction = "document_removal";

    /// <summary>The message returned when a store fault stopped the request.</summary>
    public const string FailedMessage =
        "The document could not be removed. The failure has been logged for investigation.";

    /// <summary>Recorded as the document of a refused request whose id is not one a valid request could carry.</summary>
    public const string DocumentInvalid = "invalid";

    private static readonly Regex DocumentIdFormat = new(
        "^[0-9A-F]{16}\\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IErasureOrchestrator _orchestrator;
    private readonly IDocumentLoadLedger _loadLedger;
    private readonly IAuditSink _auditSink;
    private readonly IComplianceAuditTrail _trail;
    private readonly string _host;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AuditedDocumentRemoval> _logger;

    /// <summary>Initializes a new instance of the <see cref="AuditedDocumentRemoval"/> class.</summary>
    /// <param name="orchestrator">Removes the document from every store.</param>
    /// <param name="loadLedger">When each document was last loaded; a complete removal forgets the document there.</param>
    /// <param name="auditSink">The general audit sink, by default a log line, the attempt and outcome are also written to.</param>
    /// <param name="trail">The compliance audit trail the attempt and outcome are kept in, as proof the removal was handled.</param>
    /// <param name="hostEnvironment">Names the host the receipt covers (#1003): a removal reaches only this process's stores.</param>
    /// <param name="timeProvider">Time source for record timestamps.</param>
    /// <param name="logger">Logger for store faults and audit-write failures.</param>
    public AuditedDocumentRemoval(
        IErasureOrchestrator orchestrator,
        IDocumentLoadLedger loadLedger,
        IAuditSink auditSink,
        IComplianceAuditTrail trail,
        IHostEnvironment hostEnvironment,
        TimeProvider timeProvider,
        ILogger<AuditedDocumentRemoval> logger)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(loadLedger);
        ArgumentNullException.ThrowIfNull(auditSink);
        ArgumentNullException.ThrowIfNull(trail);
        ArgumentNullException.ThrowIfNull(hostEnvironment);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _host = hostEnvironment.ApplicationName;
        _orchestrator = orchestrator;
        _loadLedger = loadLedger;
        _auditSink = auditSink;
        _trail = trail;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Whether <paramref name="documentId"/> is a document id as ingestion forms it: 16 upper-case hex digits.</summary>
    public static bool IsDocumentId(string? documentId) =>
        documentId is not null && DocumentIdFormat.IsMatch(documentId);

    /// <summary>Removes <paramref name="documentId"/> and records the attempt and its outcome.</summary>
    /// <param name="documentId">The document to remove.</param>
    /// <param name="actor">Who asked for the removal.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="loadedNoLaterThan">
    /// The latest load the caller decided to remove; the document age limit passes the load time it judged. A load recorded
    /// after it keeps its record. When omitted, the moment the removal starts.
    /// </param>
    /// <returns>The receipt, or a scrubbed failure when a fault stopped the request.</returns>
    public async Task<Result<ErasureReceipt>> RemoveAsync(
        string documentId, ErasureActor actor, CancellationToken cancellationToken, DateTimeOffset? loadedNoLaterThan = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentNullException.ThrowIfNull(actor);
        var forgetLoadsUpTo = loadedNoLaterThan ?? _timeProvider.GetUtcNow();

        var correlationId = Guid.NewGuid().ToString("N");
        var record = new AuditEntry
        {
            RequestType = nameof(CQRS.Compliance.RemoveDocument.RemoveDocumentCommand),
            Action = AuditAction,
            ExecutorId = actor.ExecutorId,
            CorrelationId = correlationId,
            Timestamp = _timeProvider.GetUtcNow(),
            Outcome = AuditOutcome.Attempted,
            Metadata = Metadata("attempt", actor, documentId, receipt: null),
        };
        await RecordSafelyAsync(record, actor);

        ErasureReceipt receipt;
        try
        {
            receipt = await _orchestrator.RemoveDocumentAsync(documentId, cancellationToken) with { Host = _host };
        }
        catch (OperationCanceledException)
        {
            await RecordFailureAsync(record, actor, documentId, "cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Document removal {CorrelationId} failed for document {DocumentId}.", correlationId, documentId);
            await RecordFailureAsync(record, actor, documentId, ex.GetType().Name);
            return Result<ErasureReceipt>.Fail(FailedMessage);
        }

        _logger.LogInformation(
            "Document removal {CorrelationId} completed for document {DocumentId}: receipt {RequestId}, completeness {Completeness}.",
            correlationId, documentId, receipt.RequestId, receipt.Completeness);
        await RecordSafelyAsync(record with
        {
            Timestamp = _timeProvider.GetUtcNow(),
            Outcome = AuditOutcome.Success,
            Metadata = Metadata("outcome", actor, documentId, receipt),
        }, actor);

        if (receipt.Completeness == ErasureCompleteness.Full)
            await ForgetLoadAsync(documentId, forgetLoadsUpTo);

        return Result<ErasureReceipt>.Success(receipt);
    }

    /// <summary>
    /// Forgets the document's load record. A failure is logged, not returned: the document is already gone, and a record
    /// left behind only means the age limit finds nothing there later.
    /// </summary>
    private async Task ForgetLoadAsync(string documentId, DateTimeOffset loadedNoLaterThan)
    {
        try
        {
            await _loadLedger.ForgetAsync(documentId, loadedNoLaterThan, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Document removal could not forget the load record of document {DocumentId}.", documentId);
        }
    }

    /// <summary>Records a request refused before anything was removed.</summary>
    /// <param name="actor">Who asked.</param>
    /// <param name="reason">Why it was refused; a fixed phrase, never an identity.</param>
    /// <param name="documentId">
    /// The document asked for, recorded only when it is a well-formed document id (unvalidated input of any length is
    /// not written into the trail); <see cref="DocumentInvalid"/> otherwise.
    /// </param>
    public Task RecordDeniedAsync(ErasureActor actor, string reason, string? documentId)
    {
        ArgumentNullException.ThrowIfNull(actor);

        return RecordSafelyAsync(new AuditEntry
        {
            RequestType = nameof(CQRS.Compliance.RemoveDocument.RemoveDocumentCommand),
            Action = AuditAction,
            ExecutorId = actor.ExecutorId,
            CorrelationId = Guid.NewGuid().ToString("N"),
            Timestamp = _timeProvider.GetUtcNow(),
            Outcome = AuditOutcome.Denied,
            FailureReason = reason,
            Metadata = Metadata("outcome", actor, IsDocumentId(documentId) ? documentId! : DocumentInvalid, receipt: null),
        }, actor);
    }

    private Task RecordFailureAsync(AuditEntry attempt, ErasureActor actor, string documentId, string reason) =>
        RecordSafelyAsync(attempt with
        {
            Timestamp = _timeProvider.GetUtcNow(),
            Outcome = AuditOutcome.Failure,
            FailureReason = reason,
            Metadata = Metadata("outcome", actor, documentId, receipt: null),
        }, actor);

    private Dictionary<string, string> Metadata(
        string phase, ErasureActor actor, string documentId, ErasureReceipt? receipt)
    {
        var metadata = new Dictionary<string, string>
        {
            ["phase"] = phase,
            ["initiator"] = actor.Initiator,
            ["host"] = _host,
            ["document_id"] = documentId,
        };
        if (receipt is null)
            return metadata;

        metadata["receipt_request_id"] = receipt.RequestId;
        metadata["completeness"] = receipt.Completeness.ToString();
        metadata["matched"] = receipt.UnmatchedDocumentIds.Count == 0 ? "true" : "false";
        metadata["graph.nodes"] = Count(receipt.NodesDeleted);
        metadata["graph.edges"] = Count(receipt.EdgesDeleted);
        metadata["graph.feedback_weights"] = Count(receipt.FeedbackWeightsDeleted);
        metadata["graph.vector_embeddings"] = Count(receipt.VectorEmbeddingsDeleted);
        metadata["graph.bm25_documents"] = Count(receipt.Bm25DocumentsDeleted);
        return metadata;
    }

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Writes one record to the compliance audit trail and to the audit sink; see <see cref="ComplianceRecords"/>.</summary>
    private Task RecordSafelyAsync(AuditEntry entry, ErasureActor actor) =>
        ComplianceRecords.WriteAsync(_trail, _auditSink, entry, actor, _logger, "document removal");
}
