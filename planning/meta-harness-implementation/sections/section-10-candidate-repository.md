# Section 10: Candidate Repository

## Overview

This section implements the persistence layer for `HarnessCandidate` objects. Each candidate is stored as a JSON file on disk, and a lightweight index file enables O(1) best-candidate queries without loading every candidate file.

**Dependencies required before starting:**
- Section 04 (trace infrastructure) — establishes the directory layout and atomic write helpers
- Section 09 (candidate domain) — provides `HarnessCandidate`, `HarnessSnapshot`, `HarnessCandidateStatus`, and `MetaHarnessConfig`

**Blocks:**
- Section 11 (proposer), Section 12 (evaluator), and Section 14 (outer loop) all depend on `IHarnessCandidateRepository` being present and registered.

---

## Directory Layout

All candidate data lives under the trace root established by `MetaHarnessConfig.TraceDirectoryRoot`:

```
{trace_root}/optimizations/{optimizationRunId}/candidates/
    index.jsonl                          ← one record per candidate, small, O(n) scan
    {candidateId}/
        candidate.json                   ← full candidate including snapshot
```

`candidate.json` is written atomically: write to a `.tmp` file, set `write_completed: true` as the final field, then rename. Readers that find `write_completed: false` (or the field absent) treat the file as corrupt and skip it.

---

## Tests First

**Test project:** `Tests/Infrastructure.AI.Tests/MetaHarness/FileSystemHarnessCandidateRepositoryTests.cs`

Write these tests before writing implementation. Each test should use a `Path.GetTempPath()` / `Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())` working directory, cleaned up in `IDisposable.Dispose`.

### Round-trip and basic persistence

```csharp
// SaveAsync_CreatesExpectedDirectoryAndCandidateJson
// Arrange: new candidate with known CandidateId and OptimizationRunId
// Act: SaveAsync(candidate)
// Assert: File.Exists("{tempDir}/optimizations/{optRunId}/candidates/{candidateId}/candidate.json")

// SaveAsync_WritesAtomically_CandidateJsonHasWriteCompletedTrue
// Arrange/Act: SaveAsync(candidate)
// Assert: deserialize candidate.json; verify write_completed == true

// GetAsync_ReturnsCandidate_AfterSave
// Arrange: SaveAsync(candidate)
// Act: GetAsync(candidate.CandidateId)
// Assert: returned candidate matches original (all properties equal)

// GetAsync_NonExistentCandidateId_ReturnsNull
// Act: GetAsync(Guid.NewGuid())
// Assert: result is null
```

### Lineage chain

```csharp
// GetLineageAsync_NoParent_ReturnsSingleElement
// Arrange: seed candidate (ParentCandidateId = null), SaveAsync
// Act: GetLineageAsync(seed.CandidateId)
// Assert: list has 1 element, element is the seed

// GetLineageAsync_ThreeGenerations_ReturnsChainOldestFirst
// Arrange: grandparent → parent → child, all SaveAsync'd
// Act: GetLineageAsync(child.CandidateId)
// Assert: [grandparent, parent, child] in that order
```

### Index and best-candidate selection

```csharp
// SaveAsync_UpdatesIndexJsonl_Atomically
// Arrange/Act: SaveAsync(candidate)
// Assert: index.jsonl exists; each line is valid JSON; no partial lines

// ListAsync_ReturnsAllCandidatesForRun
// Arrange: save 3 candidates with same OptimizationRunId
// Act: ListAsync(optimizationRunId)
// Assert: 3 candidates returned

// GetBestAsync_ReadsIndexOnly_NotCandidateFiles
// Arrange: save several candidates with Evaluated status
// Instrument: track which files are opened (use a subclass or callback)
// Act: GetBestAsync(optimizationRunId)
// Assert: only index.jsonl was opened, no candidate.json files

// GetBestAsync_MultipleEvaluatedCandidates_ReturnsHighestPassRate
// Arrange: 3 Evaluated candidates with pass rates 0.5, 0.9, 0.7
// Act: GetBestAsync(optimizationRunId)
// Assert: returned candidate has pass rate 0.9

// GetBestAsync_TieOnPassRate_ReturnsLowerTokenCost
// Arrange: 2 Evaluated candidates, same pass rate, token costs 1000 and 500
// Act: GetBestAsync(optimizationRunId)
// Assert: returned candidate has token cost 500

// GetBestAsync_TieOnBoth_ReturnsEarlierIteration
// Arrange: 2 Evaluated candidates, same pass rate, same token cost, iterations 3 and 1
// Act: GetBestAsync(optimizationRunId)
// Assert: returned candidate has iteration 1
```

