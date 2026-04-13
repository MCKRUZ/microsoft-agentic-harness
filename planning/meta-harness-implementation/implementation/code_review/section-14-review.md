# Section 14 Code Review: RunHarnessOptimizationCommand + Handler

**Reviewer**: claude-code-reviewer
**Date**: 2026-04-13

**Files reviewed**:
- RunHarnessOptimizationCommand.cs (58 lines)
- RunHarnessOptimizationCommandHandler.cs (506 lines)
- RunHarnessOptimizationCommandValidator.cs (25 lines)
- RunHarnessOptimizationCommandHandlerTests.cs (664 lines)

---

## Critical Issues

### [CRITICAL] Path traversal in WriteSnapshotFiles and WriteProposedSnapshot via untrusted relativePath

**File**: RunHarnessOptimizationCommandHandler.cs:340-347 (WriteSnapshotFiles), :356-365 (WriteProposedSnapshot)

**Issue**: SkillFileSnapshots dictionary keys are used as relative file paths via Path.Combine(snapshotDir, relativePath). These keys originate from HarnessProposal.ProposedSkillChanges, which is LLM-generated output from the proposer agent. A malicious or hallucinated proposal containing a key like ../../etc/shadow would write files outside the intended snapshot directory. No path confinement validation exists in WriteSnapshotFiles, WriteProposedSnapshot, or BuildSnapshotFromProposal.

**Fix**: Add a SafeResolvePath method that calls Path.GetFullPath on both baseDir and the combined path, then asserts the target StartsWith the base + DirectorySeparatorChar. Apply in all three methods.

---

### [CRITICAL] LoadEvalTasksAsync silently swallows all exceptions including security-relevant ones

**File**: RunHarnessOptimizationCommandHandler.cs:231-242

**Issue**: The bare catch block swallows every exception type including UnauthorizedAccessException, SecurityException, and OutOfMemoryException. A permissions failure on the eval tasks directory would silently produce an empty task list, causing the optimization run to return 0 iterations with no indication that access was denied.

**Fix**: Narrow the catch to JsonException only. At minimum, log the swallowed exception.

---

## High-Severity Issues

### [HIGH] Handler is 506 lines -- exceeds 400-line project convention

**File**: RunHarnessOptimizationCommandHandler.cs (506 lines)

**Issue**: Project rules specify most files under 150 lines, 400 max with 800-line hard ceiling. Contains seven private helpers covering file I/O, manifest persistence, snapshot construction, retention policy, summary generation, and scoring.

**Fix**: Extract RunManifestPersistence (static), SnapshotWriter (static), RetentionPolicyEnforcer (needs logger). Brings handler to ~200 lines.

---

### [HIGH] EnforceRetentionPolicy deletes directories without validating they are GUID-named

**File**: RunHarnessOptimizationCommandHandler.cs:403-427

**Issue**: Directory.GetDirectories(optimizationsDir) returns ALL subdirectories. Non-optimization dirs (_backup, README folder) would be included in retention sort and potentially deleted.

**Fix**: Filter with Guid.TryParse(Path.GetFileName(d), out _) before the retention sort.

---

### [HIGH] LoadOrCreateRunManifest uses sync File.ReadAllText + bare catch

**File**: RunHarnessOptimizationCommandHandler.cs:248-270

**Issue**: Sync I/O in otherwise fully async handler. Blocks thread pool. Bare catch swallows UnauthorizedAccessException, OutOfMemoryException, etc.

**Fix**: Make async with File.ReadAllTextAsync. Catch only JsonException.

---

### [HIGH] Bare catch in LoadOrCreateRunManifest swallows all exceptions

**File**: RunHarnessOptimizationCommandHandler.cs:257-259

**Issue**: Same bare catch pattern as LoadEvalTasksAsync. Corrupt manifest should be handled; permissions error should propagate.

**Fix**: Catch only JsonException (and optionally IOException for corrupt files).

---

## Medium-Severity Issues

### [MEDIUM] IterationCount reports planned count, not actual executed count

**File**: RunHarnessOptimizationCommandHandler.cs:217

**Issue**: Math.Max(0, maxIterations - startIteration + 1) calculates planned iterations. Cancellation mid-run still reports planned count.

**Fix**: Track with executedIterations counter incremented inside the loop.

---

### [MEDIUM] WriteProposedSnapshot calls Directory.Delete(proposedDir, recursive: true) without confinement

**File**: RunHarnessOptimizationCommandHandler.cs:353-354

**Issue**: proposedDir is Path.Combine(runDir, "_proposed") -- safe now, but add Debug.Assert confinement check for defense in depth.

---

### [MEDIUM] Seed candidate silently falls back when SeedCandidateId is missing

