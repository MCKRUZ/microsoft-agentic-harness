using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.Interfaces.Audit;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Telemetry;
using Domain.AI.Audit;
using Domain.AI.Escalation;
using Domain.Common;
using Domain.Common.Config;
using Infrastructure.AI.Audit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Domain.Common.Compliance;

namespace Infrastructure.AI.Escalation;

/// <summary>
/// Append-only JSONL file store for escalation audit records.
/// Each line is a serialized <see cref="EscalationAuditRecord"/> with a
/// <see cref="EscalationAuditRecordType"/> discriminator, linked into a tamper-evident
/// hash-chain via <see cref="HashChainedJsonlWriter"/> so a retroactively altered or
/// deleted escalation event is detectable.
/// </summary>
/// <remarks>
/// snake_case JSON, enum-as-string, <c>FileShare.ReadWrite</c> for concurrent reads.
/// Records go to one file per day, <c>escalations/yyyy-MM-dd.jsonl</c> in the configured
/// <c>EscalationConfig.AuditStoragePath</c> directory, after <c>escalations.jsonl</c> where a log written before
/// that split continues (see <see cref="DatedAuditSegments"/>). Files are created lazily on first write.
/// </remarks>
[PersonalDataStore(
    "Hash-chained JSONL under {Escalation:AuditStoragePath} (default .agent-sessions/escalations): requests, decisions, outcomes and executions. Since #903 people are keyed references and tool arguments keyed fingerprints (IAuditPseudonymizer), and free text is redacted; records written earlier hold them raw. Kept by design: deleting records would break the chain. A record that could not be appended is held, as the same line, in {Escalation:AuditStoragePath}/escalations-pending until replay writes it to the chain, then deleted.",
    PersonalDataCategories.ToolInputsAndOutputs | PersonalDataCategories.PeopleNames | PersonalDataCategories.Identifiers | PersonalDataCategories.DiagnosticText,
    PersonalDataKey.Unkeyed,
    RetentionBasis.KeptByDesign,
    ErasureSupport.Exempt,
    ExportSupport.NotSupported)]
