# Section 06: JSONL Delegation Store

## Overview

This section implements `JsonlDelegationStore`, the persistence layer for delegation records. It follows the same append-only JSONL pattern established by `JsonlAgentHistoryStore` (in `Infrastructure.AI/Memory/`) but manages **multi-session files** with **per-delegation deduplication** and a **bounded LRU semaphore cache** instead of the single-file-per-run model used by the history store.

The store writes one JSONL file per supervisor session. State transitions (Pending -> Completed, Pending -> Failed, etc.) produce additional lines for the same `DelegationId`. Read operations deduplicate by returning only the latest record per delegation.

## Dependencies on Other Sections

- **Section 02 (Domain Delegation)**: Provides `DelegationRecord`, `DelegationState`, and all orchestration domain types. These must exist before the store can serialize/deserialize.
- **Section 03 (Interfaces)**: Provides `IDelegationStore` interface that this section implements.
- **Section 08 (DI & Config)**: Registers `JsonlDelegationStore` as the `IDelegationStore` implementation and adds `DelegationStoragePath` to `SubagentConfig`. This section defines the implementation; Section 08 wires it.

## Tests

All tests go in `src/Content/Tests/Infrastructure.AI.Tests/Agents/JsonlDelegationStoreTests.cs`. The test class uses a temp directory per test run and cleans up via `IDisposable`, matching the pattern in `JsonlAgentHistoryStoreTests`.

### Test Stubs

```csharp
// File: src/Content/Tests/Infrastructure.AI.Tests/Agents/JsonlDelegationStoreTests.cs

/// <summary>
/// Tests for <see cref="JsonlDelegationStore"/> — append-only JSONL persistence
/// for delegation records with per-delegation deduplication on reads.
/// </summary>
public sealed class JsonlDelegationStoreTests : IDisposable
{
    // Setup: create a temp directory, instantiate store with that base path.
    // Teardown: delete the temp directory.

    // Helper: BuildRecord(Guid? delegationId = null, string supervisorId = "supervisor-1",
    //   DelegationState state = DelegationState.Pending, Guid? parentDelegationId = null)
    //   Returns a DelegationRecord with sensible defaults.

    // === Round-trip ===

    // Test: AppendAsync_ThenGetByIdAsync_ReturnsRecord
    //   Append a single Pending record, retrieve by DelegationId.
    //   Assert: returned record matches appended record's DelegationId, SupervisorId, State.

    // Test: AppendAsync_MultipleTimes_GetByIdAsync_ReturnsLatestState
    //   Append Pending record for delegation X, then append Completed record for same X.
    //   Assert: GetByIdAsync returns the Completed record (not Pending).

    // === Queries ===

    // Test: GetBySessionAsync_ReturnsAllRecordsDeduplicatedById
    //   Append 3 delegations (2 state changes each = 6 lines total).
    //   Assert: Returns exactly 3 records, each in its latest state.

    // Test: GetByParentAsync_ReturnsOnlyChildDelegations
    //   Append a parent delegation (ParentDelegationId=null) and 2 child delegations
    //   (ParentDelegationId=parent's DelegationId).
    //   Assert: GetByParentAsync(parentId) returns exactly 2 children.

    // === Filesystem ===

    // Test: AppendAsync_CreatesDirectoryStructureLazily
    //   Use a store pointing at a path that does not yet exist.
    //   After AppendAsync, assert the directory and JSONL file were created.

    // Test: AppendAsync_FilePathContainsSupervisorIdAndTimestamp
    //   Append a record with supervisorId="supervisor-abc".
    //   Assert: the file is created under {basePath}/supervisor-abc/ and the filename
    //   contains a timestamp-like segment (YYYYMMDD or similar).

    // === Concurrency ===

    // Test: AppendAsync_ConcurrentWrites_NoCorruption
    //   Launch 10 parallel AppendAsync calls for the same supervisor session.
    //   Assert: all records are readable via GetBySessionAsync, no corrupted lines.

    // === Error handling ===

    // Test: GetByIdAsync_PartialJsonLine_SkipsWithoutCrash
    //   Write a partial/corrupted JSON line directly to the JSONL file.
    //   Then append a valid record via the store.
    //   Assert: GetByIdAsync returns the valid record, the corrupted line is silently skipped.

    // Test: GetByIdAsync_NonexistentDelegation_ReturnsNull
    //   Query for a DelegationId that was never appended.
    //   Assert: returns null, no exception.

    // Test: GetBySessionAsync_NoFile_ReturnsEmpty
    //   Query for a supervisor session that has no JSONL file.
    //   Assert: returns empty list, no exception.
}
```

### Key Testing Patterns