**File**: RunHarnessOptimizationCommandHandler.cs:431-453

**Issue**: Caller explicitly requests resume from specific candidate; handler silently builds new seed if not found.

**Fix**: Throw InvalidOperationException when requested seed candidate not found.

---

### [MEDIUM] RunManifest uses inconsistent JSON property naming

**File**: RunHarnessOptimizationCommandHandler.cs:487-503

**Issue**: Four properties use camelCase but write_completed uses snake_case.

**Fix**: Change [JsonPropertyName("write_completed")] to [JsonPropertyName("writeCompleted")].

---

### [MEDIUM] ProposedChangesPath returns path to non-existent directory when bestCandidate is null

**File**: RunHarnessOptimizationCommandHandler.cs:207-208

**Issue**: When all iterations fail, WriteProposedSnapshot is a no-op but ProposedChangesPath still points to _proposed dir.

**Fix**: ProposedChangesPath = bestCandidate is not null ? proposedDir : string.Empty

---

## Low-Severity Issues

### [LOW] SeedCandidateId validator missing -- no Guid.Empty check when provided
### [LOW] UpdateRunManifest uses sync File.WriteAllText + File.Move (temp+rename pattern is correct)
### [LOW] Test file is 664 lines -- would split naturally with handler extraction
### [LOW] Retention test uses Task.Delay(10) for creation time ordering -- fragile for CI

---

## Test Coverage Assessment

### 13 tests reviewed -- all meaningful, no redundant tests

| # | Test | Concern | Verdict |
|---|------|---------|----------|
| 1 | Handle_ExecutesMaxIterations_WhenAllSucceed | Happy path | Good |
| 2 | Handle_ProposerParsingFailure_MarksFailedAndContinues | Proposer error resilience | Good |
| 3 | Handle_EvaluationException_MarksFailedAndContinues | Evaluator error resilience | Good |
| 4 | Handle_FailuresCountAsIterations_NotSkipped | All-fail scenario | Good |
| 5 | Handle_ScoreBelowThreshold_DoesNotUpdateBest | Threshold tie-breaking | Good |
| 6 | Handle_TieOnPassRate_PicksLowerTokenCostCandidate | Cost tie-breaking | Good |
| 7 | Handle_TieOnBoth_PicksEarlierIterationCandidate | Iteration tie-breaking | Good |
| 8 | Handle_ResumesFromManifest_SkipsAlreadyCompletedIterations | Resume behavior | Good |
| 9 | Handle_WritesRunManifestAfterEachIteration | Manifest persistence | Good |
| 10 | Handle_WritesProposedChangesToOutputDir_AtEnd | Output writing | Good |
| 11 | Handle_CancellationRequested_StopsCleanlyBetweenIterations | Cancellation | Good |
| 12 | Handle_RetentionPolicy_DeletesOldestRunsWhenExceedsMaxRunsToKeep | Retention | Good |
| 13 | Handle_NoEvalTasks_ReturnsZeroIterations | Empty input | Good |

### Missing test coverage (gaps):

1. **Path traversal in snapshot writing** -- No test for ../../escape as skill path
2. **Seed candidate not found** -- No test for missing SeedCandidateId
3. **OperationCanceledException from evaluator** -- Test 11 cancels between iterations, not during eval
4. **Malformed run_manifest.json** -- No test for corrupt manifest re-creation
5. **Retention with non-GUID dirs** -- No test verifying non-optimization dirs are preserved

---

## Security Checklist: Retention Policy Path Safety

| Check | Status | Notes |
|-------|--------|-------|
| Deletion confined to {TraceDirectoryRoot}/optimizations/ | PASS | Hardcoded subdirectory |
| Current run excluded from deletion | PASS | GUID string comparison filter |
| Only excess runs deleted | PASS | Correct arithmetic reserves current slot |
| maxRunsToKeep <= 0 disables retention | PASS | Early return at line 391 |
| Delete failures caught and logged | PASS | Per-directory try/catch with LogWarning |
| Symlink following escape risk | WARN | Directory.GetDirectories follows junctions. Low risk. |
| Non-GUID directories could be deleted | FAIL | No GUID validation on dir names (see HIGH) |
| TraceDirectoryRoot validated at config | WARN | No MetaHarnessConfigValidator exists |

---

## Verdict

**BLOCK** -- 2 CRITICAL issues (path traversal in snapshot writing, overly broad exception swallowing) and 4 HIGH issues must be resolved before merge.

After fixing CRITICAL + HIGH items, the code demonstrates solid architecture: proper cancellation propagation, atomic manifest writes via temp+rename, clean separation of propose/evaluate/score phases, and comprehensive test coverage of the iteration loop and tie-breaking logic.
