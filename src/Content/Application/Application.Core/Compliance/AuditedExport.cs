using System.Globalization;
using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Compliance;
using Application.Core.CQRS.Compliance.EraseSubjectData;
using Domain.Common;
using Domain.Common.Compliance;
using Domain.Common.Models;
using Microsoft.Extensions.Logging;

namespace Application.Core.Compliance;

/// <summary>
/// Makes one copy of a person's data and records the request in the audit trail (#902), the access and portability
/// counterpart of <see cref="AuditedErasure"/>. The record proves the copy was made and for whom, without keeping the
/// person's identity or anything the copy contains.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What the record holds.</strong> Who asked (<see cref="ErasureActor"/>, shared with erasure because the same
/// people may ask), a pseudonymous subject reference from <see cref="IErasureSubjectReference"/>, the copy's request id,
/// whether it is complete, and each store's status and record count. Never the subject's id and never any of the data.
/// </para>
/// <para>
/// <strong>Two records per request</strong>, an <see cref="AuditOutcome.Attempted"/> one before anything is read and an
/// outcome after, sharing a correlation id, so a request that dies part-way still leaves a trace that it began. Writing a
/// record is best-effort: a broken audit sink is logged and never refuses the person their copy. Store faults are logged
/// with their message withheld and surfaced as a fixed message, since store exceptions can carry paths and connection
/// strings.
/// </para>
/// </remarks>
public sealed class AuditedExport
{
    /// <summary>The audit action every export record carries.</summary>
    public const string AuditAction = "subject_access_export";

    /// <summary>The message returned when a fault stopped the request.</summary>
    public const string FailedMessage = "The copy of the data could not be made. The failure has been logged for investigation.";

    /// <summary>
    /// How long an export request may run before the request pipeline gives up on it: the same as erasure, since it reads
    /// every store erasure deletes from, and far above the pipeline's 30-second default.
    /// </summary>
    public static readonly TimeSpan RequestTimeout = AuditedErasure.RequestTimeout;

    private readonly IPersonalDataExport _export;
    private readonly IErasureSubjectReference _subjectReference;
    private readonly IAuditSink _auditSink;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AuditedExport> _logger;

