diff --git a/src/Content/Infrastructure/Infrastructure.AI/DriftDetection/JsonlDriftAuditStore.cs b/src/Content/Infrastructure/Infrastructure.AI/DriftDetection/JsonlDriftAuditStore.cs
new file mode 100644
index 0000000..aa54d8b
--- /dev/null
+++ b/src/Content/Infrastructure/Infrastructure.AI/DriftDetection/JsonlDriftAuditStore.cs
@@ -0,0 +1,175 @@
+using System.Text.Json;
+using System.Text.Json.Serialization;
+using Application.AI.Common.Interfaces.DriftDetection;
+using Domain.AI.DriftDetection;
+using Domain.Common;
+using Domain.Common.Config;
+using Microsoft.Extensions.Logging;
+using Microsoft.Extensions.Options;
+
+namespace Infrastructure.AI.DriftDetection;
+
+/// <summary>
+/// Append-only JSONL file store for drift detection audit records.
+/// Writes date-partitioned files at <c>{AuditPath}/drift-audit/{yyyy-MM-dd}.jsonl</c>.
+/// Thread-safe via <see cref="SemaphoreSlim"/> for file access.
+/// </summary>
+/// <remarks>
+/// Follows the pattern of <see cref="Escalation.JsonlEscalationAuditStore"/> with
+/// date partitioning for efficient range queries. Uses <see cref="TimeProvider"/>
+/// for deterministic timestamps in tests via <c>FakeTimeProvider</c>.
+/// </remarks>
+public sealed class JsonlDriftAuditStore : IDriftAuditStore, IDisposable
+{
+    private static readonly JsonSerializerOptions SerializeOptions = new()
+    {
+        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
+        WriteIndented = false,
+        Converters = { new JsonStringEnumConverter() }
+    };
+
+    private static readonly JsonSerializerOptions DeserializeOptions = new()
+    {
+        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
+        PropertyNameCaseInsensitive = true,
+        Converters = { new JsonStringEnumConverter() }
+    };
+
+    private readonly string _auditDirectory;
+    private readonly TimeProvider _timeProvider;
+    private readonly ILogger<JsonlDriftAuditStore> _logger;
+    private readonly SemaphoreSlim _semaphore = new(1, 1);
+
+    /// <summary>
+    /// Initializes a new instance of <see cref="JsonlDriftAuditStore"/>.
+    /// </summary>
+    /// <param name="config">Application configuration providing the audit storage path.</param>
+    /// <param name="timeProvider">Time provider for deterministic timestamps.</param>
+    /// <param name="logger">Logger for operational diagnostics.</param>
+    public JsonlDriftAuditStore(
+        IOptionsMonitor<AppConfig> config,
+        TimeProvider timeProvider,
+        ILogger<JsonlDriftAuditStore> logger)
+    {
+        _auditDirectory = Path.Combine(
+            config.CurrentValue.AI.DriftDetection.AuditPath,
+            "drift-audit");
+        _timeProvider = timeProvider;
+        _logger = logger;
+    }
+
+    /// <inheritdoc />
+    public async Task<Result> RecordAsync(DriftAuditRecord record, CancellationToken ct)
+    {
+        ArgumentNullException.ThrowIfNull(record);
+
+        var filePath = GetFilePath(record.RecordedAt);
+        var line = JsonSerializer.Serialize(record, SerializeOptions) + "\n";
+
+        await _semaphore.WaitAsync(ct);
+        try
+        {
+            Directory.CreateDirectory(_auditDirectory);
+            await File.AppendAllTextAsync(filePath, line, ct);
+        }
+        finally
+        {
+            _semaphore.Release();
+        }
+
+        _logger.LogDebug(
+            "Appended drift audit {RecordType} for event {EventId} to {FilePath}",
+            record.RecordType, record.EventId, filePath);
+
+        return Result.Success();
+    }
+
+    /// <inheritdoc />
+    public async Task<Result<IReadOnlyList<DriftAuditRecord>>> GetRecordsAsync(
+        DriftAuditQuery query, CancellationToken ct)
+    {
+        if (!Directory.Exists(_auditDirectory))
+            return Result<IReadOnlyList<DriftAuditRecord>>.Success([]);
+
+        var filePaths = ResolveFilePaths(query);
+        var records = new List<DriftAuditRecord>();
+
+        foreach (var filePath in filePaths)
+        {
+            if (!File.Exists(filePath))
+                continue;
+
+            await _semaphore.WaitAsync(ct);
+            try
+            {
+                await using var stream = new FileStream(
+                    filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
+                using var reader = new StreamReader(stream);
+
+                var lineNumber = 0;
+                while (await reader.ReadLineAsync(ct) is { } line)
+                {
+                    lineNumber++;
+                    if (string.IsNullOrWhiteSpace(line))
+                        continue;
+
+                    try
+                    {
+                        var record = JsonSerializer.Deserialize<DriftAuditRecord>(line, DeserializeOptions);
+                        if (record is not null)
+                            records.Add(record);
+                    }
+                    catch (JsonException)
+                    {
+                        _logger.LogWarning(
+                            "Skipped corrupted drift audit record at {FilePath}:{LineNumber}",
+                            filePath, lineNumber);
+                    }
+                }
+            }
+            finally
+            {
+                _semaphore.Release();
+            }
+        }
+
+        var filtered = records.AsEnumerable();
+
+        if (query.RecordType.HasValue)
+            filtered = filtered.Where(r => r.RecordType == query.RecordType.Value);
+
+        if (query.EventId.HasValue)
+            filtered = filtered.Where(r => r.EventId == query.EventId.Value);
+
+        var result = filtered.OrderBy(r => r.RecordedAt).ToList();
+        return Result<IReadOnlyList<DriftAuditRecord>>.Success(result.AsReadOnly());
+    }
+
+    /// <inheritdoc cref="IDisposable.Dispose" />
+    public void Dispose()
+    {
+        _semaphore.Dispose();
+    }
+
+    private string GetFilePath(DateTimeOffset recordedAt) =>
+        Path.Combine(_auditDirectory, $"{recordedAt:yyyy-MM-dd}.jsonl");
+
+    private IReadOnlyList<string> ResolveFilePaths(DriftAuditQuery query)
+    {
+        if (query.Start.HasValue || query.End.HasValue)
+        {
+            var start = (query.Start ?? DateTimeOffset.MinValue).Date;
+            var end = (query.End ?? _timeProvider.GetUtcNow()).Date;
+
+            var paths = new List<string>();
+            for (var date = start; date <= end; date = date.AddDays(1))
+                paths.Add(Path.Combine(_auditDirectory, $"{date:yyyy-MM-dd}.jsonl"));
+
+            return paths;
+        }
+
+        return Directory.Exists(_auditDirectory)
+            ? Directory.GetFiles(_auditDirectory, "*.jsonl")
+            : [];
+    }
+}
diff --git a/src/Content/Tests/Infrastructure.AI.Tests/DriftDetection/JsonlDriftAuditStoreTests.cs b/src/Content/Tests/Infrastructure.AI.Tests/DriftDetection/JsonlDriftAuditStoreTests.cs
new file mode 100644
index 0000000..fbadb3d
--- /dev/null
+++ b/src/Content/Tests/Infrastructure.AI.Tests/DriftDetection/JsonlDriftAuditStoreTests.cs
@@ -0,0 +1,190 @@
+using System.Text.Json;
+using Application.AI.Common.Interfaces.DriftDetection;
+using Domain.AI.DriftDetection;
+using Domain.Common.Config;
+using Domain.Common.Config.AI;
+using Domain.Common.Config.AI.DriftDetection;
+using FluentAssertions;
+using Infrastructure.AI.DriftDetection;
+using Microsoft.Extensions.Logging;
+using Microsoft.Extensions.Options;
+using Microsoft.Extensions.Time.Testing;
+using Moq;
+using Xunit;
+
+namespace Infrastructure.AI.Tests.DriftDetection;
+
+public sealed class JsonlDriftAuditStoreTests : IDisposable
+{
+    private readonly string _tempDir;
+    private readonly FakeTimeProvider _timeProvider;
+    private readonly JsonlDriftAuditStore _store;
+
+    public JsonlDriftAuditStoreTests()
+    {
+        _tempDir = Path.Combine(Path.GetTempPath(), $"drift-audit-tests-{Guid.NewGuid():N}");
+        _timeProvider = new FakeTimeProvider(new DateTimeOffset(2025, 6, 15, 10, 0, 0, TimeSpan.Zero));
+
+        var config = new AppConfig
+        {
+            AI = new AIConfig
+            {
+                DriftDetection = new DriftDetectionConfig { AuditPath = _tempDir }
+            }
+        };
+        var options = Mock.Of<IOptionsMonitor<AppConfig>>(o => o.CurrentValue == config);
+        _store = new JsonlDriftAuditStore(options, _timeProvider, Mock.Of<ILogger<JsonlDriftAuditStore>>());
+    }
+
+    public void Dispose()
+    {
+        _store.Dispose();
+        if (Directory.Exists(_tempDir))
+            Directory.Delete(_tempDir, recursive: true);
+    }
+
+    private DriftAuditRecord BuildRecord(
+        Guid? eventId = null,
+        DriftAuditRecordType recordType = DriftAuditRecordType.Detected,
+        DateTimeOffset? recordedAt = null) => new()
+    {
+        RecordId = Guid.NewGuid(),
+        EventId = eventId ?? Guid.NewGuid(),
+        RecordType = recordType,
+        Payload = JsonSerializer.Serialize(new { detail = "test" }),
+        RecordedAt = recordedAt ?? _timeProvider.GetUtcNow()
+    };
+
+    [Fact]
+    public async Task Record_AppendsToJsonlFile()
+    {
+        var record = BuildRecord();
+
+        var result = await _store.RecordAsync(record, CancellationToken.None);
+
+        result.IsSuccess.Should().BeTrue();
+        var filePath = Path.Combine(_tempDir, "drift-audit", "2025-06-15.jsonl");
+        File.Exists(filePath).Should().BeTrue();
+        var lines = await File.ReadAllLinesAsync(filePath);
+        lines.Where(l => !string.IsNullOrWhiteSpace(l)).Should().HaveCount(1);
+    }
+
+    [Fact]
+    public async Task Record_CreatesDatePartitionedFile()
+    {
+        await _store.RecordAsync(BuildRecord(), CancellationToken.None);
+
+        _timeProvider.Advance(TimeSpan.FromDays(1));
+        await _store.RecordAsync(
+            BuildRecord(recordedAt: _timeProvider.GetUtcNow()),
+            CancellationToken.None);
+
+        File.Exists(Path.Combine(_tempDir, "drift-audit", "2025-06-15.jsonl")).Should().BeTrue();
+        File.Exists(Path.Combine(_tempDir, "drift-audit", "2025-06-16.jsonl")).Should().BeTrue();
+    }
+
+    [Fact]
+    public async Task GetRecords_FiltersByDateRange()
+    {
+        // Day 1
+        await _store.RecordAsync(BuildRecord(), CancellationToken.None);
+
+        // Day 2
+        _timeProvider.Advance(TimeSpan.FromDays(1));
+        await _store.RecordAsync(
+            BuildRecord(recordedAt: _timeProvider.GetUtcNow()),
+            CancellationToken.None);
+
+        // Day 3
+        _timeProvider.Advance(TimeSpan.FromDays(1));
+        await _store.RecordAsync(
+            BuildRecord(recordedAt: _timeProvider.GetUtcNow()),
+            CancellationToken.None);
+
+        var query = new DriftAuditQuery
+        {
+            Start = new DateTimeOffset(2025, 6, 16, 0, 0, 0, TimeSpan.Zero),
+            End = new DateTimeOffset(2025, 6, 16, 23, 59, 59, TimeSpan.Zero)
+        };
+
+        var result = await _store.GetRecordsAsync(query, CancellationToken.None);
+
+        result.IsSuccess.Should().BeTrue();
+        result.Value!.Should().HaveCount(1);
+        result.Value[0].RecordedAt.Date.Should().Be(new DateTime(2025, 6, 16));
+    }
+
+    [Fact]
+    public async Task GetRecords_FiltersByRecordType()
+    {
+        await _store.RecordAsync(BuildRecord(recordType: DriftAuditRecordType.Detected), CancellationToken.None);
+        await _store.RecordAsync(BuildRecord(recordType: DriftAuditRecordType.Resolved), CancellationToken.None);
+        await _store.RecordAsync(BuildRecord(recordType: DriftAuditRecordType.BaselineUpdated), CancellationToken.None);
+
+        var query = new DriftAuditQuery { RecordType = DriftAuditRecordType.Detected };
+
+        var result = await _store.GetRecordsAsync(query, CancellationToken.None);
+
+        result.IsSuccess.Should().BeTrue();
+        result.Value!.Should().HaveCount(1);
+        result.Value[0].RecordType.Should().Be(DriftAuditRecordType.Detected);
+    }
+
+    [Fact]
+    public async Task GetRecords_FiltersByEventId()
+    {
+        var targetEventId = Guid.NewGuid();
+        await _store.RecordAsync(BuildRecord(eventId: targetEventId), CancellationToken.None);
+        await _store.RecordAsync(BuildRecord(), CancellationToken.None);
+        await _store.RecordAsync(BuildRecord(), CancellationToken.None);
+
+        var query = new DriftAuditQuery { EventId = targetEventId };
+
+        var result = await _store.GetRecordsAsync(query, CancellationToken.None);
+
+        result.IsSuccess.Should().BeTrue();
+        result.Value!.Should().HaveCount(1);
+        result.Value[0].EventId.Should().Be(targetEventId);
+    }
+
+    [Fact]
+    public async Task Record_ThreadSafe_ConcurrentWrites()
+    {
+        var eventIds = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToList();
+        var tasks = eventIds.Select(id =>
+            _store.RecordAsync(BuildRecord(eventId: id), CancellationToken.None));
+
+        var results = await Task.WhenAll(tasks);
+
+        results.Should().OnlyContain(r => r.IsSuccess);
+        var filePath = Path.Combine(_tempDir, "drift-audit", "2025-06-15.jsonl");
+        var lines = (await File.ReadAllLinesAsync(filePath))
+            .Where(l => !string.IsNullOrWhiteSpace(l))
+            .ToList();
+        lines.Should().HaveCount(20);
+
+        var allRecords = await _store.GetRecordsAsync(new DriftAuditQuery(), CancellationToken.None);
+        allRecords.Value!.Select(r => r.EventId).Should().BeEquivalentTo(eventIds);
+    }
+
+    [Fact]
+    public async Task Record_UsesTimeProviderForTimestamp()
+    {
+        _timeProvider.SetUtcNow(new DateTimeOffset(2025, 12, 25, 8, 0, 0, TimeSpan.Zero));
+        await _store.RecordAsync(
+            BuildRecord(recordedAt: _timeProvider.GetUtcNow()),
+            CancellationToken.None);
+
+        var filePath = Path.Combine(_tempDir, "drift-audit", "2025-12-25.jsonl");
+        File.Exists(filePath).Should().BeTrue();
+    }
+
+    [Fact]
+    public async Task GetRecords_EmptyDirectory_ReturnsEmpty()
+    {
+        var result = await _store.GetRecordsAsync(new DriftAuditQuery(), CancellationToken.None);
+
+        result.IsSuccess.Should().BeTrue();
+        result.Value!.Should().BeEmpty();
+    }
+}