public sealed class JsonlEscalationAuditStore : IEscalationAuditStore, IVerifiableAuditChain, IReplaysHeldAuditRecords, IDisposable
{
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(), new ApproverDecisionJsonConverter() }
    };

    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(), new ApproverDecisionJsonConverter() }
    };

    private readonly string _filePath;
    private readonly HashChainedJsonlWriter _chain;
    private readonly ILogger<JsonlEscalationAuditStore> _logger;
    private readonly EscalationAuditMinimizer _minimizer;

    /// <summary>
    /// Initializes a new instance of <see cref="JsonlEscalationAuditStore"/>.
    /// </summary>
    /// <param name="config">Application configuration providing the audit storage path.</param>
    /// <param name="timeProvider">Decides which day-file a record is appended to (#416).</param>
    /// <param name="logger">Logger for operational diagnostics.</param>
    /// <param name="pseudonymizer">Makes the keyed references and fingerprints recorded in place of people and arguments.</param>
    /// <param name="redaction">Redacts identifiers from the free text the records keep.</param>
    public JsonlEscalationAuditStore(
        IOptionsMonitor<AppConfig> config,
        TimeProvider timeProvider,
        ILogger<JsonlEscalationAuditStore> logger,
        IAuditPseudonymizer pseudonymizer,
        IContentRedactionFilter redaction)
    {
        _minimizer = new EscalationAuditMinimizer(pseudonymizer, redaction);
        var chain = AuditChains.Escalations(config.CurrentValue, timeProvider);
        _filePath = chain.Segments.LegacyFile!;
        _chain = new HashChainedJsonlWriter(chain, logger, config.CurrentValue.AI.Audit.ArchivePath);
        _logger = logger;
    }

    /// <inheritdoc />
    public string AuditName => "escalations";

    /// <inheritdoc />
    public string AuditLocation => _filePath;

    /// <inheritdoc />
    public string PendingLocation => _chain.PendingLocation!;

    /// <inheritdoc />
    public Task<PendingRecordsReplay> ReplayPendingAsync(CancellationToken cancellationToken) =>
        _chain.ReplayPendingAsync(cancellationToken);

    /// <inheritdoc />
    public async Task RecordRequestAsync(EscalationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var record = new EscalationAuditRecord
        {
            RecordType = EscalationAuditRecordType.Request,
            EscalationId = request.EscalationId,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.Serialize(_minimizer.Minimize(request), SerializeOptions)
        };

        await AppendRecordAsync(record, ct);
    }

    /// <inheritdoc />
    public async Task RecordDecisionAsync(Guid escalationId, ApproverDecision decision, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(decision);

        var record = new EscalationAuditRecord
        {
            RecordType = EscalationAuditRecordType.Decision,
            EscalationId = escalationId,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.Serialize(_minimizer.Minimize(decision), SerializeOptions)
        };

        await AppendRecordAsync(record, ct);
    }

    /// <inheritdoc />
    public async Task RecordOutcomeAsync(EscalationOutcome outcome, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        var record = new EscalationAuditRecord
        {
            RecordType = EscalationAuditRecordType.Outcome,
            EscalationId = outcome.EscalationId,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.Serialize(_minimizer.Minimize(outcome), SerializeOptions)
        };

        await AppendRecordAsync(record, ct);
    }

    /// <inheritdoc />
    public async Task RecordExecutionAsync(EscalationExecutionRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);

        var auditRecord = new EscalationAuditRecord
        {
            RecordType = EscalationAuditRecordType.Execution,
            EscalationId = record.EscalationId,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.Serialize(record, SerializeOptions)
        };

        await AppendRecordAsync(auditRecord, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EscalationAuditRecord>> GetHistoryAsync(
        Guid escalationId,
        CancellationToken ct)
    {
        var records = await ReadAllRecordsAsync(ct);
        return records.Where(r => r.EscalationId == escalationId).OrderBy(r => r.Timestamp).ToList();
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<EscalationAuditRecord>>> QueryAsync(
        EscalationAuditQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        List<EscalationAuditRecord> records;
        try
        {
            records = await ReadAllRecordsAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Failed to read escalation audit records from {FilePath}", _filePath);
            return Result<IReadOnlyList<EscalationAuditRecord>>.Fail($"Failed to read audit records: {ex.Message}");
        }

        var filtered = records.AsEnumerable();
        if (query.Start.HasValue)
            filtered = filtered.Where(r => r.Timestamp >= query.Start.Value);
        if (query.End.HasValue)
            filtered = filtered.Where(r => r.Timestamp <= query.End.Value);
        if (query.EscalationId.HasValue)
            filtered = filtered.Where(r => r.EscalationId == query.EscalationId.Value);
        if (query.RecordType.HasValue)
            filtered = filtered.Where(r => r.RecordType == query.RecordType.Value);

        var result = filtered.OrderBy(r => r.Timestamp).ToList();
        return Result<IReadOnlyList<EscalationAuditRecord>>.Success(result.AsReadOnly());
    }

    /// <summary>
    /// Reads every record from the chain, across every file it is stored in, tolerating corrupt lines by
    /// skipping them with a warning. Shared by <see cref="GetHistoryAsync"/> (which then filters
    /// to one escalation) and <see cref="QueryAsync"/> (which applies the caller's filters).
    /// </summary>
    private async Task<List<EscalationAuditRecord>> ReadAllRecordsAsync(CancellationToken ct)
    {
        var records = new List<EscalationAuditRecord>();

        await foreach (var json in _chain.ReadAllPayloadsAsync(ct).ConfigureAwait(false))
        {
            try
            {
                var record = JsonSerializer.Deserialize<EscalationAuditRecord>(json, DeserializeOptions);
                if (record is not null)
                    records.Add(record);
            }
            catch (JsonException)
            {
                _logger.LogWarning("Skipped corrupted escalation audit record in {FilePath}", _filePath);
            }
        }

        return records;
    }

    /// <inheritdoc />
    public async Task<EscalationExecutionRecord?> GetLatestExecutionAsync(Guid escalationId, CancellationToken ct)
    {
        var history = await GetHistoryAsync(escalationId, ct);
        var latest = history.LastOrDefault(r => r.RecordType == EscalationAuditRecordType.Execution);
        if (latest is null)
            return null;

        try
        {
            return JsonSerializer.Deserialize<EscalationExecutionRecord>(latest.Payload, DeserializeOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "Failed to deserialize execution audit record for escalation {EscalationId}",
                escalationId);
            return null;
        }
    }

    /// <inheritdoc />
    public Task<AuditChainVerificationResult> VerifyChainAsync(
        AuditChainCheckpoint? checkpoint, CancellationToken cancellationToken) =>
        _chain.VerifyChainAsync(checkpoint, cancellationToken);

    /// <inheritdoc cref="IDisposable.Dispose" />
    public void Dispose() => _chain.Dispose();

    /// <summary>
    /// Serializes and appends a single audit record as one hash-chained JSONL line.
    /// </summary>
    private async Task AppendRecordAsync(EscalationAuditRecord record, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(record, SerializeOptions);
        // Held as it is (#1001): every escalation record says something that happened, whatever the caller does next (an
        // execution report has already run; a resolution's outcome is reconciled and appended again).
        var result = await _chain.AppendAsync(json, ct);
        if (!result.IsSuccess)
        {
            var reason = string.Join("; ", result.Errors);
            _logger.LogError(
                "Failed to append escalation audit {RecordType} for {EscalationId}: {Reason}",
                record.RecordType, record.EscalationId, reason);
            throw new IOException(
                $"Failed to append escalation audit record for {record.EscalationId}: {reason}");
        }

        _logger.LogDebug(
            "Appended escalation audit {RecordType} for {EscalationId} to {FilePath}",
            record.RecordType, record.EscalationId, _filePath);
    }
}
