using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.Interfaces.Escalation;
using Domain.AI.Escalation;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Escalation;

/// <summary>
/// Append-only JSONL file store for escalation audit records.
/// Each line is a serialized <see cref="EscalationAuditRecord"/> with a
/// <see cref="EscalationAuditRecordType"/> discriminator.
/// Thread-safe via a single <see cref="SemaphoreSlim"/> for file access.
/// </summary>
/// <remarks>
/// Follows the same pattern as <see cref="Agents.JsonlDelegationStore"/>:
/// snake_case JSON, enum-as-string, <c>FileShare.ReadWrite</c> for concurrent reads.
/// The file is created lazily on first write in the configured
/// <c>EscalationConfig.AuditStoragePath</c> directory.
/// </remarks>
public sealed class JsonlEscalationAuditStore : IEscalationAuditStore, IDisposable
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
    private readonly ILogger<JsonlEscalationAuditStore> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

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
        _logger = logger;
    }

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

        await _semaphore.WaitAsync(ct);
        try
        {
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
                    var record = JsonSerializer.Deserialize<EscalationAuditRecord>(line, DeserializeOptions);
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
        }
        finally
        {
            _semaphore.Release();
        }

        return records.OrderBy(r => r.Timestamp).ToList();
    }

    /// <inheritdoc cref="IDisposable.Dispose" />
    public void Dispose()
    {
        _semaphore.Dispose();
    }

    /// <summary>
    /// Serializes and appends a single audit record as one JSONL line.
    /// </summary>
    private async Task AppendRecordAsync(EscalationAuditRecord record, CancellationToken ct)
    {
        var line = JsonSerializer.Serialize(record, SerializeOptions) + "\n";

        await _semaphore.WaitAsync(ct);
        try
        {
            EnsureDirectoryExists(_filePath);
            await File.AppendAllTextAsync(_filePath, line, ct);
        }
        finally
        {
            _semaphore.Release();
        }

        _logger.LogDebug(
            "Appended escalation audit {RecordType} for {EscalationId} to {FilePath}",
            record.RecordType, record.EscalationId, _filePath);
    }

    /// <summary>
    /// Ensures the parent directory for the given file path exists.
    /// </summary>
    private static void EnsureDirectoryExists(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (dir is not null)
            Directory.CreateDirectory(dir);
    }
}
