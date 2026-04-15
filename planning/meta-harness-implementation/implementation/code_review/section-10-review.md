# Code Review: Section-10 Candidate Repository

## Summary

One Application-layer interface (IHarnessCandidateRepository with 5 methods), one Infrastructure implementation (FileSystemHarnessCandidateRepository, 239 lines), 12 tests, and a DI registration update. The repository uses atomic temp-file-then-rename writes, a per-run JSONL index for O(n) best-candidate selection, and per-run SemaphoreSlim serialization for index writes.

The primary concerns are: (1) SemaphoreSlim instances accumulate indefinitely in the ConcurrentDictionary and are never disposed, (2) no resilience against corrupted JSON in candidate files or index lines, (3) GetAsync scans all optimization run directories without an index, and (4) several test coverage gaps around edge cases.

## Verdict: WARNING -- merge with fixes for M-1 and M-2; remaining items are low-risk

---

## Detailed Findings

### M-1 | MEDIUM | SemaphoreSlim leak in _indexLocks ConcurrentDictionary

**File:** `Infrastructure.AI/MetaHarness/FileSystemHarnessCandidateRepository.cs:79, 112`

**Problem:**
`_indexLocks` is a `ConcurrentDictionary<Guid, SemaphoreSlim>` that grows by one entry per unique `OptimizationRunId` seen during the process lifetime. The class is registered as Singleton and is `sealed` without implementing `IDisposable`. Semaphores are never removed or disposed.

Each SemaphoreSlim is small (~100 bytes), so this is not a memory crisis for typical workloads. However, `SemaphoreSlim` implements `IDisposable` and holds a `ManualResetEvent` internally when contention occurs. Failing to dispose them is a resource leak on principle, and the unbounded growth means a long-running process with many optimization runs will accumulate dead semaphores.

**Recommended fix:**
Implement `IDisposable` on the repository and dispose all semaphores at shutdown:

```csharp
public sealed class FileSystemHarnessCandidateRepository
    : IHarnessCandidateRepository, IDisposable
{
    public void Dispose()
    {
        foreach (var kvp in _indexLocks)
            kvp.Value.Dispose();
        _indexLocks.Clear();
    }
}
```

**Blocker:** No, but should fix. Resource hygiene matters for a Singleton with disposable children.

---

### M-2 | MEDIUM | No resilience against corrupted JSON lines

**Files:** `FileSystemHarnessCandidateRepository.cs:196-198 (GetBestAsync), 226-228 (ListAsync), 268-270 (TryReadCandidateAsync)`

**Problem:**
All JSON deserialization calls use `JsonSerializer.Deserialize` without any exception handling. If a candidate.json file or an index.jsonl line contains malformed JSON (power loss during write, disk corruption, manual edit), the entire method throws `JsonException`, failing the whole operation rather than skipping the corrupt entry.

The atomic write pattern (temp + rename) mitigates this significantly -- a half-written file should not exist at the final path. However, atomic rename is not guaranteed atomic on all filesystems (notably, some networked or FAT32 volumes).

**Recommended fix:**
Wrap index-line deserialization in a try-catch that logs and skips corrupt lines:

```csharp
foreach (var line in lines)
{
    if (string.IsNullOrWhiteSpace(line))
        continue;
    try
    {
        var record = JsonSerializer.Deserialize<IndexRecord>(line, IndexOptions);
        if (record is not null)
            latest[record.CandidateId] = record;
    }
    catch (JsonException)
    {
        // Log warning: corrupt index line skipped
    }
}
```

Similarly, `TryReadCandidateAsync` should catch `JsonException` and return null rather than propagating.

**Blocker:** No. The atomic write pattern makes corruption unlikely, but defense-in-depth is warranted for a persistence layer.

---

### M-3 | MEDIUM | GetAsync scans all run directories -- no global index

**File:** `FileSystemHarnessCandidateRepository.cs:142-159`

**Problem:**
`GetAsync(Guid candidateId)` enumerates every directory under `optimizations/`, probing each for the candidate path. With many optimization runs, this is O(runs) in directory stat calls.

The interface design forces this: `GetAsync` takes only a `candidateId`. This is fine for the current use case (lineage walking within a run uses `GetWithinRunAsync` internally), but the public API invites unbounded scans.

**Recommended fix:** Consider adding an overload with an optional `optimizationRunId` parameter. When provided, skip the directory scan entirely.