---

## Interface

**File:** `src/Content/Application/Application.AI.Common/Interfaces/MetaHarness/IHarnessCandidateRepository.cs`

```csharp
/// <summary>
/// Persistence store for <see cref="HarnessCandidate"/> records.
/// All write operations must be atomic (temp-file + rename).
/// </summary>
public interface IHarnessCandidateRepository
{
    /// <summary>Persists a candidate and updates the run index.</summary>
    Task SaveAsync(HarnessCandidate candidate, CancellationToken ct = default);

    /// <summary>Returns the candidate with the given ID, or null if not found.</summary>
    Task<HarnessCandidate?> GetAsync(Guid candidateId, CancellationToken ct = default);

    /// <summary>
    /// Returns the full ancestor chain ending at <paramref name="candidateId"/>,
    /// ordered oldest-first (seed candidate at index 0).
    /// </summary>
    Task<IReadOnlyList<HarnessCandidate>> GetLineageAsync(Guid candidateId, CancellationToken ct = default);

    /// <summary>
    /// Returns the best evaluated candidate for the given run using tie-breaking:
    /// (1) highest pass rate, (2) lowest token cost, (3) lowest iteration.
    /// Reads only the index file — does not open individual candidate.json files.
    /// </summary>
    Task<HarnessCandidate?> GetBestAsync(Guid optimizationRunId, CancellationToken ct = default);

    /// <summary>Returns all candidates for the given optimization run.</summary>
    Task<IReadOnlyList<HarnessCandidate>> ListAsync(Guid optimizationRunId, CancellationToken ct = default);
}
```

---

## Implementation

**File:** `src/Content/Infrastructure/Infrastructure.AI/MetaHarness/FileSystemHarnessCandidateRepository.cs`

Constructor injects `IOptionsMonitor<MetaHarnessConfig>` and a `SemaphoreSlim` (1,1) per optimization run ID for index-write serialization. Use `ConcurrentDictionary<Guid, SemaphoreSlim>` keyed on `optimizationRunId`.

### `SaveAsync`

1. Compute the candidate directory: `{TraceDirectoryRoot}/optimizations/{optRunId}/candidates/{candidateId}/`
2. `Directory.CreateDirectory(...)` (idempotent)
3. Serialize the full `HarnessCandidate` to JSON (camelCase, indented). Append a `write_completed: true` property at the serialization level (use a wrapper DTO or `JsonExtensionData` pattern so the flag is always last).
4. Write to `candidate.json.tmp`, flush, then `File.Move(..., overwrite: true)` to `candidate.json`.
5. Acquire the per-run semaphore, update `index.jsonl` atomically (read existing lines into memory, append the new index record, write all to `.tmp`, rename), release semaphore.

The **index record** shape (one JSON object per line):

```json
{ "candidateId": "...", "passRate": 0.9, "tokenCost": 1500, "status": "Evaluated", "iteration": 2 }
```

Index records for a candidate that is saved multiple times (e.g., status updated from `Proposed` to `Evaluated`) are appended as new lines. `GetBestAsync` uses the latest record per `candidateId` (last-write-wins on the same ID). Alternatively, overwrite the line in-place on update — choose the simpler approach; last-line-wins during the O(n) scan is simplest.

### `GetAsync`

Read and deserialize `{...}/{candidateId}/candidate.json`. Return null if the file does not exist or if `write_completed` is false.

### `GetLineageAsync`

Start from the requested `candidateId`. Call `GetAsync` to load it. Follow `ParentCandidateId` links, calling `GetAsync` for each ancestor, until `ParentCandidateId == null`. Collect in reverse insertion order to return oldest-first.

### `GetBestAsync`

1. Read `{...}/candidates/index.jsonl` line by line.
2. Parse each line as the index record shape.
3. Keep a `Dictionary<Guid, IndexRecord>` — overwrite on duplicate `candidateId` (last-line-wins).
4. Filter to `status == Evaluated`.
5. Apply tie-breaking: highest `passRate` → lowest `tokenCost` → lowest `iteration`.
6. If a winner is found, call `GetAsync(winner.candidateId)` to return the full candidate. This is the **only** `candidate.json` file opened in this method.

