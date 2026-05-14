using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.Interfaces.DriftDetection;
using Domain.AI.DriftDetection;
using Domain.Common;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.DriftDetection;

/// <summary>
/// Append-only JSONL file store for drift detection audit records.
/// Writes date-partitioned files at <c>{AuditPath}/drift-audit/{yyyy-MM-dd}.jsonl</c>.
/// Thread-safe via <see cref="SemaphoreSlim"/> for file access.
/// </summary>
/// <remarks>
/// Follows the pattern of <see cref="Escalation.JsonlEscalationAuditStore"/> with
/// date partitioning for efficient range queries. Uses <see cref="TimeProvider"/>
/// for deterministic timestamps in tests via <c>FakeTimeProvider</c>.
/// </remarks>
public sealed class JsonlDriftAuditStore : IDriftAuditStore, IDisposable
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
    private readonly ILogger<JsonlDriftAuditStore> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

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
    }

    /// <inheritdoc />
    public async Task<Result> RecordAsync(DriftAuditRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);

        var filePath = GetFilePath(record.RecordedAt);
        var line = JsonSerializer.Serialize(record, SerializeOptions) + "\n";

        await _semaphore.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_auditDirectory);
            await File.AppendAllTextAsync(filePath, line, ct);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Failed to append drift audit record to {FilePath}", filePath);
            return Result.Fail($"Failed to persist audit record: {ex.Message}");
        }
        finally
        {
            _semaphore.Release();
        }

        _logger.LogDebug(
            "Appended drift audit {RecordType} for event {EventId} to {FilePath}",
            record.RecordType, record.EventId, filePath);

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

        await _semaphore.WaitAsync(ct);
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
                        var record = JsonSerializer.Deserialize<DriftAuditRecord>(line, DeserializeOptions);
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
        finally
        {
            _semaphore.Release();
        }

        var filtered = records.AsEnumerable();

        if (query.RecordType.HasValue)
            filtered = filtered.Where(r => r.RecordType == query.RecordType.Value);

        if (query.EventId.HasValue)
            filtered = filtered.Where(r => r.EventId == query.EventId.Value);

        var result = filtered.OrderBy(r => r.RecordedAt).ToList();
        return Result<IReadOnlyList<DriftAuditRecord>>.Success(result.AsReadOnly());
    }

    /// <inheritdoc cref="IDisposable.Dispose" />
    public void Dispose()
    {
        _semaphore.Dispose();
    }

    private string GetFilePath(DateTimeOffset recordedAt) =>
        Path.Combine(_auditDirectory, $"{recordedAt:yyyy-MM-dd}.jsonl");

    private IReadOnlyList<string> ResolveFilePaths(DriftAuditQuery query)
    {
        if (query.Start.HasValue && query.End.HasValue)
            return EnumerateDatePaths(query.Start.Value.Date, query.End.Value.Date);

        if (query.Start.HasValue)
            return EnumerateDatePaths(query.Start.Value.Date, _timeProvider.GetUtcNow().Date);

        if (query.End.HasValue)
        {
            // End-only: scan existing files, filter by parsed date to avoid unbounded enumeration
            return Directory.Exists(_auditDirectory)
                ? Directory.GetFiles(_auditDirectory, "*.jsonl")
                    .Where(f => ParseDateFromFileName(f) <= query.End.Value.Date)
                    .ToList()
                : [];
        }

        return Directory.Exists(_auditDirectory)
            ? Directory.GetFiles(_auditDirectory, "*.jsonl")
            : [];
    }

    private IReadOnlyList<string> EnumerateDatePaths(DateTime start, DateTime end)
    {
        var paths = new List<string>();
        for (var date = start; date <= end; date = date.AddDays(1))
            paths.Add(Path.Combine(_auditDirectory, $"{date:yyyy-MM-dd}.jsonl"));
        return paths;
    }

    private static DateTime ParseDateFromFileName(string filePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        return DateTime.TryParseExact(fileName, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) ? date : DateTime.MaxValue;
    }
}
