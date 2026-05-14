# Section 09: Escalation Audit Store

## Overview

This section implements `JsonlEscalationAuditStore`, the append-only JSONL file store that implements the `IEscalationAuditStore` interface (defined in section-06). It follows the same architecture as the existing `JsonlDelegationStore` from Phase 1: per-file semaphore locking, snake_case JSON serialization, `JsonStringEnumConverter` for enum values, and `FileShare.ReadWrite` for concurrent read access.

The audit store writes to a single `escalations.jsonl` file in a configurable data directory. Each line is a serialized `EscalationAuditRecord` with a `RecordType` discriminator (`Request`, `Decision`, `Outcome`) that determines how the `Payload` field should be interpreted on read.

**Layer:** `Infrastructure.AI`
**Namespace:** `Infrastructure.AI.Escalation`
**File:** `src/Content/Infrastructure/Infrastructure.AI/Escalation/JsonlEscalationAuditStore.cs`

---

## Dependencies

| Dependency | Section | What It Provides |
|------------|---------|-----------------|
| Domain escalation models | section-01 | `EscalationRequest`, `EscalationOutcome`, `ApproverDecision`, `EscalationAuditRecord`, `EscalationAuditRecordType` |
| Escalation interfaces | section-06 | `IEscalationAuditStore` interface contract |
| Configuration | section-04 | `EscalationConfig` for storage path (see Design Decision below) |

**Blocks:** section-19 (DI registration -- this implementation must be registered as `IEscalationAuditStore`).

**Parallel with:** section-08 (escalation service), section-10 (notification adapters).

---

## Design Decision: Storage Path Configuration

The plan specifies "writes to `escalations.jsonl` in a configurable data directory" but section-04 does not define a storage path property on `EscalationConfig`. Following the `JsonlDelegationStore` pattern (which reads from `AppConfig.AI.Orchestration.Subagent.DelegationStoragePath`), the implementation should add an `AuditStoragePath` property to `EscalationConfig` with a default of `".agent-sessions/escalations"`. This keeps the storage layout consistent:

```
.agent-sessions/
  delegations/        -- JsonlDelegationStore
  mailbox/            -- mailbox storage
  escalations/        -- JsonlEscalationAuditStore
    escalations.jsonl
```

If section-04 has already been implemented without this property, the implementer should add it to `EscalationConfig` as a non-breaking addition:

```csharp
/// <summary>Directory path for the JSONL escalation audit store.</summary>
public string AuditStoragePath { get; set; } = ".agent-sessions/escalations";
```

---

## Tests First

**File:** `src/Content/Tests/Infrastructure.AI.Tests/Escalation/JsonlEscalationAuditStoreTests.cs`

The test class follows the exact pattern established by `JsonlDelegationStoreTests`: temp directory per test run, `IDisposable` for cleanup, config via `Mock.Of<IOptionsMonitor<T>>()`, logger via `Mock.Of<ILogger<T>>()`.

