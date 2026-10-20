using System.Globalization;
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
    private readonly TimeProvider _timeProvider;
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
        _timeProvider = timeProvider;
        _logger = logger;
        _chain = new HashChainedJsonlWriter(
            _auditDirectory,
            () => GetFilePath(_timeProvider.GetUtcNow()),
            () => Directory.Exists(_auditDirectory)
                ? Directory.GetFiles(_auditDirectory, "*.jsonl").OrderBy(f => f, StringComparer.Ordinal).ToArray()
                : [],
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
        if (!Directory.Exists(_auditDirectory))
            return Result<IReadOnlyList<DriftAuditRecord>>.Success([]);

        var filePaths = ResolveFilePaths(query);
        var records = new List<DriftAuditRecord>();

        try
        {
            foreach (var filePath in filePaths)
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

    private string GetFilePath(DateTimeOffset recordedAt) =>
        Path.Combine(_auditDirectory, recordedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");

    private IReadOnlyList<string> ResolveFilePaths(DriftAuditQuery query)
    {
        // Widen the file window by a day on each side: a record can land in the segment for its
        // append time, which may differ from RecordedAt by a clock skew / midnight crossing. The
        // precise RecordedAt filter in GetRecordsAsync trims the extra records back out.
        //
        // Files named after the window are read too. The writer never appends to a segment earlier than
        // the latest one (#416), so once a host whose clock is ahead (or a planted later-dated file) starts
        // a later segment, every host's records land there, whatever their RecordedAt.
        if (query.Start.HasValue && query.End.HasValue)
        {
            var upper = query.End.Value.Date.AddDays(1);
            return [.. EnumerateDatePaths(query.Start.Value.Date.AddDays(-1), upper), .. ExistingFilesDatedAfter(upper)];
        }

        if (query.Start.HasValue)
        {
            var upper = _timeProvider.GetUtcNow().Date;
            return [.. EnumerateDatePaths(query.Start.Value.Date.AddDays(-1), upper), .. ExistingFilesDatedAfter(upper)];
        }

        // End-only, or no window: any file can hold a record from before the end, for the reason above.
        return Directory.Exists(_auditDirectory)
            ? Directory.GetFiles(_auditDirectory, "*.jsonl")
            : [];
    }

    private IEnumerable<string> ExistingFilesDatedAfter(DateTime upper) =>
        Directory.Exists(_auditDirectory)
            ? Directory.GetFiles(_auditDirectory, "*.jsonl").Where(f => ParseDateFromFileName(f) > upper)
            : [];

    private IReadOnlyList<string> EnumerateDatePaths(DateTime start, DateTime end)
    {
        var paths = new List<string>();
        for (var date = start; date <= end; date = date.AddDays(1))
            paths.Add(Path.Combine(_auditDirectory, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl"));
        return paths;
    }

    private static DateTime ParseDateFromFileName(string filePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        return DateTime.TryParseExact(fileName, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) ? date : DateTime.MaxValue;
    }
}
