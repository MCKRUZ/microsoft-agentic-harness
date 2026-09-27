using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.Interfaces.Audit;
using Application.AI.Common.Interfaces.Escalation;
using Domain.AI.Audit;
using Domain.AI.Escalation;
using Domain.Common;
using Domain.Common.Config;
using Infrastructure.AI.Audit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
/// The file is created lazily on first write in the configured
/// <c>EscalationConfig.AuditStoragePath</c> directory.
/// </remarks>
public sealed class JsonlEscalationAuditStore : IEscalationAuditStore, IVerifiableAuditChain, IDisposable
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

    /// <summary>
    /// Initializes a new instance of <see cref="JsonlEscalationAuditStore"/>.
    /// </summary>
    /// <param name="config">Application configuration providing the audit storage path.</param>
    /// <param name="logger">Logger for operational diagnostics.</param>
    public JsonlEscalationAuditStore(
        IOptionsMonitor<AppConfig> config,
        ILogger<JsonlEscalationAuditStore> logger)
    {
        _filePath = Path.Combine(
            config.CurrentValue.AI.Governance.Escalation.AuditStoragePath,
            "escalations.jsonl");
        _chain = new HashChainedJsonlWriter(_filePath, logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public string AuditName => "escalations";

    /// <inheritdoc />
    public async Task RecordRequestAsync(EscalationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var record = new EscalationAuditRecord
        {
            RecordType = EscalationAuditRecordType.Request,
            EscalationId = request.EscalationId,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.Serialize(request, SerializeOptions)
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
            Payload = JsonSerializer.Serialize(decision, SerializeOptions)
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
            Payload = JsonSerializer.Serialize(outcome, SerializeOptions)
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
    /// Reads every record from the chain, across the whole file, tolerating corrupt lines by
    /// skipping them with a warning. Shared by <see cref="GetHistoryAsync"/> (which then filters
    /// to one escalation) and <see cref="QueryAsync"/> (which applies the caller's filters).
    /// </summary>
    private async Task<List<EscalationAuditRecord>> ReadAllRecordsAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
            return [];

        var records = new List<EscalationAuditRecord>();

        await using var stream = new FileStream(
            _filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        var lineNumber = 0;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            lineNumber++;

            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var json = HashChainedJsonlWriter.ExtractPayload(line);
                var record = JsonSerializer.Deserialize<EscalationAuditRecord>(json, DeserializeOptions);
                if (record is not null)
                    records.Add(record);
            }
            catch (JsonException)
            {
                _logger.LogWarning(
                    "Skipped corrupted audit record at {FilePath}:{LineNumber}",
                    _filePath, lineNumber);
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
    public Task<AuditChainVerificationResult> VerifyChainAsync(CancellationToken cancellationToken) =>
        _chain.VerifyChainAsync(cancellationToken);

    /// <inheritdoc cref="IDisposable.Dispose" />
    public void Dispose() => _chain.Dispose();

    /// <summary>
    /// Serializes and appends a single audit record as one hash-chained JSONL line.
    /// </summary>
    private async Task AppendRecordAsync(EscalationAuditRecord record, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(record, SerializeOptions);
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