```csharp
namespace Infrastructure.AI.Tests.Escalation;

/// <summary>
/// Tests for <see cref="JsonlEscalationAuditStore"/>.
/// Validates append-only JSONL semantics, round-trip serialization with RecordType
/// discriminator, concurrent write safety, and history retrieval by escalation ID.
/// Follows the same test structure as <see cref="JsonlDelegationStoreTests"/>.
/// </summary>
public sealed class JsonlEscalationAuditStoreTests : IDisposable
{
    // Setup:
    //   _tempDir = Path.Combine(Path.GetTempPath(), $"audit-store-tests-{Guid.NewGuid():N}")
    //   Build EscalationConfig with AuditStoragePath = _tempDir
    //   Wrap in AppConfig -> IOptionsMonitor<AppConfig> mock
    //   Create JsonlEscalationAuditStore(_options, _logger)
    //
    // Disposal:
    //   store.Dispose(), Directory.Delete(_tempDir, recursive: true)
    //
    // Helper: BuildRequest(Guid? escalationId = null) -> EscalationRequest with required fields
    // Helper: BuildDecision(string approver = "admin") -> ApproverDecision
    // Helper: BuildOutcome(Guid escalationId) -> EscalationOutcome

    // Test: RecordRequestAsync_AppendsToFile
    //   Arrange: Build an EscalationRequest
    //   Act: RecordRequestAsync(request, ct)
    //   Assert: The JSONL file exists, contains exactly 1 line,
    //           line deserializes to EscalationAuditRecord with RecordType == Request

    // Test: RecordDecisionAsync_AppendsToFile
    //   Arrange: Build an ApproverDecision with a known escalation ID
    //   Act: RecordDecisionAsync(escalationId, decision, ct)
    //   Assert: File contains 1 line with RecordType == Decision,
    //           EscalationId matches

    // Test: RecordOutcomeAsync_AppendsToFile
    //   Arrange: Build an EscalationOutcome
    //   Act: RecordOutcomeAsync(outcome, ct)
    //   Assert: File contains 1 line with RecordType == Outcome

    // Test: GetHistoryAsync_ReturnsAllRecordsForEscalation
    //   Arrange: Record a Request, then a Decision, then an Outcome for the same escalation ID.
    //            Also record a Request for a DIFFERENT escalation ID (noise).
    //   Act: GetHistoryAsync(escalationId, ct)
    //   Assert: Returns exactly 3 records (Request, Decision, Outcome) all with matching ID.
    //           Records are ordered chronologically (Request timestamp < Decision < Outcome).

    // Test: GetHistoryAsync_UnknownId_ReturnsEmpty
    //   Arrange: Record a request for one escalation ID
    //   Act: GetHistoryAsync(Guid.NewGuid(), ct) -- different ID
    //   Assert: Returns empty list

    // Test: ConcurrentWrites_NoCorruption
    //   Arrange: Generate 20 distinct EscalationRequests
    //   Act: Task.WhenAll -- RecordRequestAsync for all 20 concurrently
    //   Assert: Read all lines from file. Exactly 20 lines.
    //           Each line deserializes successfully (no partial/interleaved JSON).
    //           All 20 escalation IDs are present.

    // Test: RecordType_Discriminator_DeserializesCorrectly
    //   Arrange: Record one Request, one Decision, one Outcome for the same ID
    //   Act: GetHistoryAsync(escalationId, ct)
    //   Assert: records[0].RecordType == Request,
    //           records[1].RecordType == Decision,
    //           records[2].RecordType == Outcome.
    //           Payload of each record round-trips: deserialize Request Payload back to
    //           EscalationRequest, Decision Payload back to ApproverDecision, etc.
}
```

---

## Implementation Details

### File: `src/Content/Infrastructure/Infrastructure.AI/Escalation/JsonlEscalationAuditStore.cs`

The implementation mirrors `JsonlDelegationStore` with these differences:
- **Single file** rather than per-supervisor subdirectories -- escalations are a global audit log, not partitioned by agent.
- **Three record methods** (RecordRequest, RecordDecision, RecordOutcome) that each create an `EscalationAuditRecord` with the appropriate `RecordType` discriminator and serialize the domain object as the `Payload` string.
- **One read method** (GetHistory) that filters by `EscalationId`.

**Class structure:**

```csharp
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
```

**Key internals:**

1. **JSON options** -- Static readonly `JsonSerializerOptions` with `JsonNamingPolicy.SnakeCaseLower`, `WriteIndented = false`, and `JsonStringEnumConverter`. Same as `JsonlDelegationStore`.

2. **File path** -- Computed once from `EscalationConfig.AuditStoragePath` + `"escalations.jsonl"`. Store this in a `readonly string _filePath` field.

3. **Semaphore** -- Single `SemaphoreSlim(1, 1)` since there is only one file. Simpler than `JsonlDelegationStore`'s per-file lock cache because the audit store writes to a single file.

