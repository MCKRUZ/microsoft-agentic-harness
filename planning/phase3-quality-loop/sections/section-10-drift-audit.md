# Section 10: Drift Audit Store

## Status: IMPLEMENTED

## Implementation Notes

- I/O exceptions wrapped in `Result.Fail` (plan didn't specify, added per code review)
- `ResolveFilePaths` uses directory listing + date filtering for End-only queries to prevent unbounded enumeration
- Semaphore acquired once around entire file-reading loop (not per-file) for read consistency
- `AuditPath` validated in constructor — throws `ArgumentException` if null/empty
- DI registration deferred to Section 18 per plan
- 8 tests: append, partitioning, date range, record type, event ID, concurrent writes, time provider, empty directory

## Overview

This section implements `JsonlDriftAuditStore`, the concrete `IDriftAuditStore` for append-only compliance logging of drift detection lifecycle events. It writes date-partitioned JSONL files with thread-safe appends via `SemaphoreSlim`, mirroring the established `JsonlEscalationAuditStore` pattern.

The key difference from the escalation audit store: drift audit uses **date-partitioned files** (`{auditPath}/drift-audit/{yyyy-MM-dd}.jsonl`) rather than a single file. This enables efficient date-range queries without scanning the entire history. The store uses `TimeProvider` for deterministic timestamps (testability), unlike the escalation store which uses `DateTimeOffset.UtcNow` directly.

## Dependencies

- **Section 1 (drift-domain):** Provides `DriftAuditRecord`, `DriftAuditRecordType` domain types from `Domain.AI.DriftDetection`.
- **Section 3 (drift-config):** Provides `DriftDetectionConfig` with the `AuditPath` property (default: `"data/audit"`).
- **Section 5 (drift-interfaces):** Provides `IDriftAuditStore` interface and `DriftAuditQuery` DTO from `Application.AI.Common.Interfaces.DriftDetection`.

## Downstream Consumers

- Section 8 (drift service) -- calls `IDriftAuditStore.RecordAsync` during evaluation pipeline
- Section 18 (DI registration) -- registers `JsonlDriftAuditStore` as the `IDriftAuditStore` implementation

## Files to Create

| File | Project | Purpose |
|------|---------|---------|
| `src/Content/Infrastructure/Infrastructure.AI/DriftDetection/JsonlDriftAuditStore.cs` | Infrastructure.AI | Date-partitioned JSONL audit store |
| `src/Content/Tests/Infrastructure.AI.Tests/DriftDetection/JsonlDriftAuditStoreTests.cs` | Infrastructure.AI.Tests | Thread safety, partitioning, query tests |

---

## Tests First

### Test File

Path: `src/Content/Tests/Infrastructure.AI.Tests/DriftDetection/JsonlDriftAuditStoreTests.cs`

The test class follows the pattern from `JsonlEscalationAuditStoreTests`: temp directory per test run, `IDisposable` cleanup, `FakeTimeProvider` for deterministic time control, `IOptionsMonitor<AppConfig>` mocked with the temp directory as the audit path.

```csharp
using System.Text.Json;
using Domain.AI.DriftDetection;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.DriftDetection;
using Application.AI.Common.Interfaces.DriftDetection;
using FluentAssertions;
using Infrastructure.AI.DriftDetection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.DriftDetection;

/// <summary>
/// Tests for <see cref="JsonlDriftAuditStore"/>.
/// Validates date-partitioned JSONL file creation, append-only semantics,
/// thread-safe concurrent writes, and query filtering by date range,
/// record type, and event ID.
/// </summary>
public sealed class JsonlDriftAuditStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FakeTimeProvider _timeProvider;
    private readonly JsonlDriftAuditStore _store;

    public JsonlDriftAuditStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"drift-audit-tests-{Guid.NewGuid():N}");
        _timeProvider = new FakeTimeProvider(new DateTimeOffset(2025, 6, 15, 10, 0, 0, TimeSpan.Zero));

        var config = new AppConfig
        {
            AI = new AIConfig
            {
                DriftDetection = new DriftDetectionConfig { AuditPath = _tempDir }
            }
        };
        var options = Mock.Of<IOptionsMonitor<AppConfig>>(o => o.CurrentValue == config);
        _store = new JsonlDriftAuditStore(options, _timeProvider, Mock.Of<ILogger<JsonlDriftAuditStore>>());
    }

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // Helper: build a DriftAuditRecord with specified or default values
    private DriftAuditRecord BuildRecord(
        Guid? eventId = null,
        DriftAuditRecordType recordType = DriftAuditRecordType.Detected,
        DateTimeOffset? recordedAt = null) => new()
    {
        RecordId = Guid.NewGuid(),
        EventId = eventId ?? Guid.NewGuid(),
        RecordType = recordType,
        Data = JsonSerializer.Serialize(new { detail = "test" }),
        RecordedAt = recordedAt ?? _timeProvider.GetUtcNow()
    };

    // Test: Record_AppendsToJsonlFile
    //   Write one record, verify the JSONL file exists and contains exactly one non-empty line.

    // Test: Record_CreatesDatePartitionedFile
    //   Write records on two different dates (advance FakeTimeProvider by 1 day between writes).
    //   Verify two separate files exist: drift-audit/2025-06-15.jsonl and drift-audit/2025-06-16.jsonl.

    // Test: GetRecords_FiltersByDateRange
    //   Write records across 3 days. Query with Start/End covering only the middle day.
    //   Verify only the middle day's records are returned.

    // Test: GetRecords_FiltersByRecordType
    //   Write records with different RecordType values (Detected, Resolved, BaselineUpdated).
    //   Query with RecordType = Detected. Verify only Detected records are returned.

    // Test: GetRecords_FiltersByEventId
    //   Write multiple records for different event IDs.
    //   Query with a specific EventId. Verify only that event's records are returned.

    // Test: Record_ThreadSafe_ConcurrentWrites
    //   Launch 20 concurrent tasks each writing a record.
    //   Verify the JSONL file has exactly 20 non-empty lines and no corruption.
    //   Read back all records and verify each one's EventId is unique and present.

    // Test: Record_UsesTimeProviderForTimestamp
    //   Set FakeTimeProvider to a known time. Write a record.
    //   Verify the file partition (filename) matches the date from TimeProvider, not wall clock.
}
```

### Test Design Notes

1. **`FakeTimeProvider`** controls which date-partitioned file records land in. This is critical for the `Record_CreatesDatePartitionedFile` and `GetRecords_FiltersByDateRange` tests.
2. **Concurrent writes test** mirrors the `ConcurrentWrites_NoCorruption` test in `JsonlEscalationAuditStoreTests` -- 20 tasks writing simultaneously, then verify no corruption.
3. **Query tests** exercise each filter dimension independently, then combined (if there's an implicit combo test in the date+type scenario).

---

## Implementation

### JsonlDriftAuditStore

File: `src/Content/Infrastructure/Infrastructure.AI/DriftDetection/JsonlDriftAuditStore.cs`

Namespace: `Infrastructure.AI.DriftDetection`

This class mirrors `JsonlEscalationAuditStore` with these enhancements:
- **Date-partitioned files** instead of a single file
- **`TimeProvider` injection** instead of `DateTimeOffset.UtcNow`
- **Query support** for date range, record type, and event ID filtering

#### Class Structure

```csharp
namespace Infrastructure.AI.DriftDetection;

/// <summary>
/// Append-only JSONL file store for drift detection audit records.
/// Writes date-partitioned files at <c>{AuditPath}/drift-audit/{yyyy-MM-dd}.jsonl</c>.
/// Thread-safe via <see cref="SemaphoreSlim"/> for file access.
/// </summary>
/// <remarks>
/// Follows the pattern of <see cref="Escalation.JsonlEscalationAuditStore"/> with
/// date partitioning for efficient range queries. Uses <see cref="TimeProvider"/>
/// for deterministic timestamps.
/// </remarks>
public sealed class JsonlDriftAuditStore : IDriftAuditStore, IDisposable
{
    // Static JsonSerializerOptions: snake_case, no indent, enum-as-string
    // (identical to JsonlEscalationAuditStore pattern)

    // Fields: _auditDirectory (string), _timeProvider (TimeProvider),
    //         _logger, _semaphore (SemaphoreSlim(1,1))

    // Constructor: inject IOptionsMonitor<AppConfig>, TimeProvider, ILogger<JsonlDriftAuditStore>
    //   _auditDirectory = Path.Combine(config.CurrentValue.AI.DriftDetection.AuditPath, "drift-audit")

    // RecordAsync: serialize record to JSON line, resolve file path from record's RecordedAt date,
    //   acquire semaphore, ensure directory exists, append line, release semaphore

    // GetRecordsAsync: determine date range from query (or scan all files if no range),
    //   for each .jsonl file in range: read lines, deserialize, apply filters (RecordType, EventId),
    //   return ordered by RecordedAt

    // Dispose: dispose _semaphore
}
```

#### Key Methods

**`RecordAsync(DriftAuditRecord record, CancellationToken ct)`**

1. Validate `record` is not null.
2. Serialize the record to a single JSON line using the static `SerializeOptions` (snake_case, enum-as-string, no indent).
3. Compute the file path: `Path.Combine(_auditDirectory, $"{record.RecordedAt:yyyy-MM-dd}.jsonl")`.
4. Acquire `_semaphore`.
5. Ensure the `_auditDirectory` exists (`Directory.CreateDirectory`).
6. Append the line + newline to the file via `File.AppendAllTextAsync`.
7. Release semaphore in `finally`.
8. Log at Debug level: `"Appended drift audit {RecordType} for event {EventId} to {FilePath}"`.
9. Return `Result.Success()`.

**`GetRecordsAsync(DriftAuditQuery query, CancellationToken ct)`**

1. If `_auditDirectory` does not exist, return `Result<IReadOnlyList<DriftAuditRecord>>.Success([])`.
2. Determine which files to scan:
   - If `query.Start` and `query.End` are both set: enumerate dates from `Start.Date` to `End.Date` inclusive, build file paths.
   - If only `query.Start` is set: from `Start.Date` to today (via `_timeProvider.GetUtcNow()`).
   - If only `query.End` is set: all files up to `End.Date`.
   - If neither: all `*.jsonl` files in `_auditDirectory`.
3. For each file that exists, acquire semaphore, read all lines with `FileShare.ReadWrite`, release.
4. Deserialize each non-empty line into `DriftAuditRecord`. On `JsonException`, log warning and skip (same corruption-tolerance as escalation store).
5. Apply filters:
   - If `query.RecordType` is set, filter `record.RecordType == query.RecordType`.
   - If `query.EventId` is set, filter `record.EventId == query.EventId`.
6. Order by `RecordedAt` ascending.
7. Return `Result<IReadOnlyList<DriftAuditRecord>>.Success(records)`.

#### JSON Serialization Options

Two static `JsonSerializerOptions` instances (identical to escalation store pattern):

```csharp
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
```

#### Thread Safety

A single `SemaphoreSlim(1, 1)` guards all file I/O. Both `RecordAsync` and `GetRecordsAsync` acquire the semaphore before file access and release in `finally`. This matches the escalation store's concurrency model.

For reads, use `FileStream` with `FileMode.Open`, `FileAccess.Read`, `FileShare.ReadWrite` to allow concurrent readers while the semaphore serializes writers.

#### Date Partitioning

The file path is derived from the record's `RecordedAt` property, formatted as `yyyy-MM-dd`. This means:
- Records for 2025-06-15 go to `drift-audit/2025-06-15.jsonl`
- Records for 2025-06-16 go to `drift-audit/2025-06-16.jsonl`
- Date-range queries only scan the relevant files

When querying by date range, enumerate calendar dates from `Start.Date` to `End.Date` inclusive and check if each corresponding `.jsonl` file exists. This avoids directory listing and handles sparse days efficiently.

---

## Configuration Dependency

The store reads `AppConfig.AI.DriftDetection.AuditPath` (default: `"data/audit"`) from section 3's `DriftDetectionConfig`. The full directory for drift audit files will be `{AuditPath}/drift-audit/`.

This keeps drift audit files separate from any escalation audit files that might use the same parent directory.

---

## Conventions and Patterns

1. **Mirrors `JsonlEscalationAuditStore`** -- same JSON serialization options, same semaphore pattern, same corruption tolerance on read.
2. **`TimeProvider` injection** -- the escalation store uses `DateTimeOffset.UtcNow` directly; this store uses `TimeProvider` for testability with `FakeTimeProvider`. This is the project convention established in Phase 3.
3. **`Result` return types** -- `RecordAsync` returns `Result` (success/failure), `GetRecordsAsync` returns `Result<IReadOnlyList<DriftAuditRecord>>`.
4. **`IDisposable`** -- disposes the `SemaphoreSlim`, same as escalation store.
5. **Full XML documentation** on all public members.
6. **File-scoped namespace** -- `namespace Infrastructure.AI.DriftDetection;`.

---

## Verification

After implementing, run:

```
dotnet build src/Content/Infrastructure/Infrastructure.AI/Infrastructure.AI.csproj
dotnet test src/Content/Tests/Infrastructure.AI.Tests/Infrastructure.AI.Tests.csproj --filter "FullyQualifiedName~JsonlDriftAuditStore"
```

All 7 test stubs should pass. No existing tests should break since this is a new file with no modifications to existing code.
