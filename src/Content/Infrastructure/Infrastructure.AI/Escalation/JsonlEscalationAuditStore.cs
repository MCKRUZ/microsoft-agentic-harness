using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.Interfaces.Audit;
using Application.AI.Common.Interfaces.Escalation;
using Domain.AI.Audit;
using Domain.AI.Escalation;
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
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
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
    public async Task<IReadOnlyList<EscalationAuditRecord>> GetHistoryAsync(
        Guid escalationId,
        CancellationToken ct)
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
                if (record is not null && record.EscalationId == escalationId)
                    records.Add(record);
            }
            catch (JsonException)
            {
                _logger.LogWarning(
                    "Skipped corrupted audit record at {FilePath}:{LineNumber}",
                    _filePath, lineNumber);
            }
        }

        return records.OrderBy(r => r.Timestamp).ToList();
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
