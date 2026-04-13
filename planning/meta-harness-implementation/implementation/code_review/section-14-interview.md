# Code Review Interview: Section 14

## Auto-fixed

### CRITICAL 1: Path traversal in WriteSnapshotFiles / WriteProposedSnapshot
**Fix:** Added `SafeResolvePath(baseDir, relativePath)` helper that calls `Path.GetFullPath(Path.Combine(...))` then asserts the result starts with `baseDir + DirectorySeparatorChar`. Applied to both `WriteSnapshotFiles` and `WriteProposedSnapshot`. LLM-generated skill paths can no longer escape the snapshot directory.

### CRITICAL 2: Bare catch in LoadEvalTasksAsync swallows all exceptions
**Fix:** Narrowed catch to `catch (Exception ex) when (ex is JsonException or IOException)`. `UnauthorizedAccessException`, `OutOfMemoryException`, etc. now propagate rather than silently collapsing to an empty task list.

### HIGH 2: EnforceRetentionPolicy deletes non-GUID-named directories
**Fix:** Added `Guid.TryParse(Path.GetFileName(d), out _)` filter to the `Where` clause. Only GUID-named optimization run directories are candidates for deletion.

### HIGH 3+4: LoadOrCreateRunManifest sync I/O + bare catch
**Fix:** Made method `async Task<RunManifest>`, switched `File.ReadAllText` → `File.ReadAllTextAsync`. Narrowed catch to `catch (Exception ex) when (ex is JsonException or IOException)`.

### MEDIUM 1: IterationCount reported planned count, not actual executed
**Fix:** Added `executedIterations` counter incremented at the top of each loop iteration (before failure paths), replacing `Math.Max(0, maxIterations - startIteration + 1)` in the result.

### MEDIUM 3: Seed candidate missing — silent fallback to new seed
**Fix:** When `SeedCandidateId` is provided but the candidate is not found, now throws `InvalidOperationException` rather than silently building a new seed. Explicit resume intent should not fall back silently.

### MEDIUM 4: RunManifest write_completed → writeCompleted
**Fix:** Changed `[JsonPropertyName("write_completed")]` to `[JsonPropertyName("writeCompleted")]` for consistent camelCase JSON property naming. Updated two test assertions to match.

### MEDIUM 5: ProposedChangesPath returned non-empty string when no candidates succeeded
**Fix:** `ProposedChangesPath = bestCandidate is not null ? proposedDir : string.Empty` — returns empty string when no best candidate exists.

## Let go

### HIGH 1: Handler is 530+ lines — exceeds 400-line convention
Handler is complex by nature — it orchestrates propose, evaluate, score, manifest persistence, snapshot writing, retention, and summary generation. Extracting `RunManifestPersistence`, `SnapshotWriter`, `RetentionPolicyEnforcer` would add 3 new files for a single consumer. For this POC, the single-class approach is more readable and YAGNI-appropriate. Not extracted.

### LOW 2: UpdateRunManifest uses sync File.WriteAllText + File.Move
Atomic temp+rename pattern is correct. The sync I/O here is a deliberate tradeoff: manifest writes are short (< 1KB) and calling async from a void method would require restructuring. Acceptable for a POC.

### LOW 4: Task.Delay(10) in retention test — fragile for CI
Known issue — file creation time ordering is OS-dependent and may be unreliable on fast CI machines. Acceptable for a POC. Production fix would use an injected `TimeProvider` or mock `DirectoryInfo.CreationTimeUtc`.

## Final test count: 13 tests, all passing
