using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.Interfaces.Audit;
using Application.AI.Common.Interfaces.DriftDetection;
using Domain.AI.Audit;
using Domain.AI.DriftDetection;
using Domain.Common;
using Domain.Common.Config;
using Infrastructure.AI.Audit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.DriftDetection;

/// <summary>
/// Append-only JSONL store for drift detection audit records, date-partitioned at
/// <c>{AuditPath}/drift-audit/{yyyy-MM-dd}.jsonl</c> and linked into a tamper-evident
/// hash-chain that spans the date files via <see cref="HashChainedJsonlWriter"/>.
/// </summary>
/// <remarks>
/// Records are written to the segment for the current append time (from <see cref="TimeProvider"/>),
/// which keeps the global chain sequence monotonic across days so deleting an entire date file
/// breaks the chain exactly as deleting a single record would. Date partitioning is preserved for
/// efficient range queries; <see cref="GetRecordsAsync"/> resolves only the files in range.
/// </remarks>
public sealed class JsonlDriftAuditStore : IDriftAuditStore, IVerifiableAuditChain, IDisposable
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

    private readonly string _auditDirectory;
    private readonly DatedAuditSegments _segments;
    private readonly HashChainedJsonlWriter _chain;
    private readonly ILogger<JsonlDriftAuditStore> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="JsonlDriftAuditStore"/>.
    /// </summary>
    /// <param name="config">Application configuration providing the audit storage path.</param>
    /// <param name="timeProvider">Time provider for deterministic timestamps.</param>
    /// <param name="logger">Logger for operational diagnostics.</param>
    public JsonlDriftAuditStore(
        IOptionsMonitor<AppConfig> config,
        TimeProvider timeProvider,
        ILogger<JsonlDriftAuditStore> logger)
    {
        var auditPath = config.CurrentValue.AI.DriftDetection.AuditPath;
        ArgumentException.ThrowIfNullOrWhiteSpace(auditPath, "DriftDetection.AuditPath");

        _auditDirectory = Path.Combine(auditPath, "drift-audit");
        _logger = logger;
        _segments = new DatedAuditSegments(_auditDirectory, legacyFile: null, timeProvider);
        _chain = new HashChainedJsonlWriter(
            _segments,
            // Shared by every process writing these segments; not itself a *.jsonl segment.
            Path.Combine(_auditDirectory, ".chain.lock"),
            logger);
    }

    /// <inheritdoc />
    public string AuditName => "drift";

    /// <inheritdoc />
    public string AuditLocation => _auditDirectory;

    /// <inheritdoc />
    public async Task<Result> RecordAsync(DriftAuditRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);

        var json = JsonSerializer.Serialize(record, SerializeOptions);
        var result = await _chain.AppendAsync(json, ct);
        if (!result.IsSuccess)
            return result;

        _logger.LogDebug(
            "Appended drift audit {RecordType} for event {EventId}",
            record.RecordType, record.EventId);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<DriftAuditRecord>>> GetRecordsAsync(
        DriftAuditQuery query, CancellationToken ct)
    {
        var records = new List<DriftAuditRecord>();

        try
        {
            foreach (var filePath in ResolveFilePaths(query))
            {
                if (!File.Exists(filePath))
                    continue;

                await using var stream = new FileStream(
                    filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
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
                        var record = JsonSerializer.Deserialize<DriftAuditRecord>(json, DeserializeOptions);
                        if (record is not null)
                            records.Add(record);
                    }
                    catch (JsonException)
                    {
                        _logger.LogWarning(
                            "Skipped corrupted drift audit record at {FilePath}:{LineNumber}",
                            filePath, lineNumber);
                    }
                }
            }
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Failed to read drift audit records");
            return Result<IReadOnlyList<DriftAuditRecord>>.Fail($"Failed to read audit records: {ex.Message}");
        }

        var filtered = records.AsEnumerable();

        // Files are partitioned by append time, which is ~equal to RecordedAt in practice but can
        // differ (a back-dated record, or an append that crosses midnight). Filter precisely on
        // RecordedAt so the date-range query means "records recorded in range" regardless of which
        // file they physically landed in (ResolveFilePaths widens the file set by a day to match).
        if (query.Start.HasValue)
            filtered = filtered.Where(r => r.RecordedAt >= query.Start.Value);

        if (query.End.HasValue)
            filtered = filtered.Where(r => r.RecordedAt <= query.End.Value);

        if (query.RecordType.HasValue)
            filtered = filtered.Where(r => r.RecordType == query.RecordType.Value);

        if (query.EventId.HasValue)
            filtered = filtered.Where(r => r.EventId == query.EventId.Value);

        var result = filtered.OrderBy(r => r.RecordedAt).ToList();
        return Result<IReadOnlyList<DriftAuditRecord>>.Success(result.AsReadOnly());
    }

    /// <inheritdoc />
    public Task<AuditChainVerificationResult> VerifyChainAsync(
        AuditChainCheckpoint? checkpoint, CancellationToken cancellationToken) =>
        _chain.VerifyChainAsync(checkpoint, cancellationToken);

    /// <inheritdoc cref="IDisposable.Dispose" />
    public void Dispose() => _chain.Dispose();

    /// <summary>
    /// The chain's own day-files that can hold a record from the query's start onwards. Only the chain's files: a
    /// file the chain does not list is never verified, so it is never served either (#416).
    /// </summary>
    private IEnumerable<string> ResolveFilePaths(DriftAuditQuery query)
    {
        // A day earlier than the start: a record lands in the file for its append time, which can differ from
        // RecordedAt by clock skew or a midnight crossing; the precise RecordedAt filter trims the extra back out.
        // No upper bound: the writer never appends to a file earlier than the latest one, so once a host whose clock
        // is ahead (or a planted later-dated file) starts a later file, every host's records land there.
        if (query.Start is not { } start)
            return _segments.Ordered();

        var firstDay = DateOnly.FromDateTime(start.Date.AddDays(-1));
        return _segments.Ordered().Where(f => DatedAuditSegments.DayOf(f) >= firstDay);
    }
}