- Use `Path.Combine(Path.GetTempPath(), $"delegation-store-tests-{Guid.NewGuid():N}")` for temp directories.
- Use `FluentAssertions` for all assertions.
- The `BuildRecord` helper should use `required` properties from `DelegationRecord` (Section 02), defaulting `DelegationId` to `Guid.NewGuid()`, `StartedAt` to `DateTimeOffset.UtcNow`, and other fields to reasonable test values.
- For the corruption test, write a partial line directly via `File.AppendAllTextAsync` (same technique used in `JsonlAgentHistoryStoreTests.QueryAsync_WithCorruptedLine_SkipsCorruptedAndReturnsValid`).

## Implementation

### File to Create

`src/Content/Infrastructure/Infrastructure.AI/Agents/JsonlDelegationStore.cs`

### File Layout on Disk

```
{DelegationStoragePath}/
  {supervisorId}/
    {sessionTimestamp}.jsonl
```

- `DelegationStoragePath` comes from `SubagentConfig.DelegationStoragePath` (default: `.agent-sessions/delegations`). This config property is added in Section 08.
- Each supervisor gets its own subdirectory.
- Each session gets a single JSONL file named with a timestamp (e.g., `20260508T143022Z.jsonl`).
- Each line is a JSON-serialized `DelegationRecord`. State transitions append new lines rather than mutating existing ones.

### Class Structure

```csharp
// File: src/Content/Infrastructure/Infrastructure.AI/Agents/JsonlDelegationStore.cs

/// <summary>
/// Append-only JSONL persistence for <see cref="DelegationRecord"/>.
/// One file per supervisor session. State transitions produce additional lines
/// for the same <see cref="DelegationRecord.DelegationId"/>; read operations
/// deduplicate by returning the latest record per delegation.
/// </summary>
/// <remarks>
/// <para>
/// Thread safety: each file path gets its own <see cref="SemaphoreSlim(1,1)"/>,
/// stored in a bounded LRU cache (max 100 entries). Both reads and writes acquire
/// the semaphore — on Windows, <c>File.AppendAllTextAsync</c> is not atomic and a
/// concurrent read can observe partially-written lines.
/// </para>
/// <para>
/// Cross-session lookup is not supported in Phase 1. <see cref="GetByIdAsync"/> and
/// <see cref="GetBySessionAsync"/> read only the current session file.
/// </para>
/// </remarks>
public sealed class JsonlDelegationStore : IDelegationStore, IDisposable
{
    // Constructor: accepts IOptionsMonitor<AppConfig> (for DelegationStoragePath)
    //   and ILogger<JsonlDelegationStore>.
    //   Stores the base path and creates the semaphore cache.

    // --- Public API (IDelegationStore) ---

    // AppendAsync: serialize DelegationRecord as JSON, append line to session file.
    // GetByIdAsync: read all lines from current session file, filter by DelegationId,
    //   return the latest (last appended) record for that ID.
    // GetBySessionAsync: read all lines, deduplicate by DelegationId (keep last per ID).
    // GetByParentAsync: read all lines, filter by ParentDelegationId, deduplicate.

    // --- Internal mechanics ---

    // GetOrCreateSessionFile: resolves the JSONL file path for a supervisor.
    //   Creates directory lazily. Uses a session timestamp established on first call.
    // GetFileLock: returns the SemaphoreSlim for a given file path from the LRU cache.
    // ReadAllRecords: reads and deserializes all lines from a file, skipping corrupted lines.
    // DeduplicateByDelegationId: given a list of records, returns only the latest per DelegationId.
}
```

### Serialization

Use `System.Text.Json` with `JsonNamingPolicy.SnakeCaseLower` and `WriteIndented = false`, matching the existing `JsonlAgentHistoryStore` convention. Define static `JsonSerializerOptions` fields to avoid per-call allocation.

```csharp
private static readonly JsonSerializerOptions SerializeOptions = new()
{
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    WriteIndented = false
};

private static readonly JsonSerializerOptions DeserializeOptions = new()
{
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
};
```

### Session File Resolution

The store needs to map `supervisorId` to a file path. On first append for a given supervisor, it generates a session timestamp and caches the mapping. The session file stays the same for the lifetime of the store instance.

```csharp
// _sessionFiles: ConcurrentDictionary<string, string> mapping supervisorId → file path
// On first access for a supervisorId:
//   1. Create directory: {basePath}/{supervisorId}/
//   2. Generate filename: {DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}.jsonl
//   3. Cache in _sessionFiles
//   4. Return the full path
```

### Concurrency: Bounded LRU Semaphore Cache

Each file path needs its own `SemaphoreSlim(1, 1)` to serialize reads and writes. These semaphores are stored in a bounded cache to prevent unbounded memory growth across many supervisor sessions.

The cache has a maximum of 100 entries. When at capacity and a new file path is requested, evict the least-recently-used entry and dispose its semaphore.

