# Code Review Interview: Section-10 Candidate Repository

## Findings Triaged

### M-1 — SemaphoreSlim leak (AUTO-FIX applied)
**Decision:** Auto-fix — implemented `IDisposable` on `FileSystemHarnessCandidateRepository`, disposing all semaphores in `_indexLocks.Values`.

### M-2 — No JsonException handling (AUTO-FIX applied)
**Decision:** Auto-fix — wrapped deserialization in `try/catch (JsonException)` in three places: `GetBestAsync` index scan, `ListAsync` index scan, and `TryReadCandidateAsync`. Corrupt lines are silently skipped; corrupt candidate files return null (treated as not found).

### M-3 — GetAsync directory scan (LET GO)
**Decision:** Let go — POC scope, known tradeoff. The interface doesn't carry `optimizationRunId`, so cross-run search is the correct implementation. Acceptable for a harness with O(runs) < 100.

### L-1 — O(n²) List.Insert(0,...) in GetLineageAsync (AUTO-FIX applied)
**Decision:** Auto-fix — changed to `chain.Add(current)` followed by `chain.Reverse()` after the loop.

### L-2 — No test for save-twice dedup (TEST ADDED)
**Decision:** Added `SaveAsync_SaveTwiceSameCandidate_IndexUsesLastLineWins` — saves a candidate as Proposed, re-saves as Evaluated, verifies GetBestAsync returns the Evaluated version (proving last-line-wins in the index).

### L-3 — No test for non-Evaluated exclusion (TEST ADDED)
**Decision:** Added `GetBestAsync_IgnoresNonEvaluatedCandidates` — saves Proposed and Failed candidates, verifies GetBestAsync returns null.

### N-1 — Implicit JSON property names via CamelCase policy (LET GO)
**Decision:** Let go — nitpick. The JSON format is an internal persistence detail; no external consumers parse it.

## Final test count: 14/14 pass