**Blocker:** No. Correctness is fine; this is a scalability concern.

---

### L-1 | LOW | GetLineageAsync uses List.Insert(0, ...) -- O(n squared) chain walk

**File:** `FileSystemHarnessCandidateRepository.cs:173`

**Problem:** `chain.Insert(0, current)` shifts all existing elements right on every iteration, making the full lineage walk O(n squared). For typical lineage depths (under 50), this is negligible.

**Recommended fix:** Build the list in natural order, then call `Reverse()` once at the end.

**Blocker:** No.

---

### L-2 | LOW | Missing test: SaveAsync twice on same candidate (index dedup)

**File:** `Infrastructure.AI.Tests/MetaHarness/FileSystemHarnessCandidateRepositoryTests.cs`

**Problem:** No test verifies that saving the same candidate twice (first as Proposed, then as Evaluated via `with` expression) produces correct index behavior. The last-line-wins logic in `GetBestAsync` depends on `Dictionary<Guid, IndexRecord>` overwrite semantics, but this is not tested. This is the most important untested behavior in the index logic.

**Blocker:** No, but should add.

---

### L-3 | LOW | Missing test: GetBestAsync excludes non-Evaluated candidates

**File:** `Infrastructure.AI.Tests/MetaHarness/FileSystemHarnessCandidateRepositoryTests.cs`

**Problem:** The existing tests only save Evaluated candidates when testing GetBestAsync. There is no test confirming that Proposed or Failed candidates are excluded. The `.Where(r => r.Status == HarnessCandidateStatus.Evaluated)` filter at line 201 is untested.

**Blocker:** No, but improves confidence in the selection filter.

---

### N-1 | NITPICK | writeCompleted casing is correct but implicit

**File:** `FileSystemHarnessCandidateRepository.cs:81-86, 284-288`

**Observation:** The `CandidateFileContent.WriteCompleted` property serializes as `writeCompleted` due to `JsonNamingPolicy.CamelCase`. Round-trip is consistent -- no bug. However, if the JSON options ever change, the property name on disk changes silently. Consider adding explicit `[JsonPropertyName]` attributes.

**Blocker:** No.

---

## Answers to Specific Review Questions

1. **Last-line-wins index logic:** Correct. `Dictionary<Guid, IndexRecord>` naturally overwrites earlier entries with later ones during JSONL replay. The append-only JSONL + dictionary-replay pattern is sound.

2. **SemaphoreSlim leak:** Confirmed (M-1). Unbounded growth, never disposed. Low severity given small per-instance cost, but should implement IDisposable.

3. **GetAsync directory search:** No race condition -- file reads target atomic-written paths. TOCTOU between `Directory.Exists` and `EnumerateDirectories` is harmless (returns null). Performance concern only (M-3).

4. **writeCompleted casing:** No issue. CamelCase policy is applied consistently to both serialization and deserialization via the same `JsonOptions` instance.

5. **Test coverage gaps:** Two notable gaps -- save-twice index dedup (L-2) and non-Evaluated exclusion (L-3). The 12 existing tests cover the happy paths well.

6. **Thread safety:** The per-run SemaphoreSlim correctly serializes concurrent index writes within a run. `ConcurrentDictionary.GetOrAdd` is safe for the factory. `TryReadCandidateAsync` performs file I/O without locking, which is fine since reads target atomically-written files. No thread-safety bugs found.

---

## Fix Priority

| ID   | Severity | Effort | Recommendation |
|------|----------|--------|----------------|
| M-1  | MEDIUM   | Low    | Fix before merge -- implement IDisposable, dispose semaphores |
| M-2  | MEDIUM   | Low    | Fix before merge -- catch JsonException in deserialization paths |
| M-3  | MEDIUM   | Low    | Consider -- add optional runId param or document limitation |
| L-1  | LOW      | Low    | Optional -- replace Insert(0,...) with Add + Reverse |
| L-2  | LOW      | Low    | Add test -- save-twice index dedup is critical untested path |
| L-3  | LOW      | Low    | Add test -- Proposed/Failed exclusion from GetBestAsync |
| N-1  | NITPICK  | Low    | Optional -- explicit JsonPropertyName attributes |

No CRITICAL or HIGH issues. The core persistence logic (atomic writes, last-line-wins index, tie-breaking selection) is correct and well-tested. M-1 and M-2 are the most important fixes -- resource hygiene and resilience for a persistence layer.