Implementation approach:
- Use a `ConcurrentDictionary<string, (SemaphoreSlim Semaphore, long LastUsed)>` for the cache.
- `LastUsed` is a tick count from `Stopwatch.GetTimestamp()` or `Environment.TickCount64`.
- On `GetFileLock(path)`: if the path exists in the cache, update `LastUsed` and return the semaphore. If not, and cache is at capacity, find the entry with the smallest `LastUsed`, remove it, dispose the semaphore, then add the new entry.
- The eviction path is rare (only triggers after 100 distinct file paths) so a simple linear scan is fine.

### AppendAsync Implementation Notes

1. Resolve the session file path for the record's `SupervisorId`.
2. Serialize the `DelegationRecord` to JSON and append `\n`.
3. Acquire the file's semaphore.
4. Call `File.AppendAllTextAsync(path, line, ct)`.
5. Release the semaphore in a `finally` block.
6. Create the directory lazily if it does not exist (on first append).

### ReadAllRecords Implementation Notes

1. Check `File.Exists(path)` — if not, return empty list.
2. Acquire the file's semaphore (reads must also lock on Windows).
3. Open with `FileMode.Open`, `FileAccess.Read`, `FileShare.ReadWrite`.
4. Read line by line via `StreamReader`.
5. Skip blank lines.
6. Wrap `JsonSerializer.Deserialize<DelegationRecord>` in a try/catch for `JsonException` — skip corrupted lines (log a warning but do not throw).
7. Release the semaphore in a `finally` block.
8. Return the list.

### Deduplication

`GetByIdAsync`, `GetBySessionAsync`, and `GetByParentAsync` all need to deduplicate by `DelegationId`. Because the file is append-only and state transitions append new lines, the **last line** for a given `DelegationId` represents the latest state.

Implementation: after reading all records, group by `DelegationId` and take the last record per group. A simple approach:

```
records.GroupBy(r => r.DelegationId).Select(g => g.Last()).ToList()
```

For `GetByIdAsync`, filter to the target ID first, then take `.Last()` (or return `null` if empty).

For `GetByParentAsync`, filter by `ParentDelegationId` first, then deduplicate.

### IDisposable

Dispose all semaphores in the LRU cache. The store is registered as `Singleton` (Section 08), so disposal happens at app shutdown.

```csharp
public void Dispose()
{
    foreach (var entry in _fileLocks.Values)
        entry.Semaphore.Dispose();
    _fileLocks.Clear();
}
```

### Cross-Session Limitation (Phase 1)

`GetByIdAsync` searches only the current session file for the given `supervisorId`. If no session file exists yet (no appends), it returns `null`. Cross-session historical lookup is not supported — document this as a known limitation with a code comment and XML doc remark. Phase 2 can extend this by scanning all session files for a supervisor.

### Error Handling

- **Corrupted/partial JSON lines**: Catch `JsonException`, log a warning with the line content (truncated), and skip. This matches the established pattern in `JsonlAgentHistoryStore.QueryAsync`.
- **Missing directories/files**: Return empty results, never throw. Create lazily on write.
- **IO exceptions on write**: Let them propagate — the caller (supervisor) handles write failures as delegation failures.

### Logging

Use `ILogger<JsonlDelegationStore>` with structured logging:
- `LogDebug` for successful appends (delegation_id, state, file_path).
- `LogWarning` for skipped corrupted lines (file_path, line_number).
- `LogInformation` for session file creation (supervisor_id, file_path).

### Configuration

The store reads `SubagentConfig.DelegationStoragePath` from `IOptionsMonitor<AppConfig>`. This config property is added in Section 08. Default value: `.agent-sessions/delegations`. The store resolves relative paths from the current working directory (same as `MailboxStoragePath` in the existing `SubagentConfig`).

## Files Summary

| File | Action | Purpose |
|------|--------|---------|
| `src/Content/Infrastructure/Infrastructure.AI/Agents/JsonlDelegationStore.cs` | Create | `IDelegationStore` implementation |
| `src/Content/Tests/Infrastructure.AI.Tests/Agents/JsonlDelegationStoreTests.cs` | Create | Unit tests |

## Implementation Checklist

1. Write test stubs in `JsonlDelegationStoreTests.cs` (all tests from the Tests section above).
2. Implement `JsonlDelegationStore` class with:
   - Static `JsonSerializerOptions` for serialization/deserialization.
   - Constructor accepting `IOptionsMonitor<AppConfig>` and `ILogger<JsonlDelegationStore>`.
   - Bounded LRU semaphore cache (max 100 entries).
   - Session file resolution with lazy directory creation.
   - `AppendAsync` with semaphore-guarded file append.
   - `ReadAllRecords` helper with corrupted-line handling.
   - `DeduplicateByDelegationId` helper.
   - `GetByIdAsync`, `GetBySessionAsync`, `GetByParentAsync` using the helpers.
   - `IDisposable` to clean up semaphores.
3. Make all tests pass.
4. Verify the implementation follows the existing `JsonlAgentHistoryStore` patterns for serialization, file access, and error handling.