4. **Constructor injection:**
   - `IOptionsMonitor<AppConfig>` -- to read `config.CurrentValue.AI.Governance.Escalation.AuditStoragePath`
   - `ILogger<JsonlEscalationAuditStore>` -- for operational diagnostics

5. **RecordRequestAsync** -- Creates an `EscalationAuditRecord` with:
   - `RecordType = EscalationAuditRecordType.Request`
   - `EscalationId = request.EscalationId`
   - `Timestamp = DateTimeOffset.UtcNow`
   - `Payload = JsonSerializer.Serialize(request, serializeOptions)`
   Then calls the shared `AppendRecordAsync(record, ct)` method.

6. **RecordDecisionAsync** -- Same pattern with `RecordType = Decision`, serializes the `ApproverDecision` as payload, uses the passed `escalationId`.

7. **RecordOutcomeAsync** -- Same pattern with `RecordType = Outcome`, serializes the `EscalationOutcome` as payload, reads `EscalationId` from `outcome.EscalationId`.

8. **AppendRecordAsync (private)** -- The shared write method:
   - Serialize the `EscalationAuditRecord` to a single JSON line (no newlines within the JSON) + `"\n"` terminator
   - Acquire semaphore
   - Ensure directory exists (lazy creation on first write, matching `JsonlDelegationStore.EnsureDirectoryExists`)
   - `File.AppendAllTextAsync(path, line, ct)`
   - Release semaphore
   - Log at Debug level

9. **GetHistoryAsync** -- Read method:
   - If file does not exist, return empty list
   - Acquire semaphore
   - Open file with `FileMode.Open, FileAccess.Read, FileShare.ReadWrite`
   - Read line by line with `StreamReader.ReadLineAsync`
   - Skip blank lines
   - Deserialize each line to `EscalationAuditRecord` (with try/catch for `JsonException` -- log and skip corrupted lines)
   - Filter by `EscalationId == escalationId`
   - Return as `IReadOnlyList<EscalationAuditRecord>` ordered by `Timestamp`
   - Release semaphore

10. **Dispose** -- Dispose the semaphore.

### Serialization Contract

Each line in the JSONL file is an `EscalationAuditRecord` serialized with snake_case naming:

```json
{"record_type":"Request","escalation_id":"...","timestamp":"...","payload":"{...escaped JSON...}"}
{"record_type":"Decision","escalation_id":"...","timestamp":"...","payload":"{...escaped JSON...}"}
{"record_type":"Outcome","escalation_id":"...","timestamp":"...","payload":"{...escaped JSON...}"}
```

The `Payload` field is a string containing the JSON-serialized domain object (double-serialized). This is intentional -- it keeps the outer record shape uniform regardless of record type, and avoids polymorphic deserialization complexity. Consumers who need the typed payload can deserialize `Payload` based on `RecordType`:
- `RecordType.Request` -> `JsonSerializer.Deserialize<EscalationRequest>(record.Payload)`
- `RecordType.Decision` -> `JsonSerializer.Deserialize<ApproverDecision>(record.Payload)`
- `RecordType.Outcome` -> `JsonSerializer.Deserialize<EscalationOutcome>(record.Payload)`

This matches the plan's description: "Each record has a `RecordType` discriminator (Request, Decision, Outcome) for deserialization."

---

## Existing Patterns to Follow

The implementation must be consistent with `JsonlDelegationStore` at `src/Content/Infrastructure/Infrastructure.AI/Agents/JsonlDelegationStore.cs`. Key patterns to replicate:

1. **Static JsonSerializerOptions** -- Both serialize and deserialize options as `private static readonly` fields. Serialize options: `SnakeCaseLower`, `WriteIndented = false`, `JsonStringEnumConverter`. Deserialize options: same plus `PropertyNameCaseInsensitive = true`.