### `ListAsync`

Read `index.jsonl`, collect unique `candidateId` values, call `GetAsync` for each, filter nulls, return as `IReadOnlyList<HarnessCandidate>`.

---

## Atomic Write Pattern

Reuse the same atomic-write helper established in Section 04 (`FileSystemExecutionTraceStore`) if one was extracted. If no shared helper exists, the pattern is:

```csharp
var tmpPath = path + ".tmp";
await File.WriteAllTextAsync(tmpPath, json, ct);
File.Move(tmpPath, path, overwrite: true);
```

Do not implement a shared utility class in this section — that belongs in Section 04. If Section 04 did not extract a helper, inline the two-line pattern here.

---

## Dependency Registration

**File:** `src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs`

Add inside the existing `AddInfrastructureAIDependencies()` extension method:

```csharp
services.AddSingleton<IHarnessCandidateRepository, FileSystemHarnessCandidateRepository>();
```

Register as singleton because the index semaphore dictionary is instance state and must survive across multiple `SaveAsync` calls within one optimization run.

---

## Key Design Decisions

**Why `index.jsonl` instead of loading all `candidate.json` files for `GetBestAsync`?**
In a long optimization run with many candidates, each `candidate.json` may be large (contains full skill file snapshots). The index file contains only the five scalar fields needed for scoring. This keeps `GetBestAsync` at O(n) over a small file rather than O(n * snapshot_size).

**Why last-line-wins in the index instead of in-place update?**
Atomic in-place line replacement in JSONL requires reading the entire file, modifying one line, and rewriting — identical cost to append. Append + last-line-wins is simpler and just as correct for this use case.

**Why per-run semaphores instead of a single global lock?**
Two concurrent optimization runs (unusual but possible in tests) should not block each other. A `ConcurrentDictionary<Guid, SemaphoreSlim>` is the minimal correct solution.

---

## Implementation Notes (Actual vs Plan)

### Deviations from plan

1. **`IDisposable` added to repository** — plan did not mention disposal. Added `Dispose()` to clean up all `SemaphoreSlim` instances accumulated in `_indexLocks`.

2. **`JsonException` swallowed on corrupt lines/files** — plan had no error handling for deserialization. Added silent skip for corrupt index lines in `GetBestAsync`/`ListAsync`, and null return for corrupt candidate files in `TryReadCandidateAsync`.

3. **`GetLineageAsync` uses `Add` + `Reverse()`** — plan implied prepend/insert pattern. Changed to `List.Add` + `List.Reverse()` to avoid O(n²) insert cost.

4. **`GetAsync(Guid)` searches all run directories** — plan omitted this design detail. Since the interface takes only `candidateId` (no `runId`), the implementation walks all run subdirectories under `optimizations/`. An internal `GetWithinRunAsync(id, runId)` overload avoids the search in `GetBestAsync` and `GetLineageAsync` where the run ID is known.

### Files created
| File | Notes |
|---|---|
| `Application.AI.Common/Interfaces/MetaHarness/IHarnessCandidateRepository.cs` | As planned |
| `Infrastructure.AI/MetaHarness/FileSystemHarnessCandidateRepository.cs` | Deviations 1-4 above |
| `Tests/Infrastructure.AI.Tests/MetaHarness/FileSystemHarnessCandidateRepositoryTests.cs` | 14 tests (12 planned + 2 added in review) |

### Test results: 14/14 pass

---

## Checklist for Implementer

- [ ] Create `IHarnessCandidateRepository.cs` in `Application.AI.Common/Interfaces/MetaHarness/`
- [ ] Create `FileSystemHarnessCandidateRepository.cs` in `Infrastructure.AI/MetaHarness/`
- [ ] Create `FileSystemHarnessCandidateRepositoryTests.cs` in `Tests/Infrastructure.AI.Tests/MetaHarness/`
- [ ] Register `IHarnessCandidateRepository` as singleton in `Infrastructure.AI/DependencyInjection.cs`
- [ ] All 12 tests listed above pass
- [ ] `dotnet build src/AgenticHarness.slnx` clean
- [ ] `dotnet test src/AgenticHarness.slnx` green