    /// <summary>Initializes a new instance of the <see cref="AuditedExport"/> class.</summary>
    /// <param name="export">Collects one person's data from every registered store.</param>
    /// <param name="subjectReference">Pseudonymises the subject for the audit record.</param>
    /// <param name="auditSink">The audit trail the attempt and outcome are written to.</param>
    /// <param name="timeProvider">Time source for record timestamps.</param>
    /// <param name="logger">Logger for faults and audit-write failures.</param>
    public AuditedExport(
        IPersonalDataExport export,
        IErasureSubjectReference subjectReference,
        IAuditSink auditSink,
        TimeProvider timeProvider,
        ILogger<AuditedExport> logger)
    {
        ArgumentNullException.ThrowIfNull(export);
        ArgumentNullException.ThrowIfNull(subjectReference);
        ArgumentNullException.ThrowIfNull(auditSink);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _export = export;
        _subjectReference = subjectReference;
        _auditSink = auditSink;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Copies <paramref name="subjectId"/>'s data and records the attempt and its outcome.</summary>
    /// <param name="subjectId">The person, exactly as their identity provider issues the id.</param>
    /// <param name="actor">Who asked for the copy.</param>
    /// <param name="requestType">The command that carried the request, for the audit record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The copy, or a scrubbed failure when a fault stopped the request.</returns>
    public async Task<Result<PersonalDataExport>> ExportAsync(
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
            Metadata = Metadata("attempt", actor, subjectReference, export: null),
        };
        await RecordSafelyAsync(record);

        PersonalDataExport export;
        try
        {
            export = await _export.ExportOwnerAsync(subjectId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await RecordFailureAsync(record, actor, subjectReference, "cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(MessageWithheldException.From(ex), "Personal-data export {CorrelationId} failed (subject reference {SubjectReference}).",
                correlationId, subjectReference);
            await RecordFailureAsync(record, actor, subjectReference, ex.GetType().Name);
            return Result<PersonalDataExport>.Fail(FailedMessage);
        }

        _logger.LogInformation(
            "Personal-data export {CorrelationId} completed: request {RequestId}, complete {Complete}, {Stores} stores listed.",
            correlationId, export.RequestId, export.IsComplete, export.Manifest.Count);
        await RecordSafelyAsync(record with
        {
            Timestamp = _timeProvider.GetUtcNow(),
            Outcome = AuditOutcome.Success,
            Metadata = Metadata("outcome", actor, subjectReference, export),
        });

        return Result<PersonalDataExport>.Success(export);
    }

    /// <summary>Records a request refused before anything was read.</summary>
    /// <param name="actor">Who asked for the copy.</param>
    /// <param name="requestType">The command that carried the request.</param>
    /// <param name="reason">Why it was refused; a fixed phrase, never an identity.</param>
    /// <param name="subjectId">
    /// The person an operator asked for, recorded only by reference. <see langword="null"/> when the request named no one.
    /// </param>
    public Task RecordDeniedAsync(ErasureActor actor, string requestType, string reason, string? subjectId = null)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var correlationId = Guid.NewGuid().ToString("N");
        // A refused request's subject is unvalidated input of any length; hash only what a valid request could carry.
        var subjectReference = string.IsNullOrWhiteSpace(subjectId) ? null
            : subjectId.Length > EraseSubjectDataCommandValidator.MaxSubjectIdLength ? AuditedErasure.ReferenceInvalid
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
            Metadata = Metadata("outcome", actor, subjectReference, export: null),
        });
    }

    /// <summary>The subject's reference, or <see cref="AuditedErasure.ReferenceUnavailable"/> when the key is unusable.</summary>
    private string? ReferenceFor(string subjectId, string correlationId)
    {
        try
        {
            return _subjectReference.For(subjectId);
        }
        catch (Exception ex)
        {
            _logger.LogError(MessageWithheldException.From(ex), "Personal-data export {CorrelationId}: the subject reference could not be computed.",
                correlationId);
            return AuditedErasure.ReferenceUnavailable;
        }
    }

    private Task RecordFailureAsync(AuditEntry attempt, ErasureActor actor, string? subjectReference, string reason) =>
        RecordSafelyAsync(attempt with
        {
            Timestamp = _timeProvider.GetUtcNow(),
            Outcome = AuditOutcome.Failure,
            FailureReason = reason,
            Metadata = Metadata("outcome", actor, subjectReference, export: null),
        });

    private static Dictionary<string, string> Metadata(
        string phase, ErasureActor actor, string? subjectReference, PersonalDataExport? export)
    {
        var metadata = new Dictionary<string, string>
        {
            ["phase"] = phase,
            ["initiator"] = actor.Initiator,
        };
        if (subjectReference is not null)
            metadata["subject_reference"] = subjectReference;
        if (export is null)
            return metadata;

        metadata["export_request_id"] = export.RequestId;
        metadata["complete"] = export.IsComplete ? "true" : "false";
        foreach (var line in export.Manifest)
            metadata[$"store.{line.Store}"] = $"{line.Status}:{line.Records.ToString(CultureInfo.InvariantCulture)}";

        return metadata;
    }

    /// <summary>Writes one record, never cancelled by the caller, so a request abandoned part-way still leaves its outcome.</summary>
    private async Task RecordSafelyAsync(AuditEntry entry)
    {
        try
        {
            await _auditSink.RecordAsync(entry, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(MessageWithheldException.From(ex), "Failed to record the {Phase} of personal-data export {CorrelationId}.",
                entry.Metadata?.GetValueOrDefault("phase"), entry.CorrelationId);
        }
    }
}
