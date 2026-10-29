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
using Domain.Common.Compliance;

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
[PersonalDataStore(
    "Hash-chained JSONL day-files under {DriftDetection:AuditPath}/drift-audit (default data/audit): drift detections, resolutions, baseline changes and operator actions with the caller as a keyed reference (#903; records written earlier hold the raw id). Kept by design: deleting records would break the chain. A record that could not be appended is held, as the same line, in {DriftDetection:AuditPath}/drift-audit-pending until replay writes it to the chain, then deleted.",
    PersonalDataCategories.Identifiers | PersonalDataCategories.DiagnosticText,
    PersonalDataKey.Unkeyed,
    RetentionBasis.KeptByDesign,
    ErasureSupport.Exempt,
    ExportSupport.NotSupported)]
public sealed class JsonlDriftAuditStore : IDriftAuditStore, IVerifiableAuditChain, IReplaysHeldAuditRecords, IDisposable
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

        var chain = AuditChains.Drift(config.CurrentValue, timeProvider);
        _auditDirectory = chain.Segments.Folder;
        _logger = logger;
        _chain = new HashChainedJsonlWriter(chain, logger, config.CurrentValue.AI.Audit.ArchivePath);
    }

    /// <inheritdoc />
    public string AuditName => "drift";

    /// <inheritdoc />
    public string AuditLocation => _auditDirectory;

    /// <inheritdoc />
    public string PendingLocation => _chain.PendingLocation!;

    /// <inheritdoc />
    public Task<PendingRecordsReplay> ReplayPendingAsync(CancellationToken cancellationToken) =>
        _chain.ReplayPendingAsync(cancellationToken);

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
            // Through the chain's own reader, which never serves a line after the chain's first record that is not a
            // record (#416).
            await foreach (var json in _chain.ReadPayloadsAsync(IncludesFile(query), ct).ConfigureAwait(false))
            {
                try
                {
                    var record = JsonSerializer.Deserialize<DriftAuditRecord>(json, DeserializeOptions);
                    if (record is not null)
                        records.Add(record);
                }
                catch (JsonException)
                {
                    _logger.LogWarning("Skipped a corrupted drift audit record in {AuditLocation}", _auditDirectory);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Failed to read drift audit records");
            return Result<IReadOnlyList<DriftAuditRecord>>.Fail($"Failed to read audit records: {ex.Message}");
        }

        var filtered = records.AsEnumerable();

        // Files are partitioned by append time, which is ~equal to RecordedAt in practice but can
        // differ (a back-dated record, or an append that crosses midnight). Filter precisely on
        // RecordedAt so the date-range query means "records recorded in range" regardless of which
        // file they physically landed in (IncludesFile widens the file set by a day to match).
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
    /// Which of the chain's own files can hold a record from the query's start onwards: decided by each file's day, so
    /// a file archived while the query runs is still matched in the archive (#416). The chain reads only its own files.
    /// </summary>
    private static Func<string, bool> IncludesFile(DriftAuditQuery query)
    {
        // A day earlier than the start: a record lands in the file for its append time, which can differ from
        // RecordedAt by clock skew or a midnight crossing; the precise RecordedAt filter trims the extra back out.
        // No upper bound: the writer never appends to a file earlier than the latest one, so once a host whose clock
        // is ahead (or a planted later-dated file) starts a later file, every host's records land there.
        if (query.Start is not { } start)
            return static _ => true;

        var firstDay = DateOnly.FromDateTime(start.Date.AddDays(-1));
        return f => DatedAuditSegments.DayOf(f) >= firstDay;
    }
}