2. **SemaphoreSlim for thread safety** -- Acquired before every file read/write, released in `finally` block.

3. **Lazy directory creation** -- `EnsureDirectoryExists` called before every write, never in the constructor.

4. **FileStream for reads** -- `new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)` so reads don't block concurrent writes.

5. **Corrupted line handling** -- `try/catch (JsonException)` around each line deserialization, log warning and skip.

6. **Debug-level logging** -- Log each successful append at `LogDebug` with structured properties (`EscalationId`, `RecordType`, `FilePath`).

7. **IDisposable** -- Dispose semaphore(s) in `Dispose()`.

---

## DI Registration (for section-19 reference)

The audit store should be registered as a singleton in `Infrastructure.AI/DependencyInjection.cs`, following the delegation store pattern:

```csharp
services.AddSingleton<IEscalationAuditStore, JsonlEscalationAuditStore>();
```

This registration belongs to section-19, but is documented here so the implementer knows the expected lifetime. Singleton is correct because the store holds a semaphore for file locking that must be shared across all callers.

---

## Implementation Checklist

1. If not already present on `EscalationConfig`, add `AuditStoragePath` property (default `".agent-sessions/escalations"`) -- coordinate with section-04
2. Create `src/Content/Infrastructure/Infrastructure.AI/Escalation/JsonlEscalationAuditStore.cs`
3. Create `src/Content/Tests/Infrastructure.AI.Tests/Escalation/JsonlEscalationAuditStoreTests.cs`
4. Run tests: `dotnet test src/Content/Tests/Infrastructure.AI.Tests/Infrastructure.AI.Tests.csproj --filter "FullyQualifiedName~JsonlEscalationAuditStoreTests"`
5. Run full build: `dotnet build src/AgenticHarness.slnx`
6. Verify all 7 tests pass

---

## File Checklist

| File | Action | Project |
|------|--------|---------|
| `src/Content/Infrastructure/Infrastructure.AI/Escalation/JsonlEscalationAuditStore.cs` | Create | Infrastructure.AI |
| `src/Content/Tests/Infrastructure.AI.Tests/Escalation/JsonlEscalationAuditStoreTests.cs` | Create | Infrastructure.AI.Tests |
| `src/Content/Domain/Domain.Common/Config/AI/Governance/EscalationConfig.cs` | Modify (add AuditStoragePath) | Domain.Common |

---

## Verification

```
dotnet build src/Content/Infrastructure/Infrastructure.AI/Infrastructure.AI.csproj
dotnet test src/Content/Tests/Infrastructure.AI.Tests/Infrastructure.AI.Tests.csproj --filter "FullyQualifiedName~JsonlEscalationAuditStoreTests"
```

All tests should pass. No new NuGet dependencies are needed -- the implementation uses `System.Text.Json` (included with .NET 10) and `Microsoft.Extensions.Options` / `Microsoft.Extensions.Logging` (already referenced by `Infrastructure.AI`).

---

## Implementation Notes

**Status:** Complete
**Commit:** (see git log)

### Deviations from Plan
- Added `AuditStoragePath` `NotEmpty()` validation rule to `EscalationConfigValidator` (per code review — prevents misconfigured empty path).
- Added corresponding test `Validate_EmptyAuditStoragePath_HasError` to `EscalationConfigValidatorTests`.
- These two files were not in the original plan but are necessary for config safety.

### Additional Files Modified
| File | Change |
|------|--------|
| `src/Content/Application/Application.Core/Validation/EscalationConfigValidator.cs` | Added `NotEmpty()` rule for `AuditStoragePath` |
| `src/Content/Tests/Application.Core.Tests/Validation/EscalationConfigValidatorTests.cs` | Added empty path validation test |

### Test Results
- 7 audit store tests (JSONL append, retrieval, concurrent writes, discriminator round-trip)
- 8 escalation config validator tests (7 existing + 1 new AuditStoragePath test)
- All 15 tests pass
